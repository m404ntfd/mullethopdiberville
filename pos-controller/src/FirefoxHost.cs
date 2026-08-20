using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MulletHopPosController;

internal sealed class FirefoxHost : IDisposable
{
    public const string HomePage = "https://mullet.lilypadpos.app/public/Login.php";

    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsSystemMenu = 0x00080000L;
    private const long WsChild = 0x40000000L;
    private const long WsVisible = 0x10000000L;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    private readonly Control _host;
    private readonly System.Windows.Forms.Timer _windowTimer = new() { Interval = 500 };
    private Process? _process;
    private Process? _windowProcess;
    private IntPtr _firefoxWindow;
    private DateTime _startedUtc;
    private int _attachAttempts;
    private bool _disposed;

    public event EventHandler<string>? StatusChanged;

    public FirefoxHost(Control host)
    {
        _host = host;
        _host.Resize += (_, _) => ResizeEmbeddedWindow();
        _windowTimer.Tick += (_, _) => FindAndAttachWindow();
    }

    public void Start()
    {
        if (_disposed || (_process is not null && !_process.HasExited))
            return;

        var firefoxPath = FindFirefoxPath();
        if (firefoxPath is null)
        {
            SetStatus("Firefox is not installed. Install Mozilla Firefox, then restart the Kiosk Status Viewer.");
            return;
        }

        try
        {
            var profilePath = PrepareFirefoxProfile();
            var startInfo = new ProcessStartInfo(firefoxPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(firefoxPath) ?? string.Empty
            };
            startInfo.ArgumentList.Add("--no-remote");
            startInfo.ArgumentList.Add("--new-instance");
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add(profilePath);
            startInfo.ArgumentList.Add("--kiosk");
            startInfo.ArgumentList.Add(HomePage);

            _startedUtc = DateTime.UtcNow;
            _attachAttempts = 0;
            _firefoxWindow = IntPtr.Zero;
            _process = Process.Start(startInfo)
                       ?? throw new InvalidOperationException("Firefox did not start.");
            _windowTimer.Start();
            SetStatus("Starting Firefox and loading LilyPad POS…");
        }
        catch (Exception ex)
        {
            PosLog.Write("Firefox startup error: " + ex);
            SetStatus("Firefox could not start: " + ex.Message);
        }
    }

    public void ReloadHomePage()
    {
        if (_firefoxWindow == IntPtr.Zero || !IsWindow(_firefoxWindow))
        {
            Start();
            return;
        }

        SetForegroundWindow(_firefoxWindow);
        PostMessage(_firefoxWindow, 0x0100, (IntPtr)0x74, IntPtr.Zero);
        PostMessage(_firefoxWindow, 0x0101, (IntPtr)0x74, IntPtr.Zero);
        SetStatus("LilyPad POS is running in Firefox.");
    }

    public void ResizeToHost() => ResizeEmbeddedWindow();

    private void FindAndAttachWindow()
    {
        if (_disposed)
            return;

        if (_firefoxWindow != IntPtr.Zero && IsWindow(_firefoxWindow))
        {
            ResizeEmbeddedWindow();
            return;
        }

        _attachAttempts++;
        var window = GetFirefoxWindow();
        if (window != IntPtr.Zero)
        {
            AttachWindow(window);
            _windowTimer.Interval = 2000;
            SetStatus("LilyPad POS is running in Firefox.");
            return;
        }

        if (_attachAttempts >= 40)
        {
            _windowTimer.Stop();
            SetStatus("Firefox started, but its window could not be placed in the application. Use Reload LilyPad to retry.");
        }
    }

    private IntPtr GetFirefoxWindow()
    {
        if (_process is not null && !_process.HasExited)
        {
            _process.Refresh();
            if (_process.MainWindowHandle != IntPtr.Zero)
                return _process.MainWindowHandle;
        }

        foreach (var process in Process.GetProcessesByName("firefox"))
        {
            try
            {
                if (process.StartTime.ToUniversalTime() < _startedUtc.AddSeconds(-3))
                    continue;
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                    return process.MainWindowHandle;
            }
            catch
            {
                // A Firefox content process can exit while the process list is inspected.
            }
            finally
            {
                if (!ReferenceEquals(process, _process))
                    process.Dispose();
            }
        }
        return IntPtr.Zero;
    }

