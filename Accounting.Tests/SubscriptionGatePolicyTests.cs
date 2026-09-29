using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **gate แพ็กเกจ/ระงับบริษัทบนหน้าเว็บ — รายงานก่อน แล้วค่อยเปิดบังคับ** (รอบ 198 ข้อ 5)
///
/// <para>═══ บั๊กที่ล็อกไว้ ═══ <c>SubscriptionCheckMiddleware</c> รู้บริษัทจาก <c>X-Company-Id</c> อย่างเดียว แล้วข้ามทั้งหมดเมื่อไม่มี
/// ⇒ หน้าเว็บ (api.js ไม่ส่ง header · เส้นทาง <c>/api/companies/{companyId}/…</c>) ไม่เคยถูก gate แพ็กเกจ/ระงับบริษัท ·
/// และคำขอที่ส่ง header ของบริษัท B มากับ route ของบริษัท A ถูกตัดสินแพ็กเกจด้วย B ทั้งที่ผ่านด่านสมาชิกด้วย A</para>
///
/// <para>สองทิศ: (ก) หน้าเว็บถูกตัดสินแล้ว + route ชนะ header + โหมดเงาไม่บล็อก (ข) คำขอที่ส่ง header มาเอง "บังคับเหมือนเดิม"
/// ไม่ว่าสวิตช์เว็บจะเป็นอะไร (ห้ามหลวมลง) · ฟีเจอร์ที่อยู่ในแพ็กเกจ/เส้นทางที่กฎหมายบังคับ ต้องผ่าน</para>
/// </summary>
public class SubscriptionGatePolicyTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ════════ หาบริษัทของคำขอ (ตัวเดียวกับ TenantAccessMiddleware) ════════

    [Fact]
    public void หน้าเว็บ_ไม่มีheader_รู้บริษัทจากroute()
    {
        var t = TenantCompanyId.Resolve(A.ToString(), null);
        Assert.Equal(A, t.CompanyId);
        Assert.Equal(TenantCompanySource.Route, t.Source);
        Assert.False(t.HeaderCarried);
        Assert.False(t.HeaderDisagreesWithRoute);
    }

    [Fact]
    public void partner_ส่งheaderอย่างเดียว_ใช้header()
    {
        var t = TenantCompanyId.Resolve(null, B.ToString());
        Assert.Equal(B, t.CompanyId);
        Assert.Equal(TenantCompanySource.Header, t.Source);
        Assert.True(t.HeaderCarried);
    }

    [Fact]
    public void headerปลอมเป็นบริษัทอื่น_routeชนะ_ไม่ยืมแพ็กเกจของบริษัทอื่นได้()
    {
        var t = TenantCompanyId.Resolve(A.ToString(), B.ToString());
        Assert.Equal(A, t.CompanyId);                       // บริษัทที่ TenantAccessMiddleware ตรวจสมาชิกแล้ว
        Assert.Equal(TenantCompanySource.Route, t.Source);
        Assert.True(t.HeaderDisagreesWithRoute);
        Assert.True(t.HeaderCarried);                        // ยังเป็นคำขอแบบ header ⇒ บังคับเสมอ (ไม่หลวม)
        Assert.Equal(SubscriptionGateAction.Enforce,
            SubscriptionGatePolicy.ActionFor(t, SubscriptionEnforcementMode.Off));
    }

    [Fact]
    public void headerตรงกับroute_ไม่นับว่าขัดกัน()
    {
        var t = TenantCompanyId.Resolve(A.ToString(), A.ToString());
        Assert.Equal(A, t.CompanyId);
        Assert.False(t.HeaderDisagreesWithRoute);
        Assert.True(t.HeaderCarried);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("not-a-guid", "")]
    [InlineData("", "abc")]
    public void ไม่มีบริษัทที่อ่านได้_ไม่ตรวจอะไร(string? route, string? header)
    {
        var t = TenantCompanyId.Resolve(route, header);
        Assert.Null(t.CompanyId);
        Assert.Equal(TenantCompanySource.None, t.Source);
        Assert.Equal(SubscriptionGateAction.Skip, SubscriptionGatePolicy.ActionFor(t, SubscriptionEnforcementMode.Enforce));
    }

    [Fact]
    public void headerไม่ใช่GUID_กับroute_ถือว่าไม่ได้ส่งheader_เดินตามสวิตช์เว็บ()
    {
        var t = TenantCompanyId.Resolve(A.ToString(), "garbage");
        Assert.Equal(A, t.CompanyId);
        Assert.False(t.HeaderCarried);
        Assert.Equal(SubscriptionGateAction.Shadow, SubscriptionGatePolicy.ActionFor(t, SubscriptionEnforcementMode.Shadow));
    }

    [Fact]
    public void routeValueเป็นGuidอยู่แล้ว_อ่านได้()
    {
        var t = TenantCompanyId.Resolve(A, null);
        Assert.Equal(A, t.CompanyId);
    }

    // ════════ คำขอนี้ต้องทำอะไร (สวิตช์) ════════

    [Theory]
    [InlineData(SubscriptionEnforcementMode.Off)]
    [InlineData(SubscriptionEnforcementMode.Shadow)]
    [InlineData(SubscriptionEnforcementMode.Enforce)]
    public void คำขอที่ส่งheaderมาเอง_บังคับเสมอ_ไม่ขึ้นกับสวิตช์เว็บ(SubscriptionEnforcementMode web)
    {
        var t = TenantCompanyId.Resolve(null, B.ToString());
        Assert.Equal(SubscriptionGateAction.Enforce, SubscriptionGatePolicy.ActionFor(t, web));
    }

    [Theory]
    [InlineData(SubscriptionEnforcementMode.Off, SubscriptionGateAction.Skip)]
    [InlineData(SubscriptionEnforcementMode.Shadow, SubscriptionGateAction.Shadow)]
    [InlineData(SubscriptionEnforcementMode.Enforce, SubscriptionGateAction.Enforce)]
    public void หน้าเว็บ_เดินตามสวิตช์(SubscriptionEnforcementMode web, SubscriptionGateAction expected)
    {
        var t = TenantCompanyId.Resolve(A.ToString(), null);
        Assert.Equal(expected, SubscriptionGatePolicy.ActionFor(t, web));
    }

    [Fact]
    public void ค่าสวิตช์ที่ไม่รู้จัก_เป็นโหมดเงา_ไม่บล็อกใครเพราะค่าเพี้ยน()
    {
        var t = TenantCompanyId.Resolve(A.ToString(), null);
        Assert.Equal(SubscriptionGateAction.Shadow, SubscriptionGatePolicy.ActionFor(t, (SubscriptionEnforcementMode)99));
    }

    [Fact]
    public void ค่าตั้งต้นของสวิตช์บนแถวค่าตั้ง_คือโหมดเงา()
    {
        Assert.Equal(SubscriptionEnforcementMode.Shadow, new Accounting.Models.Entities.SiteSettings().SubscriptionEnforcementMode);
        Assert.Equal(1, (int)SubscriptionEnforcementMode.Shadow);   // ต้องตรงกับ DEFAULT 1 ของ migration
    }

    // ════════ เส้นทาง → ฟีเจอร์ ════════

    [Theory]
    [InlineData("/api/companies/x/payroll/runs", FeatureFlags.Payroll, "/payroll")]
    [InlineData("/api/companies/x/payroll", FeatureFlags.Payroll, "/payroll")]
    [InlineData("/api/companies/x/etax/sign-and-submit/abc", FeatureFlags.EtaxDirect, "/etax/sign-and-submit")]
    [InlineData("/api/companies/x/etax/documents", FeatureFlags.EtaxInvoice, "/etax")]
    [InlineData("/api/companies/x/reports/aging", FeatureFlags.AgingReport, "/reports/aging")]
    [InlineData("/api/companies/x/dimensions/cost-centers", FeatureFlags.CostCenter, "/dimensions")]
    // รอบ 200 D-P3: รอบโอน settlement ผูก feature เดียวกับกระทบยอดธนาคาร
    [InlineData("/api/companies/x/settlement/batches", FeatureFlags.BankReconciliation, "/settlement")]
    [InlineData("/api/companies/x/settlement/batches/abc/deposit-match", FeatureFlags.BankReconciliation, "/settlement")]
    [InlineData("/api/companies/x/bank/accounts", FeatureFlags.BankReconciliation, "/bank")]
    public void เส้นทางที่ถูกgate_คีย์ยาวสุดชนะ(string path, FeatureFlags feature, string key)
    {
        var r = SubscriptionGatePolicy.RequiredFeatureFor(path);
        Assert.NotNull(r);
        Assert.Equal(feature, r!.Value.Feature);
        Assert.Equal(key, r.Value.RouteKey);
    }

    [Theory]
    [InlineData("/api/companies/x/dimensions/branches")]          // §86/4 รหัสสาขา — กฎหมายบังคับ ห้าม gate
    [InlineData("/api/companies/x/dimensions/branches/abc")]
    [InlineData("/api/companies/x/payroll-history")]              // ยึดท้าย segment — ไม่ใช่ /payroll
    [InlineData("/api/companies/x/documents")]
    // รอบ 200 D-P3 ทิศตรงข้าม: เส้นทางที่มีคำว่า settlement แต่ไม่ใช่โมดูลรอบโอน ต้องไม่ถูกผูกไปด้วย
    [InlineData("/api/companies/x/pay/settlements/pending")]
    [InlineData("/api/companies/x/documents/orphaned-settlement-receipts")]
    [InlineData("/api/companies/x/pay/documents/abc/settlement-proposal")]
    [InlineData("")]
    public void เส้นทางที่ไม่ต้องใช้ฟีเจอร์_หรือกฎหมายบังคับ_ไม่gate(string path)
    {
        Assert.Null(SubscriptionGatePolicy.RequiredFeatureFor(path));
        Assert.Null(SubscriptionGatePolicy.RequiredFeatureFor(null));
    }

    [Fact]
    public void ฟีเจอร์ที่ถูกgate_ไม่ซ้ำ_และมาจากตารางเดียวกับตัวบล็อก()
    {
        var gated = SubscriptionGatePolicy.GatedFeatures;
        Assert.Equal(gated.Count, gated.Distinct().Count());
        Assert.Contains(FeatureFlags.Payroll, gated);
        Assert.Equal(SubscriptionGatePolicy.RouteFeatureMap.Select(m => m.Feature).Distinct().Count(), gated.Count);
    }

    // ════════ ตัดสิน (ตัวเดียวกันทั้งโหมดเงาและบังคับ) ════════

    private static SubscriptionGateFacts Facts(
        SubscriptionStatus status = SubscriptionStatus.Active,
        FeatureFlags features = FeatureFlags.TrialFeatures,
        bool write = false,
        SubscriptionWriteGateMode writeMode = SubscriptionWriteGateMode.Enforce,
        bool? suspended = null, bool? active = null, FeatureFlags? need = null) =>
        new(status, features, write, writeMode, suspended, active, need);

    [Theory]
    [InlineData(SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.Suspended)]
    public void subscriptionถูกยกเลิกหรือระงับ_บล็อกแม้แค่เปิดดู(SubscriptionStatus status)
    {
        var v = SubscriptionGatePolicy.Decide(Facts(status, FeatureFlags.EnterpriseFeatures, write: false));
        Assert.Equal(SubscriptionGateReason.SubscriptionInactive, v.Block);
        Assert.False(SubscriptionGatePolicy.NeedsWriteFacts(status, isWrite: true, SubscriptionWriteGateMode.Enforce));
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Trial)]
    [InlineData(SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Expired)]
    public void สถานะอื่น_ไม่บล็อกทั้งคำขอ(SubscriptionStatus status)
    {
        var v = SubscriptionGatePolicy.Decide(Facts(status));
        Assert.Equal(SubscriptionGateReason.None, v.Block);
        Assert.False(v.Blocks);
    }

    [Fact]
    public void ฟีเจอร์ไม่อยู่ในแพ็กเกจ_บล็อกพร้อมชื่อฟีเจอร์()
    {
        var need = SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/payroll/runs")!.Value.Feature;
        var v = SubscriptionGatePolicy.Decide(Facts(features: FeatureFlags.TrialFeatures, need: need));
        Assert.Equal(SubscriptionGateReason.FeatureNotInPlan, v.Block);
        Assert.Equal(FeatureFlags.Payroll, v.Feature);
    }

    [Fact]
    public void ฟีเจอร์อยู่ในแพ็กเกจ_ผ่าน()
    {
        var v = SubscriptionGatePolicy.Decide(Facts(features: FeatureFlags.EnterpriseFeatures, need: FeatureFlags.Payroll));
        Assert.Equal(SubscriptionGateReason.None, v.Block);
        Assert.Null(v.Feature);
    }

    [Fact]
    public void บริษัทถูกระงับ_config_Enforce_บล็อกเฉพาะการเขียน()
    {
        var write = SubscriptionGatePolicy.Decide(Facts(write: true, suspended: true, active: true));
        Assert.Equal(SubscriptionGateReason.CompanySuspended, write.Block);

        var read = SubscriptionGatePolicy.Decide(Facts(write: false, suspended: true, active: true));
        Assert.Equal(SubscriptionGateReason.None, read.Block);   // อ่าน/ส่งออกข้อมูลได้ (PDPA)
    }

    [Fact]
    public void บริษัทถูกระงับ_config_LogOnly_ผ่านแต่คืนเหตุให้บันทึก()
    {
        var v = SubscriptionGatePolicy.Decide(Facts(write: true, writeMode: SubscriptionWriteGateMode.LogOnly,
            suspended: true, active: false));
        Assert.Equal(SubscriptionGateReason.None, v.Block);
        Assert.Equal(new[] { SubscriptionGateReason.CompanySuspended, SubscriptionGateReason.PlanExpired }, v.LogOnly);
    }

    [Fact]
    public void config_Off_ไม่ตรวจด่านเขียนเลย()
    {
        var v = SubscriptionGatePolicy.Decide(Facts(write: true, writeMode: SubscriptionWriteGateMode.Off,
            suspended: true, active: false));
        Assert.Equal(SubscriptionGateReason.None, v.Block);
        Assert.Empty(v.LogOnly);
        Assert.False(SubscriptionGatePolicy.NeedsWriteFacts(SubscriptionStatus.Active, true, SubscriptionWriteGateMode.Off));
    }

    [Fact]
    public void หมดอายุเกินผ่อนผัน_บล็อกการเขียน_อ่านได้()
    {
        var write = SubscriptionGatePolicy.Decide(Facts(write: true, suspended: false, active: false));
        Assert.Equal(SubscriptionGateReason.PlanExpired, write.Block);
        Assert.True(SubscriptionGatePolicy.ReachedExpiryStep(write));

        var read = SubscriptionGatePolicy.Decide(Facts(write: false, suspended: false, active: false));
        Assert.Equal(SubscriptionGateReason.None, read.Block);
    }

    [Fact]
    public void อ่านสถานะไม่ได้_ไม่บล็อก_failopenเดิม()
    {
        var v = SubscriptionGatePolicy.Decide(Facts(write: true, suspended: null, active: null));
        Assert.Equal(SubscriptionGateReason.None, v.Block);
        Assert.Empty(v.LogOnly);
    }

    [Fact]
    public void ลำดับเดิม_ระงับบริษัทมาก่อนหมดอายุ_และก่อนฟีเจอร์()
    {
        var v = SubscriptionGatePolicy.Decide(Facts(write: true, suspended: true, active: false,
            features: FeatureFlags.TrialFeatures, need: FeatureFlags.Payroll));
        Assert.Equal(SubscriptionGateReason.CompanySuspended, v.Block);
        Assert.False(SubscriptionGatePolicy.ReachedExpiryStep(v));   // ไม่ติด header ผ่อนผันบนคำตอบระงับ (พฤติกรรมเดิม)
    }

    [Fact]
    public void คำอธิบายไทย_บอกฟีเจอร์และแพ็กเกจ()
    {
        var text = SubscriptionGatePolicy.Describe(SubscriptionGateReason.FeatureNotInPlan, "Payroll", "FreeTrial");
        Assert.Contains("Payroll", text);
        Assert.Contains("FreeTrial", text);
        Assert.Contains("ทั้งดูและแก้ไข",
            SubscriptionGatePolicy.Describe(SubscriptionGateReason.SubscriptionInactive, null, null));
    }
}
