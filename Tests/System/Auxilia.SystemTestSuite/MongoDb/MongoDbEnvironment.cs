using Testcontainers.MongoDb;

namespace Auxilia.SystemTestSuite.MongoDb;

/// <summary>
/// Shared environment for MongoDB data-access system tests.
/// Spins up a single MongoDB container once per test run in this namespace.
/// </summary>
[SetUpFixture]
public class MongoDbEnvironment
{
    private MongoDbContainer _mongoDb = null!;

    /// <summary>The connection string for the running MongoDB container.</summary>
    public static string ConnectionString { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mongoDb = new MongoDbBuilder("mongo:8.0")
            .Build();

        await _mongoDb.StartAsync();

        ConnectionString = _mongoDb.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_mongoDb is not null)
        {
            await _mongoDb.StopAsync();
            await _mongoDb.DisposeAsync();
        }
    }
}


