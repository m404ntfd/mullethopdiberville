namespace MulletHop.Shared;

internal sealed class WristbandColorSettingsDialog : Form
{
    private sealed record DayChoice(DayOfWeek Day)
    {
        public override string ToString() => Day.ToString();
    }

    private sealed record ColorChoice(string Id, string Display)
    {
        public override string ToString() => Display;
    }

    private static readonly DayOfWeek[] OrderedDays =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday,
        DayOfWeek.Sunday
    ];

    private readonly WristbandSettingsPackage _working;
    private readonly ComboBox _dayPicker = new();
    private readonly DataGridView _scheduleGrid = new();
    private readonly Label _scheduleStatus = new();
    private readonly TabControl _rightTabs = new();
    private readonly ListBox _colorList = new();
    private Button _editColor = null!;
    private Button _removeColor = null!;
    private Button _makeActive = null!;
    private Button _makeInactive = null!;
    private readonly Dictionary<string, ComboBox> _printerChoices =
        new(StringComparer.OrdinalIgnoreCase);
    private DayOfWeek? _loadedDay;
    private bool _loading;

    public WristbandColorSettingsDialog(WristbandSettingsPackage current)
    {
        _working = current.Clone();
        _working.Normalize();
        Text = "Wristband Colors and Jump Times";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1000, 680);
        ClientSize = new Size(1120, 760);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 248, 251);

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 82,
            BackColor = Color.FromArgb(117, 68, 154)
        };
        header.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 0, 24, 0),
            Text = "WRISTBAND COLORS AND JUMP TIMES",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 19, FontStyle.Bold)
        });

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 76,
            Padding = new Padding(0, 15, 22, 15),
            BackColor = Color.White
        };
        var save = MakeButton("Save Wristband Settings", Color.FromArgb(118, 196, 66));
        save.Dock = DockStyle.Right;
        save.Width = 210;
        save.Click += (_, _) => SaveAndClose();
        var cancel = MakeButton("Cancel", Color.FromArgb(98, 107, 117), Color.White);
        cancel.Dock = DockStyle.Right;
        cancel.Width = 120;
        cancel.Margin = new Padding(10, 0, 0, 0);
        cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(cancel);
        footer.Controls.Add(save);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(18),
            BackColor = BackColor
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        body.Controls.Add(BuildSchedulePanel(), 0, 0);
        body.Controls.Add(BuildRightPanel(), 1, 0);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);
        AcceptButton = save;
        CancelButton = cancel;

        _dayPicker.Items.AddRange(OrderedDays.Select(day => new DayChoice(day)).ToArray());
        _dayPicker.SelectedIndexChanged += (_, _) => ChangeSelectedDay();
        RefreshColorChoices();
        RefreshColorList();
        _dayPicker.SelectedIndex = Math.Max(0, Array.IndexOf(OrderedDays, DateTime.Today.DayOfWeek));
    }

    public WristbandSettingsPackage Settings => _working.Clone();

    internal static string FormatSlotForSmokeTest(int startMinute) => FormatSlot(startMinute);

    private Control BuildSchedulePanel()
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 9, 0),
            Padding = new Padding(14, 18, 14, 14),
            Text = "Daily Wristband Schedule",
            ForeColor = Color.FromArgb(8, 119, 189),
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };

        var top = new Panel { Dock = DockStyle.Top, Height = 92 };
        top.Controls.Add(new Label
        {
            Text = "Day:",
            Bounds = new Rectangle(4, 8, 54, 28),
            ForeColor = Color.FromArgb(37, 48, 58)
        });
        _dayPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _dayPicker.Bounds = new Rectangle(62, 4, 185, 32);
        top.Controls.Add(_dayPicker);
        var manage = MakeButton("Show Color List Controls", Color.FromArgb(245, 130, 32));
        manage.Bounds = new Rectangle(280, 3, 240, 35);
        manage.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        manage.Click += (_, _) => ManageColors();
        top.Controls.Add(manage);
        _scheduleStatus.Bounds = new Rectangle(4, 48, 470, 40);
        _scheduleStatus.AutoEllipsis = true;
        _scheduleStatus.ForeColor = Color.FromArgb(83, 97, 109);
        _scheduleStatus.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        top.Controls.Add(_scheduleStatus);

        _scheduleGrid.Dock = DockStyle.Fill;
        _scheduleGrid.AllowUserToAddRows = false;
        _scheduleGrid.AllowUserToDeleteRows = false;
        _scheduleGrid.AllowUserToResizeRows = false;
        _scheduleGrid.RowHeadersVisible = false;
        _scheduleGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _scheduleGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _scheduleGrid.BackgroundColor = Color.White;
        _scheduleGrid.BorderStyle = BorderStyle.FixedSingle;
        _scheduleGrid.RowTemplate.Height = 32;
        _scheduleGrid.DataError += (_, _) => { };
        _scheduleGrid.CellValueChanged += (_, e) =>
        {
            if (!_loading && e.RowIndex >= 0 && e.ColumnIndex == 1)
                ApplyScheduleCellColor(_scheduleGrid.Rows[e.RowIndex].Cells[1]);
        };
        _scheduleGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_scheduleGrid.IsCurrentCellDirty)
                _scheduleGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _scheduleGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "JumpTime",
            HeaderText = "One-Hour Jump Time",
            ReadOnly = true,
            FillWeight = 58
        });
        _scheduleGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "Color",
            HeaderText = "Wristband Color",
            DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            FlatStyle = FlatStyle.Flat,
            FillWeight = 42
        });

        group.Controls.Add(_scheduleGrid);
        group.Controls.Add(top);
        return group;
    }

    private Control BuildRightPanel()
    {
        _rightTabs.Dock = DockStyle.Fill;
        _rightTabs.Margin = new Padding(9, 0, 0, 0);
        _rightTabs.Font = new Font("Segoe UI", 10, FontStyle.Bold);

        var colorsTab = new TabPage("COLOR LIST")
        {
            BackColor = Color.FromArgb(244, 248, 251),
            Padding = new Padding(10)
        };
        colorsTab.Controls.Add(BuildColorListPanel());

        var printersTab = new TabPage("PRINTER COLORS")
        {
            BackColor = Color.FromArgb(244, 248, 251),
            Padding = new Padding(4)
        };
        printersTab.Controls.Add(BuildPrinterPanel());
        _rightTabs.TabPages.Add(colorsTab);
        _rightTabs.TabPages.Add(printersTab);
        _rightTabs.SelectedIndex = 0;
        return _rightTabs;
    }

    private Control BuildColorListPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var explanation = new Label
        {
            Dock = DockStyle.Top,
            Height = 70,
            Text = "Add or remove wristband colors here. Inactive colors remain on saved schedules but cannot be newly assigned.",
            ForeColor = Color.FromArgb(52, 65, 76),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
        };

        _colorList.Dock = DockStyle.Fill;
        _colorList.DrawMode = DrawMode.OwnerDrawFixed;
        _colorList.ItemHeight = 42;
        _colorList.IntegralHeight = false;
        _colorList.DrawItem += DrawColorListItem;
        _colorList.DoubleClick += (_, _) => EditSelectedColor();
        _colorList.SelectedIndexChanged += (_, _) => UpdateColorActionButtons();

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 112,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(0, 8, 0, 0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var add = MakeButton("Add Color", Color.FromArgb(118, 196, 66));
        _editColor = MakeButton("Edit Color", Color.FromArgb(105, 210, 236));
        _removeColor = MakeButton("Remove Color", Color.FromArgb(187, 34, 46), Color.White);
        _makeActive = MakeButton("Make Active", Color.FromArgb(245, 130, 32));
        _makeInactive = MakeButton("Make Inactive", Color.FromArgb(98, 107, 117), Color.White);
        foreach (var button in new[] { add, _editColor, _removeColor, _makeActive, _makeInactive })
        {
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(4);
        }
        add.Click += (_, _) => AddColor();
        _editColor.Click += (_, _) => EditSelectedColor();
        _removeColor.Click += (_, _) => RemoveSelectedColor();
        _makeActive.Click += (_, _) => SetSelectedColorActive(true);
        _makeInactive.Click += (_, _) => SetSelectedColorActive(false);
        actions.Controls.Add(add, 0, 0);
        actions.Controls.Add(_editColor, 1, 0);
        actions.Controls.Add(_removeColor, 2, 0);
        actions.Controls.Add(_makeActive, 0, 1);
        actions.SetColumnSpan(_makeActive, 1);
        actions.Controls.Add(_makeInactive, 1, 1);
        actions.SetColumnSpan(_makeInactive, 2);

        panel.Controls.Add(_colorList);
        panel.Controls.Add(actions);
        panel.Controls.Add(explanation);
        return panel;
    }

    private Control BuildPrinterPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(9, 0, 0, 0)
        };
        var printerGroup = new GroupBox
        {
            Dock = DockStyle.Top,
            Height = 385,
            Padding = new Padding(16, 20, 16, 14),
            Text = "Current Color Loaded in Each Printer",
            ForeColor = Color.FromArgb(117, 68, 154),
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };
        printerGroup.Controls.Add(new Label
        {
            Text = "These names appear on the WB-1 through WB-7 buttons when a wristband is printed.",
            Bounds = new Rectangle(18, 30, 300, 48),
            ForeColor = Color.FromArgb(52, 65, 76),
            Font = new Font("Segoe UI", 9, FontStyle.Regular)
        });
        for (var index = 0; index < WristbandSettingsPackage.ExpectedPrinterNames.Count; index++)
        {
            var printerName = WristbandSettingsPackage.ExpectedPrinterNames[index];
            var y = 82 + index * 41;
            printerGroup.Controls.Add(new Label
            {
                Text = printerName + ":",
                Bounds = new Rectangle(20, y + 5, 62, 28),
                ForeColor = Color.FromArgb(37, 48, 58),
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            });
            var choice = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Bounds = new Rectangle(84, y, 225, 32),
                Tag = printerName
            };
            choice.SelectedIndexChanged += (_, _) => ApplyComboColor(choice);
            _printerChoices[printerName] = choice;
            printerGroup.Controls.Add(choice);
        }

        var explanation = new GroupBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 24, 18, 12),
            Text = "How the Schedule Works",
            ForeColor = Color.FromArgb(8, 119, 189),
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };
        explanation.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "Each row is a one-hour jump window. New windows begin every 30 minutes. " +
                   "The first row uses that day's opening time and the final row is the last full-hour jump that ends at closing. A separate half-hour sale can still end at closing.\n\n" +
                   "Use the Color List tab to add or remove colors and to mark a color active or inactive.",
            ForeColor = Color.FromArgb(52, 65, 76),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
        });

        panel.Controls.Add(explanation);
        panel.Controls.Add(printerGroup);
        return panel;
    }

    private void ChangeSelectedDay()
    {
        if (_loading || _dayPicker.SelectedItem is not DayChoice selected)
            return;
        CaptureLoadedDay();
        LoadDay(selected.Day);
    }

    private void LoadDay(DayOfWeek day)
    {
        _loading = true;
        try
        {
            _loadedDay = day;
            _scheduleGrid.Rows.Clear();
            var window = _working.BusinessDays.FirstOrDefault(value => value.Day == (int)day);
            if (window is null)
            {
                _scheduleStatus.Text =
                    "Business Hours have not been received. Save Business Hours in the Systems Controller first.";
                return;
            }
            if (!window.IsOpen)
            {
                _scheduleStatus.Text = day + " is closed in Business Hours.";
                return;
            }

            var slots = _working.SlotsFor(day);
            _scheduleStatus.Text =
                $"{slots.Count} one-hour jump window{(slots.Count == 1 ? "" : "s")}; " +
                "new windows begin every 30 minutes.";
            foreach (var slot in slots)
            {
                var rowIndex = _scheduleGrid.Rows.Add(
                    FormatSlot(slot.StartMinute),
                    slot.ColorId);
                _scheduleGrid.Rows[rowIndex].Tag = slot.StartMinute;
                ApplyScheduleCellColor(_scheduleGrid.Rows[rowIndex].Cells[1]);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void CaptureLoadedDay()
    {
        if (!_loadedDay.HasValue)
            return;
        var schedule = _working.Days.First(day => day.Day == (int)_loadedDay.Value);
        schedule.Slots = _scheduleGrid.Rows
            .Cast<DataGridViewRow>()
            .Where(row => row.Tag is int)
            .Select(row => new WristbandTimeColorAssignment
            {
                StartMinute = (int)row.Tag!,
                ColorId = row.Cells[1].Value?.ToString() ?? string.Empty
            })
            .ToList();
    }

    private void RefreshColorChoices()
    {
        CaptureLoadedDay();
        _working.Normalize();
        var choices = BuildColorChoices();
        _loading = true;
        try
        {
            if (_scheduleGrid.Columns[1] is DataGridViewComboBoxColumn colorColumn)
            {
                colorColumn.DataSource = choices;
                colorColumn.DisplayMember = nameof(ColorChoice.Display);
                colorColumn.ValueMember = nameof(ColorChoice.Id);
            }

            foreach (var pair in _printerChoices)
            {
                var selectedColor = _working.Printers.First(printer =>
                    string.Equals(printer.PrinterName, pair.Key, StringComparison.OrdinalIgnoreCase)).ColorId;
                pair.Value.Items.Clear();
                pair.Value.Items.AddRange(choices.ToArray());
                pair.Value.SelectedItem = pair.Value.Items.Cast<ColorChoice>()
                    .FirstOrDefault(choice => string.Equals(
                        choice.Id,
                        selectedColor,
                        StringComparison.Ordinal));
                if (pair.Value.SelectedIndex < 0)
                    pair.Value.SelectedIndex = 0;
                ApplyComboColor(pair.Value);
            }
        }
        finally
        {
            _loading = false;
        }

        if (_dayPicker.SelectedItem is DayChoice selected)
            LoadDay(selected.Day);
    }

    private List<ColorChoice> BuildColorChoices()
    {
        var choices = new List<ColorChoice> { new(string.Empty, "Not assigned") };
        var assignedColorIds = _working.Printers.Select(printer => printer.ColorId)
            .Concat(_working.Days.SelectMany(day => day.Slots).Select(slot => slot.ColorId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        choices.AddRange(_working.Colors
            .Where(color => color.IsActive || assignedColorIds.Contains(color.Id))
            .OrderByDescending(color => color.IsActive)
            .ThenBy(color => color.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(color => new ColorChoice(
                color.Id,
                color.IsActive ? color.Name : color.Name + " (Inactive)")));
        return choices;
    }

    private void ManageColors()
    {
        _rightTabs.SelectedIndex = 0;
        _colorList.Focus();
    }

    private WristbandColorDefinition? SelectedColor =>
        _colorList.SelectedItem as WristbandColorDefinition;

    private void AddColor()
    {
        using var dialog = new WristbandColorEditDialog(null);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (_working.Colors.Any(color => string.Equals(
                color.Name,
                dialog.ColorDefinition.Name,
                StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A wristband color with that name already exists.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        CaptureLoadedDay();
        CapturePrinterAssignments();
        _working.Colors.Add(dialog.ColorDefinition);
        RefreshColorChoices();
        RefreshColorList(dialog.ColorDefinition.Id);
    }

    private void EditSelectedColor()
    {
        var selected = SelectedColor;
        if (selected is null)
            return;
        using var dialog = new WristbandColorEditDialog(selected);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (_working.Colors.Any(color => !ReferenceEquals(color, selected) && string.Equals(
                color.Name,
                dialog.ColorDefinition.Name,
                StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A wristband color with that name already exists.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        selected.Name = dialog.ColorDefinition.Name;
        selected.HexColor = dialog.ColorDefinition.HexColor;
        selected.IsActive = dialog.ColorDefinition.IsActive;
        RefreshColorChoices();
        RefreshColorList(selected.Id);
    }

    private void RemoveSelectedColor()
    {
        var selected = SelectedColor;
        if (selected is null)
            return;
        if (MessageBox.Show(
                this,
                $"Remove {selected.Name}? It will also be removed from saved jump-time and printer assignments.",
                "Remove Wristband Color",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }
        CaptureLoadedDay();
        CapturePrinterAssignments();
        _working.Colors.Remove(selected);
        RefreshColorChoices();
        RefreshColorList();
    }

    private void SetSelectedColorActive(bool active)
    {
        var selected = SelectedColor;
        if (selected is null)
            return;
        selected.IsActive = active;
        RefreshColorChoices();
        RefreshColorList(selected.Id);
    }

    private void RefreshColorList(string? selectedId = null)
    {
        _colorList.BeginUpdate();
        _colorList.Items.Clear();
        foreach (var color in _working.Colors
                     .OrderByDescending(color => color.IsActive)
                     .ThenBy(color => color.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _colorList.Items.Add(color);
        }
        _colorList.EndUpdate();
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            _colorList.SelectedItem = _colorList.Items.Cast<WristbandColorDefinition>()
                .FirstOrDefault(color => color.Id == selectedId);
        }
        else if (_colorList.Items.Count > 0)
        {
            _colorList.SelectedIndex = 0;
        }
        UpdateColorActionButtons();
        _colorList.Invalidate();
    }

    private void UpdateColorActionButtons()
    {
        var selected = SelectedColor;
        _editColor.Enabled = selected is not null;
        _removeColor.Enabled = selected is not null;
        _makeActive.Enabled = selected is not null && !selected.IsActive;
        _makeInactive.Enabled = selected is not null && selected.IsActive;
    }

    private void DrawColorListItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _colorList.Items.Count)
            return;
        var color = (WristbandColorDefinition)_colorList.Items[e.Index];
        var swatch = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 7, 48, e.Bounds.Height - 14);
        using var brush = new SolidBrush(ParseColor(color.HexColor));
        e.Graphics.FillRectangle(brush, swatch);
        e.Graphics.DrawRectangle(Pens.DimGray, swatch);
        var status = color.IsActive ? "ACTIVE" : "INACTIVE";
        var textColor = color.IsActive ? e.ForeColor : Color.FromArgb(130, 136, 142);
        TextRenderer.DrawText(
            e.Graphics,
            $"{color.Name}   •   {status}",
            e.Font ?? Font,
            new Rectangle(e.Bounds.X + 68, e.Bounds.Y, e.Bounds.Width - 74, e.Bounds.Height),
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    private void SaveAndClose()
    {
        CaptureLoadedDay();
        CapturePrinterAssignments();
        _working.Normalize();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CapturePrinterAssignments()
    {
        foreach (var pair in _printerChoices)
        {
            var assignment = _working.Printers.First(printer =>
                string.Equals(printer.PrinterName, pair.Key, StringComparison.OrdinalIgnoreCase));
            assignment.ColorId = (pair.Value.SelectedItem as ColorChoice)?.Id ?? string.Empty;
        }
    }

    private void ApplyScheduleCellColor(DataGridViewCell cell)
    {
        var color = FindColor(cell.Value?.ToString());
        ApplyCellColor(cell.Style, color);
    }

    private void ApplyComboColor(ComboBox combo)
    {
        var color = FindColor((combo.SelectedItem as ColorChoice)?.Id);
        combo.BackColor = color is null ? Color.White : ParseColor(color.HexColor);
        combo.ForeColor = ContrastingText(combo.BackColor);
    }

    private WristbandColorDefinition? FindColor(string? id) =>
        _working.Colors.FirstOrDefault(color =>
            string.Equals(color.Id, id, StringComparison.Ordinal));

    private static void ApplyCellColor(DataGridViewCellStyle style, WristbandColorDefinition? color)
    {
        style.BackColor = color is null ? Color.White : ParseColor(color.HexColor);
        style.ForeColor = ContrastingText(style.BackColor);
        style.SelectionBackColor = style.BackColor;
        style.SelectionForeColor = style.ForeColor;
    }

    private static string FormatSlot(int startMinute)
    {
        var baseDate = new DateTime(2000, 1, 1);
        var start = baseDate.AddMinutes(startMinute);
        var end = start.AddHours(1);
        var suffix = startMinute >= 1440 ? " (next day)" : string.Empty;
        return $"{start:h:mm tt} – {end:h:mm tt}{suffix}";
    }

    internal static Color ParseColor(string value)
    {
        try { return ColorTranslator.FromHtml(value); }
        catch { return Color.FromArgb(217, 222, 227); }
    }

    internal static Color ContrastingText(Color background)
    {
        var brightness = (background.R * 299 + background.G * 587 + background.B * 114) / 1000;
        return brightness >= 145 ? Color.FromArgb(25, 30, 35) : Color.White;
    }

    private static Button MakeButton(string text, Color background, Color? foreground = null)
    {
        var button = new Button
        {
            Text = text,
            BackColor = background,
            ForeColor = foreground ?? Color.FromArgb(25, 30, 35),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}

internal sealed class WristbandColorManagerDialog : Form
{
    private readonly List<WristbandColorDefinition> _colors;
    private readonly ListBox _list = new();
    private readonly Button _edit;
    private readonly Button _delete;
    private readonly Button _makeActive;
    private readonly Button _makeInactive;

    public WristbandColorManagerDialog(IEnumerable<WristbandColorDefinition> colors)
    {
        _colors = colors.Select(color => color.Clone()).ToList();
        Text = "Manage Wristband Colors";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(610, 500);
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 248, 251);

        Controls.Add(new Label
        {
            Text = "Add the wristband colors your location uses. Inactive colors remain visible on saved schedules but are clearly marked.",
            Bounds = new Rectangle(24, 18, 555, 45),
            ForeColor = Color.FromArgb(52, 65, 76)
        });

        _list.Bounds = new Rectangle(24, 70, 562, 300);
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.ItemHeight = 38;
        _list.DrawItem += DrawColorItem;
        _list.DoubleClick += (_, _) => EditSelected();
        Controls.Add(_list);

        var add = MakeButton("Add Color", 24, 384, 105, Color.FromArgb(118, 196, 66));
        _edit = MakeButton("Edit Color", 137, 384, 98, Color.FromArgb(105, 210, 236));
        _delete = MakeButton("Delete Color", 243, 384, 110, Color.FromArgb(187, 34, 46), Color.White);
        _makeActive = MakeButton("Make Active", 361, 384, 105, Color.FromArgb(245, 130, 32));
        _makeInactive = MakeButton("Make Inactive", 474, 384, 112, Color.FromArgb(98, 107, 117), Color.White);
        add.Click += (_, _) => AddColor();
        _edit.Click += (_, _) => EditSelected();
        _delete.Click += (_, _) => DeleteSelected();
        _makeActive.Click += (_, _) => SetSelectedActive(true);
        _makeInactive.Click += (_, _) => SetSelectedActive(false);
        _list.SelectedIndexChanged += (_, _) => UpdateActionButtons();
        Controls.AddRange([add, _edit, _delete, _makeActive, _makeInactive]);

        var done = MakeButton("Use This Color List", 368, 446, 218, Color.FromArgb(117, 68, 154), Color.White);
        done.DialogResult = DialogResult.OK;
        var cancel = MakeButton("Cancel", 248, 446, 110, Color.FromArgb(98, 107, 117), Color.White);
        cancel.DialogResult = DialogResult.Cancel;
        Controls.AddRange([done, cancel]);
        AcceptButton = done;
        CancelButton = cancel;
        RefreshList();
    }

    public IReadOnlyList<WristbandColorDefinition> Colors => _colors;

    private WristbandColorDefinition? SelectedColor => _list.SelectedItem as WristbandColorDefinition;

    private void AddColor()
    {
        using var dialog = new WristbandColorEditDialog(null);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (_colors.Any(color => string.Equals(
                color.Name,
                dialog.ColorDefinition.Name,
                StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A wristband color with that name already exists.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _colors.Add(dialog.ColorDefinition);
        RefreshList(dialog.ColorDefinition.Id);
    }

    private void EditSelected()
    {
        var selected = SelectedColor;
        if (selected is null)
            return;
        using var dialog = new WristbandColorEditDialog(selected);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (_colors.Any(color => !ReferenceEquals(color, selected) && string.Equals(
                color.Name,
                dialog.ColorDefinition.Name,
                StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A wristband color with that name already exists.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        selected.Name = dialog.ColorDefinition.Name;
        selected.HexColor = dialog.ColorDefinition.HexColor;
        selected.IsActive = dialog.ColorDefinition.IsActive;
        RefreshList(selected.Id);
    }

    private void DeleteSelected()
    {
        var selected = SelectedColor;
        if (selected is null)
            return;
        if (MessageBox.Show(
                this,
                $"Delete {selected.Name}? It will also be removed from saved time and printer assignments.",
                "Delete Wristband Color",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }
        _colors.Remove(selected);
        RefreshList();
    }

    private void SetSelectedActive(bool active)
    {
        var selected = SelectedColor;
        if (selected is null)
            return;
        selected.IsActive = active;
        RefreshList(selected.Id);
    }

    private void UpdateActionButtons()
    {
        var selected = SelectedColor;
        _edit.Enabled = selected is not null;
        _delete.Enabled = selected is not null;
        _makeActive.Enabled = selected is not null && !selected.IsActive;
        _makeInactive.Enabled = selected is not null && selected.IsActive;
    }

    private void RefreshList(string? selectedId = null)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var color in _colors
                     .OrderByDescending(color => color.IsActive)
                     .ThenBy(color => color.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _list.Items.Add(color);
        }
        _list.EndUpdate();
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            _list.SelectedItem = _list.Items.Cast<WristbandColorDefinition>()
                .FirstOrDefault(color => color.Id == selectedId);
        }
        else if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
        UpdateActionButtons();
        _list.Invalidate();
    }

    private void DrawColorItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _list.Items.Count)
            return;
        var color = (WristbandColorDefinition)_list.Items[e.Index];
        var swatch = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 7, 46, e.Bounds.Height - 14);
        using var brush = new SolidBrush(WristbandColorSettingsDialog.ParseColor(color.HexColor));
        e.Graphics.FillRectangle(brush, swatch);
        e.Graphics.DrawRectangle(Pens.DimGray, swatch);
        var status = color.IsActive ? "Active" : "Inactive";
        var textColor = color.IsActive ? e.ForeColor : Color.FromArgb(130, 136, 142);
        TextRenderer.DrawText(
            e.Graphics,
            $"{color.Name}   •   {status}",
            e.Font,
            new Rectangle(e.Bounds.X + 66, e.Bounds.Y, e.Bounds.Width - 72, e.Bounds.Height),
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    private static Button MakeButton(
        string text,
        int x,
        int y,
        int width,
        Color background,
        Color? foreground = null)
    {
        var button = new Button
        {
            Text = text,
            Bounds = new Rectangle(x, y, width, 38),
            BackColor = background,
            ForeColor = foreground ?? Color.FromArgb(25, 30, 35),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}

internal sealed class WristbandColorEditDialog : Form
{
    private readonly TextBox _name = new();
    private readonly Button _swatch = new();
    private readonly CheckBox _active = new();
    private Color _selectedColor;
    private readonly string _id;

    public WristbandColorEditDialog(WristbandColorDefinition? existing)
    {
        _id = existing?.Id ?? Guid.NewGuid().ToString("N");
        _selectedColor = WristbandColorSettingsDialog.ParseColor(existing?.HexColor ?? "#D9DEE3");
        Text = existing is null ? "Add Wristband Color" : "Edit Wristband Color";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(430, 245);
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 248, 251);

        Controls.Add(new Label { Text = "Color name:", Bounds = new Rectangle(24, 30, 110, 28) });
        _name.Bounds = new Rectangle(140, 26, 258, 32);
        _name.Text = existing?.Name ?? string.Empty;
        Controls.Add(_name);
        Controls.Add(new Label { Text = "Display color:", Bounds = new Rectangle(24, 84, 110, 28) });
        _swatch.Bounds = new Rectangle(140, 78, 258, 40);
        _swatch.Text = "Choose Color";
        _swatch.BackColor = _selectedColor;
        _swatch.ForeColor = WristbandColorSettingsDialog.ContrastingText(_selectedColor);
        _swatch.FlatStyle = FlatStyle.Flat;
        _swatch.Click += (_, _) => ChooseColor();
        Controls.Add(_swatch);
        _active.Text = "Color is active and available for use";
        _active.Checked = existing?.IsActive ?? true;
        _active.Bounds = new Rectangle(140, 132, 258, 32);
        Controls.Add(_active);

        var save = new Button
        {
            Text = "Save Color",
            Bounds = new Rectangle(248, 188, 150, 40),
            BackColor = Color.FromArgb(118, 196, 66),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) => SaveColor();
        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(128, 188, 110, 40),
            BackColor = Color.FromArgb(98, 107, 117),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.Cancel
        };
        cancel.FlatAppearance.BorderSize = 0;
        Controls.AddRange([save, cancel]);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public WristbandColorDefinition ColorDefinition { get; private set; } = new();

    private void ChooseColor()
    {
        using var picker = new ColorDialog
        {
            Color = _selectedColor,
            FullOpen = true,
            AnyColor = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        _selectedColor = picker.Color;
        _swatch.BackColor = _selectedColor;
        _swatch.ForeColor = WristbandColorSettingsDialog.ContrastingText(_selectedColor);
    }

    private void SaveColor()
    {
        var name = _name.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a name for the wristband color.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _name.Focus();
            return;
        }
        ColorDefinition = new WristbandColorDefinition
        {
            Id = _id,
            Name = name.Length <= 40 ? name : name[..40],
            HexColor = $"#{_selectedColor.R:X2}{_selectedColor.G:X2}{_selectedColor.B:X2}",
            IsActive = _active.Checked
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
