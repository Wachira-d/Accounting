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
