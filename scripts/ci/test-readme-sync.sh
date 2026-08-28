#!/usr/bin/env bash
#
# Local scenario tests for scripts/ci/readme-sync.sh
# (Issue #180 README auto-update). No network or GitHub access is required;
# pushes go to a local bare repository via PUSH_URL.
#
# Scenarios:
#   1. preflight: proceed on a normal PR head
#   2. preflight: skip fork PRs
#   3. preflight: skip bot-authored head commit (loop prevention)
#   4. preflight: skip when PR repo context is missing
#   5. publish: no working-tree changes -> clean no-op, no push
#   6. publish: README-only change is committed as github-actions[bot] and pushed
#   7. publish: FAILS (no commit/push) when a NON-managed file changed
#      (e.g. a prompt-injection attempt modifies .github/workflows/...)
#   8. publish: FAILS when a managed file is deleted
#   9. publish: refuses to push to main
#  10. workflow YAML is well-formed
#  11. verify-config: OK for the pinned SSOT + opencode configs
#  12. verify-config: FAILS when the SSOT config model != opencode/big-pickle
#  13. verify-config: FAILS when the opencode config model != opencode/big-pickle
#  14. verify-config: FAILS when the opencode config small_model != opencode/big-pickle
#  15. verify-config: FAILS when the pinned version is not valid semver
#  16. publish: FAILS (refuses) in bootstrap mode (README_SYNC_BOOTSTRAP=1)
#  17. workflow: NO repository-variable overrides (no `vars.`); model literal
#  18. workflow: model job contents:read + no GITHUB_TOKEN/PUSH_URL; publish job
#      contents:write with token only in the publish step; publish needs model job
#  19. workflow: bootstrap gating - publish steps skipped unless origin/main assets exist
#  20. workflow run-scripts: bash-n (and shellcheck+actionlint if installed) clean;
#      ASSETS extraction uses an array with "${ASSETS[@]}" (no unquoted expansion)
#  21. extraction step functional: origin/main assets -> bootstrap=0, PR-head
#      fallback -> bootstrap=1 (analyze-only bootstrap path)
#  22. model-output exporter functional: only-README change exports; other changes
#      or a deleted README fail closed
#  23. artifact validation functional: exactly one root README.md passes; extra
#      file, symlink, or subdirectory fail closed
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SYNC="$SCRIPT_DIR/readme-sync.sh"
WORKFLOW="$(cd "$SCRIPT_DIR/../.." && pwd)/.github/workflows/readme-autoupdate.yml"
CONFIG="$(cd "$SCRIPT_DIR/../.." && pwd)/config/readme-autoupdate.json"
OPENCODE_CONFIG="$(cd "$SCRIPT_DIR/../.." && pwd)/config/readme-autoupdate/opencode.json"

PASS=0
FAIL=0
TESTS=()
ROOT_DIR="$(mktemp -d)"
trap 'rm -rf "$ROOT_DIR"' EXIT

fail() {
  echo "  FAIL: $1" >&2
  FAIL=$((FAIL + 1))
}

assert_eq() {
  if [[ "$1" != "$2" ]]; then
    fail "expected [$1] == [$2]"
  fi
}

assert_ne() {
  if [[ "$1" == "$2" ]]; then
    fail "expected [$1] != [$2]"
  fi
}

setup_repo() {
  ROOT="$ROOT_DIR/$1"
  mkdir -p "$ROOT"
  REMOTE="$ROOT/remote.git"
  git init --bare --initial-branch=main "$REMOTE" >/dev/null 2>&1
  WORK="$ROOT/work"
  git clone -q "$REMOTE" "$WORK"
  git -C "$WORK" config user.name tester
  git -C "$WORK" config user.email tester@example.com
  cat > "$WORK/README.md" <<'EOF'
# Demo
日本語のREADME。
EOF
  mkdir -p "$WORK/.github/workflows"
  cat > "$WORK/.github/workflows/test.yml" <<'EOF'
name: CI
on: [push]
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: echo hi
EOF
  git -C "$WORK" add -A
  git -C "$WORK" commit -q -m "seed"
  git -C "$WORK" push -q origin main
  git -C "$WORK" checkout -q -b pr-branch
  git -C "$WORK" commit -q --allow-empty -m "pr head commit"
}

