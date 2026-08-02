using System.Security.Cryptography;
using System.Text;
using Auxilia.Governance.IdentityImport;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Settings;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MongoDb;

namespace Auxilia.SystemTestSuite.IdentityImport;

/// <summary>
/// Environment for identity-import system tests (#23): a real OpenLDAP server with seeded
/// users plus MongoDB for durable platform state. The Governance services run IN-PROCESS in
/// the test (mirroring how EndToEnd seeds principals Mongo-direct) — no service container
/// is needed because identity import is pure Governance behavior with no bus interaction.
///
/// Image choice: osixia/openldap:1.5.0 over bitnami/openldap. Bitnami seeds users via
/// LDAP_USERS env vars, but Bitnami's free Docker Hub catalog was discontinued in 2025 making
/// the image unreliable to pull; osixia is a stable pinned tag and seeding works just as
/// simply by copying a bootstrap LDIF into the image via Testcontainers' resource mapping
/// (no host bind mount, which is brittle on Windows hosts) plus the --copy-service flag.
/// </summary>
[SetUpFixture]
public class IdentityImportEnvironment
{
    private const string LdapImage = "osixia/openldap:1.5.0";
    private const int LdapPort = 389;

    internal const string AdminPassword = "admin-ldap-pw";
    internal const string BindDn = "cn=admin,dc=auxilia,dc=test";
    internal const string PeopleBaseDn = "ou=people,dc=auxilia,dc=test";

    /// <summary>Seeded users: ada + grace are reviewers (ou attribute), linus is not.</summary>
    private const string SeedLdif =
        """
        dn: ou=people,dc=auxilia,dc=test
        objectClass: organizationalUnit
        ou: people

        dn: uid=ada,ou=people,dc=auxilia,dc=test
        objectClass: inetOrgPerson
        uid: ada
        cn: Ada Lovelace
        sn: Lovelace
        ou: reviewers

        dn: uid=grace,ou=people,dc=auxilia,dc=test
        objectClass: inetOrgPerson
        uid: grace
        cn: Grace Hopper
        sn: Hopper
        ou: reviewers

        dn: uid=linus,ou=people,dc=auxilia,dc=test
        objectClass: inetOrgPerson
        uid: linus
        cn: Linus Torvalds
        sn: Torvalds
        """;

    private IContainer _ldap = null!;
    private MongoDbContainer _mongoDb = null!;
    private static ServiceProvider _provider = null!;

    public static string LdapHost { get; private set; } = null!;
    public static int MappedLdapPort { get; private set; }

    /// <summary>Governance + platform-data services over the shared Mongo, built once per environment.</summary>
    public static IServiceProvider Services => _provider;

    /// <summary>Plain (unprotected) connector settings for the seeded directory.</summary>
    public static Dictionary<string, string> LdapSettings() => new()
    {
        ["Host"] = LdapHost,
        ["Port"] = MappedLdapPort.ToString(),
        ["UseSsl"] = "false",
        ["BindDn"] = BindDn,
        ["BindPassword"] = AdminPassword,
        ["BaseDn"] = PeopleBaseDn,
        ["UserFilter"] = "(objectClass=inetOrgPerson)",
        ["UsernameAttribute"] = "uid",
        ["DisplayNameAttribute"] = "cn",
        // The seeded directory has no memberOf overlay; the users carry their team in the
        // standard "ou" attribute — exactly what the configurable group attribute is for.
        ["GroupAttribute"] = "ou"
    };

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _ldap = new ContainerBuilder(LdapImage)
            .WithEnvironment("LDAP_ORGANISATION", "Auxilia Test")
            .WithEnvironment("LDAP_DOMAIN", "auxilia.test")
            .WithEnvironment("LDAP_ADMIN_PASSWORD", AdminPassword)
            .WithEnvironment("LDAP_TLS", "false")
            // Bootstrap LDIFs are applied from this directory when --copy-service is passed.
            .WithResourceMapping(Encoding.UTF8.GetBytes(SeedLdif),
                "/container/service/slapd/assets/config/bootstrap/ldif/custom/50-seed-users.ldif")
            .WithCommand("--copy-service")
            .WithPortBinding(LdapPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(LdapPort))
            .Build();
        _mongoDb = new MongoDbBuilder("mongo:8.0")
            .WithUsername(string.Empty).WithPassword(string.Empty) // no auth — test only
            .Build();
        await Task.WhenAll(_ldap.StartAsync(), _mongoDb.StartAsync());

        LdapHost = _ldap.Hostname;
        MappedLdapPort = _ldap.GetMappedPublicPort(LdapPort);

        _provider = BuildServices(_mongoDb.GetConnectionString());

        await WaitUntilDirectoryServesSeededUsersAsync();
    }

    /// <summary>
    /// The internal-port wait can pass while slapd still binds localhost during bootstrap —
    /// poll a real search through the production connector until the seeded users appear.
    /// </summary>
    private static async Task WaitUntilDirectoryServesSeededUsersAsync()
    {
        var connector = new LdapIdentityImportConnector();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        ConnectorTestResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await connector.TestConnectionAsync(LdapSettings());
            if (last.Success && last.Message.Contains("3 user(s)"))
                return;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException(
            $"LDAP container did not serve the 3 seeded users in time (last result: {last?.Message ?? "none"}).");
    }

    private static ServiceProvider BuildServices(string mongoConnectionString)
    {
        var services = new ServiceCollection();
        AddEntity<PrincipalRecord>(services, mongoConnectionString);
        AddEntity<RoleAssignmentRecord>(services, mongoConnectionString);
        AddEntity<CredentialRecord>(services, mongoConnectionString);
        AddEntity<AuditRecord>(services, mongoConnectionString);
        AddEntity<IdentitySourceRecord>(services, mongoConnectionString);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuditLog>();
        // Real encryption at rest, exactly like a production host with a configured key.
        services.AddSingleton<ISettingsProtector>(
            new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32)));
        services.AddSingleton<IIdentityImportConnector, LdapIdentityImportConnector>();
        services.AddSingleton<IIdentityImportConnector, CsvIdentityImportConnector>();
        services.AddSingleton<IdentityImportService>();
        return services.BuildServiceProvider();

        static void AddEntity<TEntity>(IServiceCollection services, string connectionString)
            where TEntity : class, IEntity
            => services.AddMongoDbStorage(new MongoDbSettings<TEntity>
            {
                ConnectionString = connectionString,
                DatabaseName = "Auxilia"
            });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        if (_ldap is not null)
            await _ldap.DisposeAsync();
        if (_mongoDb is not null)
            await _mongoDb.DisposeAsync();
    }
}
