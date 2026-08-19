namespace MulletHopPosController;

internal static class NumericPinInput
{
    public static void Configure(TextBox box)
    {
        box.MaxLength = 8;
        box.UseSystemPasswordChar = true;
        box.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
                e.Handled = true;
        };
    }

    public static bool IsValid(string pin) =>
        pin.Length is >= 4 and <= 8 && pin.All(char.IsDigit);
}

internal sealed class PinEntryDialog : Form
{
    private readonly TextBox _pin = new();
    public string Pin => _pin.Text;

    public PinEntryDialog()
    {
        Text = "POS Controller Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 205);
        Font = new Font("Segoe UI", 10);

        var heading = new Label
        {
            Text = "Enter the settings passcode",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            Bounds = new Rectangle(25, 22, 370, 34),
            TextAlign = ContentAlignment.MiddleCenter
        };
        var note = new Label
        {
            Text = "A 4–8 digit passcode is required to open Settings.",
            Bounds = new Rectangle(25, 60, 370, 28),
            TextAlign = ContentAlignment.MiddleCenter
        };
        _pin.Bounds = new Rectangle(100, 98, 220, 32);
        _pin.TextAlign = HorizontalAlignment.Center;
        NumericPinInput.Configure(_pin);
        var open = new Button
        {
            Text = "Open Settings",
            Bounds = new Rectangle(105, 150, 135, 38),
            BackColor = Color.FromArgb(117, 68, 154),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(250, 150, 85, 38),
            DialogResult = DialogResult.Cancel
        };
        open.Click += (_, _) =>
        {
            if (!NumericPinInput.IsValid(_pin.Text))
            {
                MessageBox.Show(this, "Enter the 4–8 digit settings passcode.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
        };
        AcceptButton = open;
        CancelButton = cancel;
        Controls.AddRange([heading, note, _pin, open, cancel]);
        Shown += (_, _) => _pin.Focus();
    }
}

internal sealed class PinSetupDialog : Form
{
    private readonly TextBox _pin = new();
    private readonly TextBox _confirm = new();
    public string Pin => _pin.Text;

    public PinSetupDialog()
    {
        Text = "Create POS Settings Passcode";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(470, 280);
        Font = new Font("Segoe UI", 10);

        Controls.Add(new Label
        {
            Text = "Protect the POS Controller settings",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            Bounds = new Rectangle(25, 20, 420, 36),
            TextAlign = ContentAlignment.MiddleCenter
        });
        Controls.Add(new Label
        {
            Text = "Use 4–8 numbers. The dashboard controls will stay available without a passcode.",
            Bounds = new Rectangle(35, 62, 400, 50),
            TextAlign = ContentAlignment.MiddleCenter
        });
        Controls.Add(new Label { Text = "New Passcode:", Bounds = new Rectangle(40, 128, 125, 28) });
        Controls.Add(new Label { Text = "Confirm:", Bounds = new Rectangle(40, 169, 125, 28) });
        _pin.Bounds = new Rectangle(165, 124, 245, 32);
        _confirm.Bounds = new Rectangle(165, 165, 245, 32);
        NumericPinInput.Configure(_pin);
        NumericPinInput.Configure(_confirm);
        var save = new Button
        {
            Text = "Save Passcode",
            Bounds = new Rectangle(200, 220, 130, 40),
            BackColor = Color.FromArgb(117, 68, 154),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(340, 220, 90, 40),
            DialogResult = DialogResult.Cancel
        };
        save.Click += (_, _) =>
        {
            if (!NumericPinInput.IsValid(_pin.Text))
            {
                MessageBox.Show(this, "Enter a passcode containing 4–8 numbers only.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_pin.Text != _confirm.Text)
            {
                MessageBox.Show(this, "The two passcode entries do not match.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _confirm.Clear();
                _confirm.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
        };
        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([_pin, _confirm, save, cancel]);
    }
}

internal sealed class ChangePinDialog : Form
{
    private readonly PosSettings _settings;
    private readonly TextBox _current = new();
    private readonly TextBox _pin = new();
    private readonly TextBox _confirm = new();
    public string NewPin => _pin.Text;

    public ChangePinDialog(PosSettings settings)
    {
        _settings = settings;
        Text = "Change Settings Passcode";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(455, 265);
        Font = new Font("Segoe UI", 10);

        var labels = new[] { "Current Passcode:", "New Passcode:", "Confirm New:" };
        var boxes = new[] { _current, _pin, _confirm };
        for (var index = 0; index < boxes.Length; index++)
        {
            Controls.Add(new Label
            {
                Text = labels[index],
                Bounds = new Rectangle(25, 31 + index * 48, 150, 28)
            });
            boxes[index].Bounds = new Rectangle(175, 27 + index * 48, 235, 32);
            NumericPinInput.Configure(boxes[index]);
            Controls.Add(boxes[index]);
        }
        var save = new Button
        {
            Text = "Change Passcode",
            Bounds = new Rectangle(185, 190, 140, 40),
            BackColor = Color.FromArgb(117, 68, 154),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(335, 190, 85, 40),
            DialogResult = DialogResult.Cancel
        };
        save.Click += (_, _) => ValidateAndClose();
        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([save, cancel]);
    }

    private void ValidateAndClose()
    {
        if (!_settings.VerifyPin(_current.Text))
        {
            MessageBox.Show(this, "The current passcode is not correct.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _current.SelectAll();
            _current.Focus();
            return;
        }
        if (!NumericPinInput.IsValid(_pin.Text))
        {
            MessageBox.Show(this, "Enter a new passcode containing 4–8 numbers only.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_pin.Text != _confirm.Text)
        {
            MessageBox.Show(this, "The two new passcode entries do not match.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
    }
}
