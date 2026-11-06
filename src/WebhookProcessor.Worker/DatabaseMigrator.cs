using System.Reflection;
using DbUp;

namespace WebhookProcessor.Worker;

public static class DatabaseMigrator
{
    /// <summary>Applies embedded SQL scripts that have not run yet. DbUp records applied scripts in a journal table.</summary>
    public static void Migrate(string connectionString, ILogger logger)
    {
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .LogToNowhere()
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException("Database migration failed.", result.Error);
        }

        logger.LogInformation("Database migration finished, {ScriptCount} script(s) applied", result.Scripts.Count());
    }
}
