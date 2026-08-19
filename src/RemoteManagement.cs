using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MulletHopWaiverKiosk;

internal sealed partial class KioskForm
{
    private readonly System.Windows.Forms.Timer _remoteManagementTimer = new() { Interval = 5000 };
    private bool _remoteCheckInProgress;
    private bool _advertisementSyncInProgress;
    private DateTime _lastAdvertisementSyncAttemptUtc = DateTime.MinValue;
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

            if (!string.IsNullOrWhiteSpace(response.AdvertisementRevision) &&
                !string.Equals(
                    response.AdvertisementRevision,
                    _settings.AdvertisementSyncRevision,
                    StringComparison.Ordinal) &&
                DateTime.UtcNow - _lastAdvertisementSyncAttemptUtc >= TimeSpan.FromMinutes(1))
            {
                await SyncAdvertisementsFromControllerAsync();
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
        LastCommandMessage = _settings.RemoteLastCommandMessage,
        AdvertisementSyncRevision = _settings.AdvertisementSyncRevision,
        AdvertisementLastSyncUtc = _settings.AdvertisementLastSyncUtc
    };

    private async Task<AdvertisementSyncResult> SyncAdvertisementsFromControllerAsync(
        IProgress<AdvertisementSyncProgress>? progress = null)
    {
        if (_advertisementSyncInProgress)
            return new AdvertisementSyncResult(false, "An advertisement sync is already running.", 0);

        if (!_settings.RemoteManagementEnabled ||
            !RemoteManagementProtocol.IsConfigurationValid(
                _settings.RemoteControllerUrl,
                _settings.RemotePairingKey,
                out var configurationError))
        {
            var localFallback = _settings.AdvertisementLastSyncUtc.HasValue
                ? " The kiosk will keep using the local catalog saved during the last successful sync on " +
                  _settings.AdvertisementLastSyncUtc.Value.ToLocalTime().ToString("MMM d, yyyy h:mm:ss tt") + "."
                : " Existing local kiosk advertisements will remain unchanged.";
            return new AdvertisementSyncResult(
                false,
                "Connect this kiosk to the kiosk manager first. " + configurationError + localFallback,
                0);
        }

        _advertisementSyncInProgress = true;
        _lastAdvertisementSyncAttemptUtc = DateTime.UtcNow;
        var stagingDirectory = Path.Combine(
            KioskSettings.DataDirectory,
            "Advertisements.sync-" + Guid.NewGuid().ToString("N"));
        var backupDirectory = Path.Combine(
            KioskSettings.DataDirectory,
            "Advertisements.backup-" + Guid.NewGuid().ToString("N"));

        try
        {
            progress?.Report(new AdvertisementSyncProgress(2, "Connecting securely to the kiosk manager…"));
            var downloadProgress = new Progress<int>(percent =>
                progress?.Report(new AdvertisementSyncProgress(
                    Math.Clamp(percent, 3, 58),
                    "Downloading the manager advertisement catalog…")));
            var package = await RemoteManagementProtocol.DownloadAdvertisementsAsync(
                _settings.RemoteControllerUrl,
                _settings.RemotePairingKey,
                _settings.StationId,
                downloadProgress);

            if (string.IsNullOrWhiteSpace(package.Revision))
            {
                const string unpublished =
                    "The kiosk manager has not published an advertisement catalog yet.";
                _settings.AdvertisementLastSyncStatus = unpublished;
                _settings.Save();
                progress?.Report(new AdvertisementSyncProgress(0, unpublished));
                return new AdvertisementSyncResult(false, unpublished, 0);
            }
            if (package.Advertisements.Count > 100)
                throw new InvalidDataException("The manager catalog contains too many advertisements.");

            Directory.CreateDirectory(stagingDirectory);
            var advertisements = new List<KioskAdvertisement>();
            var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < package.Advertisements.Count; index++)
            {
                var item = package.Advertisements[index];
                var fileName = Path.GetFileName(item.ImageFileName ?? string.Empty);
                if (!fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    !usedFileNames.Add(fileName))
                    throw new InvalidDataException("The manager catalog contains an invalid JPG filename.");

                byte[] imageBytes;
                try
                {
                    imageBytes = Convert.FromBase64String(item.ImageBase64 ?? string.Empty);
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("A manager advertisement image is not valid.", ex);
                }
                if (imageBytes.Length is 0 or > 25_000_000)
                    throw new InvalidDataException("A manager advertisement JPG has an invalid size.");

                var stagingPath = Path.Combine(stagingDirectory, fileName);
                File.WriteAllBytes(stagingPath, imageBytes);
                using (var image = Image.FromFile(stagingPath))
                {
                    if (image.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                        throw new InvalidDataException("A manager advertisement file is not a valid JPG.");
                }

                var advertisement = new KioskAdvertisement
                {
                    Id = string.IsNullOrWhiteSpace(item.Id)
                        ? Guid.NewGuid().ToString("N")
                        : item.Id,
                    Name = item.Name,
                    ImageFileName = fileName,
                    Enabled = item.Enabled,
                    ScheduleType = item.ScheduleType == (int)AdvertisementScheduleType.Weekly
                        ? AdvertisementScheduleType.Weekly
                        : AdvertisementScheduleType.SpecificDates,
                    StartDateTime = item.StartDateTime,
                    EndDateTime = item.EndDateTime,
                    DaysOfWeek = item.DaysOfWeek
                        .Where(day => day is >= 0 and <= 6)
                        .Select(day => (DayOfWeek)day)
                        .Distinct()
                        .ToArray(),
                    DailyStartTime = item.DailyStartTime,
                    DailyEndTime = item.DailyEndTime
                };
                advertisement.Normalize();
                advertisements.Add(advertisement);

                var validationPercent = package.Advertisements.Count == 0
                    ? 85
                    : 60 + (int)Math.Round((index + 1) * 25d / package.Advertisements.Count);
                progress?.Report(new AdvertisementSyncProgress(
                    validationPercent,
                    $"Preparing advertisement {index + 1} of {package.Advertisements.Count}…"));
            }

            progress?.Report(new AdvertisementSyncProgress(90, "Installing the synchronized catalog…"));
            var originalAdvertisements = _settings.Advertisements;
            var originalRevision = _settings.AdvertisementSyncRevision;
            var originalLastSync = _settings.AdvertisementLastSyncUtc;
            var originalStatus = _settings.AdvertisementLastSyncStatus;
            var advertisementDirectory = KioskSettings.AdvertisementsDirectory;

            try
            {
                if (Directory.Exists(advertisementDirectory))
                    Directory.Move(advertisementDirectory, backupDirectory);
                Directory.Move(stagingDirectory, advertisementDirectory);

                _settings.Advertisements = advertisements;
                _settings.AdvertisementSyncRevision = package.Revision;
                _settings.AdvertisementLastSyncUtc = DateTime.UtcNow;
                _settings.AdvertisementLastSyncStatus =
                    $"Synced {advertisements.Count} advertisement(s) from the kiosk manager.";
                _settings.Save();
            }
            catch
            {
                if (Directory.Exists(advertisementDirectory))
                    Directory.Delete(advertisementDirectory, true);
                if (Directory.Exists(backupDirectory))
                    Directory.Move(backupDirectory, advertisementDirectory);
                _settings.Advertisements = originalAdvertisements;
                _settings.AdvertisementSyncRevision = originalRevision;
                _settings.AdvertisementLastSyncUtc = originalLastSync;
                _settings.AdvertisementLastSyncStatus = originalStatus;
                try { _settings.Save(); } catch { }
                throw;
            }

            try
            {
                if (Directory.Exists(backupDirectory))
                    Directory.Delete(backupDirectory, true);
            }
            catch (Exception ex)
            {
                KioskLog.Write("Advertisement backup cleanup error: " + ex.Message);
            }

            progress?.Report(new AdvertisementSyncProgress(
                100, _settings.AdvertisementLastSyncStatus));
            KioskLog.Write(_settings.AdvertisementLastSyncStatus);
            return new AdvertisementSyncResult(
                true, _settings.AdvertisementLastSyncStatus, advertisements.Count);
        }
        catch (Exception ex)
        {
            var localFallback = _settings.AdvertisementLastSyncUtc.HasValue
                ? " The kiosk is continuing to use the complete local catalog saved during the last successful sync on " +
                  _settings.AdvertisementLastSyncUtc.Value.ToLocalTime().ToString("MMM d, yyyy h:mm:ss tt") + "."
                : " No previously synced manager catalog is available, so existing local kiosk ads remain unchanged.";
            var message = "Advertisement sync failed: " + ex.Message + localFallback;
            _settings.AdvertisementLastSyncStatus = message;
            try { _settings.Save(); } catch { }
            progress?.Report(new AdvertisementSyncProgress(0, message));
            KioskLog.Write(message);
            return new AdvertisementSyncResult(false, message, 0);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, true);
            }
            catch (Exception ex)
            {
                KioskLog.Write("Advertisement sync cleanup error: " + ex.Message);
            }
            _advertisementSyncInProgress = false;
        }
    }

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
    public string AdvertisementSyncRevision { get; set; } = string.Empty;
    public DateTime? AdvertisementLastSyncUtc { get; set; }
}

