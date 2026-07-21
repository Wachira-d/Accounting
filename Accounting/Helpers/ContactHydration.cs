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
