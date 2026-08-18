using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MulletHopWaiverKiosk;

internal sealed partial class KioskForm
{
    private readonly System.Windows.Forms.Timer _remoteManagementTimer = new() { Interval = 5000 };
    private bool _remoteCheckInProgress;
    private string _lastRemoteConnectionError = string.Empty;

    private void InitializeRemoteManagement()
    {
        _remoteManagementTimer.Tick += async (_, _) => await CheckInWithControllerAsync();
    }

    private void StartRemoteManagement()
    {
        _remoteManagementTimer.Start();
        BeginInvoke(new Action(() => _ = CheckInWithControllerAsync()));
    }

    private void StopRemoteManagement()
    {
        _remoteManagementTimer.Stop();
    }

    private async Task CheckInWithControllerAsync()
    {
        if (_remoteCheckInProgress || !_browserReady || !_settings.RemoteManagementEnabled)
            return;

        if (!RemoteManagementProtocol.IsConfigurationValid(
                _settings.RemoteControllerUrl, _settings.RemotePairingKey, out var configurationError))
        {
            LogRemoteConnectionProblem(configurationError);
            return;
        }

        _remoteCheckInProgress = true;
        try
        {
            var request = CreateCheckInRequest();
            var response = await RemoteManagementProtocol.CheckInAsync(
                _settings.RemoteControllerUrl,
                _settings.RemotePairingKey,
                request);

            if (!string.IsNullOrEmpty(_lastRemoteConnectionError))
                KioskLog.Write("Connection to the kiosk controller was restored.");
            _lastRemoteConnectionError = string.Empty;

            if (response.Command is not null &&
                !string.Equals(response.Command.Id, _settings.RemoteLastCommandId, StringComparison.Ordinal))
            {
                await ExecuteRemoteCommandAsync(response.Command);
            }
        }
        catch (Exception ex)
        {
            LogRemoteConnectionProblem(ex.Message);
        }
        finally
        {
            _remoteCheckInProgress = false;
        }
    }

    private KioskCheckInRequest CreateCheckInRequest() => new()
    {
        StationId = _settings.StationId,
        StationName = _settings.StationName,
        MachineName = Environment.MachineName,
        Version = KioskUpdater.CurrentVersion,
        StationClosed = _settings.StationClosed,
        LastCommandId = _settings.RemoteLastCommandId,
        LastCommandSuccess = _settings.RemoteLastCommandSuccess,
        LastCommandMessage = _settings.RemoteLastCommandMessage
    };

    private void LogRemoteConnectionProblem(string message)
    {
        message = string.IsNullOrWhiteSpace(message) ? "Unknown controller connection error." : message.Trim();
        if (string.Equals(message, _lastRemoteConnectionError, StringComparison.Ordinal))
            return;

        _lastRemoteConnectionError = message;
        KioskLog.Write("Kiosk controller connection error: " + message);
    }

    private async Task ExecuteRemoteCommandAsync(KioskRemoteCommand command)
    {
        KioskLog.Write($"Remote command received: {command.Type} ({command.Id}).");

        try
        {
            switch (command.Type)
            {
                case RemoteCommandTypes.SetClosed when command.Closed.HasValue:
                    await SetStationClosedAsync(command.Closed.Value, "remote controller");
                    SaveRemoteCommandResult(
                        command.Id,
                        true,
                        command.Closed.Value
                            ? "The closed screen is on."
                            : "The kiosk is open for waivers.");
                    break;

                case RemoteCommandTypes.CheckUpdate:
                    var checkResult = await KioskUpdater.CheckForUpdateAsync();
                    SaveRemoteCommandResult(
                        command.Id,
                        checkResult.Status is KioskUpdateStatus.UpToDate or KioskUpdateStatus.Available,
                        checkResult.Message);
                    break;

                case RemoteCommandTypes.InstallUpdate:
                    // Save acceptance before Velopack can close and restart the process.
                    SaveRemoteCommandResult(
                        command.Id,
                        true,
                        "The update command was accepted. Checking GitHub and installing if available.");
                    var installResult = await KioskUpdater.CheckDownloadAndApplyAsync();
                    SaveRemoteCommandResult(
                        command.Id,
                        installResult.Status is KioskUpdateStatus.UpToDate or KioskUpdateStatus.Applying,
                        installResult.Message);
                    break;

                default:
                    SaveRemoteCommandResult(command.Id, false, "The controller sent an unsupported command.");
                    break;
            }
        }
        catch (Exception ex)
        {
            SaveRemoteCommandResult(command.Id, false, ex.Message);
            KioskLog.Write("Remote command error: " + ex.GetType().Name + " - " + ex.Message);
        }
    }

    private void SaveRemoteCommandResult(string commandId, bool success, string message)
    {
        _settings.RemoteLastCommandId = commandId;
        _settings.RemoteLastCommandSuccess = success;
        _settings.RemoteLastCommandMessage = message;
        _settings.Save();
        KioskLog.Write("Remote command result: " + message);
    }

