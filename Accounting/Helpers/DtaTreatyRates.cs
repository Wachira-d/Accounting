namespace Accounting.Helpers;

/// <summary>ประเภทเงินได้ที่จ่ายไปต่างประเทศ — คีย์ของทั้ง ม.70 และตารางอนุสัญญาภาษีซ้อน (<see cref="DtaTreatyRates"/>)
/// <para>แยกตามวงเล็บของ ม.40 เพราะ ม.70 ครอบเฉพาะ 40(2)(3)(4)(5)(6) และอนุสัญญาแยกอัตราตามข้อ (กำไรธุรกิจ · ดอกเบี้ย · เงินปันผล · ค่าสิทธิ)</para></summary>
public enum ForeignIncomeCategory
{
    /// <summary>ไม่รู้ประเภทเงินได้ (ไม่ได้ระบุรหัส / รหัสที่ตารางไม่รู้จัก) — ห้ามเดา</summary>
    Unknown = 0,
    /// <summary>40(2) ค่าธรรมเนียม ค่านายหน้า — ในอนุสัญญาส่วนใหญ่อยู่ใต้ "กำไรธุรกิจ" เมื่อผู้รับไม่มีสถานประกอบการถาวรในไทย</summary>
    FeesCommission = 1,
    /// <summary>40(3) ค่าแห่งลิขสิทธิ์/ค่าสิทธิ</summary>
    Royalty = 2,
    /// <summary>40(4)(ก) ดอกเบี้ย</summary>
    Interest = 3,
    /// <summary>40(4)(ข) เงินปันผล</summary>
    Dividend = 4,
    /// <summary>40(5) ค่าเช่าทรัพย์สิน</summary>
    Rent = 5,
    /// <summary>40(6) วิชาชีพอิสระ</summary>
    Professional = 6,
    /// <summary>40(1) · 40(7) · 40(8) — <b>ม.70 ไม่ครอบ</b> (ไม่มีอัตรา ม.70 ให้ใช้) · การจำแนกค่าบริการ/ค่าโฆษณาที่จ่ายต่างประเทศว่าเป็น
    /// 40(8) จริงหรือเป็น 40(2)/(3) ต้องให้ผู้ทำบัญชีตัดสิน ระบบไม่เดา</summary>
    OutsideSection70 = 9,
}

/// <summary>แถว 1 แถวของตารางอนุสัญญา — <b>ต้องมีที่มาทุกช่อง</b></summary>
/// <param name="CountryIso2">ประเทศคู่สัญญา (ISO 3166-1 alpha-2 ตัวใหญ่ เช่น "SG")</param>
/// <param name="Category">ประเภทเงินได้ที่แถวนี้ครอบ</param>
/// <param name="RatePercent">อัตราสูงสุดที่ประเทศแหล่งเงินได้ (ไทย) เก็บได้ตามอนุสัญญา (%) · 0 = ไทยไม่มีสิทธิเก็บ (เช่น กำไรธุรกิจที่ไม่มีสถานประกอบการถาวร)</param>
/// <param name="Condition">เงื่อนไขของอัตรา (ข้อความไทย — ผู้รับเป็นเจ้าของผลประโยชน์ · ไม่มีสถานประกอบการถาวร · สัดส่วนถือหุ้น ฯลฯ)</param>
/// <param name="LegalReference">ชื่ออนุสัญญา (ปีที่ลงนาม) + ข้อ/วรรค เช่น "อนุสัญญาไทย–สิงคโปร์ 2558 ข้อ 12 วรรค 2(ก)"</param>
/// <param name="SourceUrl">หน้าเว็บทางการที่ใช้ยืนยัน (rd.go.th/หน่วยงานภาษีของประเทศคู่สัญญา) — ต้องเปิดอ่านตัวบทแล้ว ไม่ใช่สรุปของบุคคลที่สาม</param>
/// <param name="VerifiedOn">วันที่มีคนอ่านตัวบทแล้วยืนยันแถวนี้</param>
/// <param name="EffectiveFrom">อนุสัญญา (ฉบับที่อ้าง) มีผลกับเงินได้ที่จ่ายตั้งแต่วันนี้</param>
public sealed record DtaTreatyRate(
    string CountryIso2,
    ForeignIncomeCategory Category,
    decimal RatePercent,
    string Condition,
    string LegalReference,
    string SourceUrl,
    DateTime VerifiedOn,
    DateTime EffectiveFrom);

