using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
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

    private static void PrintRenderedPages(
        IReadOnlyList<Bitmap> pages,
        string printerName,
        CancellationToken cancellationToken)
    {
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
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(
                image,
                new RectangleF(targetX, targetY, targetWidth, targetHeight),
                new RectangleF(0, 0, image.Width, image.Height),
                GraphicsUnit.Pixel);
            pageIndex++;
            eventArgs.HasMorePages = pageIndex < pages.Count;
        };
        cancellationToken.ThrowIfCancellationRequested();
        document.Print();
    }

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
