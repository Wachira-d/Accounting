using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>
/// ตารางสิทธิ์ "อ่าน / เขียน / อนุมัติ / ลบ(ยกเลิก)" แยกตามเมนู/ประเภทเอกสาร — <b>ตัวตั้งตัวเดียว</b>ที่หน้า <c>roles.html</c> วาดเป็นตาราง (รอบ 205 ทีม I · ข้อ 2)
///
/// <para>ทุกช่องคือคีย์ <c>perm:*</c> ที่ <b>ด่านเซิร์ฟเวอร์ตรวจจริง</b> อยู่แล้ว (ไม่สร้างคีย์ใหม่ที่ไม่มีผู้อ่าน — หลักการ F2 ข้อ 2) เช่น เอกสารฝั่งขาย/ซื้อ
/// ผ่าน <c>DocumentPermissionHelper.Can*Async</c> ที่ <c>DocumentController</c> · ช่องที่ไม่มีคีย์ = <c>null</c> (หน้าเว็บแสดง "—" ไม่ใช่ช่องติ๊กหลอก) ·
/// คีย์ที่ยังไม่มีด่านอ่าน (Document.ViewAll · Inventory.CostEdit · POS.ReportsView · CMS.*) <b>ไม่ใส่ในตาราง</b> — ติ๊กแล้วไม่มีผลคือการโกหกผู้ใช้ ·
/// "ลบ" ของเอกสาร = ยกเลิก (Void) เพราะเอกสารที่ออกเลขแล้วลบจริงไม่ได้ (ป.รัษฎากร §86/4 · ต้องออกใบยกเลิก)</para>
///
/// <para>ห้ามพิมพ์ตารางนี้ซ้ำในหน้าเว็บ — หน้าเรียก <c>GET api/companies/{id}/permission-catalog/matrix</c> · เทสต์ล็อกว่าทุกคีย์อยู่ใน
/// <see cref="PermissionKeys.Catalog"/> (คีย์พิมพ์ผิด = ติ๊กแล้วไม่มีผลเงียบ ๆ)</para>
/// </summary>
public static class PermissionMatrix
{
    public sealed record Row(string Area, string LabelTh, string Icon, string? View, string? Write, string? Approve, string? Delete, string? Note = null);

    public static readonly IReadOnlyList<string> ActionLabelsTh = new[] { "อ่าน", "เขียน", "อนุมัติ", "ลบ/ยกเลิก" };

