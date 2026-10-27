using HTBAM.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Infrastructure.Data;

public static class DevelopmentSeeder
{
    public static Task SeedAsync(AppDbContext db,CancellationToken ct=default)=>db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>
    {
        db.ChangeTracker.Clear();
        await using var tx=db.Database.IsRelational()?await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct):null;
        if(db.Database.IsSqlServer())await db.Database.ExecuteSqlRawAsync("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'HTBAM:academic-scheduling',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000; IF @r<0 THROW 50001,'Catalog seed lock timed out',1;",ct);
        await SeedCore(db,ct);if(tx is not null)await tx.CommitAsync(ct);
    });
    private static async Task SeedCore(AppDbContext db, CancellationToken ct)
    {
        if (!await db.Database.CanConnectAsync(ct)) return;

        foreach (var name in new[] { "ADMIN", "LECTURER", "TECH_AI" })
        {
            if (!await db.RolesSet.AnyAsync(x => x.Name == name, ct))
            {
                db.RolesSet.Add(new Role { Name = name });
            }
        }

        await db.SaveChangesAsync(ct);

        var admin = await EnsureUser(
            db, "admin", "admin@htbam.local", "Quản trị HTBAM", "Admin@123456", "ADMIN", ct);
        var lecturer = await EnsureUser(
            db, "lecturer", "lecturer@htbam.local", "Giảng viên Demo", "Lecturer@123456", "LECTURER", ct);
        var dean = await EnsureUser(
            db, "dean", "dean@example.local", "Trưởng khoa Demo", "Dean@123456", "LECTURER", ct);
        var departmentHead = await EnsureUser(
            db, "depthead", "depthead@example.local", "Trưởng bộ môn Demo", "DeptHead@123456", "LECTURER", ct);
        var substituteLecturer = await EnsureUser(
            db, "lecturer2", "lecturer2@example.local", "Giảng viên dạy thay", "Lecturer2@123456", "LECTURER", ct);

        await EnsureRule(db, "DISTRACTION_LONG", "Mất tập trung kéo dài", "DISTRACTED", 15, 0.70m, 0.55m, ct);
        await EnsureRule(db, "SLEEP", "Ngủ gật", "SLEEPY", 10, 0.75m, 0.55m, ct);
        await EnsureRule(db, "PHONE", "Sử dụng điện thoại", "PHONE_USE", 5, 0.70m, 0.55m, ct);
        await EnsureRule(db, "OUT_OF_VIEW", "Rời vùng quan sát", "OUT_OF_VIEW", 20, 0.80m, 0.00m, ct);

        if (!await db.AttendancePoliciesSet.AnyAsync(x => x.Code == "DEFAULT", ct))
        {
            db.AttendancePoliciesSet.Add(new AttendancePolicy
            {
                Code = "DEFAULT",
                Name = "Chính sách chuyên cần mặc định",
                PresentThreshold = .80m,
                PartialThreshold = .50m,
                PresentScore = 10,
                PartialScore = 5,
                AbsentScore = 0,
                Version = "v1",
                IsActive = true,
            });
        }

        await db.SaveChangesAsync(ct);

        var faculty = await db.FacultiesSet
            .OrderByDescending(x => x.Code == "CNTT")
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.IsActive, ct);
        if (faculty is null) return;

        var department = await db.DepartmentsSet
            .Where(x => x.FacultyId == faculty.Id && x.IsActive)
            .OrderByDescending(x => x.Code == "HTTT")
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (department is null) return;

        _ = await EnsureTeacher(
            db, "GV001", "Giảng viên Demo", lecturer.Email, lecturer, department.Id, ct);
        var deanTeacher = await EnsureTeacher(
            db, "GV-DEAN", "Trưởng khoa Demo", dean.Email, dean, department.Id, ct);
        var departmentHeadTeacher = await EnsureTeacher(
            db, "GV-HEAD", "Trưởng bộ môn Demo", departmentHead.Email, departmentHead, department.Id, ct);
        _ = await EnsureTeacher(
            db, "GV002", "Giảng viên dạy thay", substituteLecturer.Email, substituteLecturer, department.Id, ct);

        await EnsureCurrentAssignment(
            db, deanTeacher.Id, "FACULTY_HEAD", faculty.Id, null, admin.Id,
            "Dữ liệu mẫu trưởng khoa", ct);
        await EnsureCurrentAssignment(
            db, departmentHeadTeacher.Id, "DEPARTMENT_HEAD", null, department.Id, admin.Id,
            "Dữ liệu mẫu trưởng bộ môn", ct);
    }

    private static async Task<User> EnsureUser(
        AppDbContext db,
        string userName,
        string email,
        string fullName,
        string password,
        string roleName,
        CancellationToken ct)
    {
        var user = await db.UsersSet
            .Include(x => x.UserRoles)
            .FirstOrDefaultAsync(x => x.UserName == userName, ct);
        if (user is null)
        {
            user = new User
            {
                UserName = userName,
                Email = email,
                FullName = fullName,
                Status = "ACTIVE",
            };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, Environment.GetEnvironmentVariable("SEED_DEFAULT_PASSWORD") ?? "HTBAM-Local-Demo!2026#");
            db.UsersSet.Add(user);
            await db.SaveChangesAsync(ct);
        }
        else if (user.PasswordHash == "DEV-SAMPLE-NOT-A-LOGIN-HASH")
        {
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
            user.TokenVersion++;
            await db.SaveChangesAsync(ct);
        }

        var role = await db.RolesSet.SingleAsync(x => x.Name == roleName, ct);
        if (!await db.UserRoles.AnyAsync(x => x.UserId == user.Id && x.RoleId == role.Id, ct))
        {
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync(ct);
        }

        return user;
    }

    private static async Task<Teacher> EnsureTeacher(
        AppDbContext db,
        string teacherCode,
        string fullName,
        string email,
        User user,
        long departmentId,
        CancellationToken ct)
    {
        var teacher = await db.Teachers.FirstOrDefaultAsync(x => x.UserId == user.Id, ct)
            ?? await db.Teachers.FirstOrDefaultAsync(x => x.TeacherCode == teacherCode, ct)
            ?? await db.Teachers.FirstOrDefaultAsync(x => x.Email == email, ct);

        if (teacher is null)
        {
            teacher = new Teacher
            {
                TeacherCode = await LecturerCodeAllocator.Next(db,await db.DepartmentsSet.SingleAsync(x=>x.Id==departmentId,ct),true,ct),
                FullName = fullName,
                Email = email,
                UserId = user.Id,
                DepartmentId = departmentId,
                IsActive = true,
            };
            db.Teachers.Add(teacher);
        }
        else
        {
            if (teacher.UserId is not null && teacher.UserId != user.Id)
            {
                throw new InvalidOperationException(
                    $"Teacher {teacher.TeacherCode} đã liên kết với một tài khoản khác.");
            }

            if (teacher.UserId is null)
            {
                teacher.UserId = user.Id;
                teacher.FullName = fullName;
                teacher.Email = email;
                teacher.DepartmentId ??= departmentId;
            }
        }

        await db.SaveChangesAsync(ct);
        return teacher;
    }

    private static async Task EnsureCurrentAssignment(
        AppDbContext db,
        long teacherId,
        string positionType,
        long? facultyId,
        long? departmentId,
        long assignedByUserId,
        string note,
        CancellationToken ct)
    {
        // Seed an empty slot once. A user transfer or revocation must survive restarts.
        var current = await db.ManagementAssignmentsSet.FirstOrDefaultAsync(x =>
            x.PositionType == positionType &&
            x.FacultyId == facultyId &&
            x.DepartmentId == departmentId, ct);

        if (current is not null) return;

        var desired = await db.ManagementAssignmentsSet
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(x =>
                x.TeacherId == teacherId &&
                x.PositionType == positionType &&
                x.FacultyId == facultyId &&
                x.DepartmentId == departmentId, ct);

        if (desired is null)
        {
            db.ManagementAssignmentsSet.Add(new ManagementAssignment
            {
                TeacherId = teacherId,
                PositionType = positionType,
                FacultyId = facultyId,
                DepartmentId = departmentId,
                EffectiveFrom = DateTime.UtcNow,
                IsActive = true,
                AssignedByUserId = assignedByUserId,
                Note = note,
            });
        }
        else
        {
            desired.IsActive = true;
            desired.EffectiveFrom = DateTime.UtcNow;
            desired.EffectiveTo = null;
            desired.AssignedByUserId ??= assignedByUserId;
            desired.Note ??= note;
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureRule(
        AppDbContext db,
        string code,
        string name,
        string label,
        int duration,
        decimal confidence,
        decimal quality,
        CancellationToken ct)
    {
        if (!await db.AlertRulesSet.AnyAsync(x => x.Code == code, ct))
        {
            db.AlertRulesSet.Add(new AlertRule
            {
                Code = code,
                Name = name,
                BehaviorLabel = label,
                MinDurationSeconds = duration,
                MinConfidence = confidence,
                MinObservationQuality = quality,
                Enabled = true,
                Version = "v1",
            });
        }
    }
}
