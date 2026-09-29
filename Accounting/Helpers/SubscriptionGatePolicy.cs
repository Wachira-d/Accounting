using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>คำขอนี้ต้องทำอะไรกับผลตัดสินแพ็กเกจ</summary>
public enum SubscriptionGateAction
{
    /// <summary>ไม่ตรวจเลย (ไม่รู้บริษัท · หรือสวิตช์เว็บ = Off)</summary>
    Skip = 0,
    /// <summary>ตัดสินแต่ไม่บล็อก — บันทึกผลลงรายงานแอดมินแล้วปล่อยผ่าน</summary>
    Shadow = 1,
    /// <summary>บล็อกจริง (403/402)</summary>
    Enforce = 2,
}

/// <summary>โหมดของด่านเขียน (WP-A1/A2: เขียนหลังหมดอายุ · บริษัทถูกระงับ) — รอบ 200 ข้อ 14: <b>ไม่ได้อ่านจาก config แล้ว</b>
/// ได้จาก <see cref="SubscriptionGatePolicy.WriteGateModeFor"/> ตามโหมดที่มีผลจริง (<see cref="SubscriptionEnforcementResolver"/>)</summary>
public enum SubscriptionWriteGateMode
{
    Off = 0,
    LogOnly = 1,
    Enforce = 2,
}

/// <summary>เหตุที่คำขอถูก (หรือจะถูก) ปฏิเสธ — ชื่อ enum เป็นคีย์ในตาราง <c>SubscriptionGateShadowHits.Reason</c></summary>
public enum SubscriptionGateReason
{
    None = 0,
    /// <summary>Subscription = Cancelled/Suspended → 403 ทั้งอ่านและเขียน</summary>
    SubscriptionInactive = 1,
    /// <summary>บริษัทถูกแอดมินระงับ (<c>Company.Status = Suspended</c>) → 403 เฉพาะเขียน</summary>
    CompanySuspended = 2,
    /// <summary>หมดอายุเกินช่วงผ่อนผัน → 402 เฉพาะเขียน (อ่านได้)</summary>
    PlanExpired = 3,
    /// <summary>เส้นทางต้องใช้ฟีเจอร์ที่ไม่อยู่ในแพ็กเกจ → 403 ทั้งอ่านและเขียน</summary>
    FeatureNotInPlan = 4,
}

/// <summary>ข้อเท็จจริงที่ middleware โหลดมาให้ตัดสิน</summary>
/// <param name="CompanySuspended"><c>null</c> = ไม่ได้โหลด (ไม่ใช่คำขอเขียน / WriteMode = Off / อ่านไม่ได้)</param>
/// <param name="PlanActive">ผลของ <c>GetEffectivePlanAsync().IsActive</c> · <c>null</c> = ไม่ได้โหลด/อ่านไม่ได้ (fail-open)</param>
/// <param name="RequiredFeature">ผลของ <see cref="SubscriptionGatePolicy.RequiredFeatureFor"/> · <c>null</c> = เส้นทางไม่ต้องใช้ฟีเจอร์</param>
public sealed record SubscriptionGateFacts(
    SubscriptionStatus Status,
    FeatureFlags EnabledFeatures,
    bool IsWrite,
    SubscriptionWriteGateMode WriteMode,
    bool? CompanySuspended,
    bool? PlanActive,
    FeatureFlags? RequiredFeature);

/// <summary>ผลตัดสิน</summary>
/// <param name="Block">เหตุที่ต้องปฏิเสธ (<see cref="SubscriptionGateReason.None"/> = ผ่าน)</param>
/// <param name="Feature">ฟีเจอร์ที่ขาด (เฉพาะ <see cref="SubscriptionGateReason.FeatureNotInPlan"/>)</param>
/// <param name="LogOnly">เหตุที่ "จะบล็อกถ้า WriteMode = Enforce" แต่ config ตั้งเป็น LogOnly — ผ่านได้ แต่ต้อง log/บันทึก</param>
public sealed record SubscriptionGateVerdict(
    SubscriptionGateReason Block,
    FeatureFlags? Feature,
    IReadOnlyList<SubscriptionGateReason> LogOnly)
{
    public bool Blocks => Block != SubscriptionGateReason.None;
}

