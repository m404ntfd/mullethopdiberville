using System.Drawing.Drawing2D;

namespace MulletHopPosController;

internal sealed class PosControllerForm : Form
{
    private const int ExpandedSidebarWidth = 360;
    private const int CollapsedSidebarWidth = 108;

    private readonly PosSettings _settings;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2000 };
    private readonly Label _connectionStatus = new();
    private readonly Label _browserStatus = new();
    private readonly Button _checkUpdateButton = new();
    private readonly Button _toggleSidebarButton = new();
    private readonly Button _restoreKeyboardButton = new();
    private readonly Button _reloadBrowserButton = new();
    private readonly Button _staffMenuButton = new();
    private readonly Button _minimizeButton = new();
    private readonly Button _exitButton = new();
    private readonly Panel _sidebar = new();
    private readonly Panel _browserPanel = new();
    private readonly Panel _browserHostPanel = new();
    private readonly Panel _browserErrorPanel = new();
    private readonly Label _browserErrorLabel = new();
    private readonly Button _browserRestartButton = new();
    private readonly Label _brandTitle = new();
    private readonly PictureBox _brandLogo = new();
    private readonly TableLayoutPanel _brandLayout = new();
    private readonly KioskControlCard[] _cards = new KioskControlCard[4];
    private readonly System.Windows.Forms.Timer _sidebarFocusReturnTimer = new() { Interval = 750 };
    private FirefoxHost? _firefoxHost;
    private bool _sidebarExpanded = true;
    private bool _browserModeActive = true;
    private bool _refreshInProgress;
    private bool _updateCheckInProgress;
    private bool _remoteUpdateRequested;
    private bool _staffMenuOpen;
    private bool _wristbandPrinterPromptOpen;
    private bool _exitApproved;
    private bool _cleanupComplete;
    private Rectangle _fullScreenBounds;

    public PosControllerForm(PosSettings settings)
    {
        _settings = settings;
        Text = "Mullet Hop POS";
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon is not null)
            Icon = appIcon;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(900, 600);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(18, 23, 31);
        KeyPreview = true;

        Controls.Add(BuildBrowserArea());
        Controls.Add(BuildSidebar());
        SetSidebarExpanded(expanded: true);

        _firefoxHost = new FirefoxHost(_browserHostPanel);
        _firefoxHost.StatusChanged += (_, status) => SetBrowserStatus(status);
        _firefoxHost.CrashDetected += (_, message) => ShowFirefoxCrash(message);
        _firefoxHost.BrowserInteractionStarted += (_, _) => BeginBrowserInteraction();
        _firefoxHost.BrowserInteractionCompleted += (_, _) => CompleteBrowserInteraction();
        _firefoxHost.WristbandPrintRequested += HandleWristbandPrintRequested;
        TrackSidebarInteraction(_sidebar);
        _sidebarFocusReturnTimer.Tick += (_, _) => ReturnFocusAfterSidebarInteraction();
        _refreshTimer.Tick += async (_, _) => await RefreshStatusesAsync();
        Shown += async (_, _) =>
        {
            WindowState = FormWindowState.Normal;
            _fullScreenBounds = Screen.FromControl(this).Bounds;
            Bounds = _fullScreenBounds;
            UpdateUnlinkedCards();
            _firefoxHost.Start();
            if (!_settings.HasConnectionSettings)
                BeginInvoke(new Action(OpenSettings));
            await RefreshStatusesAsync();
            _refreshTimer.Start();
        };
        Activated += (_, _) =>
        {
            if (_browserModeActive)
                _firefoxHost?.FocusBrowser("POS window activation");
        };
        FormClosing += HandleFormClosing;
    }

    internal static void RunFocusRegressionSmokeTest()
    {
        using var form = new PosControllerForm(new PosSettings());
        form.CreateControl();
        if (FirefoxHost.WindowThreadIdForSmokeTest(form.Handle) == 0)
            throw new InvalidOperationException("Windows did not return the POS window thread ID.");

        if (Descendants(form._sidebar).OfType<Button>().Any(button => button.TabStop))
            throw new InvalidOperationException("A sidebar button can still capture keyboard tab focus.");

        form.SetSidebarExpanded(expanded: true);
        form.BeginBrowserInteraction();
        form.CompleteBrowserInteraction();
        if (form._sidebarExpanded || !form._browserModeActive)
            throw new InvalidOperationException("Browser input did not activate browser mode and collapse the sidebar.");

        var healthyPage = new LilyPadPageHealth(
            FirefoxHost.HomePage,
            "LilyPad POS System",
            "complete",
            1200,
            800,
            300,
            HasUsername: true,
            HasPassword: true);
        if (FirefoxHost.PageHealthIndicatesFailureForSmokeTest(
                healthyPage,
                new Size(1200, 800)))
        {
            throw new InvalidOperationException("A healthy LilyPad page failed the display-health test.");
        }
        if (!FirefoxHost.PageHealthIndicatesFailureForSmokeTest(
                healthyPage with { ViewportWidth = 120, ViewportHeight = 60 },
                new Size(1200, 800)) ||
            !FirefoxHost.PageHealthIndicatesFailureForSmokeTest(
                healthyPage with { HasPassword = false },
                new Size(1200, 800)) ||
            !FirefoxHost.PageHealthIndicatesFailureForSmokeTest(
                healthyPage with
                {
                    Url = "https://mullet.lilypadpos.app/public/WaiverAddToSale.php",
                    BodyTextLength = 0
                },
                new Size(1200, 800)))
        {
            throw new InvalidOperationException(
                "The Firefox display-health test missed a collapsed, incomplete, or blank LilyPad page.");
        }

        var wristbandPrintUrl =
            "https://mullet.lilypadpos.app/public/PrintRegularWristbandsPDF.php";
        if (!FirefoxHost.IsWristbandPrintUrlForSmokeTest(wristbandPrintUrl) ||
            !FirefoxHost.IsWristbandPrintUrlForSmokeTest(
                "https://mullet.lilypadpos.app/public/PrintBirthdayWristband.pdf?job=1") ||
            FirefoxHost.IsWristbandPrintUrlForSmokeTest(
                "https://mullet.lilypadpos.app/public/PrintReceiptPDF.php") ||
            FirefoxHost.IsWristbandPrintUrlForSmokeTest(
                "https://example.com/public/PrintRegularWristbandsPDF.php") ||
            FirefoxHost.PageHealthIndicatesFailureForSmokeTest(
                healthyPage with { Url = wristbandPrintUrl, BodyTextLength = 0 },
                new Size(1200, 800)))
        {
            throw new InvalidOperationException(
                "Wristband print-page detection did not identify only LilyPad wristband jobs.");
        }

        var printerNames = WristbandPrinterDialog.PrinterNamesForSmokeTest;
        if (printerNames.Count != 7 ||
            !printerNames.SequenceEqual(Enumerable.Range(1, 7).Select(number => $"WB-{number}")) ||
            !FirefoxPrintDestinationSelector.IsSupportedWristbandPrinterForSmokeTest("WB-7") ||
            FirefoxPrintDestinationSelector.IsSupportedWristbandPrinterForSmokeTest("WB-8") ||
            !FirefoxPrintDestinationSelector.TextIdentifiesPrinterForSmokeTest(
                "Destination: WB-1 (ready)",
                "WB-1") ||
            FirefoxPrintDestinationSelector.TextIdentifiesPrinterForSmokeTest("WB-10", "WB-1"))
        {
            throw new InvalidOperationException(
                "The wristband printer selector did not preserve the WB-1 through WB-7 range.");
        }

        if (new PosSettings().StartAutomatically)
            throw new InvalidOperationException("POS automatic startup is not off by default.");

        if (!FirefoxHost.ProfileLockDialogTitleIndicatesFailureForSmokeTest("Close Firefox") ||
            !FirefoxHost.ProfileLockDialogTitleIndicatesFailureForSmokeTest(
                "Firefox is already running") ||
            FirefoxHost.ProfileLockDialogTitleIndicatesFailureForSmokeTest("LilyPad POS System"))
        {
            throw new InvalidOperationException(
                "Firefox profile-lock dialog detection did not distinguish a normal browser window.");
        }
        FirefoxProfileRecovery.RunSmokeTest();

        using var card = new KioskControlCard(1);
        card.SetExpanded(expanded: false);
        var status = new PosKioskStatus
        {
            StationId = "smoke-kiosk",
            StationName = "Smoke Kiosk",
            IsOnline = true,
            AvailableForGuests = true,
            StatusMessage = "Ready"
        };
        card.ShowStatus(status);
        var unchangedCount = card.VisualUpdateCount;
        card.ShowStatus(status);
        if (card.VisualUpdateCount != unchangedCount)
            throw new InvalidOperationException("An unchanged kiosk status rewrote the collapsed sidebar.");

        status.AssistanceRequested = true;
        card.ShowStatus(status);
        if (card.VisualUpdateCount != unchangedCount + 1 || !card.AssistanceButtonEnabled)
            throw new InvalidOperationException("An assistance status change did not enable acknowledgment.");

        Application.DoEvents();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private Control BuildBrowserArea()
    {
        _browserPanel.Dock = DockStyle.Fill;
        _browserPanel.BackColor = Color.FromArgb(239, 244, 248);
        _browserPanel.Padding = Padding.Empty;

        _browserHostPanel.Dock = DockStyle.Fill;
        _browserHostPanel.BackColor = Color.FromArgb(239, 244, 248);

        _browserStatus.Dock = DockStyle.Fill;
        _browserStatus.Text = "Starting Firefox and loading LilyPad POS…";
        _browserStatus.TextAlign = ContentAlignment.MiddleCenter;
        _browserStatus.Font = new Font("Segoe UI", 16, FontStyle.Bold);
        _browserStatus.ForeColor = Color.FromArgb(83, 97, 109);
        _browserStatus.BackColor = Color.FromArgb(239, 244, 248);
        _browserStatus.Padding = new Padding(40);
        _browserHostPanel.Controls.Add(_browserStatus);

        _browserErrorPanel.Dock = DockStyle.Top;
        _browserErrorPanel.Height = 82;
        _browserErrorPanel.Visible = false;
        _browserErrorPanel.BackColor = Color.FromArgb(187, 34, 46);
        _browserErrorPanel.Padding = new Padding(18, 12, 18, 12);
        _browserErrorLabel.Dock = DockStyle.Fill;
        _browserErrorLabel.ForeColor = Color.White;
        _browserErrorLabel.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        _browserErrorLabel.TextAlign = ContentAlignment.MiddleLeft;
        _browserErrorLabel.Padding = new Padding(8, 0, 12, 0);
        _browserRestartButton.Text = "REFRESH LILYPAD";
        _browserRestartButton.Dock = DockStyle.Right;
        _browserRestartButton.Width = 190;
        _browserRestartButton.BackColor = Color.White;
        _browserRestartButton.ForeColor = Color.FromArgb(133, 19, 31);
        _browserRestartButton.FlatStyle = FlatStyle.Flat;
        _browserRestartButton.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _browserRestartButton.Click += (_, _) => RefreshLilypad();
        _browserErrorPanel.Controls.Add(_browserErrorLabel);
        _browserErrorPanel.Controls.Add(_browserRestartButton);

        _browserPanel.Controls.Add(_browserHostPanel);
        _browserPanel.Controls.Add(_browserErrorPanel);
        return _browserPanel;
    }

    private Control BuildSidebar()
    {
        _sidebar.Dock = DockStyle.Right;
        _sidebar.Width = ExpandedSidebarWidth;
        _sidebar.BackColor = Color.FromArgb(42, 26, 54);
        _sidebar.Padding = new Padding(8);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 224));

        ConfigureSidebarButton(_toggleSidebarButton, "◀  COLLAPSE CONTROLS",
            Color.FromArgb(255, 217, 188), Color.FromArgb(30, 20, 36));
        _toggleSidebarButton.Click += (_, _) =>
        {
            SetSidebarExpanded(!_sidebarExpanded);
            if (!_sidebarExpanded)
                QueueBrowserFocusReturn();
        };
        root.Controls.Add(_toggleSidebarButton, 0, 0);

        _brandLayout.Dock = DockStyle.Fill;
        _brandLayout.ColumnCount = 2;
        _brandLayout.RowCount = 1;
        _brandLayout.BackColor = Color.Transparent;
        _brandLayout.Margin = new Padding(0, 4, 0, 4);
        _brandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        _brandLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _brandLogo.Image = LoadLogo();
        _brandLogo.Dock = DockStyle.Fill;
        _brandLogo.SizeMode = PictureBoxSizeMode.Zoom;
        _brandLogo.BackColor = Color.Transparent;
        _brandLogo.Margin = new Padding(4);
        _brandTitle.Text = "MULLET HOP POS";
        _brandTitle.Dock = DockStyle.Fill;
        _brandTitle.TextAlign = ContentAlignment.MiddleLeft;
        _brandTitle.Font = new Font("Segoe UI", 15, FontStyle.Bold);
        _brandTitle.ForeColor = Color.White;
        _brandLayout.Controls.Add(_brandLogo, 0, 0);
        _brandLayout.Controls.Add(_brandTitle, 1, 0);
        root.Controls.Add(_brandLayout, 0, 1);

        var kioskLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        kioskLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 0; index < 4; index++)
        {
            kioskLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            _cards[index] = new KioskControlCard(index + 1);
            var slot = index;
            _cards[index].CloseRequested += async (_, _) =>
                await PromptForClosureAsync(slot);
            _cards[index].OpenRequested += async (_, _) =>
                await SendCommandAsync(slot, PosCommandTypes.SetClosed, false);
            _cards[index].ResetRequested += async (_, _) =>
                await SendCommandAsync(slot, PosCommandTypes.ResetStart);
            _cards[index].AssistanceAcknowledgedRequested += async (_, _) =>
                await SendCommandAsync(slot, PosCommandTypes.AcknowledgeAssistance);
            kioskLayout.Controls.Add(_cards[index], 0, index);
        }
        root.Controls.Add(kioskLayout, 0, 2);
        root.Controls.Add(BuildApplicationControls(), 0, 3);
        _sidebar.Controls.Add(root);
        return _sidebar;
    }

    private Control BuildApplicationControls()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Margin = Padding.Empty,
            Padding = new Padding(0, 6, 0, 0),
            BackColor = Color.Transparent
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 20));

        _connectionStatus.Dock = DockStyle.Fill;
        _connectionStatus.Text = "Starting…";
        _connectionStatus.ForeColor = Color.FromArgb(221, 226, 232);
        _connectionStatus.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _connectionStatus.TextAlign = ContentAlignment.MiddleCenter;
        _connectionStatus.AutoEllipsis = true;
        _connectionStatus.Margin = new Padding(3);
        panel.Controls.Add(_connectionStatus, 0, 0);
        panel.SetColumnSpan(_connectionStatus, 2);

        ConfigureSidebarButton(_restoreKeyboardButton, "RESTORE KEYBOARD",
            Color.FromArgb(36, 152, 125), Color.White);
        _restoreKeyboardButton.Click += (_, _) => RestoreBrowserKeyboard();
        panel.Controls.Add(_restoreKeyboardButton, 0, 1);
        panel.SetColumnSpan(_restoreKeyboardButton, 2);

        ConfigureSidebarButton(_reloadBrowserButton, "REFRESH LILYPAD",
            Color.FromArgb(8, 119, 189), Color.White);
        _reloadBrowserButton.Click += (_, _) => RefreshLilypad();
        panel.Controls.Add(_reloadBrowserButton, 0, 2);
        panel.SetColumnSpan(_reloadBrowserButton, 2);

        ConfigureSidebarButton(_staffMenuButton, "SETTINGS",
            Color.FromArgb(255, 217, 188), Color.FromArgb(30, 20, 36));
        _staffMenuButton.Click += (_, _) => OpenSettings();
        panel.Controls.Add(_staffMenuButton, 0, 3);
        panel.SetColumnSpan(_staffMenuButton, 2);

        ConfigureSidebarButton(_checkUpdateButton, "CHECK FOR UPDATES",
            Color.FromArgb(117, 68, 154), Color.White);
        _checkUpdateButton.Click += async (_, _) => await CheckForPosUpdateAsync();
        panel.Controls.Add(_checkUpdateButton, 0, 4);
        panel.SetColumnSpan(_checkUpdateButton, 2);

        ConfigureSidebarButton(_minimizeButton, "MINIMIZE",
            Color.FromArgb(66, 75, 86), Color.White);
        _minimizeButton.Click += (_, _) => MinimizeToTaskbar();
        panel.Controls.Add(_minimizeButton, 0, 5);

        ConfigureSidebarButton(_exitButton, "EXIT APP",
            Color.FromArgb(187, 34, 46), Color.White);
        _exitButton.Click += (_, _) => RequestApplicationExit();
        panel.Controls.Add(_exitButton, 1, 5);
        return panel;
    }

    private void SetSidebarExpanded(bool expanded)
    {
        _sidebarExpanded = expanded;
        _sidebar.Width = expanded ? ExpandedSidebarWidth : CollapsedSidebarWidth;
        _toggleSidebarButton.Text = expanded ? "◀  COLLAPSE CONTROLS" : "▶";
        _brandTitle.Visible = expanded;
        _brandLayout.ColumnStyles[0].Width = expanded ? 66 : Math.Max(1, CollapsedSidebarWidth - 16);
        _brandLayout.ColumnStyles[1].Width = expanded ? 100 : 0;
        _connectionStatus.Visible = expanded;
        _restoreKeyboardButton.Text = expanded ? "RESTORE KEYBOARD" : "KEYBOARD";
        _reloadBrowserButton.Text = expanded ? "REFRESH LILYPAD" : "REFRESH";
        _staffMenuButton.Text = "SETTINGS";
        _checkUpdateButton.Text = expanded
            ? (PosUpdater.HasStagedUpdate ? "INSTALL UPDATE" : "CHECK FOR UPDATES")
            : (PosUpdater.HasStagedUpdate ? "INSTALL" : "UPDATE");
        _minimizeButton.Text = expanded ? "MINIMIZE" : "—";
        _exitButton.Text = expanded ? "EXIT APP" : "X";
        foreach (var card in _cards)
            card?.SetExpanded(expanded);
        _firefoxHost?.ResizeToHost();
    }

    private void BeginBrowserInteraction()
    {
        if (_cleanupComplete || IsDisposed)
            return;

        _sidebarFocusReturnTimer.Stop();
        _browserModeActive = true;
        ActiveControl = null;
        _firefoxHost?.SetBrowserFocusPreferred(true);
    }

    private void CompleteBrowserInteraction()
    {
        if (_cleanupComplete || IsDisposed)
            return;

        if (_sidebarExpanded)
            SetSidebarExpanded(expanded: false);
        BeginInvoke(new Action(() => _firefoxHost?.FocusBrowser("browser interaction completed")));
    }

    private void BeginSidebarInteraction()
    {
        if (_cleanupComplete || IsDisposed)
            return;

        _sidebarFocusReturnTimer.Stop();
        _browserModeActive = false;
        _firefoxHost?.SetBrowserFocusPreferred(false);
    }

    private void CompleteSidebarInteraction()
    {
        if (!_sidebarExpanded)
            QueueBrowserFocusReturn();
    }

    private void QueueBrowserFocusReturn()
    {
        _sidebarFocusReturnTimer.Stop();
        _sidebarFocusReturnTimer.Start();
    }

    private void ReturnFocusAfterSidebarInteraction()
    {
        _sidebarFocusReturnTimer.Stop();
        if (_sidebarExpanded || _staffMenuOpen || _cleanupComplete || IsDisposed)
            return;
        RestoreBrowserKeyboard(showStatus: false);
    }

    private void RestoreBrowserKeyboard(bool showStatus = true)
    {
        if (_cleanupComplete || IsDisposed)
            return;

        _sidebarFocusReturnTimer.Stop();
        _browserModeActive = true;
        if (_sidebarExpanded)
            SetSidebarExpanded(expanded: false);
        ActiveControl = null;
        _firefoxHost?.SetBrowserFocusPreferred(true);
        BeginInvoke(new Action(() =>
        {
            var restored = _firefoxHost?.FocusBrowser("Restore Keyboard command") == true;
            if (showStatus)
            {
                SetConnectionStatus(
                    restored
                        ? "Firefox keyboard focus restored."
                        : "Click inside LilyPad once to restore keyboard focus.",
                    restored);
            }
        }));
    }

    private void TrackSidebarInteraction(Control control)
    {
        control.MouseDown += (_, _) => BeginSidebarInteraction();
        control.MouseUp += (_, _) => CompleteSidebarInteraction();
        control.ControlAdded += (_, args) =>
        {
            if (args.Control is not null)
                TrackSidebarInteraction(args.Control);
        };
        foreach (Control child in control.Controls)
            TrackSidebarInteraction(child);
    }

    private async Task CheckForPosUpdateAsync()
    {
        if (_updateCheckInProgress)
            return;

        _updateCheckInProgress = true;
        _checkUpdateButton.Enabled = false;
        try
        {
            if (PosUpdater.HasStagedUpdate)
            {
                PromptToInstallPosUpdate(
                    "A downloaded Mullet Hop POS update is ready to install.");
                return;
            }

            _checkUpdateButton.Text = _sidebarExpanded ? "CHECKING…" : "WAIT…";
            var result = await PosUpdater.CheckAndStageUpdateAsync();
            if (IsDisposed)
                return;

            if (result.Status == PosUpdateStatus.ReadyToInstall)
            {
                PromptToInstallPosUpdate(result.Message);
                return;
            }

            MessageBox.Show(this, result.Message, "Mullet Hop POS Update",
                MessageBoxButtons.OK,
                result.Status == PosUpdateStatus.Failed
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);
        }
        finally
        {
            _updateCheckInProgress = false;
            if (!IsDisposed)
            {
                _checkUpdateButton.Text = _sidebarExpanded
                    ? (PosUpdater.HasStagedUpdate ? "INSTALL UPDATE" : "CHECK FOR UPDATES")
                    : (PosUpdater.HasStagedUpdate ? "INSTALL" : "UPDATE");
                _checkUpdateButton.Enabled = true;
            }
        }
    }

    private void PromptToInstallPosUpdate(string message)
    {
        var answer = MessageBox.Show(this,
            message + "\n\nInstall it now? Mullet Hop POS will close and restart automatically.",
            "Install Mullet Hop POS Update",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            return;

        _checkUpdateButton.Text = "INSTALLING…";
        _exitApproved = true;
        var result = PosUpdater.ApplyStagedUpdateAndRestart();
        if (result.Status == PosUpdateStatus.Applying)
            _firefoxHost?.Dispose();
        else
            _exitApproved = false;
        if (!IsDisposed && result.Status != PosUpdateStatus.Applying)
        {
            MessageBox.Show(this, result.Message, "Mullet Hop POS Update",
                MessageBoxButtons.OK,
                result.Status == PosUpdateStatus.Failed
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);
        }
    }

    private async Task ProcessRemoteUpdateRequestAsync()
    {
        if (!_remoteUpdateRequested || _updateCheckInProgress || IsDisposed)
            return;

        _remoteUpdateRequested = false;
        _updateCheckInProgress = true;
        _checkUpdateButton.Enabled = false;
        _checkUpdateButton.Text = "REMOTE UPDATE…";
        SetConnectionStatus("Systems Controller requested a POS software update…", true);
        try
        {
            var result = await PosUpdater.CheckAndStageUpdateAsync();
            PosLog.Write("Remote Systems Controller update request: " + result.Message);
            if (IsDisposed)
                return;

            if (result.Status == PosUpdateStatus.ReadyToInstall)
            {
                _checkUpdateButton.Text = "INSTALLING…";
                _exitApproved = true;
                var applyResult = PosUpdater.ApplyStagedUpdateAndRestart();
                PosLog.Write("Remote POS update install: " + applyResult.Message);
                if (applyResult.Status == PosUpdateStatus.Applying)
                {
                    _firefoxHost?.Dispose();
                    return;
                }
                _exitApproved = false;
                SetConnectionStatus(applyResult.Message, false);
                return;
            }

            SetConnectionStatus(result.Message, result.Status == PosUpdateStatus.UpToDate);
        }
        finally
        {
            _updateCheckInProgress = false;
            if (!IsDisposed)
            {
                _checkUpdateButton.Text = _sidebarExpanded
                    ? (PosUpdater.HasStagedUpdate ? "INSTALL UPDATE" : "CHECK FOR UPDATES")
                    : (PosUpdater.HasStagedUpdate ? "INSTALL" : "UPDATE");
                _checkUpdateButton.Enabled = true;
            }
        }
    }

    private async Task RefreshStatusesAsync()
    {
        if (_refreshInProgress)
            return;
        if (!_settings.HasConnectionSettings)
        {
            UpdateUnlinkedCards();
            SetConnectionStatus("Open Settings to connect Mullet Hop POS.", false);
            return;
        }

        _refreshInProgress = true;
        try
        {
            var client = new PosControllerClient(_settings.ControllerUrl, _settings.PairingKey);
            var response = await client.GetStatusAsync();
            if (response.InstallUpdate)
                _remoteUpdateRequested = true;
            var previousSlots = _settings.KioskSlots.ToArray();
            var added = _settings.RememberSuccessfulConnection(
                _settings.ControllerUrl,
                _settings.PairingKey,
                response.Kiosks);
            if (added > 0 || !previousSlots.SequenceEqual(_settings.KioskSlots, StringComparer.Ordinal))
            {
                UpdateUnlinkedCards();
                PosLog.Write(
                    $"Automatically added {added} kiosk device{(added == 1 ? "" : "s")} from the Systems Controller.");
            }
            var byId = response.Kiosks.ToDictionary(kiosk => kiosk.StationId, StringComparer.Ordinal);
            for (var slot = 0; slot < _cards.Length; slot++)
            {
                var stationId = _settings.KioskSlots[slot];
                if (string.IsNullOrWhiteSpace(stationId))
                    _cards[slot].ShowUnlinked();
                else if (byId.TryGetValue(stationId, out var kiosk))
                    _cards[slot].ShowStatus(kiosk);
                else
                    _cards[slot].ShowMissing();
            }
            SetConnectionStatus(
                added > 0
                    ? $"Connected • {added} kiosk{(added == 1 ? "" : "s")} added"
                    : "Controller connected",
                true);
            await ProcessRemoteUpdateRequestAsync();
        }
        catch (Exception ex)
        {
            foreach (var card in _cards)
            {
                if (card.IsLinked)
                    card.ShowControllerUnavailable();
            }
            SetConnectionStatus("Controller unavailable", false);
            PosLog.Write("Status refresh failed: " + ex.Message);
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private async Task PromptForClosureAsync(int slot)
    {
        using var dialog = new KioskClosureDialog(slot + 1);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (dialog.SelectedClosureType == KioskClosureType.Business)
            await SendCommandAsync(slot, PosCommandTypes.SetBusinessClosed, true);
        else
            await SendCommandAsync(slot, PosCommandTypes.SetClosed, true);
    }

    private async void HandleWristbandPrintRequested(
        object? sender,
        WristbandPrintRequestedEventArgs e)
    {
        if (_wristbandPrinterPromptOpen || _cleanupComplete || IsDisposed ||
            _firefoxHost is null)
        {
            return;
        }

        _wristbandPrinterPromptOpen = true;
        _browserModeActive = false;
        _firefoxHost.SetBrowserFocusPreferred(false);
        try
        {
            using var dialog = new WristbandPrinterDialog();
            if (dialog.ShowDialog(this) != DialogResult.OK ||
                string.IsNullOrWhiteSpace(dialog.SelectedPrinterName))
            {
                if (!_firefoxHost.CancelPrintPreview())
                {
                    PosLog.Write(
                        "The wristband printer prompt was cancelled, but Firefox did not accept Escape.");
                }
                else
                {
                    PosLog.Write("The wristband print preview was cancelled by the user.");
                }
                return;
            }

            var printerName = dialog.SelectedPrinterName;
            PosLog.Write($"The user selected {printerName} for the current wristband print job.");
            var result = await _firefoxHost.SelectPrintDestinationAsync(printerName);
            if (!result.Success)
            {
                PosLog.Write(result.Message);
                MessageBox.Show(
                    this,
                    result.Message + "\n\nThe POS-X Thermal Printer remains the default receipt printer.",
                    "Select Wristband Printer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            PosLog.Write("Wristband printer prompt error: " + ex);
            MessageBox.Show(
                this,
                "The wristband printer could not be selected automatically. " +
                "Choose WB-1 through WB-7 manually in Firefox's Destination list.\n\n" +
                ex.Message,
                "Select Wristband Printer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _wristbandPrinterPromptOpen = false;
            _browserModeActive = true;
            _firefoxHost?.SetBrowserFocusPreferred(true);
            BeginInvoke(new Action(() =>
                _firefoxHost?.FocusBrowser("wristband printer prompt completed")));
        }
    }

    private async Task SendCommandAsync(int slot, string commandType, bool? closed = null)
    {
        if (slot < 0 || slot >= _settings.KioskSlots.Count)
            return;
        var stationId = _settings.KioskSlots[slot];
        if (string.IsNullOrWhiteSpace(stationId))
        {
            MessageBox.Show(this,
                $"Kiosk {slot + 1} is not linked. Open Settings to assign a waiver kiosk.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cards[slot].SetBusy(true);
        try
        {
            var client = new PosControllerClient(_settings.ControllerUrl, _settings.PairingKey);
            var result = await client.SendCommandAsync(stationId, commandType, closed);
            if (!result.Accepted)
                throw new InvalidOperationException(result.Message);

            if (commandType == PosCommandTypes.SetClosed && closed == true)
                _cards[slot].ShowPendingState(open: false, "Applying staff closure…");
            else if (commandType == PosCommandTypes.SetBusinessClosed && closed == true)
                _cards[slot].ShowPendingBusinessClosure("Applying business closure…");
            else if (commandType == PosCommandTypes.SetClosed)
                _cards[slot].ShowPendingState(open: true, "Putting waiver station in service…");
            else if (commandType == PosCommandTypes.AcknowledgeAssistance)
                _cards[slot].ShowAssistanceAcknowledgedPending();
            else
                _cards[slot].ShowPendingMessage("Resetting to the starting page…");

            SetConnectionStatus($"Command sent to Kiosk {slot + 1}.", true);
        }
        catch (Exception ex)
        {
            _cards[slot].ShowCommandError(ex.Message);
            SetConnectionStatus($"Kiosk {slot + 1} command failed", false);
            PosLog.Write($"Kiosk {slot + 1} command failed: {ex.Message}");
        }
        finally
        {
            _cards[slot].SetBusy(false);
        }
    }

    private void OpenSettings()
    {
        if (_staffMenuOpen)
            return;

        _staffMenuOpen = true;
        try
        {
            using var pin = new PinEntryDialog();
            if (pin.ShowDialog(this) != DialogResult.OK)
                return;
            if (!_settings.VerifyPin(pin.Pin))
            {
                MessageBox.Show(this,
                    "The Staff Menu passcode was not correct.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                PosLog.Write("Incorrect Mullet Hop POS staff-menu passcode entered.");
                return;
            }

            using var dialog = new PosSettingsDialog(_settings);
            var result = dialog.ShowDialog(this);
            if (result != DialogResult.OK && dialog.AppliedSettings is null)
                return;
            _settings.CopyFrom(result == DialogResult.OK
                ? dialog.Settings
                : dialog.AppliedSettings!);
            UpdateUnlinkedCards();
            _ = RefreshStatusesAsync();
        }
        finally
        {
            _staffMenuOpen = false;
        }
    }

    private void RequestApplicationExit()
    {
        if (_exitApproved || _staffMenuOpen)
            return;

        _staffMenuOpen = true;
        try
        {
            RestoreFromTaskbar();
            using var pin = new PinEntryDialog(
                "Exit Mullet Hop POS",
                "Enter the Staff Menu passcode to close Firefox and Mullet Hop POS.",
                "Exit Application");
            if (pin.ShowDialog(this) != DialogResult.OK)
                return;
            if (!_settings.VerifyPin(pin.Pin))
            {
                MessageBox.Show(this, "The Staff Menu passcode was not correct.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var answer = MessageBox.Show(this,
                "Close LilyPad POS, Firefox, and Mullet Hop POS?",
                "Exit Mullet Hop POS",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
                return;
            _exitApproved = true;
            Close();
        }
        finally
        {
            _staffMenuOpen = false;
        }
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_exitApproved && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            BeginInvoke(new Action(RequestApplicationExit));
            return;
        }
        Cleanup();
    }

    private void MinimizeToTaskbar()
    {
        if (_cleanupComplete || IsDisposed)
            return;

        if (WindowState == FormWindowState.Normal && !Bounds.IsEmpty)
            _fullScreenBounds = Bounds;
        ShowInTaskbar = true;
        WindowState = FormWindowState.Minimized;
        PosLog.Write("Mullet Hop POS minimized to the Windows taskbar.");
    }

    private void RestoreFromTaskbar()
    {
        if (_cleanupComplete || IsDisposed)
            return;

        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Show();
        if (!_fullScreenBounds.IsEmpty)
            Bounds = _fullScreenBounds;
        else
            Bounds = Screen.FromPoint(Cursor.Position).Bounds;
        BringToFront();
        Activate();
        _firefoxHost?.ResizeToHost();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Alt | Keys.M))
        {
            BeginInvoke(new Action(OpenSettings));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Cleanup()
    {
        if (_cleanupComplete)
            return;
        _cleanupComplete = true;
        _refreshTimer.Stop();
        _sidebarFocusReturnTimer.Stop();
        _sidebarFocusReturnTimer.Dispose();
        _firefoxHost?.Dispose();
    }

    private void UpdateUnlinkedCards()
    {
        for (var slot = 0; slot < _cards.Length; slot++)
        {
            _cards[slot].IsLinked = _settings.KioskSlots.Count > slot &&
                                    !string.IsNullOrWhiteSpace(_settings.KioskSlots[slot]);
            if (!_cards[slot].IsLinked)
                _cards[slot].ShowUnlinked();
        }
    }

    private void SetConnectionStatus(string text, bool connected)
    {
        var color = connected
            ? Color.FromArgb(177, 231, 151)
            : Color.FromArgb(255, 170, 176);
        if (string.Equals(_connectionStatus.Text, text, StringComparison.Ordinal) &&
            _connectionStatus.ForeColor == color)
        {
            return;
        }
        _connectionStatus.Text = text;
        _connectionStatus.ForeColor = color;
    }

    private void RefreshLilypad()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(RefreshLilypad));
            return;
        }

        _browserErrorPanel.Visible = false;
        SetBrowserStatus("Closing the Firefox session and reopening LilyPad POS…");
        _firefoxHost?.Restart();
    }

    private void ShowFirefoxCrash(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ShowFirefoxCrash(message)));
            return;
        }

        _browserErrorLabel.Text = message;
        _browserErrorPanel.Visible = true;
        _browserErrorPanel.BringToFront();
        _browserStatus.Visible = false;
    }

    private void SetBrowserStatus(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetBrowserStatus(text)));
            return;
        }
        _browserStatus.Text = text;
        var running = text.Contains("is running", StringComparison.OrdinalIgnoreCase);
        if (running)
            _browserErrorPanel.Visible = false;
        _browserStatus.Visible = !running && !_browserErrorPanel.Visible;
        if (_browserStatus.Visible)
            _browserStatus.BringToFront();
    }

    private static void ConfigureSidebarButton(
        Button button, string text, Color background, Color foreground)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(3);
        button.BackColor = background;
        button.ForeColor = foreground;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.TabStop = false;
        button.UseVisualStyleBackColor = false;
    }

    private static Image? LoadLogo()
    {
        try
        {
            using var stream = typeof(PosControllerForm).Assembly
                .GetManifestResourceStream(
                    "MulletHopPosController.Assets.MulletHopStatusViewerFish.png");
            if (stream is null)
                return null;
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }
}

