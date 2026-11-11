// Key Vault for the one secret the system still has: the HMAC secret shared with the webhook
// provider. Everything else (Service Bus, PostgreSQL) is reached with managed identities.
//
// Access is granted with Azure RBAC on the single secret, not on the vault.

param location string
param tags object

@description('Object id of the person or pipeline that deploys. Gets read/write access to secrets. Empty = no assignment.')
param deployerPrincipalId string

@description('Principal id of the managed identity of the API.')
param apiPrincipalId string

@secure()
param webhookSigningSecret string

// Vault names are global and limited to 24 characters.
var vaultName = 'kv-webhook-${uniqueString(resourceGroup().id, location)}'

// Built-in roles.
var secretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var secretsUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    // Shortest allowed retention, so a deleted demo vault frees its name quickly.
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource deployerAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  scope: vault
  name: guid(vault.id, deployerPrincipalId, secretsOfficerRoleId)
  properties: {
    principalId: deployerPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsOfficerRoleId)
  }
}

// API: Webhook:SigningSecret. The webhook provider signs requests with the same value.
resource signingSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'webhook-signing-secret'
  properties: {
    value: webhookSigningSecret
  }
}

resource apiReadsSigningSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: signingSecret
  name: guid(signingSecret.id, apiPrincipalId, secretsUserRoleId)
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: secretsUserRoleId
  }
}

output vaultName string = vault.name
output signingSecretUri string = signingSecret.properties.secretUri
