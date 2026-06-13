using System.Diagnostics;
using Auxilia.Governance;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.SystemTestSuite.WorkflowDispatch;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Settings;
using Auxilia.Workflows.Messaging.Messages;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.EndToEnd;

/// <summary>
/// Environment for the goal-v1 acceptance test (docs/goal-v1.md): the WHOLE platform on one
/// Docker network — GreenMail (mail trigger + reply write-back), RabbitMQ, MongoDB (shared
/// durable platform state), one Steering Instance (Mongo-backed, run-output shared with the
/// host), and one Backend Service hosting the email task-source adapter. The Code Review
/// workflow runs from its baked image; its work-items slot is the REAL email slot provider,
/// all other slots are the happy fakes.
///
/// Run-output decision: the Docker daemon resolves bind sources on the HOST, so the SI
/// container gets a host temp directory mounted at /run-output and passes the host view to
/// workflow launches via the production WorkflowDispatcher__RunOutputHostDirectory setting —
/// both containers then share the same physical directory and artifact persistence works.
/// </summary>
[SetUpFixture]
public class EndToEndEnvironment
{
    internal const string WorkflowType        = "pull-request-code-review";
    internal const string WorkflowImageName   = "auxilia-code-review-workflow:system-test";
    internal const string WorkflowPackageUri  = "docker://" + WorkflowImageName;
    internal const string BackendImageName    = "auxilia-backendservice:system-test";
    internal const string CommandQueue        = "workflow.run-commands-e2e";
    internal const string AdapterMailbox      = "workflows@localhost";
    internal const string MailboxPassword     = "pw";

    private const string RabbitMqAlias        = "rabbitmq";
    private const string MongoAlias           = "mongo";
    private const string GreenMailAlias       = "greenmail";
    private const string GreenMailImage       = "greenmail/standalone:2.1.3";
    private const int    ImapPort             = 3143;
    private const int    SmtpPort             = 3025;
    private const string DockerSocket         = "/var/run/docker.sock";
    private const string ContainerPluginsDir  = "/slot-plugins";
    private const string ContainerRunOutput   = "/run-output";

    private static readonly string NetworkName =
        $"auxilia-e2e-{Guid.NewGuid():N}".Substring(0, 30);

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private INetwork          _network    = null!;
    private RabbitMqContainer _rabbitMq   = null!;
    private MongoDbContainer  _mongoDb    = null!;
    private IContainer        _greenMail  = null!;
    private string            _publishDir = null!;
    private string            _runOutputDir = null!;

