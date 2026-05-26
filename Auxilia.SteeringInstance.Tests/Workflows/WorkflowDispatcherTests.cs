using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.SteeringInstance.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowDispatcherTests
{
    private Mock<IMessageBusClient> _mockBus = null!;
    private Mock<IWorkflowLauncher> _mockLauncher = null!;
    private Func<RunWorkflowCommand, CancellationToken, Task>? _capturedHandler;
    private WorkflowDispatcher _sut = null!;

    private static DockerWorkflowLauncherSettings DefaultSettings() => new()
    {
        NetworkName = "test-net",
        RabbitMqHost = "rabbitmq",
        RabbitMqPort = 5672,
        RabbitMqUserName = "guest",
        RabbitMqPassword = "guest"
    };

    [SetUp]
    public async Task SetUp()
    {
        _mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);
        _mockLauncher = new Mock<IWorkflowLauncher>(MockBehavior.Strict);

        var disposable = new Mock<IAsyncDisposable>();
        disposable.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockBus
            .Setup(b => b.DeclareQueueAsync("workflow.run-commands", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockBus
            .Setup(b => b.SubscribeAsync<RunWorkflowCommand>(
                "workflow.run-commands",
                It.IsAny<Func<RunWorkflowCommand, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<RunWorkflowCommand, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _capturedHandler = handler)
            .ReturnsAsync(disposable.Object);

        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new WorkflowDispatcher(
            _mockBus.Object,
            _mockLauncher.Object,
            Options.Create(DefaultSettings()),
            NullLogger<WorkflowDispatcher>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown() => await _sut.StopAsync();

    [Test]
    public void WhenStartCalled_DeclaresRunCommandsQueue()
    {
        _mockBus.Verify(
            b => b.DeclareQueueAsync("workflow.run-commands", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void WhenStartCalled_SubscribesToRunCommandsQueue()
    {
        _mockBus.Verify(
            b => b.SubscribeAsync<RunWorkflowCommand>(
                "workflow.run-commands",
                It.IsAny<Func<RunWorkflowCommand, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WhenRunCommandReceived_CallsLauncherExactlyOnce()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "auxilia-my-workflow:latest",
            new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        _mockLauncher.Verify(l => l.LaunchAsync(
            It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WhenRunCommandReceived_PassesCorrectImageToLauncher()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "auxilia-my-workflow:latest",
            new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Image, Is.EqualTo("auxilia-my-workflow:latest"));
    }

    [Test]
    public async Task WhenRunCommandReceived_InjectsRabbitMqEnvVars()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "image:latest", new Dictionary<string, string>());

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        var env = captured!.EnvironmentVariables;
        Assert.That(env["RabbitMq__Host"], Is.EqualTo("rabbitmq"));
        Assert.That(env["RabbitMq__Port"], Is.EqualTo("5672"));
        Assert.That(env["RabbitMq__UserName"], Is.EqualTo("guest"));
        Assert.That(env["RabbitMq__Password"], Is.EqualTo("guest"));
    }

    [Test]
    public async Task WhenRunCommandHasContext_ForwardsContextAsUpperCaseEnvVars()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "image:latest",
            new Dictionary<string, string>
            {
                ["repo_url"] = "https://github.com/example/repo",
                ["branch"]   = "main"
            });

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured, Is.Not.Null);
        var env = captured!.EnvironmentVariables;
        Assert.That(env.ContainsKey("WORKFLOW_CONTEXT__REPO_URL"), Is.True);
        Assert.That(env["WORKFLOW_CONTEXT__REPO_URL"], Is.EqualTo("https://github.com/example/repo"));
        Assert.That(env.ContainsKey("WORKFLOW_CONTEXT__BRANCH"), Is.True);
        Assert.That(env["WORKFLOW_CONTEXT__BRANCH"], Is.EqualTo("main"));
    }

    [Test]
    public async Task WhenRunCommandHasContext_OriginalKeysDoNotAppearWithoutPrefix()
    {
        WorkflowLaunchRequest? captured = null;
        _mockLauncher
            .Setup(l => l.LaunchAsync(It.IsAny<WorkflowLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLaunchRequest, CancellationToken>((req, _) => captured = req)
            .Returns(Task.CompletedTask);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "image:latest",
            new Dictionary<string, string> { ["MY_KEY"] = "value" });

        await _capturedHandler!(command, CancellationToken.None);

        Assert.That(captured!.EnvironmentVariables.ContainsKey("MY_KEY"), Is.False);
    }
}

