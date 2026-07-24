# Install Claude Approvals on this Windows machine. Idempotent — safe to re-run.
#
#   1. copy ClaudeApprovals.exe        -> %LOCALAPPDATA%\Programs\ClaudeApprovals
#   2. copy hook scripts               -> %USERPROFILE%\.claude\hooks\claude-approvals
#   3. ensure a shared token           -> %APPDATA%\ClaudeApprovals\token
#   4. MERGE hook registration         -> %USERPROFILE%\.claude\settings.json
#      (timestamped backup; additive-only: existing hooks are never touched)
#   5. autostart via HKCU Run key, start the app, health-check
#
# Run from an unpacked release folder (containing ClaudeApprovals.exe, hooks\,
# installer\) or a repo checkout after `dotnet publish`.
#
#   powershell -ExecutionPolicy Bypass -File installer\install.ps1

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $PSScriptRoot                  # repo/release root
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeApprovals'
$hooksDir = Join-Path $env:USERPROFILE '.claude\hooks\claude-approvals'
$cfgDir = Join-Path $env:APPDATA 'ClaudeApprovals'
$settingsPath = Join-Path $env:USERPROFILE '.claude\settings.json'

# --- 1. locate the exe ------------------------------------------------------
$exeCandidates = @(
    (Join-Path $here 'ClaudeApprovals.exe'),
    (Join-Path $here 'src\App\bin\Release\net8.0-windows\win-x64\publish\ClaudeApprovals.exe')
)
$exeSrc = $exeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exeSrc) {
    Write-Error "ClaudeApprovals.exe not found. Download a release zip or run 'dotnet publish src/App -c Release -r win-x64'."
}

New-Item -ItemType Directory -Force -Path $appDir, $hooksDir, $cfgDir | Out-Null

Write-Host "==> installing app -> $appDir"
# Stop a running instance so the copy succeeds.
Get-Process ClaudeApprovals -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
Copy-Item $exeSrc (Join-Path $appDir 'ClaudeApprovals.exe') -Force

Write-Host "==> installing hooks -> $hooksDir"
Copy-Item (Join-Path $here 'hooks\permission.ps1') $hooksDir -Force
Copy-Item (Join-Path $here 'hooks\notify.ps1') $hooksDir -Force

# --- 3. shared token --------------------------------------------------------
$tokenFile = Join-Path $cfgDir 'token'
if (-not (Test-Path $tokenFile) -or -not (Get-Content -Raw $tokenFile -ErrorAction SilentlyContinue).Trim()) {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    [Convert]::ToBase64String($bytes) | Set-Content -NoNewline $tokenFile
    Write-Host "==> generated shared token"
} else {
    Write-Host "==> token already present"
}

# --- 4. merge hooks into settings.json (backup + additive) ------------------
Write-Host "==> merging hooks into $settingsPath"
$permCmd = Join-Path $hooksDir 'permission.ps1'
$notifyCmd = Join-Path $hooksDir 'notify.ps1'

$settings = $null
if (Test-Path $settingsPath) {
    Copy-Item $settingsPath "$settingsPath.bak.$(Get-Date -Format yyyyMMdd-HHmmss)"
    $settings = Get-Content -Raw $settingsPath | ConvertFrom-Json
}
if (-not $settings) { $settings = New-Object PSObject }

if (-not ($settings.PSObject.Properties.Name -contains 'hooks')) {
    $settings | Add-Member -MemberType NoteProperty -Name hooks -Value (New-Object PSObject)
}

function Test-HasOurHook($eventEntries) {
    foreach ($entry in @($eventEntries)) {
        foreach ($h in @($entry.hooks)) {
            if ($h.command -like '*claude-approvals*') { return $true }
        }
    }
    return $false
}

function Add-HookEvent($eventName, $matcher, $command, $timeout) {
    $hooksObj = $script:settings.hooks
    $existing = if ($hooksObj.PSObject.Properties.Name -contains $eventName) { $hooksObj.$eventName } else { @() }
    if (Test-HasOurHook $existing) { return }
    $hookDef = [ordered]@{ type = 'command'; command = $command }
    if ($timeout) { $hookDef.timeout = $timeout }
    $newEntry = New-Object PSObject -Property ([ordered]@{
        matcher = $matcher
        hooks = @(New-Object PSObject -Property $hookDef)
    })
    $merged = @($existing) + @($newEntry)
    if ($hooksObj.PSObject.Properties.Name -contains $eventName) {
        $hooksObj.$eventName = $merged
    } else {
        $hooksObj | Add-Member -MemberType NoteProperty -Name $eventName -Value $merged
    }
}

Add-HookEvent 'PermissionRequest' '*' $permCmd 600
Add-HookEvent 'Stop' '' $notifyCmd $null

# Validate by re-parsing before overwriting the real file.
$json = $settings | ConvertTo-Json -Depth 16
$null = $json | ConvertFrom-Json
$tmp = "$settingsPath.tmp.$PID"
New-Item -ItemType Directory -Force -Path (Split-Path $settingsPath) | Out-Null
$json | Set-Content -Encoding UTF8 $tmp
Move-Item $tmp $settingsPath -Force
Write-Host "    hooks registered (PermissionRequest timeout=600, Stop)"

# --- 5. autostart + launch + health ----------------------------------------
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'ClaudeApprovals' -Value ('"' + (Join-Path $appDir 'ClaudeApprovals.exe') + '"')
Write-Host "==> autostart registered (HKCU Run)"

Start-Process (Join-Path $appDir 'ClaudeApprovals.exe')
Start-Sleep -Seconds 2
try {
    $health = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:8790/v1/health" -TimeoutSec 3
    Write-Host "==> app is up: $($health.Content)"
} catch {
    Write-Warning "app not answering yet on :8790 — it may still be starting."
}

Write-Host ""
Write-Host "Done. RESTART any running Claude Code sessions to pick up the hooks."
Write-Host "Uninstall: installer\uninstall.ps1"