preflight_env() {
  export README_SYNC_CONFIG="$CONFIG"
  export GITHUB_REPOSITORY="${GITHUB_REPOSITORY:-owner/repo}"
}

# --- 1. preflight proceed -----------------------------------------------
test_preflight_proceed() {
  setup_repo t1
  local out
  out="$(mktemp)"
  ( cd "$WORK" && preflight_env && PR_HEAD_REPO=owner/repo \
      PR_HEAD_SHA="$(git rev-parse HEAD)" PR_HEAD_BRANCH=pr-branch \
      bash "$SYNC" preflight "$out" ) >/dev/null
  grep -q '^action=proceed$' "$out" || fail "expected action=proceed"
}

# --- 2. preflight fork skip ----------------------------------------------
test_preflight_fork() {
  setup_repo t2
  local out
  out="$(mktemp)"
  ( cd "$WORK" && preflight_env && PR_HEAD_REPO=attacker/repo \
      PR_HEAD_SHA="$(git rev-parse HEAD)" PR_HEAD_BRANCH=pr-branch \
      bash "$SYNC" preflight "$out" ) >/dev/null
  grep -q '^action=skip$' "$out" || fail "expected action=skip"
  grep -q '^action_reason=fork_pr$' "$out" || fail "expected reason=fork_pr"
}

# --- 3. preflight bot-commit loop prevention -----------------------------
test_preflight_bot_loop() {
  setup_repo t3
  local out
  out="$(mktemp)"
  git -C "$WORK" config user.name "github-actions[bot]"
  git -C "$WORK" config user.email "41898282+github-actions[bot]@users.noreply.github.com"
  git -C "$WORK" commit -q --allow-empty -m "bot update"
  ( cd "$WORK" && preflight_env && PR_HEAD_REPO=owner/repo \
      PR_HEAD_SHA="$(git rev-parse HEAD)" PR_HEAD_BRANCH=pr-branch \
      bash "$SYNC" preflight "$out" ) >/dev/null
  grep -q '^action=skip$' "$out" || fail "expected action=skip"
  grep -q '^action_reason=bot_commit$' "$out" || fail "expected reason=bot_commit"
}

# --- 4. preflight missing context -------------------------------
test_preflight_missing_context() {
  setup_repo t4
  local out
  out="$(mktemp)"
  ( cd "$WORK" && preflight_env && PR_HEAD_REPO="" \
      PR_HEAD_SHA="$(git rev-parse HEAD)" PR_HEAD_BRANCH=pr-branch \
      bash "$SYNC" preflight "$out" ) >/dev/null
  grep -q '^action=skip$' "$out" || fail "expected action=skip"
}

# --- 5. publish no changes -> no-op ---------------------------------------
test_publish_noop() {
  setup_repo t5
  local before after
  before="$(git ls-remote "$REMOTE" refs/heads/pr-branch)"
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" PUSH_URL="file://$REMOTE" \
      PR_HEAD_BRANCH=pr-branch bash "$SYNC" publish ) >/dev/null
  after="$(git ls-remote "$REMOTE" refs/heads/pr-branch)"
  assert_eq "$before" "$after" "no-op: remote branch must be unchanged"
}

# --- 6. publish README-only -> bot commit pushed --------------------------
test_publish_readme_only() {
  setup_repo t6
  printf '\n更新ノート：タイマ実行時間の改善を追加。\n' >> "$WORK/README.md"
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" PUSH_URL="file://$REMOTE" \
      PR_HEAD_BRANCH=pr-branch bash "$SYNC" publish ) >/dev/null
  local remote_head
  remote_head="$(git ls-remote "$REMOTE" refs/heads/pr-branch | cut -f1)"
  assert_ne "" "$remote_head" "README change must be pushed"
  git -C "$WORK" fetch -q origin
  git -C "$WORK" show "origin/pr-branch:README.md" | grep -q "更新ノート" \
    || fail "remote README must contain the update"
  git -C "$WORK" show -s --format='%ae' "origin/pr-branch" | grep -q "41898282+github-actions" \
    || fail "commit author must be the configured bot"
  git -C "$WORK" show "origin/pr-branch:.github/workflows/test.yml" | grep -q "echo hi" \
    || fail "workflow file on remote must be untouched"
}

