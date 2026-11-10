using QlikSheetCleaner.Qlik;

namespace QlikSheetCleaner.Models;

public enum Scenario
{
    None,
    /// <summary>Yayınlanmamış (Work) app → Engine API ile silinir.</summary>
    A,
    /// <summary>Yayınlanmış app, kişisel sheet → QRS API ile silinir.</summary>
    B,
    /// <summary>Yayınlanmış app, community sheet → varsayılan atla; izin varsa unpublish + QRS sil.</summary>
    C,
    /// <summary>Yayınlanmış app, base (approved) sheet → HER ZAMAN atla.</summary>
    D
}

public enum RowStatus
{
    Pending,
    WouldDelete,
    Deleted,
    Skipped,
    Error
}

public enum DeleteAction
{
    None,
    Skip,
    EngineDestroy,
    QrsDelete,
    QrsUnpublishThenDelete
}

/// <summary>Excel'deki tek bir satır ve işlem sonucu. Bir satır aynı anda tek bir worker tarafından işlenir.</summary>
public sealed class SheetRow
{
    public required int RowNumber { get; init; }
    public required string AppIdRaw { get; init; }
    public required string SheetIdRaw { get; init; }

    public Guid AppId { get; set; }
    /// <summary>Normalize edilmiş (küçük harf, "D" formatı) SheetId.</summary>
    public string SheetId { get; set; } = "";
    public bool IsValid { get; set; }

    public Scenario Scenario { get; set; } = Scenario.None;
    public DeleteAction Action { get; set; } = DeleteAction.None;

    public RowStatus Status { get; private set; } = RowStatus.Pending;
    public string Reason { get; private set; } = "";

    // Rapor kolonları
    public string? AppName { get; set; }
    public bool? AppPublished { get; set; }
    public string? SheetTitle { get; set; }
    public string? SheetOwner { get; set; }
    public bool? SheetPublished { get; set; }
    public bool? SheetApproved { get; set; }
    public string? BackupPath { get; set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    // İç bilgiler (QRS'ten)
    public Guid? QrsObjectId { get; set; }
    public string? EngineObjectId { get; set; }
    public QlikIdentity? Owner { get; set; }

    public bool IsActionable =>
        Status == RowStatus.WouldDelete &&
        Action is DeleteAction.EngineDestroy or DeleteAction.QrsDelete or DeleteAction.QrsUnpublishThenDelete;

    public void SetResult(RowStatus status, string reason)
    {
        Status = status;
        Reason = reason;
        ProcessedAt = DateTimeOffset.Now;
    }

    public override string ToString() => $"Satır {RowNumber} (App={AppIdRaw}, Sheet={SheetIdRaw})";
}
