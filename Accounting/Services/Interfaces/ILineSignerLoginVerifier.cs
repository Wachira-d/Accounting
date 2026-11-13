namespace Accounting.Services.Interfaces;

/// <summary>
/// รอบ 267 P5 (267-1-13) — ยืนยันตัวผู้ลงนามด้วยช่อง LINE Login ของระบบ (ตัวเดียวกับปุ่มเข้าสู่ระบบ — <c>AuthService</c>) ·
/// ใช้เพื่อ "จำข้อมูลผู้ลงนาม" เท่านั้น: ไม่ออก JWT · ไม่สร้างผู้ใช้ · ไม่ผูกปลายทาง OA ของบริษัท (userId คนละ Provider — ดีไซน์ §14.1)
/// </summary>
public interface ILineSignerLoginVerifier
{
    /// <summary>Channel ID ของ LINE Login ที่เปิดใช้ · null = ปิด/ไม่ได้ตั้ง (หน้าเอกสารไม่แสดงปุ่ม — ลงนามได้ตามปกติ)</summary>
    Task<string?> SignerLineChannelIdAsync();

    /// <summary>แลก authorization code ⇒ LINE userId ที่ LINE ยืนยันแล้ว · ไม่สำเร็จ = null</summary>
    Task<string?> ResolveSignerLineUserAsync(string code, string redirectUri, CancellationToken ct = default);
}
