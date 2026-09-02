#!/usr/bin/env python3
import importlib.util
import tempfile
import unittest
from pathlib import Path
from subprocess import run

MODULE = Path(__file__).with_name("coderabbit-review-gate.py")
spec = importlib.util.spec_from_file_location("coderabbit_gate", MODULE)
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)
THREAD_MODULE = Path(__file__).with_name("collect-review-threads.py")
thread_spec = importlib.util.spec_from_file_location("review_threads", THREAD_MODULE)
threads = importlib.util.module_from_spec(thread_spec)
thread_spec.loader.exec_module(threads)


def item(body, oid="a" * 40):
    return {"author": {"login": "coderabbitai"}, "body": body,
            "commit": {"oid": oid}, "submittedAt": "2026-01-01T00:00:00Z"}


class StateModelTests(unittest.TestCase):
    clean = "No actionable comments were generated in the recent review."
    actionable = "Actionable comments posted: 1"

    def test_fail_closed_states(self):
        cases = {
            "": gate.ReviewState.MISSING,
            "Review skipped": gate.ReviewState.SKIPPED,
            "Review is pending": gate.ReviewState.PENDING,
            "Action performed\nReview finished.": gate.ReviewState.UNKNOWN,
            self.actionable: gate.ReviewState.COMPLETED_ACTIONABLE,
            "No files to review.": gate.ReviewState.NO_FILES_TO_REVIEW,
        }
        for body, expected in cases.items():
            actual, _, _ = gate.classify([] if not body else [item(body)], "a" * 40)
            self.assertEqual(actual, expected, body)

    def test_no_files_response_requires_current_head_binding(self):
        current = "a" * 40
        previous = "b" * 40
        missing = item("No files to review.")
        missing.pop("commit")
        stale = item(f"No files to review. Reviewed commit: {previous}", previous)
        bound = item(f"No files to review. Reviewed commit: {current}", current)

        self.assertEqual(gate.classify([missing], current)[0], gate.ReviewState.UNKNOWN)
        self.assertEqual(gate.classify([stale], current)[0], gate.ReviewState.STALE)
        self.assertEqual(gate.classify([bound], current)[0], gate.ReviewState.NO_FILES_TO_REVIEW)

    def test_clean_direct_review_requires_current_sha(self):
        self.assertEqual(gate.classify([item(self.clean)], "a" * 40)[0], gate.ReviewState.COMPLETED_CLEAN)
        self.assertEqual(gate.classify([item(self.clean)], "b" * 40)[0], gate.ReviewState.STALE)

    def test_edited_summary_is_newer_than_later_created_ack(self):
        head = "a" * 40
        summary = {
            "user": {"login": "coderabbitai[bot]"},
            "body": f"{self.clean}\nReviewing files between {'b' * 40} and {head}.",
            "created_at": "2026-01-01T00:00:00Z",
            "updated_at": "2026-01-01T00:03:00Z",
        }
        acknowledgement = {
            "user": {"login": "coderabbitai[bot]"},
            "body": "Action performed\nReview finished.",
            "created_at": "2026-01-01T00:02:00Z",
            "updated_at": "2026-01-01T00:02:00Z",
        }
        items = gate.coderabbit_items([], [summary, acknowledgement])
        self.assertIs(items[-1], summary)
        self.assertEqual(gate.classify(items, head)[0], gate.ReviewState.COMPLETED_CLEAN)

    def test_clean_comment_without_head_binding_blocks(self):
        comment = {
            "user": {"login": "coderabbitai[bot]"},
            "body": self.clean,
            "created_at": "2026-01-01T00:00:00Z",
            "updated_at": "2026-01-01T00:01:00Z",
        }
        state, _, reason = gate.classify(gate.coderabbit_items([], [comment]), "a" * 40)
        self.assertEqual(state, gate.ReviewState.UNKNOWN)
        self.assertIn("binding", reason)

    def test_actionable_without_thread_is_still_blocking(self):
        state, _, _ = gate.classify([item(self.actionable)], "a" * 40)
        self.assertEqual(state, gate.ReviewState.COMPLETED_ACTIONABLE)

    def test_regression_decision_matrix(self):
        clean = gate.ReviewState.COMPLETED_CLEAN
        no_files = gate.ReviewState.NO_FILES_TO_REVIEW
        blocked = [
            (gate.ReviewState.COMPLETED_ACTIONABLE, False, False, 0, True),
            (clean, True, False, 1, True),
            (gate.ReviewState.COMPLETED_ACTIONABLE, False, False, 0, True),
            (gate.ReviewState.SKIPPED, False, False, 0, True),
            (gate.ReviewState.MISSING, False, False, 0, True),
            (gate.ReviewState.PENDING, False, False, 0, True),
            (gate.ReviewState.FAILED, False, False, 0, True),
            (gate.ReviewState.STALE, False, False, 0, True),
            (no_files, False, False, 0, True),
            (no_files, False, True, 0, False),
            (no_files, False, True, 1, True),
            (gate.ReviewState.UNKNOWN, False, False, 0, True),
        ]
        for args in blocked:
            self.assertFalse(gate.gate_decision(*args), args)
        self.assertTrue(gate.gate_decision(clean, True, False, 0, True))
        self.assertTrue(gate.gate_decision(no_files, False, True, 0, True))
        self.assertFalse(gate.gate_decision(clean, False, False, 0, True))


