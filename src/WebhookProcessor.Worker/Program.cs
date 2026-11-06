using Azure.Messaging.ServiceBus;
using Npgsql;
using WebhookProcessor.Worker;

var builder = Host.CreateApplicationBuilder(args);

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
var serviceBus = builder.Configuration.GetConnectionString("ServiceBus")
    ?? throw new InvalidOperationException("ConnectionStrings:ServiceBus is not configured.");

builder.Services.AddSingleton(NpgsqlDataSource.Create(postgres));
builder.Services.AddSingleton(new ServiceBusClient(serviceBus));
builder.Services.AddSingleton<PaymentRepository>();
builder.Services.AddSingleton<EventProcessor>();
builder.Services.AddSingleton<QueueConsumer>();
builder.Services.AddHostedService(services => services.GetRequiredService<QueueConsumer>());

var host = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    DatabaseMigrator.Migrate(postgres, host.Services.GetRequiredService<ILogger<Program>>());
}

host.Run();
