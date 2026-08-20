namespace MulletHopKioskController;

internal sealed class MasterConnectionDialog : Form
{
    private readonly TextBox _connectionValue = new();

    public string ConnectionValue => _connectionValue.Text.Trim();
    public bool UseSavedConnection { get; private set; }

    public MasterConnectionDialog(StoredMasterControllerConnection? storedMaster)
    {
        Text = "Connect to Master Controller";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(700, 455);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            AutoSize = false,
            Text = "CONNECT TO THE MASTER CONTROLLER",
            Bounds = new Rectangle(25, 18, 650, 42),
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(117, 68, 154),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var explanation = new Label
        {
            AutoSize = false,
            Text = "Enter the master PC's private IPv4 address or copy its full pairing key from the Controller Setup section. The connection is saved by computer identity so a future DHCP address change can be found automatically.",
            Bounds = new Rectangle(42, 68, 616, 65),
            ForeColor = Color.FromArgb(52, 65, 76),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var inputLabel = new Label
        {
            AutoSize = false,
            Text = "Master IPv4 Address or Pairing Key",
            Bounds = new Rectangle(42, 150, 616, 26),
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(8, 119, 189)
        };
        _connectionValue.Bounds = new Rectangle(42, 181, 616, 34);
        _connectionValue.Font = new Font("Consolas", 10);
        _connectionValue.MaxLength = 1_000;
        _connectionValue.PlaceholderText = "Example: 192.168.1.20 or paste the full pairing key";

        var keyNote = new Label
        {
            AutoSize = false,
            Text = "A pairing key locates the master on this local subnet. Use the private IP address when the master is on a different routed subnet.",
            Bounds = new Rectangle(42, 222, 616, 44),
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(83, 97, 109),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var savedPanel = new Panel
        {
            Bounds = new Rectangle(42, 280, 616, 72),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(238, 250, 255)
        };
        var savedLabel = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(12, 7, 390, 56),
            Text = storedMaster is null
                ? "No master connection is stored on this PC yet."
                : $"Saved master: {storedMaster.MachineName}\nLast address: {storedMaster.LastKnownAddress}",
            ForeColor = Color.FromArgb(52, 65, 76),
            Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var useSaved = new Button
        {
            Text = "Use Saved Master",
            Bounds = new Rectangle(414, 13, 187, 44),
            Enabled = storedMaster is not null,
            BackColor = storedMaster is null
                ? Color.FromArgb(225, 230, 234)
                : Color.FromArgb(52, 152, 143),
            ForeColor = storedMaster is null ? Color.FromArgb(100, 110, 118) : Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.2f, FontStyle.Bold)
        };
        useSaved.Click += (_, _) =>
        {
            UseSavedConnection = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        savedPanel.Controls.AddRange([savedLabel, useSaved]);

        var connect = new Button
        {
            Text = "Connect & Save",
            Bounds = new Rectangle(356, 379, 146, 48),
            BackColor = Color.FromArgb(245, 130, 32),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        connect.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ConnectionValue))
            {
                MessageBox.Show(this,
                    "Enter the master computer's private IPv4 address or pairing key.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _connectionValue.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(512, 379, 146, 48),
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(238, 250, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };

        AcceptButton = connect;
        CancelButton = cancel;
        Controls.AddRange([
            heading, explanation, inputLabel, _connectionValue, keyNote,
            savedPanel, connect, cancel]);
        Shown += (_, _) => _connectionValue.Focus();
        ControllerTheme.Apply(this);
    }
}
