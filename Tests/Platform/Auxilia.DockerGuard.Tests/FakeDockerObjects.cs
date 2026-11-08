namespace Auxilia.DockerGuard.Tests;

/// <summary>An in-memory daemon view: containers, networks and volumes with their labels, and the root volumes asked for.</summary>
internal sealed class FakeDockerObjects : IDockerObjects
{
    public static readonly IReadOnlyDictionary<string, string> Workflow = new Dictionary<string, string> { ["auxilia.workflow"] = "1" };
    public static readonly IReadOnlyDictionary<string, string> Egress = new Dictionary<string, string> { ["auxilia.egress"] = "1" };
    public static readonly IReadOnlyDictionary<string, string> Companion = new Dictionary<string, string> { ["auxilia.companion"] = "1" };
    public static readonly IReadOnlyDictionary<string, string> Foreign = new Dictionary<string, string> { ["com.example.app"] = "web" };

    public Dictionary<string, IReadOnlyDictionary<string, string>> Containers { get; } = new()
    {
        ["owned"] = Workflow,
        ["gateway"] = Egress,
        ["foreign"] = Foreign,
    };

    public Dictionary<string, DockerNetwork> Networks { get; } = new()
    {
        ["auxilia-run-1"] = new DockerNetwork("n1", "auxilia-run-1", Egress),
        ["auxilia-pod-1"] = new DockerNetwork("n2", "auxilia-pod-1", Companion),
        ["operator-net"] = new DockerNetwork("n3", "operator-net", new Dictionary<string, string>()),
        ["other-app-net"] = new DockerNetwork("n4", "other-app-net", Foreign),
    };

    public Dictionary<string, IReadOnlyDictionary<string, string>> Volumes { get; } = new()
    {
        ["auxilia-pod-1-logs"] = Companion,
        ["database-data"] = Foreign,
    };

    public Task<IReadOnlyDictionary<string, string>?> ContainerLabelsAsync(string id, CancellationToken ct)
        => Task.FromResult(Containers.GetValueOrDefault(id));

    public Task<DockerNetwork?> NetworkAsync(string idOrName, CancellationToken ct)
        => Task.FromResult(Networks.GetValueOrDefault(idOrName) ?? Networks.Values.FirstOrDefault(n => n.Id == idOrName));

    public Task<IReadOnlyDictionary<string, string>?> VolumeLabelsAsync(string name, CancellationToken ct)
        => Task.FromResult(Volumes.GetValueOrDefault(name));

    /// <summary>The roots whose volume was asked for, in order.</summary>
    public List<string> RootVolumeRequests { get; } = [];

    /// <summary>When set, providing a root volume fails with this message (an old daemon, a foreign volume).</summary>
    public string? RootVolumeFailure { get; set; }

    public Task<string> RootVolumeAsync(string root, CancellationToken ct)
    {
        RootVolumeRequests.Add(root);
        return RootVolumeFailure is { } failure
            ? Task.FromException<string>(new InvalidOperationException(failure))
            : Task.FromResult(DockerUpstream.RootVolumeName(root));
    }
}
