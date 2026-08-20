using System.Runtime.InteropServices;
using System.Reflection;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MulletHop.KioskDiscovery;
using Velopack;
using Velopack.Sources;

namespace MulletHopWaiverKiosk;

internal static class Program
{
    private const string MutexName = "MulletHopWaiverKiosk.SingleInstance";

    [STAThread]
    private static void Main()
    {
        // Velopack must be the first application code that runs so install,
        // update, and uninstall hooks can complete without opening kiosk UI.
        VelopackApp.Build().Run();

        using var mutex = new Mutex(true, MutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show("The waiver kiosk is already running.", "Mullet Hop Waiver Kiosk",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        KioskUpdater.ApplyAvailableUpdateOnStartup();
        LegacyInstallationMigration.PreserveStartupPreference();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            var settings = KioskSettings.LoadOrCreate();
            if (settings is null)
                return;

            Application.Run(new KioskForm(settings));
        }
        catch (Exception ex)
        {
            KioskLog.Write("Fatal startup error: " + ex.GetType().Name + " - " + ex.Message);
            MessageBox.Show(
                "The waiver kiosk could not start.\n\n" + ex.Message +
                "\n\nSee README.txt for repair instructions.",
                "Mullet Hop Waiver Kiosk", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal enum KioskUpdateStatus
{
    UpToDate,
    Available,
    Applying,
    NotConfigured,
    NotInstalled,
    Failed
}

internal sealed record KioskUpdateResult(KioskUpdateStatus Status, string Message);

internal static class KioskUpdater
{
    private const string RepositoryMetadataKey = "UpdateRepositoryUrl";

    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version is null
                ? "Unknown"
                : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    private static string RepositoryUrl =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, RepositoryMetadataKey, StringComparison.Ordinal))?
            .Value?.Trim() ?? string.Empty;

    public static void ApplyAvailableUpdateOnStartup()
    {
        try
        {
            var result = CheckDownloadAndApplyAsync().GetAwaiter().GetResult();
            KioskLog.Write("Automatic update check: " + result.Message);
        }
        catch (Exception ex)
        {
            // An unavailable update service must never prevent guests from using
            // the waiver. Staff can retry from Staff Settings.
            KioskLog.Write("Automatic update check error: " +
                ex.GetType().Name + " - " + ex.Message);
        }
    }

    public static async Task<KioskUpdateResult> CheckForUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(RepositoryUrl))
        {
            return new KioskUpdateResult(
                KioskUpdateStatus.NotConfigured,
                "This build was not created by the GitHub release workflow.");
        }

        try
        {
            var manager = new UpdateManager(
                new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
            var update = await manager.CheckForUpdatesAsync();
            return update is null
                ? new KioskUpdateResult(
                    KioskUpdateStatus.UpToDate,
                    $"Version {CurrentVersion} is up to date.")
                : new KioskUpdateResult(
                    KioskUpdateStatus.Available,
                    $"Version {update.TargetFullRelease.Version} is available " +
                    $"for kiosk {Environment.MachineName}.");
        }
        catch (Exception ex) when (
            string.Equals(ex.GetType().Name, "NotInstalledException", StringComparison.Ordinal))
        {
            return new KioskUpdateResult(
                KioskUpdateStatus.NotInstalled,
                "Automatic updates begin after the kiosk is installed with the Velopack Setup file.");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Kiosk update check error: " + ex.GetType().Name + " - " + ex.Message);
            return new KioskUpdateResult(
                KioskUpdateStatus.Failed,
                "The update check failed. Verify the internet connection and try again.");
        }
    }

    public static async Task<KioskUpdateResult> CheckDownloadAndApplyAsync()
    {
        if (string.IsNullOrWhiteSpace(RepositoryUrl))
        {
            return new KioskUpdateResult(
                KioskUpdateStatus.NotConfigured,
                "This build was not created by the GitHub release workflow.");
        }

        try
        {
            var manager = new UpdateManager(
                new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                return new KioskUpdateResult(
                    KioskUpdateStatus.UpToDate,
                    $"Version {CurrentVersion} is up to date.");
            }

            await manager.DownloadUpdatesAsync(update);
            KioskLog.Write("A kiosk update was downloaded and is being applied.");
            manager.ApplyUpdatesAndRestart(update);
            return new KioskUpdateResult(
                KioskUpdateStatus.Applying,
                "The update is installing. The kiosk will restart automatically.");
        }
        catch (Exception ex) when (
            string.Equals(ex.GetType().Name, "NotInstalledException", StringComparison.Ordinal))
        {
            return new KioskUpdateResult(
                KioskUpdateStatus.NotInstalled,
                "Automatic updates begin after the kiosk is installed with the Velopack Setup file.");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Kiosk update error: " + ex.GetType().Name + " - " + ex.Message);
            return new KioskUpdateResult(
                KioskUpdateStatus.Failed,
                "The update check failed. Verify the internet connection and try again.");
        }
    }
}

internal static class LegacyInstallationMigration
{
    public static void PreserveStartupPreference()
    {
        try
        {
            var startupShortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "Mullet Hop Waiver Kiosk.lnk");
            var executablePath = Environment.ProcessPath;
            if (!File.Exists(startupShortcutPath) || string.IsNullOrWhiteSpace(executablePath))
                return;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return;

            dynamic shortcut = shell.CreateShortcut(startupShortcutPath);
            shortcut.TargetPath = executablePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;
            shortcut.Description = "Automatically start the Mullet Hop waiver kiosk";
            shortcut.Save();

            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
            KioskLog.Write("The existing Windows startup preference was migrated to the updateable kiosk.");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Startup shortcut migration error: " +
                ex.GetType().Name + " - " + ex.Message);
        }
    }
}

internal enum BusinessHoursMode
{
    Disabled,
    Open,
    Closed,
    PreOpening
}

internal readonly record struct BusinessHoursStatus(
    BusinessHoursMode Mode,
    DateTime? NextOpening,
    DateTime? CurrentClosing);

internal static class BusinessHoursCalculator
{
    public static DateTime? FindNextOpening(KioskSettings settings, DateTime now)
    {
        for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            var date = now.Date.AddDays(dayOffset);
            var schedule = settings.BusinessHours.First(item => item.Day == date.DayOfWeek);
            if (!schedule.IsOpen)
                continue;

            var candidate = date + schedule.OpenTime;
            if (candidate > now)
                return candidate;
        }

        return null;
    }

    public static BusinessHoursStatus Evaluate(KioskSettings settings, DateTime now)
    {
        if (!settings.BusinessHoursEnabled)
            return new BusinessHoursStatus(BusinessHoursMode.Disabled, null, null);

        var today = settings.BusinessHours.First(schedule => schedule.Day == now.DayOfWeek);
        if (today.IsOpen && now.TimeOfDay >= today.OpenTime && now.TimeOfDay < today.CloseTime)
        {
            return new BusinessHoursStatus(
                BusinessHoursMode.Open,
                null,
                now.Date + today.CloseTime);
        }

        var nextOpening = FindNextOpening(settings, now);

        if (nextOpening.HasValue && settings.PreOpeningScreensaverMinutes > 0 &&
            nextOpening.Value - now <=
                TimeSpan.FromMinutes(settings.PreOpeningScreensaverMinutes))
        {
            return new BusinessHoursStatus(
                BusinessHoursMode.PreOpening,
                nextOpening,
                null);
        }

        return new BusinessHoursStatus(BusinessHoursMode.Closed, nextOpening, null);
    }
}

internal sealed partial class KioskForm : Form
{
    private static readonly HttpClient ConnectionCheckClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private const int WmHotKey = 0x0312;
    private const int StaffExitHotKeyId = 0x4D48;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkM = 0x4D;
    private const string AdvertisementVirtualHost = "mullethop-ads.local";
    private const string ScreensaverVirtualHost = "mullethop-kiosk.local";
    private const string ScreensaverFileName = "MulletHopScreensaver.mp4";
    private const string KioskBackgroundFileName = "MulletHopKioskBackground.jpg";
    private const string LogoUrl =
        "https://www.coastalmississippi.com/imager/files_idss_com/C537/images/listings/Mullet-Hop-eea044b35056a36_e45adf5f6bc0c5c2a30a39868f44eab6.png";

    private readonly KioskSettings _settings;
    private readonly WebView2 _webView = new();
    private readonly string _assistanceBrowserSession = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _waiverPageScriptGate = new(1, 1);
    private readonly Label _banner = new();
    private readonly Label _previewBanner = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _completionTimer = new();
    private readonly System.Windows.Forms.Timer _retryTimer = new() { Interval = 60000 };
    private readonly System.Windows.Forms.Timer _businessHoursTimer = new() { Interval = 1000 };

    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _allowExit;
    private bool _promptOpen;
    private bool _isResetting;
    private bool _browserReady;
    private bool _hotKeyRegistered;
    private bool _showingThankYouPage;
    private bool _showingClosedPage;
    private bool _showingBusinessClosedPage;
    private bool _showingBlackout;
    private bool _manualBusinessBlackout;
    private bool _showingScreensaver;
    private bool _preOpeningScreensaverActive;
    private bool _idleResetPerformed;
    private bool _connectionCheckInProgress;
    private bool _businessHoursCheckInProgress;
    private string? _pendingSwitchEmail;
    private string? _pendingSwitchChoice;
    private string? _lastWaiverEmail;
    private string? _lastWaiverChoice;
    private DateTime? _previewDateTime;
    private DateTime? _previewStartedUtc;
    private string? _dateTimePreviewScriptId;
    private string? _waiverPageScriptId;
    private DateTime? _businessClosedPeriodStartedUtc;
    private string? _businessClosedPeriodKey;
    private long? _dismissedPreOpeningTicks;
    private DateTime? _preOpeningScreensaverOpeningTime;
    private bool _lastDarkTheme;

    public KioskForm(KioskSettings settings)
    {
        _settings = settings;
        _manualBusinessBlackout = _settings.ManualBusinessBlackout;
        _lastDarkTheme = KioskTheme.Evaluate(_settings, DateTime.Now).IsDark;

        Text = "Mullet Hop Waiver Kiosk";
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon is not null)
            Icon = appIcon;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        KeyPreview = true;
        BackColor = KioskTheme.WindowBackground(_lastDarkTheme);

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = KioskTheme.WindowBackground(_lastDarkTheme);

        _banner.Dock = DockStyle.Top;
        _banner.Height = 48;
        _banner.Padding = new Padding(14, 0, 14, 0);
        _banner.TextAlign = ContentAlignment.MiddleCenter;
        _banner.Font = new Font("Segoe UI", 13, FontStyle.Bold);
        _banner.BackColor = Color.FromArgb(255, 222, 89);
        _banner.ForeColor = Color.FromArgb(32, 32, 32);
        _banner.Visible = false;

        _previewBanner.Dock = DockStyle.Bottom;
        _previewBanner.Height = 44;
        _previewBanner.Padding = new Padding(14, 0, 14, 0);
        _previewBanner.TextAlign = ContentAlignment.MiddleCenter;
        _previewBanner.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        _previewBanner.BackColor = Color.FromArgb(117, 68, 154);
        _previewBanner.ForeColor = Color.White;
        _previewBanner.Visible = false;

        Controls.Add(_webView);
        Controls.Add(_banner);
        Controls.Add(_previewBanner);
        _banner.BringToFront();
        _previewBanner.BringToFront();
        UpdateAssistancePanelState();

        _idleTimer.Tick += IdleTimer_Tick;
        _completionTimer.Interval = Math.Max(12, _settings.CompletionResetSeconds) * 1000;
        _completionTimer.Tick += async (_, _) =>
        {
            _completionTimer.Stop();
            await ResetForNextGuestAsync("completion");
        };
        _retryTimer.Tick += RetryTimer_Tick;
        _businessHoursTimer.Tick += async (_, _) =>
        {
            await ApplyKioskThemeIfChangedAsync();
            await ApplyBusinessHoursStateAsync();
        };
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        InitializeRemoteManagement();

