namespace HTBAM.Domain.Entities;

public abstract class Entity { public long Id { get; set; } }

public sealed class User : Entity
{
    public string UserName { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Status { get; set; } = "ACTIVE";
    public int TokenVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
public sealed class Role : Entity { public string Name { get; set; } = ""; public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>(); }
public sealed class UserRole { public long UserId { get; set; } public User User { get; set; } = null!; public long RoleId { get; set; } public Role Role { get; set; } = null!; }

public sealed class Faculty : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string? Note { get; set; } public bool IsActive { get; set; } = true; }
public sealed class Department : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; public long FacultyId { get; set; } public Faculty Faculty { get; set; } = null!; public string? Note { get; set; } public bool IsActive { get; set; } = true; }
public sealed class StudentClass : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; public long FacultyId { get; set; } public Faculty Faculty { get; set; } = null!; public int? StartYear { get; set; } public string? Note { get; set; } public bool IsActive { get; set; } = true; }
public sealed class Student : Entity
{
    public string StudentCode { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public long? StudentClassId { get; set; }
    public StudentClass? StudentClass { get; set; }
    public bool IsActive { get; set; } = true;
    public string AnonymousCode { get; set; } = "";
    public string? SourceAnonymousCode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public sealed class Teacher : Entity { public string TeacherCode { get; set; } = ""; public string FullName { get; set; } = ""; public string Email { get; set; } = ""; public long? UserId { get; set; } public long? DepartmentId { get; set; } public string? Note { get; set; } public bool IsActive { get; set; } = true; }
public sealed class ManagementAssignment : Entity
{
    public long TeacherId { get; set; }
    public Teacher Teacher { get; set; } = null!;
    public string PositionType { get; set; } = "";
    public long? FacultyId { get; set; }
    public Faculty? Faculty { get; set; }
    public long? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public DateTime EffectiveFrom { get; set; } = DateTime.UtcNow;
    public DateTime? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public long? AssignedByUserId { get; set; }
    public User? AssignedByUser { get; set; }
    public long? AssignedByTeacherId { get; set; }
    public Teacher? AssignedByTeacher { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public sealed class Course : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; public long? DepartmentId { get; set; } public int Credits { get; set; } = 3; public string? Note { get; set; } public bool IsActive { get; set; } = true; }
public sealed class ClassSection : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; public long CourseId { get; set; } public Course Course { get; set; } = null!; public long TeacherId { get; set; } public Teacher Teacher { get; set; } = null!; public string Semester { get; set; } = ""; public string AcademicYear { get; set; } = ""; public string? Note { get; set; } public bool IsActive { get; set; } = true; public string? AutoComment { get; set; } public string? AutoCommentProvider { get; set; } public DateTime? AutoCommentGeneratedAt { get; set; }
    public string? SourceClassSectionCode { get; set; }
    public long? AcademicTermId { get; set; }
    public AcademicTerm? AcademicTerm { get; set; }
    public int? Phase { get; set; }
    public long? AttendancePolicyId { get; set; }
    public int Revision { get; set; }
    public string? CreationKey { get; set; }
    public string? CreationRequestHash { get; set; }
}
public sealed class Enrollment { public long ClassSectionId { get; set; } public ClassSection ClassSection { get; set; } = null!; public long StudentId { get; set; } public Student Student { get; set; } = null!; public DateTime EnrolledAt { get; set; } = DateTime.UtcNow; public string? ClassNote { get; set; } }
public sealed class Room : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string? Location { get; set; } public int Capacity { get; set; } public string? Note { get; set; } public bool IsActive { get; set; } = true; public string RoomType { get; set; } = "UNKNOWN"; }
public sealed class Camera : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; public long RoomId { get; set; } public Room Room { get; set; } = null!; public string RtspUrl { get; set; } = ""; public string Status { get; set; } = "OFFLINE"; public DateTime? LastHealthCheckAt { get; set; } public string? LastHealthMessage { get; set; } public string? Note { get; set; } public bool IsActive { get; set; } = true; }

