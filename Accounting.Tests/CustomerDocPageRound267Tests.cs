using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Xunit;
using EntDocument = Accounting.Models.Entities.Document;

namespace Accounting.Tests;

/// <summary>
/// รอบ 267 แพ็ก P1 — หน้าเอกสารสำหรับลูกค้า (pure · ทั้งสองทิศ): ตัวตัดสินเดียว <see cref="CustomerDocPage"/>
/// (ออกลิงก์ได้ไหม · เปิดได้ไหม · ปุ่มไหนเปิดในโหมดไหน · ช่วงตอบรับ · สถานะลิงก์/ขอลิงก์ใหม่ · ข้อความ · hash ของลิงก์รุ่นก่อน · ปิดบัง token ใน log)
/// — DOCUMENT_FLOW §2.12n · คำตัดสิน 267-1-1..267-1-3 · สถานการณ์ S5/S6/S13/S25/S26/S28/S29/S36/S37/S38 ของ team-267-doc-pages-design.md §17
/// (เส้นจริงบนฐาน: <c>CustomerDocPageRound267DbTests</c>)
/// </summary>
public class CustomerDocPageRound267Tests
{
    private static readonly DateTime Now = new(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);

    private static CustomerDocPage.Facts F(DocumentType type, DocumentStatus status = DocumentStatus.Approved,
        CustomerDocPage.Mode mode = CustomerDocPage.Mode.Link, bool en = false, bool pay = false, string? payNote = null, bool slip = false,
        DateTime? due = null, DateTime? start = null, DateTime? responded = null, string? href = null, bool pdf = true)
        => new(type, status, mode, en, pay, payNote, slip, due, start, responded, href, pdf);

    private static CustomerDocPage.PageAction? Act(IReadOnlyList<CustomerDocPage.PageAction> list, string id) => list.FirstOrDefault(a => a.Id == id);

    // ───────── ตารางชนิด (§3): ออกลิงก์ได้/ไม่ได้ ─────────

