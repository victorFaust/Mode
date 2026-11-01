#Requires -Version 5.1
<#
.SYNOPSIS
    Writes client/openapi.json from the API's endpoint metadata, then regenerates the TypeScript types.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & dotnet build src/Mode.Api -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'API build failed.' }

    $env:MODE_OPENAPI_EXPORT = '1'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    try {
        & dotnet tool run swagger tofile --output client/openapi.json src/Mode.Api/bin/Debug/net10.0/Mode.Api.dll v1
        if ($LASTEXITCODE -ne 0) { throw 'OpenAPI export failed.' }
    } finally {
        Remove-Item Env:MODE_OPENAPI_EXPORT
    }

    Push-Location client
    try {
        & npm.cmd run gen:api
        if ($LASTEXITCODE -ne 0) { throw 'Type generation failed.' }
    } finally { Pop-Location }
} finally { Pop-Location }
