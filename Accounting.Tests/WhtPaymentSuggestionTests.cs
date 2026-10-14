using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม A (2a · 2026-10-13) — ภาษีหัก ณ ที่จ่ายตอนบันทึกจ่ายต้องหักใบลดหนี้ซื้อที่อนุมัติแล้ว (<see cref="WhtPaymentSuggestion"/>):
/// ใบแจ้งหนี้ซื้อหัก 300 · ใบลดหนี้หัก 30 ⇒ งวดสุดท้ายแนะนำ 270 (เดิมหยิบ 300 ทั้งก้อน ⇒ 50 ทวิ ออกเกิน) ·
/// ไม่มีใบลดหนี้ ⇒ ตัวเลขเดิมของ <c>CreatePaymentAsync</c> ทุกสตางค์ · ผู้ใช้ระบุสูงกว่ายอดที่เหลือ = รับ + เตือน (ไม่ล็อกตายตัว)
/// </summary>
public class WhtPaymentSuggestionTests
{
    private static readonly decimal[] NoCn = Array.Empty<decimal>();

    /// <summary>สูตรเดิมของ <c>CreatePaymentAsync</c> ก่อนรอบนี้ (คัดจาก git 789d5dd4 · DocumentService.cs บล็อก WHT ของการจ่ายใบเดี่ยว) — baseline ของเทสต์ "ไม่มีใบลดหนี้ = เดิม"</summary>
    private static decimal Legacy(decimal docWht, decimal already, decimal payeeTotal, decimal amount, decimal settleNet, bool isFinal)
    {
        var remainingCap = Math.Max(0m, docWht - already);
        if (isFinal) return remainingCap;
        var proportional = payeeTotal > 0m
            ? Math.Round((amount + settleNet) * docWht / payeeTotal, 2, MidpointRounding.AwayFromZero)
            : 0m;
        return Math.Min(proportional, remainingCap);
    }

    [Theory]
    [InlineData(300.0, 0.0, 9700.0, 4850.0, 0.0, false)]
    [InlineData(300.0, 0.0, 7500.0, 2884.62, 0.0, false)]          // 115.38 ต่องวด (ตัวอย่างในคอมเมนต์เดิม)
    [InlineData(300.0, 230.76, 7500.0, 1730.76, 0.0, true)]      // งวดสุดท้ายได้ 69.24 ให้ครบ 300 พอดี
    [InlineData(300.0, 0.0, 9700.0, 438.0, 98.0, false)]             // บรรทัดปรับ (รอบ 193) นับเป็นการปิดหนี้งวดนี้
    [InlineData(413.56, 100.0, 5908.0, 10.0, 0.0, false)]
    [InlineData(300.0, 299.99, 9700.0, 9000.0, 0.0, false)]        // สัดส่วนเกินที่เหลือ ⇒ ตัดที่เพดาน
    [InlineData(300.0, 0.0, 0.0, 100.0, 0.0, false)]                 // ตัวหาร 0 ⇒ 0
    [InlineData(300.0, 350.0, 9700.0, 100.0, 0.0, true)]             // หักเกินมาแล้ว ⇒ 0 ไม่ติดลบ
    public void ไม่มีใบลดหนี้_ยอดแนะนำเท่าสูตรเดิมทุกสตางค์(double docWht, double already, double payee, double amount, double settleNet, bool isFinal)
    {
        decimal D(double v) => (decimal)v;
        var r = WhtPaymentSuggestion.Compute(D(docWht), NoCn, D(already), D(payee), NoCn, D(amount), D(settleNet), isFinal);

        Assert.Equal(Legacy(D(docWht), D(already), D(payee), D(amount), D(settleNet), isFinal), r.Suggested);
        Assert.Equal(Math.Max(0m, D(docWht) - D(already)), r.GrossCap);
        Assert.Equal(r.GrossCap, r.NetCap);   // ไม่มีใบลดหนี้ ⇒ สองเพดานเท่ากัน ⇒ ไม่มีคำเตือนชุดใหม่ที่ผู้ใช้เคยไม่เห็น
        Assert.False(r.HasCreditNotes);
        Assert.Null(WhtPaymentSuggestion.AboveNetWarning(r, r.GrossCap));
    }

