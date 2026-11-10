using System.Text;
using QlikSheetCleaner.Models;

namespace QlikSheetCleaner.Processing;

/// <summary>Konsolda senaryo ve durum bazında özet tablolar.</summary>
public static class SummaryPrinter
{
    private static readonly Scenario[] Scenarios = [Scenario.A, Scenario.B, Scenario.C, Scenario.D, Scenario.None];

    public static string BuildPreflightSummary(IReadOnlyList<SheetRow> rows, bool execute, bool allowCommunity)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("==================== ÖN KONTROL ÖZETİ ====================");
        sb.AppendLine($"Mod                         : {(execute ? "EXECUTE (gerçek silme)" : "DRY-RUN (hiçbir şey silinmez)")}");
        sb.AppendLine($"AllowCommunitySheetDelete   : {allowCommunity}");
        sb.AppendLine($"Toplam satır                : {rows.Count}");
        sb.AppendLine();
        sb.AppendLine($"{"Senaryo",-34}{"Toplam",8}{"Silinecek",11}{"Atlanacak",11}{"Hata",7}");
        foreach (var s in Scenarios)
        {
            var inScenario = rows.Where(r => r.Scenario == s).ToList();
            if (s == Scenario.None && inScenario.Count == 0) continue;
            sb.AppendLine($"{Label(s),-34}{inScenario.Count,8}" +
                          $"{inScenario.Count(r => r.Status == RowStatus.WouldDelete),11}" +
                          $"{inScenario.Count(r => r.Status == RowStatus.Skipped),11}" +
                          $"{inScenario.Count(r => r.Status == RowStatus.Error),7}");
        }
        sb.AppendLine(new string('-', 71));
        sb.AppendLine($"{"TOPLAM",-34}{rows.Count,8}{rows.Count(r => r.Status == RowStatus.WouldDelete),11}" +
                      $"{rows.Count(r => r.Status == RowStatus.Skipped),11}{rows.Count(r => r.Status == RowStatus.Error),7}");
        sb.AppendLine();
        sb.AppendLine($"Etkilenecek app sayısı      : {rows.Where(r => r.Status == RowStatus.WouldDelete).Select(r => r.AppId).Distinct().Count()}");
        sb.AppendLine("==========================================================");
        return sb.ToString();
    }

    public static string BuildFinalSummary(IReadOnlyList<SheetRow> rows)
    {
        var statuses = new[] { RowStatus.WouldDelete, RowStatus.Deleted, RowStatus.Skipped, RowStatus.Error };
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("======================== SONUÇ ÖZETİ ========================");
        sb.Append($"{"Senaryo",-34}");
        foreach (var st in statuses) sb.Append($"{st,12}");
        sb.AppendLine();
        foreach (var s in Scenarios)
        {
            var inScenario = rows.Where(r => r.Scenario == s).ToList();
            if (inScenario.Count == 0) continue;
            sb.Append($"{Label(s),-34}");
            foreach (var st in statuses) sb.Append($"{inScenario.Count(r => r.Status == st),12}");
            sb.AppendLine();
        }
        sb.Append($"{"TOPLAM",-34}");
        foreach (var st in statuses) sb.Append($"{rows.Count(r => r.Status == st),12}");
        sb.AppendLine();

        var topReasons = rows.Where(r => r.Status is RowStatus.Error or RowStatus.Skipped)
            .GroupBy(r => $"{r.Status}: {Truncate(r.Reason, 90)}")
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToList();
        if (topReasons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("En sık atlama/hata nedenleri:");
            foreach (var g in topReasons) sb.AppendLine($"  {g.Count(),5} × {g.Key}");
        }
        sb.AppendLine("=============================================================");
        return sb.ToString();
    }

    private static string Label(Scenario s) => s switch
    {
        Scenario.A => "A - Work app (Engine)",
        Scenario.B => "B - Kişisel sheet (QRS)",
        Scenario.C => "C - Community sheet",
        Scenario.D => "D - Base sheet (her zaman atla)",
        _ => "(atanmadı: geçersiz/bulunamadı)"
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
