// Entry point for all Azure infrastructure of the webhook processor.
// Deployed at subscription scope so that the resource group itself is part of the template:
//   az deployment sub create --location polandcentral --template-file infra/main.bicep
targetScope = 'subscription'

@description('Azure region for the resource group and everything in it.')
param location string = 'polandcentral'

@description('Name of the resource group that holds the whole project. Deleting it removes every resource.')
param resourceGroupName string = 'rg-webhook-processor'

@description('Name of the queue the API writes to and the worker reads from. Must match ServiceBus:QueueName.')
param queueName string = 'payment-events'

var tags = {
  project: 'azure-webhook-processor'
  managedBy: 'bicep'
}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module serviceBus 'modules/servicebus.bicep' = {
  name: 'servicebus'
  scope: resourceGroup
  params: {
    location: location
    tags: tags
    queueName: queueName
  }
}

output resourceGroupName string = resourceGroup.name
output serviceBusNamespaceName string = serviceBus.outputs.namespaceName
output serviceBusEndpoint string = serviceBus.outputs.endpoint
output queueName string = queueName
