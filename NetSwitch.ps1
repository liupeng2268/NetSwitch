#requires -Version 5.1
<#
===============================================================================
 NetSwitch - 内网 IP / 网关 / DNS 多套配置一键切换工具
 版本: 1.0.0
 作者: WorkBuddy
 依赖: Windows 8 / Server 2012 及以上原生命令，无需安装任何第三方组件
===============================================================================

 用法:
   图形界面:  右键 NetSwitch.ps1 -> 使用 PowerShell 运行
              或 ./NetSwitch.bat
   命令行:    powershell -ExecutionPolicy Bypass -File NetSwitch.ps1 -Apply "办公网"
              powershell -ExecutionPolicy Bypass -File NetSwitch.ps1 -ListProfiles
              powershell -ExecutionPolicy Bypass -File NetSwitch.ps1 -ShowStatus
              powershell -ExecutionPolicy Bypass -File NetSwitch.ps1 -ExportFile backup.json
              powershell -ExecutionPolicy Bypass -File NetSwitch.ps1 -ImportFile my.json

 注意: 修改 IP 需要管理员权限，非管理员时会自动请求提权。
===============================================================================
#>

[CmdletBinding()]
param(
    [string]$Apply,          # 直接应用指定配置集（名称），用于桌面快捷方式
    [switch]$ListProfiles,   # 列出所有配置集
    [switch]$ListAdapters,   # 列出本机网卡
    [switch]$ShowStatus,     # 显示当前网络状态
    [string]$ImportFile,     # 导入配置文件
    [string]$ExportFile,     # 导出配置文件
    [switch]$Silent,         # 静默模式（少输出）
    [string]$ConfigPath,     # 指定配置文件路径
    [switch]$Elevated        # 内部标记：已经过 UAC 提权，避免循环提权
)

# ============================== 全局初始化 ==============================

$ErrorActionPreference = 'Continue'
$script:AppName    = 'NetSwitch'
$script:Version    = '1.0.0'
$script:IsCliMode  = $false
$script:LogBox     = $null
$script:LogFile    = ''
$script:ConfigFile = ''
$script:ConfigData = $null

# 脚本参数快照：函数内部读不到脚本作用域的 $PSBoundParameters（那是函数自己的），
# 提权重启时必须靠这份快照把 -Apply / -Silent / -ConfigPath 等原样带给新进程
$script:ScriptArgs = @{}
foreach ($scriptArgKey in $PSBoundParameters.Keys) {
    $script:ScriptArgs[$scriptArgKey] = $PSBoundParameters[$scriptArgKey]
}

# ====================== 网卡安全过滤（防止误伤虚拟网卡） ======================
# 背景：医院机器上通常同时存在 VMware / VirtualBox / Hyper-V 虚拟网卡、蓝牙网卡、
# 环回适配器，以及手机热点共享出来的「本地连接*」。配置集里若把 adapter 写成 "*"，
# 不加过滤就会把这些网卡一并刷成 DHCP，直接破坏虚拟机网络并踢掉无线。
#
# 【如何关闭】把下面这一行改成 $false 即可完全关闭过滤，恢复成「列出所有网卡」：
$script:ExcludeVirtualAdapters = $true
#
# 图形界面里也有对应开关：主界面左下方「包含虚拟网卡（VMware/蓝牙等）」勾选框，
# 勾选后等价于把本变量设为 $false（仅对当前进程有效，不写回脚本）。
#
# 【如何增减规则】直接改下面这个数组即可，匹配不区分大小写，
# 同时按「网卡名称」和「网卡描述(InterfaceDescription)」做 -like 通配匹配。
$script:VirtualAdapterPatterns = @(
    '*VMware*',        # VMware Workstation 虚拟网卡
    '*VMnet*',         # VMnet1 / VMnet8 等，名称里不含 VMware 的情况
    '*VirtualBox*',    # VirtualBox Host-Only / Bridged
    '*Hyper-V*',       # Hyper-V 虚拟交换机
    '*HyperV*',
    '*Bluetooth*',     # 蓝牙网络连接
    '*蓝牙*',
    '*Loopback*',      # 环回适配器
    '*环回*',
    '*本地连接*',      # 手机热点 / 网络共享产生的「本地连接* 」
    '*Teredo*',        # IPv6 隧道伪接口
    '*isatap*',
    '*WAN Miniport*'   # 广域网微型端口，非真实物理网卡
)

# ============================== 通用工具函数 ==============================

# 把一行内容追加到日志文件，写失败也绝不影响主流程
function Write-LogFileLine {
    param([string]$Line)
    if ([string]::IsNullOrWhiteSpace($script:LogFile)) { return }
    try {
        Add-Content -LiteralPath $script:LogFile -Value $Line -Encoding UTF8 -ErrorAction Stop
    } catch {
        # 忽略：日志不可写不代表功能不可用
    }
}

# 启动日志并记录一次运行环境的快照，方便事后排查问题
function Initialize-Logging {
    $script:LogFile = ''

    # 依次尝试：脚本目录 -> %APPDATA%\NetSwitch -> 系统临时目录，取第一个可写的
    $candidates = @()
    if ($PSScriptRoot) { $candidates += (Join-Path $PSScriptRoot 'NetSwitch.log') }
    $appData = ''
    try { $appData = [Environment]::GetFolderPath('ApplicationData') } catch { }
    if ($appData) { $candidates += (Join-Path $appData 'NetSwitch\NetSwitch.log') }
    $candidates += (Join-Path ([System.IO.Path]::GetTempPath()) 'NetSwitch.log')

    foreach ($path in $candidates) {
        try {
            $dir = Split-Path -Parent $path
            if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            # 试探写入，确认确实可写
            [System.IO.File]::AppendAllText($path, '')
            $script:LogFile = $path
            break
        } catch {
            continue
        }
    }

    $chosen = $script:LogFile

    # 日志超过 1MB 时只保留最近 500 行，避免长期无限增长
    if ($chosen -and (Test-Path -LiteralPath $chosen)) {
        try {
            if ((Get-Item -LiteralPath $chosen).Length -gt 1MB) {
                $tail = @(Get-Content -LiteralPath $chosen -Encoding UTF8 -Tail 500)
                [System.IO.File]::WriteAllLines($chosen, $tail, (New-Object System.Text.UTF8Encoding($true)))
            }
        } catch { }
    }

    Write-NSLog '------------------------------------------------------------'
    Write-NSLog "NetSwitch $script:Version 启动"
    try {
        Write-NSLog "环境: $([System.Environment]::OSVersion.VersionString) / PowerShell $($PSVersionTable.PSVersion.ToString())"
    } catch { }
    try {
        Write-NSLog "用户: $([Environment]::UserName) / 已获取管理员权限: $(Test-IsAdmin)"
    } catch { }
    Write-NSLog "脚本路径: $PSCommandPath"
    Write-NSLog "配置文件: $($script:ConfigFile)"
    Write-NSLog "日志文件: $(if ($script:LogFile) { $script:LogFile } else { '（无可用写入位置，未启用落盘）' })"
}

# 把日志（默认最近 300 行）取出来，用于复制给开发者排查
function Get-LogTail {
    param([int]$Lines = 300)
    if ([string]::IsNullOrWhiteSpace($script:LogFile) -or -not (Test-Path $script:LogFile)) { return '' }
    $content = @(Get-Content -LiteralPath $script:LogFile -Encoding UTF8 -ErrorAction SilentlyContinue)
    if ($content.Count -gt $Lines) { $content = $content[($content.Count - $Lines)..($content.Count - 1)] }
    return ($content -join "`r`n")
}

function Write-NSLog {
    param(
        [string]$Message,
        [string]$Level = 'INFO'   # INFO / OK / WARN / ERROR
    )
    $prefix = switch ($Level) {
        'OK'    { '[ √ ]' }
        'WARN'  { '[ ! ]' }
        'ERROR' { '[ X ]' }
        default { '[ - ]' }
    }
    $plainLine = "$prefix $Message"
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $plainLine"

    # 统一落盘，保证程序关闭后现场仍可追溯
    Write-LogFileLine $line

    if ($script:LogBox) {
        try {
            $color = switch ($Level) {
                'OK'    { [System.Drawing.Color]::SeaGreen }
                'WARN'  { [System.Drawing.Color]::DarkOrange }
                'ERROR' { [System.Drawing.Color]::Firebrick }
                default { [System.Drawing.Color]::DimGray }
            }
            # SelectionColor 方案在频繁追加时性能较差，这里用简单的文本追加
            $script:LogBox.AppendText("$line`r`n")
            $script:LogBox.SelectionStart   = $script:LogBox.TextLength
            $script:LogBox.ScrollToCaret()
        } catch {
            Write-Host $line
        }
    } else {
        $color = switch ($Level) {
            'OK'    { 'Green' }
            'WARN'  { 'Yellow' }
            'ERROR' { 'Red' }
            default { 'Gray' }
        }
        if ($script:IsCliMode) { Write-Host $line -ForegroundColor $color }
    }
}

function Test-IsAdmin {
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $false
    }
}

