using Azure.Core;

namespace WebhookProcessor.Worker;

/// <summary>
/// Microsoft Entra access token that Azure Database for PostgreSQL accepts in place of a password.
/// </summary>
public static class PostgresAccessToken
{
    // The audience is the same for every Azure Database for PostgreSQL server in the public cloud.
    private static readonly TokenRequestContext Context = new(["https://ossrdbms-aad.database.windows.net/.default"]);

    public static async Task<string> GetAsync(TokenCredential credential, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(Context, cancellationToken);
        return token.Token;
    }
}
