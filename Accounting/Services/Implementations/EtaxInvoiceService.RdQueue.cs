using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 259 (B-M6 · คำตัดสิน 259-4-6) — คิวนำส่ง e-Tax ต่อกรมสรรพากร (โหมดส่งตรง) + การแจ้งเมื่อนำส่งล้ม
/// <para>เดิม: ลงนามแล้วส่งกรมไม่สำเร็จ = <c>LogWarning</c> อย่างเดียว · ไม่มีงานส่งตามกำหนดวันที่ 15 (CLAUDE.md §F "Submission cron … retry queue") ·
/// ตอนนี้: ล้ม = สถานะ/เหตุบนแถว e-Tax + เรื่องค้างหน้า Autopilot (<c>AutopilotService.IssuesAsync</c> "EtaxRdFailed") + กระดิ่ง
/// (<see cref="Accounting.Models.Constants.NotificationEvents.EtaxRdSubmitFailed"/>) · งานประจำเดือน <c>EtaxRdSubmissionJob</c> (JobLock) ส่งใบเดือนก่อนที่ค้างภายในวันที่ 15</para>
/// </summary>
public partial class EtaxInvoiceService
{
    /// <summary>แจ้งทีมเมื่อนำส่งล้ม — แจ้งล้ม = log (เหตุอยู่บนแถว e-Tax + หน้า Autopilot แล้ว · ไม่ทำให้ผู้เรียกล้มซ้อน)</summary>
    private async Task NotifyRdSubmitFailureAsync(EtaxInvoice etax, string reason, EtaxRdSubmission.Outcome outcome)
    {
        if (_notify == null) return;
        try
        {
            var number = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == etax.DocumentId && d.CompanyId == etax.CompanyId)
                .Select(d => d.DocumentNumber).FirstOrDefaultAsync();
            await _notify.DispatchAsync(etax.CompanyId, Accounting.Models.Constants.NotificationEvents.EtaxRdSubmitFailed, new NotificationContext
            {
                Title = EtaxRdSubmission.FailureTitle(number),
                Message = EtaxRdSubmission.FailureMessage(reason, EtaxRdSubmission.DeadlineFor(etax.CreatedAt), outcome),
                ActionUrl = "/pages/etax.html",
                EntityType = "EtaxInvoice",
                EntityId = etax.Id,
            });
        }
        catch (Exception nex) when (nex is not OperationCanceledException)
        {
            _logger.LogWarning(nex, "e-Tax {EtaxRef}: แจ้งเตือนนำส่งล้มไม่สำเร็จ (เหตุอยู่บนแถว e-Tax แล้ว)", etax.EtaxRefNumber);
        }
    }

    /// <summary>งานประจำเดือน: นำส่งใบที่ลงวันที่ก่อนเดือนนี้ (เดือนก่อนและเก่ากว่า) ที่ยังไม่ส่ง/ส่งล้มทางเทคนิค และถึงเวลาลองใหม่ตามคิว ·
    /// ผู้เรียก <c>EtaxRdSubmissionJob</c> (ใต้ JobLock · เฉพาะบริษัทโหมดส่งตรง · วันที่ 1–15) · ไม่ประทับว่าส่งแล้วเอง — ทุกฉบับผ่าน <see cref="SubmitToRevenueAsync"/>
    /// (ประทับ SubmittedAt เฉพาะเมื่อกรมตอบรับจริง) · ผลรวมแจ้งทีม 1 ครั้งต่อรอบที่มีงาน · กรองบริษัททุก query</summary>
    public async Task<EtaxRdBatchResult> SubmitPendingPreviousMonthsAsync(Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var cutoff = EtaxRdSubmission.CurrentMonthStart(nowUtc);
        var ids = await _db.EtaxInvoices.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.SubmittedAt == null
                && (e.Status == EtaxStatus.Signed
                    || (e.Status == EtaxStatus.Error && e.SignedAt != null && e.DigitalSignature != null && e.ErrorCode != EtaxRdSubmission.ErrorSign))
                && (e.RdNextAttemptAt == null || e.RdNextAttemptAt <= nowUtc)
                && _db.Documents.Any(d => d.Id == e.DocumentId && d.CompanyId == companyId && d.DocumentDate < cutoff))
            .OrderBy(e => e.CreatedAt)
            .Select(e => e.Id)
            .Take(EtaxRdSubmission.BatchPerCompany)
            .ToListAsync(ct);
        int ok = 0, failed = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SubmitToRevenueAsync(companyId, id);
                ok++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // เหตุอยู่บนแถว e-Tax แล้ว (SubmitToRevenueAsync ประทับ + แจ้งครั้งแรก) — นับในสรุป ไปฉบับถัดไป
                failed++;
                _logger.LogInformation("e-Tax {Id}: งานประจำเดือนนำส่งไม่สำเร็จ — {Reason}", id, ex.Message);
            }
            _db.ChangeTracker.Clear();
        }
        var deadline = EtaxRdSubmission.DeadlineFor(cutoff.AddDays(-1));
        if (EtaxRdSubmission.JobSummary(ids.Count, ok, failed, deadline) is { } summary && _notify != null)
        {
            try
            {
                await _notify.DispatchAsync(companyId, Accounting.Models.Constants.NotificationEvents.EtaxRdSubmitFailed, new NotificationContext
                {
                    Title = failed > 0 ? "นำส่ง e-Tax ประจำเดือน: มีฉบับที่ยังไม่สำเร็จ" : "นำส่ง e-Tax ประจำเดือนสำเร็จ",
                    Message = summary,
                    ActionUrl = failed > 0 ? "/pages/autopilot.html" : "/pages/etax.html",
                    EntityType = "Company",
                    EntityId = companyId,
                });
            }
            catch (Exception nex) when (nex is not OperationCanceledException)
            {
                _logger.LogWarning(nex, "e-Tax batch summary notification failed for {Company}", companyId);
            }
        }
        return new EtaxRdBatchResult(ids.Count, ok, failed);
    }
}

/// <summary>ผลงานนำส่งประจำเดือนของบริษัทหนึ่ง</summary>
public sealed record EtaxRdBatchResult(int Attempted, int Succeeded, int Failed);
