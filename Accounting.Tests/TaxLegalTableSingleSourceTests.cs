using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตารางกฎหมายภาษีเงินได้บุคคลธรรมดา — <b>สำเนาเดียว</b>
/// (งานค้าง "ลดหย่อน 3 สำเนา" · ทีม E ข้อ 3)
///
/// <para><b>ครึ่งที่ 1 (ของที่พังต้องกลับมาถูก)</b> — ขั้น §48(1) และค่าลดหย่อน
/// §47 เคยถูกพิมพ์ไว้ 3 ที่ (entity · controller · service) ⇒ แก้ไม่ครบเมื่อไร
/// หน้าตั้งค่ากับเครื่องคิดภาษีเล่าคนละเรื่อง · ตอนนี้ทุกที่อ้าง
/// <c>ThaiPitCalculator</c> — เทสต์นี้ล็อกว่า JSON ที่หน้าตั้งค่าได้รับ
/// <b>ถอดกลับเป็นขั้นชุดเดียวกับที่เครื่องคิดภาษีใช้</b></para>
///
/// <para><b>ครึ่งที่ 2 (ของที่ถูกอยู่แล้วห้ามถูกแตะ)</b> — ค่าที่ระบบเคยใช้มาตลอด
/// (60,000 / 60,000 / 30,000 / 60,000 / 30,000 / 100,000 / 100,000 / 25,000 /
/// 500,000 / 100,000 / 10%) ต้อง<b>ไม่เปลี่ยนแม้สตางค์เดียว</b> จากการยุบสำเนา —
/// ถ้าตัวเลขขยับ payroll run ที่คำนวณไปแล้วจะไม่ตรงกับที่ยื่นไปแล้ว</para>
/// </summary>
public class TaxLegalTableSingleSourceTests
{
    // ══════════════════════════════════════════════════════════════════
    // ครึ่งที่ 2 — ตัวเลขเดิมห้ามขยับ (ยุบสำเนาต้องเป็น no-op)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void Section47DefaultsAreUnchanged()
    {
        Assert.Equal(60_000m, ThaiPitCalculator.DefaultPersonalAllowance);
        Assert.Equal(60_000m, ThaiPitCalculator.DefaultSpouseAllowance);
        Assert.Equal(30_000m, ThaiPitCalculator.DefaultChildAllowance);
        Assert.Equal(60_000m, ThaiPitCalculator.DefaultChildAllowancePost2561);
        Assert.Equal(30_000m, ThaiPitCalculator.DefaultParentAllowance);
        Assert.Equal(100_000m, ThaiPitCalculator.DefaultExpenseCap);
        Assert.Equal(100_000m, ThaiPitCalculator.DefaultLifeInsuranceCap);
        Assert.Equal(25_000m, ThaiPitCalculator.DefaultHealthInsuranceCap);
        Assert.Equal(500_000m, ThaiPitCalculator.DefaultPvdCap);
        Assert.Equal(100_000m, ThaiPitCalculator.DefaultMortgageInterestCap);
        Assert.Equal(10m, ThaiPitCalculator.DefaultDonationCapPercent);
    }

    /// <summary>ค่าตั้งต้นของ <c>TaxRuleConfig</c> (แถวที่บริษัทยังไม่เคย override)
    /// ต้องมาจากตารางกลาง — ไม่ใช่ literal ในไฟล์ entity</summary>
    [Fact]
    public void TaxRuleConfigEntityDefaultsComeFromTheTable()
    {
        var cfg = new Accounting.Models.Entities.TaxRuleConfig();
        Assert.Equal(ThaiPitCalculator.DefaultPersonalAllowance, cfg.PersonalAllowance);
        Assert.Equal(ThaiPitCalculator.DefaultSpouseAllowance, cfg.SpouseAllowance);
        Assert.Equal(ThaiPitCalculator.DefaultChildAllowance, cfg.ChildAllowance);
        Assert.Equal(ThaiPitCalculator.DefaultChildAllowancePost2561, cfg.ChildAllowancePost2561);
        Assert.Equal(ThaiPitCalculator.DefaultParentAllowance, cfg.ParentAllowance);
        Assert.Equal(ThaiPitCalculator.DefaultExpenseCap, cfg.Section42TwiCap);
        Assert.Equal(ThaiPitCalculator.DefaultLifeInsuranceCap, cfg.LifeInsuranceCap);
        Assert.Equal(ThaiPitCalculator.DefaultHealthInsuranceCap, cfg.HealthInsuranceCap);
        Assert.Equal(ThaiPitCalculator.DefaultPvdCap, cfg.PvdCap);
        Assert.Equal(ThaiPitCalculator.DefaultMortgageInterestCap, cfg.MortgageInterestCap);
        Assert.Equal(ThaiPitCalculator.DefaultDonationCapPercent, cfg.DonationCapPercent);
    }

