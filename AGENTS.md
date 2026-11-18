# AGENTS.md

All rules for AI agents live in **[CLAUDE.md](CLAUDE.md)** (single source of truth); rule details in
[docs/RULES.md](docs/RULES.md). This file exists for tools that look for `AGENTS.md` (Codex, Cursor, Copilot, ...).

Tools without Claude Code hooks still get checked by the git pre-commit hook (`./tools/install-git-hooks.sh`)
and by CI. Run these yourself:
```bash
./tools/test-gate.sh check      # after each edit (milliseconds)
./tools/test-gate.sh run        # definition of done (exit 0); exit 4 = stop and report
./tools/test-gate.sh graph feature <F>   # architecture questions: query, don't read other features' code
```
System map (generated): docs/architecture/overview.md
