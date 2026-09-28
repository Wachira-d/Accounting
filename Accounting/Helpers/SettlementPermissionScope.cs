using Accounting.Models.Constants;

namespace Accounting.Helpers;

/// <summary>
/// **สิทธิ์ของงาน settlement (wallet ของ gateway/marketplace → ธนาคาร) — ตารางเดียว** (รอบ 198 เฟส 1 ทีม D)
///
/// <para>═══ ทำไมต้องมีตารางนี้ ═══ service ของทีม B/C กรอง <c>CompanyId</c> เองแต่<b>ไม่ตรวจสิทธิ์</b> (สัญญา: ด่านสิทธิ์เป็นของผู้เรียก) ⇒
/// ถ้า controller เลือกคีย์เองทีละ endpoint จะได้ "คีย์คนละตัวกับงานเดียวกัน" (defect class G-8 ของ <c>PaymentGatewayController</c>
/// ที่มีแค่ <c>[Authorize]</c> ระดับคลาส) · ไฟล์แนบต้นฉบับของรอบโอน (<c>AttachmentPermissionScope["SettlementBatch"]</c>) ใช้คีย์จากตารางนี้ด้วย</para>
///
/// <para>═══ กติกา (เลือกจาก<b>ผลกระทบ</b> ไม่ใช่ HTTP verb) ═══
/// <list type="bullet">
/// <item><see cref="View"/> — อ่านช่องทาง/รอบโอน/บรรทัด · พรีวิวการลงบัญชี (ไม่เขียนอะไร)</item>
/// <item><see cref="Import"/> — นำเข้าไฟล์ · ประกอบจากรายการรับชำระ · จัดประเภท · จับคู่ใบขาย · ยกเลิกรอบที่<b>ยังไม่ลงบัญชี</b> (ไม่แตะ GL)</item>
/// <item><see cref="Post"/> — ลงบัญชี (ใบค่าธรรมเนียม/ใบขายสรุป/รับชำระ/JE/50 ทวิ) · ยกเลิกการลงบัญชี · ดูผู้สมัคร+จับคู่เงินเข้าธนาคาร (รายการเดินบัญชีจริง) · ปิด chargeback (ขยับ GL)</item>
/// <item><see cref="Channels"/> — ตั้งค่าช่องทาง: ผังพัก · ผู้ติดต่อแพลตฟอร์ม · โหมด VAT/หัก ณ ที่จ่ายของค่าธรรมเนียม (กำหนดภาษีของทุกรอบถัดไป)</item>
/// </list>
/// ลงบัญชี/ยกเลิก/ยกเลิกรอบ/ตั้งค่าช่องทาง = ห้ามคีย์ API (<c>[RejectApiKey]</c>) — ล็อกด้วย <c>tools/owner_action_wiring_check.py</c> ·
/// ผู้กดลงบัญชียังต้องมีสิทธิ์อนุมัติเอกสารชนิดที่ระบบสร้าง (ด่านของ <c>SettlementPostingGate</c> · ซ้อนอีกชั้น ไม่ใช่แทน)</para>
///
/// <para>G6: pure · ไม่มี I/O</para>
/// </summary>
public static class SettlementPermissionScope
{
    public const string View = PermissionKeys.SettlementView;
    public const string Import = PermissionKeys.SettlementImport;
    public const string Post = PermissionKeys.SettlementPost;
    public const string Channels = PermissionKeys.SettlementChannels;

    /// <summary>อ่าน/ดาวน์โหลดไฟล์ settlement report ต้นฉบับ — มีเลขออเดอร์/ข้อมูลผู้ซื้อที่<b>ยังไม่ตัด PII</b> ⇒ แค่ "ดู" ไม่พอ
    /// ต้องเป็นคนที่นำเข้าหรือลงบัญชีรอบโอน</summary>
    public static readonly IReadOnlyList<string> SourceFileReaders = new[] { Import, Post };

    /// <summary>ลบไฟล์ต้นฉบับ — หลักฐานประกอบรายการบัญชี (พ.ร.บ.การบัญชี ม.10) ⇒ เฉพาะคนนำเข้า (ไฟล์ของรอบที่ลงบัญชีแล้วยังมีด่านไฟล์แนบชั้นถัดไป)</summary>
    public static readonly IReadOnlyList<string> SourceFileWriters = new[] { Import };
}
