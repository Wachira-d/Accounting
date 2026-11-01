using System;
using System.Collections.Generic;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>
/// <b>เงินเพิ่ม / เบี้ยปรับ / ค่าปรับ ของการนำส่งภาษี-ประกันสังคมช้า</b> — ตัวตั้งตัวเดียว (รอบ 220 · คำตัดสินข้อ 181)
///
/// <para>═══ ที่มา (erp-review/2026-11-01/TAX_PENALTY_AUDIT.md) ═══
/// หน้านำส่งภาษี/ประกันสังคมเดิมมีช่องเงินเพิ่มเฉพาะ สปส. (คิดเอง แก้ไม่ได้) และ ภ.พ.36 (แก้ได้) ·
/// ภ.พ.30 / ภ.ง.ด.1/2/3/53/54 <b>ไม่มีช่องเลย</b> (เซิร์ฟเวอร์ตั้ง 0 ตายตัว) ⇒ จ่ายสรรพากรจริง ภาษี + เงินเพิ่ม + เบี้ยปรับ
/// แต่บันทึกได้แค่ยอดภาษี ⇒ ยอดธนาคารไม่ตรง · ไม่มีเบี้ยปรับ/ค่าปรับอาญาให้กรอกในแบบใดเลย</para>
///
/// <para>═══ กติกา (ค่าที่ระบบ "เสนอ" — ผู้ใช้แก้ได้ทุกช่องก่อนกดนำส่ง ตามใบเสร็จจริง) ═══
/// <list type="bullet">
/// <item><b>เงินเพิ่ม</b> — สปส.1-10: 2%/เดือน พ.ร.บ.ประกันสังคม ม.49 (<see cref="SsoLateFee"/> ตัวเดิม) ·
/// ภ.ง.ด.1/2/3/53/54: 1.5%/เดือน ป.รัษฎากร ม.27 · ภ.พ.30/ภ.พ.36: 1.5%/เดือน ม.89/1 —
/// "เศษของเดือนนับเป็นหนึ่งเดือน" + เพดานไม่เกินภาษี (<see cref="RevenueCodeSurcharge"/>)</item>
/// <item><b>เบี้ยปรับ</b> — เสนอเฉพาะ ภ.พ.30 ที่ยื่นเองช้า (ก่อนถูกตรวจ/หมายเรียก): ม.89 ลดตามระเบียบกรมสรรพากร
/// ว่าด้วยการงดหรือลดเบี้ยปรับ — ช้า ≤ 15 วัน 2% · ≤ 30 วัน 5% · ≤ 60 วัน 10% · เกิน 60 วัน 20% ของภาษีที่ต้องชำระ
/// (<b>ต้องยืนยันเลขระเบียบกับผู้ทำบัญชี</b> — ช่องแก้ได้) · แบบอื่น = 0 (หัก ณ ที่จ่าย/ประกันสังคมไม่มีเบี้ยปรับตามแบบ)</item>
/// <item><b>ค่าปรับ (อาญา/อื่น ๆ)</b> — ระบบ<b>ไม่เดา</b> (เปรียบเทียบปรับตามที่เจ้าหน้าที่แจ้ง เช่น ม.35 ไม่เกิน 2,000 บาท) ⇒ 0 + คำแนะนำ</item>
/// </list>
/// ทั้งสามก้อน = รายจ่ายต้องห้าม §65 ตรี(6) (บวกกลับ ภ.ง.ด.50 ผ่าน <c>TaxService.ComputeSection65TerAddBackAsync</c>)</para>
///
/// <para>วันครบกำหนด: สปส. = <see cref="SsoLateFee.DueDate"/> + วันหยุดราชการ (ตัวเดียวกับหน้าเงินเดือน) · แบบสรรพากร = วัน e-Filing
/// (<see cref="TaxFilingDeadline.WarnBy(string,int,int,IReadOnlySet{DateTime}?)"/>) หรือวันกระดาษเมื่อ "ยื่นแบบกระดาษ" ·
/// <b>พ้นวันขยายแล้วนับจากวันกระดาษ</b> (<see cref="CountFrom"/> · ฝ่ายค้านข้อ 1 · ต้องยืนยันกับนักบัญชี)</para>
/// </summary>
public static class LatePaymentPenaltyPolicy
{
    public const string SurchargeRuleCode = "LATE-SURCHARGE";
    public const string NegativeRuleCode = "REMIT-LATEFEE-NEGATIVE";

