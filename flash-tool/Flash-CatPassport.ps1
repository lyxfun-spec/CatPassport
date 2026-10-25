[CmdletBinding()]
param(
    [ValidatePattern('^COM\d+$')]
    [string]$Port,

    [ValidateRange(115200, 921600)]
    [int]$Baud = 460800,

    [ValidateRange(0, 60)]
    [int]$MonitorSeconds = 12,

    [string]$FirmwareDirectory,

    [switch]$VerifyOnly,
    [switch]$ListOnly,
    [switch]$Yes
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:LogsDirectory = Join-Path $script:Root 'logs'
$script:LogPath = $null
$script:ExpectedStartupMarker = '小猫助手启动'

function Write-Status {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [ValidateSet('INFO', 'OK', 'WARN', 'ERROR')][string]$Level = 'INFO'
    )

    $colors = @{
        INFO = 'Cyan'
        OK = 'Green'
        WARN = 'Yellow'
        ERROR = 'Red'
    }
    $line = '[{0}] {1}' -f $Level, $Message
    Write-Host $line -ForegroundColor $colors[$Level]
    if ($script:LogPath) {
        $timestamped = '{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $line
        Add-Content -LiteralPath $script:LogPath -Value $timestamped -Encoding UTF8
    }
}

function Assert-RegularFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label 不存在: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label 不能是符号链接或重解析点: $Path"
    }
    return $item
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $digest = $algorithm.ComputeHash($stream)
        return ([BitConverter]::ToString($digest)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

function Get-SelectedFirmwareDirectory {
    if ($FirmwareDirectory) {
        $candidate = [IO.Path]::GetFullPath($FirmwareDirectory)
        if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
            throw "指定的固件目录不存在: $candidate"
        }
        return $candidate
    }

    $firmwareRoot = Join-Path $script:Root 'firmware'
    if (-not (Test-Path -LiteralPath $firmwareRoot -PathType Container)) {
        throw "固件目录不存在: $firmwareRoot"
    }
    $candidates = @(
        Get-ChildItem -LiteralPath $firmwareRoot -Directory -Force |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json') -PathType Leaf }
    )
    if ($candidates.Count -ne 1) {
        throw "firmware 目录必须恰好包含一个带 manifest.json 的固件包，当前数量: $($candidates.Count)"
    }
    return $candidates[0].FullName
}

