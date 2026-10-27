using HTBAM.Api.Support;
using HTBAM.Application.Interfaces;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace HTBAM.Api.Controllers;

[ApiController, Authorize, Route("api/attendance-policies")]
public sealed class AttendancePoliciesController(AppDbContext db, IAuditService audit) : ControllerBase
{
    [HttpGet] public async Task<ActionResult> List(CancellationToken ct) => Ok(await db.AttendancePoliciesSet.AsNoTracking().OrderBy(x => x.Code).ToListAsync(ct));
    public record Req(string? Code, string Name, decimal PresentThreshold, decimal PartialThreshold, decimal PresentScore, decimal PartialScore, decimal AbsentScore, string Version, bool IsActive = true);
    [HttpPost, Authorize(Roles = "LECTURER")] public Task<ActionResult> Create(Req r, CancellationToken ct) => BusinessTransaction.Run<ActionResult>(db, BusinessCodes.Resource, async () => { if (!await IsFacultyHead(ct)) return Forbid(); var err = Validate(r); if (err != null) return BadRequest(err); var code = await BusinessCodes.Next(db,"CC-",5,db.AttendancePoliciesSet.Select(x=>x.Code),ct); if (await db.AttendancePoliciesSet.AnyAsync(x => x.Code.ToUpper() == code, ct)) return Conflict(ApiValidation.Duplicate("ATTENDANCE_POLICY_CODE_DUPLICATE", "code", $"Mã chính sách chuyên cần {code} đã tồn tại.")); var x = new AttendancePolicy { Code = code, Name = r.Name.Trim(), PresentThreshold = r.PresentThreshold, PartialThreshold = r.PartialThreshold, PresentScore = r.PresentScore, PartialScore = r.PartialScore, AbsentScore = r.AbsentScore, Version = r.Version.Trim(), IsActive = r.IsActive }; db.AttendancePoliciesSet.Add(x); await db.SaveChangesAsync(ct); await audit.WriteAsync(UserContext.Id(User), "ATTENDANCE_POLICY_CREATE", "AttendancePolicy", x.Id.ToString(), new { x.Code }, ct); return Ok(x); },ct);
    [HttpPut("{id:long}"), Authorize(Roles = "LECTURER")] public async Task<ActionResult> Update(long id, Req r, CancellationToken ct) { if (!await IsFacultyHead(ct)) return Forbid(); var x = await db.AttendancePoliciesSet.FindAsync([id], ct); if (x is null) return NotFound(); var err = Validate(r); if (err != null) return BadRequest(err); var code = x.Code; if (await db.AttendancePoliciesSet.AnyAsync(y => y.Id != id && y.Code.ToUpper() == code, ct)) return Conflict(ApiValidation.Duplicate("ATTENDANCE_POLICY_CODE_DUPLICATE", "code", $"Mã chính sách chuyên cần {code} đã tồn tại.")); x.Name = r.Name.Trim(); x.PresentThreshold = r.PresentThreshold; x.PartialThreshold = r.PartialThreshold; x.PresentScore = r.PresentScore; x.PartialScore = r.PartialScore; x.AbsentScore = r.AbsentScore; x.Version = r.Version.Trim(); x.IsActive = r.IsActive; await db.SaveChangesAsync(ct); await audit.WriteAsync(UserContext.Id(User), "ATTENDANCE_POLICY_UPDATE", "AttendancePolicy", id.ToString(), new { x.Code }, ct); return NoContent(); }
    [HttpDelete("{id:long}"), Authorize(Roles = "LECTURER")] public Task<ActionResult> Deactivate(long id, CancellationToken ct) => SetActive(id, false, ct);
    [HttpPost("{id:long}/reactivate"), Authorize(Roles = "LECTURER")] public Task<ActionResult> Reactivate(long id, CancellationToken ct) => SetActive(id, true, ct);
    private async Task<ActionResult> SetActive(long id, bool isActive, CancellationToken ct) { if (!await IsFacultyHead(ct)) return Forbid(); var x = await db.AttendancePoliciesSet.FindAsync([id], ct); if (x is null) return NotFound(); x.IsActive = isActive; await db.SaveChangesAsync(ct); await audit.WriteAsync(UserContext.Id(User), isActive ? "ATTENDANCE_POLICY_REACTIVATE" : "ATTENDANCE_POLICY_DEACTIVATE", "AttendancePolicy", id.ToString(), new { x.Code }, ct); return NoContent(); }
    private async Task<bool> IsFacultyHead(CancellationToken ct) { var tid = await AccessScope.TeacherIdAsync(db, User, ct); return tid is > 0 && await AccessScope.ActiveAssignments(db).AnyAsync(x => x.TeacherId == tid && x.IsActive && x.PositionType == "FACULTY_HEAD", ct); }
    private static string? Validate(Req r) { if (r.PresentThreshold is < 0 or > 1 || r.PartialThreshold is < 0 or > 1 || r.PresentThreshold < r.PartialThreshold) return "Ngưỡng chuyên cần không hợp lệ."; if (string.IsNullOrWhiteSpace(r.Version)) return "Thiếu phiên bản."; return null; }
}
