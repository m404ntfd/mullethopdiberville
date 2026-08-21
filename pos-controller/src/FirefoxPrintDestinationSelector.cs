using System.Globalization;
using System.Runtime.InteropServices;
using Accessibility;

namespace MulletHopPosController;

internal readonly record struct PrintDestinationSelectionResult(bool Success, string Message)
{
    public static PrintDestinationSelectionResult Succeeded(string printerName) =>
        new(true, $"Firefox is using {printerName} for this wristband print job.");

    public static PrintDestinationSelectionResult Failed(string message) =>
        new(false, message);
}

/// <summary>
/// Selects one destination in Firefox's currently open print-preview UI without
/// changing the Windows default printer. Firefox exposes its chrome controls to
/// Microsoft Active Accessibility, including its Destination picker.
/// </summary>
internal static class FirefoxPrintDestinationSelector
{
    private const uint ObjectIdClient = unchecked((uint)-4);
    private const int ChildIdSelf = 0;
    private const int SelectTakeFocus = 0x1;
    private const int SelectTakeSelection = 0x2;
    private const int MaximumAccessibleNodes = 5000;
    private static readonly Guid AccessibleInterfaceId =
        new("618736E0-3C3D-11CF-810C-00AA00389B71");

    public static Task<PrintDestinationSelectionResult> SelectAsync(
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
                completion.TrySetResult(SelectOnStaThread(
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
                PosLog.Write("Firefox wristband printer selection failed: " + ex);
                completion.TrySetResult(PrintDestinationSelectionResult.Failed(
                    "Windows could not control Firefox's print destination: " + ex.Message));
            }
        })
        {
            IsBackground = true,
            Name = "Firefox wristband printer selector"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static bool IsSupportedWristbandPrinterForSmokeTest(string? printerName) =>
        IsSupportedWristbandPrinter(printerName);

    internal static bool TextIdentifiesPrinterForSmokeTest(string? value, string printerName) =>
        TextIdentifiesPrinter(value, printerName);

    private static PrintDestinationSelectionResult SelectOnStaThread(
        IntPtr firefoxWindow,
        string printerName,
        CancellationToken cancellationToken)
    {
        AccessibleNode? lastDestinationControl = null;
        string? lastAccessibilitySummary = null;
        for (var attempt = 0; attempt < 32; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsWindow(firefoxWindow))
            {
                return PrintDestinationSelectionResult.Failed(
                    "Firefox closed before the wristband printer could be selected.");
            }

            var nodes = ReadAccessibleTree(firefoxWindow);
            lastAccessibilitySummary = DescribePrinterControls(nodes);
            var currentDestination = nodes.FirstOrDefault(node =>
                IsDestinationControl(node) &&
                (TextIdentifiesPrinter(node.Name, printerName) ||
                 TextIdentifiesPrinter(node.Value, printerName)));
            if (currentDestination is not null)
                return PrintDestinationSelectionResult.Succeeded(printerName);

            var target = nodes.FirstOrDefault(node =>
                (node.Role is AccessibleRole.ListItem or
                    AccessibleRole.MenuItem or
                    AccessibleRole.RadioButton or
                    AccessibleRole.PushButton) &&
                (TextIdentifiesPrinter(node.Name, printerName) ||
                 TextIdentifiesPrinter(node.Value, printerName)));
            if (target is not null && TryActivate(target))
            {
                PosLog.Write(
                    $"Firefox print destination was changed to {printerName} for the current job.");
                return PrintDestinationSelectionResult.Succeeded(printerName);
            }

            lastDestinationControl = nodes.FirstOrDefault(IsDestinationControl) ??
                                     lastDestinationControl;
            if (lastDestinationControl is not null && attempt % 6 == 0)
                _ = TryActivate(lastDestinationControl);

            Thread.Sleep(250);
        }

        PosLog.Write(
            "Firefox print destination accessibility summary: " +
            (lastAccessibilitySummary ?? "no printer controls were exposed"));
        return lastDestinationControl is null
            ? PrintDestinationSelectionResult.Failed(
                "Firefox's Destination control was not available. " +
                $"Select {printerName} manually in the Firefox print panel.")
            : PrintDestinationSelectionResult.Failed(
                $"Firefox did not expose {printerName} in its Destination list. " +
                $"Select {printerName} manually in the Firefox print panel.");
    }

    private static List<AccessibleNode> ReadAccessibleTree(IntPtr firefoxWindow)
    {
        object? accessibleObject = null;
        var interfaceId = AccessibleInterfaceId;
        var result = AccessibleObjectFromWindow(
            firefoxWindow,
            ObjectIdClient,
            ref interfaceId,
            ref accessibleObject);
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
        if (accessibleObject is not IAccessible root)
        {
            throw new InvalidOperationException(
                "Firefox did not expose its print controls to Windows accessibility.");
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
            try
            {
                childCount = node.Accessible.accChildCount;
            }
            catch (COMException)
            {
                continue;
            }
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
            // Some Firefox accessibility nodes omit their role while the UI updates.
        }
        return new AccessibleNode(accessible, childId, name, value, role);
    }

    private static bool IsDestinationControl(AccessibleNode node)
    {
        var isInteractive = node.Role is AccessibleRole.ComboBox or
            AccessibleRole.DropList or
            AccessibleRole.ButtonDropDown or
            AccessibleRole.ButtonMenu or
            AccessibleRole.PushButton;
        if (!isInteractive)
            return false;

        return Contains(node.Name, "Destination") ||
               Contains(node.Value, "Destination") ||
               LooksLikePrinterName(node.Name) ||
               LooksLikePrinterName(node.Value);
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
            // Default action below is sufficient for controls that cannot be selected.
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

    private static bool LooksLikePrinterName(string? value) =>
        Contains(value, "POS-X Thermal Printer") ||
        Contains(value, "Save to PDF") ||
        Enumerable.Range(1, 7).Any(number =>
            TextIdentifiesPrinter(value, $"WB-{number}"));

    private static string DescribePrinterControls(IEnumerable<AccessibleNode> nodes)
    {
        var descriptions = nodes
            .Where(node =>
                IsDestinationControl(node) ||
                Contains(node.Name, "Destination") ||
                Contains(node.Value, "Destination") ||
                LooksLikePrinterName(node.Name) ||
                LooksLikePrinterName(node.Value))
            .Take(20)
            .Select(node =>
                $"{node.Role} name='{node.Name ?? ""}' value='{node.Value ?? ""}'")
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
        AccessibleRole Role);

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
