using Docker.DotNet;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Production implementation of <see cref="IDockerClientFactory"/> using
/// <see cref="DockerClientConfiguration"/> over a local or remote Docker socket.
/// </summary>
public sealed class DefaultDockerClientFactory : IDockerClientFactory
{
    public IDockerClient CreateClient(string socketPath)
        => new DockerClientConfiguration(new Uri(socketPath)).CreateClient();
}