    /// <summary>ค่าที่ระบบเสนอ + เหตุผล (แสดงบนจอ tooltip) — ทุกยอดปัด 2 ตำแหน่ง AwayFromZero</summary>
    public sealed record Suggestion(
        DateTime DueDate,
        int DaysLate,
        int MonthsLate,
        decimal Surcharge,
        bool SurchargeCapped,
        string? SurchargeReference,
        string SurchargeNote,
        decimal Penalty,
        string? PenaltyReference,
        string PenaltyNote,
        string FineNote)
    {
        public decimal Total => Surcharge + Penalty;
    }

    /// <summary>ยอดที่จะลงบัญชีจริง (หลังผู้ใช้แก้) — แยกสามก้อน + รวม</summary>
    public sealed record Applied(decimal Surcharge, decimal Penalty, decimal OtherFine)
    {
        public decimal Total => Surcharge + Penalty + OtherFine;
    }

    /// <summary>แบบที่เดิม "ไม่ส่งยอดเงินเพิ่ม = ใช้ค่าที่ระบบเสนอ" (สปส. ม.49 · ภ.พ.36 คำตัดสินข้อ 135) —
    /// คงพฤติกรรม API เดิม · แบบอื่นไม่ส่ง = 0 (ห้ามประทับค่าใช้จ่ายที่ผู้ใช้ไม่เห็น — หลักการ F2 ข้อ 3)</summary>
    private static bool AutoAppliesSurchargeWhenOmitted(string remittanceType)
        => remittanceType is "SsoSps110" or "VatPp36";

    /// <summary>วันสุดท้ายที่ยังไม่ช้า (ตัวเดียวกับป้าย "เลยกำหนด" บนหน้านำส่ง) — ยื่นออนไลน์ = วัน e-Filing (ภ.พ.36/ภ.ง.ด.54 = วันกระดาษ
    /// จนกว่ายืนยันมาตรการ) · <paramref name="paperFiling"/> = วันกระดาษ · สปส. = <see cref="SsoLateFee.DueDate"/> (ไม่มีมาตรการ e-Filing)</summary>
    public static DateTime DueDate(string remittanceType, int periodYear, int periodMonth, IReadOnlySet<DateTime>? holidays, bool paperFiling = false)
        => remittanceType == "SsoSps110"
            ? SsoLateFee.DueDate(periodYear, periodMonth, holidays)
            : paperFiling
                ? TaxFilingDeadline.For(remittanceType, periodYear, periodMonth, holidays).Paper.Date
                : TaxFilingDeadline.WarnBy(remittanceType, periodYear, periodMonth, holidays).Date;

    /// <summary><b>วันเริ่มนับเงินเพิ่ม/เบี้ยปรับเมื่อช้าแล้ว</b> (ฝ่ายค้านรอบ 220 ข้อ 1) — มาตรการขยายเวลา e-Filing ให้เฉพาะผู้ที่ยื่น<b>ทัน</b>วันขยาย ·
    /// พ้นวันขยายแล้ว = เสียสิทธิ์ขยาย ⇒ นับจาก<b>วันครบกำหนดตามกฎหมาย (วันกระดาษ)</b> · ⚠️ ต้องยืนยันกับนักบัญชี (TAX_PENALTY_AUDIT §3)</summary>
    private static DateTime CountFrom(string remittanceType, int periodYear, int periodMonth, IReadOnlySet<DateTime>? holidays)
        => remittanceType == "SsoSps110"
            ? SsoLateFee.DueDate(periodYear, periodMonth, holidays)
            : TaxFilingDeadline.For(remittanceType, periodYear, periodMonth, holidays).Paper.Date;

