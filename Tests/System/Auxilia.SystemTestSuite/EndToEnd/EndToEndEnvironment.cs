using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.PlatformData.Entities;
using Auxilia.SystemTestSuite.WorkflowDispatch;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Settings;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.EndToEnd;

/// <summary>
/// Whole-platform acceptance environment, retargeted for the BackendService retirement (Phase 4).
/// The BackendService host is gone; the mail path now runs on the split platform:
/// <list type="bullet">
///   <item><b>Core.Api</b> — the control plane + Run API. Owns the mail-review run configuration
///         (with full slot bindings so it resolves every slot JIT), identity/RBAC (the run-as
///         principal + the trigger-host service key), and audit. Its own (in-memory) store, never shared.</item>
///   <item><b>TriggerHost</b> — the workflow-domain library's reference host (ex-WorkflowStudio):
///         hosts the email task-source adapter, reads the mailbox trigger + slot-instance credential
///         from its own Mongo DB, and dispatches runs through the Core Run API on behalf of the
///         trigger's principal — never a raw bus command, no bus dependency at all.</item>
///   <item><b>Core.Runner</b> — Mongo-backed execution plane; consumes the run-command queue, resolves
///         every slot just-in-time from the Core (no local credential store), launches the workflow.</item>
///   <item>GreenMail (mail trigger + reply write-back), RabbitMQ, MongoDB — unchanged topology.</item>
/// </list>
///
/// CI ASSUMPTIONS (validated only by the Docker run in CI; not runnable locally):
///  - The Core resolves the code-review workflow's fake slots from the configuration's inline
///    <see cref="SlotBinding"/>s (ProviderType + settings, no connector) — the resolver's inline path.
///  - The runner loads each provider's plugin DLL from the mounted <c>/slot-plugins</c> directory via
///    <c>WorkflowLauncher:SlotPackages:&lt;providerType&gt;</c>, and the real email provider's mail-stack
///    dependencies (MailKit/MimeKit/...) are copied alongside it into the workflow container.
///  - The exact declared-slot set of <c>pull-request-code-review</c> matches the bindings seeded below.
///  - Audit for the run now lives in the Core.Api audit store (queried over <c>/api/audit</c>), not the
///    runner's Mongo — the test reads it there.
/// </summary>
[SetUpFixture]
public class EndToEndEnvironment
{
    internal const string WorkflowType        = "pull-request-code-review";
    internal const string WorkflowImageName   = "auxilia-code-review-workflow:system-test";
    internal const string WorkflowPackageUri  = "docker://" + WorkflowImageName;
    public   const string ClaudeWorkflowType       = "claude-code";
    public   const string ClaudeWorkflowImageName  = "auxilia-claude-code-workflow:system-test";
    public   const string ClaudeWorkflowPackageUri = "docker://" + ClaudeWorkflowImageName;
    /// <summary>In-image stand-in CLI — system tests never call real AI (cost rule).</summary>
    public   const string ClaudeStubCliPath        = "/usr/local/bin/claude-stub";
    public   const string ImplementationWorkflowType       = "implementation";
    public   const string ImplementationWorkflowImageName  = "auxilia-implementation-workflow:system-test";
    public   const string ImplementationWorkflowPackageUri = "docker://" + ImplementationWorkflowImageName;
    /// <summary>In-image DRIVEN stand-in author CLI (tmux + hook Stop protocol; never real AI).</summary>
    public   const string DrivenStubCliPath        = "/usr/local/bin/driven-stub";

    // Authenticated git server the implementation run's per-run repository is cloned from
    // (same image + credentials as the CoreApiDispatch repository scenario).
    internal const string GitServerImageName = "auxilia-git-server:system-test";
    internal const string GitServerAlias     = "gitserver";
    public   const string RepositoryCloneUrl = "http://" + GitServerAlias + "/git/test.git";
    public   const string GitUsername        = "builduser";
    public   const string GitPassword        = "the-pat";

