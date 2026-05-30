using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Tax;

/// <summary>
/// Stamp duty (อากรแสตมป์) tracking per ประมวลรัษฎากร §103-105.
///
/// The RD's stamp-duty schedule has 28 groups; this service exposes
/// the rate calculator for the 4-5 groups Thai SMEs encounter:
///
///   • Group 1 — Rental (ค่าเช่า): 0.1% of contract value, cap 1,000 baht
///     per year of lease term; minimum 1 baht.
///   • Group 4 — Hire of work (จ้างทำของ): 0.1% of value, cap 200,000.
///   • Group 5 — Loan (เงินกู้): 0.05%, cap 10,000.
///   • Group 7 — Power of Attorney (มอบอำนาจ): flat 30 baht.
///   • Group 17 — Cheque (เช็คต่างประเทศ): 3 baht/cheque (rare).
///
/// Compute() returns the duty + which schedule applied; admin can
/// override DutyAmount after saving (RD assessment may differ).
/// </summary>
public interface IStampDutyService
{
    decimal Compute(int rdScheduleNumber, decimal instrumentValue, int? leaseYears = null);

    Task<StampDutyRecord> CreateAsync(Guid companyId, string reference,
        Guid? contactId, int rdScheduleNumber, string instrumentType,
        decimal instrumentValue, DateTime instrumentDate,
        int? leaseYears, string paymentMethod, CancellationToken ct = default);

    Task<StampDutyRecord> MarkPaidAsync(Guid companyId, Guid recordId,
        string rdReceiptNumber, DateTime paidAt, CancellationToken ct = default);

    Task<IReadOnlyList<StampDutyRecord>> ListUnpaidAsync(Guid companyId,
        CancellationToken ct = default);
}

public class StampDutyService : IStampDutyService
{
    private readonly AccountingDbContext _db;

    public StampDutyService(AccountingDbContext db) { _db = db; }

    public decimal Compute(int rdScheduleNumber, decimal instrumentValue, int? leaseYears = null)
    {
        // The figures here are the RD schedule values as published; the
        // service rounds DOWN to the nearest 1 baht because stamp duty
        // doesn't allow fractional satang in physical-stamp form.
        decimal raw = rdScheduleNumber switch
        {
            1 => Math.Max(1m, instrumentValue * 0.001m / Math.Max(1, leaseYears ?? 1)),
            4 => Math.Min(200_000m, instrumentValue * 0.001m),
            5 => Math.Min(10_000m, instrumentValue * 0.0005m),
            7 => 30m,
            17 => 3m,                       // per cheque
            _ => 0m,
        };
        // Group 1: per-year cap = 1,000 baht.
        if (rdScheduleNumber == 1)
        {
            var perYear = Math.Min(1_000m, raw);
            raw = perYear * Math.Max(1, leaseYears ?? 1);
        }
        return Math.Floor(raw);
    }

    public async Task<StampDutyRecord> CreateAsync(Guid companyId, string reference,
        Guid? contactId, int rdScheduleNumber, string instrumentType,
        decimal instrumentValue, DateTime instrumentDate,
        int? leaseYears, string paymentMethod, CancellationToken ct = default)
    {
        var duty = Compute(rdScheduleNumber, instrumentValue, leaseYears);
        var record = new StampDutyRecord
        {
            CompanyId = companyId,
            Reference = reference,
            ContactId = contactId,
            RdScheduleNumber = rdScheduleNumber,
            InstrumentType = instrumentType,
            InstrumentValue = instrumentValue,
            DutyAmount = duty,
            InstrumentDate = instrumentDate,
            PaymentMethod = paymentMethod,
        };
        _db.StampDutyRecords.Add(record);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<StampDutyRecord> MarkPaidAsync(Guid companyId, Guid recordId,
        string rdReceiptNumber, DateTime paidAt, CancellationToken ct = default)
    {
        var record = await _db.StampDutyRecords.FirstOrDefaultAsync(
            r => r.Id == recordId && r.CompanyId == companyId, ct);
        if (record == null) throw new InvalidOperationException("StampDutyRecord not found.");
        record.PaidAt = paidAt;
        record.RdReceiptNumber = rdReceiptNumber;
        await _db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<IReadOnlyList<StampDutyRecord>> ListUnpaidAsync(Guid companyId,
        CancellationToken ct = default)
    {
        return await _db.StampDutyRecords.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.PaidAt == null)
            .OrderBy(r => r.InstrumentDate)
            .ToListAsync(ct);
    }
}
