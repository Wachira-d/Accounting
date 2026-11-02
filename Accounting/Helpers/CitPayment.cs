using System;
using System.Collections.Generic;

namespace Accounting.Helpers;

/// <summary>
/// <b>ชำระภาษีเงินได้นิติบุคคล ภ.ง.ด.51 / ภ.ง.ด.50</b> — ตัวตั้งตัวเดียว (รอบ 228 · TAX_PENALTY_AUDIT §3 ข้อ 1)
///
/// <para>═══ ที่มา ═══ ตั้งหนี้ CIT (<c>FinancialManagementService.PostCITAsync</c> · Cr 21920) มีอยู่แล้ว แต่<b>ไม่มีเส้นชำระ</b>ในระบบ
/// (ล้าง 21920 ด้วยใบสำคัญทั่วไปเอง ⇒ ไม่มีเงินเพิ่ม ม.27 · ไม่มี §67 ตรี · ไม่บวกกลับ §65 ตรี(6)) · และตั้งหนี้ ภ.ง.ด.50 เดิมลง 21920
/// <b>เต็มภาษี</b> ทั้งที่จ่ายจริงเฉพาะส่วนหลังหักเครดิต (ภ.ง.ด.51 ที่จ่ายแล้ว + ภาษีถูกหัก ณ ที่จ่าย) ⇒ 21920 ค้างเท่าเครดิตตลอดไป</para>
///
/// <para>═══ กติกา ═══
/// <list type="bullet">
/// <item>งวด: ภ.ง.ด.51 = (ปี, เดือนสิ้นครึ่งปี) · ภ.ง.ด.50 = (ปี, เดือนสิ้นรอบ) — รอบเดียวกับตัวคำนวณ (<c>CalculateCITAsync</c> ม.ค.–ธ.ค.)</item>
/// <item>กำหนดยื่น (แบบกระดาษ ก่อนเลื่อนวันหยุด): ภ.ง.ด.51 = ภายใน 2 เดือนนับแต่วันสุดท้ายของ 6 เดือนแรก (§67 ทวิ) ·
/// ภ.ง.ด.50 = ภายใน 150 วันนับแต่วันสุดท้ายของรอบบัญชี (§68) — ตารางวันที่อยู่ <see cref="TaxFilingDeadline"/> (เรียกที่นี่)</item>
/// <item>เครดิตหักจากภาษี: ภ.ง.ด.51 ที่จ่ายแล้วก่อน แล้วภาษีถูกหัก ณ ที่จ่าย — ไม่เกินภาษี (ส่วนเกินคงเป็นสิทธิขอคืนในผัง 11920/11910)</item>
/// <item><b>§67 ตรี</b> — ประมาณการกำไรสุทธิ (ภ.ง.ด.51) ขาดเกิน 25% ของกำไรสุทธิจริง (ภ.ง.ด.50) โดยไม่มีเหตุอันสมควร ⇒
/// เงินเพิ่ม 20% ของภาษีที่ชำระขาดตาม §67 ทวิ = 20% × (ภาษีจากกำไรจริง ÷ 2 − ภาษี ภ.ง.ด.51 ที่ชำระ) · <b>ค่าเสนอ</b> (ผู้ใช้แก้ได้ ·
/// "เหตุอันสมควร"/ข้อผ่อนผัน SME เป็นดุลพินิจผู้ทำบัญชี — ต้องยืนยัน)</item>
/// </list></para>
/// </summary>
public static class CitPayment
{
    public const string Pnd51Type = "CitPnd51";
    public const string Pnd50Type = "CitPnd50";
    public const string PayableCode = "21920";
    public const string PrepaidCode = "11920";
    public const string WhtCreditCode = "11910";
    public const string Section67TerReference = "RD-67ter";