    internal const string CoreApiImageName      = "auxilia-core-api:system-test";
    internal const string TriggerHostImageName  = "auxilia-trigger-host:system-test";
    internal const string AdminConsoleImageName = "auxilia-admin-console:system-test";
    internal const string BootstrapApiKey  = "aux-system-test-key-e2e-0123456789abcd";

    public   const string CommandQueue        = "workflow.run-commands-e2e";
    /// <summary>The Core configuration the mailbox trigger dispatches.</summary>
    public   const string MailReviewConfigurationName = "mail-review";

    /// <summary>Retained for DevStand compatibility (unused by the retargeted mail path).</summary>
    public static int SessionStubSeconds { get; set; } = 6;
    /// <summary>Retained for DevStand compatibility.</summary>
    public static bool PresentationMode { get; set; }
    /// <summary>Retained for DevStand compatibility (durable Mongo volume base name).</summary>
    public static string? DataVolumeName { get; set; }

    private static readonly Guid MailboxTriggerId = new("aaaaaaaa-e2e0-4000-8000-000000000001");
    internal const string AdapterMailbox      = "workflows@localhost";
    internal const string MailboxPassword     = "pw";

    private const string RabbitMqAlias        = "rabbitmq";
    private const string MongoAlias           = "mongo";
    private const string CoreApiAlias         = "core-api";
    private const string GreenMailAlias       = "greenmail";
    private const string GreenMailImage       = "greenmail/standalone:2.1.3";
    private const int    ImapPort             = 3143;
    private const int    SmtpPort             = 3025;
    private const string DockerSocket         = "/var/run/docker.sock";
    private const string ContainerPluginsDir  = "/slot-plugins";
    private const string ContainerRunOutput   = "/run-output";
    private const string ContainerWorkspaces  = "/workspaces";

    private static readonly string NetworkName =
        $"auxilia-e2e-{Guid.NewGuid():N}".Substring(0, 30);

    private static readonly string RepoRoot = RepoPaths.Root;

    private INetwork          _network    = null!;
    private RabbitMqContainer _rabbitMq   = null!;
    private MongoDbContainer  _mongoDb    = null!;
    private IContainer        _greenMail  = null!;
    private IContainer        _gitServer  = null!;
    private string            _publishDir = null!;
    private string            _runOutputDir = null!;
    private string            _workspaceDir = null!;

