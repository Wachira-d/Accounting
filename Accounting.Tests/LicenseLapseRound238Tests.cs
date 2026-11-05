using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 238 (คำตัดสิน 236-3 · License หมดอายุ/ระงับ — บริษัทยังอยู่ใต้ License ไม่ถอยไปใช้แถวบริษัทอัตโนมัติ แต่ต้อง "อยู่รอด" ได้) — ตัวตัดสิน pure
/// <list type="bullet">
/// <item><see cref="PlanIdentityResolver.LicenseStatusAfter"/> — ผ่อนผันมีจริง (เดิมงานเตือนตั้ง Expired ทันทีที่ถึงวันหมดอายุ ⇒ ผ่อนผัน 0 วัน)</item>
/// <item><see cref="PlanIdentityResolver.GateStatus"/> — License ระงับ/ยกเลิก ⇒ ด่านเห็น "หมดอายุ" (อ่าน/ส่งออกได้ · เขียน 402) ไม่ใช่ 403 ทุกคำขอ</item>
/// <item><see cref="SubscriptionGatePolicy.IsComplianceOutputPath"/> — ไฟล์ยื่นแบบ/ส่งออก ผ่านด่าน "หมดอายุ" เท่านั้น (ไม่ผ่านด่านบริษัทถูกระงับ)</item>
/// <item>พรีวิวถอด · คำเตือนผูกบริษัทที่จ่ายเอง (236-9) · ข้อความแจ้งเจ้าของบริษัท</item>
/// </list>
/// </summary>
public class LicenseLapseRound238Tests
{
    private static readonly DateTime End = new(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc);   // 10/11/2569 10:00 เวลาไทย
    private const string Cid = "11111111-1111-1111-1111-111111111111";

    // ════════ LicenseStatusAfter ════════

    [Theory]
    // ก่อนวันหมดอายุ ⇒ คงเดิม (ทิศตรงข้าม: ไม่แตะ License ที่ยังไม่หมด)
    [InlineData(SubscriptionStatus.Active, -1.0, SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Trial, -0.1, SubscriptionStatus.Trial)]
    [InlineData(SubscriptionStatus.PastDue, -3.0, SubscriptionStatus.PastDue)]
    // พ้นวันหมดอายุ แต่ยังในระยะผ่อนผัน 7 วัน ⇒ PastDue (เขียนได้เต็ม) — เดิม Expired ทันที
    [InlineData(SubscriptionStatus.Active, 0.01, SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Trial, 3.0, SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Active, 7.0, SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.PastDue, 6.9, SubscriptionStatus.PastDue)]
    // พ้นระยะผ่อนผัน ⇒ Expired
    [InlineData(SubscriptionStatus.Active, 7.01, SubscriptionStatus.Expired)]
    [InlineData(SubscriptionStatus.PastDue, 30.0, SubscriptionStatus.Expired)]
    // ระงับ/ยกเลิก (แอดมินตั้ง) ⇒ ไม่แตะ ไม่ว่าเวลาไหน · Expired ที่ตั้งแล้วไม่ถูกปลุกกลับ
    [InlineData(SubscriptionStatus.Suspended, 3.0, SubscriptionStatus.Suspended)]
    [InlineData(SubscriptionStatus.Suspended, 30.0, SubscriptionStatus.Suspended)]
    [InlineData(SubscriptionStatus.Cancelled, 3.0, SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.Expired, 3.0, SubscriptionStatus.Expired)]
    public void LicenseStatusAfter_ตาราง(SubscriptionStatus from, double daysAfterEnd, SubscriptionStatus expected)
        => Assert.Equal(expected, PlanIdentityResolver.LicenseStatusAfter(from, End, 7, End.AddDays(daysAfterEnd)));

