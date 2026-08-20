using System.Security.Cryptography;
using System.Text.Json;

namespace MulletHopPosController;

internal sealed class PosSettings
{
    public string ControllerUrl { get; set; } = string.Empty;
    public string PairingKey { get; set; } = string.Empty;
    public List<string> KioskSlots { get; set; } = [string.Empty, string.Empty, string.Empty, string.Empty];
    public List<PosRememberedKiosk> RememberedKiosks { get; set; } = [];
    public string StaffPinSalt { get; set; } = string.Empty;
    public string StaffPinHash { get; set; } = string.Empty;

    public static string SettingsPath => Path.Combine(PosLog.DataDirectory, "settings.json");

    public static PosSettings? LoadOrCreate()
    {
        Directory.CreateDirectory(PosLog.DataDirectory);
        var settings = new PosSettings();
        if (File.Exists(SettingsPath))
        {
            try
            {
                settings = JsonSerializer.Deserialize<PosSettings>(File.ReadAllText(SettingsPath))
                           ?? new PosSettings();
                settings.Normalize();
            }
            catch (Exception ex)
            {
                PosLog.Write("Settings read error: " + ex.Message);
                MessageBox.Show(
                    "The saved Mullet Hop POS settings could not be read. A new settings passcode is required.",
                    "Mullet Hop POS",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                settings = new PosSettings();
            }
        }

        if (string.IsNullOrWhiteSpace(settings.StaffPinHash))
        {
            using var dialog = new PinSetupDialog();
            if (dialog.ShowDialog() != DialogResult.OK)
                return null;
            settings.SetPin(dialog.Pin);
            settings.Save();
        }

        return settings;
    }

    public PosSettings Clone() => new()
    {
        ControllerUrl = ControllerUrl,
        PairingKey = PairingKey,
        KioskSlots = [.. KioskSlots],
        RememberedKiosks = RememberedKiosks.Select(kiosk => kiosk.Clone()).ToList(),
        StaffPinSalt = StaffPinSalt,
        StaffPinHash = StaffPinHash
    };

    public void CopyFrom(PosSettings source)
    {
        ControllerUrl = source.ControllerUrl;
        PairingKey = source.PairingKey;
        KioskSlots = [.. source.KioskSlots];
        RememberedKiosks = source.RememberedKiosks.Select(kiosk => kiosk.Clone()).ToList();
        StaffPinSalt = source.StaffPinSalt;
        StaffPinHash = source.StaffPinHash;
        Normalize();
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(PosLog.DataDirectory);
        var temporaryPath = SettingsPath + ".new";
        File.WriteAllText(temporaryPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, SettingsPath, true);
    }

    public void SetPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        StaffPinSalt = Convert.ToBase64String(salt);
        StaffPinHash = Convert.ToBase64String(DerivePinHash(pin, salt));
    }

    public bool VerifyPin(string pin)
    {
        try
        {
            var salt = Convert.FromBase64String(StaffPinSalt);
            var expected = Convert.FromBase64String(StaffPinHash);
            return CryptographicOperations.FixedTimeEquals(DerivePinHash(pin, salt), expected);
        }
        catch
        {
            return false;
        }
    }

    public bool HasConnectionSettings =>
        Uri.TryCreate(ControllerUrl, UriKind.Absolute, out _) && PairingKey.Length >= 16;

