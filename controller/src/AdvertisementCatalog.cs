namespace MulletHopKioskController;

internal enum ControllerAdvertisementScheduleType
{
    SpecificDates,
    Weekly
}

internal sealed class ControllerAdvertisement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Advertisement";
    public string ImageFileName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public ControllerAdvertisementScheduleType ScheduleType { get; set; } =
        ControllerAdvertisementScheduleType.SpecificDates;
    public DateTime StartDateTime { get; set; } = DateTime.Today;
    public DateTime EndDateTime { get; set; } = DateTime.Today.AddDays(1).AddTicks(-1);
    public DayOfWeek[] DaysOfWeek { get; set; } = Enum.GetValues<DayOfWeek>();
    public TimeSpan DailyStartTime { get; set; } = TimeSpan.FromHours(10);
    public TimeSpan DailyEndTime { get; set; } = TimeSpan.FromHours(22);

    public bool IsActive(DateTime now)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(ImageFileName))
            return false;
        if (ScheduleType == ControllerAdvertisementScheduleType.SpecificDates)
            return now >= StartDateTime && now <= EndDateTime;

        var time = now.TimeOfDay;
        if (DailyStartTime == DailyEndTime)
            return DaysOfWeek.Contains(now.DayOfWeek);
        if (DailyStartTime <= DailyEndTime)
            return DaysOfWeek.Contains(now.DayOfWeek) &&
                   time >= DailyStartTime && time <= DailyEndTime;
        if (DaysOfWeek.Contains(now.DayOfWeek) && time >= DailyStartTime)
            return true;
        var previousDay = (DayOfWeek)(((int)now.DayOfWeek + 6) % 7);
        return DaysOfWeek.Contains(previousDay) && time <= DailyEndTime;
    }

    public string ScheduleSummary()
    {
        if (ScheduleType == ControllerAdvertisementScheduleType.SpecificDates)
            return $"{StartDateTime:MMM d, yyyy h:mm tt} – {EndDateTime:MMM d, yyyy h:mm tt}";

        var days = DaysOfWeek.Length == 7
            ? "Every day"
            : string.Join(", ", DaysOfWeek.Select(day => day.ToString()[..3]));
        if (DailyStartTime == DailyEndTime)
            return days + " · All day";
        var overnight = DailyStartTime > DailyEndTime ? " (overnight)" : string.Empty;
        return $"{days} · {DateTime.Today.Add(DailyStartTime):h:mm tt}–" +
               $"{DateTime.Today.Add(DailyEndTime):h:mm tt}{overnight}";
    }

    public ControllerAdvertisement Clone() => new()
    {
        Id = Id,
        Name = Name,
        ImageFileName = ImageFileName,
        Enabled = Enabled,
        ScheduleType = ScheduleType,
        StartDateTime = StartDateTime,
        EndDateTime = EndDateTime,
        DaysOfWeek = [.. DaysOfWeek],
        DailyStartTime = DailyStartTime,
        DailyEndTime = DailyEndTime
    };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        Name = string.IsNullOrWhiteSpace(Name) ? "Advertisement" : Name.Trim();
        ImageFileName = Path.GetFileName(ImageFileName ?? string.Empty);
        if (ScheduleType != ControllerAdvertisementScheduleType.SpecificDates &&
            ScheduleType != ControllerAdvertisementScheduleType.Weekly)
            ScheduleType = ControllerAdvertisementScheduleType.SpecificDates;
        if (EndDateTime <= StartDateTime) EndDateTime = StartDateTime.AddHours(1);
        DaysOfWeek ??= [];
        DaysOfWeek = DaysOfWeek.Distinct().Where(day => (int)day is >= 0 and <= 6).ToArray();
        if (DaysOfWeek.Length == 0) DaysOfWeek = Enum.GetValues<DayOfWeek>();
        DailyStartTime = NormalizeTime(DailyStartTime);
        DailyEndTime = NormalizeTime(DailyEndTime);
    }

    private static TimeSpan NormalizeTime(TimeSpan value)
    {
        var ticks = value.Ticks % TimeSpan.TicksPerDay;
        if (ticks < 0) ticks += TimeSpan.TicksPerDay;
        return TimeSpan.FromTicks(ticks);
    }
}

internal sealed class AdvertisementSyncRequest
{
    public string StationId { get; set; } = string.Empty;
}

internal sealed class AdvertisementSyncPackage
{
    public string Revision { get; set; } = string.Empty;
    public DateTime GeneratedUtc { get; set; }
    public List<AdvertisementSyncItem> Advertisements { get; set; } = [];
}

