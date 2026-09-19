namespace Accounting.Helpers;

/// <summary>คำตัดสินของ <see cref="DocumentLineKind.Judge"/> — "บรรทัดนี้เขียนลงเอกสารได้ไหม"
/// <para><c>Reason</c> เป็นภาษาไทยที่เอาไปโชว์ผู้ใช้/ลง audit ได้ตรง ๆ และ**บอกทางไปต่อ**
/// (DECISION_DOCTRINE §1 G4/G6)</para></summary>
public readonly record struct DocumentLineSignVerdict(bool Ok, string? Reason);

/// <summary>
/// **ตัวตั้งตัวเดียวของกติกา "บรรทัดเอกสารติดลบได้ไหม" + "แล้วส่วนลดระดับบิลไปอยู่ตรงไหน"**
///
/// ═══ ที่มา (รอบ 184 · P0) ═══
/// ระบบเดียวเคยมีกติกาสองชุดที่ขัดกันเรื่องเดียวกัน:
/// <list type="bullet">
/// <item><c>DocumentService.ValidateDocumentLinesAsync</c> ห้าม <c>UnitPrice &lt; 0</c>
///   ⇒ <c>CmsCommerceService.BuildOrderErpLines</c> ที่ส่งบรรทัด "ส่วนลด" เป็น
///   <c>UnitPrice = -DiscountAmount</c> **โยนทุกครั้ง** ⇒ ออเดอร์หน้าร้านทุกใบที่มีส่วนลด
///   จ่ายเงินแล้วแต่ไม่เคยมีเอกสาร/JE/ลูกหนี้/ภาษีขาย</item>
/// <item><c>Helpers/PosTaxInvoiceLines</c> สร้างบรรทัดติดลบแล้วประกอบ <c>Document</c> เอง
///   ⇒ **เลี่ยง validator ทั้งหมด** ⇒ POS ย้ายเข้าเส้นเอกสารกลางไม่ได้เลย</item>
/// </list>
///
/// ═══ คำตัดสิน: **ห้ามบรรทัดติดลบ** ═══
/// เหตุผลที่ชี้ขาด (หลักฐานจากโค้ด ไม่ใช่ความเห็น):
/// <list type="number">
/// <item><b>§86/4(5)</b> บังคับทุกบรรทัดมี "ชนิด ประเภท ปริมาณ และ<b>มูลค่า</b>ของสินค้า/บริการ"
///   — บรรทัดติดลบไม่ใช่สินค้าที่ขาย จึงไม่มี "ปริมาณ × ราคาต่อหน่วย" ที่เป็นจริงได้</item>
/// <item><b>e-Tax XML มีที่ของส่วนลดอยู่แล้ว</b> — <c>EtaxInvoiceService.BuildLineItem</c>
///   ปล่อย <c>SpecifiedTradeAllowanceCharge/ChargeIndicator=false</c> + <c>ActualAmount</c>
///   จาก <c>line.DiscountAmount</c> และหัวเอกสารตรึง <c>AllowanceTotalAmount = "0.00"</c>
///   พร้อมคอมเมนต์ว่า "ส่วนลดเป็น line-level" ⇒ บรรทัดติดลบคือ<b>ช่องทางที่สอง</b>ของสิ่งเดียวกัน
///   ที่จะส่ง <c>ChargeAmount</c>/<c>BasisAmount</c>/<c>NetLineTotalAmount</c> ติดลบออกไป</item>
/// <item><b>JE</b> — <c>AutoPostToJournalAsync</c> ลงขารายได้ด้วย
///   <c>AddLine(revenue, 0, docLine.Amount)</c> ⇒ <c>Amount</c> ติดลบ = **เครดิตติดลบ**
///   (contra ที่ไม่มีใครประกาศ) ในสมุดรายวันที่เป็น append-only</item>
/// <item><b>ระบบมีทางที่ถูกอยู่แล้ว</b> — <c>DocumentLineRequest.DiscountAmount</c> (รายบรรทัด)
///   และ <c>CreateDocumentRequest.BillDiscountAmount</c> (ท้ายบิล เฉลี่ย pro-rata ผ่าน
///   <c>AllocateBillDiscount</c>) ซึ่ง **ลดฐาน VAT จริงตาม §79** และ renderer พิมพ์ให้เห็น
///   (คอลัมน์ "ส่วนลด" ต่อบรรทัด + แถว "รวมส่วนลด"/"ส่วนลดท้ายบิล")</item>
/// <item><b>เรพประกาศกติกานี้ไว้แล้ว</b> — <c>CreateDocumentRequest.DepositAppliedAmount</c>
///   เขียนไว้ตรง ๆ ว่า "line ยังเป็นการขายเต็มจำนวน (ห้าม line ติดลบ)" ⇒ ของหักระดับบิล
///   เป็น**ช่องหัวเอกสาร + แถวสรุป** ไม่ใช่บรรทัด</item>
/// </list>
/// ⇒ ของที่ "ลดยอด" ทุกชนิด (ส่วนลด · คูปอง · ปัดเศษลง) ไม่เป็นบรรทัดของตัวเอง แต่ถูก
/// **เฉลี่ยลงบรรทัดที่มีอยู่** ด้วย <see cref="AllocateDeduction"/> แล้วแสดงเป็นคอลัมน์
/// ส่วนลด/แถวสรุป — ทิศนี้ผ่านเกณฑ์ G5 เพราะความเสียหายที่เหลือคือ "ตัวเลขบนกระดาษ"
/// ที่ผู้ใช้เห็นทันที ไม่ใช่ "เอกสารหายทั้งใบ" ที่เงียบสนิทแบบของเดิม
///
/// <para><b>การคืนของ/ลดราคาหลังออกใบแล้ว</b> ไม่ใช่เรื่องของบรรทัด — ต้องออก
/// **ใบลดหนี้ (§86/10)** ซึ่งเป็นเอกสารคนละใบที่มียอดเป็นบวกของตัวเอง</para>
/// </summary>
public static class DocumentLineKind
{
    /// <summary>คำตอบเดียวของทั้งระบบ — อ่านได้ทั้ง validator, CMS, POS, เทสต์</summary>
    public const bool NegativeLineAllowed = false;