internal enum KioskClosureType
{
    Staff,
    Business
}

internal sealed class KioskClosureDialog : Form
{
    public KioskClosureType SelectedClosureType { get; private set; } = KioskClosureType.Staff;

    public KioskClosureDialog(int kioskNumber)
    {
        Text = $"Close Kiosk {kioskNumber}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(540, 245);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            Text = $"How should Kiosk {kioskNumber} be closed?",
            Dock = DockStyle.Top,
            Height = 58,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            ForeColor = Color.FromArgb(16, 24, 32)
        };
        var explanation = new Label
        {
            Text = "Staff Closure shows red. Business Closure starts the Business Closed video and shows blue.",
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(24, 0, 24, 8),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(70, 82, 94)
        };
        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(18, 14, 18, 18)
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));

        var staff = MakeChoiceButton("STAFF CLOSURE", Color.FromArgb(187, 34, 46), Color.White);
        staff.Click += (_, _) => Complete(KioskClosureType.Staff);
        var business = MakeChoiceButton("BUSINESS CLOSURE", Color.FromArgb(26, 135, 232), Color.White);
        business.Click += (_, _) => Complete(KioskClosureType.Business);
        var cancel = MakeChoiceButton("CANCEL", Color.FromArgb(120, 126, 132), Color.White);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        buttons.Controls.Add(staff, 0, 0);
        buttons.Controls.Add(business, 1, 0);
        buttons.Controls.Add(cancel, 2, 0);

        Controls.Add(buttons);
        Controls.Add(explanation);
        Controls.Add(heading);
    }

    private void Complete(KioskClosureType closureType)
    {
        SelectedClosureType = closureType;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static Button MakeChoiceButton(string text, Color background, Color foreground) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Margin = new Padding(5),
        BackColor = background,
        ForeColor = foreground,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9, FontStyle.Bold),
        Cursor = Cursors.Hand,
        UseVisualStyleBackColor = false
    };
}

