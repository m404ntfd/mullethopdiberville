using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;

namespace MulletHopPosController;

internal readonly record struct ZebraDiagnosticPrintResult(bool Success, string Message);

internal static class ZebraWristbandDiagnostics
{
    private const int PrintheadWidthDots = 448;
    private const int WristbandWidthDots = 203;
    private const int WristbandLengthDots = 2233;
    private const int CenteredWristbandLeft = 122;
    private const int RightAlignedWristbandLeft = PrintheadWidthDots - WristbandWidthDots;

    public static Task<ZebraDiagnosticPrintResult> PrintPositionTestAsync(
        string printerName,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => PrintPositionTest(printerName, cancellationToken), cancellationToken);

    internal static string BuildPositionTestCommandForSmokeTest(string printerName) =>
        BuildPositionTestCommand(printerName);

    private static ZebraDiagnosticPrintResult PrintPositionTest(
        string printerName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = new PrinterSettings { PrinterName = printerName };
        if (!settings.IsValid ||
            !string.Equals(settings.PrinterName, printerName, StringComparison.OrdinalIgnoreCase))
        {
            return new ZebraDiagnosticPrintResult(
                false,
                $"Windows does not currently have a printer named {printerName}.");
        }

        var zpl = BuildPositionTestCommand(printerName);
        SendRawPrinterBytes(
            printerName,
            Encoding.ASCII.GetBytes(zpl),
            "Mullet Hop Zebra Diagnostic");

        PosLog.Write(
            $"Sent native Zebra coordinate diagnostic to {printerName}: " +
            $"^PW={PrintheadWidthDots}, ^LL={WristbandLengthDots}; " +
            $"LEFT section x=0..{WristbandWidthDots - 1}, " +
            $"CENTER section x={CenteredWristbandLeft}..{CenteredWristbandLeft + WristbandWidthDots - 1}, " +
            $"RIGHT section x={RightAlignedWristbandLeft}..{PrintheadWidthDots - 1}. " +
            "This job contains only Zebra-native text, lines, and boxes; no PDF, image raster, or GDI rendering is used.");

        return new ZebraDiagnosticPrintResult(
            true,
            $"A native Zebra diagnostic wristband was sent to {printerName}. " +
            "It contains a 448-dot printhead map followed by LEFT, CENTER, and RIGHT 203-dot test boxes. " +
            "The LilyPad wristband itself was not printed.");
    }

    private static string BuildPositionTestCommand(string printerName)
    {
        var safePrinterName = new string(printerName
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safePrinterName))
            safePrinterName = "WB";

        var builder = new StringBuilder();
        builder.AppendLine("^XA");
        builder.AppendLine("^CI28");
        builder.AppendLine("^PON");
        builder.AppendLine("^FWN");
        builder.AppendLine("^LH0,0");
        builder.AppendLine("^LT0");
        builder.AppendLine("^LS0");
        builder.AppendLine($"^PW{PrintheadWidthDots}");
        builder.AppendLine($"^LL{WristbandLengthDots}");

        // Section 1 maps the entire 448-dot ZD411 printhead. Only marks physically
        // under the wristband should appear, which tells us where the media actually sits.
        builder.AppendLine("^FO0,20^GB448,4,4^FS");
        builder.AppendLine("^FO0,500^GB448,4,4^FS");
        var mapPositions = new[] { 0, 56, 112, 168, 224, 280, 336, 392, 444 };
        foreach (var x in mapPositions)
            builder.AppendLine($"^FO{x},20^GB4,484,4^FS");
        foreach (var x in mapPositions.Take(mapPositions.Length - 1))
            builder.AppendLine($"^FO{x + 5},35^A0N,20,20^FD{x}^FS");
        builder.AppendLine("^FO8,455^A0N,24,24^FDFULL 448-DOT PRINTHEAD MAP^FS");

        // Section 2 assumes the one-inch band begins at printhead dot 0.
        AppendAlignmentSection(
            builder,
            left: 0,
            top: 560,
            label: $"LEFT X=0 {safePrinterName}");

