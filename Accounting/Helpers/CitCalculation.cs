using System;
using System.Collections.Generic;
using System.Linq;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// <b>เครื่องคำนวณภาษีเงินได้นิติบุคคล (หน้าบริหารการเงิน) — กติกาตัวตั้งตัวเดียว</b> (รอบ 230 · TAX_PENALTY_AUDIT §5 "รู้แล้วแต่ไม่แก้" → §6)
///
/// <para>═══ ที่มา ═══ <c>FinancialManagementService.CalculateCITAsync</c> เดิม: (1) ขั้นบันได SME กับทุกบริษัท (ไม่ดูทุน/รายได้) ·
/// (2) รอบปี ม.ค.–ธ.ค. เสมอ · (3) ภ.ง.ด.51 คิดภาษีจากกำไร 6 เดือนตรง ๆ (ไม่ใช่ ประมาณการทั้งปี ÷ 2 ตาม §67 ทวิ) ·
/// (4) หักค่าใช้จ่ายภาษีเงินได้ (580xx) เป็นรายจ่าย (§65 ตรี(6 ทวิ)) และไม่บวกกลับ §65 ตรี อัตโนมัติ ·
/// (5) JE ตั้งหนี้ลงวันที่กดบันทึก — ทั้งหมดนี้ทำให้ตัวเลขต่างจาก ภ.ง.ด.50 ของหน้ารายงานภาษีบนข้อมูลชุดเดียวกัน</para>
///
/// <para>G6: pure ทั้งหมด (ยกเว้น <see cref="ExceptCitExpense"/> ที่เป็นตัวกรอง IQueryable — EF แปลเป็น SQL) · อัตราอยู่ที่ <see cref="CitRateTable"/> ·
/// ช่วงรอบอยู่ที่ <see cref="FiscalYear"/> · รูป JE ตั้งหนี้อยู่ที่ <see cref="CitPayment"/></para>
/// </summary>
public static class CitCalculation
{
    public const string HalfYear = "HalfYear";
    public const string Annual = "Annual";

    /// <summary>ภ.ง.ด.51 ตาม §67 ทวิ(1) — ประมาณการกำไรสุทธิทั้งรอบ × อัตรา ÷ 2 (ค่าตั้งต้น)</summary>
    public const string BasisEstimate = "Estimate";
    /// <summary>ภ.ง.ด.51 ตาม §67 ทวิ(2) — บริษัทจดทะเบียนในตลาดหลักทรัพย์ / ธนาคาร / สถาบันการเงิน ฯลฯ ยื่นจากกำไรสุทธิจริงของรอบครึ่งปี (งบที่ผู้สอบบัญชีสอบทาน)</summary>
    public const string BasisActualHalfYear = "ActualHalfYear";

    /// <summary>ผังค่าใช้จ่ายภาษีเงินได้ (ผังมาตรฐาน 580 / 58000) — §65 ตรี(6 ทวิ) ห้ามนำมาหักกำไร</summary>
    public const string CitExpensePrefix = "580";

    public const string SmeOverrideRuleCode = "CIT-SME-OVERRIDE-REASON";
    public const string Pnd51ExemptRuleCode = "CIT-PND51-EXEMPT";
    public const string PeriodClosedRuleCode = "CIT-POST-PERIOD-CLOSED";
    public const string PeriodNotEndedRuleCode = "CIT-POST-PERIOD-NOT-ENDED";
    /// <summary>ฝ่ายค้าน S3 — รายการคำนวณก่อนรอบ 230 ไม่มีวันสิ้นงวด ⇒ ต้องคำนวณใหม่ก่อนลงบัญชี</summary>
    public const string RecalculateRuleCode = "CIT-POST-RECALCULATE";
    /// <summary>ฝ่ายค้าน S8 — ภาษี 0 ⇒ บันทึกว่า "ไม่มีภาษีต้องชำระ" (ไม่ใช่ "Filed" — ไม่ได้ยื่น/ตั้งหนี้อะไร)</summary>
    public const string NoTaxDueStatus = "NoTaxDue";

