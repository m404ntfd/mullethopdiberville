namespace MulletHopWaiverKiosk;

internal sealed class KioskScreensaverVideo
{
    public const string DefaultId = "mh-daily";
    public const string DefaultName = "MH Daily";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Screensaver Video";
    public string VideoFileName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool ScheduledOverride { get; set; } = true;
    public DateTime StartDateTime { get; set; } = DateTime.Today;
    public DateTime EndDateTime { get; set; } = DateTime.Today.AddDays(1);

    public static KioskScreensaverVideo CreateDefault() => new()
    {
        Id = DefaultId,
        Name = DefaultName,
        VideoFileName = ScreensaverVideoFiles.DefaultPackagedFileName,
        Enabled = true,
        ScheduledOverride = false
    };

    public bool IsScheduledActive(DateTime now) =>
        Enabled && ScheduledOverride &&
        !string.IsNullOrWhiteSpace(VideoFileName) &&
        now >= StartDateTime && now <= EndDateTime;

    public string ScheduleSummary(bool isMain)
    {
        if (isMain)
            return "Main video · used when no scheduled override is active";
        if (!ScheduledOverride)
            return "No override schedule";
        return $"{StartDateTime:MMM d, yyyy h:mm tt} – {EndDateTime:MMM d, yyyy h:mm tt}";
    }

    public KioskScreensaverVideo Clone() => new()
    {
        Id = Id,
        Name = Name,
        VideoFileName = VideoFileName,
        Enabled = Enabled,
        ScheduledOverride = ScheduledOverride,
        StartDateTime = StartDateTime,
        EndDateTime = EndDateTime
    };

    public void Normalize(bool isDefault = false)
    {
        if (isDefault)
        {
            Id = DefaultId;
            Name = DefaultName;
            Enabled = true;
            ScheduledOverride = false;
        }
        else
        {
            if (!Guid.TryParseExact(Id, "N", out _))
                Id = Guid.NewGuid().ToString("N");
            Name = string.IsNullOrWhiteSpace(Name) ? "Screensaver Video" : Name.Trim();
        }

        VideoFileName = Path.GetFileName(VideoFileName ?? string.Empty);
        if (isDefault && string.IsNullOrWhiteSpace(VideoFileName))
            VideoFileName = ScreensaverVideoFiles.DefaultPackagedFileName;
        if (EndDateTime <= StartDateTime)
            EndDateTime = StartDateTime.AddHours(1);
    }
}

internal static class ScreensaverVideoCatalog
{
    public static void Normalize(KioskSettings settings)
    {
        settings.ScreensaverVideos ??= [];
        var defaultVideo = settings.ScreensaverVideos.FirstOrDefault(video =>
            string.Equals(video.Id, KioskScreensaverVideo.DefaultId, StringComparison.Ordinal));
        if (defaultVideo is null)
        {
            defaultVideo = KioskScreensaverVideo.CreateDefault();
            settings.ScreensaverVideos.Insert(0, defaultVideo);
        }

        defaultVideo.Normalize(isDefault: true);
        var usedIds = new HashSet<string>(StringComparer.Ordinal)
        {
            KioskScreensaverVideo.DefaultId
        };
        foreach (var video in settings.ScreensaverVideos.Where(video =>
                     !ReferenceEquals(video, defaultVideo)))
        {
            video.Normalize();
            while (!usedIds.Add(video.Id))
                video.Id = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(settings.MainScreensaverVideoId) ||
            settings.ScreensaverVideos.All(video =>
                !string.Equals(video.Id, settings.MainScreensaverVideoId, StringComparison.Ordinal)))
        {
            settings.MainScreensaverVideoId = KioskScreensaverVideo.DefaultId;
        }

        var main = settings.ScreensaverVideos.First(video =>
            string.Equals(video.Id, settings.MainScreensaverVideoId, StringComparison.Ordinal));
        main.Enabled = true;
    }

