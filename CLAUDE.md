# CLAUDE.md — claude-approvals

Interactive approval pop-up for Claude Code on Windows. Cross-platform Core
(.NET 8, no UI deps) + thin WPF shell (built on Windows CI only).

## Invariants (do not break)

- **Fail-open**: if the app is dead, slow, or unreachable, the hook scripts emit
  nothing and exit 0 → Claude Code falls back to its normal terminal prompt.
  The app is an enhancement, never a gate.
- **Wire compatibility**: the hook request/response JSON must stay byte-
  compatible with Claude Code's PermissionRequest consumer (see README →
  protocol). Any change here needs a protocol test in `tests/Core.Tests`.
- **All logic lives in `src/Core`** (testable on any OS). `src/App` (WPF) stays
  a thin shell — it cannot be built or tested on macOS/Linux.
- **No new dependencies** without strong justification (stdlib + xUnit only).

## Conventions

- Conventional Commits (`feat:`, `fix:`, `docs:`, `test:`, `chore:`, `ci:`).
- Commit trailer when Claude authors: `Co-Authored-By: Claude <noreply@anthropic.com>`
- `dotnet test tests/Core.Tests` must be green before any push.
- The WPF project is validated by the `windows-latest` CI workflow; never merge
  a change that breaks it.
