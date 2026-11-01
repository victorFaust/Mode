#Requires -Version 5.1
<#
.SYNOPSIS
    One-time local setup: creates .env with random secrets, seeds the mock host users database
    on SQL Server, and stores the API's connection strings in dotnet user secrets.
#>
[CmdletBinding()]
param(
    [string]$SqlServer = '.\MYDB',
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]{0,127}$')]
    [string]$HostDatabase = 'ModeHostMock'
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
if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets failed.' }

Write-Host 'Done. Stored ConnectionStrings:Mode and UserDirectory:ConnectionString in user secrets.'
