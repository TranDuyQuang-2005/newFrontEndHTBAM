using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Support;

public static class ClassSectionNumbering
{
    // All callers hold the academic-scheduling transaction/application lock, including seed/import.
    public static async Task<string> Next(AppDbContext db, AcademicTerm term, long courseId, CancellationToken ct)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Cấp mã lớp cần transaction có khóa nghiệp vụ.");
        var code = await db.Courses.Where(x => x.Id == courseId).Select(x => x.Code).SingleAsync(ct);
        var prefix = ClassSectionCodes.Prefix(term.AcademicYear, term.Semester, code);
        var counter = await db.ClassSectionCounters.SingleOrDefaultAsync(x => x.AcademicTermId == term.Id && x.CourseId == courseId, ct);
        if (counter is null)
        {
            // Bootstrap only within the protected transaction; never change any existing code.
            var existing = await db.ClassSections.Where(x => x.Code.StartsWith(prefix)).Select(x => x.Code).ToListAsync(ct);
            var last = existing.Select(x => x[prefix.Length..]).Where(x => x.Length == 2 && int.TryParse(x, out _)).Select(int.Parse).DefaultIfEmpty(0).Max();
            counter = new() { AcademicTermId = term.Id, CourseId = courseId, LastNumber = last };
            db.ClassSectionCounters.Add(counter);
        }
        if (counter.LastNumber >= 99) throw new InvalidOperationException("Đã dùng hết 99 nhóm cho môn và học kỳ này. Không thể cấp mã hai chữ số mới.");
        counter.LastNumber++;
        return prefix + counter.LastNumber.ToString("D2");
    }
}
