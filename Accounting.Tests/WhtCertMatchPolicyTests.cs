using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;
using C = Accounting.Helpers.WhtCertMatchPolicy.Candidate;
using L = Accounting.Helpers.WhtCertMatchLevel;

namespace Accounting.Tests;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §3.7 + §11 ข้อ 1–2 · AUDIT_249 §2 #31) — จับคู่ใบ ↔ แถวรอใบ (K0–K6) + ด่านเชิงกำหนด + ตัดสินยืนยันอัตโนมัติ ·
/// ทุกข้อมีทิศตรงข้าม: กำกวม ⇒ ไม่เลือกเอง · ด่านไม่ผ่าน/ไม่มั่นใจ/ปิด AI ⇒ ไม่ auto (ผู้ใช้กดยืนยัน)
/// </summary>
public class WhtCertMatchPolicyTests
{
    private const string Ours = "0105556123453";
    private const string Payer = "0107556000116";
    private const string OtherCo = "0105556987652";
    private static readonly DateTime D = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private static C Row(decimal outstanding, int daysBefore = 0, string payer = Payer)
        => new(Guid.NewGuid(), Guid.NewGuid(), payer, null, outstanding, D.AddDays(-daysBefore));

    private static WhtCertMatchPolicy.Cert Cert(decimal tax, string? payee = Ours, string? payer = Payer)
        => new(payer, null, payee, D, tax);

    [Fact]
    public void ใบเดียว450_ครอบ300กับ150_จับคู่สองแถว()
    {
        var a = Row(300m, 3); var b = Row(150m, 1); var c = Row(1_000m, 5);
        var r = WhtCertMatchPolicy.Match(Cert(450m), Ours, new[] { a, b, c });
        Assert.Equal(L.Exact, r.Level);
        Assert.Equal(new[] { a.CreditId, b.CreditId }.OrderBy(x => x), r.Allocations.Select(x => x.CreditId).OrderBy(x => x));
        Assert.Equal(450m, r.Allocations.Sum(x => x.Amount));
    }

    [Fact]
    public void ทิศตรงข้าม_สองชุดรวมได้450_กำกวม_ไม่เลือกเอง()
    {
        var r = WhtCertMatchPolicy.Match(Cert(450m), Ours, new[] { Row(300m), Row(150m), Row(200m), Row(250m) });
        Assert.Equal(L.Ambiguous, r.Level);
        Assert.Empty(r.Allocations);
    }

    [Fact]
    public void K3_ยอดเดี่ยวตรง_ภายใน1บาท()
    {
        var a = Row(300.50m);
        var r = WhtCertMatchPolicy.Match(Cert(300m), Ours, new[] { a, Row(90m) });
        Assert.Equal(L.Exact, r.Level);
        Assert.Equal(a.CreditId, Assert.Single(r.Allocations).CreditId);
    }

    [Fact]
    public void K0_ใบออกในนามบริษัทอื่น_ปฏิเสธ_ไม่มีallocation()
    {
        var r = WhtCertMatchPolicy.Match(Cert(300m, payee: OtherCo), Ours, new[] { Row(300m) });
        Assert.Equal(L.Rejected, r.Level);
        Assert.Empty(r.Allocations);
    }

    [Fact]
    public void K1_ผู้จ่ายคนอื่น_ไม่จับคู่_ตัวเลขห้ามfuzzy()
    {
        // เลขผู้จ่ายต่างหลักเดียว ⇒ ไม่ใช่ผู้จ่ายรายนี้ (ห้ามจับคู่แบบ "คล้าย")
        var r = WhtCertMatchPolicy.Match(Cert(300m, payer: "0107556000124"), Ours, new[] { Row(300m) });
        Assert.Equal(L.NoCandidate, r.Level);
    }

    [Fact]
    public void K5_ใบน้อยกว่ายอดค้าง_บางส่วน_ไม่ใช่Exact()
    {
        var a = Row(300m);
        var r = WhtCertMatchPolicy.Match(Cert(100m), Ours, new[] { a });
        Assert.Equal(L.Partial, r.Level);
        Assert.Equal(100m, Assert.Single(r.Allocations).Amount);
    }

    [Fact]
    public void K6_ใบมากกว่ายอดค้างทั้งหมด_Over()
        => Assert.Equal(L.Over, WhtCertMatchPolicy.Match(Cert(900m), Ours, new[] { Row(300m), Row(150m) }).Level);

