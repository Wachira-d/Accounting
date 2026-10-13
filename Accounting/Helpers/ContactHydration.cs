using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// แก้บั๊กระบบ: `Document.Contact` เป็น required navigation (ContactId
/// non-nullable) และ `Contact` มี global query filter `!IsDeleted`. เมื่อ query
/// ใช้ `.Include(d => d.Contact)` EF Core แปลงเป็น **INNER JOIN + !IsDeleted** →
/// เอกสารที่ contact ถูก soft-delete/ปิด จะถูก "ตัดทิ้งเงียบทั้งใบ" (list หาย /
/// single-fetch ได้ null) → รายงานภาษี/aging/ฯลฯ under-report.
///
/// วิธีใช้: **แทน** `.Include(d => d.Contact)` ด้วยการ query ปกติ (ไม่ Include
/// Contact) → materialize → เรียก `db.HydrateContactsAsync(companyId, docs)` เพื่อ
/// โหลด Contact (รวมที่ถูกลบ) ผูกกลับเข้า navigation. โค้ด `doc.Contact?.Name`
/// เดิมทั้งหมดใช้ได้ต่อทันที โดยไม่ทำให้เอกสารหาย.
/// </summary>
public static class ContactHydration
{
    /// <summary>ผูก Contact (รวมที่ถูก soft-delete) กลับเข้า <see cref="Document.Contact"/>
    /// ของเอกสารที่ query มาแบบไม่ Include Contact.</summary>
    public static async Task HydrateContactsAsync(
        this AccountingDbContext db, Guid companyId, IReadOnlyCollection<Document> docs)
    {
        if (docs is null || docs.Count == 0) return;
        var ids = docs
            .Where(d => d.Contact is null && d.ContactId != Guid.Empty)
            .Select(d => d.ContactId).Distinct().ToList();
        if (ids.Count == 0) return;

        var map = await db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);

