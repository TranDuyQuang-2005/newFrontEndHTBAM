using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Application.Services;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
        var permitted = await (await ScopedSessionsAsync(from, to, ct)).Select(x => x.Id).ToArrayAsync(ct);
        var summaries = await db.StudentSessionSummariesSet.AsNoTracking()
            .Where(x => x.StudentId == id && permitted.Contains(x.SessionId))
            .Join(db.SessionsSet, x => x.SessionId, s => s.Id, (x, s) => new { x.SessionId, s.ScheduledStart, s.ClassSectionId, x.FocusedRatio, x.DistractedRatio, x.SleepyRatio, x.ActiveRatio, x.AlertCount, x.AlertDurationSeconds, x.ObservationQuality, x.AutoComment, x.AutoCommentProvider, x.AutoCommentGeneratedAt })
            .OrderByDescending(x => x.ScheduledStart)
            .ToListAsync(ct);
        var attendance = await BuildAttendanceRowsAsync(permitted, id, ct);
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
        var trustedSessionIds = await db.StableIdentitiesSet.AsNoTracking()
            .Where(x => x.StudentId == id && x.IdentityStatus == "IDENTIFIED" && permitted.Contains(x.SessionId))
            .Select(x => x.SessionId).Distinct().ToListAsync(ct);
        var targetSessionId = summaries.Select(x => x.SessionId)
            .FirstOrDefault(sessionId => trustedSessionIds.Contains(sessionId) && attendance.Any(x => x.SessionId == sessionId && x.ObservedSeconds > 0));
        var canGenerateRole = User.IsInRole("LECTURER");
        var commentReason = !canGenerateRole ? "Tài khoản không có quyền tạo nhận xét nghiệp vụ."
            : summaries.Count == 0 ? "Chưa có tổng hợp sinh viên trong phạm vi báo cáo."
            : trustedSessionIds.Count == 0 ? "Sinh viên chưa được liên kết với danh tính IDENTIFIED đáng tin cậy."
            : targetSessionId == 0 ? "Danh tính đã liên kết nhưng chưa có thời gian hành vi được quan sát."
            : null;
        var attendanceScore = AverageAttendanceScore(attendance);
        return Ok(new
        {
            student = new { student.Id, student.StudentCode, student.FullName, student.Email },
            summary = new
            {
                SessionCount = summaries.Count,
                AverageFocused = summaries.Count == 0 ? 0 : summaries.Average(x => x.FocusedRatio),
                AverageDistracted = summaries.Count == 0 ? 0 : summaries.Average(x => x.DistractedRatio),
                AverageSleepy = summaries.Count == 0 ? 0 : summaries.Average(x => x.SleepyRatio),
                AverageActive = summaries.Count == 0 ? 0 : summaries.Average(x => x.ActiveRatio),
                AverageAttendanceScorePercent = attendanceScore,
                AttendanceScoreReason = AttendanceReason(attendance, attendanceScore),
                TotalObservedSeconds = attendance.Sum(x => x.ObservedSeconds),
                TotalAlerts = alerts.Count,
                BehaviorEvents = behaviorEvents.Count,
                IdentityObservations = identities.Count,
                Videos = videos.Count
            },
            autoCommentAvailability = new { CanGenerate = canGenerateRole && targetSessionId != 0, TargetSessionId = targetSessionId == 0 ? (long?)null : targetSessionId, Reason = commentReason },
            outputs, sessions = summaries, attendance, behaviorEvents, alerts, identities, videos
        });
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
        var scopedSessions = await q.OrderBy(x => x.ScheduledStart)
            .Select(x => new { x.Id, x.ScheduledStart, x.ScheduledEnd, x.StartedAt, x.EndedAt, x.Status }).ToListAsync(ct);
        var sessionIds = scopedSessions.Select(x => x.Id).ToArray();
        var storedSummaries = await db.ClassSessionSummariesSet.AsNoTracking().Where(x => sessionIds.Contains(x.SessionId)).ToListAsync(ct);
        var summariesBySession = storedSummaries.ToDictionary(x => x.SessionId);
        var behaviorEvents = await db.BehaviorEventsSet.AsNoTracking().Where(x => sessionIds.Contains(x.SessionId)).OrderBy(x => x.StartedAt).ToListAsync(ct);
        var eventsBySession = behaviorEvents.GroupBy(x => x.SessionId).ToDictionary(x => x.Key, x => x.ToList());
        var timeline = scopedSessions.Select(x =>
        {
            summariesBySession.TryGetValue(x.Id, out var summary);
            var events = eventsBySession.GetValueOrDefault(x.Id) ?? [];
            var rangeStart = x.StartedAt ?? x.ScheduledStart;
            var rangeEnd = x.EndedAt ?? x.ScheduledEnd ?? (events.Count > 0 ? events.Max(e => e.EndedAt) : rangeStart);
            var storedBehavior = BehaviorSummaryCalculator.Calculate(events, rangeStart, rangeEnd);
            var useEvents = storedBehavior.ObservedSeconds > 0;
            return new ClassTimelineRow(x.Id, x.ScheduledStart, x.Status, summary is null ? "NOT_CREATED" : "AVAILABLE",
                useEvents ? storedBehavior.ObservedIdentityCount : summary?.ObservedStudentCount ?? 0,
                useEvents ? storedBehavior.ObservedSeconds : 0,
                useEvents ? storedBehavior.FocusedRatio : summary?.FocusedRatio ?? 0,
                useEvents ? storedBehavior.DistractedRatio : summary?.DistractedRatio ?? 0,
                useEvents ? storedBehavior.SleepyRatio : summary?.SleepyRatio ?? 0,
                useEvents ? storedBehavior.ActiveRatio : summary?.ActiveRatio ?? 0,
                summary?.AlertCount ?? 0,
                useEvents ? storedBehavior.ObservationQuality : summary?.CameraAiQuality ?? 0,
                useEvents ? "STORED_BEHAVIOR_EVENTS" : summary is null ? "NOT_AVAILABLE" : "PERSISTED_SUMMARY",
                summary?.AutoComment, summary?.AutoCommentProvider, summary?.AutoCommentGeneratedAt);
        }).ToList();
        var availableTimeline = timeline.Where(x => x.SummaryStatus == "AVAILABLE").ToList();
        var observedTimeline = timeline.Where(x => x.ObservedSeconds > 0).ToList();
        var students = await db.StudentSessionSummariesSet.AsNoTracking().Where(x => sessionIds.Contains(x.SessionId))
            .GroupBy(x => x.StudentId)
            .Select(g => new { StudentId = g.Key, Focused = g.Average(x => x.FocusedRatio), Distracted = g.Average(x => x.DistractedRatio), Sleepy = g.Average(x => x.SleepyRatio), Active = g.Average(x => x.ActiveRatio), Alerts = g.Sum(x => x.AlertCount), Quality = g.Average(x => x.ObservationQuality) })
            .Join(db.StudentsSet, x => x.StudentId, s => s.Id, (x, s) => new { x.StudentId, s.StudentCode, s.FullName, x.Focused, x.Distracted, x.Sleepy, x.Active, x.Alerts, x.Quality, NeedsAttention = x.Distracted >= .30m || x.Sleepy >= .15m || x.Alerts >= 3 })
            .OrderByDescending(x => x.NeedsAttention).ThenByDescending(x => x.Distracted).ToListAsync(ct);
        var attendance = await BuildAttendanceRowsAsync(sessionIds, null, ct);
        var alerts = await db.AlertsSet.AsNoTracking().Where(x => sessionIds.Contains(x.SessionId)).OrderByDescending(x => x.StartedAt).ToListAsync(ct);
        var identities = await db.StableIdentitiesSet.AsNoTracking().Where(x => sessionIds.Contains(x.SessionId)).OrderByDescending(x => x.LastSeenAt)
            .Select(x => new { x.Id, x.SessionId, x.StableId, x.StudentId, x.IdentityStatus, x.IdentityConfidence, x.IdentityMargin, x.FirstSeenAt, x.LastSeenAt, x.ModelVersion }).ToListAsync(ct);
        var videos = await db.VideosSet.AsNoTracking().Where(x => x.Status != "DELETED" &&
                ((x.SessionId != null && sessionIds.Contains(x.SessionId.Value)) || db.SessionsSet.Any(s => sessionIds.Contains(s.Id) && s.VideoId == x.Id)))
            .OrderByDescending(x => x.UploadedAt).Select(x => new { x.Id, x.SessionId, x.FileName, x.ContentType, x.SizeBytes, x.Status, x.VideoType, x.UploadedAt }).ToListAsync(ct);
        var behaviorEventCount = behaviorEvents.Count;
        var alertCount = alerts.Count;
        var attendanceCount = attendance.Count;
        var identityCount = identities.Count;
        var videoCount = videos.Count;
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
        decimal BehaviorAverage(Func<ClassTimelineRow, decimal> selector)
        {
            if (observedTimeline.Count > 0)
            {
                var seconds = observedTimeline.Sum(x => x.ObservedSeconds);
                return seconds <= 0 ? 0 : observedTimeline.Sum(x => selector(x) * x.ObservedSeconds) / seconds;
            }
            return availableTimeline.Count == 0 ? 0 : availableTimeline.Average(selector);
        }
        var attendanceScore = AverageAttendanceScore(attendance);
        var canGenerate = User.IsInRole("LECTURER") && storedSummaries.Any(x => scopedSessions.Any(s => s.Id == x.SessionId && s.Status == "COMPLETED"));
        return Ok(new
        {
            classSection = new { cls.Id, cls.Code, cls.Name, Course = cls.Course.Name, Teacher = cls.Teacher.FullName, cls.Semester, cls.AcademicYear, cls.AutoComment, cls.AutoCommentProvider, cls.AutoCommentGeneratedAt },
            summary = new
            {
                SessionCount = timeline.Count,
                SummarizedSessionCount = availableTimeline.Count,
                Focused = BehaviorAverage(x => x.FocusedRatio),
                Distracted = BehaviorAverage(x => x.DistractedRatio),
                Sleepy = BehaviorAverage(x => x.SleepyRatio),
                Active = BehaviorAverage(x => x.ActiveRatio),
                AverageAttendanceScorePercent = attendanceScore,
                AttendanceScoreReason = AttendanceReason(attendance, attendanceScore),
                Alerts = alertCount,
                BehaviorEvents = behaviorEventCount,
                Attendance = attendanceCount,
                Identities = identityCount,
                Videos = videoCount
            },
            autoCommentAvailability = new { CanGenerate = canGenerate, TargetSessionId = (long?)null, Reason = canGenerate ? null : User.IsInRole("LECTURER") ? "Lớp chưa có tổng hợp của buổi học đã hoàn tất." : "Tài khoản không có quyền tạo nhận xét nghiệp vụ." },
            outputs, timeline, students, attendance, behaviorEvents, alerts, identities, videos,
            attention = students.Where(x => x.NeedsAttention).ToList()
        });
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
        var hasSummary = await db.StudentSessionSummariesSet.AsNoTracking().AnyAsync(x => x.SessionId == sessionId && x.StudentId == studentId, ct);
        if (!hasSummary) return Conflict(new { code = "SUMMARY_NOT_CREATED", message = "Chưa có dữ liệu tổng hợp sinh viên." });
        var trustedIdentity = await db.StableIdentitiesSet.AsNoTracking().AnyAsync(x => x.SessionId == sessionId && x.StudentId == studentId && x.IdentityStatus == "IDENTIFIED", ct);
        if (!trustedIdentity) return Conflict(new { code = "IDENTITY_NOT_TRUSTED", message = "Sinh viên chưa được liên kết với danh tính IDENTIFIED đáng tin cậy; không tạo nhận xét cá nhân." });
        var observed = await db.AttendanceSet.AsNoTracking().AnyAsync(x => x.SessionId == sessionId && x.StudentId == studentId && x.ObservedSeconds > 0, ct);
        if (!observed) return Conflict(new { code = "OBSERVATION_NOT_AVAILABLE", message = "Sinh viên chưa có thời gian hành vi được quan sát; không tạo nhận xét cá nhân." });
        var result = await autoComments.GenerateStudentAsync(sessionId, studentId, UserContext.Id(User), ct);
        return result is null ? Conflict(new { code = "COMMENT_DATA_NOT_AVAILABLE", message = "Dữ liệu chưa đủ để tạo nhận xét sinh viên." }) : Ok(result);
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
        return File(ReportFileBuilder.Excel(report, "Báo cáo buổi học"), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "He-thong-giam-sat-Bao-cao-buoi-hoc.xlsx");
    }

    [HttpGet("sessions/{id:long}/pdf")]
    public async Task<IActionResult> SessionPdf(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanExportSessionReportAsync(db, User, id, ct)) return Forbid();
        var report = await BuildSession(id, ct);
        return File(ReportFileBuilder.Pdf(report, "Báo cáo buổi học"), "application/pdf", "He-thong-giam-sat-Bao-cao-buoi-hoc.pdf");
    }

    [HttpGet("students/{id:long}/excel")]
    public async Task<IActionResult> StudentExcel(long id, CancellationToken ct) => await StudentFile(id, "excel", ct);

    [HttpGet("students/{id:long}/pdf")]
    public async Task<IActionResult> StudentPdf(long id, CancellationToken ct) => await StudentFile(id, "pdf", ct);

    [HttpGet("class-sections/{id:long}/excel")]
    public async Task<IActionResult> ClassExcel(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanViewReportClassSectionAsync(db, User, id, ct)) return Forbid();
        var report = ExtractReport(await Class(id, null, null, ct));
        return File(ReportFileBuilder.Excel(report, "Báo cáo lớp học phần"), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "He-thong-giam-sat-Bao-cao-lop-hoc-phan.xlsx");
    }

    [HttpGet("class-sections/{id:long}/pdf")]
    public async Task<IActionResult> ClassPdf(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanViewReportClassSectionAsync(db, User, id, ct)) return Forbid();
        var report = ExtractReport(await Class(id, null, null, ct));
        return File(ReportFileBuilder.Pdf(report, "Báo cáo lớp học phần"), "application/pdf", "He-thong-giam-sat-Bao-cao-lop-hoc-phan.pdf");
    }

    private sealed record ClassTimelineRow(long SessionId, DateTime ScheduledStart, string Status, string SummaryStatus,
        int ObservedStudentCount, int ObservedSeconds, decimal FocusedRatio, decimal DistractedRatio, decimal SleepyRatio, decimal ActiveRatio,
        int AlertCount, decimal CameraAiQuality, string BehaviorSummarySource, string? AutoComment, string? AutoCommentProvider, DateTime? AutoCommentGeneratedAt);

    private sealed record AttendanceReportRow(long SessionId, long StudentId, int PresentSeconds, int ObservedSeconds,
        decimal PresenceRatio, decimal ObservationQuality, string Status, decimal PolicyScore, decimal ScorePercent,
        string ScoreReason, string PolicyVersion);

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
        var report = ExtractReport(await Student(id, null, null, ct));
        const string title = "Báo cáo sinh viên";
        return format == "pdf"
            ? File(ReportFileBuilder.Pdf(report, title), "application/pdf", "He-thong-giam-sat-Bao-cao-sinh-vien.pdf")
            : File(ReportFileBuilder.Excel(report, title), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "He-thong-giam-sat-Bao-cao-sinh-vien.xlsx");
    }

    private static object ExtractReport(ActionResult result) =>
        (result as OkObjectResult)?.Value ?? throw new InvalidOperationException("Không thể tạo dữ liệu xuất báo cáo.");

    private async Task<object> BuildSession(long id, CancellationToken ct)
    {
        var session = await db.SessionsSet.AsNoTracking().Include(x => x.ClassSection).ThenInclude(x => x.Course).Include(x => x.ClassSection).ThenInclude(x => x.Teacher).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Session không tồn tại.");
        var substitution = await db.SessionSubstitutionsSet.AsNoTracking().Where(x => x.SessionId == id && (x.Status == "ACTIVE" || x.Status == "COMPLETED")).OrderByDescending(x => x.AssignedAt).Select(x => new { x.SubstituteTeacherId, SubstituteTeacher = x.SubstituteTeacher.FullName, x.Reason, x.Status, x.CompletedAt }).FirstOrDefaultAsync(ct);
        var cls = await db.ClassSessionSummariesSet.AsNoTracking().FirstOrDefaultAsync(x => x.SessionId == id, ct);
        var students = await db.StudentSessionSummariesSet.AsNoTracking().Where(x => x.SessionId == id).Join(db.StudentsSet, x => x.StudentId, s => s.Id, (x, s) => new { x.StudentId, s.StudentCode, s.FullName, x.FocusedRatio, x.DistractedRatio, x.SleepyRatio, x.ActiveRatio, x.AlertCount, x.AlertDurationSeconds, x.ObservationQuality, x.AutoComment, x.AutoCommentProvider, x.AutoCommentGeneratedAt }).OrderBy(x => x.StudentCode).ToListAsync(ct);
        var attendance = await BuildAttendanceRowsAsync([id], null, ct);
        var timeline = await db.BehaviorEventsSet.AsNoTracking().Where(x => x.SessionId == id).OrderBy(x => x.StartedAt).Take(10000).ToListAsync(ct);
        var alerts = await db.AlertsSet.AsNoTracking().Where(x => x.SessionId == id).OrderBy(x => x.StartedAt).ToListAsync(ct);
        var identities = await db.StableIdentitiesSet.AsNoTracking().Where(x => x.SessionId == id).OrderBy(x => x.FirstSeenAt).Select(x => new { x.Id, x.StableId, x.StudentId, x.IdentityStatus, x.IdentityConfidence, x.IdentityMargin, x.FirstSeenAt, x.LastSeenAt, x.State, x.CurrentTrackId, x.ModelVersion }).ToListAsync(ct);
        var identityIds = identities.Select(x => x.Id).ToArray();
        var identityLinks = await db.IdentityLinksSet.AsNoTracking().Where(x => identityIds.Contains(x.StableIdentityId)).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var trackSegments = await db.TrackSegmentsSet.AsNoTracking().Where(x => identityIds.Contains(x.StableIdentityId)).OrderBy(x => x.StartedAt).ToListAsync(ct);
        var videos = await db.VideosSet.AsNoTracking().Where(x => x.Status != "DELETED" && (x.SessionId == id || (session.VideoId != null && x.Id == session.VideoId))).OrderBy(x => x.UploadedAt).Select(x => new { x.Id, x.FileName, x.ContentType, x.SizeBytes, x.Status, x.VideoType, x.SessionId, x.ParentVideoId, x.UploadedAt }).ToListAsync(ct);
        var rangeStart = session.StartedAt ?? session.ScheduledStart;
        var rangeEnd = session.EndedAt ?? session.ScheduledEnd ?? (timeline.Count > 0 ? timeline.Max(x => x.EndedAt) : rangeStart);
        var storedBehavior = BehaviorSummaryCalculator.Calculate(timeline, rangeStart, rangeEnd);
        var useStoredEvents = storedBehavior.ObservedSeconds > 0;
        var attendanceScore = AverageAttendanceScore(attendance);
        var summaryView = new
        {
            ObservedStudentCount = useStoredEvents ? storedBehavior.ObservedIdentityCount : cls?.ObservedStudentCount ?? 0,
            ObservedSeconds = useStoredEvents ? storedBehavior.ObservedSeconds : attendance.Sum(x => x.ObservedSeconds),
            FocusedRatio = useStoredEvents ? storedBehavior.FocusedRatio : cls?.FocusedRatio ?? 0,
            DistractedRatio = useStoredEvents ? storedBehavior.DistractedRatio : cls?.DistractedRatio ?? 0,
            SleepyRatio = useStoredEvents ? storedBehavior.SleepyRatio : cls?.SleepyRatio ?? 0,
            ActiveRatio = useStoredEvents ? storedBehavior.ActiveRatio : cls?.ActiveRatio ?? 0,
            AlertCount = alerts.Count,
            CameraAiQuality = useStoredEvents ? storedBehavior.ObservationQuality : cls?.CameraAiQuality ?? 0,
            AverageAttendanceScorePercent = attendanceScore,
            AttendanceScoreReason = AttendanceReason(attendance, attendanceScore),
            BehaviorSummarySource = useStoredEvents ? "STORED_BEHAVIOR_EVENTS" : cls is null ? "NOT_AVAILABLE" : "PERSISTED_SUMMARY",
            AutoComment = cls?.AutoComment,
            AutoCommentProvider = cls?.AutoCommentProvider,
            AutoCommentGeneratedAt = cls?.AutoCommentGeneratedAt
        };
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
        var canGenerate = User.IsInRole("LECTURER") && cls is not null;
        return new
        {
            session = new { session.Id, session.ClassSectionId, ClassSection = session.ClassSection.Code, Course = session.ClassSection.Course.Name, Teacher = session.ClassSection.Teacher.FullName, session.OriginalTeacherId, ActiveSubstitution = substitution, session.ScheduledStart, session.StartedAt, session.EndedAt, session.Status, session.LecturerComment },
            summary = summaryView,
            autoCommentAvailability = new { CanGenerate = canGenerate, TargetSessionId = canGenerate ? id : (long?)null, Reason = canGenerate ? null : User.IsInRole("LECTURER") ? "Buổi học chưa có ClassSessionSummary; không thể tạo nhận xét từ dữ liệu chưa tổng hợp." : "Tài khoản không có quyền tạo nhận xét nghiệp vụ." },
            outputs, students, attendance, timeline, alerts, identities, identityLinks, trackSegments, videos,
            attention = students.Where(x => x.DistractedRatio >= .30m || x.SleepyRatio >= .15m || x.AlertCount >= 3).ToList()
        };
    }

    private async Task<List<AttendanceReportRow>> BuildAttendanceRowsAsync(IReadOnlyCollection<long> sessionIds, long? studentId, CancellationToken ct)
    {
        var rows = await db.AttendanceSet.AsNoTracking()
            .Where(x => sessionIds.Contains(x.SessionId) && (studentId == null || x.StudentId == studentId))
            .Join(db.SessionsSet.AsNoTracking(), attendance => attendance.SessionId, session => session.Id,
                (attendance, session) => new { Attendance = attendance, session.AttendancePolicyId })
            .Join(db.AttendancePoliciesSet.AsNoTracking(), row => row.AttendancePolicyId, policy => policy.Id,
                (row, policy) => new
                {
                    row.Attendance.SessionId, row.Attendance.StudentId, row.Attendance.PresentSeconds,
                    row.Attendance.ObservedSeconds, row.Attendance.PresenceRatio, row.Attendance.ObservationQuality,
                    row.Attendance.Status, row.Attendance.Score, row.Attendance.PolicyVersion,
                    policy.PresentScore, policy.PartialScore, policy.AbsentScore
                }).ToListAsync(ct);
        return rows.Select(x =>
        {
            var maximum = Math.Max(x.PresentScore, Math.Max(x.PartialScore, x.AbsentScore));
            var percent = maximum <= 0 ? 0 : Math.Clamp(x.Score / maximum * 100m, 0m, 100m);
            var reason = x.ObservedSeconds <= 0 ? "Điểm 0% vì không có thời gian hành vi được quan sát."
                : x.PresentSeconds <= 0 ? "Điểm 0% vì không có thời gian hiện diện."
                : x.Score <= 0 ? $"Policy {x.PolicyVersion} ánh xạ trạng thái {x.Status} thành 0 điểm."
                : $"Tính theo policy {x.PolicyVersion} và trạng thái {x.Status}.";
            return new AttendanceReportRow(x.SessionId, x.StudentId, x.PresentSeconds, x.ObservedSeconds,
                x.PresenceRatio, x.ObservationQuality, x.Status, x.Score, percent, reason, x.PolicyVersion);
        }).ToList();
    }

    private static decimal? AverageAttendanceScore(IReadOnlyCollection<AttendanceReportRow> rows) =>
        rows.Count == 0 ? null : rows.Average(x => x.ScorePercent);

    private static string AttendanceReason(IReadOnlyCollection<AttendanceReportRow> rows, decimal? score) =>
        rows.Count == 0 ? "Chưa có bản ghi chuyên cần."
        : rows.All(x => x.ObservedSeconds <= 0) ? "Điểm chuyên cần 0% vì không có thời gian hành vi được quan sát."
        : rows.All(x => x.PresentSeconds <= 0) ? "Điểm chuyên cần 0% vì không có thời gian hiện diện."
        : $"Điểm trung bình {score ?? 0:0.##}% theo policy chuyên cần đã lưu.";

}