internal sealed class KioskControlCard : Panel
{
    private static readonly Color OnlineColor = Color.FromArgb(38, 205, 91);
    private static readonly Color ErrorColor = Color.FromArgb(244, 34, 48);
    private static readonly Color BusinessClosedColor = Color.FromArgb(26, 135, 232);
    private static readonly Color AssistanceColor = Color.FromArgb(255, 213, 38);
    private static readonly Color UnlinkedColor = Color.FromArgb(82, 88, 96);

    private readonly int _kioskNumber;
    private readonly KioskStatusDot _dot = new();
    private readonly Label _title = new();
    private readonly Label _status = new();
    private readonly Button _close = new();
    private readonly Button _open = new();
    private readonly Button _reset = new();
    private readonly Button _assistance = new();
    private readonly TableLayoutPanel _body = new();
    private readonly Panel _details = new();
    private readonly System.Windows.Forms.Timer _assistanceFlashTimer = new() { Interval = 450 };
    private Color _baseStatusColor = UnlinkedColor;
    private bool _assistanceRequested;
    private bool _assistanceAcknowledged;
    private bool _assistanceFlashYellow;
    private bool _buttonsEnabled;
    private string _visualStateKey = string.Empty;
    private int _visualUpdateCount;

    public event EventHandler? CloseRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler? ResetRequested;
    public event EventHandler? AssistanceAcknowledgedRequested;
    public bool IsLinked { get; set; }
    internal int VisualUpdateCount => _visualUpdateCount;
    internal bool AssistanceButtonEnabled => _assistance.Enabled;

