using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MulletHopKioskController;

internal static class CommandTypes
{
    public const string SetClosed = "set-closed";
    public const string SetBusinessClosed = "set-business-closed";
    public const string ResetStart = "reset-start";
    public const string CheckUpdate = "check-update";
    public const string InstallUpdate = "install-update";
    public const string SyncBusinessHours = "sync-business-hours";
    public const string AcknowledgeAssistance = "acknowledge-assistance";
}

internal sealed class KioskCheckInRequest
{
    public string StationId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool StationClosed { get; set; }
    public bool BusinessHoursClosed { get; set; }
    public bool AvailableForGuests { get; set; }
    public bool HasError { get; set; }
    public bool AssistanceRequested { get; set; }
    public bool AssistanceAcknowledged { get; set; }
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
    public bool BusinessHoursClosed { get; set; }
    public bool AvailableForGuests { get; set; }
    public bool HasError { get; set; }
    public bool AssistanceRequested { get; set; }
    public bool AssistanceAcknowledged { get; set; }
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
        BusinessHoursClosed = BusinessHoursClosed,
        AvailableForGuests = AvailableForGuests,
        HasError = HasError,
        AssistanceRequested = AssistanceRequested,
        AssistanceAcknowledged = AssistanceAcknowledged,
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
    public bool BusinessHoursClosed { get; set; }
    public bool AvailableForGuests { get; set; }
    public bool HasError { get; set; }
    public bool AssistanceRequested { get; set; }
    public bool AssistanceAcknowledged { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
}

internal sealed class PosCommandRequest
{
    public string StationId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool? Closed { get; set; }
}

internal sealed class ControllerReplicaSnapshot
{
    public string MasterControllerId { get; set; } = string.Empty;
    public string MasterMachineName { get; set; } = string.Empty;
    public string PairingKey { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public DateTime GeneratedUtc { get; set; }
    public List<ManagedKiosk> Kiosks { get; set; } = [];
    public AdvertisementSyncPackage Advertisements { get; set; } = new();
    public BusinessHoursSyncPackage BusinessHours { get; set; } = new();
    public List<MasterPriorityEntry> MasterPriority { get; set; } = [];
}

internal sealed class MasterPriorityEntry
{
    public string ControllerId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string LastKnownAddress { get; set; } = string.Empty;

    public MasterPriorityEntry Clone() => new()
    {
        ControllerId = ControllerId,
        MachineName = MachineName,
        LastKnownAddress = LastKnownAddress
    };
}

internal sealed class StoredControllerConnections
{
    public int SchemaVersion { get; set; } = 1;
    public string ControllerId { get; set; } = string.Empty;
    public string ControllerMachineName { get; set; } = string.Empty;
    public string PairingKey { get; set; } = string.Empty;
    public string PeerAccessKey { get; set; } = string.Empty;
    public DateTime SavedUtc { get; set; }
    public List<ManagedKiosk> Kiosks { get; set; } = [];
}

internal sealed record StoredConnectionsResult(bool Success, string Message, int ConnectionCount);

internal sealed class StoredMasterControllerConnection
{
    public string ControllerId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string LastKnownAddress { get; set; } = string.Empty;
    public string PairingKey { get; set; } = string.Empty;
    public string PeerAccessKey { get; set; } = string.Empty;
    public DateTime LastVerifiedUtc { get; set; }

    public StoredMasterControllerConnection Clone() => new()
    {
        ControllerId = ControllerId,
        MachineName = MachineName,
        LastKnownAddress = LastKnownAddress,
        PairingKey = PairingKey,
        PeerAccessKey = PeerAccessKey,
        LastVerifiedUtc = LastVerifiedUtc
    };
}

internal sealed class PosMachinePresence
{
    public string MachineName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }

