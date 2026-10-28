using System;
using System.Linq;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม U 2026-10-26 — <see cref="PaymentSourceSplit"/>: ติ๊ก "แยกยอดจ่ายจริงตามแหล่ง" (คำสั่งผู้ใช้: 21230 เงิน 438 + 52120 หักหลัง VAT 98 = 536)
///
/// <para><b>ครึ่งที่ 1</b>: ชุดของผู้ใช้ผ่าน · ย่อยเป็น เงิน 438 / หัก 98 · ขา JE: Cr 52120 98 (แหล่งเงินตรงแหล่งหลัก ⇒ ไม่มีขาย้าย) · แหล่งอื่น ⇒ Dr หลัก/Cr แหล่งนั้น net-zero</para>
/// <para><b>ครึ่งที่ 2</b>: ผลรวมไม่ตรง / ไม่มีเงิน / ยอดศูนย์ / ผังเงินสดในบรรทัดหัก ⇒ ปฏิเสธข้อความไทย · ว่าง = พฤติกรรมเดิม · JSON round-trip</para>
/// </summary>
public class PaymentSourceSplitTests
{
    private static readonly PaymentSourceLine[] UserCase =
    {
        new(PaymentSourceKind.Money, "21230", 438.00m, "กรรมการจ่ายแทน"),
        new(PaymentSourceKind.PostVatDeduction, "52120", 98.00m, "ส่วนลดพิเศษ Shopee"),
    };

    [Fact]
    public void ชุดของผู้ใช้_21230_438_บวก_52120_98_เท่า536_ผ่าน()
    {
        var c = PaymentSourceSplit.Check(536.00m, UserCase);
        Assert.True(c.Ok, c.Error);
        Assert.Equal(438.00m, c.MoneyTotal);
        Assert.Equal(98.00m, c.DeductionTotal);
    }

    [Fact]
    public void ขาJE_แหล่งเงินตรงแหล่งหลัก_มีแค่Crหัก98()
    {
        var lines = PaymentSourceSplit.JournalLines(UserCase, "21230", "PV-TEST");
        var single = Assert.Single(lines);
        Assert.Equal(("52120", 0m, 98.00m), (single.AccountCode, single.Debit, single.Credit));
        Assert.Contains("หักหลัง VAT", single.Description);
    }

