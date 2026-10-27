using ClosedXML.Excel;
using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace HTBAM.Api.Controllers;

[ApiController, Authorize, Route("api/reports")]
public sealed class ReportsController(AppDbContext db, IAutoCommentService autoComments) : ControllerBase
{
    [HttpGet("options")]
    public async Task<ActionResult> Options([FromQuery] string kind = "sessions", CancellationToken ct = default)
    {
        var teacherId = await AccessScope.TeacherIdAsync(db, User, ct);
        if (kind == "sessions")
        {
            var rows = await AccessScope.ReportSessions(db, User, teacherId).AsNoTracking()
                .OrderByDescending(x => x.ScheduledStart)
                .Select(x => new { x.Id, Label = x.ClassSection.Code + " · " + x.ClassSection.Course.Name + " · #" + x.Id, x.Status, x.ScheduledStart })
                .ToListAsync(ct);
            return Ok(rows);
        }
        if (kind == "class-sections")
        {
            var rows = await AccessScope.ReportClassSections(db, User, teacherId).AsNoTracking()
                .OrderBy(x => x.Code)
                .Select(x => new { x.Id, Label = x.Code + " · " + x.Course.Name })
                .ToListAsync(ct);
            return Ok(rows);
        }
        if (kind == "students")
        {
            var rows = await AccessScope.ReportStudents(db, User, teacherId).AsNoTracking()
                .OrderBy(x => x.StudentCode)
                .Select(x => new { x.Id, Label = x.StudentCode + " · " + x.FullName })
                .ToListAsync(ct);
            return Ok(rows);
        }
        return BadRequest(new { message = "Loại báo cáo không hợp lệ." });
    }

