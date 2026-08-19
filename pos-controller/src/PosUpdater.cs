using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace MulletHopPosController;

internal enum PosUpdateStatus
{
    UpToDate,
    ReadyToInstall,
    Applying,
    NotConfigured,
    NotInstalled,
    Failed
}

internal sealed record PosUpdateResult(
    PosUpdateStatus Status,
    string Message);

internal static class PosUpdater
{
    private const string RepositoryMetadataKey = "UpdateRepositoryUrl";
    private const string UpdateChannel = "pos";
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

    public static void ApplyAvailableUpdateOnStartup()
    {
        if (string.IsNullOrWhiteSpace(RepositoryUrl))
            return;

        try
        {
            var result = CheckAndStageUpdateAsync().GetAwaiter().GetResult();
            if (result.Status == PosUpdateStatus.ReadyToInstall)
                ApplyStagedUpdateAndRestart();
        }
        catch (Exception ex)
        {
            PosLog.Write("Automatic update check failed: " + ex.Message);
        }
    }

    public static async Task<PosUpdateResult> CheckAndStageUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(RepositoryUrl))
        {
            return new PosUpdateResult(
                PosUpdateStatus.NotConfigured,
                "This POS Controller build was not created by the GitHub release workflow.");
        }

        try
        {
            var manager = CreateUpdateManager();
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                _stagedManager = null;
                _stagedUpdate = null;
                return new PosUpdateResult(
                    PosUpdateStatus.UpToDate,
                    $"POS Controller version {CurrentVersion} is up to date.");
            }

            await manager.DownloadUpdatesAsync(update);
            _stagedManager = manager;
            _stagedUpdate = update;
            PosLog.Write(
                $"POS Controller update {update.TargetFullRelease.Version} is downloaded and ready to install.");
            return new PosUpdateResult(
                PosUpdateStatus.ReadyToInstall,
                $"POS Controller version {update.TargetFullRelease.Version} has been downloaded. " +
                $"This computer currently has version {CurrentVersion}.");
        }
        catch (Exception ex) when (
            string.Equals(ex.GetType().Name, "NotInstalledException", StringComparison.Ordinal))
        {
            return new PosUpdateResult(
                PosUpdateStatus.NotInstalled,
                "POS Controller updates begin after installing the application with its Setup file.");
        }
        catch (Exception ex)
        {
            PosLog.Write("POS Controller update check/download error: " +
                ex.GetType().Name + " - " + ex.Message);
            return new PosUpdateResult(
                PosUpdateStatus.Failed,
                "The POS Controller update check failed. Verify the internet connection and try again.");
        }
    }

    public static PosUpdateResult ApplyStagedUpdateAndRestart()
    {
        if (_stagedManager is null || _stagedUpdate is null)
        {
            return new PosUpdateResult(
                PosUpdateStatus.UpToDate,
                "No downloaded POS Controller update is waiting to be installed.");
        }

        try
        {
            PosLog.Write("The downloaded POS Controller update is being applied.");
            _stagedManager.ApplyUpdatesAndRestart(_stagedUpdate);
            return new PosUpdateResult(
                PosUpdateStatus.Applying,
                "The POS Controller update is installing. The application will restart automatically.");
        }
        catch (Exception ex)
        {
            PosLog.Write("POS Controller update error: " +
                ex.GetType().Name + " - " + ex.Message);
            return new PosUpdateResult(
                PosUpdateStatus.Failed,
                "The POS Controller update failed. Verify the internet connection and try again.");
        }
    }

    private static UpdateManager CreateUpdateManager() => new(
        new GithubSource(RepositoryUrl, accessToken: null, prerelease: false),
        new UpdateOptions { ExplicitChannel = UpdateChannel });
}
