using QlikSheetCleaner.Models;

namespace QlikSheetCleaner.Processing.Deletion;

public interface ISheetDeletionStrategy
{
    string Name { get; }

    bool CanHandle(DeleteAction action);

    /// <summary>
    /// Bir app'e ait, bu stratejiye düşen satırları yedekler ve siler.
    /// Her satırın sonucu (Deleted/Error/Skipped) satır üzerine yazılır; satır hatası diğer satırları durdurmaz.
    /// </summary>
    Task ExecuteAsync(QrsApp app, IReadOnlyList<SheetRow> rows, CancellationToken ct);
}
