using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ยอดหนึ่งแถวบน 50 ทวิ (หรือยอดที่ใบลดหนี้ลด) — จับคู่ด้วยประเภทเงินได้ + อัตรา</summary>
public sealed record WhtCertAmountLine(string IncomeTypeCode, decimal TaxRate, decimal IncomeAmount, decimal TaxAmount,
    string Description, DateTime PaymentDate, string? Condition = null);

public enum WhtCertReissueAction
{
    /// <summary>ใบลดหนี้ไม่ลดภาษีหักของผู้ถูกหักนี้ — ไม่ต้องทำอะไร</summary>
    NoChange,
    /// <summary>ยกเลิกใบเดิมแล้วออกใบใหม่ด้วยยอดที่ลดแล้ว (เดือนภาษีเดิม)</summary>
    Reissue,
    /// <summary>ใบลดหนี้ลดภาษีหักจนเป็นศูนย์ — ยกเลิกใบเดิมอย่างเดียว</summary>
    VoidOnly,
    /// <summary>ระบบตัดสินแทนไม่ได้ (ยื่นแล้ว · ใบรายงวด · จับคู่แถวไม่ได้ · ลดเกิน) — ติดธงให้นักบัญชี (ล้มดัง ไม่แก้เงียบ)</summary>
    ManualReview,
}

public sealed record WhtCertReissueDecision(WhtCertReissueAction Action, IReadOnlyList<WhtCertAmountLine> NewLines, string Reason);

/// <summary>
/// **ตัวตัดสินเดียว "ใบลดหนี้ซื้อลดฐานหัก ณ ที่จ่าย หลังออก 50 ทวิ แล้ว — ทำอะไรกับใบเดิม"** (ข้อเสนอ 142 · เจ้าของอนุมัติข้อ 148 · รอบ 204)
///
/// <para>ยอดบน 50 ทวิ ต้องเท่ายอดที่หักจริง (ท.ป.4/2528) — ใบลดหนี้ลด JE 2191x ไปแล้วตอนอนุมัติ (Dr หนี้ WHT) ⇒ ใบรับรองต้องตาม ·
/// ระบบ<b>ไม่แก้ใบเดิม</b> (ออกให้ผู้ถูกหักแล้ว · เลขเดิมห้ามเปลี่ยนยอด) — ยกเลิกแล้วออกใบใหม่ในเดือนภาษีเดิม เมื่อเดือนนั้นยังไม่ยื่น/นำส่ง ·
/// ยื่นแล้ว = ต้องยื่นแบบเพิ่มเติม — ให้คนตัดสินเดือน/วิธี (ติดธงบนเอกสาร + ลิสต์รอออกใบ) · ห้ามแต่งการจับคู่ที่ไม่แน่ใจ (F2 ข้อ 3)</para>
/// </summary>
public static class WhtCertReissuePlan
{
    public const string RuleCode = "RD-50BIS-CN-REISSUE";

    /// <summary>ฝ่ายค้าน P3-1 (รอบ 204): สถานะของใบที่ออกแทนใบเดิม — ใบเดิมเป็นร่าง ⇒ ร่าง (ไม่เข้าแบบยื่นจนกว่าผู้ใช้ออกใบ) · อื่น ๆ ⇒ ออกแล้ว ·
    /// ต้องส่งสถานะ <b>ก่อน</b>ประทับยกเลิก (เดิมเทียบหลังประทับ Voided ⇒ ได้ "ออกแล้ว" เสมอ)</summary>
    public static Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus ReplacementStatus(Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus oldStatusBeforeVoid)
        => oldStatusBeforeVoid == Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus.Draft
            ? Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus.Draft
            : Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus.Issued;

    /// <summary>ฝ่ายค้านรอบ 204 (หลัง 0f428969): ใบที่ออกแทน "กลุ่ม" ใบเดิม (งวดจ่ายเดียวกัน — เปลี่ยนผู้ถูกหักหลังอนุมัติ) ออกเป็น "ออกแล้ว" ไหม ·
    /// ตัดสินด้วย <see cref="ReplacementStatus"/> ตัวเดียว: มีใบเดิมใดไม่ใช่ร่าง ⇒ ออกแล้ว · ทุกใบเป็นร่าง ⇒ ร่าง (เดิมออกแล้วเสมอ ⇒ ใบร่างที่ผู้ใช้ยังไม่ออก
    /// กลายเป็นใบที่เข้าแบบยื่น) · ต้องส่งสถานะ<b>ก่อน</b>ประทับยกเลิก</summary>
    public static bool ReplacementIssuesAsIssued(IEnumerable<Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus> oldStatusesBeforeVoid)
        => oldStatusesBeforeVoid.Any(st => ReplacementStatus(st) != Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus.Draft);

