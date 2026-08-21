namespace Auxilia.Workflows.Companions;

/// <summary>Fluent configuration of one declared companion (see <see cref="CompanionDeclaration"/>).</summary>
public interface ICompanionBuilder
{
    ICompanionBuilder WithEnvironment(string name, string literalValue);

    ICompanionBuilder WithEnvironment(string name, CompanionValue value);

    ICompanionBuilder WithReadinessProbe(int tcpPort);

    ICompanionBuilder WithReadinessProbe(string httpPath, int port);

    ICompanionBuilder WithResourceCaps(int? memoryMb = null, double? cpus = null);

    /// <summary>
    /// Opens the instance count to the run: the input named <paramref name="countInput"/>
    /// picks within [<paramref name="minInstances"/>, <paramref name="maxInstances"/>].
    /// </summary>
    ICompanionBuilder WithScale(int minInstances, int maxInstances, string countInput);

    ICompanionBuilder WithStartAfter(params string[] companions);

    ICompanionBuilder WithPodVolume(string name, string mountPath);
}

/// <summary>
/// A runner-resolved companion environment value: minted or derived per run, never known at
/// packaging time (see <see cref="CompanionEnvironmentVariable"/> kinds).
/// </summary>
public sealed record CompanionValue(string Kind, string? SourceCompanion = null, int? Port = null)
{
    public static CompanionValue RunSecret() => new(CompanionEnvironmentVariable.RunSecret);

    public static CompanionValue InstanceCount(string companion)
        => new(CompanionEnvironmentVariable.InstanceCount, companion);

    public static CompanionValue InstanceEndpoints(string companion, int port)
        => new(CompanionEnvironmentVariable.InstanceEndpoints, companion, port);
}

internal sealed class CompanionBuilder(string name, string image) : ICompanionBuilder
{
    private readonly List<CompanionEnvironmentVariable> _environment = new();
    private readonly List<string> _startAfter = new();
    private readonly List<CompanionPodVolume> _podVolumes = new();
    private CompanionReadinessProbe? _readiness;
    private int? _memoryMb;
    private double? _cpus;
    private int _minInstances = 1;
    private int _maxInstances = 1;
    private string? _countInput;

    public ICompanionBuilder WithEnvironment(string name, string literalValue)
    {
        _environment.Add(new CompanionEnvironmentVariable(name, CompanionEnvironmentVariable.Literal)
        {
            Value = literalValue
        });
        return this;
    }

    public ICompanionBuilder WithEnvironment(string name, CompanionValue value)
    {
        _environment.Add(new CompanionEnvironmentVariable(name, value.Kind)
        {
            SourceCompanion = value.SourceCompanion,
            Port = value.Port
        });
        return this;
    }

    public ICompanionBuilder WithReadinessProbe(int tcpPort)
    {
        _readiness = new CompanionReadinessProbe(CompanionReadinessProbe.Tcp, tcpPort);
        return this;
    }

    public ICompanionBuilder WithReadinessProbe(string httpPath, int port)
    {
        _readiness = new CompanionReadinessProbe(CompanionReadinessProbe.Http, port) { HttpPath = httpPath };
        return this;
    }

    public ICompanionBuilder WithResourceCaps(int? memoryMb = null, double? cpus = null)
    {
        _memoryMb = memoryMb;
        _cpus = cpus;
        return this;
    }

    public ICompanionBuilder WithScale(int minInstances, int maxInstances, string countInput)
    {
        ArgumentException.ThrowIfNullOrEmpty(countInput);
        _minInstances = minInstances;
        _maxInstances = maxInstances;
        _countInput = countInput;
        return this;
    }

    public ICompanionBuilder WithStartAfter(params string[] companions)
    {
        _startAfter.AddRange(companions);
        return this;
    }

    public ICompanionBuilder WithPodVolume(string name, string mountPath)
    {
        _podVolumes.Add(new CompanionPodVolume(name, mountPath));
        return this;
    }

    public CompanionDeclaration Build() => new(name, image)
    {
        EnvironmentVariables = _environment.AsReadOnly(),
        Readiness = _readiness,
        MemoryMb = _memoryMb,
        Cpus = _cpus,
        MinInstances = _minInstances,
        MaxInstances = _maxInstances,
        CountInput = _countInput,
        StartAfter = _startAfter.AsReadOnly(),
        PodVolumes = _podVolumes.AsReadOnly()
    };
}
