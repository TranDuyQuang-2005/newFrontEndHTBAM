using System.Security.Claims;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Support;

public record ClassPermissions(bool IsManager, bool CanAddDetails, bool CanEditDetails, bool CanDeleteDetails, bool CanManageStudents, bool CanDeactivate);

public static partial class AccessScope
{
    public static IQueryable<ManagementAssignment> ActiveAssignments(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        return db.ManagementAssignmentsSet.Where(x => x.IsActive && x.EffectiveFrom <= now && (x.EffectiveTo == null || x.EffectiveTo > now));
    }

    public static IQueryable<ClassSection> ClassSections(AppDbContext db, long? teacherId)
    {
        if (teacherId is not > 0) return db.ClassSections.Where(_ => false);
        return db.ClassSections.Where(c => c.TeacherId == teacherId || ActiveAssignments(db).Any(a => a.TeacherId == teacherId &&
            ((a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId == c.Course.DepartmentId) ||
             (a.PositionType == "FACULTY_HEAD" && a.FacultyId != null && db.DepartmentsSet.Any(d => d.Id == c.Course.DepartmentId && d.FacultyId == a.FacultyId)))));
    }

    public static async Task<ClassPermissions> ClassPermissions(AppDbContext db, ClaimsPrincipal user, long id, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        var section = await db.ClassSections.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.TeacherId, x.Course.DepartmentId, x.IsActive }).FirstOrDefaultAsync(ct);
        if (teacherId is not > 0 || section is null) return new(false, false, false, false, false, false);
        var manager = section.DepartmentId != null && await CanManageDepartmentDataAsync(db, user, section.DepartmentId.Value, ct);
        if (manager) return new(true, true, true, true, true, true);
        if (section.TeacherId != teacherId || !section.IsActive) return new(false, false, false, false, false, false);
        var grant = await db.ClassSectionGrants.AsNoTracking().FirstOrDefaultAsync(x => x.ClassSectionId == id && x.TeacherId == teacherId, ct);
        return new(false, grant?.CanAddDetails ?? false, grant?.CanEditDetails ?? false,
            grant?.CanDeleteDetails ?? false, grant?.CanManageStudents ?? false, false);
    }

    public static async Task<bool> CanManageClassRosterAsync(AppDbContext db, ClaimsPrincipal user, long id, CancellationToken ct) =>
        (await ClassPermissions(db, user, id, ct)).CanManageStudents;

    public static async Task<bool> CanManageGlobalStudents(AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        return teacherId > 0 && await ActiveAssignments(db).AnyAsync(x => x.TeacherId == teacherId && x.PositionType == "FACULTY_HEAD", ct);
    }

    public static async Task<bool> CanConfigureAcademicsAsync(AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        if (user.IsInRole("ADMIN")) return true;
        var teacherId = await TeacherIdAsync(db, user, ct);
        return teacherId > 0 && await ActiveAssignments(db).AsNoTracking()
            .AnyAsync(x => x.TeacherId == teacherId && x.PositionType == "FACULTY_HEAD", ct);
    }

    public static async Task<bool> CanCreateTeacher(AppDbContext db, ClaimsPrincipal user, long departmentId, CancellationToken ct) =>
        !user.IsInRole("ADMIN") && await CanManageDepartmentAsync(db, user, departmentId, ct);
}
