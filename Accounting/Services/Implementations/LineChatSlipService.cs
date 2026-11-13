using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Line;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>รอบ 260 — สลิปที่ลูกค้าส่งในแชต LINE OA ของบริษัท (คำตัดสิน 260-1-1)</summary>
public interface ILineChatSlipService
{
    /// <summary>รูปในแชต (webhook ที่ลายเซ็นผ่าน) ⇒ message ที่ตอบกลับ · null = ไม่ใช่งานของเส้นนี้ (ปิดสวิตช์ · กลุ่มที่ไม่ได้ผูก/ไม่แสดงยอด · ไม่ใช่รูป ·
    /// เกินเพดานแถว "ยังไม่ผูก" ต่อวัน) — ผู้เรียกใช้คำตอบเดิม</summary>
    Task<IReadOnlyList<object>?> HandleImageAsync(Guid companyId, string? destination, LineChatSource source, string messageId, CancellationToken ct = default);

    /// <summary>ฝ่ายค้าน L3: กลุ่มนี้เป็นงานของเส้นสลิปไหม (สวิตช์เปิด + ผูกกับผู้ติดต่อ + แสดงยอด) — อ่านอย่างเดียว · ผู้เรียกตรวจก่อนกินเพดาน</summary>
    Task<bool> IsSlipGroupAsync(Guid companyId, string? destination, string groupId, CancellationToken ct = default);

    /// <summary>ปุ่มเลือกใบ (postback) ⇒ message ที่ตอบกลับ · null = postback ไม่ใช่ของเส้นนี้</summary>
    Task<IReadOnlyList<object>?> HandlePostbackAsync(Guid companyId, string? destination, LineChatSource source, string? data, CancellationToken ct = default);

    Task<LineChatSlipListResponse> ListOpenAsync(Guid companyId, CancellationToken ct = default);
    Task<int> OpenCountAsync(Guid companyId, CancellationToken ct = default);
    Task<LineChatSlipAssignResult> AssignAsync(Guid companyId, Guid slipId, AssignLineChatSlipRequest request, string actor, CancellationToken ct = default);
    Task DismissAsync(Guid companyId, Guid slipId, string? reason, string actor, CancellationToken ct = default);
    Task<(byte[] Bytes, string ContentType)?> ReadFileAsync(Guid companyId, Guid slipId, CancellationToken ct = default);

    /// <summary>รอบ 268 (268-1-8): ผู้เรียกเดียว = คิวเบื้องหลัง <c>DeferredNotifyDispatchJob</c> (เลื่อนพ้นชั่วโมงเงียบ / ส่งซ้ำครั้งเดียว) · คืน null = ส่งแล้ว · ข้อความ = ปิดแถวพร้อมเหตุ</summary>
    Task<string?> DispatchLinkedNoticeAsync(Guid companyId, Guid slipId, CancellationToken ct = default);
}