# 以管理员权限重新启动自身（保留原参数）
function Start-Elevated {
    param([string[]]$ForwardArgs = @())

    if ($null -eq $ForwardArgs -or $ForwardArgs.Count -eq 0) {
        # 用脚本级参数快照，不能用 $PSBoundParameters —— 函数内那份只含本函数的参数，恒为空
        $ForwardArgs = @()
        foreach ($key in $script:ScriptArgs.Keys) {
            if ($key -eq 'Elevated') { continue }
            $value = $script:ScriptArgs[$key]
            $ForwardArgs += "-$key"
            if ($value -is [switch]) { continue }
            if ([string]::IsNullOrWhiteSpace([string]$value)) { continue }
            $ForwardArgs += "`"$value`""
        }
        $ForwardArgs += '-Elevated'
    }

    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($ForwardArgs.Count -gt 0) { $argList += ' ' + ($ForwardArgs -join ' ') }

    try {
        Start-Process powershell -ArgumentList $argList -Verb RunAs -WindowStyle Normal | Out-Null
        return $true
    } catch {
        Write-NSLog "提权失败：$($_.Exception.Message)（用户可能取消了 UAC 授权）" 'WARN'
        return $false
    }
}

function Test-CommandExists {
    param([string]$Name)
    return ($null -ne (Get-Command $Name -ErrorAction SilentlyContinue))
}

function Assert-Environment {
    $missing = @()
    foreach ($cmd in @('Get-NetAdapter', 'New-NetIPAddress', 'Set-NetIPInterface', 'Set-DnsClientServerAddress', 'Get-NetRoute')) {
        if (-not (Test-CommandExists $cmd)) { $missing += $cmd }
    }
    if ($missing.Count -gt 0) {
        throw "当前系统缺少 NetTCPIP 模块命令（$($missing -join ', ')），请使用 Windows 8 / Server 2012 及以上版本。"
    }
}

# ============================== 配置文件读写 ==============================

function Get-DefaultConfigPath {
    # 优先使用脚本同目录的 profiles.json，便于连同脚本一起拷给别人
    $local = Join-Path $PSScriptRoot 'profiles.json'
    $roaming = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'NetSwitch\profiles.json'
    if (Test-Path $local)   { return $local }
    if (Test-Path $roaming) { return $roaming }
    return $local
}

function ConvertTo-Array {
    param($Value)
    if ($null -eq $Value) { return @() }
    return @($Value)
}

function ConvertTo-StringList {
    param($Value)
    $result = @()
    foreach ($item in (ConvertTo-Array $Value)) {
        $text = [string]$item
        if (-not [string]::IsNullOrWhiteSpace($text)) { $result += $text.Trim() }
    }
    return @($result)
}

function New-DefaultData {
    $dhcpProfile = [pscustomobject][ordered]@{
        name     = 'DHCP 自动获取'
        remark   = '恢复全部网卡为自动获取'
        adapters = @(
            [pscustomobject][ordered]@{
                adapter  = '*'
                enabled  = $true
                mode     = 'dhcp'
                ip       = ''
                mask     = ''
                gateways = @()
                dns      = @()
                dhcpDns  = $true
                defaultMetric = $null
                routes   = @()
            }
        )
    }

    $sampleProfile = [pscustomobject][ordered]@{
        name     = '示例：内网 192.168.10.x'
        remark   = '改成本机环境的地址后即可使用，网关可写 192.168.10.1:10 指定跃点数'
        adapters = @(
            [pscustomobject][ordered]@{
                adapter  = '以太网'
                enabled  = $true
                mode     = 'static'
                ip       = '192.168.10.50'
                mask     = '255.255.255.0'
                gateways = @('192.168.10.1')
                dns      = @('192.168.10.1', '114.114.114.114')
                dhcpDns  = $false
                defaultMetric = $null
                routes   = @()
            }
        )
    }

    return [pscustomobject][ordered]@{
        version   = 1
        updatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        profiles  = @($dhcpProfile, $sampleProfile)
    }
}

# JSON 反序列化后补齐缺失字段，保证后续代码无需到处判空
function Repair-ConfigData {
    param($Data)

    if ($null -eq $Data) { $Data = New-DefaultData }
    if (-not ($Data.PSObject.Properties.Name -contains 'version'))   { $Data | Add-Member -MemberType NoteProperty -Name version -Value 1 -Force }
    if (-not ($Data.PSObject.Properties.Name -contains 'profiles'))  { $Data | Add-Member -MemberType NoteProperty -Name profiles -Value @() -Force }

    $profiles = @()
    foreach ($profileItem in (ConvertTo-Array $Data.profiles)) {
        if ($null -eq $profileItem) { continue }
        foreach ($field in @('name', 'remark')) {
            if (-not ($profileItem.PSObject.Properties.Name -contains $field)) {
                $profileItem | Add-Member -MemberType NoteProperty -Name $field -Value '' -Force
            }
        }
        if ([string]::IsNullOrWhiteSpace($profileItem.name)) { continue }

        $adapters = @()
        foreach ($ad in (ConvertTo-Array $profileItem.adapters)) {
            if ($null -eq $ad) { continue }
            if (-not ($ad.PSObject.Properties.Name -contains 'adapter'))  { $ad | Add-Member NoteProperty adapter  '' -Force }
            if (-not ($ad.PSObject.Properties.Name -contains 'enabled'))  { $ad | Add-Member NoteProperty enabled  $true -Force }
            if (-not ($ad.PSObject.Properties.Name -contains 'mode'))     { $ad | Add-Member NoteProperty mode '' -Force }
            if (-not ($ad.PSObject.Properties.Name -contains 'ip'))       { $ad | Add-Member NoteProperty ip '' -Force }
            if (-not ($ad.PSObject.Properties.Name -contains 'mask'))     { $ad | Add-Member NoteProperty mask '' -Force }
            if (-not ($ad.PSObject.Properties.Name -contains 'dhcpDns'))  { $ad | Add-Member NoteProperty dhcpDns $true -Force }
            # 兼容旧配置：没有 defaultMetric 字段的补 $null（= 交给系统自动分配）
            if (-not ($ad.PSObject.Properties.Name -contains 'defaultMetric')) { $ad | Add-Member NoteProperty defaultMetric $null -Force }
            $ad.defaultMetric = ConvertTo-MetricValue ([string]$ad.defaultMetric)

            $sortedGateways = ConvertTo-StringList $ad.gateways
            $sortedDns      = ConvertTo-StringList $ad.dns
            $sortedRoutes   = ConvertTo-StringList $ad.routes

            $ad | Add-Member NoteProperty gateways ([object[]]$sortedGateways) -Force
            $ad | Add-Member NoteProperty dns      ([object[]]$sortedDns)      -Force
            $ad | Add-Member NoteProperty routes   ([object[]]$sortedRoutes)   -Force

            # 简化写法兜底：没写 mode 时，填了 IP 就按静态处理，否则按 DHCP 处理
            if ([string]::IsNullOrWhiteSpace($ad.mode)) {
                $ad.mode = if (-not [string]::IsNullOrWhiteSpace($ad.ip)) { 'static' } else { 'dhcp' }
            }
            $ad.mode = $ad.mode.ToLower()
            if ($ad.mode -notin @('dhcp', 'static')) { $ad.mode = 'dhcp' }
            if ($ad.mode -eq 'static' -and -not [string]::IsNullOrWhiteSpace($ad.ip)) {
                $ad.dhcpDns = ($sortedDns.Count -eq 0)
                if ([string]::IsNullOrWhiteSpace($ad.mask)) { $ad.mask = '255.255.255.0' }
            }

            $adapters += $ad
        }
        $profileItem | Add-Member NoteProperty adapters ([object[]]$adapters) -Force
        $profiles += $profileItem
    }

    $Data | Add-Member NoteProperty profiles ([object[]]$profiles) -Force
    return $Data
}

function Import-ConfigFile {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        $data = New-DefaultData
        $script:ConfigData = Repair-ConfigData $data
        return $script:ConfigData
    }

    try {
        $raw = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
        if ($raw.Length -gt 0 -and $raw[0] -eq [char]0xFEFF) { $raw = $raw.Substring(1) }
        $data = $raw | ConvertFrom-Json
        $script:ConfigData = Repair-ConfigData $data
        return $script:ConfigData
    } catch {
        Write-NSLog "配置文件解析失败（$Path）：$($_.Exception.Message)，已使用默认配置启动。" 'ERROR'
        $script:ConfigData = Repair-ConfigData (New-DefaultData)
        return $script:ConfigData
    }
}

# profiles.json 自身备份的保留份数，超出的旧备份会被轮转删除
$script:ConfigBackupKeepCount = 5

# 把现有的配置文件复制一份（profiles.bak-yyyyMMdd-HHmmss.json）再让主流程覆盖写，
# 写坏或误删时能回滚。任何失败都只记 WARN，绝不影响保存主流程
function Backup-ConfigFile {
    param([string]$Path = $script:ConfigFile)

    try {
        if ([string]::IsNullOrWhiteSpace($Path)) { return }
        if (-not (Test-Path -LiteralPath $Path)) { return }

        $directory = Split-Path -Parent $Path
        if ([string]::IsNullOrWhiteSpace($directory)) { $directory = $PSScriptRoot }
        $stem = [System.IO.Path]::GetFileNameWithoutExtension($Path)

        $target = Join-Path $directory ("{0}.bak-{1}.json" -f $stem, (Get-Date).ToString('yyyyMMdd-HHmmss'))
        Copy-Item -LiteralPath $Path -Destination $target -Force -ErrorAction Stop

        # 只清理本程序自己生成的 .bak-yyyyMMdd-HHmmss.json（严格匹配日期时间戳），
        # 不使用宽泛通配符，避免误伤同目录里其他名字相近的文件
        $pattern = '^{0}\.bak-\d{{8}}-\d{{6}}\.json$' -f [regex]::Escape($stem)
        $existing = @(Get-ChildItem -LiteralPath $directory -File -ErrorAction SilentlyContinue |
                      Where-Object { $_.Name -match $pattern } |
                      Sort-Object Name -Descending)
        if ($existing.Count -gt $script:ConfigBackupKeepCount) {
            $existing | Select-Object -Skip $script:ConfigBackupKeepCount | ForEach-Object {
                try {
                    Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
                } catch {
                    Write-NSLog "删除过期配置备份失败（$($_.FullName)）：$($_.Exception.Message)" 'WARN'
                }
            }
        }

        Write-NSLog "已备份现有配置到 $target" 'OK'
    } catch {
        Write-NSLog "备份现有配置失败（不影响本次保存）：$($_.Exception.Message)" 'WARN'
    }
}

function Save-ConfigFile {
    param(
        [string]$Path = $script:ConfigFile,
        $Data = $script:ConfigData
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { throw '未指定配置文件路径。' }

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    # 覆盖写之前先给现有配置留一份备份，写坏时可直接回滚
    Backup-ConfigFile -Path $Path

    $Data.updatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $json = $Data | ConvertTo-Json -Depth 12
    # 带 BOM 写入，保证 Windows 记事本打开不乱码
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($true)))
    Write-NSLog "配置已保存到 $Path" 'OK'
}

# ============================== 切换前自动备份 ==============================

# 备份保留份数，超出的旧备份会被清理
$script:BackupKeepCount = 10

# 备份目录：与 profiles.json 同级的 backups 子目录
function Get-BackupDirectory {
    $base = $script:ConfigFile
    if ([string]::IsNullOrWhiteSpace($base)) { $base = Get-DefaultConfigPath }
    $dir = Split-Path -Parent $base
    if ([string]::IsNullOrWhiteSpace($dir)) { $dir = $PSScriptRoot }
    return (Join-Path $dir 'backups')
}

# 按名称倒序列出全部备份文件（新的在前）
function Get-BackupFiles {
    $dir = Get-BackupDirectory
    if (-not (Test-Path $dir)) { return @() }
    return @(Get-ChildItem -Path $dir -Filter 'NetSwitch-backup-*.json' -File -ErrorAction SilentlyContinue |
             Sort-Object Name -Descending)
}

# 采集各网卡当前的 IPv4 / 掩码 / 网关 / DNS，组装成与 profiles.json 同构的配置集
function Get-CurrentNetworkProfile {
    $adapters = @()

    foreach ($adapter in (Get-AdapterList)) {
        $index = $adapter.InterfaceIndex

        $entry = [pscustomobject][ordered]@{
            adapter  = $adapter.Name
            enabled  = $true
            mode     = 'dhcp'
            ip       = ''
            mask     = ''
            dhcpDns  = $true
            gateways = @()
            dns      = @()
            routes   = @()
        }

        # 只有手动设置了静态地址才记录为 static，DHCP 获取到的地址不写死
        try {
            $dhcpState = (Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -ErrorAction Stop).Dhcp
            if ($dhcpState -eq 'Disabled') {
                $addr = Get-NetIPAddress -InterfaceIndex $index -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                        Where-Object { $_.IPAddress -ne '127.0.0.1' } | Select-Object -First 1
                if ($addr) {
                    $entry.mode = 'static'
                    $entry.ip   = [string]$addr.IPAddress
                    $entry.mask = ConvertFrom-PrefixToMask $addr.PrefixLength
                }
            }
        } catch { }

        $gateways = @(Get-NetRoute -InterfaceIndex $index -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                      ForEach-Object { $_.NextHop })
        $dns = @(Get-DnsClientServerAddress -InterfaceIndex $index -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                 ForEach-Object { $_.ServerAddresses } | Select-Object -Unique)

        $entry.gateways = [object[]]$gateways
        $entry.dns      = [object[]]$dns
        $entry.dhcpDns  = ($dns.Count -eq 0)

        $adapters += $entry
    }

    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    return [pscustomobject][ordered]@{
        version   = 1
        updatedAt = $stamp
        profiles  = @(
            [pscustomobject][ordered]@{
                name     = "自动备份 $stamp"
                remark   = '切换配置集之前自动保存的当前网络状态，可通过「备份记录」导入后切回'
                adapters = [object[]]$adapters
            }
        )
    }
}

# 写出备份文件并裁剪旧备份；任何失败都只记日志，绝不影响切换流程
function Save-NetworkBackup {
    try {
        $dir = Get-BackupDirectory
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

        $data = Get-CurrentNetworkProfile
        $file = Join-Path $dir ("NetSwitch-backup-{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        $json = $data | ConvertTo-Json -Depth 12
        [System.IO.File]::WriteAllText($file, $json, (New-Object System.Text.UTF8Encoding($true)))

        $existing = @(Get-BackupFiles)
        if ($existing.Count -gt $script:BackupKeepCount) {
            $existing | Select-Object -Skip $script:BackupKeepCount | ForEach-Object {
                try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue } catch { }
            }
        }

        Write-NSLog "已自动备份当前网络状态：$file" 'OK'
        return $file
    } catch {
        Write-NSLog "自动备份失败（不影响本次切换）：$($_.Exception.Message)" 'WARN'
        return ''
    }
}

# 把备份文件里的配置集合并进当前配置（重名自动加后缀），返回新增条数
function Import-BackupAsProfiles {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { throw '备份文件不存在。' }

    $raw = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    if ($raw.Length -gt 0 -and $raw[0] -eq [char]0xFEFF) { $raw = $raw.Substring(1) }
    $data = Repair-ConfigData ($raw | ConvertFrom-Json)

    $list = [System.Collections.ArrayList]@(ConvertTo-Array $script:ConfigData.profiles)
    $taken = @{}
    foreach ($p in $list) { if ($null -ne $p -and $p.name) { $taken[[string]$p.name] = $true } }

    $added = 0
    foreach ($item in (ConvertTo-Array $data.profiles)) {
        if ($null -eq $item -or [string]::IsNullOrWhiteSpace($item.name)) { continue }
        $baseName = [string]$item.name
        $name = $baseName
        $n = 1
        while ($taken.ContainsKey($name)) { $n++; $name = "$baseName ($n)" }
        $taken[$name] = $true
        $item.name = $name
        [void]$list.Add($item)
        $added++
    }

    if ($added -gt 0) {
        $script:ConfigData.profiles = [object[]]$list
        Save-ConfigFile | Out-Null
    }
    return $added
}

# 读出备份文件，按当前机器的网卡做匹配，返回将要被改动的「网卡 + 参数」清单（用于还原前确认与执行）
function Get-BackupRestoreTargets {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { throw '备份文件不存在。' }

    $raw = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    if ($raw.Length -gt 0 -and $raw[0] -eq [char]0xFEFF) { $raw = $raw.Substring(1) }
    $data = Repair-ConfigData ($raw | ConvertFrom-Json)

    $targets = @()
    foreach ($profileItem in (ConvertTo-Array $data.profiles)) {
        if ($null -eq $profileItem) { continue }
        foreach ($config in (ConvertTo-Array $profileItem.adapters)) {
            if ($null -eq $config) { continue }
            if (-not $config.enabled) { continue }
            foreach ($adapter in (Find-MatchedAdapters $config.adapter)) {
                $targets += [pscustomobject]@{
                    Profile = [string]$profileItem.name
                    Adapter = $adapter
                    Config  = $config
                }
            }
        }
    }
    return @($targets)
}

# 把备份文件里的网络参数直接写回网卡（不用先导入成配置集），返回是否全部成功
function Restore-BackupToAdapters {
    param([string]$Path)

    if (-not (Test-IsAdmin)) {
        Write-NSLog '还原备份需要管理员权限，请以管理员身份运行本程序后再试。' 'ERROR'
        return $false
    }

    $targets = @()
    try {
        $targets = @(Get-BackupRestoreTargets -Path $Path)
    } catch {
        Write-NSLog "读取备份失败：$($_.Exception.Message)" 'ERROR'
        return $false
    }

    if ($targets.Count -eq 0) {
        Write-NSLog '备份中没有匹配到本机现有的网卡，未做任何改动。' 'WARN'
        return $false
    }

    Write-NSLog "开始还原备份：$Path" 'INFO'

    # 还原同样会先清空网卡地址，动手之前先把当前状态再存一份，方便出错时回退
    Save-NetworkBackup | Out-Null

    $failed    = 0
    $processed = 0

    foreach ($target in $targets) {
        $processed++
        try {
            Set-AdapterConfig -AdapterObject $target.Adapter -Config $target.Config
        } catch {
            $failed++
            Write-NSLog "网卡「$($target.Adapter.Name)」还原失败：$($_.Exception.Message)" 'ERROR'
        }
    }

    Start-Sleep -Milliseconds 500

    if ($failed -eq 0) {
        Write-NSLog "备份已还原到网卡（共处理 $processed 块网卡）" 'OK'
        return $true
    }

    Write-NSLog "备份还原完成，但有 $failed 项失败（共 $processed 块网卡）。" 'WARN'
    return $false
}

# ============================== 网络参数解析 ==============================

function ConvertTo-PrefixLength {
    param([string]$Mask)

    if ([string]::IsNullOrWhiteSpace($Mask)) { return 24 }

    $text = $Mask.Trim()
    if ($text -match '^\d{1,2}$') {
        $numeric = [int]$text
        if ($numeric -ge 0 -and $numeric -le 32) { return $numeric }
    }

    try {
        $bytes = ([System.Net.IPAddress]::Parse($text)).GetAddressBytes()
        $prefix = 0
        foreach ($byte in $bytes) {
            for ($bit = 7; $bit -ge 0; $bit--) {
                $prefix += (($byte -shr $bit) -band 1)
            }
        }
        return $prefix
    } catch {
        Write-NSLog "子网掩码格式不合法（$Mask），已按 255.255.255.0 处理。" 'WARN'
        return 24
    }
}

function Split-ItemList {
    param([string]$Text)
    $items = @()
    if ([string]::IsNullOrWhiteSpace($Text)) { return @() }
    foreach ($part in ($Text -split '[;,，；\s]+')) {
        $value = $part.Trim()
        if (-not [string]::IsNullOrWhiteSpace($value)) { $items += $value }
    }
    return @($items)
}

# 网关支持 "192.168.1.1" 与 "192.168.1.1:10"（冒号后为跃点数）两种写法
function ConvertTo-GatewayEntry {
    param([string]$Text)

    $result = @()
    foreach ($item in (Split-ItemList $Text)) {
        $address = $item
        $metric  = $null
        if ($item -match '^(.+?)[:：]\s*(\d+)$') {
            $address = $matches[1].Trim()
            $metric  = [int]$matches[2]
        }
        $result += [pscustomobject]@{
            Address = $address
            Metric  = $metric
        }
    }
    return @($result)
}

# 附加路由写法: "目标网络>下一跳"，例如 "10.0.0.0/8>192.168.10.254"
function ConvertTo-RouteEntry {
    param([string]$Text)

    $result = @()
    foreach ($item in (Split-ItemList $Text)) {
        $routeText = $item -replace '->', '>' -replace '=>', '>'
        if ($routeText -match '^(.+?)>\s*(.+)$') {
            $destination = $matches[1].Trim()
            $nextHop     = $matches[2].Trim()
            $result += [pscustomobject]@{
                Destination = $destination
                NextHop     = $nextHop
            }
        } else {
            Write-NSLog "附加路由格式不正确，已忽略：$item（正确写法 10.0.0.0/8>192.168.10.254）" 'WARN'
        }
    }
    return @($result)
}

# 判断一块网卡是否属于「虚拟 / 非物理」网卡。
# 关闭开关（$script:ExcludeVirtualAdapters = $false）时一律返回 $false，即不做任何排除。
function Test-VirtualAdapter {
    param($Adapter)

    if ($null -eq $Adapter) { return $false }
    if (-not $script:ExcludeVirtualAdapters) { return $false }

    $name = ''
    $desc = ''
    $props = @($Adapter.PSObject.Properties.Name)
    if ($props -contains 'Name')                  { $name = [string]$Adapter.Name }
    if ($props -contains 'InterfaceDescription')  { $desc = [string]$Adapter.InterfaceDescription }

    # 网卡对象自带 Virtual 标记时优先采信（部分系统/版本会暴露该属性）
    if ($props -contains 'Virtual') {
        try { if ([bool]$Adapter.Virtual) { return $true } } catch { }
    }

    foreach ($pattern in $script:VirtualAdapterPatterns) {
        if ($name -and ($name -like $pattern)) { return $true }
        if ($desc -and ($desc -like $pattern)) { return $true }
    }
    return $false
}

function Get-AdapterList {
    try {
        $list = @(Get-NetAdapter -ErrorAction Stop | Sort-Object { $_.InterfaceIndex })
    } catch {
        Write-NSLog "读取网卡列表失败：$($_.Exception.Message)" 'ERROR'
        return @()
    }

    # 安全第一：默认把虚拟 / 非物理网卡挡在门外，避免 adapter:"*" 这类通配配置误伤它们
    if (-not $script:ExcludeVirtualAdapters) { return @($list) }

    $kept    = @()
    $skipped = @()
    foreach ($adapter in $list) {
        if (Test-VirtualAdapter $adapter) { $skipped += [string]$adapter.Name }
        else                              { $kept   += $adapter }
    }
    if ($skipped.Count -gt 0) {
        Write-NSLog "已跳过 $($skipped.Count) 块虚拟/非物理网卡（$($skipped -join '、')）。如需把它们也纳入管理，请在主界面勾选「包含虚拟网卡」，或把脚本变量 ExcludeVirtualAdapters 设为 false。" 'INFO'
    }
    return @($kept)
}

# 把各种写法的 MAC 地址归一化成「只剩十六进制字符的大写串」，
# 这样 B0-25-AA-6F-36-44 / b0:25:aa:6f:36:44 / B025AA6F3644 都能互相认出来
function ConvertTo-MacKey {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return (([string]$Value).ToUpperInvariant() -replace '[^0-9A-F]', '')
}

# 把 InterfaceGuid 归一化成「无大括号、无首尾空白、全小写」的串，便于比较与前缀匹配
function ConvertTo-GuidKey {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return ((([string]$Value).Trim() -replace '[{}]', '').Trim().ToLowerInvariant())
}

function Find-MatchedAdapters {
    param([string]$Pattern)

    $all = Get-AdapterList
    if ([string]::IsNullOrWhiteSpace($Pattern) -or $Pattern -eq '*') { return $all }

    # 第一级：网卡名称精确匹配（最常见的正常路径，不打日志，避免刷新/预览时刷屏）
    $exact = @($all | Where-Object { $_.Name -eq $Pattern })
    if ($exact.Count -gt 0) { return $exact }

    # 第二级：网卡名称模糊匹配（同样是正常路径，不打日志）
    $fuzzy = @($all | Where-Object { $_.Name -like "*$Pattern*" })
    if ($fuzzy.Count -gt 0) { return $fuzzy }

    # 第三级：按稳定标识匹配（硬件描述 / MAC / InterfaceGuid）。
    # 走到这里说明网卡很可能被改过名，值得记一条日志说明命中的依据。
    # 网卡在 Windows 里被改名后，前两级必然落空；这三项不随改名变化，是最可靠的兜底。
    $macKey  = ConvertTo-MacKey  $Pattern
    $guidKey = ConvertTo-GuidKey $Pattern

    # 硬件描述：用户往往只记得「Realtek」「Intel」这类片段，所以做模糊匹配
    $byDesc = @($all | Where-Object { $_.InterfaceDescription -like "*$Pattern*" })
    if ($byDesc.Count -gt 0) {
        $descNames = @($byDesc | ForEach-Object { [string]$_.Name }) -join '、'
        Write-NSLog "网卡「$Pattern」按硬件描述匹配到 $($byDesc.Count) 块网卡（$descNames）。" 'INFO'
        return $byDesc
    }

    # MAC：不适合模糊匹配，只在归一化后完全相等、且长度足够时才认定命中
    if ($macKey.Length -ge 6) {
        $byMac = @($all | Where-Object { (ConvertTo-MacKey ([string]$_.MacAddress)) -eq $macKey })
        if ($byMac.Count -gt 0) {
            $macNames = @($byMac | ForEach-Object { [string]$_.Name }) -join '、'
            Write-NSLog "网卡「$Pattern」按 MAC 地址匹配到 $($byMac.Count) 块网卡（$macNames）。" 'INFO'
            return $byMac
        }
    }

    # GUID：支持完全相等或前缀匹配（配置里可能只填了前一小段）
    if ($guidKey.Length -ge 8) {
        $byGuid = @()
        foreach ($candidate in $all) {
            $key = ConvertTo-GuidKey ([string]$candidate.InterfaceGuid)
            if ($key.Length -lt 8) { continue }
            if ($key -eq $guidKey -or $key -like "$guidKey*" -or $guidKey -like "$key*") { $byGuid += $candidate }
        }
        if ($byGuid.Count -gt 0) {
            $guidNames = @($byGuid | ForEach-Object { [string]$_.Name }) -join '、'
            Write-NSLog "网卡「$Pattern」按 InterfaceGuid 匹配到 $($byGuid.Count) 块网卡（$guidNames）。" 'INFO'
            return $byGuid
        }
    }

    Write-NSLog "未找到匹配「$Pattern」的网卡，已跳过该项配置。网卡匹配字段可以填网卡名称（如「以太网」）、硬件描述（如 Realtek PCIe GbE Family Controller）或 MAC 地址（如 B0-25-AA-6F-36-44）；填硬件描述或 MAC 时，以后在系统里给网卡改名也不会失效。" 'WARN'
    return @()
}

# ============================== 存储用 MAC、显示用名称 ==============================
#
# 设计取舍：网卡名称可以在 Windows 里随便改，MAC 不会。
# 所以配置集里一律存 MAC（保证改名后仍能识别），但界面上一律显示网卡名（保证人看得懂）。
# 用户在编辑器里照常填「以太网」，保存时自动换成该网卡的 MAC；
# 读回来显示时再把 MAC 翻译回当前网卡名 —— 改了名就显示新名字，而匹配依旧成立。

# 显示用：把配置集里存的稳定标识（MAC / GUID）翻译成当前网卡名称，方便人看。
# 查不到对应网卡时原样返回，绝不丢信息。
function Get-AdapterDisplayText {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    $text = $Value.Trim()

    # 只需翻译 MAC 形态与 GUID 形态；普通名称和通配符原样返回，避免多余开销
    $macKey = ConvertTo-MacKey $text
    $isMac  = ($macKey.Length -ge 6) -and ($text -match '^[0-9A-Fa-f:.\-]+$')
    $isGuid = ($text -match '^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-')
    if (-not $isMac -and -not $isGuid) { return $text }

    foreach ($adapter in (Get-AdapterList)) {
        if ($isMac -and (ConvertTo-MacKey ([string]$adapter.MacAddress)) -eq $macKey) {
            return [string]$adapter.Name
        }
        if ($isGuid -and (ConvertTo-GuidKey ([string]$adapter.InterfaceGuid)) -eq (ConvertTo-GuidKey $text)) {
            return [string]$adapter.Name
        }
    }
    return $text
}

# 存储用：把用户填的网卡名归一化成该网卡的 MAC。
# 仅当「唯一匹配一块网卡」时才转换 —— 通配符（* / 以太网*）、匹配到多块、匹配不到，
# 都保持原样，避免悄悄改变通配语义或把无效值吞掉。
function ConvertTo-AdapterStorageValue {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    $text = $Value.Trim()

    # 含通配符的一律不转换
    if ($text -eq '*' -or $text.Contains('*') -or $text.Contains('?')) { return $text }

    $matched = @(Find-MatchedAdapters $text)
    if ($matched.Count -ne 1) { return $text }

    $mac = [string]$matched[0].MacAddress
    if ([string]::IsNullOrWhiteSpace($mac)) { return $text }
    return $mac
}

# ============================== 核心：切换引擎 ==============================

# ---------- 附加路由应用记录 ----------
# Reset-InterfaceIPv4 只清 IPv4 地址和 0.0.0.0/0 默认路由，清不掉 NetSwitch 上次写入的附加路由，
# 于是「配置集里删掉某条路由、再切一次、系统里那条还在」就成了必然。
# 解决办法：把自己加过的路由记到脚本目录的 applied-routes.json，下次切换同一网卡时先按记录删一遍。
# 存文件而不是只放内存，是因为图形界面切换完通常就退出了，内存记录会随之丢失。
# 注意：只删自己加过的，绝不碰系统或用户手工添加的路由。

function Get-AppliedRoutesPath {
    $candidates = @()
    if ($PSScriptRoot) { $candidates += (Join-Path $PSScriptRoot 'applied-routes.json') }
    if (-not [string]::IsNullOrWhiteSpace($script:ConfigFile)) {
        $dir = Split-Path -Parent $script:ConfigFile
        if ($dir) { $candidates += (Join-Path $dir 'applied-routes.json') }
    }
    $candidates += (Join-Path ([System.IO.Path]::GetTempPath()) 'NetSwitch-applied-routes.json')

    foreach ($path in $candidates) {
        try {
            # 试探写入，确认这个位置真的可写
            [System.IO.File]::AppendAllText($path, '')
            return $path
        } catch {
            continue
        }
    }
    return ''
}

function Read-AppliedRoutes {
    $path = Get-AppliedRoutesPath
    if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) { return @() }

    try {
        $raw = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
        if ([string]::IsNullOrWhiteSpace($raw)) { return @() }

        # 必须用 -InputObject：PS 5.1 里 'json' | ConvertFrom-Json 会把整个顶层数组
        # 当成单个管道对象输出，外面再套 @() 就成了「1 个元素 = 整个数组」，取数会全错。
        $parsed = ConvertFrom-Json -InputObject $raw -ErrorAction Stop

        $result = @()
        foreach ($item in @($parsed)) {
            if ($null -eq $item) { continue }
            $result += [pscustomobject][ordered]@{
                InterfaceIndex    = [int]$item.interfaceIndex
                DestinationPrefix = [string]$item.destinationPrefix
                NextHop           = [string]$item.nextHop
            }
        }
        return @($result)
    } catch {
        # 记录文件坏了就当没记录过，宁可漏删也不能让它影响切换
        return @()
    }
}

function Write-AppliedRoutes {
    param([object[]]$Routes = @())

    $path = Get-AppliedRoutesPath
    if ([string]::IsNullOrWhiteSpace($path)) { return }

    try {
        $payload = @()
        foreach ($route in (ConvertTo-Array $Routes)) {
            $payload += [pscustomobject][ordered]@{
                interfaceIndex    = [int]$route.InterfaceIndex
                destinationPrefix = [string]$route.DestinationPrefix
                nextHop           = [string]$route.NextHop
            }
        }
        if ($payload.Count -eq 0) {
            [System.IO.File]::WriteAllText($path, '[]', (New-Object System.Text.UTF8Encoding($false)))
        } else {
            # 用 -InputObject 而不是管道，避免单条记录时被拆成裸对象、下次读回来不是数组
            $json = ConvertTo-Json -InputObject $payload -Depth 6
            [System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
        }
    } catch {
        # 记不下来不影响切换，最坏结果是下次漏清一次
    }
}

function Register-AppliedRoute {
    param(
        [int]$InterfaceIndex,
        [string]$DestinationPrefix,
        [string]$NextHop
    )

    $recorded = @(Read-AppliedRoutes)
    foreach ($route in $recorded) {
        if ($route.InterfaceIndex -eq $InterfaceIndex -and
            $route.DestinationPrefix -eq $DestinationPrefix -and
            $route.NextHop -eq $NextHop) { return }
    }

    $recorded += [pscustomobject][ordered]@{
        InterfaceIndex    = $InterfaceIndex
        DestinationPrefix = $DestinationPrefix
        NextHop           = $NextHop
    }
    Write-AppliedRoutes -Routes $recorded
}

# 清掉 NetSwitch 之前为指定网卡加过的附加路由；$InterfaceIndex 为 0 时清全部
function Clear-AppliedRoutes {
    param([int]$InterfaceIndex = 0)

    $recorded = @(Read-AppliedRoutes)
    if ($recorded.Count -eq 0) { return }

    $remaining = @()
    foreach ($route in $recorded) {
        if ($InterfaceIndex -ne 0 -and $route.InterfaceIndex -ne $InterfaceIndex) {
            $remaining += $route
            continue
        }
        try {
            Remove-NetRoute -InterfaceIndex $route.InterfaceIndex -DestinationPrefix $route.DestinationPrefix `
                            -NextHop $route.NextHop -Confirm:$false -ErrorAction Stop
            Write-NSLog "  清理上次添加的附加路由 $($route.DestinationPrefix) -> $($route.NextHop)" 'OK'
        } catch {
            # 删不掉通常是它已经被系统或用户移除了；写普通日志即可，用 WARN 会每次切换都刷一条噪音
            Write-NSLog "  附加路由 $($route.DestinationPrefix) -> $($route.NextHop) 已不存在，跳过清理" 'INFO'
        }
    }

    Write-AppliedRoutes -Routes $remaining
}

