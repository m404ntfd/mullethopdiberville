using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MulletHop.KioskDiscovery;

namespace MulletHopKioskController;

internal sealed class ControllerServer : IDisposable
{
    public const int Port = 47832;
    public const string BasePath = "/mullethop/";
    private const string TimestampHeader = "X-MulletHop-Timestamp";
    private const string SignatureHeader = "X-MulletHop-Signature";
    private const string PosMachineHeader = "X-MulletHop-POS-Machine";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ControllerState _state;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _listenTask;

    public bool IsRunning => _listener.IsListening;
    public KioskDiscoveryCoordinator Discovery { get; }
    public ControllerPeerCoordinator Peers { get; }

    public ControllerServer(ControllerState state)
    {
        _state = state;
        Discovery = new KioskDiscoveryCoordinator(state);
        Peers = new ControllerPeerCoordinator(state);
        _listener.Prefixes.Add($"http://+:{Port}{BasePath}");
    }

    public void Start()
    {
        if (_listener.IsListening)
            return;
        _listener.Start();
        _listenTask = Task.Run(ListenAsync);
        Peers.Start();
        ControllerLog.Write($"Controller service listening on TCP {Port}.");
    }

    public async Task<ControllerPeerCommandResult> QueueCommandAsync(
        string stationId,
        string type,
        bool? closed = null,
        CancellationToken cancellationToken = default)
    {
        if (_state.IsMaster)
        {
            var accepted = _state.QueueCommand(stationId, type, closed);
            return new ControllerPeerCommandResult(
                accepted,
                accepted ? "Command queued for the waiver kiosk." : "Kiosk not found.");
        }

        return await Peers.QueueCommandOnMasterAsync(
            stationId,
            type,
            closed,
            cancellationToken);
    }

    private async Task ListenAsync()
    {
        while (!_stopping.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => ProcessRequestAsync(context));
            }
            catch (HttpListenerException) when (_stopping.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ControllerLog.Write("Controller listener error: " + ex.Message);
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WritePlainResponseAsync(context, HttpStatusCode.MethodNotAllowed, "POST required.");
                return;
            }

