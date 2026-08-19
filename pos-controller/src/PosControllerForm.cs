using System.Drawing.Drawing2D;

namespace MulletHopPosController;

internal sealed class PosControllerForm : Form
{
    private readonly PosSettings _settings;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2000 };
    private readonly Label _connectionStatus = new();
    private readonly Button _checkUpdateButton = new();
    private readonly KioskControlCard[] _cards = new KioskControlCard[4];
    private bool _refreshInProgress;
    private bool _updateCheckInProgress;

    public PosControllerForm(PosSettings settings)
    {
        _settings = settings;
        Text = "Mullet Hop POS Controller";
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon is not null)
            Icon = appIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 650);
        ClientSize = new Size(1280, 720);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(239, 244, 248);

        Controls.Add(BuildDashboard());
        Controls.Add(BuildStatusBar());
        Controls.Add(BuildHeader());

        _refreshTimer.Tick += async (_, _) => await RefreshStatusesAsync();
        Shown += async (_, _) =>
        {
            UpdateUnlinkedCards();
            if (!_settings.HasConnectionSettings)
                BeginInvoke(new Action(OpenSettings));
            await RefreshStatusesAsync();
            _refreshTimer.Start();
        };
        FormClosed += (_, _) => _refreshTimer.Stop();
    }

    private Panel BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 92,
            BackColor = Color.FromArgb(117, 68, 154)
        };
        var logo = new PictureBox
        {
            Image = LoadLogo(),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Bounds = new Rectangle(22, 9, 108, 72)
        };
        var title = new Label
        {
            Text = "MULLET HOP POS CONTROLLER",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 25, FontStyle.Bold),
            Bounds = new Rectangle(142, 16, 740, 58),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var settings = new Button
        {
            Text = "⚙  SETTINGS",
            Bounds = new Rectangle(1080, 24, 165, 46),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Color.FromArgb(255, 217, 188),
            ForeColor = Color.FromArgb(30, 20, 36),
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        settings.Click += (_, _) => OpenSettings();
        header.Controls.AddRange([logo, title, settings]);
        return header;
    }

    private Control BuildDashboard()
    {
        var background = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 28, 24, 22),
            BackColor = Color.FromArgb(239, 244, 248)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        for (var index = 0; index < 4; index++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            _cards[index] = new KioskControlCard(index + 1);
            var slot = index;
            _cards[index].CloseRequested += async (_, _) =>
                await SendCommandAsync(slot, PosCommandTypes.SetClosed, true);
            _cards[index].OpenRequested += async (_, _) =>
                await SendCommandAsync(slot, PosCommandTypes.SetClosed, false);
            _cards[index].ResetRequested += async (_, _) =>
                await SendCommandAsync(slot, PosCommandTypes.ResetStart);
            layout.Controls.Add(_cards[index], index, 0);
        }
        background.Controls.Add(layout);
        return background;
    }

    private Panel BuildStatusBar()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            Padding = new Padding(22, 8, 22, 8),
            BackColor = Color.White
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.White
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 205));

        _connectionStatus.Dock = DockStyle.Fill;
        _connectionStatus.Text = "Starting POS Controller…";
        _connectionStatus.ForeColor = Color.FromArgb(83, 97, 109);
        _connectionStatus.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _connectionStatus.TextAlign = ContentAlignment.MiddleLeft;

        _checkUpdateButton.Text = "CHECK FOR UPDATES";
        _checkUpdateButton.Dock = DockStyle.Fill;
        _checkUpdateButton.Margin = Padding.Empty;
        _checkUpdateButton.BackColor = Color.FromArgb(8, 119, 189);
        _checkUpdateButton.ForeColor = Color.White;
        _checkUpdateButton.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _checkUpdateButton.FlatStyle = FlatStyle.Flat;
        _checkUpdateButton.FlatAppearance.BorderSize = 0;
        _checkUpdateButton.Cursor = Cursors.Hand;
        _checkUpdateButton.UseVisualStyleBackColor = false;
        _checkUpdateButton.Click += async (_, _) => await CheckForPosUpdateAsync();

        layout.Controls.Add(_connectionStatus, 0, 0);
        layout.Controls.Add(_checkUpdateButton, 1, 0);
        bar.Controls.Add(layout);
        return bar;
    }

    private async Task CheckForPosUpdateAsync()
    {
        if (_updateCheckInProgress)
            return;

        _updateCheckInProgress = true;
        _checkUpdateButton.Enabled = false;
        try
        {
            if (PosUpdater.HasStagedUpdate)
            {
                PromptToInstallPosUpdate(
                    "A downloaded POS Controller update is ready to install.");
                return;
            }

            _checkUpdateButton.Text = "CHECKING…";
            var result = await PosUpdater.CheckAndStageUpdateAsync();
            if (IsDisposed)
                return;

            if (result.Status == PosUpdateStatus.ReadyToInstall)
            {
                PromptToInstallPosUpdate(result.Message);
                return;
            }

            MessageBox.Show(this, result.Message, "POS Controller Update",
                MessageBoxButtons.OK,
                result.Status == PosUpdateStatus.Failed
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);
        }
        finally
        {
            _updateCheckInProgress = false;
            if (!IsDisposed)
            {
                _checkUpdateButton.Text = PosUpdater.HasStagedUpdate
                    ? "INSTALL UPDATE"
                    : "CHECK FOR UPDATES";
                _checkUpdateButton.Enabled = true;
            }
        }
    }

    private void PromptToInstallPosUpdate(string message)
    {
        var answer = MessageBox.Show(this,
            message + "\n\nInstall it now? The POS Controller will close and restart automatically.",
            "Install POS Controller Update",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            return;

        _checkUpdateButton.Text = "INSTALLING…";
        var result = PosUpdater.ApplyStagedUpdateAndRestart();
        if (!IsDisposed && result.Status != PosUpdateStatus.Applying)
        {
            MessageBox.Show(this, result.Message, "POS Controller Update",
                MessageBoxButtons.OK,
                result.Status == PosUpdateStatus.Failed
                    ? MessageBoxIcon.Warning
                    : MessageBoxIcon.Information);
        }
    }

    private async Task RefreshStatusesAsync()
    {
        if (_refreshInProgress)
            return;
        if (!_settings.HasConnectionSettings)
        {
            UpdateUnlinkedCards();
            SetConnectionStatus("Open Settings to connect this POS Controller to the waiver kiosks.", false);
            return;
        }

        _refreshInProgress = true;
        try
        {
            var client = new PosControllerClient(_settings.ControllerUrl, _settings.PairingKey);
            var response = await client.GetStatusAsync();
            var previousSlots = _settings.KioskSlots.ToArray();
            var added = _settings.RememberSuccessfulConnection(
                _settings.ControllerUrl,
                _settings.PairingKey,
                response.Kiosks);
            if (added > 0 || !previousSlots.SequenceEqual(_settings.KioskSlots, StringComparer.Ordinal))
            {
                UpdateUnlinkedCards();
                PosLog.Write(
                    $"Automatically added {added} kiosk device{(added == 1 ? "" : "s")} from the Kiosk Controller.");
            }
            var byId = response.Kiosks.ToDictionary(kiosk => kiosk.StationId, StringComparer.Ordinal);
            for (var slot = 0; slot < _cards.Length; slot++)
            {
                var stationId = _settings.KioskSlots[slot];
                if (string.IsNullOrWhiteSpace(stationId))
                    _cards[slot].ShowUnlinked();
                else if (byId.TryGetValue(stationId, out var kiosk))
                    _cards[slot].ShowStatus(kiosk);
                else
                    _cards[slot].ShowMissing();
            }
            SetConnectionStatus(
                added > 0
                    ? $"Connected • {added} new kiosk device{(added == 1 ? "" : "s")} added automatically"
                    : $"Connected to the Kiosk Controller • Last update {DateTime.Now:h:mm:ss tt}",
                true);
        }
        catch (Exception ex)
        {
            foreach (var card in _cards)
            {
                if (card.IsLinked)
                    card.ShowControllerUnavailable();
            }
            SetConnectionStatus("Kiosk Controller unavailable — " + ex.Message, false);
            PosLog.Write("Status refresh failed: " + ex.Message);
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private async Task SendCommandAsync(int slot, string commandType, bool? closed = null)
    {
        if (slot < 0 || slot >= _settings.KioskSlots.Count)
            return;
        var stationId = _settings.KioskSlots[slot];
        if (string.IsNullOrWhiteSpace(stationId))
        {
            MessageBox.Show(this,
                $"Kiosk {slot + 1} is not linked. Open Settings to assign a waiver kiosk.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cards[slot].SetBusy(true);
        try
        {
            var client = new PosControllerClient(_settings.ControllerUrl, _settings.PairingKey);
            var result = await client.SendCommandAsync(stationId, commandType, closed);
            if (!result.Accepted)
                throw new InvalidOperationException(result.Message);

            if (commandType == PosCommandTypes.SetClosed && closed == true)
                _cards[slot].ShowPendingState(open: false, "Closing waiver station…");
            else if (commandType == PosCommandTypes.SetClosed)
                _cards[slot].ShowPendingState(open: true, "Putting waiver station in service…");
            else
                _cards[slot].ShowPendingMessage("Resetting to the starting page…");

            SetConnectionStatus($"Command sent to Kiosk {slot + 1}.", true);
        }
        catch (Exception ex)
        {
            _cards[slot].ShowCommandError(ex.Message);
            SetConnectionStatus($"Kiosk {slot + 1} command failed — {ex.Message}", false);
            PosLog.Write($"Kiosk {slot + 1} command failed: {ex.Message}");
        }
        finally
        {
            _cards[slot].SetBusy(false);
        }
    }

    private void OpenSettings()
    {
        using var pin = new PinEntryDialog();
        if (pin.ShowDialog(this) != DialogResult.OK)
            return;
        if (!_settings.VerifyPin(pin.Pin))
        {
            MessageBox.Show(this,
                "The Settings passcode was not correct.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            PosLog.Write("Incorrect POS settings passcode entered.");
            return;
        }

        using var dialog = new PosSettingsDialog(_settings);
        var result = dialog.ShowDialog(this);
        if (result != DialogResult.OK && dialog.AppliedSettings is null)
            return;
        _settings.CopyFrom(result == DialogResult.OK
            ? dialog.Settings
            : dialog.AppliedSettings!);
        UpdateUnlinkedCards();
        _ = RefreshStatusesAsync();
    }

    private void UpdateUnlinkedCards()
    {
        for (var slot = 0; slot < _cards.Length; slot++)
        {
            _cards[slot].IsLinked = _settings.KioskSlots.Count > slot &&
                                    !string.IsNullOrWhiteSpace(_settings.KioskSlots[slot]);
            if (!_cards[slot].IsLinked)
                _cards[slot].ShowUnlinked();
        }
    }

    private void SetConnectionStatus(string text, bool connected)
    {
        _connectionStatus.Text = text;
        _connectionStatus.ForeColor = connected
            ? Color.FromArgb(44, 116, 29)
            : Color.FromArgb(187, 34, 46);
    }

    private static Image? LoadLogo()
    {
        try
        {
            using var stream = typeof(PosControllerForm).Assembly
                .GetManifestResourceStream("MulletHopPosController.Assets.MulletHopFish.png");
            if (stream is null)
                return null;
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class KioskControlCard : Panel
{
    private readonly StatusLight _red = new(Color.FromArgb(244, 34, 48), Color.FromArgb(80, 25, 29));
    private readonly StatusLight _green = new(Color.FromArgb(38, 205, 91), Color.FromArgb(23, 75, 42));
    private readonly Label _status = new();
    private readonly Button _close = new();
    private readonly Button _open = new();
    private readonly Button _reset = new();

    public event EventHandler? CloseRequested;
    public event EventHandler? OpenRequested;
    public event EventHandler? ResetRequested;
    public bool IsLinked { get; set; }

    public KioskControlCard(int kioskNumber)
    {
        Dock = DockStyle.Fill;
        Margin = new Padding(10);
        Padding = new Padding(18);
        BackColor = Color.White;
        BorderStyle = BorderStyle.FixedSingle;

        var title = new Label
        {
            Text = $"KIOSK {kioskNumber}",
            Dock = DockStyle.Top,
            Height = 54,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(16, 24, 32)
        };
        var lightPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 76,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(58, 8, 58, 8)
        };
        lightPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lightPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _red.Dock = DockStyle.Fill;
        _red.Margin = new Padding(4);
        _green.Dock = DockStyle.Fill;
        _green.Margin = new Padding(4);
        lightPanel.Controls.Add(_red, 0, 0);
        lightPanel.Controls.Add(_green, 1, 0);

        _status.Dock = DockStyle.Top;
        _status.Height = 74;
        _status.Padding = new Padding(8);
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _status.ForeColor = Color.FromArgb(83, 97, 109);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(10, 12, 10, 10)
        };
        for (var row = 0; row < 3; row++)
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
        ConfigureButton(_close, "CLOSE STATION", Color.FromArgb(245, 130, 32), Color.White);
        ConfigureButton(_open, "PUT IN SERVICE", Color.FromArgb(239, 42, 55), Color.White);
        ConfigureButton(_reset, "RESET TO START", Color.FromArgb(8, 119, 189), Color.White);
        _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        _open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _reset.Click += (_, _) => ResetRequested?.Invoke(this, EventArgs.Empty);
        actions.Controls.Add(_close, 0, 0);
        actions.Controls.Add(_open, 0, 1);
        actions.Controls.Add(_reset, 0, 2);

        Controls.Add(actions);
        Controls.Add(_status);
        Controls.Add(lightPanel);
        Controls.Add(title);
    }

    public void ShowStatus(PosKioskStatus kiosk)
    {
        IsLinked = true;
        var open = kiosk.IsOnline && kiosk.AvailableForGuests && !kiosk.HasError;
        _green.Active = open;
        _red.Active = !open;
        _status.Text = kiosk.IsOnline
            ? (string.IsNullOrWhiteSpace(kiosk.StatusMessage)
                ? (open ? "Online and open to guests" : "Waiver station unavailable")
                : kiosk.StatusMessage)
            : "Waiver kiosk is offline";
        _status.ForeColor = open ? Color.FromArgb(44, 116, 29) : Color.FromArgb(187, 34, 46);
        SetButtonsEnabled(true);
    }

    public void ShowUnlinked()
    {
        IsLinked = false;
        _red.Active = false;
        _green.Active = false;
        _status.Text = "Not linked\nOpen Settings to assign a kiosk";
        _status.ForeColor = Color.FromArgb(83, 97, 109);
        SetButtonsEnabled(false);
    }

    public void ShowMissing()
    {
        IsLinked = true;
        _red.Active = true;
        _green.Active = false;
        _status.Text = "Linked kiosk not found by controller";
        _status.ForeColor = Color.FromArgb(187, 34, 46);
        SetButtonsEnabled(true);
    }

    public void ShowControllerUnavailable()
    {
        _red.Active = true;
        _green.Active = false;
        _status.Text = "Kiosk status unavailable";
        _status.ForeColor = Color.FromArgb(187, 34, 46);
    }

    public void ShowPendingState(bool open, string message)
    {
        _red.Active = !open;
        _green.Active = open;
        ShowPendingMessage(message);
    }

    public void ShowPendingMessage(string message)
    {
        _status.Text = message;
        _status.ForeColor = Color.FromArgb(125, 77, 9);
    }

    public void ShowCommandError(string message)
    {
        _red.Active = true;
        _green.Active = false;
        _status.Text = "Command failed\n" + message;
        _status.ForeColor = Color.FromArgb(187, 34, 46);
    }

    public void SetBusy(bool busy) => SetButtonsEnabled(IsLinked && !busy);

    private void SetButtonsEnabled(bool enabled)
    {
        _close.Enabled = enabled;
        _open.Enabled = enabled;
        _reset.Enabled = enabled;
    }

    private static void ConfigureButton(Button button, string text, Color background, Color foreground)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(5, 7, 5, 7);
        button.BackColor = background;
        button.ForeColor = foreground;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }
}

internal sealed class StatusLight : Control
{
    private readonly Color _onColor;
    private readonly Color _offColor;
    private bool _active;

    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
                return;
            _active = value;
            Invalidate();
        }
    }

    public StatusLight(Color onColor, Color offColor)
    {
        _onColor = onColor;
        _offColor = offColor;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        DoubleBuffered = true;
        MinimumSize = new Size(38, 38);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var size = Math.Max(12, Math.Min(ClientSize.Width, ClientSize.Height) - 8);
        var circle = new Rectangle(
            (ClientSize.Width - size) / 2,
            (ClientSize.Height - size) / 2,
            size,
            size);
        if (Active)
        {
            using var glow = new SolidBrush(Color.FromArgb(55, _onColor));
            var glowRectangle = Rectangle.Inflate(circle, 4, 4);
            e.Graphics.FillEllipse(glow, glowRectangle);
        }
        using var fill = new SolidBrush(Active ? _onColor : _offColor);
        using var outline = new Pen(Active ? ControlPaint.Light(_onColor) : ControlPaint.Light(_offColor), 2.5f);
        e.Graphics.FillEllipse(fill, circle);
        e.Graphics.DrawEllipse(outline, circle);
        var highlight = new Rectangle(circle.X + circle.Width / 5, circle.Y + circle.Height / 6,
            Math.Max(3, circle.Width / 4), Math.Max(3, circle.Height / 5));
        using var shine = new SolidBrush(Color.FromArgb(Active ? 95 : 35, Color.White));
        e.Graphics.FillEllipse(shine, highlight);
    }
}
