using WebhookProcessor.Worker;

var builder = Host.CreateApplicationBuilder(args);

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

var host = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    DatabaseMigrator.Migrate(postgres, host.Services.GetRequiredService<ILogger<Program>>());
}

host.Run();
