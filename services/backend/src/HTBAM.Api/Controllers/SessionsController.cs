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
    public async Task<ActionResult> List([FromQuery] string? status, [FromQuery] long? classSectionId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct, [FromQuery] long? departmentId = null, [FromQuery] int? page = null, [FromQuery] int pageSize = 20)
    {
        var teacherId = await AccessScope.TeacherIdAsync(db, User, ct);
        var q = AccessScope.Sessions(db, User, teacherId).AsNoTracking().Include(x => x.ClassSection).ThenInclude(x => x.Teacher).AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status.ToUpper());
        if (departmentId != null) q = q.Where(x => x.ClassSection.Course.DepartmentId == departmentId);
        if (classSectionId != null) q = q.Where(x => x.ClassSectionId == classSectionId);
        if (from != null) q = q.Where(x => x.ScheduledStart >= from);
        if (to != null) q = q.Where(x => x.ScheduledStart < to);
        var total = await q.CountAsync(ct);
        var resultQuery = q.OrderByDescending(x => x.ScheduledStart).ThenBy(x => x.Id).Select(x => new
        {
            x.Id,
            x.ClassSectionId,
            ClassSection = x.ClassSection.Code,
            Course = x.ClassSection.Course.Name,
            x.ClassScheduleId,
            x.OccurrenceDate,
            x.StartPeriod,
            x.EndPeriod,
            x.RoomId,
            Room = db.Rooms.Where(r => r.Id == x.RoomId).Select(r => r.Name).FirstOrDefault(),
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
                AccessScope.ActiveAssignments(db).Any(a => a.TeacherId == teacherId && a.IsActive &&
                    ((a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId == x.ClassSection.Course.DepartmentId) ||
                     (a.PositionType == "FACULTY_HEAD" && a.FacultyId != null && db.DepartmentsSet.Any(d => d.Id == x.ClassSection.Course.DepartmentId && d.FacultyId == a.FacultyId)))))
        });
        if (page is null) return Ok(await resultQuery.ToListAsync(ct));
        page = Math.Max(1, page.Value); pageSize = Math.Clamp(pageSize, 1, 100);
        return Ok(new { items = await resultQuery.Skip((page.Value - 1) * pageSize).Take(pageSize).ToListAsync(ct), total, page, pageSize });
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
        var latestJob = await db.AnalysisJobsSet.AsNoTracking().Where(j => j.SessionId == id)
            .OrderByDescending(j => j.Id).Select(j => new { j.Status, j.ErrorMessage }).FirstOrDefaultAsync(ct);
        var finalizeFailure = FinalizeFailure(latestJob?.ErrorMessage);
        return Ok(new { x.Id, x.ClassSectionId, ClassSection = x.ClassSection.Code, Course = x.ClassSection.Course.Name, Teacher = x.ClassSection.Teacher.FullName, OriginalTeacherId = originalTeacherId, OriginalTeacherName = originalTeacherName, ActualTeacherId = substitution?.SubstituteTeacherId ?? originalTeacherId, ActualTeacherName = substitution?.SubstituteTeacherName ?? originalTeacherName, Substitution = substitution, ActiveSubstitution = substitution?.Status == "ACTIVE" ? substitution : null, CanOperate = canOperate, CanManage = canManage, IsScheduled = x.ClassScheduleId != null, x.ClassScheduleId, x.OccurrenceDate, x.StartPeriod, x.EndPeriod, x.RoomId, x.CameraId, CameraName = cameraInfo?.Name, CameraMode = cameraMode, x.VideoId, SourceConfigured = x.CameraId != null || x.VideoId != null, x.AttendancePolicyId, x.ScheduledStart, x.ScheduledEnd, x.StartedAt, x.EndedAt, x.Status, x.AlertProfile, x.LecturerComment, FinalizeJobStatus = latestJob?.Status, FinalizeErrorCode = finalizeFailure.Code, FinalizeError = finalizeFailure.Message, roster });
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
        if (current.ClassScheduleId != null) return Conflict(new { code = "SCHEDULE_PREVIEW_REQUIRED", message = "Buổi thuộc lịch chỉ được thay đổi qua bước xem trước lịch lớp học phần." });
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

    public record SourceRequest(long? CameraId, long? VideoId);

    [HttpPut("{id:long}/source"), Authorize(Roles = "LECTURER")]
    public Task<ActionResult> ConfigureSource(long id, SourceRequest r, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:session-source", async () =>
        {
            if (!await AccessScope.CanOperateSessionAsync(db, User, id, ct)) return Forbid();
            var session = await db.SessionsSet.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (session is null) return NotFound();
            if (session.Status is not ("DRAFT" or "READY") || session.StartedAt != null ||
                await db.AnalysisJobsSet.AnyAsync(x => x.SessionId == id, ct))
                return Conflict("Không thể đổi nguồn buổi học đã bắt đầu hoặc có dữ liệu phân tích.");
            if ((r.CameraId is null) == (r.VideoId is null))
                return BadRequest("Chọn đúng một nguồn camera hoặc video.");

            if (r.CameraId != null)
            {
                var cameraValid = await db.CamerasSet.AnyAsync(x => x.Id == r.CameraId && x.IsActive && x.RoomId == session.RoomId, ct);
                if (!cameraValid) return BadRequest("Camera không hoạt động hoặc không thuộc phòng học.");
                var end = session.ScheduledEnd ?? session.ScheduledStart.AddHours(2);
                var conflict = await db.SessionsSet.AnyAsync(x => x.Id != id && x.CameraId == r.CameraId &&
                    x.Status != "CANCELLED" && x.Status != "COMPLETED" &&
                    x.ScheduledStart < end && (x.ScheduledEnd ?? x.ScheduledStart.AddHours(2)) > session.ScheduledStart, ct);
                if (conflict) return Conflict("Camera đã được gán cho buổi học khác trong khoảng thời gian này.");
            }
            if (r.VideoId != null && (!await AccessScope.CanViewVideoAsync(db, User, r.VideoId.Value, ct) ||
                !await db.VideosSet.AnyAsync(x => x.Id == r.VideoId && x.Status == "READY" && x.VideoType == "INPUT_UPLOAD", ct)))
                return Forbid();

            session.CameraId = r.CameraId;
            session.VideoId = r.VideoId;
            session.Status = "READY";
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(UserContext.Id(User), "SESSION_SOURCE_CONFIGURE", "Session", id.ToString(), r, ct);
            return NoContent();
        }, ct);

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
        var session = await db.SessionsSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (session is null) return NotFound();
        if ((session.CameraId is null) == (session.VideoId is null))
            return Conflict(new { code = "SOURCE_REQUIRED", message = "Buổi học chưa có đúng một nguồn camera hoặc video. Hãy cấu hình nguồn trước khi bắt đầu." });
        if (session.CameraId != null)
        {
            if (!await db.CamerasSet.AnyAsync(x => x.Id == session.CameraId && x.IsActive && x.RoomId == session.RoomId, ct))
                return Conflict(new { code = "SOURCE_INVALID", message = "Camera không còn hoạt động hoặc không thuộc phòng của buổi học." });
            if (await db.SessionsSet.AnyAsync(x => x.Id != id && x.CameraId == session.CameraId &&
                (x.Status == "STARTING" || x.Status == "RUNNING" || x.Status == "FINALIZING"), ct))
                return Conflict(new { code = "CAMERA_IN_USE", message = "Camera đang được một buổi học khác sử dụng." });
        }
        if (session.VideoId != null && !await db.VideosSet.AnyAsync(x => x.Id == session.VideoId && x.Status == "READY" && x.VideoType == "INPUT_UPLOAD", ct))
            return Conflict(new { code = "SOURCE_INVALID", message = "Video nguồn không tồn tại hoặc chưa sẵn sàng." });
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
        var status = await db.SessionsSet.Where(x => x.Id == id).Select(x => x.Status).SingleAsync(ct);
        await hub.Clients.Group($"session:{id}").SendAsync("sessionStatus", new { sessionId = id, status }, ct);
        return status == "COMPLETED" ? NoContent() : Accepted(new { status });
    }

    [HttpPost("{id:long}/retry-finalize"), Authorize(Roles = "LECTURER,TECH_AI")]
    public async Task<ActionResult> Retry(long id, CancellationToken ct)
    {
        var currentStatus = await db.SessionsSet.AsNoTracking().Where(x => x.Id == id).Select(x => x.Status).FirstOrDefaultAsync(ct);
        if (currentStatus is null) return NotFound();
        var allowed = currentStatus == "COMPLETED"
            ? await AccessScope.CanViewSessionAsync(db, User, id, ct)
            : await AccessScope.CanOperateSessionAsync(db, User, id, ct);
        if (!allowed) return Forbid();
        await sessions.RetryFinalizeAsync(id, ct);
        await audit.WriteAsync(UserContext.Id(User), "SESSION_RETRY_FINALIZE", "Session", id.ToString(), null, ct);
        var status = await db.SessionsSet.Where(x => x.Id == id).Select(x => x.Status).SingleAsync(ct);
        await hub.Clients.Group($"session:{id}").SendAsync("sessionStatus", new { sessionId = id, status }, ct);
        return status == "COMPLETED" ? NoContent() : Accepted(new { status });
    }

    public record CommentReq(string? Comment);

    private static (string? Code, string? Message) FinalizeFailure(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        if (raw.Contains("did not drain", StringComparison.OrdinalIgnoreCase))
            return ("AI_DRAIN_TIMEOUT", "AI chưa xử lý hết các đoạn video đã nhận trong thời gian chờ. Dữ liệu vẫn được giữ để thử hoàn tất lại.");
        if (raw.Contains("D6 không tạo được segment", StringComparison.OrdinalIgnoreCase))
            return ("AI_NO_BEHAVIOR_WINDOW", "Một đoạn video không đủ quan sát liên tục để tạo cửa sổ hành vi D6. Đoạn này phải được giữ với 0 quan sát, không được tạo nhãn giả.");
        if (raw.Contains("callback", StringComparison.OrdinalIgnoreCase) || raw.Contains("409", StringComparison.OrdinalIgnoreCase))
            return ("AI_CALLBACK_REJECTED", "Một số sự kiện AI chưa được backend chấp nhận. Hãy thử hoàn tất lại sau khi kiểm tra trạng thái dịch vụ AI.");
        if (raw.Contains("artifact", StringComparison.OrdinalIgnoreCase) || raw.Contains("video đầu ra", StringComparison.OrdinalIgnoreCase))
            return ("AI_ARTIFACT_FAILED", "AI chưa tạo hoặc chưa lưu được video kết quả bắt buộc.");
        if (raw.Contains("timeout", StringComparison.OrdinalIgnoreCase) || raw.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return ("AI_TIMEOUT", "Dịch vụ AI chưa phản hồi trong thời gian chờ. Có thể thử hoàn tất lại mà không tạo job mới.");
        return ("AI_FINALIZE_FAILED", "Dịch vụ AI chưa hoàn tất được dữ liệu cuối của buổi học. Dữ liệu đã nhận vẫn được giữ để thử lại.");
    }

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
