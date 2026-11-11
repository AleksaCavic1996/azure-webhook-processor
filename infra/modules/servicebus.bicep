// Service Bus namespace with the single queue used by the API and the worker.
//
// Basic tier is enough: the application uses one queue, abandon and dead-letter.
// Topics, sessions, transactions and duplicate detection (Standard and up) are not used;
// idempotency is enforced in PostgreSQL, not by the broker.
//
// Access is by Microsoft Entra identity only. Shared access keys are switched off, so there
// is no connection string that could leak.

param location string
param tags object
param queueName string

@description('Managed identities of the API (sends), the worker (receives) and the autoscaler (reads queue length).')
param apiPrincipalId string
param workerPrincipalId string
param scalerPrincipalId string

@description('Person who operates the system. May inspect and replay dead-lettered messages. Empty = no assignment.')
param operatorPrincipalId string

// Namespace names are global across Azure, so a stable hash of the resource group id and region is appended.
var namespaceName = 'sb-webhook-${uniqueString(resourceGroup().id, location)}'

// Built-in roles.
var dataSenderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
var dataReceiverRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
var dataOwnerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '090c5cfd-751d-490a-894a-3ce6f1109419')

resource namespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: namespaceName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
  properties: {
    minimumTlsVersion: '1.2'
    disableLocalAuth: true
  }
}

// Same settings as the local emulator (servicebus-config.json), so behaviour matches the tests.
resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: namespace
  name: queueName
  properties: {
    lockDuration: 'PT1M'
    maxDeliveryCount: 5
    defaultMessageTimeToLive: 'PT1H'
    deadLetteringOnMessageExpiration: true
  }
}

// Each identity gets the least it needs, on this one queue. The API can only send and the
// worker can only receive, so a compromised API cannot read payments off the queue.
resource apiSends 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: queue
  name: guid(queue.id, apiPrincipalId, dataSenderRoleId)
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: dataSenderRoleId
  }
}

resource workerReceives 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: queue
  name: guid(queue.id, workerPrincipalId, dataReceiverRoleId)
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: dataReceiverRoleId
  }
}

// Reading the queue length is a management operation, which only the owner role allows.
// A separate identity keeps that right away from the worker's own sign-in.
resource scalerReadsQueueLength 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: queue
  name: guid(queue.id, scalerPrincipalId, dataOwnerRoleId)
  properties: {
    principalId: scalerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: dataOwnerRoleId
  }
}

resource operatorManagesQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(operatorPrincipalId)) {
  scope: queue
  name: guid(queue.id, operatorPrincipalId, dataOwnerRoleId)
  properties: {
    principalId: operatorPrincipalId
    roleDefinitionId: dataOwnerRoleId
  }
}

output namespaceId string = namespace.id
output namespaceName string = namespace.name
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
