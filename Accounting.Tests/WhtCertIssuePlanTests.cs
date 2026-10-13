using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คำขอเจ้าของ 2026-10-12 — "ตอนทำใบสำคัญจ่าย ถ้าใบนั้นเลือกหัก ณ ที่จ่ายและอนุมัติ ระบบสร้างใบให้อัตโนมัติ ·
/// ให้มีช่องติ๊กว่าสร้างอัตโนมัติ · บางกรณีชื่อบนใบสำคัญจ่ายกับใบหัก ณ ที่จ่ายคนละชื่อ (ประกันผ่านโบรกเกอร์)"
/// ตัวตัดสินเดียว <see cref="WhtCertIssuePlan"/> + ป้าย <see cref="WhtCertAutoFailedNote"/> — ทั้งทิศ "ออก" และทิศ "ไม่ออก"
/// </summary>
public class WhtCertIssuePlanTests
{
    private static readonly Guid Broker = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Insurer = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Pay1 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static WhtCertIssueFacts F(
        WhtCertIssueTrigger trigger = WhtCertIssueTrigger.Approve,
        DocumentType type = DocumentType.PaymentVoucher,
        DocumentStatus status = DocumentStatus.Paid,
        decimal wht = 300m, bool auto = true, Guid? payee = null,
        bool paid = true, bool perPayment = false, int samePayment = 0, int fullIssued = 0, int any = 0)
        => new(trigger, type, status, wht, auto, Broker, payee, paid, perPayment, samePayment, fullIssued, any);

    // ── ค่าตั้งต้น = พฤติกรรมเดิม (ติ๊กเปิด) ──

    [Fact]
    public void ค่าตั้งต้นเปิด_อนุมัติใบที่จ่ายแล้ว_ออกใบทันที_ในนามผู้ติดต่อ()
    {
        var d = WhtCertIssuePlan.Decide(F());
        Assert.Equal(WhtCertIssueAction.IssueNow, d.Action);
        Assert.Equal(Broker, d.PayeeContactId);
    }

    [Fact]
    public void อนุมัติใบที่ยังไม่จ่าย_รอวันจ่าย_ไม่ออกตอนอนุมัติ()
        => Assert.Equal(WhtCertIssueAction.WaitForPayment,
            WhtCertIssuePlan.Decide(F(type: DocumentType.PurchaseInvoice, status: DocumentStatus.Approved, paid: false)).Action);

    [Fact]
    public void บันทึกจ่ายรายงวด_ออกใบของงวดนั้น()
        => Assert.Equal(WhtCertIssueAction.IssueNow,
            WhtCertIssuePlan.Decide(F(WhtCertIssueTrigger.Payment, perPayment: true, any: 1)).Action);

    // ── ผู้ถูกหักแยกจากผู้รับเงิน (ประกันผ่านโบรกเกอร์) ──

    [Fact]
    public void ผู้ถูกหักแยก_ใบออกในนามบริษัทประกัน_ไม่ใช่โบรกเกอร์()
        => Assert.Equal(Insurer, WhtCertIssuePlan.Decide(F(payee: Insurer)).PayeeContactId);

    [Fact]
    public void PayeeOf_ไม่ตั้งหรือGuidEmpty_คือผู้ติดต่อของเอกสาร()
    {
        Assert.Equal(Broker, WhtCertIssuePlan.PayeeOf(Broker, null));
        Assert.Equal(Broker, WhtCertIssuePlan.PayeeOf(Broker, Guid.Empty));
        Assert.Equal(Insurer, WhtCertIssuePlan.PayeeOf(Broker, Insurer));
    }

    [Fact]
    public void NormalizeOverride_ซ้ำผู้ติดต่อหรือว่าง_เก็บเป็นnull()
    {
        Assert.Null(WhtCertIssuePlan.NormalizeOverride(Broker, Broker));
        Assert.Null(WhtCertIssuePlan.NormalizeOverride(Broker, Guid.Empty));
        Assert.Null(WhtCertIssuePlan.NormalizeOverride(Broker, null));
        Assert.Equal(Insurer, WhtCertIssuePlan.NormalizeOverride(Broker, Insurer));
    }

