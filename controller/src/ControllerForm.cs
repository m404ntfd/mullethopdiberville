using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;

namespace MulletHopKioskController;

internal sealed class ControllerForm : Form
{
    private readonly ControllerState _state = new();
    private readonly ControllerServer _server;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1000 };
    private readonly Label _serviceStatus = new();
    private readonly ComboBox _addresses = new();
    private readonly TextBox _pairingKey = new();
    private readonly Label _onlineSummary = new();
    private readonly Label _closedSummary = new();
    private readonly Label _totalSummary = new();
    private readonly ListView _kioskList = new();
    private readonly Label _selectionStatus = new();
    private readonly Button _openButton = new();
    private readonly Button _closeButton = new();
    private readonly Button _checkUpdateButton = new();
    private readonly Button _installUpdateButton = new();
    private readonly Button _controllerUpdateButton = new();
    private readonly Button _manageAdsButton = new();
    private readonly Button _businessHoursButton = new();
    private readonly Button _remoteAccessButton = new();
    private readonly Button _restartControllerButton = new();
    private readonly Button _closeControllerButton = new();
    private readonly ComboBox _themeSelector = new();
    private readonly Label _controllerUpdateStatus = new();
    private readonly Label _controllerUpdateReady = new();
    private readonly ControllerStatusLight _masterRed = new(
        Color.FromArgb(244, 34, 48), Color.FromArgb(80, 25, 29));
    private readonly ControllerStatusLight _masterGreen = new(
        Color.FromArgb(38, 205, 91), Color.FromArgb(23, 75, 42));
    private readonly Label _masterStatus = new();
    private readonly Button _masterToggleButton = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly RemoteAccessSettings _remoteSettings = RemoteAccessSettingsStore.Load();
    private CloudSyncService? _cloudSync;
    private bool _lastResolvedDarkMode;
    private bool _masterChangeInProgress;
    private bool _allowApplicationExit;
    private bool _trayNoticeShown;
    private bool _assistanceFlashOn;

    public ControllerForm()
    {
        if (_remoteSettings.IsRemoteMachine && _state.IsMaster)
            _state.SetMaster(false, "remote-mode controllers cannot be the local master");
        _server = new ControllerServer(_state);
        _server.Peers.PeersChanged += ControllerPeersChanged;
        Text = "Mullet Hop Kiosk Controller";
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon is not null)
        {
            Icon = appIcon;
            _trayIcon.Icon = (Icon)appIcon.Clone();
        }
        else
        {
            _trayIcon.Icon = SystemIcons.Application;
        }
        ConfigureTrayIcon();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1180, 760);
        ClientSize = new Size(1320, 820);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 248, 251);

        var header = BuildHeader();
        var setup = BuildSetupPanel();
        var summaries = BuildSummaryPanel();
        var actions = BuildActionPanel();
        ConfigureKioskList();

        Controls.Add(_kioskList);
        Controls.Add(actions);
        Controls.Add(summaries);
        Controls.Add(setup);
        Controls.Add(header);

        _lastResolvedDarkMode = ControllerTheme.IsDark;
        ControllerTheme.Apply(this);

        _refreshTimer.Tick += (_, _) => RefreshKioskList();
        Shown += async (_, _) =>
        {
            StartControllerServices();
            UpdateMasterStatus();
            await CheckControllerUpdateAsync(showUpToDateMessage: false);
        };
        FormClosed += (_, _) =>
        {
            _refreshTimer.Stop();
            _cloudSync?.Dispose();
            _server.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
        };
        FormClosing += HandleFormClosing;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                HideControllerInTray();
        };
        Activated += (_, _) =>
        {
            if (ControllerTheme.Mode == ControllerThemeMode.Auto &&
                _lastResolvedDarkMode != ControllerTheme.IsDark)
                ApplyControllerTheme();
        };
    }

    private void ConfigureTrayIcon()
    {
        var open = new ToolStripMenuItem("Open Kiosk Controller");
        open.Font = new Font(open.Font, FontStyle.Bold);
        open.Click += (_, _) => RestoreControllerFromTray();
        _trayMenu.Items.Add(open);
        _trayIcon.Text = "Mullet Hop Kiosk Controller";
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.Visible = true;
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                RestoreControllerFromTray();
        };
        _trayIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                RestoreControllerFromTray();
        };
        _trayIcon.BalloonTipClicked += (_, _) => RestoreControllerFromTray();
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowApplicationExit ||
            e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            _trayIcon.Visible = false;
            return;
        }

        e.Cancel = true;
        HideControllerInTray();
    }

    private void HideControllerInTray()
    {
        if (IsDisposed || _allowApplicationExit)
            return;

        ShowInTaskbar = false;
        Hide();
        WindowState = FormWindowState.Normal;
        if (_trayNoticeShown)
            return;

        _trayNoticeShown = true;
        _trayIcon.BalloonTipTitle = "Kiosk Controller is still running";
        _trayIcon.BalloonTipText =
            "The controller service remains active. Double-click the fish icon to reopen it.";
        _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
        _trayIcon.ShowBalloonTip(3_000);
    }

    private void RestoreControllerFromTray()
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(RestoreControllerFromTray));
            return;
        }

        ShowInTaskbar = true;
        Visible = true;
        Show();
        WindowState = FormWindowState.Normal;
        TopMost = true;
        Activate();
        BringToFront();
        Focus();
        TopMost = false;
    }

    private Panel BuildHeader()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 78,
            BackColor = Color.FromArgb(117, 68, 154)
        };
        var logo = new PictureBox
        {
            Bounds = new Rectangle(18, 8, 94, 62),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Image = LoadHeaderLogo()
        };
        var title = new Label
        {
            AutoSize = false,
            Text = "MULLET HOP KIOSK CONTROLLER",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 23, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(120, 8, 690, 58),
            Anchor = AnchorStyles.Left | AnchorStyles.Top
        };
        _serviceStatus.AutoSize = false;
        _serviceStatus.Text = "Starting local network service…";
        _serviceStatus.ForeColor = Color.White;
        _serviceStatus.BackColor = Color.FromArgb(82, 49, 108);
        _serviceStatus.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _serviceStatus.TextAlign = ContentAlignment.MiddleCenter;
        _serviceStatus.Bounds = new Rectangle(850, 18, 320, 42);
        _serviceStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        panel.Controls.AddRange([logo, title, _serviceStatus]);
        return panel;
    }

    private GroupBox BuildSetupPanel()
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Top,
            Height = 215,
            Padding = new Padding(18, 24, 18, 10),
            Text = "Controller Connection Information",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            BackColor = Color.White
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.White
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var addressLabel = new Label
        {
            Text = "Controller address:",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(16, 24, 32),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(3, 2, 3, 2)
        };
        _addresses.DropDownStyle = ComboBoxStyle.DropDownList;
        _addresses.Dock = DockStyle.Fill;
        _addresses.Font = new Font("Segoe UI", 9.5f);
        _addresses.Margin = new Padding(3, 4, 8, 4);
        foreach (var address in GetControllerAddresses())
            _addresses.Items.Add(address);
        if (_addresses.Items.Count > 0)
            _addresses.SelectedIndex = 0;
        var copyAddress = MakeTableButton("Copy Address", Color.FromArgb(105, 210, 236));
        copyAddress.Click += (_, _) => CopyText(_addresses.Text, "Controller address copied.");

        var keyLabel = new Label
        {
            Text = "Pairing key:",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(16, 24, 32),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(3, 2, 3, 2)
        };
        _pairingKey.Text = _state.PairingKey;
        _pairingKey.ReadOnly = true;
        _pairingKey.UseSystemPasswordChar = true;
        _pairingKey.Dock = DockStyle.Fill;
        _pairingKey.Font = new Font("Segoe UI", 9.5f);
        _pairingKey.Margin = new Padding(3, 4, 8, 4);
        var viewKey = MakeTableButton("View Key", Color.FromArgb(255, 217, 188));
        viewKey.Click += (_, _) =>
        {
            _pairingKey.UseSystemPasswordChar = !_pairingKey.UseSystemPasswordChar;
            viewKey.Text = _pairingKey.UseSystemPasswordChar ? "View Key" : "Hide Key";
        };
        var copyKey = MakeTableButton("Copy Key", Color.FromArgb(118, 196, 66));
        copyKey.Click += (_, _) => CopyText(_state.PairingKey, "Pairing key copied.");

        var note = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "Use Discover Kiosks for automatic pairing. If a waiver station does not appear, use Add Kiosk Manually and enter the IPv4 address shown on that kiosk. No code is required for IP pairing.",
            ForeColor = Color.FromArgb(52, 65, 76),
            Font = new Font("Segoe UI", 9.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(3, 3, 3, 0)
        };
        var discover = MakeTableButton("Discover Kiosks", Color.FromArgb(245, 130, 32));
        discover.Click += (_, _) => OpenKioskDiscovery();
        var manualAdd = MakeTableButton("Add Kiosk Manually", Color.FromArgb(105, 210, 236));
        manualAdd.Click += (_, _) => OpenManualKioskSetup();

        layout.Controls.Add(addressLabel, 0, 0);
        layout.Controls.Add(_addresses, 1, 0);
        layout.Controls.Add(copyAddress, 2, 0);
        layout.SetColumnSpan(copyAddress, 2);
        layout.Controls.Add(keyLabel, 0, 1);
        layout.Controls.Add(_pairingKey, 1, 1);
        layout.Controls.Add(viewKey, 2, 1);
        layout.Controls.Add(copyKey, 3, 1);
        layout.Controls.Add(note, 0, 2);
        layout.SetColumnSpan(note, 4);
        layout.Controls.Add(manualAdd, 2, 3);
        layout.Controls.Add(discover, 3, 3);
        group.Controls.Add(layout);
        return group;
    }

    private void OpenKioskDiscovery()
    {
        if (_remoteSettings.IsRemoteMachine)
        {
            MessageBox.Show(this,
                "Kiosk discovery is available only from the controller computer on the same local network as the waiver kiosks.",
                "Discover Waiver Kiosks", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!_server.IsRunning)
        {
            MessageBox.Show(this,
                "The local controller service is not running. Resolve the network service error, then try again.",
                "Discover Waiver Kiosks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var discovery = new KioskDiscoveryDialog(_server.Discovery, _state);
        discovery.ShowDialog(this);
        RefreshKioskList();
    }

    private void OpenManualKioskSetup()
    {
        if (_remoteSettings.IsRemoteMachine)
        {
            MessageBox.Show(this,
                "Manual kiosk setup is available only from the on-site controller computer on the same local network as the waiver kiosk.",
                "Add Kiosk Manually", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!_server.IsRunning)
        {
            MessageBox.Show(this,
                "The local controller service is not running. Resolve the network service error, then try again.",
                "Add Kiosk Manually", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_addresses.Text) ||
            _addresses.Text.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this,
                "This computer does not currently have a usable private-network IPv4 address. Connect it to the kiosk network, then restart the controller.",
                "Add Kiosk Manually", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            using var setup = new ManualKioskSetupDialog(
                _state,
                _server.Discovery,
                _addresses.Text);
            setup.ShowDialog(this);
            RefreshKioskList();
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(this, ex.Message, "Add Kiosk Manually",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private Panel BuildSummaryPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 70,
            Padding = new Padding(18, 8, 18, 8),
            BackColor = Color.FromArgb(244, 248, 251)
        };
        ConfigureSummaryLabel(_onlineSummary, "0 ONLINE", 18, Color.FromArgb(54, 128, 27));
        ConfigureSummaryLabel(_closedSummary, "0 CLOSED", 258, Color.FromArgb(245, 130, 32));
        ConfigureSummaryLabel(_totalSummary, "0 KNOWN KIOSKS", 498, Color.FromArgb(8, 119, 189));
        panel.Controls.AddRange([_onlineSummary, _closedSummary, _totalSummary]);
        return panel;
    }

    private Panel BuildActionPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 320,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = Color.White
        };

        var sections = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.White
        };
        sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        sections.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var kioskGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Kiosk Controls",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Padding = new Padding(10, 22, 10, 8),
            Margin = new Padding(0, 0, 8, 0)
        };
        _selectionStatus.Text = "Select a kiosk above to manage it.";
        _selectionStatus.AutoSize = false;
        _selectionStatus.Dock = DockStyle.Top;
        _selectionStatus.Height = 27;
        _selectionStatus.ForeColor = Color.FromArgb(52, 65, 76);
        _selectionStatus.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);

        var kioskButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(0, 2, 0, 0),
            Margin = Padding.Empty
        };
        for (var index = 0; index < 3; index++)
            kioskButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        kioskButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        kioskButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        ConfigureTableActionButton(_openButton, "Open Selected", Color.FromArgb(118, 196, 66));
        ConfigureTableActionButton(_closeButton, "Close Selected", Color.FromArgb(245, 130, 32));
        ConfigureTableActionButton(_checkUpdateButton, "Check Kiosk Update", Color.FromArgb(105, 210, 236));
        ConfigureTableActionButton(_installUpdateButton, "Install Kiosk Update", Color.FromArgb(117, 68, 154), Color.White);
        _openButton.Click += (_, _) => QueueSelected(CommandTypes.SetClosed, false);
        _closeButton.Click += (_, _) => QueueSelected(CommandTypes.SetClosed, true);
        _checkUpdateButton.Click += (_, _) => QueueSelected(CommandTypes.CheckUpdate);
        _installUpdateButton.Click += (_, _) => InstallSelectedUpdate();

        var openAll = new Button();
        ConfigureTableActionButton(openAll, "Open All", Color.FromArgb(210, 239, 190));
        openAll.Click += (_, _) => QueueForAll(CommandTypes.SetClosed, false);
        var closeAll = new Button();
        ConfigureTableActionButton(closeAll, "Close All", Color.FromArgb(255, 217, 188));
        closeAll.Click += (_, _) => CloseAllKiosks();
        kioskButtons.Controls.Add(_openButton, 0, 0);
        kioskButtons.Controls.Add(_closeButton, 1, 0);
        kioskButtons.Controls.Add(_checkUpdateButton, 2, 0);
        kioskButtons.Controls.Add(_installUpdateButton, 0, 1);
        kioskButtons.Controls.Add(openAll, 1, 1);
        kioskButtons.Controls.Add(closeAll, 2, 1);
        kioskGroup.Controls.Add(kioskButtons);
        kioskGroup.Controls.Add(_selectionStatus);

        var controllerGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Controller Program",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Padding = new Padding(10, 22, 10, 8),
            Margin = new Padding(0)
        };
        var controllerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        controllerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        controllerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        controllerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        controllerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        controllerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _controllerUpdateStatus.Text = $"Version {ControllerUpdater.CurrentVersion} — checking for updates…";
        _controllerUpdateStatus.Dock = DockStyle.Fill;
        _controllerUpdateStatus.TextAlign = ContentAlignment.MiddleLeft;
        _controllerUpdateStatus.ForeColor = Color.FromArgb(52, 65, 76);
        _controllerUpdateStatus.Font = new Font("Segoe UI", 9);

        _controllerUpdateReady.Text = "! Update Ready to Install";
        _controllerUpdateReady.Dock = DockStyle.Fill;
        _controllerUpdateReady.TextAlign = ContentAlignment.MiddleLeft;
        _controllerUpdateReady.ForeColor = Color.FromArgb(196, 28, 28);
        _controllerUpdateReady.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _controllerUpdateReady.Visible = false;

        var appearancePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 3, 0, 2)
        };
        var appearanceLabel = new Label
        {
            Text = "Appearance:",
            AutoSize = false,
            Width = 92,
            Height = 29,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(52, 65, 76),
            Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
            Margin = Padding.Empty
        };
        _themeSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _themeSelector.Width = 170;
        _themeSelector.Height = 29;
        _themeSelector.Font = new Font("Segoe UI", 9.2f);
        _themeSelector.Margin = Padding.Empty;
        _themeSelector.Items.AddRange(["Auto (Windows)", "Light", "Dark"]);
        _themeSelector.SelectedIndex = ControllerTheme.Mode switch
        {
            ControllerThemeMode.Light => 1,
            ControllerThemeMode.Dark => 2,
            _ => 0
        };
        _themeSelector.SelectedIndexChanged += (_, _) => ChangeControllerTheme();
        appearancePanel.Controls.AddRange([appearanceLabel, _themeSelector]);

        var masterPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, 3, 0, 3)
        };
        masterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        masterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        masterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        masterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        _masterRed.Dock = DockStyle.Fill;
        _masterRed.Margin = new Padding(1);
        _masterGreen.Dock = DockStyle.Fill;
        _masterGreen.Margin = new Padding(1);
        _masterStatus.Dock = DockStyle.Fill;
        _masterStatus.TextAlign = ContentAlignment.MiddleLeft;
        _masterStatus.Font = new Font("Segoe UI", 8.7f, FontStyle.Bold);
        _masterStatus.ForeColor = Color.FromArgb(52, 65, 76);
        _masterStatus.AutoEllipsis = true;
        _masterStatus.Margin = new Padding(5, 0, 4, 0);
        _masterToggleButton.Dock = DockStyle.Fill;
        _masterToggleButton.Margin = new Padding(3, 1, 0, 1);
        _masterToggleButton.FlatStyle = FlatStyle.Flat;
        _masterToggleButton.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _masterToggleButton.Click += async (_, _) => await ToggleMasterAsync();
        masterPanel.Controls.Add(_masterRed, 0, 0);
        masterPanel.Controls.Add(_masterGreen, 1, 0);
        masterPanel.Controls.Add(_masterStatus, 2, 0);
        masterPanel.Controls.Add(_masterToggleButton, 3, 0);

        var controllerButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        for (var index = 0; index < 3; index++)
            controllerButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        controllerButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        controllerButtons.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        ConfigureTableActionButton(_controllerUpdateButton, "Check Updates", Color.FromArgb(8, 119, 189), Color.White);
        ConfigureTableActionButton(_manageAdsButton, "Manage Ads", Color.FromArgb(117, 68, 154), Color.White);
        ConfigureTableActionButton(_businessHoursButton, "Business Hours", Color.FromArgb(118, 196, 66));
        ConfigureTableActionButton(_remoteAccessButton, "Remote Access", Color.FromArgb(105, 210, 236));
        ConfigureTableActionButton(_restartControllerButton, "Restart Controller", Color.FromArgb(245, 130, 32));
        ConfigureTableActionButton(_closeControllerButton, "Exit Program", Color.FromArgb(180, 35, 24), Color.White);
        foreach (var button in new[]
                 {
                     _controllerUpdateButton,
                     _manageAdsButton,
                     _businessHoursButton,
                     _remoteAccessButton,
                     _restartControllerButton,
                     _closeControllerButton
                 })
        {
            button.MinimumSize = new Size(120, 48);
            button.Margin = new Padding(4);
            button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        }
        _controllerUpdateButton.Click += async (_, _) => await CheckControllerUpdateAsync(showUpToDateMessage: true);
        _manageAdsButton.Click += (_, _) =>
        {
            using var advertisements = new ControllerAdvertisementManagerDialog(_state);
            advertisements.ShowDialog(this);
            RefreshKioskList();
        };
        _businessHoursButton.Click += (_, _) =>
        {
            using var businessHours = new ControllerBusinessHoursDialog(_state, SelectedStationId());
            businessHours.ShowDialog(this);
            RefreshKioskList();
        };
        _remoteAccessButton.Click += (_, _) => OpenRemoteAccessSettings();
        _restartControllerButton.Click += (_, _) => RestartController();
        _closeControllerButton.Click += (_, _) => CloseController();
        controllerButtons.Controls.Add(_controllerUpdateButton, 0, 0);
        controllerButtons.Controls.Add(_manageAdsButton, 1, 0);
        controllerButtons.Controls.Add(_businessHoursButton, 2, 0);
        controllerButtons.Controls.Add(_remoteAccessButton, 0, 1);
        controllerButtons.Controls.Add(_restartControllerButton, 1, 1);
        controllerButtons.Controls.Add(_closeControllerButton, 2, 1);

        controllerLayout.Controls.Add(_controllerUpdateStatus, 0, 0);
        controllerLayout.Controls.Add(_controllerUpdateReady, 0, 1);
        controllerLayout.Controls.Add(appearancePanel, 0, 2);
        controllerLayout.Controls.Add(masterPanel, 0, 3);
        controllerLayout.Controls.Add(controllerButtons, 0, 4);
        controllerGroup.Controls.Add(controllerLayout);

        sections.Controls.Add(kioskGroup, 0, 0);
        sections.Controls.Add(controllerGroup, 1, 0);
        panel.Controls.Add(sections);
        UpdateActionButtons();
        return panel;
    }

    private void ConfigureKioskList()
    {
        _kioskList.Dock = DockStyle.Fill;
        _kioskList.View = View.Details;
        _kioskList.FullRowSelect = true;
        _kioskList.HideSelection = false;
        _kioskList.MultiSelect = false;
        _kioskList.GridLines = true;
        _kioskList.BackColor = Color.White;
        _kioskList.BorderStyle = BorderStyle.FixedSingle;
        _kioskList.Columns.Add("Status", 80);
        _kioskList.Columns.Add("Kiosk Name", 150);
        _kioskList.Columns.Add("PC Name", 120);
        _kioskList.Columns.Add("Version", 70);
        _kioskList.Columns.Add("Guest", 90);
        _kioskList.Columns.Add("Assistance", 115);
        _kioskList.Columns.Add("Ads Synced", 125);
        _kioskList.Columns.Add("Command / Result", 295);
        _kioskList.Columns.Add("Last Seen", 105);
        _kioskList.Columns.Add("IP Address", 120);
        _kioskList.SelectedIndexChanged += (_, _) => UpdateActionButtons();
        _kioskList.DoubleClick += (_, _) =>
        {
            var kiosk = SelectedKiosk();
            if (kiosk is not null)
                QueueSelected(CommandTypes.SetClosed, !kiosk.StationClosed);
        };
    }

    private void StartControllerServices()
    {
        if (!_remoteSettings.IsRemoteMachine)
            StartControllerService();
        else
        {
            _serviceStatus.Text = "● REMOTE MODE STARTING";
            _serviceStatus.BackColor = Color.FromArgb(8, 119, 189);
            _refreshTimer.Start();
            RefreshKioskList();
        }

        if (_remoteSettings.Enabled)
        {
            try
            {
                _cloudSync = new CloudSyncService(_state, _remoteSettings);
                _cloudSync.StatusChanged += (message, connected) =>
                {
                    if (IsDisposed) return;
                    BeginInvoke(() =>
                    {
                        _serviceStatus.Text = message;
                        _serviceStatus.BackColor = connected
                            ? Color.FromArgb(54, 128, 27)
                            : Color.FromArgb(180, 35, 24);
                    });
                };
                _cloudSync.Start();
            }
            catch (Exception ex)
            {
                ShowServiceError("Cloud synchronization could not start.\n\n" + ex.Message);
            }
        }
    }

    private void StartControllerService()
    {
        try
        {
            _server.Start();
            _serviceStatus.Text = $"● LISTENING ON TCP {ControllerServer.Port}";
            _serviceStatus.BackColor = Color.FromArgb(54, 128, 27);
            _refreshTimer.Start();
            RefreshKioskList();
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            ShowServiceError(
                "Windows did not allow the controller to open its network port. Run Install-Kiosk-Controller.cmd as administrator, then reopen the controller.");
        }
        catch (Exception ex)
        {
            ShowServiceError("The controller network service could not start.\n\n" + ex.Message);
        }
    }

    private void OpenRemoteAccessSettings()
    {
        using var settings = new RemoteAccessSettingsDialog(RemoteAccessSettingsStore.Load());
        if (settings.ShowDialog(this) != DialogResult.OK) return;
        var answer = MessageBox.Show(this,
            "Remote access settings were saved. Restart the controller now to apply them?",
            "Remote Access", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes) RestartControllerApplication();
    }

    private static Image? LoadHeaderLogo()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("MulletHopKioskController.Assets.MulletHopFish.png");
            return stream is null ? null : new Bitmap(stream);
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller header logo error: " + ex.Message);
            return null;
        }
    }

    private void ShowServiceError(string message)
    {
        _serviceStatus.Text = "● NETWORK SERVICE STOPPED";
        _serviceStatus.BackColor = Color.FromArgb(180, 35, 24);
        ControllerLog.Write(message);
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void RefreshKioskList()
    {
        UpdateMasterStatus();
        _assistanceFlashOn = !_assistanceFlashOn;
        var selectedId = SelectedStationId();
        var kiosks = _state.Snapshot();
        _kioskList.BeginUpdate();
        try
        {
            _kioskList.Items.Clear();
            foreach (var kiosk in kiosks)
            {
                var businessClosed = kiosk.IsOnline && kiosk.BusinessHoursClosed &&
                                     !kiosk.StationClosed && !kiosk.HasError;
                var commandText = kiosk.PendingCommand is null
                    ? kiosk.LastResult
                    : "PENDING — " + DescribeCommand(kiosk.PendingCommand);
                var item = new ListViewItem(kiosk.IsOnline
                    ? businessClosed ? "● Business Closed" : "● Online"
                    : "○ Offline")
                {
                    Tag = kiosk.StationId,
                    UseItemStyleForSubItems = false,
                    ForeColor = kiosk.IsOnline
                        ? businessClosed
                            ? ControllerTheme.BusinessClosedText
                            : ControllerTheme.OnlineText
                        : ControllerTheme.OfflineText,
                    BackColor = !kiosk.IsOnline
                        ? ControllerTheme.OfflineRow
                        : businessClosed
                            ? ControllerTheme.BusinessClosedRow
                            : kiosk.StationClosed
                            ? ControllerTheme.ClosedRow
                            : ControllerTheme.OnlineRow
                };
                item.SubItems.Add(kiosk.StationName);
                item.SubItems.Add(kiosk.MachineName);
                item.SubItems.Add(kiosk.Version);
                item.SubItems.Add(kiosk.StationClosed
                    ? "Closed by staff"
                    : businessClosed
                        ? "Business closed"
                        : "Open");
                var assistance = item.SubItems.Add(kiosk.AssistanceRequested
                    ? kiosk.AssistanceAcknowledged
                        ? "On the way"
                        : _assistanceFlashOn ? "● HELP" : "○ HELP"
                    : string.Empty);
                item.SubItems.Add(FormatAdvertisementSync(kiosk));
                item.SubItems.Add(commandText);
                item.SubItems.Add(FormatLastSeen(kiosk.LastSeenUtc));
                item.SubItems.Add(kiosk.LastIpAddress);
                foreach (ListViewItem.ListViewSubItem subItem in item.SubItems)
                {
                    subItem.ForeColor = item.ForeColor;
                    subItem.BackColor = item.BackColor;
                }
                assistance.ForeColor = kiosk.AssistanceRequested && !kiosk.AssistanceAcknowledged
                    ? _assistanceFlashOn
                        ? Color.FromArgb(255, 193, 7)
                        : Color.FromArgb(125, 103, 28)
                    : item.ForeColor;
                _kioskList.Items.Add(item);
                if (string.Equals(kiosk.StationId, selectedId, StringComparison.Ordinal))
                    item.Selected = true;
            }
        }
        finally
        {
            _kioskList.EndUpdate();
        }

        _onlineSummary.Text = $"{kiosks.Count(kiosk => kiosk.IsOnline)} ONLINE";
        _closedSummary.Text = $"{kiosks.Count(kiosk => kiosk.StationClosed || kiosk.BusinessHoursClosed)} CLOSED";
        _totalSummary.Text = $"{kiosks.Count} KNOWN KIOSKS";
        UpdateActionButtons();
    }

    private void ControllerPeersChanged()
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
            return;
        try { BeginInvoke((Action)UpdateMasterStatus); }
        catch (InvalidOperationException) { }
    }

    private void UpdateMasterStatus()
    {
        if (_masterStatus.IsDisposed)
            return;
        var isMaster = _state.IsMaster;
        _masterGreen.Active = isMaster;
        _masterRed.Active = !isMaster;

        if (_remoteSettings.IsRemoteMachine)
        {
            _masterStatus.Text = "Remote controller — not eligible for local master";
            _masterToggleButton.Text = "Local Controllers Only";
            _masterToggleButton.Enabled = false;
            _masterToggleButton.BackColor = Color.FromArgb(235, 238, 241);
            return;
        }

        var otherMaster = _server.Peers.Snapshot().FirstOrDefault(peer => peer.IsMaster);
        if (isMaster)
        {
            _masterStatus.Text = "This controller is MASTER";
            _masterStatus.ForeColor = ControllerTheme.SuccessText;
            _masterToggleButton.Text = "Remove Master";
            _masterToggleButton.BackColor = Color.FromArgb(255, 217, 188);
        }
        else
        {
            _masterStatus.Text = otherMaster is null
                ? "NOT MASTER — no master detected"
                : $"NOT MASTER — master: {otherMaster.MachineName}";
            _masterStatus.ForeColor = otherMaster is null
                ? ControllerTheme.ErrorText
                : ControllerTheme.MutedText;
            _masterToggleButton.Text = "Make This Master";
            _masterToggleButton.BackColor = Color.FromArgb(118, 196, 66);
        }
        _masterToggleButton.ForeColor = Color.FromArgb(16, 24, 32);
        _masterToggleButton.Enabled = !_masterChangeInProgress;
    }

    private async Task ToggleMasterAsync()
    {
        if (_masterChangeInProgress || _remoteSettings.IsRemoteMachine)
            return;
        if (_state.IsMaster)
        {
            var remove = MessageBox.Show(this,
                "Remove the master role from this controller?\n\nThe network may have no master until another controller is assigned.",
                "Remove Master Controller?",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (remove != DialogResult.Yes)
                return;
            _state.SetMaster(false, "removed by the user");
            UpdateMasterStatus();
            return;
        }

        var answer = MessageBox.Show(this,
            "Make this computer the master Kiosk Controller?\n\n" +
            "Only one controller on the local network can be master. The program will scan for another master before saving this change.",
            "Make This the Master Controller?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        _masterChangeInProgress = true;
        UpdateMasterStatus();
        try
        {
            await _server.Peers.ScanNowAsync();
            var existingMaster = _server.Peers.Snapshot().FirstOrDefault(peer => peer.IsMaster);
            if (existingMaster is not null)
            {
                MessageBox.Show(this,
                    $"{existingMaster.MachineName} is already the master controller at " +
                    existingMaster.ControllerAddress +
                    ".\n\nRemove its master role before making this controller the master.",
                    "Master Controller Already Exists",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            _state.SetMaster(true, "assigned by the user after a network scan");
            await _server.Peers.ScanNowAsync();
            if (_state.IsMaster)
            {
                MessageBox.Show(this,
                    "This computer is now the master Kiosk Controller.",
                    "Master Controller Saved",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                var resolvedMaster = _server.Peers.Snapshot()
                    .FirstOrDefault(peer => peer.IsMaster);
                MessageBox.Show(this,
                    resolvedMaster is null
                        ? "Another controller claimed the master role at the same time. This controller remained a non-master controller."
                        : resolvedMaster.MachineName +
                          " retained the master role after the controllers resolved a simultaneous change. This controller remained a non-master controller.",
                    "Master Role Resolved",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        finally
        {
            _masterChangeInProgress = false;
            UpdateMasterStatus();
        }
    }

    private void QueueSelected(string type, bool? closed = null)
    {
        var kiosk = SelectedKiosk();
        if (kiosk is null)
            return;

        _state.QueueCommand(kiosk.StationId, type, closed);
        _selectionStatus.Text = $"Command queued for {kiosk.StationName}.";
        RefreshKioskList();
    }

    private void InstallSelectedUpdate()
    {
        var kiosk = SelectedKiosk();
        if (kiosk is null)
            return;
        var answer = MessageBox.Show(this,
            $"Check GitHub and install an available update on {kiosk.StationName}?\n\nThe kiosk may restart automatically.",
            "Install Kiosk Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
            QueueSelected(CommandTypes.InstallUpdate);
    }

    private void QueueForAll(string type, bool? closed = null)
    {
        var count = _state.QueueCommandForAll(type, closed);
        _selectionStatus.Text = $"Command queued for {count} kiosk(s).";
        RefreshKioskList();
    }

    private void CloseAllKiosks()
    {
        var answer = MessageBox.Show(this,
            "Turn on the closed screen for every known kiosk?",
            "Close All Kiosks", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
            QueueForAll(CommandTypes.SetClosed, true);
    }

    private async Task CheckControllerUpdateAsync(bool showUpToDateMessage)
    {
        _controllerUpdateButton.Enabled = false;
        var originalText = _controllerUpdateButton.Text;
        _controllerUpdateButton.Text = "Checking…";
        _controllerUpdateStatus.Text = "Checking for controller updates…";
        try
        {
            var result = await ControllerUpdater.CheckAndStageUpdateAsync();
            if (IsDisposed)
                return;

            _controllerUpdateStatus.Text = result.Message;
            if (result.Status != ControllerUpdateStatus.ReadyToInstall)
            {
                _controllerUpdateReady.Visible = false;
                if (showUpToDateMessage || result.Status != ControllerUpdateStatus.UpToDate)
                {
                    MessageBox.Show(this, result.Message, "Controller Update",
                        MessageBoxButtons.OK,
                        result.Status == ControllerUpdateStatus.Failed
                            ? MessageBoxIcon.Warning
                            : MessageBoxIcon.Information);
                }
                return;
            }

            if (!ShowControllerUpdatePrompt(result.Message))
            {
                ShowDeferredUpdateReady();
                return;
            }

            _controllerUpdateButton.Text = "Installing…";
            var installResult = ApplyControllerUpdateAndRestart();
            if (!IsDisposed && installResult.Status != ControllerUpdateStatus.Applying)
            {
                MessageBox.Show(this, installResult.Message, "Controller Update",
                    MessageBoxButtons.OK,
                    installResult.Status == ControllerUpdateStatus.Failed
                        ? MessageBoxIcon.Warning
                        : MessageBoxIcon.Information);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _controllerUpdateButton.Text = originalText;
                _controllerUpdateButton.Enabled = true;
            }
        }
    }

    private void RestartController()
    {
        if (ControllerUpdater.HasStagedUpdate)
        {
            var answer = MessageBox.Show(this,
                "A controller update is ready to install. Restart now and install the update?",
                "Install Controller Update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer == DialogResult.Yes)
            {
                var result = ApplyControllerUpdateAndRestart();
                if (result.Status != ControllerUpdateStatus.Applying)
                {
                    MessageBox.Show(this, result.Message, "Controller Update",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            return;
        }

        var restart = MessageBox.Show(this,
            "Restart the kiosk controller now?",
            "Restart Kiosk Controller",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (restart == DialogResult.Yes)
            RestartControllerApplication();
    }

    private void CloseController()
    {
        var answer = MessageBox.Show(this,
            "Exit the kiosk controller program?\n\nKiosks will keep their current state, but remote commands will be unavailable until the controller starts again. Closing the window with X only sends it to the system tray.",
            "Exit Kiosk Controller",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
        {
            _allowApplicationExit = true;
            _trayIcon.Visible = false;
            Close();
        }
    }

    private void RestartControllerApplication()
    {
        _allowApplicationExit = true;
        _trayIcon.Visible = false;
        Program.RestartApplication();
    }

    private ControllerUpdateResult ApplyControllerUpdateAndRestart()
    {
        _allowApplicationExit = true;
        _trayIcon.Visible = false;
        var result = ControllerUpdater.ApplyStagedUpdateAndRestart();
        if (result.Status == ControllerUpdateStatus.Applying)
            return result;

        _allowApplicationExit = false;
        _trayIcon.Visible = true;
        return result;
    }

    private void ShowDeferredUpdateReady()
    {
        _controllerUpdateReady.Visible = true;
        _controllerUpdateStatus.Text =
            "The downloaded update will install when the controller is restarted.";
    }

    private bool ShowControllerUpdatePrompt(string message)
    {
        using var prompt = new Form
        {
            Text = "Controller Update Ready",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(520, 205),
            BackColor = Color.White,
            Font = new Font("Segoe UI", 10)
        };
        var updateMessage = new Label
        {
            Text = message +
                "\n\nRestart and install it now, or keep working and install it later?",
            AutoSize = false,
            Bounds = new Rectangle(24, 20, 472, 105),
            ForeColor = Color.FromArgb(16, 24, 32)
        };
        var installNow = new Button
        {
            Text = "Restart and Install Now",
            DialogResult = DialogResult.Yes,
            Bounds = new Rectangle(68, 142, 190, 44),
            BackColor = Color.FromArgb(8, 119, 189),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        var installLater = new Button
        {
            Text = "Install Later",
            DialogResult = DialogResult.No,
            Bounds = new Rectangle(272, 142, 180, 44),
            BackColor = Color.FromArgb(235, 238, 241),
            ForeColor = Color.FromArgb(16, 24, 32),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        prompt.AcceptButton = installNow;
        prompt.CancelButton = installLater;
        prompt.Controls.AddRange([updateMessage, installNow, installLater]);
        ControllerTheme.Apply(prompt);
        return prompt.ShowDialog(this) == DialogResult.Yes;
    }

    private void ChangeControllerTheme()
    {
        var mode = _themeSelector.SelectedIndex switch
        {
            1 => ControllerThemeMode.Light,
            2 => ControllerThemeMode.Dark,
            _ => ControllerThemeMode.Auto
        };
        ControllerTheme.SetMode(mode);
        ApplyControllerTheme();
    }

    private void ApplyControllerTheme()
    {
        _lastResolvedDarkMode = ControllerTheme.IsDark;
        ControllerTheme.Apply(this);
        RefreshKioskList();
    }

    private ManagedKiosk? SelectedKiosk()
    {
        var id = SelectedStationId();
        return id is null
            ? null
            : _state.Snapshot().FirstOrDefault(kiosk => kiosk.StationId == id);
    }

    private string? SelectedStationId() =>
        _kioskList.SelectedItems.Count == 0
            ? null
            : _kioskList.SelectedItems[0].Tag as string;

    private void UpdateActionButtons()
    {
        var kiosk = SelectedKiosk();
        var enabled = kiosk is not null;
        _openButton.Enabled = enabled;
        _closeButton.Enabled = enabled;
        _checkUpdateButton.Enabled = enabled;
        _installUpdateButton.Enabled = enabled;
        if (kiosk is not null)
        {
            _selectionStatus.Text =
                $"Selected: {kiosk.StationName} — {(kiosk.IsOnline ? "online" : "offline; commands will wait")}.";
        }
    }

    private static string DescribeCommand(KioskCommand command) => command.Type switch
    {
        CommandTypes.SetClosed when command.Closed == true => "Turn on closed screen",
        CommandTypes.SetClosed => "Open kiosk",
        CommandTypes.SetBusinessClosed when command.Closed == true => "Start business closure",
        CommandTypes.SetBusinessClosed => "End business closure",
        CommandTypes.ResetStart => "Reset to starting page",
        CommandTypes.CheckUpdate => "Check for update",
        CommandTypes.InstallUpdate => "Install update",
        CommandTypes.AcknowledgeAssistance => "Tell guest assistance is on the way",
        _ => command.Type
    };

    private static string FormatLastSeen(DateTime value)
    {
        var age = DateTime.UtcNow - value;
        if (age < TimeSpan.FromSeconds(5)) return "Just now";
        if (age < TimeSpan.FromMinutes(1)) return $"{Math.Max(1, (int)age.TotalSeconds)} sec ago";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        return value.ToLocalTime().ToString("MMM d h:mm tt");
    }

    private string FormatAdvertisementSync(ManagedKiosk kiosk)
    {
        if (string.IsNullOrWhiteSpace(_state.AdvertisementRevision))
            return "Not published";
        if (!string.Equals(
                kiosk.AdvertisementSyncRevision,
                _state.AdvertisementRevision,
                StringComparison.Ordinal))
            return "Pending";
        return kiosk.AdvertisementLastSyncUtc.HasValue
            ? kiosk.AdvertisementLastSyncUtc.Value.ToLocalTime().ToString("MMM d h:mm tt")
            : "Synced";
    }

    private static IEnumerable<string> GetControllerAddresses()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork &&
                              !IPAddress.IsLoopback(address.Address) &&
                              !address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Select(address => $"http://{address.Address}:{ControllerServer.Port}{ControllerServer.BasePath}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (addresses.Count == 0)
            addresses.Add($"http://localhost:{ControllerServer.Port}{ControllerServer.BasePath}");
        return addresses;
    }

    private void CopyText(string value, string message)
    {
        try
        {
            Clipboard.SetText(value);
            _selectionStatus.Text = message;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not copy the value.\n\n" + ex.Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static Button MakeTableButton(string text, Color color) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Margin = new Padding(3, 3, 3, 3),
        BackColor = color,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9, FontStyle.Bold)
    };

    private static void ConfigureSummaryLabel(Label label, string text, int x, Color color)
    {
        label.Text = text;
        label.AutoSize = false;
        label.Bounds = new Rectangle(x, 9, 220, 50);
        label.BackColor = Color.White;
        label.ForeColor = color;
        label.BorderStyle = BorderStyle.FixedSingle;
        label.TextAlign = ContentAlignment.MiddleCenter;
        label.Font = new Font("Segoe UI", 13, FontStyle.Bold);
    }

    private static void ConfigureFlowButton(
        Button button,
        string text,
        Color color,
        int width,
        Color? foreground = null)
    {
        button.Text = text;
        button.Size = new Size(width, 44);
        button.Margin = new Padding(3);
        button.BackColor = color;
        button.ForeColor = foreground ?? Color.FromArgb(16, 24, 32);
        button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Segoe UI", 9, FontStyle.Bold);
    }

    private static Button MakeFlowButton(string text, Color color, int width)
    {
        var button = new Button();
        ConfigureFlowButton(button, text, color, width);
        return button;
    }

    private static void ConfigureTableActionButton(
        Button button,
        string text,
        Color color,
        Color? foreground = null)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(3);
        button.BackColor = color;
        button.ForeColor = foreground ?? Color.FromArgb(16, 24, 32);
        button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
    }
}
