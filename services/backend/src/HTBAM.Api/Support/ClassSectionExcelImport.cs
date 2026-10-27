using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HTBAM.Application.DTOs;
using HTBAM.Application.Services;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using static HTBAM.Api.Support.ClassSectionWorkbook;

namespace HTBAM.Api.Support;

public sealed class ClassSectionExcelImport(AppDbContext db)
{
    public const int MaxBytes = ClassSectionWorkbook.MaxBytes;
    public static readonly string[] RequiredColumns = ["Mã LHP", "Mã môn", "Mã GV", "Loại lịch", "Thứ", "Tiết bắt đầu", "Tiết kết thúc", "Phòng", "Từ ngày", "Đến ngày"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static List<SheetData> Read(byte[] bytes, string fileName, string mime) => ClassSectionWorkbook.Read(bytes, fileName, mime);
    public static string Normalize(string value) => ClassSectionWorkbook.Normalize(value);
    public async Task<bool> Allowed(ClaimsPrincipal user, CancellationToken ct)
    {
        var teacher = await AccessScope.TeacherIdAsync(db, user, ct);
        return teacher > 0 && await AccessScope.ActiveAssignments(db).AnyAsync(x => x.TeacherId == teacher && (x.PositionType == "FACULTY_HEAD" || x.PositionType == "DEPARTMENT_HEAD"), ct);
    }

    public async Task<ImportSnapshot> Preview(byte[] bytes, string fileName, string mime, string sheetName, ImportOptions options, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await Allowed(user, ct)) throw new UnauthorizedAccessException();
        var sheet = Read(bytes, fileName, mime).SingleOrDefault(x => x.Name == sheetName) ?? throw new ArgumentException("Sheet đã chọn không tồn tại.");
        var inspection = Inspect(sheet, options.HeaderRow, options.HeaderRowCount, options.ColumnMappings);
        if (inspection.MissingColumns.Length > 0) throw new ArgumentException("Thiếu cột bắt buộc: " + string.Join(", ", inspection.MissingColumns) + ". Chọn đúng dòng tiêu đề hoặc sửa ánh xạ cột.");
        var columns = inspection.Mappings.Where(x => x.ColumnIndex is not null).ToDictionary(x => x.Field, x => x.ColumnIndex!.Value);
        var term = await db.AcademicTerms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == options.AcademicTermId, ct) ?? throw new ArgumentException("Chọn học kỳ đã cấu hình.");
        var context = await ImportPlanningContext.Load(db, term, options.AttendancePolicyId, user, ct);
        var snapshot = new ImportSnapshot { Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            AcademicTermId = term.Id, Sheet = sheetName, FileHash = Convert.ToHexString(SHA256.HashData(bytes)), IsDevelopment = term.IsDevelopment,
            WorkbookRows = sheet.Rows.Count, WorkbookColumns = sheet.ColumnCount, Format = inspection.Format, HeaderRow = inspection.HeaderRow, HeaderRowCount = inspection.HeaderRowCount,
            WeekOneMonday = term.WeekMappingConfirmed || term.IsDevelopment ? term.WeekOneMonday : null };
        snapshot.WeekMessage = snapshot.WeekOneMonday is DateOnly monday ? $"T1 được ánh xạ tới ngày {monday:dd/MM/yyyy}" : "Chưa cấu hình Mốc bắt đầu Tuần 1 cho học kỳ này";
        var courseCodes = context.Courses.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        var courseNames = context.Courses.ToLookup(x => Normalize(x.Name));
        var teacherCodes = context.Teachers.ToDictionary(x => x.TeacherCode, StringComparer.OrdinalIgnoreCase);
        var teacherNames = context.Teachers.ToLookup(x => Normalize(x.FullName));
        var roomCodes = context.Rooms.Values.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        var roomNames = context.Rooms.Values.ToLookup(x => Normalize(x.Name));
        var selections = new Dictionary<string, long>(options.CourseSelections ?? [], StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, ImportGroup>(StringComparer.OrdinalIgnoreCase);
        var requests = new Dictionary<string, List<ClassSectionRequest>>(StringComparer.OrdinalIgnoreCase);
        for (var i = inspection.HeaderRow + inspection.HeaderRowCount - 1; i < sheet.Rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var cells = sheet.Rows[i];
            object? Cell(string field) => columns.TryGetValue(field, out var index) ? cells.ElementAtOrDefault(index) : null;
            string Value(string field) => Text(Cell(field));
            if (cells.All(x => Text(x).Length == 0) || inspection.Format == "HUIT" && !int.TryParse(Value("ordinal"), out _) && new[] { "day", "startPeriod", "endPeriod", "activity" }.All(x => Value(x).Length == 0)) { snapshot.BlankRows++; continue; }
            snapshot.DataRows++;
            var source = Value("source"); var key = source.Length > 0 ? source : "missing-row-" + (i + 1);
            var row = new ImportRow { Row = i + 1, SourceClassSectionCode = source, SourceCourseName = Value("courseName"), SourceTeacher = Value("teacher"), SourceRoom = Value("room"),
                Course = Value("courseCode"), CourseName = Value("courseName"), Teacher = Value("teacherCode"), Room = Value("room"), ClassName = Value("name") };
            if (!groups.TryGetValue(key, out var group)) { groups[key] = group = new() { Key = key, SourceClassSectionCode = source }; requests[key] = []; snapshot.Groups.Add(group); }
            group.Rows.Add(row);
            void Error(string column, string message) { row.Issues.Add(new(row.Row, source, column, message)); row.Column ??= column; }
            T Check<T>(string column, Func<T> parse, T fallback) { try { return parse(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException) { Error(column, ex.Message); return fallback; } }
            if (source.Length is < 1 or > 128) Error("Mã LHP", "Mã LHP cần 1–128 ký tự; không tự điền từ dòng trước.");
            var day = Check("Thứ", () => Day(Value("day"), inspection.Format == "HUIT"), 0);
            var start = Check("Tiết bắt đầu", () => Integer(Value("startPeriod")), 0);
            var end = Check("Tiết kết thúc", () => Integer(Value("endPeriod")), 0);
            var from = Check("Từ ngày", () => Date(Cell("from")), DateOnly.MinValue);
            var to = Check("Đến ngày", () => Date(Cell("to")), DateOnly.MinValue);
            var activity = Value("activity").ToUpperInvariant(); if (activity is not ("LT" or "TH")) Error("Loại lịch", "Loại lịch phải là LT hoặc TH.");
            var weekColumns = columns.Where(x => x.Key.StartsWith("week") && x.Key != "weeks").OrderBy(x => int.Parse(x.Key[4..])).ToArray();
            var weeks = new List<int>();
            if (weekColumns.Length > 0)
            {
                foreach (var pair in weekColumns)
                {
                    var mark = Text(cells.ElementAtOrDefault(pair.Value)).ToLowerInvariant();
                    if (IsSelectedWeekMark(mark)) weeks.Add(int.Parse(pair.Key[4..]));
                    else if (mark is not ("" or "0" or "false")) Error("T" + pair.Key[4..], "Chỉ nhận x, X, 1, true, dấu ✓/✔/☑/√ hoặc ô trống/false/0.");
                }
                if (weeks.Count == 0) Error("Các tuần có lịch", "Không có tuần được đánh dấu trong bảng tuần; không suy diễn thành học tất cả tuần.");
                if (snapshot.WeekOneMonday is null) Error("Mốc bắt đầu Tuần 1", snapshot.WeekMessage + ". Hãy mở Cấu hình học vụ.");
            }
            else weeks.AddRange(Check("Các tuần có lịch", () => ParseWeeks(Value("weeks")), []));
            if (weeks.Count > 0 && snapshot.WeekOneMonday is null && !row.Issues.Any(x => x.Column == "Mốc bắt đầu Tuần 1")) Error("Mốc bắt đầu Tuần 1", snapshot.WeekMessage + ". Hãy mở Cấu hình học vụ.");
            row.Weeks = weeks.Distinct().Order().Select(w => new ImportWeek(w, snapshot.WeekOneMonday?.AddDays((w - 1) * 7), snapshot.WeekOneMonday?.AddDays((w - 1) * 7 + 6))).ToArray();
            if (columns.ContainsKey("credits")) row.Credits = Check<int?>("Số TC", () => Integer(Value("credits")), null);
            if (Value("sourcePeriods").Length > 0) row.SourceSchedulePeriods = Check<int?>("Số tiết", () => Integer(Value("sourcePeriods")), null);
            if (Value("sourceTotal").Length > 0) row.SourceTotalPeriods = Check<int?>("Tổng số tiết", () => Integer(Value("sourceTotal")), null);
            Course? course = null;
            if (selections.TryGetValue(source, out var chosen))
            {
                course = context.Courses.SingleOrDefault(x => x.Id == chosen);
                if (course is null) Error("Môn học", "Môn đã chọn không hoạt động hoặc ngoài phạm vi quản lý.");
                else row.Warnings.Add("Môn học được chọn thủ công khi xem trước: " + course.Code + " · " + course.Name);
            }
            else if (row.Course.Length > 0) courseCodes.TryGetValue(row.Course, out course);
            else
            {
                var matches = courseNames[Normalize(row.CourseName ?? "")].ToArray();
                if (matches.Length == 1) course = matches[0];
                else if (matches.Length > 1) Error("Tên môn học", "Có nhiều môn trùng tên; hãy chọn môn tương ứng cho nhóm này trong preview.");
            }
            if (course is null && !row.Issues.Any(x => x.Column is "Tên môn học" or "Môn học")) Error("Môn học", "Không tìm thấy môn đang hoạt động trong phạm vi quản lý. Hãy chọn môn ở preview; hệ thống không đoán mã môn từ Mã LHP và không tạo môn mới.");
            if (course is not null)
            {
                row.CourseId = course.Id; row.Course = course.Code; row.CourseName = course.Name;
                Check("Mã môn", () => ClassSectionCodes.Prefix(term.AcademicYear, term.Semester, course.Code), "");
                if (row.Credits is int credits && credits != course.Credits) Error("Số TC", $"Nguồn ghi {credits} tín chỉ nhưng môn {course.Code} có {course.Credits}; cần đối chiếu, không tự thay danh mục.");
            }
            Teacher? teacher = null;
            if (options.TeacherSelections?.TryGetValue(row.Row, out var selectedTeacher) == true) teacher = context.Teachers.SingleOrDefault(x => x.Id == selectedTeacher);
            else
            {
                var raw = Value("teacherCode"); var full = Value("teacher");
                if (raw.Length == 0)
                {
                    if (teacherCodes.TryGetValue(full, out var direct)) teacher = direct;
                    else
                    {
                        var split = Regex.Match(full, @"^(.+?)\s+[-–]\s+(.+)$");
                        if (!split.Success) split = Regex.Match(full, @"^(\d+|[A-Za-z0-9]+)\s*[-–]\s*(.+)$");
                        if (split.Success) { raw = split.Groups[1].Value; row.TeacherName = split.Groups[2].Value.Trim(); }
                        else { var matches = teacherNames[Normalize(full)].ToArray(); if (matches.Length == 1) teacher = matches[0]; }
                    }
                }
                if (raw.Length > 0) { row.Teacher = raw; teacherCodes.TryGetValue(raw, out teacher); }
            }
            if (teacher is null) Error("Giảng viên", "Không tìm thấy giảng viên đang hoạt động từ mã/tên nguồn. Hãy đối chiếu danh mục hoặc chọn giảng viên cho dòng này.");
            else { row.TeacherId = teacher.Id; row.Teacher = teacher.TeacherCode; row.TeacherName = teacher.FullName; if (course is not null && teacher.DepartmentId != course.DepartmentId) Error("Giảng viên", "Giảng viên không thuộc bộ môn phụ trách môn học."); }
            Room? room = null;
            if (options.RoomSelections?.TryGetValue(row.Row, out var selectedRoom) == true) context.Rooms.TryGetValue(selectedRoom, out room);
            else
            {
                if (!roomCodes.TryGetValue(row.Room, out room))
                {
                    var code = Regex.Split(row.Room, @"\s+[-–]\s+")[0].Trim();
                    if (!roomCodes.TryGetValue(code, out room)) { var matches = roomNames[Normalize(row.Room)].Concat(roomNames[Normalize(code)]).DistinctBy(x => x.Id).ToArray(); if (matches.Length == 1) room = matches[0]; }
                }
            }
            if (room is null) Error("Phòng", "Không tìm thấy phòng đang hoạt động; không tự tạo phòng mới.");
            else { row.RoomId = room.Id; row.Room = room.Code; row.RoomName = room.Name; }
            var bellGroup = room is null || activity is not ("LT" or "TH") ? Value("bellGroup") : Check("Nhóm giờ", () => AcademicRules.BellGroup(room.RoomType, activity, Value("bellGroup")), "");
            var line = new ScheduleLineRequest(Guid.NewGuid().ToString("N"), day, start, end, bellGroup, room?.Id ?? 0, activity, from, to, weeks.Distinct().Order().ToArray()); row.Schedule = line;
            var version = context.Versions.Where(x => x.Group == bellGroup && x.Confirmed && x.EffectiveFrom <= from && (x.EffectiveTo == null || x.EffectiveTo >= from)).OrderByDescending(x => x.EffectiveFrom).FirstOrDefault();
            if (version is not null)
            {
                var window = Check("Tiết bắt đầu / Tiết kết thúc", () => AcademicRules.PeriodWindow(context.Periods.Where(x => x.BellTimeVersionId == version.Id), start, end), (TimeOnly.MinValue, TimeOnly.MinValue));
                if (window.Item2 > window.Item1) { row.StartsAt = window.Item1.ToString("HH:mm"); row.EndsAt = window.Item2.ToString("HH:mm"); }
            }
            int? phase = Value("phase").Length == 0 ? options.Phase : Check<int?>("Giai đoạn", () => Integer(Value("phase")), null);
            if (course?.Credits == 3) phase = null;
            if (weeks.Count == 0 && weekColumns.Length == 0) row.Warnings.Add("Học tất cả tuần trong khoảng ngày; không suy ra mốc T1.");
            if (row.Issues.Count > 0) { row.Status = "ERROR"; row.Message = string.Join(" ", row.Issues.Select(x => x.Column + ": " + x.Message)); }
            else
            {
                row.Status = row.Warnings.Count > 0 ? "WARNING" : "VALID"; row.Message = string.Join(" ", row.Warnings);
                var name = row.ClassName; if (string.IsNullOrWhiteSpace(name)) name = course!.Name + " · " + source;
                requests[key].Add(new(name, course!.Id, teacher!.Id, term.Id, phase, options.AttendancePolicyId, "Nhập từ Excel", source, [line], CreationKey: Guid.NewGuid().ToString("N")));
            }
        }
        if (snapshot.DataRows == 0) throw new ArgumentException("Sheet không có dòng dữ liệu lịch sau tiêu đề đã chọn.");
        foreach (var group in snapshot.Groups)
        {
            if (group.Rows.Any(x => x.Status == "ERROR")) { FailGroup(group, "Nhóm lớp có dòng lỗi; chưa thể xác nhận nhập."); continue; }
            try
            {
                var entries = requests[group.Key]; var first = entries[0];
                if (entries.Any(x => x.CourseId != first.CourseId || x.TeacherId != first.TeacherId || x.Phase != first.Phase || x.Name != first.Name)) throw new ArgumentException("Các dòng cùng Mã LHP phải thống nhất môn, giảng viên, tên lớp và giai đoạn.");
                var unique = new HashSet<string>(); var lines = new List<ScheduleLineRequest>();
                foreach (var row in group.Rows)
                    if (!unique.Add(LineHash(row.Schedule!))) { row.Status = "DUPLICATE"; row.Message = "Dòng lịch trùng trong sheet, được bỏ qua."; } else lines.Add(row.Schedule!);
                var request = first with { Schedules = lines.ToArray() }; group.Request = request;
                if (context.Existing.TryGetValue(group.SourceClassSectionCode, out var existing))
                {
                    var oldLines = context.ExistingLines.GetValueOrDefault(existing.Id) ?? [];
                    if (existing.CourseId != request.CourseId || existing.TeacherId != request.TeacherId || existing.Phase != request.Phase || existing.Name != request.Name || existing.AttendancePolicyId != request.AttendancePolicyId || !oldLines.Select(LineHash).Order().SequenceEqual(lines.Select(LineHash).Order())) throw new ArgumentException("Mã lớp nguồn đã tồn tại với nội dung khác. Import không ghi đè lớp cũ; hãy dùng chức năng sửa và xem ảnh hưởng.");
                    group.Status = "DUPLICATE"; foreach (var row in group.Rows) { row.Status = "DUPLICATE"; row.Message = "Lớp và lịch đã tồn tại, không ghi thêm."; } continue;
                }
                var plan = context.Plan(request); group.Request = request with { PreviewToken = plan.Token };
                group.Status = group.Rows.Any(x => x.Status is "WARNING" or "DUPLICATE") ? "WARNING" : "VALID";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { FailGroup(group, ex.Message); }
        }
        ImportPreviewStore.Put(UserContext.Id(user)!.Value, snapshot);
        return snapshot;
    }

    public static ImportPreviewPage Page(ImportSnapshot snapshot, int page = 1, int pageSize = 100, string? query = null, string? status = null)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200); var key = (query ?? "").Trim();
        var matching = snapshot.Groups.Where(g => (string.IsNullOrEmpty(status) || status == "ALL" || g.Status == status || g.Rows.Any(r => r.Status == status)) &&
            (key.Length == 0 || g.SourceClassSectionCode.Contains(key, StringComparison.OrdinalIgnoreCase) || g.Rows.Any(r => new[] { r.Course, r.CourseName, r.Teacher, r.TeacherName, r.Room, r.Message }.Any(s => s?.Contains(key, StringComparison.OrdinalIgnoreCase) == true)))).ToArray();
        var total = matching.Sum(g => g.Rows.Count); var skip = (long)(page - 1) * pageSize; var remaining = pageSize; var result = new List<ImportGroupPage>();
        foreach (var group in matching)
        {
            if (skip >= group.Rows.Count) { skip -= group.Rows.Count; continue; }
            var rows = group.Rows.Skip((int)skip).Take(remaining).ToArray(); skip = 0; remaining -= rows.Length;
            result.Add(new(group.Key, group.SourceClassSectionCode, group.Status, group.Message, group.Rows.Count, rows)); if (remaining == 0) break;
        }
        return new(snapshot.Token, snapshot.ExpiresAt, snapshot.Sheet, snapshot.Format, snapshot.HeaderRow, snapshot.HeaderRowCount, snapshot.WorkbookRows, snapshot.WorkbookColumns, snapshot.DataRows, snapshot.Groups.Count,
            snapshot.WeekOneMonday, snapshot.IsDevelopment, snapshot.WeekMessage, snapshot.ClassesToCreate, snapshot.SchedulesToCreate, snapshot.SkippedRows, snapshot.ErrorRows, snapshot.WarningRows, snapshot.Duplicates,
            page, pageSize, total, matching.Length, result.ToArray(), snapshot.Groups.Sum(g => g.Rows.Count(r => r.CourseId is null)), snapshot.Groups.Sum(g => g.Rows.Count(r => r.TeacherId is null)), snapshot.Groups.Sum(g => g.Rows.Count(r => r.RoomId is null)));
    }

