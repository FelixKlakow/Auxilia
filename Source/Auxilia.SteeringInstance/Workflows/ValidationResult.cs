namespace Auxilia.SteeringInstance.Workflows;

public readonly record struct ValidationResult(bool IsValid, IReadOnlyList<string> UnsatisfiedRequirements);
