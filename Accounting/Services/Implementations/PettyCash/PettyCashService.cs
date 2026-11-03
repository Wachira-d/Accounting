using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Journal;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.PettyCash;

/// <summary>
/// Petty cash management — open a tin with an imprest amount, record
/// disbursements as they happen, replenish back to imprest at month-
/// end. Posts the cash-side JE automatically; user picks the expense
/// account per disbursement.
///
/// Custodian model: each fund has ONE responsible user. Audit shows
/// who took out what. Cycle-count (compare CurrentBalance to physical
/// cash on hand) belongs in the StockCount-like flow but for cash.
/// </summary>
public interface IPettyCashService
{
    /// <param name="linkedAccountId">ผังบัญชีของกองทุน (null = ใช้ผังมาตรฐาน <c>PettyCashJePlan.DefaultFundAccountCode</c>
    /// เมื่อมีและใช้งานได้ · ไม่มี = ล้มพร้อมบอกให้เลือก)</param>
    /// <param name="allowSharedAccount">true = ยอมให้ผูกผังที่กองทุนอื่นใช้อยู่ (ผู้ใช้ติ๊กเอง) · false (ค่าตั้งต้น) = ผัง 1 ผัง ↔ กองทุน 1 กองทุน
    /// (<see cref="PettyCashFundGlRule.SharedAccountProblem"/>)</param>
    Task<PettyCashFund> CreateFundAsync(Guid companyId, string name,
        Guid? custodianUserId, decimal imprestAmount, Guid? linkedAccountId,
        bool allowSharedAccount = false, CancellationToken ct = default);

    /// <summary>แก้กองทุน — ชื่อ · วงเงิน · <b>ผังบัญชีของกองทุน</b> (ทางออกของกองทุนเก่าที่เปิดโดยไม่มีผัง ⇒ ทุกการเบิก/เติมล้ม ·
    /// PAYMENT_CHANNEL_AUDIT ส่วนที่ 1 ข้อ 1 "ทางตัน") · null = ไม่เปลี่ยนช่องนั้น</summary>
    Task<PettyCashFund> UpdateFundAsync(Guid companyId, Guid fundId, string? name,
        decimal? imprestAmount, Guid? linkedAccountId, bool allowSharedAccount = false, CancellationToken ct = default);

    /// <summary>ผังที่เสนอเป็นค่าตั้งต้นของกองทุน (11112 เงินสดย่อย ถ้ามีและใช้งานได้) — null = ผังบริษัทไม่มี</summary>
    Task<PettyCashFundAccountOption?> GetDefaultFundAccountAsync(Guid companyId, CancellationToken ct = default);

    Task<PettyCashTransaction> DisburseAsync(Guid companyId, Guid fundId,
        decimal amount, string description, Guid? expenseAccountId,
        string? receiptRef, DateTime txnDate, Guid? projectId = null,
        CancellationToken ct = default);

    /// <param name="sourceAccountId">ผังบัญชีต้นทางของเงินที่เติมโดยตรง (เงินสด/กระเป๋าเงิน — ChartOfAccounts.Id)</param>
    /// <param name="sourceBankAccountId">บัญชีธนาคารต้นทาง (BankAccounts.Id → <c>LinkedAccountId</c>) · ส่งได้อย่างใดอย่างหนึ่ง ·
    /// ไม่ส่งทั้งคู่ = ระบบใช้บัญชีธนาคารที่ผูกผังอยู่ <b>ใบเดียว</b> ของบริษัท มิฉะนั้นล้มพร้อมบอกให้เลือก — ห้ามเดาแทนผู้ใช้</param>
    Task<PettyCashTransaction> ReplenishAsync(Guid companyId, Guid fundId,
        decimal amount, string? notes, DateTime txnDate,
        Guid? sourceAccountId = null, Guid? sourceBankAccountId = null, CancellationToken ct = default);

    /// <summary>เติมเงิน + แนบสลิป/หลักฐาน (ไม่บังคับ เว้นแต่บริษัทตั้ง <c>CompanySettings.PettyCashTopUpEvidenceRequired</c>) — รายการ + JE + ไฟล์ +
    /// audit อยู่ในธุรกรรมเดียว (ไฟล์ล้ม = ไม่มีรายการเกิด) · <see cref="ReplenishAsync"/> = ตัวนี้แบบไม่มีไฟล์ (ด่านบังคับสลิปเดินเหมือนกัน)</summary>
    Task<PettyCashTransaction> ReplenishWithEvidenceAsync(Guid companyId, Guid fundId,
        decimal amount, string? notes, DateTime txnDate, Guid? sourceAccountId, Guid? sourceBankAccountId,
        PettyCashEvidenceFile? evidence, Guid? actorUserId, CancellationToken ct = default);

    /// <summary>เบิก + แนบใบเสร็จ (ไม่บังคับ — ไม่มีใบเสร็จยังติดธง §65 ตรี(9) ตามช่องเลขที่ใบเสร็จเหมือนเดิม) · กลไกเดียวกับการเติม</summary>
    Task<PettyCashTransaction> DisburseWithEvidenceAsync(Guid companyId, Guid fundId,
        decimal amount, string description, Guid? expenseAccountId, string? receiptRef, DateTime txnDate, Guid? projectId,
        PettyCashEvidenceFile? evidence, Guid? actorUserId, CancellationToken ct = default);

    /// <summary>แนบหลักฐานเพิ่มให้รายการที่บันทึกแล้ว (เติม/เบิก/รายการจากทางอื่น) — ตรวจบริษัท · audit</summary>
    Task<PettyCashEvidenceRef> AttachEvidenceAsync(Guid companyId, Guid transactionId, PettyCashEvidenceFile evidence,
        Guid? actorUserId, CancellationToken ct = default);

    /// <summary>ค่าตั้งบริษัท "เติมเงินสดย่อยต้องแนบสลิป" (หน้าเงินสดย่อยใช้แสดงช่องเป็นบังคับ — ด่านจริงอยู่ฝั่งเซิร์ฟเวอร์)</summary>
    Task<bool> IsTopUpEvidenceRequiredAsync(Guid companyId, CancellationToken ct = default);

    Task<IReadOnlyList<PettyCashFundResponse>> ListAsync(Guid companyId, CancellationToken ct = default);
    Task<IReadOnlyList<PettyCashTransaction>> ListTransactionsAsync(Guid companyId, Guid fundId,
        DateTime? fromDate, DateTime? toDate, CancellationToken ct = default);

    /// <summary>ทะเบียนเบิกจ่าย/เติมเงินของกองทุน (ทุกรายการในช่วง — ไม่ตัดที่ 500 แบบ <see cref="ListTransactionsAsync"/>) ·
    /// ผัง/โครงการ/เลขใบสำคัญแปลงเป็นชื่อแล้ว · ยอดรวมเข้า/ออกคำนวณที่นี่ที่เดียว — หน้า petty-cash.html และไฟล์ส่งออกใช้ตัวเดียวกัน
    /// (2026-10-15 ทีม X5 · เดิมหน้าแสดงแค่การ์ดกองทุน ไม่มีรายการให้ตรวจ/ส่งออก)</summary>
    Task<PettyCashRegister> GetRegisterAsync(Guid companyId, Guid fundId,
        DateTime? fromDate, DateTime? toDate, CancellationToken ct = default);

    /// <summary>กระทบยอด "ผังของกองทุน (GL) ↔ ยอดกองทุน" ต่อผัง + ใบสำคัญจากทางอื่น (ใบสำคัญจ่าย/ใบเบิก/สมุดรายวัน/กลับรายการ …)
    /// ที่แตะผังกองทุนแต่ยังไม่อยู่ในทะเบียนกองทุนไหน (2026-11-02 · PETTY_CASH_AUDIT)</summary>
    Task<IReadOnlyList<PettyCashGlRecon>> GetGlReconciliationAsync(Guid companyId, CancellationToken ct = default);

