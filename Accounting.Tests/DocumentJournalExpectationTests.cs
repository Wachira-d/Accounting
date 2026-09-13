using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "ใบนี้ควรมีรายการในสมุดรายวันไหม" — ตัวตัดสินตัวเดียวของทั้งเส้นอนุมัติ
/// (<c>ApproveDocumentAsync</c>) และเส้นแสดงผล (<c>LoadGlPostingAsync</c>)
///
/// ที่มา (ผู้ใช้ถาม 2026-09-13): "ถ้ายังไม่อนุมัติ ได้เลขเอกสารมาได้ไง" — ถูกต้อง
/// ตอนสร้างเอกสารได้ <c>DRAFT-{guid}</c> เสมอ เลขจริงออกตอนอนุมัติเท่านั้น
/// (§86/4 gap-free) ⇒ ใบที่มีเลข <c>PV-20260801-0003</c> ผ่านการอนุมัติแน่นอน
/// แต่กล่อง "การบันทึกบัญชี" กลับโชว์ "(ประมาณการ — ก่อนอนุมัติ)" เพราะป้ายนั้น
/// ถูกติดให้ทุกกรณีที่หา JE ไม่เจอ **โดยไม่เคยตรวจสถานะเอกสารเลย**
///
/// เทสต์ล็อก **สองทิศ** ตามกฎ H ของ CLAUDE.md:
/// (1) ใบที่ควรมี JE แต่ไม่มี ต้องถูกจับ — นี่คืออาการหนักที่สุด (ภ.พ.30 นับแล้ว
///     แต่ GL ว่าง) ที่เดิมเงียบสนิท
/// (2) ใบที่ **ถูกต้องแล้วที่ไม่มี JE** ต้องไม่ถูกจับ — ไม่งั้นจะกลายเป็นคำเตือน
///     ที่ฟ้องใบถูกทุกใบ ซึ่งไฟล์นี้บันทึกไว้ว่า = การปิดด่านโดยไม่ได้ตั้งใจ
/// </summary>
public class DocumentJournalExpectationTests
{
    // ───────── ทิศที่ 1: ต้องจับได้ ─────────

