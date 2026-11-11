// Service Bus namespace with the single queue used by the API and the worker.
//
// Basic tier is enough: the application uses one queue, abandon and dead-letter.
// Topics, sessions, transactions and duplicate detection (Standard and up) are not used;
// idempotency is enforced in PostgreSQL, not by the broker.

param location string
param tags object
param queueName string

// Namespace names are global across Azure, so a stable hash of the resource group id and region is appended.
var namespaceName = 'sb-webhook-${uniqueString(resourceGroup().id, location)}'

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

// One key per role, each with the least it needs. The API can only send and the worker can
// only receive, so a leaked API key cannot read payments off the queue.
resource apiSendRule 'Microsoft.ServiceBus/namespaces/queues/authorizationRules@2024-01-01' = {
  parent: queue
  name: 'api-send'
  properties: {
    rights: ['Send']
  }
}

resource workerListenRule 'Microsoft.ServiceBus/namespaces/queues/authorizationRules@2024-01-01' = {
  parent: queue
  name: 'worker-listen'
  properties: {
    rights: ['Listen']
  }
}

// The autoscaler reads the queue length, which requires the Manage right (and Manage
// requires the other two). It is used by the platform only, never by application code.
resource scalerRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2024-01-01' = {
  parent: namespace
  name: 'keda-scaler'
  properties: {
    rights: ['Manage', 'Listen', 'Send']
  }
}

output namespaceName string = namespace.name
output endpoint string = 'https://${namespace.name}.servicebus.windows.net'
output apiSendRuleName string = apiSendRule.name
output workerListenRuleName string = workerListenRule.name
output scalerRuleName string = scalerRule.name
