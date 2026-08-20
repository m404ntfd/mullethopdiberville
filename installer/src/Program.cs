namespace MulletHopInstaller;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--startup-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            using var form = new InstallerForm();
            form.CreateControl();
            Environment.ExitCode = InstallerForm.SmokeTest() ? 0 : 1;
            return;
        }
        Application.Run(new InstallerForm());
    }
}
