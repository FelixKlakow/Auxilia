using Docker.DotNet;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Creates <see cref="IDockerClient"/> instances. Abstracted for testability.
/// </summary>
public interface IDockerClientFactory
{
    IDockerClient CreateClient(string socketPath);
}
