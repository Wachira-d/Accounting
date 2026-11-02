using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Forex;
using Accounting.Services.Implementations.Inventory;
using Accounting.Services.Implementations.PettyCash;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// SME operations — petty cash, stock count, FX revaluation. Each
/// is a small surface so they share a controller; can be split if/when
/// any grows.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/sme")]
[Authorize]
public class SmeOperationsController : ControllerBase
{
    // ── Petty cash ───────────────────────────────────────────────────
    // ข้อผิดพลาดทางธุรกิจโยน BusinessRuleException (ข้อความไทย + ทางไปต่อ + รหัสกฎ) ผ่าน ExceptionMiddleware ·
    // ไม่พบกองทุน = KeyNotFoundException (404) — เดิม "Fund not found." / "Insufficient petty cash…" ภาษาอังกฤษถึงผู้ใช้
    /// <param name="LinkedAccountId">ผังบัญชีของกองทุน (ChartOfAccounts.Id) · null = ใช้ผังมาตรฐาน 11112 ถ้ามี</param>
    /// <param name="AllowSharedAccount">true = ยอมใช้ผังเดียวกับกองทุนอื่น (ติ๊กเอง) · ค่าตั้งต้น false = ผัง 1 ผัง ↔ กองทุน 1 กองทุน (PETTY_CASH_AUDIT 2026-11-02)</param>
    public sealed record CreateFundRequest(string Name, Guid? CustodianUserId,
        decimal ImprestAmount, Guid? LinkedAccountId, bool AllowSharedAccount = false);

    // ฝ่ายค้านรอบ 2 P2(h): เปิดกองทุน = เลือกผังที่เบิก/เติมเงินลง (ตั้งค่าการลงบัญชี) ⇒ สิทธิ์เดียวกับแก้กองทุน — เดิมสร้างได้ทุกคนแต่แก้ไม่ได้ (ทางตัน)
    [HttpPost("petty-cash/funds")]
    [RequirePermission(PermissionKeys.JournalManage)]
    public async Task<ActionResult<ApiResponse<PettyCashFund>>> CreateFund(
        Guid companyId, [FromBody] CreateFundRequest req,
        [FromServices] IPettyCashService svc, CancellationToken ct)
    {
        var fund = await svc.CreateFundAsync(companyId, req.Name, req.CustodianUserId,
            req.ImprestAmount, req.LinkedAccountId, req.AllowSharedAccount, ct);
        return Ok(new ApiResponse<PettyCashFund>(true, fund, $"เปิดเงินสดย่อย {fund.Name}"));
    }

    /// <summary>แก้กองทุน / ผูกผังบัญชีของกองทุน — ทางออกของกองทุนที่เปิดไว้โดยไม่มีผัง (ข้อความ "ยังไม่ได้ผูกผัง" ชี้มาที่ปุ่มนี้) ·
    /// ช่องที่เป็น null = ไม่เปลี่ยน</summary>
    public sealed record UpdateFundRequest(string? Name, decimal? ImprestAmount, Guid? LinkedAccountId, bool AllowSharedAccount = false);

    // เปลี่ยนผังที่เบิก/เติมเงินลง = ตั้งค่าการลงบัญชี ⇒ ต้องมีสิทธิ์จัดการใบสำคัญ (endpoint ใหม่ — ไม่ปล่อยเป็น [Authorize] อย่างเดียว)
    [HttpPut("petty-cash/funds/{fundId:guid}")]
    [RequirePermission(PermissionKeys.JournalManage)]
    public async Task<ActionResult<ApiResponse<PettyCashFund>>> UpdateFund(
        Guid companyId, Guid fundId, [FromBody] UpdateFundRequest req,
        [FromServices] IPettyCashService svc, CancellationToken ct)
    {
        var fund = await svc.UpdateFundAsync(companyId, fundId, req.Name, req.ImprestAmount, req.LinkedAccountId, req.AllowSharedAccount, ct);
        return Ok(new ApiResponse<PettyCashFund>(true, fund, $"บันทึกกองทุน {fund.Name} แล้ว"));
    }

