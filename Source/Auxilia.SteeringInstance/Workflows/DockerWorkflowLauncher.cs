using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Launches workflow containers using the local Docker CLI.
/// Environment variables for RabbitMQ connection and workflow context are injected at launch time.
/// The container is started detached (<c>docker run -d</c>); the launcher does not wait for it to finish.
/// </summary>
public sealed class DockerWorkflowLauncher(
    IOptions<DockerWorkflowLauncherSettings> settingsOptions,
    ILogger<DockerWorkflowLauncher> logger) : IWorkflowLauncher
{
    public async Task LaunchAsync(WorkflowLaunchRequest request, CancellationToken ct = default)
    {
        var args = BuildDockerArgs(request, settingsOptions.Value);

        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        logger.LogInformation(
            "Launching workflow container. Image={Image} Network={Network}",
            request.Image,
            settingsOptions.Value.NetworkName ?? "<default>");

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start docker process.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            logger.LogError(
                "docker run failed (exit {Code}) for image {Image}: {Stderr}",
                process.ExitCode, request.Image, stderr);
            throw new InvalidOperationException(
                $"docker run failed for image '{request.Image}' (exit {process.ExitCode}): {stderr}");
        }

        var containerId = stdout.Trim();
        logger.LogInformation(
            "Workflow container started. Image={Image} ContainerId={ContainerId}",
            request.Image, containerId);
    }

    /// <summary>
    /// Builds the ordered argument list passed to <c>docker run</c>.
    /// Extracted as an <c>internal</c> static method so unit tests can verify argument
    /// construction without invoking Docker.
    /// </summary>
    internal static IReadOnlyList<string> BuildDockerArgs(
        WorkflowLaunchRequest request,
        DockerWorkflowLauncherSettings settings)
    {
        var args = new List<string> { "run", "-d" };

        if (!string.IsNullOrWhiteSpace(settings.NetworkName))
        {
            args.Add("--network");
            args.Add(settings.NetworkName);
        }

        foreach (var (key, value) in request.EnvironmentVariables)
        {
            args.Add("-e");
            args.Add($"{key}={value}");
        }

        args.Add(request.Image);
        return args.AsReadOnly();
    }
}
