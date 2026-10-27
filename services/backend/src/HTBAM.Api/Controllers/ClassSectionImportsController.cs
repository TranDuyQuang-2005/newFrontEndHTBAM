using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;

namespace HTBAM.Api.Controllers;

[ApiController, Authorize(Roles = "LECTURER"), Route("api/class-sections/import")]
public sealed class ClassSectionImportsController(AppDbContext db, IAuditService audit) : ControllerBase
{
    [HttpPost("sheets"), RequestSizeLimit(ClassSectionExcelImport.MaxBytes + 65536)]
    public async Task<ActionResult> Sheets(IFormFile file, CancellationToken ct)
    {
        if (!await new ClassSectionExcelImport(db).Allowed(User, ct)) return Forbid();
        var bytes = await Bytes(file, ct);
        var sheets = ClassSectionExcelImport.Read(bytes, file.FileName, file.ContentType);
        return Ok(new { sheets = sheets.Select(x => x.Name), sheetDetails = sheets.Select(x => ClassSectionWorkbook.Inspect(x)), maxBytes = ClassSectionExcelImport.MaxBytes,
            maxRows = ClassSectionWorkbook.MaxRows, maxColumns = ClassSectionWorkbook.MaxColumns, requiredColumns = ClassSectionExcelImport.RequiredColumns });
    }

    [HttpPost("inspect"), RequestSizeLimit(ClassSectionExcelImport.MaxBytes + 65536)]
    public async Task<ActionResult> Inspect(IFormFile file, [FromForm] string sheet, [FromForm] int? headerRow, [FromForm] int? headerRowCount, CancellationToken ct)
    {
        if (!await new ClassSectionExcelImport(db).Allowed(User, ct)) return Forbid();
        var selected = ClassSectionExcelImport.Read(await Bytes(file, ct), file.FileName, file.ContentType).SingleOrDefault(x => x.Name == sheet) ?? throw new ArgumentException("Sheet đã chọn không tồn tại.");
        return Ok(ClassSectionWorkbook.Inspect(selected, headerRow, headerRowCount));
    }

    [HttpPost("preview"), RequestSizeLimit(ClassSectionExcelImport.MaxBytes + 65536)]
    public async Task<ActionResult> Preview(IFormFile file, [FromForm] string sheet, [FromForm] long academicTermId, [FromForm] long attendancePolicyId, [FromForm] int? phase, CancellationToken ct, [FromForm] string? settings = null)
    {
        var service = new ClassSectionExcelImport(db);
        if (!await service.Allowed(User, ct)) return Forbid();
        if (settings?.Length > 500000) return BadRequest("Ánh xạ xem trước vượt giới hạn dung lượng.");
        ImportOptions options;
        try { options = string.IsNullOrEmpty(settings) ? new(academicTermId, attendancePolicyId, phase) : (JsonSerializer.Deserialize<ImportOptions>(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException()) with { AcademicTermId = academicTermId, AttendancePolicyId = attendancePolicyId, Phase = phase }; }
        catch (JsonException) { return BadRequest("Cấu hình ánh xạ không đúng định dạng JSON."); }
        return Ok(ClassSectionExcelImport.Page(await service.Preview(await Bytes(file, ct), file.FileName, file.ContentType, sheet, options, User, ct)));
    }

    [HttpGet("preview/{token}")]
    public async Task<ActionResult> PreviewPage(string token, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, [FromQuery] string? q = null, [FromQuery] string? status = null, CancellationToken ct = default) =>
        Ok(await new ClassSectionExcelImport(db).GetPage(token, User, page, pageSize, q, status, ct));

    [HttpGet("template")]
    public async Task<ActionResult> Template(CancellationToken ct)
    {
        if (!await new ClassSectionExcelImport(db).Allowed(User, ct)) return Forbid();
        using var book = new XLWorkbook(); var sheet = book.AddWorksheet("Mẫu lớp học phần");
        string[] headers = [.. ClassSectionExcelImport.RequiredColumns, "Tên lớp", "Các tuần có lịch", "Giai đoạn", "Nhóm giờ"];
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        string[][] examples = [["000012345678", "MA-MON", "MA-GV", "LT", "2", "1", "3", "MA-PHONG", "17/08/2026", "20/12/2026", "Tên lớp mẫu", "T1,T3,T5", "", "A"],
            ["000012345678", "MA-MON", "MA-GV", "TH", "3", "7", "9", "MA-PHONG-TH", "17/08/2026", "20/12/2026", "Tên lớp mẫu", "", "", "B"]];
        for (var r = 0; r < examples.Length; r++) for (var c = 0; c < headers.Length; c++) sheet.Cell(r + 2, c + 1).Value = examples[r][c];
        sheet.Row(1).Style.Font.Bold = true; sheet.SheetView.FreezeRows(1); sheet.Columns().Width = 24;
        var guide = book.AddWorksheet("Hướng dẫn"); string[] notes = ["Thay mã môn, mã GV và mã phòng bằng dữ liệu danh mục hiện có. Không tự tạo danh mục khi import.",
            "Mã LHP là mã nguồn dạng văn bản; không phải mã nội bộ. Nhiều dòng cùng mã nguồn được gom thành một lớp.", "Các tuần có lịch để trống = học tất cả tuần theo khoảng ngày. Chọn tuần bằng T1,T3,T5; cần mốc T1 đã xác nhận.",
            "Ngày dùng dd/MM/yyyy. Giai đoạn cần cho môn 1/2 tín chỉ theo cấu hình học kỳ.", "Các ngày và giá trị trong file này chỉ là ví dụ, không phải lịch chính thức."];
        for (var i = 0; i < notes.Length; i++) guide.Cell(i + 1, 1).Value = notes[i]; guide.Column(1).Width = 100; guide.Column(1).Style.Alignment.WrapText = true;
        using var stream = new MemoryStream(); book.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "mau-nhap-lop-hoc-phan.xlsx");
    }

