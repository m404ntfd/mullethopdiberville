using Velopack;

namespace MulletHopPosController;

internal static class Program
{
    private const string MutexName = "MulletHopPosController.SingleInstance";
    private const string LegacyShortcutName = "Mullet Hop POS Controller.lnk";
    private const string CurrentShortcutName = "Mullet Hop Kiosk Status Viewer.lnk";

    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Contains("--startup-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            using var form = new PosControllerForm(new PosSettings());
            form.CreateControl();
            return;
        }

        using var mutex = new Mutex(true, MutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show(
                "The Mullet Hop Kiosk Status Viewer is already running.",
                "Mullet Hop Kiosk Status Viewer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        MigrateStartMenuShortcut();
        PosUpdater.ApplyAvailableUpdateOnStartup();

        try
        {
            var settings = PosSettings.LoadOrCreate();
            if (settings is not null)
                Application.Run(new PosControllerForm(settings));
        }
        catch (Exception ex)
        {
            PosLog.Write("Fatal startup error: " + ex);
            MessageBox.Show(
                "The Kiosk Status Viewer could not start.\n\n" + ex.Message,
                "Mullet Hop Kiosk Status Viewer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void MigrateStartMenuShortcut()
    {
        try
        {
            var startMenuFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "Mullet Hop");
            var legacyPath = Path.Combine(startMenuFolder, LegacyShortcutName);
            if (!File.Exists(legacyPath))
                return;

            var currentPath = Path.Combine(startMenuFolder, CurrentShortcutName);
            if (File.Exists(currentPath))
                File.Delete(legacyPath);
            else
                File.Move(legacyPath, currentPath);
            PosLog.Write("The Start menu shortcut was renamed to Kiosk Status Viewer.");
        }
        catch (Exception ex)
        {
            PosLog.Write("Start menu shortcut rename error: " + ex.Message);
        }
    }
}

internal static class PosLog
{
    private static readonly object Gate = new();

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MulletHopPosController");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DataDirectory);
                var path = Path.Combine(DataDirectory, "pos-controller.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".old", true);
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never stop front-desk status and controls.
        }
    }
}