    public KioskControlCard(int kioskNumber)
    {
        _kioskNumber = kioskNumber;
        Dock = DockStyle.Fill;
        Margin = new Padding(3);
        Padding = new Padding(5);
        BackColor = Color.White;

        _title.Text = $"KIOSK {kioskNumber}";
        _title.Dock = DockStyle.Top;
        _title.Height = 24;
        _title.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _title.TextAlign = ContentAlignment.MiddleCenter;
        _title.ForeColor = Color.FromArgb(16, 24, 32);

        _body.Dock = DockStyle.Fill;
        _body.ColumnCount = 2;
        _body.RowCount = 1;
        _body.Margin = Padding.Empty;
        _body.Padding = Padding.Empty;
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var indicator = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        indicator.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        indicator.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _dot.Dock = DockStyle.Fill;
        _dot.Margin = new Padding(4, 0, 4, 0);
        ConfigureActionButton(_assistance, "ACK", AssistanceColor, Color.FromArgb(16, 24, 32));
        _assistance.Font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        _assistance.Margin = new Padding(2);
        _assistance.Click += (_, _) => AssistanceAcknowledgedRequested?.Invoke(this, EventArgs.Empty);
        indicator.Controls.Add(_dot, 0, 0);
        indicator.Controls.Add(_assistance, 0, 1);
        _body.Controls.Add(indicator, 0, 0);

        _details.Dock = DockStyle.Fill;
        _details.Padding = new Padding(4, 0, 0, 0);
        _status.Dock = DockStyle.Fill;
        _status.Padding = new Padding(4, 0, 4, 2);
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _status.ForeColor = Color.FromArgb(83, 97, 109);
        _status.AutoEllipsis = true;

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 31,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        for (var column = 0; column < 3; column++)
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        ConfigureActionButton(_close, "CLOSE", Color.FromArgb(245, 130, 32), Color.White);
        ConfigureActionButton(_open, "OPEN", Color.FromArgb(239, 42, 55), Color.White);
        ConfigureActionButton(_reset, "RESET", Color.FromArgb(8, 119, 189), Color.White);
        _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        _open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _reset.Click += (_, _) => ResetRequested?.Invoke(this, EventArgs.Empty);
        actions.Controls.Add(_close, 0, 0);
        actions.Controls.Add(_open, 1, 0);
        actions.Controls.Add(_reset, 2, 0);
        _details.Controls.Add(_status);
        _details.Controls.Add(actions);
        _body.Controls.Add(_details, 1, 0);

        _assistanceFlashTimer.Tick += (_, _) =>
        {
            _assistanceFlashYellow = !_assistanceFlashYellow;
            _dot.StatusColor = _assistanceFlashYellow ? AssistanceColor : _baseStatusColor;
        };

        Controls.Add(_body);
        Controls.Add(_title);
        ShowUnlinked();
    }

