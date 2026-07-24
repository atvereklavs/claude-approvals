# Claude Approvals (Windows)

An interactive approval pop-up for [Claude Code](https://claude.com/claude-code)
on Windows. When Claude needs a permission decision — run a command, edit a
file, ask you a question, approve a plan — a card slides down from the **top
center of your screen** with everything you need to decide:

- the **full command**, or a **red/green diff** for file edits
- the working directory and Claude's stated reason
- **questions rendered as real forms** (pick an option or type your own answer)
- **plans rendered readably**, with Approve & build / Reject

Click **Allow / Session / Always / Deny** (or press `Ctrl+Alt+Y` / `N` / `U`)
and the decision flows back into Claude Code instantly — without the pop-up
ever stealing keyboard focus from your editor.

**Fail-open by design:** if the app isn't running, Claude Code behaves exactly
as it does today (normal terminal prompt). The app can only ever make things
faster, never block you.

## Install

1. Download `ClaudeApprovals-win-x64.zip` from
   [Releases](../../releases), and unzip it.
   > Windows SmartScreen may warn because the exe is unsigned:
   > **More info → Run anyway.**
2. In the unzipped folder:
   ```powershell
   powershell -ExecutionPolicy Bypass -File installer\install.ps1
   ```
3. **Restart any running Claude Code sessions** (hooks load at session start).

That's it — a shield icon appears in your system tray, and the next time Claude
asks for permission, the card appears top-center.

## What the installer does

- App → `%LOCALAPPDATA%\Programs\ClaudeApprovals`
- Hook scripts → `%USERPROFILE%\.claude\hooks\claude-approvals\`
- Registers the hooks in `%USERPROFILE%\.claude\settings.json`
  (**backup taken; your existing hooks are never touched**)
- Auth token + config → `%APPDATA%\ClaudeApprovals\`
- Autostart via HKCU Run key

Uninstall (reverses everything): `installer\uninstall.ps1` (add `-Purge` to
also delete your rules + decision log).

## The decision buttons

| Button | Effect |
|---|---|
| **Allow** | approve this one call |
| **Session** | approve + auto-approve matching calls for the rest of this session |
| **Always** | approve + **persist** a rule for this project (tool + command prefix / file) |
| **Deny** | block; Claude is told it was denied |
| **✕** | close with no answer — Claude falls back to its terminal prompt |

Every decision (including auto-approvals from your rules) is recorded in
`%APPDATA%\ClaudeApprovals\decisions.jsonl` — tray menu → *Open decision log*.

## Architecture

```
Claude Code ──PermissionRequest hook──▶ hooks\permission.ps1
    └─ POST http://127.0.0.1:8790/v1/permission (long-poll) ─▶ ClaudeApprovals.exe
         ├─ top-center pop-up → you decide (or a saved rule auto-decides)
         └─ decision JSON ─▶ hook stdout ─▶ Claude Code
```

- `src/Core` — all logic (.NET 8, cross-platform, fully unit-tested)
- `src/App` — thin WPF shell (popup + tray)
- `src/Headless` — console host for development on non-Windows machines

The hook protocol is byte-compatible with the macOS sibling of this project.

## Development

```bash
dotnet test tests/Core.Tests          # runs on any OS
dotnet run --project src/Headless     # protocol server without UI (any OS)
dotnet publish src/App -c Release -r win-x64   # Windows only / CI
```

CI (`windows-latest`) runs the tests, checks the PowerShell scripts, and
publishes the zip on every tag (`v*`).
