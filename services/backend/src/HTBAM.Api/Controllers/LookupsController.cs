using System.Globalization;
using System.Text;
using HTBAM.Api.Support;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Controllers;

public sealed record LookupItem { public long Id { get; init; } public string Code { get; init; } = ""; public string Name { get; init; } = ""; public bool IsActive { get; init; } = true; }

[ApiController, Authorize, Route("api/lookups")]
public sealed class LookupsController(AppDbContext db) : ControllerBase
{
    [HttpGet("{resource}")]
    public async Task<ActionResult> Get(string resource, [FromQuery] string? q, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10, [FromQuery] long? id = null, [FromQuery] long? departmentId = null,
        [FromQuery] long? facultyId = null, [FromQuery] long? classSectionId = null, [FromQuery] long? roomId = null,
        [FromQuery] bool includeInactive = false, [FromQuery] bool vacantOnly = false, CancellationToken ct = default, [FromQuery] bool eligibleHead = false)
    {
        var tid = await AccessScope.TeacherIdAsync(db, User, ct);
        var admin = User.IsInRole("ADMIN");
        var ownDepartment = db.Teachers.Where(t => t.Id == tid).Select(t => t.DepartmentId);
        var assignments = AccessScope.ActiveAssignments(db).Where(a => a.TeacherId == tid);
        var visibleDepartments = db.DepartmentsSet.Where(d => admin || ownDepartment.Contains(d.Id) || assignments.Any(a =>
            a.DepartmentId == d.Id || (a.PositionType == "FACULTY_HEAD" && a.FacultyId == d.FacultyId)));
        var visibleFacultyIds = visibleDepartments.Select(d => d.FacultyId);
        IQueryable<LookupItem> rows;
        switch (resource)
        {
            case "faculties": rows = db.FacultiesSet.Where(x => admin || visibleFacultyIds.Contains(x.Id)).Select(x => new LookupItem { Id = x.Id, Code = x.Code, Name = x.Name, IsActive = x.IsActive }); break;
            case "departments":
                var departments = visibleDepartments.Where(x => facultyId == null || x.FacultyId == facultyId);
                if (vacantOnly) departments = departments.Where(x => assignments.Any(a => a.PositionType == "FACULTY_HEAD" && a.FacultyId == x.FacultyId) &&
                    !AccessScope.ActiveAssignments(db).Any(a => a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId == x.Id));
                rows = departments.Select(x => new LookupItem { Id = x.Id, Code = x.Code, Name = x.Name, IsActive = x.IsActive }); break;
            case "courses": rows = db.Courses.Where(x => x.DepartmentId != null && visibleDepartments.Any(d => d.Id == x.DepartmentId) && (departmentId == null || x.DepartmentId == departmentId)).Select(x => new LookupItem { Id = x.Id, Code = x.Code, Name = x.Name, IsActive = x.IsActive }); break;
            case "faculty-initial-heads":
                if (!admin) return Forbid();
                rows = FacultyInitialization.EligibleTeachers(db).Select(x => new LookupItem { Id = x.Id, Code = x.TeacherCode, Name = x.FullName, IsActive = x.IsActive }); break;
            case "teachers": rows = db.Teachers.Where(x => (admin || x.Id == tid || visibleDepartments.Any(d => d.Id == x.DepartmentId)) &&
                (departmentId == null || x.DepartmentId == departmentId) && (facultyId == null || db.DepartmentsSet.Any(d => d.Id == x.DepartmentId && d.FacultyId == facultyId)) &&
                (!eligibleHead || (x.UserId != null && db.DepartmentsSet.Any(d => d.Id == x.DepartmentId && d.IsActive && d.Faculty.IsActive) && db.UsersSet.Any(u => u.Id == x.UserId && u.Status == "ACTIVE" && u.UserRoles.Any(ur => ur.Role.Name == "LECTURER") && !u.UserRoles.Any(ur => ur.Role.Name == "ADMIN")))))
                .Select(x => new LookupItem { Id = x.Id, Code = x.TeacherCode, Name = x.FullName, IsActive = x.IsActive }); break;
            case "rooms": rows = db.Rooms.Select(x => new LookupItem { Id = x.Id, Code = x.Code, Name = x.Name + (x.RoomType == "ZOOM" ? " (Zoom)" : ""), IsActive = x.IsActive }); break;
            case "cameras": rows = db.CamerasSet.Where(x => roomId == null || x.RoomId == roomId).Select(x => new LookupItem { Id = x.Id, Code = x.Code, Name = x.Name, IsActive = x.IsActive }); break;
            case "videos": rows = AccessScope.Videos(db, User, tid).Where(x => x.Status == "READY" && x.VideoType == "INPUT_UPLOAD").Select(x => new LookupItem { Id = x.Id, Code = "VIDEO-" + x.Id, Name = x.FileName, IsActive = true }); break;
            case "class-sections": rows = AccessScope.ClassSections(db, tid).Where(x => departmentId == null || x.Course.DepartmentId == departmentId).Select(x => new LookupItem { Id = x.Id, Code = x.Code, Name = x.Name, IsActive = x.IsActive }); break;
            case "student-classes": rows = db.StudentClassesSet.Where(x => (admin || visibleFacultyIds.Contains(x.FacultyId)) && (facultyId == null || x.FacultyId == facultyId)).Select(x => new LookupItem { Id = x.Id, Code = x.Code, Name = x.Name, IsActive = x.IsActive }); break;
            case "policies": rows = db.AttendancePoliciesSet.Select(x => new LookupItem { Id = x.Id, Code = x.Code, Name = x.Name, IsActive = x.IsActive }); break;
            case "users":
                if (departmentId == null || !await AccessScope.CanCreateTeacher(db, User, departmentId.Value, ct)) return Forbid();
                rows = db.UsersSet.Where(x => x.Status == "ACTIVE" && x.UserRoles.Any(ur => ur.Role.Name == "LECTURER") && !x.UserRoles.Any(ur => ur.Role.Name == "ADMIN") && !db.Teachers.Any(t => t.UserId == x.Id))
                    .Select(x => new LookupItem { Id = x.Id, Code = x.UserName, Name = x.FullName, IsActive = true }); break;
            case "students":
                if (classSectionId == null || !await AccessScope.CanManageRosterAsync(db, User, classSectionId.Value, ct)) return Forbid();
                var faculty = await db.ClassSections.Where(x => x.Id == classSectionId).Select(x => db.DepartmentsSet.Where(d => d.Id == x.Course.DepartmentId).Select(d => (long?)d.FacultyId).FirstOrDefault()).FirstOrDefaultAsync(ct);
                rows = db.StudentsSet.Where(x => x.StudentClass != null && x.StudentClass.FacultyId == faculty).Select(x => new LookupItem { Id = x.Id, Code = x.StudentCode, Name = x.FullName, IsActive = x.IsActive }); break;
            default: return NotFound();
        }
        if (id is not null) rows = rows.Where(x => x.Id == id);
        else if (!includeInactive) rows = rows.Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var keyword = Normalize(q.Trim()[..Math.Min(q.Trim().Length, 128)]);
            if (db.Database.IsSqlServer())
                rows = rows.Where(x => EF.Functions.Collate((x.Code + " " + x.Name).Replace("đ", "d").Replace("Đ", "D"), "Latin1_General_100_CI_AI").Contains(keyword));
            else rows = rows.AsEnumerable().Where(x => Normalize(x.Code + " " + x.Name).Contains(keyword)).AsQueryable();
        }
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        // InMemory branch is used only by tests; SQL applies filtering/paging before materialization.
        if (rows.Provider is Microsoft.EntityFrameworkCore.Query.IAsyncQueryProvider)
        {
            var total = await rows.CountAsync(ct);
            return Ok(new { items = await rows.OrderBy(x => x.Code).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(ct), total, page, pageSize, hasMore = page * pageSize < total });
        }
        var matches = rows.OrderBy(x => x.Code).ThenBy(x => x.Id).ToArray();
        return Ok(new { items = matches.Skip((page - 1) * pageSize).Take(pageSize), total = matches.Length, page, pageSize, hasMore = page * pageSize < matches.Length });
    }

    public static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormD).Where(x => CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark).Select(x => x is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(x)).ToArray());
}
