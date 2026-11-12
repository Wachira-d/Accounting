using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ใบแจ้งหนี้ค่าบริการ NextAcc หนึ่งใบ (มุมผู้จ่าย) — ผลของ <see cref="PlatformInvoiceQueries"/></summary>
public sealed record PlatformInvoiceRow(
    Guid Id, Guid BuyerCompanyId, string DocumentNumber, string? Reference, DocumentStatus Status,
    DateTime DocumentDate, DateTime? DueDate, decimal TotalAmount, decimal BalanceDue,
    /// <summary>ฝ่ายค้านรอบสุดท้าย 4b: ยอดก่อน VAT ของใบ — ฐานภาษีหัก ณ ที่จ่าย (0 = ไม่รู้ ⇒ ใช้ยอดรวม)</summary>
    decimal SubTotal = 0m);

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

    /// <summary>รอบ 228 (PLATFORM_OMISE_AUDIT §5.7): <b>ใบเสร็จ/ใบกำกับภาษี/ใบลดหนี้</b> ที่บริษัทผู้เช่ารายนี้เป็นผู้ซื้อใน tenant ผู้ให้บริการ (ค่าส่วนเสริมจ่ายล่วงหน้า ·
    /// ใบเสร็จค่าแพ็กเกจ · ใบลดหนี้ของการคืนเงิน) — ผู้ซื้อตรวจด้วยผู้ติดต่อที่ผูก tenant (<c>ExternalId</c>) ชุดเดียวกับ <see cref="ForBuyer"/> ·
    /// ออกเลขแล้วเท่านั้น (ร่าง/ปฏิเสธไม่นับ · ใบที่ยกเลิกยังแสดง — ผู้ซื้อต้องเห็นว่าถูกยกเลิก)</summary>
    public static IQueryable<Document> PaidDocumentsForBuyer(AccountingDbContext db, Guid platformId, Guid buyerCompanyId)
    {
        var key = buyerCompanyId.ToString();
        var contactIds = db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == platformId && c.ExternalSystem == PlatformInvoiceRules.TenantContactSystem && c.ExternalId == key)
            .Select(c => c.Id);
        var issued = PlatformInvoiceRules.IssuedStatuses;
        return db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == platformId && !d.IsDeleted
                && (d.DocumentType == DocumentType.TaxInvoice || d.DocumentType == DocumentType.Receipt || d.DocumentType == DocumentType.CreditNote)
                && issued.Contains(d.Status) && contactIds.Contains(d.ContactId));
    }

    /// <summary>รอบ 262 ฝ่ายค้าน M2 (คำตัดสิน 262-1-11): ผู้ติดต่อ<b>ทุกแถว</b>ของบริษัทผู้เช่าในบริษัทผู้ให้บริการ (กุญแจเดียวกับ <see cref="ForBuyer"/>) —
    /// ผู้เช่าหนึ่งรายมีได้หลายแถวตามออกแบบ (เปลี่ยนเลขภาษี/สาขา ⇒ ตัวออกใบสร้างแถวใหม่ §86/4) · ผู้อ่าน: คิวการ์ดค่าบริการ (รวมปลายทางทุกแถว) · ศูนย์บิล (รายการ/ยกเลิก)</summary>
    public static Task<List<Guid>> BuyerContactIdsAsync(AccountingDbContext db, Guid platformId, Guid buyerCompanyId, CancellationToken ct = default)
    {
        var key = buyerCompanyId.ToString();
        return db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == platformId && !c.IsDeleted && c.ExternalSystem == PlatformInvoiceRules.TenantContactSystem && c.ExternalId == key)
            .OrderByDescending(c => c.CreatedAt).Select(c => c.Id).ToListAsync(ct);
    }

    /// <summary>รอบ 262 ฝ่ายค้าน M2: ผู้ติดต่อที่คำเชิญผูกเองควรลง = ผู้ติดต่อของ<b>ใบล่าสุด</b>ที่ตัวออกใบใช้ (แถวที่ถือเลขภาษี/สาขาปัจจุบัน) · ยังไม่มีใบ ⇒ แถวใหม่สุด ·
    /// ไม่มีแถวเลย (ยังไม่เคยออกใบ) ⇒ null · แถวอื่นของผู้เช่าเดียวกันยังรับการ์ดได้ (คิวรวมทุกแถว)</summary>
    public static async Task<Guid?> BuyerContactIdAsync(AccountingDbContext db, Guid platformId, Guid buyerCompanyId, CancellationToken ct = default)
    {
        var all = await BuyerContactIdsAsync(db, platformId, buyerCompanyId, ct);
        if (all.Count == 0) return null;
        var latest = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == platformId && !d.IsDeleted && all.Contains(d.ContactId))
            .OrderByDescending(d => d.DocumentDate).ThenByDescending(d => d.CreatedAt).Select(d => (Guid?)d.ContactId).FirstOrDefaultAsync(ct);
        return latest ?? all[0];
    }

    /// <summary>รอบ 262: เอกสารนี้เป็นใบของบริษัทผู้เช่า (ผู้ติดต่อผูก tenant) ในบริษัทผู้ให้บริการไหม — ปุ่มชำระของการ์ด LINE ไปศูนย์บิล ไม่ใช่ลิงก์ชำระทั่วไป</summary>
    public static async Task<bool> IsTenantDocumentAsync(AccountingDbContext db, Guid companyId, Guid contactId, CancellationToken ct = default)
        => await TenantBuyerIdAsync(db, companyId, contactId, ct) != null;

    /// <summary>รอบ 262 ฝ่ายค้าน L4: บริษัทผู้ซื้อของใบค่าบริการ (จาก <c>ExternalId</c> ของผู้ติดต่อ) · ไม่ใช่ใบผู้เช่า ⇒ null</summary>
    public static async Task<Guid?> TenantBuyerIdAsync(AccountingDbContext db, Guid companyId, Guid contactId, CancellationToken ct = default)
    {
        if (await PlatformTenantIdAsync(db, ct) is not Guid platformId || platformId != companyId) return null;
        var ext = await db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == contactId && c.CompanyId == platformId && c.ExternalSystem == PlatformInvoiceRules.TenantContactSystem && c.ExternalId != null)
            .Select(c => c.ExternalId).FirstOrDefaultAsync(ct);
        return Guid.TryParse(ext, out var buyer) ? buyer : null;
    }

    /// <summary>รอบ 262 ฝ่ายค้าน M3/L1 (คำตัดสิน 262-1-12/262-1-13): ปลายทางที่ได้การ์ด trigger เดียวกันของใบนี้แล้ว (ยังไม่ตาย · ในหน้าต่างกันซ้ำ) —
    /// <paramref name="fromPlatform"/> true = เฉพาะการ์ดค่าบริการ (กุญแจ <c>ProviderCentralOa.QueueBasePrefix</c>) · false = เฉพาะการ์ดของทางอื่น (กฎตารางส่ง/ส่งเอง) ·
    /// ตัวเทียบเดียวของทั้งสองทาง: การ์ดค่าบริการเลี่ยงการ์ดของกฎ · กฎเลี่ยงการ์ดค่าบริการ · การ์ดค่าบริการไม่กลบขั้นถัดไปของตัวเอง</summary>
    public static async Task<List<Guid>> CardedEndpointsAsync(AccountingDbContext db, Guid companyId, Guid documentId, string trigger,
        IReadOnlyCollection<Guid> endpointIds, bool fromPlatform, CancellationToken ct = default)
    {
        if (endpointIds.Count == 0) return new List<Guid>();
        var window = ProviderCentralOa.DuplicateWindow(trigger);
        var since = window is TimeSpan w ? DateTime.UtcNow - w : DateTime.MinValue;
        var prefix = ProviderCentralOa.QueueBasePrefix(documentId, trigger);
        var dead = new[] { ContactLineChannel.StatusCancelled, ContactLineChannel.StatusFailed, ContactLineChannel.StatusSkipped };
        var ids = endpointIds.ToList();
        var q = db.LineQueues.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.EntityId == documentId && x.Trigger == trigger && !x.IsDeleted && x.EndpointId != null
                && ids.Contains(x.EndpointId.Value) && !dead.Contains(x.Status) && x.CreatedAt >= since);
        q = fromPlatform ? q.Where(x => x.BaseKey.StartsWith(prefix)) : q.Where(x => !x.BaseKey.StartsWith(prefix));
        return await q.Select(x => x.EndpointId!.Value).Distinct().ToListAsync(ct);
    }

    /// <summary>project เป็นแถวมุมผู้จ่าย — ใช้เป็น<b>ขั้นสุดท้าย</b>ของ query เท่านั้น (ห้ามต่อ Where/OrderBy/Contains หลังจากนี้)</summary>
    public static IQueryable<PlatformInvoiceRow> AsRows(this IQueryable<Document> q, Guid buyerCompanyId)
        => q.Select(d => new PlatformInvoiceRow(d.Id, buyerCompanyId, d.DocumentNumber, d.Reference, d.Status,
            d.DocumentDate, d.DueDate, d.TotalAmount, d.BalanceDue, d.SubTotal));

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
                       select new { d.Id, c.ExternalId, d.DocumentNumber, d.Reference, d.Status, d.DocumentDate, d.DueDate, d.TotalAmount, d.BalanceDue, d.SubTotal })
            .FirstOrDefaultAsync(ct);
        if (r == null || !Guid.TryParse(r.ExternalId, out var buyer)) return null;
        return new PlatformInvoiceRow(r.Id, buyer, r.DocumentNumber, r.Reference, r.Status, r.DocumentDate, r.DueDate, r.TotalAmount, r.BalanceDue, r.SubTotal);
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
