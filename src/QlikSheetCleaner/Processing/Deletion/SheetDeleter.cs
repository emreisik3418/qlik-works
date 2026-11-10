using QlikSheetCleaner.Models;
using Serilog;

namespace QlikSheetCleaner.Processing.Deletion;

/// <summary>App'e ait silinecek satırları uygun stratejiye (Engine / QRS) dağıtır.</summary>
public sealed class SheetDeleter
{
    private readonly IReadOnlyList<ISheetDeletionStrategy> _strategies;
    private readonly ILogger _log = Log.ForContext<SheetDeleter>();

    public SheetDeleter(IEnumerable<ISheetDeletionStrategy> strategies) => _strategies = strategies.ToList();

    public async Task ExecuteAsync(AppWorkItem work, CancellationToken ct)
    {
        if (work.App is null) return;

        // Son savunma hattı: D senaryosu ve Skip aksiyonları hiçbir koşulda stratejiye gitmez.
        var actionable = work.Rows.Where(r => r.IsActionable && r.Scenario != Scenario.D).ToList();
        if (actionable.Count == 0) return;

        // Senaryo B ve C aynı stratejiye düşer → owner başına tek Engine oturumu
        foreach (var unhandled in actionable.Where(r => !_strategies.Any(s => s.CanHandle(r.Action))))
            unhandled.SetResult(RowStatus.Error, $"{unhandled.Action} için silme stratejisi yok");

        foreach (var group in actionable
                     .Where(r => _strategies.Any(s => s.CanHandle(r.Action)))
                     .GroupBy(r => _strategies.First(s => s.CanHandle(r.Action))))
        {
            var strategy = group.Key;
            _log.Information("{Strategy}: app {AppName} ({AppId}), {Count} sheet",
                strategy.Name, work.App.Name, work.AppId, group.Count());

            try
            {
                await strategy.ExecuteAsync(work.App, group.ToList(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Beklenmeyen hata: o app'in işlenmemiş satırlarını hataya çek, diğer app'lere devam et
                _log.Error(ex, "App {AppId} işlenirken beklenmeyen hata", work.AppId);
                foreach (var r in group.Where(r => r.Status == RowStatus.WouldDelete))
                    r.SetResult(RowStatus.Error, $"Beklenmeyen hata: {ex.Message}");
            }
        }
    }
}
