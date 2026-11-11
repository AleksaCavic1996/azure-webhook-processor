// Service Bus namespace with the single queue used by the API and the worker.
//
// Basic tier is enough: the application uses one queue, abandon and dead-letter.
// Topics, sessions, transactions and duplicate detection (Standard and up) are not used;
// idempotency is enforced in PostgreSQL, not by the broker.

param location string
param tags object
param queueName string

// Namespace names are global across Azure, so a stable hash of the resource group id is appended.
var namespaceName = 'sb-webhook-${uniqueString(resourceGroup().id)}'

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

output namespaceName string = namespace.name
output endpoint string = 'https://${namespace.name}.servicebus.windows.net'
