using System.Security.Cryptography;
using System.Text.Json;

namespace MulletHopKioskController;

internal static class CommandTypes
{
    public const string SetClosed = "set-closed";
    public const string ResetStart = "reset-start";
    public const string CheckUpdate = "check-update";
    public const string InstallUpdate = "install-update";
    public const string SyncBusinessHours = "sync-business-hours";
}

internal sealed class KioskCheckInRequest
{
    public string StationId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool StationClosed { get; set; }
    public bool AvailableForGuests { get; set; }
    public bool HasError { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string LastCommandId { get; set; } = string.Empty;
    public bool LastCommandSuccess { get; set; }
    public string LastCommandMessage { get; set; } = string.Empty;
    public string AdvertisementSyncRevision { get; set; } = string.Empty;
    public DateTime? AdvertisementLastSyncUtc { get; set; }
    public string BusinessHoursSyncRevision { get; set; } = string.Empty;
    public DateTime? BusinessHoursLastSyncUtc { get; set; }
}

internal sealed class KioskCheckInResponse
{
    public KioskCommand? Command { get; set; }
    public string AdvertisementRevision { get; set; } = string.Empty;
    public string BusinessHoursRevision { get; set; } = string.Empty;
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
    public bool AvailableForGuests { get; set; }
    public bool HasError { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
    public string LastIpAddress { get; set; } = string.Empty;
    public string LastResult { get; set; } = "Waiting for the first command.";
    public bool LastResultSuccess { get; set; } = true;
    public string AdvertisementSyncRevision { get; set; } = string.Empty;
    public DateTime? AdvertisementLastSyncUtc { get; set; }
    public string BusinessHoursSyncRevision { get; set; } = string.Empty;
    public DateTime? BusinessHoursLastSyncUtc { get; set; }
    public KioskCommand? PendingCommand { get; set; }

    public bool IsOnline => DateTime.UtcNow - LastSeenUtc < TimeSpan.FromSeconds(18);

    public ManagedKiosk Clone() => new()
    {
        StationId = StationId,
        StationName = StationName,
        MachineName = MachineName,
        Version = Version,
        StationClosed = StationClosed,
        AvailableForGuests = AvailableForGuests,
        HasError = HasError,
        StatusMessage = StatusMessage,
        LastSeenUtc = LastSeenUtc,
        LastIpAddress = LastIpAddress,
        LastResult = LastResult,
        LastResultSuccess = LastResultSuccess,
        AdvertisementSyncRevision = AdvertisementSyncRevision,
        AdvertisementLastSyncUtc = AdvertisementLastSyncUtc,
        BusinessHoursSyncRevision = BusinessHoursSyncRevision,
        BusinessHoursLastSyncUtc = BusinessHoursLastSyncUtc,
        PendingCommand = PendingCommand?.Clone()
    };
}

internal sealed class PosKioskStatus
{
    public string StationId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public bool StationClosed { get; set; }
    public bool AvailableForGuests { get; set; }
    public bool HasError { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
}

internal sealed class PosCommandRequest
{
    public string StationId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool? Closed { get; set; }
}

internal sealed class ControllerData
{
    public string PairingKey { get; set; } = string.Empty;
    public string ControllerId { get; set; } = string.Empty;
    public bool IsMaster { get; set; }
    public DateTime? MasterSinceUtc { get; set; }
    public List<ManagedKiosk> Kiosks { get; set; } = [];
    public List<ControllerAdvertisement> Advertisements { get; set; } = [];
    public string AdvertisementRevision { get; set; } = string.Empty;
    public DateTime? AdvertisementUpdatedUtc { get; set; }
    public ControllerBusinessHours BusinessHours { get; set; } = new();
    public string BusinessHoursRevision { get; set; } = string.Empty;
    public DateTime? BusinessHoursUpdatedUtc { get; set; }
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
        var changed = false;
        if (string.IsNullOrWhiteSpace(_data.PairingKey))
        {
            _data.PairingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            changed = true;
        }
        if (!Guid.TryParseExact(_data.ControllerId, "N", out _))
        {
            _data.ControllerId = Guid.NewGuid().ToString("N");
            changed = true;
        }
        if (!_data.IsMaster && _data.MasterSinceUtc.HasValue)
        {
            _data.MasterSinceUtc = null;
            changed = true;
        }
        if (_data.IsMaster && !_data.MasterSinceUtc.HasValue)
        {
            _data.MasterSinceUtc = DateTime.UtcNow;
            changed = true;
        }
        if (changed)
            SaveLocked();
    }

    public string PairingKey
    {
        get
        {
            lock (_gate)
                return _data.PairingKey;
        }
    }

    public string ControllerId
    {
        get { lock (_gate) return _data.ControllerId; }
    }

    public bool IsMaster
    {
        get { lock (_gate) return _data.IsMaster; }
    }