    public async Task<ImportPreviewPage> GetPage(string token, ClaimsPrincipal user, int page, int pageSize, string? query, string? status, CancellationToken ct)
    { if (!await Allowed(user, ct)) throw new UnauthorizedAccessException(); return Page(ImportPreviewStore.Get(UserContext.Id(user)!.Value, token), page, pageSize, query, status); }

    public async Task<ImportResult> Confirm(string token, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await Allowed(user, ct)) throw new UnauthorizedAccessException();
        var uid = UserContext.Id(user)!.Value;
        var saved = await db.ClassSectionImportPreviews.SingleOrDefaultAsync(x => x.Token == token && x.UserId == uid, ct);
        if (saved?.ResultJson is not null) return DeserializeStored<ImportResult>(saved.ResultJson);
        var snapshot = saved is null ? ImportPreviewStore.Get(uid, token) : DeserializeStored<ImportSnapshot>(saved.SnapshotJson);
        if (snapshot.ExpiresAt < DateTime.UtcNow || saved?.ExpiresAt < DateTime.UtcNow) throw new InvalidOperationException("Bản xem trước đã hết hạn. Hãy tải lại file.");
        if (snapshot.ErrorRows > 0) throw new InvalidOperationException($"Còn {snapshot.ErrorRows} dòng lỗi bắt buộc. Hãy sửa ánh xạ/danh mục/cấu hình và xem trước lại; chưa ghi dữ liệu.");
        if (saved is null) { saved = new() { Token = token, UserId = uid, ExpiresAt = snapshot.ExpiresAt, SnapshotJson = JsonSerializer.Serialize(snapshot, Json) }; db.ClassSectionImportPreviews.Add(saved); }
        var classes = new List<ImportedClass>(); var schedules = 0;
        foreach (var group in snapshot.Groups.Where(x => x.Status is "VALID" or "WARNING"))
        {
            var request = group.Request!;
            var department = await db.Courses.Where(x => x.Id == request.CourseId).Select(x => x.DepartmentId).SingleAsync(ct);
            if (department is null || !await AccessScope.CanManageDepartmentDataAsync(db, user, department.Value, ct)) throw new InvalidOperationException("Quyền quản lý đã thay đổi. Hãy xem trước lại.");
            if (await db.ClassSections.AnyAsync(x => x.AcademicTermId == request.AcademicTermId && x.SourceClassSectionCode == group.SourceClassSectionCode, ct)) throw new InvalidOperationException("Một lớp nguồn vừa được nhập ở thao tác khác. Hãy xem trước lại; chưa ghi dữ liệu.");
            var planner = new ClassSchedulePlanner(db); var plan = await planner.Prepare(request, null, ct);
            if (plan.PreviewToken != request.PreviewToken) throw new InvalidOperationException("Lịch hoặc cấu hình đã thay đổi sau khi xem trước. Hãy xem trước lại; chưa ghi dữ liệu.");
            var section = new ClassSection { Code = await ClassSectionNumbering.Next(db, plan.Term, request.CourseId, ct), CreationKey = request.CreationKey, CreationRequestHash = ClassSchedulePlanner.Hash(request with { PreviewToken = null }) };
            db.ClassSections.Add(section); await planner.Apply(section, request, plan, ct);
            foreach (var line in await db.ClassSchedules.Where(x => x.ClassSectionId == section.Id).ToListAsync(ct)) line.SourceDataJson = JsonSerializer.Serialize(new { snapshot.FileHash, snapshot.Sheet, snapshot.Format,
                Rows = group.Rows.Where(x => x.Schedule?.LineKey == line.LineKey).Select(x => new { x.Row, x.SourceCourseName, x.SourceTeacher, x.SourceRoom, x.Credits, x.ClassName, x.SourceSchedulePeriods, x.SourceTotalPeriods }) }, Json);
            classes.Add(new(section.Id, section.Code, group.SourceClassSectionCode)); schedules += request.Schedules.Length;
        }
        var result = new ImportResult(classes.Count, 0, schedules, snapshot.SkippedRows, snapshot.Duplicates, [], classes.ToArray());
        saved.ResultJson = JsonSerializer.Serialize(result, Json); await db.SaveChangesAsync(ct); return result;
    }
    public async Task<ImportResult> Result(string token, ClaimsPrincipal user, CancellationToken ct)
    { var uid = UserContext.Id(user); var json = await db.ClassSectionImportPreviews.AsNoTracking().Where(x => x.Token == token && x.UserId == uid).Select(x => x.ResultJson).SingleOrDefaultAsync(ct); return json is null ? throw new ArgumentException("Chưa có kết quả import của tài khoản này.") : DeserializeStored<ImportResult>(json); }

    private static void FailGroup(ImportGroup group, string message) { group.Status = "ERROR"; group.Message = message; foreach (var row in group.Rows) { row.Status = "ERROR"; if (string.IsNullOrEmpty(row.Message)) row.Message = message; } }
    private static T DeserializeStored<T>(string json) => JsonSerializer.Deserialize<T>(Regex.Replace(json, "\"[sS]ourceCode\"(?=\\s*:)", "\"sourceClassSectionCode\""), Json)!;
    private static int Integer(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException("Giá trị phải là số nguyên.");
    private static int Day(string value, bool huit) { var key = Normalize(value); return key is "cn" or "chunhat" || huit && key == "8" ? 7 : int.TryParse(key.Replace("thu", ""), out var n) && n is >= 2 and <= 7 ? n - 1 : throw new ArgumentException(huit ? "Thứ HUIT phải là 2–8 (8 là Chủ nhật) hoặc CN." : "Nhập 2–7 hoặc CN (thứ Hai đến Chủ nhật)."); }
    private static DateOnly Date(object? value) => value is DateTime dt ? DateOnly.FromDateTime(dt) : DateOnly.TryParseExact(Text(value), ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw new ArgumentException("Ngày phải có dạng dd/MM/yyyy hoặc ô ngày Excel.");
    public static int[] ParseWeeks(string value)
    { if (string.IsNullOrWhiteSpace(value)) return []; if (!Regex.IsMatch(value, @"^\s*[Tt]?\d+\s*(?:[,;]\s*[Tt]?\d+\s*)*$")) throw new ArgumentException("Các tuần có lịch: dùng T1, T3, T5 hoặc 1, 3, 5."); var weeks = Regex.Matches(value, @"\d+").Select(x => Integer(x.Value)).Distinct().Order().ToArray(); if (weeks.Any(x => x is < 1 or > 53)) throw new ArgumentException("Số tuần phải từ 1 đến 53 và nằm trong khoảng ngày học kỳ."); return weeks; }
    private static string LineHash(ScheduleLineRequest line) => ClassSchedulePlanner.Hash(line with { LineKey = "", WeekCalendarId = null });
    private static string LineHash(ClassSchedule line) => LineHash(new ScheduleLineRequest("", line.IsoDayOfWeek, line.StartPeriod, line.EndPeriod, line.BellGroup, line.RoomId, line.ActivityType, line.StartsOn, line.EndsOn, JsonSerializer.Deserialize<int[]>(line.SelectedWeeksJson)!));
}
