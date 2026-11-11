// Key Vault that holds the application secrets. Access is granted with Azure RBAC:
// the applications will read secrets through their managed identity, never through a stored key.

param location string
param tags object

@description('Object id of the person or pipeline that deploys. Gets read/write access to secrets. Empty = no assignment.')
param deployerPrincipalId string

@secure()
param postgresAdminPassword string

@secure()
param postgresConnectionString string

// Vault names are global and limited to 24 characters.
var vaultName = 'kv-webhook-${uniqueString(resourceGroup().id, location)}'

// Built-in role: Key Vault Secrets Officer.
var secretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

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

// Kept so that a later deployment can reuse the same password instead of rotating it.
resource adminPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'postgres-admin-password'
  properties: {
    value: postgresAdminPassword
  }
}

// Read by the worker as ConnectionStrings:Postgres.
resource connectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'postgres-connection-string'
  properties: {
    value: postgresConnectionString
  }
}

output vaultName string = vault.name
output vaultUri string = vault.properties.vaultUri
