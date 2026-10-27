using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;

namespace HTBAM.Api.Support;

public static class FacultyInitialization
{
    // Moving a first head into the new department must not change teaching or appointment history.
    public static IQueryable<Teacher> EligibleTeachers(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        return db.Teachers.Where(t => t.IsActive && t.UserId != null &&
            db.UsersSet.Any(u => u.Id == t.UserId && u.Status == "ACTIVE" &&
                u.UserRoles.Any(ur => ur.Role.Name == "LECTURER") && !u.UserRoles.Any(ur => ur.Role.Name == "ADMIN")) &&
            !db.ClassSections.Any(c => c.TeacherId == t.Id) &&
            !db.SessionsSet.Any(s => s.OriginalTeacherId == t.Id) &&
            !db.SessionSubstitutionsSet.Any(s => s.OriginalTeacherId == t.Id || s.SubstituteTeacherId == t.Id) &&
            !db.ManagementAssignmentsSet.Any(a => a.TeacherId == t.Id && a.IsActive && (a.EffectiveTo == null || a.EffectiveTo > now)));
    }
}
