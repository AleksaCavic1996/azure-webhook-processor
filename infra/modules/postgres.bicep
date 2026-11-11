// PostgreSQL Flexible Server for the worker (payments and processed_events tables).
//
// Burstable B1ms with 32 GB is the size covered by the Azure free offer (750 hours a month
// for 12 months), and it is plenty for this workload.

param location string
param tags object

@description('Administrator login. The worker connects with it; the schema is created by its migrations.')
param administratorLogin string

@secure()
param administratorPassword string

param databaseName string

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
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
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
      publicNetworkAccess: 'Enabled'
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

// The special 0.0.0.0 rule admits traffic that originates inside Azure, which is how
// Container Apps on the consumption plan reach the server (they have no fixed outbound IP).
// Trade-off: any Azure tenant can reach the port, so the password and TLS are the real
// protection. A private endpoint in a VNet would close this, at extra cost.
resource allowAzureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: server
  name: 'AllowAllAzureServicesAndResourcesWithinAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output serverName string = server.name
output host string = server.properties.fullyQualifiedDomainName
