using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Consignment;

/// <summary>
/// Consignment movement service — bridges ConsignmentRecord stock to
/// the AR/AP ledger:
///
///   • Inbound consignment receipt: vendor's goods land at our
///     warehouse; we DO NOT own them yet. QuantityOnHand grows; no
///     AP entry posted. Balance sheet excludes from inventory asset.
///   • Inbound consumption: we sell or use the goods. AP arises NOW
///     (auto-create a PurchaseInvoice draft against the consignor
///     for the consumed quantity × AgreedUnitPrice). The cost-of-goods
///     was always zero on our books because we didn't own them; the
///     PurchaseInvoice + JE makes it whole.
///   • Outbound dispatch: our goods land at customer's warehouse;
///     we still own them. QuantityOnHand grows; no AR yet.
///   • Outbound consumption: customer uses the goods. We recognise
///     revenue NOW (auto-create an Invoice draft) at consumption qty
///     × AgreedUnitPrice.
/// </summary>
public interface IConsignmentService
{
    Task<ConsignmentRecord> ReceiveInboundAsync(Guid companyId, Guid productId,
        Guid vendorContactId, decimal quantity, decimal? agreedUnitPrice,
        DateTime receivedAt, CancellationToken ct = default);

    Task<ConsignmentRecord> DispatchOutboundAsync(Guid companyId, Guid productId,
        Guid customerContactId, decimal quantity, decimal? agreedUnitPrice,
        DateTime dispatchedAt, CancellationToken ct = default);

    Task<ConsumptionResult> RecordConsumptionAsync(Guid companyId, Guid consignmentRecordId,
        decimal consumedQuantity, DateTime consumedAt, CancellationToken ct = default);

    Task<IReadOnlyList<ConsignmentRecord>> ListAsync(Guid companyId, string? direction,
        CancellationToken ct = default);
}

public sealed record ConsumptionResult(
    Guid ConsignmentRecordId,
    string Direction,
    decimal QuantityConsumed,
    decimal Amount,
    Guid? CreatedDocumentId,
    string? CreatedDocumentNumber,
    string? Notes);

public class ConsignmentService : IConsignmentService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<ConsignmentService> _logger;

    public ConsignmentService(AccountingDbContext db, ILogger<ConsignmentService> logger)
    { _db = db; _logger = logger; }

    public async Task<ConsignmentRecord> ReceiveInboundAsync(Guid companyId, Guid productId,
        Guid vendorContactId, decimal quantity, decimal? agreedUnitPrice,
        DateTime receivedAt, CancellationToken ct = default)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive.");
        var record = new ConsignmentRecord
        {
            CompanyId = companyId,
            ProductId = productId,
            ContactId = vendorContactId,
            Direction = "Inbound",
            QuantityOnHand = quantity,
            AgreedUnitPrice = agreedUnitPrice,
            ReceivedAt = receivedAt,
        };
        _db.ConsignmentRecords.Add(record);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<ConsignmentRecord> DispatchOutboundAsync(Guid companyId, Guid productId,
        Guid customerContactId, decimal quantity, decimal? agreedUnitPrice,
        DateTime dispatchedAt, CancellationToken ct = default)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive.");
        // Outbound: WE own the goods until customer consumes — reduce
        // our local stock NOW (it's at customer's location), no GL.
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.CompanyId == companyId, ct);
        if (product == null) throw new InvalidOperationException("Product not found.");
        if (product.CurrentStock < quantity)
            throw new InvalidOperationException(
                $"Insufficient stock to dispatch: have {product.CurrentStock}, need {quantity}");
        product.CurrentStock -= quantity;
        var record = new ConsignmentRecord
        {
            CompanyId = companyId,
            ProductId = productId,
            ContactId = customerContactId,
            Direction = "Outbound",
            QuantityOnHand = quantity,
            AgreedUnitPrice = agreedUnitPrice,
            ReceivedAt = dispatchedAt,
        };
        _db.ConsignmentRecords.Add(record);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<ConsumptionResult> RecordConsumptionAsync(Guid companyId, Guid consignmentRecordId,
        decimal consumedQuantity, DateTime consumedAt, CancellationToken ct = default)
    {
        if (consumedQuantity <= 0) throw new ArgumentException("Quantity must be positive.");
        var record = await _db.ConsignmentRecords.FirstOrDefaultAsync(
            r => r.Id == consignmentRecordId && r.CompanyId == companyId, ct);
        if (record == null) throw new InvalidOperationException("ConsignmentRecord not found.");
        if (record.QuantityOnHand < consumedQuantity)
            throw new InvalidOperationException(
                $"Consumption exceeds on-hand: {consumedQuantity} > {record.QuantityOnHand}");

        record.QuantityOnHand -= consumedQuantity;
        if (record.QuantityOnHand == 0) record.ReturnedOrSettledAt = consumedAt;
        var unitPrice = record.AgreedUnitPrice ?? 0m;
        var amount = consumedQuantity * unitPrice;

        Models.Entities.Document? doc = null;
        if (amount > 0)
        {
            // Inbound consumption → PurchaseInvoice (AP).
            // Outbound consumption → Invoice (AR).
            var docType = record.Direction == "Inbound"
                ? Models.Enums.DocumentType.PurchaseInvoice
                : Models.Enums.DocumentType.Invoice;
            doc = new Models.Entities.Document
            {
                CompanyId = companyId,
                ContactId = record.ContactId,
                DocumentNumber = $"CON-{DateTime.UtcNow:yyyyMMdd}-{record.Id.ToString()[..6]}",
                DocumentType = docType,
                DocumentDate = consumedAt,
                TotalAmount = amount,
                BalanceDue = amount,
                Status = Models.Enums.DocumentStatus.Draft,
                Notes = $"[Auto-Consignment-{record.Direction}] {consumedQuantity} × {unitPrice:N2}",
                CreatedBy = $"ConsignmentService:{record.Id}",
            };
            _db.Documents.Add(doc);
        }
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Consignment consumption: {Dir} record={Id} qty={Qty} amount={Amt:N2} doc={Doc}",
            record.Direction, record.Id, consumedQuantity, amount, doc?.DocumentNumber ?? "(no doc)");

        return new ConsumptionResult(
            ConsignmentRecordId: record.Id,
            Direction: record.Direction,
            QuantityConsumed: consumedQuantity,
            Amount: amount,
            CreatedDocumentId: doc?.Id,
            CreatedDocumentNumber: doc?.DocumentNumber,
            Notes: record.QuantityOnHand == 0 ? "ปิด consignment record (consumed to 0)" : null);
    }

    public Task<IReadOnlyList<ConsignmentRecord>> ListAsync(Guid companyId, string? direction,
        CancellationToken ct = default)
    {
        var q = _db.ConsignmentRecords.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted);
        if (!string.IsNullOrEmpty(direction)) q = q.Where(r => r.Direction == direction);
        return q.OrderByDescending(r => r.ReceivedAt).Take(200).ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<ConsignmentRecord>)t.Result, ct);
    }
}