    public static IContainer Runner  { get; private set; } = null!;
    /// <summary>Core.Api control plane (Run API + identity + audit + failover monitor).</summary>
    public static IContainer CoreApi { get; private set; } = null!;
    /// <summary>TriggerHost running the email task-source adapter + the client-library engines.</summary>
    public static IContainer TriggerHost { get; private set; } = null!;
    /// <summary>Auxilia.AdminConsole — the operator/admin Blazor UI, a pure Core.Api client.</summary>
    public static IContainer AdminConsole { get; private set; } = null!;
    /// <summary>Browser-reachable AdminConsole base URL (mapped host port).</summary>
    public static string AdminConsoleUrl =>
        $"http://localhost:{AdminConsole.GetMappedPublicPort(8080)}";
    public static IMessageBusClient MessageBusClient { get; private set; } = null!;
    /// <summary>Authenticated (bootstrap Administrator) client for the Core Run/identity/audit API.</summary>
    public static HttpClient CoreApiClient { get; private set; } = null!;
    public static string MongoConnectionString { get; private set; } = null!;
    public static string MailHost   { get; private set; } = null!;
    public static int    MappedImap { get; private set; }
    public static int    MappedSmtp { get; private set; }
    /// <summary>Service principal (role: User) on whose behalf mail-triggered runs dispatch.</summary>
    public static Guid RunAsPrincipalId { get; private set; }
    /// <summary>The Core configuration id the mailbox trigger dispatches (returned by the Core on create).</summary>
    public static Guid MailReviewConfigurationId { get; private set; }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _publishDir   = Path.Combine(Path.GetTempPath(), $"auxilia-e2e-plugins-{Guid.NewGuid():N}");
        _runOutputDir = Path.Combine(Path.GetTempPath(), $"auxilia-e2e-output-{Guid.NewGuid():N}");
        _workspaceDir = Path.Combine(Path.GetTempPath(), $"auxilia-e2e-workspaces-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_publishDir);
        Directory.CreateDirectory(_runOutputDir);
        Directory.CreateDirectory(_workspaceDir);

        // Host-side publishes share a dependency graph — run sequentially to avoid obj/ contention.
        // All slot providers land in ONE plugins directory the runner mounts and loads from.
        await PublishProjectAsync(
            "Tests/System/Auxilia.FakeSlots.CodeReview.Happy/Auxilia.FakeSlots.CodeReview.Happy.csproj", _publishDir);
        await PublishProjectAsync(
            "Tests/System/Auxilia.FakeSlots.CodeReview.WriteBackFailure/Auxilia.FakeSlots.CodeReview.WriteBackFailure.csproj",
            _publishDir);
        await PublishProjectAsync("Source/Slots/Auxilia.Slots.Email/Auxilia.Slots.Email.csproj", _publishDir);
        await PublishProjectAsync("Source/Slots/Auxilia.Slots.ClaudeCode/Auxilia.Slots.ClaudeCode.csproj", _publishDir);
        await PublishProjectAsync("Source/Slots/Auxilia.Slots.CodingSession/Auxilia.Slots.CodingSession.csproj", _publishDir);
        await PublishProjectAsync(
            "Source/Slots/Auxilia.Slots.SimulatedWorkItems/Auxilia.Slots.SimulatedWorkItems.csproj", _publishDir);

        // Sequential on purpose: parallel docker builds have wedged Docker Desktop daemons.
        await WorkflowDispatchEnvironment.BuildImageAsync(
            WorkflowDispatchEnvironment.RunnerImageName, "Source/Platform/Auxilia.Core.Runner/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            CoreApiImageName, "Source/Platform/Auxilia.Core.Api/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            TriggerHostImageName, "Source/Platform/Auxilia.TriggerHost/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            AdminConsoleImageName, "Source/Platform/Auxilia.AdminConsole/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            WorkflowImageName, "Source/Workflows/Auxilia.CodeReview.Workflow/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            ClaudeWorkflowImageName, "Source/Workflows/Auxilia.ClaudeCode.Workflow/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            ImplementationWorkflowImageName, "Source/Workflows/Auxilia.Implementation.Workflow/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            GitServerImageName, "Tests/System/Auxilia.SystemTestSuite/GitServer/Dockerfile");

        _network = new NetworkBuilder().WithName(NetworkName).Build();
        await _network.CreateAsync();

        _greenMail = new ContainerBuilder(GreenMailImage)
            .WithNetwork(_network)
            .WithNetworkAliases(GreenMailAlias)
            // hostname=0.0.0.0 is essential: without it GreenMail binds to loopback only and neither
            // mapped host ports nor network peers reach it.
            .WithEnvironment("GREENMAIL_OPTS",
                "-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 " +
                "-Dgreenmail.auth.disabled -Dgreenmail.verbose")
            .WithPortBinding(ImapPort, assignRandomHostPort: true)
            .WithPortBinding(SmtpPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(ImapPort))
            .Build();

        _gitServer = new ContainerBuilder(GitServerImageName)
            .WithNetwork(_network).WithNetworkAliases(GitServerAlias)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("resuming normal operations"))
            .Build();

        _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management")
            .WithUsername("guest").WithPassword("guest")
            .WithNetwork(_network).WithNetworkAliases(RabbitMqAlias)
            .Build();
        var mongoBuilder = new MongoDbBuilder("mongo:8.0")
            .WithNetwork(_network).WithNetworkAliases(MongoAlias)
            .WithUsername(string.Empty).WithPassword(string.Empty); // no auth — test only
        if (DataVolumeName is { Length: > 0 } dataVolume)
            mongoBuilder = mongoBuilder.WithVolumeMount(dataVolume + "-mongo", "/data/db");
        _mongoDb = mongoBuilder.Build();
        await Task.WhenAll(
            _greenMail.StartAsync(), _gitServer.StartAsync(),
            _rabbitMq.StartAsync(), _mongoDb.StartAsync());

        MongoConnectionString = _mongoDb.GetConnectionString();
        MailHost   = _greenMail.Hostname;
        MappedImap = _greenMail.GetMappedPublicPort(ImapPort);
        MappedSmtp = _greenMail.GetMappedPublicPort(SmtpPort);

        // --- Core.Api: control plane. Own in-memory store; dispatches onto the shared command queue. ---
        CoreApi = new ContainerBuilder(CoreApiImageName)
            .WithNetwork(_network)
            .WithNetworkAliases(CoreApiAlias)
            .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
            .WithEnvironment("RabbitMq__Host",     RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port",     "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("PlatformData__Backend", "InMemory")
            .WithEnvironment("PlatformData__ProtectionKeyBase64", Convert.ToBase64String(new byte[32]))
            .WithEnvironment("CoreSecurity__BootstrapApiKey", BootstrapApiKey)
            .WithEnvironment("CoreApi__AllowDispatchWithoutRunner", "true")
            .WithEnvironment("CoreApi__RunCommandQueue", CommandQueue)
            // Statically registered workflow types (Active) for every workflow the e2e suite runs.
            .WithEnvironment("CoreApi__StaticWorkflowTypes__0__WorkflowType", WorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__0__PackageUri", WorkflowPackageUri)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__1__WorkflowType", ClaudeWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__1__PackageUri", ClaudeWorkflowPackageUri)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__2__WorkflowType", ImplementationWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__2__PackageUri", ImplementationWorkflowPackageUri)
            .WithPortBinding(8080, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health")))
            .Build();
        await CoreApi.StartAsync();

        CoreApiClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{CoreApi.GetMappedPublicPort(8080)}")
        };
        CoreApiClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BootstrapApiKey);

        // Seed Core identity + the mail-review configuration over the REST API (bootstrap admin).
        RunAsPrincipalId = await CreateCorePrincipalAsync("E2E Mail Trigger", "User");
        var triggerHostApiKey = await CreateCoreServiceKeyAsync("E2E Trigger Host", "Operator");
        var adminConsoleApiKey = await CreateCoreServiceKeyAsync("E2E Admin Console", "Administrator");
        MailReviewConfigurationId = await CreateMailReviewConfigurationAsync();

        // --- Runner: Mongo-backed execution plane. Resolves every slot JIT from the Core. ---
        Runner = new ContainerBuilder(WorkflowDispatchEnvironment.RunnerImageName)
            .WithNetwork(_network)
            .WithBindMount(DockerSocket, DockerSocket)
            .WithBindMount(_publishDir, ContainerPluginsDir)
            .WithBindMount(_runOutputDir, ContainerRunOutput)
            .WithBindMount(_workspaceDir, ContainerWorkspaces)
            .WithEnvironment("RabbitMq__Host",     RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port",     "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            // Slots resolve just-in-time from the Core over HTTP (no local credential store).
            .WithEnvironment("WorkflowDispatcher__CoreApiBaseAddress", $"http://{CoreApiAlias}:8080")
            .WithEnvironment("WorkflowLauncher__NetworkName",      NetworkName)
            .WithEnvironment("WorkflowLauncher__RabbitMqHost",     RabbitMqAlias)
            .WithEnvironment("WorkflowLauncher__RabbitMqPort",     "5672")
            .WithEnvironment("WorkflowLauncher__RabbitMqUserName", "guest")
            .WithEnvironment("WorkflowLauncher__RabbitMqPassword", "guest")
            .WithEnvironment("WorkflowLauncher__ExtraEnvironmentVariables__AUXILIA_DEVELOPER_MODE", "1")
            // Provider plugin DLLs the launcher copies into the workflow container per provider type.
            .WithEnvironment("WorkflowLauncher__SlotPackages__fake-code-review-happy",
                $"{ContainerPluginsDir}/Auxilia.FakeSlots.CodeReview.Happy.slothandler.dll")
            .WithEnvironment("WorkflowLauncher__SlotPackages__fake-code-review-write-back-failure",
                $"{ContainerPluginsDir}/Auxilia.FakeSlots.CodeReview.WriteBackFailure.slothandler.dll")
            .WithEnvironment("WorkflowLauncher__SlotPackages__email-work-items",
                $"{ContainerPluginsDir}/Auxilia.Slots.Email.slothandler.dll")
            .WithEnvironment("WorkflowLauncher__SlotPackages__claude-code-cli",
                $"{ContainerPluginsDir}/Auxilia.Slots.ClaudeCode.slothandler.dll")
            .WithEnvironment("WorkflowLauncher__SlotPackages__coding-session-workspace",
                $"{ContainerPluginsDir}/Auxilia.Slots.CodingSession.slothandler.dll")
            .WithEnvironment("WorkflowLauncher__SlotPackages__simulated-work-items",
                $"{ContainerPluginsDir}/Auxilia.Slots.SimulatedWorkItems.slothandler.dll")
            // Long-living lifetimes are rejected unless operator-approved — the implementation
            // workflow (LongLiving) needs this or its registration dies at the directive step.
            .WithEnvironment("WorkflowDispatcher__ApprovedLongLivingWorkflowTypes__0",
                ImplementationWorkflowType)
            .WithEnvironment("WorkflowDispatcher__CommandQueueName",        CommandQueue)
            .WithEnvironment("WorkflowDispatcher__RegistrationQueueName",   "workflow-registration-e2e")
            .WithEnvironment("WorkflowDispatcher__AnnouncementQueueName",   "workflow.announcements-e2e")
            .WithEnvironment("WorkflowDispatcher__SlotActivationQueueName", "workflow-slot-activation-e2e")
            .WithEnvironment("WorkflowDispatcher__RunOutputDirectory",      ContainerRunOutput)
            .WithEnvironment("WorkflowDispatcher__RunOutputHostDirectory",  _runOutputDir)
            .WithEnvironment("WorkflowDispatcher__WorkspaceRootDirectory",     ContainerWorkspaces)
            .WithEnvironment("WorkflowDispatcher__WorkspaceRootHostDirectory", _workspaceDir)
            .WithEnvironment("PlatformData__Backend",               "MongoDb")
            .WithEnvironment("PlatformData__MongoConnectionString", $"mongodb://{MongoAlias}:27017")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("WorkflowDispatcher started"))
            .Build();

        // --- TriggerHost: email adapter + the client-library trigger engines, dispatching via the
        // Core Run API. It reads the mailbox trigger + slot instance from its own DB (this Mongo)
        // and drives runs through the Core using the service API key granted run.on-behalf-of
        // (Operator role). Deliberately NO RabbitMq environment: the host is a pure Core client.
        var triggerHostBuilder = new ContainerBuilder(TriggerHostImageName)
            .WithNetwork(_network)
            .WithEnvironment("Core__BaseAddress", $"http://{CoreApiAlias}:8080")
            .WithEnvironment("Core__ApiKey",      triggerHostApiKey)
            .WithEnvironment("PlatformData__Backend",               "MongoDb")
            .WithEnvironment("PlatformData__MongoConnectionString", $"mongodb://{MongoAlias}:27017")
            // Adapter sweep tick; each trigger additionally honours its own poll interval.
            .WithEnvironment("MailboxTriggers__TickSeconds", "1")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Email task source started"));
        TriggerHost = triggerHostBuilder.Build();

        // --- AdminConsole: the operator/admin Blazor UI, a pure Core client. The dev/screenshot
        // stand runs it with a static Administrator app key (Core__ApiKey): with no browser
        // session cookie, the console's per-user bearer provider yields null and every Core call
        // (including the auth handler's who-am-I) falls back to that key — pages render fully
        // authenticated without the same-origin gateway the production deployment uses.
        AdminConsole = new ContainerBuilder(AdminConsoleImageName)
            .WithNetwork(_network)
            .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
            .WithEnvironment("Core__BaseAddress", $"http://{CoreApiAlias}:8080")
            .WithEnvironment("Core__ApiKey",      adminConsoleApiKey)
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health")))
            .Build();

        await Task.WhenAll(Runner.StartAsync(), TriggerHost.StartAsync(), AdminConsole.StartAsync());

        MessageBusClient = await RabbitMqClient.CreateAsync(
            _rabbitMq.Hostname, _rabbitMq.GetMappedPublicPort(5672));

        // Seed the trigger host's data into its Mongo: a reusable mailbox slot instance (the
        // credential) and the mailbox trigger that points at the Core mail-review configuration.
        await using (var provider = BuildPlatformDataProvider())
        {
            var instances = provider.GetRequiredService<IDataAccess<SlotInstanceRecord>>();
            // The trigger host runs without a protection key (NullSettingsProtector) — plain settings JSON is
            // the correct stored format here.
            await instances.SaveAsync(new SlotInstanceRecord
            {
                Id = SlotInstanceRecord.IdFor("team-mailbox"),
                Name = "team-mailbox",
                DisplayName = "Team mailbox",
                ProviderType = "email-work-items",
                ProtectedSettingsJson = System.Text.Json.JsonSerializer.Serialize(EmailSettings())
            });

            await provider.GetRequiredService<IDataAccess<MailboxTriggerRecord>>()
                .SaveAsync(new MailboxTriggerRecord
                {
                    Id = MailboxTriggerId,
                    WorkflowConfigurationId = MailReviewConfigurationId,
                    SlotInstanceId = SlotInstanceRecord.IdFor("team-mailbox"),
                    PollIntervalSeconds = 2,
                    Enabled = true,
                    RunAsPrincipalId = RunAsPrincipalId
                });
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500)); // seed propagation window
    }

