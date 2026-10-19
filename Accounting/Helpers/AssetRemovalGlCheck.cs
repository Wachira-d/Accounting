using Accounting.Services.Implementations.Tax;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินเดียวว่า "ลบทะเบียนสินทรัพย์ที่ขึ้นจากเอกสารได้ไหม โดยแยกประเภทไม่ค้าง"
///
/// <para><b>ที่มา (ผู้ใช้รายงาน 2026-10-19):</b> ใบสำคัญจ่ายซื้ออุปกรณ์สำนักงาน 138.32 บาทลงผัง 12210
/// ⇒ ระบบขึ้นทะเบียนสินทรัพย์ให้อัตโนมัติ (NeedsReview) — ของชิ้นนั้นไม่ใช่สินทรัพย์เลย แต่ปุ่มลบบล็อกตลอด
/// "เพราะเอกสารต้นทางยังใช้งานอยู่" และทางเดียวที่บอกคือยกเลิกใบสำคัญจ่ายที่จ่ายเงินไปแล้วทั้งใบ.
/// ด่านเดิมดู <i>สถานะเอกสาร</i> แทนสิ่งที่มันอยากกันจริง คือ <i>ยอด Dr ผังสินทรัพย์ใน GL ที่จะลอยโดยไม่มีทะเบียน</i>
/// — ด่าน "เปลี่ยนผัง" ที่ย้ายบรรทัดไปค่าใช้จ่ายแล้วก็ยังลบไม่ได้ เพราะใบยังไม่ถูกยกเลิก.</para>
///
/// <para><b>กติกา:</b> อ่าน GL เป็นความจริง (ทุก JE ที่อ้างเอกสารนี้ — ใบหลัก + ใบปรับปรุง/ย้ายผัง) ·
/// ลบได้เมื่อยอด Dr สุทธิบนผังสินทรัพย์นั้น ≤ ต้นทุนของสินทรัพย์ตัวอื่นที่ขึ้นจากใบเดียวกันบนผังเดียวกัน
/// (= ไม่เหลือยอดที่ไม่มีทะเบียนรองรับ) · เหลือเกิน 1 สตางค์ = บล็อก และบอกยอดที่จะค้าง</para>
/// </summary>
public static class AssetRemovalGlCheck
{
    /// <summary>เศษปัดที่ยอมรับ (บาท) — ต้นทุนเฉลี่ยค่าประกอบอาจปัดต่างกัน 1 สตางค์</summary>
    public const decimal Tolerance = 0.01m;

    /// <summary>ยอดผังสินทรัพย์ใน GL ที่จะ "ไม่มีทะเบียนรองรับ" ถ้าลบสินทรัพย์ตัวนี้ (0 = ลบได้ไม่ค้าง)</summary>
    /// <param name="glNetDebitFromDocument">Σ(Dr−Cr) บนผังสินทรัพย์ของทุก JE ที่อ้างเอกสารต้นทาง</param>
    /// <param name="otherAssetsCostFromDocument">Σ ต้นทุนสินทรัพย์ตัวอื่นที่ขึ้นจากเอกสารเดียวกันบนผังเดียวกัน</param>
    public static decimal OrphanedIfRemoved(decimal glNetDebitFromDocument, decimal otherAssetsCostFromDocument)
    {
        var left = Math.Round(glNetDebitFromDocument - otherAssetsCostFromDocument, 2, MidpointRounding.AwayFromZero);
        return left > Tolerance ? left : 0m;
    }

    /// <summary>ย้ายบรรทัดจากผังที่ต้องขึ้นทะเบียนสินทรัพย์ → ผังที่ไม่ต้อง (ค่าใช้จ่าย ฯลฯ) — ทะเบียนที่ขึ้นจากบรรทัดนั้นต้องไปด้วย</summary>
    public static bool MovesOffAssetAccount(string? oldAccountCode, string? newAccountCode)
        => FixedAssetAccountClassifier.Resolve(oldAccountCode) != null
           && FixedAssetAccountClassifier.Resolve(newAccountCode) == null;
}
