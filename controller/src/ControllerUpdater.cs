using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace MulletHopKioskController;

internal enum ControllerUpdateStatus
{
    UpToDate,
    ReadyToInstall,
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
    private static UpdateManager? _stagedManager;
    private static UpdateInfo? _stagedUpdate;

    public static bool HasStagedUpdate =>
        _stagedManager is not null && _stagedUpdate is not null;

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

    public static async Task<ControllerUpdateResult> CheckAndStageUpdateAsync()
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
                _stagedManager = null;
                _stagedUpdate = null;
                return new ControllerUpdateResult(
                    ControllerUpdateStatus.UpToDate,
                    $"Controller version {CurrentVersion} is up to date.");
            }

            await manager.DownloadUpdatesAsync(update);
            _stagedManager = manager;
            _stagedUpdate = update;
            ControllerLog.Write(
                $"Controller update {update.TargetFullRelease.Version} is downloaded and ready to install.");
            return new ControllerUpdateResult(
                ControllerUpdateStatus.ReadyToInstall,
                $"Controller version {update.TargetFullRelease.Version} has been downloaded. " +
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
            ControllerLog.Write("Controller update check/download error: " +
                ex.GetType().Name + " - " + ex.Message);
            return new ControllerUpdateResult(
                ControllerUpdateStatus.Failed,
                "The controller update check failed. Verify the internet connection and try again.");
        }
    }

    public static ControllerUpdateResult ApplyStagedUpdateAndRestart()
    {
        if (_stagedManager is null || _stagedUpdate is null)
        {
            return new ControllerUpdateResult(
                ControllerUpdateStatus.UpToDate,
                "No downloaded controller update is waiting to be installed.");
        }

        try
        {
            ControllerLog.Write("The downloaded controller update is being applied.");
            _stagedManager.ApplyUpdatesAndRestart(_stagedUpdate);
            return new ControllerUpdateResult(
                ControllerUpdateStatus.Applying,
                "The controller update is installing. The controller will restart automatically.");
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
