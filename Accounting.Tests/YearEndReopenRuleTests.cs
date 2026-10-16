using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ยกเลิกการปิดปี — ตัวตัดสินเดียว <see cref="YearEndReopenRule"/> (คำสั่งเจ้าของ 2026-10-16 · DECISIONS ข้อ 152) · ทั้งสองทิศ:
/// ทุกด่านล้มพร้อมทางไปต่อ และเคสที่ถูกต้องผ่าน (ไม่ฟ้องเกิน)
/// </summary>
public class YearEndReopenRuleTests
{
    private static readonly IReadOnlyList<YearEndFiledReturn> NoFiled = Array.Empty<YearEndFiledReturn>();

    private static YearEndFiledReturn Pnd50(string status = "Filed", string? confirmation = "RD-2025-0001")
        => new(Guid.NewGuid(), "RD_CIT", "PND50", 2024, null, status, new DateTime(2025, 5, 20, 0, 0, 0, DateTimeKind.Utc), null, confirmation);

    private static YearEndReopenFacts Facts(bool closed = true, int[]? later = null, int legacy = 0,
        IReadOnlyList<YearEndFiledReturn>? filed = null, string? reason = "พบค่าใช้จ่ายลงผิดปี", bool ack = false)
        => new(2024, closed, later ?? Array.Empty<int>(), legacy, filed ?? NoFiled, reason, ack);

    [Fact]
    public void ปีล่าสุด_มีเหตุผล_ไม่มีแบบที่ยื่น_ผ่าน()
    {
        var d = YearEndReopenRule.Evaluate(Facts());
        Assert.True(d.Allowed);
        Assert.Null(d.Message);
    }

    [Fact]
    public void ปียังไม่ปิด_ล้ม_บอกว่าไม่มีอะไรต้องยกเลิก()
    {
        var d = YearEndReopenRule.Evaluate(Facts(closed: false));
        Assert.Equal(YearEndReopenOutcome.NotClosed, d.Outcome);
        Assert.Contains("2567", d.Message);
        Assert.Contains("ยังไม่มีอะไรถูกบันทึก", d.Message);
        Assert.Equal(404, new YearEndReopenException(d).StatusCode);
    }

    [Fact]
    public void มีปีหลังปิดแล้ว_ล้ม_ทางไปต่อเปิดปีล่าสุดก่อน_ปีก่อนหน้าไม่นับ()
    {
        var d = YearEndReopenRule.Evaluate(Facts(later: new[] { 2026, 2025 }));
        Assert.Equal(YearEndReopenOutcome.LaterYearClosed, d.Outcome);
        Assert.Contains("เปิดปีล่าสุดก่อน", d.Message);
        Assert.Contains("2568, 2569", d.Message);          // เรียงปี พ.ศ.
        Assert.Contains("ยกเลิกการปิดปี 2569", d.Message);  // ปีล่าสุดที่ต้องเริ่ม
        // ทิศตรงข้าม: ปีที่ปิดก่อนหน้า (2023) ไม่ใช่ "ปีหลัง" — ไม่บล็อก
        Assert.True(YearEndReopenRule.Evaluate(Facts(later: new[] { 2023 })).Allowed);
    }

