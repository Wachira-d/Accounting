namespace Accounting.Helpers;

/// <summary>ช่วงวันของรอบบัญชี 1 รอบ</summary>
/// <param name="Start">วันแรกของรอบ (เวลา 00:00)</param>
/// <param name="EndInclusive">วันสุดท้ายของรอบ (ใช้กับ <c>&lt;=</c>)</param>
/// <param name="EndExclusive">เที่ยงคืนของวันถัดจากวันสุดท้าย (ใช้กับ <c>&lt;</c>)</param>
/// <param name="HalfEndInclusive">วันสุดท้ายของ 6 เดือนแรก — ฐานของ ภ.ง.ด.51</param>
public readonly record struct FiscalYearRange(
    DateTime Start,
    DateTime EndInclusive,
    DateTime EndExclusive,
    DateTime HalfEndInclusive)
{
    /// <summary>จำนวนเดือนของรอบ — รอบปกติต้องเป็น 12 (พ.ร.บ.การบัญชี ม.11)</summary>
    public int Months => ((EndExclusive.Year - Start.Year) * 12) + EndExclusive.Month - Start.Month;
}

/// <summary>
/// **ช่วงวันของรอบบัญชี — ตัวคำนวณตัวเดียวของระบบ**
///
/// ═══ ที่มา (บั๊กจริง · ผลตรวจ C-T03) ═══
/// สูตรเดียวกันถูกเขียนซ้ำ 4 ที่ (<c>DbdXbrlExportService</c> ·
/// <c>TaxService.GenerateCitReport</c> · <c>TaxFilingExportService</c> ·
/// <c>AccountingService.YearEndClose</c>) — สามที่แรกอ่าน
/// <c>Company.FiscalYearStartMonth</c> ถูก แต่ที่สี่ <b>ตรึง 1 ม.ค. – 31 ธ.ค.
/// ตายตัว</b> ⇒ บริษัทรอบ เม.ย.–มี.ค. ปิดบัญชีด้วยตัวเลขของคนละช่วงเวลากับที่
/// ยื่น DBD/สรรพากร ⇒ <b>กำไรสะสมผิด และมีตัวเลขสองชุดในระบบเดียว</b>
/// (defect class "สำเนามือที่ drift" — สามที่ถูก หนึ่งที่ลืม)
///
/// ═══ นิยาม ═══
/// "รอบบัญชีปี N" = ช่วง 12 เดือนที่ <b>เริ่ม</b> ในปี N ที่เดือน
/// <c>FiscalYearStartMonth</c> — เช่น start=4, N=2026 ⇒ 1 เม.ย. 2026 –
/// 31 มี.ค. 2027 (ตรงกับที่ <c>DbdXbrlExportService</c> ใช้อยู่แล้ว
/// จึงไม่เปลี่ยนความหมายของตัวเลขที่ยื่นไปแล้ว)
/// </summary>
public static class FiscalYear
{
    /// <summary>เดือนเริ่มรอบที่ใช้ได้จริง — ค่านอกช่วง 1–12 (รวม 0 ที่เป็น
    /// default ของคอลัมน์ตอนยังไม่ตั้งค่า) ถือเป็นปีปฏิทิน</summary>
    public static int NormalizeStartMonth(int startMonth)
        => startMonth is >= 1 and <= 12 ? startMonth : 1;

    /// <summary>ช่วงวันของรอบบัญชีปี <paramref name="fiscalYear"/></summary>
    public static FiscalYearRange RangeFor(int fiscalYear, int startMonth)
    {
        var start = new DateTime(fiscalYear, NormalizeStartMonth(startMonth), 1);
        var endExclusive = start.AddYears(1);
        return new FiscalYearRange(
            Start: start,
            EndInclusive: endExclusive.AddDays(-1),
            EndExclusive: endExclusive,
            HalfEndInclusive: start.AddMonths(6).AddDays(-1));
    }

    /// <summary>รอบบัญชีที่วันนี้อยู่ — ใช้ตอนต้องเดาปีจากวันที่
    /// (เดือนก่อนเดือนเริ่มรอบ = ยังอยู่ในรอบของปีก่อน)</summary>
    public static int FiscalYearOf(DateTime date, int startMonth)
        => date.Month >= NormalizeStartMonth(startMonth) ? date.Year : date.Year - 1;
}
