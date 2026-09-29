using Accounting.Helpers;
using Accounting.Models.DTOs.Settlement;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ลูกค้าช่วงทดลอง/แพ็กเกจฟรีเมื่อเปิดบังคับแพ็กเกจ + D-P5 ผู้สมัครเอกสารขายของรอบโอน** (รอบ 200 คำตัดสินข้อ 14)
///
/// <para>═══ บั๊กที่ล็อกไว้ ═══ (1) สำเนาฟีเจอร์ของ subscription ทดลองว่าง (คอลัมน์ DEFAULT 0 / แอดมินบันทึกรายการว่าง) ⇒ ถูกบล็อกทุกเส้นทางที่ gate
/// ทันทีที่เปิดบังคับ (รวม /tax) โดยไม่มีอะไรบอก (2) ผู้มีแค่ Settlement.View เห็นเลขที่เอกสารขาย + ยอดค้าง ผ่านผู้สมัครของบรรทัดรอบโอน</para>
///
/// <para>สองทิศ: สำเนาที่ไม่ว่าง (ตั้งรายบริษัท) ไม่ถูกแตะ · ลูกค้าแพ็กเกจเสียเงินที่ว่างไม่ถูกเติมเอง · แพ็กเกจว่างไม่แต่งชุดฟีเจอร์ขึ้นเอง ·
/// ผู้มีสิทธิ์นำเข้า/ลงบัญชียังเห็นผู้สมัครครบ</para>
/// </summary>
public class SubscriptionTrialReadinessTests
{
    private static readonly Guid C1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid C2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ════════ ฟีเจอร์ของลูกค้าทดลองตามข้อมูลแพ็กเกจ ════════

    [Fact]
    public void ทดลองสำเนาว่าง_ใช้TrialFeaturesของแพ็กเกจ()
    {
        var r = SubscriptionTrialReadiness.ResolveFeatures(FeatureFlags.None, SubscriptionPlan.Basic, SubscriptionStatus.Trial,
            FeatureFlags.BasicFeatures, FeatureFlags.ProFeatures);
        Assert.Equal(FeatureFlags.BasicFeatures, r.Features);
        Assert.True(r.FromTemplate);
        Assert.False(r.StillEmpty);
    }

    [Fact]
    public void แพ็กเกจฟรีถาวรสำเนาว่าง_ใช้EnabledFeaturesของแพ็กเกจ()
    {
        var r = SubscriptionTrialReadiness.ResolveFeatures(FeatureFlags.None, SubscriptionPlan.FreeTrial, SubscriptionStatus.Active,
            FeatureFlags.None, FeatureFlags.TrialFeatures);
        Assert.Equal(FeatureFlags.TrialFeatures, r.Features);
        Assert.True(r.FromTemplate);
    }

    [Fact]
    public void สำเนาไม่ว่าง_คือค่าที่ตั้งรายบริษัท_ไม่ถูกแทนด้วยแพ็กเกจ()
    {
        var custom = FeatureFlags.TrialFeatures | FeatureFlags.Payroll;
        var r = SubscriptionTrialReadiness.ResolveFeatures(custom, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial,
            FeatureFlags.BasicFeatures, FeatureFlags.BasicFeatures);
        Assert.Equal(custom, r.Features);
        Assert.False(r.FromTemplate);
    }

