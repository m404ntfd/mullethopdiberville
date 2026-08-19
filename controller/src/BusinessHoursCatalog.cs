namespace MulletHopKioskController;

internal sealed class ControllerBusinessDayHours
{
    public DayOfWeek Day { get; set; }
    public bool IsOpen { get; set; } = true;
    public TimeSpan OpenTime { get; set; } = TimeSpan.FromHours(10);
    public TimeSpan CloseTime { get; set; } = TimeSpan.FromHours(22);

    public ControllerBusinessDayHours Clone() => new()
    {
        Day = Day, IsOpen = IsOpen, OpenTime = OpenTime, CloseTime = CloseTime
    };
}

internal sealed class ControllerBusinessHours
{
    public bool Enabled { get; set; }
    public int ClosedMessageMinutes { get; set; } = 5;
    public int PreOpeningScreensaverMinutes { get; set; } = 30;
    public List<ControllerBusinessDayHours> Days { get; set; } = CreateDefaultDays();

    public static DayOfWeek[] OrderedDays { get; } =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public static List<ControllerBusinessDayHours> CreateDefaultDays() =>
        OrderedDays.Select(day => new ControllerBusinessDayHours { Day = day }).ToList();

    public ControllerBusinessHours Clone() => new()
    {
        Enabled = Enabled,
        ClosedMessageMinutes = ClosedMessageMinutes,
        PreOpeningScreensaverMinutes = PreOpeningScreensaverMinutes,
        Days = Days.Select(day => day.Clone()).ToList()
    };

    public void Normalize()
    {
        Days ??= CreateDefaultDays();
        var saved = Days.Where(day => Enum.IsDefined(day.Day))
            .GroupBy(day => day.Day).ToDictionary(group => group.Key, group => group.First());
        Days = OrderedDays.Select(day => saved.TryGetValue(day, out var value)
                ? value.Clone() : new ControllerBusinessDayHours { Day = day }).ToList();
        foreach (var day in Days)
        {
            day.OpenTime = NormalizeTime(day.OpenTime);
            day.CloseTime = NormalizeTime(day.CloseTime);
            if (day.IsOpen && day.CloseTime <= day.OpenTime)
            {
                day.OpenTime = TimeSpan.FromHours(10);
                day.CloseTime = TimeSpan.FromHours(22);
            }
        }
        ClosedMessageMinutes = Math.Clamp(ClosedMessageMinutes, 1, 240);
        PreOpeningScreensaverMinutes = Math.Clamp(PreOpeningScreensaverMinutes, 0, 240);
    }

    private static TimeSpan NormalizeTime(TimeSpan value)
    {
        var ticks = value.Ticks % TimeSpan.TicksPerDay;
        if (ticks < 0) ticks += TimeSpan.TicksPerDay;
        return TimeSpan.FromTicks(ticks);
    }
}

internal sealed class BusinessHoursSyncPackage
{
    public string Revision { get; set; } = string.Empty;
    public DateTime GeneratedUtc { get; set; }
    public bool Enabled { get; set; }
    public int ClosedMessageMinutes { get; set; }
    public int PreOpeningScreensaverMinutes { get; set; }
    public List<BusinessHoursSyncItem> Days { get; set; } = [];
}

internal sealed class BusinessHoursSyncItem
{
    public int Day { get; set; }
    public bool IsOpen { get; set; }
    public TimeSpan OpenTime { get; set; }
    public TimeSpan CloseTime { get; set; }
}

internal sealed class BusinessHoursSyncRequest
{
    public string StationId { get; set; } = string.Empty;
}

