using System.Globalization;
using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ช่วงที่บริษัทเคยใช้ ภ.ง.ด.2 กับค่าสิทธิบุคคลธรรมดาแล้วปิดกลับเป็น ภ.ง.ด.3 — วันที่ตามปฏิทินไทย (00:00 UTC) ·
/// <paramref name="From"/> รวม · <paramref name="Until"/> ไม่รวม (= วันแรกที่กลับไปใช้ ภ.ง.ด.3)</summary>
public readonly record struct WhtRoyaltyWindow(DateTime From, DateTime Until);

/// <summary>ค่าตั้งบริษัท "ค่าสิทธิ ม.40(3) ที่จ่ายบุคคลธรรมดา ยื่นแบบไหน" (<c>Company.WhtRoyaltyIndividualForm</c> + <c>WhtRoyaltyPnd2From</c>
/// + ประวัติช่วงที่ปิดไปแล้ว <c>WhtRoyaltyPnd2History</c> + วันที่ผู้ใช้ตัดสินเอง <c>WhtRoyaltyFormDecidedAt</c>) —
/// ค่าตั้งต้น (<c>default</c>) = ภ.ง.ด.3 ไม่มีวันเริ่ม ไม่มีประวัติ = พฤติกรรมเดิม</summary>
public readonly record struct WhtRoyaltySetting(WhtRoyaltyIndividualForm Form, DateTime? Pnd2From,
    string? Pnd2History = null, DateTime? DecidedAt = null)
{
    /// <summary>ใช้ ภ.ง.ด.2 กับค่าสิทธิบุคคลธรรมดาที่จ่าย ณ <paramref name="taxDate"/> ไหม (<see cref="WhtRoyaltyPolicy.AppliesTo"/>) —
    /// ผู้เรียกส่งวันจาก <see cref="WhtRoyaltyPolicy.PolicyDate"/> เท่านั้น</summary>
    public bool AppliesTo(DateTime taxDate) => WhtRoyaltyPolicy.AppliesTo(this, taxDate);
}