class RequiredCICheckTests(unittest.TestCase):
    names = [
        "Artifact Contamination Gate", "Native Core Build and Test",
        ".NET Build and Test", "CI Gate",
    ]

    def records(self):
        return [{"name": name, "status": "COMPLETED", "conclusion": "SUCCESS"}
                for name in self.names]

    def test_four_exact_success_records_pass(self):
        self.assertTrue(gate.required_ci_checks_pass(self.records()))

    def test_github_lowercase_success_records_pass_after_normalization(self):
        records = [{"name": name, "status": "completed", "conclusion": "success"}
                   for name in self.names]
        normalized = [{**record,
                       "status": record["status"].upper(),
                       "conclusion": record["conclusion"].upper()}
                      for record in records]
        self.assertTrue(gate.required_ci_checks_pass(normalized))

    def test_in_progress_and_queued_null_conclusion_block(self):
        for status in ("IN_PROGRESS", "QUEUED"):
            records = self.records()
            records[0].update(status=status, conclusion="")
            self.assertFalse(gate.required_ci_checks_pass(records), status)

    def test_completed_non_success_conclusions_block(self):
        for conclusion in ("FAILURE", "CANCELLED", "SKIPPED", "NEUTRAL",
                           "TIMED_OUT", "ACTION_REQUIRED"):
            records = self.records()
            records[0]["conclusion"] = conclusion
            self.assertFalse(gate.required_ci_checks_pass(records), conclusion)

    def test_missing_required_record_blocks_even_with_duplicate_success(self):
        records = [record for record in self.records()
                   if record["name"] != "CI Gate"]
        records.append({"name": "Artifact Contamination Gate", "status": "COMPLETED", "conclusion": "SUCCESS"})
        self.assertFalse(gate.required_ci_checks_pass(records))

    def test_duplicate_required_name_blocks(self):
        records = self.records() + [self.records()[0]]
        self.assertFalse(gate.required_ci_checks_pass(records))

    def test_duplicate_disagreement_blocks(self):
        records = self.records() + [{"name": "CI Gate", "status": "COMPLETED", "conclusion": "FAILURE"}]
        self.assertFalse(gate.required_ci_checks_pass(records))

    def test_similar_name_does_not_substitute_for_required_name(self):
        records = [record for record in self.records() if record["name"] != "CI Gate"]
        records.append({"name": "CI Gate (legacy)", "status": "COMPLETED", "conclusion": "SUCCESS"})
        self.assertFalse(gate.required_ci_checks_pass(records))

    def test_unrelated_success_is_ignored(self):
        records = self.records() + [{"name": "lint", "status": "COMPLETED", "conclusion": "SUCCESS"}]
        self.assertTrue(gate.required_ci_checks_pass(records))

    def test_failure_cancelled_pending_and_in_progress_block(self):
        for status, conclusion in [("COMPLETED", "FAILURE"),
                                   ("COMPLETED", "CANCELLED"),
                                   ("PENDING", ""),
                                   ("IN_PROGRESS", "")]:
            records = self.records()
            records[0].update(status=status, conclusion=conclusion)
            self.assertFalse(gate.required_ci_checks_pass(records), (status, conclusion))

    def test_malformed_payload_and_record_block(self):
        self.assertFalse(gate.required_ci_checks_pass(None))
        records = self.records()
        del records[0]["conclusion"]
        self.assertFalse(gate.required_ci_checks_pass(records))

        records = self.records()
        records[0]["status"] = None
        self.assertFalse(gate.required_ci_checks_pass(records))

        records = self.records()
        records[0]["conclusion"] = None
        self.assertFalse(gate.required_ci_checks_pass(records))

        records = self.records()
        records[0]["status"] = 1
        self.assertFalse(gate.required_ci_checks_pass(records))