    [HttpGet("sessions/{id:long}")]
    public async Task<ActionResult> Session(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanExportSessionReportAsync(db, User, id, ct)) return Forbid();
        return Ok(await BuildSession(id, ct));
    }

    [HttpGet("students/{id:long}")]
    public async Task<ActionResult> Student(long id, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        if (!await AccessScope.CanViewReportStudentAsync(db, User, id, ct)) return Forbid();
        var student = await db.StudentsSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (student is null) return NotFound();
        var permitted = (await ScopedSessionsAsync(from, to, ct)).Select(x => x.Id);
        var summaries = await db.StudentSessionSummariesSet.AsNoTracking()
            .Where(x => x.StudentId == id && permitted.Contains(x.SessionId))
            .Join(db.SessionsSet, x => x.SessionId, s => s.Id, (x, s) => new { x.SessionId, s.ScheduledStart, s.ClassSectionId, x.FocusedRatio, x.DistractedRatio, x.SleepyRatio, x.ActiveRatio, x.AlertCount, x.AlertDurationSeconds, x.ObservationQuality, x.AutoComment, x.AutoCommentProvider, x.AutoCommentGeneratedAt })
            .OrderByDescending(x => x.ScheduledStart)
            .ToListAsync(ct);
        var attendance = await db.AttendanceSet.AsNoTracking().Where(x => x.StudentId == id && permitted.Contains(x.SessionId)).ToListAsync(ct);
        var behaviorEvents = await db.BehaviorEventsSet.AsNoTracking().Where(x => x.StudentId == id && permitted.Contains(x.SessionId)).OrderByDescending(x => x.StartedAt).ToListAsync(ct);
        var alerts = await db.AlertsSet.AsNoTracking().Where(x => x.StudentId == id && permitted.Contains(x.SessionId)).OrderByDescending(x => x.StartedAt).ToListAsync(ct);
        var identities = await db.StableIdentitiesSet.AsNoTracking().Where(x => x.StudentId == id && permitted.Contains(x.SessionId)).OrderByDescending(x => x.LastSeenAt).Select(x => new { x.Id, x.SessionId, x.StableId, x.IdentityStatus, x.IdentityConfidence, x.IdentityMargin, x.FirstSeenAt, x.LastSeenAt, x.ModelVersion }).ToListAsync(ct);
        var videos = await db.VideosSet.AsNoTracking().Where(x => x.Status != "DELETED" &&
                ((x.SessionId != null && permitted.Contains(x.SessionId.Value)) || db.SessionsSet.Any(s => permitted.Contains(s.Id) && s.VideoId == x.Id)))
            .OrderByDescending(x => x.UploadedAt).Select(x => new { x.Id, x.SessionId, x.FileName, x.ContentType, x.SizeBytes, x.Status, x.VideoType, x.UploadedAt }).ToListAsync(ct);
        static OutputAvailability Available(int count, string empty) => count > 0 ? new("AVAILABLE", count, $"Đã lưu {count} bản ghi.") : new("EMPTY", 0, empty);
        var outputs = new
        {
            Summaries = summaries.Count > 0 ? new OutputAvailability("AVAILABLE", summaries.Count, $"Đã tạo {summaries.Count} tổng hợp sinh viên.") : new OutputAvailability("NOT_CREATED", 0, "Chưa có tổng hợp sinh viên; các chỉ số hiển thị bằng 0."),
            BehaviorEvents = Available(behaviorEvents.Count, "0 sự kiện — không ghi nhận sự kiện hành vi."),
            Alerts = Available(alerts.Count, "0 cảnh báo — không có rule/model nào phát hiện cảnh báo."),
            Attendance = Available(attendance.Count, "Chưa có bản ghi chuyên cần."),
            Identities = Available(identities.Count, "Chưa ghi nhận danh tính ổn định."),
            Video = videos.Count > 0 ? new OutputAvailability("AVAILABLE", videos.Count, $"Có {videos.Count} video của các buổi liên quan.") : new OutputAvailability("NOT_CREATED", 0, "Chưa có video/artifact đã lưu.")
        };
        return Ok(new { student = new { student.Id, student.StudentCode, student.FullName, student.Email }, summary = new { SessionCount = summaries.Count, AverageFocused = summaries.Count == 0 ? 0 : summaries.Average(x => x.FocusedRatio), AverageDistracted = summaries.Count == 0 ? 0 : summaries.Average(x => x.DistractedRatio), AverageSleepy = summaries.Count == 0 ? 0 : summaries.Average(x => x.SleepyRatio), AverageActive = summaries.Count == 0 ? 0 : summaries.Average(x => x.ActiveRatio), AverageAttendanceScore = attendance.Count == 0 ? 0 : attendance.Average(x => x.Score), TotalObservedSeconds = attendance.Sum(x => x.ObservedSeconds), TotalAlerts = alerts.Count, BehaviorEvents = behaviorEvents.Count, IdentityObservations = identities.Count, Videos = videos.Count }, outputs, sessions = summaries, attendance, behaviorEvents, alerts, identities, videos });
    }

    [HttpGet("class-sections/{id:long}")]
    public async Task<ActionResult> Class(long id, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        if (!await AccessScope.CanViewReportClassSectionAsync(db, User, id, ct)) return Forbid();
        var cls = await db.ClassSections.AsNoTracking().Include(x => x.Course).Include(x => x.Teacher).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (cls is null) return NotFound();
        var q = (await ScopedSessionsAsync(null, null, ct)).Where(x => x.ClassSectionId == id);
        if (from != null) q = q.Where(x => x.ScheduledStart >= from);
        if (to != null) q = q.Where(x => x.ScheduledStart < to);
        var scopedSessions = await q.OrderBy(x => x.ScheduledStart).Select(x => new { x.Id, x.ScheduledStart, x.Status }).ToListAsync(ct);
        var sessionIds = scopedSessions.Select(x => x.Id).ToArray();
        var storedSummaries = await db.ClassSessionSummariesSet.AsNoTracking().Where(x => sessionIds.Contains(x.SessionId)).ToListAsync(ct);
        var summariesBySession = storedSummaries.ToDictionary(x => x.SessionId);
        var timeline = scopedSessions.Select(x =>
        {
            summariesBySession.TryGetValue(x.Id, out var summary);
            return new ClassTimelineRow(x.Id, x.ScheduledStart, x.Status, summary is null ? "NOT_CREATED" : "AVAILABLE",
                summary?.ObservedStudentCount ?? 0, summary?.FocusedRatio ?? 0, summary?.DistractedRatio ?? 0,
                summary?.SleepyRatio ?? 0, summary?.ActiveRatio ?? 0, summary?.AlertCount ?? 0,
                summary?.CameraAiQuality ?? 0, summary?.AutoComment, summary?.AutoCommentProvider, summary?.AutoCommentGeneratedAt);
        }).ToList();
        var availableTimeline = timeline.Where(x => x.SummaryStatus == "AVAILABLE").ToList();
        var students = await db.StudentSessionSummariesSet.AsNoTracking().Where(x => sessionIds.Contains(x.SessionId))
            .GroupBy(x => x.StudentId)
            .Select(g => new { StudentId = g.Key, Focused = g.Average(x => x.FocusedRatio), Distracted = g.Average(x => x.DistractedRatio), Sleepy = g.Average(x => x.SleepyRatio), Active = g.Average(x => x.ActiveRatio), Alerts = g.Sum(x => x.AlertCount), Quality = g.Average(x => x.ObservationQuality) })
            .Join(db.StudentsSet, x => x.StudentId, s => s.Id, (x, s) => new { x.StudentId, s.StudentCode, s.FullName, x.Focused, x.Distracted, x.Sleepy, x.Active, x.Alerts, x.Quality, NeedsAttention = x.Distracted >= .30m || x.Sleepy >= .15m || x.Alerts >= 3 })
            .OrderByDescending(x => x.NeedsAttention).ThenByDescending(x => x.Distracted).ToListAsync(ct);
        var behaviorEventCount = await db.BehaviorEventsSet.AsNoTracking().CountAsync(x => sessionIds.Contains(x.SessionId), ct);
        var alertCount = await db.AlertsSet.AsNoTracking().CountAsync(x => sessionIds.Contains(x.SessionId), ct);
        var attendanceCount = await db.AttendanceSet.AsNoTracking().CountAsync(x => sessionIds.Contains(x.SessionId), ct);
        var identityCount = await db.StableIdentitiesSet.AsNoTracking().CountAsync(x => sessionIds.Contains(x.SessionId), ct);
        var videoCount = await db.VideosSet.AsNoTracking().CountAsync(x => x.Status != "DELETED" &&
            ((x.SessionId != null && sessionIds.Contains(x.SessionId.Value)) || db.SessionsSet.Any(s => sessionIds.Contains(s.Id) && s.VideoId == x.Id)), ct);
        static OutputAvailability Availability(int count, string emptyMessage) => count > 0
            ? new("AVAILABLE", count, $"Đã lưu {count} bản ghi.")
            : new("EMPTY", 0, emptyMessage);
        var outputs = new
        {
            Summaries = storedSummaries.Count > 0 ? new OutputAvailability("AVAILABLE", storedSummaries.Count, $"Đã tạo tổng hợp cho {storedSummaries.Count}/{scopedSessions.Count} buổi.") : new OutputAvailability("NOT_CREATED", 0, "Chưa có tổng hợp buổi; các tỷ lệ hiển thị bằng 0."),
            BehaviorEvents = Availability(behaviorEventCount, "0 sự kiện — không ghi nhận sự kiện hành vi."),
            Alerts = Availability(alertCount, "0 cảnh báo — không có rule/model nào phát hiện cảnh báo."),
            Attendance = Availability(attendanceCount, "Chưa có bản ghi chuyên cần."),
            Identities = Availability(identityCount, "Chưa ghi nhận danh tính ổn định."),
            Video = videoCount > 0 ? new OutputAvailability("AVAILABLE", videoCount, $"Đã lưu {videoCount} video.") : new OutputAvailability("NOT_CREATED", 0, "Chưa có video/artifact đã lưu.")
        };
        return Ok(new { classSection = new { cls.Id, cls.Code, cls.Name, Course = cls.Course.Name, Teacher = cls.Teacher.FullName, cls.Semester, cls.AcademicYear, cls.AutoComment, cls.AutoCommentProvider, cls.AutoCommentGeneratedAt }, summary = new { SessionCount = timeline.Count, SummarizedSessionCount = availableTimeline.Count, Focused = availableTimeline.Count == 0 ? 0 : availableTimeline.Average(x => x.FocusedRatio), Distracted = availableTimeline.Count == 0 ? 0 : availableTimeline.Average(x => x.DistractedRatio), Sleepy = availableTimeline.Count == 0 ? 0 : availableTimeline.Average(x => x.SleepyRatio), Active = availableTimeline.Count == 0 ? 0 : availableTimeline.Average(x => x.ActiveRatio), Alerts = alertCount, BehaviorEvents = behaviorEventCount, Attendance = attendanceCount, Identities = identityCount, Videos = videoCount }, outputs, timeline, students, attention = students.Where(x => x.NeedsAttention).ToList() });
    }

    [HttpPost("sessions/{id:long}/auto-comment"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> GenerateSessionComment(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanViewSessionAsync(db, User, id, ct)) return Forbid();
        var result = await autoComments.GenerateSessionAsync(id, UserContext.Id(User), ct);
        return result is null ? Conflict("Buổi học chưa có dữ liệu tổng hợp.") : Ok(result);
    }

    [HttpPost("sessions/{sessionId:long}/students/{studentId:long}/auto-comment"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> GenerateStudentComment(long sessionId, long studentId, CancellationToken ct)
    {
        if (!await AccessScope.CanViewSessionAsync(db, User, sessionId, ct)) return Forbid();
        var result = await autoComments.GenerateStudentAsync(sessionId, studentId, UserContext.Id(User), ct);
        return result is null ? Conflict("Chưa có dữ liệu tổng hợp sinh viên.") : Ok(result);
    }

    [HttpPost("class-sections/{id:long}/auto-comment"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> GenerateClassSectionComment(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanViewClassSectionAsync(db, User, id, ct)) return Forbid();
        var result = await autoComments.GenerateClassSectionAsync(id, UserContext.Id(User), ct);
        return result is null ? Conflict("Lớp chưa có dữ liệu tổng hợp.") : Ok(result);
    }

    [HttpGet("sessions/{id:long}/excel")]
    public async Task<IActionResult> SessionExcel(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanExportSessionReportAsync(db, User, id, ct)) return Forbid();
        var report = await BuildSession(id, ct);
        return File(BuildSessionExcel(report), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"HTBAM-Session-{id}.xlsx");
    }

    [HttpGet("sessions/{id:long}/pdf")]
    public async Task<IActionResult> SessionPdf(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanExportSessionReportAsync(db, User, id, ct)) return Forbid();
        var report = await BuildSession(id, ct);
        return File(BuildSessionPdf(report), "application/pdf", $"HTBAM-Session-{id}.pdf");
    }

    [HttpGet("students/{id:long}/excel")]
    public async Task<IActionResult> StudentExcel(long id, CancellationToken ct) => await StudentFile(id, "excel", ct);

    [HttpGet("students/{id:long}/pdf")]
    public async Task<IActionResult> StudentPdf(long id, CancellationToken ct) => await StudentFile(id, "pdf", ct);

    [HttpGet("class-sections/{id:long}/excel")]
    public async Task<IActionResult> ClassExcel(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanViewReportClassSectionAsync(db, User, id, ct)) return Forbid();
        var sessions = await (await ScopedSessionsAsync(null, null, ct)).Where(x => x.ClassSectionId == id).OrderBy(x => x.ScheduledStart).ToListAsync(ct);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sessions");
        string[] h = ["SessionId", "ScheduledStart", "Status", "Focused", "Distracted", "Sleepy", "Active"];
        for (var i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
        var row = 2;
        foreach (var s in sessions)
        {
            var sum = await db.ClassSessionSummariesSet.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == s.Id, ct);
            ws.Cell(row, 1).Value = s.Id; ws.Cell(row, 2).Value = s.ScheduledStart; ws.Cell(row, 3).Value = s.Status; ws.Cell(row, 4).Value = sum?.FocusedRatio ?? 0; ws.Cell(row, 5).Value = sum?.DistractedRatio ?? 0; ws.Cell(row, 6).Value = sum?.SleepyRatio ?? 0; ws.Cell(row, 7).Value = sum?.ActiveRatio ?? 0; row++;
        }
        using var ms = new MemoryStream(); wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"HTBAM-Class-{id}.xlsx");
    }

    [HttpGet("class-sections/{id:long}/pdf")]
    public async Task<IActionResult> ClassPdf(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanViewReportClassSectionAsync(db, User, id, ct)) return Forbid();
        var cls = await db.ClassSections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (cls is null) return NotFound();
        var pdf = Document.Create(c => c.Page(p => { p.Size(PageSizes.A4); p.Margin(32); p.DefaultTextStyle(x => x.FontSize(10)); p.Header().Text($"HTBAM - Class {cls.Code}").Bold().FontSize(18); p.Content().Text(cls.Name); })).GeneratePdf();
        return File(pdf, "application/pdf", $"HTBAM-Class-{id}.pdf");
    }

    private sealed record ClassTimelineRow(long SessionId, DateTime ScheduledStart, string Status, string SummaryStatus,
        int ObservedStudentCount, decimal FocusedRatio, decimal DistractedRatio, decimal SleepyRatio, decimal ActiveRatio,
        int AlertCount, decimal CameraAiQuality, string? AutoComment, string? AutoCommentProvider, DateTime? AutoCommentGeneratedAt);

    private async Task<IQueryable<HTBAM.Domain.Entities.Session>> ScopedSessionsAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        var tid = await AccessScope.TeacherIdAsync(db, User, ct);
        var sq = AccessScope.ReportSessions(db, User, tid).AsNoTracking();
        if (from != null) sq = sq.Where(x => x.ScheduledStart >= from);
        if (to != null) sq = sq.Where(x => x.ScheduledStart < to);
        return sq;
    }

    private async Task<IActionResult> StudentFile(long id, string format, CancellationToken ct)
    {
        if (!await AccessScope.CanViewReportStudentAsync(db, User, id, ct)) return Forbid();
        var student = await db.StudentsSet.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (student is null) return NotFound();
        var permitted = (await ScopedSessionsAsync(null, null, ct)).Select(x => x.Id);
        var rows = await db.StudentSessionSummariesSet.AsNoTracking().Where(x => x.StudentId == id && permitted.Contains(x.SessionId)).Join(db.SessionsSet, x => x.SessionId, s => s.Id, (x, s) => new { x.SessionId, s.ScheduledStart, x.FocusedRatio, x.DistractedRatio, x.SleepyRatio, x.ActiveRatio, x.AlertCount, x.ObservationQuality }).OrderBy(x => x.ScheduledStart).ToListAsync(ct);
        if (format == "pdf")
        {
            var pdf = Document.Create(c => c.Page(p => { p.Size(PageSizes.A4); p.Margin(32); p.DefaultTextStyle(x => x.FontSize(10)); p.Header().Text($"HTBAM - Student {student.StudentCode}").Bold().FontSize(18); p.Content().Text($"{student.FullName} - sessions: {rows.Count}"); })).GeneratePdf();
            return File(pdf, "application/pdf", $"HTBAM-Student-{student.StudentCode}.pdf");
        }
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Student");
        string[] h = ["SessionId", "ScheduledStart", "Focused", "Distracted", "Sleepy", "Active", "Alerts", "Quality"];
        for (var i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
        var row = 2;
        foreach (var x in rows) { ws.Cell(row, 1).Value = x.SessionId; ws.Cell(row, 2).Value = x.ScheduledStart; ws.Cell(row, 3).Value = x.FocusedRatio; ws.Cell(row, 4).Value = x.DistractedRatio; ws.Cell(row, 5).Value = x.SleepyRatio; ws.Cell(row, 6).Value = x.ActiveRatio; ws.Cell(row, 7).Value = x.AlertCount; ws.Cell(row, 8).Value = x.ObservationQuality; row++; }
        using var ms = new MemoryStream(); wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"HTBAM-Student-{student.StudentCode}.xlsx");
    }

    private async Task<object> BuildSession(long id, CancellationToken ct)
    {
        var session = await db.SessionsSet.AsNoTracking().Include(x => x.ClassSection).ThenInclude(x => x.Course).Include(x => x.ClassSection).ThenInclude(x => x.Teacher).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Session không tồn tại.");
        var substitution = await db.SessionSubstitutionsSet.AsNoTracking().Where(x => x.SessionId == id && (x.Status == "ACTIVE" || x.Status == "COMPLETED")).OrderByDescending(x => x.AssignedAt).Select(x => new { x.SubstituteTeacherId, SubstituteTeacher = x.SubstituteTeacher.FullName, x.Reason, x.Status, x.CompletedAt }).FirstOrDefaultAsync(ct);
        var cls = await db.ClassSessionSummariesSet.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == id, ct);
        object summaryView = (object?)cls ?? new
        {
            SessionId = id,
            ObservedStudentCount = 0,
            FocusedRatio = 0m,
            DistractedRatio = 0m,
            SleepyRatio = 0m,
            ActiveRatio = 0m,
            AlertCount = 0,
            CameraAiQuality = 0m,
            AutoComment = (string?)null,
            AutoCommentProvider = (string?)null,
            AutoCommentGeneratedAt = (DateTime?)null
        };
        var students = await db.StudentSessionSummariesSet.AsNoTracking().Where(x => x.SessionId == id).Join(db.StudentsSet, x => x.StudentId, s => s.Id, (x, s) => new { x.StudentId, s.StudentCode, s.FullName, x.FocusedRatio, x.DistractedRatio, x.SleepyRatio, x.ActiveRatio, x.AlertCount, x.AlertDurationSeconds, x.ObservationQuality, x.AutoComment, x.AutoCommentProvider, x.AutoCommentGeneratedAt }).OrderBy(x => x.StudentCode).ToListAsync(ct);
        var attendance = await db.AttendanceSet.AsNoTracking().Where(x => x.SessionId == id).ToListAsync(ct);
        var timeline = await db.BehaviorEventsSet.AsNoTracking().Where(x => x.SessionId == id).OrderBy(x => x.StartedAt).Take(10000).ToListAsync(ct);
        var alerts = await db.AlertsSet.AsNoTracking().Where(x => x.SessionId == id).OrderBy(x => x.StartedAt).ToListAsync(ct);
        var identities = await db.StableIdentitiesSet.AsNoTracking().Where(x => x.SessionId == id).OrderBy(x => x.FirstSeenAt).Select(x => new { x.Id, x.StableId, x.StudentId, x.IdentityStatus, x.IdentityConfidence, x.IdentityMargin, x.FirstSeenAt, x.LastSeenAt, x.State, x.CurrentTrackId, x.ModelVersion }).ToListAsync(ct);
        var identityIds = identities.Select(x => x.Id).ToArray();
        var identityLinks = await db.IdentityLinksSet.AsNoTracking().Where(x => identityIds.Contains(x.StableIdentityId)).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var trackSegments = await db.TrackSegmentsSet.AsNoTracking().Where(x => identityIds.Contains(x.StableIdentityId)).OrderBy(x => x.StartedAt).ToListAsync(ct);
        var videos = await db.VideosSet.AsNoTracking().Where(x => x.Status != "DELETED" && (x.SessionId == id || (session.VideoId != null && x.Id == session.VideoId))).OrderBy(x => x.UploadedAt).Select(x => new { x.Id, x.FileName, x.ContentType, x.SizeBytes, x.Status, x.VideoType, x.SessionId, x.ParentVideoId, x.UploadedAt }).ToListAsync(ct);
        static OutputAvailability AvailableOrEmpty(int count, string noun) => count > 0
            ? new("AVAILABLE", count, $"Đã lưu {count} {noun}.")
            : new("EMPTY", 0, $"Không ghi nhận {noun}; các chỉ số tương ứng bằng 0.");
        var outputs = new SessionOutputStatus(
            videos.Count > 0 ? new("AVAILABLE", videos.Count, $"Đã lưu {videos.Count} video có thể xem lại.") : new("NOT_CREATED", 0, session.CameraId is not null ? "Không có video đầu ra đã lưu cho nguồn camera." : "Không tìm thấy video nguồn phù hợp."),
            AvailableOrEmpty(timeline.Count, "sự kiện hành vi"),
            AvailableOrEmpty(alerts.Count, "cảnh báo"),
            AvailableOrEmpty(identities.Count, "danh tính ổn định"),
            AvailableOrEmpty(attendance.Count, "bản ghi chuyên cần"),
            AvailableOrEmpty(students.Count + (cls is null ? 0 : 1), "bản ghi tổng hợp"));
        return new { session = new { session.Id, session.ClassSectionId, ClassSection = session.ClassSection.Code, Course = session.ClassSection.Course.Name, Teacher = session.ClassSection.Teacher.FullName, session.OriginalTeacherId, ActiveSubstitution = substitution, session.ScheduledStart, session.StartedAt, session.EndedAt, session.Status, session.LecturerComment }, summary = summaryView, outputs, students, attendance, timeline, alerts, identities, identityLinks, trackSegments, videos, attention = students.Where(x => x.DistractedRatio >= .30m || x.SleepyRatio >= .15m || x.AlertCount >= 3).ToList() };
    }

    private static byte[] BuildSessionExcel(object report)
    {
        dynamic r = report;
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Overview");
        ws.Cell("A1").Value = "HTBAM SESSION REPORT"; ws.Cell("A3").Value = "Session"; ws.Cell("B3").Value = (long)r.session.Id; ws.Cell("A4").Value = "Class"; ws.Cell("B4").Value = (string)r.session.ClassSection; ws.Cell("A5").Value = "Course"; ws.Cell("B5").Value = (string)r.session.Course;
        var students = wb.Worksheets.Add("Students");
        string[] heads = ["StudentCode", "FullName", "Focused", "Distracted", "Sleepy", "Active", "Alerts", "Quality"];
        for (var i = 0; i < heads.Length; i++) students.Cell(1, i + 1).Value = heads[i];
        var row = 2;
        foreach (var s in r.students) { students.Cell(row, 1).Value = (string)s.StudentCode; students.Cell(row, 2).Value = (string)s.FullName; students.Cell(row, 3).Value = (decimal)s.FocusedRatio; students.Cell(row, 4).Value = (decimal)s.DistractedRatio; students.Cell(row, 5).Value = (decimal)s.SleepyRatio; students.Cell(row, 6).Value = (decimal)s.ActiveRatio; students.Cell(row, 7).Value = (int)s.AlertCount; students.Cell(row, 8).Value = (decimal)s.ObservationQuality; row++; }
        using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    private static byte[] BuildSessionPdf(object report)
    {
        dynamic r = report;
        return Document.Create(c => c.Page(p => { p.Size(PageSizes.A4); p.Margin(32); p.DefaultTextStyle(x => x.FontSize(10)); p.Header().Text($"HTBAM - Session #{r.session.Id}").Bold().FontSize(18); p.Content().Column(col => { col.Spacing(6); col.Item().Text($"Class: {r.session.ClassSection}"); col.Item().Text($"Course: {r.session.Course}"); col.Item().Text($"Teacher: {r.session.Teacher}"); }); })).GeneratePdf();
    }
}
