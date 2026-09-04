namespace Accounting.Helpers;

/// <summary>เหตุผลที่ออก "ใบกำกับภาษีเต็มรูปแทนใบเดิม" ไม่ได้</summary>
public enum FullTaxInvoiceBlockReason
{
    None = 0,
    /// <summary>ใบต้นทางยังไม่อนุมัติ/ถูกยกเลิก — ยังไม่มีการขายที่ต้องรับรอง</summary>
    SourceNotIssued = 1,
    /// <summary>ใบต้นทางไม่มี VAT — ไม่มีภาษีขายให้ใบกำกับรับรอง</summary>
    NoVatOnSource = 2,
    /// <summary>ออกใบแทนไปแล้ว — การขายครั้งเดียวมีใบกำกับได้ใบเดียว</summary>
    AlreadyReplaced = 3,
    /// <summary>ข้อมูลผู้ซื้อไม่ครบ §86/4 — ออกไปก็ยังเป็นใบที่เคลมภาษีซื้อไม่ได้อยู่ดี</summary>
    BuyerIncomplete = 4,
    /// <summary>บริษัทยังไม่จด VAT — ไม่มีสิทธิ์ออกใบกำกับชนิดใดเลย</summary>
    NotVatRegistered = 5,
    /// <summary>ใบต้นทางเป็นใบกำกับเต็มรูปอยู่แล้ว</summary>
    AlreadyFullTaxInvoice = 6,
}

public readonly record struct FullTaxInvoiceEligibility(
    bool Allowed, FullTaxInvoiceBlockReason Reason, string? Message);