        Shown += async (_, _) =>
        {
            if (!_hotKeyRegistered)
            {
                _allowExit = true;
                MessageBox.Show(
                    "The staff settings shortcut could not be registered. Close any program using Ctrl + Alt + M, then start the kiosk again.",
                    "Mullet Hop Waiver Kiosk", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            Activate();
            await InitializeBrowserAsync();
        };

        Deactivate += (_, _) =>
        {
            if (!_promptOpen && !_allowExit)
                BeginInvoke(() => { TopMost = true; Activate(); _webView.Focus(); });
        };

        FormClosing += (_, e) =>
        {
            if (!_allowExit)
                e.Cancel = true;
        };
    }

    private void RequestGuestAssistance()
    {
        try
        {
            _settings.AssistanceRequested = true;
            _settings.AssistanceAcknowledged = false;
            _settings.Save();
            UpdateAssistanceStateAfterChange();
            MarkActivity();
            _ = CheckInWithControllerAsync();
            KioskLog.Write("A guest requested staff assistance.");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Assistance request save error: " + ex.Message);
            ShowBanner("The assistance request could not be saved. Please ask a staff member for help.", false);
        }
    }

    private void ClearGuestAssistance()
    {
        try
        {
            _settings.AssistanceRequested = false;
            _settings.AssistanceAcknowledged = false;
            _settings.Save();
            UpdateAssistanceStateAfterChange();
            MarkActivity();
            _ = CheckInWithControllerAsync();
            KioskLog.Write("The guest assistance call was cleared at the kiosk.");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Assistance clear save error: " + ex.Message);
            ShowBanner("The assistance call could not be cleared. Please try again.", false);
        }
    }

    private void UpdateAssistancePanelState()
    {
        if (!_browserReady || _webView.CoreWebView2 is null)
            return;

        var requested = _settings.AssistanceRequested ? "true" : "false";
        var acknowledged = _settings.AssistanceAcknowledged ? "true" : "false";
        _ = PushAssistanceStateToWaiverPageAsync(requested, acknowledged);
    }

    private void UpdateAssistanceStateAfterChange()
    {
        UpdateAssistancePanelState();
        if (_browserReady)
            _ = RefreshWaiverPageScriptForAssistanceAsync();
    }

    private async Task RefreshWaiverPageScriptForAssistanceAsync()
    {
        try
        {
            await InstallWaiverPageScriptAsync();
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
                KioskLog.Write("Future assistance card state update error: " + ex.Message);
        }
    }

    private async Task PushAssistanceStateToWaiverPageAsync(string requested, string acknowledged)
    {
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(
                $"window.__mulletHopSetAssistanceState?.({requested}, {acknowledged});");
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing)
                KioskLog.Write("Assistance card update error: " + ex.Message);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotKeyRegistered = RegisterHotKey(Handle, StaffExitHotKeyId,
            ModControl | ModAlt, VkM);

        if (!_hotKeyRegistered)
            KioskLog.Write("Unable to register the staff settings hotkey.");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_hotKeyRegistered)
            UnregisterHotKey(Handle, StaffExitHotKeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _businessHoursTimer.Stop();
        StopRemoteManagement();
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotKey && m.WParam.ToInt32() == StaffExitHotKeyId)
        {
            ShowStaffExitPrompt();
            return;
        }
        base.WndProc(ref m);
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(KioskSettings.DataDirectory, "WebView2");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: new CoreWebView2EnvironmentOptions("--disable-features=msEdgeSidebarV2"));

            await _webView.EnsureCoreWebView2Async(environment);
            ConfigureBrowser();
            await InstallWaiverPageScriptAsync();

            _browserReady = true;
            _lastActivityUtc = DateTime.UtcNow;
            _idleTimer.Start();
            _businessHoursTimer.Start();
            StartRemoteManagement();
            ShowCurrentOperatingPage();
            _webView.Focus();
            KioskLog.Write("Kiosk started.");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new InvalidOperationException(
                "Microsoft Edge WebView2 Runtime is missing. Install the Evergreen WebView2 Runtime, then start the kiosk again.");
        }
    }

    private async Task InstallWaiverPageScriptAsync()
    {
        await _waiverPageScriptGate.WaitAsync();
        try
        {
            if (_waiverPageScriptId is not null)
            {
                _webView.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_waiverPageScriptId);
                _waiverPageScriptId = null;
            }

            var waiverPageScript = ActivityAndCompletionScript.Replace(
                "__MULLET_HOP_LOGO_DATA_URL__",
                GetApplicationLogoDataUrl(),
                StringComparison.Ordinal).Replace(
                "__MULLET_HOP_PROVIDER_LOGO_DATA_URL__",
                GetProviderLogoDataUrl(),
                StringComparison.Ordinal).Replace(
                "__MULLET_HOP_BACKGROUND_URL__",
                $"https://{ScreensaverVirtualHost}/{KioskBackgroundFileName}",
                StringComparison.Ordinal).Replace(
                "__MULLET_HOP_DARK_MODE__",
                _lastDarkTheme ? "true" : "false",
                StringComparison.Ordinal).Replace(
                "__MULLET_HOP_ASSISTANCE_REQUESTED__",
                _settings.AssistanceRequested ? "true" : "false",
                StringComparison.Ordinal).Replace(
                "__MULLET_HOP_ASSISTANCE_ACKNOWLEDGED__",
                _settings.AssistanceAcknowledged ? "true" : "false",
                StringComparison.Ordinal).Replace(
                "__MULLET_HOP_ASSISTANCE_SESSION__",
                _assistanceBrowserSession,
                StringComparison.Ordinal);
            _waiverPageScriptId = await _webView.CoreWebView2
                .AddScriptToExecuteOnDocumentCreatedAsync(waiverPageScript);
        }
        finally
        {
            _waiverPageScriptGate.Release();
        }
    }

    private async Task ApplyKioskThemeIfChangedAsync(bool force = false)
    {
        var status = KioskTheme.Evaluate(_settings, DateTime.Now);
        if (!force && (_promptOpen || status.IsDark == _lastDarkTheme)) return;

        var changed = status.IsDark != _lastDarkTheme;
        _lastDarkTheme = status.IsDark;
        BackColor = KioskTheme.WindowBackground(_lastDarkTheme);
        _webView.DefaultBackgroundColor = KioskTheme.WindowBackground(_lastDarkTheme);
        if (!_browserReady) return;

        await InstallWaiverPageScriptAsync();
        try
        {
            var dark = _lastDarkTheme ? "true" : "false";
            await _webView.CoreWebView2.ExecuteScriptAsync(
                "if (window.__mulletHopSetDarkMode) window.__mulletHopSetDarkMode(" + dark + ");" +
                "else { document.body?.classList.toggle('dark-theme', " + dark + ");" +
                "document.body?.classList.toggle('mullet-hop-dark-theme', " + dark + "); }");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Live kiosk theme update error: " + ex.Message);
        }

        if (changed)
            KioskLog.Write("Kiosk appearance changed to " +
                           (_lastDarkTheme ? "Dark" : "Light") + ". " + status.Description);
    }

    private void ConfigureBrowser()
    {
        var core = _webView.CoreWebView2;
        var browserSettings = core.Settings;

        browserSettings.AreDefaultContextMenusEnabled = false;
        browserSettings.AreDevToolsEnabled = false;
        browserSettings.AreBrowserAcceleratorKeysEnabled = false;
        browserSettings.IsStatusBarEnabled = false;
        browserSettings.IsZoomControlEnabled = false;
        browserSettings.IsPinchZoomEnabled = false;
        browserSettings.IsSwipeNavigationEnabled = false;
        browserSettings.AreHostObjectsAllowed = false;
        browserSettings.IsBuiltInErrorPageEnabled = false;

        core.Profile.IsPasswordAutosaveEnabled = false;
        core.Profile.IsGeneralAutofillEnabled = false;

        AdvertisementFiles.RecoverInterruptedSync(_settings);
        Directory.CreateDirectory(KioskSettings.AdvertisementsDirectory);
        core.SetVirtualHostNameToFolderMapping(
            AdvertisementVirtualHost,
            KioskSettings.AdvertisementsDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.SetVirtualHostNameToFolderMapping(
            ScreensaverVirtualHost,
            AppContext.BaseDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);

        core.NavigationStarting += Core_NavigationStarting;
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!IsAllowedUri(e.Uri))
                e.Cancel = true;
        };
        core.NavigationCompleted += Core_NavigationCompleted;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.WebMessageReceived += Core_WebMessageReceived;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.ProcessFailed += async (_, _) =>
        {
            KioskLog.Write("Web content process failed; resetting.");
            await ResetForNextGuestAsync("browser process recovery");
        };

    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _retryTimer.Stop();

        if (IsInternalKioskPageUri(e.Uri))
            return;

        if (IsAllowedUri(e.Uri))
        {
            if (IsCompletionUri(e.Uri))
            {
                e.Cancel = true;
                BeginInvoke(new Action(ShowThankYouPage));
            }
            return;
        }

        e.Cancel = true;
        ShowBanner("For guest safety, this kiosk only opens the waiver website.", false);
        KioskLog.Write("Blocked navigation outside the waiver site.");
    }

    private async void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_showingThankYouPage || _showingClosedPage || _showingBusinessClosedPage ||
            _showingBlackout || _showingScreensaver)
            return;

        if (!e.IsSuccess || e.HttpStatusCode >= 400)
        {
            KioskLog.Write($"Waiver navigation failed: {e.WebErrorStatus}; HTTP {e.HttpStatusCode}.");
            ShowStationClosedPage(connectionError: true);
            return;
        }

        HideBanner();
        var current = _webView.Source?.ToString() ?? string.Empty;
        if (IsCompletionUri(current))
        {
            ShowThankYouPage();
            return;
        }

        await RestoreRememberedWaiverContextAsync();
        await ApplyPendingWaiverSwitchAsync();
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsAllowedUri(e.Uri))
            _webView.CoreWebView2.Navigate(e.Uri);
        else
        {
            ShowBanner("Outside websites are blocked on this waiver kiosk.", false);
            KioskLog.Write("Blocked pop-up outside the waiver site.");
        }
    }

    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_showingBlackout)
            return;

        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch { return; }

        if (message == "activity")
            MarkActivity();
        else if (message == "screensaver-wake")
            _ = WakeFromScreensaverAsync();
        else if (message == "completion-text")
            ShowThankYouPage();
        else if (message == "reset-waiver")
            _ = ResetForNextGuestAsync("guest reset button");
        else if (message == "switch-waiver-reset")
            _ = ResetForNextGuestAsync("waiver type change", showStatus: false);
        else if (message == "assistance-request")
            RequestGuestAssistance();
        else if (message == "assistance-clear")
            ClearGuestAssistance();
        else if (message.StartsWith('{'))
        {
            try
            {
                using var payload = JsonDocument.Parse(message);
                var root = payload.RootElement;
                if (!root.TryGetProperty("type", out var typeElement))
                    return;

                var type = typeElement.GetString();
                if (type == "remember-waiver-choice")
                {
                    var email = root.TryGetProperty("email", out var emailElement)
                        ? (emailElement.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    var choice = root.TryGetProperty("choice", out var choiceElement)
                        ? (choiceElement.GetString() ?? string.Empty).Trim().ToLowerInvariant()
                        : string.Empty;
                    if (email.Length is >= 3 and <= 254 && email.Contains('@') &&
                        (choice == "just-me" || choice == "family"))
                    {
                        _lastWaiverEmail = email;
                        _lastWaiverChoice = choice;
                    }
                }
                else if (type == "switch-waiver-option")
                {
                    var email = root.TryGetProperty("email", out var emailElement)
                        ? emailElement.GetString() ?? string.Empty
                        : string.Empty;
                    var choice = root.TryGetProperty("choice", out var choiceElement)
                        ? choiceElement.GetString() ?? string.Empty
                        : string.Empty;
                    _ = RestartWithAlternateChoiceAsync(email, choice);
                }
                else if (type == "switch-applied")
                {
                    _pendingSwitchEmail = null;
                    _pendingSwitchChoice = null;
                    KioskLog.Write("Alternate waiver type selected automatically.");
                }
                else if (type == "switch-failed")
                {
                    _pendingSwitchEmail = null;
                    _pendingSwitchChoice = null;
                    KioskLog.Write("Automatic waiver-type selection was not available.");
                }
            }
            catch (JsonException)
            {
                KioskLog.Write("Ignored an invalid message from the waiver page.");
            }
        }
    }

    private void IdleTimer_Tick(object? sender, EventArgs e)
    {
        if (!_browserReady || _promptOpen || _isResetting || _completionTimer.Enabled ||
            _settings.AssistanceRequested ||
            _showingClosedPage || _showingBusinessClosedPage || _showingBlackout ||
            _showingScreensaver)
            return;

        var idleFor = DateTime.UtcNow - _lastActivityUtc;
        if (idleFor >= TimeSpan.FromMinutes(Math.Max(1, _settings.ScreensaverTimeoutMinutes)))
        {
            ShowScreensaver();
            return;
        }

        if (!_idleResetPerformed &&
            idleFor >= TimeSpan.FromMinutes(Math.Max(1, _settings.IdleTimeoutMinutes)))
        {
            _idleResetPerformed = true;
            _ = ResetForNextGuestAsync("inactivity", showStatus: false, resetIdleClock: false);
        }
    }

    private void MarkActivity()
    {
        _lastActivityUtc = DateTime.UtcNow;
        _idleResetPerformed = false;
    }

    private async void RetryTimer_Tick(object? sender, EventArgs e) =>
        await CheckForRestoredConnectionAsync();

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable || IsDisposed || Disposing || !IsHandleCreated)
            return;

        try
        {
            BeginInvoke(new Action(() => _ = CheckForRestoredConnectionAsync()));
        }
        catch (InvalidOperationException)
        {
            // The kiosk window is closing, so there is no connection page to recover.
        }
    }

    private async Task CheckForRestoredConnectionAsync()
    {
        _retryTimer.Stop();

        if (!_browserReady || _settings.StationClosed || !_showingClosedPage)
            return;

        if (_isResetting || _promptOpen)
        {
            _retryTimer.Start();
            return;
        }

        if (_connectionCheckInProgress)
            return;

        _connectionCheckInProgress = true;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _settings.StartUrl);
            request.Headers.UserAgent.ParseAdd("MulletHopWaiverKiosk/" + KioskUpdater.CurrentVersion);
            using var response = await ConnectionCheckClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead);

            if (response.IsSuccessStatusCode && _browserReady && !_isResetting &&
                !_promptOpen && !_settings.StationClosed && _showingClosedPage)
            {
                KioskLog.Write("Waiver connection restored; preparing a fresh starting page.");
                await ResetForNextGuestAsync("connection restored", showStatus: false);
                return;
            }

            KioskLog.Write($"Automatic waiver connection check returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Automatic waiver connection check failed: " +
                ex.GetType().Name + " - " + ex.Message);
        }
        finally
        {
            _connectionCheckInProgress = false;
            if (_browserReady && !_isResetting && !_settings.StationClosed && _showingClosedPage)
                _retryTimer.Start();
        }
    }

    private bool IsAllowedUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var hostAllowed = _settings.AllowedHosts.Any(host =>
            string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase));
        if (!hostAllowed)
            return false;

        return _settings.AllowedPathPrefixes.Any(prefix =>
            uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsInternalKioskPageUri(string? value)
    {
        if ((!_showingThankYouPage && !_showingClosedPage && !_showingBusinessClosedPage &&
             !_showingBlackout && !_showingScreensaver) ||
            string.IsNullOrWhiteSpace(value))
            return false;

        return string.Equals(value, "about:blank", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCompletionUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;

        var partToCheck = (uri.AbsolutePath + uri.Query).ToLowerInvariant();
        return _settings.CompletionUrlKeywords.Any(keyword =>
            partToCheck.Contains(keyword.ToLowerInvariant(), StringComparison.Ordinal));
    }

    private void ShowThankYouPage() => ShowThankYouPage(staffPreview: false, scheduleTimeOverride: null);

    private void ShowThankYouPage(bool staffPreview, DateTime? scheduleTimeOverride)
    {
        if (!_browserReady || _isResetting || _settings.StationClosed ||
            (!staffPreview && !BusinessHoursAllowGuestUse()) ||
            (_showingThankYouPage && !staffPreview))
            return;

        SetBrowserInputEnabled(true);
        _showingThankYouPage = true;
        _showingClosedPage = false;
        _showingBusinessClosedPage = false;
        _showingBlackout = false;
        _showingScreensaver = false;
        _preOpeningScreensaverActive = false;
        _completionTimer.Stop();
        _retryTimer.Stop();
        HideBanner();
        UpdateAssistancePanelState();
        _webView.CoreWebView2.NavigateToString(BuildThankYouHtml(scheduleTimeOverride));
        _completionTimer.Start();
        KioskLog.Write(staffPreview
            ? "Staff preview of the branded thank-you page displayed for " +
                (scheduleTimeOverride ?? GetEffectiveNow()).ToString("O") + "."
            : "Waiver completion detected; branded thank-you page displayed.");
    }

    private async Task ResetForNextGuestAsync(
        string reason, bool showStatus = true, bool resetIdleClock = true)
    {
        if (!_browserReady || _isResetting)
            return;

        _isResetting = true;
        _pendingSwitchEmail = null;
        _pendingSwitchChoice = null;
        _lastWaiverEmail = null;
        _lastWaiverChoice = null;
        _completionTimer.Stop();
        _retryTimer.Stop();
        if (showStatus)
            ShowBanner("Preparing a fresh waiver…", true);
        else
            HideBanner();

        try
        {
            await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync();
            _showingThankYouPage = false;
            _showingClosedPage = false;
            _showingBusinessClosedPage = false;
            _showingBlackout = false;
            _showingScreensaver = false;
            _preOpeningScreensaverActive = false;
            SetBrowserInputEnabled(true);
            ShowCurrentOperatingPage();
            if (resetIdleClock)
                MarkActivity();
            KioskLog.Write("Waiver reset: " + reason + ".");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Reset error: " + ex.GetType().Name + " - " + ex.Message);
            ShowStationClosedPage(connectionError: !_settings.StationClosed);
        }
        finally
        {
            _isResetting = false;
        }
    }

    private async Task RestartWithAlternateChoiceAsync(string email, string choice)
    {
        email = email.Trim();
        choice = choice.Trim().ToLowerInvariant();
        if (!_browserReady || _isResetting || email.Length is < 3 or > 254 || !email.Contains('@') ||
            (choice != "just-me" && choice != "family"))
        {
            KioskLog.Write("Ignored an incomplete waiver-type switch request.");
            return;
        }

        _isResetting = true;
        _pendingSwitchEmail = email;
        _pendingSwitchChoice = choice;
        _lastWaiverEmail = email;
        _lastWaiverChoice = choice;
        _completionTimer.Stop();
        _retryTimer.Stop();
        HideBanner();

        try
        {
            await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync();
            _showingThankYouPage = false;
            _showingClosedPage = false;
            _showingBusinessClosedPage = false;
            _showingBlackout = false;
            _showingScreensaver = false;
            _preOpeningScreensaverActive = false;
            SetBrowserInputEnabled(true);
            _webView.CoreWebView2.Navigate(_settings.StartUrl);
            _lastActivityUtc = DateTime.UtcNow;
            KioskLog.Write("Restarting the waiver with the alternate guest option.");
        }
        catch (Exception ex)
        {
            _pendingSwitchEmail = null;
            _pendingSwitchChoice = null;
            KioskLog.Write("Waiver switch error: " + ex.GetType().Name + " - " + ex.Message);
            ShowStationClosedPage(connectionError: true);
        }
        finally
        {
            _isResetting = false;
        }
    }

    private async Task ApplyPendingWaiverSwitchAsync()
    {
        if (string.IsNullOrWhiteSpace(_pendingSwitchEmail) || string.IsNullOrWhiteSpace(_pendingSwitchChoice))
            return;

        var emailJson = JsonSerializer.Serialize(_pendingSwitchEmail);
        var choiceJson = JsonSerializer.Serialize(_pendingSwitchChoice);
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(
                $"window.__mulletHopApplyWaiverSwitch?.({emailJson}, {choiceJson});");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Waiver switch script error: " + ex.GetType().Name + " - " + ex.Message);
        }
    }

    private async Task RestoreRememberedWaiverContextAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastWaiverEmail) || string.IsNullOrWhiteSpace(_lastWaiverChoice))
            return;

        var emailJson = JsonSerializer.Serialize(_lastWaiverEmail);
        var choiceJson = JsonSerializer.Serialize(_lastWaiverChoice);
        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(
                $"window.__mulletHopSetWaiverContext?.({emailJson}, {choiceJson});");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Waiver context restore error: " + ex.GetType().Name + " - " + ex.Message);
        }
    }

    private bool BusinessHoursAllowGuestUse()
    {
        var status = BusinessHoursCalculator.Evaluate(_settings, DateTime.Now);
        return status.Mode is BusinessHoursMode.Disabled or BusinessHoursMode.Open ||
               (status.Mode == BusinessHoursMode.PreOpening &&
                status.NextOpening?.Ticks == _dismissedPreOpeningTicks);
    }

    private void ShowCurrentOperatingPage()
    {
        if (_manualBusinessBlackout)
        {
            ShowBlackoutPage(manual: true);
            return;
        }

        if (_settings.StationClosed)
        {
            ShowStationClosedPage(connectionError: false);
            return;
        }

        var now = DateTime.Now;
        var status = BusinessHoursCalculator.Evaluate(_settings, now);
        var preOpeningWasDismissed = status.Mode == BusinessHoursMode.PreOpening &&
            status.NextOpening?.Ticks == _dismissedPreOpeningTicks;

        if (status.Mode == BusinessHoursMode.Open)
            _dismissedPreOpeningTicks = null;

        if (status.Mode == BusinessHoursMode.PreOpening && !preOpeningWasDismissed)
        {
            ResetBusinessClosedPeriod();
            ShowScreensaver(preOpening: true, openingTime: status.NextOpening);
            return;
        }

        if (status.Mode == BusinessHoursMode.Closed)
        {
            EnsureBusinessClosedPeriod(status, now);
            if (DateTime.UtcNow - _businessClosedPeriodStartedUtc!.Value >=
                TimeSpan.FromMinutes(_settings.BusinessClosedMessageMinutes))
            {
                ShowBlackoutPage();
            }
            else
            {
                ShowBusinessClosedPage(status.NextOpening);
            }
            return;
        }

        ResetBusinessClosedPeriod();
        SetBrowserInputEnabled(true);
        _webView.CoreWebView2.Navigate(_settings.StartUrl);
        UpdateAssistancePanelState();
    }

    private async Task ApplyBusinessHoursStateAsync()
    {
        if (!_browserReady || _promptOpen || _isResetting || _settings.StationClosed ||
            _manualBusinessBlackout ||
            _businessHoursCheckInProgress)
            return;

        _businessHoursCheckInProgress = true;
        try
        {
            var now = DateTime.Now;
            var status = BusinessHoursCalculator.Evaluate(_settings, now);
            var preOpeningWasDismissed = status.Mode == BusinessHoursMode.PreOpening &&
                status.NextOpening?.Ticks == _dismissedPreOpeningTicks;

            if (status.Mode is BusinessHoursMode.Disabled or BusinessHoursMode.Open ||
                preOpeningWasDismissed)
            {
                ResetBusinessClosedPeriod();
                if (status.Mode == BusinessHoursMode.Open)
                    _dismissedPreOpeningTicks = null;

                if (_showingBusinessClosedPage || _showingBlackout)
                {
                    await ResetForNextGuestAsync(
                        "business hours opened", showStatus: false);
                }
                else if (_preOpeningScreensaverActive && status.Mode == BusinessHoursMode.Open)
                {
                    // At opening time the video remains on screen and behaves like the normal
                    // screensaver. Guest activity will load a clean starting waiver.
                    _preOpeningScreensaverActive = false;
                    _preOpeningScreensaverOpeningTime = null;
                }
                return;
            }

            if (status.Mode == BusinessHoursMode.PreOpening)
            {
                ResetBusinessClosedPeriod();
                if (_showingScreensaver)
                {
                    _preOpeningScreensaverActive = true;
                    _preOpeningScreensaverOpeningTime = status.NextOpening;
                    return;
                }

                await ResetForNextGuestAsync(
                    "pre-opening screensaver window began", showStatus: false);
                return;
            }

            var startedNewClosedPeriod = EnsureBusinessClosedPeriod(status, now);
            if (startedNewClosedPeriod && !_showingBusinessClosedPage && !_showingBlackout)
            {
                await ResetForNextGuestAsync(
                    "business hours closed", showStatus: false);
                return;
            }

            var closedFor = DateTime.UtcNow - _businessClosedPeriodStartedUtc!.Value;
            if (closedFor >= TimeSpan.FromMinutes(_settings.BusinessClosedMessageMinutes))
            {
                if (!_showingBlackout)
                    ShowBlackoutPage();
            }
            else if (!_showingBusinessClosedPage)
            {
                await ResetForNextGuestAsync(
                    "business closed page restored", showStatus: false);
            }
        }
        catch (Exception ex)
        {
            KioskLog.Write("Business hours transition error: " +
                ex.GetType().Name + " - " + ex.Message);
        }
        finally
        {
            _businessHoursCheckInProgress = false;
        }
    }

    private bool EnsureBusinessClosedPeriod(BusinessHoursStatus status, DateTime now)
    {
        var key = status.NextOpening.HasValue
            ? status.NextOpening.Value.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "no-scheduled-opening";
        if (string.Equals(_businessClosedPeriodKey, key, StringComparison.Ordinal) &&
            _businessClosedPeriodStartedUtc.HasValue)
            return false;

        _businessClosedPeriodKey = key;
        _businessClosedPeriodStartedUtc = DateTime.UtcNow;
        _dismissedPreOpeningTicks = null;
        KioskLog.Write("Business hours closed at " + now.ToString("O") + ".");
        return true;
    }

    private void ResetBusinessClosedPeriod()
    {
        _businessClosedPeriodKey = null;
        _businessClosedPeriodStartedUtc = null;
    }

    private void SetBrowserInputEnabled(bool enabled)
    {
        _webView.Enabled = enabled;
    }

    private void ShowBusinessClosedPage(DateTime? nextOpening)
    {
        if (!_browserReady)
            return;

        SetBrowserInputEnabled(true);
        _showingThankYouPage = false;
        _showingClosedPage = false;
        _showingBusinessClosedPage = true;
        _showingBlackout = false;
        _showingScreensaver = false;
        _preOpeningScreensaverActive = false;
        _preOpeningScreensaverOpeningTime = null;
        _completionTimer.Stop();
        _retryTimer.Stop();
        HideBanner();
        UpdateAssistancePanelState();
        _webView.CoreWebView2.NavigateToString(BuildBusinessClosedHtml(nextOpening));
        KioskLog.Write("The scheduled Business Closed page was displayed.");
    }

    private async Task PreviewBusinessClosedOverlayAsync()
    {
        const int previewSeconds = 10;
        var nextOpening = BusinessHoursCalculator.FindNextOpening(
            _settings,
            GetEffectiveNow());
        using var previewWebView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.White,
            TabStop = false
        };

        Controls.Add(previewWebView);
        previewWebView.BringToFront();
        TopMost = true;
        Activate();

        try
        {
            await previewWebView.EnsureCoreWebView2Async(
                _webView.CoreWebView2.Environment);

            var core = previewWebView.CoreWebView2;
            var browserSettings = core.Settings;
            browserSettings.AreDefaultContextMenusEnabled = false;
            browserSettings.AreDevToolsEnabled = false;
            browserSettings.AreBrowserAcceleratorKeysEnabled = false;
            browserSettings.IsStatusBarEnabled = false;
            browserSettings.IsZoomControlEnabled = false;
            browserSettings.IsPinchZoomEnabled = false;
            browserSettings.IsSwipeNavigationEnabled = false;
            browserSettings.AreHostObjectsAllowed = false;
            browserSettings.IsBuiltInErrorPageEnabled = false;
            core.SetVirtualHostNameToFolderMapping(
                ScreensaverVirtualHost,
                AppContext.BaseDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) =>
                e.State = CoreWebView2PermissionState.Deny;

            var navigationCompleted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                    navigationCompleted.TrySetResult(true);
                else
                    navigationCompleted.TrySetException(new InvalidOperationException(
                        "The Business Closed preview page could not be displayed."));
            };
            core.NavigateToString(
                BuildBusinessClosedHtml(nextOpening, previewSeconds));
            await navigationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15));

            KioskLog.Write(
                "The staff Business Closed preview was displayed over the current kiosk page.");
            await Task.Delay(TimeSpan.FromSeconds(previewSeconds));
            KioskLog.Write(
                "The staff Business Closed preview ended and the previous kiosk page was restored.");
        }
        finally
        {
            Controls.Remove(previewWebView);
            if (!_allowExit)
                TopMost = false;
        }
    }

    private void ShowBlackoutPage(bool manual = false)
    {
        if (!_browserReady)
            return;

        _manualBusinessBlackout = manual;
        _showingThankYouPage = false;
        _showingClosedPage = false;
        _showingBusinessClosedPage = false;
        _showingBlackout = true;
        _showingScreensaver = false;
        _preOpeningScreensaverActive = false;
        _preOpeningScreensaverOpeningTime = null;
        _completionTimer.Stop();
        _retryTimer.Stop();
        HideBanner();
        UpdateAssistancePanelState();
        _webView.CoreWebView2.NavigateToString(BuildBlackoutHtml());
        SetBrowserInputEnabled(false);
        KioskLog.Write("The scheduled business-hours blackout started. Only the staff shortcut remains active.");
    }

    private void ShowStationClosedPage(bool connectionError)
    {
        if (!_browserReady)
            return;

        _manualBusinessBlackout = false;
        SetBrowserInputEnabled(true);
        _showingThankYouPage = false;
        _showingClosedPage = true;
        _showingBusinessClosedPage = false;
        _showingBlackout = false;
        _showingScreensaver = false;
        _preOpeningScreensaverActive = false;
        _preOpeningScreensaverOpeningTime = null;
        _completionTimer.Stop();
        _retryTimer.Stop();
        HideBanner();
        UpdateAssistancePanelState();
        _webView.CoreWebView2.NavigateToString(BuildStationClosedHtml(connectionError));

        if (connectionError && !_settings.StationClosed)
            _retryTimer.Start();

        KioskLog.Write(connectionError
            ? "The branded connection-closed page was displayed; the waiver site will be retried automatically."
            : "The staff-controlled waiver station closed page was displayed.");
    }

    private void ShowScreensaver(bool preOpening = false, DateTime? openingTime = null)
    {
        if (!_browserReady || (_isResetting && !preOpening) || _promptOpen ||
            _settings.StationClosed || _settings.AssistanceRequested ||
            _showingClosedPage || _showingBusinessClosedPage ||
            _showingBlackout || _showingScreensaver)
            return;

        var videoPath = Path.Combine(AppContext.BaseDirectory, ScreensaverFileName);
        if (!File.Exists(videoPath))
        {
            KioskLog.Write("Screensaver video is missing: " + videoPath);
            if (!preOpening)
            {
                MarkActivity();
                return;
            }
        }

        SetBrowserInputEnabled(true);
        _showingScreensaver = true;
        _showingThankYouPage = false;
        _showingClosedPage = false;
        _showingBusinessClosedPage = false;
        _showingBlackout = false;
        _preOpeningScreensaverActive = preOpening;
        _preOpeningScreensaverOpeningTime = preOpening ? openingTime : null;
        _completionTimer.Stop();
        _retryTimer.Stop();
        HideBanner();
        UpdateAssistancePanelState();
        _webView.CoreWebView2.NavigateToString(BuildScreensaverHtml());
        KioskLog.Write(preOpening
            ? "The pre-opening screensaver started for the scheduled opening at " +
                openingTime?.ToString("O") + "."
            : "Screensaver started after " + _settings.ScreensaverTimeoutMinutes +
                " minute(s) without guest activity.");
    }

    private async Task WakeFromScreensaverAsync()
    {
        if (!_showingScreensaver || _isResetting)
            return;

        if (_preOpeningScreensaverActive && _preOpeningScreensaverOpeningTime.HasValue)
            _dismissedPreOpeningTicks = _preOpeningScreensaverOpeningTime.Value.Ticks;

        _preOpeningScreensaverActive = false;
        _preOpeningScreensaverOpeningTime = null;
        MarkActivity();
        KioskLog.Write("Screensaver dismissed by guest activity; loading a fresh starting page.");
        await ResetForNextGuestAsync("screensaver wake", showStatus: false);
    }

    private static string BuildScreensaverHtml()
    {
        var videoUrl = $"https://{ScreensaverVirtualHost}/{ScreensaverFileName}";
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Mullet Hop Waiver Kiosk</title>
              <style>
                * { box-sizing: border-box; }
                html, body {
                  width: 100%;
                  height: 100%;
                  margin: 0;
                  overflow: hidden;
                  background: #05080a;
                  cursor: none;
                }
                body {
                  display: flex;
                  flex-direction: column;
                }
                .video-stage {
                  flex: 1 1 auto;
                  min-height: 0;
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  background: #05080a;
                }
                video {
                  display: block;
                  width: 100%;
                  height: 100%;
                  object-fit: contain;
                }
                .wake-message {
                  flex: 0 0 auto;
                  width: 100%;
                  min-height: 72px;
                  padding: 18px 24px;
                  border-top: 3px solid rgba(255,255,255,.88);
                  background: #101820;
                  color: #fff;
                  font: 800 clamp(17px, 2vw, 27px)/1.2 'Segoe UI', Arial, sans-serif;
                  letter-spacing: .8px;
                  text-align: center;
                  pointer-events: none;
                }
              </style>
            </head>
            <body>
              <div class="video-stage">
                <video id="screensaver-video" autoplay loop muted playsinline preload="auto">
                  <source src="{{videoUrl}}" type="video/mp4">
                </video>
              </div>
              <div class="wake-message">TOUCH THE SCREEN TO START A WAIVER</div>
              <script>
                let waking = false;
                const wake = () => {
                  if (waking) return;
                  waking = true;
                  window.chrome.webview.postMessage('screensaver-wake');
                };
                ['pointerdown', 'touchstart', 'mousedown', 'keydown']
                  .forEach(name => window.addEventListener(name, wake,
                    { capture: true, passive: true }));
                document.getElementById('screensaver-video').play().catch(() => {});
              </script>
            </body>
            </html>
            """;
    }

    private string BuildBusinessClosedHtml(
        DateTime? nextOpening,
        int? previewSeconds = null)
    {
        var backgroundUrl = $"https://{ScreensaverVirtualHost}/{KioskBackgroundFileName}";
        var logoDataUrl = GetApplicationLogoDataUrl();
        var logoMarkup = string.IsNullOrWhiteSpace(logoDataUrl)
            ? "<div class=\"logo-fallback\">MULLET HOP</div>"
            : $"<img class=\"fish-logo\" src=\"{logoDataUrl}\" alt=\"Mullet Hop fish logo\">";
        var openingMarkup = nextOpening.HasValue
            ? "<p class=\"opening\">We reopen <strong>" +
                System.Net.WebUtility.HtmlEncode(
                    nextOpening.Value.ToString("dddd, MMMM d 'at' h:mm tt")) +
                ".</strong></p>"
            : "<p class=\"opening\">Please check with the front desk for our next opening time.</p>";
        var previewMarkup = previewSeconds.HasValue
            ? "<div class=\"business-preview-banner\" role=\"status\" aria-live=\"polite\">" +
                "THIS IS A PREVIEW &mdash; Returning to Staff Settings in " +
                "<strong id=\"business-preview-countdown\">" + previewSeconds.Value +
                "</strong> seconds.</div>"
            : string.Empty;
        var previewBodyClass = string.Join(' ', new[]
        {
            previewSeconds.HasValue ? "business-preview-mode" : string.Empty,
            _lastDarkTheme ? "dark-theme" : string.Empty
        }.Where(value => value.Length > 0));
        var previewScript = previewSeconds.HasValue
            ? $$"""
              <script>
                (() => {
                  let remaining = {{previewSeconds.Value}};
                  const countdown = document.getElementById('business-preview-countdown');
                  const tick = () => {
                    remaining = Math.max(0, remaining - 1);
                    if (countdown) countdown.textContent = String(remaining);
                    if (remaining > 0) window.setTimeout(tick, 1000);
                  };
                  window.setTimeout(tick, 1000);
                })();
              </script>
              """
            : string.Empty;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Business Closed | Mullet Hop</title>
              <style>
                :root {
                  --lime: #76c442;
                  --aqua: #00a4d6;
                  --purple: #75449a;
                  --orange: #f58220;
                  --ink: #101820;
                }
                * { box-sizing: border-box; }
                html, body { width: 100%; height: 100%; margin: 0; overflow: hidden; }
                body {
                  display: grid;
                  place-items: center;
                  padding: 28px;
                  color: var(--ink);
                  font-family: 'Open Sans', 'Segoe UI', Arial, sans-serif;
                  background-color: #eefaff;
                  background-image:
                    linear-gradient(rgba(247,252,255,.18), rgba(247,252,255,.18)),
                    url('{{backgroundUrl}}');
                  background-position: center;
                  background-size: cover;
                }
                .business-preview-banner {
                  position: fixed;
                  z-index: 20;
                  top: 0;
                  left: 0;
                  right: 0;
                  min-height: 64px;
                  display: grid;
                  place-items: center;
                  padding: 12px 28px;
                  border-bottom: 4px solid var(--ink);
                  background: var(--purple);
                  color: #fff;
                  box-shadow: 0 8px 22px rgba(16,24,32,.24);
                  font-size: clamp(20px, 2.2vw, 32px);
                  line-height: 1.2;
                  font-weight: 800;
                  letter-spacing: .4px;
                  text-align: center;
                }
                .business-preview-banner strong {
                  display: inline-grid;
                  min-width: 1.5em;
                  color: #ffe36e;
                  font-size: 1.15em;
                }
                body.business-preview-mode { padding-top: 96px; }
                .card {
                  width: min(980px, 94vw);
                  max-height: 94vh;
                  overflow: hidden;
                  border: 4px solid var(--ink);
                  border-radius: 30px;
                  background: #fff;
                  box-shadow: 0 22px 55px rgba(16,24,32,.24);
                  text-align: center;
                }
                .stripe {
                  height: 17px;
                  background: linear-gradient(90deg, var(--lime) 0 25%, var(--aqua) 25% 50%, var(--purple) 50% 75%, var(--orange) 75% 100%);
                }
                .content { padding: clamp(24px, 4.5vh, 48px) clamp(28px, 6vw, 74px) 42px; }
                .brand { min-height: 118px; display: grid; place-items: center; margin-bottom: 6px; }
                .fish-logo { width: 132px; height: 132px; object-fit: contain; }
                .logo-fallback {
                  color: #0877bd;
                  font-size: clamp(34px, 5vw, 60px);
                  font-weight: 800;
                  -webkit-text-stroke: 2px var(--ink);
                }
                .badge {
                  display: inline-grid;
                  place-items: center;
                  min-width: 140px;
                  height: 62px;
                  margin: 4px auto 18px;
                  padding: 0 26px;
                  border: 4px solid var(--ink);
                  border-radius: 999px;
                  background: var(--orange);
                  box-shadow: 0 8px 0 rgba(16,24,32,.12);
                  font-size: 22px;
                  font-weight: 800;
                  letter-spacing: 1px;
                }
                h1 {
                  margin: 0 auto 24px;
                  color: var(--purple);
                  font-size: clamp(42px, 6vw, 76px);
                  line-height: 1.02;
                  font-weight: 800;
                  letter-spacing: -2px;
                }
                .message {
                  margin: 0 auto 20px;
                  padding: 23px 30px;
                  border: 3px solid var(--aqua);
                  border-radius: 18px;
                  background: #eefaff;
                  font-size: clamp(21px, 2.4vw, 31px);
                  line-height: 1.35;
                  font-weight: 700;
                }
                .opening {
                  margin: 0 auto;
                  padding: 22px 28px;
                  border: 3px solid var(--lime);
                  border-radius: 18px;
                  background: #f7fff2;
                  font-size: clamp(20px, 2.25vw, 29px);
                  line-height: 1.3;
                }
                .opening strong { color: #397819; }
                body.dark-theme {
                  color: #edf3f7;
                  background-color: #111820;
                  background-image:
                    linear-gradient(rgba(10,16,23,.78), rgba(10,16,23,.78)),
                    url('{{backgroundUrl}}');
                }
                body.dark-theme .card {
                  background: #1b242e;
                  border-color: #d6e1e8;
                  box-shadow: 0 22px 55px rgba(0,0,0,.5);
                }
                body.dark-theme h1 { color: #d3a4ee; }
                body.dark-theme .message {
                  color: #edf3f7;
                  background: #273643;
                }
                body.dark-theme .opening {
                  color: #edf3f7;
                  background: #26372d;
                }
                body.dark-theme .opening strong { color: #9ddd83; }
                @media (max-height: 720px) {
                  .content { padding-top: 20px; padding-bottom: 24px; }
                  .brand { min-height: 78px; }
                  .fish-logo { width: 86px; height: 86px; }
                  .badge { height: 48px; margin-bottom: 12px; font-size: 18px; }
                  h1 { margin-bottom: 15px; }
                  .message, .opening { padding-top: 15px; padding-bottom: 15px; }
                  .message { margin-bottom: 14px; }
                }
              </style>
            </head>
            <body class="{{previewBodyClass}}">
              {{previewMarkup}}
              <main class="card" aria-labelledby="business-closed-heading">
                <div class="stripe"></div>
                <div class="content">
                  <div class="brand">{{logoMarkup}}</div>
                  <div class="badge" aria-hidden="true">CLOSED</div>
                  <h1 id="business-closed-heading">BUSINESS CLOSED</h1>
                  <p class="message">Mullet Hop is currently closed. Please return during our normal business hours.</p>
                  {{openingMarkup}}
                </div>
              </main>
              {{previewScript}}
            </body>
            </html>
            """;
    }

    private static string BuildBlackoutHtml() =>
        """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Mullet Hop Kiosk</title>
          <style>
            * { box-sizing: border-box; }
            html, body {
              width: 100%;
              height: 100%;
              margin: 0;
              overflow: hidden;
              background: #000;
              cursor: none;
              user-select: none;
              pointer-events: none;
            }
          </style>
        </head>
        <body aria-hidden="true"></body>
        </html>
        """;

    private string BuildStationClosedHtml(bool connectionError)
    {
        var backgroundUrl = $"https://{ScreensaverVirtualHost}/{KioskBackgroundFileName}";
        var logoDataUrl = GetApplicationLogoDataUrl();
        var logoMarkup = string.IsNullOrWhiteSpace(logoDataUrl)
            ? "<div class=\"logo-fallback\">MULLET HOP</div>"
            : $"<img class=\"fish-logo\" src=\"{logoDataUrl}\" alt=\"Mullet Hop fish logo\">";
        var statusMarkup = connectionError
            ? """
                <section class="message connection-message">
                  <span class="message-label">CONNECTION ISSUE</span>
                  <p>The application cannot reach the waiver website or does not have an internet connection.</p>
                  <small>The kiosk will check the connection every 60 seconds.</small>
                </section>
                """
            : """
                <section class="message closed-message">
                  <span class="message-label">STATION CLOSED</span>
                  <p>This waiver station is currently closed.</p>
                </section>
                """;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Waiver Station Closed | Mullet Hop</title>
              <style>
                :root {
                  --lime: #76c442;
                  --aqua: #00a4d6;
                  --blue: #0877bd;
                  --purple: #75449a;
                  --orange: #f58220;
                  --ink: #101820;
                  --paper: #ffffff;
                }
                * { box-sizing: border-box; }
                html, body { width: 100%; height: 100%; margin: 0; overflow: hidden; }
                body {
                  font-family: 'Open Sans', 'Segoe UI', Arial, sans-serif;
                  color: var(--ink);
                  background-color: #eefaff;
                  background-image:
                    linear-gradient(rgba(247,252,255,.18), rgba(247,252,255,.18)),
                    url('{{backgroundUrl}}');
                  background-position: center;
                  background-size: cover;
                  background-repeat: no-repeat;
                  background-attachment: fixed;
                  display: grid;
                  place-items: center;
                  padding: 28px;
                }
                .card {
                  position: relative;
                  width: min(980px, 94vw);
                  max-height: 94vh;
                  background: var(--paper);
                  border: 4px solid var(--ink);
                  border-radius: 30px;
                  box-shadow: 0 22px 55px rgba(16,24,32,.24);
                  overflow: hidden;
                  text-align: center;
                }
                .stripe {
                  height: 17px;
                  background: linear-gradient(90deg, var(--lime) 0 25%, var(--aqua) 25% 50%, var(--purple) 50% 75%, var(--orange) 75% 100%);
                }
                .content { padding: clamp(24px, 4.5vh, 48px) clamp(28px, 6vw, 74px) 42px; }
                .brand { min-height: 118px; display: grid; place-items: center; margin-bottom: 6px; }
                .fish-logo { width: 132px; height: 132px; object-fit: contain; }
                .logo-fallback {
                  font-size: clamp(34px, 5vw, 60px);
                  line-height: 1;
                  font-weight: 800;
                  color: var(--blue);
                  -webkit-text-stroke: 2px var(--ink);
                }
                .closed-badge {
                  display: inline-grid;
                  place-items: center;
                  min-width: 112px;
                  height: 62px;
                  margin: 4px auto 18px;
                  padding: 0 24px;
                  border: 4px solid var(--ink);
                  border-radius: 999px;
                  background: var(--orange);
                  box-shadow: 0 8px 0 rgba(16,24,32,.12);
                  color: var(--ink);
                  font-size: 22px;
                  line-height: 1;
                  font-weight: 800;
                  letter-spacing: 1px;
                }
                h1 {
                  max-width: 820px;
                  margin: 0 auto 24px;
                  color: var(--purple);
                  font-size: clamp(40px, 5.8vw, 72px);
                  line-height: 1.02;
                  font-weight: 800;
                  letter-spacing: -2px;
                }
                .message {
                  margin: 0 auto 20px;
                  padding: 22px 28px;
                  border-radius: 18px;
                }
                .connection-message { background: #fff6e9; border: 3px solid var(--orange); }
                .closed-message { background: #eefaff; border: 3px solid var(--aqua); }
                .message-label {
                  display: inline-block;
                  margin-bottom: 7px;
                  color: var(--purple);
                  font-size: 14px;
                  font-weight: 800;
                  letter-spacing: 1.5px;
                }
                .message p {
                  margin: 0;
                  font-size: clamp(20px, 2.2vw, 29px);
                  line-height: 1.35;
                  font-weight: 700;
                }
                .message small {
                  display: block;
                  margin-top: 9px;
                  color: #53616d;
                  font-size: 16px;
                  font-weight: 600;
                }
                .assistance {
                  margin: 0 auto;
                  padding: 22px 28px;
                  border: 3px solid var(--lime);
                  border-radius: 18px;
                  background: #f7fff2;
                  font-size: clamp(21px, 2.35vw, 31px);
                  line-height: 1.3;
                  font-weight: 700;
                }
                .assistance strong { color: #397819; }
                body.dark-theme {
                  color: #edf3f7;
                  background-color: #111820;
                  background-image:
                    linear-gradient(rgba(10,16,23,.78), rgba(10,16,23,.78)),
                    url('{{backgroundUrl}}');
                }
                body.dark-theme .card {
                  background: #1b242e;
                  border-color: #d6e1e8;
                  box-shadow: 0 22px 55px rgba(0,0,0,.5);
                }
                body.dark-theme h1,
                body.dark-theme .message-label { color: #d3a4ee; }
                body.dark-theme .connection-message,
                body.dark-theme .closed-message {
                  color: #edf3f7;
                  background: #273643;
                }
                body.dark-theme .message small { color: #b1bec9; }
                body.dark-theme .assistance {
                  color: #edf3f7;
                  background: #26372d;
                }
                body.dark-theme .assistance strong { color: #9ddd83; }
                @media (max-height: 720px) {
                  .content { padding-top: 20px; padding-bottom: 24px; }
                  .brand { min-height: 80px; }
                  .fish-logo { width: 88px; height: 88px; }
                  .closed-badge { height: 50px; margin-bottom: 12px; font-size: 18px; }
                  h1 { margin-bottom: 15px; }
                  .message, .assistance { padding-top: 15px; padding-bottom: 15px; }
                  .message { margin-bottom: 14px; }
                }
              </style>
            </head>
            <body class="{{(_lastDarkTheme ? "dark-theme" : string.Empty)}}">
              <main class="card" aria-labelledby="closed-heading">
                <div class="stripe"></div>
                <div class="content">
                  <div class="brand">{{logoMarkup}}</div>
                  <div class="closed-badge" aria-hidden="true">CLOSED</div>
                  <h1 id="closed-heading">WAIVER STATION CLOSED</h1>
                  {{statusMarkup}}
                  <section class="assistance">
                    Please see a staff member at the <strong>front desk</strong> for assistance.
                  </section>
                </div>
              </main>
            </body>
            </html>
            """;
    }

    private static string GetApplicationLogoDataUrl()
    {
        var embeddedLogo = GetEmbeddedPngDataUrl(
            "MulletHopWaiverKiosk.Assets.MulletHopFish.png");
        if (!string.IsNullOrWhiteSpace(embeddedLogo))
            return embeddedLogo;

        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon is null)
                return string.Empty;

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetProviderLogoDataUrl()
    {
        var providerLogo = GetEmbeddedPngDataUrl(
            "MulletHopWaiverKiosk.Assets.MulletHopFullLogo.png");
        return string.IsNullOrWhiteSpace(providerLogo)
            ? GetApplicationLogoDataUrl()
            : providerLogo;
    }

    private static string GetEmbeddedPngDataUrl(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resourceName);
            if (stream is null)
                return string.Empty;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    private string BuildThankYouHtml(DateTime? scheduleTimeOverride = null)
    {
        var backgroundUrl = $"https://{ScreensaverVirtualHost}/{KioskBackgroundFileName}";
        var resetSeconds = Math.Max(12, _settings.CompletionResetSeconds);
        var effectiveNow = scheduleTimeOverride ?? GetEffectiveNow();
        var activeAdvertisements = new List<string>();
        foreach (var advertisement in _settings.Advertisements
                     .Where(ad => ad.IsActive(effectiveNow))
                     .OrderBy(ad => ad.Name)
                     .Take(12))
        {
            try
            {
                var path = AdvertisementFiles.GetSafePath(advertisement.ImageFileName);
                if (path is null || !File.Exists(path))
                    continue;

                var fileName = Uri.EscapeDataString(Path.GetFileName(path));
                activeAdvertisements.Add($"https://{AdvertisementVirtualHost}/{fileName}");
            }
            catch (Exception ex)
            {
                KioskLog.Write("Advertisement display error: " + ex.GetType().Name + " - " + ex.Message);
            }
        }
        KioskLog.Write($"Thank-you advertisement evaluation at {effectiveNow:O}: " +
            $"{activeAdvertisements.Count} active image(s) displayed.");

        var hasAdvertisements = activeAdvertisements.Count > 0;
        var advertisementSlides = new StringBuilder();
        var advertisementDots = new StringBuilder();
        for (var index = 0; index < activeAdvertisements.Count; index++)
        {
            var activeClass = index == 0 ? " active" : string.Empty;
            advertisementSlides.Append($"<figure class=\"ad-slide{activeClass}\" data-slide=\"{index}\">" +
                $"<img src=\"{activeAdvertisements[index]}\" alt=\"Mullet Hop special\"></figure>");
            advertisementDots.Append($"<span class=\"ad-dot{activeClass}\" aria-hidden=\"true\"></span>");
        }

        var advertisementPanel = hasAdvertisements
            ? $$"""
                <aside class="ad-panel" aria-label="Mullet Hop specials">
                  <div class="ad-stripe"></div>
                  <div class="ad-heading">
                    <span class="ad-kicker">DON'T MISS</span>
                    <h2>Today's Specials</h2>
                  </div>
                  <div class="ad-stage">{{advertisementSlides}}</div>
                  <div class="ad-dots">{{advertisementDots}}</div>
                </aside>
                """
            : string.Empty;
        var bodyClass = (hasAdvertisements ? "with-ads" : "no-ads") +
                        (_lastDarkTheme ? " dark-theme" : string.Empty);

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Waiver Complete | Mullet Hop</title>
              <link rel="preconnect" href="https://fonts.googleapis.com">
              <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
              <link href="https://fonts.googleapis.com/css2?family=Open+Sans:wght@400;600;700;800&amp;display=swap" rel="stylesheet">
              <style>
                :root {
                  --lime: #76c442;
                  --aqua: #00a4d6;
                  --blue: #0877bd;
                  --purple: #75449a;
                  --orange: #f58220;
                  --ink: #101820;
                  --paper: #ffffff;
                }
                * { box-sizing: border-box; }
                html, body { width: 100%; height: 100%; margin: 0; overflow: hidden; }
                body {
                  font-family: 'Open Sans', Arial, sans-serif;
                  color: var(--ink);
                  background-color: #eefaff;
                  background-image:
                    linear-gradient(rgba(247,252,255,.18), rgba(247,252,255,.18)),
                    url('{{backgroundUrl}}');
                  background-position: center;
                  background-size: cover;
                  background-repeat: no-repeat;
                  background-attachment: fixed;
                  display: grid;
                  place-items: center;
                  padding: 26px;
                }
                .thank-layout {
                  width: min(960px, 94vw);
                }
                .with-ads .thank-layout {
                  width: min(1660px, 96vw);
                  display: grid;
                  grid-template-columns: minmax(600px, 1.15fr) minmax(360px, .85fr);
                  align-items: center;
                  gap: clamp(20px, 2.2vw, 38px);
                }
                .card {
                  position: relative;
                  width: 100%;
                  max-height: 94vh;
                  background: var(--paper);
                  border: 4px solid var(--ink);
                  border-radius: 30px;
                  box-shadow: 0 22px 55px rgba(16,24,32,.24);
                  overflow: hidden;
                  text-align: center;
                }
                .stripe {
                  height: 17px;
                  background: linear-gradient(90deg, var(--lime) 0 25%, var(--aqua) 25% 50%, var(--purple) 50% 75%, var(--orange) 75% 100%);
                }
                .content { padding: clamp(24px, 4vh, 48px) clamp(30px, 6vw, 76px) 28px; }
                .logo-wrap { min-height: 105px; display: grid; place-items: center; margin-bottom: 8px; }
                .logo { display: block; width: min(400px, 70vw); max-height: 150px; object-fit: contain; }
                .logo-fallback {
                  display: none;
                  font-size: clamp(34px, 5vw, 62px);
                  line-height: 1;
                  font-weight: 800;
                  letter-spacing: -2px;
                  color: var(--blue);
                  -webkit-text-stroke: 2px var(--ink);
                }
                .check {
                  width: 86px;
                  height: 86px;
                  margin: 6px auto 12px;
                  border-radius: 50%;
                  display: grid;
                  place-items: center;
                  background: var(--lime);
                  border: 4px solid var(--ink);
                  color: var(--ink);
                  font-size: 55px;
                  line-height: 1;
                  font-weight: 800;
                  box-shadow: 0 8px 0 rgba(16,24,32,.12);
                }
                h1 {
                  margin: 0;
                  font-size: clamp(42px, 6.2vw, 76px);
                  line-height: 1;
                  font-weight: 800;
                  letter-spacing: -2px;
                  text-transform: uppercase;
                  color: var(--purple);
                }
                .complete {
                  margin: 12px 0 22px;
                  font-size: clamp(20px, 2.2vw, 29px);
                  font-weight: 700;
                  color: var(--blue);
                }
                .next-step {
                  margin: 0 auto;
                  max-width: 760px;
                  padding: 22px 26px;
                  border-radius: 18px;
                  background: #fff6e9;
                  border: 3px solid var(--orange);
                  font-size: clamp(20px, 2.2vw, 29px);
                  line-height: 1.35;
                  font-weight: 700;
                }
                .next-step strong { color: #b64c00; }
                .countdown {
                  margin: 23px 0 0;
                  font-size: 16px;
                  color: #53616d;
                  font-weight: 600;
                }
                .countdown-number { color: var(--purple); font-weight: 800; }
                .ad-panel {
                  width: 100%;
                  max-height: 94vh;
                  background: var(--paper);
                  border: 4px solid var(--ink);
                  border-radius: 30px;
                  box-shadow: 0 22px 55px rgba(16,24,32,.24);
                  overflow: hidden;
                }
                .ad-stripe {
                  height: 17px;
                  background: linear-gradient(90deg, var(--orange) 0 38%, var(--purple) 38% 68%, var(--aqua) 68% 100%);
                }
                .ad-heading {
                  padding: 18px 24px 12px;
                  text-align: center;
                }
                .ad-kicker {
                  display: inline-block;
                  margin-bottom: 3px;
                  padding: 4px 12px;
                  border: 2px solid var(--orange);
                  border-radius: 999px;
                  color: #b64c00;
                  background: #fff6e9;
                  font-size: 13px;
                  font-weight: 800;
                  letter-spacing: 1.5px;
                }
                .ad-heading h2 {
                  margin: 4px 0 0;
                  color: var(--purple);
                  font-size: clamp(26px, 2.5vw, 40px);
                  line-height: 1.05;
                  text-transform: uppercase;
                }
                .ad-stage {
                  position: relative;
                  height: min(65vh, 700px);
                  margin: 0 20px;
                  overflow: hidden;
                  border: 3px solid var(--aqua);
                  border-radius: 20px;
                  background: #f4fbfe;
                }
                .ad-slide {
                  position: absolute;
                  inset: 0;
                  margin: 0;
                  display: grid;
                  grid-template-rows: minmax(0, 1fr);
                  opacity: 0;
                  transform: translateX(18px);
                  transition: opacity .45s ease, transform .45s ease;
                  pointer-events: none;
                }
                .ad-slide.active {
                  opacity: 1;
                  transform: translateX(0);
                }
                .ad-slide img {
                  width: 100%;
                  height: 100%;
                  min-height: 0;
                  padding: 10px;
                  object-fit: contain;
                }
                .ad-dots {
                  min-height: 29px;
                  padding: 10px 18px 12px;
                  display: flex;
                  justify-content: center;
                  gap: 8px;
                }
                .ad-dot {
                  width: 10px;
                  height: 10px;
                  border-radius: 50%;
                  background: #c9d5db;
                  transition: background .3s ease, transform .3s ease;
                }
                .ad-dot.active { background: var(--purple); transform: scale(1.25); }
                .with-ads .content { padding-left: clamp(26px, 3.5vw, 56px); padding-right: clamp(26px, 3.5vw, 56px); }
                .with-ads h1 { font-size: clamp(42px, 4.4vw, 68px); }
                .with-ads .logo { width: min(350px, 64vw); }
                body.dark-theme {
                  color: #edf3f7;
                  background-color: #111820;
                  background-image:
                    linear-gradient(rgba(10,16,23,.78), rgba(10,16,23,.78)),
                    url('{{backgroundUrl}}');
                }
                body.dark-theme .card,
                body.dark-theme .ad-panel {
                  color: #edf3f7;
                  background: #1b242e;
                  border-color: #d6e1e8;
                  box-shadow: 0 22px 55px rgba(0,0,0,.5);
                }
                body.dark-theme h1,
                body.dark-theme .ad-heading h2,
                body.dark-theme .countdown-number { color: #d3a4ee; }
                body.dark-theme .complete { color: #5bc6f0; }
                body.dark-theme .next-step,
                body.dark-theme .ad-kicker {
                  color: #edf3f7;
                  background: #3a3025;
                }
                body.dark-theme .next-step strong,
                body.dark-theme .ad-kicker { color: #ffb36c; }
                body.dark-theme .countdown { color: #b1bec9; }
                body.dark-theme .ad-stage { background: #111820; }
                @media (max-height: 700px) {
                  .content { padding-top: 18px; padding-bottom: 18px; }
                  .logo-wrap { min-height: 80px; }
                  .logo { max-height: 105px; }
                  .check { width: 68px; height: 68px; font-size: 44px; margin-bottom: 8px; }
                  .complete { margin-bottom: 14px; }
                  .next-step { padding: 15px 22px; }
                  .countdown { margin-top: 14px; }
                  .ad-heading { padding-top: 11px; padding-bottom: 8px; }
                  .ad-stage { height: 59vh; }
                  .ad-dots { padding-top: 7px; padding-bottom: 8px; }
                }
                @media (max-width: 1050px) {
                  html, body { min-height: 100%; height: auto; overflow: auto; }
                  .with-ads .thank-layout {
                    width: min(780px, 94vw);
                    grid-template-columns: 1fr;
                    padding: 22px 0;
                  }
                  .ad-stage { height: min(68vh, 650px); }
                }
              </style>
            </head>
            <body class="{{bodyClass}}">
              <div class="thank-layout">
                <main class="card" aria-labelledby="thank-you-heading">
                  <div class="stripe"></div>
                  <div class="content">
                    <div class="logo-wrap">
                      <img class="logo" src="{{LogoUrl}}" alt="Mullet Hop Trampoline Park"
                           onerror="this.style.display='none';document.getElementById('logo-fallback').style.display='block'">
                      <div class="logo-fallback" id="logo-fallback">MULLET HOP</div>
                    </div>
                    <div class="check" aria-hidden="true">✓</div>
                    <h1 id="thank-you-heading">Thank You!</h1>
                    <p class="complete">Your waiver has been successfully completed.</p>
                    <p class="next-step">
                      You can now see a staff member at the <strong>front desk</strong>
                      to purchase your <strong>jump pass and socks.</strong>
                    </p>
                    <p class="countdown">
                      This kiosk will be ready for the next guest in
                      <span class="countdown-number" id="seconds">{{resetSeconds}}</span> seconds.
                    </p>
                  </div>
                </main>
                {{advertisementPanel}}
              </div>
              <script>
                let remaining = {{resetSeconds}};
                const seconds = document.getElementById('seconds');
                setInterval(() => {
                  remaining = Math.max(0, remaining - 1);
                  seconds.textContent = String(remaining);
                }, 1000);

                const slides = Array.from(document.querySelectorAll('.ad-slide'));
                const dots = Array.from(document.querySelectorAll('.ad-dot'));
                let currentSlide = 0;
                if (slides.length > 1) {
                  setInterval(() => {
                    slides[currentSlide].classList.remove('active');
                    dots[currentSlide]?.classList.remove('active');
                    currentSlide = (currentSlide + 1) % slides.length;
                    slides[currentSlide].classList.add('active');
                    dots[currentSlide]?.classList.add('active');
                  }, 5000);
                }
              </script>
            </body>
            </html>
            """;
    }

    private async void ShowStaffExitPrompt()
    {
        if (_promptOpen)
            return;

        _promptOpen = true;
        UpdateAssistancePanelState();
        _idleTimer.Stop();
        TopMost = false;

        try
        {
            using var dialog = new PinEntryDialog(_lastDarkTheme);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                if (_settings.VerifyPin(dialog.Pin))
                {
                    while (!_allowExit)
                    {
                        using var settingsDialog = new StaffSettingsDialog(
                            _settings,
                            _previewDateTime.HasValue ? GetEffectiveNow() : null,
                            progress => SyncAdvertisementsFromControllerAsync(progress),
                            PreviewBusinessClosedOverlayAsync);
                        var settingsResult = settingsDialog.ShowDialog(this);
                        await ApplyKioskThemeIfChangedAsync(force: true);
                        if (settingsResult != DialogResult.OK)
                        {
                            await ResetForNextGuestAsync(
                                "staff returned to kiosk", showStatus: false);
                            return;
                        }

                        switch (settingsDialog.SelectedAction)
                        {
                            case StaffSettingsAction.ExitToWindows:
                                _allowExit = true;
                                KioskLog.Write("Staff exit accepted.");
                                Close();
                                return;
                            case StaffSettingsAction.ReturnToKiosk:
                                _manualBusinessBlackout = false;
                                _settings.ManualBusinessBlackout = false;
                                _settings.Save();
                                await ResetForNextGuestAsync(
                                    "staff returned to kiosk", showStatus: false);
                                return;
                            case StaffSettingsAction.PreviewDateTime:
                                await EnableDateTimePreviewAsync(settingsDialog.SelectedDateTime);
                                continue;
                            case StaffSettingsAction.UseLiveDateTime:
                                await DisableDateTimePreviewAsync();
                                continue;
                            case StaffSettingsAction.PreviewThankYouPage:
                                ShowThankYouPage(
                                    staffPreview: true,
                                    scheduleTimeOverride: settingsDialog.SelectedDateTime);
                                return;
                            case StaffSettingsAction.ToggleStationClosed:
                                try
                                {
                                    await SetStationClosedAsync(
                                        !_settings.StationClosed,
                                        "staff settings");
                                }
                                catch (Exception ex)
                                {
                                    KioskLog.Write("Closed-page setting error: " +
                                        ex.GetType().Name + " - " + ex.Message);
                                    MessageBox.Show(settingsDialog,
                                        "The waiver station setting could not be saved.\n\n" + ex.Message,
                                        "Staff Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                }
                                continue;
                            case StaffSettingsAction.StartBusinessBlackout:
                                await SetBusinessClosedAsync(true, "staff settings");
                                KioskLog.Write("Staff started an immediate business-hours blackout.");
                                return;
                        }
                    }
                    return;
                }

                MessageBox.Show(dialog, "The staff password was not correct.", "Mullet Hop Waiver Kiosk",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                KioskLog.Write("Incorrect staff settings password entered.");
            }
        }
        finally
        {
            _promptOpen = false;
            UpdateAssistancePanelState();
            if (!_allowExit)
            {
                TopMost = true;
                Activate();
                _webView.Focus();
                if (!_showingBlackout)
                    MarkActivity();
                _idleTimer.Start();
                _ = ApplyBusinessHoursStateAsync();
            }
        }
    }

    private async Task EnableDateTimePreviewAsync(DateTime selectedDateTime)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_dateTimePreviewScriptId))
                _webView.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_dateTimePreviewScriptId);

            var script = BuildDateTimePreviewScript(selectedDateTime);
            _dateTimePreviewScriptId = await _webView.CoreWebView2
                .AddScriptToExecuteOnDocumentCreatedAsync(script);
            _previewDateTime = selectedDateTime;
            _previewStartedUtc = DateTime.UtcNow;
            _previewBanner.Text = "STAFF DATE/TIME PREVIEW — " +
                selectedDateTime.ToString("dddd, MMMM d, yyyy 'at' h:mm tt") +
                " — Press Ctrl + Alt + M to return to live time";
            _previewBanner.Visible = true;
            _previewBanner.BringToFront();
            await ResetForNextGuestAsync("staff date/time preview", showStatus: false);
            KioskLog.Write("Staff date/time preview enabled for " + selectedDateTime.ToString("O") + ".");
        }
        catch (Exception ex)
        {
            _dateTimePreviewScriptId = null;
            _previewDateTime = null;
            _previewStartedUtc = null;
            _previewBanner.Visible = false;
            KioskLog.Write("Date/time preview error: " + ex.GetType().Name + " - " + ex.Message);
            MessageBox.Show(this,
                "The date/time preview could not be started.\n\n" + ex.Message,
                "Staff Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task DisableDateTimePreviewAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_dateTimePreviewScriptId))
                _webView.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(_dateTimePreviewScriptId);
            _dateTimePreviewScriptId = null;
            _previewDateTime = null;
            _previewStartedUtc = null;
            _previewBanner.Visible = false;
            await ResetForNextGuestAsync("return to live date and time", showStatus: false);
            KioskLog.Write("Staff date/time preview disabled.");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Date/time preview reset error: " + ex.GetType().Name + " - " + ex.Message);
            MessageBox.Show(this,
                "The live date and time could not be restored. Restart the kiosk before allowing another guest to use it.\n\n" + ex.Message,
                "Staff Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string BuildDateTimePreviewScript(DateTime selectedDateTime)
    {
        var localValue = DateTime.SpecifyKind(selectedDateTime, DateTimeKind.Unspecified);
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(localValue);
        var previewTimestamp = new DateTimeOffset(localValue, localOffset).ToUnixTimeMilliseconds();
        return $$"""
            (() => {
              const RealDate = window.Date;
              const previewStart = {{previewTimestamp}};
              const realStart = RealDate.now();
              const previewNow = () => previewStart + (RealDate.now() - realStart);
              function PreviewDate(...args) {
                if (!(this instanceof PreviewDate)) return new RealDate(previewNow()).toString();
                return args.length === 0 ? new RealDate(previewNow()) : new RealDate(...args);
              }
              Object.setPrototypeOf(PreviewDate, RealDate);
              PreviewDate.prototype = RealDate.prototype;
              PreviewDate.now = previewNow;
              PreviewDate.parse = RealDate.parse;
              PreviewDate.UTC = RealDate.UTC;
              window.Date = PreviewDate;
              window.__mulletHopPreviewDateTime = new RealDate(previewStart).toISOString();
            })();
            """;
    }

    private DateTime GetEffectiveNow()
    {
        if (!_previewDateTime.HasValue || !_previewStartedUtc.HasValue)
            return DateTime.Now;

        return _previewDateTime.Value + (DateTime.UtcNow - _previewStartedUtc.Value);
    }

    private void ShowBanner(string text, bool success)
    {
        _banner.Text = text;
        _banner.BackColor = success ? Color.FromArgb(126, 217, 87) : Color.FromArgb(255, 222, 89);
        _banner.Visible = true;
        _banner.BringToFront();
    }

    private void HideBanner() => _banner.Visible = false;

    private const string ActivityAndCompletionScript = """
        (() => {
          if (window.__mulletHopKioskInstalled) return;
          window.__mulletHopKioskInstalled = true;
          const kioskLogoSource = '__MULLET_HOP_LOGO_DATA_URL__';
          const providerLogoSource = '__MULLET_HOP_PROVIDER_LOGO_DATA_URL__';
          const kioskBackgroundSource = '__MULLET_HOP_BACKGROUND_URL__';
          let kioskDarkMode = __MULLET_HOP_DARK_MODE__;
          const assistanceSession = '__MULLET_HOP_ASSISTANCE_SESSION__';
          let assistanceRequested = __MULLET_HOP_ASSISTANCE_REQUESTED__;
          let assistanceAcknowledged = __MULLET_HOP_ASSISTANCE_ACKNOWLEDGED__;

          try {
            const savedAssistance = JSON.parse(
              sessionStorage.getItem('mullet-hop-assistance-state') || 'null');
            if (savedAssistance?.session === assistanceSession) {
              assistanceRequested = Boolean(savedAssistance.requested);
              assistanceAcknowledged = assistanceRequested && Boolean(savedAssistance.acknowledged);
            }
          }
          catch { }

          const setAssistanceText = (element, text) => {
            if (element && element.textContent !== text) element.textContent = text;
          };
          const updateAssistanceCard = () => {
            const card = document.getElementById('mullet-hop-assistance-card');
            if (!card) return;

            card.classList.toggle('is-requested', assistanceRequested);
            card.classList.toggle('is-acknowledged', assistanceRequested && assistanceAcknowledged);
            setAssistanceText(
              card.querySelector('h2'),
              assistanceRequested
                ? assistanceAcknowledged ? 'Assistance Is on the Way' : 'Assistance Requested'
                : 'Need Assistance?');
            setAssistanceText(
              card.querySelector('.mullet-hop-assistance-message'),
              assistanceRequested
                ? assistanceAcknowledged
                  ? 'A staff member received your request and is on the way to help.'
                  : 'A staff member has been notified. Please remain at this waiver station.'
                : 'Select the button below if you need help completing the waiver.');
            setAssistanceText(
              card.querySelector('button'),
              assistanceRequested ? 'Clear Assistance Call' : 'Call for Assistance');
          };
          window.__mulletHopSetAssistanceState = (requested, acknowledged) => {
            assistanceRequested = Boolean(requested);
            assistanceAcknowledged = assistanceRequested && Boolean(acknowledged);
            try {
              sessionStorage.setItem('mullet-hop-assistance-state', JSON.stringify({
                session: assistanceSession,
                requested: assistanceRequested,
                acknowledged: assistanceAcknowledged
              }));
            }
            catch { }
            updateAssistanceCard();
          };
          const ensureAssistanceCard = tools => {
            let card = document.getElementById('mullet-hop-assistance-card');
            if (!card) {
              card = document.createElement('aside');
              card.id = 'mullet-hop-assistance-card';
              card.className = 'mullet-hop-action-card';
              card.setAttribute('aria-label', 'Request staff assistance');
              card.innerHTML = `
                <div class='mullet-hop-assistance-light' aria-hidden='true'></div>
                <h2>Need Assistance?</h2>
                <p class='mullet-hop-assistance-message'>Select the button below if you need help completing the waiver.</p>
                <button type='button' id='mullet-hop-assistance-button'>Call for Assistance</button>
              `;
              card.querySelector('button').addEventListener('click', () => {
                window.chrome.webview.postMessage(
                  assistanceRequested ? 'assistance-clear' : 'assistance-request');
              });
              tools.appendChild(card);
            }
            updateAssistanceCard();
          };

          let lastActivityMessage = 0;
          const postActivity = () => {
            const now = Date.now();
            if (now - lastActivityMessage > 750) {
              lastActivityMessage = now;
              window.chrome.webview.postMessage('activity');
            }
          };

          ['pointerdown', 'keydown', 'input', 'change', 'touchstart', 'wheel']
            .forEach(name => window.addEventListener(name, postActivity, { capture: true, passive: true }));

          const isWaiverStartPage = () => {
            if (!document.body) return false;
            const pageText = (document.body.innerText || '').toLowerCase();
            return pageText.includes('please enter your email address to start waiver') &&
                   pageText.includes('waiver is for');
          };

          const findEmailInput = () =>
            document.querySelector("input[type='email']") ||
            document.querySelector("input[name*='email' i]") ||
            document.querySelector("input[id*='email' i]");

          const parseCssColor = value => {
            const match = String(value || '').match(/rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)(?:\s*[,/]\s*([\d.]+))?\s*\)/i);
            if (!match) return null;
            return {
              r: Number(match[1]), g: Number(match[2]), b: Number(match[3]),
              a: match[4] === undefined ? 1 : Number(match[4])
            };
          };
          const colorLuminance = color => {
            const channel = value => {
              const component = value / 255;
              return component <= .04045 ? component / 12.92 : Math.pow((component + .055) / 1.055, 2.4);
            };
            return .2126 * channel(color.r) + .7152 * channel(color.g) + .0722 * channel(color.b);
          };
          const contrastRatio = (first, second) => {
            const lighter = Math.max(colorLuminance(first), colorLuminance(second));
            const darker = Math.min(colorLuminance(first), colorLuminance(second));
            return (lighter + .05) / (darker + .05);
          };
          const effectiveBackground = element => {
            for (let current = element; current; current = current.parentElement) {
              const color = parseCssColor(getComputedStyle(current).backgroundColor);
              if (color && color.a >= .5) return color;
            }
            return { r: 17, g: 24, b: 32, a: 1 };
          };
          const restoreContrastFixes = () => {
            document.querySelectorAll('[data-mullet-hop-contrast-fixed]').forEach(element => {
              const original = element.dataset.mulletHopOriginalColor || '';
              const priority = element.dataset.mulletHopOriginalColorPriority || '';
              if (original) element.style.setProperty('color', original, priority);
              else element.style.removeProperty('color');
              delete element.dataset.mulletHopContrastFixed;
              delete element.dataset.mulletHopOriginalColor;
              delete element.dataset.mulletHopOriginalColorPriority;
            });
          };
          const repairDarkContrast = () => {
            if (!document.body?.classList.contains('mullet-hop-dark-theme')) {
              restoreContrastFixes();
              return;
            }
            document.querySelectorAll('body, body *').forEach(element => {
              if (element.matches('script, style, img, svg, path, canvas, video, source')) return;
              const style = getComputedStyle(element);
              if (style.display === 'none' || style.visibility === 'hidden') return;
              const foreground = parseCssColor(style.color);
              if (!foreground) return;
              const background = effectiveBackground(element);
              if (contrastRatio(foreground, background) >= 4.5) return;
              if (!element.dataset.mulletHopContrastFixed) {
                element.dataset.mulletHopContrastFixed = '1';
                element.dataset.mulletHopOriginalColor = element.style.getPropertyValue('color');
                element.dataset.mulletHopOriginalColorPriority = element.style.getPropertyPriority('color');
              }
              element.style.setProperty(
                'color', colorLuminance(background) < .36 ? '#f5f8fa' : '#101820', 'important');
            });
          };
          let contrastRepairQueued = false;
          const scheduleDarkContrastRepair = () => {
            if (contrastRepairQueued) return;
            contrastRepairQueued = true;
            requestAnimationFrame(() => {
              contrastRepairQueued = false;
              repairDarkContrast();
            });
          };
          window.__mulletHopRepairDarkContrast = scheduleDarkContrastRepair;
          window.__mulletHopSetDarkMode = dark => {
            kioskDarkMode = Boolean(dark);
            document.body?.classList.toggle('dark-theme', kioskDarkMode);
            document.body?.classList.toggle('mullet-hop-dark-theme', kioskDarkMode);
            scheduleDarkContrastRepair();
          };

          const radioDescription = input => {
            const parts = [input.value, input.getAttribute('aria-label'), input.getAttribute('title')];
            if (input.id) {
              const explicitLabel = Array.from(document.querySelectorAll('label'))
                .find(label => label.htmlFor === input.id);
              if (explicitLabel) parts.push(explicitLabel.innerText);
            }
            parts.push(input.closest('label')?.innerText);
            if (input.parentElement?.tagName === 'SPAN') parts.push(input.parentElement.innerText);
            return parts.filter(Boolean).join(' ').toLowerCase().replace(/\s+/g, ' ');
          };

          const normalizeWaiverChoice = input => {
            const description = radioDescription(input);
            if (/me\s+and\s+my\s+kids|kid|child|minor|family|guardian/.test(description)) return 'family';
            if (/just\s+me|myself|adult|individual/.test(description)) return 'just-me';
            const widerDescription = [input.parentElement?.innerText, input.closest('tr')?.innerText]
              .filter(Boolean).join(' ').toLowerCase().replace(/\s+/g, ' ');
            const mentionsFamily = /me\s+and\s+my\s+kids|kid|child|minor|family|guardian/.test(widerDescription);
            const mentionsJustMe = /just\s+me|myself|adult|individual/.test(widerDescription);
            if (mentionsFamily && !mentionsJustMe) return 'family';
            if (mentionsJustMe && !mentionsFamily) return 'just-me';
            const allRadios = Array.from(document.querySelectorAll("input[type='radio']"));
            const sameGroup = input.name ? allRadios.filter(radio => radio.name === input.name) : [];
            const radios = sameGroup.length === 2 ? sameGroup : allRadios;
            if (radios.length === 2) return radios.indexOf(input) === 0 ? 'just-me' : 'family';
            return '';
          };

          const rememberStartingChoice = () => {
            if (!isWaiverStartPage()) return;
            const emailInput = findEmailInput();
            const selected = Array.from(document.querySelectorAll("input[type='radio']"))
              .find(input => input.checked && normalizeWaiverChoice(input));
            const email = (emailInput?.value || '').trim();
            const choice = selected ? normalizeWaiverChoice(selected) : '';
            if (email) sessionStorage.setItem('mullet-hop-waiver-email', email);
            if (choice) sessionStorage.setItem('mullet-hop-waiver-choice', choice);
            if (email && choice) {
              window.chrome.webview.postMessage(JSON.stringify({
                type: 'remember-waiver-choice',
                email,
                choice
              }));
            }
          };

          document.addEventListener('change', rememberStartingChoice, true);
          document.addEventListener('submit', rememberStartingChoice, true);
          document.addEventListener('click', event => {
            if (event.target.closest("button, a, input[type='submit'], input[type='button'], input[type='image']"))
              rememberStartingChoice();
          }, true);

          const showWaiverSwitchGuidance = (targetRadio, choice) => {
            document.getElementById('mullet-hop-switch-guidance')?.remove();
            const targetLabel = choice === 'family' ? 'Me and My Kids!' : 'Just Me';
            const targetHolder = targetRadio.closest('label') || targetRadio.parentElement || targetRadio;
            targetRadio.scrollIntoView({ block: 'center', inline: 'nearest' });
            targetRadio.classList.add('mullet-hop-switch-target-radio');
            targetHolder.classList.add('mullet-hop-switch-target-choice');

            const guidance = document.createElement('div');
            guidance.id = 'mullet-hop-switch-guidance';
            guidance.setAttribute('role', 'status');
            guidance.innerHTML = `
              <div class='mullet-hop-guidance-arrow' aria-hidden='true'>←</div>
              <div class='mullet-hop-guidance-box'>
                <strong>New option: “${targetLabel}”</strong>
                <span>This option has been selected. The waiver will continue automatically.</span>
              </div>
            `;
            document.body.appendChild(guidance);

            const positionGuidance = () => {
              const rect = targetHolder.getBoundingClientRect();
              const guidanceWidth = Math.min(370, Math.max(160, window.innerWidth - 24));
              guidance.style.width = `${guidanceWidth}px`;
              const guidanceHeight = guidance.offsetHeight;
              const arrow = guidance.querySelector('.mullet-hop-guidance-arrow');
              const rightPosition = rect.right + 22;
              const leftPosition = rect.left - guidanceWidth - 22;

              if (rightPosition + guidanceWidth <= window.innerWidth - 12) {
                guidance.dataset.direction = 'right';
                arrow.textContent = '←';
                guidance.style.left = `${rightPosition}px`;
                guidance.style.top = `${Math.max(12, Math.min(window.innerHeight - guidanceHeight - 12, rect.top + (rect.height - guidanceHeight) / 2))}px`;
              }
              else if (leftPosition >= 12) {
                guidance.dataset.direction = 'left';
                arrow.textContent = '→';
                guidance.style.left = `${leftPosition}px`;
                guidance.style.top = `${Math.max(12, Math.min(window.innerHeight - guidanceHeight - 12, rect.top + (rect.height - guidanceHeight) / 2))}px`;
              }
              else {
                const centeredLeft = Math.max(12, Math.min(window.innerWidth - guidanceWidth - 12, rect.left + rect.width / 2 - guidanceWidth / 2));
                guidance.style.left = `${centeredLeft}px`;
                if (rect.bottom + guidanceHeight + 16 <= window.innerHeight) {
                  guidance.dataset.direction = 'below';
                  arrow.textContent = '↑';
                  guidance.style.top = `${rect.bottom + 10}px`;
                }
                else {
                  guidance.dataset.direction = 'above';
                  arrow.textContent = '↓';
                  guidance.style.top = `${Math.max(12, rect.top - guidanceHeight - 10)}px`;
                }
              }
              guidance.classList.add('is-visible');
            };
            requestAnimationFrame(positionGuidance);
          };

          window.__mulletHopApplyWaiverSwitch = (email, choice) => {
            let attempts = 0;
            let finished = false;
            const timer = setInterval(() => {
              attempts += 1;
              if (finished) return;
              if (!isWaiverStartPage()) {
                if (attempts < 50) return;
              }
              else {
                const emailInput = findEmailInput();
                const radios = Array.from(document.querySelectorAll("input[type='radio']"));
                const targetRadio = radios.find(input => normalizeWaiverChoice(input) === choice);
                if (emailInput && targetRadio) {
                  const valueSetter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
                  if (valueSetter) valueSetter.call(emailInput, email);
                  else emailInput.value = email;
                  emailInput.dispatchEvent(new Event('input', { bubbles: true }));
                  emailInput.dispatchEvent(new Event('change', { bubbles: true }));

                  targetRadio.click();
                  targetRadio.checked = true;
                  targetRadio.dispatchEvent(new Event('change', { bubbles: true }));
                  sessionStorage.setItem('mullet-hop-waiver-email', email);
                  sessionStorage.setItem('mullet-hop-waiver-choice', choice);
                  showWaiverSwitchGuidance(targetRadio, choice);

                  const form = emailInput.form || targetRadio.form || document.querySelector('form');
                  const controls = form
                    ? Array.from(form.querySelectorAll("button, a, input[type='submit'], input[type='button'], input[type='image']"))
                    : [];
                  const continueControl = controls.find(control =>
                    /continue|next|start/.test((control.innerText || control.value || '').toLowerCase())) ||
                    controls.find(control => control.type === 'submit') || controls[0];

                  finished = true;
                  clearInterval(timer);
                  window.chrome.webview.postMessage(JSON.stringify({ type: 'switch-applied' }));
                  setTimeout(() => {
                    if (continueControl) continueControl.click();
                    else if (form?.requestSubmit) form.requestSubmit();
                    else form?.submit();
                  }, 3000);
                  return;
                }
              }

              if (attempts >= 50) {
                finished = true;
                clearInterval(timer);
                window.chrome.webview.postMessage(JSON.stringify({ type: 'switch-failed' }));
              }
            }, 160);
          };

          const repairProviderLogo = () => {
            const logo = document.querySelector('div.headings > img');
            if (!logo || logo.dataset.mulletHopLogoRepaired === '1') return;

            logo.dataset.mulletHopLogoRepaired = '1';
            const showFallback = () => {
              const holder = logo.parentElement;
              if (!holder) return;
              logo.remove();
              if (holder.querySelector('#mullet-hop-provider-logo-fallback')) return;
              const fallback = document.createElement('div');
              fallback.id = 'mullet-hop-provider-logo-fallback';
              fallback.textContent = 'MULLET HOP';
              holder.insertBefore(fallback, holder.firstChild);
            };

            if (!providerLogoSource) {
              showFallback();
              return;
            }

            logo.id = 'mullet-hop-provider-logo';
            logo.alt = 'Mullet Hop fish logo';
            logo.removeAttribute('width');
            logo.removeAttribute('height');
            logo.removeAttribute('srcset');
            logo.addEventListener('error', showFallback, { once: true });
            logo.src = providerLogoSource;
          };

          const installSignatureTouchBridge = canvas => {
            if (!(canvas instanceof HTMLCanvasElement) ||
                canvas.dataset.mulletHopSignatureTouch === '1') return;

            canvas.dataset.mulletHopSignatureTouch = '1';
            canvas.style.setProperty('touch-action', 'none', 'important');
            canvas.style.setProperty('-ms-touch-action', 'none', 'important');
            canvas.style.setProperty('user-select', 'none', 'important');
            canvas.style.setProperty('-webkit-user-select', 'none', 'important');

            let activePointerId = null;

            const dispatchMouseEvent = (name, pointerEvent) => {
              const samples = name === 'mousemove' && pointerEvent.getCoalescedEvents
                ? pointerEvent.getCoalescedEvents()
                : [pointerEvent];
              const events = samples.length ? samples : [pointerEvent];

              events.forEach(sample => {
                canvas.dispatchEvent(new MouseEvent(name, {
                  bubbles: true,
                  cancelable: true,
                  view: window,
                  detail: 1,
                  screenX: sample.screenX,
                  screenY: sample.screenY,
                  clientX: sample.clientX,
                  clientY: sample.clientY,
                  ctrlKey: sample.ctrlKey,
                  altKey: sample.altKey,
                  shiftKey: sample.shiftKey,
                  metaKey: sample.metaKey,
                  button: 0,
                  buttons: name === 'mouseup' ? 0 : 1
                }));
              });
            };

            const isTouchOrPen = event =>
              event.pointerType === 'touch' || event.pointerType === 'pen';

            canvas.addEventListener('pointerdown', event => {
              if (!isTouchOrPen(event) || activePointerId !== null) return;
              activePointerId = event.pointerId;
              event.preventDefault();
              canvas.setPointerCapture?.(event.pointerId);
              dispatchMouseEvent('mousedown', event);
            }, { capture: true, passive: false });

            canvas.addEventListener('pointermove', event => {
              if (!isTouchOrPen(event) || event.pointerId !== activePointerId) return;
              event.preventDefault();
              dispatchMouseEvent('mousemove', event);
            }, { capture: true, passive: false });

            const finishStroke = event => {
              if (!isTouchOrPen(event) || event.pointerId !== activePointerId) return;
              event.preventDefault();
              dispatchMouseEvent('mouseup', event);
              if (canvas.hasPointerCapture?.(event.pointerId))
                canvas.releasePointerCapture(event.pointerId);
              activePointerId = null;
            };

            canvas.addEventListener('pointerup', finishStroke,
              { capture: true, passive: false });
            canvas.addEventListener('pointercancel', finishStroke,
              { capture: true, passive: false });
          };

          const applyWaiverTheme = () => {
            if (!document.body) return;
            if (location.hostname.toLowerCase() !== 'mullet.lilypadpos.app' ||
                !location.pathname.toLowerCase().startsWith('/public/onlinewaiver/')) return;

            if (!document.getElementById('mullet-hop-waiver-theme')) {
              const style = document.createElement('style');
              style.id = 'mullet-hop-waiver-theme';
              style.textContent = `
              @import url('https://fonts.googleapis.com/css2?family=Open+Sans:wght@400;600;700;800&display=swap');
              html {
                min-height: 100%;
                background: #eefaff;
              }
              body.mullet-hop-waiver-themed {
                min-height: 100vh;
                margin: 0 !important;
                padding: clamp(22px, 4vw, 48px) 24px 80px !important;
                box-sizing: border-box;
                background-color: #eefaff !important;
                background-image:
                  linear-gradient(rgba(247,252,255,.18), rgba(247,252,255,.18)),
                  url('${kioskBackgroundSource}') !important;
                background-position: center top !important;
                background-size: cover !important;
                background-repeat: no-repeat !important;
                background-attachment: fixed !important;
                color: #101820 !important;
                font-family: 'Open Sans', Arial, sans-serif !important;
                font-size: 17px;
                line-height: 1.5;
              }
              body.mullet-hop-waiver-themed.mullet-hop-has-side-tools {
                padding-right: 390px !important;
              }
              body.mullet-hop-waiver-themed *,
              body.mullet-hop-waiver-themed *::before,
              body.mullet-hop-waiver-themed *::after {
                box-sizing: border-box;
              }
              body.mullet-hop-waiver-themed .mullet-hop-form-card {
                display: block;
                width: min(940px, 100%) !important;
                max-width: 940px !important;
                min-width: 0 !important;
                margin: 0 auto !important;
                padding: clamp(24px, 4vw, 48px) !important;
                overflow: hidden;
                background: rgba(255,255,255,.98) !important;
                border: 3px solid #101820 !important;
                border-top: 14px solid #76c442 !important;
                border-radius: 24px !important;
                box-shadow: 0 18px 45px rgba(16,24,32,.20) !important;
                color: #101820 !important;
              }
              body.mullet-hop-waiver-themed table.mullet-hop-form-card {
                display: table;
                border-collapse: separate !important;
                border-spacing: 0 !important;
              }
              body.mullet-hop-waiver-themed table.mullet-hop-form-card > tbody > tr > td {
                padding: clamp(22px, 4vw, 44px) !important;
              }
              body.mullet-hop-waiver-themed .mullet-hop-form-card form,
              body.mullet-hop-waiver-themed .mullet-hop-form-card table,
              body.mullet-hop-waiver-themed .mullet-hop-form-card tbody,
              body.mullet-hop-waiver-themed .mullet-hop-form-card tr,
              body.mullet-hop-waiver-themed .mullet-hop-form-card td {
                max-width: 100% !important;
              }
              body.mullet-hop-waiver-themed .mullet-hop-form-card table:not(.mullet-hop-form-card) {
                width: 100% !important;
                border-collapse: separate !important;
                border-spacing: 0 8px !important;
              }
              body.mullet-hop-waiver-themed .mullet-hop-form-card td {
                padding: 6px 8px !important;
                vertical-align: middle !important;
              }
              body.mullet-hop-waiver-themed h1,
              body.mullet-hop-waiver-themed h2,
              body.mullet-hop-waiver-themed h3 {
                margin-top: 0;
                font-family: 'Open Sans', Arial, sans-serif !important;
                font-weight: 800 !important;
                line-height: 1.16 !important;
              }
              body.mullet-hop-waiver-themed h1,
              body.mullet-hop-waiver-themed h2 { color: #75449a !important; }
              body.mullet-hop-waiver-themed h3 { color: #0877bd !important; }
              body.mullet-hop-waiver-themed p,
              body.mullet-hop-waiver-themed label,
              body.mullet-hop-waiver-themed td,
              body.mullet-hop-waiver-themed span,
              body.mullet-hop-waiver-themed div {
                font-family: 'Open Sans', Arial, sans-serif;
              }
              body.mullet-hop-waiver-themed label { font-weight: 700; }
              body.mullet-hop-waiver-themed input:not([type='radio']):not([type='checkbox']):not([type='submit']):not([type='button']):not([type='reset']):not([type='hidden']):not([type='image']),
              body.mullet-hop-waiver-themed select,
              body.mullet-hop-waiver-themed textarea {
                width: min(540px, 100%) !important;
                max-width: 100% !important;
                min-height: 48px !important;
                padding: 10px 12px !important;
                background: #fbfdff !important;
                color: #101820 !important;
                border: 2px solid #aebfca !important;
                border-radius: 10px !important;
                outline: none !important;
                font: 600 17px/1.3 'Open Sans', Arial, sans-serif !important;
                box-shadow: inset 0 1px 2px rgba(16,24,32,.06) !important;
              }
              body.mullet-hop-waiver-themed textarea {
                min-height: 112px !important;
                resize: vertical;
              }
              body.mullet-hop-waiver-themed input:focus,
              body.mullet-hop-waiver-themed select:focus,
              body.mullet-hop-waiver-themed textarea:focus {
                border-color: #00a4d6 !important;
                box-shadow: 0 0 0 4px rgba(0,164,214,.18) !important;
              }
              body.mullet-hop-waiver-themed input[type='radio'],
              body.mullet-hop-waiver-themed input[type='checkbox'] {
                width: 23px !important;
                height: 23px !important;
                margin: 5px 8px 5px 2px !important;
                vertical-align: middle !important;
                accent-color: #76c442 !important;
              }
              body.mullet-hop-waiver-themed .mullet-hop-choice-group {
                display: inline-flex !important;
                align-items: center !important;
                min-height: 44px;
                margin: 3px 6px 3px 0;
                padding: 6px 11px !important;
                background: #f1fae9;
                border: 2px solid #cae8b5;
                border-radius: 10px;
                font-weight: 700;
              }
              body.mullet-hop-waiver-themed button,
              body.mullet-hop-waiver-themed input[type='submit'],
              body.mullet-hop-waiver-themed input[type='button'],
              body.mullet-hop-waiver-themed input[type='reset'],
              body.mullet-hop-waiver-themed .button,
              body.mullet-hop-waiver-themed .btn {
                min-height: 50px !important;
                margin: 5px 4px !important;
                padding: 10px 22px !important;
                background: #76c442 !important;
                color: #101820 !important;
                border: 2px solid #101820 !important;
                border-radius: 10px !important;
                font: 800 17px/1.2 'Open Sans', Arial, sans-serif !important;
                text-decoration: none !important;
                cursor: pointer !important;
                box-shadow: 0 5px 0 rgba(16,24,32,.20) !important;
              }
              body.mullet-hop-waiver-themed button:hover,
              body.mullet-hop-waiver-themed input[type='submit']:hover,
              body.mullet-hop-waiver-themed input[type='button']:hover,
              body.mullet-hop-waiver-themed .button:hover,
              body.mullet-hop-waiver-themed .btn:hover {
                background: #8bd354 !important;
              }
              body.mullet-hop-waiver-themed button:active,
              body.mullet-hop-waiver-themed input[type='submit']:active,
              body.mullet-hop-waiver-themed input[type='button']:active {
                transform: translateY(2px);
                box-shadow: 0 2px 0 rgba(16,24,32,.22) !important;
              }
              body.mullet-hop-waiver-themed fieldset {
                margin: 18px 0 !important;
                padding: 18px !important;
                background: #fbfdff !important;
                border: 2px solid #d5e6ee !important;
                border-radius: 14px !important;
              }
              body.mullet-hop-waiver-themed legend {
                padding: 0 8px;
                color: #0877bd !important;
                font-weight: 800 !important;
              }
              body.mullet-hop-waiver-themed canvas {
                max-width: 100% !important;
                background: #fff !important;
                border: 3px solid #00a4d6 !important;
                border-radius: 12px !important;
                box-shadow: inset 0 2px 7px rgba(16,24,32,.09) !important;
                touch-action: none !important;
                -ms-touch-action: none !important;
                user-select: none !important;
                -webkit-user-select: none !important;
              }
              body.mullet-hop-waiver-themed img { max-width: 100%; }
              #mullet-hop-provider-logo {
                display: block !important;
                width: min(560px, 84vw) !important;
                height: auto !important;
                max-height: 190px !important;
                margin: 0 auto 14px !important;
                object-fit: contain !important;
              }
              #mullet-hop-provider-logo-fallback {
                margin: 0 auto 14px;
                text-align: center;
                color: #0877bd;
                font: 800 clamp(32px, 5vw, 52px)/1 'Open Sans', Arial, sans-serif;
                letter-spacing: -1px;
                -webkit-text-stroke: 1px #101820;
              }
              body.mullet-hop-waiver-themed hr {
                height: 3px;
                margin: 24px 0;
                background: linear-gradient(90deg, #76c442, #00a4d6, #75449a, #f58220);
                border: 0;
                border-radius: 999px;
              }
              body.mullet-hop-waiver-themed .error,
              body.mullet-hop-waiver-themed .errors,
              body.mullet-hop-waiver-themed .validation-error,
              body.mullet-hop-waiver-themed [class*='error'] {
                color: #8b1d13 !important;
                font-weight: 700 !important;
              }
              body.mullet-hop-waiver-themed a { color: #0877bd; }
              body.mullet-hop-waiver-themed a:focus-visible,
              body.mullet-hop-waiver-themed button:focus-visible,
              body.mullet-hop-waiver-themed input:focus-visible,
              body.mullet-hop-waiver-themed select:focus-visible,
              body.mullet-hop-waiver-themed textarea:focus-visible {
                outline: 4px solid rgba(245,130,32,.48) !important;
                outline-offset: 2px !important;
              }
              #mullet-hop-side-tools {
                position: fixed;
                z-index: 2147483646;
                top: 18px;
                right: 22px;
                transform: none;
                width: min(340px, calc(100vw - 36px));
                max-height: calc(100vh - 36px);
                overflow: auto;
                display: flex;
                flex-direction: column;
                gap: 12px;
                font-family: 'Open Sans', Arial, sans-serif;
                text-align: left;
              }
              #mullet-hop-waiver-help,
              #mullet-hop-side-tools .mullet-hop-action-card {
                position: relative;
                width: 100%;
                padding: 18px;
                background: #ffffff;
                color: #101820;
                border: 3px solid #101820;
                border-radius: 20px;
                box-shadow: 0 14px 38px rgba(16,24,32,.25);
                font-family: 'Open Sans', Arial, sans-serif;
                text-align: left;
              }
              #mullet-hop-waiver-help {
                pointer-events: none;
              }
              #mullet-hop-side-tools .mullet-hop-action-card h2 {
                margin: 0 0 8px;
                font-size: 20px;
                line-height: 1.15;
                font-weight: 800;
                text-align: center;
              }
              #mullet-hop-side-tools .mullet-hop-action-card p {
                margin: 0 0 11px;
                font-size: 14px;
                line-height: 1.38;
                font-weight: 600;
              }
              #mullet-hop-side-tools .mullet-hop-action-card button {
                width: 100% !important;
                margin: 0 !important;
                padding: 9px 12px !important;
                font-size: 15px !important;
              }
              #mullet-hop-reset-card { border-color: #f58220 !important; }
              #mullet-hop-reset-card h2 { color: #b64c00 !important; }
              #mullet-hop-reset-button { background: #f6a04d !important; }
              #mullet-hop-reset-button:hover { background: #ffb468 !important; }
              #mullet-hop-switch-card { border-color: #00a4d6 !important; }
              #mullet-hop-switch-card h2 { color: #0877bd !important; }
              #mullet-hop-switch-card .mullet-hop-switch-questions {
                margin: 0 0 13px;
                padding-left: 27px;
                color: #101820;
                font-size: 15px;
                line-height: 1.38;
                font-weight: 800;
              }
              #mullet-hop-switch-card .mullet-hop-switch-questions li + li { margin-top: 8px; }
              #mullet-hop-switch-card .mullet-hop-switch-instruction {
                padding-top: 11px;
                border-top: 2px solid #cfeef6;
                text-align: center;
              }
              #mullet-hop-switch-button { background: #69d2ec !important; }
              #mullet-hop-switch-button:hover { background: #8bdef1 !important; }
              #mullet-hop-assistance-card {
                overflow: hidden;
                background: linear-gradient(145deg, #fffef2, #fff4b8) !important;
                border: 5px solid #e8b000 !important;
                box-shadow: 0 14px 38px rgba(16,24,32,.25), 0 0 0 5px rgba(255,213,38,.24) !important;
              }
              #mullet-hop-assistance-card h2 { color: #755000 !important; }
              #mullet-hop-assistance-button { background: #ffd526 !important; }
              #mullet-hop-assistance-button:hover { background: #ffe361 !important; }
              #mullet-hop-assistance-card.is-requested {
                background: linear-gradient(145deg, #fff7b8, #ffe36a) !important;
              }
              #mullet-hop-assistance-card.is-requested #mullet-hop-assistance-button {
                background: #76c442 !important;
              }
              .mullet-hop-assistance-light {
                display: none;
                width: 34px;
                height: 34px;
                margin: 0 auto 9px;
                background: #ffdd30;
                border: 4px solid #8c6500;
                border-radius: 50%;
                box-shadow: 0 0 0 7px rgba(255,213,38,.30), 0 0 24px rgba(255,193,7,.88);
              }
              #mullet-hop-assistance-card.is-requested .mullet-hop-assistance-light {
                display: block;
                animation: mullet-hop-assistance-flash 700ms ease-in-out infinite alternate;
              }
              @keyframes mullet-hop-assistance-flash {
                from { opacity: .38; filter: saturate(.65); transform: scale(.88); }
                to { opacity: 1; filter: saturate(1.2); transform: scale(1.08); }
              }
              .mullet-hop-switch-target-choice {
                outline: 5px solid #f58220 !important;
                outline-offset: 5px !important;
                border-radius: 10px !important;
                animation: mullet-hop-target-pulse 850ms ease-in-out infinite alternate;
              }
              input.mullet-hop-switch-target-radio {
                box-shadow: 0 0 0 7px rgba(245,130,32,.34) !important;
              }
              #mullet-hop-switch-guidance {
                position: fixed;
                z-index: 2147483647;
                display: flex;
                align-items: center;
                gap: 10px;
                opacity: 0;
                transform: scale(.96);
                transition: opacity 180ms ease, transform 180ms ease;
                pointer-events: none;
                font-family: 'Open Sans', Arial, sans-serif;
              }
              #mullet-hop-switch-guidance.is-visible {
                opacity: 1;
                transform: scale(1);
              }
              #mullet-hop-switch-guidance[data-direction='left'] { flex-direction: row-reverse; }
              #mullet-hop-switch-guidance[data-direction='below'] { flex-direction: column; }
              #mullet-hop-switch-guidance[data-direction='above'] { flex-direction: column-reverse; }
              #mullet-hop-switch-guidance .mullet-hop-guidance-arrow {
                flex: 0 0 auto;
                color: #f58220;
                font-size: 48px;
                line-height: 1;
                font-weight: 800;
                text-shadow: 0 2px 0 #101820;
              }
              #mullet-hop-switch-guidance .mullet-hop-guidance-box {
                flex: 1 1 auto;
                padding: 15px 17px;
                background: #ffffff;
                color: #101820;
                border: 4px solid #f58220;
                border-radius: 16px;
                box-shadow: 0 14px 34px rgba(16,24,32,.28);
                text-align: center;
              }
              #mullet-hop-switch-guidance strong {
                display: block;
                margin-bottom: 3px;
                color: #75449a;
                font-size: 20px;
                line-height: 1.18;
                font-weight: 800;
              }
              #mullet-hop-switch-guidance span {
                display: block;
                font-size: 14px;
                line-height: 1.35;
                font-weight: 700;
              }
              @keyframes mullet-hop-target-pulse {
                from { outline-color: #f58220; background-color: rgba(255,246,233,.65); }
                to { outline-color: #76c442; background-color: rgba(241,250,233,.95); }
              }
              #mullet-hop-waiver-help img {
                display: block;
                width: min(185px, 60vw);
                max-height: 80px;
                object-fit: contain;
                margin: 0 auto 9px;
              }
              #mullet-hop-logo-fallback {
                display: none;
                margin: 0 0 9px;
                text-align: center;
                color: #0877bd;
                font: 800 30px/1 'Open Sans', Arial, sans-serif;
                letter-spacing: -1px;
                -webkit-text-stroke: 1px #101820;
              }
              #mullet-hop-waiver-help h2 {
                margin: 0 0 12px;
                color: #75449a;
                font-size: 22px;
                line-height: 1.15;
                font-weight: 800;
                text-align: center;
              }
              #mullet-hop-waiver-help .choice {
                padding: 12px 13px;
                border-radius: 13px;
                font-size: 14px;
                line-height: 1.35;
              }
              #mullet-hop-waiver-help .choice + .choice { margin-top: 10px; }
              #mullet-hop-waiver-help .choice-just-me {
                background: #eaf8fd;
                border: 2px solid #00a4d6;
              }
              #mullet-hop-waiver-help .choice-family {
                background: #f1fae9;
                border: 2px solid #76c442;
              }
              #mullet-hop-waiver-help .choice-title {
                display: block;
                margin-bottom: 3px;
                font-size: 16px;
                line-height: 1.15;
                font-weight: 800;
                color: #101820;
              }
              html:has(body.mullet-hop-dark-theme) { background: #111820; }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme {
                color: #edf3f7 !important;
                color-scheme: dark;
                background-color: #111820 !important;
                background-image:
                  linear-gradient(rgba(10,16,23,.78), rgba(10,16,23,.78)),
                  url('${kioskBackgroundSource}') !important;
              }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme .mullet-hop-form-card,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme table.mullet-hop-form-card > tbody > tr > td,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme fieldset,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme #mullet-hop-waiver-help,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme .mullet-hop-action-card {
                color: #edf3f7 !important;
                background: rgba(27,36,46,.98) !important;
                border-color: #7f94a6 !important;
              }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme #mullet-hop-assistance-card {
                color: #f5f8fa !important;
                background: linear-gradient(145deg, #423b17, #2f2d20) !important;
                border-color: #ffdc38 !important;
              }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme #mullet-hop-assistance-card h2 {
                color: #ffe56f !important;
              }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme p,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme label,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme td,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme span,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme div {
                color: #edf3f7 !important;
              }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme h1,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme h2 {
                color: #d3a4ee !important;
              }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme h3,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme a {
                color: #5bc6f0 !important;
              }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme input:not([type='radio']):not([type='checkbox']):not([type='submit']):not([type='button']):not([type='reset']):not([type='hidden']):not([type='image']),
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme select,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme textarea {
                color: #f5f8fa !important;
                background: #25313d !important;
                border-color: #5bc6f0 !important;
                caret-color: #f5f8fa !important;
              }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme input::placeholder,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme textarea::placeholder {
                color: #b9c6d0 !important;
                opacity: 1 !important;
              }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme option {
                color: #f5f8fa !important;
                background: #25313d !important;
              }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme .mullet-hop-choice-group,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme .choice,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme #mullet-hop-switch-guidance {
                color: #edf3f7 !important;
                background: #2b3947 !important;
              }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme button,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme input[type='submit'],
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme input[type='button'],
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme input[type='reset'] {
                color: #101820 !important;
              }
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme button *,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme .button *,
              body.mullet-hop-waiver-themed.mullet-hop-dark-theme .btn * {
                color: inherit !important;
              }
              @media (max-width: 1100px) {
                body.mullet-hop-waiver-themed,
                body.mullet-hop-waiver-themed.mullet-hop-has-side-tools {
                  padding: 16px 12px 40px !important;
                }
                body.mullet-hop-waiver-themed .mullet-hop-form-card {
                  width: 100% !important;
                  padding: 20px 14px !important;
                  border-radius: 17px !important;
                }
                body.mullet-hop-waiver-themed table.mullet-hop-form-card > tbody > tr > td {
                  padding: 18px 10px !important;
                }
                body.mullet-hop-waiver-themed .mullet-hop-form-card table:not(.mullet-hop-form-card),
                body.mullet-hop-waiver-themed .mullet-hop-form-card tbody,
                body.mullet-hop-waiver-themed .mullet-hop-form-card tr,
                body.mullet-hop-waiver-themed .mullet-hop-form-card td {
                  display: block !important;
                  width: 100% !important;
                }
                body.mullet-hop-waiver-themed .mullet-hop-form-card td {
                  padding: 4px 0 !important;
                }
                #mullet-hop-side-tools {
                  position: relative;
                  top: auto;
                  right: auto;
                  bottom: auto;
                  transform: none;
                  width: min(600px, 100%);
                  max-height: none;
                  margin: 18px auto 0;
                  overflow: visible;
                }
                #mullet-hop-waiver-help,
                #mullet-hop-side-tools .mullet-hop-action-card {
                  padding: 14px;
                }
                #mullet-hop-waiver-help img { max-height: 62px; }
                #mullet-hop-waiver-help h2 { font-size: 19px; margin-bottom: 9px; }
                #mullet-hop-waiver-help .choice { padding: 9px 10px; font-size: 13px; }
              }
              @media (max-width: 540px) {
                #mullet-hop-side-tools {
                  left: auto;
                  right: auto;
                  bottom: auto;
                  width: 100%;
                }
              }
            `;
              (document.head || document.documentElement).appendChild(style);
            }

            document.body.classList.add('mullet-hop-waiver-themed');
            document.body.classList.toggle('mullet-hop-dark-theme', kioskDarkMode);
            scheduleDarkContrastRepair();
            repairProviderLogo();
            document.querySelectorAll('canvas').forEach(installSignatureTouchBridge);
            const main = document.getElementById('divMain') ||
                         document.querySelector('body > form') ||
                         document.querySelector("form[action*='waiver']") ||
                         document.querySelector('body form') ||
                         document.querySelector('body > table') ||
                         document.querySelector('body > div');
            if (main) main.classList.add('mullet-hop-form-card');

            document.querySelectorAll("input[type='radio'], input[type='checkbox']").forEach(input => {
              const label = input.closest('label');
              const parent = input.parentElement;
              const holder = label || (parent && parent.tagName === 'SPAN' ? parent : null);
              if (holder && holder !== document.body) holder.classList.add('mullet-hop-choice-group');
            });

            const isStartPage = isWaiverStartPage();
            document.body.classList.add('mullet-hop-has-side-tools');

            let tools = document.getElementById('mullet-hop-side-tools');
            if (!tools) {
              tools = document.createElement('section');
              tools.id = 'mullet-hop-side-tools';
              tools.setAttribute('aria-label', 'Waiver help and controls');
              document.body.appendChild(tools);
            }

            let resetCard = document.getElementById('mullet-hop-reset-card');
            if (!resetCard) {
              resetCard = document.createElement('aside');
              resetCard.id = 'mullet-hop-reset-card';
              resetCard.className = 'mullet-hop-action-card';
              resetCard.innerHTML = `
                <h2>Need to Start Over?</h2>
                <p>This clears all information entered for the current guest and returns to a fresh waiver.</p>
                <button type='button' id='mullet-hop-reset-button'>Clear Data &amp; Reset Form</button>
              `;
              resetCard.querySelector('button').addEventListener('click', () => {
                if (window.confirm('Clear all entered waiver information and return to the starting page?'))
                  window.chrome.webview.postMessage('reset-waiver');
              });
              tools.appendChild(resetCard);
            }

            if (isStartPage) {
              document.getElementById('mullet-hop-switch-card')?.remove();
              if (!document.getElementById('mullet-hop-waiver-help')) {
                const help = document.createElement('aside');
                help.id = 'mullet-hop-waiver-help';
                help.setAttribute('aria-label', 'Help choosing the correct waiver option');
                help.innerHTML = `
                  ${kioskLogoSource ? "<img id='mullet-hop-waiver-logo' src='" + kioskLogoSource + "' alt='Mullet Hop fish logo'>" : ''}
                  <div id='mullet-hop-logo-fallback' style='display:${kioskLogoSource ? 'none' : 'block'}'>MULLET HOP</div>
                  <h2>Which option should I choose?</h2>
                  <div class='choice choice-just-me'>
                    <span class='choice-title'>JUST ME</span>
                    Choose this if you are 18 or older and only need to sign for yourself, and you do not have any minors to add to your waiver.
                  </div>
                  <div class='choice choice-family'>
                    <span class='choice-title'>ME AND MY KIDS!</span>
                    Choose this if you are the legal parent or guardian signing for one or more minors.
                  </div>
                `;
                const helpLogo = help.querySelector('#mullet-hop-waiver-logo');
                helpLogo?.addEventListener('error', () => {
                  helpLogo.remove();
                  const fallback = help.querySelector('#mullet-hop-logo-fallback');
                  if (fallback) fallback.style.display = 'block';
                });
                tools.insertBefore(help, tools.firstChild);
              }
              ensureAssistanceCard(tools);
              return;
            }

            document.getElementById('mullet-hop-waiver-help')?.remove();
            if (!document.getElementById('mullet-hop-switch-card')) {
              const originalChoice = sessionStorage.getItem('mullet-hop-waiver-choice');
              const email = (sessionStorage.getItem('mullet-hop-waiver-email') || '').trim();
              const hasRememberedChoice = originalChoice === 'just-me' || originalChoice === 'family';
              const targetChoice = originalChoice === 'family' ? 'just-me' : 'family';

              const switchCard = document.createElement('aside');
              switchCard.id = 'mullet-hop-switch-card';
              switchCard.className = 'mullet-hop-action-card';
              switchCard.innerHTML = `
                <h2>Need a Different Waiver Type?</h2>
                <ol class='mullet-hop-switch-questions'>
                  <li>Need to add minors to your waiver?</li>
                  <li>Waiver asking to add a child and it's just you?</li>
                </ol>
                <p class='mullet-hop-switch-instruction'>Click below to go back and start with the correct type of waiver.</p>
                <button type='button' id='mullet-hop-switch-button'>Start a New Waiver</button>
              `;
              switchCard.querySelector('button').addEventListener('click', () => {
                const question = 'Restart this waiver? Information already entered on this form will be cleared.';
                if (!window.confirm(question)) return;
                if (hasRememberedChoice && email) {
                  window.chrome.webview.postMessage(JSON.stringify({
                    type: 'switch-waiver-option',
                    email,
                    choice: targetChoice
                  }));
                }
                else {
                  window.chrome.webview.postMessage('switch-waiver-reset');
                }
              });
              tools.appendChild(switchCard);
            }
            ensureAssistanceCard(tools);
          };

          window.__mulletHopSetWaiverContext = (email, choice) => {
            if (email) sessionStorage.setItem('mullet-hop-waiver-email', email);
            if (choice === 'just-me' || choice === 'family')
              sessionStorage.setItem('mullet-hop-waiver-choice', choice);
            document.getElementById('mullet-hop-switch-card')?.remove();
            applyWaiverTheme();
          };

          const completionPhrases = [
            'waiver has been successfully submitted',
            'waiver was successfully submitted',
            'waiver has been completed',
            'waiver is complete',
            'thank you for completing the waiver',
            'thank you for signing the waiver',
            'your waiver is now complete',
            'your waiver has been submitted'
          ];

          let completionSent = false;
          const scanForCompletion = () => {
            if (completionSent || !document.body) return;
            const text = (document.body.innerText || '').toLowerCase().replace(/\s+/g, ' ');
            if (completionPhrases.some(phrase => text.includes(phrase))) {
              completionSent = true;
              window.chrome.webview.postMessage('completion-text');
            }
          };

          window.addEventListener('DOMContentLoaded', () => {
            applyWaiverTheme();
            scanForCompletion();
          });
          new MutationObserver(() => {
            applyWaiverTheme();
            scanForCompletion();
          })
            .observe(document.documentElement, { childList: true, subtree: true, characterData: true });
          setInterval(scanForCompletion, 1500);
        })();
        """;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

internal enum AdvertisementScheduleType
{
    SpecificDates,
    Weekly
}

internal sealed class KioskAdvertisement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Advertisement";
    public string ImageFileName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public AdvertisementScheduleType ScheduleType { get; set; } = AdvertisementScheduleType.SpecificDates;
    public DateTime StartDateTime { get; set; } = DateTime.Today;
    public DateTime EndDateTime { get; set; } = DateTime.Today.AddDays(1).AddTicks(-1);
    public DayOfWeek[] DaysOfWeek { get; set; } = Enum.GetValues<DayOfWeek>();
    public TimeSpan DailyStartTime { get; set; } = TimeSpan.FromHours(10);
    public TimeSpan DailyEndTime { get; set; } = TimeSpan.FromHours(22);

    public bool IsActive(DateTime now)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(ImageFileName))
            return false;

        if (ScheduleType == AdvertisementScheduleType.SpecificDates)
            return now >= StartDateTime && now <= EndDateTime;

        var time = now.TimeOfDay;
        if (DailyStartTime == DailyEndTime)
            return DaysOfWeek.Contains(now.DayOfWeek);

        if (DailyStartTime <= DailyEndTime)
            return DaysOfWeek.Contains(now.DayOfWeek) && time >= DailyStartTime && time <= DailyEndTime;

        if (DaysOfWeek.Contains(now.DayOfWeek) && time >= DailyStartTime)
            return true;
        var previousDay = (DayOfWeek)(((int)now.DayOfWeek + 6) % 7);
        return DaysOfWeek.Contains(previousDay) && time <= DailyEndTime;
    }

    public string ScheduleSummary()
    {
        if (ScheduleType == AdvertisementScheduleType.SpecificDates)
            return $"{StartDateTime:MMM d, yyyy h:mm tt} – {EndDateTime:MMM d, yyyy h:mm tt}";

        var days = DaysOfWeek.Length == 7
            ? "Every day"
            : string.Join(", ", DaysOfWeek.Select(day => day.ToString()[..3]));
        if (DailyStartTime == DailyEndTime)
            return days + " · All day";
        var overnight = DailyStartTime > DailyEndTime ? " (overnight)" : string.Empty;
        return $"{days} · {DateTime.Today.Add(DailyStartTime):h:mm tt}–{DateTime.Today.Add(DailyEndTime):h:mm tt}{overnight}";
    }

    public KioskAdvertisement Clone() => new()
    {
        Id = Id,
        Name = Name,
        ImageFileName = ImageFileName,
        Enabled = Enabled,
        ScheduleType = ScheduleType,
        StartDateTime = StartDateTime,
        EndDateTime = EndDateTime,
        DaysOfWeek = [.. DaysOfWeek],
        DailyStartTime = DailyStartTime,
        DailyEndTime = DailyEndTime
    };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        Name = string.IsNullOrWhiteSpace(Name) ? "Advertisement" : Name.Trim();
        ImageFileName = Path.GetFileName(ImageFileName ?? string.Empty);
        if (ScheduleType != AdvertisementScheduleType.SpecificDates &&
            ScheduleType != AdvertisementScheduleType.Weekly)
            ScheduleType = AdvertisementScheduleType.SpecificDates;
        if (EndDateTime <= StartDateTime) EndDateTime = StartDateTime.AddHours(1);
        DaysOfWeek ??= [];
        DaysOfWeek = DaysOfWeek.Distinct().Where(day => (int)day is >= 0 and <= 6).ToArray();
        if (DaysOfWeek.Length == 0) DaysOfWeek = Enum.GetValues<DayOfWeek>();
        DailyStartTime = NormalizeTime(DailyStartTime);
        DailyEndTime = NormalizeTime(DailyEndTime);
    }

    private static TimeSpan NormalizeTime(TimeSpan value)
    {
        var ticks = value.Ticks % TimeSpan.TicksPerDay;
        if (ticks < 0) ticks += TimeSpan.TicksPerDay;
        return TimeSpan.FromTicks(ticks);
    }
}

internal sealed class KioskBusinessDayHours
{
    public DayOfWeek Day { get; set; }
    public bool IsOpen { get; set; } = true;
    public TimeSpan OpenTime { get; set; } = TimeSpan.FromHours(10);
    public TimeSpan CloseTime { get; set; } = TimeSpan.FromHours(22);

    public static DayOfWeek[] OrderedDays { get; } =
    [
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday
    ];

    public static List<KioskBusinessDayHours> CreateDefaults() =>
        OrderedDays.Select(day => new KioskBusinessDayHours { Day = day }).ToList();

    public void Normalize()
    {
        OpenTime = NormalizeTime(OpenTime);
        CloseTime = NormalizeTime(CloseTime);
        if (CloseTime <= OpenTime)
        {
            OpenTime = TimeSpan.FromHours(10);
            CloseTime = TimeSpan.FromHours(22);
        }
    }

    private static TimeSpan NormalizeTime(TimeSpan value)
    {
        var ticks = value.Ticks % TimeSpan.TicksPerDay;
        if (ticks < 0)
            ticks += TimeSpan.TicksPerDay;
        return TimeSpan.FromTicks(ticks);
    }
}

internal sealed class KioskSettings
{
    public string StartUrl { get; set; } =
        "https://mullet.lilypadpos.app/public/onlinewaiver/waiver.php?l=English";

    public string[] AllowedHosts { get; set; } = ["mullet.lilypadpos.app"];
    public string[] AllowedPathPrefixes { get; set; } = ["/public/onlinewaiver/"];
    public int IdleTimeoutMinutes { get; set; } = 3;
    public int ScreensaverTimeoutMinutes { get; set; } = 3;
    public int CompletionResetSeconds { get; set; } = 15;
    public bool StationClosed { get; set; }
    public bool ManualBusinessBlackout { get; set; }
    public bool BusinessHoursEnabled { get; set; }
    public int BusinessClosedMessageMinutes { get; set; } = 5;
    public int PreOpeningScreensaverMinutes { get; set; } = 30;
    public List<KioskBusinessDayHours> BusinessHours { get; set; } =
        KioskBusinessDayHours.CreateDefaults();
    public KioskThemeMode ThemeMode { get; set; } = KioskThemeMode.Auto;
    public bool ScheduledDarkEnabled { get; set; }
    public DayOfWeek[] ScheduledDarkDays { get; set; } = Enum.GetValues<DayOfWeek>();
    public TimeSpan ScheduledDarkTime { get; set; } = TimeSpan.FromHours(18);
    public bool RemoteManagementEnabled { get; set; }
    public string RemoteControllerUrl { get; set; } = string.Empty;
    public string RemotePairingKey { get; set; } = string.Empty;
    public string StationId { get; set; } = Guid.NewGuid().ToString("N");
    public string StationName { get; set; } = Environment.MachineName;
    public bool AssistanceRequested { get; set; }
    public bool AssistanceAcknowledged { get; set; }
    public string RemoteLastCommandId { get; set; } = string.Empty;
    public bool RemoteLastCommandSuccess { get; set; }
    public string RemoteLastCommandMessage { get; set; } = string.Empty;
    public string AdvertisementSyncRevision { get; set; } = string.Empty;
    public DateTime? AdvertisementLastSyncUtc { get; set; }
    public string AdvertisementLastSyncStatus { get; set; } =
        "Advertisements have not been synced with the kiosk manager.";
    public string BusinessHoursSyncRevision { get; set; } = string.Empty;
    public DateTime? BusinessHoursLastSyncUtc { get; set; }
    public string BusinessHoursLastSyncStatus { get; set; } =
        "Business Hours have not been synced with the kiosk manager.";

    public string[] CompletionUrlKeywords { get; set; } =
        ["success", "complete", "completed", "confirmation", "finished", "done", "submitted", "thankyou", "thank-you"];
    public List<KioskAdvertisement> Advertisements { get; set; } = [];

    public string StaffPinSalt { get; set; } = string.Empty;
    public string StaffPinHash { get; set; } = string.Empty;

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MulletHopWaiverKiosk", "Data");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string AdvertisementsDirectory => Path.Combine(DataDirectory, "Advertisements");

    public static KioskSettings? LoadOrCreate()
    {
        Directory.CreateDirectory(DataDirectory);
        var settings = new KioskSettings();

        if (File.Exists(SettingsPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<KioskSettings>(File.ReadAllText(SettingsPath));
                if (loaded is null)
                    throw new InvalidDataException("The settings file was empty.");

                loaded.Normalize();
                if (!string.IsNullOrWhiteSpace(loaded.StaffPinHash))
                {
                    return loaded;
                }

                settings = loaded;
                KioskLog.Write("A new staff password is required; existing kiosk and advertisement settings were retained.");
            }
            catch (Exception ex)
            {
                KioskLog.Write("Settings read error: " + ex.GetType().Name + " - " + ex.Message);
                settings = new KioskSettings();
                MessageBox.Show(
                    "The kiosk settings could not be read. A new staff password must be created.",
                    "Mullet Hop Waiver Kiosk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        using var pinDialog = new PinSetupDialog();
        if (pinDialog.ShowDialog() != DialogResult.OK)
            return null;

        settings.SetPin(pinDialog.Pin);
        settings.Save();
        return settings;
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(DataDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
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
            var actual = DerivePinHash(pin, salt);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private void Normalize()
    {
        if (!Uri.TryCreate(StartUrl, UriKind.Absolute, out _))
            StartUrl = "https://mullet.lilypadpos.app/public/onlinewaiver/waiver.php?l=English";

        AllowedHosts ??= ["mullet.lilypadpos.app"];
        AllowedPathPrefixes ??= ["/public/onlinewaiver/"];
        CompletionUrlKeywords ??= ["success", "complete", "done", "submitted"];
        Advertisements ??= [];
        BusinessHours ??= KioskBusinessDayHours.CreateDefaults();
        if (!Enum.IsDefined(ThemeMode)) ThemeMode = KioskThemeMode.Auto;
        ScheduledDarkDays ??= [];
        ScheduledDarkDays = ScheduledDarkDays
            .Where(day => (int)day is >= 0 and <= 6)
            .Distinct()
            .ToArray();
        var scheduledDarkTicks = ScheduledDarkTime.Ticks % TimeSpan.TicksPerDay;
        if (scheduledDarkTicks < 0) scheduledDarkTicks += TimeSpan.TicksPerDay;
        ScheduledDarkTime = TimeSpan.FromTicks(scheduledDarkTicks);
        var savedBusinessHours = BusinessHours
            .GroupBy(schedule => schedule.Day)
            .ToDictionary(group => group.Key, group => group.First());
        BusinessHours = KioskBusinessDayHours.OrderedDays
            .Select(day => savedBusinessHours.TryGetValue(day, out var schedule)
                ? schedule
                : new KioskBusinessDayHours { Day = day })
            .ToList();
        foreach (var schedule in BusinessHours)
            schedule.Normalize();
        RemoteControllerUrl = (RemoteControllerUrl ?? string.Empty).Trim();
        RemotePairingKey = (RemotePairingKey ?? string.Empty).Trim();
        if (!Guid.TryParseExact(StationId, "N", out _)) StationId = Guid.NewGuid().ToString("N");
        StationName = string.IsNullOrWhiteSpace(StationName)
            ? Environment.MachineName
            : StationName.Trim();
        if (!AssistanceRequested)
            AssistanceAcknowledged = false;
        if (StationClosed)
            ManualBusinessBlackout = false;
        RemoteLastCommandId ??= string.Empty;
        RemoteLastCommandMessage ??= string.Empty;
        AdvertisementSyncRevision ??= string.Empty;
        AdvertisementLastSyncStatus = string.IsNullOrWhiteSpace(AdvertisementLastSyncStatus)
            ? "Advertisements have not been synced with the kiosk manager."
            : AdvertisementLastSyncStatus.Trim();
        BusinessHoursSyncRevision ??= string.Empty;
        BusinessHoursLastSyncStatus = string.IsNullOrWhiteSpace(BusinessHoursLastSyncStatus)
            ? "Business Hours have not been synced with the kiosk manager."
            : BusinessHoursLastSyncStatus.Trim();
        foreach (var advertisement in Advertisements)
            advertisement.Normalize();
        IdleTimeoutMinutes = Math.Clamp(IdleTimeoutMinutes, 1, 60);
        ScreensaverTimeoutMinutes = Math.Clamp(ScreensaverTimeoutMinutes, 1, 240);
        CompletionResetSeconds = Math.Clamp(CompletionResetSeconds, 12, 60);
        BusinessClosedMessageMinutes = Math.Clamp(BusinessClosedMessageMinutes, 1, 240);
        PreOpeningScreensaverMinutes = Math.Clamp(PreOpeningScreensaverMinutes, 0, 240);
    }

    private static byte[] DerivePinHash(string pin, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(pin, salt, 150_000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }
}

internal sealed class PinSetupDialog : Form
{
    private readonly TextBox _pin = new() { UseSystemPasswordChar = true, MaxLength = 8, Width = 220 };
    private readonly TextBox _confirm = new() { UseSystemPasswordChar = true, MaxLength = 8, Width = 220 };
    public string Pin => _pin.Text;

    public PinSetupDialog()
    {
        Text = "Create Staff Settings Password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(470, 275);
        Font = new Font("Segoe UI", 10);

        var heading = new Label
        {
            AutoSize = false,
            Text = "Create the numerical staff password for kiosk settings.",
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            Bounds = new Rectangle(25, 20, 420, 35)
        };
        var note = new Label
        {
            AutoSize = false,
            Text = "Use 4–8 numbers only. Staff will press Ctrl + Alt + M and enter this password.",
            Bounds = new Rectangle(25, 58, 420, 48)
        };
        var pinLabel = new Label { Text = "New Password:", AutoSize = true, Location = new Point(25, 122) };
        var confirmLabel = new Label { Text = "Confirm Password:", AutoSize = true, Location = new Point(12, 161) };
        _pin.Location = new Point(150, 118);
        _confirm.Location = new Point(150, 157);
        ConfigureNumericOnly(_pin);
        ConfigureNumericOnly(_confirm);

        var save = new Button { Text = "Save and Start", Bounds = new Rectangle(213, 215, 130, 36) };
        var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(350, 215, 90, 36), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => ValidateAndClose();

        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([heading, note, pinLabel, confirmLabel, _pin, _confirm, save, cancel]);
        KioskTheme.Apply(this, KioskTheme.WindowsUsesDarkApps());
    }

    private void ValidateAndClose()
    {
        if (_pin.Text.Length is < 4 or > 8 ||
            !_pin.Text.All(character => character >= '0' && character <= '9'))
        {
            MessageBox.Show(this, "Enter a password containing 4–8 numbers only.", "Staff Password",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _pin.Focus();
            return;
        }

        if (_pin.Text != _confirm.Text)
        {
            MessageBox.Show(this, "The two password entries do not match.", "Staff Password",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _confirm.Clear();
            _confirm.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static void ConfigureNumericOnly(TextBox box)
    {
        var cleaning = false;
        box.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && (e.KeyChar < '0' || e.KeyChar > '9'))
                e.Handled = true;
        };
        box.TextChanged += (_, _) =>
        {
            if (cleaning) return;
            var numbersOnly = new string(box.Text
                .Where(character => character >= '0' && character <= '9')
                .Take(8)
                .ToArray());
            if (numbersOnly == box.Text) return;
            cleaning = true;
            box.Text = numbersOnly;
            box.SelectionStart = box.Text.Length;
            cleaning = false;
        };
    }
}

internal enum StaffSettingsAction
{
    None,
    ReturnToKiosk,
    ExitToWindows,
    PreviewDateTime,
    UseLiveDateTime,
    PreviewThankYouPage,
    ToggleStationClosed,
    StartBusinessBlackout
}

internal sealed class StaffSettingsDialog : Form
{
    private static int _lastSelectedTabIndex;

    private readonly KioskSettings _settings;
    private readonly string _connectionTestUrl;
    private readonly Func<IProgress<AdvertisementSyncProgress>?, Task<AdvertisementSyncResult>>
        _syncAdvertisements;
    private readonly Func<Task> _previewBusinessClosed;
    private readonly Button _connectionButton = new();
    private readonly Label _connectionResult = new();
    private readonly Button _updateButton = new();
    private readonly Label _updateResult = new();
    private readonly DateTimePicker _datePicker = new();
    private readonly DateTimePicker _timePicker = new();
    private readonly NumericUpDown _screensaverMinutes = new();
    private readonly Button _screensaverSaveButton = new();
    private readonly CheckBox _businessHoursEnabled = new();
    private readonly NumericUpDown _businessClosedMinutes = new();
    private readonly NumericUpDown _preOpeningScreensaverMinutes = new();
    private readonly Button _businessHoursSaveButton = new();
    private readonly Label _businessHoursStatus = new();
    private readonly TabControl _settingsTabs = new();
    private readonly ComboBox _themeMode = new();
    private readonly CheckBox _scheduledDarkEnabled = new();
    private readonly DateTimePicker _scheduledDarkTime = new();
    private readonly Label _themeStatus = new();
    private readonly Dictionary<DayOfWeek, CheckBox> _scheduledDarkDays = [];
    private bool IsDarkTheme => KioskTheme.Evaluate(_settings, DateTime.Now).IsDark;
    private readonly Dictionary<DayOfWeek,
        (CheckBox IsOpen, DateTimePicker Opens, DateTimePicker Closes)> _businessDayControls = [];

    public StaffSettingsAction SelectedAction { get; private set; }
    public DateTime SelectedDateTime => _datePicker.Value.Date + _timePicker.Value.TimeOfDay;

    public StaffSettingsDialog(
        KioskSettings settings,
        DateTime? activePreview,
        Func<IProgress<AdvertisementSyncProgress>?, Task<AdvertisementSyncResult>> syncAdvertisements,
        Func<Task> previewBusinessClosed)
    {
        _settings = settings;
        _syncAdvertisements = syncAdvertisements;
        _previewBusinessClosed = previewBusinessClosed;
        _connectionTestUrl = settings.StartUrl;
        Text = "Mullet Hop Staff Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(840, 710);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "STAFF SETTINGS",
            Font = new Font("Segoe UI", 21, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(25, 17, 790, 45)
        };
        _settingsTabs.Bounds = new Rectangle(20, 76, 800, 560);
        _settingsTabs.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _settingsTabs.Alignment = TabAlignment.Left;
        _settingsTabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _settingsTabs.SizeMode = TabSizeMode.Fixed;
        _settingsTabs.ItemSize = new Size(42, 185);
        _settingsTabs.Padding = new Point(12, 7);
        var connectionTab = new TabPage("Connection & Updates")
        {
            BackColor = Color.White,
            Padding = new Padding(8)
        };
        var dateTimeTab = new TabPage("Date & Time")
        {
            BackColor = Color.White,
            Padding = new Padding(8)
        };
        var appearanceTab = new TabPage("Appearance")
        {
            BackColor = Color.White,
            Padding = new Padding(8)
        };
        var stationTab = new TabPage("Waiver Station")
        {
            BackColor = Color.White,
            Padding = new Padding(8)
        };
        var businessHoursTab = new TabPage("Business Hours")
        {
            BackColor = Color.White,
            Padding = new Padding(8)
        };
        var staffToolsTab = new TabPage("Ads & Staff Tools")
        {
            BackColor = Color.White,
            Padding = new Padding(8)
        };
        _settingsTabs.TabPages.AddRange([
            connectionTab, dateTimeTab, appearanceTab, stationTab, businessHoursTab, staffToolsTab]);
        _settingsTabs.SelectedIndex = Math.Clamp(
            _lastSelectedTabIndex, 0, _settingsTabs.TabPages.Count - 1);
        _settingsTabs.SelectedIndexChanged += (_, _) =>
            _lastSelectedTabIndex = _settingsTabs.SelectedIndex;
        _settingsTabs.DrawItem += (_, e) =>
        {
            var selected = e.Index == _settingsTabs.SelectedIndex;
            var dark = KioskTheme.Evaluate(_settings, DateTime.Now).IsDark;
            using var background = new SolidBrush(selected
                ? KioskTheme.SelectedNavigation(dark)
                : KioskTheme.Navigation(dark));
            e.Graphics.FillRectangle(background, e.Bounds);
            var textRectangle = Rectangle.Inflate(e.Bounds, -8, -4);
            TextRenderer.DrawText(
                e.Graphics,
                _settingsTabs.TabPages[e.Index].Text,
                _settingsTabs.Font,
                textRectangle,
                selected
                    ? (dark ? Color.FromArgb(205, 153, 235) : Color.FromArgb(117, 68, 154))
                    : KioskTheme.PrimaryText(dark),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        };

        var currentStatus = new Label
        {
            AutoSize = false,
            Text = activePreview.HasValue
                ? "Date/time preview is active: " + activePreview.Value.ToString("MMM d, yyyy h:mm tt")
                : "The kiosk is currently using the live date and time.",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = activePreview.HasValue ? Color.FromArgb(182, 76, 0) : Color.FromArgb(8, 119, 189),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(20, 20, 580, 36)
        };

        var internetGroup = new GroupBox
        {
            Text = "Internet Connection and Kiosk Updates",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(20, 25, 580, 190)
        };
        var internetNote = new Label
        {
            AutoSize = false,
            Text = "Test the live waiver website or check GitHub for a newer kiosk version.",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(16, 24, 32),
            Bounds = new Rectangle(18, 30, 540, 25)
        };
        _connectionButton.Text = "Check Connection";
        _connectionButton.Bounds = new Rectangle(18, 70, 165, 38);
        _connectionButton.BackColor = Color.FromArgb(105, 210, 236);
        _connectionButton.FlatStyle = FlatStyle.Flat;
        _connectionButton.Click += async (_, _) => await CheckConnectionAsync();
        _connectionResult.AutoSize = false;
        _connectionResult.Text = "Not checked yet.";
        _connectionResult.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _connectionResult.ForeColor = Color.FromArgb(83, 97, 109);
        _connectionResult.TextAlign = ContentAlignment.MiddleLeft;
        _connectionResult.Bounds = new Rectangle(198, 70, 355, 38);
        _updateButton.Text = "Check for Updates";
        _updateButton.Bounds = new Rectangle(18, 126, 165, 38);
        _updateButton.BackColor = Color.FromArgb(118, 196, 66);
        _updateButton.FlatStyle = FlatStyle.Flat;
        _updateButton.Click += async (_, _) => await CheckForUpdateAsync();
        _updateResult.AutoSize = false;
        _updateResult.Text = "Installed version: " + KioskUpdater.CurrentVersion;
        _updateResult.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _updateResult.ForeColor = Color.FromArgb(83, 97, 109);
        _updateResult.TextAlign = ContentAlignment.MiddleLeft;
        _updateResult.Bounds = new Rectangle(198, 126, 355, 38);
        internetGroup.Controls.AddRange([
            internetNote, _connectionButton, _connectionResult, _updateButton, _updateResult]);
        var connectionHelp = new Label
        {
            AutoSize = false,
            Text = "Connection problems automatically display the Waiver Station Closed page. The kiosk checks every 60 seconds and returns to a fresh waiver when the website is available again.",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(16, 24, 32),
            Bounds = new Rectangle(30, 240, 560, 70)
        };
        connectionTab.Controls.AddRange([internetGroup, connectionHelp]);

        var previewGroup = new GroupBox
        {
            Text = "Preview a Different Date and Time",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            Bounds = new Rectangle(20, 75, 580, 245)
        };
        var previewNote = new Label
        {
            AutoSize = false,
            Text = "Choose a date and time, then reload a fresh waiver in preview mode. This changes the browser time only; content generated by LilYPad's server may still use its live server clock.",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(16, 24, 32),
            Bounds = new Rectangle(18, 30, 540, 58)
        };
        var dateLabel = new Label
        {
            Text = "Date:", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(16, 24, 32), Location = new Point(32, 108)
        };
        var timeLabel = new Label
        {
            Text = "Time:", AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(16, 24, 32), Location = new Point(320, 108)
        };
        var initialValue = activePreview ?? DateTime.Now;
        _datePicker.Format = DateTimePickerFormat.Long;
        _datePicker.Value = initialValue;
        _datePicker.Bounds = new Rectangle(82, 103, 215, 32);
        _timePicker.Format = DateTimePickerFormat.Custom;
        _timePicker.CustomFormat = "h:mm tt";
        _timePicker.ShowUpDown = true;
        _timePicker.Value = initialValue;
        _timePicker.Bounds = new Rectangle(375, 103, 125, 32);

        var previewButton = new Button
        {
            Text = "Preview Selected Date & Time",
            Bounds = new Rectangle(18, 165, 250, 44),
            BackColor = Color.FromArgb(118, 196, 66),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        previewButton.Click += (_, _) => Complete(StaffSettingsAction.PreviewDateTime);
        var liveButton = new Button
        {
            Text = "Return to Live Date & Time",
            Bounds = new Rectangle(282, 165, 250, 44),
            BackColor = Color.FromArgb(245, 130, 32),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Enabled = activePreview.HasValue
        };
        liveButton.Click += (_, _) => Complete(StaffSettingsAction.UseLiveDateTime);
        previewGroup.Controls.AddRange([
            previewNote, dateLabel, timeLabel, _datePicker, _timePicker, previewButton, liveButton]);
        dateTimeTab.Controls.AddRange([currentStatus, previewGroup]);

        var themeGroup = new GroupBox
        {
            Text = "Kiosk Theme",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            Bounds = new Rectangle(20, 20, 580, 145)
        };
        var themeNote = new Label
        {
            AutoSize = false,
            Text = "Auto follows the Windows app theme. Choosing Light or Dark overrides Windows.",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(16, 24, 32),
            Bounds = new Rectangle(18, 28, 540, 42)
        };
        var themeModeLabel = new Label
        {
            Text = "Theme mode:", AutoSize = false,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(16, 24, 32),
            Bounds = new Rectangle(18, 85, 130, 30),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _themeMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _themeMode.Items.AddRange(["Auto (Windows)", "Light", "Dark"]);
        _themeMode.SelectedIndex = _settings.ThemeMode switch
        {
            KioskThemeMode.Light => 1,
            KioskThemeMode.Dark => 2,
            _ => 0
        };
        _themeMode.Bounds = new Rectangle(155, 84, 220, 32);
        themeGroup.Controls.AddRange([themeNote, themeModeLabel, _themeMode]);

        var scheduleThemeGroup = new GroupBox
        {
            Text = "Scheduled Dark Mode",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(20, 178, 580, 342)
        };
        _scheduledDarkEnabled.Text = "Use a scheduled Dark-mode override";
        _scheduledDarkEnabled.AutoSize = true;
        _scheduledDarkEnabled.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _scheduledDarkEnabled.Checked = _settings.ScheduledDarkEnabled;
        _scheduledDarkEnabled.Location = new Point(18, 30);
        _scheduledDarkEnabled.CheckedChanged += (_, _) => UpdateScheduledThemeControls();
        var scheduleThemeNote = new Label
        {
            AutoSize = false,
            Text = "On selected days, a Light kiosk switches to Dark at this time and returns to its Light or Auto setting at the next configured business opening.",
            Font = new Font("Segoe UI", 9.2f),
            ForeColor = Color.FromArgb(16, 24, 32),
            Bounds = new Rectangle(18, 62, 540, 50)
        };
        var daysLabel = new Label
        {
            Text = "Days:", AutoSize = false, Bounds = new Rectangle(18, 122, 65, 27),
            ForeColor = Color.FromArgb(16, 24, 32),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        var dayChoices = new[]
        {
            (DayOfWeek.Monday, "Mon"), (DayOfWeek.Tuesday, "Tue"),
            (DayOfWeek.Wednesday, "Wed"), (DayOfWeek.Thursday, "Thu"),
            (DayOfWeek.Friday, "Fri"), (DayOfWeek.Saturday, "Sat"),
            (DayOfWeek.Sunday, "Sun")
        };
        for (var index = 0; index < dayChoices.Length; index++)
        {
            var check = new CheckBox
            {
                Text = dayChoices[index].Item2,
                AutoSize = false,
                Checked = _settings.ScheduledDarkDays.Contains(dayChoices[index].Item1),
                Bounds = new Rectangle(82 + index * 67, 119, 65, 29)
            };
            _scheduledDarkDays[dayChoices[index].Item1] = check;
            scheduleThemeGroup.Controls.Add(check);
        }
        var darkTimeLabel = new Label
        {
            Text = "Switch to Dark at:", AutoSize = false,
            Bounds = new Rectangle(18, 166, 160, 31),
            ForeColor = Color.FromArgb(16, 24, 32),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _scheduledDarkTime.Format = DateTimePickerFormat.Custom;
        _scheduledDarkTime.CustomFormat = "h:mm tt";
        _scheduledDarkTime.ShowUpDown = true;
        _scheduledDarkTime.Value = DateTime.Today + _settings.ScheduledDarkTime;
        _scheduledDarkTime.Bounds = new Rectangle(185, 164, 145, 32);
        _themeStatus.AutoSize = false;
        _themeStatus.Text = DescribeThemeStatus();
        _themeStatus.Bounds = new Rectangle(18, 211, 540, 55);
        _themeStatus.ForeColor = Color.FromArgb(83, 97, 109);
        _themeStatus.Font = new Font("Segoe UI", 9.2f, FontStyle.Bold);
        var saveThemeButton = new Button
        {
            Text = "Save Appearance",
            Bounds = new Rectangle(18, 281, 180, 40),
            BackColor = Color.FromArgb(118, 196, 66),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        saveThemeButton.Click += (_, _) => SaveAppearance(saveThemeButton);
        scheduleThemeGroup.Controls.AddRange([
            _scheduledDarkEnabled, scheduleThemeNote, daysLabel, darkTimeLabel,
            _scheduledDarkTime, _themeStatus, saveThemeButton]);
        appearanceTab.Controls.AddRange([themeGroup, scheduleThemeGroup]);
        UpdateScheduledThemeControls();

        var exitButton = new Button
        {
            Text = "Exit Kiosk",
            Bounds = new Rectangle(30, 652, 190, 45),
            BackColor = Color.FromArgb(245, 130, 32),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        exitButton.Click += (_, _) => Complete(StaffSettingsAction.ExitToWindows);
        var advertisementsButton = new Button
        {
            Text = "Manage Advertisements",
            Bounds = new Rectangle(20, 45, 250, 48),
            BackColor = Color.FromArgb(117, 68, 154),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        advertisementsButton.Click += (_, _) =>
        {
            using var advertisementsDialog = new AdvertisementManagerDialog(
                _settings,
                activePreview.HasValue ? SelectedDateTime : null,
                _syncAdvertisements);
            advertisementsDialog.ShowDialog(this);
        };
        var thankYouPreviewButton = new Button
        {
            Text = "Preview Thank-You Page",
            Bounds = new Rectangle(290, 45, 250, 48),
            BackColor = Color.FromArgb(105, 210, 236),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        thankYouPreviewButton.Click += (_, _) => Complete(StaffSettingsAction.PreviewThankYouPage);
        var changePasswordButton = new Button
        {
            Text = "Change Staff Password",
            Bounds = new Rectangle(20, 115, 250, 48),
            BackColor = Color.FromArgb(118, 196, 66),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        changePasswordButton.Click += (_, _) =>
        {
            using var passwordDialog = new StaffPasswordChangeDialog(_settings);
            passwordDialog.ShowDialog(this);
        };
        var remoteManagementButton = new Button
        {
            Text = "Remote Control Options",
            Bounds = new Rectangle(290, 115, 250, 48),
            BackColor = Color.FromArgb(245, 130, 32),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        remoteManagementButton.Click += (_, _) =>
        {
            using var remoteDialog = new RemoteManagementSettingsDialog(_settings);
            remoteDialog.ShowDialog(this);
        };
        var closedPageStatus = new Label
        {
            AutoSize = false,
            Text = _settings.StationClosed
                ? "Closed page is ON — guests cannot start a waiver."
                : "Closed page is OFF — the waiver station is available.",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = _settings.StationClosed
                ? Color.FromArgb(180, 35, 24)
                : Color.FromArgb(54, 128, 27),
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(18, 92, 320, 42)
        };
        var closedPageButton = new Button
        {
            Text = _settings.StationClosed ? "Turn Off Closed Page" : "Turn On Closed Page",
            Bounds = new Rectangle(350, 92, 210, 42),
            BackColor = _settings.StationClosed
                ? Color.FromArgb(118, 196, 66)
                : Color.FromArgb(245, 130, 32),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        closedPageButton.Click += (_, _) => Complete(StaffSettingsAction.ToggleStationClosed);
        var closedPageNote = new Label
        {
            AutoSize = false,
            Text = "When this page is on, Return to Kiosk keeps the closed message visible and the screensaver remains disabled.",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(16, 24, 32),
            Bounds = new Rectangle(18, 30, 540, 50)
        };
        var closedPageGroup = new GroupBox
        {
            Text = "Waiver Station Closed Page",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            Bounds = new Rectangle(20, 25, 580, 170)
        };
        closedPageGroup.Controls.AddRange([
            closedPageNote, closedPageStatus, closedPageButton]);

        var screensaverLabel = new Label
        {
            Text = "Start screensaver after:",
            AutoSize = false,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(16, 24, 32),
            Bounds = new Rectangle(18, 102, 170, 30),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _screensaverMinutes.Minimum = 1;
        _screensaverMinutes.Maximum = 240;
        _screensaverMinutes.Value = Math.Clamp(_settings.ScreensaverTimeoutMinutes, 1, 240);
        _screensaverMinutes.TextAlign = HorizontalAlignment.Center;
        _screensaverMinutes.ForeColor = Color.FromArgb(16, 24, 32);
        _screensaverMinutes.Bounds = new Rectangle(195, 101, 75, 32);
        _screensaverMinutes.ValueChanged += (_, _) =>
            _screensaverSaveButton.Text = "Save Time";
        var screensaverMinutesLabel = new Label
        {
            Text = "minutes without touch or keyboard use",
            AutoSize = false,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(16, 24, 32),
            Bounds = new Rectangle(282, 102, 278, 30),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _screensaverSaveButton.Text = "Save Time";
        _screensaverSaveButton.Bounds = new Rectangle(18, 147, 150, 40);
        _screensaverSaveButton.BackColor = Color.FromArgb(105, 210, 236);
        _screensaverSaveButton.ForeColor = Color.FromArgb(16, 24, 32);
        _screensaverSaveButton.FlatStyle = FlatStyle.Flat;
        _screensaverSaveButton.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _screensaverSaveButton.Click += (_, _) => SaveScreensaverTimeout();
        var screensaverNote = new Label
        {
            AutoSize = false,
            Text = "The video runs only while the waiver station is open. Touching the screen or pressing a key clears the previous session and loads a fresh starting page.",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(16, 24, 32),
            Bounds = new Rectangle(18, 30, 540, 58)
        };
        var screensaverGroup = new GroupBox
        {
            Text = "Video Screensaver",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(20, 215, 580, 215)
        };
        screensaverGroup.Controls.AddRange([
            screensaverNote, screensaverLabel, _screensaverMinutes,
            screensaverMinutesLabel, _screensaverSaveButton]);
        stationTab.Controls.AddRange([closedPageGroup, screensaverGroup]);

        _businessHoursEnabled.Text = "Use automatic business hours";
        _businessHoursEnabled.Checked = _settings.BusinessHoursEnabled;
        _businessHoursEnabled.AutoSize = true;
        _businessHoursEnabled.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        _businessHoursEnabled.ForeColor = Color.FromArgb(117, 68, 154);
        _businessHoursEnabled.Location = new Point(20, 16);
        _businessHoursEnabled.CheckedChanged += (_, _) =>
        {
            UpdateBusinessHoursControlState();
            _businessHoursSaveButton.Text = "Save Business Hours";
        };

        _businessHoursStatus.AutoSize = false;
        _businessHoursStatus.Text = DescribeBusinessHoursStatus(_settings);
        _businessHoursStatus.Font = new Font("Segoe UI", 9.2f, FontStyle.Bold);
        _businessHoursStatus.ForeColor = _settings.BusinessHoursEnabled
            ? Color.FromArgb(8, 119, 189)
            : Color.FromArgb(83, 97, 109);
        _businessHoursStatus.Bounds = new Rectangle(20, 44, 580, 34);

        var weeklyHoursGroup = new GroupBox
        {
            Text = "Weekly Hours",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(15, 76, 590, 270)
        };
        weeklyHoursGroup.Controls.AddRange([
            new Label
            {
                Text = "Day", AutoSize = false, Bounds = new Rectangle(18, 29, 92, 22),
                ForeColor = Color.FromArgb(16, 24, 32), Font = new Font("Segoe UI", 9, FontStyle.Bold)
            },
            new Label
            {
                Text = "Open", AutoSize = false, Bounds = new Rectangle(112, 29, 58, 22),
                ForeColor = Color.FromArgb(16, 24, 32), Font = new Font("Segoe UI", 9, FontStyle.Bold),
                TextAlign = ContentAlignment.TopCenter
            },
            new Label
            {
                Text = "Opening time", AutoSize = false, Bounds = new Rectangle(184, 29, 142, 22),
                ForeColor = Color.FromArgb(16, 24, 32), Font = new Font("Segoe UI", 9, FontStyle.Bold),
                TextAlign = ContentAlignment.TopCenter
            },
            new Label
            {
                Text = "Closing time", AutoSize = false, Bounds = new Rectangle(348, 29, 142, 22),
                ForeColor = Color.FromArgb(16, 24, 32), Font = new Font("Segoe UI", 9, FontStyle.Bold),
                TextAlign = ContentAlignment.TopCenter
            }
        ]);

        for (var index = 0; index < KioskBusinessDayHours.OrderedDays.Length; index++)
        {
            var day = KioskBusinessDayHours.OrderedDays[index];
            var schedule = _settings.BusinessHours.First(item => item.Day == day);
            var rowY = 54 + index * 30;
            var dayLabel = new Label
            {
                Text = day.ToString(), AutoSize = false,
                Bounds = new Rectangle(18, rowY + 3, 92, 25),
                ForeColor = Color.FromArgb(16, 24, 32)
            };
            var isOpen = new CheckBox
            {
                Checked = schedule.IsOpen,
                AutoSize = false,
                Bounds = new Rectangle(128, rowY + 3, 24, 24)
            };
            var opens = CreateBusinessTimePicker(schedule.OpenTime, 184, rowY);
            var closes = CreateBusinessTimePicker(schedule.CloseTime, 348, rowY);
            _businessDayControls[day] = (isOpen, opens, closes);
            isOpen.CheckedChanged += (_, _) =>
            {
                UpdateBusinessHoursControlState();
                _businessHoursSaveButton.Text = "Save Business Hours";
            };
            opens.ValueChanged += (_, _) => _businessHoursSaveButton.Text = "Save Business Hours";
            closes.ValueChanged += (_, _) => _businessHoursSaveButton.Text = "Save Business Hours";
            weeklyHoursGroup.Controls.AddRange([dayLabel, isOpen, opens, closes]);
        }

        var automationGroup = new GroupBox
        {
            Text = "Closed Display and Pre-Opening",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            Bounds = new Rectangle(15, 353, 590, 118)
        };
        var closedMinutesLabel = new Label
        {
            Text = "Show Business Closed screen for:", AutoSize = false,
            Bounds = new Rectangle(18, 30, 225, 28),
            ForeColor = Color.FromArgb(16, 24, 32), TextAlign = ContentAlignment.MiddleLeft
        };
        _businessClosedMinutes.Minimum = 1;
        _businessClosedMinutes.Maximum = 240;
        _businessClosedMinutes.Value = Math.Clamp(_settings.BusinessClosedMessageMinutes, 1, 240);
        _businessClosedMinutes.TextAlign = HorizontalAlignment.Center;
        _businessClosedMinutes.Bounds = new Rectangle(246, 29, 70, 30);
        _businessClosedMinutes.ValueChanged += (_, _) =>
            _businessHoursSaveButton.Text = "Save Business Hours";
        var closedMinutesSuffix = new Label
        {
            Text = "minutes, then black out", AutoSize = false,
            Bounds = new Rectangle(324, 30, 200, 28),
            ForeColor = Color.FromArgb(16, 24, 32), TextAlign = ContentAlignment.MiddleLeft
        };
        var preOpeningLabel = new Label
        {
            Text = "Start the screensaver:", AutoSize = false,
            Bounds = new Rectangle(18, 72, 225, 28),
            ForeColor = Color.FromArgb(16, 24, 32), TextAlign = ContentAlignment.MiddleLeft
        };
        _preOpeningScreensaverMinutes.Minimum = 0;
        _preOpeningScreensaverMinutes.Maximum = 240;
        _preOpeningScreensaverMinutes.Value = Math.Clamp(
            _settings.PreOpeningScreensaverMinutes, 0, 240);
        _preOpeningScreensaverMinutes.TextAlign = HorizontalAlignment.Center;
        _preOpeningScreensaverMinutes.Bounds = new Rectangle(246, 71, 70, 30);
        _preOpeningScreensaverMinutes.ValueChanged += (_, _) =>
            _businessHoursSaveButton.Text = "Save Business Hours";
        var preOpeningSuffix = new Label
        {
            Text = "minutes before opening (0 = off)", AutoSize = false,
            Bounds = new Rectangle(324, 72, 250, 28),
            ForeColor = Color.FromArgb(16, 24, 32), TextAlign = ContentAlignment.MiddleLeft
        };
        automationGroup.Controls.AddRange([
            closedMinutesLabel, _businessClosedMinutes, closedMinutesSuffix,
            preOpeningLabel, _preOpeningScreensaverMinutes, preOpeningSuffix]);

        _businessHoursSaveButton.Text = "Save Business Hours";
        _businessHoursSaveButton.Bounds = new Rectangle(20, 478, 180, 38);
        _businessHoursSaveButton.BackColor = Color.FromArgb(118, 196, 66);
        _businessHoursSaveButton.FlatStyle = FlatStyle.Flat;
        _businessHoursSaveButton.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _businessHoursSaveButton.Click += (_, _) => SaveBusinessHours();

        var previewClosedButton = new Button
        {
            Text = "Preview Closed Screen",
            Bounds = new Rectangle(210, 478, 180, 38),
            BackColor = Color.FromArgb(105, 210, 236),
            ForeColor = Color.FromArgb(16, 24, 32),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.2f, FontStyle.Bold)
        };
        previewClosedButton.Click += async (_, _) =>
        {
            previewClosedButton.Enabled = false;
            var previousOpacity = Opacity;
            try
            {
                TopMost = false;
                Opacity = 0;
                await Task.Yield();
                await _previewBusinessClosed();
            }
            catch (Exception ex)
            {
                Opacity = previousOpacity;
                TopMost = true;
                KioskLog.Write("Business Closed preview error: " +
                    ex.GetType().Name + " - " + ex.Message);
                MessageBox.Show(this,
                    "The Business Closed preview could not be displayed.\n\n" + ex.Message,
                    "Business Hours", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                Opacity = previousOpacity;
                TopMost = true;
                previewClosedButton.Enabled = true;
                Activate();
                BringToFront();
            }
        };

        var startBlackoutButton = new Button
        {
            Text = "Start Blackout Now",
            Bounds = new Rectangle(400, 478, 185, 38),
            BackColor = Color.Black,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        startBlackoutButton.Click += (_, _) => Complete(StaffSettingsAction.StartBusinessBlackout);
        businessHoursTab.Controls.AddRange([
            _businessHoursEnabled, _businessHoursStatus, weeklyHoursGroup,
            automationGroup, _businessHoursSaveButton, previewClosedButton,
            startBlackoutButton]);
        UpdateBusinessHoursControlState();

        var staffToolsGroup = new GroupBox
        {
            Text = "Advertisements and Staff Tools",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            Bounds = new Rectangle(20, 25, 580, 215)
        };
        staffToolsGroup.Controls.AddRange([
            advertisementsButton, thankYouPreviewButton, changePasswordButton,
            remoteManagementButton]);
        var staffToolsNote = new Label
        {
            AutoSize = false,
            Text = "Advertisement schedules and the thank-you preview use the date and time selected on the Date & Time tab.",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(16, 24, 32),
            Bounds = new Rectangle(30, 265, 560, 55)
        };
        staffToolsTab.Controls.AddRange([staffToolsGroup, staffToolsNote]);
        var returnButton = new Button
        {
            Text = "Return to Kiosk",
            Bounds = new Rectangle(620, 652, 190, 45),
            BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        returnButton.Click += (_, _) => Complete(StaffSettingsAction.ReturnToKiosk);

        CancelButton = returnButton;
        Controls.AddRange([
            heading, _settingsTabs, exitButton, returnButton]);
        KioskTheme.Apply(this, KioskTheme.Evaluate(_settings, DateTime.Now).IsDark);
    }

    private void UpdateScheduledThemeControls()
    {
        var enabled = _scheduledDarkEnabled.Checked;
        foreach (var check in _scheduledDarkDays.Values) check.Enabled = enabled;
        _scheduledDarkTime.Enabled = enabled;
    }

    private void SaveAppearance(Button saveButton)
    {
        var selectedDays = _scheduledDarkDays
            .Where(pair => pair.Value.Checked)
            .Select(pair => pair.Key)
            .ToArray();
        if (_scheduledDarkEnabled.Checked && selectedDays.Length == 0)
        {
            MessageBox.Show(this,
                "Select at least one day for scheduled Dark mode.",
                "Appearance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var oldMode = _settings.ThemeMode;
        var oldEnabled = _settings.ScheduledDarkEnabled;
        var oldDays = _settings.ScheduledDarkDays;
        var oldTime = _settings.ScheduledDarkTime;
        try
        {
            _settings.ThemeMode = _themeMode.SelectedIndex switch
            {
                1 => KioskThemeMode.Light,
                2 => KioskThemeMode.Dark,
                _ => KioskThemeMode.Auto
            };
            _settings.ScheduledDarkEnabled = _scheduledDarkEnabled.Checked;
            _settings.ScheduledDarkDays = selectedDays;
            _settings.ScheduledDarkTime = _scheduledDarkTime.Value.TimeOfDay;
            _settings.Save();

            var status = KioskTheme.Evaluate(_settings, DateTime.Now);
            _themeStatus.Text = DescribeThemeStatus();
            KioskTheme.Apply(this, status.IsDark);
            _settingsTabs.Invalidate();
            saveButton.Text = "Saved";
            KioskLog.Write("Kiosk appearance settings were updated. " + status.Description);
        }
        catch (Exception ex)
        {
            _settings.ThemeMode = oldMode;
            _settings.ScheduledDarkEnabled = oldEnabled;
            _settings.ScheduledDarkDays = oldDays;
            _settings.ScheduledDarkTime = oldTime;
            saveButton.Text = "Save Appearance";
            KioskLog.Write("Kiosk appearance settings error: " + ex.Message);
            MessageBox.Show(this,
                "The appearance settings could not be saved.\n\n" + ex.Message,
                "Appearance", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string DescribeThemeStatus()
    {
        var status = KioskTheme.Evaluate(_settings, DateTime.Now);
        return "Current kiosk appearance: " + (status.IsDark ? "DARK" : "LIGHT") +
               Environment.NewLine + status.Description;
    }

    private static DateTimePicker CreateBusinessTimePicker(TimeSpan time, int x, int y)
    {
        return new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "h:mm tt",
            ShowUpDown = true,
            Value = DateTime.Today + time,
            Bounds = new Rectangle(x, y, 142, 27),
            Font = new Font("Segoe UI", 9)
        };
    }

    private void UpdateBusinessHoursControlState()
    {
        foreach (var controls in _businessDayControls.Values)
        {
            controls.IsOpen.Enabled = _businessHoursEnabled.Checked;
            controls.Opens.Enabled = _businessHoursEnabled.Checked && controls.IsOpen.Checked;
            controls.Closes.Enabled = _businessHoursEnabled.Checked && controls.IsOpen.Checked;
        }

        _businessClosedMinutes.Enabled = _businessHoursEnabled.Checked;
        _preOpeningScreensaverMinutes.Enabled = _businessHoursEnabled.Checked;
    }

    private void SaveBusinessHours()
    {
        var previousEnabled = _settings.BusinessHoursEnabled;
        var previousClosedMinutes = _settings.BusinessClosedMessageMinutes;
        var previousPreOpeningMinutes = _settings.PreOpeningScreensaverMinutes;
        var previousSchedules = _settings.BusinessHours
            .Select(schedule => new KioskBusinessDayHours
            {
                Day = schedule.Day,
                IsOpen = schedule.IsOpen,
                OpenTime = schedule.OpenTime,
                CloseTime = schedule.CloseTime
            })
            .ToList();

        var schedules = new List<KioskBusinessDayHours>();
        foreach (var day in KioskBusinessDayHours.OrderedDays)
        {
            var controls = _businessDayControls[day];
            var openTime = controls.Opens.Value.TimeOfDay;
            var closeTime = controls.Closes.Value.TimeOfDay;
            if (controls.IsOpen.Checked && closeTime <= openTime)
            {
                MessageBox.Show(this,
                    day + " closing time must be later than its opening time.",
                    "Business Hours", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                controls.Closes.Focus();
                return;
            }

            schedules.Add(new KioskBusinessDayHours
            {
                Day = day,
                IsOpen = controls.IsOpen.Checked,
                OpenTime = openTime,
                CloseTime = closeTime
            });
        }

        try
        {
            _settings.BusinessHoursEnabled = _businessHoursEnabled.Checked;
            _settings.BusinessClosedMessageMinutes = (int)_businessClosedMinutes.Value;
            _settings.PreOpeningScreensaverMinutes = (int)_preOpeningScreensaverMinutes.Value;
            _settings.BusinessHours = schedules;
            _settings.Save();
            _businessHoursStatus.Text = DescribeBusinessHoursStatus(_settings);
            _businessHoursStatus.ForeColor = _settings.BusinessHoursEnabled
                ? KioskTheme.AccentText(IsDarkTheme)
                : KioskTheme.MutedText(IsDarkTheme);
            _businessHoursSaveButton.Text = "Saved";
            KioskLog.Write("Business hours settings were updated.");
        }
        catch (Exception ex)
        {
            _settings.BusinessHoursEnabled = previousEnabled;
            _settings.BusinessClosedMessageMinutes = previousClosedMinutes;
            _settings.PreOpeningScreensaverMinutes = previousPreOpeningMinutes;
            _settings.BusinessHours = previousSchedules;
            _businessHoursSaveButton.Text = "Save Business Hours";
            KioskLog.Write("Business hours settings error: " +
                ex.GetType().Name + " - " + ex.Message);
            MessageBox.Show(this,
                "The business hours could not be saved.\n\n" + ex.Message,
                "Business Hours", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string DescribeBusinessHoursStatus(KioskSettings settings)
    {
        var status = BusinessHoursCalculator.Evaluate(settings, DateTime.Now);
        return status.Mode switch
        {
            BusinessHoursMode.Disabled =>
                "Automatic business hours are OFF. The kiosk remains available.",
            BusinessHoursMode.Open =>
                "OPEN NOW — scheduled to close at " +
                status.CurrentClosing?.ToString("h:mm tt") + ".",
            BusinessHoursMode.PreOpening =>
                "PRE-OPENING — screensaver window for " +
                status.NextOpening?.ToString("dddd 'at' h:mm tt") + ".",
            _ when status.NextOpening.HasValue =>
                "CLOSED NOW — next opening is " +
                status.NextOpening.Value.ToString("dddd 'at' h:mm tt") + ".",
            _ => "CLOSED NOW — no opening day is currently scheduled."
        };
    }

    private async Task CheckConnectionAsync()
    {
        _connectionButton.Enabled = false;
        _connectionResult.Text = "Checking the waiver website…";
        _connectionResult.ForeColor = KioskTheme.MutedText(IsDarkTheme);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "MulletHopWaiverKiosk/" + KioskUpdater.CurrentVersion);
            using var response = await client.GetAsync(
                _connectionTestUrl, HttpCompletionOption.ResponseHeadersRead);
            if (IsDisposed) return;

            if (response.IsSuccessStatusCode)
            {
                _connectionResult.Text = "Connected — the waiver website responded successfully.";
                _connectionResult.ForeColor = KioskTheme.SuccessText(IsDarkTheme);
            }
            else
            {
                _connectionResult.Text = $"Website reached — response: {(int)response.StatusCode} {response.ReasonPhrase}";
                _connectionResult.ForeColor = KioskTheme.WarningText(IsDarkTheme);
            }
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _connectionResult.Text = "Connection failed — " + ex.Message;
            _connectionResult.ForeColor = KioskTheme.ErrorText(IsDarkTheme);
        }
        finally
        {
            if (!IsDisposed)
                _connectionButton.Enabled = true;
        }
    }

    private void SaveScreensaverTimeout()
    {
        var previousValue = _settings.ScreensaverTimeoutMinutes;
        try
        {
            _settings.ScreensaverTimeoutMinutes = (int)_screensaverMinutes.Value;
            _settings.Save();
            _screensaverSaveButton.Text = "Saved";
            KioskLog.Write("Screensaver inactivity time changed to " +
                _settings.ScreensaverTimeoutMinutes + " minute(s).");
        }
        catch (Exception ex)
        {
            _settings.ScreensaverTimeoutMinutes = previousValue;
            _screensaverMinutes.Value = Math.Clamp(previousValue, 1, 240);
            _screensaverSaveButton.Text = "Save Time";
            KioskLog.Write("Screensaver setting error: " +
                ex.GetType().Name + " - " + ex.Message);
            MessageBox.Show(this,
                "The screensaver time could not be saved.\n\n" + ex.Message,
                "Staff Settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task CheckForUpdateAsync()
    {
        _updateButton.Enabled = false;
        _updateResult.Text = "Checking GitHub for an update…";
        _updateResult.ForeColor = KioskTheme.MutedText(IsDarkTheme);

        try
        {
            var result = await KioskUpdater.CheckDownloadAndApplyAsync();
            if (IsDisposed) return;

            _updateResult.Text = result.Message;
            _updateResult.ForeColor = result.Status switch
            {
                KioskUpdateStatus.UpToDate => KioskTheme.SuccessText(IsDarkTheme),
                KioskUpdateStatus.Applying => KioskTheme.SuccessText(IsDarkTheme),
                KioskUpdateStatus.NotConfigured => KioskTheme.WarningText(IsDarkTheme),
                KioskUpdateStatus.NotInstalled => KioskTheme.WarningText(IsDarkTheme),
                _ => KioskTheme.ErrorText(IsDarkTheme)
            };
        }
        finally
        {
            if (!IsDisposed)
                _updateButton.Enabled = true;
        }
    }

    private void Complete(StaffSettingsAction action)
    {
        SelectedAction = action;
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class RemoteManagementSettingsDialog : Form
{
    private readonly KioskSettings _settings;
    private readonly CheckBox _enabled = new();
    private readonly TextBox _stationName = new();
    private readonly TextBox _manualSetupCode = new();
    private readonly Label _manualStatus = new();
    private readonly Button _connectManual = new();
    private readonly Label _connectionValue = new();
    private readonly Label _adapterValue = new();
    private readonly Label _ipValue = new();
    private readonly Label _subnetValue = new();
    private readonly Label _gatewayValue = new();

    public RemoteManagementSettingsDialog(KioskSettings settings)
    {
        _settings = settings;
        Text = "Remote Control Options";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(760, 720);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "REMOTE KIOSK CONTROL",
            Font = new Font("Segoe UI", 20, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(25, 14, 710, 42)
        };
        var note = new Label
        {
            AutoSize = false,
            Text = "Turn on remote control and give this kiosk a name. Use automatic discovery normally. A controller can also send a secure request using the IPv4 address shown below; use the setup code only as a fallback.",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(52, 65, 76),
            Bounds = new Rectangle(48, 55, 664, 55)
        };

        _enabled.Text = "Enable remote control and network discovery for this kiosk";
        _enabled.Checked = settings.RemoteManagementEnabled;
        _enabled.AutoSize = true;
        _enabled.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _enabled.ForeColor = Color.FromArgb(8, 119, 189);
        _enabled.Location = new Point(35, 117);

        var stationLabel = MakeLabel("Kiosk Name:", 35, 164);
        _stationName.Text = settings.StationName;
        _stationName.MaxLength = 60;
        _stationName.Bounds = new Rectangle(170, 158, 555, 32);

        var networkGroup = new GroupBox
        {
            Text = "Current Device and Network Connection",
            Bounds = new Rectangle(30, 202, 700, 190),
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189)
        };
        ConfigureNetworkValue(_connectionValue, 168, 27, 500);
        ConfigureNetworkValue(_adapterValue, 168, 55, 500);
        ConfigureNetworkValue(_ipValue, 168, 83, 210);
        ConfigureNetworkValue(_subnetValue, 510, 83, 158);
        ConfigureNetworkValue(_gatewayValue, 168, 111, 210);
        var deviceIdValue = new Label
        {
            AutoSize = false,
            Text = settings.StationId,
            Bounds = new Rectangle(168, 139, 500, 24),
            ForeColor = Color.FromArgb(52, 65, 76),
            Font = new Font("Consolas", 9.2f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var refreshNetwork = new Button
        {
            Text = "Refresh",
            Bounds = new Rectangle(585, 108, 83, 28),
            BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        refreshNetwork.Click += (_, _) => RefreshNetworkDetails();
        networkGroup.Controls.AddRange([
            MakeNetworkLabel("Connection:", 18, 29),
            MakeNetworkLabel("Adapter:", 18, 57),
            MakeNetworkLabel("IPv4 Address:", 18, 85),
            MakeNetworkLabel("Subnet Mask:", 397, 85),
            MakeNetworkLabel("Default Gateway:", 18, 113),
            MakeNetworkLabel("Stable Device ID:", 18, 141),
            _connectionValue, _adapterValue, _ipValue, _subnetValue, _gatewayValue,
            deviceIdValue, refreshNetwork]);

        var manualGroup = new GroupBox
        {
            Text = "Manual Connection Fallback",
            Bounds = new Rectangle(30, 402, 700, 228),
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(245, 130, 32)
        };
        var manualNote = new Label
        {
            AutoSize = false,
            Text = "Fallback only: if automatic discovery and code-free IP pairing do not work, copy the setup code from Add Kiosk Manually on the controller and paste it below.",
            Bounds = new Rectangle(18, 25, 664, 48),
            ForeColor = Color.FromArgb(52, 65, 76),
            Font = new Font("Segoe UI", 9.2f),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _manualSetupCode.Multiline = true;
        _manualSetupCode.ScrollBars = ScrollBars.Vertical;
        _manualSetupCode.WordWrap = true;
        _manualSetupCode.MaxLength = 4096;
        _manualSetupCode.Bounds = new Rectangle(18, 79, 664, 62);
        _manualSetupCode.Font = new Font("Consolas", 8.5f);
        _manualSetupCode.PlaceholderText = "Paste the MHK1 setup code from the Kiosk Controller";
        _connectManual.Text = "Connect and Save";
        _connectManual.Bounds = new Rectangle(18, 151, 175, 42);
        _connectManual.BackColor = Color.FromArgb(245, 130, 32);
        _connectManual.FlatStyle = FlatStyle.Flat;
        _connectManual.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _connectManual.Click += async (_, _) => await ConnectManuallyAsync();
        _manualStatus.AutoSize = false;
        _manualStatus.Text = "The setup code is sensitive staff information. It is tested before anything is saved.";
        _manualStatus.Bounds = new Rectangle(208, 149, 474, 49);
        _manualStatus.ForeColor = Color.FromArgb(83, 97, 109);
        _manualStatus.TextAlign = ContentAlignment.MiddleLeft;
        _manualStatus.Font = new Font("Segoe UI", 8.8f, FontStyle.Bold);
        manualGroup.Controls.AddRange([
            manualNote, _manualSetupCode, _connectManual, _manualStatus]);

        var save = new Button
        {
            Text = "Save Options",
            Bounds = new Rectangle(30, 650, 190, 46),
            BackColor = Color.FromArgb(118, 196, 66),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        save.Click += (_, _) => SaveSettings();
        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(540, 650, 190, 46),
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([
            heading, note, _enabled, stationLabel, _stationName,
            networkGroup, manualGroup, save, cancel]);
        RefreshNetworkDetails();
        KioskTheme.Apply(this, KioskTheme.Evaluate(_settings, DateTime.Now).IsDark);
    }

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 10, FontStyle.Bold),
        ForeColor = Color.FromArgb(16, 24, 32),
        Location = new Point(x, y)
    };

    private static Label MakeNetworkLabel(string text, int x, int y) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 9, FontStyle.Bold),
        ForeColor = Color.FromArgb(52, 65, 76),
        Location = new Point(x, y)
    };

    private static void ConfigureNetworkValue(Label label, int x, int y, int width)
    {
        label.AutoSize = false;
        label.Bounds = new Rectangle(x, y, width, 24);
        label.ForeColor = Color.FromArgb(16, 24, 32);
        label.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.AutoEllipsis = true;
    }

    private void RefreshNetworkDetails()
    {
        var details = KioskNetworkDetailsProvider.GetCurrent();
        _connectionValue.Text = details.ConnectionName;
        _adapterValue.Text = details.AdapterDescription;
        _ipValue.Text = details.IpAddress;
        _subnetValue.Text = details.SubnetMask;
        _gatewayValue.Text = details.DefaultGateway;
    }

    private async Task ConnectManuallyAsync()
    {
        KioskManualSetupPayload payload;
        try
        {
            payload = KioskDiscoveryProtocol.ParseManualSetupCode(_manualSetupCode.Text);
        }
        catch (InvalidDataException ex)
        {
            _manualStatus.Text = ex.Message;
            _manualStatus.ForeColor = Color.FromArgb(180, 35, 24);
            _manualSetupCode.Focus();
            return;
        }

        var replacing = RemoteManagementProtocol.IsConfigurationValid(
            _settings.RemoteControllerUrl, _settings.RemotePairingKey, out _) &&
            (!string.Equals(
                 _settings.RemoteControllerUrl,
                 payload.ControllerAddress,
                 StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(_settings.RemotePairingKey, payload.PairingKey, StringComparison.Ordinal));
        if (replacing)
        {
            var answer = MessageBox.Show(this,
                "This kiosk is already connected to a controller. Replace that saved connection with " +
                payload.ControllerName + "?",
                "Replace Controller Connection?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;
        }

        _connectManual.Enabled = false;
        _connectManual.Text = "Testing…";
        _manualStatus.Text = "Testing the secure connection to " + payload.ControllerName + "…";
        _manualStatus.ForeColor = Color.FromArgb(8, 119, 189);
        try
        {
            var test = await RemoteManagementProtocol.TestAsync(
                payload.ControllerAddress, payload.PairingKey);
            if (!test.Success)
            {
                _manualStatus.Text = test.Message;
                _manualStatus.ForeColor = Color.FromArgb(180, 35, 24);
                return;
            }

            var stationName = _stationName.Text.Trim();
            _settings.StationName = string.IsNullOrWhiteSpace(stationName)
                ? Environment.MachineName
                : stationName;
            _settings.RemoteControllerUrl = payload.ControllerAddress;
            _settings.RemotePairingKey = payload.PairingKey;
            _settings.RemoteManagementEnabled = true;
            _settings.Save();
            KioskLog.Write(
                $"Manual kiosk setup connected {_settings.StationName} to {payload.ControllerName} at {payload.ControllerAddress}.");
            MessageBox.Show(this,
                "Connected securely to " + payload.ControllerName +
                ". This kiosk will now check in using its stable Device ID, even if DHCP changes the kiosk's IP address.",
                "Manual Connection Saved",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            if (!IsDisposed)
            {
                _connectManual.Enabled = true;
                _connectManual.Text = "Connect and Save";
            }
        }
    }

    private void SaveSettings()
    {
        var stationName = _stationName.Text.Trim();
        if (_enabled.Checked && string.IsNullOrWhiteSpace(stationName))
        {
            MessageBox.Show(this, "Enter a name for this kiosk.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _stationName.Focus();
            return;
        }

        _settings.RemoteManagementEnabled = _enabled.Checked;
        _settings.StationName = string.IsNullOrWhiteSpace(stationName)
            ? Environment.MachineName
            : stationName;
        _settings.Save();
        KioskLog.Write(_enabled.Checked
            ? "Remote kiosk control and network discovery were enabled for " +
              _settings.StationName + "."
            : "Remote kiosk control was disabled.");
        DialogResult = DialogResult.OK;
        Close();
    }
}

internal sealed class StaffPasswordChangeDialog : Form
{
    private readonly KioskSettings _settings;
    private readonly TextBox _currentPassword = CreatePasswordField();
    private readonly TextBox _newPassword = CreatePasswordField();
    private readonly TextBox _confirmNewPassword = CreatePasswordField();
    private readonly Label _verificationStatus = new();
    private readonly Button _changeButton = new();
    private string? _verifiedCurrentPassword;
    private bool IsDarkTheme => KioskTheme.Evaluate(_settings, DateTime.Now).IsDark;

    public StaffPasswordChangeDialog(KioskSettings settings)
    {
        _settings = settings;
        Text = "Change Staff Password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(620, 445);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "CHANGE STAFF PASSWORD",
            Font = new Font("Segoe UI", 20, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(25, 14, 570, 44)
        };
        var requirement = new Label
        {
            AutoSize = false,
            Text = "The staff password must contain between 4–8 numbers only.",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(182, 76, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(35, 59, 550, 35)
        };

        var currentGroup = new GroupBox
        {
            Text = "Verify Existing Password",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(30, 102, 560, 132)
        };
        var currentLabel = MakePasswordLabel("Confirm Current Password:", 18, 37, 178);
        _currentPassword.Bounds = new Rectangle(202, 31, 150, 32);
        var verifyButton = new Button
        {
            Text = "Verify Current Password",
            Bounds = new Rectangle(365, 29, 175, 37),
            BackColor = Color.FromArgb(105, 210, 236),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        verifyButton.Click += (_, _) => VerifyCurrentPassword();
        _verificationStatus.AutoSize = false;
        _verificationStatus.Text = "Current password has not been verified.";
        _verificationStatus.ForeColor = Color.FromArgb(83, 97, 109);
        _verificationStatus.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _verificationStatus.Bounds = new Rectangle(202, 75, 335, 30);
        currentGroup.Controls.AddRange([
            currentLabel, _currentPassword, verifyButton, _verificationStatus]);

        var newGroup = new GroupBox
        {
            Text = "Choose New Password",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            Bounds = new Rectangle(30, 246, 560, 120)
        };
        var newLabel = MakePasswordLabel("New Password:", 60, 36, 135);
        var confirmLabel = MakePasswordLabel("Confirm New Password:", 18, 77, 178);
        _newPassword.Bounds = new Rectangle(202, 30, 180, 32);
        _confirmNewPassword.Bounds = new Rectangle(202, 71, 180, 32);
        newGroup.Controls.AddRange([
            newLabel, confirmLabel, _newPassword, _confirmNewPassword]);

        _changeButton.Text = "OK";
        _changeButton.Bounds = new Rectangle(315, 386, 135, 40);
        _changeButton.BackColor = Color.FromArgb(118, 196, 66);
        _changeButton.FlatStyle = FlatStyle.Flat;
        _changeButton.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _changeButton.Enabled = false;
        _changeButton.Click += (_, _) => ChangePassword();
        var cancelButton = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(460, 386, 130, 40),
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        _currentPassword.TextChanged += (_, _) => ResetCurrentVerificationIfChanged();
        AcceptButton = _changeButton;
        CancelButton = cancelButton;
        Controls.AddRange([
            heading, requirement, currentGroup, newGroup, _changeButton, cancelButton]);
        Shown += (_, _) => _currentPassword.Focus();
        KioskTheme.Apply(this, KioskTheme.Evaluate(_settings, DateTime.Now).IsDark);
    }

    private static TextBox CreatePasswordField()
    {
        var field = new TextBox
        {
            UseSystemPasswordChar = true,
            MaxLength = 8,
            Font = new Font("Segoe UI", 11, FontStyle.Regular)
        };
        var cleaning = false;
        field.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && (e.KeyChar < '0' || e.KeyChar > '9'))
                e.Handled = true;
        };
        field.TextChanged += (_, _) =>
        {
            if (cleaning) return;
            var numbersOnly = new string(field.Text
                .Where(character => character >= '0' && character <= '9')
                .Take(8)
                .ToArray());
            if (numbersOnly == field.Text) return;
            cleaning = true;
            field.Text = numbersOnly;
            field.SelectionStart = field.Text.Length;
            cleaning = false;
        };
        return field;
    }

    private static Label MakePasswordLabel(string text, int x, int y, int width) => new()
    {
        AutoSize = false,
        Text = text,
        ForeColor = Color.FromArgb(16, 24, 32),
        Font = new Font("Segoe UI", 10, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleRight,
        Bounds = new Rectangle(x, y, width, 25)
    };

    private static bool IsValidPassword(string value) =>
        value.Length is >= 4 and <= 8 && value.All(character => character >= '0' && character <= '9');

    private void VerifyCurrentPassword()
    {
        var current = _currentPassword.Text;
        if (!IsValidPassword(current))
        {
            ShowProblem("The current staff password must contain between 4–8 numbers.", _currentPassword);
            return;
        }
        if (!_settings.VerifyPin(current))
        {
            _verifiedCurrentPassword = null;
            _changeButton.Enabled = false;
            _verificationStatus.Text = "Current password is incorrect.";
            _verificationStatus.ForeColor = KioskTheme.ErrorText(IsDarkTheme);
            ShowProblem("The current staff password is incorrect.", _currentPassword);
            return;
        }

        _verifiedCurrentPassword = current;
        _verificationStatus.Text = "Current password verified. The OK button is now available.";
        _verificationStatus.ForeColor = KioskTheme.SuccessText(IsDarkTheme);
        _changeButton.Enabled = true;
        _newPassword.Focus();
    }

    private void ResetCurrentVerificationIfChanged()
    {
        if (_verifiedCurrentPassword is null || _currentPassword.Text == _verifiedCurrentPassword)
            return;

        _verifiedCurrentPassword = null;
        _changeButton.Enabled = false;
        _verificationStatus.Text = "Current password changed. Verify it again.";
        _verificationStatus.ForeColor = KioskTheme.WarningText(IsDarkTheme);
    }

    private void ChangePassword()
    {
        var current = _currentPassword.Text;
        if (_verifiedCurrentPassword is null || current != _verifiedCurrentPassword ||
            !_settings.VerifyPin(current))
        {
            _verifiedCurrentPassword = null;
            _changeButton.Enabled = false;
            _verificationStatus.Text = "Verify the current password before continuing.";
            _verificationStatus.ForeColor = KioskTheme.ErrorText(IsDarkTheme);
            ShowProblem("Verify the current staff password before changing it.", _currentPassword);
            return;
        }
        if (!IsValidPassword(_newPassword.Text))
        {
            ShowProblem("The new staff password must contain between 4–8 numbers.", _newPassword);
            return;
        }
        if (!IsValidPassword(_confirmNewPassword.Text))
        {
            ShowProblem("Confirm the new staff password using between 4–8 numbers.", _confirmNewPassword);
            return;
        }
        if (_newPassword.Text != _confirmNewPassword.Text)
        {
            ShowProblem("The new password and Confirm New Password do not match.", _confirmNewPassword);
            return;
        }

        try
        {
            var previousSalt = _settings.StaffPinSalt;
            var previousHash = _settings.StaffPinHash;
            _settings.SetPin(_newPassword.Text);
            try
            {
                _settings.Save();
            }
            catch
            {
                _settings.StaffPinSalt = previousSalt;
                _settings.StaffPinHash = previousHash;
                throw;
            }
            MessageBox.Show(this,
                "The staff password has been successfully changed.",
                "Password Changed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "The staff password could not be saved. No password change was completed.\n\n" + ex.Message,
                "Password Change Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowProblem(string message, Control focusControl)
    {
        MessageBox.Show(this, message, "Password Change",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        focusControl.Focus();
    }
}

internal static class AdvertisementFiles
{
    public static void RecoverInterruptedSync(KioskSettings settings)
    {
        try
        {
            var backup = Directory.Exists(KioskSettings.DataDirectory)
                ? Directory.GetDirectories(
                        KioskSettings.DataDirectory,
                        "Advertisements.backup-*",
                        SearchOption.TopDirectoryOnly)
                    .Select(path => new DirectoryInfo(path))
                    .OrderByDescending(info => info.LastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            if (backup is null)
                return;

            if (Directory.Exists(KioskSettings.AdvertisementsDirectory))
            {
                var referencedFiles = settings.Advertisements
                    .Select(advertisement => Path.GetFileName(advertisement.ImageFileName))
                    .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
                    .ToArray();
                var currentCatalogMatches = referencedFiles.All(fileName =>
                    File.Exists(Path.Combine(KioskSettings.AdvertisementsDirectory, fileName)));
                if (currentCatalogMatches)
                    return;

                var backupCatalogMatches = referencedFiles.All(fileName =>
                    File.Exists(Path.Combine(backup.FullName, fileName)));
                if (!backupCatalogMatches)
                    return;

                var interruptedDirectory = Path.Combine(
                    KioskSettings.DataDirectory,
                    "Advertisements.incomplete-" + Guid.NewGuid().ToString("N"));
                Directory.Move(KioskSettings.AdvertisementsDirectory, interruptedDirectory);
            }

            Directory.Move(backup.FullName, KioskSettings.AdvertisementsDirectory);
            KioskLog.Write("Recovered the last local advertisement catalog after an interrupted sync.");
        }
        catch (Exception ex)
        {
            KioskLog.Write("Advertisement sync recovery error: " +
                ex.GetType().Name + " - " + ex.Message);
        }
    }

    public static string ImportJpeg(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
            throw new FileNotFoundException("The selected JPG could not be found.", sourcePath);
        if (info.Length > 25_000_000)
            throw new InvalidOperationException("The JPG must be smaller than 25 MB.");

        using (var image = Image.FromFile(sourcePath))
        {
            if (image.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                throw new InvalidOperationException("The selected file is not a valid JPG image.");
        }

        Directory.CreateDirectory(KioskSettings.AdvertisementsDirectory);
        var fileName = Guid.NewGuid().ToString("N") + ".jpg";
        File.Copy(sourcePath, Path.Combine(KioskSettings.AdvertisementsDirectory, fileName), false);
        return fileName;
    }

    public static string? GetSafePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var root = Path.GetFullPath(KioskSettings.AdvertisementsDirectory) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(KioskSettings.AdvertisementsDirectory, Path.GetFileName(fileName)));
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    public static void DeleteIfPresent(string? fileName)
    {
        try
        {
            var path = GetSafePath(fileName);
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            KioskLog.Write("Advertisement image cleanup error: " + ex.GetType().Name + " - " + ex.Message);
        }
    }
}

internal sealed class AdvertisementManagerDialog : Form
{
    private readonly KioskSettings _settings;
    private readonly DateTime? _previewNow;
    private readonly Func<IProgress<AdvertisementSyncProgress>?, Task<AdvertisementSyncResult>>
        _syncAdvertisements;
    private readonly ListView _list = new();
    private readonly PictureBox _preview = new();
    private readonly Label _details = new();
    private readonly Label _syncStatus = new();
    private readonly Label _lastSync = new();
    private readonly ProgressBar _syncProgress = new();
    private readonly Button _syncButton = new();
    private bool IsDarkTheme => KioskTheme.Evaluate(_settings, DateTime.Now).IsDark;

    public AdvertisementManagerDialog(
        KioskSettings settings,
        DateTime? previewNow,
        Func<IProgress<AdvertisementSyncProgress>?, Task<AdvertisementSyncResult>> syncAdvertisements)
    {
        _settings = settings;
        _previewNow = previewNow;
        _syncAdvertisements = syncAdvertisements;
        Text = "Manage Thank-You Page Advertisements";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(960, 730);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "SCHEDULED ADVERTISEMENTS",
            Font = new Font("Segoe UI", 19, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(25, 12, 650, 42)
        };
        var note = new Label
        {
            AutoSize = false,
            Text = previewNow.HasValue
                ? "Status is shown for the staff preview time. Active JPG ads appear beside the thank-you message."
                : "Active JPG advertisements appear beside the thank-you message. Multiple active ads rotate automatically.",
            ForeColor = Color.FromArgb(83, 97, 109),
            Bounds = new Rectangle(25, 51, 890, 25)
        };

        _list.Bounds = new Rectangle(25, 82, 620, 355);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.Columns.Add("Advertisement", 180);
        _list.Columns.Add("Schedule", 335);
        _list.Columns.Add("Status", 95);
        _list.SelectedIndexChanged += (_, _) => ShowSelectedPreview();
        _list.DoubleClick += (_, _) => EditSelected();

        _preview.Bounds = new Rectangle(675, 82, 250, 250);
        _preview.BorderStyle = BorderStyle.FixedSingle;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(247, 251, 253);
        _details.AutoSize = false;
        _details.Bounds = new Rectangle(675, 345, 250, 90);
        _details.ForeColor = Color.FromArgb(16, 24, 32);
        _details.Font = new Font("Segoe UI", 9.5f);

        var syncGroup = new GroupBox
        {
            Text = "Kiosk Manager Advertisement Sync",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(25, 455, 900, 165)
        };
        _syncStatus.AutoSize = false;
        _syncStatus.Text = _settings.AdvertisementLastSyncStatus;
        _syncStatus.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _syncStatus.ForeColor = Color.FromArgb(52, 65, 76);
        _syncStatus.Bounds = new Rectangle(18, 27, 640, 40);
        _lastSync.AutoSize = false;
        _lastSync.Font = new Font("Segoe UI", 9.5f);
        _lastSync.ForeColor = Color.FromArgb(83, 97, 109);
        _lastSync.Bounds = new Rectangle(18, 67, 640, 28);
        _syncProgress.Minimum = 0;
        _syncProgress.Maximum = 100;
        _syncProgress.Style = ProgressBarStyle.Continuous;
        _syncProgress.Bounds = new Rectangle(18, 105, 640, 24);
        _syncButton.Text = "Sync Ads Now";
        _syncButton.Bounds = new Rectangle(680, 48, 195, 52);
        _syncButton.BackColor = Color.FromArgb(117, 68, 154);
        _syncButton.ForeColor = Color.White;
        _syncButton.FlatStyle = FlatStyle.Flat;
        _syncButton.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _syncButton.Click += async (_, _) => await SyncNowAsync();
        var syncNote = new Label
        {
            AutoSize = false,
            Text = "Manager changes also sync automatically while this kiosk is connected.",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(83, 97, 109),
            Bounds = new Rectangle(680, 105, 195, 38),
            TextAlign = ContentAlignment.TopCenter
        };
        syncGroup.Controls.AddRange([
            _syncStatus, _lastSync, _syncProgress, _syncButton, syncNote]);
        RefreshSyncStatus();

        var addButton = CreateButton("Add Advertisement", 25, Color.FromArgb(118, 196, 66));
        addButton.Click += (_, _) => AddAdvertisement();
        var editButton = CreateButton("Edit", 205, Color.FromArgb(105, 210, 236));
        editButton.Click += (_, _) => EditSelected();
        var toggleButton = CreateButton("Enable / Disable", 335, Color.FromArgb(255, 222, 89));
        toggleButton.Width = 160;
        toggleButton.Click += (_, _) => ToggleSelected();
        var deleteButton = CreateButton("Delete", 505, Color.FromArgb(245, 130, 32));
        deleteButton.Click += (_, _) => DeleteSelected();
        var closeButton = new Button
        {
            Text = "Close",
            Bounds = new Rectangle(805, 655, 120, 42),
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        AcceptButton = closeButton;
        CancelButton = closeButton;
        Controls.AddRange([
            heading, note, _list, _preview, _details, syncGroup,
            addButton, editButton, toggleButton, deleteButton, closeButton]);
        FormClosed += (_, _) => _preview.Image?.Dispose();
        KioskTheme.Apply(this, KioskTheme.Evaluate(_settings, DateTime.Now).IsDark);
        RefreshList();
    }

    private static Button CreateButton(string text, int x, Color color) => new()
    {
        Text = text,
        Bounds = new Rectangle(x, 655, 120, 42),
        BackColor = color,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
    };

    private KioskAdvertisement? SelectedAdvertisement =>
            _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as KioskAdvertisement : null;

    private async Task SyncNowAsync()
    {
        _syncButton.Enabled = false;
        _syncProgress.Value = 0;
        _syncStatus.Text = "Starting advertisement sync…";
        _syncStatus.ForeColor = KioskTheme.AccentText(IsDarkTheme);
        try
        {
            var progress = new Progress<AdvertisementSyncProgress>(update =>
            {
                if (IsDisposed) return;
                _syncProgress.Value = Math.Clamp(update.Percent, 0, 100);
                _syncStatus.Text = update.Message;
            });
            var result = await _syncAdvertisements(progress);
            if (IsDisposed) return;

            RefreshSyncStatus();
            if (result.Success)
            {
                _syncProgress.Value = 100;
                _syncStatus.ForeColor = KioskTheme.SuccessText(IsDarkTheme);
                RefreshList();
            }
            else
            {
                _syncProgress.Value = 0;
                _syncStatus.Text = result.Message;
                _syncStatus.ForeColor = KioskTheme.ErrorText(IsDarkTheme);
                MessageBox.Show(this, result.Message, "Advertisement Sync",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            if (!IsDisposed)
                _syncButton.Enabled = true;
        }
    }

    private void RefreshSyncStatus()
    {
        _syncStatus.Text = _settings.AdvertisementLastSyncStatus;
        _lastSync.Text = _settings.AdvertisementLastSyncUtc.HasValue
            ? "Last successful sync: " +
              _settings.AdvertisementLastSyncUtc.Value.ToLocalTime()
                  .ToString("MMM d, yyyy h:mm:ss tt")
            : "Last successful sync: Never";
    }

    private void RefreshList(string? selectId = null)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var advertisement in _settings.Advertisements.OrderBy(ad => ad.Name))
        {
            var scheduleNow = _previewNow ?? DateTime.Now;
            var status = !advertisement.Enabled
                ? "Disabled"
                : advertisement.IsActive(scheduleNow)
                    ? _previewNow.HasValue ? "Active in preview" : "Active now"
                    : "Scheduled";
            var item = new ListViewItem(advertisement.Name) { Tag = advertisement };
            item.SubItems.Add(advertisement.ScheduleSummary());
            item.SubItems.Add(status);
            if (!advertisement.Enabled) item.ForeColor = KioskTheme.MutedText(IsDarkTheme);
            _list.Items.Add(item);
            if (advertisement.Id == selectId) item.Selected = true;
        }
        _list.EndUpdate();
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
            _list.Items[0].Selected = true;
        ShowSelectedPreview();
    }

    private void ShowSelectedPreview()
    {
        _preview.Image?.Dispose();
        _preview.Image = null;
        var advertisement = SelectedAdvertisement;
        if (advertisement is null)
        {
            _details.Text = "Select an advertisement to preview it.";
            return;
        }

        var path = AdvertisementFiles.GetSafePath(advertisement.ImageFileName);
        if (path is not null && File.Exists(path))
        {
            try
            {
                using var image = Image.FromFile(path);
                _preview.Image = new Bitmap(image);
            }
            catch
            {
                _details.Text = "The saved JPG could not be opened.";
            }
        }
        _details.Text = advertisement.Name + Environment.NewLine +
            advertisement.ScheduleSummary() + Environment.NewLine +
            (advertisement.Enabled ? "Enabled" : "Disabled");
    }

    private void AddAdvertisement()
    {
        using var editor = new AdvertisementEditorDialog(
            dark: KioskTheme.Evaluate(_settings, DateTime.Now).IsDark);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Advertisement is null) return;
        _settings.Advertisements.Add(editor.Advertisement);
        if (SaveSettings()) RefreshList(editor.Advertisement.Id);
    }

    private void EditSelected()
    {
        var selected = SelectedAdvertisement;
        if (selected is null) return;
        var oldImage = selected.ImageFileName;
        using var editor = new AdvertisementEditorDialog(
            selected, KioskTheme.Evaluate(_settings, DateTime.Now).IsDark);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Advertisement is null) return;
        var index = _settings.Advertisements.FindIndex(ad => ad.Id == selected.Id);
        if (index < 0) return;
        _settings.Advertisements[index] = editor.Advertisement;
        if (SaveSettings())
        {
            if (!string.Equals(oldImage, editor.Advertisement.ImageFileName, StringComparison.OrdinalIgnoreCase))
                AdvertisementFiles.DeleteIfPresent(oldImage);
            RefreshList(editor.Advertisement.Id);
        }
    }

    private void ToggleSelected()
    {
        var selected = SelectedAdvertisement;
        if (selected is null) return;
        selected.Enabled = !selected.Enabled;
        if (SaveSettings()) RefreshList(selected.Id);
    }

    private void DeleteSelected()
    {
        var selected = SelectedAdvertisement;
        if (selected is null) return;
        if (MessageBox.Show(this,
                $"Delete the advertisement '{selected.Name}' and its saved JPG?",
                "Delete Advertisement", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _settings.Advertisements.RemoveAll(ad => ad.Id == selected.Id);
        if (SaveSettings())
        {
            AdvertisementFiles.DeleteIfPresent(selected.ImageFileName);
            RefreshList();
        }
    }

    private bool SaveSettings()
    {
        try
        {
            _settings.Save();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The advertisement settings could not be saved.\n\n" + ex.Message,
                "Advertisements", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
}

internal sealed class AdvertisementEditorDialog : Form
{
    private readonly TextBox _name = new();
    private readonly Label _fileLabel = new();
    private readonly PictureBox _preview = new();
    private readonly CheckBox _enabled = new();
    private readonly RadioButton _specificDates = new();
    private readonly RadioButton _weekly = new();
    private readonly DateTimePicker _startDate = new();
    private readonly DateTimePicker _startTime = new();
    private readonly DateTimePicker _endDate = new();
    private readonly DateTimePicker _endTime = new();
    private readonly DateTimePicker _weeklyStart = new();
    private readonly DateTimePicker _weeklyEnd = new();
    private readonly Dictionary<DayOfWeek, CheckBox> _dayChecks = [];
    private readonly KioskAdvertisement _working;
    private string? _selectedSourcePath;

    public KioskAdvertisement? Advertisement { get; private set; }

    public AdvertisementEditorDialog(KioskAdvertisement? existing = null, bool dark = false)
    {
        _working = existing?.Clone() ?? new KioskAdvertisement();
        Text = existing is null ? "Add Advertisement" : "Edit Advertisement";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(790, 690);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = existing is null ? "ADD ADVERTISEMENT" : "EDIT ADVERTISEMENT",
            Font = new Font("Segoe UI", 19, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(25, 12, 740, 42)
        };

        var imageGroup = new GroupBox
        {
            Text = "JPG Advertisement",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(25, 60, 740, 220)
        };
        _preview.Bounds = new Rectangle(18, 30, 285, 170);
        _preview.BorderStyle = BorderStyle.FixedSingle;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(247, 251, 253);
        var uploadButton = new Button
        {
            Text = "Upload JPG…",
            Bounds = new Rectangle(330, 35, 150, 40),
            BackColor = Color.FromArgb(105, 210, 236),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        uploadButton.Click += (_, _) => SelectJpeg();
        _fileLabel.AutoSize = false;
        _fileLabel.Bounds = new Rectangle(495, 35, 220, 45);
        _fileLabel.ForeColor = Color.FromArgb(83, 97, 109);
        var nameLabel = new Label
        {
            Text = "Advertisement name:", AutoSize = true,
            ForeColor = Color.FromArgb(16, 24, 32), Location = new Point(330, 105)
        };
        _name.Bounds = new Rectangle(330, 130, 385, 32);
        _name.MaxLength = 80;
        _enabled.Text = "Advertisement is enabled";
        _enabled.AutoSize = true;
        _enabled.ForeColor = Color.FromArgb(16, 24, 32);
        _enabled.Location = new Point(330, 175);
        imageGroup.Controls.AddRange([_preview, uploadButton, _fileLabel, nameLabel, _name, _enabled]);

        var scheduleGroup = new GroupBox
        {
            Text = "Schedule",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            Bounds = new Rectangle(25, 292, 740, 310)
        };
        _specificDates.Text = "Run for specific dates";
        _specificDates.AutoSize = true;
        _specificDates.Location = new Point(22, 28);
        _weekly.Text = "Repeat every week";
        _weekly.AutoSize = true;
        _weekly.Location = new Point(235, 28);
        _specificDates.CheckedChanged += (_, _) => UpdateScheduleControls();
        _weekly.CheckedChanged += (_, _) => UpdateScheduleControls();

        var specificPanel = new GroupBox
        {
            Name = "specificPanel", Text = "Specific date and time range",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(18, 57, 700, 125)
        };
        specificPanel.Controls.AddRange([
            MakeLabel("Date", 90, 24), MakeLabel("Time", 365, 24),
            MakeLabel("Starts:", 20, 50), MakeLabel("Ends:", 20, 88)]);
        ConfigureDatePicker(_startDate, 90, 43, 245);
        ConfigureTimePicker(_startTime, 365, 43, 140);
        ConfigureDatePicker(_endDate, 90, 81, 245);
        ConfigureTimePicker(_endTime, 365, 81, 140);
        specificPanel.Controls.AddRange([_startDate, _startTime, _endDate, _endTime]);

        var weeklyPanel = new GroupBox
        {
            Name = "weeklyPanel", Text = "Weekly repeating schedule",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(18, 190, 700, 100)
        };
        var dayNames = new[]
        {
            (DayOfWeek.Sunday, "Sun"), (DayOfWeek.Monday, "Mon"),
            (DayOfWeek.Tuesday, "Tue"), (DayOfWeek.Wednesday, "Wed"),
            (DayOfWeek.Thursday, "Thu"), (DayOfWeek.Friday, "Fri"),
            (DayOfWeek.Saturday, "Sat")
        };
        for (var i = 0; i < dayNames.Length; i++)
        {
            var check = new CheckBox
            {
                Text = dayNames[i].Item2, AutoSize = true,
                ForeColor = Color.FromArgb(16, 24, 32), Location = new Point(18 + i * 85, 28)
            };
            _dayChecks[dayNames[i].Item1] = check;
            weeklyPanel.Controls.Add(check);
        }
        weeklyPanel.Controls.AddRange([
            MakeLabel("Daily start:", 18, 68), MakeLabel("Daily end:", 295, 68)]);
        ConfigureTimePicker(_weeklyStart, 105, 63, 130);
        ConfigureTimePicker(_weeklyEnd, 382, 63, 130);
        weeklyPanel.Controls.AddRange([_weeklyStart, _weeklyEnd]);
        scheduleGroup.Controls.AddRange([_specificDates, _weekly, specificPanel, weeklyPanel]);

        var saveButton = new Button
        {
            Text = "Save Advertisement", Bounds = new Rectangle(455, 625, 170, 42),
            BackColor = Color.FromArgb(118, 196, 66), FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        saveButton.Click += (_, _) => SaveAndClose();
        var cancelButton = new Button
        {
            Text = "Cancel", Bounds = new Rectangle(635, 625, 130, 42),
            DialogResult = DialogResult.Cancel, BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.AddRange([heading, imageGroup, scheduleGroup, saveButton, cancelButton]);
        FormClosed += (_, _) => _preview.Image?.Dispose();
        LoadWorkingValues();
        KioskTheme.Apply(this, dark);
    }

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text, AutoSize = true, ForeColor = Color.FromArgb(16, 24, 32), Location = new Point(x, y)
    };

    private static void ConfigureDatePicker(DateTimePicker picker, int x, int y, int width)
    {
        picker.Format = DateTimePickerFormat.Short;
        picker.Font = new Font("Segoe UI", 10, FontStyle.Regular);
        picker.Bounds = new Rectangle(x, y, width, 30);
    }

    private static void ConfigureTimePicker(DateTimePicker picker, int x, int y, int width = 120)
    {
        picker.Format = DateTimePickerFormat.Custom;
        picker.CustomFormat = "h:mm tt";
        picker.ShowUpDown = true;
        picker.Font = new Font("Segoe UI", 10, FontStyle.Regular);
        picker.Bounds = new Rectangle(x, y, width, 30);
    }

    private void LoadWorkingValues()
    {
        _name.Text = _working.Name;
        _enabled.Checked = _working.Enabled;
        _specificDates.Checked = _working.ScheduleType == AdvertisementScheduleType.SpecificDates;
        _weekly.Checked = _working.ScheduleType == AdvertisementScheduleType.Weekly;
        _startDate.Value = _working.StartDateTime.Date;
        _startTime.Value = DateTime.Today.Add(_working.StartDateTime.TimeOfDay);
        _endDate.Value = _working.EndDateTime.Date;
        _endTime.Value = DateTime.Today.Add(_working.EndDateTime.TimeOfDay);
        _weeklyStart.Value = DateTime.Today.Add(_working.DailyStartTime);
        _weeklyEnd.Value = DateTime.Today.Add(_working.DailyEndTime);
        foreach (var pair in _dayChecks) pair.Value.Checked = _working.DaysOfWeek.Contains(pair.Key);
        _fileLabel.Text = string.IsNullOrWhiteSpace(_working.ImageFileName)
            ? "No JPG selected." : "Saved JPG loaded.";
        LoadPreview(AdvertisementFiles.GetSafePath(_working.ImageFileName));
        UpdateScheduleControls();
    }

    private void SelectJpeg()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Choose a JPG Advertisement",
            Filter = "JPEG images (*.jpg;*.jpeg)|*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var info = new FileInfo(picker.FileName);
            if (info.Length > 25_000_000) throw new InvalidOperationException("The JPG must be smaller than 25 MB.");
            using (var image = Image.FromFile(picker.FileName))
            {
                if (image.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                    throw new InvalidOperationException("The selected file is not a valid JPG image.");
            }
            _selectedSourcePath = picker.FileName;
            _fileLabel.Text = Path.GetFileName(picker.FileName);
            if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text == "Advertisement")
                _name.Text = Path.GetFileNameWithoutExtension(picker.FileName);
            LoadPreview(picker.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "JPG Advertisement",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LoadPreview(string? path)
    {
        _preview.Image?.Dispose();
        _preview.Image = null;
        if (path is null || !File.Exists(path)) return;
        try
        {
            using var image = Image.FromFile(path);
            _preview.Image = new Bitmap(image);
        }
        catch
        {
            _fileLabel.Text = "The JPG could not be previewed.";
        }
    }

    private void UpdateScheduleControls()
    {
        var specificPanel = Controls.Find("specificPanel", true).FirstOrDefault();
        var weeklyPanel = Controls.Find("weeklyPanel", true).FirstOrDefault();
        if (specificPanel is not null) specificPanel.Enabled = _specificDates.Checked;
        if (weeklyPanel is not null) weeklyPanel.Enabled = _weekly.Checked;
    }

    private void SaveAndClose()
    {
        var name = _name.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a name for the advertisement.", "Advertisement",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _name.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_working.ImageFileName) && string.IsNullOrWhiteSpace(_selectedSourcePath))
        {
            MessageBox.Show(this, "Upload a JPG advertisement before saving.", "Advertisement",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var start = _startDate.Value.Date + _startTime.Value.TimeOfDay;
        var end = _endDate.Value.Date + _endTime.Value.TimeOfDay;
        var selectedDays = _dayChecks.Where(pair => pair.Value.Checked).Select(pair => pair.Key).ToArray();
        if (_specificDates.Checked && end <= start)
        {
            MessageBox.Show(this, "The ending date and time must be after the starting date and time.",
                "Advertisement Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_weekly.Checked && selectedDays.Length == 0)
        {
            MessageBox.Show(this, "Select at least one day for the weekly schedule.",
                "Advertisement Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_selectedSourcePath))
                _working.ImageFileName = AdvertisementFiles.ImportJpeg(_selectedSourcePath);
            _working.Name = name;
            _working.Enabled = _enabled.Checked;
            _working.ScheduleType = _weekly.Checked
                ? AdvertisementScheduleType.Weekly : AdvertisementScheduleType.SpecificDates;
            _working.StartDateTime = start;
            _working.EndDateTime = end;
            _working.DaysOfWeek = selectedDays;
            _working.DailyStartTime = _weeklyStart.Value.TimeOfDay;
            _working.DailyEndTime = _weeklyEnd.Value.TimeOfDay;
            _working.Normalize();
            Advertisement = _working;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The JPG could not be saved.\n\n" + ex.Message,
                "Advertisement", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class PinEntryDialog : Form
{
    private readonly TextBox _pin = new() { UseSystemPasswordChar = true, MaxLength = 8, Width = 220 };
    public string Pin => _pin.Text;

    public PinEntryDialog(bool dark = false)
    {
        Text = "Staff Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(420, 190);
        Font = new Font("Segoe UI", 10);

        var heading = new Label
        {
            AutoSize = false,
            Text = "Enter the staff password to open settings.",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Bounds = new Rectangle(25, 22, 370, 32)
        };
        var pinLabel = new Label { Text = "Staff Password:", AutoSize = true, Location = new Point(18, 80) };
        _pin.Location = new Point(155, 75);
        var cleaning = false;
        _pin.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && (e.KeyChar < '0' || e.KeyChar > '9'))
                e.Handled = true;
        };
        _pin.TextChanged += (_, _) =>
        {
            if (cleaning) return;
            var numbersOnly = new string(_pin.Text
                .Where(character => character >= '0' && character <= '9')
                .Take(8)
                .ToArray());
            if (numbersOnly == _pin.Text) return;
            cleaning = true;
            _pin.Text = numbersOnly;
            _pin.SelectionStart = _pin.Text.Length;
            cleaning = false;
        };

        var exit = new Button { Text = "Open Staff Settings", Bounds = new Rectangle(145, 130, 165, 36), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(316, 130, 80, 36), DialogResult = DialogResult.Cancel };

        AcceptButton = exit;
        CancelButton = cancel;
        Controls.AddRange([heading, pinLabel, _pin, exit, cancel]);
        ActiveControl = _pin;
        Shown += (_, _) => BeginInvoke(new Action(FocusPasswordField));
        Activated += (_, _) => FocusPasswordField();
        KioskTheme.Apply(this, dark);
    }

    private void FocusPasswordField()
    {
        if (IsDisposed || Disposing)
            return;

        TopMost = true;
        Activate();
        BringToFront();
        _pin.Select();
        _pin.SelectionStart = _pin.Text.Length;
        _pin.SelectionLength = 0;
    }
}

internal static class KioskLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(KioskSettings.DataDirectory);
                var path = Path.Combine(KioskSettings.DataDirectory, "kiosk.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never prevent the kiosk from running.
        }
    }
}