    // ── ปิดติ๊ก "สร้างอัตโนมัติ" ──

    [Theory]
    [InlineData(WhtCertIssueTrigger.Approve)]
    [InlineData(WhtCertIssueTrigger.Payment)]
    [InlineData(WhtCertIssueTrigger.IntegrationSync)]
    [InlineData(WhtCertIssueTrigger.Bulk)]
    public void ปิดติ๊ก_ทางอัตโนมัติทุกทาง_ไม่ออก_รอในลิสต์(WhtCertIssueTrigger trigger)
        => Assert.Equal(WhtCertIssueAction.ManualPending, WhtCertIssuePlan.Decide(F(trigger, auto: false)).Action);

    [Fact]
    public void ปิดติ๊ก_แต่ผู้ใช้กดสร้างใบเอง_ออกได้()   // ทิศตรงข้าม: ปิดอัตโนมัติ ≠ ห้ามออก
        => Assert.Equal(WhtCertIssueAction.IssueNow, WhtCertIssuePlan.Decide(F(WhtCertIssueTrigger.Manual, auto: false)).Action);


    // ── ไม่เกี่ยว / ซ้ำ ──

    [Fact]
    public void ไม่มีWHT_หรือฝั่งขาย_หรือยกเลิก_ไม่เกี่ยว()
    {
        Assert.Equal(WhtCertIssueAction.NotApplicable, WhtCertIssuePlan.Decide(F(wht: 0m)).Action);
        Assert.Equal(WhtCertIssueAction.NotApplicable, WhtCertIssuePlan.Decide(F(type: DocumentType.Invoice)).Action);
        Assert.Equal(WhtCertIssueAction.NotApplicable, WhtCertIssuePlan.Decide(F(status: DocumentStatus.Voided)).Action);
    }

    [Fact]
    public void มีใบแล้ว_ไม่ออกซ้ำ_และไม่นับเป็นความล้มเหลว()
    {
        Assert.Equal(WhtCertIssueAction.AlreadyIssued, WhtCertIssuePlan.Decide(F(any: 1)).Action);
        Assert.Equal(WhtCertIssueAction.AlreadyIssued,
            WhtCertIssuePlan.Decide(F(WhtCertIssueTrigger.Payment, perPayment: true, samePayment: 1, any: 1)).Action);
        // ใบเต็มจำนวนที่ออกจริงแล้ว ⇒ ใบรายงวดไม่ออกทับ (กันนำส่งซ้ำ)
        Assert.Equal(WhtCertIssueAction.AlreadyIssued,
            WhtCertIssuePlan.Decide(F(WhtCertIssueTrigger.Payment, perPayment: true, fullIssued: 1, any: 1)).Action);
    }

    // ── เลขผู้เสียภาษีของผู้ถูกหัก ──

    [Fact]
    public void เลขผู้ถูกหัก_ขาดหรือหลักท้ายผิด_ออกไม่ได้_ต่างประเทศยกเว้น()
    {
        Assert.Null(WhtCertIssuePlan.PayeeTaxIdProblem("บริษัท ประกันภัย จำกัด", "0107537000017", foreignPayee: false));
        Assert.Contains("13 หลัก", WhtCertIssuePlan.PayeeTaxIdProblem("นาย ก", null, foreignPayee: false));
        Assert.Contains("หลักท้าย", WhtCertIssuePlan.PayeeTaxIdProblem("บริษัท ก", "0107537000018", foreignPayee: false));
        Assert.Null(WhtCertIssuePlan.PayeeTaxIdProblem("Google Asia Pacific", null, foreignPayee: true));
    }

    // ── ป้าย "ออกอัตโนมัติไม่สำเร็จ" ──

