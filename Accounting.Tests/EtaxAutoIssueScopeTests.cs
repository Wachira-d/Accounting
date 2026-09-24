using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// S-02 (รอบ 193) — ขอบเขตของ e-Tax อัตโนมัติหลังออกเอกสาร (<c>IssuedDocumentHooks</c>) ที่ตอนนี้ถูกเรียกจาก
/// ApproveDocumentAsync + POS ใบกำกับเต็มรูป + Integration TIV/CN/DN + CMS
///
/// <para>สองครึ่ง: (1) ใบที่ <b>ควรมี</b> e-Tax (ใบกำกับจาก POS/API · ใบลดหนี้ฝั่งขาย) ต้องไม่ถูกข้าม — เดิมไม่มีใครออกให้เลย
/// (2) ใบที่ "โดยเจตนาไม่ใช่ใบกำกับ" ต้องถูกข้ามเงียบ ๆ — เดิมถูกโยน exception แล้วประทับ [ETAX-AUTO-FAILED] ทั้งที่ไม่มีอะไรล้ม</para>
/// </summary>
public class EtaxAutoIssueScopeTests
{
    private static EtaxAutoSkip Judge(DocumentType type, DocumentStatus status = DocumentStatus.Approved,
        bool isDeposit = false, bool deferred = false, DateTime? recognizedAt = null, DocumentType? related = null,
        bool? byLaw = true, bool notFull = false)
        => EtaxAutoIssueScope.Judge(type, status, isDeposit, deferred, recognizedAt, related, byLaw, notFull);

    // ── ฝ่ายค้าน C-1/C-2: ใบที่ "ไม่ใช่ใบกำกับเต็มรูป" / ตรึงว่าไม่ใช่ใบกำกับ ต้องข้ามเงียบ ──

    [Theory]
    [InlineData(DocumentType.TaxInvoice)]   // ขายหน้าร้าน/PMS ผ่าน API ผู้ซื้อ walk-in
    [InlineData(DocumentType.Receipt)]
    public void ไม่ใช่ใบกำกับเต็มรูป_walkin_หรือผู้ซื้อไม่ประสงค์รับ_ข้าม(DocumentType type)
        => Assert.Equal(EtaxAutoSkip.NotFullTaxInvoice, Judge(type, notFull: true));

    [Theory]
    [InlineData(DocumentType.TaxInvoice)]   // VAT 0 ทั้งใบ/ยกเว้น §81 ผ่าน API → หัว "ใบเสร็จ"
    [InlineData(DocumentType.Receipt)]
    public void ตรึงว่าไม่ใช่ใบกำกับตามกฎหมาย_ข้าม(DocumentType type)
        => Assert.Equal(EtaxAutoSkip.NotTaxInvoiceByLaw, Judge(type, byLaw: false));

    [Fact]
    public void ใบก่อนมีธง_IsTaxInvoiceByLaw_null_ไม่ถูกตีความว่าไม่ใช่ใบกำกับ()
        => Assert.Equal(EtaxAutoSkip.None, Judge(DocumentType.TaxInvoice, byLaw: null));

    [Theory]
    [InlineData(DocumentType.CreditNote)]   // §86/9-10 CN/DN ถือเป็นใบกำกับ — ธงหัวกระดาษ/ผู้ซื้อไม่ใช้ข้าม
    [InlineData(DocumentType.DebitNote)]
    public void ใบลดเพิ่มหนี้_ไม่ถูกข้ามด้วยธงหัวกระดาษหรือผู้ซื้อ(DocumentType type)
        => Assert.Equal(EtaxAutoSkip.None, Judge(type, related: DocumentType.TaxInvoice, byLaw: false, notFull: true));

    [Fact]
    public void ใบกำกับเต็มรูปผู้ซื้อครบ_ยังออก_eTax()
        => Assert.Equal(EtaxAutoSkip.None, Judge(DocumentType.TaxInvoice, byLaw: true, notFull: false));

    // ── ต้องออก e-Tax (ไม่ข้าม) ──