/// <summary>
/// <b>ตัวตัดสิน gate แพ็กเกจ/ระงับบริษัทตัวเดียว</b> ของ <c>SubscriptionCheckMiddleware</c> — รอบ 198 ข้อ 5
///
/// <para>═══ ที่มา ═══ middleware เคยหาบริษัทจาก <c>X-Company-Id</c> อย่างเดียวแล้ว "ข้ามทั้งหมด" เมื่อไม่มี ⇒ หน้าเว็บ
/// (api.js ไม่ส่ง header · เส้นทางเป็น <c>/api/companies/{companyId}/…</c>) ไม่เคยถูก gate แพ็กเกจและไม่เคยถูกบล็อกเมื่อ
/// บริษัทถูกระงับ. เจ้าของตัดสิน (DECISIONS.md ข้อ 5): <b>รายงานก่อน แล้วค่อยเปิดบังคับ</b> ⇒ คำขอที่รู้บริษัทจาก route
/// เดินตามสวิตช์แพลตฟอร์ม <c>SiteSettings.SubscriptionEnforcementMode</c> (ค่าตั้งต้น Shadow) ส่วนคำขอที่ส่ง header มาเอง
/// (ถูกบังคับอยู่แล้วก่อนรอบนี้) <b>บังคับเหมือนเดิมเสมอ ห้ามหลวมลง</b></para>
///
/// <para>ตารางเส้นทาง→ฟีเจอร์ (<see cref="RouteFeatureMap"/>) ย้ายมาจาก middleware เพื่อให้รายงานแอดมิน
/// (ตรวจล่วงหน้าว่าบริษัทไหนขาดฟีเจอร์ที่ถูก gate) ใช้ตารางชุดเดียวกับตัวบล็อกจริง — ห้ามมีสำเนาที่สอง</para>
/// </summary>
public static class SubscriptionGatePolicy
{
    /// <summary>
    /// เส้นทางที่ <b>ห้าม gate ตามแพ็กเกจ</b> แม้จะตรงกับ <see cref="RouteFeatureMap"/> — เพราะเป็นข้อบังคับตามกฎหมาย
    /// ไม่ใช่ฟีเจอร์เสริมที่ขายเพิ่มได้
    ///
    /// <para><c>/dimensions/branches</c> — สาขาผูกกับ <b>รหัสสาขาสรรพากร</b> ที่ §86/4 บังคับให้ปรากฏบนใบกำกับภาษี
    /// (ประกาศอธิบดีฯ ฉบับที่ 199) และ §87 บังคับแยกรายงานภาษีซื้อ/ขายต่อสถานประกอบการ. กิจการที่มี 2 สาขาแล้วอยู่
    /// แพ็กเกจเล็กจะออกเอกสารให้ถูกกฎหมายไม่ได้เลยถ้าโดน gate — ของที่ขายเพิ่มได้คือ "มิติ/ศูนย์ต้นทุน"
    /// (<c>/dimensions</c> เส้นทางอื่น) ไม่ใช่ทะเบียนสาขา</para>
    /// </summary>
    private static readonly string[] FeatureExemptRoutes =
    {
        "/dimensions/branches",
    };

