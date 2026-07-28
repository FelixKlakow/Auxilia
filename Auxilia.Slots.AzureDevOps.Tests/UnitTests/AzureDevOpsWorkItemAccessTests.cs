using System.Net;
using System.Text;
using System.Text.Json;
using Auxilia.Slots.AzureDevOps;

namespace Auxilia.Slots.AzureDevOps.Tests.UnitTests;

/// <summary>
/// Work-item access against a MOCKED HTTP layer speaking the real Work Item Tracking REST API
/// response shapes (mirrors <c>ConnectorBrowseAzureDevOpsTests</c>: no official ADO container
/// exists). The stub asserts the exact URLs, bodies and the PAT basic-auth header, so a
/// real-tenant verification only has to confirm connectivity, not behavior.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class AzureDevOpsWorkItemAccessTests
{
    private const string OrgUrl = "https://tfs.example.com/tfs/DefaultCollection";
    private const string Pat = "ado-pat-secret";

    private static readonly string WorkItemJson = JsonSerializer.Serialize(new
    {
        id = 42,
        fields = new Dictionary<string, object>
        {
            ["System.TeamProject"] = "Tools",
            ["System.WorkItemType"] = "User Story",
            ["System.Title"] = "Fix the widget",
            ["System.Description"] = "<div>First line<br>Second &amp; last</div>",
            ["System.State"] = "Active",
            ["System.AssignedTo"] = new { displayName = "Felix Klakow", uniqueName = "felix@example.com" },
            ["System.Tags"] = "backend; urgent",
        },
        relations = new object[]
        {
            new
            {
                rel = "AttachedFile",
                url = $"{OrgUrl}/_apis/wit/attachments/att-guid-1",
                attributes = new { name = "log.txt" },
            },
            new
            {
                rel = "AttachedFile",
                url = $"{OrgUrl}/_apis/wit/attachments/att-guid-2?fileName=screenshot.png",
                attributes = new { name = "screenshot.png" },
            },
            new
            {
                rel = "System.LinkTypes.Hierarchy-Reverse",
                url = $"{OrgUrl}/_apis/wit/workItems/7",
            },
        },
    });

    private static readonly string StatesJson = JsonSerializer.Serialize(new
    {
        count = 4,
        value = new object[]
        {
            new { name = "New", category = "Proposed" },
            new { name = "Active", category = "InProgress" },
            new { name = "Resolved", category = "InProgress" },
            new { name = "Closed", category = "Completed" },
        },
    });

    private sealed record CapturedRequest(
        HttpMethod Method, string Uri, string? Body, string? ContentType, string? AuthScheme, string? AuthParameter);

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct),
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
            return responder(request);
        }
    }

    private StubHttpMessageHandler _handler = null!;
    private AzureDevOpsWorkItemAccess _sut = null!;
    private bool _failFirstAttachment;

    [SetUp]
    public void SetUp()
    {
        _failFirstAttachment = false;
        _handler = new StubHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.Contains("/_apis/wit/workitems/42?", StringComparison.Ordinal))
                return request.Method == HttpMethod.Patch ? new HttpResponseMessage(HttpStatusCode.OK) : Json(WorkItemJson);
            if (uri.Contains("/_apis/wit/workitems/99?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (uri.Contains("/workItems/42/comments", StringComparison.Ordinal))
                return Json("""{"id":1,"text":"posted"}""");
            if (uri.Contains("/workitemtypes/", StringComparison.Ordinal))
                return Json(StatesJson);
            if (uri.Contains("att-guid-1", StringComparison.Ordinal))
                return _failFirstAttachment
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : Bytes([1, 2, 3]);
            if (uri.Contains("att-guid-2", StringComparison.Ordinal))
                return Bytes([4, 5]);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        _sut = new AzureDevOpsWorkItemAccess(OrgUrl + "/", Pat, _handler); // trailing slash must be tolerated
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
        _handler.Dispose();
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(content),
    };

    [Test]
    public async Task GetWorkItem_MapsTheAdoFields_WithPatBasicAuth()
    {
        var item = await _sut.GetWorkItemAsync("42");

        Assert.Multiple(() =>
        {
            Assert.That(item, Is.Not.Null);
            Assert.That(item!.Id, Is.EqualTo("42"));
            Assert.That(item.Title, Is.EqualTo("Fix the widget"));
            Assert.That(item.Description, Is.EqualTo("First line\nSecond & last"),
                "HTML is reduced to plain text best effort: breaks kept, tags stripped, entities decoded");
            Assert.That(item.Status, Is.EqualTo("Active"));
            Assert.That(item.AssigneeDisplayName, Is.EqualTo("Felix Klakow"));
            Assert.That(item.Labels, Is.EqualTo(new[] { "backend", "urgent" }),
                "System.Tags is a single '; '-separated string");

            var request = _handler.Requests.Single();
            Assert.That(request.Uri,
                Is.EqualTo($"{OrgUrl}/_apis/wit/workitems/42?api-version=7.0&$expand=relations"));
            Assert.That(request.AuthScheme, Is.EqualTo("Basic"));
            Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(request.AuthParameter!)),
                Is.EqualTo($":{Pat}"), "PAT basic auth uses an empty user name — cloud and on-prem alike");
        });
    }

    [Test]
    public async Task GetWorkItem_NotFound_ReturnsNull()
    {
        Assert.That(await _sut.GetWorkItemAsync("99"), Is.Null);
    }

    [Test]
    public async Task GetWorkItems_LoopsIds_AndSkipsMissingItems()
    {
        var items = await _sut.GetWorkItemsAsync(["42", "99"]);

        Assert.That(items.Select(i => i.Id), Is.EqualTo(new[] { "42" }));
    }

    [Test]
    public async Task PostComment_ResolvesTheProject_AndPostsTheCommentBody()
    {
        await _sut.PostCommentAsync("42", "On it.");

        var post = _handler.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Multiple(() =>
        {
            Assert.That(_handler.Requests[0].Uri, Does.Contain("/_apis/wit/workitems/42?"),
                "the comment URL needs the project, so the work item is resolved first");
            Assert.That(post.Uri, Is.EqualTo(
                $"{OrgUrl}/Tools/_apis/wit/workItems/42/comments?api-version=7.1-preview.3"));
            Assert.That(JsonDocument.Parse(post.Body!).RootElement.GetProperty("text").GetString(),
                Is.EqualTo("On it."));
        });
    }

    [Test]
    public void PostComment_UnknownWorkItem_Throws()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.PostCommentAsync("99", "lost"));
    }

    [Test]
    public async Task GetStates_ListsTheItemTypeStates_InServerOrder()
    {
        var states = await _sut.GetStatesAsync("42");

        Assert.Multiple(() =>
        {
            Assert.That(states, Is.EqualTo(new[] { "New", "Active", "Resolved", "Closed" }));
            Assert.That(_handler.Requests[^1].Uri, Is.EqualTo(
                $"{OrgUrl}/Tools/_apis/wit/workitemtypes/User%20Story/states?api-version=7.1-preview.1"),
                "project and work-item type come from the fetched item; the type is URL-escaped");
        });
    }

    [Test]
    public async Task GetStates_UnknownWorkItem_ReturnsEmpty()
    {
        Assert.That(await _sut.GetStatesAsync("99"), Is.Empty);
    }

    [Test]
    public async Task SetState_SendsAJsonPatch_ToTheWorkItem()
    {
        await _sut.SetStateAsync("42", "Resolved");

        var patch = _handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Multiple(() =>
        {
            Assert.That(patch.Uri, Is.EqualTo($"{OrgUrl}/_apis/wit/workitems/42?api-version=7.0"));
            Assert.That(patch.ContentType, Is.EqualTo("application/json-patch+json"));
            var operation = JsonDocument.Parse(patch.Body!).RootElement.EnumerateArray().Single();
            Assert.That(operation.GetProperty("op").GetString(), Is.EqualTo("add"));
            Assert.That(operation.GetProperty("path").GetString(), Is.EqualTo("/fields/System.State"));
            Assert.That(operation.GetProperty("value").GetString(), Is.EqualTo("Resolved"));
        });
    }

    [Test]
    public void SetState_NonSuccess_Throws_WithStatusButNeverTheToken()
    {
        using var sut = new AzureDevOpsWorkItemAccess(OrgUrl, Pat, new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"message":"The field 'System.State' contains the value 'Bogus' that is not in the list of supported values"}"""),
            }));

        var error = Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetStateAsync("42", "Bogus"))!;
        Assert.Multiple(() =>
        {
            Assert.That(error.Message, Does.Contain("400"));
            Assert.That(error.Message, Does.Contain("not in the list of supported values"));
            Assert.That(error.Message, Does.Not.Contain(Pat), "the PAT must never leak into diagnostics");
        });
    }

    [Test]
    public async Task GetAttachments_DownloadsAttachedFiles_AndAppendsTheDownloadQueryOnlyWhenMissing()
    {
        var attachments = await _sut.GetAttachmentsAsync("42");

        Assert.Multiple(() =>
        {
            Assert.That(attachments.Select(a => (a.FileName, a.Content)), Is.EqualTo(new[]
            {
                ("log.txt", new byte[] { 1, 2, 3 }),
                ("screenshot.png", new byte[] { 4, 5 }),
            }));
            Assert.That(_handler.Requests.Select(r => r.Uri), Does.Contain(
                $"{OrgUrl}/_apis/wit/attachments/att-guid-1?api-version=7.0&download=true"),
                "a relation URL without a query gets the download query appended");
            Assert.That(_handler.Requests.Select(r => r.Uri), Does.Contain(
                $"{OrgUrl}/_apis/wit/attachments/att-guid-2?fileName=screenshot.png"),
                "a relation URL that already carries a query is used as-is");
        });
    }

    [Test]
    public async Task GetAttachments_SkipsASingleFailedDownload_InsteadOfThrowing()
    {
        _failFirstAttachment = true;

        var attachments = await _sut.GetAttachmentsAsync("42");

        Assert.That(attachments.Select(a => a.FileName), Is.EqualTo(new[] { "screenshot.png" }));
    }

    [Test]
    public async Task GetAttachments_UnknownWorkItem_ReturnsEmpty()
    {
        Assert.That(await _sut.GetAttachmentsAsync("99"), Is.Empty);
    }
}
