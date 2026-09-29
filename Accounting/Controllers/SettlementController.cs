using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Settlement;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Settlement;
using Accounting.Services.Settlement.Adapters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// **รอบโอนเงินจาก wallet ของ gateway/marketplace → ธนาคาร (settlement)** — ทางเข้า HTTP ของ service ทีม B (นำเข้า · จัดประเภท · จับคู่ ·
/// ตั้งค่าช่องทาง) และทีม C (ลงบัญชี · ยกเลิก · จับคู่ธนาคาร · chargeback) · รอบ 198 เฟส 1 ทีม D · DOCUMENT_FLOW §2.10
///
/// <para>═══ ด่าน ═══ service กรอง <c>CompanyId</c> เองแต่<b>ไม่ตรวจสิทธิ์</b> (สัญญาของทีม B/C) ⇒ ทุก action ที่นี่มี
/// <c>[RequirePermission]</c> จากตารางเดียว <see cref="SettlementPermissionScope"/> (ดู/นำเข้า/ลงบัญชี/ช่องทาง) · งานที่ขยับ GL หรือกำหนดภาษี
/// (ลงบัญชี · ยกเลิกการลงบัญชี · ยกเลิกรอบ · จับคู่ธนาคาร · ปิด chargeback · ตั้งค่าช่องทาง) ห้ามคีย์ API (<c>[RejectApiKey]</c>) ·
/// ไฟล์นี้อยู่ใน WATCHED ของ <c>tools/write_permission_gate_check.py</c> และแถวของ <c>tools/owner_action_wiring_check.py</c></para>
///
/// <para>═══ ข้อผิดพลาด ═══ <see cref="BusinessRuleException"/> ⇒ สถานะตามที่ service กำหนด (เช่น 409 <c>SETTLEMENT-POST-PARTIAL</c> =
/// ลงบัญชีค้างครึ่งทาง กดใหม่ได้) + ข้อความไทย + <c>ruleCode</c> ใน data · ผลที่ service คืน <c>Ok=false</c> (ถูกบล็อกพร้อมแผน/เหตุผล)
/// ⇒ 409 พร้อมตัวผลทั้งก้อน ให้หน้าเว็บแสดงปัญหา + ทางไปต่อ (ไม่ใช่ 200 ที่ว่างเปล่า — F2 ข้อ 7)</para>
///
/// <para>เส้นทางไม่ใช้คำว่า <c>/bank</c> โดยตั้งใจ — <c>SubscriptionMiddleware.RouteFeatureMap</c> จับ "มีคำนี้ใน path" ⇒ endpoint จับคู่เงินเข้า
/// จะถูกผูกกับแพ็กเกจกระทบยอดธนาคารโดยบังเอิญ ขณะที่ endpoint อื่นของรอบโอนเดียวกันไม่ถูก (ใช้ <c>deposit-…</c> แทน)</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/settlement")]
[Authorize]
public class SettlementController : ControllerBase
{
    private readonly ISettlementImportService _import;
    private readonly ISettlementChannelService _channels;
    private readonly ISettlementPostingService _posting;
    private readonly IBankService _bank;
    private readonly IPermissionService _perms;
    private readonly AccountingDbContext _db;

    public SettlementController(ISettlementImportService import, ISettlementChannelService channels, ISettlementPostingService posting,
        IBankService bank, IPermissionService perms, AccountingDbContext db)
    {
        _import = import; _channels = channels; _posting = posting; _bank = bank; _perms = perms; _db = db;
    }

    private static readonly JsonSerializerOptions FormJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private Guid UserId => JwtHelper.GetUserIdFromClaims(User);

    // ═════════════════════════════ ข้อมูลอ้างอิง ═════════════════════════════

