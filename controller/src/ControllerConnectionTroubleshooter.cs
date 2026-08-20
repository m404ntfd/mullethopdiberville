using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MulletHopKioskController;

internal enum ControllerDiagnosticState
{
    Passed,
    Warning,
    Failed
}

internal sealed record ControllerDiagnosticCheck(
    string Name,
    ControllerDiagnosticState State,
    string Details);

internal sealed record ControllerDiagnosticSnapshot(
    IReadOnlyList<ControllerDiagnosticCheck> Checks,
    bool LocalRepairRecommended);

internal sealed record ControllerNetworkRepairResult(bool Success, string Message);

internal static class ControllerConnectionDiagnostics
{
    private const string UrlPrefix = "http://+:47832/mullethop/";
    private const string FirewallName = "Mullet Hop Systems Controller (TCP 47832)";
    private const string LegacyFirewallName = "Mullet Hop Kiosk Controller (TCP 47832)";

    public static async Task<ControllerDiagnosticSnapshot> RunAsync(
        ControllerState state,
        ControllerServer server,
        string connectionValue,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<ControllerDiagnosticCheck>();
        var localRepairRecommended = false;

        checks.Add(new ControllerDiagnosticCheck(
            "Controller identity",
            ControllerDiagnosticState.Passed,
            $"{Environment.MachineName} • ID {ShortId(state.ControllerId)} • " +
            (state.IsMaster ? "Master" : "Non-master")));

        if (server.IsRunning)
        {
            checks.Add(new ControllerDiagnosticCheck(
                "Local network service",
                ControllerDiagnosticState.Passed,
                $"Listening on TCP {ControllerServer.Port}."));
        }
        else
        {
            localRepairRecommended = true;
            checks.Add(new ControllerDiagnosticCheck(
                "Local network service",
                ControllerDiagnosticState.Failed,
                $"The Systems Controller is not listening on TCP {ControllerServer.Port}."));
        }

        var urlAcl = await RunProcessAsync(
            "netsh.exe",
            ["http", "show", "urlacl", "url=" + UrlPrefix],
            cancellationToken);
        if (urlAcl.ExitCode == 0)
        {
            checks.Add(new ControllerDiagnosticCheck(
                "Windows URL reservation",
                ControllerDiagnosticState.Passed,
                UrlPrefix + " is reserved."));
        }
        else
        {
            localRepairRecommended = true;
            checks.Add(new ControllerDiagnosticCheck(
                "Windows URL reservation",
                ControllerDiagnosticState.Failed,
                "The TCP 47832 listener reservation is missing or inaccessible."));
        }

        var firewall = await RunPowerShellAsync(
            "$names=@('" + FirewallName + "','" + LegacyFirewallName + "');" +
            "$ok=$false;" +
            "foreach($rule in Get-NetFirewallRule -DisplayName $names -ErrorAction SilentlyContinue){" +
            "if($rule.Enabled -eq 'True' -and ($rule.Profile -match 'Private|Any')){" +
            "$port=$rule|Get-NetFirewallPortFilter;" +
            "if($port.Protocol -eq 'TCP' -and (@($port.LocalPort) -contains '47832')){$ok=$true}}};" +
            "if($ok){'PASS'}else{'MISSING'}",
            cancellationToken);
        if (firewall.ExitCode == 0 && firewall.Output.Contains("PASS", StringComparison.Ordinal))
        {
            checks.Add(new ControllerDiagnosticCheck(
                "Windows Firewall",
                ControllerDiagnosticState.Passed,
                "Inbound TCP 47832 is allowed on Private networks."));
        }
        else
        {
            localRepairRecommended = true;
            checks.Add(new ControllerDiagnosticCheck(
                "Windows Firewall",
                ControllerDiagnosticState.Failed,
                "The Private-network TCP 47832 firewall rule is missing or disabled."));
        }

        var profiles = await RunPowerShellAsync(
            "Get-NetConnectionProfile -ErrorAction SilentlyContinue | " +
            "Where-Object {$_.IPv4Connectivity -ne 'Disconnected'} | " +
            "ForEach-Object {$_.InterfaceAlias+'|'+$_.NetworkCategory}",
            cancellationToken);
        var profileLines = profiles.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains('|'))
            .ToList();
        if (profiles.ExitCode != 0 || profileLines.Count == 0)
        {
            checks.Add(new ControllerDiagnosticCheck(
                "Windows network profile",
                ControllerDiagnosticState.Warning,
                "No active IPv4 Windows network profile could be read."));
        }
        else
        {
            var publicProfiles = profileLines
                .Where(line => line.EndsWith("|Public", StringComparison.OrdinalIgnoreCase))
                .Select(line => line[..line.LastIndexOf('|')])
                .ToList();
            if (publicProfiles.Count > 0)
            {
                localRepairRecommended = true;
                checks.Add(new ControllerDiagnosticCheck(
                    "Windows network profile",
                    ControllerDiagnosticState.Failed,
                    "Active Public profile: " + string.Join(", ", publicProfiles) + "."));
            }
            else
            {
                checks.Add(new ControllerDiagnosticCheck(
                    "Windows network profile",
                    ControllerDiagnosticState.Passed,
                    string.Join(", ", profileLines.Select(line => line.Replace('|', ' '))) + "."));
            }
        }

