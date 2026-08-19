namespace MulletHopPosController;

internal sealed class PosSettingsDialog : Form
{
    private sealed record KioskChoice(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly PosSettings _working;
    private readonly TextBox _controllerUrl = new();
    private readonly TextBox _pairingKey = new();
    private readonly ComboBox[] _slots = [new(), new(), new(), new()];
    private readonly Label _connectionStatus = new();
    private List<PosKioskStatus> _knownKiosks = [];

    public PosSettings Settings => _working;

    public PosSettingsDialog(PosSettings current)
    {
        _working = current.Clone();
        Text = "Mullet Hop POS Controller Settings";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 650);
        ClientSize = new Size(820, 690);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 248, 251);

        var header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.FromArgb(117, 68, 154) };
        header.Controls.Add(new Label
        {
            Text = "POS CONTROLLER SETTINGS",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 21, FontStyle.Bold),
            Bounds = new Rectangle(24, 10, 600, 52),
            TextAlign = ContentAlignment.MiddleLeft
        });

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 72, BackColor = Color.White };
        var save = MakeButton("Save Settings", Color.FromArgb(117, 68, 154), Color.White);
        save.Bounds = new Rectangle(520, 15, 145, 42);
        save.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        save.Click += (_, _) => SaveAndClose();
        var cancel = MakeButton("Cancel", Color.FromArgb(225, 231, 236), Color.FromArgb(16, 24, 32));
        cancel.Bounds = new Rectangle(675, 15, 110, 42);
        cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.AddRange([save, cancel]);

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(24),
            BackColor = Color.FromArgb(244, 248, 251)
        };
        var connection = BuildConnectionGroup();
        var assignments = BuildAssignmentsGroup();
        var security = BuildSecurityGroup();
        content.Controls.Add(security);
        content.Controls.Add(assignments);
        content.Controls.Add(connection);
        Controls.Add(content);
        Controls.Add(footer);
        Controls.Add(header);
        AcceptButton = save;
        CancelButton = cancel;

        _controllerUrl.Text = _working.ControllerUrl;
        _pairingKey.Text = _working.PairingKey;
        PopulateSlots([]);
        Shown += async (_, _) =>
        {
            if (_working.HasConnectionSettings)
                await LoadKiosksAsync(showSuccess: false);
        };
    }

    private GroupBox BuildConnectionGroup()
    {
        var group = MakeGroup("Kiosk Controller Connection", 188);
        group.Dock = DockStyle.Top;
        var note = new Label
        {
            Text = "Use the same controller address and pairing key shown in the Mullet Hop Kiosk Controller. The POS app remains a separate program.",
            Bounds = new Rectangle(18, 30, 710, 42),
            ForeColor = Color.FromArgb(52, 65, 76)
        };
        group.Controls.Add(new Label { Text = "Controller address:", Bounds = new Rectangle(18, 82, 150, 28) });
        _controllerUrl.Bounds = new Rectangle(170, 78, 430, 32);
        group.Controls.Add(new Label { Text = "Pairing key:", Bounds = new Rectangle(18, 122, 150, 28) });
        _pairingKey.Bounds = new Rectangle(170, 118, 430, 32);
        _pairingKey.UseSystemPasswordChar = true;
        var view = MakeButton("View", Color.FromArgb(255, 217, 188), Color.FromArgb(16, 24, 32));
        view.Bounds = new Rectangle(610, 118, 80, 32);
        view.Click += (_, _) =>
        {
            _pairingKey.UseSystemPasswordChar = !_pairingKey.UseSystemPasswordChar;
            view.Text = _pairingKey.UseSystemPasswordChar ? "View" : "Hide";
        };
        var test = MakeButton("Pull Devices Now", Color.FromArgb(105, 210, 236), Color.FromArgb(16, 24, 32));
        test.Bounds = new Rectangle(610, 76, 150, 36);
        test.Click += async (_, _) => await LoadKiosksAsync(showSuccess: true);
        _connectionStatus.Text = "Devices are pulled from the Kiosk Controller and added to open dashboard positions.";
        _connectionStatus.Bounds = new Rectangle(18, 153, 742, 28);
        _connectionStatus.ForeColor = Color.FromArgb(83, 97, 109);
        group.Controls.AddRange([note, _controllerUrl, _pairingKey, view, test, _connectionStatus]);
        return group;
    }

    private GroupBox BuildAssignmentsGroup()
    {
        var group = MakeGroup("Dashboard Kiosk Assignments", 238);
        group.Dock = DockStyle.Top;
        group.Padding = new Padding(18, 24, 18, 12);
        var note = new Label
        {
            Text = "Paired devices are added automatically to the next open position. You can change their Kiosk 1–4 assignments below.",
            Bounds = new Rectangle(18, 30, 720, 44),
            ForeColor = Color.FromArgb(52, 65, 76)
        };
        group.Controls.Add(note);
        for (var index = 0; index < _slots.Length; index++)
        {
            var y = 80 + index * 37;
            group.Controls.Add(new Label
            {
                Text = $"Kiosk {index + 1}:",
                Bounds = new Rectangle(48, y + 3, 105, 28),
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            });
            _slots[index].Bounds = new Rectangle(155, y, 500, 32);
            _slots[index].DropDownStyle = ComboBoxStyle.DropDownList;
            group.Controls.Add(_slots[index]);
        }
        return group;
    }

    private GroupBox BuildSecurityGroup()
    {
        var group = MakeGroup("Settings Security", 98);
        group.Dock = DockStyle.Top;
        group.Controls.Add(new Label
        {
            Text = "A passcode is required for Settings. Dashboard control buttons do not require a passcode.",
            Bounds = new Rectangle(18, 33, 545, 45),
            ForeColor = Color.FromArgb(52, 65, 76)
        });
        var change = MakeButton("Change Passcode", Color.FromArgb(255, 217, 188), Color.FromArgb(16, 24, 32));
        change.Bounds = new Rectangle(590, 35, 160, 40);
        change.Click += (_, _) => ChangePasscode();
        group.Controls.Add(change);
        return group;
    }

    private async Task LoadKiosksAsync(bool showSuccess)
    {
        if (!PosControllerClient.IsConfigurationValid(
                _controllerUrl.Text, _pairingKey.Text, out var error))
        {
            SetConnectionStatus(error, false);
            return;
        }

        SetConnectionStatus("Connecting to the Kiosk Controller…", true);
        try
        {
            var client = new PosControllerClient(_controllerUrl.Text, _pairingKey.Text);
            var response = await client.GetStatusAsync();
            _knownKiosks = response.Kiosks;
            CaptureSlotSelections();
            var added = _working.AutoAssignKiosks(_knownKiosks);
            PopulateSlots(_knownKiosks);
            var assignedCount = _working.KioskSlots.Count(id => !string.IsNullOrWhiteSpace(id));
            var waitingCount = Math.Max(0, _knownKiosks.Count - assignedCount);
            SetConnectionStatus(
                added > 0
                    ? $"Connected — {added} device{(added == 1 ? "" : "s")} added automatically."
                    : waitingCount > 0
                        ? $"Connected — all four positions are filled; {waitingCount} additional device{(waitingCount == 1 ? " is" : "s are")} available."
                        : $"Connected — {_knownKiosks.Count} device{(_knownKiosks.Count == 1 ? "" : "s")} loaded.",
                true);
            if (showSuccess && _knownKiosks.Count == 0)
                MessageBox.Show(this,
                    "The controller was reached, but no waiver kiosks are paired with it yet.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetConnectionStatus("Connection failed — " + ex.Message, false);
        }
    }

    private void CaptureSlotSelections()
    {
        _working.KioskSlots = _slots
            .Select(slot => (slot.SelectedItem as KioskChoice)?.Id ?? string.Empty)
            .ToList();
    }

    private void PopulateSlots(IReadOnlyList<PosKioskStatus> kiosks)
    {
        for (var slotIndex = 0; slotIndex < _slots.Length; slotIndex++)
        {
            var selectedId = _working.KioskSlots[slotIndex];
            if (_slots[slotIndex].SelectedItem is KioskChoice current)
                selectedId = current.Id;
            _slots[slotIndex].Items.Clear();
            _slots[slotIndex].Items.Add(new KioskChoice(string.Empty, "Not linked"));
            foreach (var kiosk in kiosks)
                _slots[slotIndex].Items.Add(new KioskChoice(
                    kiosk.StationId,
                    $"{kiosk.StationName} ({kiosk.MachineName})"));
            if (!string.IsNullOrWhiteSpace(selectedId) &&
                !kiosks.Any(kiosk => kiosk.StationId == selectedId))
                _slots[slotIndex].Items.Add(new KioskChoice(
                    selectedId,
                    "Previously linked kiosk (currently unavailable)"));
            var matchingChoice = _slots[slotIndex].Items
                .Cast<KioskChoice>()
                .FirstOrDefault(choice => choice.Id == selectedId);
            _slots[slotIndex].SelectedIndex = matchingChoice is null
                ? 0
                : _slots[slotIndex].Items.IndexOf(matchingChoice);
        }
    }

    private void SaveAndClose()
    {
        if (!PosControllerClient.IsConfigurationValid(
                _controllerUrl.Text, _pairingKey.Text, out var error))
        {
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var selectedIds = _slots
            .Select(slot => (slot.SelectedItem as KioskChoice)?.Id ?? string.Empty)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        if (selectedIds.Count != selectedIds.Distinct(StringComparer.Ordinal).Count())
        {
            MessageBox.Show(this,
                "Each waiver kiosk can only be assigned to one dashboard number.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _working.ControllerUrl = _controllerUrl.Text.Trim();
        _working.PairingKey = _pairingKey.Text.Trim();
        CaptureSlotSelections();
        try
        {
            _working.Save();
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Settings could not be saved.\n\n" + ex.Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ChangePasscode()
    {
        using var dialog = new ChangePinDialog(_working);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        _working.SetPin(dialog.NewPin);
        MessageBox.Show(this, "The Settings passcode will be updated when you save.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SetConnectionStatus(string text, bool success)
    {
        _connectionStatus.Text = text;
        _connectionStatus.ForeColor = success
            ? Color.FromArgb(44, 116, 29)
            : Color.FromArgb(187, 34, 46);
    }

    private static GroupBox MakeGroup(string text, int height) => new()
    {
        Text = text,
        Height = height,
        BackColor = Color.White,
        ForeColor = Color.FromArgb(8, 119, 189),
        Font = new Font("Segoe UI", 11, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 14)
    };

    private static Button MakeButton(string text, Color background, Color foreground) => new()
    {
        Text = text,
        BackColor = background,
        ForeColor = foreground,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
        Cursor = Cursors.Hand
    };
}
