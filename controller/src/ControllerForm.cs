using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

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
    private readonly Button _exitButton = new();

    public ControllerForm()
    {
        _server = new ControllerServer(_state);
        Text = "Mullet Hop Kiosk Controller";
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon is not null)
            Icon = appIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1060, 700);
        ClientSize = new Size(1200, 760);
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

        _refreshTimer.Tick += (_, _) => RefreshKioskList();
        Shown += (_, _) => StartControllerService();
        FormClosed += (_, _) =>
        {
            _refreshTimer.Stop();
            _server.Dispose();
        };
    }

    private Panel BuildHeader()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 78,
            BackColor = Color.FromArgb(117, 68, 154)
        };
        var title = new Label
        {
            AutoSize = false,
            Text = "MULLET HOP KIOSK CONTROLLER",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 23, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new Rectangle(24, 8, 690, 58),
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
        panel.Controls.AddRange([title, _serviceStatus]);
        return panel;
    }

    private GroupBox BuildSetupPanel()
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Top,
            Height = 165,
            Padding = new Padding(18, 24, 18, 10),
            Text = "One-Time Kiosk Pairing Information",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            BackColor = Color.White
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
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
            Text = "On each kiosk: Ctrl + Alt + Shift + F12 → Staff Settings → Remote Control Setup. Enter a unique kiosk name, then paste the address and key above.",
            ForeColor = Color.FromArgb(52, 65, 76),
            Font = new Font("Segoe UI", 9.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(3, 3, 3, 0)
        };

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
        group.Controls.Add(layout);
        return group;
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
            Height = 160,
            Padding = new Padding(18, 10, 18, 10),
            BackColor = Color.White
        };
        _selectionStatus.Text = "Select a kiosk above to manage it.";
        _selectionStatus.AutoSize = false;
        _selectionStatus.ForeColor = Color.FromArgb(52, 65, 76);
        _selectionStatus.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _selectionStatus.Bounds = new Rectangle(20, 7, 900, 25);
        _selectionStatus.Anchor = AnchorStyles.Left | AnchorStyles.Top;

        ConfigureActionButton(_openButton, "Open Selected", 20, Color.FromArgb(118, 196, 66), width: 160);
        ConfigureActionButton(_closeButton, "Close Selected", 190, Color.FromArgb(245, 130, 32), width: 160);
        ConfigureActionButton(_checkUpdateButton, "Check Kiosk Update", 360, Color.FromArgb(105, 210, 236), width: 190);
        ConfigureActionButton(_installUpdateButton, "Install Kiosk Update", 560, Color.FromArgb(117, 68, 154), Color.White, 190);
        _openButton.Click += (_, _) => QueueSelected(CommandTypes.SetClosed, false);
        _closeButton.Click += (_, _) => QueueSelected(CommandTypes.SetClosed, true);
        _checkUpdateButton.Click += (_, _) => QueueSelected(CommandTypes.CheckUpdate);
        _installUpdateButton.Click += (_, _) => InstallSelectedUpdate();

        var openAll = MakeActionButton("Open All", 20, 98, Color.FromArgb(210, 239, 190), 160);
        openAll.Click += (_, _) => QueueForAll(CommandTypes.SetClosed, false);
        var closeAll = MakeActionButton("Close All", 190, 98, Color.FromArgb(255, 217, 188), 160);
        closeAll.Click += (_, _) => CloseAllKiosks();

        ConfigureActionButton(
            _controllerUpdateButton,
            "Check Controller Update",
            952,
            Color.FromArgb(8, 119, 189),
            Color.White,
            220);
        _controllerUpdateButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _controllerUpdateButton.Click += async (_, _) => await CheckControllerUpdateAsync();

        ConfigureActionButton(
            _exitButton,
            "Exit Controller",
            952,
            Color.FromArgb(180, 35, 24),
            Color.White,
            220,
            98);
        _exitButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _exitButton.Click += (_, _) => ExitController();

        panel.Controls.AddRange([
            _selectionStatus, _openButton, _closeButton, _checkUpdateButton,
            _installUpdateButton, openAll, closeAll, _controllerUpdateButton, _exitButton]);
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
        _kioskList.Columns.Add("Status", 85);
        _kioskList.Columns.Add("Kiosk Name", 170);
        _kioskList.Columns.Add("PC Name", 145);
        _kioskList.Columns.Add("Version", 80);
        _kioskList.Columns.Add("Guest Screen", 105);
        _kioskList.Columns.Add("Command / Result", 365);
        _kioskList.Columns.Add("Last Seen", 115);
        _kioskList.Columns.Add("IP Address", 125);
        _kioskList.SelectedIndexChanged += (_, _) => UpdateActionButtons();
        _kioskList.DoubleClick += (_, _) =>
        {
            var kiosk = SelectedKiosk();
            if (kiosk is not null)
                QueueSelected(CommandTypes.SetClosed, !kiosk.StationClosed);
        };
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

    private void ShowServiceError(string message)
    {
        _serviceStatus.Text = "● NETWORK SERVICE STOPPED";
        _serviceStatus.BackColor = Color.FromArgb(180, 35, 24);
        ControllerLog.Write(message);
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void RefreshKioskList()
    {
        var selectedId = SelectedStationId();
        var kiosks = _state.Snapshot();
        _kioskList.BeginUpdate();
        try
        {
            _kioskList.Items.Clear();
            foreach (var kiosk in kiosks)
            {
                var commandText = kiosk.PendingCommand is null
                    ? kiosk.LastResult
                    : "PENDING — " + DescribeCommand(kiosk.PendingCommand);
                var item = new ListViewItem(kiosk.IsOnline ? "● Online" : "○ Offline")
                {
                    Tag = kiosk.StationId,
                    ForeColor = kiosk.IsOnline
                        ? Color.FromArgb(37, 103, 24)
                        : Color.FromArgb(125, 55, 48),
                    BackColor = !kiosk.IsOnline
                        ? Color.FromArgb(255, 240, 237)
                        : kiosk.StationClosed
                            ? Color.FromArgb(255, 248, 231)
                            : Color.White
                };
                item.SubItems.Add(kiosk.StationName);
                item.SubItems.Add(kiosk.MachineName);
                item.SubItems.Add(kiosk.Version);
                item.SubItems.Add(kiosk.StationClosed ? "Closed" : "Open");
                item.SubItems.Add(commandText);
                item.SubItems.Add(FormatLastSeen(kiosk.LastSeenUtc));
                item.SubItems.Add(kiosk.LastIpAddress);
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
        _closedSummary.Text = $"{kiosks.Count(kiosk => kiosk.StationClosed)} CLOSED";
        _totalSummary.Text = $"{kiosks.Count} KNOWN KIOSKS";
        UpdateActionButtons();
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

    private async Task CheckControllerUpdateAsync()
    {
        _controllerUpdateButton.Enabled = false;
        var originalText = _controllerUpdateButton.Text;
        _controllerUpdateButton.Text = "Checking…";
        try
        {
            var result = await ControllerUpdater.CheckForUpdateAsync();
            if (IsDisposed)
                return;

            if (result.Status != ControllerUpdateStatus.Available)
            {
                MessageBox.Show(this, result.Message, "Controller Update",
                    MessageBoxButtons.OK,
                    result.Status == ControllerUpdateStatus.Failed
                        ? MessageBoxIcon.Warning
                        : MessageBoxIcon.Information);
                return;
            }

            var answer = MessageBox.Show(this,
                result.Message + "\n\nDownload and install it now? The controller will restart automatically.",
                "Controller Update Available",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
                return;

            _controllerUpdateButton.Text = "Installing…";
            var installResult = await ControllerUpdater.CheckDownloadAndApplyAsync();
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

    private void ExitController()
    {
        var answer = MessageBox.Show(this,
            "Exit the kiosk controller?\n\nKiosks will keep their current state, but remote commands will be unavailable until the controller starts again.",
            "Exit Kiosk Controller",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
            Close();
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
        CommandTypes.CheckUpdate => "Check for update",
        CommandTypes.InstallUpdate => "Install update",
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

    private static void ConfigureActionButton(
        Button button,
        string text,
        int x,
        Color color,
        Color? foreground = null,
        int width = 155,
        int y = 40)
    {
        button.Text = text;
        button.Bounds = new Rectangle(x, y, width, 48);
        button.BackColor = color;
        button.ForeColor = foreground ?? Color.FromArgb(16, 24, 32);
        button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Segoe UI", 10, FontStyle.Bold);
    }

    private static Button MakeActionButton(string text, int x, int y, Color color, int width)
    {
        var button = new Button();
        ConfigureActionButton(button, text, x, color, width: width, y: y);
        return button;
    }
}
