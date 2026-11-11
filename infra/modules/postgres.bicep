// PostgreSQL Flexible Server for the worker (payments and processed_events tables).
//
// Burstable B1ms with 32 GB is the size covered by the Azure free offer (750 hours a month
// for 12 months), and it is plenty for this workload.
//
// Two things make it closed by default:
//   - no public endpoint: the server sits in a delegated subnet of the private network
//   - no passwords: only Microsoft Entra sign-in is enabled, so there is no password to leak

param location string
param tags object

param databaseName string

@description('Subnet delegated to PostgreSQL and the private DNS zone that resolves the server name.')
param subnetId string
param privateDnsZoneId string

@description('Managed identity of the worker. It signs in with an access token and owns the schema.')
param workerPrincipalId string
param workerIdentityName string

var serverName = 'psql-webhook-${uniqueString(resourceGroup().id, location)}'

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: serverName
  location: location
  tags: tags
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    // Same major version as the local docker-compose database.
    version: '18'
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
      tenantId: subscription().tenantId
    }
    storage: {
      storageSizeGB: 32
      autoGrow: 'Disabled'
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      delegatedSubnetResourceId: subnetId
      privateDnsZoneArmResourceId: privateDnsZoneId
      publicNetworkAccess: 'Disabled'
    }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: server
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// The worker's identity is registered as the server's Microsoft Entra administrator. That is
// done here, through Azure, because nothing outside the private network can connect to run SQL.
// Trade-off: the worker has administrator rights on this server. The server hosts only this
// application's database, and the worker already needs schema rights to apply migrations.
resource workerAdministrator 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2024-08-01' = {
  parent: server
  name: workerPrincipalId
  properties: {
    principalType: 'ServicePrincipal'
    principalName: workerIdentityName
    tenantId: subscription().tenantId
  }
  // The server accepts one management operation at a time.
  dependsOn: [database]
}

output serverName string = server.name
output host string = server.properties.fullyQualifiedDomainName
