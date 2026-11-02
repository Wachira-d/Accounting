namespace Accounting.Models.Entities;

/// <summary>
/// บันทึกการ "นำส่ง" ภาษี/ประกันสังคมต่องวด — สปส.1-10 (ประกันสังคม),
/// ภงด.1 (ภาษีเงินเดือน), ภงด.3 (หัก ณ ที่จ่ายบุคคล), ภงด.53 (นิติบุคคล),
/// ภพ.30 (VAT). 1 แถว = การนำส่ง 1 ประเภท 1 งวด (unique CompanyId+Type+Year+Month).
///
/// ยอด "รอนำส่ง" ของแต่ละงวด = หนี้ที่ระบบตั้งไว้ (จาก payroll run / เอกสารหัก
/// ณ ที่จ่าย / ภพ.30) − ที่นำส่งแล้วในตารางนี้. รองรับทั้งบริษัทที่รันเงินเดือน
/// ผ่านระบบ (ตั้งค้างจ่าย 21815 อัตโนมัติ) และทำเงินเดือนข้างนอกแล้วมาลงนำส่งเอง.
/// </summary>
public class StatutoryRemittance : TenantEntity
{
    /// <summary>ประเภทการนำส่ง: SsoSps110 / WhtPnd1 / WhtPnd3 / WhtPnd53 / VatPp30</summary>
    public string RemittanceType { get; set; } = null!;

    /// <summary>งวดภาษี (ค.ศ.) — ปีที่เกิดหนี้ ไม่ใช่ปีที่จ่าย</summary>
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }

    /// <summary>ยอดเงินสมทบ/ภาษีที่นำส่ง (ไม่รวมเงินเพิ่ม/ค่าปรับ)</summary>
    public decimal Amount { get; set; }

    /// <summary>เงินเพิ่มนำส่งช้า (ปกส. ม.49 2%/เดือน · ม.27/ม.89/1 1.5%/เดือน) — ยอดที่ลงบัญชีจริง (ผู้ใช้แก้ได้)</summary>
    public decimal LateFee { get; set; }

    /// <summary>รอบ 220 (คำตัดสินข้อ 181): เบี้ยปรับ (เช่น ภ.พ.30 ม.89 ที่ลดตามระเบียบ) — ยอดที่ลงบัญชีจริง · แถวเดิม = 0</summary>
    public decimal Penalty { get; set; }

    /// <summary>รอบ 220: ค่าปรับอาญา/ค่าปรับอื่น (เปรียบเทียบปรับ ม.35 ฯลฯ) — ระบบไม่เดา ผู้ใช้กรอก · แถวเดิม = 0</summary>
    public decimal OtherFine { get; set; }

    /// <summary>รอบ 220: เงินเพิ่ม/เบี้ยปรับที่ระบบ<b>เสนอ</b> ณ วันที่จ่าย (Helpers/LatePaymentPenaltyPolicy) — เก็บคู่ยอดจริงเพื่อให้ตรวจย้อนได้ว่าผู้ใช้แก้ไหม · null = แถวก่อนรอบ 220</summary>
    public decimal? SuggestedLateFee { get; set; }
    public decimal? SuggestedPenalty { get; set; }

    /// <summary>รอบ 220 ฝ่ายค้านข้อ 1: ผู้ใช้แจ้งว่ายื่นแบบกระดาษ (ค่าเสนอคิดจากวันกระดาษ) — แถวเดิม = false</summary>
    public bool PaperFiling { get; set; }

    public DateTime PayDate { get; set; }

    /// <summary>ผังบัญชี Cr (แหล่งเงินที่จ่ายออก) — เงินสด/ธนาคาร</summary>
    public Guid? BankGlAccountId { get; set; }

    /// <summary>JournalEntry ที่ post ตอนนำส่ง (Dr หนี้ค้างจ่าย / Cr ธนาคาร)</summary>
    public Guid? JournalEntryId { get; set; }

    /// <summary>(option) ใบสำคัญจ่ายที่ผูกกับการนำส่งนี้ — กรณีออกเป็นเอกสาร</summary>
    public Guid? DocumentId { get; set; }

    /// <summary>เลขรับจากหน่วยงาน (เลขรับ สปส.1-10 / เลขอ้างอิงยื่น ภงด./ภพ.30)</summary>
    public string? FilingNumber { get; set; }

    /// <summary>ไฟล์แนบใบเสร็จ/หลักฐานการนำส่ง (FileAttachment.Id)</summary>
    public Guid? ReceiptAttachmentId { get; set; }

    public string? Note { get; set; }

    /// <summary>รอบ 228 ฝ่ายค้าน S3 — "ชำระนอกระบบแล้ว": แถวนี้ผูกใบสำคัญ (JE) ที่ผู้ใช้ลงเองไว้ก่อน (<see cref="JournalEntryId"/>) ไม่ใช่ JE ที่เส้นนำส่งสร้าง ⇒
    /// ยกเลิกแถวนี้ = ปลดการผูกเท่านั้น <b>ไม่กลับรายการ</b> JE ของผู้ใช้ · แถวเดิม = false</summary>
    public bool SettledOutside { get; set; }
}
