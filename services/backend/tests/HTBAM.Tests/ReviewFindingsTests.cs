using HTBAM.Api.Controllers;
using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static HTBAM.Tests.AcademicSchedulingTests;

namespace HTBAM.Tests;

public sealed class ReviewFindingsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(6, 0, true)]
    [InlineData(0, -28, false)]
    [InlineData(0, -7, false)]
    [InlineData(0, 7, false)]
    [InlineData(6, 7, false)]
    public async Task Term_create_update_and_week_one_endpoint_agree_on_confirmed_anchor(int startOffset, int anchorOffset, bool valid)
    {
        await using var db = await Fixture();
        var controller = With(new AcademicConfigurationController(db, new Audit()), 9, "ADMIN");
        var monday = new DateOnly(2031, 9, 1);
        var request = new AcademicTermRequest("2031-2032", 1, monday.AddDays(startOffset), monday.AddDays(27),
            true, "EQUAL_DAYS", true, monday.AddDays(anchorOffset), true, "Isolated test", []);
        var created = await controller.CreateTerm(request, Ct);
        var updated = await controller.UpdateTerm(301, request with { AcademicYear = "2032-2033" }, Ct);
        if (valid)
        {
            Assert.IsType<OkObjectResult>(created);
            Assert.IsType<OkObjectResult>(updated);
            Assert.IsType<OkObjectResult>(await controller.SetWeekOne(301, new(request.WeekOneMonday!.Value, true, "Test"), Ct));
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(created);
            Assert.IsType<BadRequestObjectResult>(updated);
            Assert.False(await db.AcademicTerms.AnyAsync(t => t.AcademicYear == "2031-2032"));
            var term = await db.AcademicTerms.SingleAsync(t => t.Id == 301);
            Assert.Equal("2030-2031", term.AcademicYear);
            term.StartsOn = request.StartsOn;
            await db.SaveChangesAsync();
            Assert.IsType<BadRequestObjectResult>(await controller.SetWeekOne(301, new(request.WeekOneMonday!.Value, true, "Test"), Ct));
        }
    }

    [Fact]
    public async Task Changing_start_date_cannot_leave_a_confirmed_anchor_in_another_week()
    {
        await using var db = await Fixture();
        var controller = With(new AcademicConfigurationController(db, new Audit()), 9, "ADMIN");
        var request = new AcademicTermRequest("2030-2031", 1, new(2030, 9, 9), new(2030, 9, 29),
            true, "EQUAL_DAYS", true, new(2030, 9, 2), true, "Test", []);
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateTerm(301, request, Ct));
        Assert.Equal(new DateOnly(2030, 9, 2), (await db.AcademicTerms.SingleAsync(t => t.Id == 301)).StartsOn);
        Assert.IsType<OkObjectResult>(await controller.UpdateTerm(301, request with { WeekMappingConfirmed = false }, Ct));
    }

    [Fact]
    public async Task Faculty_initialization_uses_existing_teacher_and_unlocks_normal_dean_workflows()
    {
        await using var db = await Fixture();
        var controller = With(new CatalogsController(db, new Audit()), 9, "ADMIN");
        Assert.IsType<BadRequestObjectResult>(await controller.AddFaculty(new(null, "Missing setup", null), Ct));
        Assert.Equal(2, await db.FacultiesSet.CountAsync());
        var request = new CatalogsController.FacultyReq(null, "New faculty", null, InitialDepartmentName: "First department", InitialHeadTeacherId: 102);
        Assert.IsType<ForbidResult>(await With(new CatalogsController(db, new Audit()), 1).AddFaculty(request, Ct));
        Assert.IsType<ForbidResult>(await With(new LookupsController(db), 1).Get("faculty-initial-heads", null, ct: Ct));
        var result = Assert.IsType<OkObjectResult>(await controller.AddFaculty(request, Ct));
        var faculty = Assert.IsType<Faculty>(result.Value);
        var department = await db.DepartmentsSet.SingleAsync(d => d.FacultyId == faculty.Id);
        Assert.Equal(department.Id, (await db.Teachers.SingleAsync(t => t.Id == 102)).DepartmentId);
        Assert.Equal(3, await db.Teachers.CountAsync());
        Assert.Equal(4, await db.UsersSet.CountAsync());
        var assignment = await db.ManagementAssignmentsSet.SingleAsync(a => a.FacultyId == faculty.Id);
        Assert.Equal(102, assignment.TeacherId);
        Assert.True(assignment.IsActive);
        var dean = With(new CatalogsController(db, new Audit()), 2);
        Assert.IsType<OkObjectResult>(await dean.AddDepartment(new(null, "Next department", faculty.Id, null), Ct));
        Assert.IsType<OkObjectResult>(await dean.AddTeacher(new(null, "New colleague", "", null, department.Id, null), Ct));
        Assert.IsType<ForbidResult>(await controller.AddTeacher(new(null, "Forbidden", "", null, department.Id, null), Ct));
        Assert.IsType<ForbidResult>(await With(new CatalogsController(db, new Audit()), 1).AddDepartment(new(null, "Outside scope", faculty.Id, null), Ct));
        Assert.IsType<BadRequestObjectResult>(await controller.AddFaculty(request with { Name = "Second faculty" }, Ct));
        Assert.Equal(3, await db.FacultiesSet.CountAsync());
    }

    [Theory]
    [InlineData("inactive-teacher")]
    [InlineData("inactive-account")]
    [InlineData("admin-account")]
    [InlineData("no-account")]
    [InlineData("class")]
    [InlineData("session-history")]
    [InlineData("substitution-history")]
    [InlineData("appointment")]
    [InlineData("future-appointment")]
    public async Task Faculty_initialization_rechecks_eligibility_and_keeps_existing_data(string reason)
    {
        await using var db = await Fixture();
        Assert.Contains(await FacultyInitialization.EligibleTeachers(db).ToArrayAsync(), t => t.Id == 102);
        var teacher = await db.Teachers.SingleAsync(t => t.Id == 102);
        switch (reason)
        {
            case "inactive-teacher": teacher.IsActive = false; break;
            case "inactive-account": (await db.UsersSet.SingleAsync(u => u.Id == 2)).Status = "LOCKED"; break;
            case "admin-account": db.UserRoles.Add(new() { UserId = 2, RoleId = 2 }); break;
            case "no-account": teacher.UserId = null; break;
            case "class": db.ClassSections.Add(new() { TeacherId = 102, CourseId = 201, Code = "OLD", Name = "Existing class" }); break;
            case "session-history": db.SessionsSet.Add(new() { OriginalTeacherId = 102 }); break;
            case "substitution-history": db.SessionSubstitutionsSet.Add(new() { SubstituteTeacherId = 102 }); break;
            default: db.ManagementAssignmentsSet.Add(new() { TeacherId = 102, PositionType = "DEPARTMENT_HEAD", DepartmentId = 11,
                EffectiveFrom = reason == "future-appointment" ? DateTime.UtcNow.AddDays(1) : DateTime.UtcNow.AddDays(-1) }); break;
        }
        await db.SaveChangesAsync();
        var controller = With(new CatalogsController(db, new Audit()), 9, "ADMIN");
        Assert.IsType<BadRequestObjectResult>(await controller.AddFaculty(new(null, "Blocked faculty", null, InitialDepartmentName: "First department", InitialHeadTeacherId: 102), Ct));
        Assert.Equal(2, await db.FacultiesSet.CountAsync());
        Assert.Equal(2, await db.DepartmentsSet.CountAsync());
        Assert.Equal(11, (await db.Teachers.SingleAsync(t => t.Id == 102)).DepartmentId);
        Assert.DoesNotContain(await FacultyInitialization.EligibleTeachers(db).ToArrayAsync(), t => t.Id == 102);
    }

    [SqlServerFact]
    public async Task Faculty_initialization_is_atomic_and_concurrent_requests_cannot_move_the_same_head_twice()
    {
        await using var database = await AcademicSqlServerTests.IsolatedDatabase.Create();
        long adminId, headId;
        await using (var db = database.Context())
        {
            var admin = new User { UserName = "init-admin", FullName = "Admin" };
            var lecturer = new User { UserName = "init-head", FullName = "Head" };
            db.UsersSet.AddRange(admin, lecturer);
            await db.SaveChangesAsync();
            adminId = admin.Id;
            db.UserRoles.AddRange(new UserRole { UserId = admin.Id, RoleId = await db.RolesSet.Where(r => r.Name == "ADMIN").Select(r => r.Id).SingleAsync() },
                new UserRole { UserId = lecturer.Id, RoleId = await db.RolesSet.Where(r => r.Name == "LECTURER").Select(r => r.Id).SingleAsync() });
            var teacher = new Teacher { TeacherCode = "INIT-HEAD", FullName = "Head", UserId = lecturer.Id };
            db.Teachers.Add(teacher);
            await db.SaveChangesAsync();
            headId = teacher.Id;
        }
        var request = new CatalogsController.FacultyReq(null, "Initialized faculty", null, InitialDepartmentName: "First department", InitialHeadTeacherId: headId);
        await using (var db = database.Context())
        {
            var controller = With(new CatalogsController(db, new FailingAudit()), adminId, "ADMIN");
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.AddFaculty(request, Ct));
        }
        await using (var db = database.Context())
        {
            Assert.False(await db.FacultiesSet.AnyAsync(f => f.Name == request.Name));
            Assert.False(await db.DepartmentsSet.AnyAsync(d => d.Name == request.InitialDepartmentName));
            Assert.Null((await db.Teachers.SingleAsync(t => t.Id == headId)).DepartmentId);
            var lookup = With(new LookupsController(db), adminId, "ADMIN");
            Assert.IsType<OkObjectResult>(await lookup.Get("faculty-initial-heads", "Head", ct: Ct));
        }
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var db = database.Context();
            return await With(new CatalogsController(db, new Audit()), adminId, "ADMIN").AddFaculty(request, Ct);
        }));
        Assert.Single(responses.OfType<OkObjectResult>());
        Assert.Single(responses.OfType<BadRequestObjectResult>());
        await using var verify = database.Context();
        var facultyId = await verify.FacultiesSet.Where(f => f.Name == request.Name).Select(f => f.Id).SingleAsync();
        var departmentId = await verify.DepartmentsSet.Where(d => d.FacultyId == facultyId).Select(d => d.Id).SingleAsync();
        Assert.Equal(departmentId, (await verify.Teachers.SingleAsync(t => t.Id == headId)).DepartmentId);
        Assert.Equal(1, await verify.ManagementAssignmentsSet.CountAsync(a => a.FacultyId == facultyId && a.IsActive));
        Assert.Equal(1, await verify.Teachers.CountAsync());
    }

    private sealed class FailingAudit : IAuditService
    {
        public Task WriteAsync(long? userId, string action, string entityType, string entityId, object? metadata = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated audit failure");
    }
}
