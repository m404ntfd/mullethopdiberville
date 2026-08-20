namespace MulletHopKioskController;

internal sealed class SystemsUpdatesDialog : Form
{
    private sealed record UpdateTarget(string Type, string Id, bool IsLocal);

    private readonly ControllerState _state;
    private readonly ControllerServer _server;
    private readonly ListView _controllers = new();
    private readonly ListView _posMachines = new();
    private readonly Label _status = new();
    private readonly Button _refresh = new();
    private readonly Button _updateSelected = new();
    private readonly Button _updateAll = new();
    private readonly Button _close = new();
    private bool _busy;

    public SystemsUpdatesDialog(ControllerState state, ControllerServer server)
    {
        _state = state;
        _server = server;
        Text = "Systems & POS Updates";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(980, 650);
        MinimumSize = new Size(820, 560);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 248, 251);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(20, 0, 0, 0),
            Text = "SYSTEMS CONTROLLERS & MULLET HOP POS",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(117, 68, 154)
        };
        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(20, 7, 20, 5),
            Text = "Select one or more computers. Update requests are securely relayed through " +
                   "the master and install after each target's next check-in.",
            ForeColor = Color.FromArgb(52, 65, 76),
            BackColor = Color.White
        };

        ConfigureList(_controllers);
        _controllers.Columns.Add("Systems Controller", 190);
        _controllers.Columns.Add("Version", 90);
        _controllers.Columns.Add("Role", 110);
        _controllers.Columns.Add("Address", 230);
        _controllers.Columns.Add("Last Seen", 150);

        ConfigureList(_posMachines);
        _posMachines.Columns.Add("POS Workstation", 190);
        _posMachines.Columns.Add("Version", 90);
        _posMachines.Columns.Add("Connected Through", 190);
        _posMachines.Columns.Add("Address", 170);
        _posMachines.Columns.Add("Last Seen", 150);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 6) };
        var controllerTab = new TabPage("Systems Controllers") { Padding = new Padding(10) };
        controllerTab.Controls.Add(_controllers);
        var posTab = new TabPage("Mullet Hop POS") { Padding = new Padding(10) };
        posTab.Controls.Add(_posMachines);
        tabs.TabPages.Add(controllerTab);
        tabs.TabPages.Add(posTab);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 78,
            ColumnCount = 5,
            Padding = new Padding(14, 13, 14, 13),
            BackColor = Color.White
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        _status.Dock = DockStyle.Fill;
        _status.Text = "Finding Systems Controllers and POS workstations…";
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.FromArgb(52, 65, 76);
        ConfigureButton(_refresh, "Refresh List", Color.FromArgb(105, 210, 236), Color.Black);
        ConfigureButton(_updateSelected, "Update Selected", Color.FromArgb(117, 68, 154), Color.White);
        ConfigureButton(_updateAll, "Update All", Color.FromArgb(8, 119, 189), Color.White);
        ConfigureButton(_close, "Close", Color.FromArgb(66, 75, 86), Color.White);
        _refresh.Click += async (_, _) => await RefreshSystemsAsync();
        _updateSelected.Click += async (_, _) => await QueueUpdatesAsync(checkedOnly: true);
        _updateAll.Click += async (_, _) => await QueueUpdatesAsync(checkedOnly: false);
        _close.Click += (_, _) => Close();
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_refresh, 1, 0);
        footer.Controls.Add(_updateSelected, 2, 0);
        footer.Controls.Add(_updateAll, 3, 0);
        footer.Controls.Add(_close, 4, 0);

        Controls.Add(tabs);
        Controls.Add(footer);
        Controls.Add(note);
        Controls.Add(title);
        Shown += async (_, _) => await RefreshSystemsAsync();
    }

    private static void ConfigureList(ListView list)
    {
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.GridLines = true;
        list.CheckBoxes = true;
        list.HideSelection = false;
        list.BackColor = Color.White;
    }

    private static void ConfigureButton(
        Button button,
        string text,
        Color background,
        Color foreground)
    {
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(4, 0, 4, 0);
        button.Text = text;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Segoe UI", 9, FontStyle.Bold);
    }

    private async Task RefreshSystemsAsync()
    {
        if (_busy)
            return;
        SetBusy(true, "Scanning the private network for Systems Controllers…");
        try
        {
            await _server.Peers.ScanNowAsync();
            PopulateLists();
            _status.Text = $"{_controllers.Items.Count} controller(s) • " +
                           $"{_posMachines.Items.Count} POS workstation(s)";
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Systems update list refresh error: " + ex.Message);
            _status.Text = "The system list could not be refreshed. Check the private network.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PopulateLists()
    {
        _controllers.BeginUpdate();
        _posMachines.BeginUpdate();
        try
        {
            _controllers.Items.Clear();
            _posMachines.Items.Clear();

            AddController(
                Environment.MachineName,
                ControllerUpdater.CurrentVersion,
                _state.IsMaster ? "Master (This PC)" : "This PC",
                "Local computer",
                DateTime.UtcNow,
                _state.ControllerId,
                isLocal: true);

            var peers = _server.Peers.Snapshot();
            foreach (var peer in peers)
            {
                AddController(
                    peer.MachineName,
                    peer.Version,
                    peer.IsMaster ? "Master" : "Controller",
                    peer.ControllerAddress,
                    peer.LastSeenUtc,
                    peer.ControllerId,
                    isLocal: false);
            }

            var posTargets = new Dictionary<string, (ControllerPosPresence Machine, string Owner)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var machine in _state.ActivePosMachinesSnapshot())
            {
                posTargets[machine.MachineName] = (
                    new ControllerPosPresence
                    {
                        MachineName = machine.MachineName,
                        IpAddress = machine.IpAddress,
                        Version = machine.Version,
                        LastSeenUtc = machine.LastSeenUtc
                    },
                    Environment.MachineName + " (This PC)");
            }
            foreach (var peer in peers)
            {
                foreach (var machine in peer.PosMachines)
                    posTargets.TryAdd(machine.MachineName, (machine, peer.MachineName));
            }
            foreach (var target in posTargets.Values
                         .OrderBy(target => target.Machine.MachineName,
                             StringComparer.CurrentCultureIgnoreCase))
            {
                var machine = target.Machine;
                var item = new ListViewItem(machine.MachineName);
                item.SubItems.Add(string.IsNullOrWhiteSpace(machine.Version) ? "Unknown" : machine.Version);
                item.SubItems.Add(target.Owner);
                item.SubItems.Add(string.IsNullOrWhiteSpace(machine.IpAddress) ? "Unknown" : machine.IpAddress);
                item.SubItems.Add(FormatLastSeen(machine.LastSeenUtc));
                item.Tag = new UpdateTarget("pos", machine.MachineName, false);
                _posMachines.Items.Add(item);
            }
        }
        finally
        {
            _controllers.EndUpdate();
            _posMachines.EndUpdate();
        }
    }

    private void AddController(
        string machineName,
        string version,
        string role,
        string address,
        DateTime lastSeenUtc,
        string controllerId,
        bool isLocal)
    {
        var item = new ListViewItem(machineName);
        item.SubItems.Add(version);
        item.SubItems.Add(role);
        item.SubItems.Add(address);
        item.SubItems.Add(FormatLastSeen(lastSeenUtc));
        item.Tag = new UpdateTarget("controller", controllerId, isLocal);
        _controllers.Items.Add(item);
    }

    private async Task QueueUpdatesAsync(bool checkedOnly)
    {
        if (_busy)
            return;
        var items = _controllers.Items.Cast<ListViewItem>()
            .Concat(_posMachines.Items.Cast<ListViewItem>())
            .Where(item => !checkedOnly || item.Checked)
            .Where(item => item.Tag is UpdateTarget)
            .OrderBy(item => ((UpdateTarget)item.Tag!).IsLocal)
            .ToList();
        if (items.Count == 0)
        {
            _status.Text = "Check one or more computers before selecting Update Selected.";
            return;
        }

        SetBusy(true, $"Sending {items.Count} update request(s)…");
        var accepted = 0;
        var failures = new List<string>();
        try
        {
            foreach (var item in items)
            {
                var target = (UpdateTarget)item.Tag!;
                var result = await _server.QueueSoftwareUpdateAsync(target.Type, target.Id);
                if (result.Accepted)
                    accepted++;
                else
                    failures.Add(item.Text + ": " + result.Message);
            }

            _status.Text = failures.Count == 0
                ? $"Update requested on {accepted} computer(s)."
                : $"{accepted} update request(s) accepted; {failures.Count} failed.";
            if (failures.Count > 0)
            {
                MessageBox.Show(this,
                    string.Join(Environment.NewLine, failures),
                    "Update Requests",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        _refresh.Enabled = !busy;
        _updateSelected.Enabled = !busy;
        _updateAll.Enabled = !busy;
        _close.Enabled = !busy;
        if (!string.IsNullOrWhiteSpace(status))
            _status.Text = status;
    }

    private static string FormatLastSeen(DateTime value) =>
        value == default ? "Recently" : value.ToLocalTime().ToString("g");
}
