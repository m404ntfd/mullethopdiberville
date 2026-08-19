using Velopack;

namespace MulletHopPosController;

internal static class Program
{
    private const string MutexName = "MulletHopPosController.SingleInstance";

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
                "The Mullet Hop POS Controller is already running.",
                "Mullet Hop POS Controller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

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
                "The POS Controller could not start.\n\n" + ex.Message,
                "Mullet Hop POS Controller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
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
            // Logging must never stop front-desk controls.
        }
    }
}
