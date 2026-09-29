using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลการหาฟีเจอร์ของ subscription ช่วงทดลอง</summary>
/// <param name="Features">ฟีเจอร์ที่ใช้ตัดสิน</param>
/// <param name="FromTemplate">ค่ามาจากข้อมูลแพ็กเกจ (PlanTemplate) เพราะแถว subscription ว่าง</param>
/// <param name="StillEmpty">ทั้งแถว subscription และข้อมูลแพ็กเกจว่าง — ลูกค้ารายนี้จะถูกบล็อกทุกเส้นทางที่ gate เมื่อเปิดบังคับ
/// (รายงานแอดมินต้องแสดง · ระบบไม่แต่งชุดฟีเจอร์ขึ้นเอง)</param>
public sealed record TrialFeatureResolution(FeatureFlags Features, bool FromTemplate, bool StillEmpty);

/// <summary>ความพร้อมของแพ็กเกจหนึ่งสำหรับลูกค้าช่วงทดลอง/แพ็กเกจฟรี เมื่อเปิดบังคับ</summary>
/// <param name="FeatureSource">ชุดที่ลูกค้ากลุ่มนี้ได้: <c>TrialFeatures</c> (ช่วงทดลอง) หรือ <c>EnabledFeatures</c> (แพ็กเกจฟรีถาวร)</param>
/// <param name="Features">ชื่อฟีเจอร์ที่ได้ (ตามข้อมูลแพ็กเกจที่แอดมินตั้ง)</param>
/// <param name="GatedFeaturesMissing">ฟีเจอร์ที่มีเส้นทางถูก gate แต่ไม่อยู่ในชุด = ถูกบล็อกเมื่อเปิดบังคับ (ชื่อ enum)</param>
/// <param name="GatedRoutesBlocked">คีย์เส้นทางที่จะถูกบล็อก (จาก <see cref="SubscriptionGatePolicy.RouteFeatureMap"/>)</param>
/// <param name="Problem">ข้อความเมื่อข้อมูลแพ็กเกจไม่พร้อม (ว่าง) · <c>null</c> = ตั้งไว้แล้ว</param>
public sealed record PlanTrialReadinessRow(
    string Plan, string Name, bool IsPermanentFree, string FeatureSource,
    List<string> Features, List<string> GatedFeaturesMissing, List<string> GatedRoutesBlocked, string? Problem);

/// <summary>ผลนับหนึ่งแถวของรายงานเงา (ข้อมูลเข้าของ <see cref="SubscriptionTrialReadiness.SummarizeTrialBlocks"/>)</summary>
public sealed record SubscriptionShadowTally(
    Guid CompanyId, string Reason, string? Feature, string? Plan, string? SubscriptionStatus, long HitCount, long BlockedCount);

/// <summary>สรุป "ลูกค้าทดลองใช้ (จะ) ถูกบล็อกกี่ครั้ง เพราะอะไร"</summary>
public sealed record TrialBlockSummary(
    string Reason, string? Feature, string Description, int Companies, long WouldBlockHits, long BlockedHits);

/// <summary>
/// <b>ลูกค้าช่วงทดลอง/แพ็กเกจฟรี ได้ฟีเจอร์อะไรเมื่อเปิดบังคับแพ็กเกจ</b> — รอบ 200 คำตัดสินข้อ 14
/// "FreeTrial ระหว่างทดลอง = ใช้ฟีเจอร์ตามแพ็กเกจที่ระบุไว้ในข้อมูลแพ็กเกจ (ตรวจว่าตั้งไว้ครบ ไม่ใช่ว่าง)"
///
/// <para>═══ ที่มา ═══ ฟีเจอร์ที่ gate ตัดสินอ่านจาก <c>Subscription.EnabledFeatures</c> (สำเนาจากแพ็กเกจตอนสมัคร + ตอนแอดมินแก้แพ็กเกจ) ·
/// คอลัมน์นี้มี <c>DEFAULT 0</c> และแพ็กเกจที่แอดมินบันทึกรายการฟีเจอร์ว่างได้ <c>None</c> ⇒ ลูกค้ารายนั้นถูกบล็อก<b>ทุก</b>เส้นทางที่ gate
/// (รวม <c>/tax</c> รายงานภาษีซื้อขาย) ทันทีที่เปิดบังคับ โดยไม่มีอะไรบอกล่วงหน้า</para>
///
/// <para>กติกา: สำเนาว่าง ⇒ อ่านจากข้อมูลแพ็กเกจ (ช่วงทดลอง = <c>TrialFeatures</c> · อื่น = <c>EnabledFeatures</c>) · แพ็กเกจก็ว่าง ⇒ คงว่าง
/// แต่รายงานแอดมินแสดง (ห้ามแต่งชุดฟีเจอร์ขึ้นเอง — DOCTRINE §1 "ไม่รู้ต้องบอกว่าไม่รู้") · สำเนาที่ไม่ว่าง = ค่าที่ตั้งรายบริษัท ไม่แตะ</para>
/// </summary>
public static class SubscriptionTrialReadiness
{
    /// <summary>ลูกค้า "ทดลองใช้" ในรายงาน = สถานะ Trial (แพ็กเกจใดก็ได้) หรือแพ็กเกจ FreeTrial (รวมแพ็กเกจฟรีถาวร)</summary>
    public static bool IsTrialLike(SubscriptionPlan plan, SubscriptionStatus status) =>
        status == SubscriptionStatus.Trial || plan == SubscriptionPlan.FreeTrial;

