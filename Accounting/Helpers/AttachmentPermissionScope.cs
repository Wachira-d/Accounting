using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>ไฟล์แนบของเจ้าของชนิดนี้ต้องถามสิทธิ์แบบไหน — ผู้เรียก (controller) ใช้เลือกว่าต้องค้นแถวเจ้าของก่อนไหม</summary>
public enum AttachmentOwnerKind
{
    /// <summary>ไม่รู้จัก — ห้ามเขียน (ทิศปลอดภัย) · อ่านได้ระดับสมาชิกบริษัท (พฤติกรรมเดิม)</summary>
    Unknown = 0,
    /// <summary>เอกสาร — สิทธิ์ตามชนิดเอกสาร (<see cref="DocumentPermissionHelper"/>) + ชั้นความลับของใบ</summary>
    Document,
    /// <summary>การชำระเงิน — สิทธิ์ตามชนิดของ<b>เอกสารที่ถูกชำระ</b> (ด่านเดียวกับ CreatePayment/VoidPayment)</summary>
    Payment,
    /// <summary>ใบเบิกค่าใช้จ่าย — เจ้าของใบแนบ/ดูของตัวเองได้เสมอ · ของคนอื่นต้องมีคีย์</summary>
    ExpenseClaim,
    /// <summary>รอบเงินเดือน — ต้องผ่านด่านความลับ Payroll ทั้งอ่านและเขียน (เงินเดือนรายคน)</summary>
    PayrollRun,
    /// <summary>ใบสำคัญ (JE) — เขียนต้องมีคีย์ · อ่านต้องผ่านชั้นความลับของใบ</summary>
    JournalEntry,
    /// <summary>ชนิดที่ตัดสินด้วยคีย์อย่างเดียว (ข้อมูลหลัก · สินทรัพย์ · ภาษี · บิลลิ่ง · ที่พัก · เว็บ · สแกน)</summary>
    KeyGated,
}

/// <summary>ผลของ <see cref="AttachmentPermissionScope.Resolve"/></summary>
/// <param name="CanonicalType">ชื่อชนิดแบบมาตรฐาน (สะกดตามที่เก็บในตาราง) · <c>null</c> = ไม่รู้จัก</param>
/// <param name="Kind">วิธีตัดสิน</param>
/// <param name="WriteAnyOf">คีย์ที่ต้องมี<b>อย่างน้อยหนึ่งตัว</b>เพื่อแนบ/ลบ (ว่าง = ตัดสินด้วย <paramref name="Kind"/> ไม่ใช่คีย์)</param>
/// <param name="ReadAnyOf">คีย์ที่ต้องมีเพื่อดูรายการ/ดาวน์โหลด · ว่าง = สมาชิกบริษัทดูได้ (ตามด่านของโมดูลนั้นเอง)</param>
/// <param name="ClientUploadAllowed">อัปโหลดจากหน้าเว็บ (<c>POST attachments/{type}/{id}</c>) ได้ไหม —
/// ชนิดที่ระบบสร้างเอง (สแกน · ใบเสร็จค่าบริการ · สลิปแขกที่พัก) แนบผ่านเส้นของมันเองเท่านั้น</param>
/// <param name="AllowsUnsavedOwner">รับ <c>entityId</c> ว่าง (Guid.Empty) ตอนอัปโหลดได้ — หน้าที่แนบไฟล์ก่อนบันทึกรายการ
/// แล้วผูกด้วย id ของไฟล์ (ภาษีถูกหัก ณ ที่จ่าย)</param>
/// <param name="ModuleTh">ชื่อโมดูลภาษาไทยสำหรับข้อความปฏิเสธ</param>
public sealed record AttachmentScopeRule(
    string? CanonicalType,
    AttachmentOwnerKind Kind,
    IReadOnlyList<string> WriteAnyOf,
    IReadOnlyList<string> ReadAnyOf,
    bool ClientUploadAllowed,
    bool AllowsUnsavedOwner,
    string ModuleTh)
{
    public bool IsKnown => Kind != AttachmentOwnerKind.Unknown;
}

