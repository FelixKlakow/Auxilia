using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Auxilia.Workflows.Actions;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows.Sandbox;
using Auxilia.Workflows.Views;
using Auxilia.Workflows.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Workflows.Tests.Sandbox;

/// <summary>
/// What keeps an agent inside its container's bounds: no platform secret reaches an agent
/// process, and a push is judged by what it DOES at the push gateway — the only route to the
/// credentialed remote — never by the command that started it.
/// </summary>
[TestFixture]
[Category("Unit")]
[NonParallelizable]
public sealed class AgentContainmentTests
{
    private const string OldId = "1111111111111111111111111111111111111111";
    private const string NewId = "2222222222222222222222222222222222222222";
    private const string Zero = "0000000000000000000000000000000000000000";

    [TestCase("RabbitMq__Password", true)]
    [TestCase("RabbitMq__UserName", true)]
    [TestCase("Workflow__InstanceToken", true)]
    [TestCase("Workflow__PushRemote__MAIN", true)]
    [TestCase("Workflow__WorkspaceMount__MAIN", false)]
    [TestCase("WORKFLOW_CONTEXT__WORKITEMID", false)]
    [TestCase("PATH", false)]
    public void PlatformSecrets_AreClassified(string variable, bool secret)
        => Assert.That(AgentSandbox.IsPlatformSecret(variable), Is.EqualTo(secret));

    [Test]
    public void Confine_NeverHandsAPlatformSecretToAnAgentProcess()
    {
        var startInfo = new ProcessStartInfo("claude") { UseShellExecute = false };
        startInfo.Environment["RabbitMq__Password"] = "bus-secret";
        startInfo.Environment["Workflow__InstanceToken"] = "instance-token";
        startInfo.Environment["Workflow__PushRemote__MAIN"] = "https://u:push-token@git/x.git";
        startInfo.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = "the-agents-own-credential";

        AgentSandbox.Confine(startInfo);

        Assert.That(startInfo.Environment.Keys.Where(AgentSandbox.IsPlatformSecret), Is.Empty);
        Assert.That(startInfo.Environment["CLAUDE_CODE_OAUTH_TOKEN"], Is.EqualTo("the-agents-own-credential"),
            "the agent keeps the credential it is MEANT to use");
    }

    [Test]
    public void ScrubPlatformSecrets_RemovesThemFromTheProcessEnvironment()
    {
        System.Environment.SetEnvironmentVariable("Workflow__PushRemote__SCRUBTEST", "https://u:t@h/r.git");
        System.Environment.SetEnvironmentVariable("Workflow__WorkspaceMount__SCRUBTEST", "/workspace/repos/x");
        try
        {
            AgentSandbox.ScrubPlatformSecrets();

            Assert.That(System.Environment.GetEnvironmentVariable("Workflow__PushRemote__SCRUBTEST"), Is.Null);
            Assert.That(System.Environment.GetEnvironmentVariable("Workflow__WorkspaceMount__SCRUBTEST"),
                Is.EqualTo("/workspace/repos/x"));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("Workflow__WorkspaceMount__SCRUBTEST", null);
        }
    }

    [Test]
    public async Task ReadCommands_ParsesTheRefUpdatesAndKeepsTheConsumedBytes()
    {
        var body = PushBody(
            $"{OldId} {NewId} refs/heads/main\0report-status side-band-64k",
            $"{Zero} {NewId} refs/heads/feature",
            $"{OldId} {Zero} refs/heads/old");
        var stream = new MemoryStream(body.Concat("PACKDATA"u8.ToArray()).ToArray());

        var (updates, consumed) = await GitPushGateway.ReadCommandsAsync(stream, CancellationToken.None);

        Assert.That(updates.Select(u => u.ToString()), Is.EqualTo(new[]
        {
            $"update refs/heads/main {OldId[..12]}→{NewId[..12]}",
            $"create refs/heads/feature at {NewId[..12]}",
            "delete refs/heads/old",
        }));
        Assert.That(consumed, Is.EqualTo(body), "exactly the command section — the pack stays in the stream");
    }

