# Vendored HTBAM training runtime

This directory mirrors the runtime source used by the HTBAM-X training/data pipeline.

Source directories:
- `E:\HTBAM_X_official_incremental_checked\src\htbamx`
- `E:\HTBAM_X_official_incremental_checked\src\htbam_builder`

Do not edit the vendored package copies manually. Re-run `36_sync_exact_training_runtime_source.py` to replace them from the training repository.

The Docker image exposes `/app/vendor` on `PYTHONPATH` so the original `htbamx` and `htbam_builder` import paths remain unchanged.