    /// <summary>ประมาณการขาดเกินสัดส่วนนี้ของกำไรจริง ⇒ §67 ตรี</summary>
    public const decimal UnderestimateThreshold = 0.25m;
    /// <summary>อัตราเงินเพิ่ม §67 ตรี</summary>
    public const decimal Section67TerRate = 0.20m;
    /// <summary>§68 — ภ.ง.ด.50 ภายใน 150 วันนับแต่วันสุดท้ายของรอบบัญชี</summary>
    public const int Pnd50DaysAfterYearEnd = 150;
    /// <summary>§67 ทวิ — ภ.ง.ด.51 ภายใน 2 เดือนนับแต่วันสุดท้ายของ 6 เดือนแรก</summary>
    public const int Pnd51MonthsAfterHalfEnd = 2;

    public static bool IsCit(string? remittanceType) => remittanceType is Pnd51Type or Pnd50Type;

    /// <summary>แบบนำส่งของรายการคำนวณ CIT (<c>CorporateIncomeTax.TaxPeriod</c>) — null = ไม่รู้จัก</summary>
    public static string? TypeOf(string? taxPeriod) => taxPeriod switch
    {
        "HalfYear" => Pnd51Type,
        "Annual" => Pnd50Type,
        _ => null,
    };

    /// <summary>ปี ค.ศ. จาก <c>TaxYear</c> ที่ผู้ใช้กรอก (พ.ศ. หรือ ค.ศ. — สูตรเดียวกับ <c>CalculateCITAsync</c>) · อ่านไม่ได้ = null</summary>
    public static int? CeYear(string? taxYear)
        => int.TryParse(taxYear?.Trim(), out var y) ? (y > 2400 ? y - 543 : y) : null;

    /// <summary>คีย์งวดของแถวนำส่ง (ปี, เดือนสิ้นงวด)</summary>
    public static (int Year, int Month) PeriodKey(string remittanceType, int ceYear)
        => remittanceType == Pnd51Type ? (ceYear, 6) : (ceYear, 12);

    /// <summary>ป้ายงวดที่แสดงบนจอ (เซิร์ฟเวอร์คิด หน้าแสดง) — ปี พ.ศ.</summary>
    public static string PeriodLabel(string remittanceType, int year)
        => remittanceType == Pnd51Type ? $"ครึ่งปีแรก {year + 543}" : $"รอบปี {year + 543}";

    /// <summary>วันครบกำหนดแบบกระดาษ <b>ก่อน</b>เลื่อนวันหยุด — ผู้เรียกคือ <see cref="TaxFilingDeadline.For(string,int,int,System.Collections.Generic.IReadOnlySet{DateTime}?)"/> เท่านั้น ·
    /// <paramref name="periodEndMonth"/> = เดือนสิ้นงวด (ครึ่งปี/รอบปี)</summary>
    internal static DateTime PaperDueRaw(string remittanceType, int year, int periodEndMonth)
    {
        var periodEnd = new DateTime(year, periodEndMonth, 1).AddMonths(1).AddDays(-1);
        return remittanceType == Pnd51Type
            ? new DateTime(year, periodEndMonth, 1).AddMonths(Pnd51MonthsAfterHalfEnd + 1).AddDays(-1)
            : periodEnd.AddDays(Pnd50DaysAfterYearEnd);
    }

    /// <summary>ภาษีหลังหักเครดิต — ภ.ง.ด.51 ที่จ่ายแล้วก่อน แล้วภาษีถูกหัก ณ ที่จ่าย · ไม่เกินภาษี · ไม่ติดลบ · ปัด 2 ตำแหน่ง AwayFromZero</summary>
    public sealed record Offsets(decimal PrepaidUsed, decimal WhtUsed, decimal Payable);

    public static Offsets Split(decimal grossTax, decimal prepaidCredit, decimal whtCredit)
    {
        var gross = R(Math.Max(0m, grossTax));
        var prepaid = R(Math.Min(Math.Max(0m, prepaidCredit), gross));
        var wht = R(Math.Min(Math.Max(0m, whtCredit), gross - prepaid));
        return new Offsets(prepaid, wht, gross - prepaid - wht);
    }

    /// <summary>บรรทัดหนึ่งของ JE ตั้งหนี้ CIT — บทบาทผัง (ผู้เรียกแปลงเป็นผังจริง) + Dr/Cr</summary>
    public enum AccrualRole { Expense, Prepaid, PrepaidOffset, WhtCredit, Payable }
    public sealed record AccrualLine(AccrualRole Role, decimal Debit, decimal Credit);