    public int AutoAssignKiosks(IEnumerable<PosKioskStatus> kiosks)
    {
        Normalize();
        var assigned = KioskSlots
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var kiosk in kiosks
                     .Where(kiosk => !string.IsNullOrWhiteSpace(kiosk.StationId))
                     .OrderBy(kiosk => kiosk.StationName, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(kiosk => kiosk.MachineName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (assigned.Contains(kiosk.StationId))
                continue;
            var openSlot = KioskSlots.FindIndex(string.IsNullOrWhiteSpace);
            if (openSlot < 0)
                break;
            KioskSlots[openSlot] = kiosk.StationId;
            assigned.Add(kiosk.StationId);
            added++;
        }
        return added;
    }

    public int RememberSuccessfulConnection(
        string controllerUrl,
        string pairingKey,
        IEnumerable<PosKioskStatus> kiosks)
    {
        var normalizedUrl = controllerUrl.Trim();
        var normalizedKey = pairingKey.Trim();
        var changed = !string.Equals(ControllerUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase) ||
                      !string.Equals(PairingKey, normalizedKey, StringComparison.Ordinal);
        ControllerUrl = normalizedUrl;
        PairingKey = normalizedKey;
        var kioskList = kiosks
            .Where(kiosk => !string.IsNullOrWhiteSpace(kiosk.StationId))
            .ToList();
        changed |= RememberKiosks(kioskList);
        var added = AutoAssignKiosks(kioskList);
        if (changed || added > 0)
            Save();
        return added;
    }

    public IReadOnlyList<PosKioskStatus> RememberedKioskStatuses() =>
        RememberedKiosks
            .Select(kiosk => new PosKioskStatus
            {
                StationId = kiosk.StationId,
                StationName = kiosk.StationName,
                MachineName = kiosk.MachineName
            })
            .ToList();

    private bool RememberKiosks(IEnumerable<PosKioskStatus> kiosks)
    {
        var changed = false;
        foreach (var kiosk in kiosks)
        {
            var remembered = RememberedKiosks.FirstOrDefault(item =>
                string.Equals(item.StationId, kiosk.StationId, StringComparison.Ordinal));
            if (remembered is null)
            {
                remembered = new PosRememberedKiosk { StationId = kiosk.StationId };
                RememberedKiosks.Add(remembered);
                changed = true;
            }
            var stationName = CleanName(kiosk.StationName, kiosk.MachineName);
            var machineName = CleanName(kiosk.MachineName, kiosk.StationName);
            if (!string.Equals(remembered.StationName, stationName, StringComparison.Ordinal) ||
                !string.Equals(remembered.MachineName, machineName, StringComparison.Ordinal))
            {
                remembered.StationName = stationName;
                remembered.MachineName = machineName;
                changed = true;
            }
        }
        return changed;
    }

    private void Normalize()
    {
        ControllerUrl = (ControllerUrl ?? string.Empty).Trim();
        PairingKey = (PairingKey ?? string.Empty).Trim();
        KioskSlots ??= [];
        KioskSlots = KioskSlots.Take(4).Select(value => value?.Trim() ?? string.Empty).ToList();
        while (KioskSlots.Count < 4)
            KioskSlots.Add(string.Empty);
        RememberedKiosks ??= [];
        RememberedKiosks = RememberedKiosks
            .Where(kiosk => kiosk is not null && !string.IsNullOrWhiteSpace(kiosk.StationId))
            .GroupBy(kiosk => kiosk.StationId.Trim(), StringComparer.Ordinal)
            .Select(group =>
            {
                var kiosk = group.Last();
                kiosk.StationId = kiosk.StationId.Trim();
                kiosk.StationName = CleanName(kiosk.StationName, kiosk.MachineName);
                kiosk.MachineName = CleanName(kiosk.MachineName, kiosk.StationName);
                return kiosk;
            })
            .Take(100)
            .ToList();
        StaffPinSalt ??= string.Empty;
        StaffPinHash ??= string.Empty;
    }

    private static string CleanName(string? value, string? fallback)
    {
        var result = string.IsNullOrWhiteSpace(value) ? fallback : value;
        result = string.IsNullOrWhiteSpace(result) ? "Waiver Kiosk" : result.Trim();
        return result.Length <= 80 ? result : result[..80];
    }

    private static byte[] DerivePinHash(string pin, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(pin, salt, 150_000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }
}

internal sealed class PosRememberedKiosk
{
    public string StationId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;

    public PosRememberedKiosk Clone() => new()
    {
        StationId = StationId,
        StationName = StationName,
        MachineName = MachineName
    };
}
