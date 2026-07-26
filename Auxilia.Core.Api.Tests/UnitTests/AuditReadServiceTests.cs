using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class AuditReadServiceTests
{
    private static readonly DateTimeOffset Base = new(2026, 07, 26, 12, 0, 0, TimeSpan.Zero);

    private static AuditReadService NewService(out InMemoryDataAccess<AuditRecord> store)
    {
        store = new InMemoryDataAccess<AuditRecord>();
        return new AuditReadService(store);
    }

    private static async Task SeedAsync(
        InMemoryDataAccess<AuditRecord> store,
        string actor, string action, string subject, DateTimeOffset when)
        => await store.SaveAsync(new AuditRecord
        {
            TimestampUtc = when, Actor = actor, Action = action, Subject = subject, Outcome = "ok"
        }, CancellationToken.None);

    [Test]
    public async Task Query_OrdersNewestFirst_AndReportsTotal()
    {
        var svc = NewService(out var store);
        await SeedAsync(store, "a", "act", "s", Base);
        await SeedAsync(store, "b", "act", "s", Base.AddMinutes(5));
        await SeedAsync(store, "c", "act", "s", Base.AddMinutes(1));

        var page = await svc.QueryAsync(new AuditQuery(), CancellationToken.None);

        Assert.That(page.Total, Is.EqualTo(3));
        Assert.That(page.Items.Select(i => i.Actor), Is.EqualTo(new[] { "b", "c", "a" }));
    }

    [Test]
    public async Task Query_FiltersByActorActionSubject_ExactMatch()
    {
        var svc = NewService(out var store);
        await SeedAsync(store, "alice", "workflow.trigger", "wt-1", Base);
        await SeedAsync(store, "bob", "workflow.trigger", "wt-1", Base);
        await SeedAsync(store, "alice", "workflow.cancel", "wt-2", Base);

        var byActor = await svc.QueryAsync(new AuditQuery(Actor: "alice"), CancellationToken.None);
        var byAction = await svc.QueryAsync(new AuditQuery(Action: "workflow.trigger"), CancellationToken.None);
        var bySubject = await svc.QueryAsync(new AuditQuery(Subject: "wt-2"), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(byActor.Total, Is.EqualTo(2));
            Assert.That(byAction.Total, Is.EqualTo(2));
            Assert.That(bySubject.Total, Is.EqualTo(1));
            Assert.That(bySubject.Items.Single().Action, Is.EqualTo("workflow.cancel"));
        });
    }

    [Test]
    public async Task Query_TimeRange_IsFromInclusive_ToExclusive()
    {
        var svc = NewService(out var store);
        await SeedAsync(store, "a", "act", "s", Base);                 // at from
        await SeedAsync(store, "b", "act", "s", Base.AddMinutes(30));  // inside
        await SeedAsync(store, "c", "act", "s", Base.AddHours(1));     // at to (excluded)

        var page = await svc.QueryAsync(
            new AuditQuery(FromUtc: Base, ToUtc: Base.AddHours(1)), CancellationToken.None);

        Assert.That(page.Items.Select(i => i.Actor), Is.EquivalentTo(new[] { "a", "b" }));
    }

    [Test]
    public async Task Query_PagesResults_WhilePreservingTotal()
    {
        var svc = NewService(out var store);
        for (var i = 0; i < 5; i++)
            await SeedAsync(store, $"a{i}", "act", "s", Base.AddMinutes(i));

        var page = await svc.QueryAsync(new AuditQuery(Skip: 1, Take: 2), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(page.Total, Is.EqualTo(5));
            Assert.That(page.Items, Has.Count.EqualTo(2));
            // Newest is a4; skipping 1 yields a3, a2.
            Assert.That(page.Items.Select(i => i.Actor), Is.EqualTo(new[] { "a3", "a2" }));
        });
    }
}
