using System.Security.Claims;
using System.Text.Json;
using ClosedXML.Excel;
using HTBAM.Api.Controllers;
using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using HTBAM.Infrastructure.Services.Llm;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;
using Xunit;

namespace HTBAM.Tests;

public sealed class LlmModuleTests
{
    [Fact]
    public async Task Missing_provider_configuration_uses_fallback_and_backend_service_continues()
    {
        await using var db = CreateDb();
        await SeedSessionAsync(db);
        var provider = new FakeProvider { IsConfiguredValue = false };
        var service = new AutoCommentService(db, new FakeResolver(provider), new AuditSpy());

        var result = await service.GenerateSessionAsync(100, 1);

        Assert.NotNull(result);
        Assert.True(result.IsFallback);
        Assert.Equal("PROVIDER_NOT_CONFIGURED", result.ErrorCode);
        Assert.Equal("RULE_BASED_FALLBACK", (await db.ClassSessionSummariesSet.SingleAsync()).AutoCommentProvider);
        Assert.False(provider.WasCalled);
    }

    [Fact]
    public void Disabled_llm_resolves_to_unavailable_provider()
    {
        var configuration = Config(new Dictionary<string, string?> { ["Llm:Enabled"] = "false", ["Llm:Provider"] = "GEMINI", ["Llm:Model"] = "gemini-2.5-pro" });
        var gemini = new GeminiLlmProvider(configuration, NullLogger<GeminiLlmProvider>.Instance);
        var unavailable = new UnavailableLlmProvider(configuration);

        var resolved = new LlmProviderResolver(configuration, gemini, unavailable).Resolve();

        Assert.False(resolved.IsConfigured);
        Assert.IsType<UnavailableLlmProvider>(resolved);
    }

    [Fact]
    public async Task Gemini_success_is_saved_with_provider_and_model()
    {
        await using var db = CreateDb();
        await SeedSessionAsync(db);
        var generatedAt = DateTime.UtcNow;
        var provider = new FakeProvider { Result = new("Nhận xét hợp lệ.", "GEMINI", "gemini-2.5-pro", generatedAt, false) };
        var service = new AutoCommentService(db, new FakeResolver(provider), new AuditSpy());

        var result = await service.GenerateSessionAsync(100, 1);

        Assert.NotNull(result);
        Assert.False(result.IsFallback);
        var saved = await db.ClassSessionSummariesSet.SingleAsync();
        Assert.Equal("Nhận xét hợp lệ.", saved.AutoComment);
        Assert.Equal("GEMINI:gemini-2.5-pro", saved.AutoCommentProvider);
        Assert.Equal(generatedAt, saved.AutoCommentGeneratedAt);
    }

    [Theory]
    [InlineData("TIMEOUT")]
    [InlineData("AUTH_ERROR")]
    [InlineData("RATE_LIMIT")]
    [InlineData("INVALID_MODEL")]
    [InlineData("NETWORK_ERROR")]
    public async Task Provider_errors_return_fallback_instead_of_throwing(string errorCode)
    {
        await using var db = CreateDb();
        await SeedSessionAsync(db);
        var provider = new FakeProvider { Result = new(string.Empty, "GEMINI", "gemini-2.5-pro", DateTime.UtcNow, false, errorCode) };
        var service = new AutoCommentService(db, new FakeResolver(provider), new AuditSpy());

        var result = await service.GenerateSessionAsync(100, 1);

        Assert.NotNull(result);
        Assert.True(result.IsFallback);
        Assert.Equal(errorCode, result.ErrorCode);
    }

    [Fact]
    public async Task Empty_provider_response_uses_fallback()
    {
        await using var db = CreateDb();
        await SeedSessionAsync(db);
        var provider = new FakeProvider { Result = new(" ", "GEMINI", "gemini-2.5-pro", DateTime.UtcNow, false) };
        var service = new AutoCommentService(db, new FakeResolver(provider), new AuditSpy());

        var result = await service.GenerateSessionAsync(100, 1);

        Assert.NotNull(result);
        Assert.True(result.IsFallback);
        Assert.Equal("PROVIDER_ERROR", result.ErrorCode);
    }