/// <summary>
/// **ค่าสิทธิ ม.40(3) จ่ายบุคคลธรรมดา — ยื่น ภ.ง.ด.2 หรือ ภ.ง.ด.3: ตัวตัดสินตัวเดียว** (รอบ 204 · c2 · คำสั่งเจ้าของ 2026-10-13
/// "ระบบห้ามล็อกแข็ง — ผู้ใช้ตัดสิน · ระบบแนะนำ/อำนวยความสะดวก")
///
/// <para>ข้อเท็จจริง: แบบ ภ.ง.ด.2 ของกรมสรรพากรใช้กับเงินได้ ม.40(3)(4) ที่จ่ายบุคคลธรรมดา (ม.50(2) · ประกาศอธิบดีฯ ฉบับที่ 58) แต่ระบบเดิม
/// (และผู้ใช้จำนวนมาก) ยื่นค่าสิทธิใน ภ.ง.ด.3 ⇒ <b>ค่าตั้งบริษัท</b>: ภ.ง.ด.3 (เดิม · ค่าตั้งต้น) / ภ.ง.ด.2 (แนะนำ) + วันที่เริ่มใช้
/// (เปิดครั้งแรก = วันที่ 1 ของเดือนปัจจุบัน · แก้ได้) — ใบที่จ่ายก่อนวันเริ่มยังเป็น ภ.ง.ด.3 (งวดที่ยื่นไปแล้วไม่ถูกย้ายแบบ)</para>
///
/// <para><b>ค่าตั้งมีประวัติ</b> (ฝ่ายค้านรอบ 204 M2): ปิดกลับเป็น ภ.ง.ด.3 = ปิดช่วง (วันเริ่ม → วันที่กลับไปใช้ ภ.ง.ด.3) เก็บลง
/// <c>WhtRoyaltyPnd2History</c> — ไม่ทิ้งช่วง ⇒ <see cref="AppliesTo"/> ของวันที่ผ่านมาแล้วคงที่ (JE 21915 · 50 ทวิ · หน้านำส่ง · รายงาน ·
/// เปลี่ยนผู้ถูกหัก อ่านตรงกับตอนลงบัญชี) · การบันทึกที่ทำให้ผลของวันใดในงวด ภ.ง.ด.2/3 ที่ยื่น/นำส่งแล้วเปลี่ยน ⇒ คำเตือนให้รับทราบ
/// (<see cref="FiledPeriodRuleCode"/> · ไม่บล็อก — ผู้ใช้ตัดสิน · audit) — ห้ามแก้งวดที่ยื่นแล้วแบบเงียบ ๆ (คำตัดสินข้อ 150)</para>
///
/// <para>ผลของ <see cref="AppliesTo"/> ส่งเข้า <c>royaltyAsPnd2</c> ของ <see cref="WhtPayeeKind.ResolveForm"/> /
/// <see cref="ThaiWhtRateTable.ClassifySection40_4"/> / <see cref="WhtPayableAccount.CodeChain"/> <b>ทุกทาง</b> (JE 21915 · 50 ทวิ ·
/// หน้านำส่ง · ปฏิทินยื่น · รายงาน) — ทางที่ไม่ส่ง = GL กับทะเบียนเล่าคนละแบบ · 50 ทวิ ที่ผู้ใช้ออกเอง: เลือก 2 หรือ 3 ได้ทั้งสองค่าตั้ง
/// (บังคับ ภ.ง.ด.2 เฉพาะ ม.40(4) ทั้งใบ — กติกาเดิม) · วันที่ใช้ตัดสินมาจาก <see cref="PolicyDate"/> ตัวเดียว</para>
/// </summary>
public static class WhtRoyaltyPolicy
{
    /// <summary>ป้ายคำเตือนก่อนอนุมัติ "แนะนำ ภ.ง.ด.2" (เตือน ไม่บล็อก — ผู้ใช้ตัดสิน) · ผู้เรียก: <c>CollectApprovalWarningsAsync</c> (ทีม A)</summary>
    public const string ApprovalWarningPrefix = "[WHT-FORM]";
    public const string RuleCode = "RD-50(2)-ROYALTY";
    /// <summary>รหัสกฎของ 409 "เปลี่ยนค่าตั้งแล้วแตะงวดที่ยื่น/นำส่งแล้ว — รับทราบแล้วบันทึกอีกครั้ง" + audit ตอนรับทราบ</summary>
    public const string FiledPeriodRuleCode = "RD-50(2)-ROYALTY-FILED";
    public const string LegalReference = "ป.รัษฎากร ม.50(2) · ประกาศอธิบดีกรมสรรพากรเกี่ยวกับภาษีเงินได้ (ฉบับที่ 58)";

    /// <summary>วันเริ่มของช่วงที่ "ไม่มีวันเริ่ม" (ข้อมูลเก่า ภ.ง.ด.2 ที่ไม่มี <c>Pnd2From</c>) — ไม่ใช้ DateTime.MinValue เพราะ ThaiDate ปรับปี &lt; 1900</summary>
    private static readonly DateTime OpenStart = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string DateFormat = "yyyy-MM-dd";

    public static bool IsApprovalWarning(string warning)
        => warning.StartsWith(ApprovalWarningPrefix, StringComparison.Ordinal);

    /// <summary>**วันที่ตัดสินค่าตั้งของการลงรายการหนึ่ง — ตัวเดียว** (ฝ่ายค้านรอบ 204 M2 boundary): การจ่ายรายงวด (เกณฑ์เงินสด · JE ตอนบันทึกจ่าย ·
    /// 50 ทวิ ของงวดนั้น) ⇒ วันที่จ่ายจริงของงวด · ไม่มีงวด (JE ตอนอนุมัติ · ใบสำคัญจ่าย · ใบที่คีย์ ·  รายงานระดับเอกสาร) ⇒ วันที่จ่ายของเอกสาร ?? วันที่เอกสาร</summary>
    public static DateTime PolicyDate(DateTime? instalmentPaymentDate, DateTime? documentPaymentDate, DateTime documentDate)
        => instalmentPaymentDate ?? documentPaymentDate ?? documentDate;

