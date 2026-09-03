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
#  24. opencode permission rules: catch-all first, specifics last
#  25. workflow: review-trigger job - isolated pull-requests:write, needs both
#      upstream jobs, SHA-verification step, fixed-literal trigger comment
#  26. .coderabbit.yaml: automatic reviews disabled and bot command supported
#  27. workflow: review-trigger SHA flow - expected comes from the publish job's
#      published_sha output (NOT the event head.sha); unset output refuses
#  28. publish record-sha functional - README change -> new HEAD SHA; no-op ->
#      HEAD unchanged (in-job paths set the output; only executed runs reach it)
#  29. workflow: README-changed regression (A -> publish -> B -> published_sha=B
#      -> review-trigger against B) and no reliance on the bot push restarting
#      the workflow (the push must not be treated as an expected-SHA source)
#  30. review gate functional: API head vs published_sha - match=1 only when the
#      publish job ran and the current head equals its output; an EMPTY
#      published_sha (job-level skip) and mismatches both fail closed to match=0
#  31. workflow: job-level publish skip (fork / bootstrap / upstream failure)
#      leaves published_sha EMPTY and the trigger gate closed - the empty
#      output is the CORRECT contract (record-sha is unreachable), not a bug
#  32. workflow: marker mutation uses bounded optimistic concurrency and
#      verifies exact post-write body/HEAD state for insertion and cleanup
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

# --- 18. workflow: token/scope separation between the three jobs -------------
test_workflow_token_boundary() {
  local model_perm publish_perm review_perm
  python3 - "$WORKFLOW" <<'PY' || fail "workflow structure check failed"
import sys, yaml
d = yaml.safe_load(open(sys.argv[1]))
jobs = d["jobs"]
assert jobs["update-readme"]["permissions"] == {"contents": "read"}, "model job must have contents: read only"
assert jobs["publish-readme"]["permissions"] == {"contents": "write"}, "publish job must have contents: write"
assert jobs["publish-readme"].get("needs") == "update-readme", "publish job must depend on the model job"
# The publish job must expose the post-publish PR head SHA to review-trigger.
outs = jobs["publish-readme"].get("outputs", {})
assert outs.get("published_sha"), "publish job must expose a published_sha output"
assert "github.event.pull_request.head.sha" in outs["published_sha"] or jobs["publish-readme"]["steps"][-1]["id"] == "record-sha", \
    "publish job must derive published_sha from a record-sha step (not the event head.sha)"
# The review trigger is a separate concern with its own minimal scope.
review_permissions = jobs["review-trigger"]["permissions"]
assert review_permissions.get("pull-requests") == "write", "review-trigger must have pull-requests: write"
assert review_permissions.get("issues") == "read", "review-trigger must have issues: read for CodeRabbit evidence"
assert "contents" not in review_permissions, "review-trigger must not have contents permission"
assert jobs["review-trigger"].get("needs") == ["update-readme", "publish-readme"], "review-trigger must depend on both upstream jobs"
for jname, job in jobs.items():
    for s in job["steps"]:
        sname = s.get("name") or ""
        blob = (s.get("run") or "") + " " + " ".join(str(v) for v in (s.get("env") or {}).values())
        if "github.token" in blob:
            allowed = (jname == "publish-readme" and sname == "Publish README updates") or \
                      (jname == "review-trigger" and sname in (
                          "Verify PR head SHA matches the README Auto-Update state",
                          "Opt in and wait for CodeRabbit review",
                          "Wait for current-HEAD CodeRabbit review evidence",
                      ))
            if not allowed:
                sys.exit("GITHUB_TOKEN referenced outside the publish/review steps: %s/%s" % (jname, sname))
        if jname == "update-readme" and "PUSH_URL" in blob:
            sys.exit("PUSH_URL referenced in the model job")
        if jname != "update-readme" and "PUSH_URL" in blob:
            sys.exit("PUSH_URL referenced in a non-publish job: %s/%s" % (jname, sname))
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
  review_perm="$(python3 - "$WORKFLOW" <<'PY'
import sys, yaml
print(yaml.safe_load(open(sys.argv[1]))["jobs"]["review-trigger"]["permissions"]["pull-requests"])
PY
)"
  assert_eq "$model_perm" "read" "model job permission must be contents:read"
  assert_eq "$publish_perm" "write" "publish job permission must be contents:write"
  assert_eq "$review_perm" "write" "review-trigger permission must be pull-requests:write"
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

