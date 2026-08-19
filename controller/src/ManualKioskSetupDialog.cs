using System.Net;
using System.Net.Sockets;
using MulletHop.KioskDiscovery;

namespace MulletHopKioskController;

internal sealed class ManualKioskSetupDialog : Form
{
    private readonly ControllerState _state;
    private readonly KioskDiscoveryCoordinator _discovery;
    private readonly HashSet<string> _knownStationIds;
    private readonly TextBox _ipAddress = new();
    private readonly Button _requestByIp = new();
    private readonly Label _ipStatus = new();
    private readonly TextBox _setupCode = new();
    private readonly Label _setupStatus = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1_000 };
    private string _ipRequestId = string.Empty;
    private DateTime _ipRequestExpiresUtc;

    public ManualKioskSetupDialog(
        ControllerState state,
        KioskDiscoveryCoordinator discovery,
        string controllerAddress)
    {
        _state = state;
        _discovery = discovery;
        _knownStationIds = state.Snapshot()
            .Select(kiosk => kiosk.StationId)
            .ToHashSet(StringComparer.Ordinal);

        Text = "Add Kiosk Manually";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(780, 735);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "ADD A WAIVER KIOSK MANUALLY",
            Bounds = new Rectangle(25, 15, 730, 43),
            Font = new Font("Segoe UI", 19, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var explanation = new Label
        {
            AutoSize = false,
            Text = "Enter the kiosk's IPv4 address for the easiest manual connection. The secure keys are exchanged automatically and the kiosk is permanently saved by its Device ID after someone approves the request on its screen.",
            Bounds = new Rectangle(45, 62, 690, 58),
            ForeColor = Color.FromArgb(52, 65, 76),
            TextAlign = ContentAlignment.MiddleCenter
        };

        var ipGroup = new GroupBox
        {
            Text = "Recommended — Add by IP Address (No Code Required)",
            Bounds = new Rectangle(30, 128, 720, 240),
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189)
        };
        var ipNote = new Label
        {
            AutoSize = false,
            Text = "Use the IPv4 address shown on the kiosk under Staff Settings > Remote Control Options. Remote control must be enabled on the kiosk.",
            Bounds = new Rectangle(18, 26, 684, 48),
            ForeColor = Color.FromArgb(52, 65, 76),
            Font = new Font("Segoe UI", 9.2f),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var ipLabel = new Label
        {
            AutoSize = true,
            Text = "Kiosk IPv4 Address:",
            Location = new Point(18, 91),
            ForeColor = Color.FromArgb(16, 24, 32),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        _ipAddress.Bounds = new Rectangle(166, 84, 220, 32);
        _ipAddress.Font = new Font("Consolas", 10);
        _ipAddress.MaxLength = 45;
        _ipAddress.PlaceholderText = "Example: 192.168.1.25";
        _requestByIp.Text = "Send Secure Request";
        _requestByIp.Bounds = new Rectangle(407, 80, 275, 42);
        _requestByIp.BackColor = Color.FromArgb(245, 130, 32);
        _requestByIp.ForeColor = Color.White;
        _requestByIp.FlatStyle = FlatStyle.Flat;
        _requestByIp.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _requestByIp.Click += (_, _) => RequestPairingByIp();
        _ipStatus.AutoSize = false;
        _ipStatus.Text = "No short code is needed. The kiosk contacts this controller, receives an encrypted pairing offer, and asks for approval.";
        _ipStatus.Bounds = new Rectangle(18, 135, 664, 76);
        _ipStatus.ForeColor = Color.FromArgb(83, 97, 109);
        _ipStatus.TextAlign = ContentAlignment.MiddleLeft;
        _ipStatus.Font = new Font("Segoe UI", 9.1f, FontStyle.Bold);
        ipGroup.Controls.AddRange([
            ipNote, ipLabel, _ipAddress, _requestByIp, _ipStatus]);

        var fallbackGroup = new GroupBox
        {
            Text = "Setup Code Fallback",
            Bounds = new Rectangle(30, 382, 720, 280),
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(245, 130, 32)
        };
        var fallbackNote = new Label
        {
            AutoSize = false,
            Text = "If IP pairing cannot communicate, copy this setup code and paste it into Remote Control Options on the kiosk. This self-contained code is longer because it securely carries the controller address and full pairing key.",
            Bounds = new Rectangle(18, 25, 684, 53),
            ForeColor = Color.FromArgb(52, 65, 76),
            Font = new Font("Segoe UI", 9.1f),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var addressLabel = new Label
        {
            AutoSize = true,
            Text = "Controller address included in the code:",
            Location = new Point(18, 88),
            ForeColor = Color.FromArgb(8, 119, 189),
            Font = new Font("Segoe UI", 9.2f, FontStyle.Bold)
        };
        var address = new TextBox
        {
            Text = controllerAddress,
            ReadOnly = true,
            Bounds = new Rectangle(18, 112, 684, 30),
            Font = new Font("Consolas", 9.2f)
        };
        _setupCode.Text = KioskDiscoveryProtocol.CreateManualSetupCode(
            controllerAddress,
            state.PairingKey,
            Environment.MachineName);
        _setupCode.ReadOnly = true;
        _setupCode.Multiline = true;
        _setupCode.WordWrap = true;
        _setupCode.ScrollBars = ScrollBars.Vertical;
        _setupCode.Bounds = new Rectangle(18, 154, 480, 70);
        _setupCode.Font = new Font("Consolas", 8.3f);

        var copy = new Button
        {
            Text = "Copy Setup Code",
            Bounds = new Rectangle(516, 164, 166, 48),
            BackColor = Color.FromArgb(245, 130, 32),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.2f, FontStyle.Bold)
        };
        copy.Click += (_, _) => CopySetupCode();
        _setupStatus.AutoSize = false;
        _setupStatus.Text = "Waiting for a kiosk using the fallback code to complete its first secure check-in.";
        _setupStatus.Bounds = new Rectangle(18, 228, 664, 38);
        _setupStatus.ForeColor = Color.FromArgb(83, 97, 109);
        _setupStatus.TextAlign = ContentAlignment.MiddleLeft;
        _setupStatus.Font = new Font("Segoe UI", 8.8f, FontStyle.Bold);
        fallbackGroup.Controls.AddRange([
            fallbackNote, addressLabel, address, _setupCode, copy, _setupStatus]);

        var close = new Button
        {
            Text = "Close",
            Bounds = new Rectangle(560, 677, 190, 44),
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };

        CancelButton = close;
        Controls.AddRange([heading, explanation, ipGroup, fallbackGroup, close]);
        _refreshTimer.Tick += (_, _) => RefreshPairingStatus();
        Shown += (_, _) => _refreshTimer.Start();
        FormClosed += (_, _) => _refreshTimer.Stop();
        ControllerTheme.Apply(this);
    }

    private void RequestPairingByIp()
    {
        if (!IPAddress.TryParse(_ipAddress.Text.Trim(), out var parsedAddress))
        {
            SetIpError("Enter a valid IPv4 address, such as 192.168.1.25.");
            _ipAddress.Focus();
            return;
        }
        if (parsedAddress.IsIPv4MappedToIPv6)
            parsedAddress = parsedAddress.MapToIPv4();
        if (parsedAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            SetIpError("Use the IPv4 address shown in Remote Control Options on the kiosk.");
            _ipAddress.Focus();
            return;
        }

        var normalizedAddress = parsedAddress.ToString();
        var answer = MessageBox.Show(this,
            $"Send a secure pairing request for the waiver kiosk at {normalizedAddress}?\n\n" +
            "Someone at the kiosk must approve the request within two minutes.",
            "Confirm Kiosk IP Address",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        var result = _discovery.QueuePairingByIp(normalizedAddress);
        if (!result.Success)
        {
            SetIpError(result.Message);
            return;
        }

        _ipAddress.Text = normalizedAddress;
        _ipRequestId = result.RequestId;
        _ipRequestExpiresUtc = result.ExpiresUtc;
        _requestByIp.Enabled = false;
        _requestByIp.Text = "Waiting for Kiosk…";
        _ipStatus.Text = result.Message;
        _ipStatus.ForeColor = ControllerTheme.WarningText;
    }

    private void RefreshPairingStatus()
    {
        LookForIpPairing();
        LookForSetupCodeKiosk();
    }

    private void LookForIpPairing()
    {
        if (string.IsNullOrWhiteSpace(_ipRequestId))
            return;

        var kiosk = _discovery.Snapshot().FirstOrDefault(item =>
            string.Equals(item.PairingRequestId, _ipRequestId, StringComparison.Ordinal));
        if (kiosk is null)
        {
            if (DateTime.UtcNow >= _ipRequestExpiresUtc)
            {
                SetIpError(
                    "The kiosk did not contact this controller before the request expired. Verify the IP address, confirm Remote Control Options is enabled, and try again.");
                EndIpRequest();
                return;
            }

            var seconds = Math.Max(1, (int)Math.Ceiling(
                (_ipRequestExpiresUtc - DateTime.UtcNow).TotalSeconds));
            _ipStatus.Text =
                $"Waiting for the kiosk at {_ipAddress.Text} to contact this controller… {seconds} seconds remaining.";
            return;
        }

        var isSaved = _state.Snapshot().Any(item =>
            string.Equals(item.StationId, kiosk.StationId, StringComparison.Ordinal));
        switch (kiosk.PairingState)
        {
            case DiscoveryPairingState.WaitingForKiosk:
                _ipStatus.Text =
                    $"Reached {kiosk.StationName} ({kiosk.MachineName}). Approve the connection on that kiosk within two minutes.";
                _ipStatus.ForeColor = ControllerTheme.WarningText;
                break;
            case DiscoveryPairingState.Accepted when isSaved:
                _ipStatus.Text =
                    $"Connected and permanently saved: {kiosk.StationName} ({kiosk.MachineName}) using Device ID {kiosk.StationId}.";
                _ipStatus.ForeColor = ControllerTheme.SuccessText;
                ControllerLog.Write(
                    $"IP pairing completed for {kiosk.StationName} using stable Device ID {kiosk.StationId}.");
                _knownStationIds.Add(kiosk.StationId);
                EndIpRequest();
                break;
            case DiscoveryPairingState.Accepted:
                _ipStatus.Text =
                    $"{kiosk.StationName} approved the request. Waiting for its first authenticated check-in…";
                _ipStatus.ForeColor = ControllerTheme.SuccessText;
                break;
            case DiscoveryPairingState.Declined:
            case DiscoveryPairingState.Failed:
            case DiscoveryPairingState.Expired:
                SetIpError(kiosk.StationName + ": " + kiosk.PairingMessage);
                EndIpRequest();
                break;
        }
    }

    private void EndIpRequest()
    {
        _ipRequestId = string.Empty;
        _requestByIp.Enabled = true;
        _requestByIp.Text = "Send Secure Request";
    }

    private void SetIpError(string message)
    {
        _ipStatus.Text = message;
        _ipStatus.ForeColor = ControllerTheme.ErrorText;
    }

    private void CopySetupCode()
    {
        try
        {
            Clipboard.SetText(_setupCode.Text);
            _setupStatus.Text =
                "Setup code copied. Paste it into Remote Control Options on the waiver kiosk.";
            _setupStatus.ForeColor = ControllerTheme.AccentText;
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

    private void LookForSetupCodeKiosk()
    {
        var kiosk = _state.Snapshot().FirstOrDefault(item =>
            !_knownStationIds.Contains(item.StationId) && item.IsOnline);
        if (kiosk is null)
            return;

        _knownStationIds.Add(kiosk.StationId);
        _setupStatus.Text =
            $"Connected and saved: {kiosk.StationName} ({kiosk.MachineName}) at {kiosk.LastIpAddress}.";
        _setupStatus.ForeColor = ControllerTheme.SuccessText;
        ControllerLog.Write(
            $"Manual setup completed for {kiosk.StationName} using stable Device ID {kiosk.StationId}.");
    }
}
