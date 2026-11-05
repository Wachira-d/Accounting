namespace Accounting.Helpers;

/// <summary>รอบ 238 ฝ่ายค้าน N1 — กดสร้างไฟล์ยื่นแบบ e-Filing ซ้ำ ไม่เก็บ <c>EFilingExports.FileContent</c> ซ้ำไม่จำกัด
/// <para>ที่มา: รอบ 238 ยอมให้ <c>POST …/tax/e-filing/{form}</c> ผ่านด่าน "หมดอายุ" (บริษัทอ่านอย่างเดียวต้องยื่นภาษีได้) ⇒ เส้นนี้เป็นทางเขียนเดียวที่เหลือของบริษัทหมดอายุ
/// และเก็บเนื้อไฟล์เต็มทุกครั้ง · กติกา: ไฟล์ฟอร์ม/งวดเดียวกัน <b>เนื้อหาเหมือนเดิมทุกตัวอักษร</b> ภายใน <see cref="WindowMinutes"/> นาที ⇒ คืนแถวเดิม (ไม่เพิ่มแถว) ·
/// ใช้กับทุกบริษัท (ไม่ผูกกับสถานะแพ็กเกจ — service ไม่รู้สถานะด่าน · ไฟล์ที่เหมือนกันทุกตัวอักษรภายในไม่กี่นาทีไม่มีคุณค่าทางหลักฐานเพิ่ม) · เนื้อต่าง (ข้อมูลเปลี่ยน) ⇒ แถวใหม่เสมอ ·
/// ผู้เรียก: <c>TaxService.GenerateEFilingAsync</c></para></summary>
public static class EFilingExportDedupe
{
    public const int WindowMinutes = 10;

    /// <summary>ใช้แถวเดิมแทนการเพิ่มแถวใหม่ได้ไหม</summary>
    public static bool ReuseExisting(string? existingContent, DateTime existingGeneratedAtUtc, string newContent, DateTime nowUtc)
        => existingContent != null
           && string.Equals(existingContent, newContent, StringComparison.Ordinal)
           && nowUtc - existingGeneratedAtUtc <= TimeSpan.FromMinutes(WindowMinutes)
           && existingGeneratedAtUtc <= nowUtc.AddMinutes(1);
}