    [Fact]
    public async Task Unexpected_sdk_exception_uses_fallback()
    {
        await using var db = CreateDb();
        await SeedSessionAsync(db);
        var service = new AutoCommentService(db, new FakeResolver(new ThrowingProvider()), new AuditSpy());

        var result = await service.GenerateSessionAsync(100, 1);

        Assert.NotNull(result);
        Assert.True(result.IsFallback);
        Assert.Equal("PROVIDER_EXCEPTION", result.ErrorCode);
    }

    [Fact]
    public async Task Student_prompt_contains_stored_metrics_but_no_mssv_or_personal_identifier()
    {
        await using var db = CreateDb();
        await SeedSessionAsync(db);
        var provider = new FakeProvider();
        var service = new AutoCommentService(db, new FakeResolver(provider), new AuditSpy());

        var result = await service.GenerateStudentAsync(100, 700, 500);

        Assert.NotNull(result);
        Assert.NotNull(provider.LastRequest);
        Assert.Equal("STORED_SUMMARY", provider.LastRequest.SubjectId);
        Assert.Equal("Sinh viên trong buổi học", provider.LastRequest.SubjectName);
        Assert.Null(provider.LastRequest.AdditionalContext);
        var prompt = LlmPromptBuilder.Build(provider.LastRequest);
        Assert.DoesNotContain("SV-SECRET-700", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("700", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SessionId", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Report_exports_use_scoped_payload_and_include_available_sections()
    {
        await using var db = CreateDb();
        await SeedSessionAsync(db);
        await SeedForeignSessionAsync(db);
        var service = new AutoCommentService(db, new FakeResolver(new FakeProvider { IsConfiguredValue = false }), new AuditSpy());
        var controller = new ReportsController(db, service) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Lecturer(500) } } };

        var owned = Assert.IsType<FileContentResult>(await controller.SessionExcel(100, CancellationToken.None));
        Assert.EndsWith(".xlsx", owned.FileDownloadName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", owned.ContentType);
        using var workbook = new XLWorkbook(new MemoryStream(owned.FileContents));
        Assert.Contains(workbook.Worksheets, x => x.Name == "Tong hop");
        Assert.Contains(workbook.Worksheets, x => x.Name == "Sinh viên");
        Assert.Contains(workbook.Worksheets, x => x.Name == "Chuyên cần");
        Assert.Contains(workbook.Worksheets, x => x.Name == "Sự kiện hành vi");
        Assert.Contains(workbook.Worksheets, x => x.Name == "Cảnh báo");
        Assert.Contains(workbook.Worksheets, x => x.Name == "Kết quả nhận diện");
        Assert.Contains(workbook.Worksheets, x => x.Name == "Video");
        var attendanceSheet = workbook.Worksheet("Chuyên cần");
        Assert.Contains(attendanceSheet.Row(1).CellsUsed(), cell => cell.GetString() == "Điểm chuyên cần (%)");
        Assert.DoesNotContain(attendanceSheet.Row(1).CellsUsed(), cell => cell.GetString().Contains("ID", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("He-thong-giam-sat-", owned.FileDownloadName, StringComparison.OrdinalIgnoreCase);
        QuestPDF.Settings.License = LicenseType.Community;
        var classPdf = Assert.IsType<FileContentResult>(await controller.ClassPdf(30, CancellationToken.None));
        Assert.EndsWith(".pdf", classPdf.FileDownloadName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("application/pdf", classPdf.ContentType);
        Assert.True(classPdf.FileContents.Length > 1_000);
        Assert.IsType<ForbidResult>(await controller.SessionExcel(200, CancellationToken.None));
    }

    [Fact]
    public async Task Personal_comment_is_blocked_when_identity_is_not_trusted()
    {
        await using var db = CreateDb();
        await SeedSessionAsync(db);
        var identity = await db.StableIdentitiesSet.SingleAsync(x => x.StudentId == 700);
        identity.IdentityStatus = "UNKNOWN";
        await db.SaveChangesAsync();
        var provider = new FakeProvider();
        var service = new AutoCommentService(db, new FakeResolver(provider), new AuditSpy());
        var controller = new ReportsController(db, service) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Lecturer(500) } } };

        var response = await controller.GenerateStudentComment(100, 700, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        Assert.Contains("IDENTIFIED", JsonSerializer.Serialize(conflict.Value));
        Assert.False(provider.WasCalled);
    }

    [Fact]
    public async Task Lecturer_can_generate_for_owned_session_but_not_foreign_session()
    {
        await using var db = CreateDb();
        await SeedSessionAsync(db);
        await SeedForeignSessionAsync(db);
        var service = new AutoCommentService(db, new FakeResolver(new FakeProvider { IsConfiguredValue = false }), new AuditSpy());
        var controller = new ReportsController(db, service) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Lecturer(500) } } };

