using System;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// FX ของการจับคู่ธนาคาร (`DECISION_AUDIT_2026-09-18.md` §3 D4-8 "FX").
///
/// ทุกหมวดมี **สองครึ่ง** ตามกฎเหล็ก #4 H:
///  (ก) ครึ่งที่พิสูจน์ว่า "ใบสกุลต่างประเทศที่เคยกระทบยอดไม่ได้ กลับมาได้"
///  (ข) ครึ่งที่พิสูจน์ว่า "ใบ THB ที่ถูกอยู่แล้ว **ไม่ถูกแตะ**"
/// เทสต์ที่มีแต่ครึ่งแรกผ่านได้ทั้งตอนแก้ถูกและตอนปิดด่านทิ้ง
/// </summary>
public class BankMatchCurrencyTests
{
    // ══════════════════════════════════════════════════════════════════
    //  (ข) ครึ่ง "ของเดิมที่ถูกอยู่แล้วต้องไม่ถูกแตะ"
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void สกุลเดียวกัน_คืนยอดเดิมเป๊ะและไม่ปัดเศษ()
    {
        var r = BankMatchCurrency.Convert("THB", "THB", 1234.56m, null, 1m, "THB");
        Assert.True(r.Comparable);
        Assert.Equal(BankAmountComparability.SameCurrency, r.Kind);
        Assert.Equal(1234.56m, r.BankCurrencyAmount);
        Assert.Equal(1m, r.Rate);
    }

    [Fact]
    public void สกุลเดียวกัน_แม้ไม่มีอัตราแลกเปลี่ยนก็ยังเทียบได้()
    {
        // ใบ THB ปกติไม่มี Payment.ExchangeRate และ Document.ExchangeRate = 1
        var r = BankMatchCurrency.Convert("THB", "THB", 500m, null, null, "THB");
        Assert.True(r.Comparable);
        Assert.Equal(500m, r.BankCurrencyAmount);
    }

    [Fact]
    public void ตัวพิมพ์เล็กใหญ่และช่องว่างไม่ทำให้กลายเป็นคนละสกุล()
    {
        var r = BankMatchCurrency.Convert(" thb ", "THB", 100m, null, 1m, "THB");
        Assert.Equal(BankAmountComparability.SameCurrency, r.Kind);
    }

    // ══════════════════════════════════════════════════════════════════
    //  (ก) ครึ่ง "ใบที่เคยพังกลับมาถูก"
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ใบUSDเข้าบัญชีTHB_แปลงด้วยอัตราวันชำระ()
    {
        // 1,000 USD × 35.25 = 35,250 บาท — เดิมเทียบ 1,000 กับ 35,250 แล้ว throw
        var r = BankMatchCurrency.Convert("THB", "USD", 1000m,
            settlementRate: 35.25m, documentRate: 34.00m, companyBaseCurrency: "THB");
        Assert.True(r.Comparable);
        Assert.Equal(BankAmountComparability.Converted, r.Kind);
        Assert.Equal(35_250.00m, r.BankCurrencyAmount);
        Assert.Equal(35.25m, r.Rate);
    }

    [Fact]
    public void อัตราวันชำระชนะอัตราบนเอกสารเสมอ()
    {
        var r = BankMatchCurrency.Convert("THB", "USD", 100m, 36m, 30m, "THB");
        Assert.Equal(36m, r.Rate);
        Assert.Equal(3600m, r.BankCurrencyAmount);
    }

    [Fact]
    public void ไม่มีอัตราวันชำระ_ถอยไปใช้อัตราบนเอกสาร()
    {
        var r = BankMatchCurrency.Convert("THB", "USD", 100m, null, 30m, "THB");
        Assert.Equal(30m, r.Rate);
        Assert.Equal(3000m, r.BankCurrencyAmount);
    }