/// <summary>
/// **ผู้เขียน <see cref="LineChatSlip"/> ตัวเดียว** — รูปในแชต ⇒ อ่านยอด (ในเครื่อง · ไม่มี AI) ⇒ ตัวตัดสินเดียว <see cref="LineChatSlipRule.Decide"/> ⇒
/// ใบเดียว + ดูเป็นสลิป: แจ้งโอนผ่านประตูเดิม <see cref="SlipEvidenceService.SubmitAsync"/> (รายการรอยืนยันในคิวเดียว · ระบบไม่ปิดหนี้เอง) ·
/// หลายใบ/ไม่แน่ใจ: ปุ่มเลือก (postback ลงลายเซ็น + ตรวจซ้ำกับฐาน) · ไม่ตรง/อ่านไม่ได้/ไม่ได้ผูก/ref ซ้ำ: คิวพนักงาน (หน้ารายการรับชำระออนไลน์) พร้อมเหตุ
/// <para>ฝ่ายค้าน 260-1: ref จาก OCR ไม่ถูกประทับลงรายการรอยืนยัน (M1) · ล้มกลางทางทุกชนิด ⇒ แถวกลับคิวพนักงาน (M2) · สำเนารูปลบเมื่อผูก/ปิด + งานเบื้องหลังลบรูปที่ค้าง (M4)</para>
/// </summary>
public sealed class LineChatSlipService : ILineChatSlipService
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ICustomerLineLinkService _links;
    private readonly ISlipImageReader _reader;
    private readonly IDocumentPayLinkService _payLinks;
    private readonly SlipEvidenceService _slips;
    private readonly Accounting.Services.Storage.IFileStorage _storage;
    private readonly ILogger<LineChatSlipService> _logger;
    private readonly INotificationEngine? _notify;
    private readonly ILineNotifyService? _line;

    public LineChatSlipService(AccountingDbContext db, ISecretProtector secrets, ICustomerLineLinkService links, ISlipImageReader reader,
        IDocumentPayLinkService payLinks, SlipEvidenceService slips, Accounting.Services.Storage.IFileStorage storage,
        ILogger<LineChatSlipService> logger, INotificationEngine? notify = null, ILineNotifyService? line = null)
    {
        _db = db; _secrets = secrets; _links = links; _reader = reader; _payLinks = payLinks; _slips = slips; _storage = storage; _logger = logger; _notify = notify;
        _line = line;
    }

    public const string ActorAuto = "line-chat:auto";
    public const string ActorCustomer = "line-chat:customer";
    public const string ActorRetention = "system:line-chat-retention";

    private sealed record CandidateRef(Guid d, decimal a);

    private static object Text(string text, IReadOnlyList<object>? quickItems = null)
        => quickItems is { Count: > 0 }
            ? new { type = "text", text, quickReply = new { items = quickItems } }
            : new { type = "text", text };

    private static IReadOnlyList<object> One(string text) => new[] { Text(text) };

    private static string? Dest(string? destination) => string.IsNullOrWhiteSpace(destination) ? null : destination.Trim();

    private static string SourceKey(string? destination, LineChatSource source) => NotifyEndpointKind.LineIdHash(Dest(destination), source.Id.Trim());

    /// <summary>ผู้ติดต่อที่ผูกแหล่งแชตนี้ (ยังไม่ยกเลิก · ผู้ติดต่อยังอยู่) + แสดงยอดได้ไหม (ทุกปลายทางต้องเปิด)</summary>
    private async Task<(List<Guid> ContactIds, bool ShowAmounts, bool AnyEndpoint)> BindingAsync(Guid companyId, string sourceKey, LineChatSource source, CancellationToken ct)
    {
        var kind = source.IsGroup ? NotifyEndpointKind.LineGroup : NotifyEndpointKind.LineUser;
        var eps = await _db.ContactNotifyEndpoints.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.Kind == kind && e.LineIdHash == sourceKey && e.UnlinkedAt == null)
            .Select(e => new { e.ContactId, e.ShowAmounts }).ToListAsync(ct);
        var ids = eps.Select(e => e.ContactId).Distinct().ToList();
        var live = ids.Count == 0 ? new List<Guid>()
            : await _db.Contacts.AsNoTracking().Where(c => c.CompanyId == companyId && ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        return (live, eps.Count > 0 && eps.All(e => e.ShowAmounts), eps.Count > 0);
    }

    private async Task<(bool Enabled, byte[]? Key)> SettingsAsync(Guid companyId, CancellationToken ct)
    {
        var s = await _db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new { x.LineEnabled, x.LineChatSlipIntakeEnabled, x.LineChannelSecret }).FirstOrDefaultAsync(ct);
        if (s == null) return (false, null);
        var secret = _secrets.Unprotect(s.LineChannelSecret);
        var key = string.IsNullOrEmpty(secret) ? null : LineChatSlipRule.KeyFrom(secret);
        return (s.LineEnabled && s.LineChatSlipIntakeEnabled && key != null, key);
    }

    public async Task<bool> IsSlipGroupAsync(Guid companyId, string? destination, string groupId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return false;
        var (enabled, key) = await SettingsAsync(companyId, ct);
        if (!enabled || key == null) return false;
        var source = LineChatSource.Group(groupId);
        var (contactIds, showAmounts, anyEndpoint) = await BindingAsync(companyId, SourceKey(destination, source), source, ct);
        return anyEndpoint && showAmounts && contactIds.Count > 0;
    }

    // ═══════════════ รูปในแชต ═══════════════

    public async Task<IReadOnlyList<object>?> HandleImageAsync(Guid companyId, string? destination, LineChatSource source, string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source.Id)) return null;
        var (enabled, key) = await SettingsAsync(companyId, ct);
        if (!enabled || key == null) return null;
        var sourceKey = SourceKey(destination, source);
        var (contactIds, showAmounts, anyEndpoint) = await BindingAsync(companyId, sourceKey, source, ct);
        // กลุ่ม: เฉพาะกลุ่มที่ผูกกับผู้ติดต่อและเปิดแสดงยอด (สมาชิกกลุ่มเห็นยอดในคำตอบ) — อื่น ๆ ไม่ใช่งานของเส้นนี้
        if (source.IsGroup && (!anyEndpoint || !showAmounts || contactIds.Count == 0)) return null;

        // ฝ่ายค้าน L1: ข้อความ LINE เดียว = แถวเดียว (LINE ส่ง webhook ซ้ำระหว่างรอบแรกยังอ่านรูป) — ตรวจก่อนดาวน์โหลด/OCR · unique index กันอีกชั้น
        var msgId = messageId.Length > 64 ? messageId[..64] : messageId;
        if (await _db.LineChatSlips.AsNoTracking().AnyAsync(x => x.CompanyId == companyId && x.LineMessageId == msgId, ct))
            return One(LineChatSlipRule.Replies.Duplicate);
        // ฝ่ายค้าน L2: LINE ที่ยังไม่ผูก — เพดานแถวต่อบริษัทต่อวันไทย (เกิน ⇒ คำตอบเดิม ไม่เก็บรูป)
        if (contactIds.Count == 0 && await UnboundTodayAsync(companyId, ct) >= LineChatSlipRule.UnboundDailyCap) return null;

        var bytes = await _links.DownloadMessageContentAsync(companyId, messageId, LineChatSlipRule.MaxBytes, ct);
        if (bytes == null || bytes.Length == 0) return One(LineChatSlipRule.Replies.DownloadFailed);
        var sniffed = SlipEvidenceRule.DetectPublicType(bytes);
        var ext = SlipEvidenceRule.ExtensionFor(sniffed);
        if (sniffed == null || ext == null) return null;

        // รอบ 268 (268-1-12 · ฝ่ายค้าน L6): PDF เกินจำนวนหน้าของสลิป ⇒ ไม่ใช่สลิป — ไม่อ่าน ไม่เก็บ (PDPA ม.22 เก็บเท่าที่จำเป็น)
        var isPdf = sniffed == "application/pdf";
        if (isPdf && LineChatSlipRule.PdfPageCount(bytes) > LineChatSlipRule.MaxPdfPages) return One(LineChatSlipRule.Replies.PdfNotSlip);
        var sha = SlipEvidenceRule.Sha256Hex(bytes);
        if (await IsDuplicateFileAsync(companyId, sha, ct)) return One(LineChatSlipRule.Replies.Duplicate);
        var reading = await _reader.ReadAsync(bytes, sniffed, ct);
        // PDF ที่อ่านแล้วไม่ดูเป็นสลิป (สำเนาบัตร/สัญญา) ⇒ ไม่เก็บไฟล์ ไม่เข้าคิว (รูปยังเข้าคิวพนักงานตามเดิม)
        if (isPdf && !reading.LooksLikeSlip) return One(LineChatSlipRule.Replies.PdfNotSlip);
        var slipRef = SlipEvidenceRule.NormalizeRef(reading.Ref);
        // ฝ่ายค้าน M1: ref ซ้ำแต่รูปต่าง ⇒ ไม่ทิ้ง — เข้าคิวพนักงานเหตุ DuplicateRef (OCR อาจอ่าน Biller ID/Ref1 ที่เหมือนกันทุกใบ)
        var refDuplicate = slipRef != null && await IsDuplicateRefAsync(companyId, slipRef, ct);
        // ฝ่ายค้าน 264-P2 L5: ref จาก QR ≠ ref จากข้อความของสำเนาเดียวกันที่ครอป QR ออก / แถวก่อนรอบนี้ ⇒ เทียบค่าจากข้อความด้วย
        var textRef = SlipEvidenceRule.NormalizeRef(reading.TextRef);
        if (!refDuplicate && textRef != null && textRef != slipRef && await IsDuplicateRefAsync(companyId, textRef, ct))
        {
            refDuplicate = true;
            slipRef ??= textRef;
        }

        var now = DateTime.UtcNow;
        var absPath = Path.Combine(_storage.Locator.RelativeBase, "wwwroot", "uploads", "line-chat-slips", now.ToString("yyyy-MM"), $"{Guid.NewGuid():N}{ext}");
        var row = new LineChatSlip
        {
            CompanyId = companyId, SourceKind = source.Kind, SourceKey = sourceKey, ContactId = contactIds.Count == 1 ? contactIds[0] : (Guid?)null,
            LineMessageId = msgId, FileSha256 = sha, StoragePath = absPath, ContentType = sniffed,
            OcrAmount = reading.Amount, OcrAmounts = LineChatSlipRule.AmountsText(reading.Amounts), OcrDate = reading.Date, OcrRef = slipRef,
            // รอบ 264 P2: ผลอ่าน QR + คู่กรณี (ไม่มีชื่อ) — คัดลอกไป intent ตอนแจ้งโอน (TrySubmitAsync) ⇒ SlipReceivingAccount ชั้น "ผู้รับบนสลิป"
            SlipReadJson = OcrSlipText.ToStoredJson(reading),
            ShowAmounts = showAmounts, CreatedBy = "line-webhook",
        };
        // เก็บไฟล์ผ่านที่เก็บกลาง (Local/S3) ในโฟลเดอร์ที่ไม่เสิร์ฟตรง · ฝ่ายค้าน M2: ล้มก่อนแถวถูกบันทึก ⇒ ลบไฟล์ (ไม่มีไฟล์กำพร้า)
        await _storage.WriteAsync(absPath, bytes, sniffed, ct);
        var saved = false;
        try
        {
            await EnsureRopaAsync(companyId, ct);
            if (refDuplicate)
            {
                await ToStaffAsync(row, LineChatSlipRule.Reasons.DuplicateRef, "เลขอ้างอิง " + slipRef, isNew: true, ct);
                return One(LineChatSlipRule.Replies.StaffWillCheck);
            }
            var candidates = contactIds.Count == 0 ? new List<LineChatSlipRule.Candidate>() : await CandidatesAsync(companyId, contactIds, 30, ct);
            var decision = LineChatSlipRule.Decide(reading, candidates, contactIds);

            if (decision.Outcome == LineChatSlipRule.Outcome.One)
            {
                var m = decision.Matches[0];
                var (submitted, problem) = await TrySubmitAsync(companyId, row, m.Doc.DocumentId, m.Doc.ContactId, bytes, m.Amount, ActorAuto,
                    SlipEvidenceService.ChannelLineChat, LineChatSlipRule.Statuses.Submitted, ct);
                saved = submitted;
                if (submitted)
                {
                    await DropFileAsync(row, ct);
                    return One(LineChatSlipRule.Replies.Submitted(m.Doc.Number, m.Amount, showAmounts));
                }
                await ToStaffAsync(row, LineChatSlipRule.Reasons.SubmitFailed, problem, isNew: true, ct);
                return One(LineChatSlipRule.Replies.StaffWillCheck);
            }
            if (decision.Outcome == LineChatSlipRule.Outcome.Many)
            {
                row.Status = LineChatSlipRule.Statuses.AwaitingChoice;
                row.ChoiceExpiresAt = now.Add(LineChatSlipRule.ChoiceTtl);
                row.CandidatesJson = JsonSerializer.Serialize(decision.Matches.Select(x => new CandidateRef(x.Doc.DocumentId, x.Amount)));
                _db.LineChatSlips.Add(row);
                await _db.SaveChangesAsync(ct);
                saved = true;
                return new[] { Text(decision.Matches.Count == 1 ? LineChatSlipRule.Replies.ChooseOne
                    : LineChatSlipRule.Replies.Choose(reading.Amount, showAmounts, decision.Matches.Count), ChoiceItems(key, companyId, sourceKey, row.Id, decision, showAmounts)) };
            }
            await ToStaffAsync(row, decision.Reason, null, isNew: true, ct);
            return One(LineChatSlipRule.Replies.StaffWillCheck);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsMessageDuplicate(ex))
            {
                _db.ChangeTracker.Clear();
                await DeleteQuietlyAsync(absPath);
                return One(LineChatSlipRule.Replies.Duplicate);
            }
            _logger.LogError(ex, "สลิปจากแชต LINE ของบริษัท {CompanyId}: ประมวลล้ม — ส่งเข้าคิวพนักงาน", companyId);
            if (await FallbackToStaffAsync(row, saved, ct)) return One(LineChatSlipRule.Replies.StaffWillCheck);
            await DeleteQuietlyAsync(absPath);
            throw;
        }
    }

    private static List<object> ChoiceItems(byte[] key, Guid companyId, string sourceKey, Guid slipId, LineChatSlipRule.Decision decision, bool showAmounts)
        => decision.Matches.Select(x => (object)new
        {
            type = "action",
            action = new
            {
                type = "postback", label = LineChatSlipRule.Replies.ChoiceLabel(x.Doc.Number),
                data = LineChatSlipRule.PostbackData(key, companyId, sourceKey, slipId, x.Doc.DocumentId),
                displayText = LineChatSlipRule.Replies.ChoiceDisplay(x.Doc.Number, x.Doc.Date, x.Amount, showAmounts),
            },
        }).Append((object)new
        {
            type = "action",
            action = new
            {
                type = "postback", label = LineChatSlipRule.Replies.DeclineLabel,
                data = LineChatSlipRule.PostbackData(key, companyId, sourceKey, slipId, null),
                displayText = LineChatSlipRule.Replies.DeclineLabel,
            },
        }).ToList();

    private static bool IsMessageDuplicate(Exception ex)
        => ex is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505", ConstraintName: "UX_LineChatSlips_Company_Message" } };

    /// <summary>ฝ่ายค้าน M2: ล้มกลางทาง ⇒ แถวต้องอยู่ในคิวพนักงาน (ไม่ใช่หายเงียบ) · แถวยังไม่ถูกบันทึก ⇒ บันทึกใหม่ (ล้าง tracker ที่อาจค้างของจากการบันทึกที่ล้ม) ·
    /// บันทึกแล้ว ⇒ คืนสถานะด้วยคำสั่งเดียว · คืน false เมื่อบันทึกไม่ได้เลย (ผู้เรียกลบไฟล์แล้วโยนต่อ)</summary>
    private async Task<bool> FallbackToStaffAsync(LineChatSlip row, bool saved, CancellationToken ct)
    {
        try
        {
            _db.ChangeTracker.Clear();
            if (saved || await _db.LineChatSlips.AsNoTracking().AnyAsync(x => x.Id == row.Id && x.CompanyId == row.CompanyId, ct))
            {
                await RestoreToStaffAsync(row.CompanyId, row.Id, LineChatSlipRule.Reasons.SystemError, ct);
                return true;
            }
            row.Status = LineChatSlipRule.Statuses.Unmatched;
            row.Reason = LineChatSlipRule.Reasons.SystemError;
            row.UpdatedAt = DateTime.UtcNow;
            _db.LineChatSlips.Add(row);
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "สลิปจากแชต LINE ของบริษัท {CompanyId}: บันทึกเข้าคิวพนักงานไม่สำเร็จ", row.CompanyId);
            return false;
        }
    }

    /// <summary>แถวที่ค้าง "กำลังแจ้งโอน"/รอเลือก ⇒ คิวพนักงาน (คำสั่งเดียว · ไม่พึ่ง tracker ที่อาจเสียจากการบันทึกที่ล้ม)</summary>
    private Task<int> RestoreToStaffAsync(Guid companyId, Guid slipId, string reason, CancellationToken ct)
    {
        var open = new[] { LineChatSlipRule.Statuses.Submitting, LineChatSlipRule.Statuses.AwaitingChoice };
        return _db.LineChatSlips.Where(x => x.Id == slipId && x.CompanyId == companyId && open.Contains(x.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, LineChatSlipRule.Statuses.Unmatched)
                .SetProperty(x => x.Reason, reason).SetProperty(x => x.UpdatedAt, (DateTime?)DateTime.UtcNow), ct);
    }

    private async Task<int> UnboundTodayAsync(Guid companyId, CancellationToken ct)
    {
        var startUtc = (DateTime.UtcNow.AddHours(7)).Date.AddHours(-7);
        return await _db.LineChatSlips.AsNoTracking()
            .CountAsync(x => x.CompanyId == companyId && x.Reason == LineChatSlipRule.Reasons.Unbound && x.CreatedAt >= startUtc, ct);
    }

    /// <summary>ไฟล์นี้ส่งเข้ามาแล้วไหม — แถวแชตที่ยังเปิด + รายการรอยืนยัน/รับเงินแล้ว (ด่านเดียวกับพอร์ทัล) · ทั้งบริษัท</summary>
    private async Task<bool> IsDuplicateFileAsync(Guid companyId, string sha, CancellationToken ct)
    {
        var live = LineChatSlipRule.Statuses.Live;
        var dupStatuses = SlipEvidenceRule.DuplicateStatuses;
        return await _db.LineChatSlips.AsNoTracking().AnyAsync(x => x.CompanyId == companyId && x.FileSha256 == sha && live.Contains(x.Status), ct)
            || await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.CompanyId == companyId && i.SlipFileSha256 == sha && dupStatuses.Contains(i.Status), ct);
    }

    /// <summary>เลขอ้างอิงนี้เคยเห็นไหม (แถวแชตทุกสถานะที่ยังไม่ถูกปิด · รายการรอยืนยัน/รับเงินแล้วที่ลูกค้าพิมพ์เลขอ้างอิงเอง) — สัญญาณให้คนดู ไม่ใช่ข้อห้าม</summary>
    private async Task<bool> IsDuplicateRefAsync(Guid companyId, string slipRef, CancellationToken ct)
    {
        var dupStatuses = SlipEvidenceRule.DuplicateStatuses;
        return await _db.LineChatSlips.AsNoTracking()
                .AnyAsync(x => x.CompanyId == companyId && x.OcrRef == slipRef && x.Status != LineChatSlipRule.Statuses.Dismissed, ct)
            || await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.CompanyId == companyId && i.SlipBankRef == slipRef && dupStatuses.Contains(i.Status), ct);
    }

    /// <summary>เอกสารค้างชำระของผู้ติดต่อที่ผูก LINE (บริษัทเดียวกัน) + ยอดจากตัวคิดของหน้าชำระ — ฝ่ายค้าน L5: กรองชนิดที่รับชำระได้ใน SQL ก่อนจำกัดจำนวน ·
    /// L4: ยอดที่ต้องโอน = หักสลิปรอตรวจของใบแล้ว — เต็มยอดแล้วไม่เป็นผู้สมัคร · เหลือบางส่วนเป็นผู้สมัครด้วยยอดที่เหลือ (ด่านเดียวกับประตูแจ้งโอน <see cref="SlipEvidenceRule.PendingProblem"/>)</summary>
    private async Task<List<LineChatSlipRule.Candidate>> CandidatesAsync(Guid companyId, IReadOnlyCollection<Guid> contactIds, int take, CancellationToken ct)
    {
        var types = DocumentPayLinkPolicy.PayableTypes;
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && contactIds.Contains(d.ContactId) && d.BalanceDue > 0.009m
                && types.Contains(d.DocumentType)
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Rejected && d.Status != DocumentStatus.Paid)
            .OrderByDescending(d => d.DocumentDate).ThenBy(d => d.DocumentNumber).Take(take)
            .Select(d => new { d.Id, d.ContactId, d.DocumentNumber, d.DocumentDate, d.Status, d.BalanceDue })
            .ToListAsync(ct);
        if (docs.Count == 0) return new List<LineChatSlipRule.Candidate>();
        var ids = docs.Select(d => d.Id).ToList();
        var openSlips = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.Document && ids.Contains(i.SourceId)
                && i.MethodKind == PaymentMethodKind.ManualSlip && PaymentIntentPolicy.OpenStatuses.Contains(i.Status))
            .Select(i => new { i.SourceId, i.Amount }).ToListAsync(ct);
        var pending = openSlips.GroupBy(i => i.SourceId).ToDictionary(g => g.Key, g => g.Sum(i => i.Amount));
        var list = new List<LineChatSlipRule.Candidate>();
        foreach (var d in docs)
        {
            var due = await _payLinks.DueAmountsForDocumentAsync(companyId, d.Id, ct);
            if (due == null || due.Refusal != null) continue;
            // สลิปรอตรวจของใบนี้หักออกจากยอดที่ต้องโอน — เต็มยอดแล้ว ⇒ ไม่เป็นผู้สมัคร · เหลือบางส่วน ⇒ ผู้สมัครด้วยยอดที่เหลือ (ลูกค้าโอนงวดที่เหลือ)
            var pendingSum = pending.GetValueOrDefault(d.Id);
            var remaining = Math.Round(due.Net - pendingSum, 2, MidpointRounding.AwayFromZero);
            if (remaining <= 0.009m || SlipEvidenceRule.PendingProblem(pendingSum, remaining, d.BalanceDue) != null) continue;
            list.Add(new LineChatSlipRule.Candidate(d.Id, d.ContactId, d.DocumentNumber, d.DocumentDate, d.Status, d.BalanceDue, remaining,
                Math.Max(remaining, due.Gross - pendingSum)));
        }
        return list;
    }

    /// <summary>แจ้งโอนผ่านประตูเดิม · สำเร็จ ⇒ แถวผูกใบและบันทึก · ไม่สำเร็จด้วยกติกา ⇒ (false, เหตุ) · ล้มอย่างอื่น ⇒ โยน (ผู้เรียกส่งเข้าคิวพนักงาน) ·
    /// ฝ่ายค้าน M1: <b>ไม่ส่งเลขอ้างอิงจาก OCR</b> เข้าประตู — ค่าที่เครื่องอ่านเดาไม่ใช่เลขที่ลูกค้าแจ้ง (กันซ้ำเท็จทุกช่องทาง · ไม่เป็นตัวระบุตัวให้ปิดหนี้อัตโนมัติ)</summary>
    private async Task<(bool Ok, string? Problem)> TrySubmitAsync(Guid companyId, LineChatSlip row, Guid documentId, Guid? contactId, byte[] bytes,
        decimal amount, string actor, string channel, string doneStatus, CancellationToken ct)
    {
        SlipEvidenceResult r;
        try
        {
            r = await _slips.SubmitAsync(companyId, documentId, contactId,
                new SlipFile(bytes, row.ContentType, "line-slip" + (SlipEvidenceRule.ExtensionFor(row.ContentType) ?? "")),
                amount, bankRef: null, channel, ct);
        }
        catch (Exception ex) when (ex is BusinessRuleException or KeyNotFoundException or ArgumentException)
        {
            return (false, ex.Message);
        }
        // รอบ 264 P2 (คำตัดสิน 264-4-5): ผลอ่านของสลิปนี้ไปอยู่บนรายการชำระ — ตัวตัดสินบัญชีที่เงินเข้า/การแจ้งคู่ค้าอ่านจากที่เดียว (ทั้งสามทางเข้าของ LINE เดินผ่านตรงนี้)
        if (row.SlipReadJson != null)
        {
            var intent = await _db.PaymentIntents.FirstOrDefaultAsync(i => i.Id == r.Intent.Id && i.CompanyId == companyId, ct);
            if (intent != null && intent.SlipReadJson == null) intent.SlipReadJson = row.SlipReadJson;
        }
        row.Status = doneStatus;
        row.DocumentId = documentId;
        row.PaymentIntentId = r.Intent.Id;
        row.Reason = null;
        row.ReasonDetail = null;
        row.ResolvedAt = DateTime.UtcNow;
        row.ResolvedBy = actor;
        if (_db.Entry(row).State == EntityState.Detached) _db.LineChatSlips.Add(row);
        await _db.SaveChangesAsync(ct);
        return (true, null);
    }

    /// <summary>ฝ่ายค้าน M4: สำเนารูปในแชตลบเมื่อผูก/ปิด (สลิปที่ผูกมีสำเนาเป็นไฟล์แนบเอกสารแล้ว) · ลบไม่ได้ ⇒ LogError แล้วงานเบื้องหลังลองใหม่</summary>
    private async Task DropFileAsync(LineChatSlip row, CancellationToken ct)
    {
        try
        {
            await _storage.DeleteAsync(row.StoragePath, ct);
            var now = DateTime.UtcNow;
            row.FileDeletedAt = now;
            await _db.LineChatSlips.Where(x => x.Id == row.Id && x.CompanyId == row.CompanyId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.FileDeletedAt, (DateTime?)now), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ลบสำเนารูปสลิปจากแชตไม่สำเร็จ (สลิป {SlipId}) — งานเก็บรักษาจะลองใหม่", row.Id);
        }
    }

    private async Task DeleteQuietlyAsync(string path)
    {
        try { await _storage.DeleteAsync(path); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "ลบไฟล์สลิปที่บันทึกไม่สำเร็จไม่ได้ {Path}", path); }
    }

    /// <summary>เข้าคิวพนักงานพร้อมเหตุ + แจ้งทีม (ฝ่ายค้าน L2: เหตุ "LINE ยังไม่ผูก" ไม่แจ้งรายแถว — รวมใน Autopilot)</summary>
    private async Task ToStaffAsync(LineChatSlip row, string? reason, string? detail, bool isNew, CancellationToken ct)
    {
        row.Status = LineChatSlipRule.Statuses.Unmatched;
        row.Reason = reason;
        row.ReasonDetail = detail == null ? null : LineMessageLimits.Truncate(detail, 500);
        row.UpdatedAt = DateTime.UtcNow;
        if (isNew && _db.Entry(row).State == EntityState.Detached) _db.LineChatSlips.Add(row);
        await _db.SaveChangesAsync(ct);
        if (_notify != null && reason != LineChatSlipRule.Reasons.Unbound)
        {
            var (title, message) = LineChatSlipRule.StaffNotice(reason, row.OcrAmount);
            await _notify.DispatchAsync(row.CompanyId, Accounting.Models.Constants.NotificationEvents.AutopilotSlipSubmitted, new NotificationContext
            {
                Title = title, Message = message, ActionUrl = AutopilotService.SlipQueueHref, EntityType = LineChatSlipRule.AuditEntityType, EntityId = row.Id,
            });
        }
    }

    /// <summary>ฝ่ายค้าน M4 (PDPA ม.39): แถว RoPA ของกิจกรรมนี้ — ครั้งเดียวต่อบริษัท (บันทึกพร้อมแถวสลิป) · ไม่ทับที่บริษัทแก้ไว้</summary>
    private async Task EnsureRopaAsync(Guid companyId, CancellationToken ct)
    {
        // รอบ 268 (268-1-12): แถวเดิมที่ยังไม่รวม PDF ⇒ ปรับประเภทข้อมูลให้ตรงสิ่งที่ประมวลผลจริง (RoPA ม.39)
        await _db.PdpaProcessingActivities
            .Where(r => r.CompanyId == companyId && r.Purpose == LineChatSlipRule.RopaPurpose && r.DataCategories != LineChatSlipRule.RopaDataCategories)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.DataCategories, LineChatSlipRule.RopaDataCategories), ct);
        if (await _db.PdpaProcessingActivities.AsNoTracking().AnyAsync(r => r.CompanyId == companyId && r.Purpose == LineChatSlipRule.RopaPurpose, ct)) return;
        if (_db.ChangeTracker.Entries<PdpaProcessingActivity>().Any(e => e.Entity.CompanyId == companyId && e.Entity.Purpose == LineChatSlipRule.RopaPurpose)) return;
        _db.PdpaProcessingActivities.Add(new PdpaProcessingActivity
        {
            CompanyId = companyId, Purpose = LineChatSlipRule.RopaPurpose, LegalBasis = LineChatSlipRule.RopaLegalBasis,
            DataCategories = LineChatSlipRule.RopaDataCategories, RetentionPeriod = LineChatSlipRule.RopaRetention,
            Recipients = LineChatSlipRule.RopaRecipients, TransfersOutsideThailand = false, Notes = LineChatSlipRule.RopaNote,
            CreatedBy = "system", LastReviewedBy = "system",
        });
    }

    // ═══════════════ ปุ่มเลือกใบ ═══════════════

    public async Task<IReadOnlyList<object>?> HandlePostbackAsync(Guid companyId, string? destination, LineChatSource source, string? data, CancellationToken ct = default)
    {
        if (!LineChatSlipRule.IsOurs(data)) return null;
        var pb = LineChatSlipRule.TryParse(data);
        var (enabled, key) = await SettingsAsync(companyId, ct);
        if (pb == null || key == null || string.IsNullOrWhiteSpace(source.Id)) return One(LineChatSlipRule.Replies.ChoiceInvalid);
        var sourceKey = SourceKey(destination, source);
        var row = await _db.LineChatSlips.FirstOrDefaultAsync(x => x.Id == pb.SlipId && x.CompanyId == companyId, ct);
        // ลายเซ็น (บริษัท + แหล่งแชต + สลิป + ใบ) + แถวต้องเป็นของแหล่งแชตเดียวกัน — ไม่ผ่าน = ข้อความเดียว (ไม่บอกว่าผิดตรงไหน · เพดานต่อแหล่งแชตกันที่ผู้เรียก)
        if (row == null || row.SourceKey != sourceKey || !LineChatSlipRule.Verify(key, companyId, sourceKey, pb.SlipId, pb.DocumentId, pb.Signature))
        {
            _logger.LogInformation("LINE postback สลิปของบริษัท {CompanyId}: ลายเซ็น/แหล่งแชตไม่ตรง", companyId);
            return One(LineChatSlipRule.Replies.ChoiceInvalid);
        }
        var now = DateTime.UtcNow;
        if (LineChatSlipRule.ChoiceProblem(row.Status, row.ChoiceExpiresAt, now) != null)
        {
            if (row.Status == LineChatSlipRule.Statuses.AwaitingChoice)
                await ToStaffAsync(row, LineChatSlipRule.Reasons.ChoiceExpired, null, isNew: false, ct);
            return One(LineChatSlipRule.Replies.ChoiceInvalid);
        }
        // ฝ่ายค้าน L9 (คำตัดสิน 260-1-10): ปิดสวิตช์แล้ว ⇒ ปุ่มที่ออกไปแล้วกดไม่ได้ — สลิปเข้าคิวพนักงาน (ไม่หาย)
        if (!enabled)
        {
            await ToStaffAsync(row, LineChatSlipRule.Reasons.IntakeOff, null, isNew: false, ct);
            return One(LineChatSlipRule.Replies.IntakeOff);
        }
        if (pb.DocumentId == null)
        {
            await ToStaffAsync(row, LineChatSlipRule.Reasons.CustomerDeclined, null, isNew: false, ct);
            return One(LineChatSlipRule.Replies.Declined);
        }
        var offered = ParseCandidates(row.CandidatesJson).FirstOrDefault(c => c.d == pb.DocumentId.Value);
        if (offered == null) return One(LineChatSlipRule.Replies.ChoiceInvalid);
        // ผูก LINE ยังอยู่ + ใบยังเป็นของผู้ติดต่อที่ผูก (บริษัทเดียวกัน) ณ ตอนกด
        var (contactIds, _, _) = await BindingAsync(companyId, sourceKey, source, ct);
        var doc = await _db.Documents.AsNoTracking().Where(d => d.Id == offered.d && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new { d.Id, d.ContactId, d.DocumentNumber }).FirstOrDefaultAsync(ct);
        if (doc == null || !contactIds.Contains(doc.ContactId)) return One(LineChatSlipRule.Replies.ChoiceInvalid);

        // compare-and-set: กดซ้ำ/สองเครื่องพร้อมกัน = ครั้งเดียว
        if (!await ClaimAsync(companyId, row.Id, new[] { LineChatSlipRule.Statuses.AwaitingChoice }, ct)) return One(LineChatSlipRule.Replies.ChoiceInvalid);
        // ค่าในฐาน = Submitting · ฝ่ายค้าน M2: ล้มด้วยเหตุใดก็ตามหลังจองแล้ว ⇒ แถวกลับคิวพนักงาน (ไม่ค้าง Submitting)
        try
        {
            var bytes = await _storage.ReadAllBytesAsync(row.StoragePath, ct);
            if (bytes == null)
            {
                await ToStaffAsync(row, LineChatSlipRule.Reasons.SubmitFailed, "ไม่พบไฟล์สลิปในที่เก็บ", isNew: false, ct);
                return One(LineChatSlipRule.Replies.StaffWillCheck);
            }
            var (ok, problem) = await TrySubmitAsync(companyId, row, doc.Id, doc.ContactId, bytes, offered.a, ActorCustomer,
                SlipEvidenceService.ChannelLineChat, LineChatSlipRule.Statuses.Submitted, ct);
            if (ok)
            {
                await DropFileAsync(row, ct);
                return One(LineChatSlipRule.Replies.Submitted(doc.DocumentNumber, offered.a, row.ShowAmounts));
            }
            await ToStaffAsync(row, LineChatSlipRule.Reasons.SubmitFailed, problem, isNew: false, ct);
            return One(LineChatSlipRule.Replies.StaffWillCheck);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "สลิปจากแชต LINE ของบริษัท {CompanyId}: แจ้งโอนจากปุ่มเลือกใบล้ม — ส่งเข้าคิวพนักงาน", companyId);
            _db.ChangeTracker.Clear();
            await RestoreToStaffAsync(companyId, row.Id, LineChatSlipRule.Reasons.SystemError, CancellationToken.None);
            return One(LineChatSlipRule.Replies.StaffWillCheck);
        }
    }

    private static List<CandidateRef> ParseCandidates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<CandidateRef>();
        try { return JsonSerializer.Deserialize<List<CandidateRef>>(json) ?? new List<CandidateRef>(); }
        catch (JsonException) { return new List<CandidateRef>(); }
    }

    /// <summary>จองแถวไปขั้น "กำลังแจ้งโอน" แบบ compare-and-set · false = มีคนทำไปแล้ว</summary>
    private async Task<bool> ClaimAsync(Guid companyId, Guid slipId, string[] fromStatuses, CancellationToken ct)
        => await _db.LineChatSlips.Where(x => x.Id == slipId && x.CompanyId == companyId && fromStatuses.Contains(x.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, LineChatSlipRule.Statuses.Submitting).SetProperty(x => x.UpdatedAt, (DateTime?)DateTime.UtcNow), ct) == 1;

    // ═══════════════ คิวพนักงาน (อ่านอย่างเดียวใน GET — ฝ่ายค้าน L5) ═══════════════

    /// <summary>แถวที่อยู่ในคิวพนักงาน: ไม่ได้ผูก · ตัวเลือกหมดอายุ (ยังไม่มีใครย้ายสถานะ) · ค้าง "กำลังแจ้งโอน" นานเกิน (ระบบล่มกลางทาง — ฝ่ายค้าน M2)</summary>
    private IQueryable<LineChatSlip> QueueQuery(Guid companyId, DateTime nowUtc) => QueueOf(_db.LineChatSlips.AsNoTracking(), companyId, nowUtc);

    /// <summary>ตัวกรองคิวพนักงานตัวเดียว — บริการนี้ + Autopilot (อ่านอย่างเดียว)</summary>
    public static IQueryable<LineChatSlip> QueueOf(IQueryable<LineChatSlip> source, Guid companyId, DateTime nowUtc)
    {
        var stuckBefore = nowUtc - LineChatSlipRule.StuckSubmitting;
        return source.Where(x => x.CompanyId == companyId
            && (x.Status == LineChatSlipRule.Statuses.Unmatched
                || (x.Status == LineChatSlipRule.Statuses.AwaitingChoice && x.ChoiceExpiresAt != null && x.ChoiceExpiresAt <= nowUtc)
                || (x.Status == LineChatSlipRule.Statuses.Submitting && (x.UpdatedAt == null || x.UpdatedAt < stuckBefore))));
    }

    /// <summary>แถวนี้อยู่ในคิวพนักงานไหม (ตัวเดียวกับ <see cref="QueueQuery"/> สำหรับแถวที่โหลดแล้ว — ผูก/ปิดได้เฉพาะแถวในคิว)</summary>
    private static bool InQueue(LineChatSlip x, DateTime nowUtc)
        => x.Status == LineChatSlipRule.Statuses.Unmatched
           || x.Status == LineChatSlipRule.Statuses.AwaitingChoice
           || (x.Status == LineChatSlipRule.Statuses.Submitting && (x.UpdatedAt == null || x.UpdatedAt < nowUtc - LineChatSlipRule.StuckSubmitting));

    public static string EffectiveReason(LineChatSlip x)
        => x.Status == LineChatSlipRule.Statuses.AwaitingChoice ? LineChatSlipRule.Reasons.ChoiceExpired
            : x.Status == LineChatSlipRule.Statuses.Submitting ? LineChatSlipRule.Reasons.SystemError
            : x.Reason ?? "";

    public Task<int> OpenCountAsync(Guid companyId, CancellationToken ct = default)
        => QueueQuery(companyId, DateTime.UtcNow).CountAsync(ct);

    public async Task<LineChatSlipListResponse> ListOpenAsync(Guid companyId, CancellationToken ct = default)
    {
        var rows = await QueueQuery(companyId, DateTime.UtcNow).OrderBy(x => x.CreatedAt).Take(50).ToListAsync(ct);
        var contactIds = rows.Where(r => r.ContactId != null).Select(r => r.ContactId!.Value).Distinct().ToList();
        var names = contactIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Contacts.AsNoTracking().Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        // ฝ่ายค้าน L5: จำนวนการคิดยอดต่อการโหลดหน้ามีเพดาน (ผู้ติดต่อ ≤ 10 · ใบ ≤ 10 ต่อผู้ติดต่อ) — ใบอื่นพิมพ์เลขที่ได้
        var optionsByContact = new Dictionary<Guid, List<LineChatSlipRule.Candidate>>();
        foreach (var cid in contactIds.Take(10)) optionsByContact[cid] = await CandidatesAsync(companyId, new[] { cid }, LineChatSlipRule.MaxChoices, ct);
        var items = rows.Select(r =>
        {
            var opts = r.ContactId is Guid c && optionsByContact.TryGetValue(c, out var list) ? list : new List<LineChatSlipRule.Candidate>();
            return new LineChatSlipRow(r.Id, r.CreatedAt, r.Status, r.SourceKind == LineChatSource.KindGroup ? "กลุ่ม LINE ของผู้ติดต่อ" : "แชต LINE 1:1",
                r.ContactId, r.ContactId is Guid n && names.TryGetValue(n, out var name) ? name : null,
                LineChatSlipRule.Reasons.Label(EffectiveReason(r)), r.ReasonDetail, r.OcrAmount, r.OcrAmounts, r.OcrDate, r.OcrRef,
                opts.Select(o => new LineChatSlipDocOption(o.DocumentId, o.Number, o.Date, o.NetDue, o.GrossDue,
                    r.OcrAmount is decimal a && a == Math.Round(o.NetDue, 2, MidpointRounding.AwayFromZero))).ToList());
        }).ToList();
        return new LineChatSlipListResponse(items.Count, items);
    }

    public async Task<LineChatSlipAssignResult> AssignAsync(Guid companyId, Guid slipId, AssignLineChatSlipRequest request, string actor, CancellationToken ct = default)
    {
        var row = await _db.LineChatSlips.FirstOrDefaultAsync(x => x.Id == slipId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบสลิป");
        if (!InQueue(row, DateTime.UtcNow))
            throw new BusinessRuleException("สลิปนี้ผูกเอกสารหรือปิดไปแล้ว — โหลดหน้าใหม่", LineChatSlipRule.RuleCode, 409);
        if (request.Amount is decimal reqAmount && reqAmount <= 0m)
            throw new BusinessRuleException("ยอดต้องมากกว่า 0", LineChatSlipRule.RuleCode, 400);
        var number = request.DocumentNumber?.Trim();
        if (request.DocumentId == null && string.IsNullOrEmpty(number))
            throw new BusinessRuleException("เลือกเอกสาร หรือพิมพ์เลขที่เอกสาร", LineChatSlipRule.RuleCode, 400);
        // ฝ่ายค้าน L6: ค้นเฉพาะชนิดที่รับชำระได้ · เลขที่ซ้ำหลายใบ ⇒ ไม่เดา (ให้เลือกจากรายการ)
        var types = DocumentPayLinkPolicy.PayableTypes;
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && types.Contains(d.DocumentType)
                && (request.DocumentId != null ? d.Id == request.DocumentId : d.DocumentNumber == number))
            .OrderBy(d => d.DocumentDate).Select(d => new { d.Id, d.DocumentNumber, d.ContactId }).Take(3).ToListAsync(ct);
        if (docs.Count == 0) throw new BusinessRuleException("ไม่พบใบแจ้งหนี้/ใบกำกับ/ใบเพิ่มหนี้เลขนี้ในบริษัท — ตรวจเลขที่เอกสาร", LineChatSlipRule.RuleCode, 404);
        if (docs.Count > 1) throw new BusinessRuleException("เลขที่นี้ตรงกับเอกสารหลายใบ — เปิดเอกสารเพื่อดูว่าใบไหน แล้วผูกจากหน้าเอกสาร หรือเลือกจากรายการ", LineChatSlipRule.RuleCode, 409);
        var doc = docs[0];
        var due = await _payLinks.DueAmountsForDocumentAsync(companyId, doc.Id, ct);
        if (due?.Refusal is string refusal) throw new BusinessRuleException(refusal, LineChatSlipRule.RuleCode, 400);
        var amount = Math.Round(request.Amount ?? row.OcrAmount ?? due?.Net ?? 0m, 2, MidpointRounding.AwayFromZero);
        var crossContact = row.ContactId != null && row.ContactId != doc.ContactId;
        var previous = row.Status;
        if (!await ClaimAsync(companyId, row.Id, new[] { previous }, ct))
            throw new BusinessRuleException("สลิปนี้กำลังถูกผูกโดยรายการอื่น — โหลดหน้าใหม่", LineChatSlipRule.RuleCode, 409);
        // ฝ่ายค้าน M2: ล้มด้วยเหตุใดก็ตามหลังจอง ⇒ คืนสถานะเดิม (แถวกลับคิว) แล้วบอกเหตุ
        var done = false;
        try
        {
            var bytes = await _storage.ReadAllBytesAsync(row.StoragePath, ct);
            if (bytes == null) throw new BusinessRuleException("ไม่พบไฟล์สลิปในที่เก็บ", LineChatSlipRule.RuleCode, 409);
            var (ok, problem) = await TrySubmitAsync(companyId, row, doc.Id, null, bytes, amount, actor,
                SlipEvidenceService.ChannelLineChatStaff, LineChatSlipRule.Statuses.Assigned, ct);
            if (!ok) throw new BusinessRuleException(problem ?? "ผูกสลิปไม่สำเร็จ", LineChatSlipRule.RuleCode, 409);
            done = true;
        }
        finally
        {
            if (!done)
            {
                _db.ChangeTracker.Clear();
                await _db.LineChatSlips.Where(x => x.Id == row.Id && x.CompanyId == companyId && x.Status == LineChatSlipRule.Statuses.Submitting)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, previous == LineChatSlipRule.Statuses.Submitting ? LineChatSlipRule.Statuses.Unmatched : previous)
                        .SetProperty(x => x.UpdatedAt, (DateTime?)DateTime.UtcNow), CancellationToken.None);
            }
        }
        // ฝ่ายค้าน 268 L5: audit ของการผูกบันทึกก่อนส่งข้อความ (ส่งแล้ว save ล้ม = ลูกค้าได้ข้อความแต่ไม่มีร่องรอย)
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = actor, Action = AuditAction.Update, EntityType = LineChatSlipRule.AuditEntityType, EntityId = row.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new { op = "assign", documentId = doc.Id, doc.DocumentNumber, amount, paymentIntentId = row.PaymentIntentId,
                crossContact, chatContactId = row.ContactId, documentContactId = doc.ContactId, rule = LineChatSlipRule.RuleCode }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        await DropFileAsync(row, ct);
        // รอบ 268 (268-1-4 · ฝ่ายค้าน M1/L5 → 268-1-7/8): แจ้งลูกค้าในแชตเดิม — ใบต้องเป็นของผู้ติดต่อที่ผูกแชตนี้ · ผูกนานหลังสลิปเข้า + ชั่วโมงเงียบ ⇒ เลื่อน · ล้ม ⇒ ส่งซ้ำครั้งเดียว
        var notice = crossContact ? LineChatSlipRule.LinkedNotice.CrossContact : await NoticeAfterAssignAsync(row, doc.DocumentNumber, doc.ContactId, amount, ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = actor, Action = AuditAction.Update, EntityType = LineChatSlipRule.AuditEntityType, EntityId = row.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new { op = "assign-notice", documentId = doc.Id, customerNotice = notice, rule = LineChatSlipRule.RuleCode }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return new LineChatSlipAssignResult(row.Id, doc.Id, doc.DocumentNumber, row.PaymentIntentId!.Value, amount, crossContact, notice);
    }

    /// <summary>268-1-8: ส่งเลย / เลื่อนพ้นชั่วโมงเงียบ / ล้มแล้วเข้าคิวส่งซ้ำครั้งเดียว (คิวเบื้องหลังเดิม <c>DeferredNotifications</c>)</summary>
    private async Task<string> NoticeAfterAssignAsync(LineChatSlip row, string documentNumber, Guid documentContactId, decimal amount, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var quiet = await _db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == row.CompanyId)
            .Select(x => new { x.LineQuietHoursEnabled, x.LineQuietStartHour, x.LineQuietEndHour }).FirstOrDefaultAsync(ct);
        var defer = quiet == null ? null
            : LineChatSlipRule.LinkedNoticeDeferUntil(now, row.CreatedAt, quiet.LineQuietHoursEnabled, quiet.LineQuietStartHour, quiet.LineQuietEndHour);
        if (defer is DateTime until)
        {
            await QueueLinkedNoticeAsync(row, until, ct);
            return LineChatSlipRule.LinkedNotice.Deferred(until);
        }
        var (notice, retryable) = await NotifyChatLinkedAsync(row, documentNumber, documentContactId, amount, ct);
        if (!retryable) return notice;
        await QueueLinkedNoticeAsync(row, now.AddMinutes(1), ct);
        return LineChatSlipRule.LinkedNotice.FailedWillRetry(notice);
    }

    private async Task QueueLinkedNoticeAsync(LineChatSlip row, DateTime dueUtc, CancellationToken ct)
    {
        // รอบ 270 (270-1-2): ประทับการผูกที่ข้อความนี้พูดถึง — ตอนส่งต้องยังเป็นอันเดิม
        var linkKey = LineChatSlipRule.LinkKey(row.DocumentId, row.PaymentIntentId);
        var existing = await _db.DeferredNotifications.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.CompanyId == row.CompanyId && d.Kind == DeferredNotifyPolicy.LineChatSlipLinked && d.EntityId == row.Id, ct);
        if (existing != null)
        {
            // ฝ่ายค้าน 270 L5: แถวคิวเดิมเป็นของการผูกอื่น (unique Kind+EntityId) ⇒ ใช้แถวเดิมกับการผูกใหม่ — ไม่เงียบข้ามข้อความใหม่
            if (existing.ExpectedKey == linkKey) return;
            existing.ExpectedKey = linkKey; existing.DueAt = dueUtc; existing.DoneAt = null; existing.ClaimedUntil = null;
            existing.Attempts = 0; existing.LastError = null; existing.Note = null; existing.IsDeleted = false; existing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return;
        }
        var q = DeferredNotifyPolicy.New(row.CompanyId, Guid.Empty, DeferredNotifyPolicy.LineChatSlipLinked, row.Id, dueUtc);
        q.ExpectedKey = linkKey;
        _db.DeferredNotifications.Add(q);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<string?> DispatchLinkedNoticeAsync(Guid companyId, Guid slipId, CancellationToken ct = default)
    {
        var row = await _db.LineChatSlips.AsNoTracking().FirstOrDefaultAsync(x => x.Id == slipId && x.CompanyId == companyId, ct);
        // รอบ 270 (270-1-2): ถึงคิวแล้วการผูกต้องยังเป็นอันเดิม — พนักงานปิด/ผูกใหม่/ปฏิเสธสลิประหว่างรอ ⇒ ปิดแถวพร้อมเหตุ (ไม่ส่งข้อความที่ไม่จริงแล้ว)
        var expected = await _db.DeferredNotifications.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.Kind == DeferredNotifyPolicy.LineChatSlipLinked && d.EntityId == slipId)
            .Select(d => d.ExpectedKey).FirstOrDefaultAsync(ct);
        var intentFacts = row?.PaymentIntentId is Guid pid
            ? await _db.PaymentIntents.AsNoTracking().Where(i => i.Id == pid && i.CompanyId == companyId)
                .Select(i => new { i.Status, i.Amount, DocId = i.SourceKind == PaymentSourceKind.Document ? (Guid?)i.SourceId : null }).FirstOrDefaultAsync(ct)
            : null;
        // ฝ่ายค้าน 270 L4: ใบที่ถูกยกเลิก/ลบหลังเข้าคิว ⇒ ไม่บอกลูกค้าว่า "ผูกกับใบนั้นแล้ว"
        var documentLive = row?.DocumentId is Guid liveId
            && await _db.Documents.AsNoTracking().AnyAsync(d => d.Id == liveId && d.CompanyId == companyId && !d.IsDeleted && d.Status != DocumentStatus.Voided, ct);
        if (LineChatSlipRule.LinkedNoticeStaleReason(expected, row?.Status, row?.DocumentId, row?.PaymentIntentId, intentFacts?.Status, intentFacts?.DocId,
                documentLive) is string stale)
        {
            if (row != null)
            {
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId, UserEmail = ActorAuto, Action = AuditAction.Update, EntityType = LineChatSlipRule.AuditEntityType, EntityId = row.Id.ToString(),
                    NewValues = JsonSerializer.Serialize(new { op = "assign-notice-stale", expected, current = LineChatSlipRule.LinkKey(row.DocumentId, row.PaymentIntentId),
                        customerNotice = stale, rule = LineChatSlipRule.RuleCode }),
                    Timestamp = DateTime.UtcNow,
                });
                await _db.SaveChangesAsync(ct);
            }
            return stale;
        }
        var docId = row!.DocumentId!.Value;
        var doc = await _db.Documents.AsNoTracking().Where(d => d.Id == docId && d.CompanyId == companyId)
            .Select(d => new { d.DocumentNumber, d.ContactId }).FirstOrDefaultAsync(ct);
        var amount = intentFacts?.Amount;
        if (doc == null || amount == null) return "ไม่พบเอกสาร/รายการรอยืนยันของสลิป — ไม่แจ้ง";
        var (notice, retryable) = await NotifyChatLinkedAsync(row, doc.DocumentNumber, doc.ContactId, amount.Value, ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = ActorAuto, Action = AuditAction.Update, EntityType = LineChatSlipRule.AuditEntityType, EntityId = row.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new { op = "assign-notice-deferred", documentId = docId, customerNotice = notice, retryable, rule = LineChatSlipRule.RuleCode }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        // ฝ่ายค้าน 270 M1 (270-2-2): ล้มชั่วคราว (Retry) ⇒ โยนให้คิวถอยแล้วลองใหม่ (DeferredNotifyPolicy.AfterFailure — ครบจำนวนครั้งแล้วปิดพร้อม LastError) ·
        // ปิดแถวเฉพาะเมื่อส่งแล้ว (null) หรือล้มถาวร (เหตุ = Note)
        if (retryable) throw new DeferredNotifyRetryException(notice);
        return notice == LineChatSlipRule.LinkedNotice.Sent ? null : notice;
    }

    /// <summary>รอบ 268 (คำตัดสิน 268-1-4): ข้อความถึงแชตที่ส่งสลิป — ปลายทางของผู้ติดต่อที่ผูกแหล่งแชตนี้ (กุญแจ = <c>LineIdHash</c> เดียวกับ <see cref="BindingAsync"/>) ·
    /// ยอดเฉพาะเมื่อแสดงยอดได้ทั้งแถว (ค่าที่ประทับตอนรับรูป) และทุกปลายทาง · ไม่รอชั่วโมงเงียบ (คำตอบต่อสลิปที่ลูกค้าส่งเอง — แบบเดียวกับใบเสร็จหลังลูกค้าจ่าย · push ตรง ไม่ผ่านคิวการ์ด) ·
    /// retry key = id ของแถว (ผูกซ้ำไม่ได้ · LINE ไม่ส่งสองครั้ง) · ไม่โยน (ผูกแล้ว — ผลกลับเป็นข้อความถึงพนักงาน)</summary>
    private async Task<(string Notice, bool Retryable)> NotifyChatLinkedAsync(LineChatSlip row, string documentNumber, Guid documentContactId, decimal amount,
        CancellationToken ct)
    {
        if (_line == null) return (LineChatSlipRule.LinkedNotice.NoLine, false);
        try
        {
            var eps = await _db.ContactNotifyEndpoints.AsNoTracking()
                .Where(e => e.CompanyId == row.CompanyId && e.LineIdHash == row.SourceKey && e.UnlinkedAt == null && e.BlockedAt == null && !e.IsDeleted
                    && (e.Kind == NotifyEndpointKind.LineUser || e.Kind == NotifyEndpointKind.LineGroup))
                .OrderBy(e => e.LinkedAt).ToListAsync(ct);
            if (eps.Count == 0) return (LineChatSlipRule.LinkedNotice.Unbound, false);
            // ฝ่ายค้าน 268 M1 (268-1-7): ใบต้องเป็นของผู้ติดต่อที่ผูกแชตนี้ (ผู้ติดต่อยังอยู่) — แถวสลิปที่ไม่มี ContactId (LINE ผูกหลายราย/ผูกทีหลัง) ไม่ใช่ใบอนุญาต
            var boundContacts = eps.Select(e => e.ContactId).Distinct().ToList();
            if (!boundContacts.Contains(documentContactId)
                || !await _db.Contacts.AsNoTracking().AnyAsync(c => c.Id == documentContactId && c.CompanyId == row.CompanyId && !c.IsDeleted, ct))
                return (LineChatSlipRule.LinkedNotice.CrossContact, false);
            var ep = eps.First(e => e.ContactId == documentContactId);
            var isGroup = ep.Kind == NotifyEndpointKind.LineGroup;
            var to = _secrets.Unprotect(isGroup ? ep.LineGroupId : ep.LineUserId);
            if (string.IsNullOrWhiteSpace(to)) return (LineChatSlipRule.LinkedNotice.Failed("ถอดรหัสปลายทางไม่ได้"), false);
            var showAmounts = row.ShowAmounts && eps.All(e => e.ShowAmounts);
            var r = await _line.TryPushContactTextAsync(row.CompanyId, ep.OaScope, isGroup ? LineTargetKind.ContactGroup : LineTargetKind.TenantMember, to,
                LineChatSlipRule.Replies.LinkedByStaff(documentNumber, amount, showAmounts),
                LineChatSlipRule.LinkedNoticeRetryKey(row.Id, LineChatSlipRule.LinkKey(row.DocumentId, row.PaymentIntentId)));
            // รอบ 270 (270-1-3): จำแนกด้วยตัวตัดสินเดียวของ LINE (LinePushOutcome) — เดิม "ไม่มีสถานะ HTTP" = ชั่วคราวเสมอ ทั้งที่ส่วนใหญ่เป็นเหตุถาวร
            // (ไม่มี token/ช่องเสีย/นโยบายปฏิเสธ) ⇒ ลองซ้ำไม่ช่วย · ชั่วคราวจริง = เครือข่ายล้ม · 5xx · 429 ที่ไม่ใช่โควตาเดือน · 409 (retry key เดิม) = LINE รับไปแล้ว
            var cls = LinePushOutcome.Classify(r.Ok, r.HttpStatus, r.Error, r.RuleCode, r.TransportError);
            return cls.Verdict == LinePushVerdict.Sent ? (LineChatSlipRule.LinkedNotice.Sent, false)
                : (LineChatSlipRule.LinkedNotice.Failed(cls.Reason ?? r.Error), cls.Verdict == LinePushVerdict.Retry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งลูกค้าในแชตหลังผูกสลิป {Slip} ไม่สำเร็จ", row.Id);
            return (LineChatSlipRule.LinkedNotice.Failed(null), true);
        }
    }

    public async Task DismissAsync(Guid companyId, Guid slipId, string? reason, string actor, CancellationToken ct = default)
    {
        var why = (reason ?? "").Trim();
        if (why.Length == 0) throw new BusinessRuleException("กรุณาระบุเหตุที่ปิดรายการ (เช่น ไม่ใช่สลิป · บันทึกเป็นเงินรอจัดสรรจากรายการเดินบัญชีแล้ว)", LineChatSlipRule.RuleCode, 400);
        var row = await _db.LineChatSlips.FirstOrDefaultAsync(x => x.Id == slipId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบสลิป");
        if (!InQueue(row, DateTime.UtcNow))
            throw new BusinessRuleException("สลิปนี้ผูกเอกสารหรือปิดไปแล้ว — โหลดหน้าใหม่", LineChatSlipRule.RuleCode, 409);
        var now = DateTime.UtcNow;
        row.Status = LineChatSlipRule.Statuses.Dismissed;
        row.ReasonDetail = LineMessageLimits.Truncate(why, 500);
        row.ResolvedAt = now;
        row.ResolvedBy = actor;
        row.UpdatedAt = now;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = actor, Action = AuditAction.Update, EntityType = LineChatSlipRule.AuditEntityType, EntityId = row.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new { op = "dismiss", reason = row.ReasonDetail, rule = LineChatSlipRule.RuleCode }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        await DropFileAsync(row, ct);
    }

    public async Task<(byte[] Bytes, string ContentType)?> ReadFileAsync(Guid companyId, Guid slipId, CancellationToken ct = default)
    {
        var row = await _db.LineChatSlips.AsNoTracking().Where(x => x.Id == slipId && x.CompanyId == companyId && x.FileDeletedAt == null)
            .Select(x => new { x.StoragePath, x.ContentType }).FirstOrDefaultAsync(ct);
        if (row == null) return null;
        var bytes = await _storage.ReadAllBytesAsync(row.StoragePath, ct);
        return bytes == null ? null : (bytes, row.ContentType);
    }

    // ═══════════════ เก็บรักษา (PDPA — ฝ่ายค้าน M4) ═══════════════

    /// <summary>ลบสำเนารูปที่ไม่มีเหตุให้เก็บแล้ว — ผูก/ปิดแล้วแต่ลบไม่สำเร็จ · ไม่ได้ผูกนานเกิน <see cref="LineChatSlipRule.UnmatchedRetentionDays"/> วัน (ปิดแถวพร้อมเหตุ) ·
    /// ทุกบริษัท (งานเบื้องหลังใต้ <c>JobLock</c>) · ลบไฟล์ไม่ได้ ⇒ ข้ามแถวนั้น (รอบหน้าลองใหม่) · คืนจำนวนรูปที่ลบ</summary>
    public static async Task<int> PurgeExpiredAsync(AccountingDbContext db, Accounting.Services.Storage.IFileStorage storage, ILogger logger, DateTime nowUtc,
        CancellationToken ct = default)
    {
        var closed = new[] { LineChatSlipRule.Statuses.Submitted, LineChatSlipRule.Statuses.Assigned, LineChatSlipRule.Statuses.Dismissed };
        var cutoff = nowUtc.AddDays(-LineChatSlipRule.UnmatchedRetentionDays);
        var rows = await db.LineChatSlips.IgnoreQueryFilters()
            .Where(x => x.FileDeletedAt == null && (closed.Contains(x.Status) || x.CreatedAt < cutoff))
            .OrderBy(x => x.CreatedAt).Take(500).ToListAsync(ct);
        var purged = 0;
        foreach (var r in rows)
        {
            try { await storage.DeleteAsync(r.StoragePath, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "ลบรูปสลิปจากแชตที่พ้นระยะเก็บไม่สำเร็จ (สลิป {SlipId})", r.Id);
                continue;
            }
            r.FileDeletedAt = nowUtc;
            // ฝ่ายค้าน 264-P2 L2: ผลอ่าน (ธนาคาร + เลขท้ายของผู้โอน/ผู้รับ) ไม่เก็บเกินรูป — แถวที่แจ้งโอนแล้วคัดลอกไป intent แล้ว (ฐานเก็บตามเอกสาร)
            r.SlipReadJson = null;
            if (!closed.Contains(r.Status))
            {
                r.Status = LineChatSlipRule.Statuses.Dismissed;
                r.ReasonDetail = $"พ้นระยะเก็บรูป {LineChatSlipRule.UnmatchedRetentionDays} วันโดยไม่ได้ผูกเอกสาร — ระบบลบรูปแล้ว";
                r.ResolvedAt = nowUtc;
                r.ResolvedBy = ActorRetention;
            }
            r.UpdatedAt = nowUtc;
            purged++;
        }
        if (purged > 0) await db.SaveChangesAsync(ct);
        return purged;
    }
}
