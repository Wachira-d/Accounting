namespace Accounting.Helpers;

/// <summary>
/// **ช่องของลำดับเลขที่เอกสาร (<c>NumberSeries</c>) ที่ API รับได้แต่ตัวออกเลขไม่ใช้** — ผลตรวจ S-20 (รอบ 193)
///
/// <para>═══ ที่มา ═══ <c>DocumentNumberGenerator</c> ออกเลขรูปแบบ <c>{ตัวย่อ}-{yyyyMMdd}-{NNNN}</c> เสมอ
/// (ตั้งใจ — กันเลขสองทรงปนกันและเลขซ้ำ §86/4 · ดู <c>ResolvePrefixAsync</c>) · อ่านแค่ <c>Prefix</c> ·
/// แต่ <c>POST/PUT settings/number-series</c> ยังรับ <c>Suffix/Format/CurrentNumber/ResetPeriod/StartNumber</c> เก็บ-ตอบกลับ
/// ⇒ ผู้เรียกตั้ง "เริ่มเลขที่ 1000" แล้วได้ "สำเร็จ" ทั้งที่ไม่มีผล = silent no-op (กฎเหล็ก #4 A)</para>
///
/// <para>กติกา: ส่งค่าที่<b>ต่างจากค่าเดิม</b> (สร้างใหม่ = ต่างจากค่าเริ่มต้นของ entity) ⇒ ปฏิเสธทั้งคำขอด้วยข้อความไทย
/// ก่อนบันทึกอะไร · ส่งค่าเดิมกลับมา (GET แล้ว PUT ทั้งก้อน) ⇒ ผ่าน · ช่องว่าง/ไม่ส่ง ⇒ ผ่าน</para>
/// </summary>
public static class NumberSeriesFieldPolicy
{
    public const string RuleCode = "SET-NUMBER-SERIES-UNUSED-FIELD";

    /// <summary>ค่าเริ่มต้นของ entity (<c>NumberSeries</c>) — สร้างใหม่ต้องเท่านี้</summary>
    public const string DefaultFormat = "{PREFIX}-{YYYY}{MM}-{SEQ:4}";
    public const int DefaultStartNumber = 1;
    public const int DefaultResetPeriod = 0;

    public const string Suffix = "ตัวต่อท้าย (Suffix)";
    public const string Format = "รูปแบบเลข (Format)";
    public const string CurrentNumber = "เลขล่าสุด (CurrentNumber)";
    public const string StartNumber = "เลขเริ่มต้น (StartNumber)";
    public const string ResetPeriod = "รอบเริ่มนับใหม่ (ResetPeriod)";

    /// <summary>ช่องที่ผู้เรียกพยายาม "เปลี่ยน" ตอนแก้ — null = ไม่ส่ง = ไม่นับ</summary>
    public static IReadOnlyList<string> ChangedOnUpdate(
        string? currentSuffix, string currentFormat, int currentNumber, int currentResetPeriod,
        string? suffix, string? format, int? newCurrentNumber, int? resetPeriod)
    {
        var changed = new List<string>();
        if (suffix != null && Blank(suffix) != Blank(currentSuffix)) changed.Add(Suffix);
        if (format != null && Blank(format) != Blank(currentFormat)) changed.Add(Format);
        if (newCurrentNumber.HasValue && newCurrentNumber.Value != currentNumber) changed.Add(CurrentNumber);
        if (resetPeriod.HasValue && resetPeriod.Value != currentResetPeriod) changed.Add(ResetPeriod);
        return changed;
    }

    /// <summary>ช่องที่ต่างจากค่าเริ่มต้นตอนสร้าง</summary>
    public static IReadOnlyList<string> ChangedOnCreate(string? suffix, string? format, int startNumber, int resetPeriod)
    {
        var changed = new List<string>();
        if (Blank(suffix) != null) changed.Add(Suffix);
        if (format != null && Blank(format) != null && Blank(format) != DefaultFormat) changed.Add(Format);
        if (startNumber != DefaultStartNumber) changed.Add(StartNumber);
        if (resetPeriod != DefaultResetPeriod) changed.Add(ResetPeriod);
        return changed;
    }

    /// <summary>ข้อความ 400 — บอกช่องที่ไม่มีผล · รูปแบบเลขจริง · และทางไปต่อ</summary>
    public static string Message(IReadOnlyList<string> fields)
        => $"ยังไม่รองรับการตั้ง {string.Join(" · ", fields)} ของลำดับเลขที่เอกสาร — ค่านี้ไม่มีผลกับการออกเลข "
           + "(ระบบออกเลขรูปแบบ ตัวย่อ-ปีเดือนวัน-ลำดับ 4 หลัก เสมอ เพื่อไม่ให้เลขซ้ำหรือขาดช่วงตาม ม.86/4) · "
           + "ไม่ได้บันทึกอะไร — ตั้งได้เฉพาะตัวย่อ (Prefix) ส่งเฉพาะช่องนั้นแล้วลองใหม่";

    /// <summary>โยน <see cref="BusinessRuleException"/> เมื่อมีช่องที่ไม่มีผล</summary>
    public static void ThrowIfAny(IReadOnlyList<string> fields)
    {
        if (fields.Count > 0) throw new BusinessRuleException(Message(fields), RuleCode);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
