using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 236 (คำตัดสิน 236-1…236-6) — เจ้าของ: "ตกลงบริษัทนี้เป็น Enterprise หรือ Free — ปรับปรุงระบบให้ใช้ข้อมูลที่ถูกต้องเพียงหนึ่งเดียว" ·
/// บริษัท "เน็ก แอค" ผูก License Enterprise ของ "สมฤทธิ์" ถึง 31/12/2593 · แถวบริษัท = Free Edition (enum FreeTrial · ฟรีถาวร) ·
/// หน้าซื้อเคยให้จ่าย ฿10,000 ซื้อ Enterprise 12 เดือน — ตัวตัดสินเดียว <see cref="PlanIdentityResolver"/> (pure)
/// </summary>
public class EffectivePlanRound236Tests
{
    private static readonly DateTime Now = new(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
    /// <summary>31/12/2593 (พ.ศ.) เวลาไทย = 2050-12-31 ค.ศ.</summary>
    private static readonly DateTime OwnerLicenseEnd = new(2050, 12, 31, 10, 0, 0, DateTimeKind.Utc);

    private static PlanIdentityResolver.LicenseSide EnterpriseLicense(SubscriptionStatus status = SubscriptionStatus.Active, DateTime? end = null, int grace = 7)
        => new(SubscriptionPlan.Enterprise, "Enterprise", status, end ?? OwnerLicenseEnd, Guid.NewGuid(), "สมฤทธิ์", "somrit@test.local", grace);

    private static PlanIdentityResolver.PlanIdentity NekAcc(PlanIdentityResolver.LicenseSide? license)
        => PlanIdentityResolver.Resolve(SubscriptionPlan.FreeTrial, SubscriptionStatus.Active, Now.AddYears(100), "Free Edition", license);

    [Fact]
    public void เจ้าของ_บริษัทผูกLicenseEnterprise_แพ็กเกจที่มีผลคือEnterprise_ซื้อเองไม่ได้_บอกLicenseผู้ถือวันหมดอายุทางไปต่อ()
    {
        var id = NekAcc(EnterpriseLicense());
        Assert.Equal(PlanIdentityResolver.SourceLicense, id.Source);
        Assert.True(id.ViaLicense);
        Assert.Equal(SubscriptionPlan.Enterprise, id.Plan);
        Assert.Equal("Enterprise", id.PlanName);

        var v = PlanIdentityResolver.CompanyPurchase(id, Now);
        Assert.False(v.Allowed);
        Assert.Contains("Enterprise", v.Reason);
        Assert.Contains("สมฤทธิ์", v.Reason);
        Assert.Contains("31/12/2593", v.Reason);
        Assert.Contains("ไม่มีผล", v.Reason);
        Assert.Contains("ถอดบริษัทออกจาก License", v.NextStep);
        Assert.Contains("ให้ผู้ถือ License สมฤทธิ์ ต่ออายุ", v.NextStep);   // ผู้ใช้ที่ไม่ใช่ผู้ถือ — ทางไปต่อที่มีจริง
        Assert.False(v.HolderMayRenew);
        Assert.False(v.LicenseRenewal);
    }

    [Fact]
    public void ทิศตรงข้าม_บริษัทไม่ผูกLicense_ซื้อเองได้ตามปกติ()
    {
        var id = NekAcc(null);
        Assert.Equal(PlanIdentityResolver.SourceCompany, id.Source);
        Assert.False(id.ViaLicense);
        Assert.Null(id.License);
        var v = PlanIdentityResolver.CompanyPurchase(id, Now);
        Assert.True(v.Allowed);
        Assert.Null(v.Reason);
    }

    [Fact]
    public void Licenseหมดอายุแต่ยังผูกอยู่_ยังเป็นตัวคุม_ซื้อเองไม่ได้_บอกว่าหมดอายุและให้ถอดออกก่อน()
    {
        var expired = EnterpriseLicense(SubscriptionStatus.Expired, Now.AddDays(-30));
        var id = NekAcc(expired);
        Assert.True(id.ViaLicense);   // ตรงกับ GetEffectivePlanAsync — License ที่ผูกอยู่ยังคุม (อ่านอย่างเดียว) จนกว่าจะถอด
        var v = PlanIdentityResolver.CompanyPurchase(id, Now);
        Assert.False(v.Allowed);
        Assert.Contains("หมดอายุแล้ว", v.Reason);
        Assert.Contains("ถอดบริษัทออกจาก License", v.NextStep);
        Assert.DoesNotContain("อยู่แล้ว", v.Reason);   // ไม่บอกว่า "ใช้ Enterprise อยู่แล้ว" ทั้งที่หมดอายุ
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active, 10, true)]
    [InlineData(SubscriptionStatus.Trial, 1, true)]
    [InlineData(SubscriptionStatus.PastDue, -3, true)]     // เลยวันหมดอายุ 3 วัน อยู่ในระยะผ่อนผัน 7 วัน
    [InlineData(SubscriptionStatus.Active, -8, false)]     // พ้นระยะผ่อนผัน
    [InlineData(SubscriptionStatus.Expired, 10, false)]
    [InlineData(SubscriptionStatus.Suspended, 10, false)]
    [InlineData(SubscriptionStatus.Cancelled, 10, false)]
    public void LicenseActive_สูตรเดียวกับ_GetEffectivePlanAsync(SubscriptionStatus status, int daysToEnd, bool expected)
        => Assert.Equal(expected, PlanIdentityResolver.LicenseActive(status, Now.AddDays(daysToEnd), 7, Now));

