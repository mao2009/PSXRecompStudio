#!/usr/bin/env bash
#
# Local scenario tests for scripts/ci/readme-sync.sh
# (Issue #180 README auto-update; Issue #244 notify-instead-of-push).
# No network or GitHub access is required; the notify API is simulated through
# the GITHUB_API_ROOT directory seam.
#
# Scenarios:
#   1. preflight: proceed on a normal PR head
#   2. preflight: skip fork PRs
#   3. preflight: skip bot-authored head commit (loop prevention)
#   4. preflight: skip when PR repo context is missing
#   5. notify: candidate == head README -> no notification (no noise), no comment
#   6. notify: candidate != head README -> posts one advisory comment with the
#      marker + head SHA, artifact reference, and apply/decline instructions
#   7. notify: idempotent - a second run for the same head SHA does not duplicate
#   8. notify: does not modify the PR head branch or any managed file
#   9. notify: FAILS (refuses) in bootstrap mode (README_SYNC_BOOTSTRAP=1)
#  10. workflow YAML is well-formed
#  11. verify-config: OK for the pinned SSOT + opencode configs
#  12. verify-config: FAILS when the SSOT config model != opencode/big-pickle
#  13. verify-config: FAILS when the opencode config model != opencode/big-pickle
#  14. verify-config: FAILS when the opencode config small_model != opencode/big-pickle
#  15. verify-config: FAILS when the pinned version is not valid semver
#  16. workflow: NO repository-variable overrides (no `vars.`); model literal
#  17. workflow: model job contents:read + no GITHUB_TOKEN/PUSH_URL; notify job
#      contents:read + pull-requests:write with token only in the notify step;
#      notify needs the model job
#  18. workflow: bootstrap gating - notify steps skipped unless origin/main assets exist
#  19. workflow run-scripts: bash-n (and shellcheck+actionlint if installed) clean;
#      ASSETS extraction uses an array with "${ASSETS[@]}" (no unquoted expansion)
#  20. extraction step functional: origin/main assets -> bootstrap=0, PR-head
#      fallback -> bootstrap=1 (analyze-only bootstrap path)
#  21. model-output exporter functional: only-README change exports; other changes
#      or a deleted README fail closed
#  22. artifact validation functional: exactly one root README.md passes; extra
#      file, symlink, or subdirectory fail closed
#  23. opencode permission rules: catch-all first, specifics last
#  24. workflow: does NOT push a bot commit to the PR head branch (Issue #244);
#      no git push / no commit authoring in the workflow
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

# --- 5. notify: candidate == head README -> no notification (no noise) -----
test_notify_no_noise() {
  setup_repo t5
  local d
  d="$ROOT_DIR/t5/api"; mkdir -p "$d"
  cp "$WORK/README.md" "$d/candidate.md"
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" GITHUB_REPOSITORY=owner/repo \
      PR_NUMBER=5 PR_HEAD_SHA="$(git rev-parse HEAD)" GITHUB_RUN_ID=123 \
      CANDIDATE_README="$d/candidate.md" GITHUB_API_ROOT="$d" \
      bash "$SYNC" notify ) >/dev/null
  local ec=$?
  assert_eq 0 "$ec" "notify must succeed when README is consistent"
  [[ -f "$d/comments.json" ]] && [[ -s "$d/comments.json" ]] \
    && fail "no comment may be posted when the candidate matches the head README"
}

# --- 6. notify: candidate != head README -> posts advisory comment ----------
test_notify_posts_when_changed() {
  setup_repo t6
  local d headsha
  d="$ROOT_DIR/t6/api"; mkdir -p "$d"
  printf '# Demo\n\n更新ノート：Phase 2Aを追加。\n' > "$d/candidate.md"
  headsha="$(git -C "$WORK" rev-parse HEAD)"
  local before after
  before="$(git ls-remote "$REMOTE" refs/heads/pr-branch)"
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" GITHUB_REPOSITORY=owner/repo \
      PR_NUMBER=6 PR_HEAD_SHA="$headsha" GITHUB_RUN_ID=456 \
      CANDIDATE_README="$d/candidate.md" GITHUB_API_ROOT="$d" \
      bash "$SYNC" notify ) >/dev/null
  local ec=$?
  assert_eq 0 "$ec" "notify must succeed when a candidate differs"
  after="$(git ls-remote "$REMOTE" refs/heads/pr-branch)"
  assert_eq "$before" "$after" "notify must NOT change the PR head branch"
  [[ -f "$d/comments.json" ]] || fail "a comment must be posted"
  grep -qsF "<!-- README-AUTOUPDATE-CANDIDATE:" "$d/comments.json" \
    || fail "comment must contain the candidate marker"
  grep -qsF "$headsha" "$d/comments.json" \
    || fail "comment must reference the head SHA"
  grep -qi "apply\|decline\|not a merge gate" "$d/comments.json" \
    || fail "comment must contain apply/decline guidance"
  grep -q "更新ノート" "$WORK/README.md" \
    && fail "the checked-out README must NOT be modified by notify"
}

