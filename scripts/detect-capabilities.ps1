[CmdletBinding()]
param(
    [string]$OutFile = ""
)

$ErrorActionPreference = "SilentlyContinue"
$dataRoot = Join-Path $env:LOCALAPPDATA "LocalDeck"
if ([string]::IsNullOrWhiteSpace($OutFile)) {
    $OutFile = Join-Path $dataRoot "capabilities.json"
}

function Test-AnyPath([string[]]$Paths) {
    foreach ($path in $Paths) {
        if (Test-Path -LiteralPath $path) {
            return $true
        }
    }
    return $false
}

$os = Get-CimInstance Win32_OperatingSystem
$board = Get-CimInstance Win32_BaseBoard | Select-Object -First 1
$computer = Get-CimInstance Win32_ComputerSystem | Select-Object -First 1
$video = @(Get-CimInstance Win32_VideoController)
$nvidia = $video | Where-Object {
    $_.AdapterCompatibility -match "NVIDIA" -or $_.Name -match "NVIDIA"
} | Select-Object -First 1
$nvidiaRtx = $null -ne $nvidia -and $nvidia.Name -match "RTX"
$webView = Test-AnyPath @(
    "${env:ProgramFiles(x86)}\Microsoft\EdgeWebView\Application",
    "$env:ProgramFiles\Microsoft\EdgeWebView\Application"
)
$localDeckInstall = Join-Path $env:LOCALAPPDATA "Programs\LocalDeck"
$hysteria = Test-Path -LiteralPath (Join-Path $localDeckInstall "hysteria.exe")
$proxyProfile = Test-Path -LiteralPath (Join-Path $dataRoot "profiles\active.yaml")
$listener = Get-NetTCPConnection -State Listen -LocalPort 7897 | Select-Object -First 1
$proxySetting = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings"
$logitech = @(Get-CimInstance Win32_PnPEntity | Where-Object {
    $_.PNPDeviceID -match "VID_046D"
})
$llamaEngine = Get-ChildItem -LiteralPath (Join-Path $dataRoot "engines") `
    -Filter "llama-server.exe" -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1

$capabilities = @(
    [pscustomobject]@{
        id = "windows"
        name = "Windows desktop APIs"
        status = if ($os) { "supported" } else { "unavailable" }
        detail = if ($os) { "$($os.Caption) build $($os.BuildNumber)" } else { "Windows OS query failed" }
    },
    [pscustomobject]@{
        id = "webview2"
        name = "WebView2 UI"
        status = if ($webView) { "supported" } else { "unavailable" }
        detail = "Microsoft Edge WebView2 Runtime"
    },
    [pscustomobject]@{
        id = "traffic-forwarding"
        name = "Local HTTP/SOCKS5 forwarding"
        status = if ($hysteria) { "supported" } else { "unavailable" }
        detail = if ($listener) { "Listening on 127.0.0.1:7897" } else { "Listener is not currently running" }
    },
    [pscustomobject]@{
        id = "proxy-profile"
        name = "User proxy profile"
        status = if ($proxyProfile) { "supported" } else { "on-demand" }
        detail = "User-provided YAML; credentials are never included in this report"
    },
    [pscustomobject]@{
        id = "nvidia"
        name = "NVIDIA GPU"
        status = if ($nvidiaRtx) { "supported" } elseif ($nvidia) { "limited" } else { "unavailable" }
        detail = if ($nvidia) { $nvidia.Name } else { "No NVIDIA adapter detected" }
    },
    [pscustomobject]@{
        id = "board-control"
        name = "Motherboard vendor control"
        status = if ($board) { "limited" } else { "unavailable" }
        detail = if ($board) {
            "$($board.Manufacturer) $($board.Product); vendor write access is validated at runtime"
        } else { "Baseboard identity unavailable" }
    },
    [pscustomobject]@{
        id = "logitech"
        name = "Logitech HID++"
        status = if ($logitech.Count -gt 0) { "supported" } else { "unavailable" }
        detail = "$($logitech.Count) compatible HID device(s)"
    },
    [pscustomobject]@{
        id = "local-ai"
        name = "Local model engine"
        status = if ($llamaEngine) { "supported" } else { "on-demand" }
        detail = if ($llamaEngine) { "llama.cpp engine detected" } else { "Engine/model resources can be added later" }
    }
)

$plugins = @(
    [pscustomobject]@{
        id = "hardware"
        name = "Hardware control"
        supported = $true
        notes = "Displays, board, devices, drivers and GPU tools"
    },
    [pscustomobject]@{
        id = "system-tools"
        name = "System maintenance"
        supported = $true
        notes = "Uninstaller, disk analysis and event logs"
    },
    [pscustomobject]@{
        id = "automation"
        name = "Automation"
        supported = $true
        notes = "Macros, side buttons and scheduled-task management"
    },
    [pscustomobject]@{
        id = "ai-workbench"
        name = "AI workbench"
        supported = $true
        notes = "Local models and cloud-model shortcuts"
    },
    [pscustomobject]@{
        id = "game-enhancements"
        name = "GPU and DLSS"
        supported = $nvidiaRtx
        notes = if ($nvidiaRtx) { "NVIDIA RTX detected" } else { "Requires an NVIDIA RTX GPU" }
    }
)

$systemInfo = [pscustomobject]@{
    manufacturer = $computer.Manufacturer
    model = $computer.Model
    os = $os.Caption
    build = $os.BuildNumber
    architecture = $os.OSArchitecture
}
$systemProxyEnabled = Convert.ToInt32($proxySetting.ProxyEnable) -ne 0
$localListenerActive = $null -ne $listener
$proxySummary = [pscustomobject]@{
    windowsSystemProxyEnabled = $systemProxyEnabled
    localListenerActive = $localListenerActive
}
$detectionReport = [pscustomobject]@{
    schemaVersion = 1
    generatedAt = (Get-Date).ToString("o")
    privacy = "No username, hostname, serial number, UUID, proxy address, subscription URL or credential."
    system = $systemInfo
    capabilities = @($capabilities)
    plugins = @($plugins)
    localProxy = $proxySummary
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
$json = ConvertTo-Json -InputObject $detectionReport -Depth 8
if ([string]::IsNullOrWhiteSpace($json) -or $json.Trim() -eq "null") {
    throw "Capability report serialization returned no data."
}
$json | Set-Content -LiteralPath $OutFile -Encoding UTF8

$capabilities |
    Select-Object name,status,detail |
    Format-Table -AutoSize

Write-Host ""
Write-Host "Capability report written to: $OutFile"
Write-Host "Run LocalDeck and open the plugin center to enable optional packs."
