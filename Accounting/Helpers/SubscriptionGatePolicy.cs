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

/// <summary>เส้นทางนี้ต้องใช้ฟีเจอร์อะไร (ผลของ <see cref="SubscriptionGatePolicy.RequiredFeatureFor"/>)</summary>
/// <param name="Feature">ฟีเจอร์ตามตารางปัจจุบัน (คีย์ยาวสุดที่ตรงชนะ)</param>
/// <param name="RouteKey">คีย์ใน <see cref="SubscriptionGatePolicy.RouteFeatureMap"/> ที่ตรง</param>
/// <param name="NewlyGated">คีย์นี้เพิ่ง "เริ่มมีผล" รอบ 200 (<see cref="SubscriptionGatePolicy.NewlyGatedRouteKeys"/>) — ต้องผ่านโหมดเงาสำหรับ<b>ทุก</b>ผู้เรียก
/// (รวม partner ที่ส่ง header) จนกว่าเจ้าของกดบังคับ (คำตัดสินข้อ 22)</param>
/// <param name="LegacyFeature">ฟีเจอร์ที่คำขอเดียวกันเคยถูกตัดสินก่อนคีย์ใหม่มีผล (คีย์ที่ไม่ใช่คีย์ใหม่ที่ตรงยาวสุด) · <c>null</c> = เดิมไม่ถูก gate —
/// partner ยังถูกบังคับด้วยค่านี้ระหว่างโหมดเงา (ไม่หลวมลง)</param>
/// <param name="LegacyRouteKey">คีย์เดิมของ <paramref name="LegacyFeature"/></param>
public readonly record struct SubscriptionRouteRequirement(
    FeatureFlags Feature, string RouteKey, bool NewlyGated, FeatureFlags? LegacyFeature, string? LegacyRouteKey);

