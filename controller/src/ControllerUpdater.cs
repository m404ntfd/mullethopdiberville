using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace MulletHopKioskController;

internal enum ControllerUpdateStatus
{
    UpToDate,
    Available,
    Applying,
    NotConfigured,
    NotInstalled,
    Failed
}

internal sealed record ControllerUpdateResult(
    ControllerUpdateStatus Status,
    string Message);

internal static class ControllerUpdater
{
    private const string RepositoryMetadataKey = "UpdateRepositoryUrl";
    private const string UpdateChannel = "controller";

    public static string CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            return version is null
                ? "Unknown"
                : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    private static string RepositoryUrl =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, RepositoryMetadataKey, StringComparison.Ordinal))?
            .Value?.Trim() ?? string.Empty;

    public static void ApplyAvailableUpdateOnStartup()
    {
        try
        {
            var result = CheckDownloadAndApplyAsync().GetAwaiter().GetResult();
            ControllerLog.Write("Automatic controller update check: " + result.Message);
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Automatic controller update error: " +
                ex.GetType().Name + " - " + ex.Message);
        }
    }

    public static async Task<ControllerUpdateResult> CheckForUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(RepositoryUrl))
        {
            return new ControllerUpdateResult(
                ControllerUpdateStatus.NotConfigured,
                "This controller build was not created by the GitHub release workflow.");
        }

        try
        {
            var manager = CreateUpdateManager();
            var update = await manager.CheckForUpdatesAsync();
            return update is null
                ? new ControllerUpdateResult(
                    ControllerUpdateStatus.UpToDate,
                    $"Controller version {CurrentVersion} is up to date.")
                : new ControllerUpdateResult(
                    ControllerUpdateStatus.Available,
                    $"Controller version {update.TargetFullRelease.Version} is available. " +
                    $"This computer currently has version {CurrentVersion}.");
        }
        catch (Exception ex) when (
            string.Equals(ex.GetType().Name, "NotInstalledException", StringComparison.Ordinal))
        {
            return new ControllerUpdateResult(
                ControllerUpdateStatus.NotInstalled,
                "Automatic controller updates begin after installing the controller with its new Setup file.");
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller update check error: " +
                ex.GetType().Name + " - " + ex.Message);
            return new ControllerUpdateResult(
                ControllerUpdateStatus.Failed,
                "The controller update check failed. Verify the internet connection and try again.");
        }
    }

    public static async Task<ControllerUpdateResult> CheckDownloadAndApplyAsync()
    {
        if (string.IsNullOrWhiteSpace(RepositoryUrl))
        {
            return new ControllerUpdateResult(
                ControllerUpdateStatus.NotConfigured,
                "This controller build was not created by the GitHub release workflow.");
        }

        try
        {
            var manager = CreateUpdateManager();
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                return new ControllerUpdateResult(
                    ControllerUpdateStatus.UpToDate,
                    $"Controller version {CurrentVersion} is up to date.");
            }

            await manager.DownloadUpdatesAsync(update);
            ControllerLog.Write("A controller update was downloaded and is being applied.");
            manager.ApplyUpdatesAndRestart(update);
            return new ControllerUpdateResult(
                ControllerUpdateStatus.Applying,
                "The controller update is installing. The controller will restart automatically.");
        }
        catch (Exception ex) when (
            string.Equals(ex.GetType().Name, "NotInstalledException", StringComparison.Ordinal))
        {
            return new ControllerUpdateResult(
                ControllerUpdateStatus.NotInstalled,
                "Automatic controller updates begin after installing the controller with its new Setup file.");
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller update error: " +
                ex.GetType().Name + " - " + ex.Message);
            return new ControllerUpdateResult(
                ControllerUpdateStatus.Failed,
                "The controller update failed. Verify the internet connection and try again.");
        }
    }

    private static UpdateManager CreateUpdateManager() => new(
        new GithubSource(RepositoryUrl, accessToken: null, prerelease: false),
        new UpdateOptions { ExplicitChannel = UpdateChannel });
}
