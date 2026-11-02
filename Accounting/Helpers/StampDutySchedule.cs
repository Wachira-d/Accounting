using System;

namespace Accounting.Helpers;

/// <summary>
/// <b>อากรแสตมป์</b> (ป.รัษฎากร §103–§118 · บัญชีอัตราอากรแสตมป์) — ตัวตั้งตัวเดียวของอัตรา · วันครบกำหนด · เงินเพิ่มอากร (รอบ 228 · TAX_PENALTY_AUDIT §3 ข้อ 3)
///
/// <para>═══ ที่มา ═══ <c>StampDutyService.Compute</c> เดิม <b>ปัดลง</b> (<c>Math.Floor</c>) และใส่เพดานที่บัญชีอัตราไม่มี (ค่าเช่า 1,000/ปี ·
/// จ้างทำของ 200,000) ⇒ อากรขาดเมื่อมูลค่ามีเศษหลักพัน · หน้าจอเขียนอัตราเป็นข้อความเอง (สำเนาที่สอง) · <c>MarkPaid</c> ไม่ลงบัญชี ไม่มีเงินเพิ่มอากร</para>
///
/// <para>═══ อัตรา (หน่วยละ "ทุกจำนวนเงิน N บาท <b>หรือเศษของ</b> N บาท" ⇒ ปัดขึ้นต่อหน่วย) ═══
/// <list type="bullet">
/// <item>ลักษณะ 1 เช่าที่ดิน/โรงเรือน — 1 บาท ต่อค่าเช่าตลอดอายุสัญญาทุก 1,000 บาทหรือเศษ (มูลค่าที่กรอก = ค่าเช่ารวมทั้งสัญญา)</item>
/// <item>ลักษณะ 4 จ้างทำของ — 1 บาท ต่อสินจ้างทุก 1,000 บาทหรือเศษ</item>
/// <item>ลักษณะ 5 กู้ยืมเงิน — 1 บาท ต่อทุก 2,000 บาทหรือเศษ ไม่เกิน 10,000 บาท</item>
/// <item>ลักษณะ 7 ใบมอบอำนาจ — ทำการมากกว่าครั้งเดียว 30 บาท (ครั้งเดียว 10 บาท — ระบบเสนอ 30 ผู้ใช้แก้ยอดได้)</item>
/// <item>ลักษณะ 17 เช็ค — ฉบับละ 3 บาท</item>
/// </list>
/// ⚠️ ต้องยืนยันกับผู้ทำบัญชีก่อนใช้ยอด (ช่องอากรแก้ได้เสมอ) — ลักษณะอื่นระบบไม่คิด (คืน 0 + ผู้ใช้กรอกเอง)</para>
///
/// <para>═══ ครบกำหนด / เงินเพิ่มอากร ═══ ชำระเป็นตัวเงิน (e-Stamp/ใบเสร็จ) ภายใน 15 วันนับแต่วันทำตราสาร · ติดแสตมป์ = ต้องติดในวันทำตราสาร ·
/// ช้า: นำมาขอเสียเองภายใน 90 วันนับแต่วันครบกำหนด ⇒ เงินเพิ่มอากร 2 เท่า (ม.114) · เกิน 90 วัน ⇒ 5 เท่า (ม.113) · ไม่ต่ำกว่า 2/5 บาท ·
/// <b>ค่าเสนอ</b> ต้องยืนยัน — ผู้ใช้แก้ตามใบเสร็จ · เงินเพิ่มอากร = รายจ่ายต้องห้าม §65 ตรี(6)</para>
/// </summary>
public static class StampDutySchedule
{
    public const string RemittanceType = "StampDuty";
    public const string ExpenseCode = "54810";      // ค่าธรรมเนียมราชการ (แม่แบบผังบัญชี) — ผู้ใช้เลือกผังอื่นได้ตอนบันทึกจ่าย
    public const int PaymentDaysAfterInstrument = 15;
    public const int VoluntaryWindowDays = 90;
    public const string LegalReference = "RD-113/114";

    /// <summary>อากรตามบัญชีอัตรา (บาทเต็ม) · ลักษณะที่ไม่รู้จัก = 0 · <paramref name="leaseYears"/> ไม่ใช้คำนวณแล้ว (คงไว้เพื่อ API เดิม)</summary>
    public static decimal Compute(int rdScheduleNumber, decimal instrumentValue, int? leaseYears = null)
    {
        var v = Math.Max(0m, instrumentValue);
        return rdScheduleNumber switch
        {
            1 => Units(v, 1_000m),
            4 => Units(v, 1_000m),
            5 => Math.Min(10_000m, Units(v, 2_000m)),
            7 => 30m,
            17 => 3m,
            _ => 0m,
        };
    }