    [Theory]
    [InlineData(DocumentType.PaymentVoucher)]   // ใบของผู้ใช้ที่รายงานมา
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.ReceiptVoucher)]
    [InlineData(DocumentType.CreditNote)]
    [InlineData(DocumentType.DebitNote)]
    [InlineData(DocumentType.CertificateInLieu)]
    [InlineData(DocumentType.GoodsReceiptNote)]
    public void เอกสารที่ลงบัญชีอัตโนมัติ_เมื่ออนุมัติแล้ว_ต้องมี_JE(DocumentType type)
    {
        Assert.True(DocumentJournalExpectation.PostsToJournal(type));
        Assert.True(DocumentJournalExpectation.ExpectsLiveJournal(
            type, DocumentStatus.Approved,
            isSettlementReceipt: false, replacesAnotherDocument: false));
    }

    [Theory]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Sent)]
    [InlineData(DocumentStatus.PartiallyPaid)]
    [InlineData(DocumentStatus.Paid)]
    [InlineData(DocumentStatus.Overdue)]
    public void สถานะเดินต่อหลังอนุมัติ_ยังต้องมี_JE(DocumentStatus status)
    {
        // เขียนด่านเป็น "ห้ามสถานะไหน" ไม่ใช่ "== Approved" เป๊ะ ๆ — สถานะไหลไป
        // ข้างหน้าเองทันทีที่ส่งอีเมล/รับเงิน แล้วใบที่ GL ว่างจะหลุดด่านไปเงียบ ๆ
        Assert.True(DocumentJournalExpectation.ExpectsLiveJournal(
            DocumentType.PaymentVoucher, status,
            isSettlementReceipt: false, replacesAnotherDocument: false));
    }

    // ───────── ทิศที่ 2: ห้ามจับ (ไม่งั้นเตือนใบถูกทุกใบ) ─────────

    [Theory]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.BillingNote)]
    [InlineData(DocumentType.PurchaseOrder)]
    [InlineData(DocumentType.PurchaseRequisition)]
    [InlineData(DocumentType.DeliveryNote)]
    public void เอกสารเชิงปฏิบัติการ_ไม่มี_JE_คือถูกต้อง(DocumentType type)
    {
        // ใบเสนอราคา/ใบวางบิลอยู่ในชุดที่พรีวิว GL วาดให้ (BuildProjectedGlAsync)
        // แต่ **ไม่อยู่** ในชุดที่ระบบ post ⇒ ถ้าไม่กันไว้ ใบพวกนี้จะขึ้นคำเตือน
        // "อนุมัติแล้วแต่ GL ว่าง" ทุกใบตลอดกาล
        Assert.False(DocumentJournalExpectation.PostsToJournal(type));
        Assert.False(DocumentJournalExpectation.ExpectsLiveJournal(
            type, DocumentStatus.Approved,
            isSettlementReceipt: false, replacesAnotherDocument: false));
    }

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.WaitingApproval)]
    [InlineData(DocumentStatus.Rejected)]
    public void ยังไม่ออกเป็นเอกสารจริง_ไม่มี_JE_คือถูกต้อง(DocumentStatus status)
    {
        // นี่คือเคสที่ป้าย "(ประมาณการ — ก่อนอนุมัติ)" เดิมพูดถูก — ต้องคงไว้
        Assert.False(DocumentJournalExpectation.ExpectsLiveJournal(
            DocumentType.PaymentVoucher, status,
            isSettlementReceipt: false, replacesAnotherDocument: false));
    }

    [Fact]
    public void ใบที่ยกเลิกแล้ว_JE_ถูกกลับรายการเป็นเรื่องปกติ_ห้ามเตือน()
    {
        Assert.False(DocumentJournalExpectation.ExpectsLiveJournal(
            DocumentType.PaymentVoucher, DocumentStatus.Voided,
            isSettlementReceipt: false, replacesAnotherDocument: false));
    }

    [Fact]
    public void ใบเสร็จหลักฐานรับเงิน_การเงินอยู่ที่_Payment_แล้ว_ห้ามเตือน()
    {
        // approve ข้าม AutoPost ให้ใบนี้โดยตั้งใจ (post ซ้ำ = รับเงินเบิ้ล)
        Assert.False(DocumentJournalExpectation.ExpectsLiveJournal(
            DocumentType.Receipt, DocumentStatus.Approved,
            isSettlementReceipt: true, replacesAnotherDocument: false));
    }

    [Fact]
    public void ใบกำกับเต็มรูปที่ออกแทนใบเดิม_ห้ามเตือน()
    {
        // เศรษฐกิจของรายการไม่เปลี่ยน — post ซ้ำ = รายได้/ภาษีขายเบิ้ล
        Assert.False(DocumentJournalExpectation.ExpectsLiveJournal(
            DocumentType.TaxInvoice, DocumentStatus.Approved,
            isSettlementReceipt: false, replacesAnotherDocument: true));
    }

    [Fact]
    public void ลิสต์ชนิดที่_post_ต้องตรงกับด่านในเส้นอนุมัติ()
    {
        // ล็อกจำนวนไว้ — ใครเพิ่มชนิดใหม่ต้องตั้งใจแก้เทสต์นี้ด้วย ไม่ใช่เพิ่ม
        // ลิสต์ฝั่งเดียวแล้วอีกฝั่งเงียบ (สำเนามือ = drift แน่นอน)
        Assert.Equal(11, DocumentJournalExpectation.PostingTypes.Length);
        Assert.Equal(DocumentJournalExpectation.PostingTypes.Length,
            DocumentJournalExpectation.PostingTypes.Distinct().Count());
    }
}
