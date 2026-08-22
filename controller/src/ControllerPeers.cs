using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MulletHop.LocalNetworking;
using MulletHop.Shared;

namespace MulletHopKioskController;

internal sealed class ControllerPosPresence
{
    public string MachineName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }

    public ControllerPosPresence Clone() => new()
    {
        MachineName = MachineName,
        IpAddress = IpAddress,
        Version = Version,
        LastSeenUtc = LastSeenUtc
    };
}

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
    public string PairingKeyFingerprint { get; set; } = string.Empty;
    public long ServerUnixTimeSeconds { get; set; }
    public List<string> ActivePosMachines { get; set; } = [];
    public List<ControllerPosPresence> PosMachines { get; set; } = [];
    public bool InstallControllerUpdate { get; set; }
    public List<string> PosUpdateRequests { get; set; } = [];
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
    public string PairingKeyFingerprint { get; set; } = string.Empty;
    public long ClockOffsetSeconds { get; set; }
    public List<string> ActivePosMachines { get; set; } = [];
    public List<ControllerPosPresence> PosMachines { get; set; } = [];
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
        PairingKeyFingerprint = PairingKeyFingerprint,
        ClockOffsetSeconds = ClockOffsetSeconds,
        ActivePosMachines = [.. ActivePosMachines],
        PosMachines = PosMachines.Select(machine => machine.Clone()).ToList(),
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

