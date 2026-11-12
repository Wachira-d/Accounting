using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 264 P5 (team-264-a2b-design §6.3 · C-02 · C-10 · B8/B10/B12 · คำตัดสิน 264-5-1…264-5-5) — ตัวตัดสิน pure ของขากลับ 50 ทวิ ระหว่างคู่ค้า NextAcc:
/// ที่มาพิสูจน์ได้ ⇒ ยืนยันโดยไม่มีคำตอบ AI · ยอด/เลขภาษีถูกแก้ ⇒ ด่าน · ตรวจก่อนส่ง (ผูกระบบอื่น = เหตุ · ที่อยู่ว่าง · ผู้รับไม่ตรง) · แถว Autopilot ม.82/3 · ทุกด่านมีทิศตรงข้าม
/// </summary>
public class WhtCertProvenanceRound264Tests
{
    private const string Us = "0105556123453";
    private const string Other = "0105556987652";
    private static readonly DateTime CertDate = new(2026, 10, 30);

    private static WhtCertIntakeVerdict.ProvenanceFacts Facts(
        bool accepted = true, int live = 1, bool voided = false,
        decimal srcTax = 300m, decimal srcIncome = 10_000m, decimal srcRate = 3m, string? srcPayee = Us, WhtPayerFormType? srcForm = WhtPayerFormType.Pnd53,
        DateTime? srcDate = null,
        decimal subTax = 300m, decimal subIncome = 10_000m, decimal subRate = 3m, string? subPayee = Us, WhtPayerFormType subForm = WhtPayerFormType.Pnd53,
        DateTime? subDate = null, bool linked = true)
        => new(accepted, live, voided,
            live == 1 ? new WhtCertIntakeVerdict.SourceCertFacts(srcIncome, srcTax, srcRate, srcDate ?? CertDate, srcForm, srcPayee) : null,
            Us, new WhtCertIntakeVerdict.SubmittedCertFacts(subPayee, subIncome, subTax, subRate, subDate ?? CertDate, subForm), linked);

    [Fact]
    public void M1_ผู้จ่ายบนแถวรอใบในสมุดเราไม่ได้ผูกกับคู่ค้า_เป็นปัญหา_ทิศตรงข้ามผูกแล้วผ่าน()
    {
        Assert.Contains(WhtCertIntakeVerdict.ProvenanceProblems(Facts(linked: false)), x => x.Contains("ยังไม่ได้ผูก"));
        Assert.Empty(WhtCertIntakeVerdict.ProvenanceProblems(Facts(linked: true)));
    }