    public void SetExpanded(bool expanded)
    {
        _details.Visible = expanded;
        _body.ColumnStyles[0].Width = expanded ? 84 : Math.Max(1, Width - Padding.Horizontal);
        _body.ColumnStyles[1].Width = expanded ? 100 : 0;
        _title.Text = expanded ? $"KIOSK {_kioskNumber}" : $"K{_kioskNumber}";
        UpdateAssistanceButton();
    }

    public void ShowStatus(PosKioskStatus kiosk)
    {
        IsLinked = true;
        var visualStateKey = string.Join('\u001f',
            "status",
            kiosk.StationId,
            kiosk.StationName,
            kiosk.IsOnline,
            kiosk.StationClosed,
            kiosk.BusinessHoursClosed,
            kiosk.AvailableForGuests,
            kiosk.HasError,
            kiosk.AssistanceRequested,
            kiosk.AssistanceAcknowledged,
            kiosk.StatusMessage);
        if (!BeginVisualUpdate(visualStateKey))
            return;

        var open = kiosk.IsOnline && kiosk.AvailableForGuests && !kiosk.HasError;
        var businessClosed = kiosk.IsOnline && kiosk.BusinessHoursClosed &&
                             !kiosk.StationClosed && !kiosk.HasError;
        SetBaseStatusColor(open
            ? OnlineColor
            : businessClosed
                ? BusinessClosedColor
                : ErrorColor);
        SetAssistanceState(kiosk.AssistanceRequested, kiosk.AssistanceAcknowledged);
        var message = kiosk.IsOnline
            ? (string.IsNullOrWhiteSpace(kiosk.StatusMessage)
                ? (open ? "Online and open to guests" : "Waiver station unavailable")
                : kiosk.StatusMessage)
            : "Waiver kiosk is offline";
        _status.Text = $"{kiosk.StationName}\n{message}";
        _status.ForeColor = open
            ? Color.FromArgb(44, 116, 29)
            : businessClosed
                ? Color.FromArgb(10, 91, 160)
                : Color.FromArgb(187, 34, 46);
        _dot.AccessibleDescription = $"Kiosk {_kioskNumber}: {message}";
        SetButtonsEnabled(true);
    }

