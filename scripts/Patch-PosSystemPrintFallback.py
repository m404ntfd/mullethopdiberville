from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


# Persist the selectable wristband print mode.
settings_path = Path("pos-controller/src/PosSettings.cs")
settings = settings_path.read_text(encoding="utf-8")
settings = replace_once(
    settings,
    "    public bool StartAutomatically { get; set; }\n    public WristbandSettingsPackage WristbandSettings { get; set; } = new();\n",
    "    public bool StartAutomatically { get; set; }\n"
    "    public bool UseCustomWristbandPrinterDialog { get; set; } = true;\n"
    "    public WristbandSettingsPackage WristbandSettings { get; set; } = new();\n",
    "PosSettings property")
settings = replace_once(
    settings,
    "        StartAutomatically = StartAutomatically,\n        WristbandSettings = WristbandSettings.Clone(),\n",
    "        StartAutomatically = StartAutomatically,\n"
    "        UseCustomWristbandPrinterDialog = UseCustomWristbandPrinterDialog,\n"
    "        WristbandSettings = WristbandSettings.Clone(),\n",
    "PosSettings clone")
settings = replace_once(
    settings,
    "        StartAutomatically = source.StartAutomatically;\n        WristbandSettings = source.WristbandSettings.Clone();\n",
    "        StartAutomatically = source.StartAutomatically;\n"
    "        UseCustomWristbandPrinterDialog = source.UseCustomWristbandPrinterDialog;\n"
    "        WristbandSettings = source.WristbandSettings.Clone();\n",
    "PosSettings copy")
settings_path.write_text(settings, encoding="utf-8")


# Add the temporary system-print fallback to the POS staff settings screen.
dialog_path = Path("pos-controller/src/PosSettingsDialog.cs")
dialog = dialog_path.read_text(encoding="utf-8")
dialog = replace_once(
    dialog,
    "    private readonly CheckBox _startAutomatically = new();\n",
    "    private readonly CheckBox _startAutomatically = new();\n"
    "    private readonly CheckBox _useCustomWristbandPrinterDialog = new();\n",
    "settings dialog field")
dialog = replace_once(
    dialog,
    "        var wristbands = BuildWristbandGroup();\n        var startup = BuildStartupGroup();\n        var security = BuildSecurityGroup();\n        content.Controls.Add(security);\n        content.Controls.Add(startup);\n        content.Controls.Add(wristbands);\n",
    "        var wristbands = BuildWristbandGroup();\n"
    "        var wristbandPrinting = BuildWristbandPrintingGroup();\n"
    "        var startup = BuildStartupGroup();\n"
    "        var security = BuildSecurityGroup();\n"
    "        content.Controls.Add(security);\n"
    "        content.Controls.Add(startup);\n"
    "        content.Controls.Add(wristbandPrinting);\n"
    "        content.Controls.Add(wristbands);\n",
    "settings dialog group order")
dialog = replace_once(
    dialog,
    "        _startAutomatically.Checked = _working.StartAutomatically;\n        PopulateSlots(_working.RememberedKioskStatuses());\n",
    "        _startAutomatically.Checked = _working.StartAutomatically;\n"
    "        _useCustomWristbandPrinterDialog.Checked =\n"
    "            _working.UseCustomWristbandPrinterDialog;\n"
    "        PopulateSlots(_working.RememberedKioskStatuses());\n",
    "settings dialog initialization")
printing_group = r'''    private GroupBox BuildWristbandPrintingGroup()
    {
        var group = MakeGroup("Wristband Printing Mode", 164);
        group.Dock = DockStyle.Top;

        _useCustomWristbandPrinterDialog.Text =
            "Use the Mullet Hop wristband printer selector (WB-1 through WB-7)";
        _useCustomWristbandPrinterDialog.Bounds = new Rectangle(20, 31, 720, 32);
        _useCustomWristbandPrinterDialog.AutoSize = false;
        _useCustomWristbandPrinterDialog.Font =
            new Font("Segoe UI", 10, FontStyle.Bold);
        _useCustomWristbandPrinterDialog.ForeColor = Color.FromArgb(16, 24, 32);

        var modeNote = new Label
        {
            Bounds = new Rectangle(46, 67, 690, 78),
            ForeColor = Color.FromArgb(83, 97, 109),
            Font = new Font("Segoe UI", 9, FontStyle.Regular)
        };
        void UpdateModeNote()
        {
            if (_useCustomWristbandPrinterDialog.Checked)
            {
                modeNote.Text =
                    "The POS displays the color-coded WB-1 through WB-7 buttons and uses " +
                    "the current direct/background wristband printing path.";
                modeNote.ForeColor = Color.FromArgb(83, 97, 109);
            }
            else
            {
                modeNote.Text =
                    "TEMPORARY FALLBACK: the Mullet Hop selector is bypassed and LilyPad/Firefox " +
                    "uses its normal system print screen. Staff must select the correct wristband " +
                    "printer and complete the print manually.";
                modeNote.ForeColor = Color.FromArgb(156, 87, 0);
            }
        }
        _useCustomWristbandPrinterDialog.CheckedChanged += (_, _) => UpdateModeNote();
        UpdateModeNote();
        group.Controls.Add(_useCustomWristbandPrinterDialog);
        group.Controls.Add(modeNote);
        return group;
    }

'''
dialog = replace_once(
    dialog,
    "    private GroupBox BuildWristbandGroup()\n",
    printing_group + "    private GroupBox BuildWristbandGroup()\n",
    "settings dialog printing group")
