using ClosedXML.Excel;
using QlikSheetCleaner.Configuration;
using QlikSheetCleaner.Models;
using Serilog;

namespace QlikSheetCleaner.Excel;

public sealed record ExcelReadResult(IReadOnlyList<SheetRow> Rows, int HeaderRowNumber, int EmptyRowCount);

/// <summary>Girdi Excel'ini okur; boş satırları atlar, geçersiz/eksik/tekrar eden satırları işaretler.</summary>
public sealed class ExcelReader
{
    private readonly ExcelSettings _settings;
    private readonly ILogger _log = Log.ForContext<ExcelReader>();

    public ExcelReader(ExcelSettings settings) => _settings = settings;

    public ExcelReadResult Read(string path)
    {
        // Dosya Excel'de açık olsa bile okuyabilmek için FileShare.ReadWrite
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var workbook = new XLWorkbook(stream);

        var worksheet = GetWorksheet(workbook, _settings.SheetName);
        var (headerRow, appCol, sheetCol) = FindHeader(worksheet, _settings);

        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRow;
        var rows = new List<SheetRow>();
        var seen = new Dictionary<(Guid, string), int>();
        var emptyCount = 0;

        for (var r = headerRow + 1; r <= lastRow; r++)
        {
            var appRaw = CellText(worksheet.Cell(r, appCol));
            var sheetRaw = CellText(worksheet.Cell(r, sheetCol));

            if (appRaw.Length == 0 && sheetRaw.Length == 0)
            {
                emptyCount++;
                continue; // boş satır → atla
            }

            var row = new SheetRow { RowNumber = r, AppIdRaw = appRaw, SheetIdRaw = sheetRaw };
            rows.Add(row);

            if (appRaw.Length == 0 || sheetRaw.Length == 0)
            {
                row.SetResult(RowStatus.Error, appRaw.Length == 0 ? "Eksik değer: AppId boş" : "Eksik değer: SheetId boş");
                continue;
            }

            if (!TryParseGuid(appRaw, out var appId))
            {
                row.SetResult(RowStatus.Error, $"Geçersiz GUID: AppId '{appRaw}'");
                continue;
            }

            if (!TryParseGuid(sheetRaw, out var sheetGuid))
            {
                row.SetResult(RowStatus.Error, $"Geçersiz GUID: SheetId '{sheetRaw}'");
                continue;
            }

            row.AppId = appId;
            row.SheetId = sheetGuid.ToString("D"); // küçük harf

            var key = (appId, row.SheetId);
            if (seen.TryGetValue(key, out var firstRow))
            {
                row.SetResult(RowStatus.Skipped, $"Tekrar eden satır (ilk geçtiği satır: {firstRow})");
                continue;
            }

            seen[key] = r;
            row.IsValid = true;
        }

        _log.Information("Excel okundu: {Path} [{Sheet}] başlık satırı {Header}, {Total} satır ({Valid} geçerli, {Invalid} hatalı/tekrar, {Empty} boş atlandı)",
            path, worksheet.Name, headerRow, rows.Count, rows.Count(x => x.IsValid), rows.Count(x => !x.IsValid), emptyCount);

        return new ExcelReadResult(rows, headerRow, emptyCount);
    }

    internal static IXLWorksheet GetWorksheet(XLWorkbook workbook, string name)
    {
        if (workbook.TryGetWorksheet(name, out var ws)) return ws;
        var match = workbook.Worksheets.FirstOrDefault(w => string.Equals(w.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException(
            $"Excel'de '{name}' sayfası bulunamadı. Mevcut sayfalar: {string.Join(", ", workbook.Worksheets.Select(w => w.Name))}");
    }

    /// <summary>Başlık satırını kolon adlarına göre ilk N satırda arar.</summary>
    internal static (int HeaderRow, int AppColumn, int SheetColumn) FindHeader(IXLWorksheet worksheet, ExcelSettings settings)
    {
        var maxRow = Math.Min(Math.Max(1, settings.HeaderSearchRows), worksheet.LastRowUsed()?.RowNumber() ?? 0);
        for (var r = 1; r <= maxRow; r++)
        {
            var row = worksheet.Row(r);
            int? appCol = null, sheetCol = null;
            foreach (var cell in row.CellsUsed())
            {
                var text = CellText(cell);
                if (appCol is null && string.Equals(text, settings.AppIdColumn.Trim(), StringComparison.OrdinalIgnoreCase))
                    appCol = cell.Address.ColumnNumber;
                else if (sheetCol is null && string.Equals(text, settings.SheetIdColumn.Trim(), StringComparison.OrdinalIgnoreCase))
                    sheetCol = cell.Address.ColumnNumber;
            }

            if (appCol is not null && sheetCol is not null)
                return (r, appCol.Value, sheetCol.Value);
        }

        throw new InvalidOperationException(
            $"'{worksheet.Name}' sayfasının ilk {settings.HeaderSearchRows} satırında '{settings.AppIdColumn}' ve '{settings.SheetIdColumn}' başlıkları bulunamadı.");
    }

    internal static string CellText(IXLCell cell)
    {
        var text = cell.Value.IsBlank ? "" : cell.GetFormattedString();
        // Excel'den kopyalanan görünmez karakterleri temizle
        return text.Replace(" ", " ").Replace("​", "").Trim();
    }

    private static bool TryParseGuid(string text, out Guid value)
    {
        var t = text.Trim().Trim('{', '}');
        return Guid.TryParseExact(t, "D", out value) || Guid.TryParseExact(t, "N", out value);
    }
}
