# CI test for the settings.json merge/unmerge semantics used by install.ps1 /
# uninstall.ps1: additive (existing hooks preserved), idempotent, and a clean
# uninstall round-trip. Runs entirely on temp files.
#
# NOTE: keeps a copy of the merge/filter logic in sync with install.ps1 /
# uninstall.ps1 — if you change one, change the other.

$ErrorActionPreference = 'Stop'
$failed = $false
function Assert($cond, $name) {
    if ($cond) { Write-Host "  PASS $name" }
    else { Write-Host "  FAIL $name"; $script:failed = $true }
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "ca-merge-$PID"
New-Item -ItemType Directory -Force $tmp | Out-Null
$settingsPath = Join-Path $tmp 'settings.json'

# Existing settings with a foreign hook that MUST survive untouched.
@'
{
  "effortLevel": "high",
  "hooks": {
    "Stop": [ { "matcher": "", "hooks": [ { "type": "command", "command": "C:\\other\\log.ps1" } ] } ]
  }
}
'@ | Set-Content -Encoding UTF8 $settingsPath

$permCmd = 'C:\Users\x\.claude\hooks\claude-approvals\permission.ps1'
$notifyCmd = 'C:\Users\x\.claude\hooks\claude-approvals\notify.ps1'

function Test-HasOurHook($eventEntries) {
    foreach ($entry in @($eventEntries)) {
        foreach ($h in @($entry.hooks)) {
            if ($h.command -like '*claude-approvals*') { return $true }
        }
    }
    return $false
}

function Merge($path) {
    $settings = Get-Content -Raw $path | ConvertFrom-Json
    if (-not ($settings.PSObject.Properties.Name -contains 'hooks')) {
        $settings | Add-Member -MemberType NoteProperty -Name hooks -Value (New-Object PSObject)
    }
    $hooksObj = $settings.hooks
    foreach ($spec in @(
        @{ Name = 'PermissionRequest'; Matcher = '*'; Cmd = $permCmd; Timeout = 600 },
        @{ Name = 'Stop'; Matcher = ''; Cmd = $notifyCmd; Timeout = $null },
        @{ Name = 'Notification'; Matcher = ''; Cmd = $notifyCmd; Timeout = $null },
        @{ Name = 'SessionStart'; Matcher = ''; Cmd = $notifyCmd; Timeout = $null },
        @{ Name = 'UserPromptSubmit'; Matcher = ''; Cmd = $notifyCmd; Timeout = $null },
        @{ Name = 'SessionEnd'; Matcher = ''; Cmd = $notifyCmd; Timeout = $null }
    )) {
        $eventName = $spec.Name
        $existing = if ($hooksObj.PSObject.Properties.Name -contains $eventName) { $hooksObj.$eventName } else { @() }
        if (Test-HasOurHook $existing) { continue }
        $hookDef = [ordered]@{ type = 'command'; command = $spec.Cmd }
        if ($spec.Timeout) { $hookDef.timeout = $spec.Timeout }
        $newEntry = New-Object PSObject -Property ([ordered]@{
            matcher = $spec.Matcher
            hooks = @(New-Object PSObject -Property $hookDef)
        })
        $merged = @($existing) + @($newEntry)
        if ($hooksObj.PSObject.Properties.Name -contains $eventName) { $hooksObj.$eventName = $merged }
        else { $hooksObj | Add-Member -MemberType NoteProperty -Name $eventName -Value $merged }
    }
    $json = $settings | ConvertTo-Json -Depth 16
    $null = $json | ConvertFrom-Json
    $json | Set-Content -Encoding UTF8 $path
}

function Unmerge($path) {
    $settings = Get-Content -Raw $path | ConvertFrom-Json
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
    ($settings | ConvertTo-Json -Depth 16) | Set-Content -Encoding UTF8 $path
}

# --- 1. merge adds our hooks, preserves the foreign Stop hook ---------------
Merge $settingsPath
$s1 = Get-Content -Raw $settingsPath | ConvertFrom-Json
Assert (@($s1.hooks.PermissionRequest).Count -eq 1) 'PermissionRequest added'
Assert ($s1.hooks.PermissionRequest[0].hooks[0].timeout -eq 600) 'timeout=600 set'
Assert (@($s1.hooks.Stop).Count -eq 2) 'Stop appended (foreign + ours)'
Assert (@($s1.hooks.Notification).Count -eq 1) 'Notification added'
Assert (@($s1.hooks.SessionStart).Count -eq 1) 'SessionStart added'
Assert (@($s1.hooks.UserPromptSubmit).Count -eq 1) 'UserPromptSubmit added'
Assert (@($s1.hooks.SessionEnd).Count -eq 1) 'SessionEnd added'
Assert ($s1.hooks.Stop[0].hooks[0].command -eq 'C:\other\log.ps1') 'foreign hook untouched'

# --- 2. idempotent ----------------------------------------------------------
Merge $settingsPath
$s2 = Get-Content -Raw $settingsPath | ConvertFrom-Json
Assert (@($s2.hooks.PermissionRequest).Count -eq 1) 'merge is idempotent (PermissionRequest)'
Assert (@($s2.hooks.Stop).Count -eq 2) 'merge is idempotent (Stop)'

# --- 3. unmerge restores the original shape ---------------------------------
Unmerge $settingsPath
$s3 = Get-Content -Raw $settingsPath | ConvertFrom-Json
Assert (-not ($s3.hooks.PSObject.Properties.Name -contains 'PermissionRequest')) 'PermissionRequest removed'
Assert (@($s3.hooks.Stop).Count -eq 1) 'foreign Stop hook survives uninstall'
Assert ($s3.effortLevel -eq 'high') 'unrelated settings preserved'

# --- 4. merge into settings with no hooks key -------------------------------
'{"permissions":{"allow":[]}}' | Set-Content -Encoding UTF8 $settingsPath
Merge $settingsPath
$s4 = Get-Content -Raw $settingsPath | ConvertFrom-Json
Assert (@($s4.hooks.PermissionRequest).Count -eq 1) 'merge into hook-less settings'

Remove-Item -Recurse -Force $tmp
if ($failed) { Write-Host 'MERGE TEST FAILED'; exit 1 }
Write-Host 'merge test passed'
