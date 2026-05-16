namespace Auxilia.SteeringInstance.Workflows;

public readonly record struct SchemaDiffResult(int AddedRequiredFieldsCount, int DirtyConfigurationCount)
{
    public bool IsDirty => DirtyConfigurationCount > 0;
}