    /// <summary>รอบภาษีที่รู้จัก — null = ไม่รู้จัก</summary>
    public static string? NormalizePeriod(string? taxPeriod) => taxPeriod switch
    {
        HalfYear => HalfYear,
        Annual => Annual,
        _ => null,
    };

    // ══════════════ (2) รอบบัญชีของบริษัท ══════════════

    /// <summary>ช่วงวันของงวดภาษี — ภ.ง.ด.51 = 6 เดือนแรกของรอบบัญชี · ภ.ง.ด.50 = ทั้งรอบ · <paramref name="FiscalYear"/> = ปี ค.ศ. ที่รอบเริ่ม</summary>
    public sealed record TaxPeriodRange(int FiscalYear, DateTime Start, DateTime EndInclusive, bool IsHalfYear, FiscalYearRange FullYear)
    {
        /// <summary>วันที่ลงบัญชีตั้งหนี้ = วันสุดท้ายของงวด (ไม่ใช่วันที่กดบันทึก) — ค่าใช้จ่าย/ภาษีจ่ายล่วงหน้าอยู่ในงวดที่ภาษีเกิด</summary>
        public DateTime PostingDate => EndInclusive;
    }

    /// <summary>งวดภาษีของรอบบัญชีปี <paramref name="ceFiscalYear"/> ตามเดือนเริ่มรอบของบริษัท (<c>Company.FiscalYearStartMonth</c> · 0/นอกช่วง = ม.ค.)</summary>
    public static TaxPeriodRange PeriodFor(int ceFiscalYear, int fiscalYearStartMonth, string taxPeriod)
    {
        var fy = FiscalYear.RangeFor(ceFiscalYear, fiscalYearStartMonth);
        var half = taxPeriod == HalfYear;
        return new TaxPeriodRange(ceFiscalYear, fy.Start, half ? fy.HalfEndInclusive : fy.EndInclusive, half, fy);
    }

    /// <summary>
    /// (5) ด่านวันที่ลงบัญชีตั้งหนี้ — null = ลงได้ · อื่น ๆ = ข้อความปฏิเสธพร้อมทางไปต่อ
    /// <para>งวดยังไม่สิ้นสุด ⇒ ปฏิเสธ (ภาษีของงวดที่ยังไม่จบยังไม่ใช่หนี้ · กันวันที่ลงบัญชีในอนาคต) ·
    /// งวดบัญชีของวันสิ้นงวดปิดแล้ว ⇒ ปฏิเสธ (ข้อความจากตัวตัดสินงวดปิดตัวเดียว <c>JournalEntryBuilder.ClosedPeriodReasonAsync</c>)</para>
    /// </summary>
    /// <param name="todayBangkok">วันนี้ตามเวลาไทย</param>
    /// <param name="closedPeriodReason">ผลของ <c>JournalEntryBuilder.ClosedPeriodReasonAsync</c> ที่วันสิ้นงวด (null = เปิด)</param>
    public static (string? Message, string? RuleCode) PostingRefusal(TaxPeriodRange period, DateTime todayBangkok, string? closedPeriodReason)
    {
        var label = period.IsHalfYear ? "ครึ่งปีแรก" : "รอบบัญชี";
        if (period.PostingDate.Date > todayBangkok.Date)
            return ($"{label} {period.FiscalYear + 543} สิ้นสุดวันที่ {period.PostingDate:dd/MM/yyyy} ซึ่งยังไม่ถึง — ตั้งหนี้ภาษีได้หลังสิ้นงวด "
                + "(ระบบลงบัญชีตั้งหนี้ ณ วันสิ้นงวด ไม่ใช่วันที่กด) · คำนวณดูตัวเลขได้ตามปกติ", PeriodNotEndedRuleCode);
        if (!string.IsNullOrWhiteSpace(closedPeriodReason))
            return ($"ตั้งหนี้ภาษีเงินได้นิติบุคคล{label} {period.FiscalYear + 543} ต้องลงวันที่สิ้นงวด {period.PostingDate:dd/MM/yyyy} แต่งวดบัญชีนั้นปิดแล้ว ({closedPeriodReason}) — "
                + "ให้ผู้มีสิทธิ์เปิดงวดนั้นที่หน้า “งวดบัญชี” แล้วกด “บันทึก” รายการภาษีนี้อีกครั้ง (ภาษีของงวดต้องอยู่ในงบของงวดนั้น · ระบบไม่ย้ายไปลงวันที่อื่นเอง)",
                PeriodClosedRuleCode);
        return (null, null);
    }

