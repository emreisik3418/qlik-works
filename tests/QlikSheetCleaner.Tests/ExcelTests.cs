using ClosedXML.Excel;
using QlikSheetCleaner.Configuration;
using QlikSheetCleaner.Excel;
using QlikSheetCleaner.Models;
using Xunit;

namespace QlikSheetCleaner.Tests;

public sealed class ExcelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qsc-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ExcelSettings _settings = new();

    private const string App1 = "11111111-2222-3333-4444-555555555555";
    private const string Sheet1 = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Sheet2 = "aaaaaaaa-bbbb-cccc-dddd-ffffffffffff";

    public ExcelTests() => Directory.CreateDirectory(_dir);

    private string CreateInput()
    {
        var path = Path.Combine(_dir, "input.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        ws.Cell(1, 1).Value = "Silinecek sheet listesi"; // başlık üstünde açıklama satırı
        ws.Cell(3, 1).Value = "Not";
        ws.Cell(3, 2).Value = " appid ";                  // farklı büyük/küçük harf + boşluk
        ws.Cell(3, 3).Value = "SheetId";
        ws.Cell(4, 2).Value = App1; ws.Cell(4, 3).Value = Sheet1;               // geçerli
        ws.Cell(5, 2).Value = App1.ToUpperInvariant(); ws.Cell(5, 3).Value = Sheet1.ToUpperInvariant(); // tekrar
        // 6: boş satır
        ws.Cell(7, 2).Value = "not-a-guid"; ws.Cell(7, 3).Value = Sheet2;       // geçersiz
        ws.Cell(8, 2).Value = App1;                                             // eksik SheetId
        ws.Cell(9, 1).Value = "sadece not";                                     // AppId/SheetId boş → boş satır sayılır
        ws.Cell(10, 2).Value = "{" + App1 + "}"; ws.Cell(10, 3).Value = Sheet2; // geçerli (süslü parantez)
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void Reader_finds_header_and_validates_rows()
    {
        var result = new ExcelReader(_settings).Read(CreateInput());

        Assert.Equal(3, result.HeaderRowNumber);
        Assert.Equal(2, result.EmptyRowCount);
        Assert.Equal(5, result.Rows.Count);

        var byRow = result.Rows.ToDictionary(r => r.RowNumber);
        Assert.True(byRow[4].IsValid);
        Assert.Equal(Sheet1, byRow[4].SheetId);
        Assert.Equal(RowStatus.Skipped, byRow[5].Status);
        Assert.Contains("Tekrar", byRow[5].Reason);
        Assert.Equal(RowStatus.Error, byRow[7].Status);
        Assert.Contains("Geçersiz GUID", byRow[7].Reason);
        Assert.Equal(RowStatus.Error, byRow[8].Status);
        Assert.Contains("Eksik", byRow[8].Reason);
        Assert.True(byRow[10].IsValid);
    }

    [Fact]
    public void Missing_header_throws()
    {
        var path = Path.Combine(_dir, "noheader.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("Sheet1").Cell(1, 1).Value = "x";
            wb.SaveAs(path);
        }
        Assert.Throws<InvalidOperationException>(() => new ExcelReader(_settings).Read(path));
    }

    [Fact]
    public void Report_is_a_copy_with_result_columns_and_original_is_untouched()
    {
        var input = CreateInput();
        var originalBytes = File.ReadAllBytes(input);
        var read = new ExcelReader(_settings).Read(input);
        var row = read.Rows.Single(r => r.RowNumber == 4);
        row.Scenario = Scenario.B;
        row.AppName = "Satış";
        row.AppPublished = true;
        row.SheetApproved = false;
        row.SetResult(RowStatus.WouldDelete, "Dry-run");

        var output = Path.Combine(_dir, "out.xlsx");
        new ReportWriter(_settings).Write(input, output, read.HeaderRowNumber, read.Rows, "DRY-RUN");

        Assert.Equal(originalBytes, File.ReadAllBytes(input));
        using var wb = new XLWorkbook(output);
        var ws = wb.Worksheet("Sheet1");
        var headers = ws.Row(3).CellsUsed().Select(c => c.GetString()).ToList();
        foreach (var col in ReportWriter.Columns) Assert.Contains(col, headers);

        int Col(string name) => ws.Row(3).CellsUsed().Single(c => c.GetString() == name).Address.ColumnNumber;
        Assert.Equal("B", ws.Cell(4, Col("Scenario")).GetString());
        Assert.Equal("WouldDelete", ws.Cell(4, Col("Status")).GetString());
        Assert.Equal("Satış", ws.Cell(4, Col("AppName")).GetString());
        Assert.True(ws.Cell(4, Col("AppPublished")).GetBoolean());
        Assert.Equal("Skipped", ws.Cell(5, Col("Status")).GetString());
        Assert.True(wb.TryGetWorksheet("CleanupSummary", out _));
    }

    [Fact]
    public void Report_refuses_to_overwrite_input()
    {
        var input = CreateInput();
        Assert.Throws<InvalidOperationException>(() => ReportWriter.EnsureNotSameFile(input, input));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
