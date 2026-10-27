import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "services" / "ai"))

from app.session_runtime import JobState, SessionInferenceManager


class AiStopLifecycleTests(unittest.TestCase):
    def test_stop_acknowledges_quickly_drains_in_background_and_is_idempotent(self):
        manager = SessionInferenceManager.__new__(SessionInferenceManager)
        manager.jobs = {}
        manager.jobs_lock = threading.Lock()
        manager.stop_drain_timeout = 5.0
        terminal_callbacks = []
        manager._send_lost_for_all = lambda job: terminal_callbacks.append(job.job_id)
        manager._retry_failed_browser_chunks = lambda job: None
        manager._finalize_video_artifact = lambda job: None

        release_worker = threading.Event()
        worker_finished = threading.Event()
        with tempfile.TemporaryDirectory() as directory:
            job = JobState(
                job_id="session-30025-test",
                request=SimpleNamespace(sourceType="BROWSER", sessionId=30025),
                work_dir=Path(directory),
            )

            def slow_worker():
                release_worker.wait(2)
                worker_finished.set()

            job.worker = threading.Thread(target=slow_worker, daemon=True)
            manager.jobs[job.job_id] = job
            job.worker.start()

            started = time.monotonic()
            first = manager.stop(job.job_id)
            elapsed = time.monotonic() - started
            same_finalizer = job.finalizer
            second = manager.stop(job.job_id)

            self.assertLess(elapsed, 0.25)
            self.assertEqual("FINALIZING", first["status"])
            self.assertEqual("FINALIZING", second["status"])
            self.assertIs(same_finalizer, job.finalizer)
            self.assertFalse(worker_finished.is_set())
            self.assertEqual([], terminal_callbacks)

            release_worker.set()
            job.finalizer.join(3)
            self.assertEqual("COMPLETED", manager.job_status(job.job_id)["status"])
            self.assertEqual([job.job_id], terminal_callbacks)


if __name__ == "__main__":
    unittest.main()
