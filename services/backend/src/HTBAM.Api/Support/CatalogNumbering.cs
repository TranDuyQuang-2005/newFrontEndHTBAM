using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Support;

public static class CatalogNumbering
{
    // Callers, including seed and faculty initialization, hold the same transaction application lock.
    public const string Resource = "HTBAM:academic-scheduling";
    private static void RequireTransaction(AppDbContext db)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null) throw new InvalidOperationException("Cấp mã danh mục cần transaction có khóa nghiệp vụ.");
    }
    public static async Task<string> Department(AppDbContext db, string name, CancellationToken ct)
    {
        RequireTransaction(db);
        var root = CatalogCodes.DepartmentBase(name);
        var existing = (await db.DepartmentsSet.Where(x=>x.Code.StartsWith(root)).Select(x=>x.Code).ToArrayAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(root)) return root;
        for (var n=2;n<=99;n++) { var code=$"{root}-{n:00}"; if (!existing.Contains(code)) return code; }
        throw new InvalidOperationException("Đã dùng hết 99 mã cho tên viết tắt bộ môn này.");
    }
    public static async Task<string> Course(AppDbContext db, Department department, bool allocate, CancellationToken ct)
    {
        RequireTransaction(db);
        var prefix = CatalogCodes.CoursePrefix(department.Code);
        var counter = await db.CourseCodeCounters.SingleOrDefaultAsync(x=>x.DepartmentId==department.Id,ct);
        // Bootstrap/reconcile only under the protected transaction. Existing business codes never change.
        var codes = await db.Courses.Where(x=>x.Code.StartsWith(prefix)).Select(x=>x.Code).ToArrayAsync(ct);
        var last = codes.Select(x=>x[prefix.Length..]).Where(x=>x.Length==3&&x.All(char.IsAsciiDigit)).Select(int.Parse).DefaultIfEmpty(0).Max();
        var next=Math.Max(last,counter?.LastNumber??0)+1;
        if(next>999)throw new InvalidOperationException("Bộ môn đã dùng hết 999 mã môn học; không thể cấp mã ba chữ số mới.");
        if(allocate){if(counter is null){counter=new(){DepartmentId=department.Id};db.CourseCodeCounters.Add(counter);}counter.LastNumber=next;}
        return prefix+next.ToString("D3",System.Globalization.CultureInfo.InvariantCulture);
    }
}
