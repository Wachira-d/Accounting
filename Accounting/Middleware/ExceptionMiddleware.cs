using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Accounting.Models.DTOs;

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
            context.Items["__ErrorLogged"] = true;
            await SaveErrorLogAsync(context, ex);
            await HandleExceptionAsync(context, ex);
        }
    }

    private static async Task SaveErrorLogAsync(HttpContext context, Exception exception)
    {
        var statusCode = exception switch
        {
            UnauthorizedAccessException => 401,
            KeyNotFoundException => 404,
            InvalidOperationException => 400,
            ArgumentException => 400,
            FormatException => 400,
            _ => 500
        };

        // Always use raw ADO.NET — the scoped DbContext may be in a broken state
        // (e.g. failed transaction from the operation that threw the exception)
        try
        {
            var config = context.RequestServices.GetService<IConfiguration>();
            var connStr = config?.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connStr)) return;

            using var conn = new Npgsql.NpgsqlConnection(connStr);
            await conn.OpenAsync();
            var sql = @"INSERT INTO ""ErrorLogs"" (""RequestPath"",""HttpMethod"",""QueryString"",""StatusCode"",""ExceptionType"",""Message"",""StackTrace"",""InnerException"",""UserId"",""IpAddress"",""UserAgent"",""Timestamp"")
                SELECT @p,@m,@q,@s,@et,@msg,@st,@ie,@u,@ip,@ua,now()
                WHERE EXISTS (SELECT 1 FROM information_schema.tables WHERE lower(table_name)='errorlogs')";
            using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@p", context.Request.Path.ToString());
            cmd.Parameters.AddWithValue("@m", context.Request.Method);
            cmd.Parameters.AddWithValue("@q", context.Request.QueryString.ToString());
            cmd.Parameters.AddWithValue("@s", statusCode);
            cmd.Parameters.AddWithValue("@et", exception.GetType().FullName ?? "Unknown");
            cmd.Parameters.AddWithValue("@msg", exception.Message);
            cmd.Parameters.AddWithValue("@st", (object?)exception.StackTrace ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ie", (object?)exception.InnerException?.Message ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@u", (object?)context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ip", (object?)context.Connection.RemoteIpAddress?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ua", context.Request.Headers.UserAgent.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception logEx)
        {
            var logger = context.RequestServices.GetService<ILogger<ExceptionMiddleware>>();
            logger?.LogError(logEx, "Error logging to ErrorLogs table failed");
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
            ArgumentException => (HttpStatusCode.BadRequest, exception.Message),
            FormatException => (HttpStatusCode.BadRequest, "ข้อมูลไม่ถูกต้อง"),
            _ => (HttpStatusCode.InternalServerError, "เกิดข้อผิดพลาดภายในระบบ")
        };

        context.Response.StatusCode = (int)statusCode;

        var response = new ApiResponse<object>(false, null, message);
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await context.Response.WriteAsync(json);
    }
}