    /// <summary>ค่าตั้งที่หนี้ WHT ของเอกสารใบหนึ่ง<b>ถูกลงบัญชีไว้แล้ว</b> (ใช้ตอนย้ายหนี้ระหว่างผัง — เปลี่ยนผู้ถูกหัก) ·
    /// มีงวดจ่ายที่ลง WHT ตอนจ่าย (เกณฑ์เงินสด) ⇒ วันที่จ่ายของงวดเหล่านั้น (<see cref="PolicyDate"/>) · ไม่มี ⇒ วันของเอกสาร ·
    /// <c>Mixed</c> = ผลต่างกันระหว่างงวด (ยอดที่ลงไว้อยู่ทั้ง 21915/21916) ⇒ ใช้ผลของงวดล่าสุด แล้วผู้เรียกต้องบอกผู้ใช้</summary>
    public static (bool AsPnd2, bool Mixed) PostedFlag(WhtRoyaltySetting setting, DateTime documentPolicyDate,
        IReadOnlyCollection<DateTime> instalmentPaymentDates)
    {
        var dates = instalmentPaymentDates.Count > 0 ? instalmentPaymentDates.OrderBy(d => d).ToList() : new List<DateTime> { documentPolicyDate };
        var flags = dates.Select(d => AppliesTo(setting, d)).ToList();
        return (flags[^1], flags.Distinct().Count() > 1);
    }

    /// <summary>ค่าตั้ง ภ.ง.ด.2 มีผลกับการจ่าย ณ <paramref name="taxDate"/> ไหม (ตามปฏิทินไทย) — ช่วงที่เปิดอยู่ (ค่าตั้งปัจจุบัน ภ.ง.ด.2 ·
    /// ไม่มีวันเริ่ม ⇒ ตั้งแต่ต้น · มีวันเริ่ม ⇒ วันที่จ่าย ≥ วันเริ่ม) <b>หรือ</b> ช่วงที่ปิดไปแล้วในประวัติ · ไม่มีประวัติ + ภ.ง.ด.3 ⇒ false (เดิม)</summary>
    public static bool AppliesTo(WhtRoyaltySetting setting, DateTime taxDate)
    {
        var d = ThaiDate.CalendarDateUtc(taxDate);
        if (setting.Form == WhtRoyaltyIndividualForm.Pnd2
            && (setting.Pnd2From is not DateTime f || d >= ThaiDate.CalendarDateUtc(f)))
            return true;
        foreach (var w in ParseHistory(setting.Pnd2History))
            if (d >= w.From && d < w.Until) return true;
        return false;
    }

