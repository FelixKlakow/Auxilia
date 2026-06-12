namespace Auxilia.PlatformData;

public enum PlatformDataBackend
{
    /// <summary>Volatile, per-process — tests only.</summary>
    InMemory,

    /// <summary>JSON files on local disk — dev and single-node deployments (default).</summary>
    Json,

    /// <summary>MongoDB — production; shared consistently across service replicas.</summary>
    MongoDb
}

/// <summary>Storage configuration shared by all durable platform entities.</summary>
public sealed class PlatformDataSettings
{
    public PlatformDataBackend Backend { get; set; } = PlatformDataBackend.Json;

    /// <summary>Directory for JSON-backend files; one file per entity type.</summary>
    public string JsonDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Auxilia", "PlatformData");

    public string MongoConnectionString { get; set; } = "mongodb://localhost:27017";

    public string MongoDatabaseName { get; set; } = "Auxilia";

    /// <summary>
    /// Base64 AES key (16/24/32 bytes) used to protect secrets inside persisted entities.
    /// When empty, values are stored unprotected — acceptable only for trusted-operator dev setups.
    /// </summary>
    public string? ProtectionKeyBase64 { get; set; }
}
