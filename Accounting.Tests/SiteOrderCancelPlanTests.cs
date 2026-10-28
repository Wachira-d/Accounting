using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 205 W2 (R-07 · S-3) — ยกเลิกออเดอร์: ใบที่มีการชำระผูก ⇒ เสนอใบลดหนี้ร่าง (§86/10) · ใบร่าง/ไม่มีชำระ ⇒ void เส้นเดิม · ใบตายแล้ว ⇒ ไม่ทำอะไร</summary>
public class SiteOrderCancelPlanTests
{
    [Theory]
    [InlineData(false, null, false, SiteOrderCancelErpAction.NothingToDo)]
    [InlineData(true, DocumentStatus.Voided, true, SiteOrderCancelErpAction.NothingToDo)]
    [InlineData(true, DocumentStatus.Rejected, false, SiteOrderCancelErpAction.NothingToDo)]
    [InlineData(true, DocumentStatus.Draft, false, SiteOrderCancelErpAction.VoidDocument)]
    [InlineData(true, DocumentStatus.Draft, true, SiteOrderCancelErpAction.VoidDocument)]          // ร่างไม่มี JE — void แม้มีแถวชำระหลง
    [InlineData(true, DocumentStatus.WaitingApproval, false, SiteOrderCancelErpAction.VoidDocument)]
    [InlineData(true, DocumentStatus.Approved, false, SiteOrderCancelErpAction.VoidDocument)]      // ออกแล้วแต่ยังไม่มีเงิน = เส้นเดิม
    [InlineData(true, DocumentStatus.Sent, false, SiteOrderCancelErpAction.VoidDocument)]
    [InlineData(true, DocumentStatus.Approved, true, SiteOrderCancelErpAction.ProposeCreditNote)]  // มีการชำระผูก
    [InlineData(true, DocumentStatus.Paid, false, SiteOrderCancelErpAction.ProposeCreditNote)]
    [InlineData(true, DocumentStatus.PartiallyPaid, false, SiteOrderCancelErpAction.ProposeCreditNote)]
    public void Decide_ตามสถานะและการชำระ(bool hasDoc, DocumentStatus? status, bool hasPayments, SiteOrderCancelErpAction expected)
        => Assert.Equal(expected, SiteOrderCancelPlan.Decide(hasDoc, status, hasPayments));

    [Fact]
    public void เหตุผลใบลดหนี้ร่าง_เป็นปรับยอด_ไม่ใช่คืนสินค้าที่คืนสต๊อก()
        => Assert.Equal(CreditNoteReason.Adjustment, SiteOrderCancelPlan.DraftReason);

    [Fact]
    public void หมายเหตุบนออเดอร์_บอกใบลดหนี้_และรายการชำระที่ต้องคืน()
    {
        var ids = new[] { Guid.Parse("11111111-2222-3333-4444-555555555555") };
        var note = SiteOrderCancelPlan.ProposedNote(new DateTime(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc), "CN-2026-0007", "INV-2026-0042", 1070m, ids, 0m);
        Assert.StartsWith(SiteOrderCancelPlan.CreditNoteDraftTag, note);
        Assert.Contains("CN-2026-0007", note);
        Assert.Contains("INV-2026-0042", note);
        Assert.Contains("ยังไม่อนุมัติ", note);
        Assert.Contains("11111111", note);
    }

    [Fact]
    public void หมายเหตุ_ไม่มีรายการgateway_แต่มีเงินโอน_บอกคืนด้วยมือ()
    {
        var note = SiteOrderCancelPlan.ProposedNote(DateTime.UtcNow, "CN-1", "INV-1", 500m, Array.Empty<Guid>(), 500m);
        Assert.Contains("คืนเงินโอน 500.00", note);
    }

    [Fact]
    public void DraftNotes_อ้างใบเดิมและวันที่_พร้อมเหตุผลลูกค้า()
    {
        var n = SiteOrderCancelPlan.DraftNotes("SO-9", "INV-5", new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc), "เปลี่ยนใจ");
        Assert.Contains("SO-9", n);
        Assert.Contains("INV-5", n);
        Assert.Contains("01/10/2026", n);
        Assert.Contains("เปลี่ยนใจ", n);
        Assert.Contains("คืนเงินลูกค้าแล้วจึงอนุมัติ", n);
    }
}
