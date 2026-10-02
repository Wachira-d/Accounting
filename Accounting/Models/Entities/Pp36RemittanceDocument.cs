namespace Accounting.Models.Entities;

/// <summary>
/// <b>ใบ ↔ รายการนำส่ง ภ.พ.36</b> — ใบเอกสารบริการต่างประเทศ (§83/6) ใบหนึ่งถูกนับในการนำส่งได้ครั้งเดียว
/// (รอบ 203 ทีม F3 · คำตัดสินข้อ 133/134 · PP36_REVIEW E-5/E-6)
///
/// <para>═══ ทำไมต้องมี ═══ เดิมการนำส่งผูกกับ "งวด" อย่างเดียว (unique CompanyId+Type+ปี+เดือน) ⇒ ใบที่อนุมัติหลังนำส่งงวดนั้นแล้ว
/// นำส่งเพิ่มไม่ได้ (ด่านกันซ้ำ "งวดนี้นำส่งแล้ว") แต่กลับถูก "รับรู้ภาษีซื้อ" ไปเคลม ภ.พ.30 ได้ = เคลม VAT ที่ไม่เคยนำส่ง ·
/// และยกเลิก/ปลดธงใบที่นำส่งแล้วได้เงียบ ๆ เพราะไม่มีอะไรบอกว่าใบไหนอยู่ในการนำส่งครั้งไหน</para>
///
/// <para>กติกา: (1) ใบหนึ่งมีแถวที่ยังไม่ลบได้แถวเดียว (unique index) · (2) <see cref="VatAmount"/> = ยอด Cr 21912 ของใบใน GL ที่มีผล ณ วันนำส่ง
/// (บาท) — ไม่ใช่ <c>Document.VatAmount</c> (สกุลเอกสาร) · (3) รับรู้ภาษีซื้อได้เฉพาะใบที่มีแถวนี้แล้ว · (4) มีแถวนี้แล้ว ⇒ ยกเลิก/ปลดธง/แก้ยอด
/// ถูกบล็อก (<c>Helpers/Pp36Lifecycle.ChangeBlockMessage</c>)</para>
/// </summary>
public class Pp36RemittanceDocument : TenantEntity
{
    /// <summary>รายการนำส่ง (<see cref="StatutoryRemittance"/> ชนิด VatPp36) ที่นับใบนี้</summary>
    public Guid StatutoryRemittanceId { get; set; }

    /// <summary>เอกสารบริการต่างประเทศที่ถูกนับ</summary>
    public Guid DocumentId { get; set; }

    /// <summary>งวด ภ.พ.36 ของใบ (เดือนที่จ่ายค่าบริการ — <c>ForeignServiceVat.Pp36PeriodDate</c>)</summary>
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }

    /// <summary>ภาษีที่นำส่งสำหรับใบนี้ (บาท) = Cr 21912 ของใบใน GL ณ วันนำส่ง</summary>
    public decimal VatAmount { get; set; }

    /// <summary>JE รับรู้ภาษีซื้อ (Dr 11610 / Cr 11640) ที่ย้ายภาษีซื้อของใบนี้แล้ว — null = ยังไม่รับรู้</summary>
    public Guid? RecognizedJournalEntryId { get; set; }

    /// <summary>ยอดที่ย้ายออกจาก 11640 ตอนรับรู้ (บาท) — 0 ได้ (บริษัทไม่จด VAT / บรรทัดภาษีซื้อต้องห้าม)</summary>
    public decimal RecognizedAmount { get; set; }
}