    // ══════════════ (1) SME ══════════════

    /// <summary>ผลตัดสิน SME ของการคำนวณ — <paramref name="Overridden"/> = ผู้ใช้กำหนดเอง (เหตุผลอยู่ใน <paramref name="Reason"/>)</summary>
    public sealed record SmeDecision(bool IsSme, string Reason, bool Overridden);

    /// <summary>
    /// SME = ทุนที่ชำระแล้ว ≤ 5 ล. <b>และ</b> รายได้ทั้งรอบ ≤ 30 ล. (เกณฑ์ที่ <see cref="CitRateTable.IsSme"/> ตัวเดียว) — ไม่รู้ทุน = ไม่ใช่ SME (Q10) ·
    /// ผู้ใช้กำหนดเองได้ (<paramref name="overrideIsSme"/>) แต่<b>ต้องมีเหตุผล</b> (เช่น ทุนชำระ ณ วันสิ้นรอบต่างจากทุนปัจจุบันในระบบ) — ไม่มีเหตุผล ⇒ ปฏิเสธ
    /// </summary>
    /// <param name="annualRevenue">รายได้ทั้งรอบ — ภ.ง.ด.51 ผู้เรียกส่งรายได้ครึ่งปี × 2 (ห้ามส่งยอดครึ่งเดียว)</param>
    public static SmeDecision DecideSme(decimal? paidUpCapital, decimal annualRevenue, bool? overrideIsSme, string? overrideReason)
    {
        var auto = CitRateTable.IsSme(paidUpCapital, annualRevenue);
        var autoReason = CitRateTable.SmeReason(paidUpCapital, annualRevenue);
        if (overrideIsSme is not bool forced) return new SmeDecision(auto, autoReason, false);
        if (string.IsNullOrWhiteSpace(overrideReason))
            throw new BusinessRuleException(
                "กำหนดสถานะ SME เอง ต้องระบุเหตุผล (เช่น ทุนที่ชำระแล้ว ณ วันสิ้นรอบบัญชี) — หรือปล่อยว่างให้ระบบตัดสินจากทุนและรายได้", SmeOverrideRuleCode);
        return new SmeDecision(forced,
            $"{(forced ? "SME" : "ไม่ใช่ SME")} — ผู้ใช้กำหนดเอง: {overrideReason.Trim()} (ระบบตัดสินจากข้อมูลว่า: {autoReason})", true);
    }

    // ══════════════ (4) กำไรสุทธิทางภาษี ══════════════