    [Fact]
    public void LicenseStatusAfter_ผ่อนผันศูนย์วัน_หมดอายุทันที_และผลสอดคล้องกับLicenseActive()
    {
        Assert.Equal(SubscriptionStatus.Expired, PlanIdentityResolver.LicenseStatusAfter(SubscriptionStatus.Active, End, 0, End.AddMinutes(1)));
        // ทุกจุดในระยะผ่อนผัน: สถานะที่งานเตือนตั้ง ⇒ LicenseActive = true (เดิมได้ false ทันทีที่หมดอายุ)
        for (var h = 1; h <= 7 * 24; h += 12)
        {
            var now = End.AddHours(h);
            var st = PlanIdentityResolver.LicenseStatusAfter(SubscriptionStatus.Active, End, 7, now);
            Assert.True(PlanIdentityResolver.LicenseActive(st, End, 7, now), $"ชั่วโมงที่ {h}");
        }
        var after = End.AddDays(8);
        Assert.False(PlanIdentityResolver.LicenseActive(PlanIdentityResolver.LicenseStatusAfter(SubscriptionStatus.PastDue, End, 7, after), End, 7, after));
    }

    // ════════ GateStatus + Decide ════════

    private static PlanIdentityResolver.PlanIdentity ViaLicense(SubscriptionStatus licenseStatus)
        => PlanIdentityResolver.Resolve(SubscriptionPlan.FreeTrial, SubscriptionStatus.Active, End.AddYears(100), "Free Edition",
            new PlanIdentityResolver.LicenseSide(SubscriptionPlan.Enterprise, "Enterprise", licenseStatus, End, Guid.NewGuid(), "สมฤทธิ์", null, 7));

    private static SubscriptionGateFacts Facts(SubscriptionStatus status, bool isWrite, bool? planActive, bool? companySuspended = false,
        string? path = null)
        => new SubscriptionGateFacts(status, FeatureFlags.ProFeatures, isWrite, SubscriptionWriteGateMode.Enforce, companySuspended, planActive, null) with
        {
            IsComplianceOutput = SubscriptionGatePolicy.IsComplianceOutputPath(path, isWrite ? "POST" : "GET"),
        };

    [Theory]
    [InlineData(SubscriptionStatus.Suspended)]
    [InlineData(SubscriptionStatus.Cancelled)]
    public void GateStatus_Licenseระงับหรือยกเลิก_ด่านเห็นหมดอายุ_อ่านได้_เขียน402(SubscriptionStatus licenseStatus)
    {
        var id = ViaLicense(licenseStatus);
        var gate = PlanIdentityResolver.GateStatus(id);
        Assert.Equal(SubscriptionStatus.Expired, gate);
        Assert.Equal(licenseStatus, id.Status);   // ไม่แต่งสถานะที่แสดง/เก็บ — เฉพาะที่ส่งให้ด่าน
        Assert.False(SubscriptionGatePolicy.Decide(Facts(gate, isWrite: false, planActive: false)).Blocks);   // อ่านได้ (เดิม 403 SubscriptionInactive)
        Assert.Equal(SubscriptionGateReason.PlanExpired, SubscriptionGatePolicy.Decide(Facts(gate, isWrite: true, planActive: false)).Block);   // เขียน 402
        // ส่งออก/ยื่นแบบผ่าน
        Assert.False(SubscriptionGatePolicy.Decide(Facts(gate, isWrite: true, planActive: false, path: $"/api/companies/{Cid}/import-export/export")).Blocks);
    }