    [Fact]
    public void ใบลดหนี้ซื้อ_งวดสุดท้ายแนะนำยอดหลังหักใบลดหนี้_ไม่ใช่ยอดเต็มใบเดิม()
    {
        // ใบเดิม 10,000 หัก 3% = 300 (จ่ายผู้รับ 9,700) · ใบลดหนี้ 1,000 หัก 30 (ลดหนี้ผู้รับ 970) ⇒ ยอดค้าง 8,730
        var r = WhtPaymentSuggestion.Compute(300m, new[] { 30m }, 0m, 9_700m, new[] { 970m }, 8_730m, 0m, isFinal: true);

        Assert.Equal(270m, r.Suggested);
        Assert.Equal(270m, r.NetWht);
        Assert.Equal(270m, r.NetCap);
        Assert.Equal(300m, r.GrossCap);
        Assert.True(r.HasCreditNotes);
        Assert.Contains("ภาษีหักที่เหลือหลังใบลดหนี้ = 270.00 (ใบเดิม 300.00 − ใบลดหนี้ 30.00)", r.ReasonText);   // ถ้อยคำเดียวกับป้าย 50 ทวิ ต้องตรวจ
        Assert.Contains("ระบบแนะนำงวดนี้ 270.00", r.ReasonText);
    }

    [Fact]
    public void ใบลดหนี้ซื้อ_งวดกลางคิดสัดส่วนจากยอดสุทธิ_สองงวดรวมเท่ายอดหลังใบลดหนี้()
    {
        var first = WhtPaymentSuggestion.Compute(300m, new[] { 30m }, 0m, 9_700m, new[] { 970m }, 4_365m, 0m, isFinal: false);
        Assert.Equal(135m, first.Suggested);   // 4,365 × 270 ÷ 8,730

        var last = WhtPaymentSuggestion.Compute(300m, new[] { 30m }, first.Suggested, 9_700m, new[] { 970m }, 4_365m, 0m, isFinal: true);
        Assert.Equal(135m, last.Suggested);
        Assert.Equal(270m, first.Suggested + last.Suggested);
        Assert.Contains("หักไปแล้ว 135.00 ⇒ เหลือหักได้ 135.00", last.ReasonText);
    }

    [Fact]
    public void ใบลดหนี้ลดเกินภาษีหักของใบเดิม_แนะนำศูนย์_ไม่ติดลบ()
    {
        var r = WhtPaymentSuggestion.Compute(300m, new[] { 400m }, 0m, 9_700m, new[] { 9_700m }, 100m, 0m, isFinal: true);
        Assert.Equal(0m, r.Suggested);
        Assert.Equal(0m, r.NetCap);
        Assert.Contains("ลดเกินภาษีหักของใบเดิม 100.00", r.ReasonText);
    }

    [Fact]
    public void ผู้ใช้ระบุสูงกว่ายอดที่เหลือหลังใบลดหนี้_รับได้พร้อมคำเตือนบอกยอดแนะนำ()
    {
        var r = WhtPaymentSuggestion.Compute(300m, new[] { 30m }, 0m, 9_700m, new[] { 970m }, 8_730m, 0m, isFinal: true);

        var warn = WhtPaymentSuggestion.AboveNetWarning(r, 300m);
        Assert.NotNull(warn);
        Assert.Contains("ระบบแนะนำ 270.00 (ใบเดิม 300.00 − ใบลดหนี้ 30.00 = 270.00)", warn);
        Assert.Contains("บันทึก 300.00 ตามที่ผู้ใช้ระบุ", warn);
        // ชำระหลายใบ — บอกเลขเอกสาร
        Assert.StartsWith("PI-001: ", WhtPaymentSuggestion.AboveNetWarning(r, 300m, "PI-001"));
    }