class PatchIdentityTests(unittest.TestCase):
    def test_content_and_metadata_are_identity_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            run(["git", "init", "-q", str(repo)], check=True)
            run(["git", "-C", str(repo), "config", "user.email", "test@example.com"], check=True)
            run(["git", "-C", str(repo), "config", "user.name", "Test"], check=True)
            (repo / "file.txt").write_text("one\n")
            run(["git", "-C", str(repo), "add", "file.txt"], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "base"], check=True)
            base = run(["git", "-C", str(repo), "rev-parse", "HEAD"], check=True, text=True, capture_output=True).stdout.strip()
            (repo / "file.txt").write_text("two\n")
            run(["git", "-C", str(repo), "add", "file.txt"], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "change"], check=True)
            head = run(["git", "-C", str(repo), "rev-parse", "HEAD"], check=True, text=True, capture_output=True).stdout.strip()
            first = gate.patch_identity(repo, base, head)
            (repo / "file.txt").chmod(0o755)
            run(["git", "-C", str(repo), "add", "file.txt"], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "mode"], check=True)
            changed = gate.patch_identity(repo, base, run(["git", "-C", str(repo), "rev-parse", "HEAD"], check=True, text=True, capture_output=True).stdout.strip())
            self.assertNotEqual(first["files"], changed["files"])


class EquivalentRebaseTests(unittest.TestCase):
    clean = "No actionable comments were generated in the recent review."

    def test_incremental_clean_range_uses_full_historical_pr_patch(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            run(["git", "init", "-q", str(repo)], check=True)
            run(["git", "-C", str(repo), "config", "user.email", "test@example.com"], check=True)
            run(["git", "-C", str(repo), "config", "user.name", "Test"], check=True)

            (repo / "base.txt").write_text("base\n")
            run(["git", "-C", str(repo), "add", "."], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "base"], check=True)
            old_base = run(["git", "-C", str(repo), "rev-parse", "HEAD"], check=True, text=True, capture_output=True).stdout.strip()
            base_branch = run(
                ["git", "-C", str(repo), "symbolic-ref", "--short", "HEAD"],
                check=True, text=True, capture_output=True
            ).stdout.strip()

            run(["git", "-C", str(repo), "checkout", "-qb", "feature"], check=True)
            (repo / "one.txt").write_text("one\n")
            run(["git", "-C", str(repo), "add", "."], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "first feature change"], check=True)
            incremental_base = run(["git", "-C", str(repo), "rev-parse", "HEAD"], check=True, text=True, capture_output=True).stdout.strip()
            (repo / "two.txt").write_text("two\n")
            run(["git", "-C", str(repo), "add", "."], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "reviewed feature head"], check=True)
            reviewed_head = run(["git", "-C", str(repo), "rev-parse", "HEAD"], check=True, text=True, capture_output=True).stdout.strip()

            run(["git", "-C", str(repo), "checkout", "-q", base_branch], check=True)
            (repo / "main.txt").write_text("main advanced\n")
            run(["git", "-C", str(repo), "add", "."], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "advance main"], check=True)
            current_base = run(["git", "-C", str(repo), "rev-parse", "HEAD"], check=True, text=True, capture_output=True).stdout.strip()

            run(["git", "-C", str(repo), "checkout", "-q", "feature"], check=True)
            run(["git", "-C", str(repo), "rebase", base_branch], check=True)
            current_head = run(["git", "-C", str(repo), "rev-parse", "HEAD"], check=True, text=True, capture_output=True).stdout.strip()
            current = gate.patch_identity(repo, current_base, current_head)

            summary = item(
                f"{self.clean}\nReviewing files that changed from the base of the PR and between "
                f"{incremental_base} and {reviewed_head}.",
                reviewed_head,
            )
            self.assertNotEqual(
                gate.patch_identity(repo, incremental_base, reviewed_head)["files"],
                current["files"],
                "regression fixture must prove the incremental range is not the full PR patch",
            )
            self.assertTrue(gate.clean_prior([summary], current, repo))

    def test_equivalent_rebase_snapshot_matches_even_when_commit_identity_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            run(["git", "init", "-q", str(repo)], check=True)
            run(["git", "-C", str(repo), "config", "user.email", "test@example.com"], check=True)
            run(["git", "-C", str(repo), "config", "user.name", "Test"], check=True)
            (repo / "base.txt").write_text("base\n")
            run(["git", "-C", str(repo), "add", "."], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "base"], check=True)
            base_branch = run(
                ["git", "-C", str(repo), "symbolic-ref", "--short", "HEAD"],
                check=True, text=True, capture_output=True
            ).stdout.strip()
            old_base = run(["git", "-C", str(repo), "rev-parse", "HEAD"],
                           check=True, text=True, capture_output=True).stdout.strip()

            run(["git", "-C", str(repo), "checkout", "-qb", "feature"], check=True)
            (repo / "feature.txt").write_text("reviewed\n")
            run(["git", "-C", str(repo), "add", "."], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "feature"], check=True)
            reviewed_head = run(["git", "-C", str(repo), "rev-parse", "HEAD"],
                                check=True, text=True, capture_output=True).stdout.strip()

            run(["git", "-C", str(repo), "checkout", "-q", base_branch], check=True)
            (repo / "unrelated.txt").write_text("main advanced\n")
            run(["git", "-C", str(repo), "add", "."], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "advance main"], check=True)
            current_base = run(["git", "-C", str(repo), "rev-parse", "HEAD"],
                               check=True, text=True, capture_output=True).stdout.strip()

            run(["git", "-C", str(repo), "checkout", "-q", "feature"], check=True)
            run(["git", "-C", str(repo), "rebase", base_branch], check=True)
            current_head = run(["git", "-C", str(repo), "rev-parse", "HEAD"],
                               check=True, text=True, capture_output=True).stdout.strip()

            old = gate.patch_identity(repo, old_base, reviewed_head)
            current = gate.patch_identity(repo, current_base, current_head)
            self.assertNotEqual(reviewed_head, current_head)
            self.assertEqual(old["snapshot"], current["snapshot"])

    def test_real_content_change_after_rebase_invalidates_snapshot(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            run(["git", "init", "-q", str(repo)], check=True)
            run(["git", "-C", str(repo), "config", "user.email", "test@example.com"], check=True)
            run(["git", "-C", str(repo), "config", "user.name", "Test"], check=True)
            (repo / "file.txt").write_text("base\n")
            run(["git", "-C", str(repo), "add", "."], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "base"], check=True)
            base = run(["git", "-C", str(repo), "rev-parse", "HEAD"],
                       check=True, text=True, capture_output=True).stdout.strip()
            (repo / "file.txt").write_text("reviewed\n")
            run(["git", "-C", str(repo), "add", "."], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "reviewed"], check=True)
            reviewed = run(["git", "-C", str(repo), "rev-parse", "HEAD"],
                           check=True, text=True, capture_output=True).stdout.strip()
            old = gate.patch_identity(repo, base, reviewed)

            (repo / "file.txt").write_text("changed after review\n")
            run(["git", "-C", str(repo), "add", "."], check=True)
            run(["git", "-C", str(repo), "commit", "-qm", "unreviewed change"], check=True)
            changed = run(["git", "-C", str(repo), "rev-parse", "HEAD"],
                          check=True, text=True, capture_output=True).stdout.strip()
            current = gate.patch_identity(repo, base, changed)
            self.assertNotEqual(old["snapshot"], current["snapshot"])


