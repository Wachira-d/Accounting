using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 194 ทีม D — ที่พัก: เงินประกันความเสียหายแยกจากมัดจำค่าห้อง + ประเภทเงินมัดจำของที่พัก (spec S2/S3/S4)
/// <list type="bullet">
/// <item><b>ครึ่งแรก (ใบที่ต้องยังถูก)</b>: ใบมัดจำเดิม (ลักษณะ NULL) · มัดจำค่าห้องที่เป็นราคา — ยังอยู่ในแผนเช็คเอาต์/ยกเลิกเหมือนเดิมทุกตัวเลข</item>
/// <item><b>ครึ่งหลัง (ที่เคยพัง)</b>: เงินประกันที่มีเลขจองเดียวกัน — เดิมตัวโหลดหยิบทุกใบ IsDeposit ⇒ ถูกหักเป็นราคาในใบเช็คเอาต์
/// (ขัด DEP-SEC-DEDUCT) หรือถูกริบเป็นค่าปรับยกเลิก · ตอนนี้ถูกกรองออก และปิดได้ 3 ทาง (ตัดชำระ/ริบค่าเสียหาย/คืน)</item>
/// </list>
/// </summary>
public class LodgingSecurityDepositTests
{
    private static readonly Guid Guest = Guid.NewGuid();
    private static readonly Guid Company = Guid.NewGuid();

    // มัดจำค่าห้อง 2,000 รวม VAT 7% (ออกใบกำกับแล้ว) — ลักษณะ "ราคา"
    private static LodgingDepositSnapshot Room2000(DepositNature? nature = DepositNature.PartOfPrice, string no = "TIV-0001")
        => new(Guid.NewGuid(), no, Guest, 1869.16m, 130.84m, 2000m, false, 0m, 0m, null, nature);

    // เงินประกัน 3,000 มัดจำเต็มยอด (VAT 0 · deferred) — ลักษณะ "เงินประกัน"
    private static LodgingDepositSnapshot Security3000(DepositNature? nature = DepositNature.RefundableSecurity,
        decimal realized = 0m, decimal refunded = 0m, Guid? id = null)
        => new(id ?? Guid.NewGuid(), "REC-SEC-01", Guest, 3000m, 0m, 3000m, true, realized, refunded, null, nature);

    // ═══ ครึ่งแรก — ใบที่ถูกอยู่แล้วไม่ถูกแตะ ═══

    [Fact]
    public void มัดจำค่าห้อง_และใบเดิมที่ลักษณะว่าง_ยังอยู่ในแผนมัดจำค่าห้องครบ_ลำดับเดิม()
    {
        var legacy = Room2000(nature: null, no: "TIV-OLD");
        var room = Room2000(no: "TIV-0002");
        var kept = LodgingDepositSettlement.RoomDeposits(new[] { legacy, room }, securityDepositDocumentId: null);
        Assert.Equal(new[] { "TIV-OLD", "TIV-0002" }, kept.Select(d => d.Number));
    }

    [Fact]
    public void ไม่มีเงินประกัน_แผนเช็คเอาต์เท่าเดิมทุกตัวเลข()
    {
        var room = Room2000();
        var before = LodgingDepositSettlement.PlanCheckout(new[] { room }, 1_000_000m, _ => 1_000_000m);
        var after = LodgingDepositSettlement.PlanCheckout(
            LodgingDepositSettlement.RoomDeposits(new[] { room }, null), 1_000_000m, _ => 1_000_000m);
        Assert.Equal(before.BaseDeducted, after.BaseDeducted);
        Assert.Equal(1869.16m, after.BaseDeducted);
        Assert.Equal(before.GrossApplied, after.GrossApplied);
        Assert.Equal(before.ExcessGross, after.ExcessGross);
    }

    // ═══ ครึ่งหลัง — เงินประกันต้องไม่เข้าแผนมัดจำค่าห้อง ═══

    [Fact]
    public void เงินประกันที่เลขจองเดียวกัน_ไม่ถูกหักเป็นราคาในใบเช็คเอาต์()
    {
        var room = Room2000();
        var sec = Security3000();
        // ก่อนแก้: ตัวโหลดคืนทั้งสองใบ ⇒ เงินประกัน 3,000 ถูกตัดชำระใบเช็คเอาต์ (เป็น "ราคา")
        var buggy = LodgingDepositSettlement.PlanCheckout(new[] { room, sec }, 1_000_000m, _ => 1_000_000m);
        Assert.Equal(3000m, buggy.GrossApplied);

        var plan = LodgingDepositSettlement.PlanCheckout(
            LodgingDepositSettlement.RoomDeposits(new[] { room, sec }, null), 1_000_000m, _ => 1_000_000m);
        Assert.Equal(0m, plan.GrossApplied);
        Assert.Empty(plan.Excess);
        Assert.Equal(1869.16m, plan.BaseDeducted);
    }