    [Fact]
    public void ป้ายเดียว_Licenseชนะ_ไม่พิมพ์ชื่อenum_FreeTrial()
    {
        Assert.Equal("Enterprise · ผ่าน License", PlanIdentityResolver.Label(NekAcc(EnterpriseLicense()), companyIsPermanentFree: true));
        var free = PlanIdentityResolver.Label(NekAcc(null), companyIsPermanentFree: true);
        Assert.Equal("Free Edition · ฟรีถาวร", free);
        Assert.DoesNotContain("FreeTrial", free);
        Assert.DoesNotContain("Active", free);
    }

    [Fact]
    public void ป้ายบริษัทช่วงทดลองใช้_บอกว่าทดลองใช้()
    {
        var trial = PlanIdentityResolver.Resolve(SubscriptionPlan.Pro, SubscriptionStatus.Trial, Now.AddDays(14), "Pro", null);
        Assert.Equal("Pro · ทดลองใช้", PlanIdentityResolver.Label(trial));
        Assert.True(PlanIdentityResolver.CompanyPurchase(trial, Now).Allowed);   // ทดลองใช้ (ไม่ผูก License) ซื้อได้
    }

    [Fact]
    public void ป้ายไม่รู้ชื่อแม่แบบ_ใช้ชื่อenumสำรอง_ไม่ว่าง()
        => Assert.Equal("Basic · ใช้งานอยู่", PlanIdentityResolver.Label(PlanIdentityResolver.Resolve(SubscriptionPlan.Basic, SubscriptionStatus.Active, Now, null, null)));

    [Fact]
    public void Preview_RefusedByLicense_แพ็กเกจปัจจุบันคือLicense_ยอด0_ทางไปต่อหน้าLicense()
    {
        var id = NekAcc(EnterpriseLicense());
        var v = PlanIdentityResolver.CompanyPurchase(id, Now);
        var pv = PlanPurchasePreview.RefusedByLicense(id, new PlanPurchasePreview.Target(SubscriptionPlan.Enterprise, "Enterprise", BillingCycle.Annual, 833.33m), v, Now);
        Assert.False(pv.Allowed);
        Assert.Equal("Refused", pv.Kind);
        Assert.Equal("Enterprise", pv.CurrentPlanName);   // เดิม "Free Edition → Enterprise"
        Assert.Equal(0m, pv.AmountDue);
        Assert.Equal(v.Reason, pv.RefusalReason);
        Assert.Equal(v.NextStep, pv.NextStep);
        Assert.Contains("31/12/2593", pv.DateLine);
    }

