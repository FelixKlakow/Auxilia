using Auxilia.Workflows.TaskSource;

namespace Auxilia.Workflows.SlotPackages.Tests.TaskSource;

[TestFixture]
[Category("Unit")]
public class WorkItemTests
{
    [Test]
    public void Constructor_SetsAllProperties()
    {
        var item = new WorkItem("42", "Fix the bug", "Detailed description", ItemType.Bug);

        Assert.That(item.Id, Is.EqualTo("42"));
        Assert.That(item.Title, Is.EqualTo("Fix the bug"));
        Assert.That(item.Description, Is.EqualTo("Detailed description"));
        Assert.That(item.Type, Is.EqualTo(ItemType.Bug));
    }

    [Test]
    public void Constructor_NullDescription_IsAllowed()
    {
        var item = new WorkItem("1", "Title only", null, ItemType.Feature);
        Assert.That(item.Description, Is.Null);
    }

    [Test]
    public void Equality_WorksAsRecord()
    {
        var a = new WorkItem("1", "Title", "Desc", ItemType.UserStory);
        var b = new WorkItem("1", "Title", "Desc", ItemType.UserStory);
        var c = new WorkItem("2", "Other", null, ItemType.Bug);

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Is.Not.EqualTo(c));
    }
}