function Test-FirmwareArchive {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $resolvedDirectory = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $directoryItem = Get-Item -LiteralPath $resolvedDirectory -Force
    if (($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "固件包目录不能是符号链接或重解析点: $resolvedDirectory"
    }
    if ($directoryItem.Name -notmatch '^[0-9a-fA-F]{64}$') {
        throw '固件包目录名必须是完整固件的 SHA-256。'
    }

    $manifestPath = Join-Path $resolvedDirectory 'manifest.json'
    Assert-RegularFile -Path $manifestPath -Label '固件清单' | Out-Null
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "无法解析固件清单: $($_.Exception.Message)"
    }

    if ($manifest.schema_version -ne 1) { throw "不支持的固件清单版本: $($manifest.schema_version)" }
    if ($manifest.target -ne 'esp32c3') { throw "固件目标不是 ESP32-C3: $($manifest.target)" }
    if ([int64]$manifest.flash_size_bytes -ne 8388608) { throw '固件不是 8 MB Flash 布局。' }
    if ($manifest.app_descriptor.project_name -ne 'FoloToy-AI-Passport') {
        throw "项目标识不匹配: $($manifest.app_descriptor.project_name)"
    }
    if ($manifest.app_descriptor.idf_version -ne 'v5.5.3') {
        throw "ESP-IDF 版本不匹配: $($manifest.app_descriptor.idf_version)"
    }
    if ([int64]$manifest.image_offsets.'bootloader/bootloader.bin' -ne 0) {
        throw '固件清单中的 bootloader 不从 0x0 开始。'
    }
    if ($manifest.app_descriptor.embedded_elf_sha256 -ne $manifest.app_elf_sha256) {
        throw '应用镜像与 ELF 调试信息不匹配。'
    }

    $directoryPrefix = $resolvedDirectory + [IO.Path]::DirectorySeparatorChar
    foreach ($fileProperty in $manifest.files.PSObject.Properties) {
        $relativeName = [string]$fileProperty.Name
        if ($relativeName -match '(^|/|\\)\.\.($|/|\\)' -or $relativeName -match '[:`;&|<>]' -or [IO.Path]::IsPathRooted($relativeName)) {
            throw "固件清单包含不安全的文件名: $relativeName"
        }
        $relativeWindowsPath = $relativeName.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $filePath = [IO.Path]::GetFullPath((Join-Path $resolvedDirectory $relativeWindowsPath))
        if (-not $filePath.StartsWith($directoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "固件文件超出归档目录: $relativeName"
        }
        $item = Assert-RegularFile -Path $filePath -Label "固件文件 $relativeName"
        if ([int64]$item.Length -ne [int64]$fileProperty.Value.size) {
            throw "固件文件大小不匹配: $relativeName"
        }
        $actualHash = Get-Sha256 -Path $filePath
        $expectedHash = ([string]$fileProperty.Value.sha256).ToLowerInvariant()
        if ($actualHash -ne $expectedHash) {
            throw "固件文件 SHA-256 不匹配: $relativeName"
        }
    }

    $fullHash = ([string]$manifest.full_bin_sha256).ToLowerInvariant()
    if ($directoryItem.Name.ToLowerInvariant() -ne $fullHash) {
        throw '固件包目录名与完整镜像 SHA-256 不匹配。'
    }
    $imagePath = Join-Path $resolvedDirectory 'FoloToy-AI-Passport-full.bin'
    $imageItem = Assert-RegularFile -Path $imagePath -Label '完整固件镜像'
    if ($imageItem.Length -gt 8388608) { throw '完整固件镜像超过 8 MB。' }
    $imageHash = Get-Sha256 -Path $imagePath
    if ($imageHash -ne $fullHash) { throw '完整固件镜像与清单不匹配。' }

    return [PSCustomObject]@{
        Directory = $resolvedDirectory
        ImagePath = $imagePath
        Hash = $fullHash
        Version = [string]$manifest.app_descriptor.version
        ElfHash = ([string]$manifest.app_elf_sha256).ToLowerInvariant()
    }
}

function Test-BundledEsptool {
    $toolManifestPath = Join-Path $script:Root 'tools\tool-manifest.json'
    Assert-RegularFile -Path $toolManifestPath -Label '烧录工具清单' | Out-Null
    $toolManifest = Get-Content -LiteralPath $toolManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($toolManifest.schema_version -ne 1 -or $toolManifest.file -ne 'esptool.exe') {
        throw '烧录工具清单无效。'
    }
    $toolPath = Join-Path $script:Root 'tools\esptool.exe'
    Assert-RegularFile -Path $toolPath -Label 'esptool.exe' | Out-Null
    $actualHash = Get-Sha256 -Path $toolPath
    if ($actualHash -ne ([string]$toolManifest.sha256).ToLowerInvariant()) {
        throw 'esptool.exe 的 SHA-256 与工具清单不匹配。'
    }
    return [PSCustomObject]@{
        Path = $toolPath
        Version = [string]$toolManifest.version
        Hash = $actualHash
    }
}

function Get-SerialPortCandidates {
    $names = @([IO.Ports.SerialPort]::GetPortNames() | Sort-Object -Unique)
    $details = @{}
    try {
        foreach ($entity in @(Get-CimInstance Win32_PnPEntity -ErrorAction Stop)) {
            if ($entity.Name -and $entity.Name -match '\((COM\d+)\)') {
                $details[$Matches[1].ToUpperInvariant()] = [string]$entity.Name
            }
        }
    }
    catch {
        Write-Status "无法读取设备详细名称，仍可使用串口编号: $($_.Exception.Message)" 'WARN'
    }

    return @($names | ForEach-Object {
        $upperName = $_.ToUpperInvariant()
        [PSCustomObject]@{
            Port = $upperName
            Description = if ($details.ContainsKey($upperName)) { $details[$upperName] } else { '未提供设备名称' }
        }
    })
}

function Select-SerialPort {
    param([Parameter(Mandatory = $true)][array]$Candidates)

    if ($Port) {
        $requested = $Port.ToUpperInvariant()
        if (-not ($Candidates.Port -contains $requested)) {
            throw "指定串口 $requested 当前不存在。"
        }
        return $requested
    }
    if ($Yes) {
        throw '非交互模式 -Yes 必须同时显式指定 -Port COMx。'
    }
    if ($Candidates.Count -eq 0) {
        throw '未发现串口设备。请开机，并使用支持数据传输的 USB 线连接 AI Passport。'
    }

    Write-Host ''
    Write-Host '发现的串口设备:' -ForegroundColor White
    for ($index = 0; $index -lt $Candidates.Count; $index++) {
        Write-Host ('  [{0}] {1}  {2}' -f ($index + 1), $Candidates[$index].Port, $Candidates[$index].Description)
    }
    $selection = Read-Host '请输入 AI Passport 对应的序号'
    $selectedIndex = 0
    if (-not [int]::TryParse($selection, [ref]$selectedIndex) -or $selectedIndex -lt 1 -or $selectedIndex -gt $Candidates.Count) {
        throw '输入的设备序号无效，未执行任何写入。'
    }
    return [string]$Candidates[$selectedIndex - 1].Port
}

function Invoke-Esptool {
    param(
        [Parameter(Mandatory = $true)][string]$ToolPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Operation
    )

    Write-Status "$Operation ..."
    $output = @(& $ToolPath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    foreach ($line in $output) {
        $text = [string]$line
        Write-Host $text
        if ($script:LogPath) { Add-Content -LiteralPath $script:LogPath -Value $text -Encoding UTF8 }
    }
    if ($exitCode -ne 0) {
        throw "$Operation 失败（esptool 退出码 $exitCode）。"
    }
    return ($output | ForEach-Object { [string]$_ }) -join "`n"
}

function Read-BoundedSerialLog {
    param(
        [Parameter(Mandatory = $true)][string]$SelectedPort,
        [Parameter(Mandatory = $true)][int]$Seconds
    )

    if ($Seconds -le 0) { return $false }
    Write-Status "收集 $Seconds 秒启动日志（打开串口在某些设备上可能引起一次复位）..."
    $serial = New-Object IO.Ports.SerialPort $SelectedPort, 115200, None, 8, One
    $serial.Encoding = New-Object Text.UTF8Encoding $false, $false
    $serial.ReadTimeout = 250
    $serial.DtrEnable = $false
    $serial.RtsEnable = $false
    $builder = New-Object Text.StringBuilder
    try {
        $serial.Open()
        $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            try {
                $chunk = $serial.ReadExisting()
                if ($chunk) {
                    [void]$builder.Append($chunk)
                    Write-Host $chunk -NoNewline
                    if ($script:LogPath) { Add-Content -LiteralPath $script:LogPath -Value $chunk -Encoding UTF8 }
                }
            }
            catch [TimeoutException] { }
            Start-Sleep -Milliseconds 100
        }
    }
    finally {
        if ($serial.IsOpen) { $serial.Close() }
        $serial.Dispose()
    }
    return $builder.ToString().Contains($script:ExpectedStartupMarker)
}

try {
    New-Item -ItemType Directory -Path $script:LogsDirectory -Force | Out-Null
    $script:LogPath = Join-Path $script:LogsDirectory ('flash-{0}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Write-Status '小猫助手 AI Passport 刷机工具'
    Write-Status "日志: $script:LogPath"

    $archiveDirectory = Get-SelectedFirmwareDirectory
    Write-Status '校验固件归档（会检查全部文件的 SHA-256）...'
    $firmware = Test-FirmwareArchive -Directory $archiveDirectory
    Write-Status "固件校验通过：版本 $($firmware.Version)" 'OK'
    Write-Status "完整镜像 SHA-256: $($firmware.Hash)"

    $tool = Test-BundledEsptool
    $versionOutput = Invoke-Esptool -ToolPath $tool.Path -Arguments @('version') -Operation '校验烧录工具'
    if (-not $versionOutput.Contains($tool.Version)) {
        throw "esptool.exe 版本与工具清单不匹配，期望 $($tool.Version)。"
    }
    Write-Status "烧录工具校验通过：esptool $($tool.Version)" 'OK'

    if ($VerifyOnly) {
        Write-Status '离线校验完成，未连接设备，也未执行任何写入。' 'OK'
        exit 0
    }

    $candidates = @(Get-SerialPortCandidates)
    if ($ListOnly) {
        if ($candidates.Count -eq 0) {
            Write-Status '未发现串口设备。' 'WARN'
        }
        else {
            foreach ($candidate in $candidates) {
                Write-Status ("{0}  {1}" -f $candidate.Port, $candidate.Description)
            }
        }
        Write-Status '仅列出设备，未执行任何写入。' 'OK'
        exit 0
    }

    $selectedPort = Select-SerialPort -Candidates $candidates
    Write-Status "检查 $selectedPort 上的芯片类型..."
    $chipOutput = Invoke-Esptool -ToolPath $tool.Path -Arguments @(
        '--chip', 'esp32c3', '--port', $selectedPort, '--connect-attempts', '3', 'chip_id'
    ) -Operation '识别 ESP32-C3'
    if ($chipOutput -notmatch 'ESP32-C3') { throw '连接的设备未确认为 ESP32-C3。' }

    $flashOutput = Invoke-Esptool -ToolPath $tool.Path -Arguments @(
        '--chip', 'esp32c3', '--port', $selectedPort, '--connect-attempts', '3', 'flash_id'
    ) -Operation '识别 Flash 容量'
    if ($flashOutput -notmatch '(?i)Detected flash size:\s*8MB') {
        throw '设备未报告 8 MB Flash，与 AI Passport 硬件基线不匹配。'
    }

    Write-Host ''
    Write-Host '================ 写入前确认 ================' -ForegroundColor Yellow
    Write-Host "设备端口: $selectedPort"
    Write-Host '目标芯片: ESP32-C3 / 8 MB Flash'
    Write-Host "固件版本: $($firmware.Version)"
    Write-Host "固件 SHA-256: $($firmware.Hash)"
    Write-Host '写入范围: 完整合并镜像，从 0x0 开始'
    Write-Host '数据影响: 将替换原厂固件，并可能重置 NVS/PHY 数据' -ForegroundColor Yellow
    Write-Host '操作边界: 不会执行整片擦除'
    Write-Host '================================================' -ForegroundColor Yellow
    if (-not $Yes) {
        $confirmation = Read-Host '确认设备和固件无误后，请输入 FLASH 开始写入'
        if ($confirmation -cne 'FLASH') {
            throw '未收到 FLASH 确认，未执行任何写入。'
        }
    }

    $writeOutput = Invoke-Esptool -ToolPath $tool.Path -Arguments @(
        '--chip', 'esp32c3',
        '--port', $selectedPort,
        '--baud', [string]$Baud,
        '--before', 'default_reset',
        '--after', 'hard_reset',
        '--connect-attempts', '7',
        'write_flash',
        '--flash_mode', 'dio',
        '--flash_freq', '80m',
        '--flash_size', '8MB',
        '--verify',
        '0x0', $firmware.ImagePath
    ) -Operation '写入并校验完整固件'
    if ($writeOutput -notmatch '(?i)Hash of data verified|verified') {
        throw '烧录工具未返回写后校验成功标志。'
    }
    Write-Status '固件写入和写后校验成功。' 'OK'

    try {
        $markerSeen = Read-BoundedSerialLog -SelectedPort $selectedPort -Seconds $MonitorSeconds
        if ($markerSeen) {
            Write-Status "已观察到启动标志“$script:ExpectedStartupMarker”。" 'OK'
        }
        elseif ($MonitorSeconds -gt 0) {
            Write-Status '未在有限时间内看到启动标志；烧录已成功，请继续检查屏幕、按键和桌面客户端连接。' 'WARN'
        }
    }
    catch {
        Write-Status "无法收集启动日志: $($_.Exception.Message)" 'WARN'
    }

    Write-Status '刷机流程完成。请按验收清单检查屏幕、按键、BLE、录音与 AI 回复。' 'OK'
    exit 0
}
catch {
    Write-Status $_.Exception.Message 'ERROR'
    Write-Status '未自动执行整片擦除或无限重试。请根据错误检查数据线、USB 接口、端口占用和设备下载状态后重新运行。' 'WARN'
    exit 1
}