    [Fact]
    public void K2_เอกสารที่ระบุมาก่อน_แม้มีแถวอื่นยอดเท่ากัน()
    {
        var a = Row(300m); var b = Row(300m);
        var r = WhtCertMatchPolicy.Match(Cert(300m), Ours, new[] { a, b }, new[] { b.DocumentId!.Value });
        Assert.Equal(L.Exact, r.Level);
        Assert.Equal(b.CreditId, Assert.Single(r.Allocations).CreditId);
        // ทิศตรงข้าม: ไม่ระบุเอกสาร + วันเท่ากัน ⇒ กำกวม
        Assert.Equal(L.Ambiguous, WhtCertMatchPolicy.Match(Cert(300m), Ours, new[] { a, b }).Level);
    }

    // ═══════════ ด่านเชิงกำหนด + ตัดสิน ═══════════

    private static WhtCertIntakeVerdict.GateInput Gate(string? payer = Payer, bool? payeeIsUs = true, decimal? conf = 0.95m,
        L level = L.Exact, string? certNo = "2569/0456", int? certFy = 2026, int rowFy = 2026)
        => new(certNo, D, payer, Ours, payeeIsUs, 10_000m, 3m, 300m, level, conf, certFy, new[] { rowFy }, D.AddDays(3));

    [Fact]
    public void ผ่านครบ_และตรวจของจริงมั่นใจ_ยืนยันอัตโนมัติ()
    {
        var g = WhtCertIntakeVerdict.Gates(Gate());
        Assert.Empty(g);
        Assert.Equal(WhtCertIntakeVerdict.Outcome.AutoConfirm,
            WhtCertIntakeVerdict.Decide(L.Exact, g, WhtCertIntakeVerdict.Genuine, 0.92m, answerUsable: true));
    }

    [Fact]
    public void KillSwitch_ไม่มีคำตอบ_หรือนักเรียนไม่มั่นใจ_ไม่auto()
    {
        var g = WhtCertIntakeVerdict.Gates(Gate());
        // ปิด provider ทุกตัว + นักเรียน cold start ⇒ ไม่มีคำตอบ
        Assert.Equal(WhtCertIntakeVerdict.Outcome.AwaitUser, WhtCertIntakeVerdict.Decide(L.Exact, g, null, null, answerUsable: false));
        // majority ของนักเรียน (≤ 0.45) / คลังทันที (0.80) ไม่ถึงเกณฑ์
        Assert.Equal(WhtCertIntakeVerdict.Outcome.AwaitUser, WhtCertIntakeVerdict.Decide(L.Exact, g, "Genuine", 0.45m, answerUsable: true));
        Assert.Equal(WhtCertIntakeVerdict.Outcome.AwaitUser, WhtCertIntakeVerdict.Decide(L.Exact, g, "Genuine", 0.80m, answerUsable: true));
        // AI ว่าน่าสงสัย ⇒ ไม่ auto แม้มั่นใจ
        Assert.Equal(WhtCertIntakeVerdict.Outcome.AwaitUser, WhtCertIntakeVerdict.Decide(L.Exact, g, "Suspect", 0.99m, answerUsable: true));
        // ความมั่นใจนอกช่วง (โมเดลตอบ 90) ⇒ ไม่ auto
        Assert.Equal(WhtCertIntakeVerdict.Outcome.AwaitUser, WhtCertIntakeVerdict.Decide(L.Exact, g, "Genuine", 90m, answerUsable: true));
    }

    [Fact]
    public void ด่านเชิงกำหนดก่อนAI_checksumผิด_AIว่าGenuineก็ไม่auto()
    {
        var g = WhtCertIntakeVerdict.Gates(Gate(payer: "0107556000117"));
        Assert.Contains(g, p => p.Contains("checksum"));
        Assert.Equal(WhtCertIntakeVerdict.Outcome.AwaitUser, WhtCertIntakeVerdict.Decide(L.Exact, g, "Genuine", 0.99m, answerUsable: true));
    }

