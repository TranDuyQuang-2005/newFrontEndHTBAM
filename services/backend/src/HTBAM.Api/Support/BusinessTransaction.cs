using System.Data;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace HTBAM.Api.Support;

public static class BusinessTransaction
{
    public static async Task<T> Run<T>(AppDbContext db, string resource, Func<Task<T>> work, CancellationToken ct)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            // Queries and permission checks belong inside this callback, after acquiring the lock.
            await using var tx = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
            if (db.Database.IsSqlServer())
            {
                try { await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @result < 0 THROW 50001, 'Business resource is busy. Retry the request.', 1;", ct); }
                catch (SqlException ex) when (ex.Number == 50001) { throw new InvalidOperationException("Dữ liệu đang được thao tác khác xử lý. Vui lòng tải lại và thử lại.", ex); }
            }
            var result = await work();
            if (tx is not null) await tx.CommitAsync(ct);
            return result;
        });
    }
}
