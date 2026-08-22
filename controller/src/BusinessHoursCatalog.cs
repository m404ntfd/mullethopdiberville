using MulletHop.Shared;

namespace MulletHopKioskController;

internal enum ControllerKioskThemeMode
{
    Auto,
    Light,
    Dark
}

internal sealed class ControllerBusinessDayHours
{
    public DayOfWeek Day { get; set; }
    public bool IsOpen { get; set; } = true;
    public TimeSpan OpenTime { get; set; } = TimeSpan.FromHours(10);
    public TimeSpan LastJumpTimeSold { get; set; }
    public TimeSpan CloseTime { get; set; } = TimeSpan.FromHours(22);

    public ControllerBusinessDayHours Clone() => new()
    {
        Day = Day, IsOpen = IsOpen, OpenTime = OpenTime,
        LastJumpTimeSold = LastJumpTimeSold, CloseTime = CloseTime
    };

    public DateTime OpeningOn(DateTime businessDate) => businessDate.Date + OpenTime;

    public DateTime ClosingOn(DateTime businessDate)
    {
        var closing = businessDate.Date + CloseTime;
        return CloseTime == TimeSpan.Zero ? closing.AddDays(1) : closing;
    }

    public DateTime LastJumpOn(DateTime businessDate)
    {
        var lastJump = businessDate.Date + LastJumpTimeSold;
        if (CloseTime == TimeSpan.Zero && LastJumpTimeSold <= OpenTime)
            lastJump = lastJump.AddDays(1);
        return lastJump;
    }

    public bool HasValidTimes()
    {
        if (!IsTimeOfDay(OpenTime) || !IsTimeOfDay(CloseTime) ||
            (CloseTime != TimeSpan.Zero && CloseTime <= OpenTime))
            return false;
        var date = new DateTime(2000, 1, 3);
        return ClosingOn(date) - OpeningOn(date) >= TimeSpan.FromHours(1);
    }

    public bool HasValidLastJumpTime()
    {
        if (!HasValidTimes() || !IsTimeOfDay(LastJumpTimeSold))
            return false;
        var date = new DateTime(2000, 1, 3);
        var opening = OpeningOn(date);
        var closing = ClosingOn(date);
        var lastJump = LastJumpOn(date);
        return lastJump == closing - TimeSpan.FromHours(1) && lastJump >= opening;
    }

    public static TimeSpan CalculateLastJumpTimeSold(TimeSpan closeTime)
    {
        var value = (closeTime - TimeSpan.FromHours(1)).Ticks % TimeSpan.TicksPerDay;
        if (value < 0) value += TimeSpan.TicksPerDay;
        return TimeSpan.FromTicks(value);
    }

    private static bool IsTimeOfDay(TimeSpan value) =>
        value >= TimeSpan.Zero && value < TimeSpan.FromDays(1);
}

internal sealed class ControllerBusinessHours
{
    public bool Enabled { get; set; }
    public bool ShowClosedVideo { get; set; } = true;
    public bool BlackoutAtClosingTime { get; set; } = true;
    // Retained so profiles created by older releases still deserialize cleanly.
    public int ClosedMessageMinutes { get; set; } = 5;
    public int PreOpeningScreensaverMinutes { get; set; } = 30;
    public ControllerKioskThemeMode ThemeMode { get; set; } = ControllerKioskThemeMode.Light;
    public bool ScheduledDarkEnabled { get; set; }
    public DayOfWeek[] ScheduledDarkDays { get; set; } = Enum.GetValues<DayOfWeek>();
    public TimeSpan[] ScheduledDarkTimes { get; set; } =
        Enumerable.Repeat(TimeSpan.FromHours(18), 7).ToArray();
    // Retained for profiles and kiosks created before per-day times.
    public TimeSpan ScheduledDarkTime { get; set; } = TimeSpan.FromHours(18);
    public List<ControllerBusinessDayHours> Days { get; set; } = CreateDefaultDays();

