# Acceptance checklist (first Windows tester)

You're testing the first Windows build. The app was developed on macOS against a
fully-tested core; **the UI layer has never run on a real Windows machine** —
your job is to find where it breaks. Please go through these in order and note
anything odd (screenshots help).

Setup: install per README, then **restart your Claude Code session**.

## Basics
- [ ] Tray icon (shield) appears after install; `Ctrl+Alt`-free right-click menu opens.
- [ ] `curl.exe http://127.0.0.1:8790/v1/health` → `{"ok":true,...}`.

## Permission flow
- [ ] Ask Claude to run a command (e.g. *"run `echo hello` in bash"*).
      A card appears **top-center** with the full command + working dir.
- [ ] Click **Allow** → the command runs in the terminal.
- [ ] **Focus check:** with your editor focused, approve via mouse — then type
      immediately. No keystrokes lost, editor never lost focus.
- [ ] Trigger another permission, click **Deny** → Claude reports it was denied.
- [ ] Trigger another, click **✕** → the normal terminal prompt appears instead.
- [ ] Hotkeys: `Ctrl+Alt+Y` approves, `Ctrl+Alt+N` denies, `Ctrl+Alt+U` dismisses.

## Rich content
- [ ] Ask Claude to edit a file → the card shows a red/green diff.
- [ ] Ask Claude something that makes it use AskUserQuestion (e.g. *"ask me
      whether I prefer tabs or spaces using your question tool"*) → options are
      clickable, free-text field works, **Send** delivers your answer and
      Claude proceeds with it.
- [ ] In plan mode, when Claude presents a plan → readable plan text,
      **Approve & build** exits plan mode.

## Rules
- [ ] Approve a `git …` command with **Always**, restart the app (tray → Quit,
      relaunch), trigger another `git …` in the same project → auto-approved,
      no card.
- [ ] Tray → *Open decision log* → shows your decisions incl. the auto-approval.

## Fail-open (important)
- [ ] Tray → Quit. Trigger a permission in Claude Code → the **normal terminal
      prompt** appears after ~1s (nothing hangs).
- [ ] Relaunch the app from `%LOCALAPPDATA%\Programs\ClaudeApprovals`.

## Edge cases
- [ ] Two Claude sessions asking at once → cards queue with a “+1” badge; each
      answer reaches the right session.
- [ ] Ctrl-C a session while its card is up → the card disappears on its own.
- [ ] Multi-monitor: the card appears on your primary display, sanely placed.
- [ ] High-DPI / display scaling ≠ 100%: text is crisp, not clipped.

## Uninstall
- [ ] `installer\uninstall.ps1` → hooks gone from settings.json (compare with
      the `.bak.*` backup), sessions restart clean, no tray icon.
