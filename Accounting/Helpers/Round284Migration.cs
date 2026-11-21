using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 284 (คำตัดสิน 284-7 · F2 ข้อ 9 "persist แล้วต้องมี migration") — ด่านสิทธิ์รายงานการเงินใหม่ (<see cref="FinancialReportAccess"/>) ทำให้
/// <b>บทบาทกำหนดเองเดิม</b>ที่ติ๊กเมนูรายงานไว้แต่ไม่เคยมีคีย์ (เพราะ API เดิมไม่ตรวจ) เปิดหน้าไม่ได้ ⇒ ครั้งเดียว (marker <see cref="MarkerKey"/>):
/// บทบาทที่ติ๊กเมนูใน <see cref="MenuGrants"/> ได้คีย์ของหน้านั้น — พฤติกรรมของบทบาทเดิมคงเดิม · ครั้งเดียวเท่านั้น (เจ้าของถอดคีย์ภายหลัง ⇒ ไม่ถูกเติมกลับตอนบูต) ·
/// บทบาทที่<b>ไม่</b>ได้ติ๊กเมนูรายงาน (เช่น "พนักงาน") ไม่ได้คีย์ ⇒ ยอดเดือนบนหน้าหลักโหมดง่ายถูกซ่อนพร้อมป้ายเหตุ (Q1 ค)
/// </summary>
public static class Round284Migration
{
    public const string MarkerKey = "r284-report-keys-from-menus";

    /// <summary>เมนูของบทบาท → คีย์ที่หน้านั้นต้องใช้ (คีย์แคบที่สุดที่เปิดหน้าได้ — ไม่ขยายสิทธิ์เกินหน้าที่ติ๊กไว้)</summary>
    public static readonly IReadOnlyList<(string MenuId, string Key)> MenuGrants = new[]
    {
        ("reports", PermissionKeys.ReportsFinancial),
        ("budget", PermissionKeys.ReportsBudget),          // ฝ่ายค้าน MED-3: คีย์แคบ (ยังไม่ deploy ⇒ แก้ตารางตรง ไม่มีแถวต้องถอน)
        ("journals", PermissionKeys.AccountingView),       // ฝ่ายค้าน MED-2: รายการ JE ทั้งบริษัท
        ("tax", PermissionKeys.TaxExport),                 // ฝ่ายค้าน MED-1: หน้ารายงานภาษี (TaxController GET)
        ("risk", PermissionKeys.ReportsOperational),       // ฝ่ายค้าน LOW: คะแนนความเสี่ยงลูกค้า/ผู้ขาย
        ("general-ledger", PermissionKeys.AccountingView),
        ("aging", PermissionKeys.ReportsOperational),
        ("arap-analysis", PermissionKeys.ReportsOperational),
        ("cash-forecast", PermissionKeys.ReportsExecutive),
        ("cash-management", PermissionKeys.ReportsExecutive),
        ("tax-export", PermissionKeys.TaxExport),
    };

    public static IReadOnlyList<string> Statements()
    {
        var values = string.Join(", ", MenuGrants.Select(g => $"('{g.MenuId}', '{g.Key}')"));
        return new[]
        {
            // รอบ 284 ฝ่ายค้าน HIGH-3: รายชื่อผู้รับภายนอกที่เจ้าของอนุมัติ (null = ยังไม่มีการอนุมัติ ⇒ ส่งถึงสมาชิกบริษัทเท่านั้น)
            """ALTER TABLE "ScheduledReports" ADD COLUMN IF NOT EXISTS "ApprovedExternalRecipientsJson" text NULL;""",
            $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM "OneShotDataFixes" WHERE "Key" = '{MarkerKey}') THEN
                    INSERT INTO "CompanyRolePermissions" ("Id", "CompanyRoleId", "MenuItemId", "CanAccess")
                    SELECT gen_random_uuid(), p."CompanyRoleId", m.key, true
                    FROM "CompanyRolePermissions" p
                    JOIN (VALUES {values}) AS m(menu, key) ON m.menu = p."MenuItemId"
                    WHERE p."CanAccess" = true
                    GROUP BY p."CompanyRoleId", m.key
                    ON CONFLICT ("CompanyRoleId", "MenuItemId") DO NOTHING;
                    INSERT INTO "OneShotDataFixes" ("Key", "AppliedAt") VALUES ('{MarkerKey}', (now() at time zone 'utc')) ON CONFLICT DO NOTHING;
                END IF;
            END $$;
            """,
        };
    }
}
