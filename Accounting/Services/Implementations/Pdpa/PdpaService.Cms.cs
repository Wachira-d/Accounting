using System.Text.RegularExpressions;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Pdpa;

/// <summary>
/// รอบ 209 P6 (R-209-D6 · คำตัดสินข้อ 168 ฑ) — DSR ม.30/31/33 ครอบข้อมูลเว็บ: <c>SiteCustomer</c>(+ที่อยู่) · ช่องผู้ซื้อ/ผู้รับของ <c>SiteOrder</c> ·
/// ผู้จองของ <c>SiteBooking</c> · <c>CmsLead</c> · <c>SiteFormSubmission</c> · ความยินยอมที่บันทึกจากเว็บ
///
/// <para>เจ้าของข้อมูลเว็บส่วนใหญ่ไม่มี User/Contact (แขกสั่งซื้อ) ⇒ ระบุด้วยอีเมลและ/หรือเบอร์ · จับคู่<b>เท่ากันหลัง normalize</b> เท่านั้น
/// (<see cref="CmsContactResolver.NormalizeEmail"/>/<see cref="CmsContactResolver.NormalizePhone"/> ตัวเดียวกับตัวหาผู้ติดต่อ — ไม่ fuzzy บนตัวเลข) ·
/// ทุก query กรอง <c>CompanyId</c> (กฎ M) · ช่องที่ล้าง/คงไว้ตัดสินที่ <see cref="CmsPdpaErasure"/> ตัวเดียว</para>
/// </summary>
public partial class PdpaService
{
    private sealed record CmsSubjectRows(
        List<SiteCustomer> Customers, List<SiteOrder> Orders, List<SiteBooking> Bookings,
        List<CmsLead> Leads, List<SiteFormSubmission> Submissions, List<PdpaConsentRecord> Consents);



    private async Task<CmsSubjectRows> LoadCmsSubjectRowsAsync(Guid companyId, string? emailRaw, string? phoneRaw, bool track, CancellationToken ct)
    {
        var email = CmsContactResolver.NormalizeEmail(emailRaw);
        var phone = CmsContactResolver.NormalizePhone(phoneRaw);
        if (email == null && phone == null)
            throw new InvalidOperationException("ระบุอีเมลหรือเบอร์โทร (อย่างน้อย 9 หลัก) ของเจ้าของข้อมูล");
        var tail = phone?[^4..] ?? "#no-phone#";
        var e = email ?? "#no-email#";

        IQueryable<T> Q<T>(IQueryable<T> q) where T : class => track ? q : q.AsNoTracking();

        var customers = (await Q(_db.SiteCustomers).Where(c => c.CompanyId == companyId
                && ((c.Email != null && c.Email.ToLower() == e) || (c.Phone != null && c.Phone.Contains(tail))))
                .ToListAsync(ct))
            .Where(c => CmsContactResolver.NormalizeEmail(c.Email) == email && email != null
                || CmsContactResolver.NormalizePhone(c.Phone) == phone && phone != null).ToList();
        var custIds = customers.Select(c => c.Id).ToList();

        var orders = (await Q(_db.SiteOrders).Where(o => o.CompanyId == companyId
                && ((o.CustomerId != null && custIds.Contains(o.CustomerId.Value))
                    || (o.ShippingEmail != null && o.ShippingEmail.ToLower() == e)
                    || (o.ShippingPhone != null && o.ShippingPhone.Contains(tail))))
                .ToListAsync(ct))
            .Where(o => (o.CustomerId is Guid cid && custIds.Contains(cid))
                || (email != null && CmsContactResolver.NormalizeEmail(o.ShippingEmail) == email)
                || (phone != null && CmsContactResolver.NormalizePhone(o.ShippingPhone) == phone)).ToList();

        var bookings = (await Q(_db.SiteBookings).Where(b => b.CompanyId == companyId
                && ((b.CustomerId != null && custIds.Contains(b.CustomerId.Value))
                    || (b.GuestEmail != null && b.GuestEmail.ToLower() == e)
                    || (b.GuestPhone != null && b.GuestPhone.Contains(tail))))
                .ToListAsync(ct))
            .Where(b => (b.CustomerId is Guid cid && custIds.Contains(cid))
                || (email != null && CmsContactResolver.NormalizeEmail(b.GuestEmail) == email)
                || (phone != null && CmsContactResolver.NormalizePhone(b.GuestPhone) == phone)).ToList();

        var leads = (await Q(_db.CmsLeads).Where(l => l.CompanyId == companyId
                && ((l.CustomerEmail != null && l.CustomerEmail.ToLower() == e)
                    || (l.CustomerPhone != null && l.CustomerPhone.Contains(tail))))
                .ToListAsync(ct))
            .Where(l => (email != null && CmsContactResolver.NormalizeEmail(l.CustomerEmail) == email)
                || (phone != null && CmsContactResolver.NormalizePhone(l.CustomerPhone) == phone)).ToList();

        var submissions = (await Q(_db.SiteFormSubmissions).Where(s => s.CompanyId == companyId
                && ((s.CustomerId != null && custIds.Contains(s.CustomerId.Value))
                    || EF.Functions.ILike(s.DataJson, "%" + e + "%")
                    || s.DataJson.Contains(tail)))
                .ToListAsync(ct))
            .Where(s => (s.CustomerId is Guid cid && custIds.Contains(cid)) || CmsPdpaErasure.SubmissionMentions(s.DataJson, email, phone)).ToList();

        var consents = await Q(_db.Set<PdpaConsentRecord>()).Where(r => r.CompanyId == companyId
                && r.SubjectContact != null && (r.SubjectContact == e || r.SubjectContact == (phone ?? "#no-phone#")))
            .ToListAsync(ct);

        return new CmsSubjectRows(customers, orders, bookings, leads, submissions, consents);
    }

