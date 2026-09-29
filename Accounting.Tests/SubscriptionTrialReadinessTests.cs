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

    private static readonly DateTime Now = new(2026, 9, 29, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>ตัวตัดสินจริง (รอบ 200 S2: รับ <see cref="TrialFeatureInputs"/> + เวลา) · ค่าตั้งต้น = ทดลองยังไม่หมดอายุ · ไม่มี trial config รายบริษัท</summary>
    private static TrialFeatureResolution Resolve(FeatureFlags snapshot, SubscriptionPlan plan, SubscriptionStatus status,
        FeatureFlags? templateTrial, FeatureFlags? templateEnabled, bool permanentFree = false, DateTime? endDate = null,
        FeatureFlags? companyTrial = null) =>
        SubscriptionTrialReadiness.ResolveFeatures(new TrialFeatureInputs(snapshot, plan, status, permanentFree,
            endDate ?? Now.AddDays(10), companyTrial, templateTrial, templateEnabled), Now);

    // ════════ ฟีเจอร์ของลูกค้าทดลองตามข้อมูลแพ็กเกจ ════════

    [Fact]
    public void ทดลองสำเนาว่าง_ใช้TrialFeaturesของแพ็กเกจ()
    {
        var r = Resolve(FeatureFlags.None, SubscriptionPlan.Basic, SubscriptionStatus.Trial,
            FeatureFlags.BasicFeatures, FeatureFlags.ProFeatures);
        Assert.Equal(FeatureFlags.BasicFeatures, r.Features);
        Assert.True(r.FromTemplate);
        Assert.False(r.StillEmpty);
    }

    [Fact]
    public void แพ็กเกจฟรีถาวรสำเนาว่าง_ใช้EnabledFeaturesของแพ็กเกจ()
    {
        var r = Resolve(FeatureFlags.None, SubscriptionPlan.FreeTrial, SubscriptionStatus.Active,
            FeatureFlags.None, FeatureFlags.TrialFeatures);
        Assert.Equal(FeatureFlags.TrialFeatures, r.Features);
        Assert.True(r.FromTemplate);
    }

    [Fact]
    public void สำเนาไม่ว่าง_คือค่าที่ตั้งรายบริษัท_ไม่ถูกแทนด้วยแพ็กเกจ()
    {
        var custom = FeatureFlags.TrialFeatures | FeatureFlags.Payroll;
        var r = Resolve(custom, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial,
            FeatureFlags.BasicFeatures, FeatureFlags.BasicFeatures);
        Assert.Equal(custom, r.Features);
        Assert.False(r.FromTemplate);
    }

    [Theory]
    [InlineData(SubscriptionPlan.Pro, SubscriptionStatus.Active)]
    [InlineData(SubscriptionPlan.Enterprise, SubscriptionStatus.PastDue)]
    public void แพ็กเกจเสียเงินสำเนาว่าง_ไม่เติมเอง_นอกขอบเขตคำตัดสิน(SubscriptionPlan plan, SubscriptionStatus status)
    {
        var r = Resolve(FeatureFlags.None, plan, status, FeatureFlags.ProFeatures, FeatureFlags.ProFeatures);
        Assert.Equal(FeatureFlags.None, r.Features);
        Assert.False(r.FromTemplate);
        Assert.False(r.StillEmpty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(FeatureFlags.None)]
    public void แพ็กเกจก็ว่างหรือไม่มีแพ็กเกจ_คงว่าง_ไม่แต่งชุดฟีเจอร์_และติดธงให้รายงาน(FeatureFlags? template)
    {
        var r = Resolve(FeatureFlags.None, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial,
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

    // ════════ รอบ 200 ฝ่ายค้าน S200-6: เติมเฉพาะ "ระหว่างทดลอง" · เคารพ None ที่แอดมินตั้งรายบริษัท ════════

    [Theory]
    [InlineData(SubscriptionStatus.Expired)]
    [InlineData(SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.Suspended)]
    public void S200_6_แพ็กเกจFreeTrialที่ไม่ใช่ระหว่างทดลอง_ไม่เติมจากแพ็กเกจ(SubscriptionStatus status)
    {
        // เดิม: IsTrialLike = แพ็กเกจ FreeTrial ทุกสถานะ ⇒ หมดอายุแล้วก็ได้ EnabledFeatures ของแพ็กเกจ
        var r = Resolve(FeatureFlags.None, SubscriptionPlan.FreeTrial, status, FeatureFlags.TrialFeatures, FeatureFlags.ProFeatures);
        Assert.Equal(FeatureFlags.None, r.Features);
        Assert.False(r.FromTemplate);
        Assert.False(SubscriptionTrialReadiness.MayFillFromTemplate(FeatureFlags.None, SubscriptionPlan.FreeTrial, status, false,
            Now.AddDays(10), Now));
    }

    [Fact]
    public void S200_6_สถานะTrialแต่เลยวันหมดอายุแล้ว_ไม่เติม()
    {
        var r = Resolve(FeatureFlags.None, SubscriptionPlan.Basic, SubscriptionStatus.Trial, FeatureFlags.BasicFeatures,
            FeatureFlags.BasicFeatures, endDate: Now.AddMinutes(-1));
        Assert.Equal(FeatureFlags.None, r.Features);
        // ทิศตรงข้าม: ยังไม่ถึงวันหมดอายุ (วันสุดท้ายพอดี) = ยังเติม
        var stillOn = Resolve(FeatureFlags.None, SubscriptionPlan.Basic, SubscriptionStatus.Trial, FeatureFlags.BasicFeatures,
            FeatureFlags.BasicFeatures, endDate: Now);
        Assert.Equal(FeatureFlags.BasicFeatures, stillOn.Features);
    }

    [Fact]
    public void S200_6_แอดมินตั้งTrialFeaturesว่างรายบริษัท_เคารพ_ไม่เติมทับด้วยแพ็กเกจ()
    {
        var r = Resolve(FeatureFlags.None, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, FeatureFlags.TrialFeatures,
            FeatureFlags.TrialFeatures, companyTrial: FeatureFlags.None);
        Assert.Equal(FeatureFlags.None, r.Features);
        Assert.True(r.AdminSetEmpty);
        Assert.False(r.FromTemplate);
        Assert.False(r.StillEmpty);   // ไม่ใช่ "ข้อมูลแพ็กเกจว่าง" — เป็นค่าที่ตั้งใจ
    }

    [Fact]
    public void S200_6_trialconfigรายบริษัทไม่ว่าง_ชนะข้อมูลแพ็กเกจ()
    {
        var perCompany = FeatureFlags.TrialFeatures | FeatureFlags.Payroll;
        var r = Resolve(FeatureFlags.None, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, FeatureFlags.TrialFeatures,
            FeatureFlags.TrialFeatures, companyTrial: perCompany);
        Assert.Equal(perCompany, r.Features);
        Assert.True(r.FromCompanyTrialConfig);
    }

    [Fact]
    public void S200_6_ฟรีถาวรที่ติดธง_Active_ใช้EnabledFeaturesของแพ็กเกจ_แม้ระดับไม่ใช่FreeTrial()
    {
        var r = Resolve(FeatureFlags.None, SubscriptionPlan.Basic, SubscriptionStatus.Active, FeatureFlags.None,
            FeatureFlags.BasicFeatures, permanentFree: true);
        Assert.Equal(FeatureFlags.BasicFeatures, r.Features);
    }

    // ════════ รอบ 200 ฝ่ายค้าน S200-4: ตัวตัดสินฟีเจอร์ตัวเดียว (overlay หน้าเว็บ = ด่านสร้างเอกสาร) ════════

    [Fact]
    public void S200_4_ทดลองใช้สำเนาว่าง_ได้DocumentEngineจากแพ็กเกจ_ด่านสร้างเอกสารผ่าน()
    {
        // SubscriptionService.ResolvePlanFeaturesAsync ส่งผลนี้ให้ทั้ง GetGateStateAsync/GetSubscriptionAsync (หน้าเว็บ + gate) และ
        // GetEffectivePlanAsync → CheckFeatureAccessAsync(DocumentEngine) (DocumentService "ไม่มีสิทธิ์ใช้ระบบเอกสาร") — ล็อกจุดเรียกทั้งสองด้วย
        // tools/required_call_site_check.py · ที่นี่ล็อกว่าผลของตัวตัดสินให้ DocumentEngine จริง และตัวตัดสิน gate เห็นว่าผ่าน
        var r = Resolve(FeatureFlags.None, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, FeatureFlags.TrialFeatures,
            FeatureFlags.TrialFeatures);
        Assert.True(r.Features.HasFlag(FeatureFlags.DocumentEngine));
        var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Trial, r.Features, IsWrite: true,
            SubscriptionWriteGateMode.Enforce, CompanySuspended: false, PlanActive: true, RequiredFeature: FeatureFlags.DocumentEngine));
        Assert.False(v.Blocks);
        // ทิศตรงข้าม: สำเนาว่างของแพ็กเกจเสียเงิน (ไม่ใช่ทดลอง) ไม่ถูกเติม ⇒ ยังสร้างเอกสารไม่ได้เหมือนเดิม (ไม่แต่งสิทธิ์ให้)
        var paid = Resolve(FeatureFlags.None, SubscriptionPlan.Pro, SubscriptionStatus.Active, FeatureFlags.ProFeatures, FeatureFlags.ProFeatures);
        Assert.False(paid.Features.HasFlag(FeatureFlags.DocumentEngine));
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

    [Fact]
    public void S200_9_ดูอย่างเดียว_บรรทัดที่ไม่มีผู้สมัคร_เหตุผลของสถานะคงอยู่()
    {
        var reason = SettlementPermissionScope.CandidatesHiddenReason(false, false)!;
        const string statusNote = "คืนเงินที่ไม่พบใบขายเดิม — เลือกใบขายของออเดอร์นี้ (ใบลดหนี้ต้องอ้างใบเดิม §86/10)";
        var view = Batch(Line(statusNote),
            Line("เลขออเดอร์ \"ORD-1\" ตรงกับ 2 รายการในระบบ — เลือกเอง",
                new SettlementMatchCandidateView("Document", Guid.NewGuid(), "INV-0002", 500m, true, false, true, null),
                new SettlementMatchCandidateView("Document", Guid.NewGuid(), "INV-0003", 570m, true, false, true, null)));
        var hidden = SettlementPermissionScope.HideCandidates(view, reason);
        Assert.Equal(statusNote, hidden.Lines[0].MatchNote);   // เดิม: ถูกแทนด้วยข้อความ "ผู้สมัคร…แสดงเฉพาะผู้มีสิทธิ์" ทุกบรรทัด
        Assert.Equal(reason, hidden.Lines[1].MatchNote);       // บรรทัดที่มีผู้สมัคร = ยังซ่อน
        Assert.All(hidden.Lines, l => Assert.Empty(l.MatchCandidates));
    }

    private static SettlementPostingPlan PlanWith(params SettlementPlanIssue[] issues) => new(
        false, 1070m, 1070m, 0m, 1000m, 0m, null, null, issues,
        Array.Empty<SettlementJournalLinePlan>(), Array.Empty<SettlementFeeDocumentPlan>(), Array.Empty<SettlementReceiptPlan>(),
        Array.Empty<SettlementSummarySalePlan>(), Array.Empty<SettlementRefundPlan>());

    [Fact]
    public void S200_5_พรีวิวลงบัญชี_ดูอย่างเดียว_ไม่เห็นเลขที่และยอดค้าง_ปัญหายังบล็อกพร้อมทางไปต่อ()
    {
        var reason = SettlementPermissionScope.CandidatesHiddenReason(false, false)!;
        var lineId = Guid.NewGuid();
        var receivable = new SettlementPlanIssue(SettlementPlanIssueCode.ReceiptDocumentNotPayable, true,
            "ใบ INV-0009 ค้างชำระ 300.00 น้อยกว่ายอดที่แพลตฟอร์มโอน 1,070.00 (เคยรับชำระทางอื่นแล้ว?)",
            "จับคู่บรรทัดขายกับใบที่ถูกต้อง", new[] { lineId }, 1070m);
        var other = new SettlementPlanIssue(SettlementPlanIssueCode.Unbalanced, true, "ยอดไม่ลงตัว 5.00", "ตรวจไฟล์", Array.Empty<Guid>(), 5m);
        var hidden = SettlementPermissionScope.HideReceivableDetails(PlanWith(receivable, other), reason);
        var h = hidden.Issues[0];
        Assert.DoesNotContain("INV-0009", h.Message);
        Assert.DoesNotContain("300.00", h.Message);
        Assert.Contains(reason, h.Message);
        Assert.True(h.Blocking);                                  // ยังบล็อก
        Assert.Equal(receivable.NextStep, h.NextStep);            // ทางไปต่อยังอยู่
        Assert.Equal(new[] { lineId }, h.LineIds);
        Assert.Equal(1070m, h.Amount);                            // ยอดที่แพลตฟอร์มโอน ไม่ใช่ยอดค้าง
        Assert.Equal(other, hidden.Issues[1]);                    // ปัญหาชนิดอื่นไม่ถูกแตะ
    }
}