internal sealed class KioskCheckInResponse
{
    public KioskRemoteCommand? Command { get; set; }
    public string AdvertisementRevision { get; set; } = string.Empty;
}

internal sealed class KioskRemoteCommand
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool? Closed { get; set; }
}

internal sealed record ControllerTestResult(bool Success, string Message);
internal sealed record AdvertisementSyncProgress(int Percent, string Message);
internal sealed record AdvertisementSyncResult(bool Success, string Message, int AdvertisementCount);

internal sealed class AdvertisementSyncPackage
{
    public string Revision { get; set; } = string.Empty;
    public DateTime GeneratedUtc { get; set; }
    public List<AdvertisementSyncItem> Advertisements { get; set; } = [];
}

internal sealed class AdvertisementSyncItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ImageFileName { get; set; } = string.Empty;
    public string ImageBase64 { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int ScheduleType { get; set; }
    public DateTime StartDateTime { get; set; }
    public DateTime EndDateTime { get; set; }
    public int[] DaysOfWeek { get; set; } = [];
    public TimeSpan DailyStartTime { get; set; }
    public TimeSpan DailyEndTime { get; set; }
}

internal static class RemoteManagementProtocol
{
    private const string TimestampHeader = "X-MulletHop-Timestamp";
    private const string SignatureHeader = "X-MulletHop-Signature";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly HttpClient AdvertisementClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };
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

    public static async Task<AdvertisementSyncPackage> DownloadAdvertisementsAsync(
        string controllerUrl,
        string pairingKey,
        string stationId,
        IProgress<int>? progress = null)
    {
        if (!TryBuildApiUri(controllerUrl, "api/ads/sync", out var uri))
            throw new InvalidOperationException("The controller address is not valid.");
        if (!Guid.TryParseExact(stationId, "N", out _))
            throw new InvalidOperationException("The kiosk station ID is not valid.");

        var body = JsonSerializer.Serialize(new { stationId }, JsonOptions);
        using var request = CreateSignedRequest(uri, pairingKey, body);
        using var response = await AdvertisementClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "The controller rejected the pairing key."
                    : $"The controller returned HTTP {(int)response.StatusCode}.");

        var length = response.Content.Headers.ContentLength;
        if (length is > 300_000_000)
            throw new InvalidDataException("The manager advertisement catalog is too large.");

        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var memory = length is > 0 and <= int.MaxValue
            ? new MemoryStream((int)length.Value)
            : new MemoryStream();
        var buffer = new byte[81_920];
        long received = 0;
        while (true)
        {
            var read = await responseStream.ReadAsync(buffer);
            if (read == 0) break;
            received += read;
            if (received > 300_000_000)
                throw new InvalidDataException("The manager advertisement catalog is too large.");
            await memory.WriteAsync(buffer.AsMemory(0, read));
            if (length is > 0)
                progress?.Report(3 + (int)Math.Min(55, received * 55 / length.Value));
        }

        var responseBody = Encoding.UTF8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
        VerifySignedResponse(response, pairingKey, responseBody);
        progress?.Report(58);
        return JsonSerializer.Deserialize<AdvertisementSyncPackage>(responseBody, JsonOptions)
               ?? new AdvertisementSyncPackage();
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
