namespace Auxilia.Workflows.Tests;

[TestFixture, Category("Unit")]
[NonParallelizable] // process environment variables are global state
public sealed class WorkflowEnvironmentVariablesTests
{
    private readonly List<string> _setVariables = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var name in _setVariables)
            System.Environment.SetEnvironmentVariable(name, null);
        _setVariables.Clear();
    }

    private void Set(string suffix, string? value)
    {
        var name = WorkflowEnvironmentVariables.WorkspaceMountPrefix + suffix;
        System.Environment.SetEnvironmentVariable(name, value);
        _setVariables.Add(name);
    }

    [Test]
    public void SingleMountRoot_NoMounts_IsNull()
        => Assert.That(WorkflowEnvironmentVariables.SingleMountRoot(), Is.Null);

    [Test]
    public void SingleMountRoot_OneMount_IsItsRoot()
    {
        Set("WORKSPACE-REPO", "/workspace/repos/workspace-repo/src");

        Assert.That(WorkflowEnvironmentVariables.SingleMountRoot(),
            Is.EqualTo("/workspace/repos/workspace-repo/src"));
    }

    [Test]
    public void SingleMountRoot_TwoDistinctMounts_IsNull_NoObviousWorkingRoot()
    {
        Set("REPO-A", "/workspace/repos/repo-a");
        Set("REPO-B", "/workspace/repos/repo-b");

        Assert.That(WorkflowEnvironmentVariables.SingleMountRoot(), Is.Null);
    }

    [Test]
    public void SingleMountRoot_EmptyValues_AreIgnored()
    {
        Set("EMPTY", "");
        Set("REAL", "/workspace/repos/real");

        Assert.That(WorkflowEnvironmentVariables.SingleMountRoot(),
            Is.EqualTo("/workspace/repos/real"));
    }
}