    [Fact]
    public void ยกยอดรุ่นเก่ายังไม่ซ่อม_ล้ม_ทางไปต่อซ่อมก่อน()
    {
        var d = YearEndReopenRule.Evaluate(Facts(legacy: 2));
        Assert.Equal(YearEndReopenOutcome.LegacyRollUnrepaired, d.Outcome);
        Assert.Contains("ซ่อมยอดยกมารุ่นเก่าก่อน", d.Message);
        Assert.Contains("2 แถว", d.Message);
        Assert.Contains("ปี 2568", d.Message);               // ปีที่ต้องกด "แก้" = ปีที่ปิด + 1
        Assert.True(YearEndReopenRule.Evaluate(Facts(legacy: 0)).Allowed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ไม่ระบุเหตุผล_ล้ม(string? reason)
    {
        var d = YearEndReopenRule.Evaluate(Facts(reason: reason));
        Assert.Equal(YearEndReopenOutcome.ReasonRequired, d.Outcome);
        Assert.Contains("เหตุผล", d.Message);
        Assert.Equal(400, new YearEndReopenException(d).StatusCode);
    }

    [Fact]
    public void เหตุผลถูกตัดช่องว่าง()
        => Assert.Equal("แก้ค่าเสื่อม", YearEndReopenRule.NormalizeReason("  แก้ค่าเสื่อม \n"));

    [Fact]
    public void ยื่นภงด50แล้ว_ไม่รับทราบ_409_บอกยื่นเพิ่มเติมและงบฉบับแก้ไข_รับทราบแล้วผ่าน()
    {
        var filed = new[] { Pnd50() };
        var d = YearEndReopenRule.Evaluate(Facts(filed: filed));
        Assert.Equal(YearEndReopenOutcome.FiledNeedsAcknowledgement, d.Outcome);
        Assert.Equal(409, new YearEndReopenException(d).StatusCode);
        Assert.Contains("ภ.ง.ด.50", d.Message);
        Assert.Contains("ยื่นแบบ ภ.ง.ด.50 เพิ่มเติม", d.Message);
        Assert.Contains("งบการเงินฉบับแก้ไข", d.Message);
        Assert.Contains("RD-2025-0001", d.Message);
        Assert.Contains(YearEndReopenRule.PriorPeriodErrorLabel, d.Message);
        Assert.Single(d.FiledReturns);
        // ทิศตรงข้าม: รับทราบแล้ว ⇒ ผ่าน และส่งแบบที่ยื่นต่อให้ audit
        var ok = YearEndReopenRule.Evaluate(Facts(filed: filed, ack: true));
        Assert.True(ok.Allowed);
        Assert.Single(ok.FiledReturns);
    }

    [Fact]
    public void ลำดับด่าน_ปีหลังปิดมาก่อนขอรับทราบ_เหตุผลมาก่อนขอรับทราบ()
    {
        Assert.Equal(YearEndReopenOutcome.LaterYearClosed,
            YearEndReopenRule.Evaluate(Facts(later: new[] { 2025 }, filed: new[] { Pnd50() }, ack: true)).Outcome);
        Assert.Equal(YearEndReopenOutcome.ReasonRequired,
            YearEndReopenRule.Evaluate(Facts(filed: new[] { Pnd50() }, reason: " ")).Outcome);
    }

    [Theory]
    [InlineData("RD_CIT", "Filed", true)]
    [InlineData("RD_CIT", "Accepted", true)]
    [InlineData("DBD_FinancialStatement", "Filed", true)]
    [InlineData("RD_CIT", "Validated", false)]
    [InlineData("RD_CIT", "NotStarted", false)]
    [InlineData("RD_VAT", "Filed", false)]          // ภ.พ.30 รายเดือนไม่เกี่ยวกับการเปิดปี (ล็อกงวดภาษีของมันเองต่อ)
    [InlineData("RD_WHT", "Accepted", false)]
    [InlineData(null, "Filed", false)]
    public void แบบที่ยื่นแล้วที่ต้องรับทราบ(string? type, string status, bool expected)
        => Assert.Equal(expected, YearEndReopenRule.IsFiledReturn(type, status));

    [Fact]
    public void ป้ายแบบ_และเลขอ้างอิง()
    {
        Assert.Equal("ภ.ง.ด.50", YearEndReopenRule.FilingLabel(Pnd50()));
        Assert.Equal("ภ.ง.ด.51", YearEndReopenRule.FilingLabel(Pnd50() with { FormCode = "PND51", Month = 6 }));
        Assert.Contains("กรมพัฒนาธุรกิจการค้า", YearEndReopenRule.FilingLabel(Pnd50() with { FilingType = "DBD_FinancialStatement", FormCode = "SBC3" }));
        Assert.Equal("SUB-1", YearEndReopenRule.FilingRef(Pnd50(confirmation: null) with { SubmissionReference = "SUB-1" }));
        Assert.Equal("ไม่มีเลขอ้างอิง", YearEndReopenRule.FilingRef(Pnd50(confirmation: " ")));
    }

    [Fact]
    public void ข้อความเปิดงวดที่ล็อก_และด่านใบปิดปี_ชี้ปุ่มยกเลิกการปิดปี_และไม่อ้าง_TAS8()
    {
        var locked = YearEndReopenRule.LockedPeriodMessage("2024/12");
        Assert.Contains("ยกเลิกการปิดปี", locked);
        Assert.Contains("TFRS for NPAEs บทที่ 5", locked);
        var lockMsg = ClosingEntryLock.BlockMessage("กลับรายการ", new (string, int?)[] { ("JV-1", 2024) });
        Assert.Contains("ยกเลิกการปิดปี", lockMsg);
        Assert.DoesNotContain("ระบบยังไม่มีคำสั่งยกเลิกการปิดปี", lockMsg);
        Assert.DoesNotContain("TAS 8", lockMsg + ClosingEntryLock.LegalReference + locked);
        Assert.Contains(YearEndReopenRule.PriorPeriodErrorLabel, lockMsg);
    }

    [Fact]
    public void ผลของการยกเลิก_บอกครบ_และเตือนว่าแบบภาษีรายเดือนยังล็อก()
    {
        var e = YearEndReopenRule.Effects(2024);
        Assert.Contains(e, x => x.Contains("กลับรายการใบปิดบัญชีสิ้นปี 2567"));
        Assert.Contains(e, x => x.Contains("2568") && x.Contains("ยอดยกมา") && x.Contains("ไม่ถูกแตะ"));
        Assert.Contains(e, x => x.Contains("เปิด") && x.Contains("ภ.พ.30"));
    }

    [Fact]
    public void ข้อยกเว้นพกผลและแบบที่ยื่น_สถานะตามผล()
    {
        var d = YearEndReopenRule.Evaluate(Facts(filed: new[] { Pnd50() }));
        var ex = new YearEndReopenException(d);
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(YearEndReopenRule.RuleCode, ex.RuleCode);
        Assert.Equal(YearEndReopenOutcome.FiledNeedsAcknowledgement, ex.Outcome);
        Assert.Single(ex.FiledReturns);
    }
}