/// <summary>
/// **ออกใบกำกับภาษีเต็มรูป "แทน" ใบเสร็จ/ใบกำกับอย่างย่อ** (§86/6 → §86/4)
///
/// ═══ ที่มา (ผู้ใช้ถาม 2026-09-03) ═══
/// ลูกค้าที่รับใบเสร็จ/ใบกำกับภาษี<b>อย่างย่อ</b>ไปแล้ว ภายหลังขอ<b>เต็มรูป</b>เพื่อเคลม
/// ภาษีซื้อ (อย่างย่อเคลมไม่ได้ §82/5(2)) — เดิมระบบแปลงไม่ได้เลย
/// (<c>ValidConversions[Receipt] = [CreditNote, DebitNote]</c>)
///
/// ═══ ทำไมต้องเป็น "ใบแทน" ไม่ใช่ "แปลงเอกสาร" ═══
/// ใบเสร็จที่มี VAT <b>นับเป็นภาษีขายเข้า ภ.พ.30 ไปแล้ว</b> (tax point = วันรับเงิน
/// §78/1) · ถ้าออกใบกำกับเต็มรูปเพิ่มอีกใบโดยไม่เรียกคืนใบเดิม ⇒ <b>ภาษีขายเข้า
/// รายงานสองรอบ</b> และรายได้ซ้ำ · การขายครั้งเดียวมีใบกำกับได้ <b>ใบเดียว</b>
///
/// จึงออกแบบเป็น: ใบใหม่ <b>แทนที่</b> ใบเดิม → ผูกกันสองทาง → รายงานภาษีขายนับ
/// <b>ใบแทน</b> และข้ามใบที่ถูกแทน ⇒ ยอด VAT รวมไม่เปลี่ยน · ไม่มี JE ใหม่
/// (รายได้/ภาษีขายลงไปแล้วตั้งแต่ใบเดิม)
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item>ใบแทนใช้ <b>วันที่ของใบเดิม</b> — tax point เกิดไปแล้ว ย้ายงวดไม่ได้
///   (ย้ายงวด = ภ.พ.30 ของสองเดือนผิดพร้อมกัน)</item>
/// <item>ต้องมีข้อมูลผู้ซื้อครบ §86/4 ก่อน มิฉะนั้นใบใหม่ก็ยังเป็นใบที่เคลมไม่ได้
///   — บล็อกพร้อมบอกว่าขาดอะไร ดีกว่าออกใบที่ไม่มีประโยชน์</item>
/// <item>ออกได้ <b>ครั้งเดียว</b> ต่อใบ</item>
/// </list>
/// </summary>
public static class FullTaxInvoiceReplacement
{
    /// <summary>ตรวจว่าออกใบแทนได้ไหม — ฟังก์ชันบริสุทธิ์ (ไม่แตะฐานข้อมูล)
    ///
    /// <para><paramref name="missingBuyerFields"/> ส่งมาจากตัวตรวจ §86/4 ที่มีอยู่แล้ว
    /// (ห้ามเขียนกติกา §86/4 ซ้ำที่นี่ — จะกลายเป็นสำเนาที่ drift)</para></summary>
    public static FullTaxInvoiceEligibility Check(
        bool sourceIsIssued,
        bool sourceIsFullTaxInvoice,
        decimal sourceVatAmount,
        bool alreadyReplaced,
        bool companyIsVatRegistered,
        IReadOnlyCollection<string> missingBuyerFields)
    {
        if (!companyIsVatRegistered)
            return new(false, FullTaxInvoiceBlockReason.NotVatRegistered,
                "บริษัทยังไม่ได้จดทะเบียนภาษีมูลค่าเพิ่ม จึงออกใบกำกับภาษีไม่ได้");

        if (!sourceIsIssued)
            return new(false, FullTaxInvoiceBlockReason.SourceNotIssued,
                "ใบต้นทางยังไม่อนุมัติหรือถูกยกเลิกไปแล้ว");

        if (sourceIsFullTaxInvoice)
            return new(false, FullTaxInvoiceBlockReason.AlreadyFullTaxInvoice,
                "ใบนี้เป็นใบกำกับภาษีเต็มรูปอยู่แล้ว");

        if (sourceVatAmount <= 0.005m)
            return new(false, FullTaxInvoiceBlockReason.NoVatOnSource,
                "ใบต้นทางไม่มีภาษีมูลค่าเพิ่ม จึงไม่มีภาษีขายให้ใบกำกับรับรอง");

        if (alreadyReplaced)
            return new(false, FullTaxInvoiceBlockReason.AlreadyReplaced,
                "ใบนี้ออกใบกำกับภาษีเต็มรูปแทนไปแล้ว — การขายครั้งเดียวมีใบกำกับได้ใบเดียว");

        if (missingBuyerFields.Count > 0)
            return new(false, FullTaxInvoiceBlockReason.BuyerIncomplete,
                "ข้อมูลผู้ซื้อยังไม่ครบตาม §86/4 — ออกไปก็ยังเป็นใบที่ผู้ซื้อเคลมภาษีซื้อไม่ได้ · "
                + "ขาด: " + string.Join(" · ", missingBuyerFields));

        return new(true, FullTaxInvoiceBlockReason.None, null);
    }

    /// <summary>หมายเหตุที่ต้องพิมพ์บน<b>ใบแทน</b> — อ้างใบเดิมเพื่อให้ตรวจสอบย้อนหลังได้
    /// และเพื่อให้ผู้ซื้อรู้ว่าใบเดิมถูกยกเลิก</summary>
    public static string ReplacementNote(string originalNumber, DateTime originalDate)
        => $"ใบกำกับภาษีฉบับนี้ออกแทน \"{originalNumber}\" ลงวันที่ "
         + $"{ThaiDate.ToThaiDisplayString(originalDate)} ซึ่งได้เรียกคืนและยกเลิกแล้ว";

    /// <summary>หมายเหตุที่ต้องบันทึกบน<b>ใบเดิม</b> (ภายใน) — ผู้สอบบัญชีต้องเห็นว่า
    /// ทำไมใบนี้ไม่อยู่ในรายงานภาษีขาย</summary>
    public static string OriginalRecalledNote(string replacementNumber, string? reason)
        => $"ถูกแทนที่ด้วยใบกำกับภาษีเต็มรูป \"{replacementNumber}\" — เรียกคืนใบนี้จากผู้ซื้อแล้ว "
         + $"(ไม่นับซ้ำในรายงานภาษีขาย)"
         + (string.IsNullOrWhiteSpace(reason) ? "" : $" · เหตุผล: {reason}");
}
