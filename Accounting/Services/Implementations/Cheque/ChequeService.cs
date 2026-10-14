using Accounting.Data;
using Accounting.Helpers;
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

    /// <summary>เล่มเช็คของบริษัท (ล่าสุดก่อน) — หน้า “ออกเช็คใหม่” เลือกเล่มจากรายการนี้ · เดิมไม่มี endpoint ⇒ รีเฟรชหน้าแล้วเล่มที่เปิดไว้หายจากตัวเลือก</summary>
    Task<IReadOnlyList<ChequeBookResponse>> ListBooksAsync(Guid companyId, bool includeExhausted = false, CancellationToken ct = default);
}

/// <summary>เล่มเช็คสำหรับหน้าจอ — ป้ายบัญชีธนาคารมาจากเซิร์ฟเวอร์ (หน้าไม่ต้องจับคู่รหัสเอง)</summary>
public sealed record ChequeBookResponse(Guid Id, string BookNumber, Guid BankAccountId, string BankLabel,
    long StartChequeNumber, long EndChequeNumber, long NextNumber, bool IsExhausted);

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
        if (string.IsNullOrWhiteSpace(bookNumber))
            throw new BusinessRuleException("กรอก “เลขเล่ม” ของเล่มเช็ค", "CHEQUE-BOOK-NUMBER");
        if (startNumber <= 0)
            throw new BusinessRuleException("“เลขเริ่มต้น” ต้องมากกว่า 0", "CHEQUE-BOOK-RANGE");
        if (endNumber < startNumber)
            throw new BusinessRuleException("“เลขสุดท้าย” ต้องไม่น้อยกว่า “เลขเริ่มต้น”", "CHEQUE-BOOK-RANGE");
        // บัญชีธนาคารต้องเป็นของบริษัทนี้และเปิดใช้งาน — เดิมไม่ตรวจ (รหัสบัญชีของบริษัทอื่น/ที่ถูกลบผ่านได้ ·
        // PAYMENT_CHANNEL_AUDIT ส่วนที่ 1 ข้อ 2) · สมุดเช็คขยับแค่ยอดธนาคาร ไม่ลง JE ⇒ ไม่บังคับการผูกผัง
        await PickedAccountGuard.RequireActiveBankAsync(_db, companyId, bankAccountId, "บัญชีธนาคาร", ct);
        var book = new ChequeBook
        {
            CompanyId = companyId,
            BankAccountId = bankAccountId,
            BookNumber = bookNumber.Trim(),
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
        if (amount <= 0) throw new BusinessRuleException("“จำนวนเงิน” ต้องมากกว่า 0", "CHEQUE-AMOUNT");
        // Take the book under tracking so NextNumber updates atomically.
        var book = await _db.ChequeBooks.FirstOrDefaultAsync(b => b.Id == chequeBookId
            && b.CompanyId == companyId && !b.IsDeleted, ct);
        if (book == null)
            throw new BusinessRuleException("ไม่พบเล่มเช็คที่เลือก — เลือก “เล่มเช็ค” ใหม่ หรือเปิดเล่มที่แท็บ “เล่มเช็ค”", "CHEQUE-BOOK-NOT-FOUND");
        if (book.NextNumber > book.EndChequeNumber)
            throw new BusinessRuleException($"เล่มเช็ค {book.BookNumber} ใช้ครบทุกใบแล้ว — เปิดเล่มใหม่ที่แท็บ “เล่มเช็ค”", "CHEQUE-BOOK-EXHAUSTED");
        await RequireContactAsync(companyId, contactId, ct);

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
        if (amount <= 0) throw new BusinessRuleException("“จำนวนเงิน” ต้องมากกว่า 0", "CHEQUE-AMOUNT");
        await RequireContactAsync(companyId, contactId, ct);
        if (PickedAccountGuard.IsSet(depositBankAccountId))
            await PickedAccountGuard.RequireActiveBankAsync(_db, companyId, depositBankAccountId!.Value, "บัญชีที่นำฝาก", ct);
        else depositBankAccountId = null;
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
        if (cheque == null) throw new KeyNotFoundException(ChequeNotFoundMessage);
        if (cheque.Status != ChequeStatus.Issued && cheque.Status != ChequeStatus.DepositedPending)
            throw new BusinessRuleException(
                $"เช็คเลขที่ {cheque.ChequeNumber} อยู่ในสถานะ{StatusLabel(cheque.Status)} — กด “ขึ้นเงินแล้ว” ได้เฉพาะเช็คที่ยังคงค้าง", "CHEQUE-NOT-OUTSTANDING");
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
        if (cheque == null) throw new KeyNotFoundException(ChequeNotFoundMessage);
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
        // รอบ 200 ทีม V1 (คำตัดสินข้อ 11): เช็คเด้ง **ห้ามบล็อก** — กลับรายการเงิน/JE เสมอ · ใบเสร็จอัตโนมัติที่ e-Tax ถึงกรมสรรพากรแล้ว
        // ไม่ถูกประทับ Voided เงียบ แต่ติดธง "ต้องยกเลิกทาง e-Tax" บนใบ (ตัวตัดสินเดียว DocumentVoidPreconditions.AutoReceiptOnPaymentVoid
        // ผ่าน PaymentVoidCause.ChequeBounce) · ข้อความธงไปถึงผู้เรียกทาง log + webhook
        string? etaxCancellationFlag = null;
        string? outputVatNotice = null;
        if (plan.ReversePayment && cheque.PaymentId.HasValue)
        {
            if (_documents == null)
                throw new InvalidOperationException(
                    "ระบบยังไม่พร้อมกลับรายการชำระของเช็คใบนี้ — ติดต่อผู้ดูแลระบบ "
                    + "(บันทึกเช็คเด้งโดยไม่กลับรายการจะทำให้งบถือเงินที่ไม่มีจริง)");
            var voided = await _documents.VoidPaymentAsync(companyId, cheque.PaymentId.Value,
                Accounting.Helpers.PaymentVoidCause.ChequeBounce);
            etaxCancellationFlag = voided.EtaxCancellationFlag;
            // รอบ 200 ทีม V1G (ข้อ 48 · RV1F-2): เดือนที่ภาษีขายถึงกำหนดปิด/ยื่นแล้ว ⇒ เงินกลับแต่ภาษีขายค้างในแบบ — ธงอยู่บนใบต้นทางแล้ว · บอกผู้เรียกด้วย
            outputVatNotice = voided.OutputVatNotice;
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
        if (etaxCancellationFlag != null)
            _logger.LogWarning("Cheque {Id} bounced: {EtaxFlag}", chequeId, etaxCancellationFlag);
        if (outputVatNotice != null)
            _logger.LogWarning("Cheque {Id} bounced: {VatNotice}", chequeId, outputVatNotice);
        await FireAsync(companyId, "cheque.bounced", new
        {
            id = cheque.Id, chequeNumber = cheque.ChequeNumber,
            amount = cheque.Amount, isInbound = cheque.IsInbound,
            reason = cheque.BounceReason,
            ruleCode = plan.RuleCode,
            reversedPayment = plan.ReversePayment ? cheque.PaymentId : null,
            etaxCancellationRequired = etaxCancellationFlag,
            outputVatUndoBlocked = outputVatNotice,
        });
        return cheque;
    }

    public async Task<ChequeEntity> VoidAsync(Guid companyId, Guid chequeId,
        string? notes, CancellationToken ct = default)
    {
        var cheque = await _db.Cheques.FirstOrDefaultAsync(c => c.Id == chequeId
            && c.CompanyId == companyId, ct);
        if (cheque == null) throw new KeyNotFoundException(ChequeNotFoundMessage);
        if (cheque.Status == ChequeStatus.Cleared)
            throw new BusinessRuleException(
                $"เช็คเลขที่ {cheque.ChequeNumber} ขึ้นเงินแล้ว ยกเลิกไม่ได้ — ถ้าธนาคารคืนเช็คให้บันทึก “เช็คคืน” แทน", "CHEQUE-VOID-CLEARED");
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

    private const string ChequeNotFoundMessage = "ไม่พบเช็คใบนี้ (อาจถูกลบหรือหน้าจอค้างข้อมูลเก่า) — กด “รีเฟรช” ที่แท็บเช็คคงค้าง";

    /// <summary>ป้ายสถานะเช็คภาษาไทย — ตัวเดียวของข้อความในไฟล์นี้</summary>
    private static string StatusLabel(ChequeStatus st) => st switch
    {
        ChequeStatus.Issued => "ออกแล้ว",
        ChequeStatus.DepositedPending => "รอเรียกเก็บ",
        ChequeStatus.Cleared => "ขึ้นเงินแล้ว",
        ChequeStatus.Bounced => "เช็คคืน",
        ChequeStatus.Voided => "ยกเลิกแล้ว",
        _ => st.ToString(),
    };

    /// <summary>ผู้ติดต่อ (ถ้าส่งมา) ต้องเป็นของบริษัทนี้ — กันรหัสข้ามบริษัทติดไปกับเช็ค</summary>
    private async Task RequireContactAsync(Guid companyId, Guid? contactId, CancellationToken ct)
    {
        if (contactId is not Guid cid || cid == Guid.Empty) return;
        if (!await _db.Contacts.AnyAsync(c => c.Id == cid && c.CompanyId == companyId && !c.IsDeleted, ct))
            throw new BusinessRuleException("ไม่พบ “ผู้รับ/ผู้ออกเช็ค” ที่เลือกในบริษัทนี้ — เลือกผู้ติดต่อใหม่หรือเว้นว่าง", "CHEQUE-CONTACT");
    }

    public async Task<IReadOnlyList<ChequeBookResponse>> ListBooksAsync(Guid companyId, bool includeExhausted = false,
        CancellationToken ct = default)
    {
        var q = _db.ChequeBooks.AsNoTracking()
            .Where(b => b.CompanyId == companyId && !b.IsDeleted);
        if (!includeExhausted) q = q.Where(b => b.NextNumber <= b.EndChequeNumber);
        return await q.OrderByDescending(b => b.ReceivedFromBankAt)
            .Select(b => new ChequeBookResponse(b.Id, b.BookNumber, b.BankAccountId,
                b.BankAccount.BankName + " " + b.BankAccount.AccountNumber + " (" + b.BankAccount.AccountName + ")",
                b.StartChequeNumber, b.EndChequeNumber, b.NextNumber, b.NextNumber > b.EndChequeNumber))
            .Take(200)
            .ToListAsync(ct);
    }
}
