# HTBAM-X Stage-2B Runtime Audit

- Training repo: `E:\HTBAM_X_official_incremental_checked`
- Files scanned: 149
- Candidate files: 124

## Resolved configuration clues

| Key | Value |
|---|---:|
| `sample_fps` | `8.0` |
| `segment_seconds` | `2.0` |
| `crop_size` | `224` |
| `context_segments` | `3` |
| `precomputed_rgb_dim` | `None` |
| `semantic_dim` | `14` |
| `pose_landmarks` | `None` |
| `segment_frames` | `frames_per_segment: 16` |

## Top candidate files

- **574** `33_train_production_htbamx.py` — semantic, semantic_mask, pose, rgb_embedding, rgb embedding, mvit, mvit_v2_s, 14, 16, sample_fps, segment_seconds, crop_size, stride, temporal_mask, context_segments, causal, track
- **562** `src/htbamx/model.py` — semantic, semantic_mask, pose, rgb_embedding, rgb embedding, mvit, mvit_v2_s, 768, 14, 33, stride, temporal_mask, ear
- **546** `src/htbamx/behavior_cache.py` — semantic, semantic_mask, pose, 14, 33, 16, sample_fps, segment_seconds, crop_size, mediapipe, yolo, track, face_visible, ear, mar, gaze, phone_use, posture, motion
- **482** `27_install_causal_masked_unique_target.py` — semantic, semantic_mask, pose, rgb_embedding, 14, window, temporal_mask, context_segments, causal, ear
- **427** `33g_rescue_torchscript_export.py` — semantic, semantic_mask, pose, rgb_embedding, mvit, mvit_v2_s, 14, 33, 16, sample_fps, segment_seconds, crop_size, stride, temporal_mask, context_segments, causal, mar
- **377** `34_install_web_htbamx_stage1.py` — semantic, semantic_mask, pose, rgb embedding, mvit, 14, 33, 16, window, temporal_mask, causal, yolo
- **367** `src/htbamx/dataset.py` — semantic, semantic_mask, pose, rgb_embedding, rgb embedding, window, stride, context_segments, track, ear, mar
- **365** `src/htbamx/train.py` — semantic, semantic_mask, pose, rgb_embedding, rgb embedding, mvit, mvit_v2_s, 768, 14, window, stride, temporal_mask, context_segments, causal, track, ear, mar
- **333** `src/htbamx/eval_dataset.py` — semantic, semantic_mask, pose, rgb_embedding, window, stride, temporal_mask, context_segments, causal, track, mar
- **288** `scripts/11_build_d6_qc_batch_02_rare_mining.py` — semantic, semantic_mask, window, track, face_visible, face_conf, ear, gaze, phone_use, motion
- **261** `scripts/15_probe_d6_full_semantics.py` — semantic, semantic_mask, pose, 16, track, face_visible, ear, mar, gaze, phone_use, motion
- **257** `33f_verify_production_pipeline.py` — semantic, pose, rgb_embedding, rgb embedding, 33, stride, temporal_mask, context_segments, causal, track, ear
- **234** `README.md` — semantic, pose, rgb_embedding, rgb embedding, mvit, 14, 33, window, mediapipe, yolo, track, ear, mar
- **226** `configs/config.yaml` — pose, mvit, mvit_v2_s, 16, sample_fps, segment_seconds, crop_size, window, stride, context_segments, mediapipe, yolo, track, ear, mar
- **225** `src/htbamx/labels.py` — semantic, semantic_mask, track, face_visible, mar, gaze, phone_use, motion
- **208** `scripts/13_analyze_d6_temporal_features.py` — semantic, semantic_mask, face_visible, face_conf, mar, gaze, phone_use, motion
- **195** `scripts/train_htbamx_from_d6.py` — rgb_embedding, window, stride, context_segments, causal, track, mar
- **191** `tests/test_htbamx.py` — semantic, semantic_mask, pose, rgb_embedding, 14, 33, 16, window, stride, context_segments, track, ear, mar
- **186** `src/htbamx/d6_validation.py` — semantic, semantic_mask, pose, 33, track, mar
- **184** `28_smoke_test_temporal_mask.py` — semantic, semantic_mask, pose, 14, 33, 16
- **179** `scripts/16_probe_d6_rgb_qc.py` — rgb_embedding, rgb embedding, mvit, mvit_v2_s, window, track, ear, mar
- **175** `scripts/12_calibrate_d6_pseudo.py` — semantic, semantic_mask, 16, face_visible, ear, gaze, phone_use, motion
- **165** `src/htbam_builder/d5.py` — 14, 16, window, track, face_conf, ear, mar, motion
- **159** `37_smoke_test_deployed_htbam_classifier.py` — semantic, semantic_mask, pose, rgb_embedding, 33, stride, temporal_mask, context_segments, causal
- **158** `26_install_unique_target_regularized_training.py` — window, stride, context_segments, track, ear

## D6 builder candidates

