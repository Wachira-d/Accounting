using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class LineNotifyService : ILineNotifyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LineNotifyService> _logger;
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;

    public LineNotifyService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<LineNotifyService> logger,
        AccountingDbContext db,
        ISecretProtector secrets)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _db = db;
        _secrets = secrets;
    }

    /// <summary>หา (token, defaultGroupId) สำหรับบริษัท — ใช้ของบริษัทถ้าตั้ง
    /// LineEnabled + มี token; ไม่งั้น fallback ไป appsettings global (เพื่อ
    /// compatibility กับการตั้งค่าเก่า). Returns (null, null) ถ้าไม่มีเลย.</summary>
    private async Task<(string? Token, string? GroupId)> ResolveConfigAsync(Guid? companyId)
    {
        if (companyId.HasValue)
        {
            var s = await _db.Set<CompanySettings>().AsNoTracking()
                .FirstOrDefaultAsync(x => x.CompanyId == companyId.Value);
            if (s != null && s.LineEnabled && !string.IsNullOrWhiteSpace(s.LineChannelAccessToken))
            {
                var token = _secrets.Unprotect(s.LineChannelAccessToken);
                if (!string.IsNullOrWhiteSpace(token))
                    return (token, s.LineDefaultGroupId);
            }
        }
        return (_configuration["Line:ChannelAccessToken"], _configuration["Line:GroupId"]);
    }

    private async Task<bool> PushRawAsync(string? token, object payload, string context)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogInformation("LINE skipped ({Ctx}) — no token configured", context);
            return false;
        }
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("https://api.line.me/v2/bot/message/push", content);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                _logger.LogError("LINE push failed ({Ctx}): {Status} {Body}", context, resp.StatusCode, body);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LINE push threw ({Ctx})", context);
            return false;
        }
    }

    public async Task SendMessageAsync(string message)
    {
        var (token, groupId) = await ResolveConfigAsync(null);
        if (string.IsNullOrWhiteSpace(groupId)) return;
        await PushRawAsync(token, new
        {
            to = groupId,
            messages = new[] { new { type = "text", text = message } }
        }, "group");
    }

    /// <summary>Push a personalised text message to one user. Used by
    /// the NotificationEngine when a recipient has a bound LineUserId.
    /// No-op when LINE is not configured (no token) or the userId is
    /// empty so the engine can fall back to System / Email cleanly.</summary>
    public Task PushToUserAsync(string lineUserId, string message)
        => PushToUserAsync(null, lineUserId, message);

    public async Task PushToUserAsync(Guid? companyId, string lineUserId, string message)
    {
        if (string.IsNullOrWhiteSpace(lineUserId)) return;
        var (token, _) = await ResolveConfigAsync(companyId);
        await PushRawAsync(token, new
        {
            to = lineUserId,
            messages = new[] { new { type = "text", text = message } }
        }, $"user {lineUserId}");
    }

    public Task PushFlexToUserAsync(string lineUserId, string altText, object flexContents)
        => PushFlexToUserAsync(null, lineUserId, altText, flexContents);

    public async Task PushFlexToUserAsync(Guid? companyId, string lineUserId, string altText, object flexContents)
    {
        if (string.IsNullOrWhiteSpace(lineUserId)) return;
        var (token, _) = await ResolveConfigAsync(companyId);
        await PushRawAsync(token, new
        {
            to = lineUserId,
            messages = new[]
            {
                new
                {
                    type = "flex",
                    altText = string.IsNullOrEmpty(altText) ? "การแจ้งเตือนจาก NextAcc" : altText,
                    contents = flexContents
                }
            }
        }, $"flex user {lineUserId}");
    }

    public async Task NotifyDocumentApprovedAsync(Guid companyId, string documentNumber, string contactName, decimal amount)
    {
        var (token, groupId) = await ResolveConfigAsync(companyId);
        if (string.IsNullOrWhiteSpace(groupId)) return;
        var msg = $"✅ เอกสารอนุมัติแล้ว\n📄 {documentNumber}\n👤 {contactName}\n💰 {amount:N2} บาท";
        await PushRawAsync(token, new { to = groupId, messages = new[] { new { type = "text", text = msg } } }, "doc-approved");
    }

    // Servers run UTC; recipients are Thai-based. Render times in ICT (UTC+7)
    // so the LINE message matches what the user sees on the wall clock.
    private static string NowIct() => DateTime.UtcNow.AddHours(7).ToString("dd/MM/yyyy HH:mm");

    public async Task NotifyPaymentReceivedAsync(Guid companyId, string documentNumber, decimal amount)
    {
        var (token, groupId) = await ResolveConfigAsync(companyId);
        if (string.IsNullOrWhiteSpace(groupId)) return;
        var msg = $"💵 รับชำระเงินแล้ว\n📄 {documentNumber}\n💰 {amount:N2} บาท\n🕐 {NowIct()}";
        await PushRawAsync(token, new { to = groupId, messages = new[] { new { type = "text", text = msg } } }, "payment");
    }

    public async Task NotifyOverdueInvoiceAsync(Guid companyId, string documentNumber, string contactName, decimal amount, int daysOverdue)
    {
        var (token, groupId) = await ResolveConfigAsync(companyId);
        if (string.IsNullOrWhiteSpace(groupId)) return;
        var msg = $"⚠️ ใบแจ้งหนี้เกินกำหนด\n📄 {documentNumber}\n👤 {contactName}\n💰 ค้างชำระ {amount:N2} บาท\n📅 เกินกำหนด {daysOverdue} วัน";
        await PushRawAsync(token, new { to = groupId, messages = new[] { new { type = "text", text = msg } } }, "overdue");
    }

    public async Task NotifyBankSyncCompleteAsync(Guid companyId, string bankName, int newTransactions)
    {
        var (token, groupId) = await ResolveConfigAsync(companyId);
        if (string.IsNullOrWhiteSpace(groupId)) return;
        var msg = $"🏦 Sync ธนาคารสำเร็จ\n🔄 {bankName}\n📊 รายการใหม่ {newTransactions} รายการ\n🕐 {NowIct()}";
        await PushRawAsync(token, new { to = groupId, messages = new[] { new { type = "text", text = msg } } }, "bank-sync");
    }

    public async Task NotifyLodgingBookingAsync(Guid companyId, string propertyName, string reservationNumber,
        string guestName, string roomSummary, DateTime checkIn, DateTime checkOut, int nights,
        decimal totalAmount, decimal depositRequired, bool isSlipUploaded)
    {
        var (token, groupId) = await ResolveConfigAsync(companyId);
        if (string.IsNullOrWhiteSpace(groupId)) return;
        var head = isSlipUploaded ? "📎 แขกส่งสลิปแล้ว รอตรวจสอบ" : "🏨 มีการจองใหม่";
        // วันที่เข้าพักเป็น "วันตามปฏิทิน" อยู่แล้ว (00:00 UTC) — ไม่ต้อง +7 ซ้ำ
        // ต่างจาก NowIct() ที่แปลงเวลา ณ ขณะนั้น
        var msg = $"{head}\n🏠 {propertyName}\n🧾 {reservationNumber}\n👤 {guestName}"
                + $"\n🛏 {roomSummary}"
                + $"\n📅 {checkIn:dd/MM/yyyy} → {checkOut:dd/MM/yyyy} ({nights} คืน)"
                + $"\n💰 ยอดรวม {totalAmount:N2} บาท"
                + (depositRequired > 0 ? $" · มัดจำ {depositRequired:N2} บาท" : "")
                + $"\n🕐 {NowIct()}";
        await PushRawAsync(token, new { to = groupId, messages = new[] { new { type = "text", text = msg } } }, "lodging-booking");
    }

    public async Task NotifyECommerceSyncAsync(Guid companyId, string platform, int newOrders, decimal totalAmount)
    {
        var (token, groupId) = await ResolveConfigAsync(companyId);
        if (string.IsNullOrWhiteSpace(groupId)) return;
        var msg = $"🛒 Sync {platform} สำเร็จ\n📦 ออเดอร์ใหม่ {newOrders} รายการ\n💰 ยอดรวม {totalAmount:N2} บาท\n🕐 {NowIct()}";
        await PushRawAsync(token, new { to = groupId, messages = new[] { new { type = "text", text = msg } } }, "ecom-sync");
    }

    public async Task NotifyPayrollCompletedAsync(Guid companyId, string runName, int employeeCount, decimal totalNet)
    {
        var (token, groupId) = await ResolveConfigAsync(companyId);
        if (string.IsNullOrWhiteSpace(groupId)) return;
        var msg = $"💼 คำนวณเงินเดือนเสร็จสิ้น\n📋 {runName}\n👥 {employeeCount} คน\n💰 รวมจ่ายสุทธิ {totalNet:N2} บาท";
        await PushRawAsync(token, new { to = groupId, messages = new[] { new { type = "text", text = msg } } }, "payroll");
    }

    public async Task NotifyLowBalanceAsync(Guid companyId, string accountName, decimal balance, decimal threshold)
    {
        var (token, groupId) = await ResolveConfigAsync(companyId);
        if (string.IsNullOrWhiteSpace(groupId)) return;
        var msg = $"🔴 แจ้งเตือน: ยอดเงินต่ำ\n🏦 {accountName}\n💰 คงเหลือ {balance:N2} บาท\n⚠️ ต่ำกว่าเกณฑ์ {threshold:N2} บาท";
        await PushRawAsync(token, new { to = groupId, messages = new[] { new { type = "text", text = msg } } }, "low-balance");
    }

    /// <summary>Test config — ส่ง echo message ไปยัง groupId หรือ lineUserId
    /// ที่ระบุ. ใช้จากปุ่ม "ทดสอบ" ในหน้าตั้งค่า. Returns (success, message).</summary>
    public async Task<(bool ok, string message)> TestConfigAsync(Guid companyId, string toLineId)
    {
        if (string.IsNullOrWhiteSpace(toLineId))
            return (false, "กรุณาระบุ LINE User ID หรือ Group ID ปลายทาง");
        var (token, _) = await ResolveConfigAsync(companyId);
        if (string.IsNullOrWhiteSpace(token))
            return (false, "ยังไม่ได้ตั้งค่า Channel Access Token");
        var ok = await PushRawAsync(token, new
        {
            to = toLineId,
            messages = new[] { new { type = "text",
                text = $"✅ ทดสอบ LINE Messaging API สำเร็จ\nบริษัท: {companyId}\nเวลา: {NowIct()}" } }
        }, "test");
        return ok
            ? (true, "ส่งข้อความทดสอบสำเร็จ")
            : (false, "ส่งไม่สำเร็จ ตรวจสอบ Token และ ID ปลายทาง (ดู log)");
    }
}