    [Fact]
    public void ด่านผู้ถูกหักไม่ใช่เรา_และความมั่นใจOCRต่ำ()
    {
        Assert.Contains(WhtCertIntakeVerdict.Gates(Gate(payeeIsUs: false)), p => p.Contains("ผู้ถูกหักบนใบไม่ใช่บริษัทเรา"));
        Assert.Contains(WhtCertIntakeVerdict.Gates(Gate(conf: 0.70m)), p => p.Contains("ความมั่นใจ"));
    }

    [Fact]
    public void ด่านปีภาษี_ใบคนละรอบกับแถว_และไม่มีเลขที่ใบ()
    {
        Assert.Contains(WhtCertIntakeVerdict.Gates(Gate(certFy: 2027)), p => p.Contains("รอบบัญชี"));
        Assert.Contains(WhtCertIntakeVerdict.Gates(Gate(certNo: null)), p => p.Contains("เลขที่"));
        Assert.Contains(WhtCertIntakeVerdict.Gates(Gate(level: L.Partial)), p => p.Contains("บางส่วน"));
        // ทิศตรงข้าม: ไม่ได้มาจาก OCR (กรอกมือ/ข้อมูลโครงสร้าง) ⇒ ไม่ใช่เหตุให้ไม่ auto
        Assert.Empty(WhtCertIntakeVerdict.Gates(Gate(conf: null)));
    }

    [Fact]
    public void K0_ตีกลับทันที_และด่านรับคำตอบAI_รับเฉพาะชุดปิด()
    {
        Assert.Equal(WhtCertIntakeVerdict.Outcome.Reject,
            WhtCertIntakeVerdict.Decide(L.Rejected, new[] { "x" }, "Genuine", 0.99m, answerUsable: true));
        Assert.Equal(WhtCertIntakeVerdict.Genuine, WhtCertIntakeVerdict.ParseAuthenticity(" genuine "));
        Assert.Equal(WhtCertIntakeVerdict.Suspect, WhtCertIntakeVerdict.ParseAuthenticity("\"Suspect\""));
        Assert.Null(WhtCertIntakeVerdict.ParseAuthenticity("yes, looks real"));
        Assert.Null(WhtCertIntakeVerdict.ParseAuthenticity(null));
    }

    [Fact]
    public void ป้ายซื่อสัตย์_AIจริงเท่านั้นที่ขึ้น_หุ่นยนต์()
    {
        Assert.StartsWith("🤖", WhtCertIntakeVerdict.AuthenticityLabel("Genuine", 0.9m, usedAi: true));
        Assert.StartsWith("⚙️", WhtCertIntakeVerdict.AuthenticityLabel("Genuine", 0.9m, usedAi: false));
        Assert.StartsWith("⚙️", WhtCertIntakeVerdict.AuthenticityLabel(null, null, usedAi: false));
    }

    [Fact]
    public void ปีภาษีตามเดือนเริ่มรอบบัญชี()
    {
        Assert.Equal(2026, WhtCertIntakeVerdict.FiscalYearOf(new DateTime(2026, 11, 3), 1));
        Assert.Equal(2025, WhtCertIntakeVerdict.FiscalYearOf(new DateTime(2026, 3, 3), 4));
        Assert.Equal(2026, WhtCertIntakeVerdict.FiscalYearOf(new DateTime(2026, 3, 3), 0));
    }

    [Fact]
    public void แปลงผลสแกน_ยอดอ่านไม่ได้_ไม่เดา_และยอดจากตัวอักษร_ความมั่นใจไม่ถึงเกณฑ์()
    {
        var unknown = WhtCertScanMapping.Map(null, null, 1_070m, "", null, 0.95m, BusinessType.JuristicPerson);
        Assert.True(unknown.AmountUnknown);
        Assert.Equal(0m, unknown.WhtAmount);   // ไม่เอายอดรวมทั้งใบเป็นยอดภาษี
        var computed = WhtCertScanMapping.Map(10_000m, 3m, 10_000m, "", "{\"SellerTaxId\":0.97,\"SubTotal\":0.91}", 0.5m, BusinessType.JuristicPerson);
        Assert.Equal(300m, computed.WhtAmount);
        Assert.Equal(0.91m, computed.ExtractionConfidence);
        Assert.Equal(WhtPayerFormType.Pnd53, computed.PayerFormType);
        Assert.Equal(WhtPayerFormType.Pnd3, WhtCertScanMapping.Map(10_000m, 3m, null, "", null, 0.9m, BusinessType.Individual).PayerFormType);
    }
}