/// <summary>
/// **ตารางอัตราตามอนุสัญญาภาษีซ้อน (DTA) — OWNER file เดียวของระบบ** (คำตัดสินรอบ 200 ข้อ 13 · สืบจาก DECISION_AUDIT Q6)
///
/// <para>═══ กติกาของตาราง (ห้ามข้าม) ═══
/// <list type="bullet">
/// <item><b>ใส่เฉพาะแถวที่ยืนยันกับตัวบททางการได้</b> — ทุกแถวต้องมี <c>LegalReference</c> + <c>SourceUrl</c> + <c>VerifiedOn</c>.
/// แถวที่แต่งขึ้นหรือคัดลอกจากบทสรุปของสำนักงานบัญชี = ระบบหักภาษี<b>ขาด</b>เงียบ ๆ แล้ว ป.รัษฎากร §54 ให้<b>ผู้จ่าย</b>รับผิดในส่วนที่ขาด
/// (มองไม่เห็นจนถูกประเมิน) — อันตรายกว่าการไม่มีแถว</item>
/// <item><b>ไม่มีแถว ⇒ อัตรา ม.70</b> (15% · เงินปันผล 10%) — ไม่ใช่ 0 · ทิศนี้ "หักเกิน" ซึ่งผู้รับเห็นทันทีและทวงได้ (มองเห็น + แก้ทัน)</item>
/// <item>แถวจะมีผลก็ต่อเมื่อ<b>มีหนังสือรับรองถิ่นที่อยู่ (Certificate of Residence) ที่ครอบวันจ่าย</b> — ไม่มี CoR ⇒ อัตรา ม.70
/// (ตัดสินที่ <see cref="ForeignWhtRateResolver.Resolve"/> ที่เดียว)</item>
/// <item>อนุสัญญาฉบับใหม่แทนฉบับเก่า ⇒ เพิ่มแถวใหม่พร้อม <c>EffectiveFrom</c> · ห้ามแก้ตัวเลขแถวเดิม (ใบที่จ่ายก่อนวันมีผลต้องยังได้อัตราเดิม)</item>
/// </list></para>
///
/// <para>═══ สถานะ ณ 2026-09-29 (รอบ 200 ทีม W) — <b>ตารางว่างโดยตั้งใจ</b> ═══
/// ทีมพยายามเปิดตัวบทอนุสัญญาที่ rd.go.th (หน้า "อนุสัญญาภาษีซ้อนที่มีผลบังคับใช้แล้ว" และหน้าข้อ 11–15 ของแต่ละประเทศ) และเว็บหน่วยงานภาษีของ
/// ประเทศคู่สัญญา — <b>เครือข่ายของสภาพแวดล้อมพัฒนาบล็อกทุกโดเมน</b> ได้แค่ผลค้นหาซึ่งเป็นบทสรุป ไม่ใช่ตัวบท ⇒ ยังไม่มีแถวใดผ่านเกณฑ์ข้อแรก
/// รายการประเทศ/ข้อ/อัตราที่ "พบในบทสรุปแต่ยังไม่ได้ยืนยัน" อยู่ใน <c>erp-review/2026-09-29/team-W.md</c> §3 ให้ผู้ทำบัญชีเปิดตัวบทแล้วเติมที่นี่
/// ⇒ วันนี้ทุกการจ่ายต่างประเทศได้อัตรา ม.70 เสมอ (ทิศที่คำตัดสินข้อ 13 เลือก)</para>
///
/// <para>⚠️ <b>ผู้ส่งหลักฐาน CoR ยังไม่มีในระบบ</b> — ผู้ติดต่อยังไม่มีช่อง "หนังสือรับรองถิ่นที่อยู่ (ถึงวันที่)" ⇒ ผู้เรียกทุกตัวส่ง
/// <see cref="ResidenceCertificate.None"/> · ตั้งใจไม่เพิ่มช่องรอบนี้เพราะตารางยังว่าง (ช่องที่เก็บได้แต่ไม่มีผล = "มีช่อง ≠ มีผล" F2 ข้อ 2) ·
/// เมื่อเติมแถวแรก ต้องเพิ่มช่อง CoR ที่ผู้ติดต่อ + ส่งเข้าผู้เรียกทุกตัวในคอมมิตเดียวกัน (<c>python3 tools/callers.py ForeignWhtRateResolver</c>)</para>
/// </summary>
public static class DtaTreatyRates
{
    /// <summary>ทุกแถวที่ยืนยันแล้ว — <b>ว่าง</b> (ดูสถานะในหัวคลาส)</summary>
    public static readonly IReadOnlyList<DtaTreatyRate> Rows = Array.Empty<DtaTreatyRate>();

    /// <summary>แถวที่ใช้กับ (ประเทศ, ประเภทเงินได้) ณ วันจ่าย — ฉบับที่มีผลล่าสุดก่อนหรือเท่ากับวันจ่าย · null = ไม่มีแถว (⇒ ม.70)</summary>
    public static DtaTreatyRate? Find(string? countryIso2, ForeignIncomeCategory category, DateTime paymentDate)
        => Find(Rows, countryIso2, category, paymentDate);

    /// <summary>ตัวค้นบนตารางที่ส่งเข้ามา — ให้เทสต์พิสูจน์ทิศ "มีแถว + CoR ⇒ อัตราอนุสัญญา" ได้โดยไม่ต้องแต่งแถวลงตารางจริง</summary>
    internal static DtaTreatyRate? Find(IReadOnlyList<DtaTreatyRate> rows, string? countryIso2, ForeignIncomeCategory category,
        DateTime paymentDate)
    {
        var cc = (countryIso2 ?? "").Trim().ToUpperInvariant();
        if (cc.Length != 2 || category is ForeignIncomeCategory.Unknown or ForeignIncomeCategory.OutsideSection70) return null;
        return rows
            .Where(r => r.CountryIso2 == cc && r.Category == category && r.EffectiveFrom.Date <= paymentDate.Date)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefault();
    }
}
