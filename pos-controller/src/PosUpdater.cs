using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace MulletHopPosController;

internal static class PosUpdater
{
    private const string RepositoryMetadataKey = "UpdateRepositoryUrl";
    private const string UpdateChannel = "pos";

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
            var manager = new UpdateManager(
                new GithubSource(RepositoryUrl, accessToken: null, prerelease: false),
                new UpdateOptions { ExplicitChannel = UpdateChannel });
            var update = manager.CheckForUpdatesAsync().GetAwaiter().GetResult();
            if (update is null)
                return;
            manager.DownloadUpdatesAsync(update).GetAwaiter().GetResult();
            PosLog.Write($"POS Controller update {update.TargetFullRelease.Version} is being installed.");
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex) when (
            string.Equals(ex.GetType().Name, "NotInstalledException", StringComparison.Ordinal))
        {
            PosLog.Write("Automatic updates begin after installing the POS Controller Setup file.");
        }
        catch (Exception ex)
        {
            PosLog.Write("Automatic update check failed: " + ex.Message);
        }
    }
}
