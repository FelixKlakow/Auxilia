namespace Auxilia.Workflows.Tests;

/// <summary>
/// The typed run-input accessor: declared names resolve from the dispatch context with
/// defaults and kind checks; a typo'd name or an unparseable value fails loudly instead of
/// running with wrong parameters.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunInputsTests
{
    private static IRunInputs Inputs(
        IReadOnlyDictionary<string, string> context, params WorkflowInputDescriptor[] declared)
        => new EnvironmentRunInputs(declared,
            key => context.GetValueOrDefault(key));

    private static string Key(string name) => $"WORKFLOW_CONTEXT__{name.ToUpperInvariant()}";

    [Test]
    public void Get_ResolvesContextValue_ElseDeclaredDefault_ElseNull()
    {
        var inputs = Inputs(
            new Dictionary<string, string> { [Key("target")] = "Felix" },
            new WorkflowInputDescriptor("target", "Target"),
            new WorkflowInputDescriptor("greeting", "Greeting") { DefaultValue = "hello" },
            new WorkflowInputDescriptor("note", "Note"));

        Assert.Multiple(() =>
        {
            Assert.That(inputs.Get("target"), Is.EqualTo("Felix"));
            Assert.That(inputs.Get("greeting"), Is.EqualTo("hello"), "the declared default applies");
            Assert.That(inputs.Get("note"), Is.Null);
        });
    }

    [Test]
    public void Get_UndeclaredName_ThrowsListingTheDeclaredOnes()
        => Assert.That(
            () => Inputs(new Dictionary<string, string>(),
                new WorkflowInputDescriptor("target", "Target")).Get("tagret"),
            Throws.ArgumentException.With.Message.Contains("not a declared input")
                .And.Message.Contains("target"),
            "a typo must throw, not silently read nothing");

    [Test]
    public void Require_ThrowsWhenNothingWasDeliveredAndNoDefaultExists()
    {
        var inputs = Inputs(new Dictionary<string, string>(),
            new WorkflowInputDescriptor("occasion", "Occasion", Required: true));

        Assert.That(() => inputs.Require("occasion"),
            Throws.InvalidOperationException.With.Message.Contains("occasion"));
    }

    [Test]
    public void TypedGetters_ParseInvariant_AndFailLoudlyOnJunk()
    {
        var inputs = Inputs(
            new Dictionary<string, string>
            {
                [Key("count")] = "4",
                [Key("ratio")] = "1.5",
                [Key("shout")] = "true",
                [Key("bad")] = "junk"
            },
            new WorkflowInputDescriptor("count", "Count") { Kind = WorkflowInputKinds.Number },
            new WorkflowInputDescriptor("ratio", "Ratio") { Kind = WorkflowInputKinds.Number },
            new WorkflowInputDescriptor("shout", "Shout") { Kind = WorkflowInputKinds.Boolean },
            new WorkflowInputDescriptor("bad", "Bad") { Kind = WorkflowInputKinds.Number },
            new WorkflowInputDescriptor("missing", "Missing") { Kind = WorkflowInputKinds.Number });

        Assert.Multiple(() =>
        {
            Assert.That(inputs.GetInt32("count"), Is.EqualTo(4));
            Assert.That(inputs.GetNumber("ratio"), Is.EqualTo(1.5), "invariant culture, never de-DE");
            Assert.That(inputs.GetBoolean("shout"), Is.True);
            Assert.That(inputs.GetInt32("missing", fallback: 7), Is.EqualTo(7));
            Assert.That(() => inputs.GetInt32("bad"),
                Throws.InvalidOperationException.With.Message.Contains("junk"),
                "a present-but-unparseable value fails the run visibly");
        });
    }

    [Test]
    public void Choice_OutsideTheDeclaredSet_Throws()
    {
        var inputs = Inputs(
            new Dictionary<string, string> { [Key("greeting")] = "howdy" },
            new WorkflowInputDescriptor("greeting", "Greeting")
            {
                Kind = WorkflowInputKinds.Choice,
                Choices = ["hello", "moin"]
            });

        Assert.That(() => inputs.Get("greeting"),
            Throws.InvalidOperationException.With.Message.Contains("hello, moin"));
    }

    [Test]
    public void GetRaw_ReachesUndeclaredContextKeys()
    {
        var inputs = Inputs(
            new Dictionary<string, string> { [Key("pod-bases")] = "sim-machine" });

        Assert.That(inputs.GetRaw("pod-bases"), Is.EqualTo("sim-machine"),
            "platform keys stay reachable without a declaration");
    }
}