    [Fact]
    public void ขาJE_แหล่งเงินอื่น_ย้ายภาระจ่าย_Drหลัก_Crแหล่งนั้น_netZero()
    {
        var split = new[]
        {
            new PaymentSourceLine(PaymentSourceKind.Money, "11110", 300.00m),
            new PaymentSourceLine(PaymentSourceKind.Money, "21230", 138.00m),
            new PaymentSourceLine(PaymentSourceKind.PostVatDeduction, "51150", 98.00m),
        };
        Assert.True(PaymentSourceSplit.Check(536.00m, split).Ok);
        var lines = PaymentSourceSplit.JournalLines(split, "11110", "PV-TEST");
        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, l => l.AccountCode == "11110" && l.Debit == 138.00m);
        Assert.Contains(lines, l => l.AccountCode == "21230" && l.Credit == 138.00m);
        Assert.Contains(lines, l => l.AccountCode == "51150" && l.Credit == 98.00m);
        // รวมกับขาเงินสดเดิม (Cr 11110 536 · Dr 11110 กลับ 98 ตาม DocumentCashDelta) ⇒ 11110 สุทธิ Cr 300 · 21230 Cr 138 · 51150 Cr 98 · รวม 536
        var cashDelta = PaymentSettlementAdjustment.DocumentCashDelta(
            Accounting.Models.Enums.DocumentType.PaymentVoucher, Accounting.Models.Enums.PaymentType.Cash, false, false, 536.00m, 438.00m);
        Assert.Null(PaymentSettlementAdjustment.CheckDocumentLines(cashDelta, lines.Sum(l => l.Debit), lines.Sum(l => l.Credit), 536.00m, 438.00m));
    }

    /// <summary>ฝ่ายค้านข้อ 3: แยกยอดที่ไม่มีบรรทัดหัก (เงินสด 300 + เจ้าหนี้กรรมการ 236 = 536) — ส่วนต่างขาเงินสด 0 แต่ขา "ย้ายแหล่งจ่าย" ต้องลงจริง
    /// (Dr แหล่งหลัก 236 / Cr 21230 236 · net-zero ⇒ ด่านสมดุลเดิมผ่าน) ไม่ใช่เก็บแล้วเงียบ</summary>
    [Fact]
    public void ฝ่ายค้าน3_แยกยอดเงินล้วนสองแหล่ง_ขาย้ายแหล่งลงจริง_netZero()
    {
        var split = new[]
        {
            new PaymentSourceLine(PaymentSourceKind.Money, "11110", 300.00m),
            new PaymentSourceLine(PaymentSourceKind.Money, "21230", 236.00m, "กรรมการจ่ายแทน"),
        };
        var c = PaymentSourceSplit.Check(536.00m, split);
        Assert.True(c.Ok);
        Assert.Equal((536.00m, 0m), (c.MoneyTotal, c.DeductionTotal));   // เงิน = ยอดรวม ⇒ ActualPaidAmount null · ส่วนต่างขาเงินสด 0
        var lines = PaymentSourceSplit.JournalLines(split, "11110", "PV-TEST");
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.AccountCode == "11110" && l.Debit == 236.00m && l.Credit == 0m);
        Assert.Contains(lines, l => l.AccountCode == "21230" && l.Credit == 236.00m && l.Debit == 0m);
        Assert.Null(PaymentSettlementAdjustment.CheckDocumentLines(0m, lines.Sum(l => l.Debit), lines.Sum(l => l.Credit), 536.00m, null));
        // ติ๊กแยกยอดที่ทุกบรรทัดคือแหล่งหลักเอง = ไม่มีขาเพิ่ม (พฤติกรรมเดิม)
        Assert.Empty(PaymentSourceSplit.JournalLines(new[] { new PaymentSourceLine(PaymentSourceKind.Money, "11110", 536.00m) }, "11110", "PV-TEST"));
    }

    [Fact]
    public void ผลรวมไม่ตรง_ปฏิเสธ_บอกส่วนต่าง()
    {
        var c = PaymentSourceSplit.Check(536.00m, new[] { new PaymentSourceLine(PaymentSourceKind.Money, "21230", 438.00m) });
        Assert.False(c.Ok);
        Assert.Contains("536.00", c.Error);
        Assert.Contains("438.00", c.Error);
        Assert.Contains("−98.00", c.Error);
    }

    [Fact]
    public void ไม่มีเงินออกเลย_ปฏิเสธ()
    {
        var c = PaymentSourceSplit.Check(98.00m, new[] { new PaymentSourceLine(PaymentSourceKind.PostVatDeduction, "51150", 98.00m) });
        Assert.False(c.Ok);
        Assert.Contains("เงินออกจริง", c.Error);
    }

    [Fact]
    public void ยอดศูนย์_ผังว่าง_ทศนิยม3_ผังเงินสดในบรรทัดหัก_ปฏิเสธ()
    {
        Assert.False(PaymentSourceSplit.Check(536m, new[] { new PaymentSourceLine(PaymentSourceKind.Money, "21230", 0m) }).Ok);
        Assert.False(PaymentSourceSplit.Check(536m, new[] { new PaymentSourceLine(PaymentSourceKind.Money, "", 536m) }).Ok);
        Assert.False(PaymentSourceSplit.Check(536m, new[] { new PaymentSourceLine(PaymentSourceKind.Money, "21230", 536.001m) }).Ok);
        Assert.False(PaymentSourceSplit.Check(536m, new[]
        {
            new PaymentSourceLine(PaymentSourceKind.Money, "21230", 438m),
            new PaymentSourceLine(PaymentSourceKind.PostVatDeduction, "11110", 98m),
        }).Ok);
    }

    [Fact]
    public void ค่าเผื่อ_ครึ่งสตางค์ผ่าน_หนึ่งสตางค์ไม่ผ่าน()
    {
        Assert.True(PaymentSourceSplit.Check(536.00m, new[] { new PaymentSourceLine(PaymentSourceKind.Money, "21230", 536.00m) }).Ok);
        Assert.False(PaymentSourceSplit.Check(536.00m, new[] { new PaymentSourceLine(PaymentSourceKind.Money, "21230", 535.99m) }).Ok);
    }

    [Fact]
    public void ว่าง_พฤติกรรมเดิม_เงินเท่ายอดรวม_หักศูนย์()
    {
        var c = PaymentSourceSplit.Check(536.00m, null);
        Assert.True(c.Ok);
        Assert.Equal((536.00m, 0m), (c.MoneyTotal, c.DeductionTotal));
        Assert.True(PaymentSourceSplit.Check(536.00m, Array.Empty<PaymentSourceLine>()).Ok);
        Assert.Null(PaymentSourceSplit.Serialize(null));
        Assert.Null(PaymentSourceSplit.Parse(null));
        Assert.Null(PaymentSourceSplit.Parse("not json"));
    }

    [Fact]
    public void JSON_เขียนแล้วอ่านกลับได้ชุดเดิม()
    {
        var json = PaymentSourceSplit.Serialize(UserCase);
        Assert.NotNull(json);
        Assert.Contains("\"kind\":\"Money\"", json);   // enum เป็นชื่อ ไม่ใช่เลข
        var back = PaymentSourceSplit.Parse(json);
        Assert.NotNull(back);
        Assert.Equal(UserCase.Select(l => (l.Kind, l.AccountCode, l.Amount, l.Note)), back!.Select(l => (l.Kind, l.AccountCode, l.Amount, l.Note)));
    }
}
