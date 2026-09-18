namespace Accounting.Helpers;

/// <summary>ใบที่สแกนมาเป็น "ใบกำกับภาษีเต็มรูปที่สมบูรณ์" ตาม §86/4 แค่ไหน</summary>
public enum PaperTaxInvoiceGrade
{
    /// <summary>ไม่มีกระดาษให้ดู / อ่านข้อความไม่ออก — <b>ยังไม่ได้ตรวจ</b>
    /// (ต่างจาก <see cref="Incomplete"/> ซึ่งแปลว่าตรวจแล้วไม่ครบ)</summary>
    Unknown = 0,

    /// <summary>ตรวจแล้ว<b>ไม่ครบ</b> §86/4 — ใบแบบนี้ "ความเงียบ" ของมันไม่ใช่หลักฐาน</summary>
    Incomplete = 1,

    /// <summary>ครบทุกรายการที่ §86/4 บังคับ — เป็นใบที่ผู้ขายออกอย่างถูกต้อง
    /// ⇒ สิ่งที่<b>ไม่ได้</b>พิมพ์ไว้บนใบ ก็เป็นข้อมูลเช่นกัน</summary>
    Complete = 2,
}

/// <param name="Grade">ผลตัดสิน</param>
/// <param name="Missing">รายการที่ขาด (ว่าง = ครบ) — เอาไปโชว์ผู้ใช้/ลง audit ได้ตรง ๆ</param>
/// <param name="Reason">สรุปภาษาไทยหนึ่งประโยค</param>
public readonly record struct PaperTaxInvoiceVerdict(
    PaperTaxInvoiceGrade Grade, IReadOnlyList<string> Missing, string Reason);

/// <summary>ข้อเท็จจริงของใบที่สแกนมา (ผู้เรียกดึงมาให้ — helper ไม่แตะฐาน)</summary>
/// <param name="RawText">ข้อความทั้งใบ (normalize แล้ว) — ว่าง = ไม่มีกระดาษให้ดู</param>
/// <param name="SellerName">ชื่อผู้ขายที่อ่านได้</param>
/// <param name="SellerTaxId">เลขผู้เสียภาษีผู้ขาย</param>
/// <param name="SellerAddress">ที่อยู่ผู้ขาย</param>
/// <param name="BuyerName">ชื่อผู้ซื้อที่อ่านได้จาก<b>กระดาษ</b> (ไม่ใช่ชื่อบริษัทเราจากฐาน)</param>
/// <param name="BuyerAddress">ที่อยู่ผู้ซื้อที่อ่านได้จากกระดาษ</param>
/// <param name="DocumentNumber">เลขที่ใบกำกับ</param>
/// <param name="DocumentDate">วันที่บนใบ</param>
/// <param name="LineCount">จำนวนบรรทัดรายการที่แกะได้</param>
/// <param name="SubTotal">ยอดก่อน VAT</param>
/// <param name="VatAmount">ยอด VAT (<c>null</c> = อ่านไม่ได้ ≠ ศูนย์)</param>
/// <param name="TotalAmount">ยอดรวม</param>
public readonly record struct PaperTaxInvoiceFacts(
    string? RawText,
    string? SellerName, string? SellerTaxId, string? SellerAddress,
    string? BuyerName, string? BuyerAddress,
    string? DocumentNumber, DateTime? DocumentDate,
    int LineCount,
    decimal? SubTotal, decimal? VatAmount, decimal? TotalAmount);

/// <summary>
/// **"ใบนี้เป็นใบกำกับภาษีเต็มรูปที่สมบูรณ์ไหม" — ตัวตัดสินตัวเดียว** (pure ไม่มี I/O)
///
/// ═══ ทำไมต้องมี (คำตัดสินเจ้าของโปรเจกต์ 2026-09-18 รอบ 178) ═══
/// ชั้นที่ 0 ของ <see cref="WhtApplicabilityEvidence"/> เดิมอ่านว่า "กระดาษไม่มีส่วน
/// หัก ณ ที่จ่าย ⇒ ไม่ต้องหัก" กับ<b>กระดาษทุกใบ</b> — ทีม T1 ท้วงว่าการ "ไม่พบคำ"
/// เป็นหลักฐานเชิงลบที่<b>อ่อนกว่า</b>การประกาศของมนุษย์ (บรรทัดที่ผู้ใช้ตั้งประเภท
/// เงินได้ ม.40 ไว้เอง) แล้วเจ้าของตัดสินว่า: <b>"ถ้าเป็นใบกำกับภาษีที่สมบูรณ์
/// กระดาษชนะ"</b> — เหตุผลเชิงธุรกิจแข็งแรง: ผู้ขายที่ออกใบครบ §86/4 คือผู้ขายที่
/// ทำเอกสารเป็น ⇒ ถ้าเงินก้อนนี้อยู่ในข่ายถูกหัก เขาจะพิมพ์บรรทัดหัก ณ ที่จ่ายมาเอง
/// ⇒ "ใบที่สมบูรณ์แต่ไม่มีบรรทัดนั้น" จึงเป็นหลักฐานจริง ส่วนใบที่กรอกไม่ครบ
/// ไม่มีน้ำหนักพอจะตีความความเงียบของมัน
///
/// ═══ สิ่งที่<b>ไม่</b>นับเป็นเงื่อนไข และทำไม ═══
/// <list type="bullet">
/// <item><b>รหัสสาขา</b> (ประกาศอธิบดีฯ 199) — ช่องนี้ของเรามีตัวเติมค่าตั้งต้น
/// <c>00000</c> อยู่ ⇒ ค่าที่เห็นอาจไม่ได้มาจากกระดาษเลย. ด่านที่ตรวจค่าที่ตัวเอง
/// เติมให้ = ผ่านตลอดกาล (หลักการข้อ 6) จึงตัดออกอย่างตั้งใจ</item>
/// <item><b>เลขผู้เสียภาษีผู้ซื้อ</b> — §86/4 บังคับเฉพาะเมื่อผู้ซื้อจด VAT
/// ซึ่งเป็นเงื่อนไขที่ตัดสินจากตัวเราเอง ไม่ใช่จากกระดาษ</item>
/// </list>
///
/// ═══ ทิศของความผิดพลาด ═══ ตัดสินผิดเป็น <see cref="PaperTaxInvoiceGrade.Incomplete"/>
/// (เช่นใบอัตราศูนย์ที่ไม่มียอด VAT ให้อ่าน) ⇒ ตกไปชั้นถัดไป ⇒ อย่างมากคือ<b>เตือนเกิน</b>
/// ซึ่งผู้ใช้เห็นและกดผ่านได้ · ตัดสินผิดเป็น <c>Complete</c> ⇒ <b>เงียบผิด</b> ซึ่ง
/// ไม่มีใครเห็น และ §54 ให้ผู้จ่ายรับผิด ⇒ เกณฑ์ทุกข้อจึงเลือกฝั่ง "เข้มไว้ก่อน"
/// </summary>
public static class PaperTaxInvoiceCompleteness
{
    /// <summary>ผลต่างที่ยอมให้ยอดรวมไม่ลงตัวพอดี (ปัดเศษรายบรรทัด)</summary>
    private const decimal ReconcileTolerance = 0.02m;

