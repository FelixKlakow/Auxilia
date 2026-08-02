namespace Auxilia.Workflows.Environment;

public sealed record OsRequirement(OsConstraint Os) : IEnvironmentRequirement;
