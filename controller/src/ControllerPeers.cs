using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MulletHopKioskController;

internal sealed class ControllerPeerPresence
{
    public int ProtocolVersion { get; set; } = 1;
    public string ControllerId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsMaster { get; set; }
    public DateTime? MasterSinceUtc { get; set; }
    public string ControllerAddress { get; set; } = string.Empty;
    public string PeerAccessKey { get; set; } = string.Empty;
    public List<string> ActivePosMachines { get; set; } = [];
}

internal sealed class DiscoveredControllerPeer
{
    public string ControllerId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool IsMaster { get; set; }
    public DateTime? MasterSinceUtc { get; set; }
    public string ControllerAddress { get; set; } = string.Empty;
    public string PeerAccessKey { get; set; } = string.Empty;
    public List<string> ActivePosMachines { get; set; } = [];
    public DateTime LastSeenUtc { get; set; }

    public DiscoveredControllerPeer Clone() => new()
    {
        ControllerId = ControllerId,
        MachineName = MachineName,
        Version = Version,
        IsMaster = IsMaster,
        MasterSinceUtc = MasterSinceUtc,
        ControllerAddress = ControllerAddress,
        PeerAccessKey = PeerAccessKey,
        ActivePosMachines = [.. ActivePosMachines],
        LastSeenUtc = LastSeenUtc
    };
}

internal class ControllerPeerRequest
{
    public string ControllerId { get; set; } = string.Empty;
}

internal sealed class ControllerPeerCommandRequest : ControllerPeerRequest
{
    public string StationId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool? Closed { get; set; }
}

