using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility;

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
/// Opens Firefox's Windows system-print dialog, selects exactly one WB printer,
/// submits the current job once, and follows the PDF's own Mullet Hop return link.
/// The Windows dialog is slower to appear than Firefox's preview, but exposes a
/// stable printer list and gives us a modal window whose closure confirms that the
/// Print action was accepted.
/// </summary>
internal static class FirefoxPrintDestinationSelector
{
    private const uint ObjectIdClient = unchecked((uint)-4);
    private const int ChildIdSelf = 0;
    private const int SelectTakeFocus = 0x1;
    private const int SelectTakeSelection = 0x2;
    private const uint GwOwner = 4;
    private const int MaximumAccessibleNodes = 5000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly Guid AccessibleInterfaceId =
        new("618736E0-3C3D-11CF-810C-00AA00389B71");

    public static Task<PrintDestinationSelectionResult> SelectAndPrintAsync(
        IntPtr firefoxWindow,
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
                "Automatic Firefox printer selection is only available on Windows."));
        }

        var completion = new TaskCompletionSource<PrintDestinationSelectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(SelectAndPrintOnStaThread(
                    firefoxWindow,
                    printerName,
                    cancellationToken));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                PosLog.Write("Windows wristband print automation failed: " + ex);
                completion.TrySetResult(PrintDestinationSelectionResult.Failed(
                    "Windows could not control the current wristband print job: " + ex.Message));
            }
        })
        {
            IsBackground = true,
            Name = "Windows wristband printer selector"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static bool IsSupportedWristbandPrinterForSmokeTest(string? printerName) =>
        IsSupportedWristbandPrinter(printerName);

    internal static bool TextIdentifiesPrinterForSmokeTest(string? value, string printerName) =>
        TextIdentifiesPrinter(value, printerName);

    internal static bool TextIdentifiesPrintActionForSmokeTest(string? value) =>
        TextIdentifiesPrintAction(value);

    internal static bool TextIdentifiesSystemPrintDialogActionForSmokeTest(string? value) =>
        TextIdentifiesSystemPrintDialogAction(value);

    internal static bool TextIdentifiesWristbandReturnLinkForSmokeTest(
        string? name,
        string? value) => TextIdentifiesWristbandReturnLink(name, value);

    private static PrintDestinationSelectionResult SelectAndPrintOnStaThread(
        IntPtr firefoxWindow,
        string printerName,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(firefoxWindow))
        {
            return PrintDestinationSelectionResult.Failed(
                "Firefox closed before the wristband printer could be selected.");
        }

        var openedSystemDialog = OpenSystemPrintDialog(
            firefoxWindow,
            cancellationToken,
            out var previewSummary);
        if (!openedSystemDialog)
        {
            PosLog.Write("Firefox print-preview accessibility summary: " + previewSummary);
            return PrintDestinationSelectionResult.Failed(
                "Firefox's 'Print using the system dialog' option was not available. " +
                $"Select it manually, choose {printerName}, and select Print.");
        }

        var systemDialog = WaitForSystemPrintDialog(firefoxWindow, cancellationToken);
        if (systemDialog == IntPtr.Zero)
        {
            return PrintDestinationSelectionResult.Failed(
                "Firefox did not open the Windows system print dialog. " +
                $"Choose {printerName} manually in the current print screen.");
        }

        PosLog.Write("Firefox opened the Windows system print dialog for the wristband job.");
        var submitted = SelectPrinterAndSubmit(
            systemDialog,
            printerName,
            cancellationToken,
            out var systemDialogSummary,
            out var printWasActivated);
        if (!submitted)
        {
            PosLog.Write("Windows system-print accessibility summary: " + systemDialogSummary);
            return PrintDestinationSelectionResult.Failed(
                printWasActivated
                    ? $"Windows accepted the Print command for {printerName}, but the print dialog did not close. " +
                      "The application did not send the command a second time; check the visible print dialog and printer queue."
                    : $"Windows could not confirm {printerName} and submit the print job. " +
                      $"Choose {printerName} in the visible system print dialog and select Print.");
        }

        PosLog.Write($"Windows confirmed that the wristband print job was submitted to {printerName}.");
        WaitForOrCloseFirefoxPreview(firefoxWindow, cancellationToken);
        var returnLinkActivated = ActivateWristbandReturnLink(
            firefoxWindow,
            cancellationToken,
            out var linkSummary);
        if (returnLinkActivated)
        {
            PosLog.Write("The Mullet Hop logo return link was selected after wristband printing.");
        }
        else
        {
            PosLog.Write("Wristband PDF return-link accessibility summary: " + linkSummary);
        }

        return PrintDestinationSelectionResult.Succeeded(printerName, returnLinkActivated);
    }

    private static bool OpenSystemPrintDialog(
        IntPtr firefoxWindow,
        CancellationToken cancellationToken,
        out string accessibilitySummary)
    {
        accessibilitySummary = "no print-preview controls were exposed";
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(firefoxWindow))
                return false;

            var nodes = ReadAccessibleTree(firefoxWindow);
            accessibilitySummary = DescribePreviewControls(nodes);
            var systemDialogAction = nodes.FirstOrDefault(IsSystemPrintDialogAction);
            if (systemDialogAction is not null && TryActivate(systemDialogAction))
            {
                PosLog.Write("Firefox was asked to open the Windows system print dialog.");
                return true;
            }

            Thread.Sleep(PollInterval);
        }
        return false;
    }

    private static IntPtr WaitForSystemPrintDialog(
        IntPtr firefoxWindow,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(firefoxWindow))
                return IntPtr.Zero;
            var dialog = FindSystemPrintDialog(firefoxWindow);
            if (dialog != IntPtr.Zero)
                return dialog;
            Thread.Sleep(PollInterval);
        }
        return IntPtr.Zero;
    }

    private static bool SelectPrinterAndSubmit(
        IntPtr systemDialog,
        string printerName,
        CancellationToken cancellationToken,
        out string accessibilitySummary,
        out bool printWasActivated)
    {
        accessibilitySummary = "no printer controls were exposed";
        printWasActivated = false;
        var printerSelectionAttempt = -5;
        var selectorOpenAttempt = -10;

        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(systemDialog))
                return false;

            var nodes = ReadAccessibleTree(systemDialog);
            accessibilitySummary = DescribeSystemPrintControls(nodes);
            if (PrinterIsSelected(nodes, printerName))
            {
                var printAction = nodes.FirstOrDefault(IsPrintAction);
                if (printAction is null)
                {
                    Thread.Sleep(PollInterval);
                    continue;
                }

                printWasActivated = TryActivate(printAction);
                if (!printWasActivated)
                    return false;

                PosLog.Write($"Windows Print was activated once for {printerName}.");
                return WaitForDialogToClose(systemDialog, cancellationToken);
            }

            var target = nodes.FirstOrDefault(node =>
                IsEnabled(node) &&
                IsPrinterOption(node) &&
                (TextIdentifiesPrinter(node.Name, printerName) ||
                 TextIdentifiesPrinter(node.Value, printerName)));
            if (target is not null)
            {
                if (attempt - printerSelectionAttempt >= 5)
                {
                    printerSelectionAttempt = attempt;
                    if (TrySelect(target))
                    {
                        PosLog.Write($"Windows was asked to select {printerName}.");
                        Thread.Sleep(PollInterval);
                        continue;
                    }
                }
            }

            if (target is null && attempt - selectorOpenAttempt >= 10)
            {
                var printerSelector = nodes.FirstOrDefault(IsPrinterSelector);
                if (printerSelector is not null && TryActivate(printerSelector))
                    selectorOpenAttempt = attempt;
            }

            Thread.Sleep(PollInterval);
        }
        return false;
    }

    private static bool WaitForDialogToClose(
        IntPtr systemDialog,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(systemDialog) || !IsWindowVisible(systemDialog))
                return true;
            Thread.Sleep(PollInterval);
        }
        return false;
    }

    private static void WaitForOrCloseFirefoxPreview(
        IntPtr firefoxWindow,
        CancellationToken cancellationToken)
    {
        var closeRequested = false;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(firefoxWindow))
                return;

            var nodes = ReadAccessibleTree(firefoxWindow);
            if (!nodes.Any(IsSystemPrintDialogAction))
                return;

            // Normally Firefox closes preview when the Windows dialog accepts Print.
            // If it does not, close only the preview after submission was confirmed.
            if (!closeRequested && attempt >= 4)
            {
                var cancel = nodes.FirstOrDefault(IsCancelAction);
                if (cancel is not null)
                {
                    closeRequested = TryActivate(cancel);
                    if (closeRequested)
                        PosLog.Write("Firefox print preview was closed after Windows confirmed submission.");
                }
            }
            Thread.Sleep(PollInterval);
        }
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

            var nodes = ReadAccessibleTree(firefoxWindow);
            var links = nodes.Where(node =>
                    node.Role == AccessibleRole.Link && IsEnabled(node))
                .ToArray();
            accessibilitySummary = DescribeLinks(links);
            var returnLink = links.FirstOrDefault(node =>
                TextIdentifiesWristbandReturnLink(node.Name, node.Value));
            if (returnLink is not null && TryActivate(returnLink))
                return true;

            Thread.Sleep(PollInterval);
        }
        return false;
    }

    private static IntPtr FindSystemPrintDialog(IntPtr firefoxWindow)
    {
        _ = GetWindowThreadProcessId(firefoxWindow, out var firefoxProcessId);
        var candidates = new List<IntPtr>();
        _ = EnumWindows((window, parameter) =>
        {
            if (window == firefoxWindow || !IsWindowVisible(window))
                return true;

            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId != firefoxProcessId && !IsOwnedBy(window, firefoxWindow))
                return true;

            var title = ReadWindowText(window);
            var className = ReadClassName(window);
            if (Contains(title, "Print") ||
                string.Equals(className, "#32770", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(window);
            }
            return true;
        }, IntPtr.Zero);

        foreach (var candidate in candidates)
        {
            try
            {
                var nodes = ReadAccessibleTree(candidate);
                if (nodes.Any(IsPrintAction) &&
                    (nodes.Any(IsPrinterSelector) || nodes.Any(node =>
                        IsPrinterOption(node) &&
                        (LooksLikePrinterName(node.Name) || LooksLikePrinterName(node.Value)))))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                // A candidate can disappear while Windows builds the print dialog.
            }
        }
        return IntPtr.Zero;
    }

    private static bool IsOwnedBy(IntPtr window, IntPtr expectedOwner)
    {
        var owner = GetWindow(window, GwOwner);
        for (var depth = 0; owner != IntPtr.Zero && depth < 8; depth++)
        {
            if (owner == expectedOwner)
                return true;
            owner = GetWindow(owner, GwOwner);
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
                "The window did not expose its controls to Windows accessibility.");
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
        AccessibleRole role = AccessibleRole.None;
        AccessibleStates state = AccessibleStates.None;
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
            // Controls can omit their role while Windows updates a modal dialog.
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
            // Controls can briefly omit state information while the UI updates.
        }
        return new AccessibleNode(accessible, childId, name, value, role, state);
    }

    private static bool PrinterIsSelected(
        IEnumerable<AccessibleNode> nodes,
        string printerName) => nodes.Any(node =>
            IsEnabled(node) &&
            (TextIdentifiesPrinter(node.Name, printerName) ||
             TextIdentifiesPrinter(node.Value, printerName)) &&
            (node.State.HasFlag(AccessibleStates.Selected) ||
             node.State.HasFlag(AccessibleStates.Checked) ||
             (IsPrinterSelector(node) && node.Role != AccessibleRole.PushButton)));

    private static bool IsPrinterOption(AccessibleNode node) =>
        node.Role is AccessibleRole.ListItem or
            AccessibleRole.MenuItem or
            AccessibleRole.RadioButton or
            AccessibleRole.Cell or
            AccessibleRole.OutlineItem or
            AccessibleRole.PushButton;

    private static bool IsPrinterSelector(AccessibleNode node)
    {
        var isSelector = node.Role is AccessibleRole.ComboBox or
            AccessibleRole.DropList or
            AccessibleRole.List or
            AccessibleRole.ButtonDropDown or
            AccessibleRole.ButtonMenu;
        return isSelector &&
               (Contains(node.Name, "Printer") ||
                Contains(node.Value, "Printer") ||
                Contains(node.Name, "Destination") ||
                Contains(node.Value, "Destination") ||
                LooksLikePrinterName(node.Name) ||
                LooksLikePrinterName(node.Value));
    }

    private static bool IsPrintAction(AccessibleNode node) =>
        node.Role == AccessibleRole.PushButton &&
        IsEnabled(node) &&
        (TextIdentifiesPrintAction(node.Name) || TextIdentifiesPrintAction(node.Value));

    private static bool IsSystemPrintDialogAction(AccessibleNode node) =>
        (node.Role is AccessibleRole.Link or AccessibleRole.PushButton) &&
        IsEnabled(node) &&
        (TextIdentifiesSystemPrintDialogAction(node.Name) ||
         TextIdentifiesSystemPrintDialogAction(node.Value));

    private static bool IsCancelAction(AccessibleNode node) =>
        node.Role == AccessibleRole.PushButton &&
        IsEnabled(node) &&
        (TextIdentifiesExactAction(node.Name, "Cancel") ||
         TextIdentifiesExactAction(node.Value, "Cancel"));

    private static bool IsEnabled(AccessibleNode node) =>
        !node.State.HasFlag(AccessibleStates.Unavailable) &&
        !node.State.HasFlag(AccessibleStates.Invisible);

    private static bool TrySelect(AccessibleNode node)
    {
        try
        {
            node.Accessible.accSelect(
                SelectTakeFocus | SelectTakeSelection,
                node.ChildId);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool TryActivate(AccessibleNode node)
    {
        _ = TrySelect(node);
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

    private static bool IsSupportedWristbandPrinter(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName) || printerName.Length != 4 ||
            !printerName.StartsWith("WB-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return printerName[3] is >= '1' and <= '7';
    }

    private static bool TextIdentifiesPrinter(string? value, string printerName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var index = value.IndexOf(printerName, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var beforeIsBoundary = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            var afterIndex = index + printerName.Length;
            var afterIsBoundary = afterIndex == value.Length ||
                                  !char.IsLetterOrDigit(value[afterIndex]);
            if (beforeIsBoundary && afterIsBoundary)
                return true;
            index = value.IndexOf(printerName, index + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool TextIdentifiesPrintAction(string? value) =>
        TextIdentifiesExactAction(value, "Print");

    private static bool TextIdentifiesSystemPrintDialogAction(string? value) =>
        TextIdentifiesExactAction(value, "Print using the system dialog");

    private static bool TextIdentifiesExactAction(string? value, string expected)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var normalized = value.Trim()
            .Replace("&", string.Empty, StringComparison.Ordinal)
            .TrimEnd('.', '\u2026')
            .Trim();
        return string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase);
    }

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

    private static bool LooksLikePrinterName(string? value) =>
        Contains(value, "POS-X Thermal Printer") ||
        Contains(value, "Save to PDF") ||
        Enumerable.Range(1, 7).Any(number =>
            TextIdentifiesPrinter(value, $"WB-{number}"));

    private static string DescribePreviewControls(IEnumerable<AccessibleNode> nodes) =>
        DescribeNodes(nodes.Where(node =>
            IsSystemPrintDialogAction(node) ||
            IsPrintAction(node) ||
            Contains(node.Name, "system dialog") ||
            Contains(node.Value, "system dialog") ||
            Contains(node.Name, "Destination") ||
            Contains(node.Value, "Destination")));

    private static string DescribeSystemPrintControls(IEnumerable<AccessibleNode> nodes) =>
        DescribeNodes(nodes.Where(node =>
            IsPrinterSelector(node) ||
            (IsPrinterOption(node) &&
             (LooksLikePrinterName(node.Name) || LooksLikePrinterName(node.Value))) ||
            IsPrintAction(node)));

    private static string DescribeLinks(IEnumerable<AccessibleNode> nodes) =>
        DescribeNodes(nodes);

    private static string DescribeNodes(IEnumerable<AccessibleNode> nodes)
    {
        var descriptions = nodes
            .Take(30)
            .Select(node =>
                $"{node.Role} state='{node.State}' name='{node.Name ?? ""}' value='{node.Value ?? ""}'")
            .ToArray();
        return descriptions.Length == 0 ? "none" : string.Join(" | ", descriptions);
    }

    private static string ReadWindowText(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
            return string.Empty;
        var result = new StringBuilder(length + 1);
        _ = GetWindowText(window, result, result.Capacity);
        return result.ToString();
    }

    private static string ReadClassName(IntPtr window)
    {
        var result = new StringBuilder(256);
        _ = GetClassName(window, result, result.Capacity);
        return result.ToString();
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

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

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
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
}
