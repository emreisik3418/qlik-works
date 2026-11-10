using QlikSheetCleaner.Models;

namespace QlikSheetCleaner.Processing;

public sealed record Classification(Scenario Scenario, DeleteAction Action, string? SkipReason);

/// <summary>Bir satırı A/B/C/D senaryolarından birine atar. Saf (yan etkisiz) mantık.</summary>
public sealed class SheetClassifier
{
    public const string BaseSheetReason = "Base sheet, sadece app yeniden publish edilerek kaldırılabilir";
    public const string CommunitySheetReason = "Community sheet";

    private readonly bool _allowCommunitySheetDelete;

    public SheetClassifier(bool allowCommunitySheetDelete) => _allowCommunitySheetDelete = allowCommunitySheetDelete;

    public Classification Classify(bool appPublished, bool sheetPublished, bool sheetApproved)
    {
        // A: yayınlanmamış (Work) app → Engine ile sil
        if (!appPublished)
            return new Classification(Scenario.A, DeleteAction.EngineDestroy, null);

        // D: base sheet → HER ZAMAN atla. Bu kural bilinçli olarak config'e bağlanmamıştır.
        if (sheetApproved)
            return new Classification(Scenario.D, DeleteAction.Skip, BaseSheetReason);

        // C: community sheet → varsayılan atla; izin varsa unpublish + QRS sil
        if (sheetPublished)
            return _allowCommunitySheetDelete
                ? new Classification(Scenario.C, DeleteAction.QrsUnpublishThenDelete, null)
                : new Classification(Scenario.C, DeleteAction.Skip, CommunitySheetReason);

        // B: kişisel sheet → QRS ile sil
        return new Classification(Scenario.B, DeleteAction.QrsDelete, null);
    }
}
