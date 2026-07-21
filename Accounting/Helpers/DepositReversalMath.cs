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
}
