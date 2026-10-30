using System.Net;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 219 เฟส 2 ขั้น 2 (G-7 · คำตัดสินข้อ 180) — <b>ผู้ส่งใบแจ้งหนี้/แจ้งเตือนค่าบริการ NextAcc ถึงผู้เช่าตัวเดียว</b> (แพลตฟอร์ม → เจ้าของบริษัท)
/// <list type="bullet">
/// <item>ช่อง: อีเมลระบบ (<c>IEmailService.TrySendAsync</c>) · LINE ผ่าน OA กลางของ NextAcc (<c>TryPushViaNextAccAsync</c> — <b>ไม่นับโควตาข้อความ</b>
/// ของร้าน: เป็นข้อความของแพลตฟอร์มเอง) · เปิด/ปิดตามค่าตั้ง</item>
/// <item><b>idempotent</b>: จองแถว <c>PlatformBillingNotices</c> (unique MessageKey+Channel · <c>INSERT … ON CONFLICT DO NOTHING</c>) <b>ก่อน</b>ส่ง ⇒ หลายเครื่อง/รันซ้ำ
/// ส่งได้ครั้งเดียว · ล้มกลางทาง = แถวค้าง Claimed (ยอมพลาด 1 ฉบับดีกว่าส่งซ้ำ) · ผลจริงประทับ Sent/Failed/Skipped + เหตุ (แอดมินเห็น — ล้มดัง)</item>
/// <item>เนื้อความ HtmlEncode ทุกช่องที่มาจากข้อมูล (กฎ #4 C)</item>
/// </list>
/// </summary>
public interface IPlatformBillingNotifier
{
    /// <summary>ส่ง "ใบแจ้งหนี้ใหม่" ของใบนี้ (คีย์ issued — ส่งครั้งเดียวต่อใบ) · ไม่โยน</summary>
    Task InvoiceIssuedAsync(Guid buyerCompanyId, Guid invoiceDocumentId, CancellationToken ct = default);

    /// <summary>ส่งข้อความตามคีย์ (ทุกช่องที่เปิด) · คืนจำนวนช่องที่ส่งถึง · ไม่โยน</summary>
    Task<int> SendAsync(Guid buyerCompanyId, Guid? documentId, string messageKey, string subject, string body, CancellationToken ct = default);
}

public class PlatformBillingNotifier : IPlatformBillingNotifier
{
    public const string ChannelEmail = "email", ChannelLine = "line";
    public const string StatusClaimed = "Claimed", StatusSent = "Sent", StatusFailed = "Failed", StatusSkipped = "Skipped";
    public const string BillingPath = "/pages/billing.html";

    private readonly AccountingDbContext _db;
    private readonly IEmailService? _email;
    private readonly ILineNotifyService? _line;
    private readonly ILogger<PlatformBillingNotifier> _logger;

    public PlatformBillingNotifier(AccountingDbContext db, ILogger<PlatformBillingNotifier> logger,
        IEmailService? email = null, ILineNotifyService? line = null)
    { _db = db; _logger = logger; _email = email; _line = line; }

