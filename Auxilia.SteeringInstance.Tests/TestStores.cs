using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Artifacts;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Tests;

/// <summary>Factory methods building the durable stores on in-memory data access for unit tests.</summary>
internal static class TestStores
{
    public static IPolicyEngine NewPolicyEngine()
        => new PolicyEngine(
            new InMemoryDataAccess<PrincipalRecord>(),
            new InMemoryDataAccess<RoleAssignmentRecord>(),
            new WorkflowTypeAccessStore(new InMemoryDataAccess<WorkflowTypeAccessRecord>()),
            NewAuditLog());

    public static SlotConfigurationStore NewSlotConfigurationStore()
        => new(new InMemoryDataAccess<SlotConfigurationRecord>(), new NullSettingsProtector());

    public static WorkflowSchemaStore NewWorkflowSchemaStore()
        => new(new InMemoryDataAccess<WorkflowSchemaRecord>());

    public static SlotProviderRegistry NewSlotProviderRegistry()
        => new(new InMemoryDataAccess<SlotProviderRecord>());

    public static SignalHandlerStore NewSignalHandlerStore()
        => new(new InMemoryDataAccess<SignalHandlerRecord>());

    public static WorkflowInstanceRegistry NewWorkflowInstanceRegistry()
        => new(new InMemoryDataAccess<WorkflowInstanceRecord>(), TimeProvider.System);

    public static AuditLog NewAuditLog()
        => new(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System);

    public static WorkflowStatusPublisher NewStatusPublisher(Auxilia.Messaging.IMessageBusClient bus)
        => new(bus, TimeProvider.System);

    public static LongLivingDrainCoordinator NewDrainCoordinator(
        Auxilia.Messaging.IMessageBusClient bus, WorkflowInstanceRegistry? instanceRegistry = null)
        => new(bus, instanceRegistry ?? NewWorkflowInstanceRegistry(), NewStatusPublisher(bus), NewAuditLog(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LongLivingDrainCoordinator>.Instance);

    public static FileSystemArtifactStore NewArtifactStore(string jsonDirectory)
        => new(new InMemoryDataAccess<ArtifactRecord>(), TimeProvider.System,
            new PlatformDataSettings { JsonDirectory = jsonDirectory });

    public static ArtifactPersister NewArtifactPersister(
        Auxilia.Messaging.IMessageBusClient bus, IArtifactStore artifactStore,
        WorkflowDispatcherSettings settings, AuditLog? auditLog = null)
        => new(artifactStore, bus, auditLog ?? NewAuditLog(), TimeProvider.System,
            Options.Create(settings),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtifactPersister>.Instance);

    public static SteeringInstanceInfo NewInstanceInfo()
        => new(Guid.NewGuid(), DateTime.UtcNow);
}