    public void ShowUnlinked()
    {
        IsLinked = false;
        if (!BeginVisualUpdate("unlinked"))
            return;
        SetBaseStatusColor(UnlinkedColor);
        SetAssistanceState(false, false);
        _status.Text = "Not linked\nOpen Settings to assign a kiosk";
        _status.ForeColor = Color.FromArgb(83, 97, 109);
        _dot.AccessibleDescription = $"Kiosk {_kioskNumber}: not linked";
        SetButtonsEnabled(false);
    }

    public void ShowMissing()
    {
        IsLinked = true;
        if (!BeginVisualUpdate("missing"))
            return;
        SetBaseStatusColor(ErrorColor);
        SetAssistanceState(false, false);
        _status.Text = "Linked kiosk not found by controller";
        _status.ForeColor = Color.FromArgb(187, 34, 46);
        _dot.AccessibleDescription = $"Kiosk {_kioskNumber}: not found";
        SetButtonsEnabled(true);
    }

    public void ShowControllerUnavailable()
    {
        if (!BeginVisualUpdate("controller-unavailable"))
            return;
        SetBaseStatusColor(ErrorColor);
        SetAssistanceState(false, false);
        _status.Text = "Kiosk status unavailable";
        _status.ForeColor = Color.FromArgb(187, 34, 46);
    }