    private void AttachWindow(IntPtr window)
    {
        _firefoxWindow = window;
        GetWindowThreadProcessId(window, out var processId);
        if (processId != 0 && _process is not null && _process.Id == processId)
        {
            _process.EnableRaisingEvents = true;
            _process.Exited += FirefoxExited;
        }
        else if (processId != 0)
        {
            try
            {
                _windowProcess?.Dispose();
                _windowProcess = Process.GetProcessById(unchecked((int)processId));
                _windowProcess.EnableRaisingEvents = true;
                _windowProcess.Exited += FirefoxExited;
            }
            catch (Exception ex)
            {
                PosLog.Write("Firefox window process lookup error: " + ex.Message);
            }
        }
        var style = GetWindowStyle(window);
        style &= ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSystemMenu);
        style |= WsChild | WsVisible;
        SetWindowStyle(window, style);
        SetParent(window, _host.Handle);
        ResizeEmbeddedWindow();
    }

    private void ResizeEmbeddedWindow()
    {
        if (_firefoxWindow == IntPtr.Zero || !IsWindow(_firefoxWindow) || !_host.IsHandleCreated)
            return;

        SetWindowPos(
            _firefoxWindow,
            IntPtr.Zero,
            0,
            0,
            Math.Max(1, _host.ClientSize.Width),
            Math.Max(1, _host.ClientSize.Height),
            SwpFrameChanged | SwpShowWindow);
    }

    private void FirefoxExited(object? sender, EventArgs e)
    {
        if (_disposed || _host.IsDisposed)
            return;
        try
        {
            _host.BeginInvoke(new Action(() =>
            {
                _firefoxWindow = IntPtr.Zero;
                _windowTimer.Stop();
                SetStatus("Firefox was closed. Use Reload LilyPad to reopen it.");
            }));
        }
        catch
        {
            // The application is already shutting down.
        }
    }

    private static string PrepareFirefoxProfile()
    {
        var profilePath = Path.Combine(PosLog.DataDirectory, "FirefoxProfile");
        Directory.CreateDirectory(profilePath);
        var preferences = $$"""
            user_pref("browser.shell.checkDefaultBrowser", false);
            user_pref("browser.aboutwelcome.enabled", false);
            user_pref("browser.startup.homepage", "{{HomePage}}");
            user_pref("browser.startup.page", 1);
            user_pref("browser.tabs.warnOnClose", false);
            user_pref("browser.sessionstore.resume_from_crash", false);
            user_pref("datareporting.policy.dataSubmissionEnabled", false);
            user_pref("datareporting.healthreport.uploadEnabled", false);
            user_pref("toolkit.telemetry.reportingpolicy.firstRun", false);
            """;
        File.WriteAllText(Path.Combine(profilePath, "user.js"), preferences);
        return profilePath;
    }

    private static string? FindFirefoxPath()
    {
        var registryPaths = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32)
        };
        foreach (var (hive, view) in registryPaths)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe");
                var value = key?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(value) && File.Exists(value.Trim('"')))
                    return value.Trim('"');
            }
            catch
            {
                // Continue with the standard installation folders.
            }
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Mozilla Firefox", "firefox.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private void SetStatus(string status)
    {
        PosLog.Write(status);
        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _windowTimer.Stop();
        _windowTimer.Dispose();

        try
        {
            if (_firefoxWindow != IntPtr.Zero && IsWindow(_firefoxWindow))
                PostMessage(_firefoxWindow, 0x0010, IntPtr.Zero, IntPtr.Zero);

            var processes = new[] { _windowProcess, _process }
                .Where(process => process is not null)
                .Cast<Process>()
                .GroupBy(process => process.Id)
                .Select(group => group.First())
                .ToList();
            foreach (var process in processes)
            {
                process.Exited -= FirefoxExited;
                if (!process.HasExited && !process.WaitForExit(1500))
                    process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            PosLog.Write("Firefox shutdown error: " + ex.Message);
        }
        finally
        {
            _windowProcess?.Dispose();
            if (_process is not null && !ReferenceEquals(_process, _windowProcess))
                _process.Dispose();
        }
    }

    private static long GetWindowStyle(IntPtr window) => IntPtr.Size == 8
        ? GetWindowLongPtr64(window, GwlStyle).ToInt64()
        : GetWindowLong32(window, GwlStyle);

    private static void SetWindowStyle(IntPtr window, long style)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(window, GwlStyle, new IntPtr(style));
        else
            SetWindowLong32(window, GwlStyle, unchecked((int)style));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
}
