using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 277 (งาน A · คำตัดสิน 277-1 · ฝ่ายค้าน MED-1) — <b>ทะเบียนเดียว</b>ของเมนูทุกตัวใน <c>layout.js navItems</c> ว่าหน้าเมนูนั้นถูกคุมด้วยอะไร
/// ฝั่ง API — เซิร์ฟเวอร์ใช้คำนวณ <c>permissionDeniedMenuIds</c> ส่งให้แถบเมนู/ช่องค้นหาเมนู/ทางลัด (<c>RolePermissionService.GetMyPermissionsAsync</c>)
///
/// <para>═══ บั๊กจริง ═══ ผู้ใช้ที่ไม่ผูกบทบาท (<c>"*"</c> = เห็นทุกเมนู) หรือบทบาทที่ติ๊กเมนู "แดชบอร์ด" แต่ไม่ได้ติ๊กสิทธิ์
/// <c>Reports.Dashboard</c> ⇒ ล็อกอินแล้วเจอ "ไม่มีสิทธิ์ ดูแดชบอร์ดภาพรวม" (เจ้าของรายงาน 2026-11-20)</para>
///
/// <para>═══ สามกลุ่ม (เมนูหนึ่งอยู่กลุ่มเดียว · เทสต์ <c>MenuPermissionInventoryTests</c> เทียบกับ layout.js ทั้งสองทิศ) ═══
/// (1) <see cref="Keys"/> — endpoint ข้อมูลหลักของหน้า (GET ที่หน้าโหลดตอนเปิด) ตรวจคีย์ <c>perm:*</c> · ค่าเป็น "คีย์ใดคีย์หนึ่งก็พอ" ·
/// ค่าคงที่ตัวเดียวกับด่าน (ห้ามพิมพ์สตริงใหม่) ·
/// (2) <see cref="DirectionMenus"/> — endpoint ตรวจ "เห็นเอกสารฝั่งขาย/ซื้อ" (<c>DocumentPermissionHelper.VisibleDirectionsAsync</c> — ตัวตัดสินเดียวกัน) ·
/// (3) <see cref="RoleMenuOnly"/> — GET หลักของหน้าไม่ตรวจคีย์ <c>perm:*</c> (ด่านเขียน/ปุ่มรายตัวตรวจเอง) ⇒ คุมด้วย "สิทธิ์เมนู" ของบทบาทอย่างเดียว ·
/// ไล่จากซอร์ส 2026-11-20 (ตัวอย่างที่ตรวจแล้วว่า <b>ไม่</b>ตรวจคีย์ตอนอ่าน: payroll (ปิดค่าด้วย Payroll.View แทน — หน้ายังเปิดได้) · fixed-assets ·
/// pos/คอร์ส/โมดิฟายเออร์ · สินค้า/คลัง · bank · tax (GET รายการ) · remittances (GET แดชบอร์ด) · leaves (หน้าพนักงานใช้ endpoint เดียวกัน))</para>
/// </summary>
public static class MenuPermissionMap
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Own = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        // แดชบอร์ดโหมดเต็ม (/app.html) — DashboardController.GateAsync ทุก endpoint · หน้าหลักโหมดง่าย (/simple.html) ไม่ใช่เมนูนี้ (ฝั่งหน้าเว็บ "simple-home" · ฝ่ายค้าน MED-2)
        ["dashboard"] = new[] { PermissionKeys.ReportsDashboard },
        // class-level [RequirePermission(ReportsDashboard)] — FinancialManagementController / ExecutiveReportController / FpaController
        ["financial-mgmt"] = new[] { PermissionKeys.ReportsDashboard },
        ["executive-reports"] = new[] { PermissionKeys.ReportsDashboard },
        ["fpa"] = new[] { PermissionKeys.ReportsDashboard },
        // class-level [RequirePermission(ReportsFinancial)] — ConsolidationController
        ["consolidation"] = new[] { PermissionKeys.ReportsFinancial },
        // SettlementController: batches/channels/reference = SettlementPermissionScope.View
        ["settlements"] = new[] { SettlementPermissionScope.View },
        ["settlement-channels"] = new[] { SettlementPermissionScope.View },
        // LodgingController: dashboard/calendar/reservations = LodgingManage · ผู้ตั้งค่าที่พัก (LodgingSettings) ต้องเห็นทางเข้าเมนูด้วย (ฝ่ายค้าน MED-2)
        ["lodging"] = new[] { PermissionKeys.LodgingManage, PermissionKeys.LodgingSettings },
        // SettingsController GET (root) = CompanySettingsEdit — ไม่มีสิทธิ์ = หน้าโหลดค่าตั้งไม่ได้
        ["settings"] = new[] { PermissionKeys.CompanySettingsEdit },
    };

    /// <summary>เมนู → คีย์ที่ API ของหน้าตรวจ ("คีย์ใดคีย์หนึ่ง") — รวมหน้ารับชำระออนไลน์ของรอบ 201 (<see cref="PaymentGatewayPermissionScope.MenuPermissionKeys"/>)</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Keys = Own
        .Concat(PaymentGatewayPermissionScope.MenuPermissionKeys
            .Where(kv => !Own.ContainsKey(kv.Key))
            .Select(kv => new KeyValuePair<string, IReadOnlyList<string>>(kv.Key, new[] { kv.Value })))
        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>รอบ 284 (คำตัดสิน 284-7): เมนูรายงานการเงินรวมที่ API ตรวจด้วย <see cref="FinancialReportAccess"/> (คีย์ใดคีย์หนึ่ง ·
    /// สมาชิกที่<b>ไม่ผูกบทบาทกำหนดเอง</b>ผ่านตามเดิม) — ค่าคงที่ตัวเดียวกับ <c>[RequireReportAccess(...)]</c> ของหน้า (เทสต์ <c>PackFRound284Tests</c>)</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> LegacyKeys = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["reports"] = new[] { PermissionKeys.ReportsFinancial },                                        // AccountingController reports/* (งบ)
        ["general-ledger"] = new[] { PermissionKeys.AccountingView, PermissionKeys.ReportsFinancial },  // reports/general-ledger
        ["aging"] = new[] { PermissionKeys.ReportsOperational },                                        // AgingReportController
        ["arap-analysis"] = new[] { PermissionKeys.ReportsOperational },                                // ArApAnalysisController
        ["cash-forecast"] = new[] { PermissionKeys.ReportsFinancial, PermissionKeys.ReportsExecutive }, // CashForecastController
    };

    public enum Direction { Revenue, Purchase }

    /// <summary>เมนูที่ API หลักตรวจ "เห็นเอกสารฝั่งนี้" (<c>VisibleDirectionsAsync</c>) — PurchaseInboxController · CustomerCreditController</summary>
    public static readonly IReadOnlyDictionary<string, Direction> DirectionMenus = new Dictionary<string, Direction>(StringComparer.Ordinal)
    {
        ["purchases"] = Direction.Purchase,
        ["customer-credits"] = Direction.Revenue,
    };

    /// <summary>เมนูที่ GET หลักไม่ตรวจคีย์ <c>perm:*</c> — คุมด้วยสิทธิ์เมนูของบทบาท (หรือเป็นหน้าของแพลตฟอร์ม/ส่วนตัว) · ทุก id ใน layout.js ที่ไม่อยู่ใน
    /// <see cref="Keys"/>/<see cref="DirectionMenus"/> ต้องอยู่ที่นี่ (เพิ่มเมนูใหม่ = ต้องตัดสินว่าอยู่กลุ่มไหน — เทสต์บังคับ)</summary>
    public static readonly IReadOnlySet<string> RoleMenuOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "getting-started", "accountant-workspace", "accountant", "documents", "recurring", "autopilot", "deposits", "mobile-receipt",
        "revenue-recognition", "customer-portal", "expense-docs", "payments", "vendor-portal-admin", "partners", "bank", "cash-management",
        "cheques", "petty-cash", "loans", "multi-currency", "fx-reval", "contacts", "products", "product-aliases", "warehouse",
        "inventory-reports", "supplies", "production", "consignment", "tax", "vat-history", "undue-vat", "wht", "wht-credit", "tax-remittance",
        "tax-calendar", "etax", "tax-export", "stamp-duty", "accounts", "journals", "fiscal", "fixed-assets",
        "risk", "budget", "cost-report", "pos", "pos-packages", "course-credits", "pos-modifiers", "pos-reports", "cms-sites",
        "cms-orders", "cms-bookings", "cms-booking-providers", "lodging-settings", "cms-leads", "organization", "employees", "payroll",
        "leave-types", "leave-calendar", "salary-advance", "commission", "project-time", "expense", "expense-no-receipt", "leave-my",
        "mobile-expense", "projects", "time-billing", "dimensions", "intercompany", "document-scan", "ocr-review-queue", "ai-tools", "assistant",
        // หน้าของแพลตฟอร์ม — ด่านจริงคือ role SystemAdmin ที่ API (เมนูซ่อนด้วยธง isSystemAdmin ใน layout.js)
        "ai-usage", "admin-company-usage", "admin-help", "admin-addons", "admin-quota",
        "import-hub", "import-export", "migrate-competitor", "migration-wizard", "import-conflicts", "payment-settings", "branches",
        "sme-config", "email-schedule", "notifications", "team", "roles", "approval", "signatures", "sensitivity", "document-templates",
        "document-brands", "settings-features", "pdpa", "cleanup", "integrations", "api-developer", "webhooks",
        // hub แพ็กเกจและการชำระเงิน — ตรรกะ billingHub ใน layout.js (หน้าลูกเปิดได้เมื่อมี hub หรือลูก) · ด่านเขียน = Billing.Manage
        "account-subscription", "subscription", "billing", "addons", "usage",
        "help", "audit",
    };

    /// <summary>เมนูที่ไม่มีสิทธิ์ (เรียงตามชื่อเมนู) — <paramref name="has"/> = ด่านจริง (<c>IPermissionService.HasPermissionAsync</c>) ·
    /// <paramref name="visible"/> = ผล <c>DocumentPermissionHelper.VisibleDirectionsAsync</c> (null = ไม่ตรวจกลุ่มทิศเอกสาร) · ตรวจคีย์ละครั้ง · ว่าง = null</summary>
    /// <param name="hasCustomRole">ผูกบทบาทกำหนดเอง — false (เมนู "*") ⇒ เมนูใน <see cref="LegacyKeys"/> ไม่ถูกตัด (กติกาเดียวกับ <see cref="FinancialReportAccess.Allows"/>)</param>
    public static async Task<List<string>?> DeniedMenuIdsAsync(Func<string, Task<bool>> has, Func<Task<(bool Revenue, bool Purchase)>>? visible = null,
        bool hasCustomRole = true)
    {
        var verdict = new Dictionary<string, bool>(StringComparer.Ordinal);
        async Task<bool> Has(string key)
        {
            if (!verdict.TryGetValue(key, out var ok)) verdict[key] = ok = await has(key);
            return ok;
        }
        var denied = new List<string>();
        foreach (var (menuId, keys) in Keys.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var any = false;
            foreach (var k in keys) if (await Has(k)) { any = true; break; }
            if (!any) denied.Add(menuId);
        }
        foreach (var (menuId, keys) in LegacyKeys.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var any = false;
            foreach (var k in keys) if (await Has(k)) { any = true; break; }
            if (!FinancialReportAccess.Allows(any, member: true, hasCustomRole)) denied.Add(menuId);
        }
        if (visible != null)
        {
            var v = await visible();
            foreach (var (menuId, dir) in DirectionMenus)
                if (!(dir == Direction.Revenue ? v.Revenue : v.Purchase)) denied.Add(menuId);
        }
        denied.Sort(StringComparer.Ordinal);
        return denied.Count == 0 ? null : denied;
    }
}