dialog = replace_once(
    dialog,
    "        _working.StartAutomatically = _startAutomatically.Checked;\n        try\n",
    "        _working.StartAutomatically = _startAutomatically.Checked;\n"
    "        _working.UseCustomWristbandPrinterDialog =\n"
    "            _useCustomWristbandPrinterDialog.Checked;\n"
    "        try\n",
    "settings dialog save")
dialog_path.write_text(dialog, encoding="utf-8")


# Apply the setting to the running Firefox host and avoid opening the custom selector in fallback mode.
form_path = Path("pos-controller/src/PosControllerForm.cs")
form = form_path.read_text(encoding="utf-8")
form = replace_once(
    form,
    "        _firefoxHost = new FirefoxHost(_browserHostPanel);\n",
    "        _firefoxHost = new FirefoxHost(\n"
    "            _browserHostPanel,\n"
    "            _settings.UseCustomWristbandPrinterDialog);\n",
    "FirefoxHost construction")
form = replace_once(
    form,
    "        if (new PosSettings().StartAutomatically)\n            throw new InvalidOperationException(\"POS automatic startup is not off by default.\");\n",
    "        var defaultSettings = new PosSettings();\n"
    "        if (defaultSettings.StartAutomatically)\n"
    "            throw new InvalidOperationException(\"POS automatic startup is not off by default.\");\n"
    "        if (!defaultSettings.UseCustomWristbandPrinterDialog)\n"
    "            throw new InvalidOperationException(\"The custom wristband printer selector is not enabled by default.\");\n",
    "settings defaults smoke test")
form = replace_once(
    form,
    "        _wristbandPrinterPromptOpen = true;\n        _browserModeActive = false;\n",
    "        if (!_settings.UseCustomWristbandPrinterDialog)\n"
    "        {\n"
    "            PosLog.Write(\n"
    "                \"The Mullet Hop wristband printer selector is disabled. \" +\n"
    "                \"LilyPad/Firefox system printing remains in control of this wristband job.\");\n"
    "            _browserModeActive = true;\n"
    "            _firefoxHost.SetBrowserFocusPreferred(true);\n"
    "            _firefoxHost.FocusBrowser(\"system wristband printing mode\");\n"
    "            return;\n"
    "        }\n\n"
    "        _wristbandPrinterPromptOpen = true;\n"
    "        _browserModeActive = false;\n",
    "system print guard")
form = replace_once(
    form,
    "            _settings.CopyFrom(result == DialogResult.OK\n                ? dialog.Settings\n                : dialog.AppliedSettings!);\n            UpdateUnlinkedCards();\n",
    "            var previousWristbandPrintMode =\n"
    "                _settings.UseCustomWristbandPrinterDialog;\n"
    "            _settings.CopyFrom(result == DialogResult.OK\n"
    "                ? dialog.Settings\n"
    "                : dialog.AppliedSettings!);\n"
    "            _firefoxHost?.SetUseCustomWristbandPrinterDialog(\n"
    "                _settings.UseCustomWristbandPrinterDialog);\n"
    "            if (previousWristbandPrintMode !=\n"
    "                _settings.UseCustomWristbandPrinterDialog)\n"
    "            {\n"
    "                PosLog.Write(\n"
    "                    _settings.UseCustomWristbandPrinterDialog\n"
    "                        ? \"Mullet Hop wristband printer selector enabled in POS settings.\"\n"
    "                        : \"Mullet Hop wristband printer selector disabled; system printing fallback enabled.\");\n"
    "            }\n"
    "            UpdateUnlinkedCards();\n",
    "apply settings to Firefox")
form_path.write_text(form, encoding="utf-8")


# Teach Firefox to let LilyPad use its own print screen when the fallback is selected.
host_path = Path("pos-controller/src/FirefoxHost.cs")
host = host_path.read_text(encoding="utf-8")
host = replace_once(
    host,
    "    private readonly Control _host;\n",
    "    private readonly Control _host;\n"
    "    private bool _useCustomWristbandPrinterDialog;\n",
    "FirefoxHost mode field")