    private static string Squash(string? s)
        => Services.Implementations.Ocr.ThaiTextNormalizer.SquashForKeywordMatch(s);

    /// <summary>ตัดสินจากข้อเท็จจริงที่ผู้เรียกส่งมา</summary>
    public static PaperTaxInvoiceVerdict Judge(PaperTaxInvoiceFacts f)
    {
        if (string.IsNullOrWhiteSpace(f.RawText))
            return new(PaperTaxInvoiceGrade.Unknown, Array.Empty<string>(),
                "ไม่มีข้อความจากกระดาษให้ตรวจ");

        var text = Squash(f.RawText);
        var hasHeader = text.Contains(Squash("ใบกำกับภาษี"), StringComparison.Ordinal);
        var abbreviatedOnly = hasHeader
            && text.Contains(Squash("ใบกำกับภาษีอย่างย่อ"), StringComparison.Ordinal);

        if (!hasHeader)
            return new(PaperTaxInvoiceGrade.Incomplete, new[] { "คำว่า \"ใบกำกับภาษี\" บนหัวเอกสาร" },
                "กระดาษใบนี้ไม่ใช่ใบกำกับภาษีเต็มรูป (ไม่มีคำว่า \"ใบกำกับภาษี\")");

        if (abbreviatedOnly)
            return new(PaperTaxInvoiceGrade.Incomplete, new[] { "เป็นใบกำกับภาษี\"อย่างย่อ\" (§86/6)" },
                "ใบกำกับภาษีอย่างย่อไม่ใช่ใบเต็มรูป — ใช้เป็นภาษีซื้อไม่ได้ (§82/5(2)) "
                + "และไม่มีช่องหัก ณ ที่จ่ายอยู่แล้วโดยธรรมชาติ");

        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(f.SellerName)) missing.Add("ชื่อผู้ขาย");
        if (!ThaiTaxId.IsPlausibleFromScan(f.SellerTaxId)) missing.Add("เลขผู้เสียภาษีผู้ขายที่ถูกต้อง 13 หลัก");
        if (string.IsNullOrWhiteSpace(f.SellerAddress)) missing.Add("ที่อยู่ผู้ขาย");
        if (string.IsNullOrWhiteSpace(f.BuyerName)) missing.Add("ชื่อผู้ซื้อ");
        if (string.IsNullOrWhiteSpace(f.BuyerAddress)) missing.Add("ที่อยู่ผู้ซื้อ");
        if (string.IsNullOrWhiteSpace(f.DocumentNumber)) missing.Add("เลขที่ใบกำกับ");
        if (f.DocumentDate == null) missing.Add("วัน เดือน ปี ที่ออกใบ");
        if (f.LineCount <= 0) missing.Add("รายการสินค้า/บริการอย่างน้อย 1 บรรทัด");

        // VAT ต้อง "แยกออกจากมูลค่า" ให้เห็น (§86/4(6)) — `null` = อ่านไม่ได้ ไม่ใช่ศูนย์
        if (f.VatAmount == null) missing.Add("จำนวนภาษีมูลค่าเพิ่มที่แยกบรรทัด");
        else if ((f.SubTotal ?? 0m) <= 0m) missing.Add("มูลค่าสินค้า/บริการก่อน VAT");
        else if (f.TotalAmount is > 0m
                 && Math.Abs((f.SubTotal ?? 0m) + f.VatAmount.Value - f.TotalAmount.Value) > ReconcileTolerance)
            missing.Add("ยอดที่กระทบกันได้ (ก่อน VAT + VAT = ยอดรวม)");

        return missing.Count == 0
            ? new(PaperTaxInvoiceGrade.Complete, Array.Empty<string>(),
                "ใบกำกับภาษีเต็มรูปครบตาม §86/4")
            : new(PaperTaxInvoiceGrade.Incomplete, missing,
                "ใบกำกับภาษีใบนี้ยังขาด: " + string.Join(" · ", missing));
    }
}
