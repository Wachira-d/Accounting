using System.Security.Claims;
using Accounting.Data;
using Accounting.Models.Entities;

namespace Accounting.Middleware;

/// <summary>
/// Middleware: บันทึก API errors ที่ไม่ใช่ exception ลง ErrorLog
/// เช่น 404 (endpoint not found), 401 (unauthorized), 403 (forbidden), 500 ที่ไม่มี exception
/// ExceptionMiddleware จัดการเฉพาะ unhandled exceptions เท่านั้น — middleware นี้จับ errors ที่เหลือ
/// </summary>
public class ApiErrorLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiErrorLoggingMiddleware> _logger;

    public ApiErrorLoggingMiddleware(RequestDelegate next, ILogger<ApiErrorLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        // Only log errors for API routes
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return;

        var statusCode = context.Response.StatusCode;

        // Log 4xx and 5xx responses that were NOT already logged by ExceptionMiddleware
        // ExceptionMiddleware sets a flag when it handles an exception
        if (statusCode >= 400 && !context.Items.ContainsKey("__ErrorLogged"))
        {
            var message = statusCode switch
            {
                401 => "Unauthorized — token missing or invalid",
                403 => "Forbidden — insufficient permissions",
                404 => "API endpoint not found",
                405 => "HTTP method not allowed",
                429 => "Rate limit exceeded",
                _ => $"HTTP {statusCode} error"
            };

            _logger.LogWarning("API error {StatusCode} on {Method} {Path}: {Message}",
                statusCode, context.Request.Method, path, message);

            await SaveErrorLogAsync(context, statusCode, message);
        }
    }

    private static async Task SaveErrorLogAsync(HttpContext context, int statusCode, string message)
    {
        try
        {
            var config = context.RequestServices.GetService<IConfiguration>();
            var connStr = config?.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connStr)) return;

            // Use raw ADO.NET to avoid EF issues (EF model might not be ready)
            using var conn = new Npgsql.NpgsqlConnection(connStr);
            await conn.OpenAsync();

            var sql = @"INSERT INTO ""ErrorLogs"" (""RequestPath"",""HttpMethod"",""QueryString"",""StatusCode"",""ExceptionType"",""Message"",""UserId"",""IpAddress"",""UserAgent"",""Timestamp"")
                SELECT @p,@m,@q,@s,'HTTP_ERROR',@msg,@u,@ip,@ua,now()
                WHERE EXISTS (SELECT 1 FROM information_schema.tables WHERE lower(table_name)='errorlogs')";

            using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@p", context.Request.Path.ToString());
            cmd.Parameters.AddWithValue("@m", context.Request.Method);
            cmd.Parameters.AddWithValue("@q", context.Request.QueryString.ToString());
            cmd.Parameters.AddWithValue("@s", statusCode);
            cmd.Parameters.AddWithValue("@msg", message);
            cmd.Parameters.AddWithValue("@u", (object?)context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ip", (object?)context.Connection.RemoteIpAddress?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ua", context.Request.Headers.UserAgent.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Logging failure should never break the app
        }
    }
}