    [HttpGet("result/{token}/download")]
    public async Task<ActionResult> DownloadResult(string token, CancellationToken ct)
    {
        var service = new ClassSectionExcelImport(db); if (!await service.Allowed(User, ct)) return Forbid();
        var result = await service.Result(token, User, ct);
        string Cell(string? value) { var text = value ?? ""; if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text; return "\"" + text.Replace("\"", "\"\"") + "\""; }
        var lines = new List<string> { "Mã nội bộ,Mã lớp nguồn,Dòng Excel,Cột,Lỗi", $"Tạo {result.CreatedClasses} lớp,Tạo {result.CreatedSchedules} lịch,Bỏ qua {result.SkippedRows} dòng,Trùng {result.Duplicates} dòng," };
        lines.AddRange(result.Classes.Select(x => $"{Cell(x.Code)},{Cell(x.SourceClassSectionCode)},,,"));
        lines.AddRange(result.Errors.Select(x => $",{Cell(x.SourceClassSectionCode)},{x.Row},{Cell(x.Column)},{Cell(x.Message)}"));
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", lines))).ToArray(), "text/csv; charset=utf-8", "ket-qua-nhap-lop-hoc-phan.csv");
    }

    [HttpGet("preview/{token}/download")]
    public async Task<ActionResult> DownloadValidation(string token, CancellationToken ct)
    {
        if (!await new ClassSectionExcelImport(db).Allowed(User, ct)) return Forbid();
        var snapshot = ImportPreviewStore.Get(UserContext.Id(User)!.Value, token);
        string Cell(string? value) { var text = value ?? ""; if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text; return "\"" + text.Replace("\"", "\"\"") + "\""; }
        var lines = new List<string> { "Dòng Excel,Mã lớp nguồn,Trạng thái,Môn học,Giảng viên,Phòng,Các tuần có lịch,Lỗi hoặc cảnh báo" };
        lines.AddRange(snapshot.Groups.SelectMany(g => g.Rows.Select(r => string.Join(",", r.Row.ToString(), Cell(g.SourceClassSectionCode), Cell(r.Status), Cell(r.SourceCourseName ?? r.Course),
            Cell(r.SourceTeacher ?? r.Teacher), Cell(r.SourceRoom ?? r.Room), Cell(string.Join(",", r.Schedule?.SelectedWeeks ?? [])), Cell(r.Message ?? g.Message)))));
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", lines))).ToArray(), "text/csv; charset=utf-8", "bao-cao-kiem-tra-lop-hoc-phan.csv");
    }

    [HttpPost("confirm")]
    public Task<ActionResult> Confirm(ImportConfirmRequest request, CancellationToken ct) =>
        BusinessTransaction.Run<ActionResult>(db, "HTBAM:academic-scheduling", async () =>
        {
            var service = new ClassSectionExcelImport(db);
            if (!await service.Allowed(User, ct)) return Forbid();
            var result = await service.Confirm(request.Token, User, ct);
            await audit.WriteAsync(UserContext.Id(User), "CLASS_SECTION_IMPORT", "ClassSectionImport", request.Token, new { result.CreatedClasses, result.CreatedSchedules }, ct);
            return Ok(result);
        }, ct);

    private static async Task<byte[]> Bytes(IFormFile file, CancellationToken ct)
    {
        if (file.Length is 0 or > ClassSectionExcelImport.MaxBytes) throw new ArgumentException("File Excel phải có dữ liệu và không vượt quá 10 MB.");
        using var stream = new MemoryStream(); await file.CopyToAsync(stream, ct);
        return stream.ToArray();
    }
}
