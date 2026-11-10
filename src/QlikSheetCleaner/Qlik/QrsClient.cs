using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Polly;
using QlikSheetCleaner.Configuration;
using QlikSheetCleaner.Models;
using Serilog;

namespace QlikSheetCleaner.Qlik;

public sealed class QrsException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public QrsException(HttpStatusCode statusCode, string message) : base(message) => StatusCode = statusCode;
}

/// <summary>
/// QRS API istemcisi (port 4242). Her çağrıda X-Qlik-Xrfkey header'ı + xrfkey query parametresi kullanılır.
/// GET çağrıları retry hattından geçer; DELETE/PUT (unpublish) ve export başlatma (POST) geçmez.
/// </summary>
public sealed class QrsClient : IDisposable
{
    private const string XrfChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly QlikSettings _settings;
    private readonly ResiliencePipeline _readPipeline;
    private readonly ILogger _log = Log.ForContext<QrsClient>();
    private readonly string _userHeader;

    public QrsClient(QlikSettings settings, CertificateProvider certificates)
    {
        _settings = settings;
        _userHeader = new QlikIdentity(settings.UserDirectory, settings.UserId).ToHeaderValue();

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            SslOptions =
            {
                ClientCertificates = new System.Security.Cryptography.X509Certificates.X509CertificateCollection
                {
                    certificates.ClientCertificate
                },
                RemoteCertificateValidationCallback = certificates.ValidateServerCertificate
            }
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{settings.Host}:{settings.QrsPort}"),
            Timeout = Timeout.InfiniteTimeSpan // zaman aşımı istek bazında CancellationToken ile yönetilir
        };

