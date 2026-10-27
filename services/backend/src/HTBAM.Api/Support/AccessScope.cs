using System.Security.Claims;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Support;

public static partial class AccessScope
{
    public static async Task<long?> TeacherIdAsync(AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        if (user.IsInRole("ADMIN")) return -1;
        if (!long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var uid)) return -1;
        return await db.Teachers.AsNoTracking()
            .Where(x => x.UserId == uid && x.IsActive && db.UsersSet.Any(u => u.Id == uid && u.Status == "ACTIVE"))
            .Select(x => (long?)x.Id)
            .FirstOrDefaultAsync(ct) ?? -1;
    }

    public static IQueryable<Session> Sessions(AppDbContext db, ClaimsPrincipal user, long? teacherId)
    {
        if (teacherId is not > 0) return db.SessionsSet.Where(_ => false);
        return ScopedSessions(db, teacherId.Value);
    }

    public static IQueryable<Session> ReportSessions(AppDbContext db, ClaimsPrincipal user, long? teacherId)
    {
        if (user.IsInRole("ADMIN")) return db.SessionsSet;
        return Sessions(db, user, teacherId);
    }

    public static IQueryable<ClassSection> ReportClassSections(AppDbContext db, ClaimsPrincipal user, long? teacherId)
    {
        if (user.IsInRole("ADMIN")) return db.ClassSections;
        var classIds = ReportSessions(db, user, teacherId).Select(x => x.ClassSectionId);
        return db.ClassSections.Where(x => classIds.Contains(x.Id));
    }

    public static IQueryable<Student> ReportStudents(AppDbContext db, ClaimsPrincipal user, long? teacherId)
    {
        if (user.IsInRole("ADMIN")) return db.Students;
        if (teacherId is not > 0) return db.Students.Where(_ => false);
        var sessionIds = ScopedSessions(db, teacherId.Value).Select(x => x.Id);
        var classIds = ScopedSessions(db, teacherId.Value).Select(x => x.ClassSectionId);
        var managedFacultyIds = ActiveAssignments(db).Where(a => a.TeacherId == teacherId && a.PositionType == "FACULTY_HEAD" && a.FacultyId != null).Select(a => a.FacultyId!.Value);
        return db.Students.Where(student =>
            (student.StudentClassId != null && db.StudentClassesSet.Any(c => c.Id == student.StudentClassId && managedFacultyIds.Contains(c.FacultyId))) ||
            db.SessionStudentsSet.Any(x => x.StudentId == student.Id && sessionIds.Contains(x.SessionId)) ||
            db.StudentSessionSummariesSet.Any(x => x.StudentId == student.Id && sessionIds.Contains(x.SessionId)) ||
            db.AttendanceSet.Any(x => x.StudentId == student.Id && sessionIds.Contains(x.SessionId)) ||
            db.Enrollments.Any(x => x.StudentId == student.Id && classIds.Contains(x.ClassSectionId)));
    }

    public static async Task<bool> CanViewClassSectionAsync(AppDbContext db, ClaimsPrincipal user, long classSectionId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        if (teacherId is not > 0) return false;
        return await db.ClassSections.AsNoTracking().AnyAsync(c =>
            c.Id == classSectionId &&
            (
                c.TeacherId == teacherId ||
                ActiveAssignments(db).Any(a =>
                    a.TeacherId == teacherId && a.IsActive &&
                    (
                        (a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId != null && c.Course.DepartmentId == a.DepartmentId) ||
                        (a.PositionType == "FACULTY_HEAD" && a.FacultyId != null && c.Course.DepartmentId != null &&
                            db.DepartmentsSet.Any(d => d.Id == c.Course.DepartmentId && d.FacultyId == a.FacultyId))
                    ))
            ), ct);
    }

    public static async Task<bool> CanManageClassSectionAsync(AppDbContext db, ClaimsPrincipal user, long classSectionId, CancellationToken ct) =>
        (await ClassPermissions(db, user, classSectionId, ct)).CanEditDetails;

    public static Task<bool> CanManageRosterAsync(AppDbContext db, ClaimsPrincipal user, long classSectionId, CancellationToken ct) =>
        CanManageClassRosterAsync(db, user, classSectionId, ct);

    public static async Task<bool> CanViewStudentAsync(AppDbContext db, ClaimsPrincipal user, long studentId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        if (teacherId is not > 0) return false;
        if (await IsStudentInManagedFacultyAsync(db, teacherId.Value, studentId, ct)) return true;
        var sessionIds = ScopedSessions(db, teacherId.Value).Select(s => s.Id);
        if (await db.SessionStudentsSet.AnyAsync(x => x.StudentId == studentId && sessionIds.Contains(x.SessionId), ct)) return true;
        return false;
    }

    public static async Task<bool> CanManageStudentAsync(AppDbContext db, ClaimsPrincipal user, long studentId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        if (teacherId is not > 0) return false;
        if (await IsStudentInManagedFacultyAsync(db, teacherId.Value, studentId, ct)) return true;
        return false;
    }

    public static async Task<bool> CanManageStudentClassAsync(AppDbContext db, ClaimsPrincipal user, long studentClassId, CancellationToken ct)
    {
        var facultyId = await db.StudentClassesSet.Where(x => x.Id == studentClassId).Select(x => (long?)x.FacultyId).FirstOrDefaultAsync(ct);
        return facultyId != null && await CanManageFacultyAsync(db, user, facultyId.Value, ct);
    }

    public static async Task<bool> CanManageDepartmentDataAsync(AppDbContext db, ClaimsPrincipal user, long departmentId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        if (teacherId is not > 0) return false;
        return await ActiveAssignments(db).AsNoTracking().AnyAsync(a => a.TeacherId == teacherId && a.IsActive &&
            ((a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId == departmentId) ||
             (a.PositionType == "FACULTY_HEAD" && a.FacultyId != null && db.DepartmentsSet.Any(d => d.Id == departmentId && d.FacultyId == a.FacultyId))), ct);
    }

    public static Task<bool> CanAccessClassAsync(AppDbContext db, ClaimsPrincipal user, long classSectionId, CancellationToken ct) =>
        CanViewClassSectionAsync(db, user, classSectionId, ct);

    public static async Task<bool> CanViewSessionAsync(AppDbContext db, ClaimsPrincipal user, long sessionId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        if (teacherId is not > 0) return false;
        return await ScopedSessions(db, teacherId.Value).AnyAsync(x => x.Id == sessionId, ct);
    }

    public static Task<bool> CanAccessSessionAsync(AppDbContext db, ClaimsPrincipal user, long sessionId, CancellationToken ct) =>
        CanViewSessionAsync(db, user, sessionId, ct);

    public static async Task<bool> CanOperateSessionAsync(AppDbContext db, ClaimsPrincipal user, long sessionId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        if (teacherId is not > 0) return false;
        return await db.SessionsSet.AsNoTracking().AnyAsync(s =>
            s.Id == sessionId &&
            s.Status != "COMPLETED" && s.Status != "CANCELLED" &&
            (
                (db.SessionSubstitutionsSet.Any(sub => sub.SessionId == s.Id && sub.Status == "ACTIVE")
                    ? db.SessionSubstitutionsSet.Any(sub => sub.SessionId == s.Id && sub.Status == "ACTIVE" && sub.SubstituteTeacherId == teacherId)
                    : (s.OriginalTeacherId == teacherId || (s.OriginalTeacherId == 0 && s.ClassSection.TeacherId == teacherId)))
            ), ct);
    }

    public static async Task<bool> CanManageSessionAsync(AppDbContext db, ClaimsPrincipal user, long sessionId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        if (teacherId is not > 0) return false;
        return await db.SessionsSet.AsNoTracking().AnyAsync(s => s.Id == sessionId &&
            (s.OriginalTeacherId == teacherId ||
             (s.OriginalTeacherId == 0 && s.ClassSection.TeacherId == teacherId) ||
             ActiveAssignments(db).Any(a => a.TeacherId == teacherId && a.IsActive &&
                ((a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId == s.ClassSection.Course.DepartmentId) ||
                 (a.PositionType == "FACULTY_HEAD" && a.FacultyId != null &&
                    db.DepartmentsSet.Any(d => d.Id == s.ClassSection.Course.DepartmentId && d.FacultyId == a.FacultyId))))), ct);
    }

    public static Task<bool> CanExportSessionReportAsync(AppDbContext db, ClaimsPrincipal user, long sessionId, CancellationToken ct) =>
        CanViewReportSessionAsync(db, user, sessionId, ct);

    public static async Task<bool> CanViewReportSessionAsync(AppDbContext db, ClaimsPrincipal user, long sessionId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        return await ReportSessions(db, user, teacherId).AsNoTracking().AnyAsync(x => x.Id == sessionId, ct);
    }

    public static async Task<bool> CanViewReportClassSectionAsync(AppDbContext db, ClaimsPrincipal user, long classSectionId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        return await ReportClassSections(db, user, teacherId).AsNoTracking().AnyAsync(x => x.Id == classSectionId, ct);
    }

    public static async Task<bool> CanViewReportStudentAsync(AppDbContext db, ClaimsPrincipal user, long studentId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        return await ReportStudents(db, user, teacherId).AsNoTracking().AnyAsync(x => x.Id == studentId, ct);
    }

    public static IQueryable<Video> Videos(AppDbContext db, ClaimsPrincipal user, long? teacherId)
    {
        var userId = long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : -1;
        if (user.IsInRole("ADMIN"))
            return db.VideosSet.Where(_ => false);
        if (teacherId is not > 0)
            return db.VideosSet.Where(v => v.VideoType == "INPUT_UPLOAD" && v.UploadedByUserId == userId);

        var sessionIds = ScopedSessions(db, teacherId.Value).Select(s => s.Id);
        var managedUploaderIds = db.Teachers.Where(t => t.UserId != null &&
            ActiveAssignments(db).Any(a => a.TeacherId == teacherId.Value && a.IsActive &&
                ((a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId != null && t.DepartmentId == a.DepartmentId) ||
                 (a.PositionType == "FACULTY_HEAD" && a.FacultyId != null && t.DepartmentId != null &&
                    db.DepartmentsSet.Any(d => d.Id == t.DepartmentId && d.FacultyId == a.FacultyId)))))
            .Select(t => t.UserId!.Value);
        return db.VideosSet.Where(v =>
            (v.VideoType == "ANNOTATED_OUTPUT" && v.SessionId != null && sessionIds.Contains(v.SessionId.Value)) ||
            (v.VideoType == "INPUT_UPLOAD" &&
                (v.UploadedByUserId == userId ||
                 (v.UploadedByUserId != null && managedUploaderIds.Contains(v.UploadedByUserId.Value) &&
                    !db.SessionsSet.Any(s => s.VideoId == v.Id)) ||
                 db.SessionsSet.Any(s => s.VideoId == v.Id && sessionIds.Contains(s.Id)))));
    }

    public static async Task<bool> CanViewVideoAsync(AppDbContext db, ClaimsPrincipal user, long videoId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        return await Videos(db, user, teacherId).AsNoTracking().AnyAsync(v => v.Id == videoId && v.Status != "DELETED", ct);
    }

    public static async Task<bool> CanDeleteVideoAsync(AppDbContext db, ClaimsPrincipal user, long videoId, CancellationToken ct)
    {
        var video = await db.VideosSet.AsNoTracking().Where(v => v.Id == videoId && v.Status != "DELETED")
            .Select(v => new { v.VideoType, v.SessionId, v.UploadedByUserId }).FirstOrDefaultAsync(ct);
        if (video is null) return false;
        if (video.VideoType == "ANNOTATED_OUTPUT")
            return video.SessionId != null && await CanManageSessionAsync(db, user, video.SessionId.Value, ct);
        return video.UploadedByUserId == (long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : -1);
    }

    public static async Task<bool> CanAssignSubstituteAsync(AppDbContext db, ClaimsPrincipal user, long sessionId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        if (teacherId is not > 0) return false;
        return await db.SessionsSet.AsNoTracking().AnyAsync(s =>
            s.Id == sessionId &&
            ActiveAssignments(db).Any(a =>
                a.TeacherId == teacherId && a.IsActive &&
                (
                    (a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId != null && s.ClassSection.Course.DepartmentId == a.DepartmentId) ||
                    (a.PositionType == "FACULTY_HEAD" && a.FacultyId != null && s.ClassSection.Course.DepartmentId != null &&
                        db.DepartmentsSet.Any(d => d.Id == s.ClassSection.Course.DepartmentId && d.FacultyId == a.FacultyId))
                )), ct);
    }

    public static async Task<bool> CanManageDepartmentAsync(AppDbContext db, ClaimsPrincipal user, long departmentId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        if (teacherId is not > 0) return false;
        return await ActiveAssignments(db).AsNoTracking().AnyAsync(a =>
            a.TeacherId == teacherId && a.IsActive && a.PositionType == "FACULTY_HEAD" && a.FacultyId != null &&
            db.DepartmentsSet.Any(d => d.Id == departmentId && d.FacultyId == a.FacultyId), ct);
    }

    public static async Task<bool> CanManageFacultyAsync(AppDbContext db, ClaimsPrincipal user, long facultyId, CancellationToken ct)
    {
        var teacherId = await TeacherIdAsync(db, user, ct);
        if (teacherId is not > 0) return false;
        return await ActiveAssignments(db).AsNoTracking().AnyAsync(a =>
            a.TeacherId == teacherId && a.IsActive && a.PositionType == "FACULTY_HEAD" && a.FacultyId == facultyId, ct);
    }

    private static Task<bool> IsStudentInManagedFacultyAsync(
        AppDbContext db,
        long teacherId,
        long studentId,
        CancellationToken ct)
    {
        var managedFacultyIds = ActiveAssignments(db)
            .Where(a =>
                a.TeacherId == teacherId &&
                a.IsActive &&
                a.PositionType == "FACULTY_HEAD" &&
                a.FacultyId != null)
            .Select(a => a.FacultyId!.Value);

        return db.StudentsSet.AsNoTracking().AnyAsync(student =>
            student.Id == studentId &&
            student.StudentClassId != null &&
            db.StudentClassesSet.Any(studentClass =>
                studentClass.Id == student.StudentClassId &&
                managedFacultyIds.Contains(studentClass.FacultyId)), ct);
    }

    private static IQueryable<Session> ScopedSessions(AppDbContext db, long teacherId) =>
        db.SessionsSet.Where(s =>
            s.OriginalTeacherId == teacherId ||
            (s.OriginalTeacherId == 0 && s.ClassSection.TeacherId == teacherId) ||
            db.SessionSubstitutionsSet.Any(sub => sub.SessionId == s.Id &&
                ((sub.Status == "ACTIVE" && s.Status != "CANCELLED") || sub.Status == "COMPLETED") &&
                sub.SubstituteTeacherId == teacherId) ||
            ActiveAssignments(db).Any(a =>
                a.TeacherId == teacherId && a.IsActive &&
                (
                    (a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId != null && s.ClassSection.Course.DepartmentId == a.DepartmentId) ||
                    (a.PositionType == "FACULTY_HEAD" && a.FacultyId != null && s.ClassSection.Course.DepartmentId != null &&
                        db.DepartmentsSet.Any(d => d.Id == s.ClassSection.Course.DepartmentId && d.FacultyId == a.FacultyId))
                )));
}