/// <summary>ฟีเจอร์ของคำขอนี้ต้องตัดสินแบบไหน (ผลของ <see cref="SubscriptionGatePolicy.FeaturePlanFor"/>)</summary>
/// <param name="Enforce">ฟีเจอร์ที่ส่งเข้า <see cref="SubscriptionGatePolicy.Decide"/> (บล็อกได้เมื่อการกระทำของคำขอ = บังคับ) · <c>null</c> = ไม่ตรวจฟีเจอร์</param>
/// <param name="EnforceRouteKey">คีย์ของ <paramref name="Enforce"/> (ลงรายงาน)</param>
/// <param name="ShadowOnly">ฟีเจอร์ที่ "ตัดสินแล้วบันทึกอย่างเดียว" แม้คำขอถูกบังคับ — คีย์ใหม่ของคำขอ partner ระหว่างโหมดที่มีผลจริงยังไม่ใช่บังคับ</param>
/// <param name="ShadowRouteKey">คีย์ของ <paramref name="ShadowOnly"/></param>
public readonly record struct SubscriptionFeaturePlan(
    FeatureFlags? Enforce, string? EnforceRouteKey, FeatureFlags? ShadowOnly, string? ShadowRouteKey);

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

    /// <summary>
    /// endpoint ที่ <b>ห้าม gate ตามแพ็กเกจ</b> เฉพาะ method นั้น + ต้องจบที่คีย์ตรงตัว (ไม่รวมเส้นทางลูก) — รอบ 200 คำตัดสินข้อ 21:
    /// "ผูกฟีเจอร์เฉพาะ endpoint ที่เป็นงานกระทบยอดจริง · การอ่านรายการบัญชีธนาคารเพื่อเลือกตอนรับ/จ่ายเงิน = งานพื้นฐาน ไม่ผูก"
    /// <list type="bullet">
    /// <item><c>GET …/bank/accounts</c> — รายการบัญชีธนาคารที่หน้าเอกสาร/ชำระเงิน/เช็ค/เงินสดย่อย/นำส่งภาษี/เงินเดือน/เกิดซ้ำ/ที่พัก/POS/เริ่มต้นใช้งาน
    /// โหลดเบื้องหลังเพื่อให้เลือก (ไล่ผู้เรียกใน wwwroot แล้ว — มีแต่ GET รายการ · สร้าง/แก้บัญชี + ทุกเส้นกระทบยอดอยู่ใน bank.html ยังผูก)</item>
    /// <item><c>GET …/warehouses</c> — รายการคลังที่หน้า POS (ตั้งเครื่อง POS · <c>pos.html</c>) และตั้งค่า SME โหลดเพื่อให้เลือก · หลักเดียวกับข้อ 21
    /// (คีย์ <c>/warehouses</c> เพิ่งเริ่มมีผลรอบนี้ตามข้อ 22 — ถ้าไม่ยกเว้น POS ของแพ็กเกจที่ไม่มีคลังหลายแห่งจะได้ 403 เบื้องหลังเมื่อบังคับ)</item>
    /// </list>
    /// </summary>
    private static readonly (string Method, string PathSuffix)[] FeatureExemptEndpoints =
    {
        ("GET", "/bank/accounts"),
        ("GET", "/warehouses"),
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
        // รอบ 200 ข้อ 22: คีย์เดิม /commission · /fixed-assets · /multi-currency · /warehouse ไม่เคยตรง endpoint จริง (ฟีเจอร์ไม่ถูก gate เลย) ⇒
        // แก้ให้ตรง route ของ controller — อยู่ใน NewlyGatedRouteKeys (โหมดเงาสำหรับทุกผู้เรียกจนกว่าเจ้าของกดบังคับ)
        ("/commissions",              FeatureFlags.Commission),        // CommissionController
        ("/fixedasset",               FeatureFlags.FixedAssets),       // FixedAssetController [controller]
        ("/recurring",                FeatureFlags.RecurringTransactions),
        ("/revenue-recognition",      FeatureFlags.RevenueRecognition),
        ("/loans",                    FeatureFlags.LoanManagement),
        ("/currency",                 FeatureFlags.MultiCurrency),     // CurrencyController [controller]
        ("/projects",                 FeatureFlags.ProjectAccounting),
        ("/time-billing",             FeatureFlags.TimeBilling),
        ("/dimensions",               FeatureFlags.CostCenter),
        ("/intercompany",             FeatureFlags.MultiCompany),
        ("/consolidation",            FeatureFlags.Consolidation),
        ("/warehouses",               FeatureFlags.WarehouseManagement), // WarehouseController (GET รายการ ยกเว้น — FeatureExemptEndpoints)
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
        ("/approval",                 FeatureFlags.ApprovalWorkflow),  // ApprovalController [controller] (กฎอนุมัติ — ตรงมาแต่เดิม)
        ("/approvals",                FeatureFlags.ApprovalWorkflow),  // SignatureApprovalController (ลายเซ็นอนุมัติเอกสาร) — รอบ 200 ข้อ 22
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
        // รอบ 200 ข้อ 22: คีย์เดิม "/ai/" ต้องเจอ "/ai//" จึงไม่เคยตรง ⇒ "/ai" (AiController + AiSuggestionController) · "/ai/ocr/…" ยังเป็น /ocr
        // และ "/ai/bank/…" ยังเป็น /bank (คีย์ยาวกว่าชนะ) · ⚠️ ตัวแนะนำหลายตัวถูกเรียกเบื้องหลังจากหน้าเอกสาร/สมุดรายวัน/เงินสดย่อย —
        // ดูในรายงานเงาก่อนกดบังคับ (api.js ข้อ 24: 403 เบื้องหลัง = แจ้งเตือน ไม่ดีดออก)
        ("/ai",                       FeatureFlags.AI_Features),
        ("/ocr",                      FeatureFlags.DocumentOCR),

        // Customer portal
        ("/customer-portal",          FeatureFlags.CustomerPortal),

        // FPA
        ("/fpa",                      FeatureFlags.FPA),

        // CMS
        ("/cms/sites",                FeatureFlags.CmsWebsiteBuilder),
        ("/cms/themes",               FeatureFlags.CmsWebsiteBuilder),
        ("/cms/customers",            FeatureFlags.CmsWebsiteBuilder),
        // รอบ 200 ข้อ 22: "/commerce" · "/booking" แพ้ "/cms/sites" (ยาวกว่า) เสมอ ⇒ ไม่เคยมีผล · route จริงอยู่ใต้ /cms/sites/{siteId}/…
        // ⇒ คีย์ที่มี * (ตรง 1 segment) — ยาวกว่า /cms/sites จึงชนะ
        ("/cms/sites/*/commerce",     FeatureFlags.CmsEcommerce),
        ("/cms/sites/*/booking",      FeatureFlags.CmsBooking),
    };

    /// <summary>
    /// คีย์ที่เพิ่ง "เริ่มมีผล" รอบ 200 (D-P3 <c>/settlement</c> + คำตัดสินข้อ 22 แก้คีย์ที่ไม่ตรง endpoint) — <b>ต้องผ่านโหมดเงาสำหรับทุกผู้เรียก
    /// รวม partner ที่ส่ง <c>X-Company-Id</c></b> จนกว่าโหมดที่มีผลจริง = บังคับ (ข้อ 22 · ข้อ 5 "รายงานก่อน แล้วค่อยเปิดบังคับ") · คำขอเดียวกันที่เดิม
    /// ถูกตัดสินด้วยคีย์อื่น (<see cref="SubscriptionRouteRequirement.LegacyFeature"/>) ยังถูกบังคับด้วยคีย์เดิม (ไม่หลวมลง) · คีย์ที่ตรงมาแต่เดิม
    /// ห้ามอยู่ในชุดนี้ (partner ถูกบังคับมาก่อนรอบ 198 — ห้ามหลวม)
    /// </summary>
    public static readonly IReadOnlySet<string> NewlyGatedRouteKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "/settlement",
        "/commissions", "/fixedasset", "/currency", "/warehouses", "/approvals", "/ai",
        "/cms/sites/*/commerce", "/cms/sites/*/booking",
    };

    /// <summary>ตัวจับคู่ของแต่ละคีย์ (สร้างครั้งเดียว) — "ยึดท้าย segment": ตรงเมื่อตามด้วย <c>/</c> หรือจบ path · <c>*</c> = 1 segment ใดก็ได้</summary>
    private static readonly IReadOnlyList<(string Path, FeatureFlags Feature, System.Text.RegularExpressions.Regex Match)> RouteMatchers =
        RouteFeatureMap
            .OrderByDescending(m => m.Path.Length)
            .Select(m => (m.Path, m.Feature, new System.Text.RegularExpressions.Regex(
                System.Text.RegularExpressions.Regex.Escape(m.Path.ToLowerInvariant()).Replace(@"\*", "[^/]+") + "(/|$)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            .ToList();

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

    /// <summary>เส้นทางนี้ต้องใช้ฟีเจอร์อะไร (คีย์ยาวสุดที่ตรงชนะ) · <c>null</c> = ไม่ต้อง หรือเป็นเส้นทางที่กฎหมาย/คำตัดสินยกเว้น
    /// (<see cref="FeatureExemptRoutes"/> ทุก method · <see cref="FeatureExemptEndpoints"/> เฉพาะ method นั้น — <paramref name="method"/> <c>null</c> =
    /// ไม่รู้ method ⇒ ไม่ใช้ข้อยกเว้นราย method) · ผลบอกด้วยว่าคีย์เพิ่งเริ่มมีผลไหม และคำขอเดียวกันเดิมถูกตัดสินด้วยคีย์ไหน</summary>
    public static SubscriptionRouteRequirement? RequiredFeatureFor(string? path, string? method = null)
    {
        var lowerPath = (path ?? "").ToLowerInvariant().TrimEnd('/');
        if (FeatureExemptRoutes.Any(r => lowerPath.Contains(r)))
            return null;
        if (method != null && FeatureExemptEndpoints.Any(e =>
                string.Equals(e.Method, method, StringComparison.OrdinalIgnoreCase) && lowerPath.EndsWith(e.PathSuffix, StringComparison.Ordinal)))
            return null;
        (string Path, FeatureFlags Feature)? current = null, legacy = null;
        foreach (var m in RouteMatchers)
        {
            if (!m.Match.IsMatch(lowerPath)) continue;
            current ??= (m.Path, m.Feature);
            if (!NewlyGatedRouteKeys.Contains(m.Path)) { legacy = (m.Path, m.Feature); break; }
        }
        if (current is not { } c) return null;
        var isNew = NewlyGatedRouteKeys.Contains(c.Path);
        return new SubscriptionRouteRequirement(c.Feature, c.Path, isNew,
            isNew ? legacy?.Feature : c.Feature, isNew ? legacy?.Path : c.Path);
    }

    /// <summary>
    /// ฟีเจอร์ของคำขอนี้ต้องตัดสินแบบไหน — รอบ 200 คำตัดสินข้อ 22 + ข้อ 23 (ตัวเดียวของ middleware · ห้ามประกอบเงื่อนไขเองที่อื่น):
    /// <list type="bullet">
    /// <item>บริษัทมาจากคีย์ API ของ <c>/api/v1</c> (<see cref="TenantCompanySource.ApiKey"/>) ⇒ <b>ไม่ตรวจฟีเจอร์ตามตารางนี้</b> (Connected มีด่าน
    /// scope + ฟีเจอร์ของตัวเองใน <c>PublicApiControllerBase</c>) — ข้อ 23 ขอแค่สถานะระงับ/หมดอายุ</item>
    /// <item>คำขอ partner (ส่ง header) + คีย์ใหม่ + โหมดที่มีผลจริงยังไม่ใช่บังคับ ⇒ คีย์ใหม่ <b>ตัดสินแล้วบันทึกอย่างเดียว</b> (<see cref="SubscriptionFeaturePlan.ShadowOnly"/>)
    /// · คีย์เดิมที่คำขอเดียวกันเคยถูกตัดสิน (<see cref="SubscriptionRouteRequirement.LegacyFeature"/>) ยังบังคับ (ไม่หลวมลง)</item>
    /// <item>นอกนั้น ⇒ ฟีเจอร์ของคีย์ปัจจุบัน (หน้าเว็บ: การกระทำของคำขอตัดสินว่าเงา/บังคับ · partner คีย์เดิม: บังคับเหมือนก่อนรอบ 198)</item>
    /// </list>
    /// </summary>
    public static SubscriptionFeaturePlan FeaturePlanFor(TenantCompanyTarget target, SubscriptionEnforcementMode effectiveMode,
        SubscriptionRouteRequirement? requirement)
    {
        if (requirement is not { } r || target.Source == TenantCompanySource.ApiKey)
            return new SubscriptionFeaturePlan(null, null, null, null);
        if (target.HeaderCarried && r.NewlyGated && effectiveMode != SubscriptionEnforcementMode.Enforce)
            return new SubscriptionFeaturePlan(r.LegacyFeature, r.LegacyRouteKey, r.Feature, r.RouteKey);
        return new SubscriptionFeaturePlan(r.Feature, r.RouteKey, null, null);
    }

    /// <summary>ผู้เรียกเป็น partner/integration (ส่ง <c>X-Company-Id</c> เอง หรือคีย์ API ของ <c>/api/v1</c>) — รายงานเงานับแยกคอลัมน์จากหน้าเว็บ</summary>
    public static bool IsPartnerCaller(TenantCompanyTarget target) =>
        target.HeaderCarried || target.Source == TenantCompanySource.ApiKey;

    /// <summary>เส้นทาง Connected (<c>/api/v1/…</c>) — บริษัทมาจากคีย์ API (<c>context.Items["CompanyId"]</c> ที่ <c>ApiKeyMiddleware</c> ใส่)</summary>
    private static bool IsPublicApiPath(string? path) =>
        (path ?? "").StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// บริษัทของคำขอ <c>/api/v1</c> — รอบ 200 คำตัดสินข้อ 23 "ตรวจสถานะระงับ/หมดอายุผ่านสวิตช์ตัวเดียวกัน (เงาก่อน)": คำขอที่ไม่มีบริษัทจาก route/header
    /// แต่เป็นเส้น <c>/api/v1</c> ที่คีย์ API ผูกบริษัทไว้ ⇒ บริษัทของคีย์ (<see cref="TenantCompanySource.ApiKey"/> · ไม่ใช่ header ⇒ เดินตามโหมดที่มีผลจริง
    /// เหมือนหน้าเว็บ: เงา = บันทึก · บังคับ = บล็อก) · คำขอที่มีบริษัทอยู่แล้ว (route/header) ไม่แตะ (partner ที่ส่ง header ถูกบังคับแบบเดิม)
    /// </summary>
    public static TenantCompanyTarget WithPublicApiCompany(TenantCompanyTarget target, string? path, object? apiKeyCompanyId) =>
        target.CompanyId is null && IsPublicApiPath(path) && apiKeyCompanyId is Guid g && g != Guid.Empty
            ? new TenantCompanyTarget(g, TenantCompanySource.ApiKey, false, false)
            : target;

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
