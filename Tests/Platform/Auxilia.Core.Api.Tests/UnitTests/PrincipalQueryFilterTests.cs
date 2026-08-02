using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Entities;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>Unit tests for the pure principal query filter (kind, enabled, name/subject search, ordering).</summary>
[TestFixture]
[Category("Unit")]
public sealed class PrincipalQueryFilterTests
{
    private static PrincipalRecord Principal(string kind, string name, string status = "Active", string? subject = null)
        => new() { Kind = kind, DisplayName = name, Status = status, ExternalSubject = subject };

    private static readonly PrincipalRecord[] Sample =
    [
        Principal("Human", "Zoe", subject: "zoe@corp"),
        Principal("Human", "alice", status: "Disabled"),
        Principal("AiAgent", "Reviewer Bot"),
        Principal("Service", "ci-runner")
    ];

    [Test]
    public void FiltersByKind_CaseInsensitive()
    {
        var result = PrincipalQueryFilter.Apply(Sample, new PrincipalQuery(Kind: "human")).ToList();
        Assert.That(result.Select(p => p.DisplayName), Is.EquivalentTo(new[] { "Zoe", "alice" }));
    }

    [Test]
    public void FiltersByEnabled()
    {
        var enabled = PrincipalQueryFilter.Apply(Sample, new PrincipalQuery(Enabled: true)).Select(p => p.DisplayName);
        var disabled = PrincipalQueryFilter.Apply(Sample, new PrincipalQuery(Enabled: false)).Select(p => p.DisplayName);
        Assert.Multiple(() =>
        {
            Assert.That(enabled, Does.Not.Contain("alice"));
            Assert.That(disabled, Is.EqualTo(new[] { "alice" }));
        });
    }

    [Test]
    public void SearchMatchesDisplayNameOrExternalSubject_CaseInsensitive()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PrincipalQueryFilter.Apply(Sample, new PrincipalQuery(Search: "bot")).Select(p => p.DisplayName),
                Is.EqualTo(new[] { "Reviewer Bot" }));
            Assert.That(PrincipalQueryFilter.Apply(Sample, new PrincipalQuery(Search: "corp")).Select(p => p.DisplayName),
                Is.EqualTo(new[] { "Zoe" }), "External subject is searched.");
        });
    }

    [Test]
    public void OrdersByDisplayName_CaseInsensitive()
        => Assert.That(
            PrincipalQueryFilter.Apply(Sample, new PrincipalQuery()).Select(p => p.DisplayName),
            Is.EqualTo(new[] { "alice", "ci-runner", "Reviewer Bot", "Zoe" }));
}