    /// <summary>The GreenMail IMAP/SMTP settings shape shared by the slot instance and the config binding.</summary>
    internal static Dictionary<string, string> EmailSettings() => new()
    {
        ["ImapHost"] = GreenMailAlias,
        ["ImapPort"] = ImapPort.ToString(),
        ["UseSsl"]   = "false",
        ["Username"] = AdapterMailbox,
        ["Password"] = MailboxPassword,
        ["SmtpHost"] = GreenMailAlias,
        ["SmtpPort"] = SmtpPort.ToString(),
        ["Folder"]   = "INBOX"
    };

    /// <summary>Creates a Core AI/service principal and assigns it a built-in role. Returns its id.</summary>
    private static async Task<Guid> CreateCorePrincipalAsync(string displayName, string role)
    {
        var resp = await CoreApiClient.PostAsJsonAsync(
            "/api/principals/ai", new CreateApiKeyPrincipalRequest(displayName));
        resp.EnsureSuccessStatusCode();
        var created = (await resp.Content.ReadFromJsonAsync<CreatedApiKeyPrincipal>())!;
        await AssignRoleAsync(created.Principal.Id, role);
        return created.Principal.Id;
    }

    /// <summary>Like <see cref="CreateCorePrincipalAsync"/> but returns the one-time API key.</summary>
    private static async Task<string> CreateCoreServiceKeyAsync(string displayName, string role)
    {
        var resp = await CoreApiClient.PostAsJsonAsync(
            "/api/principals/ai", new CreateApiKeyPrincipalRequest(displayName));
        resp.EnsureSuccessStatusCode();
        var created = (await resp.Content.ReadFromJsonAsync<CreatedApiKeyPrincipal>())!;
        await AssignRoleAsync(created.Principal.Id, role);
        return created.ApiKey;
    }

