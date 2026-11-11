// Container Apps: the environment, the API (public HTTPS) and the worker (no ingress).
//
// Both run on the consumption plan and scale to zero, so an idle system costs nothing:
//   - the API starts on the first HTTP request
//   - the worker starts when there are messages in the queue
// Secrets are not stored in the apps. Each app resolves them from Key Vault at start,
// using its own managed identity.

param location string
param tags object

@description('Image tag to run, for example sha-<commit>. The same tag is used for both images.')
param imageTag string

@description('Registry path without the application suffix.')
param imageRepository string = 'ghcr.io/aleksacavic1996/webhook-processor'

param apiIdentityId string
param workerIdentityId string

param queueName string
param serviceBusNamespaceName string

param signingSecretUri string
param serviceBusSendSecretUri string
param serviceBusListenSecretUri string
param serviceBusScalerSecretUri string
param postgresConnectionSecretUri string

@description('Principal id of the identity GitHub Actions deploys with.')
param deployPrincipalId string

var suffix = uniqueString(resourceGroup().id, location)

// Container logs (the JSON console output of both apps) land here. First 5 GB a month are free.
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-webhook-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-webhook-${suffix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-webhook-api'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentityId}': {}
    }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      secrets: [
        {
          name: 'servicebus'
          keyVaultUrl: serviceBusSendSecretUri
          identity: apiIdentityId
        }
        {
          name: 'signing-secret'
          keyVaultUrl: signingSecretUri
          identity: apiIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: '${imageRepository}-api:${imageTag}'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'ConnectionStrings__ServiceBus'
              secretRef: 'servicebus'
            }
            {
              name: 'Webhook__SigningSecret'
              secretRef: 'signing-secret'
            }
            {
              name: 'ServiceBus__QueueName'
              value: queueName
            }
            {
              // Health probes run every 30 seconds; without this each one writes several log lines.
              name: 'Logging__LogLevel__Microsoft.AspNetCore'
              value: 'Warning'
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              periodSeconds: 30
              timeoutSeconds: 5
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 2
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

resource worker 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-webhook-worker'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentityId}': {}
    }
  }
  properties: {
    environmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      // No ingress: nothing outside the container needs to call the worker.
      secrets: [
        {
          name: 'servicebus'
          keyVaultUrl: serviceBusListenSecretUri
          identity: workerIdentityId
        }
        {
          name: 'servicebus-scaler'
          keyVaultUrl: serviceBusScalerSecretUri
          identity: workerIdentityId
        }
        {
          name: 'postgres'
          keyVaultUrl: postgresConnectionSecretUri
          identity: workerIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: '${imageRepository}-worker:${imageTag}'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'ConnectionStrings__ServiceBus'
              secretRef: 'servicebus'
            }
            {
              name: 'ConnectionStrings__Postgres'
              secretRef: 'postgres'
            }
            {
              name: 'ServiceBus__QueueName'
              value: queueName
            }
            {
              // Health probes run every 30 seconds; without this each one writes several log lines.
              name: 'Logging__LogLevel__Microsoft.AspNetCore'
              value: 'Warning'
            }
            {
              name: 'Database__MigrateOnStartup'
              value: 'true'
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              periodSeconds: 30
              timeoutSeconds: 5
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 2
        rules: [
          {
            // KEDA starts a replica when messages are waiting and adds one per 5 messages.
            name: 'queue-length'
            custom: {
              type: 'azure-servicebus'
              metadata: {
                namespace: serviceBusNamespaceName
                queueName: queueName
                messageCount: '5'
              }
              auth: [
                {
                  secretRef: 'servicebus-scaler'
                  triggerParameter: 'connection'
                }
              ]
            }
          }
        ]
      }
    }
  }
}

// ---- What the deployment pipeline may do ------------------------------------------------------
// Only this: change the two container apps (new image) and keep their identities attached.
// It cannot touch the database, Service Bus, Key Vault or role assignments.

var contributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')
var identityOperatorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'f1a07417-d97a-45cb-824c-7a7467783830')

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: last(split(apiIdentityId, '/'))
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: last(split(workerIdentityId, '/'))
}

resource deployChangesApi 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: api
  name: guid(api.id, deployPrincipalId, contributorRoleId)
  properties: {
    principalId: deployPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: contributorRoleId
  }
}

resource deployChangesWorker 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: worker
  name: guid(worker.id, deployPrincipalId, contributorRoleId)
  properties: {
    principalId: deployPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: contributorRoleId
  }
}

// Updating an app that has a user-assigned identity requires permission to assign that identity.
resource deployKeepsApiIdentity 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: apiIdentity
  name: guid(apiIdentity.id, deployPrincipalId, identityOperatorRoleId)
  properties: {
    principalId: deployPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: identityOperatorRoleId
  }
}

resource deployKeepsWorkerIdentity 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: workerIdentity
  name: guid(workerIdentity.id, deployPrincipalId, identityOperatorRoleId)
  properties: {
    principalId: deployPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: identityOperatorRoleId
  }
}

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output logWorkspaceName string = logs.name
