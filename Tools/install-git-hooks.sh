#!/usr/bin/env bash
# Points git at the versioned hooks in tools/git-hooks (run once per clone).
set -euo pipefail
cd "$(dirname "$0")/.."
git config core.hooksPath "$(git rev-parse --show-prefix)tools/git-hooks"
echo "git hooks installed (core.hooksPath = $(git config core.hooksPath))"