host = replace_once(
    host,
    "    public FirefoxHost(Control host)\n    {\n        _host = host;\n",
    "    public FirefoxHost(\n"
    "        Control host,\n"
    "        bool useCustomWristbandPrinterDialog = true)\n"
    "    {\n"
    "        _host = host;\n"
    "        _useCustomWristbandPrinterDialog = useCustomWristbandPrinterDialog;\n",
    "FirefoxHost constructor")
host = replace_once(
    host,
    "                _compatibilityBridge = new LilyPadCompatibilityBridge(compatibilityPort.Value);\n",
    "                _compatibilityBridge = new LilyPadCompatibilityBridge(\n"
    "                    compatibilityPort.Value,\n"
    "                    _useCustomWristbandPrinterDialog);\n",
    "compatibility bridge construction")
setter = r'''    public void SetUseCustomWristbandPrinterDialog(bool enabled)
    {
        _useCustomWristbandPrinterDialog = enabled;
        _activeWristbandDocumentKey = null;
        _activeWristbandDownloadUrl = null;
        _activeWristbandDownload = null;
        _wristbandPromptRaised = false;
        _compatibilityBridge?.SetUseCustomWristbandPrinterDialog(enabled);
    }

'''
host = replace_once(
    host,
    "    public async Task<PrintDestinationSelectionResult> PrintCurrentPreviewAsync(\n",
    setter + "    public async Task<PrintDestinationSelectionResult> PrintCurrentPreviewAsync(\n",
    "FirefoxHost mode setter")
host = replace_once(
    host,
    "        _wristbandPromptRaised = true;\n        PosLog.Write(\"A LilyPad wristband print page requested a wristband printer selection.\");\n        WristbandPrintRequested?.Invoke(\n            this,\n            new WristbandPrintRequestedEventArgs(wristbandUrl!));\n",
    "        _wristbandPromptRaised = true;\n"
    "        if (!_useCustomWristbandPrinterDialog)\n"
    "        {\n"
    "            PosLog.Write(\n"
    "                \"A LilyPad wristband print page opened while the POS system-print fallback was active. \" +\n"
    "                \"The custom WB-1 through WB-7 selector was not shown.\");\n"
    "            return;\n"
    "        }\n"
    "        PosLog.Write(\"A LilyPad wristband print page requested a wristband printer selection.\");\n"
    "        WristbandPrintRequested?.Invoke(\n"
    "            this,\n"
    "            new WristbandPrintRequestedEventArgs(wristbandUrl!));\n",
    "FirefoxHost prompt bypass")
host_path.write_text(host, encoding="utf-8")


# Make the preload script's print suppression conditional and synchronize the mode with Firefox.
bridge_path = Path("pos-controller/src/LilyPadCompatibilityBridge.cs")
bridge = bridge_path.read_text(encoding="utf-8")
old_wristband_script = r'''          if (/wristband/i.test(location.pathname) &&
              /(pdf|php)$/i.test(location.pathname)) {
            const suppressBrowserPrintDialog = () => {
              document.documentElement?.setAttribute(
                "data-mullet-hop-direct-wristband-print",
                "1");
            };
            try {
              Object.defineProperty(window, "print", {
                configurable: true,
                writable: false,
                value: suppressBrowserPrintDialog
              });
            } catch {
              window.print = suppressBrowserPrintDialog;
            }
            return;
          }
'''
new_wristband_script = r'''          if (/wristband/i.test(location.pathname) &&
              /(pdf|php)$/i.test(location.pathname)) {
            let useCustomSelector = true;
            try {
              const stored = localStorage.getItem(
                "mulletHopUseCustomWristbandPrinterDialog");
              if (stored !== null) {
                useCustomSelector = stored !== "0";
              } else {
                const cookie = document.cookie.match(
                  /(?:^|;\s*)mulletHopUseCustomWristbandPrinterDialog=([01])/);
                if (cookie) {
                  useCustomSelector = cookie[1] !== "0";
                }
              }
            } catch {
            }
            document.documentElement?.setAttribute(
              "data-mullet-hop-wristband-print-mode",
              useCustomSelector ? "custom" : "system");
            if (!useCustomSelector) {
              return;
            }

            const suppressBrowserPrintDialog = () => {
              document.documentElement?.setAttribute(
                "data-mullet-hop-direct-wristband-print",
                "1");
            };
            try {
              Object.defineProperty(window, "print", {
                configurable: true,
                writable: false,
                value: suppressBrowserPrintDialog
              });
            } catch {
              window.print = suppressBrowserPrintDialog;
            }
            return;
          }
'''
bridge = replace_once(
    bridge,
    old_wristband_script,
    new_wristband_script,
    "conditional wristband print suppression")