    /// <summary>
    /// ฝ่ายค้าน S7 — ผังค่าใช้จ่ายภาษีเงินได้ (§65 ตรี(6 ทวิ) ห้ามหักกำไร) = ผังค่าใช้จ่าย <b>หัวหมวด "580"</b> · ผัง 5 หลัก <b>58000–58099</b> · หรือผังลูกของหัวหมวด 580 ·
    /// รหัสอื่นที่ขึ้นต้น 580 (เช่นผังที่ลูกค้าตั้งเอง "5800"/"580500") ไม่ถูกตัด — ยังหักได้ (ทิศที่ไม่ทำให้ตัดค่าใช้จ่ายจริงทิ้ง)
    /// <para>กติกาเดียวกับตัวกรอง SQL <see cref="ExceptCitExpense"/> (เทสต์ล็อกสองตัวด้วยชุดกรณีเดียวกัน) · ผู้เรียก: ตัวหาผังสำรองใน <c>PostCITAsync</c> · ผังตัดสูญ 11910 (<c>WhtCreditWriteOff</c>)</para>
    /// <para>รอบ 243a: เดิมเทียบ <c>StartsWith("5800")</c> ⇒ จับได้แค่ 58000–58009 ขัดกับที่เขียนไว้ "58000–58099" และเทสต์ <c>58010 ⇒ true</c> — ผัง 58010 ที่ไม่ผูกแม่ 580 หลุดไปเป็นรายจ่ายหักได้</para>
    /// </summary>
    public static bool IsCitExpenseAccount(string? accountCode, AccountType accountType, string? parentAccountCode)
        => accountType == AccountType.Expense && accountCode != null
        && (accountCode == CitExpensePrefix
            || (accountCode.Length == 5 && accountCode.StartsWith(CitExpensePrefix, StringComparison.Ordinal))
            || parentAccountCode == CitExpensePrefix);

    /// <summary>ตัวกรองบรรทัด GL — ตัดบรรทัดค่าใช้จ่ายภาษีเงินได้ออก (กติกา <see cref="IsCitExpenseAccount"/> · EF แปลเป็น SQL) ·
    /// ผู้เรียก: CalculateCITAsync · ภ.ง.ด.50 (TaxService) · ภ.ง.ด.51 (TaxFilingExportService)</summary>
    public static IQueryable<JournalEntryLine> ExceptCitExpense(this IQueryable<JournalEntryLine> lines)
        => lines.Where(l => !(l.Account!.AccountType == AccountType.Expense
            && (l.Account.AccountCode == CitExpensePrefix
                || (l.Account.AccountCode.Length == 5 && l.Account.AccountCode.StartsWith(CitExpensePrefix))
                || (l.Account.ParentAccount != null && l.Account.ParentAccount.AccountCode == CitExpensePrefix))));

    /// <summary>ผังค่ารับรองมาตรฐาน (ผังมาตรฐาน 54460 "ค่ารับรอง / เลี้ยงรับรอง")</summary>
    public const string EntertainmentAccountCode = "54460";

    /// <summary>
    /// ฝ่ายค้าน S6 — ผังค่ารับรอง (§65 ตรี(4)) ด้วยกติกาที่ระบุชัด: รหัส 54460 หรือผังลูก (ขึ้นต้น 54460) · หรือชื่อมี "รับรอง" / "entertainment" ·
    /// <b>ไม่ใช้</b> "ภาษีซื้อขอคืนไม่ได้" (เดิม) — ผังอื่นที่ขอคืน VAT ไม่ได้ (รถยนต์นั่ง ฯลฯ) ถูกนับเป็นค่ารับรองแล้วบวกกลับเกินจริง · ไม่มีธงเฉพาะบนผังในระบบ
    /// </summary>
    public static bool IsEntertainmentAccount(string? accountCode, string? accountName)
        => (accountCode != null && accountCode.StartsWith(EntertainmentAccountCode, StringComparison.Ordinal))
        || (accountName != null && (accountName.Contains("รับรอง", StringComparison.Ordinal)
            || accountName.Contains("entertainment", StringComparison.OrdinalIgnoreCase)));

    // ══════════════ ค่าเสื่อมที่ยังไม่อยู่ใน GL (คำตัดสิน coordinator รอบ 230 — เดิม ภ.ง.ด.50 บวกค่าเสื่อมทุกแถวซ้ำกับ GL) ══════════════