    public void ShowPendingState(bool open, string message)
    {
        if (!BeginVisualUpdate($"pending:{open}:{message}"))
            return;
        SetBaseStatusColor(open ? OnlineColor : ErrorColor);
        ApplyPendingMessage(message);
    }

    public void ShowPendingBusinessClosure(string message)
    {
        if (!BeginVisualUpdate($"pending-business:{message}"))
            return;
        SetBaseStatusColor(BusinessClosedColor);
        ApplyPendingMessage(message);
        _status.ForeColor = Color.FromArgb(10, 91, 160);
    }

    public void ShowPendingMessage(string message)
    {
        if (!BeginVisualUpdate($"pending-message:{message}"))
            return;
        ApplyPendingMessage(message);
    }

    private void ApplyPendingMessage(string message)
    {
        _status.Text = message;
        _status.ForeColor = Color.FromArgb(125, 77, 9);
    }

    public void ShowAssistanceAcknowledgedPending()
    {
        if (!BeginVisualUpdate("assistance-acknowledged-pending"))
            return;
        SetAssistanceState(requested: true, acknowledged: true);
        ApplyPendingMessage("The guest was told assistance is on the way.");
    }

    public void ShowCommandError(string message)
    {
        if (!BeginVisualUpdate($"command-error:{message}"))
            return;
        SetBaseStatusColor(ErrorColor);
        _status.Text = "Command failed\n" + message;
        _status.ForeColor = Color.FromArgb(187, 34, 46);
    }

