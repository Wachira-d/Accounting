using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **คะแนนความมั่นใจของหนึ่งแถวนำเข้า (0–1)** — ตัวเดียวของศูนย์นำเข้าข้อมูล (รอบ 205 ทีม B · IMPORT_ETL_REVIEW §2.3)
///
/// <para>ใช้ระบายสี/เรียงแถวในหน้า review (&lt; <see cref="ReviewThreshold"/> = เหลือง · กฎเหล็ก #3 ข้อ 3) — <b>ไม่ใช่ด่าน</b>: แถวที่คะแนนต่ำยังลงได้
/// เมื่อผู้ใช้ยืนยัน (คำตัดสินข้อ 150) · สูตรตั้งใจให้อธิบายได้ทีละข้อ: เริ่มจากความมั่นใจของการจับคู่คอลัมน์ที่แถวนี้ใช้ → issue ระดับ Error ตัดเหลือ ≤ 0.3 ·
/// คำเตือนละ −0.1 · ค่าที่ระบบเติมเอง (ไฟล์ว่าง) ละ −0.05 · จับคู่ข้อมูลเดิมได้ไม่เต็ม ⇒ ไม่เกินคะแนนจับคู่</para>
/// <para>G6: pure · ไม่ throw · ปัด 4 ตำแหน่งแบบ AwayFromZero</para>
/// </summary>
public static class ImportRowConfidence
{
    /// <summary>ต่ำกว่านี้ = ให้ผู้ใช้ดู (เหลือง) — ตัวเลขเดียวกับเกณฑ์ short-circuit ของกฎเหล็ก #1 ข้อ 6</summary>
    public const decimal ReviewThreshold = 0.85m;

    /// <param name="mappingConfidence">ค่าเฉลี่ยความมั่นใจของคอลัมน์ที่แถวนี้มีค่า (0–1) · ไม่มีคอลัมน์ = 1 (ไม่มีอะไรให้สงสัย)</param>
    /// <param name="issues">issue ของแถว (ทุกแหล่ง) — Info ไม่หักคะแนน</param>
    /// <param name="matchScore">คะแนนจับคู่ข้อมูลเดิม (null = สร้างใหม่ ไม่เกี่ยว)</param>
    /// <param name="autoFilledCells">จำนวนช่องที่ระบบเติมเองเพราะไฟล์ว่าง</param>
    public static decimal Compute(decimal mappingConfidence, IReadOnlyList<ImportIssueDto> issues, decimal? matchScore = null, int autoFilledCells = 0)
    {
        var c = Math.Clamp(mappingConfidence, 0m, 1m);
        if (issues.Any(i => i.Severity == ImportIssueSeverity.Error)) c = Math.Min(c, 0.3m);
        c -= 0.1m * issues.Count(i => i.Severity == ImportIssueSeverity.Warning);
        c -= 0.05m * Math.Max(0, autoFilledCells);
        if (matchScore is decimal m && m < 1m) c = Math.Min(c, Math.Clamp(m, 0m, 1m));
        return Math.Round(Math.Clamp(c, 0m, 1m), 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>คะแนนนี้ต้องให้ผู้ใช้ดูก่อนไหม (เหลือง)</summary>
    public static bool BelowReviewThreshold(decimal confidence) => confidence < ReviewThreshold;
}
