using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 194 ทีม C — แคตตาล็อกประเภทเงินมัดจำ (หน้าตั้งค่า · API /deposit-kinds · integration · CMS) · ล็อกสองครึ่ง:
/// <b>ครึ่งแรก</b> ของเดิมไม่ถูกแตะ (payload integration ที่ไม่ส่งรหัส = ข้อความหมายเหตุเดิมทุกตัวอักษร · บริษัทที่ไม่เคยตั้งค่า = CMS
/// ไม่ส่งประเภท) · <b>ครึ่งหลัง</b> ของใหม่ถูก (รูปรหัส · ตารางลักษณะ×โหมดตรงกับด่านบันทึก · ประเภทที่ปิดใช้แสดงค่าของตัวเอง ·
/// รหัสที่ไม่รู้จัก/ปิดใช้ถูกปฏิเสธ)
/// </summary>
public class DepositKindCatalogTests
{
    private static readonly Guid Co = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static DepositKind Kind(string code, DepositNature nature, DepositVatTreatment? t = null,
        bool isDefault = false, bool active = true, string? name = null)
        => new()
        {
            CompanyId = Co, Code = code, Name = name ?? "ประเภท " + code, Nature = nature, VatTreatment = t,
            IsDefault = isDefault, IsActive = active,
        };

    /// <summary>ADVANCE ตามที่ seed ให้ทุกบริษัท (ราคา · โหมด null = ตามค่าตั้งบริษัท · เริ่มต้น)</summary>
    private static DepositKind SeedAdvance() => Kind("ADVANCE", DepositNature.PartOfPrice, null, isDefault: true, name: "มัดจำ/เงินรับล่วงหน้า");

    private static DepositKindCompanyContext Ctx(DepositVatTreatment? companySetting = null, DepositKind? defaultKind = null,
        DepositSupplyNature supply = DepositSupplyNature.Service, bool chart21530 = false)
        => new(Co, supply, companySetting, defaultKind, chart21530);

    // ═════════════════ ครึ่งแรก — ของเดิมไม่ถูกแตะ ═════════════════

    /// <summary>integration payload เดิม (ไม่ส่ง depositKindCode) + บริษัทที่มี ADVANCE ตาม seed ⇒ หมายเหตุ mismatch ตัวใหม่ (ผ่าน ResolveKind)
    /// ต้องเท่ากับตัวเดิม (ผ่าน Resolve) <b>ทุกตัวอักษร</b> ทุกคู่ (ธงคู่ค้า × ประเภทธุรกิจ × ค่าตั้งบริษัท)</summary>
    [Theory]
    [InlineData(true, DepositSupplyNature.Service, null)]
    [InlineData(false, DepositSupplyNature.Service, null)]
    [InlineData(true, DepositSupplyNature.Goods, null)]
    [InlineData(false, DepositSupplyNature.Goods, DepositVatTreatment.FullDeposit)]
    [InlineData(true, DepositSupplyNature.Goods, DepositVatTreatment.FullDeposit)]
    [InlineData(false, DepositSupplyNature.Service, DepositVatTreatment.VatPendingUndue)]
    [InlineData(true, DepositSupplyNature.Unknown, DepositVatTreatment.VatImmediate)]
    [InlineData(false, DepositSupplyNature.Unknown, DepositVatTreatment.VatImmediate)]
    public void IntegrationMismatch_NoPayloadKind_SameTextAsBefore(bool payloadDeferred, DepositSupplyNature supply, DepositVatTreatment? companySetting)
    {
        var before = DepositPolicyResolver.IntegrationMismatchNote(payloadDeferred, DepositPolicyResolver.Resolve(supply, companySetting));
        foreach (var defaultKind in new DepositKind?[] { null, SeedAdvance() })
        {
            var after = DepositPolicyResolver.IntegrationMismatchNote(payloadDeferred,
                DepositKindCatalog.Decide(Ctx(companySetting, defaultKind, supply), documentKind: null));
            Assert.Equal(before, after);
        }
    }