    public static IContainer SteeringInstance { get; private set; } = null!;
    public static IContainer Backend          { get; private set; } = null!;
    public static IMessageBusClient MessageBusClient { get; private set; } = null!;
    public static string MongoConnectionString { get; private set; } = null!;
    public static string MailHost   { get; private set; } = null!;
    public static int    MappedImap { get; private set; }
    public static int    MappedSmtp { get; private set; }
    /// <summary>Service principal (role: User) on whose behalf mail-triggered runs dispatch.</summary>
    public static Guid RunAsPrincipalId { get; private set; }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _publishDir   = Path.Combine(Path.GetTempPath(), $"auxilia-e2e-plugins-{Guid.NewGuid():N}");
        _runOutputDir = Path.Combine(Path.GetTempPath(), $"auxilia-e2e-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_publishDir);
        Directory.CreateDirectory(_runOutputDir);

        // Host-side publishes share a dependency graph — run sequentially to avoid obj/
        // contention (CS2012). Both slot providers land in ONE plugins directory.
        await PublishProjectAsync(
            "Auxilia.FakeSlots.CodeReview.Happy/Auxilia.FakeSlots.CodeReview.Happy.csproj",
            _publishDir);
        await PublishProjectAsync(
            "Auxilia.Slots.Email/Auxilia.Slots.Email.csproj",
            _publishDir);

        // Sequential on purpose: parallel docker builds have wedged Docker Desktop daemons
        // (see FailoverEnvironment); the .prebuilt-images marker skips them locally anyway.
        await WorkflowDispatchEnvironment.BuildImageAsync(
            WorkflowDispatchEnvironment.SteeringImageName, "Source/Auxilia.SteeringInstance/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            BackendImageName, "Source/Auxilia.BackendService/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            WorkflowImageName, "Source/Auxilia.CodeReview.Workflow/Dockerfile");

        _network = new NetworkBuilder().WithName(NetworkName).Build();
        await _network.CreateAsync();

        _greenMail = new ContainerBuilder(GreenMailImage)
            .WithNetwork(_network)
            .WithNetworkAliases(GreenMailAlias)
            // hostname=0.0.0.0 is essential: without it GreenMail binds to the container's
            // loopback only and neither mapped host ports nor network peers reach it.
            .WithEnvironment("GREENMAIL_OPTS",
                "-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 " +
                "-Dgreenmail.auth.disabled -Dgreenmail.verbose")
            .WithPortBinding(ImapPort, assignRandomHostPort: true)
            .WithPortBinding(SmtpPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(ImapPort))
            .Build();

        _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management")
            .WithUsername("guest").WithPassword("guest")
            .WithNetwork(_network).WithNetworkAliases(RabbitMqAlias)
            .Build();
        _mongoDb = new MongoDbBuilder("mongo:8.0")
            .WithNetwork(_network).WithNetworkAliases(MongoAlias)
            .WithUsername(string.Empty).WithPassword(string.Empty) // no auth — test only
            .Build();
        await Task.WhenAll(_greenMail.StartAsync(), _rabbitMq.StartAsync(), _mongoDb.StartAsync());

        MongoConnectionString = _mongoDb.GetConnectionString();
        MailHost   = _greenMail.Hostname;
        MappedImap = _greenMail.GetMappedPublicPort(ImapPort);
        MappedSmtp = _greenMail.GetMappedPublicPort(SmtpPort);

        // The run-as principal must exist BEFORE the Backend Service starts dispatching:
        // seed it directly into the shared Mongo over the mapped port.
        await using (var provider = BuildPlatformDataProvider())
        {
            var directory = provider.GetRequiredService<PrincipalDirectory>();
            var (principal, _) = await directory.CreateApiKeyPrincipalAsync("E2E Mail Trigger", "Service");
            await directory.AssignRoleAsync(principal.Id, BuiltInRoles.User);
            RunAsPrincipalId = principal.Id;
        }

        SteeringInstance = new ContainerBuilder(WorkflowDispatchEnvironment.SteeringImageName)
            .WithNetwork(_network)
            .WithBindMount(DockerSocket, DockerSocket)
            .WithBindMount(_publishDir, ContainerPluginsDir)
            // Shared run-output root: the SI sees /run-output, the Docker daemon (and thus
            // workflow containers) see the same directory via its HOST path.
            .WithBindMount(_runOutputDir, ContainerRunOutput)
            .WithEnvironment("RabbitMq__Host",     RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port",     "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("WorkflowLauncher__NetworkName",      NetworkName)
            .WithEnvironment("WorkflowLauncher__RabbitMqHost",     RabbitMqAlias)
            .WithEnvironment("WorkflowLauncher__RabbitMqPort",     "5672")
            .WithEnvironment("WorkflowLauncher__RabbitMqUserName", "guest")
            .WithEnvironment("WorkflowLauncher__RabbitMqPassword", "guest")
            .WithEnvironment("WorkflowLauncher__ExtraEnvironmentVariables__AUXILIA_DEVELOPER_MODE", "1")
            .WithEnvironment("WorkflowDispatcher__CommandQueueName",        CommandQueue)
            .WithEnvironment("WorkflowDispatcher__RegistrationQueueName",   "workflow-registration-e2e")
            .WithEnvironment("WorkflowDispatcher__AnnouncementQueueName",   "workflow.announcements-e2e")
            .WithEnvironment("WorkflowDispatcher__SlotActivationQueueName", "workflow-slot-activation-e2e")
            .WithEnvironment("WorkflowDispatcher__RunOutputDirectory",      ContainerRunOutput)
            .WithEnvironment("WorkflowDispatcher__RunOutputHostDirectory",  _runOutputDir)
            .WithEnvironment("PlatformData__Backend",               "MongoDb")
            .WithEnvironment("PlatformData__MongoConnectionString", $"mongodb://{MongoAlias}:27017")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("WorkflowDispatcher started")
                .UntilMessageIsLogged("SlotConfigurationSeedHandler started"))
            .Build();

        Backend = new ContainerBuilder(BackendImageName)
            .WithNetwork(_network)
            // The dashboard is host-reachable so an operator (or Scripts/Run-SystemTests.ps1)
            // can watch a run live in the browser while the test executes.
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithEnvironment("RabbitMq__Host",     RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port",     "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("PlatformData__Backend",               "MongoDb")
            .WithEnvironment("PlatformData__MongoConnectionString", $"mongodb://{MongoAlias}:27017")
            .WithEnvironment("PlatformHost__CommandQueueName",      CommandQueue)
            .WithEnvironment("Governance__BootstrapAdminUsername",  "admin")
            .WithEnvironment("Governance__BootstrapAdminPassword",  "e2e-admin-pw")
            .WithEnvironment("EmailTaskSource__Enabled",             "true")
            .WithEnvironment("EmailTaskSource__ImapHost",            GreenMailAlias)
            .WithEnvironment("EmailTaskSource__ImapPort",            ImapPort.ToString())
            .WithEnvironment("EmailTaskSource__UseSsl",              "false")
            .WithEnvironment("EmailTaskSource__Username",            AdapterMailbox)
            .WithEnvironment("EmailTaskSource__Password",            MailboxPassword)
            .WithEnvironment("EmailTaskSource__SmtpHost",            GreenMailAlias)
            .WithEnvironment("EmailTaskSource__SmtpPort",            SmtpPort.ToString())
            .WithEnvironment("EmailTaskSource__Folder",              "INBOX")
            .WithEnvironment("EmailTaskSource__PollIntervalSeconds", "3")
            .WithEnvironment("EmailTaskSource__WorkflowType",        WorkflowType)
            .WithEnvironment("EmailTaskSource__WorkflowPackageUri",  WorkflowPackageUri)
            .WithEnvironment("EmailTaskSource__CommandQueueName",    CommandQueue)
            .WithEnvironment("EmailTaskSource__RunAsPrincipalId",    RunAsPrincipalId.ToString("D"))
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("HeartbeatMonitor started")
                .UntilMessageIsLogged("Email task source started"))
            .Build();

        await Task.WhenAll(SteeringInstance.StartAsync(), Backend.StartAsync());

        MessageBusClient = await RabbitMqClient.CreateAsync(
            _rabbitMq.Hostname, _rabbitMq.GetMappedPublicPort(5672));

        // Seed slot providers and configurations via the SI's per-instance seed queues
        // (targeted delivery — no fanout collisions with other environments).
        var seedBase = CommandQueue + "-slot-seed";
        await MessageBusClient.PublishAsync(seedBase + ".register",
            new RegisterSlotProviderCommand(
                "fake-code-review-happy",
                $"{ContainerPluginsDir}/Auxilia.FakeSlots.CodeReview.Happy.slothandler.dll"));
        // The published manifest sidecar is the source of truth for the email provider's
        // setting descriptors — registration carries them into the provider record (#19).
        var emailManifest = System.Text.Json.JsonSerializer.Deserialize<Auxilia.Workflows.PluginManifest>(
            await File.ReadAllTextAsync(Path.Combine(_publishDir, "Auxilia.Slots.Email.slothandler.manifest.json")))!;
        await MessageBusClient.PublishAsync(seedBase + ".register",
            new RegisterSlotProviderCommand(
                "email-work-items",
                $"{ContainerPluginsDir}/Auxilia.Slots.Email.slothandler.dll",
                emailManifest.Settings));

        foreach (var slotName in new[] { "repository", "pull-request", "primary-reviewer",
                                          "secondary-reviewer", "workflow-bootstrap" })
            await MessageBusClient.PublishAsync(seedBase + ".upsert",
                new UpsertSlotConfigurationCommand(
                    WorkflowType, slotName, "fake-code-review-happy",
                    new Dictionary<string, string>()));

        // The work-items slot is the REAL email provider: the triggering mail is the work
        // item, write-back comments become mail replies (container-internal endpoints).
        await MessageBusClient.PublishAsync(seedBase + ".upsert",
            new UpsertSlotConfigurationCommand(
                WorkflowType, "work-items", "email-work-items",
                new Dictionary<string, string>
                {
                    ["ImapHost"] = GreenMailAlias,
                    ["ImapPort"] = ImapPort.ToString(),
                    ["UseSsl"]   = "false",
                    ["Username"] = AdapterMailbox,
                    ["Password"] = MailboxPassword,
                    ["SmtpHost"] = GreenMailAlias,
                    ["SmtpPort"] = SmtpPort.ToString(),
                    ["Folder"]   = "INBOX"
                }));

        await SeedEmailSlotDependenciesAsync(seedBase);

        await Task.Delay(TimeSpan.FromMilliseconds(500)); // seed propagation window
    }