# --- 25. workflow: review-trigger job (CodeRabbit ordering, Issue #185) -------
test_workflow_review_trigger() {
  python3 - "$WORKFLOW" <<'PY' || fail "review-trigger job structure check failed"
import sys, yaml
d = yaml.safe_load(open(sys.argv[1]))
j = d["jobs"]["review-trigger"]
assert j.get("needs") == ["update-readme", "publish-readme"], "review-trigger must need both upstream jobs"
assert j.get("if") == "always()", "review-trigger must use if: always() and gate itself inside"
assert j["permissions"].get("pull-requests") == "write", "review-trigger: pull-requests: write required"
assert j["permissions"].get("issues") == "read", "review-trigger needs issues: read to observe CodeRabbit comments"
assert "contents" not in j["permissions"], "review-trigger must not touch contents"
assert j.get("concurrency", {}).get("group"), "review-trigger must serialize triggers per PR with a concurrency group"
names = [s.get("name") for s in j["steps"]]
assert any(n and "Verify PR head SHA" in n for n in names), "review-trigger must verify the PR head SHA"
# Only one checkout step permitted: the review-trigger must NOT checkout PR code.
verify = next((s for s in j["steps"] if "Verify PR head SHA" in (s.get("name") or "")), None)
assert verify is not None, "missing verify step"
env = verify.get("env") or {}
assert "PUBLISHED_SHA" in env, "verify step must read the publish job's published_sha output"
assert "github.event.pull_request.head.sha" not in env.get("PUBLISHED_SHA", ""), \
    "verify step must NOT use the event head.sha as the expected value (stale before publish)"
vrun = verify.get("run") or ""
assert 'expected="${PUBLISHED_SHA:-}"' in vrun, "verify step must default expected from PUBLISHED_SHA"
assert "-z \"$expected\"" in vrun, "verify step must refuse when published_sha output is unset/empty"
# The opt-in step must be gated on SHA match and both upstream successes.
trigger = next(s for s in j["steps"] if "Opt in and wait for CodeRabbit review" in (s.get("name") or ""))
cond = trigger.get("if") or ""
assert cond.count("&&") >= 3, "opt-in step must AND together all gates: %r" % cond
for needle in ("steps.verify.outputs.match == '1'",
               "needs.update-readme.result == 'success'",
               "needs.update-readme.outputs.action == 'proceed'",
               "needs.publish-readme.result == 'success'"):
    assert needle in cond, "opt-in step must check %s" % needle
run = trigger.get("run") or ""
assert "coderabbit:review-ready" in run, "opt-in must use the configured description marker"
assert "@coderabbitai review" not in run, "bot-authored review command must not return"
assert run.count(".head.sha") >= 2, "opt-in must re-check the PR head around marker mutation"
assert "EXPECTED_HEAD" in run, "completion check must bind evidence to expected head"
assert "while" in run and "sleep" in run, "completion check must use bounded polling"
assert "deadline=" in run and "date +%s" in run, "completion check must use an explicit wall-clock deadline"
assert "per_page=100" in run, "completion polling must bound API page size"
assert 'pulls/$PR_NUMBER/reviews?per_page=100" --paginate --slurp' in run, "review evidence must inspect all review pages within the deadline"
assert 'select((.state // "") == "COMMENTED" or' in run, "only explicitly submitted review states may count"
assert '(.state // "") == "APPROVED" or' in run, "approved reviews must count as submitted evidence"
assert '(.state // "") == "CHANGES_REQUESTED")' in run, "changes-requested reviews must count as submitted evidence"
assert 'select((.submitted_at // "") != "")' in run, "review evidence must require submission"
assert 'timeout "$remaining" gh api' in run, "each evidence API request must be bounded by remaining deadline"
assert "bounded_pr()" in run, "all PR-head reads must share the remaining-deadline wrapper"
assert 'timeout "$remaining" gh api "repos/$GITHUB_REPOSITORY/pulls/$PR_NUMBER"' in run, "PR-head wrapper must be deadline-bounded"
assert "finish_review_cycle" in run, "evidence success must use a common finalization path"
assert "mutate_marker()" in run, "insertion and cleanup must use one marker mutation helper"
assert "for attempt in 1 2 3" in run, "marker mutation retries must be bounded"
assert 'desired="${body//$marker/}"' in run, "marker transformation must preserve unrelated body content"
assert 'desired+="$marker"' in run, "marker insertion must use one canonical marker"
assert '"$body" == "$desired"' in run, "marker mutation must verify exact post-write body"
assert "Review evidence exists, but marker cleanup could not be proven safe" in run, "unsafe cleanup must fail closed"
assert "CodeRabbit review-ready marker remained after completed review" in run, "marker cleanup must be verified fail-closed"
assert run.count('PR head moved while waiting for CodeRabbit review') >= 1 and \
       'PR head moved while finalizing CodeRabbit review' in run, \
    "review cycle must verify head before and after marker cleanup"
assert run.count('date +%s') >= 3, "completion must re-check wall-clock deadline after API calls"
assert "Timed out waiting for CodeRabbit review evidence" in run, "completion check must fail closed on timeout"
PY
}