    private async Task SetStationClosedAsync(bool closed, string source)
    {
        if (_settings.StationClosed == closed)
            return;

        var previousValue = _settings.StationClosed;
        try
        {
            _settings.StationClosed = closed;
            _settings.Save();

            if (closed)
                ShowStationClosedPage(connectionError: false);
            else
                await ResetForNextGuestAsync(source + " reopened waiver station", showStatus: false);

            KioskLog.Write(closed
                ? source + " turned on the waiver station closed page."
                : source + " turned off the waiver station closed page.");
        }
        catch
        {
            _settings.StationClosed = previousValue;
            throw;
        }
    }
}

internal static class RemoteCommandTypes
{
    public const string SetClosed = "set-closed";
    public const string CheckUpdate = "check-update";
    public const string InstallUpdate = "install-update";
}

internal sealed class KioskCheckInRequest
{
    public string StationId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool StationClosed { get; set; }
    public string LastCommandId { get; set; } = string.Empty;
    public bool LastCommandSuccess { get; set; }
    public string LastCommandMessage { get; set; } = string.Empty;
}

internal sealed class KioskCheckInResponse
{
    public KioskRemoteCommand? Command { get; set; }
}

internal sealed class KioskRemoteCommand
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool? Closed { get; set; }
}

internal sealed record ControllerTestResult(bool Success, string Message);

internal static class RemoteManagementProtocol
{
    private const string TimestampHeader = "X-MulletHop-Timestamp";
    private const string SignatureHeader = "X-MulletHop-Signature";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsConfigurationValid(string controllerUrl, string pairingKey, out string error)
    {
        if (!TryBuildApiUri(controllerUrl, "api/checkin", out _))
        {
            error = "The controller address is not valid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(pairingKey) || pairingKey.Trim().Length < 16)
        {
            error = "The controller pairing key is missing or incomplete.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static async Task<KioskCheckInResponse> CheckInAsync(
        string controllerUrl,
        string pairingKey,
        KioskCheckInRequest checkIn)
    {
        if (!TryBuildApiUri(controllerUrl, "api/checkin", out var uri))
            throw new InvalidOperationException("The controller address is not valid.");

        var body = JsonSerializer.Serialize(checkIn, JsonOptions);
        using var request = CreateSignedRequest(uri, pairingKey, body);
        using var response = await Client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "The controller rejected the pairing key."
                    : $"The controller returned HTTP {(int)response.StatusCode}.");

        VerifySignedResponse(response, pairingKey, responseBody);
        return JsonSerializer.Deserialize<KioskCheckInResponse>(responseBody, JsonOptions)
               ?? new KioskCheckInResponse();
    }

    public static async Task<ControllerTestResult> TestAsync(string controllerUrl, string pairingKey)
    {
        if (!TryBuildApiUri(controllerUrl, "api/health", out var uri))
            return new ControllerTestResult(false, "Enter a valid controller address.");
        if (string.IsNullOrWhiteSpace(pairingKey) || pairingKey.Trim().Length < 16)
            return new ControllerTestResult(false, "Enter the pairing key shown on the controller PC.");

        try
        {
            const string body = "{}";
            using var request = CreateSignedRequest(uri, pairingKey, body);
            using var response = await Client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return new ControllerTestResult(
                    false,
                    response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? "The controller was reached, but the pairing key was rejected."
                        : $"The controller returned HTTP {(int)response.StatusCode}.");
            }

            VerifySignedResponse(response, pairingKey, responseBody);
            return new ControllerTestResult(true, "Connected securely to the Mullet Hop Kiosk Controller.");
        }
        catch (Exception ex)
        {
            return new ControllerTestResult(false, "Could not reach the controller: " + ex.Message);
        }
    }

    private static HttpRequestMessage CreateSignedRequest(Uri uri, string pairingKey, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp);
        request.Headers.TryAddWithoutValidation(SignatureHeader, Sign(pairingKey.Trim(), timestamp, body));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            "MulletHopWaiverKiosk", KioskUpdater.CurrentVersion));
        return request;
    }

    private static void VerifySignedResponse(HttpResponseMessage response, string pairingKey, string body)
    {
        if (!response.Headers.TryGetValues(TimestampHeader, out var timestamps) ||
            !response.Headers.TryGetValues(SignatureHeader, out var signatures))
            throw new InvalidDataException("The controller response was not signed.");

        var timestamp = timestamps.FirstOrDefault() ?? string.Empty;
        var signature = signatures.FirstOrDefault() ?? string.Empty;
        if (!long.TryParse(timestamp, out var unixTime) ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixTime) > 300)
            throw new InvalidDataException("The controller response timestamp was not valid.");

        var expected = Sign(pairingKey.Trim(), timestamp, body);
        if (!FixedTimeEquals(expected, signature))
            throw new InvalidDataException("The controller response signature was not valid.");
    }

    internal static string Sign(string pairingKey, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pairingKey));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "\n" + body)));
    }

    internal static bool FixedTimeEquals(string expected, string actual)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(expected), Convert.FromBase64String(actual));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryBuildApiUri(string controllerUrl, string relativePath, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(controllerUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            return false;

        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        if (string.IsNullOrEmpty(path) || path == "/")
            path = "/mullethop";
        builder.Path = path.TrimEnd('/') + "/";
        builder.Query = string.Empty;
        builder.Fragment = string.Empty;
        uri = new Uri(builder.Uri, relativePath);
        return true;
    }
}
