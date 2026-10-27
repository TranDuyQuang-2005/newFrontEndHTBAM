using HTBAM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Infrastructure.Data;

public static class AcademicSeed
{
    // Transcribed from the supplied HUIT notice, teaching tables 1 and 2 only.
    public static readonly string[] GroupA = ["06:30-07:20", "07:20-08:10", "08:10-09:00", "09:30-10:20", "10:20-11:10", "11:10-12:00", "12:30-13:20", "13:20-14:10", "14:10-15:00", "15:30-16:20", "16:20-17:10", "17:10-18:00", "18:10-19:00", "19:00-19:50", "19:50-20:40", "20:40-21:30"];
    public static readonly string[] GroupB = ["07:00-07:50", "07:50-08:40", "08:40-09:30", "09:30-10:20", "10:20-11:10", "11:10-12:00", "12:30-13:20", "13:20-14:10", "14:10-15:00", "15:00-15:50", "15:50-16:40", "16:40-17:30", "18:00-18:50", "18:50-19:40", "19:40-20:30", "20:30-21:20"];

    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (!await db.AcademicTerms.AnyAsync(x => x.AcademicYear == "2025-2026" && x.Semester == 2, ct))
            db.AcademicTerms.Add(new AcademicTerm { AcademicYear = "2025-2026", Semester = 2, StartsOn = new(2026, 1, 12), Evidence = "Thông báo HUIT được cung cấp: HK2 bắt đầu 12/01/2026. Chưa có ngày kết thúc/giai đoạn chính thức." });
        if (!await db.AcademicTerms.AnyAsync(x => x.AcademicYear == "2025-2026" && x.Semester == 1, ct))
            db.AcademicTerms.Add(new AcademicTerm { AcademicYear = "2025-2026", Semester = 1, Evidence = "Excel dự kiến HK1 không xác nhận ngày học kỳ chính thức." });
        foreach (var group in new[] { "A", "B" })
        {
            var version = await db.BellTimeVersions.SingleOrDefaultAsync(x => x.Group == group && x.EffectiveFrom == new DateOnly(2026, 1, 12), ct);
            if (version == null)
            {
                version = new BellTimeVersion { Code = $"HUIT-2026-01-12-{group}", Group = group, EffectiveFrom = new(2026, 1, 12), Confirmed = true, Evidence = $"Ảnh thông báo HUIT: bảng giờ học nhóm {group}; không dùng bảng giờ thi." };
                db.BellTimeVersions.Add(version);
            }
            var existing = version.Id == 0 ? [] : await db.BellPeriods.Where(x => x.BellTimeVersionId == version.Id).Select(x => x.Number).ToArrayAsync(ct);
            foreach (var (window, index) in (group == "A" ? GroupA : GroupB).Select((x, i) => (x, i)))
            {
                if (existing.Contains(index + 1)) continue;
                var time = window.Split('-');
                db.BellPeriods.Add(new BellPeriod { BellTimeVersion = version, Number = index + 1, Shift = index < 6 ? 1 : index < 12 ? 2 : 3, StartsAt = TimeOnly.ParseExact(time[0], "HH:mm"), EndsAt = TimeOnly.ParseExact(time[1], "HH:mm") });
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