    public void SetBusy(bool busy) => SetButtonsEnabled(IsLinked && !busy);

    private bool BeginVisualUpdate(string key)
    {
        if (string.Equals(_visualStateKey, key, StringComparison.Ordinal))
            return false;
        _visualStateKey = key;
        _visualUpdateCount++;
        return true;
    }

    private void SetBaseStatusColor(Color color)
    {
        _baseStatusColor = color;
        if (!_assistanceRequested || !_assistanceFlashYellow)
            _dot.StatusColor = color;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _buttonsEnabled = enabled;
        _close.Enabled = enabled;
        _open.Enabled = enabled;
        _reset.Enabled = enabled;
        UpdateAssistanceButton();
    }

    private void SetAssistanceState(bool requested, bool acknowledged)
    {
        _assistanceRequested = requested;
        _assistanceAcknowledged = requested && acknowledged;
        _assistanceFlashTimer.Stop();
        _assistanceFlashYellow = false;
        _dot.StatusColor = _baseStatusColor;
        if (_assistanceRequested)
        {
            _assistanceFlashYellow = true;
            _dot.StatusColor = AssistanceColor;
            _assistanceFlashTimer.Start();
        }
        UpdateAssistanceButton();
    }

    private void UpdateAssistanceButton()
    {
        if (!_assistanceRequested)
        {
            _assistance.Text = "NO CALL";
            _assistance.BackColor = Color.FromArgb(120, 126, 132);
            _assistance.ForeColor = Color.White;
            _assistance.Enabled = false;
            return;
        }
        if (_assistanceAcknowledged)
        {
            _assistance.Text = "ANSWERED";
            _assistance.BackColor = Color.FromArgb(120, 126, 132);
            _assistance.ForeColor = Color.White;
            _assistance.Enabled = false;
            return;
        }

        _assistance.Text = _details.Visible ? "ACKNOWLEDGE" : "ACK";
        _assistance.BackColor = AssistanceColor;
        _assistance.ForeColor = Color.FromArgb(16, 24, 32);
        _assistance.Enabled = _buttonsEnabled;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _assistanceFlashTimer.Dispose();
        base.Dispose(disposing);
    }

    private static void ConfigureActionButton(
        Button button, string text, Color background, Color foreground)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(2);
        button.BackColor = background;
        button.ForeColor = foreground;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.TabStop = false;
        button.UseVisualStyleBackColor = false;
    }
}

internal sealed class KioskStatusDot : Control
{
    private Color _statusColor = Color.FromArgb(82, 88, 96);

    public Color StatusColor
    {
        get => _statusColor;
        set
        {
            if (_statusColor == value)
                return;
            _statusColor = value;
            Invalidate();
        }
    }

    public KioskStatusDot()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.SupportsTransparentBackColor,
            true);
        MinimumSize = new Size(34, 34);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.Graphic;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var size = Math.Max(12, Math.Min(ClientSize.Width, ClientSize.Height) - 10);
        var circle = new Rectangle(
            (ClientSize.Width - size) / 2,
            (ClientSize.Height - size) / 2,
            size,
            size);
        using var glow = new SolidBrush(Color.FromArgb(65, _statusColor));
        e.Graphics.FillEllipse(glow, Rectangle.Inflate(circle, 4, 4));
        using var fill = new SolidBrush(_statusColor);
        using var outline = new Pen(ControlPaint.Light(_statusColor), 2.5f);
        e.Graphics.FillEllipse(fill, circle);
        e.Graphics.DrawEllipse(outline, circle);
        var highlight = new Rectangle(circle.X + circle.Width / 5, circle.Y + circle.Height / 6,
            Math.Max(3, circle.Width / 4), Math.Max(3, circle.Height / 5));
        using var shine = new SolidBrush(Color.FromArgb(95, Color.White));
        e.Graphics.FillEllipse(shine, highlight);
    }
}
