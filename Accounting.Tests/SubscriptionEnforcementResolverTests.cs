using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **โหมดบังคับแพ็กเกจที่มีผลจริง — ตัวตัดสินตัวเดียว** (รอบ 200 คำตัดสินข้อ 14)
///
/// <para>═══ บั๊กที่ล็อกไว้ ═══ สองสวิตช์ขัดกัน: สวิตช์แอดมิน (ฐานข้อมูล) = Enforce แต่ config <c>Subscription:Enforcement:Mode</c> = LogOnly
/// (ค่าใน appsettings) ⇒ บริษัทที่ถูกระงับ/หมดอายุยังสร้าง/แก้ไขได้ต่อ ทั้งที่แอดมินกดบังคับแล้ว — silent no-op · รายงานเงาเขียน
/// "log อย่างเดียว" ทำให้แอดมินเข้าใจว่าไม่มีผลกระทบ</para>
///
/// <para>สองทิศ: (ก) สวิตช์แอดมินชนะ config เดิมทุกคู่ค่า · override ฉุกเฉินชนะสวิตช์และบอกว่าทับอยู่ (ข) ค่าตั้งต้น (Shadow · ไม่มี override)
/// ไม่บล็อกใครเพิ่ม · คำขอ partner ที่ส่ง header ไม่หลวมลง (LogOnly เดิมเมื่อไม่ได้บังคับ)</para>
/// </summary>
public class SubscriptionEnforcementResolverTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static SubscriptionAdminSwitchRead Stored(SubscriptionEnforcementMode m) => new(m, SubscriptionAdminSwitchSource.Stored);

    // ════════ ทุกคู่ค่า สวิตช์แอดมิน × override ════════

    [Theory]
    // ไม่มี override ⇒ สวิตช์แอดมินชนะเสมอ
    [InlineData(SubscriptionEnforcementMode.Off, null, SubscriptionEnforcementMode.Off, SubscriptionEnforcementSource.AdminSwitch, true)]
    [InlineData(SubscriptionEnforcementMode.Shadow, null, SubscriptionEnforcementMode.Shadow, SubscriptionEnforcementSource.AdminSwitch, true)]
    [InlineData(SubscriptionEnforcementMode.Enforce, null, SubscriptionEnforcementMode.Enforce, SubscriptionEnforcementSource.AdminSwitch, true)]
    [InlineData(SubscriptionEnforcementMode.Enforce, "", SubscriptionEnforcementMode.Enforce, SubscriptionEnforcementSource.AdminSwitch, true)]
    [InlineData(SubscriptionEnforcementMode.Enforce, "   ", SubscriptionEnforcementMode.Enforce, SubscriptionEnforcementSource.AdminSwitch, true)]
    // override ฉุกเฉิน ⇒ ทับทุกค่าของสวิตช์ และบอกว่าสวิตช์ไม่มีผล
    [InlineData(SubscriptionEnforcementMode.Off, "Off", SubscriptionEnforcementMode.Off, SubscriptionEnforcementSource.EmergencyOverride, false)]
    [InlineData(SubscriptionEnforcementMode.Off, "shadow", SubscriptionEnforcementMode.Shadow, SubscriptionEnforcementSource.EmergencyOverride, false)]
    [InlineData(SubscriptionEnforcementMode.Off, "ENFORCE", SubscriptionEnforcementMode.Enforce, SubscriptionEnforcementSource.EmergencyOverride, false)]
    [InlineData(SubscriptionEnforcementMode.Shadow, "Off", SubscriptionEnforcementMode.Off, SubscriptionEnforcementSource.EmergencyOverride, false)]
    [InlineData(SubscriptionEnforcementMode.Shadow, " Enforce ", SubscriptionEnforcementMode.Enforce, SubscriptionEnforcementSource.EmergencyOverride, false)]
    [InlineData(SubscriptionEnforcementMode.Enforce, "Off", SubscriptionEnforcementMode.Off, SubscriptionEnforcementSource.EmergencyOverride, false)]
    [InlineData(SubscriptionEnforcementMode.Enforce, "Shadow", SubscriptionEnforcementMode.Shadow, SubscriptionEnforcementSource.EmergencyOverride, false)]
    [InlineData(SubscriptionEnforcementMode.Enforce, "Enforce", SubscriptionEnforcementMode.Enforce, SubscriptionEnforcementSource.EmergencyOverride, false)]
    // override พิมพ์ผิดตอนรีบ ⇒ โหมดเงา (ไม่บล็อกใคร) ไม่ใช่ "ไม่มี override" · ตัวเลขไม่ใช่ชื่อโหมด
    [InlineData(SubscriptionEnforcementMode.Enforce, "enfroce", SubscriptionEnforcementMode.Shadow, SubscriptionEnforcementSource.EmergencyOverride, false)]
    [InlineData(SubscriptionEnforcementMode.Off, "2", SubscriptionEnforcementMode.Shadow, SubscriptionEnforcementSource.EmergencyOverride, false)]
    [InlineData(SubscriptionEnforcementMode.Off, "LogOnly", SubscriptionEnforcementMode.Shadow, SubscriptionEnforcementSource.EmergencyOverride, false)]
    public void ทุกคู่ค่า_สวิตช์แอดมินกับoverride(SubscriptionEnforcementMode admin, string? overrideRaw,
        SubscriptionEnforcementMode expected, SubscriptionEnforcementSource source, bool adminHasEffect)
    {
        var e = SubscriptionEnforcementResolver.Resolve(Stored(admin), overrideRaw, null);
        Assert.Equal(expected, e.EffectiveMode);
        Assert.Equal(source, e.Source);
        Assert.Equal(adminHasEffect, e.AdminSwitchHasEffect);
        Assert.Equal(admin, e.AdminSwitch);
        Assert.Equal(expected == SubscriptionEnforcementMode.Enforce ? SubscriptionWriteGateMode.Enforce : SubscriptionWriteGateMode.LogOnly,
            e.HeaderWriteGateMode);
        Assert.Contains(SubscriptionEnforcementResolver.Label(expected), e.Explanation);
        if (!adminHasEffect)
        {
            Assert.Contains(SubscriptionEnforcementResolver.OverrideKey, e.Explanation);
            Assert.Contains("ยังไม่มีผล", e.Explanation);
        }
    }

    [Fact]
    public void overrideพิมพ์ผิด_มีคำเตือน_และไม่นับว่ารู้จัก()
    {
        var e = SubscriptionEnforcementResolver.Resolve(Stored(SubscriptionEnforcementMode.Enforce), "enfroce", null);
        Assert.False(e.OverrideRecognized);
        Assert.Contains(e.Warnings, w => w.Contains("enfroce") && w.Contains("ไม่รู้จัก"));
        var ok = SubscriptionEnforcementResolver.Resolve(Stored(SubscriptionEnforcementMode.Enforce), "Off", null);
        Assert.True(ok.OverrideRecognized);
        Assert.Empty(ok.Warnings);
    }

    [Theory]
    [InlineData(SubscriptionAdminSwitchSource.NoRow, SubscriptionEnforcementSource.AdminSwitchDefault, "ค่าตั้งต้น")]
    [InlineData(SubscriptionAdminSwitchSource.Unreadable, SubscriptionEnforcementSource.AdminSwitchUnreadable, "อ่านสวิตช์แอดมิน")]
    public void อ่านสวิตช์ไม่ได้หรือยังไม่มีแถว_เป็นโหมดเงา_และบอกเหตุ(SubscriptionAdminSwitchSource read,
        SubscriptionEnforcementSource expected, string phrase)
    {
        var e = SubscriptionEnforcementResolver.Resolve(new SubscriptionAdminSwitchRead(SubscriptionEnforcementMode.Shadow, read), null, null);
        Assert.Equal(SubscriptionEnforcementMode.Shadow, e.EffectiveMode);
        Assert.Equal(expected, e.Source);
        Assert.True(e.AdminSwitchHasEffect);
        Assert.Contains(phrase, e.Explanation);
        // override ยังชนะแม้อ่านสวิตช์ไม่ได้ (นี่คือเหตุผลที่มี override ฉุกเฉิน)
        var o = SubscriptionEnforcementResolver.Resolve(new SubscriptionAdminSwitchRead(SubscriptionEnforcementMode.Shadow, read), "Off", null);
        Assert.Equal(SubscriptionEnforcementMode.Off, o.EffectiveMode);
        Assert.Equal(SubscriptionEnforcementSource.EmergencyOverride, o.Source);
    }

    // ════════ บั๊กต้นทาง: config เดิม LogOnly ห้ามกลบสวิตช์แอดมินอีก ════════

    [Theory]
    [InlineData("LogOnly")]
    [InlineData("Off")]
    [InlineData("Enforce")]
    public void configเดิม_ไม่เปลี่ยนโหมดที่มีผลจริง_และเตือนให้ลบ(string legacy)
    {
        foreach (var admin in new[] { SubscriptionEnforcementMode.Off, SubscriptionEnforcementMode.Shadow, SubscriptionEnforcementMode.Enforce })
        {
            var withLegacy = SubscriptionEnforcementResolver.Resolve(Stored(admin), null, legacy);
            var without = SubscriptionEnforcementResolver.Resolve(Stored(admin), null, null);
            Assert.Equal(without.EffectiveMode, withLegacy.EffectiveMode);
            Assert.Contains(withLegacy.Warnings, w => w.Contains(SubscriptionEnforcementResolver.LegacyKey) && w.Contains("เลิกใช้"));
            Assert.Empty(without.Warnings);
            Assert.False(without.LegacyHeaderEnforce);
            // LogOnly/Off ของคีย์เดิม = ไม่เข้มกว่าเดิม ⇒ ไม่มีผล · Enforce = คุม partner ต่อ (ทดสอบแยกด้านล่าง)
            if (legacy != "Enforce")
            {
                Assert.Equal(without.HeaderWriteGateMode, withLegacy.HeaderWriteGateMode);
                Assert.False(withLegacy.LegacyHeaderEnforce);
            }
        }
    }

    // ════════ รอบ 200 ฝ่ายค้าน S200-1: คีย์เดิม = Enforce ห้ามหลวมลงเงียบ ๆ หลัง deploy ════════

    [Theory]
    [InlineData(SubscriptionEnforcementMode.Off)]
    [InlineData(SubscriptionEnforcementMode.Shadow)]
    public void S200_1_คีย์เดิมEnforce_สวิตช์ยังไม่บังคับ_partnerของบริษัทถูกระงับยังถูกบล็อกการเขียนแบบเดิม(SubscriptionEnforcementMode admin)
    {
        var e = SubscriptionEnforcementResolver.Resolve(Stored(admin), null, "Enforce");
        Assert.True(e.LegacyHeaderEnforce);
        Assert.Equal(SubscriptionWriteGateMode.Enforce, e.HeaderWriteGateMode);
        Assert.Equal(admin, e.EffectiveMode);                 // หน้าเว็บไม่เข้มขึ้น (โหมดที่มีผลจริงยังตามสวิตช์)
        Assert.Contains(SubscriptionEnforcementResolver.LegacyKey, e.Explanation);
        Assert.Contains(e.Warnings, w => w.Contains("ยังมีผล"));

        var partner = TenantCompanyId.Resolve(null, A.ToString());
        var action = SubscriptionGatePolicy.ActionFor(partner, e.EffectiveMode);
        var writeMode = SubscriptionGatePolicy.WriteGateModeFor(action, partner, e);
        Assert.Equal(SubscriptionWriteGateMode.Enforce, writeMode);
        var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.TrialFeatures,
            IsWrite: true, writeMode, CompanySuspended: true, PlanActive: true, RequiredFeature: null));
        Assert.Equal(SubscriptionGateReason.CompanySuspended, v.Block);

        // ทิศตรงข้าม: หน้าเว็บ (route) ในโหมดเงายังไม่ถูกบล็อก — คีย์เดิมไม่เคยบังคับหน้าเว็บ
        var web = TenantCompanyId.Resolve(A.ToString(), null);
        Assert.NotEqual(SubscriptionGateAction.Enforce, SubscriptionGatePolicy.ActionFor(web, e.EffectiveMode));
    }

    [Fact]
    public void S200_1_ลบคีย์เดิมแล้ว_partnerเดินตามสวิตช์_LogOnlyระหว่างโหมดเงา()
    {
        var e = SubscriptionEnforcementResolver.Resolve(Stored(SubscriptionEnforcementMode.Shadow), null, "   ");
        Assert.False(e.LegacyHeaderEnforce);
        Assert.Equal(SubscriptionWriteGateMode.LogOnly, e.HeaderWriteGateMode);
        Assert.Empty(e.Warnings);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    [InlineData("Enforce", true, true)]
    [InlineData(" enforce ", true, true)]
    [InlineData("LogOnly", true, false)]
    public void S200_1_คำเตือนตอนบูต_เมื่อยังตั้งคีย์เดิม(string? legacy, bool warns, bool saysStillEffective)
    {
        var w = SubscriptionEnforcementResolver.LegacyBootWarning(legacy);
        Assert.Equal(warns, w != null);
        if (w != null)
        {
            Assert.Contains(SubscriptionEnforcementResolver.LegacyKey, w);
            Assert.Equal(saysStillEffective, w.Contains("ยังมีผล"));
        }
    }

    [Fact]
    public void แอดมินกดบังคับ_configเดิมLogOnly_บริษัทถูกระงับถูกบล็อกการเขียนจริง_ทั้งเว็บและpartner()
    {
        var e = SubscriptionEnforcementResolver.Resolve(Stored(SubscriptionEnforcementMode.Enforce), null, "LogOnly");
        foreach (var target in new[] { TenantCompanyId.Resolve(A.ToString(), null), TenantCompanyId.Resolve(null, A.ToString()) })
        {
            var action = SubscriptionGatePolicy.ActionFor(target, e.EffectiveMode);
            Assert.Equal(SubscriptionGateAction.Enforce, action);
            var writeMode = SubscriptionGatePolicy.WriteGateModeFor(action, target, e);
            Assert.Equal(SubscriptionWriteGateMode.Enforce, writeMode);
            var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.TrialFeatures,
                IsWrite: true, writeMode, CompanySuspended: true, PlanActive: true, RequiredFeature: null));
            Assert.Equal(SubscriptionGateReason.CompanySuspended, v.Block);
            // อ่านยังได้
            var read = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.TrialFeatures,
                IsWrite: false, writeMode, CompanySuspended: true, PlanActive: true, RequiredFeature: null));
            Assert.False(read.Blocks);
        }
    }

    [Fact]
    public void ค่าตั้งต้น_โหมดเงา_partnerที่ส่งheader_ด่านเขียนยังเป็นLogOnlyเหมือนเดิม_ไม่หลวมไม่เข้มขึ้น()
    {
        var e = SubscriptionEnforcementResolver.Resolve(Stored(SubscriptionEnforcementMode.Shadow), null, null);
        var partner = TenantCompanyId.Resolve(null, A.ToString());
        var action = SubscriptionGatePolicy.ActionFor(partner, e.EffectiveMode);
        Assert.Equal(SubscriptionGateAction.Enforce, action);
        var writeMode = SubscriptionGatePolicy.WriteGateModeFor(action, partner, e);
        Assert.Equal(SubscriptionWriteGateMode.LogOnly, writeMode);
        var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.TrialFeatures,
            IsWrite: true, writeMode, CompanySuspended: true, PlanActive: false, RequiredFeature: null));
        Assert.False(v.Blocks);
        Assert.Equal(new[] { SubscriptionGateReason.CompanySuspended, SubscriptionGateReason.PlanExpired }, v.LogOnly);
    }

    [Fact]
    public void โหมดเงา_ตัดสินด่านเขียนแบบบังคับ_รายงานบอกว่าจะถูกบล็อกจริง_ไม่ใช่logอย่างเดียว()
    {
        var e = SubscriptionEnforcementResolver.Resolve(Stored(SubscriptionEnforcementMode.Shadow), null, "LogOnly");
        var web = TenantCompanyId.Resolve(A.ToString(), null);
        var action = SubscriptionGatePolicy.ActionFor(web, e.EffectiveMode);
        Assert.Equal(SubscriptionGateAction.Shadow, action);
        var writeMode = SubscriptionGatePolicy.WriteGateModeFor(action, web, e);
        Assert.Equal(SubscriptionWriteGateMode.Enforce, writeMode);
        var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.TrialFeatures,
            IsWrite: true, writeMode, CompanySuspended: false, PlanActive: false, RequiredFeature: null));
        Assert.Equal(SubscriptionGateReason.PlanExpired, v.Block);
        Assert.Empty(v.LogOnly);
    }

    [Fact]
    public void overrideฉุกเฉินOff_หน้าเว็บไม่ถูกตรวจ_แต่partnerยังถูกตัดสินฟีเจอร์เหมือนเดิม()
    {
        var e = SubscriptionEnforcementResolver.Resolve(Stored(SubscriptionEnforcementMode.Enforce), "Off", null);
        Assert.Equal(SubscriptionGateAction.Skip, SubscriptionGatePolicy.ActionFor(TenantCompanyId.Resolve(A.ToString(), null), e.EffectiveMode));
        var partner = TenantCompanyId.Resolve(null, A.ToString());
        var action = SubscriptionGatePolicy.ActionFor(partner, e.EffectiveMode);
        Assert.Equal(SubscriptionGateAction.Enforce, action);
        var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(SubscriptionStatus.Active, FeatureFlags.TrialFeatures,
            IsWrite: false, SubscriptionGatePolicy.WriteGateModeFor(action, partner, e), null, null, FeatureFlags.Payroll));
        Assert.Equal(SubscriptionGateReason.FeatureNotInPlan, v.Block);
    }

    [Theory]
    [InlineData(SubscriptionGateAction.Skip, false, SubscriptionEnforcementMode.Enforce, SubscriptionWriteGateMode.Off)]
    [InlineData(SubscriptionGateAction.Shadow, false, SubscriptionEnforcementMode.Shadow, SubscriptionWriteGateMode.Enforce)]
    [InlineData(SubscriptionGateAction.Enforce, false, SubscriptionEnforcementMode.Enforce, SubscriptionWriteGateMode.Enforce)]
    [InlineData(SubscriptionGateAction.Enforce, true, SubscriptionEnforcementMode.Enforce, SubscriptionWriteGateMode.Enforce)]
    [InlineData(SubscriptionGateAction.Enforce, true, SubscriptionEnforcementMode.Shadow, SubscriptionWriteGateMode.LogOnly)]
    [InlineData(SubscriptionGateAction.Enforce, true, SubscriptionEnforcementMode.Off, SubscriptionWriteGateMode.LogOnly)]
    public void ด่านเขียน_ตามการกระทำและที่มาของบริษัท(SubscriptionGateAction action, bool header,
        SubscriptionEnforcementMode effective, SubscriptionWriteGateMode expected)
    {
        var target = header ? TenantCompanyId.Resolve(null, A.ToString()) : TenantCompanyId.Resolve(A.ToString(), null);
        var e = SubscriptionEnforcementResolver.Resolve(Stored(effective), null, null);
        Assert.Equal(expected, SubscriptionGatePolicy.WriteGateModeFor(action, target, e));
    }

    [Theory]
    [InlineData("Off", SubscriptionEnforcementMode.Off)]
    [InlineData(" shadow", SubscriptionEnforcementMode.Shadow)]
    [InlineData("ENFORCE", SubscriptionEnforcementMode.Enforce)]
    [InlineData("1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ชื่อโหมด_อ่านเฉพาะชื่อ(string? raw, SubscriptionEnforcementMode? expected)
    {
        Assert.Equal(expected, SubscriptionEnforcementResolver.ParseMode(raw));
    }

    // ════════ endpoint ของรายงานเงา ════════

    [Theory]
    [InlineData("api/companies/{companyId:guid}/bank/accounts", null, "api/companies/{companyid}/bank/accounts")]
    [InlineData("/api/companies/{companyId:guid}/settlement/batches/{batchId:guid}", null, "api/companies/{companyid}/settlement/batches/{batchid}")]
    [InlineData("api/files/{*path}", null, "api/files/{path}")]
    [InlineData("api/x/{id?}", null, "api/x/{id}")]
    [InlineData(null, "/api/companies/11111111-1111-1111-1111-111111111111/payroll/runs/42", "api/companies/{id}/payroll/runs/{id}")]
    [InlineData("", "/api/companies/11111111-1111-1111-1111-111111111111/bank", "api/companies/{id}/bank")]
    [InlineData(null, null, "")]
    public void endpoint_ใช้templateไม่มีid(string? template, string? path, string expected)
    {
        Assert.Equal(expected, SubscriptionGatePolicy.EndpointKey(template, path));
    }

    [Fact]
    public void endpoint_ยาวเกิน_ตัดที่เพดานคอลัมน์()
    {
        var key = SubscriptionGatePolicy.EndpointKey(null, "/" + string.Join('/', Enumerable.Repeat("segment", 60)));
        Assert.Equal(SubscriptionGatePolicy.EndpointMaxLength, key.Length);
    }
}