internal sealed class ControllerPeerCommandResponse
{
    public bool Accepted { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal sealed record ControllerPeerCommandResult(bool Accepted, string Message);

internal sealed record ControllerConnectionPullResult(bool Success, string Message, int ConnectionCount);

internal sealed class ControllerPeerCoordinator : IDisposable
{
    public const string PresencePath = "api/controller/presence";
    public const string ReplicaPath = "api/controller/replica";
    public const string CommandPath = "api/controller/command";
    private const string TimestampHeader = "X-MulletHop-Timestamp";
    private const string SignatureHeader = "X-MulletHop-Signature";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly ControllerState _state;
    private readonly HttpClient _client;
    private readonly Dictionary<string, DiscoveredControllerPeer> _peers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _replicaGate = new(1, 1);
    private Task? _worker;
    private bool _disposed;

    public event Action? PeersChanged;

    public ControllerPeerCoordinator(ControllerState state)
    {
        _state = state;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(450),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(1_500) };
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _worker ??= Task.Run(() => RunAsync(_stopping.Token));
    }

    public IReadOnlyList<DiscoveredControllerPeer> Snapshot()
    {
        lock (_gate)
        {
            RemoveStalePeersLocked();
            return _peers.Values
                .Select(peer => peer.Clone())
                .OrderByDescending(peer => peer.IsMaster)
                .ThenBy(peer => peer.MachineName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public bool IsKnownController(string controllerId)
    {
        lock (_gate)
        {
            RemoveStalePeersLocked();
            return _peers.ContainsKey(controllerId);
        }
    }

    public async Task<ControllerPeerCommandResult> QueueCommandOnMasterAsync(
        string stationId,
        string type,
        bool? closed,
        CancellationToken cancellationToken = default)
    {
        var master = Snapshot().FirstOrDefault(peer =>
            peer.IsMaster &&
            !string.IsNullOrWhiteSpace(peer.ControllerAddress) &&
            !string.IsNullOrWhiteSpace(peer.PeerAccessKey));
        if (master is null)
        {
            return new ControllerPeerCommandResult(
                false,
                "No active master controller is available.");
        }

        try
        {
            var body = JsonSerializer.Serialize(new ControllerPeerCommandRequest
            {
                ControllerId = _state.ControllerId,
                StationId = stationId,
                Type = type,
                Closed = closed
            }, JsonOptions);
            var endpoint = new Uri(new Uri(master.ControllerAddress), CommandPath);
            using var request = CreateSignedRequest(endpoint, master.PeerAccessKey, body);
            using var response = await _client.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ControllerPeerCommandResult(
                    false,
                    string.IsNullOrWhiteSpace(responseBody)
                        ? $"The master controller returned HTTP {(int)response.StatusCode}."
                        : responseBody.Trim());
            }

            VerifySignedResponse(response, master.PeerAccessKey, responseBody);
            var result = JsonSerializer.Deserialize<ControllerPeerCommandResponse>(responseBody, JsonOptions);
            return result is null
                ? new ControllerPeerCommandResult(false, "The master controller returned an empty response.")
                : new ControllerPeerCommandResult(result.Accepted, result.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                   JsonException or InvalidDataException or IOException)
        {
            ControllerLog.Write("Master command relay error: " + ex.Message);
            return new ControllerPeerCommandResult(
                false,
                "The command could not be sent to the master controller.");
        }
    }

    public async Task<ControllerConnectionPullResult> PullMasterConnectionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_state.IsMaster)
        {
            return new ControllerConnectionPullResult(
                false,
                "This PC is the master controller; use its locally stored connection catalog.",
                0);
        }

        await ScanNowAsync();
        var master = Snapshot().FirstOrDefault(peer => peer.IsMaster);
        if (master is null)
        {
            return new ControllerConnectionPullResult(
                false,
                "No active master controller was detected on the local network.",
                0);
        }

        return await SynchronizeFromMasterAsync(
            new ControllerPeerPresence
            {
                ControllerId = master.ControllerId,
                MachineName = master.MachineName,
                Version = master.Version,
                IsMaster = master.IsMaster,
                MasterSinceUtc = master.MasterSinceUtc,
                ControllerAddress = master.ControllerAddress,
                PeerAccessKey = master.PeerAccessKey,
                ActivePosMachines = [.. master.ActivePosMachines]
            },
            cancellationToken,
            waitForCurrentSync: true);
    }

    public ControllerPeerPresence ProcessPresence(
        ControllerPeerPresence presence,
        IPAddress remoteAddress,
        IPAddress localAddress)
    {
        ValidatePresence(presence);
        if (!string.Equals(presence.ControllerId, _state.ControllerId, StringComparison.Ordinal))
        {
            presence.ControllerAddress = BuildControllerAddress(remoteAddress);
            UpdatePeer(presence);
        }
        return CreateLocalPresence(BuildControllerAddress(localAddress));
    }

    public async Task ScanNowAsync()
    {
        if (_disposed)
            return;
        await _scanGate.WaitAsync();
        try
        {
            await ProbeControllersAsync(
                FindSubnetControllerAddresses(),
                _stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var nextFullScanUtc = DateTime.MinValue;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var known = Snapshot()
                    .Select(peer => peer.ControllerAddress)
                    .Where(address => !string.IsNullOrWhiteSpace(address))
                    .ToArray();
                if (known.Length > 0)
                    await ProbeControllersAsync(known, cancellationToken);

                if (DateTime.UtcNow >= nextFullScanUtc &&
                    await _scanGate.WaitAsync(0, cancellationToken))
                {
                    try
                    {
                        await ProbeControllersAsync(
                            FindSubnetControllerAddresses()
                                .Except(known, StringComparer.OrdinalIgnoreCase),
                            cancellationToken);
                        nextFullScanUtc = DateTime.UtcNow.AddSeconds(15);
                    }
                    finally
                    {
                        _scanGate.Release();
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller peer discovery stopped unexpectedly: " + ex.Message);
        }
    }

    private async Task ProbeControllersAsync(
        IEnumerable<string> addresses,
        CancellationToken cancellationToken)
    {
        var targets = addresses
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(1_024)
            .ToArray();
        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 48
            },
            async (address, token) => await AnnounceAsync(address, token));
    }

    private async ValueTask AnnounceAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryNormalizeControllerAddress(address, out var normalized))
                return;
            var body = JsonSerializer.Serialize(CreateLocalPresence(string.Empty), JsonOptions);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var endpoint = new Uri(new Uri(normalized), PresencePath);
            using var response = await _client.PostAsync(endpoint, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return;
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var presence = JsonSerializer.Deserialize<ControllerPeerPresence>(responseBody, JsonOptions);
            if (presence is null)
                return;
            ValidatePresence(presence);
            if (string.Equals(presence.ControllerId, _state.ControllerId, StringComparison.Ordinal))
                return;
            if (!ControllerResponseMatchesEndpoint(normalized, presence.ControllerAddress))
                return;
            UpdatePeer(presence);
            if (presence.IsMaster && !_state.IsMaster)
                await SynchronizeFromMasterAsync(presence, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                   JsonException or InvalidDataException or IOException or
                                   ObjectDisposedException)
        {
            // Most addresses are not controller computers. Failed probes are expected.
        }
    }

    private ControllerPeerPresence CreateLocalPresence(string address) => new()
    {
        ControllerId = _state.ControllerId,
        MachineName = Environment.MachineName,
        Version = ControllerUpdater.CurrentVersion,
        IsMaster = _state.IsMaster,
        MasterSinceUtc = _state.MasterSinceUtc,
        ControllerAddress = address,
        PeerAccessKey = _state.IsMaster ? _state.PeerAccessKey : string.Empty,
        ActivePosMachines = _state.ActivePosMachineNames().ToList()
    };

    private void UpdatePeer(ControllerPeerPresence presence)
    {
        var changed = false;
        lock (_gate)
        {
            RemoveStalePeersLocked();
            if (!_peers.TryGetValue(presence.ControllerId, out var peer))
            {
                peer = new DiscoveredControllerPeer { ControllerId = presence.ControllerId };
                _peers[presence.ControllerId] = peer;
                changed = true;
                ControllerLog.Write(
                    $"Discovered Kiosk Controller {presence.MachineName} at {presence.ControllerAddress}.");
            }

            if (!string.Equals(peer.MachineName, presence.MachineName, StringComparison.Ordinal) ||
                !string.Equals(peer.Version, presence.Version, StringComparison.Ordinal) ||
                peer.IsMaster != presence.IsMaster ||
                peer.MasterSinceUtc != presence.MasterSinceUtc ||
                !string.Equals(peer.PeerAccessKey, presence.PeerAccessKey, StringComparison.Ordinal) ||
                !peer.ActivePosMachines.SequenceEqual(
                    presence.ActivePosMachines,
                    StringComparer.OrdinalIgnoreCase) ||
                !string.Equals(
                    peer.ControllerAddress,
                    presence.ControllerAddress,
                    StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
            }
            peer.MachineName = presence.MachineName.Trim();
            peer.Version = presence.Version.Trim();
            peer.IsMaster = presence.IsMaster;
            peer.MasterSinceUtc = presence.MasterSinceUtc;
            peer.ControllerAddress = presence.ControllerAddress;
            peer.PeerAccessKey = presence.PeerAccessKey;
            peer.ActivePosMachines = [.. presence.ActivePosMachines];
            peer.LastSeenUtc = DateTime.UtcNow;
        }

        if (presence.IsMaster && _state.IsMaster && !LocalMasterWins(presence))
        {
            _state.SetMaster(
                false,
                $"resolved a duplicate master conflict in favor of {presence.MachineName}");
            changed = true;
        }
        if (changed)
            RaisePeersChanged();
    }

    private bool LocalMasterWins(ControllerPeerPresence peer)
    {
        var localSince = _state.MasterSinceUtc ?? DateTime.MaxValue;
        var peerSince = peer.MasterSinceUtc ?? DateTime.MaxValue;
        var timeComparison = localSince.CompareTo(peerSince);
        return timeComparison < 0 ||
               (timeComparison == 0 && string.CompareOrdinal(_state.ControllerId, peer.ControllerId) < 0);
    }

    private void RemoveStalePeersLocked()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-25);
        foreach (var id in _peers
                     .Where(pair => pair.Value.LastSeenUtc < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _peers.Remove(id);
        }
    }

    private void RaisePeersChanged()
    {
        try { PeersChanged?.Invoke(); }
        catch (Exception ex) { ControllerLog.Write("Controller peer status update error: " + ex.Message); }
    }

    private static void ValidatePresence(ControllerPeerPresence presence)
    {
        if (presence.ProtocolVersion != 1 ||
            !Guid.TryParseExact(presence.ControllerId, "N", out _) ||
            string.IsNullOrWhiteSpace(presence.MachineName) ||
            presence.MachineName.Length > 200 ||
            presence.Version?.Length > 100 ||
            presence.ControllerAddress?.Length > 300 ||
            presence.PeerAccessKey?.Length > 200 ||
            (presence.IsMaster != presence.MasterSinceUtc.HasValue))
        {
            throw new InvalidDataException("The controller presence announcement is invalid.");
        }
        presence.MachineName = presence.MachineName.Trim();
        presence.Version = string.IsNullOrWhiteSpace(presence.Version)
            ? "Unknown"
            : presence.Version.Trim();
        presence.PeerAccessKey = presence.PeerAccessKey?.Trim() ?? string.Empty;
        presence.ActivePosMachines = (presence.ActivePosMachines ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().Length <= 80 ? name.Trim() : name.Trim()[..80])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        if (!string.IsNullOrWhiteSpace(presence.ControllerAddress))
        {
            if (!TryNormalizeControllerAddress(
                    presence.ControllerAddress, out var normalizedAddress))
            {
                throw new InvalidDataException("The controller presence address is invalid.");
            }
            presence.ControllerAddress = normalizedAddress;
        }
    }

    private async Task<ControllerConnectionPullResult> SynchronizeFromMasterAsync(
        ControllerPeerPresence master,
        CancellationToken cancellationToken,
        bool waitForCurrentSync = false)
    {
        if (string.IsNullOrWhiteSpace(master.PeerAccessKey))
        {
            return new ControllerConnectionPullResult(
                false,
                "The detected master controller does not support stored-connection transfers.",
                0);
        }

        var entered = waitForCurrentSync
            ? await _replicaGate.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
            : await _replicaGate.WaitAsync(0, cancellationToken);
        if (!entered)
        {
            return new ControllerConnectionPullResult(
                false,
                "A master connection transfer is already in progress.",
                0);
        }
        try
        {
            var body = JsonSerializer.Serialize(new ControllerPeerRequest
            {
                ControllerId = _state.ControllerId
            }, JsonOptions);
            var endpoint = new Uri(new Uri(master.ControllerAddress), ReplicaPath);
            using var request = CreateSignedRequest(endpoint, master.PeerAccessKey, body);
            using var response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ControllerConnectionPullResult(
                    false,
                    $"The master controller returned HTTP {(int)response.StatusCode}.",
                    0);
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            VerifySignedResponse(response, master.PeerAccessKey, responseBody);
            var replica = JsonSerializer.Deserialize<ControllerReplicaSnapshot>(responseBody, JsonOptions);
            if (replica is null ||
                !string.Equals(
                    replica.MasterControllerId,
                    master.ControllerId,
                    StringComparison.Ordinal) ||
                replica.Kiosks.Count > 100)
            {
                throw new InvalidDataException("The master controller replica is invalid.");
            }

            if (_state.ApplyMasterReplica(replica))
            {
                RaisePeersChanged();
                return new ControllerConnectionPullResult(
                    true,
                    $"Pulled and saved {replica.Kiosks.Count} kiosk connection(s) from " +
                    $"master controller {master.MachineName}.",
                    replica.Kiosks.Count);
            }

            return new ControllerConnectionPullResult(
                true,
                $"The {replica.Kiosks.Count} stored kiosk connection(s) from " +
                $"{master.MachineName} are already current on this PC.",
                replica.Kiosks.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ControllerConnectionPullResult(false, "The connection transfer was canceled.", 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                   JsonException or InvalidDataException or IOException)
        {
            ControllerLog.Write("Master controller synchronization error: " + ex.Message);
            return new ControllerConnectionPullResult(
                false,
                "The stored connections could not be pulled from the master controller.",
                0);
        }
        finally
        {
            _replicaGate.Release();
        }
    }

    private static HttpRequestMessage CreateSignedRequest(Uri uri, string accessKey, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp);
        request.Headers.TryAddWithoutValidation(
            SignatureHeader,
            ControllerSecurity.Sign(accessKey, timestamp, body));
        return request;
    }

    private static void VerifySignedResponse(
        HttpResponseMessage response,
        string accessKey,
        string body)
    {
        if (!response.Headers.TryGetValues(TimestampHeader, out var timestamps) ||
            !response.Headers.TryGetValues(SignatureHeader, out var signatures))
        {
            throw new InvalidDataException("The master controller response was not signed.");
        }

        var timestamp = timestamps.FirstOrDefault() ?? string.Empty;
        var signature = signatures.FirstOrDefault() ?? string.Empty;
        if (!long.TryParse(timestamp, out var unixTime) ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixTime) > 300 ||
            !ControllerSecurity.FixedTimeEquals(
                ControllerSecurity.Sign(accessKey, timestamp, body),
                signature))
        {
            throw new InvalidDataException("The master controller response signature was invalid.");
        }
    }

    private static bool TryNormalizeControllerAddress(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            uri.Port != ControllerServer.Port ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(
                uri.AbsolutePath.TrimEnd('/'),
                ControllerServer.BasePath.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        normalized = new UriBuilder(uri)
        {
            Path = ControllerServer.BasePath,
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri.AbsoluteUri;
        return true;
    }

    private static bool ControllerResponseMatchesEndpoint(string requestAddress, string responseAddress)
    {
        if (!TryNormalizeControllerAddress(requestAddress, out var requested) ||
            !TryNormalizeControllerAddress(responseAddress, out var response) ||
            !Uri.TryCreate(requested, UriKind.Absolute, out var requestUri) ||
            !Uri.TryCreate(response, UriKind.Absolute, out var responseUri))
        {
            return false;
        }
        return IPAddress.TryParse(requestUri.Host, out var requestedIp) &&
               IPAddress.TryParse(responseUri.Host, out var responseIp)
            ? requestedIp.Equals(responseIp)
            : string.Equals(requestUri.Host, responseUri.Host, StringComparison.OrdinalIgnoreCase);
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
                    var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                                ((uint)bytes[2] << 8) | bytes[3];
                    var prefixLength = Math.Clamp(unicast.PrefixLength, 24, 32);
                    var mask = uint.MaxValue << (32 - prefixLength);
                    var network = value & mask;
                    var broadcast = network | ~mask;
                    var first = prefixLength >= 31 ? network : network + 1;
                    var last = prefixLength >= 31 ? broadcast : broadcast - 1;
                    for (var candidate = first; candidate <= last; candidate++)
                    {
                        addresses.Add(BuildControllerAddress(new IPAddress([
                            (byte)(candidate >> 24),
                            (byte)(candidate >> 16),
                            (byte)(candidate >> 8),
                            (byte)candidate])));
                        if (candidate == uint.MaxValue)
                            break;
                    }
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            ControllerLog.Write("Could not enumerate local networks for controller discovery: " + ex.Message);
        }
        return addresses;
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 127 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    private static string BuildControllerAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var host = address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        return $"http://{host}:{ControllerServer.Port}{ControllerServer.BasePath}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stopping.Cancel();
        _client.Dispose();
        _stopping.Dispose();
    }
}

internal sealed class ControllerStatusLight : Control
{
    private readonly Color _onColor;
    private readonly Color _offColor;
    private bool _active;

    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
                return;
            _active = value;
            Invalidate();
        }
    }

    public ControllerStatusLight(Color onColor, Color offColor)
    {
        _onColor = onColor;
        _offColor = offColor;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        MinimumSize = new Size(30, 30);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var size = Math.Max(10, Math.Min(ClientSize.Width, ClientSize.Height) - 8);
        var circle = new Rectangle(
            (ClientSize.Width - size) / 2,
            (ClientSize.Height - size) / 2,
            size,
            size);
        if (Active)
        {
            using var glow = new SolidBrush(Color.FromArgb(55, _onColor));
            e.Graphics.FillEllipse(glow, Rectangle.Inflate(circle, 4, 4));
        }
        using var fill = new SolidBrush(Active ? _onColor : _offColor);
        using var outline = new Pen(
            Active ? ControlPaint.Light(_onColor) : ControlPaint.Light(_offColor), 2.2f);
        e.Graphics.FillEllipse(fill, circle);
        e.Graphics.DrawEllipse(outline, circle);
        var highlight = new Rectangle(
            circle.X + circle.Width / 5,
            circle.Y + circle.Height / 6,
            Math.Max(3, circle.Width / 4),
            Math.Max(3, circle.Height / 5));
        using var shine = new SolidBrush(Color.FromArgb(Active ? 95 : 35, Color.White));
        e.Graphics.FillEllipse(shine, highlight);
    }
}
