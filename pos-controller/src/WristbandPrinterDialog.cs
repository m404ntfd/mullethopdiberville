using System.Drawing.Printing;
using MulletHop.Shared;

namespace MulletHopPosController;

internal sealed class WristbandPrintRequestedEventArgs : EventArgs
{
    public WristbandPrintRequestedEventArgs(string pageUrl)
    {
        PageUrl = pageUrl;
    }

    public string PageUrl { get; }
}

internal sealed class WristbandPrinterDialog : Form
{
    private static readonly string[] ExpectedPrinterNames =
        Enumerable.Range(1, 7).Select(number => $"WB-{number}").ToArray();
    private readonly WristbandSettingsPackage _settings;

    public WristbandPrinterDialog(WristbandSettingsPackage settings)
    {
        _settings = settings.Clone();
        _settings.Normalize();
        Text = "Print Wristbands";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(820, 570);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(244, 247, 250);
        Font = new Font("Segoe UI", 10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        Controls.Add(layout);

        var header = new Label
        {
            Dock = DockStyle.Fill,
            Text = "SELECT A WRISTBAND PRINTER & PRINT",
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(42, 22, 56),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 18, FontStyle.Bold)
        };
        layout.Controls.Add(header, 0, 0);

        var instructions = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(30, 14, 30, 8),
            Text = "Choose WB-1 through WB-7. The button selects that matching Windows printer " +
                   "and immediately prints the current preview. POS-X remains the receipt printer.",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(37, 48, 58),
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };
        layout.Controls.Add(instructions, 0, 1);

        var printers = BuildPrinterGrid();
        printers.Dock = DockStyle.Fill;
        layout.Controls.Add(printers, 0, 2);

        var cancelPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(180, 16, 180, 18)
        };
        var cancel = new Button
        {
            Dock = DockStyle.Fill,
            Text = "RETURN / CANCEL PRINT",
            DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(98, 107, 117),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            TabStop = true
        };
        cancel.FlatAppearance.BorderSize = 0;
        cancelPanel.Controls.Add(cancel);
        layout.Controls.Add(cancelPanel, 0, 3);
        CancelButton = cancel;
    }

    public string? SelectedPrinterName { get; private set; }

    internal static IReadOnlyList<string> PrinterNamesForSmokeTest => ExpectedPrinterNames;

    private Control BuildPrinterGrid()
    {
        var (installed, inventoryAvailable) = GetInstalledPrinterNames();
        var grid = new TableLayoutPanel
        {
            ColumnCount = 4,
            RowCount = 2,
            Padding = new Padding(28, 12, 28, 8),
            BackColor = BackColor
        };
        for (var column = 0; column < grid.ColumnCount; column++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        for (var index = 0; index < ExpectedPrinterNames.Length; index++)
        {
            var printerName = ExpectedPrinterNames[index];
            var isInstalled = installed.Contains(printerName);
            var canSelect = isInstalled || !inventoryAvailable;
            var assignedColor = _settings.ColorForPrinter(printerName);
            var colorLabel = assignedColor is null
                ? "COLOR NOT SET"
                : assignedColor.Name.ToUpperInvariant() +
                  (assignedColor.IsActive ? string.Empty : " (INACTIVE)");
            var buttonColor = assignedColor is null
                ? Color.FromArgb(234, 239, 243)
                : WristbandColorSettingsDialog.ParseColor(assignedColor.HexColor);
            var button = new Button
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(10),
                Tag = printerName,
                Text = printerName + Environment.NewLine +
                       (canSelect ? colorLabel + Environment.NewLine + "PRINT NOW" : "NOT INSTALLED"),
                Enabled = canSelect,
                BackColor = canSelect
                    ? buttonColor
                    : Color.FromArgb(218, 222, 226),
                ForeColor = canSelect
                    ? WristbandColorSettingsDialog.ContrastingText(buttonColor)
                    : Color.FromArgb(111, 117, 123),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 13, FontStyle.Bold),
                Cursor = canSelect ? Cursors.Hand : Cursors.Default
            };
            button.FlatAppearance.BorderSize = 3;
            button.FlatAppearance.BorderColor = canSelect
                ? Color.FromArgb(41, 155, 196)
                : Color.FromArgb(173, 179, 184);
            button.Click += SelectPrinter;
            grid.Controls.Add(button, index % 4, index / 4);
        }

        var note = new Label
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(10),
            Text = "Printer colors are managed in POS or Systems Controller Settings.",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(83, 97, 109),
            Font = new Font("Segoe UI", 9, FontStyle.Italic)
        };
        grid.Controls.Add(note, 3, 1);
        return grid;
    }

    private static (HashSet<string> Names, bool Available) GetInstalledPrinterNames()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string printerName in PrinterSettings.InstalledPrinters)
                installed.Add(printerName);
            return (installed, true);
        }
        catch (Exception ex)
        {
            PosLog.Write("Windows printer enumeration failed: " + ex.Message);
            return (installed, false);
        }
    }

    private void SelectPrinter(object? sender, EventArgs e)
    {
        if (sender is not Button { Tag: string printerName })
            return;

        SelectedPrinterName = printerName;
        DialogResult = DialogResult.OK;
        Close();
    }
}