/// <summary>
/// **"ไฟล์แนบของเจ้าของชนิดนี้ แนบ/ลบ/ดู ได้ด้วยสิทธิ์อะไร" — ตัวตัดสินตัวเดียวของ <c>FileAttachmentController</c>**
///
/// <para>═══ ที่มา (คำตัดสินเจ้าของ รอบ 193 ข้อ 29) ═══ รอบ 190 ใส่ด่านให้ <c>entityType = "Document"</c> ชนิดเดียว ·
/// ชนิดอื่นทั้งหมด (การชำระ · JE · สินทรัพย์ · ใบเบิก · <b>รอบเงินเดือน</b> · ผู้ติดต่อ · สินค้า · โปรเจกต์) มีแค่
/// <c>[Authorize]</c> ระดับคลาส (= "ล็อกอินไหม") ⇒ สมาชิกคนไหนก็แนบ/ลบหลักฐานได้ และ <b>ดาวน์โหลดสลิปโอนเงินเดือน/
/// ใบนำส่ง ปกส. ที่มีเงินเดือนรายคน</b>ได้ (PDPA ม.26 · CLAUDE.md §J)</para>
///
/// <para>═══ ทำไมใช้คีย์ที่มีอยู่แล้ว ═══ คีย์ใหม่ที่ยังไม่มี role ไหน grant = ทุกคนที่ไม่ใช่ Owner ถูกล็อกออกทันที
/// (F2 ข้อ 8) ⇒ ทุกแถวผูกกับคีย์ที่<b>โมดูลนั้นใช้อยู่แล้ว</b>: ผู้ติดต่อ = <c>Contact.Edit</c> (DocumentController) ·
/// สินค้า = <c>Product.Edit</c> · JE/โปรเจกต์ = <c>Journal.Manage</c> (AccountingController · สายนำเข้า "projects"
/// ใน <see cref="ImportExportPermissionScope"/>) · สินทรัพย์ = <c>Asset.Manage</c> (FixedAssetController) ·
/// เงินเดือน = <c>Payroll.Run</c> + ด่านความลับ Payroll (PayrollController) · ใบเบิก = เจ้าของใบ หรือ
/// <c>HR.Admin</c>/<c>Expense.Approve</c> (ExpenseClaimController.GetAll) · ภาษี = <c>Tax.File</c>
/// (StatutoryRemittanceController) · บิลลิ่ง = <c>Billing.Manage</c> · ที่พัก = <c>Lodging.Manage</c> ·
/// คำสั่งซื้อจากเว็บ = <c>CMS.OrderManage</c></para>
///
/// <para>G6: pure · ไม่มี I/O · ไม่ throw · ชนิดที่ไม่รู้จัก ⇒ <see cref="AttachmentOwnerKind.Unknown"/> (ห้ามเขียน) —
/// "เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล ห้ามตกเป็นผ่าน" (DOCTRINE §1)</para>
/// </summary>
public static class AttachmentPermissionScope
{
    private static readonly string[] None = Array.Empty<string>();

    private static readonly string[] AnyDocumentCreate =
    {
        PermissionKeys.DocumentCreate, PermissionKeys.DocumentPurchaseCreate, PermissionKeys.DocumentRevenueCreate,
    };

    private static readonly string[] ExpenseClaimReviewers = { PermissionKeys.HrAdmin, PermissionKeys.ExpenseApprove };

    private static AttachmentScopeRule Rule(string type, AttachmentOwnerKind kind, string[] write, string[] read,
        bool upload, string module, bool unsaved = false)
        => new(type, kind, write, read, upload, unsaved, module);

    /// <summary>ตารางเดียว — key = ชื่อที่พบในตาราง <c>FileAttachments.EntityType</c> (ไม่สนตัวพิมพ์เล็ก/ใหญ่)
    /// รวมชื่อเก่า "contacts"/"products" ที่ <see cref="AttachmentRetention"/> รู้จัก</summary>
    private static readonly Dictionary<string, AttachmentScopeRule> Table = BuildTable();

