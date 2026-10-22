using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตั้งตัวเดียวของ "คอร์ส / เครดิตเข้าเรียน / ชั่วโมง" (ทีม L · <c>docs/COURSE_CREDIT_DESIGN.md</c>) — pure ไม่แตะฐานข้อมูล
///
/// <para><b>การรับรู้รายได้ (TFRS for NPAEs บทที่ 6 — บริการ):</b> ขายคอร์ส = รับเงินล่วงหน้า ⇒ ยอดสุทธิก่อน VAT อยู่ที่
/// "ค่าบริการรับล่วงหน้า" (หนี้สิน) · ใช้บริการแต่ละครั้ง ⇒ ย้ายเข้ารายได้ตาม<b>ราคาต่อเครดิต</b> (ปัด AwayFromZero 2 ตำแหน่ง) ·
/// เครดิตสุดท้ายรับ<b>ส่วนที่เหลือทั้งหมด</b> (ไม่มีเศษสตางค์ค้างในหนี้สิน) · หมดอายุ ⇒ ส่วนคงเหลือทั้งหมดรับรู้เป็นรายได้ (breakage)</para>
///
/// <para><b>VAT:</b> บริการ — จุดความรับผิด §78/1 = วันรับเงิน ⇒ ภาษีขายเกิดครบตอนขาย (ใบกำกับออก ณ วันขายผ่านเส้นเอกสารเดิม) ·
/// การใช้เครดิต/หมดอายุ <b>ไม่มี VAT เพิ่ม</b> (ย้ายระหว่างหนี้สินกับรายได้ล้วน)</para>
/// </summary>
public static class CourseCreditMath
{
    public const string RuleCode = "TFRS-NPAE-6-CREDIT";
    public const string LegalReference = "TFRS for NPAEs บทที่ 6 (รายได้จากการให้บริการ) · ป.รัษฎากร §78/1";

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>ราคาต่อเครดิต (สุทธิก่อน VAT) — ปัด AwayFromZero · เครดิตรวม ≤ 0 ⇒ 0</summary>
    public static decimal PricePerCredit(decimal contractAmount, decimal totalCredits)
        => totalCredits <= 0m ? 0m : R2(contractAmount / totalCredits);

    /// <summary>
    /// รายได้ที่รับรู้เมื่อใช้ <paramref name="creditsNow"/> เครดิต (เข้าเรียน · no-show · ยกเลิกช้าที่ริบเครดิต)
    /// <list type="bullet">
    /// <item>ใช้แล้วรวมครั้งนี้ ≥ เครดิตทั้งหมด ⇒ คืน<b>ส่วนที่เหลือทั้งหมด</b> (สัญญา − รับรู้แล้ว)</item>
    /// <item>อื่น ๆ ⇒ round(ราคาต่อเครดิต × เครดิตครั้งนี้) แต่ไม่เกินส่วนที่เหลือ (ราคาต่อเครดิตปัดขึ้นสะสมจนเกินสัญญาไม่ได้)</item>
    /// </list>
    /// ไม่ติดลบเสมอ · เครดิตครั้งนี้ ≤ 0 ⇒ 0
    /// </summary>
    public static decimal RecognizeOnUse(decimal contractAmount, decimal totalCredits,
        decimal creditsUsedBefore, decimal recognizedBefore, decimal creditsNow)
    {
        if (creditsNow <= 0m) return 0m;
        var remaining = contractAmount - recognizedBefore;
        if (remaining <= 0m) return 0m;
        if (creditsUsedBefore + creditsNow >= totalCredits) return remaining;
        var amount = R2(PricePerCredit(contractAmount, totalCredits) * creditsNow);
        return Math.Min(amount, remaining);
    }

    /// <summary>breakage ตอนหมดอายุ — ส่วนคงเหลือทั้งหมดในหนี้สิน (ไม่ติดลบ)</summary>
    public static decimal BreakageAmount(decimal contractAmount, decimal recognizedBefore)
        => Math.Max(0m, contractAmount - recognizedBefore);

