namespace MulletHopKioskController;

internal static class Program
{
    private const string MutexName = "MulletHopKioskController.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            MessageBox.Show(
                "The Mullet Hop Kiosk Controller is already running.",
                "Mullet Hop Kiosk Controller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Application.Run(new ControllerForm());
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Fatal controller error: " + ex);
            MessageBox.Show(
                "The kiosk controller could not start.\n\n" + ex.Message,
                "Mullet Hop Kiosk Controller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}

internal static class ControllerLog
{
    private static readonly object Gate = new();
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MulletHopKioskController");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DataDirectory);
                var path = Path.Combine(DataDirectory, "controller.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".old", true);
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must not stop kiosk management.
        }
    }
}
