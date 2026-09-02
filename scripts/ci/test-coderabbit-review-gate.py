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

    def test_clean_direct_review_requires_current_sha(self):
        self.assertEqual(gate.classify([item(self.clean)], "a" * 40)[0], gate.ReviewState.COMPLETED_CLEAN)
        self.assertEqual(gate.classify([item(self.clean)], "b" * 40)[0], gate.ReviewState.STALE)

    def test_actionable_without_thread_is_still_blocking(self):
        state, _, _ = gate.classify([item(self.actionable)], "a" * 40)
        self.assertEqual(state, gate.ReviewState.COMPLETED_ACTIONABLE)

    def test_regression_decision_matrix(self):
        clean = gate.ReviewState.COMPLETED_CLEAN
        no_files = gate.ReviewState.NO_FILES_TO_REVIEW
        blocked = [
            (gate.ReviewState.COMPLETED_ACTIONABLE, False, False, 0, True),
            (clean, True, False, 1, True),       # unresolved thread
            (gate.ReviewState.COMPLETED_ACTIONABLE, False, False, 0, True), # outside diff
            (gate.ReviewState.SKIPPED, False, False, 0, True),
            (gate.ReviewState.MISSING, False, False, 0, True),
            (gate.ReviewState.PENDING, False, False, 0, True),
            (gate.ReviewState.FAILED, False, False, 0, True),
            (gate.ReviewState.STALE, False, False, 0, True),
            (no_files, False, False, 0, True),    # no prior clean review
            (no_files, False, True, 0, False),    # CI failed
            (no_files, False, True, 1, True),     # old unresolved blocker
            (gate.ReviewState.UNKNOWN, False, False, 0, True),
        ]
        for args in blocked:
            self.assertFalse(gate.gate_decision(*args), args)
        self.assertTrue(gate.gate_decision(clean, True, False, 0, True))
        self.assertTrue(gate.gate_decision(no_files, False, True, 0, True))
        self.assertFalse(gate.gate_decision(clean, False, False, 0, True)) # stale/different patch


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
