using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HTBAM.Domain.Entities;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Support;

public static class BusinessCodes
{
    // Every allocation shares the academic-scheduling transaction/application lock.
    public const string Resource = "HTBAM:academic-scheduling";

    public static string Segment(string? value, string label)
    {
        var plain = new string((value ?? "").Trim().Replace('đ', 'd').Replace('Đ', 'D')
            .Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();
        if (!Regex.IsMatch(plain, "^[A-Z0-9]+(?:-[A-Z0-9]+)*$") || plain.Length > 40)
            throw new ArgumentException($"{label} cần 1–40 ký tự chữ, số hoặc dấu gạch ngang. Vui lòng bổ sung thông tin trước khi lưu.");
        return plain;
    }

    public static async Task<string> Next(AppDbContext db, string prefix, int digits, IQueryable<string> existing, CancellationToken ct, bool unsuffixed = false)
    {
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Cấp mã cần transaction có khóa nghiệp vụ.");
        var sequence = db.ChangeTracker.Entries<BusinessCodeSequence>().Select(x => x.Entity).FirstOrDefault(x => x.Scope == prefix)
            ?? await db.BusinessCodeSequences.SingleOrDefaultAsync(x => x.Scope == prefix, ct);
        if (sequence is null) { sequence = new() { Scope = prefix }; db.BusinessCodeSequences.Add(sequence); }
        var root = unsuffixed ? prefix.TrimEnd('-') : prefix;
        var codes = (await existing.Where(x => x.StartsWith(root)).ToArrayAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lastExisting = codes.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(x => x[prefix.Length..]).Where(x => x.Length == digits && x.All(char.IsAsciiDigit))
            .Select(int.Parse).DefaultIfEmpty(0).Max();
        sequence.LastNumber = Math.Max(sequence.LastNumber, lastExisting);
        if (unsuffixed && codes.Contains(root)) sequence.LastNumber = Math.Max(1, sequence.LastNumber);
        for (var n = sequence.LastNumber + 1; n <= Math.Pow(10, digits) - 1; n++)
        {
            var candidate = unsuffixed && n == 1 ? prefix.TrimEnd('-') : prefix + n.ToString("D" + digits, CultureInfo.InvariantCulture);
            if (codes.Contains(candidate)) continue;
            sequence.LastNumber = n;
            return candidate;
        }
        throw new InvalidOperationException("Phạm vi này đã hết số cấp mã. Vui lòng liên hệ quản trị viên để mở rộng phạm vi.");
    }

    public static string RoomPrefix(string? campus, string? building, string? number, string roomType)
    {
        var room = Segment(number, "Số phòng / phòng trực tuyến");
        var code = roomType == "ZOOM" ? "PH-ONLINE-" + room : $"PH-{Segment(campus, "Cơ sở")}-{Segment(building, "Dãy hoặc tòa")}-{room}";
        if (code.Length > 48) throw new ArgumentException("Thông tin cơ sở, tòa và phòng quá dài. Vui lòng dùng tên rút gọn để mã phòng không vượt quá 48 ký tự.");
        return code;
    }
}