    public DateTime? MasterSinceUtc
    {
        get { lock (_gate) return _data.MasterSinceUtc; }
    }

    public void SetMaster(bool isMaster, string reason)
    {
        lock (_gate)
        {
            if (_data.IsMaster == isMaster)
                return;
            _data.IsMaster = isMaster;
            _data.MasterSinceUtc = isMaster ? DateTime.UtcNow : null;
            SaveLocked();
            ControllerLog.Write(
                $"Controller master role changed to {(isMaster ? "MASTER" : "NOT MASTER")}: {reason}");
        }
    }

    public string AdvertisementRevision
    {
        get
        {
            lock (_gate)
                return _data.AdvertisementRevision;
        }
    }

    public DateTime? AdvertisementUpdatedUtc
    {
        get
        {
            lock (_gate)
                return _data.AdvertisementUpdatedUtc;
        }
    }

    public string BusinessHoursRevision
    {
        get { lock (_gate) return _data.BusinessHoursRevision; }
    }

    public DateTime? BusinessHoursUpdatedUtc
    {
        get { lock (_gate) return _data.BusinessHoursUpdatedUtc; }
    }

    public ControllerBusinessHours BusinessHoursSnapshot()
    {
        lock (_gate) return _data.BusinessHours.Clone();
    }

    public void SaveBusinessHours(ControllerBusinessHours profile)
    {
        lock (_gate)
        {
            profile = profile.Clone();
            profile.Normalize();
            _data.BusinessHours = profile;
            _data.BusinessHoursRevision = Guid.NewGuid().ToString("N");
            _data.BusinessHoursUpdatedUtc = DateTime.UtcNow;
            SaveLocked();
            ControllerLog.Write($"Published Business Hours and kiosk appearance profile {_data.BusinessHoursRevision}.");
        }
    }

    public BusinessHoursSyncPackage CreateBusinessHoursSyncPackage()
    {
        lock (_gate)
        {
            return new BusinessHoursSyncPackage
            {
                Revision = _data.BusinessHoursRevision,
                GeneratedUtc = DateTime.UtcNow,
                Enabled = _data.BusinessHours.Enabled,
                ClosedMessageMinutes = _data.BusinessHours.ClosedMessageMinutes,
                PreOpeningScreensaverMinutes = _data.BusinessHours.PreOpeningScreensaverMinutes,
                IncludesAppearanceSettings = true,
                ThemeMode = (int)_data.BusinessHours.ThemeMode,
                ScheduledDarkEnabled = _data.BusinessHours.ScheduledDarkEnabled,
                ScheduledDarkDays = _data.BusinessHours.ScheduledDarkDays
                    .Select(day => (int)day).ToArray(),
                ScheduledDarkTime = _data.BusinessHours.ScheduledDarkTime,
                Days = _data.BusinessHours.Days.Select(day => new BusinessHoursSyncItem
                {
                    Day = (int)day.Day, IsOpen = day.IsOpen,
                    OpenTime = day.OpenTime, CloseTime = day.CloseTime
                }).ToList()
            };
        }
    }

