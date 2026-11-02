#Requires -Version 5.1
<#
.SYNOPSIS
    One-time local setup: creates .env with random secrets, seeds the mock host users database
    on SQL Server, and stores the API's connection strings in dotnet user secrets.
.PARAMETER PushSubject
    Contact for browser push services (mailto: or https: URL). Push stays off until this is set.
#>
[CmdletBinding()]
param(
    [string]$SqlServer = '.',
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]{0,127}$')]
    [string]$HostDatabase = 'ModeHostMock',
    [ValidatePattern('^(mailto:\S+@\S+|https://\S+)?$')]
    [string]$PushSubject = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
$apiProject = Join-Path $root 'src\Mode.Api\Mode.Api.csproj'

function New-Secret {
    $chars = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    # Prefix guarantees upper, lower and digit for SQL Server's password policy.
    'Aa9' + (-join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] }))
}

$secretKeys = 'POSTGRES_PASSWORD', 'HOST_DIRECTORY_READER_PASSWORD', 'PROCESSOR_KEY', 'FILES_LINK_SIGNING_KEY'

function New-SecretFor([string]$Key) {
    if ($Key -ne 'FILES_LINK_SIGNING_KEY') { return New-Secret }
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [Convert]::ToBase64String($bytes)
}

if (-not (Test-Path $envFile)) {
    Copy-Item (Join-Path $root '.env.example') $envFile
    Write-Host 'Created .env from .env.example.'
}

# Replace placeholders and add keys introduced since .env was created; existing secrets are kept.
$lines = [System.Collections.Generic.List[string]](Get-Content $envFile)
foreach ($key in $secretKeys) {
    $index = $lines.FindIndex({ param($l) $l -match "^$key=" })
    if ($index -lt 0) { $lines.Add("$key=$(New-SecretFor $key)") }
    elseif ($lines[$index] -eq "$key=change-me") { $lines[$index] = "$key=$(New-SecretFor $key)" }
}

function ConvertTo-Base64Url([byte[]]$Bytes) {
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

# Web Push (VAPID) key pair: P-256, public key as an uncompressed point, both base64url. Generated once, kept after.
if (-not ($lines | Where-Object { $_ -match '^VAPID_PRIVATE_KEY=.+' })) {
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $ecKey = $ecdsa.ExportParameters($true)
        $public = ConvertTo-Base64Url ([byte[]](@(4) + $ecKey.Q.X + $ecKey.Q.Y))
        $private = ConvertTo-Base64Url $ecKey.D
    } finally { $ecdsa.Dispose() }
    $lines = [System.Collections.Generic.List[string]]($lines | Where-Object { $_ -notmatch '^VAPID_(PUBLIC|PRIVATE)_KEY=' })
    $lines.Add("VAPID_PUBLIC_KEY=$public")
    $lines.Add("VAPID_PRIVATE_KEY=$private")
}
Set-Content -Path $envFile -Value $lines -Encoding UTF8

$settings = @{}
Get-Content $envFile | Where-Object { $_ -match '^\s*([A-Z_]+)=(.*)$' } | ForEach-Object {
    $settings[$Matches[1]] = $Matches[2]
}

Write-Host "Seeding $HostDatabase on $SqlServer ..."
& sqlcmd -S $SqlServer -E -C -b -i (Join-Path $root 'db\host-mock\setup.sql') `
    -v "HostDatabase=$HostDatabase" "ReaderPassword=$($settings.HOST_DIRECTORY_READER_PASSWORD)"
if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed with exit code $LASTEXITCODE" }

$modeConnection = "Host=localhost;Port=$($settings.POSTGRES_PORT);Database=mode;Username=mode;Password=$($settings.POSTGRES_PASSWORD)"
$directoryConnection = "Server=$SqlServer;Database=$HostDatabase;User Id=mode_reader;Password=$($settings.HOST_DIRECTORY_READER_PASSWORD);TrustServerCertificate=True;Application Name=Mode"

& dotnet user-secrets set 'ConnectionStrings:Mode' $modeConnection --project $apiProject | Out-Null
& dotnet user-secrets set 'UserDirectory:ConnectionString' $directoryConnection --project $apiProject | Out-Null
& dotnet user-secrets set 'Files:LinkSigningKey' $settings.FILES_LINK_SIGNING_KEY --project $apiProject | Out-Null
& dotnet user-secrets set 'Files:ProcessorKey' $settings.PROCESSOR_KEY --project $apiProject | Out-Null
& dotnet user-secrets set 'Push:PublicKey' $settings.VAPID_PUBLIC_KEY --project $apiProject | Out-Null
& dotnet user-secrets set 'Push:PrivateKey' $settings.VAPID_PRIVATE_KEY --project $apiProject | Out-Null
if ($PushSubject) { & dotnet user-secrets set 'Push:Subject' $PushSubject --project $apiProject | Out-Null }
if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets failed.' }

Write-Host 'Done. Stored connection strings, file keys and VAPID keys in user secrets.'
if (-not $PushSubject) { Write-Host 'Browser push is off until you rerun with -PushSubject mailto:you@example.com.' }
