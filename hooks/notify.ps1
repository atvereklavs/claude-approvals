# Fire-and-forget notification hook (Stop etc.) for Claude Approvals (Windows).
# FAIL-OPEN and fast: short timeout, always exits 0.
$ErrorActionPreference = 'Stop'
$port = if ($env:CLAUDE_APPROVALS_PORT) { $env:CLAUDE_APPROVALS_PORT } else { 8790 }
$tokenFile = Join-Path $env:APPDATA 'ClaudeApprovals\token'
$token = ''
if (Test-Path $tokenFile) {
    try { $token = (Get-Content -Raw $tokenFile).Trim() } catch { $token = '' }
}

try {
    $body = [Console]::In.ReadToEnd()
    Invoke-WebRequest -UseBasicParsing -Method Post `
        -Uri "http://127.0.0.1:$port/v1/notify" `
        -ContentType 'application/json; charset=utf-8' `
        -Headers @{ 'X-Notch-Token' = $token } `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) `
        -TimeoutSec 3 | Out-Null
} catch { }
exit 0