    /// <summary>เครดิตคงเหลือที่จองได้ = ทั้งหมด − ใช้แล้ว − ถูกจองไว้ (ไม่ติดลบ)</summary>
    public static decimal Available(decimal total, decimal used, decimal reserved)
        => Math.Max(0m, total - used - reserved);

    /// <summary>เครดิตที่ต้องใช้ต่อหนึ่งรอบเรียน — หน่วย "ครั้ง" = ค่าที่รอบตั้ง (ตั้งต้น 1) · หน่วย "ชั่วโมง" = ระยะเวลารอบ ÷ 60 (2 ตำแหน่ง)</summary>
    public static decimal CreditsForSession(CreditUnit unit, int durationMinutes, decimal sessionCreditCost)
        => unit == CreditUnit.Hour
            ? R2(Math.Max(0, durationMinutes) / 60m)
            : (sessionCreditCost > 0m ? sessionCreditCost : 1m);

    /// <summary>วันสุดท้ายที่ใช้ได้ (วันตามปฏิทินไทย เก็บแบบ <see cref="ThaiDate.CalendarDateUtc(DateTime)"/>) =
    /// วันซื้อ + อายุวัน · อายุ ≤ 0 = ไม่หมดอายุ (null)</summary>
    public static DateTime? ExpiresOn(DateTime purchasedAtUtc, int validityDays)
        => validityDays <= 0 ? null : ThaiDate.CalendarDateUtc(purchasedAtUtc).AddDays(validityDays);

    /// <summary>หมดอายุแล้วหรือยัง — วันนี้ (ปฏิทินไทย) เลยวันสุดท้ายที่ใช้ได้ไปแล้ว</summary>
    public static bool IsExpired(DateTime? expiresOn, DateTime nowUtc)
        => expiresOn.HasValue && ThaiDate.CalendarDateUtc(nowUtc) > expiresOn.Value.Date;

    /// <summary>ผลของการยกเลิกการจองโดยลูกค้า — ยกเลิกก่อนเวลาเรียน ≥ <paramref name="freeCancelHours"/> ชม. ⇒ คืนเครดิต ·
    /// น้อยกว่านั้น ⇒ ริบเครดิต (รับรู้รายได้เหมือนเข้าเรียน) · ร้านยกเลิกรอบเอง ⇒ คืนเสมอ (ผู้เรียกไม่ต้องเรียกตัวนี้)</summary>
    public static CreditCancelOutcome CancelOutcome(DateTime sessionStartUtc, DateTime cancelledAtUtc, int freeCancelHours)
        => cancelledAtUtc <= sessionStartUtc.AddHours(-Math.Max(0, freeCancelHours))
            ? CreditCancelOutcome.Refund
            : CreditCancelOutcome.Forfeit;

    /// <summary>รูปเอกสารขาย — บริษัทจด VAT + อัตรา 7/0 ⇒ ใบกำกับภาษี/ใบเสร็จ (ขายเงินสด) · ยกเว้น VAT §81 ⇒ ใบเสร็จ (ห้ามออกใบกำกับ) ·
    /// ไม่จด VAT ⇒ ใบเสร็จ VAT 0</summary>
    public static (DocumentType Type, decimal LineVatRate, bool IssuedAsCashReceipt) SaleDocumentShape(
        bool companyVatRegistered, decimal packageVatRate)
    {
        if (!companyVatRegistered) return (DocumentType.Receipt, 0m, false);
        if (packageVatRate < 0m) return (DocumentType.Receipt, -1m, false);
        return (DocumentType.TaxInvoice, packageVatRate == 0m ? 0m : 7m, true);
    }

    /// <summary>ข้อเท็จจริงของบัตรเครดิตที่ตัวตัดสินการจองต้องใช้</summary>
    public sealed record PassFacts(
        CreditPassStatus Status, DateTime? ExpiresOn, decimal Total, decimal Used, decimal Reserved,
        CreditClassFormat PackageFormat, IReadOnlyCollection<Guid> AllowedServiceIds);

