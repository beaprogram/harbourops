// HarbourOps public demo: one resource group, deployed with
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/main.parameters.example.json -p postgresAdminPassword=... appDbPassword=...
// Nothing in this repository runs this automatically. See docs/deployment.md for the full runbook and costs.
//
// Shape: App Service (Linux container, B1) -> PostgreSQL Flexible Server 18 (Burstable B1ms), private Blob
// container for demo photos, Key Vault (Data Protection key + connection strings), Container Registry (Basic).
// Every app-to-Azure connection uses a user-assigned managed identity; GitHub deploys through OIDC federation.

targetScope = 'resourceGroup'

@description('Azure region. Canada Central is the closest region to Halifax with every service used here.')
param location string = resourceGroup().location

@description('Short lowercase prefix for resource names, e.g. "hops-demo".')
@minLength(3)
@maxLength(12)
param namePrefix string

@description('Container image (repository:tag) in the registry. The deploy workflow replaces it with the commit SHA tag.')
param containerImage string = 'harbourops:bootstrap'

@description('''The exact "sub" claim of the deploy job's GitHub OIDC token, read from the "Show OIDC subject" workflow
(.github/workflows/oidc-subject.yml). Repositories created after 15 July 2026 use an immutable format that embeds the
owner and repository IDs, so the subject is copied, never guessed. It must match exactly: Azure rejects a mismatch
at sign-in time without saying why.''')
param githubOidcSubject string

@description('Monthly budget for this resource group, in the subscription\'s billing currency. Alerts only: Azure keeps billing past it.')
param budgetMonthlyAmount int = 50

@description('Email addresses for budget alerts. Leave empty to skip creating the budget.')
param budgetContactEmails array = []

@description('First day of the month the budget starts (yyyy-MM-01). Defaults to the current month.')
param budgetStartDate string = utcNow('yyyy-MM-01')

@description('PostgreSQL administrator login. Used only by the migration step (it owns the schema).')
param postgresAdminLogin string = 'harbourops_admin'

@description('PostgreSQL administrator password. Supplied at deployment time, stored only in Key Vault.')
@secure()
param postgresAdminPassword string

@description('Password for the app\'s least-privilege role (harbourops_app), created by infra/sql/create-app-role.sql.')
@secure()
param appDbPassword string

@description('App Service plan SKU. B1 (1 vCPU, 1.75 GB) was sized from measured memory: see docs/benchmarks/photo-memory.')
param appServiceSku string = 'B1'

var suffix = uniqueString(resourceGroup().id)
var names = {
  logs: '${namePrefix}-logs'
  appIdentity: '${namePrefix}-app-id'
  deployIdentity: '${namePrefix}-deploy-id'
  // Registry and storage names allow only letters and digits; the 13-character unique suffix keeps them global-unique.
  registry: 'acr${suffix}${take(replace(namePrefix, '-', ''), 12)}'
  storage: 'st${suffix}${take(replace(namePrefix, '-', ''), 9)}'
  vault: take('${namePrefix}-kv-${suffix}', 24)
  postgres: '${namePrefix}-pg-${suffix}'
  plan: '${namePrefix}-plan'
  site: '${namePrefix}-${suffix}'
}
var photoContainer = 'demo-photos'
var dataProtectionKeyName = 'dataprotection'

// Built-in role definition IDs (verified against learn.microsoft.com built-in roles list, 2026-10-26).
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  acrPush: '8311e382-0749-4cb8-b61a-304f252e45ec'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6' // gitleaks:allow (public built-in role ID, not a secret)
  keyVaultCryptoServiceEncryptionUser: 'e147488a-f6f5-4113-8e2d-b22465e65bf6'
  websiteContributor: 'de139f84-1756-47ae-9be6-808fbbe84772'
  contributor: 'b24988ac-6180-42a0-ab88-20f7382dd24c'
}

// ---------- Monitoring ----------
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logs
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// ---------- Identities ----------
resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.appIdentity
  location: location
}

resource deployIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.deployIdentity
  location: location
}

// GitHub Actions jobs in the protected environment get short-lived tokens; no secret is stored in GitHub.
resource githubFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: deployIdentity
  name: 'github-production'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: githubOidcSubject
    audiences: [ 'api://AzureADTokenExchange' ]
  }
}

// ---------- Container registry ----------
resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: names.registry
  location: location
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: 'Enabled'
    policies: {
      // App Service pulls with managed identity, which needs ARM-audience tokens.
      azureADAuthenticationAsArmPolicy: { status: 'enabled' }
    }
  }
}

// ---------- Photo storage ----------
resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: names.storage
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false   // no public containers, ever
    allowSharedKeyAccess: false    // Microsoft Entra ID (managed identity) only: no account keys or SAS
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled' // reachable by the app; access still requires the app's identity
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2024-01-01' = {
  parent: storage
  name: 'default'
  properties: {
    // Recover from accidental deletion for 7 days (demo photos are temporary by design).
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

resource photos 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  parent: blobService
  name: photoContainer
  properties: { publicAccess: 'None' }
}

// ---------- Key Vault: Data Protection key and connection strings ----------
resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: names.vault
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true    // a deleted vault (and the key wrapping the cookie keys) can be recovered
    publicNetworkAccess: 'Enabled'
  }
}

// Wraps every Data Protection key the app creates. The app uses the versionless identifier, so rotating this
// key (az keyvault key rotate) wraps new keys with the new version while old versions still unwrap old keys.
resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2024-11-01' = {
  parent: vault
  name: dataProtectionKeyName
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [ 'wrapKey', 'unwrapKey' ]
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2025-08-01' = {
  name: names.postgres
  location: location
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '18'
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    storage: { storageSizeGB: 32, autoGrow: 'Disabled' }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    network: { publicNetworkAccess: 'Enabled' } // firewall below admits only the app's outbound addresses
    authConfig: { passwordAuth: 'Enabled', activeDirectoryAuth: 'Disabled' }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2025-08-01' = {
  parent: postgres
  name: 'harbourops'
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

