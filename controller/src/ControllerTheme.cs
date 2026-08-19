using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace MulletHopKioskController;

internal enum ControllerThemeMode
{
    Auto,
    Light,
    Dark
}

internal sealed class ControllerThemeSettings
{
    public ControllerThemeMode Mode { get; set; } = ControllerThemeMode.Auto;
}

internal static class ControllerTheme
{
    private sealed record OriginalColors(Color BackColor, Color ForeColor);
    private sealed class NativeThemeState
    {
        public bool Dark { get; set; }
    }

    private static readonly ConditionalWeakTable<Control, OriginalColors> Original = new();
    private static readonly ConditionalWeakTable<Control, NativeThemeState> NativeThemeStates = new();
    private static readonly ConditionalWeakTable<Form, object> TitleBarHooks = new();
    private static readonly string SettingsPath = Path.Combine(
        ControllerLog.DataDirectory, "controller-theme.json");
    private static ControllerThemeMode _mode = LoadMode();

    public static ControllerThemeMode Mode => _mode;
    public static bool IsDark => _mode == ControllerThemeMode.Dark ||
                                 (_mode == ControllerThemeMode.Auto && WindowsUsesDarkApps());

    public static Color WindowBackground => IsDark
        ? Color.FromArgb(18, 24, 31)
        : Color.FromArgb(244, 248, 251);
    public static Color SurfaceBackground => IsDark
        ? Color.FromArgb(27, 36, 46)
        : Color.White;
    public static Color InputBackground => IsDark
        ? Color.FromArgb(37, 48, 60)
        : Color.White;
    public static Color PrimaryText => IsDark
        ? Color.FromArgb(235, 241, 246)
        : Color.FromArgb(16, 24, 32);
    public static Color MutedText => IsDark
        ? Color.FromArgb(177, 190, 201)
        : Color.FromArgb(52, 65, 76);
    public static Color OnlineRow => IsDark
        ? Color.FromArgb(28, 55, 39)
        : Color.White;
    public static Color ClosedRow => IsDark
        ? Color.FromArgb(66, 53, 27)
        : Color.FromArgb(255, 248, 231);
    public static Color OfflineRow => IsDark
        ? Color.FromArgb(66, 38, 39)
        : Color.FromArgb(255, 240, 237);
    public static Color OnlineText => IsDark
        ? Color.FromArgb(144, 220, 126)
        : Color.FromArgb(37, 103, 24);
    public static Color OfflineText => IsDark
        ? Color.FromArgb(255, 163, 154)
        : Color.FromArgb(125, 55, 48);
    public static Color AccentText => IsDark
        ? Color.FromArgb(91, 198, 240)
        : Color.FromArgb(8, 119, 189);
    public static Color SuccessText => IsDark
        ? Color.FromArgb(144, 220, 126)
        : Color.FromArgb(54, 128, 27);
    public static Color WarningText => IsDark
        ? Color.FromArgb(255, 190, 120)
        : Color.FromArgb(182, 76, 0);
    public static Color ErrorText => IsDark
        ? Color.FromArgb(255, 150, 143)
        : Color.FromArgb(180, 35, 24);

