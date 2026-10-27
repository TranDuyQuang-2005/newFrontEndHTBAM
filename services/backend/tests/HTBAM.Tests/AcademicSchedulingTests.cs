using System.Security.Claims;
using System.Text.Json;
using HTBAM.Api.Controllers;
using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HTBAM.Tests;

public sealed class AcademicSchedulingTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Internal_codes_are_unique_under_parallel_creation_and_backed_by_database_indexes()
    {
        var codes = await Task.WhenAll(Enumerable.Range(0, 2000).Select(_ => Task.Run(() => InternalCode.New("LHP"))));
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, x => Assert.Matches("^LHP-[a-f0-9]{32}$", x));
        await using var db = NewDb();
        Assert.Contains(db.Model.FindEntityType(typeof(ClassSection))!.GetIndexes(), x => x.IsUnique && x.Properties.Count == 1 && x.Properties[0].Name == "Code");
        Assert.Contains(db.Model.FindEntityType(typeof(Session))!.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(["ClassScheduleId", "OccurrenceDate"]));
    }

    [Theory]
    [InlineData(1, 3)] [InlineData(2, 2)] [InlineData(3, 1)]
    public void Credit_rules_have_independent_complete_consecutive_ranges(int credits, int count)
    {
        var term = Term();
        var ranges = Enumerable.Range(1, count).Select(n => AcademicRules.Range(term, [], credits, credits == 3 ? null : n)).ToArray();
        Assert.Equal(term.StartsOn, ranges[0].StartsOn); Assert.Equal(term.EndsOn, ranges[^1].EndsOn);
        for (var i = 1; i < ranges.Length; i++) Assert.Equal(ranges[i-1].EndsOn.AddDays(1), ranges[i].StartsOn);
        var two = AcademicRules.Range(term, [], 2, 1); var three = AcademicRules.Range(term, [], 1, 1);
        Assert.NotEqual(two.EndsOn, three.EndsOn);
    }

    [Fact]
    public void Unconfirmed_dates_phases_unsupported_credits_and_invalid_phase_choices_are_blocked()
    {
        var term = Term(); term.DatesConfirmed = false;
        Assert.Throws<InvalidOperationException>(() => AcademicRules.Range(term, [], 3, null));
        term.DatesConfirmed = true; term.PhasesConfirmed = false;
        Assert.Throws<InvalidOperationException>(() => AcademicRules.Range(term, [], 2, 1));
        Assert.Throws<ArgumentException>(() => AcademicRules.Range(term, [], 4, null));
        Assert.Throws<ArgumentException>(() => AcademicRules.Range(term, [], 3, 1));
        Assert.Throws<ArgumentException>(() => AcademicRules.Range(term, [], 2, 3));
    }

    [Fact]
    public async Task Huit_seed_matches_every_period_and_never_seeds_unconfirmed_semester_end_or_old_hours()
    {
        await using var db = NewDb(); await AcademicSeed.SeedAsync(db);
        Assert.Equal(32, await db.BellPeriods.CountAsync());
        string[] expectedA = ["06:30-07:20","07:20-08:10","08:10-09:00","09:30-10:20","10:20-11:10","11:10-12:00","12:30-13:20","13:20-14:10","14:10-15:00","15:30-16:20","16:20-17:10","17:10-18:00","18:10-19:00","19:00-19:50","19:50-20:40","20:40-21:30"];
        string[] expectedB = ["07:00-07:50","07:50-08:40","08:40-09:30","09:30-10:20","10:20-11:10","11:10-12:00","12:30-13:20","13:20-14:10","14:10-15:00","15:00-15:50","15:50-16:40","16:40-17:30","18:00-18:50","18:50-19:40","19:40-20:30","20:30-21:20"];
        foreach (var group in new[] { "A", "B" })
        {
            var version = await db.BellTimeVersions.SingleAsync(x => x.Group == group);
            Assert.Equal(new DateOnly(2026,1,12), version.EffectiveFrom);
            var periods = await db.BellPeriods.Where(x => x.BellTimeVersionId == version.Id).OrderBy(x => x.Number).ToListAsync();
            Assert.Equal(group == "A" ? expectedA : expectedB, periods.Select(x => $"{x.StartsAt:HH:mm}-{x.EndsAt:HH:mm}"));
            Assert.Throws<ArgumentException>(() => AcademicRules.PeriodWindow(periods,6,7));
            Assert.Throws<ArgumentException>(() => AcademicRules.PeriodWindow(periods,12,13));
            Assert.Throws<ArgumentException>(() => AcademicRules.PeriodWindow(periods,3,2));
        }
        var semester = await db.AcademicTerms.SingleAsync(x => x.Semester == 2);
        Assert.Equal(new DateOnly(2026,1,12), semester.StartsOn); Assert.Null(semester.EndsOn); Assert.False(semester.DatesConfirmed);
        Assert.Empty(await db.TermPhases.ToListAsync());
        await AcademicSeed.SeedAsync(db); Assert.Equal(32, await db.BellPeriods.CountAsync());
    }

    [Theory]
    [InlineData("THEORY","TH","A")] [InlineData("ZOOM","LT","A")]
    [InlineData("LAB","TH","B")] [InlineData("COMPUTER","LT","B")]
    public void Bell_group_uses_room_type_and_does_not_assume_practical_always_means_group_B(string room, string activity, string expected) =>
        Assert.Equal(expected, AcademicRules.BellGroup(room,activity,null));

    [Fact]
    public void Unknown_room_requires_explicit_group_and_wrong_group_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => AcademicRules.BellGroup("UNKNOWN","LT",null));
        Assert.Throws<ArgumentException>(() => AcademicRules.BellGroup("THEORY","TH","B"));
        Assert.Equal("B",AcademicRules.BellGroup("UNKNOWN","TH","B"));
    }

    [Fact]
    public async Task Expansion_preserves_nonconsecutive_weeks_room_changes_holidays_and_vietnam_midnight()
    {
        await using var db = await Fixture(); var term = await db.AcademicTerms.FindAsync(301L);
        var lines = new[] { Line() with { SelectedWeeks=[1], EndsOn=new(2030,9,15) },
            Line() with { LineKey=Guid.NewGuid().ToString("N"),RoomId=403,StartsOn=new(2030,9,16),SelectedWeeks=[3,4] } };
        var occurrences = AcademicRules.Expand(term!,new(term!.StartsOn!.Value,term.EndsOn!.Value),lines,
            await db.Rooms.ToDictionaryAsync(x=>x.Id),102,await db.BellTimeVersions.ToArrayAsync(),await db.BellPeriods.ToArrayAsync(),new HashSet<DateOnly>{new(2030,9,23)});
        Assert.Equal(2,occurrences.Length);Assert.Equal(new DateOnly(2030,9,2),occurrences[0].Date);Assert.Equal(401,occurrences[0].RoomId);
        Assert.Equal(new DateOnly(2030,9,16),occurrences[1].Date);Assert.Equal(403,occurrences[1].RoomId);
        Assert.Equal(new DateTime(2030,9,1,23,30,0,DateTimeKind.Utc),occurrences[0].StartsAtUtc);
        Assert.Equal(new DateOnly(2030,9,2),DateOnly.FromDateTime(occurrences[0].StartsAtUtc.AddHours(7)));
    }

    [Fact]
    public async Task Older_dates_missing_week_anchor_and_inactive_rooms_cannot_generate_sessions()
    {
        await using var db = await Fixture(); var term=Term();term.WeekMappingConfirmed=false;
        var versions=await db.BellTimeVersions.ToArrayAsync();var periods=await db.BellPeriods.ToArrayAsync();var rooms=await db.Rooms.ToDictionaryAsync(x=>x.Id);
        Assert.Throws<InvalidOperationException>(()=>AcademicRules.Expand(term,new(term.StartsOn!.Value,term.EndsOn!.Value),[Line() with {SelectedWeeks=[1]}],rooms,102,versions,periods,new HashSet<DateOnly>()));
        term.StartsOn=new(2025,8,18);term.EndsOn=new(2025,9,30);
        Assert.Throws<InvalidOperationException>(()=>AcademicRules.Expand(term,new(term.StartsOn.Value,term.EndsOn.Value),[Line() with {StartsOn=term.StartsOn.Value,EndsOn=term.EndsOn.Value}],rooms,102,versions,periods,new HashSet<DateOnly>()));
        rooms[401].IsActive=false;term=Term();
        Assert.Throws<ArgumentException>(()=>AcademicRules.Expand(term,new(term.StartsOn!.Value,term.EndsOn!.Value),[Line()],rooms,102,versions,periods,new HashSet<DateOnly>()));
    }

    [Fact]
    public async Task Per_group_week_calendars_support_different_T1_and_reject_cross_term_ids()
    {
        await using var db=await Fixture();var term=Term();term.WeekMappingConfirmed=false;
        var calendar=new AcademicWeekCalendar{Id=71,AcademicTermId=301,Name="Group",WeekOneMonday=new(2030,9,9),Confirmed=true,Evidence="Test confirmed calendar"};
        var calendars=new Dictionary<long,AcademicWeekCalendar>{{71,calendar}};
        var result=AcademicRules.Expand(term,new(term.StartsOn!.Value,term.EndsOn!.Value),[Line() with {SelectedWeeks=[1],WeekCalendarId=71}],await db.Rooms.ToDictionaryAsync(x=>x.Id),102,await db.BellTimeVersions.ToArrayAsync(),await db.BellPeriods.ToArrayAsync(),new HashSet<DateOnly>(),calendars);
        Assert.Equal(new DateOnly(2030,9,9),Assert.Single(result).Date);
        calendar.AcademicTermId=999;
        Assert.Throws<ArgumentException>(()=>AcademicRules.Expand(term,new(term.StartsOn.Value,term.EndsOn.Value),[Line() with {SelectedWeeks=[1],WeekCalendarId=71}],new Dictionary<long,Room>{{401,new Room{Id=401,RoomType="THEORY"}}},102,[],[],new HashSet<DateOnly>(),calendars));
    }

    [Fact]
    public async Task Create_is_idempotent_and_schedule_changes_preserve_sessions_with_business_data()
    {
        await using var db=await Fixture();var controller=With(new ClassSectionsController(db,new Audit()),1);
        var request=Request();Assert.IsType<OkObjectResult>(await controller.Create(request,Ct));Assert.IsType<OkObjectResult>(await controller.Create(request,Ct));
        Assert.Single(await db.ClassSections.ToListAsync());Assert.Equal(4,await db.SessionsSet.CountAsync());
        var section=await db.ClassSections.SingleAsync();var code=section.Code;
        var protectedSession=await db.SessionsSet.OrderBy(x=>x.ScheduledStart).FirstAsync();
        db.AttendanceSet.Add(new Attendance{SessionId=protectedSession.Id,StudentId=601,Status="PRESENT"});await db.SaveChangesAsync();
        var edit=request with {Revision=section.Revision,CreationKey=null,Schedules=[request.Schedules[0] with {RoomId=403}]};
        var preview=Assert.IsType<OkObjectResult>(await controller.PreviewUpdate(section.Id,edit,Ct));
        var token=JsonSerializer.SerializeToElement(preview.Value).GetProperty("PreviewToken").GetString();
        Assert.IsType<OkObjectResult>(await controller.Update(section.Id,edit with {PreviewToken=token},Ct));
        Assert.Equal(code,(await db.ClassSections.SingleAsync()).Code);Assert.Equal(7,await db.SessionsSet.CountAsync());
        Assert.Equal(4,await db.SessionsSet.CountAsync(x=>x.Status!="CANCELLED"));
        Assert.Equal(401,(await db.SessionsSet.SingleAsync(x=>x.Id==protectedSession.Id)).RoomId);
        Assert.Single(await db.AttendanceSet.ToListAsync());Assert.Equal(2,await db.ClassSchedules.CountAsync());
        Assert.IsType<ConflictObjectResult>(await controller.Update(section.Id,edit with {PreviewToken=token},Ct));
        Assert.Equal(7,await db.SessionsSet.CountAsync());
    }

    [Fact]
    public async Task Room_teacher_and_self_conflicts_are_checked_on_actual_dates()
    {
        await using var db=await Fixture();var c=With(new ClassSectionsController(db,new Audit()),1);var r=Request();
        await c.Create(r,Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>c.Create(r with {CreationKey=Guid.NewGuid().ToString("N"),Name="Room conflict"},Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>c.Create(r with {CreationKey=Guid.NewGuid().ToString("N"),Schedules=[r.Schedules[0] with {RoomId=403}]},Ct));
        var self=Request() with {Schedules=[Line() with {IsoDayOfWeek=2},Line() with {IsoDayOfWeek=2}]};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>c.Create(self,Ct));
        var disjoint=Request() with {Schedules=[Line() with {IsoDayOfWeek=2}]};
        Assert.IsType<OkObjectResult>(await c.Create(disjoint,Ct));
    }

    [Fact]
    public async Task Exceptions_regenerate_future_sessions_without_resurrecting_manual_cancellations_and_preview_detects_drift()
    {
        await using var db=await Fixture();var controller=With(new ClassSectionsController(db,new Audit()),1);var request=Request();await controller.Create(request,Ct);
        var section=await db.ClassSections.SingleAsync();
        var first=await db.SessionsSet.OrderBy(x=>x.ScheduledStart).FirstAsync();
        var sessions=With(new SessionsController(db,null!,new NoopAi(),new ConfigurationBuilder().Build(),new Audit(),null!),2);
        Assert.IsType<NoContentResult>(await sessions.Cancel(first.Id,Ct));
        db.ScheduleExceptions.Add(new ScheduleException{AcademicTermId=301,Date=new(2030,9,9),Reason="Configured test holiday"});await db.SaveChangesAsync();
        var edit=request with{Revision=section.Revision,CreationKey=null};
        var preview=JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.PreviewUpdate(section.Id,edit,Ct)).Value);
        Assert.Equal(2,preview.GetProperty("CreateCount").GetInt32());Assert.Equal(3,preview.GetProperty("ReplaceCount").GetInt32());
        Assert.IsType<OkObjectResult>(await controller.Update(section.Id,edit with{PreviewToken=preview.GetProperty("PreviewToken").GetString()},Ct));
        Assert.Equal(2,await db.SessionsSet.CountAsync(x=>x.Status!="CANCELLED"));
        Assert.False((await db.SessionsSet.SingleAsync(x=>x.Id==first.Id)).SupersededByScheduleChange);
        section=await db.ClassSections.SingleAsync();edit=edit with{Revision=section.Revision};
        preview=JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.PreviewUpdate(section.Id,edit,Ct)).Value);
        Assert.Equal(0,preview.GetProperty("CreateCount").GetInt32());
        db.ScheduleExceptions.Add(new ScheduleException{AcademicTermId=301,Date=new(2030,9,16),Reason="New holiday after preview"});await db.SaveChangesAsync();
        Assert.IsType<ConflictObjectResult>(await controller.Update(section.Id,edit with{PreviewToken=preview.GetProperty("PreviewToken").GetString()},Ct));
        Assert.Equal(2,await db.SessionsSet.CountAsync(x=>x.Status!="CANCELLED"));
    }

    [Fact]
    public async Task Appending_a_bell_version_keeps_old_periods_and_requires_schedule_preview_for_future_changes()
    {
        await using var db=await Fixture();var controller=With(new ClassSectionsController(db,new Audit()),1);var request=Request();await controller.Create(request,Ct);var section=await db.ClassSections.SingleAsync();
        var originalStart=await db.SessionsSet.OrderBy(x=>x.ScheduledStart).Select(x=>x.ScheduledStart).FirstAsync();
        var a=await db.BellTimeVersions.SingleAsync(x=>x.Group=="A");
        var periods=(await db.BellPeriods.Where(x=>x.BellTimeVersionId==a.Id).ToListAsync()).Select(x=>new BellPeriodRequest(x.Number,x.Shift,x.StartsAt.AddMinutes(5),x.EndsAt.AddMinutes(5))).ToArray();
        var config=With(new AcademicConfigurationController(db,new Audit()),9,"ADMIN");
        Assert.IsType<OkObjectResult>(await config.AddBellVersion(new("TEST-2029-A","A",new(2029,1,1),null,"ISOLATED TEST EVIDENCE",true,periods),Ct));
        Assert.Equal(new DateOnly(2028,12,31),(await db.BellTimeVersions.SingleAsync(x=>x.Id==a.Id)).EffectiveTo);
        Assert.Equal(16,await db.BellPeriods.CountAsync(x=>x.BellTimeVersionId==a.Id));
        Assert.Equal(originalStart,await db.SessionsSet.OrderBy(x=>x.ScheduledStart).Select(x=>x.ScheduledStart).FirstAsync());
        var preview=JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.PreviewUpdate(section.Id,request with{Revision=1,CreationKey=null},Ct)).Value);
        Assert.Equal(4,preview.GetProperty("CreateCount").GetInt32());Assert.Equal(4,preview.GetProperty("ReplaceCount").GetInt32());
    }

    [Fact]
    public async Task Lecturer_grants_are_per_class_revocable_and_never_allow_deactivation_or_global_student_edits()
    {
        await using var db=await Fixture();var head=With(new ClassSectionsController(db,new Audit()),1);var r=Request();await head.Create(r,Ct);var section=await db.ClassSections.SingleAsync();
        var lecturer=With(new ClassSectionsController(db,new Audit()),2);
        Assert.False((await AccessScope.ClassPermissions(db,lecturer.User,section.Id,Ct)).CanEditDetails);
        Assert.IsType<ForbidResult>(await lecturer.SetStudents(section.Id,new([601]),Ct));
        Assert.IsType<OkObjectResult>(await head.SetGrant(section.Id,102,new(true,true,true,true),Ct));
        Assert.True((await AccessScope.ClassPermissions(db,lecturer.User,section.Id,Ct)).CanEditDetails);
        Assert.IsType<NoContentResult>(await lecturer.SetStudents(section.Id,new([601]),Ct));
        Assert.IsType<NoContentResult>(await lecturer.SetStudentNote(section.Id,601,new("Class-only note"),Ct));
        Assert.Equal("Student",(await db.StudentsSet.SingleAsync()).FullName);
        Assert.IsType<ForbidResult>(await lecturer.Deactivate(section.Id,Ct));
        var students=With(new StudentsController(db,new Audit()),2);
        Assert.IsType<ForbidResult>(await students.List(null,null,false,null,Ct));
        Assert.IsType<ForbidResult>(await students.Update(601,new("001234","Changed","",501,"OTHER"),Ct));
        var other=Request() with {Schedules=[Line() with {IsoDayOfWeek=2}]};await head.Create(other,Ct);var otherId=await db.ClassSections.Where(x=>x.Id!=section.Id).Select(x=>x.Id).SingleAsync();
        Assert.IsType<ForbidResult>(await lecturer.SetStudents(otherId,new([601]),Ct));
        Assert.IsType<OkObjectResult>(await head.SetGrant(section.Id,102,new(false,false,false,false),Ct));
        Assert.IsType<ForbidResult>(await lecturer.SetStudentNote(section.Id,601,new("After revoke"),Ct));
        Assert.IsType<ForbidResult>(await With(new ClassSectionsController(db,new Audit()),3).SetGrant(section.Id,102,new(true,true,true,true),Ct));
    }

    [Fact]
    public async Task Admin_can_assign_existing_dean_with_confirmation_but_cannot_create_teacher()
    {
        await using var db=await Fixture();var admin=With(new ManagementAssignmentsController(db,new Audit()),9,"ADMIN");
        Assert.IsType<ConflictObjectResult>(await admin.TransferFacultyHead(new(102,1,"transfer"),Ct));
        Assert.IsType<OkObjectResult>(await admin.TransferFacultyHead(new(102,1,"transfer",true),Ct));
        var all=await db.ManagementAssignmentsSet.Where(x=>x.FacultyId==1&&x.PositionType=="FACULTY_HEAD").ToArrayAsync();
        Assert.Equal(2,all.Length);Assert.Single(all,x=>x.IsActive);Assert.Contains(all,x=>!x.IsActive&&x.EffectiveTo!=null);
        Assert.Equal(3,await db.Teachers.CountAsync());
        var catalogs=With(new CatalogsController(db,new Audit()),9,"ADMIN","LECTURER");
        Assert.IsType<ForbidResult>(await catalogs.AddTeacher(new("MANUAL","New","x@example.test",null,11,null),Ct));
        var label=JsonSerializer.SerializeToElement(await AuthProfileSupport.Get(db,2,Ct)).GetProperty("PositionLabel").GetString();Assert.Equal("Trưởng khoa",label);
        var active=all.Single(x=>x.IsActive);Assert.IsType<NoContentResult>(await admin.Revoke(active.Id,Ct));
        label=JsonSerializer.SerializeToElement(await AuthProfileSupport.Get(db,2,Ct)).GetProperty("PositionLabel").GetString();Assert.Equal("Giảng viên",label);
    }

    [Fact]
    public async Task Faculty_head_creates_teachers_only_in_own_faculty_and_vacancies_return_after_revoke()
    {
        await using var db=await Fixture();var catalogs=With(new CatalogsController(db,new Audit()),1);
        (await db.DepartmentsSet.FindAsync(11L))!.Code="BM-CNPM";await db.SaveChangesAsync();
        Assert.IsType<ForbidResult>(await catalogs.AddTeacher(new(null,"Outside","x@example.test",null,22,null),Ct));
        Assert.IsType<OkObjectResult>(await catalogs.AddTeacher(new("CLIENT","Inside","x@example.test",null,11,null),Ct));
        Assert.StartsWith("GV-",(await db.Teachers.SingleAsync(x=>x.FullName=="Inside")).TeacherCode);
        var assignments=With(new ManagementAssignmentsController(db,new Audit()),1);
        Assert.Single(Items(await assignments.VacantDepartments(Ct)));
        Assert.IsType<OkObjectResult>(await assignments.AssignDepartmentHead(new(102,11,null),Ct));
        Assert.Empty(Items(await assignments.VacantDepartments(Ct)));
        Assert.IsType<ConflictObjectResult>(await assignments.AssignDepartmentHead(new(102,11,null),Ct));
        var id=await db.ManagementAssignmentsSet.Where(x=>x.PositionType=="DEPARTMENT_HEAD"&&x.IsActive).Select(x=>x.Id).SingleAsync();
        Assert.IsType<NoContentResult>(await assignments.Revoke(id,Ct));Assert.Single(Items(await assignments.VacantDepartments(Ct)));
        Assert.IsType<OkObjectResult>(await assignments.AssignDepartmentHead(new(102,11,null),Ct));
    }

    [Fact]
    public async Task Manual_session_creation_is_preserved_while_scheduled_updates_require_preview_and_list_is_paged()
    {
        await using var db=await Fixture();var classes=With(new ClassSectionsController(db,new Audit()),1);await classes.Create(Request(),Ct);
        var sessions=With(new SessionsController(db,new SessionApiService(),new NoopAi(),new ConfigurationBuilder().Build(),new Audit(),null!),2);
        var r=new CreateSessionRequest(1,401,null,null,501,DateTime.UtcNow,null,[]);
        Assert.IsType<OkObjectResult>(await sessions.Create(r,Ct));Assert.IsType<ConflictObjectResult>(await sessions.Update(1,r,Ct));
        var response=JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await sessions.List(null,null,null,null,Ct,11,1,2)).Value);
        Assert.Equal(4,response.GetProperty("total").GetInt32());Assert.Equal(2,response.GetProperty("items").GetArrayLength());
        var empty=JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await sessions.List(null,null,null,null,Ct,22,1,2)).Value);Assert.Equal(0,empty.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Scheduled_source_configuration_accepts_browser_camera_blocks_time_conflicts_and_start_without_source()
    {
        await using var db=await Fixture();
        db.CamerasSet.Add(new Camera{Id=701,Code="CAM-BROWSER",Name="Laptop browser",RoomId=401,RtspUrl="browser://laptop",IsActive=true});
        await db.SaveChangesAsync();
        var classes=With(new ClassSectionsController(db,new Audit()),1);await classes.Create(Request(),Ct);
        var generated=await db.SessionsSet.OrderBy(x=>x.ScheduledStart).Take(2).ToArrayAsync();
        generated[1].ScheduledStart=generated[0].ScheduledStart;generated[1].ScheduledEnd=generated[0].ScheduledEnd;
        await db.SaveChangesAsync();
        var sessions=With(new SessionsController(db,new SessionApiService(),new NoopAi(),new ConfigurationBuilder().Build(),new Audit(),null!),2);

        Assert.IsType<NoContentResult>(await sessions.ConfigureSource(generated[0].Id,new(701,null),Ct));
        var configured=await db.SessionsSet.AsNoTracking().SingleAsync(x=>x.Id==generated[0].Id);
        Assert.Equal(701,configured.CameraId);Assert.Equal("READY",configured.Status);
        Assert.IsType<ConflictObjectResult>(await sessions.ConfigureSource(generated[1].Id,new(701,null),Ct));
        var missing=Assert.IsType<ConflictObjectResult>(await sessions.Start(generated[1].Id,Ct));
        Assert.Equal("SOURCE_REQUIRED",JsonSerializer.SerializeToElement(missing.Value).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Lookup_search_finds_allowed_results_beyond_first_page_with_vietnamese_normalization()
    {
        await using var db=await Fixture();for(var i=0;i<20;i++)db.Rooms.Add(new Room{Code=$"X{i:00}",Name=i==19?"Phòng thí nghiệm":"Room"});await db.SaveChangesAsync();
        var lookup=With(new LookupsController(db),2);
        var first=JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await lookup.Get("rooms",null,1,10,ct:Ct)).Value);Assert.Equal(10,first.GetProperty("items").GetArrayLength());Assert.True(first.GetProperty("hasMore").GetBoolean());
        var match=JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await lookup.Get("rooms","phong thi nghiem",1,10,ct:Ct)).Value);Assert.Equal("X19",match.GetProperty("items")[0].GetProperty("Code").GetString());
        Assert.IsType<ForbidResult>(await lookup.Get("students",null,classSectionId:999,ct:Ct));
    }

    [Fact]
    public async Task Csv_import_generates_internal_code_while_preserving_source_identifiers_with_leading_zeros()
    {
        await using var db=await Fixture();var controller=With(new StudentsController(db,new Audit()),1);
        var csv="StudentCode,FullName,Email,StudentClassCode,AnonymousCode\n00001234,Imported,import@test.invalid,SC,00000999";
        using var stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));var file=new FormFile(stream,0,stream.Length,"file","students.csv");
        Assert.IsType<OkObjectResult>(await controller.ImportCsv(file,Ct));
        var student=await db.StudentsSet.SingleAsync(x=>x.StudentCode=="00001234");
        Assert.Equal("00000999",student.SourceAnonymousCode);Assert.StartsWith("SV-",student.AnonymousCode);
        using var otherStream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv.Replace("00001234","00001235").Replace("import@test.invalid","other@test.invalid")));
        Assert.IsType<ConflictObjectResult>(await controller.ImportCsv(new FormFile(otherStream,0,otherStream.Length,"file","other.csv"),Ct));
        Assert.Equal(2,await db.StudentsSet.CountAsync());
    }

    [Fact]
    public async Task Academic_configuration_is_limited_to_admin_and_active_faculty_head()
    {
        await using var db=await Fixture();
        static string[] Permissions(object? profile)=>JsonSerializer.SerializeToElement(profile).GetProperty("Permissions").EnumerateArray().Select(x=>x.GetString()!).ToArray();
        Assert.Contains("ACADEMIC_CONFIGURE",Permissions(await AuthProfileSupport.Get(db,1,Ct)));
        Assert.Contains("ACADEMIC_CONFIGURE",Permissions(await AuthProfileSupport.Get(db,9,Ct)));
        Assert.DoesNotContain("ACADEMIC_CONFIGURE",Permissions(await AuthProfileSupport.Get(db,2,Ct)));

        var facultyHead=With(new AcademicConfigurationController(db,new Audit()),1);
        Assert.IsType<OkObjectResult>(await facultyHead.AddException(new(301,null,new(2030,9,3),"Faculty head test"),Ct));
        var admin=With(new AcademicConfigurationController(db,new Audit()),9,"ADMIN");
        Assert.IsType<OkObjectResult>(await admin.AddException(new(301,null,new(2030,9,4),"Admin test"),Ct));

        db.ManagementAssignmentsSet.Add(new ManagementAssignment{TeacherId=102,PositionType="DEPARTMENT_HEAD",DepartmentId=11,EffectiveFrom=DateTime.UtcNow.AddDays(-1),IsActive=true});
        await db.SaveChangesAsync();
        Assert.DoesNotContain("ACADEMIC_CONFIGURE",Permissions(await AuthProfileSupport.Get(db,2,Ct)));
        var departmentHead=With(new AcademicConfigurationController(db,new Audit()),2);
        Assert.IsType<ForbidResult>(await departmentHead.AddException(new(301,null,new(2030,9,5),"Department head denied"),Ct));
    }

    [Fact]
    public async Task Profile_uses_assignments_not_display_names_and_preserves_existing_technical_catalog_access()
    {
        await using var db=await Fixture();(await db.Teachers.SingleAsync(x=>x.Id==102)).FullName="Trưởng khoa Demo";
        db.RolesSet.Add(new Role{Id=3,Name="TECH_AI"});db.UsersSet.Add(new User{Id=10,UserName="tech",FullName="Technical",Status="ACTIVE"});db.UserRoles.Add(new UserRole{UserId=10,RoleId=3});await db.SaveChangesAsync();
        var lecturer=JsonSerializer.SerializeToElement(await AuthProfileSupport.Get(db,2,Ct));Assert.Equal("Giảng viên",lecturer.GetProperty("PositionLabel").GetString());
        var tech=JsonSerializer.SerializeToElement(await AuthProfileSupport.Get(db,10,Ct));var permissions=tech.GetProperty("Permissions").EnumerateArray().Select(x=>x.GetString()).ToArray();
        Assert.Contains("CATALOG_VIEW",permissions);Assert.Contains("CAMERA_TEST",permissions);Assert.DoesNotContain("STUDENT_GLOBAL_VIEW",permissions);Assert.DoesNotContain("CLASS_SECTION_CREATE",permissions);
    }

    private static JsonElement[] Items(ActionResult result) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value).EnumerateArray().ToArray();
    internal static AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    internal static AcademicTerm Term() => new(){Id=301,AcademicYear="2030-2031",Semester=1,StartsOn=new(2030,9,2),EndsOn=new(2030,9,29),DatesConfirmed=true,DivisionPolicy="EQUAL_DAYS",PhasesConfirmed=true,WeekOneMonday=new(2030,9,2),WeekMappingConfirmed=true,Evidence="Isolated test configuration"};
    internal static ScheduleLineRequest Line() => new(Guid.NewGuid().ToString("N"),1,1,3,"A",401,"LT",new(2030,9,2),new(2030,9,29),[]);
    internal static ClassSectionRequest Request() => new("Test class",201,102,301,null,501,null,null,[Line()],CreationKey:Guid.NewGuid().ToString("N"));
    internal static T With<T>(T controller,long userId,params string[] roles) where T:ControllerBase
    {if(roles.Length==0)roles=["LECTURER"];var claims=new[]{new Claim(ClaimTypes.NameIdentifier,userId.ToString())}.Concat(roles.Select(x=>new Claim(ClaimTypes.Role,x)));controller.ControllerContext=new(){HttpContext=new DefaultHttpContext{User=new ClaimsPrincipal(new ClaimsIdentity(claims,"Test"))}};return controller;}
    internal sealed class Audit:IAuditService{public Task WriteAsync(long? userId,string action,string entityType,string entityId,object? metadata=null,CancellationToken ct=default)=>Task.CompletedTask;}
    private sealed class NoopAi:IAiClient
    {
        public Task<StartAiJobResponse> StartAsync(StartAiJobRequest request,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<StopAiJobResponse> StopAsync(string externalJobId,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<StopAiJobResponse> GetJobAsync(string externalJobId,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<AiCapabilitiesResponse> CapabilitiesAsync(CancellationToken ct=default)=>Task.FromResult(new AiCapabilitiesResponse(true,true,true,["test"]));
        public Task<StartFaceEnrollmentResponse> StartFaceEnrollmentAsync(FaceEnrollmentManifest request,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<bool> HealthAsync(CancellationToken ct=default)=>Task.FromResult(true);
        public Task UploadBrowserChunkAsync(string externalJobId,Stream stream,string fileName,string contentType,CancellationToken ct=default)=>Task.CompletedTask;
        public Task<(byte[] Data,string ContentType)?> GetOverlayPreviewAsync(string externalJobId,CancellationToken ct=default)=>Task.FromResult<(byte[] Data,string ContentType)?>(null);
        public Task<string?> FastOverlayAsync(string externalJobId,Stream stream,string fileName,string contentType,CancellationToken ct=default)=>Task.FromResult<string?>(null);
    }
    private sealed class SessionApiService:ISessionService
    {
        public Task<Session> CreateAsync(CreateSessionRequest request,CancellationToken ct)=>Task.FromResult(new Session{Id=900,ClassSectionId=request.ClassSectionId,AttendancePolicyId=request.AttendancePolicyId,ScheduledStart=request.ScheduledStart,Status="READY"});
        public Task<AnalysisJob> StartAsync(long sessionId,string callbackUrl,string callbackApiKey,CancellationToken ct)=>throw new NotSupportedException();
        public Task StopAsync(long sessionId,CancellationToken ct)=>throw new NotSupportedException();
        public Task RetryFinalizeAsync(long sessionId,CancellationToken ct)=>throw new NotSupportedException();
        public Task<IReadOnlyList<long>> ProcessPendingFinalizationsAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<long>>([]);
        public Task<DashboardSnapshot> DashboardAsync(long sessionId,CancellationToken ct)=>throw new NotSupportedException();
    }
    internal static async Task<AppDbContext> Fixture()
    {
        var db=NewDb();db.RolesSet.AddRange(new Role{Id=1,Name="LECTURER"},new Role{Id=2,Name="ADMIN"});
        foreach(var id in new[]{1,2,3,9}){db.UsersSet.Add(new User{Id=id,UserName=$"user{id}",FullName=$"User {id}",Status="ACTIVE"});db.UserRoles.Add(new UserRole{UserId=id,RoleId=id==9?2:1});}
        db.FacultiesSet.AddRange(new Faculty{Id=1,Code="F1",Name="Faculty"},new Faculty{Id=2,Code="F2",Name="Other faculty"});
        db.DepartmentsSet.AddRange(new Department{Id=11,Code="D1",Name="Department",FacultyId=1},new Department{Id=22,Code="D2",Name="Other department",FacultyId=2});
        db.Teachers.AddRange(new Teacher{Id=101,TeacherCode="GV1",FullName="Dean",UserId=1,DepartmentId=11},new Teacher{Id=102,TeacherCode="GV2",FullName="Lecturer",UserId=2,DepartmentId=11},new Teacher{Id=103,TeacherCode="GV3",FullName="Other",UserId=3,DepartmentId=22});
        db.ManagementAssignmentsSet.Add(new ManagementAssignment{TeacherId=101,PositionType="FACULTY_HEAD",FacultyId=1,EffectiveFrom=DateTime.UtcNow.AddDays(-1),IsActive=true});
        db.Courses.Add(new Course{Id=201,Code="MH1",Name="Course",DepartmentId=11,Credits=3});db.AcademicTerms.Add(Term());
        db.Rooms.AddRange(new Room{Id=401,Code="R1",Name="Room",RoomType="THEORY"},new Room{Id=402,Code="R2",Name="Lab",RoomType="LAB"},new Room{Id=403,Code="R3",Name="Zoom",RoomType="ZOOM"});
        db.AttendancePoliciesSet.Add(new AttendancePolicy{Id=501,Code="DEFAULT",IsActive=true});db.StudentClassesSet.Add(new StudentClass{Id=501,Code="SC",Name="Student class",FacultyId=1});
        db.StudentsSet.Add(new Student{Id=601,StudentCode="001234",FullName="Student",StudentClassId=501,AnonymousCode="ANON"});
        await db.SaveChangesAsync();await AcademicSeed.SeedAsync(db);return db;
    }
}