    /// <summary>
    /// แถวค่าเสื่อม (<c>AssetDepreciation</c>) ที่<b>ยังไม่ลงบัญชี</b> — นับเพิ่มเป็นค่าใช้จ่ายทางภาษีได้ · แถวที่ลงบัญชีแล้ว (<c>IsPosted</c> หรือมี <c>JournalEntryId</c>)
    /// อยู่ในค่าใช้จ่าย GL ของงวดแล้ว (<c>FixedAssetService</c> ลง JE วันสิ้นเดือน Dr ผังค่าเสื่อม / Cr ค่าเสื่อมสะสม แล้วประทับ IsPosted + JournalEntryId)
    /// ⇒ นับอีก = หักซ้ำ · ต้องไม่ลงบัญชี<b>ทั้งสองธง</b> (ธงใดธงหนึ่งบอกว่าลงแล้ว = ไม่นับ — ทิศที่ไม่หักซ้ำ) · EF แปลเป็น SQL ·
    /// ผู้เรียก: <c>Tax/CitPeriodFigures.UnbookedDepreciationAsync</c> (ภ.ง.ด.50 + เครื่องคำนวณ + ไฟล์ ภ.ง.ด.51)
    /// </summary>
    public static IQueryable<AssetDepreciation> Unbooked(this IQueryable<AssetDepreciation> rows)
        => rows.Where(d => !d.IsPosted && d.JournalEntryId == null);

    // ══════════════ ฝ่ายค้าน S4 — ผลขาดทุนสุทธิยกมา 5 รอบ (§65 ตรี(12)) ══════════════

    /// <summary>
    /// ผลขาดทุนยกมาที่ยังใช้ได้ ณ รอบ <paramref name="fiscalYear"/> — ขาดทุนของรอบ Y ใช้ได้ถึงรอบ Y+5 · รอบที่มีกำไรกินโควตาเก่าสุดก่อน (FIFO)
    /// <para>ตัวตั้งเดียวของ ภ.ง.ด.50 (<c>TaxService.GenerateCitReport</c>) และค่าตั้งต้นช่องผลขาดทุนยกมาของเครื่องคำนวณหน้าบริหารการเงิน ·
    /// ฐาน = รายงาน ภ.ง.ด.50 ที่ยื่น/ประกาศว่ายื่นแล้วของรอบก่อน (กำไรสุทธิก่อนหักขาดทุนยกมา)</para>
    /// </summary>
    /// <param name="priorYears">(รอบ, กำไรสุทธิ) ของรอบก่อน — ลำดับใดก็ได้ · ติดลบ = ขาดทุน</param>
    public static decimal LossCarryForwardAvailable(IEnumerable<(int Year, decimal NetProfit)> priorYears, int fiscalYear)
    {
        var pool = new List<(int Year, decimal Remaining)>();
        foreach (var pr in priorYears.Where(p => p.Year < fiscalYear).OrderBy(p => p.Year))
        {
            pool.RemoveAll(x => pr.Year > x.Year + 5);
            if (pr.NetProfit < 0) { pool.Add((pr.Year, Math.Abs(pr.NetProfit))); continue; }
            var left = pr.NetProfit;
            for (var i = 0; i < pool.Count && left > 0; i++)
            {
                var use = Math.Min(pool[i].Remaining, left);
                pool[i] = (pool[i].Year, pool[i].Remaining - use);
                left -= use;
            }
            pool.RemoveAll(x => x.Remaining <= 0.005m);
        }
        pool.RemoveAll(x => fiscalYear > x.Year + 5);
        return R(pool.Sum(x => x.Remaining));
    }

    /// <summary>ส่วนที่ใช้ได้ในรอบนี้ = MIN(ที่มี, กำไรก่อนหัก) · กำไร ≤ 0 = 0</summary>
    public static decimal LossCarryForwardUsed(decimal available, decimal profitBeforeLoss)
        => profitBeforeLoss > 0m ? Math.Min(Math.Max(0m, available), profitBeforeLoss) : 0m;

    // ══════════════ ฝ่ายค้าน S5 — เครดิตภาษีถูกหัก ณ ที่จ่าย ══════════════

