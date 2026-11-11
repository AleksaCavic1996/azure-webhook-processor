// Key Vault that holds the application secrets. Access is granted with Azure RBAC, per secret:
// each application identity can read only the secrets it actually uses.

param location string
param tags object

@description('Object id of the person or pipeline that deploys. Gets read/write access to secrets. Empty = no assignment.')
param deployerPrincipalId string

@description('Principal ids of the managed identities of the two applications.')
param apiPrincipalId string
param workerPrincipalId string

@secure()
param postgresAdminPassword string

@secure()
param postgresConnectionString string

@secure()
param webhookSigningSecret string

param serviceBusNamespaceName string
param queueName string
param apiSendRuleName string
param workerListenRuleName string
param scalerRuleName string

// Vault names are global and limited to 24 characters.
var vaultName = 'kv-webhook-${uniqueString(resourceGroup().id, location)}'

// Built-in roles.
var secretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var secretsUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: serviceBusNamespaceName

  resource queue 'queues' existing = {
    name: queueName

    resource apiSendRule 'authorizationRules' existing = {
      name: apiSendRuleName
    }

    resource workerListenRule 'authorizationRules' existing = {
      name: workerListenRuleName
    }
  }

  resource scalerRule 'authorizationRules' existing = {
    name: scalerRuleName
  }
}

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

// ---- Secrets -------------------------------------------------------------------------------

// Kept so that a later deployment can reuse the same password instead of rotating it.
resource adminPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'postgres-admin-password'
  properties: {
    value: postgresAdminPassword
  }
}

// Worker: ConnectionStrings:Postgres.
resource postgresConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'postgres-connection-string'
  properties: {
    value: postgresConnectionString
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

// API: ConnectionStrings:ServiceBus (send only).
resource serviceBusSendSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'servicebus-send-connection-string'
  properties: {
    value: serviceBusNamespace::queue::apiSendRule.listKeys().primaryConnectionString
  }
}

// Worker: ConnectionStrings:ServiceBus (listen only).
resource serviceBusListenSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'servicebus-listen-connection-string'
  properties: {
    value: serviceBusNamespace::queue::workerListenRule.listKeys().primaryConnectionString
  }
}

// Worker scale rule: lets the platform read the queue length.
resource serviceBusScalerSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'servicebus-scaler-connection-string'
  properties: {
    value: serviceBusNamespace::scalerRule.listKeys().primaryConnectionString
  }
}

// ---- Who can read what ---------------------------------------------------------------------

resource apiReadsSigningSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: signingSecret
  name: guid(signingSecret.id, apiPrincipalId, secretsUserRoleId)
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: secretsUserRoleId
  }
}

resource apiReadsSendSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBusSendSecret
  name: guid(serviceBusSendSecret.id, apiPrincipalId, secretsUserRoleId)
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: secretsUserRoleId
  }
}

resource workerReadsPostgresSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: postgresConnectionSecret
  name: guid(postgresConnectionSecret.id, workerPrincipalId, secretsUserRoleId)
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: secretsUserRoleId
  }
}

resource workerReadsListenSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBusListenSecret
  name: guid(serviceBusListenSecret.id, workerPrincipalId, secretsUserRoleId)
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: secretsUserRoleId
  }
}

resource workerReadsScalerSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBusScalerSecret
  name: guid(serviceBusScalerSecret.id, workerPrincipalId, secretsUserRoleId)
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: secretsUserRoleId
  }
}

output vaultName string = vault.name
output signingSecretUri string = signingSecret.properties.secretUri
output serviceBusSendSecretUri string = serviceBusSendSecret.properties.secretUri
output serviceBusListenSecretUri string = serviceBusListenSecret.properties.secretUri
output serviceBusScalerSecretUri string = serviceBusScalerSecret.properties.secretUri
output postgresConnectionSecretUri string = postgresConnectionSecret.properties.secretUri