internal sealed class ControllerBusinessHoursDialog : Form
{
    private readonly ControllerState _state;
    private readonly string? _selectedStationId;
    private readonly CheckBox _enabled = new();
    private readonly NumericUpDown _closedMinutes = new();
    private readonly NumericUpDown _preOpeningMinutes = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1000 };
    private readonly Dictionary<DayOfWeek, (CheckBox Open, DateTimePicker Starts, DateTimePicker Ends)> _days = [];

    public ControllerBusinessHoursDialog(ControllerState state, string? selectedStationId)
    {
        _state = state;
        _selectedStationId = selectedStationId;
        Text = "Business Hours";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(650, 670);
        MinimumSize = new Size(666, 709);
        MaximizeBox = false;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 248, 251);

        var profile = state.BusinessHoursSnapshot();
        _enabled.Text = "Use automatic business hours";
        _enabled.Checked = profile.Enabled;
        _enabled.AutoSize = true;
        _enabled.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        _enabled.ForeColor = Color.FromArgb(117, 68, 154);
        _enabled.Location = new Point(24, 20);

        var weekly = new GroupBox
        {
            Text = "Weekly Hours", Bounds = new Rectangle(20, 58, 610, 278),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), ForeColor = Color.FromArgb(8, 119, 189)
        };
        weekly.Controls.AddRange([
            LabelAt("Day", 18, 28, 100, true), LabelAt("Open", 120, 28, 60, true),
            LabelAt("Opening time", 194, 28, 145, true), LabelAt("Closing time", 360, 28, 145, true)
        ]);
        for (var index = 0; index < ControllerBusinessHours.OrderedDays.Length; index++)
        {
            var day = ControllerBusinessHours.OrderedDays[index];
            var value = profile.Days.First(item => item.Day == day);
            var y = 55 + index * 30;
            var open = new CheckBox { Checked = value.IsOpen, Bounds = new Rectangle(140, y + 2, 25, 25) };
            var starts = TimePicker(value.OpenTime, 194, y);
            var ends = TimePicker(value.CloseTime, 360, y);
            _days[day] = (open, starts, ends);
            open.CheckedChanged += (_, _) => UpdateEnabledState();
            weekly.Controls.AddRange([LabelAt(day.ToString(), 18, y + 2, 105), open, starts, ends]);
        }

        var display = new GroupBox
        {
            Text = "Closed Display and Pre-Opening", Bounds = new Rectangle(20, 346, 610, 124),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), ForeColor = Color.FromArgb(117, 68, 154)
        };
        _closedMinutes.SetBounds(250, 30, 72, 30);
        _closedMinutes.Minimum = 1; _closedMinutes.Maximum = 240; _closedMinutes.Value = profile.ClosedMessageMinutes;
        _preOpeningMinutes.SetBounds(250, 73, 72, 30);
        _preOpeningMinutes.Minimum = 0; _preOpeningMinutes.Maximum = 240; _preOpeningMinutes.Value = profile.PreOpeningScreensaverMinutes;
        display.Controls.AddRange([
            LabelAt("Show Business Closed screen for:", 18, 31, 225), _closedMinutes,
            LabelAt("minutes, then black out", 334, 31, 210),
            LabelAt("Start the screensaver:", 18, 74, 225), _preOpeningMinutes,
            LabelAt("minutes before opening (0 = off)", 334, 74, 260)
        ]);

        _status.SetBounds(24, 482, 602, 50);
        _status.ForeColor = Color.FromArgb(52, 65, 76);
        _status.Text = DescribeStatus();
        _progress.SetBounds(24, 536, 602, 16);
        _progress.Maximum = 100;
        _progress.Value = SyncPercent();

        var save = ButtonAt("Save && Publish", 24, 570, 180, Color.FromArgb(118, 196, 66));
        var selected = ButtonAt("Sync Selected Kiosk", 214, 570, 195, Color.FromArgb(105, 210, 236));
        var all = ButtonAt("Sync All Kiosks", 419, 570, 207, Color.FromArgb(117, 68, 154), Color.White);
        var close = ButtonAt("Close", 446, 620, 180, Color.FromArgb(83, 97, 109), Color.White);
        selected.Enabled = !string.IsNullOrWhiteSpace(selectedStationId);
        save.Click += (_, _) => SaveAndPublish();
        selected.Click += (_, _) => QueueSyncSelected();
        all.Click += (_, _) => QueueSyncAll();
        close.Click += (_, _) => Close();
        _enabled.CheckedChanged += (_, _) => UpdateEnabledState();
        Controls.AddRange([_enabled, weekly, display, _status, _progress, save, selected, all, close]);
        UpdateEnabledState();
        _refreshTimer.Tick += (_, _) =>
        {
            _status.Text = DescribeStatus();
            _progress.Value = SyncPercent();
        };
        Shown += (_, _) => _refreshTimer.Start();
        FormClosed += (_, _) => _refreshTimer.Stop();
        ControllerTheme.Apply(this);
    }

    private void SaveAndPublish()
    {
        var profile = ReadProfile();
        if (profile is null) return;
        _state.SaveBusinessHours(profile);
        _state.QueueCommandForAll(CommandTypes.SyncBusinessHours);
        RefreshStatus("Business Hours published; all kiosks will sync on their next check-in.");
    }

    private ControllerBusinessHours? ReadProfile()
    {
        var profile = new ControllerBusinessHours
        {
            Enabled = _enabled.Checked,
            ClosedMessageMinutes = (int)_closedMinutes.Value,
            PreOpeningScreensaverMinutes = (int)_preOpeningMinutes.Value,
            Days = []
        };
        foreach (var day in ControllerBusinessHours.OrderedDays)
        {
            var controls = _days[day];
            if (controls.Open.Checked && controls.Ends.Value.TimeOfDay <= controls.Starts.Value.TimeOfDay)
            {
                MessageBox.Show(this, day + " closing time must be later than its opening time.",
                    "Business Hours", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                controls.Ends.Focus();
                return null;
            }
            profile.Days.Add(new ControllerBusinessDayHours
            {
                Day = day, IsOpen = controls.Open.Checked,
                OpenTime = controls.Starts.Value.TimeOfDay, CloseTime = controls.Ends.Value.TimeOfDay
            });
        }
        return profile;
    }

    private void QueueSyncSelected()
    {
        if (_selectedStationId is not null && _state.QueueCommand(_selectedStationId, CommandTypes.SyncBusinessHours))
            RefreshStatus("Business Hours sync queued for the selected kiosk.");
    }

    private void QueueSyncAll()
    {
        var count = _state.QueueCommandForAll(CommandTypes.SyncBusinessHours);
        RefreshStatus($"Business Hours sync queued for {count} kiosk(s).");
    }

    private void RefreshStatus(string message)
    {
        _status.Text = message + "\n" + DescribeStatus();
        _progress.Value = SyncPercent();
    }

    private string DescribeStatus()
    {
        var revision = _state.BusinessHoursRevision;
        if (string.IsNullOrWhiteSpace(revision)) return "No Business Hours profile has been published yet.";
        var kiosks = _state.Snapshot();
        var synced = kiosks.Count(kiosk => kiosk.BusinessHoursSyncRevision == revision);
        var updated = _state.BusinessHoursUpdatedUtc?.ToLocalTime().ToString("MMM d, yyyy h:mm tt") ?? "unknown";
        return $"Published {updated} — {synced} of {kiosks.Count} kiosk(s) synced.";
    }

    private int SyncPercent()
    {
        var kiosks = _state.Snapshot();
        if (kiosks.Count == 0 || string.IsNullOrWhiteSpace(_state.BusinessHoursRevision)) return 0;
        return kiosks.Count(kiosk => kiosk.BusinessHoursSyncRevision == _state.BusinessHoursRevision) * 100 / kiosks.Count;
    }

    private void UpdateEnabledState()
    {
        foreach (var controls in _days.Values)
        {
            controls.Open.Enabled = _enabled.Checked;
            controls.Starts.Enabled = _enabled.Checked && controls.Open.Checked;
            controls.Ends.Enabled = _enabled.Checked && controls.Open.Checked;
        }
        _closedMinutes.Enabled = _enabled.Checked;
        _preOpeningMinutes.Enabled = _enabled.Checked;
    }

    private static Label LabelAt(string text, int x, int y, int width, bool bold = false) => new()
    {
        Text = text, AutoSize = false, Bounds = new Rectangle(x, y, width, 27),
        ForeColor = Color.FromArgb(16, 24, 32),
        Font = new Font("Segoe UI", 9.2f, bold ? FontStyle.Bold : FontStyle.Regular),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static DateTimePicker TimePicker(TimeSpan value, int x, int y) => new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "h:mm tt", ShowUpDown = true,
        Value = DateTime.Today + value, Bounds = new Rectangle(x, y, 145, 27), Font = new Font("Segoe UI", 9)
    };

    private static Button ButtonAt(string text, int x, int y, int width, Color background, Color? foreground = null)
    {
        var button = new Button
        {
            Text = text, Bounds = new Rectangle(x, y, width, 40), BackColor = background,
            ForeColor = foreground ?? Color.FromArgb(16, 24, 32), FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
        return button;
    }
}
