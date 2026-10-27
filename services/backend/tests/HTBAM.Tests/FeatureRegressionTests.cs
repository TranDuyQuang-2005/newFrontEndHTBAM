using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HTBAM.Api.Controllers;
using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HTBAM.Tests;

public sealed class FeatureRegressionTests
{
    [Theory]
    [InlineData("", "Name", "DISTRACTED", 10)]
    [InlineData("RULE", "Name", "", 10)]
    [InlineData("RULE", "Name", "DISTRACTED", 0)]
    public async Task Alert_rule_rejects_missing_or_invalid_required_values(string code, string name, string behavior, int duration)
    {
        await using var db = CreateDb();
        var controller = WithAdmin(new AlertsController(db, new AuditSpy()));
        var request = new AlertsController.RuleReq(code, name, behavior, duration, .7m, .5m, true, "v1");

        var result = await controller.AddRule(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.AlertRulesSet.ToListAsync());
    }

    [Fact]
    public async Task Alert_rule_accepts_valid_values_and_trims_strings()
    {
        await using var db = CreateDb();
        var controller = WithAdmin(new AlertsController(db, new AuditSpy()));
        var request = new AlertsController.RuleReq(" rule-1 ", " Rule name ", " distracted ", 10, .7m, .5m, true, " v1 ");

        var result = await controller.AddRule(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = await db.AlertRulesSet.SingleAsync();
        Assert.Equal("RULE-1", saved.Code);
        Assert.Equal("Rule name", saved.Name);
        Assert.Equal("DISTRACTED", saved.BehaviorLabel);
        Assert.Equal("v1", saved.Version);
    }

    [Fact]
    public async Task Alert_rule_rejects_every_missing_field_and_out_of_range_score()
    {
        await using var db = CreateDb();
        var controller = WithAdmin(new AlertsController(db, new AuditSpy()));
        AlertsController.RuleReq[] invalid =
        [
            new("RULE", " ", "DISTRACTED", 10, .7m, .5m, true, "v1"),
            new("RULE", "Name", "DISTRACTED", 10, null, .5m, true, "v1"),
            new("RULE", "Name", "DISTRACTED", 10, 1.01m, .5m, true, "v1"),
            new("RULE", "Name", "DISTRACTED", 10, .7m, null, true, "v1"),
            new("RULE", "Name", "DISTRACTED", 10, .7m, -.01m, true, "v1"),
            new("RULE", "Name", "DISTRACTED", 10, .7m, .5m, null, "v1"),
            new("RULE", "Name", "DISTRACTED", 10, .7m, .5m, true, " ")
        ];

        foreach (var request in invalid)
            Assert.IsType<BadRequestObjectResult>(await controller.AddRule(request, CancellationToken.None));

        Assert.Empty(await db.AlertRulesSet.ToListAsync());
    }

    [Fact]
    public async Task Alert_rule_update_accepts_valid_values()
    {
        await using var db = CreateDb();
        db.AlertRulesSet.Add(new AlertRule { Id = 1, Code = "OLD", Name = "Old", BehaviorLabel = "SLEEPY", MinDurationSeconds = 5, MinConfidence = .5m, MinObservationQuality = .5m, Enabled = true, Version = "v1" });
        await db.SaveChangesAsync();
        var controller = WithAdmin(new AlertsController(db, new AuditSpy()));

        var result = await controller.UpdateRule(1, new(" NEW ", " New name ", "PHONE_USE", 8, .8m, .6m, false, "v2"), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var saved = await db.AlertRulesSet.SingleAsync();
        Assert.Equal("NEW", saved.Code);
        Assert.False(saved.Enabled);
    }

    [Fact]
    public async Task Alert_rule_duplicate_code_is_case_insensitive_and_field_specific()
    {
        await using var db = CreateDb();
        db.AlertRulesSet.Add(new AlertRule { Id = 1, Code = "RULE-1", Name = "Existing", BehaviorLabel = "SLEEPY", MinDurationSeconds = 5, Enabled = true, Version = "v1" });
        await db.SaveChangesAsync();
        var controller = WithAdmin(new AlertsController(db, new AuditSpy()));

        var result = Assert.IsType<ConflictObjectResult>(await controller.AddRule(
            new AlertsController.RuleReq(" rule-1 ", "Other", "DISTRACTED", 10, .7m, .5m, true, "v1"),
            CancellationToken.None));

        AssertDuplicate(result, "ALERT_RULE_CODE_DUPLICATE", "code");
    }

    [Fact]
    public async Task Csv_with_utf8_bom_and_five_valid_rows_imports_all_and_maps_class_code()
    {
        await using var db = CreateDb();
        await SeedFacultyHead(db);
        db.StudentClassesSet.Add(new StudentClass { Id = 10, Code = "12DHTH01", Name = "Lớp 12DHTH01", FacultyId = 1, IsActive = true });
        await db.SaveChangesAsync();
        var controller = WithUser(new StudentsController(db, new AuditSpy()), 100, "LECTURER");
        var csv = "\uFEFFStudentCode,FullName,Email,StudentClassCode,AnonymousCode\r\n" +
                  string.Join("\r\n", Enumerable.Range(1, 5).Select(i => $"SV{i:000},Nguyễn Văn {i},sv{i:000}@example.edu.vn,12DHTH01,"));

        var result = await controller.ImportCsv(File(csv), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var students = await db.StudentsSet.OrderBy(x => x.StudentCode).ToListAsync();
        Assert.Equal(5, students.Count);
        Assert.All(students, student => Assert.Equal(10, student.StudentClassId));
        Assert.All(students, student => { Assert.StartsWith("SV-", student.AnonymousCode); Assert.Null(student.SourceAnonymousCode); });
        Assert.Contains(students, student => student.FullName == "Nguyễn Văn 1");
    }

    [Fact]
    public async Task Csv_duplicate_student_code_returns_conflict_without_partial_insert()
    {
        await using var db = CreateDb();
        await SeedFacultyHead(db);
        db.StudentClassesSet.Add(new StudentClass { Id = 10, Code = "12DHTH01", Name = "Class", FacultyId = 1, IsActive = true });
        db.StudentsSet.Add(new Student { Id = 20, StudentCode = "SV001", FullName = "Existing", Email = "old@example.edu.vn", StudentClassId = 10, AnonymousCode = "ANON-OLD" });
        await db.SaveChangesAsync();
        var controller = WithUser(new StudentsController(db, new AuditSpy()), 100, "LECTURER");
        var csv = "StudentCode,FullName,Email,StudentClassCode,AnonymousCode\r\nSV001,Duplicate,new@example.edu.vn,12DHTH01,ANON-NEW\r\nSV002,Valid,sv002@example.edu.vn,12DHTH01,ANON-SV002";

        var result = await controller.ImportCsv(File(csv), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Single(await db.StudentsSet.ToListAsync());
    }

    [Fact]
    public async Task Csv_invalid_class_returns_line_error_without_partial_insert()
    {
        await using var db = CreateDb();
        await SeedFacultyHead(db);
        db.StudentClassesSet.Add(new StudentClass { Id = 10, Code = "12DHTH01", Name = "Class", FacultyId = 1, IsActive = true });
        await db.SaveChangesAsync();
        var controller = WithUser(new StudentsController(db, new AuditSpy()), 100, "LECTURER");
        var csv = "StudentCode,FullName,Email,StudentClassCode,AnonymousCode\r\nSV001,Valid,sv001@example.edu.vn,12DHTH01,ANON-SV001\r\nSV002,Invalid,sv002@example.edu.vn,12DHTH99,ANON-SV002";

        var result = await controller.ImportCsv(File(csv), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await db.StudentsSet.ToListAsync());
    }

    [Fact]
    public async Task Video_preview_returns_backend_stream_url_only_when_object_exists()
    {
        await using var db = CreateDb();
        db.VideosSet.Add(new Video { Id = 1, FileName = "lesson.mp4", StorageRef = "videos/lesson.mp4", ContentType = "video/mp4", Status = "READY", VideoType = "INPUT_UPLOAD", UploadedByUserId = 1 });
        await db.SaveChangesAsync();
        var storage = new FakeStorage { Exists = true };
        var controller = WithUser(new VideosController(db, storage, new AuditSpy(), EmptyConfiguration(), NullLogger<VideosController>.Instance), 1, "TECH_AI");

        var result = await controller.Preview(1, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("videos/lesson.mp4", storage.LastStatKey);
        var payload = Assert.IsType<OkObjectResult>(result).Value!;
        var url = Assert.IsType<string>(payload.GetType().GetProperty("url")!.GetValue(payload));
        Assert.StartsWith("/api/videos/1/stream?token=", url);
        Assert.DoesNotContain("videos/lesson.mp4", url);
    }

    [Fact]
    public async Task Video_stream_returns_partial_content_with_exact_range_headers()
    {
        await using var db = CreateDb();
        var bytes = Encoding.UTF8.GetBytes("0123456789");
        db.VideosSet.Add(new Video { Id = 1, FileName = "lesson.mp4", StorageRef = "videos/private/lesson.mp4", ContentType = "video/mp4", SizeBytes = bytes.Length, Status = "READY", VideoType = "INPUT_UPLOAD", UploadedByUserId = 1 });
        await db.SaveChangesAsync();
        var storage = new FakeStorage { Exists = true, Bytes = bytes };
        var controller = WithUser(new VideosController(db, storage, new AuditSpy(), EmptyConfiguration(), NullLogger<VideosController>.Instance), 1, "TECH_AI");
        var preview = Assert.IsType<OkObjectResult>(await controller.Preview(1, CancellationToken.None));
        var url = (string)preview.Value!.GetType().GetProperty("url")!.GetValue(preview.Value)!;
        var token = Uri.UnescapeDataString(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        var body = new MemoryStream();
        controller.HttpContext.Response.Body = body;
        controller.HttpContext.Request.Method = HttpMethods.Get;
        controller.HttpContext.Request.Headers.Range = "bytes=2-6";

        Assert.IsType<EmptyResult>(await controller.Stream(1, token, CancellationToken.None));

        Assert.Equal(StatusCodes.Status206PartialContent, controller.HttpContext.Response.StatusCode);
        Assert.Equal("video/mp4", controller.HttpContext.Response.ContentType);
        Assert.Equal(5, controller.HttpContext.Response.ContentLength);
        Assert.Equal("bytes", controller.HttpContext.Response.Headers.AcceptRanges);
        Assert.Equal("bytes 2-6/10", controller.HttpContext.Response.Headers.ContentRange);
        Assert.Equal(2, storage.LastOffset);
        Assert.Equal(5, storage.LastLength);
        Assert.Equal("23456", Encoding.UTF8.GetString(body.ToArray()));

        var fullBody = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(1, "TECH_AI") } };
        controller.HttpContext.Request.Method = HttpMethods.Get;
        controller.HttpContext.Response.Body = fullBody;
        Assert.IsType<EmptyResult>(await controller.Stream(1, token, CancellationToken.None));
        Assert.Equal(StatusCodes.Status200OK, controller.HttpContext.Response.StatusCode);
        Assert.Equal(bytes.Length, controller.HttpContext.Response.ContentLength);
        Assert.Equal(bytes, fullBody.ToArray());
    }

    [Fact]
    public async Task Video_preview_returns_not_found_when_object_was_removed()
    {
        await using var db = CreateDb();
        db.VideosSet.Add(new Video { Id = 1, FileName = "missing.mp4", StorageRef = "videos/missing.mp4", ContentType = "video/mp4", Status = "READY", VideoType = "INPUT_UPLOAD", UploadedByUserId = 1 });
        await db.SaveChangesAsync();
        var controller = WithUser(new VideosController(db, new FakeStorage { Exists = false }, new AuditSpy(), EmptyConfiguration(), NullLogger<VideosController>.Instance), 1, "TECH_AI");

        var result = await controller.Preview(1, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Video_duplicate_content_returns_precise_conflict()
    {
        await using var db = CreateDb();
        var bytes = Encoding.UTF8.GetBytes("same-video-content");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        db.VideosSet.Add(new Video { Id = 1, FileName = "existing.mp4", StorageRef = "videos/existing.mp4", ContentType = "video/mp4", SizeBytes = bytes.Length, Sha256 = sha, Status = "READY", VideoType = "INPUT_UPLOAD", UploadedByUserId = 1 });
        await db.SaveChangesAsync();
        var controller = WithUser(new VideosController(db, new FakeStorage(), new AuditSpy(), EmptyConfiguration(), NullLogger<VideosController>.Instance), 1, "LECTURER");

        var result = Assert.IsType<ConflictObjectResult>(await controller.Upload(VideoFile(bytes), CancellationToken.None));

        AssertDuplicate(result, "VIDEO_CONTENT_DUPLICATE", "file", "Video này trùng nội dung với một video đã được tải lên trước đó.");
    }

    [Fact]
    public async Task Catalog_deactivation_succeeds_without_dependencies_and_conflicts_when_referenced()
    {
        await using var db = CreateDb();
        await SeedFacultyHead(db);
        db.StudentClassesSet.AddRange(
            new StudentClass { Id = 1, Code = "EMPTY", Name = "Empty", FacultyId = 1, IsActive = true },
            new StudentClass { Id = 2, Code = "USED", Name = "Used", FacultyId = 1, IsActive = true });
        db.StudentsSet.Add(new Student { Id = 10, StudentCode = "SV010", FullName = "Student", StudentClassId = 2, AnonymousCode = "ANON-10" });
        await db.SaveChangesAsync();
        var controller = WithUser(new CatalogsController(db, new AuditSpy()), 100, "LECTURER");

        var success = await controller.DeactivateCatalog("student-classes", 1, CancellationToken.None);
        var conflict = await controller.DeactivateCatalog("student-classes", 2, CancellationToken.None);

        Assert.IsType<NoContentResult>(success);
        Assert.False((await db.StudentClassesSet.FindAsync(1L))!.IsActive);
        Assert.IsType<ConflictObjectResult>(conflict);
        Assert.True((await db.StudentClassesSet.FindAsync(2L))!.IsActive);
    }

    [Fact]
    public async Task Admin_cannot_deactivate_academic_catalog()
    {
        await using var db = CreateDb();
        db.FacultiesSet.Add(new Faculty { Id = 1, Code = "F", Name = "Faculty" });
        db.StudentClassesSet.Add(new StudentClass { Id = 1, Code = "C", Name = "Class", FacultyId = 1 });
        await db.SaveChangesAsync();
        var controller = WithAdmin(new CatalogsController(db, new AuditSpy()));

        Assert.IsType<ForbidResult>(await controller.DeactivateCatalog("student-classes", 1, CancellationToken.None));
    }

    [Fact]
    public async Task Admin_does_not_bypass_session_business_scope()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        var admin = Principal(90, "ADMIN");

        Assert.False(await HTBAM.Api.Support.AccessScope.CanViewSessionAsync(db, admin, ids.SessionA, CancellationToken.None));
        Assert.False(await HTBAM.Api.Support.AccessScope.CanOperateSessionAsync(db, admin, ids.SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task Admin_has_no_video_business_scope()
    {
        await using var db = CreateDb();
        db.VideosSet.Add(new Video
        {
            Id = 91,
            FileName = "academic.mp4",
            StorageRef = "videos/academic.mp4",
            ContentType = "video/mp4",
            Status = "READY",
            VideoType = "INPUT_UPLOAD",
            UploadedByUserId = 90
        });
        await db.SaveChangesAsync();

        var admin = Principal(90, "ADMIN");

        Assert.False(await AccessScope.CanViewVideoAsync(
            db,
            admin,
            91,
            CancellationToken.None));
    }

    [Fact]
    public async Task Admin_with_linked_teacher_record_still_has_no_academic_scope()
    {
        await using var db = CreateDb();
        db.UsersSet.Add(new User { Id = 901, UserName = "admin-teacher", FullName = "Admin Teacher", Status = "ACTIVE" });
        db.Teachers.Add(new Teacher { Id = 902, UserId = 901, TeacherCode = "GV-ADMIN", FullName = "Admin Teacher", IsActive = true });
        await db.SaveChangesAsync();

        var admin = Principal(901, "ADMIN", "LECTURER");

        Assert.Equal(-1, await AccessScope.TeacherIdAsync(db, admin, CancellationToken.None));
    }

    [Fact]
    public async Task Lecturer_sees_own_session_but_not_other_lecturer_session()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);

        Assert.True(await HTBAM.Api.Support.AccessScope.CanViewSessionAsync(db, Principal(ids.UserA, "LECTURER"), ids.SessionA, CancellationToken.None));
        Assert.False(await HTBAM.Api.Support.AccessScope.CanViewSessionAsync(db, Principal(ids.UserA, "LECTURER"), ids.SessionB, CancellationToken.None));
    }

    [Fact]
    public async Task Substitute_operates_active_substitution_and_original_only_views_reports()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.SessionSubstitutionsSet.Add(new SessionSubstitution { SessionId = ids.SessionA, OriginalTeacherId = ids.TeacherA, SubstituteTeacherId = ids.TeacherB, Reason = "Busy", Status = "ACTIVE" });
        await db.SaveChangesAsync();

        Assert.True(await HTBAM.Api.Support.AccessScope.CanViewSessionAsync(db, Principal(ids.UserA, "LECTURER"), ids.SessionA, CancellationToken.None));
        Assert.True(await HTBAM.Api.Support.AccessScope.CanExportSessionReportAsync(db, Principal(ids.UserA, "LECTURER"), ids.SessionA, CancellationToken.None));
        Assert.False(await HTBAM.Api.Support.AccessScope.CanOperateSessionAsync(db, Principal(ids.UserA, "LECTURER"), ids.SessionA, CancellationToken.None));
        Assert.True(await HTBAM.Api.Support.AccessScope.CanViewSessionAsync(db, Principal(ids.UserB, "LECTURER"), ids.SessionA, CancellationToken.None));
        Assert.True(await HTBAM.Api.Support.AccessScope.CanOperateSessionAsync(db, Principal(ids.UserB, "LECTURER"), ids.SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task Department_head_assigns_substitute_while_regular_lecturer_and_original_teacher_are_rejected()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.ManagementAssignmentsSet.Add(new ManagementAssignment
        {
            TeacherId = ids.HeadTeacher,
            PositionType = "DEPARTMENT_HEAD",
            DepartmentId = ids.Department1,
            IsActive = true
        });
        await db.SaveChangesAsync();

        var lecturer = WithUser(new SessionsController(db, null!, TestAi(), EmptyConfiguration(), new AuditSpy(), null!), ids.UserA, "LECTURER");
        Assert.IsType<ForbidResult>(await lecturer.AssignSubstitution(
            ids.SessionA,
            new(ids.TeacherB, "Không có quyền", null),
            CancellationToken.None));

        var departmentHead = WithUser(new SessionsController(db, null!, TestAi(), EmptyConfiguration(), new AuditSpy(), null!), ids.HeadUser, "LECTURER");
        Assert.IsType<BadRequestObjectResult>(await departmentHead.AssignSubstitution(
            ids.SessionA,
            new(ids.TeacherA, "Không được tự thay chính mình", null),
            CancellationToken.None));

        Assert.IsType<OkObjectResult>(await departmentHead.AssignSubstitution(
            ids.SessionA,
            new(ids.TeacherB, "Phân công hợp lệ", "Kiểm thử hồi quy"),
            CancellationToken.None));

        var substitution = await db.SessionSubstitutionsSet.SingleAsync();
        Assert.Equal("ACTIVE", substitution.Status);
        Assert.Equal(ids.TeacherA, substitution.OriginalTeacherId);
        Assert.Equal(ids.TeacherB, substitution.SubstituteTeacherId);
        Assert.False(await AccessScope.CanOperateSessionAsync(db, Principal(ids.UserA, "LECTURER"), ids.SessionA, CancellationToken.None));
        Assert.True(await AccessScope.CanOperateSessionAsync(db, Principal(ids.UserB, "LECTURER"), ids.SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task Completed_substitute_keeps_view_and_report_scope_but_cannot_operate()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        (await db.SessionsSet.FindAsync(ids.SessionA))!.Status = "COMPLETED";
        db.SessionSubstitutionsSet.Add(new SessionSubstitution { SessionId = ids.SessionA, OriginalTeacherId = ids.TeacherA, SubstituteTeacherId = ids.TeacherB, Reason = "Đã dạy thay", Status = "COMPLETED", CompletedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var substitute = Principal(ids.UserB, "LECTURER");
        Assert.True(await AccessScope.CanViewSessionAsync(db, substitute, ids.SessionA, CancellationToken.None));
        Assert.True(await AccessScope.CanExportSessionReportAsync(db, substitute, ids.SessionA, CancellationToken.None));
        Assert.False(await AccessScope.CanOperateSessionAsync(db, substitute, ids.SessionA, CancellationToken.None));
        Assert.True(await AccessScope.CanViewSessionAsync(db, Principal(ids.UserA, "LECTURER"), ids.SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_substitution_before_start_restores_original_operation_and_removes_substitute_scope()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "DEPARTMENT_HEAD", DepartmentId = ids.Department1, IsActive = true });
        db.SessionSubstitutionsSet.Add(new SessionSubstitution { SessionId = ids.SessionA, OriginalTeacherId = ids.TeacherA, SubstituteTeacherId = ids.TeacherB, Reason = "Bận", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var controller = WithUser(new SessionsController(db, null!, TestAi(), EmptyConfiguration(), new AuditSpy(), null!), ids.HeadUser, "LECTURER");

        Assert.IsType<NoContentResult>(await controller.CancelSubstitution(ids.SessionA, CancellationToken.None));
        Assert.True(await AccessScope.CanOperateSessionAsync(db, Principal(ids.UserA, "LECTURER"), ids.SessionA, CancellationToken.None));
        Assert.False(await AccessScope.CanViewSessionAsync(db, Principal(ids.UserB, "LECTURER"), ids.SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_substitution_after_session_started_is_rejected()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        (await db.SessionsSet.FindAsync(ids.SessionA))!.Status = "RUNNING";
        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "DEPARTMENT_HEAD", DepartmentId = ids.Department1, IsActive = true });
        db.SessionSubstitutionsSet.Add(new SessionSubstitution { SessionId = ids.SessionA, OriginalTeacherId = ids.TeacherA, SubstituteTeacherId = ids.TeacherB, Reason = "Bận", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var controller = WithUser(new SessionsController(db, null!, TestAi(), EmptyConfiguration(), new AuditSpy(), null!), ids.HeadUser, "LECTURER");

        Assert.IsType<ConflictObjectResult>(await controller.CancelSubstitution(ids.SessionA, CancellationToken.None));
        Assert.Equal("ACTIVE", (await db.SessionSubstitutionsSet.SingleAsync()).Status);
    }

    [Fact]
    public async Task Substitute_can_view_session_student_but_cannot_edit_student_record()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.StudentClassesSet.Add(new StudentClass { Id = 701, Code = "SC1", Name = "Student class", FacultyId = ids.Faculty1, IsActive = true });
        db.StudentsSet.Add(new Student { Id = 702, StudentCode = "SV702", FullName = "Student", Email = "student@example.test", StudentClassId = 701, AnonymousCode = "ANON-702", IsActive = true });
        db.SessionStudentsSet.Add(new SessionStudent { SessionId = ids.SessionA, StudentId = 702 });
        db.SessionSubstitutionsSet.Add(new SessionSubstitution { SessionId = ids.SessionA, OriginalTeacherId = ids.TeacherA, SubstituteTeacherId = ids.TeacherB, Reason = "Busy", Status = "ACTIVE" });
        await db.SaveChangesAsync();

        var substitute = Principal(ids.UserB, "LECTURER");
        Assert.True(await HTBAM.Api.Support.AccessScope.CanViewStudentAsync(db, substitute, 702, CancellationToken.None));
        Assert.False(await HTBAM.Api.Support.AccessScope.CanManageStudentAsync(db, substitute, 702, CancellationToken.None));
    }

    [Fact]
    public async Task Admin_cannot_create_business_student()
    {
        await using var db = CreateDb();
        db.FacultiesSet.Add(new Faculty { Id = 1, Code = "F", Name = "Faculty" });
        db.StudentClassesSet.Add(new StudentClass { Id = 1, Code = "SC", Name = "Class", FacultyId = 1, IsActive = true });
        await db.SaveChangesAsync();
        var controller = WithAdmin(new StudentsController(db, new AuditSpy()));
        var request = new UpsertStudentRequest("SV1", "Student", "student@example.test", 1, "ANON-1", true);

        Assert.IsType<ForbidResult>(await controller.Create(request, CancellationToken.None));
    }

    [Fact]
    public async Task Admin_cannot_change_academic_data_but_can_read_system_report()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        var sessionRequest = new CreateSessionRequest(401, null, 1, null, 501, DateTime.UtcNow.AddDays(5), DateTime.UtcNow.AddDays(5).AddHours(2), [], "DEFAULT");
        var sessions = WithAdmin(new SessionsController(db, null!, TestAi(), EmptyConfiguration(), new AuditSpy(), null!));
        var reports = WithAdmin(new ReportsController(db, null!));
        var catalogs = WithAdmin(new CatalogsController(db, new AuditSpy()));

        Assert.IsType<ForbidResult>(await sessions.Create(sessionRequest, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await reports.Session(ids.SessionA, CancellationToken.None));
        Assert.IsType<ForbidResult>(await catalogs.SetRoster(401, new CatalogsController.RosterReq([]), CancellationToken.None));
    }

    [Fact]
    public async Task Lecturer_cannot_modify_student_outside_own_scope()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.StudentClassesSet.Add(new StudentClass { Id = 701, Code = "SC1", Name = "Student class", FacultyId = ids.Faculty2, IsActive = true });
        db.StudentsSet.Add(new Student { Id = 702, StudentCode = "SV702", FullName = "Student", Email = "student@example.test", StudentClassId = 701, AnonymousCode = "ANON-702", IsActive = true });
        db.Enrollments.Add(new Enrollment { ClassSectionId = 403, StudentId = 702 });
        await db.SaveChangesAsync();
        var controller = WithUser(new StudentsController(db, new AuditSpy()), ids.UserA, "LECTURER");
        var request = new UpsertStudentRequest("SV702", "Changed", "student@example.test", 701, "ANON-702", true);

        Assert.IsType<ForbidResult>(await controller.Update(702, request, CancellationToken.None));
    }

    [Fact]
    public async Task Substitution_rejects_second_active_assignment()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "DEPARTMENT_HEAD", DepartmentId = ids.Department1, IsActive = true });
        db.SessionSubstitutionsSet.Add(new SessionSubstitution { SessionId = ids.SessionA, OriginalTeacherId = ids.TeacherA, SubstituteTeacherId = ids.TeacherB, Reason = "Existing", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var controller = WithUser(new SessionsController(db, null!, TestAi(), EmptyConfiguration(), new AuditSpy(), null!), ids.HeadUser, "LECTURER");

        Assert.IsType<ConflictObjectResult>(await controller.AssignSubstitution(ids.SessionA, new(ids.TeacherB, "Again", null), CancellationToken.None));
    }

    [Fact]
    public async Task Substitution_rejects_inactive_teacher_and_inactive_account()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "DEPARTMENT_HEAD", DepartmentId = ids.Department1, IsActive = true });
        await db.SaveChangesAsync();
        var controller = WithUser(new SessionsController(db, null!, TestAi(), EmptyConfiguration(), new AuditSpy(), null!), ids.HeadUser, "LECTURER");

        (await db.Teachers.FindAsync(ids.TeacherB))!.IsActive = false;
        await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await controller.AssignSubstitution(ids.SessionA, new(ids.TeacherB, "Inactive teacher", null), CancellationToken.None));

        (await db.Teachers.FindAsync(ids.TeacherB))!.IsActive = true;
        (await db.UsersSet.FindAsync(ids.UserB))!.Status = "LOCKED";
        await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await controller.AssignSubstitution(ids.SessionA, new(ids.TeacherB, "Inactive account", null), CancellationToken.None));
    }

    [Fact]
    public async Task Substitution_rejects_teacher_schedule_conflict()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "DEPARTMENT_HEAD", DepartmentId = ids.Department1, IsActive = true });
        var target = await db.SessionsSet.FindAsync(ids.SessionA);
        db.SessionsSet.Add(new Session { Id = 604, ClassSectionId = 402, OriginalTeacherId = ids.TeacherB, AttendancePolicyId = 501, ScheduledStart = target!.ScheduledStart.AddMinutes(30), ScheduledEnd = target.ScheduledEnd, Status = "READY" });
        await db.SaveChangesAsync();
        var controller = WithUser(new SessionsController(db, null!, TestAi(), EmptyConfiguration(), new AuditSpy(), null!), ids.HeadUser, "LECTURER");

        Assert.IsType<ConflictObjectResult>(await controller.AssignSubstitution(ids.SessionA, new(ids.TeacherB, "Conflict", null), CancellationToken.None));
    }

    [Fact]
    public async Task Department_head_scope_is_limited_to_managed_department()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "DEPARTMENT_HEAD", DepartmentId = ids.Department1, IsActive = true });
        await db.SaveChangesAsync();

        Assert.True(await HTBAM.Api.Support.AccessScope.CanViewSessionAsync(db, Principal(ids.HeadUser, "LECTURER"), ids.SessionA, CancellationToken.None));
        Assert.False(await HTBAM.Api.Support.AccessScope.CanViewSessionAsync(db, Principal(ids.HeadUser, "LECTURER"), ids.SessionC, CancellationToken.None));
    }

    [Fact]
    public async Task Faculty_head_scope_is_limited_to_managed_faculty()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "FACULTY_HEAD", FacultyId = ids.Faculty1, IsActive = true });
        await db.SaveChangesAsync();

        Assert.True(await HTBAM.Api.Support.AccessScope.CanViewSessionAsync(db, Principal(ids.HeadUser, "LECTURER"), ids.SessionA, CancellationToken.None));
        Assert.True(await HTBAM.Api.Support.AccessScope.CanViewSessionAsync(db, Principal(ids.HeadUser, "LECTURER"), ids.SessionB, CancellationToken.None));
        Assert.False(await HTBAM.Api.Support.AccessScope.CanViewSessionAsync(db, Principal(ids.HeadUser, "LECTURER"), ids.SessionC, CancellationToken.None));
    }

    [Fact]
    public async Task Report_options_and_detail_enforce_admin_faculty_department_and_lecturer_substitute_scopes()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.ManagementAssignmentsSet.AddRange(
            new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "FACULTY_HEAD", FacultyId = ids.Faculty1, IsActive = true, EffectiveFrom = DateTime.UtcNow.AddDays(-1) },
            new ManagementAssignment { TeacherId = ids.TeacherB, PositionType = "DEPARTMENT_HEAD", DepartmentId = ids.Department1, IsActive = true, EffectiveFrom = DateTime.UtcNow.AddDays(-1) });
        db.SessionSubstitutionsSet.Add(new SessionSubstitution { SessionId = ids.SessionC, OriginalTeacherId = ids.TeacherC, SubstituteTeacherId = ids.TeacherA, Reason = "Dạy thay", Status = "ACTIVE" });
        await db.SaveChangesAsync();

        var admin = WithAdmin(new ReportsController(db, null!));
        var facultyHead = WithUser(new ReportsController(db, null!), ids.HeadUser, "LECTURER");
        var departmentHead = WithUser(new ReportsController(db, null!), ids.UserB, "LECTURER");
        var lecturer = WithUser(new ReportsController(db, null!), ids.UserA, "LECTURER");

        Assert.Equal([ids.SessionA, ids.SessionB, ids.SessionC], ResultIds(await admin.Options("sessions", CancellationToken.None)));
        Assert.Equal([ids.SessionA, ids.SessionB], ResultIds(await facultyHead.Options("sessions", CancellationToken.None)));
        Assert.Equal([ids.SessionA, ids.SessionB], ResultIds(await departmentHead.Options("sessions", CancellationToken.None)));
        Assert.Equal([ids.SessionA, ids.SessionC], ResultIds(await lecturer.Options("sessions", CancellationToken.None)));

        Assert.IsType<OkObjectResult>(await admin.Session(ids.SessionC, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await facultyHead.Session(ids.SessionB, CancellationToken.None));
        Assert.IsType<ForbidResult>(await facultyHead.Session(ids.SessionC, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await departmentHead.Session(ids.SessionA, CancellationToken.None));
        Assert.IsType<ForbidResult>(await departmentHead.Session(ids.SessionC, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await lecturer.Session(ids.SessionC, CancellationToken.None));
        Assert.IsType<ForbidResult>(await lecturer.Session(ids.SessionB, CancellationToken.None));
    }

    [Fact]
    public async Task Faculty_head_can_manage_unenrolled_students_only_in_managed_faculty()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.ManagementAssignmentsSet.Add(new ManagementAssignment
        {
            TeacherId = ids.HeadTeacher,
            PositionType = "FACULTY_HEAD",
            FacultyId = ids.Faculty1,
            IsActive = true,
        });
        db.StudentClassesSet.AddRange(
            new StudentClass { Id = 701, Code = "SC-F1", Name = "Faculty 1", FacultyId = ids.Faculty1, IsActive = true },
            new StudentClass { Id = 702, Code = "SC-F2", Name = "Faculty 2", FacultyId = ids.Faculty2, IsActive = true });
        db.StudentsSet.AddRange(
            new Student { Id = 801, StudentCode = "SV-F1", FullName = "Managed", StudentClassId = 701, AnonymousCode = "ANON-F1", IsActive = true },
            new Student { Id = 802, StudentCode = "SV-F2", FullName = "Outside", StudentClassId = 702, AnonymousCode = "ANON-F2", IsActive = true });
        await db.SaveChangesAsync();

        var principal = Principal(ids.HeadUser, "LECTURER");
        Assert.True(await AccessScope.CanViewStudentAsync(db, principal, 801, CancellationToken.None));
        Assert.True(await AccessScope.CanManageStudentAsync(db, principal, 801, CancellationToken.None));
        Assert.False(await AccessScope.CanViewStudentAsync(db, principal, 802, CancellationToken.None));
        Assert.False(await AccessScope.CanManageStudentAsync(db, principal, 802, CancellationToken.None));

        var controller = WithUser(new StudentsController(db, new AuditSpy()), ids.HeadUser, "LECTURER");
        var result = Assert.IsType<OkObjectResult>(await controller.List(null, null, false, null, CancellationToken.None));
        var rows = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).ToArray();
        var row = Assert.Single(rows);
        Assert.Equal(801L, row.GetType().GetProperty("Id")!.GetValue(row));
        Assert.True((bool)row.GetType().GetProperty("CanManage")!.GetValue(row)!);
    }

    [Fact]
    public async Task Locked_lecturer_account_has_no_academic_scope()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        (await db.UsersSet.FindAsync(ids.UserA))!.Status = "LOCKED";
        await db.SaveChangesAsync();

        Assert.False(await AccessScope.CanViewSessionAsync(db, Principal(ids.UserA, "LECTURER"), ids.SessionA, CancellationToken.None));
        Assert.False(await AccessScope.CanOperateSessionAsync(db, Principal(ids.UserA, "LECTURER"), ids.SessionA, CancellationToken.None));
    }

    [Fact]
    public async Task Video_scope_follows_uploader_session_substitution_department_and_faculty()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.VideosSet.AddRange(
            new Video { Id = 701, FileName = "a.mp4", StorageRef = "videos/a.mp4", Sha256 = new string('a', 64), Status = "READY", VideoType = "INPUT_UPLOAD", UploadedByUserId = ids.UserA },
            new Video { Id = 702, FileName = "b.mp4", StorageRef = "videos/b.mp4", Sha256 = new string('b', 64), Status = "READY", VideoType = "INPUT_UPLOAD", UploadedByUserId = ids.UserB },
            new Video { Id = 703, FileName = "a-annotations.mp4", StorageRef = "annotations/a.mp4", Sha256 = new string('c', 64), Status = "READY", VideoType = "ANNOTATED_OUTPUT", SessionId = ids.SessionA, ParentVideoId = 701, IsSystemGenerated = true },
            new Video { Id = 704, FileName = "a-unassigned.mp4", StorageRef = "videos/a-unassigned.mp4", Sha256 = new string('d', 64), Status = "READY", VideoType = "INPUT_UPLOAD", UploadedByUserId = ids.UserA },
            new Video { Id = 705, FileName = "c-unassigned.mp4", StorageRef = "videos/c-unassigned.mp4", Sha256 = new string('e', 64), Status = "READY", VideoType = "INPUT_UPLOAD", UploadedByUserId = ids.UserC });
        (await db.SessionsSet.FindAsync(ids.SessionA))!.VideoId = 701;
        (await db.SessionsSet.FindAsync(ids.SessionB))!.VideoId = 702;
        db.SessionSubstitutionsSet.Add(new SessionSubstitution { SessionId = ids.SessionA, OriginalTeacherId = ids.TeacherA, SubstituteTeacherId = ids.TeacherB, Reason = "Đã dạy thay", Status = "COMPLETED", CompletedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        Assert.False(await AccessScope.CanViewVideoAsync(db, Principal(ids.UserA, "LECTURER"), 702, CancellationToken.None));
        Assert.True(await AccessScope.CanViewVideoAsync(db, Principal(ids.UserA, "LECTURER"), 703, CancellationToken.None));
        Assert.True(await AccessScope.CanViewVideoAsync(db, Principal(ids.UserB, "LECTURER"), 703, CancellationToken.None));

        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "DEPARTMENT_HEAD", DepartmentId = ids.Department1, IsActive = true });
        await db.SaveChangesAsync();
        Assert.True(await AccessScope.CanViewVideoAsync(db, Principal(ids.HeadUser, "LECTURER"), 701, CancellationToken.None));
        Assert.True(await AccessScope.CanViewVideoAsync(db, Principal(ids.HeadUser, "LECTURER"), 704, CancellationToken.None));
        Assert.False(await AccessScope.CanViewVideoAsync(db, Principal(ids.HeadUser, "LECTURER"), 702, CancellationToken.None));
        Assert.False(await AccessScope.CanViewVideoAsync(db, Principal(ids.HeadUser, "LECTURER"), 705, CancellationToken.None));

        db.ManagementAssignmentsSet.RemoveRange(db.ManagementAssignmentsSet);
        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "FACULTY_HEAD", FacultyId = ids.Faculty1, IsActive = true });
        await db.SaveChangesAsync();
        Assert.True(await AccessScope.CanViewVideoAsync(db, Principal(ids.HeadUser, "LECTURER"), 702, CancellationToken.None));
        Assert.True(await AccessScope.CanViewVideoAsync(db, Principal(ids.HeadUser, "LECTURER"), 704, CancellationToken.None));
        Assert.False(await AccessScope.CanViewVideoAsync(db, Principal(ids.HeadUser, "LECTURER"), 705, CancellationToken.None));
        Assert.False(await AccessScope.CanViewVideoAsync(db, Principal(ids.UserC, "LECTURER"), 703, CancellationToken.None));
    }

    [Fact]
    public async Task Finalize_persists_real_annotation_once_and_completes_substitution_on_retry()
    {
        await using var db = CreateDb();
        db.SessionsSet.Add(new Session { Id = 1, ClassSectionId = 1, OriginalTeacherId = 10, AttendancePolicyId = 1, ScheduledStart = DateTime.UtcNow.AddHours(-1), StartedAt = DateTime.UtcNow.AddMinutes(-30), Status = "RUNNING" });
        db.AnalysisJobsSet.Add(new AnalysisJob { Id = 2, SessionId = 1, CorrelationId = "corr", ExternalJobId = "job", Status = "RUNNING" });
        db.SessionSubstitutionsSet.Add(new SessionSubstitution { Id = 3, SessionId = 1, OriginalTeacherId = 10, SubstituteTeacherId = 11, Reason = "Bận", Status = "ACTIVE" });
        await db.SaveChangesAsync();
        var artifact = new AiArtifactMetadata("annotations/sessions/1/corr.mp4", "annotated.mp4", "video/mp4", 1234, new string('a', 64));
        var summary = new FlakySummary();
        var service = new SessionService(db, new FakeAi(artifact), summary, new FakeStorage { Exists = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StopAsync(1, CancellationToken.None));
        Assert.Single(await db.VideosSet.Where(x => x.VideoType == "ANNOTATED_OUTPUT").ToListAsync());
        Assert.Equal("FINALIZE_FAILED", (await db.SessionsSet.FindAsync(1L))!.Status);
        Assert.Null((await db.SessionsSet.FindAsync(1L))!.EndedAt);

        await service.RetryFinalizeAsync(1, CancellationToken.None);
        Assert.Equal([1L], await service.ProcessPendingFinalizationsAsync(CancellationToken.None));

        Assert.Single(await db.VideosSet.Where(x => x.VideoType == "ANNOTATED_OUTPUT").ToListAsync());
        Assert.Equal("COMPLETED", (await db.SessionsSet.FindAsync(1L))!.Status);
        var substitution = await db.SessionSubstitutionsSet.SingleAsync();
        Assert.Equal("COMPLETED", substitution.Status);
        Assert.NotNull(substitution.CompletedAt);
    }

    [Fact]
    public async Task Faculty_head_cannot_transfer_own_position_without_admin()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "FACULTY_HEAD", FacultyId = ids.Faculty1, IsActive = true });
        await db.SaveChangesAsync();
        var controller = WithUser(new ManagementAssignmentsController(db, new AuditSpy()), ids.HeadUser, "LECTURER");

        var result = await controller.TransferFacultyHead(new ManagementAssignmentsController.AssignFacultyHeadRequest(ids.TeacherA, ids.Faculty1, "transfer"), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        var all = await db.ManagementAssignmentsSet.Where(x => x.PositionType == "FACULTY_HEAD" && x.FacultyId == ids.Faculty1).ToListAsync();
        Assert.Single(all);
        Assert.Single(all, x => x.IsActive);
    }

    [Fact]
    public async Task Catalog_departments_endpoint_limits_lecturer_to_own_department()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        var controller = WithUser(new CatalogsController(db, new AuditSpy()), ids.UserA, "LECTURER");

        var result = await controller.Departments(CancellationToken.None);

        Assert.Equal([ids.Department1], ResultIds(result));
    }

    [Fact]
    public async Task Catalog_departments_endpoint_resolves_department_head_scope()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "DEPARTMENT_HEAD", DepartmentId = ids.Department1, IsActive = true });
        await db.SaveChangesAsync();
        var controller = WithUser(new CatalogsController(db, new AuditSpy()), ids.HeadUser, "LECTURER");

        var result = await controller.Departments(CancellationToken.None);

        Assert.Equal([ids.Department1], ResultIds(result));
    }

    [Fact]
    public async Task Catalog_departments_endpoint_resolves_faculty_head_scope()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "FACULTY_HEAD", FacultyId = ids.Faculty1, IsActive = true });
        await db.SaveChangesAsync();
        var controller = WithUser(new CatalogsController(db, new AuditSpy()), ids.HeadUser, "LECTURER");

        var result = await controller.Departments(CancellationToken.None);

        Assert.Equal([ids.Department1, ids.Department2], ResultIds(result));
    }

    [Fact]
    public async Task Catalog_departments_endpoint_does_not_allow_admin_business_bypass()
    {
        await using var db = CreateDb();
        await SeedScopeFixture(db);
        var controller = WithAdmin(new CatalogsController(db, new AuditSpy()));

        var result = await controller.Departments(CancellationToken.None);

        Assert.Empty(ResultIds(result));
    }

    [Fact]
    public async Task Student_duplicates_are_case_insensitive_field_specific_and_self_update_is_allowed()
    {
        await using var db = CreateDb();
        await SeedFacultyHead(db);
        db.StudentClassesSet.Add(new StudentClass { Id = 10, Code = "SC-10", Name = "Class", FacultyId = 1, IsActive = true });
        db.StudentsSet.Add(new Student { Id = 20, StudentCode = "SV001", FullName = "Existing", Email = "sv001@example.test", StudentClassId = 10, AnonymousCode = "ANON-001", IsActive = true });
        await db.SaveChangesAsync();
        var controller = WithUser(new StudentsController(db, new AuditSpy()), 100, "LECTURER");

        var duplicate = Assert.IsType<ConflictObjectResult>(await controller.Create(
            new UpsertStudentRequest(" sv001 ", "Other", " SV001@EXAMPLE.TEST ", 10, " anon-001 ", true),
            CancellationToken.None));
        var body = Assert.IsType<ApiErrorResponse>(duplicate.Value);
        Assert.Equal("DUPLICATE_FIELDS", body.Code);
        Assert.Equal(["email", "studentCode"], body.Errors!.Select(x => x.Field).OrderBy(x => x).ToArray());

        var codeDuplicate = Assert.IsType<ConflictObjectResult>(await controller.Create(
            new UpsertStudentRequest(" sv001 ", "Other", "unique-code@example.test", 10, "ANON-CODE", true),
            CancellationToken.None));
        AssertDuplicate(codeDuplicate, "STUDENT_CODE_DUPLICATE", "studentCode", "MSSV sv001 đã tồn tại.");
        var emailDuplicate = Assert.IsType<ConflictObjectResult>(await controller.Create(
            new UpsertStudentRequest("SV-EMAIL", "Other", " SV001@EXAMPLE.TEST ", 10, "ANON-EMAIL", true),
            CancellationToken.None));
        AssertDuplicate(emailDuplicate, "STUDENT_EMAIL_DUPLICATE", "email", "Email SV001@EXAMPLE.TEST đã được sử dụng.");
        Assert.IsType<CreatedAtActionResult>(await controller.Create(
            new UpsertStudentRequest("SV-ANON", "Other", "unique-anon@example.test", 10, " anon-001 ", true),
            CancellationToken.None));
        Assert.StartsWith("SV-", (await db.StudentsSet.SingleAsync(x => x.StudentCode == "SV-ANON")).AnonymousCode);

        var selfUpdate = await controller.Update(20,
            new UpsertStudentRequest(" sv001 ", "Existing", " SV001@EXAMPLE.TEST ", 10, " anon-001 ", true),
            CancellationToken.None);
        Assert.IsType<NoContentResult>(selfUpdate);

        db.StudentsSet.Add(new Student { Id = 30, StudentCode = "SV002", FullName = "Other", Email = "sv002@example.test", StudentClassId = 10, AnonymousCode = "ANON-002", IsActive = true });
        await db.SaveChangesAsync();
        Assert.IsType<NoContentResult>(await controller.Update(20,
            new UpsertStudentRequest("sv002", "Existing", "sv001@example.test", 10, "ANON-001", true),
            CancellationToken.None));
        Assert.Equal("SV001", (await db.StudentsSet.FindAsync(20L))!.StudentCode);
    }

    [Fact]
    public async Task Catalog_room_duplicate_is_precise_and_self_update_and_reactivation_work()
    {
        await using var db = CreateDb();
        db.Rooms.AddRange(
            new Room { Id = 1, Code = "A101", Name = "Room", IsActive = true },
            new Room { Id = 2, Code = "B202", Name = "Inactive", IsActive = false });
        await db.SaveChangesAsync();
        var controller = WithAdmin(new CatalogsController(db, new AuditSpy()));

        Assert.IsType<OkObjectResult>(await controller.AddRoom(
            new CatalogsController.RoomReq(" a101 ", "Other", null, 20, null, Campus:"CS1",Building:"A",RoomNumber:"102"), CancellationToken.None));
        Assert.StartsWith("PH-", (await db.Rooms.SingleAsync(x => x.Name == "Other")).Code);
        Assert.IsType<NoContentResult>(await controller.UpdateRoom(1,
            new CatalogsController.RoomReq(" a101 ", "Room", null, 20, null), CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.ReactivateCatalog("rooms", 2, CancellationToken.None));
        Assert.True((await db.Rooms.FindAsync(2L))!.IsActive);
    }

    [Fact]
    public async Task Catalog_codes_are_generated_immutable_and_legacy_class_writes_are_blocked()
    {
        await using var db = CreateDb();
        await SeedFacultyHead(db);
        db.FacultiesSet.Add(new Faculty { Id = 2, Code = "F2", Name = "Other faculty", IsActive = true });
        db.StudentClassesSet.Add(new StudentClass { Id = 10, Code = "SC1", Name = "Class", FacultyId = 1, IsActive = true });
        db.Courses.Add(new Course { Id = 20, Code = "C1", Name = "Course", DepartmentId = 1, Credits = 3, IsActive = true });
        db.Teachers.Add(new Teacher { Id = 30, TeacherCode = "GV1", FullName = "Teacher", DepartmentId = 1, IsActive = true });
        db.ClassSections.Add(new ClassSection { Id = 40, Code = "CS1", Name = "Section", CourseId = 20, TeacherId = 30, Semester = "HK1", AcademicYear = "2026", IsActive = true });
        db.Rooms.Add(new Room { Id = 50, Code = "R1", Name = "Room", IsActive = true });
        db.CamerasSet.Add(new Camera { Id = 60, Code = "CAM1", Name = "Camera", RoomId = 50, RtspUrl = "rtsp://camera.test/live", IsActive = true });
        await db.SaveChangesAsync();
        var academic = WithUser(new CatalogsController(db, new AuditSpy()), 100, "LECTURER");
        var admin = WithAdmin(new CatalogsController(db, new AuditSpy()));

        Assert.IsType<NoContentResult>(await academic.UpdateFaculty(1,
            new CatalogsController.FacultyReq(" f2 ", "Faculty", null), CancellationToken.None));
        Assert.Equal("F1", (await db.FacultiesSet.FindAsync(1L))!.Code);
        Assert.IsType<OkObjectResult>(await academic.AddDepartment(new(" d1 ", "Department", 1, null), CancellationToken.None));
        (await db.DepartmentsSet.FindAsync(1L))!.Code="BM-CNPM";await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await academic.AddStudentClass(new(" sc1 ", "Class", 1, 2026, null, DepartmentId:1), CancellationToken.None));
        Assert.IsType<OkObjectResult>(await academic.AddCourse(new(null, "Course", 1, 3, null, StartAcademicYear:"2026-2027", StartSemester:"HK1"), CancellationToken.None));
        Assert.IsType<OkObjectResult>(await academic.AddTeacher(new(" gv1 ", "Teacher", "", null, 1, null), CancellationToken.None));
        Assert.IsType<ConflictObjectResult>(await academic.AddClass(new(" cs1 ", "Section", 20, 30, "HK1", "2026", null), CancellationToken.None));
        Assert.IsType<OkObjectResult>(await admin.AddRoom(new(" r1 ", "Room", null, 10, null, Campus:"CS1",Building:"A",RoomNumber:"101"), CancellationToken.None));
        (await db.Rooms.FindAsync(50L))!.Code="PH-CS1-A-102";await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await admin.AddCamera(new(" cam1 ", "Camera", 50, "rtsp://camera.test/other", null), CancellationToken.None));
        Assert.Contains(await db.DepartmentsSet.ToListAsync(), x => x.Code.StartsWith("BM-"));
        Assert.Contains(await db.StudentClassesSet.ToListAsync(), x => x.Code.StartsWith("LSH-"));
        Assert.Contains(await db.Courses.ToListAsync(), x => x.Code.StartsWith("MH-"));
        Assert.Contains(await db.Teachers.ToListAsync(), x => x.TeacherCode.StartsWith("GV-"));
        Assert.Single(await db.ClassSections.ToListAsync());
    }

    [Fact]
    public async Task Catalog_and_rule_updates_allow_their_own_unique_values()
    {
        await using var db = CreateDb();
        await SeedFacultyHead(db);
        db.StudentClassesSet.Add(new StudentClass { Id = 10, Code = "SC1", Name = "Class", FacultyId = 1, IsActive = true });
        db.Courses.Add(new Course { Id = 20, Code = "C1", Name = "Course", DepartmentId = 1, Credits = 3, IsActive = true });
        db.UsersSet.Add(new User { Id = 200, UserName = "teacher", FullName = "Teacher", Status = "ACTIVE" });
        db.Teachers.Add(new Teacher { Id = 30, TeacherCode = "GV1", FullName = "Teacher", Email = "teacher@example.test", UserId = 200, DepartmentId = 1, IsActive = true });
        db.ClassSections.Add(new ClassSection { Id = 40, Code = "CS1", Name = "Section", CourseId = 20, TeacherId = 30, Semester = "HK1", AcademicYear = "2026", IsActive = true });
        db.Rooms.Add(new Room { Id = 50, Code = "R1", Name = "Room", Capacity = 20, IsActive = true });
        db.CamerasSet.Add(new Camera { Id = 60, Code = "CAM1", Name = "Camera", RoomId = 50, RtspUrl = "rtsp://camera.test/live", IsActive = true });
        db.AttendancePoliciesSet.Add(new AttendancePolicy { Id = 70, Code = "DEFAULT", Name = "Default", PresentThreshold = .8m, PartialThreshold = .5m, PresentScore = 10, PartialScore = 5, AbsentScore = 0, Version = "v1", IsActive = true });
        db.AlertRulesSet.Add(new AlertRule { Id = 80, Code = "PHONE", Name = "Phone", BehaviorLabel = "PHONE_USE", MinDurationSeconds = 5, MinConfidence = .7m, MinObservationQuality = .5m, Enabled = true, Version = "v1" });
        await db.SaveChangesAsync();

        var academic = WithUser(new CatalogsController(db, new AuditSpy()), 100, "LECTURER");
        Assert.IsType<NoContentResult>(await academic.UpdateFaculty(1, new(" f1 ", "Faculty 1", null), CancellationToken.None));
        Assert.IsType<NoContentResult>(await academic.UpdateDepartment(1, new(" d1 ", "Department 1", 1, null), CancellationToken.None));
        Assert.IsType<NoContentResult>(await academic.UpdateStudentClass(10, new(" sc1 ", "Class", 1, 2026, null), CancellationToken.None));
        Assert.IsType<NoContentResult>(await academic.UpdateCourse(20, new(" c1 ", "Course", 1, 3, null, StartAcademicYear:"2026-2027", StartSemester:"HK1"), CancellationToken.None));
        Assert.IsType<NoContentResult>(await academic.UpdateTeacher(30, new(" gv1 ", "Teacher", "teacher@example.test", 200, 1, null), CancellationToken.None));
        Assert.IsType<ConflictObjectResult>(await academic.UpdateClass(40, new(" cs1 ", "Section", 20, 30, "HK1", "2026", null), CancellationToken.None));

        var admin = WithAdmin(new CatalogsController(db, new AuditSpy()));
        Assert.IsType<NoContentResult>(await admin.UpdateRoom(50, new(" r1 ", "Room", null, 20, null), CancellationToken.None));
        Assert.IsType<NoContentResult>(await admin.UpdateCamera(60, new(" cam1 ", "Camera", 50, "rtsp://camera.test/live", null), CancellationToken.None));

        var policy = WithUser(new AttendancePoliciesController(db, new AuditSpy()), 100, "LECTURER");
        Assert.IsType<NoContentResult>(await policy.Update(70, new(" default ", "Default", .8m, .5m, 10, 5, 0, "v1"), CancellationToken.None));

        var alerts = WithAdmin(new AlertsController(db, new AuditSpy()));
        Assert.IsType<NoContentResult>(await alerts.UpdateRule(80,
            new(" phone ", "Phone", "PHONE_USE", 5, .7m, .5m, true, "v1"),
            CancellationToken.None));
    }

    [Fact]
    public async Task Admin_user_duplicates_are_precise_and_self_email_update_is_allowed()
    {
        await using var db = CreateDb();
        var role = new Role { Id = 1, Name = "LECTURER" };
        var existing = new User { Id = 10, UserName = "teacher", Email = "teacher@example.test", FullName = "Teacher", Status = "ACTIVE" };
        db.RolesSet.Add(role);
        db.UsersSet.Add(existing);
        db.UserRoles.Add(new UserRole { UserId = 10, RoleId = 1 });
        await db.SaveChangesAsync();
        var controller = WithAdmin(new AdminUsersController(db, new AuditSpy()));

        var duplicate = Assert.IsType<ConflictObjectResult>(await controller.Create(
            new AdminUsersController.CreateReq(" TEACHER ", " TEACHER@EXAMPLE.TEST ", "Other", "ValidPass1!", ["LECTURER"]),
            CancellationToken.None));
        var body = Assert.IsType<ApiErrorResponse>(duplicate.Value);
        Assert.Equal("DUPLICATE_FIELDS", body.Code);
        Assert.Equal(["email", "userName"], body.Errors!.Select(x => x.Field).OrderBy(x => x).ToArray());

        AssertDuplicate(Assert.IsType<ConflictObjectResult>(await controller.Create(
            new AdminUsersController.CreateReq("teacher", "other@example.test", "Other", "ValidPass1!", ["LECTURER"]),
            CancellationToken.None)), "USER_NAME_DUPLICATE", "userName");
        AssertDuplicate(Assert.IsType<ConflictObjectResult>(await controller.Create(
            new AdminUsersController.CreateReq("other", "teacher@example.test", "Other", "ValidPass1!", ["LECTURER"]),
            CancellationToken.None)), "USER_EMAIL_DUPLICATE", "email");

        Assert.IsType<NoContentResult>(await controller.Update(10,
            new AdminUsersController.UpdateReq(" TEACHER@EXAMPLE.TEST ", "Teacher", ["LECTURER"]),
            CancellationToken.None));
    }

    [Fact]
    public async Task Attendance_policy_duplicate_is_precise_and_deactivate_reactivate_persists()
    {
        await using var db = CreateDb();
        await SeedFacultyHead(db);
        db.AttendancePoliciesSet.Add(new AttendancePolicy { Id = 20, Code = "DEFAULT", Name = "Default", IsActive = true });
        await db.SaveChangesAsync();
        var controller = WithUser(new AttendancePoliciesController(db, new AuditSpy()), 100, "LECTURER");
        var request = new AttendancePoliciesController.Req(" default ", "Other", .8m, .5m, 10, 5, 0, "v1");

        Assert.IsType<OkObjectResult>(await controller.Create(request, CancellationToken.None));
        Assert.StartsWith("CC-", (await db.AttendancePoliciesSet.SingleAsync(x => x.Id != 20)).Code);
        Assert.IsType<NoContentResult>(await controller.Deactivate(20, CancellationToken.None));
        Assert.False((await db.AttendancePoliciesSet.FindAsync(20L))!.IsActive);
        Assert.IsType<NoContentResult>(await controller.Reactivate(20, CancellationToken.None));
        Assert.True((await db.AttendancePoliciesSet.FindAsync(20L))!.IsActive);
    }

    [Fact]
    public async Task Student_reactivation_enforces_scope_and_persists_for_faculty_head()
    {
        await using var db = CreateDb();
        var ids = await SeedScopeFixture(db);
        db.StudentClassesSet.Add(new StudentClass { Id = 701, Code = "SC-F1", Name = "Faculty 1", FacultyId = ids.Faculty1, IsActive = true });
        db.StudentClassesSet.Add(new StudentClass { Id = 702, Code = "SC-F1-INACTIVE", Name = "Inactive class", FacultyId = ids.Faculty1, IsActive = false });
        db.StudentsSet.Add(new Student { Id = 801, StudentCode = "SV801", FullName = "Student", StudentClassId = 701, AnonymousCode = "ANON-801", IsActive = false });
        db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = ids.HeadTeacher, PositionType = "FACULTY_HEAD", FacultyId = ids.Faculty1, IsActive = true });
        await db.SaveChangesAsync();

        var outside = WithUser(new StudentsController(db, new AuditSpy()), ids.UserC, "LECTURER");
        Assert.IsType<ForbidResult>(await outside.Reactivate(801, CancellationToken.None));
        var head = WithUser(new StudentsController(db, new AuditSpy()), ids.HeadUser, "LECTURER");
        Assert.IsType<NoContentResult>(await head.Reactivate(801, CancellationToken.None));
        Assert.True((await db.StudentsSet.FindAsync(801L))!.IsActive);

        var outsideCatalog = WithUser(new CatalogsController(db, new AuditSpy()), ids.UserC, "LECTURER");
        Assert.IsType<ForbidResult>(await outsideCatalog.ReactivateCatalog("student-classes", 702, CancellationToken.None));
        var headCatalog = WithUser(new CatalogsController(db, new AuditSpy()), ids.HeadUser, "LECTURER");
        Assert.IsType<NoContentResult>(await headCatalog.ReactivateCatalog("student-classes", 702, CancellationToken.None));
        Assert.True((await db.StudentClassesSet.FindAsync(702L))!.IsActive);
    }

    [Fact]
    public async Task Login_unknown_wrong_password_and_locked_return_same_payload_but_distinct_audit_reasons()
    {
        await using var db = CreateDb();
        var active = new User { Id = 1, UserName = "active", Email = "active@example.test", FullName = "Active", Status = "ACTIVE" };
        active.PasswordHash = new PasswordHasher<User>().HashPassword(active, "ValidPass1!");
        var locked = new User { Id = 2, UserName = "locked", Email = "locked@example.test", FullName = "Locked", Status = "LOCKED" };
        locked.PasswordHash = new PasswordHasher<User>().HashPassword(locked, "ValidPass1!");
        db.UsersSet.AddRange(active, locked);
        await db.SaveChangesAsync();
        var audit = new AuditSpy();
        var controller = new AuthController(db, new TokenSpy(), audit);

        var unknown = Assert.IsType<UnauthorizedObjectResult>((await controller.Login(new LoginRequest("missing", "bad"), CancellationToken.None)).Result);
        var wrong = Assert.IsType<UnauthorizedObjectResult>((await controller.Login(new LoginRequest(" ACTIVE ", "bad"), CancellationToken.None)).Result);
        var blocked = Assert.IsType<UnauthorizedObjectResult>((await controller.Login(new LoginRequest("locked@example.test", "ValidPass1!"), CancellationToken.None)).Result);

        Assert.Equal(Payload(unknown), Payload(wrong));
        Assert.Equal(Payload(unknown), Payload(blocked));
        Assert.Equal("INVALID_CREDENTIALS", Payload(unknown).Code);
        Assert.Equal(["ACCOUNT_LOCKED", "BAD_PASSWORD", "INVALID_ACCOUNT"], audit.Entries.Select(x => x.Reason).Where(x => x != null).Select(x => x!).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task ApiExceptionMiddleware_request_abort_cancellation_does_not_return_internal_error()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        var context = new DefaultHttpContext { RequestAborted = aborted.Token };
        context.Response.Body = new MemoryStream();
        var middleware = new ApiExceptionMiddleware(_ => throw new TaskCanceledException("A task was canceled"), NullLogger<ApiExceptionMiddleware>.Instance);

        await middleware.Invoke(context);

        Assert.Equal(499, context.Response.StatusCode);
    }

    [Fact]
    public async Task ApiExceptionMiddleware_non_request_cancellation_is_not_swallowed()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ApiExceptionMiddleware(_ => throw new TaskCanceledException("Internal timeout"), NullLogger<ApiExceptionMiddleware>.Instance);

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task ApiExceptionMiddleware_maps_contract_only_ai_to_service_unavailable()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ApiExceptionMiddleware(
            _ => throw new AiServiceUnavailableException(
                "AI inference chưa sẵn sàng."),
            NullLogger<ApiExceptionMiddleware>.Instance);

        await middleware.Invoke(context);

        Assert.Equal(
            StatusCodes.Status503ServiceUnavailable,
            context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var payload = await reader.ReadToEndAsync();
        Assert.Contains(
            "AI_SERVICE_UNAVAILABLE",
            payload);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Secret"] = new string('x', 64) }).Build();
    private static FormFile File(string csv)
    {
        var bytes = new UTF8Encoding(true).GetBytes(csv);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "students.csv") { Headers = new HeaderDictionary(), ContentType = "text/csv" };
    }
    private static FormFile VideoFile(byte[] bytes) =>
        new(new MemoryStream(bytes), 0, bytes.Length, "file", "video.mp4")
        {
            Headers = new HeaderDictionary(),
            ContentType = "video/mp4",
        };
    private static T WithAdmin<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "ADMIN")], "Test")) } };
        return controller;
    }

    private static T WithUser<T>(T controller, long userId, params string[] roles) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(userId, roles) } };
        return controller;
    }

    private static async Task SeedFacultyHead(AppDbContext db)
    {
        if (!await db.FacultiesSet.AnyAsync(x => x.Id == 1)) db.FacultiesSet.Add(new Faculty { Id = 1, Code = "F1", Name = "Faculty 1" });
        if (!await db.DepartmentsSet.AnyAsync(x => x.Id == 1)) db.DepartmentsSet.Add(new Department { Id = 1, Code = "D1", Name = "Department 1", FacultyId = 1 });
        if (!await db.UsersSet.AnyAsync(x => x.Id == 100)) db.UsersSet.Add(new User { Id = 100, UserName = "faculty-head", FullName = "Faculty Head", Status = "ACTIVE" });
        if (!await db.Teachers.AnyAsync(x => x.Id == 100)) db.Teachers.Add(new Teacher { Id = 100, TeacherCode = "GV-H", FullName = "Faculty Head", UserId = 100, DepartmentId = 1, IsActive = true });
        if (!await db.ManagementAssignmentsSet.AnyAsync(x => x.TeacherId == 100)) db.ManagementAssignmentsSet.Add(new ManagementAssignment { TeacherId = 100, PositionType = "FACULTY_HEAD", FacultyId = 1, IsActive = true });
        await db.SaveChangesAsync();
    }

    private static ClaimsPrincipal Principal(long userId, params string[] roles) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }.Concat(roles.Select(role => new Claim(ClaimTypes.Role, role))), "Test"));

    private static long[] ResultIds(ActionResult result)
    {
        var value = Assert.IsType<OkObjectResult>(result).Value;
        return Assert.IsAssignableFrom<IEnumerable<object>>(value)
            .Select(item => (long)item.GetType().GetProperty("Id")!.GetValue(item)!)
            .OrderBy(id => id)
            .ToArray();
    }

    private static void AssertDuplicate(ConflictObjectResult result, string code, string field, string? message = null)
    {
        var body = Assert.IsType<ApiErrorResponse>(result.Value);
        Assert.Equal(code, body.Code);
        Assert.Equal(field, Assert.Single(body.Errors!).Field);
        if (message is not null) Assert.Equal(message, body.Message);
    }

    private static (string Code, string Message) Payload(UnauthorizedObjectResult result)
    {
        var value = result.Value!;
        return (
            (string)value.GetType().GetProperty("code")!.GetValue(value)!,
            (string)value.GetType().GetProperty("message")!.GetValue(value)!);
    }

    private sealed record ScopeIds(long UserA, long UserB, long UserC, long HeadUser, long TeacherA, long TeacherB, long TeacherC, long HeadTeacher, long Faculty1, long Faculty2, long Department1, long Department2, long Department3, long SessionA, long SessionB, long SessionC);

    private static async Task<ScopeIds> SeedScopeFixture(AppDbContext db)
    {
        db.FacultiesSet.AddRange(new Faculty { Id = 1, Code = "F1", Name = "Faculty 1" }, new Faculty { Id = 2, Code = "F2", Name = "Faculty 2" });
        db.DepartmentsSet.AddRange(new Department { Id = 10, Code = "D1", Name = "Dept 1", FacultyId = 1 }, new Department { Id = 11, Code = "D2", Name = "Dept 2", FacultyId = 1 }, new Department { Id = 12, Code = "D3", Name = "Dept 3", FacultyId = 2 });
        db.UsersSet.AddRange(
            new User { Id = 101, UserName = "a", FullName = "A", Status = "ACTIVE" },
            new User { Id = 102, UserName = "b", FullName = "B", Status = "ACTIVE" },
            new User { Id = 103, UserName = "c", FullName = "C", Status = "ACTIVE" },
            new User { Id = 104, UserName = "head", FullName = "Head", Status = "ACTIVE" },
            new User { Id = 90, UserName = "admin", FullName = "Admin", Status = "ACTIVE" });
        db.Teachers.AddRange(
            new Teacher { Id = 201, TeacherCode = "GV-A", FullName = "Teacher A", UserId = 101, DepartmentId = 10, IsActive = true },
            new Teacher { Id = 202, TeacherCode = "GV-B", FullName = "Teacher B", UserId = 102, DepartmentId = 10, IsActive = true },
            new Teacher { Id = 203, TeacherCode = "GV-C", FullName = "Teacher C", UserId = 103, DepartmentId = 12, IsActive = true },
            new Teacher { Id = 204, TeacherCode = "GV-H", FullName = "Head Teacher", UserId = 104, DepartmentId = 10, IsActive = true });
        db.Courses.AddRange(new Course { Id = 301, Code = "C1", Name = "Course 1", DepartmentId = 10, IsActive = true }, new Course { Id = 302, Code = "C2", Name = "Course 2", DepartmentId = 11, IsActive = true }, new Course { Id = 303, Code = "C3", Name = "Course 3", DepartmentId = 12, IsActive = true });
        db.ClassSections.AddRange(new ClassSection { Id = 401, Code = "CS1", Name = "CS1", CourseId = 301, TeacherId = 201, Semester = "HK1", AcademicYear = "2026", IsActive = true }, new ClassSection { Id = 402, Code = "CS2", Name = "CS2", CourseId = 302, TeacherId = 202, Semester = "HK1", AcademicYear = "2026", IsActive = true }, new ClassSection { Id = 403, Code = "CS3", Name = "CS3", CourseId = 303, TeacherId = 203, Semester = "HK1", AcademicYear = "2026", IsActive = true });
        db.AttendancePoliciesSet.Add(new AttendancePolicy { Id = 501, Code = "P", Name = "Policy", PresentThreshold = .8m, PartialThreshold = .5m, PresentScore = 10, PartialScore = 5, AbsentScore = 0, Version = "v1", IsActive = true });
        db.SessionsSet.AddRange(
            new Session { Id = 601, ClassSectionId = 401, OriginalTeacherId = 201, AttendancePolicyId = 501, ScheduledStart = DateTime.UtcNow.AddDays(1), ScheduledEnd = DateTime.UtcNow.AddDays(1).AddHours(2), Status = "READY" },
            new Session { Id = 602, ClassSectionId = 402, OriginalTeacherId = 202, AttendancePolicyId = 501, ScheduledStart = DateTime.UtcNow.AddDays(2), ScheduledEnd = DateTime.UtcNow.AddDays(2).AddHours(2), Status = "READY" },
            new Session { Id = 603, ClassSectionId = 403, OriginalTeacherId = 203, AttendancePolicyId = 501, ScheduledStart = DateTime.UtcNow.AddDays(3), ScheduledEnd = DateTime.UtcNow.AddDays(3).AddHours(2), Status = "READY" });
        await db.SaveChangesAsync();
        return new ScopeIds(101, 102, 103, 104, 201, 202, 203, 204, 1, 2, 10, 11, 12, 601, 602, 603);
    }

    private sealed class AuditSpy : IAuditService
    {
        public List<(string Action, string? Reason)> Entries { get; } = [];
        public Task WriteAsync(long? userId, string action, string entityType, string entityId, object? metadata = null, CancellationToken ct = default)
        {
            Entries.Add((action, metadata?.GetType().GetProperty("reason")?.GetValue(metadata)?.ToString()));
            return Task.CompletedTask;
        }
    }

    private sealed class TokenSpy : ITokenService
    {
        public LoginResponse Create(User user, string[] roles) => new("token", DateTime.UtcNow.AddHours(1), user.Id, user.FullName, roles, user.TokenVersion);
    }

    private sealed class FakeStorage : IObjectStorage
    {
        public bool Exists { get; init; }
        public string? LastReadKey { get; private set; }
        public string? LastStatKey { get; private set; }
        public long LastOffset { get; private set; }
        public long LastLength { get; private set; }
        public byte[] Bytes { get; init; } = Encoding.UTF8.GetBytes("fake-video-content");
        public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PutAsync(string objectKey, Stream stream, long sizeBytes, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string objectKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetReadUrlAsync(string objectKey, int expirySeconds = 900, CancellationToken ct = default) { LastReadKey = objectKey; return Task.FromResult($"https://storage.test/{objectKey}"); }
        public Task<string> GetInternalReadUrlAsync(string objectKey, int expirySeconds = 900, CancellationToken ct = default) => GetReadUrlAsync(objectKey, expirySeconds, ct);
        public Task<string> GetInternalWriteUrlAsync(string objectKey, int expirySeconds = 900, CancellationToken ct = default) => Task.FromResult($"https://storage.test/{objectKey}");
        public Task<ObjectStorageMetadata?> StatAsync(string objectKey, CancellationToken ct = default) { LastStatKey = objectKey; return Task.FromResult<ObjectStorageMetadata?>(Exists ? new(Bytes.LongLength, "video/mp4") : null); }
        public async Task CopyRangeToAsync(string objectKey, long offset, long length, Stream destination, CancellationToken ct = default) { LastReadKey = objectKey; LastOffset = offset; LastLength = length; await destination.WriteAsync(Bytes.AsMemory((int)offset, (int)length), ct); }
        public Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default) => Task.FromResult(Exists);
        public Task<bool> HealthAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private static IAiClient TestAi() => new FakeAi(new AiArtifactMetadata("test.mp4", "test.mp4", "video/mp4", 1, new string('a', 64)));

    private sealed class FakeAi(AiArtifactMetadata artifact) : IAiClient
    {
        public Task<StartAiJobResponse> StartAsync(StartAiJobRequest request, CancellationToken ct = default) => Task.FromResult(new StartAiJobResponse("job", "RUNNING", "test"));
        public Task<StopAiJobResponse> StopAsync(string externalJobId, CancellationToken ct = default) => Task.FromResult(new StopAiJobResponse(externalJobId, "COMPLETED", artifact));
        public Task<StopAiJobResponse> GetJobAsync(string externalJobId, CancellationToken ct = default) => Task.FromResult(new StopAiJobResponse(externalJobId, "COMPLETED", artifact));
        public Task<AiCapabilitiesResponse> CapabilitiesAsync(CancellationToken ct = default) => Task.FromResult(new AiCapabilitiesResponse(true, false, true, ["test"]));
        public Task<StartFaceEnrollmentResponse> StartFaceEnrollmentAsync(FaceEnrollmentManifest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UploadBrowserChunkAsync(string externalJobId, Stream stream, string fileName, string contentType, CancellationToken ct = default) => Task.CompletedTask;
        public Task<(byte[] Data, string ContentType)?> GetOverlayPreviewAsync(string externalJobId, CancellationToken ct = default) => Task.FromResult<(byte[] Data, string ContentType)?>(null);
        public Task<string?> FastOverlayAsync(string externalJobId, Stream stream, string fileName, string contentType, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<bool> HealthAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FlakySummary : ISummaryService
    {
        private int attempts;
        public Task RebuildSessionAsync(long sessionId, DateTime endedAt, CancellationToken ct)
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("Lỗi tổng hợp tạm thời");
            return Task.CompletedTask;
        }
    }
}
