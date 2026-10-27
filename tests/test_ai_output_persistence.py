import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "services" / "ai"))

from app.session_runtime import JobState, SessionInferenceManager


class _Response:
    def raise_for_status(self):
        return None


class _Client:
    uploaded = b""

    def __init__(self, *args, **kwargs):
        pass

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def put(self, _url, content, headers):
        _Client.uploaded = b"".join(content)
        return _Response()


class AiOutputPersistenceTests(unittest.TestCase):
    def setUp(self):
        _Client.uploaded = b""

    def manager(self):
        manager = SessionInferenceManager.__new__(SessionInferenceManager)
        manager.jobs = {}
        manager.jobs_lock = threading.Lock()
        manager.browser_queue_size = 1
        return manager

    @staticmethod
    def mark_rendered_annotations(job):
        job.annotation_clips = dict(job.recording_clips)
        job.annotation_stats = {
            index: {"frames": 1, "labeledFrames": 1, "boxes": 1}
            for index in job.recording_clips
        }

    def test_full_queue_rejects_new_chunk_without_evicting_accepted_chunk(self):
        manager = self.manager()
        with tempfile.TemporaryDirectory() as directory:
            job = JobState(
                "job-browser",
                SimpleNamespace(sourceType="BROWSER", sessionId=30023),
                Path(directory),
            )
            manager.jobs[job.job_id] = job

            first = manager.enqueue_browser_chunk(job.job_id, b"first", ".webm", "webcam-000000.webm")
            repeated = manager.enqueue_browser_chunk(job.job_id, b"first", ".webm", "webcam-000000.webm")
            with self.assertRaisesRegex(RuntimeError, "retry this chunk"):
                manager.enqueue_browser_chunk(job.job_id, b"second", ".webm", "webcam-000001.webm")

            self.assertTrue(first["queued"])
            self.assertEqual(first, repeated)
            self.assertFalse(first["droppedOldest"])
            self.assertEqual(1, job.accepted_chunks)
            index, path = job.browser_queue.get_nowait()
            self.assertEqual(0, index)
            self.assertEqual(b"first", path.read_bytes())

    def test_original_clip_cannot_be_uploaded_as_an_annotation_artifact(self):
        manager = self.manager()
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            original = work / "camera-original.mp4"
            original.write_bytes(b"original-camera-pixels")
            target = SimpleNamespace(
                objectKey="annotations/sessions/30022/corr.mp4",
                uploadUrl="http://storage/upload",
                fileName="buoi-hoc-30022-annotations.mp4",
                contentType="video/mp4",
            )
            job = JobState(
                "job-original-only",
                SimpleNamespace(sourceType="CAMERA", sessionId=30022, annotatedVideoUpload=target),
                work,
            )
            job.recording_clips = {0: original}

            with self.assertRaisesRegex(RuntimeError, "No successfully rendered annotation chunks"):
                manager._finalize_video_artifact(job)

            self.assertIsNone(job.artifact)
            self.assertTrue(original.exists())

    def test_browser_clips_are_assembled_uploaded_and_reported_as_artifact(self):
        manager = self.manager()
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            first = work / "browser_chunk_000000.mp4"
            second = work / "browser_chunk_000001.mp4"
            first.write_bytes(b"clip-one")
            second.write_bytes(b"clip-two")
            target = SimpleNamespace(
                objectKey="annotations/sessions/30023/corr.mp4",
                uploadUrl="http://storage/upload",
                fileName="buoi-hoc-30023.mp4",
                contentType="video/mp4",
            )
            job = JobState(
                "job-browser",
                SimpleNamespace(sourceType="BROWSER", sessionId=30023, annotatedVideoUpload=target),
                work,
            )
            job.recording_clips = {0: first, 1: second}
            self.mark_rendered_annotations(job)

            encoded = self._mp4_bytes()
            commands = []

            def fake_run(command, **_kwargs):
                commands.append(command)
                if command[0] == "ffprobe":
                    is_output = ".tmp.mp4" in str(command[-1])
                    return SimpleNamespace(
                        returncode=0,
                        stderr="",
                        stdout=json.dumps(self._probe(is_output=is_output, with_audio=False)),
                    )
                Path(command[-1]).write_bytes(encoded)
                return SimpleNamespace(returncode=0, stderr="", stdout="")

            with patch("app.session_runtime.subprocess.run", side_effect=fake_run), patch(
                "app.session_runtime.httpx.Client", _Client
            ):
                manager._finalize_video_artifact(job)

            self.assertEqual(encoded, _Client.uploaded)
            self.assertEqual(len(encoded), job.artifact["sizeBytes"])
            self.assertEqual(hashlib.sha256(encoded).hexdigest(), job.artifact["sha256"])
            self.assertEqual(target.objectKey, job.artifact["objectKey"])
            ffmpeg_command = next(command for command in commands if command[0] == "ffmpeg")
            self.assertIn("libx264", ffmpeg_command)
            self.assertIn("yuv420p", ffmpeg_command)
            self.assertIn("avc1", ffmpeg_command)
            self.assertIn("+faststart", ffmpeg_command)
            self.assertIn("-an", ffmpeg_command)
            self.assertNotIn("-c:a", ffmpeg_command)
            self.assertNotIn("-b:a", ffmpeg_command)
            self.assertFalse(first.exists())
            self.assertFalse(second.exists())
            self.assertEqual([], list(work.glob("recording-concat-*.txt")))
            self.assertEqual([], list(work.glob("session-recording-*.tmp.mp4")))

    def test_audio_is_encoded_as_aac_only_when_source_has_audio(self):
        manager = self.manager()
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            clip = work / "browser_chunk_000000.mp4"
            clip.write_bytes(b"clip-with-audio")
            target = SimpleNamespace(
                objectKey="annotations/sessions/30024/corr.mp4",
                uploadUrl="http://storage/upload",
                fileName="buoi-hoc-30024.mp4",
                contentType="video/mp4",
            )
            job = JobState(
                "job-browser-audio",
                SimpleNamespace(sourceType="BROWSER", sessionId=30024, annotatedVideoUpload=target),
                work,
            )
            job.recording_clips = {0: clip}
            self.mark_rendered_annotations(job)
            encoded = self._mp4_bytes()
            ffmpeg_commands = []

            def fake_run(command, **_kwargs):
                if command[0] == "ffprobe":
                    is_output = ".tmp.mp4" in str(command[-1])
                    return SimpleNamespace(
                        returncode=0,
                        stderr="",
                        stdout=json.dumps(self._probe(is_output=is_output, with_audio=True)),
                    )
                ffmpeg_commands.append(command)
                Path(command[-1]).write_bytes(encoded)
                return SimpleNamespace(returncode=0, stderr="", stdout="")

            with patch("app.session_runtime.subprocess.run", side_effect=fake_run), patch(
                "app.session_runtime.httpx.Client", _Client
            ):
                manager._finalize_video_artifact(job)

            command = ffmpeg_commands[0]
            self.assertIn("-c:a", command)
            self.assertEqual("aac", command[command.index("-c:a") + 1])
            self.assertIn("-b:a", command)
            self.assertNotIn("-an", command)

    def test_invalid_transcode_is_not_uploaded_and_sources_are_preserved_for_retry(self):
        manager = self.manager()
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            clip = work / "browser_chunk_000000.mp4"
            clip.write_bytes(b"retryable-source")
            target = SimpleNamespace(
                objectKey="annotations/sessions/30025/corr.mp4",
                uploadUrl="http://storage/upload",
                fileName="buoi-hoc-30025.mp4",
                contentType="video/mp4",
            )
            job = JobState(
                "job-browser-invalid",
                SimpleNamespace(sourceType="BROWSER", sessionId=30025, annotatedVideoUpload=target),
                work,
            )
            job.recording_clips = {0: clip}
            self.mark_rendered_annotations(job)
            encoded = self._mp4_bytes()

            def fake_run(command, **_kwargs):
                if command[0] == "ffprobe":
                    is_output = ".tmp.mp4" in str(command[-1])
                    probe = self._probe(is_output=is_output, with_audio=False)
                    if is_output:
                        probe["streams"][0]["codec_name"] = "mpeg4"
                    return SimpleNamespace(returncode=0, stderr="", stdout=json.dumps(probe))
                Path(command[-1]).write_bytes(encoded)
                return SimpleNamespace(returncode=0, stderr="", stdout="")

            with patch("app.session_runtime.subprocess.run", side_effect=fake_run), patch(
                "app.session_runtime.httpx.Client", _Client
            ):
                with self.assertRaisesRegex(RuntimeError, "expected h264"):
                    manager._finalize_video_artifact(job)

            self.assertIsNone(job.artifact)
            self.assertEqual(b"", _Client.uploaded)
            self.assertTrue(clip.exists())
            self.assertEqual(b"retryable-source", clip.read_bytes())
            self.assertEqual([], list(work.glob("recording-concat-*.txt")))
            self.assertEqual([], list(work.glob("session-recording-*.tmp.mp4")))

    @unittest.skipUnless(shutil.which("ffmpeg") and shutil.which("ffprobe"), "FFmpeg tools are required")
    def test_real_ffmpeg_output_passes_h264_faststart_validation(self):
        manager = self.manager()
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            clip = work / "camera_chunk_000000.mp4"
            generated = subprocess.run(
                [
                    "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10",
                    "-t", "0.5", "-an", "-c:v", "mpeg4", "-pix_fmt", "yuv420p", str(clip),
                ],
                capture_output=True,
                text=True,
                timeout=30,
            )
            self.assertEqual(0, generated.returncode, generated.stderr)
            target = SimpleNamespace(
                objectKey="annotations/sessions/30027/corr.mp4",
                uploadUrl="http://storage/upload",
                fileName="buoi-hoc-30027.mp4",
                contentType="video/mp4",
            )
            job = JobState(
                "job-camera-real-ffmpeg",
                SimpleNamespace(sourceType="CAMERA", sessionId=30027, annotatedVideoUpload=target),
                work,
            )
            job.recording_clips = {0: clip}
            self.mark_rendered_annotations(job)

            with patch("app.session_runtime.httpx.Client", _Client):
                manager._finalize_video_artifact(job)

            self.assertIsNotNone(job.artifact)
            self.assertGreater(job.artifact["sizeBytes"], 0)
            self.assertEqual(b"ftyp", _Client.uploaded[4:8])
            self.assertGreater(_Client.uploaded.find(b"mdat"), _Client.uploaded.find(b"moov"))
            self.assertFalse(clip.exists())

    def test_finalize_does_not_complete_job_when_artifact_validation_fails(self):
        manager = self.manager()
        callbacks = []
        manager._retry_failed_browser_chunks = lambda _job: None
        manager._finalize_video_artifact = lambda _job: (_ for _ in ()).throw(
            RuntimeError("artifact codec validation failed")
        )
        manager._send_lost_for_all = lambda _job: callbacks.append("sent")
        with tempfile.TemporaryDirectory() as directory:
            job = JobState(
                "job-invalid-artifact",
                SimpleNamespace(sourceType="BROWSER", sessionId=30026),
                Path(directory),
                status="FINALIZING",
            )
            manager._finalize_job(job)

        self.assertEqual("FINALIZE_FAILED", job.status)
        self.assertIn("artifact codec validation failed", job.error)
        self.assertEqual([], callbacks)

    @staticmethod
    def _box(name: bytes, payload: bytes = b"") -> bytes:
        return (8 + len(payload)).to_bytes(4, "big") + name + payload

    @classmethod
    def _mp4_bytes(cls) -> bytes:
        return cls._box(b"ftyp", b"isom") + cls._box(b"moov") + cls._box(b"mdat", b"video")

    @staticmethod
    def _probe(is_output: bool, with_audio: bool) -> dict:
        video = {
            "codec_type": "video",
            "codec_name": "h264" if is_output else "mpeg4",
            "codec_tag_string": "avc1" if is_output else "mp4v",
            "pix_fmt": "yuv420p",
            "duration": "2.5",
        }
        streams = [video]
        if with_audio:
            streams.append({"codec_type": "audio", "codec_name": "aac" if is_output else "opus"})
        return {"streams": streams, "format": {"duration": "2.5", "size": "33"}}


if __name__ == "__main__":
    unittest.main()
