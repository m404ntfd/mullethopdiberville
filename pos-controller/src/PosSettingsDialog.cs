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
    private readonly Label _assignmentStatus = new();
    private List<PosKioskStatus> _knownKiosks = [];
    private List<string> _pendingAssignments = [string.Empty, string.Empty, string.Empty, string.Empty];
    private bool _updatingSlotControls;

    public PosSettings Settings => _working;
    public PosSettings? AppliedSettings { get; private set; }

    public PosSettingsDialog(PosSettings current)
    {
        _working = current.Clone();
        Text = "Mullet Hop POS Controller Staff Menu";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(760, 650);
        ClientSize = new Size(820, 690);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 248, 251);

        var header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.FromArgb(117, 68, 154) };
        header.Controls.Add(new Label
        {
            Text = "POS CONTROLLER STAFF MENU",
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
        PopulateSlots(_working.RememberedKioskStatuses());
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
        var test = MakeButton("Connect & Remember", Color.FromArgb(105, 210, 236), Color.FromArgb(16, 24, 32));
        test.Bounds = new Rectangle(610, 76, 150, 36);
        test.Click += async (_, _) => await LoadKiosksAsync(showSuccess: true);
        _connectionStatus.Text = "A verified controller connection and all kiosk-number assignments are remembered automatically.";
        _connectionStatus.Bounds = new Rectangle(18, 153, 742, 28);
        _connectionStatus.ForeColor = Color.FromArgb(83, 97, 109);
        group.Controls.AddRange([note, _controllerUrl, _pairingKey, view, test, _connectionStatus]);
        return group;
    }

    private GroupBox BuildAssignmentsGroup()
    {
        var group = MakeGroup("Known Machines & Dashboard Assignments", 270);
        group.Dock = DockStyle.Top;
        group.Padding = new Padding(18, 24, 18, 12);
        var note = new Label
        {
            Text = "Choose which known machine appears in each Kiosk 1–4 position. Selecting a machine already in another position moves or swaps it automatically.",
            Bounds = new Rectangle(18, 30, 525, 44),
            ForeColor = Color.FromArgb(52, 65, 76)
        };
        group.Controls.Add(note);
        var saveAssignments = MakeButton(
            "Save Kiosk Assignments",
            Color.FromArgb(118, 196, 66),
            Color.FromArgb(16, 24, 32));
        saveAssignments.Bounds = new Rectangle(558, 30, 192, 42);
        saveAssignments.Click += (_, _) => SaveKioskAssignments();
        group.Controls.Add(saveAssignments);
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
            var changedSlot = index;
            _slots[index].SelectionChangeCommitted += (_, _) =>
                MoveOrSwapKioskAssignment(changedSlot);
            group.Controls.Add(_slots[index]);
        }
        _assignmentStatus.Text = "Assignments remain unchanged until you select Save Kiosk Assignments or Save Settings.";
        _assignmentStatus.Bounds = new Rectangle(18, 230, 732, 28);
        _assignmentStatus.ForeColor = Color.FromArgb(83, 97, 109);
        _assignmentStatus.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        group.Controls.Add(_assignmentStatus);
        return group;
    }

    private GroupBox BuildSecurityGroup()
    {
        var group = MakeGroup("Settings Security", 98);
        group.Dock = DockStyle.Top;
        group.Controls.Add(new Label
        {
            Text = "Ctrl + Alt + M or the Staff Menu button opens this protected menu. Dashboard control buttons do not require a passcode.",
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
            _knownKiosks = MergeWithRememberedKiosks(response.Kiosks);
            CaptureSlotSelections();
            var added = _working.RememberSuccessfulConnection(
                _controllerUrl.Text,
                _pairingKey.Text,
                response.Kiosks);
            AppliedSettings = _working.Clone();
            PopulateSlots(_knownKiosks);
            var assignedIds = _working.KioskSlots
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            var waitingCount = response.Kiosks.Count(kiosk => !assignedIds.Contains(kiosk.StationId));
            SetConnectionStatus(
                added > 0
                    ? $"Connected and remembered — {added} device{(added == 1 ? "" : "s")} added automatically."
                    : waitingCount > 0
                        ? $"Connected and remembered — all four positions are filled; {waitingCount} additional device{(waitingCount == 1 ? " is" : "s are")} available."
                        : $"Connected and remembered — {response.Kiosks.Count} device{(response.Kiosks.Count == 1 ? "" : "s")} loaded.",
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

    private List<PosKioskStatus> MergeWithRememberedKiosks(
        IEnumerable<PosKioskStatus> currentKiosks)
    {
        var merged = _working.RememberedKioskStatuses()
            .ToDictionary(kiosk => kiosk.StationId, StringComparer.Ordinal);
        foreach (var kiosk in currentKiosks)
            merged[kiosk.StationId] = kiosk;
        return merged.Values
            .OrderBy(kiosk => kiosk.StationName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(kiosk => kiosk.MachineName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void CaptureSlotSelections()
    {
        _pendingAssignments = _slots
            .Select(slot => (slot.SelectedItem as KioskChoice)?.Id ?? string.Empty)
            .ToList();
        _working.KioskSlots = [.. _pendingAssignments];
    }

    private void MoveOrSwapKioskAssignment(int targetSlot)
    {
        if (_updatingSlotControls || targetSlot < 0 || targetSlot >= _slots.Length)
            return;

        var selected = (_slots[targetSlot].SelectedItem as KioskChoice)?.Id ?? string.Empty;
        var previous = _pendingAssignments[targetSlot];
        var previousSlot = -1;
        if (!string.IsNullOrWhiteSpace(selected))
        {
            for (var index = 0; index < _pendingAssignments.Count; index++)
            {
                if (index != targetSlot &&
                    string.Equals(_pendingAssignments[index], selected, StringComparison.Ordinal))
                {
                    previousSlot = index;
                    break;
                }
            }
        }

        _pendingAssignments[targetSlot] = selected;
        if (previousSlot >= 0)
            _pendingAssignments[previousSlot] = previous;
        _working.KioskSlots = [.. _pendingAssignments];
        ApplyPendingSelections();

        var selectedName = _knownKiosks.FirstOrDefault(kiosk =>
            string.Equals(kiosk.StationId, selected, StringComparison.Ordinal))?.StationName;
        _assignmentStatus.Text = string.IsNullOrWhiteSpace(selected)
            ? $"Kiosk {targetSlot + 1} will be left unassigned after you save."
            : previousSlot >= 0
                ? $"{selectedName ?? "The selected machine"} was moved to Kiosk {targetSlot + 1}; the two positions were swapped. Select Save to confirm."
                : $"{selectedName ?? "The selected machine"} is now assigned to Kiosk {targetSlot + 1}. Select Save to confirm.";
        _assignmentStatus.ForeColor = Color.FromArgb(143, 91, 10);
    }

    private void SaveKioskAssignments()
    {
        if (!TryGetUniqueSlotAssignments(out var assignments))
            return;

        _working.KioskSlots = assignments;
        try
        {
            _working.Save();
            AppliedSettings = _working.Clone();
            _assignmentStatus.Text = "Assignments saved. The POS dashboard will use these Kiosk 1–4 positions.";
            _assignmentStatus.ForeColor = Color.FromArgb(44, 116, 29);
            MessageBox.Show(this,
                "The Kiosk 1–4 assignments were saved and will remain assigned after the POS Controller restarts.",
                "Kiosk Assignments Saved",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The kiosk assignments could not be saved.\n\n" + ex.Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool TryGetUniqueSlotAssignments(out List<string> assignments)
    {
        CaptureSlotSelections();
        assignments = [.. _pendingAssignments];
        var selectedIds = assignments
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        if (selectedIds.Count == selectedIds.Distinct(StringComparer.Ordinal).Count())
            return true;

        MessageBox.Show(this,
            "Each waiver kiosk can only be assigned to one dashboard number.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private void PopulateSlots(IReadOnlyList<PosKioskStatus> kiosks)
    {
        _knownKiosks = kiosks.ToList();
        _pendingAssignments = _working.KioskSlots
            .Take(_slots.Length)
            .Select(id => id?.Trim() ?? string.Empty)
            .ToList();
        while (_pendingAssignments.Count < _slots.Length)
            _pendingAssignments.Add(string.Empty);

        _updatingSlotControls = true;
        try
        {
            for (var slotIndex = 0; slotIndex < _slots.Length; slotIndex++)
            {
                var selectedId = _pendingAssignments[slotIndex];
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
        finally
        {
            _updatingSlotControls = false;
        }
    }

    private void ApplyPendingSelections()
    {
        _updatingSlotControls = true;
        try
        {
            for (var slotIndex = 0; slotIndex < _slots.Length; slotIndex++)
            {
                var selectedId = _pendingAssignments[slotIndex];
                var matchingChoice = _slots[slotIndex].Items
                    .Cast<KioskChoice>()
                    .FirstOrDefault(choice => choice.Id == selectedId);
                _slots[slotIndex].SelectedIndex = matchingChoice is null
                    ? 0
                    : _slots[slotIndex].Items.IndexOf(matchingChoice);
            }
        }
        finally
        {
            _updatingSlotControls = false;
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

        if (!TryGetUniqueSlotAssignments(out var assignments))
            return;

        _working.ControllerUrl = _controllerUrl.Text.Trim();
        _working.PairingKey = _pairingKey.Text.Trim();
        _working.KioskSlots = assignments;
        try
        {
            _working.Save();
            AppliedSettings = _working.Clone();
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