    /// <summary>ฝ่ายค้าน P2b (รอบ 204): ยกเลิกใบลดหนี้ซื้อที่ลดภาษีหัก ณ ที่จ่ายไปแล้ว (ตอนอนุมัติ 50 ทวิ ของใบต้นทางถูกยกเลิก+ออกใหม่ยอดที่ลด
    /// หรือติดป้าย) — ยกเลิกใบลดหนี้แล้วภาษีหักกลับเป็นยอดเต็ม แต่ 50 ทวิ ยังเป็นยอดที่ลด ⇒ ข้อความป้าย "50 ทวิ ต้องตรวจ" บนใบต้นทาง ·
    /// ไม่เข้าเงื่อนไข (ไม่ใช่ใบลดหนี้ · ไม่มีภาษีหัก · ฝั่งขาย · ยังไม่อนุมัติ · ใบต้นทางไม่มี 50 ทวิ) ⇒ null</summary>
    public static string? CreditNoteVoidedReason(DocumentType type, DocumentStatus statusBeforeVoid, decimal withholdingTaxAmount,
        bool? purchaseSideOverride, bool sourceIsPayerSide, int sourceActiveCerts, string? creditNoteNumber)
    {
        if (type != DocumentType.CreditNote || withholdingTaxAmount <= 0m || purchaseSideOverride == false || !sourceIsPayerSide) return null;
        if (statusBeforeVoid is DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Voided or DocumentStatus.Rejected) return null;
        if (sourceActiveCerts <= 0) return null;
        return $"ใบลดหนี้ {creditNoteNumber} ที่ลดภาษีหัก ณ ที่จ่าย {withholdingTaxAmount:N2} บาท ถูกยกเลิก — ภาษีหักของใบนี้กลับเป็นยอดเต็ม "
               + "แต่ 50 ทวิ อาจเป็นยอดที่ลดตามใบลดหนี้ (ต่ำกว่าที่หักจริง) · ตรวจยอดบน 50 ทวิ แล้วยกเลิก/ออกใบใหม่เอง (หรือยื่นแบบเพิ่มเติมถ้าเดือนนั้นยื่นแล้ว)";
    }

