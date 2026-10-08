namespace Accounting.Helpers;

/// <summary>
/// **ใบ/รอบไหน "อยู่ใน" การนำส่งภาษีหัก ณ ที่จ่ายของงวด** — ตัวตัดสินตัวเดียวของด่านยกเลิก 50 ทวิ (<see cref="WhtCertVoidGuard"/>)
/// และด่านแก้/ยกเลิกรอบเงินเดือน (<c>PayrollService.LoadRecalculateLockEvidenceAsync</c>) · รอบ 201 ฝ่ายค้านรอบสาม PR2 (P1-1)
///
/// <para>ที่มา: รอบสองนับบันทึกนำส่ง (<c>StatutoryRemittance</c>) เป็นหลักฐาน "ยื่นแล้ว" ของ<b>ทั้งงวด</b> ⇒ ใบ 50 ทวิ ที่ออก<b>หลัง</b>นำส่ง
/// (PV ลงวันที่ย้อนเข้างวดที่นำส่งแล้ว) ยกเลิกไม่ได้ทั้งที่ยังไม่ได้อยู่ในเงินที่นำส่ง — หน้านำส่งเองคิดยอดค้าง = ใบ − ยอดที่นำส่ง
/// (<c>StatutoryRemittanceService</c>) จึงนับใบนั้นเป็น "ยังค้างนำส่ง" · รอบเงินเดือนที่ยกเลิกแล้วสร้างใหม่ในเดือนเดียวกันก็ถูกล็อกทันทีด้วยเหตุเดียวกัน</para>
/// <para>กติกา: นับว่าอยู่ในการนำส่งเฉพาะของที่<b>เกิดก่อนหรือพร้อม</b>เวลาบันทึกการนำส่งล่าสุดของงวด (<c>CreatedAt</c> ของแถวนำส่ง —
/// ระบบห้ามนำส่งงวดเดิมซ้ำ จึงมีแถวที่ยังไม่ถูกลบได้แถวเดียว · ใช้ค่าล่าสุดกันข้อมูลเก่าที่ซ้ำ) · ไม่มีบันทึกนำส่ง = ไม่อยู่ในการนำส่ง</para>
/// <para>รอบเงินเดือน: นับด้วยเวลาจ่ายจริง <c>PaidAt</c> (ข้อ 115 Q1 · 2026-10-08) · ข้อมูลเก่าที่ไม่มีเวลาจ่าย ⇒ เวลาสร้างรอบ
/// (ทิศ "ล็อกไว้ก่อน": รอบที่สร้างก่อนนำส่งยังนับว่าอยู่ในการนำส่ง ดีกว่าปลดรอบที่ยอดออกนอกระบบไปแล้ว)</para>
/// </summary>
public static class RemittanceInclusion
{
    /// <summary>ของที่เกิดเมื่อ <paramref name="itemAtUtc"/> อยู่ในการนำส่งที่บันทึกเมื่อ <paramref name="remittedAtUtc"/> หรือไม่
    /// (null = งวดนั้นยังไม่มีบันทึกนำส่ง ⇒ ไม่อยู่)</summary>
    public static bool Includes(DateTime? remittedAtUtc, DateTime itemAtUtc) =>
        remittedAtUtc is DateTime r && itemAtUtc <= r;

    /// <summary>เวลาที่ 50 ทวิ "นับเข้ายอดนำส่ง" — <c>IssuedDate</c> (ทุกทางออกใบประทับเวลาจริงตอนออก) · ไม่มี ⇒ เวลาสร้างแถว</summary>
    public static DateTime CertCountedAt(DateTime? issuedDateUtc, DateTime createdAtUtc) => issuedDateUtc ?? createdAtUtc;

    /// <summary>เวลาที่รอบเงินเดือน "นับเข้ายอดนำส่ง" (คำตัดสินข้อ 115 Q1) — <c>PaidAt</c> (เวลาจ่ายจริง) · ไม่มี (ข้อมูลเก่าที่ไม่มี JE) ⇒
    /// เวลาสร้างรอบ (ทิศเดิม "ล็อกไว้ก่อน")</summary>
    public static DateTime RunCountedAt(DateTime? paidAtUtc, DateTime createdAtUtc) => paidAtUtc ?? createdAtUtc;

    /// <summary>เวลาบันทึกการนำส่งล่าสุดต่อกุญแจงวด — ผู้เรียกส่งเฉพาะแถวที่ยังไม่ถูกลบของบริษัทนั้น</summary>
    public static Dictionary<TKey, DateTime> LatestByPeriod<TKey>(IEnumerable<(TKey Key, DateTime RecordedAtUtc)> records)
        where TKey : notnull
    {
        var latest = new Dictionary<TKey, DateTime>();
        foreach (var (key, at) in records)
            if (!latest.TryGetValue(key, out var cur) || at > cur) latest[key] = at;
        return latest;
    }

    /// <summary>วันเวลาบันทึกการนำส่งสำหรับข้อความถึงผู้ใช้ — เวลาไทย (UTC+7) ปี พ.ศ.</summary>
    public static string ThaiStamp(DateTime remittedAtUtc)
    {
        var th = remittedAtUtc.AddHours(7);
        return $"{th:dd}/{th:MM}/{th.Year + 543} {th:HH}:{th:mm} น.";
    }
}