    private static async Task AssignRoleAsync(Guid principalId, string role)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/principals/{principalId}/roles")
        {
            Content = JsonContent.Create(new AssignRoleRequest(role))
        };
        // Granting Administrator is step-up gated: re-prove the bootstrap caller's own credential
        // (the API key) for a short-lived elevation ticket and send it along.
        if (role == "Administrator")
        {
            var stepUp = await CoreApiClient.PostAsJsonAsync(
                "/auth/step-up", new StepUpRequest(BootstrapApiKey));
            stepUp.EnsureSuccessStatusCode();
            var ticket = (await stepUp.Content.ReadFromJsonAsync<ElevationTicket>())!;
            request.Headers.Add("X-Auxilia-Elevation", ticket.Token);
        }
        var resp = await CoreApiClient.SendAsync(request);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Creates the mail-review Core configuration with full slot bindings so the Core resolves every
    /// slot just-in-time: the code-review slots to the happy fake, the work-items slot to the real
    /// email provider (its IMAP/SMTP settings inline). Returns the Core-assigned configuration id.
    /// </summary>
    private static async Task<Guid> CreateMailReviewConfigurationAsync()
    {
        var bindings = new List<SlotBinding>
        {
            new("repository",         "fake-code-review-happy"),
            new("pull-request",       "fake-code-review-happy"),
            new("primary-reviewer",   "fake-code-review-happy"),
            new("secondary-reviewer", "fake-code-review-happy"),
            new("workflow-bootstrap", "fake-code-review-happy"),
            new("work-items",         "email-work-items", Settings: EmailSettings())
        };
        var resp = await CoreApiClient.PostAsJsonAsync("/api/configurations",
            new CreateRunConfiguration(
                MailReviewConfigurationName, WorkflowType,
                Context: new Dictionary<string, string>(), SlotBindings: bindings));
        resp.EnsureSuccessStatusCode();
        var config = (await resp.Content.ReadFromJsonAsync<RunConfiguration>())!;
        return config.Id;
    }

