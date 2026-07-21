namespace Accounting.Helpers;

/// <summary>Helper จัดการ "วันที่ตามปฏิทินไทย" (Asia/Bangkok) ให้ unambiguous
/// ทั่วระบบ. ปัญหาที่แก้: DocumentDate/PaymentDate ฯลฯ เป็น "calendar date"
/// (สนใจแค่ วัน/เดือน/ปี ไม่สนเวลา) แต่ถูกเก็บใน Postgres timestamptz เป็น UTC.
/// เมื่อ frontend ส่ง "2026-06-02" แล้ว parse เป็น local midnight → convert UTC
/// = "2026-06-01 17:00 UTC" (shift ถอยหลัง 1 วัน). ทำให้:
///   • เลขเอกสาร format UTC ดิบ = ผิดวัน
///   • ภพ.30 boundary: ใบ 01/06 ไทย เก็บ 31/05 17:00 UTC → ตกงวด มิ.ย.
///
/// วิธีแก้: normalize ทุก calendar date → "วันที่ไทย ณ 00:00 UTC" (anchor
/// midnight UTC ของวันตามปฏิทินไทย) ก่อนเก็บ. ทำให้ raw-UTC date = Bangkok
/// date = วันที่จริง — ทุก comparison/format ตรงกันไม่มี ambiguity.</summary>
public static class ThaiDate
{
    private static TimeZoneInfo Bkk()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Bangkok");
        }
        catch { return TimeZoneInfo.CreateCustomTimeZone("BKK", TimeSpan.FromHours(7), "BKK", "BKK"); }
    }

    /// <summary>คืนวันที่ตามปฏิทินไทยของ dt (ไม่ว่า dt จะ Kind ไหน) ในรูป
    /// DateTime ณ 00:00:00 Kind=Utc → เก็บลง timestamptz แล้ว round-trip
    /// กลับมาเป็นวันเดิมเสมอ + format/compare ตรงทุก layer.</summary>
    public static DateTime CalendarDateUtc(DateTime dt)
    {
        // Era guard — storage ต้องเป็น ค.ศ. เสมอ (ช่วงสมเหตุผล ~1900–2400).
        // ปีนอกช่วงแปลว่ามีการปน พ.ศ./ค.ศ. หลุดมา: 2569 (พ.ศ. ไม่ถูกแปลง) หรือ
        // 1483 (ค.ศ. ถูกลบ 543 เกิน) → ปรับกลับให้อยู่ในช่วง ค.ศ. กัน bug
        // ทุกทาง (create/update/import/OCR/auto-receipt) เขียนปีเพี้ยนลง DB
        // ซึ่งทำให้ aging/เลขเอกสาร (yyyyMMdd) ผิด ~543 ปี. AddYears กัน Feb29.
        if (dt.Year > 2400) dt = dt.AddYears(-543);
        else if (dt.Year < 1900) dt = dt.AddYears(543);
        // ตีความ dt ว่าเป็น instant UTC (Unspecified/Local → treat เป็น UTC
        // เพื่อไม่ double-shift; midnight Unspecified → Bangkok 07:00 = วันเดิม)
        var utc = dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        var bkk = TimeZoneInfo.ConvertTimeFromUtc(utc, Bkk());
        return new DateTime(bkk.Year, bkk.Month, bkk.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>nullable overload.</summary>
    public static DateTime? CalendarDateUtc(DateTime? dt)
        => dt.HasValue ? CalendarDateUtc(dt.Value) : null;

    /// <summary>yyyyMMdd ของวันที่ไทย — ใช้กับเลขเอกสาร.</summary>
    public static string YyyyMmDd(DateTime dt)
        => CalendarDateUtc(dt).ToString("yyyyMMdd");
}
