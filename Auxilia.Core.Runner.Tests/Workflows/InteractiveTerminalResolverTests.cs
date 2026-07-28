using Auxilia.Core.Runner.Workflows;
using Auxilia.Workflows;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture, Category("Unit")]
public sealed class InteractiveTerminalResolverTests
{
    private static WorkflowSchema Schema(InteractiveTerminalGate? gate, string? defaultValue = "advanced")
        => new("wf", [], [])
        {
            InteractiveTerminalPort = 7681,
            InteractiveTerminalGate = gate,
            Inputs =
            [
                new WorkflowInputDescriptor("view-mode", "View") { DefaultValue = defaultValue }
            ]
        };

    [Test]
    public void NoSchemaOrNoTerminal_ResolvesNoPort()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InteractiveTerminalResolver.ResolvePort(null, new Dictionary<string, string>()), Is.Null);
            Assert.That(InteractiveTerminalResolver.ResolvePort(
                new WorkflowSchema("wf", [], []), new Dictionary<string, string>()), Is.Null);
        });
    }

    [Test]
    public void UngatedTerminal_IsExposedForEveryRun()
        => Assert.That(
            InteractiveTerminalResolver.ResolvePort(Schema(gate: null), new Dictionary<string, string>()),
            Is.EqualTo(7681));

    [Test]
    public void GatedTerminal_IsExposedOnlyWhenTheContextValueMatches()
    {
        var schema = Schema(new InteractiveTerminalGate("view-mode", "console"));

        Assert.Multiple(() =>
        {
            Assert.That(InteractiveTerminalResolver.ResolvePort(
                    schema, new Dictionary<string, string> { ["view-mode"] = "console" }),
                Is.EqualTo(7681));
            Assert.That(InteractiveTerminalResolver.ResolvePort(
                    schema, new Dictionary<string, string> { ["View-Mode"] = " Console " }),
                Is.EqualTo(7681),
                "context key and value match case-insensitively, values trimmed");
            Assert.That(InteractiveTerminalResolver.ResolvePort(
                    schema, new Dictionary<string, string> { ["view-mode"] = "advanced" }),
                Is.Null);
        });
    }

    [Test]
    public void GatedTerminal_FallsBackToTheInputsDeclaredDefault()
    {
        var gate = new InteractiveTerminalGate("view-mode", "console");

        Assert.Multiple(() =>
        {
            Assert.That(InteractiveTerminalResolver.ResolvePort(
                    Schema(gate, defaultValue: "advanced"), new Dictionary<string, string>()),
                Is.Null,
                "an absent context entry resolves through the declared default");
            Assert.That(InteractiveTerminalResolver.ResolvePort(
                    Schema(gate, defaultValue: "console"), new Dictionary<string, string>()),
                Is.EqualTo(7681));
            Assert.That(InteractiveTerminalResolver.ResolvePort(
                    Schema(gate, defaultValue: null), new Dictionary<string, string>()),
                Is.Null,
                "no context value and no default means the gate stays closed");
        });
    }
}
