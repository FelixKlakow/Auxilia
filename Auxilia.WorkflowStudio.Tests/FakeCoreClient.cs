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
            Guid.NewGuid(), request.Name, request.WorkflowType, request.PackageUri,
            request.Context ?? new Dictionary<string, string>(),
            request.SlotBindings ?? new List<SlotBinding>(), request.Enabled, DateTimeOffset.UtcNow));
    }

    public Task<RunAccepted> RunConfigurationAsync(Guid id, CancellationToken ct = default)
    {
        RunConfigurationIds.Add(id);
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
    public Task<RunConfiguration?> GetConfigurationAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RunConfiguration?>(null);
    public Task<PagedResult<RunConfiguration>> QueryConfigurationsAsync(ConfigurationQuery query, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<RunConfiguration>(new List<RunConfiguration>(), 0, query.Skip, query.Take));
    public Task<RunAccepted> RunAsync(RunRequest request, CancellationToken ct = default)
        => Task.FromResult(new RunAccepted(Guid.NewGuid(), Guid.NewGuid()));
    public Task<RunStatus?> GetRunAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<RunStatus?>(null);
    public Task<PagedResult<RunStatus>> QueryRunsAsync(RunQuery query, CancellationToken ct = default)
        => Task.FromResult(new PagedResult<RunStatus>(new List<RunStatus>(), 0, query.Skip, query.Take));
    public Task CancelRunAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
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
    public Task<IReadOnlyList<GroupMappingDto>> ListGroupMappingsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GroupMappingDto>>([]);
    public Task<GroupMappingDto> CreateGroupMappingAsync(CreateGroupMappingRequest request, CancellationToken ct = default)
        => Task.FromResult(new GroupMappingDto(
            Guid.NewGuid(), request.IdentityProvider, request.GroupClaim, request.RoleName));
    public Task RemoveGroupMappingAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    public Task<CurrentPrincipal> GetCurrentPrincipalAsync(CancellationToken ct = default)
        => Task.FromResult(new CurrentPrincipal(Guid.NewGuid(), "fake", []));
    public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(true);
}