# --- 7. publish non-managed change -> fail closed, nothing pushed ---------
test_publish_non_managed_fail_closed() {
  setup_repo t7
  printf '\nname: EVIL\non: [push]\n' >> "$WORK/.github/workflows/test.yml"
  local before
  before="$(git ls-remote "$REMOTE" refs/heads/pr-branch)"
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" PUSH_URL="file://$REMOTE" \
      PR_HEAD_BRANCH=pr-branch bash "$SYNC" publish ) >/dev/null 2>&1;
  local ec=$?
  assert_ne 0 "$ec" "publish must fail when a non-managed file is modified"
  assert_eq "$before" "$(git ls-remote "$REMOTE" refs/heads/pr-branch)" \
    "remote branch must be unchanged"
  assert_ne "$(git -C "$WORK" log --oneline -1 --format=%s)" "docs: update README" \
    "no bot commit may be created"
}

# --- 8. publish deletion of managed file -> fail closed --------------------
test_publish_deletion_fail_closed() {
  setup_repo t8
  rm "$WORK/README.md"
  local before
  before="$(git ls-remote "$REMOTE" refs/heads/pr-branch)"
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" PUSH_URL="file://$REMOTE" \
      PR_HEAD_BRANCH=pr-branch bash "$SYNC" publish ) >/dev/null 2>&1;
  local ec=$?
  assert_ne 0 "$ec" "publish must fail when a managed file is deleted"
  assert_eq "$before" "$(git ls-remote "$REMOTE" refs/heads/pr-branch)" \
    "remote branch must be unchanged"
}

# --- 9. publish refuses to push to main ----------------------------------
test_publish_no_main() {
  setup_repo t9
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" PUSH_URL="file://$REMOTE" \
      PR_HEAD_BRANCH=main bash "$SYNC" publish ) >/dev/null 2>&1;
  local ec=$?
  assert_ne 0 "$ec" "publish must refuse to push to main"
}

# --- 10. workflow structure: triggers + two-job least-privilege scope ---------
test_workflow_yaml() {
  python3 - "$WORKFLOW" <<'PY' || fail "workflow YAML did not parse"
import sys, yaml
with open(sys.argv[1]) as f:
    d = yaml.safe_load(f)
assert "on" in d, "missing 'on'"
assert "pull_request" in d["on"], "missing pull_request trigger"
# Least privilege: workflow level grants NOTHING by default.
assert d.get("permissions") == {}, "top-level permissions must be empty ({}); jobs grant their own minimum"
jobs = d["jobs"]
assert jobs["update-readme"]["permissions"] == {"contents": "read"}, "model job: contents: read only"
assert jobs["publish-readme"]["permissions"] == {"contents": "write"}, "publish job: contents: write"
assert jobs["publish-readme"]["needs"] == "update-readme", "publish job must depend on the model job"
PY
}

# --- 11. verify-config OK with the pinned configs --------------------------
test_verify_config_ok() {
  local out
  out="$(mktemp)"
  README_SYNC_CONFIG="$CONFIG" \
    OPENCODE_CONFIG="$(cd "$SCRIPT_DIR/../.." && pwd)/config/readme-autoupdate/opencode.json" \
    bash "$SYNC" verify-config "$out" >/dev/null
  grep -q '^model=opencode/big-pickle$' "$out" || fail "verify-config must report the pinned model"
  grep -q '^version=v1\.18\.25$' "$out" || fail "verify-config must report the pinned version"
}

# --- 12. verify-config rejects a tampered SSOT model ------------------------
test_verify_config_rejects_ssot_model() {
  local dir cfg oc
  dir="$ROOT_DIR/t12"
  mkdir -p "$dir"
  cp "$CONFIG" "$dir/readme-autoupdate.json"
  cp "$(cd "$SCRIPT_DIR/../.." && pwd)/config/readme-autoupdate/opencode.json" "$dir/opencode.json"
  python3 - "$dir/readme-autoupdate.json" <<'PY' || fail "failed to tamper config"
import json, sys
p = sys.argv[1]
d = json.load(open(p))
d["opencode"]["model"] = "anthropic/claude-sonnet-4-6"
json.dump(d, open(p, "w"))
PY
  ( cd "$dir" && README_SYNC_CONFIG="$dir/readme-autoupdate.json" \
      OPENCODE_CONFIG="$dir/opencode.json" \
      bash "$SYNC" verify-config out.txt >/dev/null 2>&1 )
  local ec=$?
  assert_ne 0 "$ec" "verify-config must reject a non-Big-Pickle SSOT model"
}

