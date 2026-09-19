using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
// Alias the Cheque entity — the file's own namespace
// (Accounting.Services.Implementations.Cheque) shadows the unqualified
// name "Cheque" via outer-namespace resolution, so we use this alias
// in method signatures + var initialisers to disambiguate.
using ChequeEntity = Accounting.Models.Entities.Cheque;

namespace Accounting.Services.Implementations.Cheque;

/// <summary>
/// Cheque lifecycle management — Thai SMEs still use cheques heavily
/// for vendor payments and customer collections. This service owns:
///
///   • ChequeBook ordering + sequence integrity (the bank gives us
///     a roll of 25/50/100 pre-printed cheques; we issue them in
///     order; missing numbers are flagged unless explicitly Voided).
///   • Outbound cheque issuance — link to a Payment + a contact.
///   • Inbound cheque deposit — customer hands us a cheque; we record
///     it as DepositedPending until it clears.
///   • State transitions — Issued → Cleared | Bounced.
///   • Bounce + replacement chain — when a cheque bounces, the
///     replacement carries ReplacesChequeId for audit.
///   • Outstanding-cheques report — AP team's "what cheques are still
///     out there" view that drives bank rec.
/// </summary>
public interface IChequeService
{
    Task<ChequeBook> OpenChequeBookAsync(Guid companyId, Guid bankAccountId,
        string bookNumber, long startNumber, long endNumber, CancellationToken ct = default);

    Task<ChequeEntity> IssueOutboundAsync(Guid companyId, Guid chequeBookId,
        Guid? contactId, DateTime chequeDate, decimal amount, Guid? paymentId,
        string? notes, Guid? projectId = null, CancellationToken ct = default);

    Task<ChequeEntity> RecordInboundAsync(Guid companyId, Guid? contactId,
        long chequeNumber, string issuingBank, DateTime chequeDate,
        decimal amount, Guid? paymentId, string? notes,
        Guid? projectId = null,
        /// <summary>Our bank account that the cheque is deposited into.
        /// When omitted, MarkCleared logs but doesn't update bank balance —
        /// caller can patch later. Strongly encouraged for proper bank rec.</summary>
        Guid? depositBankAccountId = null,
        CancellationToken ct = default);

    Task<ChequeEntity> MarkClearedAsync(Guid companyId, Guid chequeId,
        DateTime clearedAt, CancellationToken ct = default);

    Task<ChequeEntity> MarkBouncedAsync(Guid companyId, Guid chequeId,
        string reason, CancellationToken ct = default);

    Task<ChequeEntity> VoidAsync(Guid companyId, Guid chequeId,
        string? notes, CancellationToken ct = default);

    Task<IReadOnlyList<ChequeEntity>> ListOutstandingAsync(Guid companyId,
        Guid? bankAccountId, CancellationToken ct = default);
}

