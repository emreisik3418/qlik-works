using ClosedXML.Excel;
using QlikSheetCleaner.Configuration;
using QlikSheetCleaner.Models;
using Serilog;

namespace QlikSheetCleaner.Excel;

/// <summary>Girdi Excel'inin kopyasını oluşturur ve her satıra sonuç kolonlarını ekler. Orijinal dosyaya dokunmaz.</summary>
public sealed class ReportWriter
{
    public static readonly string[] Columns =
    [
        "Scenario", "Status", "Reason", "AppName", "AppPublished", "SheetTitle", "SheetOwner",
        "SheetPublished", "SheetApproved", "BackupPath", "ProcessedAt"
    ];

    private readonly ExcelSettings _settings;
    private readonly ILogger _log = Log.ForContext<ReportWriter>();

    public ReportWriter(ExcelSettings settings) => _settings = settings;

    public static string DefaultOutputPath(string inputPath, bool execute)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(inputPath))!;
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var mode = execute ? "execute" : "dryrun";
        return Path.Combine(dir, $"{name}_result_{mode}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
    }

    public static void EnsureNotSameFile(string inputPath, string outputPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), comparison))
            throw new InvalidOperationException("--output girdi dosyası ile aynı olamaz; orijinal dosyanın üzerine yazılmaz.");
    }

    public void Write(string inputPath, string outputPath, int headerRow, IReadOnlyList<SheetRow> rows, string modeText)
    {
        EnsureNotSameFile(inputPath, outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        // Orijinali kopyala (girdi Excel'de açık olabilir → paylaşımlı okuma)
        using (var source = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var target = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(target);
        }

        using var workbook = new XLWorkbook(outputPath);
        var ws = ExcelReader.GetWorksheet(workbook, _settings.SheetName);

        // Kolonları bul (aynı rapor tekrar girdi olarak verilmişse mevcut kolonları yeniden kullan) ya da sona ekle
        var columnMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var header = ws.Row(headerRow);
        foreach (var cell in header.CellsUsed())
        {
            var text = ExcelReader.CellText(cell);
            if (Columns.Contains(text, StringComparer.OrdinalIgnoreCase) && !columnMap.ContainsKey(text))
                columnMap[text] = cell.Address.ColumnNumber;
        }

        var nextCol = (header.LastCellUsed()?.Address.ColumnNumber ?? 0) + 1;
        foreach (var name in Columns)
        {
            if (columnMap.ContainsKey(name)) continue;
            columnMap[name] = nextCol++;
            var cell = ws.Cell(headerRow, columnMap[name]);
            cell.Value = name;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        foreach (var row in rows)
        {
            var r = row.RowNumber;
            Set(ws, r, columnMap["Scenario"], row.Scenario == Scenario.None ? "" : row.Scenario.ToString());
            var statusCell = Set(ws, r, columnMap["Status"], row.Status == RowStatus.Pending ? "" : row.Status.ToString());
            Set(ws, r, columnMap["Reason"], row.Reason);
            Set(ws, r, columnMap["AppName"], row.AppName);
            SetBool(ws, r, columnMap["AppPublished"], row.AppPublished);
            Set(ws, r, columnMap["SheetTitle"], row.SheetTitle);
            Set(ws, r, columnMap["SheetOwner"], row.SheetOwner);
            SetBool(ws, r, columnMap["SheetPublished"], row.SheetPublished);
            SetBool(ws, r, columnMap["SheetApproved"], row.SheetApproved);
            Set(ws, r, columnMap["BackupPath"], row.BackupPath);

            var processed = ws.Cell(r, columnMap["ProcessedAt"]);
            if (row.ProcessedAt is { } at)
            {
                processed.Value = at.LocalDateTime;
                processed.Style.DateFormat.Format = "yyyy-MM-dd HH:mm:ss";
            }
            else
            {
                processed.Clear(XLClearOptions.Contents);
            }

            statusCell.Style.Fill.BackgroundColor = row.Status switch
            {
                RowStatus.Deleted => XLColor.LightGreen,
                RowStatus.WouldDelete => XLColor.LightYellow,
                RowStatus.Skipped => XLColor.LightBlue,
                RowStatus.Error => XLColor.Salmon,
                _ => XLColor.NoColor
            };
        }

        WriteSummarySheet(workbook, rows, modeText);

        foreach (var col in columnMap.Values) ws.Column(col).AdjustToContents(headerRow, headerRow + Math.Min(rows.Count, 500));
        workbook.Save();

        _log.Information("Rapor yazıldı: {Output}", outputPath);
    }

    private static void WriteSummarySheet(XLWorkbook workbook, IReadOnlyList<SheetRow> rows, string modeText)
    {
        const string name = "CleanupSummary";
        if (workbook.TryGetWorksheet(name, out var existing)) existing.Delete();
        var ws = workbook.AddWorksheet(name);

        ws.Cell(1, 1).Value = "Mod";
        ws.Cell(1, 2).Value = modeText;
        ws.Cell(2, 1).Value = "Oluşturulma";
        ws.Cell(2, 2).Value = DateTime.Now;
        ws.Cell(2, 2).Style.DateFormat.Format = "yyyy-MM-dd HH:mm:ss";

        var statuses = new[] { RowStatus.WouldDelete, RowStatus.Deleted, RowStatus.Skipped, RowStatus.Error };
        ws.Cell(4, 1).Value = "Scenario";
        for (var i = 0; i < statuses.Length; i++) ws.Cell(4, i + 2).Value = statuses[i].ToString();
        ws.Cell(4, statuses.Length + 2).Value = "Toplam";
        ws.Row(4).Style.Font.Bold = true;

        var scenarios = new[] { Scenario.A, Scenario.B, Scenario.C, Scenario.D, Scenario.None };
        var r = 5;
        foreach (var s in scenarios)
        {
            ws.Cell(r, 1).Value = s == Scenario.None ? "(atanmadı)" : s.ToString();
            for (var i = 0; i < statuses.Length; i++)
                ws.Cell(r, i + 2).Value = rows.Count(x => x.Scenario == s && x.Status == statuses[i]);
            ws.Cell(r, statuses.Length + 2).Value = rows.Count(x => x.Scenario == s);
            r++;
        }
        ws.Columns().AdjustToContents();
    }

    private static IXLCell Set(IXLWorksheet ws, int row, int col, string? value)
    {
        var cell = ws.Cell(row, col);
        cell.Value = value ?? "";
        return cell;
    }

    private static void SetBool(IXLWorksheet ws, int row, int col, bool? value)
    {
        var cell = ws.Cell(row, col);
        if (value is null) cell.Value = "";
        else cell.Value = value.Value;
    }
}
