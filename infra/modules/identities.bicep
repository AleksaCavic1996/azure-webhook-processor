// User-assigned managed identities. Every sign-in in the system goes through one of these:
// to Service Bus, to PostgreSQL, to Key Vault and from the deployment pipeline to Azure.
//
// User-assigned (not system-assigned) so that the identities exist, and have their roles,
// before the container apps that depend on them are created.

param location string
param tags object

@description('Subject prefix of the GitHub OIDC token for the repository whose main branch may deploy.')
param githubSubjectPrefix string

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

// Used only by the worker's scale rule, to read the queue length.
resource scalerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-webhook-scaler'
  location: location
  tags: tags
}

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

output apiIdentityId string = apiIdentity.id
output apiPrincipalId string = apiIdentity.properties.principalId
output apiClientId string = apiIdentity.properties.clientId

output workerIdentityId string = workerIdentity.id
output workerIdentityName string = workerIdentity.name
output workerPrincipalId string = workerIdentity.properties.principalId
output workerClientId string = workerIdentity.properties.clientId

output scalerIdentityId string = scalerIdentity.id
output scalerPrincipalId string = scalerIdentity.properties.principalId

output deployClientId string = deployIdentity.properties.clientId
output deployPrincipalId string = deployIdentity.properties.principalId
