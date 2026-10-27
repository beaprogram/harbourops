// One firewall rule per possible outbound address of the web app. A module, because the list is only known
// once the site exists (resource loops in the parent template must be computable at deployment start).

@description('PostgreSQL flexible server name.')
param serverName string

@description('IPv4 addresses allowed to connect (the web app\'s possible outbound addresses).')
param addresses array

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2025-08-01' existing = {
  name: serverName
}

resource rules 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2025-08-01' = [for (address, i) in addresses: {
  parent: server
  name: 'app-outbound-${i}'
  properties: {
    startIpAddress: trim(address)
    endIpAddress: trim(address)
  }
}]