# --- 13. verify-config rejects a tampered opencode config model -------------
test_verify_config_rejects_opencode_model() {
  local dir cfg oc
  dir="$ROOT_DIR/t13"
  mkdir -p "$dir"
  cp "$CONFIG" "$dir/readme-autoupdate.json"
  cp "$(cd "$SCRIPT_DIR/../.." && pwd)/config/readme-autoupdate/opencode.json" "$dir/opencode.json"
  python3 - "$dir/opencode.json" <<'PY' || fail "failed to tamper opencode config"
import json, sys
p = sys.argv[1]
d = json.load(open(p))
d["model"] = "openai/gpt-5"
json.dump(d, open(p, "w"))
PY
  ( cd "$dir" && README_SYNC_CONFIG="$dir/readme-autoupdate.json" \
      OPENCODE_CONFIG="$dir/opencode.json" \
      bash "$SYNC" verify-config out.txt >/dev/null 2>&1 )
  local ec=$?
  assert_ne 0 "$ec" "verify-config must reject a non-Big-Pickle opencode model"
}

# --- 14. verify-config rejects a tampered small_model -----------------------
test_verify_config_rejects_small_model() {
  local dir cfg oc
  dir="$ROOT_DIR/t14"
  mkdir -p "$dir"
  cp "$CONFIG" "$dir/readme-autoupdate.json"
  cp "$(cd "$SCRIPT_DIR/../.." && pwd)/config/readme-autoupdate/opencode.json" "$dir/opencode.json"
  python3 - "$dir/opencode.json" <<'PY' || fail "failed to tamper opencode config"
import json, sys
p = sys.argv[1]
d = json.load(open(p))
d["small_model"] = "openai/gpt-5-mini"
json.dump(d, open(p, "w"))
PY
  ( cd "$dir" && README_SYNC_CONFIG="$dir/readme-autoupdate.json" \
      OPENCODE_CONFIG="$dir/opencode.json" \
      bash "$SYNC" verify-config out.txt >/dev/null 2>&1 )
  local ec=$?
  assert_ne 0 "$ec" "verify-config must reject a non-Big-Pickle small_model"
}

# --- 15. verify-config rejects an invalid pinned version --------------------
test_verify_config_rejects_bad_version() {
  local dir
  dir="$ROOT_DIR/t15"
  mkdir -p "$dir"
  cp "$CONFIG" "$dir/readme-autoupdate.json"
  cp "$(cd "$SCRIPT_DIR/../.." && pwd)/config/readme-autoupdate/opencode.json" "$dir/opencode.json"
  python3 - "$dir/readme-autoupdate.json" <<'PY' || fail "failed to tamper config"
import json, sys
p = sys.argv[1]
d = json.load(open(p))
d["opencode"]["version"] = "../../etc/passwd"
json.dump(d, open(p, "w"))
PY
  ( cd "$dir" && README_SYNC_CONFIG="$dir/readme-autoupdate.json" \
      OPENCODE_CONFIG="$dir/opencode.json" \
      bash "$SYNC" verify-config out.txt >/dev/null 2>&1 )
  local ec=$?
  assert_ne 0 "$ec" "verify-config must reject a non-semver version (path injection guard)"
}

# --- 16. publish refuses in bootstrap mode ----------------------------------
test_publish_bootstrap_refused() {
  setup_repo t16
  local ec before after
  before="$(git ls-remote "$REMOTE" refs/heads/pr-branch)"
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" PUSH_URL="file://$REMOTE" \
      PR_HEAD_BRANCH=pr-branch README_SYNC_BOOTSTRAP=1 \
      bash "$SYNC" publish ) >/dev/null 2>&1
  ec=$?
  after="$(git ls-remote "$REMOTE" refs/heads/pr-branch)"
  assert_ne 0 "$ec" "publish must refuse to run in bootstrap mode"
  assert_eq "$before" "$after" "bootstrap refusal must leave the remote untouched"
}

# wf_step_run <job> <name-substring> -> prints the `run` block of that step
wf_step_run() {
  python3 - "$WORKFLOW" "$1" "$2" <<'PY'
import sys, yaml
wf, job, name = sys.argv[1], sys.argv[2], sys.argv[3]
d = yaml.safe_load(open(wf))
for s in d["jobs"][job]["steps"]:
    if name in (s.get("name") or ""):
        print(s.get("run") or "")
        break
PY
}

