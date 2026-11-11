// Private network for the applications and the database.
//
// The database lives in its own subnet and has no public endpoint at all. The container apps
// run in a second subnet of the same network and reach the database over private addresses.
// Name resolution inside the network comes from a private DNS zone linked to it.

param location string
param tags object

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-webhook'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: ['10.20.0.0/16']
    }
    subnets: [
      {
        // Delegated to Container Apps: only a Container Apps environment can use this subnet.
        name: 'snet-apps'
        properties: {
          addressPrefix: '10.20.0.0/23'
          delegations: [
            {
              name: 'container-apps'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        // Delegated to PostgreSQL: only flexible servers can be placed here.
        name: 'snet-postgres'
        properties: {
          addressPrefix: '10.20.2.0/27'
          delegations: [
            {
              name: 'postgres'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
        }
      }
    ]
  }

  resource appsSubnet 'subnets' existing = {
    name: 'snet-apps'
  }

  resource postgresSubnet 'subnets' existing = {
    name: 'snet-postgres'
  }
}

// The server's public name resolves to its private address through this zone.
// The zone name must end with ".postgres.database.azure.com".
resource postgresDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'webhook.private.postgres.database.azure.com'
  location: 'global'
  tags: tags

  resource networkLink 'virtualNetworkLinks' = {
    name: 'vnet-webhook'
    location: 'global'
    properties: {
      virtualNetwork: {
        id: network.id
      }
      registrationEnabled: false
    }
  }
}

output appsSubnetId string = network::appsSubnet.id
output postgresSubnetId string = network::postgresSubnet.id
output postgresDnsZoneId string = postgresDnsZone.id
