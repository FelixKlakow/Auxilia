using Auxilia.Core.Client;
using Auxilia.Core.Contracts;

namespace Auxilia.WorkflowStudio.Tests;

/// <summary>In-memory <see cref="ICoreClient"/> that records calls the Studio makes into the Core.</summary>
public sealed class FakeCoreClient : ICoreClient
{
    public List<Connector> Connectors { get; } = new();
    public List<CreateRunConfiguration> CreatedConfigurations { get; } = new();
    public List<Guid> RunConfigurationIds { get; } = new();

    public Connector AddConnector(string name = "conn", string providerType = "github")
    {
        var connector = new Connector(Guid.NewGuid(), name, providerType, new List<string>(), DateTimeOffset.UtcNow);
        Connectors.Add(connector);
        return connector;
    }

    public Task<RunConfiguration> CreateConfigurationAsync(CreateRunConfiguration request, CancellationToken ct = default)
    {
        CreatedConfigurations.Add(request);
        return Task.FromResult(new RunConfiguration(
            Guid.NewGuid(), request.Name, request.WorkflowType,
            request.Context ?? new Dictionary<string, string>(),
            request.SlotBindings ?? new List<SlotBinding>(), request.Enabled, DateTimeOffset.UtcNow));
    }

    public List<Guid?> RunConfigurationOnBehalfOf { get; } = new();
    public List<IReadOnlyDictionary<string, string>?> RunConfigurationContexts { get; } = new();

    public Task<RunAccepted> RunConfigurationAsync(
        Guid id, Guid? onBehalfOf = null, IReadOnlyDictionary<string, string>? context = null,
        CancellationToken ct = default)
    {
        RunConfigurationIds.Add(id);
        RunConfigurationOnBehalfOf.Add(onBehalfOf);
        RunConfigurationContexts.Add(context);
        var commandId = Guid.NewGuid();
        return Task.FromResult(new RunAccepted(commandId, commandId));
    }

    public Task<PagedResult<Connector>> QueryConnectorsAsync(ConnectorQuery query, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<Connector>(Connectors, Connectors.Count, query.Skip, query.Take));

    public Task<Connector> CreateConnectorAsync(CreateConnector request, CancellationToken ct = default)
    {
        var connector = new Connector(
            Guid.NewGuid(), request.Name, request.ProviderType, request.Settings.Keys.ToList(), DateTimeOffset.UtcNow);
        Connectors.Add(connector);
        return Task.FromResult(connector);
    }

    // Unused by the Studio — minimal stubs.
    public Task<RunConfiguration> UpdateConfigurationAsync(
        Guid id, UpdateRunConfiguration request, CancellationToken ct = default)
        => throw new NotSupportedException("not used by the Studio");
    public Task<Connector> UpdateConnectorAsync(Guid id, UpdateConnector request, CancellationToken ct = default)
        => throw new NotSupportedException("not used by the Studio");
    public Task<RunConfiguration?> GetConfigurationAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RunConfiguration?>(null);
    public Task<PagedResult<RunConfiguration>> QueryConfigurationsAsync(ConfigurationQuery query, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<RunConfiguration>(new List<RunConfiguration>(), 0, query.Skip, query.Take));
    public List<RunRequest> RunRequests { get; } = new();
    public Task<RunAccepted> RunAsync(RunRequest request, CancellationToken ct = default)
    {
        RunRequests.Add(request);
        return Task.FromResult(new RunAccepted(Guid.NewGuid(), Guid.NewGuid()));
    }
    public Task<RunStatus?> GetRunAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RunStatus?>(null);
    public Task<PagedResult<RunStatus>> QueryRunsAsync(RunQuery query, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<RunStatus>(new List<RunStatus>(), 0, query.Skip, query.Take));
    public Task<RunAccepted> RerunAsync(Guid id, CancellationToken ct = default)
        => throw new NotSupportedException("not used by the Studio");