    /// <summary>
    /// รอบ 228 ทีมภาษี (ฝ่ายค้าน S1) — รูป JE ตั้งหนี้ (pure · Dr = Cr ทุกกรณี · ผู้เรียกเดียว <c>PostCITAsync</c>):
    /// <list type="bullet">
    /// <item>ภ.ง.ด.51: Dr 11920 = <b>ยอดที่ต้องชำระ</b> (ภาษีครึ่งปี − ภาษีถูกหัก ณ ที่จ่ายที่ใช้) / Cr 21920 เท่ากัน — <b>ไม่แตะ 11910</b>
    /// (ภาษีถูกหักทั้งปีถูกใช้ครั้งเดียวตอน ภ.ง.ด.50 ⇒ 11910 ไม่ถูกล้างซ้ำสองครั้ง)</item>
    /// <item>ภ.ง.ด.50: Dr ค่าใช้จ่าย = ภาษีทั้งปี / Cr เครดิต ภ.ง.ด.51 (= ยอดที่ชำระ) / Cr 11910 ภาษีถูกหักทั้งปี / Cr 21920 สุทธิ</item>
    /// </list>
    /// ⇒ ครบวงจร (ตั้ง ภ.ง.ด.51 → ชำระ → ตั้ง ภ.ง.ด.50 → ชำระ) 11920 และ 11910 กลับเป็นศูนย์ (เทสต์ <c>Round228TaxRemittanceTests</c>)
    /// </summary>
    public static IReadOnlyList<AccrualLine> AccrualPlan(string remittanceType, decimal taxAmount, decimal prepaidCredit, decimal whtCredit)
    {
        var lines = new List<AccrualLine>();
        if (remittanceType == Pnd51Type)
        {
            var o = Split(taxAmount, 0m, whtCredit);
            if (o.Payable > 0m)
            {
                lines.Add(new AccrualLine(AccrualRole.Prepaid, o.Payable, 0m));
                lines.Add(new AccrualLine(AccrualRole.Payable, 0m, o.Payable));
            }
            return lines;
        }
        var a = Split(taxAmount, prepaidCredit, whtCredit);
        if (R(Math.Max(0m, taxAmount)) <= 0m) return lines;
        lines.Add(new AccrualLine(AccrualRole.Expense, R(taxAmount), 0m));
        if (a.PrepaidUsed > 0m) lines.Add(new AccrualLine(AccrualRole.PrepaidOffset, 0m, a.PrepaidUsed));
        if (a.WhtUsed > 0m) lines.Add(new AccrualLine(AccrualRole.WhtCredit, 0m, a.WhtUsed));
        if (a.Payable > 0m) lines.Add(new AccrualLine(AccrualRole.Payable, 0m, a.Payable));
        return lines;
    }

    /// <summary>
    /// ตอนชำระ — แยกหนี้ 21920 ที่ตั้งไว้จริงใน GL (<paramref name="glCredit21920"/> ของ JE ตั้งหนี้) ออกเป็น "จ่ายธนาคาร" + "ล้างเครดิต" ·
    /// ตั้งหนี้แบบใหม่ (รอบ 228: 21920 = ยอดสุทธิแล้ว) ⇒ ไม่มีส่วนล้าง · ตั้งหนี้แบบเดิม (21920 = ภาษีเต็ม) ⇒ ส่วนเกินยอดสุทธิล้างกับ
    /// ภ.ง.ด.51 ที่จ่ายแล้วก่อน แล้วภาษีถูกหัก ณ ที่จ่าย (ส่วนที่เกินทั้งสอง = คงไว้ใน 21920 ให้ผู้ทำบัญชีตรวจ — ไม่เดาผัง)
    /// </summary>
    public static Offsets AtPayment(decimal glCredit21920, decimal netTaxPayable, decimal prepaidCredit, decimal whtCredit)
    {
        var gl = R(Math.Max(0m, glCredit21920));
        var net = R(Math.Max(0m, netTaxPayable));
        if (gl <= net + 0.005m) return new Offsets(0m, 0m, gl);
        var excess = gl - net;
        var prepaid = R(Math.Min(Math.Max(0m, prepaidCredit), excess));
        var wht = R(Math.Min(Math.Max(0m, whtCredit), excess - prepaid));
        return new Offsets(prepaid, wht, net);
    }

