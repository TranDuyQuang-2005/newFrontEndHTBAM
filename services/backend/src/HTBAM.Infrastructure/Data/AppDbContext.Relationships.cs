using HTBAM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Infrastructure.Data;

public sealed partial class AppDbContext
{
    private static void ConfigureSqlRelationships(ModelBuilder builder)
    {
        // These scalar IDs have no CLR navigation. Match the relationships already enforced in SQL.
        (Type Source, string Column, Type Target)[] relations = [
            (typeof(Teacher), "UserId", typeof(User)), (typeof(Teacher), "DepartmentId", typeof(Department)),
            (typeof(Course), "DepartmentId", typeof(Department)),
            (typeof(Video), "UploadedByUserId", typeof(User)),
            (typeof(Session), "RoomId", typeof(Room)), (typeof(Session), "CameraId", typeof(Camera)),
            (typeof(Session), "VideoId", typeof(Video)), (typeof(Session), "AttendancePolicyId", typeof(AttendancePolicy)),
            (typeof(IdentityLink), "StudentId", typeof(Student)), (typeof(AiEventReceipt), "SessionId", typeof(Session)),
            (typeof(BehaviorEvent), "SessionId", typeof(Session)), (typeof(BehaviorEvent), "StableIdentityId", typeof(StableIdentity)),
            (typeof(BehaviorEvent), "StudentId", typeof(Student)), (typeof(Alert), "SessionId", typeof(Session)),
            (typeof(Alert), "StableIdentityId", typeof(StableIdentity)), (typeof(Alert), "StudentId", typeof(Student)),
            (typeof(Attendance), "SessionId", typeof(Session)), (typeof(Attendance), "StudentId", typeof(Student)),
            (typeof(StudentSessionSummary), "SessionId", typeof(Session)), (typeof(StudentSessionSummary), "StudentId", typeof(Student)),
            (typeof(ClassSessionSummary), "SessionId", typeof(Session)), (typeof(AuditLog), "UserId", typeof(User))
        ];
        foreach (var (source, column, target) in relations)
        {
            var entity = builder.Model.FindEntityType(source)!;
            if (entity.GetForeignKeys().Any(x => x.Properties.Count == 1 && x.Properties[0].Name == column)) continue;
            builder.Entity(source).HasOne(target).WithMany().HasForeignKey(column).OnDelete(DeleteBehavior.Restrict);
        }
    }
}