    public PosMachinePresence Clone() => new()
    {
        MachineName = MachineName,
        IpAddress = IpAddress,
        Version = Version,
        LastSeenUtc = LastSeenUtc
    };
}

internal sealed class ControllerData
{
    public string PairingKey { get; set; } = string.Empty;
    public string PeerAccessKey { get; set; } = string.Empty;
    public string ControllerId { get; set; } = string.Empty;
    public bool IsMaster { get; set; }
    public DateTime? MasterSinceUtc { get; set; }
    public StoredMasterControllerConnection? MasterController { get; set; }
    public List<MasterPriorityEntry> MasterPriority { get; set; } = [];
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
    private readonly string _masterConnectionsPath = Path.Combine(
        ControllerLog.DataDirectory,
        "master-connections.json");
    private readonly Dictionary<string, PosMachinePresence> _posMachines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingControllerUpdates =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingPosUpdates =
        new(StringComparer.OrdinalIgnoreCase);
    private ControllerData _data;
    private string _lastMasterReplicaRevision = string.Empty;

    public ControllerState()
    {
        _data = Load();
        var changed = false;
        if (string.IsNullOrWhiteSpace(_data.PairingKey))
        {
            _data.PairingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(_data.PeerAccessKey))
        {
            _data.PeerAccessKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
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
        if (_data.IsMaster && _data.MasterController is not null)
        {
            _data.MasterController = null;
            changed = true;
        }
        if (_data.IsMaster && _data.MasterPriority.Count == 0)
        {
            _data.MasterPriority.Add(new MasterPriorityEntry
            {
                ControllerId = _data.ControllerId,
                MachineName = Environment.MachineName
            });
            changed = true;
        }
        if (changed || (_data.IsMaster && !File.Exists(_masterConnectionsPath)))
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

    public string PeerAccessKey
    {
        get { lock (_gate) return _data.PeerAccessKey; }
    }

    public bool IsMaster
    {
        get { lock (_gate) return _data.IsMaster; }
    }

    public DateTime? MasterSinceUtc
    {
        get { lock (_gate) return _data.MasterSinceUtc; }
    }

    public StoredMasterControllerConnection? MasterControllerSnapshot()
    {
        lock (_gate) return _data.MasterController?.Clone();
    }

    public IReadOnlyList<MasterPriorityEntry> MasterPrioritySnapshot()
    {
        lock (_gate) return _data.MasterPriority.Select(entry => entry.Clone()).ToList();
    }

    public void SaveMasterPriority(IEnumerable<MasterPriorityEntry> entries)
    {
        lock (_gate)
        {
            if (!_data.IsMaster)
                throw new InvalidOperationException(
                    "Master priority can be changed only on the active master Systems Controller.");
            var normalized = NormalizeMasterPriority(entries);
            if (normalized.Count == 0)
                throw new InvalidOperationException("Add at least one eligible master controller.");
            _data.MasterPriority = normalized;
            SaveLocked();
            ControllerLog.Write(
                $"Saved master failover priority for {_data.MasterPriority.Count} controller(s).");
        }
    }

    public void RememberControllerPresence(
        string controllerId,
        string machineName,
        string controllerAddress)
    {
        lock (_gate)
        {
            var entry = _data.MasterPriority.FirstOrDefault(candidate =>
                string.Equals(candidate.ControllerId, controllerId, StringComparison.Ordinal));
            if (entry is null)
                return;
            var cleanName = Clean(machineName, entry.MachineName, 200);
            var cleanAddress = Clean(controllerAddress, entry.LastKnownAddress, 300);
            if (string.Equals(entry.MachineName, cleanName, StringComparison.Ordinal) &&
                string.Equals(entry.LastKnownAddress, cleanAddress, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            entry.MachineName = cleanName;
            entry.LastKnownAddress = cleanAddress;
            SaveLocked();
        }
    }

    public int MasterPriorityRank(string controllerId)
    {
        lock (_gate)
        {
            var index = _data.MasterPriority.FindIndex(entry =>
                string.Equals(entry.ControllerId, controllerId, StringComparison.Ordinal));
            return index < 0 ? int.MaxValue : index;
        }
    }

    public bool RepairDuplicateControllerIdentity(
        string duplicatedControllerId,
        string remoteMachineName,
        out string newControllerId)
    {
        newControllerId = string.Empty;
        lock (_gate)
        {
            if (_data.IsMaster ||
                !string.Equals(
                    _data.ControllerId,
                    duplicatedControllerId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            newControllerId = Guid.NewGuid().ToString("N");
            _data.ControllerId = newControllerId;
            SaveLocked();
            ControllerLog.Write(
                $"Replaced a duplicated controller identity shared with " +
                $"{Clean(remoteMachineName, "another PC", 200)}. New controller ID: " +
                newControllerId + ".");
            return true;
        }
    }

    public void RememberMasterController(
        string controllerId,
        string machineName,
        string controllerAddress,
        string pairingKey,
        string peerAccessKey)
    {
        if (!Guid.TryParseExact(controllerId, "N", out _) ||
            string.IsNullOrWhiteSpace(machineName) ||
            string.IsNullOrWhiteSpace(controllerAddress) ||
            string.IsNullOrWhiteSpace(pairingKey) ||
            pairingKey.Length is < 16 or > 1_000 ||
            string.IsNullOrWhiteSpace(peerAccessKey) ||
            peerAccessKey.Length is < 16 or > 1_000)
        {
            return;
        }

        lock (_gate)
        {
            if (_data.IsMaster)
                return;
            var normalized = new StoredMasterControllerConnection
            {
                ControllerId = controllerId,
                MachineName = Clean(machineName, "Master Controller", 200),
                LastKnownAddress = Clean(controllerAddress, string.Empty, 300),
                PairingKey = pairingKey.Trim(),
                PeerAccessKey = peerAccessKey.Trim(),
                LastVerifiedUtc = DateTime.UtcNow
            };
            var existing = _data.MasterController;
            var changed = existing is null ||
                          !string.Equals(existing.ControllerId, normalized.ControllerId, StringComparison.Ordinal) ||
                          !string.Equals(existing.MachineName, normalized.MachineName, StringComparison.Ordinal) ||
                          !string.Equals(existing.LastKnownAddress, normalized.LastKnownAddress, StringComparison.OrdinalIgnoreCase) ||
                          !string.Equals(existing.PairingKey, normalized.PairingKey, StringComparison.Ordinal) ||
                          !string.Equals(existing.PeerAccessKey, normalized.PeerAccessKey, StringComparison.Ordinal);
            _data.MasterController = normalized;
            if (changed)
            {
                SaveLocked();
                ControllerLog.Write(
                    $"Saved master controller {normalized.MachineName} at {normalized.LastKnownAddress}.");
            }
        }
    }

    public void SetMaster(bool isMaster, string reason)
    {
        lock (_gate)
        {
            if (_data.IsMaster == isMaster)
                return;
            _data.IsMaster = isMaster;
            _data.MasterSinceUtc = isMaster ? DateTime.UtcNow : null;
            if (isMaster)
            {
                _data.MasterController = null;
                if (!_data.MasterPriority.Any(entry =>
                        string.Equals(entry.ControllerId, _data.ControllerId, StringComparison.Ordinal)))
                {
                    _data.MasterPriority.Insert(0, new MasterPriorityEntry
                    {
                        ControllerId = _data.ControllerId,
                        MachineName = Environment.MachineName
                    });
                }
            }
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
                IncludesClosureSettings = true,
                ShowClosedVideo = _data.BusinessHours.ShowClosedVideo,
                BlackoutAtClosingTime = _data.BusinessHours.BlackoutAtClosingTime,
                ClosedMessageMinutes = _data.BusinessHours.ClosedMessageMinutes,
                PreOpeningScreensaverMinutes = _data.BusinessHours.PreOpeningScreensaverMinutes,
                IncludesAppearanceSettings = true,
                ThemeMode = (int)_data.BusinessHours.ThemeMode,
                ScheduledDarkEnabled = _data.BusinessHours.ScheduledDarkEnabled,
                ScheduledDarkDays = _data.BusinessHours.ScheduledDarkDays
                    .Select(day => (int)day).ToArray(),
                ScheduledDarkTimes = _data.BusinessHours.ScheduledDarkTimes.ToArray(),
                ScheduledDarkTime = _data.BusinessHours.ScheduledDarkTime,
                Days = _data.BusinessHours.Days.Select(day => new BusinessHoursSyncItem
                {
                    Day = (int)day.Day, IsOpen = day.IsOpen,
                    OpenTime = day.OpenTime,
                    LastJumpTimeSold = day.LastJumpTimeSold,
                    CloseTime = day.CloseTime
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

    public ControllerReplicaSnapshot CreateReplicaSnapshot()
    {
        lock (_gate)
        {
            var kiosks = _data.Kiosks
                .Select(kiosk => kiosk.Clone())
                .OrderBy(kiosk => kiosk.StationId, StringComparer.Ordinal)
                .ToList();
            AdvertisementSyncPackage advertisements;
            try
            {
                advertisements = CreateAdvertisementSyncPackage();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ControllerLog.Write(
                    "Skipped advertisement content in a controller replica: " + ex.Message);
                advertisements = new AdvertisementSyncPackage();
            }
            var businessHours = CreateBusinessHoursSyncPackage();
            var priority = _data.MasterPriority.Select(entry => entry.Clone()).ToList();
            var serialized = JsonSerializer.Serialize(new
            {
                kiosks,
                pairingKey = _data.PairingKey,
                advertisementRevision = advertisements.Revision,
                businessHoursRevision = _data.BusinessHoursRevision,
                priority
            }, JsonOptions);
            return new ControllerReplicaSnapshot
            {
                MasterControllerId = _data.ControllerId,
                MasterMachineName = Environment.MachineName,
                PairingKey = _data.PairingKey,
                Revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized))),
                GeneratedUtc = DateTime.UtcNow,
                Kiosks = kiosks,
                Advertisements = advertisements,
                BusinessHours = businessHours,
                MasterPriority = priority
            };
        }
    }

    public StoredConnectionsResult ReloadStoredMasterConnections()
    {
        lock (_gate)
        {
            if (!_data.IsMaster)
            {
                return new StoredConnectionsResult(
                    false,
                    "Only the master controller can reload the connection catalog stored on this PC.",
                    0);
            }
            if (!File.Exists(_masterConnectionsPath))
            {
                return new StoredConnectionsResult(
                    false,
                    "No stored master connection catalog exists on this PC yet.",
                    0);
            }

            try
            {
                var stored = JsonSerializer.Deserialize<StoredControllerConnections>(
                    File.ReadAllText(_masterConnectionsPath),
                    JsonOptions);
                if (stored is null ||
                    stored.SchemaVersion != 1 ||
                    !Guid.TryParseExact(stored.ControllerId, "N", out _) ||
                    string.IsNullOrWhiteSpace(stored.PairingKey) ||
                    stored.PairingKey.Length is < 16 or > 1_000 ||
                    string.IsNullOrWhiteSpace(stored.PeerAccessKey) ||
                    stored.PeerAccessKey.Length is < 16 or > 1_000 ||
                    stored.Kiosks is null ||
                    stored.Kiosks.Count > 100)
                {
                    throw new InvalidDataException("The stored master connection catalog is invalid.");
                }

                var kiosks = stored.Kiosks
                    .Where(kiosk => kiosk is not null &&
                                    Guid.TryParseExact(kiosk.StationId, "N", out _))
                    .GroupBy(kiosk => kiosk.StationId, StringComparer.Ordinal)
                    .Select(group => group.Last().Clone())
                    .ToList();
                if (kiosks.Count != stored.Kiosks.Count)
                    throw new InvalidDataException("The stored master connection catalog is invalid.");
                foreach (var kiosk in kiosks)
                    kiosk.PendingCommand = null;

                _data.ControllerId = stored.ControllerId;
                _data.PairingKey = stored.PairingKey.Trim();
                _data.PeerAccessKey = stored.PeerAccessKey.Trim();
                _data.Kiosks = kiosks;
                _lastMasterReplicaRevision = string.Empty;
                SaveLocked();
                ControllerLog.Write(
                    $"Reloaded {kiosks.Count} kiosk connection(s) from {_masterConnectionsPath}.");
                return new StoredConnectionsResult(
                    true,
                    $"Reloaded {kiosks.Count} stored kiosk connection(s) from this master PC.",
                    kiosks.Count);
            }
            catch (Exception ex)
            {
                ControllerLog.Write("Stored master connection reload error: " + ex.Message);
                return new StoredConnectionsResult(
                    false,
                    "The stored master connection catalog could not be read. " +
                    "Check the controller log for details.",
                    0);
            }
        }
    }

    public bool ApplyMasterReplica(ControllerReplicaSnapshot replica)
    {
        lock (_gate)
        {
            if (_data.IsMaster ||
                string.IsNullOrWhiteSpace(replica.Revision) ||
                string.IsNullOrWhiteSpace(replica.PairingKey) ||
                replica.PairingKey.Length is < 16 or > 1_000 ||
                string.Equals(
                    _lastMasterReplicaRevision,
                    replica.Revision,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var kiosks = (replica.Kiosks ?? [])
                .Where(kiosk => kiosk is not null &&
                                Guid.TryParseExact(kiosk.StationId, "N", out _))
                .GroupBy(kiosk => kiosk.StationId, StringComparer.Ordinal)
                .Select(group => group.Last().Clone())
                .Take(100)
                .ToList();
            foreach (var kiosk in kiosks)
                kiosk.PendingCommand = null;

            _data.Kiosks = kiosks;
            _data.PairingKey = replica.PairingKey.Trim();
            _data.MasterPriority = NormalizeMasterPriority(replica.MasterPriority ?? []);
            if (!string.IsNullOrWhiteSpace(replica.Advertisements?.Revision) &&
                !string.Equals(
                    replica.Advertisements.Revision,
                    _data.AdvertisementRevision,
                    StringComparison.Ordinal))
            {
                ApplyCloudAdvertisements(replica.Advertisements, replica.GeneratedUtc);
            }
            if (!string.IsNullOrWhiteSpace(replica.BusinessHours?.Revision) &&
                !string.Equals(
                    replica.BusinessHours.Revision,
                    _data.BusinessHoursRevision,
                    StringComparison.Ordinal))
            {
                ApplyCloudBusinessHours(replica.BusinessHours, replica.GeneratedUtc);
            }
            _lastMasterReplicaRevision = replica.Revision;
            SaveLocked();
            ControllerLog.Write(
                $"Loaded {kiosks.Count} saved kiosk connection(s) from master controller " +
                $"{replica.MasterMachineName}.");
            return true;
        }
    }

    public void RecordPosMachine(string? machineName, string? ipAddress, string? version)
    {
        lock (_gate)
        {
            RemoveStalePosMachinesLocked();
            var cleanedIp = Clean(ipAddress, "Unknown address", 80);
            var cleanedName = Clean(
                machineName,
                cleanedIp == "Unknown address" ? "Mullet Hop POS" : $"POS at {cleanedIp}",
                80);
            var key = cleanedName + "|" + cleanedIp;
            _posMachines[key] = new PosMachinePresence
            {
                MachineName = cleanedName,
                IpAddress = cleanedIp,
                Version = Clean(version, "Unknown", 40),
                LastSeenUtc = DateTime.UtcNow
            };
        }
    }

    public IReadOnlyList<PosMachinePresence> ActivePosMachinesSnapshot()
    {
        lock (_gate)
        {
            RemoveStalePosMachinesLocked();
            return _posMachines.Values
                .Select(machine => machine.Clone())
                .OrderBy(machine => machine.MachineName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(machine => machine.IpAddress, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<string> ActivePosMachineNames()
    {
        lock (_gate)
        {
            RemoveStalePosMachinesLocked();
            return _posMachines.Values
                .Select(machine => machine.MachineName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public void QueueControllerUpdate(string controllerId)
    {
        if (!Guid.TryParseExact(controllerId, "N", out _))
            return;
        lock (_gate)
            _pendingControllerUpdates.Add(controllerId);
    }

    public bool TakeControllerUpdate(string controllerId)
    {
        lock (_gate)
            return _pendingControllerUpdates.Remove(controllerId);
    }

    public void QueuePosUpdate(string machineName)
    {
        var cleanedName = Clean(machineName, string.Empty, 80);
        if (string.IsNullOrWhiteSpace(cleanedName))
            return;
        lock (_gate)
            _pendingPosUpdates.Add(cleanedName);
    }

    public bool TakePosUpdate(string? machineName)
    {
        var cleanedName = Clean(machineName, string.Empty, 80);
        if (string.IsNullOrWhiteSpace(cleanedName))
            return false;
        lock (_gate)
            return _pendingPosUpdates.Remove(cleanedName);
    }

    public IReadOnlyList<string> TakePosUpdates(IEnumerable<string> machineNames)
    {
        lock (_gate)
        {
            var matches = machineNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => _pendingPosUpdates.Remove(name))
                .ToList();
            return matches;
        }
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
                    BusinessHoursClosed = kiosk.BusinessHoursClosed,
                    AvailableForGuests = kiosk.AvailableForGuests,
                    HasError = kiosk.HasError,
                    AssistanceRequested = kiosk.AssistanceRequested,
                    AssistanceAcknowledged = kiosk.AssistanceAcknowledged,
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
            kiosk.BusinessHoursClosed = request.BusinessHoursClosed;
            kiosk.AvailableForGuests = request.AvailableForGuests;
            kiosk.HasError = request.HasError;
            kiosk.AssistanceRequested = request.AssistanceRequested;
            kiosk.AssistanceAcknowledged = request.AssistanceRequested && request.AssistanceAcknowledged;
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
                ShowClosedVideo = package.IncludesClosureSettings
                    ? package.ShowClosedVideo
                    : _data.BusinessHours.ShowClosedVideo,
                BlackoutAtClosingTime = package.IncludesClosureSettings
                    ? package.BlackoutAtClosingTime
                    : _data.BusinessHours.BlackoutAtClosingTime,
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
                ScheduledDarkTimes = package.IncludesAppearanceSettings
                    ? package.ScheduledDarkTimes?.Length == 7
                        ? package.ScheduledDarkTimes.ToArray()
                        : Enumerable.Repeat(package.ScheduledDarkTime, 7).ToArray()
                    : _data.BusinessHours.ScheduledDarkTimes.ToArray(),
                ScheduledDarkTime = package.IncludesAppearanceSettings
                    ? package.ScheduledDarkTime
                    : _data.BusinessHours.ScheduledDarkTime,
                Days = package.Days.Select(item => new ControllerBusinessDayHours
                {
                    Day = item.Day is >= 0 and <= 6 ? (DayOfWeek)item.Day : DayOfWeek.Monday,
                    IsOpen = item.IsOpen,
                    OpenTime = item.OpenTime,
                    LastJumpTimeSold = package.IncludesClosureSettings
                        ? item.LastJumpTimeSold
                        : item.CloseTime,
                    CloseTime = item.CloseTime
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
            data.MasterController = NormalizeStoredMasterController(data.MasterController);
            data.MasterPriority = NormalizeMasterPriority(data.MasterPriority ?? []);
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
            if (_data.IsMaster)
                SaveMasterConnectionsLocked();
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller data save error: " + ex.Message);
        }
    }

    private void SaveMasterConnectionsLocked()
    {
        try
        {
            var kiosks = _data.Kiosks.Select(kiosk => kiosk.Clone()).ToList();
            foreach (var kiosk in kiosks)
                kiosk.PendingCommand = null;
            var stored = new StoredControllerConnections
            {
                ControllerId = _data.ControllerId,
                ControllerMachineName = Environment.MachineName,
                PairingKey = _data.PairingKey,
                PeerAccessKey = _data.PeerAccessKey,
                SavedUtc = DateTime.UtcNow,
                Kiosks = kiosks
            };
            var temporaryPath = _masterConnectionsPath + ".new";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(stored, JsonOptions));
            File.Move(temporaryPath, _masterConnectionsPath, true);
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Master connection catalog save error: " + ex.Message);
        }
    }

    private void RemoveStalePosMachinesLocked()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-20);
        foreach (var key in _posMachines
                     .Where(pair => pair.Value.LastSeenUtc < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _posMachines.Remove(key);
        }
    }

    private static StoredMasterControllerConnection? NormalizeStoredMasterController(
        StoredMasterControllerConnection? stored)
    {
        if (stored is null ||
            !Guid.TryParseExact(stored.ControllerId, "N", out _) ||
            string.IsNullOrWhiteSpace(stored.MachineName) ||
            stored.MachineName.Length > 200 ||
            string.IsNullOrWhiteSpace(stored.LastKnownAddress) ||
            stored.LastKnownAddress.Length > 300 ||
            string.IsNullOrWhiteSpace(stored.PairingKey) ||
            stored.PairingKey.Length is < 16 or > 1_000 ||
            string.IsNullOrWhiteSpace(stored.PeerAccessKey) ||
            stored.PeerAccessKey.Length is < 16 or > 1_000)
        {
            return null;
        }

        stored.ControllerId = stored.ControllerId.Trim();
        stored.MachineName = stored.MachineName.Trim();
        stored.LastKnownAddress = stored.LastKnownAddress.Trim();
        stored.PairingKey = stored.PairingKey.Trim();
        stored.PeerAccessKey = stored.PeerAccessKey.Trim();
        return stored;
    }

    private static List<MasterPriorityEntry> NormalizeMasterPriority(
        IEnumerable<MasterPriorityEntry> entries) =>
        entries
            .Where(entry => entry is not null &&
                            Guid.TryParseExact(entry.ControllerId, "N", out _))
            .Select(entry => new MasterPriorityEntry
            {
                ControllerId = entry.ControllerId.Trim(),
                MachineName = Clean(entry.MachineName, "Systems Controller", 200),
                LastKnownAddress = Clean(entry.LastKnownAddress, string.Empty, 300)
            })
            .GroupBy(entry => entry.ControllerId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(32)
            .ToList();

    private static string DescribeQueuedCommand(string type, bool? closed) => type switch
    {
        CommandTypes.SetClosed when closed == true => "Close-screen command queued.",
        CommandTypes.SetClosed => "Open-kiosk command queued.",
        CommandTypes.SetBusinessClosed when closed == true => "Business-closure command queued.",
        CommandTypes.SetBusinessClosed => "End-business-closure command queued.",
        CommandTypes.ResetStart => "Reset-to-start command queued.",
        CommandTypes.CheckUpdate => "Update check queued.",
        CommandTypes.InstallUpdate => "Update installation queued.",
        CommandTypes.SyncBusinessHours => "Business Hours sync queued.",
        CommandTypes.AcknowledgeAssistance => "Assistance response queued.",
        _ => "Command queued."
    };

    private static string Clean(string? value, string fallback, int maxLength)
    {
        value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
