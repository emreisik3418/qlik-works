using QlikSheetCleaner.Configuration;
using QlikSheetCleaner.Models;
using QlikSheetCleaner.Qlik;
using Serilog;

namespace QlikSheetCleaner.Processing;

/// <summary>QRS üzerinden ön kontrol: app var mı, sheet bu app'e ait ve sheet mi, sahiplik/durum bilgisi, senaryo ataması.</summary>
public sealed class PreflightService
{
    private readonly QrsClient _qrs;
    private readonly SheetClassifier _classifier;
    private readonly ProcessingSettings _settings;
    private readonly ILogger _log = Log.ForContext<PreflightService>();

    public PreflightService(QrsClient qrs, SheetClassifier classifier, ProcessingSettings settings)
    {
        _qrs = qrs;
        _classifier = classifier;
        _settings = settings;
    }

    public async Task CheckAppAsync(AppWorkItem work, bool execute, CancellationToken ct)
    {
        // 1) App var mı?
        QrsApp? app;
        try
        {
            app = await _qrs.GetAppAsync(work.AppId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "App {AppId} QRS'ten okunamadı", work.AppId);
            FailAll(work.Rows, $"App bilgisi okunamadı: {ex.Message}");
            return;
        }

        if (app is null)
        {
            FailAll(work.Rows, "App bulunamadı (QRS 404)");
            return;
        }

        work.App = app;
        foreach (var row in work.Rows)
        {
            row.AppName = app.Name;
            row.AppPublished = app.Published;
        }

        // 2) App'in sheet'leri
        IReadOnlyList<QrsAppObject> sheets;
        try
        {
            sheets = await _qrs.GetSheetsAsync(work.AppId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "App {AppId} sheet listesi okunamadı", work.AppId);
            FailAll(work.Rows, $"Sheet listesi okunamadı: {ex.Message}");
            return;
        }

        _log.Information("App {AppName} ({AppId}) published={Published}, {Count} sheet bulundu",
            app.Name, app.Id, app.Published, sheets.Count);

        foreach (var row in work.Rows)
        {
            ct.ThrowIfCancellationRequested();
            await CheckRowAsync(app, sheets, row, execute, ct);
        }
    }

    private async Task CheckRowAsync(QrsApp app, IReadOnlyList<QrsAppObject> sheets, SheetRow row, bool execute, CancellationToken ct)
    {
        var matches = FindMatches(sheets, row.SheetId);

        if (matches.Count == 0)
        {
            var reason = await DiagnoseMissingAsync(app.Id, row.SheetId, ct);
            row.SetResult(RowStatus.Error, reason);
            return;
        }

        if (matches.Count > 1)
        {
            row.SetResult(RowStatus.Error,
                $"SheetId birden fazla nesneyle eşleşti ({string.Join(", ", matches.Select(m => m.Id))}); güvenlik için atlandı");
            return;
        }

        var sheet = matches[0];

        // 2) app'e ait mi ve objectType = 'sheet' mi? (filtre zaten bunu sağlar; savunma amaçlı tekrar kontrol)
        if (sheet.App is not null && sheet.App.Id != app.Id)
        {
            row.SetResult(RowStatus.Error, $"Sheet başka bir app'e ait ({sheet.App.Id})");
            return;
        }
        if (!string.Equals(sheet.ObjectType, "sheet", StringComparison.OrdinalIgnoreCase))
        {
            row.SetResult(RowStatus.Error, $"Nesne sheet değil (objectType={sheet.ObjectType})");
            return;
        }

        // 3) owner/published/approved/name
        row.QrsObjectId = sheet.Id;
        row.EngineObjectId = sheet.EngineObjectId;
        row.SheetTitle = sheet.Name;
        row.SheetPublished = sheet.Published;
        row.SheetApproved = sheet.Approved;
        if (sheet.Owner is not null)
        {
            row.Owner = new QlikIdentity(sheet.Owner.UserDirectory, sheet.Owner.UserId);
            row.SheetOwner = row.Owner.ToString();
        }

        // 4) senaryo
        var c = _classifier.Classify(app.Published, sheet.Published, sheet.Approved);
        row.Scenario = c.Scenario;
        row.Action = c.Action;

        if (c.Action == DeleteAction.Skip)
        {
            row.SetResult(RowStatus.Skipped, c.SkipReason ?? "Atlandı");
            return;
        }

        if (c.Scenario is Scenario.B or Scenario.C && row.Owner is null)
        {
            row.SetResult(RowStatus.Error, "Sheet sahibi QRS'te bulunamadı; owner adına yedek alınamaz");
            return;
        }

        if (c.Scenario == Scenario.A && app.Owner is null)
        {
            row.SetResult(RowStatus.Error, "App sahibi QRS'te bulunamadı");
            return;
        }

        var actionText = c.Action switch
        {
            DeleteAction.EngineDestroy => "Engine DestroyObject + DoSave",
            DeleteAction.QrsDelete => "QRS DELETE",
            DeleteAction.QrsUnpublishThenDelete => "QRS unpublish + DELETE",
            _ => c.Action.ToString()
        };
        row.SetResult(RowStatus.WouldDelete, execute ? $"Silinmeye hazır ({actionText})" : $"Dry-run: silinecekti ({actionText})");
    }

    private List<QrsAppObject> FindMatches(IReadOnlyList<QrsAppObject> sheets, string sheetId)
    {
        var byEngineId = sheets.Where(s => string.Equals(s.EngineObjectId, sheetId, StringComparison.OrdinalIgnoreCase)).ToList();
        var byQrsId = Guid.TryParse(sheetId, out var g) ? sheets.Where(s => s.Id == g).ToList() : [];

        return _settings.SheetIdMatch switch
        {
            SheetIdMatchMode.EngineObjectId => byEngineId,
            SheetIdMatchMode.QrsId => byQrsId,
            _ => byEngineId.Count > 0 ? byEngineId : byQrsId
        };
    }

    private async Task<string> DiagnoseMissingAsync(Guid appId, string sheetId, CancellationToken ct)
    {
        try
        {
            var others = await _qrs.FindObjectsByEngineIdAsync(appId, sheetId, ct);
            if (others.Count > 0)
                return $"Nesne sheet değil (objectType={string.Join(",", others.Select(o => o.ObjectType).Distinct())})";

            if (Guid.TryParse(sheetId, out var g))
            {
                var byId = await _qrs.GetAppObjectAsync(g, ct);
                if (byId is not null && byId.App?.Id != appId)
                    return $"Sheet bu app'e ait değil (ait olduğu app: {byId.App?.Id})";
                if (byId is not null)
                    return $"Nesne sheet değil (objectType={byId.ObjectType})";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Debug(ex, "Eksik sheet teşhisi başarısız");
        }

        return "Sheet bu app'te bulunamadı";
    }

    private static void FailAll(IEnumerable<SheetRow> rows, string reason)
    {
        foreach (var row in rows) row.SetResult(RowStatus.Error, reason);
    }
}
