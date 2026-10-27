using System.Net.Mail;
using HTBAM.Api.Support;
using HTBAM.Application.Interfaces;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Controllers;

[ApiController, Authorize(Roles = "ADMIN,LECTURER"), Route("api/management-assignments")]
public sealed class ManagementAssignmentsController(AppDbContext db, IAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult> List(CancellationToken ct)
    {
        var teacherId = await AccessScope.TeacherIdAsync(db, User, ct);
        if (!User.IsInRole("ADMIN") && teacherId is not > 0) return Forbid();
        var scope = await AccessScope.ActiveAssignments(db).AsNoTracking().Where(a => a.TeacherId == teacherId).ToListAsync(ct);
        var facultyIds = scope.Where(x => x.PositionType == "FACULTY_HEAD").Select(x => x.FacultyId).ToArray();
        var departmentIds = scope.Where(x => x.PositionType == "DEPARTMENT_HEAD").Select(x => x.DepartmentId).ToArray();
        var admin = User.IsInRole("ADMIN");
        var teachers = await db.Teachers.AsNoTracking().Where(t => admin || t.Id == teacherId ||
            (t.DepartmentId != null && (departmentIds.Contains(t.DepartmentId) || db.DepartmentsSet.Any(d => d.Id == t.DepartmentId && facultyIds.Contains(d.FacultyId)))))
            .OrderBy(x => x.TeacherCode).Select(t => new { t.Id, t.TeacherCode, t.FullName, t.Email, t.UserId, t.DepartmentId, t.IsActive, t.Note,
                Department = db.DepartmentsSet.Where(d => d.Id == t.DepartmentId).Select(d => d.Name).FirstOrDefault(),
                FacultyId = db.DepartmentsSet.Where(d => d.Id == t.DepartmentId).Select(d => (long?)d.FacultyId).FirstOrDefault(),
                Faculty = db.DepartmentsSet.Where(d => d.Id == t.DepartmentId).Select(d => d.Faculty.Name).FirstOrDefault() }).ToListAsync(ct);
        var ids = teachers.Select(t => t.Id).ToArray();
        var assignments = await AccessScope.ActiveAssignments(db).AsNoTracking().Where(a => ids.Contains(a.TeacherId)).ToListAsync(ct);
        return Ok(teachers.Select(t => new { t.Id, t.TeacherCode, t.FullName, t.Email, t.UserId, t.DepartmentId, t.Department, t.FacultyId, t.Faculty, t.IsActive, t.Note,
            Assignments = assignments.Where(a => a.TeacherId == t.Id).Select(a => new { a.Id, a.TeacherId, a.PositionType, a.FacultyId, a.DepartmentId, a.EffectiveFrom, a.EffectiveTo, a.Note }),
            PositionLabel = PositionLabels.For(assignments.Where(a => a.TeacherId == t.Id), ["LECTURER"]) }));
    }

    [HttpGet("vacant-departments")]
    public async Task<ActionResult> VacantDepartments(CancellationToken ct)
    {
        var tid = await AccessScope.TeacherIdAsync(db, User, ct);
        var facultyIds = AccessScope.ActiveAssignments(db).Where(x => x.TeacherId == tid && x.PositionType == "FACULTY_HEAD").Select(x => x.FacultyId);
        return Ok(await db.DepartmentsSet.AsNoTracking().Where(d => d.IsActive && d.Faculty.IsActive && facultyIds.Contains(d.FacultyId) &&
            !AccessScope.ActiveAssignments(db).Any(a => a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId == d.Id))
            .Select(d => new { d.Id, d.Code, d.Name, d.FacultyId }).OrderBy(x => x.Code).ToListAsync(ct));
    }

    public record AssignFacultyHeadRequest(long TeacherId, long FacultyId, string? Note, bool ConfirmTransfer = false);
    public record AssignDepartmentHeadRequest(long TeacherId, long DepartmentId, string? Note);
    public record HeadProfileRequest(string FullName, string Email, string? Note);

    [HttpPost("faculty-head"), Authorize(Roles = "ADMIN")]
    public Task<ActionResult> TransferFacultyHead(AssignFacultyHeadRequest r, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            if (!User.IsInRole("ADMIN")) return Forbid();
            if (!await db.FacultiesSet.AnyAsync(x => x.Id == r.FacultyId && x.IsActive, ct) ||
                !await db.Teachers.AnyAsync(t => t.Id == r.TeacherId && t.IsActive && t.UserId != null && t.DepartmentId != null &&
                    db.UsersSet.Any(u => u.Id == t.UserId && u.Status == "ACTIVE" && u.UserRoles.Any(ur => ur.Role.Name == "LECTURER") && !u.UserRoles.Any(ur => ur.Role.Name == "ADMIN")) &&
                    db.DepartmentsSet.Any(d => d.Id == t.DepartmentId && d.IsActive && d.FacultyId == r.FacultyId), ct))
                return BadRequest("Chọn giảng viên hiện có, có tài khoản giảng viên hoạt động và thuộc khoa cần bổ nhiệm.");
            var current = await AccessScope.ActiveAssignments(db).Where(x => x.PositionType == "FACULTY_HEAD" && x.FacultyId == r.FacultyId).FirstOrDefaultAsync(ct);
            if (current?.TeacherId == r.TeacherId) return Ok(new { current.Id, Replayed = true });
            if (current is not null && !r.ConfirmTransfer) return Conflict(new { code = "TRANSFER_CONFIRMATION_REQUIRED", message = "Khoa đã có Trưởng khoa. Cần xác nhận chuyển giao trước khi áp dụng." });
            await RetireSlot("FACULTY_HEAD", r.FacultyId, null, ct);
            var assignment = NewAssignment("FACULTY_HEAD", r.TeacherId, r.FacultyId, null, r.Note);
            db.ManagementAssignmentsSet.Add(assignment); await db.SaveChangesAsync(ct);
            await audit.WriteAsync(UserContext.Id(User), current is null ? "FACULTY_HEAD_ASSIGN" : "FACULTY_HEAD_TRANSFER", "ManagementAssignment", assignment.Id.ToString(), r, ct);
            return Ok(new { assignment.Id, assignment.TeacherId, assignment.FacultyId, assignment.PositionType });
        }, ct);

    [HttpPost("department-head"), Authorize(Roles = "LECTURER")]
    public Task<ActionResult> AssignDepartmentHead(AssignDepartmentHeadRequest r, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            if (!await AccessScope.CanManageDepartmentAsync(db, User, r.DepartmentId, ct)) return Forbid();
            if (!await db.Teachers.AnyAsync(t => t.Id == r.TeacherId && t.IsActive && t.UserId != null && t.DepartmentId == r.DepartmentId &&
                db.UsersSet.Any(u => u.Id == t.UserId && u.Status == "ACTIVE" && u.UserRoles.Any(ur => ur.Role.Name == "LECTURER") && !u.UserRoles.Any(ur => ur.Role.Name == "ADMIN")), ct))
                return BadRequest("Giảng viên phải có tài khoản hoạt động và thuộc bộ môn được chọn.");
            if (!await db.DepartmentsSet.AnyAsync(x => x.Id == r.DepartmentId && x.IsActive && x.Faculty.IsActive, ct)) return BadRequest("Bộ môn đã ngừng hoạt động.");
            if (await AccessScope.ActiveAssignments(db).AnyAsync(x => x.PositionType == "DEPARTMENT_HEAD" && x.DepartmentId == r.DepartmentId, ct))
                return Conflict("Bộ môn đã có Trưởng bộ môn. Hãy thu hồi chức vụ hiện tại trước khi gán người khác.");
            await RetireSlot("DEPARTMENT_HEAD", null, r.DepartmentId, ct);
            var assignment = NewAssignment("DEPARTMENT_HEAD", r.TeacherId, null, r.DepartmentId, r.Note);
            db.ManagementAssignmentsSet.Add(assignment); await db.SaveChangesAsync(ct);
            await audit.WriteAsync(UserContext.Id(User), "DEPARTMENT_HEAD_ASSIGN", "ManagementAssignment", assignment.Id.ToString(), r, ct);
            return Ok(new { assignment.Id, assignment.TeacherId, assignment.DepartmentId, assignment.PositionType });
        }, ct);

    [HttpDelete("{id:long}")]
    public Task<ActionResult> Revoke(long id, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            var assignment = await db.ManagementAssignmentsSet.FirstOrDefaultAsync(x => x.Id == id && x.IsActive, ct);
            if (assignment is null) return NotFound();
            var allowed = assignment.PositionType == "FACULTY_HEAD" ? User.IsInRole("ADMIN") :
                assignment.DepartmentId != null && await AccessScope.CanManageDepartmentAsync(db, User, assignment.DepartmentId.Value, ct);
            if (!allowed) return Forbid();
            assignment.IsActive = false; assignment.EffectiveTo = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(UserContext.Id(User), "MANAGEMENT_ASSIGNMENT_REVOKE", "ManagementAssignment", id.ToString(), new { assignment.TeacherId, assignment.PositionType, assignment.FacultyId, assignment.DepartmentId }, ct);
            return NoContent();
        }, ct);

    [HttpPut("{id:long}/teacher"), Authorize(Roles = "ADMIN")]
    public async Task<ActionResult> UpdateHeadProfile(long id, HeadProfileRequest r, CancellationToken ct)
    {
        if (!User.IsInRole("ADMIN")) return Forbid();
        var teacherId = await AccessScope.ActiveAssignments(db).Where(x => x.Id == id && x.PositionType == "FACULTY_HEAD").Select(x => (long?)x.TeacherId).FirstOrDefaultAsync(ct);
        if (teacherId is null) return NotFound();
        if (string.IsNullOrWhiteSpace(r.FullName) || !MailAddress.TryCreate(r.Email, out _)) return BadRequest("Họ tên/email chưa hợp lệ.");
        var teacher = await db.Teachers.SingleAsync(x => x.Id == teacherId, ct);
        teacher.FullName = r.FullName.Trim(); teacher.Email = r.Email.Trim(); teacher.Note = r.Note?.Trim();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "FACULTY_HEAD_PROFILE_UPDATE", "Teacher", teacher.Id.ToString(), new { teacher.FullName, teacher.Email }, ct);
        return NoContent();
    }

    private async Task RetireSlot(string position, long? facultyId, long? departmentId, CancellationToken ct)
    {
        foreach (var old in await db.ManagementAssignmentsSet.Where(x => x.PositionType == position && x.IsActive && x.FacultyId == facultyId && x.DepartmentId == departmentId).ToListAsync(ct))
        { old.IsActive = false; old.EffectiveTo ??= DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);
    }

    private ManagementAssignment NewAssignment(string type, long teacherId, long? facultyId, long? departmentId, string? note) =>
        new() { TeacherId = teacherId, PositionType = type, FacultyId = facultyId, DepartmentId = departmentId,
            EffectiveFrom = DateTime.UtcNow, AssignedByUserId = UserContext.Id(User), Note = note?.Trim(), IsActive = true };
}