    public static DayOfWeek[] OrderedDays { get; } =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public static List<ControllerBusinessDayHours> CreateDefaultDays() =>
        OrderedDays.Select(day => new ControllerBusinessDayHours
        {
            Day = day,
            LastJumpTimeSold = TimeSpan.FromHours(21)
        }).ToList();

    public ControllerBusinessHours Clone() => new()
    {
        Enabled = Enabled,
        ShowClosedVideo = ShowClosedVideo,
        BlackoutAtClosingTime = BlackoutAtClosingTime,
        ClosedMessageMinutes = ClosedMessageMinutes,
        PreOpeningScreensaverMinutes = PreOpeningScreensaverMinutes,
        ThemeMode = ThemeMode,
        ScheduledDarkEnabled = ScheduledDarkEnabled,
        ScheduledDarkDays = ScheduledDarkDays.ToArray(),
        ScheduledDarkTimes = ScheduledDarkTimes.ToArray(),
        ScheduledDarkTime = ScheduledDarkTime,
        Days = Days.Select(day => day.Clone()).ToList()
    };

    public void Normalize()
    {
        Days ??= CreateDefaultDays();
        var saved = Days.Where(day => Enum.IsDefined(day.Day))
            .GroupBy(day => day.Day).ToDictionary(group => group.Key, group => group.First());
        Days = OrderedDays.Select(day => saved.TryGetValue(day, out var value)
                ? value.Clone() : new ControllerBusinessDayHours
                {
                    Day = day,
                    LastJumpTimeSold = TimeSpan.FromHours(21)
                }).ToList();
        foreach (var day in Days)
        {
            day.OpenTime = NormalizeTime(day.OpenTime);
            day.CloseTime = NormalizeTime(day.CloseTime);
            if (day.IsOpen && !day.HasValidTimes())
            {
                day.OpenTime = TimeSpan.FromHours(10);
                day.CloseTime = TimeSpan.FromHours(22);
            }
            day.LastJumpTimeSold = ControllerBusinessDayHours.CalculateLastJumpTimeSold(day.CloseTime);
        }
        ClosedMessageMinutes = Math.Clamp(ClosedMessageMinutes, 1, 240);
        PreOpeningScreensaverMinutes = Math.Clamp(PreOpeningScreensaverMinutes, 0, 240);
        if (!Enum.IsDefined(ThemeMode)) ThemeMode = ControllerKioskThemeMode.Light;
        ScheduledDarkDays ??= [];
        ScheduledDarkDays = ScheduledDarkDays
            .Where(day => (int)day is >= 0 and <= 6)
            .Distinct()
            .ToArray();
        ScheduledDarkTime = NormalizeTime(ScheduledDarkTime);
        if (ScheduledDarkTimes is null || ScheduledDarkTimes.Length != 7)
            ScheduledDarkTimes = Enumerable.Repeat(ScheduledDarkTime, 7).ToArray();
        else
            ScheduledDarkTimes = ScheduledDarkTimes.Select(NormalizeTime).ToArray();
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
    public bool IncludesClosureSettings { get; set; }
    public bool ShowClosedVideo { get; set; }
    public bool BlackoutAtClosingTime { get; set; }
    public int ClosedMessageMinutes { get; set; }
    public int PreOpeningScreensaverMinutes { get; set; }
    public bool IncludesAppearanceSettings { get; set; }
    public int ThemeMode { get; set; }
    public bool ScheduledDarkEnabled { get; set; }
    public int[] ScheduledDarkDays { get; set; } = [];
    public TimeSpan[] ScheduledDarkTimes { get; set; } = [];
    public TimeSpan ScheduledDarkTime { get; set; }
    public List<BusinessHoursSyncItem> Days { get; set; } = [];
}

internal sealed class BusinessHoursSyncItem
{
    public int Day { get; set; }
    public bool IsOpen { get; set; }
    public TimeSpan OpenTime { get; set; }
    public TimeSpan LastJumpTimeSold { get; set; }
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
    private readonly CheckBox _showClosedVideo = new();
    private readonly CheckBox _blackoutAtClosingTime = new();
    private readonly NumericUpDown _preOpeningMinutes = new();
    private readonly ComboBox _kioskThemeMode = new();
    private readonly CheckBox _scheduledDarkEnabled = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1000 };
    private readonly Dictionary<DayOfWeek,
        (CheckBox Open, DateTimePicker Starts, DateTimePicker LastJump, DateTimePicker Ends)> _days = [];
    private readonly Dictionary<DayOfWeek, CheckBox> _scheduledDarkDays = [];
    private readonly Dictionary<DayOfWeek, DateTimePicker> _scheduledDarkTimes = [];

