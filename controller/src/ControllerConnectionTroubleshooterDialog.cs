namespace MulletHopKioskController;

internal sealed class ControllerConnectionTroubleshooterDialog : Form
{
    private readonly ControllerState _state;
    private readonly ControllerServer _server;
    private readonly TextBox _connectionValue = new();
    private readonly ListView _checks = new();
    private readonly Label _summary = new();
    private readonly Button _runChecks = new();
    private readonly Button _repair = new();
    private readonly Button _close = new();
    private bool _busy;

    public ControllerConnectionTroubleshooterDialog(
        ControllerState state,
        ControllerServer server)
    {
        _state = state;
        _server = server;

        Text = "Systems Controller Connection Troubleshooter";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        MinimumSize = new Size(850, 620);
        ClientSize = new Size(940, 680);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "CONNECTION TROUBLESHOOTER",
            Bounds = new Rectangle(28, 18, 884, 40),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var explanation = new Label
        {
            AutoSize = false,
            Text = "Checks controller identity, discovery, TCP 47832, the Windows network profile, firewall rule, and URL reservation. Diagnose & Repair can request administrator approval, make the local Windows changes, and retry the master connection.",
            Bounds = new Rectangle(42, 64, 856, 60),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = Color.FromArgb(52, 65, 76),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var valueLabel = new Label
        {
            AutoSize = false,
            Text = "Master IPv4 Address or Pairing Key (optional)",
            Bounds = new Rectangle(42, 130, 856, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189)
        };
        _connectionValue.Bounds = new Rectangle(42, 158, 856, 34);
        _connectionValue.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _connectionValue.Font = new Font("Consolas", 10);
        _connectionValue.MaxLength = 1_000;
        _connectionValue.PlaceholderText = "Leave blank to use the saved or automatically detected master";
        if (_state.MasterControllerSnapshot() is { } stored &&
            !string.IsNullOrWhiteSpace(stored.LastKnownAddress))
        {
            _connectionValue.Text = stored.LastKnownAddress;
        }

        _checks.Bounds = new Rectangle(42, 208, 856, 350);
        _checks.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _checks.View = View.Details;
        _checks.FullRowSelect = true;
        _checks.GridLines = true;
        _checks.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _checks.Columns.Add("Status", 105);
        _checks.Columns.Add("Check", 205);
        _checks.Columns.Add("Details", 525);

        _summary.AutoSize = false;
        _summary.Bounds = new Rectangle(42, 566, 856, 38);
        _summary.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _summary.Text = "Select Run Checks for a report, or Diagnose & Repair to fix this PC and retry the master.";
        _summary.ForeColor = Color.FromArgb(52, 65, 76);
        _summary.TextAlign = ContentAlignment.MiddleLeft;

        ConfigureButton(_runChecks, "Run Checks", Color.FromArgb(105, 210, 236), Color.FromArgb(16, 24, 32));
        _runChecks.Bounds = new Rectangle(414, 617, 150, 46);
        _runChecks.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _runChecks.Click += async (_, _) => await RunChecksAsync();

        ConfigureButton(_repair, "Diagnose && Repair", Color.FromArgb(245, 130, 32), Color.White);
        _repair.Bounds = new Rectangle(574, 617, 174, 46);
        _repair.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _repair.Click += async (_, _) => await DiagnoseAndRepairAsync();

        ConfigureButton(_close, "Close", Color.FromArgb(238, 250, 255), Color.FromArgb(16, 24, 32));
        _close.Bounds = new Rectangle(758, 617, 140, 46);
        _close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _close.DialogResult = DialogResult.Cancel;

        CancelButton = _close;
        Controls.AddRange([
            heading, explanation, valueLabel, _connectionValue, _checks,
            _summary, _runChecks, _repair, _close]);
        ControllerTheme.Apply(this);
    }

    private async Task RunChecksAsync()
    {
        if (_busy)
            return;
        SetBusy(true, "Running controller and Windows network checks…");
        try
        {
            var snapshot = await ControllerConnectionDiagnostics.RunAsync(
                _state,
                _server,
                _connectionValue.Text);
            ShowSnapshot(snapshot);
            _summary.Text = snapshot.Checks.Any(check => check.State == ControllerDiagnosticState.Failed)
                ? "One or more checks failed. Diagnose & Repair can correct this PC and retry the connection."
                : "The local checks passed. If the master is still unreachable, run this troubleshooter on the master PC too.";
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller diagnostic error: " + ex.Message);
            ShowFailure("Troubleshooter", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DiagnoseAndRepairAsync()
    {
        if (_busy)
            return;
        SetBusy(true, "Diagnosing the controller connection…");
        try
        {
            var snapshot = await ControllerConnectionDiagnostics.RunAsync(
                _state,
                _server,
                _connectionValue.Text);
            ShowSnapshot(snapshot);

            if (snapshot.LocalRepairRecommended)
            {
                var answer = MessageBox.Show(
                    this,
                    "Windows administrator approval is required to set active Public networks to Private, reserve the Systems Controller listener, and allow inbound TCP 47832.\n\nApply these repairs on this PC now?",
                    Text,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    _summary.Text = "No Windows settings were changed. The diagnostic report remains available above.";
                    return;
                }

                _summary.Text = "Waiting for Windows administrator approval…";
                var repair = await ControllerConnectionDiagnostics.RepairLocalNetworkAsync();
                if (!repair.Success)
                {
                    ShowFailure("Windows repair", repair.Message);
                    _summary.Text = repair.Message;
                    return;
                }
                _summary.Text = repair.Message + " Restarting the local listener…";
                try
                {
                    _server.Start();
                }
                catch (Exception ex)
                {
                    ControllerLog.Write("Controller listener restart after repair failed: " + ex.Message);
                }
            }

            var connection = await RetryMasterConnectionAsync();
            var finalSnapshot = await ControllerConnectionDiagnostics.RunAsync(
                _state,
                _server,
                _connectionValue.Text);
            ShowSnapshot(finalSnapshot);
            AddCheck(new ControllerDiagnosticCheck(
                "Repair and connection retry",
                connection.Success ? ControllerDiagnosticState.Passed : ControllerDiagnosticState.Failed,
                connection.Message));
            _summary.Text = connection.Message;

            MessageBox.Show(
                this,
                connection.Message + (connection.Success
                    ? string.Empty
                    : "\n\nIf TCP 47832 still cannot be reached, open this troubleshooter on the master PC and select Diagnose & Repair there as well."),
                Text,
                MessageBoxButtons.OK,
                connection.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller repair workflow error: " + ex);
            ShowFailure("Troubleshooter", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<ControllerMasterConnectionResult> RetryMasterConnectionAsync()
    {
        if (_state.IsMaster)
        {
            await _server.Peers.ScanNowAsync();
            return new ControllerMasterConnectionResult(
                true,
                "This PC is the master. Its local network service is ready for other Systems Controllers.");
        }

        var value = _connectionValue.Text.Trim();
        if (!string.IsNullOrWhiteSpace(value))
            return await _server.Peers.ConnectToMasterAsync(value);
        if (_state.MasterControllerSnapshot() is not null)
            return await _server.Peers.ConnectToStoredMasterAsync();

        await _server.Peers.ScanNowAsync();
        var detectedMaster = _server.Peers.Snapshot().FirstOrDefault(peer => peer.IsMaster);
        return detectedMaster is null
            ? new ControllerMasterConnectionResult(
                false,
                "No master Systems Controller was detected. Enter its private IPv4 address or pairing key and run the repair again.")
            : await _server.Peers.ConnectToMasterAsync(detectedMaster.ControllerAddress);
    }

    private void ShowSnapshot(ControllerDiagnosticSnapshot snapshot)
    {
        _checks.BeginUpdate();
        try
        {
            _checks.Items.Clear();
            foreach (var check in snapshot.Checks)
                AddCheck(check);
        }
        finally
        {
            _checks.EndUpdate();
        }
    }

    private void AddCheck(ControllerDiagnosticCheck check)
    {
        var (status, color) = check.State switch
        {
            ControllerDiagnosticState.Passed => ("PASS", Color.FromArgb(45, 125, 50)),
            ControllerDiagnosticState.Warning => ("WARNING", Color.FromArgb(176, 107, 0)),
            _ => ("FAILED", Color.FromArgb(187, 34, 46))
        };
        var item = new ListViewItem(status) { ForeColor = color };
        item.SubItems.Add(check.Name);
        item.SubItems.Add(check.Details);
        _checks.Items.Add(item);
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        _connectionValue.Enabled = !busy;
        _runChecks.Enabled = !busy;
        _repair.Enabled = !busy;
        _close.Enabled = !busy;
        UseWaitCursor = busy;
        if (!string.IsNullOrWhiteSpace(message))
            _summary.Text = message;
    }

    private void ShowFailure(string name, string details)
    {
        AddCheck(new ControllerDiagnosticCheck(name, ControllerDiagnosticState.Failed, details));
        MessageBox.Show(this, details, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static void ConfigureButton(Button button, string text, Color background, Color foreground)
    {
        button.Text = text;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }
}
