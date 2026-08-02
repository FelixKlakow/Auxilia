namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class EnvironmentDeveloperModeProviderTests
{
    private string? _originalValue;

    [SetUp]
    public void SetUp()
        => _originalValue = global::System.Environment.GetEnvironmentVariable("AUXILIA_DEVELOPER_MODE");

    [TearDown]
    public void TearDown()
        => global::System.Environment.SetEnvironmentVariable("AUXILIA_DEVELOPER_MODE", _originalValue,
            EnvironmentVariableTarget.Process);

    [Test]
    public void IsActive_EnvVar1_ReturnsTrue()
    {
        global::System.Environment.SetEnvironmentVariable("AUXILIA_DEVELOPER_MODE", "1",
            EnvironmentVariableTarget.Process);

        var sut = new EnvironmentDeveloperModeProvider();

        Assert.That(sut.IsActive, Is.True);
    }

    [Test]
    public void IsActive_EnvVarTrue_ReturnsTrue()
    {
        global::System.Environment.SetEnvironmentVariable("AUXILIA_DEVELOPER_MODE", "true",
            EnvironmentVariableTarget.Process);

        var sut = new EnvironmentDeveloperModeProvider();

        Assert.That(sut.IsActive, Is.True);
    }

    [Test]
    public void IsActive_EnvVarYes_ReturnsTrue()
    {
        global::System.Environment.SetEnvironmentVariable("AUXILIA_DEVELOPER_MODE", "YES",
            EnvironmentVariableTarget.Process);

        var sut = new EnvironmentDeveloperModeProvider();

        Assert.That(sut.IsActive, Is.True);
    }

    [Test]
    public void IsActive_NotSet_ReturnsFalse()
    {
        global::System.Environment.SetEnvironmentVariable("AUXILIA_DEVELOPER_MODE", null,
            EnvironmentVariableTarget.Process);

        var sut = new EnvironmentDeveloperModeProvider();

        Assert.That(sut.IsActive, Is.False);
    }

    [Test]
    public void IsActive_EmptyString_ReturnsFalse()
    {
        global::System.Environment.SetEnvironmentVariable("AUXILIA_DEVELOPER_MODE", "",
            EnvironmentVariableTarget.Process);

        var sut = new EnvironmentDeveloperModeProvider();

        Assert.That(sut.IsActive, Is.False);
    }
}
