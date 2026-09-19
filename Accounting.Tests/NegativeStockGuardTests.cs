using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// DECISION_AUDIT_2026-09-18 D5-6 (ราก SYSTEM_REVIEW E-03) — ด่านสต็อกติดลบ
///
/// <para>ด่านนี้เคยอยู่ใน <c>InventoryCostingService.ResolveOutboundCostAsync</c> ซึ่ง
/// <c>StockLedger.MoveAsync</c> เรียกเฉพาะตอนผู้เรียกไม่ส่ง <c>UnitCostOverride</c>
/// — และเส้นเอกสารส่งเสมอ ⇒ <c>AllowNegativeStock=false</c> ไม่เคยถูกบังคับจริง
/// ตอนนี้ ledger ถามด่านนี้กับ**ทุก** delta &lt; 0</para>
///
/// <para>เทสต์มีทั้งสองทิศ: เคสที่ต้องถูกบล็อก **และ** เคสที่ต้องผ่านเหมือนเดิม
/// (allow=true · ไม่ตามสต็อก · รับเข้า · ตัดพอดี) — ด่านที่ฟ้องทุกใบ = ปิดด่าน
/// โดยไม่ตั้งใจ (CLAUDE.md กฎเหล็ก #4 F2 ข้อ 8)</para>
/// </summary>
public class NegativeStockGuardTests
{
    // ── ทิศที่ต้องบล็อก ────────────────────────────────────────────────

    [Fact]
    public void สต็อก3_ตัดออก5_ไม่อนุญาตติดลบ_ต้องบล็อก()
    {
        var v = NegativeStockGuard.Evaluate(trackStock: true, currentQty: 3m, delta: -5m,
            allowNegative: false, productCode: "RM-01", productName: "ใบชา");
        Assert.True(v.Blocked);
        Assert.Equal(2m, v.Shortfall);
    }

    [Fact]
    public void ข้อความที่บล็อก_ต้องบอกทางไปต่อของผู้ใช้()
    {
        var v = NegativeStockGuard.Evaluate(true, 3m, -5m, false, "RM-01", "ใบชา", "กก.");
        Assert.NotNull(v.Message);
        Assert.Contains("อนุญาตสต๊อกติดลบ", v.Message);       // ทางไปต่อ
        Assert.Contains("ตั้งค่าบริษัท", v.Message);            // ไปทำที่ไหน
        Assert.Contains("RM-01", v.Message);                    // ใบไหน/ตัวไหน
        Assert.Contains("ใบชา", v.Message);
        Assert.Contains("กก.", v.Message);                      // หน่วยนับ
    }

    [Fact]
    public void สต็อกติดลบอยู่แล้ว_ตัดเพิ่มอีก_ยังบล็อก()
    {
        var v = NegativeStockGuard.Evaluate(true, -2m, -1m, false, "P1", "สินค้า");
        Assert.True(v.Blocked);
        Assert.Equal(3m, v.Shortfall);
    }

    [Fact]
    public void เศษส่วน_ขาดแค่นิดเดียวก็บล็อก()
    {
        var v = NegativeStockGuard.Evaluate(true, 0.5m, -0.75m, false, "RM-02", "นมสด");
        Assert.True(v.Blocked);
        Assert.Equal(0.25m, v.Shortfall);
    }

    // ── ทิศตรงข้าม: ต้องผ่านเหมือนเดิม ─────────────────────────────────

    [Fact]
    public void เปิดอนุญาตสต๊อกติดลบ_ต้องผ่านทุกเส้น()
    {
        var v = NegativeStockGuard.Evaluate(true, 3m, -5m, allowNegative: true,
            productCode: "RM-01", productName: "ใบชา");
        Assert.False(v.Blocked);
        Assert.Null(v.Message);
        Assert.Equal(2m, v.Shortfall);   // ยังรายงานส่วนที่ขาดให้ผู้เรียกใช้ต่อได้
    }

    [Fact]
    public void สินค้าไม่ตามสต็อก_เช่นบริการ_ไม่ถูกแตะแม้ไม่อนุญาตติดลบ()
    {
        var v = NegativeStockGuard.Evaluate(trackStock: false, currentQty: 0m, delta: -99m,
            allowNegative: false, productCode: "SV-01", productName: "ค่าบริการ");
        Assert.False(v.Blocked);
    }

    [Fact]
    public void ตัดออกพอดีเท่าที่มี_ขอบเขต_ต้องผ่าน()
    {
        var v = NegativeStockGuard.Evaluate(true, 3m, -3m, false, "P1", "สินค้า");
        Assert.False(v.Blocked);
        Assert.Equal(0m, v.Shortfall);
    }

    [Fact]
    public void รับเข้า_deltaบวก_ด่านไม่แตะแม้ยอดปัจจุบันติดลบ()
    {
        var v = NegativeStockGuard.Evaluate(true, -5m, +2m, false, "P1", "สินค้า");
        Assert.False(v.Blocked);
    }

    [Fact]
    public void ไม่มีการเปลี่ยนแปลง_deltaศูนย์_ด่านไม่แตะ()
    {
        var v = NegativeStockGuard.Evaluate(true, 0m, 0m, false, "P1", "สินค้า");
        Assert.False(v.Blocked);
    }

    [Fact]
    public void ตรวจนับได้ศูนย์จากยอดเดิม10_deltaลบ10_ยังผ่านเพราะยอดหลังนับเป็นศูนย์()
    {
        // เส้น SetAbsolute (ตรวจนับ) — "ยอดที่นับได้" ไม่เคยติดลบ ⇒ ด่านต้องไม่ขวาง
        var v = NegativeStockGuard.Evaluate(true, 10m, -10m, false, "P1", "สินค้า");
        Assert.False(v.Blocked);
    }

    [Fact]
    public void ช่องข้ามด่าน_ต้องปิดเป็นค่าตั้งต้น()
    {
        // `AllowNegativeOverride` มีไว้ให้เส้น **กลับรายการ (void)** เท่านั้น —
        // ถ้าใครเผลอเปลี่ยน default เป็น true ด่านทั้งระบบจะหายไปเงียบ ๆ
        // (บั๊ก D5-6 รอบก่อนก็เกิดแบบนี้: ช่องที่ไม่ได้แปลว่า "ปิดด่าน" กลับปิดด่าน)
        var r = new Accounting.Services.Interfaces.StockMoveRequest(
            CompanyId: Guid.NewGuid(), ProductId: Guid.NewGuid(),
            Quantity: -5m, MovementType: "OUT");
        Assert.False(r.AllowNegativeOverride);

        // ทิศตรงข้าม: เส้น void ตั้งได้จริง
        var reversal = r with { AllowNegativeOverride = true };
        Assert.True(reversal.AllowNegativeOverride);
    }
}