    [Fact]
    public void GateStatus_ทิศตรงข้าม_แถวบริษัทไม่ผูกLicenseถูกระงับ_ยัง403ทั้งอ่านและเขียน()
    {
        var id = PlanIdentityResolver.Resolve(SubscriptionPlan.Pro, SubscriptionStatus.Suspended, End, "Pro", null);
        var gate = PlanIdentityResolver.GateStatus(id);
        Assert.Equal(SubscriptionStatus.Suspended, gate);
        Assert.Equal(SubscriptionGateReason.SubscriptionInactive, SubscriptionGatePolicy.Decide(Facts(gate, isWrite: false, planActive: false)).Block);
        Assert.Equal(SubscriptionGateReason.SubscriptionInactive,
            SubscriptionGatePolicy.Decide(Facts(gate, isWrite: true, planActive: false, path: $"/api/companies/{Cid}/tax/e-filing/PP.30")).Block);
        // ยกเลิกแบบไม่ผูก ก็เหมือนเดิม
        Assert.Equal(SubscriptionStatus.Cancelled,
            PlanIdentityResolver.GateStatus(PlanIdentityResolver.Resolve(SubscriptionPlan.Pro, SubscriptionStatus.Cancelled, End, "Pro", null)));
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Expired)]
    [InlineData(SubscriptionStatus.Trial)]
    public void GateStatus_Licenseสถานะอื่น_ส่งต่อตามจริง(SubscriptionStatus s)
        => Assert.Equal(s, PlanIdentityResolver.GateStatus(ViaLicense(s)));

    // ════════ Decide: ผลลัพธ์เชิงกฎหมายของบริษัทหมดอายุ ════════

    [Theory]
    [InlineData("/api/companies/" + Cid + "/tax/e-filing/PP.30")]
    [InlineData("/api/companies/" + Cid + "/tax/e-filing/PND.53")]
    [InlineData("/api/companies/" + Cid + "/Tax/e-filing/PND.1/")]
    [InlineData("/api/companies/" + Cid + "/import-export/export")]
    [InlineData("/api/companies/" + Cid + "/reports/22222222-2222-2222-2222-222222222222/export/xlsx")]
    public void Decide_หมดอายุ_สร้างไฟล์ยื่นแบบและส่งออกผ่าน(string path)
    {
        Assert.True(SubscriptionGatePolicy.IsComplianceOutputPath(path, "POST"));
        var v = SubscriptionGatePolicy.Decide(Facts(SubscriptionStatus.Expired, isWrite: true, planActive: false, path: path));
        Assert.False(v.Blocks);
        Assert.Empty(v.LogOnly);
    }

    [Theory]
    [InlineData("/api/companies/" + Cid + "/documents")]
    [InlineData("/api/companies/" + Cid + "/tax/" + "33333333-3333-3333-3333-333333333333" + "/file")]
    [InlineData("/api/companies/" + Cid + "/tax/e-filing/PP.30/extra")]
    [InlineData("/api/companies/" + Cid + "/import-export/import")]
    [InlineData("/api/companies/" + Cid + "/journal-entries/export-something/post")]
    public void Decide_ทิศตรงข้าม_POSTอื่นยัง402(string path)
    {
        Assert.False(SubscriptionGatePolicy.IsComplianceOutputPath(path, "POST"));
        Assert.Equal(SubscriptionGateReason.PlanExpired,
            SubscriptionGatePolicy.Decide(Facts(SubscriptionStatus.Expired, isWrite: true, planActive: false, path: path)).Block);
    }

    [Fact]
    public void Decide_ยื่นแบบของบริษัทที่ถูกระงับ_ยังบล็อก_และเมธอดอื่นไม่ใช่ผลลัพธ์เชิงกฎหมาย()
    {
        var path = $"/api/companies/{Cid}/tax/e-filing/PP.30";
        Assert.Equal(SubscriptionGateReason.CompanySuspended,
            SubscriptionGatePolicy.Decide(Facts(SubscriptionStatus.Expired, isWrite: true, planActive: false, companySuspended: true, path: path)).Block);
        Assert.False(SubscriptionGatePolicy.IsComplianceOutputPath(path, "PUT"));
        Assert.False(SubscriptionGatePolicy.IsComplianceOutputPath(path, "DELETE"));
        Assert.False(SubscriptionGatePolicy.IsComplianceOutputPath(null, "POST"));
        // บริษัทที่ยังใช้งานได้ ไม่มีอะไรเปลี่ยน
        Assert.False(SubscriptionGatePolicy.Decide(Facts(SubscriptionStatus.Active, isWrite: true, planActive: true, path: $"/api/companies/{Cid}/documents")).Blocks);
    }

    // ════════ พรีวิวถอด · คำเตือนผูก · ข้อความแจ้ง ════════

    [Fact]
    public void DetachPreview_แถวProจ่ายเองยังไม่หมด_กลับมาใช้ถึงวันเดิม()
    {
        var now = new DateTime(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
        var after = PlanIdentityResolver.Resolve(SubscriptionPlan.Pro, SubscriptionStatus.Active, new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc), "Pro", null);
        var p = PlanIdentityResolver.DetachPreview(after, companyIsPermanentFree: false, now);
        Assert.True(p.Resumes);
        Assert.True(p.CanBuyNow);
        Assert.Contains("หลังถอดจะใช้ Pro", p.Text);
        Assert.Contains("2570", p.Text);   // พ.ศ.
        Assert.NotNull(p.UsableUntilUtc);
    }

    [Fact]
    public void DetachPreview_แถวหมดอายุ_บอกตรงๆว่าหมดอายุ_ซื้อได้ทันที()
    {
        var now = new DateTime(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
        var after = PlanIdentityResolver.Resolve(SubscriptionPlan.Pro, SubscriptionStatus.Expired, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), "Pro", null);
        var p = PlanIdentityResolver.DetachPreview(after, false, now);
        Assert.False(p.Resumes);
        Assert.True(p.CanBuyNow);
        Assert.Contains("ซื้อ", p.Text);
        Assert.Contains("หมดอายุแล้ว", p.Text);
        Assert.Null(p.UsableUntilUtc);
    }

    // ════════ ฝ่ายค้าน S1: partner header + LogOnly ════════

    private static SubscriptionGateFacts HeaderFacts(bool isWrite, bool? planActive, bool inactiveReadOnly, SubscriptionWriteGateMode mode,
        string? path = null)
        => new SubscriptionGateFacts(SubscriptionStatus.Expired, FeatureFlags.ProFeatures, isWrite, mode, false, planActive, null) with
        {
            InactiveReadOnly = inactiveReadOnly,
            IsComplianceOutput = SubscriptionGatePolicy.IsComplianceOutputPath(path, isWrite ? "POST" : "GET"),
        };

    [Theory]
    [InlineData(SubscriptionWriteGateMode.LogOnly)]
    [InlineData(SubscriptionWriteGateMode.Off)]
    [InlineData(SubscriptionWriteGateMode.Enforce)]
    public void S1_Licenseระงับ_ทุกโหมด_เขียนไม่ได้_อ่านได้_ยื่นแบบได้(SubscriptionWriteGateMode mode)
    {
        Assert.True(PlanIdentityResolver.LicenseInactiveReadOnly(ViaLicense(SubscriptionStatus.Suspended)));
        // เดิม (ก่อนรอบ 238) 403 ไม่ขึ้นกับโหมด ⇒ หลังแปลงเป็นหมดอายุต้องไม่หลวมเป็น log อย่างเดียว
        Assert.Equal(SubscriptionGateReason.PlanExpired, SubscriptionGatePolicy.Decide(HeaderFacts(true, planActive: false, true, mode)).Block);
        // GetEffectivePlanAsync อ่านไม่ได้ (null) ก็ยังบล็อก — ไม่ fail-open
        Assert.Equal(SubscriptionGateReason.PlanExpired, SubscriptionGatePolicy.Decide(HeaderFacts(true, planActive: null, true, mode)).Block);
        Assert.False(SubscriptionGatePolicy.Decide(HeaderFacts(false, planActive: false, true, mode)).Blocks);
        Assert.False(SubscriptionGatePolicy.Decide(HeaderFacts(true, false, true, mode, path: $"/api/companies/{Cid}/tax/e-filing/PP.30")).Blocks);
    }

    [Fact]
    public void S1_ทิศตรงข้าม_headerLogOnly_หมดอายุธรรมดา_ยังเป็นlogอย่างเดียวตามเดิม()
    {
        Assert.False(PlanIdentityResolver.LicenseInactiveReadOnly(ViaLicense(SubscriptionStatus.Expired)));
        Assert.False(PlanIdentityResolver.LicenseInactiveReadOnly(
            PlanIdentityResolver.Resolve(SubscriptionPlan.Pro, SubscriptionStatus.Suspended, End, "Pro", null)));
        var v = SubscriptionGatePolicy.Decide(HeaderFacts(true, planActive: false, false, SubscriptionWriteGateMode.LogOnly));
        Assert.False(v.Blocks);
        Assert.Contains(SubscriptionGateReason.PlanExpired, v.LogOnly);
        Assert.False(SubscriptionGatePolicy.Decide(HeaderFacts(true, planActive: null, false, SubscriptionWriteGateMode.Enforce)).Blocks);   // fail-open เดิมของหมดอายุธรรมดา
    }

    // ════════ ฝ่ายค้าน B2 / S2 / N1 ════════

    [Theory]
    [InlineData(SubscriptionStatus.Active, -5.0, false, SubscriptionStatus.Expired)]     // เลยวันหมดอายุ ⇒ Expired (ไม่ปล่อยให้งานเดินถึง Suspended)
    [InlineData(SubscriptionStatus.PastDue, -40.0, false, SubscriptionStatus.Expired)]
    [InlineData(SubscriptionStatus.Trial, -1.0, false, SubscriptionStatus.Expired)]
    [InlineData(SubscriptionStatus.Active, 30.0, false, SubscriptionStatus.Active)]      // ทิศตรงข้าม: ยังไม่หมด ⇒ คงเดิม
    [InlineData(SubscriptionStatus.Active, -5.0, true, SubscriptionStatus.Active)]       // ฟรีถาวร ⇒ คงเดิม
    [InlineData(SubscriptionStatus.Suspended, -5.0, false, SubscriptionStatus.Suspended)]
    [InlineData(SubscriptionStatus.Expired, -5.0, false, SubscriptionStatus.Expired)]
    public void B2_CompanyStatusAfterDetach(SubscriptionStatus s, double endOffsetDays, bool permanentFree, SubscriptionStatus expected)
        => Assert.Equal(expected, PlanIdentityResolver.CompanyStatusAfterDetach(s, End.AddDays(endOffsetDays), permanentFree, End));

    [Fact]
    public void B2_พรีวิวแถวActiveที่เลยวันหมดอายุ_บอกซื้อได้ทันที()
    {
        var after = PlanIdentityResolver.Resolve(SubscriptionPlan.Pro,
            PlanIdentityResolver.CompanyStatusAfterDetach(SubscriptionStatus.Active, End.AddDays(-5), false, End), End.AddDays(-5), "Pro", null);
        var p = PlanIdentityResolver.DetachPreview(after, false, End);
        Assert.False(p.Resumes);
        Assert.Contains("ซื้อ", p.Text);
    }

    [Fact]
    public void S2_ผูกในช่วงผ่อนผัน_เตือน_และไม่ผูกอัตโนมัติ()
    {
        Assert.Null(PlanIdentityResolver.AttachLicenseGraceWarning(End, 7, End.AddDays(-1)));
        var w = PlanIdentityResolver.AttachLicenseGraceWarning(End, 7, End.AddDays(2));
        Assert.Contains("ช่วงผ่อนผันถึง 17/11/2569", w);
        Assert.True(PlanIdentityResolver.MayAutoAttach(SubscriptionStatus.Active, End, End.AddDays(-1)));
        Assert.False(PlanIdentityResolver.MayAutoAttach(SubscriptionStatus.PastDue, End, End.AddDays(1)));
        Assert.False(PlanIdentityResolver.MayAutoAttach(SubscriptionStatus.Expired, End.AddDays(10), End));
    }

    [Fact]
    public void N1_ไฟล์ยื่นแบบซ้ำเหมือนเดิมภายใน10นาที_ใช้แถวเดิม_เนื้อต่างหรือนานกว่า_แถวใหม่()
    {
        Assert.True(EFilingExportDedupe.ReuseExisting("A|B", End, "A|B", End.AddMinutes(5)));
        Assert.False(EFilingExportDedupe.ReuseExisting("A|B", End, "A|C", End.AddMinutes(1)));
        Assert.False(EFilingExportDedupe.ReuseExisting("A|B", End, "A|B", End.AddMinutes(11)));
        Assert.False(EFilingExportDedupe.ReuseExisting(null, End, "A|B", End));
    }

    [Fact]
    public void LicenseLapseNote_ในผ่อนผัน_พ้นผ่อนผัน_ระงับ_และทิศตรงข้ามยังไม่หมดอายุไม่มีข้อความ()
    {
        var inGrace = PlanIdentityResolver.LicenseLapseNote(ViaLicense(SubscriptionStatus.PastDue), End.AddDays(2));
        Assert.Contains("ใช้งานได้เต็มถึงสิ้นระยะผ่อนผัน 17/11/2569", inGrace);
        var lapsed = PlanIdentityResolver.LicenseLapseNote(ViaLicense(SubscriptionStatus.Expired), End.AddDays(9));
        Assert.Contains("อ่านอย่างเดียว", lapsed);
        Assert.Contains("ยื่นแบบ", lapsed);
        Assert.Contains("ถูกระงับ", PlanIdentityResolver.LicenseLapseNote(ViaLicense(SubscriptionStatus.Suspended), End.AddDays(-30)));
        Assert.Null(PlanIdentityResolver.LicenseLapseNote(ViaLicense(SubscriptionStatus.Active), End.AddDays(-1)));
        Assert.Null(PlanIdentityResolver.LicenseLapseNote(
            PlanIdentityResolver.Resolve(SubscriptionPlan.Pro, SubscriptionStatus.Expired, End, "Pro", null), End.AddDays(9)));
    }

    [Fact]
    public void MayDetach_เจ้าของหรือผู้ถือเท่านั้น_และต้องผูกอยู่()
    {
        Assert.True(PlanIdentityResolver.MayDetach(true, isCompanyOwner: true, isLicenseHolder: false));
        Assert.True(PlanIdentityResolver.MayDetach(true, isCompanyOwner: false, isLicenseHolder: true));
        Assert.False(PlanIdentityResolver.MayDetach(true, false, false));
        Assert.False(PlanIdentityResolver.MayDetach(false, true, true));
    }

    [Fact]
    public void AttachPaidTimeWarning_จ่ายเองเหลือเวลา_เตือน_ทดลองหรือฟรีหรือหมดแล้ว_ไม่เตือน()
    {
        var now = new DateTime(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
        var w = PlanIdentityResolver.AttachPaidTimeWarning(SubscriptionKind.Paid, false, SubscriptionStatus.Active, now.AddDays(45), null, now);
        Assert.NotNull(w);
        Assert.Contains("45 วันยังเดินอยู่ ไม่คืนเงิน", w);
        // แถวเก่า Kind=Unknown ที่มีวันชำระ = จ่ายเงิน
        Assert.NotNull(PlanIdentityResolver.AttachPaidTimeWarning(SubscriptionKind.Unknown, false, SubscriptionStatus.Active, now.AddDays(10), now.AddDays(-20), now));
        Assert.Null(PlanIdentityResolver.AttachPaidTimeWarning(SubscriptionKind.Trial, false, SubscriptionStatus.Trial, now.AddDays(10), null, now));
        Assert.Null(PlanIdentityResolver.AttachPaidTimeWarning(SubscriptionKind.Free, true, SubscriptionStatus.Active, now.AddYears(100), null, now));
        Assert.Null(PlanIdentityResolver.AttachPaidTimeWarning(SubscriptionKind.Paid, false, SubscriptionStatus.Expired, now.AddDays(-1), null, now));
        Assert.Null(PlanIdentityResolver.AttachPaidTimeWarning(SubscriptionKind.Unknown, false, SubscriptionStatus.Active, now.AddDays(10), null, now));
    }

    [Fact]
    public void LicenseLapseCompanyNotice_บอกวันสิ้นผ่อนผันพศ_อ่านส่งออกยื่นแบบได้เสมอ_และทางถอด()
    {
        var (subject, lines) = PlanIdentityResolver.LicenseLapseCompanyNotice("บริษัท ก", "Enterprise", "สมฤทธิ์", End, 7, expiryDay: true, daysLeft: 0);
        var all = string.Join("\n", lines);
        Assert.Contains("17/11/2569", subject);   // สิ้นผ่อนผัน (End 10/11/2569 +7 วัน · เวลาไทย)
        Assert.Contains("ส่งออก", all);
        Assert.Contains("ยื่นแบบ", all);
        Assert.Contains("ถอดบริษัทออกจาก License", all);
        Assert.Contains(PlanIdentityResolver.DetachPageUrl, all);
        var (s7, _) = PlanIdentityResolver.LicenseLapseCompanyNotice("บริษัท ก", "Enterprise", "สมฤทธิ์", End, 7, expiryDay: false, daysLeft: 7);
        Assert.Contains("7 วัน", s7);
    }
}
