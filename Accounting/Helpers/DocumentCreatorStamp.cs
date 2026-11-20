namespace Accounting.Helpers;

/// <summary>
/// รอบ 276 (ฝ่ายค้าน LOW-10) — "ใครสร้างเอกสารใบนี้" เป็น id ผู้ใช้ (pure) · ผู้เรียก: <c>AccountingDbContext.StampDocumentCreators</c> (ทุกทางเข้า ณ จุดบันทึกเดียว)
/// </summary>
public static class DocumentCreatorStamp
{
    /// <summary><paramref name="createdBy"/> เป็น id ผู้ใช้ ⇒ id นั้น · ไม่ใช่ (ป้ายระบบ/อีเมล) แต่บันทึกในคำขอของผู้ใช้ที่ถูกจำกัด ⇒ ผู้ใช้คนนั้น
    /// (เอกสารที่ OCR/LINE สร้างให้เขา = ของเขา) · อื่น ๆ = null (อีเมลให้ผู้เรียกจับคู่สมาชิกต่อ)</summary>
    public static Guid? Resolve(string? createdBy, Guid? restrictedActorId)
    {
        if (Guid.TryParse(createdBy, out var id) && id != Guid.Empty) return id;
        return restrictedActorId;
    }

    public static bool LooksLikeEmail(string? s) => !string.IsNullOrWhiteSpace(s) && s.Contains('@') && !s.Contains(' ');
}
