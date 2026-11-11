// Container Apps: the environment, the API (public HTTPS) and the worker (no ingress).
//
// Both run on the consumption profile and scale to zero, so an idle system costs nothing:
//   - the API starts on the first HTTP request
//   - the worker starts when there are messages in the queue
// The environment is placed in the private network, which is how the worker reaches the
// database. Sign-in to Service Bus and PostgreSQL uses each app's managed identity.

param location string
param tags object

@description('Image tag to run, for example sha-<commit>. The same tag is used for both images.')
param imageTag string

@description('Registry path without the application suffix.')
param imageRepository string = 'ghcr.io/aleksacavic1996/webhook-processor'

@description('Subnet of the private network that is delegated to Container Apps.')
param subnetId string

param apiIdentityId string
param apiClientId string
param workerIdentityId string
param workerIdentityName string
param workerClientId string
param scalerIdentityId string

@description('Principal id of the identity GitHub Actions deploys with.')
param deployPrincipalId string

param queueName string
param serviceBusFullyQualifiedNamespace string

param postgresHost string
param postgresDatabaseName string

param signingSecretUri string

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

// Application Insights on top of the same workspace: traces, metrics and logs that both apps
// export through OpenTelemetry. Shares the free 5 GB a month with the container logs.
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-webhook-${suffix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: 'cae-webhook-${suffix}'
  location: location
  tags: tags
  properties: {
    // Apps get private addresses in this subnet. Ingress stays public (internal: false),
    // because the webhook provider calls the API from the internet.
    vnetConfiguration: {
      infrastructureSubnetId: subnetId
      internal: false
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource api 'Microsoft.App/containerApps@2025-01-01' = {
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
    workloadProfileName: 'Consumption'
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
          // The only secret in the system: the HMAC secret shared with the webhook provider.
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
              name: 'Webhook__SigningSecret'
              secretRef: 'signing-secret'
            }
            {
              // Sign in to Service Bus with the managed identity. No connection string exists.
              name: 'ServiceBus__FullyQualifiedNamespace'
              value: serviceBusFullyQualifiedNamespace
            }
            {
              name: 'ServiceBus__QueueName'
              value: queueName
            }
            {
              // Tells the Azure SDK which user-assigned identity to sign in with.
              name: 'AZURE_CLIENT_ID'
              value: apiClientId
            }
            {
              // Turns telemetry export on. The value identifies the resource; it grants no read access.
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: insights.properties.ConnectionString
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

resource worker 'Microsoft.App/containerApps@2025-01-01' = {
  name: 'ca-webhook-worker'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentityId}': {}
      '${scalerIdentityId}': {}
    }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // No ingress: nothing outside the container needs to call the worker.
      // No secrets either: every dependency is reached with a managed identity.
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
              // No password: the user name is the worker's identity and the password is an
              // access token the worker fetches at run time.
              name: 'ConnectionStrings__Postgres'
              value: 'Host=${postgresHost};Port=5432;Database=${postgresDatabaseName};Username=${workerIdentityName};SSL Mode=Require'
            }
            {
              name: 'Postgres__UseManagedIdentity'
              value: 'true'
            }
            {
              // Sign in to Service Bus with the managed identity. No connection string exists.
              name: 'ServiceBus__FullyQualifiedNamespace'
              value: serviceBusFullyQualifiedNamespace
            }
            {
              name: 'ServiceBus__QueueName'
              value: queueName
            }
            {
              // Tells the Azure SDK which of the two attached identities the worker signs in with.
              name: 'AZURE_CLIENT_ID'
              value: workerClientId
            }
            {
              name: 'Database__MigrateOnStartup'
              value: 'true'
            }
            {
              // Turns telemetry export on. The value identifies the resource; it grants no read access.
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: insights.properties.ConnectionString
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
            // KEDA starts a replica when messages are waiting and adds one per 5 messages.
            // It reads the queue length as the scaler identity, not with a key.
            name: 'queue-length'
            custom: {
              type: 'azure-servicebus'
              identity: scalerIdentityId
              metadata: {
                namespace: split(serviceBusFullyQualifiedNamespace, '.')[0]
                queueName: queueName
                messageCount: '5'
              }
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
resource attachedIdentities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = [
  for id in [apiIdentityId, workerIdentityId, scalerIdentityId]: {
    name: last(split(id, '/'))
  }
]

resource deployKeepsIdentities 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for i in range(0, 3): {
    scope: attachedIdentities[i]
    name: guid(attachedIdentities[i].id, deployPrincipalId, identityOperatorRoleId)
    properties: {
      principalId: deployPrincipalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: identityOperatorRoleId
    }
  }
]

output apiId string = api.id
output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output logWorkspaceName string = logs.name
output applicationInsightsName string = insights.name