    /// <summary>รอบ 211: ลิงก์ LINE ของสมาชิก/ออเดอร์/การจองของเจ้าของข้อมูล (กรองบริษัท)</summary>
    private async Task<List<CustomerLineLink>> LoadCmsLineLinksAsync(Guid companyId, CmsSubjectRows r, bool track, CancellationToken ct)
    {
        var mem = r.Customers.Select(c => c.Id).ToList();
        var ord = r.Orders.Select(o => o.Id).ToList();
        var bkg = r.Bookings.Select(b => b.Id).ToList();
        if (mem.Count + ord.Count + bkg.Count == 0) return new List<CustomerLineLink>();
        var q = _db.CustomerLineLinks.Where(l => l.CompanyId == companyId
            && ((l.TargetKind == CustomerLineLinkRule.KindMember && mem.Contains(l.TargetId))
                || (l.TargetKind == CustomerLineLinkRule.KindOrder && ord.Contains(l.TargetId))
                || (l.TargetKind == CustomerLineLinkRule.KindBooking && bkg.Contains(l.TargetId))));
        return await (track ? q : q.AsNoTracking()).ToListAsync(ct);
    }

    public async Task<CmsSubjectDataReport> GenerateCmsAccessReportAsync(Guid companyId, string? email, string? phone, CancellationToken ct = default)
    {
        var r = await LoadCmsSubjectRowsAsync(companyId, email, phone, track: false, ct);
        var custIds = r.Customers.Select(c => c.Id).ToList();
        var addresses = custIds.Count == 0 ? new List<SiteCustomerAddress>()
            : await _db.SiteCustomerAddresses.AsNoTracking().Where(a => a.CompanyId == companyId && custIds.Contains(a.CustomerId)).ToListAsync(ct);
        // รอบ 211: LINE ที่ลูกค้าผูกเอง (สมาชิก/การจอง/ออเดอร์) — ม.30 ต้องเห็นว่าระบบถือ LINE userId ของเขาไว้
        var lineLinks = await LoadCmsLineLinksAsync(companyId, r, track: false, ct);
        List<Dictionary<string, object?>> LinesOf(string kind, Guid id) => lineLinks.Where(l => l.TargetKind == kind && l.TargetId == id)
            .Select(l => new Dictionary<string, object?>
            {
                ["lineUserId"] = l.LineUserId, ["oaScope"] = l.OaScope, ["linkedAt"] = l.LinkedAt, ["unlinkedAt"] = l.UnlinkedAt,
                ["unlinkReason"] = l.UnlinkReason, ["blockedAt"] = l.BlockedAt,
            }).ToList();

        var customers = r.Customers.Select(c => new Dictionary<string, object?>
        {
            ["id"] = c.Id, ["siteId"] = c.SiteId, ["email"] = c.Email, ["fullName"] = c.FullName, ["phone"] = c.Phone,
            ["taxId"] = c.TaxId, ["branchCode"] = c.BranchCode, ["companyName"] = c.CompanyName,
            ["acceptMarketing"] = c.AcceptMarketing, ["consentGiven"] = c.ConsentGiven, ["consentGivenAt"] = c.ConsentGivenAt,
            ["consentWithdrawnAt"] = c.ConsentWithdrawnAt, ["createdAt"] = c.CreatedAt, ["lastLoginAt"] = c.LastLoginAt,
            ["lineLinks"] = LinesOf(CustomerLineLinkRule.KindMember, c.Id),
            ["addresses"] = addresses.Where(a => a.CustomerId == c.Id).Select(a => new Dictionary<string, object?>
            {
                ["label"] = a.Label, ["recipientName"] = a.RecipientName, ["phone"] = a.Phone, ["addressLine"] = a.AddressLine,
                ["subDistrict"] = a.SubDistrict, ["district"] = a.District, ["province"] = a.Province, ["postalCode"] = a.PostalCode,
                // รอบ 216 (ข้อ 174): ที่อยู่หลักจากหน้า "ข้อมูลส่วนตัว" ของสมาชิก — ม.30/31 ต้องเห็นว่าแถวไหนเป็นที่อยู่จัดส่ง/ใบกำกับ
                ["buildingNumber"] = a.BuildingNumber, ["buildingName"] = a.BuildingName, ["streetName"] = a.StreetName, ["countryCode"] = a.CountryCode,
                ["addressType"] = a.AddressType.ToString(), ["isDefault"] = a.IsDefault, ["updatedAt"] = a.UpdatedAt,
            }).ToList(),
        }).ToList();
        var orders = r.Orders.Select(o => new Dictionary<string, object?>
        {
            ["id"] = o.Id, ["orderNumber"] = o.OrderNumber, ["createdAt"] = o.CreatedAt, ["status"] = o.Status.ToString(),
            ["totalAmount"] = o.TotalAmount, ["currency"] = o.Currency,
            ["shippingName"] = o.ShippingName, ["shippingAddress"] = o.ShippingAddress, ["shippingPhone"] = o.ShippingPhone,
            ["shippingEmail"] = o.ShippingEmail, ["billingName"] = o.BillingName, ["billingAddress"] = o.BillingAddress,
            ["billingTaxId"] = o.BillingTaxId, ["customerNotes"] = o.CustomerNotes, ["erpDocumentId"] = o.ErpDocumentId,
            ["lineLinks"] = LinesOf(CustomerLineLinkRule.KindOrder, o.Id),
        }).ToList();
        var bookings = r.Bookings.Select(b => new Dictionary<string, object?>
        {
            ["id"] = b.Id, ["bookingNumber"] = b.BookingNumber, ["bookingDate"] = b.BookingDate, ["status"] = b.Status.ToString(),
            ["guestName"] = b.GuestName, ["guestEmail"] = b.GuestEmail, ["guestPhone"] = b.GuestPhone,
            ["customerNotes"] = b.CustomerNotes, ["totalAmount"] = b.TotalAmount, ["erpDocumentId"] = b.ErpDocumentId,
            ["lineLinks"] = LinesOf(CustomerLineLinkRule.KindBooking, b.Id),
        }).ToList();
        var leads = r.Leads.Select(l => new Dictionary<string, object?>
        {
            ["id"] = l.Id, ["leadNumber"] = l.LeadNumber, ["createdAt"] = l.CreatedAt, ["status"] = l.Status.ToString(),
            ["customerName"] = l.CustomerName, ["customerEmail"] = l.CustomerEmail, ["customerPhone"] = l.CustomerPhone,
            ["customerCompany"] = l.CustomerCompany, ["customerTaxId"] = l.CustomerTaxId, ["message"] = l.Message, ["fields"] = l.DataJson,
        }).ToList();
        var subs = r.Submissions.Select(s => new Dictionary<string, object?>
        {
            ["id"] = s.Id, ["formId"] = s.FormId, ["createdAt"] = s.CreatedAt, ["data"] = s.DataJson, ["ipAddress"] = s.IpAddress,
        }).ToList();
        var consents = r.Consents.Select(c => new Dictionary<string, object?>
        {
            ["id"] = c.Id, ["purpose"] = c.Purpose, ["policyVersion"] = c.PolicyVersion, ["grantedAt"] = c.GrantedAt,
            ["withdrawnAt"] = c.WithdrawnAt, ["channel"] = c.Channel, ["evidenceHash"] = c.EvidenceHash,
            // รอบ 214 ฝ่ายค้าน S4: ผลตรวจหลักฐานรายแถว (ตัวตรวจเดียวกับแถวยอมรับนโยบาย)
            ["evidenceCheck"] = Accounting.Helpers.PlatformPolicyAcceptanceRules.CheckLabel(c),
        }).ToList();

        return new CmsSubjectDataReport(DateTime.UtcNow, CmsContactResolver.NormalizeEmail(email), CmsContactResolver.NormalizePhone(phone),
            customers, orders, bookings, leads, subs, consents,
            customers.Count + orders.Count + bookings.Count + leads.Count + subs.Count + consents.Count);
    }

