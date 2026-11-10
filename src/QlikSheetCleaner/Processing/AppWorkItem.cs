using QlikSheetCleaner.Models;

namespace QlikSheetCleaner.Processing;

/// <summary>Aynı app'e ait geçerli satırlar. Bir app her zaman tek bir worker tarafından işlenir.</summary>
public sealed class AppWorkItem
{
    public AppWorkItem(Guid appId, List<SheetRow> rows)
    {
        AppId = appId;
        Rows = rows;
    }

    public Guid AppId { get; }
    public List<SheetRow> Rows { get; }
    public QrsApp? App { get; set; }
}