    /// <summary>รหัสกฎสำหรับ audit/tooltip (กฎเหล็ก #2 M — legal reference logging)</summary>
    public const string SignRuleCode = "RD-86/4-LINE-SIGN";

    /// <summary>ข้อความเดียวที่ทุกด่านใช้ — บอก**เหตุผล**และ**ทางไปต่อ** ไม่ใช่แค่ "ไม่ผ่าน"</summary>
    public const string NegativeUnitPriceReason =
        "ราคาต่อหน่วยต้องไม่ติดลบ — ใบกำกับภาษี (ป.รัษฎากร มาตรา 86/4(5)) บังคับให้ทุกบรรทัดเป็น "
        + "สินค้า/บริการที่ขายจริง บรรทัดติดลบจึงไม่ใช่รายการที่ขาย "
        + "· ถ้าต้องการ “หักส่วนลด” ให้กรอกเป็นส่วนลดของบรรทัดนั้น (ช่อง “ส่วนลด”) "
        + "หรือส่วนลดท้ายบิล ซึ่งระบบจะเฉลี่ยลงบรรทัดและลดฐานภาษีให้ถูกต้องตามมาตรา 79 "
        + "· ถ้าเป็นการคืนของ/ลดราคาหลังออกใบไปแล้ว ต้องออก “ใบลดหนี้” (มาตรา 86/10) ไม่ใช่บรรทัดติดลบ";

    public const string NonPositiveQuantityReason =
        "จำนวนสินค้าต้องมากกว่า 0 — มาตรา 86/4(5) บังคับให้ระบุปริมาณของสินค้า/บริการที่ขายจริง "
        + "· ถ้าต้องการหักยอด ให้ใช้ช่องส่วนลด (รายบรรทัด/ท้ายบิล) หรือออกใบลดหนี้";

    public const string NegativeDiscountReason =
        "ส่วนลดต่อบรรทัดต้องไม่ติดลบ — ส่วนลดติดลบคือการ “บวกเพิ่ม” ที่ไม่มีที่อยู่บนใบกำกับ "
        + "· ถ้าต้องการเรียกเก็บเพิ่ม ให้เพิ่มเป็นบรรทัดของตัวเอง หรือออกใบเพิ่มหนี้ (มาตรา 86/9)";

    /// <summary>ด่านเดียวของ "เครื่องหมาย" บนบรรทัดเอกสาร — pure, ไม่ throw, ไม่มี I/O
    /// (DECISION_DOCTRINE §1 G6). ผู้เรียกเป็นคนตัดสินใจว่าจะ throw หรือเตือน</summary>
    /// <param name="quantity">ปริมาณบนบรรทัด</param>
    /// <param name="unitPrice">ราคาต่อหน่วย (จะรวม VAT หรือไม่ก็ได้ — เครื่องหมายเป็นเรื่องเดียวกัน)</param>
    /// <param name="discountAmount">ส่วนลดรายบรรทัดเป็นยอดเงิน (0 = ไม่ระบุ)</param>
    public static DocumentLineSignVerdict Judge(decimal quantity, decimal unitPrice, decimal discountAmount)
    {
        if (quantity <= 0m) return new DocumentLineSignVerdict(false, NonPositiveQuantityReason);
        if (unitPrice < 0m) return new DocumentLineSignVerdict(false, NegativeUnitPriceReason);
        if (discountAmount < 0m) return new DocumentLineSignVerdict(false, NegativeDiscountReason);
        return new DocumentLineSignVerdict(true, null);
    }

