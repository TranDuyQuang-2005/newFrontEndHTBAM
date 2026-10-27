using System.Text.RegularExpressions;
using HTBAM.Api.Controllers;
using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HTBAM.Tests;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HTBAM_TEST_SQL_CONNECTION")))
            Skip = "Set HTBAM_TEST_SQL_CONNECTION to run against a disposable SQL Server database.";
    }
}

public sealed class AcademicSqlServerTests
{
    [SqlServerFact]
    public async Task Runner_upgrades_v6_and_partial_v7_without_overwriting_data_and_seed_repeats_exactly()
    {
        await using var database = await IsolatedDatabase.Create(academic: false);
        await using (var db = database.Context())
        {
            await db.Database.ExecuteSqlRawAsync("INSERT Faculties(Code,Name) VALUES('PRESERVE','Original business record'); CREATE TABLE AcademicTerms(Id bigint IDENTITY PRIMARY KEY, AcademicYear nvarchar(16) NOT NULL, Semester int NOT NULL); ALTER TABLE ClassSections ADD CreationKey nvarchar(32) NULL; ALTER TABLE Sessions ADD ClassScheduleId bigint NULL;");
        }
        var directory = SqlMigrationRunner.FindDirectory();
        await SqlMigrationRunner.RunAsync(database.ConnectionString, directory);
        await SqlMigrationRunner.RunAsync(database.ConnectionString, directory);
        await using var verify = database.Context();
        await SqlMigrationRunner.VerifyAsync(verify, directory);
        var sqlRelations = (await verify.Database.SqlQueryRaw<string>("SELECT OBJECT_NAME(f.parent_object_id)+'.'+COL_NAME(f.parent_object_id,f.parent_column_id)+'>'+OBJECT_NAME(f.referenced_object_id) AS Value FROM sys.foreign_key_columns f").ToArrayAsync()).ToHashSet();
        var sqlUniqueIndexes = (await verify.Database.SqlQueryRaw<string>("SELECT t.name+'.'+STRING_AGG(c.name,',') WITHIN GROUP(ORDER BY ic.key_ordinal) AS Value FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.is_unique=1 AND ic.is_included_column=0 GROUP BY t.name,i.index_id").ToArrayAsync()).ToHashSet();
        foreach (var entity in verify.Model.GetEntityTypes())
        {
            foreach (var fk in entity.GetForeignKeys())
                Assert.Contains(entity.GetTableName() + "." + fk.Properties.Single().Name + ">" + fk.PrincipalEntityType.GetTableName(), sqlRelations);
            foreach (var index in entity.GetIndexes().Where(x => x.IsUnique))
                Assert.Contains(entity.GetTableName() + "." + string.Join(",", index.Properties.Select(x => x.Name)), sqlUniqueIndexes);
        }
        Assert.Equal("Original business record", (await verify.FacultiesSet.SingleAsync(x => x.Code == "PRESERVE")).Name);
        verify.DevelopmentDataEnabled = true;
        await DemoDataSeeder.SeedAsync(verify);
        var first = await FixtureCounts(verify);
        var dean = await verify.Teachers.SingleAsync(x => verify.UsersSet.Any(u=>u.Id==x.UserId&&u.UserName=="demo.dean"));
        var position = await verify.ManagementAssignmentsSet.SingleAsync(x => x.TeacherId == dean.Id && x.IsActive);
        position.IsActive = false; position.EffectiveTo = DateTime.UtcNow; dean.FullName = "User edited teacher";
        await verify.SaveChangesAsync();
        await DemoDataSeeder.SeedAsync(verify);
        Assert.Equal(first, await FixtureCounts(verify));
        Assert.Equal("User edited teacher", (await verify.Teachers.SingleAsync(x => x.Id == dean.Id)).FullName);
        Assert.False((await verify.ManagementAssignmentsSet.SingleAsync(x => x.Id == position.Id)).IsActive);
        Assert.Equal(13, await verify.UsersSet.CountAsync(x => x.UserName.StartsWith("demo.")));
        Assert.Equal(60, await verify.StudentsSet.CountAsync(x => x.StudentCode.StartsWith("DEMO-SV-")));
        Assert.Equal(12, await verify.ClassSections.CountAsync(x => x.SourceClassSectionCode != null && x.SourceClassSectionCode.StartsWith("DEMO-LHP-")));
        Assert.Equal(14, await verify.ClassSchedules.CountAsync());
        Assert.True(await verify.SessionsSet.AnyAsync(x => x.ScheduledStart > DateTime.UtcNow));
        Assert.False(await verify.AttendanceSet.AnyAsync(x => verify.SessionsSet.Any(s => s.Id == x.SessionId && s.ScheduledStart > DateTime.UtcNow)));
        var terms = await verify.AcademicTerms.ToDictionaryAsync(x => x.Id);
        Assert.All(await verify.SessionsSet.Include(x => x.ClassSchedule).Include(x => x.ClassSection).Where(x => x.ClassScheduleId != null).ToArrayAsync(), s =>
        {
            Assert.InRange(s.OccurrenceDate!.Value, s.ClassSchedule!.StartsOn, s.ClassSchedule.EndsOn);
            var weeks = System.Text.Json.JsonSerializer.Deserialize<int[]>(s.ClassSchedule.SelectedWeeksJson)!;
            var week = (s.OccurrenceDate.Value.DayNumber - terms[s.ClassSection.AcademicTermId!.Value].WeekOneMonday!.Value.DayNumber) / 7 + 1;
            Assert.Contains(week, weeks);
        });
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            verify.AcademicTerms.Add(new() { AcademicYear = "2026-2027", Semester = 1 });
            await verify.SaveChangesAsync();
        });
    }

    [SqlServerFact]
    public async Task Failed_migration_rolls_back_all_batches_and_history_and_can_be_retried()
    {
        await using var database = await IsolatedDatabase.Create(academic: false);
        var source = SqlMigrationRunner.FindDirectory();
        var copy = Path.Combine(Path.GetTempPath(), "HTBAM_MigrationFailure_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        foreach (var file in SqlMigrationRunner.Files) File.Copy(Path.Combine(source, file), Path.Combine(copy, file));
        await File.AppendAllTextAsync(Path.Combine(copy, "007_academic_scheduling.sql"), "\nGO\nTHROW 51001, 'Injected test failure after all migration batches', 1;\n");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SqlMigrationRunner.RunAsync(database.ConnectionString, copy));
        Assert.Contains("007_academic_scheduling.sql", error.Message);
        await using (var db = database.Context())
        {
            Assert.Null(await db.Database.SqlQueryRaw<string>("SELECT OBJECT_NAME(OBJECT_ID('dbo.AcademicTerms')) AS Value").FirstAsync());
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM dbo.SchemaMigrations WHERE ScriptName='007_academic_scheduling.sql'").SingleAsync());
        }
        await SqlMigrationRunner.RunAsync(database.ConnectionString, source);
        await using var verify = database.Context();
        await SqlMigrationRunner.VerifyAsync(verify, source);
        Assert.Equal(32, await verify.BellPeriods.CountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqlMigrationRunner.RunAsync(database.ConnectionString, copy));
    }

    private static async Task<string> FixtureCounts(AppDbContext db) => string.Join(",", new[] {
        await db.UsersSet.CountAsync(), await db.Teachers.CountAsync(), await db.ManagementAssignmentsSet.CountAsync(),
        await db.StudentsSet.CountAsync(), await db.Courses.CountAsync(), await db.Rooms.CountAsync(), await db.ClassSections.CountAsync(),
        await db.Enrollments.CountAsync(), await db.ClassSchedules.CountAsync(), await db.SessionsSet.CountAsync(), await db.SessionStudentsSet.CountAsync(),
        await db.AttendanceSet.CountAsync(), await db.BehaviorEventsSet.CountAsync(), await db.AlertsSet.CountAsync(), await db.StudentSessionSummariesSet.CountAsync(), await db.AuditLogsSet.CountAsync() });
    [SqlServerFact]
    public async Task Migration_is_repeatable_and_parallel_catalog_creates_are_unique_on_sqlserver()
    {
        await using var database = await IsolatedDatabase.Create();
        var results = await Task.WhenAll(Enumerable.Range(0,24).Select(async i =>
        {
            await using var db=database.Context();
            var controller=AcademicSchedulingTests.With(new CatalogsController(db,new AcademicSchedulingTests.Audit()),9,"ADMIN");
            return Assert.IsType<OkObjectResult>(await controller.AddRoom(new("CLIENT_CODE",$"Room {i}",null,10,null),CancellationToken.None));
        }));
        await using var verify=database.Context();
        var codes=await verify.Rooms.Select(x=>x.Code).ToArrayAsync();
        Assert.Equal(24,codes.Length);Assert.Equal(24,codes.Distinct().Count());Assert.All(codes,x=>Assert.StartsWith("PH-",x));
        Assert.Equal(32,await verify.BellPeriods.CountAsync());
        await database.Apply("007_academic_scheduling.sql");
        Assert.Equal(32,await verify.BellPeriods.CountAsync());
    }

    [SqlServerFact]
    public async Task Sql_server_schedule_transaction_filters_and_assignment_races_enforce_invariants()
    {
        await using var database=await IsolatedDatabase.Create();
        long headUserId, ownerUserId, headTeacherId, ownerTeacherId, departmentId, facultyId, termId, courseId, roomId, policyId;
        await using(var db=database.Context())
        {
            var lecturerRole=await db.RolesSet.SingleAsync(x=>x.Name=="LECTURER");
            var headUser=new User{UserName="test-head",Email="head@test.invalid",FullName="Head",Status="ACTIVE"};
            var ownerUser=new User{UserName="test-owner",Email="owner@test.invalid",FullName="Owner",Status="ACTIVE"};
            db.UsersSet.AddRange(headUser,ownerUser);await db.SaveChangesAsync();headUserId=headUser.Id;ownerUserId=ownerUser.Id;
            db.UserRoles.AddRange(new UserRole{UserId=headUser.Id,RoleId=lecturerRole.Id},new UserRole{UserId=ownerUser.Id,RoleId=lecturerRole.Id});
            var faculty=new Faculty{Code="F-TEST",Name="Faculty"};db.FacultiesSet.Add(faculty);await db.SaveChangesAsync();facultyId=faculty.Id;
            var department=new Department{Code="D-TEST",Name="Department",FacultyId=faculty.Id};db.DepartmentsSet.Add(department);await db.SaveChangesAsync();departmentId=department.Id;
            var head=new Teacher{TeacherCode="T-HEAD",FullName="Head",DepartmentId=department.Id,UserId=headUser.Id};
            var owner=new Teacher{TeacherCode="T-OWNER",FullName="Owner",DepartmentId=department.Id,UserId=ownerUser.Id};db.Teachers.AddRange(head,owner);await db.SaveChangesAsync();headTeacherId=head.Id;ownerTeacherId=owner.Id;
            db.ManagementAssignmentsSet.Add(new ManagementAssignment{TeacherId=head.Id,FacultyId=faculty.Id,PositionType="FACULTY_HEAD",EffectiveFrom=DateTime.UtcNow.AddDays(-1)});
            var course=new Course{Code="C-TEST",Name="Course",DepartmentId=department.Id,Credits=3};var room=new Room{Code="R-TEST",Name="Phòng thí nghiệm thử",RoomType="THEORY"};var term=AcademicSchedulingTests.Term();term.Id=0;
            db.Courses.Add(course);db.Rooms.Add(room);db.AcademicTerms.Add(term);await db.SaveChangesAsync();courseId=course.Id;roomId=room.Id;termId=term.Id;policyId=await db.AttendancePoliciesSet.Select(x=>x.Id).FirstAsync();
        }
        var line=AcademicSchedulingTests.Line() with{RoomId=roomId};
        var request=AcademicSchedulingTests.Request() with{CourseId=courseId,TeacherId=ownerTeacherId,AcademicTermId=termId,AttendancePolicyId=policyId,Schedules=[line]};
        long classId;
        await using(var db=database.Context())
        {
            var classes=AcademicSchedulingTests.With(new ClassSectionsController(db,new AcademicSchedulingTests.Audit()),headUserId);
            Assert.IsType<OkObjectResult>(await classes.Create(request,CancellationToken.None));
            classId=await db.ClassSections.Select(x=>x.Id).SingleAsync();
            Assert.Equal(4,await db.SessionsSet.CountAsync());
            Assert.IsType<OkObjectResult>(await classes.Create(request,CancellationToken.None));
            var lookup=AcademicSchedulingTests.With(new LookupsController(db),ownerUserId);
            Assert.IsType<OkObjectResult>(await lookup.Get("rooms","phong thi nghiem thu",ct:CancellationToken.None));
            Assert.IsType<OkObjectResult>(await classes.List(null,departmentId,1,20,CancellationToken.None));
        }
        var assignments=await Task.WhenAll(Enumerable.Range(0,2).Select(async _=>
        {
            await using var db=database.Context();
            var controller=AcademicSchedulingTests.With(new ManagementAssignmentsController(db,new AcademicSchedulingTests.Audit()),headUserId);
            return await controller.AssignDepartmentHead(new(ownerTeacherId,departmentId,"race"),CancellationToken.None);
        }));
        Assert.Single(assignments.OfType<OkObjectResult>());Assert.Single(assignments.OfType<ConflictObjectResult>());
        await using(var db=database.Context())
        {
            Assert.Equal(1,await db.ManagementAssignmentsSet.CountAsync(x=>x.PositionType=="DEPARTMENT_HEAD"&&x.IsActive));
            var controller=AcademicSchedulingTests.With(new ClassSectionsController(db,new AcademicSchedulingTests.Audit()),headUserId);
            Assert.IsType<OkObjectResult>(await controller.SetGrant(classId,ownerTeacherId,new(true,true,true,true),CancellationToken.None));
        }
    }

    internal sealed class IsolatedDatabase(string connectionString,string databaseName,string masterConnection) : IAsyncDisposable
    {
        public string ConnectionString => connectionString;
        public AppDbContext Context()=>new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connectionString,sql=>sql.EnableRetryOnFailure()).Options);
        public static async Task<IsolatedDatabase> Create(bool academic = true)
        {
            var original=Environment.GetEnvironmentVariable("HTBAM_TEST_SQL_CONNECTION")!;
            var name="HTBAM_AcademicTests_"+Guid.NewGuid().ToString("N");
            var master=new SqlConnectionStringBuilder(original){InitialCatalog="master"}.ConnectionString;
            await using(var connection=new SqlConnection(master)){await connection.OpenAsync();await using var command=new SqlCommand($"CREATE DATABASE [{name}]",connection);await command.ExecuteNonQueryAsync();}
            var database=new IsolatedDatabase(new SqlConnectionStringBuilder(original){InitialCatalog=name}.ConnectionString,name,master);
            try
            {
                foreach(var script in academic ? SqlMigrationRunner.Files : SqlMigrationRunner.Files.Take(5))await database.Apply(script);
                return database;
            }
            catch {await database.DisposeAsync();throw;}
        }
        public async Task Apply(string file)
        {
            var root=new DirectoryInfo(AppContext.BaseDirectory);
            while(root!=null&&!Directory.Exists(Path.Combine(root.FullName,"database","sqlserver")))root=root.Parent;
            if(root==null)throw new InvalidOperationException("Cannot locate SQL scripts.");
            var sql=await File.ReadAllTextAsync(Path.Combine(root.FullName,"database","sqlserver",file));
            sql=Regex.Replace(sql,@"^\s*USE\s+(?:HTBAM|\[HTBAM\])\s*;?\s*$","",RegexOptions.Multiline|RegexOptions.IgnoreCase);
            if(Regex.IsMatch(sql,@"^\s*USE\b",RegexOptions.Multiline|RegexOptions.IgnoreCase))throw new InvalidOperationException("Test scripts must not switch database context.");
            await using var connection=new SqlConnection(connectionString);await connection.OpenAsync();
            foreach(var batch in Regex.Split(sql,@"^\s*GO\s*$",RegexOptions.Multiline|RegexOptions.IgnoreCase))
            {if(string.IsNullOrWhiteSpace(batch))continue;await using var command=new SqlCommand(batch,connection){CommandTimeout=60};await command.ExecuteNonQueryAsync();}
        }
        public async ValueTask DisposeAsync()
        {
            if(!Regex.IsMatch(databaseName,"^HTBAM_AcademicTests_[a-f0-9]{32}$"))throw new InvalidOperationException("Invalid isolated database name.");
            SqlConnection.ClearAllPools();
            await using var connection=new SqlConnection(masterConnection);await connection.OpenAsync();
            await using var command=new SqlCommand($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];",connection);await command.ExecuteNonQueryAsync();
        }
    }
}
