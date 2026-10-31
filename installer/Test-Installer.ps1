[CmdletBinding()]
param([switch]$RequireSigned)

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
$setupProject = Join-Path $scriptRoot 'src\CatAssistant.Setup\CatAssistant.Setup.csproj'
$msiProject = Join-Path $scriptRoot 'msi\CatAssistant.Package.wixproj'

Write-Host '==> 编译安装向导（不嵌入 MSI 的界面验证构建）'
& dotnet build $setupProject -c Release
if ($LASTEXITCODE -ne 0) { throw '安装向导编译失败。' }

$testExecutable = Get-ChildItem (Join-Path $scriptRoot 'src\CatAssistant.Setup\bin\Release') -Filter 'CatAssistantSetup.exe' -Recurse -File | Select-Object -First 1
if (-not $testExecutable) { throw '未找到安装向导测试程序。' }
$progressTest = Start-Process -FilePath $testExecutable.FullName -ArgumentList '--progress-self-test' -Wait -PassThru -WindowStyle Hidden
if ($progressTest.ExitCode -ne 0) { throw 'Windows Installer 进度计算自检失败。' }
Write-Host 'Windows Installer 进度计算自检通过。'
$versionPolicyTest = Start-Process -FilePath $testExecutable.FullName -ArgumentList '--version-policy-self-test' -Wait -PassThru -WindowStyle Hidden
if ($versionPolicyTest.ExitCode -ne 0) { throw '安装版本策略自检失败。' }
Write-Host '安装版本策略自检通过。'

Write-Host '==> 验证关键安装配置'
[xml]$package = Get-Content (Join-Path $scriptRoot 'msi\Package.wxs') -Raw -Encoding UTF8
$packageNode = $package.Wix.Package
if ($packageNode.Scope -ne 'perMachine') { throw 'MSI 必须使用 perMachine 范围。' }
if (-not $packageNode.MajorUpgrade) { throw 'MSI 缺少 MajorUpgrade 配置。' }
if ($packageNode.MajorUpgrade.AllowSameVersionUpgrades -ne 'yes') { throw 'MSI 必须允许同版本覆盖安装。' }
if (-not $packageNode.MediaTemplate.EmbedCab) { throw 'MSI 必须嵌入 CAB。' }
$wixProjectText = Get-Content $msiProject -Raw
if ($wixProjectText -notmatch 'ICE61') { throw '允许同版本 Major Upgrade 时必须明确记录 ICE61 例外。' }

$latestSetup = Get-ChildItem (Join-Path $scriptRoot 'artifacts') -Filter '小猫助手-*-安装程序.exe' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($latestSetup) {
    $signature = Get-AuthenticodeSignature -LiteralPath $latestSetup.FullName
    Write-Host "安装程序签名状态：$($signature.Status)"
    if ($RequireSigned -and $signature.Status -ne 'Valid') { throw '正式发布检查要求安装程序具有有效签名。' }

    Write-Host '==> 验证最终 EXE 内嵌 MSI'
    $selfTest = Start-Process -FilePath $latestSetup.FullName -ArgumentList '--self-test' -Wait -PassThru
    if ($selfTest.ExitCode -ne 0) { throw '安装程序自检失败，内嵌 MSI 缺失或无效。' }
}

Write-Host '安装器静态验证通过。'