internal sealed class AdvertisementSyncItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ImageFileName { get; set; } = string.Empty;
    public string ImageBase64 { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int ScheduleType { get; set; }
    public DateTime StartDateTime { get; set; }
    public DateTime EndDateTime { get; set; }
    public int[] DaysOfWeek { get; set; } = [];
    public TimeSpan DailyStartTime { get; set; }
    public TimeSpan DailyEndTime { get; set; }
}

internal static class ControllerAdvertisementFiles
{
    public static string DirectoryPath => Path.Combine(ControllerLog.DataDirectory, "Advertisements");

    public static string ImportJpeg(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
            throw new FileNotFoundException("The selected JPG could not be found.", sourcePath);
        if (info.Length > 25_000_000)
            throw new InvalidOperationException("The JPG must be smaller than 25 MB.");

        using (var image = Image.FromFile(sourcePath))
        {
            if (image.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                throw new InvalidOperationException("The selected file is not a valid JPG image.");
        }

        Directory.CreateDirectory(DirectoryPath);
        var fileName = Guid.NewGuid().ToString("N") + ".jpg";
        File.Copy(sourcePath, Path.Combine(DirectoryPath, fileName), false);
        return fileName;
    }

    public static string? GetSafePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var root = Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(DirectoryPath, Path.GetFileName(fileName)));
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    public static void DeleteIfPresent(string? fileName)
    {
        try
        {
            var path = GetSafePath(fileName);
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Advertisement image cleanup error: " + ex.Message);
        }
    }
}