            if (context.Request.ContentLength64 > 65_536)
            {
                await WritePlainResponseAsync(context, HttpStatusCode.RequestEntityTooLarge, "Request too large.");
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            if (string.Equals(
                    path,
                    BasePath.TrimEnd('/') + "/" + ControllerPeerCoordinator.PresencePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                var remoteAddress = context.Request.RemoteEndPoint?.Address;
                var localAddress = context.Request.LocalEndPoint?.Address;
                if (remoteAddress is null || localAddress is null || !IsPrivateOrLocal(remoteAddress))
                {
                    await WritePlainResponseAsync(
                        context, HttpStatusCode.Forbidden, "Controller discovery is limited to the local network.");
                    return;
                }

                var presence = JsonSerializer.Deserialize<ControllerPeerPresence>(body, JsonOptions);
                if (presence is null)
                {
                    await WritePlainResponseAsync(
                        context, HttpStatusCode.BadRequest, "Invalid controller presence announcement.");
                    return;
                }
                try
                {
                    var response = Peers.ProcessPresence(presence, remoteAddress, localAddress);
                    await WriteJsonResponseAsync(context, response);
                }
                catch (InvalidDataException ex)
                {
                    ControllerLog.Write("Rejected controller presence announcement: " + ex.Message);
                    await WritePlainResponseAsync(context, HttpStatusCode.BadRequest, ex.Message);
                }
                return;
            }
            if (string.Equals(
                    path,
                    BasePath.TrimEnd('/') + "/api/discovery/announce",
                    StringComparison.OrdinalIgnoreCase))
            {
                var remoteAddress = context.Request.RemoteEndPoint?.Address;
                var localAddress = context.Request.LocalEndPoint?.Address;
                if (remoteAddress is null || localAddress is null || !IsPrivateOrLocal(remoteAddress))
                {
                    await WritePlainResponseAsync(
                        context, HttpStatusCode.Forbidden, "Discovery is limited to the local network.");
                    return;
                }

                var announcement = JsonSerializer.Deserialize<KioskDiscoveryAnnouncement>(body, JsonOptions);
                if (announcement is null)
                {
                    await WritePlainResponseAsync(
                        context, HttpStatusCode.BadRequest, "Invalid kiosk discovery announcement.");
                    return;
                }

                try
                {
                    var response = Discovery.ProcessAnnouncement(
                        announcement, remoteAddress, localAddress);
                    await WriteJsonResponseAsync(context, response);
                }
                catch (InvalidDataException ex)
                {
                    ControllerLog.Write("Rejected kiosk discovery announcement: " + ex.Message);
                    await WritePlainResponseAsync(context, HttpStatusCode.BadRequest, ex.Message);
                }
                return;
            }

            if (string.Equals(
                    path,
                    BasePath.TrimEnd('/') + "/" + ControllerPeerCoordinator.ReplicaPath,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    path,
                    BasePath.TrimEnd('/') + "/" + ControllerPeerCoordinator.CommandPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                var remoteAddress = context.Request.RemoteEndPoint?.Address;
                if (remoteAddress is null || !IsPrivateOrLocal(remoteAddress))
                {
                    await WritePlainResponseAsync(
                        context,
                        HttpStatusCode.Forbidden,
                        "Controller synchronization is limited to the local network.");
                    return;
                }
                if (!_state.IsMaster)
                {
                    await WritePlainResponseAsync(
                        context,
                        HttpStatusCode.Conflict,
                        "This computer is not the master controller.");
                    return;
                }
                if (!IsAuthorized(context.Request, body, _state.PeerAccessKey))
                {
                    await WritePlainResponseAsync(
                        context,
                        HttpStatusCode.Unauthorized,
                        "Invalid controller synchronization key.");
                    return;
                }

                if (string.Equals(
                        path,
                        BasePath.TrimEnd('/') + "/" + ControllerPeerCoordinator.ReplicaPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var replicaRequest = JsonSerializer.Deserialize<ControllerPeerRequest>(body, JsonOptions);
                    if (replicaRequest is null ||
                        !Guid.TryParseExact(replicaRequest.ControllerId, "N", out _) ||
                        !Peers.IsKnownController(replicaRequest.ControllerId))
                    {
                        await WritePlainResponseAsync(
                            context,
                            HttpStatusCode.Forbidden,
                            "The requesting controller has not been discovered.");
                        return;
                    }

                    await WriteSignedResponseAsync(
                        context,
                        _state.CreateReplicaSnapshot(),
                        _state.PeerAccessKey);
                    return;
                }

                var peerCommand = JsonSerializer.Deserialize<ControllerPeerCommandRequest>(body, JsonOptions);
                if (peerCommand is null ||
                    !Guid.TryParseExact(peerCommand.ControllerId, "N", out _) ||
                    !Peers.IsKnownController(peerCommand.ControllerId) ||
                    !Guid.TryParseExact(peerCommand.StationId, "N", out _) ||
                    !IsValidPeerCommand(peerCommand.Type, peerCommand.Closed))
                {
                    await WritePlainResponseAsync(
                        context,
                        HttpStatusCode.BadRequest,
                        "Invalid controller command relay request.");
                    return;
                }

                var peerAccepted = _state.QueueCommand(
                    peerCommand.StationId,
                    peerCommand.Type,
                    peerCommand.Closed);
                await WriteSignedResponseAsync(
                    context,
                    new ControllerPeerCommandResponse
                    {
                        Accepted = peerAccepted,
                        Message = peerAccepted
                            ? "Command queued on the master controller."
                            : "Kiosk not found on the master controller."
                    },
                    _state.PeerAccessKey);
                return;
            }

            if (!IsAuthorized(context.Request, body))
            {
                ControllerLog.Write("Rejected a controller request with an invalid signature from " +
                    (context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown address") + ".");
                await WritePlainResponseAsync(context, HttpStatusCode.Unauthorized, "Invalid pairing key.");
                return;
            }

            if (string.Equals(path, BasePath.TrimEnd('/') + "/api/health", StringComparison.OrdinalIgnoreCase))
            {
                if (!_state.IsMaster)
                {
                    await WritePlainResponseAsync(
                        context,
                        HttpStatusCode.Conflict,
                        "This computer is not the master controller.");
                    return;
                }
                await WriteSignedResponseAsync(context, new
                {
                    ok = true,
                    serverTimeUtc = DateTime.UtcNow
                });
                return;
            }

            if (string.Equals(path, BasePath.TrimEnd('/') + "/api/pos/status", StringComparison.OrdinalIgnoreCase))
            {
                RecordPosMachine(context.Request);
                await WriteSignedResponseAsync(context, new
                {
                    serverTimeUtc = DateTime.UtcNow,
                    kiosks = _state.PosStatusSnapshot()
                });
                return;
            }

            if (string.Equals(path, BasePath.TrimEnd('/') + "/api/pos/command", StringComparison.OrdinalIgnoreCase))
            {
                RecordPosMachine(context.Request);
                var commandRequest = JsonSerializer.Deserialize<PosCommandRequest>(body, JsonOptions);
                if (commandRequest is null ||
                    !Guid.TryParseExact(commandRequest.StationId, "N", out _) ||
                    !IsValidPosCommand(commandRequest.Type, commandRequest.Closed))
                {
                    await WritePlainResponseAsync(context, HttpStatusCode.BadRequest, "Invalid Mullet Hop POS command.");
                    return;
                }

                var commandResult = await QueueCommandAsync(
                    commandRequest.StationId,
                    commandRequest.Type,
                    commandRequest.Closed);
                if (!commandResult.Accepted)
                {
                    await WritePlainResponseAsync(
                        context,
                        HttpStatusCode.NotFound,
                        commandResult.Message);
                    return;
                }

                await WriteSignedResponseAsync(context, new
                {
                    accepted = true,
                    message = commandResult.Message
                });
                return;
            }

            if (string.Equals(path, BasePath.TrimEnd('/') + "/api/ads/sync", StringComparison.OrdinalIgnoreCase))
            {
                if (!_state.IsMaster)
                {
                    await WritePlainResponseAsync(
                        context,
                        HttpStatusCode.Conflict,
                        "Kiosks synchronize with the master controller.");
                    return;
                }
                var syncRequest = JsonSerializer.Deserialize<AdvertisementSyncRequest>(body, JsonOptions);
                if (syncRequest is null || !Guid.TryParseExact(syncRequest.StationId, "N", out _))
                {
                    await WritePlainResponseAsync(
                        context, HttpStatusCode.BadRequest, "Invalid advertisement sync request.");
                    return;
                }

                var package = _state.CreateAdvertisementSyncPackage();
                ControllerLog.Write(
                    $"Advertisement catalog {package.Revision} sent to kiosk {syncRequest.StationId}.");
                await WriteSignedResponseAsync(context, package);
                return;
            }

            if (string.Equals(path, BasePath.TrimEnd('/') + "/api/business-hours/sync", StringComparison.OrdinalIgnoreCase))
            {
                if (!_state.IsMaster)
                {
                    await WritePlainResponseAsync(
                        context,
                        HttpStatusCode.Conflict,
                        "Kiosks synchronize with the master controller.");
                    return;
                }
                var syncRequest = JsonSerializer.Deserialize<BusinessHoursSyncRequest>(body, JsonOptions);
                if (syncRequest is null || !Guid.TryParseExact(syncRequest.StationId, "N", out _))
                {
                    await WritePlainResponseAsync(
                        context, HttpStatusCode.BadRequest, "Invalid Business Hours sync request.");
                    return;
                }

                var package = _state.CreateBusinessHoursSyncPackage();
                ControllerLog.Write(
                    $"Business Hours profile {package.Revision} sent to kiosk {syncRequest.StationId}.");
                await WriteSignedResponseAsync(context, package);
                return;
            }

            if (!string.Equals(path, BasePath.TrimEnd('/') + "/api/checkin", StringComparison.OrdinalIgnoreCase))
            {
                await WritePlainResponseAsync(context, HttpStatusCode.NotFound, "Not found.");
                return;
            }

            if (!_state.IsMaster)
            {
                await WritePlainResponseAsync(
                    context,
                    HttpStatusCode.Conflict,
                    "Kiosks check in with the master controller.");
                return;
            }

            var checkIn = JsonSerializer.Deserialize<KioskCheckInRequest>(body, JsonOptions);
            if (checkIn is null || !Guid.TryParseExact(checkIn.StationId, "N", out _))
            {
                await WritePlainResponseAsync(context, HttpStatusCode.BadRequest, "Invalid kiosk check-in.");
                return;
            }

            var command = _state.ProcessCheckIn(
                checkIn,
                context.Request.RemoteEndPoint?.Address.ToString() ?? string.Empty);
            await WriteSignedResponseAsync(context, new KioskCheckInResponse
            {
                Command = command,
                AdvertisementRevision = _state.AdvertisementRevision,
                BusinessHoursRevision = _state.BusinessHoursRevision
            });
        }
        catch (JsonException)
        {
            await WritePlainResponseAsync(context, HttpStatusCode.BadRequest, "Invalid JSON.");
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller request error: " + ex);
            if (context.Response.OutputStream.CanWrite)
                await WritePlainResponseAsync(context, HttpStatusCode.InternalServerError, "Controller error.");
        }
    }

    private bool IsAuthorized(HttpListenerRequest request, string body, string? accessKey = null)
    {
        var timestamp = request.Headers[TimestampHeader] ?? string.Empty;
        var signature = request.Headers[SignatureHeader] ?? string.Empty;
        if (!long.TryParse(timestamp, out var unixTime) ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixTime) > 300)
            return false;

        var expected = ControllerSecurity.Sign(accessKey ?? _state.PairingKey, timestamp, body);
        return ControllerSecurity.FixedTimeEquals(expected, signature);
    }

    private async Task WriteSignedResponseAsync(
        HttpListenerContext context,
        object payload,
        string? accessKey = null)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        context.Response.Headers[TimestampHeader] = timestamp;
        context.Response.Headers[SignatureHeader] =
            ControllerSecurity.Sign(accessKey ?? _state.PairingKey, timestamp, body);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await WriteBodyAsync(context, body);
    }

    private void RecordPosMachine(HttpListenerRequest request)
    {
        _state.RecordPosMachine(
            request.Headers[PosMachineHeader],
            request.RemoteEndPoint?.Address.ToString());
    }

    private static bool IsValidPosCommand(string type, bool? closed) =>
        (type == CommandTypes.SetClosed ||
         type == CommandTypes.SetBusinessClosed ||
         type == CommandTypes.ResetStart ||
         type == CommandTypes.AcknowledgeAssistance) &&
        ((type != CommandTypes.SetClosed && type != CommandTypes.SetBusinessClosed) ||
         closed.HasValue);

    private static bool IsValidPeerCommand(string type, bool? closed) =>
        (type == CommandTypes.SetClosed ||
         type == CommandTypes.SetBusinessClosed ||
         type == CommandTypes.ResetStart ||
         type == CommandTypes.CheckUpdate ||
         type == CommandTypes.InstallUpdate ||
         type == CommandTypes.SyncBusinessHours ||
         type == CommandTypes.AcknowledgeAssistance) &&
        ((type != CommandTypes.SetClosed && type != CommandTypes.SetBusinessClosed) ||
         closed.HasValue);

    private static async Task WriteJsonResponseAsync(HttpListenerContext context, object payload)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await WriteBodyAsync(context, body);
    }

    private static async Task WritePlainResponseAsync(
        HttpListenerContext context, HttpStatusCode statusCode, string message)
    {
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await WriteBodyAsync(context, message);
    }

    private static async Task WriteBodyAsync(HttpListenerContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static bool IsPrivateOrLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
        }
        return false;
    }

    public void Dispose()
    {
        Peers.Dispose();
        _stopping.Cancel();
        if (_listener.IsListening)
            _listener.Stop();
        _listener.Close();
        _stopping.Dispose();
    }
}

internal static class ControllerSecurity
{
    public static string Fingerprint(string pairingKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pairingKey.Trim())));

    public static string Sign(string pairingKey, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pairingKey));
        return Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "\n" + body)));
    }

    public static bool FixedTimeEquals(string expected, string actual)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(expected), Convert.FromBase64String(actual));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
