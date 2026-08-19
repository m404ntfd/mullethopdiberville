using System.Net;
using MulletHop.KioskDiscovery;

namespace MulletHopKioskController;

internal enum DiscoveryPairingState
{
    None,
    WaitingForKiosk,
    Accepted,
    Declined,
    Failed,
    Expired
}

internal sealed class DiscoveredKiosk
{
    public string StationId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
    public bool IsManaged { get; set; }
    public string CurrentController { get; set; } = string.Empty;
    public string KioskPublicKey { get; set; } = string.Empty;
    public string PairingRequestId { get; set; } = string.Empty;
    public DateTime PairingExpiresUtc { get; set; }
    public DiscoveryPairingState PairingState { get; set; }
    public string PairingMessage { get; set; } = string.Empty;

    public DiscoveredKiosk Clone() => new()
    {
        StationId = StationId,
        StationName = StationName,
        MachineName = MachineName,
        Version = Version,
        IpAddress = IpAddress,
        LastSeenUtc = LastSeenUtc,
        IsManaged = IsManaged,
        CurrentController = CurrentController,
        KioskPublicKey = KioskPublicKey,
        PairingRequestId = PairingRequestId,
        PairingExpiresUtc = PairingExpiresUtc,
        PairingState = PairingState,
        PairingMessage = PairingMessage
    };
}

internal sealed record PairingQueueResult(bool Success, string Message, string RequestId);

internal sealed class KioskDiscoveryCoordinator
{
    private readonly object _gate = new();
    private readonly ControllerState _state;
    private readonly Dictionary<string, DiscoveredKiosk> _devices = new(StringComparer.Ordinal);

    public KioskDiscoveryCoordinator(ControllerState state)
    {
        _state = state;
    }

