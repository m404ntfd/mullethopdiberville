using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace MulletHopKioskController;

internal enum PosStartupStatus
{
    Disabled,
    Started,
    AlreadyRunning,
    NotInstalled,
    Failed
}

internal sealed record PosStartupResult(
    PosStartupStatus Status,
    string Message);

internal static class ControllerWindowsStartup
{
    public const string StartupArgument = "--windows-startup";
    public const string TaskName = "Mullet Hop Systems Controller";
    private const string ControllerInstallFolderName = "MulletHop.KioskController";
    private const string ControllerExecutableName = "MulletHopKioskController.exe";
    private const string PosInstallFolderName = "MulletHop.POSController";
    private const string PosDataFolderName = "MulletHopPosController";
    private const string PosExecutableName = "MulletHopPosController.exe";

    public static bool IsWindowsStartup(string[] args) =>
        args.Contains(StartupArgument, StringComparer.OrdinalIgnoreCase);

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool RestartElevated(string[] args)
    {
        try
        {
            var executable = Environment.ProcessPath ?? Application.ExecutablePath;
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            foreach (var argument in args)
                start.ArgumentList.Add(argument);

            _ = Process.Start(start) ?? throw new InvalidOperationException(
                "Windows did not create the elevated controller process.");
            ControllerLog.Write("Restarting the Systems Controller with administrator privileges.");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ControllerLog.Write("Systems Controller administrator approval was canceled.");
            MessageBox.Show(
                "The Systems Controller needs administrator approval so its network service, " +
                "automatic startup task, and connected-system functions can run. No controller " +
                "was started because the Windows approval prompt was canceled.",
                "Administrator Approval Required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Systems Controller elevation error: " + ex.Message);
            MessageBox.Show(
                "Windows could not start the Systems Controller as an administrator.\n\n" +
                ex.Message,
                "Mullet Hop Systems Controller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
    }

    public static bool EnsureRegistered(bool startedByTask, out string error)
    {
        error = string.Empty;
        var executable = ResolveInstalledControllerExecutable();
        if (executable is null)
            return true;

        if (startedByTask)
        {
            RemoveLegacyStartupShortcuts();
            return true;
        }

        object? serviceObject = null;
        object? rootFolderObject = null;
        object? taskDefinitionObject = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service")
                              ?? throw new InvalidOperationException(
                                  "Windows Task Scheduler is unavailable.");
            serviceObject = Activator.CreateInstance(serviceType)
                            ?? throw new InvalidOperationException(
                                "Windows Task Scheduler could not be opened.");
            dynamic service = serviceObject;
            service.Connect();
            rootFolderObject = service.GetFolder("\\");
            taskDefinitionObject = service.NewTask(0);
            dynamic task = taskDefinitionObject;

            var userName = WindowsIdentity.GetCurrent().Name;
            task.RegistrationInfo.Description =
                "Starts the Mullet Hop Systems Controller at sign-in with administrator " +
                "privileges and keeps it in the system tray.";
            task.Principal.UserId = userName;
            task.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
            task.Principal.RunLevel = 1; // TASK_RUNLEVEL_HIGHEST
            task.Settings.Enabled = true;
            task.Settings.StartWhenAvailable = true;
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.ExecutionTimeLimit = "PT0S";
            task.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
            task.Settings.RestartInterval = "PT1M";
            task.Settings.RestartCount = 3;

            dynamic trigger = task.Triggers.Create(9); // TASK_TRIGGER_LOGON
            trigger.UserId = userName;
            trigger.Enabled = true;

            dynamic action = task.Actions.Create(0); // TASK_ACTION_EXEC
            action.Path = executable;
            action.Arguments = StartupArgument;
            action.WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty;

            dynamic rootFolder = rootFolderObject;
            rootFolder.RegisterTaskDefinition(
                TaskName,
                task,
                6, // TASK_CREATE_OR_UPDATE
                userName,
                null,
                3, // TASK_LOGON_INTERACTIVE_TOKEN
                null);

            RemoveLegacyStartupShortcuts();
            ControllerLog.Write(
                "Verified the elevated Windows sign-in task for the Systems Controller.");
            return true;
        }
        catch (Exception ex)
        {
            error = UnwrapException(ex).Message;
            ControllerLog.Write("Systems Controller startup task error: " + error);
            return false;
        }
        finally
        {
            ReleaseComObject(taskDefinitionObject);
            ReleaseComObject(rootFolderObject);
            ReleaseComObject(serviceObject);
        }
    }

    public static PosStartupResult StartPosAfterControllerReady()
    {
        if (!IsPosAutoStartEnabled())
        {
            const string disabledMessage =
                "Mullet Hop POS automatic startup is off in POS Settings.";
            ControllerLog.Write(disabledMessage);
            return new PosStartupResult(PosStartupStatus.Disabled, disabledMessage);
        }

        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            if (IsPosRunningInSession(currentProcess.SessionId))
            {
                const string runningMessage =
                    "Mullet Hop POS is already running in this Windows session.";
                ControllerLog.Write(runningMessage);
                return new PosStartupResult(PosStartupStatus.AlreadyRunning, runningMessage);
            }

            var executable = ResolveInstalledPosExecutable();
            if (executable is null)
            {
                const string missingMessage =
                    "Mullet Hop POS automatic startup is enabled, but POS is not installed on " +
                    "this computer.";
                ControllerLog.Write(missingMessage);
                return new PosStartupResult(PosStartupStatus.NotInstalled, missingMessage);
            }

            _ = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                    UseShellExecute = true
                }) ?? throw new InvalidOperationException(
                "Windows did not create the Mullet Hop POS process.");
            var message =
                $"Started Mullet Hop POS after the controller service became ready: {executable}";
            ControllerLog.Write(message);
            return new PosStartupResult(PosStartupStatus.Started, message);
        }
        catch (Exception ex)
        {
            var message = "Mullet Hop POS could not be started after the controller: " + ex.Message;
            ControllerLog.Write(message);
            return new PosStartupResult(PosStartupStatus.Failed, message);
        }
    }

    public static bool SmokeTest()
    {
        var fakeLocalAppData = Path.Combine("C:", "Users", "MulletHop", "AppData", "Local");
        var posCandidates = GetPosExecutableCandidates(fakeLocalAppData).ToArray();
        return IsWindowsStartup(["--WINDOWS-STARTUP"]) &&
               !IsWindowsStartup(["--wait-for-process", "10"]) &&
               string.Equals(TaskName, "Mullet Hop Systems Controller", StringComparison.Ordinal) &&
               ParsePosAutoStartSetting("{\"StartAutomatically\":true}") &&
               !ParsePosAutoStartSetting("{\"StartAutomatically\":false}") &&
               !ParsePosAutoStartSetting("{\"ControllerUrl\":\"http://localhost\"}") &&
               posCandidates.Length == 2 &&
               posCandidates[0].EndsWith(
                   Path.Combine(PosInstallFolderName, PosExecutableName),
                   StringComparison.OrdinalIgnoreCase) &&
               posCandidates[1].EndsWith(
                   Path.Combine(PosInstallFolderName, "current", PosExecutableName),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPosAutoStartEnabled()
    {
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            PosDataFolderName,
            "settings.json");
        if (!File.Exists(settingsPath))
            return false;

        try
        {
            return ParsePosAutoStartSetting(File.ReadAllText(settingsPath));
        }
        catch (Exception ex)
        {
            ControllerLog.Write(
                "POS automatic-startup setting could not be read; leaving POS stopped: " +
                ex.Message);
            return false;
        }
    }

    private static bool ParsePosAutoStartSetting(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        "StartAutomatically",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind == JsonValueKind.True;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }
        return false;
    }

    private static string? ResolveInstalledControllerExecutable()
    {
        var installFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ControllerInstallFolderName);
        var installedExecutable = Path.Combine(installFolder, ControllerExecutableName);
        if (File.Exists(installedExecutable))
            return installedExecutable;

        var currentExecutable = Environment.ProcessPath ?? Application.ExecutablePath;
        return IsWithinFolder(currentExecutable, installFolder) && File.Exists(currentExecutable)
            ? currentExecutable
            : null;
    }

    private static string? ResolveInstalledPosExecutable() =>
        GetPosExecutableCandidates(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .FirstOrDefault(File.Exists);

    private static IEnumerable<string> GetPosExecutableCandidates(string localApplicationData)
    {
        var installFolder = Path.Combine(localApplicationData, PosInstallFolderName);
        yield return Path.Combine(installFolder, PosExecutableName);
        yield return Path.Combine(installFolder, "current", PosExecutableName);
    }

    private static bool IsPosRunningInSession(int sessionId)
    {
        var processes = Process.GetProcessesByName(
            Path.GetFileNameWithoutExtension(PosExecutableName));
        var found = false;
        foreach (var process in processes)
        {
            try
            {
                if (process.SessionId == sessionId)
                    found = true;
            }
            catch
            {
                // A process can end between enumeration and inspection.
            }
            finally
            {
                process.Dispose();
            }
        }
        return found;
    }

    private static bool IsWithinFolder(string path, string folder)
    {
        var normalizedFolder = Path.GetFullPath(folder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveLegacyStartupShortcuts()
    {
        try
        {
            var startupFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
            foreach (var shortcutName in new[]
                     {
                         "Mullet Hop Kiosk Controller.lnk",
                         "Mullet Hop Systems Controller.lnk"
                     })
            {
                var shortcut = Path.Combine(startupFolder, shortcutName);
                if (File.Exists(shortcut))
                    File.Delete(shortcut);
            }
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Legacy controller startup shortcut cleanup error: " + ex.Message);
        }
    }

    private static Exception UnwrapException(Exception exception)
    {
        while (exception.InnerException is not null &&
               exception is System.Reflection.TargetInvocationException)
        {
            exception = exception.InnerException;
        }
        return exception;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
            return;
        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch
        {
            // Task registration is already complete; COM cleanup is best effort.
        }
    }
}
