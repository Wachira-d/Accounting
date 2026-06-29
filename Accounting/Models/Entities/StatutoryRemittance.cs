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

    /// <summary>เงินเพิ่ม/ค่าปรับนำส่งช้า (ปกส. §49 2%/เดือน ฯลฯ)</summary>
    public decimal LateFee { get; set; }

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
}
