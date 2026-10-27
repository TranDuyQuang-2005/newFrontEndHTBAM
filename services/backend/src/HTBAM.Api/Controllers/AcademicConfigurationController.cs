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

[ApiController, Authorize, Route("api/academic-configuration")]
public sealed class AcademicConfigurationController(AppDbContext db, IAuditService audit) : ControllerBase
{
    [HttpGet("week-one")]
    public async Task<ActionResult> WeekOne([FromQuery] string academicYear, [FromQuery] int semester, CancellationToken ct)
    {
        var term = await db.AcademicTerms.AsNoTracking().SingleOrDefaultAsync(x => x.AcademicYear == academicYear && x.Semester == semester, ct);
        return term is null ? NotFound("Chưa cấu hình học kỳ này.") : Ok(new { term.Id, term.AcademicYear, term.Semester, term.WeekOneMonday, term.WeekMappingConfirmed, term.IsDevelopment });
    }

    [HttpPut("terms/{id:long}/week-one")]
    public Task<ActionResult> SetWeekOne(long id, WeekOneRequest r, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            if (!await AccessScope.CanConfigureAcademicsAsync(db, User, ct)) return Forbid();
            var term = await db.AcademicTerms.FindAsync([id], ct);
            if (term is null) return NotFound();
            if (!r.Confirmed || r.WeekOneMonday.DayOfWeek != DayOfWeek.Monday || string.IsNullOrWhiteSpace(r.Evidence)) return BadRequest("Cần Mốc bắt đầu Tuần 1 vào thứ Hai, căn cứ và xác nhận.");
            if (!WeekOneContainsStart(r.WeekOneMonday, term.StartsOn)) return BadRequest("Tuần 1 phải bao gồm ngày bắt đầu học kỳ.");
            if (term.WeekOneMonday != r.WeekOneMonday && await db.ClassSchedules.AnyAsync(x => x.ClassSection.AcademicTermId == id && x.WeekCalendarId == null && x.SelectedWeeksJson != "[]", ct))
                return Conflict("Mốc T1 đã được dùng bởi lịch chọn tuần. Cần đối chiếu ảnh hưởng trước khi thay đổi để giữ lịch sử.");
            term.WeekOneMonday = r.WeekOneMonday; term.WeekMappingConfirmed = true; term.Evidence = r.Evidence.Trim();
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(UserContext.Id(User), "WEEK_ONE_CONFIGURE", "AcademicTerm", id.ToString(), r, ct);
            return Ok(term);
        }, ct);
    [HttpGet]
    public async Task<ActionResult> Get(CancellationToken ct) => Ok(new
    {
        Terms = await db.AcademicTerms.AsNoTracking().OrderByDescending(x => x.AcademicYear).ThenBy(x => x.Semester).ToListAsync(ct),
        Phases = await db.TermPhases.AsNoTracking().OrderBy(x => x.Credits).ThenBy(x => x.Number).ToListAsync(ct),
        WeekCalendars = await db.AcademicWeekCalendars.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct),
        BellVersions = await db.BellTimeVersions.AsNoTracking().OrderBy(x => x.EffectiveFrom).ToListAsync(ct),
        Periods = await db.BellPeriods.AsNoTracking().OrderBy(x => x.Number).ToListAsync(ct),
        Exceptions = await db.ScheduleExceptions.AsNoTracking().Where(x => x.ClassSectionId == null).OrderBy(x => x.Date).ToListAsync(ct)
    });

    [HttpPost("terms")]
    public Task<ActionResult> CreateTerm(AcademicTermRequest request, CancellationToken ct) => SaveTerm(null, request, ct);

    [HttpPut("terms/{id:long}")]
    public Task<ActionResult> UpdateTerm(long id, AcademicTermRequest request, CancellationToken ct) => SaveTerm(id, request, ct);

    private Task<ActionResult> SaveTerm(long? id, AcademicTermRequest r, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            if (!await AccessScope.CanConfigureAcademicsAsync(db, User, ct)) return Forbid();
            if (!Regex.IsMatch(r.AcademicYear, @"^\d{4}-\d{4}$") || int.Parse(r.AcademicYear[..4]) + 1 != int.Parse(r.AcademicYear[5..]) || r.Semester is < 1 or > 2)
                return BadRequest("Năm học phải có dạng 2025-2026 và chỉ có học kỳ 1/2.");
            if (r.StartsOn is not null && r.EndsOn is not null && (r.EndsOn < r.StartsOn || r.EndsOn.Value.DayNumber - r.StartsOn.Value.DayNumber > 400))
                return BadRequest("Khoảng ngày học kỳ không hợp lệ hoặc vượt quá 400 ngày.");
            if (r.DatesConfirmed && (r.StartsOn is null || r.EndsOn is null || string.IsNullOrWhiteSpace(r.Evidence)))
                return BadRequest("Cần đủ ngày học kỳ và căn cứ trước khi xác nhận.");
            if (r.WeekMappingConfirmed && (r.WeekOneMonday is null || r.WeekOneMonday.Value.DayOfWeek != DayOfWeek.Monday || string.IsNullOrWhiteSpace(r.Evidence)))
                return BadRequest("Cần xác nhận mốc thứ Hai của tuần T1 và căn cứ.");
            if (r.WeekMappingConfirmed && !WeekOneContainsStart(r.WeekOneMonday!.Value, r.StartsOn))
                return BadRequest("Tuần 1 phải bao gồm ngày bắt đầu học kỳ.");
            if (r.DivisionPolicy is not ("EXPLICIT" or "EQUAL_DAYS")) return BadRequest("Chính sách giai đoạn không hợp lệ.");
            if (await db.AcademicTerms.AnyAsync(x => x.Id != id && x.AcademicYear == r.AcademicYear && x.Semester == r.Semester, ct)) return Conflict("Học kỳ này đã có cấu hình.");
            var term = id is null ? new AcademicTerm() : await db.AcademicTerms.FindAsync([id.Value], ct);
            if (term is null) return NotFound();
            if (id is not null && await db.ClassSections.AnyAsync(x => x.AcademicTermId == id, ct))
                return Conflict("Học kỳ đã được dùng bởi lớp học phần. Không sửa cấu hình nền làm thay đổi lịch sử; cần đối chiếu ảnh hưởng các lớp trước.");
            term.AcademicYear = r.AcademicYear; term.Semester = r.Semester; term.StartsOn = r.StartsOn; term.EndsOn = r.EndsOn;
            term.DatesConfirmed = r.DatesConfirmed; term.DivisionPolicy = r.DivisionPolicy; term.PhasesConfirmed = r.PhasesConfirmed;
            term.WeekOneMonday = r.WeekOneMonday; term.WeekMappingConfirmed = r.WeekMappingConfirmed; term.Evidence = r.Evidence?.Trim();
            var phases = r.Phases.Select(x => new TermPhase { AcademicTerm = term, Credits = x.Credits, Number = x.Number, StartsOn = x.StartsOn, EndsOn = x.EndsOn }).ToArray();
            if (phases.Any(x => x.Credits is not (1 or 2)) || phases.GroupBy(x => (x.Credits, x.Number)).Any(x => x.Count() > 1)) return BadRequest("Cấu hình giai đoạn bị trùng hoặc sai tín chỉ.");
            if (r.PhasesConfirmed)
            {
                if (!r.DatesConfirmed) return BadRequest("Cần xác nhận ngày học kỳ trước khi xác nhận giai đoạn.");
                foreach (var credits in new[] { 1, 2 })
                {
                    if (r.DivisionPolicy == "EXPLICIT" && !phases.Where(x => x.Credits == credits).Select(x => x.Number).Order().SequenceEqual(Enumerable.Range(1, credits == 1 ? 3 : 2)))
                        return BadRequest("Cần đủ các giai đoạn đánh số liên tiếp cho cả môn 1 và 2 tín chỉ.");
                    _ = AcademicRules.Range(term, phases, credits, 1);
                }
            }
            if (id is null) db.AcademicTerms.Add(term);
            else db.TermPhases.RemoveRange(await db.TermPhases.Where(x => x.AcademicTermId == id).ToListAsync(ct));
            db.TermPhases.AddRange(phases);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(UserContext.Id(User), "ACADEMIC_TERM_CONFIGURE", "AcademicTerm", term.Id.ToString(), r, ct);
            return Ok(term);
        }, ct);

    private static bool WeekOneContainsStart(DateOnly monday, DateOnly? startsOn) =>
        startsOn is null || startsOn.Value.DayNumber - monday.DayNumber is >= 0 and <= 6;

    [HttpGet("terms/{id:long}/range")]
    public async Task<ActionResult> Range(long id, [FromQuery] int credits, [FromQuery] int? phase, CancellationToken ct)
    {
        var term = await db.AcademicTerms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (term is null) return NotFound();
        var range = AcademicRules.Range(term, await db.TermPhases.AsNoTracking().Where(x => x.AcademicTermId == id).ToListAsync(ct), credits, phase);
        return Ok(range);
    }

    [HttpPost("bell-versions")]
    public Task<ActionResult> AddBellVersion(BellVersionRequest r, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            if (!await AccessScope.CanConfigureAcademicsAsync(db, User, ct)) return Forbid();
            if (r.Group is not ("A" or "B") || !r.Confirmed || string.IsNullOrWhiteSpace(r.Evidence) || string.IsNullOrWhiteSpace(r.Code) || r.EffectiveTo < r.EffectiveFrom)
                return BadRequest("Cần nhóm A/B, phiên bản, khoảng hiệu lực và căn cứ đã xác nhận.");
            var periods = r.Periods.OrderBy(x => x.Number).ToArray();
            if (!periods.Select(x => x.Number).SequenceEqual(Enumerable.Range(1, 16)) ||
                periods.Any(x => x.EndsAt <= x.StartsAt || x.Shift != (x.Number <= 6 ? 1 : x.Number <= 12 ? 2 : 3)) ||
                periods.Skip(1).Where((x, i) => x.StartsAt < periods[i].EndsAt).Any()) return BadRequest("Bảng giờ phải có đủ 16 tiết đúng ca, không chồng lấn.");
            var overlaps = await db.BellTimeVersions.Where(x => x.Group == r.Group && (r.EffectiveTo == null || x.EffectiveFrom <= r.EffectiveTo) && (x.EffectiveTo == null || x.EffectiveTo >= r.EffectiveFrom)).ToListAsync(ct);
            if (overlaps.Any(x => x.EffectiveTo != null || x.EffectiveFrom >= r.EffectiveFrom))
                return Conflict("Khoảng hiệu lực trùng phiên bản giờ đã có. Bảng giờ cũ bổ sung phải kết thúc trước phiên bản tiếp theo.");
            // Appending a new version only closes the previous validity interval; its periods and stored sessions remain intact.
            foreach (var previous in overlaps) previous.EffectiveTo = r.EffectiveFrom.AddDays(-1);
            var version = new BellTimeVersion { Code = r.Code.Trim(), Group = r.Group, EffectiveFrom = r.EffectiveFrom, EffectiveTo = r.EffectiveTo, Confirmed = true, Evidence = r.Evidence.Trim() };
            db.BellTimeVersions.Add(version);
            db.BellPeriods.AddRange(periods.Select(x => new BellPeriod { BellTimeVersion = version, Number = x.Number, Shift = x.Shift, StartsAt = x.StartsAt, EndsAt = x.EndsAt }));
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(UserContext.Id(User), "BELL_VERSION_CREATE", "BellTimeVersion", version.Id.ToString(), r, ct);
            return Ok(version);
        }, ct);

    [HttpPost("week-calendars")]
    public async Task<ActionResult> AddWeekCalendar(WeekCalendarRequest r, CancellationToken ct)
    {
        if (!await AccessScope.CanConfigureAcademicsAsync(db, User, ct)) return Forbid();
        if (!r.Confirmed || r.WeekOneMonday.DayOfWeek != DayOfWeek.Monday || string.IsNullOrWhiteSpace(r.Evidence) || r.Name.Length > 128 ||
            !await db.AcademicTerms.AnyAsync(x => x.Id == r.AcademicTermId, ct)) return BadRequest("Cần mốc thứ Hai T1, học kỳ, nhóm áp dụng và căn cứ đã xác nhận.");
        var calendar = new AcademicWeekCalendar { AcademicTermId = r.AcademicTermId, Name = r.Name.Trim(), WeekOneMonday = r.WeekOneMonday, Confirmed = true, Evidence = r.Evidence.Trim() };
        db.AcademicWeekCalendars.Add(calendar); await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "WEEK_CALENDAR_CREATE", "AcademicWeekCalendar", calendar.Id.ToString(), r, ct);
        return Ok(calendar);
    }

    [HttpPost("exceptions")]
    public async Task<ActionResult> AddException(ScheduleExceptionRequest r, CancellationToken ct)
    {
        if (!await AccessScope.CanConfigureAcademicsAsync(db, User, ct)) return Forbid();
        var term = await db.AcademicTerms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.AcademicTermId, ct);
        if (term is null || (term.StartsOn != null && r.Date < term.StartsOn) || (term.EndsOn != null && r.Date > term.EndsOn)) return BadRequest("Ngày nghỉ nằm ngoài học kỳ.");
        if (r.ClassSectionId != null && !await db.ClassSections.AnyAsync(x => x.Id == r.ClassSectionId && x.AcademicTermId == r.AcademicTermId, ct)) return BadRequest("Lớp không thuộc học kỳ này.");
        if (await db.ScheduleExceptions.AnyAsync(x => x.AcademicTermId == r.AcademicTermId && x.ClassSectionId == r.ClassSectionId && x.Date == r.Date, ct)) return Conflict("Ngoại lệ đã có.");
        var exception = new ScheduleException { AcademicTermId = r.AcademicTermId, ClassSectionId = r.ClassSectionId, Date = r.Date, Reason = r.Reason.Trim() };
        db.ScheduleExceptions.Add(exception); await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "SCHEDULE_EXCEPTION_CREATE", "ScheduleException", exception.Id.ToString(), r, ct);
        return Ok(new { exception.Id, Message = "Đã lưu ngoại lệ. Các lớp đã có lịch cần xem trước và áp dụng thay đổi để đồng bộ các buổi tương lai." });
    }
}
