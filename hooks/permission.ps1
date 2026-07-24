# Blocking permission hook for Claude Code (Windows). Forwards the hook's stdin
# JSON to the Claude Approvals app and echoes the app's decision JSON on stdout.
# FAIL-OPEN: if the app is unreachable, times out, or returns nothing, exit 0
# with no output -> Claude Code falls back to its normal terminal prompt.
# PowerShell 5.1-compatible. Registered with "timeout": 600.

$ErrorActionPreference = 'Stop'
$port = if ($env:CLAUDE_APPROVALS_PORT) { $env:CLAUDE_APPROVALS_PORT } else { 8790 }
$tokenFile = Join-Path $env:APPDATA 'ClaudeApprovals\token'
$token = ''
if (Test-Path $tokenFile) {
    try { $token = (Get-Content -Raw $tokenFile).Trim() } catch { $token = '' }
}

try {
    $body = [Console]::In.ReadToEnd()
    $resp = Invoke-WebRequest -UseBasicParsing -Method Post `
        -Uri "http://127.0.0.1:$port/v1/permission" `
        -ContentType 'application/json; charset=utf-8' `
        -Headers @{ 'X-Notch-Token' = $token } `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) `
        -TimeoutSec 590
    if ($resp.Content -and $resp.Content.Length -gt 0) {
        [Console]::Out.Write($resp.Content)
    }
} catch {
    # fail-open: no output, success exit
}
exit 0
