namespace MulletHopKioskController;

internal sealed class KioskDiscoveryDialog : Form
{
    private const int ScanDurationSeconds = 15;
    private readonly KioskDiscoveryCoordinator _discovery;
    private readonly ControllerState _state;
    private readonly ListView _devices = new();
    private readonly Label _status = new();
    private readonly Button _scanNetwork = new();
    private readonly Button _requestAdd = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1_000 };
    private readonly HashSet<string> _reportedPairings = new(StringComparer.Ordinal);
    private DateTime? _scanStartedUtc;
    private DateTime? _scanEndsUtc;
    private bool _scanCompletionReported;

    public KioskDiscoveryDialog(KioskDiscoveryCoordinator discovery, ControllerState state)
    {
        _discovery = discovery;
        _state = state;
        Text = "Discover Waiver Kiosks";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 520);
        ClientSize = new Size(1_080, 620);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 248, 251);

        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(20, 14, 20, 0),
            Text = "SCAN THIS NETWORK FOR WAIVER KIOSKS",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            BackColor = Color.White
        };
        var instructions = new Label
        {
            Dock = DockStyle.Top,
            Height = 62,
            Padding = new Padding(20, 6, 20, 8),
            Text = "Select Scan Network to run a fresh 15-second search for kiosks with Remote Control Options enabled. " +
                   "Select a result and choose Request Add; someone must then press Yes on that kiosk within two minutes.",
            ForeColor = Color.FromArgb(52, 65, 76),
            BackColor = Color.White
        };

        ConfigureDeviceList();
        var footer = BuildFooter();
        Controls.Add(_devices);
        Controls.Add(footer);
        Controls.Add(instructions);
        Controls.Add(heading);

        _devices.SelectedIndexChanged += (_, _) => UpdateRequestButton();
        _devices.DoubleClick += (_, _) => RequestSelectedKiosk();
        _refreshTimer.Tick += (_, _) => RefreshDevices();
        Shown += (_, _) =>
        {
            _refreshTimer.Start();
            StartNetworkScan();
        };
        FormClosed += (_, _) => _refreshTimer.Stop();
        ControllerTheme.Apply(this);
    }

    private void ConfigureDeviceList()
    {
        _devices.Dock = DockStyle.Fill;
        _devices.Margin = Padding.Empty;
        _devices.View = View.Details;
        _devices.FullRowSelect = true;
        _devices.MultiSelect = false;
        _devices.HideSelection = false;
        _devices.GridLines = true;
        _devices.Columns.Add("Status", 175);
        _devices.Columns.Add("Kiosk Name", 170);
        _devices.Columns.Add("PC Name", 150);
        _devices.Columns.Add("Version", 80);
        _devices.Columns.Add("IP Address", 125);
        _devices.Columns.Add("Current Controller", 225);
        _devices.Columns.Add("Last Seen", 110);
    }

    private Panel BuildFooter()
    {
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 92,
            Padding = new Padding(20, 10, 20, 12),
            BackColor = Color.White
        };
        _status.Dock = DockStyle.Fill;
        _status.Text = "Waiting for waiver kiosks to announce themselves…";
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.FromArgb(52, 65, 76);
        _status.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);

        var close = new Button
        {
            Text = "Close",
            Dock = DockStyle.Right,
            Width = 110,
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(235, 238, 241),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(8, 0, 0, 0)
        };
        _requestAdd.Text = "Request Add";
        _requestAdd.Dock = DockStyle.Right;
        _requestAdd.Width = 150;
        _requestAdd.Enabled = false;
        _requestAdd.BackColor = Color.FromArgb(245, 130, 32);
        _requestAdd.ForeColor = Color.White;
        _requestAdd.FlatStyle = FlatStyle.Flat;
        _requestAdd.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _requestAdd.Click += (_, _) => RequestSelectedKiosk();

        _scanNetwork.Text = "Scan Network";
        _scanNetwork.Dock = DockStyle.Right;
        _scanNetwork.Width = 145;
        _scanNetwork.BackColor = Color.FromArgb(8, 119, 189);
        _scanNetwork.ForeColor = Color.White;
        _scanNetwork.FlatStyle = FlatStyle.Flat;
        _scanNetwork.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _scanNetwork.Click += (_, _) => StartNetworkScan();

        footer.Controls.Add(_status);
        footer.Controls.Add(close);
        footer.Controls.Add(_requestAdd);
        footer.Controls.Add(_scanNetwork);
        CancelButton = close;
        return footer;
    }

    private void RefreshDevices()
    {
        var selectedId = SelectedStationId();
        var savedIds = _state.Snapshot()
            .Select(kiosk => kiosk.StationId)
            .ToHashSet(StringComparer.Ordinal);
        var discovered = _discovery.Snapshot()
            .Where(kiosk => !_scanStartedUtc.HasValue ||
                            kiosk.LastSeenUtc >= _scanStartedUtc.Value)
            .ToList();

        _devices.BeginUpdate();
        try
        {
            _devices.Items.Clear();
            foreach (var kiosk in discovered)
            {
                var isSaved = savedIds.Contains(kiosk.StationId);
                var (statusText, color) = DescribeStatus(kiosk, isSaved);
                var item = new ListViewItem(statusText)
                {
                    Tag = kiosk.StationId,
                    ForeColor = color,
                    BackColor = isSaved
                        ? ControllerTheme.OnlineRow
                        : DateTime.UtcNow - kiosk.LastSeenUtc > TimeSpan.FromSeconds(30)
                            ? ControllerTheme.OfflineRow
                            : ControllerTheme.InputBackground
                };
                item.SubItems.Add(kiosk.StationName);
                item.SubItems.Add(kiosk.MachineName);
                item.SubItems.Add(kiosk.Version);
                item.SubItems.Add(kiosk.IpAddress);
                item.SubItems.Add(string.IsNullOrWhiteSpace(kiosk.CurrentController)
                    ? "Not connected"
                    : kiosk.CurrentController);
                item.SubItems.Add(FormatLastSeen(kiosk.LastSeenUtc));
                _devices.Items.Add(item);
                if (string.Equals(kiosk.StationId, selectedId, StringComparison.Ordinal))
                    item.Selected = true;

                if (kiosk.PairingState == DiscoveryPairingState.Accepted &&
                    isSaved &&
                    !string.IsNullOrWhiteSpace(kiosk.PairingRequestId) &&
                    _reportedPairings.Add(kiosk.PairingRequestId))
                {
                    _status.Text =
                        $"{kiosk.StationName} was approved and saved. Linked Kiosk Status Viewers will add and save it automatically.";
                    _status.ForeColor = ControllerTheme.SuccessText;
                }
                else if (kiosk.PairingState is DiscoveryPairingState.Declined or
                         DiscoveryPairingState.Failed or
                         DiscoveryPairingState.Expired &&
                         !string.IsNullOrWhiteSpace(kiosk.PairingRequestId) &&
                         _reportedPairings.Add(kiosk.PairingRequestId))
                {
                    _status.Text = kiosk.StationName + ": " + kiosk.PairingMessage;
                    _status.ForeColor = ControllerTheme.ErrorText;
                }
            }
        }
        finally
        {
            _devices.EndUpdate();
        }

        if (IsScanInProgress())
        {
            var secondsRemaining = Math.Max(
                1,
                (int)Math.Ceiling((_scanEndsUtc!.Value - DateTime.UtcNow).TotalSeconds));
            _scanNetwork.Text = $"Scanning… {secondsRemaining}s";
            _status.Text = discovered.Count == 0
                ? "Scanning the local network for enabled waiver kiosks…"
                : $"Scanning… found {discovered.Count} kiosk{(discovered.Count == 1 ? "" : "s")} so far.";
            _status.ForeColor = ControllerTheme.AccentText;
        }
        else if (_scanEndsUtc.HasValue && !_scanCompletionReported)
        {
            _scanCompletionReported = true;
            _scanNetwork.Text = "Scan Again";
            _scanNetwork.Enabled = true;
            _status.Text = discovered.Count == 0
                ? "Scan complete. No kiosks responded. Make sure Remote Control Options is enabled on each kiosk and all computers are on the same private network."
                : $"Scan complete. Found {discovered.Count} kiosk{(discovered.Count == 1 ? "" : "s")}. Select one, then choose Request Add.";
            _status.ForeColor = discovered.Count == 0
                ? ControllerTheme.ErrorText
                : ControllerTheme.SuccessText;
            ControllerLog.Write(
                $"Manual kiosk network scan completed with {discovered.Count} result{(discovered.Count == 1 ? "" : "s")}.");
        }
        else if (discovered.Count == 0 && !_scanStartedUtc.HasValue)
        {
            _status.Text = "Select Scan Network to look for waiver kiosks on this private network.";
            _status.ForeColor = ControllerTheme.MutedText;
        }
        UpdateRequestButton();
    }

    private void StartNetworkScan()
    {
        _scanStartedUtc = DateTime.UtcNow;
        _scanEndsUtc = _scanStartedUtc.Value.AddSeconds(ScanDurationSeconds);
        _scanCompletionReported = false;
        _scanNetwork.Enabled = false;
        _scanNetwork.Text = $"Scanning… {ScanDurationSeconds}s";
        _status.Text = "Scanning the local network for enabled waiver kiosks…";
        _status.ForeColor = ControllerTheme.AccentText;
        _devices.Items.Clear();
        _requestAdd.Enabled = false;
        ControllerLog.Write("Manual kiosk network scan started.");
        RefreshDevices();
    }

    private bool IsScanInProgress() =>
        _scanEndsUtc.HasValue && _scanEndsUtc.Value > DateTime.UtcNow;

    private void StopScanForPairing()
    {
        if (!_scanEndsUtc.HasValue)
            return;
        _scanEndsUtc = DateTime.UtcNow;
        _scanCompletionReported = true;
        _scanNetwork.Enabled = true;
        _scanNetwork.Text = "Scan Again";
    }

    private void RequestSelectedKiosk()
    {
        var stationId = SelectedStationId();
        if (stationId is null)
            return;
        var result = _discovery.QueuePairing(stationId);
        StopScanForPairing();
        _status.Text = result.Success
            ? "Request sent. Go to the selected waiver kiosk and press Yes within two minutes."
            : result.Message;
        _status.ForeColor = result.Success
            ? ControllerTheme.WarningText
            : ControllerTheme.ErrorText;
        RefreshDevices();
    }

    private void UpdateRequestButton()
    {
        var stationId = SelectedStationId();
        if (stationId is null)
        {
            _requestAdd.Enabled = false;
            return;
        }

        var isSaved = _state.Snapshot().Any(kiosk => kiosk.StationId == stationId);
        var discovered = _discovery.Snapshot()
            .FirstOrDefault(kiosk => kiosk.StationId == stationId);
        _requestAdd.Enabled = !isSaved &&
                              discovered is not null &&
                              DateTime.UtcNow - discovered.LastSeenUtc <= TimeSpan.FromSeconds(30) &&
                              discovered.PairingState != DiscoveryPairingState.WaitingForKiosk;
        _requestAdd.Text = discovered?.PairingState == DiscoveryPairingState.WaitingForKiosk
            ? "Waiting on Kiosk…"
            : "Request Add";
    }

    private static (string Text, Color Color) DescribeStatus(
        DiscoveredKiosk kiosk,
        bool isSaved)
    {
        if (isSaved)
            return ("● Added and Saved", ControllerTheme.SuccessText);
        if (DateTime.UtcNow - kiosk.LastSeenUtc > TimeSpan.FromSeconds(30))
            return ("○ No Longer Responding", ControllerTheme.OfflineText);
        return kiosk.PairingState switch
        {
            DiscoveryPairingState.WaitingForKiosk =>
                ("◐ Awaiting Approval", ControllerTheme.WarningText),
            DiscoveryPairingState.Accepted =>
                ("◐ Approved; Connecting", ControllerTheme.SuccessText),
            DiscoveryPairingState.Declined =>
                ("● Declined", ControllerTheme.ErrorText),
            DiscoveryPairingState.Failed =>
                ("● Pairing Failed", ControllerTheme.ErrorText),
            DiscoveryPairingState.Expired =>
                ("○ Request Expired", ControllerTheme.OfflineText),
            _ when kiosk.IsManaged =>
                ("● Managed Elsewhere", ControllerTheme.WarningText),
            _ => ("● Available", ControllerTheme.AccentText)
        };
    }

    private string? SelectedStationId() =>
        _devices.SelectedItems.Count == 0
            ? null
            : _devices.SelectedItems[0].Tag as string;

    private static string FormatLastSeen(DateTime lastSeenUtc)
    {
        var age = DateTime.UtcNow - lastSeenUtc;
        if (age < TimeSpan.FromSeconds(3)) return "Just now";
        if (age < TimeSpan.FromMinutes(1)) return $"{Math.Max(1, (int)age.TotalSeconds)} sec ago";
        return lastSeenUtc.ToLocalTime().ToString("h:mm:ss tt");
    }
}
