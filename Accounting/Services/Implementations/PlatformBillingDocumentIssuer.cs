using Accounting.Data;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ออกเอกสารค่าบริการ SaaS ผ่าน <b>tenant ของผู้ให้บริการเอง</b>
/// (ACCOUNT_STRUCTURE §6.1) — บริษัทผู้ให้บริการเป็นลูกค้าของระบบตัวเอง
///
/// <para><b>ทำไมต้องมี:</b> เดิม <see cref="SaasBillingDocumentService"/> ปั้น PDF
/// เดี่ยว ๆ พร้อมเลขรันของตัวเอง (RCPT-yyyyMM-####) ⇒ (1) รายได้ค่าบริการไม่เคยลง
/// GL ของบริษัทเรา (2) ไม่เข้ารายงานภาษีขาย/ภ.พ.30 — ผิดกฎหมายถ้าจด VAT
/// (3) ออก e-Tax ไม่ได้ (4) เลขไม่ได้อยู่ในชุด gap-free ตาม §86/4. เส้นนี้ทำให้
/// ทุกบาทที่เก็บได้กลายเป็นเอกสารจริง + JE จริง โดยไม่ต้องเขียนระบบบิลใหม่</para>
///
/// <para><b>กฎเหล็ก:</b> ห้าม throw ขึ้นไปทำให้การอนุมัติเงินพัง — ล้มเหลวคืน null
/// แล้วให้ผู้เรียกตกกลับไปโหมด PDF เดิม (เงินเข้าแล้วต้องบันทึกได้เสมอ)</para></summary>
public interface IPlatformBillingDocumentIssuer
{
    /// <summary>ออก "ใบกำกับภาษี/ใบเสร็จรับเงิน" (ขายเงินสด) ใน tenant ผู้ให้บริการ
    /// สำหรับเงินค่าบริการที่รับแล้ว — คืน null ถ้ายังไม่ได้ตั้งค่า tenant/ทำไม่สำเร็จ</summary>
    Task<PlatformDocResult?> IssuePaidReceiptAsync(SubscriptionPayment payment, Company buyer);

    /// <summary>ออก "ใบแจ้งหนี้" ต่ออายุล่วงหน้า (ยังไม่รับเงิน) ใน tenant ผู้ให้บริการ</summary>
    Task<PlatformDocResult?> IssueRenewalInvoiceAsync(Guid buyerCompanyId, string description,
        decimal amountNet, DateTime issueDate, DateTime dueDate, string reference);

    /// <summary>รอบ 219 เฟส 2 (G-10 · คำตัดสินข้อ 180): บันทึก "รับชำระใบแจ้งหนี้เดิม" ใน tenant ผู้ให้บริการ (Dr เงินสด/ธนาคาร / Cr ลูกหนี้ ผ่าน
    /// <c>IDocumentService.CreatePaymentAsync</c> ตัวเดียวกับเส้นอื่น) — <b>ไม่ออกขายสดใบใหม่</b> (เดิมรับเงินค่าต่ออายุ = ใบแจ้งหนี้ + ใบขายสด ⇒ รายได้ซ้ำ) ·
    /// idempotent ด้วย <paramref name="reference"/> (ชำระแล้ว = คืนผลเดิม) · คืน null เมื่อยังไม่ตั้ง tenant/ใบไม่ใช่ของ tenant/ล้ม (ไม่โยน)
    /// <para>ฝ่ายค้าน S1: ออก<b>ใบเสร็จรับเงินจริง</b>คู่การรับชำระ (<c>IssueReceiptDocument: true</c> — ใบแจ้งหนี้บริการรับครบงวดเดียว ⇒ ใบเสร็จถือ VAT
    /// = ใบกำกับภาษี/ใบเสร็จ ณ วันรับเงิน §78/1) แล้วคืนเลขใบเสร็จ + ธงใบกำกับ (เดิมคืนเลขใบแจ้งหนี้ ⇒ ผู้เช่า "ดาวน์โหลดใบเสร็จ" ได้ใบแจ้งหนี้) ·
    /// ฝ่ายค้าน S3: รับไม่เกินยอดค้างของใบ (ส่วนเกิน = <c>Excess</c> ให้แอดมินจัดการ · ไม่ตัดเกินหนี้)</para></summary>
    Task<PlatformSettleResult?> SettleInvoiceAsync(Guid invoiceDocumentId, decimal amountReceived, decimal withholdingTax,
        DateTime paymentDate, string reference, string notes, Guid? subscriptionPaymentId = null);

    /// <summary>รอบ 219 เฟส 2 (S9): ยกเลิกใบแจ้งหนี้ต่ออายุที่ไม่มีการชำระเลย ผ่าน <c>IDocumentService.VoidDocumentAsync</c> (เก็บใบ + JE กลับรายการ ·
    /// ห้ามแก้ใบย้อนหลัง §86/4) · มีการชำระแล้ว ⇒ NeedsCreditNote (ไม่แตะ) · มีรายการชำระที่ผูกใบค้างอยู่ ⇒ PendingPayment (ไม่แตะ · ฝ่ายค้าน B2) · ไม่โยน</summary>
    Task<StaleRenewalOutcome> VoidStaleRenewalInvoiceAsync(Guid invoiceDocumentId);

    /// <summary>ออก "ใบแจ้งหนี้ค่าใช้งานตามจริง" หลายบรรทัดในใบเดียว
    /// (ค่าเหมา add-on · เอกสารเกินโควตา · การเข้าพัก · SMS ฯลฯ ของงวดนั้น)
    ///
    /// <para>ทำไมต้องหลายบรรทัด: ถ้ายุบเป็นบรรทัดเดียว "ค่าบริการเดือน ก.ย." ลูกค้า
    /// จะไม่มีทางรู้ว่ายอดมาจากอะไร แล้วทุกครั้งที่ยอดขยับต้องโทรถาม — ใบกำกับคือ
    /// คำอธิบายค่าใช้จ่าย ไม่ใช่แค่ตัวเลขรวม</para></summary>
    Task<PlatformDocResult?> IssueUsageInvoiceAsync(Guid buyerCompanyId, IReadOnlyList<PlatformInvoiceLine> lines,
        DateTime issueDate, DateTime dueDate, string reference, string? notes = null);

    /// <summary>tenant ผู้ให้บริการถูกตั้งค่าไว้แล้วหรือยัง (ใช้เลือกเส้นทาง)</summary>
    Task<bool> IsEnabledAsync();

    /// <summary>รอบ 227 (PLATFORM_OMISE_AUDIT C-3 · G-9 เดิม): ออก "ใบกำกับภาษี/ใบเสร็จรับเงิน" (ขายเงินสด) ของ<b>ค่าส่วนเสริมที่จ่ายล่วงหน้า</b> ใน tenant ผู้ให้บริการ —
    /// เดิมจ่ายแล้ว (ออนไลน์/สลิปที่แอดมินอนุมัติ) ไม่มีเอกสารเลย ⇒ รายได้ไม่ลง GL ของ NextAcc · ไม่เข้ารายงานภาษีขาย (§86/4 · จุดรับเงินบริการ §78/1) ·
    /// ยอด <paramref name="amountReceived"/> = เงินที่ได้รับ (ราคาตั้งรวม VAT หรือไม่ ตามค่าตั้ง <c>PlatformPriceIncludesVat</c> เดียวกับเส้นอื่น) ·
    /// <b>idempotent</b> ด้วย <paramref name="reference"/> (มีใบที่อ้างอิงนี้แล้ว = คืนใบเดิม) · ขาเงินเข้า = บัญชีพักผู้ให้บริการรับชำระเมื่อจ่ายผ่าน gateway
    /// (<paramref name="gatewayIntentId"/>) ไม่ใช่ธนาคาร · คืน null เมื่อยังไม่ตั้ง tenant/ล้ม (ไม่โยน — เงินเข้าแล้วต้องบันทึกสิทธิ์ได้เสมอ)</summary>
    /// <param name="discountReceived">รอบ 230: ส่วนลดคูปอง (รวม VAT) — &gt; 0 ⇒ บรรทัดราคาเต็ม + ส่วนลดของบรรทัดก่อน VAT (§86/4) · ฐานหลังส่วนลด + VAT = เงินที่รับ</param>
    Task<PlatformDocResult?> IssueAddOnReceiptAsync(Guid buyerCompanyId, string description, decimal amountReceived, DateTime paidAt,
        string reference, Guid? gatewayIntentId, decimal discountReceived = 0m);

