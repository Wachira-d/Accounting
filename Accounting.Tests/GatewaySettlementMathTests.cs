using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คณิตของการบันทึก "เงินที่ผู้ให้บริการรับชำระเงินโอนเข้าธนาคาร" (settlement)
///
/// ═══ invariant ที่ต้องจริงเสมอ ═══
/// <code>Dr ธนาคาร + Dr ค่าธรรมเนียม(ก่อนหัก) = Cr 11340 + Cr ภาษีหัก ณ ที่จ่าย</code>
/// ถ้าไม่ balance = JE ลงไม่ได้ · ถ้ายอดธนาคารไม่ตรงสเตทเมนต์ = กระทบยอดไม่ได้ตลอดไป
/// </summary>
public class GatewaySettlementMathTests
{
    private static SettlementIntentInput I(decimal amount, decimal? feeActual = null,
        decimal feeEst = 0m)
        => new(Guid.NewGuid(), amount, feeActual, feeEst);

    private static SettlementPlan Plan(IReadOnlyCollection<SettlementIntentInput> rows,
        decimal actualNet, GatewayFeeWhtMode wht = GatewayFeeWhtMode.None)
        => GatewaySettlementMath.Plan(rows, actualNet, wht, "TRF-001");

    // ── ทางปกติ ──

    [Fact]
    public void ยอดตรง_ไม่หัก_ณ_ที่จ่าย_ได้แผน_3_บรรทัด()
    {
        // 2 รายการ 1,000 + 500 · ค่าธรรมเนียมจริง 32.10 + 16.05 = 48.15 ⇒ สุทธิ 1,451.85
        var p = Plan(new[] { I(1000m, 32.10m), I(500m, 16.05m) }, 1451.85m);

        Assert.True(p.Ok);
        Assert.Equal(1500m, p.Gross);
        Assert.Equal(48.15m, p.FeeNetPaid);
        Assert.Equal(0m, p.WhtOnFee);
        Assert.Equal(48.15m, p.FeeGrossedUp);
        Assert.Equal(1451.85m, p.ExpectedNet);
        Assert.Equal(3, p.Lines.Count);   // ธนาคาร + ค่าธรรมเนียม + ล้าง 11340
    }

    [Fact]
    public void ทุกแผนที่ผ่าน_เดบิตต้องเท่ากับเครดิตเสมอ()
    {
        // ไล่หลายรูปทรง — invariant นี้พังเมื่อไรคือ JE ลงไม่ได้
        (decimal[] amounts, decimal[] fees, GatewayFeeWhtMode wht)[] cases =
        {
            (new[] { 1000m }, new[] { 32.10m }, GatewayFeeWhtMode.None),
            (new[] { 1000m }, new[] { 32.10m }, GatewayFeeWhtMode.Withhold3Percent),
            (new[] { 999.99m, 0.01m }, new[] { 3.21m, 0.01m }, GatewayFeeWhtMode.Withhold3Percent),
            (new[] { 12345.67m }, new[] { 411.52m }, GatewayFeeWhtMode.Withhold3Percent),
            (new[] { 100m, 200m, 300m }, new[] { 3.21m, 6.42m, 9.63m }, GatewayFeeWhtMode.Withhold3Percent),
        };
        foreach (var (amounts, fees, wht) in cases)
        {
            var rows = amounts.Zip(fees, (a, f) => I(a, f)).ToList();
            var net = amounts.Sum() - fees.Sum();
            var p = Plan(rows, net, wht);
            Assert.True(p.Ok, p.Message);
            var dr = p.Lines.Sum(l => l.Debit);
            var cr = p.Lines.Sum(l => l.Credit);
            Assert.Equal(dr, cr);
        }
    }

    // ── หัก ณ ที่จ่ายบนค่าธรรมเนียม (§3 เตรส · ภ.ง.ด.53) ──

    [Fact]
    public void หัก_ณ_ที่จ่าย_ต้อง_gross_up_ไม่ใช่คิด_3_เปอร์เซ็นต์จากยอดสุทธิ()
    {
        // ผู้ให้บริการหักค่าธรรมเนียมไป 97 บาท = ยอด**สุทธิ**ที่เขาได้รับ
        // ⇒ ค่าบริการก่อนหัก = 100 · ภาษี = 3 (ไม่ใช่ 97 × 3% = 2.91)
        var p = Plan(new[] { I(1000m, 97m) }, 903m, GatewayFeeWhtMode.Withhold3Percent);

        Assert.True(p.Ok, p.Message);
        Assert.Equal(97m, p.FeeNetPaid);
        Assert.Equal(3m, p.WhtOnFee);
        Assert.Equal(100m, p.FeeGrossedUp);
    }

    [Fact]
    public void ก่อนหัก_ลบภาษี_ต้องเท่ากับสุทธิที่จ่ายจริงเป๊ะทุกยอด()
    {
        // ล็อก invariant ที่ยอดบนหนังสือรับรอง 50 ทวิ ต้องสอดคล้องกับเงินที่ผู้ให้
        // บริการได้รับจริง: ก่อนหัก − ภาษี = สุทธิ
        //
        // ⚠️ หมายเหตุความซื่อสัตย์: สูตรทางเลือก round(สุทธิ/0.97, 2) **ไม่ได้ผิด** —
        // ไล่ทุกสตางค์ ฿0.01–฿5,000 แล้วให้ผลเท่ากันทุกยอด (ต่างกัน 0 ยอด) เทสต์นี้
        // จึงไม่ใช่ "หลักฐานว่าเคยมีบั๊ก" แต่เป็นการตรึง**คุณสมบัติเชิงโครงสร้าง**
        // ไว้กันคนถัดไปเปลี่ยนไปใช้รูปที่ต้องพึ่งความบังเอิญของการปัด
        for (var cents = 1; cents <= 20000; cents++)
        {
            var feeNet = cents / 100m;
            var p = Plan(new[] { I(100000m, feeNet) }, 100000m - feeNet,
                GatewayFeeWhtMode.Withhold3Percent);
            Assert.True(p.Ok, p.Message);
            Assert.Equal(feeNet, p.FeeGrossedUp - p.WhtOnFee);
        }
    }