    [Fact]
    public void เงินประกันที่ลักษณะว่างแต่การจองผูกไว้_ถูกกรองด้วยid()
    {
        var secId = Guid.NewGuid();
        var sec = Security3000(nature: null, id: secId);
        var kept = LodgingDepositSettlement.RoomDeposits(new[] { Room2000(), sec }, secId);
        Assert.Single(kept);
        Assert.Same(sec, LodgingDepositSettlement.SecurityDeposit(new[] { Room2000(), sec }, secId));
    }

    [Fact]
    public void ยกเลิก_ค่าปรับริบเฉพาะมัดจำค่าห้อง_เงินประกันไม่ถูกริบ()
    {
        var room = Room2000();
        var sec = Security3000();
        // ค่าปรับ 5,000 > มัดจำค่าห้อง — ก่อนแก้จะริบเงินประกัน 3,000 ไปด้วย
        var buggy = LodgingDepositSettlement.PlanCancellation(5000m, 5000m, new[] { room, sec });
        Assert.Equal(5000m, buggy.Forfeit);

        var plan = LodgingDepositSettlement.PlanCancellation(5000m, 2000m,
            LodgingDepositSettlement.RoomDeposits(new[] { room, sec }, null));
        Assert.Equal(2000m, plan.Forfeit);
        Assert.Equal(3000m, plan.UncollectedFee);
        Assert.DoesNotContain(plan.Lines, l => l.Number == "REC-SEC-01");
    }

    // ═══ ปิดเงินประกัน 3 ทาง (spec S3) ═══

    [Fact]
    public void คืนเต็ม_ไม่มีตัดชำระไม่มีริบ()
    {
        var (plan, problem) = LodgingDepositSettlement.PlanSecuritySettlement(Security3000(), 0m, 0m, null, sameContact: false);
        Assert.Null(problem);
        Assert.Equal(3000m, plan!.HeldGross);
        Assert.Equal(3000m, plan.RemainderGross);
        Assert.Equal(0m, plan.ForfeitBase);
    }

    [Fact]
    public void หักค่าเสียหายเข้าใบเช็คเอาต์ที่คิดVAT_แล้วคืนส่วนที่เหลือ()
    {
        // ใบเช็คเอาต์ค้าง 535 (ค่าเสียหาย 500 + VAT 35 — VAT อยู่ในใบนั้นแล้ว) ⇒ ตัดชำระ 535 · คืน 2,465
        var (plan, problem) = LodgingDepositSettlement.PlanSecuritySettlement(Security3000(), 535m, 0m, 535m, sameContact: true);
        Assert.Null(problem);
        Assert.Equal(535m, plan!.ApplyGross);
        Assert.Equal(2465m, plan.RemainderGross);
    }

    [Fact]
    public void ริบเป็นค่าเสียหาย_ฐานเท่ายอด_คืนส่วนที่เหลือ()
    {
        var (plan, problem) = LodgingDepositSettlement.PlanSecuritySettlement(Security3000(), 0m, 800m, null, sameContact: false);
        Assert.Null(problem);
        Assert.Equal(800m, plan!.ForfeitGross);
        Assert.Equal(800m, plan.ForfeitBase);   // เงินประกันเต็มยอด VAT 0 ⇒ ฐาน = ยอด
        Assert.Equal(2200m, plan.RemainderGross);
    }

    [Fact]
    public void ตัดชำระ_ริบ_คืน_รวมเท่าเงินประกันพอดี()
    {
        var (plan, _) = LodgingDepositSettlement.PlanSecuritySettlement(Security3000(), 1000m, 500m, 1200m, sameContact: true);
        Assert.Equal(3000m, plan!.ApplyGross + plan.ForfeitGross + plan.RemainderGross);
    }

    [Fact]
    public void ตัดชำระเกินยอดค้างของใบ_ถูกปฏิเสธพร้อมทางไปต่อ()
    {
        var (plan, problem) = LodgingDepositSettlement.PlanSecuritySettlement(Security3000(), 1000m, 0m, 535m, sameContact: true);
        Assert.Null(plan);
        Assert.Contains("เกินยอดค้าง", problem);
    }

