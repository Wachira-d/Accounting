using Accounting.Models.Entities;

namespace Accounting.Services.Interfaces;

/// <summary>
/// ระบบส่งอีเมลอัตโนมัติตามกฎ — เรียกจาก service events:
///   DocumentService.ApproveDocumentAsync → OnDocumentApprovedAsync
///   PayrollService.ProcessPaymentAsync   → OnPayrollPaidAsync
///   WhtCertService.IssueAsync            → OnWhtCertIssuedAsync
///   Background daily scan                → ScanDueSoonAndOverdueAsync
/// แต่ละ method จะเช็คกฎที่ตรงกัน, render template, enqueue email queue.
/// Worker (EmailScheduleWorker) ดึงคิวมาส่งทีหลัง.
/// </summary>
public interface IEmailScheduleService
{
    Task OnDocumentApprovedAsync(Guid companyId, Guid documentId, CancellationToken ct = default);
    Task OnPayrollPaidAsync(Guid companyId, Guid payrollRunId, CancellationToken ct = default);
    Task OnWhtCertIssuedAsync(Guid companyId, Guid certId, CancellationToken ct = default);

    /// <summary>Recurring สร้าง document ใหม่ — รวมทั้งแบบ auto-approve
    /// และไม่ approve. ใช้ trigger RecurringInvoiceCreated เพื่อแยกจาก
    /// DocumentApproved (กฎเดียวกันส่งซ้ำได้เพราะ idempotency key ต่างกัน).</summary>
    Task OnRecurringDocumentCreatedAsync(Guid companyId, Guid documentId, Guid recurringId, CancellationToken ct = default);

    /// <summary>ใช้โดย background service ทุกชั่วโมง — สแกนเอกสารที่
    /// ครบกำหนดใน N วัน / เกินกำหนด N วัน แล้ว enqueue.</summary>
    Task<int> ScanDueSoonAndOverdueAsync(CancellationToken ct = default);

    /// <summary>ใช้โดย background service ทุกชั่วโมง — สแกนกฎ
    /// MonthlyStatement และส่งสรุปรายการลูกค้าของเดือนที่แล้วในวันที่
    /// (DayOfMonth + SendAtHour) ของเดือนปัจจุบัน. คืนจำนวน statement
    /// ที่ enqueue.</summary>
    Task<int> ScanMonthlyStatementsAsync(CancellationToken ct = default);

    /// <summary>ดึงคิวที่ถึงเวลา + ส่งให้ EmailService — เรียกโดย worker.
    /// คืนจำนวนที่ส่งสำเร็จ.</summary>
    Task<int> ProcessPendingQueueAsync(CancellationToken ct = default);

    // CRUD rules
    Task<List<EmailScheduleRule>> GetRulesAsync(Guid companyId);
    Task<EmailScheduleRule> UpsertRuleAsync(Guid companyId, EmailScheduleRule rule, string actor);
    Task DeleteRuleAsync(Guid companyId, Guid ruleId);

    Task<(int Pending, int Sent, int Failed)> GetQueueStatsAsync(Guid companyId);
    Task<List<EmailQueue>> GetRecentQueueAsync(Guid companyId, int take = 50);
    Task RetryFailedAsync(Guid companyId, Guid queueId);
    Task CancelPendingAsync(Guid companyId, Guid queueId);
}
