using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    private readonly Control _host;
    private readonly System.Windows.Forms.Timer _windowTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _browserInputTimer = new() { Interval = 30 };
    private Process? _process;
    private Process? _windowProcess;
    private LilyPadCompatibilityBridge? _compatibilityBridge;
    private IntPtr _firefoxWindow;
    private Size _lastEmbeddedSize = Size.Empty;
    private DateTime _attachedUtc;
    private int _attachAttempts;
    private bool _crashReported;
    private bool _automaticRecoveryUsed;
    private bool _recoveryQueued;
    private bool _restarting;
    private bool _pointerWasDown;
    private bool _keyboardWasDown;
    private bool _browserInteractionPending;
    private bool _browserFocusPreferred = true;
    private DateTime _nextFocusGuardUtc = DateTime.MinValue;
    private DateTime _lastFocusFailureLoggedUtc = DateTime.MinValue;
    private DateTime _lastLayoutPulseUtc = DateTime.MinValue;
    private DateTime? _unhealthyPageSinceUtc;
    private bool _layoutRepairAttempted;
    private string? _lastKnownLilyPadUrl;
    private string? _activeWristbandDocumentKey;
    private bool _wristbandPromptRaised;
    private string? _nextLaunchUrl;
    private bool _disposed;

    private static string FirefoxProfilePath =>
        Path.Combine(PosLog.DataDirectory, "FirefoxProfile");

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? CrashDetected;
    public event EventHandler? BrowserInteractionStarted;
    public event EventHandler? BrowserInteractionCompleted;
    public event EventHandler<WristbandPrintRequestedEventArgs>? WristbandPrintRequested;

    public FirefoxHost(Control host)
    {
        _host = host;
        _host.Resize += (_, _) => ResizeEmbeddedWindow();
        _windowTimer.Tick += (_, _) => FindAndAttachWindow();
        _browserInputTimer.Tick += (_, _) => ObserveBrowserPointerActivity();
    }

    public void Start()
    {
        if (_disposed || (_process is not null && !_process.HasExited))
            return;

        var firefoxPath = FindFirefoxPath();
        if (firefoxPath is null)
        {
            SetStatus("Firefox is not installed. Install Mozilla Firefox, then restart Mullet Hop POS.");
            return;
        }

        try
        {
            var profilePath = FirefoxProfilePath;
            var profileRecovery = FirefoxProfileRecovery.PrepareForLaunch(profilePath);
            if (!profileRecovery.Success)
            {
                HandleFailure(
                    profileRecovery.Message +
                    " Select Refresh Lilypad to retry the profile cleanup and reopen LilyPad POS.");
                return;
            }

            var launchUrl = NormalizeLilyPadUrl(_nextLaunchUrl) ??
                            NormalizeLilyPadUrl(profileRecovery.RecoveryUrl) ??
                            HomePage;
            _nextLaunchUrl = null;
            _lastKnownLilyPadUrl = launchUrl;
            if (!string.Equals(launchUrl, HomePage, StringComparison.OrdinalIgnoreCase))
                PosLog.Write("Restoring the last healthy LilyPad page: " + launchUrl);

            ClearFirefoxSessionState(profilePath);
            PrepareFirefoxProfile(profilePath);
            var startInfo = new ProcessStartInfo(firefoxPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(firefoxPath) ?? string.Empty
            };
            startInfo.ArgumentList.Add("--no-remote");
            startInfo.ArgumentList.Add("--new-instance");
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add(profilePath);
            int? compatibilityPort = null;
            try
            {
                compatibilityPort = LilyPadCompatibilityBridge.AllocateLoopbackPort();
                startInfo.ArgumentList.Add("--remote-debugging-port");
                startInfo.ArgumentList.Add(compatibilityPort.Value.ToString());
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                PosLog.Write("Could not reserve the local LilyPad compatibility port: " + ex.Message);
            }
            startInfo.ArgumentList.Add("--new-window");
            startInfo.ArgumentList.Add(launchUrl);

            _attachAttempts = 0;
            _crashReported = false;
            _unhealthyPageSinceUtc = null;
            _layoutRepairAttempted = false;
            _activeWristbandDocumentKey = null;
            _wristbandPromptRaised = false;
            _lastLayoutPulseUtc = DateTime.MinValue;
            _firefoxWindow = IntPtr.Zero;
            _process = Process.Start(startInfo)
                       ?? throw new InvalidOperationException("Firefox did not start.");
            FirefoxProfileRecovery.RecordLaunchProcess(profilePath, _process, launchUrl);
            if (compatibilityPort.HasValue)
            {
                _compatibilityBridge = new LilyPadCompatibilityBridge(compatibilityPort.Value);
                _compatibilityBridge.PageHealthObserved += HandlePageHealthObserved;
                _compatibilityBridge.Start();
            }
            _windowTimer.Interval = 500;
            _windowTimer.Start();
            SetStatus("Starting Firefox and loading LilyPad POS…");
        }
        catch (Exception ex)
        {
            _compatibilityBridge?.Dispose();
            _compatibilityBridge = null;
            PosLog.Write("Firefox startup error: " + ex);
            HandleFailure(
                "Firefox could not start. Select Refresh Lilypad to try again.\n\n" + ex.Message);
        }
    }

    public void Restart()
    {
        _automaticRecoveryUsed = false;
        _lastKnownLilyPadUrl = HomePage;
        _nextLaunchUrl = null;
        RestartCore(preserveCurrentPage: false);
    }

    private void RestartCore(bool preserveCurrentPage)
    {
        if (_disposed)
            return;

        _nextLaunchUrl = preserveCurrentPage
            ? NormalizeLilyPadUrl(_lastKnownLilyPadUrl)
            : null;

        _restarting = true;
        _windowTimer.Stop();
        _browserInputTimer.Stop();
        _pointerWasDown = false;
        _keyboardWasDown = false;
        _browserInteractionPending = false;
        _crashReported = false;
        Exception? restartError = null;
        try
        {
            StopFirefoxProcesses();
        }
        catch (Exception ex)
        {
            restartError = ex;
        }
        finally
        {
            _firefoxWindow = IntPtr.Zero;
            _process = null;
            _windowProcess = null;
            _restarting = false;
        }
        if (restartError is not null)
        {
            PosLog.Write("Firefox restart error: " + restartError.Message);
            ReportFinalFailure(
                "Firefox could not be restarted. Select Refresh Lilypad to try again or restart Mullet Hop POS.");
            return;
        }
        Start();
    }

    public void ResizeToHost() => ResizeEmbeddedWindow();

    public bool FocusBrowser(string reason = "POS window activation") =>
        FocusEmbeddedWindow(reason);

    internal static uint WindowThreadIdForSmokeTest(IntPtr window) =>
        GetWindowThreadProcessId(window, out _);

    public void SetBrowserFocusPreferred(bool preferred)
    {
        _browserFocusPreferred = preferred;
        if (preferred)
            FocusEmbeddedWindow("browser mode enabled");
    }

    public Task<PrintDestinationSelectionResult> PrintCurrentPreviewAsync(
        string printerName,
        CancellationToken cancellationToken = default)
    {
        if (_firefoxWindow == IntPtr.Zero || !IsWindow(_firefoxWindow))
        {
            return Task.FromResult(PrintDestinationSelectionResult.Failed(
                "The embedded Firefox window is not available."));
        }

        return FirefoxPrintDestinationSelector.SelectAndPrintAsync(
            _firefoxWindow,
            printerName,
            cancellationToken);
    }

    public bool CancelPrintPreview()
    {
        if (_firefoxWindow == IntPtr.Zero || !IsWindow(_firefoxWindow))
            return false;

        _ = FocusEmbeddedWindow("cancel wristband print preview");
        var keyDownPosted = PostMessage(
            _firefoxWindow,
            WmKeyDown,
            new IntPtr(VkEscape),
            new IntPtr(0x00010001));
        var keyUpPosted = PostMessage(
            _firefoxWindow,
            WmKeyUp,
            new IntPtr(VkEscape),
            new IntPtr(unchecked((int)0xC0010001)));
        return keyDownPosted && keyUpPosted;
    }

    private void FindAndAttachWindow()
    {
        if (_disposed)
            return;

        if (_firefoxWindow != IntPtr.Zero && IsWindow(_firefoxWindow))
        {
            var now = DateTime.UtcNow;
            if (now - _attachedUtc < TimeSpan.FromSeconds(12) &&
                now - _lastLayoutPulseUtc >= TimeSpan.FromSeconds(2))
            {
                _lastLayoutPulseUtc = now;
                ResizeEmbeddedWindow(forceLayout: true);
            }
            if (_automaticRecoveryUsed && DateTime.UtcNow - _attachedUtc >= TimeSpan.FromSeconds(20))
            {
                _automaticRecoveryUsed = false;
                PosLog.Write("Firefox remained healthy after automatic recovery; crash retry is available again.");
            }
            if (IsHungAppWindow(_firefoxWindow) || WindowTitleIndicatesFailure())
            {
                HandleFailure(
                    "Firefox stopped responding. Select Refresh Lilypad to close it and reopen the LilyPad POS home page.");
                return;
            }
            DetectTabCrash();
            return;
        }

        _attachAttempts++;
        if (ProfileLockDialogIsVisible())
        {
            _windowTimer.Stop();
            PosLog.Write(
                "Firefox displayed its profile-lock dialog; automatic orphan cleanup was requested.");
            HandleFailure(
                "Firefox's dedicated Mullet Hop POS profile is locked by an older Firefox " +
                "process. Select Refresh Lilypad to terminate that POS Firefox session and retry.");
            return;
        }

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
            HandleFailure(
                "Firefox started, but its window could not be placed in the application. Select Refresh Lilypad to try again.");
        }
    }

    private IntPtr GetFirefoxWindow()
    {
        var processIds = GetCurrentLaunchFirefoxProcessIds();
        var candidates = new List<(IntPtr Window, long Area, bool LilyPadTitle)>();
        _ = EnumWindows((window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var processId);
            if (!processIds.Contains(processId) || !IsFirefoxBrowserWindow(window))
                return true;

            _ = TryGetWindowRectangle(window, out var rectangle);
            var title = GetWindowTitle(window);
            candidates.Add((
                window,
                Math.Max(0L, (long)rectangle.Width * rectangle.Height),
                title.Contains("LilyPad", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("mullet.lilypadpos.app", StringComparison.OrdinalIgnoreCase)));
            return true;
        }, IntPtr.Zero);

        var selected = candidates
            .OrderByDescending(candidate => candidate.LilyPadTitle)
            .ThenByDescending(candidate => candidate.Area)
            .FirstOrDefault();
        if (selected.Window != IntPtr.Zero)
            return selected.Window;

        if (_process is not null && !_process.HasExited)
        {
            _process.Refresh();
            if (_process.MainWindowHandle != IntPtr.Zero &&
                IsFirefoxBrowserWindow(_process.MainWindowHandle))
            {
                return _process.MainWindowHandle;
            }
        }

        return IntPtr.Zero;
    }

    private HashSet<uint> GetCurrentLaunchFirefoxProcessIds()
    {
        var processIds = new HashSet<uint>();
        if (_process is null)
            return processIds;

        try
        {
            foreach (var processId in FirefoxProfileRecovery.GetFirefoxProcessTreeIds(_process.Id))
                processIds.Add(unchecked((uint)processId));
        }
        catch (InvalidOperationException)
        {
            // The launcher exited while its Firefox descendants were being inspected.
        }
        return processIds;
    }

    private bool ProfileLockDialogIsVisible()
    {
        var processIds = GetCurrentLaunchFirefoxProcessIds();
        var found = false;
        _ = EnumWindows((window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var processId);
            if (!processIds.Contains(processId) || !IsWindowVisible(window))
                return true;
            found = ProfileLockDialogTitleIndicatesFailure(GetWindowTitle(window));
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsFirefoxBrowserWindow(IntPtr window)
    {
        if (!IsWindowVisible(window) ||
            GetWindow(window, GwOwner) != IntPtr.Zero ||
            !TryGetWindowRectangle(window, out _))
        {
            return false;
        }

        return GetWindowClassName(window).Contains(
                   "MozillaWindowClass",
                   StringComparison.OrdinalIgnoreCase) &&
               !ProfileLockDialogTitleIndicatesFailure(GetWindowTitle(window));
    }

    private static bool ProfileLockDialogTitleIndicatesFailure(string? title) =>
        !string.IsNullOrWhiteSpace(title) &&
        (title.Contains("Close Firefox", StringComparison.OrdinalIgnoreCase) ||
         title.Contains("Firefox is already running", StringComparison.OrdinalIgnoreCase));

    internal static bool ProfileLockDialogTitleIndicatesFailureForSmokeTest(string? title) =>
        ProfileLockDialogTitleIndicatesFailure(title);

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
        var windowProcess = processId != 0 && _process is not null && _process.Id == processId
            ? _process
            : _windowProcess;
        if (windowProcess is not null)
            FirefoxProfileRecovery.RecordWindowProcess(FirefoxProfilePath, windowProcess);
        var style = GetWindowStyle(window);
        style &= ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSystemMenu);
        style |= WsChild | WsVisible;
        SetWindowStyle(window, style);
        SetParent(window, _host.Handle);
        _ = SetWindowPos(
            window,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        _lastEmbeddedSize = Size.Empty;
        _attachedUtc = DateTime.UtcNow;
        PosLog.Write($"Embedding Firefox window '{GetWindowTitle(window)}'.");
        ResizeEmbeddedWindow(forceLayout: true);
        FocusEmbeddedWindow("Firefox attached");
        _browserInputTimer.Start();
        try
        {
            _host.BeginInvoke(new Action(() => FocusEmbeddedWindow("Firefox attach follow-up")));
        }
        catch (InvalidOperationException)
        {
            // The POS window is closing while Firefox finishes attaching.
        }
    }

    private void ResizeEmbeddedWindow(bool forceLayout = false)
    {
        if (_firefoxWindow == IntPtr.Zero || !IsWindow(_firefoxWindow) || !_host.IsHandleCreated)
            return;

        var size = new Size(
            Math.Max(1, _host.ClientSize.Width),
            Math.Max(1, _host.ClientSize.Height));
        if (!forceLayout && size == _lastEmbeddedSize)
            return;

        _ = SetWindowPos(
            _firefoxWindow,
            IntPtr.Zero,
            0,
            0,
            size.Width,
            size.Height,
            SwpNoZOrder | SwpNoActivate | SwpShowWindow);
        _lastEmbeddedSize = size;
        if (forceLayout)
        {
            _ = SendMessage(
                _firefoxWindow,
                WmSize,
                IntPtr.Zero,
                new IntPtr((size.Height << 16) | (size.Width & 0xFFFF)));
        }
    }

    private void HandlePageHealthObserved(LilyPadPageHealth health)
    {
        if (_disposed || _host.IsDisposed)
            return;
        var currentUrl = NormalizeLilyPadUrl(health.Url);
        if (currentUrl is not null)
        {
            _lastKnownLilyPadUrl = currentUrl;
            FirefoxProfileRecovery.RecordPageUrl(FirefoxProfilePath, currentUrl);
        }
        try
        {
            _host.BeginInvoke(new Action(() =>
            {
                EvaluatePageHealth(health);
                ObserveWristbandPrintPage(health);
            }));
        }
        catch (InvalidOperationException)
        {
            // The POS window is closing while the health result arrives.
        }
    }

    private void ObserveWristbandPrintPage(LilyPadPageHealth health)
    {
        var wristbandUrl = NormalizeLilyPadUrl(health.Url);
        if (!IsWristbandPrintUrl(wristbandUrl))
        {
            _activeWristbandDocumentKey = null;
            _wristbandPromptRaised = false;
            return;
        }

        var documentKey = $"{wristbandUrl}|{health.PageTimeOrigin:R}";
        if (!string.Equals(
                _activeWristbandDocumentKey,
                documentKey,
                StringComparison.OrdinalIgnoreCase))
        {
            _activeWristbandDocumentKey = documentKey;
            _wristbandPromptRaised = false;
        }

        if (_wristbandPromptRaised)
            return;

        _wristbandPromptRaised = true;
        PosLog.Write("A LilyPad wristband print page requested a wristband printer selection.");
        WristbandPrintRequested?.Invoke(
            this,
            new WristbandPrintRequestedEventArgs(wristbandUrl!));
    }

    private void EvaluatePageHealth(LilyPadPageHealth health)
    {
        if (_disposed || _restarting || _crashReported || _firefoxWindow == IntPtr.Zero)
            return;

        var issue = DescribePageHealthIssue(health, _host.ClientSize);
        if (issue is null)
        {
            _unhealthyPageSinceUtc = null;
            _layoutRepairAttempted = false;
            return;
        }

        var now = DateTime.UtcNow;
        _unhealthyPageSinceUtc ??= now;
        if (!_layoutRepairAttempted)
        {
            _layoutRepairAttempted = true;
            ResizeEmbeddedWindow(forceLayout: true);
            PosLog.Write("Firefox display health check requested a layout repair: " + issue);
            return;
        }

        if (now - _unhealthyPageSinceUtc < TimeSpan.FromSeconds(5))
            return;

        PosLog.Write("Firefox display health check failed after layout repair: " + issue);
        HandleFailure(
            "Firefox displayed an incomplete or collapsed LilyPad page. Select Refresh Lilypad " +
            "to close Firefox and reopen the LilyPad POS home page.");
    }

    private static string? DescribePageHealthIssue(LilyPadPageHealth health, Size hostSize)
    {
        if (!string.Equals(health.ReadyState, "complete", StringComparison.OrdinalIgnoreCase))
            return null;

        var minimumWidth = Math.Max(320, hostSize.Width / 3);
        var minimumHeight = Math.Max(240, hostSize.Height / 3);
        if (hostSize.Width >= 640 && hostSize.Height >= 480 &&
            (health.ViewportWidth < minimumWidth || health.ViewportHeight < minimumHeight))
        {
            return $"Firefox viewport {health.ViewportWidth}x{health.ViewportHeight} is smaller " +
                   $"than its {hostSize.Width}x{hostSize.Height} POS host.";
        }

        if (health.IsLoginPage && (!health.HasUsername || !health.HasPassword))
        {
            return "The completed LilyPad login page does not contain its username and password controls.";
        }

        if (health.BodyTextLength == 0 && !IsWristbandPrintUrl(health.Url))
            return "The completed LilyPad page has no visible content.";

        return null;
    }

    internal static bool PageHealthIndicatesFailureForSmokeTest(
        LilyPadPageHealth health,
        Size hostSize) => DescribePageHealthIssue(health, hostSize) is not null;

    internal static bool IsWristbandPrintUrlForSmokeTest(string? value) =>
        IsWristbandPrintUrl(value);

    private void FirefoxExited(object? sender, EventArgs e)
    {
        if (_disposed || _restarting || _host.IsDisposed)
            return;
        try
        {
            _host.BeginInvoke(new Action(() =>
            {
                _firefoxWindow = IntPtr.Zero;
                _windowTimer.Stop();
                _browserInputTimer.Stop();
                HandleFailure(
                    "Firefox has crashed. Select Refresh Lilypad to restart Firefox and reopen the LilyPad POS home page.");
            }));
        }
        catch
        {
            // The application is already shutting down.
        }
    }

    private void DetectTabCrash()
    {
        var length = GetWindowTextLength(_firefoxWindow);
        if (length <= 0)
            return;
        var title = new StringBuilder(length + 1);
        _ = GetWindowText(_firefoxWindow, title, title.Capacity);
        var value = title.ToString();
        if (value.Contains("tab crash", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("crash reporter", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("your tab just crashed", StringComparison.OrdinalIgnoreCase))
            HandleFailure(
                "The Firefox tab has crashed. Select Refresh Lilypad to restart Firefox and reopen the LilyPad POS home page.");
    }

    private bool WindowTitleIndicatesFailure()
    {
        var length = GetWindowTextLength(_firefoxWindow);
        if (length <= 0)
            return false;
        var title = new StringBuilder(length + 1);
        _ = GetWindowText(_firefoxWindow, title, title.Capacity);
        return title.ToString().Contains("not responding", StringComparison.OrdinalIgnoreCase);
    }

    private void HandleFailure(string finalMessage)
    {
        if (_disposed || _restarting || _crashReported || _recoveryQueued)
            return;

        if (_automaticRecoveryUsed)
        {
            ReportFinalFailure(finalMessage);
            return;
        }

        _automaticRecoveryUsed = true;
        _recoveryQueued = true;
        _windowTimer.Stop();
        SetStatus("Firefox encountered a problem. Closing it and reopening LilyPad POS once automatically…");
        PosLog.Write("Firefox automatic recovery attempt started.");
        try
        {
            _host.BeginInvoke(new Action(() =>
            {
                _recoveryQueued = false;
                RestartCore(preserveCurrentPage: true);
            }));
        }
        catch
        {
            _recoveryQueued = false;
            ReportFinalFailure(finalMessage);
        }
    }

    private void ReportFinalFailure(string message)
    {
        if (_crashReported)
            return;
        _crashReported = true;
        SetStatus(message);
        CrashDetected?.Invoke(this, message);
    }

    private void ObserveBrowserPointerActivity()
    {
        if (_disposed || _firefoxWindow == IntPtr.Zero || !IsWindow(_firefoxWindow) ||
            !_host.IsHandleCreated)
        {
            _pointerWasDown = false;
            _keyboardWasDown = false;
            _browserInteractionPending = false;
            return;
        }

        var pointerDown = IsPointerButtonDown();
        var keyboardDown = IsKeyboardKeyDown();
        if (pointerDown && !_pointerWasDown && IsPointerInsideBrowser())
        {
            _browserInteractionPending = true;
            FocusEmbeddedWindow("browser pointer input");
            BrowserInteractionStarted?.Invoke(this, EventArgs.Empty);
        }

        if (!pointerDown && _pointerWasDown && _browserInteractionPending)
        {
            _browserInteractionPending = false;
            BrowserInteractionCompleted?.Invoke(this, EventArgs.Empty);
            FocusEmbeddedWindow("browser pointer release");
        }

        _pointerWasDown = pointerDown;

        if (keyboardDown && !_keyboardWasDown &&
            FocusEmbeddedWindow("browser keyboard input probe", focusIfNeeded: false))
        {
            BrowserInteractionStarted?.Invoke(this, EventArgs.Empty);
            BrowserInteractionCompleted?.Invoke(this, EventArgs.Empty);
        }
        _keyboardWasDown = keyboardDown;

        var now = DateTime.UtcNow;
        if (_browserFocusPreferred && !pointerDown && now >= _nextFocusGuardUtc)
        {
            _nextFocusGuardUtc = now.AddMilliseconds(250);
            FocusEmbeddedWindow("background browser focus guard");
        }
    }

    private bool IsPointerInsideBrowser()
    {
        if (!GetCursorPos(out var cursor))
            return false;
        try
        {
            return _host.RectangleToScreen(_host.ClientRectangle).Contains(cursor);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsPointerButtonDown() =>
        IsKeyDown(VkLeftButton) ||
        IsKeyDown(VkRightButton) ||
        IsKeyDown(VkMiddleButton) ||
        IsKeyDown(VkXButton1) ||
        IsKeyDown(VkXButton2);

    private static bool IsKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsKeyboardKeyDown()
    {
        for (var virtualKey = 0x08; virtualKey <= 0xFE; virtualKey++)
        {
            if (IsKeyDown(virtualKey))
                return true;
        }
        return false;
    }

    private bool FocusEmbeddedWindow(string reason, bool focusIfNeeded = true)
    {
        if (_firefoxWindow == IntPtr.Zero || !IsWindow(_firefoxWindow))
            return false;

        var form = _host.FindForm();
        if (form is null || form.WindowState == FormWindowState.Minimized)
            return false;

        var foreground = GetForegroundWindow();
        var foregroundRoot = foreground == IntPtr.Zero
            ? IntPtr.Zero
            : GetAncestor(foreground, GaRoot);
        if (Form.ActiveForm != form &&
            foreground != form.Handle &&
            foregroundRoot != form.Handle)
            return false;

        try
        {
            var browserThread = GetWindowThreadProcessId(_firefoxWindow, out _);
            var formThread = GetWindowThreadProcessId(form.Handle, out _);
            if (browserThread == 0 || formThread == 0)
            {
                LogFocusFailure(reason, "Windows did not return a browser or POS UI thread ID.");
                return false;
            }

            var attached = browserThread == formThread ||
                           AttachThreadInput(formThread, browserThread, true);
            if (!attached)
            {
                LogFocusFailure(
                    reason,
                    $"Windows could not join POS thread {formThread} to Firefox thread {browserThread}; " +
                    $"error {Marshal.GetLastWin32Error()}.");
                return false;
            }
            try
            {
                var focusedWindow = GetFocus();
                if (focusedWindow == _firefoxWindow ||
                    (focusedWindow != IntPtr.Zero && IsChild(_firefoxWindow, focusedWindow)))
                {
                    return true;
                }
                if (!focusIfNeeded)
                    return false;

                _ = SetForegroundWindow(form.Handle);
                _ = SetFocus(_firefoxWindow);
                focusedWindow = GetFocus();
                var succeeded = focusedWindow == _firefoxWindow ||
                                (focusedWindow != IntPtr.Zero && IsChild(_firefoxWindow, focusedWindow));
                if (!succeeded)
                {
                    LogFocusFailure(
                        reason,
                        $"Firefox did not accept keyboard focus; error {Marshal.GetLastWin32Error()}.");
                }
                return succeeded;
            }
            finally
            {
                if (browserThread != formThread)
                    _ = AttachThreadInput(formThread, browserThread, false);
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            LogFocusFailure(reason, "Windows Firefox focus support is unavailable: " + ex.Message);
            return false;
        }
    }

    private void LogFocusFailure(string reason, string detail)
    {
        var now = DateTime.UtcNow;
        if (now - _lastFocusFailureLoggedUtc < TimeSpan.FromSeconds(15))
            return;
        _lastFocusFailureLoggedUtc = now;
        PosLog.Write($"Firefox keyboard focus recovery failed during {reason}: {detail}");
    }

    private static string? NormalizeLilyPadUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "mullet.lilypadpos.app", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return uri.AbsoluteUri;
    }

    private static bool IsWristbandPrintUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "mullet.lilypadpos.app", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(uri.AbsolutePath);
        return fileName.Contains("Wristband", StringComparison.OrdinalIgnoreCase) &&
               (fileName.Contains("Print", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("PDF", StringComparison.OrdinalIgnoreCase)) &&
               (fileName.EndsWith(".php", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
    }

    private static void PrepareFirefoxProfile(string profilePath)
    {
        Directory.CreateDirectory(profilePath);
        var preferences = $$"""
            user_pref("browser.shell.checkDefaultBrowser", false);
            user_pref("browser.aboutwelcome.enabled", false);
            user_pref("browser.startup.homepage", "{{HomePage}}");
            user_pref("browser.startup.page", 1);
            user_pref("browser.tabs.warnOnClose", false);
            user_pref("browser.sessionstore.resume_from_crash", false);
            user_pref("browser.sessionstore.max_resumed_crashes", 0);
            user_pref("javascript.enabled", true);
            user_pref("browser.cache.disk.enable", false);
            user_pref("browser.cache.offline.enable", false);
            user_pref("network.http.use-cache", false);
            user_pref("print.save_print_settings", false);
            user_pref("print_printer", "POS-X Thermal Printer");
            user_pref("datareporting.policy.dataSubmissionEnabled", false);
            user_pref("datareporting.healthreport.uploadEnabled", false);
            user_pref("toolkit.telemetry.reportingpolicy.firstRun", false);
            """;
        File.WriteAllText(Path.Combine(profilePath, "user.js"), preferences);
        EnsureFirefoxMenuBar(profilePath);
    }

    private static void ClearFirefoxSessionState(string profilePath)
    {
        try
        {
            foreach (var fileName in new[]
                     {
                         "sessionstore.jsonlz4",
                         "sessionCheckpoints.json"
                     })
            {
                var path = Path.Combine(profilePath, fileName);
                if (File.Exists(path))
                    File.Delete(path);
            }

            var backupsPath = Path.Combine(profilePath, "sessionstore-backups");
            if (Directory.Exists(backupsPath))
                Directory.Delete(backupsPath, recursive: true);

            PosLog.Write("Firefox tab and session state was cleared before reloading LilyPad POS.");
        }
        catch (Exception ex)
        {
            PosLog.Write("Firefox session reset error: " + ex.Message);
        }
    }

    private static void EnsureFirefoxMenuBar(string profilePath)
    {
        try
        {
            var path = Path.Combine(profilePath, "xulstore.json");
            var root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();
            foreach (var browserDocument in new[]
                     {
                         "chrome://browser/content/browser.xhtml",
                         "chrome://browser/content/browser.xul"
                     })
            {
                var browser = root[browserDocument] as JsonObject;
                if (browser is null)
                {
                    browser = new JsonObject();
                    root[browserDocument] = browser;
                }
                var menu = browser["toolbar-menubar"] as JsonObject;
                if (menu is null)
                {
                    menu = new JsonObject();
                    browser["toolbar-menubar"] = menu;
                }
                menu["autohide"] = "false";
                menu["collapsed"] = "false";
            }
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch (Exception ex)
        {
            PosLog.Write("Firefox menu-bar preference error: " + ex.Message);
        }
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
        _browserInputTimer.Stop();

        try { StopFirefoxProcesses(); }
        catch (Exception ex)
        {
            PosLog.Write("Firefox shutdown error: " + ex.Message);
        }
        finally
        {
            _windowProcess?.Dispose();
            if (_process is not null && !ReferenceEquals(_process, _windowProcess))
                _process.Dispose();
            _windowTimer.Dispose();
            _browserInputTimer.Dispose();
        }
    }

    private void StopFirefoxProcesses()
    {
        _browserInputTimer.Stop();
        _pointerWasDown = false;
        _keyboardWasDown = false;
        _browserInteractionPending = false;
        _activeWristbandDocumentKey = null;
        _wristbandPromptRaised = false;
        _compatibilityBridge?.Dispose();
        _compatibilityBridge = null;
        var processes = new[] { _windowProcess, _process }
            .Where(process => process is not null)
            .Cast<Process>()
            .GroupBy(process => process.Id)
            .Select(group => group.First())
            .ToList();
        foreach (var process in processes)
        {
            process.Exited -= FirefoxExited;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(3000);
            }
            process.Dispose();
        }
        FirefoxProfileRecovery.ForgetRecordedSession(FirefoxProfilePath);
        PosLog.Write("The complete embedded Firefox process tree was terminated.");
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

    private const uint WmSize = 0x0005;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint GwOwner = 4;

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Math.Max(0, Right - Left);
        public int Height => Math.Max(0, Bottom - Top);
    }

    private static bool TryGetWindowRectangle(IntPtr window, out NativeRectangle rectangle) =>
        GetWindowRect(window, out rectangle) && rectangle.Width > 0 && rectangle.Height > 0;

    private static string GetWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
            return string.Empty;
        var value = new StringBuilder(length + 1);
        _ = GetWindowText(window, value, value.Capacity);
        return value.ToString();
    }

    private static string GetWindowClassName(IntPtr window)
    {
        var value = new StringBuilder(256);
        _ = GetClassName(window, value, value.Capacity);
        return value.ToString();
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    private const uint GaRoot = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private const int VkLeftButton = 0x01;
    private const int VkRightButton = 0x02;
    private const int VkMiddleButton = 0x04;
    private const int VkXButton1 = 0x05;
    private const int VkXButton2 = 0x06;
    private const int VkEscape = 0x1B;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);
}
