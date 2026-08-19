using System.Security.Cryptography;
using System.Text.Json;

namespace MulletHopPosController;

internal sealed class PosSettings
{
    public string ControllerUrl { get; set; } = string.Empty;
    public string PairingKey { get; set; } = string.Empty;
    public List<string> KioskSlots { get; set; } = [string.Empty, string.Empty, string.Empty, string.Empty];
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
                    "The saved POS Controller settings could not be read. A new settings passcode is required.",
                    "Mullet Hop POS Controller",
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
        StaffPinSalt = StaffPinSalt,
        StaffPinHash = StaffPinHash
    };

    public void CopyFrom(PosSettings source)
    {
        ControllerUrl = source.ControllerUrl;
        PairingKey = source.PairingKey;
        KioskSlots = [.. source.KioskSlots];
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

    private void Normalize()
    {
        ControllerUrl = (ControllerUrl ?? string.Empty).Trim();
        PairingKey = (PairingKey ?? string.Empty).Trim();
        KioskSlots ??= [];
        KioskSlots = KioskSlots.Take(4).Select(value => value?.Trim() ?? string.Empty).ToList();
        while (KioskSlots.Count < 4)
            KioskSlots.Add(string.Empty);
        StaffPinSalt ??= string.Empty;
        StaffPinHash ??= string.Empty;
    }

    private static byte[] DerivePinHash(string pin, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(pin, salt, 150_000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }
}
