using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Infrastructure.Data;

public static class LecturerCodeAllocator
{
    public const string InvalidDepartmentMessage="Bộ môn chưa có mã hợp lệ. Vui lòng chuẩn hóa mã bộ môn trước khi thêm giảng viên.";
    // API and both development seed paths hold HTBAM:academic-scheduling exclusively.
    public static async Task<string> Next(AppDbContext db,Department department,bool allocate,CancellationToken ct)
    {
        if(db.Database.IsRelational()&&db.Database.CurrentTransaction is null)throw new InvalidOperationException("Cấp mã giảng viên cần transaction có khóa nghiệp vụ.");
        if(!CatalogCodes.ValidDepartment(department.Code))throw new ArgumentException(InvalidDepartmentMessage);
        var prefix="GV-"+department.Code[3..]+"-";
        var counter=await db.LecturerCodeSequences.SingleOrDefaultAsync(x=>x.DepartmentId==department.Id,ct);
        // Reconcile only exact four-digit suffixes, including teachers who have moved department.
        var codes=await db.Teachers.Where(x=>x.TeacherCode.StartsWith(prefix)).Select(x=>x.TeacherCode).ToArrayAsync(ct);
        var last=codes.Select(x=>x[prefix.Length..]).Where(x=>x.Length==4&&x.All(char.IsAsciiDigit)).Select(int.Parse).DefaultIfEmpty(0).Max();
        var next=Math.Max(counter?.LastNumber??0,last)+1;
        if(next>9999)throw new InvalidOperationException("Bộ môn đã dùng hết 9.999 mã giảng viên; không thể cấp mã bốn chữ số mới.");
        if(allocate){if(counter is null){counter=new(){DepartmentId=department.Id};db.LecturerCodeSequences.Add(counter);}counter.LastNumber=next;counter.UpdatedAt=DateTime.UtcNow;}
        return prefix+next.ToString("D4",System.Globalization.CultureInfo.InvariantCulture);
    }
}
