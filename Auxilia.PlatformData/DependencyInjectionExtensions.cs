using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.PlatformData;

public static class DependencyInjectionExtensions
{
    /// <summary>Registers an <see cref="IDataAccess{TEntity}"/> using the configured backend.</summary>
    public static IServiceCollection AddPlatformEntity<TEntity>(
        this IServiceCollection services, PlatformDataSettings settings)
        where TEntity : class, IEntity
        => settings.Backend switch
        {
            PlatformDataBackend.InMemory => services.AddInMemoryStorage<TEntity>(),
            PlatformDataBackend.Json => services.AddJsonStorage(new JsonStorageSettings<TEntity>
            {
                StorageFilePath = Path.Combine(settings.JsonDirectory, $"{typeof(TEntity).Name}.json")
            }),
            PlatformDataBackend.MongoDb => services.AddMongoDbStorage(new MongoDbSettings<TEntity>
            {
                ConnectionString = settings.MongoConnectionString,
                DatabaseName = settings.MongoDatabaseName
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Backend, "Unknown platform data backend.")
        };

    /// <summary>
    /// Registers the <see cref="ISettingsProtector"/>: AES-GCM when a protection key is
    /// configured, pass-through otherwise (trusted-operator dev only).
    /// </summary>
    public static IServiceCollection AddSettingsProtection(
        this IServiceCollection services, PlatformDataSettings settings)
    {
        services.AddSingleton<ISettingsProtector>(_ =>
            string.IsNullOrWhiteSpace(settings.ProtectionKeyBase64)
                ? new NullSettingsProtector()
                : new AesGcmSettingsProtector(Convert.FromBase64String(settings.ProtectionKeyBase64)));
        return services;
    }
}
