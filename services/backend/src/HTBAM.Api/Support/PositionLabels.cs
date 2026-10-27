using HTBAM.Domain.Entities;

namespace HTBAM.Api.Support;

public static class PositionLabels
{
    public static string For(IEnumerable<ManagementAssignment> assignments, IEnumerable<string> roles)
    {
        var current = assignments.Where(x => x.IsActive && x.EffectiveFrom <= DateTime.UtcNow && (x.EffectiveTo == null || x.EffectiveTo > DateTime.UtcNow)).ToArray();
        if (roles.Contains("ADMIN")) return "Quản trị viên";
        if (current.Any(x => x.PositionType == "FACULTY_HEAD")) return "Trưởng khoa";
        if (current.Any(x => x.PositionType == "DEPARTMENT_HEAD")) return "Trưởng bộ môn";
        if (roles.Contains("LECTURER")) return "Giảng viên";
        if (roles.Contains("TECH_AI")) return "Kỹ thuật AI";
        return "Người dùng";
    }
}
