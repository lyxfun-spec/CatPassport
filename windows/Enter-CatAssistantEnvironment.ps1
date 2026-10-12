[CmdletBinding()]
param(
    [switch]$SkipFirmware
)

$ErrorActionPreference = 'Stop'

$dotnet = Get-Command dotnet -ErrorAction Stop
$sdkList = & $dotnet.Source --list-sdks
if ([string]::IsNullOrWhiteSpace($sdkList)) {
    throw '.NET SDK 8 is required. Install Microsoft.DotNet.SDK.8 and retry.'
}

if (-not $SkipFirmware) {
    $idfExport = 'C:\Espressif\v5.5.3\esp-idf\export.ps1'
    if (-not (Test-Path -LiteralPath $idfExport)) {
        throw "ESP-IDF 5.5.3 was not found at $idfExport. Install it with the Espressif tools installer."
    }

    . $idfExport
    $idfVersion = idf.py --version
    if ($idfVersion -notmatch '^ESP-IDF v5\.5\.3$') {
        throw "Expected ESP-IDF v5.5.3, got: $idfVersion"
    }
}

Write-Host "dotnet: $(& $dotnet.Source --version)"
if (-not $SkipFirmware) {
    Write-Host "ESP-IDF: $(idf.py --version)"
}