    [Fact]
    public void เกณฑ์เงินสด_คำเตือนบอกยอดเดบิตที่ค้างในเจ้าหนี้_เกณฑ์สิทธิไม่บอก()
    {
        // ฝ่ายค้านรอบ 204 L4: Dr เจ้าหนี้ = เงิน + ภาษีหัก ⇒ หัก 300 ทั้งที่เหลือ 270 ⇒ เจ้าหนี้ติดเดบิต 30 หลังปิดยอด (ยังรับตามผู้ใช้)
        var r = WhtPaymentSuggestion.Compute(300m, new[] { 30m }, 0m, 9_700m, new[] { 970m }, 8_730m, 0m, isFinal: true);
        var cash = WhtPaymentSuggestion.AboveNetWarning(r, 300m, cashBasis: true);
        Assert.NotNull(cash);
        Assert.Contains("เจ้าหนี้ของใบนี้จะค้างยอดเดบิต 30.00", cash);
        Assert.Contains("ระบบแนะนำ 270.00", cash);
        // ทิศตรงข้าม: เกณฑ์สิทธิ (ภาษีหักลงตอนตั้งหนี้ — งวดจ่ายไม่แตะเจ้าหนี้ด้วยภาษี) ⇒ ไม่มีข้อความนี้ · ไม่เกินยอดที่เหลือ ⇒ ไม่เตือนเลย
        Assert.DoesNotContain("ค้างยอดเดบิต", WhtPaymentSuggestion.AboveNetWarning(r, 300m)!);
        Assert.Null(WhtPaymentSuggestion.AboveNetWarning(r, 270m, cashBasis: true));
    }

    [Theory]
    [InlineData(270.0)]      // เท่ายอดแนะนำ
    [InlineData(270.01)]   // ปัดเศษในเกณฑ์ 1 สตางค์
    [InlineData(200.0)]      // ต่ำกว่ายอดแนะนำ (หนังสือรับรองระบุน้อยกว่า) ⇒ รับเงียบ
    [InlineData(0.0)]
    public void ทิศตรงข้าม_ไม่เกินยอดที่เหลือ_ไม่เตือน(double entered)
    {
        var r = WhtPaymentSuggestion.Compute(300m, new[] { 30m }, 0m, 9_700m, new[] { 970m }, 8_730m, 0m, isFinal: true);
        Assert.Null(WhtPaymentSuggestion.AboveNetWarning(r, (decimal)entered));
    }

    [Fact]
    public void ใบสำคัญจ่ายแปลงมาหักเต็มใบเดิมหลังใบลดหนี้_เตือนก่อนอนุมัติพร้อมยอดที่ควรหัก()
    {
        var warn = WhtPaymentSuggestion.ApprovalWarning("PI-001", 300m, new[] { 30m }, 0m, 300m);
        Assert.NotNull(warn);
        Assert.StartsWith(WhtPaymentSuggestion.ApprovalWarningPrefix, warn);
        Assert.True(WhtPaymentSuggestion.IsApprovalWarning(warn!));
        Assert.Contains("ภาษีหักที่เหลือหลังใบลดหนี้ = 270.00 (ใบเดิม 300.00 − ใบลดหนี้ 30.00)", warn);
        Assert.Contains("ระบบแนะนำให้ใบนี้หัก 270.00", warn);

        // งวดที่สองของใบที่จ่ายบางส่วนแล้ว
        var second = WhtPaymentSuggestion.ApprovalWarning("PI-001", 300m, new[] { 30m }, 135m, 165m);
        Assert.Contains("+ ที่หักไปแล้ว 135.00 = 300.00", second);
        Assert.Contains("ระบบแนะนำให้ใบนี้หัก 135.00", second);
    }

