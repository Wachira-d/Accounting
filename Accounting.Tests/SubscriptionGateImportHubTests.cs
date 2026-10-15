using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **รอบ 206 (ทีมงานค้าง) — ศูนย์นำเข้าข้อมูลจำกัดตามแพ็กเกจเหมือนหน้านำเข้าเดิม**
///
/// <para>═══ บั๊ก ═══ <c>/import-export</c> (หน้าเดิม) ถูก gate ด้วย <c>BulkImport</c> ใน <see cref="SubscriptionGatePolicy.RouteFeatureMap"/> แต่
/// <c>ImportHubController</c> (<c>api/companies/{cid}/import</c>) ที่มาแทนไม่อยู่ในตาราง + เมนู <c>import-hub</c> ไม่ผูกฟีเจอร์ ⇒ แพ็กเกจที่ไม่มีนำเข้าจำนวนมาก
/// นำเข้าผ่านศูนย์ได้ ทั้งที่ผู้รวมงานแจ้งเจ้าของว่า "จำกัดตามหน้าเดิม"</para>
///
/// <para>สองทิศ: ไม่มี BulkImport ⇒ FeatureNotInPlan (ข้อความอัปเกรดมาตรฐานของ middleware ใช้ <c>verdict.Feature</c>) · มี ⇒ ผ่าน ·
/// เส้นอื่นที่มีคำว่า import (AI จับคู่คอลัมน์ · ย้ายระบบ · สินทรัพย์ · รอบโอน · เงินเดือน) ไม่เปลี่ยนฟีเจอร์ (ห้ามเข้มเกินพฤติกรรมเดิม)</para>
/// </summary>
public class SubscriptionGateImportHubTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static TenantCompanyTarget Web => TenantCompanyId.Resolve(A.ToString(), null);
    private static TenantCompanyTarget Partner => TenantCompanyId.Resolve(null, A.ToString());

    private const string HubKey = "/companies/*/import";

    [Theory]
    [InlineData("/api/companies/x/import/batches", "POST")]
    [InlineData("/api/companies/x/import/batches/abc/commit", "POST")]
    [InlineData("/api/companies/x/import/entities", "GET")]
    [InlineData("/api/companies/x/import", "GET")]
    public void ศูนย์นำเข้า_ผูกBulkImport_เหมือนหน้าเดิม(string path, string method)
    {
        var r = SubscriptionGatePolicy.RequiredFeatureFor(path, method);
        Assert.NotNull(r);
        Assert.Equal(FeatureFlags.BulkImport, r!.Value.Feature);
        Assert.Equal(HubKey, r.Value.RouteKey);
        Assert.True(r.Value.NewlyGated);              // คีย์ใหม่ ⇒ เงาสำหรับ partner จนกว่ากดบังคับ (ข้อ 22)
        Assert.Null(r.Value.LegacyFeature);           // เดิมไม่ถูก gate เลย
        // ฟีเจอร์เดียวกับหน้าเดิม
        Assert.Equal(SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/import-export/import", method)!.Value.Feature, r.Value.Feature);
    }

    [Theory]
    // ทิศตรงข้าม: เส้นทางอื่นที่มี segment "import" ต้องคงฟีเจอร์เดิม (คีย์ "/import" เปล่าจะลากทั้งหมดนี้มาเป็น BulkImport)
    [InlineData("/api/companies/x/import-export/import", FeatureFlags.BulkImport, "/import-export")]
    [InlineData("/api/companies/x/ai/import/suggest-mapping", FeatureFlags.AI_Features, "/ai")]
    [InlineData("/api/companies/x/fixedasset/import", FeatureFlags.FixedAssets, "/fixedasset")]
    [InlineData("/api/companies/x/settlement/files/import", FeatureFlags.BankReconciliation, "/settlement")]
    [InlineData("/api/companies/x/payroll/runs/import", FeatureFlags.Payroll, "/payroll")]
    [InlineData("/api/companies/x/bank/import-statement", FeatureFlags.BankReconciliation, "/bank")]
    public void เส้นอื่นที่มีคำว่าimport_ไม่เปลี่ยนฟีเจอร์(string path, FeatureFlags feature, string key)
    {
        var r = SubscriptionGatePolicy.RequiredFeatureFor(path, "POST")!.Value;
        Assert.Equal(feature, r.Feature);
        Assert.Equal(key, r.RouteKey);
    }

    [Theory]
    // เส้นที่เดิมไม่ถูก gate ต้องไม่ถูก gate (migration/* เดิมไม่อยู่ในตาราง — ห้ามเข้มเกินพฤติกรรมเดิม)
    [InlineData("/api/companies/x/migration/import")]
    [InlineData("/api/companies/x/migration/preview")]
    [InlineData("/api/companies/x/migration/sessions")]
    [InlineData("/api/companies/x/import-conflicts")]
    [InlineData("/api/companies/x/imports")]
    public void เส้นที่เดิมไม่gate_ยังไม่gate(string path)
    {
        Assert.Null(SubscriptionGatePolicy.RequiredFeatureFor(path, "POST"));
    }

    [Fact]
    public void แพ็กเกจไม่มีBulkImport_บังคับแล้ว_บล็อกด้วยข้อความอัปเกรดมาตรฐาน()
    {
        var req = SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/import/batches", "POST");
        var plan = SubscriptionGatePolicy.FeaturePlanFor(Web, SubscriptionEnforcementMode.Enforce, req);
        Assert.Equal(FeatureFlags.BulkImport, plan.Enforce);
        var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.BasicFeatures,
            true, SubscriptionWriteGateMode.Enforce, false, true, plan.Enforce));
        Assert.True(v.Blocks);
        Assert.Equal(SubscriptionGateReason.FeatureNotInPlan, v.Block);
        Assert.Equal(FeatureFlags.BulkImport, v.Feature);
        Assert.Contains("BulkImport", SubscriptionGatePolicy.Describe(v.Block, v.Feature?.ToString(), "Basic"));
        Assert.Equal(SubscriptionGateAction.Enforce, SubscriptionGatePolicy.ActionFor(Web, SubscriptionEnforcementMode.Enforce));
    }

    [Fact]
    public void ทิศตรงข้าม_แพ็กเกจมีBulkImport_ผ่าน()
    {
        var req = SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/import/batches", "POST");
        var plan = SubscriptionGatePolicy.FeaturePlanFor(Web, SubscriptionEnforcementMode.Enforce, req);
        var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.ProFeatures,
            true, SubscriptionWriteGateMode.Enforce, false, true, plan.Enforce));
        Assert.False(v.Blocks);
    }

    [Theory]
    [InlineData(SubscriptionEnforcementMode.Off)]
    [InlineData(SubscriptionEnforcementMode.Shadow)]
    [InlineData(SubscriptionEnforcementMode.Enforce)]
    public void หน้าเว็บ_ศูนย์นำเข้าตัดสินเหมือนหน้าเดิมทุกโหมด(SubscriptionEnforcementMode mode)
    {
        var hub = SubscriptionGatePolicy.FeaturePlanFor(Web, mode, SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/import/batches", "POST"));
        var old = SubscriptionGatePolicy.FeaturePlanFor(Web, mode, SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/import-export/import", "POST"));
        Assert.Equal(old.Enforce, hub.Enforce);
        Assert.Equal(old.ShadowOnly, hub.ShadowOnly);
    }

    [Theory]
    [InlineData(SubscriptionEnforcementMode.Off)]
    [InlineData(SubscriptionEnforcementMode.Shadow)]
    public void partner_ระหว่างยังไม่บังคับ_คีย์ใหม่เป็นเงา_ไม่บล็อก(SubscriptionEnforcementMode mode)
    {
        var plan = SubscriptionGatePolicy.FeaturePlanFor(Partner, mode, SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/import/batches", "POST"));
        Assert.Null(plan.Enforce);
        Assert.Equal(FeatureFlags.BulkImport, plan.ShadowOnly);
        Assert.Equal(HubKey, plan.ShadowRouteKey);
    }

    [Fact]
    public void partner_กดบังคับแล้ว_ถูกบล็อก()
    {
        var plan = SubscriptionGatePolicy.FeaturePlanFor(Partner, SubscriptionEnforcementMode.Enforce,
            SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/import/batches", "POST"));
        Assert.Equal(FeatureFlags.BulkImport, plan.Enforce);
        var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.TrialFeatures,
            false, SubscriptionWriteGateMode.Enforce, null, null, plan.Enforce));
        Assert.Equal(SubscriptionGateReason.FeatureNotInPlan, v.Block);
    }

    [Fact]
    public void คีย์ศูนย์นำเข้าอยู่ในตาราง_และอยู่ในชุดคีย์ใหม่()
    {
        Assert.Contains(SubscriptionGatePolicy.RouteFeatureMap, m => m.Path == HubKey && m.Feature == FeatureFlags.BulkImport);
        Assert.Contains(HubKey, SubscriptionGatePolicy.NewlyGatedRouteKeys);
        Assert.DoesNotContain("/import-export", SubscriptionGatePolicy.NewlyGatedRouteKeys);   // หน้าเดิม partner บังคับมาแต่เดิม — ห้ามหลวม
    }
}
