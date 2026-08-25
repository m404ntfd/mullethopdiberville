from pathlib import Path
import re

path = Path("pos-controller/src/DirectWristbandPrinter.cs")
text = path.read_text(encoding="utf-8")

builder = '''    private static string BuildStoredGraphicZplCommand(
        MonochromeRaster raster,
        int printheadWidth,
        string graphicName)
    {
        if (raster.Width != printheadWidth)
        {
            throw new ArgumentException(
                "The stored Zebra graphic must span the complete printhead.",
                nameof(raster));
        }
        if (graphicName.Length is < 1 or > 8 ||
            graphicName.Any(character => !char.IsLetterOrDigit(character)))
        {
            throw new ArgumentException(
                "A Zebra GRF name must contain one to eight letters or digits.",
                nameof(graphicName));
        }

        var hexadecimal = Convert.ToHexString(raster.Bits);
        var command = new StringBuilder(hexadecimal.Length + 256);
        command.Append("~DGR:")
            .Append(graphicName)
            .Append(".GRF,")
            .Append(raster.Bits.Length)
            .Append(',')
            .Append(raster.BytesPerRow)
            .Append(',')
            .Append(hexadecimal)
            .AppendLine();
        command.AppendLine("^XA");
        command.AppendLine("^CI28");
        command.AppendLine("^PON");
        command.AppendLine("^FWN");
        command.AppendLine("^LH0,0");
        command.AppendLine("^LT0");
        command.AppendLine("^LS0");
        command.Append("^PW").AppendLine(printheadWidth.ToString(CultureInfo.InvariantCulture));
        command.Append("^LL").AppendLine(raster.Height.ToString(CultureInfo.InvariantCulture));
        command.Append("^FO0,0^XGR:")
            .Append(graphicName)
            .AppendLine(".GRF,1,1^FS");
        command.AppendLine("^XZ");
        return command.ToString();
    }
'''

pattern = (
    r"    private static string BuildStoredGraphicZplCommand\(.*?\n"
    r"    \}\n\n(?=    private static bool IsNativeZplDriver)"
)
text, count = re.subn(
    pattern,
    lambda _: builder + "\n",
    text,
    count=1,
    flags=re.S,
)
assert count == 1

text = text.replace(
    "Keep the entire ^GF wire raster at full printhead width and embed the",
    "Keep the entire stored GRF raster at full printhead width and embed the",
    1,
)

path.write_text(text, encoding="utf-8")
