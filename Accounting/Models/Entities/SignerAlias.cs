namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 278 (ฝ่ายค้าน HIGH-2) — ชื่อเล่นของสมาชิก<b>ต่อบริษัท</b> ที่ระบบภายนอกใช้เรียกในช่องผู้จัดทำ (เช่น "K.นัท") · กุญแจผูกช่องลายเซ็น
/// (<c>Helpers/SignatureSlotIdentity</c>) <b>มีผลเมื่อเจ้าของบริษัทอนุมัติแล้วเท่านั้น</b> · ยกเลิก/ปฏิเสธ = ประทับ <see cref="RevokedAt"/> (ไม่ลบ — ประวัติ) ·
/// ทุกการเพิ่ม/อนุมัติ/ยกเลิกเข้า audit อัตโนมัติ (TenantEntity)
/// </summary>
public class SignerAlias : TenantEntity
{
    public Guid UserId { get; set; }
    public string Alias { get; set; } = null!;
    /// <summary>รูปเทียบ (<c>SignatureSlotIdentity.NormalizeName</c>) — ตรงทั้งคำเท่านั้น</summary>
    public string NormalizedAlias { get; set; } = null!;
    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
}