# ---------- DHCP 续租等待 ----------
# ipconfig /renew 返回时地址往往还没真正落位，原来只 Start-Sleep 300 毫秒就往下走，
# 后面的 DNS 设置和状态刷新都建立在「已经有地址」的假设上。
function Wait-DhcpAddress {
    param(
        [int]$InterfaceIndex,
        [int]$TimeoutSeconds = 6
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        try {
            # 排除 127.* 与 169.254.*（APIPA：DHCP 没拿到地址时系统临时分配的占位地址）
            $addresses = @(Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction Stop |
                           Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' })
            if ($addresses.Count -gt 0) { return [string]$addresses[0].IPAddress }
        } catch {
            # 网卡正在重置的瞬间读取会报错，下一轮再试
        }
    }
    return ''
}

# ---------- 静态 IP 冲突探测 ----------
# 尽力而为的探测，结论仅供参考：先删掉该地址的 ARP 缓存项，再发一次短超时 ping 逼出 ARP 请求，
# 最后回查 ARP 表看有没有人应答。只有「ping 通」或「确实解析出 MAC」才判为疑似占用。
# 离线设备、静态 ARP 项、虚拟网卡都会造成假阳性，所以它只能用来提示，绝不能用来自动中止切换。
function Test-IpAddressConflict {
    param(
        [string]$IpAddress,
        [int]$TimeoutMs = 800
    )

    if ([string]::IsNullOrWhiteSpace($IpAddress)) { return $false }
    if ($IpAddress -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { return $false }

    try { [void](& arp.exe -d $IpAddress 2>$null) } catch { }

    try {
        $ping  = New-Object System.Net.NetworkInformation.Ping
        $reply = $ping.Send($IpAddress, $TimeoutMs)
        if ($null -ne $reply -and $reply.Status -eq 'Success') { return $true }
    } catch { }

    # ping 不通也别急着下结论：不少主机禁 ICMP，但 ARP 仍会应答
    try {
        $pattern = [regex]::Escape($IpAddress.Trim())
        foreach ($line in @(& arp.exe -a $IpAddress 2>$null)) {
            if ($line -match $pattern -and $line -match '([0-9a-fA-F]{2}[-:]){5}[0-9a-fA-F]{2}') { return $true }
        }
    } catch { }

    return $false
}

# 是否允许弹交互框。命令行 / 桌面快捷方式常以隐藏窗口运行（带 -Silent），
# 这种场景弹 MessageBox 用户看不见也点不到，进程会永久挂死，只能写日志放行。
function Test-CanInteract {
    return (-not $script:IsCliMode)
}

# 把「默认路由跃点数」文本规范成整数，留空或非法一律返回 $null 由调用方按自动处理。
# 合法区间 0~9999：0 也是有效值（WLAN 的 DHCP 默认路由 metric 就是 0，要压过它必须允许填 0）。
function ConvertTo-MetricValue {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $trimmed = $Text.Trim()
    if ($trimmed -notmatch '^\d{1,4}$') { return $null }
    $value = [int]$trimmed
    if ($value -lt 0 -or $value -gt 9999) { return $null }
    return $value
}

function Reset-InterfaceIPv4 {
    param([int]$InterfaceIndex)

    # 先按记录清掉 NetSwitch 上次为这块网卡加的附加路由：本函数只清 IPv4 地址和默认路由，
    # 配置集里删掉的那条路由会一直残留在系统路由表里，只有靠这份记录才能删干净
    Clear-AppliedRoutes -InterfaceIndex $InterfaceIndex

    Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -ne '127.0.0.1' } |
        Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue

    Get-NetRoute -InterfaceIndex $InterfaceIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
}

function Set-AdapterConfig {
    param(
        $AdapterObject,
        $Config
    )

    $index   = $AdapterObject.InterfaceIndex
    $name    = $AdapterObject.Name
    $isDhcp  = ($Config.mode -eq 'dhcp')
    $dnsList = ConvertTo-StringList $Config.dns

    Write-NSLog "网卡「$name」(#$index) -> $(if ($isDhcp) { 'DHCP 自动获取' } else { '静态 IP' })"

    Reset-InterfaceIPv4 -InterfaceIndex $index

    if ($isDhcp) {
        if ($dnsList.Count -eq 0 -or $Config.dhcpDns) {
            Set-DnsClientServerAddress -InterfaceIndex $index -ResetServerAddresses -ErrorAction SilentlyContinue
        }
        Set-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -Dhcp Enabled -ErrorAction Stop
        Start-Sleep -Milliseconds 300
        # ipconfig /renew 需要网卡名称。这里后台发起续租、随后轮询地址是否落位，
        # 而不是给它加 -Wait：续租失败时 ipconfig 最长要几十秒才返回，会把界面整个卡死。
        Start-Process -FilePath 'ipconfig' -ArgumentList "/renew", "`"$name`"" -WindowStyle Hidden -ErrorAction SilentlyContinue
        $leasedIp = Wait-DhcpAddress -InterfaceIndex $index -TimeoutSeconds 6
        if ($leasedIp) {
            Write-NSLog "  已获取到 DHCP 地址 $leasedIp" 'OK'
        } else {
            Write-NSLog '  等待 DHCP 地址超时（6 秒）：网线未插好或网内没有 DHCP 服务器时属正常，配置已下发，可稍后手动执行 ipconfig /renew。' 'WARN'
        }
        if ($dnsList.Count -gt 0 -and -not $Config.dhcpDns) {
            Set-DnsClientServerAddress -InterfaceIndex $index -ServerAddresses $dnsList -ErrorAction SilentlyContinue
        }
        $dnsNote = if ($dnsList.Count -gt 0 -and -not $Config.dhcpDns) { 'DNS 固定为 ' + ($dnsList -join ', ') } else { 'DNS 自动' }
        Write-NSLog "  已切换为自动获取（$dnsNote）" 'OK'
    } else {
        $ipAddress = ([string]$Config.ip).Trim()
        if ([string]::IsNullOrWhiteSpace($ipAddress)) {
            Write-NSLog "  静态模式但未填写 IP 地址，已跳过该网卡。" 'WARN'
            return
        }

        # 写入前先探一下这个地址是否已被局域网内其他设备占用。
        # 探测结果不可靠（离线设备、静态 ARP 项都会造成假阳性），所以只 WARN + 让用户选，
        # 绝不自动中止 —— 误拦会让用户切不了网，比误报严重得多。
        if (Test-IpAddressConflict -IpAddress $ipAddress) {
            Write-NSLog "  探测到 IP $ipAddress 可能已被局域网内其他设备占用。" 'WARN'
            $proceed = $true
            if (Test-CanInteract) {
                Add-Type -AssemblyName System.Windows.Forms
                $answer = [System.Windows.Forms.MessageBox]::Show(
                    "探测到 IP 地址 $ipAddress 可能已被局域网内其他设备占用。`r`n`r`n继续写入可能造成 IP 冲突，导致两台机器都上不了网。`r`n`r`n如果确认该地址当前无人使用（对方已关机也可能触发本提示），可以选择「仍然继续」。",
                    'NetSwitch - IP 可能冲突',
                    [System.Windows.Forms.MessageBoxButtons]::YesNo,
                    [System.Windows.Forms.MessageBoxIcon]::Warning,
                    [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
                $proceed = ($answer -eq [System.Windows.Forms.DialogResult]::Yes)
            }
            if (-not $proceed) {
                Write-NSLog "  已按用户选择跳过网卡「$name」的静态 IP 写入。" 'WARN'
                return
            }
            Write-NSLog '  用户选择仍然继续写入。' 'INFO'
        }

        $prefix = ConvertTo-PrefixLength $Config.mask
        Set-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -Dhcp Disabled -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 150

        $gateways = ConvertTo-GatewayEntry (($Config.gateways -join ';'))
        # 网卡上指定了「默认跃点」时，统一改走「先建地址、再手工建默认路由」这条路，
        # 因为 New-NetIPAddress -DefaultGateway 会强行用系统默认 metric，覆盖不掉。
        $configMetric = ConvertTo-MetricValue ([string]$Config.defaultMetric)
        if ($gateways.Count -eq 1 -and $null -eq $gateways[0].Metric -and $null -eq $configMetric) {
            New-NetIPAddress -InterfaceIndex $index -IPAddress $ipAddress -PrefixLength $prefix `
                             -DefaultGateway $gateways[0].Address -ErrorAction Stop | Out-Null
        } else {
            New-NetIPAddress -InterfaceIndex $index -IPAddress $ipAddress -PrefixLength $prefix -ErrorAction Stop | Out-Null
            $step = 0
            foreach ($gateway in $gateways) {
                # 优先级：网关串里写的 1.1.1.1:10  > 网卡的「默认跃点」  > 自动 10+序号
                # 多个网关时即使指定了跃点也要逐个 +1，避免两条默认路由 metric 完全相同
                $metric = if ($null -ne $gateway.Metric) { $gateway.Metric }
                          elseif ($null -ne $configMetric) { $configMetric + $step }
                          else { 10 + $step }
                New-NetRoute -InterfaceIndex $index -DestinationPrefix '0.0.0.0/0' `
                             -NextHop $gateway.Address -RouteMetric $metric -ErrorAction SilentlyContinue | Out-Null
                $step++
            }
        }

        if ($dnsList.Count -gt 0) {
            Set-DnsClientServerAddress -InterfaceIndex $index -ServerAddresses $dnsList -ErrorAction SilentlyContinue
        } else {
            Set-DnsClientServerAddress -InterfaceIndex $index -ResetServerAddresses -ErrorAction SilentlyContinue
        }

        $summary = "  IP=$ipAddress/$prefix"
        if ($gateways.Count -gt 0) { $summary += "  网关=$(($gateways | ForEach-Object { $_.Address }) -join ',')" }
        if ($null -ne $configMetric) { $summary += "  默认跃点=$configMetric" }
        if ($dnsList.Count -gt 0)  { $summary += "  DNS=$($dnsList -join ',')" }
        Write-NSLog $summary 'OK'
    }

    # 附加静态路由（可选）
    foreach ($routeText in (ConvertTo-StringList $Config.routes)) {
        foreach ($route in (ConvertTo-RouteEntry $routeText)) {
            Remove-NetRoute -DestinationPrefix $route.Destination -NextHop $route.NextHop -Confirm:$false -ErrorAction SilentlyContinue
            try {
                New-NetRoute -InterfaceIndex $index -DestinationPrefix $route.Destination -NextHop $route.NextHop -ErrorAction Stop | Out-Null
                # 记下这条是 NetSwitch 加的，下次切换前先按记录删，配置集里删掉的才不会残留
                Register-AppliedRoute -InterfaceIndex $index -DestinationPrefix $route.Destination -NextHop $route.NextHop
                Write-NSLog "  附加路由 $($route.Destination) -> $($route.NextHop)" 'OK'
            } catch {
                Write-NSLog "  附加路由添加失败 $($route.Destination) -> $($route.NextHop)：$($_.Exception.Message)" 'WARN'
            }
        }
    }
}

function Invoke-Profile {
    param(
        [string]$ProfileName,
        [switch]$DryRun
    )

    $target = Get-ProfileByName $ProfileName
    if ($null -eq $target) {
        $names = Get-ProfileNameList
        $hint = if ($names.Count -gt 0) { "现有配置集：" + ($names -join '、') } else { '当前没有任何配置集。' }
        Write-NSLog "找不到配置集「$ProfileName」。（$hint）" 'ERROR'
        return $false
    }

    Write-NSLog "开始应用配置集「$($target.name)」" 'INFO'

    if ($DryRun) {
        foreach ($config in (ConvertTo-Array $target.adapters)) {
            Write-NSLog "  [预览] 网卡匹配=$($config.adapter)  模式=$($config.mode)  IP=$($config.ip)"
        }
        return $true
    }

    if (-not (Test-IsAdmin)) {
        Write-NSLog '当前没有管理员权限，无法修改网络配置。请以管理员身份运行。' 'ERROR'
        return $false
    }

    # 切换会先清空网卡现有地址，所以动手之前先把当前网络状态备份下来
    Save-NetworkBackup | Out-Null

    $failed = 0
    $processed = 0

    foreach ($config in (ConvertTo-Array $target.adapters)) {
        if (-not $config.enabled) {
            Write-NSLog "网卡匹配「$($config.adapter)」已禁用，跳过。" 'INFO'
            continue
        }
        $matched = Find-MatchedAdapters $config.adapter
        if ($matched.Count -eq 0) { continue }

        foreach ($adapter in $matched) {
            $processed++
            try {
                Set-AdapterConfig -AdapterObject $adapter -Config $config
            } catch {
                $failed++
                Write-NSLog "网卡「$($adapter.Name)」配置失败：$($_.Exception.Message)" 'ERROR'
            }
        }
    }

    Start-Sleep -Milliseconds 500

    if ($failed -eq 0) {
        Write-NSLog "配置集「$($target.name)」已生效（共处理 $processed 块网卡）" 'OK'
        return $true
    } else {
        Write-NSLog "配置集「$($target.name)」完成，但有 $failed 项失败（共 $processed 块网卡）。" 'WARN'
        return $false
    }
}

# 为指定配置集生成（或覆盖）桌面快捷方式：双击即以管理员身份直接切换
function New-ProfileShortcut {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }

    $shell    = New-Object -ComObject WScript.Shell
    $desktop  = $shell.SpecialFolders('Desktop')
    $safeName = ($Name -replace '[\\/:*?"<>|]', '_')
    $linkPath = Join-Path $desktop "$($script:AppName)-$safeName.lnk"

    $shortcut = $shell.CreateShortcut($linkPath)
    $shortcut.TargetPath       = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
    # 必须带 -Silent：窗口是隐藏的，用户看不到也按不到「按任意键结束」，否则进程会一直挂着
    $shortcut.Arguments        = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`" -Apply `"$Name`" -Silent"
    $shortcut.WorkingDirectory = $PSScriptRoot
    $shortcut.Description      = "一键切换到 [$Name]"
    $shortcut.IconLocation     = 'netshell.dll,18'
    $shortcut.Save()

    # 给 .lnk 打上「以管理员身份运行」标志位，双击直接提权，省掉二次确认
    $bytes = [System.IO.File]::ReadAllBytes($linkPath)
    $bytes[0x15] = ($bytes[0x15] -bor 0x20)
    [System.IO.File]::WriteAllBytes($linkPath, $bytes)

    return $linkPath
}

# 配置集改名后同步桌面快捷方式；否则旧快捷方式里的名称失配，双击只会报「找不到配置集」
function Sync-ProfileShortcutOnRename {
    param([string]$OldName, [string]$NewName)

    if ([string]::IsNullOrWhiteSpace($OldName)) { return }
    if ($OldName -eq $NewName) { return }

    try {
        $shell   = New-Object -ComObject WScript.Shell
        $desktop = $shell.SpecialFolders('Desktop')
        $oldSafe = ($OldName -replace '[\\/:*?"<>|]', '_')
        $oldLink = Join-Path $desktop "$($script:AppName)-$oldSafe.lnk"
        if (-not (Test-Path -LiteralPath $oldLink)) { return }

        # 先按新名称生成，确认成功后再删旧链；避免中途失败反而把可用的快捷方式弄丢
        $newLink = New-ProfileShortcut -Name $NewName
        if (Test-Path -LiteralPath $oldLink) { Remove-Item -LiteralPath $oldLink -Force }
        Write-NSLog "配置集已改名，桌面快捷方式同步更新为「$(Split-Path $newLink -Leaf)」" 'OK'
    } catch {
        Write-NSLog "同步桌面快捷方式失败：$($_.Exception.Message)" 'WARN'
    }
}

# 命令行模式下的可见反馈：桌面快捷方式用 -WindowStyle Hidden 启动，
# 控制台文字用户根本看不到，出错时必须弹窗，否则用户只会觉得「双击没反应」
function Show-CliMessageBox {
    param(
        [string]$Message,
        [string]$Title = 'NetSwitch',
        [string]$Icon  = 'Information'
    )

    try {
        [void][System.Reflection.Assembly]::LoadWithPartialName('System.Windows.Forms')
        [System.Windows.Forms.MessageBox]::Show($Message, $Title, 'OK', $Icon) | Out-Null
    } catch {
        # 弹窗失败不影响主流程，日志里已经记录了原因
    }
}

# 读取当前网络状态（主界面右上「当前网络状态」与命令行 -ShowStatus 共用）。
#
# 这里刻意绕开 Get-NetIPAddress / Get-NetIPInterface / Get-DnsClientServerAddress /
# Get-NetRoute 这四个 cmdlet：它们各自要加载一个 CDXML 模块，冷启动时四个模块合计约 7 秒，
# 而且是「每块网卡各调一遍」，网卡越多耗时越长。
# 改成对 WMI 各查一次全量数据、再在内存里按 InterfaceIndex 配对：
# 冷启动约 1 秒，且耗时不再随网卡数量线性增长。
# 万一 WMI 不可用（极少数精简/受限系统），自动退回原来的 cmdlet 实现，不影响可用性。
function Get-StatusText {
    try {
        return (Get-StatusTextViaCim)
    } catch {
        Write-NSLog "WMI 读取网络状态失败，已改用网卡 PowerShell 模块：$($_.Exception.Message)" 'WARN'
        return (Get-StatusTextViaCmdlet)
    }
}

function Get-StatusTextViaCim {
    $builder = New-Object System.Text.StringBuilder

    $cfgs = @(Get-CimInstance -ClassName Win32_NetworkAdapterConfiguration -Filter 'IPEnabled = True' -ErrorAction Stop |
              Sort-Object { [int]$_.Index })
    if ($cfgs.Count -eq 0) {
        [void]$builder.AppendLine('没有找到已启用且已连接网络的网卡。')
        return $builder.ToString()
    }

    $nics = @(Get-CimInstance -ClassName Win32_NetworkAdapter -ErrorAction Stop)
    $nicMap = @{}
    foreach ($n in $nics) { $nicMap[[string]$n.DeviceID] = $n }

    foreach ($c in $cfgs) {
        $nic = $nicMap[[string]$c.Index]

        # 与主界面的网卡列表共用同一套虚拟网卡过滤规则，
        # 否则会出现「列表里没有、状态里却有」的不一致。
        $probe = [pscustomobject]@{
            Name                 = $(if ($null -ne $nic) { [string]$nic.NetConnectionID } else { [string]$c.Description })
            InterfaceDescription = [string]$c.Description
        }
        if (Test-VirtualAdapter $probe) { continue }

        $name = [string]$probe.Name
        if ([string]::IsNullOrWhiteSpace($name)) { $name = [string]$c.Description }

        $statusText = ConvertFrom-ConnectionStatus $nic
        $speedText  = ConvertFrom-AdapterSpeed $nic

        # WMI 的 MAC 用冒号分隔，这里统一成连字符，与配置集里存的写法一致
        $mac = ([string]$c.MACAddress) -replace ':', '-'

        [void]$builder.AppendLine("■ $name   [$statusText]  $speedText")
        [void]$builder.AppendLine("    MAC        : $mac")
        [void]$builder.AppendLine("    获取方式   : $(if ([bool]$c.DHCPEnabled) { 'DHCP' } else { '静态' })")

        # IPAddress 与 IPSubnet 是两个一一对应的数组，里面混着 IPv6，
        # 因此按下标配对，只保留 IPv4 那几组
        $rawIps   = @($c.IPAddress)
        $rawMasks = @($c.IPSubnet)
        $ipv4     = @()
        for ($i = 0; $i -lt $rawIps.Count; $i++) {
            if ([string]$rawIps[$i] -notmatch '^\d+\.\d+\.\d+\.\d+$') { continue }
            $mask = ''
            if ($i -lt $rawMasks.Count) { $mask = [string]$rawMasks[$i] }
            if ([string]::IsNullOrWhiteSpace($mask)) { $mask = '255.255.255.0' }
            $ipv4 += [pscustomobject]@{ IP = [string]$rawIps[$i]; Mask = $mask }
        }

        if ($ipv4.Count -eq 0) {
            [void]$builder.AppendLine('    IPv4       : （无）')
        } else {
            foreach ($item in $ipv4) {
                $prefix = ConvertFrom-MaskToPrefix $item.Mask
                [void]$builder.AppendLine("    IPv4       : $($item.IP)/$prefix  ($($item.Mask))")
            }
        }

        $gateways = @($c.DefaultIPGateway   | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' })
        $dnsList  = @($c.DNSServerSearchOrder | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' })

        $gwText  = if ($gateways.Count -gt 0) { $gateways -join ', ' } else { '（无）' }
        $dnsText = if ($dnsList.Count  -gt 0) { $dnsList  -join ', ' } else { '（自动/无）' }

        [void]$builder.AppendLine("    网关       : $gwText")
        [void]$builder.AppendLine("    DNS        : $dnsText")
        [void]$builder.AppendLine('')
    }

    return $builder.ToString()
}

# 原来的实现：逐块网卡调四个 cmdlet。保留作为 WMI 不可用时的退路。
function Get-StatusTextViaCmdlet {
    $builder = New-Object System.Text.StringBuilder
    $adapters = Get-AdapterList

    foreach ($adapter in $adapters) {
        $index = $adapter.InterfaceIndex
        $addresses = @(Get-NetIPAddress -InterfaceIndex $index -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                       Where-Object { $_.IPAddress -ne '127.0.0.1' })
        $mode = '未知'
        try {
            $mode = if ((Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -ErrorAction Stop).Dhcp -eq 'Enabled') { 'DHCP' } else { '静态' }
        } catch {
            $mode = '未知'
        }

        $dns = @(Get-DnsClientServerAddress -InterfaceIndex $index -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                 ForEach-Object { $_.ServerAddresses } | Select-Object -Unique)
        $gateways = @(Get-NetRoute -InterfaceIndex $index -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                      ForEach-Object { $_.NextHop })

        [void]$builder.AppendLine("■ $($adapter.Name)   [$($adapter.Status)]  $($adapter.LinkSpeed)")
        [void]$builder.AppendLine("    MAC        : $($adapter.MacAddress)")
        [void]$builder.AppendLine("    获取方式   : $mode")
        if ($addresses.Count -eq 0) {
            [void]$builder.AppendLine('    IPv4       : （无）')
        } else {
            foreach ($item in $addresses) {
                $maskText = ConvertFrom-PrefixToMask $item.PrefixLength
                [void]$builder.AppendLine("    IPv4       : $($item.IPAddress)/$($item.PrefixLength)  ($maskText)")
            }
        }
        [void]$builder.AppendLine("    网关       : $(if ($gateways.Count -gt 0) { $gateways -join ', ' } else { '（无）' })")
        [void]$builder.AppendLine("    DNS        : $(if ($dns.Count -gt 0) { $dns -join ', ' } else { '（自动/无）' })")
        [void]$builder.AppendLine('')
    }

    return $builder.ToString()
}

# WMI 的 NetConnectionStatus 是数字，翻成中文显示
function ConvertFrom-ConnectionStatus {
    param($Nic)
    if ($null -eq $Nic) { return '未知' }
    $code = 0
    try { $code = [int]$Nic.NetConnectionStatus } catch { return '未知' }
    switch ($code) {
        0      { return '已断开' }
        1      { return '正在连接' }
        2      { return '已连接' }
        3      { return '正在断开' }
        4      { return '硬件不存在' }
        5      { return '已禁用' }
        6      { return '硬件故障' }
        7      { return '网线已拔出' }
        8      { return '正在验证身份' }
        9      { return '身份验证成功' }
        10     { return '身份验证失败' }
        11     { return '地址无效' }
        12     { return '需要凭据' }
        default { return "状态 $code" }
    }
}

# WMI 的 Speed 单位是 bps（字符串），翻成 Gbps / Mbps
function ConvertFrom-AdapterSpeed {
    param($Nic)
    if ($null -eq $Nic) { return '' }
    $bps = 0
    try { $bps = [double]$Nic.Speed } catch { return '' }
    if ($bps -le 0) { return '' }
    # 与 Get-NetAdapter 的 LinkSpeed 保持同样的写法：1 Gbps / 866.7 Mbps
    if ($bps -ge 1000000000) {
        return ([math]::Round(($bps / 1000000000), 1).ToString() + ' Gbps')
    }
    return ([math]::Round(($bps / 1000000), 1).ToString() + ' Mbps')
}

# 掩码 -> 前缀长度（WMI 只给掩码，界面上要显示成 192.168.1.10/24 这种形式）
function ConvertFrom-MaskToPrefix {
    param([string]$Mask)
    if ([string]::IsNullOrWhiteSpace($Mask)) { return 24 }
    if ($Mask -notmatch '^\d+\.\d+\.\d+\.\d+$') { return 24 }
    $prefix = 0
    foreach ($part in $Mask.Split('.')) {
        $value = 0
        [void][int]::TryParse($part, [ref]$value)
        while ($value -gt 0) {
            $prefix += ($value -band 1)
            $value = $value -shr 1
        }
    }
    if ($prefix -lt 0 -or $prefix -gt 32) { return 24 }
    return $prefix
}

function ConvertFrom-PrefixToMask {
    param([int]$Prefix)
    if ($Prefix -lt 0 -or $Prefix -gt 32) { return '' }
    $bits = ('1' * $Prefix).PadRight(32, '0')
    $bytes = @()
    for ($i = 0; $i -lt 4; $i++) {
        $bytes += [Convert]::ToInt32($bits.Substring($i * 8, 8), 2)
    }
    return ($bytes -join '.')
}

# ============================== 命令行模式 ==============================

function Invoke-Cli {
    Assert-Environment

    if ($PSBoundParameters.ContainsKey('ConfigPath') -and $ConfigPath) {
        $script:ConfigFile = $ConfigPath
    } else {
        $script:ConfigFile = Get-DefaultConfigPath
    }
    Import-ConfigFile -Path $script:ConfigFile | Out-Null

    if ($ListAdapters) {
        Write-Host "`n本机网卡列表：" -ForegroundColor Cyan
        Get-AdapterList | Format-Table -AutoSize `
            @{ Label = '索引'; Expression = { $_.InterfaceIndex } },
            @{ Label = '名称'; Expression = { $_.Name } },
            @{ Label = '状态'; Expression = { $_.Status } },
            @{ Label = 'MAC'; Expression = { $_.MacAddress } }
        return
    }

    if ($ListProfiles) {
        Write-Host "`n已配置的配置集：" -ForegroundColor Cyan
        foreach ($item in (ConvertTo-Array $script:ConfigData.profiles)) {
            Write-Host "  - $($item.name)" -NoNewline -ForegroundColor White
            if ($item.remark) { Write-Host "    $($item.remark)" -ForegroundColor DarkGray } else { Write-Host '' }
            foreach ($ad in (ConvertTo-Array $item.adapters)) {
                $modeText = if ($ad.mode -eq 'dhcp') { 'DHCP' } else { "$($ad.ip) mask:$($ad.mask) gw:$($ad.gateways -join ',')" }
                $line = "      [$(Get-AdapterDisplayText ([string]$ad.adapter))] $modeText"
                Write-Host $line -ForegroundColor Gray
            }
        }
        return
    }

    if ($ShowStatus) {
        Write-Host "`n当前网络状态：`n" -ForegroundColor Cyan
        Write-Host (Get-StatusText)
        return
    }

    if ($ExportFile) {
        $target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExportFile)
        Save-ConfigFile -Path $target | Out-Null
        Write-Host "配置已导出到：$target" -ForegroundColor Green
        return
    }

    if ($ImportFile) {
        $target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ImportFile)
        if (-not (Test-Path $target)) { Write-Host "文件不存在：$target" -ForegroundColor Red; return }
        Copy-Item -Path $target -Destination $script:ConfigFile -Force
        Import-ConfigFile -Path $script:ConfigFile | Out-Null
        Write-Host "已导入到配置文件：$($script:ConfigFile)" -ForegroundColor Green
        return
    }

    if ($Apply) {
        if (-not (Test-IsAdmin)) {
            if (-not $Elevated) {
                Write-Host '需要管理员权限，正在请求 UAC 提权…' -ForegroundColor Yellow
                if (Start-Elevated) { return }
            }
            Write-Host '未获得管理员权限，操作取消。' -ForegroundColor Red
            Show-CliMessageBox -Message '未能获得管理员权限，网络配置没有做任何改动。' -Title 'NetSwitch - 操作取消' -Icon 'Error'
            exit 1
        }

        # 桌面快捷方式把名称写死在参数里，改了名就会失配；先解析，失败时把可选名称列出来
        $actualName = Resolve-ProfileName $Apply
        if ($null -eq $actualName) {
            $names = Get-ProfileNameList
            $body  = if ($names.Count -gt 0) { $names -join "`n  " } else { '（当前没有任何配置集）' }
            $msg   = "找不到配置集：「$Apply」`n`n当前可用的配置集：`n  $body`n`n如果刚给配置集改过名字，回主界面点「快捷方式」重新生成一个即可。"
            Write-NSLog "找不到配置集「$Apply」" 'ERROR'
            Write-Host $msg -ForegroundColor Red
            Show-CliMessageBox -Message $msg -Title 'NetSwitch - 找不到配置集' -Icon 'Warning'
            exit 1
        }

        $result = Invoke-Profile -ProfileName $actualName
        if (-not $result) {
            $msg = "配置集「$actualName」没有切换成功，网络可能未按预期生效。`n`n日志文件：$($script:LogFile)"
            Write-Host $msg -ForegroundColor Red
            Show-CliMessageBox -Message $msg -Title 'NetSwitch - 切换未完成' -Icon 'Warning'
            exit 1
        }

        Write-Host "`n切换后的网络状态：`n" -ForegroundColor Cyan
        Write-Host (Get-StatusText)
        if (-not $Silent) {
            Write-Host '按任意键结束…' -ForegroundColor DarkGray
            try { [void]$Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { }
        }
        return
    }

    # 无明确指令时给出用法提示
    Write-Host -ForegroundColor Cyan @"

NetSwitch $script:Version - 用法提示
  -Apply "名称"     应用指定配置集（可配合 -Silent 用于快捷方式）
  -ListProfiles    列出全部配置集
  -ListAdapters    列出本机网卡名称（用于填写配置）
  -ShowStatus      查看当前网络状态
  -ExportFile x    导出配置
  -ImportFile x    导入配置
不带参数运行即为图形界面模式。

"@
}

# ============================== 图形界面 ==============================

# ====================== 高 DPI 与控制台窗口 ======================
# 背景：脚本没有 manifest，默认不声明 DPI 感知。Windows 在 125% / 150% 缩放下会把整个
#       窗口当成一张位图放大后显示，表现就是「文字发虚、描边发毛」，放大倍数越大越明显。
#
# 修法分两步，必须成对出现，否则界面会整体变大或变小：
#   1) 在「任何窗口被创建之前」声明本进程为高 DPI 感知 —— 系统不再拉伸，改由程序按真实 DPI 渲染；
#   2) 再把所有控件的坐标、尺寸、行高按同一个倍数放大，把视觉尺寸补回来。
#
# 【如何关闭】把下面这一行改成 $false，即可完全回到改造前的行为（窗口交给系统拉伸）：
$script:EnableDpiAwareness = $true

# 所有坐标与字体的统一放大倍数。100% 缩放下恒为 1，等价于不做任何缩放。
$script:UiScale = 1.0

# 声明高 DPI 感知。必须在创建任何窗口之前调用，且每个进程只能成功设置一次，
# 所以失败（比如在已感知的宿主里运行）一律静默降级，绝不让异常冒出去。
function Enable-ProcessDpiAwareness {
    if (-not $script:EnableDpiAwareness) { return $false }
    try {
        Add-Type -Namespace 'NSUi' -Name 'Dpi' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
public static extern bool SetProcessDpiAwarenessContext(int value);
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
public static extern bool SetProcessDPIAware();
'@
        # -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2（Win10 1703+ / Win11）
        try { if ([NSUi.Dpi]::SetProcessDpiAwarenessContext(-4)) { return $true } } catch { }
        try { return [NSUi.Dpi]::SetProcessDPIAware() } catch { }
    } catch { }
    return $false
}

# 让界面在启动时先把自己「放大」到与系统拉伸后一致的视觉尺寸。
# 只放大坐标与尺寸，绝不碰字体 —— 字号由 New-FormFont / New-MonoFont 统一乘 UiScale，
# 两边各管一头，才不会重复放大。
function Invoke-UiScaleLayout {
    param($Root, [double]$Factor)

    if ($null -eq $Root -or $Factor -le 1.0) { return }

    foreach ($c in $Root.Controls) {
        $c.Location = New-Object System.Drawing.Point(
            [int][math]::Round($c.Location.X * $Factor),
            [int][math]::Round($c.Location.Y * $Factor))
        $c.Size = New-Object System.Drawing.Size(
            [int][math]::Round($c.Size.Width * $Factor),
            [int][math]::Round($c.Size.Height * $Factor))

        if ($c -is [System.Windows.Forms.ListBox]) {
            $c.ItemHeight = [int][math]::Round($c.ItemHeight * $Factor)
        }
        if ($c -is [System.Windows.Forms.DataGridView]) {
            $c.RowTemplate.Height = [int][math]::Round($c.RowTemplate.Height * $Factor)
            $c.ColumnHeadersHeight = [int][math]::Round($c.ColumnHeadersHeight * $Factor)
        }

        # 字体不在这里动：所有字号都由 New-FormFont / New-MonoFont 统一乘 UiScale，
        # 布局只负责坐标与尺寸，避免两边各放大一次。
        if ($c.HasChildren) { Invoke-UiScaleLayout -Root $c -Factor $Factor }
    }

    # 窗体放到最后放大：先放大会触发锚点布局，子控件随后再手工放大就会被放大两次。
    if ($Root -is [System.Windows.Forms.Form]) {
        $Root.Size = New-Object System.Drawing.Size(
            [int][math]::Round($Root.Size.Width * $Factor),
            [int][math]::Round($Root.Size.Height * $Factor))
        if ($Root.MinimumSize.Width -gt 0 -and $Root.MinimumSize.Height -gt 0) {
            $Root.MinimumSize = New-Object System.Drawing.Size(
                [int][math]::Round($Root.MinimumSize.Width * $Factor),
                [int][math]::Round($Root.MinimumSize.Height * $Factor))
        }
    }
}

# 双击 .bat 启动时，cmd 那个黑窗口会一直挂在图形界面后面等进程结束。
# 界面显示出来之后就把它收起来 —— 启动阶段的报错信息仍然打印过，不影响排查。
#
# 只在这种情况下收：进程是「由资源管理器或 cmd 直接拉起来的」，也就是用户双击启动的场景。
# 如果用户是在自己开着的终端里运行本脚本，那个终端是他的，不能动。
function Hide-ConsoleWindow {
    if (-not (Test-LaunchedFromShell)) { return }
    try {
        Add-Type -Namespace 'NSUi' -Name 'Window' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern IntPtr GetConsoleWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
'@
        $h = [NSUi.Window]::GetConsoleWindow()
        if ($h -ne [System.IntPtr]::Zero) { [void][NSUi.Window]::ShowWindow($h, 0) }
    } catch { }
}

# 开机后立即执行：先声明感知，再算出放大倍数。
# 关键自检：只有「确实声明成功」时才放大。若声明失败（系统仍会拉伸窗口），
# 这里绝不能再放大，否则就会被拉伸一次 + 放大一次，界面凭空大一倍。
# 判断本进程是不是「用户双击启动」的：看父进程是不是资源管理器或 cmd。
# 结果缓存，避免每次都查。
$script:LaunchedFromShell = $null

function Test-LaunchedFromShell {
    if ($null -ne $script:LaunchedFromShell) { return [bool]$script:LaunchedFromShell }

    $result = $false
    try {
        $me = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $PID" -ErrorAction Stop
        $parentId = 0
        try { $parentId = [int]$me.ParentProcessId } catch { $parentId = 0 }
        if ($parentId -gt 0) {
            $parent = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $parentId" -ErrorAction Stop
            $pn = ([string]$parent.Name).ToLowerInvariant()
            if ($pn -eq 'explorer.exe' -or $pn -eq 'cmd.exe') { $result = $true }
        }
    } catch { }

    $script:LaunchedFromShell = $result
    return $result
}

function Initialize-UiScale {
    $script:UiScale = 1.0
    if (-not (Enable-ProcessDpiAwareness)) { return }
    try {
        Add-Type -AssemblyName System.Drawing
        $g = [System.Drawing.Graphics]::FromHwnd([System.IntPtr]::Zero)
        try {
            $scale = $g.DpiX / 96.0
            if ($scale -gt 1.0 -and $scale -lt 4.0) { $script:UiScale = $scale }
        } finally { $g.Dispose() }
    } catch { }
}

# 注意：Initialize-UiScale 不在顶层调用。声明感知要编译一小段 C#，有约 1 秒开销，
# 而命令行模式根本用不到界面，不该为它付这个代价 —— 因此放到图形界面入口处再调用。

function New-FormFont {
    param([single]$Size = 9, [string]$Style = 'Regular')
    # 字号用「磅」为单位，磅是物理单位，高 DPI 感知下系统会自动换成更多像素，
    # 视觉大小保持不变。这里乘 UiScale 是为了让字与放大后的控件保持原有比例。
    $pt = $Size * $script:UiScale
    try {
        return (New-Object System.Drawing.Font('Microsoft YaHei UI', $pt, [System.Drawing.FontStyle]$Style))
    } catch {
        return (New-Object System.Drawing.Font('Microsoft Sans Serif', $pt, [System.Drawing.FontStyle]$Style))
    }
}

function New-MonoFont {
    param([single]$Size = 9)
    return (New-Object System.Drawing.Font('Consolas', ($Size * $script:UiScale)))
}

# ============================== Win11 风格主题 ==============================
# 目标：不引入任何第三方组件、不新增外部资源文件，纯代码把界面换成 Windows 11 观感。
# 做法：按钮 / 卡片全部用 GDI+ 自绘（GraphicsPath + AntiAlias），再由 Invoke-UiTheme
#       递归统一套用 —— 改动集中在主题层，各处布局坐标一行不动。
#
# 两个必须记住的 WinForms 细节：
#   1) Paint 事件在控件自身文字绘制「之后」触发，自绘的背景会盖掉按钮文字，
#      因此必须在 Paint 里自己 DrawString 重画文字。
#   2) DrawString 必须显式构造 RectangleF，否则 PowerShell 会选中 PointF 重载而报错。

# 配色表必须延迟初始化：脚本顶层执行时 System.Drawing 往往还没加载，
# 直接在上面写 [System.Drawing.Color]::FromArgb 会因找不到类型而静默失败，
# 结果是 $script:UiTheme 为 $null、后面每次取色都拿到空值。
$script:UiTheme = $null

function Initialize-UiTheme {
    if ($null -ne $script:UiTheme) { return }
    Add-Type -AssemblyName System.Drawing

    $script:UiTheme = @{
    Bg         = [System.Drawing.Color]::FromArgb(243, 243, 243)  # 窗口底色
    Card       = [System.Drawing.Color]::White                    # 卡片
    Border     = [System.Drawing.Color]::FromArgb(229, 229, 229)  # 卡片描边
    Text       = [System.Drawing.Color]::FromArgb(26, 26, 26)     # 正文
    TextMuted  = [System.Drawing.Color]::FromArgb(96, 96, 96)     # 次要文字
    Accent     = [System.Drawing.Color]::FromArgb(0, 103, 192)    # Win11 强调色
    AccentHot  = [System.Drawing.Color]::FromArgb(25, 118, 200)
    AccentDown = [System.Drawing.Color]::FromArgb(0, 84, 158)
    AccentSoft = [System.Drawing.Color]::FromArgb(232, 240, 250)  # 选中项浅强调色
    Danger     = [System.Drawing.Color]::FromArgb(196, 43, 28)
    DangerHot  = [System.Drawing.Color]::FromArgb(253, 242, 240)
    Hover      = [System.Drawing.Color]::FromArgb(246, 246, 246)
    Pressed    = [System.Drawing.Color]::FromArgb(237, 237, 237)
    LogBg      = [System.Drawing.Color]::FromArgb(250, 250, 250)
    }
}

# 取主题色。所有用到配色的地方都走这个函数，保证不论从哪个入口（主界面/编辑器/备份）
# 进来，System.Drawing 与色表都已就绪。
function Get-UiColor {
    param([string]$Key)
    Initialize-UiTheme
    if ($script:UiTheme.ContainsKey($Key)) { return $script:UiTheme[$Key] }
    return [System.Drawing.Color]::Black
}

# 要整张色表（自绘时一次取多种颜色，逐次调 Get-UiColor 反而绕）
function Get-UiThemeMap {
    Initialize-UiTheme
    return $script:UiTheme
}

# 圆角矩形路径。半径给的是 96 DPI 下的「圆角半径」，内部按屏幕缩放倍数换算后再按直径 AddArc。
function New-UiRoundPath {
    param(
        [System.Drawing.Rectangle]$Rect,
        [int]$Radius = 4
    )
    # 控件尺寸已经被 Invoke-UiScaleLayout 放大过，圆角也要同步放大，否则高 DPI 下圆角会显得过尖
    $d = [int][math]::Round($Radius * $script:UiScale) * 2
    # 半径不能超过短边的一半，否则弧会互相穿插画出畸形路径
    if ($d -gt $Rect.Height) { $d = $Rect.Height }
    if ($d -gt $Rect.Width) { $d = $Rect.Width }

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($Rect.X, $Rect.Y, $d, $d, 180, 90)
    $path.AddArc(($Rect.Right - $d), $Rect.Y, $d, $d, 270, 90)
    $path.AddArc(($Rect.Right - $d), ($Rect.Bottom - $d), $d, $d, 0, 90)
    $path.AddArc($Rect.X, ($Rect.Bottom - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# 把普通 Button 改造成 Win11 圆角按钮。
# Role：Primary（实心强调色，主操作）/ Secondary（白底描边）/ Danger（红字，破坏性操作）
function Set-UiButtonStyle {
    param(
        $Button,
        [ValidateSet('Primary', 'Secondary', 'Danger')]
        [string]$Role = 'Secondary',
        [int]$Radius = 4
    )

    $t = Get-UiThemeMap
    # 底色调成父容器的颜色：自绘只覆盖圆角以内，四角露出的一两像素要与父容器一致，
    # 否则会看到方形的白角/黑角残留。
    $base = $t.Bg
    if ($null -ne $Button.Parent) { $base = $Button.Parent.BackColor }

    $Button.FlatStyle = 'Flat'
    $Button.FlatAppearance.BorderSize = 0
    $Button.FlatAppearance.MouseOverBackColor = $base
    $Button.FlatAppearance.MouseDownBackColor = $base
    $Button.BackColor = $base
    $Button.UseVisualStyleBackColor = $false
    $Button.Cursor = 'Hand'
    if ($Role -eq 'Primary') { $Button.Font = New-FormFont 9 Bold }

    $Button.Tag = @{
        Role   = $Role
        Radius = $Radius
        Hover  = $false
        Down   = $false
    }

    $Button.Add_MouseEnter({ param($s, $e) $s.Tag['Hover'] = $true;  $s.Invalidate() })
    $Button.Add_MouseLeave({ param($s, $e) $s.Tag['Hover'] = $false; $s.Tag['Down'] = $false; $s.Invalidate() })
    $Button.Add_MouseDown({
        param($s, $e)
        if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) { $s.Tag['Down'] = $true; $s.Invalidate() }
    })
    $Button.Add_MouseUp({ param($s, $e) $s.Tag['Down'] = $false; $s.Invalidate() })

    $Button.Add_Paint({
        param($s, $e)
        $tag = $s.Tag
        if ($null -eq $tag -or -not ($tag -is [hashtable]) -or -not $tag.ContainsKey('Role')) { return }

        $th = Get-UiThemeMap
        $g  = $e.Graphics
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

        $cr = $s.ClientRectangle
        $rect = New-Object System.Drawing.Rectangle -ArgumentList @(($cr.X + 1), ($cr.Y + 1), ($cr.Width - 3), ($cr.Height - 3))
        if ($rect.Width -le 4 -or $rect.Height -le 4) { return }

        $path = New-UiRoundPath -Rect $rect -Radius ([int]$tag['Radius'])

        $fill = $null
        $line = $null
        $fore = $th.Text
        $hover = [bool]$tag['Hover']
        $down  = [bool]$tag['Down']

        switch ([string]$tag['Role']) {
            'Primary' {
                $fill = $th.Accent
                if ($hover) { $fill = $th.AccentHot }
                if ($down)  { $fill = $th.AccentDown }
                $fore = [System.Drawing.Color]::White
            }
            'Danger' {
                $fill = [System.Drawing.Color]::White
                if ($hover) { $fill = $th.DangerHot }
                if ($down)  { $fill = [System.Drawing.Color]::FromArgb(250, 231, 228) }
                $line = $th.Border
                $fore = $th.Danger
            }
            default {
                $fill = [System.Drawing.Color]::White
                if ($hover) { $fill = $th.Hover }
                if ($down)  { $fill = $th.Pressed }
                $line = $th.Border
                $fore = $th.Text
            }
        }
        if (-not $s.Enabled) { $fore = [System.Drawing.Color]::FromArgb(160, 160, 160) }

        $brush = New-Object System.Drawing.SolidBrush $fill
        $g.FillPath($brush, $path)
        $brush.Dispose()
        if ($null -ne $line) {
            $pen = New-Object System.Drawing.Pen -ArgumentList @($line, 1)
            $g.DrawPath($pen, $path)
            $pen.Dispose()
        }

        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = 'Center'
        $sf.LineAlignment = 'Center'
        $rf = New-Object System.Drawing.RectangleF -ArgumentList @([float]$rect.X, [float]$rect.Y, [float]$rect.Width, [float]$rect.Height)
        $tb = New-Object System.Drawing.SolidBrush $fore
        $g.DrawString($s.Text, $s.Font, $tb, $rf, $sf)
        $tb.Dispose()
        $sf.Dispose()
        $path.Dispose()
    })
}

# GroupBox 的方框 + 内嵌标题是典型的旧式外观，换成「圆角白卡片 + 卡内标题」。
# 布局不变：卡片与原 GroupBox 同尺寸同锚点，子控件整体下移 10px、高度减 10px，
# 底边距保持不变，所以底部对齐（Anchor=Bottom）的控件不会错位。
function ConvertTo-UiCard {
    param($GroupBox)

    $t = Get-UiThemeMap
    $parent = $GroupBox.Parent
    if ($null -eq $parent) { return $GroupBox }

    $card = New-Object System.Windows.Forms.Panel
    $card.Location = $GroupBox.Location
    $card.Size     = $GroupBox.Size
    $card.Anchor   = $GroupBox.Anchor
    $card.BackColor = $t.Bg
    $card.Tag = @{ Title = [string]$GroupBox.Text }

    foreach ($child in @($GroupBox.Controls)) {
        $GroupBox.Controls.Remove($child)
        $child.Location = New-Object System.Drawing.Point -ArgumentList @($child.Location.X, ($child.Location.Y + 10))
        $child.Size     = New-Object System.Drawing.Size  -ArgumentList @($child.Size.Width, ($child.Size.Height - 10))
        $card.Controls.Add($child)
    }

    $card.Add_Paint({
        param($s, $e)
        $tag = $s.Tag
        # 必须先给默认值：PowerShell 下未赋值的变量在严格模式下取用会报错
        $titleText = ''
        if ($tag -is [hashtable] -and $tag.ContainsKey('Title')) { $titleText = [string]$tag['Title'] }

        $th = Get-UiThemeMap
        $g  = $e.Graphics
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

        $cr = $s.ClientRectangle
        $rect = New-Object System.Drawing.Rectangle -ArgumentList @(($cr.X + 1), ($cr.Y + 1), ($cr.Width - 3), ($cr.Height - 3))
        if ($rect.Width -le 8 -or $rect.Height -le 8) { return }

        $path = New-UiRoundPath -Rect $rect -Radius 8
        $brush = New-Object System.Drawing.SolidBrush $th.Card
        $g.FillPath($brush, $path)
        $brush.Dispose()
        $pen = New-Object System.Drawing.Pen -ArgumentList @($th.Border, 1)
        $g.DrawPath($pen, $path)
        $pen.Dispose()

        if ($titleText) {
            $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
            $font = New-FormFont 10 Bold
            $sf = New-Object System.Drawing.StringFormat
            $sf.LineAlignment = 'Center'
            # 标题的边距与高度同样是 96 DPI 下的数值，按缩放倍数换算后才是同一视觉位置
            $padX = 14.0 * $script:UiScale
            $padY = 5.0 * $script:UiScale
            $rf = New-Object System.Drawing.RectangleF -ArgumentList @(
                [float]$padX, [float]$padY,
                [float]($rect.Width - ($padX * 2)),
                [float](22.0 * $script:UiScale))
            $tb = New-Object System.Drawing.SolidBrush $th.Text
            $g.DrawString($titleText, $font, $tb, $rf, $sf)
            $tb.Dispose()
            $sf.Dispose()
            $font.Dispose()
        }
        $path.Dispose()
    })

    $parent.Controls.Add($card)
    $parent.Controls.Remove($GroupBox)
    $GroupBox.Dispose()
    return $card
}

# 递归套用主题。先处理容器（决定底色），再处理子控件。
function Invoke-UiTheme {
    param($Root)

    $t = Get-UiThemeMap

    if ($Root -is [System.Windows.Forms.Form]) {
        $Root.BackColor = $t.Bg
    }

    foreach ($c in @($Root.Controls)) {

        if ($c -is [System.Windows.Forms.GroupBox]) {
            $card = ConvertTo-UiCard -GroupBox $c
            Invoke-UiTheme -Root $card
            continue
        }

        if ($c -is [System.Windows.Forms.Button]) {
            # 调用方可以用 Tag 显式指定层级（推荐）；没指定时按原 BackColor 猜：
            # 绿色（应用 / 保存 / 绑定）-> Primary，红色（删除）-> Danger，其余 -> Secondary
            $role = 'Secondary'
            if ($c.Tag -is [string] -and $c.Tag -in @('Primary', 'Secondary', 'Danger')) {
                $role = [string]$c.Tag
                $argb = -1
            } else {
                $argb = $c.BackColor.ToArgb()
            }
            if ($argb -eq [System.Drawing.Color]::SeaGreen.ToArgb() -or
                $argb -eq [System.Drawing.Color]::FromArgb(40, 150, 110).ToArgb() -or
                $argb -eq [System.Drawing.Color]::FromArgb(200, 130, 40).ToArgb()) {
                $role = 'Primary'
            } elseif ($argb -eq [System.Drawing.Color]::FromArgb(190, 80, 80).ToArgb()) {
                $role = 'Danger'
            }
            Set-UiButtonStyle -Button $c -Role $role
            continue
        }

        if ($c -is [System.Windows.Forms.RichTextBox]) {
            $c.BackColor = $t.LogBg
            $c.ForeColor = $t.Text
            $c.BorderStyle = 'None'
            continue
        }

        if ($c -is [System.Windows.Forms.TextBox]) {
            $c.BackColor = $t.Card
            $c.ForeColor = $t.Text
            continue
        }

        if ($c -is [System.Windows.Forms.DataGridView]) {
            $c.BackColor = $t.Card
            $c.BackgroundColor = $t.Card
            $c.GridColor = $t.Border
            $c.EnableHeadersVisualStyles = $false
            $c.ColumnHeadersDefaultCellStyle.BackColor = [System.Drawing.Color]::FromArgb(250, 250, 250)
            $c.ColumnHeadersDefaultCellStyle.ForeColor = $t.Text
            $c.ColumnHeadersDefaultCellStyle.SelectionBackColor = [System.Drawing.Color]::FromArgb(250, 250, 250)
            $c.ColumnHeadersDefaultCellStyle.SelectionForeColor = $t.Text
            $c.AlternatingRowsDefaultCellStyle.BackColor = [System.Drawing.Color]::FromArgb(252, 252, 252)
            $c.DefaultCellStyle.SelectionBackColor = $t.AccentSoft
            $c.DefaultCellStyle.SelectionForeColor = $t.Text
            continue
        }

        if ($c -is [System.Windows.Forms.Label]) {
            # 只改默认黑色文字的标签；顶部标题那种显式设成白色的保持原样
            if ($c.ForeColor.ToArgb() -eq [System.Drawing.Color]::Black.ToArgb()) { $c.ForeColor = $t.Text }
            continue
        }

        if ($c -is [System.Windows.Forms.ListBox]) {
            $c.ForeColor = $t.Text
            $c.BackColor = $t.Card
            # 默认的系统深蓝选中色与浅色主题冲突，换成强调色
            if ($c.DrawMode -eq 'Normal') {
                $c.DrawMode = 'OwnerDrawFixed'
                $c.ItemHeight = [int]($c.Font.GetHeight() + 8)
                $c.Add_DrawItem({
                    param($sender, $e)
                    if ($e.Index -lt 0) { return }
                    $th = Get-UiThemeMap
                    $selected = (($e.State -band [System.Windows.Forms.DrawItemState]::Selected) -ne 0)
                    $fill = $th.Card
                    $fore = $th.Text
                    if ($selected) { $fill = $th.AccentSoft; $fore = $th.Text }
                    $b = New-Object System.Drawing.SolidBrush $fill
                    $e.Graphics.FillRectangle($b, $e.Bounds)
                    $b.Dispose()
                    $tb = New-Object System.Drawing.SolidBrush $fore
                    $rf = New-Object System.Drawing.RectangleF -ArgumentList @(
                        [float]($e.Bounds.X + (6 * $script:UiScale)),
                        [float]$e.Bounds.Y,
                        [float]($e.Bounds.Width - (8 * $script:UiScale)),
                        [float]$e.Bounds.Height)
                    $e.Graphics.DrawString([string]$sender.Items[$e.Index], $sender.Font, $tb, $rf)
                    $tb.Dispose()
                })
            }
            continue
        }

        if ($c -is [System.Windows.Forms.CheckBox]) {
            $c.ForeColor = $t.TextMuted
            $c.Cursor = 'Hand'
            continue
        }

        if ($c.HasChildren) { Invoke-UiTheme -Root $c }
    }

    # 只在最外层跑一次：此时所有控件都已建好、GroupBox 也换成卡片了，
    # 统一按屏幕缩放倍数放大坐标与尺寸，把「系统不再拉伸」丢掉的视觉尺寸补回来。
    if ($Root -is [System.Windows.Forms.Form]) {
        Invoke-UiScaleLayout -Root $Root -Factor $script:UiScale
    }
}

# 在配置集里按名称定位，容忍首尾空格与大小写差异，返回真实名称（找不到返回 $null）
# 桌面快捷方式把名称写死在参数里，手工改名后只要差一个空格或一个字母就会失配
function Resolve-ProfileName {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    $want  = $Name.Trim()
    $items = @(ConvertTo-Array $script:ConfigData.profiles)

    foreach ($item in $items) { if ([string]$item.name -ceq $want) { return [string]$item.name } }
    foreach ($item in $items) { if (([string]$item.name).Trim() -ceq $want) { return [string]$item.name } }
    foreach ($item in $items) {
        if (([string]$item.name).Trim().ToLowerInvariant() -eq $want.ToLowerInvariant()) { return [string]$item.name }
    }
    return $null
}

# 列出全部配置集名称，用于「找不到配置集」时告诉用户可选范围
function Get-ProfileNameList {
    $names = @()
    foreach ($item in (ConvertTo-Array $script:ConfigData.profiles)) { $names += [string]$item.name }
    return @($names)
}

function Get-ProfileByName {
    param([string]$Name)
    $resolved = Resolve-ProfileName $Name
    if ($null -eq $resolved) { return $null }
    foreach ($item in (ConvertTo-Array $script:ConfigData.profiles)) {
        if ($item.name -eq $resolved) { return $item }
    }
    return $null
}

function Get-ProfileSummaryText {
    param($SourceProfile)

    if ($null -eq $SourceProfile) { return '请从左侧选择一个配置集。' }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine("名称：$($SourceProfile.name)")
    if ($SourceProfile.remark) { [void]$builder.AppendLine("说明：$($SourceProfile.remark)") }
    [void]$builder.AppendLine('─' * 76)

    $adapters = ConvertTo-Array $SourceProfile.adapters
    if ($adapters.Count -eq 0) {
        [void]$builder.AppendLine('（该配置集还没有网卡条目，请点「编辑」添加）')
        return $builder.ToString()
    }

    foreach ($ad in $adapters) {
        $flag = if ($ad.enabled) { '启用' } else { '停用' }
        [void]$builder.AppendLine("[匹配网卡] $(Get-AdapterDisplayText ([string]$ad.adapter))    $flag")
        if ($ad.mode -eq 'dhcp') {
            [void]$builder.AppendLine('    获取方式: DHCP 自动获取')
            $dns = ConvertTo-StringList $ad.dns
            if ($dns.Count -gt 0 -and -not $ad.dhcpDns) {
                [void]$builder.AppendLine("    DNS     : $($dns -join ', ') (手动指定)")
            } else {
                [void]$builder.AppendLine('    DNS     : 自动获取')
            }
        } else {
            $mask = if ([string]::IsNullOrWhiteSpace($ad.mask)) { '255.255.255.0' } else { $ad.mask }
            [void]$builder.AppendLine("    IPv4    : $(if ($ad.ip) { $ad.ip } else { '（未填写）' })  掩码: $mask")
            $gateways = ConvertTo-StringList $ad.gateways
            [void]$builder.AppendLine("    网关    : $(if ($gateways.Count -gt 0) { $gateways -join ', ' } else { '（不设置）' })")
            $dns = ConvertTo-StringList $ad.dns
            [void]$builder.AppendLine("    DNS     : $(if ($dns.Count -gt 0) { $dns -join ', ' } else { '（自动）' })")
        }
        $routes = ConvertTo-StringList $ad.routes
        if ($routes.Count -gt 0) {
            [void]$builder.AppendLine("    附加路由: $($routes -join ' ; ')")
        }
        [void]$builder.AppendLine('')
    }
    return $builder.ToString()
}

# 算出一个配置集真正会命中哪些网卡（只统计 enabled 的条目），用于「应用前确认」和「预览」。
# 返回对象：.Count = 命中的网卡数量；.Text = 可直接展示的多行摘要（含每块网卡当前 IP）。
# 走的是 Find-MatchedAdapters，因此与真正应用时的匹配结果完全一致（含虚拟网卡排除）。
function Get-ProfileImpact {
    param($SourceProfile)

    $result = [pscustomobject]@{
        Count = 0
        Text  = ''
    }
    if ($null -eq $SourceProfile) { return $result }

    $builder = New-Object System.Text.StringBuilder
    $count   = 0

    foreach ($config in (ConvertTo-Array $SourceProfile.adapters)) {
        if (-not $config.enabled) { continue }

        $matched = @(Find-MatchedAdapters ([string]$config.adapter))
        if ($matched.Count -eq 0) { continue }

        if ([string]$config.mode -eq 'dhcp') {
            $modeText = 'DHCP 自动获取'
        } else {
            $ipText = [string]$config.ip
            if ([string]::IsNullOrWhiteSpace($ipText.Trim())) { $ipText = '（未填写 IP）' }
            $modeText = "静态 $ipText"
        }

        foreach ($adapter in $matched) {
            $count++
            $current = @(Get-NetIPAddress -InterfaceIndex ([int]$adapter.InterfaceIndex) -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                         Where-Object { $_.IPAddress -ne '127.0.0.1' } |
                         ForEach-Object { $_.IPAddress })
            $currentText = if ($current.Count -gt 0) { $current -join ', ' } else { '（当前无 IPv4）' }
            [void]$builder.AppendLine("  • $($adapter.Name)")
            [void]$builder.AppendLine("      当前 $currentText   →   $modeText")
        }
    }

    $result.Count = $count
    $result.Text  = $builder.ToString()
    return $result
}

# 配置集列表项的第二行灰色摘要，例如「静态 192.168.120.130」「DHCP」
function Get-ProfileBriefText {
    param($SourceProfile)

    if ($null -eq $SourceProfile) { return '' }

    $adapters = @(ConvertTo-Array $SourceProfile.adapters)
    if ($adapters.Count -eq 0) { return '(无网卡条目)' }

    $first = $adapters[0]
    if ([string]$first.mode -eq 'dhcp') {
        $brief = 'DHCP'
    } else {
        $ip = [string]$first.ip
        if ([string]::IsNullOrWhiteSpace($ip.Trim())) { $ip = '未填写 IP' }
        $brief = "静态 $ip"
    }
    if (-not $first.enabled) { $brief += '（已停用）' }
    if ($adapters.Count -gt 1) { $brief += "  +$($adapters.Count - 1) 项" }
    return $brief
}

# ---------- 重新绑定网卡 ----------

# 配置集里写的网卡名，一旦用户在 Windows 里把网卡改了名（「以太网」→「内网」），按名称匹配就会落空，
# 整个配置集随之失效。这里弹出一个对话框：左边列配置集里的网卡条目，右边列本机真实网卡，
# 让用户挑一条重新绑定，并按「网卡名称」或「MAC 地址」写回配置集（MAC 不随改名变化，故为推荐项）。
# 成功重绑返回 $true，用户取消或出错返回 $false；异常一律吞掉并记日志，不让其冒泡。
function Show-AdapterRebindDialog {
    param(
        [Alias('Profile')]
        $SourceProfile
    )

    try {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing

        if ($null -eq $SourceProfile) {
            Write-NSLog '重新绑定网卡失败：没有指定配置集。' 'ERROR'
            return $false
        }

        $profileName = [string]$SourceProfile.name
        $entries = @(ConvertTo-Array $SourceProfile.adapters)
        if ($entries.Count -eq 0) {
            Write-NSLog "重新绑定网卡失败：配置集「$profileName」里没有网卡条目。" 'ERROR'
            return $false
        }

        # 复用 Get-AdapterList：它已经按开关排除了虚拟/非物理网卡
        $adapters = @(Get-AdapterList)
        if ($adapters.Count -eq 0) {
            Write-NSLog '重新绑定网卡失败：本机没有可用的网卡（可能都被虚拟网卡排除规则挡掉了）。' 'ERROR'
            return $false
        }

        $form = New-Object System.Windows.Forms.Form
        $form.Text = '重新绑定网卡'
        $form.Size = New-Object System.Drawing.Size(520, 420)
        $form.MinimumSize = New-Object System.Drawing.Size(480, 380)
        $form.StartPosition = 'CenterParent'
        $form.FormBorderStyle = 'FixedDialog'
        $form.MaximizeBox = $false
        $form.MinimizeBox = $false
        $form.Font = New-FormFont 9

        $lblEntries = New-Object System.Windows.Forms.Label
        $lblEntries.Text = "配置集「$profileName」里匹配不到的网卡条目："
        $lblEntries.Location = New-Object System.Drawing.Point(12, 12)
        $lblEntries.Size = New-Object System.Drawing.Size(480, 20)

        $lstEntries = New-Object System.Windows.Forms.ListBox
        $lstEntries.Location = New-Object System.Drawing.Point(12, 34)
        $lstEntries.Size = New-Object System.Drawing.Size(480, 76)
        $lstEntries.Font = New-MonoFont 9
        foreach ($entry in $entries) {
            [void]$lstEntries.Items.Add((Get-AdapterDisplayText ([string]$entry.adapter)))
        }
        if ($lstEntries.Items.Count -gt 0) { $lstEntries.SelectedIndex = 0 }

        $lblAdapters = New-Object System.Windows.Forms.Label
        $lblAdapters.Text = '选择要绑定到哪块网卡（已排除虚拟网卡）：'
        $lblAdapters.Location = New-Object System.Drawing.Point(12, 116)
        $lblAdapters.Size = New-Object System.Drawing.Size(480, 20)

        $lstAdapters = New-Object System.Windows.Forms.ListBox
        $lstAdapters.Location = New-Object System.Drawing.Point(12, 138)
        $lstAdapters.Size = New-Object System.Drawing.Size(480, 150)
        $lstAdapters.Font = New-MonoFont 9
        foreach ($nic in $adapters) {
            [void]$lstAdapters.Items.Add(('{0}  |  {1}  |  {2}' -f [string]$nic.Name, [string]$nic.InterfaceDescription, [string]$nic.MacAddress))
        }
        if ($lstAdapters.Items.Count -gt 0) { $lstAdapters.SelectedIndex = 0 }

        # 一律按 MAC 记录（MAC 不随系统改名变化），界面上仍显示网卡名称，所以不再提供选项
        $lblMode = New-Object System.Windows.Forms.Label
        $lblMode.Text = '绑定方式：按 MAC 地址记录（界面仍显示网卡名称，以后在系统里改名也不会失效）'
        $lblMode.Location = New-Object System.Drawing.Point(12, 296)
        $lblMode.Size = New-Object System.Drawing.Size(480, 20)
        $lblMode.ForeColor = [System.Drawing.Color]::FromArgb(110, 122, 140)

        $btnBind = New-Object System.Windows.Forms.Button
        $btnBind.Text = '绑定'
        $btnBind.Location = New-Object System.Drawing.Point(292, 330)
        $btnBind.Size = New-Object System.Drawing.Size(96, 32)
        $btnBind.BackColor = [System.Drawing.Color]::SeaGreen
        $btnBind.ForeColor = [System.Drawing.Color]::White
        $btnBind.FlatStyle = 'Flat'

        $btnCancel = New-Object System.Windows.Forms.Button
        $btnCancel.Text = '取消'
        $btnCancel.Location = New-Object System.Drawing.Point(396, 330)
        $btnCancel.Size = New-Object System.Drawing.Size(96, 32)
        $btnCancel.FlatStyle = 'Flat'

        $form.AcceptButton = $btnBind
        $form.CancelButton = $btnCancel

        $btnBind.Add_Click({
            try {
                if ($lstEntries.SelectedIndex -lt 0) {
                    [System.Windows.Forms.MessageBox]::Show('请先选择要重新绑定的网卡条目。', '提示', 'OK', 'Information') | Out-Null
                    return
                }
                if ($lstAdapters.SelectedIndex -lt 0) {
                    [System.Windows.Forms.MessageBox]::Show('请选择要绑定到哪一块本机网卡。', '提示', 'OK', 'Information') | Out-Null
                    return
                }

                $entry = $entries[$lstEntries.SelectedIndex]
                $nic   = $adapters[$lstAdapters.SelectedIndex]

                # 存 MAC、显名称：日志里也用名称，方便核对
                $newValue = [string]$nic.MacAddress
                if ([string]::IsNullOrWhiteSpace($newValue)) { $newValue = [string]$nic.Name }
                $oldText = Get-AdapterDisplayText ([string]$entry.adapter)
                $newText = Get-AdapterDisplayText $newValue

                if ($entry.PSObject.Properties.Name -contains 'adapter') {
                    $entry.adapter = $newValue
                } else {
                    $entry | Add-Member NoteProperty adapter $newValue -Force
                }

                Save-ConfigFile | Out-Null
                Write-NSLog "配置集「$profileName」已重新绑定网卡条目：「$oldText」→「$newText」（按 MAC 地址记录）。" 'OK'

                $form.DialogResult = 'OK'
                $form.Close()
            } catch {
                Write-NSLog "重新绑定网卡失败：$($_.Exception.Message)" 'ERROR'
                $form.DialogResult = 'Cancel'
                $form.Close()
            }
        })

        $btnCancel.Add_Click({
            $form.DialogResult = 'Cancel'
            $form.Close()
        })

        $form.Controls.AddRange(@($lblEntries, $lstEntries, $lblAdapters, $lstAdapters, $lblMode, $btnBind, $btnCancel))

        $btnBind.Tag = 'Primary'
        try { Invoke-UiTheme -Root $form } catch { }

        $result = $form.ShowDialog()
        return ($result -eq [System.Windows.Forms.DialogResult]::OK)
    } catch {
        Write-NSLog "打开重新绑定网卡对话框失败：$($_.Exception.Message)" 'ERROR'
        return $false
    }
}

# ---------- 配置集编辑器 ----------

function Show-ProfileEditor {
    param(
        # 调用方历史上一直写成 -Profile，而参数本名是 SourceProfile；
        # 未绑定时 $SourceProfile 恒为 $null，表现是「点编辑却打开空的新建窗口」。
        # 这里保留 Profile 别名兜底，同时调用处已统一改为 -SourceProfile。
        [Alias('Profile')]
        $SourceProfile,          # $null 表示新建
        [string[]]$AdapterNames
    )

    # 本函数也可能被单独调用（不经过 Show-MainForm），这里自行确保 WinForms 已加载
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    $form = New-Object System.Windows.Forms.Form
    $form.Text = if ($null -eq $SourceProfile) { '新建配置集' } else { "编辑配置集 - $($SourceProfile.name)" }
    $form.Size = New-Object System.Drawing.Size(1020, 620)
    $form.MinimumSize = New-Object System.Drawing.Size(900, 560)
    $form.StartPosition = 'CenterParent'
    $form.Font = New-FormFont 9
    $form.FormBorderStyle = 'Sizable'
    $form.MaximizeBox = $false

    # 名称 / 说明
    $lblName = New-Object System.Windows.Forms.Label
    $lblName.Text = '名称：'; $lblName.Location = New-Object System.Drawing.Point(14, 18); $lblName.Size = New-Object System.Drawing.Size(50, 22)
    $txtName = New-Object System.Windows.Forms.TextBox
    $txtName.Location = New-Object System.Drawing.Point(66, 14); $txtName.Size = New-Object System.Drawing.Size(230, 26)
    $txtName.Text = if ($null -eq $SourceProfile) { '' } else { $SourceProfile.name }

    $lblRemark = New-Object System.Windows.Forms.Label
    $lblRemark.Text = '说明：'; $lblRemark.Location = New-Object System.Drawing.Point(320, 18); $lblRemark.Size = New-Object System.Drawing.Size(50, 22)
    $txtRemark = New-Object System.Windows.Forms.TextBox
    $txtRemark.Location = New-Object System.Drawing.Point(372, 14); $txtRemark.Size = New-Object System.Drawing.Size(420, 26)
    $txtRemark.Text = if ($null -eq $SourceProfile) { '' } else { $SourceProfile.remark }

    # 网卡选择 + 添加行
    $lblPick = New-Object System.Windows.Forms.Label
    $lblPick.Text = '从本机网卡添加：'; $lblPick.Location = New-Object System.Drawing.Point(14, 54); $lblPick.Size = New-Object System.Drawing.Size(110, 22)
    $cboAdapters = New-Object System.Windows.Forms.ComboBox
    $cboAdapters.Location = New-Object System.Drawing.Point(128, 50); $cboAdapters.Size = New-Object System.Drawing.Size(240, 26)
    $cboAdapters.DropDownStyle = 'DropDownList'
    foreach ($name in $AdapterNames) { [void]$cboAdapters.Items.Add($name) }
    if ($cboAdapters.Items.Count -gt 0) { $cboAdapters.SelectedIndex = 0 }

    $btnAddRow = New-Object System.Windows.Forms.Button
    $btnAddRow.Text = '+ 添加网卡行'; $btnAddRow.Location = New-Object System.Drawing.Point(378, 48); $btnAddRow.Size = New-Object System.Drawing.Size(120, 28)
    $btnAddRow.BackColor = [System.Drawing.Color]::FromArgb(70, 130, 180)
    $btnAddRow.ForeColor = [System.Drawing.Color]::White
    $btnAddRow.FlatStyle = 'Flat'

    $btnAddWildcard = New-Object System.Windows.Forms.Button
    $btnAddWildcard.Text = '+ 全部网卡（*）'; $btnAddWildcard.Location = New-Object System.Drawing.Point(506, 48); $btnAddWildcard.Size = New-Object System.Drawing.Size(130, 28)

    $btnRemoveRow = New-Object System.Windows.Forms.Button
    $btnRemoveRow.Text = '- 删除选中行'; $btnRemoveRow.Location = New-Object System.Drawing.Point(644, 48); $btnRemoveRow.Size = New-Object System.Drawing.Size(120, 28)

    $lblTip = New-Object System.Windows.Forms.Label
    $lblTip.Text = "填写方式：在下方表格里双击单元格，直接输入 IP / 子网掩码 / 网关 / DNS；最左侧「启用」列打勾表示该行生效。`n" +
                   '「网卡名称/匹配」列照常填网卡名即可（如「以太网」）：程序会自动按 MAC 地址记住这块网卡，界面上仍显示名称，' +
                   '以后在系统里给网卡改名也不会失效。也可用 *（全部网卡）或 以太网* 这类模糊匹配；网关可写为 192.168.1.1:10 指定跃点数，多个网关或 DNS 用分号隔开；附加路由写法 10.0.0.0/8>网关'
    $lblTip.Location = New-Object System.Drawing.Point(14, 78); $lblTip.Size = New-Object System.Drawing.Size(950, 48)
    $lblTip.ForeColor = [System.Drawing.Color]::DimGray

    # 表格
    $table = New-Object System.Data.DataTable
    [void]$table.Columns.Add('Enabled', [bool])
    [void]$table.Columns.Add('Adapter', [string])
    [void]$table.Columns.Add('Mode', [string])
    [void]$table.Columns.Add('IP', [string])
    [void]$table.Columns.Add('Mask', [string])
    [void]$table.Columns.Add('Gateways', [string])
    [void]$table.Columns.Add('Metric', [string])
    [void]$table.Columns.Add('DNS', [string])
    [void]$table.Columns.Add('Routes', [string])
    $table.Columns['Enabled'].DefaultValue = $true
    $table.Columns['Mode'].DefaultValue    = '静态IP'
    $table.Columns['Mask'].DefaultValue    = '255.255.255.0'

    if ($null -ne $SourceProfile) {
        foreach ($ad in (ConvertTo-Array $SourceProfile.adapters)) {
            $row = $table.NewRow()
            $row['Enabled']  = [bool]$ad.enabled
            $row['Adapter']  = Get-AdapterDisplayText ([string]$ad.adapter)
            $row['Mode']     = if ($ad.mode -eq 'dhcp') { 'DHCP自动' } else { '静态IP' }
            $row['IP']       = [string]$ad.ip
            $row['Mask']     = [string]$ad.mask
            $row['Gateways'] = ((ConvertTo-StringList $ad.gateways) -join ';')
            $row['Metric']   = if ($null -ne $ad.defaultMetric) { [string]$ad.defaultMetric } else { '' }
            $row['DNS']      = ((ConvertTo-StringList $ad.dns) -join ';')
            $row['Routes']   = ((ConvertTo-StringList $ad.routes) -join ';')
            [void]$table.Rows.Add($row)
        }
    }

    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Location = New-Object System.Drawing.Point(14, 130)
    $grid.Size = New-Object System.Drawing.Size(980, 376)
    $grid.Anchor = 'Top, Bottom, Left, Right'
    $grid.AllowUserToAddRows = $false
    $grid.AllowUserToDeleteRows = $false
    $grid.AutoSizeColumnsMode = 'Fill'
    $grid.SelectionMode = 'FullRowSelect'
    $grid.RowHeadersVisible = $false
    $grid.MultiSelect = $false
    $grid.BackgroundColor = [System.Drawing.Color]::White
    $grid.BorderStyle = 'FixedSingle'
    $grid.EditMode = 'EditOnEnter'          # 点进单元格即可直接输入，省一次双击
    $grid.RowTemplate.Height = 28

    # 列必须显式创建：DataGridView 要等窗口显示、句柄建立之后才会按 DataSource 自动生成列，
    # 在窗口构造阶段按列名读写（如 Columns['Mode']）会取到空值并抛未处理异常。
    $grid.AutoGenerateColumns = $false

    $colEnabled = New-Object System.Windows.Forms.DataGridViewCheckBoxColumn
    $colEnabled.Name = 'Enabled'; $colEnabled.DataPropertyName = 'Enabled'
    $colEnabled.HeaderText = '启用'; $colEnabled.FillWeight = 22

    $colAdapter = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colAdapter.Name = 'Adapter'; $colAdapter.DataPropertyName = 'Adapter'
    $colAdapter.HeaderText = '网卡名称/匹配(支持*)'; $colAdapter.FillWeight = 58

    $colMode = New-Object System.Windows.Forms.DataGridViewComboBoxColumn
    $colMode.Name = 'Mode'; $colMode.DataPropertyName = 'Mode'
    $colMode.HeaderText = '获取方式'; $colMode.FillWeight = 40
    [void]$colMode.Items.Add('静态IP')
    [void]$colMode.Items.Add('DHCP自动')

    $colIp = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colIp.Name = 'IP'; $colIp.DataPropertyName = 'IP'
    $colIp.HeaderText = 'IP 地址'; $colIp.FillWeight = 46

    $colMask = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colMask.Name = 'Mask'; $colMask.DataPropertyName = 'Mask'
    $colMask.HeaderText = '子网掩码'; $colMask.FillWeight = 44

    $colGw = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colGw.Name = 'Gateways'; $colGw.DataPropertyName = 'Gateways'
    $colGw.HeaderText = '网关[:跃点]'; $colGw.FillWeight = 56

    # 默认路由跃点数（可选）：留空=自动；填了就覆盖「单网关用系统默认值 / 多网关 10+序号」的行为
    $colMetric = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colMetric.Name = 'Metric'; $colMetric.DataPropertyName = 'Metric'
    $colMetric.HeaderText = '默认跃点(可选)'; $colMetric.FillWeight = 34

    $colDns = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colDns.Name = 'DNS'; $colDns.DataPropertyName = 'DNS'
    $colDns.HeaderText = 'DNS 服务器'; $colDns.FillWeight = 54

    $colRoutes = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colRoutes.Name = 'Routes'; $colRoutes.DataPropertyName = 'Routes'
    $colRoutes.HeaderText = '附加路由(可选)'; $colRoutes.FillWeight = 62

    foreach ($col in @($colEnabled, $colAdapter, $colMode, $colIp, $colMask, $colGw, $colMetric, $colDns, $colRoutes)) {
        [void]$grid.Columns.Add($col)
    }

    $grid.DataSource = $table

    # 表格为空时盖一层提示，明确告诉用户「IP 要填在哪」，避免面对空白表格无从下手
    $lblEmpty = New-Object System.Windows.Forms.Label
    $lblEmpty.Text = "表格还是空的`n`n请点上方「+ 添加网卡行」加入一行，`n再双击单元格填写 IP / 掩码 / 网关 / DNS"
    $lblEmpty.Location = New-Object System.Drawing.Point(24, 136)
    $lblEmpty.Size = New-Object System.Drawing.Size(960, 130)
    $lblEmpty.TextAlign = 'MiddleCenter'
    $lblEmpty.Font = New-FormFont 11
    $lblEmpty.ForeColor = [System.Drawing.Color]::FromArgb(120, 132, 148)
    $lblEmpty.BackColor = [System.Drawing.Color]::White
    $lblEmpty.Visible = $false

    # 底部按钮
    $btnSave = New-Object System.Windows.Forms.Button
    $btnSave.Text = '保存'; $btnSave.Size = New-Object System.Drawing.Size(120, 36); $btnSave.Location = New-Object System.Drawing.Point(760, 522)
    $btnSave.Anchor = 'Bottom, Right'
    $btnSave.BackColor = [System.Drawing.Color]::SeaGreen
    $btnSave.ForeColor = [System.Drawing.Color]::White
    $btnSave.FlatStyle = 'Flat'
    $btnSave.Font = New-FormFont 9 Bold
    $form.AcceptButton = $btnSave

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = '取消'; $btnCancel.Size = New-Object System.Drawing.Size(120, 36); $btnCancel.Location = New-Object System.Drawing.Point(890, 522)
    $btnCancel.Anchor = 'Bottom, Right'

    # 事件
    $addFromCombo = {
        if ($null -eq $cboAdapters.SelectedItem) { return }
        $adapterName = [string]$cboAdapters.SelectedItem
        $currentIp = ''
        $currentMask = '255.255.255.0'
        try {
            $ifObj = Get-NetAdapter -Name $adapterName -ErrorAction Stop
            $addr = Get-NetIPAddress -InterfaceIndex $ifObj.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                    Where-Object { $_.IPAddress -ne '127.0.0.1' } | Select-Object -First 1
            if ($addr) {
                $currentIp = $addr.IPAddress
                $currentMask = ConvertFrom-PrefixToMask $addr.PrefixLength
            }
            $gwDefault = Get-NetRoute -InterfaceIndex $ifObj.InterfaceIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                         Select-Object -First 1
            $gwText = if ($gwDefault) { $gwDefault.NextHop } else { '' }
        } catch {
            $gwText = ''
        }

        $newRow = $table.NewRow()
        $newRow['Enabled']  = $true
        $newRow['Adapter']  = $adapterName
        $newRow['Mode']     = '静态IP'
        $newRow['IP']       = $currentIp
        $newRow['Mask']     = $currentMask
        $newRow['Gateways'] = $gwText
        $newRow['Metric']   = ''
        $newRow['DNS']      = ''
        $newRow['Routes']   = ''
        [void]$table.Rows.Add($newRow)
        & $updateEmptyHint
    }

    $btnAddRow.Add_Click($addFromCombo)

    # 表格空/非空时切换提示层
    $updateEmptyHint = {
        $lblEmpty.Visible = ($table.Rows.Count -eq 0)
        if ($lblEmpty.Visible) { $lblEmpty.BringToFront() }
    }

    $btnAddWildcard.Add_Click({
        $newRow = $table.NewRow()
        $newRow['Enabled']  = $true
        $newRow['Adapter']  = '*'
        $newRow['Mode']     = 'DHCP自动'
        $newRow['IP']       = ''
        $newRow['Mask']     = '255.255.255.0'
        $newRow['Gateways'] = ''
        $newRow['Metric']   = ''
        $newRow['DNS']      = ''
        $newRow['Routes']   = ''
        [void]$table.Rows.Add($newRow)
        & $updateEmptyHint
    })

    $btnRemoveRow.Add_Click({
        if ($grid.CurrentRow -and -not $grid.CurrentRow.IsNewRow) {
            $grid.Rows.RemoveAt($grid.CurrentRow.Index)
        }
        & $updateEmptyHint
    })


    $btnSave.Add_Click({
        # 确保正在编辑的单元格内容提交到 DataTable
        $grid.EndEdit()
        $grid.CommitEdit([System.Windows.Forms.DataGridViewDataErrorContexts]::Commit)

        $name = $txtName.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($name)) {
            [System.Windows.Forms.MessageBox]::Show('请填写配置集名称。', '提示', 'OK', 'Information') | Out-Null
            return
        }
        $exists = $false
        foreach ($item in (ConvertTo-Array $script:ConfigData.profiles)) {
            if ($item.name -eq $name -and ($null -eq $SourceProfile -or $item.name -ne $SourceProfile.name)) { $exists = $true; break }
        }
        if ($exists) {
            [System.Windows.Forms.MessageBox]::Show("已存在同名配置集「$name」，请换一个名称。", '提示', 'OK', 'Warning') | Out-Null
            return
        }

        $adapterList = @()
        foreach ($row in $table.Rows) {
            if ($row.RowState -eq 'Deleted') { continue }
            $adapterName = [string]$row['Adapter']
            if ([string]::IsNullOrWhiteSpace($adapterName)) { continue }
            $modeText = [string]$row['Mode']
            if ($modeText -ne 'DHCP自动') { $modeText = '静态IP' }

            $dnsText = [string]$row['DNS']
            $dnsArray = @(Split-ItemList $dnsText)

            $adapterList += [pscustomobject][ordered]@{
                # 存 MAC：用户填的是网卡名，这里换成该网卡的 MAC，以后改名也不会失效
                adapter  = ConvertTo-AdapterStorageValue $adapterName
                enabled  = [bool]$row['Enabled']
                mode     = if ($modeText -eq 'DHCP自动') { 'dhcp' } else { 'static' }
                ip       = ([string]$row['IP']).Trim()
                mask     = ([string]$row['Mask']).Trim()
                gateways = @(Split-ItemList ([string]$row['Gateways']))
                dns      = $dnsArray
                dhcpDns  = ($dnsArray.Count -eq 0)
                defaultMetric = ConvertTo-MetricValue ([string]$row['Metric'])
                routes   = @(Split-ItemList ([string]$row['Routes']))
            }
        }

        $script:EditorResult = [pscustomobject][ordered]@{
            name     = $name
            remark   = $txtRemark.Text.Trim()
            adapters = $adapterList
        }
        $form.DialogResult = 'OK'
        $form.Close()
    })

    $btnCancel.Add_Click({ $form.DialogResult = 'Cancel'; $form.Close() })

    $form.Controls.AddRange(@(
        $lblName, $txtName, $lblRemark, $txtRemark,
        $lblPick, $cboAdapters, $btnAddRow, $btnAddWildcard, $btnRemoveRow, $lblTip,
        $grid, $lblEmpty, $btnSave, $btnCancel
    ))

    $btnSave.Tag = 'Primary'
    # 编辑器是固定坐标布局，套用卡片化会整体位移，这里只统一配色与按钮观感
    try { Invoke-UiTheme -Root $form } catch { }
    # 空表格提示层盖在表格上，套完主题要重新压到最上层
    if ($lblEmpty.Visible) { $lblEmpty.BringToFront() }

    $form.Add_Shown({
        # 新建时先自动放一行，用户打开就能直接往格子里填 IP，不必再找入口
        if ($null -eq $SourceProfile -and $table.Rows.Count -eq 0) { & $addFromCombo }
        & $updateEmptyHint
        if ($grid.Rows.Count -gt 0) { $grid.CurrentCell = $grid.Rows[0].Cells['IP'] }
    })

    $script:EditorResult = $null
    $dialog = $form.ShowDialog()
    if ($dialog -eq 'OK') { return $script:EditorResult }
    return $null
}

# ---------- 备份记录 ----------

function Show-BackupManager {

    $dir = Get-BackupDirectory
    $script:BackupManagerChanged = $false

    $form = New-Object System.Windows.Forms.Form
    $form.Text = '备份记录（切换配置集前自动保存）'
    $form.Size = New-Object System.Drawing.Size(620, 452)
    $form.StartPosition = 'CenterParent'
    $form.MinimizeBox = $false
    $form.MaximizeBox = $false
    $form.Font = New-FormFont 9

    $lblDir = New-Object System.Windows.Forms.Label
    $lblDir.Text = "备份目录：$dir"
    $lblDir.Location = New-Object System.Drawing.Point(14, 12)
    $lblDir.Size = New-Object System.Drawing.Size(580, 20)
    $lblDir.ForeColor = [System.Drawing.Color]::DimGray

    $list = New-Object System.Windows.Forms.ListBox
    $list.Location = New-Object System.Drawing.Point(14, 40)
    $list.Size = New-Object System.Drawing.Size(580, 250)
    $list.Font = New-MonoFont 9
    $list.BorderStyle = 'FixedSingle'

    $files = @(Get-BackupFiles)
    foreach ($item in $files) { [void]$list.Items.Add($item.Name) }
    if ($list.Items.Count -gt 0) { $list.SelectedIndex = 0 }

    $lblTip = New-Object System.Windows.Forms.Label
    $lblTip.Text = '每次点「应用此配置集」之前，程序都会把当时的网络参数存成一条备份。' + "`n" +
                  '选中一条点「导入为配置集」，它就会出现在左侧列表里，再对它点「应用此配置集」即可切回当时的状态。'
    $lblTip.Location = New-Object System.Drawing.Point(14, 298)
    $lblTip.Size = New-Object System.Drawing.Size(580, 44)
    $lblTip.ForeColor = [System.Drawing.Color]::FromArgb(60, 72, 90)

    $btnRestore = New-Object System.Windows.Forms.Button
    $btnRestore.Text = '导入为配置集'
    $btnRestore.Location = New-Object System.Drawing.Point(14, 356)
    $btnRestore.Size = New-Object System.Drawing.Size(140, 36)
    $btnRestore.FlatStyle = 'Flat'
    $btnRestore.BackColor = [System.Drawing.Color]::FromArgb(40, 150, 110)
    $btnRestore.ForeColor = [System.Drawing.Color]::White

    $btnOpenDir = New-Object System.Windows.Forms.Button
    $btnOpenDir.Text = '打开备份目录'
    $btnOpenDir.Location = New-Object System.Drawing.Point(164, 356)
    $btnOpenDir.Size = New-Object System.Drawing.Size(140, 36)
    $btnOpenDir.FlatStyle = 'Flat'
    $btnOpenDir.BackColor = [System.Drawing.Color]::FromArgb(110, 122, 140)
    $btnOpenDir.ForeColor = [System.Drawing.Color]::White

    $btnRestoreDirect = New-Object System.Windows.Forms.Button
    $btnRestoreDirect.Text = '还原此备份到网卡'
    $btnRestoreDirect.Location = New-Object System.Drawing.Point(314, 356)
    $btnRestoreDirect.Size = New-Object System.Drawing.Size(140, 36)
    $btnRestoreDirect.FlatStyle = 'Flat'
    $btnRestoreDirect.BackColor = [System.Drawing.Color]::FromArgb(200, 130, 40)
    $btnRestoreDirect.ForeColor = [System.Drawing.Color]::White

    $btnClose = New-Object System.Windows.Forms.Button
    $btnClose.Text = '关闭'
    $btnClose.Location = New-Object System.Drawing.Point(454, 356)
    $btnClose.Size = New-Object System.Drawing.Size(140, 36)
    $btnClose.FlatStyle = 'Flat'

    $btnRestore.Add_Click({
        if ($list.SelectedIndex -lt 0) {
            [System.Windows.Forms.MessageBox]::Show('请先选择一条备份记录。', '提示', 'OK', 'Information') | Out-Null
            return
        }
        $file = $files[$list.SelectedIndex].FullName
        try {
            $count = Import-BackupAsProfiles -Path $file
            $script:BackupManagerChanged = $true
            Write-NSLog "已从备份导入 $count 个配置集：$file" 'OK'
            [System.Windows.Forms.MessageBox]::Show(
                "已导入 $count 个配置集。回到主界面选中它，点「应用此配置集」即可切回当时的网络状态。",
                '导入完成', 'OK', 'Information') | Out-Null
            $form.Close()
        } catch {
            Write-NSLog "备份导入失败：$($_.Exception.Message)" 'ERROR'
            [System.Windows.Forms.MessageBox]::Show("导入失败：$($_.Exception.Message)", '错误', 'OK', 'Error') | Out-Null
        }
    })

    $btnOpenDir.Add_Click({
        try {
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            Start-Process explorer.exe -ArgumentList "`"$dir`"" -ErrorAction SilentlyContinue
        } catch { }
    })

    $btnRestoreDirect.Add_Click({
        if ($list.SelectedIndex -lt 0) {
            [System.Windows.Forms.MessageBox]::Show('请先选择一条备份记录。', '提示', 'OK', 'Information') | Out-Null
            return
        }
        $file = $files[$list.SelectedIndex].FullName

        # 写网卡必须有管理员权限，先问一次要不要提权重开
        if (-not (Test-IsAdmin)) {
            $answer = [System.Windows.Forms.MessageBox]::Show(
                "还原备份需要管理员权限。`n是否现在切换到管理员模式运行？",
                '需要管理员权限', 'YesNo', 'Question')
            if ($answer -eq 'Yes') {
                if (Start-Elevated) { $form.Close() }
                return
            }
            Write-NSLog '未获得管理员权限，已取消还原备份。' 'WARN'
            return
        }

        $names = @()
        try {
            $names = @(Get-BackupRestoreTargets -Path $file | ForEach-Object { $_.Adapter.Name })
        } catch {
            Write-NSLog "读取备份失败：$($_.Exception.Message)" 'ERROR'
            [System.Windows.Forms.MessageBox]::Show("读取备份失败：$($_.Exception.Message)", '错误', 'OK', 'Error') | Out-Null
            return
        }

        if ($names.Count -eq 0) {
            Write-NSLog "备份中没有匹配到本机现有的网卡，已取消还原：$file" 'WARN'
            [System.Windows.Forms.MessageBox]::Show(
                '这份备份里的网卡在本机上都找不到，无法还原。', '提示', 'OK', 'Information') | Out-Null
            return
        }

        $confirm = [System.Windows.Forms.MessageBox]::Show(
            "即将把这份备份直接写回以下网卡：`n`n" + ($names -join "`n") +
            "`n`n还原过程中网络会短暂断开，是否继续？",
            '确认还原', 'YesNo', 'Warning')
        if ($confirm -ne 'Yes') { Write-NSLog '已取消还原备份。' 'INFO'; return }

        $form.Cursor = 'WaitCursor'
        try {
            if (Restore-BackupToAdapters -Path $file) {
                $script:BackupManagerChanged = $true
                [System.Windows.Forms.MessageBox]::Show(
                    '备份已还原到网卡，请查看日志确认结果。', '还原完成', 'OK', 'Information') | Out-Null
            } else {
                [System.Windows.Forms.MessageBox]::Show(
                    '还原未全部成功，请查看日志了解详情。', '还原未完成', 'OK', 'Warning') | Out-Null
            }
        } finally {
            $form.Cursor = 'Default'
        }
    })

    $btnClose.Add_Click({ $form.Close() })

    $form.Controls.AddRange(@($lblDir, $list, $lblTip, $btnRestore, $btnOpenDir, $btnRestoreDirect, $btnClose))

    $btnRestore.Tag = 'Primary'
    # 「还原到网卡」会直接改写网络参数，风险高于「导入为配置集」，降为次级按钮避免两个主操作并列
    $btnRestoreDirect.Tag = 'Secondary'
    try { Invoke-UiTheme -Root $form } catch { }

    [void]$form.ShowDialog()

    return $script:BackupManagerChanged
}

# ---------- 主界面 ----------

function Show-MainForm {

    [void][System.Reflection.Assembly]::LoadWithPartialName('System.Windows.Forms')
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "NetSwitch $script:Version  -  内网 IP / 网关 一键切换"
    $form.Size = New-Object System.Drawing.Size(1180, 760)
    # 最小尺寸不能随便给：右侧操作区 8 个按钮固定宽 866px（左侧面板 292 + 866 = 1158 客户区），
    # 窗口再窄按钮就会互相重叠；高度低于 720 时下方日志卡片会被压扁到看不见。
    # 旧值是 (1020,660)，实测缩到该尺寸确实会出现按钮溢出与日志卡片塌陷。
    $form.MinimumSize = New-Object System.Drawing.Size(1180, 720)
    $form.StartPosition = 'CenterScreen'
    $form.Font = New-FormFont 9
    $form.BackColor = Get-UiColor 'Bg'

    # ===== 顶部 =====
    # Win11 风格：不再用深色横幅，改成与窗口同底的浅色区，靠一条细分隔线与内容区分开
    $header = New-Object System.Windows.Forms.Panel
    $header.Location = New-Object System.Drawing.Point(0, 0)
    $header.Size = New-Object System.Drawing.Size(1180, 70)
    $header.Dock = 'None'
    $header.Anchor = 'Top, Left, Right'
    $header.BackColor = Get-UiColor 'Bg'

    $headerLine = New-Object System.Windows.Forms.Panel
    $headerLine.Location = New-Object System.Drawing.Point(0, 69)
    $headerLine.Size = New-Object System.Drawing.Size(1180, 1)
    $headerLine.Anchor = 'Bottom, Left, Right'
    $headerLine.BackColor = Get-UiColor 'Border'

    $lblTitle = New-Object System.Windows.Forms.Label
    $lblTitle.Text = 'NetSwitch  内网 IP / 网关 / DNS 切换器'
    $lblTitle.ForeColor = Get-UiColor 'Text'
    $lblTitle.Font = New-FormFont 15 Bold
    $lblTitle.Location = New-Object System.Drawing.Point(20, 13)
    $lblTitle.Size = New-Object System.Drawing.Size(520, 30)

    $lblConfig = New-Object System.Windows.Forms.Label
    $lblConfig.Text = "配置文件：$($script:ConfigFile)"
    $lblConfig.ForeColor = Get-UiColor 'TextMuted'
    $lblConfig.Font = New-FormFont 8
    $lblConfig.Location = New-Object System.Drawing.Point(22, 45)
    $lblConfig.Size = New-Object System.Drawing.Size(620, 18)

    $lblAdmin = New-Object System.Windows.Forms.Label
    $lblAdmin.Location = New-Object System.Drawing.Point(700, 18)
    $lblAdmin.Size = New-Object System.Drawing.Size(300, 26)
    $lblAdmin.TextAlign = 'MiddleRight'
    $lblAdmin.Font = New-FormFont 9 Bold
    $lblAdmin.ForeColor = Get-UiColor 'Text'

    $btnElevate = New-Object System.Windows.Forms.Button
    $btnElevate.Location = New-Object System.Drawing.Point(1010, 16)
    $btnElevate.Size = New-Object System.Drawing.Size(150, 34)
    $btnElevate.Anchor = 'Top, Right'
    $btnElevate.Text = '提权并重启'
    $btnElevate.Tag = 'Primary'
    $btnElevate.Visible = $false

    $header.Controls.AddRange(@($lblTitle, $lblConfig, $lblAdmin, $btnElevate, $headerLine))

    # ===== 左侧 =====
    $side = New-Object System.Windows.Forms.Panel
    $side.Location = New-Object System.Drawing.Point(12, 82)
    $side.Size = New-Object System.Drawing.Size(268, 630)
    $side.Anchor = 'Top, Bottom, Left'
    $side.BackColor = Get-UiColor 'Bg'

    $lblSide = New-Object System.Windows.Forms.Label
    $lblSide.Text = '配置集列表'; $lblSide.Location = New-Object System.Drawing.Point(2, 0); $lblSide.Size = New-Object System.Drawing.Size(200, 20)
    $lblSide.Font = New-FormFont 9 Bold
    $lblSide.ForeColor = Get-UiColor 'TextMuted'

    $lstProfiles = New-Object System.Windows.Forms.ListBox
    $lstProfiles.Location = New-Object System.Drawing.Point(2, 24)
    $lstProfiles.Size = New-Object System.Drawing.Size(262, 400)
    $lstProfiles.Anchor = 'Top, Bottom, Left, Right'
    $lstProfiles.Font = New-FormFont 10
    $lstProfiles.DrawMode = 'OwnerDrawFixed'
    $lstProfiles.ItemHeight = 44
    $lstProfiles.BorderStyle = 'None'
    $lstProfiles.BackColor = Get-UiColor 'Bg'

    $lstProfiles.Add_DrawItem({
        param($sender, $e)
        if ($e.Index -lt 0) { return }
        $theme = Get-UiThemeMap
        $text = [string]$lstProfiles.Items[$e.Index]
        $selected = (($e.State -band [System.Windows.Forms.DrawItemState]::Selected) -ne 0)

        $g = $e.Graphics
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

        # Win11 列表项：整行底色与窗口一致，选中态是「圆角浅色块 + 左侧强调色竖条」，
        # 而不是旧式整块高饱和蓝填充。
        $bgBrush = New-Object System.Drawing.SolidBrush $theme.Bg
        $g.FillRectangle($bgBrush, $e.Bounds)
        $bgBrush.Dispose()

        $itemRect = New-Object System.Drawing.Rectangle -ArgumentList @(($e.Bounds.X + 2), ($e.Bounds.Y + 2), ($e.Bounds.Width - 6), ($e.Bounds.Height - 5))
        if ($selected) {
            $path = New-UiRoundPath -Rect $itemRect -Radius 4
            $selBrush = New-Object System.Drawing.SolidBrush $theme.AccentSoft
            $g.FillPath($selBrush, $path)
            $selBrush.Dispose()
            $path.Dispose()

            $barRect = New-Object System.Drawing.Rectangle -ArgumentList @(($itemRect.X + 3), ($itemRect.Y + 9), 3, ($itemRect.Height - 18))
            $barPath = New-UiRoundPath -Rect $barRect -Radius 1
            $barBrush = New-Object System.Drawing.SolidBrush $theme.Accent
            $g.FillPath($barBrush, $barPath)
            $barBrush.Dispose()
            $barPath.Dispose()
        }

        $foreColor = $theme.Text
        $subColor  = $theme.TextMuted
        if ($selected) { $subColor = [System.Drawing.Color]::FromArgb(64, 96, 132) }

        # 第二行灰色次要信息：取自该配置集首个网卡条目的 mode / ip
        $subText = ''
        $itemProfile = Get-ProfileByName $text
        if ($null -ne $itemProfile) { $subText = Get-ProfileBriefText $itemProfile }

        $font = New-FormFont 10
        $subFont = New-FormFont 8
        $textBrush = New-Object System.Drawing.SolidBrush($foreColor)
        $subBrush = New-Object System.Drawing.SolidBrush($subColor)
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
        # 注意：RectangleF 的每个参数都必须单独加括号，否则 PowerShell 会把逗号解析成
        # 数组构造运算符，进而触发 [System.Object[]] 的 op_Subtraction 异常。切勿去掉括号。
        $lineX = 14 * $script:UiScale
        $textRect = New-Object System.Drawing.RectangleF(([float]$lineX), ([float]($e.Bounds.Y + (5 * $script:UiScale))), ([float]($e.Bounds.Width - (24 * $script:UiScale))), ([float](18 * $script:UiScale)))
        $subRect  = New-Object System.Drawing.RectangleF(([float]$lineX), ([float]($e.Bounds.Y + (23 * $script:UiScale))), ([float]($e.Bounds.Width - (24 * $script:UiScale))), ([float](15 * $script:UiScale)))
        $g.DrawString($text, $font, $textBrush, $textRect)
        if ($subText) { $g.DrawString($subText, $subFont, $subBrush, $subRect) }
        $subBrush.Dispose()
        $textBrush.Dispose()
        $subFont.Dispose()
        $font.Dispose()
    })

    $mkButton = {
        param($Text, $X, $Y, $W, $H, $Role)
        $button = New-Object System.Windows.Forms.Button
        $button.Text = $Text
        $button.Location = New-Object System.Drawing.Point($X, $Y)
        $button.Size = New-Object System.Drawing.Size($W, $H)
        $button.Anchor = 'Bottom, Left'
        $button.Tag = $Role
        return $button
    }

    # 按钮层级沿用 Win11 语义：每屏只有一个主操作（实心强调色），其余为白底描边，
    # 破坏性操作（删除）用红字。原来 8 个按钮各用一种饱和色，视觉上分不出轻重。
    $btnNew     = & $mkButton '新建'     2   436 62 32 'Primary'
    $btnCopy    = & $mkButton '复制'     70  436 62 32 'Secondary'
    $btnEdit    = & $mkButton '编辑'     138 436 62 32 'Secondary'
    $btnDelete  = & $mkButton '删除'     206 436 58 32 'Danger'
    $btnUp      = & $mkButton '上移'     2   474 62 32 'Secondary'
    $btnDown    = & $mkButton '下移'     70  474 62 32 'Secondary'
    $btnImport  = & $mkButton '导入配置' 138 474 126 32 'Secondary'

    # 虚拟网卡排除开关：默认不勾选（即排除 VMware / 蓝牙 / 环回 / 本地连接* 等网卡）。
    # 勾选后等价于把 $script:ExcludeVirtualAdapters 设为 $false，所有网卡都可能被匹配到。
    $chkIncludeVirtual = New-Object System.Windows.Forms.CheckBox
    $chkIncludeVirtual.Text = '包含虚拟网卡（VMware/蓝牙等）'
    $chkIncludeVirtual.Location = New-Object System.Drawing.Point(2, 506)
    $chkIncludeVirtual.Size = New-Object System.Drawing.Size(258, 22)
    $chkIncludeVirtual.Anchor = 'Bottom, Left'
    $chkIncludeVirtual.ForeColor = Get-UiColor 'TextMuted'
    $chkIncludeVirtual.Checked = (-not $script:ExcludeVirtualAdapters)

    $side.Controls.AddRange(@($lblSide, $lstProfiles, $btnNew, $btnCopy, $btnEdit, $btnDelete, $btnUp, $btnDown, $btnImport, $chkIncludeVirtual))

    # ===== 右侧 =====
    $right = New-Object System.Windows.Forms.Panel
    $right.Location = New-Object System.Drawing.Point(292, 82)
    $right.Size = New-Object System.Drawing.Size(870, 630)
    $right.Anchor = 'Top, Bottom, Left, Right'
    $right.BackColor = Get-UiColor 'Bg'

    # 当前状态
    $grpStatus = New-Object System.Windows.Forms.GroupBox
    $grpStatus.Text = '当前网络状态'
    $grpStatus.Location = New-Object System.Drawing.Point(0, 0)
    $grpStatus.Size = New-Object System.Drawing.Size(866, 226)
    $grpStatus.Anchor = 'Top, Left, Right'
    $txtStatus = New-Object System.Windows.Forms.TextBox
    $txtStatus.Multiline = $true; $txtStatus.ReadOnly = $true
    $txtStatus.ScrollBars = 'Vertical'
    $txtStatus.Location = New-Object System.Drawing.Point(14, 30)
    $txtStatus.Size = New-Object System.Drawing.Size(838, 184)
    $txtStatus.Anchor = 'Top, Bottom, Left, Right'
    $txtStatus.BorderStyle = 'None'
    $txtStatus.BackColor = Get-UiColor 'LogBg'
    $txtStatus.Font = New-MonoFont 9
    $grpStatus.Controls.Add($txtStatus)

    # 配置集详情
    $grpDetail = New-Object System.Windows.Forms.GroupBox
    $grpDetail.Text = '配置集内容'
    $grpDetail.Location = New-Object System.Drawing.Point(0, 234)
    $grpDetail.Size = New-Object System.Drawing.Size(866, 212)
    # 右侧四块的锚点分工（窗口变矮时谁让出高度）：
    #   状态卡片 固定高（内容要完整看得清）
    #   详情卡片 Top+Bottom —— 唯一响应高度变化的一块，表格多一行少一行都不影响阅读
    #   操作区   Bottom —— 钉在底部跟着日志走
    #   日志卡片 Bottom 固定高 —— 不能被压扁，否则日志等于看不见
    # 早先四块全是 Top 锚定，缩到最小尺寸时按钮行溢出、日志卡片塌陷成一条缝。
    $grpDetail.Anchor = 'Top, Bottom, Left, Right'
    $txtDetail = New-Object System.Windows.Forms.TextBox
    $txtDetail.Multiline = $true; $txtDetail.ReadOnly = $true
    $txtDetail.ScrollBars = 'Vertical'
    $txtDetail.Location = New-Object System.Drawing.Point(14, 30)
    $txtDetail.Size = New-Object System.Drawing.Size(838, 170)
    $txtDetail.Anchor = 'Top, Bottom, Left, Right'
    $txtDetail.BorderStyle = 'None'
    $txtDetail.BackColor = Get-UiColor 'LogBg'
    $txtDetail.Font = New-MonoFont 9
    $grpDetail.Controls.Add($txtDetail)

    # 操作区
    $action = New-Object System.Windows.Forms.Panel
    $action.Location = New-Object System.Drawing.Point(0, 454)
    $action.Size = New-Object System.Drawing.Size(866, 46)
    $action.Anchor = 'Bottom, Left, Right'
    $action.BackColor = Get-UiColor 'Bg'

    # 操作区共 8 个按钮，容器宽 866px，相邻间隔统一 6px：
    # 宽度合计 = 866 - 7*6 = 824
    #   X: 0(128) 134(116) 256(136) 398(88) 492(92) 590(88) 684(104) 794(72) -> 右边缘 866
    $btnApply = New-Object System.Windows.Forms.Button
    $btnApply.Text = '应用此配置集'
    $btnApply.Location = New-Object System.Drawing.Point(0, 5)
    $btnApply.Size = New-Object System.Drawing.Size(128, 36)
    $btnApply.Tag = 'Primary'

    $btnPreview = New-Object System.Windows.Forms.Button
    $btnPreview.Text = '预览此配置集'
    $btnPreview.Location = New-Object System.Drawing.Point(134, 5)
    $btnPreview.Size = New-Object System.Drawing.Size(116, 36)
    $btnPreview.Tag = 'Secondary'

    $btnShortcut = New-Object System.Windows.Forms.Button
    $btnShortcut.Text = '生成桌面快捷方式'
    $btnShortcut.Location = New-Object System.Drawing.Point(256, 5)
    $btnShortcut.Size = New-Object System.Drawing.Size(136, 36)
    $btnShortcut.Tag = 'Secondary'

    $btnRefresh = New-Object System.Windows.Forms.Button
    $btnRefresh.Text = '刷新状态'
    $btnRefresh.Location = New-Object System.Drawing.Point(398, 5)
    $btnRefresh.Size = New-Object System.Drawing.Size(88, 36)
    $btnRefresh.Tag = 'Secondary'

    $btnBackup = New-Object System.Windows.Forms.Button
    $btnBackup.Text = '备份记录'
    $btnBackup.Location = New-Object System.Drawing.Point(492, 5)
    $btnBackup.Size = New-Object System.Drawing.Size(92, 36)
    $btnBackup.Tag = 'Secondary'

    $btnExport = New-Object System.Windows.Forms.Button
    $btnExport.Text = '导出配置'
    $btnExport.Location = New-Object System.Drawing.Point(590, 5)
    $btnExport.Size = New-Object System.Drawing.Size(88, 36)
    $btnExport.Tag = 'Secondary'

    $btnOpenFile = New-Object System.Windows.Forms.Button
    $btnOpenFile.Text = '打开配置文件'
    $btnOpenFile.Location = New-Object System.Drawing.Point(684, 5)
    $btnOpenFile.Size = New-Object System.Drawing.Size(104, 36)
    $btnOpenFile.Tag = 'Secondary'

    $btnCopyLog = New-Object System.Windows.Forms.Button
    $btnCopyLog.Text = '复制日志'
    $btnCopyLog.Location = New-Object System.Drawing.Point(794, 5)
    $btnCopyLog.Size = New-Object System.Drawing.Size(72, 36)
    $btnCopyLog.Tag = 'Secondary'
    $btnCopyLog.Anchor = 'Top, Right'

    $action.Controls.AddRange(@($btnApply, $btnPreview, $btnShortcut, $btnRefresh, $btnBackup, $btnExport, $btnOpenFile, $btnCopyLog))

    # 日志
    $grpLog = New-Object System.Windows.Forms.GroupBox
    $grpLog.Text = '操作日志'
    $grpLog.Location = New-Object System.Drawing.Point(0, 508)
    $grpLog.Size = New-Object System.Drawing.Size(866, 118)
    $grpLog.Anchor = 'Bottom, Left, Right'
    $txtLog = New-Object System.Windows.Forms.RichTextBox
    $txtLog.Multiline = $true; $txtLog.ReadOnly = $true
    $txtLog.Location = New-Object System.Drawing.Point(14, 30)
    $txtLog.Size = New-Object System.Drawing.Size(838, 76)
    $txtLog.Anchor = 'Top, Bottom, Left, Right'
    $txtLog.BorderStyle = 'None'
    $txtLog.BackColor = Get-UiColor 'LogBg'
    $txtLog.ForeColor = Get-UiColor 'Text'
    $txtLog.Font = New-MonoFont 9
    $grpLog.Controls.Add($txtLog)

    $right.Controls.AddRange(@($grpStatus, $grpDetail, $action, $grpLog))
    $form.Controls.AddRange(@($header, $side, $right))

    $script:LogBox = $txtLog

    # 回填历史日志：程序启动快照早于本窗口创建，此时 LogBox 还是空的，
    # 若不回填，用户打开界面会看到「操作日志」一片空白，误以为什么都没记录。
    $backfill = Get-LogTail -Lines 300
    if (-not [string]::IsNullOrWhiteSpace($backfill)) {
        $txtLog.Text = $backfill
        $txtLog.SelectionStart = $txtLog.TextLength
        $txtLog.ScrollToCaret()
    }

    # 控件全部建好后再统一套用 Win11 主题：这样各处布局代码只关心坐标，
    # 圆角/配色/层级集中在这一处，将来调整风格不必再逐个控件改。
    # 主题失败不能让整个界面起不来，所以包一层 try。
    try {
        Invoke-UiTheme -Root $form
    } catch {
        Write-NSLog "界面主题应用失败（已回退为默认外观）：$($_.Exception.Message)" 'WARN'
    }

    # 日志框换成浅色底后，原来深色底上的浅灰字就看不见了，这里统一回正文色
    $txtLog.ForeColor = Get-UiColor 'Text'
    $txtLog.BackColor = Get-UiColor 'LogBg'

    # ===== 界面逻辑 =====

    $reloadProfiles = {
        param([int]$SelectIndex = -1)
        $lstProfiles.BeginUpdate()
        $lstProfiles.Items.Clear()
        foreach ($item in (ConvertTo-Array $script:ConfigData.profiles)) {
            [void]$lstProfiles.Items.Add([string]$item.name)
        }
        $lstProfiles.EndUpdate()
        if ($lstProfiles.Items.Count -gt 0) {
            $index = if ($SelectIndex -ge 0 -and $SelectIndex -lt $lstProfiles.Items.Count) { $SelectIndex } else { 0 }
            $lstProfiles.SelectedIndex = $index
        } else {
            $txtDetail.Text = '还没有任何配置集，点左下角「新建」开始。'
        }
    }

    $updateDetail = {
        if ($lstProfiles.SelectedIndex -lt 0) { return }
        $selected = Get-ProfileByName ([string]$lstProfiles.SelectedItem)
        $txtDetail.Text = Get-ProfileSummaryText $selected
    }

    $refreshStatus = {
        try {
            $txtStatus.Text = (Get-StatusText)
        } catch {
            $txtStatus.Text = "读取网络状态失败：$($_.Exception.Message)"
        }
    }

    $updateAdminLabel = {
        if (Test-IsAdmin) {
            $lblAdmin.Text = '● 已获得管理员权限'
            $lblAdmin.ForeColor = [System.Drawing.Color]::FromArgb(120, 220, 160)
            $btnElevate.Visible = $false
        } else {
            $lblAdmin.Text = '● 普通权限（只能编辑配置）'
            $lblAdmin.ForeColor = [System.Drawing.Color]::FromArgb(240, 170, 120)
            $btnElevate.Visible = $true
        }
    }

    $getAdapterNames = {
        return @(Get-AdapterList | ForEach-Object { $_.Name })
    }

    & $reloadProfiles
    & $updateAdminLabel
    & $updateDetail
    # 网络状态要查 WMI，冷启动约 1 秒。放在这里先占位，等窗口显示出来再填充，
    # 免得用户盯着白屏等状态刷新完。
    $txtStatus.Text = '正在读取网络状态…'

    Write-NSLog "界面已就绪：共 $(@($script:ConfigData.profiles).Count) 个配置集。新建/编辑后点「应用此配置集」即可切换网络。" 'OK'

    $lstProfiles.Add_SelectedIndexChanged($updateDetail)

    $lstProfiles.Add_DoubleClick({
        if ($lstProfiles.SelectedIndex -ge 0) { & $script:EditAction }
    })

    $btnElevate.Add_Click({
        Write-NSLog '正在请求管理员权限并重启程序…'
        # 新模式进程已拉起，本实例必须自行退出，否则会同时留下两个主窗口
        if (Start-Elevated) { $form.Close() }
    })

    $script:EditAction = {
        if ($lstProfiles.SelectedIndex -lt 0) {
            [System.Windows.Forms.MessageBox]::Show('请先选择一个配置集。', '提示', 'OK', 'Information') | Out-Null
            return
        }
        $original = Get-ProfileByName ([string]$lstProfiles.SelectedItem)
        $edited = Show-ProfileEditor -SourceProfile $original -AdapterNames (& $getAdapterNames)
        if ($null -ne $edited) {
            $index = 0
            $position = 0
            foreach ($item in (ConvertTo-Array $script:ConfigData.profiles)) {
                if ($item.name -eq $original.name) { $index = $position; break }
                $position++
            }
            $list = [System.Collections.ArrayList]@(ConvertTo-Array $script:ConfigData.profiles)
            $list[$index] = $edited
            $script:ConfigData.profiles = [object[]]$list
            Save-ConfigFile | Out-Null
            & $reloadProfiles $index
            Write-NSLog "配置集「$($edited.name)」已更新" 'OK'

            # 改了名字，桌面快捷方式里的旧名称就失效了，顺手同步
            if ($original.name -ne $edited.name) {
                Sync-ProfileShortcutOnRename -OldName ([string]$original.name) -NewName ([string]$edited.name)
            }
        }
    }

    $btnEdit.Add_Click({ & $script:EditAction })

    $btnNew.Add_Click({
        $created = Show-ProfileEditor -SourceProfile $null -AdapterNames (& $getAdapterNames)
        if ($null -ne $created) {
            $script:ConfigData.profiles = [object[]]@((ConvertTo-Array $script:ConfigData.profiles) + $created)
            Save-ConfigFile | Out-Null
            & $reloadProfiles ((ConvertTo-Array $script:ConfigData.profiles).Count - 1)
            Write-NSLog "已新建配置集「$($created.name)」" 'OK'
        }
    })

    $btnCopy.Add_Click({
        if ($lstProfiles.SelectedIndex -lt 0) { return }
        $source = Get-ProfileByName ([string]$lstProfiles.SelectedItem)
        $copy = [pscustomobject][ordered]@{
            name     = "$($source.name) - 副本"
            remark   = $source.remark
            adapters = ConvertTo-Array $source.adapters
        }
        $n = 2
        while ($null -ne (Get-ProfileByName $copy.name)) {
            $copy.name = "$($source.name) - 副本$n"
            $n++
        }
        $script:ConfigData.profiles = [object[]]@((ConvertTo-Array $script:ConfigData.profiles) + $copy)
        Save-ConfigFile | Out-Null
        & $reloadProfiles ((ConvertTo-Array $script:ConfigData.profiles).Count - 1)
        Write-NSLog "已复制为「$($copy.name)」" 'OK'
    })

    $btnDelete.Add_Click({
        if ($lstProfiles.SelectedIndex -lt 0) { return }
        $name = [string]$lstProfiles.SelectedItem
        $confirm = [System.Windows.Forms.MessageBox]::Show("确定删除配置集「$name」吗？", '确认删除', 'YesNo', 'Question')
        if ($confirm -ne 'Yes') { return }
        $remaining = @()
        foreach ($item in (ConvertTo-Array $script:ConfigData.profiles)) {
            if ($item.name -ne $name) { $remaining += $item }
        }
        $script:ConfigData.profiles = [object[]]$remaining
        Save-ConfigFile | Out-Null
        & $reloadProfiles
        Write-NSLog "已删除配置集「$name」" 'OK'
    })

    $moveProfile = {
        param([int]$Delta)
        if ($lstProfiles.SelectedIndex -lt 0) { return }
        $current = $lstProfiles.SelectedIndex
        $target = $current + $Delta
        if ($target -lt 0 -or $target -ge $lstProfiles.Items.Count) { return }
        $list = [System.Collections.ArrayList]@(ConvertTo-Array $script:ConfigData.profiles)
        $item = $list[$current]
        $list.RemoveAt($current)
        $list.Insert($target, $item)
        $script:ConfigData.profiles = [object[]]$list
        Save-ConfigFile | Out-Null
        & $reloadProfiles $target
    }

    $btnUp.Add_Click({ & $moveProfile -Delta -1 })
    $btnDown.Add_Click({ & $moveProfile -Delta 1 })

    $btnImport.Add_Click({
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        $dialog.Filter = 'JSON 配置|*.json|所有文件|*.*'
        $dialog.Title = '选择要导入的配置文件'
        if ($dialog.ShowDialog() -eq 'OK') {
            try {
                Import-ConfigFile -Path $dialog.FileName | Out-Null
                Save-ConfigFile | Out-Null
                & $reloadProfiles
                Write-NSLog "已从 $($dialog.FileName) 导入配置" 'OK'
            } catch {
                Write-NSLog "导入失败：$($_.Exception.Message)" 'ERROR'
            }
        }
    })

    $chkIncludeVirtual.Add_CheckedChanged({
        $script:ExcludeVirtualAdapters = (-not $chkIncludeVirtual.Checked)
        if ($script:ExcludeVirtualAdapters) {
            Write-NSLog '已排除虚拟网卡：VMware / VirtualBox / Hyper-V / 蓝牙 / 环回 / 本地连接* 等不会被配置集匹配。' 'INFO'
        } else {
            Write-NSLog '已取消虚拟网卡排除：所有网卡都可能被「*」之类的通配匹配到，请确认后再应用。' 'WARN'
        }
        & $refreshStatus
    })

    $btnBackup.Add_Click({
        if (Show-BackupManager) { & $reloadProfiles }
    })

    $btnExport.Add_Click({
        $dialog = New-Object System.Windows.Forms.SaveFileDialog
        $dialog.Filter = 'JSON 配置|*.json'
        $dialog.Title = '导出配置文件'
        $dialog.FileName = "NetSwitch-config-$(Get-Date -Format 'yyyyMMdd-HHmm').json"
        if ($dialog.ShowDialog() -eq 'OK') {
            Save-ConfigFile -Path $dialog.FileName | Out-Null
            Write-NSLog "配置已导出到 $($dialog.FileName)" 'OK'
        }
    })

    $btnOpenFile.Add_Click({
        if (Test-Path $script:ConfigFile) {
            Start-Process notepad -ArgumentList "`"$($script:ConfigFile)`"" -ErrorAction SilentlyContinue
        } else {
            Write-NSLog '配置文件尚不存在，请先新建一个配置集。' 'WARN'
        }
    })

    # 把日志复制出来，用户可以直接粘贴给开发者排查问题
    $btnCopyLog.Add_Click({
        try {
            $text = Get-LogTail -Lines 300
            if ([string]::IsNullOrWhiteSpace($text)) {
                Write-NSLog '暂无日志内容可复制。' 'WARN'
                return
            }
            [System.Windows.Forms.Clipboard]::SetText($text)
            Write-NSLog '日志最近 300 行已复制到剪贴板，可直接粘贴给开发者排查。' 'OK'
        } catch {
            Write-NSLog "复制日志失败：$($_.Exception.Message)" 'ERROR'
        }
    })

    $btnRefresh.Add_Click({
        & $refreshStatus
        Write-NSLog '已刷新网络状态'
    })

    $btnApply.Add_Click({
        if ($lstProfiles.SelectedIndex -lt 0) {
            [System.Windows.Forms.MessageBox]::Show('请先选择一个配置集。', '提示', 'OK', 'Information') | Out-Null
            return
        }
        $name = [string]$lstProfiles.SelectedItem
        if (-not (Test-IsAdmin)) {
            $answer = [System.Windows.Forms.MessageBox]::Show(
                "应用配置需要管理员权限。`n是否现在切换到管理员模式运行？",
                '需要管理员权限', 'YesNo', 'Question')
            if ($answer -eq 'Yes') {
                if (Start-Elevated) { $form.Close() }
                return
            }
            Write-NSLog '未获得管理员权限，已取消应用。' 'WARN'
            return
        }
        # 动手之前先把实际会命中哪几块网卡列出来，让用户确认，避免通配匹配误伤其它网卡
        $target = Get-ProfileByName $name
        $impact = Get-ProfileImpact $target
        if ($impact.Count -eq 0) {
            Write-NSLog "配置集「$name」匹配到 0 块网卡。" 'WARN'
            $rebind = [System.Windows.Forms.MessageBox]::Show(
                "配置集「$name」没有匹配到任何网卡。可能是网卡在系统里被改过名。`n`n是否现在重新绑定到本机的一块网卡？",
                '未匹配到网卡', 'YesNo', 'Question')
            if ($rebind -ne 'Yes') {
                Write-NSLog '已取消切换。' 'INFO'
                return
            }
            if (-not (Show-AdapterRebindDialog -Profile $target)) {
                Write-NSLog '未重新绑定网卡，已取消切换。' 'WARN'
                return
            }
            # 重绑成功后配置已落盘：重新载入配置、重算命中范围，让这一次点击直接走完「重绑 + 应用」
            [void](Import-ConfigFile -Path $script:ConfigFile)
            $target = Get-ProfileByName $name
            $impact = Get-ProfileImpact $target
            if ($impact.Count -eq 0) {
                Write-NSLog "重新绑定后配置集「$name」仍未匹配到任何网卡，已中止应用。" 'WARN'
                [System.Windows.Forms.MessageBox]::Show(
                    "配置集「$name」没有匹配到任何可操作的网卡，已中止。`n`n可能原因：`n  1) 配置集里的网卡条目全部处于「停用」状态；`n  2) 网卡匹配名写错，本机不存在同名网卡；`n  3) 目标网卡被虚拟网卡排除规则挡掉了（可在主界面左下角勾选「包含虚拟网卡」后再试）。",
                    '无法应用', 'OK', 'Warning') | Out-Null
                return
            }
        }

        $confirm = [System.Windows.Forms.MessageBox]::Show(
            "即将切换到配置集：「$name」`n`n本次将影响以下 $($impact.Count) 块网卡：`n$($impact.Text)`n切换过程中网络会短暂断开，是否继续？",
            '确认切换', 'YesNo', 'Question')
        if ($confirm -ne 'Yes') { Write-NSLog '已取消切换。' 'INFO'; return }

        $form.Cursor = 'WaitCursor'
        try {
            Invoke-Profile -ProfileName $name | Out-Null
        } finally {
            $form.Cursor = 'Default'
            & $refreshStatus
        }
    })

    # 预览：调用 Invoke-Profile -DryRun（只写日志、不碰网络），再把命中范围弹给用户
    $btnPreview.Add_Click({
        if ($lstProfiles.SelectedIndex -lt 0) {
            [System.Windows.Forms.MessageBox]::Show('请先选择一个配置集。', '提示', 'OK', 'Information') | Out-Null
            return
        }
        $name = [string]$lstProfiles.SelectedItem
        $target = Get-ProfileByName $name
        if ($null -eq $target) {
            Write-NSLog "找不到配置集「$name」，无法预览。" 'ERROR'
            return
        }

        Write-NSLog "预览配置集「$name」（不会修改任何网络设置）" 'INFO'
        [void](Invoke-Profile -ProfileName $name -DryRun)

        $impact = Get-ProfileImpact $target
        if ($impact.Count -eq 0) {
            Write-NSLog "预览结果：「$name」没有匹配到任何网卡。" 'WARN'
            $rebind = [System.Windows.Forms.MessageBox]::Show(
                "配置集「$name」没有匹配到任何网卡。可能是网卡在系统里被改过名。`n`n是否现在重新绑定到本机的一块网卡？",
                '未匹配到网卡', 'YesNo', 'Question')
            if ($rebind -eq 'Yes') {
                # 预览只是重绑后重算一次命中范围，本身不做任何网络修改
                if (Show-AdapterRebindDialog -Profile $target) {
                    [void](Import-ConfigFile -Path $script:ConfigFile)
                    $target = Get-ProfileByName $name
                    if ($null -eq $target) {
                        Write-NSLog "重新载入配置后找不到配置集「$name」，无法预览。" 'ERROR'
                        return
                    }
                    [void](Invoke-Profile -ProfileName $name -DryRun)
                    $impact = Get-ProfileImpact $target
                }
            }
            if ($impact.Count -eq 0) {
                [System.Windows.Forms.MessageBox]::Show(
                    "配置集「$name」没有匹配到任何可操作的网卡。`n`n请检查网卡匹配名，或在主界面左下角勾选「包含虚拟网卡」。",
                    '预览结果', 'OK', 'Warning') | Out-Null
                return
            }
        }

        Write-NSLog "预览结果：「$name」将影响 $($impact.Count) 块网卡。" 'OK'
        [System.Windows.Forms.MessageBox]::Show(
            "配置集「$name」将影响以下 $($impact.Count) 块网卡（预览，未做任何修改）：`n$($impact.Text)",
            '预览结果', 'OK', 'Information') | Out-Null
    })

    $btnShortcut.Add_Click({
        if ($lstProfiles.SelectedIndex -lt 0) { return }
        $name = [string]$lstProfiles.SelectedItem
        try {
            $linkPath = New-ProfileShortcut -Name $name
            Write-NSLog "桌面快捷方式已创建：$linkPath（双击即可切换到「$name」）" 'OK'
        } catch {
            Write-NSLog "创建快捷方式失败：$($_.Exception.Message)" 'ERROR'
        }
    })

    $form.Add_Shown({
        $form.Activate()
        # 窗口已经画出来了，这时才去读网络状态
        & $refreshStatus
        # 界面稳住之后，把双击 .bat 时挂在后面的黑窗口收掉
        Hide-ConsoleWindow
    })
    [void]$form.ShowDialog()
}