    /// <summary>
    /// หนังสือรับรองที่เครดิตได้ของงวด — สถานะ Received/Claimed (Pending ยังเครดิตไม่ได้ — WHT_CREDIT_PLAN L1) และ<b>วันที่บนหนังสือรับรองอยู่ในงวด</b>
    /// (รอบบัญชีของบริษัท · ครึ่งปี = 6 เดือนแรก) · ไม่มีวันที่ ⇒ ไม่นับ (ผู้เรียกบอกจำนวนให้ผู้ใช้ตรวจ) · ตัวตั้งเดียวของเครื่องคำนวณ + ภ.ง.ด.50
    /// </summary>
    public static IQueryable<WhtCreditReceived> CreditableInPeriod(this IQueryable<WhtCreditReceived> q, Guid companyId, DateTime start, DateTime endInclusive)
    {
        var endExclusive = endInclusive.Date.AddDays(1);
        // รอบ 252 (WP-09): แถว Pending ที่ได้ใบบางส่วนแล้ว (ReceivedAmount > 0 — ใบรวม/ผ่อนงวด) ก็เครดิตได้ <b>เฉพาะส่วนที่ได้ใบ</b> ⇒
        // ผู้เรียกต้องรวมยอดด้วย Helpers/WhtCreditableAmount.Of (ห้าม Sum(WhtAmount) ตรง — จะนับเต็มแถว)
        return q.Where(w => w.CompanyId == companyId
            && (w.Status == WhtCreditStatus.Received || w.Status == WhtCreditStatus.Claimed
                || (w.Status == WhtCreditStatus.Pending && w.ReceivedAmount > 0.005m))
            && w.CertificateDate != null && w.CertificateDate >= start && w.CertificateDate < endExclusive);
    }

    /// <summary>หนังสือรับรองที่ได้รับแล้วแต่ไม่มีวันที่ (นับเข้างวดไม่ได้) — ผู้เรียกแสดงเป็นคำเตือน</summary>
    public static IQueryable<WhtCreditReceived> CreditableUndated(this IQueryable<WhtCreditReceived> q, Guid companyId, int fiscalYear)
        => q.Where(w => w.CompanyId == companyId && w.CertificateDate == null && w.TaxYear == fiscalYear
            && (w.Status == WhtCreditStatus.Received || w.Status == WhtCreditStatus.Claimed
                || (w.Status == WhtCreditStatus.Pending && w.ReceivedAmount > 0.005m)));

