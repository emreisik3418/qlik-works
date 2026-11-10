using QlikSheetCleaner.Configuration;
using QlikSheetCleaner.Models;
using QlikSheetCleaner.Qlik;
using Serilog;

namespace QlikSheetCleaner.Processing.Deletion;

/// <summary>
/// Senaryo A (yayınlanmamış app): .qvf yedeği → OpenDoc (bir kez) → her sheet için DestroyObject → DoSave (bir kez).
/// Master item'lara dokunulmaz (yalnızca qGenericType = sheet olan nesneler yok edilir).
/// </summary>
public sealed class EngineSheetDeletionStrategy : ISheetDeletionStrategy
{
    private readonly EngineClient _engine;
    private readonly BackupService _backup;
    private readonly QlikSettings _settings;
    private readonly ILogger _log = Log.ForContext<EngineSheetDeletionStrategy>();

    public EngineSheetDeletionStrategy(EngineClient engine, BackupService backup, QlikSettings settings)
    {
        _engine = engine;
        _backup = backup;
        _settings = settings;
    }

    public string Name => "Engine (Senaryo A)";

    public bool CanHandle(DeleteAction action) => action == DeleteAction.EngineDestroy;

    public async Task ExecuteAsync(QrsApp app, IReadOnlyList<SheetRow> rows, CancellationToken ct)
    {
        // Savunma: bu strateji yalnızca yayınlanmamış app'lerde çalışır
        if (app.Published)
        {
            foreach (var r in rows) r.SetResult(RowStatus.Error, "Engine silme stratejisi yayınlanmış app için çağrıldı; işlem yapılmadı");
            return;
        }

        // 1) Yedek (.qvf). Alınamazsa bu app'te hiçbir şey silinmez.
        string backupPath;
        try
        {
            backupPath = await _backup.BackupAppAsync(app, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "App {AppId} yedeği alınamadı; bu app'te silme yapılmayacak", app.Id);
            foreach (var r in rows) r.SetResult(RowStatus.Error, $"Yedek alınamadı (.qvf export): {ex.Message}");
            return;
        }

        foreach (var r in rows) r.BackupPath = backupPath;

        // 2) Engine oturumu (veriyle; DoSave yapılacağı için qNoData ASLA kullanılmaz)
        var identity = _settings.ScenarioAEngineIdentity == EngineIdentityMode.AppOwner && app.Owner is not null
            ? new QlikIdentity(app.Owner.UserDirectory, app.Owner.UserId)
            : _engine.ServiceAccount;

        EngineSession session;
        try
        {
            session = await _engine.OpenAppAsync(app.Id, identity, noData: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "App {AppId} Engine'de açılamadı ({User}); app atlanıyor", app.Id, identity);
            foreach (var r in rows) r.SetResult(RowStatus.Error, $"App'e Engine ile bağlanılamadı ({identity}): {ex.Message}");
            return;
        }

        await using (session)
        {
            var destroyed = new List<SheetRow>();

            foreach (var row in rows)
            {
                if (ct.IsCancellationRequested) break; // kalanlar işlenmedi olarak işaretlenecek

                var id = row.EngineObjectId ?? row.SheetId;
                try
                {
                    var obj = await session.GetObjectAsync(id, ct);
                    if (obj is null)
                    {
                        row.SetResult(RowStatus.Error, $"Sheet Engine'de bulunamadı ({identity} oturumunda)");
                        continue;
                    }
                    if (!string.Equals(obj.GenericType, "sheet", StringComparison.OrdinalIgnoreCase))
                    {
                        row.SetResult(RowStatus.Error, $"Engine nesnesi sheet değil (qGenericType={obj.GenericType}); dokunulmadı");
                        continue;
                    }

                    var ok = await session.DestroyObjectAsync(id, ct);
                    if (!ok)
                    {
                        row.SetResult(RowStatus.Error, "DestroyObject qSuccess=false döndü");
                        continue;
                    }

                    destroyed.Add(row);
                    _log.Information("DestroyObject: {Title} ({SheetId}) app {AppId}", row.SheetTitle, id, app.Id);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "DestroyObject başarısız: {Row}", row);
                    row.SetResult(RowStatus.Error, $"DestroyObject hatası: {ex.Message}");
                }
            }

            if (destroyed.Count == 0) return;

            // 3) DoSave (bir kez). İptal edilmiş olsa bile, bellekte yok edilmiş nesneler tutarlı kalsın diye kaydet.
            try
            {
                await session.DoSaveAsync(CancellationToken.None);
                foreach (var r in destroyed) r.SetResult(RowStatus.Deleted, "Engine DestroyObject + DoSave");
                _log.Information("DoSave tamam: app {AppId}, {Count} sheet silindi", app.Id, destroyed.Count);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "DoSave başarısız: app {AppId}", app.Id);
                foreach (var r in destroyed)
                    r.SetResult(RowStatus.Error, $"DestroyObject yapıldı ama DoSave başarısız; silme kalıcı olmayabilir: {ex.Message}");
            }
        }
    }
}
