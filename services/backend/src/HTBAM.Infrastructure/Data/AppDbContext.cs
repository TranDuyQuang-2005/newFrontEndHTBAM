using HTBAM.Application.Interfaces;
using HTBAM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Infrastructure.Data;

public sealed partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public bool DevelopmentDataEnabled { get; set; }
    public DbSet<CourseCodeCounter> CourseCodeCounters => Set<CourseCodeCounter>();
    public DbSet<LecturerCodeSequence> LecturerCodeSequences => Set<LecturerCodeSequence>();
    public DbSet<CatalogSeedIdentity> CatalogSeedIdentities => Set<CatalogSeedIdentity>();
    public DbSet<User> UsersSet => Set<User>(); public IQueryable<User> Users => UsersSet;
    public DbSet<Role> RolesSet => Set<Role>(); public IQueryable<Role> Roles => RolesSet;
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Faculty> FacultiesSet => Set<Faculty>(); public IQueryable<Faculty> Faculties => FacultiesSet;
    public DbSet<Department> DepartmentsSet => Set<Department>(); public IQueryable<Department> Departments => DepartmentsSet;
    public DbSet<StudentClass> StudentClassesSet => Set<StudentClass>(); public IQueryable<StudentClass> StudentClasses => StudentClassesSet;
    public DbSet<Student> StudentsSet => Set<Student>(); public IQueryable<Student> Students => StudentsSet;
    public DbSet<Teacher> Teachers => Set<Teacher>(); public DbSet<Course> Courses => Set<Course>(); public DbSet<ClassSection> ClassSections => Set<ClassSection>(); IQueryable<ClassSection> IAppDbContext.ClassSections => ClassSections;
    public DbSet<ManagementAssignment> ManagementAssignmentsSet => Set<ManagementAssignment>(); public IQueryable<ManagementAssignment> ManagementAssignments => ManagementAssignmentsSet;
    public DbSet<Enrollment> Enrollments => Set<Enrollment>(); IQueryable<Enrollment> IAppDbContext.Enrollments => Enrollments;
    public DbSet<Room> Rooms => Set<Room>(); IQueryable<Room> IAppDbContext.Rooms => Rooms;
    public DbSet<Camera> CamerasSet => Set<Camera>(); public IQueryable<Camera> Cameras => CamerasSet;
    public DbSet<FaceEnrollment> FaceEnrollmentsSet => Set<FaceEnrollment>(); public IQueryable<FaceEnrollment> FaceEnrollments => FaceEnrollmentsSet;
    public DbSet<StudentFaceImage> StudentFaceImagesSet => Set<StudentFaceImage>(); public IQueryable<StudentFaceImage> StudentFaceImages => StudentFaceImagesSet;
    public DbSet<StudentFaceTemplate> StudentFaceTemplatesSet => Set<StudentFaceTemplate>(); public IQueryable<StudentFaceTemplate> StudentFaceTemplates => StudentFaceTemplatesSet;
    public DbSet<Video> VideosSet => Set<Video>(); public IQueryable<Video> Videos => VideosSet;
    public DbSet<AttendancePolicy> AttendancePoliciesSet => Set<AttendancePolicy>(); public IQueryable<AttendancePolicy> AttendancePolicies => AttendancePoliciesSet;
    public DbSet<Session> SessionsSet => Set<Session>(); public IQueryable<Session> Sessions => SessionsSet;
    public DbSet<SessionSubstitution> SessionSubstitutionsSet => Set<SessionSubstitution>(); public IQueryable<SessionSubstitution> SessionSubstitutions => SessionSubstitutionsSet;
    public DbSet<SessionStudent> SessionStudentsSet => Set<SessionStudent>(); public IQueryable<SessionStudent> SessionStudents => SessionStudentsSet;
    public DbSet<AnalysisJob> AnalysisJobsSet => Set<AnalysisJob>(); public IQueryable<AnalysisJob> AnalysisJobs => AnalysisJobsSet;
    public DbSet<StableIdentity> StableIdentitiesSet => Set<StableIdentity>(); public IQueryable<StableIdentity> StableIdentities => StableIdentitiesSet;
    public DbSet<TrackSegment> TrackSegmentsSet => Set<TrackSegment>(); public IQueryable<TrackSegment> TrackSegments => TrackSegmentsSet;
    public DbSet<IdentityLink> IdentityLinksSet => Set<IdentityLink>(); public IQueryable<IdentityLink> IdentityLinks => IdentityLinksSet;
    public DbSet<AiEventReceipt> AiEventReceiptsSet => Set<AiEventReceipt>(); public IQueryable<AiEventReceipt> AiEventReceipts => AiEventReceiptsSet;
    public DbSet<BehaviorEvent> BehaviorEventsSet => Set<BehaviorEvent>(); public IQueryable<BehaviorEvent> BehaviorEvents => BehaviorEventsSet;
    public DbSet<Alert> AlertsSet => Set<Alert>(); public IQueryable<Alert> Alerts => AlertsSet;
    public DbSet<AlertRule> AlertRulesSet => Set<AlertRule>(); public IQueryable<AlertRule> AlertRules => AlertRulesSet;
    public DbSet<Attendance> AttendanceSet => Set<Attendance>(); public IQueryable<Attendance> Attendance => AttendanceSet;
    public DbSet<StudentSessionSummary> StudentSessionSummariesSet => Set<StudentSessionSummary>(); public IQueryable<StudentSessionSummary> StudentSessionSummaries => StudentSessionSummariesSet;
    public DbSet<ClassSessionSummary> ClassSessionSummariesSet => Set<ClassSessionSummary>(); public IQueryable<ClassSessionSummary> ClassSessionSummaries => ClassSessionSummariesSet;
    public DbSet<AuditLog> AuditLogsSet => Set<AuditLog>(); public IQueryable<AuditLog> AuditLogs => AuditLogsSet;

    Task IAppDbContext.AddAsync<T>(T entity, CancellationToken ct) => Set<T>().AddAsync(entity, ct).AsTask();
    void IAppDbContext.Update<T>(T entity) => Set<T>().Update(entity);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<CourseCodeCounter>().ToTable("CourseCodeCounters");
        b.Entity<LecturerCodeSequence>().ToTable("LecturerCodeSequences");
        b.Entity<LecturerCodeSequence>().HasKey(x=>x.DepartmentId);
        b.Entity<LecturerCodeSequence>().HasOne<Department>().WithMany().HasForeignKey(x=>x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CourseCodeCounter>().HasKey(x=>x.DepartmentId);
        b.Entity<CourseCodeCounter>().HasOne<Department>().WithMany().HasForeignKey(x=>x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CatalogSeedIdentity>().ToTable("CatalogSeedIdentities");
        b.Entity<CatalogSeedIdentity>().HasKey(x=>x.Key);
        b.Entity<CatalogSeedIdentity>().Property(x=>x.Key).HasMaxLength(64);
        b.Entity<CatalogSeedIdentity>().HasOne<Department>().WithMany().HasForeignKey(x=>x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CatalogSeedIdentity>().HasOne<Course>().WithMany().HasForeignKey(x=>x.CourseId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CatalogSeedIdentity>().HasIndex(x=>x.DepartmentId).IsUnique().HasFilter("[DepartmentId] IS NOT NULL");
        b.Entity<CatalogSeedIdentity>().HasIndex(x=>x.CourseId).IsUnique().HasFilter("[CourseId] IS NOT NULL");
        var tableNames = new Dictionary<Type,string> {
            [typeof(User)]="Users",[typeof(Role)]="Roles",[typeof(UserRole)]="UserRoles",[typeof(Faculty)]="Faculties",[typeof(Department)]="Departments",[typeof(StudentClass)]="StudentClasses",[typeof(Student)]="Students",[typeof(Teacher)]="Teachers",[typeof(ManagementAssignment)]="ManagementAssignments",[typeof(Course)]="Courses",[typeof(ClassSection)]="ClassSections",[typeof(Enrollment)]="Enrollments",[typeof(Room)]="Rooms",[typeof(Camera)]="Cameras",[typeof(FaceEnrollment)]="FaceEnrollments",[typeof(StudentFaceImage)]="StudentFaceImages",[typeof(StudentFaceTemplate)]="StudentFaceTemplates",[typeof(Video)]="Videos",[typeof(AttendancePolicy)]="AttendancePolicies",[typeof(Session)]="Sessions",[typeof(SessionSubstitution)]="SessionSubstitutions",[typeof(SessionStudent)]="SessionStudents",[typeof(AnalysisJob)]="AnalysisJobs",[typeof(StableIdentity)]="StableIdentities",[typeof(TrackSegment)]="TrackSegments",[typeof(IdentityLink)]="IdentityLinks",[typeof(AiEventReceipt)]="AiEventReceipts",[typeof(BehaviorEvent)]="BehaviorEvents",[typeof(AlertRule)]="AlertRules",[typeof(Alert)]="Alerts",[typeof(Attendance)]="Attendance",[typeof(StudentSessionSummary)]="StudentSessionSummaries",[typeof(ClassSessionSummary)]="ClassSessionSummaries",[typeof(AuditLog)]="AuditLogs"};
        foreach (var kv in tableNames) b.Entity(kv.Key).ToTable(kv.Value);

        b.Entity<User>().HasIndex(x=>x.UserName).IsUnique(); b.Entity<User>().HasIndex(x=>x.Email).IsUnique().HasFilter("[Email] <> ''");
        b.Entity<Role>().HasIndex(x=>x.Name).IsUnique(); b.Entity<UserRole>().HasKey(x=>new{x.UserId,x.RoleId});
        b.Entity<Faculty>().HasIndex(x=>x.Code).IsUnique(); b.Entity<Department>().HasIndex(x=>x.Code).IsUnique(); b.Entity<StudentClass>().HasIndex(x=>x.Code).IsUnique();
        b.Entity<Student>().Property(x=>x.StudentCode).HasMaxLength(64); b.Entity<Student>().Property(x=>x.FullName).HasMaxLength(256); b.Entity<Student>().Property(x=>x.Email).HasMaxLength(256); b.Entity<Student>().Property(x=>x.AnonymousCode).HasMaxLength(128);
        b.Entity<Student>().HasIndex(x=>x.StudentCode).IsUnique(); b.Entity<Student>().HasIndex(x=>x.AnonymousCode).IsUnique(); b.Entity<Student>().HasIndex(x=>x.Email).IsUnique().HasFilter("[Email] <> ''");
        b.Entity<Teacher>().HasIndex(x=>x.TeacherCode).IsUnique(); b.Entity<Teacher>().HasIndex(x=>x.UserId).IsUnique().HasFilter("[UserId] IS NOT NULL");
        b.Entity<ManagementAssignment>().HasIndex(x=>new{x.TeacherId,x.IsActive}); b.Entity<ManagementAssignment>().HasIndex(x=>x.FacultyId).IsUnique().HasFilter("[IsActive] = 1 AND [PositionType] = 'FACULTY_HEAD' AND [FacultyId] IS NOT NULL"); b.Entity<ManagementAssignment>().HasIndex(x=>x.DepartmentId).IsUnique().HasFilter("[IsActive] = 1 AND [PositionType] = 'DEPARTMENT_HEAD' AND [DepartmentId] IS NOT NULL"); b.Entity<ManagementAssignment>().HasOne(x=>x.AssignedByUser).WithMany().HasForeignKey(x=>x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict); b.Entity<ManagementAssignment>().HasOne(x=>x.AssignedByTeacher).WithMany().HasForeignKey(x=>x.AssignedByTeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Course>().HasIndex(x=>x.Code).IsUnique(); b.Entity<ClassSection>().HasIndex(x=>x.Code).IsUnique(); b.Entity<Enrollment>().HasKey(x=>new{x.ClassSectionId,x.StudentId});
        b.Entity<Room>().HasIndex(x=>x.Code).IsUnique(); b.Entity<Camera>().HasIndex(x=>x.Code).IsUnique();
        b.Entity<Faculty>().Property(x=>x.Note).HasColumnType("nvarchar(max)");
        b.Entity<Department>().Property(x=>x.Note).HasColumnType("nvarchar(max)");
        b.Entity<StudentClass>().Property(x=>x.Note).HasColumnType("nvarchar(max)");
        b.Entity<Course>().Property(x=>x.Note).HasColumnType("nvarchar(max)");
        b.Entity<Teacher>().Property(x=>x.Note).HasColumnType("nvarchar(max)");
        b.Entity<ClassSection>().Property(x=>x.Note).HasColumnType("nvarchar(max)");
        b.Entity<Room>().Property(x=>x.Note).HasColumnType("nvarchar(max)");
        b.Entity<Camera>().Property(x=>x.Note).HasColumnType("nvarchar(max)");
        b.Entity<FaceEnrollment>().HasIndex(x=>new{x.StudentId,x.Status}); b.Entity<StudentFaceImage>().HasIndex(x=>x.ObjectKey).IsUnique(); b.Entity<StudentFaceImage>().HasIndex(x=>new{x.StudentId,x.Sha256,x.IsActive}); b.Entity<StudentFaceTemplate>().HasIndex(x=>new{x.StudentId,x.IsActive});
        b.Entity<Video>().HasIndex(x=>new{x.SessionId,x.VideoType}).IsUnique().HasFilter("[VideoType] = 'ANNOTATED_OUTPUT' AND [SessionId] IS NOT NULL AND [Status] <> 'DELETED'"); b.Entity<Video>().HasOne(x=>x.Session).WithMany().HasForeignKey(x=>x.SessionId).OnDelete(DeleteBehavior.Restrict); b.Entity<Video>().HasOne(x=>x.ParentVideo).WithMany().HasForeignKey(x=>x.ParentVideoId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AttendancePolicy>().HasIndex(x=>x.Code).IsUnique(); b.Entity<Session>().Property(x=>x.RowVersion).IsRowVersion(); b.Entity<Session>().HasOne(x=>x.OriginalTeacher).WithMany().HasForeignKey(x=>x.OriginalTeacherId).OnDelete(DeleteBehavior.Restrict); b.Entity<SessionSubstitution>().HasIndex(x=>x.SessionId).IsUnique().HasFilter("[Status] = 'ACTIVE'"); b.Entity<SessionSubstitution>().HasIndex(x=>new{x.SubstituteTeacherId,x.Status}); b.Entity<SessionSubstitution>().HasOne(x=>x.OriginalTeacher).WithMany().HasForeignKey(x=>x.OriginalTeacherId).OnDelete(DeleteBehavior.Restrict); b.Entity<SessionSubstitution>().HasOne(x=>x.SubstituteTeacher).WithMany().HasForeignKey(x=>x.SubstituteTeacherId).OnDelete(DeleteBehavior.Restrict); b.Entity<SessionSubstitution>().HasOne(x=>x.AssignedByUser).WithMany().HasForeignKey(x=>x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict); b.Entity<SessionSubstitution>().HasOne(x=>x.AssignedByTeacher).WithMany().HasForeignKey(x=>x.AssignedByTeacherId).OnDelete(DeleteBehavior.Restrict); b.Entity<SessionStudent>().HasKey(x=>new{x.SessionId,x.StudentId});
        b.Entity<AlertRule>().Property(x=>x.Code).HasMaxLength(64); b.Entity<AlertRule>().Property(x=>x.Name).HasMaxLength(256); b.Entity<AlertRule>().Property(x=>x.BehaviorLabel).HasMaxLength(32); b.Entity<AlertRule>().Property(x=>x.Version).HasMaxLength(32);
        b.Entity<AnalysisJob>().HasIndex(x=>x.CorrelationId).IsUnique(); b.Entity<StableIdentity>().HasIndex(x=>new{x.SessionId,x.StableId}).IsUnique(); b.Entity<AiEventReceipt>().HasIndex(x=>x.ExternalEventId).IsUnique(); b.Entity<BehaviorEvent>().HasIndex(x=>x.ExternalEventId).IsUnique(); b.Entity<Alert>().HasIndex(x=>x.ExternalEventId).IsUnique(); b.Entity<AlertRule>().HasIndex(x=>x.Code).IsUnique(); b.Entity<Attendance>().HasIndex(x=>new{x.SessionId,x.StudentId}).IsUnique(); b.Entity<StudentSessionSummary>().HasIndex(x=>new{x.SessionId,x.StudentId}).IsUnique(); b.Entity<ClassSessionSummary>().HasIndex(x=>x.SessionId).IsUnique();
        ConfigureAcademicModel(b);
        ConfigureSqlRelationships(b);
        ConfigureSqlColumnSizes(b);
        foreach(var p in b.Model.GetEntityTypes().SelectMany(t=>t.GetProperties()).Where(p=>p.ClrType==typeof(decimal)||p.ClrType==typeof(decimal?))){p.SetPrecision(8);p.SetScale(5);}
    }
}