    /// <summary>เส้นทาง → ฟีเจอร์ที่ต้องมี · จับคู่แบบ "ยึดท้าย segment" (มี <c>{key}/</c> หรือจบด้วย <c>{key}</c>) ·
    /// คีย์ยาวกว่าชนะ (เฉพาะเจาะจงกว่า)</summary>
    public static readonly IReadOnlyList<(string Path, FeatureFlags Feature)> RouteFeatureMap = new List<(string, FeatureFlags)>
    {
        // Reports - more specific first
        ("/reports/aging",            FeatureFlags.AgingReport),
        ("/reports/budget",           FeatureFlags.BudgetManagement),
        ("/reports/fpa",              FeatureFlags.FPA),
        ("/reports/arap",             FeatureFlags.AdvancedReporting),
        ("/reports/financial-mgmt",   FeatureFlags.AdvancedReporting),
        ("/executive-reports",        FeatureFlags.AdvancedReporting),

        // Operations
        ("/payroll",                  FeatureFlags.Payroll),
        ("/commission",               FeatureFlags.Commission),
        ("/fixed-assets",             FeatureFlags.FixedAssets),
        ("/recurring",                FeatureFlags.RecurringTransactions),
        ("/revenue-recognition",      FeatureFlags.RevenueRecognition),
        ("/loans",                    FeatureFlags.LoanManagement),
        ("/multi-currency",           FeatureFlags.MultiCurrency),
        ("/projects",                 FeatureFlags.ProjectAccounting),
        ("/time-billing",             FeatureFlags.TimeBilling),
        ("/dimensions",               FeatureFlags.CostCenter),
        ("/intercompany",             FeatureFlags.MultiCompany),
        ("/consolidation",            FeatureFlags.Consolidation),
        ("/warehouse",                FeatureFlags.WarehouseManagement),
        ("/inventory",                FeatureFlags.Inventory),
        ("/products",                 FeatureFlags.Inventory),
        ("/supplies",                 FeatureFlags.Inventory),
        ("/budget",                   FeatureFlags.BudgetManagement),
        ("/aging",                    FeatureFlags.AgingReport),

        // Banking
        ("/bank",                     FeatureFlags.BankReconciliation),
        // รอบ 200 ข้อ 14 (review198-D D-P3): รอบโอน wallet → ธนาคาร จับคู่เงินเข้าผ่าน IBankService.ReconcileAsync = งานกระทบยอดธนาคาร ⇒
        // feature key เดียวกับ /bank (เมนู settlements/settlement-channels ใน layout.js ผูก BankReconciliation คู่กัน) · "/pay/settlements"
        // ของ gateway ไม่ตรงคีย์นี้ (ส่วน "/settlements/" ≠ "/settlement/")
        ("/settlement",               FeatureFlags.BankReconciliation),

        // Tax / Documents — more specific eTax routes evaluated first (longest-key wins)
        ("/etax/send-email",          FeatureFlags.EtaxByEmail),
        ("/etax/by-email",            FeatureFlags.EtaxByEmail),
        ("/etax/sign-and-submit",     FeatureFlags.EtaxDirect),
        ("/etax/quick-submit",        FeatureFlags.EtaxDirect),
        ("/etax/submit",              FeatureFlags.EtaxDirect),
        ("/etax/sign",                FeatureFlags.EtaxDirect),
        ("/etax",                     FeatureFlags.EtaxInvoice),
        ("/tax",                      FeatureFlags.TaxManagement),
        ("/withholding-tax-certs",    FeatureFlags.TaxManagement),

        // Workflow / Approval
        ("/approval",                 FeatureFlags.ApprovalWorkflow),
        ("/signatures",               FeatureFlags.ApprovalWorkflow),

        // Audit
        ("/audit",                    FeatureFlags.AuditLog),

        // Integration / API
        ("/api-developer",            FeatureFlags.APIAccess),
        ("/integrations",             FeatureFlags.APIAccess),
        ("/webhooks",                 FeatureFlags.Webhook),
        ("/import-export",            FeatureFlags.BulkImport),

        // AI
        ("/ai-tools",                 FeatureFlags.AI_Features),
        ("/ai/",                      FeatureFlags.AI_Features),
        ("/ocr",                      FeatureFlags.DocumentOCR),

        // Customer portal
        ("/customer-portal",          FeatureFlags.CustomerPortal),

        // FPA
        ("/fpa",                      FeatureFlags.FPA),

        // CMS
        ("/cms/sites",                FeatureFlags.CmsWebsiteBuilder),
        ("/cms/themes",               FeatureFlags.CmsWebsiteBuilder),
        ("/cms/customers",            FeatureFlags.CmsWebsiteBuilder),
        ("/commerce",                 FeatureFlags.CmsEcommerce),
        ("/booking",                  FeatureFlags.CmsBooking),
    };