mode_function = r'''    private const string SetWristbandPrintModeFunction = """
        (useCustomSelector) => {
          const value = useCustomSelector ? "1" : "0";
          try {
            localStorage.setItem(
              "mulletHopUseCustomWristbandPrinterDialog",
              value);
          } catch {
          }
          try {
            document.cookie =
              `mulletHopUseCustomWristbandPrinterDialog=${value}; Path=/; SameSite=Lax`;
          } catch {
          }
          document.documentElement?.setAttribute(
            "data-mullet-hop-wristband-print-mode",
            useCustomSelector ? "custom" : "system");
          return value;
        }
        """;
'''
bridge = replace_once(
    bridge,
    "    private const string DownloadPdfFunction = \"\"\"\n",
    mode_function + "    private const string DownloadPdfFunction = \"\"\"\n",
    "print mode JavaScript function")
bridge = replace_once(
    bridge,
    "    private readonly int _port;\n",
    "    private readonly int _port;\n"
    "    private bool _useCustomWristbandPrinterDialog;\n",
    "bridge mode field")
bridge = replace_once(
    bridge,
    "    public LilyPadCompatibilityBridge(int port)\n    {\n        _port = port;\n    }\n",
    "    public LilyPadCompatibilityBridge(\n"
    "        int port,\n"
    "        bool useCustomWristbandPrinterDialog = true)\n"
    "    {\n"
    "        _port = port;\n"
    "        _useCustomWristbandPrinterDialog = useCustomWristbandPrinterDialog;\n"
    "    }\n",
    "bridge constructor")
bridge_setter = r'''    public void SetUseCustomWristbandPrinterDialog(bool enabled)
    {
        _useCustomWristbandPrinterDialog = enabled;
        ClientWebSocket? socket;
        lock (_socketGate)
            socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyWristbandPrintModeAsync(socket, _stopping.Token);
                PosLog.Write(
                    enabled
                        ? "Firefox wristband print mode changed to the Mullet Hop printer selector."
                        : "Firefox wristband print mode changed to the normal LilyPad/system print screen.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or
                                       InvalidDataException or JsonException)
            {
                PosLog.Write("Firefox wristband print-mode update skipped: " + ex.Message);
            }
        });
    }

'''
bridge = replace_once(
    bridge,
    "    public async Task<LilyPadPdfDownloadResult> DownloadWristbandPdfAsync(\n",
    bridge_setter + "    public async Task<LilyPadPdfDownloadResult> DownloadWristbandPdfAsync(\n",
    "bridge mode setter")
probe_marker = "    private async Task<LilyPadPageHealth?> ProbePageHealthAsync(\n"
apply_method = r'''    private async Task ApplyWristbandPrintModeAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var tree = await SendCommandAsync(
            socket,
            "browsingContext.getTree",
            new { },
            cancellationToken);
        if (!tree.TryGetProperty("contexts", out var contexts) ||
            contexts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var context in EnumerateContexts(contexts))
        {
            if (!context.TryGetProperty("context", out var contextId) ||
                contextId.ValueKind != JsonValueKind.String ||
                !context.TryGetProperty("url", out var url) ||
                url.ValueKind != JsonValueKind.String ||
                !IsLilyPadUrl(url.GetString()))
            {
                continue;
            }

            await SendCommandAsync(
                socket,
                "script.callFunction",
                new
                {
                    functionDeclaration = SetWristbandPrintModeFunction,
                    awaitPromise = false,
                    target = new { context = contextId.GetString() },
                    arguments = new[]
                    {
                        new
                        {
                            type = "boolean",
                            value = _useCustomWristbandPrinterDialog
                        }
                    }
                },
                cancellationToken);
        }
    }

'''
bridge = replace_once(
    bridge,
    probe_marker,
    apply_method + probe_marker,
    "bridge apply mode method")
# Apply the stored setting to the current LilyPad origin after the compatibility script is installed.
configure_tail = "                cancellationToken);\n        }\n    }\n\n    private async Task ApplyWristbandPrintModeAsync(\n"
bridge = replace_once(
    bridge,
    configure_tail,
    "                cancellationToken);\n"
    "        }\n\n"
    "        await ApplyWristbandPrintModeAsync(socket, cancellationToken);\n"
    "    }\n\n"
    "    private async Task ApplyWristbandPrintModeAsync(\n",
    "configure initial print mode")
bridge_path.write_text(bridge, encoding="utf-8")


# Stage the next POS release.
project_path = Path("pos-controller/src/MulletHopPosController.csproj")
project = project_path.read_text(encoding="utf-8")
project = replace_once(
    project,
    "<Version>1.7.23</Version>",
    "<Version>1.7.24</Version>",
    "POS version")
project_path.write_text(project, encoding="utf-8")

print("Applied POS system-print fallback and staged version 1.7.24.")