    [Fact]
    public void L3_ขอบเขตsql_ครอบทุกใบที่ตัวตัดสินแสดง()
    {
        var now = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);
        var (from, before, expBefore) = PartnerTaxEvidenceAlerts.QueryBounds(now);
        foreach (var d in Enumerable.Range(0, 400).Select(i => new DateTime(2025, 10, 1).AddDays(i)))
        {
            var w = PartnerTaxEvidenceAlerts.InputVatWindowOf(d, now);
            if (w.State == PartnerTaxEvidenceAlerts.WindowState.Approaching) Assert.True(d >= from && d < before, d.ToString("yyyy-MM-dd"));
            if (w.State == PartnerTaxEvidenceAlerts.WindowState.Expired) Assert.True(d < expBefore, d.ToString("yyyy-MM-dd"));
        }
    }

    // ═══════════ C-02: ที่มาพิสูจน์ได้ ⇒ ยืนยันโดยไม่มีคำตอบ AI ═══════════

    [Fact]
    public void ที่มาตรงทุกช่อง_ไม่มีปัญหา_ตัดสินยืนยันอัตโนมัติด้วยคำตอบจากที่มา_ไม่ต้องมีAI()
    {
        Assert.Empty(WhtCertIntakeVerdict.ProvenanceProblems(Facts()));
        // ผู้เรียกตั้งคำตอบจากที่มา (ไม่ใช่ AI/นักเรียน) แล้วใช้ตัวตัดสินเดิม — ไม่มี orchestrator ในสายนี้
        Assert.Equal(WhtCertIntakeVerdict.Outcome.AutoConfirm, WhtCertIntakeVerdict.Decide(WhtCertMatchLevel.Exact, Array.Empty<string>(),
            WhtCertIntakeVerdict.Genuine, WhtCertIntakeVerdict.ProvenanceConfidence, answerUsable: true));
        Assert.Equal(WhtCertIntakeVerdict.ProvenanceLabel,
            WhtCertIntakeVerdict.AuthenticityLabel(WhtCertIntakeVerdict.Genuine, 1m, usedAi: false, byProvenance: true));
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่ใช่ที่มา_ป้ายเดิม_ใบปิดAIยังรอคน()
    {
        // ใบอัปโหลด (ไม่ใช่ช่องคู่ค้า) ไม่มีคำตอบ ⇒ รอคนเหมือนเดิม · ป้ายนักเรียนไม่กลายเป็นป้ายที่มา
        Assert.Equal(WhtCertIntakeVerdict.Outcome.AwaitUser,
            WhtCertIntakeVerdict.Decide(WhtCertMatchLevel.Exact, Array.Empty<string>(), null, null, answerUsable: false));
        Assert.StartsWith("⚙️ ผู้จ่ายรายนี้ยืนยันแล้ว", WhtCertIntakeVerdict.AuthenticityLabel(WhtCertIntakeVerdict.Genuine, 0.9m, usedAi: false, 2));
        Assert.StartsWith("🤖", WhtCertIntakeVerdict.AuthenticityLabel(WhtCertIntakeVerdict.Genuine, 0.9m, usedAi: true, byProvenance: true));
    }

    [Theory]
    [InlineData(299.99, 10_000, "ยอดภาษี")]
    [InlineData(300, 10_001, "ยอดเงินได้")]
    public void ยอดถูกแก้ระหว่างทาง_เป็นปัญหา_ไม่ยืนยัน(double subTax, double subIncome, string expected)
    {
        var p = WhtCertIntakeVerdict.ProvenanceProblems(Facts(subTax: (decimal)subTax, subIncome: (decimal)subIncome));
        Assert.Contains(p, x => x.Contains(expected));
        Assert.Equal(WhtCertIntakeVerdict.Outcome.AwaitUser, WhtCertIntakeVerdict.Decide(WhtCertMatchLevel.Exact,
            p.Select(x => WhtCertIntakeVerdict.ProvenanceGatePrefix + x).ToList(), WhtCertIntakeVerdict.Genuine, 1m, true));
    }

    [Fact]
    public void เลขภาษีผู้ถูกหักไม่ใช่เรา_ทั้งต้นทางและที่ส่งมา_เป็นปัญหา()
    {
        Assert.Contains(WhtCertIntakeVerdict.ProvenanceProblems(Facts(srcPayee: Other, subPayee: Other)), x => x.Contains("ไม่ตรงเลขของบริษัทเรา"));
        Assert.Contains(WhtCertIntakeVerdict.ProvenanceProblems(Facts(subPayee: Other)), x => x.Contains("ที่ส่งมาไม่ตรง"));
    }

    [Fact]
    public void อัตรา_วันที่_แบบ_ไม่ตรง_เป็นปัญหาแต่ละข้อ()
    {
        Assert.Contains(WhtCertIntakeVerdict.ProvenanceProblems(Facts(subRate: 5m)), x => x.Contains("อัตรา"));
        Assert.Contains(WhtCertIntakeVerdict.ProvenanceProblems(Facts(subDate: CertDate.AddDays(-1))), x => x.Contains("วันที่"));
        Assert.Contains(WhtCertIntakeVerdict.ProvenanceProblems(Facts(subForm: WhtPayerFormType.Pnd3)), x => x.Contains("ภ.ง.ด.3/53"));
        // ทิศตรงข้าม: ต่างไม่เกินครึ่งสตางค์ (ปัดเศษ) ไม่ใช่ปัญหา · เวลาในวันเดียวกันไม่ใช่ปัญหา
        Assert.Empty(WhtCertIntakeVerdict.ProvenanceProblems(Facts(subTax: 300.004m, subDate: CertDate.AddHours(15))));
    }

    [Fact]
    public void ผู้จ่ายยกเลิก_ไม่พบ_กำกวม_คู่ไม่ได้เชื่อม_เป็นปัญหาพร้อมเหตุของตัวเอง()
    {
        Assert.Contains(WhtCertIntakeVerdict.ProvenanceProblems(Facts(live: 0, voided: true)), x => x.Contains("ยกเลิก"));
        Assert.Contains(WhtCertIntakeVerdict.ProvenanceProblems(Facts(live: 0)), x => x.Contains("ไม่พบ"));
        Assert.Contains(WhtCertIntakeVerdict.ProvenanceProblems(Facts(live: 2)), x => x.Contains("มากกว่าหนึ่ง"));
        Assert.Contains(WhtCertIntakeVerdict.ProvenanceProblems(Facts(accepted: false)), x => x.Contains("คู่ค้า"));
    }

    // ═══════════ C-10 · B12 · ผู้รับ: ตรวจก่อนส่ง ═══════════

    private static PartnerExchangeRules.WhtDeliveryFacts D(string? system = PartnerExchangeRules.NextAccTenantSystem, bool linkedAccepted = true,
        bool taxMatch = false, string? contactTax = Us, string? contactBranch = "00000", string? partnerTax = Us, string? partnerBranch = "00000",
        bool address = true, bool allowed = true)
        => new(system, linkedAccepted, taxMatch, contactTax, contactBranch, partnerTax, partnerBranch, address, allowed);

    [Fact]
    public void ครบทุกข้อ_ส่ง()
        => Assert.Equal(PartnerExchangeRules.WhtDeliveryAction.Send, PartnerExchangeRules.WhtDeliveryCheckOf(D()).Action);

    [Fact]
    public void C10_ผูกระบบอื่นแต่เลขตรงคู่ค้า_มีเหตุบอกระบบ_ไม่ข้ามเงียบ()
    {
        var c = PartnerExchangeRules.WhtDeliveryCheckOf(D(system: "Peak", linkedAccepted: false, taxMatch: true));
        Assert.Equal(PartnerExchangeRules.WhtDeliveryAction.Skip, c.Action);
        Assert.Contains("ผูกกับระบบ Peak", c.Reason);
        Assert.Contains("หน้าผู้ติดต่อ", c.Reason);
        // ยังไม่ผูกอะไรเลยแต่เลขตรงคู่ค้า ⇒ เหตุด้วย (ไม่ผูกให้เอง)
        Assert.Contains("ยังไม่ผูก", PartnerExchangeRules.WhtDeliveryCheckOf(D(system: null, linkedAccepted: false, taxMatch: true)).Reason);
    }

    [Fact]
    public void ทิศตรงข้าม_ผู้ติดต่อทั่วไปที่ไม่ใช่คู่ค้า_ไม่ประทับอะไร()
    {
        var c = PartnerExchangeRules.WhtDeliveryCheckOf(D(system: "Peak", linkedAccepted: false, taxMatch: false));
        Assert.Equal(PartnerExchangeRules.WhtDeliveryAction.NotPartner, c.Action);
        Assert.Null(c.Reason);
        Assert.Equal(PartnerExchangeRules.WhtDeliveryAction.NotPartner,
            PartnerExchangeRules.WhtDeliveryCheckOf(D(system: null, linkedAccepted: false, taxMatch: false)).Action);
    }

    [Fact]
    public void B12_ที่อยู่ว่าง_ผู้รับไม่ตรง_สาขาไม่ตรง_คู่ไม่ได้เชื่อม_ปิดค่าตั้ง_มีเหตุแต่ละข้อ()
    {
        Assert.Contains("ที่อยู่ผู้ถูกหักว่าง", PartnerExchangeRules.WhtDeliveryCheckOf(D(address: false)).Reason);
        Assert.Contains("ไม่ตรงบริษัทคู่ค้า", PartnerExchangeRules.WhtDeliveryCheckOf(D(contactTax: Other)).Reason);
        Assert.Contains("สาขา", PartnerExchangeRules.WhtDeliveryCheckOf(D(contactBranch: "00001")).Reason);
        Assert.Contains("ไม่ได้เชื่อม", PartnerExchangeRules.WhtDeliveryCheckOf(D(linkedAccepted: false)).Reason);
        Assert.Contains("ปิดการส่ง", PartnerExchangeRules.WhtDeliveryCheckOf(D(allowed: false)).Reason);
        // ทิศตรงข้าม: สาขาว่าง = สำนักงานใหญ่ (ไม่ฟ้องใบถูก)
        Assert.Equal(PartnerExchangeRules.WhtDeliveryAction.Send, PartnerExchangeRules.WhtDeliveryCheckOf(D(contactBranch: null)).Action);
    }

    [Fact]
    public void ที่อยู่_ข้อความหรืออำเภอจังหวัด_นับว่ามี()
    {
        Assert.True(PartnerExchangeRules.PayeeAddressPresent("1 ถนนสาทร", null, null));
        Assert.True(PartnerExchangeRules.PayeeAddressPresent(null, "บางรัก", "กรุงเทพมหานคร"));
        Assert.False(PartnerExchangeRules.PayeeAddressPresent(" ", "บางรัก", null));
    }

    // ═══════════ แถว Autopilot ม.82/3 · ภ.ง.ด.53 ด่วน ═══════════

    [Fact]
    public void ม82_3_ห้าเดือนแสดง_สองเดือนไม่แสดง_เกินหกเดือนเลยกรอบ()
    {
        var now = new DateTime(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);
        var five = PartnerTaxEvidenceAlerts.InputVatWindowOf(new DateTime(2026, 6, 20), now);
        Assert.True(PartnerTaxEvidenceAlerts.ShowsRow(five));
        Assert.Equal(1, five.MonthsLeft);
        Assert.Equal(new DateTime(2026, 12, 1), five.LastClaimMonth);
        var two = PartnerTaxEvidenceAlerts.InputVatWindowOf(new DateTime(2026, 9, 1), now);
        Assert.False(PartnerTaxEvidenceAlerts.ShowsRow(two));
        var expired = PartnerTaxEvidenceAlerts.InputVatWindowOf(new DateTime(2026, 4, 30), now);
        Assert.Equal(PartnerTaxEvidenceAlerts.WindowState.Expired, expired.State);
        Assert.Contains("เลยกรอบ", PartnerTaxEvidenceAlerts.WindowTitle("PI-1", expired));
    }

    [Fact]
    public void ม82_3_นับเดือนตามเวลาไทย()
    {
        // 31 ต.ค. 18:00 UTC = 1 พ.ย. เวลาไทย ⇒ ใบเดือน พ.ค. ผ่านไป 6 เดือน (เหลือ 0 — ยังเคลมได้เดือนนี้)
        var w = PartnerTaxEvidenceAlerts.InputVatWindowOf(new DateTime(2026, 5, 15), new DateTime(2026, 10, 31, 18, 0, 0, DateTimeKind.Utc));
        Assert.Equal(6, w.MonthsElapsed);
        Assert.Equal(PartnerTaxEvidenceAlerts.WindowState.Approaching, w.State);
    }

    [Fact]
    public void ภงด53_ด่วนตั้งแต่วันที่1ของเดือนถัดไป_ไม่มีภาษีหักไม่ด่วน()
    {
        var paid = new DateTime(2026, 10, 20, 5, 0, 0, DateTimeKind.Utc);
        Assert.Null(PartnerTaxEvidenceAlerts.PayNoticeWhtUrgent(paid, 30m, new DateTime(2026, 10, 31, 10, 0, 0, DateTimeKind.Utc)));
        var due = PartnerTaxEvidenceAlerts.PayNoticeWhtUrgent(paid, 30m, new DateTime(2026, 10, 31, 18, 0, 0, DateTimeKind.Utc));
        Assert.Equal(TaxFilingDeadline.EFilingFor(TaxType.WithholdingTax53, 2026, 10), due);
        Assert.Null(PartnerTaxEvidenceAlerts.PayNoticeWhtUrgent(paid, 0m, new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc)));
    }
}
