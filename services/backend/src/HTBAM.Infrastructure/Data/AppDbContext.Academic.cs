using HTBAM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Infrastructure.Data;

public sealed partial class AppDbContext
{
    public DbSet<ClassSectionCounter> ClassSectionCounters => Set<ClassSectionCounter>();
    public DbSet<ClassSectionImportPreview> ClassSectionImportPreviews => Set<ClassSectionImportPreview>();
    public DbSet<AcademicTerm> AcademicTerms => Set<AcademicTerm>();
    public DbSet<TermPhase> TermPhases => Set<TermPhase>();
    public DbSet<AcademicWeekCalendar> AcademicWeekCalendars => Set<AcademicWeekCalendar>();
    public DbSet<BellTimeVersion> BellTimeVersions => Set<BellTimeVersion>();
    public DbSet<BellPeriod> BellPeriods => Set<BellPeriod>();
    public DbSet<ClassSchedule> ClassSchedules => Set<ClassSchedule>();
    public DbSet<ScheduleException> ScheduleExceptions => Set<ScheduleException>();
    public DbSet<ClassSectionGrant> ClassSectionGrants => Set<ClassSectionGrant>();

    private static void ConfigureAcademicModel(ModelBuilder b)
    {
        b.Entity<ClassSectionCounter>().ToTable("ClassSectionCounters");
        b.Entity<ClassSectionCounter>().HasIndex(x => new { x.AcademicTermId, x.CourseId }).IsUnique();
        b.Entity<ClassSectionCounter>().HasOne<AcademicTerm>().WithMany().HasForeignKey(x => x.AcademicTermId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ClassSectionCounter>().HasOne<Course>().WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ClassSectionImportPreview>().ToTable("ClassSectionImportPreviews");
        b.Entity<ClassSectionImportPreview>().Property(x => x.Token).HasMaxLength(64).IsUnicode(false);
        b.Entity<ClassSectionImportPreview>().HasIndex(x => x.Token).IsUnique();
        b.Entity<ClassSectionImportPreview>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ClassSection>().HasIndex(x => new { x.AcademicTermId, x.SourceClassSectionCode }).IsUnique().HasFilter("[SourceClassSectionCode] IS NOT NULL AND [SourceClassSectionCode] <> ''");
        b.Entity<AcademicTerm>().Property(x => x.Status).HasMaxLength(16);
        b.Entity<ClassSection>().HasOne<AttendancePolicy>().WithMany().HasForeignKey(x => x.AttendancePolicyId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ClassSectionGrant>().HasOne<User>().WithMany().HasForeignKey(x => x.GrantedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BellTimeVersion>().HasIndex(x => x.Code).IsUnique();
        b.Entity<ScheduleException>().HasIndex(x => new { x.AcademicTermId, x.ClassSectionId, x.Date }).IsUnique().HasFilter(null);
        b.Entity<AcademicTerm>().ToTable("AcademicTerms", t => t.HasCheckConstraint("CK_AcademicTerm_Semester", "[Semester] IN (1,2)"));
        b.Entity<AcademicTerm>().Property(x => x.AcademicYear).HasMaxLength(16);
        b.Entity<AcademicTerm>().Property(x => x.DivisionPolicy).HasMaxLength(32);
        b.Entity<AcademicTerm>().HasIndex(x => new { x.AcademicYear, x.Semester }).IsUnique();
        b.Entity<TermPhase>().ToTable("TermPhases");
        b.Entity<AcademicWeekCalendar>().ToTable("AcademicWeekCalendars");
        b.Entity<AcademicWeekCalendar>().Property(x => x.Name).HasMaxLength(128);
        b.Entity<AcademicWeekCalendar>().HasIndex(x => new { x.AcademicTermId, x.Name }).IsUnique();
        b.Entity<TermPhase>().HasIndex(x => new { x.AcademicTermId, x.Credits, x.Number }).IsUnique();
        b.Entity<BellTimeVersion>().ToTable("BellTimeVersions");
        b.Entity<BellTimeVersion>().Property(x => x.Code).HasMaxLength(64);
        b.Entity<BellTimeVersion>().Property(x => x.Group).HasMaxLength(1);
        b.Entity<BellTimeVersion>().HasIndex(x => new { x.Group, x.EffectiveFrom }).IsUnique();
        b.Entity<BellPeriod>().ToTable("BellPeriods");
        b.Entity<BellPeriod>().HasIndex(x => new { x.BellTimeVersionId, x.Number }).IsUnique();
        b.Entity<ClassSection>().Property(x => x.SourceClassSectionCode).HasMaxLength(128);
        b.Entity<ClassSection>().Property(x => x.CreationKey).HasMaxLength(32);
        b.Entity<ClassSection>().Property(x => x.CreationRequestHash).HasMaxLength(64);
        b.Entity<ClassSection>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<ClassSection>().HasIndex(x => x.CreationKey).IsUnique().HasFilter("[CreationKey] IS NOT NULL");
        b.Entity<ClassSection>().HasOne(x => x.AcademicTerm).WithMany().HasForeignKey(x => x.AcademicTermId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Room>().Property(x => x.RoomType).HasMaxLength(32);
        b.Entity<Student>().Property(x => x.SourceAnonymousCode).HasMaxLength(128);
        b.Entity<Student>().HasIndex(x => x.SourceAnonymousCode).IsUnique().HasFilter("[SourceAnonymousCode] IS NOT NULL AND [SourceAnonymousCode] <> ''");
        b.Entity<ClassSchedule>().ToTable("ClassSchedules");
        b.Entity<ClassSchedule>().Property(x => x.LineKey).HasMaxLength(32);
        b.Entity<ClassSchedule>().Property(x => x.BellGroup).HasMaxLength(1);
        b.Entity<ClassSchedule>().Property(x => x.ActivityType).HasMaxLength(2);
        b.Entity<ClassSchedule>().HasIndex(x => new { x.ClassSectionId, x.Revision, x.LineKey }).IsUnique();
        b.Entity<ClassSchedule>().HasOne(x => x.ClassSection).WithMany().HasForeignKey(x => x.ClassSectionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ClassSchedule>().HasOne(x => x.Room).WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ClassSchedule>().HasOne<AcademicWeekCalendar>().WithMany().HasForeignKey(x => x.WeekCalendarId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ScheduleException>().ToTable("ScheduleExceptions");
        b.Entity<ScheduleException>().HasOne(x => x.ClassSection).WithMany().HasForeignKey(x => x.ClassSectionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ClassSectionGrant>().ToTable("ClassSectionGrants");
        b.Entity<ClassSectionGrant>().HasIndex(x => new { x.ClassSectionId, x.TeacherId }).IsUnique();
        b.Entity<ClassSectionGrant>().HasOne(x => x.ClassSection).WithMany().HasForeignKey(x => x.ClassSectionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ClassSectionGrant>().HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Session>().HasOne(x => x.ClassSchedule).WithMany().HasForeignKey(x => x.ClassScheduleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Session>().ToTable("Sessions", t => t.HasCheckConstraint("CK_Session_Source", "([CameraId] IS NOT NULL AND [VideoId] IS NULL) OR ([CameraId] IS NULL AND [VideoId] IS NOT NULL) OR ([ClassScheduleId] IS NOT NULL AND ([Status] IN ('DRAFT','CANCELLED') OR ([IsDevelopment] = 1 AND [Status] = 'COMPLETED')) AND [CameraId] IS NULL AND [VideoId] IS NULL)"));
        b.Entity<Session>().HasOne<BellTimeVersion>().WithMany().HasForeignKey(x => x.BellTimeVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Session>().HasIndex(x => new { x.ClassScheduleId, x.OccurrenceDate }).IsUnique().HasFilter("[ClassScheduleId] IS NOT NULL AND [OccurrenceDate] IS NOT NULL");
    }
}