    [Fact]
    public void ทิศตรงข้าม_ใบสำคัญจ่ายหักตามยอดสุทธิหรือไม่มีใบลดหนี้_ไม่เตือน()
    {
        Assert.Null(WhtPaymentSuggestion.ApprovalWarning("PI-001", 300m, new[] { 30m }, 0m, 270m));
        Assert.Null(WhtPaymentSuggestion.ApprovalWarning("PI-001", 300m, new[] { 30m }, 135m, 135m));
        Assert.Null(WhtPaymentSuggestion.ApprovalWarning("PI-001", 300m, NoCn, 0m, 300m));        // ไม่มีใบลดหนี้ = ไม่ใช่เรื่องของด่านนี้
        Assert.Null(WhtPaymentSuggestion.ApprovalWarning("PI-001", 300m, new[] { 0m }, 0m, 300m)); // ใบลดหนี้ไม่แตะภาษีหัก
        Assert.Null(WhtPaymentSuggestion.ApprovalWarning("PI-001", 300m, new[] { 30m }, 0m, 0m));  // ใบนี้ไม่หัก
    }

    [Fact]
    public void คำเตือนก่อนอนุมัติชุดใหม่_ทางเข้าไม่มีคนผ่านได้_เว็บยังหยุดให้รับทราบ_อ้างมาตราหักณที่จ่าย()
    {
        var warn = WhtPaymentSuggestion.ApprovalWarning("PI-001", 300m, new[] { 30m }, 0m, 300m)!;
        var list = new[] { warn };

        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, list));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, list));
        Assert.Equal(list, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.None, list).ToArray());   // เว็บ/มือถือยังแสดงให้กดรับทราบ
        Assert.Equal(ApprovalAcknowledgement.WhtWarningsLegalReference, ApprovalAcknowledgement.LegalReference(list));
    }

    [Fact]
    public void ป้ายของทีมคู่ขนานลงทะเบียนล่วงหน้า_ทางเข้าไม่มีคนผ่านได้_ป้ายอื่นยังหยุด()
    {
        var pp36 = new[] { "[PP36-BASE] ฐาน ภ.พ.36 ทดสอบ" };
        var whtForm = new[] { "[WHT-FORM] แบบยื่นทดสอบ" };
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, pp36));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, whtForm));
        Assert.Equal("RD-83/6", ApprovalAcknowledgement.LegalReference(pp36));
        Assert.Equal(ApprovalAcknowledgement.WhtWarningsLegalReference, ApprovalAcknowledgement.LegalReference(whtForm));

        // ทิศตรงข้าม: คำเตือนทั่วไป (และป้ายที่แค่ "มีคำ" อยู่กลางข้อความ) ยังหยุดทางเข้าไม่มีคนเหมือนเดิม
        var general = new[] { "ยอดรวมเอกสาร 600,000.00 THB — ตรวจตัวเลขก่อนยืนยัน", "ข้อความที่มี [WHT-CN-NET] กลางประโยค" };
        Assert.Equal(general, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.Unattended, general).ToArray());
    }

    [Fact]
    public void ป้าย50ทวิต้องตรวจ_บอกว่าระบบเติมยอดแนะนำให้แล้ว_ไม่ใช่ระบบยังไม่หักให้เอง()
    {
        var f = WhtCreditNoteRemainder.Compute(300m, new[] { 30m }, 135m);
        var before = WhtCreditNoteRemainder.BeforeAnyCertReason("p", WhtCreditNoteRemainder.Compute(300m, new[] { 30m }));
        var partly = WhtCreditNoteRemainder.PartlyPaidReason("p", f);
        foreach (var text in new[] { before, partly })
        {
            Assert.Contains("(ระบบเติมยอดแนะนำให้แล้ว แก้ได้)", text);
            Assert.DoesNotContain("ระบบยังไม่หักให้เอง", text);
        }
    }
}