    public Task CancelRunAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    public async IAsyncEnumerable<RunStreamEvent> StreamRunAsync(
        Guid runId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
    public Task<Connector?> GetConnectorAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(Connectors.FirstOrDefault(c => c.Id == id));
    public Task SetConnectorGrantsAsync(Guid id, SetConnectorGrants request, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<GroupDto> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct = default)
        => Task.FromResult(new GroupDto(Guid.NewGuid(), request.Name, request.Description, [], []));
    public Task<IReadOnlyList<GroupDto>> ListGroupsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GroupDto>>([]);
    public Task AddGroupMemberAsync(Guid groupId, AddGroupMemberRequest request, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task AssignGroupRoleAsync(Guid groupId, AssignGroupRoleRequest request, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<PagedResult<PrincipalDto>> QueryPrincipalsAsync(PrincipalQuery query, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<PrincipalDto>([], 0, query.Skip, query.Take));
    public Task<PrincipalDto?> GetPrincipalAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<PrincipalDto?>(null);
    public Task<PrincipalDto> CreateHumanPrincipalAsync(CreateHumanPrincipalRequest request, CancellationToken ct = default)
        => Task.FromResult(new PrincipalDto(Guid.NewGuid(), "Human", request.DisplayName, "Active", null, []));
    public Task<CreatedApiKeyPrincipal> CreateApiKeyPrincipalAsync(CreateApiKeyPrincipalRequest request, CancellationToken ct = default)
        => Task.FromResult(new CreatedApiKeyPrincipal(
            new PrincipalDto(Guid.NewGuid(), request.Kind, request.DisplayName, "Active", null, []), "aux_fake-key"));
    public Task AssignPrincipalRoleAsync(Guid id, AssignRoleRequest request, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task RevokePrincipalRoleAsync(Guid id, string roleName, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task SetPrincipalEnabledAsync(Guid id, SetPrincipalEnabledRequest request, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<IReadOnlyList<GroupMappingDto>> ListGroupMappingsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GroupMappingDto>>([]);
    public Task<GroupMappingDto> CreateGroupMappingAsync(CreateGroupMappingRequest request, CancellationToken ct = default)
        => Task.FromResult(new GroupMappingDto(
            Guid.NewGuid(), request.IdentityProvider, request.GroupClaim, request.RoleName));
    public Task RemoveGroupMappingAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<IdentityConnectorDescriptorDto>> ListIdentityConnectorsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IdentityConnectorDescriptorDto>>([]);
    public Task<IReadOnlyList<IdentitySourceDto>> ListIdentitySourcesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IdentitySourceDto>>([]);
    public Task<IdentitySourceDto?> GetIdentitySourceAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<IdentitySourceDto?>(null);
    public Task<IdentitySourceDto> SaveIdentitySourceAsync(SaveIdentitySourceRequest request, CancellationToken ct = default)
        => Task.FromResult(new IdentitySourceDto(
            Guid.NewGuid(), request.Name, request.ConnectorType, request.DisableMissing, request.DefaultRole,
            request.GroupRoleMappings, request.Settings, [], null, null));
    public Task DeleteIdentitySourceAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IdentityConnectorTestResult> TestIdentitySourceAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(new IdentityConnectorTestResult(true, "0 user(s)"));
    public Task<IdentityImportSummaryDto> ImportIdentitySourceAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(new IdentityImportSummaryDto(0, 0, 0, 0, []));
    public Task<CurrentPrincipal> GetCurrentPrincipalAsync(CancellationToken ct = default)
        => Task.FromResult(new CurrentPrincipal(Guid.NewGuid(), "fake", []));
    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<PagedResult<AuditEntry>> QueryAuditAsync(AuditQuery query, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<AuditEntry>(new List<AuditEntry>(), 0, query.Skip, query.Take));
    public Task<PagedResult<ProviderCatalogEntry>> QueryProviderCatalogAsync(ProviderCatalogQuery query, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<ProviderCatalogEntry>(new List<ProviderCatalogEntry>(), 0, query.Skip, query.Take));
    public Task<ProviderCatalogEntry> SetProviderAvailabilityAsync(string providerType, bool available, CancellationToken ct = default)
        => Task.FromResult(new ProviderCatalogEntry(providerType, available, "", [], [], null));
    public Task<ProviderCatalogEntry> SetProviderSettingDisabledAsync(string providerType, string settingKey, bool disabled, CancellationToken ct = default)
        => Task.FromResult(new ProviderCatalogEntry(providerType, false, "", [], [], null));
    public Task<PagedResult<Auxilia.Core.Contracts.WorkflowTypeDto>> ListWorkflowTypesAsync(Auxilia.Core.Contracts.WorkflowTypeQuery query, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<Auxilia.Core.Contracts.WorkflowTypeDto>([], 0, query.Skip, query.Take));
    public Task<Auxilia.Core.Contracts.WorkflowSchemaDto?> GetWorkflowSchemaAsync(string workflowType, CancellationToken ct = default)
        => Task.FromResult<Auxilia.Core.Contracts.WorkflowSchemaDto?>(null);
    public Task<PagedResult<RunViewItem>> GetRunViewsAsync(
        Guid runId, string? view = null, int skip = 0, int take = 200, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<RunViewItem>([], 0, skip, take));

    public Task<ProviderCatalogEntry> RegisterProviderAsync(RegisterSlotProvider request, CancellationToken ct = default)
        => Task.FromResult(new ProviderCatalogEntry(request.ProviderType, false, request.Category, [], request.Contracts, request.Description));

    public Task DeleteConfigurationAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteProviderAsync(string providerType, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<EnvironmentLayerDto>> ListEnvironmentLayersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<EnvironmentLayerDto>>([]);
    public Task<EnvironmentLayerDto?> GetEnvironmentLayerAsync(string providerType, CancellationToken ct = default)
        => Task.FromResult<EnvironmentLayerDto?>(null);
    public Task<EnvironmentLayerDto> UpsertEnvironmentLayerAsync(UpsertEnvironmentLayer request, CancellationToken ct = default)
        => Task.FromResult(new EnvironmentLayerDto(
            request.ProviderType, request.Description, request.BaseEnvironment,
            request.SetupScript, request.Version, DateTimeOffset.UtcNow));
    public Task DeleteEnvironmentLayerAsync(string providerType, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteConnectorAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ConnectorBrowseResult> BrowseConnectorAsync(Guid id, BrowseConnector request, CancellationToken ct = default)
        => Task.FromResult(new ConnectorBrowseResult([]));

    public Task ProvideInputAsync(Guid runId, string payloadJson, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<TerminalTicket> OpenTerminalAsync(Guid runId, CancellationToken ct = default)
        => Task.FromResult(new TerminalTicket(
            runId, $"/api/runs/{runId}/terminal/?ticket=fake", DateTimeOffset.UtcNow.AddMinutes(2)));
    public Task<int> ClearFinishedRunsAsync(CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<WorkflowTypeRegistrationDto> RegisterWorkflowTypeAsync(RegisterWorkflowTypeRequest request, CancellationToken ct = default)
        => Task.FromResult(new WorkflowTypeRegistrationDto(
            request.WorkflowType, request.PackageUri, WorkflowTypeStatus.Active, null, null, false,
            null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    public Task<WorkflowTypeRegistrationDto?> GetWorkflowTypeRegistrationAsync(string workflowType, CancellationToken ct = default)
        => Task.FromResult<WorkflowTypeRegistrationDto?>(null);
    public Task UnregisterWorkflowTypeAsync(string workflowType, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<WorkflowTypeRegistrationDto> ApproveWorkflowTypeAsync(string workflowType, CancellationToken ct = default)
        => Task.FromResult(new WorkflowTypeRegistrationDto(
            workflowType, null, WorkflowTypeStatus.Active, null, null, false,
            null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    public Task<WorkflowTypeRegistrationDto> DenyWorkflowTypeAsync(string workflowType, string reason, CancellationToken ct = default)
        => Task.FromResult(new WorkflowTypeRegistrationDto(
            workflowType, null, WorkflowTypeStatus.Denied, reason, null, false,
            null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
}
