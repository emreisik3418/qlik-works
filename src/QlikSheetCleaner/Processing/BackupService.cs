using System.Text.Json;
using System.Text.Json.Nodes;
using QlikSheetCleaner.Configuration;
using QlikSheetCleaner.Models;
using QlikSheetCleaner.Qlik;
using Serilog;

namespace QlikSheetCleaner.Processing;

/// <summary>
/// Senaryo A: QRS export ile .qvf yedeği.
/// Senaryo B/C: sheet sahibi adına açılmış Engine oturumunda GetFullPropertyTree → JSON.
/// </summary>
public sealed class BackupService
{
    public const int SheetBackupFormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly QrsClient _qrs;
    private readonly QlikSettings _qlik;
    private readonly ProcessingSettings _settings;
    private readonly ILogger _log = Log.ForContext<BackupService>();

    public BackupService(QrsClient qrs, QlikSettings qlik, ProcessingSettings settings)
    {
        _qrs = qrs;
        _qlik = qlik;
        _settings = settings;
    }

    private string AppFolder(Guid appId) =>
        Path.Combine(Path.GetFullPath(_settings.BackupFolder), appId.ToString("D"));

    /// <summary>App'in .qvf yedeğini alır: {yedek}/{AppId}/{AppId}_{zaman}.qvf</summary>
    public async Task<string> BackupAppAsync(QrsApp app, CancellationToken ct)
    {
        var folder = AppFolder(app.Id);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{app.Id:D}_{DateTime.Now:yyyyMMdd_HHmmss}.qvf");

        _log.Information("App yedeği alınıyor: {AppName} ({AppId}) → {File}", app.Name, app.Id, file);
        var bytes = await _qrs.ExportAppAsync(app.Id, file, _settings.ExportSkipData, ct);
        _log.Information("App yedeği alındı: {File} ({Size:n0} byte)", file, bytes);
        return file;
    }

    /// <summary>Sheet'in tam property tree'sini JSON'a yazar: {yedek}/{AppId}/{SheetId}.json</summary>
    public async Task<string> BackupSheetAsync(EngineSession session, QrsApp app, SheetRow row, CancellationToken ct)
    {
        var engineId = row.EngineObjectId ?? row.SheetId;

        var obj = await session.GetObjectAsync(engineId, ct)
                  ?? throw new InvalidOperationException($"Sheet Engine'de bulunamadı ({engineId}) — sahibi ({session.Identity}) adına açılan oturumda görünmüyor");

        if (!string.Equals(obj.GenericType, "sheet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Engine nesnesi sheet değil (qGenericType={obj.GenericType})");

        var tree = await session.GetFullPropertyTreeAsync(obj.Handle, ct);

        // Doğrulama: kök qProperty.qInfo.qType = sheet olmalı
        if (!tree.TryGetProperty("qProperty", out var prop) ||
            !prop.TryGetProperty("qInfo", out var info) ||
            !string.Equals(info.GetProperty("qType").GetString(), "sheet", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GetFullPropertyTree beklenen sheet yapısını döndürmedi");
        }

        var document = new JsonObject
        {
            ["backupFormatVersion"] = SheetBackupFormatVersion,
            ["createdAt"] = DateTimeOffset.Now.ToString("O"),
            ["server"] = _qlik.Host,
            ["appId"] = app.Id.ToString("D"),
            ["appName"] = app.Name,
            ["engineObjectId"] = engineId,
            ["qrsObjectId"] = row.QrsObjectId?.ToString("D"),
            ["title"] = row.SheetTitle,
            ["owner"] = new JsonObject
            {
                ["userDirectory"] = row.Owner?.UserDirectory,
                ["userId"] = row.Owner?.UserId
            },
            ["published"] = row.SheetPublished,
            ["approved"] = row.SheetApproved,
            ["scenario"] = row.Scenario.ToString(),
            // GetFullPropertyTree çıktısı (qPropEntry) olduğu gibi:
            ["propertyTree"] = JsonNode.Parse(tree.GetRawText())
        };

        var folder = AppFolder(app.Id);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{engineId}.json");

        // Önceki çalıştırmadan kalan yedek varsa ezme; zaman damgalı olarak kenara al
        if (File.Exists(file))
            File.Move(file, Path.Combine(folder, $"{engineId}.{DateTime.Now:yyyyMMddHHmmss}.json"));

        var temp = file + ".partial";
        await File.WriteAllTextAsync(temp, document.ToJsonString(WriteOptions), ct);
        if (new FileInfo(temp).Length == 0) throw new IOException("Yedek dosyası boş yazıldı");
        File.Move(temp, file, overwrite: true);

        _log.Information("Sheet yedeği alındı: {Title} ({SheetId}) → {File}", row.SheetTitle, engineId, file);
        return file;
    }
}