    /// <summary>รุ่นชื่อ (จากแถวรายงานที่เก็บเป็นชื่อ enum) — ค่าว่าง/ไม่รู้จัก = ไม่ใช่</summary>
    public static bool IsTrialLike(string? plan, string? status) =>
        string.Equals(status, nameof(SubscriptionStatus.Trial), StringComparison.OrdinalIgnoreCase)
        || string.Equals(plan, nameof(SubscriptionPlan.FreeTrial), StringComparison.OrdinalIgnoreCase);

    /// <summary>ฟีเจอร์ที่ใช้ตัดสิน — สำเนาบนแถว subscription ชนะ · ว่างและเป็นลูกค้าทดลอง ⇒ ข้อมูลแพ็กเกจ · แพ็กเกจว่าง ⇒ ว่าง + <c>StillEmpty</c></summary>
    /// <param name="templateTrial"><c>PlanTemplate.TrialFeatures</c> · <c>null</c> = ไม่มีแพ็กเกจที่เปิดใช้</param>
    /// <param name="templateEnabled"><c>PlanTemplate.EnabledFeatures</c></param>
    public static TrialFeatureResolution ResolveFeatures(FeatureFlags snapshot, SubscriptionPlan plan, SubscriptionStatus status,
        FeatureFlags? templateTrial, FeatureFlags? templateEnabled)
    {
        if (snapshot != FeatureFlags.None || !IsTrialLike(plan, status))
            return new TrialFeatureResolution(snapshot, false, false);
        var fromTemplate = status == SubscriptionStatus.Trial ? templateTrial : templateEnabled;
        return fromTemplate is FeatureFlags f && f != FeatureFlags.None
            ? new TrialFeatureResolution(f, true, false)
            : new TrialFeatureResolution(FeatureFlags.None, false, true);
    }

    /// <summary>ตรวจแพ็กเกจหนึ่ง: ลูกค้าทดลอง/ฟรีของแพ็กเกจนี้ได้อะไร และจะถูกบล็อกเส้นทางไหนเมื่อเปิดบังคับ — ใช้ตัวตัดสิน
    /// <see cref="SubscriptionGatePolicy.Decide"/> ตัวเดียวกับ middleware ด้วยคำขออ่านสมมุติ (ไม่เขียนเงื่อนไขฟีเจอร์ซ้ำ)</summary>
    public static PlanTrialReadinessRow CheckTemplate(SubscriptionPlan plan, string name, bool isPermanentFree,
        FeatureFlags enabledFeatures, FeatureFlags trialFeatures)
    {
        var source = isPermanentFree ? "EnabledFeatures" : "TrialFeatures";
        var features = isPermanentFree ? enabledFeatures : trialFeatures;
        var missing = new List<string>();
        var routes = new List<string>();
        foreach (var f in SubscriptionGatePolicy.GatedFeatures)
        {
            var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(
                SubscriptionStatus.Trial, features, IsWrite: false, SubscriptionWriteGateMode.Off, null, null, f));
            if (v.Block != SubscriptionGateReason.FeatureNotInPlan) continue;
            missing.Add(f.ToString());
            routes.AddRange(SubscriptionGatePolicy.RouteFeatureMap.Where(m => m.Feature == f).Select(m => m.Path));
        }
        var problem = features == FeatureFlags.None
            ? (isPermanentFree
                ? "แพ็กเกจฟรีนี้ไม่ได้ตั้งฟีเจอร์ (EnabledFeatures ว่าง)"
                : "แพ็กเกจนี้ไม่ได้ตั้งฟีเจอร์ช่วงทดลอง (TrialFeatures ว่าง)")
              + " — ลูกค้าจะถูกบล็อกทุกเส้นทางที่ gate (รวมรายงานภาษี) ทันทีที่เปิดบังคับ · ตั้งฟีเจอร์ที่หน้าแพ็กเกจก่อนเปิดบังคับ"
            : null;
        return new PlanTrialReadinessRow(plan.ToString(), name, isPermanentFree, source,
            FeatureCatalog.NamesOf(features), missing, routes.Distinct().ToList(), problem);
    }

    /// <summary>สรุปรายงานเงาเฉพาะลูกค้าทดลองใช้ (<see cref="IsTrialLike(string?, string?)"/>): เหตุ × ฟีเจอร์ → กี่บริษัท · จะถูกบล็อกกี่ครั้ง ·
    /// ถูกบล็อกจริงกี่ครั้ง · เรียงจากจำนวนบริษัทมากไปน้อย</summary>
    public static IReadOnlyList<TrialBlockSummary> SummarizeTrialBlocks(IEnumerable<SubscriptionShadowTally> rows) =>
        rows.Where(r => IsTrialLike(r.Plan, r.SubscriptionStatus))
            .GroupBy(r => (r.Reason, Feature: string.IsNullOrEmpty(r.Feature) ? null : r.Feature))
            .Select(g =>
            {
                Enum.TryParse<SubscriptionGateReason>(g.Key.Reason, out var reason);
                return new TrialBlockSummary(g.Key.Reason, g.Key.Feature,
                    SubscriptionGatePolicy.Describe(reason, g.Key.Feature, "ทดลองใช้"),
                    g.Select(r => r.CompanyId).Distinct().Count(), g.Sum(r => r.HitCount), g.Sum(r => r.BlockedCount));
            })
            .OrderByDescending(s => s.Companies).ThenByDescending(s => s.WouldBlockHits + s.BlockedHits)
            .ThenBy(s => s.Reason, StringComparer.Ordinal).ThenBy(s => s.Feature, StringComparer.Ordinal)
            .ToList();
}
