using System.Reflection;

namespace Auxilia.BackendService;

/// <summary>
///     Immutable snapshot of runtime identity for this service instance.
///     Registered as a singleton in DI and captured once at startup.
/// </summary>
public sealed class ServiceInfo
{
    public Guid ServiceId { get; } = Guid.NewGuid();
    public DateTime StartupTimeUtc { get; } = DateTime.UtcNow;

    public string Version { get; } =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";
}