    public ControllerBusinessHoursDialog(ControllerState state, string? selectedStationId)
    {
        _state = state;
        _selectedStationId = selectedStationId;
        Text = "Business Hours, Kiosk Appearance, and Wristbands";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(650, 700);
        MinimumSize = new Size(666, 739);
        MaximizeBox = false;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 248, 251);

        var profile = state.BusinessHoursSnapshot();
        var tabs = new TabControl
        {
            Bounds = new Rectangle(14, 12, 622, 505),
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        var hoursPage = new TabPage("Business Hours")
        {
            BackColor = Color.FromArgb(244, 248, 251), Padding = new Padding(0)
        };
        var appearancePage = new TabPage("Kiosk Appearance")
        {
            BackColor = Color.FromArgb(244, 248, 251), Padding = new Padding(0)
        };
        var wristbandPage = new TabPage("Wristband Colors")
        {
            BackColor = Color.FromArgb(244, 248, 251), Padding = new Padding(0)
        };
        tabs.TabPages.AddRange([hoursPage, appearancePage, wristbandPage]);

        _enabled.Text = "Use automatic business hours";
        _enabled.Checked = profile.Enabled;
        _enabled.AutoSize = true;
        _enabled.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        _enabled.ForeColor = Color.FromArgb(117, 68, 154);
        _enabled.Location = new Point(14, 12);

        var weekly = new GroupBox
        {
            Text = "Weekly Hours", Bounds = new Rectangle(10, 48, 590, 278),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), ForeColor = Color.FromArgb(8, 119, 189)
        };
        weekly.Controls.AddRange([
            LabelAt("Day", 10, 28, 72, true), LabelAt("Open", 77, 28, 44, true),
            LabelAt("Opening", 120, 28, 120, true),
            LabelAt("Last 1-Hour Jump", 250, 28, 145, true),
            LabelAt("Closing", 405, 28, 140, true)
        ]);
        for (var index = 0; index < ControllerBusinessHours.OrderedDays.Length; index++)
        {
            var day = ControllerBusinessHours.OrderedDays[index];
            var value = profile.Days.First(item => item.Day == day);
            var y = 55 + index * 30;
            var open = new CheckBox { Checked = value.IsOpen, Bounds = new Rectangle(88, y + 2, 25, 25) };
            var starts = TimePicker(value.OpenTime, 120, y, 120);
            var lastJump = TimePicker(
                ControllerBusinessDayHours.CalculateLastJumpTimeSold(value.CloseTime),
                250, y, 145);
            var ends = TimePicker(value.CloseTime, 405, y, 140);
            _days[day] = (open, starts, lastJump, ends);
            open.CheckedChanged += (_, _) => UpdateEnabledState();
            ends.ValueChanged += (_, _) =>
                lastJump.Value = DateTime.Today +
                    ControllerBusinessDayHours.CalculateLastJumpTimeSold(ends.Value.TimeOfDay);
            weekly.Controls.AddRange([
                LabelAt(day.ToString(), 10, y + 2, 72), open, starts, lastJump, ends]);
        }

