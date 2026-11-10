using QlikSheetCleaner.Models;
using QlikSheetCleaner.Qlik;
using Serilog;

namespace QlikSheetCleaner.Processing.Deletion;

/// <summary>
/// Senaryo B (kişisel) ve C (community, izin verildiyse) — yayınlanmış app'ler:
/// owner adına Engine'den JSON yedek → (C: unpublish) → approved=false tekrar kontrolü → DELETE /qrs/app/object/{id}.
/// Bu app'lerde DoSave ÇAĞRILMAZ ve app Engine üzerinden DEĞİŞTİRİLMEZ (Engine oturumu yalnızca okuma için açılır).
/// </summary>
public sealed class QrsSheetDeletionStrategy : ISheetDeletionStrategy
{
    private readonly QrsClient _qrs;
    private readonly EngineClient _engine;
    private readonly BackupService _backup;
    private readonly bool _backupWithoutData;
    private readonly ILogger _log = Log.ForContext<QrsSheetDeletionStrategy>();

    public QrsSheetDeletionStrategy(QrsClient qrs, EngineClient engine, BackupService backup, bool backupWithoutData)
    {
        _qrs = qrs;
        _engine = engine;
        _backup = backup;
        _backupWithoutData = backupWithoutData;
    }

    public string Name => "QRS (Senaryo B/C)";

    public bool CanHandle(DeleteAction action) =>
        action is DeleteAction.QrsDelete or DeleteAction.QrsUnpublishThenDelete;

    public async Task ExecuteAsync(QrsApp app, IReadOnlyList<SheetRow> rows, CancellationToken ct)
    {
        if (!app.Published)
        {
            foreach (var r in rows) r.SetResult(RowStatus.Error, "QRS silme stratejisi yayınlanmamış app için çağrıldı; işlem yapılmadı");
            return;
        }

        // 1) Yedek: her owner için tek Engine oturumu (salt okuma)
        var backedUp = new List<SheetRow>();
        foreach (var group in rows.GroupBy(r => r.Owner!, OwnerComparer.Instance))
        {
            if (ct.IsCancellationRequested) return;
            var owner = group.Key;

            EngineSession session;
            try
            {
                session = await _engine.OpenAppAsync(app.Id, owner, _backupWithoutData, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error(ex, "Yedek için app {AppId} owner {Owner} adına açılamadı", app.Id, owner);
                foreach (var r in group)
                    r.SetResult(RowStatus.Error, $"Yedek alınamadı: app'e owner ({owner}) adına bağlanılamadı: {ex.Message}");
                continue;
            }

            await using (session)
            {
                foreach (var row in group)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        row.BackupPath = await _backup.BackupSheetAsync(session, app, row, ct);
                        backedUp.Add(row);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Sheet yedeği alınamadı: {Row}", row);
                        row.SetResult(RowStatus.Error, $"Yedek alınamadı (JSON): {ex.Message}");
                    }
                }
            }
            // Oturum silmeden ÖNCE kapatılır.
        }

        // 2) Silme (retry yok)
        foreach (var row in backedUp)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await DeleteRowAsync(row, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "QRS silme başarısız: {Row}", row);
                row.SetResult(RowStatus.Error, $"QRS silme hatası: {ex.Message}");
            }
        }
    }

    private async Task DeleteRowAsync(SheetRow row, CancellationToken ct)
    {
        var id = row.QrsObjectId ?? throw new InvalidOperationException("QRS object id yok");

        // Silmeden hemen önce tekrar kontrol
        var current = await _qrs.GetAppObjectAsync(id, ct);
        if (!PassesRecheck(row, current, expectPublished: row.Action == DeleteAction.QrsUnpublishThenDelete))
            return;

        if (row.Action == DeleteAction.QrsUnpublishThenDelete)
        {
            await _qrs.UnpublishAppObjectAsync(id, ct);

            current = await _qrs.GetAppObjectAsync(id, ct);
            if (!PassesRecheck(row, current, expectPublished: false))
                return;
        }

        await _qrs.DeleteAppObjectAsync(id, ct);
        row.SetResult(RowStatus.Deleted, row.Action == DeleteAction.QrsUnpublishThenDelete
            ? "QRS unpublish + DELETE"
            : "QRS DELETE");
    }

    /// <summary>
    /// approved=false olmalı (aksi halde base sheet → HER ZAMAN atla) ve published beklenen değerde olmalı.
    /// </summary>
    private bool PassesRecheck(SheetRow row, QrsAppObject? current, bool expectPublished)
    {
        if (current is null)
        {
            row.SetResult(RowStatus.Error, "Silmeden önceki kontrolde sheet QRS'te bulunamadı (başkası silmiş olabilir)");
            return false;
        }

        row.SheetApproved = current.Approved;
        row.SheetPublished = current.Published;

        if (current.Approved)
        {
            row.Scenario = Scenario.D;
            row.SetResult(RowStatus.Skipped, $"{SheetClassifier.BaseSheetReason} (silmeden önceki kontrolde approved=true)");
            _log.Warning("Silmeden önce approved=true tespit edildi, atlandı: {Row}", row);
            return false;
        }

        if (current.Published != expectPublished)
        {
            row.SetResult(RowStatus.Skipped,
                $"Sheet durumu ön kontrolden sonra değişti (published={current.Published}); güvenlik için atlandı");
            _log.Warning("Sheet durumu değişti, atlandı: {Row}", row);
            return false;
        }

        return true;
    }

    private sealed class OwnerComparer : IEqualityComparer<QlikIdentity>
    {
        public static readonly OwnerComparer Instance = new();
        public bool Equals(QlikIdentity? x, QlikIdentity? y) => x is not null && y is not null && x.Matches(y);
        public int GetHashCode(QlikIdentity obj) =>
            HashCode.Combine(obj.UserDirectory.ToUpperInvariant(), obj.UserId.ToUpperInvariant());
    }
}
