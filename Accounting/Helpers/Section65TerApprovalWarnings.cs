using Accounting.Models.Enums;
using Accounting.Services.Implementations.Tax;

namespace Accounting.Helpers;

/// <summary>
/// **§65 ตรี ตอนอนุมัติ — ตัวตัดสินตัวเดียวว่า "ใบไหนเข้าข่ายประเมิน" และ "ผลข้อไหนต้องยกขึ้นให้ผู้อนุมัติเห็นก่อนกด"** (รอบ 201 ทีม TX · A-TX1)
///
/// ═══ ที่มา (team-R B-05(b)) ═══
/// <para><c>ApplySection65TerAsync</c> ประเมินรายจ่ายต้องห้ามใน<b>ธุรกรรมอนุมัติ</b> — หลังด่านคำเตือน ⇒ ผู้อนุมัติกด "อนุมัติ" โดยไม่เคยเห็นว่า
/// ระบบจะบวกกลับ ภ.ง.ด.50 กี่บาท (ค่าปรับ · ค่ารับรองเกินเพดาน · รายจ่ายส่วนตัว) · ผลไปโผล่ครั้งแรกตอนปิดรอบ</para>
///
/// ═══ วัดก่อนเปิด (F2 ข้อ 8 — คำเตือนที่ฟ้องใบถูกทุกใบ = ปิดด่านโดยไม่ตั้งใจ) ═══
/// <para>ผลของตัวตรวจแบ่งสามกลุ่ม: (ก) <b>บวกกลับจริง</b> (ยอด &gt; 0) — ผู้อนุมัติต้องเห็นเสมอ · (ข) <b>ต้องให้นักบัญชียืนยัน</b> ที่เกิดเฉพาะใบผิดปกติ
/// (capex · เงินบริจาค · ผู้เกี่ยวโยง · รายจ่ายรอบก่อน · ค่ารับรองที่ยังไม่มีฐานเพดาน) — เห็นเสมอ · (ค) <b>บันทึกอย่างเดียว</b> — ข้อที่ฟ้องใบปกติจำนวนมาก
/// (ผู้รับเงินไม่มีเลขผู้เสียภาษี = ค่าใช้จ่ายเงินสดรายย่อยทุกใบ · ไม่มีเลขใบกำกับ/ไฟล์แนบ = ใบคีย์มือส่วนใหญ่ · หลักฐานการจ่าย = ยังไม่ได้ส่งข้อมูล)
/// ยังอยู่ใน <c>NonDeductibleRuleJson</c> เหมือนเดิม แต่ไม่ขัดจังหวะการอนุมัติ · ชุดเทสต์ <c>Section65TerApprovalWarningGoldenTests</c>
/// ล็อกว่าใบปกติ 0 คำเตือน และใบที่มีรายจ่ายต้องห้ามได้คำเตือนทุกใบ</para>
/// <para>ข้อที่ <b>บล็อก</b> (HardBlock) ไม่อยู่ในรายการนี้ — ยังโยนในธุรกรรมอนุมัติพร้อมข้อความของมันเอง (รับทราบคำเตือนก็ผ่านไม่ได้)</para>
/// </summary>
public static class Section65TerApprovalWarnings
{
    /// <summary>คำนำหน้าคำเตือนชุดนี้ (ให้คน/เทสต์แยกออกจากคำเตือนชนิดอื่นได้)</summary>
    public const string Prefix = "ℹ️ รายจ่ายต้องห้าม §65 ตรี";

