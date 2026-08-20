using System.Net;

namespace MulletHopKioskController;

internal sealed class MasterPriorityDialog : Form
{
    private readonly ControllerState _state;
    private readonly List<DiscoveredControllerPeer> _detected;
    private readonly ListView _list = new();
    private readonly Label _status = new();

    public MasterPriorityDialog(
        ControllerState state,
        IEnumerable<DiscoveredControllerPeer> detected)
    {
        _state = state;
        _detected = detected.Select(peer => peer.Clone()).ToList();
        Text = "Master Controller Priority";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 530);
        MinimumSize = new Size(760, 480);
        Font = new Font("Segoe UI", 10);

        var title = new Label
        {
            Text = "MASTER CONTROLLER PRIORITY",
            Dock = DockStyle.Top,
            Height = 52,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154)
        };
        var explanation = new Label
        {
            Text = "Controllers are identified by their permanent Device ID. If the active master " +
                   "stops responding, the highest listed controller that is reachable becomes master. " +
                   "Changing DHCP addresses does not change this order.",
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(18, 4, 18, 4),
            ForeColor = Color.FromArgb(52, 65, 76)
        };
        var localId = new Label
        {
            Text = $"This PC: {Environment.MachineName}  •  Device ID: {_state.ControllerId}",
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(18, 0, 18, 0),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189)
        };

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.Columns.Add("Priority", 72);
        _list.Columns.Add("Computer", 190);
        _list.Columns.Add("Device ID", 280);
        _list.Columns.Add("Last known address", 230);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(14, 8, 8, 6)
        };
        var addDetected = MakeButton("Add Detected", Color.FromArgb(105, 210, 236));
        var addManual = MakeButton("Add by Device ID", Color.FromArgb(8, 119, 189), Color.White);
        var moveUp = MakeButton("Move Up", Color.FromArgb(210, 239, 190));
        var moveDown = MakeButton("Move Down", Color.FromArgb(210, 239, 190));
        var remove = MakeButton("Remove", Color.FromArgb(255, 217, 188));
        buttons.Controls.AddRange([addDetected, addManual, moveUp, moveDown, remove]);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            ColumnCount = 3,
            Padding = new Padding(14, 8, 14, 8)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        var save = MakeButton("Save", Color.FromArgb(118, 196, 66));
        var cancel = MakeButton("Cancel", Color.FromArgb(235, 238, 241));
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(save, 1, 0);
        footer.Controls.Add(cancel, 2, 0);

        Controls.Add(_list);
        Controls.Add(buttons);
        Controls.Add(footer);
        Controls.Add(localId);
        Controls.Add(explanation);
        Controls.Add(title);

        foreach (var entry in _state.MasterPrioritySnapshot())
            AddEntry(entry);
        RefreshRows();

        var editable = _state.IsMaster;
        addDetected.Enabled = editable;
        addManual.Enabled = editable;
        moveUp.Enabled = editable;
        moveDown.Enabled = editable;
        remove.Enabled = editable;
        save.Enabled = editable;
        _status.Text = editable
            ? "Save this order on the active master; it will sync to every controller."
            : "View only: change this list on the active master Systems Controller.";

        addDetected.Click += (_, _) => AddDetectedControllers();
        addManual.Click += (_, _) => AddManualController();
        moveUp.Click += (_, _) => MoveSelected(-1);
        moveDown.Click += (_, _) => MoveSelected(1);
        remove.Click += (_, _) => RemoveSelected();
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        ControllerTheme.Apply(this);
    }

    private static Button MakeButton(string text, Color backColor, Color? foreColor = null) => new()
    {
        Text = text,
        Width = 138,
        Height = 36,
        BackColor = backColor,
        ForeColor = foreColor ?? Color.FromArgb(16, 24, 32),
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9, FontStyle.Bold),
        Margin = new Padding(4, 0, 4, 0)
    };

    private void AddDetectedControllers()
    {
        var candidates = new List<MasterPriorityEntry>
        {
            new()
            {
                ControllerId = _state.ControllerId,
                MachineName = Environment.MachineName
            }
        };
        candidates.AddRange(_detected.Select(peer => new MasterPriorityEntry
        {
            ControllerId = peer.ControllerId,
            MachineName = peer.MachineName,
            LastKnownAddress = peer.ControllerAddress
        }));
        var added = 0;
        foreach (var candidate in candidates)
        {
            if (Contains(candidate.ControllerId))
                continue;
            AddEntry(candidate);
            added++;
        }
        RefreshRows();
        _status.Text = added == 0
            ? "Every currently detected controller is already listed."
            : $"Added {added} detected controller(s). Arrange them, then select Save.";
    }

    private void AddManualController()
    {
        using var dialog = new ManualMasterCandidateDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (Contains(dialog.Entry.ControllerId))
        {
            _status.Text = "That Device ID is already in the priority list.";
            return;
        }
        AddEntry(dialog.Entry);
        RefreshRows();
    }

    private bool Contains(string controllerId) => _list.Items.Cast<ListViewItem>().Any(item =>
        item.Tag is MasterPriorityEntry entry &&
        string.Equals(entry.ControllerId, controllerId, StringComparison.Ordinal));

    private void AddEntry(MasterPriorityEntry entry)
    {
        var item = new ListViewItem { Tag = entry.Clone() };
        item.SubItems.Add(string.Empty);
        item.SubItems.Add(string.Empty);
        item.SubItems.Add(string.Empty);
        _list.Items.Add(item);
    }

    private void MoveSelected(int direction)
    {
        if (_list.SelectedIndices.Count == 0)
            return;
        var from = _list.SelectedIndices[0];
        var to = from + direction;
        if (to < 0 || to >= _list.Items.Count)
            return;
        var item = _list.Items[from];
        _list.Items.RemoveAt(from);
        _list.Items.Insert(to, item);
        item.Selected = true;
        item.Focused = true;
        RefreshRows();
    }

    private void RemoveSelected()
    {
        if (_list.SelectedItems.Count == 0)
            return;
        _list.Items.Remove(_list.SelectedItems[0]);
        RefreshRows();
    }

    private void RefreshRows()
    {
        for (var index = 0; index < _list.Items.Count; index++)
        {
            var item = _list.Items[index];
            var entry = (MasterPriorityEntry)item.Tag!;
            item.Text = (index + 1).ToString();
            item.SubItems[1].Text = entry.MachineName;
            item.SubItems[2].Text = entry.ControllerId;
            item.SubItems[3].Text = entry.LastKnownAddress;
        }
    }

    private void Save()
    {
        try
        {
            _state.SaveMasterPriority(_list.Items.Cast<ListViewItem>()
                .Select(item => ((MasterPriorityEntry)item.Tag!).Clone()));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }
}

