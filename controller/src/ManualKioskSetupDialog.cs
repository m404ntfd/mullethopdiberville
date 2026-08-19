using MulletHop.KioskDiscovery;

namespace MulletHopKioskController;

internal sealed class ManualKioskSetupDialog : Form
{
    private readonly ControllerState _state;
    private readonly HashSet<string> _knownStationIds;
    private readonly TextBox _setupCode = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1_000 };

    public ManualKioskSetupDialog(
        ControllerState state,
        string controllerAddress)
    {
        _state = state;
        _knownStationIds = state.Snapshot()
            .Select(kiosk => kiosk.StationId)
            .ToHashSet(StringComparer.Ordinal);

        Text = "Add Kiosk Manually";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(760, 545);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "ADD A WAIVER KIOSK MANUALLY",
            Bounds = new Rectangle(25, 18, 710, 43),
            Font = new Font("Segoe UI", 19, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var explanation = new Label
        {
            AutoSize = false,
            Text = "Use this fallback when Discover Kiosks does not find the waiver station. The kiosk still connects outbound and is saved by its stable Device ID—not by its DHCP address or MAC address.",
            Bounds = new Rectangle(45, 66, 670, 60),
            ForeColor = Color.FromArgb(52, 65, 76),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var steps = new Label
        {
            AutoSize = false,
            Text = "1. Copy the setup code below.\r\n" +
                   "2. On the waiver kiosk, open Staff Settings > Remote Control Options.\r\n" +
                   "3. Paste the code under Manual Connection Fallback and select Connect and Save.",
            Bounds = new Rectangle(50, 136, 660, 78),
            ForeColor = Color.FromArgb(16, 24, 32),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        var addressLabel = new Label
        {
            AutoSize = true,
            Text = "Controller address included in this code:",
            Location = new Point(50, 222),
            ForeColor = Color.FromArgb(8, 119, 189),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        var address = new TextBox
        {
            Text = controllerAddress,
            ReadOnly = true,
            Bounds = new Rectangle(50, 247, 660, 31),
            Font = new Font("Consolas", 9.5f)
        };
        var codeLabel = new Label
        {
            AutoSize = true,
            Text = "Manual setup code:",
            Location = new Point(50, 292),
            ForeColor = Color.FromArgb(8, 119, 189),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        _setupCode.Text = KioskDiscoveryProtocol.CreateManualSetupCode(
            controllerAddress,
            state.PairingKey,
            Environment.MachineName);
        _setupCode.ReadOnly = true;
        _setupCode.Multiline = true;
        _setupCode.WordWrap = true;
        _setupCode.ScrollBars = ScrollBars.Vertical;
        _setupCode.Bounds = new Rectangle(50, 317, 660, 72);
        _setupCode.Font = new Font("Consolas", 8.5f);

        var copy = new Button
        {
            Text = "Copy Setup Code",
            Bounds = new Rectangle(50, 405, 190, 44),
            BackColor = Color.FromArgb(245, 130, 32),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        copy.Click += (_, _) => CopySetupCode();
        var close = new Button
        {
            Text = "Close",
            Bounds = new Rectangle(520, 405, 190, 44),
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        _status.AutoSize = false;
        _status.Text = "Waiting for a new kiosk to complete its first secure check-in…";
        _status.Bounds = new Rectangle(50, 463, 660, 54);
        _status.ForeColor = Color.FromArgb(83, 97, 109);
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Font = new Font("Segoe UI", 9.2f, FontStyle.Bold);

        CancelButton = close;
        Controls.AddRange([
            heading, explanation, steps, addressLabel, address,
            codeLabel, _setupCode, copy, close, _status]);
        _refreshTimer.Tick += (_, _) => LookForNewKiosk();
        Shown += (_, _) => _refreshTimer.Start();
        FormClosed += (_, _) => _refreshTimer.Stop();
        ControllerTheme.Apply(this);
    }

    private void CopySetupCode()
    {
        try
        {
            Clipboard.SetText(_setupCode.Text);
            _status.Text = "Setup code copied. Paste it into Remote Control Options on the waiver kiosk.";
            _status.ForeColor = ControllerTheme.AccentText;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "The setup code could not be copied.\n\n" + ex.Message,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void LookForNewKiosk()
    {
        var kiosk = _state.Snapshot().FirstOrDefault(item =>
            !_knownStationIds.Contains(item.StationId) && item.IsOnline);
        if (kiosk is null)
            return;

        _knownStationIds.Add(kiosk.StationId);
        _status.Text =
            $"Connected and saved: {kiosk.StationName} ({kiosk.MachineName}) at {kiosk.LastIpAddress}.";
        _status.ForeColor = ControllerTheme.SuccessText;
        ControllerLog.Write(
            $"Manual setup completed for {kiosk.StationName} using stable Device ID {kiosk.StationId}.");
    }
}
