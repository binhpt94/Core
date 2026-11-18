#!/usr/bin/env bash
# Points git at the versioned hooks in Tools/GitHooks (run once per clone).
set -euo pipefail
cd "$(dirname "$0")/.."
git config core.hooksPath "$(git rev-parse --show-prefix)Tools/GitHooks"
echo "git hooks installed (core.hooksPath = $(git config core.hooksPath))"