    [Test]
    public async Task Gateway_RelaysFetches_WithTheCredential_TheWorkspaceNeverSees()
    {
        var upstream = new RecordingUpstream();
        await using var gateway = GitPushGateway.Start(
            new Dictionary<string, Uri> { ["MAIN"] = new("https://bot:push-token@git.example.invalid/org/repo.git") },
            NullLogger.Instance, upstream);
        using var client = new HttpClient();

        var response = await client.GetAsync(gateway.LocalUrlFor("MAIN") + "/info/refs?service=git-upload-pack");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var forwarded = upstream.Requests.Single();
        Assert.That(forwarded.Uri, Is.EqualTo("https://git.example.invalid/org/repo.git/info/refs?service=git-upload-pack"));
        Assert.That(forwarded.Authorization,
            Is.EqualTo("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("bot:push-token"))));
    }

    [Test]
    public async Task Gateway_DeniedPush_NeverReachesTheRemote()
    {
        var upstream = new RecordingUpstream();
        await using var gateway = GitPushGateway.Start(
            new Dictionary<string, Uri> { ["MAIN"] = new("https://bot:t@git.example.invalid/repo.git") },
            NullLogger.Instance, upstream);
        var approver = new FixedApprover(ActionDecision.Deny("not today"));
        using var _ = ActionGate.Register(approver);
        using var client = new HttpClient();

        var response = await client.PostAsync(gateway.LocalUrlFor("MAIN") + "/git-receive-pack",
            new ByteArrayContent(PushBody($"{OldId} {NewId} refs/heads/main\0report-status")));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("not today"));
        Assert.That(upstream.Requests, Is.Empty, "a denied push must not reach the remote at all");
        Assert.That(approver.Seen.Single().Kind, Is.EqualTo(ActionKinds.GitPush));
        Assert.That(approver.Seen.Single().Details.Single(), Does.Contain("refs/heads/main"));
    }

    [Test]
    public async Task Gateway_AllowedPush_ReachesTheRemoteByteForByte()
    {
        var upstream = new RecordingUpstream();
        await using var gateway = GitPushGateway.Start(
            new Dictionary<string, Uri> { ["MAIN"] = new("https://bot:t@git.example.invalid/repo.git") },
            NullLogger.Instance, upstream);
        using var _ = ActionGate.Register(new FixedApprover(ActionDecision.Allow));
        using var client = new HttpClient();
        var body = PushBody($"{OldId} {NewId} refs/heads/main\0report-status")
            .Concat("PACK-AND-MORE-DATA"u8.ToArray()).ToArray();
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-receive-pack-request");

        var response = await client.PostAsync(gateway.LocalUrlFor("MAIN") + "/git-receive-pack", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(upstream.Requests.Single().Body, Is.EqualTo(body));
        Assert.That(upstream.Requests.Single().ContentType, Is.EqualTo("application/x-git-receive-pack-request"));
    }

    [Test]
    public async Task SessionApprover_AutoAllow_AllowsAndRecords()
    {
        var chat = new List<AgentChatEntry>();
        var approver = new AgentSessionActionApprover(_ => AgentPermissionModes.AutoAllow, null,
            (e, _) => { chat.Add(e); return Task.CompletedTask; });

        var decision = await approver.DecideAsync(Push().ToAction(), CancellationToken.None);

        Assert.That(decision.Allowed, Is.True);
        Assert.That(chat.Single().Content, Does.Contain("refs/heads/main"));
    }

    [Test]
    public async Task SessionApprover_AskOperator_AsksWithWhatTheActionDoes()
    {
        var interaction = new ScriptedInteraction("deny");
        var approver = new AgentSessionActionApprover(_ => AgentPermissionModes.AskOperator, interaction,
            (_, _) => Task.CompletedTask);

        var decision = await approver.DecideAsync(Push().ToAction(), CancellationToken.None);

        Assert.That(decision.Allowed, Is.False);
        Assert.That(interaction.Asked!.Prompt, Does.Contain("PUSH"));
        Assert.That(interaction.Asked.Detail, Does.Contain("refs/heads/main"));
    }

    [Test]
    public async Task SessionApprover_AskOperatorWithoutAnOperator_Denies()
    {
        var approver = new AgentSessionActionApprover(_ => AgentPermissionModes.AskOperator, null,
            (_, _) => Task.CompletedTask);

        var decision = await approver.DecideAsync(Push().ToAction(), CancellationToken.None);

        Assert.That(decision.Allowed, Is.False, "a policy that requires asking cannot be satisfied silently");
    }

    [Test]
    public async Task SessionApprover_MapsEachActionKindToItsOwnPolicy()
    {
        var interaction = new ScriptedInteraction("deny");
        var approver = new AgentSessionActionApprover(
            kind => kind == ActionKinds.GitPush ? AgentPermissionModes.AskOperator : AgentPermissionModes.AutoAllow,
            interaction, (_, _) => Task.CompletedTask);

        var other = await approver.DecideAsync(
            new GatedAction("mail.send", "ops@example.invalid", "a status mail", []), CancellationToken.None);
        var push = await approver.DecideAsync(Push().ToAction(), CancellationToken.None);

        Assert.That(other.Allowed, Is.True, "a kind under auto-allow passes unasked");
        Assert.That(push.Allowed, Is.False, "the push kind follows its own ask-operator policy");
    }

    [Test]
    public async Task Gate_WithoutARegisteredPolicy_AllowsTheWorkflowsOwnPush()
        => Assert.That((await ActionGate.DecideAsync(Push().ToAction(), CancellationToken.None)).Allowed, Is.True);

    private static GitPushRequest Push()
        => new("MAIN", "https://git.example.invalid/repo.git", [new GitRefUpdate(OldId, NewId, "refs/heads/main")]);

    private static byte[] PushBody(params string[] commands)
    {
        var builder = new StringBuilder();
        foreach (var command in commands)
            builder.Append($"{command.Length + 5:x4}{command}\n");
        builder.Append("0000");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private sealed class FixedApprover(ActionDecision decision) : IActionApprover
    {
        public List<GatedAction> Seen { get; } = [];

        public Task<ActionDecision> DecideAsync(GatedAction action, CancellationToken cancellationToken)
        {
            Seen.Add(action);
            return Task.FromResult(decision);
        }
    }

    private sealed class RecordingUpstream : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, byte[] Body, string? ContentType)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body,
                request.Content?.Headers.ContentType?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("ok"u8.ToArray()) };
        }
    }

    private sealed class ScriptedInteraction(string answer) : IAgentInteraction
    {
        public AgentQuestion? Asked { get; private set; }

        public Task<AgentAnswer> AskAsync(AgentQuestion question, CancellationToken cancellationToken)
        {
            Asked = question;
            return Task.FromResult(new AgentAnswer([answer]));
        }

        public Task<string> WaitForGuidanceAsync(CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(_ => "", cancellationToken);

        public Task<AgentSetting> WaitForSettingAsync(CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(_ => new AgentSetting("", ""), cancellationToken);
    }
}
