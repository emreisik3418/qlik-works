using QlikSheetCleaner.Configuration;
using QlikSheetCleaner.Excel;
using QlikSheetCleaner.Models;
using QlikSheetCleaner.Processing.Deletion;
using Serilog;

namespace QlikSheetCleaner.Processing;

public sealed record RunOptions(string InputPath, string OutputPath, bool Execute, bool AssumeYes);

public enum RunOutcome
{
    Success = 0,
    CompletedWithErrors = 1,
    Fatal = 2,
    Aborted = 3
}

/// <summary>Akış: Excel oku → ön kontrol (QRS) → özet + onay → (execute) yedek + silme → rapor.</summary>
public sealed class CleanupRunner
{
    private readonly AppSettings _settings;
    private readonly ExcelReader _reader;
    private readonly PreflightService _preflight;
    private readonly SheetDeleter _deleter;
    private readonly ReportWriter _reportWriter;
    private readonly Func<string?> _readConfirmation;
    private readonly ILogger _log = Log.ForContext<CleanupRunner>();

    public CleanupRunner(
        AppSettings settings,
        ExcelReader reader,
        PreflightService preflight,
        SheetDeleter deleter,
        ReportWriter reportWriter,
        Func<string?>? readConfirmation = null)
    {
        _settings = settings;
        _reader = reader;
        _preflight = preflight;
        _deleter = deleter;
        _reportWriter = reportWriter;
        _readConfirmation = readConfirmation ?? Console.ReadLine;
    }

    public async Task<RunOutcome> RunAsync(RunOptions options, CancellationToken ct)
    {
        ReportWriter.EnsureNotSameFile(options.InputPath, options.OutputPath);

        var read = _reader.Read(options.InputPath);
        var rows = read.Rows;
        var modeText = options.Execute ? "EXECUTE" : "DRY-RUN";
        var outcome = RunOutcome.Success;

        try
        {
            // ---- Ön kontrol
            var work = rows.Where(r => r.IsValid)
                .GroupBy(r => r.AppId)
                .Select(g => new AppWorkItem(g.Key, g.ToList()))
                .ToList();

            _log.Information("Ön kontrol başlıyor: {Apps} app, {Rows} geçerli satır, paralellik {Dop}",
                work.Count, work.Sum(w => w.Rows.Count), _settings.Processing.MaxDegreeOfParallelism);

            await ForEachAppAsync(work, (w, t) => _preflight.CheckAppAsync(w, options.Execute, t), ct);

            Console.WriteLine(SummaryPrinter.BuildPreflightSummary(rows, options.Execute, _settings.Processing.AllowCommunitySheetDelete));

            var toDelete = rows.Count(r => r.IsActionable);

            if (!options.Execute)
            {
                _log.Information("DRY-RUN tamamlandı; hiçbir şey silinmedi. Gerçek silme için --execute kullanın.");
            }
            else if (toDelete == 0)
            {
                _log.Information("Silinecek sheet yok.");
            }
            else
            {
                if (!options.AssumeYes && !Confirm(toDelete))
                {
                    _log.Warning("Kullanıcı onay vermedi; hiçbir şey silinmedi.");
                    foreach (var r in rows.Where(r => r.Status == RowStatus.WouldDelete))
                        r.SetResult(RowStatus.Skipped, "Kullanıcı onay vermedi");
                    outcome = RunOutcome.Aborted;
                    return outcome;
                }

                _log.Information("EXECUTE başlıyor: {Count} sheet silinecek. Yedek klasörü: {Backup}",
                    toDelete, Path.GetFullPath(_settings.Processing.BackupFolder));

                await ForEachAppAsync(work.Where(w => w.Rows.Any(r => r.IsActionable)).ToList(),
                    (w, t) => _deleter.ExecuteAsync(w, t), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.Warning("İşlem iptal edildi (Ctrl+C). Tamamlanan satırlar raporlanacak.");
            outcome = RunOutcome.Aborted;
        }
        finally
        {
            // İşlenmeden kalan satırları işaretle
            foreach (var r in rows)
            {
                if (r.Status == RowStatus.Pending)
                    r.SetResult(RowStatus.Skipped, "İşlenmedi (iptal)");
                else if (options.Execute && r.Status == RowStatus.WouldDelete)
                    r.SetResult(ct.IsCancellationRequested ? RowStatus.Skipped : RowStatus.Error,
                        ct.IsCancellationRequested ? "İşlenmedi (iptal)" : "İşlenmedi (beklenmeyen durum)");
            }

            try
            {
                _reportWriter.Write(options.InputPath, options.OutputPath, read.HeaderRowNumber, rows, modeText);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Rapor yazılamadı: {Output}", options.OutputPath);
                outcome = RunOutcome.Fatal;
            }

            Console.WriteLine(SummaryPrinter.BuildFinalSummary(rows));
            Console.WriteLine($"Rapor: {Path.GetFullPath(options.OutputPath)}");
        }

        if (outcome == RunOutcome.Success && rows.Any(r => r.Status == RowStatus.Error))
            outcome = RunOutcome.CompletedWithErrors;
        return outcome;
    }

    private bool Confirm(int toDelete)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"{toDelete} sheet KALICI olarak silinecek (önce yedek alınacak).");
        Console.Write("Devam etmek için büyük harflerle EVET yazın: ");
        Console.ResetColor();

        var answer = _readConfirmation();
        return string.Equals(answer?.Trim(), "EVET", StringComparison.Ordinal);
    }

    private Task ForEachAppAsync(IReadOnlyList<AppWorkItem> work, Func<AppWorkItem, CancellationToken, Task> body, CancellationToken ct) =>
        Parallel.ForEachAsync(work,
            new ParallelOptions { MaxDegreeOfParallelism = _settings.Processing.MaxDegreeOfParallelism, CancellationToken = ct },
            async (w, t) => await body(w, t));
}