    public static readonly IReadOnlyList<Row> Rows = new List<Row>
    {
        new("doc-revenue", "เอกสารฝั่งขาย (ใบเสนอราคา · ใบแจ้งหนี้ · ใบกำกับ · ใบเสร็จ · ใบลด/เพิ่มหนี้)", "🧾",
            PermissionKeys.DocumentRevenueView, PermissionKeys.DocumentRevenueCreate,
            PermissionKeys.DocumentRevenueApprove, PermissionKeys.DocumentRevenueVoid,
            "อนุมัติ = ออกเลขเอกสารจริง · ลบ = ยกเลิกเอกสาร (ลบเลขที่ออกแล้วไม่ได้ตามกฎหมาย)"),
        new("doc-purchase", "เอกสารฝั่งซื้อ (ใบขอซื้อ · ใบสั่งซื้อ · ใบรับสินค้า · ใบตั้งหนี้ · ใบสำคัญจ่าย)", "📥",
            PermissionKeys.DocumentPurchaseView, PermissionKeys.DocumentPurchaseCreate,
            PermissionKeys.DocumentPurchaseApprove, PermissionKeys.DocumentPurchaseVoid),
        new("doc-all", "เอกสารทุกประเภท (ครอบทั้งสองฝั่ง)", "📄",
            null, PermissionKeys.DocumentCreate,
            PermissionKeys.DocumentApprove, PermissionKeys.DocumentVoid,
            "การอ่านเอกสารคุมด้วยแถวฝั่งขาย/ฝั่งซื้อด้านบน (ไม่ติ๊กทั้งสองฝั่ง = เห็นทุกฝั่งตามเดิม)"),
        new("etax", "ใบกำกับภาษีอิเล็กทรอนิกส์ (e-Tax)", "📨",
            null, PermissionKeys.EtaxIssue, PermissionKeys.EtaxSubmit, PermissionKeys.EtaxVoid,
            "อนุมัติ = ส่งกรมสรรพากร"),
        new("bank", "ธนาคาร", "🏦",
            PermissionKeys.BankView, PermissionKeys.BankReconcile, PermissionKeys.BankPaymentInit, null,
            "เขียน = กระทบยอด · อนุมัติ = สั่งโอนเงินออก"),
        new("settlement", "รับเงินจากแพลตฟอร์ม (settlement)", "💳",
            PermissionKeys.SettlementView, PermissionKeys.SettlementImport, PermissionKeys.SettlementPost, null,
            "อนุมัติ = ลงบัญชีรอบโอน"),
        new("journal", "สมุดรายวัน / ใบสำคัญ", "📒",
            PermissionKeys.AccountingView, PermissionKeys.JournalManage, null, null),
        new("payroll", "เงินเดือน", "💰",
            PermissionKeys.PayrollView, PermissionKeys.PayrollRun, PermissionKeys.PayrollApprove, null,
            "การจ่ายจริงแยกเป็นสิทธิ์ “จ่าย Payroll” ในรายการสิทธิ์ด้านล่าง"),
        new("expense", "ใบเบิกค่าใช้จ่าย", "🧾",
            null, null, PermissionKeys.ExpenseApprove, PermissionKeys.ExpenseReject,
            "ลบ = ปฏิเสธใบเบิก · พนักงานยื่นเบิกของตัวเองได้เสมอ"),
        new("inventory", "สต๊อกสินค้า", "📦",
            PermissionKeys.InventoryView, PermissionKeys.InventoryAdjust, null, null,
            "รับ/โอน/เบิกออก อยู่ในรายการสิทธิ์ด้านล่าง"),
        new("asset", "ทรัพย์สินถาวร", "🏗️",
            null, PermissionKeys.AssetManage, PermissionKeys.AssetDepreciate, PermissionKeys.AssetDispose,
            "อนุมัติ = รันค่าเสื่อม · ลบ = จำหน่ายทรัพย์สิน"),
        new("pos", "หน้าร้าน (POS)", "🛒",
            null, PermissionKeys.PosCashier, PermissionKeys.PosCloseDay, PermissionKeys.PosRefund,
            "เขียน = ขาย/รับเงิน · อนุมัติ = ปิดยอดวัน · ลบ = คืนเงิน/ยกเลิกบิล"),
        new("reports", "รายงานการเงิน", "📊",
            PermissionKeys.ReportsFinancial, null, null, null, "ส่งออกรายงานอยู่ในรายการสิทธิ์ด้านล่าง"),
        new("tax", "ภาษี (ภ.พ.30 · ภ.ง.ด.)", "🏛️",
            null, PermissionKeys.TaxExport, PermissionKeys.TaxFile, null, "เขียน = ส่งออกไฟล์ · อนุมัติ = ยื่นแบบ"),
        new("master", "ผู้ติดต่อ · สินค้า · ผังบัญชี", "👥",
            null, PermissionKeys.ContactEdit, null, null, "ผังบัญชี/สินค้า แก้ได้ด้วยสิทธิ์ของตัวเองในรายการด้านล่าง"),
    };

    /// <summary>ทุกคีย์ที่ตารางอ้าง (ไม่รวมช่องว่าง)</summary>
    public static IEnumerable<string> AllKeys()
        => Rows.SelectMany(r => new[] { r.View, r.Write, r.Approve, r.Delete }).OfType<string>();
}
