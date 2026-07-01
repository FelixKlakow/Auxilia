using Auxilia.Slots.ClaudeCode;
using Auxilia.Workflows.Views;

namespace Auxilia.Slots.ClaudeCode.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class ClaudeStreamJsonParserTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void SystemInit_BecomesSystemEntryWithModel()
    {
        var parser = new ClaudeStreamJsonParser();

        var entries = parser.ParseLine(
            """{"type":"system","subtype":"init","session_id":"s1","model":"claude-sonnet-4-6","tools":["Bash"]}""",
            Timestamp);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Role, Is.EqualTo(AgentChatRole.System));
            Assert.That(entries[0].Content, Does.Contain("claude-sonnet-4-6"));
            Assert.That(entries[0].Label, Is.EqualTo("Claude Code"));
            Assert.That(entries[0].TimestampUtc, Is.EqualTo(Timestamp));
        });
    }

    [Test]
    public void AssistantText_BecomesAssistantEntry()
    {
        var parser = new ClaudeStreamJsonParser();

        var entries = parser.ParseLine(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Working on it."}]}}""",
            Timestamp);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Role, Is.EqualTo(AgentChatRole.Assistant));
            Assert.That(entries[0].Content, Is.EqualTo("Working on it."));
        });
    }

    [Test]
    public void AssistantWithTextAndToolUse_YieldsBothEntriesInOrder()
    {
        var parser = new ClaudeStreamJsonParser();

        var entries = parser.ParseLine(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Running tests."},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"dotnet test"}}]}}""",
            Timestamp);

        Assert.That(entries, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Role, Is.EqualTo(AgentChatRole.Assistant));
            Assert.That(entries[1].Role, Is.EqualTo(AgentChatRole.Tool));
            Assert.That(entries[1].ToolName, Is.EqualTo("Bash"));
            Assert.That(entries[1].ToolState, Is.EqualTo("Running"));
            Assert.That(entries[1].Content, Does.Contain("dotnet test"));
        });
    }

    [Test]
    public void ToolResult_ResolvesToolNameFromPrecedingToolUse()
    {
        var parser = new ClaudeStreamJsonParser();
        parser.ParseLine(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"a.cs"}}]}}""",
            Timestamp);

        var entries = parser.ParseLine(
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"file content","is_error":false}]}}""",
            Timestamp);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Role, Is.EqualTo(AgentChatRole.Tool));
            Assert.That(entries[0].ToolName, Is.EqualTo("Read"));
            Assert.That(entries[0].ToolState, Is.EqualTo("Success"));
            Assert.That(entries[0].Content, Is.EqualTo("file content"));
        });
    }

    [Test]
    public void ToolResultWithErrorFlag_BecomesErrorState()
    {
        var parser = new ClaudeStreamJsonParser();

        var entries = parser.ParseLine(
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_x","content":"boom","is_error":true}]}}""",
            Timestamp);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].ToolState, Is.EqualTo("Error"));
    }

    [Test]
    public void ToolResultWithTextBlockArrayContent_IsFlattened()
    {
        var parser = new ClaudeStreamJsonParser();

        var entries = parser.ParseLine(
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_x","content":[{"type":"text","text":"line one"},{"type":"text","text":"line two"}]}]}}""",
            Timestamp);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Content, Is.EqualTo("line one\nline two"));
    }

    [Test]
    public void SuccessResult_CapturesResultAndEmitsSummaryEntry()
    {
        var parser = new ClaudeStreamJsonParser();

        var entries = parser.ParseLine(
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":1800,"num_turns":3,"result":"All done.","total_cost_usd":0.0042}""",
            Timestamp);

        Assert.That(parser.Result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(parser.Result!.IsError, Is.False);
            Assert.That(parser.Result.ResultText, Is.EqualTo("All done."));
            Assert.That(parser.Result.NumTurns, Is.EqualTo(3));
            Assert.That(parser.Result.TotalCostUsd, Is.EqualTo(0.0042m));
            Assert.That(parser.Result.DurationMs, Is.EqualTo(1800L));
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Role, Is.EqualTo(AgentChatRole.System));
        });
    }

    [Test]
    public void ErrorResult_IsCapturedAsError()
    {
        var parser = new ClaudeStreamJsonParser();

        parser.ParseLine(
            """{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":25}""",
            Timestamp);

        Assert.That(parser.Result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(parser.Result!.IsError, Is.True);
            Assert.That(parser.Result.Subtype, Is.EqualTo("error_max_turns"));
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json at all")]
    [TestCase("""{"no-type":"here"}""")]
    [TestCase("""{"type":"some_future_event","payload":1}""")]
    [TestCase("""[1,2,3]""")]
    public void UnknownOrMalformedLines_AreIgnored(string line)
    {
        var parser = new ClaudeStreamJsonParser();

        var entries = parser.ParseLine(line, Timestamp);

        Assert.Multiple(() =>
        {
            Assert.That(entries, Is.Empty);
            Assert.That(parser.Result, Is.Null);
        });
    }

    [Test]
    public void OversizedContent_IsTruncated()
    {
        var parser = new ClaudeStreamJsonParser();
        var huge = new string('x', 10_000);
        var line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"HUGE"}]}}"""
            .Replace("HUGE", huge);

        var entries = parser.ParseLine(line, Timestamp);

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Content, Has.Length.LessThan(5000));
            Assert.That(entries[0].Content, Does.EndWith("(truncated)"));
        });
    }
}