# ============================== 程序入口 ==============================

$cliRequested = $false
foreach ($key in @('Apply', 'ListProfiles', 'ListAdapters', 'ShowStatus', 'ImportFile', 'ExportFile')) {
    if ($PSBoundParameters.ContainsKey($key)) {
        $value = $PSBoundParameters[$key]
        if ($value -is [switch]) { if ($value) { $cliRequested = $true } }
        elseif (-not [string]::IsNullOrWhiteSpace([string]$value)) { $cliRequested = $true }
    }
}

try {
    Assert-Environment
} catch {
    Initialize-Logging
    Write-NSLog "环境检查失败：$($_.Exception.Message)" 'ERROR'
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "详细信息已记录到日志文件：$($script:LogFile)" -ForegroundColor Yellow
    if (-not $cliRequested) {
        [System.Reflection.Assembly]::LoadWithPartialName('System.Windows.Forms') | Out-Null
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, '环境不支持', 'OK', 'Error') | Out-Null
    }
    exit 1
}

# 确定配置文件路径（命令行与图形界面共用），然后启动日志
if ($PSBoundParameters.ContainsKey('ConfigPath') -and $ConfigPath) {
    $script:ConfigFile = $ConfigPath
} else {
    $script:ConfigFile = Get-DefaultConfigPath
}
Initialize-Logging