    /// <summary>อ่านประวัติช่วงที่ปิดแล้ว (<c>yyyy-MM-dd..yyyy-MM-dd;…</c> — ตัวเขียนเดียว <see cref="FormatHistory"/>) · ว่าง = ไม่มี</summary>
    private static IReadOnlyList<WhtRoyaltyWindow> ParseHistory(string? history)
    {
        if (string.IsNullOrWhiteSpace(history)) return Array.Empty<WhtRoyaltyWindow>();
        var result = new List<WhtRoyaltyWindow>();
        foreach (var part in history.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split("..", StringSplitOptions.TrimEntries);
            if (ends.Length == 2
                && DateTime.TryParseExact(ends[0], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
                && DateTime.TryParseExact(ends[1], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var until)
                && until > from)
                result.Add(new WhtRoyaltyWindow(DateTime.SpecifyKind(from, DateTimeKind.Utc), DateTime.SpecifyKind(until, DateTimeKind.Utc)));
        }
        return result;
    }

    /// <summary>เขียนประวัติแบบ canonical — ตัดช่วงว่าง · เรียงตามวันเริ่ม · รวมช่วงที่ซ้อน/ต่อกัน · ไม่มีช่วง ⇒ null</summary>
    private static string? FormatHistory(IEnumerable<WhtRoyaltyWindow> windows)
    {
        var merged = new List<WhtRoyaltyWindow>();
        foreach (var w in windows.Where(x => x.Until > x.From).OrderBy(x => x.From))
        {
            if (merged.Count > 0 && w.From <= merged[^1].Until)
                merged[^1] = merged[^1] with { Until = w.Until > merged[^1].Until ? w.Until : merged[^1].Until };
            else merged.Add(w);
        }
        return merged.Count == 0 ? null
            : string.Join(";", merged.Select(w => w.From.ToString(DateFormat, CultureInfo.InvariantCulture) + ".."
                                                  + w.Until.ToString(DateFormat, CultureInfo.InvariantCulture)));
    }

    /// <summary>วันเริ่มใช้ตั้งต้นเมื่อผู้ใช้เปิด ภ.ง.ด.2 — วันที่ 1 ของเดือนปัจจุบัน (ปฏิทินไทย) · ผู้ใช้แก้ได้</summary>
    private static DateTime DefaultFromDate(DateTime utcNow)
    {
        var today = ThaiDate.CalendarDateUtc(utcNow);
        return new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>ค่าที่ต้องเก็บลงบริษัทหลังผู้ใช้บันทึกค่าตั้ง (server computes) — ไม่ส่งแบบ = ไม่แตะ · เปิด ภ.ง.ด.2 โดยไม่ระบุวัน ⇒ คงวันเดิม
    /// (ถ้าเปิดอยู่แล้ว) ไม่งั้นวันที่ 1 ของเดือนนี้ · ระบุวัน ⇒ ใช้วันนั้น (ปฏิทินไทย) ·
    /// <b>ปิดกลับเป็น ภ.ง.ด.3</b> ⇒ ปิดช่วง [วันเริ่ม, <paramref name="requestedPnd3From"/> ?? วันนี้) ลงประวัติ (ไม่ทิ้ง — ใบที่จ่ายในช่วงนั้นยังเป็น ภ.ง.ด.2) ·
    /// วันที่ผู้ใช้ตัดสิน (<see cref="WhtRoyaltySetting.DecidedAt"/>) ประทับเมื่อแบบเปลี่ยน หรือผู้ใช้ยืนยัน (<paramref name="confirm"/>)</summary>
    public static WhtRoyaltySetting ApplyUpdate(WhtRoyaltySetting current, WhtRoyaltyIndividualForm? requestedForm,
        DateTime? requestedFrom, DateTime utcNow, DateTime? requestedPnd3From = null, bool confirm = false)
    {
        var form = requestedForm ?? current.Form;
        var from = requestedFrom is DateTime rf ? ThaiDate.CalendarDateUtc(rf) : current.Pnd2From;
        var history = current.Pnd2History;
        if (form == WhtRoyaltyIndividualForm.Pnd2 && requestedFrom == null
            && (current.Form != WhtRoyaltyIndividualForm.Pnd2 || from == null))
            from = DefaultFromDate(utcNow);
        if (form == WhtRoyaltyIndividualForm.Pnd3 && current.Form == WhtRoyaltyIndividualForm.Pnd2)
        {
            // ปิดช่วงที่เปิดอยู่ (ไม่ทิ้ง) — วันเริ่มของช่วงคือค่าที่ใช้อยู่ก่อนบันทึกนี้ ไม่ใช่วันในคำขอ
            var start = current.Pnd2From is DateTime cf ? ThaiDate.CalendarDateUtc(cf) : OpenStart;
            var until = ThaiDate.CalendarDateUtc(requestedPnd3From ?? utcNow);
            history = FormatHistory(ParseHistory(current.Pnd2History).Append(new WhtRoyaltyWindow(start, until)));
            // ฝ่ายค้านรอบสาม (L-a): ล้างวันเริ่มของช่วงที่ปิดแล้ว (ช่วงอยู่ในประวัติครบ) — เดิมคงไว้ ⇒ หน้าตั้งค่าแสดงวันเดิมกลับมา
            // แล้วเปิด ภ.ง.ด.2 ใหม่ทีหลังส่งวันนั้นกลับ ⇒ ช่วงที่เป็น ภ.ง.ด.3 ระหว่างกลางพลิกเป็น ภ.ง.ด.2 เงียบ ๆ (GL อยู่ 21916 แต่รายงานอ่าน 21915)
            from = null;
        }
        var decidedAt = confirm || form != current.Form ? utcNow : current.DecidedAt;
        return new WhtRoyaltySetting(form, from, history, decidedAt);
    }

    /// <summary>จุดที่ผลของค่าตั้งอาจเปลี่ยน (วันเริ่ม/วันสิ้นของทุกช่วงทั้งก่อนและหลัง) — ระหว่างจุดเหล่านี้ผลคงที่</summary>
    private static IEnumerable<DateTime> Boundaries(WhtRoyaltySetting s)
    {
        yield return OpenStart;
        if (s.Pnd2From is DateTime f) yield return ThaiDate.CalendarDateUtc(f);
        foreach (var w in ParseHistory(s.Pnd2History)) { yield return w.From; yield return w.Until; }
    }

    /// <summary>ผลของ <see cref="AppliesTo"/> ต่างกันระหว่าง <paramref name="before"/> กับ <paramref name="after"/> ในวันใดของช่วง
    /// [<paramref name="start"/>, <paramref name="endExclusive"/>) ไหม (ตรวจวันแรกของช่วง + ทุกจุดเปลี่ยนในช่วง — ผลเป็นขั้นบันได)</summary>
    private static bool ChangedWithin(WhtRoyaltySetting before, WhtRoyaltySetting after, DateTime start, DateTime endExclusive)
    {
        var s = ThaiDate.CalendarDateUtc(start);
        var e = ThaiDate.CalendarDateUtc(endExclusive);
        return Boundaries(before).Concat(Boundaries(after)).Where(b => b > s && b < e).Prepend(s)
            .Any(b => AppliesTo(before, b) != AppliesTo(after, b));
    }

    /// <summary>วันแรก (ปฏิทินไทย) ที่ผลของค่าตั้งเปลี่ยน · null = ทุกวันได้ผลเท่าเดิม (บันทึกเรื่องอื่น/ยืนยันแบบเดิม)</summary>
    public static DateTime? FirstChangedDate(WhtRoyaltySetting before, WhtRoyaltySetting after)
    {
        foreach (var b in Boundaries(before).Concat(Boundaries(after)).Distinct().OrderBy(x => x))
            if (AppliesTo(before, b) != AppliesTo(after, b)) return b;
        return null;
    }

    /// <summary>งวด (แบบ, ปี, เดือน) ที่ยื่น/นำส่งแล้ว ซึ่งการเปลี่ยนค่าตั้งแตะจริง (ผลของวันใดวันหนึ่งในเดือนนั้นเปลี่ยน)</summary>
    public static IReadOnlyList<(TaxType Form, int Year, int Month)> TouchedFiledPeriods(WhtRoyaltySetting before, WhtRoyaltySetting after,
        IEnumerable<(TaxType Form, int Year, int Month)> filedPeriods)
        => filedPeriods.Where(p =>
            {
                var monthStart = new DateTime(p.Year, p.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                return ChangedWithin(before, after, monthStart, monthStart.AddMonths(1));
            }).Distinct().ToList();

    /// <summary>ข้อความ 409 เมื่อการเปลี่ยนแตะงวดที่ยื่น/นำส่งแล้ว — บอกผลที่เกิด + ทางไปต่อ (รับทราบแล้วบันทึกอีกครั้ง)</summary>
    public static string FiledPeriodWarning(IEnumerable<(TaxType Form, int Year, int Month)> touched)
        => "การเปลี่ยนแบบยื่นค่าสิทธิ ม.40(3) ครั้งนี้มีผลกับงวดที่ยื่น/นำส่งแล้ว — "
           + (WhtFiledPeriodAck.SupplementaryFilingLabels(touched) ?? "")
           + " · ใบที่จ่ายในงวดนั้นจะถูกจัดเป็นแบบใหม่ในหน้านำส่ง/รายงาน/50 ทวิ ที่ออกต่อจากนี้ (บัญชีและ 50 ทวิ ที่ลงไว้แล้ว ระบบไม่ย้ายให้) "
           + "· ถ้าตั้งใจ ให้รับทราบแล้วบันทึกอีกครั้ง (ระบบบันทึกประวัติการรับทราบ) — ไม่งั้นเลือกวันเริ่ม/วันกลับไปใช้ ภ.ง.ด.3 หลังงวดที่ยื่นแล้ว";

    /// <summary>สรุปค่าตั้งให้หน้าเว็บแสดง (server computes) — ช่วงที่เคยใช้ ภ.ง.ด.2 + ช่วงที่เปิดอยู่ · ว่าง = ไม่เคยใช้ ภ.ง.ด.2</summary>
    public static string? Summary(WhtRoyaltySetting s)
    {
        static string D(DateTime d) => ThaiDate.ToThaiDisplayString(d);
        var parts = ParseHistory(s.Pnd2History)
            .Select(w => $"ภ.ง.ด.2 {(w.From == OpenStart ? "ตั้งแต่ต้น" : D(w.From))} – {D(w.Until.AddDays(-1))}")
            .ToList();
        if (s.Form == WhtRoyaltyIndividualForm.Pnd2)
            parts.Add(s.Pnd2From is DateTime f ? $"ภ.ง.ด.2 ตั้งแต่ {D(ThaiDate.CalendarDateUtc(f))} (ใช้อยู่)" : "ภ.ง.ด.2 ทุกวันที่จ่าย (ใช้อยู่)");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// คำแนะนำก่อนอนุมัติ (ขึ้นต้น <see cref="ApprovalWarningPrefix"/> — คืนข้อความพร้อมป้ายแล้ว · null = ไม่ต้องแนะนำ) — ค่าตั้งยังเป็น ภ.ง.ด.3
    /// <b>ที่ผู้ใช้ยังไม่เคยตัดสินเอง</b> (<see cref="WhtRoyaltySetting.DecidedAt"/> = null — ค่าตั้งต้นของระบบ) และผู้ถูกหักเป็นบุคคลธรรมดาในประเทศ
    /// (ตัวตัดสินเดียวกับแบบที่จะลง — <see cref="WhtPayeeKind.ResolveForm"/>) และ<b>ทุกบรรทัดที่หัก</b>เป็นค่าสิทธิ ม.40(3)
    /// <para>เตือน ไม่บล็อก — ค่าตั้งเป็นของผู้ใช้ (คำสั่งเจ้าของ 2026-10-13) · ค่าตั้ง ภ.ง.ด.2 หรือผู้ใช้ยืนยัน ภ.ง.ด.3 แล้ว ⇒ เงียบ
    /// (ฝ่ายค้านรอบ 204 L6 · CLAUDE.md F2#8 คำเตือนที่ฟ้องทุกใบที่ถูกตามค่าตั้ง = ปิดด่าน) — คำแนะนำยังอยู่ที่หน้าตั้งค่า</para>
    /// </summary>
    /// <param name="setting">ค่าตั้งของบริษัท (<see cref="LoadAsync"/>)</param>
    public static string? ApprovalWarning(WhtRoyaltySetting setting, bool documentIsForeignService, string? countryCode,
        string? taxId, ContactType contactType, string? name, IEnumerable<string?>? withheldIncomeTypeCodes)
    {
        if (setting.Form != WhtRoyaltyIndividualForm.Pnd3 || setting.DecidedAt != null) return null;
        var codes = withheldIncomeTypeCodes?.ToList();
        if (codes == null || codes.Count == 0 || !codes.All(ThaiWhtRateTable.IsSection40_3)) return null;
        if (WhtPayeeKind.ResolveForm(documentIsForeignService, countryCode, taxId, contactType, name, codes) != TaxType.WithholdingTax3)
            return null;
        return ApprovalWarningPrefix + " ระบบแนะนำยื่น ภ.ง.ด.2 (ประกาศอธิบดีฯ ฉบับที่ 58) — เปลี่ยนได้ที่ ตั้งค่า > ภาษี "
               + "(หรือยืนยันใช้ ภ.ง.ด.3 ที่นั่นเพื่อหยุดคำเตือนนี้)";
    }

    /// <summary>โหลดค่าตั้งของบริษัท (ไม่พบ = ค่าตั้งต้น ภ.ง.ด.3 = พฤติกรรมเดิม) · กรอง <c>Id == companyId</c> · โหลดครั้งเดียวต่อคำขอ แล้วใช้ <see cref="WhtRoyaltySetting.AppliesTo"/> ต่อใบ</summary>
    public static async Task<WhtRoyaltySetting> LoadAsync(AccountingDbContext db, Guid companyId)
    {
        var row = await db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.WhtRoyaltyIndividualForm, c.WhtRoyaltyPnd2From, c.WhtRoyaltyPnd2History, c.WhtRoyaltyFormDecidedAt })
            .FirstOrDefaultAsync();
        return row == null ? default
            : new WhtRoyaltySetting(row.WhtRoyaltyIndividualForm, row.WhtRoyaltyPnd2From, row.WhtRoyaltyPnd2History, row.WhtRoyaltyFormDecidedAt);
    }
}
