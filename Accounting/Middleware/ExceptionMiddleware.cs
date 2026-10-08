using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Accounting.Models.DTOs;
using Accounting.Services.Localization;

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
            // รหัสอ้างอิงสั้น ๆ ผูก response ↔ แถวใน ErrorLogs — ผู้ใช้เจอ 500
            // แจ้งรหัสนี้มา ก็เปิด ErrorLogs (หน้า admin) หาแถวจริงได้ทันที
            // (เดิม "เกิดข้อผิดพลาดภายในระบบ" เฉย ๆ ตามรอยไม่ได้เลยว่าใบไหน/บรรทัดไหน)
            var refCode = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            _logger.LogError(ex, "Unhandled exception [REF:{Ref}]: {Message}", refCode, ex.Message);
            context.Items["__ErrorLogged"] = true;
            await SaveErrorLogAsync(context, ex, refCode);
            await HandleExceptionAsync(context, ex, refCode);
        }
    }

    /// <summary>สถานะ HTTP ของ exception — ตัวตั้งเดียวที่ทั้ง "คำตอบถึงผู้ใช้" และ "แถว Error Logs" ใช้ (ห้ามมีตารางที่สอง)</summary>
    public static int StatusFor(Exception exception) => exception switch
    {
        Accounting.Helpers.BusinessRuleException bre => bre.StatusCode,
        UnauthorizedAccessException => 401,
        KeyNotFoundException => 404,
        InvalidOperationException => 400,
        ArgumentException => 400,
        FormatException => 400,
        Microsoft.EntityFrameworkCore.DbUpdateException dbe
            when dbe.InnerException is Npgsql.PostgresException { SqlState: "23505" } => 409,
        _ => 500
    };

    private static async Task SaveErrorLogAsync(HttpContext context, Exception exception, string refCode)
    {
        // ตัวตั้งเดียวกับสถานะที่ตอบผู้ใช้ (HandleExceptionAsync) — เดิมตารางนี้ไม่มี BusinessRuleException/23505
        // ⇒ Error Logs บันทึก 500 ทั้งที่ผู้ใช้ได้ 400/409/422 (ผู้ดูแลอ่าน log แล้วเข้าใจผิดว่าระบบพัง · 2026-10-08)
        var statusCode = StatusFor(exception);

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
            // ฝ่ายค้าน GWO-6: path ของ webhook มีรหัสลับของร้าน — ปิดบังก่อนเก็บลงตาราง
            cmd.Parameters.AddWithValue("@p", Accounting.Helpers.GatewayWebhookRoute.RedactPath(context.Request.Path.ToString()));
            cmd.Parameters.AddWithValue("@m", context.Request.Method);
            cmd.Parameters.AddWithValue("@q", context.Request.QueryString.ToString());
            cmd.Parameters.AddWithValue("@s", statusCode);
            cmd.Parameters.AddWithValue("@et", exception.GetType().FullName ?? "Unknown");
            cmd.Parameters.AddWithValue("@msg", $"[REF:{refCode}] {exception.Message}");
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

    private static async Task HandleExceptionAsync(HttpContext context, Exception exception, string refCode)
    {
        context.Response.ContentType = "application/json";

        // ข้อความที่จะส่งออกไปต้องเป็น "ข้อความที่ตั้งใจสื่อสารกับผู้ใช้" เท่านั้น
        //
        // ปัญหาเดิม: โค้ดโยน InvalidOperationException/ArgumentException สำหรับกฎ
        // ธุรกิจ (ข้อความไทย) แต่ EF/LINQ/framework ก็โยนชนิดเดียวกัน ("Sequence
        // contains no elements", "Nullable object must have a value") ⇒ ข้อความ
        // ภายในระบบรั่วถึง client เป็น HTTP 400 และผู้ใช้เห็น error ที่ไม่มีความหมาย
        //
        // ทางแก้ระยะยาว = BusinessRuleException (โค้ดใหม่ใช้ตัวนี้)
        // ทางแก้ที่ครอบโค้ดเดิมทั้งหมดโดยไม่ต้องแก้ throw หลายร้อยจุด: ใช้กติกา
        // ของโปรเจกต์เองเป็นตัวแยก — CLAUDE.md กำหนดว่า UI string เป็นภาษาไทย
        // และ ErrorMessageTranslator ก็ออกแบบบนสมมติฐาน "ข้อความ backend เป็นไทย"
        // ⇒ ข้อความที่ไม่มีอักษรไทยเลย ถือว่าเป็น framework error → ปิดบัง
        static bool LooksUserFacing(string? m) =>
            !string.IsNullOrWhiteSpace(m) && m.Any(ch => ch is >= '฀' and <= '๿');

        var statusCode = (HttpStatusCode)StatusFor(exception);
        var (_, message) = exception switch
        {
            // กฎธุรกิจที่ประกาศชัด — ส่งข้อความออกเสมอ ไม่ต้องเดา
            Accounting.Helpers.BusinessRuleException bre
                => ((HttpStatusCode)bre.StatusCode, bre.Message),
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized,
                LooksUserFacing(exception.Message) ? exception.Message : "ไม่มีสิทธิ์เข้าถึงรายการนี้"),
            KeyNotFoundException => (HttpStatusCode.NotFound,
                LooksUserFacing(exception.Message) ? exception.Message : "ไม่พบข้อมูลที่ร้องขอ"),
            InvalidOperationException => (HttpStatusCode.BadRequest,
                LooksUserFacing(exception.Message) ? exception.Message : "ไม่สามารถดำเนินการนี้ได้ในสถานะปัจจุบัน"),
            ArgumentException => (HttpStatusCode.BadRequest,
                LooksUserFacing(exception.Message) ? exception.Message : "ข้อมูลที่ส่งมาไม่ถูกต้อง"),
            FormatException => (HttpStatusCode.BadRequest, "ข้อมูลไม่ถูกต้อง"),
            // ชนคีย์ไม่ซ้ำของ Postgres (23505) — เดิมตกลงไปที่ default แล้วผู้ใช้เห็น
            // "เกิดข้อผิดพลาดภายในระบบ" ซึ่งบอกไม่ได้ว่าต้องแก้อะไร ทั้งที่เป็นเรื่องที่
            // ผู้ใช้แก้เองได้ (เปลี่ยนรหัส/ชื่อ/URL) — บั๊กจริง REF:F37BE341
            // ⚠️ ข้อความ **บอกอาการ ไม่วินิจฉัยสาเหตุ**: ที่นี่ไม่รู้ว่าซ้ำกับแถวที่เห็นอยู่
            // หรือแถวที่ถูกลบไปแล้วยังจองคีย์ไว้ — เดาแล้วพาไล่ผิดทาง (บทเรียน CSP/Google SSO)
            // รหัสอ้างอิงยังติดไปด้วยเสมอ ผู้ดูแลจึงเปิด Error Logs ดูชื่อ constraint ได้
            Microsoft.EntityFrameworkCore.DbUpdateException dbe
                when dbe.InnerException is Npgsql.PostgresException { SqlState: "23505" }
                => (HttpStatusCode.Conflict,
                    $"ข้อมูลนี้ซ้ำกับรายการที่มีอยู่แล้ว — รหัส ชื่อ หรือ URL ที่กรอกถูกใช้ไปแล้ว กรุณาเปลี่ยนแล้วลองใหม่ (รหัสอ้างอิง {refCode})"),
            _ => (HttpStatusCode.InternalServerError,
                $"เกิดข้อผิดพลาดภายในระบบ (รหัสอ้างอิง {refCode} — แจ้งรหัสนี้ให้ผู้ดูแลระบบเพื่อดูรายละเอียดใน Error Logs)")
        };

        // ข้อความที่ถูกปิดบังยังต้องตามรอยได้ — log ไว้ให้ dev เห็นว่าเกิดอะไรจริง
        // (ตัวเต็มลง ErrorLogs อยู่แล้วจากขั้นก่อนหน้า ตรงนี้ทำให้ค้นง่ายขึ้น)
        if (exception is not Accounting.Helpers.BusinessRuleException
            && !LooksUserFacing(exception.Message)
            && statusCode != HttpStatusCode.InternalServerError)
        {
            context.RequestServices.GetService<ILogger<ExceptionMiddleware>>()?
                .LogWarning(exception,
                    "ปิดบังข้อความ exception ภายในระบบไม่ให้ส่งออก ({Type} → {Status})",
                    exception.GetType().Name, (int)statusCode);
        }

        var locale = ErrorMessageTranslator.ResolveLocale(context.Request.Headers["Accept-Language"].ToString());
        var translatedMessage = ErrorMessageTranslator.Translate(message, locale);

        context.Response.StatusCode = (int)statusCode;

        // BusinessRuleException ที่มี RuleCode: ส่งรหัสกฎไปใน data ให้หน้าเว็บตัดสินใจ "ทางไปต่อ" ได้
        // (เช่น CONTACT-ROLE-CUSTOMER → เสนอเปิดสถานะลูกค้าให้เลย · CONVERT-OVER-AMOUNT → ยืนยันแล้วแปลงต่อ)
        // ข้อความยังเป็นตัวหลักเหมือนเดิม — รหัสเป็นส่วนเสริม ไม่กระทบผู้เรียกที่อ่านแค่ message
        object? data = exception is Accounting.Helpers.BusinessRuleException { RuleCode: { Length: > 0 } rc }
            ? new { ruleCode = rc }
            : null;
        var response = new ApiResponse<object>(false, data, translatedMessage);
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await context.Response.WriteAsync(json);
    }
}