    /// <summary>
    /// **เฉลี่ย "ยอดหักระดับบิล" (ส่วนลด · คูปอง · ปัดเศษลง) ลงบรรทัดที่มีอยู่แบบ pro-rata**
    /// — ตัวเดียวที่ทั้ง CMS และ POS ใช้ แทนการสร้างบรรทัดติดลบคนละแบบ
    ///
    /// <para>รับประกัน (เทสต์ล็อกไว้ทั้งสามข้อ):</para>
    /// <list type="number">
    /// <item><c>Σ ผลลัพธ์ == min(deduction, Σ lineGross ที่เป็นบวก)</c> **เป๊ะ**
    ///   — เศษปัดไปอยู่บรรทัดที่ยอดใหญ่สุด (ปัดรายบรรทัดอิสระจะขาด/เกิน 1-2 สตางค์เสมอ
    ///   ⇒ ยอดเอกสาร ≠ เงินที่ลูกค้าจ่าย ⇒ ลูกหนี้ค้างเศษถาวร)
    ///   · <b>ห้ามปัด <paramref name="deduction"/> เป็น 2 ตำแหน่งก่อน</b> — ยอดหักต้นทาง
    ///   ไม่ได้ปัดเสมอไป (POS: <c>SubTotal × DiscountPercent / 100</c> ได้ทศนิยม 5 ตำแหน่ง)
    ///   ปัดที่นี่แล้วผลรวมจะคลาดจากยอดที่ลูกค้าจ่ายไปเศษสตางค์ ซึ่งเป็นตัวที่ด่าน
    ///   "ยอดไม่ลงตัว" จับไม่ได้ (ต่ำกว่า 0.005) แต่ไปโผล่เป็นลูกหนี้ค้างแทน</item>
    /// <item><c>0 ≤ ผลลัพธ์[i] ≤ lineGross[i]</c> ทุกบรรทัด — ไม่มีบรรทัดไหนถูกหักจนติดลบ
    ///   (ส่วนที่ล้นถูกเกลี่ยไปบรรทัดที่ยังรับได้)</item>
    /// <item>บรรทัดที่ <c>lineGross ≤ 0</c> ไม่ถูกหัก (ไม่มีฐานให้หัก)</item>
    /// </list>
    ///
    /// <para><b>เฉลี่ยตามยอด "รวมภาษี" หรือ "ก่อนภาษี" ก็ได้ — แต่ต้องเป็นฐานเดียวกันทั้งอาเรย์</b>
    /// ผู้เรียกเป็นคนเลือก: CMS ส่งยอด gross (ราคารวม VAT) เพราะ <c>ComputeLineAmounts</c>
    /// หักส่วนลดออกจาก gross ก่อนแยก VAT ⇒ ยอดรวมเอกสารลดลงเท่ายอดหักเป๊ะทุกกรณี
    /// แม้บรรทัดคนละอัตรา VAT</para>
    /// </summary>
    public static decimal[] AllocateDeduction(IReadOnlyList<decimal> lineGross, decimal deduction)
    {
        const MidpointRounding R = MidpointRounding.AwayFromZero;
        var alloc = new decimal[lineGross.Count];
        if (lineGross.Count == 0 || deduction <= 0m) return alloc;

        decimal total = 0m;
        for (var i = 0; i < lineGross.Count; i++)
            if (lineGross[i] > 0m) total += lineGross[i];
        if (total <= 0m) return alloc;

        var target = Math.Min(deduction, total);
        if (target <= 0m) return alloc;

        decimal running = 0m;
        var biggest = -1;
        decimal biggestGross = -1m;
        for (var i = 0; i < lineGross.Count; i++)
        {
            if (lineGross[i] <= 0m) continue;
            alloc[i] = Math.Round(target * lineGross[i] / total, 2, R);
            running += alloc[i];
            if (lineGross[i] > biggestGross) { biggestGross = lineGross[i]; biggest = i; }
        }
        if (biggest >= 0 && running != target) alloc[biggest] += target - running;

        // เศษที่ยัดบรรทัดใหญ่สุดอาจ "ล้น" ยอดบรรทัดนั้น (บิลที่ส่วนลดเกือบเท่ายอดทั้งใบ)
        // → เกลี่ยส่วนล้นไปบรรทัดที่ยังรับได้ · target ≤ total เสมอ ⇒ เกลี่ยลงได้หมดแน่นอน
        for (var guard = 0; guard < lineGross.Count; guard++)
        {
            var over = -1;
            for (var i = 0; i < lineGross.Count; i++)
                if (alloc[i] > lineGross[i]) { over = i; break; }
            if (over < 0) break;
            var overflow = alloc[over] - lineGross[over];
            alloc[over] = lineGross[over];
            for (var i = 0; i < lineGross.Count && overflow > 0m; i++)
            {
                if (i == over || lineGross[i] <= 0m) continue;
                var capacity = lineGross[i] - alloc[i];
                if (capacity <= 0m) continue;
                var take = Math.Min(capacity, overflow);
                alloc[i] += take;
                overflow -= take;
            }
        }
        return alloc;
    }
}
