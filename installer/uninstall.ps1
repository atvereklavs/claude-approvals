# Remove Claude Approvals: app process/exe, autostart, hook scripts, and our
# hook registrations (existing hooks are preserved; backup taken first).
#   powershell -ExecutionPolicy Bypass -File installer\uninstall.ps1 [-Purge]
param([switch]$Purge)

$ErrorActionPreference = 'Stop'
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\ClaudeApprovals'
$hooksDir = Join-Path $env:USERPROFILE '.claude\hooks\claude-approvals'
$cfgDir = Join-Path $env:APPDATA 'ClaudeApprovals'
$settingsPath = Join-Path $env:USERPROFILE '.claude\settings.json'

Get-Process ClaudeApprovals -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'ClaudeApprovals' -ErrorAction SilentlyContinue

if (Test-Path $settingsPath) {
    Write-Host "==> removing our hooks from $settingsPath"
    Copy-Item $settingsPath "$settingsPath.bak.$(Get-Date -Format yyyyMMdd-HHmmss)"
    $settings = Get-Content -Raw $settingsPath | ConvertFrom-Json
    if ($settings.PSObject.Properties.Name -contains 'hooks') {
        $hooksObj = $settings.hooks
        foreach ($eventName in @($hooksObj.PSObject.Properties.Name)) {
            $kept = @()
            foreach ($entry in @($hooksObj.$eventName)) {
                $ours = $false
                foreach ($h in @($entry.hooks)) {
                    if ($h.command -like '*claude-approvals*') { $ours = $true }
                }
                if (-not $ours) { $kept += $entry }
            }
            if ($kept.Count -gt 0) { $hooksObj.$eventName = $kept }
            else { $hooksObj.PSObject.Properties.Remove($eventName) }
        }
        if (@($hooksObj.PSObject.Properties.Name).Count -eq 0) {
            $settings.PSObject.Properties.Remove('hooks')
        }
    }
    $json = $settings | ConvertTo-Json -Depth 16
    $null = $json | ConvertFrom-Json
    $json | Set-Content -Encoding UTF8 $settingsPath
}

Remove-Item -Recurse -Force $hooksDir -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $appDir -ErrorAction SilentlyContinue

if ($Purge) {
    Remove-Item -Recurse -Force $cfgDir -ErrorAction SilentlyContinue
    Write-Host "==> purged config (token, rules, decision log)"
} else {
    Write-Host "    (kept $cfgDir — pass -Purge to remove)"
}

Write-Host "Done. Restart Claude Code sessions to drop the hooks fully."