    [Fact]
    public void Resolveเดิมไม่มีLicense_ฟิลด์ใหม่ไม่บังคับ_ผู้เรียกเดิมยังได้ผลเดิม()
    {
        // ผู้เรียกเดิม (DashboardService/CompanyService) ส่ง LicenseSide 4 ช่อง — ยังเป็นที่มา License และซื้อเองไม่ได้
        var id = PlanIdentityResolver.Resolve(SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, Now, "Free",
            new PlanIdentityResolver.LicenseSide(SubscriptionPlan.Enterprise, "Enterprise", SubscriptionStatus.Active, Now.AddYears(1)));
        Assert.True(id.ViaLicense);
        var v = PlanIdentityResolver.CompanyPurchase(id, Now);
        Assert.False(v.Allowed);
        Assert.Contains("ผู้ถือ License", v.Reason);   // ไม่รู้ชื่อผู้ถือ = ข้อความกลาง ไม่ว่าง
    }

    [Fact]
    public void ตารางสถานะเดียว_ศูนย์บิลกับป้ายแพ็กเกจตรงกัน()
    {
        foreach (var s in Enum.GetValues<SubscriptionStatus>())
            Assert.False(string.IsNullOrWhiteSpace(PlanIdentityResolver.StatusLabel(s)));
        Assert.Equal("ใช้งานอยู่", PlanIdentityResolver.StatusLabel(SubscriptionStatus.Active));
        Assert.Equal("เกินกำหนดต่ออายุ", PlanIdentityResolver.StatusLabel(SubscriptionStatus.PastDue));
    }

    // ── ฝ่ายค้าน B1: คืนทางต่ออายุ License ที่เคยใช้ได้จริง (รอบ 219 S5 · AccountPlanExtendedViaSlip) ──

    [Fact]
    public void B1_ผู้ถือLicenseจ่ายแพ็กระดับเดียวกัน_ยอมเป็นการต่ออายุLicense()
    {
        var v = PlanIdentityResolver.CompanyPurchase(NekAcc(EnterpriseLicense()), Now, actorIsLicenseHolder: true, targetPlan: SubscriptionPlan.Enterprise);
        Assert.True(v.Allowed);
        Assert.True(v.LicenseRenewal);
        Assert.Equal(PlanIdentityResolver.LicenseRenewalHeadline, v.NextStep);
    }

    [Fact]
    public void B1_ทิศตรงข้าม_ผู้ถือเลือกแพ็กต่างระดับ_ปฏิเสธพร้อมทางต่ออายุด้วยแพ็กของLicense()
    {
        var v = PlanIdentityResolver.CompanyPurchase(NekAcc(EnterpriseLicense()), Now, actorIsLicenseHolder: true, targetPlan: SubscriptionPlan.Pro);
        Assert.False(v.Allowed);
        Assert.False(v.LicenseRenewal);
        Assert.True(v.HolderMayRenew);
        Assert.Contains("ต่ออายุ License ได้ที่หน้านี้โดยเลือกแพ็ก Enterprise รอบเดิม", v.NextStep);
        Assert.Contains("ติดต่อผู้ดูแลระบบ", v.NextStep);
    }

    [Fact]
    public void B1_ทิศตรงข้าม_ไม่ใช่ผู้ถือ_แพ็กระดับเดียวกันก็ปฏิเสธ()
    {
        var v = PlanIdentityResolver.CompanyPurchase(NekAcc(EnterpriseLicense()), Now, actorIsLicenseHolder: false, targetPlan: SubscriptionPlan.Enterprise);
        Assert.False(v.Allowed);
        Assert.False(v.HolderMayRenew);
    }

