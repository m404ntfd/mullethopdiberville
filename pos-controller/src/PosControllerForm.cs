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
    private readonly Button _reloadBrowserButton = new();
    private readonly Button _staffMenuButton = new();
    private readonly Button _minimizeButton = new();
    private readonly Button _exitButton = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly Panel _sidebar = new();
    private readonly Panel _browserPanel = new();
    private readonly Label _brandTitle = new();
    private readonly PictureBox _brandLogo = new();
    private readonly TableLayoutPanel _brandLayout = new();
    private readonly KioskControlCard[] _cards = new KioskControlCard[4];
    private FirefoxHost? _firefoxHost;
    private bool _sidebarExpanded = true;
    private bool _refreshInProgress;
    private bool _updateCheckInProgress;
    private bool _staffMenuOpen;
    private bool _exitApproved;
    private bool _cleanupComplete;
    private bool _hiddenToTray;
    private Rectangle _fullScreenBounds;

    public PosControllerForm(PosSettings settings)
    {
        _settings = settings;
        Text = "Mullet Hop Kiosk Status Viewer";
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
        ConfigureTrayIcon();

        _firefoxHost = new FirefoxHost(_browserPanel);
        _firefoxHost.StatusChanged += (_, status) => SetBrowserStatus(status);
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
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && !_hiddenToTray)
                BeginInvoke(new Action(HideToTray));
        };
        FormClosing += HandleFormClosing;
    }

    private Control BuildBrowserArea()
    {
        _browserPanel.Dock = DockStyle.Fill;
        _browserPanel.BackColor = Color.FromArgb(239, 244, 248);
        _browserPanel.Padding = Padding.Empty;

        _browserStatus.Dock = DockStyle.Fill;
        _browserStatus.Text = "Starting Firefox and loading LilyPad POS…";
        _browserStatus.TextAlign = ContentAlignment.MiddleCenter;
        _browserStatus.Font = new Font("Segoe UI", 16, FontStyle.Bold);
        _browserStatus.ForeColor = Color.FromArgb(83, 97, 109);
        _browserStatus.BackColor = Color.FromArgb(239, 244, 248);
        _browserStatus.Padding = new Padding(40);
        _browserPanel.Controls.Add(_browserStatus);
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 184));

        ConfigureSidebarButton(_toggleSidebarButton, "◀  COLLAPSE CONTROLS",
            Color.FromArgb(255, 217, 188), Color.FromArgb(30, 20, 36));
        _toggleSidebarButton.Click += (_, _) => SetSidebarExpanded(!_sidebarExpanded);
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
        _brandTitle.Text = "KIOSK STATUS VIEWER";
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
                await SendCommandAsync(slot, PosCommandTypes.SetClosed, true);
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
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = new Padding(0, 6, 0, 0),
            BackColor = Color.Transparent
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 25));

        _connectionStatus.Dock = DockStyle.Fill;
        _connectionStatus.Text = "Starting…";
        _connectionStatus.ForeColor = Color.FromArgb(221, 226, 232);
        _connectionStatus.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _connectionStatus.TextAlign = ContentAlignment.MiddleCenter;
        _connectionStatus.AutoEllipsis = true;
        _connectionStatus.Margin = new Padding(3);
        panel.Controls.Add(_connectionStatus, 0, 0);
        panel.SetColumnSpan(_connectionStatus, 2);

        ConfigureSidebarButton(_reloadBrowserButton, "RELOAD LILYPAD",
            Color.FromArgb(8, 119, 189), Color.White);
        _reloadBrowserButton.Click += (_, _) => _firefoxHost?.ReloadHomePage();
        panel.Controls.Add(_reloadBrowserButton, 0, 1);
        panel.SetColumnSpan(_reloadBrowserButton, 2);

        ConfigureSidebarButton(_staffMenuButton, "STAFF MENU",
            Color.FromArgb(255, 217, 188), Color.FromArgb(30, 20, 36));
        _staffMenuButton.Click += (_, _) => OpenSettings();
        panel.Controls.Add(_staffMenuButton, 0, 2);
        panel.SetColumnSpan(_staffMenuButton, 2);

        ConfigureSidebarButton(_checkUpdateButton, "CHECK FOR UPDATES",
            Color.FromArgb(117, 68, 154), Color.White);
        _checkUpdateButton.Click += async (_, _) => await CheckForPosUpdateAsync();
        panel.Controls.Add(_checkUpdateButton, 0, 3);
        panel.SetColumnSpan(_checkUpdateButton, 2);

        ConfigureSidebarButton(_minimizeButton, "MINIMIZE",
            Color.FromArgb(66, 75, 86), Color.White);
        _minimizeButton.Click += (_, _) => HideToTray();
        panel.Controls.Add(_minimizeButton, 0, 4);

        ConfigureSidebarButton(_exitButton, "EXIT APP",
            Color.FromArgb(187, 34, 46), Color.White);
        _exitButton.Click += (_, _) => RequestApplicationExit();
        panel.Controls.Add(_exitButton, 1, 4);
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
        _reloadBrowserButton.Text = expanded ? "RELOAD LILYPAD" : "RELOAD";
        _staffMenuButton.Text = expanded ? "STAFF MENU" : "STAFF";
        _checkUpdateButton.Text = expanded
            ? (PosUpdater.HasStagedUpdate ? "INSTALL UPDATE" : "CHECK FOR UPDATES")
            : (PosUpdater.HasStagedUpdate ? "INSTALL" : "UPDATE");
        _minimizeButton.Text = expanded ? "MINIMIZE" : "—";
        _exitButton.Text = expanded ? "EXIT APP" : "X";
        foreach (var card in _cards)
            card?.SetExpanded(expanded);
        _firefoxHost?.ResizeToHost();
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
                    "A downloaded Kiosk Status Viewer update is ready to install.");
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

            MessageBox.Show(this, result.Message, "Kiosk Status Viewer Update",
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
            message + "\n\nInstall it now? The Kiosk Status Viewer will close and restart automatically.",
            "Install Kiosk Status Viewer Update",
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
            MessageBox.Show(this, result.Message, "Kiosk Status Viewer Update",
                MessageBoxButtons.OK,
                result.Status == PosUpdateStatus.Failed
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);
        }
    }

    private async Task RefreshStatusesAsync()
    {
        if (_refreshInProgress)
            return;
        if (!_settings.HasConnectionSettings)
        {
            UpdateUnlinkedCards();
            SetConnectionStatus("Open Staff Menu to connect the viewer.", false);
            return;
        }

        _refreshInProgress = true;
        try
        {
            var client = new PosControllerClient(_settings.ControllerUrl, _settings.PairingKey);
            var response = await client.GetStatusAsync();
            var previousSlots = _settings.KioskSlots.ToArray();
            var added = _settings.RememberSuccessfulConnection(
                _settings.ControllerUrl,
                _settings.PairingKey,
                response.Kiosks);
            if (added > 0 || !previousSlots.SequenceEqual(_settings.KioskSlots, StringComparer.Ordinal))
            {
                UpdateUnlinkedCards();
                PosLog.Write(
                    $"Automatically added {added} kiosk device{(added == 1 ? "" : "s")} from the Kiosk Controller.");
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
                    : $"Controller connected • {DateTime.Now:h:mm:ss tt}",
                true);
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

    private async Task SendCommandAsync(int slot, string commandType, bool? closed = null)
    {
        if (slot < 0 || slot >= _settings.KioskSlots.Count)
            return;
        var stationId = _settings.KioskSlots[slot];
        if (string.IsNullOrWhiteSpace(stationId))
        {
            MessageBox.Show(this,
                $"Kiosk {slot + 1} is not linked. Open the Staff Menu to assign a waiver kiosk.",
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
                _cards[slot].ShowPendingState(open: false, "Closing waiver station…");
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
                PosLog.Write("Incorrect Kiosk Status Viewer staff-menu passcode entered.");
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
            RestoreFromTray();
            using var pin = new PinEntryDialog(
                "Exit Kiosk Status Viewer",
                "Enter the Staff Menu passcode to close Firefox and the Kiosk Status Viewer.",
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
                "Close LilyPad POS, Firefox, and the Kiosk Status Viewer?",
                "Exit Kiosk Status Viewer",
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
            HideToTray();
            return;
        }
        Cleanup();
    }

    private void ConfigureTrayIcon()
    {
        var showItem = new ToolStripMenuItem("Show Kiosk Status Viewer");
        showItem.Click += (_, _) => RestoreFromTray();
        var staffItem = new ToolStripMenuItem("Staff Menu");
        staffItem.Click += (_, _) =>
        {
            RestoreFromTray();
            BeginInvoke(new Action(OpenSettings));
        };
        var exitItem = new ToolStripMenuItem("Exit Application…");
        exitItem.Click += (_, _) => RequestApplicationExit();
        _trayMenu.Items.Add(showItem);
        _trayMenu.Items.Add(staffItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.Icon = Icon ?? SystemIcons.Application;
        _trayIcon.Text = "Mullet Hop Kiosk Status Viewer";
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void HideToTray()
    {
        if (_cleanupComplete || _hiddenToTray)
            return;

        if (WindowState == FormWindowState.Normal && !Bounds.IsEmpty)
            _fullScreenBounds = Bounds;
        _hiddenToTray = true;
        ShowInTaskbar = false;
        Hide();
        WindowState = FormWindowState.Normal;
        PosLog.Write("Kiosk Status Viewer minimized to the notification area.");
    }

    private void RestoreFromTray()
    {
        if (_cleanupComplete || IsDisposed)
            return;

        _hiddenToTray = false;
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
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
        _firefoxHost?.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
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
        _connectionStatus.Text = text;
        _connectionStatus.ForeColor = connected
            ? Color.FromArgb(177, 231, 151)
            : Color.FromArgb(255, 170, 176);
    }

    private void SetBrowserStatus(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetBrowserStatus(text)));
            return;
        }
        _browserStatus.Text = text;
        _browserStatus.Visible = !text.Contains("is running", StringComparison.OrdinalIgnoreCase);
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

internal sealed class KioskControlCard : Panel
{
    private static readonly Color OnlineColor = Color.FromArgb(38, 205, 91);
    private static readonly Color ErrorColor = Color.FromArgb(244, 34, 48);
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

    public event EventHandler? CloseRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler? ResetRequested;
    public event EventHandler? AssistanceAcknowledgedRequested;
    public bool IsLinked { get; set; }

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
        var open = kiosk.IsOnline && kiosk.AvailableForGuests && !kiosk.HasError;
        SetBaseStatusColor(open ? OnlineColor : ErrorColor);
        SetAssistanceState(kiosk.AssistanceRequested, kiosk.AssistanceAcknowledged);
        var message = kiosk.IsOnline
            ? (string.IsNullOrWhiteSpace(kiosk.StatusMessage)
                ? (open ? "Online and open to guests" : "Waiver station unavailable")
                : kiosk.StatusMessage)
            : "Waiver kiosk is offline";
        _status.Text = $"{kiosk.StationName}\n{message}";
        _status.ForeColor = open ? Color.FromArgb(44, 116, 29) : Color.FromArgb(187, 34, 46);
        _dot.AccessibleDescription = $"Kiosk {_kioskNumber}: {message}";
        SetButtonsEnabled(true);
    }

    public void ShowUnlinked()
    {
        IsLinked = false;
        SetBaseStatusColor(UnlinkedColor);
        SetAssistanceState(false, false);
        _status.Text = "Not linked\nOpen Staff Menu to assign a kiosk";
        _status.ForeColor = Color.FromArgb(83, 97, 109);
        _dot.AccessibleDescription = $"Kiosk {_kioskNumber}: not linked";
        SetButtonsEnabled(false);
    }

    public void ShowMissing()
    {
        IsLinked = true;
        SetBaseStatusColor(ErrorColor);
        SetAssistanceState(false, false);
        _status.Text = "Linked kiosk not found by controller";
        _status.ForeColor = Color.FromArgb(187, 34, 46);
        _dot.AccessibleDescription = $"Kiosk {_kioskNumber}: not found";
        SetButtonsEnabled(true);
    }

    public void ShowControllerUnavailable()
    {
        SetBaseStatusColor(ErrorColor);
        SetAssistanceState(false, false);
        _status.Text = "Kiosk status unavailable";
        _status.ForeColor = Color.FromArgb(187, 34, 46);
    }

    public void ShowPendingState(bool open, string message)
    {
        SetBaseStatusColor(open ? OnlineColor : ErrorColor);
        ShowPendingMessage(message);
    }

    public void ShowPendingMessage(string message)
    {
        _status.Text = message;
        _status.ForeColor = Color.FromArgb(125, 77, 9);
    }

    public void ShowAssistanceAcknowledgedPending()
    {
        SetAssistanceState(requested: true, acknowledged: true);
        ShowPendingMessage("The guest was told assistance is on the way.");
    }

    public void ShowCommandError(string message)
    {
        SetBaseStatusColor(ErrorColor);
        _status.Text = "Command failed\n" + message;
        _status.ForeColor = Color.FromArgb(187, 34, 46);
    }

    public void SetBusy(bool busy) => SetButtonsEnabled(IsLinked && !busy);

    private void SetBaseStatusColor(Color color)
    {
        _baseStatusColor = color;
        if (!_assistanceRequested || _assistanceAcknowledged || !_assistanceFlashYellow)
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
        if (_assistanceRequested && !_assistanceAcknowledged)
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
            _assistance.Text = "ON WAY";
            _assistance.BackColor = Color.FromArgb(118, 196, 66);
            _assistance.ForeColor = Color.FromArgb(16, 24, 32);
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
