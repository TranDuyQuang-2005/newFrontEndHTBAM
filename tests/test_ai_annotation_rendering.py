import shutil
import os
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import cv2
import numpy as np
import pandas as pd

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "services" / "ai"))

from app.session_runtime import JobState, SessionInferenceManager, StableState


class _Response:
    def raise_for_status(self):
        return None


class _CaptureClient:
    uploaded = b""

    def __init__(self, *args, **kwargs):
        pass

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def put(self, _url, content, headers):
        self.__class__.uploaded = b"".join(content)
        return _Response()


@unittest.skipUnless(shutil.which("ffprobe"), "ffprobe is required")
class AiAnnotationRenderingTests(unittest.TestCase):
    def manager(self):
        manager = SessionInferenceManager.__new__(SessionInferenceManager)
        manager.jobs = {}
        manager.jobs_lock = threading.Lock()
        return manager

    @staticmethod
    def source_video(path: Path, fps: float = 10.0, frames: int = 6):
        writer = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), fps, (160, 120))
        if not writer.isOpened():
            raise RuntimeError("OpenCV could not create the annotation test source")
        for index in range(frames):
            frame = np.full((120, 160, 3), 210, dtype=np.uint8)
            cv2.circle(frame, (25 + index * 8, 80), 8, (80, 80, 80), -1)
            writer.write(frame)
        writer.release()

    @staticmethod
    def frame(path: Path, index: int):
        cap = cv2.VideoCapture(str(path))
        cap.set(cv2.CAP_PROP_POS_FRAMES, index)
        ok, frame = cap.read()
        cap.release()
        if not ok:
            raise RuntimeError(f"Could not read frame {index} from {path}")
        return frame

    def test_real_tracking_identity_and_behavior_are_burned_into_frames(self):
        manager = self.manager()
        _CaptureClient.uploaded = b""
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            source = work / "source.mp4"
            self.source_video(source)
            rows = []
            for frame_index in range(1, 5):
                rows.append({
                    "frame_index": frame_index,
                    "timestamp_sec": frame_index / 10.0,
                    "track_id": 7,
                    "tracklet_id": "T00007_000",
                    "x1": 40,
                    "y1": 28,
                    "x2": 125,
                    "y2": 105,
                    "confidence": 0.93,
                })
            enriched = pd.DataFrame(rows)
            state = StableState(stable_id="S30030-00001", student_id=12)
            resolved = {"T00007_000": (state, 0.88, 0.21)}
            roster = [{
                "student_id": 12,
                "student_code": "SV0012",
                "full_name": "Nguyễn Văn An",
            }]
            job = JobState(
                "job-annotation",
                SimpleNamespace(
                    sourceType="BROWSER",
                    sessionId=30030,
                    annotatedVideoUpload=SimpleNamespace(
                        objectKey="annotations/sessions/30030/test.mp4",
                        uploadUrl="http://storage/upload",
                        fileName="buoi-hoc-30030-annotations.mp4",
                        contentType="video/mp4",
                    ),
                ),
                work,
            )
            job.annotation_behavior_segments[0] = [{
                "trackletId": "T00007_000",
                "stableId": state.stable_id,
                "trackId": 7,
                "startSec": 0.1,
                "endSec": 0.4,
                "label": "FOCUSED",
                "probability": 0.91,
                "observationQuality": 0.95,
            }]

            annotated = manager._render_annotated_chunk(
                job, source, 0, enriched, resolved, roster, 0.0
            )

            self.assertTrue(annotated.is_file())
            self.assertEqual(6, job.annotation_stats[0]["frames"])
            self.assertEqual(4, job.annotation_stats[0]["boxes"])
            self.assertFalse(job.annotation_stats[0]["hasAudio"])
            source_frame = self.frame(source, 2)
            annotated_frame = self.frame(annotated, 2)
            border_source = source_frame[26:34, 38:128]
            border_annotated = annotated_frame[26:34, 38:128]
            self.assertGreater(
                float(np.mean(cv2.absdiff(border_source, border_annotated))),
                10.0,
            )
            job.annotation_clips[0] = annotated
            job.annotation_source_clips[0] = source
            job.recording_clips[0] = annotated
            with patch("app.session_runtime.httpx.Client", _CaptureClient):
                manager._finalize_video_artifact(job)

            artifact = work / "captured-annotation-artifact.mp4"
            artifact.write_bytes(_CaptureClient.uploaded)
            probe = manager._probe_media(artifact)
            video = next(stream for stream in probe["streams"] if stream["codec_type"] == "video")
            self.assertEqual("h264", video["codec_name"])
            self.assertEqual("yuv420p", video["pix_fmt"])
            self.assertEqual("avc1", video["codec_tag_string"])
            self.assertFalse(any(stream["codec_type"] == "audio" for stream in probe["streams"]))
            final_frame = self.frame(artifact, 2)
            self.assertGreater(
                float(np.mean(cv2.absdiff(source_frame[26:34, 38:128], final_frame[26:34, 38:128]))),
                10.0,
            )

            evidence_dir = str(os.getenv("HTBAM_ANNOTATION_EVIDENCE_DIR", "")).strip()
            if evidence_dir:
                evidence = Path(evidence_dir)
                evidence.mkdir(parents=True, exist_ok=True)
                shutil.copy2(artifact, evidence / "annotation-artifact-h264.mp4")
                frames = [self.frame(artifact, index) for index in (1, 2, 4)]
                cv2.imwrite(str(evidence / "annotation-frames.jpg"), np.concatenate(frames, axis=1))

    @unittest.skipUnless(shutil.which("ffmpeg"), "ffmpeg is required")
    def test_real_source_audio_is_preserved_as_aac(self):
        manager = self.manager()
        _CaptureClient.uploaded = b""
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            source = work / "source-with-audio.mp4"
            generated = subprocess.run(
                [
                    "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10",
                    "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=44100",
                    "-t", "1", "-c:v", "mpeg4", "-pix_fmt", "yuv420p",
                    "-c:a", "aac", "-shortest", str(source),
                ],
                capture_output=True,
                text=True,
                timeout=30,
            )
            self.assertEqual(0, generated.returncode, generated.stderr)
            enriched = pd.DataFrame([
                {
                    "frame_index": index,
                    "track_id": 3,
                    "tracklet_id": "T00003_000",
                    "x1": 30,
                    "y1": 20,
                    "x2": 120,
                    "y2": 105,
                    "confidence": 0.85,
                }
                for index in range(10)
            ])
            state = StableState(stable_id="S30032-00001")
            target = SimpleNamespace(
                objectKey="annotations/sessions/30032/test.mp4",
                uploadUrl="http://storage/upload",
                fileName="buoi-hoc-30032-annotations.mp4",
                contentType="video/mp4",
            )
            job = JobState(
                "job-annotation-audio",
                SimpleNamespace(sourceType="VIDEO", sessionId=30032, annotatedVideoUpload=target),
                work,
            )
            annotated = manager._render_annotated_chunk(
                job,
                source,
                0,
                enriched,
                {"T00003_000": (state, None, None)},
                [],
                0.0,
            )
            self.assertTrue(job.annotation_stats[0]["hasAudio"])
            annotated_probe = manager._probe_media(annotated)
            self.assertTrue(any(
                stream["codec_type"] == "audio" and stream["codec_name"] == "aac"
                for stream in annotated_probe["streams"]
            ))

            job.annotation_clips[0] = annotated
            job.annotation_source_clips[0] = source
            job.recording_clips[0] = annotated
            with patch("app.session_runtime.httpx.Client", _CaptureClient):
                manager._finalize_video_artifact(job)
            artifact = work / "captured-audio-annotation.mp4"
            artifact.write_bytes(_CaptureClient.uploaded)
            final_probe = manager._probe_media(artifact)
            self.assertTrue(any(
                stream["codec_type"] == "audio" and stream["codec_name"] == "aac"
                for stream in final_probe["streams"]
            ))

    def test_behavior_label_is_only_used_inside_its_real_segment(self):
        segments = {
            "T00001_000": [{"startSec": 1.0, "endSec": 2.0, "label": "SLEEPY"}]
        }
        self.assertIsNone(SessionInferenceManager._behavior_for_frame(segments, "T00001_000", 0.9))
        self.assertEqual(
            "SLEEPY",
            SessionInferenceManager._behavior_for_frame(segments, "T00001_000", 1.5)["label"],
        )
        self.assertIsNone(SessionInferenceManager._behavior_for_frame(segments, "T00001_000", 2.1))

    def test_missing_detections_cannot_be_labeled_as_annotation(self):
        manager = self.manager()
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            source = work / "source.mp4"
            self.source_video(source)
            empty = pd.DataFrame(columns=[
                "frame_index", "track_id", "tracklet_id", "x1", "y1", "x2", "y2"
            ])
            job = JobState(
                "job-no-detections",
                SimpleNamespace(sourceType="BROWSER", sessionId=30031),
                work,
            )
            with self.assertRaisesRegex(RuntimeError, "no real person detections"):
                manager._render_annotated_chunk(job, source, 0, empty, {}, [], 0.0)


if __name__ == "__main__":
    unittest.main()
