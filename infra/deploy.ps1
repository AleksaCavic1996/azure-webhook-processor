# Deploys the whole infrastructure from infra/main.bicep.
#
# The PostgreSQL administrator password is never typed or printed:
#   - first run: a random password is generated and stored in Key Vault by the template
#   - later runs: the existing password is read back from Key Vault and reused
#
# Usage (from the repository root, after `az login`):
#   ./infra/deploy.ps1            deploy
#   ./infra/deploy.ps1 -WhatIf    show what would change, create nothing
param(
    [string]$Location = 'northeurope',
    [string]$ResourceGroupName = 'rg-webhook-processor',
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$template = Join-Path $PSScriptRoot 'main.bicep'

function New-RandomPassword {
    # 40 letters and digits: no characters that need escaping in a connection string.
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'.ToCharArray()
    $bytes = New-Object byte[] 40
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
}

$deployerId = az ad signed-in-user show --query id -o tsv
if (-not $deployerId) { throw 'Not logged in. Run az login first.' }

# Reuse the password of an existing deployment, if there is one.
$password = $null
$vaultName = az keyvault list --resource-group $ResourceGroupName --query "[0].name" -o tsv 2>$null
if ($vaultName) {
    $password = az keyvault secret show --vault-name $vaultName --name postgres-admin-password --query value -o tsv 2>$null
    if (-not $password) {
        throw "Key Vault $vaultName exists, but postgres-admin-password could not be read. Refusing to rotate the password silently."
    }
    Write-Host "Reusing the PostgreSQL password stored in $vaultName."
}
else {
    $password = New-RandomPassword
    Write-Host 'First deployment: generated a new PostgreSQL password.'
}

$operation = if ($WhatIf) { 'what-if' } else { 'create' }
az deployment sub $operation `
    --name webhook-processor `
    --location $Location `
    --template-file $template `
    --parameters location=$Location resourceGroupName=$ResourceGroupName `
                 deployerPrincipalId=$deployerId postgresAdministratorPassword=$password `
    --query properties.outputs
if ($LASTEXITCODE -ne 0) { throw 'Deployment failed.' }
