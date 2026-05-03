using Auxilia.Messaging;

var builder = WebApplication.CreateBuilder(args);

// --- Service identity ---
var serviceId = Guid.NewGuid();
var startupTime = DateTime.UtcNow;
builder.Services.AddSingleton(new SteeringInstanceInfo(serviceId, startupTime));

// --- Messaging ---
builder.Services.AddSingleton<IMessageBusClient>(_ =>
    RabbitMqClient.CreateAsync(
        builder.Configuration["RabbitMq:Host"] ?? "localhost",
        int.Parse(builder.Configuration["RabbitMq:Port"] ?? "5672"),
        builder.Configuration["RabbitMq:UserName"] ?? "guest",
        builder.Configuration["RabbitMq:Password"] ?? "guest"
    ).GetAwaiter().GetResult());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

public partial class Program
{
}

public sealed record SteeringInstanceInfo(Guid ServiceId, DateTime StartupTimeUtc);