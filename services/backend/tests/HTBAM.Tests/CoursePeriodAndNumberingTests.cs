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

public sealed class CoursePeriodAndNumberingTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData(" 2026 – 2027 ", "1", 2026, "HK1")]
    [InlineData("2025-2026", " hk2 ", 2025, "HK2")]
    public void Start_period_normalizes_keyboard_values(string year, string semester, int expected, string term)
    {
        var period = CourseStartPeriod.Parse(year, semester);
        Assert.Equal((expected, expected + 1, term), period);
    }

    [Theory]
    [InlineData(null, "HK1")][InlineData("26-27", "HK1")][InlineData("2026", "HK1")]
    [InlineData("2026/2027", "HK1")][InlineData("2026-2028", "HK1")]
    [InlineData("2026-2027", "HK3")][InlineData("2026-2027", null)]
    public void Invalid_or_missing_period_is_rejected(string? year, string? term) =>
        Assert.Throws<ArgumentException>(() => CourseStartPeriod.Parse(year, term));

    [Fact]
    public async Task Course_period_roundtrips_and_edit_requires_legacy_records_to_be_completed()
    {
        await using var db = await Fixture();
        (await db.DepartmentsSet.FindAsync(11L))!.Code = "BM-CNDL";
        await db.SaveChangesAsync();
        var api = With(new CatalogsController(db, new Audit()), 1);
        var course = Assert.IsType<Course>(Assert.IsType<OkObjectResult>(await api.AddCourse(
            new("FORGED", "Môn học", 11, 3, null, StartAcademicYear: " 2026 – 2027 ", StartSemester: "1"), Ct)).Value);
        Assert.Equal("MH-CNDL-001", course.Code);
        Assert.Equal("2026-2027", course.StartAcademicYear);
        Assert.Equal("HK1", course.StartSemester);
        Assert.IsType<NoContentResult>(await api.UpdateCourse(course.Id,
            new("FORGED-EDIT", "Tên mới", 11, 3, null, StartAcademicYear: "2025-2026", StartSemester: "HK2"), Ct));
        db.ChangeTracker.Clear();
        var saved = await db.Courses.SingleAsync(x => x.Id == course.Id);
        Assert.Equal("MH-CNDL-001", saved.Code);
        Assert.Equal("2025-2026", saved.StartAcademicYear);
        Assert.Equal("HK2", saved.StartSemester);
        await Assert.ThrowsAsync<ArgumentException>(() => api.UpdateCourse(saved.Id, new(null, "Missing", 11, 3, null), Ct));
    }

    [Fact]
    public async Task Generic_sequence_bootstraps_existing_codes_and_preserves_unsuffixed_identity()
    {
        await using var db = await Fixture();
        db.CamerasSet.Add(new() { Code = "CAM-CS1-A-101-09", Name = "Legacy", RoomId = 1, RtspUrl = "rtsp://example.invalid" });
        db.FacultiesSet.Add(new() { Code = "KHOA-CNDL", Name = "Existing" });
        await db.SaveChangesAsync();
        var camera = await BusinessTransaction.Run(db, BusinessCodes.Resource, async () => {
            var code = await BusinessCodes.Next(db, "CAM-CS1-A-101-", 2, db.CamerasSet.Select(x => x.Code), Ct);
            await db.SaveChangesAsync(); return code;
        }, Ct);
        Assert.Equal("CAM-CS1-A-101-10", camera);
        var faculty = await BusinessTransaction.Run(db, BusinessCodes.Resource, async () => {
            var code = await BusinessCodes.Next(db, "KHOA-CNDL-", 2, db.FacultiesSet.Select(x => x.Code), Ct, true);
            await db.SaveChangesAsync(); return code;
        }, Ct);
        Assert.Equal("KHOA-CNDL-02", faculty);
        Assert.Equal("PH-CS1-A-101", BusinessCodes.RoomPrefix("cs1", "a", "101", "THEORY"));
        Assert.Equal("PH-ONLINE-ZOOM48", BusinessCodes.RoomPrefix(null, null, "zoom48", "ZOOM"));
        Assert.Throws<ArgumentException>(() => BusinessCodes.RoomPrefix(null, "A", "101", "THEORY"));
        Assert.Equal("LHP-2627-HK1-CNDL-001-", ClassSectionCodes.Prefix("2026-2027", 1, "MH-CNDL-001"));
    }

    [SqlServerFact]
    public async Task Sql_sequence_serializes_concurrent_allocations_and_constraints_reject_invalid_periods()
    {
        await using var database = await AcademicSqlServerTests.IsolatedDatabase.Create();
        var codes = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ => {
            await using var db = database.Context();
            return await BusinessTransaction.Run(db, BusinessCodes.Resource, async () => {
                var code = await BusinessCodes.Next(db, "CC-", 5, db.AttendancePoliciesSet.Select(x => x.Code), Ct);
                db.AttendancePoliciesSet.Add(new() { Code = code, Name = "Concurrent test", Version = "v1" });
                await db.SaveChangesAsync(); return code;
            }, Ct);
        }));
        Assert.Equal(12, codes.Distinct().Count());
        Assert.Contains("CC-00001", codes); Assert.Contains("CC-00012", codes);
        await using var context = database.Context();
        context.Courses.Add(new() { Code = "MH-TEST-001", Name = "Legacy nullable period" });
        await context.SaveChangesAsync();
        context.Courses.Add(new() { Code = "MH-TEST-002", Name = "Invalid", StartYear = 2026, EndYear = 2028, StartSemester = "HK1" });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
        context.Courses.Add(new() { Code = "MH-TEST-003", Name = "Invalid", StartYear = 2026, EndYear = 2027, StartSemester = "HK3" });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        await database.Apply("014_course_period_and_business_sequences.sql");
    }
}
