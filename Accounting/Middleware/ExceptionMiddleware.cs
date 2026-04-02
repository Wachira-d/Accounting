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
                    ArgumentException => 400,
                    FormatException => 400,
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
            // EF-based logging failed (possibly model building issue) — try raw ADO.NET
            var logger = context.RequestServices.GetService<ILogger<ExceptionMiddleware>>();
            logger?.LogWarning(logEx, "EF error log failed, trying raw ADO.NET");

            try
            {
                var config = context.RequestServices.GetService<IConfiguration>();
                var connStr = config?.GetConnectionString("DefaultConnection");
                if (!string.IsNullOrEmpty(connStr))
                {
                    using var conn = new Npgsql.NpgsqlConnection(connStr);
                    await conn.OpenAsync();
                    var sql = @"INSERT INTO ""ErrorLogs"" (""Id"",""RequestPath"",""HttpMethod"",""QueryString"",""StatusCode"",""ExceptionType"",""Message"",""StackTrace"",""InnerException"",""UserId"",""IpAddress"",""UserAgent"",""Timestamp"",""CreatedAt"",""IsDeleted"")
                        SELECT gen_random_uuid(),@p,@m,@q,@s,@et,@msg,@st,@ie,@u,@ip,@ua,now(),now(),false
                        WHERE EXISTS (SELECT 1 FROM information_schema.tables WHERE lower(table_name)='errorlogs')";
                    using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@p", context.Request.Path.ToString() ?? "");
                    cmd.Parameters.AddWithValue("@m", context.Request.Method ?? "");
                    cmd.Parameters.AddWithValue("@q", context.Request.QueryString.ToString() ?? "");
                    cmd.Parameters.AddWithValue("@s", 500);
                    cmd.Parameters.AddWithValue("@et", exception.GetType().FullName ?? "Unknown");
                    cmd.Parameters.AddWithValue("@msg", exception.Message ?? "");
                    cmd.Parameters.AddWithValue("@st", (object?)exception.StackTrace ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ie", (object?)exception.InnerException?.Message ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@u", (object?)context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ip", (object?)context.Connection.RemoteIpAddress?.ToString() ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ua", context.Request.Headers.UserAgent.ToString() ?? "");
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception rawEx)
            {
                logger?.LogError(rawEx, "Raw ADO.NET error logging also failed");
            }
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