    [Fact]
    public void หัก_ณ_ที่จ่าย_ไม่กระทบยอดเงินที่เข้าธนาคาร()
    {
        // ภาษีหัก ณ ที่จ่ายเป็นภาระที่บริษัทต้องนำส่ง ไม่ใช่เงินที่หายจากยอดโอน
        var withhold = Plan(new[] { I(1000m, 32.10m) }, 967.90m, GatewayFeeWhtMode.Withhold3Percent);
        var plain = Plan(new[] { I(1000m, 32.10m) }, 967.90m);

        Assert.True(withhold.Ok);
        Assert.Equal(plain.ExpectedNet, withhold.ExpectedNet);
        Assert.Equal(967.90m,
            withhold.Lines.Single(l => l.Role == SettlementLineRole.Bank).Debit);
    }

    [Fact]
    public void โหมดหัก_ต้องมีบรรทัดภาษีค้างนำส่ง_โหมดปกติต้องไม่มี()
    {
        Assert.Contains(Plan(new[] { I(1000m, 32.10m) }, 967.90m, GatewayFeeWhtMode.Withhold3Percent).Lines,
            l => l.Role == SettlementLineRole.WhtPayable);
        Assert.DoesNotContain(Plan(new[] { I(1000m, 32.10m) }, 967.90m).Lines,
            l => l.Role == SettlementLineRole.WhtPayable);
    }

    // ── ด่านที่ต้องบล็อก ──

    [Fact]
    public void ไม่มีรายการ_ต้องบล็อกพร้อมบอกทางแก้()
    {
        var p = Plan(Array.Empty<SettlementIntentInput>(), 1000m);
        Assert.False(p.Ok);
        Assert.Equal(SettlementBlockReason.NoIntents, p.Reason);
        Assert.False(string.IsNullOrWhiteSpace(p.Message));
    }

    [Fact]
    public void ยอดโอนจริงไม่ตรง_ต้องบล็อก_ห้ามปัดให้ลงตัว()
    {
        // ต่างกัน 100 บาท — ระบบต้องไม่ "ยัด" ส่วนต่างเข้าค่าธรรมเนียมให้ลงตัว
        var p = Plan(new[] { I(1000m, 32.10m) }, 867.90m);
        Assert.False(p.Ok);
        Assert.Equal(SettlementBlockReason.NetMismatch, p.Reason);
        Assert.Empty(p.Lines);                       // ไม่มีแผน JE ให้ลงเลย
        Assert.Contains("967.90", p.Message);        // บอกยอดที่คำนวณได้
        Assert.Contains("-100.00", p.Message);       // และบอกว่าต่างเท่าไร
    }

    [Fact]
    public void ต่างในระดับเศษสตางค์_ยอมรับได้()
    {
        // ผู้ให้บริการปัดเศษเองต่างจากเรา 1 สตางค์ = สภาพปกติ ไม่ใช่ของผิด
        var p = Plan(new[] { I(1000m, 32.10m) }, 967.91m);
        Assert.True(p.Ok, p.Message);
        // แต่ JE ยังลงตามที่ "คำนวณได้" ไม่ใช่ตามยอดที่พิมพ์มา — งบต้องอธิบายได้
        Assert.Equal(967.90m, p.Lines.Single(l => l.Role == SettlementLineRole.Bank).Debit);
    }

    [Fact]
    public void ค่าธรรมเนียมมากกว่ายอดรับชำระ_ต้องบล็อก()
    {
        var p = Plan(new[] { I(100m, 500m) }, -400m);
        Assert.False(p.Ok);
        Assert.Equal(SettlementBlockReason.NegativeNet, p.Reason);
    }

    // ── ค่าธรรมเนียม: ตัวจริงชนะตัวประมาณ ──

    [Fact]
    public void ใช้ค่าธรรมเนียมจริงเมื่อมี_ตัวประมาณเป็นทางเลือกสุดท้าย()
    {
        // รายการแรกมีตัวจริง 30 (ตัวประมาณ 99 ต้องไม่ถูกใช้) · รายการสองมีแต่ตัวประมาณ 16.05
        var p = Plan(new[] { I(1000m, feeActual: 30m, feeEst: 99m), I(500m, feeEst: 16.05m) },
            1453.95m);
        Assert.True(p.Ok, p.Message);
        Assert.Equal(46.05m, p.FeeNetPaid);
    }

    [Fact]
    public void แผนต้องคืนรายการที่เลือกครบ_เพื่อให้มาร์กได้ถูกตัว()
    {
        var rows = new[] { I(100m, 3m), I(200m, 6m), I(300m, 9m) };
        var p = Plan(rows, 582m);
        Assert.True(p.Ok, p.Message);
        Assert.Equal(rows.Select(r => r.IntentId).ToHashSet(), p.IntentIds.ToHashSet());
    }

    [Fact]
    public void ไม่มีค่าธรรมเนียมเลย_ต้องไม่มีบรรทัดค่าธรรมเนียม()
    {
        var p = Plan(new[] { I(1000m, 0m) }, 1000m);
        Assert.True(p.Ok, p.Message);
        Assert.DoesNotContain(p.Lines, l => l.Role == SettlementLineRole.FeeExpense);
        Assert.Equal(2, p.Lines.Count);
    }
}