    /// <summary>ระบุว่าใบสำคัญจากทางอื่นเป็นของกองทุนไหน — เพิ่มแถวในทะเบียนกองทุน (อ้างใบสำคัญเดิม · <b>ไม่ลง JE ใหม่</b> เพราะ GL ขยับไปแล้ว)
    /// และปรับยอดกองทุนตามยอดสุทธิที่ใบนั้นลงผังกองทุน</summary>
    Task<PettyCashTransaction> AttributeExternalEntryAsync(Guid companyId, Guid fundId, Guid journalEntryId,
        Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>ทำเครื่องหมายใบสำคัญจากทางอื่นว่า "ไม่กระทบกองทุน (ปรับเฉพาะ GL)" (เช่น ปรับปรุงยอดยกมา/แก้ผังผิด) — ออกจากรายการรอระบุ ·
    /// ไม่ขยับยอดกองทุน · audit · ยกเลิกได้ด้วย <see cref="UndoGlOnlyAsync"/></summary>
    Task<PettyCashTransaction> MarkGlOnlyAsync(Guid companyId, Guid accountId, Guid journalEntryId, string? note,
        Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>ยกเลิกเครื่องหมาย "ปรับเฉพาะ GL" — ใบกลับไปอยู่ในรายการรอระบุ · audit</summary>
    Task UndoGlOnlyAsync(Guid companyId, Guid transactionId, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>ย้อนผลต่อกองทุนของแถวทะเบียนที่อ้างใบสำคัญซึ่งไม่อยู่ใน GL แล้ว (ถูกลบ/ยกเลิก) — ยอดกองทุนกลับตาม
    /// <see cref="PettyCashFundGlRule.FundEffect"/> · แถวถูก soft-delete · <b>ไม่ลง JE</b> (GL ไม่มีใบนั้นแล้ว) · audit</summary>
    Task<PettyCashFund> ReverseOrphanedEntryAsync(Guid companyId, Guid transactionId, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>ตรวจก่อนปิดกองทุน (ตัวตัดสินเดียวกับ <see cref="CloseFundAsync"/>) — ยอดที่ต้องคืน · รายการค้างบนผังกองทุน · ข้อความถ้าปิดไม่ได้</summary>
    Task<PettyCashClosePreview> GetClosePreviewAsync(Guid companyId, Guid fundId, CancellationToken ct = default);

    /// <summary>ปิดกองทุน: คืนเงินคงเหลือเข้าบัญชีที่เลือก (JE: Dr บัญชีนั้น / Cr ผังกองทุน · เลข/งวดผ่านตัวกลาง) → ยอด 0 → ปิดใช้ · audit ·
    /// รายการค้างบนผังกองทุนต้องเป็นศูนย์ หรือผู้ใช้ติ๊กรับทราบ (<paramref name="acknowledgeOpenItems"/>)</summary>
    Task<PettyCashFund> CloseFundAsync(Guid companyId, Guid fundId, Guid? returnAccountId, Guid? returnBankAccountId,
        DateTime closeDate, string? note, bool acknowledgeOpenItems, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>เปิดกองทุนที่ปิดแล้วอีกครั้ง (ยอดเริ่มจากที่เหลือ — ปกติ 0 ⇒ เติมเงินก่อนเบิก) · audit</summary>
    Task<PettyCashFund> ReopenFundAsync(Guid companyId, Guid fundId, Guid? actorUserId = null, CancellationToken ct = default);
}

/// <summary>แถวทะเบียนกองทุนที่อ้างใบสำคัญซึ่งไม่ถูกนับใน GL แล้ว · <c>FundEffectReversal</c> = ยอดที่กองทุนจะขยับเมื่อกด “ย้อนผลต่อกองทุน”</summary>
public sealed record PettyCashOrphanEntry(Guid TransactionId, Guid FundId, string FundName, DateTime TransactionDate, string Type, string TypeLabel,
    decimal Amount, Guid JournalEntryId, string? EntryNumber, string Reason, decimal FundEffectReversal);

/// <summary>ผลตรวจก่อนปิดกองทุน · <c>Blocker</c> = null ⇒ ปิดได้โดยไม่ต้องติ๊กรับทราบ · <c>CanAcknowledge</c> = false ⇒ ติ๊กรับทราบก็ปิดไม่ได้
/// (รายการค้างของกองทุนนี้เองที่อ้างใบสำคัญซึ่งหาย — ต้องย้อนผลก่อน) · <c>NeedsReturnAccount</c> = ยอดคงเหลือ &gt; 0 ต้องเลือกบัญชีรับคืน ·
/// <c>NegativeBalanceMessage</c> = ยอดติดลบ ⇒ ปิดไม่ได้จนกว่าจะเติม/ย้อนผล</summary>
public sealed record PettyCashClosePreview(Guid FundId, string FundName, bool IsActive, decimal Balance, bool NeedsReturnAccount,
    int UnattributedCount, int OrphanedCount, decimal Difference, string? Blocker, string? NegativeBalanceMessage, string? FundAccountLabel,
    int OwnOrphanedCount = 0, bool CanAcknowledge = true);

/// <summary>ใบสำคัญจากทางอื่นที่แตะผังกองทุน แต่ยังไม่อยู่ในทะเบียนกองทุนใด · <c>NetDebit</c> = เดบิต − เครดิต ที่ลงผังนี้ ·
/// <c>Type</c>/<c>Amount</c> = แถวทะเบียนที่จะเกิดเมื่อระบุกองทุน · <c>SuggestedFundId</c> = มีกองทุนเดียวบนผังนี้ (คนยังต้องกดยืนยัน)</summary>
public sealed record PettyCashExternalEntry(Guid JournalEntryId, string EntryNumber, DateTime EntryDate, string? Description, string? Reference,
    decimal NetDebit, string Type, string TypeLabel, decimal Amount, Guid? SuggestedFundId);

public sealed record PettyCashGlFund(Guid FundId, string Name, decimal CurrentBalance, bool IsActive);

/// <summary>ผลกระทบยอดของผังหนึ่ง · <c>Difference</c> = ยอด GL − ผลรวมยอดกองทุน − ยอดที่ทำเครื่องหมาย "ปรับเฉพาะ GL" (<c>GlOnlyNet</c>) · <c>Status</c> = ชื่อ enum <see cref="PettyCashGlReconStatus"/> ·
/// <c>UnattributedEntries</c> แสดงล่าสุดไม่เกิน <see cref="PettyCashService.MaxExternalEntriesShown"/> ใบ (นับครบใน <c>UnattributedCount</c>)</summary>
public sealed record PettyCashGlRecon(Guid AccountId, string AccountCode, string AccountName, decimal GlBalance, decimal FundsTotal,
    decimal Difference, string Status, string StatusLabel, bool SharedByMultipleFunds, IReadOnlyList<PettyCashGlFund> Funds,
    int UnattributedCount, decimal UnattributedNet, IReadOnlyList<PettyCashExternalEntry> UnattributedEntries,
    decimal GlOnlyNet = 0m, IReadOnlyList<PettyCashGlOnlyEntry>? GlOnlyEntries = null,
    int OrphanedCount = 0, IReadOnlyList<PettyCashOrphanEntry>? OrphanedEntries = null);

/// <summary>ใบสำคัญที่ทำเครื่องหมาย "ไม่กระทบกองทุน (ปรับเฉพาะ GL)" แล้ว — <c>TransactionId</c> ใช้ยกเลิกเครื่องหมาย</summary>
public sealed record PettyCashGlOnlyEntry(Guid TransactionId, Guid JournalEntryId, string? EntryNumber, DateTime EntryDate, decimal NetDebit,
    string? Description);

/// <summary>ไฟล์หลักฐานที่ผ่าน <see cref="PettyCashEvidence.Check"/> แล้ว (ชนิด/นามสกุลจากไบต์จริง)</summary>
public sealed record PettyCashEvidenceFile(string DisplayName, string ContentType, byte[] Content);

/// <summary>หลักฐานที่แนบกับรายการ — ดาวน์โหลดผ่าน <c>GET attachments/{AttachmentId}/download</c></summary>
public sealed record PettyCashEvidenceRef(Guid AttachmentId, string FileName, string ContentType);

/// <summary>หนึ่งรายการในทะเบียนเงินสดย่อย · <c>AmountIn</c> = เติมเงิน · <c>AmountOut</c> = เบิกจ่าย ·
/// <c>TypeLabel</c> = ป้ายไทยจาก <see cref="PettyCashRegister.TypeLabel"/></summary>
public sealed record PettyCashRegisterRow(Guid Id, DateTime TransactionDate, string Type, string TypeLabel, string Description,
    string? ReceiptReference, decimal Amount, decimal AmountIn, decimal AmountOut,
    string? ExpenseAccountCode, string? ExpenseAccountName, string? ProjectLabel,
    Guid? JournalEntryId, string? JournalEntryNumber, bool IsNonDeductible, string? NonDeductibleRuleCode, DateTime CreatedAt,
    IReadOnlyList<PettyCashEvidenceRef>? Evidence = null);

/// <summary>ทะเบียนเงินสดย่อยของกองทุนหนึ่ง (เรียงวันที่เก่า → ใหม่ ตามสมุดเงินสด)</summary>
public sealed record PettyCashRegister(Guid FundId, string FundName, decimal ImprestAmount, decimal CurrentBalance,
    string? FundAccountLabel, DateTime? FromDate, DateTime? ToDate, IReadOnlyList<PettyCashRegisterRow> Rows,
    decimal TotalIn, decimal TotalOut)
{
    /// <summary>ป้ายชนิดรายการ — ตัวเดียวของระบบ</summary>
    public static string TypeLabel(string? type) => type switch
    {
        "Disbursement" => "เบิกจ่าย",
        "Replenishment" => "เติมเงิน",
        "Adjustment" => "ปรับปรุง",
        PettyCashFundGlRule.ReturnType => "คืนเงิน (ปิดกองทุน)",
        PettyCashFundGlRule.GlOnlyType => "ปรับเฉพาะ GL (ไม่กระทบกองทุน)",
        _ => type ?? "",
    };
}

/// <summary>กองทุนเงินสดย่อยสำหรับหน้าจอ — ผังของกองทุนออกเป็นรหัส+ชื่อ (หน้าไม่ต้องเดา) · <c>HasPostedTransactions</c> บอกว่าเปลี่ยนผังได้ไหม</summary>
public sealed record PettyCashFundResponse(
    Guid Id, string Name, Guid? CustodianUserId, decimal ImprestAmount, decimal CurrentBalance,
    Guid? LinkedAccountId, string? LinkedAccountCode, string? LinkedAccountName, bool IsActive,
    bool HasPostedTransactions);

/// <summary>ผังบัญชีที่เสนอให้กองทุน</summary>
public sealed record PettyCashFundAccountOption(Guid AccountId, string AccountCode, string AccountName);

public class PettyCashService : IPettyCashService
{
    private readonly AccountingDbContext _db;
    private readonly IFileAttachmentService? _files;

    public PettyCashService(AccountingDbContext db, IFileAttachmentService? files = null) { _db = db; _files = files; }

    private const string FundAccountField = "ผังบัญชีของกองทุน";

    public async Task<PettyCashFund> CreateFundAsync(Guid companyId, string name,
        Guid? custodianUserId, decimal imprestAmount, Guid? linkedAccountId,
        bool allowSharedAccount = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleException("กรอก “ชื่อกองทุน” ก่อนบันทึก", "PETTY-FUND-NAME");
        if (imprestAmount <= 0)
            throw new BusinessRuleException("“วงเงินกองทุน (imprest)” ต้องมากกว่า 0", "PETTY-FUND-IMPREST");
        if (custodianUserId is Guid cu && cu != Guid.Empty
            && !await _db.CompanyUsers.AnyAsync(x => x.CompanyId == companyId && x.UserId == cu, ct))
            throw new BusinessRuleException("“ผู้ดูแลกองทุน” ไม่ใช่สมาชิกของบริษัทนี้ — เลือกผู้ดูแลใหม่", "PETTY-FUND-CUSTODIAN");

        // ผังของกองทุน: ผู้ใช้เลือก > ผังมาตรฐาน 11112 (ค่าตั้งต้นที่แน่นอน ไม่ใช่การเดา) > ล้มพร้อมบอกให้เลือก ·
        // เดิมหน้าเว็บส่ง null เสมอและไม่มีที่ผูกภายหลัง ⇒ ทุกการเบิก/เติมล้ม (PAYMENT_CHANNEL_AUDIT ส่วนที่ 1 ข้อ 1)
        Guid fundAccountId;
        if (PickedAccountGuard.IsSet(linkedAccountId))
        {
            await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, linkedAccountId!.Value,
                FundAccountField, new[] { AccountType.Asset }, ct);
            fundAccountId = linkedAccountId!.Value;
        }
        else
        {
            var def = await GetDefaultFundAccountAsync(companyId, ct)
                ?? throw new BusinessRuleException(
                    $"เลือก “{FundAccountField}” ก่อนบันทึก — ผังบัญชีของบริษัทไม่มี {PettyCashJePlan.DefaultFundAccountCode} เงินสดย่อย "
                    + "(เพิ่มที่หน้า “ผังบัญชี” หรือเลือกผังสินทรัพย์อื่นที่ใช้เป็นเงินสดย่อย)", PettyCashJePlan.RuleNoFundAccount);
            fundAccountId = def.AccountId;
        }
        await RequireAccountNotSharedAsync(companyId, null, fundAccountId, allowSharedAccount, ct);

        var fund = new PettyCashFund
        {
            CompanyId = companyId,
            Name = name.Trim(),
            CustodianUserId = PickedAccountGuard.IsSet(custodianUserId) ? custodianUserId : null,
            ImprestAmount = imprestAmount,
            CurrentBalance = 0m,            // bank → cash JE on first replenishment funds the tin
            LinkedAccountId = fundAccountId,
        };
        _db.PettyCashFunds.Add(fund);
        await _db.SaveChangesAsync(ct);
        return fund;
    }

    public async Task<PettyCashFund> UpdateFundAsync(Guid companyId, Guid fundId, string? name,
        decimal? imprestAmount, Guid? linkedAccountId, bool allowSharedAccount = false, CancellationToken ct = default)
    {
        var fund = await FindFundAsync(companyId, fundId, ct);
        if (name != null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new BusinessRuleException("“ชื่อกองทุน” ว่างไม่ได้", "PETTY-FUND-NAME");
            fund.Name = name.Trim();
        }
        if (imprestAmount.HasValue)
        {
            if (imprestAmount.Value <= 0)
                throw new BusinessRuleException("“วงเงินกองทุน (imprest)” ต้องมากกว่า 0", "PETTY-FUND-IMPREST");
            fund.ImprestAmount = imprestAmount.Value;
        }
        if (PickedAccountGuard.IsSet(linkedAccountId) && linkedAccountId != fund.LinkedAccountId)
        {
            await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, linkedAccountId!.Value,
                FundAccountField, new[] { AccountType.Asset }, ct);
            // ยอดที่ลงบัญชีไปแล้วอยู่ในผังเดิม — เปลี่ยนผังตอนนี้ = ยอดเงินสดย่อยในงบแตกเป็นสองผังโดยไม่มีใครรู้ (ความถูกต้องของ GL ⇒ บล็อก)
            if (fund.LinkedAccountId.HasValue
                && await _db.PettyCashTransactions.AnyAsync(t => t.CompanyId == companyId && t.FundId == fund.Id
                    && !t.IsDeleted && t.JournalEntryId != null, ct))
                throw new BusinessRuleException(
                    "กองทุนนี้มีรายการที่ลงบัญชีไปแล้ว — เปลี่ยน “ผังบัญชีของกองทุน” ไม่ได้ เพราะยอดเดิมอยู่ในผังเก่า "
                    + "· ถ้าต้องย้ายผังจริง: บันทึกโอนยอดที่หน้า “สมุดรายวันทั่วไป” (เดบิตผังใหม่ / เครดิตผังเดิม) แล้วเปิดกองทุนใหม่ที่ผูกผังใหม่",
                    "PETTY-FUND-ACCOUNT-LOCKED");
            await RequireAccountNotSharedAsync(companyId, fund.Id, linkedAccountId!.Value, allowSharedAccount, ct);
            fund.LinkedAccountId = linkedAccountId;
        }
        fund.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return fund;
    }

    public async Task<PettyCashFundAccountOption?> GetDefaultFundAccountAsync(Guid companyId, CancellationToken ct = default)
        => await _db.ChartOfAccounts.WhereNotControlAccount(_db).AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive
                && a.AccountType == AccountType.Asset && a.AccountCode == PettyCashJePlan.DefaultFundAccountCode)
            .Select(a => new PettyCashFundAccountOption(a.Id, a.AccountCode, a.AccountName))
            .FirstOrDefaultAsync(ct);

    /// <summary>ผัง 1 ผัง ↔ กองทุน 1 กองทุน (เว้นแต่ผู้ใช้ติ๊กยอมใช้ผังร่วม) — กติกาที่ <see cref="PettyCashFundGlRule.SharedAccountProblem"/></summary>
    private async Task RequireAccountNotSharedAsync(Guid companyId, Guid? selfFundId, Guid accountId, bool allowShared, CancellationToken ct)
    {
        if (allowShared) return;
        var others = await _db.PettyCashFunds.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted && f.LinkedAccountId == accountId && (selfFundId == null || f.Id != selfFundId))
            .OrderBy(f => f.Name).Select(f => f.Name).ToListAsync(ct);
        if (others.Count == 0) return;
        var label = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.CompanyId == companyId)
            .Select(a => a.AccountCode + " " + a.AccountName).FirstOrDefaultAsync(ct) ?? "ที่เลือก";
        var problem = PettyCashFundGlRule.SharedAccountProblem(label, others, allowShared);
        if (problem != null) throw new BusinessRuleException(problem, PettyCashFundGlRule.RuleSharedAccount);
    }

