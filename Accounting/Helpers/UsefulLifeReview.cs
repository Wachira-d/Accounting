using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>การทบทวนอายุใช้งาน/มูลค่าซาก/วิธีคิดค่าเสื่อมสิ้นรอบบัญชี (TFRS for NPAEs บทที่ 10) — ตัวตัดสินเดียวว่า
/// "สินทรัพย์ตัวนี้ทบทวนในรอบบัญชีนี้แล้วหรือยัง" (รอบ 201 ทีม IN · C-6 · คำตัดสินข้อ 79)
///
/// ═══ ที่มา ═══
/// <c>FixedAsset.UsefulLifeReviewedAt</c> มีผู้เขียน (ปุ่ม “ปรับอายุการใช้งาน”) แต่ไม่มีผู้อ่าน ⇒ ไม่มีอะไรเตือนตอนปิดปีว่ายังไม่ได้
/// ทบทวน ทั้งที่มาตรฐานกำหนดให้ทบทวนอย่างน้อยทุกสิ้นรอบ (ค่าตั้งที่ไม่มีผู้อ่าน = ไม่มีผล · F2 ข้อ 2)
///
/// ═══ กติกา ═══
/// • นับเฉพาะสินทรัพย์ที่ยังใช้งาน (<see cref="AssetStatus.Active"/>) และ<b>มี</b>อายุใช้งาน (ที่ดิน/งานระหว่างก่อสร้าง = ไม่คิดค่าเสื่อม ⇒ ไม่ต้องทบทวน)
/// • ซื้อ/ขึ้นทะเบียนในรอบบัญชีนี้ = ประมาณการตั้งในรอบนี้แล้ว ⇒ ไม่ต้องเตือน
/// • ทบทวนแล้ว = วันที่ทบทวน (ตามปฏิทินไทย) ตั้งแต่วันเริ่มรอบบัญชีนี้ — ทบทวนหลังวันสิ้นรอบก่อนปิดบัญชีก็นับ
/// • เตือนในรายการตรวจก่อนปิดงวดเฉพาะเดือนสุดท้ายของรอบบัญชี (เดือนอื่นไม่เตือน — คำเตือนที่ฟ้องทุกเดือน = ปิดด่านโดยไม่ตั้งใจ · F2 ข้อ 8)
/// </summary>
public static class UsefulLifeReview
{
    public const string RuleCode = "TFRS-NPAES-10-LIFE-REVIEW";
    public const string LegalReference = "TFRS for NPAEs บทที่ 10 (ที่ดิน อาคาร และอุปกรณ์ — ทบทวนอายุ/ซาก/วิธีคิดสิ้นรอบ)";

    /// <summary>เดือนสุดท้ายของรอบบัญชี (1–12) จากเดือนเริ่มรอบของบริษัท · ค่านอกช่วง = รอบปีปฏิทิน (ธ.ค.)</summary>
    private static int FiscalYearEndMonth(int fiscalYearStartMonth)
        => fiscalYearStartMonth is < 2 or > 12 ? 12 : fiscalYearStartMonth - 1;

    /// <summary>เดือนนี้คือเดือนสุดท้ายของรอบบัญชีหรือไม่</summary>
    public static bool IsFiscalYearEndMonth(int month, int fiscalYearStartMonth)
        => month == FiscalYearEndMonth(fiscalYearStartMonth);

    /// <summary>วันเริ่มรอบบัญชีที่มีเดือน (<paramref name="year"/>, <paramref name="month"/>) อยู่</summary>
    public static DateTime FiscalYearStart(int year, int month, int fiscalYearStartMonth)
    {
        var s = fiscalYearStartMonth is < 1 or > 12 ? 1 : fiscalYearStartMonth;
        return month >= s ? new DateTime(year, s, 1) : new DateTime(year - 1, s, 1);
    }

    /// <summary>สินทรัพย์ตัวนี้ยังไม่ได้ทบทวนในรอบบัญชีที่เริ่ม <paramref name="fiscalYearStart"/> หรือไม่</summary>
    /// <param name="reviewedAtUtc"><c>UsefulLifeReviewedAt</c> (เวลา UTC ที่ระบบประทับ)</param>
    public static bool NeedsReview(AssetStatus status, DepreciationMethod method, int usefulLifeMonths,
        DateTime purchaseDate, DateTime? reviewedAtUtc, DateTime fiscalYearStart)
    {
        if (status != AssetStatus.Active) return false;
        if (method == DepreciationMethod.None || usefulLifeMonths <= 0) return false;
        if (ThaiDate.CalendarDateUtc(purchaseDate).Date >= fiscalYearStart.Date) return false;
        if (reviewedAtUtc is not DateTime reviewed) return true;
        return ThaiDate.CalendarDateUtc(reviewed).Date < fiscalYearStart.Date;
    }
}
