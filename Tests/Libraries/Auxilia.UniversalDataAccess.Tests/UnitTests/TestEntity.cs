using Auxilia.UniversalDataAccess;

namespace Auxilia.UniversalDataAccess.Tests.UnitTests;

public sealed class NestedInfo
{
    public string Label { get; init; } = string.Empty;
    public double Value { get; init; }
}

public sealed record TestEntity : IEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public IList<string> Tags { get; init; } = new List<string>();
    public IDictionary<string, int> Scores { get; init; } = new Dictionary<string, int>();
    public NestedInfo? Details { get; init; }

    public static TestEntity CreateSample() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Sample Entity",
        Count = 42,
        IsActive = true,
        CreatedAt = new DateTime(2026, 5, 4, 10, 30, 0, DateTimeKind.Utc),
        Tags = new List<string> { "alpha", "beta", "gamma" },
        Scores = new Dictionary<string, int> { ["easy"] = 100, ["hard"] = 9000 },
        Details = new NestedInfo { Label = "nested", Value = 3.14 }
    };

    public void AssertEquivalentTo(TestEntity other)
    {
        Assert.That(other.Id, Is.EqualTo(Id));
        Assert.That(other.Name, Is.EqualTo(Name));
        Assert.That(other.Count, Is.EqualTo(Count));
        Assert.That(other.IsActive, Is.EqualTo(IsActive));
        Assert.That(other.CreatedAt, Is.EqualTo(CreatedAt));
        Assert.That(other.Tags, Is.EquivalentTo(Tags));
        Assert.That(other.Scores, Is.EquivalentTo(Scores));
        Assert.That(other.Details, Is.Not.Null);
        Assert.That(other.Details!.Label, Is.EqualTo(Details?.Label));
        Assert.That(other.Details!.Value, Is.EqualTo(Details?.Value));
    }
}

