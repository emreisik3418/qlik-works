using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace QlikSheetCleaner.Qlik;

public sealed class EngineException : Exception
{
    public int Code { get; }
    public EngineException(int code, string message) : base(message) => Code = code;
}

public sealed record EngineObjectRef(int Handle, string GenericId, string GenericType);

/// <summary>
/// Tek bir app için açılmış Engine (QIX, JSON-RPC 2.0 over WebSocket) oturumu.
/// </summary>
public sealed class EngineSession : IAsyncDisposable
{
    private const int GlobalHandle = -1;

    private readonly ClientWebSocket _socket;
    private readonly TimeSpan _requestTimeout;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveLoop;
    private int _nextId;

    public Guid AppId { get; }
    public QlikIdentity Identity { get; }
    public int AppHandle { get; private set; } = int.MinValue;

    internal EngineSession(ClientWebSocket socket, Guid appId, QlikIdentity identity, TimeSpan requestTimeout, ILogger log)
    {
        _socket = socket;
        AppId = appId;
        Identity = identity;
        _requestTimeout = requestTimeout;
        _log = log.ForContext("AppId", appId).ForContext("EngineUser", identity.ToString());
    }

    internal async Task InitializeAsync(bool noData, CancellationToken ct)
    {
        _receiveLoop = Task.Run(ReceiveLoopAsync);

        // Engine ilk mesaj olarak OnConnected bildirimi gönderir; bekleyelim (en fazla 30 sn).
        var connectedTask = _connected.Task;
        var finished = await Task.WhenAny(connectedTask, Task.Delay(TimeSpan.FromSeconds(30), ct));
        if (finished != connectedTask)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Engine OnConnected bildirimi gelmedi.");
        }
        await connectedTask; // hata varsa fırlat

        var result = await InvokeAsync(GlobalHandle, "OpenDoc", new JsonObject
        {
            ["qDocName"] = AppId.ToString(),
            ["qNoData"] = noData
        }, ct);

