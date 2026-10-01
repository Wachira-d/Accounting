namespace Accounting.Models.Entities;

/// <summary>
/// วันหยุดราชการ <b>ระดับแพลตฟอร์ม</b> (ทุกบริษัทใช้ร่วม) — ใช้เลื่อนกำหนดยื่นแบบภาษี (ป.พ.พ. §193/8) และนับวันทำการ (§87) ·
/// รอบ 201 ทีม PL (B-9)
///
/// <para>ต่างจาก <see cref="PublicHoliday"/> (ของเงินเดือน · รายบริษัท · รวมวันหยุดบริษัทเอง) — กำหนดยื่นแบบต่อกรมสรรพากรขึ้นกับวันหยุดราชการ
/// ไม่ขึ้นกับวันหยุดของบริษัท · ข้อมูลมาจากประกาศ ครม./สำนักนายกฯ รายปี = <b>ข้อมูลภายนอก</b> แอดมินกรอกเอง (ระบบไม่แต่ง) ·
/// ตารางว่าง = พฤติกรรมเดิม (เลื่อนเฉพาะเสาร์/อาทิตย์)</para>
/// </summary>
public class PlatformHoliday : BaseEntity
{
    /// <summary>วันหยุด (เก็บเฉพาะวันที่ · ไม่ซ้ำในแถวที่ยังไม่ถูกลบ)</summary>
    public DateTime Date { get; set; }
    public string NameTh { get; set; } = "";
    public string? NameEn { get; set; }

    /// <summary>"Public" (นักขัตฤกษ์/ราชการ) · "Substitute" (ชดเชย) · "Special" (วันหยุดพิเศษตามมติ ครม.)</summary>
    public string Kind { get; set; } = "Public";

    /// <summary>ที่มา — เช่น เลขประกาศ/มติ ครม. (ให้ตรวจย้อนได้)</summary>
    public string? SourceReference { get; set; }
}
