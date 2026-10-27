using HTBAM.Api.Controllers;
using HTBAM.Api.Support;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static HTBAM.Tests.AcademicSchedulingTests;

namespace HTBAM.Tests;

public sealed class LecturerCodeTests
{
    private static readonly CancellationToken Ct=CancellationToken.None;
    [Fact]
    public async Task Codes_are_per_department_and_survive_profile_edits_appointments_revocations_and_transfer()
    {
        await using var db=await Fixture();(await db.DepartmentsSet.FindAsync(11L))!.Code="BM-CNPM";
        db.UsersSet.Add(new(){Id=4,UserName="new-lecturer",FullName="New lecturer",Status="ACTIVE"});db.UserRoles.Add(new(){UserId=4,RoleId=1});await db.SaveChangesAsync();
        var api=With(new CatalogsController(db,new Audit()),1);
        var attt=Assert.IsType<Department>(Assert.IsType<OkObjectResult>(await api.AddDepartment(new(null,"An toàn thông tin",1,null),Ct)).Value);
        Assert.IsType<OkObjectResult>(await api.NextTeacherCode(11,Ct));Assert.Empty(await db.LecturerCodeSequences.ToArrayAsync());
        var one=Assert.IsType<Teacher>(Assert.IsType<OkObjectResult>(await api.AddTeacher(new("FORGED-GUID","First","first@example.invalid",4,11,null),Ct)).Value);
        var two=Assert.IsType<Teacher>(Assert.IsType<OkObjectResult>(await api.AddTeacher(new("GV-DEAN","Second","second@example.invalid",null,11,null),Ct)).Value);
        var third=Assert.IsType<Teacher>(Assert.IsType<OkObjectResult>(await api.AddTeacher(new(null,"Third","third@example.invalid",null,attt.Id,null),Ct)).Value);
        Assert.Equal("GV-CNPM-0001",one.TeacherCode);Assert.Equal("GV-CNPM-0002",two.TeacherCode);Assert.Equal("GV-ATTT-0001",third.TeacherCode);
        Assert.IsType<NoContentResult>(await api.UpdateTeacher(one.Id,new("FORGED","Renamed","renamed@example.invalid",4,11,null),Ct));
        var assignments=With(new ManagementAssignmentsController(db,new Audit()),1);var admin=With(new ManagementAssignmentsController(db,new Audit()),9,"ADMIN");
        Assert.IsType<OkObjectResult>(await assignments.AssignDepartmentHead(new(one.Id,11,null),Ct));Assert.Equal(one.TeacherCode,(await db.Teachers.FindAsync(one.Id))!.TeacherCode);
        var deptAssignment=await db.ManagementAssignmentsSet.SingleAsync(x=>x.TeacherId==one.Id&&x.PositionType=="DEPARTMENT_HEAD"&&x.IsActive);
        Assert.IsType<NoContentResult>(await assignments.Revoke(deptAssignment.Id,Ct));Assert.Equal("GV-CNPM-0001",(await db.Teachers.FindAsync(one.Id))!.TeacherCode);
        Assert.IsType<OkObjectResult>(await admin.TransferFacultyHead(new(one.Id,1,null,true),Ct));Assert.Equal("GV-CNPM-0001",(await db.Teachers.FindAsync(one.Id))!.TeacherCode);
        var facultyAssignment=await db.ManagementAssignmentsSet.SingleAsync(x=>x.TeacherId==one.Id&&x.PositionType=="FACULTY_HEAD"&&x.IsActive);
        Assert.IsType<NoContentResult>(await admin.Revoke(facultyAssignment.Id,Ct));
        Assert.IsType<OkObjectResult>(await admin.TransferFacultyHead(new(101,1,null),Ct));
        Assert.IsType<NoContentResult>(await api.UpdateTeacher(one.Id,new(null,"Moved","moved@example.invalid",4,attt.Id,null),Ct));
        var moved=await db.Teachers.FindAsync(one.Id);Assert.Equal(attt.Id,moved!.DepartmentId);Assert.Equal("GV-CNPM-0001",moved.TeacherCode);
        var next=Assert.IsType<Teacher>(Assert.IsType<OkObjectResult>(await api.AddTeacher(new(null,"Next","next@example.invalid",null,11,null),Ct)).Value);Assert.Equal("GV-CNPM-0003",next.TeacherCode);
        Assert.Equal("GV-CNPM-0002",(await db.Teachers.FindAsync(two.Id))!.TeacherCode);
    }
    [Theory]
    [InlineData("CNPM")][InlineData("bm0001")][InlineData("BM-8f6c523117f842b4a5e40eff155feafc")][InlineData("BM-8F6C523117F842B4A5E40EFF155FEAFC")]
    public async Task Legacy_department_is_blocked_and_never_gets_a_temporary_teacher_code(string code)
    {
        await using var db=await Fixture();(await db.DepartmentsSet.FindAsync(11L))!.Code=code;await db.SaveChangesAsync();var count=await db.Teachers.CountAsync();
        var error=await Assert.ThrowsAsync<ArgumentException>(()=>With(new CatalogsController(db,new Audit()),1).AddTeacher(new("MANUAL","Blocked","x@example.invalid",null,11,null),Ct));
        Assert.Equal(LecturerCodeAllocator.InvalidDepartmentMessage,error.Message);Assert.Equal(count,await db.Teachers.CountAsync());Assert.Empty(await db.LecturerCodeSequences.ToArrayAsync());
    }
    [Fact]
    public async Task Admin_ordinary_lecturer_department_head_and_outside_faculty_cannot_create()
    {
        await using var db=await Fixture();(await db.DepartmentsSet.FindAsync(11L))!.Code="BM-CNPM";db.ManagementAssignmentsSet.Add(new(){TeacherId=102,DepartmentId=11,PositionType="DEPARTMENT_HEAD",EffectiveFrom=DateTime.UtcNow.AddDays(-1)});await db.SaveChangesAsync();
        foreach(var (id,role) in new[]{(9L,"ADMIN"),(2L,"LECTURER"),(3L,"LECTURER")})Assert.IsType<ForbidResult>(await With(new CatalogsController(db,new Audit()),id,role).AddTeacher(new(null,"Forbidden","x@example.invalid",null,11,null),Ct));
    }
    [SqlServerFact]
    public async Task Concurrent_sql_allocation_unique_index_rollback_and_seed_replay_keep_old_codes()
    {
        await using var database=await AcademicSqlServerTests.IsolatedDatabase.Create();long userId,departmentId;Dictionary<long,string> oldCodes;
        await using(var db=database.Context()){
            db.DevelopmentDataEnabled=true;await DemoDataSeeder.SeedAsync(db);
            Assert.All(await db.Teachers.Where(x=>db.UsersSet.Any(u=>u.Id==x.UserId&&u.UserName.StartsWith("demo."))).ToArrayAsync(),x=>Assert.Matches("^GV-[A-Z0-9]+(?:-[0-9]{2})?-[0-9]{4}$",x.TeacherCode));
            userId=await db.UsersSet.Where(x=>x.UserName=="demo.dean").Select(x=>x.Id).SingleAsync();var facultyId=(await db.CatalogSeedIdentities.SingleAsync(x=>x.Key=="demo.faculty")).FacultyId!.Value;
            departmentId=Assert.IsType<Department>(Assert.IsType<OkObjectResult>(await With(new CatalogsController(db,new Audit()),userId).AddDepartment(new(null,"Trí tuệ nhân tạo",facultyId,null),Ct)).Value).Id;
            oldCodes=await db.Teachers.ToDictionaryAsync(x=>x.Id,x=>x.TeacherCode);
        }
        var rows=await Task.WhenAll(Enumerable.Range(0,8).Select(async i=>{await using var db=database.Context();return Assert.IsType<Teacher>(Assert.IsType<OkObjectResult>(await With(new CatalogsController(db,new Audit()),userId).AddTeacher(new("FORGED",$"Concurrent {i}",$"concurrent{i}@example.invalid",null,departmentId,null),Ct)).Value);}));
        Assert.Equal(8,rows.Select(x=>x.TeacherCode).Distinct().Count());Assert.Contains(rows,x=>x.TeacherCode=="GV-TTNT-0001");Assert.Contains(rows,x=>x.TeacherCode=="GV-TTNT-0008");
        await using(var db=database.Context())await Assert.ThrowsAsync<InvalidOperationException>(()=>With(new CatalogsController(db,new FailingAudit()),userId).AddTeacher(new(null,"Rollback","rollback@example.invalid",null,departmentId,null),Ct));
        await using(var db=database.Context()){
            var sequence=await db.LecturerCodeSequences.FindAsync(departmentId);Assert.Equal(8,sequence!.LastNumber);Assert.True(sequence.UpdatedAt>DateTime.UtcNow.AddMinutes(-5));Assert.False(await db.Teachers.AnyAsync(x=>x.FullName=="Rollback"));
            db.Teachers.Add(new(){TeacherCode=rows[0].TeacherCode,FullName="Duplicate",DepartmentId=departmentId});await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());
        }
        await using(var db=database.Context()){
            db.DevelopmentDataEnabled=true;var count=await db.Teachers.CountAsync();await DemoDataSeeder.SeedAsync(db);await DemoDataSeeder.SeedAsync(db);Assert.Equal(count,await db.Teachers.CountAsync());
            foreach(var pair in oldCodes)Assert.Equal(pair.Value,(await db.Teachers.FindAsync(pair.Key))!.TeacherCode);
        }
        await database.Apply("013_lecturer_business_codes.sql");await database.Apply("013_lecturer_business_codes.sql");
    }
    private sealed class FailingAudit:HTBAM.Application.Interfaces.IAuditService{public Task WriteAsync(long? userId,string action,string entityType,string entityId,object? metadata=null,CancellationToken ct=default)=>throw new InvalidOperationException("Audit rollback test");}
}
