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
        string FineNote,
        // รอบ 228 ทีมภาษี: เบี้ยปรับที่เสนอจากกติกาที่ยังไม่ยืนยัน (ภ.พ.36) — หน้าแสดงเป็นข้อมูล แต่ไม่เติมลงช่อง (ผู้ใช้กรอกตามใบเสร็จเอง)
        bool PenaltyNeedsConfirmation = false,
        // ฝ่ายค้าน S4: เงินเพิ่มจากกติกาที่ยังไม่ยืนยัน (กองทุนเงินทดแทน ม.46) — ข้อมูลเท่านั้น ไม่เติมช่อง
        bool SurchargeNeedsConfirmation = false,
        // ฝ่ายค้าน S6: เงินเพิ่ม §67 ตรี (ภ.ง.ด.50) แยกก้อน/ข้อความของตัวเอง — ข้อมูลเท่านั้น ไม่รวมในเงินเพิ่ม ม.27 และไม่เติมช่อง
        decimal Section67Ter = 0m,
        string? Section67TerNote = null)
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
        decimal taxAmount, DateTime payDate, IReadOnlySet<DateTime>? holidays, bool paperFiling = false, DateTime? filedAt = null,
        CitPayment.EstimateFacts? citEstimate = null)
    {
        var s = SuggestCore(remittanceType, periodYear, periodMonth, taxAmount, payDate, holidays, paperFiling, filedAt);
        // รอบ 228 — ภ.ง.ด.50: เงินเพิ่ม §67 ตรี (ประมาณการ ภ.ง.ด.51 ขาดเกิน 25%) ไม่ขึ้นกับการจ่ายช้า · ฝ่ายค้าน S6: ก้อน/ข้อความของตัวเอง
        // (ไม่รวมในเงินเพิ่ม ม.27 · ข้อความ "ชำระทันกำหนด" คงเดิม · ไม่เติมช่อง — "เหตุอันสมควร"/ข้อยกเว้นผู้ทำบัญชีตัดสิน) · ไม่มีข้อเท็จจริง ⇒ บอกว่าไม่ได้คำนวณ
        if (remittanceType != CitPayment.Pnd50Type) return s;
        if (citEstimate == null)
            return s with { Section67TerNote = "§67 ตรี: ไม่พบ ภ.ง.ด.51 ของรอบนี้ในระบบ — ระบบไม่คำนวณเงินเพิ่มประมาณการขาด (ถ้ามี กรอกรวมในช่องเงินเพิ่ม)" };
        var p67 = CitPayment.Section67Ter(citEstimate);
        return s with { Section67Ter = p67.Amount, Section67TerNote = p67.Note };
    }

    private static Suggestion SuggestCore(string remittanceType, int periodYear, int periodMonth,
        decimal taxAmount, DateTime payDate, IReadOnlySet<DateTime>? holidays, bool paperFiling, DateTime? filedAt)
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
            case CitPayment.Pnd51Type: case CitPayment.Pnd50Type:
                surcharge = RevenueCodeSurcharge.Compute(taxAmount, from, pay);
                capped = RevenueCodeSurcharge.IsCapped(taxAmount, from, pay);
                months = RevenueCodeSurcharge.MonthsLate(from, pay);
                sRef = RevenueCodeSurcharge.LegalReference;
                sNote = $"เงินเพิ่ม 1.5% ต่อเดือน × {months} เดือน (ป.รัษฎากร ม.27 · {(remittanceType == CitPayment.Pnd51Type ? "ภ.ง.ด.51 §67 ทวิ" : "ภ.ง.ด.50 §68")} · เศษของเดือนนับเป็นหนึ่งเดือน · ไม่เกินภาษี)";
                break;
            case WorkersCompensationBase.RemittanceType:
            {
                var w = WorkersCompensationBase.LateSurcharge(taxAmount, from, pay);
                surcharge = w.Amount; capped = w.Capped; months = w.Months;
                sRef = WorkersCompensationBase.LegalReference;
                sNote = $"เงินเพิ่ม {WorkersCompensationBase.LateSurchargePercentPerMonth:0.##}% ต่อเดือน × {months} เดือน (พ.ร.บ.เงินทดแทน ม.46 · ไม่เกินเงินสมทบ · "
                    + "อัตรา (2% ตามฉบับแก้ไข 2561 หรือ 3%) และการนับเศษเดือนต้องยืนยันกับผู้ทำบัญชี — ระบบไม่เติมลงช่อง กรอกตามใบเสร็จ)";
                break;
            }
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
        return new Suggestion(grace, daysLate, months, surcharge, capped, sRef, sNote, penalty, pRef, pNote, fineNote,
            PenaltyNeedsConfirmation: penalty > 0m && remittanceType == "VatPp36",
            SurchargeNeedsConfirmation: surcharge > 0m && remittanceType == WorkersCompensationBase.RemittanceType);
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

    /// <summary>ป้าย "ค่าเสนอ — ยังไม่ยืนยันเลขระเบียบ" (AUDIT_249 §3.3 N5 · ระบบไม่ลงบัญชีเบี้ยปรับเอง ผู้ใช้ตัดสินตอนยื่นจริง) — ตัวเดียว</summary>
    public const string PendingRegulationNote = "ค่าเสนอ — ยังไม่ยืนยันเลขระเบียบกรมสรรพากรของตารางลดเบี้ยปรับ";

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
                    + "ใช้เมื่อยื่นเองก่อนถูกตรวจ/ได้รับหมายเรียก · ยึดตามใบเสร็จจริง · "
                    // รอบ 252 WP-13 (AUDIT_249 §3.3 N5): ตารางลดเบี้ยปรับยังไม่ได้ยืนยันเลขระเบียบกับผู้ทำบัญชี ⇒ บอกในข้อความที่หน้าแสดง (tooltip/คำอธิบายช่อง)
                    + PendingRegulationNote);
            }
            case "VatPp36":
            {
                // รอบ 228 (TAX_PENALTY_AUDIT §3 ข้อ 5): เสนอตารางลดเบี้ยปรับชุดเดียวกับ ภ.พ.30 (ยื่นเองช้า) — ข้อมูลเท่านั้น: API ไม่ลงเองเมื่อไม่ส่งค่า
                // (Resolve: ไม่ส่ง = 0) · หน้าไม่เติมลงช่อง (Suggestion.PenaltyNeedsConfirmation ⇒ PendingRemittanceItem.PenaltyPrefill = 0) ·
                // ⚠️ ต้องยืนยันกับผู้ทำบัญชีว่าระเบียบลดเบี้ยปรับครอบ ภ.พ.36
                var rate = Pp30ReducedPenaltyRate(daysLate);
                var amt = Math.Round(tax * rate, 2, MidpointRounding.AwayFromZero);
                return (amt, "RD-89",
                    $"เบี้ยปรับ ภ.พ.36 ยื่นเองช้า {daysLate} วัน — ถ้าระเบียบลดเบี้ยปรับชุดเดียวกับ ภ.พ.30 ใช้ได้ = {rate * 100m:0}% ≈ {amt:N2} บาท · "
                    + "ต้องยืนยันกับผู้ทำบัญชี — ระบบไม่เติมลงช่อง กรอกตามใบเสร็จจริง");
            }
            case CitPayment.Pnd51Type: case CitPayment.Pnd50Type:
                return (0m, null, "ภ.ง.ด.50/51 ชำระช้ามีเงินเพิ่ม ม.27 · ยื่นช้ามีค่าปรับอาญา ม.35 (กรอกช่องค่าปรับ) — ระบบไม่เสนอเบี้ยปรับ (มีเมื่อถูกประเมิน)");
            case WorkersCompensationBase.RemittanceType:
                return (0m, null, "กองทุนเงินทดแทนไม่มีเบี้ยปรับตามแบบ — มีเฉพาะเงินเพิ่ม ม.46");
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
        WorkersCompensationBase.RemittanceType => "ค่าปรับ (ถ้ามี) — กรอกตามหนังสือแจ้งของสำนักงานประกันสังคม (กองทุนเงินทดแทน) · ระบบไม่เดา",
        CitPayment.Pnd51Type or CitPayment.Pnd50Type => "ค่าปรับอาญา ม.35 (ยื่นแบบช้า) — กรอกตามที่สรรพากรเปรียบเทียบปรับ · ระบบไม่เดา",
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

    /// <summary>รอบ 228 ทีมภาษี — เครื่องหมายในคำอธิบายบรรทัดค่าปรับที่ <see cref="JournalLines"/> เขียน · ตัวบวกกลับ §65 ตรี(6) ใช้แยกบรรทัดค่าปรับ
    /// ออกจากบรรทัดค่าใช้จ่ายที่หักได้ใน JE เดียวกัน (<see cref="ExpenseLinesMixed"/>)</summary>
    public const string Section65Ter6Marker = "รายจ่ายต้องห้าม §65 ตรี(6)";

    /// <summary>แบบที่ JE นำส่งมีบรรทัดค่าใช้จ่าย<b>ที่หักได้</b>ปนกับค่าปรับ (กท.20ก ส่วนต่าง 54121 · ค่าอากร · ภ.ง.ด.50 ล้างเครดิตแบบเดิมที่ 580xx) —
    /// ตัวบวกกลับต้องนับเฉพาะบรรทัดที่มี <see cref="Section65Ter6Marker"/></summary>
    public static bool ExpenseLinesMixed(string remittanceType)
        => remittanceType is WorkersCompensationBase.RemittanceType or StampDutySchedule.RemittanceType
            or CitPayment.Pnd51Type or CitPayment.Pnd50Type;

    /// <summary>คำอธิบายบรรทัด JE ต่อก้อน (ผังค่าปรับ/เงินเพิ่ม · รายจ่ายต้องห้าม §65 ตรี(6))</summary>
    public static IEnumerable<(decimal Amount, string Description)> JournalLines(string form, Applied a, string? surchargeReference)
    {
        if (a.Surcharge > 0)
            yield return (a.Surcharge, $"เงินเพิ่มนำส่ง{form}ช้า{(surchargeReference == null ? "" : $" ({surchargeReference})")} — {Section65Ter6Marker}");
        if (a.Penalty > 0)
            yield return (a.Penalty, $"เบี้ยปรับ{form} — {Section65Ter6Marker}");
        if (a.OtherFine > 0)
            yield return (a.OtherFine, $"ค่าปรับ (อาญา/อื่น ๆ) {form} — {Section65Ter6Marker}");
    }
}
