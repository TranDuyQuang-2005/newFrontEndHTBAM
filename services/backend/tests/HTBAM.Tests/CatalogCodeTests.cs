using HTBAM.Api.Controllers;
using HTBAM.Api.Support;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static HTBAM.Tests.AcademicSchedulingTests;

namespace HTBAM.Tests;

public sealed class CatalogCodeTests
{
    private static readonly CancellationToken Ct=CancellationToken.None;
    [Theory]
    [InlineData("Công nghệ dữ liệu","BM-CNDL")][InlineData("Công nghệ phần mềm","BM-CNPM")]
    [InlineData("An toàn thông tin","BM-ATTT")][InlineData("Hệ thống thông tin","BM-HTTT")]
    [InlineData("Khoa học dữ liệu","BM-KHDL")][InlineData("Dữ liệu và trí tuệ","BM-DLTT")]
    public void Department_abbreviation_is_uppercase_unaccented_and_ignores_connectors(string name,string expected)=>Assert.Equal(expected,CatalogCodes.DepartmentBase(name));

    [Fact]
    public async Task Create_ignores_client_codes_sequences_by_department_and_updates_keep_business_codes()
    {
        await using var db=await Fixture();var api=With(new CatalogsController(db,new Audit()),1);
        var facultyId=(await db.DepartmentsSet.FindAsync(11L))!.FacultyId;
        var department=Assert.IsType<Department>(Assert.IsType<OkObjectResult>(await api.AddDepartment(new("bm0001","Công nghệ dữ liệu",facultyId,null),Ct)).Value);
        var second=Assert.IsType<Department>(Assert.IsType<OkObjectResult>(await api.AddDepartment(new("BM-FAKE","Công nghệ dữ liệu",facultyId,null),Ct)).Value);
        Assert.Equal("BM-CNDL",department.Code);Assert.Equal("BM-CNDL-02",second.Code);
        var preview=Assert.IsType<OkObjectResult>(await api.NextCourseCode(department.Id,Ct));Assert.Empty(await db.CourseCodeCounters.ToArrayAsync());
        var one=Assert.IsType<Course>(Assert.IsType<OkObjectResult>(await api.AddCourse(new("MH-FAKE-999","Môn một",department.Id,3,null, StartAcademicYear:"2026-2027", StartSemester:"HK1"),Ct)).Value);
        var two=Assert.IsType<Course>(Assert.IsType<OkObjectResult>(await api.AddCourse(new(null,"Môn hai",department.Id,2,null, StartAcademicYear:"2026-2027", StartSemester:"HK1"),Ct)).Value);
        Assert.Equal("MH-CNDL-001",one.Code);Assert.Equal("MH-CNDL-002",two.Code);
        var other=Assert.IsType<Course>(Assert.IsType<OkObjectResult>(await api.AddCourse(new(null,"Môn khác",second.Id,3,null, StartAcademicYear:"2026-2027", StartSemester:"HK1"),Ct)).Value);Assert.Equal("MH-CNDL-02-001",other.Code);
        Assert.IsType<NoContentResult>(await api.UpdateCourse(one.Id,new("CLIENT-CHANGE","Tên mới",department.Id,4,null, StartAcademicYear:"2026-2027", StartSemester:"HK1"),Ct));
        Assert.Equal("MH-CNDL-001",(await db.Courses.FindAsync(one.Id))!.Code);
        Assert.IsType<ConflictObjectResult>(await api.UpdateCourse(one.Id,new(null,"Chuyển bộ môn",second.Id,4,null, StartAcademicYear:"2026-2027", StartSemester:"HK1"),Ct));
        Assert.IsType<NoContentResult>(await api.UpdateDepartment(department.Id,new("CLIENT-CHANGE","Tên bộ môn mới",facultyId,null),Ct));
        Assert.Equal("BM-CNDL",(await db.DepartmentsSet.FindAsync(department.Id))!.Code);
    }

    [Theory]
    [InlineData("ATTT")][InlineData("bm0001")][InlineData("BM-8f6c523117f842b4a5e40eff155feafc")]
    [InlineData("BM-8F6C523117F842B4A5E40EFF155FEAFC")][InlineData("CNPM")]
    public async Task Legacy_departments_cannot_generate_new_courses_and_remain_unchanged(string code)
    {
        await using var db=await Fixture();(await db.DepartmentsSet.FindAsync(11L))!.Code=code;await db.SaveChangesAsync();
        var count=await db.Courses.CountAsync();var api=With(new CatalogsController(db,new Audit()),1);
        var error=await Assert.ThrowsAsync<ArgumentException>(()=>api.AddCourse(new("MH-CLIENT-001","Blocked",11,3,null, StartAcademicYear:"2026-2027", StartSemester:"HK1"),Ct));
        Assert.Equal(CatalogCodes.InvalidDepartmentMessage,error.Message);Assert.Equal(code,(await db.DepartmentsSet.FindAsync(11L))!.Code);
        Assert.Equal(count,await db.Courses.CountAsync());Assert.Empty(await db.CourseCodeCounters.ToArrayAsync());
    }

