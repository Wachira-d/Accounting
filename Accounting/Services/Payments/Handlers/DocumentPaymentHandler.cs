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
    /// <summary>รอบ 241: งานส่งเอกสารหลังรับชำระ (null = สร้างนอก DI)</summary>
    private readonly IPostPaymentAutomationService? _postPay;

    public DocumentPaymentHandler(AccountingDbContext db, IDocumentService docs,
        IGatewayAccountResolver accounts, ILogger<DocumentPaymentHandler> logger,
        IPostPaymentAutomationService? postPay = null)
    { _db = db; _docs = docs; _accounts = accounts; _logger = logger; _postPay = postPay; }

    public PaymentSourceKind SourceKind => PaymentSourceKind.Document;

    /// <summary>อ้างอิงที่ผูกการชำระกับ intent — ใช้ทั้งตอนเขียนและตอนกันซ้ำ
    /// (canonical function เดียว ห้ามเขียน format string สองที่)</summary>
    public static string PaymentReference(Guid intentId) => Accounting.Helpers.GatewayPaymentRecord.Reference(intentId);

    public async Task HandleSucceededAsync(PaymentIntent intent, CancellationToken ct = default)
    {
        // รอบ 232 (232-Omise-2): ด่านชั้นสอง — เงินโหมดทดสอบห้ามมีผลทางธุรกิจ (dispatcher กันชั้นแรก · ตัวตัดสินเดียว TestModePaymentPolicy)
        await Accounting.Helpers.TestModePaymentPolicy.GuardAsync(_db, intent, ct);
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
            .Select(d => new { d.Id, d.DocumentNumber, d.BalanceDue, d.Status, d.WithholdingTaxAmount, d.ContactId })
            .FirstOrDefaultAsync(ct);
        if (doc == null)
        {
            // ห้ามเงียบ: เงินเข้าแล้วแต่ไม่รู้จะตัดหนี้ใบไหน
            _logger.LogError(
                "เงินเข้าแล้วแต่หาเอกสารไม่พบ — intent {Intent} เอกสาร {Doc} บริษัท {Company} "
                + "ยอด {Amount:N2} · ต้องบันทึกรับชำระด้วยมือ",
                intent.Id, intent.SourceId, intent.CompanyId, intent.Amount);
            // รอบ 205 ทีม E: โยนแทน return เงียบ — PaymentIntentService บันทึกลงประวัติของรายการชำระ + แจ้งเจ้าของ
            throw new Accounting.Helpers.BusinessRuleException(
                Accounting.Helpers.GatewayPaymentRecord.SourceMissingMessage("เอกสาร", intent.Id, intent.Amount), Accounting.Helpers.GatewayPaymentRecord.SourceMissingRuleCode);
        }

        // จ่ายเกินยอดค้าง = ข้อมูลขัดกัน ห้ามเดา — CreatePaymentAsync จะปฏิเสธเองอยู่แล้ว
        // แต่ log ที่นี่บอกบริบทได้ตรงกว่า (ผู้ใช้เห็นว่าเงินเข้าจริงแต่ตัดหนี้ไม่ได้เพราะอะไร)
        if (intent.Amount > doc.BalanceDue + 0.01m)
            _logger.LogError(
                "เงินเข้า {Amount:N2} มากกว่ายอดค้างของ {DocNo} ({Balance:N2}) — intent {Intent} "
                + "อาจมีการชำระซ้ำหรือยอดเอกสารถูกแก้หลังสร้างรายการจ่าย",
                intent.Amount, doc.DocumentNumber, doc.BalanceDue, intent.Id);

        var moneyIn = await _accounts.ResolveMoneyInAccountAsync(intent, ct);

        // รอบ 241 (คำตัดสิน 241-3): ลิงก์ชำระเงินเก็บ "ยอดสุทธิหลังลูกค้าหัก ณ ที่จ่าย" ⇒ เงินเข้า + WHT ที่เหลือ = ยอดค้าง ⇒ บันทึก WHT ทั้งก้อน (ปิดหนี้)
        // ตัวตัดสินเดียว DocumentPayLinkPolicy.WhtForPayment · ไม่ตรง = null = ตัวบันทึกรับชำระแนะนำเอง (พฤติกรรมเดิม)
        // ตัวนับ "หักไปแล้ว" ตัวเดียวกับตัวบันทึกรับชำระ (ฝ่ายค้าน B1) · ผู้ซื้อไม่มีหน้าที่หัก ⇒ ยอดหักที่คาด 0 (ฝ่ายค้าน S3 · ตัวตัดสินเดียวกับหน้าลิงก์)
        // รอบ 244 (ฝ่ายค้าน S2): WHT ที่เหลือจากตัวคิดเดียวกับตัวบันทึกรับชำระ (ใบลดหนี้ที่มีผล + ตัวนับหักไปแล้ว — NetCap) — ตัวเดียวกับหน้าลิงก์
        var docEntity = await _db.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id && d.CompanyId == intent.CompanyId, ct);
        var whtFigures = await Accounting.Services.Implementations.DocumentService.PaymentWhtFiguresAsync(_db, intent.CompanyId, docEntity, intent.Amount, ct);
        var buyer = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == doc.ContactId && c.CompanyId == intent.CompanyId)
            .Select(c => new { c.ContactType, c.TaxId }).FirstOrDefaultAsync(ct);
        var buyerWithholds = Accounting.Helpers.DocumentPayLinkPolicy.BuyerWithholds(buyer?.ContactType, buyer?.TaxId);
        var expectedWht = Accounting.Helpers.DocumentPayLinkPolicy.ExpectedWht(whtFigures.NetCap, buyerWithholds);
        // รอบ 244 (ฝ่ายค้าน S1): ผู้ซื้อไม่มีหน้าที่หัก ⇒ ส่ง 0 ชัด ๆ — null = ตัวบันทึกรับชำระใช้ยอดแนะนำ (หัก WHT ที่ลูกค้าไม่ได้หักจริง + ออก 50 ทวิ เท็จ)
        var paymentWht = buyerWithholds == false ? 0m
            : Accounting.Helpers.DocumentPayLinkPolicy.WhtForPayment(intent.Amount, doc.BalanceDue,
                Accounting.Helpers.DocumentPayLinkPolicy.AmountsFor(doc.BalanceDue, expectedWht, 0m).Wht);

        var recorded = await _docs.CreatePaymentAsync(intent.CompanyId, new Models.DTOs.Document.CreatePaymentRequest(
            DocumentId: doc.Id,
            // รอบ 250 ฝ่ายค้าน M4: วันเงินเข้าตามหลักฐาน (สลิป — คนยืนยันระบุ/ค่าตั้งต้นวันแนบ) ก่อนวันกดยืนยัน (tax point §78/1)
            PaymentDate: intent.PaidAt ?? intent.ConfirmedAt ?? DateTime.UtcNow,
            Amount: intent.Amount,
            PaymentMethod: PaymentMethod.BankTransfer,
            Reference: reference,
            BankAccount: null,
            Notes: $"ชำระออนไลน์ผ่านระบบรับชำระเงิน — {doc.DocumentNumber}",
            OverridePaymentAccountId: moneyIn,
            WithholdingTaxAmount: paymentWht
        ), intent.ConfirmedBy ?? "payment-gateway");

        // รอบ 241: งานส่งเอกสารหลังรับชำระ (ออก/ส่งใบเสร็จ) — หลัง commit · ไม่โยน (เงินบันทึกแล้ว) · แถวเดียวต่อการชำระ (webhook ซ้ำไม่เพิ่ม)
        if (_postPay != null)
            await _postPay.EnqueueAsync(intent.CompanyId, recorded.Id, "gateway", intent.ConfirmedBy ?? "payment-gateway", ct);
    }
}