public class ChequeService : IChequeService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<ChequeService> _logger;
    private readonly Accounting.Services.Interfaces.IWebhookService? _webhooks;
    /// <summary>ใช้กลับรายการชำระตอนเช็คเด้ง — เส้นกลับรายการ (JE + สถานะใบ +
    /// ยอดธนาคาร + ปลดการกระทบยอด) อยู่ที่ `DocumentService.VoidPaymentAsync`
    /// ที่เดียว ห้ามเขียนซ้ำที่นี่ (ตัวตั้งตัวเดียว — F2 ข้อ 4)</summary>
    private readonly Accounting.Services.Interfaces.IDocumentService? _documents;

    public ChequeService(AccountingDbContext db, ILogger<ChequeService> logger,
        Accounting.Services.Interfaces.IWebhookService? webhooks = null,
        Accounting.Services.Interfaces.IDocumentService? documents = null)
    { _db = db; _logger = logger; _webhooks = webhooks; _documents = documents; }

    private async Task FireAsync(Guid companyId, string eventType, object payload)
    {
        if (_webhooks == null) return;
        try { await _webhooks.TriggerAsync(companyId, eventType, payload); }
        catch { /* fire-and-forget */ }
    }

    public async Task<ChequeBook> OpenChequeBookAsync(Guid companyId, Guid bankAccountId,
        string bookNumber, long startNumber, long endNumber, CancellationToken ct = default)
    {
        if (endNumber < startNumber)
            throw new ArgumentException("EndChequeNumber must be ≥ StartChequeNumber.");
        var book = new ChequeBook
        {
            CompanyId = companyId,
            BankAccountId = bankAccountId,
            BookNumber = bookNumber,
            StartChequeNumber = startNumber,
            EndChequeNumber = endNumber,
            NextNumber = startNumber,
            ReceivedFromBankAt = DateTime.UtcNow,
        };
        _db.ChequeBooks.Add(book);
        await _db.SaveChangesAsync(ct);
        return book;
    }

    public async Task<ChequeEntity> IssueOutboundAsync(Guid companyId, Guid chequeBookId,
        Guid? contactId, DateTime chequeDate, decimal amount, Guid? paymentId,
        string? notes, Guid? projectId = null, CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentException("Amount must be positive.");
        // Take the book under tracking so NextNumber updates atomically.
        var book = await _db.ChequeBooks.FirstOrDefaultAsync(b => b.Id == chequeBookId
            && b.CompanyId == companyId, ct);
        if (book == null) throw new InvalidOperationException("ChequeBook not found.");
        if (book.NextNumber > book.EndChequeNumber)
            throw new InvalidOperationException("ChequeBook exhausted — open a new book first.");

        var cheque = new ChequeEntity
        {
            CompanyId = companyId,
            ChequeBookId = book.Id,
            ChequeNumber = book.NextNumber,
            ChequeDate = chequeDate,
            Amount = amount,
            ContactId = contactId,
            PaymentId = paymentId,
            Status = ChequeStatus.Issued,
            IsInbound = false,
            Notes = notes,
            ProjectId = projectId,
        };
        _db.Cheques.Add(cheque);
        book.NextNumber++;
        if (book.NextNumber > book.EndChequeNumber)
            book.ExhaustedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return cheque;
    }

    public async Task<ChequeEntity> RecordInboundAsync(Guid companyId, Guid? contactId,
        long chequeNumber, string issuingBank, DateTime chequeDate,
        decimal amount, Guid? paymentId, string? notes,
        Guid? projectId = null,
        Guid? depositBankAccountId = null, CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentException("Amount must be positive.");
        var cheque = new ChequeEntity
        {
            CompanyId = companyId,
            ChequeBookId = null,                 // not from OUR book
            ChequeNumber = chequeNumber,
            ChequeDate = chequeDate,
            Amount = amount,
            ContactId = contactId,
            IssuingBankName = issuingBank,
            PaymentId = paymentId,
            Status = ChequeStatus.DepositedPending,
            IsInbound = true,
            Notes = notes,
            ProjectId = projectId,
            DepositBankAccountId = depositBankAccountId,
        };
        _db.Cheques.Add(cheque);
        await _db.SaveChangesAsync(ct);
        return cheque;
    }

    public async Task<ChequeEntity> MarkClearedAsync(Guid companyId, Guid chequeId,
        DateTime clearedAt, CancellationToken ct = default)
    {
        var cheque = await _db.Cheques
            .Include(c => c.ChequeBook).ThenInclude(b => b!.BankAccount)
            .Include(c => c.DepositBankAccount)
            .FirstOrDefaultAsync(c => c.Id == chequeId && c.CompanyId == companyId, ct);
        if (cheque == null) throw new InvalidOperationException("Cheque not found.");
        if (cheque.Status != ChequeStatus.Issued && cheque.Status != ChequeStatus.DepositedPending)
            throw new InvalidOperationException($"Cheque {cheque.Status} cannot be cleared.");
        cheque.Status = ChequeStatus.Cleared;
        cheque.ClearedAt = clearedAt;

        // Float settles — move money. Outbound debits the cheque book's
        // BankAccount; inbound credits the DepositBankAccount the
        // operator specified at deposit time. Re-clearing is blocked by
        // the status guard above so this is idempotent on its own.
        // When the inbound deposit bank wasn't recorded, we log + skip
        // the balance side rather than silently mismatch.
        //
        // กันนับซ้ำ: ถ้าเช็คผูกกับ Payment ที่บันทึกพร้อม BankAccountId —
        // CreatePaymentAsync ปรับ CurrentBalance ไปแล้วตอนบันทึกรับ/จ่าย
        // การ clear เช็คห้ามปรับซ้ำ (ไม่งั้นยอดธนาคารบวก/ลบสองรอบจากเงินก้อนเดียว)
        var paymentAlreadyMovedBalance = false;
        if (cheque.PaymentId.HasValue)
        {
            paymentAlreadyMovedBalance = await _db.Payments.AnyAsync(p =>
                p.Id == cheque.PaymentId.Value && p.CompanyId == companyId
                && !p.IsDeleted && p.BankAccountId != null, ct);
        }

        var bank = cheque.IsInbound
            ? cheque.DepositBankAccount
            : cheque.ChequeBook?.BankAccount;
        if (paymentAlreadyMovedBalance)
        {
            _logger.LogInformation(
                "Cheque {Id} cleared — balance already moved by linked payment {PaymentId}, skipping double adjustment",
                chequeId, cheque.PaymentId);
        }
        else if (bank != null)
        {
            if (cheque.IsInbound) bank.CurrentBalance += cheque.Amount;
            else                  bank.CurrentBalance -= cheque.Amount;
        }
        else
        {
            // ⚠ เดิม `LogWarning` แล้วผ่าน ⇒ เช็คขึ้นสถานะ "ขึ้นเงินแล้ว"
            // โดย**ยอดธนาคารไม่ขยับและไม่มีใครรู้** (`DECISION_AUDIT` §3 D4-6 ·
            // F2 ข้อ 7 "LogWarning ไม่ใช่การดัง"). ทางไปต่อของผู้ใช้ชัดเจน:
            // ระบุบัญชีที่นำฝาก/บัญชีของสมุดเช็ค แล้วกดใหม่
            throw new InvalidOperationException(
                cheque.IsInbound
                    ? "เช็ครับใบนี้ยังไม่ได้ระบุบัญชีที่นำฝาก — ระบุบัญชีธนาคารที่นำเช็คเข้า "
                      + "ก่อนกด “ขึ้นเงินแล้ว” มิฉะนั้นยอดธนาคารจะไม่ขยับตามเงินจริง"
                    : "เช็คจ่ายใบนี้ไม่พบบัญชีธนาคารของสมุดเช็ค — ตั้งค่าบัญชีธนาคารของสมุดเช็ค "
                      + "ก่อนกด “ขึ้นเงินแล้ว”");
        }

        await _db.SaveChangesAsync(ct);
        await FireAsync(companyId, "cheque.cleared", new
        {
            id = cheque.Id, chequeNumber = cheque.ChequeNumber,
            amount = cheque.Amount, isInbound = cheque.IsInbound,
            clearedAt = cheque.ClearedAt,
            bankBalanceAfter = bank?.CurrentBalance,
        });
        return cheque;
    }

    /// <summary>
    /// เช็คเด้ง — **เงินไม่เคยเปลี่ยนมือ** ⇒ ต้องถอยรายการบัญชีที่ลงไว้ด้วย
    /// ไม่ใช่เปลี่ยนแค่สถานะ (`DECISION_AUDIT_2026-09-18.md` §3 D4-6):
    /// เดิมเมธอดนี้ตั้ง `Status = Bounced` + เหตุผล แล้วจบ ⇒ `Payment` ยังอยู่ ·
    /// ใบยังเป็น "ชำระแล้ว" · JE ยังอยู่ ⇒ **เงินที่ไม่เคยเข้ายังอยู่ในบัญชี**
    ///
    /// สิ่งที่ต้องถอยตัดสินที่ <see cref="Accounting.Helpers.ChequeBouncePlan"/>
    /// (pure + มีเทสต์) และการถอยจริงเดินผ่าน `VoidPaymentAsync` ตัวเดียว
    /// (กลับ JE · คืนยอดธนาคาร · คืนสถานะใบ · ปลดการกระทบยอด — ครบในที่เดียว)
    ///
    /// **เช็คที่ขึ้นเงินไปแล้วก็เด้งได้** — ธนาคารคืนเช็คหลังให้เครดิตชั่วคราว
    /// เป็นเรื่องปกติ. เดิมบล็อกไว้ ⇒ ผู้ใช้ **ไม่มีทางบันทึกเหตุการณ์นี้เลย**
    /// (ด่านที่ไม่มีทางไปต่อ — กฎเหล็ก #4 F2 ข้อ 8)
    /// </summary>
    public async Task<ChequeEntity> MarkBouncedAsync(Guid companyId, Guid chequeId,
        string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException(
                "กรุณาระบุเหตุผลที่เช็คเด้ง (เช่น เงินในบัญชีไม่พอ / ปิดบัญชี / ลายเซ็นไม่ตรง)");

        var cheque = await _db.Cheques
            .Include(c => c.ChequeBook).ThenInclude(b => b!.BankAccount)
            .Include(c => c.DepositBankAccount)
            .FirstOrDefaultAsync(c => c.Id == chequeId && c.CompanyId == companyId, ct);
        if (cheque == null) throw new InvalidOperationException("Cheque not found.");
        if (cheque.Status == ChequeStatus.Bounced)
            return cheque;                       // idempotent — กดซ้ำไม่ถอยซ้ำ
        if (cheque.Status == ChequeStatus.Voided)
            throw new InvalidOperationException("เช็คที่ยกเลิกแล้วไม่สามารถทำรายการเด้งได้");

        var plan = Accounting.Helpers.ChequeBouncePlan.Decide(
            cheque.IsInbound,
            wasCleared: cheque.Status == ChequeStatus.Cleared,
            hasLinkedPayment: cheque.PaymentId.HasValue);

        // ── ถอยการชำระ (JE + สถานะใบ + ยอดธนาคาร + ปลดกระทบยอด) ────────────
        // ⚠ ห้ามกลืน error ในเส้นเงิน (กฎเหล็ก #4 E) — ถอยไม่สำเร็จแปลว่า
        // งบยังถือเงินที่ไม่มีจริง ⇒ ต้องล้มทั้งรายการ ไม่ใช่ประทับ Bounced
        // แล้วปล่อยผ่าน
        if (plan.ReversePayment && cheque.PaymentId.HasValue)
        {
            if (_documents == null)
                throw new InvalidOperationException(
                    "ระบบยังไม่พร้อมกลับรายการชำระของเช็คใบนี้ — ติดต่อผู้ดูแลระบบ "
                    + "(บันทึกเช็คเด้งโดยไม่กลับรายการจะทำให้งบถือเงินที่ไม่มีจริง)");
            await _documents.VoidPaymentAsync(companyId, cheque.PaymentId.Value);
        }
        else if (plan.RestoreBankBalanceDirectly)
        {
            // เช็คที่ขึ้นเงินเองโดยไม่มีการชำระผูก — ตอน `MarkClearedAsync`
            // มันเป็นคนขยับยอด ⇒ ตอนเด้งก็ต้องเป็นคนคืนยอดเอง (สมมาตร)
            var bank = cheque.IsInbound ? cheque.DepositBankAccount : cheque.ChequeBook?.BankAccount;
            if (bank == null)
                throw new InvalidOperationException(
                    "เช็คใบนี้เคยขึ้นเงินแล้วแต่ไม่พบบัญชีธนาคารที่ผูกไว้ — "
                    + "คืนยอดไม่ได้ ระบุบัญชีธนาคารของเช็คก่อน");
            if (cheque.IsInbound) bank.CurrentBalance -= cheque.Amount;
            else                  bank.CurrentBalance += cheque.Amount;
        }

        cheque.Status = ChequeStatus.Bounced;
        cheque.BounceReason = reason;

        await _db.SaveChangesAsync(ct);
        _logger.LogWarning("Cheque {Id} bounced ({Rule}): {Reason} — {Plan}",
            chequeId, plan.RuleCode, reason, plan.Reason);
        await FireAsync(companyId, "cheque.bounced", new
        {
            id = cheque.Id, chequeNumber = cheque.ChequeNumber,
            amount = cheque.Amount, isInbound = cheque.IsInbound,
            reason = cheque.BounceReason,
            ruleCode = plan.RuleCode,
            reversedPayment = plan.ReversePayment ? cheque.PaymentId : null,
        });
        return cheque;
    }

    public async Task<ChequeEntity> VoidAsync(Guid companyId, Guid chequeId,
        string? notes, CancellationToken ct = default)
    {
        var cheque = await _db.Cheques.FirstOrDefaultAsync(c => c.Id == chequeId
            && c.CompanyId == companyId, ct);
        if (cheque == null) throw new InvalidOperationException("Cheque not found.");
        if (cheque.Status == ChequeStatus.Cleared)
            throw new InvalidOperationException("Cleared cheque cannot be voided.");
        cheque.Status = ChequeStatus.Voided;
        if (!string.IsNullOrWhiteSpace(notes))
            cheque.Notes = (cheque.Notes ?? "") + "\nVOID: " + notes;
        await _db.SaveChangesAsync(ct);
        return cheque;
    }

    public async Task<IReadOnlyList<ChequeEntity>> ListOutstandingAsync(Guid companyId,
        Guid? bankAccountId, CancellationToken ct = default)
    {
        var q = _db.Cheques.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted
                        && (c.Status == ChequeStatus.Issued
                            || c.Status == ChequeStatus.DepositedPending));
        if (bankAccountId.HasValue)
            q = q.Where(c => c.ChequeBook != null && c.ChequeBook.BankAccountId == bankAccountId.Value);
        return await q.OrderBy(c => c.ChequeDate).ToListAsync(ct);
    }
}