class ReviewThreadPaginationTests(unittest.TestCase):
    @staticmethod
    def page(nodes, has_next=False, end_cursor=None):
        return {"data": {"repository": {"pullRequest": {
            "reviewThreads": {"nodes": nodes,
                               "pageInfo": {"hasNextPage": has_next,
                                             "endCursor": end_cursor}}
        }}}}

    def test_exactly_one_hundred_resolved_threads(self):
        calls = []
        resolved = [{"isResolved": True} for _ in range(100)]
        result = threads.collect(lambda cursor: calls.append(cursor) or self.page(resolved))
        self.assertEqual(len(result), 100)
        self.assertEqual(calls, [None])

    def test_unresolved_thread_on_second_page_blocks(self):
        pages = {
            None: self.page([{"isResolved": True}] * 100, True, "cursor-1"),
            "cursor-1": self.page([{"isResolved": False}]),
        }
        result = threads.collect(pages.__getitem__)
        self.assertEqual(sum(not node["isResolved"] for node in result), 1)

    def test_multiple_resolved_pages_complete(self):
        pages = {
            None: self.page([{"isResolved": True}] * 100, True, "cursor-1"),
            "cursor-1": self.page([{"isResolved": True}] * 100, True, "cursor-2"),
            "cursor-2": self.page([{"isResolved": True}] * 3),
        }
        result = threads.collect(pages.__getitem__)
        self.assertEqual(len(result), 203)
        self.assertFalse(any(not node["isResolved"] for node in result))

    def test_api_failure_after_first_page_fails_closed(self):
        def fetch(cursor):
            if cursor is None:
                return self.page([{"isResolved": True}], True, "cursor-1")
            raise RuntimeError("API failure")
        with self.assertRaises(RuntimeError):
            threads.collect(fetch)

    def test_missing_cursor_with_next_page_fails_closed(self):
        with self.assertRaises(ValueError):
            threads.collect(lambda _: self.page([], True, None))

    def test_malformed_node_fails_closed(self):
        with self.assertRaises(ValueError):
            threads.collect(lambda _: self.page([{"isResolved": "unknown"}]))


if __name__ == "__main__":
    unittest.main()