internal sealed class ControllerAdvertisementManagerDialog : Form
{
    private readonly ControllerState _state;
    private readonly List<ControllerAdvertisement> _advertisements;
    private readonly ListView _list = new();
    private readonly PictureBox _preview = new();
    private readonly Label _details = new();
    private readonly Label _publishStatus = new();
    private readonly ProgressBar _fleetSyncProgress = new();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };

    public ControllerAdvertisementManagerDialog(ControllerState state)
    {
        _state = state;
        _advertisements = state.AdvertisementSnapshot().Select(item => item.Clone()).ToList();
        Text = "Manage Kiosk Advertisements";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(980, 700);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "KIOSK MANAGER ADVERTISEMENTS",
            Font = new Font("Segoe UI", 19, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            Bounds = new Rectangle(25, 12, 760, 42)
        };
        var note = new Label
        {
            AutoSize = false,
            Text = "Changes publish a new catalog. Connected kiosks automatically sync it on their next check-in.",
            ForeColor = Color.FromArgb(83, 97, 109),
            Bounds = new Rectangle(25, 51, 920, 25)
        };

        _list.Bounds = new Rectangle(25, 82, 630, 480);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.Columns.Add("Advertisement", 190);
        _list.Columns.Add("Schedule", 335);
        _list.Columns.Add("Status", 95);
        _list.SelectedIndexChanged += (_, _) => ShowSelectedPreview();
        _list.DoubleClick += (_, _) => EditSelected();

        _preview.Bounds = new Rectangle(685, 82, 260, 250);
        _preview.BorderStyle = BorderStyle.FixedSingle;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(247, 251, 253);
        _details.AutoSize = false;
        _details.Bounds = new Rectangle(685, 345, 260, 105);
        _details.ForeColor = Color.FromArgb(16, 24, 32);
        _details.Font = new Font("Segoe UI", 9.5f);
        var syncStatusGroup = new GroupBox
        {
            Text = "Kiosk Sync Status",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(675, 445, 280, 125)
        };
        _publishStatus.AutoSize = false;
        _publishStatus.Bounds = new Rectangle(12, 24, 255, 58);
        _publishStatus.ForeColor = Color.FromArgb(8, 119, 189);
        _publishStatus.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _fleetSyncProgress.Minimum = 0;
        _fleetSyncProgress.Maximum = 100;
        _fleetSyncProgress.Style = ProgressBarStyle.Continuous;
        _fleetSyncProgress.Bounds = new Rectangle(12, 88, 255, 22);
        syncStatusGroup.Controls.AddRange([_publishStatus, _fleetSyncProgress]);

        var addButton = CreateButton("Add Advertisement", 25, 165, Color.FromArgb(118, 196, 66));
        addButton.Click += (_, _) => AddAdvertisement();
        var editButton = CreateButton("Edit", 200, 110, Color.FromArgb(105, 210, 236));
        editButton.Click += (_, _) => EditSelected();
        var toggleButton = CreateButton("Enable / Disable", 320, 155, Color.FromArgb(255, 222, 89));
        toggleButton.Click += (_, _) => ToggleSelected();
        var deleteButton = CreateButton("Delete", 485, 110, Color.FromArgb(245, 130, 32));
        deleteButton.Click += (_, _) => DeleteSelected();
        var syncAllButton = CreateButton("Sync All Kiosks", 605, 160, Color.FromArgb(117, 68, 154));
        syncAllButton.ForeColor = Color.White;
        syncAllButton.Click += (_, _) => RepublishCatalog();
        var closeButton = CreateButton("Close", 825, 120, Color.FromArgb(238, 250, 255));
        closeButton.DialogResult = DialogResult.OK;

        AcceptButton = closeButton;
        CancelButton = closeButton;
        Controls.AddRange([
            heading, note, _list, _preview, _details, syncStatusGroup,
            addButton, editButton, toggleButton, deleteButton, syncAllButton, closeButton]);
        _statusTimer.Tick += (_, _) => UpdatePublishStatus();
        _statusTimer.Start();
        FormClosed += (_, _) =>
        {
            _statusTimer.Stop();
            _preview.Image?.Dispose();
        };
        RefreshList();
        UpdatePublishStatus();
        ControllerTheme.Apply(this);
    }

    private static Button CreateButton(string text, int x, int width, Color color) => new()
    {
        Text = text,
        Bounds = new Rectangle(x, 625, width, 44),
        BackColor = color,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
    };

    private ControllerAdvertisement? SelectedAdvertisement =>
        _list.SelectedItems.Count == 1
            ? _list.SelectedItems[0].Tag as ControllerAdvertisement
            : null;

    private void RefreshList(string? selectId = null)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var advertisement in _advertisements.OrderBy(ad => ad.Name))
        {
            var status = !advertisement.Enabled
                ? "Disabled"
                : advertisement.IsActive(DateTime.Now) ? "Active now" : "Scheduled";
            var item = new ListViewItem(advertisement.Name) { Tag = advertisement };
            item.SubItems.Add(advertisement.ScheduleSummary());
            item.SubItems.Add(status);
            if (!advertisement.Enabled) item.ForeColor = Color.Gray;
            _list.Items.Add(item);
            if (advertisement.Id == selectId) item.Selected = true;
        }
        _list.EndUpdate();
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
            _list.Items[0].Selected = true;
        ShowSelectedPreview();
    }

    private void ShowSelectedPreview()
    {
        _preview.Image?.Dispose();
        _preview.Image = null;
        var advertisement = SelectedAdvertisement;
        if (advertisement is null)
        {
            _details.Text = "Select an advertisement to preview it.";
            return;
        }

        var path = ControllerAdvertisementFiles.GetSafePath(advertisement.ImageFileName);
        if (path is not null && File.Exists(path))
        {
            try
            {
                using var image = Image.FromFile(path);
                _preview.Image = new Bitmap(image);
            }
            catch
            {
                _details.Text = "The saved JPG could not be opened.";
            }
        }
        _details.Text = advertisement.Name + Environment.NewLine +
            advertisement.ScheduleSummary() + Environment.NewLine +
            (advertisement.Enabled ? "Enabled" : "Disabled");
    }

    private void AddAdvertisement()
    {
        using var editor = new ControllerAdvertisementEditorDialog();
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Advertisement is null) return;
        _advertisements.Add(editor.Advertisement);
        PublishChanges();
        RefreshList(editor.Advertisement.Id);
    }

    private void EditSelected()
    {
        var selected = SelectedAdvertisement;
        if (selected is null) return;
        var oldImage = selected.ImageFileName;
        using var editor = new ControllerAdvertisementEditorDialog(selected);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Advertisement is null) return;
        var index = _advertisements.FindIndex(ad => ad.Id == selected.Id);
        if (index < 0) return;
        _advertisements[index] = editor.Advertisement;
        PublishChanges();
        if (!string.Equals(oldImage, editor.Advertisement.ImageFileName, StringComparison.OrdinalIgnoreCase))
            ControllerAdvertisementFiles.DeleteIfPresent(oldImage);
        RefreshList(editor.Advertisement.Id);
    }

    private void ToggleSelected()
    {
        var selected = SelectedAdvertisement;
        if (selected is null) return;
        selected.Enabled = !selected.Enabled;
        PublishChanges();
        RefreshList(selected.Id);
    }

    private void DeleteSelected()
    {
        var selected = SelectedAdvertisement;
        if (selected is null) return;
        if (MessageBox.Show(this,
                $"Delete the advertisement '{selected.Name}' and publish this change to all kiosks?",
                "Delete Advertisement", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _advertisements.RemoveAll(ad => ad.Id == selected.Id);
        PublishChanges();
        ControllerAdvertisementFiles.DeleteIfPresent(selected.ImageFileName);
        RefreshList();
    }

    private void RepublishCatalog()
    {
        _state.SaveAdvertisements(_advertisements);
        UpdatePublishStatus("A fresh sync was queued for every connected kiosk.");
    }

    private void PublishChanges()
    {
        _state.SaveAdvertisements(_advertisements);
        UpdatePublishStatus("Changes published. Kiosks will sync automatically.");
    }

    private void UpdatePublishStatus(string? message = null)
    {
        var kiosks = _state.Snapshot();
        var revision = _state.AdvertisementRevision;
        var synchronized = string.IsNullOrWhiteSpace(revision)
            ? 0
            : kiosks.Count(kiosk => string.Equals(
                kiosk.AdvertisementSyncRevision, revision, StringComparison.Ordinal));
        _fleetSyncProgress.Value = kiosks.Count == 0
            ? 0
            : Math.Clamp((int)Math.Round(synchronized * 100d / kiosks.Count), 0, 100);
        var published = _state.AdvertisementUpdatedUtc.HasValue
            ? _state.AdvertisementUpdatedUtc.Value.ToLocalTime().ToString("MMM d, yyyy h:mm:ss tt")
            : "Not published yet";
        var syncSummary = kiosks.Count == 0
            ? "No kiosks connected yet."
            : $"{synchronized} of {kiosks.Count} kiosk(s) synced.";
        _publishStatus.Text = (message ?? syncSummary) +
            Environment.NewLine + "Published: " + published;
    }
}

internal sealed class ControllerAdvertisementEditorDialog : Form
{
    private readonly TextBox _name = new();
    private readonly Label _fileLabel = new();
    private readonly PictureBox _preview = new();
    private readonly CheckBox _enabled = new();
    private readonly RadioButton _specificDates = new();
    private readonly RadioButton _weekly = new();
    private readonly DateTimePicker _startDate = new();
    private readonly DateTimePicker _startTime = new();
    private readonly DateTimePicker _endDate = new();
    private readonly DateTimePicker _endTime = new();
    private readonly DateTimePicker _weeklyStart = new();
    private readonly DateTimePicker _weeklyEnd = new();
    private readonly Dictionary<DayOfWeek, CheckBox> _dayChecks = [];
    private readonly ControllerAdvertisement _working;
    private string? _selectedSourcePath;

    public ControllerAdvertisement? Advertisement { get; private set; }

    public ControllerAdvertisementEditorDialog(ControllerAdvertisement? existing = null)
    {
        _working = existing?.Clone() ?? new ControllerAdvertisement();
        Text = existing is null ? "Add Manager Advertisement" : "Edit Manager Advertisement";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(790, 690);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = existing is null ? "ADD MANAGER ADVERTISEMENT" : "EDIT MANAGER ADVERTISEMENT",
            Font = new Font("Segoe UI", 19, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(25, 12, 740, 42)
        };

        var imageGroup = new GroupBox
        {
            Text = "JPG Advertisement",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(25, 60, 740, 220)
        };
        _preview.Bounds = new Rectangle(18, 30, 285, 170);
        _preview.BorderStyle = BorderStyle.FixedSingle;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(247, 251, 253);
        var uploadButton = new Button
        {
            Text = "Upload JPG…",
            Bounds = new Rectangle(330, 35, 150, 40),
            BackColor = Color.FromArgb(105, 210, 236),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        uploadButton.Click += (_, _) => SelectJpeg();
        _fileLabel.AutoSize = false;
        _fileLabel.Bounds = new Rectangle(495, 35, 220, 45);
        _fileLabel.ForeColor = Color.FromArgb(83, 97, 109);
        var nameLabel = new Label
        {
            Text = "Advertisement name:", AutoSize = true,
            ForeColor = Color.FromArgb(16, 24, 32), Location = new Point(330, 105)
        };
        _name.Bounds = new Rectangle(330, 130, 385, 32);
        _name.MaxLength = 80;
        _enabled.Text = "Advertisement is enabled";
        _enabled.AutoSize = true;
        _enabled.ForeColor = Color.FromArgb(16, 24, 32);
        _enabled.Location = new Point(330, 175);
        imageGroup.Controls.AddRange([_preview, uploadButton, _fileLabel, nameLabel, _name, _enabled]);

        var scheduleGroup = new GroupBox
        {
            Text = "Schedule",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            Bounds = new Rectangle(25, 292, 740, 310)
        };
        _specificDates.Text = "Run for specific dates";
        _specificDates.AutoSize = true;
        _specificDates.Location = new Point(22, 28);
        _weekly.Text = "Repeat every week";
        _weekly.AutoSize = true;
        _weekly.Location = new Point(235, 28);
        _specificDates.CheckedChanged += (_, _) => UpdateScheduleControls();
        _weekly.CheckedChanged += (_, _) => UpdateScheduleControls();

        var specificPanel = new GroupBox
        {
            Name = "specificPanel", Text = "Specific date and time range",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(18, 57, 700, 125)
        };
        specificPanel.Controls.AddRange([
            MakeLabel("Date", 90, 24), MakeLabel("Time", 365, 24),
            MakeLabel("Starts:", 20, 50), MakeLabel("Ends:", 20, 88)]);
        ConfigureDatePicker(_startDate, 90, 43, 245);
        ConfigureTimePicker(_startTime, 365, 43, 140);
        ConfigureDatePicker(_endDate, 90, 81, 245);
        ConfigureTimePicker(_endTime, 365, 81, 140);
        specificPanel.Controls.AddRange([_startDate, _startTime, _endDate, _endTime]);

        var weeklyPanel = new GroupBox
        {
            Name = "weeklyPanel", Text = "Weekly repeating schedule",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(18, 190, 700, 100)
        };
        var dayNames = new[]
        {
            (DayOfWeek.Sunday, "Sun"), (DayOfWeek.Monday, "Mon"),
            (DayOfWeek.Tuesday, "Tue"), (DayOfWeek.Wednesday, "Wed"),
            (DayOfWeek.Thursday, "Thu"), (DayOfWeek.Friday, "Fri"),
            (DayOfWeek.Saturday, "Sat")
        };
        for (var i = 0; i < dayNames.Length; i++)
        {
            var check = new CheckBox
            {
                Text = dayNames[i].Item2, AutoSize = true,
                ForeColor = Color.FromArgb(16, 24, 32), Location = new Point(18 + i * 85, 28)
            };
            _dayChecks[dayNames[i].Item1] = check;
            weeklyPanel.Controls.Add(check);
        }
        weeklyPanel.Controls.AddRange([
            MakeLabel("Daily start:", 18, 68), MakeLabel("Daily end:", 295, 68)]);
        ConfigureTimePicker(_weeklyStart, 105, 63, 130);
        ConfigureTimePicker(_weeklyEnd, 382, 63, 130);
        weeklyPanel.Controls.AddRange([_weeklyStart, _weeklyEnd]);
        scheduleGroup.Controls.AddRange([_specificDates, _weekly, specificPanel, weeklyPanel]);

        var saveButton = new Button
        {
            Text = "Publish Advertisement", Bounds = new Rectangle(455, 625, 170, 42),
            BackColor = Color.FromArgb(118, 196, 66), FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        saveButton.Click += (_, _) => SaveAndClose();
        var cancelButton = new Button
        {
            Text = "Cancel", Bounds = new Rectangle(635, 625, 130, 42),
            DialogResult = DialogResult.Cancel, BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.AddRange([heading, imageGroup, scheduleGroup, saveButton, cancelButton]);
        FormClosed += (_, _) => _preview.Image?.Dispose();
        LoadWorkingValues();
        ControllerTheme.Apply(this);
    }

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text, AutoSize = true,
        ForeColor = Color.FromArgb(16, 24, 32), Location = new Point(x, y)
    };

    private static void ConfigureDatePicker(DateTimePicker picker, int x, int y, int width)
    {
        picker.Format = DateTimePickerFormat.Short;
        picker.Font = new Font("Segoe UI", 10, FontStyle.Regular);
        picker.Bounds = new Rectangle(x, y, width, 30);
    }

    private static void ConfigureTimePicker(DateTimePicker picker, int x, int y, int width)
    {
        picker.Format = DateTimePickerFormat.Custom;
        picker.CustomFormat = "h:mm tt";
        picker.ShowUpDown = true;
        picker.Font = new Font("Segoe UI", 10, FontStyle.Regular);
        picker.Bounds = new Rectangle(x, y, width, 30);
    }

    private void LoadWorkingValues()
    {
        _name.Text = _working.Name;
        _enabled.Checked = _working.Enabled;
        _specificDates.Checked =
            _working.ScheduleType == ControllerAdvertisementScheduleType.SpecificDates;
        _weekly.Checked = _working.ScheduleType == ControllerAdvertisementScheduleType.Weekly;
        _startDate.Value = _working.StartDateTime.Date;
        _startTime.Value = DateTime.Today.Add(_working.StartDateTime.TimeOfDay);
        _endDate.Value = _working.EndDateTime.Date;
        _endTime.Value = DateTime.Today.Add(_working.EndDateTime.TimeOfDay);
        _weeklyStart.Value = DateTime.Today.Add(_working.DailyStartTime);
        _weeklyEnd.Value = DateTime.Today.Add(_working.DailyEndTime);
        foreach (var pair in _dayChecks) pair.Value.Checked = _working.DaysOfWeek.Contains(pair.Key);
        _fileLabel.Text = string.IsNullOrWhiteSpace(_working.ImageFileName)
            ? "No JPG selected." : "Saved JPG loaded.";
        LoadPreview(ControllerAdvertisementFiles.GetSafePath(_working.ImageFileName));
        UpdateScheduleControls();
    }

    private void SelectJpeg()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Choose a JPG Advertisement",
            Filter = "JPEG images (*.jpg;*.jpeg)|*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var info = new FileInfo(picker.FileName);
            if (info.Length > 25_000_000)
                throw new InvalidOperationException("The JPG must be smaller than 25 MB.");
            using (var image = Image.FromFile(picker.FileName))
            {
                if (image.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                    throw new InvalidOperationException("The selected file is not a valid JPG image.");
            }
            _selectedSourcePath = picker.FileName;
            _fileLabel.Text = Path.GetFileName(picker.FileName);
            if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text == "Advertisement")
                _name.Text = Path.GetFileNameWithoutExtension(picker.FileName);
            LoadPreview(picker.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "JPG Advertisement",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void LoadPreview(string? path)
    {
        _preview.Image?.Dispose();
        _preview.Image = null;
        if (path is null || !File.Exists(path)) return;
        try
        {
            using var image = Image.FromFile(path);
            _preview.Image = new Bitmap(image);
        }
        catch
        {
            _fileLabel.Text = "The JPG could not be previewed.";
        }
    }

    private void UpdateScheduleControls()
    {
        var specific = _specificDates.Checked;
        foreach (Control control in Controls.Find("specificPanel", true)) control.Enabled = specific;
        foreach (Control control in Controls.Find("weeklyPanel", true)) control.Enabled = !specific;
    }

    private void SaveAndClose()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            ShowProblem("Enter an advertisement name.", _name);
            return;
        }
        if (_selectedSourcePath is null && string.IsNullOrWhiteSpace(_working.ImageFileName))
        {
            ShowProblem("Upload a JPG advertisement.", _name);
            return;
        }
        if (_weekly.Checked && !_dayChecks.Values.Any(check => check.Checked))
        {
            ShowProblem("Select at least one day for the weekly schedule.", _weekly);
            return;
        }

        string? importedFileName = null;
        try
        {
            if (_selectedSourcePath is not null)
                importedFileName = ControllerAdvertisementFiles.ImportJpeg(_selectedSourcePath);

            var result = _working.Clone();
            result.Name = name;
            result.ImageFileName = importedFileName ?? _working.ImageFileName;
            result.Enabled = _enabled.Checked;
            result.ScheduleType = _weekly.Checked
                ? ControllerAdvertisementScheduleType.Weekly
                : ControllerAdvertisementScheduleType.SpecificDates;
            result.StartDateTime = _startDate.Value.Date + _startTime.Value.TimeOfDay;
            result.EndDateTime = _endDate.Value.Date + _endTime.Value.TimeOfDay;
            result.DaysOfWeek = _dayChecks.Where(pair => pair.Value.Checked)
                .Select(pair => pair.Key).ToArray();
            result.DailyStartTime = _weeklyStart.Value.TimeOfDay;
            result.DailyEndTime = _weeklyEnd.Value.TimeOfDay;
            result.Normalize();

            Advertisement = result;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            ControllerAdvertisementFiles.DeleteIfPresent(importedFileName);
            MessageBox.Show(this, "The advertisement could not be saved.\n\n" + ex.Message,
                "Advertisement", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowProblem(string message, Control focusControl)
    {
        MessageBox.Show(this, message, "Advertisement",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        focusControl.Focus();
    }
}
