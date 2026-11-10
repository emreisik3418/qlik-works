using System.Net.WebSockets;
using Polly;
using QlikSheetCleaner.Configuration;
using Serilog;

namespace QlikSheetCleaner.Qlik;

/// <summary>
/// Engine API'ye (port 4747) sertifika + X-Qlik-User header'ı ile bağlanır.
/// X-Qlik-User ile istenen kullanıcı (ör. sheet sahibi) adına oturum açılabilir.
/// </summary>
public sealed class EngineClient
{
    private readonly QlikSettings _settings;
    private readonly CertificateProvider _certificates;
    private readonly ResiliencePipeline _connectPipeline;
    private readonly ILogger _log = Log.ForContext<EngineClient>();

    public QlikIdentity ServiceAccount { get; }

    public EngineClient(QlikSettings settings, CertificateProvider certificates)
    {
        _settings = settings;
        _certificates = certificates;
        ServiceAccount = new QlikIdentity(settings.UserDirectory, settings.UserId);
        // Bağlantı açma + OpenDoc veri değiştirmez → okuma sayılır, retry'lı.
        _connectPipeline = ReadRetryPolicy.Create(settings.ReadRetryCount, settings.ReadRetryBaseDelaySeconds, _log);
    }

    /// <param name="noData">
    /// true: app veri yüklenmeden açılır (yalnızca okuma/yedek için!). DoSave yapılacak oturumlarda false olmalı.
    /// </param>
    public Task<EngineSession> OpenAppAsync(Guid appId, QlikIdentity identity, bool noData, CancellationToken ct) =>
        _connectPipeline.ExecuteAsync(async token => await OpenOnceAsync(appId, identity, noData, token), ct).AsTask();

    private async Task<EngineSession> OpenOnceAsync(Guid appId, QlikIdentity identity, bool noData, CancellationToken ct)
    {
        var path = _settings.UseIsolatedEngineSession
            ? $"/app/{appId}/identity/{Guid.NewGuid():N}"
            : $"/app/{appId}";
        var uri = new Uri($"wss://{_settings.Host}:{_settings.EnginePort}{path}");

        var socket = new ClientWebSocket();
        socket.Options.ClientCertificates.Add(_certificates.ClientCertificate);
        socket.Options.RemoteCertificateValidationCallback = _certificates.ValidateServerCertificate;
        socket.Options.SetRequestHeader("X-Qlik-User", identity.ToHeaderValue());
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        EngineSession? session = null;
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds));
            _log.Debug("Engine bağlantısı: {Uri} as {User}", uri, identity);
            try
            {
                await socket.ConnectAsync(uri, connectTimeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Engine bağlantısı {_settings.RequestTimeoutSeconds} sn içinde kurulamadı: {uri}");
            }

            session = new EngineSession(socket, appId, identity,
                TimeSpan.FromSeconds(_settings.EngineRequestTimeoutSeconds), _log);
            await session.InitializeAsync(noData, ct);
            return session;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync();
            else socket.Dispose();
            throw;
        }
    }
}
