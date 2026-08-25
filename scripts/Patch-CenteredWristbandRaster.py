from pathlib import Path
import re

path = Path('pos-controller/src/DirectWristbandPrinter.cs')
text = path.read_text(encoding='utf-8')

needle = '    private const double NativeWristbandLengthInches = 11d;\n'
replacement = needle + '    private const int Zd411PrintheadWidthDots203Dpi = 448;\n    private const int Zd411PrintheadWidthDots300Dpi = 638;\n'
assert text.count(needle) == 1
text = text.replace(needle, replacement, 1)

smoke = r'''    internal static bool NativeZplPackingPassesSmokeTest()
    {
        using var marked = new Bitmap(8, 1, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(marked))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Black, 0, 0, 1, 1);
        }
        var sourceRaster = CreateMonochromeRaster(marked);
        var configuredPaper = new PaperSize("Custom", 100, 1000);
        var standardLayout = CalculateNativeWristbandLayout(
            203,
            203,
            configuredPaper,
            "ZDesigner ZD411-203dpi ZPL");
        var paddedRaster = PadRasterToPrinthead(
            sourceRaster,
            standardLayout.PrintheadWidth,
            standardLayout.LeftOffset);
        var command = BuildZplCommand(
            paddedRaster,
            standardLayout.PrintheadWidth,
            0);
        var expectedByte = standardLayout.LeftOffset / 8;
        var expectedMask = (byte)(0x80 >> (standardLayout.LeftOffset % 8));
        return sourceRaster.Width == 8 &&
               sourceRaster.Height == 1 &&
               sourceRaster.BytesPerRow == 1 &&
               sourceRaster.Bits.Length == 1 &&
               sourceRaster.Bits[0] == 0x80 &&
               standardLayout.MediaWidth == 203 &&
               standardLayout.MediaLength == 2233 &&
               standardLayout.PrintheadWidth == 448 &&
               standardLayout.LeftOffset == 122 &&
               paddedRaster.Width == 448 &&
               paddedRaster.Height == 1 &&
               paddedRaster.BytesPerRow == 56 &&
               paddedRaster.Bits.Length == 56 &&
               paddedRaster.Bits[expectedByte] == expectedMask &&
               command.Contains("^GFA,56,56,56,", StringComparison.Ordinal) &&
               command.Contains("^PW448", StringComparison.Ordinal) &&
               command.Contains("^FO0,0", StringComparison.Ordinal) &&
               !command.Contains("^LS", StringComparison.Ordinal) &&
               !command.Contains("^PR", StringComparison.Ordinal);
    }
'''
text, count = re.subn(
    r'    internal static bool NativeZplPackingPassesSmokeTest\(\)\n    \{.*?\n    \}\n\n(?=    private static PrintDestinationSelectionResult)',
    smoke + '\n',
    text,
    count=1,
    flags=re.S)
assert count == 1

print_method = r'''    private static void PrintRenderedPagesAsZpl(
        IReadOnlyList<Bitmap> pages,
        string printerName,
        string driverName,
        CancellationToken cancellationToken)
    {
        var printerSettings = new PrinterSettings { PrinterName = printerName };
        var pageSettings = printerSettings.DefaultPageSettings;
        var paper = pageSettings.PaperSize;
        var dpiX = pageSettings.PrinterResolution.X > 0
            ? pageSettings.PrinterResolution.X
            : 203;
        var dpiY = pageSettings.PrinterResolution.Y > 0
            ? pageSettings.PrinterResolution.Y
            : dpiX;
        var layout = CalculateNativeWristbandLayout(dpiX, dpiY, paper, driverName);
        var mediaWidth = layout.MediaWidth;
        var mediaLength = layout.MediaLength;
        var printableArea = pageSettings.PrintableArea;

        PosLog.Write(
            $"RAW ZPL wristband media for {printerName} from Windows driver '{driverName}': " +
            $"configured paper={paper.PaperName} {paper.Width}x{paper.Height} hundredths-inch, " +
            $"landscape={pageSettings.Landscape}, " +
            $"printable={printableArea.X:0.##},{printableArea.Y:0.##}," +
            $"{printableArea.Width:0.##}x{printableArea.Height:0.##}, " +
            $"resolution={dpiX}x{dpiY} dpi; physical wristband=" +
            $"{NativeWristbandWidthInches:0.##}x{NativeWristbandLengthInches:0.##} inches, " +
            $"artwork raster={mediaWidth}x{mediaLength} dots, printhead={layout.PrintheadWidth} dots, " +
            $"validated centered wristband x={layout.LeftOffset}..{layout.LeftOffset + mediaWidth - 1}.");

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var fitted = FitForNativeWristband(page, mediaWidth, mediaLength, dpiX, dpiY);
            var artworkRaster = CreateMonochromeRaster(fitted);
            var raster = PadRasterToPrinthead(
                artworkRaster,
                layout.PrintheadWidth,
                layout.LeftOffset);
            var ink = MeasureRasterInk(raster);
            SendRawPrinterBytes(
                printerName,
                Encoding.ASCII.GetBytes(BuildZplCommand(
                    raster,
                    layout.PrintheadWidth,
                    0)),
                "Mullet Hop Wristbands");
            PosLog.Write(
                $"Submitted a centered full-printhead RAW ZPL raster to {printerName}: " +
                $"artwork={artworkRaster.Width}x{artworkRaster.Height} dots, " +
                $"wire raster={raster.Width}x{raster.Height} dots, {raster.Bits.Length} raster bytes, " +
                $"artwork x={layout.LeftOffset}..{layout.LeftOffset + artworkRaster.Width - 1}, " +
                $"black dots={ink.Count}, black bounds={ink.MinX},{ink.MinY}..{ink.MaxX},{ink.MaxY}, " +
                "^PW spans the complete printhead and ^FO is 0,0.");
        }
    }
'''
text, count = re.subn(
    r'    private static void PrintRenderedPagesAsZpl\(.*?\n    \}\n\n(?=    private static NativeWristbandLayout CalculateNativeWristbandLayout)',
    print_method + '\n',
    text,
    count=1,
    flags=re.S)
