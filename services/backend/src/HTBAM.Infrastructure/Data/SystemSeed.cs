using HTBAM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Infrastructure.Data;

public static class SystemSeed
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        foreach (var name in new[] { "ADMIN", "LECTURER", "TECH_AI" })
            if (!await db.RolesSet.AnyAsync(x => x.Name == name, ct)) db.RolesSet.Add(new Role { Name = name });
        if (!await db.AttendancePoliciesSet.AnyAsync(x => x.Code == "DEFAULT", ct))
            db.AttendancePoliciesSet.Add(new AttendancePolicy { Code = "DEFAULT", Name = "Chính sách chuyên cần mặc định" });
        foreach (var (code, name, label, seconds) in new[] {
            ("DISTRACTION_LONG", "Mất tập trung kéo dài", "DISTRACTED", 15), ("SLEEP", "Ngủ gật", "SLEEPY", 10),
            ("PHONE", "Sử dụng điện thoại", "PHONE_USE", 5), ("OUT_OF_VIEW", "Rời vùng quan sát", "OUT_OF_VIEW", 20) })
            if (!await db.AlertRulesSet.AnyAsync(x => x.Code == code, ct)) db.AlertRulesSet.Add(new AlertRule {
                Code = code, Name = name, BehaviorLabel = label, MinDurationSeconds = seconds, MinConfidence = .7m, MinObservationQuality = label == "OUT_OF_VIEW" ? 0 : .55m });
        await db.SaveChangesAsync(ct);
    }
}
