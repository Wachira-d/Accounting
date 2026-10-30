namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 214 ต่อ (คำตัดสินข้อ 173 ฐ) — คำขอลบข้อมูลส่วนบุคคล (PDPA ม.33) ที่ผู้ใช้ยื่นเอง<b>ระดับแพลตฟอร์ม</b> (ผู้ควบคุมข้อมูล = ผู้ให้บริการ) —
/// ใช้ได้ทั้งผู้ใช้ที่ไม่มีบริษัท · ไม่ใช่ <see cref="PdpaDataSubjectRequest"/> (นั่นเป็นของ tenant · FK → Companies) ·
/// SystemAdmin ดำเนินการที่ <c>/admin/legal.html</c> แท็บคำขอลบ → แกนลบตัวเดียว <c>PdpaService.AnonymiseUserCoreAsync</c> ·
/// SLA 30 วัน (<see cref="DueAt"/>) · ค้างได้ฉบับเดียวต่อผู้ใช้ (unique ระดับฐาน)
/// </summary>
public class PlatformErasureRequest : BaseEntity
{
    public Guid UserId { get; set; }
    /// <summary>อีเมล ณ ตอนยื่น — ใช้แจ้งผลหลังลบ (ถูกล้างเป็นอีเมลนิรนามเมื่อดำเนินการเสร็จ)</summary>
    public string RequesterEmail { get; set; } = "";
    public DateTime RequestedAt { get; set; }
    public DateTime DueAt { get; set; }
    /// <summary>"Pending" | "Completed" | "Rejected" (<c>PlatformErasureRequestPolicy</c>)</summary>
    public string Status { get; set; } = "Pending";
    public string? Reason { get; set; }
    public string? RequesterIp { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public string? ResolutionNote { get; set; }
}
