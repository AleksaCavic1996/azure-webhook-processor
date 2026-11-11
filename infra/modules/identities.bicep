// One user-assigned managed identity per application. The container apps use them to read
// their secrets from Key Vault, so no credential for the vault is stored anywhere.
//
// User-assigned (not system-assigned) so that the identities exist, and have their Key Vault
// roles, before the container apps that depend on them are created.

param location string
param tags object

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-webhook-api'
  location: location
  tags: tags
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-webhook-worker'
  location: location
  tags: tags
}

output apiIdentityId string = apiIdentity.id
output apiPrincipalId string = apiIdentity.properties.principalId
output workerIdentityId string = workerIdentity.id
output workerPrincipalId string = workerIdentity.properties.principalId

// ---- Deployment from GitHub Actions --------------------------------------------------------

@description('Subject prefix of the GitHub OIDC token for the repository whose main branch may deploy.')
param githubSubjectPrefix string

// GitHub Actions signs in as this identity with a short-lived OIDC token. There is no client
// secret: Azure trusts a token only if GitHub issued it for this repository and this branch.
resource deployIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-webhook-deploy'
  location: location
  tags: tags

  resource githubMain 'federatedIdentityCredentials' = {
    name: 'github-main'
    properties: {
      issuer: 'https://token.actions.githubusercontent.com'
      subject: '${githubSubjectPrefix}:ref:refs/heads/main'
      audiences: ['api://AzureADTokenExchange']
    }
  }
}

output deployClientId string = deployIdentity.properties.clientId
output deployPrincipalId string = deployIdentity.properties.principalId