    public async Task<CmsErasureResult> ApplyCmsErasureAsync(Guid companyId, string? email, string? phone, string actor, CancellationToken ct = default)
    {
        var r = await LoadCmsSubjectRowsAsync(companyId, email, phone, track: true, ct);
        var now = DateTime.UtcNow;

        // วันที่ของเอกสารบัญชีที่ผูก (ใบขาย/ใบมัดจำของออเดอร์ · ใบของการจอง · ใบเสนอราคาของ lead) — กรองบริษัท · ไม่ลบ
        var orderIds = r.Orders.Select(o => o.Id).ToList();
        var depositDocs = orderIds.Count == 0 ? new List<(Guid OrderId, Guid DocId)>()
            : (await _db.SiteOrderPayments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && orderIds.Contains(p.OrderId) && p.DepositDocumentId != null)
                .Select(p => new { p.OrderId, DocId = p.DepositDocumentId!.Value }).ToListAsync(ct))
                .Select(x => (x.OrderId, x.DocId)).ToList();
        var docIds = r.Orders.Where(o => o.ErpDocumentId != null).Select(o => o.ErpDocumentId!.Value)
            .Concat(depositDocs.Select(d => d.DocId))
            .Concat(r.Bookings.Where(b => b.ErpDocumentId != null).Select(b => b.ErpDocumentId!.Value))
            .Concat(r.Leads.Where(l => l.ErpDocumentId != null).Select(l => l.ErpDocumentId!.Value))
            .Distinct().ToList();
        var docDates = docIds.Count == 0 ? new Dictionary<Guid, DateTime>()
            : await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && docIds.Contains(d.Id) && !d.IsDeleted)
                .ToDictionaryAsync(d => d.Id, d => d.DocumentDate, ct);
        DateTime? Latest(IEnumerable<Guid> ids) => ids.Where(docDates.ContainsKey).Select(id => (DateTime?)docDates[id]).Max();

        var rows = 0; var held = 0;
        foreach (var o in r.Orders)
        {
            var linked = new List<Guid>();
            if (o.ErpDocumentId is Guid d) linked.Add(d);
            linked.AddRange(depositDocs.Where(x => x.OrderId == o.Id).Select(x => x.DocId));
            var hold = CmsPdpaErasure.IsUnderLegalHold(Latest(linked), now);
            if (hold) held++;
            if (CmsPdpaErasure.AnonymizeOrder(o, hold) > 0) { o.UpdatedAt = now; o.UpdatedBy = actor; rows++; }
        }
        foreach (var b in r.Bookings)
        {
            var hold = CmsPdpaErasure.IsUnderLegalHold(b.ErpDocumentId is Guid d ? Latest(new[] { d }) : null, now);
            if (hold) held++;
            if (CmsPdpaErasure.AnonymizeBooking(b, hold) > 0) { b.UpdatedAt = now; b.UpdatedBy = actor; rows++; }
        }
        foreach (var l in r.Leads)
        {
            var hold = CmsPdpaErasure.IsUnderLegalHold(l.ErpDocumentId is Guid d ? Latest(new[] { d }) : null, now);
            if (hold) held++;
            if (CmsPdpaErasure.AnonymizeLead(l, hold) > 0) { l.UpdatedAt = now; l.UpdatedBy = actor; rows++; }
        }
        foreach (var s in r.Submissions)
            if (CmsPdpaErasure.AnonymizeSubmission(s) > 0) { s.UpdatedAt = now; s.UpdatedBy = actor; rows++; }
        var custIds = r.Customers.Select(c => c.Id).ToList();
        foreach (var c in r.Customers)
        {
            rows += CmsPdpaErasure.AnonymizeCustomer(c, now);
            c.UpdatedAt = now; c.UpdatedBy = actor;
        }
        if (custIds.Count > 0)
        {
            var addresses = await _db.SiteCustomerAddresses.Where(a => a.CompanyId == companyId && custIds.Contains(a.CustomerId)).ToListAsync(ct);
            _db.SiteCustomerAddresses.RemoveRange(addresses);
            rows += addresses.Count;
        }
        // รอบ 211: LINE ที่ลูกค้าผูกเอง — ไม่ใช่เอกสารบัญชี (ไม่มี legal hold) ⇒ เส้นปิดเดียวกับการยกเลิก (ฝ่ายค้าน ข้อ 2):
        // ถอนยินยอม · ปิด + ล้าง LINE userId · audit · เผารหัสผูกที่ค้าง (รวมเป้าหมายที่ยังไม่เคยผูก — รหัสที่ออกก่อนลบต้องผูกกลับไม่ได้)
        var lineLinks = await LoadCmsLineLinksAsync(companyId, r, track: true, ct);
        foreach (var l in lineLinks) l.UpdatedBy = actor;
        rows += await CustomerLineLinkClosure.CloseAsync(_db, companyId, lineLinks, "erase", now, ct);
        var lineTargets = r.Customers.Select(c => (Kind: CustomerLineLinkRule.KindMember, Id: c.Id))
            .Concat(r.Orders.Select(o => (Kind: CustomerLineLinkRule.KindOrder, Id: o.Id)))
            .Concat(r.Bookings.Select(b => (Kind: CustomerLineLinkRule.KindBooking, Id: b.Id)));
        foreach (var (kind, id) in lineTargets)
            await CustomerLineLinkClosure.BurnPendingCodesAsync(_db, companyId, kind, id, now, ct);
        // ฝ่ายค้านรอบ 212 S3: บันทึกการส่งข้อความถึงเป้าหมายของเจ้าของข้อมูล (สมาชิก/ออเดอร์/การจอง/ฟอร์ม) — ไม่ใช่เอกสารบัญชี ⇒ ลบจริง
        var notifyTargets = r.Customers.Select(c => c.Id).Concat(r.Orders.Select(o => o.Id)).Concat(r.Bookings.Select(b => b.Id))
            .Concat(r.Submissions.Select(x => x.Id)).Distinct().ToList();
        if (notifyTargets.Count > 0)
            rows += await _db.CustomerNotifyLogs.IgnoreQueryFilters()
                .Where(l => l.CompanyId == companyId && l.TargetId != null && notifyTargets.Contains(l.TargetId.Value))
                .ExecuteDeleteAsync(ct);
        // ความยินยอมเป็น "หลักฐาน" ของการประมวลผลที่ผ่านมา (ม.19 ภาระพิสูจน์ของผู้ควบคุม) ⇒ ไม่ลบ แต่ถอนความยินยอมทุกแถวที่ยังไม่ถอน
        foreach (var cr in r.Consents.Where(x => x.WithdrawnAt == null))
        {
            cr.WithdrawnAt = now;
            cr.WithdrawnReason = "DSR ม.33 ขอลบข้อมูล";
            rows++;
        }

        if (rows > 0) await _db.SaveChangesAsync(ct);
        var notes = new List<string>();
        if (held > 0)
            notes.Add($"{held} แถวผูกเอกสารบัญชีที่ยังอยู่ในงวดเก็บ {CmsPdpaErasure.RetentionYears} ปี (พ.ร.บ.การบัญชี ม.10 · ป.รัษฎากร §87/3) — คงชื่อ/ที่อยู่/เลขผู้เสียภาษีของผู้ซื้อไว้ ล้างช่องติดต่อแล้ว");
        if (rows == 0) notes.Add("ไม่พบข้อมูลเว็บของเจ้าของข้อมูลนี้ (หรือถูกลบไปแล้ว)");
        notes.Add($"[{CmsPdpaErasure.RuleCode}] ผู้ติดต่อ/เอกสารบัญชีของรายนี้ลบผ่าน DSR ของผู้ติดต่อ (dsr/erase) แยกต่างหาก");
        return new CmsErasureResult(rows, held, notes);
    }
}
