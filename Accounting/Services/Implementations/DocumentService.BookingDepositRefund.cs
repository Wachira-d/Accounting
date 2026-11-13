using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 270 (ฝ่ายค้าน 268 L7 · คำตัดสิน 270-1-1) — คืนมัดจำของการจองนัดหมายที่รับหลายงวด: <b>ขาเงินออกตามบัญชีที่เงินแต่ละงวดอยู่จริง</b> ·
/// ตัวแบ่งเดียว <see cref="BookingDepositInstalmentChannel.PlanRefund"/> (pure) · ที่นี่แค่โหลดข้อเท็จจริง (อ่านอย่างเดียว · tenant ทุก query)
/// <para>งวดสุดท้าย = ผังของหัวใบ (ใบเสร็จมัดจำลงตามงวดนั้น) · งวดก่อนหน้าที่ประทับช่องทาง = ผังของงวดนั้น <b>เมื่อ JE ย้ายเงิน <c>BKDEP-</c> ลงแล้ว</b>
/// (หรือผังเดียวกับใบ) · ประทับแต่ JE ย้ายไม่มี / ไม่ประทับ (ก่อนรอบ 268) = ไม่รู้ ⇒ ผู้ใช้เลือก · ใบที่ไม่ผูกการจองหรือไม่มีแถวรับเงิน ⇒ null (เส้นเดิม)</para>
/// </summary>
public partial class DocumentService
{
    private async Task<BookingDepositInstalmentChannel.RefundPlan?> BookingDepositRefundPlanAsync(Guid companyId, Document doc, decimal amount)
    {
        var bookingId = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.ErpDocumentId == doc.Id && !b.IsDeleted)
            .OrderBy(b => b.CreatedAt).Select(b => (Guid?)b.Id).FirstOrDefaultAsync();
        if (bookingId == null) return null;
        var rows = await _db.SiteBookingPayments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.BookingId == bookingId && !p.IsDeleted && !p.IsRefund && p.Status == SitePaymentStatus.Completed)
            .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Amount, p.ChannelStamped, p.BankAccountId, p.MoneyInAccountId })
            .ToListAsync();
        if (rows.Count == 0) return null;
        const string hint = "แก้บัญชีธนาคาร/ผังที่หน้า “บัญชีธนาคาร” หรือเลือกบัญชีที่จ่ายคืนเอง";
        var headerGl = await DocumentMoneyChannel.ResolvePostingAccountAsync(_db, companyId, doc.BankAccountId, doc.PaymentAccountId, hint)
            ?? await DocumentMoneyChannel.DefaultCashAccountIdAsync(_db, companyId);
        var moved = (await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && j.SourceDocumentId == doc.Id && !j.IsDeleted && j.Status == JournalEntryStatus.Posted
                    && j.ReversedByEntryId == null && j.Reference != null && j.Reference.StartsWith("BKDEP-"))
                .Select(j => j.Reference!).ToListAsync()).ToHashSet();
        var pools = new List<BookingDepositInstalmentChannel.RefundPool>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            Guid? acc;
            if (i == rows.Count - 1) acc = headerGl;
            else if (!r.ChannelStamped) acc = null;
            else
            {
                var gl = await DocumentMoneyChannel.ResolvePostingAccountAsync(_db, companyId, r.MoneyInAccountId == null ? r.BankAccountId : null, r.MoneyInAccountId, hint)
                    ?? await DocumentMoneyChannel.DefaultCashAccountIdAsync(_db, companyId);
                acc = gl == headerGl ? headerGl : moved.Contains(BookingDepositInstalmentChannel.ReferenceFor(r.Id)) ? gl : null;
            }
            pools.Add(new BookingDepositInstalmentChannel.RefundPool(acc, r.Amount));
        }
        var refunded = await (from l in _db.JournalEntryLines.AsNoTracking()
                              join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                              where j.CompanyId == companyId && j.SourceDocumentId == doc.Id && !j.IsDeleted && !l.IsDeleted
                                    && j.Status == JournalEntryStatus.Posted && j.ReversedByEntryId == null && j.OriginalEntryId == null
                                    && l.CreditAmount > 0m && l.Description == BookingDepositInstalmentChannel.RefundMoneyLineDescription
                              group l by l.AccountId into g
                              select new { g.Key, Amount = g.Sum(x => x.CreditAmount) })
            .ToDictionaryAsync(x => x.Key, x => x.Amount);
        return BookingDepositInstalmentChannel.PlanRefund(pools, refunded, doc.DepositRefundedAmount, amount);
    }
}
