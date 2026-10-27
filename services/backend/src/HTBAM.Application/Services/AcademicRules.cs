using HTBAM.Application.DTOs;
using HTBAM.Domain.Entities;

namespace HTBAM.Application.Services;

public record AcademicDateRange(DateOnly StartsOn, DateOnly EndsOn);
public record PlannedOccurrence(string LineKey, DateOnly Date, int StartPeriod, int EndPeriod,
    long RoomId, long TeacherId, long BellTimeVersionId, DateTime StartsAtUtc, DateTime EndsAtUtc);

public static class AcademicRules
{
    public static AcademicDateRange Range(AcademicTerm term, IEnumerable<TermPhase> phases, int credits, int? phase)
    {
        if (credits is < 1 or > 3) throw new ArgumentException("Tín chỉ ngoài 1–3 chưa có quy tắc giai đoạn phù hợp.");
        var missing = new List<string>();
        if (term.StartsOn is null) missing.Add("ngày bắt đầu học kỳ");
        if (term.EndsOn is null) missing.Add("ngày kết thúc học kỳ");
        if (!term.DatesConfirmed && !term.IsDevelopment) missing.Add("xác nhận ngày học kỳ");
        if (missing.Count > 0) throw new InvalidOperationException("Còn thiếu: " + string.Join(", ", missing) + ". Hãy mở Cấu hình học vụ trước khi sinh lịch.");
        if (term.EndsOn < term.StartsOn) throw new ArgumentException("Ngày kết thúc học kỳ phải sau ngày bắt đầu.");
        if (credits == 3)
        {
            if (phase is not null) throw new ArgumentException("Môn 3 tín chỉ học nguyên kỳ, không chọn giai đoạn.");
            return new(term.StartsOn!.Value, term.EndsOn!.Value);
        }
        var count = credits == 2 ? 2 : 3;
        if (phase is null || phase < 1 || phase > count) throw new ArgumentException($"Cần chọn giai đoạn từ 1 đến {count}.");
        if (!term.PhasesConfirmed && !term.IsDevelopment) throw new InvalidOperationException("Chính sách và ranh giới giai đoạn chưa được xác nhận.");
        var ranges = term.DivisionPolicy == "EQUAL_DAYS"
            ? EqualDays(term.StartsOn!.Value, term.EndsOn!.Value, count)
            : term.DivisionPolicy == "EXPLICIT"
                ? phases.Where(x => x.Credits == credits).OrderBy(x => x.Number)
                    .Select(x => new AcademicDateRange(x.StartsOn, x.EndsOn)).ToArray()
                : throw new ArgumentException("Chính sách chia giai đoạn chưa được hỗ trợ.");
        ValidateCoverage(term, ranges, count);
        return ranges[phase.Value - 1];
    }

    public static AcademicDateRange[] EqualDays(DateOnly start, DateOnly end, int count)
    {
        var days = end.DayNumber - start.DayNumber + 1;
        if (count <= 0 || days < count) throw new ArgumentException("Khoảng ngày không đủ để chia giai đoạn.");
        var ranges = new List<AcademicDateRange>();
        for (var i = 0; i < count; i++)
        {
            var size = days / count + (i < days % count ? 1 : 0);
            ranges.Add(new(start, start.AddDays(size - 1)));
            start = start.AddDays(size);
        }
        return ranges.ToArray();
    }

    public static void ValidateCoverage(AcademicTerm term, AcademicDateRange[] ranges, int count)
    {
        if (ranges.Length != count || ranges[0].StartsOn != term.StartsOn || ranges[^1].EndsOn != term.EndsOn ||
            ranges.Any(x => x.EndsOn < x.StartsOn) ||
            ranges.Skip(1).Where((x, i) => x.StartsOn != ranges[i].EndsOn.AddDays(1)).Any())
            throw new ArgumentException("Các giai đoạn phải liên tiếp, không chồng lấn và phủ đủ học kỳ; cách chia 2 và 3 độc lập.");
    }

    public static string BellGroup(string roomType, string activityType, string? selectedGroup)
    {
        if (activityType is not ("LT" or "TH")) throw new ArgumentException("Loại lịch phải là LT hoặc TH.");
        var derived = roomType switch
        {
            "THEORY" or "ZOOM" => "A",
            "COMPUTER" or "LAB" or "PRACTICE" or "PE" or "DEFENSE" => "B",
            _ => null
        };
        if (derived is null && selectedGroup is not ("A" or "B"))
            throw new ArgumentException("Chưa xác định loại phòng: cần chọn rõ nhóm giờ A hoặc B.");
        if (derived is not null && !string.IsNullOrEmpty(selectedGroup) && selectedGroup != derived)
            throw new ArgumentException("Nhóm giờ đã chọn không phù hợp loại phòng. Hãy tính lại giờ và kiểm tra xung đột.");
        return derived ?? selectedGroup!;
    }

