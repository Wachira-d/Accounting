using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Tax;

/// <summary>
/// Stamp duty (อากรแสตมป์) tracking per ประมวลรัษฎากร §103-105.
///
/// รอบ 228: อัตรา/วันครบกำหนด/เงินเพิ่มอากรอยู่ที่ตัวตั้งเดียว <c>Helpers/StampDutySchedule</c> ·
/// การจ่ายที่ลงบัญชีเดินเส้นนำส่ง <c>StatutoryRemittanceService.RemitStampDutyAsync</c> (JE + แถวนำส่ง + ยกเลิกได้) ·
/// <see cref="IStampDutyService.MarkPaidAsync"/> เหลือไว้สำหรับ "บันทึกเลขใบเสร็จอย่างเดียว ไม่ลงบัญชี" (ผู้ใช้เลือกเอง — เช่นลงค่าอากรผ่านใบสำคัญจ่ายแล้ว)
/// </summary>
public interface IStampDutyService
{
    decimal Compute(int rdScheduleNumber, decimal instrumentValue, int? leaseYears = null);

    Task<StampDutyRecord> CreateAsync(Guid companyId, string reference,
        Guid? contactId, int rdScheduleNumber, string instrumentType,
        decimal instrumentValue, DateTime instrumentDate,
        int? leaseYears, string paymentMethod,
        Guid? projectId = null, CancellationToken ct = default);

    Task<StampDutyRecord> MarkPaidAsync(Guid companyId, Guid recordId,
        string rdReceiptNumber, DateTime paidAt, CancellationToken ct = default, string performedBy = "");

    Task<IReadOnlyList<StampDutyRecord>> ListUnpaidAsync(Guid companyId,
        CancellationToken ct = default);
}

public class StampDutyService : IStampDutyService
{
    private readonly AccountingDbContext _db;

    public StampDutyService(AccountingDbContext db) { _db = db; }

    /// <summary>รอบ 228 — อัตราย้ายไปตัวตั้งเดียว <see cref="Accounting.Helpers.StampDutySchedule.Compute"/> (เดิมปัดลง + เพดานที่บัญชีอัตราไม่มี)</summary>
    public decimal Compute(int rdScheduleNumber, decimal instrumentValue, int? leaseYears = null)
    {
        return Accounting.Helpers.StampDutySchedule.Compute(rdScheduleNumber, instrumentValue, leaseYears);
    }

    public async Task<StampDutyRecord> CreateAsync(Guid companyId, string reference,
        Guid? contactId, int rdScheduleNumber, string instrumentType,
        decimal instrumentValue, DateTime instrumentDate,
        int? leaseYears, string paymentMethod,
        Guid? projectId = null, CancellationToken ct = default)
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
            ProjectId = projectId,
        };
        _db.StampDutyRecords.Add(record);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<StampDutyRecord> MarkPaidAsync(Guid companyId, Guid recordId,
        string rdReceiptNumber, DateTime paidAt, CancellationToken ct = default, string performedBy = "")
    {
        if (string.IsNullOrWhiteSpace(rdReceiptNumber))
            throw new InvalidOperationException("กรอกเลขที่ใบเสร็จ/เลขอ้างอิง e-Stamp");
        // ฝ่ายค้าน S9c: ล็อกแถวเดียวกับเส้นลงบัญชี (RemitStampDutyAsync) ⇒ กดสองทางพร้อมกันไม่ทับกัน
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"StampDutyRecords\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", new object[] { recordId, companyId }, ct);
        var record = await _db.StampDutyRecords.FirstOrDefaultAsync(
            r => r.Id == recordId && r.CompanyId == companyId && !r.IsDeleted, ct);
        if (record == null) throw new InvalidOperationException("ไม่พบรายการอากรแสตมป์");
        // รอบ 228 (ฝ่ายค้าน S9b): ที่ลงบัญชีผ่านเส้นนำส่งแล้ว (มีแถวนำส่ง) ห้ามเขียนทับ — ยกเลิกรายการนำส่งก่อน · แบบบันทึกเลขใบเสร็จอย่างเดียวแก้ได้
        if (record.StatutoryRemittanceId != null)
            throw new InvalidOperationException($"อากร {record.Reference} บันทึกจ่ายและลงบัญชีแล้ว — ถ้าจะแก้ ให้ยกเลิกรายการนำส่งในประวัติหน้านำส่งก่อน");
        record.PaidAt = paidAt;
        record.RdReceiptNumber = rdReceiptNumber.Trim();
        record.UpdatedBy = performedBy;
        record.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
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