internal sealed class ControllerSoftwareUpdateRequest : ControllerPeerRequest
{
    public string TargetType { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
}

internal sealed class ControllerWristbandSettingsRequest : ControllerPeerRequest
{
    public WristbandSettingsPackage Settings { get; set; } = new();
}

internal sealed class ControllerPeerCommandResponse
{
    public bool Accepted { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal sealed record ControllerPeerCommandResult(bool Accepted, string Message);

internal sealed record ControllerConnectionPullResult(bool Success, string Message, int ConnectionCount);

internal sealed record ControllerMasterConnectionResult(bool Success, string Message);

internal static class ControllerMasterElection
{
    public static MasterPriorityEntry? SelectWinner(
        IEnumerable<MasterPriorityEntry> priority,
        IEnumerable<string> reachableControllerIds)
    {
        var reachable = reachableControllerIds.ToHashSet(StringComparer.Ordinal);
        return priority.FirstOrDefault(entry => reachable.Contains(entry.ControllerId));
    }

    public static bool SmokeTest()
    {
        const string first = "11111111111111111111111111111111";
        const string second = "22222222222222222222222222222222";
        var priority = new[]
        {
            new MasterPriorityEntry { ControllerId = first },
            new MasterPriorityEntry { ControllerId = second }
        };
        return WristbandSettingsPackage.SmokeTest() &&
               ControllerBusinessDayHours.CalculateLastJumpTimeSold(TimeSpan.FromHours(22)) ==
                   TimeSpan.FromHours(21) &&
               ControllerBusinessDayHours.CalculateLastJumpTimeSold(TimeSpan.Zero) ==
                   TimeSpan.FromHours(23) &&
               SelectWinner(priority, new[] { first, second })?.ControllerId == first &&
               SelectWinner(priority, new[] { second })?.ControllerId == second &&
               SelectWinner(priority, Array.Empty<string>()) is null;
    }
}

internal sealed class ControllerPeerCoordinator : IDisposable
{
    public const string PresencePath = "api/controller/presence";
    public const string ReplicaPath = "api/controller/replica";
    public const string CommandPath = "api/controller/command";
    public const string SoftwareUpdatePath = "api/controller/software-update";
    public const string WristbandSettingsPath = "api/controller/wristband-settings";
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
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private DateTime? _noMasterSinceUtc;
    private Task? _worker;
    private bool _disposed;

    public event Action? PeersChanged;
    public event Action? SoftwareUpdateRequested;

    public void RequestLocalSoftwareUpdate() => RaiseSoftwareUpdateRequested();

    public ControllerPeerCoordinator(ControllerState state)
    {
        _state = state;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(1_500),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
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

    public bool IsKnownPosMachine(string machineName) =>
        _state.ActivePosMachineNames().Contains(machineName, StringComparer.OrdinalIgnoreCase) ||
        Snapshot().Any(peer =>
            peer.ActivePosMachines.Contains(machineName, StringComparer.OrdinalIgnoreCase));

    private void RemovePeer(string controllerId)
    {
        bool removed;
        lock (_gate)
            removed = _peers.Remove(controllerId);
        if (removed)
            RaisePeersChanged();
    }

    public async Task<ControllerPeerCommandResult> QueueCommandOnMasterAsync(
        string stationId,
        string type,
        bool? closed,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var master = await ResolveMasterAsync(cancellationToken);
            if (master is null)
            {
                return new ControllerPeerCommandResult(
                    false,
                    "No active or saved master controller is available.");
            }

            try
            {
                return await SendMasterCommandAsync(
                    master,
                    stationId,
                    type,
                    closed,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ControllerPeerCommandResult(false, "The controller command was canceled.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                       JsonException or InvalidDataException or IOException)
            {
                ControllerLog.Write("Master command relay error: " + ex.Message);
                RemovePeer(master.ControllerId);
                if (attempt == 0)
                    continue;
            }
        }

        return new ControllerPeerCommandResult(
            false,
            "The command could not be sent to the saved master controller.");
    }

    private async Task<ControllerPeerCommandResult> SendMasterCommandAsync(
        DiscoveredControllerPeer master,
        string stationId,
        string type,
        bool? closed,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new ControllerPeerCommandRequest
        {
            ControllerId = _state.ControllerId,
            StationId = stationId,
            Type = type,
            Closed = closed
        }, JsonOptions);
        var endpoint = new Uri(new Uri(master.ControllerAddress), CommandPath);
        using var request = CreateSignedRequest(
            endpoint, master.PeerAccessKey, body, master.ClockOffsetSeconds);
        using var response = await _client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(responseBody)
                    ? $"The master controller returned HTTP {(int)response.StatusCode}."
                    : responseBody.Trim());
        }

        VerifySignedResponse(
            response, master.PeerAccessKey, responseBody, master.ClockOffsetSeconds);
        var result = JsonSerializer.Deserialize<ControllerPeerCommandResponse>(responseBody, JsonOptions);
        return result is null
            ? new ControllerPeerCommandResult(false, "The master controller returned an empty response.")
            : new ControllerPeerCommandResult(result.Accepted, result.Message);
    }

    public async Task<WristbandSettingsPackage> SaveWristbandSettingsOnMasterAsync(
        WristbandSettingsPackage settings,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var master = await ResolveMasterAsync(cancellationToken);
            if (master is null)
            {
                throw new InvalidOperationException(
                    "No active or saved master Systems Controller is available.");
            }

            try
            {
                var body = JsonSerializer.Serialize(new ControllerWristbandSettingsRequest
                {
                    ControllerId = _state.ControllerId,
                    Settings = settings
                }, JsonOptions);
                var endpoint = new Uri(new Uri(master.ControllerAddress), WristbandSettingsPath);
                using var request = CreateSignedRequest(
                    endpoint,
                    master.PeerAccessKey,
                    body,
                    master.ClockOffsetSeconds);
                using var response = await _client.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidDataException(
                        string.IsNullOrWhiteSpace(responseBody)
                            ? $"The master Systems Controller returned HTTP {(int)response.StatusCode}."
                            : responseBody.Trim());
                }

                VerifySignedResponse(
                    response,
                    master.PeerAccessKey,
                    responseBody,
                    master.ClockOffsetSeconds);
                return JsonSerializer.Deserialize<WristbandSettingsPackage>(
                           responseBody,
                           JsonOptions)
                       ?? throw new InvalidDataException(
                           "The master Systems Controller returned empty wristband settings.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                       JsonException or InvalidDataException or IOException)
            {
                ControllerLog.Write("Master wristband-settings relay error: " + ex.Message);
                RemovePeer(master.ControllerId);
                if (attempt == 0)
                    continue;
                throw new InvalidOperationException(
                    "The wristband settings could not be sent to the master Systems Controller.",
                    ex);
            }
        }

        throw new InvalidOperationException(
            "The wristband settings could not be sent to the master Systems Controller.");
    }

    public async Task<ControllerPeerCommandResult> QueueSoftwareUpdateOnMasterAsync(
        string targetType,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var master = await ResolveMasterAsync(cancellationToken);
            if (master is null)
            {
                return new ControllerPeerCommandResult(
                    false,
                    "No active or saved master Systems Controller is available.");
            }

            try
            {
                var body = JsonSerializer.Serialize(new ControllerSoftwareUpdateRequest
                {
                    ControllerId = _state.ControllerId,
                    TargetType = targetType,
                    TargetId = targetId
                }, JsonOptions);
                var endpoint = new Uri(new Uri(master.ControllerAddress), SoftwareUpdatePath);
                using var request = CreateSignedRequest(
                    endpoint, master.PeerAccessKey, body, master.ClockOffsetSeconds);
                using var response = await _client.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidDataException(
                        string.IsNullOrWhiteSpace(responseBody)
                            ? $"The master Systems Controller returned HTTP {(int)response.StatusCode}."
                            : responseBody.Trim());
                }

                VerifySignedResponse(
                    response, master.PeerAccessKey, responseBody, master.ClockOffsetSeconds);
                var result = JsonSerializer.Deserialize<ControllerPeerCommandResponse>(
                    responseBody, JsonOptions);
                return result is null
                    ? new ControllerPeerCommandResult(
                        false, "The master Systems Controller returned an empty response.")
                    : new ControllerPeerCommandResult(result.Accepted, result.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new ControllerPeerCommandResult(false, "The update request was canceled.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                       JsonException or InvalidDataException or IOException)
            {
                ControllerLog.Write("Master software update relay error: " + ex.Message);
                RemovePeer(master.ControllerId);
                if (attempt == 0)
                    continue;
            }
        }

        return new ControllerPeerCommandResult(
            false,
            "The update request could not be sent to the saved master Systems Controller.");
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

        var master = await ResolveMasterAsync(cancellationToken);
        if (master is null)
        {
            return new ControllerConnectionPullResult(
                false,
                "No active master controller was detected on the local network.",
                0);
        }

        return await SynchronizeFromMasterAsync(
            ToPresence(master),
            cancellationToken,
            waitForCurrentSync: true);
    }

    public async Task<ControllerMasterConnectionResult> ConnectToMasterAsync(
        string connectionValue,
        CancellationToken cancellationToken = default)
    {
        if (_state.IsMaster)
        {
            return new ControllerMasterConnectionResult(
                false,
                "This computer is already the master controller.");
        }

        connectionValue = connectionValue?.Trim() ?? string.Empty;
        if (TryBuildManualControllerAddress(connectionValue, out var address))
        {
            var diagnostic = string.Empty;
            var presence = await AnnounceAsync(
                address,
                cancellationToken,
                failure => diagnostic = failure);
            if (presence is null)
            {
                return new ControllerMasterConnectionResult(
                    false,
                    "No Systems Controller answered at that local-network IP address." +
                    (string.IsNullOrWhiteSpace(diagnostic)
                        ? " Confirm that the master is running and that TCP 47832 is allowed on its Private-network firewall."
                        : "\n\n" + diagnostic));
            }
            if (!presence.IsMaster)
            {
                return new ControllerMasterConnectionResult(
                    false,
                    $"{presence.MachineName} answered at that address, but it is not the master controller.");
            }

            var result = await SynchronizeFromMasterAsync(
                presence,
                cancellationToken,
                waitForCurrentSync: true);
            return new ControllerMasterConnectionResult(result.Success, result.Message);
        }

        if (connectionValue.Length is < 16 or > 1_000)
        {
            return new ControllerMasterConnectionResult(
                false,
                "Enter the master computer's local IPv4 address or its full pairing key.");
        }

        var fingerprint = ControllerSecurity.Fingerprint(connectionValue);
        var matchingMaster = Snapshot().FirstOrDefault(peer =>
            peer.IsMaster &&
            string.Equals(peer.PairingKeyFingerprint, fingerprint, StringComparison.Ordinal));
        if (matchingMaster is null)
        {
            await ScanNowAsync(cancellationToken);
            matchingMaster = Snapshot().FirstOrDefault(peer =>
                peer.IsMaster &&
                string.Equals(peer.PairingKeyFingerprint, fingerprint, StringComparison.Ordinal));
        }
        if (matchingMaster is null)
        {
            foreach (var legacyMaster in Snapshot().Where(peer =>
                         peer.IsMaster &&
                         string.IsNullOrWhiteSpace(peer.PairingKeyFingerprint)))
            {
                var legacyResult = await SynchronizeFromMasterAsync(
                    ToPresence(legacyMaster),
                    cancellationToken,
                    waitForCurrentSync: true,
                    expectedPairingKey: connectionValue);
                if (legacyResult.Success)
                {
                    return new ControllerMasterConnectionResult(
                        true,
                        legacyResult.Message);
                }
            }
            return new ControllerMasterConnectionResult(
                false,
                "No master controller using that pairing key was found on the local network. " +
                "If discovery is unavailable, enter the master's local IPv4 address instead.");
        }

        var syncResult = await SynchronizeFromMasterAsync(
            ToPresence(matchingMaster),
            cancellationToken,
            waitForCurrentSync: true,
            expectedPairingKey: connectionValue);
        return new ControllerMasterConnectionResult(syncResult.Success, syncResult.Message);
    }

    public async Task<ControllerMasterConnectionResult> ConnectToStoredMasterAsync(
        CancellationToken cancellationToken = default)
    {
        var stored = _state.MasterControllerSnapshot();
        if (stored is null)
        {
            return new ControllerMasterConnectionResult(
                false,
                "No master controller connection has been saved on this computer yet.");
        }

        var master = await ResolveMasterAsync(cancellationToken, stored);
        if (master is null)
        {
            return new ControllerMasterConnectionResult(
                false,
                $"The saved master controller {stored.MachineName} could not be reached. " +
                "Confirm that it is running and connected to the local network.");
        }

        var result = await SynchronizeFromMasterAsync(
            ToPresence(master),
            cancellationToken,
            waitForCurrentSync: true,
            expectedPairingKey: stored.PairingKey);
        return new ControllerMasterConnectionResult(result.Success, result.Message);
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
        var response = CreateLocalPresence(BuildControllerAddress(localAddress));
        if (_state.IsMaster &&
            !string.Equals(presence.ControllerId, _state.ControllerId, StringComparison.Ordinal))
        {
            response.InstallControllerUpdate =
                _state.TakeControllerUpdate(presence.ControllerId);
            response.PosUpdateRequests = _state
                .TakePosUpdates(presence.ActivePosMachines)
                .ToList();
        }
        return response;
    }

    public async Task ScanNowAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopping.Token);
        await _scanGate.WaitAsync(linkedCancellation.Token);
        try
        {
            await ProbeControllersAsync(
                FindSubnetControllerAddresses(),
                linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
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
                var stored = _state.MasterControllerSnapshot();
                if (stored is not null &&
                    !Snapshot().Any(peer =>
                        peer.IsMaster &&
                        string.Equals(peer.ControllerId, stored.ControllerId, StringComparison.Ordinal)))
                {
                    await ProbeStoredMasterAsync(stored, cancellationToken);
                }

                await ProbePriorityCandidatesAsync(cancellationToken);

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
                EvaluateMasterElection();
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

    private async ValueTask<ControllerPeerPresence?> AnnounceAsync(
        string address,
        CancellationToken cancellationToken,
        Action<string>? reportFailure = null,
        string? expectedControllerId = null,
        string? expectedPairingKey = null,
        bool allowDuplicateIdentityRepair = true)
    {
        try
        {
            if (!TryNormalizeControllerAddress(address, out var normalized))
            {
                reportFailure?.Invoke("The controller address format was not valid.");
                return null;
            }
            var body = JsonSerializer.Serialize(CreateLocalPresence(string.Empty), JsonOptions);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var endpoint = new Uri(new Uri(normalized), PresencePath);
            using var response = await _client.PostAsync(endpoint, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                reportFailure?.Invoke(
                    $"The controller returned HTTP {(int)response.StatusCode}" +
                    (string.IsNullOrWhiteSpace(errorBody) ? "." : ": " + errorBody.Trim()));
                return null;
            }
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var presence = JsonSerializer.Deserialize<ControllerPeerPresence>(responseBody, JsonOptions);
            if (presence is null)
            {
                reportFailure?.Invoke("The computer answered, but its controller response was empty.");
                return null;
            }
            ValidatePresence(presence);
            if (string.Equals(presence.ControllerId, _state.ControllerId, StringComparison.Ordinal))
            {
                if (allowDuplicateIdentityRepair &&
                    !IsLocalControllerAddress(normalized) &&
                    _state.RepairDuplicateControllerIdentity(
                        presence.ControllerId,
                        presence.MachineName,
                        out var replacementId))
                {
                    ControllerLog.Write(
                        $"Retrying {presence.MachineName} after automatically repairing " +
                        $"the duplicated controller ID ({replacementId}).");
                    return await AnnounceAsync(
                        address,
                        cancellationToken,
                        reportFailure,
                        expectedControllerId,
                        expectedPairingKey,
                        allowDuplicateIdentityRepair: false);
                }
                reportFailure?.Invoke("That address belongs to this controller, not the master PC.");
                return null;
            }
            if (!string.IsNullOrWhiteSpace(expectedControllerId) &&
                !string.Equals(presence.ControllerId, expectedControllerId, StringComparison.Ordinal))
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(expectedPairingKey) &&
                !string.IsNullOrWhiteSpace(presence.PairingKeyFingerprint) &&
                !string.Equals(
                    presence.PairingKeyFingerprint,
                    ControllerSecurity.Fingerprint(expectedPairingKey),
                    StringComparison.Ordinal))
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(expectedControllerId) &&
                !ControllerResponseMatchesEndpoint(normalized, presence.ControllerAddress))
            {
                reportFailure?.Invoke(
                    "The controller answered from a different address than the one entered.");
                return null;
            }
            UpdatePeer(presence);
            if (presence.IsMaster && !_state.IsMaster)
            {
                await SynchronizeFromMasterAsync(
                    presence,
                    cancellationToken,
                    expectedPairingKey: expectedPairingKey);
            }
            foreach (var machineName in presence.PosUpdateRequests)
                _state.QueuePosUpdate(machineName);
            if (presence.InstallControllerUpdate)
                RaiseSoftwareUpdateRequested();
            return presence;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
                                   JsonException or InvalidDataException or IOException or
                                   ObjectDisposedException)
        {
            // Most addresses are not controller computers. Failed probes are expected.
            if (reportFailure is not null)
            {
                var message = ex is TaskCanceledException
                    ? "The connection timed out. Verify that the master controller is open and TCP 47832 is allowed through Windows Firewall."
                    : $"The controller connection failed: {ex.Message}";
                ControllerLog.Write($"Manual master probe failed for {address}: {ex.Message}");
                reportFailure(message);
            }
            return null;
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
        PairingKeyFingerprint = _state.IsMaster
            ? ControllerSecurity.Fingerprint(_state.PairingKey)
            : string.Empty,
        ServerUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ActivePosMachines = _state.ActivePosMachineNames().ToList(),
        PosMachines = _state.ActivePosMachinesSnapshot()
            .Select(machine => new ControllerPosPresence
            {
                MachineName = machine.MachineName,
                IpAddress = machine.IpAddress,
                Version = machine.Version,
                LastSeenUtc = machine.LastSeenUtc
            })
            .ToList()
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
                    $"Discovered Systems Controller {presence.MachineName} at {presence.ControllerAddress}.");
            }

            if (!string.Equals(peer.MachineName, presence.MachineName, StringComparison.Ordinal) ||
                !string.Equals(peer.Version, presence.Version, StringComparison.Ordinal) ||
                peer.IsMaster != presence.IsMaster ||
                peer.MasterSinceUtc != presence.MasterSinceUtc ||
                !string.Equals(peer.PeerAccessKey, presence.PeerAccessKey, StringComparison.Ordinal) ||
                !string.Equals(
                    peer.PairingKeyFingerprint,
                    presence.PairingKeyFingerprint,
                    StringComparison.Ordinal) ||
                !peer.ActivePosMachines.SequenceEqual(
                    presence.ActivePosMachines,
                    StringComparer.OrdinalIgnoreCase) ||
                !PosMachinesEqual(peer.PosMachines, presence.PosMachines) ||
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
            peer.PairingKeyFingerprint = presence.PairingKeyFingerprint;
            peer.ClockOffsetSeconds = CalculateClockOffsetSeconds(
                presence.ServerUnixTimeSeconds);
            peer.ActivePosMachines = [.. presence.ActivePosMachines];
            peer.PosMachines = presence.PosMachines
                .Select(machine => machine.Clone())
                .ToList();
            peer.LastSeenUtc = DateTime.UtcNow;
        }

        _state.RememberControllerPresence(
            presence.ControllerId,
            presence.MachineName,
            presence.ControllerAddress);

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
        var localPriority = _state.MasterPriorityRank(_state.ControllerId);
        var peerPriority = _state.MasterPriorityRank(peer.ControllerId);
        if (localPriority != peerPriority)
            return localPriority < peerPriority;
        var localSince = _state.MasterSinceUtc ?? DateTime.MaxValue;
        var peerSince = peer.MasterSinceUtc ?? DateTime.MaxValue;
        var timeComparison = localSince.CompareTo(peerSince);
        return timeComparison < 0 ||
               (timeComparison == 0 && string.CompareOrdinal(_state.ControllerId, peer.ControllerId) < 0);
    }

    private void EvaluateMasterElection()
    {
        if (_state.IsMaster)
        {
            _noMasterSinceUtc = null;
            return;
        }

        var peers = Snapshot();
        if (peers.Any(peer => peer.IsMaster))
        {
            _noMasterSinceUtc = null;
            return;
        }

        var priority = _state.MasterPrioritySnapshot();
        if (priority.Count == 0 ||
            !priority.Any(entry =>
                string.Equals(entry.ControllerId, _state.ControllerId, StringComparison.Ordinal)))
        {
            _noMasterSinceUtc = null;
            return;
        }

        var now = DateTime.UtcNow;
        _noMasterSinceUtc ??= now;
        if (now - _startedUtc < TimeSpan.FromSeconds(30) ||
            now - _noMasterSinceUtc < TimeSpan.FromSeconds(20))
        {
            return;
        }

        var reachable = peers.Select(peer => peer.ControllerId).Append(_state.ControllerId);
        var winner = ControllerMasterElection.SelectWinner(priority, reachable);
        if (winner is null ||
            !string.Equals(winner.ControllerId, _state.ControllerId, StringComparison.Ordinal))
        {
            return;
        }

        _state.SetMaster(
            true,
            "automatic failover selected this device ID as the highest-priority reachable controller");
        _noMasterSinceUtc = null;
        RaisePeersChanged();
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

    private void RaiseSoftwareUpdateRequested()
    {
        try { SoftwareUpdateRequested?.Invoke(); }
        catch (Exception ex) { ControllerLog.Write("Remote controller update notification error: " + ex.Message); }
    }

    private static bool PosMachinesEqual(
        IReadOnlyList<ControllerPosPresence> first,
        IReadOnlyList<ControllerPosPresence> second) =>
        first.OrderBy(machine => machine.MachineName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(machine => machine.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(machine => $"{machine.MachineName}|{machine.IpAddress}|{machine.Version}")
            .SequenceEqual(
                second.OrderBy(machine => machine.MachineName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(machine => machine.IpAddress, StringComparer.OrdinalIgnoreCase)
                    .Select(machine => $"{machine.MachineName}|{machine.IpAddress}|{machine.Version}"),
                StringComparer.OrdinalIgnoreCase);

    private static void ValidatePresence(ControllerPeerPresence presence)
    {
        if (presence.ProtocolVersion != 1 ||
            !Guid.TryParseExact(presence.ControllerId, "N", out _) ||
            string.IsNullOrWhiteSpace(presence.MachineName) ||
            presence.MachineName.Length > 200 ||
            presence.Version?.Length > 100 ||
            presence.ControllerAddress?.Length > 300 ||
            presence.PeerAccessKey?.Length > 200 ||
            (presence.PairingKeyFingerprint?.Length is not 0 and not 64) ||
            presence.ServerUnixTimeSeconds is < 0 or > 253_402_300_799 ||
            (presence.IsMaster != presence.MasterSinceUtc.HasValue))
        {
            throw new InvalidDataException("The controller presence announcement is invalid.");
        }
        presence.MachineName = presence.MachineName.Trim();
        presence.Version = string.IsNullOrWhiteSpace(presence.Version)
            ? "Unknown"
            : presence.Version.Trim();
        presence.PeerAccessKey = presence.PeerAccessKey?.Trim() ?? string.Empty;
        presence.PairingKeyFingerprint = presence.PairingKeyFingerprint?.Trim() ?? string.Empty;
        presence.ActivePosMachines = (presence.ActivePosMachines ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().Length <= 80 ? name.Trim() : name.Trim()[..80])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        presence.PosMachines = (presence.PosMachines ?? [])
            .Where(machine => machine is not null && !string.IsNullOrWhiteSpace(machine.MachineName))
            .Select(machine =>
            {
                var machineName = machine.MachineName.Trim();
                var ipAddress = machine.IpAddress?.Trim() ?? string.Empty;
                var version = machine.Version?.Trim() ?? string.Empty;
                return new ControllerPosPresence
                {
                    MachineName = machineName.Length <= 80 ? machineName : machineName[..80],
                    IpAddress = ipAddress.Length <= 80 ? ipAddress : ipAddress[..80],
                    Version = string.IsNullOrWhiteSpace(version)
                        ? "Unknown"
                        : version.Length <= 40 ? version : version[..40],
                    LastSeenUtc = machine.LastSeenUtc
                };
            })
            .GroupBy(machine => machine.MachineName + "|" + machine.IpAddress,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Take(10)
            .ToList();
        if (presence.PosMachines.Count == 0)
        {
            presence.PosMachines = presence.ActivePosMachines
                .Select(name => new ControllerPosPresence
                {
                    MachineName = name,
                    Version = "Unknown"
                })
                .ToList();
        }
        presence.PosUpdateRequests = (presence.PosUpdateRequests ?? [])
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
        bool waitForCurrentSync = false,
        string? expectedPairingKey = null)
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
            var clockOffsetSeconds = CalculateClockOffsetSeconds(
                master.ServerUnixTimeSeconds);
            using var request = CreateSignedRequest(
                endpoint, master.PeerAccessKey, body, clockOffsetSeconds);
            using var response = await _client.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ControllerConnectionPullResult(
                    false,
                    $"The master controller returned HTTP {(int)response.StatusCode}" +
                    (string.IsNullOrWhiteSpace(responseBody)
                        ? "."
                        : ": " + responseBody.Trim()),
                    0);
            }

            VerifySignedResponse(
                response, master.PeerAccessKey, responseBody, clockOffsetSeconds);
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

            if (!string.IsNullOrWhiteSpace(master.PairingKeyFingerprint) &&
                !string.Equals(
                    master.PairingKeyFingerprint,
                    ControllerSecurity.Fingerprint(replica.PairingKey),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The master controller pairing identity did not match.");
            }
            if (!string.IsNullOrWhiteSpace(expectedPairingKey) &&
                !string.Equals(
                    ControllerSecurity.Fingerprint(expectedPairingKey),
                    ControllerSecurity.Fingerprint(replica.PairingKey),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The saved master pairing key did not match.");
            }

            _state.RememberMasterController(
                master.ControllerId,
                master.MachineName,
                master.ControllerAddress,
                replica.PairingKey,
                master.PeerAccessKey);

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
                "The stored connections could not be pulled from the master controller.\n\n" +
                ex.Message,
                0);
        }
        finally
        {
            _replicaGate.Release();
        }
    }

    private static HttpRequestMessage CreateSignedRequest(
        Uri uri,
        string accessKey,
        string body,
        long clockOffsetSeconds = 0)
    {
        var timestamp = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + clockOffsetSeconds)
            .ToString();
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
        string body,
        long clockOffsetSeconds = 0)
    {
        if (!response.Headers.TryGetValues(TimestampHeader, out var timestamps) ||
            !response.Headers.TryGetValues(SignatureHeader, out var signatures))
        {
            throw new InvalidDataException("The master controller response was not signed.");
        }

        var timestamp = timestamps.FirstOrDefault() ?? string.Empty;
        var signature = signatures.FirstOrDefault() ?? string.Empty;
        var expectedRemoteTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + clockOffsetSeconds;
        if (!long.TryParse(timestamp, out var unixTime) ||
            Math.Abs(expectedRemoteTime - unixTime) > 300 ||
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

    private static bool IsLocalControllerAddress(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return false;
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!IPAddress.TryParse(uri.Host, out var target))
            return false;
        if (target.IsIPv4MappedToIPv6)
            target = target.MapToIPv4();
        if (IPAddress.IsLoopback(target))
            return true;

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address.IsIPv4MappedToIPv6
                    ? unicast.Address.MapToIPv4()
                    : unicast.Address)
                .Any(local => local.Equals(target));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private async Task<DiscoveredControllerPeer?> ResolveMasterAsync(
        CancellationToken cancellationToken,
        StoredMasterControllerConnection? requiredMaster = null)
    {
        var stored = requiredMaster ?? _state.MasterControllerSnapshot();
        var master = FindPreferredMaster(stored, allowOtherMaster: requiredMaster is null);
        if (master is not null)
            return master;

        if (stored is not null)
        {
            await ProbeStoredMasterAsync(stored, cancellationToken);
            master = FindPreferredMaster(stored, allowOtherMaster: requiredMaster is null);
            if (master is not null)
                return master;
            if (requiredMaster is not null)
            {
                await ScanNowAsync(cancellationToken);
                return FindPreferredMaster(stored, allowOtherMaster: false);
            }
        }

        await ScanNowAsync(cancellationToken);
        return FindPreferredMaster(stored);
    }

    private DiscoveredControllerPeer? FindPreferredMaster(
        StoredMasterControllerConnection? stored,
        bool allowOtherMaster = true)
    {
        var peers = Snapshot();
        if (stored is not null)
        {
            var savedMaster = peers.FirstOrDefault(peer =>
                peer.IsMaster &&
                !string.IsNullOrWhiteSpace(peer.ControllerAddress) &&
                !string.IsNullOrWhiteSpace(peer.PeerAccessKey) &&
                string.Equals(peer.ControllerId, stored.ControllerId, StringComparison.Ordinal));
            if (savedMaster is not null)
                return savedMaster;
        }
        return allowOtherMaster
            ? peers.FirstOrDefault(peer =>
                peer.IsMaster &&
                !string.IsNullOrWhiteSpace(peer.ControllerAddress) &&
                !string.IsNullOrWhiteSpace(peer.PeerAccessKey))
            : null;
    }

    private async Task ProbeStoredMasterAsync(
        StoredMasterControllerConnection stored,
        CancellationToken cancellationToken)
    {
        var targets = new List<string>();
        if (TryNormalizeControllerAddress(stored.LastKnownAddress, out var savedAddress))
            targets.Add(savedAddress);
        if (TryBuildComputerNameAddress(stored.MachineName, out var computerNameAddress))
            targets.Add(computerNameAddress);

        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var presence = await AnnounceAsync(
                target,
                cancellationToken,
                expectedControllerId: stored.ControllerId,
                expectedPairingKey: stored.PairingKey);
            if (presence?.IsMaster == true)
                return;
        }
    }

    private async Task ProbePriorityCandidatesAsync(CancellationToken cancellationToken)
    {
        var knownIds = Snapshot().Select(peer => peer.ControllerId)
            .Append(_state.ControllerId)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = _state.MasterPrioritySnapshot()
            .Where(candidate => !knownIds.Contains(candidate.ControllerId))
            .ToArray();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 8
            },
            async (candidate, token) => await ProbePriorityCandidateAsync(candidate, token));
    }

    private async ValueTask ProbePriorityCandidateAsync(
        MasterPriorityEntry candidate,
        CancellationToken cancellationToken)
    {
        var targets = new List<string>();
        if (TryNormalizeControllerAddress(candidate.LastKnownAddress, out var savedAddress))
            targets.Add(savedAddress);
        if (TryBuildComputerNameAddress(candidate.MachineName, out var computerNameAddress))
            targets.Add(computerNameAddress);
        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var presence = await AnnounceAsync(
                target,
                cancellationToken,
                expectedControllerId: candidate.ControllerId);
            if (presence is not null)
                break;
        }
    }

