using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>
/// **"ชนิดข้อมูลนี้ นำเข้า/ส่งออกได้ด้วยสิทธิ์อะไร" — ตัวตัดสินตัวเดียวของเส้นนำเข้าไฟล์**
///
/// ═══ ทำไมต้องมี (ช่องโหว่จริง · ผลตรวจทีมทางเข้าภายนอก รอบ 184) ═══
/// <para><c>ImportExportController</c> มีแค่ <c>[Authorize]</c> ระดับคลาส ซึ่งตอบได้
/// แค่ <b>"ล็อกอินอยู่ไหม"</b> ⇒ สมาชิกคนไหนของบริษัทก็:</para>
/// <list type="bullet">
/// <item><b>นำเข้า</b>สมุดรายวัน · การชำระเงิน · สินทรัพย์ถาวร · ยอดลูกหนี้/เจ้าหนี้
///   ยกมา · พนักงาน (พร้อมเงินเดือน) ได้เป็นพัน ๆ แถวในคำสั่งเดียว</item>
/// <item><b>ส่งออก</b>ทะเบียนพนักงานทั้งบริษัท ซึ่งมี <c>CitizenId</c> 13 หลัก ·
///   <c>BankAccountNumber</c> · <c>BaseSalary</c> — ข้อมูลอ่อนไหวตาม PDPA ม.26</item>
/// </list>
/// <para>นี่คือ defect class เดียวกับที่ <c>tools/write_permission_gate_check.py</c>
/// เกิดมาเพื่อจับ — แต่ไฟล์นี้<b>ไม่เคยอยู่ใน WATCHED</b> จึงรายงานเขียวตลอด
/// ("allow-list ครบไหม ≠ ผ่านไหม" — บทเรียนซ้ำรอบที่ 6)</para>
///
/// ═══ ทำไมใช้คีย์ที่มีอยู่แล้ว ไม่สร้างคีย์ใหม่ ═══
/// <para>คีย์ใหม่ที่ยังไม่มี role ไหน grant = <b>ทุกคนที่ไม่ใช่ Owner/SystemAdmin
/// ถูกล็อกออกทันที</b> ⇒ การเข้มขึ้นที่ไม่มีทางไปต่อ (CLAUDE.md F2 ข้อ 8).
/// ที่นี่จึงผูกกับคีย์ที่ <c>UserRole.Accountant</c> ได้อยู่แล้วโดยอัตโนมัติ
/// (<c>PermissionService.AccountantDefaultKeys</c>) ⇒ <b>คนที่ทำงานนี้อยู่ทุกวันนี้
/// ไม่มีใครถูกกัน</b> เปลี่ยนเฉพาะคนที่ไม่เคยควรทำได้ตั้งแต่ต้น</para>
///
/// <para><b>ข้อยกเว้นเดียวที่ตั้งใจให้เข้มกว่า role เดิม</b>: ส่งออก<b>ทะเบียนพนักงาน</b>
/// ต้องมี <c>Pii.View</c> (PDPA ม.26 + CLAUDE.md §J: เลขบัตรประชาชนเต็ม ๆ เปิดได้
/// เฉพาะ role <c>pii:view</c>) — นักบัญชีที่ต้องใช้จริงให้เจ้าของ grant คีย์นี้เพิ่ม
/// ซึ่งข้อความปฏิเสธบอกไว้ตรง ๆ แล้ว</para>
///
/// <para>G6: pure · ไม่มี I/O · ไม่ throw · ผู้เรียกเอาคีย์ที่ได้ไปถาม
/// <c>IPermissionService</c> เอง</para>
/// </summary>
public static class ImportExportPermissionScope
{
    /// <summary>คีย์ที่ใช้เมื่อไม่รู้จักชนิดข้อมูล — ต้องเป็น<b>ชั้นที่เข้มที่สุด</b>
    /// ของชุดที่นักบัญชีถืออยู่ มิฉะนั้นชนิดใหม่ที่เพิ่มพรุ่งนี้จะหลุดด่านโดยอัตโนมัติ
    /// ("เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล ห้ามตกเป็นผ่าน" — DOCTRINE §1 G3)</summary>
    public const string FallbackKey = PermissionKeys.JournalManage;

    /// <summary>ชื่อชนิดข้อมูลแบบมาตรฐาน — สะกดได้หลายแบบในเรพ
    /// (<c>chartofaccounts</c> / <c>chart-of-accounts</c>) ·
    /// <c>private</c>: ผู้เรียกต้องถามผ่าน <see cref="ForImport"/>/<see cref="ForExport"/></summary>
    private static string Normalize(string? entityType)
        => (entityType ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");

    /// <summary>สิทธิ์ที่ต้องมีเพื่อ<b>นำเข้า</b>ข้อมูลชนิดนี้</summary>
    public static string ForImport(string? entityType) => Normalize(entityType) switch
    {
        "contacts" => PermissionKeys.ContactEdit,
        "products" => PermissionKeys.ProductEdit,
        "chartofaccounts" => PermissionKeys.ChartOfAccountsEdit,
        "journalentries" or "openingar" or "openingap" or "budgets" or "projects"
            => PermissionKeys.JournalManage,
        "documents" or "payments" => PermissionKeys.DocumentCreate,
        "banktransactions" => PermissionKeys.BankReconcile,
        "stockopening" or "stockadjustment" or "stockmovements" or "stockbalances"
            => PermissionKeys.InventoryAdjust,
        "fixedassets" => PermissionKeys.AssetManage,
        // ทะเบียนพนักงาน = เงินเดือน + เลขบัตรประชาชน + เลขบัญชีธนาคาร
        "employees" => PermissionKeys.HrAdmin,
        _ => FallbackKey,
    };

    /// <summary>สิทธิ์ที่ต้องมีเพื่อ<b>ส่งออก</b>ข้อมูลชนิดนี้เป็นไฟล์</summary>
    public static string ForExport(string? entityType) => Normalize(entityType) switch
    {
        // ไฟล์ทะเบียนพนักงานมี CitizenId 13 หลัก + เลขบัญชี + เงินเดือน
        // ⇒ PDPA ม.26: เปิดได้เฉพาะผู้มีสิทธิ์ดูข้อมูลส่วนบุคคล
        "employees" => PermissionKeys.PiiView,
        "banktransactions" => PermissionKeys.BankView,
        "stockmovements" or "stockbalances" => PermissionKeys.InventoryView,
        _ => PermissionKeys.DocumentExport,
    };

    /// <summary>ข้อความปฏิเสธที่บอก<b>ทางไปต่อ</b> — ห้ามคืน 403 เปล่าที่ผู้ใช้เดาต่อไม่ถูก</summary>
    public static string DeniedMessage(string? entityType, string requiredKey, bool isExport)
    {
        var what = isExport ? "ส่งออก" : "นำเข้า";
        var extra = requiredKey == PermissionKeys.PiiView
            ? " — ไฟล์นี้มีเลขบัตรประชาชน เลขบัญชีธนาคาร และเงินเดือนของพนักงาน "
              + "(ข้อมูลอ่อนไหวตาม พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล ม.26)"
            : "";
        return $"บัญชีของคุณไม่มีสิทธิ์{what}ข้อมูลชนิด \"{entityType}\"{extra} "
             + $"— ให้เจ้าของบริษัทเพิ่มสิทธิ์ \"{requiredKey}\" ให้บทบาทของคุณ "
             + "ที่หน้าบทบาท/สิทธิ์ แล้วลองใหม่";
    }
}