# wf_step_if <job> <name-substring> -> prints the `if` condition of that step
wf_step_if() {
  python3 - "$WORKFLOW" "$1" "$2" <<'PY'
import sys, yaml
wf, job, name = sys.argv[1], sys.argv[2], sys.argv[3]
d = yaml.safe_load(open(wf))
for s in d["jobs"][job]["steps"]:
    if name in (s.get("name") or ""):
        print(s.get("if") or "")
        break
PY
}

# --- 17. workflow: no repository-variable overrides; model literal ----------
test_workflow_no_vars() {
  if grep -q 'vars\.' "$WORKFLOW"; then
    fail "workflow must not read repository variables"
  fi
  if ! wf_step_run update-readme "Run OpenCode" | grep -q -- '--model opencode/big-pickle'; then
    fail "opencode must be invoked with the literal pinned model"
  fi
}

# --- 18. workflow: token/scope separation between the two jobs --------------
test_workflow_token_boundary() {
  local model_perm publish_perm
  python3 - "$WORKFLOW" <<'PY' || fail "workflow structure check failed"
import sys, yaml
d = yaml.safe_load(open(sys.argv[1]))
jobs = d["jobs"]
assert jobs["update-readme"]["permissions"] == {"contents": "read"}, "model job must have contents: read only"
assert jobs["publish-readme"]["permissions"] == {"contents": "write"}, "publish job must have contents: write"
assert jobs["publish-readme"].get("needs") == "update-readme", "publish job must depend on the model job"
for jname, job in jobs.items():
    for s in job["steps"]:
        sname = s.get("name") or ""
        blob = (s.get("run") or "") + " " + " ".join(str(v) for v in (s.get("env") or {}).values())
        if "github.token" in blob and not (jname == "publish-readme" and sname == "Publish README updates"):
            sys.exit("GITHUB_TOKEN referenced outside the publish step: %s/%s" % (jname, sname))
        if jname == "update-readme" and "PUSH_URL" in blob:
            sys.exit("PUSH_URL referenced in the model job")
PY
  model_perm="$(python3 - "$WORKFLOW" <<'PY'
import sys, yaml
print(yaml.safe_load(open(sys.argv[1]))["jobs"]["update-readme"]["permissions"]["contents"])
PY
)"
  publish_perm="$(python3 - "$WORKFLOW" <<'PY'
import sys, yaml
print(yaml.safe_load(open(sys.argv[1]))["jobs"]["publish-readme"]["permissions"]["contents"])
PY
)"
  assert_eq "$model_perm" "read" "model job permission must be contents:read"
  assert_eq "$publish_perm" "write" "publish job permission must be contents:write"
}

# --- 19. workflow: bootstrap gating on publish steps ------------------------
test_workflow_bootstrap_gating() {
  local job_if cond run
  job_if="$(python3 - "$WORKFLOW" <<'PY'
import sys, yaml
d = yaml.safe_load(open(sys.argv[1]))
print(d["jobs"]["publish-readme"].get("if") or "")
PY
)"
  if [[ "$job_if" != *"needs.update-readme.outputs.bootstrap == '0'"* ]]; then
    fail "publish job must be gated on the model job's bootstrap output (bootstrap runs stay analyze-only)"
  fi
  cond="$(wf_step_if publish-readme "Publish README updates")"
  if [[ "$cond" != *"steps.extract.outputs.bootstrap == '0'"* ]]; then
    fail "publish step must be gated on bootstrap=0"
  fi
  cond="$(wf_step_if publish-readme "Download candidate README artifact")"
  if [[ "$cond" != *"steps.extract.outputs.bootstrap == '0'"* ]]; then
    fail "artifact download must be gated on bootstrap=0"
  fi
  run="$(wf_step_run publish-readme "Extract trusted assets")"
  if ! echo "$run" | grep -q 'git archive origin/main "${ASSETS\[@\]}"'; then
    fail "publish extraction must use origin/main with the quoted asset array"
  fi
  if ! echo "$run" | grep -q '::warning::BOOTSTRAP'; then
    fail "publish extraction must emit a loud bootstrap warning instead of falling back"
  fi
}

