using System.Text.RegularExpressions;

namespace HTBAM.Application.Services;

public static class ClassSectionCodes
{
    public static bool ValidCourseCode(string? code) => code is not null && Regex.IsMatch(code, "^[A-Z0-9][A-Z0-9-]{0,39}$");

    public static string Prefix(string year, int semester, string courseCode)
    {
        if (!Regex.IsMatch(year, "^[0-9]{4}-[0-9]{4}$") || int.Parse(year[..4]) + 1 != int.Parse(year[5..]) || semester is not (1 or 2))
            throw new ArgumentException("Năm học phải có dạng YYYY-YYYY, hai năm liên tiếp; học kỳ chỉ có HK1/HK2.");
        if (!ValidCourseCode(courseCode))
            throw new ArgumentException($"Mã môn '{courseCode}' không hợp lệ. Cần 1–40 ký tự A–Z, 0–9 hoặc dấu gạch ngang, viết hoa, không dấu và không khoảng trắng. Hãy chỉnh môn học trước khi tạo lớp.");
        return $"LHP-{year.Substring(2, 2)}{year.Substring(7, 2)}-HK{semester}-{courseCode}-";
    }
}