        foreach (var d in docs)
            if (d.Contact is null && map.TryGetValue(d.ContactId, out var c))
                d.Contact = c;
    }

    /// <summary>ผูก Contact ให้เอกสารเดี่ยว (single-fetch) — คืนเอกสารเดิมเพื่อ chain สะดวก.</summary>
    public static async Task<Document?> HydrateContactAsync(
        this AccountingDbContext db, Guid companyId, Document? doc)
    {
        if (doc is not null) await db.HydrateContactsAsync(companyId, new[] { doc });
        return doc;
    }

    /// <summary>ผูก Contact ให้ `Payment.Document.Contact` (เคส Payment→Document→Contact).</summary>
    public static async Task HydratePaymentContactsAsync(
        this AccountingDbContext db, Guid companyId, IReadOnlyCollection<Payment> payments)
    {
        if (payments is null || payments.Count == 0) return;
        var docs = payments.Where(p => p.Document is not null).Select(p => p.Document!).ToList();
        await db.HydrateContactsAsync(companyId, docs);
    }

    /// <summary>โหลดผู้ถูกหักภาษี (<see cref="Document.WhtPayeeContactId"/>) ของเอกสารที่ตั้งผู้ถูกหักแยกจากผู้ติดต่อ — รวมที่ถูก soft-delete ·
    /// ใช้คู่กับ <see cref="WhtPayeeOf"/> (ใบที่ไม่ได้ตั้ง = ผู้ติดต่อของเอกสาร) · ทุกจุดที่แบ่ง ภ.ง.ด.3/53/54 จากเอกสารต้องอ่านผู้ถูกหัก ไม่ใช่ผู้รับเงิน</summary>
    public static async Task<IReadOnlyDictionary<Guid, Contact>> LoadWhtPayeeContactsAsync(
        this AccountingDbContext db, Guid companyId, IEnumerable<Document> docs)
    {
        var docList = docs as IReadOnlyCollection<Document> ?? docs.ToList();
        var ids = docList
            .Where(d => d.WhtPayeeContactId is Guid p && p != Guid.Empty && p != d.ContactId)
            .Select(d => d.WhtPayeeContactId!.Value)
            // รอบ 204 (ข้อ 143): ผู้ถูกหักรายบรรทัด (เฉพาะบรรทัดที่โหลดมากับเอกสาร) — ผู้เรียกที่แบ่งแบบรายบรรทัดหาได้จากแผนที่เดียวกัน
            .Concat(docList.SelectMany(d => d.Lines)
                .Where(l => !l.IsDeleted && l.WhtPayeeContactId is Guid lp && lp != Guid.Empty)
                .Select(l => l.WhtPayeeContactId!.Value))
            .Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, Contact>();
        return await db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);
    }

    /// <summary>ผู้ถูกหักภาษีของเอกสาร (ต้อง hydrate <see cref="Document.Contact"/> และโหลดแผนที่จาก <see cref="LoadWhtPayeeContactsAsync"/> แล้ว) ·
    /// ตั้งผู้ถูกหักไว้แต่หาไม่เจอ ⇒ null (ไม่ตกกลับไปผู้รับเงินเงียบ ๆ — จะได้แบบ ภ.ง.ด. ผิดคน)</summary>
    public static Contact? WhtPayeeOf(Document doc, IReadOnlyDictionary<Guid, Contact> payees)
    {
        // ฝ่ายค้าน P1 (รอบ 204): ทุกบรรทัดที่หักเป็นของผู้ถูกหักรายบรรทัดรายเดียว ⇒ ผู้ถูกหักของใบคือรายนั้น (ตรงกับ JE · WhtPayeeSplit.EffectiveSinglePayee)
        var id = WhtPayeeSplit.EffectiveSinglePayee(doc);
        if (id == doc.ContactId) return doc.Contact;
        return payees.TryGetValue(id, out var c) ? c : null;
    }

    /// <summary>รอบ 204 (ข้อ 143): ผู้ถูกหักของ<b>บรรทัด</b> (ค่าบนบรรทัดชนะ · ว่าง = ผู้ถูกหักของหัวเอกสาร) — แผนที่จาก <see cref="LoadWhtPayeeContactsAsync"/> ·
    /// ตั้งไว้แต่หาไม่เจอ ⇒ null (ไม่ตกกลับไปผู้รับเงินเงียบ ๆ)</summary>
    public static Contact? WhtLinePayeeOf(Document doc, DocumentLine line, IReadOnlyDictionary<Guid, Contact> payees)
    {
        var id = WhtPayeeSplit.LinePayeeOf(doc.ContactId, doc.WhtPayeeContactId, line.WhtPayeeContactId);
        if (id == doc.ContactId) return doc.Contact;
        return payees.TryGetValue(id, out var c) ? c : null;
    }

    /// <summary>ผู้ถูกหักภาษีของเอกสารใบเดียว — ไม่ได้ตั้งแยก = <see cref="Document.Contact"/> (ต้อง hydrate ก่อน)</summary>
    public static async Task<Contact?> LoadWhtPayeeAsync(this AccountingDbContext db, Guid companyId, Document doc)
    {
        // ฝ่ายค้าน P1 (รอบ 204): ผู้ถูกหัก "ที่มีผลจริง" ของใบรายเดียว (บรรทัดที่โหลดมา) — ไม่มีบรรทัด = หัวเอกสาร (ข้อมูลที่บันทึกถูกยกขึ้นหัวเอกสารแล้ว)
        var id = WhtPayeeSplit.EffectiveSinglePayee(doc);
        if (id == doc.ContactId) return doc.Contact;
        return await db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == id);
    }

    /// <summary>ฝ่ายค้าน P3-6 (รอบ 204): ผู้ติดต่อที่ยังใช้งาน (ไม่ถูกลบ) ของบริษัทนี้จากชุด id — ตัวกรองตอนสืบทอดผู้ถูกหักรายบรรทัด
    /// (<see cref="WhtPayeeSplit.InheritPayee"/>) · เกณฑ์เดียวกับตัวตรวจตอนสร้าง (query filter ของ Contact)</summary>
    public static async Task<HashSet<Guid>> ActiveContactIdsAsync(this AccountingDbContext db, Guid companyId, IEnumerable<Guid?> ids)
    {
        var wanted = ids.Where(i => i is Guid g && g != Guid.Empty).Select(i => i!.Value).Distinct().ToList();
        if (wanted.Count == 0) return new HashSet<Guid>();
        var found = await db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && wanted.Contains(c.Id))
            .Select(c => c.Id).ToListAsync();
        return found.ToHashSet();
    }

    /// <summary>ผูก PayeeContact (รวมที่ถูก soft-delete) เข้า
    /// <see cref="WithholdingTaxCert.PayeeContact"/> — required nav บน Contact ที่มี
    /// filter !IsDeleted เช่นกัน → .Include(w => w.PayeeContact) จะ INNER JOIN ตัด
    /// หนังสือรับรอง 50 ทวิ ที่ payee ถูกลบทิ้ง (under-report ภ.ง.ด.1ก/3ก).</summary>
    public static async Task HydratePayeeContactsAsync(
        this AccountingDbContext db, Guid companyId, IReadOnlyCollection<WithholdingTaxCert> certs)
    {
        if (certs is null || certs.Count == 0) return;
        var ids = certs
            .Where(w => w.PayeeContact is null && w.PayeeContactId != Guid.Empty)
            .Select(w => w.PayeeContactId).Distinct().ToList();
        if (ids.Count == 0) return;

        var map = await db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);

        foreach (var w in certs)
            if (w.PayeeContact is null && map.TryGetValue(w.PayeeContactId, out var c))
                w.PayeeContact = c;
    }

    /// <summary>ผูก PayeeContact ให้ 50 ทวิ ใบเดียว (single-fetch).</summary>
    public static async Task<WithholdingTaxCert?> HydratePayeeContactAsync(
        this AccountingDbContext db, Guid companyId, WithholdingTaxCert? cert)
    {
        if (cert is not null) await db.HydratePayeeContactsAsync(companyId, new[] { cert });
        return cert;
    }
}
