using HTBAM.Application.DTOs;
using Microsoft.Extensions.Caching.Memory;

namespace HTBAM.Api.Support;

// Ephemeral previews never create database records. Confirmation receipts remain durable in SQL.
public static class ImportPreviewStore
{
    private sealed record Entry(long UserId, ImportSnapshot Snapshot);
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit = 100000 });
    public static void Put(long userId, ImportSnapshot snapshot)
    {
        Cache.Set(snapshot.Token, new Entry(userId, snapshot), new MemoryCacheEntryOptions { AbsoluteExpiration = snapshot.ExpiresAt, Size = Math.Max(1, snapshot.DataRows) });
        if (!Cache.TryGetValue(snapshot.Token, out _)) throw new InvalidOperationException("Bộ nhớ xem trước đang đầy. Vui lòng thử lại sau.");
    }
    public static ImportSnapshot Get(long userId, string token)
    {
        if (!Cache.TryGetValue<Entry>(token, out var entry) || entry!.UserId != userId) throw new ArgumentException("Bản xem trước không tồn tại, đã hết hạn hoặc không thuộc tài khoản này. Hãy tải lại file.");
        if (entry.Snapshot.ExpiresAt < DateTime.UtcNow) throw new InvalidOperationException("Bản xem trước đã hết hạn sau 30 phút. Hãy tải lại file.");
        return entry.Snapshot;
    }
}