    public static void SetMode(ControllerThemeMode mode)
    {
        if (!Enum.IsDefined(mode)) mode = ControllerThemeMode.Auto;
        _mode = mode;
        try
        {
            Directory.CreateDirectory(ControllerLog.DataDirectory);
            var temporaryPath = SettingsPath + ".new";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
                new ControllerThemeSettings { Mode = mode },
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, SettingsPath, true);
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller theme save error: " + ex.Message);
        }
    }

    public static void Apply(Control root)
    {
        CaptureOriginalTree(root);
        ApplyControl(root);
        if (root is Form form)
        {
            ApplyTitleBar(form);
            if (!form.IsHandleCreated && !TitleBarHooks.TryGetValue(form, out _))
            {
                TitleBarHooks.Add(form, new object());
                form.HandleCreated += (_, _) => ApplyTitleBar(form);
            }
        }
        root.Invalidate(true);
    }

    private static void CaptureOriginalTree(Control control)
    {
        Original.GetValue(control, item => new OriginalColors(item.BackColor, item.ForeColor));
        foreach (Control child in control.Controls)
            CaptureOriginalTree(child);
    }

    private static void ApplyControl(Control control)
    {
        var original = Original.GetValue(control,
            item => new OriginalColors(item.BackColor, item.ForeColor));

        if (!IsDark)
        {
            control.BackColor = original.BackColor;
            control.ForeColor = original.ForeColor;
            if (control is DateTimePicker picker)
            {
                picker.CalendarMonthBackground = SystemColors.Window;
                picker.CalendarForeColor = SystemColors.WindowText;
                picker.CalendarTitleBackColor = SystemColors.ActiveCaption;
                picker.CalendarTitleForeColor = SystemColors.ActiveCaptionText;
            }
        }
        else
        {
            ApplyDarkColors(control, original);
        }

        EnsureReadableForeground(control);
        ApplyNativeControlTheme(control);

        foreach (Control child in control.Controls)
            ApplyControl(child);
    }

    private static void ApplyDarkColors(Control control, OriginalColors original)
    {
        switch (control)
        {
            case Form:
                control.BackColor = WindowBackground;
                control.ForeColor = PrimaryText;
                break;

            case DateTimePicker picker:
                picker.BackColor = InputBackground;
                picker.ForeColor = PrimaryText;
                picker.CalendarMonthBackground = InputBackground;
                picker.CalendarForeColor = PrimaryText;
                picker.CalendarTitleBackColor = SurfaceBackground;
                picker.CalendarTitleForeColor = PrimaryText;
                break;

            case TextBoxBase or ComboBox or NumericUpDown or ListBox or ListView:
                control.BackColor = InputBackground;
                control.ForeColor = PrimaryText;
                break;

            case TabPage:
                control.BackColor = SurfaceBackground;
                control.ForeColor = PrimaryText;
                break;

            case GroupBox:
                control.BackColor = SurfaceBackground;
                control.ForeColor = BrightAccent(original.ForeColor);
                break;

            case Button button:
                if (IsLightSurface(original.BackColor) || IsSystemButtonSurface(original.BackColor))
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
                control.ForeColor = MapTextColor(original.ForeColor);
                break;

            case LinkLabel link:
                link.BackColor = Color.Transparent;
                link.ForeColor = AccentText;
                link.LinkColor = AccentText;
                link.ActiveLinkColor = Color.FromArgb(255, 182, 110);
                link.VisitedLinkColor = Color.FromArgb(205, 153, 235);
                break;

            case PictureBox:
                if (IsLightSurface(original.BackColor))
                    control.BackColor = InputBackground;
                control.ForeColor = MapTextColor(original.ForeColor);
                break;

            case Label:
                if (original.BackColor != Color.Transparent && IsLightSurface(original.BackColor))
                    control.BackColor = SurfaceBackground;
                control.ForeColor = MapTextColor(original.ForeColor);
                break;

            case Panel or TableLayoutPanel or FlowLayoutPanel:
                if (IsLightSurface(original.BackColor))
                    control.BackColor = original.BackColor.ToArgb() == Color.White.ToArgb()
                        ? SurfaceBackground
                        : WindowBackground;
                control.ForeColor = MapTextColor(original.ForeColor);
                break;

            default:
                if (IsLightSurface(original.BackColor))
                    control.BackColor = SurfaceBackground;
                control.ForeColor = MapTextColor(original.ForeColor);
                break;
        }
    }

    private static Color MapTextColor(Color color)
    {
        if (color == Color.White || color == Color.Transparent) return color;
        if (color.ToArgb() == Color.FromArgb(8, 119, 189).ToArgb())
            return Color.FromArgb(91, 198, 240);
        if (color.ToArgb() == Color.FromArgb(117, 68, 154).ToArgb())
            return Color.FromArgb(205, 153, 235);
        if (color.ToArgb() == Color.FromArgb(196, 28, 28).ToArgb())
            return Color.FromArgb(255, 119, 111);
        if (color.ToArgb() == Color.FromArgb(54, 128, 27).ToArgb())
            return Color.FromArgb(144, 220, 126);
        if (color.ToArgb() == Color.FromArgb(245, 130, 32).ToArgb())
            return Color.FromArgb(255, 182, 110);
        if (IsDarkColor(color)) return PrimaryText;
        return color;
    }

    private static Color BrightAccent(Color color)
    {
        var mapped = MapTextColor(color);
        return mapped == PrimaryText ? Color.FromArgb(91, 198, 240) : mapped;
    }

    private static bool IsLightSurface(Color color) =>
        color == Color.White ||
        color.ToArgb() == SystemColors.Control.ToArgb() ||
        color.ToArgb() == SystemColors.ControlLight.ToArgb() ||
        color.ToArgb() == SystemColors.Window.ToArgb() ||
        color.ToArgb() == Color.FromArgb(244, 248, 251).ToArgb() ||
        color.ToArgb() == Color.FromArgb(247, 251, 253).ToArgb() ||
        color.ToArgb() == Color.FromArgb(238, 250, 255).ToArgb() ||
        color.ToArgb() == Color.FromArgb(235, 238, 241).ToArgb();

    private static bool IsSystemButtonSurface(Color color) =>
        color.ToArgb() == SystemColors.Control.ToArgb() || color.IsEmpty;

    private static bool IsDarkColor(Color color) =>
        color.A > 0 && (color.R * 299 + color.G * 587 + color.B * 114) / 1000 < 145;

    private static void EnsureReadableForeground(Control control)
    {
        if (control is PictureBox or ProgressBar || control.ForeColor == Color.Transparent) return;
        var background = EffectiveBackground(control);
        if (ContrastRatio(control.ForeColor, background) >= 4.5) return;
        control.ForeColor = IsDarkColor(background)
            ? Color.FromArgb(245, 248, 250)
            : Color.FromArgb(16, 24, 32);
    }

    private static Color EffectiveBackground(Control control)
    {
        for (Control? current = control; current is not null; current = current.Parent)
        {
            if (current.BackColor != Color.Transparent && current.BackColor.A > 0)
                return current.BackColor;
        }
        return IsDark ? WindowBackground : Color.White;
    }

    private static double ContrastRatio(Color foreground, Color background)
    {
        var lighter = Math.Max(RelativeLuminance(foreground), RelativeLuminance(background));
        var darker = Math.Min(RelativeLuminance(foreground), RelativeLuminance(background));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var component = value / 255d;
            return component <= 0.04045
                ? component / 12.92
                : Math.Pow((component + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static void ApplyNativeControlTheme(Control control)
    {
        if (control is not (TextBoxBase or ComboBox or NumericUpDown or DateTimePicker or
            ListBox or ListView or TabControl)) return;

        var state = NativeThemeStates.GetValue(control, item =>
        {
            var value = new NativeThemeState();
            item.HandleCreated += (_, _) => SetNativeTheme(item, value.Dark);
            return value;
        });
        state.Dark = IsDark;
        if (control.IsHandleCreated) SetNativeTheme(control, state.Dark);
    }

    private static void SetNativeTheme(Control control, bool dark)
    {
        if (!OperatingSystem.IsWindows() || !control.IsHandleCreated) return;
        try
        {
            SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Native control theme error: " + ex.Message);
        }
    }

    private static ControllerThemeMode LoadMode()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return ControllerThemeMode.Auto;
            var settings = JsonSerializer.Deserialize<ControllerThemeSettings>(
                File.ReadAllText(SettingsPath));
            return settings is not null && Enum.IsDefined(settings.Mode)
                ? settings.Mode
                : ControllerThemeMode.Auto;
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller theme read error: " + ex.Message);
            return ControllerThemeMode.Auto;
        }
    }

    private static bool WindowsUsesDarkApps()
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
            ControllerLog.Write("Windows theme read error: " + ex.Message);
            return false;
        }
    }

    private static void ApplyTitleBar(Form form)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated) return;
        try
        {
            var enabled = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller title-bar theme error: " + ex.Message);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle, int attribute, ref int value, int valueSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr windowHandle, string? subAppName, string? subIdList);
}