internal sealed class ManualMasterCandidateDialog : Form
{
    private readonly TextBox _deviceId = new();
    private readonly TextBox _machineName = new();
    private readonly TextBox _address = new();
    private readonly Label _error = new();

    public MasterPriorityEntry Entry { get; private set; } = new();

    public ManualMasterCandidateDialog()
    {
        Text = "Add Master Candidate";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(570, 285);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            Padding = new Padding(18)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        AddRow(layout, 0, "Device ID", _deviceId);
        AddRow(layout, 1, "Computer name", _machineName);
        AddRow(layout, 2, "IP or address", _address);
        _error.Dock = DockStyle.Fill;
        _error.ForeColor = Color.FromArgb(196, 28, 28);
        _error.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_error, 0, 3);
        layout.SetColumnSpan(_error, 2);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var add = new Button { Text = "Add", Width = 100, Height = 34, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", Width = 100, Height = 34, DialogResult = DialogResult.Cancel };
        actions.Controls.Add(add);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 4);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = add;
        CancelButton = cancel;
        add.Click += (_, _) => Accept();
        ControllerTheme.Apply(this);
    }

    private static void AddRow(TableLayoutPanel layout, int row, string label, Control field)
    {
        layout.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9, FontStyle.Bold)
        }, 0, row);
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(3, 5, 3, 5);
        layout.Controls.Add(field, 1, row);
    }

    private void Accept()
    {
        var id = _deviceId.Text.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (!Guid.TryParseExact(id, "N", out _))
        {
            _error.Text = "Enter the controller's 32-character Device ID.";
            return;
        }
        var name = _machineName.Text.Trim();
        if (name.Length is < 1 or > 200)
        {
            _error.Text = "Enter the Windows computer name.";
            return;
        }
        var address = NormalizeAddress(_address.Text);
        Entry = new MasterPriorityEntry
        {
            ControllerId = id,
            MachineName = name,
            LastKnownAddress = address
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string NormalizeAddress(string value)
    {
        value = value.Trim();
        if (IPAddress.TryParse(value, out var ip))
            return $"http://{ip}:47832/mullethop/";
        return value;
    }
}
