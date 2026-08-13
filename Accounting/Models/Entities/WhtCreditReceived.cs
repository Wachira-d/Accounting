using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// ทะเบียน "หนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ) ที่<b>เราได้รับ</b>"
/// — ภาษีที่ลูกค้าหักจากเราตอนจ่ายเงิน ใช้เป็น<b>เครดิตภาษี</b>ในแบบ ภ.ง.ด.51/50
///
/// <para><b>ทำไมไม่ใช้ <see cref="WithholdingTaxCert"/> เดิม:</b> คนละเอกสารทาง
/// กฎหมาย — ของเดิมคือใบที่ <i>เราออกให้ผู้อื่น</i> (เลขรันของเรา ต้องพิมพ์ ผูกกับ
/// ภ.ง.ด.3/53 ที่เรานำส่ง = หนี้ที่ต้องจ่ายสรรพากร) ส่วนตัวนี้คือใบที่
/// <i>ผู้อื่นออกให้เรา</i> (เลขเป็นของเขา เราแค่บันทึก+เก็บไว้ ผูกกับ ภ.ง.ด.50/51
/// = สินทรัพย์ที่เอาไปหักภาษี) ถ้ายัดรวมเป็นตารางเดียวแล้วแยกด้วย flag ทุก query
/// และทุกรายงานจะต้องจำใส่เงื่อนไขทิศทางเอง ลืมเมื่อไรตัวเลขผิดเงียบ ๆ
/// (บทเรียนเดียวกับใบลดหนี้ที่ใช้ชนิดเดียวรับสองฝั่ง)</para>
///
/// <para><b>กฎที่คุมตัวเลข:</b> เครดิตภาษีได้เฉพาะเมื่อ<b>มีหนังสือรับรองจริง</b>
/// (สถานะ Received/Claimed) — ยอดที่ยังรอใบ (Pending) เป็นเพียงยอดค้างใน 11910
/// ที่ยัง<b>เครดิตไม่ได้</b> ต้องตามทวงจากลูกค้าก่อน</para>
/// </summary>
public class WhtCreditReceived : TenantEntity
{
    /// <summary>ปีภาษี (ค.ศ.) ที่ใช้เครดิตนี้ — ต้องเป็นปีที่รับรู้เงินได้นั้น</summary>
    public int TaxYear { get; set; }

    /// <summary>เลขที่บนหนังสือรับรองของผู้จ่าย (ไม่ใช่เลขของเรา) —
    /// null ตอนสถานะ Pending (ยังไม่ได้รับใบ)</summary>
    public string? CertificateNumber { get; set; }
    public DateTime? CertificateDate { get; set; }

    /// <summary>ลูกค้าที่หักภาษีเรา — nullable เผื่อผู้จ่ายไม่มีใน Contacts</summary>
    public Guid? PayerContactId { get; set; }
    public Contact? PayerContact { get; set; }

    /// <summary>Snapshot ชื่อ/เลขผู้เสียภาษีของผู้จ่าย ณ วันที่บนหนังสือรับรอง —
    /// contact อาจถูกแก้ภายหลัง แต่ทะเบียนภาษีย้อนหลังต้องคงข้อมูลตามใบจริง</summary>
    public string PayerName { get; set; } = "";
    public string? PayerTaxId { get; set; }

    /// <summary>แบบที่ "ผู้จ่าย" ใช้ยื่น (ภ.ง.ด.3 = จ่ายบุคคล / ภ.ง.ด.53 = จ่ายนิติบุคคล)
    /// — เราเป็นนิติบุคคล ปกติจึงเป็น ภ.ง.ด.53</summary>
    public WhtPayerFormType PayerFormType { get; set; } = WhtPayerFormType.Pnd53;

    /// <summary>ประเภทเงินได้ตามมาตรา 40 (เช่น "40(2)", "40(8)") — ใช้ตรวจว่าอัตรา
    /// ที่ถูกหักตรงกับ ท.ป.4/2528 หรือไม่</summary>
    public string? IncomeTypeCode { get; set; }

    public decimal IncomeAmount { get; set; }
    public decimal WhtRate { get; set; }

    /// <summary>ภาษีที่ถูกหัก = ยอดที่นำไปเครดิต</summary>
    public decimal WhtAmount { get; set; }

    public WhtCreditStatus Status { get; set; } = WhtCreditStatus.Pending;

    /// <summary>เอกสารขายที่ทำให้เกิดการถูกหัก (ใบเสร็จ/ใบกำกับ/ใบแจ้งหนี้)</summary>
    public Guid? DocumentId { get; set; }
    public Document? Document { get; set; }

    /// <summary>รายการรับชำระที่ทำให้เกิดการถูกหัก (เกณฑ์เงินสด)</summary>
    public Guid? PaymentId { get; set; }

    /// <summary>ไฟล์สแกนหนังสือรับรอง — ต้องเก็บ 5 ปี (พ.ร.บ.บัญชี ม.10)</summary>
    public Guid? AttachmentId { get; set; }

    /// <summary>ใช้เครดิตไปกับแบบไหนแล้ว (ภ.ง.ด.51 หรือ 50) — กันใช้ซ้ำ
    /// ระหว่างแบบครึ่งปีกับแบบสิ้นปี</summary>
    public Guid? ClaimedInTaxReportId { get; set; }
    public DateTime? ClaimedAt { get; set; }

    /// <summary>ปีที่ยกเครดิตคงเหลือมาจากปีก่อน (null = เกิดในปีนี้เอง)</summary>
    public int? CarriedFromTaxYear { get; set; }

    public string? Notes { get; set; }
}
