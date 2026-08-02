using Auxilia.Workflows.AiAgent;

namespace Auxilia.Workflows.SlotPackages.Tests.AiAgent;

[TestFixture]
[Category("Unit")]
public class ModalityTests
{
    [Test]
    public void Text_And_Image_Values_Exist()
    {
        Assert.That(Enum.IsDefined(typeof(Modality), "Text"), Is.True);
        Assert.That(Enum.IsDefined(typeof(Modality), "Image"), Is.True);
    }
}
