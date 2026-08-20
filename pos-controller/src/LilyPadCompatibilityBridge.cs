using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MulletHopPosController;

/// <summary>
/// Adds a narrowly scoped compatibility handler to the LilyPad login page.
/// Firefox exposes WebDriver BiDi only on a randomly selected loopback port, so
/// the bridge is not reachable from another computer on the network.
/// </summary>
internal sealed class LilyPadCompatibilityBridge : IDisposable
{
    private const int MaximumMessageBytes = 1_048_576;
    private const string CompatibilityFunction = """
        () => {
          if (location.hostname !== "mullet.lilypadpos.app" ||
              location.pathname !== "/public/Login.php") {
            return;
          }

          const install = () => {
            const root = document.documentElement;
            if (!root || root.dataset.mulletHopLocationCompatibility === "1") {
              return;
            }
            root.dataset.mulletHopLocationCompatibility = "1";

            let requestTimer = 0;
            let requestSequence = 0;
            let lastRequestedUsername = "";
            const requestLocations = () => {
              const username = document.getElementById("Username");
              if (!(username instanceof HTMLInputElement) || !username.value.trim()) {
                return;
              }

              const usernameValue = username.value.trim();
              const station = document.getElementById("StationName");
              if (usernameValue === lastRequestedUsername &&
                  station instanceof HTMLSelectElement &&
                  station.options.length > 1) {
                return;
              }

              const ajaxDisplay = document.getElementById("ajaxDiv");
              if (!(ajaxDisplay instanceof HTMLElement)) {
                return;
              }

              lastRequestedUsername = usernameValue;
              const sequence = ++requestSequence;
              const endpoint = new URL("CheckUsernameLoginScript.php", location.href);
              endpoint.searchParams.set("UN", usernameValue);
              fetch(endpoint, {
                cache: "no-store",
                credentials: "same-origin"
              })
                .then(response => {
                  if (!response.ok) {
                    throw new Error(`LilyPad returned HTTP ${response.status}.`);
                  }
                  return response.text();
                })
                .then(html => {
                  if (sequence === requestSequence &&
                      username.value.trim() === usernameValue) {
                    ajaxDisplay.innerHTML = html;
                  }
                })
                .catch(() => {
                  if (sequence === requestSequence &&
                      typeof window.ajaxFunction === "function") {
                    window.ajaxFunction();
                  }
                });
            };

            const scheduleLocations = () => {
              window.clearTimeout(requestTimer);
              requestTimer = window.setTimeout(requestLocations, 350);
            };
            const handleUsernameInput = event => {
              const target = event.target;
              if (target instanceof Element && target.id === "Username") {
                scheduleLocations();
              }
            };
            const handlePasswordFocus = event => {
              const target = event.target;
              if (target instanceof Element && target.id === "Password") {
                window.clearTimeout(requestTimer);
                requestLocations();
              }
            };

            document.addEventListener("input", handleUsernameInput, true);
            document.addEventListener("change", handleUsernameInput, true);
            document.addEventListener("pointerdown", handlePasswordFocus, true);
            document.addEventListener("focusin", handlePasswordFocus, true);
          };

          if (document.readyState === "loading") {
            document.addEventListener("DOMContentLoaded", install, { once: true });
          } else {
            install();
          }
        }
        """;

    private readonly int _port;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _socketGate = new();
    private ClientWebSocket? _socket;
    private Task? _worker;
    private int _nextCommandId;
    private bool _disposed;

    public LilyPadCompatibilityBridge(int port)
    {
        _port = port;
    }

    public static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cancellationToken = _stopping.Token;
        _worker ??= Task.Run(() => RunAsync(cancellationToken));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Exception? lastError = null;
        while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            using var socket = new ClientWebSocket();
            lock (_socketGate)
                _socket = socket;
            try
            {
                await socket.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{_port}/session"),
                    cancellationToken);
                await ConfigureAsync(socket, cancellationToken);
                PosLog.Write(
                    "LilyPad login compatibility is active for the username-to-location transition.");

