using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Run input files through the Core's surface: a caller uploads, its run request names the upload
/// under the workflow's declared name, only the NAME rides the dispatch command, and the runner
/// fetches the content with the run's resolution token while the run is live. Plus the run's
/// required tools, which ride the command to the runner's pre-flight.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class RunInputFileTests : CoreApiComponentTestBase
{
    private const string WorkflowType = "input-file-workflow";

    private readonly string _payloadRoot = Path.Combine(Path.GetTempPath(), $"auxilia-input-files-{Guid.NewGuid():N}");

    protected override void ConfigureHost(IWebHostBuilder builder)
        => builder.UseSetting("CoreApi:MaxRunInputFileBytes", "4096")
            // Its own storage — never the machine's real platform-data folder.
            .UseSetting("ArtifactStore:PayloadRoot", _payloadRoot);

    [TearDown]
    public void RemovePayloads()
    {
        if (Directory.Exists(_payloadRoot))
            Directory.Delete(_payloadRoot, recursive: true);
    }

    [SetUp]
    public async Task DeclareTheWorkflow()
    {
        var client = CreateClient();
        await RegisterActiveTypeAsync(client, WorkflowType);
        await MessageBus.SimulateReceivedAsync(WorkflowSchemaPublished.ExchangeName,
            new WorkflowSchemaPublished(WorkflowType,
                new WorkflowSchema(WorkflowType, [], [])
                {
                    InputFiles =
                    [
                        new WorkflowInputFileDescriptor("session-state", "the session to continue", Required: true),
                        new WorkflowInputFileDescriptor("notes"),
                    ],
                },
                DateTimeOffset.UtcNow));
    }

    [Test]
    public async Task TheRunnerFetchesTheRunsFile_WithTheRunsToken_AndOnlyTheNameRidesTheCommand()
    {
        var client = CreateClient();
        var content = "the conversation so far — the run's alone";
        var upload = await UploadAsync(client, Encoding.UTF8.GetBytes(content));

        var accepted = await RunAsync(client, new RunRequest(WorkflowType) { InputFiles = Files(("session-state", upload.Id)) });
        var command = CommandOf(accepted);

        Assert.Multiple(() =>
        {
            Assert.That(command.InputFiles, Is.EqualTo(new[] { "session-state" }));
            Assert.That(JsonSerializer.Serialize(command), Does.Not.Contain("conversation"), "never the content");
        });

        using var fetched = await FetchAsync(command.CommandId, "session-state", command.ResolutionToken!);
        Assert.That(fetched.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await fetched.Content.ReadAsStringAsync(), Is.EqualTo(content));

        using var wrongToken = await FetchAsync(command.CommandId, "session-state", "not-the-token");
        using var notReceived = await FetchAsync(command.CommandId, "notes", command.ResolutionToken!);
        Assert.Multiple(() =>
        {
            Assert.That(wrongToken.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(notReceived.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task AnEndedRun_IsServedNothingAnyMore()
    {
        var client = CreateClient();
        var upload = await UploadAsync(client, "state"u8.ToArray());
        var command = CommandOf(await RunAsync(client,
            new RunRequest(WorkflowType) { InputFiles = Files(("session-state", upload.Id)) }));

        var runs = Factory.Services.GetRequiredService<IDataAccess<CoreRunRecord>>();
        var record = (await runs.ReadAsync(command.CommandId))!;
        await runs.SaveAsync(record with { State = RunStates.Success });

        using var fetched = await FetchAsync(command.CommandId, "session-state", command.ResolutionToken!);
        Assert.That(fetched.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "the token is a capability of a live run");
    }

    [Test]
    public async Task WhatTheWorkflowDoesNotDeclare_OrStillNeeds_OrAlreadyUsed_IsA400()
    {
        var client = CreateClient();
        var first = await UploadAsync(client, "a"u8.ToArray());
        var second = await UploadAsync(client, "b"u8.ToArray());

        using var undeclared = await client.PostAsJsonAsync("/api/runs", new RunRequest(WorkflowType)
        {
            InputFiles = Files(("session-state", first.Id), ("secrets", second.Id)),
        });
        using var missingRequired = await client.PostAsJsonAsync("/api/runs", new RunRequest(WorkflowType)
        {
            InputFiles = Files(("notes", second.Id)),
        });
        await RunAsync(client, new RunRequest(WorkflowType) { InputFiles = Files(("session-state", first.Id)) });
        using var reused = await client.PostAsJsonAsync("/api/runs", new RunRequest(WorkflowType)
        {
            InputFiles = Files(("session-state", first.Id)),
        });
        using var unknown = await client.PostAsJsonAsync("/api/runs", new RunRequest(WorkflowType)
        {
            InputFiles = Files(("session-state", Guid.NewGuid())),
        });

        Assert.Multiple(async () =>
        {
            Assert.That(undeclared.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), await undeclared.Content.ReadAsStringAsync());
            Assert.That(missingRequired.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(reused.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "one upload, one run");
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task AFileAboveTheLimit_IsA413()
    {
        using var response = await CreateClient().PostAsync("/api/run-input-files", new ByteArrayContent(new byte[4097]));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public async Task ARerunOfARunWithInputFiles_IsRefused()
    {
        var client = CreateClient();
        var upload = await UploadAsync(client, "state"u8.ToArray());
        var accepted = await RunAsync(client, new RunRequest(WorkflowType) { InputFiles = Files(("session-state", upload.Id)) });

        using var rerun = await client.PostAsync($"/api/runs/{accepted.RunId}/rerun", null);

        Assert.That(rerun.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await rerun.Content.ReadAsStringAsync(), Does.Contain("input files"));
    }

    [Test]
    public async Task ARunsRequiredTools_RideTheCommand_AndAMalformedOneIsA400()
    {
        var client = CreateClient();
        var upload = await UploadAsync(client, "state"u8.ToArray());

        var command = CommandOf(await RunAsync(client, new RunRequest(WorkflowType)
        {
            InputFiles = Files(("session-state", upload.Id)),
            RequiredTools = ["agent-cli@1.0.107"],
        }));
        using var malformed = await client.PostAsJsonAsync("/api/runs", new RunRequest(WorkflowType)
        {
            RequiredTools = ["agent-cli@"],
        });

        Assert.Multiple(() =>
        {
            Assert.That(command.RequiredTools, Is.EqualTo(new[] { "agent-cli@1.0.107" }), "checked by the runner before launch");
            Assert.That(malformed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<RunInputFile> UploadAsync(HttpClient client, byte[] content)
    {
        using var response = await client.PostAsync("/api/run-input-files", new ByteArrayContent(content));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RunInputFile>())!;
    }

    private static async Task<RunAccepted> RunAsync(HttpClient client, RunRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/runs", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<RunAccepted>())!;
    }

    private RunWorkflowCommand CommandOf(RunAccepted accepted)
        => MessageBus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>()
            .Single(c => c.CommandId == accepted.CommandId);

    private Task<HttpResponseMessage> FetchAsync(Guid commandId, string name, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/runs/{commandId}/input-files/{name}");
        request.Headers.Add("X-Resolution-Token", token);
        return Factory.CreateClient().SendAsync(request);
    }

    private static Dictionary<string, Guid> Files(params (string Name, Guid Id)[] files)
        => files.ToDictionary(f => f.Name, f => f.Id);
}
