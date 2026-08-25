from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


dialog_path = Path("pos-controller/src/PosSettingsDialog.cs")
dialog = dialog_path.read_text(encoding="utf-8")

dialog = replace_once(
    dialog,
    '        var save = MakeButton("Save Settings", Color.FromArgb(117, 68, 154), Color.White);\n        save.Bounds = new Rectangle(520, 15, 145, 42);\n',
    '        var save = MakeButton("Save & Apply Changes", Color.FromArgb(117, 68, 154), Color.White);\n        save.Bounds = new Rectangle(470, 15, 195, 42);\n',
    "footer save button")

dialog = replace_once(
    dialog,
    '        var group = MakeGroup("Wristband Printing Mode", 164);\n',
    '        var group = MakeGroup("Wristband Printing Mode", 220);\n',
    "wristband printing group height")

dialog = replace_once(
    dialog,
    '        group.Controls.Add(_useCustomWristbandPrinterDialog);\n        group.Controls.Add(modeNote);\n        return group;\n',
    '        var apply = MakeButton(\n            "Save & Apply Changes",\n            Color.FromArgb(117, 68, 154),\n            Color.White);\n        apply.Bounds = new Rectangle(545, 158, 205, 42);\n        apply.Click += (_, _) => SaveAndClose();\n\n        group.Controls.Add(_useCustomWristbandPrinterDialog);\n        group.Controls.Add(modeNote);\n        group.Controls.Add(apply);\n        return group;\n',
    "wristband printing apply button")

dialog_path.write_text(dialog, encoding="utf-8")

form_path = Path("pos-controller/src/PosControllerForm.cs")
form = form_path.read_text(encoding="utf-8")
form = replace_once(
    form,
    '        var defaultSettings = new PosSettings();\n        if (defaultSettings.StartAutomatically)\n',
    '        using (var settingsDialog = new PosSettingsDialog(new PosSettings()))\n        {\n            settingsDialog.CreateControl();\n            var saveAndApplyButtons = Descendants(settingsDialog)\n                .OfType<Button>()\n                .Count(button => string.Equals(\n                    button.Text,\n                    "Save & Apply Changes",\n                    StringComparison.Ordinal));\n            if (saveAndApplyButtons < 2)\n                throw new InvalidOperationException(\n                    "The POS settings menu is missing its Save & Apply Changes controls.");\n        }\n\n        var defaultSettings = new PosSettings();\n        if (defaultSettings.StartAutomatically)\n',
    "settings save controls smoke test")
form_path.write_text(form, encoding="utf-8")

project_path = Path("pos-controller/src/MulletHopPosController.csproj")
project = project_path.read_text(encoding="utf-8")
project = replace_once(
    project,
    "    <Version>1.7.24</Version>\n",
    "    <Version>1.7.25</Version>\n",
    "POS version")
project_path.write_text(project, encoding="utf-8")
