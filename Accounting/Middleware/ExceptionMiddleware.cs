using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;

namespace Accounting.Middleware;

public class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception: {Message}", ex.Message);
            await SaveErrorLogAsync(context, ex);
            await HandleExceptionAsync(context, ex);
        }
    }

    private static async Task SaveErrorLogAsync(HttpContext context, Exception exception)
    {
        try
        {
            var db = context.RequestServices.GetService<AccountingDbContext>();
            if (db == null) return;

            var errorLog = new ErrorLog
            {
                RequestPath = context.Request.Path,
                HttpMethod = context.Request.Method,
                QueryString = context.Request.QueryString.ToString(),
                StatusCode = exception switch
                {
                    UnauthorizedAccessException => 401,
                    KeyNotFoundException => 404,
                    InvalidOperationException => 400,
                    _ => 500
                },
                ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                Message = exception.Message,
                StackTrace = exception.StackTrace,
                InnerException = exception.InnerException?.Message,
                UserId = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                IpAddress = context.Connection.RemoteIpAddress?.ToString(),
                UserAgent = context.Request.Headers.UserAgent.ToString(),
                Timestamp = DateTime.UtcNow
            };

            db.ErrorLogs.Add(errorLog);
            await db.SaveChangesAsync();
        }
        catch (Exception logEx)
        {
            // ถ้าบันทึก error log ไม่ได้ ก็ log ลง console ไว้ ไม่ให้กระทบ response
            var logger = context.RequestServices.GetService<ILogger<ExceptionMiddleware>>();
            logger?.LogWarning(logEx, "Failed to save error log to database");
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType = "application/json";

        var (statusCode, message) = exception switch
        {
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, exception.Message),
            KeyNotFoundException => (HttpStatusCode.NotFound, exception.Message),
            InvalidOperationException => (HttpStatusCode.BadRequest, exception.Message),
            _ => (HttpStatusCode.InternalServerError, "เกิดข้อผิดพลาดภายในระบบ")
        };

        context.Response.StatusCode = (int)statusCode;

        var response = new ApiResponse<object>(false, null, message);
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await context.Response.WriteAsync(json);
    }
}