    /// <summary>ฟีเจอร์ทุกตัวที่มีเส้นทางถูก gate (ไม่ซ้ำ · เรียงตามชื่อ) — รายงานแอดมินใช้ตรวจล่วงหน้าว่าบริษัทไหนขาดอะไร</summary>
    public static IReadOnlyList<FeatureFlags> GatedFeatures { get; } = RouteFeatureMap
        .Select(m => m.Feature).Distinct().OrderBy(f => f.ToString(), StringComparer.Ordinal).ToList();

    /// <summary>คำขอนี้ต้องทำอะไร — คำขอที่ <b>ส่ง header มาเอง</b> บังคับเสมอ (พฤติกรรมก่อนรอบ 198 · ห้ามหลวม) ไม่ว่า
    /// สวิตช์เว็บจะเป็นอะไร · คำขอที่รู้บริษัทจาก route อย่างเดียวเดินตาม<b>โหมดที่มีผลจริง</b>
    /// (<see cref="SubscriptionEnforcementState.EffectiveMode"/> — สวิตช์แอดมิน หรือ override ฉุกเฉิน) · ค่าที่ไม่รู้จัก = Shadow
    /// (ไม่บล็อกใครเพราะค่าเพี้ยน แต่ยังบันทึกให้เห็น)</summary>
    public static SubscriptionGateAction ActionFor(TenantCompanyTarget target, SubscriptionEnforcementMode webMode)
    {
        if (target.CompanyId is null) return SubscriptionGateAction.Skip;
        if (target.HeaderCarried) return SubscriptionGateAction.Enforce;
        return webMode switch
        {
            SubscriptionEnforcementMode.Off => SubscriptionGateAction.Skip,
            SubscriptionEnforcementMode.Enforce => SubscriptionGateAction.Enforce,
            _ => SubscriptionGateAction.Shadow,
        };
    }

    /// <summary>
    /// โหมดของด่านเขียน (บริษัทถูกระงับ/หมดอายุ) สำหรับคำขอนี้ — รอบ 200 ข้อ 14 (แทนการอ่าน config <c>Subscription:Enforcement:Mode</c>
    /// ที่ขัดกับสวิตช์แอดมิน):
    /// <list type="bullet">
    /// <item>โหมดเงา ⇒ <b>Enforce สมมุติ</b> — รายงานเงาต้องบอกผลของ "ถ้ากดบังคับ" ครบทุกเหตุ (เดิมบันทึกเป็น "log อย่างเดียว" เพราะ config
    /// = LogOnly ⇒ แอดมินอ่านว่าไม่มีผล แล้วกด Enforce ก็ไม่มีผลจริง)</item>
    /// <item>หน้าเว็บที่บังคับ (โหมดที่มีผลจริง = Enforce) ⇒ Enforce</item>
    /// <item>คำขอที่ส่ง header มาเอง ⇒ <see cref="SubscriptionEnforcementState.HeaderWriteGateMode"/> (Enforce เมื่อโหมดที่มีผลจริง = Enforce ·
    /// ไม่งั้น LogOnly = ค่าเดิมของ appsettings — ไม่หลวมกว่าเดิม)</item>
    /// <item>ไม่ตรวจ ⇒ Off</item>
    /// </list>
    /// </summary>
    public static SubscriptionWriteGateMode WriteGateModeFor(SubscriptionGateAction action, TenantCompanyTarget target,
        SubscriptionEnforcementState enforcement) => action switch
    {
        SubscriptionGateAction.Skip => SubscriptionWriteGateMode.Off,
        SubscriptionGateAction.Shadow => SubscriptionWriteGateMode.Enforce,
        _ => target.HeaderCarried ? enforcement.HeaderWriteGateMode : SubscriptionWriteGateMode.Enforce,
    };

