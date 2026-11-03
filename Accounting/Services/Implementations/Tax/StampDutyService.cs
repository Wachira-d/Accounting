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
    /// <summary>รอบ 230 — บันทึกเลขใบเสร็จอย่างเดียว (มีวันจ่าย แต่ไม่มีแถวนำส่ง/JE) ⇒ ลงบัญชีทับได้ผ่าน <c>RemitStampDutyAsync</c></summary>
    Task<(IReadOnlyList<StampDutyRecord> Items, int Total)> ListMarkedOnlyAsync(Guid companyId, int page = 1, int pageSize = 50,
        bool includeConfirmed = false, CancellationToken ct = default);
    /// <summary>รอบ 230 N4 — ยืนยัน/ยกเลิกยืนยันว่าค่าอากรของใบนี้ลงบัญชีด้วยมือแล้ว (ซ่อนจากรายการ · กันลงซ้ำ) · audit chain</summary>
    Task<StampDutyRecord> ConfirmManualPostingAsync(Guid companyId, Guid recordId, bool confirmed, string? note, string performedBy,
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

    /// <summary>รอบ 230 (TAX_PENALTY_AUDIT §5 ความเสี่ยงที่เหลือ S3 ข้อสุดท้าย) — เดิมรายการค้างแสดงเฉพาะ <c>PaidAt == null</c> ⇒ ใบที่บันทึกเลขใบเสร็จอย่างเดียวแล้ว
    /// ลงบัญชีทับได้ทาง API แต่ไม่มีทางเข้าบนหน้า · เงื่อนไขเดียวกับด่านใน <c>RemitStampDutyAsync</c> (<c>StatutoryRemittanceId == null</c>)</summary>
    public async Task<(IReadOnlyList<StampDutyRecord> Items, int Total)> ListMarkedOnlyAsync(Guid companyId, int page = 1, int pageSize = 50,
        bool includeConfirmed = false, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var q = _db.StampDutyRecords.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.PaidAt != null && r.StatutoryRemittanceId == null
                && (includeConfirmed || r.ManualPostingConfirmedAt == null));
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(r => r.PaidAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<StampDutyRecord> ConfirmManualPostingAsync(Guid companyId, Guid recordId, bool confirmed, string? note, string performedBy,
        CancellationToken ct = default)
    {
        // ล็อกแถวเดียวกับเส้นลงบัญชี (RemitStampDutyAsync) ⇒ ยืนยันพร้อมกดลงบัญชีไม่ทับกัน
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"StampDutyRecords\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", new object[] { recordId, companyId }, ct);
        var record = await _db.StampDutyRecords.FirstOrDefaultAsync(r => r.Id == recordId && r.CompanyId == companyId && !r.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบรายการอากรแสตมป์");
        if (record.StatutoryRemittanceId != null)
            throw new Accounting.Helpers.BusinessRuleException($"อากร {record.Reference} ลงบัญชีผ่านระบบแล้ว — ไม่ต้องยืนยันลงบัญชีด้วยมือ", "STAMP-MANUAL-ALREADY-POSTED", 409);
        if (record.PaidAt == null)
            throw new Accounting.Helpers.BusinessRuleException($"อากร {record.Reference} ยังไม่ได้บันทึกจ่าย — บันทึกเลขใบเสร็จก่อน", "STAMP-MANUAL-NOT-PAID", 409);
        var before = new { record.ManualPostingConfirmedAt, record.ManualPostingConfirmedBy };
        record.ManualPostingConfirmedAt = confirmed ? DateTime.UtcNow : null;
        record.ManualPostingConfirmedBy = confirmed ? performedBy : null;
        record.UpdatedBy = performedBy;
        record.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = Guid.TryParse(performedBy, out var actor) ? actor : null,
            Action = Accounting.Models.Enums.AuditAction.Update,
            EntityType = "StampDutyRecord",
            EntityId = record.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                Operation = confirmed ? "ManualPostingConfirmed" : "ManualPostingUnconfirmed", record.Reference, record.DutyAmount,
                record.RdReceiptNumber, Note = note?.Trim(),
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return record;
    }
}
