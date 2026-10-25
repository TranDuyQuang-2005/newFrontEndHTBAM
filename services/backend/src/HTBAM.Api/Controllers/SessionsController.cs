using HTBAM.Api.Hubs;
using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Controllers;

[ApiController, Authorize(Roles = "ADMIN,LECTURER,TECH_AI"), Route("api/sessions")]
public sealed class SessionsController(AppDbContext db, ISessionService sessions, IAiClient ai, IConfiguration cfg, IAuditService audit, IHubContext<SessionHub> hub) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult> List([FromQuery] string? status, [FromQuery] long? classSectionId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var teacherId = await AccessScope.TeacherIdAsync(db, User, ct);
        var q = AccessScope.Sessions(db, User, teacherId).AsNoTracking().Include(x => x.ClassSection).ThenInclude(x => x.Teacher).AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status.ToUpper());
        if (classSectionId != null) q = q.Where(x => x.ClassSectionId == classSectionId);
        if (from != null) q = q.Where(x => x.ScheduledStart >= from);
        if (to != null) q = q.Where(x => x.ScheduledStart < to);
        return Ok(await q.OrderByDescending(x => x.ScheduledStart).Select(x => new
        {
            x.Id,
            x.ClassSectionId,
            x.ScheduledStart,
            x.ScheduledEnd,
            x.StartedAt,
            x.EndedAt,
            x.Status,
            x.CameraId,
            x.VideoId,
            x.AttendancePolicyId,
            OriginalTeacherId = x.OriginalTeacherId == 0 ? x.ClassSection.TeacherId : x.OriginalTeacherId,
            OriginalTeacherName = x.OriginalTeacherId == 0 ? x.ClassSection.Teacher.FullName : db.Teachers.Where(t => t.Id == x.OriginalTeacherId).Select(t => t.FullName).FirstOrDefault(),
            ActiveSubstitute = db.SessionSubstitutionsSet.Where(s => s.SessionId == x.Id && s.Status == "ACTIVE").Select(s => new { s.SubstituteTeacherId, SubstituteTeacherName = s.SubstituteTeacher.FullName, s.Status, s.Reason, s.AssignedAt }).FirstOrDefault(),
            Substitution = db.SessionSubstitutionsSet.Where(s => s.SessionId == x.Id && (s.Status == "ACTIVE" || s.Status == "COMPLETED")).OrderByDescending(s => s.AssignedAt).Select(s => new { s.SubstituteTeacherId, SubstituteTeacherName = s.SubstituteTeacher.FullName, s.Status, s.Reason, s.AssignedAt, s.CompletedAt }).FirstOrDefault(),
            IsSubstituteTeaching = teacherId > 0 && db.SessionSubstitutionsSet.Any(s => s.SessionId == x.Id && (s.Status == "ACTIVE" || s.Status == "COMPLETED") && s.SubstituteTeacherId == teacherId),
            HasActiveSubstitution = db.SessionSubstitutionsSet.Any(s => s.SessionId == x.Id && s.Status == "ACTIVE"),
            CanOperate = teacherId > 0 && x.Status != "COMPLETED" && x.Status != "CANCELLED" && (db.SessionSubstitutionsSet.Any(s => s.SessionId == x.Id && s.Status == "ACTIVE")
                ? db.SessionSubstitutionsSet.Any(s => s.SessionId == x.Id && s.Status == "ACTIVE" && s.SubstituteTeacherId == teacherId)
                : (x.OriginalTeacherId == teacherId || (x.OriginalTeacherId == 0 && x.ClassSection.TeacherId == teacherId))),
            CanManage = teacherId > 0 && (x.OriginalTeacherId == teacherId || (x.OriginalTeacherId == 0 && x.ClassSection.TeacherId == teacherId) ||
                db.ManagementAssignmentsSet.Any(a => a.TeacherId == teacherId && a.IsActive &&
                    ((a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId == x.ClassSection.Course.DepartmentId) ||
                     (a.PositionType == "FACULTY_HEAD" && a.FacultyId != null && db.DepartmentsSet.Any(d => d.Id == x.ClassSection.Course.DepartmentId && d.FacultyId == a.FacultyId)))))
        }).ToListAsync(ct));
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult> Get(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanViewSessionAsync(db, User, id, ct)) return Forbid();
        var x = await db.SessionsSet.AsNoTracking().Include(s => s.ClassSection).ThenInclude(c => c.Course).Include(s => s.ClassSection).ThenInclude(c => c.Teacher).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (x is null) return NotFound();
        var roster = await db.SessionStudentsSet.AsNoTracking().Where(r => r.SessionId == id).Include(r => r.Student).Select(r => new { r.StudentId, r.Student.StudentCode, r.Student.FullName }).ToListAsync(ct);
        var substitution = await ActiveSubstitution(id, ct);
        var originalTeacherId = x.OriginalTeacherId == 0 ? x.ClassSection.TeacherId : x.OriginalTeacherId;
        var originalTeacherName = x.OriginalTeacherId == 0 ? x.ClassSection.Teacher.FullName : await db.Teachers.Where(t => t.Id == x.OriginalTeacherId).Select(t => t.FullName).FirstOrDefaultAsync(ct);
        var canOperate = await AccessScope.CanOperateSessionAsync(db, User, id, ct);
        var canManage = await AccessScope.CanManageSessionAsync(db, User, id, ct);
        var cameraInfo = x.CameraId is null
            ? null
            : await db.CamerasSet.AsNoTracking()
                .Where(c => c.Id == x.CameraId.Value)
                .Select(c => new { c.Name, c.RtspUrl })
                .FirstOrDefaultAsync(ct);
        var cameraMode = cameraInfo?.RtspUrl.StartsWith("browser://", StringComparison.OrdinalIgnoreCase) == true
            ? "BROWSER"
            : x.CameraId is not null ? "RTSP" : null;
        return Ok(new { x.Id, x.ClassSectionId, ClassSection = x.ClassSection.Code, Course = x.ClassSection.Course.Name, Teacher = x.ClassSection.Teacher.FullName, OriginalTeacherId = originalTeacherId, OriginalTeacherName = originalTeacherName, ActualTeacherId = substitution?.SubstituteTeacherId ?? originalTeacherId, ActualTeacherName = substitution?.SubstituteTeacherName ?? originalTeacherName, Substitution = substitution, ActiveSubstitution = substitution?.Status == "ACTIVE" ? substitution : null, CanOperate = canOperate, CanManage = canManage, x.RoomId, x.CameraId, CameraName = cameraInfo?.Name, CameraMode = cameraMode, x.VideoId, x.AttendancePolicyId, x.ScheduledStart, x.ScheduledEnd, x.StartedAt, x.EndedAt, x.Status, x.AlertProfile, x.LecturerComment, roster });
    }

    [HttpPost, Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> Create(CreateSessionRequest r, CancellationToken ct)
    {
        if (!await AccessScope.CanViewClassSectionAsync(db, User, r.ClassSectionId, ct)) return Forbid();
        if (r.VideoId is not null && !await AccessScope.CanViewVideoAsync(db, User, r.VideoId.Value, ct)) return Forbid();
        var s = await sessions.CreateAsync(r, ct);
        await audit.WriteAsync(UserContext.Id(User), "SESSION_CREATE", "Session", s.Id.ToString(), new { s.ClassSectionId, s.OriginalTeacherId, s.CameraId, s.VideoId, s.ScheduledStart }, ct);
        return Ok(new { s.Id, s.Status });
    }

    [HttpPut("{id:long}"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> Update(long id, CreateSessionRequest r, CancellationToken ct)
    {
        if (!await AccessScope.CanManageSessionAsync(db, User, id, ct) || !await AccessScope.CanViewClassSectionAsync(db, User, r.ClassSectionId, ct)) return Forbid();
        if (r.VideoId is not null && !await AccessScope.CanViewVideoAsync(db, User, r.VideoId.Value, ct)) return Forbid();
        var current = await db.SessionsSet.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (current is null) return NotFound();
        if (current.Status is not ("DRAFT" or "READY")) return Conflict("Chỉ có thể sửa buổi học ở trạng thái bản nháp hoặc sẵn sàng.");
        if ((r.CameraId is null) == (r.VideoId is null)) return BadRequest("Phải chọn đúng một nguồn camera hoặc video.");
        if (r.AttendancePolicyId <= 0 || !await db.AttendancePoliciesSet.AnyAsync(x => x.Id == r.AttendancePolicyId && x.IsActive, ct)) return BadRequest("Chính sách chuyên cần không hợp lệ.");
        if (r.ScheduledEnd != null && r.ScheduledEnd <= r.ScheduledStart) return BadRequest("Thời gian kết thúc phải sau thời gian bắt đầu.");
        if (r.CameraId != null && await db.SessionsSet.AnyAsync(x => x.Id != id && x.CameraId == r.CameraId && x.Status != "CANCELLED" && x.Status != "COMPLETED" && x.ScheduledStart < (r.ScheduledEnd ?? r.ScheduledStart.AddHours(2)) && (x.ScheduledEnd ?? x.ScheduledStart.AddHours(2)) > r.ScheduledStart, ct)) return Conflict("Camera bị trùng lịch.");
        var classRoster = await db.Enrollments.Where(x => x.ClassSectionId == r.ClassSectionId).Select(x => x.StudentId).ToListAsync(ct);
        var roster = (r.StudentIds?.Distinct().ToArray() ?? Array.Empty<long>());
        if (roster.Length == 0) roster = classRoster.ToArray();
        if (roster.Except(classRoster).Any()) return BadRequest("Danh sách buổi học chứa sinh viên không thuộc lớp học phần.");
        current.ClassSectionId = r.ClassSectionId; current.RoomId = r.RoomId; current.CameraId = r.CameraId; current.VideoId = r.VideoId; current.AttendancePolicyId = r.AttendancePolicyId; current.ScheduledStart = r.ScheduledStart; current.ScheduledEnd = r.ScheduledEnd; current.AlertProfile = string.IsNullOrWhiteSpace(r.AlertProfile) ? "DEFAULT" : r.AlertProfile.Trim();
        if (current.OriginalTeacherId == 0) current.OriginalTeacherId = await db.ClassSections.Where(x => x.Id == r.ClassSectionId).Select(x => x.TeacherId).SingleAsync(ct);
        var old = await db.SessionStudentsSet.Where(x => x.SessionId == id).ToListAsync(ct);
        db.SessionStudentsSet.RemoveRange(old);
        foreach (var sid in roster) db.SessionStudentsSet.Add(new SessionStudent { SessionId = id, StudentId = sid });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "SESSION_UPDATE", "Session", id.ToString(), new { current.ClassSectionId, current.OriginalTeacherId, current.CameraId, current.VideoId }, ct);
        return NoContent();
    }

    [HttpPost("{id:long}/cancel"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> Cancel(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanManageSessionAsync(db, User, id, ct)) return Forbid();
        var s = await db.SessionsSet.FindAsync([id], ct);
        if (s is null) return NotFound();
        if (s.Status is not ("DRAFT" or "READY")) return Conflict("Chỉ có thể hủy buổi học chưa bắt đầu.");
        s.Status = "CANCELLED";
        var substitutions = await db.SessionSubstitutionsSet.Where(x => x.SessionId == id && x.Status == "ACTIVE").ToListAsync(ct);
        foreach (var substitution in substitutions)
        {
            substitution.Status = "CANCELLED";
            substitution.CancelledAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "SESSION_CANCEL", "Session", id.ToString(), null, ct);
        return NoContent();
    }

    [HttpPost("{id:long}/start"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> Start(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanOperateSessionAsync(db, User, id, ct)) return Forbid();
        var baseUrl = cfg["PublicBaseUrl"] ?? $"{Request.Scheme}://{Request.Host}";
        var job = await sessions.StartAsync(id, $"{baseUrl.TrimEnd('/')}/api/ai/events", cfg["AiService:CallbackApiKey"] ?? "", ct);
        await audit.WriteAsync(UserContext.Id(User), "SESSION_START", "Session", id.ToString(), new { job.Id, job.ExternalJobId, job.ModelVersion }, ct);
        await hub.Clients.Group($"session:{id}").SendAsync("sessionStatus", new { sessionId = id, status = "RUNNING" }, ct);
        return Ok(new { job.Id, job.ExternalJobId, job.Status, job.ModelVersion });
    }

    [HttpPost("{id:long}/browser-camera/chunk"), Authorize(Roles = "LECTURER")]
    [RequestSizeLimit(100_000_000)]
    public async Task<ActionResult> BrowserCameraChunk(long id, IFormFile file, CancellationToken ct)
    {
        if (!await AccessScope.CanOperateSessionAsync(db, User, id, ct)) return Forbid();
        if (file is null || file.Length == 0) return BadRequest("Chunk camera rỗng.");

        var session = await db.SessionsSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (session is null) return NotFound();
        if (session.Status != "RUNNING") return Conflict("Buổi học chưa ở trạng thái RUNNING.");
        if (session.CameraId is null) return BadRequest("Buổi học không dùng camera.");

        var camera = await db.CamerasSet.FirstOrDefaultAsync(x => x.Id == session.CameraId.Value, ct);
        if (camera is null || !camera.RtspUrl.StartsWith("browser://", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Buổi học không dùng Camera laptop của trình duyệt.");

        var job = await db.AnalysisJobsSet.AsNoTracking()
            .Where(x => x.SessionId == id && x.Status == "RUNNING")
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (job is null || string.IsNullOrWhiteSpace(job.ExternalJobId))
            return Conflict("Không tìm thấy AI job đang chạy.");

        await using var stream = file.OpenReadStream();
        await ai.UploadBrowserChunkAsync(
            job.ExternalJobId,
            stream,
            file.FileName,
            string.IsNullOrWhiteSpace(file.ContentType) ? "video/webm" : file.ContentType,
            ct);

        camera.Status = "ONLINE";
        camera.LastHealthCheckAt = DateTime.UtcNow;
        camera.LastHealthMessage = "Đang nhận hình ảnh từ camera laptop qua trình duyệt.";
        await db.SaveChangesAsync(ct);
        return Accepted(new { queued = true, file.Length });
    }

    [HttpPost("{id:long}/stop"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> Stop(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanOperateSessionAsync(db, User, id, ct)) return Forbid();
        await sessions.StopAsync(id, ct);
        await audit.WriteAsync(UserContext.Id(User), "SESSION_STOP", "Session", id.ToString(), null, ct);
        await hub.Clients.Group($"session:{id}").SendAsync("sessionStatus", new { sessionId = id, status = "COMPLETED" }, ct);
        return NoContent();
    }

    [HttpPost("{id:long}/retry-finalize"), Authorize(Roles = "LECTURER,TECH_AI")]
    public async Task<ActionResult> Retry(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanOperateSessionAsync(db, User, id, ct)) return Forbid();
        await sessions.RetryFinalizeAsync(id, ct);
        await audit.WriteAsync(UserContext.Id(User), "SESSION_RETRY_FINALIZE", "Session", id.ToString(), null, ct);
        return NoContent();
    }

    public record CommentReq(string? Comment);
    [HttpPut("{id:long}/lecturer-comment"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> Comment(long id, CommentReq r, CancellationToken ct)
    {
        if (!await AccessScope.CanOperateSessionAsync(db, User, id, ct)) return Forbid();
        var s = await db.SessionsSet.FindAsync([id], ct);
        if (s is null) return NotFound();
        s.LecturerComment = string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment.Trim();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "SESSION_COMMENT", "Session", id.ToString(), null, ct);
        return NoContent();
    }



    [HttpPost("{id:long}/fast-overlay")]
    public async Task<ActionResult> FastOverlay(long id, IFormFile file, CancellationToken ct)
    {
        if (!await AccessScope.CanViewSessionAsync(db, User, id, ct)) return Forbid();
        if (file is null || file.Length == 0) return BadRequest("Thiếu khung hình realtime.");
        var externalJobId = await db.AnalysisJobsSet.AsNoTracking()
            .Where(x => x.SessionId == id).OrderByDescending(x => x.Id)
            .Select(x => x.ExternalJobId).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(externalJobId)) return NoContent();
        await using var stream = file.OpenReadStream();
        var json = await ai.FastOverlayAsync(externalJobId, stream, file.FileName, file.ContentType, ct);
        if (string.IsNullOrWhiteSpace(json)) return NoContent();
        return Content(json, "application/json");
    }

    [HttpGet("{id:long}/ai-preview")]
    public async Task<ActionResult> AiPreview(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanViewSessionAsync(db, User, id, ct)) return Forbid();

        var externalJobId = await db.AnalysisJobsSet.AsNoTracking()
            .Where(x => x.SessionId == id)
            .OrderByDescending(x => x.Id)
            .Select(x => x.ExternalJobId)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(externalJobId)) return NoContent();

        var preview = await ai.GetOverlayPreviewAsync(externalJobId, ct);
        if (preview is null) return NoContent();

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return File(preview.Value.Data, preview.Value.ContentType);
    }

    [HttpGet("{id:long}/dashboard")]
    public async Task<ActionResult> Dashboard(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanViewSessionAsync(db, User, id, ct)) return Forbid();
        return Ok(await sessions.DashboardAsync(id, ct));
    }

    [HttpGet("{id:long}/substitution")]
    public async Task<ActionResult> GetSubstitution(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanViewSessionAsync(db, User, id, ct)) return Forbid();
        return Ok(await ActiveSubstitution(id, ct));
    }

    public record AssignSubstituteRequest(long SubstituteTeacherId, string Reason, string? Note);

    [HttpPost("{id:long}/substitution"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> AssignSubstitution(long id, AssignSubstituteRequest r, CancellationToken ct)
    {
        if (!await AccessScope.CanAssignSubstituteAsync(db, User, id, ct)) return Forbid();
        var session = await db.SessionsSet.Include(x => x.ClassSection).ThenInclude(x => x.Course).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (session is null) return NotFound();
        if (session.Status is not ("DRAFT" or "READY")) return Conflict("Chỉ phân công dạy thay cho buổi học chưa bắt đầu.");
        if (await db.SessionSubstitutionsSet.AnyAsync(x => x.SessionId == id && x.Status == "ACTIVE", ct)) return Conflict("Buổi học đã có giảng viên dạy thay đang hoạt động.");

        var originalTeacherId = session.OriginalTeacherId == 0 ? session.ClassSection.TeacherId : session.OriginalTeacherId;
        if (r.SubstituteTeacherId == originalTeacherId) return BadRequest("Giảng viên dạy thay không được là giảng viên gốc.");

        var substitute = await db.Teachers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.SubstituteTeacherId && x.IsActive && x.UserId != null, ct);
        if (substitute is null) return BadRequest("Giảng viên dạy thay không hợp lệ hoặc chưa liên kết tài khoản.");
        if (!await db.UsersSet.AnyAsync(x => x.Id == substitute.UserId && x.Status == "ACTIVE", ct)) return BadRequest("Tài khoản giảng viên dạy thay không hoạt động.");
        if (!await SubstituteInActorScope(session, substitute, ct)) return Forbid();

        var start = session.ScheduledStart;
        var end = session.ScheduledEnd ?? session.ScheduledStart.AddHours(2);
        var conflicts = await db.SessionsSet.AsNoTracking().AnyAsync(s =>
            s.Id != id && s.Status != "CANCELLED" && s.Status != "COMPLETED" &&
            s.ScheduledStart < end && (s.ScheduledEnd ?? s.ScheduledStart.AddHours(2)) > start &&
            (
                s.OriginalTeacherId == r.SubstituteTeacherId ||
                (s.OriginalTeacherId == 0 && s.ClassSection.TeacherId == r.SubstituteTeacherId) ||
                db.SessionSubstitutionsSet.Any(sub => sub.SessionId == s.Id && sub.Status == "ACTIVE" && sub.SubstituteTeacherId == r.SubstituteTeacherId)
            ), ct);
        if (conflicts) return Conflict("Giảng viên dạy thay bị trùng lịch dạy.");

        var actorTeacherId = await AccessScope.TeacherIdAsync(db, User, ct);
        var sub = new SessionSubstitution { SessionId = id, OriginalTeacherId = originalTeacherId, SubstituteTeacherId = r.SubstituteTeacherId, AssignedByUserId = UserContext.Id(User), AssignedByTeacherId = actorTeacherId > 0 ? actorTeacherId : null, Reason = r.Reason.Trim(), Note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim(), Status = "ACTIVE" };
        db.SessionSubstitutionsSet.Add(sub);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "SESSION_SUBSTITUTE_ASSIGN", "SessionSubstitution", sub.Id.ToString(), new { sub.SessionId, sub.OriginalTeacherId, sub.SubstituteTeacherId, sub.AssignedByTeacherId }, ct);
        return Ok(new { sub.Id, sub.SessionId, sub.SubstituteTeacherId, sub.Status });
    }

    [HttpDelete("{id:long}/substitution"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> CancelSubstitution(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanAssignSubstituteAsync(db, User, id, ct)) return Forbid();
        var sessionStatus = await db.SessionsSet.AsNoTracking().Where(x => x.Id == id).Select(x => x.Status).FirstOrDefaultAsync(ct);
        if (sessionStatus is null) return NotFound();
        if (sessionStatus is not ("DRAFT" or "READY")) return Conflict("Chỉ hủy phân công dạy thay khi buổi học ở trạng thái bản nháp hoặc sẵn sàng.");
        var sub = await db.SessionSubstitutionsSet.FirstOrDefaultAsync(x => x.SessionId == id && x.Status == "ACTIVE", ct);
        if (sub is null) return NotFound();
        sub.Status = "CANCELLED";
        sub.CancelledAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "SESSION_SUBSTITUTE_CANCEL", "SessionSubstitution", sub.Id.ToString(), new { sub.SessionId, sub.OriginalTeacherId, sub.SubstituteTeacherId }, ct);
        return NoContent();
    }

    private sealed record SubstitutionView(long Id, long SessionId, long OriginalTeacherId, string OriginalTeacherName, long SubstituteTeacherId, string SubstituteTeacherName, long? AssignedByTeacherId, string Reason, string Status, DateTime AssignedAt, DateTime? CancelledAt, DateTime? CompletedAt, string? Note);

    private async Task<SubstitutionView?> ActiveSubstitution(long sessionId, CancellationToken ct) =>
        await db.SessionSubstitutionsSet.AsNoTracking().Where(x => x.SessionId == sessionId && (x.Status == "ACTIVE" || x.Status == "COMPLETED"))
            .OrderByDescending(x => x.AssignedAt)
            .Select(x => new SubstitutionView(x.Id, x.SessionId, x.OriginalTeacherId, x.OriginalTeacher.FullName, x.SubstituteTeacherId, x.SubstituteTeacher.FullName, x.AssignedByTeacherId, x.Reason, x.Status, x.AssignedAt, x.CancelledAt, x.CompletedAt, x.Note))
            .FirstOrDefaultAsync(ct);

    private async Task<bool> SubstituteInActorScope(Session session, Teacher substitute, CancellationToken ct)
    {
        var actorTeacherId = await AccessScope.TeacherIdAsync(db, User, ct);
        if (actorTeacherId is not > 0) return false;
        var sessionDepartmentId = session.ClassSection.Course.DepartmentId;
        if (sessionDepartmentId is null || substitute.DepartmentId is null) return false;
        return await db.ManagementAssignmentsSet.AsNoTracking().AnyAsync(a =>
            a.TeacherId == actorTeacherId && a.IsActive &&
            (
                (a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId == sessionDepartmentId && substitute.DepartmentId == sessionDepartmentId) ||
                (a.PositionType == "FACULTY_HEAD" && a.FacultyId != null &&
                    db.DepartmentsSet.Any(sd => sd.Id == sessionDepartmentId && sd.FacultyId == a.FacultyId) &&
                    db.DepartmentsSet.Any(td => td.Id == substitute.DepartmentId && td.FacultyId == a.FacultyId))
            ), ct);
    }
}
