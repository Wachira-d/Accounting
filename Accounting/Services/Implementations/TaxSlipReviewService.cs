using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 272 แพ็ก P5 (ดีไซน์ §4.2 "ตัวตรวจทานหลังนำส่ง" · คำตัดสินเจ้าของ 272-0-1) — แถวนำส่งที่บันทึกจากสลิป (<c>Source=Slip</c>) เทียบกับรายการเดินบัญชีของบริษัทลูกค้า:
/// ผูกกับ JE แล้ว หรือมีเงินออกยอดตรงทุกสตางค์ในบัญชีที่จ่าย ±3 วัน ⇒ <c>StatementFound</c> · พบในบัญชีอื่น ⇒ <c>OtherAccount</c> · พ้น 10 วันไม่พบ ⇒ <c>NotFound</c> ·
/// <b>เป็นธงเตือนเท่านั้น</b> (ไม่กลับรายการเอง · ไม่ผูก MatchedJournalEntryId เอง) + กระดิ่งพนักงานของผู้ให้บริการ · แถวที่ประทับแล้ว = watermark (ไม่สแกนซ้ำ)
/// </summary>
public class TaxSlipReviewService
{
    public const int WindowDays = 3;
    public const int NotFoundAfterDays = 10;

    private readonly AccountingDbContext _db;
    private readonly ILogger<TaxSlipReviewService> _logger;
    private readonly INotificationEngine? _notify;

    public TaxSlipReviewService(AccountingDbContext db, ILogger<TaxSlipReviewService> logger, INotificationEngine? notify = null)
    { _db = db; _logger = logger; _notify = notify; }

    /// <summary>ผลตรวจ (pure) — <paramref name="sameAccount"/>/<paramref name="otherAccount"/> = มีเงินออกยอดตรงในช่วงวัน · <paramref name="linked"/> = ผูก JE แล้ว</summary>
    /// <summary>รอบ 275 แพ็ก D (M8): ผล "ไม่พบเงินออก" ตรวจซ้ำได้ภายในกี่วันหลังวันจ่าย (รายการเดินบัญชีที่นำเข้ารายเดือนมาถึงทีหลัง)</summary>
    public const int RecheckNotFoundDays = 60;

    /// <param name="statementCovered">รอบ 275 แพ็ก D (M8): มีรายการเดินบัญชีของบริษัท<b>หลัง</b>ช่วงตรวจแล้ว (นำเข้าถึงช่วงนั้นแล้ว) — ยังไม่มี ⇒ "ยังไม่ได้ตรวจ" ไม่ใช่ "ไม่พบ"</param>
    public static string? Verdict(bool linked, bool sameAccount, bool otherAccount, DateTime payDate, DateTime nowUtc, bool statementCovered = true)
    {
        if (linked || sameAccount) return TaxSlipRemitView.Review.StatementFound;
        if (otherAccount) return TaxSlipRemitView.Review.OtherAccount;
        if (!statementCovered) return null;
        return nowUtc.Date > payDate.Date.AddDays(NotFoundAfterDays) ? TaxSlipRemitView.Review.NotFound : null;
    }

    public async Task<int> ReviewAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var recheckFrom = nowUtc.AddDays(-RecheckNotFoundDays);
        var rows = await _db.StatutoryRemittances.Where(r => r.Source == TaxSlipRemitView.SourceSlip && !r.IsDeleted && r.PayDate < nowUtc
                && (r.ReviewStatus == null || (r.ReviewStatus == TaxSlipRemitView.Review.NotFound && r.PayDate >= recheckFrom)))
            .OrderBy(r => r.ReviewStatus != null).ThenBy(r => r.PayDate).Take(200).ToListAsync(ct);   // แถวที่ยังไม่เคยตรวจก่อน (ตรวจซ้ำไม่บังแถวใหม่)
        var n = 0;
        foreach (var r in rows)
        {
            var total = r.Amount + r.LateFee + r.Penalty + r.OtherFine;
            var from = r.PayDate.Date.AddDays(-WindowDays);
            var to = r.PayDate.Date.AddDays(WindowDays + 1);
            var linked = r.JournalEntryId is Guid je && await _db.BankTransactions.AsNoTracking().AnyAsync(t => t.CompanyId == r.CompanyId && t.MatchedJournalEntryId == je, ct);
            var bank = r.BankGlAccountId is Guid gl
                ? await _db.BankAccounts.AsNoTracking().Where(b => b.CompanyId == r.CompanyId && b.LinkedAccountId == gl).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct) : null;
            var hits = await _db.BankTransactions.AsNoTracking()
                .Where(t => t.CompanyId == r.CompanyId && t.TransactionDate >= from && t.TransactionDate < to
                    && (t.TransactionType == BankTransactionType.Withdrawal || t.Amount < 0) && (t.Amount == total || t.Amount == -total))
                .Select(t => t.BankAccountId).ToListAsync(ct);
            var covered = await _db.BankTransactions.AsNoTracking().AnyAsync(t => t.CompanyId == r.CompanyId && t.TransactionDate >= to, ct);
            var verdict = Verdict(linked, bank != null && hits.Contains(bank.Value), hits.Any(h => h != bank), r.PayDate, nowUtc, covered);
            // ตรวจซ้ำแล้วผลเดิม ⇒ ไม่ประทับซ้ำ/ไม่กระดิ่งซ้ำ
            if (verdict == null || verdict == r.ReviewStatus) continue;
            r.ReviewStatus = verdict;
            r.ReviewedAt = nowUtc;
            r.ReviewNote = verdict switch
            {
                TaxSlipRemitView.Review.StatementFound => linked ? "ผูกกับรายการเดินบัญชีแล้ว" : "พบเงินออกยอดตรงในบัญชีที่จ่าย ±" + WindowDays + " วัน",
                TaxSlipRemitView.Review.OtherAccount => "พบเงินออกยอดตรงในบัญชีอื่นของบริษัท — บัญชีที่บันทึกอาจผิด",
                _ => "ไม่พบเงินออกยอดนี้ในรายการเดินบัญชีภายใน " + NotFoundAfterDays + " วันหลังวันที่สลิป",
            };
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = r.CompanyId, Action = AuditAction.Update, EntityType = nameof(StatutoryRemittance), EntityId = r.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new { @event = "TaxSlipRemitReviewed", verdict, note = r.ReviewNote }), Timestamp = nowUtc,
            });
            await _db.SaveChangesAsync(ct);
            n++;
            if (verdict != TaxSlipRemitView.Review.StatementFound && r.ActedByProviderCompanyId is Guid pid && _notify != null)
            {
                try
                {
                    await _notify.DispatchAsync(pid, NotificationEvents.TaxSlipNeedsStaff, new NotificationContext
                    {
                        Title = "⚠ ตรวจบัญชีที่จ่ายของการนำส่งจากสลิป", Message = r.ReviewNote + $" (ยอด {total:N2} บาท)",
                        ActionUrl = "/pages/tax-slip-desk.html", EntityType = nameof(StatutoryRemittance), EntityId = r.Id,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "แจ้งผลตรวจทานนำส่ง {Id} ไม่สำเร็จ", r.Id); }
            }
        }
        return n;
    }
}
