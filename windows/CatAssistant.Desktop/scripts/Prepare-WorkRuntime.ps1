param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$outputRoot = [IO.Path]::GetPathRoot($OutputDirectory)
if ([string]::IsNullOrWhiteSpace($outputRoot) -or $OutputDirectory.TrimEnd('\') -eq $outputRoot.TrimEnd('\') -or
    [IO.Path]::GetFileName($OutputDirectory.TrimEnd('\')) -ne 'work-runtime') {
    throw "OutputDirectory must be a dedicated directory named work-runtime, not a drive or broad directory: $OutputDirectory"
}
$env:PIP_INDEX_URL = 'https://pypi.org/simple'
$env:PIP_CONFIG_FILE = 'NUL'

$pythonVersion = '3.13.16'
$archiveArchitecture = if ($Architecture -eq 'arm64') { 'arm64' } else { 'amd64' }
$pythonHash = if ($Architecture -eq 'arm64') {
    '790D097697A2020477549A6764D89D22194C88D6E42CAEBCB2DB63791E004C94'
} else {
    '97DAE5274CC54867065E8D5A3226E48C35017ED332A0FDB0E27D5B5821961297'
}
$getPipHash = 'FB24E693BAB954209A063D90953621412CCAD4A500905A726286E038F508DDF6'
$packages = @(
    'python-docx==1.2.0',
    'python-pptx==1.0.2',
    'openpyxl==3.1.5',
    'pypdf==6.21.1',
    'pdfplumber==0.11.10',
    'reportlab==5.0.1',
    'lxml==6.1.3',
    'typing-extensions==4.16.0',
    'pillow==13.0.0',
    'xlsxwriter==3.2.9',
    'et-xmlfile==2.0.0',
    'pdfminer.six==20260107',
    'pypdfium2==5.14.0',
    'charset-normalizer==3.5.2',
    'cryptography==50.0.2',
    'cffi==2.1.1',
    'pycparser==3.11'
)
$manifestIdentity = "$pythonVersion-$Architecture-" + ($packages -join ',')
$manifestPath = Join-Path $OutputDirectory 'runtime.identity'
$pythonPath = Join-Path $OutputDirectory 'python.exe'
if ((Test-Path -LiteralPath $pythonPath) -and (Test-Path -LiteralPath $manifestPath) -and
    ((Get-Content -LiteralPath $manifestPath -Raw).Trim() -eq $manifestIdentity)) {
    Write-Host "Work runtime is current: $OutputDirectory"
    exit 0
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ("cat-assistant-runtime-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $archive = Join-Path $temporary 'python.zip'
    $pythonUrl = "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-$archiveArchitecture.zip"
    Invoke-WebRequest -Uri $pythonUrl -OutFile $archive
    $actualPythonHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash
    if ($actualPythonHash -ne $pythonHash) { throw "Python archive hash mismatch: $actualPythonHash" }

    if (Test-Path -LiteralPath $OutputDirectory) { Remove-Item -LiteralPath $OutputDirectory -Recurse -Force }
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $OutputDirectory

    $pthPath = Join-Path $OutputDirectory 'python313._pth'
    $pth = Get-Content -LiteralPath $pthPath
    $pth = $pth -replace '^#import site$', 'import site'
    $pth += 'Lib\site-packages'
    Set-Content -LiteralPath $pthPath -Value $pth -Encoding ascii

    $getPip = Join-Path $temporary 'get-pip.py'
    Invoke-WebRequest -Uri 'https://bootstrap.pypa.io/get-pip.py' -OutFile $getPip
    $actualGetPipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $getPip).Hash
    if ($actualGetPipHash -ne $getPipHash) { throw "get-pip.py hash mismatch: $actualGetPipHash" }
    & $pythonPath $getPip --no-warn-script-location --disable-pip-version-check
    if ($LASTEXITCODE -ne 0) { throw "get-pip.py failed with exit code $LASTEXITCODE" }

    & $pythonPath -m pip install --disable-pip-version-check --no-warn-script-location --no-cache-dir --only-binary=:all: --no-deps @packages
    if ($LASTEXITCODE -ne 0) { throw "Installing bundled Work dependencies failed with exit code $LASTEXITCODE" }
    & $pythonPath -c "import docx, pptx, openpyxl, pypdf, pdfplumber, reportlab; print('Work runtime verified')"
    if ($LASTEXITCODE -ne 0) { throw "Bundled Work dependency import check failed with exit code $LASTEXITCODE" }
    Set-Content -LiteralPath $manifestPath -Value $manifestIdentity -Encoding ascii
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
