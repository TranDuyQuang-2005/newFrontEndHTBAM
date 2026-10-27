using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HTBAM.Application.Services;

public static class CatalogCodes
{
    public const string InvalidDepartmentMessage = "Bộ môn chưa có mã hợp lệ. Vui lòng chuẩn hóa mã bộ môn trước khi tạo môn học.";
    public static bool ValidDepartment(string? code) => code is not null && code.Length <= 36 &&
        Regex.IsMatch(code, "^BM-[A-Z0-9]+(?:-[0-9]{2})?$") &&
        !Regex.IsMatch(code, "^BM-[A-F0-9]{32}(?:-[0-9]{2})?$");
    public static string DepartmentBase(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 256) throw new ArgumentException("Tên bộ môn cần 1–256 ký tự.");
        var plain = new string(name.Replace('đ','d').Replace('Đ','D').Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();
        var words = Regex.Matches(plain,"[A-Z0-9]+").Select(x=>x.Value).Where(x=>x is not ("VA" or "CUA" or "VE" or "CHO" or "CAC")).ToArray();
        var abbreviation = string.Concat(words.Select(x=>x[0]));
        if (abbreviation.Length is < 1 or > 29) throw new ArgumentException("Tên bộ môn cần tạo được mã viết tắt từ 1 đến 29 ký tự A–Z, 0–9.");
        return "BM-"+abbreviation;
    }
    public static string CoursePrefix(string departmentCode)
    {
        if (!ValidDepartment(departmentCode)) throw new ArgumentException(InvalidDepartmentMessage);
        return "MH-"+departmentCode[3..]+"-";
    }
}