assert count == 1

layout_method = r'''    private static NativeWristbandLayout CalculateNativeWristbandLayout(
        int dpiX,
        int dpiY,
        PaperSize paper,
        string driverName)
    {
        _ = paper;
        var mediaWidth = Math.Clamp(
            (int)Math.Round(NativeWristbandWidthInches * Math.Max(1, dpiX)),
            128,
            1200);
        var mediaLength = Math.Clamp(
            (int)Math.Round(NativeWristbandLengthInches * Math.Max(1, dpiY)),
            1408,
            13_200);
        var printheadWidth = driverName.Contains("ZD411", StringComparison.OrdinalIgnoreCase)
            ? (dpiX >= 250
                ? Zd411PrintheadWidthDots300Dpi
                : Zd411PrintheadWidthDots203Dpi)
            : mediaWidth;
        printheadWidth = Math.Max(printheadWidth, mediaWidth);
        var leftOffset = Math.Max(0, (printheadWidth - mediaWidth) / 2);

        // The 1.7.20 native coordinate test physically verified that the one-inch
        // wristband on the ZD411 is centered under the 448-dot 203-dpi printhead.
        // Keep the entire ^GF wire raster at full printhead width and embed the
        // artwork into the centered 203-dot window. This removes any dependency on
        // ^FO clipping or the driver's private media origin.
        return new NativeWristbandLayout(
            mediaWidth,
            mediaLength,
            printheadWidth,
            leftOffset);
    }
'''
text, count = re.subn(
    r'    private static NativeWristbandLayout CalculateNativeWristbandLayout\(.*?\n    \}\n\n(?=    private static Bitmap FitForNativeWristband)',
    layout_method + '\n',
    text,
    count=1,
    flags=re.S)
assert count == 1

helpers = r'''    private static MonochromeRaster PadRasterToPrinthead(
        MonochromeRaster source,
        int printheadWidth,
        int leftOffset)
    {
        if (printheadWidth < source.Width)
            throw new ArgumentOutOfRangeException(nameof(printheadWidth));
        if (leftOffset < 0 || leftOffset + source.Width > printheadWidth)
            throw new ArgumentOutOfRangeException(nameof(leftOffset));

        var bytesPerRow = checked((printheadWidth + 7) / 8);
        var bits = new byte[checked(bytesPerRow * source.Height)];
        for (var y = 0; y < source.Height; y++)
        {
            var sourceRow = y * source.BytesPerRow;
            var destinationRow = y * bytesPerRow;
            for (var x = 0; x < source.Width; x++)
            {
                if ((source.Bits[sourceRow + x / 8] & (0x80 >> (x % 8))) == 0)
                    continue;
                var destinationX = leftOffset + x;
                bits[destinationRow + destinationX / 8] |=
                    (byte)(0x80 >> (destinationX % 8));
            }
        }
        return new MonochromeRaster(printheadWidth, source.Height, bytesPerRow, bits);
    }

    private static (long Count, int MinX, int MinY, int MaxX, int MaxY) MeasureRasterInk(
        MonochromeRaster raster)
    {
        long count = 0;
        var minX = raster.Width;
        var minY = raster.Height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < raster.Height; y++)
        {
            var row = y * raster.BytesPerRow;
            for (var x = 0; x < raster.Width; x++)
            {
                if ((raster.Bits[row + x / 8] & (0x80 >> (x % 8))) == 0)
                    continue;
                count++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        return count == 0
            ? (0, -1, -1, -1, -1)
            : (count, minX, minY, maxX, maxY);
    }

'''
marker = '    private static string BuildZplCommand(\n'
assert text.count(marker) == 1
text = text.replace(marker, helpers + marker, 1)

path.write_text(text, encoding='utf-8')

project = Path('pos-controller/src/MulletHopPosController.csproj')
project_text = project.read_text(encoding='utf-8')
assert project_text.count('<Version>1.7.20</Version>') == 1
project.write_text(
    project_text.replace('<Version>1.7.20</Version>', '<Version>1.7.21</Version>', 1),
    encoding='utf-8')