    // ══════════════════════════════════════════════════════════════════
    // ครึ่งที่ 1 — JSON ที่หน้าตั้งค่าได้รับ ต้องถอดกลับเป็นขั้นชุดเดียวกัน
    // ══════════════════════════════════════════════════════════════════

    /// <summary>ถอด JSON ด้วย<b>ตรรกะชุดเดียวกับ <c>PayrollService.ParseBrackets</c></b>
    /// (<c>upperBound &lt;= 0</c> = ขั้นสุดท้าย) — ถ้าทั้งสองฝั่งไม่ตรงกัน
    /// ตารางที่หน้าตั้งค่าโชว์จะไม่ใช่ตารางที่เครื่องคิดภาษีใช้</summary>
    private static List<(decimal Upper, decimal Rate)> ParseLikePayrollService(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<(decimal, decimal)>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var u = el.GetProperty("upperBound").GetDecimal();
            var r = el.GetProperty("rate").GetDecimal();
            list.Add((u <= 0 ? decimal.MaxValue : u, r));
        }
        return list.OrderBy(x => x.Item1).ToList();
    }

    [Fact]
    public void DefaultBracketsJsonRoundTripsToTheSameBrackets()
    {
        var parsed = ParseLikePayrollService(ThaiPitCalculator.DefaultBracketsJson());
        var expected = ThaiPitCalculator.DefaultBrackets
            .Select(b => (b.UpperBound, b.Rate)).ToList();

        Assert.Equal(expected.Count, parsed.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].UpperBound, parsed[i].Upper);
            Assert.Equal(expected[i].Rate, parsed[i].Rate);
        }
    }

    /// <summary>ขั้นสุดท้ายต้องออกมาเป็น <c>upperBound = 0</c> —
    /// <c>decimal.MaxValue</c> เขียนลง JSON แล้วฝั่งอ่านจะได้ขั้นที่ไม่มีวันถึง</summary>
    [Fact]
    public void LastBracketIsSerialisedAsZeroCatchAll()
    {
        using var doc = JsonDocument.Parse(ThaiPitCalculator.DefaultBracketsJson());
        var arr = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(8, arr.Count);
        Assert.Equal(0m, arr[^1].GetProperty("upperBound").GetDecimal());
        Assert.Equal(0.35m, arr[^1].GetProperty("rate").GetDecimal());
        Assert.Equal(150_000m, arr[0].GetProperty("upperBound").GetDecimal());
        Assert.Equal(0m, arr[0].GetProperty("rate").GetDecimal());
    }

    /// <summary>ยอดภาษีจากขั้นชุดนี้ต้องเท่าเดิมทุกบาท — เลขอ้างอิงคิดจากขั้น
    /// §48(1) ที่ใช้ตั้งแต่ปีภาษี 2560 (ตัวเดียวกับที่ ภ.ง.ด.91 ใน
    /// <c>TaxService</c> เคยคิดด้วยตารางสำเนาของตัวเอง)</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(150_000, 0)]
    [InlineData(300_000, 7_500)]          // 150k×0 + 150k×5%
    [InlineData(500_000, 27_500)]         // + 200k×10%
    [InlineData(1_000_000, 115_000)]      // + 250k×15% + 250k×20%
    public void AnnualTaxMatchesTheStatutoryLadder(int taxable, int expected)
        => Assert.Equal((decimal)expected, ThaiPitCalculator.AnnualTax(taxable));
}
