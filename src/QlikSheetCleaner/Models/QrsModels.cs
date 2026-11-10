using System.Text.Json.Serialization;

namespace QlikSheetCleaner.Models;

public sealed class QrsUser
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    [JsonPropertyName("userDirectory")] public string UserDirectory { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }

    public override string ToString() => $"{UserDirectory}\\{UserId}";
}

public sealed class QrsStream
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class QrsApp
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("published")] public bool Published { get; set; }
    [JsonPropertyName("owner")] public QrsUser? Owner { get; set; }
    [JsonPropertyName("stream")] public QrsStream? Stream { get; set; }
}

public sealed class QrsAppRef
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class QrsAppObject
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("engineObjectId")] public string EngineObjectId { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("objectType")] public string ObjectType { get; set; } = "";
    [JsonPropertyName("published")] public bool Published { get; set; }
    [JsonPropertyName("approved")] public bool Approved { get; set; }
    [JsonPropertyName("owner")] public QrsUser? Owner { get; set; }
    [JsonPropertyName("app")] public QrsAppRef? App { get; set; }
}

public sealed class QrsExportTicket
{
    [JsonPropertyName("exportToken")] public string? ExportToken { get; set; }
    [JsonPropertyName("appId")] public Guid AppId { get; set; }
    [JsonPropertyName("downloadPath")] public string? DownloadPath { get; set; }
}