        // Section 3 assumes the one-inch band is centered on the 448-dot printhead.
        AppendAlignmentSection(
            builder,
            left: CenteredWristbandLeft,
            top: 1030,
            label: $"CENTER X=122 {safePrinterName}");

        // Section 4 assumes the one-inch band is aligned to the far right of the head.
        AppendAlignmentSection(
            builder,
            left: RightAlignedWristbandLeft,
            top: 1500,
            label: $"RIGHT X=245 {safePrinterName}");

        // Feed-axis references make a media-length or top-position problem obvious.
        builder.AppendLine("^FO0,1970^GB448,4,4^FS");
        builder.AppendLine("^FO0,2100^GB448,4,4^FS");
        builder.AppendLine("^FO8,1990^A0N,28,28^FDEND / FEED AXIS CHECK^FS");
        builder.AppendLine("^XZ");
        return builder.ToString();
    }

    private static void AppendAlignmentSection(
        StringBuilder builder,
        int left,
        int top,
        string label)
    {
        const int sectionHeight = 390;
        builder.AppendLine($"^FO{left},{top}^GB{WristbandWidthDots},{sectionHeight},4^FS");
        builder.AppendLine($"^FO{left},{top + 95}^GB{WristbandWidthDots},4,4^FS");
        builder.AppendLine($"^FO{left},{top + 195}^GB{WristbandWidthDots},4,4^FS");
        builder.AppendLine($"^FO{left},{top + 295}^GB{WristbandWidthDots},4,4^FS");
        builder.AppendLine($"^FO{left + 8},{top + 24}^A0N,22,22^FD{label}^FS");
        builder.AppendLine($"^FO{left + 10},{top + 125}^A0N,46,46^FDTEST^FS");
        builder.AppendLine($"^FO{left + 10},{top + 225}^A0N,30,30^FD203 DOTS^FS");
        builder.AppendLine($"^FO{left + 10},{top + 325}^A0N,24,24^FD1 INCH BAND^FS");
    }

    private static void SendRawPrinterBytes(
        string printerName,
        byte[] bytes,
        string documentName)
    {
        if (!OpenPrinter(printerName, out var printerHandle, IntPtr.Zero))
        {
            throw new InvalidOperationException(
                $"Windows could not open {printerName} for the Zebra diagnostic. " +
                $"Error: {Marshal.GetLastWin32Error()}.");
        }

        var documentStarted = false;
        var pageStarted = false;
        try
        {
            var document = new DocInfo1
            {
                DocumentName = documentName,
                DataType = "RAW"
            };
            if (StartDocPrinter(printerHandle, 1, document) == 0)
            {
                throw new InvalidOperationException(
                    $"Windows rejected the native diagnostic job for {printerName}. " +
                    $"Error: {Marshal.GetLastWin32Error()}.");
            }
            documentStarted = true;

            if (!StartPagePrinter(printerHandle))
            {
                throw new InvalidOperationException(
                    $"Windows could not start the diagnostic page for {printerName}. " +
                    $"Error: {Marshal.GetLastWin32Error()}.");
            }
            pageStarted = true;

            if (!WritePrinter(
                    printerHandle,
                    bytes,
                    checked((uint)bytes.Length),
                    out var written) ||
                written != bytes.Length)
            {
                throw new InvalidOperationException(
                    $"Windows wrote only {written} of {bytes.Length} diagnostic bytes to {printerName}. " +
                    $"Error: {Marshal.GetLastWin32Error()}.");
            }
        }
        finally
        {
            if (pageStarted)
                EndPagePrinter(printerHandle);
            if (documentStarted)
                EndDocPrinter(printerHandle);
            ClosePrinter(printerHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string DocumentName = string.Empty;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? OutputFile;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string DataType = "RAW";
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool OpenPrinter(
        string printerName,
        out IntPtr printerHandle,
        IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int StartDocPrinter(
        IntPtr printerHandle,
        int level,
        [In] DocInfo1 documentInformation);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(
        IntPtr printerHandle,
        [In] byte[] bytes,
        uint byteCount,
        out uint bytesWritten);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr printerHandle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr printerHandle);
}
