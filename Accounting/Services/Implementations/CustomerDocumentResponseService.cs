using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 267 แพ็ก P2 — <b>ผู้เขียนเดียวของ "ลูกค้าตอบรับเอกสาร"</b> (ยอมรับ · ไม่รับ · ขอแก้ใบเสนอราคา · เซ็นรับสินค้า) ·
/// คำตัดสิน 267-1-4…267-1-6 · 267-1-11 · 267-1-33… · DOCUMENT_FLOW §2.12n-P2
///
/// <para>แกนของ <c>PublicQuotationController.Accept</c>/<c>SignDelivery</c> (รอบ 239/240/267 P1) ย้ายมาที่นี่ทั้งชุด — controller ทั้งสองตัว
/// (หน้าเอกสาร <c>PublicDocumentPageController.Respond</c> และหน้าตอบรับรุ่นก่อน) เรียก service นี้ ไม่มีตรรกะของตัวเอง ·
/// ทุกคำตอบ = <b>ธุรกรรมเดียว</b>: จองภาพฉายบนใบแบบอะตอมมิก → แถวหลักฐาน <see cref="CustomerDocumentAction"/> (append-only) →
/// audit chain → เหตุการณ์ outbox · หลัง commit: สร้างฉบับที่ลูกค้าเซ็น (ไฟล์ + SHA-256) ทันทีถ้าทำได้ ไม่ได้ = งาน outbox ลองใหม่</para>
///
/// <para>ลิงก์หน้าเอกสาร (token ของใบ) ⇒ ผู้ลงนามครบ (ชื่อ · นามสกุล · ตำแหน่ง · ติ๊กรับรอง — <see cref="CustomerSignRule.DecideSigner"/>) และปุ่มนั้นต้องเปิดอยู่
/// ตามตัวตัดสินเดียวของหน้า (<see cref="CustomerDocPage.Actions"/>) · ลิงก์ยอมรับ/เซ็นรับที่ออกก่อนรอบ 267 ⇒ กติกาเดิม (พิมพ์ชื่อ) จนหมดอายุ
/// แต่ลงแถวหลักฐานด้วย (ระดับตัวตน NameOnly — ไม่แต่งช่องที่ไม่ได้เก็บ)</para>
/// </summary>
public class CustomerDocumentResponseService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CustomerDocumentResponseService> _logger;
    /// <summary>รอบ 209 P3 (E2): ยอมรับแล้ว ⇒ lead ที่ผูกใบ = ได้งาน + แจ้งร้าน · null = ไม่ทำขั้นนี้</summary>
    private readonly CmsLeadService? _leads;
    private readonly IPdfGenerationService? _pdf;
    private readonly IFileStorage? _storage;
    private readonly IChatRateLimiter? _rate;
    private readonly IEmailService? _email;
    private readonly INotificationEngine? _notify;
    private Caller _caller = new(null, null);

    public CustomerDocumentResponseService(AccountingDbContext db, ILogger<CustomerDocumentResponseService> logger,
        CmsLeadService? leads = null, IPdfGenerationService? pdf = null, IFileStorage? storage = null, IChatRateLimiter? rate = null,
        IEmailService? email = null, INotificationEngine? notify = null)
    {
        _db = db;
        _logger = logger;
        _leads = leads;
        _pdf = pdf;
        _storage = storage;
        _rate = rate;
        _email = email;
        _notify = notify;
    }

    /// <summary>ผู้เรียกจากภายนอก (หลักฐาน ม.9/ม.11: IP + ลายนิ้วมือของเบราว์เซอร์)</summary>
    public sealed record Caller(string? Ip, string? UserAgent);

    /// <summary>ผลของคำขอ — controller แปลงเป็น HTTP ตรงตัว (ไม่ตัดสินเพิ่ม)</summary>
    public sealed record Outcome(int Status, object? Data, string? Message)
    {
        public bool Success => Status == 200;
    }

    /// <summary>คำตอบจากหน้าเอกสาร (<c>POST …/documents/{token}/respond</c>) — <paramref name="Action"/> = รหัสปุ่มจาก <see cref="CustomerDocPage.Actions"/></summary>
    public sealed record RespondRequest(string? Action, CustomerSignRule.SignerInput? Signer = null, string? SignatureBase64 = null,
        string? Note = null, string? ReasonCode = null, string? Message = null, int? RevisionNumber = null, string? ContentHash = null, string? OtpCode = null);

    public IFileStorage SignatureStore => _storage ?? FileStorageDefaults.Local;

    private static Outcome Ok(object data, string? message) => new(200, data, message);
    private static Outcome NotFound(string message = "ลิงก์ไม่ถูกต้องหรือหมดอายุ") => new(404, null, message);
    private static Outcome BadRequest(string message) => new(400, null, message);
    private static Outcome Conflict(string message) => new(409, null, message);
    private static Outcome TooMany() => new(429, null, "เรียกถี่เกินไป — กรุณารอสักครู่แล้วลองใหม่");

    /// <summary>เพดานต่อ IP ของเส้นสาธารณะ (ตัวนับร่วมข้าม instance) · true = เกินเพดาน</summary>
    public async Task<bool> OverRateAsync(string bucket, int perMinute, int perDay)
    {
        if (_rate == null) return false;
        var ip = string.IsNullOrWhiteSpace(_caller.Ip) ? "unknown" : _caller.Ip;
        return !await _rate.TryConsumeAsync($"qt-public:{bucket}:{ip}", perMinute, perDay);
    }

    public static object AcceptedState(Document doc) => new
    {
        acceptedAt = doc.QuotationAcceptedAt,
        acceptedBy = doc.QuotationAcceptedBy,
        acceptedNote = doc.QuotationAcceptNote,
        hasSignature = !string.IsNullOrWhiteSpace(doc.QuotationAcceptSignaturePath),
    };

    // ═══════════════ เส้นเดียวของหน้าเอกสาร ═══════════════

    /// <summary>คำตอบจากหน้าเอกสาร — ส่งต่อให้ตัวเขียนของชนิดนั้น (token ต้องเป็นของบริษัทใน URL — tenant)</summary>
    public async Task<Outcome> RespondAsync(Guid companyId, string token, RespondRequest? request, Caller caller)
    {
        _caller = caller;
        if (request == null || string.IsNullOrWhiteSpace(request.Action)) return BadRequest("ไม่ได้ระบุรายการที่ต้องการทำ");
        if (!DocumentPayLinkPolicy.LooksValid(token)) return NotFound(CustomerDocPage.NotFoundMessage);
        var hash = DocumentPayLinkPolicy.TokenHash(token);
        var type = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.PayLinkTokenHash == hash && d.PayLinkRevokedAt == null && !d.IsDeleted)
            .Select(d => (DocumentType?)d.DocumentType).FirstOrDefaultAsync();
        if (type == null) return NotFound(CustomerDocPage.NotFoundMessage);
        var kind = CustomerSignRule.KindFor(request.Action.Trim(), type.Value);
        return kind switch
        {
            CustomerSignRule.Kind.Accepted => await Accept(token, new PublicQuotationController.AcceptRequest("", request.Note, request.SignatureBase64,
                request.RevisionNumber, request.ContentHash, request.Signer, request.OtpCode), caller),
            CustomerSignRule.Kind.DeliverySigned => await SignDelivery(token, new PublicQuotationController.DeliverySignRequest("", request.SignatureBase64 ?? "",
                request.Note, request.RevisionNumber, request.ContentHash, request.Signer), caller),
            CustomerSignRule.Kind.Declined or CustomerSignRule.Kind.ChangeRequested => await DeclineOrRequestChange(token, kind.Value, request, caller),
            _ => BadRequest("รายการนี้ทำกับเอกสารชนิดนี้ไม่ได้"),
        };
    }

    // ═══════════════ ยอมรับใบเสนอราคา ═══════════════

    public async Task<Outcome> Accept(string token, PublicQuotationController.AcceptRequest? request, Caller caller)
    {
        _caller = caller;
        if (await OverRateAsync("accept", 10, 60)) return TooMany();
        var doc = await FindByTokenAsync(token);
        if (doc == null) return NotFound();

        // ตัวตัดสินเดียว: ยอมรับแล้ว (idempotent) · ชื่อบังคับ · ลายเซ็นบังคับตามค่าตั้ง · ลายเซ็นที่ส่งมาต้องผ่านตัวตรวจ
        var requireSignature = await RequireSignatureAsync(doc.CompanyId);
        // รอบ 267 P1: เปิดด้วย token ของหน้าเอกสาร ⇒ ช่วงตอบรับจากตัวตัดสินเดียวกับปุ่มบนหน้าเอกสาร
        var viaPage = ViaPageToken(doc.QuotationAcceptToken, token);
        var en = viaPage && await IsEnglishAsync(doc);
        var buyer = BuyerOf(doc);
        // รอบ 267 P2: ลิงก์หน้าเอกสาร ⇒ ผู้ลงนามครบ (ชื่อ · นามสกุล · ตำแหน่ง · ติ๊กรับรอง) · ยอดถึงเกณฑ์ของบริษัท ⇒ ยืนยันอีเมลด้วยรหัส (267-1-6)
        var otpRequired = viaPage && CustomerSignRule.OtpRequired(await OtpMinAmountAsync(doc.CompanyId), doc.TotalAmount, CustomerSignRule.Kind.Accepted);
        CustomerSignRule.Signer? signer = null;
        string? signerError = null;
        if (viaPage && doc.QuotationAcceptedAt == null)
            (signer, signerError) = CustomerSignRule.DecideSigner(request?.Signer, buyer.IsJuristic, otpRequired, en);
        var decision = QuotationAcceptance.DecideAccept(doc.QuotationAcceptedAt, viaPage ? signer?.FullName ?? "-" : request?.AcceptedBy,
            request?.SignatureBase64, requireSignature);
        if (decision.Outcome == QuotationAcceptance.Outcome.AlreadyAccepted)
            return Ok(AcceptedState(doc), decision.Message);
        // ฝ่ายค้าน 267 L3: ช่วงตอบรับตรวจทั้งสองรุ่นของลิงก์ (รุ่นก่อน = เฉพาะวันยืนราคา — วันหมดอายุของตัวเองตรวจใน FindByTokenAsync แล้ว)
        if (RespondClosedReason(doc, viaPage) is { } closed)
            return Conflict(closed);
        // รอบ 267 P2: ปุ่มนี้ต้องเปิดอยู่ตามตัวตัดสินเดียวของหน้า (ตอบไปแล้ว · ลิงก์ชำระรุ่นก่อน · ใบยกเลิก = ปิด)
        if (viaPage && PageActionClosedReason(doc, CustomerDocPage.ActionAccept, en) is { } pageClosed)
            return Conflict(pageClosed);
        // hash เนื้อหาที่ลูกค้าเห็นตอนยอมรับ — สูตรเดียวกับลายเซ็นลูกค้าทุกเส้น (ไม่ต่อข้อความลง Notes อีก ⇒ เนื้อหาไม่เปลี่ยนหลังยอมรับ)
        var contentHash = DocumentSignedContent.Hash(doc, doc.Lines);
        // รอบ 267 ฝ่ายค้าน H1: ยอมรับได้เฉพาะฉบับที่ลูกค้าเห็น — เลข Rev (+ hash เนื้อหา) ที่หน้าส่งมาต้องตรงกับใบ ณ ตอนนี้ · ใส่เลข Rev ในเงื่อนไขจองด้วย ·
        // ตรวจก่อนด่านช่องกรอก: เอกสารถูกแก้ ⇒ บอกให้เปิดฉบับล่าสุด (ไม่ให้ผู้ลงนามแก้ฟอร์มของฉบับเก่าต่อ)
        var seenRevision = request?.RevisionNumber;
        if (CustomerDocPage.RespondStaleReason(viaPage, seenRevision, doc.RevisionNumber, request?.ContentHash, contentHash) is { } stale)
            return Conflict(stale);
        if (signerError != null) return BadRequest(signerError);
        if (!decision.Accept)
            return BadRequest(decision.Message);

        var now = DateTime.UtcNow;
        var name = viaPage ? signer!.FullName : QuotationAcceptance.CleanText(request!.AcceptedBy, QuotationAcceptance.NameMax)!;
        var note = QuotationAcceptance.CleanText(request!.Note, QuotationAcceptance.NoteMax);
        // 267-1-6: รหัสยืนยันอีเมล (เฉพาะเมื่อบริษัทตั้งเกณฑ์และยอดถึง) — ผิด/หมดอายุ = ปฏิเสธก่อนเขียนอะไร
        Guid? otpId = null;
        if (otpRequired)
        {
            var (id, otpError) = await CheckOtpAsync(doc, signer!.Email!, request.OtpCode, now, en);
            if (otpError != null) return BadRequest(otpError);
            otpId = id;
        }
        var ip = caller.Ip;
        var uaHash = QuotationAcceptance.Sha256Hex(caller.UserAgent);
        var tokenHash = QuotationAcceptance.Sha256Hex(token.ToLowerInvariant());
        var pageHash = DocumentPayLinkPolicy.TokenHash(token);

        // ลายเซ็น: เขียนไบต์ที่ผ่านตัวตรวจแล้วลงที่เก็บกลางก่อน (ไม่ใช่ข้อความดิบจากคำขอ) — ยอมรับไม่สำเร็จ ⇒ ลบทิ้ง
        string? sigPath = null;
        string? sigSha = null;
        if (decision.Signature?.Png is { } png)
        {
            sigPath = QuotationAcceptance.SignatureLocation(SignatureStore.Locator.AttachmentsRoot, doc.CompanyId, doc.Id, Guid.NewGuid());
            await SignatureStore.WriteAsync(sigPath, png, "image/png");
            sigSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant();
        }

        var evidence = NewEvidence(doc, CustomerSignRule.Kind.Accepted, viaPage, signer, name, buyer, en, now, contentHash, tokenHash!, ip, uaHash);
        evidence.SignatureFilePath = sigPath;
        evidence.SignatureSha256 = sigSha;
        evidence.Message = note;
        evidence.EmailVerified = otpId != null;
        var accepted = nameof(CustomerSignRule.Kind.Accepted);

        // จองการยอมรับแบบอะตอมมิก (เฉพาะแถวที่ยังไม่ถูกยอมรับ + token เดิม) + แถวหลักฐาน + audit chain ในธุรกรรมเดียว — กดพร้อมกันสองเครื่อง = ยอมรับครั้งเดียว
        var claimed = 0;
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            claimed = await _db.Documents
                .Where(d => d.Id == doc.Id && d.CompanyId == doc.CompanyId
                    && d.QuotationAcceptedAt == null && d.QuotationAcceptToken == doc.QuotationAcceptToken
                    && (seenRevision == null || d.RevisionNumber == seenRevision)
                    // 267-1-11 หนึ่งคำตอบต่อ Rev: ลูกค้าแจ้งไม่รับ/ขอแก้ไประหว่างนี้ = ไม่จอง
                    && (!viaPage || d.CustomerResponse == null)
                    // รอบ 267: token ของหน้าเอกสาร ⇒ ลิงก์ต้องยังเป็น token นี้และไม่ถูกเพิกถอน ณ ตอนจอง (เพิกถอนระหว่างทาง = ไม่จอง)
                    && (!viaPage || (d.PayLinkTokenHash == pageHash && d.PayLinkRevokedAt == null)))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(d => d.QuotationAcceptedAt, now)
                    .SetProperty(d => d.QuotationAcceptedBy, name)
                    .SetProperty(d => d.QuotationAcceptNote, note)
                    .SetProperty(d => d.QuotationAcceptSignaturePath, sigPath)
                    .SetProperty(d => d.QuotationAcceptContentHash, contentHash)
                    .SetProperty(d => d.QuotationAcceptIp, ip)
                    .SetProperty(d => d.QuotationAcceptUserAgentHash, uaHash)
                    .SetProperty(d => d.QuotationAcceptTokenHash, tokenHash)
                    .SetProperty(d => d.CustomerResponse, accepted)
                    .SetProperty(d => d.CustomerResponseAt, now)
                    .SetProperty(d => d.UpdatedAt, now));
            // รหัสยืนยันใช้ได้ครั้งเดียว — ถูกใช้ไปแล้วระหว่างนี้ = ยกเลิกการจอง (ธุรกรรมย้อนกลับเมื่อออกจากบล็อก)
            if (claimed == 1 && otpId != null && await _db.CustomerSignOtps
                    .Where(o => o.Id == otpId && o.CompanyId == doc.CompanyId && o.ConsumedAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(o => o.ConsumedAt, now)) == 0)
            {
                await tx.RollbackAsync();
                if (sigPath != null) await TryDeleteSignatureAsync(sigPath);
                return BadRequest(CustomerSignRule.OtpMessage(en, CustomerSignRule.OtpOutcome.Missing));
            }
            if (claimed == 1)
            {
                _db.CustomerDocumentActions.Add(evidence);
                await EnsureRopaAsync(doc.CompanyId);
                _db.AddChainedAuditLog(new Models.Entities.AuditLog
                {
                    CompanyId = doc.CompanyId, UserId = null, UserEmail = null, Action = AuditAction.Approve,
                    EntityType = nameof(Models.Entities.Document), EntityId = doc.Id.ToString(),
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new { quotationAcceptedAt = (DateTime?)null }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        @event = "QuotationAcceptedOnline", documentNumber = doc.DocumentNumber, revisionNumber = doc.RevisionNumber,
                        acceptedBy = name, acceptedAt = now, note, hasSignature = sigPath != null, signatureSha256 = sigSha,
                        contentHash, tokenSha256 = tokenHash, userAgentSha256 = uaHash,
                        customerActionId = evidence.Id, identityLevel = evidence.IdentityLevel, signerTitle = evidence.SignerTitle,
                        signerOrgName = evidence.SignerOrgName, attestationVersion = evidence.AttestationVersion,
                        attestationTextHash = evidence.AttestationTextHash, emailVerified = evidence.EmailVerified,
                        ruleCode = CustomerSignText.RuleCode, legalReference = CustomerSignText.LegalReference,
                    }),
                    IpAddress = ip, Timestamp = now,
                });
                // รอบ 240 (คำตัดสิน 240-3): เหตุการณ์ quotation.accepted ลง outbox ในธุรกรรมเดียวกัน — ส่งจริงในงานเบื้องหลัง
                // (callback ต่อเอกสาร · webhook ที่ลงทะเบียน · แจ้งผู้ขาย · ฉบับที่ลูกค้าเซ็น) ⇒ ปลายทางล่มไม่ทำให้การยอมรับล้ม/ช้า ·
                // กุญแจกันซ้ำผูกกับแถวหลักฐาน (token ของหน้าเอกสารไม่เปลี่ยนตอนออก Rev ⇒ ยอมรับ Rev ถัดไปต้องได้เหตุการณ์ใหม่)
                var eventRef = EventRef(tokenHash!, evidence.Id);
                SignEventOutbox.Enqueue(_db, doc, SignRequestKinds.QuotationAccept, now, name, sigPath != null, contentHash, eventRef).CustomerActionId = evidence.Id;
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
        }
        catch
        {
            if (sigPath != null) await TryDeleteSignatureAsync(sigPath);
            throw;
        }

        if (claimed == 0)
        {
            // อีกคำขอยอมรับไปก่อน (หรือลิงก์ถูกเพิกถอน/ลูกค้าตอบอย่างอื่นระหว่างนี้) — ไม่ทับ · ทิ้งไฟล์ที่เพิ่งเขียน · ตอบสถานะจริง
            if (sigPath != null) await TryDeleteSignatureAsync(sigPath);
            var fresh = await FindByTokenAsync(token);
            if (fresh == null) return NotFound();
            if (fresh.QuotationAcceptedAt == null) return Conflict(CustomerDocPage.RespondStaleMessage);
            return Ok(AcceptedState(fresh), "ใบเสนอราคานี้ถูกยอมรับไปแล้ว");
        }

        _logger.LogInformation("Quotation {DocNo} accepted online (signature: {HasSig}) (company {Cid})",
            doc.DocumentNumber, sigPath != null, doc.CompanyId);
        // รอบ 209 P3 (E2): lead ที่ผูกใบนี้ ⇒ ได้งาน + แจ้งร้าน (หลังบันทึกการยอมรับ · ไม่โยน — ล้มประทับบน lead/บันทึกระบบ)
        if (_leads != null)
            await _leads.OnQuotationAcceptedAsync(doc.CompanyId, doc.Id, doc.DocumentNumber, name, doc.TotalAmount);

        doc.QuotationAcceptedAt = now;
        doc.QuotationAcceptedBy = name;
        doc.QuotationAcceptNote = note;
        doc.QuotationAcceptSignaturePath = sigPath;
        var signedCopyReady = await TrySnapshotNowAsync(evidence);
        return Ok(new
        {
            acceptedAt = (DateTime?)now, acceptedBy = name, acceptedNote = note, hasSignature = sigPath != null,
            actionId = evidence.Id, signedCopyReady,
        }, viaPage ? CustomerSignText.Done(en, CustomerSignRule.Kind.Accepted) : decision.Message);
    }

    // ═══════════════ ไม่รับ / ขอแก้ใบเสนอราคา (เฉพาะลิงก์หน้าเอกสาร) ═══════════════

    public async Task<Outcome> DeclineOrRequestChange(string token, CustomerSignRule.Kind kind, RespondRequest request, Caller caller)
    {
        _caller = caller;
        if (kind is not (CustomerSignRule.Kind.Declined or CustomerSignRule.Kind.ChangeRequested)) return BadRequest("รายการนี้ทำกับเอกสารชนิดนี้ไม่ได้");
        if (await OverRateAsync("respond", 10, 60)) return TooMany();
        var doc = await FindByTokenAsync(token);
        if (doc == null) return NotFound();
        if (!ViaPageToken(doc.QuotationAcceptToken, token))
            return Conflict("รายการนี้ทำได้จากลิงก์หน้าเอกสารที่ผู้ขายส่งให้เท่านั้น — กรุณาขอลิงก์ใหม่จากผู้ขาย");
        var en = await IsEnglishAsync(doc);
        var actionId = kind == CustomerSignRule.Kind.Declined ? CustomerDocPage.ActionDecline : CustomerDocPage.ActionRequestChange;
        if (PageActionClosedReason(doc, actionId, en) is { } closed) return Conflict(closed);
        var buyer = BuyerOf(doc);
        var (signer, signerError) = CustomerSignRule.DecideSigner(request.Signer, buyer.IsJuristic, emailRequired: false, en);
        if (signerError != null) return BadRequest(signerError);
        var (reasonCode, message, reasonError) = CustomerSignRule.DecideReason(kind, request.ReasonCode, request.Message, en);
        if (reasonError != null) return BadRequest(reasonError);

        var now = DateTime.UtcNow;
        var contentHash = DocumentSignedContent.Hash(doc, doc.Lines);
        var seenRevision = request.RevisionNumber;
        if (CustomerDocPage.RespondStaleReason(true, seenRevision, doc.RevisionNumber, request.ContentHash, contentHash) is { } stale)
            return Conflict(stale);
        var tokenHash = QuotationAcceptance.Sha256Hex(token.ToLowerInvariant())!;
        var pageHash = DocumentPayLinkPolicy.TokenHash(token);
        var evidence = NewEvidence(doc, kind, true, signer, signer!.FullName, buyer, en, now, contentHash, tokenHash, caller.Ip,
            QuotationAcceptance.Sha256Hex(caller.UserAgent));
        evidence.ReasonCode = reasonCode;
        evidence.Message = message;
        var response = kind.ToString();

        await using var tx = await _db.Database.BeginTransactionAsync();
        // 267-1-11 หนึ่งคำตอบต่อ Rev — จองเฉพาะใบที่ยังไม่ถูกยอมรับและยังไม่เคยตอบ + Rev ที่ลูกค้าเห็น + ลิงก์ยังไม่ถูกเพิกถอน
        var claimed = await _db.Documents
            .Where(d => d.Id == doc.Id && d.CompanyId == doc.CompanyId
                && d.QuotationAcceptedAt == null && d.CustomerResponse == null
                && d.RevisionNumber == seenRevision
                && d.PayLinkTokenHash == pageHash && d.PayLinkRevokedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.CustomerResponse, response)
                .SetProperty(d => d.CustomerResponseAt, now)
                .SetProperty(d => d.UpdatedAt, now));
        if (claimed == 0)
        {
            await tx.RollbackAsync();
            return Conflict(CustomerDocPage.RespondStaleMessage);
        }
        _db.CustomerDocumentActions.Add(evidence);
        await EnsureRopaAsync(doc.CompanyId);
        _db.AddChainedAuditLog(new Models.Entities.AuditLog
        {
            CompanyId = doc.CompanyId, UserId = null, UserEmail = null, Action = AuditAction.Update,
            EntityType = nameof(Models.Entities.Document), EntityId = doc.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { customerResponse = (string?)null }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = kind == CustomerSignRule.Kind.Declined ? "QuotationDeclinedOnline" : "QuotationChangeRequestedOnline",
                documentNumber = doc.DocumentNumber, revisionNumber = doc.RevisionNumber, respondedBy = signer.FullName, respondedAt = now,
                reasonCode, message, contentHash, tokenSha256 = tokenHash, customerActionId = evidence.Id,
                signerTitle = evidence.SignerTitle, signerOrgName = evidence.SignerOrgName,
                attestationVersion = evidence.AttestationVersion, attestationTextHash = evidence.AttestationTextHash,
                ruleCode = CustomerSignText.RuleCode, legalReference = CustomerSignText.LegalReference,
            }),
            IpAddress = caller.Ip, Timestamp = now,
        });
        SignEventOutbox.EnqueueEvent(_db, doc, SignRequestKinds.EventFor(kind), now, signer.FullName, false, contentHash, EventRef(tokenHash, evidence.Id))
            .CustomerActionId = evidence.Id;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        _logger.LogInformation("Quotation {DocNo} {Kind} online (company {Cid})", doc.DocumentNumber, kind, doc.CompanyId);
        return Ok(new { respondedAt = now, respondedBy = signer.FullName, response, actionId = evidence.Id }, CustomerSignText.Done(en, kind));
    }

    // ═══════════════ เซ็นรับสินค้า ═══════════════

    public async Task<Outcome> SignDelivery(string token, PublicQuotationController.DeliverySignRequest request, Caller caller)
    {
        _caller = caller;
        // รอบ 239 ฝ่ายค้าน S2: เพดานต่อ IP → หาใบจาก token → เซ็นแล้ว = สถานะเดิม (idempotent) → ค่อยถอด/ตรวจรูป
        // (เดิมถอดรูปก่อนรู้ว่า token ใช้ได้ ⇒ token มั่วก็กิน CPU ถอด PNG ได้)
        if (await OverRateAsync("delivery", 10, 60)) return TooMany();
        var doc = await FindDeliveryByTokenAsync(token, track: true);
        if (doc == null) return NotFound();
        if (doc.DeliverySignedAt != null)
            return Ok(new { signedAt = doc.DeliverySignedAt, signedBy = doc.DeliverySignedBy }, "ใบส่งของนี้ถูกเซ็นรับไปแล้ว");
        // รอบ 267 P1: token ของหน้าเอกสาร ⇒ ช่วงเซ็นรับจากตัวตัดสินเดียวกับปุ่มบนหน้าเอกสาร
        var viaPage = ViaPageToken(doc.DeliverySignToken, token);
        var en = viaPage && await IsEnglishAsync(doc);
        if (RespondClosedReason(doc, viaPage) is { } closed)
            return Conflict(closed);
        if (viaPage && PageActionClosedReason(doc, CustomerDocPage.ActionSignDelivery, en) is { } pageClosed)
            return Conflict(pageClosed);
        // รอบ 267 ฝ่ายค้าน H1: เซ็นรับได้เฉพาะฉบับที่ลูกค้าเห็น (เลข Rev + hash เนื้อหา) · เลข Rev อยู่ในเงื่อนไขจองด้วย
        var seenRevision = request.RevisionNumber;
        if (CustomerDocPage.RespondStaleReason(viaPage, seenRevision, doc.RevisionNumber, request.ContentHash, DocumentSignedContent.Hash(doc, doc.Lines)) is { } stale)
            return Conflict(stale);
        var buyer = BuyerOf(doc);
        // รอบ 267 P2: ลิงก์หน้าเอกสาร ⇒ ผู้รับของครบ (ชื่อ · นามสกุล · ตำแหน่ง · ติ๊กรับรอง) · ลิงก์รุ่นก่อน = พิมพ์ชื่อตามเดิม
        CustomerSignRule.Signer? signer = null;
        if (viaPage)
        {
            var (s, signerError) = CustomerSignRule.DecideSigner(request.Signer, buyer.IsJuristic, emailRequired: false, en);
            if (signerError != null) return BadRequest(signerError);
            signer = s;
        }
        else if (string.IsNullOrWhiteSpace(request.SignedBy))
            return BadRequest("กรุณาระบุชื่อผู้รับสินค้า");
        // รอบ 239: ตัวตรวจลายเซ็นตัวเดียวกับการยอมรับใบเสนอราคา (PNG จริง · ≤ 200KB · ขนาดภาพ · ไม่ว่าง) — เดิมรับ base64 อะไรก็ได้ ≤ 700KB
        var sig = SignatureImageValidator.Validate(request.SignatureBase64);
        if (!sig.Ok)
            return BadRequest(sig.Message ?? "กรุณาวาดลายเซ็น");

        // รอบ 240: จองการเซ็นแบบอะตอมมิก (เฉพาะแถวที่ยังไม่เซ็น + token เดิม) + แถวหลักฐาน + เหตุการณ์ delivery.signed ลง outbox ในธุรกรรมเดียว —
        // กดพร้อมกันสองเครื่อง = เซ็นครั้งเดียว · เหตุการณ์เดียว (เดิมเขียนทับกันได้)
        var now = DateTime.UtcNow;
        var contentHash = DocumentSignedContent.Hash(doc, doc.Lines);   // เนื้อหาที่ลูกค้าเห็นตอนเซ็น (ก่อนต่อหมายเหตุ)
        var tokenHash = QuotationAcceptance.Sha256Hex(token.ToLowerInvariant());
        var pageHash = DocumentPayLinkPolicy.TokenHash(token);
        await using var tx = await _db.Database.BeginTransactionAsync();
        var claimed = await _db.Documents
            .Where(d => d.Id == doc.Id && d.CompanyId == doc.CompanyId
                && d.DeliverySignedAt == null && d.DeliverySignToken == doc.DeliverySignToken
                && (seenRevision == null || d.RevisionNumber == seenRevision)
                && (!viaPage || (d.PayLinkTokenHash == pageHash && d.PayLinkRevokedAt == null)))
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.DeliverySignedAt, now));
        if (claimed == 0)
        {
            await tx.RollbackAsync();
            var fresh = await FindDeliveryByTokenAsync(token);
            if (fresh == null) return NotFound();
            if (fresh.DeliverySignedAt == null) return Conflict(CustomerDocPage.RespondStaleMessage);
            return Ok(new { signedAt = fresh.DeliverySignedAt, signedBy = fresh.DeliverySignedBy }, "ใบส่งของนี้ถูกเซ็นรับไปแล้ว");
        }

        doc.DeliverySignedAt = now;
        doc.DeliverySignedBy = signer?.FullName ?? QuotationAcceptance.CleanText(request.SignedBy, QuotationAcceptance.NameMax) ?? "-";
        doc.DeliverySignatureBase64 = sig.DataUri;   // ไบต์ที่ผ่านตัวตรวจแล้ว (ไม่ใช่ข้อความดิบจากคำขอ)
        var note = QuotationAcceptance.CleanText(request.Note, QuotationAcceptance.NoteMax);
        // ลิงก์รุ่นก่อน: ต่อบรรทัดลงหมายเหตุตามเดิม (พฤติกรรมรอบ 239) · ลิงก์หน้าเอกสาร: ไม่แตะเนื้อหาของใบหลังเซ็น — หมายเหตุของผู้รับอยู่ในแถวหลักฐาน
        // (เนื้อหาเปลี่ยนหลังเซ็น = hash ไม่ตรงกับที่ลงนาม ⇒ สร้างฉบับที่ลูกค้าเซ็นไม่ได้)
        if (!viaPage)
            doc.Notes = (doc.Notes ?? "") + $"\n[ลูกค้าเซ็นรับสินค้าออนไลน์] {doc.DeliverySignedBy} — {now:yyyy-MM-dd HH:mm} UTC" + (note == null ? "" : $" · หมายเหตุ: {note}");
        var evidence = NewEvidence(doc, CustomerSignRule.Kind.DeliverySigned, viaPage, signer, doc.DeliverySignedBy, buyer, en, now, contentHash, tokenHash!,
            caller.Ip, QuotationAcceptance.Sha256Hex(caller.UserAgent));
        evidence.SignatureSha256 = sig.Png is { } sigPng ? CustomerSignRule.FileSha256(sigPng) : null;
        evidence.Message = note;
        _db.CustomerDocumentActions.Add(evidence);
        await EnsureRopaAsync(doc.CompanyId);
        _db.AddChainedAuditLog(new Models.Entities.AuditLog
        {
            CompanyId = doc.CompanyId, UserId = null, UserEmail = null, Action = AuditAction.Approve,
            EntityType = nameof(Models.Entities.Document), EntityId = doc.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { deliverySignedAt = (DateTime?)null }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "DeliverySignedOnline", documentNumber = doc.DocumentNumber, revisionNumber = doc.RevisionNumber,
                signedBy = doc.DeliverySignedBy, signedAt = now, note, signatureSha256 = evidence.SignatureSha256, contentHash, tokenSha256 = tokenHash,
                customerActionId = evidence.Id, identityLevel = evidence.IdentityLevel, signerTitle = evidence.SignerTitle,
                signerOrgName = evidence.SignerOrgName, attestationVersion = evidence.AttestationVersion, attestationTextHash = evidence.AttestationTextHash,
                ruleCode = CustomerSignText.RuleCode, legalReference = CustomerSignText.LegalReference,
            }),
            IpAddress = caller.Ip, Timestamp = now,
        });
        var eventRef = EventRef(tokenHash!, evidence.Id);
        SignEventOutbox.Enqueue(_db, doc, SignRequestKinds.DeliverySign, now, doc.DeliverySignedBy, hasSignature: true, contentHash, eventRef).CustomerActionId = evidence.Id;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        _logger.LogInformation("DeliveryNote {DocNo} signed online (company {Cid})", doc.DocumentNumber, doc.CompanyId);

        var signedCopyReady = await TrySnapshotNowAsync(evidence);
        return Ok(new { signedAt = doc.DeliverySignedAt, signedBy = doc.DeliverySignedBy, actionId = evidence.Id, signedCopyReady },
            viaPage ? CustomerSignText.Done(en, CustomerSignRule.Kind.DeliverySigned) : "เซ็นรับสินค้าเรียบร้อย — ลายเซ็นถูกประทับลงเอกสารแล้ว");
    }

    // ═══════════════ แถวหลักฐาน ═══════════════

    /// <summary>กุญแจกันซ้ำของเหตุการณ์ outbox ต่อแถวหลักฐาน (ไม่ใช่ต่อ token — token ของหน้าเอกสารอยู่ข้าม Rev)</summary>
    private static string EventRef(string tokenHash, Guid actionId) => tokenHash[..Math.Min(16, tokenHash.Length)] + ":" + actionId.ToString("N");

    private static (string? Name, bool IsJuristic) BuyerOf(Document doc)
        => (doc.Contact?.Name, doc.Contact == null || doc.Contact.ContactType != ContactType.Individual);

    /// <summary>แถวหลักฐานของคำตอบหนึ่งครั้ง — ลิงก์หน้าเอกสาร = ตัวตนครบ + hash ของข้อความรับรองที่ติ๊ก · ลิงก์รุ่นก่อน = ชื่อที่พิมพ์อย่างเดียว</summary>
    private static CustomerDocumentAction NewEvidence(Document doc, CustomerSignRule.Kind kind, bool viaPage, CustomerSignRule.Signer? signer,
        string displayName, (string? Name, bool IsJuristic) buyer, bool en, DateTime now, string contentHash, string tokenHash, string? ip, string? uaHash)
    {
        var full = viaPage && signer != null;
        return new CustomerDocumentAction
        {
            CompanyId = doc.CompanyId, DocumentId = doc.Id, RevisionNumber = doc.RevisionNumber, Action = kind.ToString(),
            Channel = viaPage ? CustomerSignRule.ChannelLink : CustomerSignRule.ChannelLegacyLink,
            IdentityLevel = full ? CustomerSignRule.IdentityFull : CustomerSignRule.IdentityNameOnly,
            SignerFirstName = full ? signer!.FirstName : displayName,
            SignerLastName = full ? signer!.LastName : null,
            SignerTitle = full ? signer!.Title : null,
            SignerOrgName = full ? QuotationAcceptance.CleanText(buyer.Name, 300) : null,
            SignerEmail = full ? signer!.Email : null,
            AttestationVersion = full ? CustomerSignText.Version : CustomerSignText.LegacyVersion,
            AttestationTextHash = full ? CustomerSignText.TextHash(CustomerSignText.Attestation(en, kind, buyer.Name, buyer.IsJuristic)) : null,
            ContentHash = contentHash, TokenHash = tokenHash, Ip = ip, UserAgentHash = uaHash,
            CreatedAt = now, CreatedBy = "ผู้ถือลิงก์เอกสาร (ไม่ได้ล็อกอิน)",
        };
    }

    /// <summary>PDPA ม.39: บริษัทเริ่มเก็บข้อมูลผู้ลงนามของคู่ค้า ⇒ มีแถว RoPA ของกิจกรรมนี้ (ครั้งเดียวต่อบริษัท · ไม่ทับที่บริษัทแก้ไว้) — เพิ่มในธุรกรรมเดียวกับแถวหลักฐานแรก</summary>
    private async Task EnsureRopaAsync(Guid companyId)
    {
        if (await _db.PdpaProcessingActivities.AsNoTracking().AnyAsync(r => r.CompanyId == companyId && r.Purpose == CustomerSignText.RopaPurpose)) return;
        _db.PdpaProcessingActivities.Add(new PdpaProcessingActivity
        {
            CompanyId = companyId, Purpose = CustomerSignText.RopaPurpose, LegalBasis = CustomerSignText.RopaLegalBasis,
            DataCategories = CustomerSignText.RopaDataCategories, RetentionPeriod = CustomerSignText.RopaRetention,
            Recipients = CustomerSignText.RopaRecipients, TransfersOutsideThailand = false, Notes = CustomerSignText.RopaNote,
            CreatedBy = "system", LastReviewedBy = "system",
        });
    }

    // ═══════════════ ฟอร์มของหน้า + ประวัติ ═══════════════

    /// <summary>ฟอร์มผู้ลงนามที่หน้าแสดง (ข้อความทั้งหมดจาก <see cref="CustomerSignText"/>) — null = ใบนี้ไม่มีปุ่มตอบรับที่เปิดอยู่</summary>
    public async Task<CustomerDocRespondForm?> FormAsync(Guid companyId, Guid documentId, IReadOnlyList<CustomerDocPage.PageAction> actions, bool en, CancellationToken ct = default)
    {
        var open = actions.Where(a => a.Enabled && a.Id is CustomerDocPage.ActionAccept or CustomerDocPage.ActionDecline
            or CustomerDocPage.ActionRequestChange or CustomerDocPage.ActionSignDelivery).ToList();
        open = open.Where(a => a.Enabled).ToList();
        if (open.Count == 0) return null;
        var doc = await _db.Documents.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted, ct);
        if (doc == null) return null;
        await _db.HydrateContactAsync(companyId, doc);
        var buyer = BuyerOf(doc);
        var sellerName = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var settings = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => new { s.RequireSignatureForQuotationAccept, s.CustomerSignOtpMinAmount }).FirstOrDefaultAsync(ct);
        var items = new List<CustomerDocRespondAction>();
        foreach (var a in open)
        {
            if (CustomerSignRule.KindFor(a.Id, doc.DocumentType) is not { } kind) continue;
            var signature = kind == CustomerSignRule.Kind.DeliverySigned ? "required"
                : kind == CustomerSignRule.Kind.Accepted ? (settings?.RequireSignatureForQuotationAccept == true ? "required" : "optional") : "none";
            items.Add(new CustomerDocRespondAction(a.Id, a.Label,
                CustomerSignText.Attestation(en, kind, buyer.Name, buyer.IsJuristic), CustomerSignText.Consent(en, kind), signature,
                OtpRequired: CustomerSignRule.OtpRequired(settings?.CustomerSignOtpMinAmount, doc.TotalAmount, kind),
                NeedsReason: kind == CustomerSignRule.Kind.Declined, NeedsMessage: kind == CustomerSignRule.Kind.ChangeRequested));
        }
        if (items.Count == 0) return null;
        return new CustomerDocRespondForm(doc.RevisionNumber, DocumentSignedContent.Hash(doc, doc.Lines), CustomerSignText.Version,
            TitleRequired: buyer.IsJuristic, items,
            CustomerSignText.DeclineReasons.Select(r => new CustomerDocRespondReason(r.Code, en ? r.En : r.Th)).ToList(),
            CustomerSignText.PrivacyNotice(en, sellerName), CustomerSignText.LegalReference, CustomerSignText.FormLabels(en));
    }

    /// <summary>คำตอบที่ลูกค้าเคยทำบนใบนี้ (ใหม่ → เก่า) สำหรับหน้าเอกสาร — ชื่อ/เวลา/Rev เท่านั้น (ไม่ส่ง IP · ไม่ส่งพาธไฟล์)</summary>
    public async Task<IReadOnlyList<CustomerDocResponseItem>> HistoryAsync(Guid companyId, Guid documentId, bool en, CancellationToken ct = default)
    {
        var rows = await _db.CustomerDocumentActions.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.DocumentId == documentId)
            .OrderByDescending(a => a.CreatedAt).Take(20)
            .Select(a => new { a.Id, a.Action, a.RevisionNumber, a.SignerFirstName, a.SignerLastName, a.SignerTitle, a.CreatedAt, a.SignedPdfSha256, a.Channel, a.ErasedAt })
            .ToListAsync(ct);
        return rows.Select(a =>
        {
            var kind = Enum.TryParse<CustomerSignRule.Kind>(a.Action, out var k) ? k : CustomerSignRule.Kind.Accepted;
            var signs = kind is CustomerSignRule.Kind.Accepted or CustomerSignRule.Kind.DeliverySigned;
            return new CustomerDocResponseItem(a.Id, a.Action, CustomerSignText.KindLabel(en, kind), a.RevisionNumber,
                string.Join(" ", new[] { a.SignerFirstName, a.SignerLastName }.Where(s => !string.IsNullOrWhiteSpace(s))), a.SignerTitle, a.CreatedAt,
                SignedCopyReady: a.SignedPdfSha256 != null && a.ErasedAt == null,
                SignedCopyExpected: signs && a.Channel == CustomerSignRule.ChannelLink && a.ErasedAt == null);
        }).ToList();
    }

    // ═══════════════ รหัสยืนยันอีเมล (267-1-6) ═══════════════

    private async Task<decimal?> OtpMinAmountAsync(Guid companyId)
        => await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId).Select(s => s.CustomerSignOtpMinAmount).FirstOrDefaultAsync();

    /// <summary>ส่งรหัสยืนยันไปที่อีเมลของผู้ลงนาม — เฉพาะใบเสนอราคาที่ปุ่มยอมรับเปิดอยู่และบริษัทตั้งเกณฑ์ไว้ · ไม่บอกว่าอีเมลใดเคยขอ ·
    /// ส่งไม่ได้ = บอกตรง ๆ (ลูกค้ายังขอให้ผู้ขายส่งลิงก์/ติดต่อผู้ขายได้ — ไม่ยอมรับแบบข้ามรหัส)</summary>
    public async Task<Outcome> RequestOtpAsync(Guid companyId, string token, string? emailRaw, Caller caller)
    {
        _caller = caller;
        if (await OverRateAsync("otp", 5, 30)) return TooMany();
        var doc = await FindByTokenAsync(token);
        if (doc == null || doc.CompanyId != companyId || !ViaPageToken(doc.QuotationAcceptToken, token)) return NotFound(CustomerDocPage.NotFoundMessage);
        var en = await IsEnglishAsync(doc);
        if (PageActionClosedReason(doc, CustomerDocPage.ActionAccept, en) is { } closed) return Conflict(closed);
        if (!CustomerSignRule.OtpRequired(await OtpMinAmountAsync(companyId), doc.TotalAmount, CustomerSignRule.Kind.Accepted))
            return BadRequest(en ? "This document does not need a verification code" : "เอกสารนี้ไม่ต้องใช้รหัสยืนยัน");
        var (email, emailError) = CustomerSignRule.CleanEmail(emailRaw, en);
        if (emailError != null || email == null) return BadRequest(emailError ?? (en ? "Please enter your email" : "กรุณากรอกอีเมล"));
        var now = DateTime.UtcNow;
        var recent = now.AddSeconds(-CustomerSignRule.OtpResendSeconds);
        if (await _db.CustomerSignOtps.AsNoTracking().AnyAsync(o => o.CompanyId == companyId && o.DocumentId == doc.Id && o.CreatedAt > recent))
            return new Outcome(429, null, en ? "A code was just sent — please wait a minute before requesting another" : "เพิ่งส่งรหัสไป — กรุณารอ 1 นาทีก่อนขอรหัสใหม่");
        if (_email == null || !await _email.IsSystemEmailConfiguredAsync())
            return new Outcome(503, null, en ? "The verification email cannot be sent right now — please contact the seller"
                : "ระบบส่งอีเมลรหัสยืนยันไม่ได้ในขณะนี้ — กรุณาติดต่อผู้ขาย");
        var code = CustomerSignRule.NewOtpCode();
        var row = new CustomerSignOtp
        {
            CompanyId = companyId, DocumentId = doc.Id, EmailHash = CustomerSignRule.EmailHash(email),
            CodeHash = CustomerSignRule.OtpHash(doc.Id, email, code), ExpiresAt = now.AddMinutes(CustomerSignRule.OtpTtlMinutes), CreatedAt = now,
        };
        _db.CustomerSignOtps.Add(row);
        await _db.SaveChangesAsync();
        var seller = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync() ?? "";
        var enc = System.Net.WebUtility.HtmlEncode;
        var body = en
            ? $"<p>Your verification code for quotation <b>{enc(doc.DocumentNumber)}</b> from {enc(seller)} is</p><p style=\"font-size:24px;letter-spacing:4px\"><b>{code}</b></p><p>The code is valid for {CustomerSignRule.OtpTtlMinutes} minutes. If you did not request it, ignore this email.</p>"
            : $"<p>รหัสยืนยันสำหรับใบเสนอราคา <b>{enc(doc.DocumentNumber)}</b> ของ {enc(seller)} คือ</p><p style=\"font-size:24px;letter-spacing:4px\"><b>{code}</b></p><p>รหัสใช้ได้ {CustomerSignRule.OtpTtlMinutes} นาที หากท่านไม่ได้ขอรหัสนี้ ไม่ต้องดำเนินการใด ๆ</p>";
        if (!await _email.TrySendAsync(email, CustomerSignText.OtpSubject(en, doc.DocumentNumber), body))
        {
            // ส่งไม่ถึง ⇒ รหัสที่เพิ่งออกใช้ไม่ได้ (ไม่ทิ้งรหัสที่ไม่มีใครได้รับไว้ให้เดา)
            await _db.CustomerSignOtps.Where(o => o.Id == row.Id && o.CompanyId == companyId).ExecuteUpdateAsync(u => u.SetProperty(o => o.ConsumedAt, now));
            _logger.LogError("ส่งอีเมลรหัสยืนยันของการยอมรับใบเสนอราคาไม่สำเร็จ doc={DocId} company={CompanyId}", doc.Id, companyId);
            return new Outcome(503, null, en ? "The verification email could not be sent — please check the address or contact the seller"
                : "ส่งอีเมลรหัสยืนยันไม่สำเร็จ — กรุณาตรวจอีเมล หรือติดต่อผู้ขาย");
        }
        return Ok(new { sent = true, expiresInMinutes = CustomerSignRule.OtpTtlMinutes }, CustomerSignText.FormLabels(en)["otpSent"]);
    }

    /// <summary>ตรวจรหัสกับแถวล่าสุดของ (ใบ + อีเมล) — ผิด = นับครั้ง (เกิน <see cref="CustomerSignRule.OtpMaxAttempts"/> ต้องขอรหัสใหม่) · ผ่าน = คืนแถวให้ผู้เรียกประทับ "ใช้แล้ว" ในธุรกรรม</summary>
    private async Task<(Guid? Id, string? Error)> CheckOtpAsync(Document doc, string email, string? code, DateTime now, bool en)
    {
        var emailHash = CustomerSignRule.EmailHash(email);
        var row = await _db.CustomerSignOtps.AsNoTracking()
            .Where(o => o.CompanyId == doc.CompanyId && o.DocumentId == doc.Id && o.EmailHash == emailHash && o.ConsumedAt == null)
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync();
        var outcome = CustomerSignRule.VerifyOtp(row?.CodeHash, row?.ExpiresAt, row?.Attempts ?? 0, row?.ConsumedAt, doc.Id, email, code, now);
        if (outcome == CustomerSignRule.OtpOutcome.Ok) return (row!.Id, null);
        if (outcome == CustomerSignRule.OtpOutcome.Wrong)
            await _db.CustomerSignOtps.Where(o => o.Id == row!.Id && o.CompanyId == doc.CompanyId)
                .ExecuteUpdateAsync(u => u.SetProperty(o => o.Attempts, o => o.Attempts + 1));
        return (null, CustomerSignRule.OtpMessage(en, outcome));
    }

    // ═══════════════ ฉบับที่ลูกค้าเซ็น (ไฟล์ + SHA-256) ═══════════════

    /// <summary>ผลของขั้น "ฉบับที่ลูกค้าเซ็น" — <paramref name="Done"/> = ไม่ต้องลองอีก (สำเร็จ หรือไม่มีอะไรให้ทำ) · ไม่ Done = ลองใหม่ได้ (เหตุใน <paramref name="Note"/>)</summary>
    public sealed record SignedCopyResult(bool Done, bool HasFile, string? Note);

    /// <summary>ลองสร้างฉบับที่ลูกค้าเซ็นทันทีหลังบันทึก (ลูกค้าดาวน์โหลดจากหน้าได้เลย) — ล้ม = ไม่กระทบคำตอบที่บันทึกแล้ว · งาน outbox ลองใหม่และแจ้งพนักงานเมื่อเลิกลอง</summary>
    private async Task<bool> TrySnapshotNowAsync(CustomerDocumentAction evidence)
    {
        try { return (await EnsureSnapshotAsync(evidence.CompanyId, evidence.Id)).HasFile; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "สร้างฉบับที่ลูกค้าเซ็นทันทีไม่สำเร็จ action={ActionId} company={CompanyId} — งาน outbox จะลองใหม่", evidence.Id, evidence.CompanyId);
            return false;
        }
    }

    /// <summary>
    /// สร้างไฟล์ฉบับที่ลูกค้าเซ็นของแถวหลักฐาน (ครั้งเดียว) — เฉพาะแถวจากลิงก์หน้าเอกสารที่เป็น "ยอมรับ/เซ็นรับ" และใบยังเป็น Rev + เนื้อหาเดียวกับตอนลงนาม
    /// (<see cref="CustomerSignRule.SnapshotStillValid"/> — ไม่อย่างนั้นไฟล์ที่ได้ไม่ใช่ฉบับที่ลูกค้าเห็น ⇒ ไม่สร้าง) · เขียนไฟล์นอก allow-list <c>/uploads</c> ·
    /// ประทับ <c>SignedPdf*</c> แบบมีเงื่อนไข (ยังว่างเท่านั้น — สองทางพร้อมกันได้ไฟล์เดียว) · โยนเมื่อเรนเดอร์/เขียนไฟล์ล้ม (ผู้เรียกตัดสินว่าจะลองใหม่)
    /// </summary>
    public async Task<SignedCopyResult> EnsureSnapshotAsync(Guid companyId, Guid actionId, CancellationToken ct = default)
    {
        var a = await _db.CustomerDocumentActions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == actionId && x.CompanyId == companyId, ct);
        if (a == null) return new(true, false, "ไม่พบแถวหลักฐาน");
        if (a.SignedPdfSha256 != null) return new(true, a.ErasedAt == null, null);
        if (a.ErasedAt != null) return new(true, false, "ข้อมูลผู้ลงนามถูกลบตามคำขอแล้ว");
        if (a.Action is not (nameof(CustomerSignRule.Kind.Accepted) or nameof(CustomerSignRule.Kind.DeliverySigned)))
            return new(true, false, null);
        if (a.Channel != CustomerSignRule.ChannelLink)
            return new(true, false, "ลิงก์รุ่นก่อนรอบ 267 ไม่มีฉบับที่ลูกค้าเซ็น (หลักฐานอยู่ในแถวนี้และบนเอกสาร)");
        if (_pdf == null) return new(false, false, "ยังไม่มีตัวสร้าง PDF");
        var doc = await _db.Documents.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == a.DocumentId && d.CompanyId == companyId, ct);
        if (doc == null) return new(true, false, "ไม่พบเอกสาร");
        if (!CustomerSignRule.SnapshotStillValid(a.RevisionNumber, a.ContentHash, doc.RevisionNumber, DocumentSignedContent.Hash(doc, doc.Lines)))
            return new(true, false, "เอกสารถูกแก้หลังลูกค้าลงนามก่อนที่ระบบจะสร้างฉบับที่ลงนามได้ — ไม่สร้างไฟล์จากเนื้อหาที่ลูกค้าไม่ได้เห็น");
        var gen = await _pdf.GenerateDocumentPdfAsync(companyId, new Models.DTOs.DocumentTemplate.GeneratePdfRequest(doc.Id, null, null, null, null));
        if (gen?.PdfData is not { Length: > 0 } bytes) return new(false, false, "สร้างไฟล์ PDF ไม่สำเร็จ");
        var path = CustomerSignRule.SignedCopyLocation(SignatureStore.Locator.AttachmentsRoot, companyId, doc.Id, a.Id);
        var sha = CustomerSignRule.FileSha256(bytes);
        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var claimed = await _db.CustomerDocumentActions
            .Where(x => x.Id == a.Id && x.CompanyId == companyId && x.SignedPdfSha256 == null && x.ErasedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.SignedPdfPath, path).SetProperty(x => x.SignedPdfSha256, sha).SetProperty(x => x.SignedPdfAt, now), ct);
        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            return new(true, true, null);   // อีกทางสร้างไปแล้ว — ไม่เขียนทับไฟล์ของเขา
        }
        // เขียนไฟล์ก่อน commit: เขียนล้ม ⇒ ธุรกรรมย้อนกลับ (ไม่มีแถวที่ชี้ไฟล์ที่ไม่มี)
        await SignatureStore.WriteAsync(path, bytes, "application/pdf", ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = "ระบบ (ฉบับที่ลูกค้าเซ็น)", Action = AuditAction.Create,
            EntityType = nameof(CustomerDocumentAction), EntityId = a.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "CustomerSignedCopyStored", documentId = doc.Id, documentNumber = doc.DocumentNumber, revisionNumber = a.RevisionNumber,
                signedPdfSha256 = sha, contentHash = a.ContentHash, ruleCode = CustomerSignRule.RuleCode, legalReference = CustomerSignText.LegalReference,
            }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(true, true, null);
    }

    /// <summary>อ่านไฟล์ฉบับที่ลูกค้าเซ็นของแถวหลักฐาน — ตำแหน่งต้องเป็นของบริษัท/เอกสาร/แถวนี้ และ SHA-256 ต้องตรงกับที่บันทึก (ไฟล์ถูกแก้ = ไม่ส่งออก + log error)</summary>
    public async Task<(byte[] Bytes, string FileName)?> ReadSignedCopyAsync(Guid companyId, Guid documentId, Guid actionId, CancellationToken ct = default)
    {
        var a = await _db.CustomerDocumentActions.AsNoTracking()
            .Where(x => x.Id == actionId && x.CompanyId == companyId && x.DocumentId == documentId && x.ErasedAt == null)
            .Select(x => new { x.SignedPdfPath, x.SignedPdfSha256, x.RevisionNumber }).FirstOrDefaultAsync(ct);
        if (a?.SignedPdfSha256 == null
            || !CustomerSignRule.IsOwnSignedCopyLocation(a.SignedPdfPath, SignatureStore.Locator.AttachmentsRoot, companyId, documentId, actionId))
            return null;
        var bytes = await SignatureStore.ReadAllBytesAsync(a.SignedPdfPath, ct);
        if (!CustomerSignRule.SignedCopyIntact(bytes, a.SignedPdfSha256))
        {
            _logger.LogError("ฉบับที่ลูกค้าเซ็นไม่ตรงกับ SHA-256 ที่บันทึก (ไฟล์หาย/ถูกแก้) action={ActionId} company={CompanyId} — ไม่ส่งไฟล์ออก", actionId, companyId);
            return null;
        }
        var number = await _db.Documents.AsNoTracking().Where(d => d.Id == documentId && d.CompanyId == companyId).Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct);
        return (bytes!, $"{number}-signed-rev{a.RevisionNumber}.pdf");
    }

    /// <summary>ผู้ถือลิงก์ของใบดาวน์โหลดฉบับที่ลงนามของใบนั้น (แถวหลักฐานต้องเป็นของใบของ token นี้ — token ของใบหนึ่งอ่านของอีกใบไม่ได้)</summary>
    public async Task<(byte[] Bytes, string FileName, Guid DocumentId)?> SignedCopyForTokenAsync(Guid companyId, string token, Guid actionId, CancellationToken ct = default)
    {
        if (!DocumentPayLinkPolicy.LooksValid(token)) return null;
        var hash = DocumentPayLinkPolicy.TokenHash(token);
        var d = await _db.Documents.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.PayLinkTokenHash == hash && x.PayLinkRevokedAt == null && !x.IsDeleted)
            .Select(x => new { x.Id, x.PayLinkTokenHash, x.PayLinkExpiresAt, x.PayLinkRevokedAt, x.Status }).FirstOrDefaultAsync(ct);
        if (d == null || !DocumentPayLinkPolicy.TokenMatches(token, d.PayLinkTokenHash)
            || CustomerDocPage.StateOf(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, DateTime.UtcNow) != CustomerDocPage.LinkState.Usable
            || !DocumentStatusRules.IsIssued(d.Status))
            return null;
        var file = await ReadSignedCopyAsync(companyId, d.Id, actionId, ct);
        return file == null ? null : (file.Value.Bytes, file.Value.FileName, d.Id);
    }

    /// <summary>
    /// ขั้นของงาน outbox ต่อแถวหลักฐาน: (1) สร้างฉบับที่ลูกค้าเซ็นถ้ายังไม่มี (2) ส่งสำเนาทางอีเมลถ้าผู้ลงนามให้อีเมลไว้ (ช่องทางที่ผู้ลงนามเลือกเอง) ·
    /// ไม่ Done = งาน outbox ลองใหม่ตามจังหวะ (ไฟล์ที่สร้างแล้วไม่ถูกสร้างซ้ำ) · ผู้เรียกเดียว <c>SignEventDispatcher</c>
    /// </summary>
    public async Task<SignedCopyResult> ProcessSignedCopyAsync(Guid companyId, Guid actionId, CancellationToken ct = default)
    {
        var snap = await EnsureSnapshotAsync(companyId, actionId, ct);
        if (!snap.Done || !snap.HasFile) return snap;
        var a = await _db.CustomerDocumentActions.AsNoTracking()
            .Where(x => x.Id == actionId && x.CompanyId == companyId)
            .Select(x => new { x.DocumentId, x.SignerEmail, x.SignerFirstName, x.ErasedAt }).FirstOrDefaultAsync(ct);
        if (a == null || a.ErasedAt != null || string.IsNullOrWhiteSpace(a.SignerEmail)) return snap;   // ไม่ให้อีเมล = รับจากหน้าเอกสาร
        if (_email == null) return new(false, true, "ยังไม่มีตัวส่งอีเมล — ส่งสำเนาฉบับที่ลงนามไม่ได้");
        var file = await ReadSignedCopyAsync(companyId, a.DocumentId, actionId, ct);
        if (file == null) return new(false, true, "อ่านไฟล์ฉบับที่ลงนามไม่ได้ (ไฟล์หายหรือไม่ตรงกับ SHA-256 ที่บันทึก)");
        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, a.DocumentId);
        var seller = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var number = await _db.Documents.AsNoTracking().Where(d => d.Id == a.DocumentId && d.CompanyId == companyId).Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct) ?? "";
        var enc = System.Net.WebUtility.HtmlEncode;
        var body = heading.IsEnglish
            ? $"<p>Dear {enc(a.SignerFirstName)},</p><p>Attached is the copy of {enc(heading.Title)} <b>{enc(number)}</b> from {enc(seller)} as you signed it online.</p>"
            : $"<p>เรียน คุณ{enc(a.SignerFirstName)}</p><p>แนบสำเนา{enc(heading.Title)} <b>{enc(number)}</b> ของ {enc(seller)} ฉบับที่ท่านลงนามออนไลน์</p>";
        var sent = await _email.TrySendWithAttachmentAsync(a.SignerEmail!, CustomerSignText.SignedCopySubject(heading.IsEnglish, heading.Title, number), body,
            file.Value.FileName, file.Value.Bytes, "application/pdf");
        return sent ? snap : new(false, true, "ส่งอีเมลสำเนาฉบับที่ลงนามไม่สำเร็จ");
    }

    // ═══════════════ ฝั่งพนักงาน ═══════════════

    /// <summary>หลักฐานการตอบรับของลูกค้าทั้งหมดของใบ (พนักงานที่มีสิทธิ์อ่านเอกสาร — ผู้เรียกตรวจสิทธิ์ + บันทึกการเข้าถึงข้อมูลส่วนบุคคล) ·
    /// IP แสดงเฉพาะฝั่งพนักงาน (ไม่พิมพ์บนกระดาษ/ไม่ส่งให้หน้าลูกค้า — 267-1-17)</summary>
    public async Task<CustomerDocEvidenceView> EvidenceForStaffAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var rows = await _db.CustomerDocumentActions.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.DocumentId == documentId)
            .OrderByDescending(a => a.CreatedAt).Take(100).ToListAsync(ct);
        var doc = await _db.Documents.AsNoTracking().Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => new { d.DocumentType, d.CustomerResponse, d.RevisionNumber }).FirstOrDefaultAsync(ct);
        var accepted = doc?.CustomerResponse == nameof(CustomerSignRule.Kind.Accepted);
        var stamp = accepted && CustomerSignRule.StampDutyWarn(doc!.DocumentType, CustomerSignRule.Kind.Accepted, await HasServiceLineAsync(companyId, documentId, ct));
        return new CustomerDocEvidenceView(
            rows.Select(a => new CustomerDocEvidenceItem(a.Id, a.Action,
                CustomerSignText.KindLabel(false, Enum.TryParse<CustomerSignRule.Kind>(a.Action, out var k) ? k : CustomerSignRule.Kind.Accepted),
                a.RevisionNumber, a.Channel, a.IdentityLevel, a.SignerFirstName, a.SignerLastName, a.SignerTitle, a.SignerOrgName, a.SignerEmail, a.EmailVerified,
                a.AttestationVersion, a.ReasonCode, CustomerSignText.ReasonLabel(false, a.ReasonCode), a.Message, a.ContentHash, a.Ip,
                a.SignatureSha256 != null, a.SignedPdfSha256, a.SignedPdfAt, a.CreatedAt, a.ErasedAt,
                IsCurrentRevision: doc != null && a.RevisionNumber == doc.RevisionNumber)).ToList(),
            doc?.CustomerResponse,
            accepted ? CustomerSignText.ConvertSuggestionText : null,
            accepted ? CustomerSignText.ConvertSuggestions.Select(t => t.ToString()).ToList() : new List<string>(),
            stamp ? CustomerSignText.StampDutyWarning : null, stamp ? CustomerSignText.StampDutyPage : null,
            CustomerSignText.LegalReference);
    }

    /// <summary>ใบนี้มีบรรทัดงานบริการไหม — สินค้าชนิดบริการ หรือบรรทัดพิมพ์เองที่ไม่ผูกสินค้า (ไม่รู้ชนิด ⇒ นับว่า "อาจเป็นบริการ" เพราะเป็นคำเตือนเท่านั้น)</summary>
    public async Task<bool> HasServiceLineAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var codes = await _db.DocumentLines.AsNoTracking()
            .Where(l => l.DocumentId == documentId && l.Document.CompanyId == companyId)
            .Select(l => l.ProductCode).ToListAsync(ct);
        if (codes.Count == 0) return false;
        if (codes.Any(string.IsNullOrWhiteSpace)) return true;
        var distinct = codes.Distinct().ToList();
        return await _db.Products.AsNoTracking()
            .AnyAsync(p => p.CompanyId == companyId && distinct.Contains(p.Code) && p.ProductType == ProductType.Service, ct);
    }

    // ═══════════════ ด่าน token (ย้ายจาก PublicQuotationController — รอบ 239/267 P1) ═══════════════

    /// <summary>ภาษาของเอกสาร (resolver กลางตัวเดียวกับกระดาษ) — ข้อความถึงผู้ลงนามใช้ภาษาเดียวกับหน้าเอกสาร</summary>
    private async Task<bool> IsEnglishAsync(Document doc)
    {
        try { return (await PdfGenerationService.ResolveDocumentHeadingAsync(_db, doc.CompanyId, doc.Id)).IsEnglish; }
        catch (KeyNotFoundException) { return false; }
    }

    /// <summary>ปุ่มนี้เปิดอยู่บนหน้าเอกสารของใบนี้ไหม ณ ตอนนี้ — ตัวตัดสินเดียวกับที่หน้าแสดง (<see cref="CustomerDocPage.Actions"/>) ·
    /// null = เปิด · ปิด = เหตุของปุ่ม (ไม่มีปุ่ม = ข้อความ "เอกสารถูกแก้ไข")</summary>
    private static string? PageActionClosedReason(Document doc, string actionId, bool en)
    {
        var respondedAt = doc.DocumentType == DocumentType.Quotation ? doc.QuotationAcceptedAt
            : doc.DocumentType == DocumentType.DeliveryNote ? doc.DeliverySignedAt : null;
        var actions = CustomerDocPage.Actions(new CustomerDocPage.Facts(
            doc.DocumentType, doc.Status, CustomerDocPage.Mode.Link, en, false, null, false,
            doc.DueDate, doc.SignLinkIssuedAt, respondedAt, CustomerDocPage.RespondPath(doc.CompanyId, "-"),
            PayOnly: CustomerDocPage.IsPayOnlyLink(doc.PayLinkIssuedAt, doc.PayLinkExpiresAt), CustomerResponse: doc.CustomerResponse), DateTime.UtcNow);
        var a = actions.FirstOrDefault(x => x.Id == actionId);
        if (a is { Enabled: true }) return null;
        return a?.DisabledReason
            ?? actions.FirstOrDefault(x => x.Id is CustomerDocPage.ActionAccept or CustomerDocPage.ActionSignDelivery)?.DisabledReason
            ?? CustomerDocPage.RespondStaleMessage;
    }

    /// <summary>รอบ 240: ประทับ "ลูกค้าเปิดลิงก์แล้ว" ครั้งแรก (ป้ายสถานะบนรายการเอกสาร) — นับจากการโหลดหน้าสาธารณะเท่านั้น · ล้มไม่กระทบการเปิดดู</summary>
    public async Task MarkOpenedAsync(Document doc)
    {
        if (doc.SignLinkOpenedAt != null || doc.QuotationAcceptedAt != null || doc.DeliverySignedAt != null) return;
        try
        {
            var now = DateTime.UtcNow;
            await _db.Documents
                .Where(d => d.Id == doc.Id && d.CompanyId == doc.CompanyId && d.SignLinkOpenedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.SignLinkOpenedAt, now));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ประทับเวลาเปิดลิงก์ไม่สำเร็จ doc={DocId}", doc.Id);
        }
    }

    /// <summary>ค่าตั้ง "ต้องเซ็นเพื่อยอมรับใบเสนอราคา" ของบริษัทเจ้าของใบ (ไม่มีแถว = ไม่บังคับ)</summary>
    public async Task<bool> RequireSignatureAsync(Guid companyId)
        => await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.RequireSignatureForQuotationAccept)
            .FirstOrDefaultAsync();

    /// <summary>ลบไฟล์ลายเซ็นที่เขียนแล้วแต่การยอมรับไม่เกิด (ไม่ใช่เส้นเงิน · ล้มแค่บันทึก — ไฟล์กำพร้าไม่มีแถวชี้ ไม่มีใครเปิดได้)</summary>
    private async Task TryDeleteSignatureAsync(string location)
    {
        try { await SignatureStore.DeleteAsync(location); }
        catch (Exception ex) { _logger.LogWarning(ex, "ลบไฟล์ลายเซ็นที่ไม่ได้ใช้ไม่สำเร็จ {Location}", location); }
    }

    /// <summary>ด่าน token ตัวเดียวของทุกเส้นใบเสนอราคาสาธารณะ (ดู · PDF · ลายเซ็น · ยอมรับ · ไม่รับ · ขอแก้) — รูปแบบ hex · ใบเสนอราคาที่ออกแล้ว
    /// (ไม่ร่าง/ไม่ยกเลิก/ไม่ถูกลบ) · ไม่หมดอายุ · การเทียบ token ทำในฐานด้วยดัชนี (ค้นแบบ equality ไม่ใช่วนเทียบในหน่วยความจำ)</summary>
    public async Task<Document?> FindByTokenAsync(string token)
    {
        // token = 64 hex — ตรวจรูปแบบก่อน กัน probing ด้วย input แปลก ๆ
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 80
            || !token.All(Uri.IsHexDigit))
            return null;

        // รอบ 267 P1 (267-1-3): ฐานไม่เก็บ token ดิบ — ค้นด้วย hash สองรุ่น: (ก) ลิงก์ยอมรับที่ออกก่อนรอบ 267 (คอลัมน์เดิมเก็บ h1:hash · หมดเองตามอายุ)
        // (ข) token ของหน้าเอกสารของใบ (ลิงก์เดียวต่อใบ)
        var legacyKey = CustomerDocPage.LegacyStored(token);
        var pageHash = DocumentPayLinkPolicy.TokenHash(token);
        var doc = await _db.Documents.AsNoTracking()
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => (d.QuotationAcceptToken == legacyKey || (d.PayLinkTokenHash == pageHash && d.PayLinkRevokedAt == null))
                && d.DocumentType == DocumentType.Quotation
                && !d.IsDeleted
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Draft);
        if (doc == null) return null;
        if (ViaPageToken(doc.QuotationAcceptToken, token))
        {
            if (!PageLinkLive(doc, token)) return null;
            await _db.HydrateContactAsync(doc.CompanyId, doc);
            return doc;
        }
        if (doc.QuotationAcceptTokenExpiresAt is { } exp && exp < DateTime.UtcNow) return null;
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate ผ่าน doc.CompanyId
        await _db.HydrateContactAsync(doc.CompanyId, doc);
        return doc;
    }

    public async Task<Document?> FindDeliveryByTokenAsync(string token, bool track = false)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 80
            || !token.All(Uri.IsHexDigit))
            return null;

        var q = track ? _db.Documents.AsQueryable() : _db.Documents.AsNoTracking();
        // รอบ 267 P1 (267-1-3): ค้นด้วย hash สองรุ่น เหมือน FindByTokenAsync (ลิงก์เซ็นรับรุ่นก่อน · token ของหน้าเอกสาร)
        var legacyKey = CustomerDocPage.LegacyStored(token);
        var pageHash = DocumentPayLinkPolicy.TokenHash(token);
        var doc = await q
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => (d.DeliverySignToken == legacyKey || (d.PayLinkTokenHash == pageHash && d.PayLinkRevokedAt == null))
                && d.DocumentType == DocumentType.DeliveryNote
                && !d.IsDeleted
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Draft);
        if (doc == null) return null;
        if (ViaPageToken(doc.DeliverySignToken, token))
        {
            if (!PageLinkLive(doc, token)) return null;
            await _db.HydrateContactAsync(doc.CompanyId, doc);
            return doc;
        }
        if (doc.DeliverySignTokenExpiresAt is { } exp && exp < DateTime.UtcNow) return null;
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate ผ่าน doc.CompanyId
        await _db.HydrateContactAsync(doc.CompanyId, doc);
        return doc;
    }

    /// <summary>คำขอนี้มาด้วย token ของหน้าเอกสาร (ไม่ใช่ลิงก์ยอมรับ/เซ็นรับรุ่นก่อน) — ค่าในคอลัมน์รุ่นก่อนไม่ตรง hash ของ token ที่ถือมา</summary>
    public static bool ViaPageToken(string? legacyStored, string token)
    {
        return !string.Equals(legacyStored, CustomerDocPage.LegacyStored(token), StringComparison.Ordinal);
    }

    /// <summary>token ของหน้าเอกสารยังใช้ได้กับใบนี้ไหม — ยืนยัน hash เวลาคงที่ · ไม่เพิกถอน/ไม่หมดอายุ · ใบออกเลขแล้ว (ตัวตัดสินลิงก์เดียวกับหน้าเอกสาร)</summary>
    private static bool PageLinkLive(Document doc, string token)
    {
        return DocumentPayLinkPolicy.TokenMatches(token, doc.PayLinkTokenHash)
               && DocumentPayLinkPolicy.LinkUsable(doc.PayLinkTokenHash, doc.PayLinkExpiresAt, doc.PayLinkRevokedAt, DateTime.UtcNow, null)
               && DocumentStatusRules.IsIssued(doc.Status);
    }

    /// <summary>เหตุที่ตอบรับไม่ได้เพราะพ้นช่วง — ตัวตัดสินเดียว <see cref="CustomerDocPage.RespondWindowClosedReason"/></summary>
    /// <remarks>ฝ่ายค้าน 267 M1: token ของหน้าเอกสาร ⇒ ต้องมี <c>SignLinkIssuedAt</c> (ผู้ขายส่งให้ตอบรับแล้ว) — ไม่ถอยไปใช้วันออกลิงก์ ·
    /// ฝ่ายค้าน 267 L3: ลิงก์รุ่นก่อน ⇒ ตรวจเฉพาะวันยืนราคา (จุดเริ่ม = ตอนนี้ — วันหมดอายุของลิงก์รุ่นก่อนตรวจที่ตัวค้นแล้ว)</remarks>
    private static string? RespondClosedReason(Document doc, bool viaPage)
    {
        var now = DateTime.UtcNow;
        return CustomerDocPage.RespondWindowClosedReason(false, doc.DocumentType, doc.DueDate, viaPage ? doc.SignLinkIssuedAt : now, now);
    }

    /// <summary>วันสิ้นสุดที่หน้าตอบรับรุ่นก่อนแสดง — ลิงก์รุ่นก่อน = วันหมดอายุของตัวเอง · token ของหน้าเอกสาร = สิ้นสุดช่วงตอบรับ</summary>
    public static DateTime? RespondEnd(Document doc, string token)
        => ViaPageToken(doc.QuotationAcceptToken, token)
            ? CustomerDocPage.RespondWindowEnd(doc.DocumentType, doc.DueDate, doc.SignLinkIssuedAt)
            : doc.QuotationAcceptTokenExpiresAt;
}