    /// <summary>เหตุที่จองไม่ได้ (ภาษาไทยถึงผู้ใช้) · null = จองได้</summary>
    public static string? CannotBookReason(PassFacts pass, decimal credits, DateTime sessionStartUtc,
        Guid? bookingServiceId, CreditClassFormat sessionFormat, DateTime nowUtc)
    {
        if (pass.Status != CreditPassStatus.Active)
            return $"คอร์สนี้อยู่ในสถานะ {StatusTh(pass.Status)} — ใช้จองไม่ได้";
        if (IsExpired(pass.ExpiresOn, nowUtc))
            return $"คอร์สหมดอายุแล้ว (ใช้ได้ถึง {ThaiDate.ToThaiDisplayString(pass.ExpiresOn!.Value)})";
        if (pass.ExpiresOn.HasValue && ThaiDate.CalendarDateUtc(sessionStartUtc) > pass.ExpiresOn.Value.Date)
            return $"รอบเรียนอยู่หลังวันหมดอายุของคอร์ส (ใช้ได้ถึง {ThaiDate.ToThaiDisplayString(pass.ExpiresOn.Value)})";
        if (credits <= 0m) return "จำนวนเครดิตที่ใช้ต้องมากกว่า 0";
        var available = Available(pass.Total, pass.Used, pass.Reserved);
        if (available < credits)
            return $"เครดิตคงเหลือไม่พอ (เหลือ {available:0.##} · ต้องใช้ {credits:0.##})";
        if (pass.PackageFormat != CreditClassFormat.Any && sessionFormat != CreditClassFormat.Any
            && pass.PackageFormat != sessionFormat)
            return $"คอร์สนี้ใช้ได้กับคลาสแบบ {FormatTh(pass.PackageFormat)} เท่านั้น";
        if (pass.AllowedServiceIds.Count > 0 && (bookingServiceId is not Guid sid || !pass.AllowedServiceIds.Contains(sid)))
            return "คอร์สนี้ใช้กับบริการ/คลาสนี้ไม่ได้ (ไม่อยู่ในรายการบริการที่ใช้ได้ของแพ็กเกจ)";
        return null;
    }

    /// <summary>เลือกครูฝึกให้อัตโนมัติ — ตัดคนที่ติดรอบซ้อนเวลา (<paramref name="busy"/>) แล้วเลือกคนที่มีภาระในช่วงน้อยที่สุด ·
    /// เสมอกัน ⇒ ตามลำดับที่ตั้งในแพ็กเกจ · ไม่มีใครว่าง ⇒ null (ผู้เรียกบอกผู้ใช้ — ห้ามยัดคนที่ติดรอบ)</summary>
    public static Guid? PickTrainer(IReadOnlyList<Guid> eligibleInOrder, IReadOnlyDictionary<Guid, int> load,
        IReadOnlySet<Guid> busy)
    {
        Guid? best = null;
        var bestLoad = int.MaxValue;
        foreach (var t in eligibleInOrder)
        {
            if (busy.Contains(t)) continue;
            var l = load.TryGetValue(t, out var v) ? v : 0;
            if (l < bestLoad) { best = t; bestLoad = l; }
        }
        return best;
    }

    public static string StatusTh(CreditPassStatus s) => s switch
    {
        CreditPassStatus.Active => "ใช้งานได้",
        CreditPassStatus.Exhausted => "ใช้ครบแล้ว",
        CreditPassStatus.Expired => "หมดอายุ",
        CreditPassStatus.Cancelled => "ยกเลิก",
        _ => s.ToString(),
    };

    public static string FormatTh(CreditClassFormat f) => f switch
    {
        CreditClassFormat.OneOnOne => "ตัวต่อตัว (1-on-1)",
        CreditClassFormat.Group => "กลุ่ม",
        _ => "ทุกแบบ",
    };
}
