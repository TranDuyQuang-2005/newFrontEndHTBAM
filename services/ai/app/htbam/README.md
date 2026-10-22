# HTBAM-X runtime

Loads the final causal T=3 production classifier from `/models/htbamx`.
The classifier consumes exact precomputed MViT RGB embeddings + 14 semantic features + pose + temporal mask.
`sessionInference` remains false until the complete per-person video pipeline is integrated.
