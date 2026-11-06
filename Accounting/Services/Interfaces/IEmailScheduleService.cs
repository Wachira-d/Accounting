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
    /// DocumentApproved.
    /// <para>รอบ 250 (WP-02 §2.4 ข้อ 1/3): คืนผล Enqueued/Skipped(เหตุ)/Failed(เหตุ) — ไม่โยน · ผู้เรียกตั้งธง <c>EmailEnqueueFailed</c> จากผล ·
    /// มีกฎ "เมื่ออนุมัติเอกสาร" ครอบชนิดนี้และใบอนุมัติแล้ว ⇒ Skipped (กฎนั้นเข้าคิวไปแล้วตอนอนุมัติ — กันอีเมลซ้ำ)</para></summary>
    Task<Accounting.Helpers.EmailEnqueueOutcome> OnRecurringDocumentCreatedAsync(Guid companyId, Guid documentId, Guid recurringId, CancellationToken ct = default);

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

    /// <summary>รอบ 254 WP-11 L2 (คำตอบเจ้าของ Q7): LINE ของใบนี้ล้มถาวร ⇒ เข้าคิว<b>อีเมล</b>แทนหนึ่งครั้ง (เส้นเข้าคิวอีเมลเดิมทั้งชุด: ผู้รับตามเรื่อง ·
    /// ไม่ส่งอัตโนมัติ · e-Tax · เพดานผู้รับ) · กุญแจ = กุญแจเหตุการณ์เดิม + ":line-fallback" (ถอยซ้ำไม่ได้) · คืน null = เข้าคิวแล้ว/มีอยู่แล้ว · ข้อความ = เหตุที่ถอยไม่ได้</summary>
    Task<string?> EnqueueLineFallbackEmailAsync(Guid companyId, Guid? ruleId, Guid documentId, string baseKey, string trigger, CancellationToken ct = default);

    // CRUD rules
    Task<List<EmailScheduleRule>> GetRulesAsync(Guid companyId);
    Task<EmailScheduleRule> UpsertRuleAsync(Guid companyId, EmailScheduleRule rule, string actor);
    Task DeleteRuleAsync(Guid companyId, Guid ruleId);

    Task<(int Pending, int Sent, int Failed)> GetQueueStatsAsync(Guid companyId);
    Task<List<EmailQueue>> GetRecentQueueAsync(Guid companyId, int take = 50);
    Task RetryFailedAsync(Guid companyId, Guid queueId);
    Task CancelPendingAsync(Guid companyId, Guid queueId);
}
