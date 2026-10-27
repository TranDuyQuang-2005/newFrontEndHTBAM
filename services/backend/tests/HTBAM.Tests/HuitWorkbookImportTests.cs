using System.Diagnostics;
using ClosedXML.Excel;
using HTBAM.Api.Controllers;
using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace HTBAM.Tests;

public sealed class RealHuitWorkbookFactAttribute : FactAttribute
{
    public RealHuitWorkbookFactAttribute() { if (!File.Exists(Environment.GetEnvironmentVariable("HTBAM_HUIT_WORKBOOK"))) Skip = "Set HTBAM_HUIT_WORKBOOK to the real HUIT timetable workbook."; }
}

public sealed class HuitWorkbookImportTests(ITestOutputHelper output)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static string FixtureFile(string extension)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory); while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "fixtures", "huit-two-header." + extension);
    }
    private static async Task<(HTBAM.Infrastructure.Data.AppDbContext Db, System.Security.Claims.ClaimsPrincipal User)> Context()
    {
        var db = await AcademicSchedulingTests.Fixture(); (await db.Teachers.FindAsync(102L))!.TeacherCode = "00102"; await db.SaveChangesAsync();
        return (db, AcademicSchedulingTests.With(new ClassSectionsController(db, new AcademicSchedulingTests.Audit()), 1).User);
    }

    private static void AssertHeaderAndColumns(ImportSheetInspection info)
    {
        Assert.Equal(7, info.HeaderStartRow); Assert.Equal(2, info.HeaderRowCount); Assert.Equal(8, info.HeaderEndRow); Assert.Equal(9, info.DataStartRow);
        Assert.Equal("Thời gian học / Bắt đầu", info.Columns[10].Header); Assert.Equal("K", info.Columns[10].Letter);
        for (var week = 1; week <= 26; week++) Assert.Equal("Tuần học / T" + week, info.Columns[week + 10].Header);
        Assert.Equal("L", info.Columns[11].Letter); Assert.Equal("AK", info.Columns[36].Letter);
        Assert.Equal("Thời gian học / Kết thúc", info.Columns[37].Header); Assert.Equal("AL", info.Columns[37].Letter);
        Assert.Equal("Tổng số tiết", info.Columns[38].Header); Assert.Equal("AM", info.Columns[38].Letter);
        Assert.Equal("Phòng", info.Columns[39].Header); Assert.Equal("AN", info.Columns[39].Letter);
        Assert.Equal("Giảng viên", info.Columns[40].Header); Assert.Equal("AO", info.Columns[40].Letter);
        Assert.Equal("Mã LHP", info.Columns[1].Header); Assert.DoesNotContain(info.Columns, x => x.Header == "Cột M");
        var group = Assert.Single(info.ColumnGroups); Assert.Equal("weeks", group.Field); Assert.Equal(26, group.Columns.Length);
        Assert.Equal("week1", group.Columns[0].Field); Assert.Equal(11, group.Columns[0].ColumnIndex); Assert.Equal(36, group.Columns[^1].ColumnIndex);
        Assert.InRange(info.SampleRows.Length, 2, 5); Assert.Equal(9, info.SampleRows[0].Row); Assert.NotEmpty(info.Columns[10].Sample); Assert.NotEmpty(info.Columns[40].Sample);
    }

    [Theory]
    [InlineData("xls", "application/vnd.ms-excel")]
    [InlineData("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task Two_header_HUIT_maps_names_teacher_codes_week_marks_dates_and_source_totals(string extension, string mime)
    {
        var bytes = await File.ReadAllBytesAsync(FixtureFile(extension)); var sheets = ClassSectionExcelImport.Read(bytes, "test." + extension, mime);
        var inspect = ClassSectionWorkbook.Inspect(sheets[0]); Assert.Equal("HUIT", inspect.Format); Assert.Equal(7, inspect.HeaderRow); Assert.Equal(2, inspect.HeaderRowCount); Assert.Equal(41, inspect.ColumnCount); Assert.Empty(inspect.MissingColumns);
        AssertHeaderAndColumns(inspect);
        Assert.Equal(26, inspect.Mappings.Count(x => x.Field.StartsWith("week") && x.Field != "weeks"));
        var (db, user) = await Context(); await using var owned = db;
        var preview = await new ClassSectionExcelImport(db).Preview(bytes, "test." + extension, mime, "HUIT", new(301, 501), user, Ct);
        Assert.Equal(1, preview.ClassesToCreate); Assert.Equal(2, preview.SchedulesToCreate); Assert.Equal(0, preview.ErrorRows);
        var rows = preview.Groups[0].Rows; Assert.Equal("0000123456789", rows[0].SourceClassSectionCode); Assert.Equal(201, rows[0].CourseId); Assert.Equal("00102", rows[0].Teacher);
        Assert.Equal(new[] { 1, 2, 4 }, rows[0].Schedule!.SelectedWeeks); Assert.Equal(new[] { 1, 2, 3, 4 }, rows[1].Schedule!.SelectedWeeks);
        Assert.Equal(new DateOnly(2030, 9, 9), rows[0].Weeks[1].StartsOn); Assert.Equal("LT", rows[0].Schedule!.ActivityType); Assert.Equal("TH", rows[1].Schedule!.ActivityType);
        Assert.Equal(401, rows[0].RoomId); Assert.Equal(12, rows[0].SourceSchedulePeriods); Assert.Equal(12, rows[0].SourceTotalPeriods);
        Assert.Empty(await db.ClassSections.ToListAsync()); Assert.Empty(await db.ClassSectionImportPreviews.ToListAsync()); Assert.False(db.ChangeTracker.HasChanges());
        var result = await new ClassSectionExcelImport(db).Confirm(preview.Token, user, Ct); Assert.Equal(1, result.CreatedClasses); Assert.Equal(2, result.CreatedSchedules);
        Assert.Contains("sourceSchedulePeriods", (await db.ClassSchedules.FirstAsync()).SourceDataJson!);
        var again = await new ClassSectionExcelImport(db).Preview(bytes, "test." + extension, mime, "HUIT", new(301, 501), user, Ct); Assert.Equal(2, again.Duplicates);
    }

    [Fact]
    public void Scan_limit_is_not_header_start_and_manual_recognition_rebuilds_columns_and_samples()
    {
        var sheet = ClassSectionWorkbook.Read(File.ReadAllBytes(FixtureFile("xlsx")), "huit.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")[0];
        AssertHeaderAndColumns(ClassSectionWorkbook.Inspect(sheet));
        var shifted = sheet with { Rows = new List<object?[]> { new object?[41] }.Concat(sheet.Rows).ToList() };
        var auto = ClassSectionWorkbook.Inspect(shifted); Assert.Equal(8, auto.HeaderStartRow); Assert.Equal(10, auto.DataStartRow);
        var manual = ClassSectionWorkbook.Inspect(shifted, 8, 2); Assert.Equal(auto.Columns, manual.Columns); Assert.Equal(10, manual.SampleRows[0].Row);
        Assert.Equal(7, ClassSectionWorkbook.Inspect(sheet).HeaderStartRow);
    }

    [Fact]
    public void Duplicate_single_column_mapping_and_overriding_one_HUIT_week_column_are_rejected()
    {
        var sheet = ClassSectionWorkbook.Read(File.ReadAllBytes(FixtureFile("xlsx")), "huit.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")[0];
        Assert.Contains("nhiều trường", Assert.Throws<ArgumentException>(() => ClassSectionWorkbook.Inspect(sheet, overrides: new() { ["to"] = 10 })).Message);
        Assert.Throws<ArgumentException>(() => ClassSectionWorkbook.Inspect(sheet, overrides: new() { ["weeks"] = 11 }));
        Assert.Throws<ArgumentException>(() => ClassSectionWorkbook.Inspect(sheet, overrides: new() { ["week2"] = 14 }));
    }

    [Fact]
    public async Task T1_T3_T5_marks_are_sorted_and_normalized_with_check_symbols()
    {
        using var book = new XLWorkbook(FixtureFile("xlsx")); var sheet = book.Worksheet("HUIT"); sheet.Range(9, 12, 9, 37).Clear();
        sheet.Cell(9, 12).Value = "x"; sheet.Cell(9, 14).Value = "✓"; sheet.Cell(9, 16).Value = "true";
        using var stream = new MemoryStream(); book.SaveAs(stream); var (db, user) = await Context(); await using var owned = db;
        var preview = await new ClassSectionExcelImport(db).Preview(stream.ToArray(), "huit.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "HUIT", new(301, 501), user, Ct);
        Assert.Equal(new[] {1,3,5}, preview.Groups[0].Rows[0].Schedule!.SelectedWeeks);
        Assert.Equal(new[] {1,3,5}, preview.Groups[0].Rows[0].Weeks.Select(w => w.Number));
    }

    [Fact]
    public async Task Same_source_and_time_with_T1_to_T10_and_T12_T15_to_T18_remain_two_schedules_and_reimport_is_safe()
    {
        using var book = new XLWorkbook(FixtureFile("xlsx")); var sheet = book.Worksheet("HUIT"); sheet.Range(9, 12, 10, 37).Clear();
        for (var week = 1; week <= 10; week++) sheet.Cell(9, 11 + week).Value = "x";
        foreach (var week in new[] {12,15,16,17,18}) sheet.Cell(10, 11 + week).Value = "x";
        sheet.Cell(9, 38).Value = "10/11/2030"; sheet.Cell(10, 11).Value = "18/11/2030"; sheet.Cell(10, 38).Value = "05/01/2031";
        sheet.Cell(10, 6).Value = 2; sheet.Cell(10, 7).Value = 1; sheet.Cell(10, 8).Value = 3; sheet.Cell(10, 9).Value = "LT"; sheet.Cell(10, 40).Value = "R1";
        using var stream = new MemoryStream(); book.SaveAs(stream); var bytes = stream.ToArray(); var (db, user) = await Context(); await using var owned = db;
        (await db.AcademicTerms.FindAsync(301L))!.EndsOn = new(2031,1,5); await db.SaveChangesAsync();
        var service = new ClassSectionExcelImport(db); var preview = await service.Preview(bytes, "huit.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "HUIT", new(301,501), user, Ct);
        Assert.Equal(0, preview.ErrorRows); Assert.Equal(1, preview.ClassesToCreate); Assert.Equal(2, preview.SchedulesToCreate);
        Assert.Equal(Enumerable.Range(1,10), preview.Groups[0].Rows[0].Schedule!.SelectedWeeks); Assert.Equal(new[] {12,15,16,17,18}, preview.Groups[0].Rows[1].Schedule!.SelectedWeeks);
        var result = await service.Confirm(preview.Token, user, Ct); Assert.Equal(1, result.CreatedClasses); Assert.Equal(2, result.CreatedSchedules);
        Assert.Equal(2, await db.ClassSchedules.CountAsync());
        var again = await service.Preview(bytes, "huit.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "HUIT", new(301,501), user, Ct); Assert.Equal(2, again.Duplicates); Assert.Equal(0, again.SchedulesToCreate);
    }

    [Fact]
    public async Task HUIT_numeric_8_is_sunday_and_is_not_interpreted_as_an_invalid_weekday()
    {
        using var book = new XLWorkbook(FixtureFile("xlsx")); book.Worksheet("HUIT").Cell(10, 6).Value = 8;
        using var stream = new MemoryStream(); book.SaveAs(stream); var (db, user) = await Context(); await using var owned = db;
        var preview = await new ClassSectionExcelImport(db).Preview(stream.ToArray(), "test.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "HUIT", new(301, 501), user, Ct);
        Assert.Equal(7, preview.Groups[0].Rows[1].Schedule!.IsoDayOfWeek); Assert.Equal(0, preview.ErrorRows);
    }

    [Fact]
    public async Task Unresolved_or_ambiguous_courses_require_explicit_selection_and_never_create_catalogs()
    {
        var bytes = await File.ReadAllBytesAsync(FixtureFile("xlsx")); var (db, user) = await Context(); await using var owned = db;
        var course = await db.Courses.FindAsync(201L); course!.Name = "Another name"; await db.SaveChangesAsync(); var service = new ClassSectionExcelImport(db);
        var missing = await service.Preview(bytes, "test.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "HUIT", new(301, 501), user, Ct);
        Assert.Equal(2, missing.ErrorRows); Assert.Single(await db.Courses.ToArrayAsync());
        var selected = await service.Preview(bytes, "test.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "HUIT", new(301, 501, CourseSelections: new() { ["0000123456789"] = 201 }), user, Ct);
        Assert.Equal(1, selected.ClassesToCreate); Assert.Equal(2, selected.WarningRows);
        course.Name = "Course"; db.Courses.Add(new() { Code = "OTHER", Name = "Course", DepartmentId = 11, Credits = 3 }); await db.SaveChangesAsync();
        var ambiguous = await service.Preview(bytes, "test.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "HUIT", new(301, 501), user, Ct);
        Assert.Contains("nhiều môn", ambiguous.Groups[0].Rows[0].Message!);
    }

    [Theory]
    [InlineData("source")][InlineData("activity")][InlineData("teacher")][InlineData("room")]
    public async Task Missing_required_data_identifies_excel_row_and_column(string field)
    {
        using var book = new XLWorkbook(FixtureFile("xlsx")); var sheet = book.Worksheet("HUIT"); var index = field switch { "source" => 2, "activity" => 9, "teacher" => 41, _ => 40 }; sheet.Cell(9, index).Clear();
        using var stream = new MemoryStream(); book.SaveAs(stream); var (db, user) = await Context(); await using var owned = db;
        var preview = await new ClassSectionExcelImport(db).Preview(stream.ToArray(), "test.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "HUIT", new(301, 501), user, Ct);
        Assert.True(preview.ErrorRows > 0); Assert.Contains(preview.Groups.SelectMany(x => x.Rows), row => row.Row == 9 && row.Issues.Count > 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ClassSectionExcelImport(db).Confirm(preview.Token, user, Ct)); Assert.Empty(await db.ClassSectionImportPreviews.ToListAsync());
    }

    [Fact]
    public async Task HUIT_empty_week_grid_is_not_all_weeks_and_missing_anchor_is_blocking()
    {
        var (db, user) = await Context(); await using var owned = db; var service = new ClassSectionExcelImport(db);
        using var book = new XLWorkbook(FixtureFile("xlsx")); book.Worksheet("HUIT").Range(9, 12, 10, 37).Clear(); using var stream = new MemoryStream(); book.SaveAs(stream);
        var empty = await service.Preview(stream.ToArray(), "test.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "HUIT", new(301, 501), user, Ct);
        Assert.Equal(2, empty.ErrorRows); Assert.Contains("Không có tuần", empty.Groups[0].Rows[0].Message!);
        var term = await db.AcademicTerms.FindAsync(301L); term!.WeekOneMonday = null; term.WeekMappingConfirmed = false; await db.SaveChangesAsync();
        var noAnchor = await service.Preview(await File.ReadAllBytesAsync(FixtureFile("xlsx")), "test.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "HUIT", new(301, 501), user, Ct);
        Assert.Equal(2, noAnchor.ErrorRows); Assert.Null(noAnchor.WeekOneMonday); Assert.All(noAnchor.Groups[0].Rows[0].Weeks, x => Assert.Null(x.StartsOn));
    }

    [Fact]
    public void Header_override_column_mapping_and_numeric_limit_messages_use_actual_counts()
    {
        var sheets = ClassSectionExcelImport.Read(File.ReadAllBytes(FixtureFile("xlsx")), "test.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        Assert.Equal(7, ClassSectionWorkbook.Inspect(sheets[0], 7, 2, new() { ["room"] = 39 }).HeaderRow);
        Assert.Throws<ArgumentException>(() => ClassSectionWorkbook.Inspect(sheets[0], 21, 2));
        using var book = new XLWorkbook(); var sheet = book.AddWorksheet("Too wide"); sheet.Cell(1, 65).Value = "65 columns"; using var stream = new MemoryStream(); book.SaveAs(stream);
        var error = Assert.Throws<ArgumentException>(() => ClassSectionExcelImport.Read(stream.ToArray(), "test.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        Assert.Contains("65 cột", error.Message); Assert.Contains("20.000", error.Message);
    }

    [RealHuitWorkbookFact]
    public async Task Real_HUIT_10202_rows_41_columns_is_fully_validated_and_only_100_rows_are_returned()
    {
        var path = Environment.GetEnvironmentVariable("HTBAM_HUIT_WORKBOOK")!; var timer = Stopwatch.StartNew(); var bytes = await File.ReadAllBytesAsync(path);
        var sheets = ClassSectionExcelImport.Read(bytes, Path.GetFileName(path), "application/vnd.ms-excel"); var inspect = ClassSectionWorkbook.Inspect(sheets[0]);
        Assert.Equal(10202, inspect.RowCount); Assert.Equal(41, inspect.ColumnCount); Assert.Equal(7, inspect.HeaderRow); Assert.Equal(2, inspect.HeaderRowCount);
        AssertHeaderAndColumns(inspect); Assert.Equal(5, inspect.SampleRows.Length);
        var (db, user) = await Context(); await using var owned = db;
        var term = await db.AcademicTerms.SingleAsync(x => x.AcademicYear == "2025-2026" && x.Semester == 1);
        var preview = await new ClassSectionExcelImport(db).Preview(bytes, Path.GetFileName(path), "application/vnd.ms-excel", sheets[0].Name, new(term.Id, 501), user, Ct);
        Assert.Equal(10190, preview.DataRows); Assert.Equal(5447, preview.Groups.Count); Assert.Equal(10190, preview.ErrorRows); Assert.Null(preview.WeekOneMonday);
        Assert.Equal(801, preview.Groups.SelectMany(g => g.Rows).Count(r => r.Schedule!.IsoDayOfWeek == 7));
        Assert.All(preview.Groups.SelectMany(g => g.Rows), r => Assert.InRange(r.Schedule!.IsoDayOfWeek, 1, 7));
        Assert.Equal(100, ClassSectionExcelImport.Page(preview).Groups.Sum(x => x.Rows.Length));
        Assert.Equal(200, ClassSectionExcelImport.Page(preview, pageSize: 10000).Groups.Sum(x => x.Rows.Length));
        var page2 = ClassSectionExcelImport.Page(preview, 2); Assert.True(page2.Groups[0].Rows[0].Row > ClassSectionExcelImport.Page(preview).Groups[0].Rows[0].Row);
        var search = ClassSectionExcelImport.Page(preview, query: "010110215301", status: "ERROR"); Assert.Equal(2, search.MatchingRows); Assert.Single(search.Groups);
        Assert.Empty(await db.ClassSectionImportPreviews.ToArrayAsync()); Assert.Empty(await db.ClassSections.ToArrayAsync()); Assert.False(db.ChangeTracker.HasChanges());
        output.WriteLine($"HUIT_ACTUAL_OK: rows={inspect.RowCount}; columns={inspect.ColumnCount}; data={preview.DataRows}; groups={preview.Groups.Count}; errors={preview.ErrorRows}; page=100; elapsed={timer.Elapsed.TotalSeconds:F2}s; bytes={bytes.Length}");
    }
}