        _readPipeline = ReadRetryPolicy.Create(settings.ReadRetryCount, settings.ReadRetryBaseDelaySeconds, _log);
    }

    // ---------------------------------------------------------------- okuma çağrıları (retry'lı)

    /// <summary>GET /qrs/app/{id}. 404 ise null.</summary>
    public Task<QrsApp?> GetAppAsync(Guid appId, CancellationToken ct) =>
        GetJsonAsync<QrsApp>($"/qrs/app/{appId}", query: null, allowNotFound: true, ct);

    /// <summary>GET /qrs/app/object/full?filter=app.id eq {appId} and objectType eq 'sheet'</summary>
    public async Task<IReadOnlyList<QrsAppObject>> GetSheetsAsync(Guid appId, CancellationToken ct) =>
        await GetJsonAsync<List<QrsAppObject>>("/qrs/app/object/full",
            $"filter={Uri.EscapeDataString($"app.id eq {appId} and objectType eq 'sheet'")}",
            allowNotFound: false, ct) ?? [];

    /// <summary>Tür fark etmeksizin app içinde engineObjectId ile nesne ara (teşhis amaçlı).</summary>
    public async Task<IReadOnlyList<QrsAppObject>> FindObjectsByEngineIdAsync(Guid appId, string engineObjectId, CancellationToken ct)
    {
        var safeId = engineObjectId.Replace("'", "''");
        return await GetJsonAsync<List<QrsAppObject>>("/qrs/app/object/full",
            $"filter={Uri.EscapeDataString($"app.id eq {appId} and engineObjectId eq '{safeId}'")}",
            allowNotFound: false, ct) ?? [];
    }

    /// <summary>GET /qrs/app/object/{id}/... tam bilgi. 404 ise null.</summary>
    public async Task<QrsAppObject?> GetAppObjectAsync(Guid objectId, CancellationToken ct)
    {
        var list = await GetJsonAsync<List<QrsAppObject>>("/qrs/app/object/full",
            $"filter={Uri.EscapeDataString($"id eq {objectId}")}", allowNotFound: true, ct);
        return list?.FirstOrDefault();
    }

    // ---------------------------------------------------------------- yazma çağrıları (retry YOK)

    /// <summary>PUT /qrs/app/object/{id}/unpublish — retry yok.</summary>
    public async Task UnpublishAppObjectAsync(Guid objectId, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Put, $"/qrs/app/object/{objectId}/unpublish", null);
        request.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
        using var response = await SendOnceAsync(request, _settings.RequestTimeoutSeconds, ct);
        await EnsureSuccessAsync(response, $"PUT unpublish {objectId}", ct);
        _log.Information("Unpublish edildi: app.object {ObjectId}", objectId);
    }

    /// <summary>DELETE /qrs/app/object/{id} — retry yok.</summary>
    public async Task DeleteAppObjectAsync(Guid objectId, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"/qrs/app/object/{objectId}", null);
        using var response = await SendOnceAsync(request, _settings.RequestTimeoutSeconds, ct);
        await EnsureSuccessAsync(response, $"DELETE app.object {objectId}", ct);
        _log.Information("QRS ile silindi: app.object {ObjectId}", objectId);
    }

    // ---------------------------------------------------------------- export (.qvf yedek)

    /// <summary>
    /// POST /qrs/app/{id}/export/{token} ile export bileti alır ve .qvf'i indirir.
    /// Export başlatma tek sefer; indirme okuma olduğu için retry'lı.
    /// </summary>
    public async Task<long> ExportAppAsync(Guid appId, string targetFile, bool skipData, CancellationToken ct)
    {
        var token = Guid.NewGuid();
        QrsExportTicket ticket;

        using (var request = CreateRequest(HttpMethod.Post, $"/qrs/app/{appId}/export/{token}", $"skipData={(skipData ? "true" : "false")}"))
        {
            request.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
            using var response = await SendOnceAsync(request, _settings.ExportTimeoutSeconds, ct);
            await EnsureSuccessAsync(response, $"POST export {appId}", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            ticket = JsonSerializer.Deserialize<QrsExportTicket>(body, JsonOptions)
                     ?? throw new InvalidOperationException("Export yanıtı boş.");
        }

        if (string.IsNullOrWhiteSpace(ticket.DownloadPath))
            throw new InvalidOperationException("Export yanıtında downloadPath yok.");

        // downloadPath örn: /tempcontent/{guid}/App.qvf?serverNodeId={guid}
        var (path, query) = SplitPathAndQuery(ticket.DownloadPath);
        var tempFile = targetFile + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetFile))!);

        try
        {
            await _readPipeline.ExecuteAsync(async token2 =>
            {
                using var request = CreateRequest(HttpMethod.Get, path, query);
                using var response = await SendOnceAsync(request, _settings.ExportTimeoutSeconds, token2);
                await EnsureSuccessAsync(response, $"GET {path}", token2);
                await using var source = await response.Content.ReadAsStreamAsync(token2);
                await using var target = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await source.CopyToAsync(target, token2);
            }, ct);

            var length = new FileInfo(tempFile).Length;
            if (length == 0) throw new InvalidOperationException("İndirilen .qvf dosyası boş.");
            File.Move(tempFile, targetFile, overwrite: true);
            return length;
        }
        finally
        {
            if (File.Exists(tempFile)) TryDelete(tempFile);
            await TryDeleteExportTicketAsync(appId, token);
        }
    }

    private async Task TryDeleteExportTicketAsync(Guid appId, Guid token)
    {
        // Geçici export dosyasını sunucudan temizle (best effort, retry yok).
        try
        {
            using var request = CreateRequest(HttpMethod.Delete, $"/qrs/app/{appId}/export/{token}", null);
            using var response = await SendOnceAsync(request, 60, CancellationToken.None);
            if (!response.IsSuccessStatusCode)
                _log.Debug("Export bileti silinemedi ({Status}) app {AppId}", (int)response.StatusCode, appId);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Export bileti temizlenemedi, app {AppId}", appId);
        }
    }

    // ---------------------------------------------------------------- yardımcılar

    private async Task<T?> GetJsonAsync<T>(string path, string? query, bool allowNotFound, CancellationToken ct) where T : class
    {
        return await _readPipeline.ExecuteAsync(async token =>
        {
            using var request = CreateRequest(HttpMethod.Get, path, query);
            using var response = await SendOnceAsync(request, _settings.RequestTimeoutSeconds, token);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
            await EnsureSuccessAsync(response, $"GET {path}", token);
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, token);
        }, ct);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string? query)
    {
        var xrfKey = NewXrfKey();
        var q = string.IsNullOrEmpty(query) ? $"xrfkey={xrfKey}" : $"{query}&xrfkey={xrfKey}";
        var request = new HttpRequestMessage(method, $"{path}?{q}");
        request.Headers.Add("X-Qlik-Xrfkey", xrfKey);
        request.Headers.Add("X-Qlik-User", _userHeader);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, int timeoutSeconds, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            _log.Debug("QRS {Method} {Uri}", request.Method, request.RequestUri);
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"QRS isteği {timeoutSeconds} sn içinde yanıt vermedi: {request.Method} {request.RequestUri?.AbsolutePath}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string body;
        try { body = await response.Content.ReadAsStringAsync(ct); }
        catch { body = ""; }
        if (body.Length > 500) body = body[..500];
        throw new QrsException(response.StatusCode, $"{operation} başarısız: HTTP {(int)response.StatusCode} {response.ReasonPhrase} {body}".Trim());
    }

    private static (string Path, string? Query) SplitPathAndQuery(string pathAndQuery)
    {
        var idx = pathAndQuery.IndexOf('?');
        return idx < 0 ? (pathAndQuery, null) : (pathAndQuery[..idx], pathAndQuery[(idx + 1)..]);
    }

    private static string NewXrfKey() => RandomNumberGenerator.GetString(XrfChars, 16);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* yoksay */ }
    }

    public void Dispose() => _http.Dispose();
}
