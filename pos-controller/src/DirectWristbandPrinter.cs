using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Accessibility;
using Windows.Data.Pdf;
using Windows.Storage.Streams;
using WindowsPdfDocument = Windows.Data.Pdf.PdfDocument;

namespace MulletHopPosController;

internal readonly record struct PrintDestinationSelectionResult(
    bool Success,
    bool ReturnLinkActivated,
    string Message)
{
    public static PrintDestinationSelectionResult Succeeded(
        string printerName,
        bool returnLinkActivated) =>
        new(
            true,
            returnLinkActivated,
            returnLinkActivated
                ? $"The current wristband print job was sent to {printerName}, and LilyPad was asked to return through the wristband logo."
                : $"The current wristband print job was sent to {printerName}, but the wristband logo return link was not available.");

    public static PrintDestinationSelectionResult Failed(string message) =>
        new(false, false, message);
}

/// <summary>
/// Renders the authenticated LilyPad wristband PDF with Windows' PDF renderer,
/// submits it directly to exactly one WB printer without a browser or system
/// print dialog, and follows the PDF's own Mullet Hop return link.
/// </summary>
internal static class DirectWristbandPrinter
{
    private const int MaximumPdfBytes = 20 * 1024 * 1024;
    private const int MaximumPdfPages = 50;
    private const int MaximumAccessibleNodes = 5000;
    private const double RenderDpi = 300;
    private const uint ObjectIdClient = unchecked((uint)-4);
    private const int ChildIdSelf = 0;
    private const int SelectTakeFocus = 0x1;
    private const int SelectTakeSelection = 0x2;
    private const int StretchHalftone = 4;
    private const uint DibRgbColors = 0;
    private const uint SourceCopyRasterOperation = 0x00CC0020;
    private const int GdiError = -1;
    private static readonly TimeSpan ReturnLinkPollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly Guid AccessibleInterfaceId =
        new("618736E0-3C3D-11CF-810C-00AA00389B71");