    [Fact]
    public void ปัดเศษแบบAwayFromZeroสองตำแหน่ง()
    {
        // 3.333 × 3 = 9.999 → 10.00 (ไม่ใช่ banker's rounding)
        var r = BankMatchCurrency.Convert("THB", "USD", 3.333m, 3m, null, "THB");
        Assert.Equal(10.00m, r.BankCurrencyAmount);

        // 0.125 × 1 = 0.125 → 0.13 (banker's จะได้ 0.12)
        var r2 = BankMatchCurrency.Convert("THB", "USD", 0.125m, 1m, null, "THB");
        Assert.Equal(0.13m, r2.BankCurrencyAmount);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ทิศตรงข้าม: "ไม่รู้ ต้องไม่ตกเป็นผ่าน" (G3)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void ใบUSDแต่ไม่มีอัตราเลย_ต้องบอกว่าเทียบไม่ได้ไม่ใช่เดา1ต่อ1()
    {
        var r = BankMatchCurrency.Convert("THB", "USD", 1000m, null, null, "THB");
        Assert.False(r.Comparable);
        Assert.Equal(BankMatchCurrency.RuleNoRate, r.RuleCode);
        Assert.Equal(0m, r.BankCurrencyAmount);
        Assert.Contains("อัตราแลกเปลี่ยน", r.Reason);
    }

    [Fact]
    public void อัตราศูนย์หรือติดลบ_ถือว่าไม่มีอัตรา()
    {
        Assert.False(BankMatchCurrency.Convert("THB", "USD", 100m, 0m, 0m, "THB").Comparable);
        Assert.False(BankMatchCurrency.Convert("THB", "USD", 100m, null, -1m, "THB").Comparable);
    }

    [Fact]
    public void บัญชีUSDกับใบEUR_ข้ามสกุลที่เราไม่มีอัตรา_ต้องปฏิเสธ()
    {
        var r = BankMatchCurrency.Convert("USD", "EUR", 100m, 1.1m, 1.1m, "THB");
        Assert.False(r.Comparable);
        Assert.Equal(BankMatchCurrency.RuleCross, r.RuleCode);
    }

    [Fact]
    public void บัญชีUSDกับใบTHB_ก็แปลงไม่ได้เพราะอัตราเก็บเทียบสกุลฐาน()
    {
        var r = BankMatchCurrency.Convert("USD", "THB", 35_000m, null, 1m, "THB");
        Assert.False(r.Comparable);
        Assert.Equal(BankMatchCurrency.RuleCross, r.RuleCode);
    }

    [Fact]
    public void ไม่รู้รหัสสกุลของฝั่งใดฝั่งหนึ่ง_ต้องปฏิเสธ()
    {
        Assert.Equal(BankMatchCurrency.RuleUnknownCode,
            BankMatchCurrency.Convert(null, "THB", 100m, null, 1m, "THB").RuleCode);
        Assert.Equal(BankMatchCurrency.RuleUnknownCode,
            BankMatchCurrency.Convert("THB", "  ", 100m, null, 1m, "THB").RuleCode);
    }

    [Fact]
    public void ไม่รู้สกุลฐานของบริษัท_ห้ามเดาว่าเป็นTHB()
    {
        var r = BankMatchCurrency.Convert("THB", "USD", 100m, 35m, 35m, null);
        Assert.False(r.Comparable);
        Assert.Equal(BankMatchCurrency.RuleCross, r.RuleCode);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ต่อกับตัวกระทบยอด — หักในสกุลเอกสารก่อน แล้วแปลงครั้งเดียว
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void กระทบยอดใบUSDที่ถูกหักWht_ต้องหักก่อนแปลง()
    {
        // ใบ 1,000 USD หัก WHT 30 USD → เงินเข้าจริง 970 USD × 35 = 33,950 บาท
        var item = new BankMatchAmountReconciler.Item(
            Guid.NewGuid(), "ใบแจ้งหนี้ INV-USD-1", BankFlowDirection.Inflow,
            RecordedAmount: 1000m, BankLineAmount: null, WithheldAmount: 30m,
            FeeAmount: 0m, ConversionRate: 35m);

        Assert.Equal(33_950m, item.ExpectedCashAmount);
        var r = BankMatchAmountReconciler.Reconcile(33_950m, BankFlowDirection.Inflow, new[] { item });
        Assert.True(r.Ok);
    }

    [Fact]
    public void อัตรา1_ต้องได้ยอดเดิมเป๊ะไม่ผ่านการปัดเศษ()
    {
        // ครึ่ง "ของเดิมไม่ถูกแตะ": ใบ THB ทุกใบเดินทางผ่าน ConversionRate = 1
        var item = new BankMatchAmountReconciler.Item(
            Guid.NewGuid(), "ใบสำคัญจ่าย PV-1", BankFlowDirection.Outflow,
            RecordedAmount: 7490.005m, WithheldAmount: 0m);
        Assert.Equal(7490.005m, item.ExpectedCashAmount);
    }
}
