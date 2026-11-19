using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>แถวยอดท้ายของ "ใบเสร็จรับชำระ" ที่ไม่ถือ VAT ของตัวเอง</b> — ตัวตัดสินเดียวของ renderer ทั้งสอง (HTML <c>BuildDocumentHtml</c> ·
/// QuestPDF <c>DocumentRenderer</c>) และหน้ารายละเอียด (<c>documents.html</c> อ่านผลจาก <c>DocumentResponse.SettlementTotals</c>) — รอบ 273
///
/// <para>═══ ที่มา (ผู้ใช้รายงาน 2026-11-19) ═══ ใบกำกับภาษี TIV-20261026-0001 มูลค่า 700,000.00 + VAT 49,000.00 = 749,000.00 → บันทึกรับชำระ →
/// ใบเสร็จ REC-20261029-0001 บรรทัดเดียว "รับชำระเงินตามใบกำกับภาษีเลขที่ …" 749,000.00 · กระดาษและหน้ารายละเอียดพิมพ์
/// <b>"ยอดรวมก่อน VAT 749,000.00 · VAT 0.00 · ยอดสุทธิ 749,000.00"</b> — ผู้อ่านเข้าใจว่าเงิน 749,000 ยังไม่รวม VAT ทั้งที่รวมแล้ว และ VAT ถูกเรียกเก็บบนใบกำกับไปแล้ว</para>
///
/// <para>═══ ข้อมูลที่เก็บถูกอยู่แล้ว ═══ ใบเสร็จนี้<b>ไม่เป็นเจ้าของภาษีขาย</b> (VatAmount = 0 · <see cref="SettlementReceiptPolicy.CarriesTaxInvoiceRole"/> = false ·
/// รอบ 269 <see cref="OutputVatOwnership"/> ให้อยู่นอก ภ.พ.30/§87) — ตัวเลขที่ persist ถูก ผิดแค่ "ป้าย" ⇒ ตัวนี้เปลี่ยนเฉพาะการแสดงผล ไม่แตะยอดที่เก็บ/ภาษี</para>
///
/// <para>═══ การแสดงผลที่ถูก ═══ ซ่อนแถว "ยอดรวมก่อน VAT" + "VAT" (ใบนี้ไม่ได้เรียกเก็บ VAT — ห้ามบอกว่ามีฐานก่อน VAT เท่ายอดที่รับ) ·
/// แถวสุดท้ายเป็น "ยอดรับชำระ" · ใต้ยอดพิมพ์บรรทัดอ้างอิงต่อใบต้นทาง "(ตามใบกำกับภาษีเลขที่ … ลงวันที่ …: มูลค่า … · VAT … · รวม …)" + หมายเหตุว่า
/// ยอดรับชำระรวม VAT ตามเอกสารอ้างอิงแล้ว · รับบางส่วน ⇒ บอกว่าเป็นการรับบางส่วนของยอดใด · มีภาษีหัก ณ ที่จ่ายบนใบเสร็จ ⇒ คงแถว "ยอดตัดชำระ"
/// (ก่อนหัก) + แถวภาษีหัก ให้ตัวเลขบวกลงยอดรับชำระได้</para>
///
/// <para>ขอบเขตจงใจแคบ — ใบที่ <b>ถือ VAT ของตัวเอง</b> (ใบเสร็จขายสด · ใบกำกับ ณ วันรับเงิน §78/1 · มัดจำ) ไม่เข้า ⇒ "ยอดรวมก่อน VAT / VAT" เหมือนเดิมทุกตัวอักษร ·
/// ใบต้นทางที่ไม่มี VAT ก็ไม่เข้า (ไม่มี VAT ให้อธิบาย · กระดาษเดิมไม่ถูกแตะ)</para>
/// </summary>
public static class SettlementReceiptTotals
{
    public const string RuleCode = "RCPT-SETTLEMENT-TOTALS";

    private const decimal Eps = 0.005m;

    /// <summary>ใบต้นทางหนึ่งใบที่ใบเสร็จนี้รับชำระ (ค่าตามที่ persist บนใบต้นทาง · สกุลของใบต้นทาง)</summary>
    /// <param name="SubTotal">มูลค่าก่อน VAT (หลังส่วนลด)</param>
    /// <param name="TotalAmount">ยอดที่ลูกค้าต้องจ่ายตามใบ (สุทธิหลังภาษีหัก ณ ที่จ่าย — <c>WhtGrossUp.PayeeTotal</c>)</param>
    public sealed record Source(DocumentType Type, string Number, DateTime Date,
        decimal SubTotal, decimal VatAmount, decimal WithholdingTaxAmount, decimal TotalAmount);

    /// <summary>ข้อเท็จจริงของใบเสร็จ (entity) + ใบต้นทางที่อ้าง — ผู้เรียกโหลดมา (ตัวตัดสินไม่รับ DbContext)</summary>
    public sealed record Facts(
        DocumentType ReceiptType,
        bool IsTaxInvoiceByLaw,
        bool IsDeposit,
        decimal ReceiptVatAmount,
        decimal ReceiptWithholdingTaxAmount,
        decimal ReceiptTotalAmount,
        IReadOnlyList<Source> Sources);

    /// <param name="AmountReceived">ยอดแถวสุดท้าย "ยอดรับชำระ" (= <c>TotalAmount</c> ของใบเสร็จ — ค่าเดิม ไม่คิดใหม่)</param>
    /// <param name="ShowSettledGrossRow">ใบเสร็จมีภาษีหัก ณ ที่จ่าย ⇒ คงแถวยอดตัดชำระก่อนหัก (ป้าย "ยอดตัดชำระ") ให้บวกลงยอดรับชำระได้ · ไม่มี ⇒ ไม่มีแถวนี้</param>
    /// <param name="IsPartial">ยอดรับครั้งนี้น้อยกว่ายอดตามใบต้นทางรวม</param>
    /// <param name="SourcesTotal">ยอดตามใบต้นทางรวม (สุทธิ) — ใช้ในข้อความ "รับชำระบางส่วนของยอด …"</param>
    /// <param name="References">บรรทัดอ้างอิง (หนึ่งบรรทัดต่อใบต้นทาง · ตามลำดับที่ส่งมา)</param>
    public sealed record View(decimal AmountReceived, bool ShowSettledGrossRow, bool IsPartial, decimal SourcesTotal,
        IReadOnlyList<Source> References);

    /// <summary>ชนิดใบเสร็จที่ตัวนี้ดูแล (ใบรับเงินฝั่งขาย)</summary>
    public static bool IsReceiptType(DocumentType t) => t is DocumentType.Receipt or DocumentType.ReceiptVoucher;

    /// <summary><c>null</c> = ใบนี้ใช้แถวยอดปกติ ("ยอดรวมก่อน VAT / VAT / ยอดสุทธิ") · มีค่า = ใบเสร็จรับชำระที่ไม่ถือ VAT ของใบต้นทางที่มี VAT</summary>
    public static View? Decide(Facts f)
    {
        if (!IsReceiptType(f.ReceiptType)) return null;
        // ใบที่ถือ VAT ของตัวเอง (ใบกำกับ ณ วันรับเงิน §78/1 · ขายสด · มัดจำ) = แถวปกติ — ทิศตรงข้ามที่เทสต์ล็อกไว้
        if (f.IsTaxInvoiceByLaw || f.IsDeposit || Math.Abs(f.ReceiptVatAmount) >= Eps) return null;
        var sources = (f.Sources ?? Array.Empty<Source>())
            .Where(s => SettlementReceiptPolicy.IsReceivableSource(s.Type)).ToList();
        if (sources.Count == 0) return null;
        // ใบต้นทางไม่มี VAT ⇒ ไม่มีอะไรต้องอธิบาย (กระดาษเดิม)
        if (sources.Sum(s => s.VatAmount) < Eps) return null;
        var sourcesTotal = sources.Sum(s => s.TotalAmount);
        var partial = f.ReceiptTotalAmount < sourcesTotal - Eps;
        return new View(f.ReceiptTotalAmount, f.ReceiptWithholdingTaxAmount > 0m, partial, sourcesTotal, sources);
    }

    /// <summary>"รวม" ในบรรทัดอ้างอิง = มูลค่า + VAT (ยอดบนใบกำกับก่อนหัก ณ ที่จ่าย — ตรงกับตัวเลขที่ลูกค้าเห็นบนใบต้นทาง)</summary>
    public static decimal GrossOf(Source s) => s.SubTotal + s.VatAmount;
}
