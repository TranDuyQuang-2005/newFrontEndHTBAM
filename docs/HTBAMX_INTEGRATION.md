# HTBAM-X production integration

Deployment uses a separate production checkpoint trained on all 1,590 Human-Gold targets.

Causal model contract: `[t-2,t-1,t] -> t`, T=3, RGB embedding + 14 semantic features + pose + temporal mask.

Before `sessionInference=true`: reuse the exact D6 feature extractor and exact MViT embedding preprocessing, then add YOLO + BoT-SORT, stable identity / ArcFace-ReID, causal buffers, smoothing/EventEngine and callback flush.
