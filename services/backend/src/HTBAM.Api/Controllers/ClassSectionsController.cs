using System.Text.Json;
using System.Text.RegularExpressions;
using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Controllers;

[ApiController, Authorize(Roles = "LECTURER"), Route("api/class-sections")]
public sealed class ClassSectionsController(AppDbContext db, IAuditService audit) : ControllerBase
{
    private readonly ClassSchedulePlanner planner = new(db);

    [HttpGet]
    public async Task<ActionResult> List([FromQuery] string? q, [FromQuery] long? departmentId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var tid = await AccessScope.TeacherIdAsync(db, User, ct);
        var query = AccessScope.ClassSections(db, tid).AsNoTracking();
        if (departmentId != null) query = query.Where(x => x.Course.DepartmentId == departmentId);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Code.Contains(q) || x.Name.Contains(q) || (x.SourceClassSectionCode != null && x.SourceClassSectionCode.Contains(q)));
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.Code).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.Code, x.SourceClassSectionCode, x.Name, x.CourseId, Course = x.Course.Name, x.Course.Credits,
                x.Course.DepartmentId, Department = db.DepartmentsSet.Where(d => d.Id == x.Course.DepartmentId).Select(d => d.Name).FirstOrDefault(),
                x.TeacherId, Teacher = x.Teacher.FullName, x.AcademicYear, x.Semester, x.Phase, x.IsActive, x.Revision }).ToListAsync(ct);
        var items = new List<object>();
        foreach (var row in rows) items.Add(new { Data = row, Permissions = await AccessScope.ClassPermissions(db, User, row.Id, ct) });
        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult> Get(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanViewClassSectionAsync(db, User, id, ct)) return Forbid();
        var section = await db.ClassSections.AsNoTracking().Include(x => x.Course).Include(x => x.Teacher).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (section is null) return NotFound();
        var department = await db.DepartmentsSet.AsNoTracking().Include(x => x.Faculty).FirstOrDefaultAsync(x => x.Id == section.Course.DepartmentId, ct);
        var lines = await db.ClassSchedules.AsNoTracking().Include(x => x.Room).Where(x => x.ClassSectionId == id && x.IsActive).ToListAsync(ct);
        var term = await db.AcademicTerms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == section.AcademicTermId, ct);
        AcademicDateRange? range = null;
        if (term?.DatesConfirmed == true || term?.IsDevelopment == true)
        {
            try { range = AcademicRules.Range(term, await db.TermPhases.AsNoTracking().Where(x => x.AcademicTermId == term.Id).ToListAsync(ct), section.Course.Credits, section.Phase); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        var protectedIds = await planner.ProtectedSessionIds(await db.SessionsSet.Where(x => x.ClassSectionId == id).Select(x => x.Id).ToArrayAsync(ct), ct);
        return Ok(new { section.Id, section.Code, section.SourceClassSectionCode, section.Name, section.CourseId, Course = section.Course.Name, section.Course.Credits,
            section.TeacherId, Teacher = section.Teacher.FullName, section.Teacher.TeacherCode,
            DepartmentId = department?.Id, Department = department?.Name, FacultyId = department?.FacultyId, Faculty = department?.Faculty.Name,
            section.AcademicTermId, section.Phase, section.AttendancePolicyId, section.AcademicYear, section.Semester,
            section.Note, section.IsActive, section.Revision,
            Term = term, DateRange = range, HasProtectedSessions = protectedIds.Count > 0,
            Permissions = await AccessScope.ClassPermissions(db, User, id, ct),
            Schedules = lines.Select(x => new { x.Id, x.LineKey, x.IsoDayOfWeek, x.StartPeriod, x.EndPeriod, x.BellGroup,
                x.RoomId, Room = x.Room.Name, RoomCode = x.Room.Code, x.Room.RoomType, x.ActivityType, x.StartsOn, x.EndsOn,
                SelectedWeeks = JsonSerializer.Deserialize<int[]>(x.SelectedWeeksJson), x.WeekCalendarId, x.SourceDataJson }),
            Exceptions = await db.ScheduleExceptions.AsNoTracking().Where(x => x.ClassSectionId == id || (x.AcademicTermId == section.AcademicTermId && x.ClassSectionId == null)).ToListAsync(ct)
        });
    }

    [HttpPost("preview")]
    public async Task<ActionResult> PreviewCreate(ClassSectionRequest r, CancellationToken ct)
    {
        if (!await CanCreate(r.CourseId, ct)) return Forbid();
        if (!string.IsNullOrEmpty(r.SourceClassSectionCode)) return BadRequest("Mã lớp nguồn chỉ được ghi qua Nhập Excel.");
        return Ok(Preview(await planner.Prepare(r, null, ct)));
    }

    [HttpPost("{id:long}/preview")]
    public async Task<ActionResult> PreviewUpdate(long id, ClassSectionRequest r, CancellationToken ct)
    {
        var section = await db.ClassSections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (section is null) return NotFound();
        if (!await CanEdit(section, r, ct)) return Forbid();
        if (section.Revision != r.Revision) return Conflict("Lớp vừa được cập nhật. Hãy tải lại trước khi xem ảnh hưởng.");
        return Ok(Preview(await planner.Prepare(r, section, ct)));
    }

    [HttpPost]
    public Task<ActionResult> Create(ClassSectionRequest r, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            if (!await CanCreate(r.CourseId, ct)) return Forbid();
            if (!string.IsNullOrEmpty(r.SourceClassSectionCode)) return BadRequest("Mã lớp nguồn chỉ được ghi qua Nhập Excel.");
            if (r.CreationKey is null || !Regex.IsMatch(r.CreationKey, "^[a-fA-F0-9]{32}$")) return BadRequest("Thiếu khóa chống gửi trùng của form.");
            var hash = ClassSchedulePlanner.Hash(r with { PreviewToken = null });
            var existing = await db.ClassSections.FirstOrDefaultAsync(x => x.CreationKey == r.CreationKey, ct);
            if (existing is not null)
            {
                if (existing.CreationRequestHash != hash || !await AccessScope.CanViewClassSectionAsync(db, User, existing.Id, ct)) return Conflict("Khóa tạo lớp đã được sử dụng cho nội dung khác.");
                return Ok(new { existing.Id, existing.Code, existing.Revision, Replayed = true });
            }
            var plan = await planner.Prepare(r, null, ct);
            if (r.PreviewToken is not null && r.PreviewToken != plan.PreviewToken) return Conflict("Cần xem và xác nhận lịch mới nhất trước khi lưu.");
            var section = new ClassSection { Code = await ClassSectionNumbering.Next(db, plan.Term, r.CourseId, ct), CreationKey = r.CreationKey, CreationRequestHash = hash };
            db.ClassSections.Add(section);
            await planner.Apply(section, r, plan, ct);
            await audit.WriteAsync(UserContext.Id(User), "CLASS_SECTION_CREATE_SCHEDULED", "ClassSection", section.Id.ToString(), new { section.Code, Occurrences = plan.Occurrences.Length }, ct);
            return Ok(new { section.Id, section.Code, section.Revision, Generated = plan.Occurrences.Length });
        }, ct);

    [HttpPut("{id:long}")]
    public Task<ActionResult> Update(long id, ClassSectionRequest r, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            var section = await db.ClassSections.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (section is null) return NotFound();
            if (!section.IsActive || !await CanEdit(section, r, ct)) return Forbid();
            if (section.Revision != r.Revision) return Conflict("Lớp vừa được cập nhật; cần tải lại và xem ảnh hưởng mới.");
            var plan = await planner.Prepare(r, section, ct);
            if (r.PreviewToken != plan.PreviewToken) return Conflict("Cần xem và xác nhận ảnh hưởng mới nhất trước khi áp dụng lịch.");
            await planner.Apply(section, r, plan, ct);
            await audit.WriteAsync(UserContext.Id(User), "CLASS_SECTION_UPDATE_SCHEDULED", "ClassSection", id.ToString(), new { Replaced = plan.ScheduleChanged ? plan.Replaceable.Length : 0, Preserved = plan.Protected.Length, Generated = plan.ScheduleChanged ? plan.Occurrences.Length : 0 }, ct);
            return Ok(new { section.Id, section.Code, section.Revision });
        }, ct);

    [HttpDelete("{id:long}")]
    public Task<ActionResult> Deactivate(long id, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            if (!(await AccessScope.ClassPermissions(db, User, id, ct)).CanDeactivate) return Forbid();
            var section = await db.ClassSections.FindAsync([id], ct);
            if (section is null) return NotFound();
            var future = await db.SessionsSet.Where(x => x.ClassSectionId == id && x.ScheduledStart > DateTime.UtcNow && (x.Status == "DRAFT" || x.Status == "READY")).ToArrayAsync(ct);
            var protectedIds = await planner.ProtectedSessionIds(future.Select(x => x.Id).ToArray(), ct);
            foreach (var session in future.Where(x => !protectedIds.Contains(x.Id))) session.Status = "CANCELLED";
            section.IsActive = false; section.Revision++;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(UserContext.Id(User), "CLASS_SECTION_DEACTIVATE", "ClassSection", id.ToString(), new { Preserved = protectedIds.Count }, ct);
            return NoContent();
        }, ct);

    [HttpGet("{id:long}/grants")]
    public async Task<ActionResult> Grants(long id, CancellationToken ct)
    {
        if (!await CanGrant(id, ct)) return Forbid();
        return Ok(await db.ClassSectionGrants.AsNoTracking().Where(x => x.ClassSectionId == id).ToListAsync(ct));
    }

    [HttpPut("{id:long}/grants/{teacherId:long}")]
    public Task<ActionResult> SetGrant(long id, long teacherId, ClassGrantRequest r, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            if (!await CanGrant(id, ct)) return Forbid();
            if (!await db.ClassSections.AnyAsync(x => x.Id == id && x.IsActive && x.TeacherId == teacherId && x.Teacher.IsActive && x.Teacher.DepartmentId == x.Course.DepartmentId, ct))
                return BadRequest("Chỉ cấp quyền cho giảng viên được phân công, thuộc bộ môn của lớp.");
            var grant = await db.ClassSectionGrants.FirstOrDefaultAsync(x => x.ClassSectionId == id && x.TeacherId == teacherId, ct);
            if (grant is null) { grant = new ClassSectionGrant { ClassSectionId = id, TeacherId = teacherId }; db.ClassSectionGrants.Add(grant); }
            grant.CanAddDetails = r.CanAddDetails; grant.CanEditDetails = r.CanEditDetails; grant.CanDeleteDetails = r.CanDeleteDetails; grant.CanManageStudents = r.CanManageStudents;
            grant.GrantedByUserId = UserContext.Id(User) ?? throw new InvalidOperationException("Thiếu người cấp quyền."); grant.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(UserContext.Id(User), "CLASS_SECTION_GRANT_SET", "ClassSection", id.ToString(), new { TeacherId = teacherId, Permissions = r }, ct);
            return Ok(grant);
        }, ct);

    [HttpGet("{id:long}/students")]
    public async Task<ActionResult> Students(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanViewClassSectionAsync(db, User, id, ct)) return Forbid();
        return Ok(await db.Enrollments.AsNoTracking().Where(x => x.ClassSectionId == id)
            .Select(x => new { x.StudentId, x.Student.StudentCode, x.Student.FullName, x.ClassNote, x.Student.IsActive }).OrderBy(x => x.StudentCode).ToListAsync(ct));
    }

    [HttpPut("{id:long}/students")]
    public Task<ActionResult> SetStudents(long id, ClassRosterRequest r, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            if (!await AccessScope.CanManageRosterAsync(db, User, id, ct)) return Forbid();
            var ids = r.StudentIds.Distinct().ToArray();
            var old = await db.Enrollments.Where(x => x.ClassSectionId == id).ToListAsync(ct);
            if (ids.Length > 1000 || !await ValidRosterStudents(db, id, ids.Except(old.Select(x => x.StudentId)).ToArray(), ct)) return BadRequest("Sinh viên thêm mới phải đang hoạt động và thuộc khoa của lớp.");
            db.Enrollments.RemoveRange(old.Where(x => !ids.Contains(x.StudentId)));
            var previous = old.Select(x => x.StudentId).ToHashSet();
            foreach (var sid in ids.Where(x => !previous.Contains(x))) db.Enrollments.Add(new Enrollment { ClassSectionId = id, StudentId = sid });
            var sessions = await db.SessionsSet.Where(x => x.ClassSectionId == id && (x.Status == "DRAFT" || x.Status == "READY") && x.ScheduledStart > DateTime.UtcNow).ToArrayAsync(ct);
            var protectedIds = await planner.ProtectedSessionIds(sessions.Select(x => x.Id).ToArray(), ct);
            foreach (var session in sessions.Where(x => !protectedIds.Contains(x.Id)))
            {
                var roster = await db.SessionStudentsSet.Where(x => x.SessionId == session.Id).ToListAsync(ct);
                db.SessionStudentsSet.RemoveRange(roster.Where(x => !ids.Contains(x.StudentId)));
                foreach (var sid in ids.Except(roster.Select(x => x.StudentId))) db.SessionStudentsSet.Add(new SessionStudent { SessionId = session.Id, StudentId = sid });
            }
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(UserContext.Id(User), "CLASS_ROSTER_SET", "ClassSection", id.ToString(), new { StudentIds = ids }, ct);
            return NoContent();
        }, ct);

    [HttpPut("{id:long}/students/{studentId:long}/note")]
    public async Task<ActionResult> SetStudentNote(long id, long studentId, ClassStudentNoteRequest r, CancellationToken ct)
    {
        if (!await AccessScope.CanManageRosterAsync(db, User, id, ct)) return Forbid();
        var enrollment = await db.Enrollments.FirstOrDefaultAsync(x => x.ClassSectionId == id && x.StudentId == studentId, ct);
        if (enrollment is null) return NotFound();
        enrollment.ClassNote = r.Note?.Trim(); await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "CLASS_STUDENT_NOTE_UPDATE", "ClassSection", id.ToString(), new { StudentId = studentId }, ct);
        return NoContent();
    }

    public static async Task<bool> ValidRosterStudents(AppDbContext db, long classId, long[] ids, CancellationToken ct)
    {
        var facultyId = await db.ClassSections.Where(x => x.Id == classId).Select(x => db.DepartmentsSet.Where(d => d.Id == x.Course.DepartmentId).Select(d => (long?)d.FacultyId).FirstOrDefault()).FirstOrDefaultAsync(ct);
        return facultyId != null && await db.StudentsSet.CountAsync(x => ids.Contains(x.Id) && x.IsActive && x.StudentClass != null && x.StudentClass.FacultyId == facultyId, ct) == ids.Length;
    }

    private async Task<bool> CanCreate(long courseId, CancellationToken ct)
    {
        if (User.IsInRole("ADMIN")) return false;
        var departmentId = await db.Courses.Where(x => x.Id == courseId && x.IsActive).Select(x => x.DepartmentId).FirstOrDefaultAsync(ct);
        return departmentId != null && await AccessScope.CanManageDepartmentDataAsync(db, User, departmentId.Value, ct);
    }

    private async Task<bool> CanGrant(long id, CancellationToken ct)
    {
        var facultyId = await db.ClassSections.Where(x => x.Id == id).Select(x => db.DepartmentsSet.Where(d => d.Id == x.Course.DepartmentId).Select(d => (long?)d.FacultyId).FirstOrDefault()).FirstOrDefaultAsync(ct);
        return facultyId != null && await AccessScope.CanManageFacultyAsync(db, User, facultyId.Value, ct);
    }

    private async Task<bool> CanEdit(ClassSection current, ClassSectionRequest r, CancellationToken ct)
    {
        if (current.SourceClassSectionCode != r.SourceClassSectionCode) return false;
        var permissions = await AccessScope.ClassPermissions(db, User, current.Id, ct);
        if (permissions.IsManager) return await CanCreate(r.CourseId, ct);
        if (current.CourseId != r.CourseId || current.TeacherId != r.TeacherId || current.AcademicTermId != r.AcademicTermId || current.Phase != r.Phase || current.AttendancePolicyId != r.AttendancePolicyId || current.SourceClassSectionCode != r.SourceClassSectionCode) return false;
        if ((current.Name != r.Name || (current.Note ?? "") != (r.Note ?? "")) && !permissions.CanEditDetails) return false;
        var old = await db.ClassSchedules.AsNoTracking().Where(x => x.ClassSectionId == current.Id && x.IsActive).ToListAsync(ct);
        if (r.Schedules.Any(x => old.All(y => y.LineKey != x.LineKey)) && !permissions.CanAddDetails) return false;
        if (old.Any(x => r.Schedules.All(y => y.LineKey != x.LineKey)) && !permissions.CanDeleteDetails) return false;
        if (old.Any(x => r.Schedules.Any(y => y.LineKey == x.LineKey && !ClassSchedulePlanner.SameLines([x], [y]))) && !permissions.CanEditDetails) return false;
        return permissions.CanAddDetails || permissions.CanEditDetails || permissions.CanDeleteDetails;
    }

    private static object Preview(SchedulePlan p) => new { p.Range, p.PreviewToken, p.ScheduleChanged,
        CreateCount = p.ScheduleChanged ? p.Occurrences.Length : 0, ReplaceCount = p.ScheduleChanged ? p.Replaceable.Length : 0,
        PreserveCount = p.Protected.Length, PreservedSessionIds = p.Protected.Select(x => x.Id),
        AffectedSessions = p.Replaceable.Select(x => new { x.Id, x.ScheduledStart, x.ScheduledEnd, x.Status }), p.Occurrences };
}
