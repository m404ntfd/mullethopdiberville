using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MulletHop.KioskDiscovery;

namespace MulletHopWaiverKiosk;

internal sealed class KioskDiscoveryClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly KioskSettings _settings;
    private readonly Func<KioskPairingPayload, Task<bool>> _confirmPairing;
    private readonly Action _pairingApplied;
    private readonly ECDiffieHellman _kioskKey = KioskDiscoveryProtocol.CreateKioskKey();
    private readonly HttpClient _httpClient;
    private readonly HashSet<string> _knownControllers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _handledRequests = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loopTask;
    private KioskPairingResult? _pendingResult;
    private int _controllerRecoveryInProgress;
    private bool _disposed;

    public KioskDiscoveryClient(
        KioskSettings settings,
        Func<KioskPairingPayload, Task<bool>> confirmPairing,
        Action pairingApplied)
    {
        _settings = settings;
        _confirmPairing = confirmPairing;
        _pairingApplied = pairingApplied;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(450),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(1_500)
        };

        if (TryNormalizeControllerAddress(_settings.RemoteControllerUrl, out var configured))
            _knownControllers.Add(configured);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loopTask ??= Task.Run(() => RunAsync(_stopping.Token));
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var nextFullScanUtc = DateTime.MinValue;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_settings.RemoteManagementEnabled)
                {
                    nextFullScanUtc = DateTime.MinValue;
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    continue;
                }

                string[] known;
                KioskPairingResult? pendingResult;
                lock (_gate)
                {
                    known = _knownControllers.ToArray();
                    pendingResult = _pendingResult;
                }

                if (known.Length > 0)
                    await ProbeControllersAsync(known, cancellationToken);

                if (DateTime.UtcNow >= nextFullScanUtc)
                {
                    var subnetControllers = FindSubnetControllerAddresses()
                        .Except(known, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    await ProbeControllersAsync(subnetControllers, cancellationToken);
                    nextFullScanUtc = DateTime.UtcNow.AddSeconds(60);
                }

                var delay = pendingResult is not null
                    ? TimeSpan.FromSeconds(1)
                    : known.Length > 0
                        ? TimeSpan.FromSeconds(3)
                        : TimeSpan.FromSeconds(5);
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            KioskLog.Write("Kiosk discovery stopped unexpectedly: " + ex.Message);
        }
    }

    private async Task ProbeControllersAsync(
        IEnumerable<string> controllerAddresses,
        CancellationToken cancellationToken)
    {
        var addresses = controllerAddresses
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(1_024)
            .ToArray();
        if (addresses.Length == 0)
            return;

        await Parallel.ForEachAsync(
            addresses,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 48
            },
            async (address, token) => await AnnounceToControllerAsync(address, token));
    }

    private async ValueTask AnnounceToControllerAsync(
        string controllerAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryNormalizeControllerAddress(controllerAddress, out var normalizedAddress))
                return;

            KioskPairingResult? result;
            lock (_gate)
            {
                result = _pendingResult is null
                    ? null
                    : new KioskPairingResult
                    {
                        RequestId = _pendingResult.RequestId,
                        Accepted = _pendingResult.Accepted,
                        Message = _pendingResult.Message
                    };
            }

            var announcement = new KioskDiscoveryAnnouncement
            {
                StationId = _settings.StationId,
                StationName = _settings.StationName,
                MachineName = Environment.MachineName,
                Version = KioskUpdater.CurrentVersion,
                KioskPublicKey = KioskDiscoveryProtocol.ExportPublicKey(_kioskKey),
                IsManaged = _settings.RemoteManagementEnabled &&
                            RemoteManagementProtocol.IsConfigurationValid(
                                _settings.RemoteControllerUrl,
                                _settings.RemotePairingKey,
                                out _),
                CurrentController = _settings.RemoteControllerUrl,
                PairingResult = result
            };
            var body = JsonSerializer.Serialize(announcement, JsonOptions);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var endpoint = new Uri(new Uri(normalizedAddress), KioskDiscoveryProtocol.AnnouncementPath);
            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return;

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var discoveryResponse = JsonSerializer.Deserialize<KioskDiscoveryResponse>(
                responseBody, JsonOptions);
            if (discoveryResponse is null ||
                discoveryResponse.ProtocolVersion != KioskDiscoveryProtocol.Version ||
                !ControllerResponseMatchesEndpoint(normalizedAddress, discoveryResponse.ControllerAddress))
            {
                return;
            }

            if (TryNormalizeControllerAddress(discoveryResponse.ControllerAddress, out var actualAddress))
            {
                lock (_gate)
                {
                    _knownControllers.Add(actualAddress);
                    if (_pendingResult is not null &&
                        string.Equals(
                            discoveryResponse.AcknowledgedPairingRequestId,
                            _pendingResult.RequestId,
                            StringComparison.Ordinal))
                    {
                        _pendingResult = null;
                    }
                }

                await TryRestoreSavedControllerAddressAsync(actualAddress);
            }

            if (discoveryResponse.PairingOffer is not null)
            {
                await HandlePairingOfferAsync(
                    discoveryResponse.PairingOffer,
                    discoveryResponse.ControllerAddress);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                   JsonException or IOException or ObjectDisposedException)
        {
            // Most addresses on a subnet are not controllers. Failed probes are expected.
        }
    }

    private async Task TryRestoreSavedControllerAddressAsync(string discoveredAddress)
    {
        if (!_settings.RemoteManagementEnabled ||
            string.IsNullOrWhiteSpace(_settings.RemotePairingKey) ||
            _settings.RemotePairingKey.Trim().Length < 16 ||
            Interlocked.CompareExchange(ref _controllerRecoveryInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (TryNormalizeControllerAddress(_settings.RemoteControllerUrl, out var savedAddress) &&
                string.Equals(savedAddress, discoveredAddress, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var result = await RemoteManagementProtocol.TestAsync(
                discoveredAddress,
                _settings.RemotePairingKey);
            if (!result.Success)
                return;

            var previousAddress = _settings.RemoteControllerUrl;
            try
            {
                _settings.RemoteControllerUrl = discoveredAddress;
                _settings.Save();
            }
            catch
            {
                _settings.RemoteControllerUrl = previousAddress;
                throw;
            }

            KioskLog.Write(
                $"Securely reconnected to the saved kiosk controller at {discoveredAddress}.");
            _pairingApplied();
        }
        catch (Exception ex)
        {
            KioskLog.Write("Could not restore the saved controller connection: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _controllerRecoveryInProgress, 0);
        }
    }

    private async Task HandlePairingOfferAsync(KioskPairingOffer offer, string respondingAddress)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(offer.RequestId) ||
                !_handledRequests.Add(offer.RequestId))
            {
                return;
            }

            if (_handledRequests.Count > 100)
                _handledRequests.Remove(_handledRequests.First());
        }

        KioskPairingPayload payload;
        try
        {
            payload = KioskDiscoveryProtocol.DecryptPairingOffer(_kioskKey, offer);
            ValidatePairingPayload(payload, respondingAddress);
        }
        catch (Exception ex)
        {
            KioskLog.Write("Rejected an invalid discovery pairing request: " + ex.Message);
            SetPendingResult(offer.RequestId, false, "The kiosk rejected an invalid pairing request.");
            return;
        }

        if (!_settings.RemoteManagementEnabled)
        {
            SetPendingResult(
                payload.RequestId,
                false,
                "Remote control and network discovery were turned off on the kiosk.");
            return;
        }

        var accepted = false;
        try
        {
            accepted = await _confirmPairing(payload);
            if (accepted)
            {
                if (payload.ExpiresUtc <= DateTime.UtcNow)
                {
                    SetPendingResult(
                        payload.RequestId,
                        false,
                        "The pairing request expired before it was approved.");
                    KioskLog.Write(
                        $"Pairing request from {payload.ControllerName} expired before approval.");
                    return;
                }

                var previousUrl = _settings.RemoteControllerUrl;
                var previousKey = _settings.RemotePairingKey;
                var previousEnabled = _settings.RemoteManagementEnabled;
                try
                {
                    _settings.RemoteControllerUrl = payload.ControllerAddress;
                    _settings.RemotePairingKey = payload.PairingKey;
                    _settings.RemoteManagementEnabled = true;
                    _settings.Save();
                }
                catch
                {
                    _settings.RemoteControllerUrl = previousUrl;
                    _settings.RemotePairingKey = previousKey;
                    _settings.RemoteManagementEnabled = previousEnabled;
                    throw;
                }

                lock (_gate)
                    _knownControllers.Add(payload.ControllerAddress);
                SetPendingResult(
                    payload.RequestId,
                    true,
                    $"Connection approved and saved on {Environment.MachineName}.");
                KioskLog.Write(
                    $"Approved and saved kiosk controller connection to {payload.ControllerAddress}.");
                _pairingApplied();
                return;
            }
        }
        catch (Exception ex)
        {
            KioskLog.Write("Discovery pairing could not be saved: " + ex.Message);
            SetPendingResult(payload.RequestId, false, "The kiosk could not save the connection.");
            return;
        }

        SetPendingResult(payload.RequestId, false, "The connection was declined at the kiosk.");
        KioskLog.Write($"Declined pairing request from {payload.ControllerName}.");
    }

    private void SetPendingResult(string requestId, bool accepted, string message)
    {
        lock (_gate)
        {
            _pendingResult = new KioskPairingResult
            {
                RequestId = requestId,
                Accepted = accepted,
                Message = message
            };
        }
    }

    private static void ValidatePairingPayload(
        KioskPairingPayload payload,
        string respondingAddress)
    {
        if (payload.ExpiresUtc <= DateTime.UtcNow ||
            payload.ExpiresUtc > DateTime.UtcNow.AddMinutes(3))
        {
            throw new InvalidDataException("The pairing request expired.");
        }
        if (string.IsNullOrWhiteSpace(payload.ControllerName) ||
            payload.ControllerName.Length > 200 ||
            payload.PairingKey.Length is < 16 or > 1_000 ||
            !ControllerResponseMatchesEndpoint(respondingAddress, payload.ControllerAddress))
        {
            throw new InvalidDataException("The pairing request did not match the controller.");
        }
    }

    private static bool ControllerResponseMatchesEndpoint(
        string requestedAddress,
        string responseAddress)
    {
        if (!TryNormalizeControllerAddress(requestedAddress, out var requested) ||
            !TryNormalizeControllerAddress(responseAddress, out var response) ||
            !Uri.TryCreate(requested, UriKind.Absolute, out var requestedUri) ||
            !Uri.TryCreate(response, UriKind.Absolute, out var responseUri))
        {
            return false;
        }

        if (IPAddress.TryParse(requestedUri.Host, out var requestedIp) &&
            IPAddress.TryParse(responseUri.Host, out var responseIp))
        {
            return requestedIp.Equals(responseIp);
        }

        return string.Equals(requestedUri.Host, responseUri.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeControllerAddress(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != KioskDiscoveryProtocol.ControllerPort ||
            !string.Equals(
                uri.AbsolutePath.TrimEnd('/'),
                KioskDiscoveryProtocol.ControllerBasePath.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Path = KioskDiscoveryProtocol.ControllerBasePath,
            Query = string.Empty,
            Fragment = string.Empty
        };
        normalized = builder.Uri.AbsoluteUri;
        return true;
    }

    private static IEnumerable<string> FindSubnetControllerAddresses()
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BuildControllerAddress(IPAddress.Loopback)
        };
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                        NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork ||
                        !IsPrivateAddress(address))
                    {
                        continue;
                    }

                    var bytes = address.GetAddressBytes();
                    var value = ((uint)bytes[0] << 24) |
                                ((uint)bytes[1] << 16) |
                                ((uint)bytes[2] << 8) |
                                bytes[3];
                    var prefixLength = Math.Clamp(unicast.PrefixLength, 24, 32);
                    var mask = prefixLength == 0 ? 0U : uint.MaxValue << (32 - prefixLength);
                    var network = value & mask;
                    var broadcast = network | ~mask;
                    var first = prefixLength >= 31 ? network : network + 1;
                    var last = prefixLength >= 31 ? broadcast : broadcast - 1;
                    for (var candidate = first; candidate <= last; candidate++)
                    {
                        var candidateAddress = new IPAddress(
                        [
                            (byte)(candidate >> 24),
                            (byte)(candidate >> 16),
                            (byte)(candidate >> 8),
                            (byte)candidate
                        ]);
                        addresses.Add(BuildControllerAddress(candidateAddress));
                        if (candidate == uint.MaxValue)
                            break;
                    }
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            KioskLog.Write("Could not enumerate local networks for kiosk discovery: " + ex.Message);
        }

        return addresses;
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 127 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    private static string BuildControllerAddress(IPAddress address) =>
        $"http://{address}:{KioskDiscoveryProtocol.ControllerPort}" +
        KioskDiscoveryProtocol.ControllerBasePath;

    public void Dispose()
    {
        Task? loopTask;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stopping.Cancel();
            loopTask = _loopTask;
        }

        try { loopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _httpClient.Dispose();
        _kioskKey.Dispose();
        _stopping.Dispose();
    }
}