    /// <param name="certLines">แถวบน 50 ทวิ ที่ยังมีผลของผู้ถูกหักนี้ (ใบเดียว)</param>
    /// <param name="reductions">ยอดที่ใบลดหนี้ลด (เงินได้ + ภาษีหัก) ของผู้ถูกหักนี้ — บวก = ลด</param>
    /// <param name="filedReason">เหตุผลจากด่านยื่นแล้ว (<see cref="WhtCertVoidGuard"/>) · null = ยกเลิกได้</param>
    /// <param name="certIsInstallment">ใบผูกงวดจ่าย (SourcePaymentId) — ลดงวดไหนต้องให้คนตัดสิน</param>
    /// <param name="activeCertCountForPayee">จำนวนใบที่ยังมีผลของผู้ถูกหักนี้บนเอกสาร</param>
    public static WhtCertReissueDecision ForCreditNote(IReadOnlyList<WhtCertAmountLine> certLines,
        IReadOnlyList<WhtCertAmountLine> reductions, string? filedReason, bool certIsInstallment, int activeCertCountForPayee,
        string certNumber)
    {
        WhtCertReissueDecision D(WhtCertReissueAction a, string why, IReadOnlyList<WhtCertAmountLine>? lines = null)
            => new(a, lines ?? Array.Empty<WhtCertAmountLine>(), why);

        var cutTax = reductions.Sum(r => r.TaxAmount);
        var cutIncome = reductions.Sum(r => r.IncomeAmount);
        if (cutTax <= 0m && cutIncome <= 0m)
            return D(WhtCertReissueAction.NoChange, "ใบลดหนี้ไม่ลดภาษีหัก ณ ที่จ่ายของผู้ถูกหักนี้");

        if (filedReason != null)
            return D(WhtCertReissueAction.ManualReview,
                $"ใบลดหนี้ลดภาษีหัก ณ ที่จ่าย {cutTax:N2} บาท แต่ {filedReason}");
        if (activeCertCountForPayee != 1 || certIsInstallment)
            return D(WhtCertReissueAction.ManualReview,
                $"ใบลดหนี้ลดภาษีหัก ณ ที่จ่าย {cutTax:N2} บาท — 50 ทวิ ของผู้ถูกหักนี้ออกเป็นรายงวด/หลายใบ ระบบเลือกแทนไม่ได้ว่าลดใบไหน · "
                + "ยกเลิกใบที่เกี่ยวข้องแล้วออกใหม่ด้วยยอดที่ถูกที่หน้า \"หนังสือรับรองหัก ณ ที่จ่าย\"");

        // จับคู่ด้วย (ประเภทเงินได้, อัตรา) — ลดแถวตามลำดับบนใบ
        var work = certLines.Select(l => l with { }).ToList();
        foreach (var r in reductions.Where(r => r.TaxAmount > 0m || r.IncomeAmount > 0m))
        {
            decimal remainTax = r.TaxAmount, remainIncome = r.IncomeAmount;
            var matched = false;
            for (var i = 0; i < work.Count && (remainTax > 0m || remainIncome > 0m); i++)
            {
                var w = work[i];
                if (!string.Equals(w.IncomeTypeCode, r.IncomeTypeCode, StringComparison.Ordinal) || w.TaxRate != r.TaxRate) continue;
                matched = true;
                var takeTax = Math.Min(w.TaxAmount, remainTax);
                var takeIncome = Math.Min(w.IncomeAmount, remainIncome);
                work[i] = w with { TaxAmount = w.TaxAmount - takeTax, IncomeAmount = w.IncomeAmount - takeIncome };
                remainTax -= takeTax;
                remainIncome -= takeIncome;
            }
            if (!matched)
                return D(WhtCertReissueAction.ManualReview,
                    $"ใบลดหนี้มีรายการประเภทเงินได้ {r.IncomeTypeCode} อัตรา {r.TaxRate:0.##}% ที่ไม่มีบน 50 ทวิ {certNumber} — ระบบจับคู่แทนไม่ได้ "
                    + "ตรวจยอดแล้วยกเลิก/ออกใบใหม่เองที่หน้า \"หนังสือรับรองหัก ณ ที่จ่าย\"");
            if (remainTax > 0.005m || remainIncome > 0.005m)
                return D(WhtCertReissueAction.ManualReview,
                    $"ใบลดหนี้ลดยอดเกินกว่าที่อยู่บน 50 ทวิ {certNumber} (ประเภท {r.IncomeTypeCode} {r.TaxRate:0.##}%) — ตรวจยอดแล้วแก้ใบเอง");
        }

        var kept = work.Where(w => w.TaxAmount > 0m || w.IncomeAmount > 0m).ToList();
        if (kept.Sum(w => w.TaxAmount) <= 0m)
            return D(WhtCertReissueAction.VoidOnly,
                $"ใบลดหนี้ลดภาษีหัก ณ ที่จ่ายของใบ {certNumber} จนเป็นศูนย์ — ยกเลิกใบเดิม");
        return D(WhtCertReissueAction.Reissue,
            $"ใบลดหนี้ลดภาษีหัก ณ ที่จ่าย {cutTax:N2} บาท — ยกเลิก 50 ทวิ {certNumber} แล้วออกใบใหม่ด้วยยอดที่ลดแล้ว (เดือนภาษีเดิม)", kept);
    }
}

