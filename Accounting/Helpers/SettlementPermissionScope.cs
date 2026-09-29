using Accounting.Models.Constants;
using Accounting.Models.DTOs.Settlement;

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

    /// <summary>
    /// **จำการจับคู่คอลัมน์ไว้กับช่องทางตอนนำเข้าได้ไหม** (review198-D D-P2 · R5 "ทางเข้าอื่นไม่เดินด่านเดียวกัน")
    ///
    /// <para>การจับคู่ที่จำไว้ (<c>SettlementChannel.ColumnMapJson</c>) มี "กลับเครื่องหมาย"/"ยังไม่รวม VAT" ซึ่งกำหนดเครื่องหมายและ VAT ของ<b>ทุกรอบถัดไป</b>
    /// = ค่าตั้งของช่องทาง ⇒ ด่านเดียวกับ <c>PUT channels/{id}</c>: ต้องมีสิทธิ์ <see cref="Channels"/> และห้ามคีย์ API · ไม่ผ่าน ⇒ ยังนำเข้าได้
    /// (การจับคู่ใช้กับไฟล์นี้ไฟล์เดียว) แต่<b>ไม่จำ</b> + บอกผู้ใช้ในผลการนำเข้า (ห้าม silent no-op)</para>
    /// </summary>
    /// <param name="requested">ผู้ใช้ติ๊ก "จำการจับคู่นี้ไว้กับช่องทาง"</param>
    /// <returns><c>Remember</c> = บันทึกลงช่องทาง · <c>Notice</c> = ข้อความถึงผู้ใช้เมื่อขอให้จำแต่ไม่ได้จำ (null = ไม่มีอะไรต้องบอก)</returns>
    public static (bool Remember, string? Notice) ColumnMapMemory(bool requested, bool isApiKeyRequest, bool hasChannelsPermission)
    {
        if (!requested) return (false, null);
        if (isApiKeyRequest)
            return (false, "ไม่ได้จำการจับคู่คอลัมน์ไว้กับช่องทาง — คำขอจากคีย์ API เปลี่ยนค่าตั้งของช่องทางไม่ได้ (การจับคู่นี้ใช้กับไฟล์นี้อย่างเดียว) · "
                + "ให้ผู้ใช้ที่มีสิทธิ์ตั้งค่าช่องทางนำเข้าจากหน้าเว็บ หรือแก้ที่หน้าตั้งค่าช่องทาง");
        if (!hasChannelsPermission)
            return (false, $"ไม่ได้จำการจับคู่คอลัมน์ไว้กับช่องทาง — ต้องมีสิทธิ์ “{PermissionKeys.LabelOf(Channels)}” "
                + "(การจับคู่กำหนดเครื่องหมาย/VAT ของทุกรอบถัดไป) · การจับคู่นี้ใช้กับไฟล์นี้อย่างเดียว — ขอให้ผู้มีสิทธิ์บันทึกไว้ครั้งหน้า");
        return (true, null);
    }

    /// <summary>
    /// **ผู้สมัครเอกสารขาย/ยอดค้างของบรรทัดรอบโอน เห็นได้ไหม** (review198-D D-P5 · รอบ 200 คำตัดสินข้อ 14)
    ///
    /// <para><c>GET batches/{id}</c> (สิทธิ์ <see cref="View"/>) เคยคืน <c>MatchCandidates</c> (เลขที่เอกสารขาย + ยอดค้าง) และ <c>MatchNote</c>
    /// (ข้อความที่อ้างเลขที่/ยอดของผู้สมัคร) ของทุกบรรทัด ⇒ บทบาทที่ถูกจำกัดการดูเอกสารรายได้เห็นยอดค้างลูกหนี้ผ่านทางนี้ · ผู้สมัครมีไว้ให้
    /// "ตัดสินการจับคู่" ซึ่งต้องใช้ <see cref="Import"/> อยู่แล้ว ⇒ เห็นได้เมื่อมี <see cref="Import"/> หรือ <see cref="Post"/> (ผู้ลงบัญชีต้องตรวจ
    /// ว่าจับคู่ถูกก่อนลง) · ไม่มีทั้งสอง ⇒ ซ่อน + บอกเหตุผล (ไม่ใช่รายการว่างเงียบ ๆ ที่อ่านได้ว่า "ไม่มีผู้สมัคร")</para>
    /// </summary>
    /// <returns><c>null</c> = เห็นได้ · ข้อความ = เหตุผลที่ซ่อน (หน้าเว็บ/ผู้เรียก API แสดงแทนผู้สมัคร)</returns>
    public static string? CandidatesHiddenReason(bool canImport, bool canPost) =>
        canImport || canPost
            ? null
            : $"ผู้สมัครเอกสารขายและยอดค้างของแต่ละบรรทัดแสดงเฉพาะผู้มีสิทธิ์ \u201C{PermissionKeys.LabelOf(Import)}\u201D "
              + $"หรือ \u201C{PermissionKeys.LabelOf(Post)}\u201D (ข้อมูลลูกหนี้) — สถานะการจับคู่ยังดูได้ตามปกติ";

    /// <summary>ซ่อนผู้สมัคร/ข้อความที่อ้างผู้สมัครของทุกบรรทัด (<see cref="CandidatesHiddenReason"/> ไม่เป็น <c>null</c>) — สถานะ/เอกสารที่จับคู่แล้ว
    /// (<c>MatchedDocumentId</c>) คงไว้ เพราะเป็นผลที่ลงแล้ว ไม่ใช่รายชื่อยอดค้าง</summary>
    public static SettlementBatchView HideCandidates(SettlementBatchView view, string reason) =>
        view with
        {
            Lines = view.Lines
                .Select(l => l with
                {
                    MatchCandidates = Array.Empty<SettlementMatchCandidateView>(),
                    MatchNote = l.MatchNote is null ? null : reason,
                })
                .ToList(),
        };
}
