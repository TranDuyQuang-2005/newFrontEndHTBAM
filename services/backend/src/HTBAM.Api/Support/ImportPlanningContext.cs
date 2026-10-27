using HTBAM.Application.DTOs;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Support;

public sealed class ImportPlanningContext
{
    public AcademicTerm Term { get; init; } = null!;
    public Course[] Courses { get; init; } = [];
    public Teacher[] Teachers { get; init; } = [];
    public Dictionary<long, Room> Rooms { get; init; } = [];
    public BellTimeVersion[] Versions { get; init; } = [];
    public BellPeriod[] Periods { get; init; } = [];
    public TermPhase[] Phases { get; init; } = [];
    public HashSet<DateOnly> Exclusions { get; init; } = [];
    public HashSet<long> AllowedDepartments { get; init; } = [];
    public HashSet<long> ActiveDepartments { get; init; } = [];
    public bool PolicyValid { get; init; }
    public bool DevelopmentEnabled { get; init; }
    public Dictionary<string, ClassSection> Existing { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<long, ClassSchedule[]> ExistingLines { get; init; } = [];
    private readonly Dictionary<(long Id, DateOnly Date), List<(DateTime Start, DateTime End)>> roomSlots = [];
    private readonly Dictionary<(long Id, DateOnly Date), List<(DateTime Start, DateTime End)>> teacherSlots = [];

    public static async Task<ImportPlanningContext> Load(AppDbContext db, AcademicTerm term, long policyId, System.Security.Claims.ClaimsPrincipal user, CancellationToken ct)
    {
        var departments = await db.DepartmentsSet.AsNoTracking().Include(x => x.Faculty).ToArrayAsync(ct);
        var allowed = new HashSet<long>();
        foreach (var department in departments)
            if (await AccessScope.CanManageDepartmentDataAsync(db, user, department.Id, ct)) allowed.Add(department.Id);
        var existing = await db.ClassSections.AsNoTracking().Where(x => x.AcademicTermId == term.Id && x.SourceClassSectionCode != null).ToArrayAsync(ct);
        var lines = await db.ClassSchedules.AsNoTracking().Where(x => x.IsActive && x.ClassSection.AcademicTermId == term.Id && x.ClassSection.SourceClassSectionCode != null).ToArrayAsync(ct);
        var context = new ImportPlanningContext {
            Term = term, Courses = await db.Courses.AsNoTracking().Where(x => x.IsActive && x.DepartmentId != null && allowed.Contains(x.DepartmentId.Value)).ToArrayAsync(ct),
            Teachers = await db.Teachers.AsNoTracking().Where(x => x.IsActive && x.DepartmentId != null && allowed.Contains(x.DepartmentId.Value)).ToArrayAsync(ct),
            Rooms = await db.Rooms.AsNoTracking().Where(x => x.IsActive).ToDictionaryAsync(x => x.Id, ct),
            Versions = await db.BellTimeVersions.AsNoTracking().ToArrayAsync(ct), Periods = await db.BellPeriods.AsNoTracking().ToArrayAsync(ct),
            Phases = await db.TermPhases.AsNoTracking().Where(x => x.AcademicTermId == term.Id).ToArrayAsync(ct),
            Exclusions = (await db.ScheduleExceptions.AsNoTracking().Where(x => x.AcademicTermId == term.Id && x.ClassSectionId == null).Select(x => x.Date).ToArrayAsync(ct)).ToHashSet(),
            AllowedDepartments = allowed, ActiveDepartments = departments.Where(x => x.IsActive && x.Faculty.IsActive).Select(x => x.Id).ToHashSet(),
            PolicyValid = await db.AttendancePoliciesSet.AnyAsync(x => x.Id == policyId && x.IsActive, ct), DevelopmentEnabled = db.DevelopmentDataEnabled,
            Existing = existing.ToDictionary(x => x.SourceClassSectionCode!, StringComparer.OrdinalIgnoreCase), ExistingLines = lines.GroupBy(x => x.ClassSectionId).ToDictionary(x => x.Key, x => x.ToArray()),
        };
        if (term.StartsOn is DateOnly first && term.EndsOn is DateOnly last)
        {
            var start = AcademicRules.VietnamUtc(first, TimeOnly.MinValue); var end = AcademicRules.VietnamUtc(last.AddDays(1), TimeOnly.MinValue);
            var sessions = await db.SessionsSet.AsNoTracking().Where(x => x.Status != "CANCELLED" && x.ScheduledStart < end && (x.ScheduledEnd == null || x.ScheduledEnd > start))
                .Select(x => new { x.RoomId, x.ScheduledStart, x.ScheduledEnd, TeacherId = db.SessionSubstitutionsSet.Where(s => s.SessionId == x.Id && s.Status == "ACTIVE").Select(s => (long?)s.SubstituteTeacherId).FirstOrDefault() ?? (x.OriginalTeacherId == 0 ? x.ClassSection.TeacherId : x.OriginalTeacherId) }).ToArrayAsync(ct);
            foreach (var session in sessions)
            {
                var until = session.ScheduledEnd ?? end;
                var firstDate = DateOnly.FromDateTime(session.ScheduledStart.AddHours(7)); if (firstDate < first) firstDate = first;
                for (var date = firstDate; date <= DateOnly.FromDateTime(until.AddHours(7)); date = date.AddDays(1))
                {
                    if (session.RoomId is long room) Add(context.roomSlots, room, date, session.ScheduledStart, until);
                    Add(context.teacherSlots, session.TeacherId, date, session.ScheduledStart, until);
                }
            }
        }
        return context;
    }

    public (PlannedOccurrence[] Occurrences, string Token) Plan(ClassSectionRequest request)
    {
        if (request.Schedules.Length is < 1 or > 32) throw new ArgumentException("Cần 1–32 dòng lịch không trùng nhau cho một lớp.");
        if (request.Name.Length is < 1 or > 256) throw new ArgumentException("Tên lớp cần 1–256 ký tự.");
        var course = Courses.Single(x => x.Id == request.CourseId);
        if (course.DepartmentId is null || !ActiveDepartments.Contains(course.DepartmentId.Value)) throw new ArgumentException("Bộ môn hoặc khoa của môn học đã ngừng sử dụng.");
        if (!PolicyValid) throw new ArgumentException("Cần chọn chính sách chuyên cần đang hoạt động.");
        if (Term.IsDevelopment && !DevelopmentEnabled) throw new InvalidOperationException("Học kỳ DEMO chỉ dùng khi bật dữ liệu Development.");
        _ = ClassSectionCodes.Prefix(Term.AcademicYear, Term.Semester, course.Code);
        var range = AcademicRules.Range(Term, Phases, course.Credits, request.Phase);
        if (range.EndsOn.DayNumber - range.StartsOn.DayNumber > 400) throw new ArgumentException("Khoảng ngày lịch vượt quá 400 ngày.");
        var occurrences = AcademicRules.Expand(Term, range, request.Schedules, Rooms, request.TeacherId, Versions, Periods, Exclusions);
        foreach (var occurrence in occurrences)
        {
            if (occurrences.Any(x => !ReferenceEquals(x, occurrence) && AcademicRules.Overlaps(x.StartsAtUtc, x.EndsAtUtc, occurrence.StartsAtUtc, occurrence.EndsAtUtc))) throw new ArgumentException($"Các dòng lịch của lớp bị trùng vào {occurrence.Date:dd/MM/yyyy}.");
            if (Overlaps(roomSlots, occurrence.RoomId, occurrence) || Overlaps(teacherSlots, occurrence.TeacherId, occurrence)) throw new ArgumentException($"Trùng phòng hoặc giảng viên vào {occurrence.Date:dd/MM/yyyy}, trong database hoặc nhóm đã xem trước.");
        }
        foreach (var occurrence in occurrences)
        {
            Add(roomSlots, occurrence.RoomId, occurrence.Date, occurrence.StartsAtUtc, occurrence.EndsAtUtc);
            Add(teacherSlots, occurrence.TeacherId, occurrence.Date, occurrence.StartsAtUtc, occurrence.EndsAtUtc);
        }
        // Same canonical input as ClassSchedulePlanner.Prepare(create), without per-group database queries.
        var token = ClassSchedulePlanner.Hash(new { Request = request with { PreviewToken = null }, Revision = 0, Changed = true, Occurrences = occurrences, Sessions = Array.Empty<object>() });
        return (occurrences, token);
    }
    private static void Add(Dictionary<(long, DateOnly), List<(DateTime, DateTime)>> slots, long id, DateOnly day, DateTime start, DateTime end)
    { if (!slots.TryGetValue((id, day), out var list)) slots[(id, day)] = list = []; list.Add((start, end)); }
    private static bool Overlaps(Dictionary<(long, DateOnly), List<(DateTime Start, DateTime End)>> slots, long id, PlannedOccurrence occurrence) => slots.TryGetValue((id, occurrence.Date), out var list) && list.Any(x => AcademicRules.Overlaps(x.Start, x.End, occurrence.StartsAtUtc, occurrence.EndsAtUtc));
}