    /// <summary>
    /// The platform copies exactly the (dll, manifest) pair of every provider referenced by
    /// the workflow's slot configurations into the workflow container — no dependency
    /// closure (the fake providers never needed one; their dependencies coincide with the
    /// workflow image's own assemblies). The REAL email provider additionally needs its mail
    /// stack next to the handler DLL, so each dependency assembly from the publish output is
    /// registered as an auxiliary provider wired to an unused slot name; the dispatcher then
    /// ships the file with the launch and plugin discovery ignores it (not *.slothandler.dll).
    /// </summary>
    private async Task SeedEmailSlotDependenciesAsync(string seedBase)
    {
        string[] dependencyAssemblies =
        [
            "Auxilia.Adapters.Email.dll",
            "MailKit.dll",
            "MimeKit.dll",
            "BouncyCastle.Cryptography.dll",
            "System.Formats.Asn1.dll"
        ];

        var index = 0;
        foreach (var assembly in dependencyAssemblies)
        {
            if (!File.Exists(Path.Combine(_publishDir, assembly)))
                continue;

            // The dispatcher always pairs a DLL with "<basename>.manifest.json" — create an
            // inert stub so the file copy succeeds (never loaded as a plugin in the container).
            var providerType = $"e2e-dep-{index}";
            var stubName = Path.GetFileNameWithoutExtension(assembly) + ".manifest.json";
            await File.WriteAllTextAsync(
                Path.Combine(_publishDir, stubName),
                $$"""{ "ProviderType": "{{providerType}}", "ContentHashBase64": "", "SignatureBase64": "", "PublicKeyBase64": "" }""");

            await MessageBusClient.PublishAsync(seedBase + ".register",
                new RegisterSlotProviderCommand(providerType, $"{ContainerPluginsDir}/{assembly}"));
            await MessageBusClient.PublishAsync(seedBase + ".upsert",
                new UpsertSlotConfigurationCommand(
                    WorkflowType, $"e2e-dependency-{index}", providerType,
                    new Dictionary<string, string>()));
            index++;
        }
    }

