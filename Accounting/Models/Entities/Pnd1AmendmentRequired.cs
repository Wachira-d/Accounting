namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 275 แพ็ก E ฝ่ายค้าน MED-3 — <b>รายการค้าง "งวดนี้ต้องยื่น ภ.ง.ด.1 เพิ่มเติม"</b> ที่เกิดจากการออกใบแทน 50 ทวิ ของงวดที่ยื่นแล้ว
/// (เครื่องมือ <c>Helpers/PayrollJunkPayee</c>) · ค้างจนกว่าผู้ใช้บันทึกว่ายื่นเพิ่มเติมแล้ว (<see cref="ResolvedAt"/>) · หนึ่งแถวต่อบริษัท × ปี × เดือนที่ยังค้าง
/// </summary>
public class Pnd1AmendmentRequired : TenantEntity
{
    public int TaxYear { get; set; }
    public int TaxMonth { get; set; }
    /// <summary>เหตุ (เช่น "แก้เลขผู้ถูกหักภาษีในหนังสือรับรอง")</summary>
    public string Reason { get; set; } = "";
    /// <summary>ใบเดิม → ใบแทน (ข้อความ คั่นด้วย " · ")</summary>
    public string? CertificateNumbers { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    /// <summary>เลขอ้างอิงการยื่นเพิ่มเติม / หมายเหตุ (บังคับตอนบันทึกว่ายื่นแล้ว)</summary>
    public string? ResolvedReference { get; set; }
}
