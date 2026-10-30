using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ใบแจ้งหนี้ค่าบริการ NextAcc หนึ่งใบ (มุมผู้จ่าย) — ผลของ <see cref="PlatformInvoiceQueries"/></summary>
public sealed record PlatformInvoiceRow(
    Guid Id, Guid BuyerCompanyId, string DocumentNumber, string? Reference, DocumentStatus Status,
    DateTime DocumentDate, DateTime? DueDate, decimal TotalAmount, decimal BalanceDue);

/// <summary>
/// รอบ 219 เฟส 2 (คำตัดสินข้อ 180) — <b>ตัวหาใบแจ้งหนี้ค่าบริการของบริษัทผู้เช่าตัวเดียว</b>: เอกสารชนิดใบแจ้งหนี้ใน tenant ผู้ให้บริการ
/// (<c>SiteSettings.PlatformCompanyId</c>) ที่ผู้ติดต่อผูกกับบริษัทผู้เช่า (<c>ExternalSystem = NextAccTenant</c> · <c>ExternalId = CompanyId</c> —
/// กุญแจที่ <c>PlatformBillingDocumentIssuer</c> ประทับ) · ใช้ร่วม: ศูนย์บิล · แจ้งชำระใบ · dunning · หน้าแอดมิน · ลิงก์ชำระ
/// <para>tenant isolation: ทุก query กรอง <c>CompanyId == platformId</c> (เอกสาร + ผู้ติดต่อ) และผู้ติดต่อของบริษัทผู้เช่ารายนั้นเท่านั้น ⇒
/// บริษัท A ไม่มีทางเห็น/จ่ายใบของบริษัท B (ใบที่ไม่ใช่ของตน = "ไม่พบ" เหมือนไม่มี)</para>
/// </summary>
public static class PlatformInvoiceQueries
{
    /// <summary>บริษัทผู้ให้บริการที่ตั้งไว้และยังอยู่ — null = ยังไม่ตั้ง (โหมด PDF เดี่ยว ไม่มีใบแจ้งหนี้จริงให้จ่าย)</summary>
    public static async Task<Guid?> PlatformTenantIdAsync(AccountingDbContext db, CancellationToken ct = default)
    {
        var id = await db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt)
            .Select(s => s.PlatformCompanyId).FirstOrDefaultAsync(ct);
        if (id == null) return null;
        var alive = await db.Companies.AsNoTracking().AnyAsync(c => c.Id == id.Value && !c.IsDeleted, ct);
        return alive ? id : null;
    }

    /// <summary>ใบแจ้งหนี้ (ออกแล้ว) ของบริษัทผู้เช่ารายเดียว — คืน<b>เอกสาร</b> (ผู้เรียกกรอง/เรียงด้วยคอลัมน์จริงก่อน แล้วค่อย <see cref="AsRows"/> เป็นขั้นสุดท้าย:
    /// EF แปล Where/Contains ที่ต่อหลังการ project เข้า constructor ของ record ไม่ได้ — CI Db รอบ 219 เฟส 2 ล้ม 6 เทสต์)</summary>
    public static IQueryable<Document> ForBuyer(AccountingDbContext db, Guid platformId, Guid buyerCompanyId)
    {
        var key = buyerCompanyId.ToString();
        var contactIds = db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == platformId && c.ExternalSystem == PlatformInvoiceRules.TenantContactSystem && c.ExternalId == key)
            .Select(c => c.Id);
        var issued = PlatformInvoiceRules.IssuedStatuses;
        return db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == platformId && !d.IsDeleted && d.DocumentType == DocumentType.Invoice
                && issued.Contains(d.Status) && contactIds.Contains(d.ContactId));
    }

    /// <summary>project เป็นแถวมุมผู้จ่าย — ใช้เป็น<b>ขั้นสุดท้าย</b>ของ query เท่านั้น (ห้ามต่อ Where/OrderBy/Contains หลังจากนี้)</summary>
    public static IQueryable<PlatformInvoiceRow> AsRows(this IQueryable<Document> q, Guid buyerCompanyId)
        => q.Select(d => new PlatformInvoiceRow(d.Id, buyerCompanyId, d.DocumentNumber, d.Reference, d.Status,
            d.DocumentDate, d.DueDate, d.TotalAmount, d.BalanceDue));

    /// <summary>ใบที่ยังค้างของทุกบริษัทผู้เช่า (dunning · หน้าแอดมิน) — ผู้ติดต่อที่ไม่ได้ผูกกับบริษัทผู้เช่า (ลูกค้าทั่วไปของ NextAcc) ไม่อยู่ในชุดนี้</summary>
    public static async Task<List<PlatformInvoiceRow>> OpenForAllBuyersAsync(AccountingDbContext db, Guid platformId,
        int take, CancellationToken ct = default)
    {
        var open = PlatformInvoiceRules.OpenStatuses;
        var rows = await (from d in db.Documents.AsNoTracking()
                          join c in db.Contacts.AsNoTracking() on d.ContactId equals c.Id
                          where d.CompanyId == platformId && !d.IsDeleted && d.DocumentType == DocumentType.Invoice
                                && open.Contains(d.Status) && d.BalanceDue > PlatformInvoiceRules.Epsilon
                                && c.CompanyId == platformId && c.ExternalSystem == PlatformInvoiceRules.TenantContactSystem
                                && c.ExternalId != null
                          orderby d.DueDate
                          select new { d.Id, c.ExternalId, d.DocumentNumber, d.Reference, d.Status, d.DocumentDate, d.DueDate, d.TotalAmount, d.BalanceDue })
            .Take(take).ToListAsync(ct);
        return rows
            .Where(r => Guid.TryParse(r.ExternalId, out _))
            .Select(r => new PlatformInvoiceRow(r.Id, Guid.Parse(r.ExternalId!), r.DocumentNumber, r.Reference, r.Status,
                r.DocumentDate, r.DueDate, r.TotalAmount, r.BalanceDue))
            .ToList();
    }

    /// <summary>ใบเดียวตาม id (ไม่รู้ผู้จ่ายล่วงหน้า — ลิงก์ชำระ/แอดมิน) · null = ไม่ใช่ใบแจ้งหนี้ค่าบริการของบริษัทผู้เช่าใด</summary>
    public static async Task<PlatformInvoiceRow?> ByIdAsync(AccountingDbContext db, Guid platformId, Guid documentId, CancellationToken ct = default)
    {
        var issued = PlatformInvoiceRules.IssuedStatuses;
        var r = await (from d in db.Documents.AsNoTracking()
                       join c in db.Contacts.AsNoTracking() on d.ContactId equals c.Id
                       where d.Id == documentId && d.CompanyId == platformId && !d.IsDeleted && d.DocumentType == DocumentType.Invoice
                             && issued.Contains(d.Status)
                             && c.CompanyId == platformId && c.ExternalSystem == PlatformInvoiceRules.TenantContactSystem
                       select new { d.Id, c.ExternalId, d.DocumentNumber, d.Reference, d.Status, d.DocumentDate, d.DueDate, d.TotalAmount, d.BalanceDue })
            .FirstOrDefaultAsync(ct);
        if (r == null || !Guid.TryParse(r.ExternalId, out var buyer)) return null;
        return new PlatformInvoiceRow(r.Id, buyer, r.DocumentNumber, r.Reference, r.Status, r.DocumentDate, r.DueDate, r.TotalAmount, r.BalanceDue);
    }

    /// <summary>บรรทัดย่อของใบ (คำอธิบาย + ยอด) — หน้าลิงก์ชำระ/ศูนย์บิลแสดง "จ่ายค่าอะไร" โดยไม่เปิดเผยข้อมูลอื่นของใบ</summary>
    public static async Task<List<(string Description, decimal Amount)>> LineSummaryAsync(AccountingDbContext db, Guid platformId,
        Guid documentId, int take, CancellationToken ct = default)
    {
        var lines = await db.DocumentLines.AsNoTracking()
            .Where(l => l.DocumentId == documentId && l.Document.CompanyId == platformId)
            .OrderBy(l => l.LineOrder)
            .Select(l => new { l.Description, l.Amount })
            .Take(take).ToListAsync(ct);
        return lines.Select(l => (l.Description, l.Amount)).ToList();
    }
}