    [Theory]
    [InlineData(SubscriptionPlan.Pro, SubscriptionStatus.Active)]
    [InlineData(SubscriptionPlan.Enterprise, SubscriptionStatus.PastDue)]
    public void แพ็กเกจเสียเงินสำเนาว่าง_ไม่เติมเอง_นอกขอบเขตคำตัดสิน(SubscriptionPlan plan, SubscriptionStatus status)
    {
        var r = SubscriptionTrialReadiness.ResolveFeatures(FeatureFlags.None, plan, status, FeatureFlags.ProFeatures, FeatureFlags.ProFeatures);
        Assert.Equal(FeatureFlags.None, r.Features);
        Assert.False(r.FromTemplate);
        Assert.False(r.StillEmpty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(FeatureFlags.None)]
    public void แพ็กเกจก็ว่างหรือไม่มีแพ็กเกจ_คงว่าง_ไม่แต่งชุดฟีเจอร์_และติดธงให้รายงาน(FeatureFlags? template)
    {
        var r = SubscriptionTrialReadiness.ResolveFeatures(FeatureFlags.None, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial,
            template, template);
        Assert.Equal(FeatureFlags.None, r.Features);
        Assert.True(r.StillEmpty);
    }

    [Theory]
    [InlineData(SubscriptionPlan.Basic, SubscriptionStatus.Trial, true)]
    [InlineData(SubscriptionPlan.FreeTrial, SubscriptionStatus.Active, true)]
    [InlineData(SubscriptionPlan.Pro, SubscriptionStatus.Active, false)]
    public void ใครคือลูกค้าทดลอง(SubscriptionPlan plan, SubscriptionStatus status, bool expected)
    {
        Assert.Equal(expected, SubscriptionTrialReadiness.IsTrialLike(plan, status));
        Assert.Equal(expected, SubscriptionTrialReadiness.IsTrialLike(plan.ToString(), status.ToString()));
    }

    // ════════ ความพร้อมของแพ็กเกจ (ตามข้อมูลที่แอดมินตั้ง / seeder) ════════

    [Fact]
    public void แพ็กเกจฟรีตามseeder_ไม่ว่าง_รายงานภาษีไม่ถูกปิด_แต่บอกเส้นทางที่จะถูกปิด()
    {
        // ค่าเดียวกับ SeedPlanTemplates "Free Edition" (IsPermanentFree · EnabledFeatures = TrialFeatures)
        var row = SubscriptionTrialReadiness.CheckTemplate(SubscriptionPlan.FreeTrial, "Free Edition", isPermanentFree: true,
            FeatureFlags.TrialFeatures, FeatureFlags.TrialFeatures);
        Assert.Null(row.Problem);
        Assert.Equal("EnabledFeatures", row.FeatureSource);
        Assert.Contains("TaxManagement", row.Features);
        Assert.DoesNotContain("/tax", row.GatedRoutesBlocked);
        Assert.DoesNotContain("TaxManagement", row.GatedFeaturesMissing);
        // ที่จะถูกปิดต้องบอกให้เห็นก่อนเปิดบังคับ (รวม settlement ตาม D-P3)
        Assert.Contains("BankReconciliation", row.GatedFeaturesMissing);
        Assert.Contains("/bank", row.GatedRoutesBlocked);
        Assert.Contains("/settlement", row.GatedRoutesBlocked);
        Assert.Contains("Inventory", row.GatedFeaturesMissing);
    }

    [Fact]
    public void แพ็กเกจทดลองตั้งฟีเจอร์ว่าง_รายงานเป็นปัญหาต้องแก้ก่อนเปิด_และทุกเส้นทางที่gateถูกปิด()
    {
        var row = SubscriptionTrialReadiness.CheckTemplate(SubscriptionPlan.Basic, "Starter", isPermanentFree: false,
            FeatureFlags.BasicFeatures, FeatureFlags.None);
        Assert.NotNull(row.Problem);
        Assert.Contains("TrialFeatures", row.Problem);
        Assert.Equal("TrialFeatures", row.FeatureSource);
        Assert.Equal(SubscriptionGatePolicy.GatedFeatures.Count, row.GatedFeaturesMissing.Count);
        Assert.Contains("/tax", row.GatedRoutesBlocked);
    }

    [Fact]
    public void แพ็กเกจทดลองเต็มชุด_ไม่มีเส้นทางถูกปิด()
    {
        var row = SubscriptionTrialReadiness.CheckTemplate(SubscriptionPlan.Enterprise, "Enterprise", false,
            FeatureFlags.EnterpriseFeatures, FeatureFlags.EnterpriseFeatures);
        Assert.Null(row.Problem);
        Assert.Empty(row.GatedFeaturesMissing);
        Assert.Empty(row.GatedRoutesBlocked);
    }

    // ════════ รายงานเงา: ลูกค้าทดลองถูกบล็อกกี่ครั้งเพราะอะไร ════════

    [Fact]
    public void สรุปลูกค้าทดลอง_นับบริษัทและครั้ง_ไม่ปนลูกค้าเสียเงิน()
    {
        var rows = new[]
        {
            new SubscriptionShadowTally(C1, "FeatureNotInPlan", "BankReconciliation", "FreeTrial", "Active", 5, 0),
            new SubscriptionShadowTally(C1, "FeatureNotInPlan", "BankReconciliation", "FreeTrial", "Active", 2, 1), // endpoint ที่สอง
            new SubscriptionShadowTally(C2, "FeatureNotInPlan", "BankReconciliation", "Basic", "Trial", 3, 0),
            new SubscriptionShadowTally(C2, "PlanExpired", "", "Basic", "Trial", 1, 0),
            new SubscriptionShadowTally(Guid.NewGuid(), "FeatureNotInPlan", "BankReconciliation", "Pro", "Active", 99, 99),
        };
        var s = SubscriptionTrialReadiness.SummarizeTrialBlocks(rows);
        Assert.Equal(2, s.Count);
        var bank = s[0];
        Assert.Equal("BankReconciliation", bank.Feature);
        Assert.Equal(2, bank.Companies);
        Assert.Equal(10, bank.WouldBlockHits);
        Assert.Equal(1, bank.BlockedHits);
        Assert.Contains("BankReconciliation", bank.Description);
        var expired = s[1];
        Assert.Equal("PlanExpired", expired.Reason);
        Assert.Null(expired.Feature);
        Assert.Equal(1, expired.Companies);
    }

    [Fact]
    public void สรุปลูกค้าทดลอง_ไม่มีแถว_ได้รายการว่าง()
    {
        Assert.Empty(SubscriptionTrialReadiness.SummarizeTrialBlocks(Array.Empty<SubscriptionShadowTally>()));
    }

    // ════════ D-P5: ผู้มีแค่ Settlement.View ไม่เห็นผู้สมัคร/ยอดค้าง ════════

    private static SettlementLineView Line(string? note, params SettlementMatchCandidateView[] candidates) => new(
        Guid.NewGuid(), 1, SettlementLineType.Sale, "ยอดขาย", "Order income", null, null, "ORD-1", null, 1070m, null, null,
        SettlementClassifiedBy.AdapterRule, false, null, SettlementMatchStatus.Unmatched, null, null, null, note,
        candidates, null, null);

    private static SettlementBatchView Batch(params SettlementLineView[] lines) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Shopee", "P-1", new DateTime(2026, 9, 1), null, null, "THB", 1000m, 0m, 0m,
        SettlementBatchStatus.Imported, SettlementSourceKind.CsvImport, null, null, 1070m, lines.Length, 0, 1, 0, false, null,
        new DateTime(2026, 9, 1), lines);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void มีสิทธิ์นำเข้าหรือลงบัญชี_เห็นผู้สมัคร(bool canImport, bool canPost)
    {
        Assert.Null(SettlementPermissionScope.CandidatesHiddenReason(canImport, canPost));
    }

