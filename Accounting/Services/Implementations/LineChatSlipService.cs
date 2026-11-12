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
    /// <summary>รูปในแชต (webhook ที่ลายเซ็นผ่าน) ⇒ message ที่ตอบกลับ · null = ไม่ใช่งานของเส้นนี้ (ปิดสวิตช์ · กลุ่มที่ไม่ได้ผูก/ไม่แสดงยอด · ไม่ใช่รูป) — ผู้เรียกใช้คำตอบเดิม</summary>
    Task<IReadOnlyList<object>?> HandleImageAsync(Guid companyId, string? destination, LineChatSource source, string messageId, CancellationToken ct = default);

    /// <summary>ปุ่มเลือกใบ (postback) ⇒ message ที่ตอบกลับ · null = postback ไม่ใช่ของเส้นนี้</summary>
    Task<IReadOnlyList<object>?> HandlePostbackAsync(Guid companyId, string? destination, LineChatSource source, string? data, CancellationToken ct = default);

    Task<LineChatSlipListResponse> ListOpenAsync(Guid companyId, CancellationToken ct = default);
    Task<int> OpenCountAsync(Guid companyId, CancellationToken ct = default);
    Task<LineChatSlipAssignResult> AssignAsync(Guid companyId, Guid slipId, AssignLineChatSlipRequest request, string actor, CancellationToken ct = default);
    Task DismissAsync(Guid companyId, Guid slipId, string? reason, string actor, CancellationToken ct = default);
    Task<(byte[] Bytes, string ContentType)?> ReadFileAsync(Guid companyId, Guid slipId, CancellationToken ct = default);
}

