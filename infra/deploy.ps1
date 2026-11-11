# Deploys the whole infrastructure from infra/main.bicep.
#
# The system has one secret, the webhook signing secret, and it is never typed or printed:
#   - first run: a random value is generated and stored in Key Vault by the template
#   - later runs: the existing value is read back from Key Vault and reused
# Service Bus and PostgreSQL have no passwords or keys; they are reached with managed identities.
#
# Usage (from the repository root, after `az login`):
#   ./infra/deploy.ps1                      deploy the images of the latest commit on origin/main
#   ./infra/deploy.ps1 -ImageTag sha-<sha>  deploy a specific image
#   ./infra/deploy.ps1 -WhatIf              show what would change, create nothing
param(
    [string]$Location = 'northeurope',
    [string]$ResourceGroupName = 'rg-webhook-processor',
    [string]$ImageTag,
    # Receives alert emails. Defaults to the signed-in account, so no address is kept in the repository.
    [string]$AlertEmail,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$template = Join-Path $PSScriptRoot 'main.bicep'

function New-RandomSecret {
    # 40 letters and digits: no characters that need escaping.
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'.ToCharArray()
    $bytes = New-Object byte[] 40
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
}

function Get-VaultSecret([string]$Vault, [string]$Name) {
    # Returns $null when the secret does not exist.
    $ErrorActionPreference = 'Continue'
    az keyvault secret show --vault-name $Vault --name $Name --query value -o tsv 2>$null
}

$deployerId = az ad signed-in-user show --query id -o tsv
if (-not $deployerId) { throw 'Not logged in. Run az login first.' }

if (-not $AlertEmail) { $AlertEmail = az account show --query user.name -o tsv }

if (-not $ImageTag) {
    # CI publishes an image tagged with the full commit SHA for every commit on main.
    $sha = git -C $PSScriptRoot rev-parse origin/main
    if (-not $sha) { throw 'Could not resolve origin/main. Pass -ImageTag explicitly.' }
    $ImageTag = "sha-$sha"
}
Write-Host "Image tag: $ImageTag"

# Reuse the signing secret of an existing deployment, if there is one.
$signingSecret = $null
$vaultName = az keyvault list --resource-group $ResourceGroupName --query "[0].name" -o tsv 2>$null
if ($vaultName) {
    $signingSecret = Get-VaultSecret $vaultName 'webhook-signing-secret'
    if (-not $signingSecret) {
        throw "Key Vault $vaultName exists, but webhook-signing-secret could not be read. Refusing to rotate it silently."
    }
    Write-Host "Reusing the webhook signing secret stored in $vaultName."
}
else {
    $signingSecret = New-RandomSecret
    Write-Host 'Generated a new webhook signing secret.'
}

$operation = if ($WhatIf) { 'what-if' } else { 'create' }
az deployment sub $operation `
    --name webhook-processor `
    --location $Location `
    --template-file $template `
    --parameters location=$Location resourceGroupName=$ResourceGroupName imageTag=$ImageTag `
                 deployerPrincipalId=$deployerId alertEmail=$AlertEmail `
                 webhookSigningSecret=$signingSecret `
    --query properties.outputs
if ($LASTEXITCODE -ne 0) { throw 'Deployment failed.' }