    [Fact]
    public void ตัดชำระเมื่อยังไม่มีใบเช็คเอาต์_หรือใบออกในนามอื่น_ถูกปฏิเสธ()
    {
        Assert.Contains("ยังไม่มีใบเช็คเอาต์", LodgingDepositSettlement.PlanSecuritySettlement(Security3000(), 100m, 0m, null, true).Problem);
        Assert.Contains("ในนามอื่น", LodgingDepositSettlement.PlanSecuritySettlement(Security3000(), 100m, 0m, 535m, false).Problem);
    }

    [Fact]
    public void เงินประกันที่ออกใบกำกับไปแล้ว_ตัดชำระใบกำกับอีกใบไม่ได้_แต่คืนได้()
    {
        var taxed = new LodgingDepositSnapshot(Guid.NewGuid(), "TIV-SEC", Guest, 2803.74m, 196.26m, 3000m, false, 0m, 0m, null,
            DepositNature.RefundableSecurity);
        Assert.Contains("VAT ซ้ำ", LodgingDepositSettlement.PlanSecuritySettlement(taxed, 100m, 0m, 535m, true).Problem);
        var (plan, problem) = LodgingDepositSettlement.PlanSecuritySettlement(taxed, 0m, 0m, null, false);
        Assert.Null(problem);
        Assert.Equal(3000m, plan!.RemainderGross);
    }

    [Fact]
    public void ตัดชำระบวกริบเกินเงินประกัน_หรือปิดไปแล้ว_ถูกปฏิเสธ()
    {
        Assert.Contains("เกินเงินประกันคงเหลือ",
            LodgingDepositSettlement.PlanSecuritySettlement(Security3000(), 2000m, 1500m, 5000m, true).Problem);
        Assert.Contains("ปิดไปแล้ว",
            LodgingDepositSettlement.PlanSecuritySettlement(Security3000(refunded: 3000m), 0m, 0m, null, false).Problem);
        Assert.Contains("ติดลบ",
            LodgingDepositSettlement.PlanSecuritySettlement(Security3000(), -1m, 0m, 535m, true).Problem);
    }

    // ═══ ตัวตัดสินประเภทของที่พัก (ResolveKind · บริการ §78/1) ═══

    private static DepositKind Kind(DepositNature nature, DepositVatTreatment? t, bool isDefault = false, string code = "K")
        => new() { Id = Guid.NewGuid(), CompanyId = Company, Code = code, Name = code, Nature = nature, VatTreatment = t, IsDefault = isDefault, IsActive = true };

    [Fact]
    public void ที่พักไม่ตั้งประเภท_ใช้ประเภทเริ่มต้นบริษัท_โหมดตามค่าบริษัท_คำเตือนบริการ()
    {
        var advance = Kind(DepositNature.PartOfPrice, null, isDefault: true, code: "ADVANCE");
        var d = DepositPolicyResolver.ResolveKind(Company, DepositSupplyNature.Service, null, null, null, advance, DepositVatTreatment.FullDeposit);
        Assert.Equal(advance.Id, d.KindId);
        Assert.Equal(DepositVatTreatment.FullDeposit, d.Treatment);
        Assert.Equal(DepositPolicyResolver.KindPriceServiceRuleCode, d.RuleCode);
    }

    [Fact]
    public void ประเภทของที่พักชนะประเภทเริ่มต้นบริษัท_และค่าเดิมของที่พักไม่ถูกอ่านเมื่อมีประเภท()
    {
        var advance = Kind(DepositNature.PartOfPrice, null, isDefault: true, code: "ADVANCE");
        var room = Kind(DepositNature.PartOfPrice, DepositVatTreatment.VatImmediate, code: "ROOM");
        var d = DepositPolicyResolver.ResolveKind(Company, DepositSupplyNature.Service, null, room,
            DepositVatTreatment.VatPendingUndue, advance, DepositVatTreatment.FullDeposit);
        Assert.Equal(room.Id, d.KindId);
        Assert.Equal(DepositVatTreatment.VatImmediate, d.Treatment);
        Assert.Null(d.Warning);
    }

    [Fact]
    public void เงินประกันเป็นประเภทบนใบ_บัญชี21530เมื่อผังโรงแรมมี_ไม่งั้น21620()
    {
        var sec = Kind(DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit, code: "SECURITY");
        Assert.Equal("21530", DepositPolicyResolver.ResolveKind(Company, DepositSupplyNature.Service, sec, null, null, null, null, chartHas21530: true).LiabilityAccountCode);
        Assert.Equal("21620", DepositPolicyResolver.ResolveKind(Company, DepositSupplyNature.Service, sec, null, null, null, null).LiabilityAccountCode);
    }
}