    /// <summary>ล็อกแถวกองทุน (<c>FOR UPDATE</c>) — ตัวเดียวของทุกเส้นที่แก้ <c>CurrentBalance</c> (เบิก · เติม · ระบุกองทุน) ·
    /// <b>ผู้เรียกต้องถือธุรกรรม</b> (นอกธุรกรรม = autocommit ล็อกหลุดทันที) · ต้องเรียกก่อน <see cref="FindFundAsync"/> เพื่อให้อ่านยอดหลังล็อก</summary>
    private Task LockFundRowAsync(Guid companyId, Guid fundId, CancellationToken ct)
        => _db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"PettyCashFunds\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", new object[] { fundId, companyId }, ct);

    /// <summary>ล็อกต่อใบสำคัญ (advisory xact) — ระบุกองทุน/ปรับเฉพาะ GL ของใบเดียวกันพร้อมกันได้แถวเดียว แม้ต่างกองทุนบนผังร่วม (ฝ่ายค้านข้อ 2)</summary>
    private Task LockEntryAsync(Guid companyId, Guid journalEntryId, CancellationToken ct)
        => _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.PettyCashEntry, journalEntryId.ToString("N")) }, ct);

    private async Task<PettyCashFund> FindFundAsync(Guid companyId, Guid fundId, CancellationToken ct)
        => await _db.PettyCashFunds.FirstOrDefaultAsync(
               f => f.Id == fundId && f.CompanyId == companyId && !f.IsDeleted, ct)
           ?? throw new KeyNotFoundException("ไม่พบกองทุนเงินสดย่อยนี้ (อาจถูกลบไปแล้ว) — รีเฟรชหน้าแล้วเลือกกองทุนใหม่");

    /// <summary>ผังของกองทุนต้องยังลงรายการได้ตอนลง JE (ผังอาจถูกปิดใช้งานหลังผูก)</summary>
    private async Task RequireFundAccountUsableAsync(Guid companyId, PettyCashFund fund, CancellationToken ct)
        => await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, fund.LinkedAccountId!.Value,
            FundAccountField + $" ของกองทุน {fund.Name}", new[] { AccountType.Asset }, ct);

    /// <summary>กองทุนที่ปิดแล้วห้ามขยับยอด (เบิก · เติม · ระบุกองทุน · ย้อนผลรายการค้าง) — ทางไปต่อ = เปิดกองทุนอีกครั้ง</summary>
    private static void RequireFundOpen(PettyCashFund fund)
    {
        if (!fund.IsActive)
            throw new BusinessRuleException(PettyCashFundGlRule.FundClosedMessage(fund.Name), PettyCashFundGlRule.RuleFundClosed, 409);
    }

    /// <summary>ด่านงวดบัญชีปิด — ตัวเดียวกับ JE อื่น (<see cref="JournalEntryBuilder.ClosedPeriodReasonAsync"/>) · ตรวจ<b>ก่อน</b>ขยับยอดกองทุน
    /// เพื่อให้ผู้ใช้ได้ข้อความไทย (builder เองโยน InvalidOperationException)</summary>
    private async Task RequirePeriodOpenAsync(Guid companyId, DateTime date, CancellationToken ct)
    {
        if (await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, companyId, date, ct) is string closed)
            throw new BusinessRuleException(closed, PettyCashJePlan.RulePeriodClosed);
    }

    public Task<PettyCashTransaction> DisburseAsync(Guid companyId, Guid fundId,
        decimal amount, string description, Guid? expenseAccountId,
        string? receiptRef, DateTime txnDate, Guid? projectId = null,
        CancellationToken ct = default)
        => DisburseWithEvidenceAsync(companyId, fundId, amount, description, expenseAccountId, receiptRef, txnDate, projectId,
            null, null, ct);

    public async Task<PettyCashTransaction> DisburseWithEvidenceAsync(Guid companyId, Guid fundId,
        decimal amount, string description, Guid? expenseAccountId, string? receiptRef, DateTime txnDate, Guid? projectId,
        PettyCashEvidenceFile? evidence, Guid? actorUserId, CancellationToken ct = default)
    {
        // ธุรกรรมเสมอ — ล็อกแถวกองทุน (LockFundRowAsync) ใน core ต้องอยู่ใต้ธุรกรรมจึงมีผล (ฝ่ายค้านข้อ 1: lost update ของ CurrentBalance)
        // ไฟล์หลักฐานเก็บ<b>ก่อน</b>ออกเลขใบสำคัญ (ใน core) — ล็อกเลข JV ของทั้งบริษัท/เดือนต้องไม่ถูกถือระหว่างอัปโหลด (ฝ่ายค้านส่วน D ข้อ 4)
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var txn = await DisburseCoreAsync(companyId, fundId, amount, description, expenseAccountId, receiptRef, txnDate, projectId,
            evidence, actorUserId, ct);
        await tx.CommitAsync(ct);
        return txn;
    }

    private async Task<PettyCashTransaction> DisburseCoreAsync(Guid companyId, Guid fundId,
        decimal amount, string description, Guid? expenseAccountId,
        string? receiptRef, DateTime txnDate, Guid? projectId, PettyCashEvidenceFile? evidence, Guid? actorUserId, CancellationToken ct)
    {
        txnDate = ThaiDate.CalendarDateUtc(txnDate);   // วันตามปฏิทินไทย — 00:00–06:59 ของวันที่ 1 ต้องไม่ตกเดือนก่อน/งวดที่ปิด
        if (amount <= 0) throw new BusinessRuleException("“จำนวนเงิน” ต้องมากกว่า 0", PettyCashJePlan.RuleBadAmount);
        if (string.IsNullOrWhiteSpace(description))
            throw new BusinessRuleException("กรอก “คำอธิบาย” ว่าจ่ายค่าอะไร ก่อนบันทึก", "PETTY-NO-DESCRIPTION");
        await LockFundRowAsync(companyId, fundId, ct);   // อ่านยอดใต้ล็อก — สองคนเบิกพร้อมกันต้องไม่ทับยอดกัน
        var fund = await FindFundAsync(companyId, fundId, ct);
        RequireFundOpen(fund);
        await RequirePeriodOpenAsync(companyId, txnDate, ct);

        // ── ลงบัญชี: Dr ค่าใช้จ่าย / Cr ผังของกองทุน ────────────────────
        // ⚠ เดิมเป็น `if (มีทั้งสองผัง) { ลง JE }` **ไม่มี else** ⇒ ขาดผังไหน
        // ก็บันทึกรายการโดยไม่มี JE เงียบ ๆ = เงินออกจากลิ้นชักโดยบัญชีไม่รู้
        // (`DECISION_AUDIT_2026-09-18.md` §3 D4-7)
        var plan = Accounting.Helpers.PettyCashJePlan.ForDisbursement(
            expenseAccountId, fund.LinkedAccountId, receiptRef, amount);
        if (!plan.Ok) throw new BusinessRuleException(plan.Error!, plan.RuleCode);
        // รหัสจาก request ต้องเป็นผังของบริษัทนี้ที่ลงรายการได้ (เดิมเขียนตรงลง JournalEntryLine.AccountId — ข้ามบริษัท/ผังคุมได้)
        await PickedAccountGuard.RequirePostingAccountAsync(_db, companyId, plan.DebitAccountId, "บัญชีค่าใช้จ่าย", null, ct);
        await RequireFundAccountUsableAsync(companyId, fund, ct);
        if (projectId is Guid pid && pid != Guid.Empty
            && !await _db.Projects.AnyAsync(p => p.Id == pid && p.CompanyId == companyId && !p.IsDeleted, ct))
            throw new BusinessRuleException("ไม่พบ “โครงการ” ที่เลือกในบริษัทนี้ — รีเฟรชหน้าแล้วเลือกโครงการใหม่ หรือเว้นว่าง", "PETTY-PROJECT");
        if (projectId == Guid.Empty) projectId = null;

        if (fund.CurrentBalance < amount)
            throw new BusinessRuleException(
                $"เงินสดย่อยกองทุน {fund.Name} คงเหลือ {fund.CurrentBalance:N2} บาท ไม่พอจ่าย {amount:N2} บาท — "
                + "กด “เติมเงิน” ที่กองทุนนี้ก่อน หรือลดจำนวนเงิน", "PETTY-INSUFFICIENT");
        fund.CurrentBalance -= amount;
        var txn = new PettyCashTransaction
        {
            CompanyId = companyId,
            FundId = fundId,
            TransactionDate = txnDate,
            Amount = amount,
            Type = "Disbursement",
            Description = description,
            ExpenseAccountId = expenseAccountId,
            ReceiptReference = receiptRef,
            ProjectId = projectId,
        };
        _db.PettyCashTransactions.Add(txn);
        var evidenceId = evidence != null ? await StoreEvidenceFileAsync(companyId, txn.Id, evidence, actorUserId, ct) : (Guid?)null;

        // เลขใบสำคัญ + งวดบัญชี ผ่านตัวกลางเดียวกับ JE อัตโนมัติอื่น (JournalEntryBuilder: เลข JV-yyyyMM-#### ต่อเนื่องใต้ advisory lock ·
        // FiscalPeriodId · ด่านงวดปิด) — เดิมเลข "JV-PC-yyyyMMdd-XXXX" สุ่ม 4 หลัก + ไม่ตรวจงวด (PETTY_CASH_AUDIT งานค้าง)
        var je = await JournalEntryBuilder.For(_db, companyId, txnDate)
            .Description($"จ่ายเงินสดย่อย: {description}")
            .Reference(receiptRef)
            .Project(projectId)
            .Debit(plan.DebitAccountId, amount, description)
            .Credit(plan.CreditAccountId, amount, $"จ่ายจากเงินสดย่อย {fund.Name}")
            .PostAsync(actorUserId?.ToString(), ct);
        foreach (var line in je.Lines) line.ProjectId = projectId;   // ต้นทุนโครงการอ่านจากบรรทัด (เหมือนเดิม)
        txn.JournalEntryId = je.Id;

        // §65 ตรี(9) — จ่ายโดยไม่มีหลักฐานผู้รับ: **บันทึกได้ แต่ติดธงบวกกลับ**
        // (เงินออกจากลิ้นชักไปแล้วจริง การห้ามบันทึกจะทำให้ยอดในระบบไม่ตรงกับ
        // เงินในลิ้นชักถาวร ซึ่งเสียหายกว่า — ดู doc ของ `PettyCashJePlan`)
        txn.IsNonDeductible = plan.NonDeductible;
        txn.NonDeductibleRuleCode = plan.NonDeductible ? plan.RuleCode : null;
        if (evidenceId is Guid evId) AddEvidenceAudit(companyId, txn, evidence!, evId, actorUserId);

        await _db.SaveChangesAsync(ct);
        return txn;
    }

    /// <summary>
    /// เติมเงินสดย่อย — **ต้องมี JE เสมอ**: Dr ผังของกองทุน / Cr ผังบัญชีต้นทาง.
    /// เดิมเมธอดนี้ **ไม่มี JE เลย** ⇒ `PettyCashFund.CurrentBalance` ขึ้น แต่ GL
    /// ไม่ขยับทั้งสองขา = ยอดธนาคารในงบไม่ลด ยอดเงินสดย่อยในงบไม่ขึ้น
    /// (`DECISION_AUDIT_2026-09-18.md` §3 D4-7 — "เงินออกธนาคารเข้าถังโดย GL ไม่รู้")
    /// </summary>
    public Task<PettyCashTransaction> ReplenishAsync(Guid companyId, Guid fundId,
        decimal amount, string? notes, DateTime txnDate,
        Guid? sourceAccountId = null, Guid? sourceBankAccountId = null, CancellationToken ct = default)
        => ReplenishWithEvidenceAsync(companyId, fundId, amount, notes, txnDate, sourceAccountId, sourceBankAccountId, null, null, ct);

    public async Task<PettyCashTransaction> ReplenishWithEvidenceAsync(Guid companyId, Guid fundId,
        decimal amount, string? notes, DateTime txnDate, Guid? sourceAccountId, Guid? sourceBankAccountId,
        PettyCashEvidenceFile? evidence, Guid? actorUserId, CancellationToken ct = default)
    {
        // ด่านค่าตั้งบริษัท — ทุกทางเข้าของการเติม (หน้าเว็บ/JSON API เดิม) เดินผ่านที่นี่ ⇒ บังคับครบ ไม่ใช่แค่หน้าจอ
        if (evidence == null && await IsTopUpEvidenceRequiredAsync(companyId, ct))
            throw new BusinessRuleException(PettyCashEvidence.RequiredMessage, PettyCashEvidence.RuleRequired);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var txn = await ReplenishCoreAsync(companyId, fundId, amount, notes, txnDate, sourceAccountId, sourceBankAccountId,
            evidence, actorUserId, ct);
        await tx.CommitAsync(ct);
        return txn;
    }

    public async Task<bool> IsTopUpEvidenceRequiredAsync(Guid companyId, CancellationToken ct = default)
        => await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => (bool?)s.PettyCashTopUpEvidenceRequired)
            .FirstOrDefaultAsync(ct) ?? false;

    public async Task<PettyCashEvidenceRef> AttachEvidenceAsync(Guid companyId, Guid transactionId, PettyCashEvidenceFile evidence,
        Guid? actorUserId, CancellationToken ct = default)
    {
        var txn = await _db.PettyCashTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.CompanyId == companyId && !t.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบรายการเงินสดย่อยนี้ในบริษัท — รีเฟรชทะเบียนแล้วลองใหม่");
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var r = await SaveEvidenceAsync(companyId, txn, evidence, actorUserId, ct);
        await tx.CommitAsync(ct);
        return r;
    }

    /// <summary>แนบหลักฐานให้รายการที่บันทึกแล้ว: เก็บไฟล์ + audit (hash chain) — ผู้เรียกถือธุรกรรมไว้ (ล้ม = ย้อนทั้งรายการ)</summary>
    private async Task<PettyCashEvidenceRef> SaveEvidenceAsync(Guid companyId, PettyCashTransaction txn, PettyCashEvidenceFile evidence,
        Guid? actorUserId, CancellationToken ct)
    {
        var attachmentId = await StoreEvidenceFileAsync(companyId, txn.Id, evidence, actorUserId, ct);
        AddEvidenceAudit(companyId, txn, evidence, attachmentId, actorUserId);
        await _db.SaveChangesAsync(ct);
        return new PettyCashEvidenceRef(attachmentId, evidence.DisplayName, evidence.ContentType);
    }

    /// <summary>เก็บไฟล์หลักฐานผ่านที่เก็บไฟล์แนบกลาง (รหัสรายการรู้ก่อนบันทึก — Guid ฝั่งแอป) · เบิก/เติมเรียก<b>ก่อน</b>ออกเลขใบสำคัญ</summary>
    private async Task<Guid> StoreEvidenceFileAsync(Guid companyId, Guid transactionId, PettyCashEvidenceFile evidence,
        Guid? actorUserId, CancellationToken ct)
    {
        if (_files == null)
            throw new InvalidOperationException("ที่เก็บไฟล์แนบไม่พร้อม (PettyCashService ไม่ได้รับ IFileAttachmentService)");
        var saved = actorUserId is Guid uid && uid != Guid.Empty
            ? await _files.UploadBytesAsync(companyId, PettyCashEvidence.EntityType, transactionId, evidence.DisplayName, evidence.ContentType, evidence.Content, uid)
            : await _files.UploadAnonymousBytesAsync(companyId, PettyCashEvidence.EntityType, transactionId, evidence.DisplayName, evidence.ContentType, evidence.Content);
        return saved.Id;
    }

    /// <summary>audit ของไฟล์หลักฐาน (hash chain) — หลังรู้เลข JE ของรายการ · ผู้เรียก SaveChanges</summary>
    private void AddEvidenceAudit(Guid companyId, PettyCashTransaction txn, PettyCashEvidenceFile evidence, Guid attachmentId, Guid? actorUserId)
    {
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorUserId,
            EntityType = nameof(PettyCashTransaction),
            EntityId = txn.Id.ToString(),
            Action = AuditAction.Create,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "petty-cash-evidence-attached",
                transactionType = txn.Type,
                fundId = txn.FundId,
                amount = txn.Amount,
                journalEntryId = txn.JournalEntryId,
                attachmentId,
                fileName = evidence.DisplayName,
                contentType = evidence.ContentType,
                size = evidence.Content.LongLength,
            }),
            Timestamp = DateTime.UtcNow,
        });
    }

    private async Task<PettyCashTransaction> ReplenishCoreAsync(Guid companyId, Guid fundId,
        decimal amount, string? notes, DateTime txnDate, Guid? sourceAccountId, Guid? sourceBankAccountId,
        PettyCashEvidenceFile? evidence, Guid? actorUserId, CancellationToken ct)
    {
        txnDate = ThaiDate.CalendarDateUtc(txnDate);   // วันตามปฏิทินไทย (เหมือนเบิก)
        if (amount <= 0) throw new BusinessRuleException("“จำนวนเติม” ต้องมากกว่า 0", PettyCashJePlan.RuleBadAmount);
        await LockFundRowAsync(companyId, fundId, ct);   // อ่านยอดใต้ล็อก — กัน lost update กับเบิก/ระบุกองทุนที่ทำพร้อมกัน
        var fund = await FindFundAsync(companyId, fundId, ct);
        RequireFundOpen(fund);
        await RequirePeriodOpenAsync(companyId, txnDate, ct);
        if (!fund.LinkedAccountId.HasValue)
            throw new BusinessRuleException(PettyCashJePlan.NoFundAccountMessage, PettyCashJePlan.RuleNoFundAccount);
        await RequireFundAccountUsableAsync(companyId, fund, ct);

        // ต้นทางของเงิน: บัญชีธนาคาร (→ LinkedAccountId) หรือผังเงินตรง ที่ผู้ใช้เลือก > บัญชีธนาคารที่ผูกผังอยู่ **ใบเดียว**
        // ของบริษัท (ไม่ใช่การเดา — เป็นคำตอบเดียวที่เป็นไปได้) > ล้มพร้อมบอก ·
        // เดิมหน้าเว็บส่ง BankAccount.Id มาในช่องผังบัญชี แล้วเขียนตรงลง JE ⇒ FK ล้ม (บั๊กคลาสเดียวกับรายการเกิดซ้ำ)
        const string sourceField = "เติมจากบัญชี";
        var resolvedSource = await PickedAccountGuard.ResolveMoneyChannelAsync(_db, companyId,
            sourceBankAccountId, sourceAccountId, sourceField, ct);
        if (!resolvedSource.HasValue)
        {
            var linkedBanks = await _db.BankAccounts.AsNoTracking()
                .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.IsActive
                    && b.LinkedAccountId != null)
                .Select(b => b.Id)
                .Take(2)
                .ToListAsync(ct);
            if (linkedBanks.Count == 1)
                resolvedSource = await PickedAccountGuard.ResolveBankGlAsync(_db, companyId, linkedBanks[0], sourceField, ct);
        }

        var plan = Accounting.Helpers.PettyCashJePlan.ForReplenishment(
            fund.LinkedAccountId, resolvedSource, amount);
        if (!plan.Ok) throw new BusinessRuleException(plan.Error!, plan.RuleCode);

        fund.CurrentBalance += amount;
        var txn = new PettyCashTransaction
        {
            CompanyId = companyId,
            FundId = fundId,
            TransactionDate = txnDate,
            Amount = amount,
            Type = "Replenishment",
            Description = string.IsNullOrWhiteSpace(notes) ? "เติมเงินสดย่อย" : notes.Trim(),
        };
        _db.PettyCashTransactions.Add(txn);
        var evidenceId = evidence != null ? await StoreEvidenceFileAsync(companyId, txn.Id, evidence, actorUserId, ct) : (Guid?)null;

        var je = await JournalEntryBuilder.For(_db, companyId, txnDate)
            .Description($"เติมเงินสดย่อย {fund.Name}")
            .Debit(plan.DebitAccountId, amount, $"เติมเงินสดย่อย {fund.Name}")
            .Credit(plan.CreditAccountId, amount, $"โอนออกเพื่อเติมเงินสดย่อย {fund.Name}")
            .PostAsync(actorUserId?.ToString(), ct);
        txn.JournalEntryId = je.Id;
        if (evidenceId is Guid evId) AddEvidenceAudit(companyId, txn, evidence!, evId, actorUserId);

        await _db.SaveChangesAsync(ct);
        return txn;
    }

    public async Task<IReadOnlyList<PettyCashFundResponse>> ListAsync(Guid companyId, CancellationToken ct = default) =>
        await _db.PettyCashFunds.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted)
            .OrderBy(f => f.Name)
            .Select(f => new PettyCashFundResponse(
                f.Id, f.Name, f.CustodianUserId, f.ImprestAmount, f.CurrentBalance,
                f.LinkedAccountId,
                f.LinkedAccount != null ? f.LinkedAccount.AccountCode : null,
                f.LinkedAccount != null ? f.LinkedAccount.AccountName : null,
                f.IsActive,
                _db.PettyCashTransactions.Any(t => t.CompanyId == companyId && t.FundId == f.Id
                    && !t.IsDeleted && t.JournalEntryId != null)))
            .ToListAsync(ct);

    public async Task<PettyCashRegister> GetRegisterAsync(Guid companyId, Guid fundId,
        DateTime? fromDate, DateTime? toDate, CancellationToken ct = default)
    {
        var fund = await _db.PettyCashFunds.AsNoTracking()
            .Where(f => f.Id == fundId && f.CompanyId == companyId && !f.IsDeleted)
            .Select(f => new
            {
                f.Id, f.Name, f.ImprestAmount, f.CurrentBalance,
                Account = f.LinkedAccount != null ? f.LinkedAccount.AccountCode + " " + f.LinkedAccount.AccountName : null,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบกองทุนเงินสดย่อย");
        var q = _db.PettyCashTransactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.FundId == fundId && !t.IsDeleted);
        // วันที่ล้วน = รวมทั้งวัน (ช่วงเดียวกับรายงานอื่น — "ถึงวันที่" ไม่ตัดวันสุดท้าย)
        if (fromDate.HasValue) q = q.Where(t => t.TransactionDate >= fromDate.Value.Date);
        if (toDate.HasValue)
        {
            var end = toDate.Value.TimeOfDay == TimeSpan.Zero ? toDate.Value.Date.AddDays(1) : toDate.Value.AddTicks(1);
            q = q.Where(t => t.TransactionDate < end);
        }
        var txns = await q
            .OrderBy(t => t.TransactionDate).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new
            {
                t.Id, t.TransactionDate, t.Type, t.Description, t.ReceiptReference, t.Amount,
                AccCode = t.ExpenseAccount != null ? t.ExpenseAccount.AccountCode : null,
                AccName = t.ExpenseAccount != null ? t.ExpenseAccount.AccountName : null,
                t.ProjectId, t.JournalEntryId, t.IsNonDeductible, t.NonDeductibleRuleCode, t.CreatedAt,
            })
            .ToListAsync(ct);
        var jeIds = txns.Where(t => t.JournalEntryId.HasValue).Select(t => t.JournalEntryId!.Value).Distinct().ToList();
        var jeNumbers = jeIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && jeIds.Contains(j.Id))
                .ToDictionaryAsync(j => j.Id, j => j.EntryNumber, ct);
        var projectIds = txns.Where(t => t.ProjectId.HasValue).Select(t => t.ProjectId!.Value).Distinct().ToList();
        var projects = projectIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Projects.AsNoTracking()
                .Where(p => p.CompanyId == companyId && projectIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Code + " " + p.Name, ct);
        var txnIds = txns.Select(t => t.Id).ToList();
        var evidence = txnIds.Count == 0 ? new Dictionary<Guid, List<PettyCashEvidenceRef>>()
            : (await _db.FileAttachments.AsNoTracking()
                .Where(a => a.CompanyId == companyId && a.EntityType == PettyCashEvidence.EntityType && txnIds.Contains(a.EntityId))
                .OrderBy(a => a.CreatedAt)
                .Select(a => new { a.EntityId, a.Id, a.OriginalFileName, a.ContentType })
                .ToListAsync(ct))
              .GroupBy(a => a.EntityId)
              .ToDictionary(g => g.Key, g => g.Select(a => new PettyCashEvidenceRef(a.Id, a.OriginalFileName, a.ContentType)).ToList());
        var rows = txns.Select(t => new PettyCashRegisterRow(
            t.Id, t.TransactionDate, t.Type, PettyCashRegister.TypeLabel(t.Type), t.Description, t.ReceiptReference, t.Amount,
            Math.Max(0m, PettyCashFundGlRule.FundEffect(t.Type, t.Amount) ?? 0m),
            Math.Max(0m, -(PettyCashFundGlRule.FundEffect(t.Type, t.Amount) ?? 0m)),
            t.AccCode, t.AccName,
            t.ProjectId.HasValue && projects.TryGetValue(t.ProjectId.Value, out var pl) ? pl : null,
            t.JournalEntryId,
            t.JournalEntryId.HasValue && jeNumbers.TryGetValue(t.JournalEntryId.Value, out var jn) ? jn : null,
            t.IsNonDeductible, t.NonDeductibleRuleCode, t.CreatedAt,
            evidence.TryGetValue(t.Id, out var ev) ? ev : new List<PettyCashEvidenceRef>())).ToList();
        return new PettyCashRegister(fund.Id, fund.Name, fund.ImprestAmount, fund.CurrentBalance, fund.Account,
            fromDate, toDate, rows, rows.Sum(r => r.AmountIn), rows.Sum(r => r.AmountOut));
    }

    public Task<IReadOnlyList<PettyCashTransaction>> ListTransactionsAsync(Guid companyId, Guid fundId,
        DateTime? fromDate, DateTime? toDate, CancellationToken ct = default)
    {
        var q = _db.PettyCashTransactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.FundId == fundId && !t.IsDeleted);
        if (fromDate.HasValue) q = q.Where(t => t.TransactionDate >= fromDate.Value);
        if (toDate.HasValue) q = q.Where(t => t.TransactionDate <= toDate.Value);
        return q.OrderByDescending(t => t.TransactionDate).Take(500).ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<PettyCashTransaction>)t.Result, ct);
    }

    /// <summary>จำนวนใบสำคัญจากทางอื่นที่แสดงต่อผัง (ล่าสุดก่อน) — นับครบเสมอใน <c>UnattributedCount</c></summary>
    public const int MaxExternalEntriesShown = 200;

    /// <summary>ใบสำคัญที่นับยอด — เหมือนยอดผังที่หน้าบัญชีธนาคาร (<c>BankService.GetBankAccountsAsync</c>): ลงบัญชีแล้ว + ถูกกลับรายการ
    /// (ใบกลับรายการเองก็ Posted ⇒ สุทธิเป็นศูนย์)</summary>
    private IQueryable<Guid> CountedEntryIds(Guid companyId) => _db.JournalEntries
        .Where(j => j.CompanyId == companyId && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed))
        .Select(j => j.Id);

    public async Task<IReadOnlyList<PettyCashGlRecon>> GetGlReconciliationAsync(Guid companyId, CancellationToken ct = default)
    {
        var funds = await _db.PettyCashFunds.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted && f.LinkedAccountId != null)
            .OrderBy(f => f.Name)
            .Select(f => new { f.Id, f.Name, f.CurrentBalance, f.IsActive, Gl = f.LinkedAccountId!.Value })
            .ToListAsync(ct);
        if (funds.Count == 0) return Array.Empty<PettyCashGlRecon>();
        var glIds = funds.Select(f => f.Gl).Distinct().ToList();
        var accounts = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && glIds.Contains(a.Id))
            .Select(a => new { a.Id, a.AccountCode, a.AccountName })
            .ToDictionaryAsync(a => a.Id, ct);
        var counted = CountedEntryIds(companyId);
        var balances = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => glIds.Contains(l.AccountId) && counted.Contains(l.JournalEntryId))
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Net = g.Sum(l => l.DebitAmount - l.CreditAmount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Net, ct);
        // ใบที่แตะผังกองทุน แต่ไม่มีแถวทะเบียนของ "กองทุนที่ผูกผังนั้น" อ้างถึง (ใบเติมเงินที่ใช้ผังกองทุนอื่นเป็นต้นทาง ก็โผล่ที่ผังต้นทางด้วย)
        var external = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => glIds.Contains(l.AccountId) && counted.Contains(l.JournalEntryId)
                && !_db.PettyCashTransactions.Any(t => t.CompanyId == companyId && !t.IsDeleted
                    && t.JournalEntryId == l.JournalEntryId && t.Fund.LinkedAccountId == l.AccountId))
            .GroupBy(l => new { l.AccountId, l.JournalEntryId })
            .Select(g => new { g.Key.AccountId, g.Key.JournalEntryId, Net = g.Sum(l => l.DebitAmount - l.CreditAmount) })
            .ToListAsync(ct);
        external = external.Where(e => e.Net != 0m).ToList();
        var jeIds = external.Select(e => e.JournalEntryId).Distinct().ToList();
        var headers = new Dictionary<Guid, (string No, DateTime Date, string? Desc, string? Ref)>();
        if (jeIds.Count > 0)
            foreach (var j in await _db.JournalEntries.AsNoTracking()
                         .Where(x => x.CompanyId == companyId && jeIds.Contains(x.Id))
                         .Select(x => new { x.Id, x.EntryNumber, x.EntryDate, x.Description, x.Reference })
                         .ToListAsync(ct))
                headers[j.Id] = (j.EntryNumber, j.EntryDate, j.Description, j.Reference);

        // แถวทะเบียนที่อ้างใบสำคัญซึ่ง GL ไม่นับแล้ว (ใบถูกลบ · ร่างที่ถูกยกเลิก) — ยอดกองทุนยังนับ ⇒ ผลต่างที่เดิมขึ้นแค่ "Difference" ไม่มีทางแก้
        var orphanRows = await _db.PettyCashTransactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.JournalEntryId != null
                && t.Fund.LinkedAccountId != null && glIds.Contains(t.Fund.LinkedAccountId.Value)
                && !counted.Contains(t.JournalEntryId!.Value))
            .OrderByDescending(t => t.TransactionDate)
            .Select(t => new { t.Id, t.FundId, FundName = t.Fund.Name, Gl = t.Fund.LinkedAccountId!.Value, t.TransactionDate, t.Type, t.Amount,
                JeId = t.JournalEntryId!.Value, t.ReceiptReference })
            .ToListAsync(ct);
        var orphanJeIds = orphanRows.Select(o => o.JeId).Distinct().ToList();
        var orphanJes = orphanJeIds.Count == 0 ? new Dictionary<Guid, (string No, bool Deleted, string Status)>()
            : (await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(j => j.CompanyId == companyId && orphanJeIds.Contains(j.Id))
                .Select(j => new { j.Id, j.EntryNumber, j.IsDeleted, j.Status })
                .ToListAsync(ct))
              .ToDictionary(j => j.Id, j => (No: j.EntryNumber, Deleted: j.IsDeleted, Status: j.Status.ToString()));

        var glOnly = await _db.PettyCashTransactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.Type == PettyCashFundGlRule.GlOnlyType
                && t.Fund.LinkedAccountId != null && glIds.Contains(t.Fund.LinkedAccountId.Value)
                // ใบที่หายจาก GL ไม่นับเป็น "ปรับเฉพาะ GL" (GL ไม่มียอดนั้นแล้ว) — แถวนั้นอยู่ในรายการค้างให้ย้อนผลแทน
                && t.JournalEntryId != null && counted.Contains(t.JournalEntryId.Value))
            .OrderByDescending(t => t.TransactionDate)
            .Select(t => new { t.Id, Gl = t.Fund.LinkedAccountId!.Value, t.JournalEntryId, t.ReceiptReference, t.TransactionDate, t.Amount, t.Description })
            .ToListAsync(ct);

        var result = new List<PettyCashGlRecon>();
        foreach (var gl in glIds)
        {
            var glFunds = funds.Where(f => f.Gl == gl).Select(f => new PettyCashGlFund(f.Id, f.Name, f.CurrentBalance, f.IsActive)).ToList();
            var activeFunds = glFunds.Where(f => f.IsActive).ToList();
            Guid? suggested = activeFunds.Count == 1 ? activeFunds[0].FundId : null;
            var ext = external.Where(e => e.AccountId == gl)
                .Select(e =>
                {
                    headers.TryGetValue(e.JournalEntryId, out var h);
                    var mv = PettyCashFundGlRule.ExternalMovement(e.Net)!.Value;
                    return new PettyCashExternalEntry(e.JournalEntryId, h.No ?? "(ไม่พบเลขใบสำคัญ)", h.Date, h.Desc, h.Ref, e.Net,
                        mv.Type, PettyCashRegister.TypeLabel(mv.Type), mv.Amount, suggested);
                })
                .OrderByDescending(e => e.EntryDate).ThenBy(e => e.EntryNumber)
                .ToList();
            var glBalance = balances.TryGetValue(gl, out var b) ? b : 0m;
            var fundsTotal = glFunds.Sum(f => f.CurrentBalance);
            var glOnlyRows = glOnly.Where(g => g.Gl == gl)
                .Select(g => new PettyCashGlOnlyEntry(g.Id, g.JournalEntryId ?? Guid.Empty, g.ReceiptReference, g.TransactionDate, g.Amount, g.Description))
                .ToList();
            var glOnlyNet = glOnlyRows.Sum(g => g.NetDebit);
            var orphans = orphanRows.Where(o => o.Gl == gl).Select(o =>
            {
                var found = orphanJes.TryGetValue(o.JeId, out var j);
                return new PettyCashOrphanEntry(o.Id, o.FundId, o.FundName, o.TransactionDate, o.Type, PettyCashRegister.TypeLabel(o.Type), o.Amount,
                    o.JeId, found ? j.No : o.ReceiptReference, PettyCashFundGlRule.OrphanReason(found, found && j.Deleted, found ? j.Status : null),
                    -(PettyCashFundGlRule.FundEffect(o.Type, o.Amount) ?? 0m));
            }).ToList();
            // ส่วนที่ทำเครื่องหมาย "ปรับเฉพาะ GL" = GL ต่างจากกองทุนโดยตั้งใจ ⇒ เทียบ GL กับ (ยอดกองทุน + ส่วนนั้น)
            var status = PettyCashFundGlRule.Classify(glBalance, fundsTotal + glOnlyNet, ext.Count, orphans.Count);
            accounts.TryGetValue(gl, out var acc);
            result.Add(new PettyCashGlRecon(gl, acc?.AccountCode ?? "", acc?.AccountName ?? "(ไม่พบผัง — อาจถูกลบ)",
                glBalance, fundsTotal, glBalance - fundsTotal - glOnlyNet, status.ToString(), PettyCashFundGlRule.StatusLabel(status),
                glFunds.Count > 1, glFunds, ext.Count, ext.Sum(e => e.NetDebit), ext.Take(MaxExternalEntriesShown).ToList(),
                glOnlyNet, glOnlyRows, orphans.Count, orphans));
        }
        return result.OrderBy(r => r.AccountCode).ToList();
    }

    public async Task<PettyCashTransaction> AttributeExternalEntryAsync(Guid companyId, Guid fundId, Guid journalEntryId,
        Guid? actorUserId = null, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // ล็อกใบสำคัญก่อน (กันสองกองทุนบนผังร่วมระบุใบเดียวกัน) แล้วล็อกแถวกองทุน (กัน lost update กับเบิก/เติม) — ตรวจซ้ำใต้ล็อกทั้งคู่
        await LockEntryAsync(companyId, journalEntryId, ct);
        await LockFundRowAsync(companyId, fundId, ct);
        var fund = await FindFundAsync(companyId, fundId, ct);
        RequireFundOpen(fund);
        if (!fund.LinkedAccountId.HasValue)
            throw new BusinessRuleException(PettyCashJePlan.NoFundAccountMessage, PettyCashJePlan.RuleNoFundAccount);
        var gl = fund.LinkedAccountId.Value;
        var je = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.Id == journalEntryId && j.CompanyId == companyId
                && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed))
            .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.Description })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญนี้ในบริษัท (หรือยังไม่ได้ลงบัญชี) — รีเฟรชส่วน “กระทบยอดกับผังบัญชี” แล้วเลือกใหม่");
        var net = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntryId == je.Id && l.AccountId == gl)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount), ct) ?? 0m;
        var mv = PettyCashFundGlRule.ExternalMovement(net)
            ?? throw new BusinessRuleException(
                $"ใบสำคัญ {je.EntryNumber} ไม่ได้ลงผังของกองทุน {fund.Name} (หรือยอดสุทธิเป็นศูนย์) — เลือกกองทุนที่ผูกผังเดียวกับใบสำคัญ",
                PettyCashFundGlRule.RuleAttribute);
        if (await _db.PettyCashTransactions.AnyAsync(t => t.CompanyId == companyId && !t.IsDeleted
                && t.JournalEntryId == je.Id && t.Fund.LinkedAccountId == gl, ct))
            throw new BusinessRuleException(
                $"ใบสำคัญ {je.EntryNumber} อยู่ในทะเบียนกองทุนที่ใช้ผังนี้แล้ว — ไม่ต้องระบุซ้ำ (รีเฟรชหน้า)", PettyCashFundGlRule.RuleAttribute, 409);

        fund.CurrentBalance += PettyCashFundGlRule.FundEffect(mv.Type, mv.Amount)!.Value;   // ตัวตัดสินผลต่อยอดตัวเดียว
        fund.UpdatedAt = DateTime.UtcNow;
        var txn = new PettyCashTransaction
        {
            CompanyId = companyId,
            FundId = fund.Id,
            TransactionDate = je.EntryDate,
            Amount = mv.Amount,
            Type = mv.Type,
            Description = $"รายการจากทางอื่น ({je.EntryNumber}): {je.Description}".Trim(),
            ReceiptReference = je.EntryNumber,
            JournalEntryId = je.Id,   // อ้างใบเดิม — ไม่ลง JE ใหม่ (GL ขยับไปแล้วตอนออกใบ)
        };
        _db.PettyCashTransactions.Add(txn);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorUserId,
            EntityType = nameof(PettyCashTransaction),
            EntityId = txn.Id.ToString(),
            Action = AuditAction.Create,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "petty-cash-entry-attributed",
                journalEntryId = je.Id,
                entryNumber = je.EntryNumber,
                net,
                type = mv.Type,
                amount = mv.Amount,
                fundId = fund.Id,
                fundBalanceAfter = fund.CurrentBalance,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return txn;
    }

    public async Task<PettyCashTransaction> MarkGlOnlyAsync(Guid companyId, Guid accountId, Guid journalEntryId, string? note,
        Guid? actorUserId = null, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockEntryAsync(companyId, journalEntryId, ct);
        // แถวต้องผูกกองทุน (FK) — ใช้กองทุนบนผังนั้น (เปิดใช้ก่อน · ชื่อ) เป็นที่เก็บเครื่องหมาย · ยอดกองทุนไม่ขยับ
        var fund = await _db.PettyCashFunds.AsNoTracking()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted && f.LinkedAccountId == accountId)
            .OrderByDescending(f => f.IsActive).ThenBy(f => f.Name)
            .Select(f => new { f.Id, f.Name })
            .FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("ผังนี้ไม่ได้เป็นผังของกองทุนเงินสดย่อยใด — รีเฟรชส่วน “กระทบยอดกับผังบัญชี”", PettyCashFundGlRule.RuleAttribute);
        var je = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.Id == journalEntryId && j.CompanyId == companyId
                && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed))
            .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.Description })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบใบสำคัญนี้ในบริษัท (หรือยังไม่ได้ลงบัญชี) — รีเฟรชส่วน “กระทบยอดกับผังบัญชี” แล้วเลือกใหม่");
        var net = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntryId == je.Id && l.AccountId == accountId)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount), ct) ?? 0m;
        if (net == 0m)
            throw new BusinessRuleException($"ใบสำคัญ {je.EntryNumber} ไม่ได้ลงผังนี้ (หรือยอดสุทธิเป็นศูนย์) — ไม่ต้องทำเครื่องหมาย", PettyCashFundGlRule.RuleAttribute);
        if (await _db.PettyCashTransactions.AnyAsync(t => t.CompanyId == companyId && !t.IsDeleted
                && t.JournalEntryId == je.Id && t.Fund.LinkedAccountId == accountId, ct))
            throw new BusinessRuleException(
                $"ใบสำคัญ {je.EntryNumber} ถูกระบุกองทุน/ทำเครื่องหมายบนผังนี้แล้ว (รีเฟรชหน้า)", PettyCashFundGlRule.RuleAttribute, 409);
        var txn = new PettyCashTransaction
        {
            CompanyId = companyId,
            FundId = fund.Id,
            TransactionDate = je.EntryDate,
            Amount = net,   // มีเครื่องหมาย — ไม่ใช่เงินเข้า/ออกกองทุน (ทะเบียนไม่นับเข้า/ออก)
            Type = PettyCashFundGlRule.GlOnlyType,
            Description = $"ไม่กระทบกองทุน — ปรับเฉพาะ GL ({je.EntryNumber})" + (string.IsNullOrWhiteSpace(note) ? "" : ": " + note.Trim()),
            ReceiptReference = je.EntryNumber,
            JournalEntryId = je.Id,
        };
        _db.PettyCashTransactions.Add(txn);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorUserId,
            EntityType = nameof(PettyCashTransaction),
            EntityId = txn.Id.ToString(),
            Action = AuditAction.Create,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "petty-cash-entry-gl-only",
                journalEntryId = je.Id,
                entryNumber = je.EntryNumber,
                accountId,
                net,
                fundId = fund.Id,
                note = note?.Trim(),
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return txn;
    }

    public async Task UndoGlOnlyAsync(Guid companyId, Guid transactionId, Guid? actorUserId = null, CancellationToken ct = default)
    {
        var txn = await _db.PettyCashTransactions
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.CompanyId == companyId && !t.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบเครื่องหมายนี้ (อาจถูกยกเลิกไปแล้ว) — รีเฟรชหน้า");
        if (txn.Type != PettyCashFundGlRule.GlOnlyType)
            throw new BusinessRuleException("ยกเลิกได้เฉพาะเครื่องหมาย “ไม่กระทบกองทุน (ปรับเฉพาะ GL)” — รายการเบิก/เติม/ระบุกองทุนกลับรายการด้วยใบสำคัญ",
                PettyCashFundGlRule.RuleAttribute);
        txn.IsDeleted = true;
        txn.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorUserId,
            EntityType = nameof(PettyCashTransaction),
            EntityId = txn.Id.ToString(),
            Action = AuditAction.Delete,
            OldValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "petty-cash-entry-gl-only-undone",
                journalEntryId = txn.JournalEntryId,
                entryNumber = txn.ReceiptReference,
                net = txn.Amount,
                fundId = txn.FundId,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    // ══════════════ ส่วน D — รายการค้างที่อ้างใบสำคัญซึ่งหายจาก GL · ปิด/เปิดกองทุน (2026-11-02 รอบ 2) ══════════════

    public async Task<PettyCashFund> ReverseOrphanedEntryAsync(Guid companyId, Guid transactionId, Guid? actorUserId = null, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var head = await _db.PettyCashTransactions.AsNoTracking()
            .Where(t => t.Id == transactionId && t.CompanyId == companyId && !t.IsDeleted)
            .Select(t => new { t.FundId, t.JournalEntryId })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบรายการเงินสดย่อยนี้ (อาจถูกย้อนผลไปแล้ว) — รีเฟรชหน้า");
        if (head.JournalEntryId is not Guid jeId)
            throw new BusinessRuleException("รายการนี้ไม่ได้อ้างใบสำคัญ — ไม่ใช่รายการค้างจากใบที่ถูกลบ/ยกเลิก", PettyCashFundGlRule.RuleOrphan);
        // ล็อกต่อใบ (ตัวเดียวกับระบุกองทุน/ปรับเฉพาะ GL) แล้วล็อกแถวกองทุน — ตรวจซ้ำทุกอย่างใต้ล็อก
        await LockEntryAsync(companyId, jeId, ct);
        await LockFundRowAsync(companyId, head.FundId, ct);
        var fund = await FindFundAsync(companyId, head.FundId, ct);
        var txn = await _db.PettyCashTransactions
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.CompanyId == companyId && !t.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบรายการเงินสดย่อยนี้ (อาจถูกย้อนผลไปแล้ว) — รีเฟรชหน้า");
        // ใบยังถูกนับใน GL = ไม่ใช่รายการค้าง (ห้ามย้อนผลของรายการปกติ — ถ้าต้องการยกเลิกให้กลับรายการใบสำคัญ แล้วระบุใบกลับเข้ากองทุน)
        if (await CountedEntryIds(companyId).AnyAsync(id => id == jeId, ct))
            throw new BusinessRuleException(
                "ใบสำคัญของรายการนี้ยังอยู่ในบัญชี — ย้อนผลได้เฉพาะรายการที่ใบสำคัญถูกลบ/ยกเลิกแล้ว · ถ้าต้องการยกเลิกรายการนี้ ให้กลับรายการใบสำคัญที่หน้า "
                + "“สมุดรายวันทั่วไป” แล้วระบุใบกลับรายการเข้ากองทุนที่ส่วน “กระทบยอดกับผังบัญชี”", PettyCashFundGlRule.RuleOrphan, 409);
        var effect = PettyCashFundGlRule.FundEffect(txn.Type, txn.Amount)
            ?? throw new BusinessRuleException($"รายการชนิด “{PettyCashRegister.TypeLabel(txn.Type)}” ย้อนผลอัตโนมัติไม่ได้ — ปรับยอดด้วยมือผ่านผู้ดูแลระบบ",
                PettyCashFundGlRule.RuleOrphan);
        // กองทุนที่ปิดแล้วห้ามขยับยอด — แต่แถวที่ไม่มีผลต่อยอด (ปรับเฉพาะ GL) ย้อนได้แม้กองทุนปิด (ไม่มีอะไรขยับ)
        if (effect != 0m) RequireFundOpen(fund);
        var jeState = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.Id == jeId && j.CompanyId == companyId)
            .Select(j => new { j.EntryNumber, j.IsDeleted, j.Status })
            .FirstOrDefaultAsync(ct);
        // ร่างที่ยังไม่ลบ/ยกเลิก = ยังอาจถูกลงบัญชี — ห้ามย้อนผลแทนผู้ใช้ (ลงบัญชีแล้วกองทุนจะขาดสองครั้ง)
        if (jeState is { IsDeleted: false, Status: JournalEntryStatus.Draft })
            throw new BusinessRuleException(
                $"ใบสำคัญ {jeState.EntryNumber} ยังเป็นแบบร่าง — ลงบัญชีใบนั้น (ยอดจะตรงเอง) หรือยกเลิก/ลบใบนั้นก่อน แล้วค่อยย้อนผลต่อกองทุน",
                PettyCashFundGlRule.RuleOrphan, 409);
        var before = fund.CurrentBalance;
        fund.CurrentBalance -= effect;
        fund.UpdatedAt = DateTime.UtcNow;
        txn.IsDeleted = true;
        txn.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorUserId,
            EntityType = nameof(PettyCashTransaction),
            EntityId = txn.Id.ToString(),
            Action = AuditAction.Delete,
            OldValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "petty-cash-orphan-reversed",
                fundId = fund.Id,
                transactionType = txn.Type,
                amount = txn.Amount,
                journalEntryId = jeId,
                entryNumber = jeState?.EntryNumber ?? txn.ReceiptReference,
                reason = PettyCashFundGlRule.OrphanReason(jeState != null, jeState?.IsDeleted == true, jeState?.Status.ToString()),
                fundBalanceBefore = before,
                fundBalanceAfter = fund.CurrentBalance,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return fund;
    }

    public async Task<PettyCashClosePreview> GetClosePreviewAsync(Guid companyId, Guid fundId, CancellationToken ct = default)
    {
        var fund = await _db.PettyCashFunds.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fundId && f.CompanyId == companyId && !f.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบกองทุนเงินสดย่อยนี้ (อาจถูกลบไปแล้ว) — รีเฟรชหน้าแล้วเลือกกองทุนใหม่");
        return await EvaluateCloseAsync(companyId, fund, acknowledged: false, ct);
    }

    /// <summary>ตัวตัดสินเดียวของ "ปิดกองทุนได้ไหม" — พรีวิวกับการปิดจริงใช้ตัวนี้ (ห้ามมีสำเนาเงื่อนไขในหน้าเว็บ)</summary>
    private async Task<PettyCashClosePreview> EvaluateCloseAsync(Guid companyId, PettyCashFund fund, bool acknowledged, CancellationToken ct)
    {
        int unattributed = 0, orphaned = 0, ownOrphaned = 0;
        decimal difference = 0m;
        string? accountLabel = null;
        if (fund.LinkedAccountId is Guid gl)
        {
            var recon = (await GetGlReconciliationAsync(companyId, ct)).FirstOrDefault(r => r.AccountId == gl);
            if (recon != null)
            {
                unattributed = recon.UnattributedCount;
                orphaned = recon.OrphanedCount;
                ownOrphaned = (recon.OrphanedEntries ?? Array.Empty<PettyCashOrphanEntry>()).Count(o => o.FundId == fund.Id);
                difference = recon.Difference;
                accountLabel = $"{recon.AccountCode} {recon.AccountName}".Trim();
            }
        }
        var negative = fund.CurrentBalance < 0m
            ? $"ยอดกองทุน {fund.Name} ติดลบ {fund.CurrentBalance:N2} บาท — ปิดไม่ได้ (คืนเงินติดลบไม่ได้) · เติมเงินให้ยอดเป็น 0 ก่อน "
              + "หรือตรวจรายการค้างที่ส่วน “กระทบยอดกับผังบัญชี”"
            : null;
        return new PettyCashClosePreview(fund.Id, fund.Name, fund.IsActive, fund.CurrentBalance, fund.CurrentBalance > 0m,
            unattributed, orphaned, difference, PettyCashFundGlRule.CloseBlocker(unattributed, orphaned, ownOrphaned, difference, acknowledged),
            negative, accountLabel, ownOrphaned,
            CanAcknowledge: PettyCashFundGlRule.CloseBlocker(unattributed, orphaned, ownOrphaned, difference, acknowledged: true) == null);
    }

    public async Task<PettyCashFund> CloseFundAsync(Guid companyId, Guid fundId, Guid? returnAccountId, Guid? returnBankAccountId,
        DateTime closeDate, string? note, bool acknowledgeOpenItems, Guid? actorUserId = null, CancellationToken ct = default)
    {
        closeDate = ThaiDate.CalendarDateUtc(closeDate);   // วันตามปฏิทินไทย (เหมือนเบิก/เติม)
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockFundRowAsync(companyId, fundId, ct);   // ยอดที่คืน = ยอดใต้ล็อก (เบิก/เติมพร้อมกันต้องรอ)
        var fund = await FindFundAsync(companyId, fundId, ct);
        if (!fund.IsActive)
            throw new BusinessRuleException($"กองทุน “{fund.Name}” ปิดไปแล้ว — รีเฟรชหน้า", PettyCashFundGlRule.RuleFundClosed, 409);
        var check = await EvaluateCloseAsync(companyId, fund, acknowledgeOpenItems, ct);
        if (check.NegativeBalanceMessage != null)
            throw new BusinessRuleException(check.NegativeBalanceMessage, PettyCashFundGlRule.RuleCloseNegative);
        if (check.Blocker != null)
            throw new BusinessRuleException(check.Blocker, PettyCashFundGlRule.RuleCloseOpenItems);

        var returned = fund.CurrentBalance;
        PettyCashTransaction? txn = null;
        Guid? returnGl = null;
        if (returned > 0m)
        {
            if (!fund.LinkedAccountId.HasValue)
                throw new BusinessRuleException(PettyCashJePlan.NoFundAccountMessage, PettyCashJePlan.RuleNoFundAccount);
            await RequirePeriodOpenAsync(companyId, closeDate, ct);
            await RequireFundAccountUsableAsync(companyId, fund, ct);
            // บัญชีที่รับคืน = ผู้ใช้เลือกเท่านั้น (ไม่เดาแม้มีธนาคารใบเดียว — เงินสดในลิ้นชักอาจเข้าเงินสดใหญ่ ไม่ใช่ธนาคาร)
            returnGl = await PickedAccountGuard.ResolveMoneyChannelAsync(_db, companyId, returnBankAccountId, returnAccountId, "คืนเงินเข้าบัญชี", ct);
            // คืนเข้าผังของกองทุนเงินสดย่อย (กองทุนใดก็ตาม) = เงินย้ายกองทุนโดยกองทุนปลายทางไม่รู้ ⇒ ปฏิเสธ (โอนระหว่างกองทุนใช้ “เติมเงิน” ของกองทุนปลายทาง)
            var targetIsPettyFund = returnGl is Guid rg && await _db.PettyCashFunds.AsNoTracking()
                .AnyAsync(f => f.CompanyId == companyId && !f.IsDeleted && f.LinkedAccountId == rg, ct);
            var plan = PettyCashJePlan.ForReturn(fund.LinkedAccountId, returnGl, returned, targetIsPettyFund);
            if (!plan.Ok) throw new BusinessRuleException(plan.Error!, plan.RuleCode);
            var desc = string.IsNullOrWhiteSpace(note) ? $"ปิดกองทุนเงินสดย่อย {fund.Name} — คืนเงินคงเหลือ" : note.Trim();
            var je = await JournalEntryBuilder.For(_db, companyId, closeDate)
                .Description($"คืนเงินสดย่อย (ปิดกองทุน) {fund.Name}")
                .Debit(plan.DebitAccountId, returned, $"รับคืนเงินสดย่อย {fund.Name}")
                .Credit(plan.CreditAccountId, returned, $"คืนเงินคงเหลือ ปิดกองทุน {fund.Name}")
                .PostAsync(actorUserId?.ToString(), ct);
            txn = new PettyCashTransaction
            {
                CompanyId = companyId,
                FundId = fund.Id,
                TransactionDate = closeDate,
                Amount = returned,
                Type = PettyCashFundGlRule.ReturnType,
                Description = desc,
                JournalEntryId = je.Id,
            };
            _db.PettyCashTransactions.Add(txn);
            fund.CurrentBalance = 0m;
        }
        fund.IsActive = false;
        fund.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorUserId,
            EntityType = nameof(PettyCashFund),
            EntityId = fund.Id.ToString(),
            Action = AuditAction.Update,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "petty-cash-fund-closed",
                fundName = fund.Name,
                balanceReturned = returned,
                returnAccountId = returnGl,
                returnBankAccountId = PickedAccountGuard.IsSet(returnBankAccountId) ? returnBankAccountId : null,
                returnTransactionId = txn?.Id,
                journalEntryId = txn?.JournalEntryId,
                closeDate,
                note = note?.Trim(),
                acknowledgedOpenItems = acknowledgeOpenItems,
                unattributedCount = check.UnattributedCount,
                orphanedCount = check.OrphanedCount,
                difference = check.Difference,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return fund;
    }

    public async Task<PettyCashFund> ReopenFundAsync(Guid companyId, Guid fundId, Guid? actorUserId = null, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await LockFundRowAsync(companyId, fundId, ct);
        var fund = await FindFundAsync(companyId, fundId, ct);
        if (fund.IsActive)
            throw new BusinessRuleException($"กองทุน “{fund.Name}” เปิดใช้อยู่แล้ว — รีเฟรชหน้า", PettyCashFundGlRule.RuleFundAlreadyOpen, 409);
        fund.IsActive = true;
        fund.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorUserId,
            EntityType = nameof(PettyCashFund),
            EntityId = fund.Id.ToString(),
            Action = AuditAction.Update,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "petty-cash-fund-reopened",
                fundName = fund.Name,
                balance = fund.CurrentBalance,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return fund;
    }
}