    /// <summary>ค่าที่ระบบเสนอสำหรับการจ่ายวันที่ <paramref name="payDate"/> · ภาษี ≤ 0 / จ่ายทันกำหนด ⇒ 0 ทุกก้อน ·
    /// <paramref name="paperFiling"/> = ยื่นแบบกระดาษ (ไม่มีสิทธิ์ขยาย e-Filing) · <paramref name="filedAt"/> = วันที่ยื่นแบบจริง (ถ้ารู้ —
    /// เบี้ยปรับคิดจากการ<b>ยื่น</b>ช้า: ยื่นทันแต่จ่ายช้า ⇒ มีแต่เงินเพิ่ม ไม่มีเบี้ยปรับ · ฝ่ายค้านข้อ 7)</summary>
    public static Suggestion Suggest(string remittanceType, int periodYear, int periodMonth,
        decimal taxAmount, DateTime payDate, IReadOnlySet<DateTime>? holidays, bool paperFiling = false, DateTime? filedAt = null)
    {
        var grace = DueDate(remittanceType, periodYear, periodMonth, holidays, paperFiling);
        var from = CountFrom(remittanceType, periodYear, periodMonth, holidays);
        var pay = payDate.Date;
        var late = pay > grace;
        var daysLate = late ? (int)(pay - from).TotalDays : 0;
        var fineNote = FineNote(remittanceType);

        if (!late || taxAmount <= 0)
            return new Suggestion(grace, daysLate, 0, 0m, false, null,
                late ? "ไม่มียอดภาษีต้องชำระ — ไม่มีเงินเพิ่ม" : "ชำระทันกำหนด — ไม่มีเงินเพิ่ม",
                0m, null, late ? "ไม่มียอดภาษีต้องชำระ" : "ชำระทันกำหนด — ไม่มีเบี้ยปรับ", fineNote);

        var lostExtension = !paperFiling && from < grace;
        decimal surcharge; bool capped; int months; string? sRef; string sNote;
        switch (remittanceType)
        {
            case "SsoSps110":
            {
                var r = SsoLateFee.Compute(periodYear, periodMonth, payDate, taxAmount, holidays);
                surcharge = r.Fee; months = r.MonthsLate;
                capped = taxAmount * 0.02m * months > taxAmount;
                sRef = "SSA-49";
                sNote = $"เงินเพิ่ม 2% ต่อเดือน × {months} เดือน (พ.ร.บ.ประกันสังคม ม.49 · เศษของเดือนนับเป็นหนึ่งเดือน)";
                break;
            }
            case "WhtPnd1": case "WhtPnd2": case "WhtPnd3": case "WhtPnd53": case "WhtPnd54":
                surcharge = RevenueCodeSurcharge.Compute(taxAmount, from, pay);
                capped = RevenueCodeSurcharge.IsCapped(taxAmount, from, pay);
                months = RevenueCodeSurcharge.MonthsLate(from, pay);
                sRef = RevenueCodeSurcharge.LegalReference;
                sNote = $"เงินเพิ่ม 1.5% ต่อเดือน × {months} เดือน (ป.รัษฎากร ม.27 · เศษของเดือนนับเป็นหนึ่งเดือน · ไม่เกินภาษี)";
                break;
            case "VatPp30":
                surcharge = RevenueCodeSurcharge.Compute(taxAmount, from, pay);
                capped = RevenueCodeSurcharge.IsCapped(taxAmount, from, pay);
                months = RevenueCodeSurcharge.MonthsLate(from, pay);
                sRef = "RD-89/1";
                sNote = $"เงินเพิ่ม 1.5% ต่อเดือน × {months} เดือน (ป.รัษฎากร ม.89/1 · เศษของเดือนนับเป็นหนึ่งเดือน · ไม่เกินภาษี)";
                break;
            case "VatPp36":
                surcharge = Pp36Lifecycle.SuggestedSurcharge(taxAmount, from, pay);
                capped = RevenueCodeSurcharge.IsCapped(taxAmount, from, pay);
                months = RevenueCodeSurcharge.MonthsLate(from, pay);
                sRef = "RD-89/1";
                sNote = $"เงินเพิ่ม 1.5% ต่อเดือน × {months} เดือน (ป.รัษฎากร ม.89/1 · คำตัดสินข้อ 135 · ไม่เกินภาษี)";
                break;
            default:
                return new Suggestion(grace, daysLate, 0, 0m, false, null, "แบบนี้ไม่มีสูตรเงินเพิ่มในระบบ — กรอกตามใบเสร็จ",
                    0m, null, "กรอกตามใบเสร็จ", fineNote);
        }
        if (capped) sNote += " — ชนเพดานแล้ว (ไม่เกินจำนวนภาษี)";
        if (lostExtension) sNote += $" · พ้นวันขยาย e-Filing ({grace:dd/MM/yyyy}) ⇒ นับจากวันครบกำหนดตามกฎหมาย {from:dd/MM/yyyy} (ต้องยืนยันกับนักบัญชี)";

        // เบี้ยปรับคิดจากการ "ยื่น" ช้า — รู้วันยื่นจริง ⇒ ใช้วันนั้น · ยังไม่ยื่น ⇒ สมมติยื่นพร้อมวันจ่าย
        var filing = (filedAt ?? payDate).Date;
        var (penalty, pRef, pNote) = filing <= grace
            ? (0m, (string?)null, $"ยื่นแบบทันกำหนด ({filing:dd/MM/yyyy}) — ไม่มีเบี้ยปรับยื่นช้า (มีเฉพาะเงินเพิ่มจากการชำระช้า)")
            : PenaltyFor(remittanceType, taxAmount, (int)(filing - from).TotalDays);
        return new Suggestion(grace, daysLate, months, surcharge, capped, sRef, sNote, penalty, pRef, pNote, fineNote);
    }

