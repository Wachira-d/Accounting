using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รหัสประเภทเงินได้ของ<b>บรรทัดที่หักภาษีจริง</b> — ข้อมูลที่ตัวเลือกแบบ ภ.ง.ด. (<see cref="WhtPayeeKind.ResolveForm"/>) ใช้แยก ภ.ง.ด.2 ออกจาก ภ.ง.ด.3
/// (รอบ 204 · คำตัดสินเจ้าของ DECISIONS ข้อ 148)
///
/// <para><b>ทำไมต้องมีตัวกลาง</b>: ทุกทางที่ตัดสินแบบ/ผังค้างจ่าย (JE ตอนอนุมัติ · JE รายงวด · 50 ทวิ อัตโนมัติ · รายงาน · หน้านำส่ง · ปฏิทิน)
/// ต้องได้ "ชุดรหัส" เดียวกัน — ถ้าทางหนึ่งนับบรรทัด WHT 0 เข้าไปด้วยแล้วอีกทางไม่นับ ใบเดียวจะเป็น ภ.ง.ด.2 ที่ GL แต่ ภ.ง.ด.3 ที่ทะเบียน
/// (defect class "สอง renderer ห้าม drift") · เกณฑ์เดียว: <c>WithholdingTaxAmount &gt; 0</c> (ตรงกับบรรทัดที่ 50 ทวิ อัตโนมัติหยิบไปออกใบ)</para>
/// </summary>
public static class WhtIncomeCodes
{
    /// <summary>รหัสของบรรทัดเอกสารที่หักภาษี (pure)</summary>
    public static IReadOnlyList<string?> Withheld(IEnumerable<DocumentLine>? lines)
        => (lines ?? Enumerable.Empty<DocumentLine>())
            .Where(l => !l.IsDeleted && l.WithholdingTaxAmount > 0m)
            .Select(l => l.IncomeTypeCode)
            .ToList();

    /// <summary>รหัสของบรรทัดที่หักของ<b>ผู้ถูกหักรายหนึ่ง</b> บนใบหลายผู้ถูกหัก (รอบ 204 ข้อ 143 × ภ.ง.ด.2) — <paramref name="lineIndexes"/> =
    /// <see cref="WhtPayeePortion.LineIndexes"/> ที่ชี้เข้า <paramref name="orderedLines"/> (รายการเดียวกับที่ส่งเข้า <see cref="WhtPayeeSplit.Portions"/>) ·
    /// เกณฑ์เดียวกับ <see cref="Withheld(IEnumerable{DocumentLine}?)"/> ⇒ ใบรายเดียว (ทุกบรรทัดที่หักเป็นของรายเดียว) ได้ชุดรหัสเท่ากับทั้งใบ</summary>
    public static IReadOnlyList<string?> WithheldAt(IReadOnlyList<DocumentLine> orderedLines, IReadOnlyList<int> lineIndexes)
        => Withheld(lineIndexes.Where(i => i >= 0 && i < orderedLines.Count).Select(i => orderedLines[i]));

    /// <summary>รหัสของบรรทัดที่หักซึ่งเป็นของผู้ถูกหัก <paramref name="payeeContactId"/> (ผู้ถูกหักของบรรทัดตาม <see cref="WhtPayeeSplit.LinePayeeOf"/>) —
    /// ผู้เรียกที่ถือเอกสารทั้งใบแต่ไม่ได้ผ่าน <see cref="WhtPayeeSplit.Portions"/> (รายงาน ภ.ง.ด. · 50 ทวิ ต่อราย) · ใบรายเดียว = ทั้งใบ</summary>
    public static IReadOnlyList<string?> WithheldForPayee(IEnumerable<DocumentLine>? lines, Guid docContactId, Guid? docPayeeOverride,
        Guid payeeContactId)
        => Withheld((lines ?? Enumerable.Empty<DocumentLine>())
            .Where(l => WhtPayeeSplit.LinePayeeOf(docContactId, docPayeeOverride, l.WhtPayeeContactId) == payeeContactId));

    /// <summary>เหมือน <see cref="WithheldAt(IReadOnlyList{DocumentLine}, IReadOnlyList{int})"/> สำหรับผู้เรียกที่ project บรรทัดใน SQL แล้ว —
    /// <paramref name="lineCodes"/> = รหัสเงินได้ของบรรทัด<b>ลำดับเดียวกับที่ส่งเข้า</b> <see cref="WhtPayeeSplit.Portions"/>
    /// (ผู้เรียกกรองบรรทัดที่หัก <c>WithholdingTaxAmount &gt; 0</c> ใน SQL — เกณฑ์เดียวกับ <see cref="Withheld(IEnumerable{DocumentLine}?)"/>)</summary>
    public static IReadOnlyList<string?> At(IReadOnlyList<string?> lineCodes, IReadOnlyList<int> lineIndexes)
        => lineIndexes.Where(i => i >= 0 && i < lineCodes.Count).Select(i => lineCodes[i]).ToList();

    /// <summary>รหัสของบรรทัดหนังสือรับรอง 50 ทวิ ที่มีภาษี (pure)</summary>
    public static IReadOnlyList<string?> Withheld(IEnumerable<WithholdingTaxCertLine>? lines)
        => (lines ?? Enumerable.Empty<WithholdingTaxCertLine>())
            .Where(l => !l.IsDeleted && l.TaxAmount > 0m)
            .Select(l => (string?)l.IncomeTypeCode)
            .ToList();

    /// <summary>
    /// รหัสของบรรทัดที่หักภาษีของเอกสารใบหนึ่ง — ใช้บรรทัดในหน่วยความจำเมื่อโหลดไว้แล้ว (ทางอนุมัติ — บรรทัดอาจเพิ่งแก้ในคำขอเดียวกัน) ·
    /// ไม่ได้โหลด (ทางบันทึกจ่ายที่ล็อกแถวด้วย <c>FOR UPDATE</c> ไม่ Include บรรทัด) ⇒ อ่านจากฐาน <b>กรองบริษัท</b>
    /// <para>⚠️ ห้ามอ่าน <c>doc.Lines</c> ตรง ๆ ในทางที่ไม่ได้ Include — คอลเลกชันว่าง = "ไม่รู้ประเภท" ⇒ ใบดอกเบี้ยตกกลับ 21916 เงียบ ๆ
    /// ขณะที่ JE ตอนอนุมัติลง 21915 (หนี้คนละผังของใบเดียว)</para>
    /// </summary>
    public static async Task<IReadOnlyList<string?>> LoadWithheldAsync(this AccountingDbContext db, Guid companyId, Document doc)
    {
        if (db.Entry(doc).Collection(d => d.Lines).IsLoaded || doc.Lines.Count > 0)
            return Withheld(doc.Lines);
        return await db.DocumentLines.AsNoTracking()
            .Where(l => l.DocumentId == doc.Id && l.Document.CompanyId == companyId
                && !l.IsDeleted && l.WithholdingTaxAmount > 0m)
            .Select(l => l.IncomeTypeCode)
            .ToListAsync();
    }
}