var postgresHost = postgres.properties.fullyQualifiedDomainName

resource appConnectionString 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: vault
  name: 'ConnectionStrings--Default'
  properties: {
    value: 'Host=${postgresHost};Database=harbourops;Username=harbourops_app;Password=${appDbPassword};SSL Mode=VerifyFull;GSS Encryption Mode=Disable'
  }
}

resource migratorConnectionString 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: vault
  name: 'postgres-migrator-connection'
  properties: {
    value: 'Host=${postgresHost};Database=harbourops;Username=${postgresAdminLogin};Password=${postgresAdminPassword};SSL Mode=VerifyFull;GSS Encryption Mode=Disable'
  }
}

// ---------- App Service ----------
resource plan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: names.plan
  location: location
  kind: 'linux'
  sku: { name: appServiceSku }
  properties: { reserved: true }
}

resource site 'Microsoft.Web/sites@2024-11-01' = {
  name: names.site
  location: location
  kind: 'app,linux,container'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${appIdentity.id}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    keyVaultReferenceIdentity: appIdentity.id
    siteConfig: {
      linuxFxVersion: 'DOCKER|${registry.properties.loginServer}/${containerImage}'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: appIdentity.properties.clientId
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health/ready'
      appSettings: [
        // appsettings.PublicDemo.json: sandboxes on, shared accounts off, secure-configuration guard on.
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'PublicDemo' }
        // App Service terminates TLS; trust its X-Forwarded-Proto/For (HTTPS detection, Secure cookies, per-IP limits).
        { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
        { name: 'WEBSITES_PORT', value: '8080' }
        { name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE', value: 'false' }
        { name: 'ConnectionStrings__Default', value: '@Microsoft.KeyVault(SecretUri=${appConnectionString.properties.secretUri})' }
        { name: 'DataProtection__KeyVaultKeyUri', value: '${vault.properties.vaultUri}keys/${dataProtectionKeyName}' }
        { name: 'Attachments__Storage__AzureBlob__ServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'Azure__ManagedIdentityClientId', value: appIdentity.properties.clientId }
      ]
    }
  }
  dependsOn: [ appCanPull, appCanReadConnectionString ]
}

// Console logs (the app's JSON logs: method, path, status, duration; never bodies, cookies, or headers) and
// platform logs only. AppServiceHTTPLogs is deliberately NOT enabled: that table has a Cookie column.
resource siteDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: site
  properties: {
    workspaceId: logs.id
    logs: [
      { category: 'AppServiceConsoleLogs', enabled: true }
      { category: 'AppServicePlatformLogs', enabled: true }
    ]
  }
}

// The database admits only this app's possible outbound addresses (the deploy job adds and removes a rule for
// its own runner while it migrates). Passed through a module so the list can come from the deployed site.
module postgresFirewall 'modules/postgres-firewall.bicep' = {
  name: 'postgres-firewall'
  params: {
    serverName: postgres.name
    addresses: split(site.properties.possibleOutboundIpAddresses, ',')
  }
}

// ---------- Role assignments (least privilege, narrowest scope) ----------
resource appCanPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, appIdentity.id, roles.acrPull)
  scope: registry
  properties: {
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
  }
}

resource appCanUsePhotos 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(photos.id, appIdentity.id, roles.storageBlobDataContributor)
  scope: photos
  properties: {
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
  }
}

resource appCanReadConnectionString 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appConnectionString.id, appIdentity.id, roles.keyVaultSecretsUser)
  scope: appConnectionString
  properties: {
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
  }
}

// Get, wrap, and unwrap only: what ProtectKeysWithAzureKeyVault needs.
resource appCanWrapKeys 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dataProtectionKey.id, appIdentity.id, roles.keyVaultCryptoServiceEncryptionUser)
  scope: dataProtectionKey
  properties: {
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultCryptoServiceEncryptionUser)
  }
}

resource deployCanPush 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, deployIdentity.id, roles.acrPush)
  scope: registry
  properties: {
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPush)
  }
}

resource deployCanUpdateSite 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(site.id, deployIdentity.id, roles.websiteContributor)
  scope: site
  properties: {
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.websiteContributor)
  }
}

// To add and remove the temporary firewall rule for the migration step (scoped to this one server).
resource deployCanManageDatabaseFirewall 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(postgres.id, deployIdentity.id, roles.contributor)
  scope: postgres
  properties: {
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.contributor)
  }
}

resource deployCanReadMigratorSecret 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(migratorConnectionString.id, deployIdentity.id, roles.keyVaultSecretsUser)
  scope: migratorConnectionString
  properties: {
    principalId: deployIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
  }
}

// ---------- Cost alerts (notifications only: a budget never stops resources or caps spending) ----------
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = if (!empty(budgetContactEmails)) {
  name: '${namePrefix}-monthly'
  properties: {
    category: 'Cost'
    amount: budgetMonthlyAmount
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    notifications: {
      actualOver80Percent: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: budgetContactEmails
      }
      forecastOver100Percent: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: budgetContactEmails
      }
    }
  }
}

// ---------- Outputs: the values the GitHub "production" environment needs (none are secrets) ----------
output webAppName string = site.name
output webAppUrl string = 'https://${site.properties.defaultHostName}'
output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output postgresServerName string = postgres.name
output keyVaultName string = vault.name
output storageAccountName string = storage.name
output deployClientId string = deployIdentity.properties.clientId
output tenantId string = subscription().tenantId
output subscriptionId string = subscription().subscriptionId