    /// <summary>รอบ 252 (WP-09): ยอดเครดิตรวมของแถวที่ <see cref="CreditableInPeriod"/> คืน — ผ่านตัวคำนวณเดียว <see cref="WhtCreditableAmount.Of"/>
    /// (ใบรวม/ผ่อนงวด ⇒ เฉพาะส่วนที่ได้ใบ) · ตัวรวมเดียวของ ภ.ง.ด.50 (<c>TaxService</c>) + เครื่องคำนวณหน้าบริหารการเงิน</summary>
    public static async Task<decimal> CreditableSumAsync(this IQueryable<WhtCreditReceived> creditable, CancellationToken ct = default)
    {
        var rows = await creditable.Select(w => new { w.Status, w.WhtAmount, w.ReceivedAmount })
            .ToListAsync(ct);
        return Math.Round(rows.Sum(r => WhtCreditableAmount.Of(r.Status, r.WhtAmount, r.ReceivedAmount)), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>เพดานค่ารับรองที่หักได้ทั้งรอบ — MAX(0.3% รายได้, 0.3% ทุนชำระแล้ว) ไม่เกิน 10 ล้าน (กฎกระทรวง 143)</summary>
    public const decimal EntertainmentCapRate = 0.003m;
    public const decimal EntertainmentCapCeiling = 10_000_000m;

    public static decimal EntertainmentCap(decimal annualRevenue, decimal paidUpCapital)
        => Math.Min(EntertainmentCapCeiling, Math.Max(Math.Max(0m, annualRevenue) * EntertainmentCapRate, Math.Max(0m, paidUpCapital) * EntertainmentCapRate));

    /// <summary>ส่วนเกินเพดานค่ารับรอง (บวกกลับ) — ครึ่งปีใช้เพดานครึ่งหนึ่งของเพดานจากรายได้ทั้งปีประมาณการ (รายได้ครึ่งปี × 2)</summary>
    /// <param name="periodRevenue">รายได้ของงวด (ครึ่งปี = 6 เดือน)</param>
    public static decimal EntertainmentExcess(decimal entertainmentExpense, decimal periodRevenue, decimal paidUpCapital, bool halfYear)
    {
        var cap = halfYear ? EntertainmentCap(periodRevenue * 2m, paidUpCapital) / 2m : EntertainmentCap(periodRevenue, paidUpCapital);
        return R(Math.Max(0m, entertainmentExpense - cap));
    }

    /// <summary>
    /// กำไรสุทธิทางภาษี = กำไรทางบัญชี (ไม่หักค่าใช้จ่ายภาษีเงินได้ 580xx) + บวกกลับ §65 ตรี อัตโนมัติ + บวกกลับที่ผู้ใช้กรอก
    /// − รายได้ที่ได้รับยกเว้น − รายการหักเพิ่ม · ค่าติดลบของช่องที่ผู้ใช้กรอกถูกปฏิเสธที่ผู้เรียก (<see cref="RejectNegative"/>)
    /// </summary>
    public static decimal TaxableProfit(decimal accountingProfitExCit, decimal autoAddBack, decimal manualAddBack, decimal exemptIncome, decimal otherDeductions)
        => R(accountingProfitExCit + autoAddBack + manualAddBack - exemptIncome - otherDeductions);

    /// <summary>ช่องตัวเลขที่ผู้ใช้กรอกต้องไม่ติดลบ — คืนข้อความปฏิเสธ หรือ null</summary>
    public static string? RejectNegative(params (string Label, decimal? Value)[] fields)
    {
        var bad = fields.Where(f => f.Value is < 0m).Select(f => f.Label).ToList();
        return bad.Count == 0 ? null : $"ช่อง {string.Join(", ", bad)} ต้องไม่ติดลบ";
    }

    // ══════════════ (3) ภ.ง.ด.51 ══════════════

    /// <summary>ผลภาษีครึ่งปี — <paramref name="EstimatedAnnualProfit"/> = ฐานทั้งปีที่ใช้หาอัตรา · <paramref name="AnnualTaxOnBasis"/> = ภาษีของฐานนั้นทั้งปี ·
    /// <paramref name="HalfYearTax"/> = ภาษีที่ต้องชำระครึ่งปี (ก่อนหักภาษีถูกหัก ณ ที่จ่าย)</summary>
    public sealed record HalfYearResult(string Basis, decimal EstimatedAnnualProfit, decimal AnnualTaxOnBasis, decimal HalfYearTax, string Note);

    /// <summary>
    /// §67 ทวิ — ภ.ง.ด.51:
    /// <list type="bullet">
    /// <item>(1) <see cref="BasisEstimate"/> (ค่าตั้งต้น): ประมาณการกำไรสุทธิทั้งรอบ (ผู้ใช้กรอก · ว่าง = กำไรสุทธิครึ่งปีจริง × 2) → ภาษีตามตาราง ÷ 2</item>
    /// <item>(2) <see cref="BasisActualHalfYear"/>: กำไรสุทธิจริงครึ่งปี — ภาษี = ภาษีของ (กำไรครึ่งปี × 2) ÷ 2
    /// (= ขอบขั้นบันไดครึ่งหนึ่ง · ทั่วไป 20% ตรง ๆ — ต้องยืนยันกับนักบัญชีสำหรับ SME)</item>
    /// </list>
    /// ประมาณการติดลบ/ศูนย์ ⇒ ภาษี 0 · ปัด AwayFromZero
    /// </summary>
    public static HalfYearResult HalfYearTax(decimal halfYearTaxableProfit, decimal? estimatedAnnualProfit, string? basis, bool isSme, int fiscalYear)
    {
        var b = basis == BasisActualHalfYear ? BasisActualHalfYear : BasisEstimate;
        if (b == BasisActualHalfYear && estimatedAnnualProfit is not null)
            throw new BusinessRuleException(
                "ยื่น ภ.ง.ด.51 จากกำไรสุทธิจริงครึ่งปี (§67 ทวิ(2)) ไม่ใช้ช่องประมาณการกำไรทั้งปี — ล้างช่องประมาณการ หรือเลือกวิธีประมาณการ", "CIT-PND51-BASIS");
        var annualBase = b == BasisActualHalfYear ? halfYearTaxableProfit * 2m : estimatedAnnualProfit ?? halfYearTaxableProfit * 2m;
        annualBase = R(annualBase);
        var annualTax = CitRateTable.Compute(annualBase, isSme, fiscalYear);
        var half = R(annualTax / 2m);
        var note = b == BasisActualHalfYear
            ? $"ภ.ง.ด.51 จากกำไรสุทธิจริงครึ่งปี {halfYearTaxableProfit:N2} (§67 ทวิ(2)) — ภาษี = ภาษีของ {annualBase:N2} ÷ 2 = {half:N2}"
            : $"ภ.ง.ด.51 ประมาณการกำไรสุทธิทั้งรอบ {annualBase:N2}"
              + (estimatedAnnualProfit is null ? $" (ค่าตั้งต้น = กำไรครึ่งปี {halfYearTaxableProfit:N2} × 2 — แก้ได้)" : " (ผู้ใช้กรอก)")
              + $" × อัตรา = {annualTax:N2} ÷ 2 = {half:N2} (§67 ทวิ(1)) · ประมาณการขาดเกิน 25% ของกำไรจริง ⇒ เงินเพิ่ม 20% (§67 ตรี)";
        return new HalfYearResult(b, annualBase, annualTax, half, note);
    }

    /// <summary>
    /// รอบบัญชีแรก/รอบที่ไม่ถึง 12 เดือน ไม่ต้องยื่น ภ.ง.ด.51 (§67 ทวิ) — ระบบ<b>ไม่ตัดสินเอง</b> (ไม่มีวันจดทะเบียนในระบบ ·
    /// <c>Company.CreatedAt</c> = วันเริ่มใช้ระบบ ไม่ใช่วันจดทะเบียน) ⇒ ยกเว้นเมื่อผู้ใช้ประกาศ <paramref name="declaredShortYear"/> = true ·
    /// ไม่ประกาศแต่บริษัทเริ่มใช้ระบบในรอบนี้ ⇒ คำเตือน (ไม่ยกเว้น — ทิศที่เสียหายมองไม่เห็นคือยกเว้นผิดแล้วไม่ได้ยื่น)
    /// </summary>
    public static (bool Exempt, string? Note) Pnd51Exemption(bool? declaredShortYear, DateTime? companyCreatedAt, FiscalYearRange fullYear)
    {
        if (declaredShortYear == true)
            return (true, "รอบบัญชีนี้ไม่ถึง 12 เดือน (ผู้ใช้ระบุ) — ไม่ต้องยื่น ภ.ง.ด.51 (§67 ทวิ)");
        if (declaredShortYear is null && companyCreatedAt is DateTime c && c > fullYear.Start && c < fullYear.EndExclusive)
            return (false, "⚠️ บริษัทเริ่มใช้ระบบในรอบบัญชีนี้ — ถ้าเป็นรอบบัญชีแรกที่ไม่ถึง 12 เดือน ไม่ต้องยื่น ภ.ง.ด.51 (ติ๊ก \"รอบบัญชีไม่ถึง 12 เดือน\" แล้วคำนวณใหม่)");
        return (false, null);
    }

    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
