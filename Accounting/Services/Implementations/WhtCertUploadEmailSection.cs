using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §3.2 จุดแสดงลิงก์ข้อ 1 · §6.4) — บล็อก "ส่งหนังสือรับรอง 50 ทวิ ให้เรา" ในอีเมลใบเสร็จหลังรับชำระ
/// (จุดต่อเดิม <see cref="IReceiptEmailSectionProvider"/> → ผู้ประกอบเดียว <c>ReceiptEmailSections.ComposeBody</c> — ค่าทุกตัวถูกหนีที่นั่น)
///
/// <para>แสดงเมื่อการชำระนี้ถูกหัก ณ ที่จ่าย (&gt; 0) และแถวเครดิตของเอกสารยังรอใบ (Pending) · ออกลิงก์ใหม่ต่ออีเมล (ฐานเก็บเฉพาะ hash) ·
/// ไม่มีที่อยู่เว็บของระบบ ⇒ ไม่มีบล็อก (ไม่ส่งลิงก์เสีย) · ไม่ใช่ https ⇒ ผู้ประกอบตัดลิงก์ทิ้งเอง</para>
/// </summary>
public class WhtCertUploadEmailSection : IReceiptEmailSectionProvider
{
    private readonly AccountingDbContext _db;
    private readonly IConfiguration? _config;
    private readonly IServiceScopeFactory? _scopes;

    public WhtCertUploadEmailSection(AccountingDbContext db, IConfiguration? config = null, IServiceScopeFactory? scopes = null)
    { _db = db; _config = config; _scopes = scopes; }

    public async Task<IReadOnlyList<ReceiptEmailSections.Section>> SectionsAsync(Guid companyId, Guid receiptDocumentId, Guid paymentId,
        CancellationToken ct = default)
    {
        var pay = await _db.Payments.AsNoTracking()
            .Where(p => p.Id == paymentId && p.CompanyId == companyId && !p.IsDeleted)
            .Select(p => new { p.DocumentId, p.WithholdingTaxAmount }).FirstOrDefaultAsync(ct);
        if (pay == null || pay.WithholdingTaxAmount <= 0.005m) return Array.Empty<ReceiptEmailSections.Section>();
        var doc = await _db.Documents.AsNoTracking().Where(d => d.Id == pay.DocumentId && d.CompanyId == companyId)
            .Select(d => new { d.ContactId }).FirstOrDefaultAsync(ct);
        if (doc == null) return Array.Empty<ReceiptEmailSections.Section>();
        var awaiting = await _db.WhtCreditsReceived.AsNoTracking().AnyAsync(w => w.CompanyId == companyId && w.DocumentId == pay.DocumentId
            && w.Status == WhtCreditStatus.Pending && w.WhtAmount - w.ReceivedAmount > 0.005m, ct);
        if (!awaiting) return Array.Empty<ReceiptEmailSections.Section>();

        // ฝ่ายค้าน WP-10 M3: ออกลิงก์ใน scope ของตัวเอง — ไม่ SaveChanges บน DbContext ของเส้นส่งใบเสร็จกลางทาง (บันทึกค่าที่เส้นนั้นยังไม่ตั้งใจบันทึก)
        WhtCertUploadLinkIssued issued;
        if (_scopes != null)
        {
            using var scope = _scopes.CreateScope();
            var own = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            issued = await WhtCertUploadLinkService.IssueCoreAsync(own, _config, companyId, doc.ContactId, pay.DocumentId, "receipt-email",
                null, "receipt-email", ct);
        }
        else
            issued = await WhtCertUploadLinkService.IssueCoreAsync(_db, _config, companyId, doc.ContactId, pay.DocumentId, "receipt-email",
                null, "receipt-email", ct);
        if (issued.Url == null) return Array.Empty<ReceiptEmailSections.Section>();
        var co = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.Name, c.TaxId, c.BranchCode }).FirstOrDefaultAsync(ct);
        return new[]
        {
            new ReceiptEmailSections.Section(
                "ส่งหนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ) ให้เรา",
                $"ท่านหักภาษี ณ ที่จ่ายไว้ {pay.WithholdingTaxAmount:N2} บาท — กรุณาออกหนังสือรับรองในนาม {co?.Name} "
                + $"เลขประจำตัวผู้เสียภาษี {co?.TaxId} ({TaxBranchCode.Label(co?.BranchCode)}) แล้วส่งไฟล์ (PDF หรือรูปถ่าย) ได้ที่ลิงก์นี้ — ไม่ต้องเข้าสู่ระบบ",
                issued.Url, "อัปโหลดหนังสือรับรอง 50 ทวิ"),
        };
    }
}