if ($cliRequested) {
    $script:IsCliMode = $true
    Invoke-Cli
    exit 0
}

# 图形界面模式
# 声明高 DPI 感知 + 算出统一放大倍数。必须在 Show-MainForm 之前、任何窗口创建之前完成。
Initialize-UiScale

if (-not (Test-Path $script:ConfigFile)) {
    $script:ConfigData = Repair-ConfigData (New-DefaultData)
    Save-ConfigFile | Out-Null
} else {
    Import-ConfigFile -Path $script:ConfigFile | Out-Null
}

try {
    Show-MainForm
} catch {
    $detail = $_.Exception.Message
    Write-NSLog "界面启动失败：$detail" 'ERROR'
    try { Write-LogFileLine ($_.ScriptStackTrace) } catch { }
    Write-Host "界面启动失败：$detail" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '请确认：1) 系统为 Windows 8 / Server 2012 及以上；2) 使用 Windows PowerShell 运行（PowerShell 7 亦可）；3) 脚本未被另存为 ANSI 编码。' -ForegroundColor Yellow
    Write-Host '也可以先试用命令行模式：NetSwitch.ps1 -ListProfiles' -ForegroundColor Yellow
    if ($script:LogFile) { Write-Host "错误详情已写入日志文件：$($script:LogFile)" -ForegroundColor Yellow }
    exit 1
}