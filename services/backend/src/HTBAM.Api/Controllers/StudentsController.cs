using System.Data;
using System.Net.Mail;
using System.Text;
using HTBAM.Api.Support;
using HTBAM.Application.DTOs;
using HTBAM.Application.Interfaces;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Controllers;

[ApiController, Authorize, Route("api/students")]
public sealed class StudentsController(AppDbContext db, IAuditService audit) : ControllerBase
{
    private static readonly string[] CsvHeaders = ["StudentCode", "FullName", "Email", "StudentClassCode", "AnonymousCode"];

    [HttpGet]
    public async Task<ActionResult> List([FromQuery] string? q, [FromQuery] long? studentClassId, [FromQuery] bool includeInactive = false, [FromQuery] string? status = null, CancellationToken ct = default)
    {
        if (!await AccessScope.CanManageGlobalStudents(db, User, ct)) return Forbid();
        var teacherId = await AccessScope.TeacherIdAsync(db, User, ct);
        var permittedSessions = AccessScope.Sessions(db, User, teacherId).Select(s => s.Id);
        var permittedStudents = db.SessionStudentsSet.Where(x => permittedSessions.Contains(x.SessionId)).Select(x => x.StudentId)
            .Concat(db.Enrollments.Where(x => db.ClassSections.Any(c => c.Id == x.ClassSectionId && c.TeacherId == teacherId)).Select(x => x.StudentId));
        var managedFacultyIds = db.ManagementAssignmentsSet.Where(a =>
            a.TeacherId == teacherId && a.IsActive && a.PositionType == "FACULTY_HEAD" && a.FacultyId != null)
            .Select(a => a.FacultyId!.Value);
        var managedFacultyStudents = db.StudentsSet.Where(student =>
            student.StudentClassId != null &&
            db.StudentClassesSet.Any(studentClass =>
                studentClass.Id == student.StudentClassId && managedFacultyIds.Contains(studentClass.FacultyId)))
            .Select(student => student.Id);
        var manageableStudents = db.Enrollments.Where(x =>
            x.ClassSection.TeacherId == teacherId ||
            db.ManagementAssignmentsSet.Any(a =>
                a.TeacherId == teacherId &&
                a.IsActive &&
                ((a.PositionType == "DEPARTMENT_HEAD" && a.DepartmentId == x.ClassSection.Course.DepartmentId) ||
                 (a.PositionType == "FACULTY_HEAD" && a.FacultyId != null && db.DepartmentsSet.Any(d => d.Id == x.ClassSection.Course.DepartmentId && d.FacultyId == a.FacultyId)))))
            .Select(x => x.StudentId)
            .Concat(managedFacultyStudents);
        var visibleStudents = permittedStudents.Concat(managedFacultyStudents);
        var query = db.StudentsSet.AsNoTracking().Include(s => s.StudentClass).Where(s => visibleStudents.Contains(s.Id));
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = status.Trim().ToLowerInvariant() switch
            {
                "active" => query.Where(s => s.IsActive),
                "inactive" => query.Where(s => !s.IsActive),
                _ => query
            };
        }
        else if (!includeInactive) query = query.Where(s => s.IsActive);
        if (studentClassId is not null) query = query.Where(s => s.StudentClassId == studentClassId);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var keyword = q.Trim();
            query = query.Where(s => s.StudentCode.Contains(keyword) || s.FullName.Contains(keyword) || s.Email.Contains(keyword) || s.AnonymousCode.Contains(keyword));
        }
        return Ok(await query.OrderBy(s => s.StudentCode).Select(s => new
        {
            s.Id, s.StudentCode, s.FullName, s.Email, s.StudentClassId,
            StudentClass = s.StudentClass != null ? s.StudentClass.Code : null,
            s.AnonymousCode, s.SourceAnonymousCode, s.IsActive, s.CreatedAt,
            CanManage = manageableStudents.Contains(s.Id)
        }).ToListAsync(ct));
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult> Get(long id, CancellationToken ct)
    {
        if (!await AccessScope.CanManageGlobalStudents(db, User, ct)) return Forbid();
        if (!await AccessScope.CanViewStudentAsync(db, User, id, ct)) return Forbid();
        var student = await db.StudentsSet.AsNoTracking().Include(x => x.StudentClass).FirstOrDefaultAsync(x => x.Id == id, ct);
        return student is null
            ? NotFound()
            : Ok(new { student.Id, student.StudentCode, student.FullName, student.Email, student.StudentClassId, StudentClass = student.StudentClass?.Code, student.AnonymousCode, student.SourceAnonymousCode, student.IsActive, student.CreatedAt });
    }

    [HttpPost, Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> Create(UpsertStudentRequest request, CancellationToken ct)
    {
        if (request.StudentClassId is null || !await AccessScope.CanManageStudentClassAsync(db, User, request.StudentClassId.Value, ct)) return Forbid();
        request = request with { AnonymousCode = InternalCode.New("SV") };
        var error = await Validate(request, null, ct);
        if (error is not null) return error;
        var student = new Student
        {
            StudentCode = request.StudentCode.Trim(), FullName = request.FullName.Trim(), Email = request.Email.Trim(),
            StudentClassId = request.StudentClassId, AnonymousCode = (request.AnonymousCode ?? "").Trim(), IsActive = request.IsActive
        };
        db.StudentsSet.Add(student);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "STUDENT_CREATE", "Student", student.Id.ToString(), new { student.StudentCode }, ct);
        return CreatedAtAction(nameof(Get), new { id = student.Id }, student);
    }

    [HttpPut("{id:long}"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> Update(long id, UpsertStudentRequest request, CancellationToken ct)
    {
        var student = await db.StudentsSet.FindAsync([id], ct);
        if (student is null) return NotFound();
        if (!await AccessScope.CanManageStudentAsync(db, User, id, ct)) return Forbid();
        request = request with { AnonymousCode = student.AnonymousCode };
        if (request.StudentClassId != student.StudentClassId && request.StudentClassId is not null && !await AccessScope.CanManageStudentClassAsync(db, User, request.StudentClassId.Value, ct)) return Forbid();
        var error = await Validate(request, id, ct);
        if (error is not null) return error;
        student.StudentCode = request.StudentCode.Trim();
        student.FullName = request.FullName.Trim();
        student.Email = request.Email.Trim();
        student.StudentClassId = request.StudentClassId;
        student.AnonymousCode = (request.AnonymousCode ?? "").Trim();
        student.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "STUDENT_UPDATE", "Student", id.ToString(), new { student.StudentCode, student.IsActive }, ct);
        return NoContent();
    }

    [HttpDelete("{id:long}"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> Deactivate(long id, CancellationToken ct)
    {
        var student = await db.StudentsSet.FindAsync([id], ct);
        if (student is null) return NotFound();
        if (!await AccessScope.CanManageStudentAsync(db, User, id, ct)) return Forbid();
        student.IsActive = false;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "STUDENT_DEACTIVATE", "Student", id.ToString(), new { student.StudentCode }, ct);
        return NoContent();
    }

    [HttpPost("{id:long}/reactivate"), Authorize(Roles = "LECTURER")]
    public async Task<ActionResult> Reactivate(long id, CancellationToken ct)
    {
        var student = await db.StudentsSet.FindAsync([id], ct);
        if (student is null) return NotFound();
        if (!await AccessScope.CanManageStudentAsync(db, User, id, ct)) return Forbid();
        student.IsActive = true;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(UserContext.Id(User), "STUDENT_REACTIVATE", "Student", id.ToString(), new { student.StudentCode }, ct);
        return NoContent();
    }

    [HttpPost("import-csv"), Authorize(Roles = "LECTURER"), RequestSizeLimit(10_000_000)]
    public async Task<ActionResult> ImportCsv(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { message = "Thiếu file CSV." });

        var parsedRows = new List<(int Line, string[] Values)>();
        var parseErrors = new List<CsvImportError>();
        try
        {
            using var reader = new StreamReader(file.OpenReadStream(), new UTF8Encoding(false, true), true);
            var lineNumber = 0;
            char delimiter = ',';
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (parsedRows.Count == 0) delimiter = line.Contains(';') && !line.Contains(',') ? ';' : ',';
                if (!TryParseCsvLine(line, delimiter, out var values, out var parseError))
                {
                    parseErrors.Add(new CsvImportError(lineNumber, null, parseError!, false));
                    continue;
                }
                parsedRows.Add((lineNumber, values));
            }
        }
        catch (DecoderFallbackException)
        {
            return BadRequest(new { message = "CSV phải sử dụng mã hóa UTF-8 hoặc UTF-8 BOM.", errors = new[] { new { line = 1, field = (string?)null, message = "Không thể đọc ký tự theo UTF-8." } } });
        }

        if (parsedRows.Count == 0)
            return BadRequest(new { message = "CSV rỗng hoặc không có dòng hợp lệ.", errors = parseErrors });

        var headers = parsedRows[0].Values.Select(x => x.Trim().TrimStart('\uFEFF')).ToArray();
        var headerMap = headers.Select((value, index) => (value, index)).GroupBy(x => x.value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().index, StringComparer.OrdinalIgnoreCase);
        var missingHeaders = CsvHeaders.Where(x => !headerMap.ContainsKey(x)).ToArray();
        if (missingHeaders.Length > 0)
        {
            parseErrors.Add(new CsvImportError(parsedRows[0].Line, null, $"Thiếu cột: {string.Join(", ", missingHeaders)}.", false));
            return BadRequest(new { message = "Header CSV không hợp lệ.", errors = parseErrors });
        }

        var errors = new List<CsvImportError>(parseErrors);
        var candidates = new List<CsvCandidate>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAnonymousCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in parsedRows.Skip(1))
        {
            string Value(string header) => headerMap[header] < row.Values.Length ? row.Values[headerMap[header]].Trim() : "";
            var code = Value("StudentCode");
            var fullName = Value("FullName");
            var email = Value("Email");
            var classCode = Value("StudentClassCode");
            var anonymousCode = Value("AnonymousCode");
            var before = errors.Count;

            RequiredAndLength(errors, row.Line, "StudentCode", code, 64, "MSSV");
            RequiredAndLength(errors, row.Line, "FullName", fullName, 256, "Họ tên");
            RequiredAndLength(errors, row.Line, "StudentClassCode", classCode, 64, "Mã lớp");
            if (anonymousCode.Length > 128) errors.Add(new CsvImportError(row.Line, "AnonymousCode", "Mã nguồn ẩn danh không được vượt quá 128 ký tự.", false));
            if (email.Length > 256) errors.Add(new CsvImportError(row.Line, "Email", "Email không được vượt quá 256 ký tự.", false));
            else if (email.Length > 0 && !MailAddress.TryCreate(email, out _)) errors.Add(new CsvImportError(row.Line, "Email", $"Email '{email}' không hợp lệ.", false));
            if (code.Length > 0 && !seenCodes.Add(code)) errors.Add(new CsvImportError(row.Line, "StudentCode", $"MSSV {code} bị trùng trong file CSV.", true));
            if (email.Length > 0 && !seenEmails.Add(email)) errors.Add(new CsvImportError(row.Line, "Email", $"Email {email} bị trùng trong file CSV.", true));
            if (anonymousCode.Length > 0 && !seenAnonymousCodes.Add(anonymousCode)) errors.Add(new CsvImportError(row.Line, "AnonymousCode", $"Mã ẩn danh {anonymousCode} bị trùng trong file CSV.", true));
            if (errors.Count == before) candidates.Add(new CsvCandidate(row.Line, code, fullName, email, classCode, anonymousCode));
        }

        if (parsedRows.Count == 1)
            errors.Add(new CsvImportError(parsedRows[0].Line, null, "CSV không có dòng dữ liệu sinh viên.", false));
        if (errors.Count > 0) return CsvErrorResult(errors);

        ActionResult? finalError = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
                : null;

            var classCodes = candidates.Select(x => x.StudentClassCode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var classes = await db.StudentClassesSet.AsNoTracking().Where(x => classCodes.Contains(x.Code) && x.IsActive).ToListAsync(ct);
        var classByCode = classes.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var studentClass in classes)
            if (!await AccessScope.CanManageStudentClassAsync(db, User, studentClass.Id, ct))
            {
                if (transaction is not null) await transaction.RollbackAsync(ct);
                finalError = Forbid();
                return;
            }
        var studentCodes = candidates.Select(x => ApiValidation.Key(x.StudentCode)).ToArray();
        var emails = candidates.Where(x => x.Email.Length > 0).Select(x => ApiValidation.Key(x.Email)).ToArray();
        var anonymousCodes = candidates.Where(x => x.AnonymousCode.Length > 0).Select(x => ApiValidation.Key(x.AnonymousCode)).ToArray();
        var existing = await db.StudentsSet.AsNoTracking().Where(x =>
            studentCodes.Contains(x.StudentCode.ToUpper()) ||
            anonymousCodes.Contains(x.AnonymousCode.ToUpper()) || (x.SourceAnonymousCode != null && anonymousCodes.Contains(x.SourceAnonymousCode.ToUpper())) ||
            (x.Email != "" && emails.Contains(x.Email.ToUpper()))).ToListAsync(ct);
        var existingCodes = existing.Select(x => x.StudentCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingEmails = existing.Where(x => x.Email.Length > 0).Select(x => x.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingAnonymousCodes = existing.SelectMany(x => new[] { x.AnonymousCode, x.SourceAnonymousCode ?? "" }).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (!classByCode.ContainsKey(candidate.StudentClassCode))
                errors.Add(new CsvImportError(candidate.Line, "StudentClassCode", $"Lớp {candidate.StudentClassCode} không tồn tại hoặc đã ngừng hoạt động.", false));
            if (existingCodes.Contains(candidate.StudentCode))
                errors.Add(new CsvImportError(candidate.Line, "StudentCode", $"MSSV {candidate.StudentCode} đã tồn tại.", true));
            if (candidate.Email.Length > 0 && existingEmails.Contains(candidate.Email))
                errors.Add(new CsvImportError(candidate.Line, "Email", $"Email {candidate.Email} đã tồn tại.", true));
            if (candidate.AnonymousCode.Length > 0 && existingAnonymousCodes.Contains(candidate.AnonymousCode))
                errors.Add(new CsvImportError(candidate.Line, "AnonymousCode", $"Mã ẩn danh {candidate.AnonymousCode} đã tồn tại.", true));
        }

            if (errors.Count > 0)
            {
                if (transaction is not null) await transaction.RollbackAsync(ct);
                finalError = CsvErrorResult(errors);
                return;
            }

            var createdAt = DateTime.UtcNow;
        foreach (var candidate in candidates)
        {
            db.StudentsSet.Add(new Student
            {
                StudentCode = candidate.StudentCode, FullName = candidate.FullName, Email = candidate.Email,
                StudentClassId = classByCode[candidate.StudentClassCode].Id, AnonymousCode = InternalCode.New("SV"), SourceAnonymousCode = string.IsNullOrEmpty(candidate.AnonymousCode) ? null : candidate.AnonymousCode,
                IsActive = true, CreatedAt = createdAt
            });
        }
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        });

        if (finalError is not null) return finalError;
        await audit.WriteAsync(UserContext.Id(User), "STUDENT_IMPORT_CSV", "Student", "bulk", new { inserted = candidates.Count, errorCount = 0 }, ct);
        return Ok(new { inserted = candidates.Count, errors = Array.Empty<CsvImportError>(), message = $"Đã import {candidates.Count} sinh viên" });
    }

    private ActionResult CsvErrorResult(List<CsvImportError> errors)
    {
        var body = new { message = "CSV có dữ liệu chưa hợp lệ; chưa sinh viên nào được import.", errors = errors.Select(x => new { line = x.Line, field = x.Field, message = x.Message }).ToArray() };
        return errors.All(x => x.IsConflict) ? Conflict(body) : BadRequest(body);
    }

    private static void RequiredAndLength(List<CsvImportError> errors, int line, string field, string value, int maxLength, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add(new CsvImportError(line, field, $"Thiếu {label}.", false));
        else if (value.Length > maxLength) errors.Add(new CsvImportError(line, field, $"{label} không được vượt quá {maxLength} ký tự.", false));
    }

    private async Task<ActionResult?> Validate(UpsertStudentRequest request, long? id, CancellationToken ct)
    {
        var code = request.StudentCode.Trim();
        var anonymousCode = (request.AnonymousCode ?? "").Trim();
        var email = request.Email.Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(request.FullName) || string.IsNullOrWhiteSpace(anonymousCode)) return BadRequest("Thiếu MSSV/họ tên/mã ẩn danh.");
        if (email.Length > 0 && !MailAddress.TryCreate(email, out _)) return BadRequest("Email không hợp lệ.");
        if (request.StudentClassId is not null && !await db.StudentClassesSet.AnyAsync(x => x.Id == request.StudentClassId && x.IsActive, ct)) return BadRequest("Lớp sinh hoạt không hợp lệ.");

        var codeKey = ApiValidation.Key(code);
        var anonymousCodeKey = ApiValidation.Key(anonymousCode);
        var emailKey = ApiValidation.Key(email);
        var conflicts = new List<ApiFieldError>();

        if (await db.StudentsSet.AsNoTracking().AnyAsync(
                x => x.Id != id && x.StudentCode.ToUpper() == codeKey,
                ct))
            conflicts.Add(new ApiFieldError("studentCode", $"MSSV {code} đã tồn tại."));

        if (email.Length > 0 && await db.StudentsSet.AsNoTracking().AnyAsync(
                x => x.Id != id && x.Email != "" && x.Email.ToUpper() == emailKey,
                ct))
            conflicts.Add(new ApiFieldError("email", $"Email {email} đã được sử dụng."));

        if (await db.StudentsSet.AsNoTracking().AnyAsync(
                x => x.Id != id && x.AnonymousCode.ToUpper() == anonymousCodeKey,
                ct))
            conflicts.Add(new ApiFieldError("anonymousCode", $"Mã ẩn danh '{anonymousCode}' đã tồn tại."));

        if (conflicts.Count > 1) return Conflict(ApiValidation.DuplicateFields(conflicts));
        if (conflicts.Count == 1)
        {
            var conflict = conflicts[0];
            var errorCode = conflict.Field switch
            {
                "studentCode" => "STUDENT_CODE_DUPLICATE",
                "email" => "STUDENT_EMAIL_DUPLICATE",
                _ => "STUDENT_ANONYMOUS_CODE_DUPLICATE"
            };
            return Conflict(ApiValidation.Duplicate(errorCode, conflict.Field, conflict.Message));
        }
        return null;
    }

    private static bool TryParseCsvLine(string line, char delimiter, out string[] values, out string? error)
    {
        var cells = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (character == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (character == delimiter && !quoted) { cells.Add(value.ToString()); value.Clear(); }
            else value.Append(character);
        }
        if (quoted) { values = []; error = "Dấu ngoặc kép trong CSV chưa được đóng."; return false; }
        cells.Add(value.ToString());
        values = cells.ToArray();
        error = null;
        return true;
    }

    private sealed record CsvCandidate(int Line, string StudentCode, string FullName, string Email, string StudentClassCode, string AnonymousCode);
    private sealed record CsvImportError(int Line, string? Field, string Message, bool IsConflict);
}
