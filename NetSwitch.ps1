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

    if ($ForwardArgs.Count -eq 0) {
        $ForwardArgs = @()
        foreach ($key in $PSBoundParameters.Keys) {
            if ($key -eq 'Elevated') { continue }
            $ForwardArgs += "-$key"
            $value = $PSBoundParameters[$key]
            if ($value -is [switch]) { continue }
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

    $Data.updatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $json = $Data | ConvertTo-Json -Depth 12
    # 带 BOM 写入，保证 Windows 记事本打开不乱码
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($true)))
    Write-NSLog "配置已保存到 $Path" 'OK'
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

function Get-AdapterList {
    try {
        return @(Get-NetAdapter -ErrorAction Stop | Sort-Object { $_.InterfaceIndex })
    } catch {
        Write-NSLog "读取网卡列表失败：$($_.Exception.Message)" 'ERROR'
        return @()
    }
}

function Find-MatchedAdapters {
    param([string]$Pattern)

    $all = Get-AdapterList
    if ([string]::IsNullOrWhiteSpace($Pattern) -or $Pattern -eq '*') { return $all }

    $exact = @($all | Where-Object { $_.Name -eq $Pattern })
    if ($exact.Count -gt 0) { return $exact }

    $fuzzy = @($all | Where-Object { $_.Name -like "*$Pattern*" })
    if ($fuzzy.Count -gt 0) { return $fuzzy }

    Write-NSLog "未找到匹配「$Pattern」的网卡，已跳过该项配置。" 'WARN'
    return @()
}

# ============================== 核心：切换引擎 ==============================

