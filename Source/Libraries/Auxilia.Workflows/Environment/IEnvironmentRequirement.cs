using System.Text.Json.Serialization;

namespace Auxilia.Workflows.Environment;

/// <summary>Marker interface for workflow environment requirements.</summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(ToolRequirement), typeDiscriminator: "tool")]
[JsonDerivedType(typeof(OsRequirement), typeDiscriminator: "os")]
[JsonDerivedType(typeof(PortRequirement), typeDiscriminator: "port")]
public interface IEnvironmentRequirement;
