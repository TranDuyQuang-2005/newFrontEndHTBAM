using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HTBAM.Tests;

public sealed class DevelopmentSeederTests
{
    [Fact]
    public async Task Restart_does_not_restore_revoked_management_position_or_overwrite_existing_teacher_profile()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var faculty = new Faculty { Code = "F", Name = "Faculty" }; db.FacultiesSet.Add(faculty); await db.SaveChangesAsync();
        db.DepartmentsSet.Add(new Department { Code = "BM-D", Name = "Department", FacultyId = faculty.Id }); await db.SaveChangesAsync();
        await DevelopmentSeeder.SeedAsync(db);
        var assignment = await db.ManagementAssignmentsSet.SingleAsync(x => x.PositionType == "FACULTY_HEAD");
        assignment.IsActive = false; assignment.EffectiveTo = DateTime.UtcNow;
        var teacher = await db.Teachers.SingleAsync(x => x.Id == assignment.TeacherId);
        teacher.FullName = "Updated profile"; teacher.IsActive = false; await db.SaveChangesAsync();
        await DevelopmentSeeder.SeedAsync(db);
        Assert.False((await db.ManagementAssignmentsSet.SingleAsync(x => x.Id == assignment.Id)).IsActive);
        Assert.Equal("Updated profile", (await db.Teachers.SingleAsync(x => x.Id == teacher.Id)).FullName);
        Assert.False((await db.Teachers.SingleAsync(x => x.Id == teacher.Id)).IsActive);
    }

    [Fact]
    public async Task SeedAsync_RepairsDemoProfiles_AndRemainsIdempotent()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"development-seeder-{Guid.NewGuid()}")
            .Options;
        await using var db = new AppDbContext(options);

        var faculty = new Faculty { Code = "CNTT", Name = "Khoa Công nghệ thông tin" };
        db.FacultiesSet.Add(faculty);
        await db.SaveChangesAsync();
        var department = new Department
        {
            Code = "HTTT",
            Name = "Bộ môn Hệ thống thông tin",
            FacultyId = faculty.Id,
        };
        db.DepartmentsSet.Add(department);
        db.Teachers.AddRange(
            new Teacher { TeacherCode = "GV001", FullName = "Giảng viên Demo", Email = "lecturer@example.local", DepartmentId = department.Id },
            new Teacher { TeacherCode = "GV-DEAN", FullName = "Trưởng khoa Demo", Email = "dean@example.local", DepartmentId = department.Id },
            new Teacher { TeacherCode = "GV-HEAD", FullName = "Trưởng bộ môn Demo", Email = "depthead@example.local", DepartmentId = department.Id },
            new Teacher { TeacherCode = "GV002", FullName = "Giảng viên dạy thay", Email = "lecturer2@example.local", DepartmentId = department.Id });
        await db.SaveChangesAsync();

        await DevelopmentSeeder.SeedAsync(db);
        var firstCounts = new
        {
            Users = await db.UsersSet.CountAsync(),
            Teachers = await db.Teachers.CountAsync(),
            ActiveAssignments = await db.ManagementAssignmentsSet.CountAsync(x => x.IsActive),
        };

        await DevelopmentSeeder.SeedAsync(db);
        var secondCounts = new
        {
            Users = await db.UsersSet.CountAsync(),
            Teachers = await db.Teachers.CountAsync(),
            ActiveAssignments = await db.ManagementAssignmentsSet.CountAsync(x => x.IsActive),
        };

        Assert.Equal(firstCounts, secondCounts);
        Assert.Equal(5, secondCounts.Users);
        Assert.Equal(4, secondCounts.Teachers);
        Assert.Equal(2, secondCounts.ActiveAssignments);

        var dean = await db.UsersSet.SingleAsync(x => x.UserName == "dean");
        var departmentHead = await db.UsersSet.SingleAsync(x => x.UserName == "depthead");
        var lecturer = await db.UsersSet.SingleAsync(x => x.UserName == "lecturer");
        var substitute = await db.UsersSet.SingleAsync(x => x.UserName == "lecturer2");

        Assert.Equal(dean.Id, (await db.Teachers.SingleAsync(x => x.TeacherCode == "GV-DEAN")).UserId);
        Assert.Equal(departmentHead.Id, (await db.Teachers.SingleAsync(x => x.TeacherCode == "GV-HEAD")).UserId);
        Assert.Equal(lecturer.Id, (await db.Teachers.SingleAsync(x => x.TeacherCode == "GV001")).UserId);
        Assert.Equal(substitute.Id, (await db.Teachers.SingleAsync(x => x.TeacherCode == "GV002")).UserId);

        Assert.Single(await db.ManagementAssignmentsSet.Where(x =>
            x.IsActive && x.PositionType == "FACULTY_HEAD" && x.FacultyId == faculty.Id).ToListAsync());
        Assert.Single(await db.ManagementAssignmentsSet.Where(x =>
            x.IsActive && x.PositionType == "DEPARTMENT_HEAD" && x.DepartmentId == department.Id).ToListAsync());
    }
}
