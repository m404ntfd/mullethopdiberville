using System.Diagnostics;
using Velopack;

namespace MulletHopKioskController;

internal static class Program
{
    private const string MutexName = "MulletHopKioskController.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack must run before the normal controller startup so install,
        // update, and uninstall hooks can finish without opening the dashboard.
        VelopackApp.Build().Run();

        WaitForPreviousInstance(args);

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

    public static void RestartApplication()
    {
        var executable = Environment.ProcessPath ?? Application.ExecutablePath;
        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"--wait-for-process {Environment.ProcessId}",
            UseShellExecute = true
        });
        Application.Exit();
    }

    private static void WaitForPreviousInstance(string[] args)
    {
        if (args.Length != 2 ||
            !string.Equals(args[0], "--wait-for-process", StringComparison.Ordinal) ||
            !int.TryParse(args[1], out var processId))
        {
            return;
        }

        try
        {
            using var previousProcess = Process.GetProcessById(processId);
            previousProcess.WaitForExit(15_000);
        }
        catch (ArgumentException)
        {
            // The previous controller already exited.
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller restart wait error: " +
                ex.GetType().Name + " - " + ex.Message);
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
