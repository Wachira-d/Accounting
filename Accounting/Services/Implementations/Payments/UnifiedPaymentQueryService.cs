using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Payments;

/// <summary>Cross-domain payment aggregator — รวม Payment (AR/AP) +
/// SiteOrderPayment (CMS storefront) + PosPayment (POS) + SubscriptionPayment
/// เป็น stream เดียวสำหรับ cash flow forecast / executive dashboard.
/// duplicate audit #1 phase 1: unified read-side ไม่ migrate persistence
///
/// **Filter**: companyId + date range. ผลลัพธ์ normalized แล้ว
/// (Domain discriminator + Currency + Method).</summary>
public interface IUnifiedPaymentQueryService
{
    Task<List<UnifiedPaymentRow>> QueryAsync(Guid companyId, DateTime from, DateTime to,
        string? domain = null, CancellationToken ct = default);

    Task<decimal> SumInflowsAsync(Guid companyId, DateTime from, DateTime to,
        CancellationToken ct = default);
}

public sealed record UnifiedPaymentRow(
    string Domain,                // AR / AP / POS / SUB
    Guid EntityId,
    DateTime PaymentDate,
    decimal Amount,
    PaymentMethod Method,
    string Currency,
    string? Reference);

public class UnifiedPaymentQueryService : IUnifiedPaymentQueryService
{
    private readonly AccountingDbContext _db;
    public UnifiedPaymentQueryService(AccountingDbContext db) { _db = db; }

    public async Task<List<UnifiedPaymentRow>> QueryAsync(Guid companyId, DateTime from, DateTime to,
        string? domain = null, CancellationToken ct = default)
    {
        var result = new List<UnifiedPaymentRow>();

        // AR/AP: Payment table — domain ตาม source Document.DocumentType
        if (domain == null || domain == "AR" || domain == "AP")
        {
            var pays = await _db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted
                    && p.PaymentDate >= from && p.PaymentDate <= to)
                .Join(_db.Documents.AsNoTracking(), p => p.DocumentId, d => d.Id,
                    (p, d) => new {
                        p.Id, p.PaymentDate, p.Amount, p.PaymentMethod, p.Reference,
                        d.Currency, d.DocumentType,
                    })
                .ToListAsync(ct);
            foreach (var p in pays)
            {
                var isAr = p.DocumentType == DocumentType.Invoice
                        || p.DocumentType == DocumentType.TaxInvoice
                        || p.DocumentType == DocumentType.DebitNote
                        || p.DocumentType == DocumentType.Receipt
                        || p.DocumentType == DocumentType.ReceiptVoucher;
                var d = isAr ? "AR" : "AP";
                if (domain != null && domain != d) continue;
                result.Add(new UnifiedPaymentRow(d, p.Id, p.PaymentDate, p.Amount,
                    p.PaymentMethod, p.Currency ?? "THB", p.Reference));
            }
        }

        // POS: PosPayment — BaseEntity → CompanyId via PosOrder join.
        // Method syntax เพื่อเลี่ยง C# parser ambiguity ระหว่าง LINQ `from`
        // keyword กับ parameter name `from` (date range)
        if (domain == null || domain == "POS")
        {
            var posPays = await _db.Set<PosPayment>().AsNoTracking()
                .Join(_db.PosOrders.AsNoTracking(),
                    p => p.OrderId, o => o.Id,
                    (p, o) => new { p, o })
                .Where(x => x.o.CompanyId == companyId && !x.p.IsDeleted
                    && x.p.PaidAt >= from && x.p.PaidAt <= to)
                .Select(x => new {
                    x.p.Id, x.p.PaidAt, x.p.Amount, x.p.PaymentMethod, x.p.ReferenceNo,
                })
                .ToListAsync(ct);
            foreach (var p in posPays)
                result.Add(new UnifiedPaymentRow("POS", p.Id, p.PaidAt, p.Amount,
                    p.PaymentMethod, "THB", p.ReferenceNo));
        }

        // CMS storefront: SiteOrderPayment (only Completed)
        if (domain == null || domain == "CMS")
        {
            var cmsPays = await _db.Set<SiteOrderPayment>().AsNoTracking()
                .Where(p => p.CompanyId == companyId && !p.IsDeleted
                    && p.Status == SitePaymentStatus.Completed
                    && p.PaidAt != null
                    && p.PaidAt >= from && p.PaidAt <= to)
                .Select(p => new {
                    p.Id, p.PaidAt, p.Amount, p.PaymentMethod, p.Currency, p.Reference,
                }).ToListAsync(ct);
            foreach (var p in cmsPays)
                result.Add(new UnifiedPaymentRow("CMS", p.Id, p.PaidAt!.Value, p.Amount,
                    p.PaymentMethod, p.Currency ?? "THB", p.Reference));
        }

        return result.OrderBy(x => x.PaymentDate).ToList();
    }

    public async Task<decimal> SumInflowsAsync(Guid companyId, DateTime from, DateTime to,
        CancellationToken ct = default)
    {
        // AR + POS + CMS = ฝั่งรับเงิน (กระแสเงินสดเข้า)
        var rows = await QueryAsync(companyId, from, to, null, ct);
        return rows.Where(r => r.Domain != "AP").Sum(r => r.Amount);
    }
}