    [Fact]
    public void ป้ายล้มเหลว_เขียนอ่านล้าง_ไม่สะสมและไม่แตะหมายเหตุอื่น()
    {
        var notes = WhtCertAutoFailedNote.Append("หมายเหตุเดิม", "เหตุ ก");
        notes = WhtCertAutoFailedNote.Append(notes, "เหตุ ข");
        Assert.Equal("เหตุ ข", WhtCertAutoFailedNote.ReasonOf(notes));
        Assert.Equal(1, notes.Split(WhtCertAutoFailedNote.Marker).Length - 1);
        Assert.Equal("หมายเหตุเดิม", WhtCertAutoFailedNote.Clear(notes));
        Assert.Null(WhtCertAutoFailedNote.ReasonOf("หมายเหตุเดิม"));
        Assert.Equal("หมายเหตุเดิม", WhtCertAutoFailedNote.Clear("หมายเหตุเดิม"));
    }

    [Fact]
    public void ลิสต์รอออกใบ_บอกเหตุที่ยังไม่มีใบ()
    {
        Assert.StartsWith("ออกอัตโนมัติไม่สำเร็จ", WhtCertIssuePlan.PendingLabel(true, "ไม่มีเลข", paid: true));
        Assert.Contains("ออกใบเอง", WhtCertIssuePlan.PendingLabel(false, null, paid: true));
        Assert.Contains("รอจ่าย", WhtCertIssuePlan.PendingLabel(true, null, paid: false));
    }

    // ── คำเตือนก่อนอนุมัติเรื่องผู้ถูกหัก: ทางที่ไม่มีคนเห็นต้องไม่ถูกหยุด (ใบประจำ/API เคยอนุมัติผ่าน) ──

    [Fact]
    public void คำเตือนผู้ถูกหัก_ทางไม่มีคน_ผ่าน_เว็บยังหยุด()
    {
        var w = new[] { WhtCertIssuePlan.ApprovalWarningPrefix + " ผู้ถูกหักภาษี \"นาย ก\" ยังไม่มีเลขประจำตัวผู้เสียภาษี 13 หลัก" };
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, w));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, w));
        Assert.Single(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.None, w));
    }

    // ── ฝ่ายค้านรอบ 204 (หลัง 0f428969): ผู้ถูกหักที่สืบทอดถูกตัดทิ้ง (ป้ายต้องตรวจยังไม่รับทราบ) ⇒ ไม่ออกอัตโนมัติ ──

    [Theory]
    [InlineData(WhtCertIssueTrigger.Approve)]
    [InlineData(WhtCertIssueTrigger.Payment)]
    [InlineData(WhtCertIssueTrigger.IntegrationSync)]
    [InlineData(WhtCertIssueTrigger.Bulk)]
    public void ผู้ถูกหักต้องตรวจ_ทางอัตโนมัติไม่ออก_รอในลิสต์(WhtCertIssueTrigger trigger)
    {
        var d = WhtCertIssuePlan.Decide(F(trigger) with { PayeeReviewPending = true });
        Assert.Equal(WhtCertIssueAction.ManualPending, d.Action);
        Assert.Contains("รับทราบ", d.Reason);
    }

    [Fact]
    public void ผู้ถูกหักต้องตรวจ_ทิศตรงข้าม_กดทีละใบออกได้_ไม่มีป้ายออกตามเดิม_มีใบแล้วไม่ออกซ้ำ()
    {
        Assert.Equal(WhtCertIssueAction.IssueNow,
            WhtCertIssuePlan.Decide(F(WhtCertIssueTrigger.Manual) with { PayeeReviewPending = true }).Action);   // คนตัดสินแล้ว
        Assert.Equal(WhtCertIssueAction.IssueNow, WhtCertIssuePlan.Decide(F()).Action);                       // ค่าตั้งต้น = พฤติกรรมเดิม
        Assert.Equal(WhtCertIssueAction.AlreadyIssued,
            WhtCertIssuePlan.Decide(F(any: 1) with { PayeeReviewPending = true }).Action);
    }
}