    private static Dictionary<string, AttachmentScopeRule> BuildTable()
    {
        var contact = Rule("Contact", AttachmentOwnerKind.KeyGated, new[] { PermissionKeys.ContactEdit }, None, true, "ผู้ติดต่อ");
        var product = Rule("Product", AttachmentOwnerKind.KeyGated, new[] { PermissionKeys.ProductEdit }, None, true, "สินค้า/บริการ");
        var ocr = Rule("OcrScan", AttachmentOwnerKind.KeyGated, AnyDocumentCreate, None, false, "สแกนเอกสาร (OCR)");
        var t = new Dictionary<string, AttachmentScopeRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["Document"] = Rule("Document", AttachmentOwnerKind.Document, None, None, true, "เอกสาร"),
            // "Expense" อยู่ในรายชื่อที่หน้าเว็บส่งได้มาตั้งแต่ก่อน แต่ไม่มีตาราง Expense แยก — ค่าใช้จ่ายคือ
            // เอกสารชนิด DocumentType.Expense ⇒ ตัดสินแบบเอกสาร (ค้นใน Documents)
            ["Expense"] = Rule("Expense", AttachmentOwnerKind.Document, None, None, true, "เอกสารค่าใช้จ่าย"),
            ["Payment"] = Rule("Payment", AttachmentOwnerKind.Payment, None, None, true, "การชำระเงิน"),
            ["ExpenseClaim"] = Rule("ExpenseClaim", AttachmentOwnerKind.ExpenseClaim, ExpenseClaimReviewers, ExpenseClaimReviewers, true, "ใบเบิกค่าใช้จ่าย"),
            ["PayrollRun"] = Rule("PayrollRun", AttachmentOwnerKind.PayrollRun, new[] { PermissionKeys.PayrollRun }, None, true, "รอบเงินเดือน"),
            ["JournalEntry"] = Rule("JournalEntry", AttachmentOwnerKind.JournalEntry, new[] { PermissionKeys.JournalManage }, None, true, "ใบสำคัญ (สมุดรายวัน)"),
            ["FixedAsset"] = Rule("FixedAsset", AttachmentOwnerKind.KeyGated, new[] { PermissionKeys.AssetManage }, None, true, "สินทรัพย์ถาวร"),
            ["Contact"] = contact,
            ["contacts"] = contact,
            ["Product"] = product,
            ["products"] = product,
            ["Project"] = Rule("Project", AttachmentOwnerKind.KeyGated, new[] { PermissionKeys.JournalManage }, None, true, "โปรเจกต์"),
            // หน้า wht-credit.html แนบหนังสือรับรอง 50 ทวิ ที่ลูกค้าหักเรา — แนบก่อนบันทึก (id ว่าง) แล้วผูกด้วย AttachmentId
            // (เดิมถูกตีกลับ "ประเภทไม่ถูกต้อง" ทุกครั้งเพราะไม่อยู่ในรายชื่อ = ปุ่มแนบใช้ไม่ได้เลย)
            ["WhtCredit"] = Rule("WhtCredit", AttachmentOwnerKind.KeyGated, new[] { PermissionKeys.TaxFile }, None, true, "ภาษีถูกหัก ณ ที่จ่าย", unsaved: true),
            // ── ชนิดที่ระบบแนบเอง (ไม่รับอัปโหลดจากหน้าเว็บ) — ต้องอยู่ในตารางเพื่อให้ "ลบ" มีด่านที่ถูกโมดูล ──
            ["StatutoryRemittance"] = Rule("StatutoryRemittance", AttachmentOwnerKind.KeyGated, new[] { PermissionKeys.TaxFile }, None, false, "การนำส่งภาษี/ประกันสังคม"),
            ["SubscriptionReceipt"] = Rule("SubscriptionReceipt", AttachmentOwnerKind.KeyGated, new[] { PermissionKeys.BillingManage }, None, false, "ใบเสร็จค่าบริการ"),
            ["SubscriptionInvoice"] = Rule("SubscriptionInvoice", AttachmentOwnerKind.KeyGated, new[] { PermissionKeys.BillingManage }, None, false, "ใบแจ้งหนี้ค่าบริการ"),
            // สลิปมัดจำที่แขกส่งเข้ามา — ข้อมูลของบุคคลภายนอก ⇒ อ่านก็ต้องเป็นฝ่ายต้อนรับ
            ["LodgingReservation"] = Rule("LodgingReservation", AttachmentOwnerKind.KeyGated, new[] { PermissionKeys.LodgingManage }, new[] { PermissionKeys.LodgingManage }, false, "การจองที่พัก"),
            ["SiteOrder"] = Rule("SiteOrder", AttachmentOwnerKind.KeyGated, new[] { PermissionKeys.CmsOrderManage }, new[] { PermissionKeys.CmsOrderManage }, false, "คำสั่งซื้อจากเว็บไซต์"),
            ["OcrScan"] = ocr,
        };
        return t;
    }

    /// <summary>กฎของชนิดนี้ — ไม่พบ ⇒ <see cref="AttachmentOwnerKind.Unknown"/> (ห้ามเขียน · ห้ามอัปโหลด)</summary>
    public static AttachmentScopeRule Resolve(string? entityType)
    {
        var key = (entityType ?? "").Trim();
        if (key.Length > 0 && Table.TryGetValue(key, out var rule)) return rule;
        return new AttachmentScopeRule(null, AttachmentOwnerKind.Unknown, None, None, false, false, "ไม่ทราบชนิด");
    }

    /// <summary>ข้อความเมื่อชนิดไม่อยู่ในตาราง — บอกว่าทำไมและทำอะไรต่อได้</summary>
    public static string UnknownTypeMessage(string? entityType)
        => $"ไม่รู้จักชนิดรายการ \"{entityType}\" ของไฟล์แนบ — ระบบไม่อนุญาตให้แนบหรือลบไฟล์ของชนิดที่ไม่รู้ว่าใครมีสิทธิ์ "
         + "(กันหลักฐานบัญชีถูกแก้โดยไม่มีด่าน) · แนบไฟล์จากหน้าของรายการนั้นโดยตรง หรือแจ้งผู้ดูแลระบบ";

    /// <summary>ข้อความปฏิเสธที่บอก<b>ทางไปต่อ</b> — ชื่อคีย์ + ที่ที่เจ้าของไปเพิ่มสิทธิ์ได้</summary>
    public static string DeniedMessage(AttachmentScopeRule rule, string verb, IReadOnlyList<string> anyOfKeys)
    {
        var need = anyOfKeys.Count == 0
            ? ""
            : " (ต้องมีสิทธิ์ " + string.Join(" หรือ ", anyOfKeys.Select(k => k.Replace("perm:", ""))) + ")";
        return $"ไม่มีสิทธิ์{verb}ของ{rule.ModuleTh}{need} — ให้เจ้าของบริษัทเพิ่มสิทธิ์ให้บทบาทของคุณที่หน้าบทบาท/สิทธิ์ แล้วลองใหม่";
    }
}
