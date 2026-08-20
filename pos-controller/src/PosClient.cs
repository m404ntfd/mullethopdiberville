using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MulletHopPosController;

internal static class PosCommandTypes
{
    public const string SetClosed = "set-closed";
    public const string SetBusinessClosed = "set-business-closed";
    public const string ResetStart = "reset-start";
    public const string AcknowledgeAssistance = "acknowledge-assistance";
}

internal sealed class PosKioskStatus
{
    public string StationId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public bool StationClosed { get; set; }
    public bool BusinessHoursClosed { get; set; }
    public bool AvailableForGuests { get; set; }
    public bool HasError { get; set; }
    public bool AssistanceRequested { get; set; }
    public bool AssistanceAcknowledged { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
}

internal sealed class PosStatusResponse
{
    public DateTime ServerTimeUtc { get; set; }
    public List<PosKioskStatus> Kiosks { get; set; } = [];
}

internal sealed class PosCommandResponse
{
    public bool Accepted { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal sealed class PosControllerClient
{
    private const string TimestampHeader = "X-MulletHop-Timestamp";
    private const string SignatureHeader = "X-MulletHop-Signature";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Client = CreateClient();
    private readonly string _controllerUrl;
    private readonly string _pairingKey;

    public PosControllerClient(string controllerUrl, string pairingKey)
    {
        _controllerUrl = controllerUrl.Trim();
        _pairingKey = pairingKey.Trim();
    }

    public async Task<PosStatusResponse> GetStatusAsync()
    {
        return await PostAsync<PosStatusResponse>("api/pos/status", "{}");
    }

    public async Task<PosCommandResponse> SendCommandAsync(
        string stationId,
        string commandType,
        bool? closed = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            stationId,
            type = commandType,
            closed
        }, JsonOptions);
        return await PostAsync<PosCommandResponse>("api/pos/command", body);
    }

    public static bool IsConfigurationValid(string controllerUrl, string pairingKey, out string error)
    {
        if (!Uri.TryCreate(controllerUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Enter a valid kiosk controller address.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(pairingKey) || pairingKey.Trim().Length < 16)
        {
            error = "Enter the complete pairing key from the Kiosk Controller.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private async Task<T> PostAsync<T>(string relativePath, string body)
    {
        if (!TryBuildApiUri(_controllerUrl, relativePath, out var uri))
            throw new InvalidOperationException("The kiosk controller address is not valid.");

        using var request = CreateSignedRequest(uri, _pairingKey, body);
        using var response = await Client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "The Kiosk Controller rejected the pairing key."
                    : $"The Kiosk Controller returned HTTP {(int)response.StatusCode}: " +
                      (string.IsNullOrWhiteSpace(responseBody) ? response.ReasonPhrase : responseBody));
        }

        VerifySignedResponse(response, _pairingKey, responseBody);
        return JsonSerializer.Deserialize<T>(responseBody, JsonOptions)
               ?? throw new InvalidDataException("The Kiosk Controller returned an empty response.");
    }

    private static HttpRequestMessage CreateSignedRequest(Uri uri, string pairingKey, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp);
        request.Headers.TryAddWithoutValidation(
            SignatureHeader,
            Sign(pairingKey.Trim(), timestamp, body));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            "MulletHopPosController", PosUpdater.CurrentVersion));
        return request;
    }

    private static void VerifySignedResponse(HttpResponseMessage response, string pairingKey, string body)
    {
        if (!response.Headers.TryGetValues(TimestampHeader, out var timestamps) ||
            !response.Headers.TryGetValues(SignatureHeader, out var signatures))
            throw new InvalidDataException("The Kiosk Controller response was not signed.");

        var timestamp = timestamps.FirstOrDefault() ?? string.Empty;
        var signature = signatures.FirstOrDefault() ?? string.Empty;
        if (!long.TryParse(timestamp, out var unixTime) ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixTime) > 300)
            throw new InvalidDataException("The Kiosk Controller response timestamp was not valid.");

        var expected = Sign(pairingKey.Trim(), timestamp, body);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromBase64String(expected), Convert.FromBase64String(signature)))
                throw new InvalidDataException("The Kiosk Controller response signature was not valid.");
        }
        catch (FormatException)
        {
            throw new InvalidDataException("The Kiosk Controller response signature was not valid.");
        }
    }

    private static string Sign(string pairingKey, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pairingKey));
        return Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "\n" + body)));
    }

    private static bool TryBuildApiUri(string controllerUrl, string relativePath, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(controllerUrl, UriKind.Absolute, out var baseUri))
            return false;
        var builder = new UriBuilder(baseUri);
        var path = builder.Path.TrimEnd('/');
        if (string.IsNullOrEmpty(path))
            path = "/mullethop";
        builder.Path = path + "/";
        builder.Query = string.Empty;
        builder.Fragment = string.Empty;
        uri = new Uri(builder.Uri, relativePath);
        return true;
    }

    private static HttpClient CreateClient() => new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
}