    public static KioskScreensaverVideo SelectConfigured(KioskSettings settings, DateTime now)
    {
        Normalize(settings);
        var scheduled = settings.ScreensaverVideos
            .Where(video =>
                !string.Equals(video.Id, settings.MainScreensaverVideoId, StringComparison.Ordinal) &&
                video.IsScheduledActive(now))
            .OrderByDescending(video => video.StartDateTime)
            .ThenBy(video => video.Name, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();
        if (scheduled is not null)
            return scheduled;

        return settings.ScreensaverVideos.First(video =>
            string.Equals(video.Id, settings.MainScreensaverVideoId, StringComparison.Ordinal));
    }

    public static KioskScreensaverVideo SelectPlayable(KioskSettings settings, DateTime now)
    {
        Normalize(settings);
        var scheduled = settings.ScreensaverVideos
            .Where(video =>
                !string.Equals(video.Id, settings.MainScreensaverVideoId, StringComparison.Ordinal) &&
                video.IsScheduledActive(now) &&
                ScreensaverVideoFiles.Exists(video.VideoFileName))
            .OrderByDescending(video => video.StartDateTime)
            .ThenBy(video => video.Name, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();
        if (scheduled is not null)
            return scheduled;

        var main = settings.ScreensaverVideos.First(video =>
            string.Equals(video.Id, settings.MainScreensaverVideoId, StringComparison.Ordinal));
        if (ScreensaverVideoFiles.Exists(main.VideoFileName))
            return main;

        var defaultVideo = settings.ScreensaverVideos.First(video =>
            string.Equals(video.Id, KioskScreensaverVideo.DefaultId, StringComparison.Ordinal));
        if (ScreensaverVideoFiles.Exists(defaultVideo.VideoFileName))
            return defaultVideo;

        var packagedFallback = KioskScreensaverVideo.CreateDefault();
        return ScreensaverVideoFiles.Exists(packagedFallback.VideoFileName)
            ? packagedFallback
            : defaultVideo;
    }
}

internal static class ScreensaverVideoFiles
{
    public const string DefaultPackagedFileName = "MulletHopScreensaver.mp4";
    public const string CustomVideoVirtualHost = "mullethop-screensaver-videos.local";
    private const long MaximumVideoBytes = 1_500_000_000;

    public static string ImportMp4(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
            throw new FileNotFoundException("The selected MP4 video could not be found.", sourcePath);
        if (!string.Equals(info.Extension, ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an MP4 video file.");
        if (info.Length < 1_024)
            throw new InvalidOperationException("The selected MP4 video is empty or incomplete.");
        if (info.Length > MaximumVideoBytes)
            throw new InvalidOperationException("The MP4 video must be smaller than 1.5 GB.");

        Directory.CreateDirectory(KioskSettings.ScreensaverVideosDirectory);
        var fileName = Guid.NewGuid().ToString("N") + ".mp4";
        File.Copy(sourcePath, Path.Combine(KioskSettings.ScreensaverVideosDirectory, fileName), false);
        return fileName;
    }

    public static string? GetPath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        var safeName = Path.GetFileName(fileName);
        if (string.Equals(safeName, DefaultPackagedFileName, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(AppContext.BaseDirectory, DefaultPackagedFileName);

        var root = Path.GetFullPath(KioskSettings.ScreensaverVideosDirectory) +
                   Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(
            KioskSettings.ScreensaverVideosDirectory, safeName));
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    public static bool Exists(string? fileName)
    {
        var path = GetPath(fileName);
        return path is not null && File.Exists(path);
    }

    public static string GetBrowserUrl(string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        var escaped = Uri.EscapeDataString(safeName);
        return string.Equals(safeName, DefaultPackagedFileName, StringComparison.OrdinalIgnoreCase)
            ? $"https://mullethop-kiosk.local/{escaped}"
            : $"https://{CustomVideoVirtualHost}/{escaped}";
    }

    public static void DeleteIfManaged(string? fileName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                string.Equals(
                    Path.GetFileName(fileName),
                    DefaultPackagedFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var path = GetPath(fileName);
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            KioskLog.Write("Screensaver video cleanup error: " +
                           ex.GetType().Name + " - " + ex.Message);
        }
    }
}

internal sealed class ScreensaverVideoManagerControl : UserControl
{
    private readonly KioskSettings _settings;
    private readonly DateTime? _previewNow;
    private readonly Label _activeVideo = new();
    private readonly ListView _list = new();
    private readonly Label _details = new();
    private readonly System.Windows.Forms.Timer _activeStatusTimer = new() { Interval = 1000 };
    private string _displayedActiveKey = string.Empty;
    private bool IsDarkTheme => KioskTheme.Evaluate(_settings, DateTime.Now).IsDark;

    public ScreensaverVideoManagerControl(KioskSettings settings, DateTime? previewNow)
    {
        _settings = settings;
        _previewNow = previewNow;
        Size = new Size(590, 540);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "SCREENSAVER VIDEOS",
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(10, 3, 560, 32)
        };
        var note = new Label
        {
            AutoSize = false,
            Text = "MH Daily is the original kiosk video. Imported MP4 files are copied into the kiosk's managed application files. Scheduled videos temporarily override the selected main video.",
            ForeColor = Color.FromArgb(52, 65, 76),
            Bounds = new Rectangle(15, 36, 550, 46)
        };
        var activeGroup = new GroupBox
        {
            Text = "Current Active Video",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(10, 84, 560, 66)
        };
        _activeVideo.AutoSize = false;
        _activeVideo.Bounds = new Rectangle(14, 23, 532, 30);
        _activeVideo.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _activeVideo.TextAlign = ContentAlignment.MiddleLeft;
        activeGroup.Controls.Add(_activeVideo);

        _list.Bounds = new Rectangle(10, 160, 560, 215);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.Columns.Add("Option", 135);
        _list.Columns.Add("Video File", 130);
        _list.Columns.Add("Schedule", 205);
        _list.Columns.Add("Status", 85);
        _list.SelectedIndexChanged += (_, _) => ShowSelectedDetails();
        _list.DoubleClick += (_, _) => EditSelected();

        var add = CreateButton("Add Video", 10, 385, 105, Color.FromArgb(118, 196, 66));
        add.Click += (_, _) => AddVideo();
        var edit = CreateButton("Edit", 123, 385, 92, Color.FromArgb(105, 210, 236));
        edit.Click += (_, _) => EditSelected();
        var setMain = CreateButton("Set as Main", 223, 385, 112, Color.FromArgb(117, 68, 154));
        setMain.ForeColor = Color.White;
        setMain.Click += (_, _) => SetSelectedAsMain();
        var toggle = CreateButton("Enable / Disable", 343, 385, 128, Color.FromArgb(255, 222, 89));
        toggle.Click += (_, _) => ToggleSelected();
        var delete = CreateButton("Delete", 479, 385, 91, Color.FromArgb(245, 130, 32));
        delete.ForeColor = Color.White;
        delete.Click += (_, _) => DeleteSelected();

        _details.AutoSize = false;
        _details.Bounds = new Rectangle(15, 432, 550, 82);
        _details.ForeColor = Color.FromArgb(52, 65, 76);
        _details.Font = new Font("Segoe UI", 9.2f, FontStyle.Bold);

        Controls.AddRange([
            heading, note, activeGroup, _list, add, edit, setMain, toggle, delete, _details]);
        KioskTheme.Apply(this, IsDarkTheme);
        RefreshList();
        if (!_previewNow.HasValue)
        {
            _activeStatusTimer.Tick += (_, _) => RefreshIfActiveVideoChanged();
            _activeStatusTimer.Start();
        }
        Disposed += (_, _) => _activeStatusTimer.Dispose();
    }

    private static Button CreateButton(string text, int x, int y, int width, Color color) => new()
    {
        Text = text,
        Bounds = new Rectangle(x, y, width, 37),
        BackColor = color,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 8.7f, FontStyle.Bold)
    };

    private KioskScreensaverVideo? SelectedVideo =>
        _list.SelectedItems.Count == 1
            ? _list.SelectedItems[0].Tag as KioskScreensaverVideo
            : null;

    private void RefreshList(string? selectId = null)
    {
        ScreensaverVideoCatalog.Normalize(_settings);
        var now = _previewNow ?? DateTime.Now;
        var active = ScreensaverVideoCatalog.SelectPlayable(_settings, now);
        _displayedActiveKey = ActiveKey(active);
        _activeVideo.Text = (_previewNow.HasValue ? "At the preview time: " : "Active now: ") +
                            active.Name + "  ·  " + Path.GetFileName(active.VideoFileName);
        _activeVideo.ForeColor = ScreensaverVideoFiles.Exists(active.VideoFileName)
            ? KioskTheme.SuccessText(IsDarkTheme)
            : KioskTheme.ErrorText(IsDarkTheme);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var video in _settings.ScreensaverVideos
                     .OrderBy(video => string.Equals(
                         video.Id,
                         _settings.MainScreensaverVideoId,
                         StringComparison.Ordinal) ? 0 : 1)
                     .ThenBy(video => video.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var isMain = string.Equals(
                video.Id, _settings.MainScreensaverVideoId, StringComparison.Ordinal);
            var isActive = string.Equals(video.Id, active.Id, StringComparison.Ordinal) &&
                           string.Equals(
                               video.VideoFileName,
                               active.VideoFileName,
                               StringComparison.OrdinalIgnoreCase);
            var status = !ScreensaverVideoFiles.Exists(video.VideoFileName)
                ? "Missing"
                : isActive
                    ? "Active"
                    : !video.Enabled
                        ? "Disabled"
                        : isMain ? "Main" : video.ScheduledOverride ? "Scheduled" : "Available";
            var item = new ListViewItem(video.Name) { Tag = video };
            item.SubItems.Add(Path.GetFileName(video.VideoFileName));
            item.SubItems.Add(video.ScheduleSummary(isMain));
            item.SubItems.Add(status);
            if (!video.Enabled || !ScreensaverVideoFiles.Exists(video.VideoFileName))
                item.ForeColor = KioskTheme.MutedText(IsDarkTheme);
            _list.Items.Add(item);
            if (string.Equals(video.Id, selectId, StringComparison.Ordinal))
                item.Selected = true;
        }
        _list.EndUpdate();
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0)
            _list.Items[0].Selected = true;
        ShowSelectedDetails();
    }

    private void RefreshIfActiveVideoChanged()
    {
        var active = ScreensaverVideoCatalog.SelectPlayable(_settings, DateTime.Now);
        if (string.Equals(_displayedActiveKey, ActiveKey(active), StringComparison.Ordinal))
            return;
        RefreshList(SelectedVideo?.Id);
    }

    private static string ActiveKey(KioskScreensaverVideo video) =>
        video.Id + "\u001f" + video.VideoFileName;

    private void ShowSelectedDetails()
    {
        var video = SelectedVideo;
        if (video is null)
        {
            _details.Text = "Select a video option to view or edit it.";
            return;
        }

        var isMain = string.Equals(
            video.Id, _settings.MainScreensaverVideoId, StringComparison.Ordinal);
        var path = ScreensaverVideoFiles.GetPath(video.VideoFileName);
        _details.Text = video.Name + " · " + (isMain ? "MAIN VIDEO" : "OVERRIDE OPTION") +
                        Environment.NewLine + video.ScheduleSummary(isMain) +
                        Environment.NewLine + (path ?? "No saved MP4 file") +
                        (ScreensaverVideoFiles.Exists(video.VideoFileName) ? string.Empty : "  ·  FILE MISSING");
    }

    private void AddVideo()
    {
        using var editor = new ScreensaverVideoEditorDialog(dark: IsDarkTheme);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Video is null)
            return;
        SaveEditedVideo(editor.Video, editor.SelectedSourcePath, oldFileName: null, isNew: true);
    }

    private void EditSelected()
    {
        var selected = SelectedVideo;
        if (selected is null)
            return;
        using var editor = new ScreensaverVideoEditorDialog(
            selected,
            isDefault: string.Equals(
                selected.Id, KioskScreensaverVideo.DefaultId, StringComparison.Ordinal),
            dark: IsDarkTheme);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Video is null)
            return;
        SaveEditedVideo(
            editor.Video,
            editor.SelectedSourcePath,
            selected.VideoFileName,
            isNew: false);
    }