        try
        {
            await server.Peers.ScanNowAsync(cancellationToken);
            var peers = server.Peers.Snapshot();
            checks.Add(new ControllerDiagnosticCheck(
                "Systems Controller discovery",
                peers.Count > 0 ? ControllerDiagnosticState.Passed : ControllerDiagnosticState.Warning,
                peers.Count > 0
                    ? $"Found {peers.Count} other controller(s): " +
                      string.Join(", ", peers.Select(peer => peer.MachineName)) + "."
                    : "No other Systems Controller answered the local-network scan."));

            var master = peers.FirstOrDefault(peer => peer.IsMaster);
            if (state.IsMaster)
            {
                checks.Add(new ControllerDiagnosticCheck(
                    "Master controller",
                    ControllerDiagnosticState.Passed,
                    "This PC is the master Systems Controller."));
            }
            else if (master is not null)
            {
                checks.Add(new ControllerDiagnosticCheck(
                    "Master controller",
                    ControllerDiagnosticState.Passed,
                    $"Detected {master.MachineName} at {master.ControllerAddress}."));
            }
            else if (state.MasterControllerSnapshot() is { } stored)
            {
                checks.Add(new ControllerDiagnosticCheck(
                    "Master controller",
                    ControllerDiagnosticState.Warning,
                    $"Saved master {stored.MachineName} is not currently answering at " +
                    stored.LastKnownAddress + "."));
            }
            else
            {
                checks.Add(new ControllerDiagnosticCheck(
                    "Master controller",
                    ControllerDiagnosticState.Failed,
                    "No master has been detected or saved on this PC."));
            }

            var target = ResolveTarget(connectionValue, peers, state.MasterControllerSnapshot());
            if (target is not null)
            {
                var tcp = await TestTcpAsync(target.Value.Host, ControllerServer.Port, cancellationToken);
                checks.Add(new ControllerDiagnosticCheck(
                    "Master TCP connection",
                    tcp.Success ? ControllerDiagnosticState.Passed : ControllerDiagnosticState.Failed,
                    tcp.Success
                        ? $"{target.Value.Host}:{ControllerServer.Port} accepted a TCP connection."
                        : $"{target.Value.Host}:{ControllerServer.Port} did not answer: {tcp.Message}"));
            }
            else if (!string.IsNullOrWhiteSpace(connectionValue))
            {
                checks.Add(new ControllerDiagnosticCheck(
                    "Master connection value",
                    ControllerDiagnosticState.Failed,
                    "No discovered master matched that pairing key or address."));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add(new ControllerDiagnosticCheck(
                "Controller discovery",
                ControllerDiagnosticState.Failed,
                ex.Message));
        }

        return new ControllerDiagnosticSnapshot(checks, localRepairRecommended);
    }

