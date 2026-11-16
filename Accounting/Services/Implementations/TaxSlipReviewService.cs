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
    public static string? Verdict(bool linked, bool sameAccount, bool otherAccount, DateTime payDate, DateTime nowUtc)
    {
        if (linked || sameAccount) return TaxSlipRemitView.Review.StatementFound;
        if (otherAccount) return TaxSlipRemitView.Review.OtherAccount;
        return nowUtc.Date > payDate.Date.AddDays(NotFoundAfterDays) ? TaxSlipRemitView.Review.NotFound : null;
    }

    public async Task<int> ReviewAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var rows = await _db.StatutoryRemittances.Where(r => r.Source == TaxSlipRemitView.SourceSlip && r.ReviewStatus == null && !r.IsDeleted && r.PayDate < nowUtc)
            .OrderBy(r => r.PayDate).Take(200).ToListAsync(ct);
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
            var verdict = Verdict(linked, bank != null && hits.Contains(bank.Value), hits.Any(h => h != bank), r.PayDate, nowUtc);
            if (verdict == null) continue;
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
