using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;
using HTBAM.Application.DTOs;

namespace HTBAM.Api.Support;

public static class ClassSectionWorkbook
{
    public const int MaxBytes = 10 * 1024 * 1024, MaxRows = 20000, MaxColumns = 64;
    private const int MaxHeaderScanRows = 20;
    public record SheetData(string Name, List<object?[]> Rows, int ColumnCount);
    public static readonly (string Field, string Label, bool Required)[] Fields = [
        ("source", "Mã LHP", true), ("courseCode", "Mã môn", false), ("courseName", "Tên môn học", false),
        ("credits", "Số TC", false), ("name", "Tên lớp / Lớp học", false), ("teacherCode", "Mã GV", false),
        ("teacher", "Giảng viên", false), ("activity", "Loại lịch", true), ("day", "Thứ", true),
        ("startPeriod", "Tiết bắt đầu", true), ("endPeriod", "Tiết kết thúc", true), ("room", "Phòng", true),
        ("from", "Từ ngày", true), ("to", "Đến ngày", true), ("weeks", "Các tuần có lịch", false),
        ("sourcePeriods", "Số tiết nguồn theo dòng", false), ("sourceTotal", "Tổng số tiết nguồn", false),
        ("phase", "Giai đoạn", false), ("bellGroup", "Nhóm giờ", false),
    ];