    /// <summary>รอบ 228 (PLATFORM_OMISE_AUDIT §5.5 · §86/10): ออก<b>ใบลดหนี้</b> (อนุมัติทันที) อ้างใบกำกับ/ใบเสร็จเดิมของรายการชำระค่าบริการออนไลน์ที่แอดมินคืนเงิน —
    /// ใน tenant ผู้ให้บริการ (ตัวเดียวกับ intent) · ยอด = เงินที่คืน (รวม VAT) · ด่าน Σ ใบลดหนี้ ≤ ใบเดิม (<c>Helpers/PlatformRefundCreditNote</c>) ·
    /// idempotent ด้วยเลขคืนเงิน · คืนผล (ไม่โยน — เงินออกไปแล้ว ผู้เรียกต้องบอกผู้ใช้ว่าออกใบไม่สำเร็จพร้อมเหตุ)</summary>
    /// <param name="counterAccountId">ฝ่ายค้าน B1: ขา counter ของใบลดหนี้ (ช่องทางเงินของใบ) — บัญชีพักผู้ให้บริการรับชำระ (ปกติ: เงินคืนออกจากยอดที่ผู้ให้บริการถือ) ·
    /// ลูกหนี้ (ออกใหม่หลังลงใบสำคัญสำรอง Dr ลูกหนี้/Cr บัญชีพักแล้ว) — ห้ามปล่อยให้ตกเงินสดตั้งต้น (เงินสดหายสองครั้ง)</param>
    Task<PlatformRefundCreditResult> IssueRefundCreditNoteAsync(Guid intentId, decimal refundAmount, string refundRef, string reason, Guid counterAccountId);
}

/// <summary>รอบ 228: ผลออกใบลดหนี้ของการคืนเงินค่าบริการ — <c>Ok=false</c> ⇒ <c>Problem</c> = เหตุ + ทางไปต่อ (ไทย)</summary>
public sealed record PlatformRefundCreditResult(bool Ok, Guid? CreditNoteId, string? CreditNoteNumber, string? Problem);

/// <summary>ผลการจัดการใบต่ออายุที่ล้าสมัย (S9) · ฝ่ายค้าน B2: <c>PendingPayment</c> = มีรายการแจ้งชำระ/ชำระออนไลน์ที่ผูกใบนี้ค้างอยู่ ⇒ ไม่ยกเลิก
/// (ยกเลิกแล้วเงินที่ตามมาจะไม่มีใบให้ตัด · อนุมัติแล้วต่ออายุซ้อน)</summary>
public enum StaleRenewalOutcome { Voided = 1, NeedsCreditNote = 2, NotFound = 3, Failed = 4, PendingPayment = 5 }

/// <summary>ผลตัดชำระใบแจ้งหนี้ค่าบริการ (ฝ่ายค้าน S1/S3) — <paramref name="ReceiptDocumentId"/> null = รับชำระแล้วแต่ใบเสร็จออกไม่สำเร็จ (ต้องออกด้วยมือ) ·
/// <paramref name="Applied"/> = ยอดเงินที่ตัดหนี้จริง · <paramref name="Excess"/> = ส่วนที่เกินยอดค้าง (ไม่ตัด · แอดมินจัดการ)</summary>
public sealed record PlatformSettleResult(Guid InvoiceId, string InvoiceNumber, Guid? ReceiptDocumentId, string? ReceiptNumber,
    bool ReceiptIsTaxInvoice, decimal Applied, decimal Excess);

public sealed record PlatformDocResult(Guid DocumentId, string DocumentNumber, bool IsTaxInvoice);

/// <summary>1 บรรทัดบนใบแจ้งหนี้ค่าใช้งาน — <paramref name="AmountNet"/> คือยอดรวม
/// ของบรรทัดนั้นตามที่เก็บใน <c>UsageEvent.ChargedAmount</c> (คิดมาแล้ว ไม่คิดใหม่:
/// ราคาถูก snapshot ไว้ตอนเกิดเหตุการณ์ การคำนวณซ้ำจะได้คนละยอดเมื่อราคาเปลี่ยน)</summary>
public sealed record PlatformInvoiceLine(string Description, decimal AmountNet, string Unit = "รายการ", decimal Quantity = 1m);

