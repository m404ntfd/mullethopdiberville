using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MulletHopWaiverKiosk;

internal enum KioskThemeMode
{
    Auto,
    Light,
    Dark
}

internal readonly record struct KioskThemeStatus(
    bool IsDark,
    bool ScheduledOverride,
    DateTime? ScheduledRelease,
    string Description);

internal static class KioskTheme
{
    private sealed record OriginalColors(Color BackColor, Color ForeColor);

    private static readonly ConditionalWeakTable<Control, OriginalColors> Original = new();
    private static readonly ConditionalWeakTable<Form, object> TitleBarHooks = new();

    public static KioskThemeStatus Evaluate(KioskSettings settings, DateTime now)
    {
        var windowsDark = WindowsUsesDarkApps();
        var baseDark = settings.ThemeMode == KioskThemeMode.Dark ||
                       (settings.ThemeMode == KioskThemeMode.Auto && windowsDark);
        if (baseDark)
        {
            var source = settings.ThemeMode == KioskThemeMode.Dark
                ? "Dark mode is selected."
                : "Auto is following Windows Dark mode.";
            return new KioskThemeStatus(true, false, null, source);
        }

        if (settings.ScheduledDarkEnabled && settings.ScheduledDarkDays.Length > 0)
        {
            for (var offset = 0; offset <= 7; offset++)
            {
                var date = now.Date.AddDays(-offset);
                if (!settings.ScheduledDarkDays.Contains(date.DayOfWeek)) continue;
                var start = date + settings.ScheduledDarkTime;
                if (start > now) continue;

                var release = FindFollowingBusinessOpening(settings, start);
                if (now < release)
                {
                    return new KioskThemeStatus(
                        true,
                        true,
                        release,
                        "Scheduled Dark mode is active until business opens " +
                        release.ToString("dddd 'at' h:mm tt") + ".");
                }
            }
        }

        var description = settings.ThemeMode == KioskThemeMode.Auto
            ? "Auto is following Windows Light mode."
            : "Light mode is selected.";
        return new KioskThemeStatus(false, false, null, description);
    }

    public static void Apply(Control root, bool dark)
    {
        ApplyControl(root, dark);
        if (root is Form form)
        {
            ApplyTitleBar(form, dark);
            if (!form.IsHandleCreated && !TitleBarHooks.TryGetValue(form, out _))
            {
                TitleBarHooks.Add(form, new object());
                form.HandleCreated += (_, _) => ApplyTitleBar(form, dark);
            }
        }
        root.Invalidate(true);
    }

    public static Color WindowBackground(bool dark) => dark
        ? Color.FromArgb(18, 24, 31)
        : Color.White;
    public static Color SurfaceBackground(bool dark) => dark
        ? Color.FromArgb(27, 36, 46)
        : Color.White;
    public static Color InputBackground(bool dark) => dark
        ? Color.FromArgb(37, 48, 60)
        : Color.White;
    public static Color PrimaryText(bool dark) => dark
        ? Color.FromArgb(235, 241, 246)
        : Color.FromArgb(16, 24, 32);
    public static Color MutedText(bool dark) => dark
        ? Color.FromArgb(177, 190, 201)
        : Color.FromArgb(83, 97, 109);
    public static Color SelectedNavigation(bool dark) => dark
        ? Color.FromArgb(45, 58, 72)
        : Color.FromArgb(238, 250, 255);
    public static Color Navigation(bool dark) => dark
        ? Color.FromArgb(29, 38, 48)
        : Color.FromArgb(247, 247, 247);

    private static DateTime FindFollowingBusinessOpening(KioskSettings settings, DateTime start)
    {
        var nextDay = start.Date.AddDays(1);
        for (var offset = 0; offset <= 7; offset++)
        {
            var date = nextDay.AddDays(offset);
            var schedule = settings.BusinessHours.First(item => item.Day == date.DayOfWeek);
            if (schedule.IsOpen) return date + schedule.OpenTime;
        }
        return nextDay + TimeSpan.FromHours(10);
    }

