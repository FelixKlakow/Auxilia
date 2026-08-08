using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The approval-scoped path of <c>GET /api/workflow-types/{type}/package</c>: the verdict run
/// holds no principal credential and no run resolution token for the package under review, so
/// the approval pipeline mints a scoped download token. That token fetches exactly THE pending
/// package it was minted for — nothing else, and nothing once the type is no longer Pending.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class PendingPackageDownloadTests : CoreApiComponentTestBase
{
    private string _packageDir = null!;

    protected override void ConfigureHost(IWebHostBuilder builder)
    {
        _packageDir = Path.Combine(Path.GetTempPath(), "auxilia-pkg-dl-tests", Guid.NewGuid().ToString("N"));
        builder.UseSetting("CoreApi:PackageStoreDirectory", _packageDir);
    }

    [TearDown]
    public void DeletePackages()
    {
        if (Directory.Exists(_packageDir))
            Directory.Delete(_packageDir, recursive: true);
    }

    /// <summary>A minimal signed workflow package (untrusted publisher key → registers as Pending).</summary>
    private static byte[] BuildSignedPackage(string workflowName)
    {
        using var rsa = RSA.Create(2048);
        var payload = Encoding.UTF8.GetBytes("payload");
        var schemaBytes = JsonSerializer.SerializeToUtf8Bytes(
            new WorkflowSchema(workflowName, [], []) { Version = "1.0.0" },
            WorkflowPackageJsonOptions.SerializeOptions);

        var unsigned = new WorkflowPackageManifest(
            [
                new WorkflowPackageFileEntry("wf.exe", Convert.ToBase64String(SHA256.HashData(payload))),
                new WorkflowPackageFileEntry("workflow-schema.json", Convert.ToBase64String(SHA256.HashData(schemaBytes)))
            ],
            string.Empty, Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), "wf.exe");
        var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsigned, WorkflowPackageJsonOptions.SerializeOptions);
        var manifest = unsigned with
        {
            SignatureBase64 = Convert.ToBase64String(rsa.SignHash(
                SHA256.HashData(unsignedBytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        };

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] bytes)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bytes);
            }
            Add("wf.exe", payload);
            Add("workflow-schema.json", schemaBytes);
            Add("package-manifest.json",
                JsonSerializer.SerializeToUtf8Bytes(manifest, WorkflowPackageJsonOptions.SerializeOptions));
        }
        return buffer.ToArray();
    }

    private async Task<HttpClient> RegisterPendingUploadAsync(string type)
    {
        var client = CreateClient();
        var register = await client.PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest(type,
                PackageBase64: Convert.ToBase64String(BuildSignedPackage(type))));
        Assert.That(register.IsSuccessStatusCode, Is.True,
            await register.Content.ReadAsStringAsync());
        var registration = await register.Content.ReadFromJsonAsync<WorkflowTypeRegistrationDto>();
        Assert.That(registration!.Status, Is.EqualTo(WorkflowTypeStatus.Pending),
            "an untrusted publisher key must enter Pending");
        return client;
    }

    private string IssueToken(string type)
        => Factory.Services.GetRequiredService<PendingPackageDownloadTokenService>().Issue(type);

    [Test]
    public async Task ApprovalToken_DownloadsThePendingPackage()
    {
        await RegisterPendingUploadAsync("under-review");

        var response = await CreateAnonymousClient().GetAsync(
            $"/api/workflow-types/under-review/package?approvalToken={Uri.EscapeDataString(IssueToken("under-review"))}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/zip"));
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Not.Empty);
    }

    [Test]
    public async Task ApprovalToken_OfAnotherType_IsRefused()
    {
        await RegisterPendingUploadAsync("under-review");

        var response = await CreateAnonymousClient().GetAsync(
            $"/api/workflow-types/under-review/package?approvalToken={Uri.EscapeDataString(IssueToken("other-type"))}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            "the token is scoped to exactly one pending package");
    }

    [Test]
    public async Task ApprovalToken_AfterApproval_IsRefused()
    {
        var client = await RegisterPendingUploadAsync("under-review");
        var token = IssueToken("under-review");
        var approve = await client.PostAsync("/api/workflow-types/under-review/approve", null);
        Assert.That(approve.IsSuccessStatusCode, Is.True);

        var response = await CreateAnonymousClient().GetAsync(
            $"/api/workflow-types/under-review/package?approvalToken={Uri.EscapeDataString(token)}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            "the token is time-limited to the approval evaluation — once decided, it is dead");
    }

    [Test]
    public async Task NoToken_IsRefused()
    {
        await RegisterPendingUploadAsync("under-review");

        var response = await CreateAnonymousClient().GetAsync("/api/workflow-types/under-review/package");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}