    /// <summary>ป้ายลักษณะ + อัตรา (เซิร์ฟเวอร์ส่งให้หน้าแสดง — ห้ามเขียนอัตราในหน้า)</summary>
    public static string Label(int rdScheduleNumber) => rdScheduleNumber switch
    {
        1 => "ลักษณะ 1 เช่าที่ดิน/โรงเรือน — 1 บาท ต่อค่าเช่าทั้งสัญญาทุก 1,000 บาทหรือเศษ",
        4 => "ลักษณะ 4 จ้างทำของ — 1 บาท ต่อสินจ้างทุก 1,000 บาทหรือเศษ",
        5 => "ลักษณะ 5 กู้ยืมเงิน — 1 บาท ต่อทุก 2,000 บาทหรือเศษ (ไม่เกิน 10,000)",
        7 => "ลักษณะ 7 ใบมอบอำนาจ — 30 บาท (ทำการครั้งเดียว 10 บาท — แก้ยอดได้)",
        17 => "ลักษณะ 17 เช็ค — ฉบับละ 3 บาท",
        _ => $"ลักษณะ {rdScheduleNumber} — ระบบไม่มีอัตรา กรอกอากรเอง",
    };

    public static readonly int[] KnownSchedules = { 1, 4, 5, 7, 17 };

    /// <summary>คำอธิบายบรรทัดค่าอากร (หักเป็นรายจ่ายได้) — <b>ห้ามมี</b> <see cref="LatePaymentPenaltyPolicy.Section65Ter6Marker"/></summary>
    public static string ExpenseLineDescription(string reference, int rdScheduleNumber) => $"ค่าอากรแสตมป์ {reference} ({Label(rdScheduleNumber)})";

    /// <summary>ฝ่ายค้าน S9a — ติดแสตมป์ (Affix): ค่าอากรมักถูกลงค่าใช้จ่ายไปแล้วตอนซื้อแสตมป์ ⇒ ค่าตั้งต้นไม่ลงบัญชีซ้ำ (ผู้ใช้ติ๊กเองได้) · ชำระเป็นตัวเงิน = ลงบัญชี</summary>
    public static bool DefaultPostJournal(string? paymentMethod)
        => !string.Equals(paymentMethod, "Affix", StringComparison.OrdinalIgnoreCase);

    /// <summary>วันสุดท้ายที่ยังไม่ช้า — ติดแสตมป์ (<c>Affix</c>) = วันทำตราสาร · ชำระเป็นตัวเงิน = +15 วัน (เลื่อนวันหยุดด้วยตัวตัดสินวันทำการกลาง)</summary>
    public static DateTime DueDate(DateTime instrumentDate, string? paymentMethod, System.Collections.Generic.IReadOnlySet<DateTime>? holidays = null)
        => string.Equals(paymentMethod, "Affix", StringComparison.OrdinalIgnoreCase)
            ? instrumentDate.Date
            : BusinessDayCalendar.RollForward(instrumentDate.Date.AddDays(PaymentDaysAfterInstrument), holidays);

    public sealed record SurchargeSuggestion(decimal Amount, int DaysLate, string Note);

    /// <summary>เงินเพิ่มอากรที่เสนอ (ค่าเสนอ — ต้องยืนยัน)</summary>
    public static SurchargeSuggestion SuggestSurcharge(decimal duty, DateTime dueDate, DateTime payDate)
    {
        var days = (int)(payDate.Date - dueDate.Date).TotalDays;
        if (days <= 0 || duty <= 0m)
            return new SurchargeSuggestion(0m, Math.Max(0, days), "ชำระทันกำหนด — ไม่มีเงินเพิ่มอากร");
        var voluntary = days <= VoluntaryWindowDays;
        var (mult, min) = voluntary ? (2m, 2m) : (5m, 5m);
        var amt = Math.Max(min, Math.Round(duty * mult, 2, MidpointRounding.AwayFromZero));
        return new SurchargeSuggestion(amt, days,
            $"ช้า {days} วัน — เงินเพิ่มอากร {mult:0} เท่า ({(voluntary ? "นำมาขอเสียเองภายใน 90 วัน ม.114" : "เกิน 90 วัน ม.113")}) · "
            + "ค่าเสนอ ต้องยืนยันกับผู้ทำบัญชี/ใบเสร็จจริง · รายจ่ายต้องห้าม §65 ตรี(6)");
    }

    private static decimal Units(decimal value, decimal per) => value <= 0m ? 0m : Math.Ceiling(value / per);
}