    public static async Task<ControllerNetworkRepairResult> RepairLocalNetworkAsync()
    {
        var script =
            "$ErrorActionPreference='Stop';" +
            "$prefix='" + UrlPrefix + "';" +
            "$user=[Security.Principal.WindowsIdentity]::GetCurrent().Name;" +
            "Get-NetConnectionProfile -ErrorAction SilentlyContinue | " +
            "Where-Object {$_.IPv4Connectivity -ne 'Disconnected' -and $_.NetworkCategory -eq 'Public'} | " +
            "Set-NetConnectionProfile -NetworkCategory Private;" +
            "& netsh.exe http delete urlacl url=$prefix 2>$null | Out-Null;" +
            "& netsh.exe http add urlacl url=$prefix user=$user listen=yes | Out-Null;" +
            "if($LASTEXITCODE -ne 0){throw 'Windows could not reserve TCP 47832 for the current user.'};" +
            "$names=@('" + FirewallName + "','" + LegacyFirewallName + "');" +
            "Get-NetFirewallRule -DisplayName $names -ErrorAction SilentlyContinue | Remove-NetFirewallRule;" +
            "New-NetFirewallRule -DisplayName '" + FirewallName + "' -Direction Inbound " +
            "-Action Allow -Protocol TCP -LocalPort 47832 -Profile Private | Out-Null;";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
                return new ControllerNetworkRepairResult(false, "Windows did not start the repair process.");
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? new ControllerNetworkRepairResult(
                    true,
                    "Windows network profile, firewall, and TCP 47832 reservation were repaired.")
                : new ControllerNetworkRepairResult(
                    false,
                    $"The elevated repair process returned exit code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ControllerNetworkRepairResult(
                false,
                "Windows administrator approval was canceled, so no system settings were changed.");
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller network repair error: " + ex.Message);
            return new ControllerNetworkRepairResult(false, "Windows network repair failed: " + ex.Message);
        }
    }

    private static (string Host, string Source)? ResolveTarget(
        string connectionValue,
        IReadOnlyList<DiscoveredControllerPeer> peers,
        StoredMasterControllerConnection? stored)
    {
        connectionValue = connectionValue?.Trim() ?? string.Empty;
        if (TryGetHost(connectionValue, out var enteredHost))
            return (enteredHost, "entered address");

        if (connectionValue.Length >= 16)
        {
            var fingerprint = ControllerSecurity.Fingerprint(connectionValue);
            var keyMatch = peers.FirstOrDefault(peer =>
                peer.IsMaster &&
                string.Equals(peer.PairingKeyFingerprint, fingerprint, StringComparison.Ordinal));
            if (keyMatch is not null && TryGetHost(keyMatch.ControllerAddress, out var keyHost))
                return (keyHost, "pairing key");
            return null;
        }

        var master = peers.FirstOrDefault(peer => peer.IsMaster);
        if (master is not null && TryGetHost(master.ControllerAddress, out var discoveredHost))
            return (discoveredHost, "discovered master");
        if (stored is not null && TryGetHost(stored.LastKnownAddress, out var storedHost))
            return (storedHost, "saved master");
        return null;
    }

    private static bool TryGetHost(string value, out string host)
    {
        host = string.Empty;
        value = value?.Trim() ?? string.Empty;
        if (IPAddress.TryParse(value, out var address))
        {
            host = address.ToString();
            return true;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            host = uri.Host;
            return true;
        }
        return false;
    }

    private static async Task<(bool Success, string Message)> TestTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
            return (true, "Connected");
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or IOException)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunPowerShellAsync(
        string script,
        CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return await RunProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            cancellationToken);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null)
                return (-1, "Process could not start.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, (await standardOutput) + Environment.NewLine + (await standardError));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, ex.Message);
        }
    }

    private static string ShortId(string controllerId) =>
        controllerId.Length <= 8 ? controllerId : controllerId[..8];
}
