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
    // รอบ 250 (WP-02 §2.1): งานส่งเอกสารหลังรับชำระย้ายเข้าแกน DocumentService.CreatePaymentAsync (source: Gateway) — handler ไม่เรียกเองแล้ว

    public DocumentPaymentHandler(AccountingDbContext db, IDocumentService docs,
        IGatewayAccountResolver accounts, ILogger<DocumentPaymentHandler> logger,
        Accounting.Services.Storage.IFileStorage? storage = null,
        IDocumentPayLinkService? links = null)
    { _db = db; _docs = docs; _accounts = accounts; _logger = logger; _storage = storage; _links = links; }

    /// <summary>รอบ 267 P5 (267-1-72): ตัวตัดสินชุดใบของใบวางบิล "จ่ายทั้งใบ" (null = สร้างนอก DI — ใบวางบิลล้มดังแทนการเดา)</summary>
    private readonly IDocumentPayLinkService? _links;

    /// <summary>รอบ 260-2: ที่เก็บไฟล์ — ตรวจรูป QR ของบัญชีตอนหาค่าแนะนำ "บัญชีที่แสดงให้ลูกค้าโอน" (null = นับเฉพาะพร้อมเพย์ · เทสต์)</summary>
    private readonly Accounting.Services.Storage.IFileStorage? _storage;

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
            .Select(d => new { d.Id, d.DocumentNumber, d.BalanceDue, d.Status, d.WithholdingTaxAmount, d.ContactId, d.DocumentType })
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

        // รอบ 267 P5 (267-1-9 v2 · 267-1-72): ใบวางบิล "จ่ายทั้งใบ" — ไม่มีเส้นเงินใหม่: ตัดสินชุดใบซ้ำ ณ ตอนเงินเข้า (ตัวตัดสินเดียว BillingNoteCombinedPay)
        // แล้วเรียกการรับชำระหลายใบตัวเดิม (CreateMultiDocPaymentAsync — ล็อกแถว · JE · ตัดลูกหนี้ · ใบเสร็จรายใบ) · ยอดไม่ตรงกับยอดค้างรวมตอนนี้ = ล้มดัง (ไม่เดา)
        if (doc.DocumentType == DocumentType.BillingNote)
        {
            await HandleBillingNoteAsync(intent, doc.Id, doc.DocumentNumber, reference, ct);
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
        // รอบ 260-2: สลิปโอน ⇒ บัญชีธนาคารที่เงินเข้า (ค่าที่ประทับตอนยืนยัน · ไม่มี ⇒ ค่าแนะนำ) จากตัวตัดสินเดียว Helpers/SlipReceivingAccount —
        // เดิมส่ง null ⇒ สืบทอดแหล่งเงินของใบ (ส่วนใหญ่ว่าง) ⇒ JE ลงเงินสด 111 ทั้งที่เงินเข้าธนาคาร · เงินผ่านผู้ให้บริการ (moneyIn มีค่า) ไม่ใช้ตัวนี้
        var receivingBank = moneyIn == null
            ? await Accounting.Helpers.SlipReceivingAccount.OverrideForHandlerAsync(_db, _storage?.Locator.AttachmentsRoot, intent, ct)
            : null;

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
        // รอบ 253 (WP-10 · คำตัดสินเจ้าของ §11 ข้อ 3): ลูกค้าเลือก "ไม่หัก ณ ที่จ่าย" บนหน้าชำระ (แถวหลักฐานผูกรายการนี้) ⇒ ภาษีหัก ณ ที่จ่ายของงวดนี้ = 0 ·
        // เงินเต็มยอดเกินยอดค้าง (ใบตั้งยอดสุทธิหลังหัก) ⇒ ล้มดังพร้อมป้ายฝั่งผู้ขาย (ประวัติรายการ + แจ้งเจ้าของ) ไม่เดาว่าจะลงส่วนเกินที่ไหน (คำตัดสิน 253-WP10-3)
        var noWht = await _db.NoWhtAcceptances.AsNoTracking()
            .AnyAsync(a => a.CompanyId == intent.CompanyId && a.PaymentIntentId == intent.Id, ct);
        if (noWht && Accounting.Helpers.NoWhtChoicePolicy.HandlerRefusal(intent.Amount, doc.BalanceDue, doc.DocumentNumber) is string noWhtRefusal)
            throw new Accounting.Helpers.BusinessRuleException(noWhtRefusal, Accounting.Helpers.NoWhtChoicePolicy.RuleCode, 409);
        // รอบ 244 (ฝ่ายค้าน S1): ผู้ซื้อไม่มีหน้าที่หัก ⇒ ส่ง 0 ชัด ๆ — null = ตัวบันทึกรับชำระใช้ยอดแนะนำ (หัก WHT ที่ลูกค้าไม่ได้หักจริง + ออก 50 ทวิ เท็จ)
        var paymentWht = noWht ? 0m : buyerWithholds == false ? 0m
            : Accounting.Helpers.DocumentPayLinkPolicy.WhtForPayment(intent.Amount, doc.BalanceDue,
                Accounting.Helpers.DocumentPayLinkPolicy.AmountsFor(doc.BalanceDue, expectedWht, 0m).Wht);

        await _docs.CreatePaymentAsync(intent.CompanyId, new Models.DTOs.Document.CreatePaymentRequest(
            DocumentId: doc.Id,
            // รอบ 250 ฝ่ายค้าน M4: วันเงินเข้าตามหลักฐาน (สลิป — คนยืนยันระบุ/ค่าตั้งต้นวันแนบ) ก่อนวันกดยืนยัน (tax point §78/1)
            PaymentDate: intent.PaidAt ?? intent.ConfirmedAt ?? DateTime.UtcNow,
            Amount: intent.Amount,
            PaymentMethod: PaymentMethod.BankTransfer,
            Reference: reference,
            BankAccount: null,
            Notes: $"ชำระออนไลน์ผ่านระบบรับชำระเงิน — {doc.DocumentNumber}",
            OverrideBankAccountId: receivingBank,
            OverridePaymentAccountId: moneyIn,
            WithholdingTaxAmount: paymentWht
        ), intent.ConfirmedBy ?? "payment-gateway", source: Accounting.Helpers.PaymentEntrySource.Gateway);
        // รอบ 258-3 (F-H3) → รอบ 264 P3 (C-04): การแจ้งผู้ซื้อคู่ค้าย้ายเข้าขั้นหลัง commit ของ CreatePaymentAsync (ทุกทางเข้า · กุญแจ = การรับชำระ) — handler ไม่เรียกเองแล้ว
        // รอบ 250 (WP-02 §2.1): งานส่งเอกสารหลังรับชำระเข้าคิวในแกน CreatePaymentAsync (หลัง commit · แถวเดียวต่อการชำระ — webhook ซ้ำไม่เพิ่ม)
    }

    /// <summary>
    /// รอบ 267 P5 (267-1-72): เงินเข้าสำหรับใบวางบิล "จ่ายทั้งใบ" ⇒ การรับชำระหนึ่งรายการจัดสรรให้ใบแจ้งหนี้แต่ละใบเต็มยอดค้าง ผ่าน
    /// <c>IDocumentService.CreateMultiDocPaymentAsync</c> ตัวเดิม · ภาษีหัก ณ ที่จ่ายของทุกใบ = 0 ชัด ๆ (ชุดที่ผู้ซื้อต้องหักจ่ายรวมไม่ได้ตั้งแต่ต้น — ตัวตัดสินเดียว) ·
    /// ชุดใบเปลี่ยนระหว่างลูกค้าจ่าย (ใบถูกชำระ/ยกเลิก/แก้ยอด) ⇒ โยนพร้อมเหตุ (ประวัติรายการชำระ + แจ้งเจ้าของ — ไม่ลงเงินรอจัดสรรเอง · 258-1-7)
    /// </summary>
    private async Task HandleBillingNoteAsync(PaymentIntent intent, Guid billingNoteId, string billingNoteNumber, string reference, CancellationToken ct)
    {
        var plan = _links == null ? null : await _links.BillingNotePlanAsync(intent.CompanyId, billingNoteId, ct);
        if (plan == null)
            throw new Accounting.Helpers.BusinessRuleException(
                $"เงินเข้าแล้ว {intent.Amount:N2} บาทสำหรับใบวางบิล {billingNoteNumber} แต่ระบบตัดสินชุดใบไม่ได้ — บันทึกรับชำระด้วยมือ",
                Accounting.Helpers.BillingNoteCombinedPay.RuleCode, 409);
        if (Accounting.Helpers.BillingNoteCombinedPay.SettleRefusal(intent.Amount, plan, billingNoteNumber) is string refusal)
            throw new Accounting.Helpers.BusinessRuleException(refusal, Accounting.Helpers.BillingNoteCombinedPay.RuleCode, 409);
        var moneyIn = await _accounts.ResolveMoneyInAccountAsync(intent, ct);
        var receivingBank = moneyIn == null
            ? await Accounting.Helpers.SlipReceivingAccount.OverrideForHandlerAsync(_db, _storage?.Locator.AttachmentsRoot, intent, ct)
            : null;
        await _docs.CreateMultiDocPaymentAsync(intent.CompanyId, new Models.DTOs.Document.CreatePaymentRequest(
            DocumentId: plan.Allocations[0].DocumentId,
            PaymentDate: intent.PaidAt ?? intent.ConfirmedAt ?? DateTime.UtcNow,
            Amount: intent.Amount,
            PaymentMethod: PaymentMethod.BankTransfer,
            Reference: reference,
            BankAccount: null,
            Notes: $"ชำระออนไลน์ทั้งใบวางบิล {billingNoteNumber} ผ่านระบบรับชำระเงิน",
            OverrideBankAccountId: receivingBank,
            OverridePaymentAccountId: moneyIn,
            Allocations: plan.Allocations
                .Select(a => new Models.DTOs.Document.PaymentAllocationRequest(a.DocumentId, a.Amount, WithholdingTaxAmount: 0m,
                    Note: $"ใบวางบิล {billingNoteNumber}"))
                .ToList()
        ), intent.ConfirmedBy ?? "payment-gateway");
        _logger.LogInformation("บันทึกรับชำระทั้งใบวางบิล {Bn} ({Count} ใบ · {Amount:N2}) จาก intent {Intent}",
            billingNoteNumber, plan.Allocations.Count, intent.Amount, intent.Id);
    }
}
