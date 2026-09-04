using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments.Handlers;

/// <summary>
/// เงินเข้าแล้วสำหรับ **ใบแจ้งหนี้/ใบวางบิลที่ลูกค้าจ่ายผ่าน portal**
/// (<c>PaymentSourceKind.Document</c>)
///
/// <para>เรียก <c>IDocumentService.CreatePaymentAsync</c> ที่มีอยู่เดิม — ตัวนั้นทำครบ
/// (ล็อกแถวเอกสาร · กันจ่ายเกิน · ลง JE · ตัด AR · WHT ตามเกณฑ์ · ออกใบเสร็จ) และ
/// ผ่านการใช้งานจริงมาแล้ว · <b>หน้าที่ของ handler คือเรียกของที่มี ไม่ใช่เขียนใหม่</b></para>
///
/// <para><b>idempotent</b>: อ้างอิงการชำระใช้ id ของ intent ⇒ ถ้า webhook มาซ้ำหรือ job
/// กระทบยอดยิงซ้ำ จะเจอแถวเดิมแล้วข้าม (ไม่ใช่สร้าง Payment ใบที่สอง = เงินสดเบิ้ล + AR ติดลบ)</para>
/// </summary>
public class DocumentPaymentHandler : IPaymentCompletionHandler
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly IGatewayAccountResolver _accounts;
    private readonly ILogger<DocumentPaymentHandler> _logger;

    public DocumentPaymentHandler(AccountingDbContext db, IDocumentService docs,
        IGatewayAccountResolver accounts, ILogger<DocumentPaymentHandler> logger)
    { _db = db; _docs = docs; _accounts = accounts; _logger = logger; }

    public PaymentSourceKind SourceKind => PaymentSourceKind.Document;

    /// <summary>อ้างอิงที่ผูกการชำระกับ intent — ใช้ทั้งตอนเขียนและตอนกันซ้ำ
    /// (canonical function เดียว ห้ามเขียน format string สองที่)</summary>
    public static string PaymentReference(Guid intentId) => $"PAY-INTENT-{intentId:N}";

    public async Task HandleSucceededAsync(PaymentIntent intent, CancellationToken ct = default)
    {
        var reference = PaymentReference(intent.Id);

        // กันซ้ำก่อนทำอะไร — webhook ซ้ำเป็นเรื่องปกติของทุกเจ้า
        var already = await _db.Payments.AsNoTracking().AnyAsync(p =>
            p.CompanyId == intent.CompanyId && !p.IsDeleted && p.Reference == reference, ct);
        if (already)
        {
            _logger.LogInformation("บันทึกรับชำระของ intent {Intent} ไปแล้ว — ข้าม", intent.Id);
            return;
        }

        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == intent.SourceId && d.CompanyId == intent.CompanyId && !d.IsDeleted)
            .Select(d => new { d.Id, d.DocumentNumber, d.BalanceDue, d.Status })
            .FirstOrDefaultAsync(ct);
        if (doc == null)
        {
            // ห้ามเงียบ: เงินเข้าแล้วแต่ไม่รู้จะตัดหนี้ใบไหน
            _logger.LogError(
                "เงินเข้าแล้วแต่หาเอกสารไม่พบ — intent {Intent} เอกสาร {Doc} บริษัท {Company} "
                + "ยอด {Amount:N2} · ต้องบันทึกรับชำระด้วยมือ",
                intent.Id, intent.SourceId, intent.CompanyId, intent.Amount);
            return;
        }

        // จ่ายเกินยอดค้าง = ข้อมูลขัดกัน ห้ามเดา — CreatePaymentAsync จะปฏิเสธเองอยู่แล้ว
        // แต่ log ที่นี่บอกบริบทได้ตรงกว่า (ผู้ใช้เห็นว่าเงินเข้าจริงแต่ตัดหนี้ไม่ได้เพราะอะไร)
        if (intent.Amount > doc.BalanceDue + 0.01m)
            _logger.LogError(
                "เงินเข้า {Amount:N2} มากกว่ายอดค้างของ {DocNo} ({Balance:N2}) — intent {Intent} "
                + "อาจมีการชำระซ้ำหรือยอดเอกสารถูกแก้หลังสร้างรายการจ่าย",
                intent.Amount, doc.DocumentNumber, doc.BalanceDue, intent.Id);

        var moneyIn = await _accounts.ResolveMoneyInAccountAsync(intent, ct);

        await _docs.CreatePaymentAsync(intent.CompanyId, new Models.DTOs.Document.CreatePaymentRequest(
            DocumentId: doc.Id,
            PaymentDate: intent.ConfirmedAt ?? DateTime.UtcNow,
            Amount: intent.Amount,
            PaymentMethod: PaymentMethod.BankTransfer,
            Reference: reference,
            BankAccount: null,
            Notes: $"ชำระออนไลน์ผ่านระบบรับชำระเงิน — {doc.DocumentNumber}",
            OverridePaymentAccountId: moneyIn
        ), intent.ConfirmedBy ?? "payment-gateway");
    }
}