    private static void ApplyControl(Control control, bool dark)
    {
        var original = Original.GetValue(control,
            item => new OriginalColors(item.BackColor, item.ForeColor));
        if (!dark)
        {
            control.BackColor = original.BackColor;
            control.ForeColor = original.ForeColor;
        }
        else
        {
            switch (control)
            {
                case Form:
                    control.BackColor = WindowBackground(true);
                    control.ForeColor = PrimaryText(true);
                    break;
                case TabPage or GroupBox:
                    control.BackColor = SurfaceBackground(true);
                    control.ForeColor = MapText(original.ForeColor);
                    break;
                case TextBoxBase or ComboBox or NumericUpDown or DateTimePicker or ListBox or ListView:
                    control.BackColor = InputBackground(true);
                    control.ForeColor = PrimaryText(true);
                    break;
                case Button button:
                    if (IsLightSurface(original.BackColor))
                        button.BackColor = Color.FromArgb(51, 65, 78);
                    else
                        button.BackColor = original.BackColor;
                    button.ForeColor = IsDarkColor(button.BackColor)
                        ? Color.White
                        : Color.FromArgb(16, 24, 32);
                    button.FlatAppearance.BorderColor = Color.FromArgb(92, 108, 122);
                    break;
                case CheckBox or RadioButton:
                    control.BackColor = Color.Transparent;
                    control.ForeColor = MapText(original.ForeColor);
                    break;
                case Label:
                    if (original.BackColor != Color.Transparent && IsLightSurface(original.BackColor))
                        control.BackColor = SurfaceBackground(true);
                    control.ForeColor = MapText(original.ForeColor);
                    break;
                case PictureBox:
                    if (IsLightSurface(original.BackColor))
                        control.BackColor = InputBackground(true);
                    control.ForeColor = MapText(original.ForeColor);
                    break;
                default:
                    if (IsLightSurface(original.BackColor))
                        control.BackColor = SurfaceBackground(true);
                    control.ForeColor = MapText(original.ForeColor);
                    break;
            }
        }

        foreach (Control child in control.Controls)
            ApplyControl(child, dark);
    }

    private static Color MapText(Color color)
    {
        if (color == Color.White || color == Color.Transparent) return color;
        if (color.ToArgb() == Color.FromArgb(8, 119, 189).ToArgb())
            return Color.FromArgb(91, 198, 240);
        if (color.ToArgb() == Color.FromArgb(117, 68, 154).ToArgb())
            return Color.FromArgb(205, 153, 235);
        if (color.ToArgb() == Color.FromArgb(180, 35, 24).ToArgb())
            return Color.FromArgb(255, 119, 111);
        if (color.ToArgb() == Color.FromArgb(54, 128, 27).ToArgb())
            return Color.FromArgb(144, 220, 126);
        if (IsDarkColor(color)) return PrimaryText(true);
        return color;
    }

    private static bool IsLightSurface(Color color) =>
        color == Color.White ||
        color.ToArgb() == Color.FromArgb(238, 250, 255).ToArgb() ||
        color.ToArgb() == Color.FromArgb(247, 247, 247).ToArgb() ||
        color.ToArgb() == Color.FromArgb(247, 251, 253).ToArgb();

    private static bool IsDarkColor(Color color) =>
        color.A > 0 && (color.R * 299 + color.G * 587 + color.B * 114) / 1000 < 145;

    public static bool WindowsUsesDarkApps()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex)
        {
            KioskLog.Write("Windows theme read error: " + ex.Message);
            return false;
        }
    }

    private static void ApplyTitleBar(Form form, bool dark)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated) return;
        try
        {
            var enabled = dark ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
        }
        catch (Exception ex)
        {
            KioskLog.Write("Kiosk title-bar theme error: " + ex.Message);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle, int attribute, ref int value, int valueSize);
}