    public static (TimeOnly Start, TimeOnly End) PeriodWindow(IEnumerable<BellPeriod> periods, int start, int end)
    {
        if (start < 1 || end > 16 || end < start) throw new ArgumentException("Khoảng tiết không hợp lệ.");
        var selected = periods.Where(x => x.Number >= start && x.Number <= end).OrderBy(x => x.Number).ToArray();
        if (selected.Length != end - start + 1 || selected.Select(x => x.Shift).Distinct().Count() != 1)
            throw new ArgumentException("Không được tạo một buổi kéo dài qua nhiều ca học hoặc dùng tiết chưa cấu hình.");
        return (selected[0].StartsAt, selected[^1].EndsAt);
    }

    public static PlannedOccurrence[] Expand(AcademicTerm term, AcademicDateRange range, IEnumerable<ScheduleLineRequest> lines,
        IReadOnlyDictionary<long, Room> rooms, long teacherId, IEnumerable<BellTimeVersion> versions,
        IEnumerable<BellPeriod> periods, ISet<DateOnly> exclusions, IReadOnlyDictionary<long, AcademicWeekCalendar>? weekCalendars = null)
    {
        var result = new List<PlannedOccurrence>();
        foreach (var line in lines)
        {
            if (line.IsoDayOfWeek is < 1 or > 7 || line.StartsOn < range.StartsOn || line.EndsOn > range.EndsOn || line.EndsOn < line.StartsOn)
                throw new ArgumentException("Thứ hoặc khoảng ngày lịch nằm ngoài giai đoạn được chọn.");
            if (!rooms.TryGetValue(line.RoomId, out var room) || !room.IsActive) throw new ArgumentException("Phòng học không tồn tại hoặc đã ngừng sử dụng.");
            var group = BellGroup(room.RoomType, line.ActivityType, line.BellGroup);
            var weeks = line.SelectedWeeks.Distinct().ToArray();
            var weekOne = term.WeekOneMonday;
            var weekConfirmed = term.WeekMappingConfirmed || term.IsDevelopment;
            if (line.WeekCalendarId != null)
            {
                if (weekCalendars is null || !weekCalendars.TryGetValue(line.WeekCalendarId.Value, out var calendar) || calendar.AcademicTermId != term.Id)
                    throw new ArgumentException("Mốc tuần theo nhóm không thuộc học kỳ của lớp.");
                weekOne = calendar.WeekOneMonday; weekConfirmed = calendar.Confirmed;
            }
            if (weeks.Any(x => x is < 1 or > 53)) throw new ArgumentException("Số tuần không hợp lệ.");
            if (weeks.Length > 0 && (!weekConfirmed || weekOne is null || weekOne.Value.DayOfWeek != DayOfWeek.Monday))
                throw new InvalidOperationException("Chưa xác nhận mốc tuần T1. Không thể tự suy ra tuần từ ngày bắt đầu từng dòng.");
            if (weeks.Length > 0)
            {
                var maxWeek = (term.EndsOn!.Value.DayNumber - weekOne!.Value.DayNumber) / 7 + 1;
                if (weeks.Any(w => w > maxWeek || weekOne.Value.AddDays((w - 1) * 7) > line.EndsOn || weekOne.Value.AddDays((w - 1) * 7 + 6) < line.StartsOn))
                    throw new ArgumentException("Các tuần có lịch vượt ngoài học kỳ hoặc khoảng ngày của dòng lịch.");
            }
            var lineOccurrences = 0;
            for (var date = line.StartsOn; date <= line.EndsOn; date = date.AddDays(1))
            {
                var isoDay = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
                if (isoDay != line.IsoDayOfWeek || exclusions.Contains(date)) continue;
                if (weeks.Length > 0)
                {
                    var delta = date.DayNumber - weekOne!.Value.DayNumber;
                    if (delta < 0 || !weeks.Contains(delta / 7 + 1)) continue;
                }
                var version = versions.Where(x => x.Group == group && x.Confirmed && x.EffectiveFrom <= date &&
                    (x.EffectiveTo == null || x.EffectiveTo >= date)).OrderByDescending(x => x.EffectiveFrom).FirstOrDefault();
                if (version is null) throw new InvalidOperationException($"Thiếu bảng giờ có căn cứ cho nhóm {group}, ngày {date:dd/MM/yyyy}. Không áp dụng ngược bảng mới.");
                var window = PeriodWindow(periods.Where(x => x.BellTimeVersionId == version.Id), line.StartPeriod, line.EndPeriod);
                result.Add(new(line.LineKey, date, line.StartPeriod, line.EndPeriod, room.Id, teacherId, version.Id,
                    VietnamUtc(date, window.Start), VietnamUtc(date, window.End)));
                lineOccurrences++;
            }
            if (lineOccurrences == 0) throw new ArgumentException("Một dòng lịch không phát sinh ngày học nào; hãy kiểm tra thứ, tuần và ngoại lệ.");
        }
        return result.ToArray();
    }

    public static DateTime VietnamUtc(DateOnly date, TimeOnly time) =>
        new DateTimeOffset(date.ToDateTime(time, DateTimeKind.Unspecified), TimeSpan.FromHours(7)).UtcDateTime;

    public static bool Overlaps(DateTime start, DateTime end, DateTime otherStart, DateTime otherEnd) => start < otherEnd && otherStart < end;
}
