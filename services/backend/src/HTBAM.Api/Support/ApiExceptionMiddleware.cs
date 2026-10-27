using HTBAM.Application.Services;
using Microsoft.EntityFrameworkCore;

namespace HTBAM.Api.Support;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        try { await next(context); }
        catch (OperationCanceledException ex) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Request aborted by client");
            if (!context.Response.HasStarted) context.Response.StatusCode = 499;
        }
        catch (KeyNotFoundException ex) { await Write(context, 404, "NOT_FOUND", ex.Message); }
        catch (UnauthorizedAccessException) { await Write(context, 403, "FORBIDDEN", "Bạn không có quyền thực hiện thao tác này."); }
        catch (DbUpdateConcurrencyException ex) { await Write(context, 409, "CONCURRENCY_CONFLICT", "Dữ liệu vừa được thay đổi bởi thao tác khác. Hãy tải lại và thử lại."); logger.LogWarning(ex, "Concurrency conflict"); }
        catch (DbUpdateException ex) { await Write(context, 409, "DATA_CONFLICT", "Không thể lưu thay đổi vì dữ liệu đã được cập nhật hoặc đang được sử dụng. Vui lòng tải lại và thử lại."); logger.LogWarning(ex, "Database conflict"); }
        catch (AiServiceUnavailableException ex) { await Write(context, 503, "AI_SERVICE_UNAVAILABLE", ex.Message); }
        catch (InvalidOperationException ex) { await Write(context, 409, "INVALID_STATE", ex.Message); }
        catch (ArgumentException ex) { await Write(context, 400, "BAD_REQUEST", ex.Message); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled API exception");
            await Write(context, 500, "INTERNAL_ERROR", "Có lỗi nội bộ. Vui lòng kiểm tra log máy chủ.");
        }
    }

    private static async Task Write(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new { status, code, message, traceId = context.TraceIdentifier });
    }
}