    public static List<SheetData> Read(byte[] bytes, string fileName, string mime)
    {
        if (bytes.Length is 0 or > MaxBytes) throw new ArgumentException($"File có {bytes.Length:N0} byte. Giới hạn dung lượng là 10 MB; file phải có dữ liệu.");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".xls" or ".xlsx") || mime is not ("application/vnd.ms-excel" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" or "application/octet-stream")) throw new ArgumentException("Chỉ nhận .xls/.xlsx với MIME Excel hợp lệ.");
        var binary = bytes.AsSpan().StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });
        var zip = bytes.AsSpan().StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        if (extension == ".xls" && !binary || extension == ".xlsx" && !zip) throw new ArgumentException("Nội dung file không khớp định dạng Excel đã chọn.");
        try
        {
            if (zip)
            {
                using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                if (archive.Entries.Count > 2000 || archive.Entries.Sum(x => x.Length) > 64 * 1024 * 1024 || archive.GetEntry("xl/workbook.xml") is null) throw new ArgumentException("Workbook không hợp lệ hoặc vượt giới hạn 64 MB sau giải nén.");
                if (archive.Entries.Any(x => x.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Không nhận workbook chứa macro.");
            }
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var stream = new MemoryStream(bytes);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var result = new List<SheetData>(); var total = 0;
            do
            {
                if (result.Count >= 30) throw new ArgumentException("Workbook có quá 30 sheet. Giới hạn là 30 sheet.");
                var rows = new List<object?[]>(); var lastNonempty = 0; var columns = reader.FieldCount;
                if (columns > MaxColumns || reader.RowCount > 100000) throw Limits(reader.RowCount, columns);
                while (reader.Read())
                {
                    var cells = new object[columns]; reader.GetValues(cells);
                    if (cells.Any(x => Text(x).Length > 0)) lastNonempty = rows.Count + 1;
                    if (rows.Count < MaxRows + 1) rows.Add(cells);
                    else throw Limits(reader.RowCount, columns);
                }
                if (lastNonempty < rows.Count) rows.RemoveRange(lastNonempty, rows.Count - lastNonempty);
                if (rows.Count > MaxRows || total + rows.Count > MaxRows) throw Limits(total + rows.Count, columns);
                total += rows.Count; result.Add(new(reader.Name, rows, columns));
            } while (reader.NextResult());
            return result;
        }
        catch (ArgumentException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { throw new ArgumentException("Không đọc được workbook. File có thể hỏng, có mật khẩu hoặc không đúng định dạng Excel."); }
    }

    private static ArgumentException Limits(int rows, int columns) => new($"File có {rows.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"))} dòng và {columns} cột. Giới hạn hiện tại là 20.000 dòng và 64 cột trong workbook.");

    public static ImportSheetInspection Inspect(SheetData sheet, int? headerRow = null, int? headerCount = null, Dictionary<string, int>? overrides = null)
    {
        if (headerRow is < 1 or > MaxHeaderScanRows || headerCount is < 1 or > 2) throw new ArgumentException("Chọn dòng tiêu đề từ 1 đến 20 và số dòng tiêu đề là 1 hoặc 2.");
        var row = headerRow is int selected ? selected - 1 : Enumerable.Range(0, Math.Min(MaxHeaderScanRows, sheet.Rows.Count)).FirstOrDefault(i => sheet.Rows[i].Any(x => Normalize(Text(x)) == "malhp"), -1);
        if (row < 0 || row >= sheet.Rows.Count) return new(sheet.Name, sheet.Rows.Count, sheet.ColumnCount, "UNKNOWN", "Chưa nhận diện bảng trong 20 dòng đầu. Hãy chọn dòng tiêu đề.", 1, 1, [], [], ["Mã LHP"]);
        var main = sheet.Rows[row]; var keys = main.Select(x => Normalize(Text(x))).ToHashSet();
        var huit = new[] { "stt", "malhp", "tenmonhoc", "sotc", "lophoc", "lichhoc", "thoigianhoc", "tuanhoc", "tongsotiet", "phong", "giangvien" }.All(keys.Contains);
        var count = headerCount ?? (huit ? 2 : 1);
        if (row + count > sheet.Rows.Count) throw new ArgumentException("Sheet không đủ các dòng tiêu đề đã chọn.");
        var child = count == 2 ? sheet.Rows[row + 1] : [];
        var samples = sheet.Rows.Skip(row + count).Select((cells, i) => new ImportSampleRow(row + count + i + 1,
            cells.Select(x => x is DateTime date ? date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : Text(x)).Select(x => x.Length > 500 ? x[..500] : x).ToArray()))
            .Where(x => x.Cells.Any(value => value.Length > 0)).Take(5).ToArray();
        var columns = new List<ExcelColumn>(); var detected = new Dictionary<string, int>(); var parent = "";
        for (var i = 0; i < sheet.ColumnCount; i++)
        {
            var top = Text(main.ElementAtOrDefault(i)); var sub = Text(child.ElementAtOrDefault(i));
            if (top.Length > 0) parent = top;
            var key = Normalize(sub.Length > 0 ? sub : top);
            var isWeek = Regex.IsMatch(key, "^t([1-9]|[1-4][0-9]|5[0-3])$");
            var label = count == 2 && sub.Length > 0 ? (isWeek ? "Tuần học" : key is "batdau" or "ketthuc" ? "Thời gian học" : parent) + " / " + sub : top;
            columns.Add(new(i, Letter(i), label.Length > 0 ? label : "Cột " + Letter(i), samples.Select(x => x.Cells.ElementAtOrDefault(i)).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? ""));
            var field = key switch {
                "malhp" or "malophocphan" => "source", "mamon" or "mamonhoc" => "courseCode", "tenmonhoc" => "courseName", "sotc" or "sotinchi" => "credits",
                "tenlop" or "lophoc" => "name", "magv" or "magiangvien" => "teacherCode", "giangvien" => "teacher", "loailich" => "activity", "thu" => "day",
                "tietbatdau" or "tutiet" => "startPeriod", "tietketthuc" or "dentiet" => "endPeriod", "phong" or "phonghoc" => "room",
                "tungay" or "batdau" => "from", "denngay" or "ketthuc" => "to", "cactuancolich" => "weeks", "sotiet" => "sourcePeriods", "tongsotiet" => "sourceTotal",
                "giaidoan" => "phase", "nhomgio" => "bellGroup", "stt" => "ordinal", _ => Regex.IsMatch(key, "^t([1-9]|[1-4][0-9]|5[0-3])$") ? "week" + key[1..] : null };
            if (field is not null && !detected.TryAdd(field, i)) throw new ArgumentException($"Tiêu đề trường {field} bị trùng ở cột {Letter(i)}.");
        }
        if (overrides is not null)
            foreach (var pair in overrides)
            {
                if (!Fields.Any(x => x.Field == pair.Key) && pair.Key != "ordinal" && !Regex.IsMatch(pair.Key, "^week([1-9]|[1-4][0-9]|5[0-3])$")) throw new ArgumentException("Trường ánh xạ không hợp lệ: " + pair.Key);
                if (huit && (pair.Key == "weeks" && pair.Value != -1 || pair.Key.StartsWith("week") && pair.Key != "weeks" && (!detected.TryGetValue(pair.Key, out var weekIndex) || pair.Value != weekIndex)))
                    throw new ArgumentException("Các tuần có lịch của HUIT phải dùng cả nhóm cột tuần đã nhận diện; không ánh xạ sang cột đơn.");
                if (pair.Value == -1) { detected.Remove(pair.Key); continue; }
                if (pair.Value < 0 || pair.Value >= sheet.ColumnCount) throw new ArgumentException("Cột ánh xạ nằm ngoài sheet.");
                detected[pair.Key] = pair.Value;
            }
        var duplicate = detected.Where(x => x.Key != "ordinal").GroupBy(x => x.Value).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Cột {Letter(duplicate.Key)} đang dùng cho nhiều trường: {string.Join(", ", duplicate.Select(x => Fields.FirstOrDefault(f => f.Field == x.Key).Label ?? x.Key))}. Mỗi cột chỉ được ánh xạ một trường.");
        var fields = Fields.Concat(detected.Keys.Where(x => x.StartsWith("week") && x != "weeks").OrderBy(x => int.Parse(x[4..])).Select(x => (x, "T" + x[4..], false))).ToArray();
        var mappings = fields.Select(x => new ImportColumn(x.Item1, x.Item2, detected.TryGetValue(x.Item1, out var index) ? index : null, x.Item3)).ToList();
        if (detected.TryGetValue("ordinal", out var ordinal)) mappings.Add(new("ordinal", "STT", ordinal, false));
        var missing = Fields.Where(x => x.Required && !detected.ContainsKey(x.Field)).Select(x => x.Label).ToList();
        if (!detected.ContainsKey("courseCode") && !detected.ContainsKey("courseName")) missing.Add("Mã môn hoặc Tên môn học");
        if (!detected.ContainsKey("teacherCode") && !detected.ContainsKey("teacher")) missing.Add("Mã GV hoặc Giảng viên");
        if (huit && !detected.Keys.Any(x => x.StartsWith("week") && x != "weeks")) missing.Add("Nhóm cột tuần T1–T26 (kiểm tra hai dòng tiêu đề)");
        return new(sheet.Name, sheet.Rows.Count, sheet.ColumnCount, huit ? "HUIT" : "FLAT", huit ? "Đã nhận diện định dạng thời khóa biểu HUIT" : "Đã nhận diện bảng phẳng", row + 1, count, columns.ToArray(), mappings.ToArray(), missing.ToArray()) { SampleRows = samples };
    }

    public static string Text(object? value) => value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture)!.Trim();
    public static bool IsSelectedWeekMark(string value) => value.Trim().ToLowerInvariant() is "x" or "1" or "true" or "✓" or "✔" or "☑" or "√";
    public static string Normalize(string value) => new(value.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).ToArray());
    private static string Letter(int index) { var value = ""; do { value = (char)('A' + index % 26) + value; index = index / 26 - 1; } while (index >= 0); return value; }
}