    /// <summary>ข้อเท็จจริงของ §67 ตรี — ประมาณการทั้งปีที่แสดงใน ภ.ง.ด.51 · กำไรสุทธิจริง + ภาษีจริงของ ภ.ง.ด.50 ·
    /// <paramref name="HalfYearTaxPaid"/> = ภาษีที่ชำระตาม ภ.ง.ด.51 <b>จริง</b> (ยอดนำส่ง + ภาษีถูกหัก ณ ที่จ่ายที่ใช้ในครึ่งปี · ฝ่ายค้าน S5a) ·
    /// <paramref name="PaidBasis"/> = บอกว่ายอดชำระมาจากไหน (แสดงในข้อความ) · <paramref name="PossiblyFirstYear"/> = บริษัทเริ่มในรอบนี้ (รอบแรก &lt; 12 เดือนไม่ต้องยื่น ภ.ง.ด.51)</summary>
    public sealed record EstimateFacts(decimal EstimatedAnnualProfit, decimal ActualAnnualProfit, decimal ActualAnnualTax, decimal HalfYearTaxPaid,
        string? PaidBasis = null, bool PossiblyFirstYear = false);

    public sealed record EstimatePenalty(decimal Amount, bool Applies, string Note);

    /// <summary>§67 ตรี — เงินเพิ่ม 20% ของภาษีที่ชำระขาดตาม §67 ทวิ เมื่อประมาณการขาดเกิน 25% ของกำไรจริง (ค่าเสนอ)</summary>
    public static EstimatePenalty Section67Ter(EstimateFacts f)
    {
        if (f.ActualAnnualProfit <= 0m)
            return new EstimatePenalty(0m, false, "กำไรสุทธิจริงไม่เกิน 0 — ไม่มีเงินเพิ่ม §67 ตรี");
        var shortPct = (f.ActualAnnualProfit - f.EstimatedAnnualProfit) / f.ActualAnnualProfit;
        if (shortPct <= UnderestimateThreshold)
            return new EstimatePenalty(0m, false,
                $"ประมาณการ ภ.ง.ด.51 ขาด {Math.Max(0m, shortPct) * 100m:0.##}% ของกำไรจริง (ไม่เกิน 25%) — ไม่มีเงินเพิ่ม §67 ตรี");
        var shortTax = R(Math.Max(0m, f.ActualAnnualTax / 2m - f.HalfYearTaxPaid));
        var amt = R(shortTax * Section67TerRate);
        return new EstimatePenalty(amt, amt > 0m,
            $"§67 ตรี: ประมาณการกำไร {f.EstimatedAnnualProfit:N2} ขาด {shortPct * 100m:0.##}% ของกำไรจริง {f.ActualAnnualProfit:N2} (เกิน 25%) ⇒ "
            + $"เงินเพิ่ม 20% × ภาษีที่ชำระขาด {shortTax:N2} (ภาษีจริง ÷ 2 − ภ.ง.ด.51 ที่ชำระ {f.HalfYearTaxPaid:N2}"
            + (f.PaidBasis == null ? "" : $" · ฐาน: {f.PaidBasis}") + $") = {amt:N2} · "
            + "ตรวจสอบ: ไม่ต้องเสียถ้ามีเหตุอันสมควร · ผู้ยื่น ภ.ง.ด.51 จากงบครึ่งปีจริง (บจ./สถาบันการเงิน §67 ทวิ(2)) ไม่อยู่ใต้ §67 ตรี"
            + (f.PossiblyFirstYear ? " · ⚠️ บริษัทอาจอยู่ในรอบบัญชีแรก (< 12 เดือน ไม่ต้องยื่น ภ.ง.ด.51) — ตรวจสอบ" : "")
            + " — ระบบไม่เติมลงช่อง ผู้ทำบัญชียืนยันแล้วกรอกเอง");
    }

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
