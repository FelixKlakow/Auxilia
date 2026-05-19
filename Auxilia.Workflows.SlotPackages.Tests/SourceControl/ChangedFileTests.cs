using Auxilia.Workflows.SourceControl;

namespace Auxilia.Workflows.SlotPackages.Tests.SourceControl;

[TestFixture]
[Category("Unit")]
public class ChangedFileTests
{
    [Test]
    public void Constructor_SetsProperties()
    {
        var file = new ChangedFile("src/Foo.cs", ChangeKind.Modified);

        Assert.That(file.Path, Is.EqualTo("src/Foo.cs"));
        Assert.That(file.Kind, Is.EqualTo(ChangeKind.Modified));
    }

    [Test]
    public void ChangeKind_AllValues_Defined()
    {
        Assert.That(Enum.GetValues<ChangeKind>(), Is.EquivalentTo(new[]
        {
            ChangeKind.Added,
            ChangeKind.Modified,
            ChangeKind.Removed,
            ChangeKind.Renamed
        }));
    }

    [Test]
    public void EqualityAndDeconstruct_WorkAsRecord()
    {
        var a = new ChangedFile("foo.cs", ChangeKind.Added);
        var b = new ChangedFile("foo.cs", ChangeKind.Added);
        var c = new ChangedFile("bar.cs", ChangeKind.Removed);

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Is.Not.EqualTo(c));
    }
}
