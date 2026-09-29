using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **รอบ 200 ทีม S2 — คำตัดสินข้อ 21–23 + ฝ่ายค้าน S200-3/S200-8** (gate แพ็กเกจก่อนเจ้าของกดบังคับ)
///
/// <para>═══ บั๊ก/คำตัดสินที่ล็อกไว้ ═══
/// (1) ข้อ 22: คีย์ <c>RouteFeatureMap</c> ไม่ตรง endpoint จริง (<c>/fixed-assets</c> ↔ <c>/fixedasset</c> · <c>/multi-currency</c> ↔ <c>/currency</c> ·
/// <c>/warehouse</c> · <c>/commission</c> · <c>/approvals</c> · <c>/ai/</c> · <c>/commerce</c>/<c>/booking</c> ที่แพ้ <c>/cms/sites</c>) ⇒ ฟีเจอร์ไม่ถูก gate เลย
/// (2) S200-3: คีย์ที่เพิ่งเริ่มมีผล (รวม <c>/settlement</c>) บังคับ partner ที่ส่ง header <b>ทันที</b>หลัง deploy และไม่ถูกนับในรายงาน
/// (3) ข้อ 21: <c>GET …/bank/accounts</c> (รายการบัญชีที่หน้าเอกสาร/รับชำระใช้เลือก) ถูกผูกฟีเจอร์กระทบยอด
/// (4) ข้อ 23: <c>/api/v1</c> ไม่ตรวจสถานะระงับ/หมดอายุ</para>
///
/// <para>สองทิศทุกข้อ: คีย์ใหม่ = เงาสำหรับ partner <b>ระหว่าง</b>โหมดเงา แต่บังคับเมื่อกดบังคับ · คีย์เดิมของคำขอเดียวกันยังบังคับ (ไม่หลวม) ·
/// เส้นกระทบยอดจริงยังผูก · /api/v1 ถูกบล็อกเมื่อกดบังคับ</para>
/// </summary>
public class SubscriptionGateRound200S2Tests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static TenantCompanyTarget Web => TenantCompanyId.Resolve(A.ToString(), null);
    private static TenantCompanyTarget Partner => TenantCompanyId.Resolve(null, A.ToString());

    // ════════ ข้อ 22: คีย์ตรง route จริงของ controller ════════

    [Theory]
    [InlineData("/api/companies/x/fixedasset", FeatureFlags.FixedAssets, "/fixedasset")]
    [InlineData("/api/companies/x/fixedasset/abc/dispose", FeatureFlags.FixedAssets, "/fixedasset")]
    [InlineData("/api/companies/x/currency/rates/latest", FeatureFlags.MultiCurrency, "/currency")]
    [InlineData("/api/companies/x/warehouses/transfers", FeatureFlags.WarehouseManagement, "/warehouses")]
    [InlineData("/api/companies/x/commissions/plans", FeatureFlags.Commission, "/commissions")]
    [InlineData("/api/companies/x/approvals/pending", FeatureFlags.ApprovalWorkflow, "/approvals")]
    [InlineData("/api/companies/x/approval/rules", FeatureFlags.ApprovalWorkflow, "/approval")]      // ตรงมาแต่เดิม
    [InlineData("/api/companies/x/ai/anomalies", FeatureFlags.AI_Features, "/ai")]
    [InlineData("/api/companies/x/ai/gl-account/suggest", FeatureFlags.AI_Features, "/ai")]
    [InlineData("/api/companies/x/cms/sites/0e6f/commerce/orders", FeatureFlags.CmsEcommerce, "/cms/sites/*/commerce")]
    [InlineData("/api/companies/x/cms/sites/0e6f/booking/slots", FeatureFlags.CmsBooking, "/cms/sites/*/booking")]
    [InlineData("/api/companies/x/settlement/batches", FeatureFlags.BankReconciliation, "/settlement")]
    public void ข้อ22_คีย์ตรงrouteจริง_และเป็นคีย์ใหม่(string path, FeatureFlags feature, string key)
    {
        var r = SubscriptionGatePolicy.RequiredFeatureFor(path, "GET");
        Assert.NotNull(r);
        Assert.Equal(feature, r!.Value.Feature);
        Assert.Equal(key, r.Value.RouteKey);
        Assert.Equal(SubscriptionGatePolicy.NewlyGatedRouteKeys.Contains(key), r.Value.NewlyGated);
    }

    [Theory]
    // คีย์ยาวกว่ายังชนะ — /ai/bank/… ยังเป็นกระทบยอด · /ai/ocr/… ยังเป็น OCR (ไม่เปลี่ยนฟีเจอร์ของเส้นเดิม)
    [InlineData("/api/companies/x/ai/bank/suggest-match", FeatureFlags.BankReconciliation, "/bank")]
    [InlineData("/api/companies/x/ai/ocr/s1/ai-review", FeatureFlags.DocumentOCR, "/ocr")]
    [InlineData("/api/companies/x/ai-tools/run", FeatureFlags.AI_Features, "/ai-tools")]
    // * ตรง 1 segment เท่านั้น — /cms/sites (ไม่มี id) ยังเป็นตัวสร้างเว็บ
    [InlineData("/api/companies/x/cms/sites", FeatureFlags.CmsWebsiteBuilder, "/cms/sites")]
    [InlineData("/api/companies/x/cms/sites/0e6f/pages", FeatureFlags.CmsWebsiteBuilder, "/cms/sites")]
    public void ข้อ22_ทิศตรงข้าม_เส้นเดิมไม่เปลี่ยนฟีเจอร์(string path, FeatureFlags feature, string key)
    {
        var r = SubscriptionGatePolicy.RequiredFeatureFor(path, "GET")!.Value;
        Assert.Equal(feature, r.Feature);
        Assert.Equal(key, r.RouteKey);
        Assert.False(r.NewlyGated);
        Assert.Equal(feature, r.LegacyFeature);
    }

    [Theory]
    [InlineData("/api/companies/x/ai-feedback/stats")]
    [InlineData("/api/companies/x/ai-usage")]
    [InlineData("/api/companies/x/currency-report")]
    [InlineData("/api/companies/x/documents")]
    public void ข้อ22_คำที่ขึ้นต้นคล้ายกัน_ไม่ถูกผูก(string path)
    {
        Assert.Null(SubscriptionGatePolicy.RequiredFeatureFor(path, "GET"));
    }

    [Theory]
    [InlineData("/api/companies/x/warehouses/products/p1/stock", FeatureFlags.WarehouseManagement, FeatureFlags.Inventory, "/products")]
    [InlineData("/api/companies/x/cms/sites/0e6f/commerce/orders", FeatureFlags.CmsEcommerce, FeatureFlags.CmsWebsiteBuilder, "/cms/sites")]
    public void ข้อ22_คีย์ใหม่_บอกคีย์เดิมที่คำขอเดียวกันเคยถูกตัดสิน(string path, FeatureFlags feature, FeatureFlags legacy, string legacyKey)
    {
        var r = SubscriptionGatePolicy.RequiredFeatureFor(path, "GET")!.Value;
        Assert.True(r.NewlyGated);
        Assert.Equal(feature, r.Feature);
        Assert.Equal(legacy, r.LegacyFeature);
        Assert.Equal(legacyKey, r.LegacyRouteKey);
    }

    [Fact]
    public void ข้อ22_คีย์ใหม่ทุกตัวอยู่ในตาราง_และคีย์ที่ไม่เคยตรงถูกถอดแล้ว()
    {
        var paths = SubscriptionGatePolicy.RouteFeatureMap.Select(m => m.Path).ToHashSet();
        Assert.All(SubscriptionGatePolicy.NewlyGatedRouteKeys, k => Assert.Contains(k, paths));
        foreach (var dead in new[] { "/fixed-assets", "/multi-currency", "/warehouse", "/commission", "/ai/", "/commerce", "/booking" })
            Assert.DoesNotContain(dead, paths);
        // คีย์ที่ partner ถูกบังคับมาก่อนรอบ 198 ห้ามอยู่ในชุดคีย์ใหม่ (ห้ามหลวม)
        foreach (var old in new[] { "/payroll", "/bank", "/tax", "/etax", "/approval", "/cms/sites", "/products", "/ocr" })
            Assert.DoesNotContain(old, SubscriptionGatePolicy.NewlyGatedRouteKeys);
    }

    // ════════ ข้อ 21: อ่านรายการบัญชีธนาคาร/คลังเพื่อเลือก = ไม่ผูก · งานกระทบยอดจริงยังผูก ════════

    [Theory]
    [InlineData("GET", "/api/companies/x/bank/accounts")]
    [InlineData("get", "/api/companies/x/bank/accounts/")]
    [InlineData("GET", "/api/companies/x/warehouses")]
    public void ข้อ21_อ่านรายการเพื่อเลือก_ไม่ผูกฟีเจอร์(string method, string path)
    {
        Assert.Null(SubscriptionGatePolicy.RequiredFeatureFor(path, method));
    }

    [Theory]
    [InlineData("POST", "/api/companies/x/bank/accounts", "/bank")]                      // สร้างบัญชี (หน้า bank.html)
    [InlineData("GET", "/api/companies/x/bank/accounts/a1/transactions", "/bank")]       // รายการเดินบัญชี
    [InlineData("GET", "/api/companies/x/bank/accounts/a1/reconciliation-summary", "/bank")]
    [InlineData("POST", "/api/companies/x/bank/reconcile", "/bank")]
    [InlineData("POST", "/api/companies/x/bank/import-statement", "/bank")]
    [InlineData("POST", "/api/companies/x/warehouses", "/warehouses")]
    [InlineData("GET", "/api/companies/x/warehouses/transfers", "/warehouses")]
    public void ข้อ21_ทิศตรงข้าม_งานกระทบยอดและงานคลังจริงยังผูก(string method, string path, string key)
    {
        Assert.Equal(key, SubscriptionGatePolicy.RequiredFeatureFor(path, method)!.Value.RouteKey);
    }

    // ════════ S200-3 + ข้อ 22: คีย์ใหม่เป็นเงาสำหรับ partner จนกว่าเจ้าของกดบังคับ ════════

    [Theory]
    [InlineData(SubscriptionEnforcementMode.Off)]
    [InlineData(SubscriptionEnforcementMode.Shadow)]
    public void S200_3_partnerเรียกsettlement_ระหว่างยังไม่บังคับ_ไม่ถูกบล็อก_แต่ถูกบันทึกเป็นเงา(SubscriptionEnforcementMode mode)
    {
        var req = SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/settlement/batches", "GET");
        var plan = SubscriptionGatePolicy.FeaturePlanFor(Partner, mode, req);
        Assert.Null(plan.Enforce);                                         // /settlement ไม่มีคีย์เดิม ⇒ ไม่มีอะไรบังคับ
        Assert.Equal(FeatureFlags.BankReconciliation, plan.ShadowOnly);
        Assert.Equal(SubscriptionGateAction.Enforce, SubscriptionGatePolicy.ActionFor(Partner, mode));   // สถานะยังบังคับ (เดิม)
        var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.TrialFeatures,
            false, SubscriptionWriteGateMode.LogOnly, null, null, plan.Enforce));
        Assert.False(v.Blocks);
        var shadow = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.TrialFeatures,
            false, SubscriptionWriteGateMode.Off, null, null, plan.ShadowOnly));
        Assert.Equal(SubscriptionGateReason.FeatureNotInPlan, shadow.Block);   // รายงานเงานับแถว partner
        Assert.True(SubscriptionGatePolicy.IsPartnerCaller(Partner));
    }

    [Fact]
    public void S200_3_ทิศตรงข้าม_กดบังคับแล้ว_partnerถูกบล็อกด้วยคีย์ใหม่()
    {
        var req = SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/settlement/batches", "GET");
        var plan = SubscriptionGatePolicy.FeaturePlanFor(Partner, SubscriptionEnforcementMode.Enforce, req);
        Assert.Equal(FeatureFlags.BankReconciliation, plan.Enforce);
        Assert.Null(plan.ShadowOnly);
        var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.TrialFeatures,
            false, SubscriptionWriteGateMode.Enforce, null, null, plan.Enforce));
        Assert.Equal(SubscriptionGateReason.FeatureNotInPlan, v.Block);
    }

    [Theory]
    [InlineData(SubscriptionEnforcementMode.Off)]
    [InlineData(SubscriptionEnforcementMode.Shadow)]
    public void S200_3_ไม่หลวมลง_คำขอเดียวกันที่เดิมถูกตัดสินด้วยคีย์เก่า_partnerยังถูกบังคับด้วยคีย์เก่า(SubscriptionEnforcementMode mode)
    {
        // /cms/sites/{id}/commerce เดิมถูกตัดสินด้วย /cms/sites (CmsWebsiteBuilder) — partner ที่ไม่มีตัวสร้างเว็บต้องยังถูกบล็อก
        var req = SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/cms/sites/0e6f/commerce/orders", "GET");
        var plan = SubscriptionGatePolicy.FeaturePlanFor(Partner, mode, req);
        Assert.Equal(FeatureFlags.CmsWebsiteBuilder, plan.Enforce);
        Assert.Equal("/cms/sites", plan.EnforceRouteKey);
        Assert.Equal(FeatureFlags.CmsEcommerce, plan.ShadowOnly);
        var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.TrialFeatures,
            false, SubscriptionWriteGateMode.LogOnly, null, null, plan.Enforce));
        Assert.Equal(SubscriptionGateReason.FeatureNotInPlan, v.Block);
        Assert.Equal(FeatureFlags.CmsWebsiteBuilder, v.Feature);
    }

    [Theory]
    [InlineData(SubscriptionEnforcementMode.Off)]
    [InlineData(SubscriptionEnforcementMode.Shadow)]
    [InlineData(SubscriptionEnforcementMode.Enforce)]
    public void S200_3_คีย์เดิม_partnerบังคับเสมอเหมือนก่อนรอบ198(SubscriptionEnforcementMode mode)
    {
        var req = SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/payroll/runs", "GET");
        var plan = SubscriptionGatePolicy.FeaturePlanFor(Partner, mode, req);
        Assert.Equal(FeatureFlags.Payroll, plan.Enforce);
        Assert.Null(plan.ShadowOnly);
    }

    [Theory]
    [InlineData(SubscriptionEnforcementMode.Shadow)]
    [InlineData(SubscriptionEnforcementMode.Enforce)]
    public void หน้าเว็บ_ตัดสินคีย์ใหม่ตามการกระทำของคำขอ_ไม่มีส่วนเงาแยก(SubscriptionEnforcementMode mode)
    {
        var req = SubscriptionGatePolicy.RequiredFeatureFor("/api/companies/x/fixedasset", "GET");
        var plan = SubscriptionGatePolicy.FeaturePlanFor(Web, mode, req);
        Assert.Equal(FeatureFlags.FixedAssets, plan.Enforce);
        Assert.Null(plan.ShadowOnly);
        Assert.False(SubscriptionGatePolicy.IsPartnerCaller(Web));
    }

    // ════════ ข้อ 23: /api/v1 ตรวจสถานะระงับ/หมดอายุผ่านสวิตช์เดียวกัน (เงาก่อน) ════════

    private static TenantCompanyTarget ApiKeyTarget(string path = "/api/v1/documents") =>
        SubscriptionGatePolicy.WithPublicApiCompany(TenantCompanyId.Resolve(null, null), path, A);

    [Theory]
    [InlineData(SubscriptionEnforcementMode.Off, SubscriptionGateAction.Skip)]
    [InlineData(SubscriptionEnforcementMode.Shadow, SubscriptionGateAction.Shadow)]
    [InlineData(SubscriptionEnforcementMode.Enforce, SubscriptionGateAction.Enforce)]
    public void ข้อ23_apiv1_รู้บริษัทจากคีย์_เดินตามสวิตช์เดียวกับหน้าเว็บ(SubscriptionEnforcementMode mode, SubscriptionGateAction expected)
    {
        var t = ApiKeyTarget();
        Assert.Equal(A, t.CompanyId);
        Assert.Equal(TenantCompanySource.ApiKey, t.Source);
        Assert.False(t.HeaderCarried);
        Assert.Equal(expected, SubscriptionGatePolicy.ActionFor(t, mode));
        Assert.True(SubscriptionGatePolicy.IsPartnerCaller(t));   // รายงานนับในคอลัมน์ partner/API
    }

    [Fact]
    public void ข้อ23_apiv1_บริษัทถูกระงับ_โหมดเงาไม่บล็อก_กดบังคับแล้วบล็อกการเขียน()
    {
        var t = ApiKeyTarget();
        foreach (var (mode, blocks) in new[] { (SubscriptionEnforcementMode.Shadow, false), (SubscriptionEnforcementMode.Enforce, true) })
        {
            var e = SubscriptionEnforcementResolver.Resolve(new SubscriptionAdminSwitchRead(mode, SubscriptionAdminSwitchSource.Stored), null, null);
            var action = SubscriptionGatePolicy.ActionFor(t, e.EffectiveMode);
            var writeMode = SubscriptionGatePolicy.WriteGateModeFor(action, t, e);
            Assert.Equal(SubscriptionWriteGateMode.Enforce, writeMode);    // เงา = ตัดสินแบบบังคับเพื่อรายงาน
            var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.TrialFeatures,
                IsWrite: true, writeMode, CompanySuspended: true, PlanActive: true,
                SubscriptionGatePolicy.FeaturePlanFor(t, e.EffectiveMode,
                    SubscriptionGatePolicy.RequiredFeatureFor("/api/v1/bank/statements", "POST")).Enforce));
            Assert.Equal(SubscriptionGateReason.CompanySuspended, v.Block);
            // ผลจริงต่อคำขอ: เงา = บันทึกแล้วปล่อยผ่าน (action Shadow) · บังคับ = 403
            Assert.Equal(blocks, action == SubscriptionGateAction.Enforce && v.Blocks);
        }
    }

    [Fact]
    public void ข้อ23_apiv1_ไม่ตรวจฟีเจอร์ตามตารางเว็บ_Connectedมีด่านของตัวเอง()
    {
        var t = ApiKeyTarget("/api/v1/ocr/scan");
        var plan = SubscriptionGatePolicy.FeaturePlanFor(t, SubscriptionEnforcementMode.Enforce,
            SubscriptionGatePolicy.RequiredFeatureFor("/api/v1/ocr/scan", "POST"));
        Assert.Null(plan.Enforce);
        Assert.Null(plan.ShadowOnly);
    }

    [Fact]
    public void ข้อ23_ทิศตรงข้าม_คำขอที่มีบริษัทอยู่แล้ว_หรือไม่ใช่apiv1_ไม่ถูกแตะ()
    {
        var header = SubscriptionGatePolicy.WithPublicApiCompany(Partner, "/api/v1/documents", Guid.NewGuid());
        Assert.Equal(TenantCompanySource.Header, header.Source);             // partner ที่ส่ง header คงพฤติกรรมเดิม
        Assert.Equal(A, header.CompanyId);
        var notV1 = SubscriptionGatePolicy.WithPublicApiCompany(TenantCompanyId.Resolve(null, null), "/api/other", A);
        Assert.Null(notV1.CompanyId);
        var noKey = SubscriptionGatePolicy.WithPublicApiCompany(TenantCompanyId.Resolve(null, null), "/api/v1/documents", "not-a-guid");
        Assert.Null(noKey.CompanyId);
        var emptyKey = SubscriptionGatePolicy.WithPublicApiCompany(TenantCompanyId.Resolve(null, null), "/api/v1/documents", Guid.Empty);
        Assert.Null(emptyKey.CompanyId);
    }

    // ════════ S200-8: แคชสวิตช์สั้น ๆ (ต่อเครื่อง · DB ยังเป็นความจริง) ════════

    [Fact]
    public void S200_8_แคช_หมดอายุตามTtl_ล้างได้_และไม่เชื่อนาฬิกาถอยหลัง()
    {
        var cache = new SubscriptionAdminSwitchCache();
        var t0 = new DateTime(2026, 9, 29, 3, 0, 0, DateTimeKind.Utc);
        Assert.Null(cache.TryGet(t0));
        var read = new SubscriptionAdminSwitchRead(SubscriptionEnforcementMode.Enforce, SubscriptionAdminSwitchSource.Stored);
        cache.Set(read, t0);
        Assert.Equal(read, cache.TryGet(t0.AddSeconds(1)));
        Assert.Null(cache.TryGet(t0 + SubscriptionAdminSwitchCache.Ttl));   // ครบอายุ = อ่านใหม่
        Assert.Null(cache.TryGet(t0.AddSeconds(-1)));                       // นาฬิกาถอยหลัง = ไม่เชื่อแคช
        cache.Set(read, t0);
        cache.Invalidate();
        Assert.Null(cache.TryGet(t0.AddSeconds(1)));                        // แอดมินกดเปลี่ยน = มีผลทันทีบนเครื่องนี้
        Assert.True(SubscriptionAdminSwitchCache.Ttl <= TimeSpan.FromSeconds(10));
    }
}
