using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Support;

public static class AuthProfileSupport
{
    public static async Task<object?> Get(AppDbContext db, long id, CancellationToken ct)
    {
        var user = await db.UsersSet.AsNoTracking().Include(x => x.UserRoles).ThenInclude(x => x.Role).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (user is null || user.Status != "ACTIVE") return null;
        var teacher = await db.Teachers.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == id && x.IsActive, ct);
        var assignments = teacher is null ? [] : await AccessScope.ActiveAssignments(db).AsNoTracking().Where(x => x.TeacherId == teacher.Id).ToArrayAsync(ct);
        var roles = user.UserRoles.Select(x => x.Role.Name).ToArray();
        var admin = roles.Contains("ADMIN");
        var permissions = new List<string>();
        if (admin) permissions.AddRange(["ACCOUNT_MANAGE", "PASSWORD_RESET", "ACCOUNT_LOCK", "SYSTEM_VIEW", "ROOM_MANAGE", "CAMERA_MANAGE", "CAMERA_TEST", "TECH_CATALOG_DEACTIVATE", "FACULTY_HEAD_ASSIGN", "ACADEMIC_CONFIGURE", "FACULTY_CREATE", "CATALOG_VIEW", "REPORT_VIEW"]);
        if (roles.Contains("TECH_AI")) permissions.AddRange(["CAMERA_TEST", "TECH_CATALOG_DEACTIVATE", "CATALOG_VIEW"]);
        if (!admin && teacher is not null && roles.Contains("LECTURER"))
        {
            permissions.AddRange(["SESSION_VIEW_OWN", "SESSION_REPORT_EXPORT_OWN", "REPORT_VIEW", "CLASS_SECTION_VIEW"]);
            if (assignments.Any(x => x.PositionType == "DEPARTMENT_HEAD"))
                permissions.AddRange(["DEPARTMENT_VIEW", "SUBSTITUTE_ASSIGN", "DEPARTMENT_DATA_MANAGE", "CLASS_SECTION_CREATE", "CLASS_SECTION_UPDATE_SCOPE", "CLASS_SECTION_DEACTIVATE_SCOPE", "CATALOG_VIEW"]);
            if (assignments.Any(x => x.PositionType == "FACULTY_HEAD"))
                permissions.AddRange(["FACULTY_VIEW", "DEPARTMENT_HEAD_ASSIGN", "SUBSTITUTE_ASSIGN", "STUDENT_GLOBAL_VIEW", "STUDENT_EDIT_SCOPE", "STUDENT_CREATE", "STUDENT_IMPORT", "FACE_ENROLL_SCOPE", "FACULTY_UPDATE", "DEPARTMENT_MANAGE", "STUDENT_CLASS_MANAGE", "ATTENDANCE_POLICY_MANAGE", "DEPARTMENT_DATA_MANAGE", "TEACHER_CREATE", "CLASS_SECTION_CREATE", "CLASS_SECTION_UPDATE_SCOPE", "CLASS_SECTION_DEACTIVATE_SCOPE", "CLASS_SECTION_GRANT", "CATALOG_VIEW", "ACADEMIC_CONFIGURE"]);
        }
        return new { user.Id, user.UserName, user.Email, FullName = teacher?.FullName ?? user.FullName, user.Status,
            TeacherId = teacher?.Id, Roles = roles, PositionLabel = PositionLabels.For(assignments, roles),
            ManagementAssignments = assignments.Select(x => new { x.Id, x.PositionType, x.FacultyId, x.DepartmentId, x.EffectiveFrom, x.EffectiveTo, x.IsActive, x.Note }),
            FacultyIds = assignments.Where(x => x.FacultyId != null).Select(x => x.FacultyId),
            DepartmentIds = assignments.Where(x => x.DepartmentId != null).Select(x => x.DepartmentId), Permissions = permissions.Distinct() };
    }
}