public class PlatformBillingDocumentIssuer : IPlatformBillingDocumentIssuer
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<PlatformBillingDocumentIssuer> _logger;
    /// <summary>resolve <see cref="IDocumentService"/> ตอนเรียกใช้ ไม่ใช่ตอน ctor
    /// — inject ตรง ๆ จะเกิดวงกลม (DI ปฏิเสธตั้งแต่ตอน start):
    /// <c>IDocumentService → ISubscriptionService → ISaasBillingDocumentService
    /// → IPlatformBillingDocumentIssuer → IDocumentService</c>
    /// (pattern เดียวกับ <see cref="ApprovalService"/> ที่เจอวงกลมแบบเดียวกัน).
    /// ทุกตัวเป็น Scoped และ resolve จาก provider ของ scope เดิม ⇒ ได้ instance
    /// เดียวกับที่ request นี้ใช้อยู่ (DbContext/transaction เดียวกัน)</summary>
    private readonly IServiceProvider _services;

    /// <summary>รอบ 227 (C-2): ผังขาเงินเข้าของเงินที่รับผ่านผู้ให้บริการรับชำระ (บัญชีพัก 11340 ไม่ใช่ธนาคาร) — ตัวเดียวกับเส้นของร้าน · null (เทสต์เดิม) = ใช้ผังเงินสดตามเดิม</summary>
    private readonly Accounting.Services.Payments.IGatewayAccountResolver? _gatewayAccounts;

    public PlatformBillingDocumentIssuer(AccountingDbContext db, IServiceProvider services,
        ILogger<PlatformBillingDocumentIssuer> logger, Accounting.Services.Payments.IGatewayAccountResolver? gatewayAccounts = null)
    { _db = db; _services = services; _logger = logger; _gatewayAccounts = gatewayAccounts; }

    private IDocumentService Documents =>
        (IDocumentService)_services.GetService(typeof(IDocumentService))!;

    private const string Actor = "platform-billing";

    public async Task<bool> IsEnabledAsync()
    {
        var s = await LoadSettingsAsync();
        return s?.PlatformCompanyId != null;
    }

    private Task<SiteSettings?> LoadSettingsAsync() => _db.SiteSettings.AsNoTracking()
        .OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();

    /// <summary>PP36_REVIEW P2-4 — ใบร่างใน tenant ผู้ให้บริการเกิดแล้วแต่อนุมัติล้ม ⇒ หมายเหตุบนใบ (สถานะจริง · ตัวเดียวของทุกทางเข้า) แทน log อย่างเดียว ·
    /// ล้มก่อนสร้างใบ (draft = null) ⇒ ไม่มีใบให้บอก (log ข้างบนพอ) · ตัวบันทึกไม่โยน (RecordAutoApproveFailureAsync จับเอง)</summary>
    private async Task NoteDraftFailureAsync((Guid TenantId, Guid DocumentId)? draft, string channel, Exception ex)
    {
        if (draft is { } d)
            await Documents.RecordAutoApproveFailureAsync(d.TenantId, d.DocumentId, channel, ex);
    }

    public async Task<PlatformDocResult?> IssuePaidReceiptAsync(SubscriptionPayment payment, Company buyer)
    {
        (Guid TenantId, Guid DocumentId)? draft = null;
        try
        {
            var (settings, tenantId) = await ResolveTenantAsync();
            if (settings == null || tenantId == null) return null;

            var contactId = await EnsureContactAsync(tenantId.Value, buyer);
            if (contactId == null) return null;

            // ยอดที่ได้รับจริง = payment.Amount (สุทธิหลังถูกหักภาษี ณ ที่จ่าย)
            // ยอดตามใบกำกับ = สุทธิ + WHT ที่ลูกค้าหักไว้ — ต้องออกใบเต็มยอด
            // ไม่งั้นรายได้ต่ำไปและเครดิตภาษี 3% ที่ควรได้คืนหายไป
            var net = payment.Amount;
            var wht = payment.WithholdingTaxAmount;
            var grossBeforeWht = decimal.Round(net + wht, 2, MidpointRounding.AwayFromZero);
            if (grossBeforeWht <= 0m) return null;   // ยกเว้นค่าบริการ (0 บาท) ไม่ต้องออกใบ

            var isVat = Accounting.Helpers.PlanPurchasePreview.IssuesTaxInvoice(settings.PlatformIsVatRegistered, settings.PlatformSellerTaxId);   // รอบ 233 N3: ตัวตัดสินเดียวกับ preview
            // ฐานก่อน VAT: ถ้าราคาที่ตั้งไว้รวม VAT แล้ว ต้องถอดออกก่อน เพราะบรรทัด
            // เอกสารเก็บราคา ex-VAT (PricesIncludeVat ของ document ใช้เส้นทางคนละแบบ)
            // ฝ่ายค้านรอบ 227 S2: เงินที่รับแล้วรวม VAT เสมอ + VAT = ยอด − ฐาน (ไม่มีเศษ 0.01 · ไม่ออกใบสูงกว่าเงินที่รับเมื่อตั้ง "ราคาไม่รวม VAT")
            // รอบ 230 (คำตัดสินข้อ 230-Q2 · §86/4): คูปอง ⇒ บรรทัดราคาเต็ม + ส่วนลดของบรรทัด (หักก่อน VAT) · ฐานหลังส่วนลด + VAT = เงินที่รับพอดี
            // รอบ 233 (คำตัดสิน 233-4): เปลี่ยนรอบบิล ⇒ เครดิตรอบเดิมเป็นส่วนลดของบรรทัด (ก่อน VAT) เหมือนคูปอง — บรรทัด = ค่ารอบใหม่เต็ม (เดิม: บรรทัดเป็นยอดสุทธิ เครดิตอยู่แค่ในคำอธิบาย)
            var (fullBase, discountBase, baseAmount, vatAmount) = Accounting.Helpers.PlatformCouponRule.SplitDiscounted(
                grossBeforeWht, Accounting.Helpers.PlanPurchasePreview.InvoiceDiscountGross(payment.CouponDiscountAmount, payment.IsCycleChange, payment.CycleChangeCredit), isVat);
            // อัตรา WHT คำนวณกลับจากยอดจริง — ให้ยอดหักตรงกับที่ลูกค้าหักเป๊ะ
            // (กันเศษสตางค์จากการปัดอัตรา 3.00%)
            var whtRate = wht > 0m && baseAmount > 0m
                ? decimal.Round(wht / baseAmount * 100m, 4, MidpointRounding.AwayFromZero) : 0m;

            var desc = BuildServiceDescription(payment, buyer);
            var req = new CreateDocumentRequest(
                DocumentType: isVat ? DocumentType.TaxInvoice : DocumentType.Receipt,
                DocumentDate: payment.PaymentDate == default ? DateTime.UtcNow : payment.PaymentDate,
                DueDate: null,
                ContactId: contactId.Value,
                Reference: payment.PaymentNumber,
                Notes: $"ค่าบริการระบบบัญชีออนไลน์ · {payment.PaymentNumber}"
                     + (string.IsNullOrWhiteSpace(payment.TransferReference) ? "" : $" · อ้างอิงโอน {payment.TransferReference}"),
                Lines: new List<DocumentLineRequest>
                {
                    new(Description: desc, Quantity: 1m, Unit: "รายการ",
                        UnitPrice: fullBase, DiscountPercent: 0m,
                        VatRate: isVat ? 7m : 0m, WithholdingTaxRate: whtRate,
                        AccountId: null,
                        AccountCode: string.IsNullOrWhiteSpace(settings.PlatformRevenueAccountCode)
                            ? "41000" : settings.PlatformRevenueAccountCode,
                        DiscountAmount: discountBase > 0m ? discountBase : null,
                        VatAmountOverride: isVat ? vatAmount : null)
                },
                // เงินรับแล้ว → ออกใบเดียวจบแบบขายเงินสด (Dr เงินสด/ธนาคาร ไม่ตั้งลูกหนี้)
                // ใบกำกับ = T03 หัว "ใบเสร็จรับเงิน/ใบกำกับภาษี". Receipt ธรรมดา
                // (ไม่จด VAT) ก็ลง Dr เงินสด / Cr รายได้ อยู่แล้ว
                IssuedAsCashReceipt: isVat ? true : null,
                // รอบ 227 (C-2): จ่ายผ่าน gateway ⇒ Dr บัญชีพักผู้ให้บริการรับชำระ (ล้างตอนบันทึกรอบโอน) · สลิป ⇒ ผังธนาคารที่ตั้ง (เดิม: ธนาคารเสมอ)
                PaymentAccountId: await MoneyInAccountIdAsync(tenantId.Value, settings,
                    Accounting.Models.Enums.PaymentSourceKind.SubscriptionPayment, payment.Id, null));

            var created = await Documents.CreateDocumentAsync(tenantId.Value, req, Actor);
            draft = (tenantId.Value, created.Id);   // P2-4: อนุมัติล้มต้องลงหมายเหตุบนใบร่างที่เกิดแล้ว
            // ฝ่ายค้านรอบ 201 รอบสาม P2-6: ไม่มีคนเห็นคำเตือนในเส้นนี้ ⇒ SystemWorkflow — ผ่านเหมือนเดิม (ไม่หยุดการออกเอกสาร) แต่ร่องรอยบอกตามจริงว่าไม่ใช่คนรับทราบ (เดิม acknowledgeWarnings: true = ประทับ AcknowledgedByPerson)
            var approved = await Documents.ApproveDocumentAsync(tenantId.Value, created.Id, Actor,
                Accounting.Helpers.ApprovalAckSource.SystemWorkflow, withAiHints: false);
            _logger.LogInformation(
                "ออกเอกสารค่าบริการใน tenant ผู้ให้บริการ {DocNo} (payment {PaymentNo} · ฐาน {Base} · VAT {Vat} · WHT {Wht})",
                approved.DocumentNumber, payment.PaymentNumber, baseAmount, isVat, wht);
            return new PlatformDocResult(approved.Id, approved.DocumentNumber, isVat);
        }
        catch (Exception ex)
        {
            // เงินเข้าแล้วต้องบันทึกได้เสมอ — ล้มตรงนี้ให้ตกกลับไปโหมด PDF เดิม
            _logger.LogError(ex, "ออกเอกสารค่าบริการผ่าน tenant ไม่สำเร็จ (payment {PaymentId}) — ใช้โหมด PDF เดิมแทน",
                payment.Id);
            await NoteDraftFailureAsync(draft, "ออกใบเสร็จค่าบริการแพลตฟอร์ม", ex);
            return null;
        }
    }

    public async Task<PlatformDocResult?> IssueRenewalInvoiceAsync(Guid buyerCompanyId, string description,
        decimal amountNet, DateTime issueDate, DateTime dueDate, string reference)
    {
        (Guid TenantId, Guid DocumentId)? draft = null;
        try
        {
            var (settings, tenantId) = await ResolveTenantAsync();
            if (settings == null || tenantId == null || amountNet <= 0m) return null;

            var buyer = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == buyerCompanyId);
            if (buyer == null) return null;
            var contactId = await EnsureContactAsync(tenantId.Value, buyer);
            if (contactId == null) return null;

            var isVat = Accounting.Helpers.PlanPurchasePreview.IssuesTaxInvoice(settings.PlatformIsVatRegistered, settings.PlatformSellerTaxId);   // รอบ 233 N3: ตัวตัดสินเดียวกับ preview
            var baseAmount = isVat && settings.PlatformPriceIncludesVat
                ? decimal.Round(amountNet * 100m / 107m, 2, MidpointRounding.AwayFromZero)
                : amountNet;

            // ใบแจ้งหนี้ = ยังไม่รับเงิน ⇒ ไม่ระบุ WHT (ลูกค้าหักตอนจ่ายจริง
            // ระบบจะบันทึกตอนออกใบเสร็จ) และไม่ใช่ IssuedAsCashReceipt
            var req = new CreateDocumentRequest(
                DocumentType: DocumentType.Invoice,
                DocumentDate: issueDate,
                DueDate: dueDate,
                ContactId: contactId.Value,
                Reference: reference,
                Notes: "ใบแจ้งหนี้ค่าบริการต่ออายุระบบบัญชีออนไลน์",
                Lines: new List<DocumentLineRequest>
                {
                    new(Description: description, Quantity: 1m, Unit: "รายการ",
                        UnitPrice: baseAmount, DiscountPercent: 0m,
                        VatRate: isVat ? 7m : 0m, WithholdingTaxRate: 0m,
                        AccountId: null,
                        AccountCode: string.IsNullOrWhiteSpace(settings.PlatformRevenueAccountCode)
                            ? "41000" : settings.PlatformRevenueAccountCode)
                });

            var created = await Documents.CreateDocumentAsync(tenantId.Value, req, Actor);
            draft = (tenantId.Value, created.Id);   // P2-4: อนุมัติล้มต้องลงหมายเหตุบนใบร่างที่เกิดแล้ว
            // ฝ่ายค้านรอบ 201 รอบสาม P2-6: ไม่มีคนเห็นคำเตือนในเส้นนี้ ⇒ SystemWorkflow — ผ่านเหมือนเดิม (ไม่หยุดการออกเอกสาร) แต่ร่องรอยบอกตามจริงว่าไม่ใช่คนรับทราบ (เดิม acknowledgeWarnings: true = ประทับ AcknowledgedByPerson)
            var approved = await Documents.ApproveDocumentAsync(tenantId.Value, created.Id, Actor,
                Accounting.Helpers.ApprovalAckSource.SystemWorkflow, withAiHints: false);
            return new PlatformDocResult(approved.Id, approved.DocumentNumber, isVat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ออกใบแจ้งหนี้ต่ออายุผ่าน tenant ไม่สำเร็จ (company {CompanyId})", buyerCompanyId);
            await NoteDraftFailureAsync(draft, "ออกใบแจ้งหนี้ต่ออายุแพลตฟอร์ม", ex);
            return null;
        }
    }

    public async Task<PlatformSettleResult?> SettleInvoiceAsync(Guid invoiceDocumentId, decimal amountReceived, decimal withholdingTax,
        DateTime paymentDate, string reference, string notes, Guid? subscriptionPaymentId = null)
    {
        try
        {
            var (settings, tenantId) = await ResolveTenantAsync();
            if (settings == null || tenantId == null || amountReceived <= 0m) return null;
            var doc = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == invoiceDocumentId && d.CompanyId == tenantId.Value && !d.IsDeleted)
                .Select(d => new { d.Id, d.DocumentNumber, d.BalanceDue, d.Status })
                .FirstOrDefaultAsync();
            if (doc == null) return null;
            // กันซ้ำ: อนุมัติซ้ำ/เรียกซ้ำหลังล้มกลางทาง ⇒ ไม่บันทึกรับเงินสองครั้ง (และหาใบเสร็จของการรับครั้งเดิม)
            var existing = await _db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == tenantId.Value && !p.IsDeleted && p.Reference == reference)
                .Select(p => new { p.Amount, p.ReceiptDocumentId })
                .FirstOrDefaultAsync();
            decimal applied, excess;
            Guid? receiptId;
            if (existing != null)
            {
                applied = existing.Amount;
                excess = Math.Max(0m, amountReceived - existing.Amount);
                receiptId = existing.ReceiptDocumentId;
            }
            else
            {
                (applied, var whtApplied, excess) = Accounting.Helpers.PlatformInvoiceRules.CapToBalance(amountReceived, withholdingTax,
                    doc.Status == DocumentStatus.Voided ? 0m : doc.BalanceDue);
                receiptId = null;
                if (applied > 0m || whtApplied > 0m)
                {
                    var res = await Documents.CreatePaymentAsync(tenantId.Value, new CreatePaymentRequest(
                        DocumentId: doc.Id,
                        PaymentDate: paymentDate == default ? DateTime.UtcNow : paymentDate,
                        Amount: applied,
                        PaymentMethod: PaymentMethod.BankTransfer,
                        Reference: reference,
                        BankAccount: null,
                        Notes: notes,
                        // รอบ 227 (C-2): รายการชำระที่จ่ายผ่าน gateway ⇒ บัญชีพักผู้ให้บริการรับชำระ (ไม่ใช่ธนาคาร)
                        OverridePaymentAccountId: subscriptionPaymentId is Guid spId
                            ? await MoneyInAccountIdAsync(tenantId.Value, settings, Accounting.Models.Enums.PaymentSourceKind.SubscriptionPayment, spId, null)
                            : await ResolveCashAccountIdAsync(tenantId.Value, settings),
                        WithholdingTaxAmount: whtApplied > 0m ? whtApplied : null,
                        IssueReceiptDocument: true), Actor);
                    receiptId = res.ReceiptDocumentId;
                }
            }
            var receipt = receiptId is Guid rid
                ? await _db.Documents.AsNoTracking().Where(d => d.Id == rid && d.CompanyId == tenantId.Value)
                    .Select(d => new { d.Id, d.DocumentNumber, d.IsTaxInvoiceByLaw }).FirstOrDefaultAsync()
                : null;
            _logger.LogInformation("ตัดชำระใบแจ้งหนี้ค่าบริการ {DocNo} ({Amount:N2} · ตัดจริง {Applied:N2} · เกิน {Excess:N2} · ใบเสร็จ {Receipt} · อ้างอิง {Ref} · ซ้ำ={Dup})",
                doc.DocumentNumber, amountReceived, applied, excess, receipt?.DocumentNumber ?? "-", reference, existing != null);
            return new PlatformSettleResult(doc.Id, doc.DocumentNumber, receipt?.Id, receipt?.DocumentNumber,
                receipt?.IsTaxInvoiceByLaw ?? false, applied, excess);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ตัดชำระใบแจ้งหนี้ค่าบริการไม่สำเร็จ (ใบ {DocId} · อ้างอิง {Ref})", invoiceDocumentId, reference);
            return null;
        }
    }

    public async Task<StaleRenewalOutcome> VoidStaleRenewalInvoiceAsync(Guid invoiceDocumentId)
    {
        try
        {
            var (_, tenantId) = await ResolveTenantAsync();
            if (tenantId == null) return StaleRenewalOutcome.NotFound;
            var doc = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == invoiceDocumentId && d.CompanyId == tenantId.Value && !d.IsDeleted)
                .Select(d => new { d.Id, d.Status, d.PaidAmount, d.BalanceDue, d.TotalAmount })
                .FirstOrDefaultAsync();
            if (doc == null) return StaleRenewalOutcome.NotFound;
            if (doc.Status == DocumentStatus.Voided) return StaleRenewalOutcome.Voided;
            if (doc.PaidAmount > 0m || doc.BalanceDue < doc.TotalAmount - Accounting.Helpers.PlatformInvoiceRules.Epsilon)
                return StaleRenewalOutcome.NeedsCreditNote;
            // ฝ่ายค้าน B2: มีรายการชำระที่ผูกใบนี้ค้างอยู่ (สลิปรอตรวจ · รอเงินเข้าออนไลน์) ⇒ ไม่ยกเลิก — เงินที่ตามมาต้องมีใบให้ตัด ·
            // การอนุมัติรายการนั้นภายหลังถูกพักให้แอดมิน (ใบต่ออายุที่ถูกแทนที่ — ReviewPaymentAsync) ไม่ต่ออายุซ้อน
            if (await _db.SubscriptionPayments.AsNoTracking().AnyAsync(p => p.PlatformInvoiceDocumentId == doc.Id
                    && (p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview)))
                return StaleRenewalOutcome.PendingPayment;
            await Documents.VoidDocumentAsync(tenantId.Value, doc.Id);
            return StaleRenewalOutcome.Voided;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ยกเลิกใบต่ออายุที่ล้าสมัยไม่สำเร็จ (ใบ {DocId})", invoiceDocumentId);
            return StaleRenewalOutcome.Failed;
        }
    }

    public async Task<PlatformDocResult?> IssueUsageInvoiceAsync(Guid buyerCompanyId,
        IReadOnlyList<PlatformInvoiceLine> lines, DateTime issueDate, DateTime dueDate,
        string reference, string? notes = null)
    {
        (Guid TenantId, Guid DocumentId)? draft = null;
        try
        {
            var (settings, tenantId) = await ResolveTenantAsync();
            if (settings == null || tenantId == null) return null;

            // บรรทัดยอด 0 ถูกตัดทิ้ง (โควตาฟรีกลืนไปแล้ว) — แต่ถ้าตัดจนไม่เหลือ
            // แปลว่าไม่มีอะไรต้องเก็บเงิน **ไม่ใช่**ออกใบเปล่า (§86/4 ห้ามใบไม่มีบรรทัด)
            var billable = lines.Where(l => l.AmountNet > 0m).ToList();
            if (billable.Count == 0) return null;

            var buyer = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == buyerCompanyId);
            if (buyer == null) return null;
            var contactId = await EnsureContactAsync(tenantId.Value, buyer);
            if (contactId == null) return null;

            var isVat = Accounting.Helpers.PlanPurchasePreview.IssuesTaxInvoice(settings.PlatformIsVatRegistered, settings.PlatformSellerTaxId);   // รอบ 233 N3: ตัวตัดสินเดียวกับ preview
            var revenueCode = string.IsNullOrWhiteSpace(settings.PlatformRevenueAccountCode)
                ? "41000" : settings.PlatformRevenueAccountCode;

            var docLines = billable.Select(l =>
            {
                // ถอด VAT ต่อบรรทัด — ปัดที่ระดับบรรทัดเหมือนเส้นทางอื่นของไฟล์นี้
                // (ถอดจากยอดรวมแล้วเฉลี่ยกลับ จะได้เศษไม่ตรงกับที่แสดงในแต่ละบรรทัด)
                // ฝ่ายค้านรอบ 229 S9: สูตรบรรทัดตัวเดียว (Helpers/PlatformInvoiceLineMath) — เทสต์ยอดใบต่ออายุหลายบรรทัดเทียบยอดที่คิดได้
                var qty = l.Quantity > 0m ? l.Quantity : 1m;
                var math = Accounting.Helpers.PlatformInvoiceLineMath.Line(l.AmountNet, qty, isVat, settings.PlatformPriceIncludesVat);
                return new DocumentLineRequest(
                    Description: l.Description,
                    Quantity: qty,
                    Unit: l.Unit,
                    UnitPrice: math.UnitPrice,
                    DiscountPercent: 0m,
                    VatRate: isVat ? 7m : 0m,
                    WithholdingTaxRate: 0m,
                    AccountId: null,
                    AccountCode: revenueCode);
            }).ToList();

            var req = new CreateDocumentRequest(
                DocumentType: DocumentType.Invoice,
                DocumentDate: issueDate,
                DueDate: dueDate,
                ContactId: contactId.Value,
                Reference: reference,
                Notes: notes ?? "ใบแจ้งหนี้ค่าใช้งานตามจริงของรอบบิล",
                Lines: docLines);

            var created = await Documents.CreateDocumentAsync(tenantId.Value, req, Actor);
            draft = (tenantId.Value, created.Id);   // P2-4: อนุมัติล้มต้องลงหมายเหตุบนใบร่างที่เกิดแล้ว
            // ฝ่ายค้านรอบ 201 รอบสาม P2-6: ไม่มีคนเห็นคำเตือนในเส้นนี้ ⇒ SystemWorkflow — ผ่านเหมือนเดิม (ไม่หยุดการออกเอกสาร) แต่ร่องรอยบอกตามจริงว่าไม่ใช่คนรับทราบ (เดิม acknowledgeWarnings: true = ประทับ AcknowledgedByPerson)
            var approved = await Documents.ApproveDocumentAsync(tenantId.Value, created.Id, Actor,
                Accounting.Helpers.ApprovalAckSource.SystemWorkflow, withAiHints: false);
            _logger.LogInformation("ออกใบแจ้งหนี้ค่าใช้งาน {DocNo} ({Lines} บรรทัด · อ้างอิง {Ref})",
                approved.DocumentNumber, docLines.Count, reference);
            return new PlatformDocResult(approved.Id, approved.DocumentNumber, isVat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ออกใบแจ้งหนี้ค่าใช้งานไม่สำเร็จ (company {CompanyId} · อ้างอิง {Ref})",
                buyerCompanyId, reference);
            await NoteDraftFailureAsync(draft, "ออกใบแจ้งหนี้ค่าใช้งานแพลตฟอร์ม", ex);
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────

    private async Task<(SiteSettings? Settings, Guid? TenantId)> ResolveTenantAsync()
    {
        var s = await LoadSettingsAsync();
        if (s?.PlatformCompanyId == null) return (s, null);
        var alive = await _db.Companies.AsNoTracking()
            .AnyAsync(c => c.Id == s.PlatformCompanyId.Value && !c.IsDeleted);
        if (!alive)
        {
            _logger.LogWarning("tenant ผู้ให้บริการที่ตั้งไว้ ({Id}) ไม่มีอยู่/ถูกลบ — ข้ามการออกเอกสารจริง",
                s.PlatformCompanyId);
            return (s, null);
        }
        return (s, s.PlatformCompanyId);
    }

    /// <summary>หา/สร้าง Contact ของบริษัทลูกค้าในผังผู้ติดต่อของ tenant ผู้ให้บริการ
    ///
    /// <para>จับคู่ด้วย <b>เลขผู้เสียภาษี</b> ก่อน (ตัวตนตามกฎหมาย) แล้วค่อยชื่อ —
    /// กันสร้างผู้ติดต่อซ้ำทุกครั้งที่ลูกค้าจ่ายเงิน ซึ่งจะทำให้รายงานลูกหนี้/
    /// ยอดขายรายลูกค้าของเราเองแตกเป็นหลายราย</para></summary>
    private async Task<Guid?> EnsureContactAsync(Guid tenantId, Company buyer)
    {
        // ฝ่ายค้าน C-8: บริษัทที่สมัครใหม่มี TaxId = "-" (AuthService) — ค่าที่ไม่มีตัวเลขเลย = ไม่มีเลข ไม่ใช่กุญแจ ·
        // เดิม "-" ถูกเทียบตรงตัว ⇒ ลูกค้ารายที่สองที่ยังไม่กรอกเลขได้ผู้ติดต่อ (ชื่อ/ที่อยู่) ของรายแรก และแถวใหม่เก็บ "-" สะสม
        var taxId = Accounting.Helpers.ContactTaxBranchKey.HasTaxId(buyer.TaxId) ? buyer.TaxId!.Trim() : null;
        var buyerBranch = string.IsNullOrWhiteSpace(buyer.BranchCode) ? "00000" : buyer.BranchCode;
        var buyerKey = buyer.Id.ToString();

        // (1) กุญแจแรก**เสมอ** = แถวที่ผูกกับ tenant นี้ไว้แล้ว (ExternalSystem/ExternalId ที่แถวใหม่ข้างล่างประทับเอง) — ฝ่ายค้านรอบสอง
        //     R2-C9: เดิมใช้เฉพาะกิ่ง "ไม่มีเลข" ⇒ tenant ที่บิลแรกตอนเลขยังเป็น "-" แล้วกรอกเลขทีหลัง ได้ผู้ติดต่อแถวที่สอง
        //     (ลูกหนี้/ประวัติใบกำกับแยกสองแถว แถวเก่าไม่เคยได้เลข) · ข้ามแถวนี้เฉพาะเมื่อมันถือเลข/สาขา**อื่น**แล้ว
        //     (ลูกค้าเปลี่ยนสาขาที่ลงทะเบียน ⇒ ต้องเป็นผู้ติดต่อของสาขาใหม่ §86/4)
        var existing = await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == tenantId && !c.IsDeleted
            && c.ExternalSystem == Accounting.Helpers.PlatformInvoiceRules.TenantContactSystem && c.ExternalId == buyerKey);
        if (existing != null && taxId != null && Accounting.Helpers.ContactTaxBranchKey.HasTaxId(existing.TaxId)
            && !Accounting.Helpers.ContactTaxBranchKey.Pick(
                new[] { new Accounting.Helpers.ContactKeyCandidate(existing.Id, existing.TaxId, existing.BranchCode) },
                taxId, buyerBranch).Found)
            existing = null;
        var matchedBy = Accounting.Helpers.ContactMatchKind.ExternalKey;

        // (2) เลขภาษี + สาขา (คำตัดสินเจ้าของข้อ 20) — ใบกำกับค่าบริการต้องออกในนามสาขาที่ลูกค้าลงทะเบียนไว้ (§86/4)
        var key = default(Accounting.Helpers.ContactKeyMatch);
        if (existing == null && taxId != null)
        {
            key = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(_db.Contacts, tenantId, taxId, buyerBranch);
            if (key.ContactId is Guid keyId)
                existing = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == keyId && c.CompanyId == tenantId && !c.IsDeleted);
            matchedBy = Accounting.Helpers.ContactMatchKind.TaxKey;
        }

        // (3) ชื่อ — บนชุด SoftScope (เลขใหม่ ⇒ เฉพาะแถวที่ยังไม่มีเลข · เลขมีแล้วคนละสาขา ⇒ ห้าม) และไม่เอาแถวที่ผูกกับ tenant อื่น
        //     (ชื่อซ้ำข้ามลูกค้าที่ยังไม่กรอกเลข = คนละราย)
        var softScope = Accounting.Helpers.ContactTaxBranchKey.SoftScope(_db.Contacts, tenantId, taxId, key);
        if (existing == null && softScope != null)
        {
            matchedBy = Accounting.Helpers.ContactMatchKind.ExactName;
            existing = await softScope
                .Where(c => !c.IsDeleted && c.Name == buyer.Name
                    && !(c.ExternalSystem == Accounting.Helpers.PlatformInvoiceRules.TenantContactSystem && c.ExternalId != buyerKey))
                .OrderByDescending(c => c.IsCustomer)
                .FirstOrDefaultAsync();
        }

        // แถวที่ยังไม่มีเลข (บิลก่อนลูกค้ากรอกเลข) รับเลข + สาขาปัจจุบัน — ตัวช่วยเดียวของทุกทางเข้า (R2-C5/R2-C9) ·
        // แถวลูกค้าทั่วไป (walk-in) ไม่รับเลข ⇒ Reject = สร้างแถวใหม่ (ฝ่ายค้านรอบสาม B8)
        var adopt = Accounting.Helpers.ContactTaxBranchKey.AdoptTaxId(existing, taxId, buyerBranch, matchedBy);
        if (adopt == Accounting.Helpers.ContactAdoptOutcome.Reject) existing = null;

        if (existing != null)
        {
            var dirty = adopt == Accounting.Helpers.ContactAdoptOutcome.Adopted;
            // ผู้ติดต่อเดิมอาจถูกสร้างไว้เป็นผู้ขายอย่างเดียว — ต้องเป็นลูกค้าด้วย
            // ไม่งั้นสร้างเอกสารฝั่งขายไม่ผ่าน validation บทบาทคู่ค้า
            if (!existing.IsCustomer) { existing.IsCustomer = true; dirty = true; }
            // ผูกกุญแจ tenant ให้แถวที่จับได้ด้วยเลข/ชื่อ (ถ้ายังไม่ผูกกับระบบใด) — รอบหน้าเจอด้วยกุญแจ (1) ทันที
            if (string.IsNullOrWhiteSpace(existing.ExternalSystem) && string.IsNullOrWhiteSpace(existing.ExternalId))
            {
                existing.ExternalSystem = Accounting.Helpers.PlatformInvoiceRules.TenantContactSystem;
                existing.ExternalId = buyerKey;
                dirty = true;
            }
            if (dirty) await _db.SaveChangesAsync();
            return existing.Id;
        }

        var contact = new Contact
        {
            CompanyId = tenantId,
            Name = buyer.Name,
            TaxId = taxId,
            BranchCode = buyerBranch,
            ContactType = taxId != null && taxId.Length == 13 && taxId.StartsWith('0')
                ? ContactType.JuristicPerson : ContactType.Individual,
            IsCustomer = true,
            Address = buyer.Address,
            SubDistrict = buyer.SubDistrict,
            District = buyer.District,
            Province = buyer.Province,
            PostalCode = buyer.PostalCode,
            Phone = buyer.Phone,
            Email = buyer.Email,
            CountryCode = "TH",
            // ผูกกลับไปยัง tenant ลูกค้า — ตามรอยได้ว่าผู้ติดต่อรายนี้คือบริษัทไหนในระบบ
            ExternalSystem = Accounting.Helpers.PlatformInvoiceRules.TenantContactSystem,
            ExternalId = buyer.Id.ToString(),
            CreatedBy = Actor,
        };
        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync();
        return contact.Id;
    }

    /// <summary>รอบ 227 (PLATFORM_OMISE_AUDIT C-2) — ขาเงินเข้าของเงินค่าบริการ: <b>จ่ายผ่านผู้ให้บริการรับชำระ</b> (มี intent สำเร็จของต้นทางนี้ใต้ tenant ผู้ให้บริการ)
    /// ⇒ ผังที่ <c>IGatewayAccountResolver.ResolveMoneyInAccountAsync</c> ตัดสิน (บัญชีพัก 11340 / ที่ตั้งไว้ — ผู้ให้บริการถือเงิน T+n แล้วหักค่าธรรมเนียม ·
    /// ล้างด้วยการบันทึกรอบโอนของบริษัทผู้ให้บริการ) · ไม่ใช่ gateway / ไม่รู้ / ไม่มีผังพัก ⇒ ผังเงินสด/ธนาคารที่ตั้ง (เดิม)
    /// <para><b>ที่มา:</b> เดิมทุกเส้นลง Dr ธนาคาร ⇒ รอบโอนของบริษัทผู้ให้บริการหยิบ intent ค่าบริการเหล่านี้แล้ว Cr 11340 ที่ไม่เคยถูก Dr ⇒ บัญชีพักติดลบ +
    /// ธนาคารเกินจริงสองเท่า (เงินเดียวลงธนาคารตอนอนุมัติ และอีกครั้งตอนรอบโอน)</para></summary>
    private async Task<Guid?> MoneyInAccountIdAsync(Guid tenantId, SiteSettings s, Accounting.Models.Enums.PaymentSourceKind kind, Guid? sourceId,
        Guid? intentId)
    {
        var gatewayAccount = await GatewayMoneyInAccountIdAsync(tenantId, kind, sourceId, intentId);
        return Accounting.Helpers.PlatformMoneyInAccount.Pick(gatewayAccount, await ResolveCashAccountIdAsync(tenantId, s));
    }

    /// <summary>intent ที่รับเงินก้อนนี้ — ระบุ <paramref name="intentId"/> (ส่วนเสริม: รู้รายการตรง ๆ) หรือหาจากต้นทาง <paramref name="sourceId"/> (รายการชำระค่าแพ็กเกจ)</summary>
    private async Task<Guid?> GatewayMoneyInAccountIdAsync(Guid tenantId, Accounting.Models.Enums.PaymentSourceKind kind, Guid? sourceId, Guid? intentId)
    {
        if (_gatewayAccounts == null || (sourceId == null && intentId == null)) return null;
        var intent = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == tenantId && i.SourceKind == kind
                && (intentId != null ? i.Id == intentId : i.SourceId == sourceId)
                && i.ConfirmedAt != null
                // ฝ่ายค้านรอบ 227 S1: เฉพาะเงินจริงที่ยังอยู่ (สำเร็จ/คืนบางส่วน) — คืนเต็มแล้ว/โหมดทดสอบ ไม่ใช่หลักฐานว่าเงินก้อนนี้มาทาง gateway
                && i.IsTestMode != true
                && (i.Status == Accounting.Models.Enums.PaymentIntentStatus.Succeeded
                    || i.Status == Accounting.Models.Enums.PaymentIntentStatus.PartiallyRefunded))
            .OrderByDescending(i => i.ConfirmedAt)
            .FirstOrDefaultAsync();
        return intent == null ? null : await _gatewayAccounts.ResolveMoneyInAccountAsync(intent);
    }

    public async Task<PlatformDocResult?> IssueAddOnReceiptAsync(Guid buyerCompanyId, string description, decimal amountReceived, DateTime paidAt,
        string reference, Guid? gatewayIntentId, decimal discountReceived = 0m)
    {
        (Guid TenantId, Guid DocumentId)? draft = null;
        try
        {
            var (settings, tenantId) = await ResolveTenantAsync();
            if (settings == null || tenantId == null || amountReceived <= 0m || string.IsNullOrWhiteSpace(reference)) return null;

            // กันซ้ำ: webhook ซ้ำ/แอดมินกดซ้ำ/เรียกซ้ำหลังล้มกลางทาง ⇒ ใบเดิม (ไม่ออกใบกำกับสองใบให้เงินก้อนเดียว)
            // ฝ่ายค้านรอบ 227 S6: เฉพาะใบที่ออกเลขแล้ว (ร่างที่อนุมัติล้มค้างอยู่ไม่ใช่ "ใบเดิม" — และห้ามบังใบจริงที่ออกทีหลัง)
            var existing = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == tenantId.Value && !d.IsDeleted && d.Reference == reference
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                    && (d.DocumentType == DocumentType.TaxInvoice || d.DocumentType == DocumentType.Receipt))
                .OrderBy(d => d.CreatedAt)
                .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType })
                .FirstOrDefaultAsync();
            if (existing != null)
                return new PlatformDocResult(existing.Id, existing.DocumentNumber, existing.DocumentType == DocumentType.TaxInvoice);

            var buyer = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == buyerCompanyId);
            if (buyer == null) return null;
            var contactId = await EnsureContactAsync(tenantId.Value, buyer);
            if (contactId == null) return null;

            var isVat = Accounting.Helpers.PlanPurchasePreview.IssuesTaxInvoice(settings.PlatformIsVatRegistered, settings.PlatformSellerTaxId);   // รอบ 233 N3: ตัวตัดสินเดียวกับ preview
            // ฝ่ายค้านรอบ 227 S2: ใบ = เงินที่รับพอดี (ฐาน + VAT บังคับ = ยอด) · รอบ 230: คูปอง ⇒ ส่วนลดของบรรทัดก่อน VAT (ตัวแยกเดียว PlatformCouponRule.SplitDiscounted)
            var (fullBase, discountBase, baseAmount, vatAmount) = Accounting.Helpers.PlatformCouponRule.SplitDiscounted(amountReceived, discountReceived, isVat);
            var req = new CreateDocumentRequest(
                DocumentType: isVat ? DocumentType.TaxInvoice : DocumentType.Receipt,
                // ฝ่ายค้านรอบ 227 N4: วันที่ใบ = วันที่ตามเวลาไทยของเวลารับเงิน (เงินเข้า 00:30 น. ไทย = วันนี้ ไม่ใช่เมื่อวานตาม UTC)
                DocumentDate: Accounting.Helpers.ThaiDate.CalendarDateUtc(paidAt == default ? DateTime.UtcNow : paidAt),
                DueDate: null,
                ContactId: contactId.Value,
                Reference: reference,
                Notes: $"ค่าส่วนเสริมระบบบัญชีออนไลน์ (ชำระล่วงหน้า) · {reference}",
                Lines: new List<DocumentLineRequest>
                {
                    new(Description: description, Quantity: 1m, Unit: "รายการ",
                        UnitPrice: fullBase, DiscountPercent: 0m,
                        VatRate: isVat ? 7m : 0m, WithholdingTaxRate: 0m,
                        AccountId: null,
                        AccountCode: string.IsNullOrWhiteSpace(settings.PlatformRevenueAccountCode) ? "41000" : settings.PlatformRevenueAccountCode,
                        DiscountAmount: discountBase > 0m ? discountBase : null,
                        VatAmountOverride: isVat ? vatAmount : null)
                },
                IssuedAsCashReceipt: isVat ? true : null,
                // รอบ 229: ชนิดต้นทางตาม intent จริง (ส่วนเสริม · โควตาเสริม) — เดิมฝัง AddOnPurchase ⇒ เงินโควตาเสริมผ่าน gateway หา intent ไม่เจอ ⇒ ตก Dr ธนาคาร (C-2 ซ้ำ)
                PaymentAccountId: gatewayIntentId is Guid gid
                    ? await MoneyInAccountIdAsync(tenantId.Value, settings,
                        await _db.PaymentIntents.AsNoTracking().Where(i => i.Id == gid && i.CompanyId == tenantId.Value)
                            .Select(i => (Accounting.Models.Enums.PaymentSourceKind?)i.SourceKind).FirstOrDefaultAsync()
                            ?? Accounting.Models.Enums.PaymentSourceKind.AddOnPurchase, null, gid)
                    : await ResolveCashAccountIdAsync(tenantId.Value, settings));

            var created = await Documents.CreateDocumentAsync(tenantId.Value, req, Actor);
            draft = (tenantId.Value, created.Id);
            var approved = await Documents.ApproveDocumentAsync(tenantId.Value, created.Id, Actor,
                Accounting.Helpers.ApprovalAckSource.SystemWorkflow, withAiHints: false);
            _logger.LogInformation("ออกเอกสารค่าส่วนเสริมใน tenant ผู้ให้บริการ {DocNo} (อ้างอิง {Ref} · ฐาน {Base} · VAT {Vat} · gateway {Intent})",
                approved.DocumentNumber, reference, baseAmount, isVat, gatewayIntentId);
            return new PlatformDocResult(approved.Id, approved.DocumentNumber, isVat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ออกเอกสารค่าส่วนเสริมผ่าน tenant ไม่สำเร็จ (company {CompanyId} · อ้างอิง {Ref})", buyerCompanyId, reference);
            await NoteDraftFailureAsync(draft, "ออกใบเสร็จค่าส่วนเสริมแพลตฟอร์ม", ex);
            return null;
        }
    }

    public async Task<PlatformRefundCreditResult> IssueRefundCreditNoteAsync(Guid intentId, decimal refundAmount, string refundRef, string reason, Guid counterAccountId)
    {
        (Guid TenantId, Guid DocumentId)? draft = null;
        try
        {
            var (settings, tenantId) = await ResolveTenantAsync();
            if (settings == null || tenantId == null)
                return new PlatformRefundCreditResult(false, null, null, "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ — ออกใบลดหนี้ด้วยมือ");
            var intent = await _db.PaymentIntents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == tenantId.Value);
            if (intent == null || !Accounting.Helpers.PlatformRevenueSource.IsPlatformRevenue(intent.SourceKind))
                return new PlatformRefundCreditResult(false, null, null, "ไม่ใช่รายการชำระค่าบริการของบริษัทผู้ให้บริการ");

            var cnRef = Accounting.Helpers.PlatformRefundCreditNote.Reference(refundRef);
            // กันซ้ำ: การคืนเงินครั้งนี้มีใบลดหนี้แล้ว (เรียกซ้ำ/ตรวจผลซ้ำ) ⇒ ใบเดิม
            var dup = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == tenantId.Value && !d.IsDeleted && d.DocumentType == DocumentType.CreditNote
                    && d.Reference == cnRef && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { d.Id, d.DocumentNumber, d.Status }).FirstOrDefaultAsync();
            if (dup != null && dup.Status != DocumentStatus.Draft)
                return new PlatformRefundCreditResult(true, dup.Id, dup.DocumentNumber, null);

            // ใบเดิม: ส่วนเสริม = ใบที่อ้างอิงรายการชำระนี้ · ค่าแพ็กเกจ = เอกสารจริงที่ผูกรายการชำระ (ใบเสร็จขายสด/ใบเสร็จตัดชำระใบแจ้งหนี้)
            Guid? originalId = null;
            if (intent.SourceKind == Accounting.Models.Enums.PaymentSourceKind.QuotaAddOnPurchase)
            {
                // รอบ 229: ใบเดิมของโควตาเสริม = ใบที่อ้างอิงรายการชำระนี้ (เลขอ้างอิงตัวเดียวกับฝั่งออกใบ)
                var quotaRef = Accounting.Services.Payments.QuotaAddOnPurchaseService.ReceiptReference(intent.SourceId, intent.Id);
                originalId = await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == tenantId.Value && !d.IsDeleted && d.Reference == quotaRef && d.Status != DocumentStatus.Draft
                        && (d.DocumentType == DocumentType.TaxInvoice || d.DocumentType == DocumentType.Receipt))
                    .OrderBy(d => d.CreatedAt).Select(d => (Guid?)d.Id).FirstOrDefaultAsync();
            }
            else if (intent.SourceKind == Accounting.Models.Enums.PaymentSourceKind.AddOnPurchase)
            {
                var addOnRef = Accounting.Services.Payments.AddOnPurchaseService.ReceiptReference(intent.SourceId, intent.Id, null);
                originalId = await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == tenantId.Value && !d.IsDeleted && d.Reference == addOnRef && d.Status != DocumentStatus.Draft
                        && (d.DocumentType == DocumentType.TaxInvoice || d.DocumentType == DocumentType.Receipt))
                    // ฝ่ายค้าน S3: ใบที่ยังมีผลก่อน (ใบเดิมถูกยกเลิกแล้วออกใหม่ด้วยเลขอ้างอิงเดิม ⇒ อ้างใบใหม่)
                    .OrderBy(d => d.Status == DocumentStatus.Voided).ThenBy(d => d.CreatedAt).Select(d => (Guid?)d.Id).FirstOrDefaultAsync();
            }
            else
                originalId = await _db.SubscriptionPayments.AsNoTracking()
                    .Where(p => p.Id == intent.SourceId && (intent.PayerCompanyId == null || p.Subscription.CompanyId == intent.PayerCompanyId))
                    .Select(p => p.PlatformDocumentId).FirstOrDefaultAsync();
            var original = originalId is Guid oid
                ? await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == oid && d.CompanyId == tenantId.Value && !d.IsDeleted)
                    .Select(d => new { d.Id, d.DocumentNumber, d.DocumentDate, d.DocumentType, d.Status, d.TotalAmount, d.VatAmount, d.ContactId, d.BranchId })
                    .FirstOrDefaultAsync()
                : null;
            var credited = original == null ? 0m : await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == tenantId.Value && !d.IsDeleted && d.DocumentType == DocumentType.CreditNote
                    && d.RelatedDocumentId == original.Id && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                    && (dup == null || d.Id != dup.Id))
                .SumAsync(d => (decimal?)d.TotalAmount) ?? 0m;
            var plan = Accounting.Helpers.PlatformRefundCreditNote.Decide(refundAmount, original != null, original?.Status == DocumentStatus.Voided,
                original?.TotalAmount ?? 0m, credited, (original?.VatAmount ?? 0m) > 0m);
            if (!plan.Ok || original == null) return new PlatformRefundCreditResult(false, null, null, plan.Problem);

            Guid cnId;
            if (dup != null) cnId = dup.Id;   // ร่างค้างจากครั้งก่อน (อนุมัติล้ม) ⇒ อนุมัติใบเดิม ไม่สร้างใบที่สอง
            else
            {
                // ใบเดิมมี VAT (ใบกำกับขายสด หรือใบเสร็จที่ถือ VAT ของใบแจ้งหนี้บริการ §78/1) ⇒ ใบลดหนี้ลด VAT ตามส่วน
                var isVat = original.VatAmount > 0m;
                var created = await Documents.CreateDocumentAsync(tenantId.Value, new CreateDocumentRequest(
                    DocumentType: DocumentType.CreditNote,
                    DocumentDate: Accounting.Helpers.ThaiDate.CalendarDateUtc(DateTime.UtcNow),
                    DueDate: null,
                    ContactId: original.ContactId,
                    Reference: cnRef,
                    Notes: Accounting.Helpers.PlatformRefundCreditNote.Notes(original.DocumentNumber, original.DocumentDate, original.TotalAmount, refundAmount, reason),
                    Lines: new List<DocumentLineRequest>
                    {
                        // ฝ่ายค้าน N4: เหตุผลที่แอดมินระบุพิมพ์บนใบ (§86/10 บังคับระบุสาเหตุ) — หมวดปิด Adjustment + ข้อความของแอดมิน
                        new(Description: Accounting.Helpers.PlatformRefundCreditNote.LineDescription(original.DocumentNumber, reason), Quantity: 1m, Unit: "รายการ",
                            UnitPrice: plan.BaseAmount, DiscountPercent: 0m,
                            VatRate: isVat ? 7m : 0m, WithholdingTaxRate: 0m, AccountId: null,
                            AccountCode: string.IsNullOrWhiteSpace(settings.PlatformRevenueAccountCode) ? "41000" : settings.PlatformRevenueAccountCode,
                            VatAmountOverride: isVat ? plan.VatAmount : null)
                    },
                    RelatedDocumentId: original.Id,
                    CnDnPurchaseSideOverride: false,
                    CreditNoteReason: Accounting.Models.Enums.CreditNoteReason.Adjustment,
                    BranchId: original.BranchId,
                    // ฝ่ายค้าน B1: ใบเดิมชำระครบ ⇒ DocumentService ลงขา counter ที่ "ช่องทางเงินของใบ" — ระบุบัญชีพัก/ลูกหนี้ตรง ๆ (ไม่ตกเงินสด 111)
                    PaymentAccountId: counterAccountId), Actor);
                cnId = created.Id;
            }
            draft = (tenantId.Value, cnId);
            var approved = await Documents.ApproveDocumentAsync(tenantId.Value, cnId, Actor,
                Accounting.Helpers.ApprovalAckSource.SystemWorkflow, withAiHints: false);
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = tenantId.Value, EntityType = nameof(PaymentIntent), EntityId = intent.Id.ToString(), Action = AuditAction.Create,
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "platform-refund-credit-note", ruleCode = Accounting.Helpers.PlatformRefundCreditNote.RuleCode,
                    legalReference = Accounting.Helpers.PlatformRefundCreditNote.LegalReference,
                    creditNote = approved.DocumentNumber, original = original.DocumentNumber, refundAmount, refundRef,
                }),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();
            _logger.LogInformation("ออกใบลดหนี้ {Cn} อ้างใบ {Orig} ของการคืนเงินค่าบริการ {Ref} ยอด {Amount}", approved.DocumentNumber, original.DocumentNumber, refundRef, refundAmount);
            return new PlatformRefundCreditResult(true, approved.Id, approved.DocumentNumber, null);
        }
        catch (Exception ex)
        {
            // CI รอบ 228 (ต้นเหตุ 11340 ลดสองเท่า): ApproveDocumentAsync ลง JE + ออกเลขแล้ว commit ก่อนงานข้างเคียงหลังอนุมัติ (ฝึก OCR/แจ้งเตือน ฯลฯ) —
            // งานข้างเคียงโยน ⇒ เดิมตอบ "ล้ม" ⇒ ผู้เรียกลงใบสำคัญสำรอง Cr บัญชีพักอีกขา (เงินออกสองครั้ง) · ตอนนี้ถามฐานก่อน: ใบลดหนี้ออกเลขแล้ว = สำเร็จ
            if (draft is { } d)
            {
                try
                {
                    var issued = await _db.Documents.AsNoTracking()
                        .Where(x => x.Id == d.DocumentId && x.CompanyId == d.TenantId && !x.IsDeleted
                            && x.Status != DocumentStatus.Draft && x.Status != DocumentStatus.Voided && x.Status != DocumentStatus.Rejected)
                        .Select(x => new { x.Id, x.DocumentNumber }).FirstOrDefaultAsync();
                    if (issued != null)
                    {
                        _logger.LogWarning(ex, "ใบลดหนี้ {Cn} อนุมัติแล้ว แต่งานหลังอนุมัติล้ม — ถือว่าออกใบสำเร็จ (ไม่ลงใบสำคัญสำรองซ้ำ)", issued.DocumentNumber);
                        return new PlatformRefundCreditResult(true, issued.Id, issued.DocumentNumber, null);
                    }
                }
                catch (Exception probe) { _logger.LogError(probe, "ตรวจสถานะใบลดหนี้หลังอนุมัติล้มไม่สำเร็จ (intent {Intent})", intentId); }
            }
            _logger.LogError(ex, "ออกใบลดหนี้ของการคืนเงินค่าบริการไม่สำเร็จ (intent {Intent} · อ้างอิง {Ref})", intentId, refundRef);
            await NoteDraftFailureAsync(draft, "ออกใบลดหนี้คืนเงินค่าบริการแพลตฟอร์ม", ex);
            return new PlatformRefundCreditResult(false, null, null,
                $"ออกใบลดหนี้อัตโนมัติไม่สำเร็จ ({(ex is InvalidOperationException or Accounting.Helpers.BusinessRuleException ? ex.Message : "ดูบันทึกระบบ")}) — ออกใบลดหนี้อ้างใบเดิมที่หน้าเอกสารของบริษัทผู้ให้บริการ");
        }
    }

    /// <summary>ผังเงินสด/ธนาคารที่จะรับเงินค่าบริการ — null = ให้ DocumentService
    /// ใช้ค่าเริ่มต้นของมันเอง (เงินสด)</summary>
    private async Task<Guid?> ResolveCashAccountIdAsync(Guid tenantId, SiteSettings s)
    {
        var code = s.PlatformCashAccountCode;
        if (string.IsNullOrWhiteSpace(code)) return null;
        return await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == tenantId && a.AccountCode == code.Trim() && a.IsActive)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
    }

    private static string BuildServiceDescription(SubscriptionPayment payment, Company buyer)
    {
        string desc;
        // รอบ 230: ส่วนต่างอัปเกรดกลางรอบ — บอกช่วงที่คิด (วันที่ไทย พ.ศ.) แทน "1 เดือน" ที่ไม่จริง
        if (payment.IsProration && payment.ProrationCycleEndUtc is DateTime end && payment.ProrationDays is int d && payment.ProrationCycleDays is int c)
            desc = Accounting.Helpers.PlatformProration.ReceiptDescription(payment.ProrationFromPlan ?? "-", payment.RequestedPlan.ToString(), end, d, c)
                   + $" — {buyer.Name}";
        else
        {
            var months = payment.RequestedPeriodMonths > 0 ? payment.RequestedPeriodMonths : 1;
            desc = $"ค่าบริการระบบบัญชีออนไลน์ แพ็กเกจ {payment.RequestedPlan} ({months} เดือน) — {buyer.Name}";
            // รอบ 231 (231-9): เปลี่ยนรอบบิลกลางรอบ — บอกเครดิตรอบเดิมที่หักบนใบ (ยอดรับ = ราคารอบใหม่ − เครดิต) · วันที่ไทย พ.ศ.
            if (payment.IsCycleChange && payment.CycleChangeCredit is decimal credit && credit > 0m
                && payment.ProrationCycleEndUtc is DateTime ccEnd && payment.ProrationDays is int ccD && payment.ProrationCycleDays is int ccC)
                desc += " · " + Accounting.Helpers.PlatformProration.CycleChangeCreditDescription(credit, ccD, ccC, ccEnd);
        }
        if (payment.CouponDiscountAmount > 0m && !string.IsNullOrWhiteSpace(payment.CouponCode))
            desc += $" · ส่วนลดคูปอง {payment.CouponCode}";
        return desc;
    }
}