/// <summary>
/// **ตัวตัดสินเดียว "เปลี่ยนผู้ถูกหักภาษีหลังอนุมัติ"** (ข้อเสนอ 145 · เจ้าของอนุมัติข้อ 148 · รอบ 204)
///
/// <para>ห้ามเปลี่ยนคอลัมน์เฉย ๆ หลังลงบัญชี (GL กับทะเบียนแยกทาง) — ต้องเป็น "การกระทำ" ที่มีเหตุผล: ยกเลิก 50 ทวิ เดิม (ไม่ใช่แก้) ·
/// ย้ายหนี้ WHT ระหว่างผัง 21915/21916/21917/21918 (<c>WhtPayableAccount.DocumentPayableCodes</c>) ด้วย JE ปรับปรุงเมื่อแบบเปลี่ยน · ออกใบใหม่ในนามผู้ถูกหักที่ถูก · audit ทุกขั้น ·
/// เดือนภาษีที่ยื่น/นำส่งแบบเดิมหรือแบบใหม่แล้ว ⇒ ปฏิเสธ (ต้องยื่นเพิ่มเติม — คนตัดสิน)</para>
/// </summary>
public static class WhtPayeeChangePlan
{
    public const string RuleCode = "RD-50BIS-PAYEE-CHANGE";

    /// <summary>เหตุผลที่ห้ามเปลี่ยน (null = ทำได้)</summary>
    public static string? BlockReason(bool payerSide, DocumentStatus status, decimal withholdingTaxAmount,
        string? reason, bool anythingChanges, string? filedReason)
    {
        if (!payerSide) return "เอกสารนี้ไม่ใช่เอกสารที่บริษัทเป็นผู้หักภาษี ณ ที่จ่าย";
        if (withholdingTaxAmount <= 0m) return "เอกสารนี้ไม่มีภาษีหัก ณ ที่จ่าย";
        if (status is DocumentStatus.Voided or DocumentStatus.Rejected)
            return "เอกสารถูกยกเลิก/ตีกลับแล้ว";
        if (status is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
            return "เอกสารยังไม่อนุมัติ — แก้ผู้ถูกหักที่ฟอร์มแก้ไขเอกสารได้เลย";
        if (string.IsNullOrWhiteSpace(reason)) return "ระบุเหตุผลการเปลี่ยนผู้ถูกหัก (บันทึกลงประวัติการแก้ไข)";
        if (!anythingChanges) return "ผู้ถูกหักที่เลือกตรงกับที่บันทึกไว้แล้ว — ไม่มีอะไรเปลี่ยน";
        return filedReason;
    }

    /// <summary>แบบ ภ.ง.ด. ของเดือนภาษีนั้นถูกประกาศว่ายื่น/ยื่นแล้ว หรือมีบันทึกนำส่งแล้วไหม — ข้อความเหตุผล (null = ยังไม่ยื่น/นำส่ง) ·
    /// หลักฐานชุดเดียวกับ <see cref="WhtCertVoidGuard"/> (รายงานภาษีสถานะ <see cref="TaxFilingLockPolicy.DeclaredOrFiledStatuses"/> ·
    /// <c>StatutoryRemittance</c> แปลงชนิดด้วย <see cref="WhtCertVoidGuard.RemittanceForm"/>) แต่ระดับ "งวด" (ใช้เมื่อยังไม่มีใบให้ตรวจรายใบ)
    /// <para>ฝ่ายค้าน P3-4 (รอบ 204): ตรวจ<b>ทุกงวด</b>ใน <paramref name="periods"/> (<see cref="AffectedPeriods"/> — เดือนภาษีของเอกสาร + ทุกเดือนที่มีการจ่าย/50 ทวิ) ·
    /// เดิมดูแค่ PaymentDate ?? DocumentDate ⇒ ใบจ่ายสองงวดข้ามเดือน งวดแรกยื่นแล้วแต่ระบบยังยกเลิก/ออกใบใหม่ให้</para></summary>
    public static async Task<string?> PeriodFiledReasonAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyCollection<TaxType> forms, IReadOnlyCollection<(int Year, int Month)> periods, CancellationToken ct = default)
    {
        foreach (var (year, month) in periods)
            if (await PeriodFiledReasonAsync(db, companyId, forms, year, month, ct) is string reason)
                return reason;
        return null;
    }

    /// <summary>งวด (ปี, เดือน) ที่การเปลี่ยนผู้ถูกหักแตะ — เดือนภาษีของเอกสาร + ทุกเดือนที่มีการจ่าย + ทุกเดือนภาษีของ 50 ทวิ ที่ยังมีผล · ไม่ซ้ำ · เรียงตามเวลา</summary>
    public static IReadOnlyList<(int Year, int Month)> AffectedPeriods(DateTime taxDate, IEnumerable<DateTime> paymentDates,
        IEnumerable<(int Year, int Month)> certPeriods)
        => new[] { (taxDate.Year, taxDate.Month) }
            .Concat(paymentDates.Select(d => (d.Year, d.Month)))
            .Concat(certPeriods)
            .Distinct().OrderBy(p => p.Item1).ThenBy(p => p.Item2).ToList();