    [Theory]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.DeliveryNote)]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.DebitNote)]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.ReceiptVoucher)]
    [InlineData(DocumentType.CreditNote)]
    [InlineData(DocumentType.BillingNote)]
    public void CDP267_SaleSideIssuedTypes_ออกลิงก์หน้าเอกสารได้(DocumentType type)
    {
        Assert.Null(CustomerDocPage.IssueRefusal(type, DocumentStatus.Approved, customerFacing: true, platformTenantDocument: false));
        Assert.False(CustomerDocPage.ViewRefused(type, DocumentStatus.Paid, customerFacing: true));
    }

    [Fact]
    public void CDP267_PurchaseOrder_ออกได้แม้ไม่ใช่เอกสารที่ลูกค้าเห็น_ผู้รับคือผู้ขาย()
    {
        Assert.Null(CustomerDocPage.IssueRefusal(DocumentType.PurchaseOrder, DocumentStatus.Approved, customerFacing: false, platformTenantDocument: false));
        Assert.False(CustomerDocPage.ViewRefused(DocumentType.PurchaseOrder, DocumentStatus.Approved, customerFacing: false));
    }

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    [InlineData(DocumentType.PurchaseRequisition)]
    [InlineData(DocumentType.GoodsReceiptNote)]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.CertificateInLieu)]
    public void CDP267_PurchaseSideRefused_ฝั่งซื้อออกลิงก์ไม่ได้_และเปิดไม่ได้(DocumentType type)
    {
        Assert.NotNull(CustomerDocPage.IssueRefusal(type, DocumentStatus.Approved, customerFacing: false, platformTenantDocument: false));
        Assert.True(CustomerDocPage.ViewRefused(type, DocumentStatus.Approved, customerFacing: false));
        // แม้ผู้เรียกส่ง customerFacing ผิดเป็น true ชนิดนอกตารางก็ยังไม่ได้ (ตารางชนิดเป็นด่านของตัวเอง)
        Assert.NotNull(CustomerDocPage.IssueRefusal(type, DocumentStatus.Approved, customerFacing: true, platformTenantDocument: false));
    }

    [Fact]
    public void CDP267_AdjustmentNoteUnknownSide_ใบลดหนี้เพิ่มหนี้ที่ไม่รู้ฝั่ง_ไม่ออก()
    {
        // ฝั่งมาจาก DocumentPayLinkPolicy.IsCustomerFacing (ไม่รู้ฝั่ง/ฝั่งซื้อ = false) — ที่นี่ต้องปฏิเสธตามนั้น
        Assert.NotNull(CustomerDocPage.IssueRefusal(DocumentType.CreditNote, DocumentStatus.Approved, customerFacing: false, platformTenantDocument: false));
        Assert.NotNull(CustomerDocPage.IssueRefusal(DocumentType.DebitNote, DocumentStatus.Approved, customerFacing: false, platformTenantDocument: false));
        Assert.Null(CustomerDocPage.IssueRefusal(DocumentType.CreditNote, DocumentStatus.Approved, customerFacing: true, platformTenantDocument: false));
    }

    [Fact]
    public void CDP267_PlatformTenantRefused_ใบค่าบริการของผู้เช่าออกลิงก์ไม่ได้_ข้อความเดียวกับลิงก์ชำระ()
    {
        var why = CustomerDocPage.IssueRefusal(DocumentType.Invoice, DocumentStatus.Approved, customerFacing: true, platformTenantDocument: true);
        Assert.Equal(DocumentPayLinkPolicy.PlatformTenantRefusal, why);
        // ทิศตรงข้าม: ใบแจ้งหนี้ของบริษัททั่วไป ⇒ ออกได้
        Assert.Null(CustomerDocPage.IssueRefusal(DocumentType.Invoice, DocumentStatus.Approved, customerFacing: true, platformTenantDocument: false));
    }

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.WaitingApproval)]
    [InlineData(DocumentStatus.Rejected)]
    public void CDP267_NotIssued_ร่าง_รออนุมัติ_ถูกปฏิเสธ_ออกไม่ได้และเปิดไม่ได้(DocumentStatus status)
    {
        Assert.Contains("อนุมัติ", CustomerDocPage.IssueRefusal(DocumentType.Invoice, status, true, false));
        Assert.True(CustomerDocPage.ViewRefused(DocumentType.Invoice, status, true));   // ใบกลับเป็นร่างหลังส่งลิงก์ ⇒ ลิงก์เดิมเปิดไม่ได้
    }

    [Fact]
    public void CDP267_Voided_ออกลิงก์ใหม่ไม่ได้_แต่ลิงก์ที่ส่งไปแล้วยังเปิดได้_มีแค่ดาวน์โหลด()
    {
        Assert.NotNull(CustomerDocPage.IssueRefusal(DocumentType.Invoice, DocumentStatus.Voided, true, false));
        Assert.False(CustomerDocPage.ViewRefused(DocumentType.Invoice, DocumentStatus.Voided, true));   // ทิศตรงข้าม: เปิดดูสถานะยกเลิกได้
        var actions = CustomerDocPage.Actions(F(DocumentType.Invoice, DocumentStatus.Voided, pay: true, slip: true), Now);
        var only = Assert.Single(actions);
        Assert.Equal(CustomerDocPage.ActionDownloadPdf, only.Id);
        Assert.True(only.Enabled);
        Assert.Equal("ยกเลิกแล้ว", CustomerDocPage.StatusLabel(false, DocumentType.Invoice, DocumentStatus.Voided, 100m));
    }

    // ───────── ปุ่มต่อชนิด/โหมด ─────────

    [Fact]
    public void CDP267_InvoiceLink_จ่ายและสลิปเปิดตามชั้นจ่าย_ปิดพร้อมเหตุ()
    {
        var open = CustomerDocPage.Actions(F(DocumentType.Invoice, pay: true, slip: true), Now);
        Assert.Equal(new[] { CustomerDocPage.ActionDownloadPdf, CustomerDocPage.ActionPayOnline, CustomerDocPage.ActionSubmitSlip }, open.Select(a => a.Id));
        Assert.All(open, a => Assert.True(a.Enabled));
        Assert.All(open, a => Assert.Null(a.DisabledReason));

        var paid = CustomerDocPage.Actions(F(DocumentType.Invoice, DocumentStatus.Paid, payNote: "เอกสารนี้ชำระครบแล้ว"), Now);
        Assert.False(Act(paid, CustomerDocPage.ActionPayOnline)!.Enabled);
        Assert.Equal("เอกสารนี้ชำระครบแล้ว", Act(paid, CustomerDocPage.ActionPayOnline)!.DisabledReason);
        Assert.False(Act(paid, CustomerDocPage.ActionSubmitSlip)!.Enabled);
        Assert.NotNull(Act(paid, CustomerDocPage.ActionSubmitSlip)!.DisabledReason);
        Assert.True(Act(paid, CustomerDocPage.ActionDownloadPdf)!.Enabled);                     // ดาวน์โหลดได้ต่อ (400 วัน)
        Assert.Null(Act(paid, CustomerDocPage.ActionRequestLink));                              // ไม่มีอะไรให้ขอ
    }

    [Theory]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.ReceiptVoucher)]
    [InlineData(DocumentType.CreditNote)]
    [InlineData(DocumentType.BillingNote)]
    [InlineData(DocumentType.PurchaseOrder)]
    public void CDP267_ViewOnlyTypes_มีแค่ดาวน์โหลด_ไม่มีปุ่มจ่ายหรือตอบรับ(DocumentType type)
    {
        // P1: ใบลดหนี้ไม่มีปุ่มรับทราบ (267-1-10) · ใบวางบิลจ่ายรายใบ/ใบสั่งซื้อรับทราบ = แพ็กถัดไป — แม้ผู้เรียกส่ง pay=true มาก็ไม่มีปุ่มจ่าย
        var only = Assert.Single(CustomerDocPage.Actions(F(type, pay: true, slip: true), Now));
        Assert.Equal(CustomerDocPage.ActionDownloadPdf, only.Id);
    }

    [Fact]
    public void CDP267_PreviewNoSideEffects_พรีวิวของพนักงาน_ทุกปุ่มปิดพร้อมเหตุ_ไม่มีปุ่มขอลิงก์()
    {
        foreach (var type in new[] { DocumentType.Invoice, DocumentType.Quotation, DocumentType.DeliveryNote, DocumentType.Receipt })
        {
            var actions = CustomerDocPage.Actions(F(type, mode: CustomerDocPage.Mode.Preview, pay: true, slip: true, start: Now, href: "/x"), Now);
            Assert.NotEmpty(actions);
            Assert.All(actions, a => Assert.False(a.Enabled));
            Assert.All(actions, a => Assert.False(string.IsNullOrWhiteSpace(a.DisabledReason)));
            Assert.All(actions, a => Assert.Null(a.Href));
            Assert.Null(Act(actions, CustomerDocPage.ActionRequestLink));
        }
        Assert.Equal("preview", CustomerDocPage.ModeCode(CustomerDocPage.Mode.Preview));
    }

    [Theory]
    [InlineData(CustomerDocPage.Mode.LineView)]
    [InlineData(CustomerDocPage.Mode.Portal)]
    public void CDP267_ViewModes_ดูและดาวน์โหลดได้_จ่ายต้องใช้ลิงก์_มีทางไปต่อ(CustomerDocPage.Mode mode)
    {
        var actions = CustomerDocPage.Actions(F(DocumentType.Invoice, mode: mode, pay: true), Now);
        Assert.True(Act(actions, CustomerDocPage.ActionDownloadPdf)!.Enabled);
        var pay = Act(actions, CustomerDocPage.ActionPayOnline)!;
        Assert.False(pay.Enabled);
        Assert.Contains("ลิงก์", pay.DisabledReason);
        Assert.Null(Act(actions, CustomerDocPage.ActionSubmitSlip));                 // ฟอร์มสลิปมีเฉพาะโหมดลิงก์
        Assert.True(Act(actions, CustomerDocPage.ActionRequestLink)!.Enabled);       // ไม่ใช่ทางตัน
        // ทิศตรงข้าม: ชำระครบแล้ว ⇒ ไม่ชวนขอลิงก์ และบอกเหตุของชั้นจ่าย
        var paid = CustomerDocPage.Actions(F(DocumentType.Invoice, DocumentStatus.Paid, mode, payNote: "เอกสารนี้ชำระครบแล้ว"), Now);
        Assert.Null(Act(paid, CustomerDocPage.ActionRequestLink));
        Assert.Equal("เอกสารนี้ชำระครบแล้ว", Act(paid, CustomerDocPage.ActionPayOnline)!.DisabledReason);
        // ใบเสนอราคาในโหมดดู: ตอบรับปิด + ขอลิงก์ได้
        var qt = CustomerDocPage.Actions(F(DocumentType.Quotation, mode: mode, start: Now, href: null), Now);
        Assert.False(Act(qt, CustomerDocPage.ActionAccept)!.Enabled);
        Assert.NotNull(Act(qt, CustomerDocPage.ActionRequestLink));
    }

    [Fact]
    public void CDP267_NoProviderPageStillWorks_ไม่มีช่องทางออนไลน์_หน้ายังใช้ได้_เหลือแค่ปุ่มจ่ายออนไลน์ปิด()
    {
        // S37: บริษัทปิด gateway ทุกตัว ⇒ ดู/ดาวน์โหลด/แจ้งโอนยังทำได้
        const string note = "ผู้ขายยังไม่เปิดรับชำระออนไลน์สำหรับเอกสารนี้ — กรุณาติดต่อผู้ขาย";
        var actions = CustomerDocPage.Actions(F(DocumentType.Invoice, pay: false, payNote: note, slip: true), Now);
        Assert.True(Act(actions, CustomerDocPage.ActionDownloadPdf)!.Enabled);
        Assert.True(Act(actions, CustomerDocPage.ActionSubmitSlip)!.Enabled);
        Assert.False(Act(actions, CustomerDocPage.ActionPayOnline)!.Enabled);
        Assert.Equal(note, Act(actions, CustomerDocPage.ActionPayOnline)!.DisabledReason);
        // ใบเสนอราคาไม่ขึ้นกับ gateway เลย
        var qt = CustomerDocPage.Actions(F(DocumentType.Quotation, start: Now, href: "/pages/quotation-accept.html?token=ab"), Now);
        Assert.True(Act(qt, CustomerDocPage.ActionAccept)!.Enabled);
    }

    [Fact]
    public void CDP267_PdfUnavailable_ปุ่มปิดพร้อมเหตุ()
    {
        var a = Act(CustomerDocPage.Actions(F(DocumentType.Receipt, pdf: false), Now), CustomerDocPage.ActionDownloadPdf)!;
        Assert.False(a.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(a.DisabledReason));
    }

    // ───────── ช่วงตอบรับ (S5 · S6) ─────────

    [Fact]
    public void CDP267_AcceptOpenBeforeValidity_ยอมรับได้ตลอดวันยืนราคาตามเวลาไทย()
    {
        var due = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);              // วันปฏิทินไทย 20 พ.ย. (เก็บ 00:00 UTC)
        var lastMoment = new DateTime(2026, 11, 20, 16, 59, 0, DateTimeKind.Utc);     // 23:59 เวลาไทยของวันยืนราคา
        var href = "/pages/quotation-accept.html?token=ab";
        var a = Act(CustomerDocPage.Actions(F(DocumentType.Quotation, due: due, start: Now.AddDays(-40), href: href), lastMoment), CustomerDocPage.ActionAccept)!;
        Assert.True(a.Enabled);                                                        // วันยืนราคาชนะ "30 วันจากออกลิงก์"
        Assert.Equal(href, a.Href);
        Assert.Null(a.DisabledReason);
        Assert.Null(Act(CustomerDocPage.Actions(F(DocumentType.Quotation, due: due, start: Now, href: href), lastMoment), CustomerDocPage.ActionRequestLink));
    }

    [Fact]
    public void CDP267_AcceptClosedAfterValidity_พ้นวันยืนราคา_ปุ่มปิดพร้อมเหตุ_และมีทางขอลิงก์()
    {
        var due = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        var justAfter = new DateTime(2026, 11, 20, 17, 0, 0, DateTimeKind.Utc);       // เที่ยงคืนเวลาไทยของวันถัดไป
        var actions = CustomerDocPage.Actions(F(DocumentType.Quotation, due: due, start: Now, href: "/pages/quotation-accept.html?token=ab"), justAfter);
        var a = Act(actions, CustomerDocPage.ActionAccept)!;
        Assert.False(a.Enabled);
        Assert.Contains("ยืนราคาถึง", a.DisabledReason);
        Assert.Null(a.Href);                                                           // ไม่ส่งที่อยู่ของปุ่มที่ปิด
        Assert.True(Act(actions, CustomerDocPage.ActionRequestLink)!.Enabled);
        Assert.Equal(justAfter, CustomerDocPage.RespondWindowEnd(DocumentType.Quotation, due, Now));
    }

    [Fact]
    public void CDP267_NoValidityUses30Days_ไม่มีวันยืนราคา_ใช้30วันจากวันส่งลิงก์()
    {
        var href = "/pages/quotation-accept.html?token=ab";
        Assert.True(Act(CustomerDocPage.Actions(F(DocumentType.Quotation, start: Now, href: href), Now.AddDays(30).AddSeconds(-1)), CustomerDocPage.ActionAccept)!.Enabled);
        var late = Act(CustomerDocPage.Actions(F(DocumentType.Quotation, start: Now, href: href), Now.AddDays(30)), CustomerDocPage.ActionAccept)!;
        Assert.False(late.Enabled);
        Assert.Contains("พ้นกำหนด", late.DisabledReason);
        // ยังไม่มีวันออกลิงก์ (ข้อมูลไม่ครบ) ⇒ ปิดพร้อมเหตุ ไม่ใช่เปิดตลอดกาล
        Assert.False(Act(CustomerDocPage.Actions(F(DocumentType.Quotation, href: href), Now), CustomerDocPage.ActionAccept)!.Enabled);
        Assert.Equal(SignRequestKinds.QuotationAccept.TtlDays, CustomerDocPage.QuotationDefaultWindowDays);   // ตัวเลขเดียว
    }

    [Fact]
    public void CDP267_DeliverySign14Days_และตอบรับแล้วไม่ขอลิงก์()
    {
        var href = "/pages/delivery-sign.html?token=ab";
        Assert.True(Act(CustomerDocPage.Actions(F(DocumentType.DeliveryNote, start: Now, href: href), Now.AddDays(13)), CustomerDocPage.ActionSignDelivery)!.Enabled);
        Assert.False(Act(CustomerDocPage.Actions(F(DocumentType.DeliveryNote, start: Now, href: href), Now.AddDays(14)), CustomerDocPage.ActionSignDelivery)!.Enabled);
        Assert.Equal(SignRequestKinds.DeliverySign.TtlDays, CustomerDocPage.DeliverySignWindowDays);
        var signed = CustomerDocPage.Actions(F(DocumentType.DeliveryNote, start: Now, responded: Now, href: href), Now.AddDays(1));
        Assert.False(Act(signed, CustomerDocPage.ActionSignDelivery)!.Enabled);
        Assert.Contains("เซ็นรับ", Act(signed, CustomerDocPage.ActionSignDelivery)!.DisabledReason);
        Assert.Null(Act(signed, CustomerDocPage.ActionRequestLink));
        Assert.False(CustomerDocPage.ShowsAmounts(DocumentType.DeliveryNote));          // ใบส่งของไม่แสดงราคา
        Assert.True(CustomerDocPage.ShowsAmounts(DocumentType.Quotation));
    }

    [Fact]
    public void CDP267_SignStatusChip_ช่วงตอบรับไม่เกินวันหมดอายุของลิงก์_และวันยืนราคาชนะ()
    {
        var d = new EntDocument { DocumentType = DocumentType.Quotation, PayLinkTokenHash = "h", PayLinkExpiresAt = Now.AddDays(10) };
        SignRequestKinds.MarkIssued(d, Now);
        Assert.Equal(Now.AddDays(10), SignRequestKinds.Read(d).ExpiresAt);             // ลิงก์หมดก่อน 30 วัน ⇒ ใช้วันหมดของลิงก์
        d.PayLinkExpiresAt = Now.AddDays(400);
        d.DueDate = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 11, 15, 17, 0, 0, DateTimeKind.Utc), SignRequestKinds.Read(d).ExpiresAt);
        Assert.True(SignRequestKinds.LinkUsable(SignRequestKinds.Read(d), Now));
        Assert.False(SignRequestKinds.LinkUsable(SignRequestKinds.Read(d), Now.AddDays(4)));
    }

    // ───────── สถานะลิงก์ · ขอลิงก์ใหม่ (S27 · S28 · S29) ─────────

    [Fact]
    public void CDP267_LinkState_ใช้ได้_หมดอายุ_เพิกถอน_ไม่มี()
    {
        Assert.Equal(CustomerDocPage.LinkState.None, CustomerDocPage.StateOf(null, null, null, Now));
        Assert.Equal(CustomerDocPage.LinkState.Usable, CustomerDocPage.StateOf("h", Now.AddDays(1), null, Now));
        Assert.Equal(CustomerDocPage.LinkState.Usable, CustomerDocPage.StateOf("h", null, null, Now));
        Assert.Equal(CustomerDocPage.LinkState.Expired, CustomerDocPage.StateOf("h", Now, null, Now));
        Assert.Equal(CustomerDocPage.LinkState.Revoked, CustomerDocPage.StateOf("h", Now.AddDays(1), Now, Now));
        Assert.Equal(CustomerDocPage.LinkState.Revoked, CustomerDocPage.StateOf("h", Now.AddDays(-9), Now, Now));   // เพิกถอนชนะหมดอายุ
        // ระดับหน้า = ตัวตัดสินเดิมของลิงก์ชำระเมื่อไม่มีใบกำกับอ้าง (สองตัวต้องตอบตรงกัน)
        foreach (var (hash, exp, rev) in new (string?, DateTime?, DateTime?)[] { ("h", Now.AddDays(1), null), ("h", Now, null), ("h", null, Now), (null, null, null) })
            Assert.Equal(DocumentPayLinkPolicy.LinkUsable(hash, exp, rev, Now, null), CustomerDocPage.StateOf(hash, exp, rev, Now) == CustomerDocPage.LinkState.Usable);
    }

    [Fact]
    public void CDP267_RevokedNoRequestButton_เพิกถอนขอลิงก์ใหม่ไม่ได้_หมดอายุขอได้ภายใน180วัน()
    {
        var exp = Now.AddDays(-10);
        Assert.True(CustomerDocPage.CanRequestNewLink(CustomerDocPage.LinkState.Expired, exp, Now));                 // S27: หมดอายุ 10 วัน
        Assert.False(CustomerDocPage.CanRequestNewLink(CustomerDocPage.LinkState.Revoked, exp, Now));                // S28: ผู้ขายตั้งใจปิด
        Assert.False(CustomerDocPage.CanRequestNewLink(CustomerDocPage.LinkState.Usable, Now.AddDays(5), Now));
        Assert.False(CustomerDocPage.CanRequestNewLink(CustomerDocPage.LinkState.None, null, Now));
        Assert.True(CustomerDocPage.CanRequestNewLink(CustomerDocPage.LinkState.Expired, Now.AddDays(-CustomerDocPage.ExpiredRequestWindowDays), Now));
        Assert.False(CustomerDocPage.CanRequestNewLink(CustomerDocPage.LinkState.Expired, Now.AddDays(-CustomerDocPage.ExpiredRequestWindowDays - 1), Now));
    }

    [Fact]
    public void CDP267_EnumerationUniform_ลิงก์ใช้ไม่ได้ไม่มีข้อมูลของใบ_ต่างกันแค่ขอลิงก์ใหม่ได้ไหม()
    {
        var revoked = CustomerDocPageView.Unusable(CustomerDocPage.LinkState.Revoked, false);
        var expired = CustomerDocPageView.Unusable(CustomerDocPage.LinkState.Expired, true);
        foreach (var v in new[] { revoked, expired })
        {
            Assert.False(v.Viewable);
            Assert.Empty(v.Lines);
            Assert.Empty(v.Actions);
            Assert.Empty(v.Related);
            Assert.Equal(0m, v.TotalAmount);
            Assert.Equal("", v.StatusLabel);
            Assert.Null(v.PdfPath);
            Assert.Null(v.RespondedBy);
        }
        Assert.False(revoked.CanRequestLink);
        Assert.True(expired.CanRequestLink);
        Assert.DoesNotContain("เพิกถอน", CustomerDocPage.NotFoundMessage);            // ข้อความเดียว ไม่บอกเหตุ
    }

    [Fact]
    public void CDP267_LinkTtl_ตัวเลขเดียวของลิงก์ชำระและลิงก์หน้าเอกสาร()
    {
        Assert.Equal(CustomerDocPage.LinkTtlDays, DocumentPayLinkPolicy.TtlDays);
        Assert.Equal(LineDocumentViewLink.TtlDays, CustomerDocPage.LinkTtlDays);
        Assert.Equal(Now.AddDays(400), CustomerDocPage.NewLinkExpiry(Now));
    }

    // ───────── ซ่อนยอด (S21) ─────────

    [Fact]
    public void CDP267_HiddenAmountGroupNoPageLink_กลุ่มLINEซ่อนยอดไม่ได้ลิงก์หน้าเอกสาร()
    {
        Assert.False(CustomerDocPage.PageLinkAllowedForLineEndpoint(NotifyEndpointKind.LineGroup, showAmounts: false, null, null));
        // ทิศตรงข้าม: กลุ่มที่แสดงยอด · แชต 1:1 ⇒ ได้
        Assert.True(CustomerDocPage.PageLinkAllowedForLineEndpoint(NotifyEndpointKind.LineGroup, showAmounts: true, null, null));
        Assert.True(CustomerDocPage.PageLinkAllowedForLineEndpoint(NotifyEndpointKind.LineUser, showAmounts: false, null, null));
        // ยกเลิกผูก/บล็อก/ไม่ใช่ LINE ⇒ ไม่ได้
        Assert.False(CustomerDocPage.PageLinkAllowedForLineEndpoint(NotifyEndpointKind.LineUser, true, Now, null));
        Assert.False(CustomerDocPage.PageLinkAllowedForLineEndpoint(NotifyEndpointKind.LineUser, true, null, Now));
        Assert.False(CustomerDocPage.PageLinkAllowedForLineEndpoint(NotifyEndpointKind.Email, true, null, null));
    }

    // ───────── ภาษา (S38) ─────────

    [Fact]
    public void CDP267_EnglishDocumentPage_ป้ายและปุ่มเป็นอังกฤษ_ไทยเป็นไทย_ชุดคีย์เท่ากัน()
    {
        var th = CustomerDocPage.Labels(false, DocumentType.Quotation);
        var en = CustomerDocPage.Labels(true, DocumentType.Quotation);
        Assert.Equal(th.Keys.OrderBy(k => k), en.Keys.OrderBy(k => k));
        Assert.Equal("ยืนราคาถึง", th["due"]);
        Assert.Equal("Valid until", en["due"]);
        Assert.Equal("Due date", CustomerDocPage.Labels(true, DocumentType.Invoice)["due"]);
        Assert.Equal("กำหนดส่งมอบ", CustomerDocPage.Labels(false, DocumentType.PurchaseOrder)["due"]);
        Assert.Equal("บาท", th["currency"]);
        Assert.Equal("THB", en["currency"]);
        Assert.All(en.Values, v => Assert.DoesNotMatch("[ก-๙]", v));                   // โหมดอังกฤษไม่ปนไทย
        var actions = CustomerDocPage.Actions(F(DocumentType.Quotation, en: true, due: Now.AddDays(-30), start: Now.AddDays(-60), href: "/x"), Now);
        Assert.All(actions, a => Assert.DoesNotMatch("[ก-๙]", a.Label + (a.DisabledReason ?? "")));
        Assert.Equal("Paid in full", CustomerDocPage.StatusLabel(true, DocumentType.Invoice, DocumentStatus.Paid, 0m));
        Assert.Equal("รอชำระ", CustomerDocPage.StatusLabel(false, DocumentType.Invoice, DocumentStatus.Approved, 10m));
        Assert.Equal("ออกเอกสารแล้ว", CustomerDocPage.StatusLabel(false, DocumentType.Quotation, DocumentStatus.Approved, 0m));
    }

    // ───────── ลิงก์รุ่นก่อน: ฐานไม่เก็บ token ดิบ (267-1-3) ─────────

    [Fact]
    public void CDP267_LegacyStored_เก็บhashไม่ใช่tokenดิบ_สูตรเดียวกับหลักฐาน_ตัวพิมพ์ไม่มีผล()
    {
        const string raw = "AB12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34";
        var stored = CustomerDocPage.LegacyStored(raw);
        Assert.StartsWith(CustomerDocPage.LegacyHashPrefix, stored);
        Assert.Equal(CustomerDocPage.LegacyHashPrefix.Length + 64, stored.Length);
        Assert.True(stored.Length <= 80);                                               // คอลัมน์ varchar(80)
        Assert.DoesNotContain(raw.ToLowerInvariant(), stored);
        Assert.Equal(stored, CustomerDocPage.LegacyStored(raw.ToLowerInvariant()));
        Assert.Equal(CustomerDocPage.LegacyHashPrefix + QuotationAcceptance.Sha256Hex(raw.ToLowerInvariant()), stored);
        // ถือ hash ที่รั่วจากฐาน ≠ ถือลิงก์: hash ของ hash ไม่ตรงค่าที่เก็บ
        Assert.NotEqual(stored, CustomerDocPage.LegacyStored(stored[CustomerDocPage.LegacyHashPrefix.Length..]));
    }

    [Fact]
    public void CDP267_LegacyMigrationSql_รันซ้ำได้_ครอบสองคอลัมน์()
    {
        Assert.Equal(2, CustomerDocPage.LegacyTokenHashSql.Length);
        Assert.Contains(CustomerDocPage.LegacyTokenHashSql, s => s.Contains("\"QuotationAcceptToken\""));
        Assert.Contains(CustomerDocPage.LegacyTokenHashSql, s => s.Contains("\"DeliverySignToken\""));
        Assert.All(CustomerDocPage.LegacyTokenHashSql, s => Assert.Contains("NOT LIKE 'h1:%'", s));   // ข้ามแถวที่ hash แล้ว
        Assert.All(CustomerDocPage.LegacyTokenHashSql, s => Assert.Contains("'" + CustomerDocPage.LegacyHashPrefix + "' ||", s));
    }

    [Fact]
    public void CDP267_LegacyRespondPath_เฉพาะชนิดที่มีหน้าตอบรับเดิม()
    {
        Assert.Equal("/pages/quotation-accept.html?token=ab12", CustomerDocPage.LegacyRespondPath(DocumentType.Quotation, "ab12"));
        Assert.Equal("/pages/delivery-sign.html?token=ab12", CustomerDocPage.LegacyRespondPath(DocumentType.DeliveryNote, "ab12"));
        Assert.Null(CustomerDocPage.LegacyRespondPath(DocumentType.Invoice, "ab12"));
    }

    // ───────── log ไม่เก็บ token ─────────

    [Fact]
    public void CDP267_RedactPath_ปิดบังtokenของหน้าเอกสารและหน้าตอบรับเดิม_pathอื่นคงเดิม()
    {
        const string t = "ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34";
        const string c = "/api/companies/3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        Assert.Equal(c + "/public-pay/documents/[redacted]", CustomerDocPage.RedactPath(c + "/public-pay/documents/" + t));
        Assert.Equal(c + "/public-pay/documents/[redacted]/pdf", CustomerDocPage.RedactPath(c + "/public-pay/documents/" + t + "/pdf"));
        Assert.Equal(c + "/public-pay/documents/[redacted]/intents/x", CustomerDocPage.RedactPath(c + "/public-pay/documents/" + t + "/intents/x"));
        Assert.Equal("/api/public/quotation/[redacted]/accept", CustomerDocPage.RedactPath("/api/public/quotation/" + t + "/accept"));
        Assert.Equal("/api/public/delivery/[redacted]", CustomerDocPage.RedactPath("/api/public/delivery/" + t));
        // ทิศตรงข้าม: path อื่นไม่ถูกแตะ
        Assert.Equal(c + "/document/list", CustomerDocPage.RedactPath(c + "/document/list"));
        Assert.Equal(c + "/public-pay/methods", CustomerDocPage.RedactPath(c + "/public-pay/methods"));
        Assert.Equal("", CustomerDocPage.RedactPath(null));
        // จุดเรียกเดียวของ middleware (GatewayWebhookRoute.RedactPath) ครอบทั้งสองชนิด
        Assert.DoesNotContain(t, GatewayWebhookRoute.RedactPath(c + "/public-pay/documents/" + t + "/slip"));
        Assert.Equal("/api/pay/webhooks/omise/[redacted]", GatewayWebhookRoute.RedactPath("/api/pay/webhooks/omise/" + t));
    }

    [Fact]
    public void CDP267_RateKeys_ไม่มีtokenตรงในกุญแจตัวนับ()
    {
        const string t = "ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34";
        var key = CustomerDocPage.RateKey("view", t, "1.2.3.4");
        Assert.DoesNotContain(t, key);
        Assert.DoesNotContain("1.2.3.4", key);
        Assert.True(key.Length <= 60, key);
        Assert.NotEqual(key, CustomerDocPage.RateKey("pdf", t, "1.2.3.4"));                 // คนละเส้นคนละถัง
        Assert.NotEqual(key, CustomerDocPage.RateKey("view", t, "5.6.7.8"));               // ต่อ (token + IP) — ลูกค้าหลัง NAT เดียวกันไม่โดนรวม
        Assert.Equal(CustomerDocPage.RateKey("view", t, null), CustomerDocPage.RateKey("view", t, " "));   // ไม่รู้ IP = ถังเดียวกัน
        // รูป token ผิด ⇒ ถังเดียวต่อ IP (ไม่สร้างแถวตัวนับจากขยะทีละค่า)
        Assert.Equal(CustomerDocPage.RateKey("view", "x", "1.2.3.4"), CustomerDocPage.RateKey("view", "../etc", "1.2.3.4"));
    }
}