# --- 26. workflow: execute current-HEAD failure paths with mocked API ---------
# The static contract checks above catch accidental removal of the guards, but
# these scenarios execute the actual review-trigger run block. The mock changes
# only the PR head; no real GitHub API or body write is used.
test_workflow_head_move_fail_closed() {
  local mock run log ec
  mock="$ROOT_DIR/marker-mock"
  mkdir -p "$mock"

  cat > "$mock/jq" <<'PY'
#!/usr/bin/env python3
import json, sys
args = sys.argv[1:]
data = sys.stdin.read()
if "-e" in args:
    sys.exit(0 if "No actionable comments were generated" in data and "expected-head" in data else 1)
query = next((a for a in args if a.startswith(".")), "")
obj = json.loads(data)
if query.startswith(".head.sha"):
    print(obj["head"]["sha"])
elif query.startswith(".body"):
    print(obj.get("body") or "")
else:
    raise SystemExit("unsupported mock jq query: " + query)
PY
  chmod +x "$mock/jq"

  cat > "$mock/gh" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
state="$MOCK_STATE"
count=0
[[ -f "$state" ]] && count="$(<"$state")"
echo "scenario=$MOCK_SCENARIO count=$count args=$*" >> "$MOCK_LOG"
if [[ " $* " == *" -X PATCH "* ]]; then
  exit 0
fi
path=""
for arg in "$@"; do
  [[ "$arg" == repos/* ]] && path="$arg"
done
if [[ "$path" == *"/issues/"*"/comments"* ]]; then
  printf '[{"user":{"login":"coderabbitai[bot]"},"body":"No actionable comments were generated for expected-head"}]\n'
  exit 0
fi
if [[ "$path" == *"/pulls/"*"/reviews"* ]]; then
  printf '[[]]\n'
  exit 0
fi
count=$((count + 1))
printf '%s\n' "$count" > "$state"
head="expected-head"
if [[ "$MOCK_SCENARIO" == "wait-head-move" && "$count" -ge 3 ]]; then
  head="moved-head"
elif [[ "$MOCK_SCENARIO" == "finalize-head-move" && "$count" -ge 7 ]]; then
  head="moved-head"
fi
body='description'
if [[ "$count" -ge 2 && "$count" -le 4 ]]; then
  body=$'description\n\n<!-- coderabbit:review-ready -->'
elif [[ "$MOCK_SCENARIO" == "finalize-head-move" && "$count" -eq 5 ]]; then
  body=$'description\n\n<!-- coderabbit:review-ready --> '
elif [[ "$MOCK_SCENARIO" == "finalize-head-move" && "$count" -ge 6 ]]; then
  body=$'description\n\n '
fi
python3 - "$head" "$body" <<'PY'
import json, sys
print(json.dumps({"head": {"sha": sys.argv[1]}, "body": sys.argv[2]}))
PY
SH
  chmod +x "$mock/gh"

  run="$(wf_step_run review-trigger "Opt in and wait for CodeRabbit review")"

  log="$ROOT_DIR/wait-head-move.log"
  set +e
  ( PATH="$mock:$PATH" MOCK_STATE="$ROOT_DIR/wait-head-move.state" \
      MOCK_SCENARIO=wait-head-move MOCK_LOG="$ROOT_DIR/wait-head-move.api.log" GITHUB_REPOSITORY=owner/repo PR_NUMBER=1 \
      PUBLISHED_SHA=expected-head GH_TOKEN=test GITHUB_OUTPUT="$ROOT_DIR/wait.out" \
      bash -e -c "$run" ) >"$log" 2>&1
  ec=$?
  set -e
  assert_ne 0 "$ec" "HEAD move during evidence wait must fail closed"
  grep -q "PR head moved while waiting for CodeRabbit review" "$log" \
    || fail "wait HEAD move must use the executable fail-closed guard"

  log="$ROOT_DIR/finalize-head-move.log"
  set +e
  ( PATH="$mock:$PATH" MOCK_STATE="$ROOT_DIR/finalize-head-move.state" \
      MOCK_SCENARIO=finalize-head-move MOCK_LOG="$ROOT_DIR/finalize-head-move.api.log" GITHUB_REPOSITORY=owner/repo PR_NUMBER=1 \
      PUBLISHED_SHA=expected-head GH_TOKEN=test GITHUB_OUTPUT="$ROOT_DIR/finalize.out" \
      bash -e -c "$run" ) >"$log" 2>&1
  ec=$?
  set -e
  assert_ne 0 "$ec" "HEAD move after cleanup must fail closed"
  grep -q "PR head moved while finalizing CodeRabbit review" "$log" \
    || fail "finalization HEAD move must use the executable fail-closed guard"
}

# --- 27. .coderabbit.yaml: automatic reviews disabled, bot command supported --
test_coderabbit_config() {
  local cfg
  cfg="$(cd "$SCRIPT_DIR/../.." && pwd)/.coderabbit.yaml"
  python3 - "$cfg" <<'PY' || fail "coderabbit config check failed"
import sys, yaml
d = yaml.safe_load(open(sys.argv[1]))
ar = d["reviews"]["auto_review"]
assert ar.get("enabled") is False, "auto_review.enabled must be false"
assert ar.get("description_keyword") == "coderabbit:review-ready", "description_keyword must match the workflow opt-in marker"
assert ar.get("auto_incremental_review") is True, "incremental review must stay enabled for later final-head changes"
assert ar.get("auto_pause_after_reviewed_commits") == 0, "auto review must not pause after reviewed commits"
PY
}

# --- 27. workflow: review-trigger SHA flow (published_sha, not event head.sha) ---
# Regression for PR #186 (blocking): expected SHA must come from the publish
# job's published_sha output, NEVER from github.event.pull_request.head.sha
# (which is captured before publish and is stale whenever a README commit is
# pushed). Also guards an unset output so the review can never be triggered
# against an unknown SHA.
test_workflow_sha_flow() {
  python3 - "$WORKFLOW" <<'PY' || fail "review-trigger SHA flow check failed"
import sys, yaml
d = yaml.safe_load(open(sys.argv[1]))
publish = d["jobs"]["publish-readme"]
# published_sha must come from a step, not be a constant, and must be declared.
outs = publish.get("outputs", {})
assert "published_sha" in outs, "publish job must output published_sha"
assert "github.event.pull_request.head.sha" not in outs["published_sha"], \
    "published_sha must be derived from the publish result, not the event head.sha"
verify = next(s for s in d["jobs"]["review-trigger"]["steps"]
              if "Verify PR head SHA" in (s.get("name") or ""))
env = verify.get("env") or {}
assert "PUBLISHED_SHA" in env and "needs.publish-readme.outputs.published_sha" in env["PUBLISHED_SHA"], \
    "verify step must compare against the publish job's published_sha output"
# No PR-controlled/untrusted content may feed the expected SHA.
expected = env["PUBLISHED_SHA"]
for untrusted in ("github.event.pull_request.body", "github.event.pull_request.title",
                  "steps.update-readme", "README", "PR_BODY"):
    assert untrusted not in expected, "expected SHA must not depend on untrusted content: %s" % untrusted
# git rev-parse HEAD must be absent from the verify step (verify uses the API,
# not a checkout, so it cannot be fooled by PR-controlled working-tree content).
assert "git rev-parse HEAD" not in (verify.get("run") or ""), \
    "verify step must fetch the current head via the API, not a PR checkout"
# The expected SHA must not be wired to any model/AI output.
allruns = "".join((s.get("run") or "") for s in d["jobs"]["review-trigger"]["steps"])
assert "opencode" not in allruns.lower() and "openmodel" not in allruns.lower(), \
    "review-trigger must not depend on AI/model output"
PY
}

# --- 28. publish record-sha functional (in-job paths set the output) -----------
#                                               ^ job-level skip never reaches this step (covered in 31)
test_publish_record_sha() {
  local script
  script="$(wf_step_run publish-readme "Record published PR head SHA")"
  [[ -n "$script" ]] || { fail "record-sha step missing from publish job"; return 1; }
  local d w rt out ec head
  d="$ROOT_DIR/recksha"
  mkdir -p "$d"
  w="$d/w"
  git init -q --initial-branch=main "$w"
  git -C "$w" config user.name tester
  git -C "$w" config user.email tester@example.com
  printf '# Demo\n' > "$w/README.md"
  git -C "$w" add -A && git -C "$w" commit -q -m seed
  head="$(git -C "$w" rev-parse HEAD)"

  # path 1: HEAD unchanged (README no-op / bootstrap skip) -> output == HEAD
  rt="$d/rt1"; mkdir -p "$rt"; out="$d/out1.txt"
  ( cd "$w" && RUNNER_TEMP="$rt" GITHUB_OUTPUT="$out" bash -e -c "$script" ) >/dev/null
  grep -q "^sha=$head$" "$out" || fail "no-op path must record the unchanged HEAD ($head)"

  # path 2: publish advanced HEAD (simulate a committed README change) -> new SHA
  printf 'update\n' >> "$w/README.md"
  git -C "$w" add README.md
  git -C "$w" -c user.name=bot -c user.email=41898282+github-actions[bot]@users.noreply.github.com \
      commit -q -m "docs: update README"
  new_head="$(git -C "$w" rev-parse HEAD)"
  assert_ne "$head" "$new_head" "README commit must create a new HEAD"
  rt="$d/rt2"; mkdir -p "$rt"; out="$d/out2.txt"
  ( cd "$w" && RUNNER_TEMP="$rt" GITHUB_OUTPUT="$out" bash -e -c "$script" ) >/dev/null
  grep -q "^sha=$new_head$" "$out" || fail "changed path must record the new HEAD ($new_head)"
  if grep -q "^sha=$head$" "$out"; then
    fail "changed path must NOT record the stale older HEAD"
  fi
}

# --- 29. workflow: README-changed regression + no bot-push-restart reliance -----
# The blocking bug (PR #186) was: after publish pushes a README commit, the
# event head.sha (A) no longer matches the real PR head (B), so review-trigger
# skipped. The fix makes review-trigger compare the API-fetched current head
# against the publish job's published_sha (B). We assert:
#   - published_sha equals the post-publish HEAD, computed in the publish job;
#   - review-trigger does NOT rely on a GITHUB_TOKEN push restarting the
#     workflow (i.e. the bot push is never assumed to be a fresh pull_request
#     event that would trigger a second review on SHA B).
test_workflow_readme_changed_regression() {
  python3 - "$WORKFLOW" <<'PY' || fail "README-changed regression check failed"
import sys, yaml
d = yaml.safe_load(open(sys.argv[1]))
publish = d["jobs"]["publish-readme"]
steps = publish["steps"]
record = next((s for s in steps if s.get("id") == "record-sha"), None)
assert record is not None, "publish job must have a record-sha step"
recrun = record.get("run") or ""
# The recorded SHA must come from the job's own git HEAD (post-publish), so it
# is the real PR head after the README commit (B), not the event SHA (A).
assert "git rev-parse HEAD" in recrun, "record-sha must use git rev-parse HEAD (post-publish)"
# It must run unconditionally once the job executes, so published_sha is set
# on every in-job path; a job-level skip cannot reach it (see test 31).
assert "if:" not in record, "record-sha step must run on every in-job path"
# The publish script may END right before record-sha (after pushing B) - ensure
# ordering: record-sha comes after the push step.
publish_step = next((s for s in steps if "Publish README updates" in (s.get("name") or "")), None)
assert publish_step is not None and steps.index(record) > steps.index(publish_step), \
    "record-sha must run after the publish (push) step so it reflects PR head B"
verify = next(s for s in d["jobs"]["review-trigger"]["steps"]
              if "Verify PR head SHA" in (s.get("name") or ""))
# The only source for the expected SHA is the publish job output; there must be
# NO fallback to the event head.sha anywhere in the review-trigger job.
allruns = "".join((s.get("run") or "") + " " + " ".join(str(v) for v in (s.get("env") or {}).values())
                  for s in d["jobs"]["review-trigger"]["steps"])
assert "github.event.pull_request.head.sha" not in allruns, \
    "review-trigger must not compare against the (stale) event head.sha"
# Per the verified GitHub Actions spec, a GITHUB_TOKEN push is NOT guaranteed to
# restart a pull_request workflow (those runs require approval and other token
# events don't create runs at all). So the fix must publish the real post-publish
# SHA and compare against it; the expected value must never be wired such that a
# stale review would be "fixed" by a later run restarting. We assert the publish
# job actually records the post-push SHA (done above) and that review-trigger
# treats the current head as authoritative only when it matches that SHA.
PY
}

# --- 30. review gate functional: API head vs published_sha -----------------------
# Functional test of the review-trigger "Verify PR head SHA" step (its run
# block) with a stubbed `gh`. Distinguishes the three contract cases:
#   - executed no-op:        current == published_sha == A -> match=1
#   - executed publication:  current == published_sha == B -> match=1
#   - job-level skip:        published_sha is EMPTY          -> match=0 (fail-closed)
# plus a post-publish head move -> match=0 (stale-review avoidance).
test_review_gate_contract() {
  local script d
  script="$(wf_step_run review-trigger "Verify PR head SHA")"
  [[ -n "$script" ]] || { fail "verify step missing from review-trigger"; return 1; }
  d="$ROOT_DIR/reviewgate"
  mkdir -p "$d"

  # run_gate <published_sha> <fake_api_head> -> runs the verify step block with
  # a stub `gh` that reports the fake API head; echoes the produced match line.
  run_gate() {
    local out log run
    out="$d/out.txt"; log="$d/log.txt"
    rm -f "$out" "$log"
    run=$'gh() { echo "$FAKE_CURRENT"; }\n'"$script"
    ( cd "$d" \
        && GITHUB_OUTPUT="$out" \
           GITHUB_REPOSITORY=owner/repo \
           PR_NUMBER=123 \
           PUBLISHED_SHA="$1" FAKE_CURRENT="$2" \
           bash -e -c "$run" >"$log" 2>&1 )
    grep -o '^match=[01]$' "$out" | tail -n1 || echo "no-match-line"
  }

  local log m
  # executed no-op: publish ran, current head == published_sha (A) -> success path
  m="$(run_gate "aaaa" "aaaa" || echo "no-match-line")"
  assert_eq "$m" "match=1" "executed no-op: matching head must yield match=1"
  # executed publication: publish ran + pushed B; API head == published_sha (B) -> success path
  m="$(run_gate "bbbb" "bbbb" || echo "no-match-line")"
  assert_eq "$m" "match=1" "executed publication: post-publish head must yield match=1"
  # head moved after publish -> fail-closed (stale-review avoidance)
  m="$(run_gate "bbbb" "cccc" || echo "no-match-line")"
  assert_eq "$m" "match=0" "moved head must yield match=0"
  # job-level skip: published_sha EMPTY -> match=0. The empty output is the
  # CORRECT contract for a skipped publish job and it must fail closed.
  m="$(run_gate "" "aaaa" || echo "no-match-line")"
  assert_eq "$m" "match=0" "job-level skip: empty published_sha must yield match=0"
  log="$d/log.txt"
  grep -qi "did not expose a published_sha output" "$log" \
    || fail "empty published_sha must log the fail-closed error"
}

# --- 31. workflow: job-level publish skip -> published_sha EMPTY + gate closed --
# CodeRabbit finding (PR #186): published_sha is NOT set on every path. A
# job-level skip of the publish JOB (fork PR, bootstrap run, upstream failure)
# means record-sha never executes, so
# needs.publish-readme.outputs.published_sha is EMPTY - and that is the CORRECT
# contract. Assert the workflow keeps it: the empty output is structurally
# guaranteed (record-sha is unreachable) and the review gate fails closed on it
# (no trigger without a real publish).
test_workflow_skip_contract() {
  python3 - "$WORKFLOW" <<'PY' || fail "job-level skip contract check failed"
import sys, yaml
d = yaml.safe_load(open(sys.argv[1]))
jobs = d["jobs"]
pub = jobs["publish-readme"]
pub_if = pub.get("if") or ""
# Fork PRs and bootstrap runs skip the publish JOB (not just its steps).
assert "github.event.pull_request.head.repo.full_name == github.repository" in pub_if, \
    "publish job must skip fork PRs at the job level"
assert "needs.update-readme.outputs.bootstrap == '0'" in pub_if, \
    "publish job must skip bootstrap runs at the job level"
# record-sha is inside the job with no step-level if, so a job-level skip makes
# it unreachable: no step output, hence no published_sha.
steps = pub["steps"]
record = next((s for s in steps if s.get("id") == "record-sha"), None)
assert record is not None, "publish job must end with a record-sha step"
assert "if" not in record, "record-sha has no step-level if"
assert pub["outputs"].get("published_sha") == "${{ steps.record-sha.outputs.sha }}", \
    "published_sha must come from the in-job record-sha step output"
# Gate semantics: a skipped publish job yields result 'skipped' (not 'success')
# and its outputs are empty strings, so both fail-closed checks must exist:
# the verify step maps an empty expected value to match=0, and the trigger
# additionally requires the publish result to be success.
verify = next(s for s in jobs["review-trigger"]["steps"] if "Verify PR head SHA" in (s.get("name") or ""))
vrun = verify.get("run") or ""
assert '-z "$expected"' in vrun, "verify must turn an empty published_sha into a fail-closed match=0"
trigger = next(s for s in jobs["review-trigger"]["steps"] if "Opt in and wait for CodeRabbit review" in (s.get("name") or ""))
cond = trigger.get("if") or ""
assert "steps.verify.outputs.match == '1'" in cond, \
    "trigger must require match == 1 (empty/mismatch -> match == 0 -> no trigger)"
assert "needs.publish-readme.result == 'success'" in cond, \
    "trigger must require publish-readme.result == success (a skipped publish job must never trigger a review)"
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
         test_opencode_permission_rules test_workflow_review_trigger test_workflow_head_move_fail_closed test_coderabbit_config \
         test_workflow_sha_flow test_publish_record_sha test_workflow_readme_changed_regression \
         test_review_gate_contract test_workflow_skip_contract)
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