    /// <summary>
    /// Direct access to the shared Mongo state over the mapped port — the same database
    /// (DatabaseName "Auxilia") the TriggerHost and the Runner use via AddPlatformEntity.
    /// </summary>
    public static ServiceProvider BuildPlatformDataProvider()
    {
        var services = new ServiceCollection();
        AddEntity<ArtifactRecord>(services);
        AddEntity<ViewDataRecord>(services);
        AddEntity<WorkflowInstanceRecord>(services);
        // The TriggerHost (trigger.mail-dispatch) and the Runner (registration/slot/artifact) both write audit
        // records into this shared Mongo DB. Core.Api's policy/on-behalf-of audit is in its own store
        // and is read over the /api/audit REST endpoint instead.
        AddEntity<AuditRecord>(services);
        AddEntity<MailboxTriggerRecord>(services);
        AddEntity<SlotInstanceRecord>(services);
        AddEntity<TriggerHealthRecord>(services);
        services.AddSingleton(TimeProvider.System);
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
        CoreApiClient?.Dispose();
        if (MessageBusClient is IAsyncDisposable d) await d.DisposeAsync();
        if (AdminConsole is not null) await AdminConsole.DisposeAsync();
        if (TriggerHost is not null) await TriggerHost.DisposeAsync();
        if (CoreApi is not null) await CoreApi.DisposeAsync();
        if (Runner  is not null) await Runner.DisposeAsync();
        await _greenMail.DisposeAsync();
        await _gitServer.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _mongoDb.DisposeAsync();
        await _network.DisposeAsync();
        TryDelete(_publishDir);
        TryDelete(_runOutputDir);
        TryDelete(_workspaceDir);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            // Git object/pack files are read-only — strip the attribute or recursive delete throws.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort — leftover temp directories are harmless.
        }
    }

    private static async Task PublishProjectAsync(string projectRelativePath, string outputDir)
    {
        // -nodeReuse:false + UseSharedCompilation=false: persistent MSBuild/Roslyn workers inherit the
        // redirected pipes; with node reuse they outlive the publish and ReadToEndAsync stalls.
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