    /// <summary>บริษัทที่ไม่เคยแตะค่าตั้งมัดจำเลย (ค่าตั้ง NULL + ADVANCE โหมด NULL) ⇒ CMS ไม่ส่งประเภท = ใบจองเหมือนเดิม (VAT ทันที)</summary>
    [Fact]
    public void CompanyConfigured_UntouchedCompany_False()
    {
        Assert.False(DepositKindCatalog.CompanyConfigured(null, SeedAdvance()));
        Assert.False(DepositKindCatalog.CompanyConfigured(null, null));
        Assert.False(Ctx(null, SeedAdvance()).CompanyConfigured);
        // ประเภทเริ่มต้นที่ปิดใช้/ลบแล้วไม่นับ (ตัวตัดสินข้ามอยู่แล้ว)
        Assert.False(DepositKindCatalog.CompanyConfigured(null,
            Kind("X", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit, isDefault: true, active: false)));
        var deleted = Kind("Y", DepositNature.PartOfPrice, DepositVatTreatment.FullDeposit, isDefault: true);
        deleted.IsDeleted = true;
        Assert.False(DepositKindCatalog.CompanyConfigured(null, deleted));
    }

    [Fact]
    public void UnusableCodeMessage_ActiveKind_Null()
        => Assert.Null(DepositKindCatalog.UnusableCodeMessage("advance", SeedAdvance()));

    // ═════════════════ ครึ่งหลัง — ของใหม่ถูก ═════════════════

    [Fact]
    public void CompanyConfigured_SettingOrDefaultKindMode_True()
    {
        Assert.True(DepositKindCatalog.CompanyConfigured(DepositVatTreatment.VatImmediate, SeedAdvance()));
        Assert.True(DepositKindCatalog.CompanyConfigured(DepositVatTreatment.FullDeposit, null));
        Assert.True(DepositKindCatalog.CompanyConfigured(null,
            Kind("DEP", DepositNature.PartOfPrice, DepositVatTreatment.VatPendingUndue, isDefault: true)));
        // ค่าขยะของ enum (เช่น 0 จาก select ว่าง) ไม่ถือว่าตั้งแล้ว
        Assert.False(DepositKindCatalog.CompanyConfigured((DepositVatTreatment)0, null));
    }

