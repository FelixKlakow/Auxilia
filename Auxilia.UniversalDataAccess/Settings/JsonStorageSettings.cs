namespace Auxilia.UniversalDataAccess.Settings;

public class JsonStorageSettings<TEntity> where TEntity : IEntity
{
    /// <summary>
    /// If true, the data will only be saved to the JSON file when the data access instance is disposed. If false, the data will be saved immediately after any change. Setting this to true can improve performance by reducing file I/O operations through caching, but it also increases the risk of data loss if the application crashes before disposal.
    /// </summary>
    public bool OnlySaveOnDispose { get; init; } = false;

    /// <summary>
    /// The directory where JSON files will be stored. Directory structure and file will be created if they're missing 
    /// </summary>
    public string StorageFilePath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Auxilia", "DataAccess", $"{nameof(TEntity)}.json");
}