# --- 20. workflow run-scripts: bash -n (+ shellcheck/actionlint when present); ASSETS array ----
test_workflow_run_scripts() {
  local tmp failed=0 f
  tmp="$(mktemp -d)"
  trap 'rm -rf "${tmp:-}"' RETURN
  python3 - "$WORKFLOW" "$tmp/scripts" <<'PY' || fail "could not dump workflow run blocks"
import os, sys, yaml
wf, outdir = sys.argv[1], sys.argv[2]
os.makedirs(outdir, exist_ok=True)
d = yaml.safe_load(open(wf))
i = 0
for job in d["jobs"].values():
    for s in job["steps"]:
        if s.get("run"):
            i += 1
            open(os.path.join(outdir, "step%02d.sh" % i), "w").write(s["run"])
PY
  for f in "$tmp/scripts"/*.sh; do
    sed -E 's/\$\{\{[^}]*\}\}/X/g' "$f" > "$tmp/step.sh"
    if ! bash -n "$tmp/step.sh"; then
      fail "workflow run-step is not valid bash: $f"
      failed=1
    fi
    if command -v shellcheck >/dev/null 2>&1; then
      if ! shellcheck -x -S warning "$tmp/step.sh" >/dev/null 2>&1; then
        fail "workflow run-step failed shellcheck: $f"
        failed=1
      fi
    fi
  done
  local run_main run_pub
  run_main="$(wf_step_run update-readme "Extract trusted assets")"
  if ! echo "$run_main" | grep -q '^ASSETS=('; then
    fail "extract step must define ASSETS as an array"
  fi
  if ! echo "$run_main" | grep -q 'git archive origin/main "${ASSETS\[@\]}"'; then
    fail "extract step must use the quoted array expansion in git archive"
  fi
  if ! echo "$run_main" | grep -q 'tar -C . -cf - "${ASSETS\[@\]}"'; then
    fail "extract step must use the quoted array expansion in tar"
  fi
  # "$ASSETS" (bare, unquoted) must never appear; the array form is "${ASSETS[@]}".
  if echo "$run_main" | grep -q '\$ASSETS'; then
    fail "unquoted ASSETS expansion detected"
  fi
  run_pub="$(wf_step_run publish-readme "Extract trusted assets")"
  if ! echo "$run_pub" | grep -q 'git archive origin/main "${ASSETS\[@\]}"'; then
    fail "publish extract step must use the quoted array expansion in git archive"
  fi
  [[ $failed -eq 0 ]]
}

# --- 21. extraction functional: trusted vs bootstrap ------------------------
test_workflow_extract_functional() {
  local dir r1 r2 rt repo w1 w2 out1 out2
  dir="$ROOT_DIR/wfextract"
  mkdir -p "$dir"
  repo="$(cd "$SCRIPT_DIR/../.." && pwd)"

  # ---- trusted: origin/main HAS the assets ----
  r1="$dir/trusted.git"
  git init -q --bare --initial-branch=main "$r1"
  w1="$dir/w1"
  git clone -q "$r1" "$w1"
  git -C "$w1" config user.name tester
  git -C "$w1" config user.email tester@example.com
  mkdir -p "$w1/scripts/ci" "$w1/config/readme-autoupdate"
  cp "$repo/scripts/ci/readme-sync.sh" "$w1/scripts/ci/"
  cp "$repo/config/readme-autoupdate.json" "$w1/config/"
  cp "$repo/config/readme-autoupdate/opencode.json" "$w1/config/readme-autoupdate/"
  cp "$repo/config/readme-autoupdate/prompt.md" "$w1/config/readme-autoupdate/"
  git -C "$w1" add -A
  git -C "$w1" commit -q -m "trusted assets"
  git -C "$w1" push -q origin main
  out1="$dir/out1.txt"
  rt="$dir/rt1"
  mkdir -p "$rt"
  ( cd "$w1" && RUNNER_TEMP="$rt" GITHUB_OUTPUT="$out1" bash -e -c "$(wf_step_run update-readme "Extract trusted assets")" ) >/dev/null
  grep -q '^bootstrap=0$' "$out1" || fail "trusted extraction must report bootstrap=0"
  [[ -f "$rt/readme-sync/trusted/scripts/ci/readme-sync.sh" ]] || fail "trusted extraction must include readme-sync.sh"
  [[ -f "$rt/readme-sync/trusted/config/readme-autoupdate.json" ]] || fail "trusted extraction must include the SSOT config"
  [[ -f "$rt/readme-sync/trusted/config/readme-autoupdate/opencode.json" ]] || fail "trusted extraction must include the opencode config"
  [[ -f "$rt/readme-sync/trusted/config/readme-autoupdate/prompt.md" ]] || fail "trusted extraction must include the prompt"

  # ---- bootstrap: origin/main lacks the assets, PR head has them ----
  r2="$dir/bootstrap.git"
  git init -q --bare --initial-branch=main "$r2"
  w2="$dir/w2"
  git clone -q "$r2" "$w2"
  git -C "$w2" config user.name tester
  git -C "$w2" config user.email tester@example.com
  git -C "$w2" commit -q --allow-empty -m "seed"
  git -C "$w2" push -q origin main
  mkdir -p "$w2/scripts/ci" "$w2/config/readme-autoupdate"
  cp "$repo/scripts/ci/readme-sync.sh" "$w2/scripts/ci/"
  cp "$repo/config/readme-autoupdate.json" "$w2/config/"
  cp "$repo/config/readme-autoupdate/opencode.json" "$w2/config/readme-autoupdate/"
  cp "$repo/config/readme-autoupdate/prompt.md" "$w2/config/readme-autoupdate/"
  git -C "$w2" add -A
  git -C "$w2" commit -q -m "introduce assets on PR head only"
  out2="$dir/out2.txt"
  rt="$dir/rt2"
  mkdir -p "$rt"
  ( cd "$w2" && RUNNER_TEMP="$rt" GITHUB_OUTPUT="$out2" bash -e -c "$(wf_step_run update-readme "Extract trusted assets")" ) >/dev/null 2>&1
  grep -q '^bootstrap=1$' "$out2" || fail "bootstrap extraction must report bootstrap=1"
  [[ -f "$rt/readme-sync/trusted/scripts/ci/readme-sync.sh" ]] || fail "bootstrap extraction must still provide the assets from the PR head"
}

# --- 22. model-output exporter functional (candidate README gate) -----------
test_model_exporter_gate() {
  local d w rt script ec
  d="$ROOT_DIR/exporter"
  mkdir -p "$d"
  w="$d/w"
  git init -q --initial-branch=main "$w"
  git -C "$w" config user.name tester
  git -C "$w" config user.email tester@example.com
  printf '# Demo\n' > "$w/README.md"
  git -C "$w" add -A && git -C "$w" commit -q -m seed
  script="$(wf_step_run update-readme "Validate model output")"

  # README-only change -> exports exactly the candidate README
  printf '# Demo\n\nupdated text\n' > "$w/README.md"
  rt="$d/rt"
  mkdir -p "$rt"
  ( cd "$w" && RUNNER_TEMP="$rt" bash -e -c "$script" ) >/dev/null
  ec=$?
  assert_eq 0 "$ec" "README-only change must export successfully"
  [[ -f "$rt/readme-candidate/README.md" ]] || fail "candidate README must be exported"
  grep -q "updated text" "$rt/readme-candidate/README.md" || fail "exported candidate must match the working tree"

  # non-README change -> fails closed, nothing exported
  mkdir -p "$w/.github"
  printf 'x\n' > "$w/.github/EVIL"
  rm -f "$rt/readme-candidate/README.md"
  ( cd "$w" && RUNNER_TEMP="$rt" bash -e -c "$script" ) >/dev/null 2>&1
  ec=$?
  assert_ne 0 "$ec" "exporter must fail when the model touched a non-README path"
  [[ -f "$rt/readme-candidate/README.md" ]] && fail "exporter must not export a candidate when the gate fails"

  # deleted README -> fails closed
  rm "$w/.github/EVIL"
  rm "$w/README.md"
  ( cd "$w" && RUNNER_TEMP="$rt" bash -e -c "$script" ) >/dev/null 2>&1
  ec=$?
  assert_ne 0 "$ec" "exporter must fail when the candidate README is missing (deletion)"
}

# --- 23. artifact validation functional (publish-side gate) -----------------
test_artifact_validation() {
  local script d rt
  script="$(wf_step_run publish-readme "Validate artifact")"
  d="$ROOT_DIR/artifact"
  mkdir -p "$d"

  # good: exactly one root-level README.md
  rt="$d/good"
  mkdir -p "$rt/readme-artifact"
  printf '# hi\n' > "$rt/readme-artifact/README.md"
  ( cd "$d" && RUNNER_TEMP="$rt" bash -e -c "$script" ) >/dev/null
  assert_eq 0 "$?" "artifact with exactly one README.md must validate"

  # extra file -> fail
  rt="$d/extra"
  mkdir -p "$rt/readme-artifact"
  printf '# hi\n' > "$rt/readme-artifact/README.md"
  printf 'x\n' > "$rt/readme-artifact/EVIL.md"
  ( cd "$d" && RUNNER_TEMP="$rt" bash -e -c "$script" ) >/dev/null 2>&1
  assert_ne 0 "$?" "artifact with an extra non-README file must be rejected"

  # scripts/ sneaking in -> fail
  rt="$d/scripts"
  mkdir -p "$rt/readme-artifact"
  printf '# hi\n' > "$rt/readme-artifact/README.md"
  mkdir -p "$rt/readme-artifact/scripts/ci"
  touch "$rt/readme-artifact/scripts/ci/evil.sh"
  ( cd "$d" && RUNNER_TEMP="$rt" bash -e -c "$script" ) >/dev/null 2>&1
  assert_ne 0 "$?" "artifact containing scripts/ must be rejected"

  # symlink README -> fail
  rt="$d/symlink"
  mkdir -p "$rt/readme-artifact"
  ln -s /etc/passwd "$rt/readme-artifact/README.md"
  ( cd "$d" && RUNNER_TEMP="$rt" bash -e -c "$script" ) >/dev/null 2>&1
  assert_ne 0 "$?" "artifact containing a symlink must be rejected"
}

# --- 24. opencode permission rules: catch-all first, specifics last ----------
# OpenCode evaluates object rules with "last matching rule winning" (see
# docs/adr/009-readme-autoupdate.md and https://opencode.ai/docs/permissions/).
# The catch-all "*" deny must therefore come BEFORE the specific allows so the
# read-only git rules remain effective; a trailing "*": "deny" would override
# them all and break the model analysis.
test_opencode_permission_rules() {
  python3 - "$OPENCODE_CONFIG" <<'PY' || fail "opencode permission rules mis-ordered"
import json, sys
d = json.load(open(sys.argv[1]))
bash = d["permission"]["bash"]
rules = list(bash.items())
assert rules and rules[0] == ("*", "deny"), "bash: catch-all '*': 'deny' must be the FIRST rule (last-match-wins)"
expected = {
    "git status*": "allow",
    "git diff*": "allow",
    "git log*": "allow",
    "git show*": "allow",
    "git blame*": "allow",
}
for k, v in expected.items():
    assert bash.get(k) == v, "bash: missing/incorrect allow rule %s" % k
edit = d["permission"]["edit"]
assert list(edit.items())[0] == ("*", "deny"), "edit: catch-all '*': 'deny' must be the FIRST rule"
assert edit.get("README.md") == "allow" and edit.get("**/README.md") == "allow", "edit: README.md allows required"
assert d["permission"].get("webfetch") == "deny", "webfetch must be denied"
assert d["permission"].get("websearch") == "deny", "websearch must be denied"
PY
}