public sealed class FaceEnrollment : Entity
{
    public long StudentId { get; set; }
    public Student Student { get; set; } = null!;
    public string Status { get; set; } = "IN_PROGRESS";
    public string RequiredPoses { get; set; } = "FRONT,LEFT,RIGHT";
    public int MinAcceptedImages { get; set; } = 6;
    public int AcceptedImageCount { get; set; }
    public string? FailureReason { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
public sealed class StudentFaceImage : Entity
{
    public long FaceEnrollmentId { get; set; }
    public FaceEnrollment FaceEnrollment { get; set; } = null!;
    public long StudentId { get; set; }
    public Student Student { get; set; } = null!;
    public string ObjectKey { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string CapturePose { get; set; } = "FRONT";
    public string SourceType { get; set; } = "UPLOAD";
    public decimal? QualityScore { get; set; }
    public string QualityStatus { get; set; } = "PENDING";
    public string? QualityReason { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}
public sealed class StudentFaceTemplate : Entity
{
    public long StudentId { get; set; }
    public Student Student { get; set; } = null!;
    public long? FaceEnrollmentId { get; set; }
    public string TemplateRef { get; set; } = "";
    public decimal QualityScore { get; set; }
    public string PoseCoverage { get; set; } = "";
    public string ModelName { get; set; } = "ArcFace";
    public string TemplateVersion { get; set; } = "";
    public int EmbeddingDimension { get; set; } = 512;
    public int SourceImageCount { get; set; }
    public string QualityStatus { get; set; } = "PASS";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Video : Entity
{
    public string FileName { get; set; } = "";
    public string StorageRef { get; set; } = "";
    public string ContentType { get; set; } = "video/mp4";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string Status { get; set; } = "READY";
    public string VideoType { get; set; } = "INPUT_UPLOAD";
    public long? SessionId { get; set; }
    public Session? Session { get; set; }
    public long? ParentVideoId { get; set; }
    public Video? ParentVideo { get; set; }
    public bool IsSystemGenerated { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public long? UploadedByUserId { get; set; }
}

public sealed class AttendancePolicy : Entity
{
    public string Code { get; set; } = "DEFAULT";
    public string Name { get; set; } = "Chính sách mặc định";
    public decimal PresentThreshold { get; set; } = 0.80m;
    public decimal PartialThreshold { get; set; } = 0.50m;
    public decimal PresentScore { get; set; } = 10m;
    public decimal PartialScore { get; set; } = 5m;
    public decimal AbsentScore { get; set; } = 0m;
    public string Version { get; set; } = "v1";
    public bool IsActive { get; set; } = true;
}
public sealed class Session : Entity
{
    public bool IsDevelopment { get; set; }
    public bool SupersededByScheduleChange { get; set; }
    public long? ClassScheduleId { get; set; }
    public ClassSchedule? ClassSchedule { get; set; }
    public DateOnly? OccurrenceDate { get; set; }
    public int? StartPeriod { get; set; }
    public int? EndPeriod { get; set; }
    public long? BellTimeVersionId { get; set; }
    public long ClassSectionId { get; set; }
    public ClassSection ClassSection { get; set; } = null!;
    public long OriginalTeacherId { get; set; }
    public Teacher OriginalTeacher { get; set; } = null!;
    public long? RoomId { get; set; }
    public long? CameraId { get; set; }
    public long? VideoId { get; set; }
    public long AttendancePolicyId { get; set; }
    public DateTime ScheduledStart { get; set; }
    public DateTime? ScheduledEnd { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string Status { get; set; } = "DRAFT";
    public string AlertProfile { get; set; } = "DEFAULT";
    public string? LecturerComment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
public sealed class SessionSubstitution : Entity
{
    public long SessionId { get; set; }
    public Session Session { get; set; } = null!;
    public long OriginalTeacherId { get; set; }
    public Teacher OriginalTeacher { get; set; } = null!;
    public long SubstituteTeacherId { get; set; }
    public Teacher SubstituteTeacher { get; set; } = null!;
    public long? AssignedByUserId { get; set; }
    public User? AssignedByUser { get; set; }
    public long? AssignedByTeacherId { get; set; }
    public Teacher? AssignedByTeacher { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "ACTIVE";
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CancelledAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Note { get; set; }
}
public sealed class SessionStudent { public long SessionId { get; set; } public Session Session { get; set; } = null!; public long StudentId { get; set; } public Student Student { get; set; } = null!; }
public sealed class AnalysisJob : Entity { public long SessionId { get; set; } public Session Session { get; set; } = null!; public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N"); public string ExternalJobId { get; set; } = ""; public string Status { get; set; } = "CREATED"; public string ModelVersion { get; set; } = "not-loaded"; public DateTime CreatedAt { get; set; } = DateTime.UtcNow; public DateTime? StartedAt { get; set; } public DateTime? EndedAt { get; set; } public string? ErrorMessage { get; set; } }

public sealed class StableIdentity : Entity
{
    public long SessionId { get; set; }
    public Session Session { get; set; } = null!;
    public string StableId { get; set; } = "";
    public long? StudentId { get; set; }
    public Student? Student { get; set; }
    public string? CurrentTrackId { get; set; }
    public string IdentityStatus { get; set; } = "UNKNOWN";
    public decimal? IdentityConfidence { get; set; }
    public decimal? IdentityMargin { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public string State { get; set; } = "ACTIVE";
    public string? LastZone { get; set; }
    public string? SeatHint { get; set; }
    public string ModelVersion { get; set; } = "";
}
public sealed class TrackSegment : Entity { public long StableIdentityId { get; set; } public StableIdentity StableIdentity { get; set; } = null!; public string TrackId { get; set; } = ""; public DateTime StartedAt { get; set; } public DateTime EndedAt { get; set; } public decimal? AvgQuality { get; set; } public int ObservationCount { get; set; } = 1; }
public sealed class IdentityLink : Entity { public long StableIdentityId { get; set; } public StableIdentity StableIdentity { get; set; } = null!; public long? StudentId { get; set; } public string EvidenceType { get; set; } = ""; public decimal Score { get; set; } public decimal? Margin { get; set; } public string Decision { get; set; } = "EVIDENCE"; public string ModelVersion { get; set; } = ""; public DateTime CreatedAt { get; set; } = DateTime.UtcNow; }
public sealed class AiEventReceipt : Entity { public string ExternalEventId { get; set; } = ""; public long SessionId { get; set; } public string EventType { get; set; } = ""; public DateTime EventTimestamp { get; set; } public DateTime ReceivedAt { get; set; } = DateTime.UtcNow; }

public sealed class BehaviorEvent : Entity { public string ExternalEventId { get; set; } = ""; public long SessionId { get; set; } public long StableIdentityId { get; set; } public long? StudentId { get; set; } public string Label { get; set; } = ""; public decimal Probability { get; set; } public DateTime StartedAt { get; set; } public DateTime EndedAt { get; set; } public decimal ObservationQuality { get; set; } public string ModelVersion { get; set; } = ""; public string ThresholdVersion { get; set; } = ""; }
public sealed class Alert : Entity { public string ExternalEventId { get; set; } = ""; public long SessionId { get; set; } public long StableIdentityId { get; set; } public long? StudentId { get; set; } public string Type { get; set; } = ""; public DateTime StartedAt { get; set; } public DateTime? EndedAt { get; set; } public int DurationSeconds { get; set; } public decimal Confidence { get; set; } public decimal ObservationQuality { get; set; } public string Status { get; set; } = "OPEN"; public string? LecturerNote { get; set; } public string? EvidenceRef { get; set; } public DateTime CreatedAt { get; set; } = DateTime.UtcNow; public DateTime? AcknowledgedAt { get; set; } public DateTime? ClosedAt { get; set; } }
public sealed class AlertRule : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; public string BehaviorLabel { get; set; } = ""; public int MinDurationSeconds { get; set; } public decimal MinConfidence { get; set; } public decimal MinObservationQuality { get; set; } = 0.5m; public bool Enabled { get; set; } = true; public string Version { get; set; } = "v1"; }
public sealed class Attendance : Entity { public long SessionId { get; set; } public long StudentId { get; set; } public int PresentSeconds { get; set; } public int ObservedSeconds { get; set; } public decimal PresenceRatio { get; set; } public decimal ObservationQuality { get; set; } public string Status { get; set; } = "UNKNOWN"; public decimal Score { get; set; } public string PolicyVersion { get; set; } = ""; }
public sealed class StudentSessionSummary : Entity { public long SessionId { get; set; } public long StudentId { get; set; } public decimal FocusedRatio { get; set; } public decimal DistractedRatio { get; set; } public decimal SleepyRatio { get; set; } public decimal ActiveRatio { get; set; } public int AlertCount { get; set; } public int AlertDurationSeconds { get; set; } public decimal ObservationQuality { get; set; } public string? AutoComment { get; set; } public string? AutoCommentProvider { get; set; } public DateTime? AutoCommentGeneratedAt { get; set; } }
public sealed class ClassSessionSummary : Entity { public long SessionId { get; set; } public int ObservedStudentCount { get; set; } public decimal FocusedRatio { get; set; } public decimal DistractedRatio { get; set; } public decimal SleepyRatio { get; set; } public decimal ActiveRatio { get; set; } public int AlertCount { get; set; } public decimal CameraAiQuality { get; set; } public string? AutoComment { get; set; } public string? AutoCommentProvider { get; set; } public DateTime? AutoCommentGeneratedAt { get; set; } }
public sealed class AuditLog : Entity { public long? UserId { get; set; } public string Action { get; set; } = ""; public string EntityType { get; set; } = ""; public string EntityId { get; set; } = ""; public string? IpAddress { get; set; } public string? MetadataJson { get; set; } public DateTime CreatedAt { get; set; } = DateTime.UtcNow; }