        var display = new GroupBox
        {
            Text = "Closed Display and Pre-Opening", Bounds = new Rectangle(10, 336, 590, 124),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), ForeColor = Color.FromArgb(117, 68, 154)
        };
        _showClosedVideo.Text = "Show Closed Video at final one-hour jump time";
        _showClosedVideo.Checked = profile.ShowClosedVideo;
        _showClosedVideo.AutoSize = true;
        _showClosedVideo.Location = new Point(18, 32);
        _blackoutAtClosingTime.Text = "Blackout 1 minute after closing time";
        _blackoutAtClosingTime.Checked = profile.BlackoutAtClosingTime;
        _blackoutAtClosingTime.AutoSize = true;
        _blackoutAtClosingTime.Location = new Point(300, 32);
        _preOpeningMinutes.SetBounds(250, 73, 72, 30);
        _preOpeningMinutes.Minimum = 0; _preOpeningMinutes.Maximum = 240; _preOpeningMinutes.Value = profile.PreOpeningScreensaverMinutes;
        display.Controls.AddRange([
            _showClosedVideo, _blackoutAtClosingTime,
            LabelAt("Start the screensaver before opening:", 18, 74, 225), _preOpeningMinutes,
            LabelAt("minutes before opening (0 = off)", 334, 74, 260)
        ]);

        hoursPage.Controls.AddRange([_enabled, weekly, display]);

        var themeGroup = new GroupBox
        {
            Text = "Kiosk Theme", Bounds = new Rectangle(10, 18, 590, 132),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), ForeColor = Color.FromArgb(117, 68, 154)
        };
        themeGroup.Controls.Add(LabelAt(
            "Auto follows Windows on each kiosk. Light and Dark override Windows.", 18, 28, 545));
        themeGroup.Controls.Add(LabelAt("Theme mode:", 18, 76, 125, true));
        _kioskThemeMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _kioskThemeMode.Items.AddRange(["Auto (Windows)", "Light", "Dark"]);
        _kioskThemeMode.SelectedIndex = profile.ThemeMode switch
        {
            ControllerKioskThemeMode.Light => 1,
            ControllerKioskThemeMode.Dark => 2,
            _ => 0
        };
        _kioskThemeMode.Bounds = new Rectangle(150, 74, 210, 30);
        themeGroup.Controls.Add(_kioskThemeMode);

        var scheduleGroup = new GroupBox
        {
            Text = "Scheduled Dark Mode", Bounds = new Rectangle(10, 155, 590, 310),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), ForeColor = Color.FromArgb(8, 119, 189)
        };
        _scheduledDarkEnabled.Text = "Switch Light kiosks to Dark on a schedule";
        _scheduledDarkEnabled.Checked = profile.ScheduledDarkEnabled;
        _scheduledDarkEnabled.AutoSize = true;
        _scheduledDarkEnabled.Location = new Point(18, 29);
        _scheduledDarkEnabled.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _scheduledDarkEnabled.CheckedChanged += (_, _) => UpdateAppearanceEnabledState();
        var scheduleNote = LabelAt(
            "Each selected day can switch to Dark at its own time. The override ends at the next configured business opening.",
            18, 58, 545);
        scheduleNote.Height = 38;
        scheduleGroup.Controls.AddRange([
            _scheduledDarkEnabled, scheduleNote,
            LabelAt("Day", 18, 96, 80, true),
            LabelAt("Switch to Dark at", 135, 96, 160, true)]);
        var dayChoices = new[]
        {
            (DayOfWeek.Monday, "Mon"), (DayOfWeek.Tuesday, "Tue"),
            (DayOfWeek.Wednesday, "Wed"), (DayOfWeek.Thursday, "Thu"),
            (DayOfWeek.Friday, "Fri"), (DayOfWeek.Saturday, "Sat"),
            (DayOfWeek.Sunday, "Sun")
        };
        for (var index = 0; index < dayChoices.Length; index++)
        {
            var day = dayChoices[index].Item1;
            var rowY = 119 + index * 25;
            var check = new CheckBox
            {
                Text = dayChoices[index].Item2,
                Checked = profile.ScheduledDarkDays.Contains(day),
                AutoSize = false,
                Bounds = new Rectangle(18, rowY, 90, 24)
            };
            var time = TimePicker(profile.ScheduledDarkTimes[(int)day], 135, rowY, 145);
            time.Height = 24;
            check.CheckedChanged += (_, _) => UpdateAppearanceEnabledState();
            _scheduledDarkDays[day] = check;
            _scheduledDarkTimes[day] = time;
            scheduleGroup.Controls.AddRange([check, time]);
        }
        var resultNote = LabelAt(
            "These settings are published and synced with the Business Hours profile.",
            315, 122, 245, true);
        resultNote.Height = 70;
        scheduleGroup.Controls.Add(resultNote);
        appearancePage.Controls.AddRange([themeGroup, scheduleGroup]);
        UpdateAppearanceEnabledState();

        var wristbandGroup = new GroupBox
        {
            Text = "Wristband Color Schedule",
            Bounds = new Rectangle(10, 18, 590, 230),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154)
        };
        var wristbandNote = LabelAt(
            "Set the wristband color for every one-hour jump window. New windows begin every 30 minutes, from opening through the final full-hour jump that ends at closing. A separate half-hour sale can still end at closing. You can also assign the color currently loaded in WB-1 through WB-7.",
            20, 35, 548);
        wristbandNote.Height = 86;
        var editWristbands = ButtonAt(
            "Edit Wristband Colors && Jump Times",
            120, 142, 350, Color.FromArgb(245, 130, 32));
        editWristbands.Height = 48;
        editWristbands.Click += (_, _) => OpenWristbandSettings();
        wristbandGroup.Controls.AddRange([wristbandNote, editWristbands]);
        wristbandPage.Controls.Add(wristbandGroup);
        var wristbandHelp = LabelAt(
            "The schedule uses the currently saved Business Hours. If hours have changed, select Save & Publish before editing wristband times.",
            30, 280, 548, true);
        wristbandHelp.Height = 70;
        wristbandPage.Controls.Add(wristbandHelp);

        _status.SetBounds(24, 526, 602, 44);
        _status.ForeColor = Color.FromArgb(52, 65, 76);
        _status.Text = DescribeStatus();
        _progress.SetBounds(24, 574, 602, 16);
        _progress.Maximum = 100;
        _progress.Value = SyncPercent();

        var save = ButtonAt("Save && Publish", 24, 600, 180, Color.FromArgb(118, 196, 66));
        var selected = ButtonAt("Sync Selected Kiosk", 214, 600, 195, Color.FromArgb(105, 210, 236));
        var all = ButtonAt("Sync All Kiosks", 419, 600, 207, Color.FromArgb(117, 68, 154), Color.White);
        var close = ButtonAt("Close", 446, 650, 180, Color.FromArgb(83, 97, 109), Color.White);
        selected.Enabled = !string.IsNullOrWhiteSpace(selectedStationId);
        save.Click += (_, _) => SaveAndPublish();
        selected.Click += (_, _) => QueueSyncSelected();
        all.Click += (_, _) => QueueSyncAll();
        close.Click += (_, _) => Close();
        _enabled.CheckedChanged += (_, _) => UpdateEnabledState();
        Controls.AddRange([tabs, _status, _progress, save, selected, all, close]);
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
        RefreshStatus("Business Hours and kiosk appearance published; all kiosks will sync on their next check-in.");
    }

    private void OpenWristbandSettings()
    {
        if (!_state.IsMaster)
        {
            MessageBox.Show(
                this,
                "Wristband settings can be changed on the active master Systems Controller or from Mullet Hop POS.",
                "Wristband Colors",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using var dialog = new WristbandColorSettingsDialog(
            _state.CreateWristbandSettingsPackage());
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            _state.SaveWristbandSettings(dialog.Settings);
            RefreshStatus("Wristband colors and jump-time schedule saved and shared with Mullet Hop POS.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "The wristband settings could not be saved.\n\n" + ex.Message,
                "Wristband Colors",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private ControllerBusinessHours? ReadProfile()
    {
        var scheduledDays = _scheduledDarkDays
            .Where(pair => pair.Value.Checked)
            .Select(pair => pair.Key)
            .ToArray();
        if (_scheduledDarkEnabled.Checked && scheduledDays.Length == 0)
        {
            MessageBox.Show(this, "Select at least one day for scheduled Dark mode.",
                "Kiosk Appearance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        var profile = new ControllerBusinessHours
        {
            Enabled = _enabled.Checked,
            ShowClosedVideo = _showClosedVideo.Checked,
            BlackoutAtClosingTime = _blackoutAtClosingTime.Checked,
            PreOpeningScreensaverMinutes = (int)_preOpeningMinutes.Value,
            ThemeMode = _kioskThemeMode.SelectedIndex switch
            {
                1 => ControllerKioskThemeMode.Light,
                2 => ControllerKioskThemeMode.Dark,
                _ => ControllerKioskThemeMode.Auto
            },
            ScheduledDarkEnabled = _scheduledDarkEnabled.Checked,
            ScheduledDarkDays = scheduledDays,
            ScheduledDarkTimes = Enumerable.Range(0, 7)
                .Select(index => _scheduledDarkTimes[(DayOfWeek)index].Value.TimeOfDay)
                .ToArray(),
            Days = []
        };
        profile.ScheduledDarkTime = profile.ScheduledDarkTimes[(int)DayOfWeek.Monday];
        foreach (var day in ControllerBusinessHours.OrderedDays)
        {
            var controls = _days[day];
            var schedule = new ControllerBusinessDayHours
            {
                Day = day,
                IsOpen = controls.Open.Checked,
                OpenTime = controls.Starts.Value.TimeOfDay,
                LastJumpTimeSold = ControllerBusinessDayHours.CalculateLastJumpTimeSold(
                    controls.Ends.Value.TimeOfDay),
                CloseTime = controls.Ends.Value.TimeOfDay
            };
            if (schedule.IsOpen && !schedule.HasValidTimes())
            {
                MessageBox.Show(this,
                    day + " closing time must be at least one hour later than its opening time. " +
                    "A 12:00 AM closing is treated as midnight at the end of that business day.",
                    "Business Hours", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                controls.Ends.Focus();
                return null;
            }
            profile.Days.Add(schedule);
        }
        return profile;
    }

    private void QueueSyncSelected()
    {
        if (_selectedStationId is not null && _state.QueueCommand(_selectedStationId, CommandTypes.SyncBusinessHours))
            RefreshStatus("Hours and appearance sync queued for the selected kiosk.");
    }

    private void QueueSyncAll()
    {
        var count = _state.QueueCommandForAll(CommandTypes.SyncBusinessHours);
        RefreshStatus($"Hours and appearance sync queued for {count} kiosk(s).");
    }

    private void RefreshStatus(string message)
    {
        _status.Text = message + "\n" + DescribeStatus();
        _progress.Value = SyncPercent();
    }

    private string DescribeStatus()
    {
        var revision = _state.BusinessHoursRevision;
        if (string.IsNullOrWhiteSpace(revision)) return "No Hours and Appearance profile has been published yet.";
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
            controls.LastJump.Enabled = false;
            controls.Ends.Enabled = _enabled.Checked && controls.Open.Checked;
        }
        _showClosedVideo.Enabled = _enabled.Checked;
        _blackoutAtClosingTime.Enabled = _enabled.Checked;
        _preOpeningMinutes.Enabled = _enabled.Checked;
    }

    private void UpdateAppearanceEnabledState()
    {
        var enabled = _scheduledDarkEnabled.Checked;
        foreach (var pair in _scheduledDarkDays)
        {
            pair.Value.Enabled = enabled;
            _scheduledDarkTimes[pair.Key].Enabled = enabled && pair.Value.Checked;
        }
    }

    private static Label LabelAt(string text, int x, int y, int width, bool bold = false) => new()
    {
        Text = text, AutoSize = false, Bounds = new Rectangle(x, y, width, 27),
        ForeColor = Color.FromArgb(16, 24, 32),
        Font = new Font("Segoe UI", 9.2f, bold ? FontStyle.Bold : FontStyle.Regular),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static DateTimePicker TimePicker(TimeSpan value, int x, int y, int width) => new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "h:mm tt", ShowUpDown = true,
        Value = DateTime.Today + value, Bounds = new Rectangle(x, y, width, 27), Font = new Font("Segoe UI", 9)
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
