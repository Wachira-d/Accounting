using System.Text;
using System.Text.Json;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations;

public class LineNotifyService : ILineNotifyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LineNotifyService> _logger;

    public LineNotifyService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<LineNotifyService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendMessageAsync(string message)
    {
        var channelAccessToken = _configuration["Line:ChannelAccessToken"];
        var groupId = _configuration["Line:GroupId"];

        if (string.IsNullOrEmpty(channelAccessToken) || string.IsNullOrEmpty(groupId))
        {
            _logger.LogWarning("LINE Messaging API not configured. Skipping notification.");
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", channelAccessToken);

            var payload = new
            {
                to = groupId,
                messages = new[]
                {
                    new { type = "text", text = message }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("https://api.line.me/v2/bot/message/push", content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("LINE message sent successfully to group {GroupId}", groupId);
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("LINE API error: {StatusCode} - {Body}", response.StatusCode, responseBody);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send LINE message");
        }
    }

    public async Task NotifyDocumentApprovedAsync(Guid companyId, string documentNumber, string contactName, decimal amount)
    {
        var msg = $"✅ เอกสารอนุมัติแล้ว\n📄 {documentNumber}\n👤 {contactName}\n💰 {amount:N2} บาท";
        await SendMessageAsync(msg);
    }

    public async Task NotifyPaymentReceivedAsync(Guid companyId, string documentNumber, decimal amount)
    {
        var msg = $"💵 รับชำระเงินแล้ว\n📄 {documentNumber}\n💰 {amount:N2} บาท\n🕐 {DateTime.Now:dd/MM/yyyy HH:mm}";
        await SendMessageAsync(msg);
    }

    public async Task NotifyOverdueInvoiceAsync(Guid companyId, string documentNumber, string contactName, decimal amount, int daysOverdue)
    {
        var msg = $"⚠️ ใบแจ้งหนี้เกินกำหนด\n📄 {documentNumber}\n👤 {contactName}\n💰 ค้างชำระ {amount:N2} บาท\n📅 เกินกำหนด {daysOverdue} วัน";
        await SendMessageAsync(msg);
    }

    public async Task NotifyBankSyncCompleteAsync(Guid companyId, string bankName, int newTransactions)
    {
        var msg = $"🏦 Sync ธนาคารสำเร็จ\n🔄 {bankName}\n📊 รายการใหม่ {newTransactions} รายการ\n🕐 {DateTime.Now:dd/MM/yyyy HH:mm}";
        await SendMessageAsync(msg);
    }

    public async Task NotifyECommerceSyncAsync(Guid companyId, string platform, int newOrders, decimal totalAmount)
    {
        var msg = $"🛒 Sync {platform} สำเร็จ\n📦 ออเดอร์ใหม่ {newOrders} รายการ\n💰 ยอดรวม {totalAmount:N2} บาท\n🕐 {DateTime.Now:dd/MM/yyyy HH:mm}";
        await SendMessageAsync(msg);
    }

    public async Task NotifyPayrollCompletedAsync(Guid companyId, string runName, int employeeCount, decimal totalNet)
    {
        var msg = $"💼 คำนวณเงินเดือนเสร็จสิ้น\n📋 {runName}\n👥 {employeeCount} คน\n💰 รวมจ่ายสุทธิ {totalNet:N2} บาท";
        await SendMessageAsync(msg);
    }

    public async Task NotifyLowBalanceAsync(Guid companyId, string accountName, decimal balance, decimal threshold)
    {
        var msg = $"🔴 แจ้งเตือน: ยอดเงินต่ำ\n🏦 {accountName}\n💰 คงเหลือ {balance:N2} บาท\n⚠️ ต่ำกว่าเกณฑ์ {threshold:N2} บาท";
        await SendMessageAsync(msg);
    }
}
