using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.UniversalDataAccess.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.UniversalDataAccess;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddJsonStorage<TEntity>(this IServiceCollection services, JsonStorageSettings<TEntity> settings) where TEntity : IEntity
    {
        services.AddSingleton<IDataAccess<TEntity>, JsonFileDataAccess<TEntity>>();
        services.AddSingleton(settings);
        return services;
    }

    public static IServiceCollection AddJsonStorage<TEntity>(this IServiceCollection services)
        where TEntity : IEntity => services.AddJsonStorage<TEntity>(new JsonStorageSettings<TEntity>());
    
    public static IServiceCollection AddInMemoryStorage<TEntity>(this IServiceCollection services) where TEntity : IEntity
    {
        services.AddSingleton<IDataAccess<TEntity>, InMemoryDataAccess<TEntity>>();
        return services;
    }

    public static IServiceCollection AddMongoDbStorage<TEntity>(
        this IServiceCollection services,
        MongoDbSettings<TEntity> settings)
        where TEntity : class, IEntity
    {
        services.AddSingleton(settings);
        services.AddDbContextFactory<MongoDbContext<TEntity>>(options =>
            options.UseMongoDB(settings.ConnectionString, settings.DatabaseName));
        services.AddSingleton<IDataAccess<TEntity>, MongoDbEfDataAccess<TEntity>>();
        return services;
    }

    public static IServiceCollection AddMongoDbStorage<TEntity>(this IServiceCollection services)
        where TEntity : class, IEntity
        => services.AddMongoDbStorage(new MongoDbSettings<TEntity>());
}