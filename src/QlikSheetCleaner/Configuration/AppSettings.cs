namespace QlikSheetCleaner.Configuration;

/// <summary>Root of appsettings.json.</summary>
public sealed class AppSettings
{
    public QlikSettings Qlik { get; set; } = new();
    public ExcelSettings Excel { get; set; } = new();
    public ProcessingSettings Processing { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();

    public void Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Qlik.Host)) errors.Add("Qlik:Host boş olamaz.");
        if (string.IsNullOrWhiteSpace(Qlik.ClientCertificatePath)) errors.Add("Qlik:ClientCertificatePath boş olamaz.");
        if (string.IsNullOrWhiteSpace(Qlik.RootCertificatePath)) errors.Add("Qlik:RootCertificatePath boş olamaz.");
        if (string.IsNullOrWhiteSpace(Qlik.UserDirectory) || string.IsNullOrWhiteSpace(Qlik.UserId))
            errors.Add("Qlik:UserDirectory ve Qlik:UserId boş olamaz.");
        if (string.IsNullOrWhiteSpace(Excel.SheetName)) errors.Add("Excel:SheetName boş olamaz.");
        if (string.IsNullOrWhiteSpace(Excel.AppIdColumn)) errors.Add("Excel:AppIdColumn boş olamaz.");
        if (string.IsNullOrWhiteSpace(Excel.SheetIdColumn)) errors.Add("Excel:SheetIdColumn boş olamaz.");
        if (string.IsNullOrWhiteSpace(Processing.BackupFolder)) errors.Add("Processing:BackupFolder boş olamaz.");
        if (Processing.MaxDegreeOfParallelism < 1) errors.Add("Processing:MaxDegreeOfParallelism en az 1 olmalı.");
        if (Qlik.ReadRetryCount < 0) errors.Add("Qlik:ReadRetryCount negatif olamaz.");

        if (errors.Count > 0)
            throw new InvalidOperationException("Geçersiz yapılandırma:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }
}

public sealed class QlikSettings
{
    /// <summary>Central node FQDN, ör. qlik.sirket.local (sertifikadaki ad ile aynı olmalı).</summary>
    public string Host { get; set; } = "";
    public int QrsPort { get; set; } = 4242;
    public int EnginePort { get; set; } = 4747;

    /// <summary>QMC'den export edilen client.pfx (private key içerir).</summary>
    public string ClientCertificatePath { get; set; } = "";
    /// <summary>pfx parolası. Tercihen ortam değişkeni ile verin: QSC_Qlik__ClientCertificatePassword</summary>
    public string? ClientCertificatePassword { get; set; }
    /// <summary>QMC'den export edilen root.cer (sunucu sertifikasını doğrulamak için).</summary>
    public string RootCertificatePath { get; set; } = "";
    /// <summary>Sunucu sertifikasındaki ad, Host ile eşleşmezse yine de kabul et (zincir yine root.cer ile doğrulanır).</summary>
    public bool AllowCertificateNameMismatch { get; set; } = false;

    public string UserDirectory { get; set; } = "INTERNAL";
    public string UserId { get; set; } = "sa_api";

    public int RequestTimeoutSeconds { get; set; } = 120;
    public int ExportTimeoutSeconds { get; set; } = 3600;
    public int EngineRequestTimeoutSeconds { get; set; } = 600;

    /// <summary>Sadece okuma çağrılarında (GET, export indirme, Engine bağlantısı açma) kullanılan retry sayısı.</summary>
    public int ReadRetryCount { get; set; } = 3;
    public int ReadRetryBaseDelaySeconds { get; set; } = 2;

    /// <summary>Senaryo A'da Engine'e hangi kimlikle bağlanılacağı: AppOwner (varsayılan) veya ServiceAccount.</summary>
    public EngineIdentityMode ScenarioAEngineIdentity { get; set; } = EngineIdentityMode.AppOwner;

    /// <summary>Sheet JSON yedeği için app'i veri yüklemeden (qNoData) aç. Senaryo A oturumunda ASLA kullanılmaz.</summary>
    public bool BackupOpenWithoutData { get; set; } = true;

    /// <summary>Her Engine bağlantısı için ayrı oturum (/identity/{guid}) aç; kullanıcıların açık oturumlarıyla paylaşma.</summary>
    public bool UseIsolatedEngineSession { get; set; } = true;
}

public enum EngineIdentityMode
{
    AppOwner,
    ServiceAccount
}

public sealed class ExcelSettings
{
    public string SheetName { get; set; } = "Sheet1";
    public string AppIdColumn { get; set; } = "AppId";
    public string SheetIdColumn { get; set; } = "SheetId";
    /// <summary>Başlık satırının aranacağı ilk N satır.</summary>
    public int HeaderSearchRows { get; set; } = 20;
}

public sealed class ProcessingSettings
{
    public string BackupFolder { get; set; } = "backups";
    public int MaxDegreeOfParallelism { get; set; } = 1;

    /// <summary>true ise Senaryo C (community sheet) önce unpublish edilip silinir. Varsayılan false.</summary>
    public bool AllowCommunitySheetDelete { get; set; } = false;

    /// <summary>Senaryo A .qvf yedeğini veri olmadan al (skipData=true). Varsayılan false: tam yedek.</summary>
    public bool ExportSkipData { get; set; } = false;

    /// <summary>Excel'deki SheetId neyle eşleşsin: Auto (önce engineObjectId, sonra QRS id), EngineObjectId, QrsId.</summary>
    public SheetIdMatchMode SheetIdMatch { get; set; } = SheetIdMatchMode.Auto;
}

public enum SheetIdMatchMode
{
    Auto,
    EngineObjectId,
    QrsId
}

public sealed class LoggingSettings
{
    public string LogFolder { get; set; } = "logs";
    public string MinimumLevel { get; set; } = "Information";
}