    /// <summary>ฝ่ายค้าน P2a (รอบ 204): ใบลด/เพิ่มหนี้ที่อนุมัติแล้วและหักภาษีอ้างเอกสารนี้ ⇒ ห้ามเปลี่ยนผู้ถูกหัก (409) พร้อมทางไปต่อ ·
    /// เหตุ: ยอดหนี้ WHT ที่ย้ายผังนับเฉพาะ JE ของเอกสารนี้ (ไม่รวมตัวกลับของใบลดหนี้) และ 50 ทวิ ที่ออกใหม่คิดจากบรรทัดของเอกสารนี้ (ไม่หักใบลดหนี้)
    /// ⇒ ผังผู้ถูกหักเดิมค้างยอดติดลบ + 50 ทวิ เกินยอดที่หักจริง · ไม่มีใบลด/เพิ่มหนี้ ⇒ null (เส้นเดิม)</summary>
    public static string? AdjustmentNotesReason(string? documentNumber, IReadOnlyCollection<string> approvedNoteNumbers)
        => approvedNoteNumbers.Count == 0 ? null
            : $"เอกสาร {documentNumber} มีใบลด/เพิ่มหนี้ที่อนุมัติแล้วและหักภาษี ณ ที่จ่ายอ้างอยู่ ({string.Join(", ", approvedNoteNumbers)}) — "
              + "เปลี่ยนผู้ถูกหักตอนนี้ ระบบย้ายหนี้ภาษีและออก 50 ทวิ ใหม่ได้ไม่ตรงยอดหลังลด/เพิ่มหนี้ · ทางไปต่อ: ยกเลิกใบลด/เพิ่มหนี้ก่อน "
              + "แล้วเปลี่ยนผู้ถูกหัก จากนั้นออกใบลด/เพิ่มหนี้ใหม่ — หรือให้นักบัญชีปรับรายการบัญชี/50 ทวิ เอง";

    private static async Task<string?> PeriodFiledReasonAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyCollection<TaxType> forms, int year, int month, CancellationToken ct)
    {
        if (forms.Count == 0) return null;
        var formList = forms.Distinct().ToList();
        var reported = await db.TaxReports.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.Year == year && t.Month == month
                && formList.Contains(t.TaxType) && TaxFilingLockPolicy.DeclaredOrFiledStatuses.Contains(t.Status))
            .Select(t => (TaxType?)t.TaxType).FirstOrDefaultAsync(ct);
        if (reported is TaxType rf)
            return $"{WhtUnissuedCertGate.FormLabel(rf)} เดือน {month:00}/{year + 543} ยื่นแล้ว — ต้องยื่นแบบเพิ่มเติม (ให้นักบัญชีตัดสิน) ระบบไม่เปลี่ยนให้";
        var remitTypes = await db.StatutoryRemittances.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.PeriodYear == year && r.PeriodMonth == month)
            .Select(r => r.RemittanceType).ToListAsync(ct);
        foreach (var rt in remitTypes)
            if (WhtCertVoidGuard.RemittanceForm(rt) is TaxType f && formList.Contains(f))
                return $"{WhtUnissuedCertGate.FormLabel(f)} เดือน {month:00}/{year + 543} บันทึกนำส่งแล้ว — ต้องยื่นแบบเพิ่มเติม (ให้นักบัญชีตัดสิน) ระบบไม่เปลี่ยนให้ "
                    + "(ถ้าบันทึกนำส่งผิด ให้ผู้มีสิทธิ์ยกเลิกรายการนำส่งก่อน)";
        return null;
    }

    /// <summary>ผลต่างยอดหนี้ WHT ต่อผัง (ปัจจุบัน → ปลายทาง) — บวก = ต้องเพิ่มเครดิต (Cr) · ลบ = ต้องลด (Dr) · ผังที่ไม่ขยับไม่อยู่ในผล ·
    /// Σ ผลต่าง = 0 เสมอเมื่อยอดรวมสองฝั่งเท่ากัน (ย้ายผัง ไม่เปลี่ยนยอด)</summary>
    public static IReadOnlyList<(string AccountCode, decimal Delta)> PayableDeltas(
        IReadOnlyDictionary<string, decimal> current, IReadOnlyDictionary<string, decimal> target)
        => current.Keys.Union(target.Keys).OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => (k, (target.TryGetValue(k, out var t) ? t : 0m) - (current.TryGetValue(k, out var c) ? c : 0m)))
            .Where(x => Math.Abs(x.Item2) >= 0.005m)
            .ToList();
}

