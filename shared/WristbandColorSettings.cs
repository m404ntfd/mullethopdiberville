namespace MulletHop.Shared;

internal sealed class WristbandColorDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string HexColor { get; set; } = "#D9DEE3";
    public bool IsActive { get; set; } = true;

    public WristbandColorDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        HexColor = HexColor,
        IsActive = IsActive
    };
}

internal sealed class WristbandPrinterColorAssignment
{
    public string PrinterName { get; set; } = string.Empty;
    public string ColorId { get; set; } = string.Empty;

    public WristbandPrinterColorAssignment Clone() => new()
    {
        PrinterName = PrinterName,
        ColorId = ColorId
    };
}

internal sealed class WristbandTimeColorAssignment
{
    // Minutes from the start of the selected business date. Values above 1439
    // represent a slot after midnight for a business day that closes the next day.
    public int StartMinute { get; set; }
    public string ColorId { get; set; } = string.Empty;

    public WristbandTimeColorAssignment Clone() => new()
    {
        StartMinute = StartMinute,
        ColorId = ColorId
    };
}

internal sealed class WristbandDayColorSchedule
{
    public int Day { get; set; }
    public List<WristbandTimeColorAssignment> Slots { get; set; } = [];

    public WristbandDayColorSchedule Clone() => new()
    {
        Day = Day,
        Slots = Slots.Select(slot => slot.Clone()).ToList()
    };
}

internal sealed class WristbandBusinessDayWindow
{
    public int Day { get; set; }
    public bool IsOpen { get; set; }
    public int OpenMinute { get; set; }
    public int LastJumpMinute { get; set; }

    public WristbandBusinessDayWindow Clone() => new()
    {
        Day = Day,
        IsOpen = IsOpen,
        OpenMinute = OpenMinute,
        LastJumpMinute = LastJumpMinute
    };
}

internal sealed class WristbandSettingsPackage
{
    public string Revision { get; set; } = string.Empty;
    public DateTime GeneratedUtc { get; set; }
    public List<WristbandColorDefinition> Colors { get; set; } = [];
    public List<WristbandPrinterColorAssignment> Printers { get; set; } =
        CreateDefaultPrinters();
    public List<WristbandDayColorSchedule> Days { get; set; } = CreateDefaultDays();
    public List<WristbandBusinessDayWindow> BusinessDays { get; set; } = [];

    public static IReadOnlyList<string> ExpectedPrinterNames { get; } =
        Enumerable.Range(1, 7).Select(number => $"WB-{number}").ToArray();

    public WristbandSettingsPackage Clone() => new()
    {
        Revision = Revision,
        GeneratedUtc = GeneratedUtc,
        Colors = Colors.Select(color => color.Clone()).ToList(),
        Printers = Printers.Select(printer => printer.Clone()).ToList(),
        Days = Days.Select(day => day.Clone()).ToList(),
        BusinessDays = BusinessDays.Select(day => day.Clone()).ToList()
    };

    public void Normalize()
    {
        Revision = Clean(Revision, 80);
        Colors ??= [];
        var normalizedColors = new List<WristbandColorDefinition>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Colors.Where(color => color is not null).Take(50))
        {
            var name = Clean(source.Name, 40);
            if (string.IsNullOrWhiteSpace(name) || !usedNames.Add(name))
                continue;
            var id = Clean(source.Id, 80);
            if (string.IsNullOrWhiteSpace(id) || !usedIds.Add(id))
            {
                do { id = Guid.NewGuid().ToString("N"); }
                while (!usedIds.Add(id));
            }
            normalizedColors.Add(new WristbandColorDefinition
            {
                Id = id,
                Name = name,
                HexColor = NormalizeHex(source.HexColor),
                IsActive = source.IsActive
            });
        }
        Colors = normalizedColors;
        var validColorIds = Colors.Select(color => color.Id).ToHashSet(StringComparer.Ordinal);

