[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0',
    [ValidateSet('x64')]
    [string]$Architecture = 'x64',
    [switch]$SkipAppPackage,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
$repoRoot = Split-Path $scriptRoot -Parent
$desktopRoot = Join-Path $repoRoot 'windows\CatAssistant.Desktop'
$payloadDir = Join-Path $desktopRoot 'out\小猫助手-win32-x64'
$artifacts = Join-Path $scriptRoot 'artifacts'
$msiOutput = Join-Path $artifacts 'msi'
$setupOutput = Join-Path $artifacts 'setup'
$icon = Join-Path $repoRoot 'assets\branding\cat-assistant-icon.ico'
$msiProject = Join-Path $scriptRoot 'msi\CatAssistant.Package.wixproj'
$setupProject = Join-Path $scriptRoot 'src\CatAssistant.Setup\CatAssistant.Setup.csproj'

function Assert-LastExitCode([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step 失败，退出代码 $LASTEXITCODE。" }
}

function Invoke-Sign([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { return }
    $signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signTool) { throw '指定了签名证书，但未找到 signtool.exe。请安装 Windows SDK。' }
    & $signTool.Source sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $Path
    Assert-LastExitCode "签名 $Path"
}

if (-not $SkipAppPackage) {
    Write-Host '==> 构建 Electron 应用'
    & pnpm --dir $desktopRoot package
    Assert-LastExitCode 'Electron 应用打包'
}

$appExe = Join-Path $payloadDir 'CatAssistant.exe'
if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
    throw "未找到应用产物：$appExe。请移除 -SkipAppPackage 重新构建。"
}

New-Item -ItemType Directory -Path $msiOutput -Force | Out-Null
New-Item -ItemType Directory -Path $setupOutput -Force | Out-Null

Write-Host '==> 构建事务型 MSI'
& dotnet clean $msiProject -c Release -v:q
Assert-LastExitCode 'MSI 构建缓存清理'
& dotnet build $msiProject -c Release -p:PayloadDir=$payloadDir -p:ProductVersion=$Version -p:AppIcon=$icon -o $msiOutput
Assert-LastExitCode 'MSI 构建'
$msiPath = Join-Path $msiOutput 'CatAssistant.msi'
if (-not (Test-Path -LiteralPath $msiPath -PathType Leaf)) { throw "MSI 构建成功但未找到 $msiPath" }
Invoke-Sign $msiPath

Write-Host '==> 构建自包含安装向导'
& dotnet clean $setupProject -c Release -v:q
Assert-LastExitCode '安装向导清理'
& dotnet publish $setupProject -c Release -r "win-$Architecture" --self-contained true -p:Version=$Version -p:CatAssistantAppVersion=$Version -p:InstallerMsi=$msiPath -o $setupOutput
Assert-LastExitCode '安装向导构建'

$rawSetup = Join-Path $setupOutput 'CatAssistantSetup.exe'
$finalSetup = Join-Path $artifacts "小猫助手-$Version-安装程序.exe"
Copy-Item -LiteralPath $rawSetup -Destination $finalSetup -Force
Invoke-Sign $finalSetup

$hash = Get-FileHash -LiteralPath $finalSetup -Algorithm SHA256
$hashLine = "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($finalSetup))"
[System.IO.File]::WriteAllText("$finalSetup.sha256", "$hashLine`r`n", [System.Text.UTF8Encoding]::new($false))

Write-Host "`n完成：$finalSetup"
Write-Host "SHA-256：$($hash.Hash)"
if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    Write-Warning '当前产物未签名。正式发布必须通过 -CertificateThumbprint 指定代码签名证书。'
}