# --- 7. notify: idempotent per (marker, head SHA) ---------------------------
test_notify_idempotent() {
  setup_repo t7
  local d headsha
  d="$ROOT_DIR/t7/api"; mkdir -p "$d"
  printf '# Demo\n\nchanged again\n' > "$d/candidate.md"
  headsha="$(git -C "$WORK" rev-parse HEAD)"
  local run1
  run1="$(cd "$WORK" && env README_SYNC_CONFIG="$CONFIG" GITHUB_REPOSITORY=owner/repo \
      PR_NUMBER=7 PR_HEAD_SHA="$headsha" GITHUB_RUN_ID=789 \
      CANDIDATE_README="$d/candidate.md" GITHUB_API_ROOT="$d" \
      bash "$SYNC" notify)"
  local run2
  run2="$(cd "$WORK" && env README_SYNC_CONFIG="$CONFIG" GITHUB_REPOSITORY=owner/repo \
      PR_NUMBER=7 PR_HEAD_SHA="$headsha" GITHUB_RUN_ID=790 \
      CANDIDATE_README="$d/candidate.md" GITHUB_API_ROOT="$d" \
      bash "$SYNC" notify)"
  local count
  count="$(grep -c "<!-- README-AUTOUPDATE-CANDIDATE:" "$d/comments.json" || true)"
  assert_eq 1 "$count" "a second notify run for the same head SHA must not duplicate the comment"
}

# --- 8. notify: bootstrap mode -> refused (no token-backed comment) ----------
test_notify_bootstrap_refused() {
  setup_repo t8
  local d headsha ec
  d="$ROOT_DIR/t8/api"; mkdir -p "$d"
  printf '# Demo\n\nbootstrap candidate\n' > "$d/candidate.md"
  headsha="$(git -C "$WORK" rev-parse HEAD)"
  ( cd "$WORK" && README_SYNC_CONFIG="$CONFIG" GITHUB_REPOSITORY=owner/repo \
      PR_NUMBER=8 PR_HEAD_SHA="$headsha" GITHUB_RUN_ID=1 \
      CANDIDATE_README="$d/candidate.md" GITHUB_API_ROOT="$d" \
      README_SYNC_BOOTSTRAP=1 bash "$SYNC" notify ) >/dev/null 2>&1
  ec=$?
  assert_ne 0 "$ec" "notify must refuse in bootstrap mode"
  [[ -f "$d/comments.json" ]] && [[ -s "$d/comments.json" ]] \
    && fail "bootstrap mode must never post a comment"
}

# --- 9. workflow structure: triggers + two-job least-privilege scope ---------
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
assert jobs["notify-readme"]["permissions"] == {"contents": "read", "pull-requests": "write"}, "notify job: contents read + pull-requests write"
assert jobs["notify-readme"]["needs"] == "update-readme", "notify job must depend on the model job"
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

# --- notify bootstrap refusal is covered by test #8 ---

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
  local model_perm notify_perm
  python3 - "$WORKFLOW" <<'PY' || fail "workflow structure check failed"
import sys, yaml
d = yaml.safe_load(open(sys.argv[1]))
jobs = d["jobs"]
assert set(jobs) == {"update-readme", "notify-readme"}, "README updater must have only model and notify jobs"
assert jobs["update-readme"]["permissions"] == {"contents": "read"}
assert jobs["notify-readme"]["permissions"] == {"contents": "read", "pull-requests": "write"}
assert jobs["notify-readme"].get("needs") == "update-readme"
for job in jobs.values():
    for step in job["steps"]:
        blob = (step.get("run") or "") + " " + " ".join(str(v) for v in (step.get("env") or {}).values())
        if "github.token" in blob and step.get("name") != "Notify README candidate":
            raise AssertionError("GITHUB_TOKEN referenced outside the notify step")
PY
  model_perm="$(python3 - "$WORKFLOW" <<'PY'
import sys, yaml
print(yaml.safe_load(open(sys.argv[1]))["jobs"]["update-readme"]["permissions"]["contents"])
PY
)"
  notify_perm="$(python3 - "$WORKFLOW" <<'PY'
import sys, yaml
print(yaml.safe_load(open(sys.argv[1]))["jobs"]["notify-readme"]["permissions"]["pull-requests"])
PY
)"
  assert_eq "$model_perm" "read" "model job permission must be contents:read"
  assert_eq "$notify_perm" "write" "notify job permission must grant pull-requests:write"
}

