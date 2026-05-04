using System.Reflection;

namespace Auxilia.BackendService;

/// <summary>
///     Immutable snapshot of runtime identity for this service instance.
///     Registered as a singleton in DI and captured once at startup.
/// </summary>
public sealed class ServiceInfo
{
    /// <summary>Creates a new <see cref="ServiceInfo" /> with an auto-generated instance ID.</summary>
    public ServiceInfo() : this(Guid.NewGuid())
    {
    }

    /// <summary>
    ///     Creates a new <see cref="ServiceInfo" /> with a pre-generated <paramref name="serviceId" />.
    ///     Use this overload in <c>Program.cs</c> to keep the instance ID in sync with the log-file name.
    /// </summary>
    public ServiceInfo(Guid serviceId)
    {
        ServiceId = serviceId;
    }

    public Guid ServiceId { get; }
    public DateTime StartupTimeUtc { get; } = DateTime.UtcNow;

    public string Version { get; } =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";
}