    public async Task InvoiceIssuedAsync(Guid buyerCompanyId, Guid invoiceDocumentId, CancellationToken ct = default)
    {
        try
        {
            var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct);
            if (platformId == null) return;
            var inv = await PlatformInvoiceQueries.ForBuyer(_db, platformId.Value, buyerCompanyId)
                .FirstOrDefaultAsync(d => d.Id == invoiceDocumentId, ct);
            if (inv == null) return;
            var (subject, body) = PlatformBillingMessages.For(PlatformDunningPolicy.KindIssued, 0, inv);
            await SendAsync(buyerCompanyId, inv.Id, PlatformDunningPolicy.Key(inv.Id, PlatformDunningPolicy.KindIssued, 0), subject, body, ct);
        }
        catch (Exception ex) { _logger.LogError(ex, "ส่งใบแจ้งหนี้ค่าบริการไม่สำเร็จ (company {Company} · ใบ {Doc})", buyerCompanyId, invoiceDocumentId); }
    }

    public async Task<int> SendAsync(Guid buyerCompanyId, Guid? documentId, string messageKey, string subject, string body, CancellationToken ct = default)
    {
        var delivered = 0;
        try
        {
            var policy = await PlatformBillingSettingsService.LoadPolicyAsync(_db, ct);
            var owners = await _db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == buyerCompanyId && cu.Role == UserRole.Owner)
                .Join(_db.Users.AsNoTracking(), cu => cu.UserId, u => u.Id, (cu, u) => new { u.Email, u.FullName, u.LineUserId })
                .ToListAsync(ct);
            var baseUrl = (await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.AppBaseUrl).FirstOrDefaultAsync(ct))?.TrimEnd('/');
            var link = (baseUrl ?? "") + BillingPath;

            if (policy.ReminderEmailEnabled)
            {
                var claim = await ClaimAsync(buyerCompanyId, documentId, messageKey, ChannelEmail, ct);
                if (claim is Guid id)
                {
                    var to = owners.Where(o => !string.IsNullOrWhiteSpace(o.Email)).ToList();
                    if (_email == null || to.Count == 0) await FinishAsync(id, StatusSkipped, _email == null ? "ไม่มีบริการอีเมล" : "ไม่มีอีเมลของเจ้าของบริษัท", ct);
                    else
                    {
                        var ok = 0;
                        var html = $"<div style='font-family:sans-serif;max-width:600px'><h2 style='color:#4F46E5'>{WebUtility.HtmlEncode(subject)}</h2>"
                            + $"<p style='white-space:pre-line'>{WebUtility.HtmlEncode(body)}</p>"
                            + $"<p><a href='{WebUtility.HtmlEncode(link)}'>เปิดศูนย์บิลค่าบริการ</a></p></div>";
                        foreach (var o in to) if (await _email.TrySendAsync(o.Email!, subject, html)) ok++;
                        await FinishAsync(id, ok > 0 ? StatusSent : StatusFailed, ok > 0 ? $"ส่งถึง {ok}/{to.Count}" : "SMTP ไม่ได้ตั้ง/ส่งล้ม", ct);
                        if (ok > 0) delivered++;
                    }
                }
            }
            if (policy.ReminderLineEnabled)
            {
                var claim = await ClaimAsync(buyerCompanyId, documentId, messageKey, ChannelLine, ct);
                if (claim is Guid id)
                {
                    var to = owners.Where(o => !string.IsNullOrWhiteSpace(o.LineUserId)).ToList();
                    if (_line == null || to.Count == 0) await FinishAsync(id, StatusSkipped, _line == null ? "ไม่มีบริการ LINE" : "เจ้าของบริษัทยังไม่ผูก LINE", ct);
                    else
                    {
                        var ok = 0; string? err = null;
                        foreach (var o in to)
                        {
                            var r = await _line.TryPushViaNextAccAsync(o.LineUserId!, new[] { $"{subject}\n\n{body}\n\n🔗 {link}" });
                            if (r.Ok) ok++; else err = r.Error;
                        }
                        await FinishAsync(id, ok > 0 ? StatusSent : StatusFailed, ok > 0 ? $"ส่งถึง {ok}/{to.Count}" : (err ?? "ส่งไม่สำเร็จ"), ct);
                        if (ok > 0) delivered++;
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "ส่งแจ้งเตือนค่าบริการไม่สำเร็จ (company {Company} · คีย์ {Key})", buyerCompanyId, messageKey); }
        return delivered;
    }

    /// <summary>จองสิทธิ์ส่ง — คืน id ของแถวที่จองได้ · null = ส่งไปแล้ว/อีกเครื่องกำลังส่ง</summary>
    private async Task<Guid?> ClaimAsync(Guid buyerCompanyId, Guid? documentId, string key, string channel, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var n = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""PlatformBillingNotices"" (""Id"", ""BuyerCompanyId"", ""DocumentId"", ""MessageKey"", ""Channel"", ""Status"", ""CreatedAt"", ""IsDeleted"")
            VALUES ({id}, {buyerCompanyId}, {documentId}, {key}, {channel}, {StatusClaimed}, {now}, false)
            ON CONFLICT (""MessageKey"", ""Channel"") DO NOTHING", ct);
        return n == 1 ? id : null;
    }

    private async Task FinishAsync(Guid id, string status, string? detail, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var d = detail is { Length: > 500 } ? detail[..500] : detail;
        DateTime? sentAt = status == StatusSent ? now : null;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""PlatformBillingNotices"" SET ""Status"" = {status}, ""Detail"" = {d}, ""SentAt"" = {sentAt}, ""UpdatedAt"" = {now} WHERE ""Id"" = {id}", ct);
    }
}

/// <summary>ข้อความไทยของแต่ละชนิดแจ้งเตือน — ตัวเดียว (pure)</summary>
public static class PlatformBillingMessages
{
    public static (string Subject, string Body) For(string kind, int days, PlatformInvoiceRow inv)
    {
        var amount = $"{inv.BalanceDue:N2} บาท";
        var due = inv.DueDate is DateTime d ? PlatformInvoiceRules.ThaiDate(d).ToString("dd/MM/yyyy") : "-";
        return kind switch
        {
            PlatformDunningPolicy.KindIssued => ($"ใบแจ้งหนี้ค่าบริการ NextAcc {inv.DocumentNumber}",
                $"ใบแจ้งหนี้เลขที่ {inv.DocumentNumber} ยอด {amount} ครบกำหนดชำระ {due}\nชำระได้ที่ศูนย์บิลค่าบริการ (โอนแล้วแนบสลิป)"),
            PlatformDunningPolicy.KindBefore => ($"แจ้งเตือน: ใบแจ้งหนี้ {inv.DocumentNumber} ครบกำหนดในอีก {days} วัน",
                $"ใบแจ้งหนี้เลขที่ {inv.DocumentNumber} ยอดค้าง {amount} ครบกำหนด {due}"),
            PlatformDunningPolicy.KindDue => ($"ใบแจ้งหนี้ {inv.DocumentNumber} ครบกำหนดชำระวันนี้",
                $"ใบแจ้งหนี้เลขที่ {inv.DocumentNumber} ยอดค้าง {amount} ครบกำหนดวันนี้ ({due})"),
            _ => ($"ค้างชำระ: ใบแจ้งหนี้ {inv.DocumentNumber} เกินกำหนด {days} วัน",
                $"ใบแจ้งหนี้เลขที่ {inv.DocumentNumber} ยอดค้าง {amount} เกินกำหนดตั้งแต่ {due}\nกรุณาชำระเพื่อหลีกเลี่ยงการระงับการใช้งาน"),
        };
    }
}