        AppHandle = result.GetProperty("qReturn").GetProperty("qHandle").GetInt32();
        _log.Debug("OpenDoc tamam (handle {Handle}, noData={NoData})", AppHandle, noData);
    }

    /// <summary>GetObject. Nesne yoksa null döner.</summary>
    public async Task<EngineObjectRef?> GetObjectAsync(string objectId, CancellationToken ct)
    {
        JsonElement result;
        try
        {
            result = await InvokeAsync(AppHandle, "GetObject", new JsonObject { ["qId"] = objectId }, ct);
        }
        catch (EngineException ex) when (ex.Code is 2 or -2) // LOCERR_GENERIC_NOT_FOUND
        {
            return null;
        }

        if (!result.TryGetProperty("qReturn", out var ret) ||
            ret.ValueKind != JsonValueKind.Object ||
            !ret.TryGetProperty("qHandle", out var handle) ||
            handle.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return new EngineObjectRef(
            handle.GetInt32(),
            ret.TryGetProperty("qGenericId", out var gid) ? gid.GetString() ?? objectId : objectId,
            ret.TryGetProperty("qGenericType", out var gt) ? gt.GetString() ?? "" : "");
    }

    /// <summary>GenericObject.GetFullPropertyTree → qPropEntry.</summary>
    public async Task<JsonElement> GetFullPropertyTreeAsync(int objectHandle, CancellationToken ct)
    {
        var result = await InvokeAsync(objectHandle, "GetFullPropertyTree", new JsonObject(), ct);
        return result.GetProperty("qPropEntry").Clone();
    }

    /// <summary>Doc.DestroyObject → qSuccess. Çocuk nesneleri de kaldırır. Retry YOK.</summary>
    public async Task<bool> DestroyObjectAsync(string objectId, CancellationToken ct)
    {
        var result = await InvokeAsync(AppHandle, "DestroyObject", new JsonObject { ["qId"] = objectId }, ct);
        return result.TryGetProperty("qSuccess", out var s) && s.ValueKind == JsonValueKind.True;
    }

    /// <summary>Doc.DoSave. Retry YOK.</summary>
    public async Task DoSaveAsync(CancellationToken ct)
    {
        await InvokeAsync(AppHandle, "DoSave", new JsonObject(), ct);
    }

    public async Task<JsonElement> InvokeAsync(int handle, string method, JsonObject parameters, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open)
            throw new WebSocketException($"Engine bağlantısı açık değil ({_socket.State}).");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["handle"] = handle,
            ["params"] = parameters
        };
        var bytes = Encoding.UTF8.GetBytes(request.ToJsonString());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_requestTimeout);
        using var registration = timeout.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            await _sendLock.WaitAsync(timeout.Token);
            try
            {
                _log.Debug("Engine → {Method} (handle {Handle}, id {Id})", method, handle, id);
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
            }
            finally
            {
                _sendLock.Release();
            }

            return await tcs.Task;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Engine {method} çağrısı {_requestTimeout.TotalSeconds:n0} sn içinde yanıt vermedi.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open && !_receiveCts.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _receiveCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        FailAll(new WebSocketException($"Engine bağlantıyı kapattı: {result.CloseStatus} {result.CloseStatusDescription}"));
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                HandleMessage(message.GetBuffer().AsSpan(0, (int)message.Length));
            }
        }
        catch (OperationCanceledException) when (_receiveCts.IsCancellationRequested)
        {
            // kapatılıyor
        }
        catch (Exception ex)
        {
            FailAll(ex);
        }
        finally
        {
            FailAll(new WebSocketException("Engine bağlantısı sonlandı."));
        }
    }

    private void HandleMessage(ReadOnlySpan<byte> payload)
    {
        using var doc = JsonDocument.Parse(payload.ToArray());
        var root = doc.RootElement;

        if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
        {
            var id = idElement.GetInt32();
            if (!_pending.TryGetValue(id, out var tcs)) return;

            if (root.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "Engine hatası";
                var param = error.TryGetProperty("parameter", out var p) ? p.GetString() : null;
                tcs.TrySetException(new EngineException(code, $"Engine hatası {code}: {msg}{(param is null ? "" : $" ({param})")}"));
            }
            else
            {
                tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
            }
            return;
        }

        // Bildirimler (id yok)
        if (root.TryGetProperty("method", out var methodElement))
        {
            var method = methodElement.GetString();
            switch (method)
            {
                case "OnConnected":
                    _log.Debug("Engine OnConnected: {Params}", root.TryGetProperty("params", out var pr) ? pr.GetRawText() : "");
                    _connected.TrySetResult(true);
                    break;
                case "OnAuthenticationInformation":
                    var mustAuth = root.TryGetProperty("params", out var ap) &&
                                   ap.TryGetProperty("mustAuthenticate", out var ma) && ma.ValueKind == JsonValueKind.True;
                    if (mustAuth)
                        _connected.TrySetException(new UnauthorizedAccessException("Engine kimlik doğrulama istiyor; sertifika/X-Qlik-User kabul edilmedi."));
                    break;
                case "OnLicenseAccessDenied":
                case "OnNoEngineAvailable":
                case "OnEngineWebsocketFailed":
                case "OnSessionTimedOut":
                case "OnSessionClosed":
                    _log.Warning("Engine bildirimi: {Method}", method);
                    _connected.TrySetException(new InvalidOperationException($"Engine bildirimi: {method}"));
                    break;
                default:
                    _log.Verbose("Engine bildirimi: {Method}", method);
                    break;
            }
        }
    }

    private void FailAll(Exception ex)
    {
        _connected.TrySetException(ex);
        foreach (var kv in _pending)
            kv.Value.TrySetException(ex);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Engine bağlantısı kapatılırken hata");
        }

        _receiveCts.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* yoksay */ }
        }

        _socket.Dispose();
        _receiveCts.Dispose();
        _sendLock.Dispose();
    }
}