    /// <summary>อัตราเบี้ยปรับ ภ.พ.30 ที่ลดแล้ว (ยื่นเองช้า) ตามจำนวนวันที่พ้นกำหนด — 2% / 5% / 10% / 20%</summary>
    private static decimal Pp30ReducedPenaltyRate(int daysLate) => daysLate switch
    {
        <= 0 => 0m,
        <= 15 => 0.02m,
        <= 30 => 0.05m,
        <= 60 => 0.10m,
        _ => 0.20m,
    };

    private static (decimal Amount, string? Ref, string Note) PenaltyFor(string type, decimal tax, int daysLate)
    {
        switch (type)
        {
            case "VatPp30":
            {
                var rate = Pp30ReducedPenaltyRate(daysLate);
                var amt = Math.Round(tax * rate, 2, MidpointRounding.AwayFromZero);
                return (amt, "RD-89",
                    $"เบี้ยปรับ ม.89 ลดเหลือ {rate * 100m:0}% (ยื่นเองช้า {daysLate} วัน · ≤15 วัน 2% · ≤30 วัน 5% · ≤60 วัน 10% · เกิน 20%) — "
                    + "ใช้เมื่อยื่นเองก่อนถูกตรวจ/ได้รับหมายเรียก · ยึดตามใบเสร็จจริง");
            }
            case "VatPp36":
                return (0m, null, "ระบบไม่เสนอเบี้ยปรับ ภ.พ.36 (ต้องยืนยันกับผู้ทำบัญชี) — กรอกตามใบเสร็จถ้ามี");
            case "SsoSps110":
                return (0m, null, "ประกันสังคมไม่มีเบี้ยปรับ — มีเฉพาะเงินเพิ่ม ม.49");
            default:
                return (0m, null, "หัก ณ ที่จ่ายยื่นช้าไม่มีเบี้ยปรับตามแบบ — มีเงินเพิ่ม ม.27 + ค่าปรับอาญา (กรอกช่องค่าปรับ)");
        }
    }

