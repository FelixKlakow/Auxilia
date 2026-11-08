using System.Text.Json;
using System.Text.Json.Nodes;

namespace Auxilia.DockerGuard.Tests;

/// <summary>
/// The guard lets a runner do exactly what a runner does — and a compromised runner nothing
/// more: no privileged or unconfined container, no host namespace, device, socket or foreign
/// bind (a host directory is only ever mounted beneath its root, resolved by the daemon), no
/// volume or network the host's own, no operation on another application's objects, no
/// exec/attach/commit, no build or deletion of anything but composed environments, no import —
/// and no smuggling any of it past the guard in a body, path or query the daemon reads differently.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class DockerApiPolicyTests
{
    private static readonly DockerGuardSettings Settings = new()
    {
        BindRoots = ["/var/lib/auxilia/workspaces", "/var/lib/auxilia/run-output"],
        OutboundNetworks = ["operator-net", "bridge"],
    };

    private FakeDockerObjects _objects = null!;

    [SetUp]
    public void SetUp() => _objects = new FakeDockerObjects();
    private DockerApiPolicy Policy => new(Settings, _objects);

    // ------------------------------------------------------------------ what a runner does

    [TestCase("GET", "/v1.47/_ping")]
    [TestCase("GET", "/v1.47/info")]
    [TestCase("GET", "/v1.47/containers/json")]
    [TestCase("GET", "/v1.47/containers/owned/json")]
    [TestCase("POST", "/v1.47/containers/owned/start")]
    [TestCase("POST", "/v1.47/containers/owned/wait")]
    [TestCase("GET", "/v1.47/containers/owned/logs")]
    [TestCase("PUT", "/v1.47/containers/owned/archive")]
    [TestCase("GET", "/v1.47/containers/owned/archive")]
    [TestCase("DELETE", "/v1.47/containers/owned")]
    [TestCase("GET", "/v1.47/images/registry.example:5000/team/wf@sha256:abc/json")]
    [TestCase("GET", "/v1.47/images/json")]
    [TestCase("DELETE", "/v1.47/images/auxilia-env:0123456789abcdef")]
    [TestCase("GET", "/v1.47/networks")]
    [TestCase("DELETE", "/v1.47/networks/auxilia-run-1")]
    [TestCase("GET", "/v1.47/volumes")]
    [TestCase("DELETE", "/v1.47/volumes/auxilia-pod-1-logs")]
    public async Task TheRunnersOwnCalls_Pass(string method, string path)
        => Assert.That((await DecideAsync(method, path)).Allowed, Is.True);

    [Test]
    public async Task AHardenedOwnedWorkflowContainer_MayBeCreated()
    {
        var decision = await CreateAsync(WorkflowContainer());
        Assert.That(decision.Allowed, Is.True, decision.Reason);
    }

    [Test]
    public async Task AHostBind_IsCarriedOutBeneathItsRoot_AsASubpathOfTheRootsVolume()
    {
        var body = WorkflowContainer();
        body["HostConfig"]!["Binds"] = new JsonArray(
            "/var/lib/auxilia/workspaces/run-1:/workspace",
            "/var/lib/auxilia/run-output/run-1/pkg:/workflow:ro",
            "auxilia-pod-1-logs:/logs");

        var decision = await CreateAsync(body);

        Assert.That(decision.Allowed, Is.True, decision.Reason);
        var host = JsonNode.Parse(decision.Body!)!["HostConfig"]!;
        Assert.That(host["Binds"]!.AsArray().Select(b => (string)b!), Is.EqualTo(new[] { "auxilia-pod-1-logs:/logs" }),
            "no host path reaches the daemon as a bind — a bind source follows symlinks");
        var mounts = host["Mounts"]!.AsArray().Select(m => m!.AsObject()).ToList();
        var workspace = mounts.Single(m => (string)m["Target"]! == "/workspace");
        var package = mounts.Single(m => (string)m["Target"]! == "/workflow");
        Assert.Multiple(() =>
        {
            Assert.That((string)workspace["Type"]!, Is.EqualTo("volume"));
            Assert.That((string)workspace["Source"]!, Is.EqualTo(DockerUpstream.RootVolumeName("/var/lib/auxilia/workspaces")));
            Assert.That((string)workspace["VolumeOptions"]!["Subpath"]!, Is.EqualTo("run-1"));
            Assert.That((bool)workspace["VolumeOptions"]!["NoCopy"]!, Is.True, "like a bind: no copy-up of image content");
            Assert.That((bool)workspace["ReadOnly"]!, Is.False);
            Assert.That((string)package["Source"]!, Is.EqualTo(DockerUpstream.RootVolumeName("/var/lib/auxilia/run-output")));
            Assert.That((string)package["VolumeOptions"]!["Subpath"]!, Is.EqualTo("run-1/pkg"));
            Assert.That((bool)package["ReadOnly"]!, Is.True);
            Assert.That(mounts.Count(m => (string)m["Target"]! == "/app"), Is.EqualTo(1), "the runner's own mounts stay");
            Assert.That(host["Memory"]!.GetValue<long>(), Is.EqualTo(8589934592), "everything else is forwarded as sent");
        });
    }

    [Test]
    public async Task AHostBind_IsRefused_WhenItsRootCannotBeMountedSafely()
    {
        _objects.RootVolumeFailure = "the Docker daemon (Engine API 1.43) cannot mount volume subpaths";
        var decision = await CreateAsync(WorkflowContainer());
        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Reason, Does.Contain("cannot mount volume subpaths"));
    }

    [Test]
    public async Task ARefusedContainer_ProvisionsNoRootVolume()
    {
        var body = WorkflowContainer();
        body["HostConfig"]!["Privileged"] = true;
        await CreateAsync(body);
        Assert.That(_objects.RootVolumeRequests, Is.Empty);
    }

    [Test]
    public async Task ACompanionOnItsPodNetwork_WithItsPodVolume_MayBeCreated()
    {
        var body = WorkflowContainer();
        body["Labels"] = new JsonObject { ["auxilia.companion"] = "1" };
        body["HostConfig"]!["Binds"] = new JsonArray("auxilia-pod-1-logs:/logs");
        body["NetworkingConfig"] = new JsonObject
        {
            ["EndpointsConfig"] = new JsonObject { ["auxilia-pod-1"] = new JsonObject() }
        };
        Assert.That(await CreateAsync(body), Is.EqualTo(GuardDecision.Allow));
    }

    [Test]
    public async Task TheRunnersNetworkVolumeAndMembershipCalls_Pass()
    {
        Assert.Multiple(async () =>
        {
            Assert.That((await DecideAsync("POST", "/networks/create",
                new JsonObject { ["Name"] = "auxilia-run-2", ["Internal"] = true, ["Labels"] = Labels("auxilia.egress") })).Allowed, Is.True);
            Assert.That((await DecideAsync("POST", "/volumes/create",
                new JsonObject { ["Name"] = "auxilia-pod-2-logs", ["Labels"] = Labels("auxilia.companion") })).Allowed, Is.True);
            Assert.That((await DecideAsync("POST", "/networks/operator-net/connect",
                new JsonObject { ["Container"] = "gateway" })).Allowed, Is.True, "the gateway's outbound leg");
            Assert.That((await DecideAsync("POST", "/networks/auxilia-pod-1/connect",
                new JsonObject { ["Container"] = "owned" })).Allowed, Is.True, "a workflow container joins its pod");
            Assert.That((await DecideAsync("POST", "/images/create",
                query: Query(("fromImage", "registry.example/team/wf@sha256:abc")))).Allowed, Is.True);
            Assert.That((await DecideAsync("POST", "/build", query: Query(("t", "auxilia-env:0123456789abcdef")))).Allowed, Is.True);
        });
    }

    // ------------------------------------------------------------------ escalation through a create

    [TestCase("Privileged", true)]
    [TestCase("PublishAllPorts", true)]
    [TestCase("OomKillDisable", true)]
    public async Task AContainerThatLiftsAProtection_IsRefused(string field, bool value)
    {
        var body = WorkflowContainer();
        body["HostConfig"]![field] = value;
        Assert.That((await CreateAsync(body)).Allowed, Is.False);
    }

    [TestCase("NetworkMode", "host")]
    [TestCase("NetworkMode", "container:foreign")]
    [TestCase("NetworkMode", "other-app-net")]
    [TestCase("PidMode", "host")]
    [TestCase("IpcMode", "host")]
    [TestCase("UTSMode", "host")]
    [TestCase("UsernsMode", "host")]
    [TestCase("CgroupnsMode", "host")]
    [TestCase("CgroupParent", "/system.slice")]
    [TestCase("VolumeDriver", "convoy")]
    public async Task AContainerSharingTheHostsOrAnotherAppsWorld_IsRefused(string field, string value)
    {
        var body = WorkflowContainer();
        body["HostConfig"]![field] = value;
        Assert.That((await CreateAsync(body)).Allowed, Is.False);
    }

    [Test]
    public async Task ExtraCapabilities_AreRefused_TheRetainedSetPasses()
    {
        var body = WorkflowContainer();
        body["HostConfig"]!["CapAdd"] = new JsonArray("CAP_CHOWN", "kill", "SYS_ADMIN");
        Assert.That((await CreateAsync(body)).Reason, Does.Contain("SYS_ADMIN"));
    }

    [TestCase("\"ALL\"")]
    [TestCase("\"SYS_MODULE\"")]
    [TestCase("[\"CHOWN\", 7]")]
    public async Task CapAddAsAString_IsReadLikeTheDaemonReadsIt(string capAdd)
    {
        // The daemon's string-slice type takes a bare string as a one-element list.
        var body = WorkflowContainer();
        body["HostConfig"]!["CapAdd"] = JsonNode.Parse(capAdd);
        Assert.That((await CreateAsync(body)).Allowed, Is.False, capAdd);
    }

    [Test]
    public async Task WhatAContainerKeeps_DecidesNotWhatItAdds()
    {
        var keepsDefaults = WorkflowContainer();
        ((JsonObject)keepsDefaults["HostConfig"]!).Remove("CapDrop");
        ((JsonObject)keepsDefaults["HostConfig"]!).Remove("CapAdd");
        var companion = WorkflowContainer();
        companion["HostConfig"]!["CapDrop"] = new JsonArray("NET_RAW", "MKNOD", "SYS_CHROOT", "SETFCAP", "SETPCAP", "AUDIT_WRITE");
        ((JsonObject)companion["HostConfig"]!).Remove("CapAdd");
        var dropAllAsString = WorkflowContainer();
        dropAllAsString["HostConfig"]!["CapDrop"] = "ALL";

        Assert.Multiple(async () =>
        {
            Assert.That((await CreateAsync(keepsDefaults)).Reason, Does.Contain("MKNOD").Or.Contain("NET_RAW"),
                "a container that drops nothing keeps Docker's defaults — raw sockets and device nodes included");
            Assert.That((await CreateAsync(companion)).Allowed, Is.True, "the runner's companions drop exactly the dangerous defaults");
            Assert.That((await CreateAsync(dropAllAsString)).Allowed, Is.True, "a bare string is a one-element list for the daemon");
        });
    }

    [Test]
    public async Task AnEndpointNamingANetworkById_IsRefused_OnCreateAndOnConnect()
    {
        // The daemon attaches to EndpointSettings.NetworkID, whatever the endpoint's name says.
        var body = WorkflowContainer();
        body["NetworkingConfig"] = new JsonObject
        {
            ["EndpointsConfig"] = new JsonObject { ["auxilia-run-1"] = new JsonObject { ["NetworkID"] = "n4" } }
        };
        Assert.Multiple(async () =>
        {
            Assert.That((await CreateAsync(body)).Reason, Does.Contain("NetworkID"));
            Assert.That((await DecideAsync("POST", "/networks/auxilia-pod-1/connect", new JsonObject
            {
                ["Container"] = "owned",
                ["EndpointConfig"] = new JsonObject { ["NetworkID"] = "n4" }
            })).Allowed, Is.False);
            Assert.That((await DecideAsync("POST", "/networks/auxilia-pod-1/connect", new JsonObject
            {
                ["Container"] = "owned",
                ["EndpointConfig"] = new JsonObject { ["Aliases"] = new JsonArray("workflow") }
            })).Allowed, Is.True, "aliases are what the runner sets");
        });
    }

    [Test]
    public async Task TheRuntime_IsTheDaemonsDefault_OrOneTheGuardLists()
    {
        var runc = WorkflowContainer();
        runc["HostConfig"]!["Runtime"] = "runc";
        var runsc = WorkflowContainer();
        runsc["HostConfig"]!["Runtime"] = "runsc";
        var sandboxed = new DockerApiPolicy(new DockerGuardSettings
        {
            BindRoots = Settings.BindRoots,
            OutboundNetworks = Settings.OutboundNetworks,
            Runtimes = ["runsc"],
        }, _objects);
        Task<GuardDecision> UnderSandbox(JsonNode node)
            => sandboxed.DecideAsync("POST", "/containers/create", new Dictionary<string, string[]>(), JsonDocument.Parse(node.ToJsonString()).RootElement.Clone(), CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That((await CreateAsync(runc)).Allowed, Is.False, "without listed runtimes only the daemon's default runs");
            Assert.That((await CreateAsync(WorkflowContainer())).Allowed, Is.True);
            Assert.That((await UnderSandbox(runsc)).Allowed, Is.True);
            Assert.That((await UnderSandbox(runc)).Allowed, Is.False, "naming runc would leave the sandbox");
            Assert.That((await UnderSandbox(WorkflowContainer())).Allowed, Is.False, "and so would naming nothing");
        });
    }

    [Test]
    public async Task NoNewPrivileges_IsMandatory_AndUnconfinedProfilesAreRefused()
    {
        var withoutNnp = WorkflowContainer();
        withoutNnp["HostConfig"]!["SecurityOpt"] = new JsonArray();
        var unconfined = WorkflowContainer();
        unconfined["HostConfig"]!["SecurityOpt"] = new JsonArray("no-new-privileges:true", "seccomp=unconfined");
        Assert.That((await CreateAsync(withoutNnp)).Allowed, Is.False);
        Assert.That((await CreateAsync(unconfined)).Allowed, Is.False);
    }

    [TestCase("Devices", "[{\"PathOnHost\":\"/dev/kmsg\"}]")]
    [TestCase("DeviceRequests", "[{\"Driver\":\"nvidia\"}]")]
    [TestCase("Sysctls", "{\"kernel.core_pattern\":\"|/tmp/x\"}")]
    [TestCase("MaskedPaths", "[]")]
    [TestCase("ReadonlyPaths", "[]")]
    [TestCase("VolumesFrom", "[\"foreign\"]")]
    [TestCase("Annotations", "{\"org.systemd.property.x\":\"y\"}")]
    [TestCase("LogConfig", "{\"Type\":\"syslog\",\"Config\":{\"syslog-address\":\"tcp://collector:514\"}}")]
    [TestCase("PortBindings", "{\"7681/tcp\":[{\"HostIp\":\"0.0.0.0\",\"HostPort\":\"\"}]}")]
    [TestCase("PortBindings", "{\"7681/tcp\":[{\"HostIp\":\"localhost\",\"HostPort\":\"\"}]}")]
    [TestCase("PortBindings", "{\"7681/tcp\":[{\"HostPort\":\"7681\"}]}")]
    public async Task FieldsTheRunnerNeverSets_AreRefused(string field, string json)
    {
        var body = WorkflowContainer();
        body["HostConfig"]![field] = JsonNode.Parse(json);
        Assert.That((await CreateAsync(body)).Allowed, Is.False, field);
    }

    [TestCase("/var/run/docker.sock:/var/run/docker.sock")]
    [TestCase("/:/host")]
    [TestCase("/etc:/x:ro")]
    [TestCase("/var/lib/auxilia/workspaces/../../../etc:/x")]
    [TestCase("/var/lib/auxilia/workspaces-evil:/x")]
    [TestCase("database-data:/var/lib/data")]
    [TestCase("/var/lib/auxilia/workspaces/run-1:/workspace:rshared")]
    [TestCase("/var/lib/auxilia/workspaces/run-1:/workspace:z")]
    [TestCase("/var/lib/auxilia/workspaces:/all-workspaces")]
    [TestCase(@"C:\auxilia\workspaces\run-1:/workspace")]
    [TestCase("auxilia-pod-1-logs:/logs:rshared")]
    public async Task BindsBeyondTheRunnersRoots_AreRefused(string bind)
    {
        var body = WorkflowContainer();
        body["HostConfig"]!["Binds"] = new JsonArray(bind);
        Assert.That((await CreateAsync(body)).Allowed, Is.False, bind);
    }

    [Test]
    public async Task AMountBeyondTheRunnersRoots_IsRefused_TmpfsAndAnonymousVolumesPass()
    {
        var hostMount = WorkflowContainer();
        hostMount["HostConfig"]!["Mounts"] = new JsonArray(new JsonObject { ["Type"] = "bind", ["Source"] = "/etc", ["Target"] = "/x" });
        var npipe = WorkflowContainer();
        npipe["HostConfig"]!["Mounts"] = new JsonArray(new JsonObject { ["Type"] = "npipe", ["Source"] = @"\\.\pipe\docker_engine", ["Target"] = "/x" });
        var mountBelowRoot = WorkflowContainer();
        mountBelowRoot["HostConfig"]!["Mounts"] = new JsonArray(new JsonObject
            { ["Type"] = "bind", ["Source"] = "/var/lib/auxilia/workspaces/run-1", ["Target"] = "/x" });
        Assert.That((await CreateAsync(hostMount)).Allowed, Is.False);
        Assert.That((await CreateAsync(npipe)).Allowed, Is.False);
        Assert.That((await CreateAsync(mountBelowRoot)).Allowed, Is.False,
            "a bind-type mount would follow a symlink — host directories go through Binds, carried out beneath their root");
        Assert.That((await CreateAsync(WorkflowContainer())).Allowed, Is.True, "the runner's own tmpfs + anonymous volumes");
    }

    [Test]
    public async Task AVolumeWithDriverOptions_OrAPartOfAVolume_IsRefused()
    {
        // The local driver with "o=bind,device=/" is the host's root — no name needed.
        var hostRootVolume = WorkflowContainer();
        hostRootVolume["HostConfig"]!["Mounts"] = new JsonArray(new JsonObject
        {
            ["Type"] = "volume", ["Target"] = "/host",
            ["VolumeOptions"] = new JsonObject
            {
                ["DriverConfig"] = new JsonObject
                {
                    ["Name"] = "",
                    ["Options"] = new JsonObject { ["type"] = "none", ["o"] = "bind", ["device"] = "/" }
                }
            }
        });
        var subpath = WorkflowContainer();
        subpath["HostConfig"]!["Mounts"] = new JsonArray(new JsonObject
        {
            ["Type"] = "volume", ["Source"] = DockerUpstream.RootVolumeName("/var/lib/auxilia/workspaces"), ["Target"] = "/x",
            ["VolumeOptions"] = new JsonObject { ["Subpath"] = "run-2" }
        });
        Assert.Multiple(async () =>
        {
            Assert.That((await CreateAsync(hostRootVolume)).Allowed, Is.False);
            Assert.That((await CreateAsync(subpath)).Allowed, Is.False, "the runner never names a root volume itself");
            Assert.That((await DecideAsync("POST", "/volumes/create", new JsonObject
            {
                ["Name"] = "auxilia-pod-2-logs", ["Labels"] = Labels("auxilia.companion"),
                ["DriverOpts"] = new JsonObject { ["type"] = "none", ["o"] = "bind", ["device"] = "/" }
            })).Allowed, Is.False, "an owned volume that binds the host root");
            Assert.That((await DecideAsync("POST", "/volumes/create", new JsonObject
            {
                ["Name"] = "auxilia-pod-2-logs", ["Labels"] = Labels("auxilia.companion"), ["Driver"] = "rclone"
            })).Allowed, Is.False);
        });
    }

    [TestCase("seccomp=unconfined")]
    [TestCase("seccomp={\"defaultAction\":\"SCMP_ACT_ALLOW\"}")]
    [TestCase("apparmor=unconfined")]
    [TestCase("apparmor=lxc-container-default-with-nesting")]
    [TestCase("label=type:spc_t")]
    [TestCase("label=disable")]
    [TestCase("systempaths=unconfined")]
    [TestCase("writable-cgroups=true")]
    public async Task EverySecurityOptionButNoNewPrivileges_IsRefused(string option)
    {
        var body = WorkflowContainer();
        body["HostConfig"]!["SecurityOpt"] = new JsonArray("no-new-privileges:true", option);
        Assert.That((await CreateAsync(body)).Allowed, Is.False, option);
    }

    [TestCase("{\"Driver\":\"macvlan\",\"Options\":{\"parent\":\"eth0\"}}")]
    [TestCase("{\"Driver\":\"ipvlan\"}")]
    [TestCase("{\"Options\":{\"com.docker.network.bridge.gateway_mode_ipv4\":\"routed\"}}")]
    [TestCase("{\"IPAM\":{\"Driver\":\"dhcp\"}}")]
    [TestCase("{\"ConfigOnly\":true}")]
    [TestCase("{\"Scope\":\"swarm\"}")]
    public async Task ANetworkBeyondAnInternalDefaultBridge_IsRefused(string fields)
    {
        var body = new JsonObject { ["Name"] = "auxilia-run-2", ["Internal"] = true, ["Labels"] = Labels("auxilia.egress") };
        foreach (var (key, value) in JsonNode.Parse(fields)!.AsObject())
            body[key] = value!.DeepClone();
        Assert.That((await DecideAsync("POST", "/networks/create", body)).Allowed, Is.False, fields);
    }

    [Test]
    public async Task AnUnownedContainer_IsNeverCreated()
    {
        var body = WorkflowContainer();
        body["Labels"] = new JsonObject { ["com.example.app"] = "web" };
        Assert.That((await CreateAsync(body)).Allowed, Is.False);
    }

    // ------------------------------------------------------------------ bodies the daemon reads differently

    [Test]
    public async Task AFieldInAnotherCase_IsReadLikeTheDaemonReadsIt()
    {
        var body = WorkflowContainer();
        var host = (JsonObject)body["HostConfig"]!;
        host.Remove("Privileged");
        host["privileged"] = true;
        Assert.That((await CreateAsync(body)).Reason, Does.Contain("privileged"),
            "the daemon matches field names case-insensitively — so does the guard");
    }

    [Test]
    public async Task ADuplicateField_IsRefused()
    {
        var json = WorkflowContainer().ToJsonString().Replace("\"HostConfig\":{", "\"HostConfig\":{\"Privileged\":false,\"PRIVILEGED\":true,");
        Assert.That((await DecideAsync("POST", "/containers/create", JsonNode.Parse(json))).Allowed, Is.False);
    }

    [Test]
    public async Task ANonAsciiFieldName_IsRefused()
    {
        // The daemon folds U+017F (long s) to 's': "Bindſ" would be its "Binds".
        var body = WorkflowContainer();
        body["HostConfig"]!["Bind\u017F"] = new JsonArray("/:/host");
        Assert.That((await CreateAsync(body)).Allowed, Is.False);
    }

    [Test]
    public async Task ADeprecatedTopLevelHostField_IsRefused()
    {
        var body = WorkflowContainer();
        body["Privileged"] = true;
        Assert.That((await CreateAsync(body)).Allowed, Is.False, "older daemons still read host fields at the top level");
    }

    // ------------------------------------------------------------------ other applications' objects

    [TestCase("GET", "/containers/foreign/json")]
    [TestCase("GET", "/containers/foreign/logs")]
    [TestCase("GET", "/containers/foreign/archive")]
    [TestCase("POST", "/containers/foreign/stop")]
    [TestCase("DELETE", "/containers/foreign")]
    [TestCase("DELETE", "/containers/unknown")]
    [TestCase("DELETE", "/networks/other-app-net")]
    [TestCase("DELETE", "/networks/operator-net")]
    [TestCase("DELETE", "/volumes/database-data")]
    public async Task AnotherApplicationsObjects_AreUntouchable(string method, string path)
        => Assert.That((await DecideAsync(method, path)).Allowed, Is.False);

    [Test]
    public async Task NetworkMembership_IsOnlyForOwnedContainers_OnOwnedOrOutboundNetworks()
    {
        Assert.Multiple(async () =>
        {
            Assert.That((await DecideAsync("POST", "/networks/operator-net/connect",
                new JsonObject { ["Container"] = "foreign" })).Allowed, Is.False);
            Assert.That((await DecideAsync("POST", "/networks/other-app-net/connect",
                new JsonObject { ["Container"] = "owned" })).Allowed, Is.False);
        });
    }

    // ------------------------------------------------------------------ operations a runner never makes

    [TestCase("POST", "/containers/owned/exec")]
    [TestCase("POST", "/containers/owned/attach")]
    [TestCase("POST", "/containers/owned/update")]
    [TestCase("POST", "/commit")]
    [TestCase("POST", "/exec/abc/start")]
    [TestCase("POST", "/images/auxilia-egress-gateway:latest/tag")]
    [TestCase("POST", "/images/load")]
    [TestCase("POST", "/containers/prune")]
    [TestCase("GET", "/events")]
    [TestCase("POST", "/swarm/init")]
    [TestCase("POST", "/plugins/pull")]
    [TestCase("DELETE", "/images/auxilia-egress-gateway:latest")]
    public async Task OperationsARunnerNeverMakes_AreRefused(string method, string path)
        => Assert.That((await DecideAsync(method, path)).Allowed, Is.False);

    [Test]
    public async Task BuildsAndImports_CannotRePointAnImageThePlatformLaunchesByName()
    {
        Assert.Multiple(async () =>
        {
            Assert.That((await DecideAsync("POST", "/build", query: Query(("t", "auxilia-egress-gateway:latest")))).Allowed, Is.False);
            Assert.That((await DecideAsync("POST", "/build", query: Query(("t", "auxilia-env:a"), ("t", "nginx:latest")))).Allowed, Is.False);
            Assert.That((await DecideAsync("POST", "/build", query: Query(("t", "auxilia-env:a"), ("networkmode", "host")))).Allowed, Is.False);
            Assert.That((await DecideAsync("POST", "/build", query: Query(("t", "auxilia-env:a"), ("networkmode", "other-app-net")))).Allowed, Is.False,
                "a build joins no other application's network");
            Assert.That((await DecideAsync("POST", "/build", query: Query(("t", "auxilia-env:a"), ("cgroupparent", "/system.slice")))).Allowed, Is.False);
            Assert.That((await DecideAsync("POST", "/images/create", query: Query(("fromImage", "postgres"), ("tag", "16")))).Allowed, Is.True,
                "a pull changes only the name it fetches, from that name's registry (companions are pulled by tag)");
            Assert.That((await DecideAsync("POST", "/images/create", query: Query(("fromSrc", "-"), ("repo", "auxilia-egress-gateway"), ("tag", "latest")))).Allowed, Is.False,
                "an import names its own repository and tag");
            Assert.That((await DecideAsync("POST", "/networks/create",
                new JsonObject { ["Name"] = "x", ["Labels"] = Labels("auxilia.egress") })).Allowed, Is.False, "runner networks are internal");
            Assert.That((await DecideAsync("POST", "/volumes/create", new JsonObject { ["Name"] = "x" })).Allowed, Is.False);
        });
    }

    // ------------------------------------------------------------------ paths and queries the daemon reads differently

    [TestCase("DELETE", "/images/auxilia-env:x%2F..%2F..%2Fcontainers%2Fforeign")]
    [TestCase("POST", "/containers/owned%2F..%2Fforeign/stop")]
    [TestCase("POST", "/containers/owned/../foreign/stop")]
    [TestCase("GET", "/containers/owned\\..\\foreign/json")]
    public async Task APathTheDaemonWouldDecodeDifferently_IsRefused(string method, string path)
        => Assert.That((await DecideAsync(method, path)).Allowed, Is.False, path);

    [TestCase("/v1.24/containers/owned/json")]
    [TestCase("/v1.44/containers/owned/json")]
    [TestCase("/v1/containers/owned/json")]
    public async Task AnOldApiVersion_IsRefused(string path)
        => Assert.That((await DecideAsync("GET", path)).Allowed, Is.False, "old versions carry compatibility behaviour");

    [Test]
    public async Task TheQuery_IsReadCaseSensitively_LikeTheDaemonReadsIt()
    {
        var query = DockerApiPolicy.ParseQuery("?t=auxilia-env:a&NetworkMode=bridge&networkmode=host&T=nginx%3Alatest");

        Assert.That(query["networkmode"], Is.EqualTo(new[] { "host" }), "the daemon reads exactly this key");
        Assert.That(query["T"], Is.EqualTo(new[] { "nginx:latest" }));
        Assert.That((await DecideAsync("POST", "/build", query: query)).Allowed, Is.False);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A body shaped like the runner's baked-image workflow container (DockerWorkflowLauncher).</summary>
    private static JsonObject WorkflowContainer() => new()
    {
        ["Image"] = "registry.example/team/wf@sha256:abc",
        ["Env"] = new JsonArray("Workflow__InstanceId=1"),
        ["Labels"] = Labels("auxilia.workflow"),
        ["HostConfig"] = new JsonObject
        {
            ["Binds"] = new JsonArray("/var/lib/auxilia/workspaces/run-1:/workspace", "/var/lib/auxilia/run-output/run-1:/workflow-output"),
            ["CapDrop"] = new JsonArray("ALL"),
            ["CapAdd"] = new JsonArray("CHOWN", "DAC_OVERRIDE", "FOWNER", "SETUID", "SETGID", "KILL"),
            ["SecurityOpt"] = new JsonArray("no-new-privileges:true"),
            ["ReadonlyRootfs"] = true,
            ["Tmpfs"] = new JsonObject { ["/tmp"] = "rw,mode=1777,size=2048m" },
            ["Mounts"] = new JsonArray(
                new JsonObject { ["Type"] = "volume", ["Target"] = "/home/auxilia-agent", ["VolumeOptions"] = new JsonObject { ["Labels"] = Labels("auxilia.workflow") } },
                new JsonObject { ["Type"] = "volume", ["Target"] = "/app" }),
            ["Memory"] = 8589934592,
            ["MemorySwap"] = 8589934592,
            ["PidsLimit"] = 4096,
            ["Privileged"] = false,
            ["PortBindings"] = new JsonObject
            {
                ["7681/tcp"] = new JsonArray(new JsonObject { ["HostIp"] = "127.0.0.1", ["HostPort"] = "" })
            },
        },
        ["NetworkingConfig"] = new JsonObject
        {
            ["EndpointsConfig"] = new JsonObject { ["auxilia-run-1"] = new JsonObject { ["Aliases"] = new JsonArray("workflow") } }
        },
    };

    private static JsonObject Labels(string kind) => new() { [kind] = "1", ["auxilia.instance-id"] = Guid.NewGuid().ToString() };

    private static Dictionary<string, string[]> Query(params (string Key, string Value)[] pairs)
        => pairs.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Select(p => p.Value).ToArray());

    private Task<GuardDecision> CreateAsync(JsonNode body) => DecideAsync("POST", "/v1.47/containers/create", body);

    private Task<GuardDecision> DecideAsync(
        string method, string path, JsonNode? body = null, Dictionary<string, string[]>? query = null)
    {
        JsonElement? element = body is null ? null : JsonDocument.Parse(body.ToJsonString()).RootElement.Clone();
        return Policy.DecideAsync(method, path, query ?? [], element, CancellationToken.None);
    }
}