    private static ControllerPeerPresence ToPresence(DiscoveredControllerPeer peer) => new()
    {
        ControllerId = peer.ControllerId,
        MachineName = peer.MachineName,
        Version = peer.Version,
        IsMaster = peer.IsMaster,
        MasterSinceUtc = peer.MasterSinceUtc,
        ControllerAddress = peer.ControllerAddress,
        PeerAccessKey = peer.PeerAccessKey,
        PairingKeyFingerprint = peer.PairingKeyFingerprint,
        ServerUnixTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() +
                                peer.ClockOffsetSeconds,
        ActivePosMachines = [.. peer.ActivePosMachines],
        PosMachines = peer.PosMachines.Select(machine => machine.Clone()).ToList()
    };

    private static long CalculateClockOffsetSeconds(long serverUnixTimeSeconds)
    {
        if (serverUnixTimeSeconds <= 0)
            return 0;
        return serverUnixTimeSeconds - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static bool TryBuildManualControllerAddress(string value, out string address)
    {
        address = string.Empty;
        if (IPAddress.TryParse(value, out var parsedAddress))
        {
            if (parsedAddress.IsIPv4MappedToIPv6)
                parsedAddress = parsedAddress.MapToIPv4();
            if (!LocalNetworkAddress.IsPrivateOrDirectlyConnectedIpv4(parsedAddress))
                return false;
            address = BuildControllerAddress(parsedAddress);
            return true;
        }

        if (!TryNormalizeControllerAddress(value, out var normalized) ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !IPAddress.TryParse(uri.Host, out parsedAddress) ||
            !LocalNetworkAddress.IsPrivateOrDirectlyConnectedIpv4(parsedAddress))
        {
            return false;
        }
        address = normalized;
        return true;
    }

    private static bool TryBuildComputerNameAddress(string machineName, out string address)
    {
        address = string.Empty;
        machineName = machineName?.Trim() ?? string.Empty;
        if (machineName.Length is < 1 or > 200 ||
            Uri.CheckHostName(machineName) == UriHostNameType.Unknown)
        {
            return false;
        }
        address = $"http://{machineName}:{ControllerServer.Port}{ControllerServer.BasePath}";
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
                        !LocalNetworkAddress.IsUsableAdapterIpv4(address))
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