    public static Task<PrintDestinationSelectionResult> PrintAsync(
        IntPtr firefoxWindow,
        byte[] pdfBytes,
        string printerName,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedWristbandPrinter(printerName))
        {
            return Task.FromResult(PrintDestinationSelectionResult.Failed(
                $"{printerName} is not a configured wristband printer."));
        }
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(PrintDestinationSelectionResult.Failed(
                "Direct wristband printing is only available on Windows."));
        }
        if (!HasPdfSignature(pdfBytes))
        {
            return Task.FromResult(PrintDestinationSelectionResult.Failed(
                "LilyPad did not return a valid wristband PDF."));
        }
        if (pdfBytes.Length > MaximumPdfBytes)
        {
            return Task.FromResult(PrintDestinationSelectionResult.Failed(
                "The LilyPad wristband PDF is too large to print safely."));
        }

        var completion = new TaskCompletionSource<PrintDestinationSelectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(RenderAndPrintOnStaThread(
                    firefoxWindow,
                    pdfBytes,
                    printerName,
                    cancellationToken));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                PosLog.Write("Direct Windows wristband printing failed: " + ex);
                completion.TrySetResult(PrintDestinationSelectionResult.Failed(
                    "Windows could not submit the wristband print job directly: " + ex.Message));
            }
        })
        {
            IsBackground = true,
            Name = "Direct Windows wristband printer"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static bool IsSupportedWristbandPrinterForSmokeTest(string? printerName) =>
        IsSupportedWristbandPrinter(printerName);

    internal static bool HasPdfSignatureForSmokeTest(byte[]? bytes) =>
        HasPdfSignature(bytes);

    internal static bool TextIdentifiesWristbandReturnLinkForSmokeTest(
        string? name,
        string? value) => TextIdentifiesWristbandReturnLink(name, value);

    internal static bool InkMeasurementPassesSmokeTest()
    {
        using var blank = new Bitmap(32, 32, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(blank))
            graphics.Clear(Color.White);
        using var marked = new Bitmap(32, 32, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(marked))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Black, 8, 8, 16, 16);
        }
        return MeasureVisibleInkPercentage(blank) == 0 &&
               MeasureVisibleInkPercentage(marked) > 0;
    }

    internal static bool PrinterRasterPackingPassesSmokeTest()
    {
        using var marked = new Bitmap(4, 3, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(marked))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Black, 0, 0, 1, 1);
        }
        var raster = CreatePrinterRaster(marked);
        return raster.Width == 4 &&
               raster.Height == 3 &&
               raster.Stride == 12 &&
               raster.Bits.Length == 36 &&
               raster.Bits[0] == 0 &&
               raster.Bits[1] == 0 &&
               raster.Bits[2] == 0 &&
               raster.Bits[3] == 255;
    }

    internal static bool NativeZplPackingPassesSmokeTest()
    {
        using var marked = new Bitmap(8, 1, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(marked))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Black, 0, 0, 1, 1);
        }
        var raster = CreateMonochromeRaster(marked);
        var command = BuildZplCommand(raster);
        return raster.Width == 8 &&
               raster.Height == 1 &&
               raster.BytesPerRow == 1 &&
               raster.Bits.Length == 1 &&
               raster.Bits[0] == 0x80 &&
               command.Contains("^GFA,1,1,1,80", StringComparison.Ordinal);
    }

    private static PrintDestinationSelectionResult RenderAndPrintOnStaThread(
        IntPtr firefoxWindow,
        byte[] pdfBytes,
        string printerName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var printerSettings = new PrinterSettings { PrinterName = printerName };
        if (!printerSettings.IsValid ||
            !string.Equals(printerSettings.PrinterName, printerName, StringComparison.OrdinalIgnoreCase))
        {
            return PrintDestinationSelectionResult.Failed(
                $"Windows does not currently have a printer named {printerName}. " +
                "Install or reconnect that wristband printer and try again.");
        }

        var pages = RenderPdfPages(pdfBytes, cancellationToken);
        if (pages.Count == 0)
        {
            return PrintDestinationSelectionResult.Failed(
                "The LilyPad wristband PDF did not contain a printable page.");
        }
        try
        {
            PrintRenderedPages(pages, printerName, cancellationToken);
        }
        finally
        {
            foreach (var page in pages)
                page.Dispose();
        }

        PosLog.Write($"The wristband PDF was submitted directly to {printerName} without a print dialog.");
        var linkSummary = "the Firefox window was not available";
        var returnLinkActivated = false;
        try
        {
            returnLinkActivated = IsWindow(firefoxWindow) && ActivateWristbandReturnLink(
                firefoxWindow,
                CancellationToken.None,
                out linkSummary);
        }
        catch (Exception ex)
        {
            linkSummary = ex.Message;
            PosLog.Write("The wristbands were already submitted, but the return-link action failed: " + ex);
        }
        if (returnLinkActivated)
            PosLog.Write("The Mullet Hop logo return link was selected after wristband printing.");
        else
            PosLog.Write("Wristband PDF return-link accessibility summary: " + linkSummary);
        return PrintDestinationSelectionResult.Succeeded(printerName, returnLinkActivated);
    }

    private static List<Bitmap> RenderPdfPages(
        byte[] pdfBytes,
        CancellationToken cancellationToken)
    {
        using var source = new MemoryStream(pdfBytes, writable: false);
        using var randomAccessSource = source.AsRandomAccessStream();
        var document = WindowsPdfDocument.LoadFromStreamAsync(randomAccessSource)
            .AsTask(cancellationToken).GetAwaiter().GetResult();
        if (document.PageCount > MaximumPdfPages)
        {
            throw new InvalidDataException(
                $"The wristband PDF contains {document.PageCount} pages; the safety limit is {MaximumPdfPages}.");
        }

        var pages = new List<Bitmap>(checked((int)document.PageCount));
        try
        {
            for (uint pageNumber = 0; pageNumber < document.PageCount; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var page = document.GetPage(pageNumber);
                using var rendered = new InMemoryRandomAccessStream();
                var width = Math.Clamp(
                    (uint)Math.Ceiling(page.Size.Width * RenderDpi / 96d),
                    1u,
                    10_000u);
                var height = Math.Clamp(
                    (uint)Math.Ceiling(page.Size.Height * RenderDpi / 96d),
                    1u,
                    10_000u);
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = width,
                    DestinationHeight = height
                };
                page.RenderToStreamAsync(rendered, options)
                    .AsTask(cancellationToken).GetAwaiter().GetResult();
                rendered.Seek(0);
                using var imageStream = rendered.AsStreamForRead();
                using var decoded = new Bitmap(imageStream);
                // Thermal-printer drivers are commonly backed by older GDI renderers.
                // Keep the page fully opaque so the spool file contains a normal RGB
                // raster instead of an alpha-blended EMF record that some drivers
                // accept but output as a blank wristband.
                var detached = new Bitmap(
                    decoded.Width,
                    decoded.Height,
                    PixelFormat.Format24bppRgb);
                detached.SetResolution((float)RenderDpi, (float)RenderDpi);
                using (var graphics = Graphics.FromImage(detached))
                {
                    graphics.Clear(Color.White);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.DrawImageUnscaled(decoded, 0, 0);
                }
                var inkPercentage = MeasureVisibleInkPercentage(detached);
                PosLog.Write(
                    $"Rendered wristband PDF page {pageNumber + 1}: " +
                    $"{detached.Width}x{detached.Height} pixels, " +
                    $"{inkPercentage:0.###}% visible ink.");
                if (inkPercentage <= 0)
                {
                    detached.Dispose();
                    throw new InvalidDataException(
                        $"Windows rendered wristband PDF page {pageNumber + 1} as blank. " +
                        "No print job was sent.");
                }
                pages.Add(detached);
            }
            return pages;
        }
        catch
        {
            foreach (var page in pages)
                page.Dispose();
            throw;
        }
    }

    private static double MeasureVisibleInkPercentage(Bitmap image)
    {
        var bounds = new Rectangle(0, 0, image.Width, image.Height);
        var data = image.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            // Sample at most about one million pixels. This is dense enough to catch
            // fine barcode/text strokes without delaying the front-desk print flow.
            var xStep = Math.Max(1, image.Width / 1024);
            var yStep = Math.Max(1, image.Height / 1024);
            var rowLength = checked(Math.Abs(data.Stride));
            var row = new byte[rowLength];
            long samples = 0;
            long inkSamples = 0;
            for (var y = 0; y < image.Height; y += yStep)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowLength);
                for (var x = 0; x < image.Width; x += xStep)
                {
                    var offset = x * 3;
                    if (offset + 2 >= row.Length)
                        break;
                    samples++;
                    if (row[offset] < 250 || row[offset + 1] < 250 || row[offset + 2] < 250)
                        inkSamples++;
                }
            }
            return samples == 0 ? 0 : inkSamples * 100d / samples;
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    private static PrinterRaster CreatePrinterRaster(Bitmap image)
    {
        var width = image.Width;
        var height = image.Height;
        var sourceLength = checked(width * 3);
        var destinationStride = checked((sourceLength + 3) & ~3);
        var bits = new byte[checked(destinationStride * height)];
        var bounds = new Rectangle(0, 0, width, height);
        var data = image.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(data.Scan0, y * data.Stride),
                    bits,
                    y * destinationStride,
                    sourceLength);
            }
        }
        finally
        {
            image.UnlockBits(data);
        }
        return new PrinterRaster(width, height, destinationStride, bits);
    }

    private static void PrintRenderedPagesAsZpl(
        IReadOnlyList<Bitmap> pages,
        string printerName,
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
        var mediaWidthHundredths = Math.Min(paper.Width, paper.Height);
        var mediaLengthHundredths = Math.Max(paper.Width, paper.Height);
        var mediaWidth = Math.Clamp(
            (int)Math.Round(mediaWidthHundredths * dpiX / 100d),
            64,
            4096);
        var mediaLength = Math.Clamp(
            (int)Math.Round(mediaLengthHundredths * dpiY / 100d),
            64,
            30_000);

        PosLog.Write(
            $"Native ZPL media for {printerName}: driver paper={paper.PaperName} " +
            $"{paper.Width}x{paper.Height} hundredths-inch, resolution={dpiX}x{dpiY} dpi, " +
            $"raster={mediaWidth}x{mediaLength} dots.");

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var fitted = FitForNativeWristband(page, mediaWidth, mediaLength, dpiX, dpiY);
            var raster = CreateMonochromeRaster(fitted);
            SendRawPrinterBytes(
                printerName,
                Encoding.ASCII.GetBytes(BuildZplCommand(raster)),
                "Mullet Hop Wristbands");
            PosLog.Write(
                $"Submitted a native ZPL wristband raster to {printerName}: " +
                $"{raster.Width}x{raster.Height} dots, {raster.Bits.Length} raster bytes.");
        }
    }

    private static Bitmap FitForNativeWristband(
        Bitmap source,
        int width,
        int height,
        int dpiX,
        int dpiY)
    {
        Bitmap? rotated = null;
        var oriented = source;
        if ((source.Width > source.Height) != (width > height))
        {
            rotated = (Bitmap)source.Clone();
            rotated.RotateFlip(RotateFlipType.Rotate90FlipNone);
            oriented = rotated;
        }

        try
        {
            var output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            output.SetResolution(dpiX, dpiY);
            using var graphics = Graphics.FromImage(output);
            graphics.Clear(Color.White);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            var scale = Math.Min(width / (double)oriented.Width, height / (double)oriented.Height);
            var targetWidth = Math.Max(1, (int)Math.Round(oriented.Width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(oriented.Height * scale));
            var targetX = (width - targetWidth) / 2;
            var targetY = (height - targetHeight) / 2;
            graphics.DrawImage(
                oriented,
                new Rectangle(targetX, targetY, targetWidth, targetHeight),
                0,
                0,
                oriented.Width,
                oriented.Height,
                GraphicsUnit.Pixel);
            return output;
        }
        finally
        {
            rotated?.Dispose();
        }
    }

    private static MonochromeRaster CreateMonochromeRaster(Bitmap image)
    {
        var bytesPerRow = checked((image.Width + 7) / 8);
        var bits = new byte[checked(bytesPerRow * image.Height)];
        var bounds = new Rectangle(0, 0, image.Width, image.Height);
        var data = image.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var sourceStride = Math.Abs(data.Stride);
            var row = new byte[sourceStride];
            for (var y = 0; y < image.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, sourceStride);
                var destinationOffset = y * bytesPerRow;
                for (var x = 0; x < image.Width; x++)
                {
                    var sourceOffset = x * 3;
                    var luminance =
                        row[sourceOffset] * 0.114d +
                        row[sourceOffset + 1] * 0.587d +
                        row[sourceOffset + 2] * 0.299d;
                    if (luminance < 245d)
                        bits[destinationOffset + x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }
        }
        finally
        {
            image.UnlockBits(data);
        }
        return new MonochromeRaster(image.Width, image.Height, bytesPerRow, bits);
    }

    private static string BuildZplCommand(MonochromeRaster raster)
    {
        var hexadecimal = Convert.ToHexString(raster.Bits);
        return "^XA\n^CI28\n^PW" + raster.Width +
               "\n^LL" + raster.Height +
               "\n^LH0,0\n^FO0,0^GFA," + raster.Bits.Length + "," +
               raster.Bits.Length + "," + raster.BytesPerRow + "," +
               hexadecimal + "^FS\n^XZ\n";
    }

    private static bool IsNativeZplDriver(string driverName) =>
        driverName.Contains("Zebra", StringComparison.OrdinalIgnoreCase) ||
        driverName.Contains("ZDesigner", StringComparison.OrdinalIgnoreCase) ||
        driverName.Contains("ZPL", StringComparison.OrdinalIgnoreCase) ||
        driverName.Contains("ZD510", StringComparison.OrdinalIgnoreCase) ||
        driverName.Contains("HC100", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetPrinterDriverName(string printerName, out string driverName)
    {
        driverName = string.Empty;
        if (!OpenPrinter(printerName, out var printerHandle, IntPtr.Zero))
            return false;
        try
        {
            GetPrinter(printerHandle, 2, IntPtr.Zero, 0, out var requiredBytes);
            if (requiredBytes == 0)
                return false;
            var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
            try
            {
                if (!GetPrinter(printerHandle, 2, buffer, requiredBytes, out _))
                    return false;
                var information = Marshal.PtrToStructure<PrinterInfo2>(buffer);
                driverName = Marshal.PtrToStringUni(information.DriverName) ?? string.Empty;
                return !string.IsNullOrWhiteSpace(driverName);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            ClosePrinter(printerHandle);
        }
    }

    private static void SendRawPrinterBytes(
        string printerName,
        byte[] bytes,
        string documentName)
    {
        if (!OpenPrinter(printerName, out var printerHandle, IntPtr.Zero))
            throw new InvalidOperationException(
                $"Windows could not open {printerName} for native wristband output. " +
                $"Error: {Marshal.GetLastWin32Error()}.");

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
                throw new InvalidOperationException(
                    $"Windows rejected the native {printerName} wristband job. " +
                    $"Error: {Marshal.GetLastWin32Error()}.");
            documentStarted = true;
            if (!StartPagePrinter(printerHandle))
                throw new InvalidOperationException(
                    $"Windows could not start the native {printerName} wristband page. " +
                    $"Error: {Marshal.GetLastWin32Error()}.");
            pageStarted = true;
            if (!WritePrinter(printerHandle, bytes, checked((uint)bytes.Length), out var written) ||
                written != bytes.Length)
            {
                throw new InvalidOperationException(
                    $"Windows wrote only {written} of {bytes.Length} native wristband bytes to " +
                    $"{printerName}. Error: {Marshal.GetLastWin32Error()}.");
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

    private static void PrintRenderedPages(
        IReadOnlyList<Bitmap> pages,
        string printerName,
        CancellationToken cancellationToken)
    {
        if (TryGetPrinterDriverName(printerName, out var driverName) &&
            IsNativeZplDriver(driverName))
        {
            PosLog.Write(
                $"Detected wristband driver '{driverName}' for {printerName}; " +
                "using native ZPL raster output instead of the Windows graphics spool path.");
            PrintRenderedPagesAsZpl(pages, printerName, cancellationToken);
            return;
        }

        PosLog.Write(
            $"Wristband printer {printerName} uses driver " +
            $"'{(string.IsNullOrWhiteSpace(driverName) ? "unknown" : driverName)}'; " +
            "using the Windows 24-bit device-raster fallback.");
        var pageIndex = 0;
        using var document = new PrintDocument
        {
            DocumentName = "Mullet Hop Wristbands",
            PrintController = new StandardPrintController(),
            OriginAtMargins = false,
            PrinterSettings = new PrinterSettings
            {
                PrinterName = printerName,
                Copies = 1
            },
            DefaultPageSettings =
            {
                Margins = new Margins(0, 0, 0, 0)
            }
        };
        document.QueryPageSettings += (_, eventArgs) =>
        {
            var image = pages[Math.Min(pageIndex, pages.Count - 1)];
            var imageIsWide = image.Width > image.Height;
            var paperBounds = eventArgs.PageSettings.Bounds;
            var paperIsWide = paperBounds.Width > paperBounds.Height;
            if (imageIsWide != paperIsWide)
                eventArgs.PageSettings.Landscape = !eventArgs.PageSettings.Landscape;
            eventArgs.PageSettings.Margins = new Margins(0, 0, 0, 0);
            eventArgs.PageSettings.Color = false;
        };
        document.PrintPage += (_, eventArgs) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = pages[pageIndex];
            var graphics = eventArgs.Graphics ?? throw new InvalidOperationException(
                "Windows did not provide a graphics surface for the wristband printer.");
            var printableBounds = graphics.VisibleClipBounds;
            if (!float.IsFinite(printableBounds.Width) ||
                !float.IsFinite(printableBounds.Height) ||
                printableBounds.Width <= 1 ||
                printableBounds.Height <= 1)
            {
                printableBounds = new RectangleF(
                    0,
                    0,
                    Math.Max(1, eventArgs.MarginBounds.Width),
                    Math.Max(1, eventArgs.MarginBounds.Height));
            }
            var scale = Math.Min(
                printableBounds.Width / image.Width,
                printableBounds.Height / image.Height);
            var targetWidth = image.Width * scale;
            var targetHeight = image.Height * scale;
            var targetX = printableBounds.Left + (printableBounds.Width - targetWidth) / 2f;
            var targetY = printableBounds.Top + (printableBounds.Height - targetHeight) / 2f;
            PosLog.Write(
                $"Printing wristband page {pageIndex + 1} to {printerName}: " +
                $"paper={eventArgs.PageBounds.Width}x{eventArgs.PageBounds.Height}, " +
                $"printable={printableBounds.X:0.##},{printableBounds.Y:0.##}," +
                $"{printableBounds.Width:0.##}x{printableBounds.Height:0.##}, " +
                $"target={targetX:0.##},{targetY:0.##},{targetWidth:0.##}x{targetHeight:0.##}.");
            TransferRasterToPrinter(
                graphics,
                image,
                new RectangleF(targetX, targetY, targetWidth, targetHeight),
                printerName,
                pageIndex + 1);
            pageIndex++;
            eventArgs.HasMorePages = pageIndex < pages.Count;
        };
        cancellationToken.ThrowIfCancellationRequested();
        document.Print();
    }

    private static void TransferRasterToPrinter(
        Graphics graphics,
        Bitmap image,
        RectangleF pageTarget,
        string printerName,
        int pageNumber)
    {
        var targetPoints = new[]
        {
            new PointF(pageTarget.Left, pageTarget.Top),
            new PointF(pageTarget.Right, pageTarget.Bottom)
        };
        graphics.TransformPoints(CoordinateSpace.Device, CoordinateSpace.Page, targetPoints);
        var destination = Rectangle.FromLTRB(
            (int)Math.Round(Math.Min(targetPoints[0].X, targetPoints[1].X)),
            (int)Math.Round(Math.Min(targetPoints[0].Y, targetPoints[1].Y)),
            (int)Math.Round(Math.Max(targetPoints[0].X, targetPoints[1].X)),
            (int)Math.Round(Math.Max(targetPoints[0].Y, targetPoints[1].Y)));
        if (destination.Width <= 0 || destination.Height <= 0)
        {
            throw new InvalidOperationException(
                $"The {printerName} driver returned an empty device target for wristband page {pageNumber}.");
        }

        var raster = CreatePrinterRaster(image);
        var bitmapInfo = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = checked((uint)Marshal.SizeOf<BitmapInfoHeader>()),
                Width = raster.Width,
                // A negative height declares the byte array top-down so the
                // printer receives the same row order that Windows rendered.
                Height = -raster.Height,
                Planes = 1,
                BitCount = 24,
                Compression = 0,
                SizeImage = checked((uint)raster.Bits.Length),
                XPelsPerMeter = (int)Math.Round(image.HorizontalResolution / 0.0254d),
                YPelsPerMeter = (int)Math.Round(image.VerticalResolution / 0.0254d)
            }
        };

        var hdc = graphics.GetHdc();
        try
        {
            SetStretchBltMode(hdc, StretchHalftone);
            SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
            var copiedLines = StretchDIBits(
                hdc,
                destination.X,
                destination.Y,
                destination.Width,
                destination.Height,
                0,
                0,
                raster.Width,
                raster.Height,
                raster.Bits,
                ref bitmapInfo,
                DibRgbColors,
                SourceCopyRasterOperation);
            if (copiedLines == 0 || copiedLines == GdiError)
            {
                throw new InvalidOperationException(
                    $"The {printerName} driver rejected the device raster for wristband page {pageNumber}. " +
                    $"Windows error: {Marshal.GetLastWin32Error()}.");
            }
            PosLog.Write(
                $"Transferred wristband page {pageNumber} to {printerName} as a 24-bit device raster: " +
                $"{raster.Width}x{raster.Height} source pixels to " +
                $"{destination.Width}x{destination.Height} printer pixels; " +
                $"GDI copied {copiedLines} line(s).");
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }

    private sealed record PrinterRaster(int Width, int Height, int Stride, byte[] Bits);
    private sealed record MonochromeRaster(int Width, int Height, int BytesPerRow, byte[] Bits);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo2
    {
        public IntPtr ServerName;
        public IntPtr PrinterName;
        public IntPtr ShareName;
        public IntPtr PortName;
        public IntPtr DriverName;
        public IntPtr Comment;
        public IntPtr Location;
        public IntPtr DevMode;
        public IntPtr SeparatorFile;
        public IntPtr PrintProcessor;
        public IntPtr DataType;
        public IntPtr Parameters;
        public IntPtr SecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint JobCount;
        public uint AveragePagesPerMinute;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint FirstColor;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr previousPoint);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StretchDIBits(
        IntPtr hdc,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        byte[] bits,
        ref BitmapInfo bitmapInfo,
        uint colorUse,
        uint rasterOperation);

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool OpenPrinter(
        string printerName,
        out IntPtr printerHandle,
        IntPtr defaults);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool GetPrinter(
        IntPtr printerHandle,
        uint level,
        IntPtr printerInformation,
        uint bufferSize,
        out uint requiredBytes);

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

    private static bool ActivateWristbandReturnLink(
        IntPtr firefoxWindow,
        CancellationToken cancellationToken,
        out string accessibilitySummary)
    {
        accessibilitySummary = "no links were exposed";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(firefoxWindow))
                return false;

            var links = ReadAccessibleTree(firefoxWindow)
                .Where(node => node.Role == AccessibleRole.Link && IsEnabled(node))
                .ToArray();
            accessibilitySummary = DescribeLinks(links);
            var returnLink = links.FirstOrDefault(node =>
                TextIdentifiesWristbandReturnLink(node.Name, node.Value));
            if (returnLink is not null && TryActivate(returnLink))
                return true;
            Thread.Sleep(ReturnLinkPollInterval);
        }
        return false;
    }

    private static List<AccessibleNode> ReadAccessibleTree(IntPtr window)
    {
        object? accessibleObject = null;
        var interfaceId = AccessibleInterfaceId;
        var result = AccessibleObjectFromWindow(
            window,
            ObjectIdClient,
            ref interfaceId,
            ref accessibleObject);
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
        if (accessibleObject is not IAccessible root)
        {
            throw new InvalidOperationException(
                "The Firefox window did not expose its controls to Windows accessibility.");
        }

        var nodes = new List<AccessibleNode>();
        var pending = new Queue<AccessibleNode>();
        pending.Enqueue(ReadNode(root, ChildIdSelf));
        while (pending.Count > 0 && nodes.Count < MaximumAccessibleNodes)
        {
            var node = pending.Dequeue();
            nodes.Add(node);
            if (node.ChildId is not int childId || childId != ChildIdSelf)
                continue;

            int childCount;
            try { childCount = node.Accessible.accChildCount; }
            catch (COMException) { continue; }
            if (childCount <= 0)
                continue;

            childCount = Math.Min(childCount, MaximumAccessibleNodes - nodes.Count);
            var children = new object[childCount];
            int childResult;
            int obtained;
            try
            {
                childResult = AccessibleChildren(
                    node.Accessible,
                    0,
                    childCount,
                    children,
                    out obtained);
            }
            catch (COMException)
            {
                continue;
            }
            if (childResult < 0)
                continue;

            for (var index = 0; index < obtained; index++)
            {
                switch (children[index])
                {
                    case IAccessible child:
                        pending.Enqueue(ReadNode(child, ChildIdSelf));
                        break;
                    case int simpleChildId:
                        pending.Enqueue(ReadNode(node.Accessible, simpleChildId));
                        break;
                }
            }
        }
        return nodes;
    }

    private static AccessibleNode ReadNode(IAccessible accessible, object childId)
    {
        string? name = null;
        string? value = null;
        var role = AccessibleRole.None;
        var state = AccessibleStates.None;
        try { name = accessible.get_accName(childId); }
        catch (COMException) { }
        try { value = accessible.get_accValue(childId); }
        catch (COMException) { }
        try
        {
            var roleValue = accessible.get_accRole(childId);
            if (roleValue is not null)
            {
                role = (AccessibleRole)Convert.ToInt32(
                    roleValue,
                    CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is COMException or FormatException or InvalidCastException)
        {
            // Firefox can update an accessibility node while it is being read.
        }
        try
        {
            var stateValue = accessible.get_accState(childId);
            if (stateValue is not null)
            {
                state = (AccessibleStates)Convert.ToInt32(
                    stateValue,
                    CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is COMException or FormatException or InvalidCastException)
        {
            // Firefox can update an accessibility node while it is being read.
        }
        return new AccessibleNode(accessible, childId, name, value, role, state);
    }

    private static bool TryActivate(AccessibleNode node)
    {
        try
        {
            node.Accessible.accSelect(
                SelectTakeFocus | SelectTakeSelection,
                node.ChildId);
        }
        catch (COMException)
        {
            // The default action can still work without selecting the link first.
        }
        try
        {
            node.Accessible.accDoDefaultAction(node.ChildId);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool IsEnabled(AccessibleNode node) =>
        !node.State.HasFlag(AccessibleStates.Unavailable) &&
        !node.State.HasFlag(AccessibleStates.Invisible);

    private static bool IsSupportedWristbandPrinter(string? printerName) =>
        !string.IsNullOrWhiteSpace(printerName) &&
        printerName.Length == 4 &&
        printerName.StartsWith("WB-", StringComparison.OrdinalIgnoreCase) &&
        printerName[3] is >= '1' and <= '7';

    private static bool HasPdfSignature(byte[]? bytes) =>
        bytes is { Length: >= 5 } &&
        bytes[0] == (byte)'%' &&
        bytes[1] == (byte)'P' &&
        bytes[2] == (byte)'D' &&
        bytes[3] == (byte)'F' &&
        bytes[4] == (byte)'-';

    private static bool TextIdentifiesWristbandReturnLink(string? name, string? value)
    {
        if (Contains(name, "Mullet Hop") || Contains(name, "MulletHop") ||
            Contains(value, "Mullet Hop") || Contains(value, "MulletHop"))
        {
            return true;
        }
        return IsLilyPadReturnUrl(name) || IsLilyPadReturnUrl(value);
    }

    private static bool IsLilyPadReturnUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "mullet.lilypadpos.app", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return !uri.AbsolutePath.Contains("Wristband", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeLinks(IEnumerable<AccessibleNode> nodes)
    {
        var descriptions = nodes
            .Take(30)
            .Select(node =>
                $"{node.Role} state='{node.State}' name='{node.Name ?? ""}' value='{node.Value ?? ""}'")
            .ToArray();
        return descriptions.Length == 0 ? "none" : string.Join(" | ", descriptions);
    }

    private static bool Contains(string? value, string expected) =>
        value?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true;

    private sealed record AccessibleNode(
        IAccessible Accessible,
        object ChildId,
        string? Name,
        string? Value,
        AccessibleRole Role,
        AccessibleStates State);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr window,
        uint objectId,
        ref Guid interfaceId,
        [In, Out, MarshalAs(UnmanagedType.Interface)] ref object? accessibleObject);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren(
        [MarshalAs(UnmanagedType.Interface)] IAccessible container,
        int childStart,
        int childCount,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children,
        out int obtained);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
}