    [Fact]
    public void B1_Licenseถูกระงับ_ผู้ถือจ่ายแพ็กเดิมก็ไม่ยอม_จ่ายแล้วไม่ปลดสถานะ()
    {
        var v = PlanIdentityResolver.CompanyPurchase(NekAcc(EnterpriseLicense(SubscriptionStatus.Suspended)), Now, true, SubscriptionPlan.Enterprise);
        Assert.False(v.Allowed);
        Assert.Contains("ติดต่อผู้ดูแลระบบ", v.NextStep);
    }

    [Fact]
    public void B1_Licenseหมดอายุ_ผู้ถือต่ออายุได้_ทางเดิมที่cascadeฟื้นLicense()
        => Assert.True(PlanIdentityResolver.CompanyPurchase(NekAcc(EnterpriseLicense(SubscriptionStatus.Expired, Now.AddDays(-30))), Now, true,
            SubscriptionPlan.Enterprise).LicenseRenewal);

    [Fact]
    public void B1_Preview_ต่ออายุLicense_บอกตรงๆ_วันหมดอายุใหม่ของLicense()
    {
        var id = NekAcc(EnterpriseLicense());
        var pv = PlanPurchasePreview.ForLicenseRenewal(id, new PlanPurchasePreview.Target(SubscriptionPlan.Enterprise, "Enterprise", BillingCycle.Annual, 833.33m),
            new PlanPurchasePreview.Money(10_000m, 0m, 10_000m), Now, vatIncluded: true);
        Assert.True(pv.Allowed);
        Assert.Equal("LicenseRenewal", pv.Kind);
        Assert.Equal(PlanIdentityResolver.LicenseRenewalHeadline, pv.Headline);
        Assert.Contains("ครอบทุกบริษัทใต้ License", pv.Headline);
        Assert.Contains("31/12/2593 → 31/12/2594", pv.DateLine);   // ต่อจากวันหมดอายุเดิมของ License 12 เดือน
        Assert.Equal(10_000m, pv.AmountDue);
        Assert.Contains(pv.Notes, n => n.Contains("ไม่ใช่การซื้อแพ็กเกจระดับบริษัท"));
    }

    [Fact]
    public void B1_ข้อความเตือนLicense_ชี้ทางต่ออายุที่มีอยู่จริง()
    {
        var t = PlanIdentityResolver.LicenseRenewalHowTo("Enterprise");
        Assert.Contains("เลือกแพ็ก Enterprise รอบเดิม", t);
        Assert.DoesNotContain("หน้าจัดการ License", t);
    }

    // ── ฝ่ายค้าน S3: สิทธิ์ใช้ทรัพยากร (OCR) อ่านตามสถานะ License ไม่ใช่แถวบริษัทที่ค้าง Suspended ──

    [Fact]
    public void S3_LicenseActive_แถวบริษัทSuspended_ยังใช้ได้()
        => Assert.True(PlanIdentityResolver.CompanyRowUsable(SubscriptionStatus.Suspended, NekAcc(EnterpriseLicense()), Now));

    [Fact]
    public void S3_ทิศตรงข้าม_ไม่มีLicense_แถวSuspended_ถูกตัด_และLicenseหมดอายุพ้นผ่อนผันก็ถูกตัด()
    {
        Assert.False(PlanIdentityResolver.CompanyRowUsable(SubscriptionStatus.Suspended, NekAcc(null), Now));
        Assert.False(PlanIdentityResolver.CompanyRowUsable(SubscriptionStatus.Cancelled, NekAcc(null), Now));
        Assert.True(PlanIdentityResolver.CompanyRowUsable(SubscriptionStatus.Active, NekAcc(null), Now));
        Assert.False(PlanIdentityResolver.CompanyRowUsable(SubscriptionStatus.Active, NekAcc(EnterpriseLicense(SubscriptionStatus.Expired, Now.AddDays(-30))), Now));
    }
}
