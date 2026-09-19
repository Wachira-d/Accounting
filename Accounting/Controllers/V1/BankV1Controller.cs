using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers.V1;

/// <summary>
/// `/api/v1/bank` — ส่งรายการเดินบัญชีเข้ามา แล้วให้ระบบจับคู่กับเอกสาร/JE
///
/// เหมาะกับลูกค้าที่เก็บสมุดบัญชีไว้ที่ ERP ตัวเอง แต่อยากใช้เครื่องจับคู่
/// ของเรา (pattern learning + AI) แล้วเอาผลกลับไปกระทบยอดในระบบเขา
/// </summary>
[ApiController]
[Route("api/v1/bank")]
[Authorize]
public class BankV1Controller : PublicApiControllerBase
{
    private const string Feature = "bank.recon";

    private readonly IBankService _bank;
    private readonly ILogger<BankV1Controller> _logger;

    public BankV1Controller(AccountingDbContext db, IUsageMeteringService metering,
        IBankService bank, ILogger<BankV1Controller> logger) : base(db, metering)
    { _bank = bank; _logger = logger; }

    public record StatementLineRequest(
        DateTime TransactionDate, decimal Amount, string Direction,
        string? Description, string? Reference, string? Payee, string? ExternalId);

    public record ImportStatementRequest(Guid BankAccountId, List<StatementLineRequest> Lines);

    /// <summary>ผลจับคู่ต่อบรรทัด — เป็น record ไม่ใช่ anonymous type เพื่อให้
    /// นับ/กรองได้โดยไม่ต้อง reflection และ shape ของ v1 ถูกล็อกไว้ชัดเจน</summary>
    public record MatchResultDto(
        Guid BankTransactionId, string? ExternalId, bool Found, string? Type,
        List<Guid> SuggestedIds, decimal SuggestedTotal, decimal Difference, string Message);

    /// <summary>
    /// นำเข้ารายการเดินบัญชี แล้วคืนผลจับคู่ทันทีต่อบรรทัด
    ///
    /// **คิดเงินตามจำนวนบรรทัดที่ประมวลผล ไม่ใช่ตามจำนวนที่จับคู่สำเร็จ** —
    /// เพราะงานที่เราทำคือการวิเคราะห์ทุกบรรทัด และการคิดตาม "สำเร็จ" จะเปิด
    /// ช่องให้เถียงกันว่าอันนี้จับผิดไม่จ่าย ซึ่งไม่มีวันจบ
    /// </summary>
    [HttpPost("statements")]
    public async Task<IActionResult> ImportStatement([FromBody] ImportStatementRequest req, CancellationToken ct)
    {
        var (ctx, error) = await ResolveCallerAsync("bank:write", Feature, ct);
        if (error != null) return error;

        if (req?.Lines == null || req.Lines.Count == 0)
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาส่งรายการเดินบัญชีอย่างน้อย 1 บรรทัด"));
        if (req.Lines.Count > 2000)
            return BadRequest(new ApiResponse<string>(false, null,
                "ส่งได้ครั้งละไม่เกิน 2,000 บรรทัด — แบ่งเป็นหลายชุด"));

        var account = await Db.Set<BankAccount>().AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == req.BankAccountId && a.CompanyId == ctx!.CompanyId && !a.IsDeleted, ct);
        if (account == null)
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบบัญชีธนาคารในบริษัทนี้"));

        var created = new List<BankTransaction>();
        var skipped = 0;

        foreach (var l in req.Lines)
        {
            // กันนำเข้าซ้ำด้วย ExternalId ของลูกค้า — ยิงไฟล์เดิมซ้ำต้องไม่เกิด
            // รายการซ้ำและต้องไม่ถูกคิดเงินรอบสอง
            if (!string.IsNullOrWhiteSpace(l.ExternalId))
            {
                var dup = await Db.Set<BankTransaction>().AsNoTracking()
                    .AnyAsync(t => t.CompanyId == ctx!.CompanyId
                                && t.BankAccountId == req.BankAccountId
                                && t.Reference == l.ExternalId, ct);
                if (dup) { skipped++; continue; }
            }

            var isDeposit = string.Equals(l.Direction, "In", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(l.Direction, "Deposit", StringComparison.OrdinalIgnoreCase);

            created.Add(new BankTransaction
            {
                CompanyId = ctx!.CompanyId,
                BankAccountId = req.BankAccountId,
                TransactionDate = l.TransactionDate,
                TransactionType = isDeposit ? BankTransactionType.Deposit : BankTransactionType.Withdrawal,
                Amount = Math.Abs(l.Amount),
                Description = l.Description,
                Reference = string.IsNullOrWhiteSpace(l.ExternalId) ? l.Reference : l.ExternalId,
                Payee = l.Payee,
                ReconciliationStatus = ReconciliationStatus.Unmatched,
                CreatedBy = "api:v1",
            });
        }

        if (created.Count == 0)
            return Ok(new ApiResponse<object>(true, new { imported = 0, skipped, matches = Array.Empty<object>() },
                "ทุกบรรทัดถูกนำเข้าไปแล้วก่อนหน้านี้ — ไม่มีการคิดค่าบริการ"));

        Db.Set<BankTransaction>().AddRange(created);
        await Db.SaveChangesAsync(ct);

        // จับคู่ทีละบรรทัดด้วยเครื่องเดิม (ผ่าน pattern ที่เรียนรู้ไว้แล้วของ tenant นี้)
        var matches = new List<MatchResultDto>();
        foreach (var txn in created)
        {
            try
            {
                var s = await _bank.SuggestMatchAsync(ctx!.CompanyId, txn.Id);
                matches.Add(new MatchResultDto(txn.Id, txn.Reference, s.Found, s.Type,
                    s.SuggestedIds, s.SuggestedTotal, s.Difference, s.Message));
            }
            catch (Exception ex)
            {
                // บรรทัดเดียวพังต้องไม่ทำให้ทั้งชุดพัง
                _logger.LogWarning(ex, "จับคู่ไม่สำเร็จ txn={Txn}", txn.Id);
                matches.Add(new MatchResultDto(txn.Id, txn.Reference, false, null,
                    new List<Guid>(), 0, 0, "จับคู่ไม่สำเร็จ"));
            }
        }

        var usage = await MeterAsync(ctx!, Feature, created.Count, "BankStatementImport", req.BankAccountId, ct);

        return Ok(new ApiResponse<object>(true, new
        {
            imported = created.Count,
            skipped,
            matched = matches.Count(m => m.Found),
            matches,
            billing = new { charged = usage.ChargedAmount, coveredByFreeQuota = usage.CoveredByFreeQuota },
        }));
    }