    [Fact]
    public void ดูอย่างเดียว_ผู้สมัครและข้อความที่อ้างยอดค้างถูกซ่อน_พร้อมเหตุผล_สถานะยังอยู่()
    {
        var reason = SettlementPermissionScope.CandidatesHiddenReason(false, false);
        Assert.NotNull(reason);
        Assert.Contains("ยอดค้าง", reason);
        var docId = Guid.NewGuid();
        var view = Batch(
            Line("ยอดขายของออเดอร์ในรอบโอน (1,070.00) ไม่เท่ายอดค้างของ INV-0001 — ตรวจก่อนลงบัญชี",
                new SettlementMatchCandidateView("Document", Guid.NewGuid(), "INV-0001", 1070m, true, false, true, null)),
            Line(null) with { MatchStatus = SettlementMatchStatus.Matched, MatchedDocumentId = docId });
        var hidden = SettlementPermissionScope.HideCandidates(view, reason!);
        Assert.All(hidden.Lines, l => Assert.Empty(l.MatchCandidates));
        Assert.Equal(reason, hidden.Lines[0].MatchNote);
        Assert.DoesNotContain(hidden.Lines, l => (l.MatchNote ?? "").Contains("INV-0001"));
        // ผลที่ลงแล้ว/สถานะ ไม่ถูกแตะ · บรรทัดที่ไม่มีข้อความ ไม่ถูกเติมข้อความ
        Assert.Equal(SettlementMatchStatus.Matched, hidden.Lines[1].MatchStatus);
        Assert.Equal(docId, hidden.Lines[1].MatchedDocumentId);
        Assert.Null(hidden.Lines[1].MatchNote);
        Assert.Equal(view.NetPayout, hidden.NetPayout);
        // ของเดิมไม่ถูกแก้ (record ใหม่)
        Assert.Single(view.Lines[0].MatchCandidates);
    }
}
