namespace Accounting.Helpers;

/// <summary>ต้นทางรายการประจำของใบที่กำลังสร้าง (รอบ 259 · B-L2/L3) — <b>พารามิเตอร์ของเมธอด <c>CreateDocumentAsync</c> ไม่ใช่ช่องใน request</b>
/// (ค่าที่ client คุมได้ห้ามเป็นกุญแจกันซ้ำ) · ประทับใน INSERT เดียวกับตัวเอกสาร ⇒ ไม่มีช่องว่าง "สร้างใบแล้วยังไม่ได้ประทับ"</summary>
public sealed record RecurringRunOrigin(Guid RecurringTransactionId, string? RunKey);

/// <summary>
/// รอบ 259 (B-L2 · B-L3 · คำตัดสิน 259-4-3) — กุญแจกันซ้ำ "รายการประจำ + งวด" บนเอกสาร (<c>Documents.RecurringRunKey</c> + unique index ต่อบริษัท)
/// <para>L2: ระบบล้มระหว่างสร้างเอกสารกับประทับรอบ (<c>MarkRunAsync</c>) ⇒ รอบถัดไปเจอใบของงวดนี้อยู่แล้ว ⇒ ประทับรอบด้วยใบเดิม ไม่ออกซ้ำ</para>
/// <para>L3: "สร้างทันที" ก่อนถึงกำหนด (รอบพิเศษ ไม่กินงวด) ออกได้ 1 ใบต่องวดที่กำลังจะถึง — กดซ้ำ ⇒ 409 ถามยืนยัน ·
/// ยืนยันแล้ว (ตั้งใจออกเพิ่ม เช่นงวดที่ระบบข้าม) ⇒ ไม่มีกุญแจ (ไม่จำกัด แต่ต้องตั้งใจทุกครั้ง)</para>
/// <para>สมุดรายวันไม่ใช้กุญแจนี้ (ความเสี่ยงคงค้าง — ใบสำคัญสร้างในธุรกรรมของ <c>AccountingService</c> ที่ไม่มีช่องกุญแจ)</para>
/// </summary>
public static class RecurringRunKey
{
    public const string RuleCode = "RECURRING-RUN-DUP";
    public const int MaxLength = 64;

    /// <summary>กุญแจของงวด (วันของงวดตามปฏิทินไทย — ค่าเดียวกันทุกครั้งที่งวดนั้นถูกหยิบ)</summary>
    public static string ForPeriod(Guid recurringId, DateTime periodDate)
        => $"{recurringId:N}:{ThaiDate.CalendarDateUtc(periodDate):yyyyMMdd}";

    /// <summary>รอบ 265 (ฝ่ายค้าน M3): อ่านกุญแจงวดกลับ (รายการ + วันงวด) — กุญแจรอบพิเศษ (<c>:early</c>) / รูปแบบอื่น = false</summary>
    public static bool TryParsePeriod(string? key, out Guid recurringId, out DateTime periodDate)
    {
        recurringId = Guid.Empty;
        periodDate = default;
        if (string.IsNullOrEmpty(key)) return false;
        var parts = key.Split(':');
        return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out recurringId)
            && DateTime.TryParseExact(parts[1], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out periodDate);
    }

    /// <summary>กุญแจของรอบพิเศษก่อนกำหนด — 1 ใบต่องวดที่กำลังจะถึง</summary>
    private static string ForEarly(Guid recurringId, DateTime upcomingPeriodDate) => ForPeriod(recurringId, upcomingPeriodDate) + ":early";

    /// <summary>กุญแจของรอบนี้ — รอบที่ใช้งวด = กุญแจงวด · รอบพิเศษ = กุญแจรอบพิเศษ · รอบพิเศษที่ผู้ใช้ยืนยันออกเพิ่ม = null (ไม่จำกัด)</summary>
    public static string? For(Guid recurringId, DateTime nextRunDate, bool consumesPeriod, bool confirmedExtra)
        => consumesPeriod ? ForPeriod(recurringId, nextRunDate)
            : confirmedExtra ? null
            : ForEarly(recurringId, nextRunDate);

    /// <summary>ข้อความ 409 ของรอบพิเศษซ้ำ (หน้าเว็บจับ <see cref="RuleCode"/> แล้วถามยืนยัน)</summary>
    public static string EarlyAlreadyIssuedMessage(string? documentNumber, DateTime upcomingPeriodDate)
    {
        var d = ThaiDate.CalendarDateUtc(upcomingPeriodDate);
        return $"ออกใบล่วงหน้าของงวด {d:dd/MM}/{d.Year + 543} ไปแล้ว ({documentNumber ?? "ใบร่าง"}) — กดซ้ำจะได้อีกใบ · "
               + "ถ้าตั้งใจออกเพิ่ม (เช่น งวดที่ระบบข้าม) กดยืนยันออกอีกใบ";
    }

    /// <summary>ข้อความบนแถวเมื่อรอบตามกำหนดพบใบของงวดนี้อยู่แล้ว (ระบบหยุดระหว่างทางรอบก่อน) — ไม่ออกซ้ำ</summary>
    public static string RecoveredNote(string? documentNumber, DateTime periodDate)
    {
        var d = ThaiDate.CalendarDateUtc(periodDate);
        return $"งวด {d:dd/MM}/{d.Year + 543} มีใบแล้ว ({documentNumber ?? "ใบร่าง"}) จากรอบก่อนที่ระบบหยุดระหว่างทาง — ไม่ออกซ้ำ · "
               + "ตรวจว่าใบนั้นอนุมัติ/ส่งแล้วหรือยัง";
    }

    /// <summary>รอบพิเศษก่อนกำหนดที่ "ใบของงวด" เป็นใบเดียวกับใบล่าสุดของรายการ (ประทับรอบแล้ว) ⇒ ไม่นับครั้งซ้ำ</summary>
    public static bool CountsAsNewRun(Guid? lastDocumentId, Guid existingDocumentId) => lastDocumentId != existingDocumentId;
}