    public record ConfirmMatchRequest(Guid BankTransactionId, List<Guid> EntryIds);

    /// <summary>
    /// ยืนยันว่าเลือกชุดจับคู่ไหน — **การยืนยันนี้คือ feedback ที่สอนระบบ**
    /// (§7.3 ข้อ 2) ทำให้ pattern ของ tenant นี้แม่นขึ้นทุกครั้งที่ใช้งานตามปกติ
    /// โดยลูกค้าไม่ต้องส่งอะไรเพิ่ม. ไม่คิดเงินซ้ำ — เก็บไปแล้วตอนนำเข้า
    /// </summary>
    [HttpPost("matches/confirm")]
    public async Task<IActionResult> ConfirmMatch([FromBody] ConfirmMatchRequest req, CancellationToken ct)
    {
        var (ctx, error) = await ResolveCallerAsync("bank:write", Feature, ct);
        if (error != null) return error;

        if (req?.EntryIds == null || req.EntryIds.Count == 0)
            return BadRequest(new ApiResponse<string>(false, null, "กรุณาระบุรายการที่จับคู่"));

        var txn = await Db.Set<BankTransaction>()
            .FirstOrDefaultAsync(t => t.Id == req.BankTransactionId && t.CompanyId == ctx!.CompanyId, ct);
        if (txn == null)
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบรายการเดินบัญชี"));

        // ── ทางเข้าอื่นต้องเดินด่านเดียวกัน (F3 ข้อ 8 · ราก R5) ────────────
        // เดิมทางนี้เขียนสถานะ `Matched` **ตรงลงตาราง**: ไม่ผ่านด่านยอด
        // (`ValidateMatchAmountAsync`) · ไม่ผ่านด่านงวดบัญชี · ไม่ล็อกแถว ·
        // ไม่มี audit row · และ **ไม่บันทึกแพตเทิร์นการเรียนรู้เลย** ทั้งที่
        // doc-comment ข้างบนสัญญาว่า "การยืนยันนี้คือ feedback ที่สอนระบบ"
        // (defect class "ด่าน/คำสัญญาที่คอมเมนต์บอกว่ามี แต่ไม่มี")
        try
        {
            await _bank.BatchReconcileAsync(ctx!.CompanyId, new Models.DTOs.Bank.BatchReconcileRequest(
                new List<Models.DTOs.Bank.BatchReconcileItem>
                {
                    new(BankTransactionId: txn.Id,
                        MatchType: req.EntryIds.Count == 1 ? "JournalEntry" : "Multiple",
                        MatchedPaymentId: null,
                        MatchedJournalEntryId: req.EntryIds.Count == 1 ? req.EntryIds[0] : null,
                        MatchedEntryIds: req.EntryIds.Count == 1 ? null : req.EntryIds,
                        ConfidenceAtApply: null,
                        WasAiValidated: false,
                        AlternativesJson: null)
                }));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<string>(false, null, ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ApiResponse<string>(false, null, ex.Message));
        }

        // ผู้กระทำคือ **ระบบภายนอก** ไม่ใช่คนที่นั่งหน้าจอ — ป้ายต้องบอกตรง ๆ
        // (`BatchReconcileAsync` เขียน `Person(Guid.Empty)` เพราะไม่มี JWT)
        txn.ReconciledBy = Accounting.Helpers.BankMatchAttribution.ApiV1;
        await Db.SaveChangesAsync(ct);

        return Ok(new ApiResponse<object>(true,
            new { bankTransactionId = txn.Id, entryCount = req.EntryIds.Count },
            "ยืนยันการจับคู่แล้ว — ระบบจะจดจำรูปแบบนี้เพื่อให้ครั้งหน้าแม่นขึ้น"));
    }
}