    /// <summary>ผลโหมดเงาที่ไม่ถูกพบซ้ำเกินกี่วันจะถูกตัดทิ้ง (ตารางไม่โตไม่จำกัด · DELETE ตามเวลา ปลอดภัยข้าม instance)</summary>
    public const int ShadowRetentionDays = 90;

    /// <summary>ความยาวสูงสุดของคีย์ endpoint ที่เก็บ</summary>
    public const int EndpointMaxLength = 200;

    /// <summary>
    /// คีย์ endpoint ของรายงานเงา — "endpoint ไหน" โดยไม่เก็บ id/PII และไม่ทำให้ตารางโตตามจำนวนเอกสาร: ใช้ route template ของ endpoint
    /// (<c>api/companies/{companyId:guid}/bank/accounts</c> ⇒ <c>api/companies/{companyId}/bank/accounts</c>) · ไม่มี template ⇒ path ที่แทน
    /// ส่วนที่เป็น GUID/ตัวเลขด้วย <c>{id}</c> · ตัดที่ <see cref="EndpointMaxLength"/>
    /// </summary>
    public static string EndpointKey(string? routeTemplate, string? path)
    {
        string key;
        if (!string.IsNullOrWhiteSpace(routeTemplate))
        {
            // ตัด constraint/ค่าตั้งต้นใน {name:guid} {name=x} {name?} ⇒ {name}
            key = System.Text.RegularExpressions.Regex.Replace(routeTemplate.Trim().TrimStart('/'),
                @"\{\*?([A-Za-z0-9_]+)[^}]*\}", "{$1}");
        }
        else
        {
            var segs = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(seg => Guid.TryParse(seg, out _) || seg.All(char.IsDigit) ? "{id}" : seg);
            key = string.Join('/', segs);
        }
        key = key.ToLowerInvariant();
        return key.Length > EndpointMaxLength ? key[..EndpointMaxLength] : key;
    }

    /// <summary>Subscription สถานะนี้ถูกปฏิเสธทุกคำขอ (อ่าน+เขียน) หรือไม่</summary>
    private static bool IsInactive(SubscriptionStatus status) =>
        status == SubscriptionStatus.Cancelled || status == SubscriptionStatus.Suspended;

    /// <summary>ต้องโหลดสถานะบริษัท/แผนที่มีผลไหม (ด่านเขียน WP-A1/A2) — ใช้เงื่อนไขเดียวกับ <see cref="Decide"/></summary>
    public static bool NeedsWriteFacts(SubscriptionStatus status, bool isWrite, SubscriptionWriteGateMode writeMode) =>
        !IsInactive(status) && isWrite && writeMode != SubscriptionWriteGateMode.Off;

    /// <summary>เส้นทางนี้ต้องใช้ฟีเจอร์อะไร (คีย์ยาวสุดที่ตรงชนะ) · <c>null</c> = ไม่ต้อง หรือเป็นเส้นทางที่กฎหมายบังคับ</summary>
    public static (FeatureFlags Feature, string RouteKey)? RequiredFeatureFor(string? path)
    {
        var lowerPath = (path ?? "").ToLowerInvariant();
        if (FeatureExemptRoutes.Any(r => lowerPath.Contains(r)))
            return null;
        foreach (var m in RouteFeatureMap.OrderByDescending(m => m.Path.Length))
        {
            var key = m.Path.ToLowerInvariant();
            if (lowerPath.Contains(key + "/") || lowerPath.EndsWith(key))
                return (m.Feature, m.Path);
        }
        return null;
    }