    [Theory]
    [InlineData(DocumentType.TaxInvoice, DocumentStatus.Approved)]   // POS ใบกำกับเต็มรูป / Integration TIV
    [InlineData(DocumentType.TaxInvoice, DocumentStatus.Paid)]       // ขายเงินสด — สถานะเดินต่อแล้ว
    [InlineData(DocumentType.DebitNote, DocumentStatus.Approved)]    // Integration DN (ไม่มีใบต้นทางในระบบ → ให้ตัวออกตัดสิน)
    [InlineData(DocumentType.Receipt, DocumentStatus.Paid)]          // ใบเสร็จขายสด
    public void ใบที่เป็นใบกำกับได้_ไม่ถูกข้าม(DocumentType type, DocumentStatus status)
        => Assert.Equal(EtaxAutoSkip.None, Judge(type, status));

    [Fact]
    public void ใบลดหนี้ฝั่งขาย_อ้างใบกำกับ_ไม่ถูกข้าม()
        => Assert.Equal(EtaxAutoSkip.None, Judge(DocumentType.CreditNote, related: DocumentType.TaxInvoice));

    [Fact]
    public void ใบมัดจำที่_VAT_รับรู้แล้ว_ไม่ถูกข้าม()
        => Assert.Equal(EtaxAutoSkip.None,
            Judge(DocumentType.Receipt, isDeposit: true, deferred: true, recognizedAt: new DateTime(2026, 9, 1)));

    [Fact]
    public void ใบลดหนี้ที่ไม่รู้ใบต้นทาง_ไม่ถูกข้าม_ให้ตัวออกดัง()
        // "ไม่รู้" ห้ามตกเป็นเงียบ — GenerateAsync จะโยน "ยังไม่ได้อ้างอิงใบกำกับเดิม" แล้ว hook ประทับป้าย
        => Assert.Equal(EtaxAutoSkip.None, Judge(DocumentType.CreditNote, related: null));

    // ── ข้ามโดยเจตนา (เงียบ) ──

    [Theory]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.PurchaseOrder)]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.CertificateInLieu)]
    public void ชนิดที่ไม่ใช่ใบกำกับ_ข้าม(DocumentType type)
        => Assert.Equal(EtaxAutoSkip.NotEtaxType, Judge(type));

    [Theory]
    [InlineData(DocumentStatus.Draft)]              // Integration Expense/PV autoApprove=false
    [InlineData(DocumentStatus.WaitingApproval)]
    [InlineData(DocumentStatus.Rejected)]
    public void ยังไม่ออกจริง_ข้าม(DocumentStatus status)
        => Assert.Equal(EtaxAutoSkip.NotIssued, Judge(DocumentType.TaxInvoice, status));

    [Fact]
    public void ยกเลิกแล้ว_ข้าม()
        => Assert.Equal(EtaxAutoSkip.Voided, Judge(DocumentType.TaxInvoice, DocumentStatus.Voided));

    [Fact]
    public void ใบรับมัดจำที่_VAT_ยังพักรอ_ข้าม()
        => Assert.Equal(EtaxAutoSkip.DeferredDepositReceipt,
            Judge(DocumentType.Receipt, DocumentStatus.Paid, isDeposit: true, deferred: true));

    [Fact]
    public void ใบเสร็จรับชำระใบกำกับ_ข้าม()
        => Assert.Equal(EtaxAutoSkip.ReceiptSettlesTaxInvoice,
            Judge(DocumentType.Receipt, DocumentStatus.Paid, related: DocumentType.TaxInvoice));

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.CertificateInLieu)]
    public void ใบลดหนี้ฝั่งซื้อ_ข้าม(DocumentType source)
    {
        Assert.Equal(EtaxAutoSkip.PurchaseSideAdjustment, Judge(DocumentType.CreditNote, related: source));
        Assert.Equal(EtaxAutoSkip.PurchaseSideAdjustment, Judge(DocumentType.DebitNote, related: source));
    }

    [Fact]
    public void ชุดชนิด_e_Tax_มีสี่ชนิดตาม_T01_T04()
        => Assert.Equal(new[] { DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.DebitNote, DocumentType.CreditNote },
            EtaxAutoIssueScope.EtaxTypes);
}
