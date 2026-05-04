using System.Diagnostics;
using System.Diagnostics.Metrics;
using Auxilia.Messaging;
using Auxilia.Messaging.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Auxilia.BackendService.Tests.UnitTests;

/// <summary>
///     Unit tests that verify OpenTelemetry tracing and metrics instrumentation on
///     <see cref="IdentificationRequestHandler" />.
/// </summary>
[TestFixture]
[Category("Unit")]
public class IdentificationRequestHandlerTelemetryTests
{
    private static IConfiguration EmptyConfig => new ConfigurationBuilder().Build();

    private Mock<IMessageBusClient> _mockBus = null!;
    private IdentificationRequestHandler _sut = null!;
    private Func<IdentificationRequestMessage, CancellationToken, Task>? _capturedHandler;

    [SetUp]
    public async Task SetUp()
    {
        _mockBus = new Mock<IMessageBusClient>(MockBehavior.Strict);

        var subscriptionHandle = new Mock<IAsyncDisposable>();
        subscriptionHandle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockBus
            .Setup(b => b.SubscribeAsync<IdentificationRequestMessage>(
                QueueInitializer.DefaultQueueName,
                It.IsAny<Func<IdentificationRequestMessage, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<IdentificationRequestMessage, CancellationToken, Task>, CancellationToken>(
                (_, handler, _) => _capturedHandler = handler)
            .ReturnsAsync(subscriptionHandle.Object);

        _mockBus
            .Setup(b => b.PublishAsync(
                It.IsAny<string>(),
                It.IsAny<IdentificationResponseMessage>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new IdentificationRequestHandler(
            _mockBus.Object,
            new ServiceInfo(),
            EmptyConfig,
            NullLogger<IdentificationRequestHandler>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task WhenHandlingRequest_StartsActivityWithCorrectOperationName()
    {
        // Arrange
        var capturedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackendServiceTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = capturedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var request = new IdentificationRequestMessage(Guid.NewGuid(), Guid.NewGuid(), "response-topic");

        // Act
        await _capturedHandler!(request, CancellationToken.None);

        // Assert
        Assert.That(capturedActivities, Has.Count.EqualTo(1),
            "Expected exactly one activity to be started for an identification request");
        Assert.That(capturedActivities[0].OperationName,
            Is.EqualTo("identification.request.handle"),
            "Activity operation name should be 'identification.request.handle'");
    }

    [Test]
    public async Task WhenHandlingRequest_ActivityContainsRequestTags()
    {
        // Arrange
        Activity? capturedActivity = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackendServiceTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => capturedActivity = a
        };
        ActivitySource.AddActivityListener(listener);

        var messageId = Guid.NewGuid();
        var requestingServiceId = Guid.NewGuid();
        var request = new IdentificationRequestMessage(messageId, requestingServiceId, "response-topic");

        // Act
        await _capturedHandler!(request, CancellationToken.None);

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.GetTagItem("request.message_id"),
            Is.EqualTo(messageId.ToString()),
            "Activity should be tagged with the request message_id");
        Assert.That(capturedActivity.GetTagItem("request.service_id"),
            Is.EqualTo(requestingServiceId.ToString()),
            "Activity should be tagged with the requesting service_id");
    }

    [Test]
    public async Task WhenHandlingRequest_IncrementsIdentificationRequestsCounter()
    {
        // Arrange
        long totalMeasured = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == BackendServiceTelemetry.MeterName &&
                instrument.Name == "identification.requests.received")
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>(
            (_, measurement, _, _) => Interlocked.Add(ref totalMeasured, measurement));
        meterListener.Start();

        var request = new IdentificationRequestMessage(Guid.NewGuid(), Guid.NewGuid(), "response-topic");

        // Act
        await _capturedHandler!(request, CancellationToken.None);

        // Assert
        Assert.That(totalMeasured, Is.EqualTo(1),
            "identification.requests.received counter should have been incremented by 1");
    }

    [Test]
    public async Task WhenHandlingMultipleRequests_CounterReflectsAllRequests()
    {
        // Arrange
        long totalMeasured = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == BackendServiceTelemetry.MeterName &&
                instrument.Name == "identification.requests.received")
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>(
            (_, measurement, _, _) => Interlocked.Add(ref totalMeasured, measurement));
        meterListener.Start();

        // Act
        for (var i = 0; i < 3; i++)
            await _capturedHandler!(
                new IdentificationRequestMessage(Guid.NewGuid(), Guid.NewGuid(), "response-topic"),
                CancellationToken.None);

        // Assert
        Assert.That(totalMeasured, Is.EqualTo(3),
            "Counter should have been incremented once per request (3 total)");
    }
}

