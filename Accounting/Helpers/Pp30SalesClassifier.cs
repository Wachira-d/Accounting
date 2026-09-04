namespace Accounting.Helpers;

/// <summary>บรรทัดของเอกสารเท่าที่ ภ.พ.30 ต้องใช้ (ตัดส่วนที่ไม่เกี่ยวออก)</summary>
public readonly record struct Pp30Line(decimal VatRate, decimal Amount, decimal VatAmount);

/// <summary>ยอดขายของเอกสาร 1 ใบ แยกตามช่องของ ภ.พ.30</summary>
public readonly record struct Pp30SalesSplit(
    decimal TaxableBase,
    decimal ZeroRatedBase,
    decimal ExemptBase,
    decimal OutputVat)
{
    /// <summary>ช่อง 6 "ยอดขายในเดือนนี้" = ผลรวมทั้งสามช่อง</summary>
    public decimal TotalSales => TaxableBase + ZeroRatedBase + ExemptBase;

    public bool HasAnything =>
        TaxableBase != 0m || ZeroRatedBase != 0m || ExemptBase != 0m || OutputVat != 0m;
}

/// <summary>
/// **แยกยอดขายเข้าช่องของ ภ.พ.30 — ฟังก์ชันบริสุทธิ์**
///
/// ═══ ที่มา (บั๊กจริง · ผลตรวจ C-T01) ═══
/// <c>GenerateVatReport</c> คัดเอกสารด้วย <c>VatAmount != 0</c> ⇒ ใบที่
/// **ทั้งใบเป็น 0% หรือยกเว้น** มี <c>VatAmount == 0</c> จึง<b>ไม่เคยเข้ารายงานเลย</b>:
/// <list type="bullet">
/// <item><b>ช่อง 7</b> ยอดขายที่เสียภาษีอัตราร้อยละ 0 (ส่งออก §80/1) — หายทั้งช่อง</item>
/// <item><b>ช่อง 8</b> ยอดขายที่ได้รับยกเว้น (§81) — นับได้เฉพาะใบที่บังเอิญมีบรรทัด
///   7% ปนอยู่ด้วย (บิล Makro/BigC) · ใบที่ยกเว้นล้วนหายหมด</item>
/// </list>
/// และยอดยกเว้นที่นับได้ ถูกยัดรวมเป็นบรรทัดเดียวชื่อ "ยอดขาย/<b>ซื้อ</b>ยกเว้นภาษี"
/// ⇒ ยอดฝั่งซื้อปนเข้าช่องฝั่งขาย ซึ่ง ภ.พ.30 แยกกันคนละช่อง
///
/// ═══ ความหมายของ VatRate (ยึดตามที่ฟอร์มให้ผู้ใช้เลือกจริง) ═══
/// <c>documents.html</c> มีตัวเลือก 3 ค่าเท่านั้น: <c>7</c> · <c>0</c> · <c>-1</c>
/// <list type="bullet">
/// <item><c>7</c> (หรือ &gt; 0) = เสียภาษี → ช่อง 9</item>
/// <item><c>0</c> = **เสียภาษีอัตราร้อยละ 0** (§80/1 ส่งออก/บริการที่ใช้ต่างประเทศ) → ช่อง 7
///   <para>⚠️ ตีความแบบนี้ได้เพราะ ภ.พ.30 ยื่นเฉพาะผู้ประกอบการที่<b>จดทะเบียน VAT</b> —
///   บริษัทที่ไม่จดไม่มีรายงานนี้ให้ผิดตั้งแต่ต้น · ผู้เรียกต้องเช็ค
///   <c>CompanySettings.VatRegistered</c> ก่อนอยู่แล้ว</para></item>
/// <item><c>-1</c> = ยกเว้น (§81 สินค้าเกษตรไม่แปรรูป/การแพทย์/การศึกษา) → ช่อง 8
///   <para>ยกเว้น ≠ 0% : 0% <b>ออกใบกำกับได้และเคลมภาษีซื้อได้</b> ·
///   ยกเว้น<b>ออกใบกำกับไม่ได้</b>และภาษีซื้อลงเป็นต้นทุน (CLAUDE.md กฎเหล็ก #2 D)</para></item>
/// </list>
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item>ใช้ <c>Line.Amount</c> ซึ่งเป็นยอด <b>net ก่อน VAT หลังส่วนลด</b> ตาม
///   convention ของเรพ (DOCUMENT_FLOW §6.2b) — ห้ามคิดใหม่จาก qty × price</item>
/// <item>ภาษีขายใช้ <c>Line.VatAmount</c> ที่บันทึกไว้จริง ไม่คิดใหม่จากอัตรา
///   (ใบจาก OCR/integration เก็บ VAT ที่ปัดมาแล้ว — คิดใหม่จะต่าง 0.01)</item>
/// <item>ไม่ปัดเศษที่นี่ — ผู้เรียกรวมทั้งงวดแล้วค่อยปัดครั้งเดียว</item>
/// </list>
/// </summary>
public static class Pp30SalesClassifier
{
    /// <summary>ค่า <c>VatRate</c> ที่แปลว่า "ยกเว้นภาษี" (§81) ตาม convention ของเรพ</summary>
    public const decimal ExemptRate = -1m;

    public static Pp30SalesSplit Split(IEnumerable<Pp30Line> lines)
    {
        decimal taxable = 0m, zero = 0m, exempt = 0m, vat = 0m;
        foreach (var l in lines)
        {
            if (l.VatRate == ExemptRate) exempt += l.Amount;
            else if (l.VatRate == 0m) zero += l.Amount;
            else { taxable += l.Amount; }
            vat += l.VatAmount;
        }
        return new Pp30SalesSplit(taxable, zero, exempt, vat);
    }

    /// <summary>เอกสารใบนี้ต้องเข้ารายงาน ภ.พ.30 ไหม — <b>ตัวตัดสินตัวเดียว</b>
    ///
    /// <para>เดิมเงื่อนไขคือ "มี VAT" อย่างเดียว ซึ่งตัดช่อง 7/8 ทิ้งทั้งช่อง ·
    /// ที่ถูกคือ "มีอะไรให้รายงาน" — ภาษีขาย **หรือ** ยอดในช่องใดช่องหนึ่ง</para></summary>
    public static bool ShouldReport(Pp30SalesSplit split) => split.HasAnything;
}