    public IReadOnlyList<DiscoveredKiosk> Snapshot()
    {
        lock (_gate)
        {
            ExpireRequestsLocked();
            RemoveStaleDevicesLocked();
            return _devices.Values
                .Select(device => device.Clone())
                .OrderBy(device => device.StationName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(device => device.MachineName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public PairingQueueResult QueuePairing(string stationId)
    {
        lock (_gate)
        {
            ExpireRequestsLocked();
            if (!_devices.TryGetValue(stationId, out var device) ||
                DateTime.UtcNow - device.LastSeenUtc > TimeSpan.FromSeconds(30))
            {
                return new PairingQueueResult(
                    false,
                    "That kiosk is no longer responding. Refresh discovery and try again.",
                    string.Empty);
            }

            if (!KioskDiscoveryProtocol.IsValidPublicKey(device.KioskPublicKey))
            {
                return new PairingQueueResult(
                    false,
                    "The kiosk did not provide a valid secure pairing key.",
                    string.Empty);
            }

            device.PairingRequestId = Guid.NewGuid().ToString("N");
            device.PairingExpiresUtc = DateTime.UtcNow.AddMinutes(2);
            device.PairingState = DiscoveryPairingState.WaitingForKiosk;
            device.PairingMessage = "Waiting for confirmation on the waiver kiosk.";
            ControllerLog.Write(
                $"Pairing confirmation requested from {device.StationName} ({device.MachineName}).");
            return new PairingQueueResult(true, device.PairingMessage, device.PairingRequestId);
        }
    }

    public KioskDiscoveryResponse ProcessAnnouncement(
        KioskDiscoveryAnnouncement announcement,
        IPAddress remoteAddress,
        IPAddress localAddress)
    {
        ValidateAnnouncement(announcement);
        lock (_gate)
        {
            RemoveStaleDevicesLocked();
            if (!_devices.TryGetValue(announcement.StationId, out var device))
            {
                if (_devices.Count >= 200)
                {
                    var oldest = _devices.Values.MinBy(item => item.LastSeenUtc);
                    if (oldest is not null)
                        _devices.Remove(oldest.StationId);
                }
                device = new DiscoveredKiosk { StationId = announcement.StationId };
                _devices[announcement.StationId] = device;
                ControllerLog.Write(
                    $"Discovered waiver kiosk {announcement.StationName} ({announcement.MachineName}) at {remoteAddress}.");
            }

            device.StationName = Clean(announcement.StationName, announcement.MachineName, 60);
            device.MachineName = Clean(announcement.MachineName, "Unknown PC", 80);
            device.Version = Clean(announcement.Version, "Unknown", 30);
            device.IpAddress = remoteAddress.ToString();
            device.LastSeenUtc = DateTime.UtcNow;
            device.IsManaged = announcement.IsManaged;
            device.CurrentController = Clean(announcement.CurrentController, string.Empty, 300);
            device.KioskPublicKey = announcement.KioskPublicKey;

            ExpireRequestLocked(device);
            var acknowledgedRequestId = string.Empty;
            if (announcement.PairingResult is not null &&
                string.Equals(
                    announcement.PairingResult.RequestId,
                    device.PairingRequestId,
                    StringComparison.Ordinal))
            {
                acknowledgedRequestId = device.PairingRequestId;
                if (device.PairingState == DiscoveryPairingState.WaitingForKiosk)
                {
                    device.PairingState = announcement.PairingResult.Accepted
                        ? DiscoveryPairingState.Accepted
                        : DiscoveryPairingState.Declined;
                    device.PairingMessage = Clean(
                        announcement.PairingResult.Message,
                        announcement.PairingResult.Accepted
                            ? "The kiosk approved the connection."
                            : "The kiosk declined the connection.",
                        300);
                }
                ControllerLog.Write($"{device.StationName}: {device.PairingMessage}");
            }

            KioskPairingOffer? offer = null;
            var controllerAddress = BuildControllerAddress(localAddress);
            if (device.PairingState == DiscoveryPairingState.WaitingForKiosk)
            {
                try
                {
                    offer = KioskDiscoveryProtocol.EncryptPairingOffer(
                        device.KioskPublicKey,
                        new KioskPairingPayload
                        {
                            RequestId = device.PairingRequestId,
                            ControllerName = Environment.MachineName,
                            ControllerAddress = controllerAddress,
                            PairingKey = _state.PairingKey,
                            ExpiresUtc = device.PairingExpiresUtc
                        });
                }
                catch (Exception ex)
                {
                    device.PairingState = DiscoveryPairingState.Failed;
                    device.PairingMessage = "The secure pairing request could not be created.";
                    ControllerLog.Write("Discovery pairing encryption error: " + ex.Message);
                }
            }

            return new KioskDiscoveryResponse
            {
                ControllerName = Environment.MachineName,
                ControllerAddress = controllerAddress,
                PairingOffer = offer,
                AcknowledgedPairingRequestId = acknowledgedRequestId
            };
        }
    }

    private static void ValidateAnnouncement(KioskDiscoveryAnnouncement announcement)
    {
        if (announcement.ProtocolVersion != KioskDiscoveryProtocol.Version)
            throw new InvalidDataException("Unsupported discovery protocol version.");
        if (!Guid.TryParseExact(announcement.StationId, "N", out _))
            throw new InvalidDataException("The kiosk station ID is invalid.");
        if (announcement.StationName?.Length > 200 ||
            announcement.MachineName?.Length > 200 ||
            announcement.Version?.Length > 100 ||
            announcement.CurrentController?.Length > 500 ||
            announcement.KioskPublicKey?.Length > 1_000 ||
            !KioskDiscoveryProtocol.IsValidPublicKey(announcement.KioskPublicKey ?? string.Empty))
        {
            throw new InvalidDataException("The kiosk discovery announcement is invalid.");
        }
        if (announcement.PairingResult is not null &&
            (announcement.PairingResult.RequestId?.Length > 80 ||
             announcement.PairingResult.Message?.Length > 1_000))
        {
            throw new InvalidDataException("The kiosk pairing result is invalid.");
        }
    }

    private static string BuildControllerAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        return $"http://{host}:{KioskDiscoveryProtocol.ControllerPort}{KioskDiscoveryProtocol.ControllerBasePath}";
    }

    private void ExpireRequestsLocked()
    {
        foreach (var device in _devices.Values)
            ExpireRequestLocked(device);
    }

    private static void ExpireRequestLocked(DiscoveredKiosk device)
    {
        if (device.PairingState == DiscoveryPairingState.WaitingForKiosk &&
            device.PairingExpiresUtc <= DateTime.UtcNow)
        {
            device.PairingState = DiscoveryPairingState.Expired;
            device.PairingMessage = "The kiosk did not answer before the request expired.";
        }
    }

    private void RemoveStaleDevicesLocked()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        foreach (var stationId in _devices
                     .Where(pair => pair.Value.LastSeenUtc < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _devices.Remove(stationId);
        }
    }

    private static string Clean(string? value, string fallback, int maxLength)
    {
        value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
