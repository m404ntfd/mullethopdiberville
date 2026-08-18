using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MulletHopKioskController;

internal sealed class ControllerServer : IDisposable
{
    public const int Port = 47832;
    public const string BasePath = "/mullethop/";
    private const string TimestampHeader = "X-MulletHop-Timestamp";
    private const string SignatureHeader = "X-MulletHop-Signature";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ControllerState _state;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _listenTask;

    public bool IsRunning => _listener.IsListening;

    public ControllerServer(ControllerState state)
    {
        _state = state;
        _listener.Prefixes.Add($"http://+:{Port}{BasePath}");
    }

    public void Start()
    {
        if (_listener.IsListening)
            return;
        _listener.Start();
        _listenTask = Task.Run(ListenAsync);
        ControllerLog.Write($"Controller service listening on TCP {Port}.");
    }

    private async Task ListenAsync()
    {
        while (!_stopping.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => ProcessRequestAsync(context));
            }
            catch (HttpListenerException) when (_stopping.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ControllerLog.Write("Controller listener error: " + ex.Message);
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WritePlainResponseAsync(context, HttpStatusCode.MethodNotAllowed, "POST required.");
                return;
            }

            if (context.Request.ContentLength64 > 65_536)
            {
                await WritePlainResponseAsync(context, HttpStatusCode.RequestEntityTooLarge, "Request too large.");
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            if (!IsAuthorized(context.Request, body))
            {
                ControllerLog.Write("Rejected a controller request with an invalid signature from " +
                    (context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown address") + ".");
                await WritePlainResponseAsync(context, HttpStatusCode.Unauthorized, "Invalid pairing key.");
                return;
            }

            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            if (string.Equals(path, BasePath.TrimEnd('/') + "/api/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteSignedResponseAsync(context, new
                {
                    ok = true,
                    serverTimeUtc = DateTime.UtcNow
                });
                return;
            }

            if (!string.Equals(path, BasePath.TrimEnd('/') + "/api/checkin", StringComparison.OrdinalIgnoreCase))
            {
                await WritePlainResponseAsync(context, HttpStatusCode.NotFound, "Not found.");
                return;
            }

            var checkIn = JsonSerializer.Deserialize<KioskCheckInRequest>(body, JsonOptions);
            if (checkIn is null || !Guid.TryParseExact(checkIn.StationId, "N", out _))
            {
                await WritePlainResponseAsync(context, HttpStatusCode.BadRequest, "Invalid kiosk check-in.");
                return;
            }

            var command = _state.ProcessCheckIn(
                checkIn,
                context.Request.RemoteEndPoint?.Address.ToString() ?? string.Empty);
            await WriteSignedResponseAsync(context, new KioskCheckInResponse { Command = command });
        }
        catch (JsonException)
        {
            await WritePlainResponseAsync(context, HttpStatusCode.BadRequest, "Invalid JSON.");
        }
        catch (Exception ex)
        {
            ControllerLog.Write("Controller request error: " + ex);
            if (context.Response.OutputStream.CanWrite)
                await WritePlainResponseAsync(context, HttpStatusCode.InternalServerError, "Controller error.");
        }
    }

    private bool IsAuthorized(HttpListenerRequest request, string body)
    {
        var timestamp = request.Headers[TimestampHeader] ?? string.Empty;
        var signature = request.Headers[SignatureHeader] ?? string.Empty;
        if (!long.TryParse(timestamp, out var unixTime) ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixTime) > 300)
            return false;

        var expected = ControllerSecurity.Sign(_state.PairingKey, timestamp, body);
        return ControllerSecurity.FixedTimeEquals(expected, signature);
    }

    private async Task WriteSignedResponseAsync(HttpListenerContext context, object payload)
    {
        var body = JsonSerializer.Serialize(payload, JsonOptions);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        context.Response.Headers[TimestampHeader] = timestamp;
        context.Response.Headers[SignatureHeader] =
            ControllerSecurity.Sign(_state.PairingKey, timestamp, body);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await WriteBodyAsync(context, body);
    }

    private static async Task WritePlainResponseAsync(
        HttpListenerContext context, HttpStatusCode statusCode, string message)
    {
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await WriteBodyAsync(context, message);
    }

    private static async Task WriteBodyAsync(HttpListenerContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        _stopping.Cancel();
        if (_listener.IsListening)
            _listener.Stop();
        _listener.Close();
        _stopping.Dispose();
    }
}

internal static class ControllerSecurity
{
    public static string Sign(string pairingKey, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pairingKey));
        return Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "\n" + body)));
    }

    public static bool FixedTimeEquals(string expected, string actual)
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
}