    [Theory]
    [InlineData("ADVANCE")]
    [InlineData(" rent-adv ")]
    [InlineData("LP-1A2B3C4D")]
    [InlineData("SEC_2")]
    public void CodeProblem_ValidCodes_Pass(string code) => Assert.Null(DepositKindCatalog.CodeProblem(code));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("-ADV")]
    [InlineData("มัดจำ")]
    [InlineData("A B")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456")]   // 33 ตัว
    public void CodeProblem_InvalidCodes_ThaiMessage(string? code)
    {
        var msg = DepositKindCatalog.CodeProblem(code);
        Assert.NotNull(msg);
        Assert.Contains("รหัส", msg);
    }

    [Fact]
    public void NormalizeCode_TrimsAndUppercases()
    {
        Assert.Equal("RENT-ADV", DepositKindCatalog.NormalizeCode("  rent-adv "));
        Assert.Equal("", DepositKindCatalog.NormalizeCode(null));
    }

    [Fact]
    public void NatureOptions_CoverEveryEnumValue_ByName()
    {
        var values = DepositKindCatalog.NatureOptions.Select(o => o.Value).ToArray();
        Assert.Equal(Enum.GetNames<DepositNature>(), values);
        Assert.All(DepositKindCatalog.NatureOptions, o => Assert.Contains("มาตรา", o.LegalReference + o.Description));
        Assert.Equal("ไม่ทราบ", DepositKindCatalog.NatureLabelOf(null));
        Assert.Equal("เงินประกันที่ต้องคืน", DepositKindCatalog.NatureLabelOf(DepositNature.RefundableSecurity));
    }

    /// <summary>ตาราง matrix = ด่านบันทึกตัวจริง (KindProblem) + คำเตือนตัวจริง (KindWarning) — หน้าเว็บเห็นเท่ากับที่เซิร์ฟเวอร์จะตัดสิน</summary>
    [Fact]
    public void Matrix_MatchesSaveGateAndWarnings()
    {
        var m = DepositKindCatalog.Matrix(Ctx(null, SeedAdvance(), DepositSupplyNature.Goods));
        Assert.Equal(12, m.Count);
        DepositKindMatrixCell Cell(DepositNature n, DepositVatTreatment? t)
            => m.Single(c => c.Nature == n.ToString() && c.Treatment == t?.ToString());

        var priceFull = Cell(DepositNature.PartOfPrice, DepositVatTreatment.FullDeposit);
        Assert.True(priceFull.ReasonRequired);
        Assert.Equal(DepositPolicyResolver.KindPriceGoodsRuleCode, priceFull.RuleCode);   // สินค้าเตือนแรงเท่าบริการ (รหัสสินค้า)
        Assert.NotNull(priceFull.Warning);

        var priceImmediate = Cell(DepositNature.PartOfPrice, DepositVatTreatment.VatImmediate);
        Assert.False(priceImmediate.ReasonRequired);
        Assert.Null(priceImmediate.Warning);

        // "ตามค่าตั้งบริษัท" ไม่บังคับเหตุผลที่ประเภท (คำเตือนระดับบริษัทอยู่ที่หัวข้อวิธีบันทึก) · บริษัทไม่ได้ตั้ง ⇒ VAT ทันที
        var priceInherit = Cell(DepositNature.PartOfPrice, null);
        Assert.False(priceInherit.ReasonRequired);
        Assert.Equal(nameof(DepositVatTreatment.VatImmediate), priceInherit.EffectiveTreatment);

        Assert.Null(Cell(DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit).Warning);
        Assert.Equal(DepositPolicyResolver.KindSecurityEarlyVatRuleCode, Cell(DepositNature.RefundableSecurity, DepositVatTreatment.VatImmediate).RuleCode);
        Assert.All(m.Where(c => c.Nature == nameof(DepositNature.NonVatSupply)),
            c => Assert.Equal(DepositPolicyResolver.KindNonVatRuleCode, c.RuleCode));

        // ทุกช่อง: ReasonRequired ตรงกับด่านจริงตอนบันทึก
        foreach (var c in m)
        {
            var n = Enum.Parse<DepositNature>(c.Nature);
            DepositVatTreatment? t = c.Treatment is null ? null : Enum.Parse<DepositVatTreatment>(c.Treatment);
            Assert.Equal(DepositPolicyResolver.KindProblem(n, t, null) != null, c.ReasonRequired);
            Assert.Null(DepositPolicyResolver.KindProblem(n, t, "เลขสัญญา 123"));   // มีเหตุผลแล้วผ่านทุกช่อง
        }
    }

    /// <summary>"ตามค่าตั้งบริษัท" ตามค่าตั้งจริง — บริษัทตั้งมัดจำเต็มยอด ⇒ ประเภทราคาที่ไม่ได้ตั้งโหมดใช้เต็มยอด + คำเตือน</summary>
    [Fact]
    public void Matrix_InheritRow_FollowsCompanySetting()
    {
        var m = DepositKindCatalog.Matrix(Ctx(DepositVatTreatment.FullDeposit, SeedAdvance(), DepositSupplyNature.Service));
        var inherit = m.Single(c => c.Nature == nameof(DepositNature.PartOfPrice) && c.Treatment == null);
        Assert.Equal(nameof(DepositVatTreatment.FullDeposit), inherit.EffectiveTreatment);
        Assert.Equal(DepositPolicyResolver.KindPriceServiceRuleCode, inherit.RuleCode);
        Assert.False(inherit.ReasonRequired);
    }

    /// <summary>ประเภทที่ปิดใช้ต้องแสดงค่าของตัวเองในหน้าตั้งค่า (ไม่ใช่ค่าที่ตัวตัดสินตกไปชั้นถัดไป) · ตัวตัดสินจริงยังข้ามมันเหมือนเดิม</summary>
    [Fact]
    public void Preview_InactiveKind_ShowsOwnValues_DecideStillSkipsIt()
    {
        var ctx = Ctx(null, SeedAdvance());
        var sec = Kind("SECURITY", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit, active: false);
        var preview = DepositKindCatalog.Preview(ctx, sec);
        Assert.Equal(DepositNature.RefundableSecurity, preview.Nature);
        Assert.Equal(DepositVatTreatment.FullDeposit, preview.Treatment);
        Assert.False(sec.IsActive);   // ไม่แตะแถวจริง

        var real = DepositKindCatalog.Decide(ctx, sec);
        Assert.Equal(DepositNature.PartOfPrice, real.Nature);          // ตกไปประเภทเริ่มต้น (ADVANCE)
        Assert.Equal(DepositVatTreatmentSource.BusinessTypeDefault, real.Source);
    }

    [Fact]
    public void UnusableCodeMessage_UnknownOrInactive_Rejected()
    {
        var unknown = DepositKindCatalog.UnusableCodeMessage(" xyz ", null);
        Assert.NotNull(unknown);
        Assert.Contains("XYZ", unknown);
        Assert.Contains("ไม่รู้จัก", unknown);
        var inactive = DepositKindCatalog.UnusableCodeMessage("SECURITY",
            Kind("SECURITY", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit, active: false));
        Assert.NotNull(inactive);
        Assert.Contains("ปิดใช้", inactive);
    }

    /// <summary>คู่ค้าระบุประเภท ⇒ เทียบกับประเภทนั้น: เงินประกันเต็มยอด + ธง "พักรอ" = สอดคล้อง (ไม่มีหมายเหตุ) · ธง "รับรู้แล้ว" = ขัด
    /// (รหัส DEPOSIT-VAT-TREATMENT · อ้างชื่อประเภท) · นอกระบบ VAT ถือว่าพักรอเสมอ</summary>
    [Fact]
    public void IntegrationMismatch_PayloadKind_ComparesAgainstThatKind()
    {
        var ctx = Ctx(null, SeedAdvance());
        var sec = Kind("SECURITY", DepositNature.RefundableSecurity, DepositVatTreatment.FullDeposit, name: "เงินประกันความเสียหาย");
        Assert.Null(DepositPolicyResolver.IntegrationMismatchNote(true, DepositKindCatalog.Decide(ctx, sec)));
        var note = DepositPolicyResolver.IntegrationMismatchNote(false, DepositKindCatalog.Decide(ctx, sec));
        Assert.NotNull(note);
        Assert.StartsWith("[DEPOSIT-VAT-TREATMENT]", note);
        Assert.Contains("เงินประกันความเสียหาย", note);

        var rent = Kind("RENT-ADV", DepositNature.NonVatSupply, DepositVatTreatment.VatImmediate);
        Assert.Null(DepositPolicyResolver.IntegrationMismatchNote(true, DepositKindCatalog.Decide(ctx, rent)));

        // ราคา + VAT ทันที แต่คู่ค้าบอกพักรอ ⇒ รหัส §78/1 เดิม (บริการ)
        var adv = Kind("ADV2", DepositNature.PartOfPrice, DepositVatTreatment.VatImmediate);
        var priceNote = DepositPolicyResolver.IntegrationMismatchNote(true, DepositKindCatalog.Decide(ctx, adv));
        Assert.NotNull(priceNote);
        Assert.StartsWith($"[{DepositPolicyResolver.ServiceNonImmediateRuleCode}]", priceNote);
    }
}
