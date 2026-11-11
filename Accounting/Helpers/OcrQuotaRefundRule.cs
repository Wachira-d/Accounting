namespace Accounting.Helpers;

/// <summary>
/// **คืนโควตาหน้า OCR ที่หักล่วงหน้าเมื่อไร** — ตัวตัดสินตัวเดียวของทางเข้าอัปโหลด (เว็บ + LINE)
///
/// <para>═══ ที่มา (ฝ่ายค้านด่านไฟล์ซ้ำ 2026-10-09 · D1) ═══ เดิมสองทางเข้าเขียนกติกาเองคนละที่ว่า
/// "ไฟล์ซ้ำ ⇒ คืนโควตา" เพราะไฟล์ซ้ำเคยหมายถึง "คัดลอกผลเดิม ไม่ได้เรียก engine" เสมอ · เมื่อด่านไฟล์ซ้ำแยก
/// "ไฟล์ซ้ำ" ออกจาก "ใช้ผลเดิม" (<see cref="OcrDuplicateReusePolicy"/>) ไฟล์ซ้ำที่ถูก<b>อ่านใหม่</b>ด้วย Azure
/// ก็ยังถูกคืนโควตา ⇒ หน้า Azure ฟรีทุกครั้งที่อัปไฟล์เก่าซ้ำ (ทุกไฟล์ก่อน deploy มีรุ่น NULL = อ่านใหม่หมด)</para>
///
/// <para>═══ กติกา ═══ คืนเมื่อ "ไม่ได้ใช้ engine จริง": สแกนไม่สำเร็จ · อ่านจาก e-Tax XML ที่ฝังในไฟล์ ·
/// คัดลอกผลเดิม (engine = Cached) — <b>ไม่ใช่</b>เพราะติดธงไฟล์ซ้ำ</para>
/// </summary>
public static class OcrQuotaRefundRule
{
    public const string CompletedStatus = "Completed";

    /// <param name="scanStatus">สถานะสแกนที่ตอบกลับ</param>
    /// <param name="ocrEngine">engine ที่ประทับบนสแกน (null = ไม่ทราบ ⇒ ถือว่าใช้จริง ไม่คืน)</param>
    public static bool ShouldRefund(string? scanStatus, string? ocrEngine)
    {
        if (!string.Equals(scanStatus, CompletedStatus, StringComparison.Ordinal)) return true;
        if (string.Equals(ocrEngine, OcrDuplicateReusePolicy.EtaxXmlEngine, StringComparison.Ordinal)) return true;
        if (string.Equals(ocrEngine, OcrDuplicateReusePolicy.CachedEngine, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>รอบ 258 (C-H3): เหตุที่ "ไม่คิดค่าบริการ/ไม่เสียโควตา" ของสแกนนี้ (ข้อความถึงผู้เรียก API) · null = ใช้เครื่องอ่านจริงและสำเร็จ ⇒ คิดตามปกติ ·
    /// ตัดสินด้วย <see cref="ShouldRefund"/> ตัวเดียว (เส้นเว็บ · LINE · API v1 ใช้กติกาเดียวกัน)</summary>
    public static string? NotBilledReason(string? scanStatus, string? ocrEngine)
    {
        if (!ShouldRefund(scanStatus, ocrEngine)) return null;
        if (!string.Equals(scanStatus, CompletedStatus, StringComparison.Ordinal))
            return "สแกนไม่สำเร็จ — ไม่มีการคิดค่าบริการ";
        return string.Equals(ocrEngine, OcrDuplicateReusePolicy.EtaxXmlEngine, StringComparison.Ordinal)
            ? "อ่านจากไฟล์ e-Tax XML (ไม่ได้ใช้เครื่องอ่าน) — ไม่มีการคิดค่าบริการ"
            : "ไฟล์นี้เคยสแกนแล้ว (ใช้ผลเดิม) — ไม่มีการคิดค่าบริการ";
    }
}
