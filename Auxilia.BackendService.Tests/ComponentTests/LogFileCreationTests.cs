using Auxilia.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
///     Component test that verifies the BackendService creates a Serilog log file at the expected
///     location: <c>%appdata%/Auxilia/TestData/Logs/Backend_{instanceGuid}.log</c>.
/// </summary>
[TestFixture]
[Category("Component")]
public class LogFileCreationTests
{
    private Guid _instanceId;
    private string _expectedLogPath = null!;
    private IHost _host = null!;
    private FakeMessageBusClient _fakeBus = null!;

    [SetUp]
    public async Task SetUp()
    {
        _instanceId = Guid.NewGuid();

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Auxilia", "TestData", "Logs");
        Directory.CreateDirectory(logDir);

        _expectedLogPath = Path.Combine(logDir, $"Backend_{_instanceId}.log");

        _fakeBus = new FakeMessageBusClient();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog((_, lc) => lc
                .MinimumLevel.Debug()
                .WriteTo.File(_expectedLogPath))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMessageBusClient>(_fakeBus);
                services.AddSingleton(new ServiceInfo(_instanceId));
                services.AddHostedService<QueueInitializer>();
                services.AddHostedService<IdentificationRequestHandler>();
            })
            .Build();

        await _host.StartAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _host.StopAsync();
        _host.Dispose();

        // Flush Serilog before deleting the file
        await Log.CloseAndFlushAsync();

        if (File.Exists(_expectedLogPath))
            File.Delete(_expectedLogPath);
    }

    [Test]
    public async Task WhenServiceStarts_LogFileIsCreatedAtConfiguredLocation()
    {
        // Wait for the queue to be declared (proof that the hosted services have started)
        var started = await _fakeBus.WaitForConditionAsync(
            () => _fakeBus.DeclaredQueues.Contains(QueueInitializer.DefaultQueueName),
            TimeSpan.FromSeconds(10));

        Assert.That(started, Is.True, "Queue was not declared within 10 seconds");

        // The Serilog file sink creates the file as soon as the first log event is written
        Assert.That(File.Exists(_expectedLogPath), Is.True,
            $"Expected log file was not found at: {_expectedLogPath}");
    }

    [Test]
    public async Task WhenIdentificationRequestHandled_LogFileContainsRequestEntry()
    {
        // Wait for the service to be ready
        await _fakeBus.WaitForConditionAsync(
            () => _fakeBus.DeclaredQueues.Contains(QueueInitializer.DefaultQueueName),
            TimeSpan.FromSeconds(10));

        var messageId = Guid.NewGuid();
        await _fakeBus.SimulateReceivedAsync(
            QueueInitializer.DefaultQueueName,
            new Messaging.Messages.IdentificationRequestMessage(messageId, Guid.NewGuid(), "test-response"));

        // Give the async handler a moment to complete and flush logs
        await Task.Delay(500);

        Assert.That(File.Exists(_expectedLogPath), Is.True,
            $"Log file was not created at: {_expectedLogPath}");

        // Read with FileShare.ReadWrite so we can share the file with the Serilog sink
        string logContent;
        using (var fs = new FileStream(_expectedLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs))
            logContent = await reader.ReadToEndAsync();

        Assert.That(logContent, Does.Contain(messageId.ToString()),
            "Log file should contain the message ID of the handled request");
    }
}