    [HttpGet("petty-cash/funds")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PettyCashFundResponse>>>> ListFunds(
        Guid companyId, [FromServices] IPettyCashService svc, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<PettyCashFundResponse>>(true, await svc.ListAsync(companyId, ct)));

    /// <summary>ผังที่เสนอเป็นค่าตั้งต้นของ “ผังบัญชีของกองทุน” (เซิร์ฟเวอร์ตัดสิน — หน้าไม่มีรหัส 11112 ของตัวเอง) · data = null เมื่อผังบริษัทไม่มี</summary>
    [HttpGet("petty-cash/fund-account-default")]
    public async Task<ActionResult<ApiResponse<PettyCashFundAccountOption?>>> DefaultFundAccount(
        Guid companyId, [FromServices] IPettyCashService svc, CancellationToken ct) =>
        Ok(new ApiResponse<PettyCashFundAccountOption?>(true, await svc.GetDefaultFundAccountAsync(companyId, ct)));

    public sealed record DisburseRequest(decimal Amount, string Description,
        Guid? ExpenseAccountId, string? ReceiptReference, DateTime TxnDate,
        Guid? ProjectId = null);

    // รับ JSON (เดิม) หรือ multipart + ใบเสร็จ (PETTY_CASH_AUDIT ส่วน C) — endpoint เดียว ด่านเดียว
    [HttpPost("petty-cash/funds/{fundId:guid}/disburse")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<PettyCashTransaction>>> Disburse(
        Guid companyId, Guid fundId, [FromServices] IPettyCashService svc, CancellationToken ct)
    {
        var (req, file) = await ReadPettyRequestAsync<DisburseRequest>(ct);
        var t = await svc.DisburseWithEvidenceAsync(companyId, fundId, req.Amount, req.Description,
            req.ExpenseAccountId, req.ReceiptReference, req.TxnDate, req.ProjectId, file, JwtHelper.GetUserIdFromClaims(User), ct);
        return Ok(new ApiResponse<PettyCashTransaction>(true, t, $"จ่ายเงินสดย่อย {req.Amount:N2}" + (file == null ? "" : " · แนบใบเสร็จแล้ว")));
    }

    /// <param name="SourceAccountId">ผังบัญชีต้นทางของเงินที่เติม <b>โดยตรง</b> (เงินสด/กระเป๋าเงิน — ChartOfAccounts.Id)</param>
    /// <param name="SourceBankAccountId">บัญชีธนาคารต้นทาง (BankAccounts.Id — เซิร์ฟเวอร์แปลงเป็นผังที่ผูก) · ส่งอย่างใดอย่างหนึ่ง ·
    /// จำเป็นเมื่อบริษัทมีบัญชีธนาคารที่ผูกผังมากกว่า 1 ใบ</param>
    public sealed record ReplenishRequest(decimal Amount, string? Notes, DateTime TxnDate,
        Guid? SourceAccountId = null, Guid? SourceBankAccountId = null);

    // รับ JSON (เดิม) หรือ multipart + สลิป · บริษัทตั้ง "ต้องแนบสลิป" ⇒ service ปฏิเสธทั้งสองรูปเมื่อไม่มีไฟล์ (PETTY_CASH_AUDIT ส่วน C)
    [HttpPost("petty-cash/funds/{fundId:guid}/replenish")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<PettyCashTransaction>>> Replenish(
        Guid companyId, Guid fundId, [FromServices] IPettyCashService svc, CancellationToken ct)
    {
        var (req, file) = await ReadPettyRequestAsync<ReplenishRequest>(ct);
        var t = await svc.ReplenishWithEvidenceAsync(companyId, fundId, req.Amount, req.Notes, req.TxnDate,
            req.SourceAccountId, req.SourceBankAccountId, file, JwtHelper.GetUserIdFromClaims(User), ct);
        return Ok(new ApiResponse<PettyCashTransaction>(true, t, file == null ? "เติมเงินสดย่อยแล้ว" : "เติมเงินสดย่อยแล้ว · แนบสลิปแล้ว"));
    }

    [HttpGet("petty-cash/funds/{fundId:guid}/transactions")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PettyCashTransaction>>>> ListTransactions(
        Guid companyId, Guid fundId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate,
        [FromServices] IPettyCashService svc, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<PettyCashTransaction>>(true,
            await svc.ListTransactionsAsync(companyId, fundId, fromDate, toDate, ct)));

    /// <summary>ทะเบียนเงินสดย่อยของกองทุน (ทุกรายการในช่วง · ชื่อผัง/โครงการ/เลขใบสำคัญแปลงแล้ว · ยอดรวมเข้า/ออกจากเซิร์ฟเวอร์) —
    /// ตารางรายการบนหน้า petty-cash.html (ทีม X5 2026-10-15) · ไฟล์ส่งออกใช้ <see cref="IPettyCashService.GetRegisterAsync"/> ตัวเดียวกัน</summary>
    [HttpGet("petty-cash/funds/{fundId:guid}/register")]
    public async Task<ActionResult<ApiResponse<PettyCashRegister>>> Register(
        Guid companyId, Guid fundId, [FromQuery] DateTime? fromDate, [FromQuery] DateTime? toDate,
        [FromServices] IPettyCashService svc, CancellationToken ct) =>
        Ok(new ApiResponse<PettyCashRegister>(true, await svc.GetRegisterAsync(companyId, fundId, fromDate, toDate, ct)));

    // ── หลักฐาน (สลิปเติม / ใบเสร็จเบิก) — PETTY_CASH_AUDIT ส่วน C · กติกาไฟล์ที่ Helpers/PettyCashEvidence ·
    //    เก็บผ่านที่เก็บไฟล์แนบกลาง (IFileAttachmentService) · ดู/ดาวน์โหลดผ่าน GET attachments/{id}/download (ด่าน + ตรวจบริษัท) ──
    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>อ่านไฟล์จากฟอร์ม → ตรวจชนิดจากไบต์จริง · ไม่มีไฟล์ = null · ไฟล์ผิด = BusinessRuleException ข้อความไทย</summary>
    private static async Task<PettyCashEvidenceFile?> ReadEvidenceAsync(IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0) return null;
        if (file.Length > PettyCashEvidence.MaxBytes)
            throw new BusinessRuleException($"ไฟล์หลักฐานใหญ่เกิน {PettyCashEvidence.MaxBytes / (1024 * 1024)} MB — ย่อรูป/บีบ PDF แล้วแนบใหม่", PettyCashEvidence.RuleBadFile);
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var (kind, error) = PettyCashEvidence.Check(bytes, file.FileName);
        if (kind is not { } k) throw new BusinessRuleException(error!, PettyCashEvidence.RuleBadFile);
        return new PettyCashEvidenceFile(PettyCashEvidence.DisplayName(file.FileName, k.Extension), k.ContentType, bytes);
    }

    /// <summary>คำขอเบิก/เติม รับได้สองรูปที่ endpoint เดิม (ไม่เพิ่มทางเข้าเขียนใหม่): JSON (API เดิม) หรือ multipart
    /// (<c>payload</c> = JSON รูปเดียวกัน + <c>evidence</c> = รูป/PDF ไม่บังคับ)</summary>
    private async Task<(T Request, PettyCashEvidenceFile? Evidence)> ReadPettyRequestAsync<T>(CancellationToken ct) where T : class
    {
        try
        {
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync(ct);
                var payload = form["payload"].ToString();
                var req = (string.IsNullOrWhiteSpace(payload) ? null : System.Text.Json.JsonSerializer.Deserialize<T>(payload, WebJson))
                    ?? throw new BusinessRuleException("ข้อมูลรายการไม่ครบ — รีเฟรชหน้าแล้วลองใหม่", "PETTY-FORM");
                return (req, await ReadEvidenceAsync(form.Files.GetFile("evidence"), ct));
            }
            var body = await System.Text.Json.JsonSerializer.DeserializeAsync<T>(Request.Body, WebJson, ct)
                ?? throw new BusinessRuleException("ข้อมูลรายการไม่ครบ — รีเฟรชหน้าแล้วลองใหม่", "PETTY-FORM");
            return (body, null);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new BusinessRuleException("ข้อมูลรายการอ่านไม่ได้ (รูปแบบตัวเลข/วันที่ไม่ถูกต้อง) — รีเฟรชหน้าแล้วลองใหม่", "PETTY-FORM");
        }
    }

    public sealed record PettyCashOptionsResponse(bool TopUpEvidenceRequired);

    /// <summary>ค่าตั้งที่หน้าเงินสดย่อยต้องรู้ (ช่องสลิปบังคับไหม) — ด่านจริงอยู่ที่ service</summary>
    [HttpGet("petty-cash/options")]
    public async Task<ActionResult<ApiResponse<PettyCashOptionsResponse>>> PettyCashOptions(
        Guid companyId, [FromServices] IPettyCashService svc, CancellationToken ct) =>
        Ok(new ApiResponse<PettyCashOptionsResponse>(true, new PettyCashOptionsResponse(await svc.IsTopUpEvidenceRequiredAsync(companyId, ct))));

    /// <summary>แนบหลักฐานเพิ่มให้รายการที่บันทึกแล้ว (multipart: <c>evidence</c>)</summary>
    // แนบภายหลัง = แก้หลักฐานของรายการที่ลงบัญชีแล้ว ⇒ สิทธิ์เดียวกับจัดการใบสำคัญ (ตอนเบิก/เติมแนบได้ทุกคนที่ทำรายการได้)
    [HttpPost("petty-cash/transactions/{transactionId:guid}/evidence")]
    [RequirePermission(PermissionKeys.JournalManage)]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<PettyCashEvidenceRef>>> AttachEvidence(
        Guid companyId, Guid transactionId, IFormFile? evidence,
        [FromServices] IPettyCashService svc, CancellationToken ct)
    {
        var file = await ReadEvidenceAsync(evidence, ct)
            ?? throw new BusinessRuleException("เลือกไฟล์รูปหรือ PDF ก่อนกดแนบ", PettyCashEvidence.RuleBadFile);
        var r = await svc.AttachEvidenceAsync(companyId, transactionId, file, JwtHelper.GetUserIdFromClaims(User), ct);
        return Ok(new ApiResponse<PettyCashEvidenceRef>(true, r, "แนบหลักฐานแล้ว"));
    }

    /// <summary>กระทบยอด "ผังของกองทุน (GL) ↔ ยอดกองทุน" + ใบสำคัญจากทางอื่นที่แตะผังกองทุนแต่ยังไม่ระบุกองทุน (PETTY_CASH_AUDIT 2026-11-02)</summary>
    [HttpGet("petty-cash/reconciliation")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PettyCashGlRecon>>>> Reconciliation(
        Guid companyId, [FromServices] IPettyCashService svc, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<PettyCashGlRecon>>(true, await svc.GetGlReconciliationAsync(companyId, ct)));

    public sealed record AttributeEntryRequest(Guid JournalEntryId);

    // ระบุกองทุนของใบสำคัญจากทางอื่น = เปลี่ยนยอดกองทุน (ทะเบียนเงิน) ⇒ สิทธิ์เดียวกับตั้งค่ากองทุน
    [HttpPost("petty-cash/funds/{fundId:guid}/attribute-entry")]
    [RequirePermission(PermissionKeys.JournalManage)]
    public async Task<ActionResult<ApiResponse<PettyCashTransaction>>> AttributeEntry(
        Guid companyId, Guid fundId, [FromBody] AttributeEntryRequest req,
        [FromServices] IPettyCashService svc, CancellationToken ct)
    {
        var t = await svc.AttributeExternalEntryAsync(companyId, fundId, req.JournalEntryId, JwtHelper.GetUserIdFromClaims(User), ct);
        return Ok(new ApiResponse<PettyCashTransaction>(true, t, $"บันทึกเข้าทะเบียนกองทุนแล้ว ({t.ReceiptReference})"));
    }

    public sealed record GlOnlyRequest(Guid AccountId, Guid JournalEntryId, string? Note);

    /// <summary>ใบสำคัญจากทางอื่นที่ "ไม่กระทบกองทุน (ปรับเฉพาะ GL)" — ออกจากรายการรอระบุ · ไม่ขยับยอดกองทุน · audit · ยกเลิกได้ (ฝ่ายค้านข้อ 3)</summary>
    [HttpPost("petty-cash/gl-only")]
    [RequirePermission(PermissionKeys.JournalManage)]
    public async Task<ActionResult<ApiResponse<PettyCashTransaction>>> MarkGlOnly(
        Guid companyId, [FromBody] GlOnlyRequest req, [FromServices] IPettyCashService svc, CancellationToken ct)
    {
        var t = await svc.MarkGlOnlyAsync(companyId, req.AccountId, req.JournalEntryId, req.Note, JwtHelper.GetUserIdFromClaims(User), ct);
        return Ok(new ApiResponse<PettyCashTransaction>(true, t, $"ทำเครื่องหมาย “ไม่กระทบกองทุน” แล้ว ({t.ReceiptReference})"));
    }

    [HttpPost("petty-cash/gl-only/{transactionId:guid}/undo")]
    [RequirePermission(PermissionKeys.JournalManage)]
    public async Task<ActionResult<ApiResponse<string>>> UndoGlOnly(
        Guid companyId, Guid transactionId, [FromServices] IPettyCashService svc, CancellationToken ct)
    {
        await svc.UndoGlOnlyAsync(companyId, transactionId, JwtHelper.GetUserIdFromClaims(User), ct);
        return Ok(new ApiResponse<string>(true, "ok", "ยกเลิกเครื่องหมายแล้ว — ใบสำคัญกลับไปรอระบุกองทุน"));
    }

    // ── Stock count ─────────────────────────────────────────────────
    public sealed record StartCountRequest(string CountNumber, Guid? WarehouseId,
        string CountType, List<Guid> ProductIds);

    [HttpPost("stock-counts")]
    public async Task<ActionResult<ApiResponse<StockCount>>> StartStockCount(
        Guid companyId, [FromBody] StartCountRequest req,
        [FromServices] IStockCountService svc, CancellationToken ct)
    {
        var sc = await svc.StartAsync(companyId, req.CountNumber, req.WarehouseId,
            req.CountType, req.ProductIds, ct);
        return Ok(new ApiResponse<StockCount>(true, sc,
            $"เปิดรอบนับสต็อก {sc.CountNumber} จำนวน {sc.Lines.Count} รายการ"));
    }

    public sealed record SetLineCountRequest(Guid LineId, decimal CountedQuantity, string? Notes);

    [HttpPut("stock-counts/{stockCountId:guid}/line")]
    public async Task<ActionResult<ApiResponse<StockCount>>> SetLine(
        Guid companyId, Guid stockCountId, [FromBody] SetLineCountRequest req,
        [FromServices] IStockCountService svc, CancellationToken ct)
    {
        try
        {
            var sc = await svc.SetLineCountAsync(companyId, stockCountId,
                req.LineId, req.CountedQuantity, req.Notes, ct);
            return Ok(new ApiResponse<StockCount>(true, sc, "บันทึกจำนวนนับแล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpPost("stock-counts/{stockCountId:guid}/close")]
    public async Task<ActionResult<ApiResponse<StockCount>>> CloseCount(
        Guid companyId, Guid stockCountId,
        [FromServices] IStockCountService svc, CancellationToken ct)
    {
        try
        {
            var sc = await svc.CloseAsync(companyId, stockCountId, ct);
            return Ok(new ApiResponse<StockCount>(true, sc, "ปิดรอบนับสต็อก + อัปเดต CurrentStock แล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("stock-counts")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<StockCount>>>> ListStockCounts(
        Guid companyId, [FromQuery] string? status,
        [FromServices] IStockCountService svc, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<StockCount>>(true, await svc.ListAsync(companyId, status, ct)));

    // ── FX revaluation ──────────────────────────────────────────────
    public sealed record FxRevalRequest(DateTime AsOf, Dictionary<string, decimal> ClosingRates);

    [HttpPost("fx-revaluation/propose")]
    public async Task<ActionResult<ApiResponse<FxRevaluationResult>>> ProposeFxRevaluation(
        Guid companyId, [FromBody] FxRevalRequest req,
        [FromServices] IFxRevaluationService svc, CancellationToken ct)
    {
        var result = await svc.ProposeAsync(companyId, req.AsOf, req.ClosingRates, ct);
        return Ok(new ApiResponse<FxRevaluationResult>(true, result,
            $"กำไรสุทธิจาก FX revaluation: {result.NetEffect:N2}"));
    }

    /// <summary>Post the proposed FX revaluation as a single JE
    /// (Dr/Cr AR/AP control vs FX gain/loss accounts). Idempotent per
    /// period via Reference="FX-REVAL-yyyyMM"; re-running on the same
    /// period returns the existing JE.</summary>
    [HttpPost("fx-revaluation/post")]
    public async Task<ActionResult<ApiResponse<FxRevaluationPostResult>>> PostFxRevaluation(
        Guid companyId, [FromBody] FxRevalRequest req,
        [FromServices] IFxRevaluationService svc, CancellationToken ct)
    {
        var userId = Helpers.JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await svc.PostAsync(companyId, req.AsOf, req.ClosingRates, userId, ct);
        return Ok(new ApiResponse<FxRevaluationPostResult>(true, result, result.Message));
    }
}
