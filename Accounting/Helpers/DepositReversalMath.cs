namespace Accounting.Helpers;

/// <summary>
/// การคำนวณ "pure" (ไม่มี DB) สำหรับการนำมัดจำมาหักแบบขับ JE (driveDeposit) —
/// แยกออกมาเพื่อ unit-test ได้ (โค้ด AutoPostToJournalAsync ผูก DbContext + raw
/// SQL FOR UPDATE จึง test ตรง ๆ ใน CI ไม่ได้). ทุกฟังก์ชันต้องให้ผลตรงกับ
/// สูตรที่ฝังใน DocumentService เป๊ะ — เป็น single source of truth ของสูตร.
/// </summary>
public static class DepositReversalMath
{
    /// <summary>
    /// แยก "ยอดมัดจำที่นำมาหัก (gross รวม VAT)" เป็นฐาน (ex-VAT) + VAT ตามสัดส่วน
    /// ของบัญชีที่ใบมัดจำเครดิตไว้จริง.
    ///   ratio = vatCr / (deferredCr + vatCr)   (0 ถ้า gross ≤ 0 → ฐานเต็ม ไม่มี VAT)
    ///   base  = round(appliedGross × (1 − ratio), 2, AwayFromZero)
    ///   vat   = round(appliedGross − base, 2, AwayFromZero)   (ผลต่าง → รวมกันได้ gross เป๊ะ)
    /// ใช้ทั้ง 3 เส้นใน DocumentService: GL-driven (ขา Cr จริง), fallback (field
    /// เอกสาร: deferredCr=SubTotal, vatCr=VatAmount), และ journal-ref (เคส B).
    /// </summary>
    public static (decimal Base, decimal Vat) SplitBaseVat(decimal appliedGross, decimal deferredCr, decimal vatCr)
    {
        var gross = deferredCr + vatCr;
        var ratio = gross > 0m ? vatCr / gross : 0m;
        var baseAmt = System.Math.Round(appliedGross * (1 - ratio), 2, System.MidpointRounding.AwayFromZero);
        var vat = System.Math.Round(appliedGross - baseAmt, 2, System.MidpointRounding.AwayFromZero);
        return (baseAmt, vat);
    }

    /// <summary>
    /// แยกเลขใบมัดจำที่อ้าง (depositAppliedRef) — รองรับหลายใบคั่นจุลภาค
    /// ("REC-0009, REC-0010") + ตัดช่องว่าง + ข้ามค่าว่าง. null/ว่าง → array ว่าง.
    /// </summary>
    public static string[] ParseDepositRefs(string? refs) =>
        (refs ?? string.Empty)
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

    /// <summary>
    /// แยกยอด "คืนมัดจำ" (gross) เป็นฐาน + VAT พร้อมด่าน "คืนเกินคงเหลือ" — ตัวเดียวของ <c>RefundDepositAsync</c>
    /// และตัววางแผนคืนเงินของที่พัก (รอบ 193 ฝ่ายค้านรอบสอง N4)
    /// <para><b>VAT คิดจากยอดคืนสะสม</b>: vat = round((คืนแล้ว + gross) × VAT/รวม) − round(คืนแล้ว × VAT/รวม) · ฐาน = gross − vat ·
    /// ฐานคงเหลือ = ฐาน − รับรู้แล้ว − (คืนแล้ว − round(คืนแล้ว × VAT/รวม)) ⇒ ด่านขึ้นกับ <b>ยอดคืนสะสม</b> อย่างเดียว
    /// ไม่ว่าจะแบ่งกี่งวด (เดิมปัด VAT ทีละงวดแต่เทียบกับฐานที่ปัดจากยอดสะสม ⇒ คืน 72.02 แล้ว 3,154.54 ไม่ผ่าน ค้าง 0.01 ถาวร)
    /// · คืนครั้งเดียวจากศูนย์ = สูตรเดิมทุกประการ</para></summary>
    public static (decimal Base, decimal Vat, bool Ok, decimal RemainingBase) RefundSplit(
        decimal gross, decimal subTotal, decimal vatAmount, decimal total, decimal realizedBase, decimal refundedGross)
    {
        const System.MidpointRounding R = System.MidpointRounding.AwayFromZero;
        var portion = total > 0m ? vatAmount / total : 0m;
        var vatBefore = System.Math.Round(refundedGross * portion, 2, R);
        var vat = System.Math.Round((refundedGross + gross) * portion, 2, R) - vatBefore;
        var baseAmt = gross - vat;
        var remainingBase = subTotal - realizedBase - (refundedGross - vatBefore);
        return (baseAmt, vat, gross > 0m && baseAmt <= remainingBase + 0.005m, remainingBase);
    }