    /// <summary>ป้ายไทยของ enum ทุกตัว · บทบาทผังค่าธรรมเนียม · ช่องจับคู่คอลัมน์ · บัญชีธนาคาร/gateway ของบริษัทนี้ — หน้าเว็บไม่มีตารางป้ายเอง ·
    /// ค่าเริ่มต้นของบัญชีธนาคาร (เฉพาะเมื่อมีบัญชีเดียว · D-03) · เพดานขนาดไฟล์ (หน้าเว็บเตือนก่อนอัปโหลด · D-P4) ·
    /// ผู้ใช้คนนี้จำการจับคู่คอลัมน์ได้ไหม (D-P2 — ช่องติ๊กบอกเหตุผลแทนการไม่จำเงียบ ๆ)</summary>
    [HttpGet("reference")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.View)]
    public async Task<ActionResult<ApiResponse<object>>> Reference(Guid companyId, CancellationToken ct)
    {
        var banks = await BankOptionsAsync(companyId, null, ct);
        var gateways = await _db.PaymentProviderConfigs.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .OrderBy(c => c.ProviderCode)
            .Select(c => new { id = c.Id, label = c.DisplayName ?? c.ProviderCode })
            .ToListAsync(ct);
        var memory = SettlementPermissionScope.ColumnMapMemory(true, OwnerActionGuard.IsApiKeyRequest(HttpContext),
            await _perms.HasPermissionAsync(companyId, UserId, SettlementPermissionScope.Channels));
        return Ok(new ApiResponse<object>(true, new
        {
            catalog = SettlementReferenceCatalog.Build(),
            bankAccounts = banks,
            defaultBankAccountId = SettlementBankAccountRule.DefaultChoice(banks.Select(b => b.Id).ToList()),
            gatewayConfigs = gateways,
            maxUploadBytes = SettlementFileReader.MaxFileBytes,
            maxUploadMessage = TooLargeMessage,
            columnMapMemory = new { allowed = memory.Remember, reason = memory.Notice },
        }));
    }

    /// <summary>ตัวเลือกบัญชีธนาคาร 1 รายการ — ป้ายรูปแบบเดียวทั้งฟอร์มนำเข้า หัวรอบโอน และพรีวิว</summary>
    public sealed record BankOption(Guid Id, string Label);

    /// <summary>บัญชีธนาคารที่เปิดใช้ของบริษัท (tenant) · <paramref name="onlyId"/> = เฉพาะบัญชีนั้น (รวมที่ปิดใช้แล้ว — หัวรอบโอนเก่าต้องแสดงชื่อได้)</summary>
    private async Task<List<BankOption>> BankOptionsAsync(Guid companyId, Guid? onlyId, CancellationToken ct)
    {
        var q = _db.BankAccounts.AsNoTracking().Where(b => b.CompanyId == companyId);
        q = onlyId is Guid id ? q.Where(b => b.Id == id) : q.Where(b => b.IsActive);
        return await q.OrderBy(b => b.BankName).ThenBy(b => b.AccountNumber)
            .Select(b => new BankOption(b.Id, b.BankName + " " + b.AccountNumber + " (" + b.AccountName + ")"))
            .ToListAsync(ct);
    }

    private static readonly string TooLargeMessage =
        $"ไฟล์ใหญ่เกิน {SettlementFileReader.MaxFileBytes / (1024 * 1024)} MB — ส่งออกรายงานทีละรอบโอน (ช่วงวันที่สั้นลง) แล้วนำเข้าทีละไฟล์";

    // ═════════════════════════════ ช่องทาง ═════════════════════════════

    [HttpGet("channels")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.View)]
    public Task<ActionResult<ApiResponse<IReadOnlyList<SettlementChannelView>>>> ListChannels(Guid companyId,
        [FromQuery] bool includeInactive = false, CancellationToken ct = default)
        => Guarded(() => _channels.ListAsync(companyId, includeInactive, ct));

    [HttpGet("channels/{channelId:guid}")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.View)]
    public Task<ActionResult<ApiResponse<SettlementChannelView>>> GetChannel(Guid companyId, Guid channelId, CancellationToken ct)
        => Guarded(() => _channels.GetAsync(companyId, channelId, ct));

    /// <summary>สร้างช่องทาง — ผังพัก 1134x ถูกสร้าง/ผูกในธุรกรรมเดียวกัน · ตั้งโหมดภาษีของค่าธรรมเนียม ⇒ ห้ามคีย์ API</summary>
    [HttpPost("channels")]
    [Accounting.Filters.RejectApiKey("ตั้งค่าช่องทางรับเงินผ่าน wallet")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Channels)]
    public Task<ActionResult<ApiResponse<SettlementChannelView>>> CreateChannel(Guid companyId,
        [FromBody] SettlementChannelUpsertRequest request, CancellationToken ct)
        => Guarded(() => _channels.CreateAsync(companyId, UserId, request, ct), "บันทึกช่องทางแล้ว");

    /// <summary>แก้ช่องทาง — มีรอบโอนแล้ว ⇒ ชนิด/gateway/ผังพักแก้ไม่ได้ (service ตีกลับพร้อมเหตุผล · หน้าเว็บล็อกช่องตาม <c>HasBatches</c>)</summary>
    [HttpPut("channels/{channelId:guid}")]
    [Accounting.Filters.RejectApiKey("ตั้งค่าช่องทางรับเงินผ่าน wallet")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Channels)]
    public Task<ActionResult<ApiResponse<SettlementChannelView>>> UpdateChannel(Guid companyId, Guid channelId,
        [FromBody] SettlementChannelUpsertRequest request, CancellationToken ct)
        => Guarded(() => _channels.UpdateAsync(companyId, UserId, channelId, request, ct), "บันทึกช่องทางแล้ว");

    // ═════════════════════════════ นำเข้า ═════════════════════════════

    /// <summary>ตรวจไฟล์ก่อนนำเข้า (ไม่บันทึกอะไร) — หัวคอลัมน์ · แถวตัวอย่างที่ตัด PII แล้ว · การจับคู่ที่จำไว้/ที่เสนอ</summary>
    [HttpPost("files/inspect")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Import)]
    public async Task<ActionResult<ApiResponse<SettlementFileInspection>>> InspectFile(Guid companyId,
        [FromForm] Guid channelId, IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<SettlementFileInspection>(false, null, "เลือกไฟล์รายงานรอบโอน (CSV หรือ Excel) ก่อน"));
        if (file.Length > SettlementFileReader.MaxFileBytes)
            return BadRequest(new ApiResponse<SettlementFileInspection>(false, null, TooLargeMessage));
        await using var stream = file.OpenReadStream();
        return await Guarded(() => _import.InspectFileAsync(companyId, channelId, file.FileName, stream, ct));
    }

    /// <summary>นำเข้าไฟล์เป็นรอบโอน — multipart: <c>file</c> + <c>payload</c> (JSON ของ <see cref="SettlementFileImportRequest"/>)</summary>
    [HttpPost("files/import")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Import)]
    public async Task<ActionResult<ApiResponse<SettlementImportResult>>> ImportFile(Guid companyId,
        [FromForm] string? payload, IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<SettlementImportResult>(false, null, "เลือกไฟล์รายงานรอบโอน (CSV หรือ Excel) ก่อน"));
        if (file.Length > SettlementFileReader.MaxFileBytes)
            return BadRequest(new ApiResponse<SettlementImportResult>(false, null, TooLargeMessage));
        SettlementFileImportRequest? request;
        try
        {
            request = string.IsNullOrWhiteSpace(payload) ? null : JsonSerializer.Deserialize<SettlementFileImportRequest>(payload, FormJson);
        }
        catch (JsonException)
        {
            request = null;
        }
        // D-07: "header": null ⇒ STJ ไม่บังคับ non-nullable ⇒ เดิม NullReferenceException 500
        if (request is null || !HeaderPresent(request.Header))
            return BadRequest(new ApiResponse<SettlementImportResult>(false, null, HeaderMissingMessage));
        // D-P2: จำการจับคู่คอลัมน์ = เปลี่ยนค่าตั้งของช่องทาง ⇒ ด่านเดียวกับ PUT channels (สิทธิ์ Channels + ห้ามคีย์ API) · ไม่ผ่าน = ใช้กับไฟล์นี้อย่างเดียว + บอก
        var memory = SettlementPermissionScope.ColumnMapMemory(request.RememberColumnMap, OwnerActionGuard.IsApiKeyRequest(HttpContext),
            await _perms.HasPermissionAsync(companyId, UserId, SettlementPermissionScope.Channels));
        var effective = request with { RememberColumnMap = memory.Remember };
        await using var stream = file.OpenReadStream();
        return await Guarded(async () =>
        {
            var r = await _import.ImportFileAsync(companyId, UserId, effective, file.FileName, stream, ct);
            return memory.Notice is string notice ? r with { Warnings = r.Warnings.Append(notice).ToList() } : r;
        }, "นำเข้ารอบโอนแล้ว");
    }

    private const string HeaderMissingMessage = "ข้อมูลหัวรอบโอนไม่ครบ — เลือกช่องทาง แล้วกรอกวันที่เงินเข้าและยอดโอนเข้าธนาคารจริง";

    /// <summary>หัวรอบโอนมีจริงและเลือกช่องทางแล้ว — <c>"header": null</c> ใน JSON ผ่าน model binding ได้ (STJ ไม่บังคับ non-nullable · D-07)</summary>
    private static bool HeaderPresent(SettlementBatchHeaderRequest? h) => h is not null && h.ChannelId != Guid.Empty;

    /// <summary>ประกอบรอบโอนจากรายการรับชำระออนไลน์ในระบบ (ช่องทาง Gateway ที่ผูกการตั้งค่า gateway)</summary>
    [HttpPost("batches/from-payment-intents")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Import)]
    public async Task<ActionResult<ApiResponse<SettlementImportResult>>> ImportFromIntents(Guid companyId,
        [FromBody] SettlementIntentBatchRequest? request, CancellationToken ct)
    {
        if (request is null || !HeaderPresent(request.Header))
            return BadRequest(new ApiResponse<SettlementImportResult>(false, null, HeaderMissingMessage));
        return await Guarded(() => _import.ImportFromPaymentIntentsAsync(companyId, UserId, request, ct), "ประกอบรอบโอนแล้ว");
    }

    // ═════════════════════════════ รอบโอน ═════════════════════════════

    /// <summary>หน้าหนึ่งของรายการรอบโอน — <c>HasMore</c> = มีรอบเก่ากว่านี้อีก (หน้าเว็บแสดงปุ่ม "โหลดเพิ่ม" · D-11)</summary>
    public sealed record SettlementBatchPage(IReadOnlyList<SettlementBatchView> Items, int Skip, int Take, bool HasMore);

    /// <summary>จำนวนรอบต่อหน้าสูงสุด (service clamp 200 — ต้องต่ำกว่าเพื่อขอเกิน 1 แถวไว้ดูว่ายังมีหน้าถัดไปไหม)</summary>
    private const int MaxPageSize = 100;

    [HttpGet("batches")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.View)]
    public async Task<ActionResult<ApiResponse<SettlementBatchPage>>> ListBatches(Guid companyId,
        [FromQuery] Guid? channelId, [FromQuery] SettlementBatchStatus? status,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        // D-05: รอบที่ยกเลิกแล้วถูก soft-delete — กรองด้วยสถานะนี้ได้รายการว่างเสมอ ⇒ บอกเหตุผล (ไม่ใช่ 200 กับของว่าง)
        if (status is SettlementBatchStatus st && !SettlementReferenceCatalog.IsListable(st))
            return BadRequest(new ApiResponse<SettlementBatchPage>(false, null, SettlementReferenceCatalog.VoidedNotListedMessage));
        var size = Math.Clamp(take, 1, MaxPageSize);
        var from = Math.Max(0, skip);
        return await Guarded(async () =>
        {
            var rows = await _import.ListBatchesAsync(companyId, channelId, status, from, size + 1, ct);
            return new SettlementBatchPage(rows.Take(size).ToList(), from, size, rows.Count > size);
        });
    }

    /// <summary>รอบโอน + บรรทัด + ผู้สมัครจับคู่ + ปุ่มที่กดได้ (<see cref="SettlementBatchActions"/>) + ผลการลงบัญชีที่ผูกไว้</summary>
    [HttpGet("batches/{batchId:guid}")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.View)]
    public Task<ActionResult<ApiResponse<object>>> GetBatch(Guid companyId, Guid batchId, CancellationToken ct)
        => Guarded(() => BatchDetailAsync(companyId, batchId, ct));

    private async Task<object> BatchDetailAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var batch = await _import.GetBatchAsync(companyId, batchId, ct);
        var head = await _db.SettlementBatches.AsNoTracking()
            .Where(b => b.Id == batchId && b.CompanyId == companyId)
            .Select(b => new { b.PayoutJournalEntryId, b.BankTransactionId, b.PostedAt })
            .FirstOrDefaultAsync(ct);
        string? jeNumber = null;
        if (head?.PayoutJournalEntryId is Guid jeId)
            jeNumber = await _db.JournalEntries.AsNoTracking()
                .Where(j => j.Id == jeId && j.CompanyId == companyId).Select(j => j.EntryNumber).FirstOrDefaultAsync(ct);
        // D-04: chargeback ที่ปิดแล้ว — นิยามเดียวกับด่านกันลงซ้ำของ service · D-06: สิทธิ์ของผู้ใช้ด้วยคีย์เดียวกับ [RequirePermission] ของ endpoint
        var chargebackIds = batch.Lines.Where(l => l.LineType == SettlementLineType.Chargeback).Select(l => l.Id).ToList();
        var closedChargebacks = await _posting.ClosedChargebacksAsync(companyId, chargebackIds, ct);
        var permissions = new SettlementActionPermissions(
            await _perms.HasPermissionAsync(companyId, UserId, SettlementPermissionScope.Import),
            await _perms.HasPermissionAsync(companyId, UserId, SettlementPermissionScope.Post),
            await _perms.HasPermissionAsync(companyId, UserId, PermissionKeys.JournalManage));
        // D-03: บัญชีธนาคารที่ผูกกับรอบนี้ (รวมบัญชีที่ปิดใช้ภายหลัง — ต้องเห็นว่าผูกอะไรไว้)
        var bank = batch.BankAccountId is Guid bankId ? (await BankOptionsAsync(companyId, bankId, ct)).FirstOrDefault() : null;
        // D-P5 (รอบ 200 ข้อ 14): ผู้มีแค่ Settlement.View ไม่เห็นผู้สมัครเอกสารขาย/ยอดค้าง — ตัดสินจากสิทธิ์ชุดเดียวกับปุ่ม (permissions) · ซ่อน = บอกเหตุผล
        var candidatesHidden = SettlementPermissionScope.CandidatesHiddenReason(permissions.Import, permissions.Post);
        if (candidatesHidden is string hiddenReason)
            batch = SettlementPermissionScope.HideCandidates(batch, hiddenReason);
        return new
        {
            batch,
            candidatesHiddenReason = candidatesHidden,
            // ตัวตัดสินเดียวกับด่านของ service (ทีม S3): ลงค้างครึ่งทาง = ป้ายชุดเดียวกับ LoadEditableBatchAsync · ยกเลิกการลงบัญชี = SettlementUnpostGate
            actions = SettlementBatchActions.For(batch.Status, batch.Lines.Select(l => (l.Id, l.LineType)), batch.PostingArtifacts,
                await _posting.UnpostBlockersAsync(companyId, batchId, ct), closedChargebacks, permissions),
            bankAccount = new
            {
                id = batch.BankAccountId,
                label = bank?.Label,
                missingReason = SettlementBankAccountRule.MissingForImport(batch.NetPayout, batch.BankAccountId),
            },
            posting = new
            {
                payoutJournalEntryId = head?.PayoutJournalEntryId,
                payoutJournalEntryNumber = jeNumber,
                bankTransactionId = head?.BankTransactionId,
                postedAt = head?.PostedAt,
            },
        };
    }

    public sealed record ReasonRequest(string? Reason);

    /// <summary>ยกเลิกรอบโอนที่ยังไม่ลงบัญชี (soft-delete · นำเข้าไฟล์เดิมใหม่ได้) — บังคับเหตุผล · audit</summary>
    [HttpPost("batches/{batchId:guid}/void")]
    [Accounting.Filters.RejectApiKey("ยกเลิกรอบโอน")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Import)]
    public async Task<ActionResult<ApiResponse<object>>> VoidBatch(Guid companyId, Guid batchId, [FromBody] ReasonRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new ApiResponse<object>(false, null, "ระบุเหตุผลที่ยกเลิกรอบโอน (ผู้สอบบัญชีต้องเห็นว่ายกเลิกเพราะอะไร)"));
        var reason = request.Reason.Trim();
        return await Guarded<object>(async () =>
        {
            await _import.VoidBatchAsync(companyId, UserId, batchId, reason, ct);
            return new { batchId };
        }, "ยกเลิกรอบโอนแล้ว");
    }

    /// <summary>จับคู่ใบขายใหม่ทั้งรอบ (หลังสร้างเอกสารขายที่ขาด) — ไม่แตะบรรทัดที่ผู้ใช้ตัดสินเองแล้ว</summary>
    [HttpPost("batches/{batchId:guid}/rematch")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Import)]
    public Task<ActionResult<ApiResponse<SettlementBatchView>>> Rematch(Guid companyId, Guid batchId, CancellationToken ct)
        => Guarded(() => _import.RematchBatchAsync(companyId, batchId, ct), "จับคู่ใหม่แล้ว");

    /// <summary>เปลี่ยนบัญชีธนาคารที่รับเงินของรอบที่ยังแก้ได้ (D-03) — ด่านเดียวกับแก้บรรทัด (ลงบัญชีแล้ว/ค้างครึ่งทาง ⇒ 409 พร้อมทางไปต่อ)</summary>
    [HttpPut("batches/{batchId:guid}/bank-account")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Import)]
    public Task<ActionResult<ApiResponse<SettlementBatchView>>> SetBankAccount(Guid companyId, Guid batchId,
        [FromBody] SettlementBatchBankAccountRequest? request, CancellationToken ct)
        => Guarded(() => _import.SetBankAccountAsync(companyId, UserId, batchId, request?.BankAccountId, ct), "บันทึกบัญชีธนาคารที่รับเงินแล้ว");

    // ═════════════════════════════ บรรทัด ═════════════════════════════

    /// <summary>ผู้ใช้เลือก/แก้ประเภทบรรทัด — ปิดลูปการเรียนรู้ (service บันทึก <c>RecordUserChoiceAsync(…, Explicit)</c> ในธุรกรรมเดียวกัน) ·
    /// ห้ามคีย์ API (D-P1): คำตอบของ integration ที่จัดประเภทเป็นชุดจะถูกนับเป็น "ผู้ใช้เลือก" ชั้นสูงสุดของคลังเรียนรู้ (DOCTRINE §3 กันคลังเอียง)</summary>
    [HttpPost("lines/{lineId:guid}/reclassify")]
    [Accounting.Filters.RejectApiKey("จัดประเภทบรรทัดรอบโอน (ระบบบันทึกเป็นคำตอบของผู้ใช้ในคลังเรียนรู้)")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Import)]
    public Task<ActionResult<ApiResponse<SettlementLineView>>> Reclassify(Guid companyId, Guid lineId,
        [FromBody] SettlementReclassifyRequest request, CancellationToken ct)
        => Guarded(() => _import.ReclassifyLineAsync(companyId, UserId, lineId, request, ct), "บันทึกประเภทแล้ว");

    /// <summary>ผู้ใช้ตัดสินการจับคู่ของบรรทัดขาย/คืนเงิน (เลือกเอกสาร หรือยืนยันเข้าใบขายสรุปรายวัน)</summary>
    [HttpPost("lines/{lineId:guid}/match")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Import)]
    public Task<ActionResult<ApiResponse<SettlementLineView>>> AssignMatch(Guid companyId, Guid lineId,
        [FromBody] SettlementAssignMatchRequest request, CancellationToken ct)
        => Guarded(() => _import.AssignLineMatchAsync(companyId, lineId, request, ct), "บันทึกการจับคู่แล้ว");

    /// <param name="Won">ต้องระบุเสมอ (D-08) — เดิม <c>bool</c> ⇒ body ที่ไม่มี <c>won</c> = แพ้ = ลง JE ขาดทุนเงียบ ๆ</param>
    public sealed record ChargebackResolveRequest(bool? Won);

    /// <summary>ปิดรายการ chargeback ที่พักไว้ (แพ้ = ขาดทุน · ชนะและได้เงินคืนนอกไฟล์ = กลับเข้าผังพัก) — ลง JE ⇒ ห้ามคีย์ API</summary>
    [HttpPost("lines/{lineId:guid}/chargeback-resolve")]
    [Accounting.Filters.RejectApiKey("ปิดรายการ chargeback")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Post)]
    public async Task<ActionResult<ApiResponse<SettlementChargebackResult>>> ResolveChargeback(Guid companyId, Guid lineId,
        [FromBody] ChargebackResolveRequest request, CancellationToken ct)
    {
        if (request?.Won is not bool won)
            return BadRequest(new ApiResponse<SettlementChargebackResult>(false, null,
                "ระบุผลของ chargeback (won = true: ชนะและได้เงินคืน · false: แพ้ ตัดเป็นขาดทุน) — ระบบไม่ถือว่าแพ้ให้เอง"));
        try
        {
            var r = await _posting.ResolveChargebackAsync(companyId, lineId, won, UserId, ct);
            return r.Ok
                ? Ok(new ApiResponse<SettlementChargebackResult>(true, r, r.Message))
                : Conflict(new ApiResponse<SettlementChargebackResult>(false, r, r.Message));
        }
        catch (BusinessRuleException ex) { return Fail(ex); }
        catch (KeyNotFoundException ex) { return NotFoundMessage(ex); }
    }

    // ═════════════════════════════ ลงบัญชี ═════════════════════════════

    /// <summary>พรีวิวการลงบัญชี (ไม่เขียนอะไร) — แผน · ปัญหาพร้อมทางไปต่อ · <c>CanPost</c> · ของที่ลงไว้แล้ว (ค้างครึ่งทาง/ลงแล้ว)</summary>
    [HttpGet("batches/{batchId:guid}/posting-preview")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.View)]
    public Task<ActionResult<ApiResponse<SettlementPostingPreview>>> PostingPreview(Guid companyId, Guid batchId, CancellationToken ct)
    {
        return Guarded(async () =>
        {
            var preview = await _posting.PreviewAsync(companyId, batchId, UserId, ct);
            // S200-5 (ต่อจาก D-P5): ผู้มีแค่ Settlement.View ไม่เห็นเลขที่/ยอดค้างของใบขายผ่านข้อความปัญหา — ตัวตัดสินสิทธิ์ตัวเดียวกับหน้ารอบโอน
            var hidden = SettlementPermissionScope.CandidatesHiddenReason(
                await _perms.HasPermissionAsync(companyId, UserId, SettlementPermissionScope.Import),
                await _perms.HasPermissionAsync(companyId, UserId, SettlementPermissionScope.Post));
            return hidden is string reason
                ? preview with { Plan = SettlementPermissionScope.HideReceivableDetails(preview.Plan, reason) }
                : preview;
        });
    }

    /// <summary>ลงบัญชีรอบโอน — ถูกบล็อก ⇒ 409 พร้อมแผน (<c>Plan.Issues</c> มี <c>NextStep</c>) · ค้างครึ่งทาง ⇒ 409 <c>SETTLEMENT-POST-PARTIAL</c></summary>
    [HttpPost("batches/{batchId:guid}/post")]
    [Accounting.Filters.RejectApiKey("ลงบัญชีรอบโอน")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Post)]
    public async Task<ActionResult<ApiResponse<SettlementPostingResult>>> Post(Guid companyId, Guid batchId, CancellationToken ct)
    {
        try
        {
            var r = await _posting.PostAsync(companyId, batchId, UserId, ct);
            return r.Ok
                ? Ok(new ApiResponse<SettlementPostingResult>(true, r, r.Message))
                : Conflict(new ApiResponse<SettlementPostingResult>(false, r, r.Message));
        }
        catch (BusinessRuleException ex) { return Fail(ex); }
        catch (KeyNotFoundException ex) { return NotFoundMessage(ex); }
    }

    /// <summary>ยกเลิกการลงบัญชี (ถอนจับคู่ธนาคาร → ยกเลิกเอกสาร/การรับชำระ/50 ทวิ → กลับรายการ JE) — บังคับเหตุผล</summary>
    [HttpPost("batches/{batchId:guid}/unpost")]
    [Accounting.Filters.RejectApiKey("ยกเลิกการลงบัญชีรอบโอน")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Post)]
    public async Task<ActionResult<ApiResponse<SettlementUnpostResult>>> Unpost(Guid companyId, Guid batchId,
        [FromBody] ReasonRequest request, CancellationToken ct)
    {
        try
        {
            var r = await _posting.UnpostAsync(companyId, batchId, UserId, request.Reason ?? string.Empty, ct);
            return r.Ok
                ? Ok(new ApiResponse<SettlementUnpostResult>(true, r, r.Message))
                : Conflict(new ApiResponse<SettlementUnpostResult>(false, r, r.Message));
        }
        catch (BusinessRuleException ex) { return Fail(ex); }
        catch (KeyNotFoundException ex) { return NotFoundMessage(ex); }
    }

    /// <param name="ArtifactId">id ของเอกสาร (<c>IsPayment=false</c>) หรือการรับชำระ (<c>IsPayment=true</c>) ที่เป็นของกำพร้า</param>
    /// <param name="BatchId">รอบโอนที่ผู้ใช้กำลังดูพรีวิวและตรวจเทียบ (ฝ่ายค้านรอบ 200 V2-P1 — เก็บลง audit · การรับรู้เดิมที่ไม่ครอบรอบนี้รับรู้ใหม่ได้) · null = ไม่ระบุ</param>
    public sealed record OrphanAckRequest(Guid ArtifactId, bool IsPayment, string? Reason, Guid? BatchId = null);

    /// <summary>**รับรู้ของกำพร้า** (รอบ 200 · DECISIONS ข้อ 10) — เฉพาะชิ้นที่ยกเลิกไม่ได้จริงของรอบโอนที่ถูกยกเลิกแล้ว · บังคับเหตุผล · ประทับผู้/เวลา/เหตุผล
    /// บนแถว + audit chain ⇒ ไม่บล็อกการลงบัญชีของช่องทางนั้นอีก · กำหนดว่ารอบโอนถัดไปลงบัญชีได้ (ขยับ GL) ⇒ ห้ามคีย์ API · ปฏิเสธ = 409 พร้อมทางไปต่อ</summary>
    [HttpPost("orphans/acknowledge")]
    [Accounting.Filters.RejectApiKey("รับรู้ของกำพร้าของรอบโอน")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Post)]
    public async Task<ActionResult<ApiResponse<SettlementOrphanAckResult>>> AcknowledgeOrphan(Guid companyId,
        [FromBody] OrphanAckRequest? request, CancellationToken ct)
    {
        if (request is null || request.ArtifactId == Guid.Empty)
            return BadRequest(new ApiResponse<SettlementOrphanAckResult>(false, null, "ระบุรายการของกำพร้าที่จะรับรู้ (เอกสารหรือการรับชำระ)"));
        try
        {
            var r = await _posting.AcknowledgeOrphanAsync(companyId, request.ArtifactId, request.IsPayment, UserId, request.Reason,
                request.BatchId, ct);
            return r.Ok
                ? Ok(new ApiResponse<SettlementOrphanAckResult>(true, r, r.Message))
                : Conflict(new ApiResponse<SettlementOrphanAckResult>(false, r, r.Message));
        }
        catch (BusinessRuleException ex) { return Fail(ex); }
        catch (KeyNotFoundException ex) { return NotFoundMessage(ex); }
    }

    // ═════════════════════════════ จับคู่เงินเข้าธนาคาร ═════════════════════════════

    /// <summary>ผู้สมัครรายการเดินบัญชี — รายการที่ยังไม่กระทบยอดของบัญชีที่รอบโอนระบุ (<c>IBankService.GetUnreconciledAsync</c> · tenant) ·
    /// ตัดสินทีละแถวด้วย <see cref="SettlementBankMatch.Check"/> ตัวเดียวกับตอนกดจับคู่ · คีย์ = <c>Settlement.Post</c> (ไม่ใช่ View):
    /// รายการเดินบัญชีธนาคารไม่ควรเปิดให้คนที่มีแค่สิทธิ์ดูรอบโอน — เห็นได้เฉพาะคนที่มีสิทธิ์จับคู่</summary>
    [HttpGet("batches/{batchId:guid}/deposit-candidates")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Post)]
    public Task<ActionResult<ApiResponse<object>>> DepositCandidates(Guid companyId, Guid batchId, CancellationToken ct)
        => Guarded(() => DepositCandidatesAsync(companyId, batchId, ct));

    private async Task<object> DepositCandidatesAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var b = await _db.SettlementBatches.AsNoTracking()
            .Where(x => x.Id == batchId && x.CompanyId == companyId)
            .Select(x => new SettlementBankBatchFacts(x.Id, x.Status, x.NetPayout, x.PayoutDate, x.BankAccountId,
                x.PayoutJournalEntryId, x.BankTransactionId))
            .FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("ไม่พบรอบโอนนี้ในบริษัท", "SETTLEMENT-BATCH", 404);
        if (b.BankAccountId is not Guid bankAccountId)
            return new
            {
                candidates = Array.Empty<SettlementBankCandidate>(),
                message = "รอบโอนนี้ยังไม่ได้ระบุบัญชีธนาคารที่รับเงิน — ยกเลิกรอบแล้วนำเข้าใหม่พร้อมเลือกบัญชีธนาคาร",
            };
        var txns = await _bank.GetUnreconciledAsync(companyId, bankAccountId);
        var ids = txns.Select(t => t.Id).ToList();
        var linked = await _db.SettlementBatches.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.Id != batchId && x.BankTransactionId != null && ids.Contains(x.BankTransactionId.Value))
            .Select(x => new { x.Id, TxnId = x.BankTransactionId!.Value })
            .ToListAsync(ct);
        var linkedBy = linked.GroupBy(x => x.TxnId).ToDictionary(g => g.Key, g => g.First().Id);
        var rows = txns.Select(t => new SettlementBankTxnRow(t.Id, t.BankAccountId, t.TransactionDate, t.TransactionType, t.Amount,
            t.ReconciliationStatus, t.MatchedJournalEntryId, linkedBy.TryGetValue(t.Id, out var other) ? other : null,
            t.Description, t.Reference));
        var candidates = SettlementBankCandidates.Evaluate(b, rows);
        return new
        {
            candidates,
            message = candidates.Count == 0
                ? $"ไม่พบรายการเดินบัญชีที่ยังไม่กระทบยอดในช่วง ±{SettlementBankCandidates.WindowDays} วันจากวันเงินเข้า — นำเข้าสเตทเมนต์ธนาคารก่อน"
                : null,
        };
    }

    public sealed record DepositMatchRequest(Guid BankTransactionId);

    /// <summary>จับคู่ JE รอบโอนกับรายการเดินบัญชีจริง (สถานะ BankMatched) — กระทบยอดธนาคาร ⇒ ห้ามคีย์ API</summary>
    [HttpPost("batches/{batchId:guid}/deposit-match")]
    [Accounting.Filters.RejectApiKey("จับคู่เงินเข้าธนาคารของรอบโอน")]
    [Accounting.Filters.RequirePermission(SettlementPermissionScope.Post)]
    public async Task<ActionResult<ApiResponse<SettlementBankMatchResult>>> MatchDeposit(Guid companyId, Guid batchId,
        [FromBody] DepositMatchRequest request, CancellationToken ct)
    {
        try
        {
            var r = await _posting.MatchBankTransactionAsync(companyId, batchId, request.BankTransactionId, UserId, ct);
            return r.Ok
                ? Ok(new ApiResponse<SettlementBankMatchResult>(true, r, r.Message))
                : Conflict(new ApiResponse<SettlementBankMatchResult>(false, r, r.Message));
        }
        catch (BusinessRuleException ex) { return Fail(ex); }
        catch (KeyNotFoundException ex) { return NotFoundMessage(ex); }
    }

    // ═════════════════════════════ ตัวช่วย ═════════════════════════════

    /// <summary>เรียก service แล้วห่อผล — ข้อผิดพลาดของผู้ใช้เป็นข้อความไทยพร้อม <c>ruleCode</c> (หน้าเว็บใช้แยก "ค้างครึ่งทาง" ออกจาก "ถูกบล็อก")</summary>
    private async Task<ActionResult<ApiResponse<T>>> Guarded<T>(Func<Task<T>> work, string? okMessage = null)
    {
        try
        {
            return Ok(new ApiResponse<T>(true, await work(), okMessage));
        }
        catch (BusinessRuleException ex) { return Fail(ex); }
        catch (KeyNotFoundException ex) { return NotFoundMessage(ex); }
    }

    private ObjectResult Fail(BusinessRuleException ex)
        => StatusCode(ex.StatusCode, new ApiResponse<object>(false, new { ruleCode = ex.RuleCode }, ex.Message));

    /// <summary>D-09: <c>KeyNotFoundException</c> ที่ service โยนพร้อมข้อความไทย ("ไม่พบรอบโอน" · "ไม่พบบรรทัดของรอบโอน") = 404 ตามข้อความนั้น ·
    /// ไม่มีข้อความไทย = บั๊กภายใน (เช่น lookup ใน dictionary) ⇒ ห้ามแปลงเป็น "ไม่พบรายการ" (ระบุสาเหตุผิด · F2 ข้อ 7) — โยนต่อเป็นข้อผิดพลาดภายใน
    /// ให้ <c>ExceptionMiddleware</c> ตอบ 500 พร้อมรหัสอ้างอิง (บันทึก Error Logs)</summary>
    private ObjectResult NotFoundMessage(KeyNotFoundException ex)
        => ex.Message.Any(ch => ch is >= '฀' and <= '๿')
            ? StatusCode(404, new ApiResponse<object>(false, null, ex.Message))
            : throw new SettlementInternalLookupException(ex);

    /// <summary>lookup ภายในล้ม (ไม่ใช่ "ผู้ใช้ขอของที่ไม่มี") — ExceptionMiddleware แปลงเป็น 500 + รหัสอ้างอิง</summary>
    private sealed class SettlementInternalLookupException : Exception
    {
        public SettlementInternalLookupException(KeyNotFoundException inner)
            : base("settlement: KeyNotFoundException ที่ไม่มีข้อความถึงผู้ใช้ — บั๊กภายใน ไม่ใช่รายการที่ไม่มีอยู่", inner) { }
    }
}