    private static string FineNote(string type) => type switch
    {
        "SsoSps110" => "ค่าปรับทางอาญา/ค่าปรับอื่น (ถ้ามี) — กรอกตามหนังสือแจ้งของสำนักงานประกันสังคม · ระบบไม่เดา",
        "VatPp30" or "VatPp36" => "ค่าปรับอาญา (ถ้ามี) — กรอกตามใบเสร็จ/หนังสือเปรียบเทียบปรับ · ระบบไม่เดา",
        _ => "ค่าปรับอาญา ม.35 (ไม่เกิน 2,000 บาท) — กรอกตามที่สรรพากรเปรียบเทียบปรับ · ระบบไม่เดา",
    };

    /// <summary>ยอดที่จะลงบัญชีจริง — ค่าที่ผู้ใช้ส่งมาชนะเสมอ · ไม่ส่งเงินเพิ่ม = ค่าเสนอเฉพาะแบบที่เดิมทำแบบนั้น
    /// (<see cref="AutoAppliesSurchargeWhenOmitted"/>) · ไม่ส่งเบี้ยปรับ/ค่าปรับ = 0 · <paramref name="includeSurcharge"/>=false ⇒ เงินเพิ่ม 0 ·
    /// ติดลบ ⇒ ปฏิเสธ (ไม่ตัดเป็น 0 เงียบ)</summary>
    public static Applied Resolve(string remittanceType, Suggestion suggested, bool includeSurcharge,
        decimal? surchargeOverride, decimal? penaltyOverride, decimal? otherFineOverride)
    {
        foreach (var (v, label) in new[] { (surchargeOverride, "เงินเพิ่ม"), (penaltyOverride, "เบี้ยปรับ"), (otherFineOverride, "ค่าปรับ") })
            if (v is < 0m)
                throw new BusinessRuleException($"{label}ต้องไม่ติดลบ", NegativeRuleCode);

        var surcharge = !includeSurcharge ? 0m
            : surchargeOverride ?? (AutoAppliesSurchargeWhenOmitted(remittanceType) ? suggested.Surcharge : 0m);
        return new Applied(
            Math.Round(surcharge, 2, MidpointRounding.AwayFromZero),
            Math.Round(penaltyOverride ?? 0m, 2, MidpointRounding.AwayFromZero),
            Math.Round(otherFineOverride ?? 0m, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>เติม JE id ต่อท้ายเครื่องหมาย "JE ที่มีเงินเพิ่ม" (คั่น ",") — ไม่ซ้ำ · ใช้กับ <c>PayrollRun.SsoLateFeeJournalEntryIds</c> (ฝ่ายค้านข้อ 3)</summary>
    public static string AppendJournalId(string? existing, Guid journalEntryId)
    {
        var ids = ParseJournalIds(existing).ToList();
        if (!ids.Contains(journalEntryId)) ids.Add(journalEntryId);
        return string.Join(",", ids);
    }

    /// <summary>อ่านเครื่องหมาย JE id (ข้ามค่าขยะ) — คู่กับ <see cref="AppendJournalId"/></summary>
    public static IEnumerable<Guid> ParseJournalIds(string? stored)
        => (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct();

    /// <summary>คำอธิบายบรรทัด JE ต่อก้อน (ผังค่าปรับ/เงินเพิ่ม · รายจ่ายต้องห้าม §65 ตรี(6))</summary>
    public static IEnumerable<(decimal Amount, string Description)> JournalLines(string form, Applied a, string? surchargeReference)
    {
        if (a.Surcharge > 0)
            yield return (a.Surcharge, $"เงินเพิ่มนำส่ง{form}ช้า{(surchargeReference == null ? "" : $" ({surchargeReference})")} — รายจ่ายต้องห้าม §65 ตรี(6)");
        if (a.Penalty > 0)
            yield return (a.Penalty, $"เบี้ยปรับ{form} — รายจ่ายต้องห้าม §65 ตรี(6)");
        if (a.OtherFine > 0)
            yield return (a.OtherFine, $"ค่าปรับ (อาญา/อื่น ๆ) {form} — รายจ่ายต้องห้าม §65 ตรี(6)");
    }
}
