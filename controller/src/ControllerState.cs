using System.Security.Cryptography;
using System.Text.Json;

namespace MulletHopKioskController;

internal static class CommandTypes
{
    public const string SetClosed = "set-closed";
    public const string CheckUpdate = "check-update";
    public const string InstallUpdate = "install-update";
}

internal sealed class KioskCheckInRequest
{
    public string StationId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool StationClosed { get; set; }
    public string LastCommandId { get; set; } = string.Empty;
    public bool LastCommandSuccess { get; set; }
    public string LastCommandMessage { get; set; } = string.Empty;
}

internal sealed class KioskCheckInResponse
{
    public KioskCommand? Command { get; set; }
}

internal sealed class KioskCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = string.Empty;
    public bool? Closed { get; set; }
    public DateTime QueuedUtc { get; set; } = DateTime.UtcNow;

    public KioskCommand Clone() => new()
    {
        Id = Id,
        Type = Type,
        Closed = Closed,
        QueuedUtc = QueuedUtc
    };
}

internal sealed class ManagedKiosk
{
    public string StationId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool StationClosed { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public string LastIpAddress { get; set; } = string.Empty;
    public string LastResult { get; set; } = "Waiting for the first command.";
    public bool LastResultSuccess { get; set; } = true;
    public KioskCommand? PendingCommand { get; set; }

    public bool IsOnline => DateTime.UtcNow - LastSeenUtc < TimeSpan.FromSeconds(18);

    public ManagedKiosk Clone() => new()
    {
        StationId = StationId,
        StationName = StationName,
        MachineName = MachineName,
        Version = Version,
        StationClosed = StationClosed,
        LastSeenUtc = LastSeenUtc,
        LastIpAddress = LastIpAddress,
        LastResult = LastResult,
        LastResultSuccess = LastResultSuccess,
        PendingCommand = PendingCommand?.Clone()
    };
}

internal sealed class ControllerData
{
    public string PairingKey { get; set; } = string.Empty;
    public List<ManagedKiosk> Kiosks { get; set; } = [];
}

internal sealed class ControllerState
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly object _gate = new();
    private readonly string _dataPath = Path.Combine(ControllerLog.DataDirectory, "controller.json");
    private ControllerData _data;

    public ControllerState()
    {
        _data = Load();
        if (string.IsNullOrWhiteSpace(_data.PairingKey))
        {
            _data.PairingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            SaveLocked();
        }
    }

    public string PairingKey
    {
        get
        {
            lock (_gate)
                return _data.PairingKey;
        }
    }

    public IReadOnlyList<ManagedKiosk> Snapshot()
    {
        lock (_gate)
            return _data.Kiosks.Select(kiosk => kiosk.Clone())
                .OrderBy(kiosk => kiosk.StationName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
    }

    public KioskCommand? ProcessCheckIn(KioskCheckInRequest request, string ipAddress)
    {
        lock (_gate)
        {
            var kiosk = _data.Kiosks.FirstOrDefault(item =>
                string.Equals(item.StationId, request.StationId, StringComparison.Ordinal));
            if (kiosk is null)
            {
                kiosk = new ManagedKiosk { StationId = request.StationId };
                _data.Kiosks.Add(kiosk);
                ControllerLog.Write($"New kiosk paired: {request.StationName} ({request.MachineName}).");
            }

            kiosk.StationName = Clean(request.StationName, request.MachineName, 60);
            kiosk.MachineName = Clean(request.MachineName, "Unknown PC", 80);
            kiosk.Version = Clean(request.Version, "Unknown", 30);
            kiosk.StationClosed = request.StationClosed;
            kiosk.LastSeenUtc = DateTime.UtcNow;
            kiosk.LastIpAddress = Clean(ipAddress, string.Empty, 80);

            if (kiosk.PendingCommand is not null &&
                string.Equals(kiosk.PendingCommand.Id, request.LastCommandId, StringComparison.Ordinal))
            {
                kiosk.LastResult = Clean(
                    request.LastCommandMessage,
                    request.LastCommandSuccess ? "Command completed." : "Command failed.",
                    500);
                kiosk.LastResultSuccess = request.LastCommandSuccess;
                ControllerLog.Write(
                    $"{kiosk.StationName} completed {kiosk.PendingCommand.Type}: {kiosk.LastResult}");
                kiosk.PendingCommand = null;
            }

            SaveLocked();
            return kiosk.PendingCommand?.Clone();
        }
    }

    public bool QueueCommand(string stationId, string type, bool? closed = null)
    {
        lock (_gate)
        {
            var kiosk = _data.Kiosks.FirstOrDefault(item =>
                string.Equals(item.StationId, stationId, StringComparison.Ordinal));
            if (kiosk is null)
                return false;

            kiosk.PendingCommand = new KioskCommand
            {
                Type = type,
                Closed = closed
            };
            kiosk.LastResult = DescribeQueuedCommand(type, closed);
            kiosk.LastResultSuccess = true;
            SaveLocked();
            ControllerLog.Write(kiosk.StationName + ": " + kiosk.LastResult);
            return true;
        }
    }

    public int QueueCommandForAll(string type, bool? closed = null)
    {
        lock (_gate)
        {
            foreach (var kiosk in _data.Kiosks)
            {
                kiosk.PendingCommand = new KioskCommand { Type = type, Closed = closed };
                kiosk.LastResult = DescribeQueuedCommand(type, closed);
                kiosk.LastResultSuccess = true;
            }
            SaveLocked();
            ControllerLog.Write($"Queued {type} for all {_data.Kiosks.Count} known kiosks.");
            return _data.Kiosks.Count;
        }
    }

    private ControllerData Load()
    {
        try
        {
            Directory.CreateDirectory(ControllerLog.DataDirectory);
            if (!File.Exists(_dataPath))
                return new ControllerData();

            var data = JsonSerializer.Deserialize<ControllerData>(File.ReadAllText(_dataPath), JsonOptions)
                       ?? new ControllerData();
            data.Kiosks ??= [];
            return data;
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller data read error: " + ex.Message);
            return new ControllerData();
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(ControllerLog.DataDirectory);
            var temporaryPath = _dataPath + ".new";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_data, JsonOptions));
            File.Move(temporaryPath, _dataPath, true);
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller data save error: " + ex.Message);
        }
    }

    private static string DescribeQueuedCommand(string type, bool? closed) => type switch
    {
        CommandTypes.SetClosed when closed == true => "Close-screen command queued.",
        CommandTypes.SetClosed => "Open-kiosk command queued.",
        CommandTypes.CheckUpdate => "Update check queued.",
        CommandTypes.InstallUpdate => "Update installation queued.",
        _ => "Command queued."
    };

    private static string Clean(string? value, string fallback, int maxLength)
    {
        value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