        var owned = await controller.GenerateSessionComment(100, CancellationToken.None);
        var foreign = await controller.GenerateSessionComment(200, CancellationToken.None);

        Assert.IsType<OkObjectResult>(owned);
        Assert.IsType<ForbidResult>(foreign);
    }

    [Fact]
    public async Task Admin_cannot_generate_business_comment_without_teaching_scope()
    {
        await using var db = CreateDb();
        await SeedSessionAsync(db);
        var service = new AutoCommentService(db, new FakeResolver(new FakeProvider { IsConfiguredValue = false }), new AuditSpy());
        var controller = new ReportsController(db, service) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(1, "ADMIN") } } };

        var response = await controller.GenerateSessionComment(100, CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
    }

    [Fact]
    public void Status_response_never_contains_api_key_or_secret_metadata()
    {
        var configuration = Config(new Dictionary<string, string?>
        {
            ["Llm:Enabled"] = "true", ["Llm:Provider"] = "GEMINI", ["Llm:Model"] = "gemini-2.5-pro"
        });
        var controller = new LlmController(configuration, new FakeResolver(new FakeProvider()));

        var response = Assert.IsType<OkObjectResult>(controller.Status());
        var json = JsonSerializer.Serialize(response.Value).ToUpperInvariant();

        Assert.DoesNotContain("APIKEY", json);
        Assert.DoesNotContain("API_KEY", json);
        Assert.DoesNotContain("SECRET", json);
        Assert.Contains("FALLBACKAVAILABLE", json);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task SeedSessionAsync(AppDbContext db)
    {
        db.UsersSet.Add(new User { Id = 500, UserName = "lecturer", FullName = "Lecturer", Status = "ACTIVE" });
        var teacher = new Teacher { Id = 10, TeacherCode = "GV01", FullName = "Lecturer", UserId = 500 };
        var course = new Course { Id = 20, Code = "CS", Name = "Course" };
        var section = new ClassSection { Id = 30, Code = "CLS01", Name = "Class", CourseId = 20, Course = course, TeacherId = 10, Teacher = teacher };
        var started = DateTime.UtcNow.AddMinutes(-10);
        var policy = new AttendancePolicy { Id = 40, Code = "DEFAULT", Name = "Default", PresentScore = 10, PartialScore = 5, AbsentScore = 0 };
        var session = new Session { Id = 100, ClassSectionId = 30, ClassSection = section, AttendancePolicyId = 40, Status = "COMPLETED", ScheduledStart = started, StartedAt = started, EndedAt = started.AddMinutes(10) };
        db.AddRange(teacher, course, section, policy, session, new ClassSessionSummary
        {
            Id = 110, SessionId = 100, ObservedStudentCount = 3, FocusedRatio = .7m,
            DistractedRatio = .15m, SleepyRatio = .05m, ActiveRatio = .1m, AlertCount = 1, CameraAiQuality = .9m
        }, new Student { Id = 700, StudentCode = "SV-SECRET-700", FullName = "Private Student", Email = "private@example.test", AnonymousCode = "ANON-SECRET", StudentClassId = 1 },
        new StudentSessionSummary { Id = 701, SessionId = 100, StudentId = 700, FocusedRatio = .6m, DistractedRatio = .2m, SleepyRatio = .1m, ActiveRatio = .1m, AlertCount = 2, ObservationQuality = .8m },
        new StableIdentity { Id = 800, SessionId = 100, StableId = "stable-private", StudentId = 700, IdentityStatus = "IDENTIFIED", IdentityConfidence = .9m, FirstSeenAt = started, LastSeenAt = started.AddSeconds(60) },
        new BehaviorEvent { Id = 801, ExternalEventId = "private-behavior", SessionId = 100, StableIdentityId = 800, StudentId = 700, Label = "FOCUSED", Probability = .9m, ObservationQuality = .8m, StartedAt = started, EndedAt = started.AddSeconds(60), ModelVersion = "test", ThresholdVersion = "test" },
        new Attendance { Id = 802, SessionId = 100, StudentId = 700, PresentSeconds = 60, ObservedSeconds = 60, PresenceRatio = 1, ObservationQuality = .8m, Status = "PRESENT", Score = 10, PolicyVersion = "v1" });
        await db.SaveChangesAsync();
    }

    private static async Task SeedForeignSessionAsync(AppDbContext db)
    {
        var teacher = new Teacher { Id = 11, TeacherCode = "GV02", FullName = "Other", UserId = 501 };
        var course = new Course { Id = 21, Code = "MATH", Name = "Math" };
        var section = new ClassSection { Id = 31, Code = "CLS02", Name = "Other class", CourseId = 21, Course = course, TeacherId = 11, Teacher = teacher };
        var session = new Session { Id = 200, ClassSectionId = 31, ClassSection = section, AttendancePolicyId = 40, Status = "COMPLETED", ScheduledStart = DateTime.UtcNow };
        db.AddRange(teacher, course, section, session, new ClassSessionSummary { Id = 210, SessionId = 200, CameraAiQuality = .8m });
        await db.SaveChangesAsync();
    }

    private static ClaimsPrincipal Lecturer(long userId) => Principal(userId, "LECTURER");
    private static ClaimsPrincipal Principal(long userId, string role) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role)
    ], "Test"));
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class FakeResolver(ILlmProvider provider) : ILlmProviderResolver { public ILlmProvider Resolve() => provider; }
    private sealed class FakeProvider : ILlmProvider
    {
        public string ProviderName => "GEMINI";
        public string ModelName => "gemini-2.5-pro";
        public bool IsConfigured => IsConfiguredValue;
        public bool IsConfiguredValue { get; init; } = true;
        public bool WasCalled { get; private set; }
        public LlmCommentRequest? LastRequest { get; private set; }
        public LlmGenerationResult? Result { get; init; } = new("OK", "GEMINI", "gemini-2.5-pro", DateTime.UtcNow, false);
        public Task<LlmGenerationResult?> GenerateCommentAsync(LlmCommentRequest request, CancellationToken cancellationToken = default) { WasCalled = true; LastRequest = request; return Task.FromResult(Result); }
    }
    private sealed class ThrowingProvider : ILlmProvider
    {
        public string ProviderName => "GEMINI";
        public string ModelName => "gemini-2.5-pro";
        public bool IsConfigured => true;
        public Task<LlmGenerationResult?> GenerateCommentAsync(LlmCommentRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("SDK failed");
    }
    private sealed class AuditSpy : IAuditService
    {
        public List<string> Actions { get; } = [];
        public Task WriteAsync(long? userId, string action, string entityType, string entityId, object? metadata = null, CancellationToken ct = default) { Actions.Add(action); return Task.CompletedTask; }
    }
}
