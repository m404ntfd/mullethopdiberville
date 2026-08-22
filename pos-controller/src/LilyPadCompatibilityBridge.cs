using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MulletHopPosController;

internal sealed record LilyPadPageHealth(
    string Url,
    string Title,
    string ReadyState,
    int ViewportWidth,
    int ViewportHeight,
    int BodyTextLength,
    bool HasUsername,
    bool HasPassword,
    double PageTimeOrigin = 0)
{
    public bool IsLoginPage =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.AbsolutePath, "/public/Login.php", StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct LilyPadPdfDownloadResult(
    bool Success,
    byte[]? PdfBytes,
    string Message)
{
    public static LilyPadPdfDownloadResult Failed(string message) =>
        new(false, null, message);
}

/// <summary>
/// Adds narrowly scoped compatibility handlers for the LilyPad login page and
/// direct wristband printing.
/// Firefox exposes WebDriver BiDi only on a randomly selected loopback port, so
/// the bridge is not reachable from another computer on the network.
/// </summary>
internal sealed class LilyPadCompatibilityBridge : IDisposable
{
    private const int MaximumMessageBytes = 32 * 1024 * 1024;
    private const int MaximumPdfBytes = 20 * 1024 * 1024;
    private const string CompatibilityFunction = """
        () => {
          if (location.hostname !== "mullet.lilypadpos.app") {
            return;
          }

          if (/wristband/i.test(location.pathname) &&
              /(pdf|php)$/i.test(location.pathname)) {
            const suppressBrowserPrintDialog = () => {
              document.documentElement?.setAttribute(
                "data-mullet-hop-direct-wristband-print",
                "1");
            };
            try {
              Object.defineProperty(window, "print", {
                configurable: true,
                writable: false,
                value: suppressBrowserPrintDialog
              });
            } catch {
              window.print = suppressBrowserPrintDialog;
            }
            return;
          }

          if (location.pathname !== "/public/Login.php") {
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
    private const string PageHealthFunction = """
        () => JSON.stringify({
          url: location.href,
          title: document.title || "",
          readyState: document.readyState || "",
          viewportWidth: Math.max(0, Math.round(window.innerWidth || 0)),
          viewportHeight: Math.max(0, Math.round(window.innerHeight || 0)),
          bodyTextLength: document.body && document.body.innerText
            ? document.body.innerText.trim().length
            : 0,
          hasUsername: document.getElementById("Username") instanceof HTMLInputElement,
          hasPassword: document.getElementById("Password") instanceof HTMLInputElement,
          pageTimeOrigin: Number.isFinite(performance.timeOrigin) ? performance.timeOrigin : 0
        })
        """;
    private const string DownloadPdfFunction = """
        async (expectedUrl) => {
          const failure = message => JSON.stringify({ success: false, message });
          try {
            const requested = new URL(expectedUrl, location.href);
            if (requested.hostname !== "mullet.lilypadpos.app" ||
                !/wristband/i.test(requested.pathname) ||
                !/(pdf|php)$/i.test(requested.pathname)) {
              return failure("The current page is not a LilyPad wristband PDF.");
            }

            let bytes = null;
            let contentType = "application/pdf";
            const loadedDocument = globalThis.PDFViewerApplication?.pdfDocument;
            if (loadedDocument && typeof loadedDocument.getData === "function") {
              const loadedBytes = await loadedDocument.getData();
              bytes = loadedBytes instanceof Uint8Array
                ? loadedBytes
                : new Uint8Array(loadedBytes);
            }
            if (!bytes) {
              const response = await fetch(requested.href, {
                credentials: "include",
                cache: "force-cache",
                redirect: "follow"
              });
              if (!response.ok) {
                return failure(`LilyPad returned HTTP ${response.status} for the wristband PDF.`);
              }
              bytes = new Uint8Array(await response.arrayBuffer());
              contentType = response.headers.get("content-type") || "";
            }
            if (bytes.byteLength < 5 ||
                bytes[0] !== 0x25 || bytes[1] !== 0x50 || bytes[2] !== 0x44 ||
                bytes[3] !== 0x46 || bytes[4] !== 0x2d) {
              return failure("LilyPad did not return a valid wristband PDF.");
            }
            if (bytes.byteLength > 20971520) {
              return failure("The LilyPad wristband PDF is larger than the 20 MB safety limit.");
            }

            let binary = "";
            const chunkSize = 0x8000;
            for (let offset = 0; offset < bytes.length; offset += chunkSize) {
              binary += String.fromCharCode(...bytes.subarray(offset, offset + chunkSize));
            }
            return JSON.stringify({
              success: true,
              contentType,
              base64: btoa(binary)
            });
          } catch (error) {
            return failure(error instanceof Error ? error.message : String(error));
          }
        }
        """;
    private static readonly JsonSerializerOptions HealthJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly int _port;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _socketGate = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private ClientWebSocket? _socket;
    private Task? _worker;
    private int _nextCommandId;
    private bool _disposed;

    public event Action<LilyPadPageHealth>? PageHealthObserved;

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

    public async Task<LilyPadPdfDownloadResult> DownloadWristbandPdfAsync(
        string pageUrl,
        CancellationToken cancellationToken = default)
    {
        if (!IsLilyPadWristbandUrl(pageUrl))
        {
            return LilyPadPdfDownloadResult.Failed(
                "The current page is not a LilyPad wristband PDF.");
        }

        ClientWebSocket? socket;
        lock (_socketGate)
            socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return LilyPadPdfDownloadResult.Failed(
                "Firefox's local LilyPad connection is not ready. Wait a moment and try the wristband print again.");
        }

        var callerCancellationToken = cancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        cancellationToken = timeout.Token;
        try
        {
            var tree = await SendCommandAsync(
                socket,
                "browsingContext.getTree",
                new { },
                cancellationToken);
            if (!tree.TryGetProperty("contexts", out var contexts) ||
                contexts.ValueKind != JsonValueKind.Array)
            {
                return LilyPadPdfDownloadResult.Failed(
                    "Firefox did not expose the current LilyPad wristband page.");
            }

            foreach (var context in EnumerateContexts(contexts))
            {
                if (!context.TryGetProperty("context", out var contextId) ||
                    contextId.ValueKind != JsonValueKind.String ||
                    !context.TryGetProperty("url", out var contextUrl) ||
                    contextUrl.ValueKind != JsonValueKind.String ||
                    !IsLilyPadWristbandUrl(contextUrl.GetString()))
                {
                    continue;
                }

                var result = await SendCommandAsync(
                    socket,
                    "script.callFunction",
                    new
                    {
                        functionDeclaration = DownloadPdfFunction,
                        awaitPromise = true,
                        target = new { context = contextId.GetString() },
                        arguments = new[] { new { type = "string", value = pageUrl } }
                    },
                    cancellationToken);
                var payload = ReadRemoteString(result);
                if (string.IsNullOrWhiteSpace(payload))
                {
                    return LilyPadPdfDownloadResult.Failed(
                        "Firefox did not return the wristband PDF data.");
                }

                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
                {
                    var message = root.TryGetProperty("message", out var messageValue)
                        ? messageValue.GetString()
                        : null;
                    return LilyPadPdfDownloadResult.Failed(
                        string.IsNullOrWhiteSpace(message)
                            ? "Firefox could not retrieve the authenticated LilyPad wristband PDF."
                            : message);
                }
                if (!root.TryGetProperty("base64", out var base64) ||
                    base64.ValueKind != JsonValueKind.String)
                {
                    return LilyPadPdfDownloadResult.Failed(
                        "Firefox returned an incomplete wristband PDF.");
                }

                var bytes = Convert.FromBase64String(base64.GetString() ?? string.Empty);
                if (bytes.Length > MaximumPdfBytes || !HasPdfSignature(bytes))
                {
                    return LilyPadPdfDownloadResult.Failed(
                        "Firefox returned an invalid or oversized wristband PDF.");
                }
                return new LilyPadPdfDownloadResult(
                    true,
                    bytes,
                    "The authenticated LilyPad wristband PDF is ready to print.");
            }
            return LilyPadPdfDownloadResult.Failed(
                "The LilyPad wristband page is no longer open in Firefox.");
        }
        catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
        {
            return LilyPadPdfDownloadResult.Failed(
                "Firefox did not provide the wristband PDF within 20 seconds. No print job was sent.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLog.Write("Authenticated LilyPad wristband PDF retrieval failed: " + ex);
            return LilyPadPdfDownloadResult.Failed(
                "Firefox could not retrieve the current wristband PDF: " + ex.Message);
        }
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
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    var health = await ProbePageHealthAsync(socket, cancellationToken);
                    if (health is not null)
                        PageHealthObserved?.Invoke(health);
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

        foreach (var context in EnumerateContexts(contexts))
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

    private async Task<LilyPadPageHealth?> ProbePageHealthAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        JsonElement tree;
        try
        {
            tree = await SendCommandAsync(
                socket,
                "browsingContext.getTree",
                new { },
                cancellationToken);
        }
        catch (InvalidDataException)
        {
            // Navigation can invalidate a context between health checks.
            return null;
        }

        if (!tree.TryGetProperty("contexts", out var contexts) ||
            contexts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var context in EnumerateContexts(contexts))
        {
            if (!context.TryGetProperty("context", out var contextId) ||
                contextId.ValueKind != JsonValueKind.String ||
                !context.TryGetProperty("url", out var url) ||
                url.ValueKind != JsonValueKind.String ||
                !IsLilyPadUrl(url.GetString()))
            {
                continue;
            }

            try
            {
                var result = await SendCommandAsync(
                    socket,
                    "script.callFunction",
                    new
                    {
                        functionDeclaration = PageHealthFunction,
                        awaitPromise = false,
                        target = new { context = contextId.GetString() }
                    },
                    cancellationToken);
                if (!result.TryGetProperty("type", out var resultType) ||
                    !string.Equals(
                        resultType.GetString(),
                        "success",
                        StringComparison.OrdinalIgnoreCase) ||
                    !result.TryGetProperty("result", out var remoteValue) ||
                    !remoteValue.TryGetProperty("type", out var remoteType) ||
                    !string.Equals(
                        remoteType.GetString(),
                        "string",
                        StringComparison.OrdinalIgnoreCase) ||
                    !remoteValue.TryGetProperty("value", out var value) ||
                    value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                return JsonSerializer.Deserialize<LilyPadPageHealth>(
                    value.GetString() ?? string.Empty,
                    HealthJsonOptions);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                PosLog.Write("LilyPad page-health probe skipped: " + ex.Message);
                return null;
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateContexts(JsonElement contexts)
    {
        foreach (var context in contexts.EnumerateArray())
        {
            yield return context;
            if (!context.TryGetProperty("children", out var children) ||
                children.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var child in EnumerateContexts(children))
                yield return child;
        }
    }

    private async Task<JsonElement> SendCommandAsync(
        ClientWebSocket socket,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
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
        finally
        {
            _commandGate.Release();
        }
    }

    private static string? ReadRemoteString(JsonElement result)
    {
        if (!result.TryGetProperty("type", out var resultType) ||
            !string.Equals(resultType.GetString(), "success", StringComparison.OrdinalIgnoreCase) ||
            !result.TryGetProperty("result", out var remoteValue) ||
            !remoteValue.TryGetProperty("type", out var remoteType) ||
            !string.Equals(remoteType.GetString(), "string", StringComparison.OrdinalIgnoreCase) ||
            !remoteValue.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return value.GetString();
    }

    private static bool HasPdfSignature(byte[] bytes) =>
        bytes.Length >= 5 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' &&
        bytes[2] == (byte)'D' && bytes[3] == (byte)'F' && bytes[4] == (byte)'-';

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

    private static bool IsLilyPadUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, "mullet.lilypadpos.app", StringComparison.OrdinalIgnoreCase);

    private static bool IsLilyPadWristbandUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, "mullet.lilypadpos.app", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Contains("Wristband", StringComparison.OrdinalIgnoreCase) &&
        (uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
         uri.AbsolutePath.EndsWith(".php", StringComparison.OrdinalIgnoreCase));

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
