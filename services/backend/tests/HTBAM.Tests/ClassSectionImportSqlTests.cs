using System.Text.Json;
using ClosedXML.Excel;
using HTBAM.Api.Controllers;
using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HTBAM.Tests;

public sealed class ClassSectionImportSqlTests
{
    [SqlServerFact]
    public async Task Migration_011_preserves_legacy_ids_codes_sources_and_uniqueness_on_replay()
    {
        await using var database = await AcademicSqlServerTests.IsolatedDatabase.Create(academic: false);
        foreach (var file in HTBAM.Infrastructure.Data.SqlMigrationRunner.Files.Skip(5).TakeWhile(x => !x.StartsWith("011_"))) await database.Apply(file);
        await using (var db = database.Context())
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT Faculties(Code,Name) VALUES('F011','Preserve');
                INSERT Departments(Code,Name,FacultyId) SELECT 'D011','Preserve',Id FROM Faculties WHERE Code='F011';
                INSERT Teachers(TeacherCode,FullName,DepartmentId) SELECT 'T011','Preserve',Id FROM Departments WHERE Code='D011';
                INSERT Courses(Code,Name,DepartmentId,Credits) SELECT 'C011','Preserve',Id,3 FROM Departments WHERE Code='D011';
                INSERT ClassSections(Code,Name,CourseId,TeacherId,Semester,AcademicYear,SourceCode)
                SELECT 'demo_001','Preserve',c.Id,t.Id,'HK1','2026-2027','0012345678901' FROM Courses c CROSS JOIN Teachers t WHERE c.Code='C011' AND t.TeacherCode='T011';
                """);
        }
        long before;
        await using (var db = database.Context()) before = await db.Database.SqlQueryRaw<long>("SELECT Id AS Value FROM ClassSections WHERE Code='demo_001'").SingleAsync();
        await HTBAM.Infrastructure.Data.SqlMigrationRunner.RunAsync(database.ConnectionString, HTBAM.Infrastructure.Data.SqlMigrationRunner.FindDirectory());
        await HTBAM.Infrastructure.Data.SqlMigrationRunner.RunAsync(database.ConnectionString, HTBAM.Infrastructure.Data.SqlMigrationRunner.FindDirectory());
        await using var verify = database.Context();
        var preserved = await verify.ClassSections.SingleAsync(x => x.Id == before);
        Assert.Equal("demo_001", preserved.Code); Assert.Equal("0012345678901", preserved.SourceClassSectionCode);
        Assert.Equal(0, await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.columns WHERE object_id=OBJECT_ID('ClassSections') AND name='SourceCode'").SingleAsync());
        verify.ClassSections.Add(new() { Code = "other_legacy", Name = "Duplicate source", CourseId = preserved.CourseId, TeacherId = preserved.TeacherId, Semester = preserved.Semester, AcademicYear = preserved.AcademicYear, SourceClassSectionCode = preserved.SourceClassSectionCode });
        await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync());
    }
    [SqlServerFact]
    public async Task Readiness_waits_for_target_database_online_even_when_master_accepts_connections()
    {
        await using var database = await AcademicSqlServerTests.IsolatedDatabase.Create();
        var target = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString);
        var name = target.InitialCatalog;
        Assert.Matches("^HTBAM_AcademicTests_[a-f0-9]{32}$", name);
        target.InitialCatalog = "master";
        await using var master = new Microsoft.Data.SqlClient.SqlConnection(target.ConnectionString);
        await master.OpenAsync();
        async Task Execute(string sql) { await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, master); await command.ExecuteNonQueryAsync(); }
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new List<string>();
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        try
        {
            await Execute($"ALTER DATABASE [{name}] SET OFFLINE WITH ROLLBACK IMMEDIATE");
            var wait = HTBAM.Infrastructure.Data.SqlMigrationRunner.WaitForDatabaseAsync(database.ConnectionString,
                message => { logs.Add(message); if (message.Contains("OFFLINE")) observed.TrySetResult(); }, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100));
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(wait.IsCompleted);
            await Execute($"ALTER DATABASE [{name}] SET ONLINE");
            await wait;
            Assert.Contains(logs, x => x.Contains("ONLINE và kết nối đích sẵn sàng"));
        }
        finally { await Execute($"ALTER DATABASE [{name}] SET ONLINE"); }
    }
    [SqlServerFact]
    public async Task Concurrent_number_allocation_uses_sql_lock_and_unique_index_without_touching_legacy_codes()
    {
        await using var database = await AcademicSqlServerTests.IsolatedDatabase.Create();
        long courseId, termId, teacherId; string courseCode;
        await using (var db = database.Context())
        {
            db.DevelopmentDataEnabled = true; await DemoDataSeeder.SeedAsync(db);
            var owner = await db.Teachers.SingleAsync(x => db.UsersSet.Any(u=>u.Id==x.UserId&&u.UserName=="demo.editor")); teacherId = owner.Id;
            var course = await db.Courses.SingleAsync(x => x.Name == "Hệ thống theo dõi hành vi (DEMO)"); courseId = course.Id;courseCode=course.Code;
            termId = await db.AcademicTerms.Where(x => x.AcademicYear == "2026-2027" && x.Semester == 1).Select(x => x.Id).SingleAsync();
        }
        var codes = await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
        {
            await using var db = database.Context();
            return await BusinessTransaction.Run(db, "HTBAM:academic-scheduling", async () =>
            {
                var term = await db.AcademicTerms.SingleAsync(x => x.Id == termId);
                var code = await ClassSectionNumbering.Next(db, term, courseId, CancellationToken.None);
                db.ClassSections.Add(new() { Code = code, Name = "Concurrency test", CourseId = courseId, TeacherId = teacherId, AcademicTermId = termId, AcademicYear = term.AcademicYear, Semester = "1" });
                await db.SaveChangesAsync(); return code;
            }, CancellationToken.None);
        }));
        Assert.Equal(24, codes.Distinct().Count());
        Assert.Contains($"LHP-2627-HK1-{courseCode}-01", codes); Assert.Contains($"LHP-2627-HK1-{courseCode}-02", codes); Assert.Contains($"LHP-2627-HK1-{courseCode}-24", codes);
        await using var verify = database.Context();
        Assert.Equal(24, (await verify.ClassSectionCounters.SingleAsync(x => x.CourseId == courseId)).LastNumber);
    }

    [SqlServerFact]
    public async Task Import_rolls_back_all_classes_schedules_sessions_and_numbers_when_later_group_fails()
    {
        await using var database = await AcademicSqlServerTests.IsolatedDatabase.Create();
        long termId, policyId, userId, lateRoomId;
        byte[] bytes;
        await using (var db = database.Context())
        {
            db.DevelopmentDataEnabled = true; await DemoDataSeeder.SeedAsync(db);
            var term = new AcademicTerm { AcademicYear = "2030-2031", Semester = 1, StartsOn = new(2030, 9, 2), EndsOn = new(2030, 9, 29), DatesConfirmed = true, WeekOneMonday = new(2030, 9, 2), WeekMappingConfirmed = true };
            db.AcademicTerms.Add(term); await db.SaveChangesAsync(); termId = term.Id;
            policyId = await db.AttendancePoliciesSet.Where(x => x.Code == "DEFAULT").Select(x => x.Id).SingleAsync();
            userId = await db.UsersSet.Where(x => x.UserName == "demo.dean").Select(x => x.Id).SingleAsync();
            var rooms = await db.Rooms.Where(x => x.RoomType == "THEORY" && x.IsActive).OrderBy(x => x.Id).Take(2).ToArrayAsync(); lateRoomId = rooms[1].Id;
            var practical = await db.Rooms.FirstAsync(x => x.RoomType == "COMPUTER" && x.IsActive);
            using var workbook = new XLWorkbook(new MemoryStream(ClassSectionImportTests.Workbook())); var sheet = workbook.Worksheet("Lịch");
            var courseCode=await db.Courses.Where(x=>x.Name=="Cơ sở dữ liệu (DEMO)").Select(x=>x.Code).SingleAsync();
            sheet.Cell(2, 2).Value = courseCode; sheet.Cell(3, 2).Value = courseCode;
            var teacherCode=await db.Teachers.Where(x=>db.UsersSet.Any(u=>u.Id==x.UserId&&u.UserName=="demo.editor")).Select(x=>x.TeacherCode).SingleAsync();
            sheet.Cell(2, 3).Value = teacherCode; sheet.Cell(3, 3).Value = teacherCode;
            sheet.Cell(2, 8).Value = rooms[0].Code; sheet.Cell(3, 8).Value = practical.Code;
            sheet.Row(2).CopyTo(sheet.Row(4)); sheet.Cell(4, 1).Value = "SRC-02"; sheet.Cell(4, 5).Value = "4"; sheet.Cell(4, 8).Value = rooms[1].Code;
            using var stream = new MemoryStream(); workbook.SaveAs(stream); bytes = stream.ToArray();
        }
        string token;
        await using (var db = database.Context())
        {
            var user = AcademicSchedulingTests.With(new ClassSectionsController(db, new AcademicSchedulingTests.Audit()), userId).User;
            var preview = await new ClassSectionExcelImport(db).Preview(bytes, "test.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Lịch", new(termId, policyId), user, CancellationToken.None);
            Assert.Equal(2, preview.ClassesToCreate); token = preview.Token;
            (await db.Rooms.FindAsync(lateRoomId))!.IsActive = false; await db.SaveChangesAsync();
        }
        await using (var db = database.Context())
        {
            var user = AcademicSchedulingTests.With(new ClassSectionsController(db, new AcademicSchedulingTests.Audit()), userId).User;
            await Assert.ThrowsAsync<ArgumentException>(() => BusinessTransaction.Run(db, "HTBAM:academic-scheduling", () => new ClassSectionExcelImport(db).Confirm(token, user, CancellationToken.None), CancellationToken.None));
        }
        await using var verify = database.Context();
        Assert.False(await verify.ClassSections.AnyAsync(x => x.AcademicTermId == termId));
        Assert.False(await verify.ClassSectionCounters.AnyAsync(x => x.AcademicTermId == termId));
        Assert.False(await verify.ClassSchedules.AnyAsync(x => x.ClassSection.AcademicTermId == termId));
        Assert.False(await verify.ClassSectionImportPreviews.AnyAsync(x => x.Token == token));
    }
}
