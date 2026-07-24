namespace Auxilia.Core.Runner.Workflows;

public readonly record struct ValidationResult(bool IsValid, IReadOnlyList<string> UnsatisfiedRequirements);
