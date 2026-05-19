using System.Text.Json.Serialization;

namespace Auxilia.Workflows.Messaging.Messages;

[JsonPolymorphic]
[JsonDerivedType(typeof(InvokeWorkflowSignalHandler), typeDiscriminator: "invoke-workflow")]
[JsonDerivedType(typeof(NotifySignalHandler), typeDiscriminator: "notify")]
[JsonDerivedType(typeof(NullSignalHandler), typeDiscriminator: "null")]
public interface ISignalHandlerDescriptor;
