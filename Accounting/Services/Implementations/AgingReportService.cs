using Accounting.Helpers;
using Accounting.Data;
using Accounting.Models.DTOs.Aging;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class AgingReportService : IAgingReportService
{
    private readonly AccountingDbContext _db;

    public AgingReportService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<AgingReportResponse> GetReceivableAgingAsync(Guid companyId, AgingReportRequest request)
    {
        return await BuildAgingReportAsync(companyId, AgingReportType.AccountsReceivable, request);
    }

    public async Task<AgingReportResponse> GetPayableAgingAsync(Guid companyId, AgingReportRequest request)
    {
        return await BuildAgingReportAsync(companyId, AgingReportType.AccountsPayable, request);
    }

    public async Task<AgingContactDetail> GetContactAgingDetailAsync(Guid companyId, Guid contactId, AgingReportType reportType, DateTime? asOfDate = null)
    {
        var report = await BuildAgingReportAsync(companyId, reportType, new AgingReportRequest(asOfDate, contactId));
        return report.Details.FirstOrDefault()
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลลูกค้า/เจ้าหนี้");
    }

    private async Task<AgingReportResponse> BuildAgingReportAsync(Guid companyId, AgingReportType reportType, AgingReportRequest request)
    {
        var asOfDate = request.AsOfDate ?? DateTime.UtcNow.Date;

        // Document types per side (หลักบัญชีไทย):
        //   AR (ลูกหนี้):  Invoice/TaxInvoice/BillingNote/DebitNote เพิ่มยอด
        //                  CreditNote ลดยอด → รวมแยกต่างหากด้านล่าง.
        //   AP (เจ้าหนี้): PurchaseInvoice เพิ่มหนี้การค้า (21210),
        //                  Expense เพิ่มเจ้าหนี้อื่น (21220),
        //                  DebitNote (ผู้ขายออก) เพิ่มหนี้,
        //                  CreditNote (ผู้ขายออก) ลดหนี้.
        //   PaymentVoucher/CertificateInLieu = หลักฐานการจ่ายเงินสด ตัด
        //   หนี้/จ่ายตรงเสมอ — ห้ามอยู่ใน aging (เคยมีบั๊ก voucher BalanceDue>0
        //   จาก data ไม่ครบ → ขึ้น aging หลอกว่าค้างจ่าย).
        var positiveTypes = reportType == AgingReportType.AccountsReceivable
            ? new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.BillingNote, DocumentType.DebitNote }
            : new[] { DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.DebitNote };
        var negativeType = DocumentType.CreditNote;

        var allTypes = positiveTypes.Concat(new[] { negativeType }).ToArray();
        var query = _db.Documents.AsNoTracking()
            // ไม่ Include Contact — hydrate แยก (กัน INNER JOIN ตัดใบที่ contact ถูกลบ
            // ออกจากรายงานอายุ AR/AP = under-report + TFRS NPAEs ch.9 allowance ผิด)
            .Where(d => d.CompanyId == companyId
                && !d.IsDeleted
                && allTypes.Contains(d.DocumentType)
                // Exclude every state ที่ยังไม่ใช่ "หนี้จริง":
                //   Draft/WaitingApproval = ยังไม่ใช่หนี้  · Rejected = ถูกปฏิเสธ
                //   Voided                = ยกเลิก         · Paid = ชำระครบแล้ว
                && d.Status != DocumentStatus.Draft
                && d.Status != DocumentStatus.WaitingApproval
                && d.Status != DocumentStatus.Rejected
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Paid
                && d.BalanceDue > 0
                && d.DocumentDate <= asOfDate);

        if (request.ContactId.HasValue)
            query = query.Where(d => d.ContactId == request.ContactId.Value);

        if (request.ProjectId.HasValue)
            query = query.Where(d => d.ProjectId == request.ProjectId.Value);

        var documents = await query.ToListAsync();

        // ใบลดหนี้/เพิ่มหนี้อยู่ได้สองฝั่ง — ชนิดอย่างเดียวตัดสินไม่ได้ (Helpers/DocumentSide
        // ระบุเป็น BothSides) เดิม aging รับ CN/DN ทุกใบทั้ง AR และ AP ⇒ CN ที่เราออกให้ลูกค้า
        // ไปลดยอด "เจ้าหนี้" · DN ที่ผู้ขายออกไปเพิ่มยอด "ลูกหนี้" (ERP_REVIEW F-03).
        // ใช้ฝั่งที่ระบบตัดสินไว้ตอนสร้าง (CnDnPurchaseSideOverride — DocumentService ตั้งจาก
        // ใบต้นทาง) · แถวเก่าที่ยังเป็น null คงพฤติกรรมเดิม (โผล่ทั้งสองฝั่ง) จนกว่าจะ backfill —
        // ห้ามเดาฝั่งจากชนิด เพราะ DocumentSide.IsPurchase(CN) ไม่มี ourRole = "ซื้อ" เสมอ
        // ⇒ CN ฝั่งขายเก่าทั้งหมดจะหายจาก AR aging เงียบ ๆ (ค่า default ที่แต่งขึ้น)
        var wantPurchase = reportType != AgingReportType.AccountsReceivable;
        documents = documents.Where(d =>
                !DocumentSide.IsAmbiguous(d.DocumentType)
                || d.CnDnPurchaseSideOverride == null
                || d.CnDnPurchaseSideOverride == wantPurchase)
            .ToList();
        await _db.HydrateContactsAsync(companyId, documents);

        // Build contact details with aging (d.Contact ผูกกลับแล้วจาก hydrate — ปลอดภัย)
        var contactGroups = documents.GroupBy(d => new { d.ContactId, d.Contact.Name, d.Contact.TaxId });

        var details = new List<AgingContactDetail>();
        foreach (var group in contactGroups)
        {
            var docs = new List<AgingDocumentDetail>();
            decimal current = 0, d1to30 = 0, d31to60 = 0, d61to90 = 0, over90 = 0;

            foreach (var doc in group)
            {
                var dueDate = doc.DueDate ?? doc.DocumentDate;
                var agingDays = Math.Max(0, (int)(asOfDate - dueDate).TotalDays);
                var bucket = GetAgingBucket(agingDays);
                // CreditNote ลดยอดลูกหนี้/เจ้าหนี้ → เก็บเป็นลบใน bucket
                var sign = doc.DocumentType == negativeType ? -1m : 1m;
                var amount = sign * doc.BalanceDue;

                switch (bucket)
                {
                    case "Current": current += amount; break;
                    case "1-30 วัน": d1to30 += amount; break;
                    case "31-60 วัน": d31to60 += amount; break;
                    case "61-90 วัน": d61to90 += amount; break;
                    default: over90 += amount; break;
                }

                docs.Add(new AgingDocumentDetail(doc.Id, doc.DocumentNumber, doc.DocumentType,
                    doc.DocumentDate, doc.DueDate, doc.TotalAmount, amount, agingDays, bucket));
            }

            details.Add(new AgingContactDetail(group.Key.ContactId, group.Key.Name, group.Key.TaxId,
                current, d1to30, d31to60, d61to90, over90,
                current + d1to30 + d31to60 + d61to90 + over90, docs.OrderByDescending(d => d.AgingDays).ToList()));
        }

        var totals = new AgingTotals(
            details.Sum(d => d.Current),
            details.Sum(d => d.Days1To30),
            details.Sum(d => d.Days31To60),
            details.Sum(d => d.Days61To90),
            details.Sum(d => d.Over90Days),
            details.Sum(d => d.TotalBalance));

        var buckets = new List<AgingBucket>
        {
            new("Current", 0, 0, totals.Current, documents.Count(d => GetAgingDays(d, asOfDate) == 0)),
            new("1-30 วัน", 1, 30, totals.Days1To30, documents.Count(d => { var days = GetAgingDays(d, asOfDate); return days >= 1 && days <= 30; })),
            new("31-60 วัน", 31, 60, totals.Days31To60, documents.Count(d => { var days = GetAgingDays(d, asOfDate); return days >= 31 && days <= 60; })),
            new("61-90 วัน", 61, 90, totals.Days61To90, documents.Count(d => { var days = GetAgingDays(d, asOfDate); return days >= 61 && days <= 90; })),
            new("มากกว่า 90 วัน", 91, null, totals.Over90Days, documents.Count(d => GetAgingDays(d, asOfDate) > 90))
        };

        // Filter out contacts whose net balance nets to zero (e.g. CN
        // perfectly offset by an invoice) — they shouldn't clutter aging.
        var meaningful = details.Where(d => Math.Abs(d.TotalBalance) >= 0.01m)
            .OrderByDescending(d => d.TotalBalance).ToList();

        return new AgingReportResponse(reportType, asOfDate, buckets, meaningful, totals);
    }

    private static int GetAgingDays(Models.Entities.Document doc, DateTime asOfDate)
    {
        var dueDate = doc.DueDate ?? doc.DocumentDate;
        return Math.Max(0, (int)(asOfDate - dueDate).TotalDays);
    }

    private static string GetAgingBucket(int days) => days switch
    {
        0 => "Current",
        <= 30 => "1-30 วัน",
        <= 60 => "31-60 วัน",
        <= 90 => "61-90 วัน",
        _ => "มากกว่า 90 วัน"
    };
}