                while (!cancellationToken.IsCancellationRequested &&
                       socket.State == WebSocketState.Open)
                {
                    if (await ReceiveTextAsync(socket, cancellationToken) is null)
                        break;
                }
                if (!cancellationToken.IsCancellationRequested)
                    lastError = new IOException("Firefox closed the local compatibility connection.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or
                                       IOException or SocketException or InvalidDataException or
                                       JsonException)
            {
                lastError = ex;
            }
            finally
            {
                lock (_socketGate)
                {
                    if (ReferenceEquals(_socket, socket))
                        _socket = null;
                }
            }

            try
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            PosLog.Write(
                "LilyPad login compatibility could not connect to Firefox: " +
                (lastError?.Message ?? "the local Firefox debugging endpoint did not answer."));
        }
    }

    private async Task ConfigureAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        await SendCommandAsync(
            socket,
            "session.new",
            new { capabilities = new { } },
            cancellationToken);

        try
        {
            await SendCommandAsync(
                socket,
                "script.addPreloadScript",
                new { functionDeclaration = CompatibilityFunction },
                cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            // Current-page injection below still fixes the initial login on an
            // older Firefox that does not yet implement preload scripts.
            PosLog.Write("Firefox preload compatibility is unavailable: " + ex.Message);
        }

        var tree = await SendCommandAsync(
            socket,
            "browsingContext.getTree",
            new { },
            cancellationToken);
        if (!tree.TryGetProperty("contexts", out var contexts) ||
            contexts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var context in contexts.EnumerateArray())
        {
            if (!context.TryGetProperty("context", out var contextId) ||
                contextId.ValueKind != JsonValueKind.String ||
                !context.TryGetProperty("url", out var url) ||
                url.ValueKind != JsonValueKind.String ||
                !IsLilyPadLoginUrl(url.GetString()))
            {
                continue;
            }

            await SendCommandAsync(
                socket,
                "script.callFunction",
                new
                {
                    functionDeclaration = CompatibilityFunction,
                    awaitPromise = false,
                    target = new { context = contextId.GetString() }
                },
                cancellationToken);
        }
    }

    private async Task<JsonElement> SendCommandAsync(
        ClientWebSocket socket,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var commandId = Interlocked.Increment(ref _nextCommandId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = commandId,
            method,
            @params = parameters
        });
        await socket.SendAsync(
            new ArraySegment<byte>(payload),
            WebSocketMessageType.Text,
            true,
            cancellationToken);

        while (true)
        {
            var message = await ReceiveTextAsync(socket, cancellationToken);
            if (message is null)
                throw new IOException("Firefox closed the local compatibility connection.");

            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) ||
                responseId.ValueKind != JsonValueKind.Number ||
                responseId.GetInt32() != commandId)
            {
                continue;
            }

            if (root.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                var error = root.TryGetProperty("error", out var errorValue)
                    ? errorValue.GetString()
                    : "unknown error";
                var detail = root.TryGetProperty("message", out var detailValue)
                    ? detailValue.GetString()
                    : string.Empty;
                throw new InvalidDataException(
                    string.IsNullOrWhiteSpace(detail) ? error : $"{error}: {detail}");
            }

            return root.TryGetProperty("result", out var result)
                ? result.Clone()
                : default;
        }
    }

    private static async Task<string?> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Firefox returned a non-text compatibility message.");
            if (message.Length + result.Count > MaximumMessageBytes)
                throw new InvalidDataException("Firefox returned an oversized compatibility message.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
        }
    }

    private static bool IsLilyPadLoginUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, "mullet.lilypadpos.app", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.AbsolutePath, "/public/Login.php", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stopping.Cancel();
        lock (_socketGate)
        {
            try { _socket?.Abort(); }
            catch
            {
                // Firefox is already closing.
            }
            _socket = null;
        }
        _stopping.Dispose();
    }
}
