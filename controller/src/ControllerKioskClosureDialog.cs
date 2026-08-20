namespace MulletHopKioskController;

internal enum ControllerKioskClosureType
{
    Staff,
    Business
}

internal sealed class ControllerKioskClosureDialog : Form
{
    public ControllerKioskClosureType SelectedClosureType { get; private set; } =
        ControllerKioskClosureType.Staff;

    public ControllerKioskClosureDialog(string target)
    {
        Text = "Close Kiosk";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 250);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;

        var heading = new Label
        {
            Text = $"Why are you closing {target}?",
            Dock = DockStyle.Top,
            Height = 62,
            Padding = new Padding(20, 0, 20, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            ForeColor = Color.FromArgb(16, 24, 32)
        };
        var explanation = new Label
        {
            Text = "Staff Closure shows the staff closure screen and a red status. " +
                   "Business Closure starts the Business Closed video and shows a blue status.",
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(24, 0, 24, 8),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(70, 82, 94)
        };
        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(18, 14, 18, 18)
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));

        var staff = MakeChoiceButton(
            "STAFF CLOSURE", Color.FromArgb(187, 34, 46), Color.White);
        staff.Click += (_, _) => Complete(ControllerKioskClosureType.Staff);
        var business = MakeChoiceButton(
            "BUSINESS CLOSURE", Color.FromArgb(26, 135, 232), Color.White);
        business.Click += (_, _) => Complete(ControllerKioskClosureType.Business);
        var cancel = MakeChoiceButton(
            "CANCEL", Color.FromArgb(120, 126, 132), Color.White);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        buttons.Controls.Add(staff, 0, 0);
        buttons.Controls.Add(business, 1, 0);
        buttons.Controls.Add(cancel, 2, 0);

        Controls.Add(buttons);
        Controls.Add(explanation);
        Controls.Add(heading);
        ControllerTheme.Apply(this);
    }

    private void Complete(ControllerKioskClosureType closureType)
    {
        SelectedClosureType = closureType;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static Button MakeChoiceButton(
        string text,
        Color background,
        Color foreground) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Margin = new Padding(5),
        BackColor = background,
        ForeColor = foreground,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 9, FontStyle.Bold),
        Cursor = Cursors.Hand,
        UseVisualStyleBackColor = false
    };
}