function Reset-InterfaceIPv4 {
    param([int]$InterfaceIndex)

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
        # ipconfig /renew 需要网卡名称，配合 -ErrorAction 静默失败即可（多数情况下系统会自动完成续租）
        Start-Process -FilePath 'ipconfig' -ArgumentList "/renew", "`"$name`"" -WindowStyle Hidden -ErrorAction SilentlyContinue
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

        $prefix = ConvertTo-PrefixLength $Config.mask
        Set-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -Dhcp Disabled -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 150

        $gateways = ConvertTo-GatewayEntry (($Config.gateways -join ';'))
        if ($gateways.Count -eq 1 -and $null -eq $gateways[0].Metric) {
            New-NetIPAddress -InterfaceIndex $index -IPAddress $ipAddress -PrefixLength $prefix `
                             -DefaultGateway $gateways[0].Address -ErrorAction Stop | Out-Null
        } else {
            New-NetIPAddress -InterfaceIndex $index -IPAddress $ipAddress -PrefixLength $prefix -ErrorAction Stop | Out-Null
            $step = 0
            foreach ($gateway in $gateways) {
                $metric = if ($null -ne $gateway.Metric) { $gateway.Metric } else { 10 + $step }
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
        if ($dnsList.Count -gt 0)  { $summary += "  DNS=$($dnsList -join ',')" }
        Write-NSLog $summary 'OK'
    }

    # 附加静态路由（可选）
    foreach ($routeText in (ConvertTo-StringList $Config.routes)) {
        foreach ($route in (ConvertTo-RouteEntry $routeText)) {
            Remove-NetRoute -DestinationPrefix $route.Destination -NextHop $route.NextHop -Confirm:$false -ErrorAction SilentlyContinue
            try {
                New-NetRoute -InterfaceIndex $index -DestinationPrefix $route.Destination -NextHop $route.NextHop -ErrorAction Stop | Out-Null
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

    $target = $null
    foreach ($item in (ConvertTo-Array $script:ConfigData.profiles)) {
        if ($item.name -eq $ProfileName) { $target = $item; break }
    }
    if ($null -eq $target) {
        Write-NSLog "找不到配置集「$ProfileName」" 'ERROR'
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

function Get-StatusText {
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
                $line = "      [$($ad.adapter)] $modeText"
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
            exit 1
        }

        $result = Invoke-Profile -ProfileName $Apply
        Write-Host "`n切换后的网络状态：`n" -ForegroundColor Cyan
        Write-Host (Get-StatusText)
        if (-not $Silent) {
            Write-Host '按任意键结束…' -ForegroundColor DarkGray
            [void]$Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        }
        if (-not $result) { exit 1 }
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

function New-FormFont {
    param([single]$Size = 9, [string]$Style = 'Regular')
    try {
        return (New-Object System.Drawing.Font('Microsoft YaHei UI', $Size, [System.Drawing.FontStyle]$Style))
    } catch {
        return (New-Object System.Drawing.Font('Microsoft Sans Serif', $Size, [System.Drawing.FontStyle]$Style))
    }
}

function New-MonoFont {
    param([single]$Size = 9)
    return (New-Object System.Drawing.Font('Consolas', $Size))
}

function Get-ProfileByName {
    param([string]$Name)
    foreach ($item in (ConvertTo-Array $script:ConfigData.profiles)) {
        if ($item.name -eq $Name) { return $item }
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
        [void]$builder.AppendLine("[匹配网卡] $($ad.adapter)    $flag")
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

# ---------- 配置集编辑器 ----------

function Show-ProfileEditor {
    param(
        $SourceProfile,          # $null 表示新建
        [string[]]$AdapterNames
    )

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
    $lblTip.Text = '提示：网关可写为 192.168.1.1:10 指定跃点数，多个网关用分号隔开；DNS 多个用分号隔开；附加路由写法 10.0.0.0/8>网关'
    $lblTip.Location = New-Object System.Drawing.Point(14, 82); $lblTip.Size = New-Object System.Drawing.Size(950, 20)
    $lblTip.ForeColor = [System.Drawing.Color]::DimGray

    # 表格
    $table = New-Object System.Data.DataTable
    [void]$table.Columns.Add('Enabled', [bool])
    [void]$table.Columns.Add('Adapter', [string])
    [void]$table.Columns.Add('Mode', [string])
    [void]$table.Columns.Add('IP', [string])
    [void]$table.Columns.Add('Mask', [string])
    [void]$table.Columns.Add('Gateways', [string])
    [void]$table.Columns.Add('DNS', [string])
    [void]$table.Columns.Add('Routes', [string])
    $table.Columns['Enabled'].DefaultValue = $true
    $table.Columns['Mode'].DefaultValue    = '静态IP'
    $table.Columns['Mask'].DefaultValue    = '255.255.255.0'

    if ($null -ne $SourceProfile) {
        foreach ($ad in (ConvertTo-Array $SourceProfile.adapters)) {
            $row = $table.NewRow()
            $row['Enabled']  = [bool]$ad.enabled
            $row['Adapter']  = [string]$ad.adapter
            $row['Mode']     = if ($ad.mode -eq 'dhcp') { 'DHCP自动' } else { '静态IP' }
            $row['IP']       = [string]$ad.ip
            $row['Mask']     = [string]$ad.mask
            $row['Gateways'] = ((ConvertTo-StringList $ad.gateways) -join ';')
            $row['DNS']      = ((ConvertTo-StringList $ad.dns) -join ';')
            $row['Routes']   = ((ConvertTo-StringList $ad.routes) -join ';')
            [void]$table.Rows.Add($row)
        }
    }

    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Location = New-Object System.Drawing.Point(14, 106)
    $grid.Size = New-Object System.Drawing.Size(980, 400)
    $grid.Anchor = 'Top, Bottom, Left, Right'
    $grid.AllowUserToAddRows = $false
    $grid.AllowUserToDeleteRows = $false
    $grid.AutoSizeColumnsMode = 'Fill'
    $grid.SelectionMode = 'FullRowSelect'
    $grid.RowHeadersVisible = $false
    $grid.MultiSelect = $false
    $grid.BackgroundColor = [System.Drawing.Color]::White
    $grid.BorderStyle = 'FixedSingle'

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

    $colDns = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colDns.Name = 'DNS'; $colDns.DataPropertyName = 'DNS'
    $colDns.HeaderText = 'DNS 服务器'; $colDns.FillWeight = 54

    $colRoutes = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colRoutes.Name = 'Routes'; $colRoutes.DataPropertyName = 'Routes'
    $colRoutes.HeaderText = '附加路由(可选)'; $colRoutes.FillWeight = 62

    foreach ($col in @($colEnabled, $colAdapter, $colMode, $colIp, $colMask, $colGw, $colDns, $colRoutes)) {
        [void]$grid.Columns.Add($col)
    }

    $grid.DataSource = $table

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
        $newRow['DNS']      = ''
        $newRow['Routes']   = ''
        [void]$table.Rows.Add($newRow)
    }

    $btnAddRow.Add_Click($addFromCombo)

    $btnAddWildcard.Add_Click({
        $newRow = $table.NewRow()
        $newRow['Enabled']  = $true
        $newRow['Adapter']  = '*'
        $newRow['Mode']     = 'DHCP自动'
        $newRow['IP']       = ''
        $newRow['Mask']     = '255.255.255.0'
        $newRow['Gateways'] = ''
        $newRow['DNS']      = ''
        $newRow['Routes']   = ''
        [void]$table.Rows.Add($newRow)
    })

    $btnRemoveRow.Add_Click({
        if ($grid.CurrentRow -and -not $grid.CurrentRow.IsNewRow) {
            $grid.Rows.RemoveAt($grid.CurrentRow.Index)
        }
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
                adapter  = $adapterName.Trim()
                enabled  = [bool]$row['Enabled']
                mode     = if ($modeText -eq 'DHCP自动') { 'dhcp' } else { 'static' }
                ip       = ([string]$row['IP']).Trim()
                mask     = ([string]$row['Mask']).Trim()
                gateways = @(Split-ItemList ([string]$row['Gateways']))
                dns      = $dnsArray
                dhcpDns  = ($dnsArray.Count -eq 0)
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
        $grid, $btnSave, $btnCancel
    ))

    $script:EditorResult = $null
    $dialog = $form.ShowDialog()
    if ($dialog -eq 'OK') { return $script:EditorResult }
    return $null
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
    $form.MinimumSize = New-Object System.Drawing.Size(1020, 660)
    $form.StartPosition = 'CenterScreen'
    $form.Font = New-FormFont 9

    # ===== 顶部 =====
    $header = New-Object System.Windows.Forms.Panel
    $header.Location = New-Object System.Drawing.Point(0, 0)
    $header.Size = New-Object System.Drawing.Size(1180, 70)
    $header.Dock = 'None'
    $header.Anchor = 'Top, Left, Right'
    $header.BackColor = [System.Drawing.Color]::FromArgb(43, 54, 70)

    $lblTitle = New-Object System.Windows.Forms.Label
    $lblTitle.Text = 'NetSwitch  内网 IP / 网关 / DNS 切换器'
    $lblTitle.ForeColor = [System.Drawing.Color]::White
    $lblTitle.Font = New-FormFont 15 Bold
    $lblTitle.Location = New-Object System.Drawing.Point(20, 14)
    $lblTitle.Size = New-Object System.Drawing.Size(520, 30)

    $lblConfig = New-Object System.Windows.Forms.Label
    $lblConfig.Text = "配置文件：$($script:ConfigFile)"
    $lblConfig.ForeColor = [System.Drawing.Color]::FromArgb(170, 180, 195)
    $lblConfig.Font = New-FormFont 8
    $lblConfig.Location = New-Object System.Drawing.Point(22, 46)
    $lblConfig.Size = New-Object System.Drawing.Size(620, 18)

    $lblAdmin = New-Object System.Windows.Forms.Label
    $lblAdmin.Location = New-Object System.Drawing.Point(700, 18)
    $lblAdmin.Size = New-Object System.Drawing.Size(300, 26)
    $lblAdmin.TextAlign = 'MiddleRight'
    $lblAdmin.Font = New-FormFont 9 Bold
    $lblAdmin.ForeColor = [System.Drawing.Color]::White

    $btnElevate = New-Object System.Windows.Forms.Button
    $btnElevate.Location = New-Object System.Drawing.Point(1010, 18)
    $btnElevate.Size = New-Object System.Drawing.Size(150, 34)
    $btnElevate.Anchor = 'Top, Right'
    $btnElevate.Text = '提权并重启'
    $btnElevate.FlatStyle = 'Flat'
    $btnElevate.BackColor = [System.Drawing.Color]::FromArgb(200, 130, 40)
    $btnElevate.ForeColor = [System.Drawing.Color]::White
    $btnElevate.Font = New-FormFont 9 Bold
    $btnElevate.Visible = $false

    $header.Controls.AddRange(@($lblTitle, $lblConfig, $lblAdmin, $btnElevate))

    # ===== 左侧 =====
    $side = New-Object System.Windows.Forms.Panel
    $side.Location = New-Object System.Drawing.Point(12, 82)
    $side.Size = New-Object System.Drawing.Size(268, 630)
    $side.Anchor = 'Top, Bottom, Left'

    $lblSide = New-Object System.Windows.Forms.Label
    $lblSide.Text = '配置集列表'; $lblSide.Location = New-Object System.Drawing.Point(2, 0); $lblSide.Size = New-Object System.Drawing.Size(200, 20)
    $lblSide.Font = New-FormFont 9 Bold
    $lblSide.ForeColor = [System.Drawing.Color]::FromArgb(60, 72, 90)

    $lstProfiles = New-Object System.Windows.Forms.ListBox
    $lstProfiles.Location = New-Object System.Drawing.Point(2, 24)
    $lstProfiles.Size = New-Object System.Drawing.Size(262, 400)
    $lstProfiles.Anchor = 'Top, Bottom, Left, Right'
    $lstProfiles.Font = New-FormFont 10
    $lstProfiles.DrawMode = 'OwnerDrawFixed'
    $lstProfiles.ItemHeight = 26
    $lstProfiles.BorderStyle = 'FixedSingle'

    $lstProfiles.Add_DrawItem({
        param($sender, $e)
        if ($e.Index -lt 0) { return }
        $e.DrawBackground()
        $text = [string]$lstProfiles.Items[$e.Index]
        $selected = (($e.State -band [System.Windows.Forms.DrawItemState]::Selected) -ne 0)
        $backColor = if ($selected) { [System.Drawing.Color]::FromArgb(52, 152, 219) } else { [System.Drawing.Color]::White }
        $foreColor = if ($selected) { [System.Drawing.Color]::White } else { [System.Drawing.Color]::FromArgb(45, 55, 70) }
        $brush = New-Object System.Drawing.SolidBrush($backColor)
        $e.Graphics.FillRectangle($brush, $e.Bounds)
        $brush.Dispose()
        $font = New-FormFont 10
        $textBrush = New-Object System.Drawing.SolidBrush($foreColor)
        $textRect = New-Object System.Drawing.RectangleF(10, ($e.Bounds.Y + 4), ($e.Bounds.Width - 10), 20)
        $e.Graphics.DrawString($text, $font, $textBrush, $textRect)
        $textBrush.Dispose()
        $font.Dispose()
        $e.DrawFocusRectangle()
    })

    $mkButton = {
        param($Text, $X, $Y, $W, $H, $Color)
        $button = New-Object System.Windows.Forms.Button
        $button.Text = $Text
        $button.Location = New-Object System.Drawing.Point($X, $Y)
        $button.Size = New-Object System.Drawing.Size($W, $H)
        $button.FlatStyle = 'Flat'
        $button.Anchor = 'Bottom, Left'
        $button.BackColor = $Color
        $button.ForeColor = [System.Drawing.Color]::White
        $button.Font = New-FormFont 9
        return $button
    }

    $gray  = [System.Drawing.Color]::FromArgb(110, 122, 140)
    $blue  = [System.Drawing.Color]::FromArgb(52, 120, 190)
    $green = [System.Drawing.Color]::FromArgb(40, 150, 110)
    $red   = [System.Drawing.Color]::FromArgb(190, 80, 80)

    $btnNew     = & $mkButton '新建'  2    432 62 30 $green
    $btnCopy    = & $mkButton '复制'  70   432 62 30 $gray
    $btnEdit    = & $mkButton '编辑'  138  432 62 30 $blue
    $btnDelete  = & $mkButton '删除'  206  432 58 30 $red
    $btnUp      = & $mkButton '上移'  2    468 62 30 $gray
    $btnDown    = & $mkButton '下移'  70   468 62 30 $gray
    $btnImport  = & $mkButton '导入配置' 138 468 126 30 $gray

    $side.Controls.AddRange(@($lblSide, $lstProfiles, $btnNew, $btnCopy, $btnEdit, $btnDelete, $btnUp, $btnDown, $btnImport))

    # ===== 右侧 =====
    $right = New-Object System.Windows.Forms.Panel
    $right.Location = New-Object System.Drawing.Point(292, 82)
    $right.Size = New-Object System.Drawing.Size(870, 630)
    $right.Anchor = 'Top, Bottom, Left, Right'

    # 当前状态
    $grpStatus = New-Object System.Windows.Forms.GroupBox
    $grpStatus.Text = '当前网络状态'
    $grpStatus.Location = New-Object System.Drawing.Point(0, 0)
    $grpStatus.Size = New-Object System.Drawing.Size(866, 226)
    $grpStatus.Anchor = 'Top, Left, Right'
    $txtStatus = New-Object System.Windows.Forms.TextBox
    $txtStatus.Multiline = $true; $txtStatus.ReadOnly = $true
    $txtStatus.ScrollBars = 'Vertical'
    $txtStatus.Location = New-Object System.Drawing.Point(10, 22)
    $txtStatus.Size = New-Object System.Drawing.Size(846, 196)
    $txtStatus.Anchor = 'Top, Bottom, Left, Right'
    $txtStatus.BorderStyle = 'None'
    $txtStatus.BackColor = [System.Drawing.Color]::FromArgb(250, 250, 250)
    $txtStatus.Font = New-MonoFont 9
    $grpStatus.Controls.Add($txtStatus)

    # 配置集详情
    $grpDetail = New-Object System.Windows.Forms.GroupBox
    $grpDetail.Text = '配置集内容'
    $grpDetail.Location = New-Object System.Drawing.Point(0, 234)
    $grpDetail.Size = New-Object System.Drawing.Size(866, 212)
    $grpDetail.Anchor = 'Top, Left, Right'
    $txtDetail = New-Object System.Windows.Forms.TextBox
    $txtDetail.Multiline = $true; $txtDetail.ReadOnly = $true
    $txtDetail.ScrollBars = 'Vertical'
    $txtDetail.Location = New-Object System.Drawing.Point(10, 22)
    $txtDetail.Size = New-Object System.Drawing.Size(846, 182)
    $txtDetail.Anchor = 'Top, Bottom, Left, Right'
    $txtDetail.BorderStyle = 'None'
    $txtDetail.BackColor = [System.Drawing.Color]::FromArgb(250, 250, 250)
    $txtDetail.Font = New-MonoFont 9
    $grpDetail.Controls.Add($txtDetail)

    # 操作区
    $action = New-Object System.Windows.Forms.Panel
    $action.Location = New-Object System.Drawing.Point(0, 454)
    $action.Size = New-Object System.Drawing.Size(866, 46)
    $action.Anchor = 'Top, Left, Right'

    $btnApply = New-Object System.Windows.Forms.Button
    $btnApply.Text = '应用此配置集'
    $btnApply.Location = New-Object System.Drawing.Point(0, 6)
    $btnApply.Size = New-Object System.Drawing.Size(170, 36)
    $btnApply.BackColor = [System.Drawing.Color]::SeaGreen
    $btnApply.ForeColor = [System.Drawing.Color]::White
    $btnApply.FlatStyle = 'Flat'
    $btnApply.Font = New-FormFont 10 Bold

    $btnShortcut = New-Object System.Windows.Forms.Button
    $btnShortcut.Text = '生成桌面快捷方式'
    $btnShortcut.Location = New-Object System.Drawing.Point(180, 6)
    $btnShortcut.Size = New-Object System.Drawing.Size(160, 36)
    $btnShortcut.FlatStyle = 'Flat'
    $btnShortcut.BackColor = [System.Drawing.Color]::FromArgb(70, 130, 180)
    $btnShortcut.ForeColor = [System.Drawing.Color]::White

    $btnRefresh = New-Object System.Windows.Forms.Button
    $btnRefresh.Text = '刷新状态'
    $btnRefresh.Location = New-Object System.Drawing.Point(350, 6)
    $btnRefresh.Size = New-Object System.Drawing.Size(120, 36)
    $btnRefresh.FlatStyle = 'Flat'
    $btnRefresh.BackColor = $gray
    $btnRefresh.ForeColor = [System.Drawing.Color]::White

    $btnExport = New-Object System.Windows.Forms.Button
    $btnExport.Text = '导出配置'
    $btnExport.Location = New-Object System.Drawing.Point(480, 6)
    $btnExport.Size = New-Object System.Drawing.Size(120, 36)
    $btnExport.FlatStyle = 'Flat'
    $btnExport.BackColor = $gray
    $btnExport.ForeColor = [System.Drawing.Color]::White

    $btnOpenFile = New-Object System.Windows.Forms.Button
    $btnOpenFile.Text = '打开配置文件'
    $btnOpenFile.Location = New-Object System.Drawing.Point(610, 6)
    $btnOpenFile.Size = New-Object System.Drawing.Size(130, 36)
    $btnOpenFile.FlatStyle = 'Flat'
    $btnOpenFile.BackColor = $gray
    $btnOpenFile.ForeColor = [System.Drawing.Color]::White

    $btnCopyLog = New-Object System.Windows.Forms.Button
    $btnCopyLog.Text = '复制日志'
    $btnCopyLog.Location = New-Object System.Drawing.Point(750, 6)
    $btnCopyLog.Size = New-Object System.Drawing.Size(116, 36)
    $btnCopyLog.FlatStyle = 'Flat'
    $btnCopyLog.BackColor = [System.Drawing.Color]::FromArgb(90, 100, 120)
    $btnCopyLog.ForeColor = [System.Drawing.Color]::White
    $btnCopyLog.Anchor = 'Top, Right'

    $action.Controls.AddRange(@($btnApply, $btnShortcut, $btnRefresh, $btnExport, $btnOpenFile, $btnCopyLog))

    # 日志
    $grpLog = New-Object System.Windows.Forms.GroupBox
    $grpLog.Text = '操作日志'
    $grpLog.Location = New-Object System.Drawing.Point(0, 508)
    $grpLog.Size = New-Object System.Drawing.Size(866, 118)
    $grpLog.Anchor = 'Top, Bottom, Left, Right'
    $txtLog = New-Object System.Windows.Forms.RichTextBox
    $txtLog.Multiline = $true; $txtLog.ReadOnly = $true
    $txtLog.Location = New-Object System.Drawing.Point(10, 22)
    $txtLog.Size = New-Object System.Drawing.Size(846, 88)
    $txtLog.Anchor = 'Top, Bottom, Left, Right'
    $txtLog.BorderStyle = 'None'
    $txtLog.BackColor = [System.Drawing.Color]::FromArgb(32, 36, 42)
    $txtLog.ForeColor = [System.Drawing.Color]::FromArgb(220, 225, 232)
    $txtLog.Font = New-MonoFont 9
    $grpLog.Controls.Add($txtLog)

    $right.Controls.AddRange(@($grpStatus, $grpDetail, $action, $grpLog))
    $form.Controls.AddRange(@($header, $side, $right))

    $script:LogBox = $txtLog

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
    & $refreshStatus
    & $updateAdminLabel
    & $updateDetail

    $lstProfiles.Add_SelectedIndexChanged($updateDetail)

    $lstProfiles.Add_DoubleClick({
        if ($lstProfiles.SelectedIndex -ge 0) { & $script:EditAction }
    })

    $btnElevate.Add_Click({
        Write-NSLog '正在请求管理员权限并重启程序…'
        $null = Start-Elevated
    })

    $script:EditAction = {
        if ($lstProfiles.SelectedIndex -lt 0) {
            [System.Windows.Forms.MessageBox]::Show('请先选择一个配置集。', '提示', 'OK', 'Information') | Out-Null
            return
        }
        $original = Get-ProfileByName ([string]$lstProfiles.SelectedItem)
        $edited = Show-ProfileEditor -Profile $original -AdapterNames (& $getAdapterNames)
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
        }
    }

    $btnEdit.Add_Click({ & $script:EditAction })

    $btnNew.Add_Click({
        $created = Show-ProfileEditor -Profile $null -AdapterNames (& $getAdapterNames)
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
            if ($answer -eq 'Yes') { Start-Elevated | Out-Null; return }
            Write-NSLog '未获得管理员权限，已取消应用。' 'WARN'
            return
        }
        $confirm = [System.Windows.Forms.MessageBox]::Show(
            "即将切换到配置集：「$name」`n切换过程中网络会短暂断开，是否继续？",
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

    $btnShortcut.Add_Click({
        if ($lstProfiles.SelectedIndex -lt 0) { return }
        $name = [string]$lstProfiles.SelectedItem
        try {
            $shell = New-Object -ComObject WScript.Shell
            $desktop = $shell.SpecialFolders('Desktop')
            $safeName = ($name -replace '[\\/:*?"<>|]', '_')
            $linkPath = Join-Path $desktop "NetSwitch-$safeName.lnk"
            $shortcut = $shell.CreateShortcut($linkPath)
            $shortcut.TargetPath = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
            $shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`" -Apply `"$name`""
            $shortcut.WorkingDirectory = $PSScriptRoot
            $shortcut.Description = "一键切换到 [$name]"
            $shortcut.IconLocation = 'netshell.dll,18'
            $shortcut.Save()

            # 让快捷方式以管理员身份启动
            $bytes = [System.IO.File]::ReadAllBytes($linkPath)
            $bytes[0x15] = ($bytes[0x15] -bor 0x20)
            [System.IO.File]::WriteAllBytes($linkPath, $bytes)

            Write-NSLog "桌面快捷方式已创建：$linkPath（双击即可切换到「$name」）" 'OK'
        } catch {
            Write-NSLog "创建快捷方式失败：$($_.Exception.Message)" 'ERROR'
        }
    })

    $form.Add_Shown({ $form.Activate() })
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