    public IReadOnlyList<ManagedKiosk> Snapshot()
    {
        lock (_gate)
            return _data.Kiosks.Select(kiosk => kiosk.Clone())
                .OrderBy(kiosk => kiosk.StationName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
    }

    public IReadOnlyList<PosKioskStatus> PosStatusSnapshot()
    {
        lock (_gate)
        {
            return _data.Kiosks
                .OrderBy(kiosk => kiosk.StationName, StringComparer.CurrentCultureIgnoreCase)
                .Select(kiosk => new PosKioskStatus
                {
                    StationId = kiosk.StationId,
                    StationName = kiosk.StationName,
                    MachineName = kiosk.MachineName,
                    IsOnline = kiosk.IsOnline,
                    StationClosed = kiosk.StationClosed,
                    AvailableForGuests = kiosk.AvailableForGuests,
                    HasError = kiosk.HasError,
                    StatusMessage = kiosk.StatusMessage,
                    LastSeenUtc = kiosk.LastSeenUtc
                })
                .ToList();
        }
    }

    public IReadOnlyList<ControllerAdvertisement> AdvertisementSnapshot()
    {
        lock (_gate)
            return _data.Advertisements.Select(advertisement => advertisement.Clone())
                .OrderBy(advertisement => advertisement.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
    }

    public void SaveAdvertisements(IEnumerable<ControllerAdvertisement> advertisements)
    {
        lock (_gate)
        {
            var normalized = advertisements.Select(advertisement => advertisement.Clone()).ToList();
            foreach (var advertisement in normalized)
                advertisement.Normalize();

            _data.Advertisements = normalized;
            _data.AdvertisementRevision = Guid.NewGuid().ToString("N");
            _data.AdvertisementUpdatedUtc = DateTime.UtcNow;
            SaveLocked();
            ControllerLog.Write(
                $"Published advertisement catalog {_data.AdvertisementRevision} with {normalized.Count} ad(s).");
        }
    }

    public AdvertisementSyncPackage CreateAdvertisementSyncPackage()
    {
        lock (_gate)
        {
            var package = new AdvertisementSyncPackage
            {
                Revision = _data.AdvertisementRevision,
                GeneratedUtc = DateTime.UtcNow
            };

            if (string.IsNullOrWhiteSpace(package.Revision))
                return package;

            foreach (var advertisement in _data.Advertisements)
            {
                var path = ControllerAdvertisementFiles.GetSafePath(advertisement.ImageFileName);
                if (path is null || !File.Exists(path))
                {
                    ControllerLog.Write(
                        $"Manager advertisement image is missing for {advertisement.Name}.");
                    throw new FileNotFoundException(
                        $"The manager advertisement image for {advertisement.Name} is missing.");
                }

                package.Advertisements.Add(new AdvertisementSyncItem
                {
                    Id = advertisement.Id,
                    Name = advertisement.Name,
                    ImageFileName = advertisement.ImageFileName,
                    ImageBase64 = Convert.ToBase64String(File.ReadAllBytes(path)),
                    Enabled = advertisement.Enabled,
                    ScheduleType = (int)advertisement.ScheduleType,
                    StartDateTime = advertisement.StartDateTime,
                    EndDateTime = advertisement.EndDateTime,
                    DaysOfWeek = advertisement.DaysOfWeek.Select(day => (int)day).ToArray(),
                    DailyStartTime = advertisement.DailyStartTime,
                    DailyEndTime = advertisement.DailyEndTime
                });
            }

            return package;
        }
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
            kiosk.AvailableForGuests = request.AvailableForGuests;
            kiosk.HasError = request.HasError;
            kiosk.StatusMessage = Clean(
                request.StatusMessage,
                request.AvailableForGuests ? "Online and open to guests." : "Not available to guests.",
                200);
            kiosk.LastSeenUtc = DateTime.UtcNow;
            kiosk.LastIpAddress = Clean(ipAddress, string.Empty, 80);
            kiosk.AdvertisementSyncRevision = Clean(
                request.AdvertisementSyncRevision, string.Empty, 80);
            kiosk.AdvertisementLastSyncUtc = request.AdvertisementLastSyncUtc;
            kiosk.BusinessHoursSyncRevision = Clean(
                request.BusinessHoursSyncRevision, string.Empty, 80);
            kiosk.BusinessHoursLastSyncUtc = request.BusinessHoursLastSyncUtc;

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

    public IReadOnlyList<CloudCommand> PendingCloudCommands()
    {
        lock (_gate)
        {
            return _data.Kiosks
                .Where(kiosk => kiosk.PendingCommand is not null)
                .Select(kiosk => new CloudCommand
                {
                    StationId = kiosk.StationId,
                    Command = kiosk.PendingCommand!.Clone()
                })
                .ToList();
        }
    }

    public void AcknowledgeCloudCommands(IEnumerable<CloudCommand> commands)
    {
        lock (_gate)
        {
            foreach (var sent in commands)
            {
                var kiosk = _data.Kiosks.FirstOrDefault(item => item.StationId == sent.StationId);
                if (kiosk?.PendingCommand?.Id == sent.Command.Id)
                {
                    kiosk.PendingCommand = null;
                    kiosk.LastResult = "Command accepted by the cloud relay.";
                }
            }
            SaveLocked();
        }
    }

    public void QueueCloudCommand(CloudCommand cloudCommand)
    {
        lock (_gate)
        {
            var kiosk = _data.Kiosks.FirstOrDefault(item => item.StationId == cloudCommand.StationId);
            if (kiosk is null || string.IsNullOrWhiteSpace(cloudCommand.Command.Id)) return;
            if (kiosk.PendingCommand?.Id == cloudCommand.Command.Id) return;
            kiosk.PendingCommand = cloudCommand.Command.Clone();
            kiosk.LastResult = DescribeQueuedCommand(
                cloudCommand.Command.Type, cloudCommand.Command.Closed);
            SaveLocked();
        }
    }

    public void ApplyCloudKioskSnapshot(IEnumerable<ManagedKiosk> cloudKiosks)
    {
        lock (_gate)
        {
            var pendingByStation = _data.Kiosks
                .Where(kiosk => kiosk.PendingCommand is not null)
                .ToDictionary(kiosk => kiosk.StationId, kiosk => kiosk.PendingCommand!.Clone());
            _data.Kiosks = cloudKiosks.Select(item => item.Clone()).ToList();
            foreach (var kiosk in _data.Kiosks)
            {
                if (pendingByStation.TryGetValue(kiosk.StationId, out var pending))
                    kiosk.PendingCommand = pending;
            }
            SaveLocked();
        }
    }

    public void ApplyCloudAdvertisements(AdvertisementSyncPackage package, DateTime updatedUtc)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(ControllerAdvertisementFiles.DirectoryPath);
            var imported = new List<ControllerAdvertisement>();
            foreach (var item in package.Advertisements)
            {
                var safeName = Path.GetFileName(item.ImageFileName);
                if (string.IsNullOrWhiteSpace(safeName) ||
                    !safeName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                    safeName = Guid.NewGuid().ToString("N") + ".jpg";
                var path = ControllerAdvertisementFiles.GetSafePath(safeName)
                           ?? throw new InvalidOperationException("The cloud advertisement filename is invalid.");
                File.WriteAllBytes(path, Convert.FromBase64String(item.ImageBase64));
                imported.Add(new ControllerAdvertisement
                {
                    Id = item.Id,
                    Name = item.Name,
                    ImageFileName = safeName,
                    Enabled = item.Enabled,
                    ScheduleType = Enum.IsDefined(typeof(ControllerAdvertisementScheduleType), item.ScheduleType)
                        ? (ControllerAdvertisementScheduleType)item.ScheduleType
                        : ControllerAdvertisementScheduleType.SpecificDates,
                    StartDateTime = item.StartDateTime,
                    EndDateTime = item.EndDateTime,
                    DaysOfWeek = item.DaysOfWeek
                        .Where(value => value is >= 0 and <= 6)
                        .Select(value => (DayOfWeek)value).ToArray(),
                    DailyStartTime = item.DailyStartTime,
                    DailyEndTime = item.DailyEndTime
                });
            }

            foreach (var advertisement in imported) advertisement.Normalize();
            _data.Advertisements = imported;
            _data.AdvertisementRevision = package.Revision;
            _data.AdvertisementUpdatedUtc = updatedUtc;
            SaveLocked();
            ControllerLog.Write($"Applied cloud advertisement catalog {package.Revision}.");
        }
    }

    public void ApplyCloudBusinessHours(BusinessHoursSyncPackage package, DateTime updatedUtc)
    {
        lock (_gate)
        {
            var profile = new ControllerBusinessHours
            {
                Enabled = package.Enabled,
                ClosedMessageMinutes = package.ClosedMessageMinutes,
                PreOpeningScreensaverMinutes = package.PreOpeningScreensaverMinutes,
                ThemeMode = package.IncludesAppearanceSettings && package.ThemeMode is >= 0 and <= 2
                    ? (ControllerKioskThemeMode)package.ThemeMode
                    : _data.BusinessHours.ThemeMode,
                ScheduledDarkEnabled = package.IncludesAppearanceSettings
                    ? package.ScheduledDarkEnabled
                    : _data.BusinessHours.ScheduledDarkEnabled,
                ScheduledDarkDays = package.IncludesAppearanceSettings
                    ? (package.ScheduledDarkDays ?? [])
                    .Where(day => day is >= 0 and <= 6)
                    .Select(day => (DayOfWeek)day).Distinct().ToArray()
                    : _data.BusinessHours.ScheduledDarkDays.ToArray(),
                ScheduledDarkTime = package.IncludesAppearanceSettings
                    ? package.ScheduledDarkTime
                    : _data.BusinessHours.ScheduledDarkTime,
                Days = package.Days.Select(item => new ControllerBusinessDayHours
                {
                    Day = item.Day is >= 0 and <= 6 ? (DayOfWeek)item.Day : DayOfWeek.Monday,
                    IsOpen = item.IsOpen, OpenTime = item.OpenTime, CloseTime = item.CloseTime
                }).ToList()
            };
            profile.Normalize();
            _data.BusinessHours = profile;
            _data.BusinessHoursRevision = package.Revision;
            _data.BusinessHoursUpdatedUtc = updatedUtc;
            SaveLocked();
            ControllerLog.Write($"Applied cloud Business Hours and kiosk appearance profile {package.Revision}.");
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
            data.ControllerId ??= string.Empty;
            data.Kiosks ??= [];
            data.Advertisements ??= [];
            data.AdvertisementRevision ??= string.Empty;
            data.BusinessHours ??= new ControllerBusinessHours();
            data.BusinessHoursRevision ??= string.Empty;
            data.BusinessHours.Normalize();
            foreach (var advertisement in data.Advertisements)
                advertisement.Normalize();
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
        CommandTypes.ResetStart => "Reset-to-start command queued.",
        CommandTypes.CheckUpdate => "Update check queued.",
        CommandTypes.InstallUpdate => "Update installation queued.",
        CommandTypes.SyncBusinessHours => "Business Hours sync queued.",
        _ => "Command queued."
    };

    private static string Clean(string? value, string fallback, int maxLength)
    {
        value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