    private void SaveEditedVideo(
        KioskScreensaverVideo edited,
        string? selectedSourcePath,
        string? oldFileName,
        bool isNew)
    {
        string? importedFileName = null;
        var previousVideos = _settings.ScreensaverVideos
            .Select(video => video.Clone())
            .ToList();
        var previousMainId = _settings.MainScreensaverVideoId;
        try
        {
            if (!string.IsNullOrWhiteSpace(selectedSourcePath))
            {
                importedFileName = ScreensaverVideoFiles.ImportMp4(selectedSourcePath);
                edited.VideoFileName = importedFileName;
            }

            if (isNew)
                _settings.ScreensaverVideos.Add(edited);
            else
            {
                var index = _settings.ScreensaverVideos.FindIndex(video =>
                    string.Equals(video.Id, edited.Id, StringComparison.Ordinal));
                if (index < 0)
                    throw new InvalidOperationException("The selected video option no longer exists.");
                _settings.ScreensaverVideos[index] = edited;
            }

            _settings.Save();
            if (importedFileName is not null &&
                !string.Equals(oldFileName, importedFileName, StringComparison.OrdinalIgnoreCase))
            {
                ScreensaverVideoFiles.DeleteIfManaged(oldFileName);
            }
            RefreshList(edited.Id);
        }
        catch (Exception ex)
        {
            _settings.ScreensaverVideos = previousVideos;
            _settings.MainScreensaverVideoId = previousMainId;
            ScreensaverVideoFiles.DeleteIfManaged(importedFileName);
            MessageBox.Show(this,
                "The screensaver video option could not be saved.\n\n" + ex.Message,
                "Screensaver Videos", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetSelectedAsMain()
    {
        var selected = SelectedVideo;
        if (selected is null)
            return;
        if (!ScreensaverVideoFiles.Exists(selected.VideoFileName))
        {
            MessageBox.Show(this,
                "Choose a valid MP4 file for this option before making it the main video.",
                "Screensaver Videos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var previousMainId = _settings.MainScreensaverVideoId;
        var previouslyEnabled = selected.Enabled;
        selected.Enabled = true;
        _settings.MainScreensaverVideoId = selected.Id;
        if (SaveSettings())
            RefreshList(selected.Id);
        else
        {
            _settings.MainScreensaverVideoId = previousMainId;
            selected.Enabled = previouslyEnabled;
        }
    }

    private void ToggleSelected()
    {
        var selected = SelectedVideo;
        if (selected is null)
            return;
        if (string.Equals(selected.Id, _settings.MainScreensaverVideoId, StringComparison.Ordinal))
        {
            MessageBox.Show(this,
                "The main screensaver video cannot be disabled. Select another main video first.",
                "Screensaver Videos", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var previousEnabled = selected.Enabled;
        selected.Enabled = !selected.Enabled;
        if (SaveSettings())
            RefreshList(selected.Id);
        else
            selected.Enabled = previousEnabled;
    }

    private void DeleteSelected()
    {
        var selected = SelectedVideo;
        if (selected is null)
            return;
        if (string.Equals(selected.Id, KioskScreensaverVideo.DefaultId, StringComparison.Ordinal))
        {
            MessageBox.Show(this,
                "MH Daily is the built-in fallback and cannot be deleted. Its MP4 file can be changed with Edit.",
                "Screensaver Videos", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.Equals(selected.Id, _settings.MainScreensaverVideoId, StringComparison.Ordinal))
        {
            MessageBox.Show(this,
                "Select a different main video before deleting this option.",
                "Screensaver Videos", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this,
                $"Delete '{selected.Name}' and its saved MP4 file?",
                "Delete Screensaver Video", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        var previousIndex = _settings.ScreensaverVideos.FindIndex(video =>
            string.Equals(video.Id, selected.Id, StringComparison.Ordinal));
        _settings.ScreensaverVideos.RemoveAll(video =>
            string.Equals(video.Id, selected.Id, StringComparison.Ordinal));
        if (SaveSettings())
        {
            ScreensaverVideoFiles.DeleteIfManaged(selected.VideoFileName);
            RefreshList();
        }
        else if (previousIndex >= 0)
            _settings.ScreensaverVideos.Insert(previousIndex, selected);
    }

    private bool SaveSettings()
    {
        try
        {
            _settings.Save();
            KioskLog.Write("Screensaver video catalog changed. Active video: " +
                           ScreensaverVideoCatalog.SelectConfigured(
                               _settings, DateTime.Now).Name + ".");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "The screensaver video settings could not be saved.\n\n" + ex.Message,
                "Screensaver Videos", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
}

internal sealed class ScreensaverVideoEditorDialog : Form
{
    private readonly TextBox _name = new();
    private readonly Label _fileLabel = new();
    private readonly CheckBox _enabled = new();
    private readonly CheckBox _scheduledOverride = new();
    private readonly DateTimePicker _startDate = new();
    private readonly DateTimePicker _startTime = new();
    private readonly DateTimePicker _endDate = new();
    private readonly DateTimePicker _endTime = new();
    private readonly GroupBox _scheduleGroup = new();
    private readonly KioskScreensaverVideo _working;
    private readonly bool _isDefault;

    public KioskScreensaverVideo? Video { get; private set; }
    public string? SelectedSourcePath { get; private set; }

    public ScreensaverVideoEditorDialog(
        KioskScreensaverVideo? existing = null,
        bool isDefault = false,
        bool dark = false)
    {
        _working = existing?.Clone() ?? new KioskScreensaverVideo();
        _isDefault = isDefault;
        Text = existing is null ? "Add Screensaver Video" : "Edit Screensaver Video";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(720, 505);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = existing is null ? "ADD SCREENSAVER VIDEO" : "EDIT SCREENSAVER VIDEO",
            Font = new Font("Segoe UI", 19, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(25, 12, 670, 42)
        };
        var videoGroup = new GroupBox
        {
            Text = "Video Option",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189),
            Bounds = new Rectangle(25, 62, 670, 160)
        };
        var nameLabel = MakeLabel("Option name:", 18, 35, 115);
        _name.Bounds = new Rectangle(138, 30, 300, 32);
        _name.MaxLength = 80;
        _name.ReadOnly = _isDefault;
        var upload = new Button
        {
            Text = "Choose MP4…",
            Bounds = new Rectangle(18, 78, 140, 38),
            BackColor = Color.FromArgb(105, 210, 236),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        upload.Click += (_, _) => SelectMp4();
        _fileLabel.AutoSize = false;
        _fileLabel.Bounds = new Rectangle(172, 75, 475, 45);
        _fileLabel.ForeColor = Color.FromArgb(52, 65, 76);
        _fileLabel.TextAlign = ContentAlignment.MiddleLeft;
        _enabled.Text = "This video option is enabled";
        _enabled.AutoSize = true;
        _enabled.Location = new Point(18, 128);
        videoGroup.Controls.AddRange([nameLabel, _name, upload, _fileLabel, _enabled]);

        _scheduleGroup.Text = "Scheduled Override";
        _scheduleGroup.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        _scheduleGroup.ForeColor = Color.FromArgb(117, 68, 154);
        _scheduleGroup.Bounds = new Rectangle(25, 235, 670, 190);
        _scheduledOverride.Text = "Temporarily override the main video during this date and time range";
        _scheduledOverride.AutoSize = true;
        _scheduledOverride.Location = new Point(18, 28);
        _scheduledOverride.CheckedChanged += (_, _) => UpdateScheduleControls();
        _scheduleGroup.Controls.AddRange([
            _scheduledOverride,
            MakeLabel("Date", 125, 63, 90), MakeLabel("Time", 410, 63, 90),
            MakeLabel("Starts:", 25, 91, 90), MakeLabel("Ends:", 25, 137, 90)]);
        ConfigureDatePicker(_startDate, 125, 83, 245);
        ConfigureTimePicker(_startTime, 410, 83, 140);
        ConfigureDatePicker(_endDate, 125, 129, 245);
        ConfigureTimePicker(_endTime, 410, 129, 140);
        _scheduleGroup.Controls.AddRange([_startDate, _startTime, _endDate, _endTime]);

        var save = new Button
        {
            Text = "Save Video Option",
            Bounds = new Rectangle(385, 445, 175, 42),
            BackColor = Color.FromArgb(118, 196, 66),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(570, 445, 125, 42),
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10, FontStyle.Bold)
        };
        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([heading, videoGroup, _scheduleGroup, save, cancel]);
        LoadValues();
        KioskTheme.Apply(this, dark);
    }

    private static Label MakeLabel(string text, int x, int y, int width) => new()
    {
        Text = text,
        AutoSize = false,
        Bounds = new Rectangle(x, y, width, 28),
        ForeColor = Color.FromArgb(16, 24, 32),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static void ConfigureDatePicker(DateTimePicker picker, int x, int y, int width)
    {
        picker.Format = DateTimePickerFormat.Long;
        picker.Bounds = new Rectangle(x, y, width, 31);
    }

    private static void ConfigureTimePicker(DateTimePicker picker, int x, int y, int width)
    {
        picker.Format = DateTimePickerFormat.Custom;
        picker.CustomFormat = "h:mm tt";
        picker.ShowUpDown = true;
        picker.Bounds = new Rectangle(x, y, width, 31);
    }

    private void LoadValues()
    {
        _name.Text = _isDefault ? KioskScreensaverVideo.DefaultName : _working.Name;
        _enabled.Checked = _working.Enabled || _isDefault;
        _enabled.Enabled = !_isDefault;
        _scheduledOverride.Checked = !_isDefault && _working.ScheduledOverride;
        _scheduledOverride.Enabled = !_isDefault;
        var start = _working.StartDateTime < DateTime.Today
            ? DateTime.Now : _working.StartDateTime;
        var end = _working.EndDateTime <= start
            ? start.AddDays(1) : _working.EndDateTime;
        _startDate.Value = start.Date;
        _startTime.Value = DateTime.Today.Add(start.TimeOfDay);
        _endDate.Value = end.Date;
        _endTime.Value = DateTime.Today.Add(end.TimeOfDay);
        _fileLabel.Text = string.IsNullOrWhiteSpace(_working.VideoFileName)
            ? "No MP4 selected." : Path.GetFileName(_working.VideoFileName);
        UpdateScheduleControls();
    }

    private void SelectMp4()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Choose a Screensaver MP4 Video",
            Filter = "MP4 videos (*.mp4)|*.mp4",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var info = new FileInfo(picker.FileName);
            if (info.Length < 1_024)
                throw new InvalidOperationException("The selected MP4 video is empty or incomplete.");
            if (info.Length > 1_500_000_000)
                throw new InvalidOperationException("The MP4 video must be smaller than 1.5 GB.");
            SelectedSourcePath = picker.FileName;
            _fileLabel.Text = info.Name + "  ·  " + FormatFileSize(info.Length);
            if (!_isDefault &&
                (string.IsNullOrWhiteSpace(_name.Text) || _name.Text == "Screensaver Video"))
            {
                _name.Text = Path.GetFileNameWithoutExtension(info.Name);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Screensaver Video",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateScheduleControls()
    {
        var enabled = !_isDefault && _scheduledOverride.Checked;
        _startDate.Enabled = enabled;
        _startTime.Enabled = enabled;
        _endDate.Enabled = enabled;
        _endTime.Enabled = enabled;
    }

    private void SaveAndClose()
    {
        var name = _isDefault ? KioskScreensaverVideo.DefaultName : _name.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "Enter a name for the video option.",
                "Screensaver Video", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _name.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_working.VideoFileName) &&
            string.IsNullOrWhiteSpace(SelectedSourcePath))
        {
            MessageBox.Show(this, "Choose an MP4 video before saving.",
                "Screensaver Video", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var start = _startDate.Value.Date + _startTime.Value.TimeOfDay;
        var end = _endDate.Value.Date + _endTime.Value.TimeOfDay;
        if (!_isDefault && _scheduledOverride.Checked && end <= start)
        {
            MessageBox.Show(this,
                "The ending date and time must be after the starting date and time.",
                "Screensaver Video Schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _working.Name = name;
        _working.Enabled = _isDefault || _enabled.Checked;
        _working.ScheduledOverride = !_isDefault && _scheduledOverride.Checked;
        _working.StartDateTime = start;
        _working.EndDateTime = end;
        _working.Normalize(_isDefault);
        Video = _working;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string FormatFileSize(long bytes) => bytes >= 1_000_000_000
        ? $"{bytes / 1_000_000_000d:0.0} GB"
        : $"{bytes / 1_000_000d:0.0} MB";
}