    /// <summary>ผลที่บันทึกบนเอกสารอย่างเดียว ไม่ยกขึ้นตอนอนุมัติ (ฟ้องใบปกติจำนวนมาก — เหตุผลอยู่ใน summary ของคลาส)</summary>
    private static readonly HashSet<string> RecordOnlyRules = new(StringComparer.Ordinal)
    {
        "RD-65ter(11)(18)",   // ไม่มีเลขผู้เสียภาษีผู้รับ (ไม่บล็อก) — ค่าใช้จ่ายเงินสดรายย่อย
        "RD-65ter(9)",        // ไม่มีเลขใบกำกับผู้ขาย/ไฟล์แนบ — ใบคีย์มือ
        "RD-65ter(8)",        // หลักฐานการจ่าย — ผู้เรียกยังไม่ส่งข้อมูล
        // ฝ่ายค้านรอบ 201 RTX-1/RTX-2 + คำตัดสินข้อ 110: (5) capex ฟ้องทุกบรรทัด ≥ 50,000 ผัง 5xxxx (ต้นทุนสินค้า/วัตถุดิบ/เหมาช่วง/ค่าเช่า) ·
        // (4) ค่ารับรองคิดเพดานต่อรอบบัญชีที่ตัวรวม ภ.ง.ด.50 ตอนปิดรอบ (ฐานรายได้ YTD ต้นปียังต่ำ = ฟ้องผิด) ⇒ บันทึกอย่างเดียว
        "RD-65ter(5)",
        "RD-65ter(4)",
    };

    /// <summary>
    /// **ใบชนิดนี้ต้องประเมิน §65 ตรี ไหม** — ตัวเดียวของด่านอนุมัติ (ธุรกรรม) และตัวรวบรวมคำเตือน · ฝั่งซื้อ/ค่าใช้จ่ายที่กระทบกำไรสุทธิ ·
    /// ใบรับรองแทนใบเสร็จ (CIL) ที่แปลงมาจากใบตั้งหนี้ (<paramref name="relatedDocumentId"/> มีค่า) ไม่ประเมินซ้ำ (ใบต้นทางบวกกลับไปแล้ว)
    /// </summary>
    public static bool AppliesTo(DocumentType type, Guid? relatedDocumentId)
        => type is DocumentType.PurchaseInvoice or DocumentType.Expense or DocumentType.PaymentVoucher
           || (type == DocumentType.CertificateInLieu && relatedDocumentId == null);

    /// <summary>ผลข้อนี้ต้องยกขึ้นให้ผู้อนุมัติเห็นไหม</summary>
    private static bool Surfaces(Section65TerValidator.Finding f)
        => !f.HardBlock && !RecordOnlyRules.Contains(f.RuleCode) && (f.AddBackAmount > 0m || f.NeedsConfirmation);

    /// <summary>
    /// **คำเตือนนี้เป็นของชุด §65 ตรีไหม** — ตัวเดียวที่ <c>ApprovalAcknowledgement</c> ใช้แยกชุดนี้ออกจากคำเตือนชนิดอื่น (คำตัดสินข้อ 110:
    /// ทางเข้าที่ไม่มีคนกดรับทราบ (API v1 · ใบประจำ · ใบเบิก · OCR/LINE อัตโนมัติ) ไม่ถูกหยุดด้วยชุดนี้ — ผ่านแล้วทิ้งร่องรอย · หน้าเว็บ/มือถือยังต้องรับทราบ)
    /// </summary>
    public static bool IsWarning(string warning)
        => warning.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>ข้อความคำเตือนก่อนอนุมัติจากผลประเมิน (ลำดับตามผลของตัวตรวจ · ว่าง = ไม่มีอะไรต้องให้เห็น)</summary>
    public static IReadOnlyList<string> For(Section65TerValidator.Result result)
        => result.Findings.Where(Surfaces).Select(Text).ToList();

    private static string Text(Section65TerValidator.Finding f)
        => f.AddBackAmount > 0m
            ? $"{Prefix} ({f.LegalReference}) {f.Message} — เมื่ออนุมัติ ระบบจะบันทึกยอดบวกกลับใน ภ.ง.ด.50 {f.AddBackAmount:N2} บาท "
              + $"(ไม่บล็อก · ถ้าไม่ใช่รายจ่ายต้องห้าม แก้คำอธิบาย/ผังบัญชีของบรรทัดก่อนอนุมัติ) [{f.RuleCode}]"
            : $"{Prefix} ({f.LegalReference}) {f.Message} (ไม่บล็อก — ตรวจก่อนอนุมัติ) [{f.RuleCode}]";
}