# --- 19. workflow: bootstrap gating on notify steps --------------------------
test_workflow_bootstrap_gating() {
  local job_if cond run
  job_if="$(python3 - "$WORKFLOW" <<'PY'
import sys, yaml
d = yaml.safe_load(open(sys.argv[1]))
print(d["jobs"]["notify-readme"].get("if") or "")
PY
)"
  if [[ "$job_if" != *"needs.update-readme.outputs.bootstrap == '0'"* ]]; then
    fail "notify job must be gated on the model job's bootstrap output (bootstrap runs stay analyze-only)"
  fi
  cond="$(wf_step_if notify-readme "Notify README candidate")"
  if [[ "$cond" != *"steps.extract.outputs.bootstrap == '0'"* ]]; then
    fail "notify step must be gated on bootstrap=0"
  fi
  cond="$(wf_step_if notify-readme "Download candidate README artifact")"
  if [[ "$cond" != *"steps.extract.outputs.bootstrap == '0'"* ]]; then
    fail "artifact download must be gated on bootstrap=0"
  fi
  run="$(wf_step_run notify-readme "Extract trusted assets")"
  if ! echo "$run" | grep -q 'git archive origin/main "${ASSETS\[@\]}"'; then
    fail "notify extraction must use origin/main with the quoted asset array"
  fi
  if ! echo "$run" | grep -q '::warning::BOOTSTRAP'; then
    fail "notify extraction must emit a loud bootstrap warning instead of falling back"
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
  run_pub="$(wf_step_run notify-readme "Extract trusted assets")"
  if ! echo "$run_pub" | grep -q 'git archive origin/main "${ASSETS\[@\]}"'; then
    fail "notify extract step must use the quoted array expansion in git archive"
  fi
  [[ $failed -eq 0 ]]
}

# --- 21. workflow: no bot push to the PR head branch (Issue #244) -----------
test_workflow_no_bot_push() {
  # The workflow must never author a bot commit or push to the PR head branch.
  # A github-actions[bot]-authored PR HEAD leaves required CI in action_required
  # with 0 jobs, permanently blocking the merge gate (Issue #244). README
  # changes are surfaced as an advisory comment only.
  if grep -qE 'git push|git commit -m "docs: update README"|github-actions\[bot\].*commit|contents: write' "$WORKFLOW"; then
    fail "workflow must not push a bot-authored commit to the PR head branch (Issue #244)"
  fi
  local notify_run
  notify_run="$(wf_step_run notify-readme "Notify README candidate")"
  if ! echo "$notify_run" | grep -q 'readme-sync.sh" notify'; then
    fail "notify step must invoke readme-sync.sh notify (comment-only, no push)"
  fi
  if echo "$notify_run" | grep -q 'publish'; then
    fail "notify step must not invoke a publish path"
  fi
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
  script="$(wf_step_run notify-readme "Validate artifact")"
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

# --- CodeRabbit configuration and workflow independence --------------------
test_coderabbit_config() {
  python3 - "$SCRIPT_DIR/../../.coderabbit.yaml" "$WORKFLOW" <<'PY' || fail "CodeRabbit independence contract failed"
import sys, yaml
config = yaml.safe_load(open(sys.argv[1]))
auto = config["reviews"]["auto_review"]
assert auto["enabled"] is True
assert auto["auto_incremental_review"] is True
assert auto["auto_pause_after_reviewed_commits"] == 0
assert "description_keyword" not in auto
workflow = open(sys.argv[2]).read()
assert "review-trigger" not in workflow
assert "@coderabbitai review" not in workflow
assert "coderabbit-review-gate" not in workflow
assert "published_sha" not in workflow
assert "CodeRabbit" not in workflow
PY
}

main() {
  bash -n "$SYNC" || { echo "syntax error in readme-sync.sh"; exit 1; }
  tests=(test_preflight_proceed test_preflight_fork test_preflight_bot_loop \
         test_preflight_missing_context test_notify_no_noise \
         test_notify_posts_when_changed test_notify_idempotent \
         test_notify_bootstrap_refused \
         test_workflow_yaml test_verify_config_ok \
         test_verify_config_rejects_ssot_model test_verify_config_rejects_opencode_model \
         test_verify_config_rejects_small_model test_verify_config_rejects_bad_version \
         test_workflow_no_vars test_workflow_token_boundary \
         test_workflow_bootstrap_gating test_workflow_run_scripts \
         test_workflow_no_bot_push \
         test_workflow_extract_functional test_model_exporter_gate test_artifact_validation \
         test_opencode_permission_rules test_coderabbit_config)
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