/// <summary>
/// **ป้าย "50 ทวิ ต้องตรวจ/ออกใหม่" บนหมายเหตุภายในของเอกสาร — เจ้าของรูปแบบตัวเดียว** (รอบ 204 · ข้อ 142/145)
///
/// <para>ใช้เมื่อระบบแก้ 50 ทวิ ที่ออกแล้วเองไม่ได้: ใบลดหนี้ลดฐานหลังยื่น · ยกเลิกการจ่ายแต่ใบออกให้ผู้ถูกหักแล้ว (เดิมแค่ LogWarning) ·
/// ล้มดัง 3 ที่ = ป้ายบนเอกสาร (หน้ารายละเอียด) + ลิสต์ "เอกสารรอออกใบ" + audit · ผู้ใช้กด "รับทราบ" หลังจัดการแล้ว (audit) ⇒ ล้างป้าย ·
/// ต่างจาก <see cref="WhtCertAutoFailedNote"/> (ออกใบไม่สำเร็จ — ล้างเองเมื่อออกได้) · ป้ายหลายเรื่องสะสมได้ (ไม่ทับกัน)</para>
/// </summary>
public static class WhtCertReviewNote
{
    public const string Marker = "[WHT-CERT-REVIEW]";
    private const string Lead = " 50 ทวิ ต้องตรวจ: ";

    public static string Build(string reason) => $"{Marker}{Lead}{reason}";

    /// <summary>ต่อท้ายหมายเหตุเดิม — เรื่องเดียวกันซ้ำ (ข้อความเท่ากัน) ไม่เพิ่ม</summary>
    public static string Append(string? existing, string reason)
    {
        var note = Build(reason.Trim());
        if (!string.IsNullOrEmpty(existing) && existing.Replace("\r\n", "\n").Split("\n\n").Any(p => p.Trim() == note))
            return existing;
        return string.IsNullOrWhiteSpace(existing) ? note : existing.TrimEnd() + "\n\n" + note;
    }

    public static bool Has(string? notes)
        => !string.IsNullOrEmpty(notes) && notes.Contains(Marker, StringComparison.Ordinal);

    /// <summary>ทุกเรื่องที่ค้าง (ว่าง = ไม่มีป้าย)</summary>
    public static IReadOnlyList<string> Reasons(string? notes)
    {
        if (!Has(notes)) return Array.Empty<string>();
        return notes!.Replace("\r\n", "\n").Split("\n\n")
            .Select(p => p.Trim())
            .Where(p => p.StartsWith(Marker, StringComparison.Ordinal))
            .Select(p => p.Substring(Marker.Length))
            .Select(t => (t.StartsWith(Lead, StringComparison.Ordinal) ? t.Substring(Lead.Length) : t).Trim())
            .ToList();
    }

    /// <summary>เรื่องที่ค้างรวมเป็นข้อความเดียว (null = ไม่มีป้าย) — ช่องของคำตอบให้หน้าเว็บแสดง</summary>
    public static string? ReasonOf(string? notes)
    {
        var r = Reasons(notes);
        return r.Count == 0 ? null : string.Join(" · ", r);
    }

    /// <summary>ถอดทุกย่อหน้าของป้าย · ไม่มีป้าย = คืนค่าเดิมทุกตัวอักษร · เหลือแต่ช่องว่าง = null</summary>
    public static string? Clear(string? notes)
    {
        if (!Has(notes)) return notes;
        var paras = notes!.Replace("\r\n", "\n").Split("\n\n")
            .Where(p => !p.TrimStart().StartsWith(Marker, StringComparison.Ordinal));
        var joined = string.Join("\n\n", paras).Trim();
        return joined.Length == 0 ? null : joined;
    }
}
