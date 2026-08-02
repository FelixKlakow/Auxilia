using Auxilia.Core.Runner.Workflows;
using Auxilia.Workflows.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class NetworkPolicyResolverTests
{
    private readonly NetworkPolicyResolver _sut = new(NullLogger<NetworkPolicyResolver>.Instance);

    private static IReadOnlyList<NetworkEndpointDeclaration> Manifest(params string[] endpoints)
        => endpoints.Select(e => new NetworkEndpointDeclaration(e, "test purpose")).ToList();

    private static Dictionary<string, string> Context(
        string? networkMode = null, string? networkAllow = null)
    {
        var context = new Dictionary<string, string>();
        if (networkMode is not null)
            context["NetworkMode"] = networkMode;
        if (networkAllow is not null)
            context["NetworkAllow"] = networkAllow;
        return context;
    }

    [Test]
    public void Resolve_ManifestEndpointsOnly_DefaultDenyWithManifestAllowlist()
    {
        var policy = _sut.Resolve(
            Manifest("api.nuget.org", "registry.npmjs.org"),
            Context(),
            new WorkflowDispatcherSettings());

        Assert.Multiple(() =>
        {
            Assert.That(policy.Mode, Is.EqualTo(NetworkPolicyMode.DefaultDeny));
            Assert.That(policy.AllowedEndpoints,
                Is.EqualTo(new[] { "api.nuget.org", "registry.npmjs.org" }));
            Assert.That(policy.Note, Is.Null);
        });
    }

    [Test]
    public void Resolve_NoDeclarationsAtAll_DefaultDenyWithEmptyAllowlist()
    {
        var policy = _sut.Resolve(Manifest(), Context(), new WorkflowDispatcherSettings());

        Assert.That(policy.Mode, Is.EqualTo(NetworkPolicyMode.DefaultDeny));
        Assert.That(policy.AllowedEndpoints, Is.Empty);
    }

    [Test]
    public void Resolve_RunConfigExtras_MergedIntoAllowlist()
    {
        var policy = _sut.Resolve(
            Manifest("api.nuget.org"),
            Context(networkAllow: "internal-api.corp.local, ci.corp.local"),
            new WorkflowDispatcherSettings());

        Assert.That(policy.AllowedEndpoints,
            Is.EqualTo(new[] { "api.nuget.org", "internal-api.corp.local", "ci.corp.local" }));
    }

    [Test]
    public void Resolve_DuplicateEndpoints_Deduplicated()
    {
        var policy = _sut.Resolve(
            Manifest("api.nuget.org"),
            Context(networkAllow: "api.nuget.org"),
            new WorkflowDispatcherSettings());

        Assert.That(policy.AllowedEndpoints, Is.EqualTo(new[] { "api.nuget.org" }));
    }

    [Test]
    public void Resolve_AllowAllRequested_AndPlatformPermits_ModeIsAllowAll()
    {
        var policy = _sut.Resolve(
            Manifest("api.nuget.org"),
            Context(networkMode: "allow-all"),
            new WorkflowDispatcherSettings { AllowAllNetworkPermitted = true });

        Assert.Multiple(() =>
        {
            Assert.That(policy.Mode, Is.EqualTo(NetworkPolicyMode.AllowAll));
            Assert.That(policy.Note, Is.Null);
        });
    }

    [Test]
    public void Resolve_AllowAllRequested_ButPlatformForbids_ClampedToDefaultDenyWithNote()
    {
        var policy = _sut.Resolve(
            Manifest("api.nuget.org"),
            Context(networkMode: "allow-all"),
            new WorkflowDispatcherSettings()); // AllowAllNetworkPermitted defaults to false

        Assert.Multiple(() =>
        {
            Assert.That(policy.Mode, Is.EqualTo(NetworkPolicyMode.DefaultDeny));
            Assert.That(policy.AllowedEndpoints, Is.EqualTo(new[] { "api.nuget.org" }));
            Assert.That(policy.Note, Does.Contain("allow-all"));
        });
    }

    [Test]
    public void Resolve_UnknownNetworkMode_TreatedAsDefaultDeny()
    {
        var policy = _sut.Resolve(
            Manifest(),
            Context(networkMode: "something-else"),
            new WorkflowDispatcherSettings { AllowAllNetworkPermitted = true });

        Assert.That(policy.Mode, Is.EqualTo(NetworkPolicyMode.DefaultDeny));
    }

    [Test]
    public void Resolve_PlatformBlockedEndpoints_RemovedFromAllowlist()
    {
        var policy = _sut.Resolve(
            Manifest("api.nuget.org", "evil.example.com"),
            Context(networkAllow: "Blocked.Corp.Local"),
            new WorkflowDispatcherSettings
            {
                BlockedEndpoints = ["evil.example.com", "blocked.corp.local"]
            });

        Assert.That(policy.AllowedEndpoints, Is.EqualTo(new[] { "api.nuget.org" }));
    }
}
