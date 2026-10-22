from datetime import datetime
from pydantic import BaseModel, Field, HttpUrl


class RosterCandidate(BaseModel):
    studentId: int
    templateReadUrl: str
    templateVersion: str
    modelName: str
    embeddingDimension: int

class StartJobRequest(BaseModel):
    sessionId: int
    sourceType: str
    source: str
    correlationId: str
    rosterStudentIds: list[int]
    rosterCandidates: list["RosterCandidate"] = Field(default_factory=list)
    alertProfile: str = "DEFAULT"
    callbackUrl: str
    callbackApiKey: str
    annotatedVideoUpload: "ArtifactUploadTarget | None" = None

class ArtifactUploadTarget(BaseModel):
    objectKey: str
    uploadUrl: str
    fileName: str
    contentType: str

class StartJobResponse(BaseModel):
    jobId: str
    status: str
    modelVersion: str

class StopJobResponse(BaseModel):
    jobId: str
    status: str
    artifact: "ArtifactMetadata | None" = None

class ArtifactMetadata(BaseModel):
    objectKey: str
    fileName: str
    contentType: str
    sizeBytes: int
    sha256: str

class CapabilitiesResponse(BaseModel):
    sessionInference: bool
    faceEnrollment: bool
    annotatedVideoOutput: bool
    loadedModels: list[str]

class FaceEnrollmentManifestImage(BaseModel):
    imageId: int
    capturePose: str
    contentType: str
    readUrl: str

class FaceEnrollmentManifest(BaseModel):
    enrollmentId: int
    studentId: int
    studentCode: str
    images: list[FaceEnrollmentManifestImage]
    templateObjectKey: str
    templateUploadUrl: str
    callbackUrl: str
    callbackApiKey: str

class StartFaceEnrollmentResponse(BaseModel):
    jobId: str
    status: str

class AiEvent(BaseModel):
    eventId: str
    sessionId: int
    eventType: str
    timestamp: datetime
    stableId: str
    trackId: str | None = None
    studentId: int | None = None
    identityConfidence: float | None = None
    identityMargin: float | None = None
    behaviorLabel: str | None = None
    behaviorProbability: float | None = None
    observationQuality: float = Field(ge=0, le=1)
    alertType: str | None = None
    alertDurationSeconds: int | None = None
    modelVersion: str = "stub-contract-v2"
    thresholdVersion: str = "stub-threshold-v2"
    startedAt: datetime | None = None
    endedAt: datetime | None = None
    identityDecision: str | None = None


class HtbamPredictRequest(BaseModel):
    rgbEmbedding: list[list[float]]
    rgbQuality: list[float]
    semantic: list[list[float]]
    semanticMask: list[list[float]]
    pose: list
    temporalMask: list[bool]

class HtbamPredictResponse(BaseModel):
    label: str
    classId: int
    probability: float
    probabilities: dict[str, float]
    modelVersion: str
    device: str