        Printers ??= [];
        var savedPrinters = Printers
            .Where(printer => printer is not null)
            .GroupBy(printer => Clean(printer.PrinterName, 20), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        Printers = ExpectedPrinterNames.Select(printerName =>
        {
            var colorId = savedPrinters.TryGetValue(printerName, out var saved)
                ? Clean(saved.ColorId, 80)
                : string.Empty;
            return new WristbandPrinterColorAssignment
            {
                PrinterName = printerName,
                ColorId = validColorIds.Contains(colorId) ? colorId : string.Empty
            };
        }).ToList();

        Days ??= [];
        var savedDays = Days
            .Where(day => day is not null && day.Day is >= 0 and <= 6)
            .GroupBy(day => day.Day)
            .ToDictionary(group => group.Key, group => group.Last());
        Days = Enumerable.Range(0, 7).Select(dayNumber =>
        {
            var slots = savedDays.TryGetValue(dayNumber, out var saved)
                ? saved.Slots ?? []
                : [];
            return new WristbandDayColorSchedule
            {
                Day = dayNumber,
                Slots = slots
                    .Where(slot => slot is not null && slot.StartMinute is >= 0 and <= 2879)
                    .GroupBy(slot => slot.StartMinute)
                    .Select(group => group.Last())
                    .Select(slot => new WristbandTimeColorAssignment
                    {
                        StartMinute = slot.StartMinute,
                        ColorId = validColorIds.Contains(Clean(slot.ColorId, 80))
                            ? Clean(slot.ColorId, 80)
                            : string.Empty
                    })
                    .OrderBy(slot => slot.StartMinute)
                    .ToList()
            };
        }).ToList();

        BusinessDays ??= [];
        BusinessDays = BusinessDays
            .Where(day => day is not null && day.Day is >= 0 and <= 6)
            .GroupBy(day => day.Day)
            .Select(group => group.Last().Clone())
            .OrderBy(day => day.Day)
            .ToList();
        foreach (var day in BusinessDays)
        {
            day.OpenMinute = Math.Clamp(day.OpenMinute, 0, 2879);
            day.LastJumpMinute = Math.Clamp(day.LastJumpMinute, 0, 2879);
            if (day.LastJumpMinute < day.OpenMinute)
                day.IsOpen = false;
        }
    }

    public WristbandColorDefinition? ColorForPrinter(string printerName)
    {
        var colorId = Printers.FirstOrDefault(printer =>
            string.Equals(
                printer.PrinterName,
                printerName,
                StringComparison.OrdinalIgnoreCase))?.ColorId;
        return Colors.FirstOrDefault(color =>
            string.Equals(color.Id, colorId, StringComparison.Ordinal));
    }

    public IReadOnlyList<WristbandTimeColorAssignment> SlotsFor(DayOfWeek day)
    {
        var window = BusinessDays.FirstOrDefault(value => value.Day == (int)day);
        if (window is null || !window.IsOpen || window.LastJumpMinute < window.OpenMinute)
            return [];
        var saved = Days.FirstOrDefault(value => value.Day == (int)day)?.Slots
            .ToDictionary(slot => slot.StartMinute, slot => slot.ColorId) ?? [];
        var result = new List<WristbandTimeColorAssignment>();
        for (var minute = window.OpenMinute; minute <= window.LastJumpMinute; minute += 30)
        {
            result.Add(new WristbandTimeColorAssignment
            {
                StartMinute = minute,
                ColorId = saved.GetValueOrDefault(minute) ?? string.Empty
            });
        }
        return result;
    }

    public static List<WristbandPrinterColorAssignment> CreateDefaultPrinters() =>
        ExpectedPrinterNames.Select(printerName => new WristbandPrinterColorAssignment
        {
            PrinterName = printerName
        }).ToList();

    public static List<WristbandDayColorSchedule> CreateDefaultDays() =>
        Enumerable.Range(0, 7).Select(day => new WristbandDayColorSchedule
        {
            Day = day
        }).ToList();

    internal static bool SmokeTest()
    {
        var blue = new WristbandColorDefinition
        {
            Name = "Blue",
            HexColor = "#0055CC"
        };
        var settings = new WristbandSettingsPackage
        {
            Colors = [blue],
            BusinessDays =
            [
                new WristbandBusinessDayWindow
                {
                    Day = (int)DayOfWeek.Monday,
                    IsOpen = true,
                    OpenMinute = 10 * 60,
                    LastJumpMinute = 21 * 60
                }
            ]
        };
        settings.Normalize();
        settings.Printers[2].ColorId = blue.Id;
        settings.Days.First(day => day.Day == (int)DayOfWeek.Monday).Slots.Add(
            new WristbandTimeColorAssignment
            {
                StartMinute = 10 * 60,
                ColorId = blue.Id
            });
        settings.Normalize();
        var slots = settings.SlotsFor(DayOfWeek.Monday);
        return settings.Printers.Count == 7 &&
               settings.Days.Count == 7 &&
               slots.Count == 23 &&
               slots[0].StartMinute == 10 * 60 &&
               slots[^1].StartMinute == 21 * 60 &&
               slots[0].ColorId == blue.Id &&
               settings.ColorForPrinter("WB-3")?.Name == "Blue" &&
               settings.ColorForPrinter("WB-8") is null;
    }

    internal static bool IsValidHex(string? value)
    {
        if (value is null || value.Length != 7 || value[0] != '#')
            return false;
        return value.Skip(1).All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');
    }

    private static string NormalizeHex(string? value) =>
        IsValidHex(value) ? value!.ToUpperInvariant() : "#D9DEE3";

    private static string Clean(string? value, int maximumLength)
    {
        var result = value?.Trim() ?? string.Empty;
        return result.Length <= maximumLength ? result : result[..maximumLength];
    }
}
