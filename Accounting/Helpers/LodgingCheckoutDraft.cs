using System.Linq.Expressions;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 194 R3 (P-1) — "ใบร่างเช็คเอาต์ที่ค้าง" ของการจองที่พัก: เช็คเอาต์สร้างใบสุดท้ายเป็นร่างแล้วอนุมัติล้ม (เช่น "รอสักครู่" ของล็อกยอดมัดจำ ·
/// ด่านอนุมัติ) ⇒ ยังไม่ประทับ <c>FinalDocumentId</c> · เดิมกดเช็คเอาต์ซ้ำ = สร้างใบร่างใหม่ทุกครั้ง (ใบร่างเก่าค้างเป็นขยะ · คนเปิดอนุมัติผิดใบได้)
/// ⇒ กดซ้ำต้อง<b>ใช้ใบร่างเดิม</b> (idempotent) — ตัวกรองตัวเดียวของ "ใบไหนคือใบร่างเช็คเอาต์ของการจองนี้"
/// <para>แคบโดยตั้งใจ: บริษัทนี้ · ไม่ลบ · ร่าง · ไม่ใช่มัดจำ (มัดจำค่าห้อง/เงินประกันเป็น IsDeposit) · ออกจากโมดูลที่พัก · เลขจอง <b>และ</b> เลขอ้างอิง
/// = เลขการจอง (ใบกำกับของการริบมัดจำที่พักอ้างเลขใบมัดจำ ไม่ใช่เลขจอง) · ใบกำกับภาษี/ใบแจ้งหนี้ (ชนิดที่เช็คเอาต์ออก)</para>
/// </summary>
public static class LodgingCheckoutDraft
{
    /// <summary>ใบร่างเช็คเอาต์ที่ค้างของการจอง <paramref name="reservationNumber"/> (รูป expression ให้ EF แปลเป็น SQL · เทสต์ compile แล้วรันกับ entity ตรง ๆ)</summary>
    public static Expression<Func<Document, bool>> LeftoverOf(Guid companyId, string reservationNumber, string originModule)
        => d => d.CompanyId == companyId && !d.IsDeleted && d.Status == DocumentStatus.Draft && !d.IsDeposit
             && d.OriginModule == originModule
             && d.BookingNumber == reservationNumber && d.Reference == reservationNumber
             && (d.DocumentType == DocumentType.TaxInvoice || d.DocumentType == DocumentType.Invoice);

    /// <summary>จากใบร่างที่ค้าง (ใหม่สุดก่อน) — ใบที่ใช้ต่อ = ใบใหม่สุดที่ชนิดตรงกับที่จะออกตอนนี้ · ที่เหลือ (ชนิดไม่ตรง เพราะที่พักเปลี่ยนโหมด VAT
    /// ระหว่างกดสองครั้ง · หรือค้างเกินหนึ่งใบจากรุ่นก่อนแก้) = ลบทิ้ง (ใบร่างไม่มีเลขจริง — ลบแล้วไม่เกิดช่องว่างของเลข §86/4)</summary>
    public static (Guid? Reuse, IReadOnlyList<Guid> Discard) Plan(
        IReadOnlyList<(Guid Id, DocumentType Type)> leftoversNewestFirst, DocumentType typeNow)
    {
        Guid? reuse = leftoversNewestFirst.Where(x => x.Type == typeNow).Select(x => (Guid?)x.Id).FirstOrDefault();
        return (reuse, leftoversNewestFirst.Where(x => x.Id != reuse).Select(x => x.Id).ToList());
    }
}
