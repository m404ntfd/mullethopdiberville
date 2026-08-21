using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MulletHopPosController;

internal sealed record FirefoxProfileRecoveryResult(
    bool Success,
    string Message,
    IReadOnlyCollection<int> TerminatedProcessIds,
    string? RecoveryUrl);

/// <summary>
/// Owns the dedicated POS Firefox profile across application restarts. Firefox
/// can outlive the WinForms process after a crash or forced shutdown, so relying
/// only on the Process objects held by FirefoxHost is not sufficient.
/// </summary>
internal static class FirefoxProfileRecovery
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const uint ToolhelpSnapshotProcesses = 0x00000002;
    private const int MaximumRecoveryAttempts = 5;
    private const string SessionRecordFileName = "firefox-session.json";
    private static readonly TimeSpan MaximumRecoveryPageAge = TimeSpan.FromHours(4);
    private static readonly TimeSpan RecoveryPageHeartbeatInterval = TimeSpan.FromMinutes(1);
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly object SessionRecordGate = new();
    private static readonly JsonSerializerOptions SessionJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed class FirefoxProcessIdentity
    {
        public int ProcessId { get; set; }
        public DateTime StartTimeUtc { get; set; }
    }

    private sealed class FirefoxSessionRecord
    {
        public FirefoxProcessIdentity? LaunchProcess { get; set; }
        public FirefoxProcessIdentity? WindowProcess { get; set; }
        public string? LastKnownUrl { get; set; }
        public DateTime LastObservedUtc { get; set; }
    }

    private sealed record ProcessSnapshotEntry(int ParentProcessId, string ExecutableName);

    public static FirefoxProfileRecoveryResult PrepareForLaunch(string profilePath)
    {
        Directory.CreateDirectory(profilePath);
        var terminated = new HashSet<int>();
        var errors = new List<string>();
        var recordedSession = ReadRecordedSession(profilePath);
        var recoveryUrl = recordedSession is not null &&
                          recordedSession.LastObservedUtc >= DateTime.UtcNow - MaximumRecoveryPageAge
            ? NormalizeRecoveryUrl(recordedSession.LastKnownUrl)
            : null;

        TerminateRecordedSession(recordedSession, terminated, errors);

        for (var attempt = 1; attempt <= MaximumRecoveryAttempts; attempt++)
        {
            var lockPaths = GetProfileLockPaths(profilePath)
                .Where(File.Exists)
                .ToArray();
            if (lockPaths.Length > 0)
            {
                var snapshot = TakeProcessSnapshot();
                foreach (var processId in GetProcessesLockingFiles(lockPaths, errors))
                    TerminateFirefoxTree(processId, snapshot, terminated, errors);
            }

            if (TryDeleteProfileLocks(profilePath, out var lockError))
            {
                ForgetRecordedSession(profilePath);
                var message = terminated.Count == 0
                    ? "The dedicated Firefox profile is ready."
                    : $"Recovered the dedicated Firefox profile after terminating " +
                      $"orphaned Firefox process tree(s): {string.Join(", ", terminated.Order())}.";
                if (terminated.Count > 0)
                    PosLog.Write(message);
                return new FirefoxProfileRecoveryResult(
                    true,
                    message,
                    terminated.ToArray(),
                    recoveryUrl);
            }

            if (!string.IsNullOrWhiteSpace(lockError))
                errors.Add(lockError);
            Thread.Sleep(200 * attempt);
        }

        var detail = errors
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .LastOrDefault();
        var failure =
            "Firefox's dedicated Mullet Hop POS profile is still locked after cleanup." +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail);
        PosLog.Write("Firefox profile recovery failed: " + failure);
        return new FirefoxProfileRecoveryResult(
            false,
            failure,
            terminated.ToArray(),
            recoveryUrl);
    }

    public static void RecordLaunchProcess(
        string profilePath,
        Process process,
        string initialUrl) =>
        UpdateRecordedSession(
            profilePath,
            process,
            isWindowProcess: false,
            initialUrl: initialUrl);

    public static void RecordWindowProcess(string profilePath, Process process) =>
        UpdateRecordedSession(profilePath, process, isWindowProcess: true, initialUrl: null);

    internal static IReadOnlyCollection<int> GetFirefoxProcessTreeIds(int rootProcessId) =>
        GetFirefoxProcessTreeIds(rootProcessId, TakeProcessSnapshot());

    public static void RecordPageUrl(string profilePath, string? url)
    {
        var normalizedUrl = NormalizeRecoveryUrl(url);
        if (normalizedUrl is null)
            return;
        try
        {
            lock (SessionRecordGate)
            {
                var record = ReadRecordedSession(profilePath);
                if (record is null)
                    return;

                var now = DateTime.UtcNow;
                if (string.Equals(
                        record.LastKnownUrl,
                        normalizedUrl,
                        StringComparison.Ordinal) &&
                    now - record.LastObservedUtc < RecoveryPageHeartbeatInterval)
                {
                    return;
                }

                record.LastKnownUrl = normalizedUrl;
                record.LastObservedUtc = now;
                SaveRecordedSession(profilePath, record);
            }
        }
        catch (Exception ex)
        {
            PosLog.Write("Firefox recovery-page write error: " + ex.Message);
        }
    }

    public static void ForgetRecordedSession(string profilePath)
    {
        try
        {
            lock (SessionRecordGate)
            {
                var path = GetSessionRecordPath(profilePath);
                if (File.Exists(path))
                    File.Delete(path);
                var temporaryPath = path + ".new";
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        catch (Exception ex)
        {
            PosLog.Write("Firefox session-record cleanup error: " + ex.Message);
        }
    }

    internal static void RunSmokeTest()
    {
        var snapshot = new Dictionary<int, ProcessSnapshotEntry>
        {
            [8264] = new(1988, "firefox.exe"),
            [1988] = new(3092, "firefox.exe"),
            [3092] = new(0, "MulletHopPosController.exe")
        };
        if (FindHighestFirefoxAncestor(8264, snapshot) != 1988)
        {
            throw new InvalidOperationException(
                "Firefox profile recovery did not resolve the root Firefox ancestor.");
        }

        var treeIds = GetFirefoxProcessTreeIds(1988, snapshot);
        if (!new HashSet<int>(treeIds).SetEquals(new[] { 1988, 8264 }))
        {
            throw new InvalidOperationException(
                "Firefox profile recovery included an unrelated process in the POS tree.");
        }

        var smokeRoot = Path.Combine(
            Path.GetTempPath(),
            "MulletHopPosFirefoxRecoverySmoke",
            Guid.NewGuid().ToString("N"));
        var profilePath = Path.Combine(smokeRoot, "FirefoxProfile");
        Directory.CreateDirectory(profilePath);
        try
        {
            SaveRecordedSession(profilePath, new FirefoxSessionRecord
            {
                LastKnownUrl = "https://mullet.lilypadpos.app/public/WaiverAddToSale.php?ArrayKey=0",
                LastObservedUtc = DateTime.UtcNow
            });
            File.WriteAllText(Path.Combine(profilePath, "parent.lock"), "smoke");
            var result = PrepareForLaunch(profilePath);
            if (!result.Success ||
                File.Exists(Path.Combine(profilePath, "parent.lock")) ||
                !string.Equals(
                    result.RecoveryUrl,
                    "https://mullet.lilypadpos.app/public/WaiverAddToSale.php?ArrayKey=0",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Firefox profile recovery did not remove an unowned lock and retain its page.");
            }
            SaveRecordedSession(profilePath, new FirefoxSessionRecord
            {
                LastKnownUrl = "https://mullet.lilypadpos.app/public/OldSale.php",
                LastObservedUtc = DateTime.UtcNow - MaximumRecoveryPageAge - TimeSpan.FromMinutes(1)
            });
            File.WriteAllText(Path.Combine(profilePath, "parent.lock"), "smoke");
            result = PrepareForLaunch(profilePath);
            if (!result.Success || result.RecoveryUrl is not null)
            {
                throw new InvalidOperationException(
                    "Firefox profile recovery retained an expired page.");
            }
        }
        finally
        {
            if (Directory.Exists(smokeRoot))
                Directory.Delete(smokeRoot, recursive: true);
        }
    }

    private static void UpdateRecordedSession(
        string profilePath,
        Process process,
        bool isWindowProcess,
        string? initialUrl)
    {
        try
        {
            process.Refresh();
            var identity = new FirefoxProcessIdentity
            {
                ProcessId = process.Id,
                StartTimeUtc = process.StartTime.ToUniversalTime()
            };
            lock (SessionRecordGate)
            {
                var record = ReadRecordedSession(profilePath) ?? new FirefoxSessionRecord();
                if (isWindowProcess)
                {
                    record.WindowProcess = identity;
                    record.LastObservedUtc = DateTime.UtcNow;
                }
                else
                {
                    record = new FirefoxSessionRecord
                    {
                        LaunchProcess = identity,
                        LastKnownUrl = NormalizeRecoveryUrl(initialUrl),
                        LastObservedUtc = DateTime.UtcNow
                    };
                }
                SaveRecordedSession(profilePath, record);
            }
        }
        catch (Exception ex)
        {
            PosLog.Write("Firefox session-record write error: " + ex.Message);
        }
    }

    private static void SaveRecordedSession(
        string profilePath,
        FirefoxSessionRecord record)
    {
        var path = GetSessionRecordPath(profilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".new";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record, SessionJsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void TerminateRecordedSession(
        FirefoxSessionRecord? record,
        ISet<int> terminated,
        ICollection<string> errors)
    {
        if (record is null)
            return;

        var identities = new[] { record.LaunchProcess, record.WindowProcess }
            .Where(identity => identity is not null)
            .Cast<FirefoxProcessIdentity>()
            .GroupBy(identity => identity.ProcessId)
            .Select(group => group.First());
        foreach (var identity in identities)
        {
            if (!TryOpenRecordedFirefoxProcess(identity, out var process))
                continue;
            using (process)
            {
                TerminateProcessTree(process, terminated, errors, "recorded POS Firefox session");
            }
        }
    }

    private static bool TryOpenRecordedFirefoxProcess(
        FirefoxProcessIdentity identity,
        out Process process)
    {
        process = null!;
        try
        {
            var candidate = Process.GetProcessById(identity.ProcessId);
            candidate.Refresh();
            var startTimeUtc = candidate.StartTime.ToUniversalTime();
            using var current = Process.GetCurrentProcess();
            if (!string.Equals(candidate.ProcessName, "firefox", StringComparison.OrdinalIgnoreCase) ||
                candidate.SessionId != current.SessionId ||
                Math.Abs((startTimeUtc - identity.StartTimeUtc).TotalSeconds) > 2)
            {
                candidate.Dispose();
                return false;
            }

            process = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static FirefoxSessionRecord? ReadRecordedSession(string profilePath)
    {
        try
        {
            var path = GetSessionRecordPath(profilePath);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<FirefoxSessionRecord>(
                    File.ReadAllText(path),
                    SessionJsonOptions)
                : null;
        }
        catch (Exception ex)
        {
            PosLog.Write("Firefox session-record read error: " + ex.Message);
            return null;
        }
    }

    private static string GetSessionRecordPath(string profilePath) =>
        Path.Combine(
            Directory.GetParent(profilePath)?.FullName ?? PosLog.DataDirectory,
            SessionRecordFileName);

    private static string? NormalizeRecoveryUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "mullet.lilypadpos.app", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return uri.AbsoluteUri;
    }

    private static IEnumerable<string> GetProfileLockPaths(string profilePath)
    {
        yield return Path.Combine(profilePath, "parent.lock");
        yield return Path.Combine(profilePath, "lock");
        yield return Path.Combine(profilePath, ".parentlock");
    }

    private static bool TryDeleteProfileLocks(string profilePath, out string error)
    {
        error = string.Empty;
        foreach (var path in GetProfileLockPaths(profilePath))
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"Windows could not release {Path.GetFileName(path)}: {ex.Message}";
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyCollection<int> GetProcessesLockingFiles(
        string[] paths,
        ICollection<string> errors)
    {
        uint sessionHandle = 0;
        var sessionKey = new StringBuilder(64);
        try
        {
            var result = RmStartSession(out sessionHandle, 0, sessionKey);
            if (result != ErrorSuccess)
            {
                errors.Add($"Windows Restart Manager could not start (error {result}).");
                return Array.Empty<int>();
            }

            result = RmRegisterResources(
                sessionHandle,
                (uint)paths.Length,
                paths,
                0,
                null,
                0,
                null);
            if (result != ErrorSuccess)
            {
                errors.Add($"Windows could not inspect the Firefox profile lock (error {result}).");
                return Array.Empty<int>();
            }

            uint needed = 0;
            uint count = 0;
            uint rebootReasons = 0;
            result = RmGetList(
                sessionHandle,
                out needed,
                ref count,
                null,
                ref rebootReasons);
            if (result == ErrorSuccess)
                return Array.Empty<int>();
            if (result != ErrorMoreData)
            {
                errors.Add($"Windows could not list the Firefox profile owner (error {result}).");
                return Array.Empty<int>();
            }

            var processInfo = new RmProcessInfo[needed];
            count = needed;
            result = RmGetList(
                sessionHandle,
                out needed,
                ref count,
                processInfo,
                ref rebootReasons);
            if (result != ErrorSuccess)
            {
                errors.Add($"Windows could not read the Firefox profile owner (error {result}).");
                return Array.Empty<int>();
            }

            return processInfo
                .Take(checked((int)count))
                .Select(info => info.Process.ProcessId)
                .Where(processId => processId > 0)
                .Distinct()
                .ToArray();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            errors.Add("Windows profile-lock inspection is unavailable: " + ex.Message);
            return Array.Empty<int>();
        }
        finally
        {
            if (sessionHandle != 0)
                _ = RmEndSession(sessionHandle);
        }
    }

    private static void TerminateFirefoxTree(
        int processId,
        IReadOnlyDictionary<int, ProcessSnapshotEntry> snapshot,
        ISet<int> terminated,
        ICollection<string> errors)
    {
        var rootProcessId = FindHighestFirefoxAncestor(processId, snapshot);
        Process? process = null;
        try
        {
            process = Process.GetProcessById(rootProcessId);
            process.Refresh();
            using var current = Process.GetCurrentProcess();
            if (!string.Equals(process.ProcessName, "firefox", StringComparison.OrdinalIgnoreCase) ||
                process.SessionId != current.SessionId)
            {
                errors.Add(
                    $"Process {processId} holds the POS Firefox profile but is not a Firefox " +
                    "process in this Windows session.");
                return;
            }

            TerminateProcessTree(process, terminated, errors, "Firefox profile-lock owner");
        }
        catch (ArgumentException)
        {
            // The process released the profile while Restart Manager was queried.
        }
        catch (Exception ex)
        {
            errors.Add($"Could not terminate Firefox process {rootProcessId}: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TerminateProcessTree(
        Process process,
        ISet<int> terminated,
        ICollection<string> errors,
        string reason)
    {
        try
        {
            if (process.HasExited)
                return;
            var processId = process.Id;
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000))
            {
                errors.Add($"Firefox process {processId} did not exit within five seconds.");
                return;
            }
            terminated.Add(processId);
            PosLog.Write($"Terminated Firefox process tree {processId}: {reason}.");
        }
        catch (InvalidOperationException)
        {
            // The process ended between inspection and termination.
        }
        catch (Exception ex)
        {
            errors.Add($"Could not terminate Firefox process {process.Id}: {ex.Message}");
        }
    }

    private static int FindHighestFirefoxAncestor(
        int processId,
        IReadOnlyDictionary<int, ProcessSnapshotEntry> snapshot)
    {
        var current = processId;
        var visited = new HashSet<int>();
        while (visited.Add(current) &&
               snapshot.TryGetValue(current, out var entry) &&
               entry.ParentProcessId > 0 &&
               snapshot.TryGetValue(entry.ParentProcessId, out var parent) &&
               string.Equals(parent.ExecutableName, "firefox.exe", StringComparison.OrdinalIgnoreCase))
        {
            current = entry.ParentProcessId;
        }
        return current;
    }

    private static IReadOnlyCollection<int> GetFirefoxProcessTreeIds(
        int rootProcessId,
        IReadOnlyDictionary<int, ProcessSnapshotEntry> snapshot)
    {
        var result = new HashSet<int> { rootProcessId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (processId, entry) in snapshot)
            {
                if (result.Contains(processId) ||
                    !result.Contains(entry.ParentProcessId) ||
                    !string.Equals(
                        entry.ExecutableName,
                        "firefox.exe",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(processId);
                changed = true;
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<int, ProcessSnapshotEntry> TakeProcessSnapshot()
    {
        var result = new Dictionary<int, ProcessSnapshotEntry>();
        var snapshot = CreateToolhelp32Snapshot(ToolhelpSnapshotProcesses, 0);
        if (snapshot == InvalidHandleValue)
            return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
                return result;
            do
            {
                result[unchecked((int)entry.ProcessId)] = new ProcessSnapshotEntry(
                    unchecked((int)entry.ParentProcessId),
                    entry.ExecutableFile ?? string.Empty);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private enum RmApplicationType
    {
        Unknown,
        MainWindow,
        OtherWindow,
        Service,
        Explorer,
        Console,
        Critical
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ApplicationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public RmApplicationType ApplicationType;
        public uint ApplicationStatus;
        public uint TerminalServicesSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(
        out uint sessionHandle,
        int sessionFlags,
        StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        string[] fileNames,
        uint applicationCount,
        RmUniqueProcess[]? applications,
        uint serviceCount,
        string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint processInfoNeeded,
        ref uint processInfo,
        [In, Out] RmProcessInfo[]? affectedApplications,
        ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
