using System.Text.RegularExpressions;

namespace HTBAM.Application.Services;

public static class CourseStartPeriod
{
    public const string YearError = "Năm học phải có dạng YYYY-YYYY và hai năm phải liên tiếp.";
    public const string SemesterError = "Học kỳ chỉ nhận HK1 hoặc HK2.";

    public static (int StartYear, int EndYear, string Semester) Parse(string? year, string? semester)
    {
        var normalized = Regex.Replace(year?.Trim() ?? "", @"\s*[-‐‑‒–—−]\s*", "-");
        if (!Regex.IsMatch(normalized, @"^[0-9]{4}-[0-9]{4}$") ||
            !int.TryParse(normalized[..4], out var start) || start < 1000 || start >= 9999 ||
            !int.TryParse(normalized[5..], out var end) || end != start + 1)
            throw new ArgumentException(YearError);
        var term = semester?.Trim().ToUpperInvariant();
        term = term is "1" or "2" ? "HK" + term : term;
        if (term is not ("HK1" or "HK2")) throw new ArgumentException(SemesterError);
        return (start, end, term);
    }
}
