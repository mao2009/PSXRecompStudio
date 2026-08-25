#!/bin/bash
# PSXRecompStudio - Git Hooks Setup Script
# Enables local Git hooks from .githooks/ directory
# Run from repository root: ./scripts/setup-git-hooks.sh

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null)"
if [ -z "$REPO_ROOT" ]; then
    echo "❌ Not inside a Git repository"
    exit 1
fi

HOOKS_DIR="$REPO_ROOT/.githooks"
if [ ! -d "$HOOKS_DIR" ]; then
    echo "❌ Hooks directory not found: $HOOKS_DIR"
    echo "   Expected .githooks/ with pre-commit and pre-push"
    exit 1
fi

if [ ! -f "$HOOKS_DIR/pre-commit" ] || [ ! -f "$HOOKS_DIR/pre-push" ]; then
    echo "❌ Required hooks not found in $HOOKS_DIR"
    echo "   Missing: pre-commit and/or pre-push"
    exit 1
fi

CURRENT_HOOKS_PATH="$(git config --get core.hooksPath 2>/dev/null || echo "")"
EXPECTED_HOOKS_PATH=".githooks"

if [ "$CURRENT_HOOKS_PATH" = "$EXPECTED_HOOKS_PATH" ]; then
    echo "✅ Git hooks already configured: core.hooksPath = $EXPECTED_HOOKS_PATH"
    echo "   Hooks directory: $HOOKS_DIR"
    exit 0
fi

echo "🔧 Configuring Git hooks..."
git config core.hooksPath "$EXPECTED_HOOKS_PATH"

NEW_HOOKS_PATH="$(git config --get core.hooksPath)"
if [ "$NEW_HOOKS_PATH" = "$EXPECTED_HOOKS_PATH" ]; then
    echo "✅ Git hooks configured successfully"
    echo "   core.hooksPath = $NEW_HOOKS_PATH"
    echo "   Hooks directory: $HOOKS_DIR"
    echo ""
    echo "Installed hooks:"
    ls -la "$HOOKS_DIR/" | grep -E '(pre-commit|pre-push)'
    echo ""
    echo "To verify: git config --get core.hooksPath"
else
    echo "❌ Failed to configure hooks"
    exit 1
fi