    /// <summary>ตัดสินตามลำดับเดิมของ middleware: Subscription ไม่ active → บริษัทถูกระงับ (เขียน) → หมดอายุเกินผ่อนผัน
    /// (เขียน) → ฟีเจอร์ไม่อยู่ในแพ็กเกจ · ด่านเขียนสองตัวทำงานเมื่อ WriteMode ≠ Off: Enforce = บล็อก · LogOnly = ผ่านแต่คืน
    /// เหตุใน <see cref="SubscriptionGateVerdict.LogOnly"/> · ข้อมูลที่อ่านไม่ได้ (<c>null</c>) = ไม่บล็อก (fail-open เดิม)</summary>
    public static SubscriptionGateVerdict Decide(SubscriptionGateFacts f)
    {
        var none = Array.Empty<SubscriptionGateReason>();
        if (IsInactive(f.Status))
            return new SubscriptionGateVerdict(SubscriptionGateReason.SubscriptionInactive, null, none);

        var logOnly = new List<SubscriptionGateReason>();
        if (f.WriteMode != SubscriptionWriteGateMode.Off && f.IsWrite)
        {
            if (f.CompanySuspended == true)
            {
                if (f.WriteMode == SubscriptionWriteGateMode.Enforce)
                    return new SubscriptionGateVerdict(SubscriptionGateReason.CompanySuspended, null, none);
                logOnly.Add(SubscriptionGateReason.CompanySuspended);
            }
            if (f.PlanActive == false)
            {
                if (f.WriteMode == SubscriptionWriteGateMode.Enforce)
                    return new SubscriptionGateVerdict(SubscriptionGateReason.PlanExpired, null, logOnly);
                logOnly.Add(SubscriptionGateReason.PlanExpired);
            }
        }

        if (f.RequiredFeature is FeatureFlags need && !f.EnabledFeatures.HasFlag(need))
            return new SubscriptionGateVerdict(SubscriptionGateReason.FeatureNotInPlan, need, logOnly);

        return new SubscriptionGateVerdict(SubscriptionGateReason.None, null, logOnly);
    }

    /// <summary>ด่านเขียนตัวไหนผ่านมาถึงขั้นหมดอายุแล้ว (ใช้ตัดสินว่าจะติด header <c>X-Subscription-Grace</c> ไหม — เดิม
    /// ติดเฉพาะเมื่อไม่ถูกบล็อกก่อนถึงขั้นนั้น)</summary>
    public static bool ReachedExpiryStep(SubscriptionGateVerdict v) =>
        v.Block is not SubscriptionGateReason.SubscriptionInactive and not SubscriptionGateReason.CompanySuspended;

    /// <summary>ข้อความภาษาไทยของเหตุ (รายงานแอดมิน)</summary>
    public static string Describe(SubscriptionGateReason reason, string? feature, string? plan) => reason switch
    {
        SubscriptionGateReason.SubscriptionInactive => "การสมัครสมาชิกถูกยกเลิก/ระงับ — จะถูกบล็อกทุกคำขอ (ทั้งดูและแก้ไข)",
        SubscriptionGateReason.CompanySuspended => "บริษัทถูกระงับการใช้งาน — จะถูกบล็อกเฉพาะการสร้าง/แก้ไข",
        SubscriptionGateReason.PlanExpired => "แพ็กเกจหมดอายุเกินช่วงผ่อนผัน — จะดูได้แต่สร้าง/แก้ไขไม่ได้",
        SubscriptionGateReason.FeatureNotInPlan =>
            $"ฟีเจอร์ \"{feature}\" ไม่อยู่ในแพ็กเกจ {(string.IsNullOrEmpty(plan) ? "ปัจจุบัน" : plan)} — จะถูกบล็อกทั้งดูและแก้ไข",
        _ => "-",
    };
}