- `33_train_production_htbamx.py` — symbols: seed_all, move, call_model, behavior_loss, atomic_target_loss, temporal_smoothness, metrics_from_cm, resolve_full_gold_manifest, infer_epochs, main, ScriptableClassifier
- `src/htbamx/model.py` — symbols: make_video_backbone, TinyVideoBackbone, PoseTemporalEncoder, QualityFusion, HTBAMXOutput, HTBAMX
- `src/htbamx/behavior_cache.py` — symbols: _dist, _ear, _mar, _head_pose, _iris_gaze_away, _pose_vector, _blur_quality, _square_pad, _square_resize, _nearest_track_row, _sha256_file, _cfg_fingerprint
- `27_install_causal_masked_unique_target.py` — symbols: (no top-level symbols)
- `33g_rescue_torchscript_export.py` — symbols: patch_future_trace, build_model, make_inputs, compare, main, ExportWrapper
- `34_install_web_htbamx_stage1.py` — symbols: (no top-level symbols)
- `src/htbamx/dataset.py` — symbols: BehaviorWindowDataset
- `src/htbamx/train.py` — symbols: seed_all, _class_counts, _class_weights, _move, behavior_loss_safe, temporal_smoothness, evaluate, _make_optimizer, _infer_precomputed_dim, _save_dataset_reports, train_htbamx
- `src/htbamx/eval_dataset.py` — symbols: CausalGoldTargetDataset
- `scripts/11_build_d6_qc_batch_02_rare_mining.py` — symbols: clean, read_csv, masked_mean, feature_profile, nearest_similarity, add_candidate, diverse_take, stratified_random, main

## RGB embedding candidates

- `33_train_production_htbamx.py` — symbols: seed_all, move, call_model, behavior_loss, atomic_target_loss, temporal_smoothness, metrics_from_cm, resolve_full_gold_manifest, infer_epochs, main, ScriptableClassifier
- `src/htbamx/model.py` — symbols: make_video_backbone, TinyVideoBackbone, PoseTemporalEncoder, QualityFusion, HTBAMXOutput, HTBAMX
- `27_install_causal_masked_unique_target.py` — symbols: (no top-level symbols)
- `33g_rescue_torchscript_export.py` — symbols: patch_future_trace, build_model, make_inputs, compare, main, ExportWrapper
- `34_install_web_htbamx_stage1.py` — symbols: (no top-level symbols)
- `src/htbamx/dataset.py` — symbols: BehaviorWindowDataset
- `src/htbamx/train.py` — symbols: seed_all, _class_counts, _class_weights, _move, behavior_loss_safe, temporal_smoothness, evaluate, _make_optimizer, _infer_precomputed_dim, _save_dataset_reports, train_htbamx
- `src/htbamx/eval_dataset.py` — symbols: CausalGoldTargetDataset
- `33f_verify_production_pipeline.py` — symbols: (no top-level symbols)
- `README.md` — symbols: (no top-level symbols)

## Training dataset candidates

- `33_train_production_htbamx.py` — symbols: seed_all, move, call_model, behavior_loss, atomic_target_loss, temporal_smoothness, metrics_from_cm, resolve_full_gold_manifest, infer_epochs, main, ScriptableClassifier
- `src/htbamx/dataset.py` — symbols: BehaviorWindowDataset
- `src/htbamx/train.py` — symbols: seed_all, _class_counts, _class_weights, _move, behavior_loss_safe, temporal_smoothness, evaluate, _make_optimizer, _infer_precomputed_dim, _save_dataset_reports, train_htbamx
- `src/htbamx/eval_dataset.py` — symbols: CausalGoldTargetDataset
- `scripts/train_htbamx_from_d6.py` — symbols: _find_final_eval, _build_runtime_manifest, _aggregate
- `33c_fix_production_manifest_source.py` — symbols: (no top-level symbols)
- `33d_check_loso_runtime_manifest.py` — symbols: (no top-level symbols)
- `33d_fix_use_loso_runtime_manifest.py` — symbols: (no top-level symbols)
- `33c_check_production_manifest_source.py` — symbols: (no top-level symbols)
- `SOURCE_MANIFEST.txt` — symbols: (no top-level symbols)

## Causal context candidates

- `33_train_production_htbamx.py` — symbols: seed_all, move, call_model, behavior_loss, atomic_target_loss, temporal_smoothness, metrics_from_cm, resolve_full_gold_manifest, infer_epochs, main, ScriptableClassifier
- `src/htbamx/model.py` — symbols: make_video_backbone, TinyVideoBackbone, PoseTemporalEncoder, QualityFusion, HTBAMXOutput, HTBAMX
- `27_install_causal_masked_unique_target.py` — symbols: (no top-level symbols)
- `33g_rescue_torchscript_export.py` — symbols: patch_future_trace, build_model, make_inputs, compare, main, ExportWrapper
- `34_install_web_htbamx_stage1.py` — symbols: (no top-level symbols)
- `src/htbamx/dataset.py` — symbols: BehaviorWindowDataset
- `src/htbamx/train.py` — symbols: seed_all, _class_counts, _class_weights, _move, behavior_loss_safe, temporal_smoothness, evaluate, _make_optimizer, _infer_precomputed_dim, _save_dataset_reports, train_htbamx
- `src/htbamx/eval_dataset.py` — symbols: CausalGoldTargetDataset
- `33f_verify_production_pipeline.py` — symbols: (no top-level symbols)
- `configs/config.yaml` — symbols: (no top-level symbols)
