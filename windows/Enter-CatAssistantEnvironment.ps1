[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$dotnet = Get-Command dotnet -ErrorAction Stop
$node = Get-Command node -ErrorAction Stop
$pnpm = Get-Command pnpm -ErrorAction Stop

$dotnetVersion = [Version](& $dotnet.Source --version)
$nodeVersion = [Version]((& $node.Source --version).TrimStart('v'))

if ($dotnetVersion.Major -ne 8) {
    throw ".NET SDK 8 is required; found $dotnetVersion."
}
if ($nodeVersion.Major -lt 22) {
    throw "Node.js 22 or newer is required; found $nodeVersion."
}

Write-Host "dotnet: $dotnetVersion"
Write-Host "node: $nodeVersion"
Write-Host "pnpm: $(& $pnpm.Source --version)"
Write-Host 'Ready. Run pnpm dev from the repository root.'