    /// <summary>
    /// Direct access to the shared platform state over the mapped Mongo port — the same
    /// database (and DatabaseName "Auxilia") the services use via AddPlatformEntity.
    /// </summary>
    public static ServiceProvider BuildPlatformDataProvider()
    {
        var services = new ServiceCollection();
        AddEntity<PrincipalRecord>(services);
        AddEntity<RoleAssignmentRecord>(services);
        AddEntity<CredentialRecord>(services);
        AddEntity<AuditRecord>(services);
        AddEntity<ArtifactRecord>(services);
        AddEntity<ViewDataRecord>(services);
        AddEntity<WorkflowInstanceRecord>(services);
        AddEntity<SlotProviderRecord>(services);
        AddEntity<ProviderCatalogRecord>(services);
        AddEntity<WorkflowConfigurationRecord>(services);
        AddEntity<ScheduledTriggerRecord>(services);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuditLog>();
        services.AddSingleton<PrincipalDirectory>();
        return services.BuildServiceProvider();

        static void AddEntity<TEntity>(IServiceCollection services)
            where TEntity : class, IEntity
            => services.AddMongoDbStorage(new MongoDbSettings<TEntity>
            {
                ConnectionString = MongoConnectionString,
                DatabaseName = "Auxilia"
            });
    }

    /// <summary>Tail of a container's combined stdout/stderr for assertion diagnostics.</summary>
    public static async Task<string> LogTailAsync(IContainer container, int maxChars = 4000)
    {
        var (stdout, stderr) = await container.GetLogsAsync();
        var logs = stdout + stderr;
        return logs.Length <= maxChars ? logs : logs[^maxChars..];
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (MessageBusClient is IAsyncDisposable d) await d.DisposeAsync();
        await Backend.DisposeAsync();
        await SteeringInstance.DisposeAsync();
        await _greenMail.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _mongoDb.DisposeAsync();
        await _network.DisposeAsync();
        TryDelete(_publishDir);
        TryDelete(_runOutputDir);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort — leftover temp directories are harmless.
        }
    }

    private static async Task PublishProjectAsync(string projectRelativePath, string outputDir)
    {
        // -nodeReuse:false + UseSharedCompilation=false: persistent MSBuild/Roslyn worker
        // processes inherit the redirected stdout/stderr pipes; with node reuse the workers
        // outlive the publish and ReadToEndAsync stalls until their idle timeout (~15 min).
        var psi = new ProcessStartInfo("dotnet",
            $"publish {projectRelativePath} -c Release -o {outputDir} --no-self-contained -nodeReuse:false -p:UseSharedCompilation=false")
        {
            WorkingDirectory       = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start dotnet publish.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"dotnet publish failed for {projectRelativePath} (exit {process.ExitCode}):\n{stdout}\n{stderr}");
    }
}
