using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;

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

    public static SteeringInstanceInfo NewInstanceInfo()
        => new(Guid.NewGuid(), DateTime.UtcNow);
}