    [SqlServerFact]
    public async Task Sql_concurrent_creates_unique_indexes_rollback_and_seed_replay_preserve_legacy_records()
    {
        await using var database=await AcademicSqlServerTests.IsolatedDatabase.Create();long userId,facultyId,departmentId;string[] originalCodes;
        await using(var db=database.Context()){
            db.DevelopmentDataEnabled=true;await DemoDataSeeder.SeedAsync(db);
            Assert.All(await db.DepartmentsSet.Where(x=>db.CatalogSeedIdentities.Any(s=>s.DepartmentId==x.Id)).ToArrayAsync(),x=>Assert.True(CatalogCodes.ValidDepartment(x.Code)));
            Assert.All(await db.Courses.Where(x=>db.CatalogSeedIdentities.Any(s=>s.CourseId==x.Id)).ToArrayAsync(),x=>Assert.Matches("^MH-[A-Z0-9]+(?:-[0-9]{2})?-[0-9]{3}$",x.Code));
            userId=await db.UsersSet.Where(x=>x.UserName=="demo.dean").Select(x=>x.Id).SingleAsync();facultyId=(await db.CatalogSeedIdentities.SingleAsync(x=>x.Key=="demo.faculty")).FacultyId!.Value;
            var legacy=new Department{Code="bm0001",Name="Legacy untouched",FacultyId=facultyId};db.Add(legacy);await db.SaveChangesAsync();
            originalCodes=await db.DepartmentsSet.OrderBy(x=>x.Id).Select(x=>x.Code).ToArrayAsync();
        }
        var departments=await Task.WhenAll(Enumerable.Range(0,2).Select(async _=>{await using var db=database.Context();return Assert.IsType<Department>(Assert.IsType<OkObjectResult>(await With(new CatalogsController(db,new Audit()),userId).AddDepartment(new("FORGED","Công nghệ dữ liệu",facultyId,null),Ct)).Value);}));
        Assert.Equal(new[]{"BM-CNDL","BM-CNDL-02"},departments.Select(x=>x.Code).Order().ToArray());departmentId=departments.Single(x=>x.Code=="BM-CNDL").Id;
        var courses=await Task.WhenAll(Enumerable.Range(0,16).Select(async i=>{await using var db=database.Context();return Assert.IsType<Course>(Assert.IsType<OkObjectResult>(await With(new CatalogsController(db,new Audit()),userId).AddCourse(new("FORGED",$"Concurrent {i}",departmentId,3,null, StartAcademicYear:"2026-2027", StartSemester:"HK1"),Ct)).Value);}));
        Assert.Equal(16,courses.Select(x=>x.Code).Distinct().Count());Assert.Contains(courses,x=>x.Code=="MH-CNDL-001");Assert.Contains(courses,x=>x.Code=="MH-CNDL-016");
        await using(var db=database.Context()){
            var api=With(new CatalogsController(db,new FailingAudit()),userId);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>api.AddCourse(new(null,"Rolled back",departmentId,3,null, StartAcademicYear:"2026-2027", StartSemester:"HK1"),Ct));
        }
        await using(var db=database.Context()){
            Assert.Equal(16,(await db.CourseCodeCounters.FindAsync(departmentId))!.LastNumber);Assert.False(await db.Courses.AnyAsync(x=>x.Name=="Rolled back"));
            db.Courses.Add(new(){Code=courses[0].Code,Name="Duplicate",DepartmentId=departmentId});await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());
        }
        await using(var db=database.Context()){
            db.DepartmentsSet.Add(new(){Code=departments[0].Code,Name="Duplicate",FacultyId=facultyId});await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());
        }
        await using(var db=database.Context()){
            db.DevelopmentDataEnabled=true;var marker=await db.CatalogSeedIdentities.FirstAsync(x=>x.CourseId!=null);(await db.Courses.FindAsync(marker.CourseId))!.Name="Renamed by user";await db.SaveChangesAsync();
            var counts=(await db.DepartmentsSet.CountAsync(),await db.Courses.CountAsync(),await db.CatalogSeedIdentities.CountAsync());await DemoDataSeeder.SeedAsync(db);await DemoDataSeeder.SeedAsync(db);
            Assert.Equal(counts,(await db.DepartmentsSet.CountAsync(),await db.Courses.CountAsync(),await db.CatalogSeedIdentities.CountAsync()));
            Assert.Equal(originalCodes,(await db.DepartmentsSet.OrderBy(x=>x.Id).Select(x=>x.Code).ToArrayAsync()).Take(originalCodes.Length));
        }
        await database.Apply("012_catalog_business_codes.sql");await database.Apply("012_catalog_business_codes.sql");
    }
    private sealed class FailingAudit:HTBAM.Application.Interfaces.IAuditService{public Task WriteAsync(long? userId,string action,string entityType,string entityId,object? metadata=null,CancellationToken ct=default)=>throw new InvalidOperationException("Audit rollback test");}
}
