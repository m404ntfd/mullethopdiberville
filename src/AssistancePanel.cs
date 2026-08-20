using System.Drawing.Drawing2D;

namespace MulletHopWaiverKiosk;

internal sealed class KioskAssistancePanel : Panel
{
    private readonly Label _heading = new();
    private readonly Label _message = new();
    private readonly FlashingAssistanceLight _light = new();
    private readonly Button _action = new();
    private bool _requested;

    public event EventHandler? AssistanceRequested;
    public event EventHandler? AssistanceCleared;

    public KioskAssistancePanel()
    {
        Width = 320;
        Dock = DockStyle.Right;
        Padding = new Padding(20, 30, 20, 30);
        BorderStyle = BorderStyle.FixedSingle;

        _heading.Dock = DockStyle.Top;
        _heading.Height = 92;
        _heading.TextAlign = ContentAlignment.MiddleCenter;
        _heading.Font = new Font("Segoe UI", 21, FontStyle.Bold);

        _light.Dock = DockStyle.Top;
        _light.Height = 116;
        _light.Margin = new Padding(30, 8, 30, 8);
        _light.Visible = false;

        _message.Dock = DockStyle.Fill;
        _message.Padding = new Padding(12);
        _message.TextAlign = ContentAlignment.MiddleCenter;
        _message.Font = new Font("Segoe UI", 13, FontStyle.Bold);

        _action.Dock = DockStyle.Bottom;
        _action.Height = 76;
        _action.Margin = new Padding(0, 18, 0, 0);
        _action.FlatStyle = FlatStyle.Flat;
        _action.FlatAppearance.BorderSize = 0;
        _action.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        _action.Cursor = Cursors.Hand;
        _action.Click += (_, _) =>
        {
            if (_requested)
                AssistanceCleared?.Invoke(this, EventArgs.Empty);
            else
                AssistanceRequested?.Invoke(this, EventArgs.Empty);
        };

        Controls.Add(_message);
        Controls.Add(_light);
        Controls.Add(_heading);
        Controls.Add(_action);
        SetState(requested: false, acknowledged: false);
    }

    public void SetState(bool requested, bool acknowledged)
    {
        _requested = requested;
        _light.Visible = requested;
        _light.Flashing = requested;
        if (!requested)
        {
            _heading.Text = "NEED ASSISTANCE?";
            _message.Text =
                "If you need help completing the waiver, select the button below and a staff member will assist you.";
            _action.Text = "CALL FOR ASSISTANCE";
            _action.BackColor = Color.FromArgb(245, 130, 32);
            _action.ForeColor = Color.White;
            return;
        }

        _heading.Text = acknowledged
            ? "ASSISTANCE IS ON THE WAY"
            : "ASSISTANCE REQUESTED";
        _message.Text = acknowledged
            ? "A staff member has received your request and is on the way to help you."
            : "A staff member has been notified. Please remain at this waiver station.";
        _action.Text = "CLEAR ASSISTANCE CALL";
        _action.BackColor = Color.FromArgb(118, 196, 66);
        _action.ForeColor = Color.FromArgb(16, 24, 32);
    }

    public void ApplyTheme(bool dark)
    {
        BackColor = dark ? Color.FromArgb(27, 36, 46) : Color.White;
        _heading.ForeColor = dark ? Color.FromArgb(255, 222, 89) : Color.FromArgb(117, 68, 154);
        _message.ForeColor = dark ? Color.FromArgb(235, 241, 246) : Color.FromArgb(52, 65, 76);
        Invalidate(true);
    }
}

internal sealed class FlashingAssistanceLight : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 450 };
    private bool _flashing;
    private bool _bright = true;

    public bool Flashing
    {
        get => _flashing;
        set
        {
            if (_flashing == value)
                return;
            _flashing = value;
            _bright = true;
            if (value)
                _timer.Start();
            else
                _timer.Stop();
            Invalidate();
        }
    }

    public FlashingAssistanceLight()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        _timer.Tick += (_, _) =>
        {
            _bright = !_bright;
            Invalidate();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var diameter = Math.Max(34, Math.Min(92, Math.Min(ClientSize.Width, ClientSize.Height) - 14));
        var circle = new Rectangle(
            (ClientSize.Width - diameter) / 2,
            (ClientSize.Height - diameter) / 2,
            diameter,
            diameter);
        var yellow = _bright ? Color.FromArgb(255, 221, 48) : Color.FromArgb(126, 102, 12);
        if (_bright)
        {
            using var glow = new SolidBrush(Color.FromArgb(72, 255, 210, 35));
            e.Graphics.FillEllipse(glow, Rectangle.Inflate(circle, 8, 8));
        }
        using var fill = new SolidBrush(yellow);
        using var border = new Pen(Color.FromArgb(137, 99, 0), 4);
        e.Graphics.FillEllipse(fill, circle);
        e.Graphics.DrawEllipse(border, circle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }
}
