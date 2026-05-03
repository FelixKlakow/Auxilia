using Auxilia.BackendService;
using Auxilia.Messaging;

var builder = WebApplication.CreateBuilder(args);

// --- Service identity (singleton, captured once at startup) ---
builder.Services.AddSingleton<ServiceInfo>();

// --- Messaging ---
builder.Services.AddSingleton<IMessageBusClient>(_ =>
    RabbitMqClient.CreateAsync(
        builder.Configuration["RabbitMq:Host"] ?? "localhost",
        int.Parse(builder.Configuration["RabbitMq:Port"] ?? "5672"),
        builder.Configuration["RabbitMq:UserName"] ?? "guest",
        builder.Configuration["RabbitMq:Password"] ?? "guest"
    ).GetAwaiter().GetResult());

// --- Hosted services ---
builder.Services.AddHostedService<QueueInitializer>();
builder.Services.AddHostedService<IdentificationRequestHandler>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

// Make the implicit Program class visible to test projects
public partial class Program
{
}