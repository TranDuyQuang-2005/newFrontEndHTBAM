using ClosedXML.Excel;
using HTBAM.Api.Controllers;
using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HTBAM.Tests;

public sealed class ClassSectionImportTests
{
    private const string Mime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public void Legacy_source_request_is_accepted_and_only_canonical_name_is_emitted()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var request = AcademicSchedulingTests.Request() with { SourceClassSectionCode = "KEEP-001" };
        var canonical = System.Text.Json.JsonSerializer.Serialize(request, options);
        Assert.Contains("\"sourceClassSectionCode\":\"KEEP-001\"", canonical);
        Assert.DoesNotContain("\"sourceCode\"", canonical);
        var legacy = canonical.Replace("sourceClassSectionCode", "sourceCode");
        Assert.Equal("KEEP-001", System.Text.Json.JsonSerializer.Deserialize<ClassSectionRequest>(legacy, options)!.SourceClassSectionCode);
    }

    [Fact]
    public async Task Completed_import_tokens_from_previous_schema_replay_without_creating_data()
    {
        await using var db = await AcademicSchedulingTests.Fixture();
        var user = AcademicSchedulingTests.With(new ClassSectionsController(db, new AcademicSchedulingTests.Audit()), 1).User;
        db.ClassSectionImportPreviews.Add(new() { UserId = 1, Token = "legacy-completed-token", ExpiresAt = DateTime.UtcNow.AddDays(-1), SnapshotJson = "{}",
            ResultJson = "{\"createdClasses\":1,\"updatedClasses\":0,\"createdSchedules\":2,\"skippedRows\":0,\"duplicates\":0,\"errors\":[],\"classes\":[{\"id\":42,\"code\":\"demo_001\",\"sourceCode\":\"KEEP-001\"}]}" });
        await db.SaveChangesAsync();
        var result = await new ClassSectionExcelImport(db).Confirm("legacy-completed-token", user, Ct);
        Assert.Equal("KEEP-001", result.Classes[0].SourceClassSectionCode);
        Assert.Equal("demo_001", result.Classes[0].Code);
        Assert.Empty(await db.ClassSections.ToListAsync());
    }

    [Fact]
    public async Task Unavailable_sql_times_out_with_retry_log_instead_of_unhandled_failure()
    {
        var messages = new List<string>();
        await Assert.ThrowsAsync<TimeoutException>(() => HTBAM.Infrastructure.Data.SqlMigrationRunner.WaitForDatabaseAsync(
            "Server=127.0.0.1,1;Database=HTBAM;User Id=unused;Password=unused;Encrypt=False;TrustServerCertificate=True",
            messages.Add, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10)));
        Assert.Contains(messages, x => x.Contains("lần 1") && x.Contains("SQL"));
    }

    [Theory]
    [InlineData("xls", "application/vnd.ms-excel")]
    [InlineData("xlsx", Mime)]
    public async Task Real_binary_xls_and_xlsx_fixtures_can_preview_and_import(string extension, string mime)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures"))) directory = directory.Parent;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory!.FullName, "tests", "fixtures", "class-section-unit." + extension));
        await using var db = await AcademicSchedulingTests.Fixture();
        var user = AcademicSchedulingTests.With(new ClassSectionsController(db, new AcademicSchedulingTests.Audit()), 1).User;
        var service = new ClassSectionExcelImport(db);
        var snapshot = await service.Preview(bytes, "test." + extension, mime, "Lịch", new(301, 501), user, Ct);
        Assert.Equal(1, snapshot.ClassesToCreate); Assert.Equal(2, snapshot.SchedulesToCreate);
        Assert.Equal(1, (await service.Confirm(snapshot.Token, user, Ct)).CreatedClasses);
    }

    [Theory]
    [InlineData("2026-2027", 1, "HTBAM", "LHP-2627-HK1-HTBAM-")]
    [InlineData("2025-2026", 2, "CNPM", "LHP-2526-HK2-CNPM-")]
    public void Code_prefix_matches_academic_year_and_semester(string year, int semester, string course, string expected) => Assert.Equal(expected, ClassSectionCodes.Prefix(year, semester, course));

    [Theory]
    [InlineData("2026", 1, "HTBAM")]
    [InlineData("2026-2028", 1, "HTBAM")]
    [InlineData("2026-2027", 3, "HTBAM")]
    [InlineData("2026-2027", 1, "htbam")]
    [InlineData("2026-2027", 1, "HỌC")]
    [InlineData("2026-2027", 1, "HT BAM")]
    [InlineData("2026-2027", 1, "BAD/CODE")]
    public void Invalid_code_context_is_rejected(string year, int semester, string course) => Assert.Throws<ArgumentException>(() => ClassSectionCodes.Prefix(year, semester, course));

    [Fact]
    public async Task Creating_two_classes_allocates_01_02_and_edit_does_not_allocate_or_change_code()
    {
        await using var db = await AcademicSchedulingTests.Fixture();
        var controller = AcademicSchedulingTests.With(new ClassSectionsController(db, new AcademicSchedulingTests.Audit()), 1);
        var first = AcademicSchedulingTests.Request();
        Assert.IsType<OkObjectResult>(await controller.Create(first, Ct));
        Assert.IsType<OkObjectResult>(await controller.Create(AcademicSchedulingTests.Request() with { Schedules = [AcademicSchedulingTests.Line() with { IsoDayOfWeek = 2 }] }, Ct));
        var sections = await db.ClassSections.OrderBy(x => x.Code).ToArrayAsync();
        Assert.Equal(new[] { "LHP-3031-HK1-MH1-01", "LHP-3031-HK1-MH1-02" }, sections.Select(x => x.Code));
        var edit = first with { TeacherId = 101, Revision = sections[0].Revision, Schedules = [first.Schedules[0] with { RoomId = 403, StartPeriod = 4, EndPeriod = 6 }] };
        var plan = await new ClassSchedulePlanner(db).Prepare(edit, sections[0], Ct);
        Assert.IsType<OkObjectResult>(await controller.Update(sections[0].Id, edit with { PreviewToken = plan.PreviewToken }, Ct));
        Assert.Equal("LHP-3031-HK1-MH1-01", (await db.ClassSections.FindAsync(sections[0].Id))!.Code);
        Assert.Equal(2, (await db.ClassSectionCounters.SingleAsync()).LastNumber);
    }

    [Fact]
    public async Task Manual_source_injection_and_imported_source_changes_are_rejected()
    {
        await using var db = await AcademicSchedulingTests.Fixture();
        var controller = AcademicSchedulingTests.With(new ClassSectionsController(db, new AcademicSchedulingTests.Audit()), 1);
        Assert.IsType<BadRequestObjectResult>(await controller.Create(AcademicSchedulingTests.Request() with { SourceClassSectionCode = "INJECT" }, Ct));
        var service = new ClassSectionExcelImport(db); var user = controller.User;
        var snapshot = await service.Preview(Workbook(), "test.xlsx", Mime, "Lịch", new(301, 501), user, Ct);
        await service.Confirm(snapshot.Token, user, Ct);
        var section = await db.ClassSections.SingleAsync();
        Assert.IsType<ForbidResult>(await controller.Update(section.Id, snapshot.Groups[0].Request! with { SourceClassSectionCode = "CHANGED", Revision = section.Revision }, Ct));
        Assert.Equal("SRC-01", section.SourceClassSectionCode);
    }

    [Fact]
    public async Task Workbook_groups_LT_TH_shows_anchor_and_times_and_replay_does_not_duplicate()
    {
        await using var db = await AcademicSchedulingTests.Fixture();
        var service = new ClassSectionExcelImport(db); var user = AcademicSchedulingTests.With(new ClassSectionsController(db, new AcademicSchedulingTests.Audit()), 1).User;
        var bytes = Workbook(duplicate: true);
        Assert.Equal(new[] { "Lịch", "Sheet khác" }, ClassSectionExcelImport.Read(bytes, "test.xlsx", Mime).Select(x => x.Name));
        var preview = await service.Preview(bytes, "test.xlsx", Mime, "Lịch", new(301, 501), user, Ct);
        Assert.Single(preview.Groups); Assert.Equal(1, preview.ClassesToCreate); Assert.Equal(2, preview.SchedulesToCreate); Assert.Equal(1, preview.Duplicates);
        Assert.Contains("02/09/2030", preview.WeekMessage); Assert.Equal("06:30", preview.Groups[0].Rows[0].StartsAt);
        var result = await service.Confirm(preview.Token, user, Ct);
        Assert.Equal(1, result.CreatedClasses); Assert.Equal(2, result.CreatedSchedules);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(result), System.Text.Json.JsonSerializer.Serialize(await service.Confirm(preview.Token, user, Ct)));
        db.ChangeTracker.Clear();
        var repeated = await service.Preview(bytes, "test.xlsx", Mime, "Lịch", new(301, 501), user, Ct);
        Assert.Equal("DUPLICATE", repeated.Groups[0].Status);
        Assert.Equal(0, (await service.Confirm(repeated.Token, user, Ct)).CreatedClasses);
        Assert.Single(await db.ClassSections.ToListAsync()); Assert.Equal(2, await db.ClassSchedules.CountAsync());
        var section = await db.ClassSections.SingleAsync(); Assert.Equal("SRC-01", section.SourceClassSectionCode); Assert.NotEqual(section.Code, section.SourceClassSectionCode);
    }

    [Fact]
    public async Task Missing_header_invalid_row_and_whole_group_errors_have_excel_coordinates()
    {
        await using var db = await AcademicSchedulingTests.Fixture();
        var service = new ClassSectionExcelImport(db); var user = AcademicSchedulingTests.With(new ClassSectionsController(db, new AcademicSchedulingTests.Audit()), 1).User;
        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.Preview(Workbook(missingHeader: true), "test.xlsx", Mime, "Lịch", new(301, 501), user, Ct));
        Assert.Contains("Tiết kết thúc", error.Message);
        var preview = await service.Preview(Workbook(invalid: true), "test.xlsx", Mime, "Lịch", new(301, 501), user, Ct);
        Assert.Equal(0, preview.ClassesToCreate); Assert.Equal(2, preview.ErrorRows);
        var bad = preview.Groups[0].Rows.Single(x => x.Row == 3); Assert.Equal("Tiết bắt đầu", bad.Column);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Confirm(preview.Token, user, Ct));
        Assert.Empty(await db.ClassSchedules.ToListAsync()); Assert.Empty(await db.ClassSectionImportPreviews.ToListAsync());
    }

    [Fact]
    public async Task Missing_anchor_blocks_selected_weeks_but_empty_weeks_are_all_weeks()
    {
        await using var db = await AcademicSchedulingTests.Fixture();
        var term = await db.AcademicTerms.FindAsync(301L); term!.WeekOneMonday = null; term.WeekMappingConfirmed = false; await db.SaveChangesAsync();
        var service = new ClassSectionExcelImport(db); var user = AcademicSchedulingTests.With(new ClassSectionsController(db, new AcademicSchedulingTests.Audit()), 1).User;
        var preview = await service.Preview(Workbook(), "test.xlsx", Mime, "Lịch", new(301, 501), user, Ct);
        Assert.Equal("ERROR", preview.Groups[0].Status); Assert.Null(preview.WeekOneMonday); Assert.Contains("Chưa cấu hình", preview.WeekMessage);
        preview = await service.Preview(Workbook(allWeeks: true), "test.xlsx", Mime, "Lịch", new(301, 501), user, Ct);
        Assert.Equal("WARNING", preview.Groups[0].Status); Assert.Equal(1, preview.ClassesToCreate);
    }

    [Fact]
    public async Task Preview_is_bound_to_user_and_expiry_and_permissions()
    {
        await using var db = await AcademicSchedulingTests.Fixture();
        var service = new ClassSectionExcelImport(db); var controller = AcademicSchedulingTests.With(new ClassSectionsController(db, new AcademicSchedulingTests.Audit()), 1);
        var preview = await service.Preview(Workbook(), "test.xlsx", Mime, "Lịch", new(301, 501), controller.User, Ct);
        var viewer = AcademicSchedulingTests.With(new ClassSectionsController(db, new AcademicSchedulingTests.Audit()), 2).User;
        Assert.False(await service.Allowed(viewer, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.Confirm(preview.Token, viewer, Ct));
        Assert.Empty(await db.ClassSectionImportPreviews.ToListAsync());
        preview.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Confirm(preview.Token, controller.User, Ct));
        Assert.Empty(await db.ClassSections.ToListAsync());
    }

    [Theory]
    [InlineData("T3, T1, T3", new[] { 1, 3 })]
    [InlineData(" 2 ; 1 ", new[] { 1, 2 })]
    [InlineData("", new int[0])]
    public void Weeks_are_normalized_sorted_and_deduplicated(string input, int[] expected) => Assert.Equal(expected, ClassSectionExcelImport.ParseWeeks(input));

    [Theory]
    [InlineData("T0")][InlineData("1,54")][InlineData("1, bad")][InlineData("1-3")]
    public void Invalid_weeks_are_rejected(string input) => Assert.Throws<ArgumentException>(() => ClassSectionExcelImport.ParseWeeks(input));

    [Fact]
    public async Task Weeks_beyond_term_or_line_are_rejected_and_T2_is_seven_days_after_T1()
    {
        await using var db = await AcademicSchedulingTests.Fixture(); var planner = new ClassSchedulePlanner(db);
        var r = AcademicSchedulingTests.Request() with { Schedules = [AcademicSchedulingTests.Line() with { SelectedWeeks = [1, 2, 2] }] };
        var plan = await planner.Prepare(r, null, Ct); Assert.Equal(2, plan.Occurrences.Length); Assert.Equal(7, plan.Occurrences[1].Date.DayNumber - plan.Occurrences[0].Date.DayNumber);
        await Assert.ThrowsAsync<ArgumentException>(() => planner.Prepare(r with { Schedules = [r.Schedules[0] with { SelectedWeeks = [5] }] }, null, Ct));
    }

    [Fact]
    public void Upload_rejects_extension_mime_signature_and_oversize()
    {
        Assert.Throws<ArgumentException>(() => ClassSectionExcelImport.Read(Workbook(), "test.csv", Mime));
        Assert.Throws<ArgumentException>(() => ClassSectionExcelImport.Read(Workbook(), "test.xlsx", "text/plain"));
        Assert.Throws<ArgumentException>(() => ClassSectionExcelImport.Read([1, 2, 3], "test.xlsx", Mime));
        Assert.Throws<ArgumentException>(() => ClassSectionExcelImport.Read(new byte[ClassSectionExcelImport.MaxBytes + 1], "test.xlsx", Mime));
    }

    internal static byte[] Workbook(bool duplicate = false, bool missingHeader = false, bool invalid = false, bool allWeeks = false)
    {
        using var workbook = new XLWorkbook(); var sheet = workbook.AddWorksheet("Lịch"); workbook.AddWorksheet("Sheet khác");
        var columns = ClassSectionExcelImport.RequiredColumns.Concat(new[] { "Các tuần có lịch" }).ToArray();
        for (var i = 0; i < columns.Length; i++) sheet.Cell(1, i + 1).Value = missingHeader && i == 6 ? "Sai tiêu đề" : columns[i];
        string[][] rows = [["SRC-01", "MH1", "GV2", "LT", "2", "1", "3", "R1", "02/09/2030", "29/09/2030", allWeeks ? "" : "T1, T3"],
            ["SRC-01", "MH1", "GV2", "TH", "3", invalid ? "sai" : "7", "9", "R2", "02/09/2030", "29/09/2030", allWeeks ? "" : "T1, T3"]];
        if (duplicate) rows = [.. rows, rows[0]];
        for (var i = 0; i < rows.Length; i++) for (var j = 0; j < rows[i].Length; j++) sheet.Cell(i + 2, j + 1).Value = rows[i][j];
        using var stream = new MemoryStream(); workbook.SaveAs(stream); return stream.ToArray();
    }
}
