using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HTBAM.Application.DTOs;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Support;

public sealed record SchedulePlan(AcademicTerm Term, AcademicDateRange Range, PlannedOccurrence[] Occurrences,
    Session[] Replaceable, Session[] Protected, ClassSchedule[] CurrentLines, bool ScheduleChanged, string PreviewToken);

public sealed class ClassSchedulePlanner(AppDbContext db)
{
    public async Task<SchedulePlan> Prepare(ClassSectionRequest r, ClassSection? current, CancellationToken ct)
    {
        if (current != null && (current.CourseId != r.CourseId || current.AcademicTermId != r.AcademicTermId || current.Phase != r.Phase))
        {
            var history = await ProtectedSessionIds(await db.SessionsSet.Where(x => x.ClassSectionId == current.Id).Select(x => x.Id).ToArrayAsync(ct), ct);
            if (history.Count > 0) throw new InvalidOperationException("Lớp đã có lịch sử: không đổi môn/học kỳ/giai đoạn làm thay đổi ngữ cảnh dữ liệu cũ. Chỉ điều chỉnh các dòng lịch tương lai phù hợp.");
        }
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 256 || r.SourceClassSectionCode?.Length > 128 || r.Note?.Length > 8000)
            throw new ArgumentException("Tên lớp hoặc thông tin nguồn/ghi chú không hợp lệ.");
        if (r.Schedules is null || r.Schedules.Length is < 1 or > 32 || r.Schedules.Any(x => !Regex.IsMatch(x.LineKey, "^[a-fA-F0-9]{32}$")) ||
            r.Schedules.Select(x => x.LineKey).Distinct().Count() != r.Schedules.Length)
            throw new ArgumentException("Cần 1–32 dòng lịch có khóa riêng, không trùng nhau.");
        var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.CourseId && x.IsActive, ct)
            ?? throw new ArgumentException("Môn học không hợp lệ.");
        if (course.DepartmentId == null || !await db.DepartmentsSet.AnyAsync(x => x.Id == course.DepartmentId && x.IsActive && x.Faculty.IsActive, ct))
            throw new ArgumentException("Bộ môn hoặc khoa của môn học đã ngừng sử dụng.");
        if (!await db.Teachers.AnyAsync(x => x.Id == r.TeacherId && x.IsActive && x.DepartmentId == course.DepartmentId, ct))
            throw new ArgumentException("Giảng viên phải còn hoạt động và thuộc bộ môn phụ trách môn học.");
        if (!await db.AttendancePoliciesSet.AnyAsync(x => x.Id == r.AttendancePolicyId && x.IsActive, ct))
            throw new ArgumentException("Cần chọn chính sách chuyên cần đang hoạt động.");
        var term = await db.AcademicTerms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.AcademicTermId, ct)
            ?? throw new ArgumentException("Chưa có cấu hình học kỳ này.");
        if (current is null) _ = ClassSectionCodes.Prefix(term.AcademicYear, term.Semester, course.Code);
        if (term.IsDevelopment && !db.DevelopmentDataEnabled)
            throw new InvalidOperationException("Học kỳ DEMO chỉ dùng khi bật dữ liệu Development; không dùng để sinh lịch Production.");
        var range = AcademicRules.Range(term, await db.TermPhases.AsNoTracking().Where(x => x.AcademicTermId == term.Id).ToListAsync(ct), course.Credits, r.Phase);
        if (range.EndsOn.DayNumber - range.StartsOn.DayNumber > 400) throw new ArgumentException("Khoảng ngày lịch vượt quá 400 ngày.");
        var roomIds = r.Schedules.Select(x => x.RoomId).Distinct().ToArray();
        var rooms = await db.Rooms.AsNoTracking().Where(x => roomIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var exclusions = (await db.ScheduleExceptions.AsNoTracking().Where(x => x.AcademicTermId == term.Id && (x.ClassSectionId == null || x.ClassSectionId == (current == null ? -1 : current.Id))).Select(x => x.Date).ToListAsync(ct)).ToHashSet();
        var occurrences = AcademicRules.Expand(term, range, r.Schedules, rooms, r.TeacherId,
            await db.BellTimeVersions.AsNoTracking().ToListAsync(ct), await db.BellPeriods.AsNoTracking().ToListAsync(ct), exclusions,
            await db.AcademicWeekCalendars.AsNoTracking().Where(x => x.AcademicTermId == term.Id).ToDictionaryAsync(x => x.Id, ct));
        var currentLines = current is null ? [] : await db.ClassSchedules.AsNoTracking().Where(x => x.ClassSectionId == current.Id && x.IsActive).ToArrayAsync(ct);
        var oldSessions = current is null ? [] : await db.SessionsSet.Include(x => x.ClassSchedule).Where(x => x.ClassSectionId == current.Id && x.Status != "CANCELLED").ToArrayAsync(ct);
        var protectedIds = await ProtectedSessionIds(oldSessions.Select(x => x.Id).ToArray(), ct);
        var protectedSessions = oldSessions.Where(x => protectedIds.Contains(x.Id)).ToArray();
        var replaceable = oldSessions.Where(x => !protectedIds.Contains(x.Id)).ToArray();
        var changed = current is null || current.CourseId != r.CourseId || current.TeacherId != r.TeacherId ||
            current.AcademicTermId != r.AcademicTermId || current.Phase != r.Phase || current.AttendancePolicyId != r.AttendancePolicyId ||
            !SameLines(currentLines, r.Schedules);
        if (current is not null)
        {
            var cancellations = await db.SessionsSet.AsNoTracking().Where(x => x.ClassSectionId == current.Id && x.ClassScheduleId != null && x.Status == "CANCELLED" && !x.SupersededByScheduleChange)
                .Select(x => new { x.ClassSchedule!.LineKey, x.OccurrenceDate }).ToArrayAsync(ct);
            occurrences = occurrences.Where(x => x.StartsAtUtc > DateTime.UtcNow &&
                !protectedSessions.Any(s => s.ClassSchedule?.LineKey == x.LineKey && s.OccurrenceDate == x.Date) &&
                !cancellations.Any(s => s.LineKey == x.LineKey && s.OccurrenceDate == x.Date)).ToArray();
            changed |= !SameOccurrences(replaceable, occurrences, current.TeacherId);
        }
        if (changed) await ValidateConflicts(occurrences, replaceable.Select(x => x.Id).ToHashSet(), current?.Id, ct);
        var token = Hash(new { Request = r with { PreviewToken = null }, Revision = current?.Revision ?? 0, Changed = changed, Occurrences = occurrences,
            Sessions = oldSessions.OrderBy(x => x.Id).Select(x => new { x.Id, x.Status, Started = x.StartedAt?.Ticks, Start = x.ScheduledStart.Ticks, End = x.ScheduledEnd?.Ticks, Protected = protectedIds.Contains(x.Id) }) });
        return new(term, range, occurrences, replaceable, protectedSessions, currentLines, changed, token);
    }

    public async Task Apply(ClassSection section, ClassSectionRequest r, SchedulePlan plan, CancellationToken ct)
    {
        section.Name = r.Name.Trim(); section.Note = r.Note?.Trim(); section.SourceClassSectionCode = r.SourceClassSectionCode?.Trim();
        if (plan.ScheduleChanged)
        {
            foreach (var session in plan.Replaceable) { session.Status = "CANCELLED"; session.SupersededByScheduleChange = true; }
            var oldIds = plan.CurrentLines.Select(x => x.Id).ToArray();
            foreach (var old in await db.ClassSchedules.Where(x => oldIds.Contains(x.Id)).ToListAsync(ct)) old.IsActive = false;
            section.CourseId = r.CourseId; section.TeacherId = r.TeacherId; section.AcademicTermId = plan.Term.Id;
            section.AcademicYear = plan.Term.AcademicYear; section.Semester = plan.Term.Semester.ToString();
            section.Phase = r.Phase; section.AttendancePolicyId = r.AttendancePolicyId;
            var roster = section.Id == 0 ? [] : await db.Enrollments.Where(x => x.ClassSectionId == section.Id).Select(x => x.StudentId).ToArrayAsync(ct);
            foreach (var line in r.Schedules)
            {
                var room = await db.Rooms.AsNoTracking().SingleAsync(x => x.Id == line.RoomId, ct);
                var sourceLine = plan.CurrentLines.FirstOrDefault(x => x.LineKey == line.LineKey);
                var schedule = new ClassSchedule { ClassSection = section, LineKey = line.LineKey, Revision = section.Revision + 1,
                    IsoDayOfWeek = line.IsoDayOfWeek, StartPeriod = line.StartPeriod, EndPeriod = line.EndPeriod,
                    BellGroup = AcademicRules.BellGroup(room.RoomType, line.ActivityType, line.BellGroup), RoomId = line.RoomId,
                    ActivityType = line.ActivityType, StartsOn = line.StartsOn, EndsOn = line.EndsOn,
                    SelectedWeeksJson = JsonSerializer.Serialize(line.SelectedWeeks.Distinct().Order()), WeekCalendarId = line.WeekCalendarId, SourceDataJson = sourceLine?.SourceDataJson };
                db.ClassSchedules.Add(schedule);
                foreach (var occurrence in plan.Occurrences.Where(x => x.LineKey == line.LineKey))
                {
                    var shortCode = section.Code.StartsWith("LHP-") ? section.Code[4..] : BusinessCodes.Segment(section.Code, "Mã lớp học phần");
                    if (Regex.IsMatch(shortCode, "[A-F0-9]{32}")) throw new ArgumentException("Lớp học phần đang dùng mã kỹ thuật cũ. Vui lòng chọn lớp có mã nghiệp vụ hợp lệ trước khi sinh buổi học.");
                    var session = new Session { Code = await BusinessCodes.Next(db, "BH-" + shortCode + "-", 3, db.SessionsSet.Where(x => x.Code != null).Select(x => x.Code!), ct), ClassSection = section, ClassSchedule = schedule, OccurrenceDate = occurrence.Date,
                        StartPeriod = occurrence.StartPeriod, EndPeriod = occurrence.EndPeriod, BellTimeVersionId = occurrence.BellTimeVersionId,
                        OriginalTeacherId = r.TeacherId, RoomId = occurrence.RoomId, AttendancePolicyId = r.AttendancePolicyId,
                        ScheduledStart = occurrence.StartsAtUtc, ScheduledEnd = occurrence.EndsAtUtc, Status = "DRAFT" };
                    db.SessionsSet.Add(session);
                    foreach (var studentId in roster) db.SessionStudentsSet.Add(new SessionStudent { Session = session, StudentId = studentId });
                }
            }
        }
        section.Revision++;
        await db.SaveChangesAsync(ct);
    }

    public async Task<HashSet<long>> ProtectedSessionIds(long[] ids, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return (await db.SessionsSet.AsNoTracking().Where(x => ids.Contains(x.Id) &&
            (x.StartedAt != null || x.ScheduledStart <= now || (x.Status != "DRAFT" && x.Status != "READY") || x.LecturerComment != null ||
             db.AttendanceSet.Any(a => a.SessionId == x.Id) || db.StableIdentitiesSet.Any(a => a.SessionId == x.Id) ||
             db.AiEventReceiptsSet.Any(a => a.SessionId == x.Id) ||
             db.BehaviorEventsSet.Any(a => a.SessionId == x.Id) || db.AlertsSet.Any(a => a.SessionId == x.Id) ||
             db.AnalysisJobsSet.Any(a => a.SessionId == x.Id) || db.StudentSessionSummariesSet.Any(a => a.SessionId == x.Id) ||
             db.ClassSessionSummariesSet.Any(a => a.SessionId == x.Id) || db.SessionSubstitutionsSet.Any(a => a.SessionId == x.Id) ||
             db.VideosSet.Any(a => a.SessionId == x.Id)))
            .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
    }

    private async Task ValidateConflicts(PlannedOccurrence[] occurrences, ISet<long> excludedSessions, long? classId, CancellationToken ct)
    {
        if (occurrences.Length == 0) return;
        foreach (var occurrence in occurrences)
            if (occurrences.Any(x => !ReferenceEquals(x, occurrence) && AcademicRules.Overlaps(x.StartsAtUtc, x.EndsAtUtc, occurrence.StartsAtUtc, occurrence.EndsAtUtc)))
                throw new InvalidOperationException($"Các dòng lịch của lớp bị trùng vào {occurrence.Date:dd/MM/yyyy}.");
        var start = occurrences.Min(x => x.StartsAtUtc); var end = occurrences.Max(x => x.EndsAtUtc);
        var existing = await db.SessionsSet.AsNoTracking().Where(x => x.Status != "CANCELLED" && x.ScheduledStart < end && (x.ScheduledEnd == null || x.ScheduledEnd > start))
            .Select(x => new { x.Id, x.ClassSectionId, x.RoomId, x.ScheduledStart, x.ScheduledEnd,
                TeacherId = db.SessionSubstitutionsSet.Where(a => a.SessionId == x.Id && a.Status == "ACTIVE").Select(a => (long?)a.SubstituteTeacherId).FirstOrDefault() ?? (x.OriginalTeacherId == 0 ? x.ClassSection.TeacherId : x.OriginalTeacherId) }).ToListAsync(ct);
        foreach (var occurrence in occurrences)
            if (existing.Any(x => !excludedSessions.Contains(x.Id) && (x.RoomId == occurrence.RoomId || x.TeacherId == occurrence.TeacherId || x.ClassSectionId == classId) &&
                AcademicRules.Overlaps(occurrence.StartsAtUtc, occurrence.EndsAtUtc, x.ScheduledStart, x.ScheduledEnd ?? DateTime.MaxValue)))
                throw new InvalidOperationException($"Trùng phòng, giảng viên hoặc lịch lớp vào {occurrence.Date:dd/MM/yyyy}. Cần xử lý xung đột trước khi lưu.");
    }

    public static bool SameLines(IEnumerable<ClassSchedule> current, IEnumerable<ScheduleLineRequest> requested) =>
        Hash(current.OrderBy(x => x.LineKey).Select(x => new { x.LineKey, x.IsoDayOfWeek, x.StartPeriod, x.EndPeriod, x.BellGroup, x.RoomId, x.ActivityType, x.StartsOn, x.EndsOn, x.WeekCalendarId, Weeks = JsonSerializer.Deserialize<int[]>(x.SelectedWeeksJson)!.Order().ToArray() })) ==
        Hash(requested.OrderBy(x => x.LineKey).Select(x => new { x.LineKey, x.IsoDayOfWeek, x.StartPeriod, x.EndPeriod, x.BellGroup, x.RoomId, x.ActivityType, x.StartsOn, x.EndsOn, x.WeekCalendarId, Weeks = x.SelectedWeeks.Distinct().Order().ToArray() }));

    private static bool SameOccurrences(IEnumerable<Session> current, IEnumerable<PlannedOccurrence> planned, long teacherId) =>
        Hash(current.OrderBy(x => x.ClassSchedule?.LineKey).ThenBy(x => x.OccurrenceDate).Select(x => new { LineKey = x.ClassSchedule?.LineKey, Date = x.OccurrenceDate,
            x.RoomId, TeacherId = x.OriginalTeacherId == 0 ? teacherId : x.OriginalTeacherId, x.StartPeriod, x.EndPeriod, x.BellTimeVersionId, Start = x.ScheduledStart.Ticks, End = x.ScheduledEnd?.Ticks })) ==
        Hash(planned.OrderBy(x => x.LineKey).ThenBy(x => x.Date).Select(x => new { x.LineKey, Date = (DateOnly?)x.Date,
            RoomId = (long?)x.RoomId, x.TeacherId, StartPeriod = (int?)x.StartPeriod, EndPeriod = (int?)x.EndPeriod, BellTimeVersionId = (long?)x.BellTimeVersionId, Start = x.StartsAtUtc.Ticks, End = (long?)x.EndsAtUtc.Ticks }));

    public static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
