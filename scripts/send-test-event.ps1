# Sends one signed test event to the local API with curl and prints the HTTP status.
#
# Example:
#   ./scripts/send-test-event.ps1 -EventId evt-1 -PaymentId pay-1 -Type payment.captured -Amount 1500
#
# Against the API container (docker compose --profile app), signed with the secret from .env:
#   ./scripts/send-test-event.ps1 -EventId evt-1 -PaymentId pay-1 -FromEnvFile

param(
    [Parameter(Mandatory)] [string] $EventId,
    [Parameter(Mandatory)] [string] $PaymentId,
    [ValidateSet('payment.authorized', 'payment.captured', 'payment.failed')]
    [string] $Type = 'payment.captured',
    [long] $Amount = 1500,
    [string] $Currency = 'EUR',
    [string] $Url,
    # Overrides the signature, to test rejection.
    [string] $Signature,
    # Sign with WEBHOOK_SIGNING_SECRET from .env and default to the container port 8080.
    [switch] $FromEnvFile
)

$ErrorActionPreference = 'Stop'

if ($FromEnvFile) {
    $envFile = Join-Path $PSScriptRoot '..\.env'
    $secretLine = Get-Content $envFile | Where-Object { $_ -like 'WEBHOOK_SIGNING_SECRET=*' }
    if (-not $secretLine) { throw 'WEBHOOK_SIGNING_SECRET is not set in .env.' }
    $secret = $secretLine -replace '^WEBHOOK_SIGNING_SECRET=', ''
    if (-not $Url) { $Url = 'http://localhost:8080/webhooks/payments' }
}
else {
    $apiProject = Join-Path $PSScriptRoot '..\src\WebhookProcessor.Api'
    $secretLine = dotnet user-secrets list --project $apiProject | Where-Object { $_ -like 'Webhook:SigningSecret = *' }
    if (-not $secretLine) { throw 'Webhook:SigningSecret is not set in user secrets.' }
    $secret = $secretLine -replace '^Webhook:SigningSecret = ', ''
    if (-not $Url) { $Url = 'http://localhost:5271/webhooks/payments' }
}

$body = @{
    eventId    = $EventId
    type       = $Type
    paymentId  = $PaymentId
    amount     = $Amount
    currency   = $Currency
    occurredAt = [DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json -Compress

$bodyBytes = [Text.Encoding]::UTF8.GetBytes($body)
$timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()

if (-not $Signature) {
    $hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($secret))
    $payload = [Text.Encoding]::UTF8.GetBytes("$timestamp.") + $bodyBytes
    $Signature = 'sha256=' + (($hmac.ComputeHash($payload) | ForEach-Object { $_.ToString('x2') }) -join '')
    $hmac.Dispose()
}

# A file keeps the exact signed bytes. Windows PowerShell mangles quotes in native arguments.
$bodyFile = [IO.Path]::GetTempFileName()
try {
    [IO.File]::WriteAllBytes($bodyFile, $bodyBytes)
    curl.exe -s -o NUL -w "$EventId -> %{http_code}`n" -X POST $Url `
        -H 'Content-Type: application/json' `
        -H "X-Webhook-Timestamp: $timestamp" `
        -H "X-Webhook-Signature: $Signature" `
        --data-binary "@$bodyFile"
}
finally {
    Remove-Item $bodyFile
}
