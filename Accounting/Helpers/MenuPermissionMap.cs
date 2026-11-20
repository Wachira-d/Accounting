using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 277 (งาน A · คำตัดสิน 277-1) — <b>ตารางเดียว</b> "เมนู → สิทธิ์ที่ API ของหน้านั้นตรวจ" ที่เซิร์ฟเวอร์ใช้คำนวณ
/// <c>permissionDeniedMenuIds</c> ส่งให้แถบเมนู/ช่องค้นหาเมนู/ทางลัด (<c>RolePermissionService.GetMyPermissionsAsync</c>) — หน้าเว็บไม่พิมพ์คีย์สิทธิ์ซ้ำ
///
/// <para>═══ บั๊กจริง ═══ ผู้ใช้ที่ไม่ผูกบทบาท (ได้ <c>"*"</c> = เห็นทุกเมนู) หรือบทบาทที่ติ๊กเมนู "แดชบอร์ด" แต่ไม่ได้ติ๊กสิทธิ์
/// <c>Reports.Dashboard</c> ⇒ ล็อกอินแล้วถูกพาไปแดชบอร์ด แล้วเจอ "ไม่มีสิทธิ์ ดูแดชบอร์ดภาพรวม" ทันที (เจ้าของรายงาน 2026-11-20)</para>
///
/// <para>═══ กติกา ═══ ใส่เฉพาะเมนูที่ <b>ข้อมูลหลักของหน้า</b> ถูกด่านด้วยคีย์นั้นจริง (class-level <c>[RequirePermission]</c> หรือ endpoint
/// รายการ/ภาพรวมของหน้า) — เมนูที่ API ไม่ตรวจคีย์ใดยังอยู่ใต้สิทธิ์เมนู (<c>allowedMenuIds</c>) ตามเดิม · ค่าที่นี่ = ค่าคงที่ตัวเดียวกับด่าน
/// (ห้ามพิมพ์สตริงใหม่) · ทางเข้าเดิมของรอบ 201 GW (<see cref="PaymentGatewayPermissionScope.MenuPermissionKeys"/>) รวมเข้ามาที่นี่</para>
/// </summary>
public static class MenuPermissionMap
{
    private static readonly IReadOnlyDictionary<string, string> Own = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // แดชบอร์ด (/app.html · /simple.html) — DashboardController.GateAsync ทุก endpoint
        ["dashboard"] = PermissionKeys.ReportsDashboard,
        // class-level [RequirePermission(ReportsDashboard)] — FinancialManagementController / ExecutiveReportController / FpaController
        ["financial-mgmt"] = PermissionKeys.ReportsDashboard,
        ["executive-reports"] = PermissionKeys.ReportsDashboard,
        ["fpa"] = PermissionKeys.ReportsDashboard,
        // class-level [RequirePermission(ReportsFinancial)] — ConsolidationController
        ["consolidation"] = PermissionKeys.ReportsFinancial,
        // SettlementController: batches/reference/orphans = SettlementPermissionScope.View
        ["settlements"] = SettlementPermissionScope.View,
        // LodgingController: dashboard/calendar/reservations/housekeeping = LodgingManage
        ["lodging"] = PermissionKeys.LodgingManage,
    };

    /// <summary>ตารางรวม (ของรอบนี้ + ของหน้ารับชำระออนไลน์รอบ 201) — คีย์ = id เมนูใน <c>layout.js</c></summary>
    public static readonly IReadOnlyDictionary<string, string> Keys = Own
        .Concat(PaymentGatewayPermissionScope.MenuPermissionKeys.Where(kv => !Own.ContainsKey(kv.Key)))
        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>เมนูที่ไม่มีสิทธิ์ (เรียงตามชื่อเมนู) — <paramref name="has"/> = ด่านจริง (ตัวเดียวกับ <c>IPermissionService.HasPermissionAsync</c>) ·
    /// ตรวจคีย์ละครั้ง · ว่าง = null (เดิม: ไม่มีเมนูที่ถูกตัด)</summary>
    public static async Task<List<string>?> DeniedMenuIdsAsync(Func<string, Task<bool>> has)
    {
        var verdict = new Dictionary<string, bool>(StringComparer.Ordinal);
        var denied = new List<string>();
        foreach (var (menuId, key) in Keys.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!verdict.TryGetValue(key, out var ok)) verdict[key] = ok = await has(key);
            if (!ok) denied.Add(menuId);
        }
        return denied.Count == 0 ? null : denied;
    }
}