main() {
  bash -n "$SYNC" || { echo "syntax error in readme-sync.sh"; exit 1; }
  tests=(test_preflight_proceed test_preflight_fork test_preflight_bot_loop \
         test_preflight_missing_context test_publish_noop \
         test_publish_readme_only test_publish_non_managed_fail_closed \
         test_publish_deletion_fail_closed test_publish_no_main \
         test_workflow_yaml test_verify_config_ok \
         test_verify_config_rejects_ssot_model test_verify_config_rejects_opencode_model \
         test_verify_config_rejects_small_model test_verify_config_rejects_bad_version \
         test_publish_bootstrap_refused test_workflow_no_vars test_workflow_token_boundary \
         test_workflow_bootstrap_gating test_workflow_run_scripts \
         test_workflow_extract_functional test_model_exporter_gate test_artifact_validation \
         test_opencode_permission_rules)
  for t in "${tests[@]}"; do
    printf '%s ...\n' "$t"
    if "$t"; then
      PASS=$((PASS + 1))
    else
      TESTS+=("$t")
    fi
    echo
  done
  echo "PASS=$PASS FAIL=$FAIL"
  if [[ $FAIL -gt 0 ]]; then
    printf 'failed: %s\n' "${TESTS[*]}"
    exit 1
  fi
}

main