/// <summary>
/// **ผู้เขียน <see cref="LineChatSlip"/> ตัวเดียว** — รูปในแชต ⇒ อ่านยอด (ในเครื่อง · ไม่มี AI) ⇒ ตัวตัดสินเดียว <see cref="LineChatSlipRule.Decide"/> ⇒
/// ใบเดียว: แจ้งโอนผ่านประตูเดิม <see cref="SlipEvidenceService.SubmitAsync"/> (รายการรอยืนยันในคิวเดียว · ระบบไม่ปิดหนี้เอง) ·
/// หลายใบ: ปุ่มเลือก (postback ลงลายเซ็น + ตรวจซ้ำกับฐาน) · ไม่ตรง/อ่านไม่ได้/ไม่ได้ผูก: คิวพนักงาน (หน้ารายการรับชำระออนไลน์) พร้อมเหตุ
/// <para>ยอดสุทธิหลังหัก ณ ที่จ่าย = ตัวคิดเดียวกับหน้าชำระ/พอร์ทัล (<see cref="IDocumentPayLinkService.DueAmountsForDocumentAsync"/>) · ทุก query กรองบริษัท</para>
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

    public LineChatSlipService(AccountingDbContext db, ISecretProtector secrets, ICustomerLineLinkService links, ISlipImageReader reader,
        IDocumentPayLinkService payLinks, SlipEvidenceService slips, Accounting.Services.Storage.IFileStorage storage,
        ILogger<LineChatSlipService> logger, INotificationEngine? notify = null)
    {
        _db = db; _secrets = secrets; _links = links; _reader = reader; _payLinks = payLinks; _slips = slips; _storage = storage; _logger = logger; _notify = notify;
    }

    public const string ActorAuto = "line-chat:auto";
    public const string ActorCustomer = "line-chat:customer";

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

        var bytes = await _links.DownloadMessageContentAsync(companyId, messageId, LineChatSlipRule.MaxBytes, ct);
        if (bytes == null || bytes.Length == 0) return One(LineChatSlipRule.Replies.DownloadFailed);
        var sniffed = SlipEvidenceRule.DetectPublicType(bytes);
        var ext = SlipEvidenceRule.ExtensionFor(sniffed);
        if (sniffed == null || ext == null) return null;

        var sha = SlipEvidenceRule.Sha256Hex(bytes);
        if (await IsDuplicateAsync(companyId, sha, null, ct)) return One(LineChatSlipRule.Replies.Duplicate);
        var reading = await _reader.ReadAsync(bytes, sniffed, ct);
        var slipRef = SlipEvidenceRule.NormalizeRef(reading.Ref);
        if (slipRef != null && await IsDuplicateAsync(companyId, null, slipRef, ct)) return One(LineChatSlipRule.Replies.Duplicate);

        // เก็บไฟล์ผ่านที่เก็บกลาง (Local/S3) ในโฟลเดอร์ที่<b>ไม่เสิร์ฟตรง</b> (สลิป = ข้อมูลส่วนบุคคล · พนักงานเปิดผ่าน endpoint ที่ตรวจสิทธิ์)
        var now = DateTime.UtcNow;
        var absPath = Path.Combine(_storage.Locator.RelativeBase, "wwwroot", "uploads", "line-chat-slips", now.ToString("yyyy-MM"), $"{Guid.NewGuid():N}{ext}");
        await _storage.WriteAsync(absPath, bytes, sniffed, ct);

        var row = new LineChatSlip
        {
            CompanyId = companyId, SourceKind = source.Kind, SourceKey = sourceKey, ContactId = contactIds.Count == 1 ? contactIds[0] : (Guid?)null,
            LineMessageId = messageId.Length > 64 ? messageId[..64] : messageId,
            FileSha256 = sha, StoragePath = absPath, ContentType = sniffed,
            OcrAmount = reading.Amount, OcrAmounts = LineChatSlipRule.AmountsText(reading.Amounts), OcrDate = reading.Date, OcrRef = slipRef,
            ShowAmounts = showAmounts, CreatedBy = "line-webhook",
        };

        var candidates = contactIds.Count == 0 ? new List<LineChatSlipRule.Candidate>() : await CandidatesAsync(companyId, contactIds, ct);
        var decision = LineChatSlipRule.Decide(reading, candidates, contactIds);

        if (decision.Outcome == LineChatSlipRule.Outcome.One)
        {
            var m = decision.Matches[0];
            var (submitted, problem) = await TrySubmitAsync(companyId, row, m.Doc.DocumentId, m.Doc.ContactId, bytes, m.Amount, ActorAuto, ct);
            if (submitted)
                return One(LineChatSlipRule.Replies.Submitted(m.Doc.Number, m.Amount, showAmounts));
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
            var items = decision.Matches.Select(x => (object)new
            {
                type = "action",
                action = new
                {
                    type = "postback", label = LineChatSlipRule.Replies.ChoiceLabel(x.Doc.Number),
                    data = LineChatSlipRule.PostbackData(key, companyId, sourceKey, row.Id, x.Doc.DocumentId),
                    displayText = LineChatSlipRule.Replies.ChoiceDisplay(x.Doc.Number, x.Doc.Date, x.Amount, showAmounts),
                },
            }).Append((object)new
            {
                type = "action",
                action = new
                {
                    type = "postback", label = LineChatSlipRule.Replies.DeclineLabel,
                    data = LineChatSlipRule.PostbackData(key, companyId, sourceKey, row.Id, null),
                    displayText = LineChatSlipRule.Replies.DeclineLabel,
                },
            }).ToList();
            var text = decision.Matches.Count == 1 ? LineChatSlipRule.Replies.ChooseOne
                : LineChatSlipRule.Replies.Choose(reading.Amount, showAmounts, decision.Matches.Count);
            return new[] { Text(text, items) };
        }
        await ToStaffAsync(row, decision.Reason, null, isNew: true, ct);
        return One(LineChatSlipRule.Replies.StaffWillCheck);
    }

    /// <summary>สลิปนี้ส่งเข้ามาแล้วไหม — แถวแชตที่ยังเปิด + รายการรอยืนยัน/รับเงินแล้ว (ด่านเดียวกับพอร์ทัล) · ทั้งบริษัท</summary>
    private async Task<bool> IsDuplicateAsync(Guid companyId, string? sha, string? slipRef, CancellationToken ct)
    {
        var live = LineChatSlipRule.Statuses.Live;
        var dupStatuses = SlipEvidenceRule.DuplicateStatuses;
        if (sha != null)
            return await _db.LineChatSlips.AsNoTracking().AnyAsync(x => x.CompanyId == companyId && x.FileSha256 == sha && live.Contains(x.Status), ct)
                || await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.CompanyId == companyId && i.SlipFileSha256 == sha && dupStatuses.Contains(i.Status), ct);
        return await _db.LineChatSlips.AsNoTracking().AnyAsync(x => x.CompanyId == companyId && x.OcrRef == slipRef && live.Contains(x.Status), ct)
            || await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.CompanyId == companyId && i.SlipBankRef == slipRef && dupStatuses.Contains(i.Status), ct);
    }

    /// <summary>เอกสารค้างชำระของผู้ติดต่อที่ผูก LINE (บริษัทเดียวกัน) + ยอดสุทธิจากตัวคิดของหน้าชำระ — ใบที่รับชำระทางนี้ไม่ได้ (IssueRefusal) ไม่เป็นผู้สมัคร</summary>
    private async Task<List<LineChatSlipRule.Candidate>> CandidatesAsync(Guid companyId, IReadOnlyCollection<Guid> contactIds, CancellationToken ct)
    {
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && contactIds.Contains(d.ContactId) && d.BalanceDue > 0.009m
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Rejected && d.Status != DocumentStatus.Paid)
            .OrderBy(d => d.DocumentDate).ThenBy(d => d.DocumentNumber).Take(60)
            .Select(d => new { d.Id, d.ContactId, d.DocumentNumber, d.DocumentDate, d.Status, d.BalanceDue })
            .ToListAsync(ct);
        var list = new List<LineChatSlipRule.Candidate>();
        foreach (var d in docs)
        {
            var due = await _payLinks.DueAmountsForDocumentAsync(companyId, d.Id, ct);
            if (due == null || due.Refusal != null) continue;
            list.Add(new LineChatSlipRule.Candidate(d.Id, d.ContactId, d.DocumentNumber, d.DocumentDate, d.Status, d.BalanceDue, due.Net, due.Gross));
        }
        return list;
    }

    /// <summary>แจ้งโอนผ่านประตูเดิม · สำเร็จ ⇒ แถวผูกใบ (Submitted/Assigned) และบันทึก · ไม่สำเร็จ ⇒ (false, เหตุ) — ผู้เรียกส่งเข้าคิวพนักงาน/แจ้งพนักงาน</summary>
    private async Task<(bool Ok, string? Problem)> TrySubmitAsync(Guid companyId, LineChatSlip row, Guid documentId, Guid? contactId, byte[] bytes,
        decimal amount, string actor, CancellationToken ct, string doneStatus = LineChatSlipRule.Statuses.Submitted)
    {
        try
        {
            var r = await _slips.SubmitAsync(companyId, documentId, contactId,
                new SlipFile(bytes, row.ContentType, "line-slip" + (SlipEvidenceRule.ExtensionFor(row.ContentType) ?? "")),
                amount, row.OcrRef, SlipEvidenceService.ChannelLineChat, ct);
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
        catch (Exception ex) when (ex is BusinessRuleException or KeyNotFoundException or ArgumentException)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>เข้าคิวพนักงานพร้อมเหตุ + แจ้งทีม</summary>
    private async Task ToStaffAsync(LineChatSlip row, string? reason, string? detail, bool isNew, CancellationToken ct)
    {
        row.Status = LineChatSlipRule.Statuses.Unmatched;
        row.Reason = reason;
        row.ReasonDetail = detail == null ? null : LineMessageLimits.Truncate(detail, 500);
        row.UpdatedAt = DateTime.UtcNow;
        if (isNew && _db.Entry(row).State == EntityState.Detached) _db.LineChatSlips.Add(row);
        await _db.SaveChangesAsync(ct);
        if (_notify != null)
        {
            var (title, message) = LineChatSlipRule.StaffNotice(reason, row.OcrAmount);
            await _notify.DispatchAsync(row.CompanyId, Accounting.Models.Constants.NotificationEvents.AutopilotSlipSubmitted, new NotificationContext
            {
                Title = title, Message = message, ActionUrl = AutopilotService.SlipQueueHref, EntityType = LineChatSlipRule.AuditEntityType, EntityId = row.Id,
            });
        }
    }

    // ═══════════════ ปุ่มเลือกใบ ═══════════════

    public async Task<IReadOnlyList<object>?> HandlePostbackAsync(Guid companyId, string? destination, LineChatSource source, string? data, CancellationToken ct = default)
    {
        if (!LineChatSlipRule.IsOurs(data)) return null;
        var pb = LineChatSlipRule.TryParse(data);
        var (_, key) = await SettingsAsync(companyId, ct);
        if (pb == null || key == null || string.IsNullOrWhiteSpace(source.Id)) return One(LineChatSlipRule.Replies.ChoiceInvalid);
        var sourceKey = SourceKey(destination, source);
        var row = await _db.LineChatSlips.FirstOrDefaultAsync(x => x.Id == pb.SlipId && x.CompanyId == companyId, ct);
        // ลายเซ็น (บริษัท + แหล่งแชต + สลิป + ใบ) + แถวต้องเป็นของแหล่งแชตเดียวกัน — ไม่ผ่าน = ข้อความเดียว (ไม่บอกว่าผิดตรงไหน)
        if (row == null || row.SourceKey != sourceKey || !LineChatSlipRule.Verify(key, companyId, sourceKey, pb.SlipId, pb.DocumentId, pb.Signature))
        {
            _logger.LogWarning("LINE postback สลิปของบริษัท {CompanyId}: ลายเซ็น/แหล่งแชตไม่ตรง (สลิป {SlipId})", companyId, pb.SlipId);
            return One(LineChatSlipRule.Replies.ChoiceInvalid);
        }
        var now = DateTime.UtcNow;
        if (LineChatSlipRule.ChoiceProblem(row.Status, row.ChoiceExpiresAt, now) != null)
        {
            if (row.Status == LineChatSlipRule.Statuses.AwaitingChoice)
                await ToStaffAsync(row, LineChatSlipRule.Reasons.ChoiceExpired, null, isNew: false, ct);
            return One(LineChatSlipRule.Replies.ChoiceInvalid);
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
        // ค่าในฐาน = Submitting (ClaimAsync) · แถวในหน่วยความจำคงค่าที่โหลด — สถานะปลายทาง (Submitted/Unmatched) เขียนทับด้วย SaveChanges
        var bytes = await _storage.ReadAllBytesAsync(row.StoragePath, ct);
        if (bytes == null)
        {
            await ToStaffAsync(row, LineChatSlipRule.Reasons.SubmitFailed, "ไม่พบไฟล์สลิปในที่เก็บ", isNew: false, ct);
            return One(LineChatSlipRule.Replies.StaffWillCheck);
        }
        var (ok, problem) = await TrySubmitAsync(companyId, row, doc.Id, doc.ContactId, bytes, offered.a, ActorCustomer, ct);
        if (ok) return One(LineChatSlipRule.Replies.Submitted(doc.DocumentNumber, offered.a, row.ShowAmounts));
        await ToStaffAsync(row, LineChatSlipRule.Reasons.SubmitFailed, problem, isNew: false, ct);
        return One(LineChatSlipRule.Replies.StaffWillCheck);
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

    // ═══════════════ คิวพนักงาน ═══════════════

    /// <summary>ตัวเลือกที่หมดอายุ ⇒ คิวพนักงาน (เหตุ "ลูกค้าไม่ได้เลือก") — คำสั่งเดียว · ผู้อ่านคิวเรียกก่อนนับ/แสดง</summary>
    private Task<int> ExpireChoicesAsync(Guid companyId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return _db.LineChatSlips
            .Where(x => x.CompanyId == companyId && x.Status == LineChatSlipRule.Statuses.AwaitingChoice && x.ChoiceExpiresAt != null && x.ChoiceExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, LineChatSlipRule.Statuses.Unmatched)
                .SetProperty(x => x.Reason, LineChatSlipRule.Reasons.ChoiceExpired).SetProperty(x => x.UpdatedAt, (DateTime?)now), ct);
    }

    public async Task<int> OpenCountAsync(Guid companyId, CancellationToken ct = default)
    {
        await ExpireChoicesAsync(companyId, ct);
        return await _db.LineChatSlips.AsNoTracking().CountAsync(x => x.CompanyId == companyId && x.Status == LineChatSlipRule.Statuses.Unmatched, ct);
    }

    public async Task<LineChatSlipListResponse> ListOpenAsync(Guid companyId, CancellationToken ct = default)
    {
        await ExpireChoicesAsync(companyId, ct);
        var rows = await _db.LineChatSlips.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.Status == LineChatSlipRule.Statuses.Unmatched)
            .OrderBy(x => x.CreatedAt).Take(100).ToListAsync(ct);
        var contactIds = rows.Where(r => r.ContactId != null).Select(r => r.ContactId!.Value).Distinct().ToList();
        var names = contactIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Contacts.AsNoTracking().Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var optionsByContact = new Dictionary<Guid, List<LineChatSlipRule.Candidate>>();
        foreach (var cid in contactIds) optionsByContact[cid] = await CandidatesAsync(companyId, new[] { cid }, ct);
        var items = rows.Select(r =>
        {
            var opts = r.ContactId is Guid c && optionsByContact.TryGetValue(c, out var list) ? list : new List<LineChatSlipRule.Candidate>();
            return new LineChatSlipRow(r.Id, r.CreatedAt, r.Status, r.SourceKind == LineChatSource.KindGroup ? "กลุ่ม LINE ของผู้ติดต่อ" : "แชต LINE 1:1",
                r.ContactId, r.ContactId is Guid n && names.TryGetValue(n, out var name) ? name : null,
                LineChatSlipRule.Reasons.Label(r.Reason), r.ReasonDetail, r.OcrAmount, r.OcrAmounts, r.OcrDate, r.OcrRef,
                opts.Take(LineChatSlipRule.MaxChoices).Select(o => new LineChatSlipDocOption(o.DocumentId, o.Number, o.Date, o.NetDue, o.GrossDue,
                    r.OcrAmount is decimal a && a == Math.Round(o.NetDue, 2, MidpointRounding.AwayFromZero))).ToList());
        }).ToList();
        return new LineChatSlipListResponse(items.Count, items);
    }

    public async Task<LineChatSlipAssignResult> AssignAsync(Guid companyId, Guid slipId, AssignLineChatSlipRequest request, string actor, CancellationToken ct = default)
    {
        var row = await _db.LineChatSlips.FirstOrDefaultAsync(x => x.Id == slipId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบสลิป");
        if (!LineChatSlipRule.Statuses.Assignable.Contains(row.Status))
            throw new BusinessRuleException("สลิปนี้ผูกเอกสารหรือปิดไปแล้ว — โหลดหน้าใหม่", LineChatSlipRule.RuleCode, 409);
        var number = request.DocumentNumber?.Trim();
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && (request.DocumentId != null ? d.Id == request.DocumentId : (number != null && number != "" && d.DocumentNumber == number)))
            .Select(d => new { d.Id, d.DocumentNumber }).FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("ไม่พบเอกสารนี้ในบริษัท — ตรวจเลขที่เอกสาร", LineChatSlipRule.RuleCode, 404);
        var due = await _payLinks.DueAmountsForDocumentAsync(companyId, doc.Id, ct);
        if (due?.Refusal is string refusal) throw new BusinessRuleException(refusal, LineChatSlipRule.RuleCode, 400);
        var amount = Math.Round(request.Amount ?? row.OcrAmount ?? due?.Net ?? 0m, 2, MidpointRounding.AwayFromZero);
        var previous = row.Status;
        if (!await ClaimAsync(companyId, row.Id, LineChatSlipRule.Statuses.Assignable, ct))
            throw new BusinessRuleException("สลิปนี้กำลังถูกผูกโดยรายการอื่น — โหลดหน้าใหม่", LineChatSlipRule.RuleCode, 409);
        var bytes = await _storage.ReadAllBytesAsync(row.StoragePath, ct);
        string? problem = "ไม่พบไฟล์สลิปในที่เก็บ";
        var ok = false;
        if (bytes != null) (ok, problem) = await TrySubmitAsync(companyId, row, doc.Id, null, bytes, amount, actor, ct, LineChatSlipRule.Statuses.Assigned);
        if (!ok)
        {
            // คืนสถานะเดิม (คิวพนักงาน) แล้วบอกเหตุจริงจากประตูแจ้งโอน
            await _db.LineChatSlips.Where(x => x.Id == row.Id && x.CompanyId == companyId && x.Status == LineChatSlipRule.Statuses.Submitting)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, previous), ct);
            throw new BusinessRuleException(problem ?? "ผูกสลิปไม่สำเร็จ", LineChatSlipRule.RuleCode, 409);
        }
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = actor, Action = AuditAction.Update, EntityType = LineChatSlipRule.AuditEntityType, EntityId = row.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new { op = "assign", documentId = doc.Id, doc.DocumentNumber, amount, paymentIntentId = row.PaymentIntentId, rule = LineChatSlipRule.RuleCode }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return new LineChatSlipAssignResult(row.Id, doc.Id, doc.DocumentNumber, row.PaymentIntentId!.Value, amount);
    }

    public async Task DismissAsync(Guid companyId, Guid slipId, string? reason, string actor, CancellationToken ct = default)
    {
        var why = (reason ?? "").Trim();
        if (why.Length == 0) throw new BusinessRuleException("กรุณาระบุเหตุที่ปิดรายการ (เช่น ไม่ใช่สลิป · บันทึกเป็นเงินรอจัดสรรจากรายการเดินบัญชีแล้ว)", LineChatSlipRule.RuleCode, 400);
        var row = await _db.LineChatSlips.FirstOrDefaultAsync(x => x.Id == slipId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบสลิป");
        if (!LineChatSlipRule.Statuses.Assignable.Contains(row.Status))
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
    }

    public async Task<(byte[] Bytes, string ContentType)?> ReadFileAsync(Guid companyId, Guid slipId, CancellationToken ct = default)
    {
        var row = await _db.LineChatSlips.AsNoTracking().Where(x => x.Id == slipId && x.CompanyId == companyId)
            .Select(x => new { x.StoragePath, x.ContentType }).FirstOrDefaultAsync(ct);
        if (row == null) return null;
        var bytes = await _storage.ReadAllBytesAsync(row.StoragePath, ct);
        return bytes == null ? null : (bytes, row.ContentType);
    }
}