    /// <summary>
    /// รอบ 194 R3 (P-4) — คืนยอด "หักมัดจำแบบขับ JE" เมื่อใบที่หักถูกยกเลิก/ลบ: แยกขา Dr ของ JE ต้นฉบับของใบนั้นเป็นรายใบมัดจำ
    /// <list type="bullet">
    /// <item>มัดจำใบเดียว ⇒ ทุกขา Dr 215/217 = ฐานของใบนั้น · มีขา Dr 21913 = ใบนี้ประทับ RecognizedAt (สูตรเดิมทุกตัวอักษร · audit F1–F3)</item>
    /// <item>หลายใบ ⇒ ขาถูกผูกกับใบมัดจำจากคำอธิบายที่เส้นหักเขียนเอง (“นำมัดจำ {เลข} มาหักเต็มใบ” · “มัดจำ {เลข} → ใบกำกับ” · “ล้าง VAT มัดจำ {เลข}”)
    /// โดยจับเลขทั้งคำ (REC-1 ไม่ชน REC-10) · ขาที่ผูกไม่ได้ ⇒ <c>Unattributed</c> (ผู้เรียกต้องบอกให้เห็น — ห้ามเดาใบ)</item>
    /// </list>
    /// เดิมเส้นคืนเทียบ <c>DocumentNumber == DepositAppliedRef</c> ตรงตัว ⇒ เลขหลายใบ ("REC-9, REC-10") ไม่เคยตรง ⇒ ยกเลิกใบแล้วยอดมัดจำไม่คืน
    /// </summary>
    /// <param name="depositNumbers">เลขใบมัดจำที่ resolve ได้ (ตามลำดับในเลขอ้างอิง · ไม่ซ้ำ)</param>
    /// <param name="debitLegs">ขา Dr ของ JE ต้นฉบับของใบที่หัก: (รหัสบัญชี · ยอด Dr · คำอธิบายบรรทัด)</param>
    public static DrivesUnrealizeSplit SplitDrivesUnrealize(
        IReadOnlyList<string> depositNumbers, IReadOnlyList<(string AccountCode, decimal Debit, string? Description)> debitLegs)
    {
        static bool IsBase(string code) => code.StartsWith("217", System.StringComparison.Ordinal) || code.StartsWith("215", System.StringComparison.Ordinal);
        static bool IsUndue(string code) => code == "21913";
        var bases = depositNumbers.ToDictionary(n => n, _ => 0m, System.StringComparer.Ordinal);
        var stamped = depositNumbers.ToDictionary(n => n, _ => false, System.StringComparer.Ordinal);
        var unattributed = 0m;
        foreach (var (code, debit, desc) in debitLegs)
        {
            if (debit <= 0m || (!IsBase(code) && !IsUndue(code))) continue;
            string? owner = depositNumbers.Count == 1 ? depositNumbers[0] : OwnerOf(depositNumbers, desc);
            if (owner == null)
            {
                if (IsBase(code)) unattributed += debit;
                continue;
            }
            if (IsBase(code)) bases[owner] += debit;
            else stamped[owner] = true;
        }
        return new DrivesUnrealizeSplit(
            depositNumbers.Select(n => new DrivesUnrealizeShare(n, bases[n], stamped[n])).ToList(), unattributed);
    }

    /// <summary>เลขใบมัดจำที่คำอธิบายบรรทัดอ้าง "มัดจำ {เลข}" ทั้งคำ (ตามด้วยช่องว่าง/วงเล็บ/จุลภาค/ท้ายข้อความ) · ไม่พบ/อ้างหลายใบ = null (ห้ามเดา)</summary>
    private static string? OwnerOf(IReadOnlyList<string> numbers, string? description)
    {
        if (string.IsNullOrEmpty(description)) return null;
        var hits = numbers.Where(n => System.Text.RegularExpressions.Regex.IsMatch(description,
                "มัดจำ\\s+" + System.Text.RegularExpressions.Regex.Escape(n) + "(?=$|[\\s),])"))
            .ToList();
        return hits.Count == 1 ? hits[0] : null;
    }
}

/// <summary>ผลแยกยอดคืนของมัดจำหนึ่งใบ (<see cref="DepositReversalMath.SplitDrivesUnrealize"/>)</summary>
/// <param name="DepositNumber">เลขใบมัดจำ</param>
/// <param name="Base">ฐาน (Dr 215/217) ที่ใบที่หักตัดไปจากมัดจำใบนี้ — ต้องคืนเข้า <c>DepositRealizedAmount</c></param>
/// <param name="Stamped21913">ใบที่หักเป็นผู้ย้าย VAT พักของมัดจำใบนี้ (มีขา Dr 21913) ⇒ ล้าง <c>DepositOutputVatRecognizedAt</c> ได้</param>
public sealed record DrivesUnrealizeShare(string DepositNumber, decimal Base, bool Stamped21913);

/// <summary>ผลแยกยอดคืนทั้งใบ — <paramref name="Unattributed"/> = ฐานที่ผูกกับใบมัดจำใดไม่ได้ (ผู้เรียกต้องบอกให้เห็น)</summary>
public sealed record DrivesUnrealizeSplit(IReadOnlyList<DrivesUnrealizeShare> Shares, decimal Unattributed);
