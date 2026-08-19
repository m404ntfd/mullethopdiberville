$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
    throw 'Node.js is required. Install the current Node.js LTS release, then run this setup again.'
}

Write-Host 'Cloudflare will open an official browser login if this PC is not connected yet.'
& npx --yes wrangler@latest whoami
if ($LASTEXITCODE -ne 0) { & npx --yes wrangler@latest login }

$databaseName = 'mullet-hop-kiosk-relay'
$bucketName = 'mullet-hop-kiosk-ads'
$workerName = 'mullet-hop-kiosk-relay'

$d1Output = (& npx --yes wrangler@latest d1 create $databaseName 2>&1 | Out-String)
$databaseId = [regex]::Match($d1Output, 'database_id\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $databaseId) {
    $list = (& npx --yes wrangler@latest d1 list --json | Out-String) | ConvertFrom-Json
    $databaseId = ($list | Where-Object name -eq $databaseName | Select-Object -First 1).uuid
}
if (-not $databaseId) { throw 'Cloudflare did not return the D1 database ID.' }

& npx --yes wrangler@latest r2 bucket create $bucketName 2>$null
$config = (Get-Content -LiteralPath 'wrangler.toml.example' -Raw).Replace('REPLACED_BY_SETUP', $databaseId)
Set-Content -LiteralPath 'wrangler.toml' -Value $config -Encoding utf8
& npx --yes wrangler@latest d1 execute $databaseName --remote --file schema.sql

$accessKeyBytes = [byte[]]::new(32)
[Security.Cryptography.RandomNumberGenerator]::Fill($accessKeyBytes)
$accessKey = [Convert]::ToBase64String($accessKeyBytes)
$accessKey | & npx --yes wrangler@latest secret put RELAY_ACCESS_KEY
Write-Host 'Deploying the secure relay...'
$deployOutput = (& npx --yes wrangler@latest deploy 2>&1 | Tee-Object -Variable deployLines | Out-String)
if ($LASTEXITCODE -ne 0) { throw 'Cloudflare Worker deployment failed.' }

$locationId = 'mullet-hop-' + ([guid]::NewGuid().ToString('N').Substring(0, 10))
$relayUrl = [regex]::Match($deployOutput, 'https://[a-z0-9.-]+\.workers\.dev').Value
if (-not $relayUrl) { throw 'The Worker was deployed, but its workers.dev URL could not be detected.' }
$result = [ordered]@{
    RelayUrl = $relayUrl
    LocationId = $locationId
    AccessKey = $accessKey
}
$result | ConvertTo-Json | Set-Content -LiteralPath 'Mullet-Hop-Remote-Connection.json' -Encoding utf8

Write-Host ''
Write-Host 'Cloudflare relay deployed.' -ForegroundColor Green
Write-Host "Relay URL:  $relayUrl"
Write-Host "Location ID: $locationId"
Write-Host "Access Key:  $accessKey"
Write-Host 'Enter these values in Controller Program > Remote Access on the on-site controller.'
Write-Host 'Use Copy Setup Code there, then Paste Setup Code on the remote controller and check This is a remote machine.'
Read-Host 'Press Enter to close'
