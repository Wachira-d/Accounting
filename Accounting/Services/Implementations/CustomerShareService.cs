using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Email;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 267 แพ็ก P3 — <b>"📤 ส่งให้ลูกค้า"</b>: หน้าต่างเดียวแทน 6 ปุ่มเดิม (คำตัดสิน 267-1-61…67 · DOCUMENT_FLOW §2.12n-P3) ·
/// <b>orchestrate บริการเดิมเท่านั้น — ไม่ประกอบเส้นส่งเอง</b> (ล็อกด้วย <c>tools/required_call_site_check.py</c> R267c):
/// <list type="number">
/// <item>ลิงก์ = ตัวออกเดียว <see cref="IDocumentPayLinkService.EnsureCustomerLinkAsync"/> (ลิงก์ที่ใช้ได้ไม่ถูกทับ · ถูกยกเลิก ⇒ ออกใหม่เฉพาะผู้ใช้ติ๊กตั้งใจ — 259-2-8)</item>
/// <item>เปิดช่วงตอบรับ = <see cref="ISignRequestService.IssueLinkAsync"/> (ลิงก์ดู ≠ สิทธิ์ตอบรับ — 267-1-25)</item>
/// <item>อีเมล = <see cref="IDocumentEmailService.SendDocumentRoutedAsync"/> (ทางเลือก e-Tax by Email ตัวเดียว · บล็อกลิงก์หน้าเอกสาร)</item>
/// <item>LINE = <see cref="IDocumentLineSendService.SendAsync"/> (ลิงก์หน้าเอกสารต่อปลายทางตาม <see cref="DocumentLineSendRule.PageLinkAllowed"/>)</item>
/// </list>
/// <b>ผลแยกต่อช่องทาง</b> — อีเมลล้มไม่ลาก LINE (และกลับกัน) · ทุกช่องทางบอกผลของตัวเองในคำตอบ (F2 ข้อ 7) · "เข้าคิวแล้ว" ≠ "ส่งแล้ว"
/// </summary>
public class CustomerShareService : ICustomerShareService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentPayLinkService _links;
    private readonly IDocumentEmailService _email;
    private readonly IDocumentLineSendService _line;
    private readonly ISignRequestService? _sign;
    private readonly ILogger<CustomerShareService> _logger;

    public CustomerShareService(AccountingDbContext db, IDocumentPayLinkService links, IDocumentEmailService email, IDocumentLineSendService line,
        ILogger<CustomerShareService> logger, ISignRequestService? sign = null)
    {
        _db = db; _links = links; _email = email; _line = line; _logger = logger; _sign = sign;
    }

    private sealed record DocRow(Guid Id, Guid ContactId, DocumentType Type, DocumentStatus Status, string Number, decimal BalanceDue,
        string? PayLinkTokenHash, DateTime? PayLinkExpiresAt, DateTime? PayLinkRevokedAt);

    private Task<DocRow?> LoadAsync(Guid companyId, Guid documentId, CancellationToken ct)
        => _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new DocRow(d.Id, d.ContactId, d.DocumentType, d.Status, d.DocumentNumber, d.BalanceDue,
                d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt))
            .FirstOrDefaultAsync(ct);

    // ═══════════════ หน้าต่าง ═══════════════

    public async Task<CustomerShareOptions> OptionsAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var d = await LoadAsync(companyId, documentId, ct) ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var now = DateTime.UtcNow;
        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, d.Id);
        var refusal = await _links.CustomerLinkRefusalAsync(companyId, documentId, ct);
        var state = CustomerDocPage.StateOf(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, now);
        var url = state == CustomerDocPage.LinkState.Usable ? await _links.CurrentCustomerUrlAsync(companyId, documentId, ct) : null;
        var linkNote = state switch
        {
            CustomerDocPage.LinkState.Revoked => "ลิงก์ของใบนี้ถูกยกเลิกไว้ — ติ๊ก \"ออกลิงก์ใหม่แทน\" ถ้าตั้งใจส่งใหม่ (ลิงก์เดิมใช้ไม่ได้ต่อ)",
            CustomerDocPage.LinkState.Expired => "ลิงก์เดิมหมดอายุแล้ว — ส่งครั้งนี้ระบบออกลิงก์ใหม่ให้",
            CustomerDocPage.LinkState.None => "ยังไม่มีลิงก์ — ส่งครั้งแรกระบบออกลิงก์ให้ (ลิงก์เดียวต่อใบ)",
            _ => "ใช้ลิงก์เดิมของใบนี้ (ลิงก์ที่ส่งไปก่อนหน้ายังใช้ได้)",
        };
        // ฝ่ายค้าน 267-P3 MED-3: ออกลิงก์หน้าเอกสารไม่ได้ ⇒ ยังส่ง PDF/อีเมล/LINE แบบเดิมได้ (ไม่มีลิงก์หน้าเอกสาร) — บอกเหตุตรง ๆ
        if (refusal != null) linkNote = CustomerShareRule.NoPageLinkNote(refusal);
        var link = new CustomerShareLink(state.ToString(), url, state == CustomerDocPage.LinkState.Usable ? d.PayLinkExpiresAt : null, refusal, linkNote,
            ReissueOffered: state == CustomerDocPage.LinkState.Revoked && refusal == null);

        // ปุ่มที่ลูกค้าจะเห็น — view model เดียวกับหน้าลูกค้า (โหมดพรีวิว: ไม่ออกลิงก์ · ไม่ประทับเปิด)
        var preview = refusal == null ? await _links.PageForDocumentAsync(companyId, documentId, CustomerDocPage.Mode.Preview, ct) : null;
        var actions = (preview?.Page?.Actions ?? Array.Empty<CustomerDocPage.PageAction>())
            .Select(a => new CustomerShareAction(a.Id, a.Label)).ToList();

        var full = await _db.Documents.AsNoTracking().FirstAsync(x => x.Id == d.Id && x.CompanyId == companyId, ct);
        var sign = SignRequestKinds.Read(full);
        var respond = CustomerShareRule.Respond(d.Type, d.Status, SignRequestKinds.LinkUsable(sign, now), sign.SignedAt);
        if (refusal != null && respond.Applies) respond = respond with { Offered = false, Note = CustomerShareRule.NoPageLinkNote(refusal) };

        // อีเมล — ทางเลือกตัวเดียว (e-Tax by Email ⇒ ไม่มีบล็อกลิงก์) · ผู้รับตั้งต้น = อีเมลผู้ติดต่อ
        var route = await _email.RouteAsync(companyId, documentId);
        var carries = CustomerShareRule.EmailCarriesPageLink(route.Channel);
        IReadOnlyList<string> problems = Array.Empty<string>(), warnings = Array.Empty<string>();
        string? defaultTo = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == d.ContactId && c.CompanyId == companyId).Select(c => c.Email).FirstOrDefaultAsync(ct);
        if (route.Channel != EtaxEmailChannel.PlainEmail)
        {
            var p = await _email.PreviewEtaxEmailAsync(companyId, documentId);
            problems = p.Problems; warnings = p.Warnings;
            defaultTo ??= p.DefaultTo;
        }

        var line = await _line.OptionsAsync(companyId, documentId, ct, forShare: true);
        var pageInEmail = carries && refusal == null;
        var email = new CustomerShareEmail(route.Channel.ToString(), route.Reason, string.IsNullOrWhiteSpace(defaultTo) ? null : defaultTo!.Trim(),
            pageInEmail, !carries ? CustomerShareRule.EtaxNoPageLinkNote : refusal != null ? CustomerShareRule.NoPageLinkNote(refusal) : null,
            carries && line.PayLinkOffered, carries ? line.PayLinkNote : CustomerShareRule.EtaxNoPageLinkNote, problems, warnings);

        var channels = CustomerShareRule.Channels.Select(c => c.Code switch
        {
            CustomerShareRule.ChannelLine => new CustomerShareChannelOption(c.Code, c.Label, c.Hint, line.CanSend, line.Problem),
            CustomerShareRule.ChannelEmail => new CustomerShareChannelOption(c.Code, c.Label, c.Hint, problems.Count == 0,
                problems.Count == 0 ? null : string.Join(" · ", problems)),
            // คัดลอกลิงก์ / QR ต้องมีลิงก์หน้าเอกสาร
            _ => new CustomerShareChannelOption(c.Code, c.Label, c.Hint, refusal == null, refusal == null ? null : CustomerShareRule.NoPageLinkNote(refusal)),
        }).ToList();
        var lineTargets = refusal == null ? line.Targets
            : line.Targets.Select(t => t with { PageLink = false, PageLinkNote = t.Available ? CustomerShareRule.NoPageLinkNote(refusal) : t.PageLinkNote }).ToList();

        var status = await _links.StatusAsync(companyId, documentId, ct);
        var postPay = new CustomerSharePostPay(status.PostPaySettingApplies, status.PostPayReceiptMode, status.PostPayPartialMode, status.PostPayEffectiveLabel);

        var warn = new List<string>();
        if (preview != null && preview.Page?.ShowAmounts == false) warn.Add("หน้าเอกสารของใบส่งของไม่แสดงราคา/ยอด (รายการ + จำนวนเท่านั้น)");
        // ส่งได้ = มีช่องทางใดช่องทางหนึ่งใช้ได้ (ลิงก์หน้าเอกสาร หรือการส่งแบบเดิม)
        var canShare = channels.Any(c => c.Available);
        return new CustomerShareOptions(d.Id, d.Number, heading.Title, canShare, canShare ? null : refusal ?? "ใบนี้ยังส่งให้ลูกค้าไม่ได้", link, actions, respond, channels, email,
            new CustomerShareLine(line.Problem, lineTargets, line.PayLinkOffered, line.PayLinkNote), postPay, warn);
    }

    // ═══════════════ ส่ง ═══════════════

    public async Task<CustomerShareResult> ShareAsync(Guid companyId, Guid documentId, CustomerShareRequest request, Guid? userId, string? actor, string origin,
        CancellationToken ct = default)
    {
        var (channels, channelProblem) = CustomerShareRule.ParseChannels(request?.Channels);
        if (channelProblem != null) throw new BusinessRuleException(channelProblem, CustomerShareRule.RuleCode, 400);
        var req = request!;
        var d = await LoadAsync(companyId, documentId, ct) ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        // 1) ลิงก์ (ตัวออกเดียว) — ฝ่ายค้าน 267-P3 MED-3: ออกไม่ได้ / ถูกยกเลิกไว้โดยไม่ติ๊ก "ออกใหม่แทน" ⇒ ไม่ปฏิเสธทั้งคำขอ:
        //    อีเมล/LINE ส่งแบบเดิม (ไม่มีลิงก์หน้าเอกสาร) + บอกเหตุในผลทุกช่องทาง · คัดลอกลิงก์/QR = ล้มพร้อมเหตุ
        CustomerLinkResult? link = null;
        string? noPage = null;
        try
        {
            var r = await _links.EnsureCustomerLinkAsync(companyId, documentId, "share", ct, allowReissueRevoked: req.ReissueRevoked, actor: actor,
                fallbackOrigin: origin);
            if (r.Ok && r.Url != null) link = r; else noPage = r.Refusal ?? "ออกลิงก์หน้าเอกสารไม่สำเร็จ";
        }
        catch (PayLinkRevokedException)
        {
            noPage = "ลิงก์ของใบนี้ถูกยกเลิกไว้ (ติ๊ก \"ออกลิงก์ใหม่แทน\" ถ้าตั้งใจส่งลิงก์ใหม่)";
        }
        var notes = new List<string>();
        if (noPage != null) notes.Add(CustomerShareRule.NoPageLinkNote(noPage));

        // 2) เปิดช่วงตอบรับ (ผู้ใช้ติ๊ก · ต้องมีลิงก์หน้าเอกสาร) — ล้ม ⇒ ไม่ส่ง "ขอให้ยอมรับ" ที่ลูกค้ากดไม่ได้ (ปฏิเสธทั้งคำขอพร้อมเหตุ)
        if (req.OpenRespond && SignRequestKinds.For(d.Type) != null)
        {
            if (link == null) notes.Add("ไม่ได้เปิดให้ลูกค้าตอบรับ — ครั้งนี้ไม่มีลิงก์หน้าเอกสาร");
            else
            {
                if (_sign == null) throw new BusinessRuleException("บริการลิงก์ให้ลูกค้าตอบรับยังไม่พร้อม", CustomerShareRule.RuleCode, 503);
                var full = await _db.Documents.AsNoTracking().FirstAsync(x => x.Id == d.Id && x.CompanyId == companyId, ct);
                if (!SignRequestKinds.LinkUsable(SignRequestKinds.Read(full), DateTime.UtcNow))
                    await _sign.IssueLinkAsync(companyId, documentId, new IssueSignLinkOptions(Regenerate: false, StaffIntent: true), origin, ct);
            }
        }

        // 3) ช่องทาง — ทีละช่องทาง · ผลแยกกัน (ช่องทางหนึ่งล้มไม่หยุดช่องทางถัดไป)
        var outcomes = new List<CustomerShareRule.ChannelOutcome>();
        string? qr = null;
        foreach (var ch in channels)
        {
            switch (ch)
            {
                case CustomerShareRule.ChannelCopy:
                    outcomes.Add(link != null ? CustomerShareRule.Ok(ch, "ลิงก์พร้อมคัดลอก") : CustomerShareRule.Failed(ch, CustomerShareRule.NoPageLinkNote(noPage!)));
                    break;
                case CustomerShareRule.ChannelQr:
                    if (link != null) { qr = QrDataUri(link.Url!); outcomes.Add(CustomerShareRule.Ok(ch, "QR พร้อมพิมพ์")); }
                    else outcomes.Add(CustomerShareRule.Failed(ch, CustomerShareRule.NoPageLinkNote(noPage!)));
                    break;
                case CustomerShareRule.ChannelEmail:
                    outcomes.Add(await SendEmailAsync(companyId, documentId, req, actor, link != null, noPage));
                    break;
                case CustomerShareRule.ChannelLine:
                    outcomes.Add(await SendLineAsync(companyId, documentId, req, userId, actor, ct, link != null, noPage));
                    break;
            }
        }
        var (allOk, message) = CustomerShareRule.Summary(outcomes);
        if (notes.Count > 0) message = string.Join(" · ", notes) + " — " + message;

        // ฝ่ายค้าน 267-P3 LOW-4: ร่องรอยของคำสั่ง "ส่งให้ลูกค้า" เอง (ใครแจกลิงก์/QR เมื่อไร · ช่องทาง · ผล) — audit chain ตัวเดียว
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, EntityType = "Document", EntityId = documentId.ToString(), Action = AuditAction.Export,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = CustomerShareRule.AuditAction, ruleCode = CustomerShareRule.RuleCode, by = actor, pageLink = link != null, noPageLinkReason = noPage,
                openRespond = req.OpenRespond, outcomes = outcomes.Select(o => new { o.Channel, o.Status }),
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return new CustomerShareResult(allOk, message, link?.Url, link?.ExpiresAt, qr, outcomes, noPage == null ? null : CustomerShareRule.NoPageLinkNote(noPage));
    }

    /// <summary>อีเมล — ทางเข้าเดียว <see cref="IDocumentEmailService.SendDocumentRoutedAsync"/> · ล้ม ⇒ ผลของช่องทางนี้เป็น "ล้ม" พร้อมเหตุ (ไม่โยนต่อ — LINE ยังส่ง)</summary>
    private async Task<CustomerShareRule.ChannelOutcome> SendEmailAsync(Guid companyId, Guid documentId, CustomerShareRequest req, string? actor,
        bool hasPageLink, string? noPage)
    {
        const string ch = CustomerShareRule.ChannelEmail;
        var to = (req.EmailTo ?? "").Trim();
        if (to.Length == 0) return CustomerShareRule.Failed(ch, "ไม่มีอีเมลผู้รับ — ใส่อีเมลลูกค้าแล้วส่งใหม่");
        try
        {
            var route = await _email.RouteAsync(companyId, documentId);
            var carries = CustomerShareRule.EmailCarriesPageLink(route.Channel);
            var page = carries && hasPageLink;
            var log = await _email.SendDocumentRoutedAsync(companyId, documentId,
                new SendDocumentEmailRequest(to, string.IsNullOrWhiteSpace(req.EmailCc) ? null : req.EmailCc.Trim(), null, null, null,
                    AttachPdf: req.AttachPdf || !page, IncludePayLink: carries && req.EmailPayLink, IncludePageLink: page), actor);
            var tail = carries && !hasPageLink && noPage != null ? " · " + CustomerShareRule.NoPageLinkNote(noPage) + " (แนบ PDF แทน)" : "";
            if (log.Status == EmailLogStatus.Sent)
                return CustomerShareRule.Ok(ch, CustomerShareRule.EmailSentMessage(log.IsEtaxByEmail, log.IncludedRdTimestamp) + " ถึง " + to + tail);
            return CustomerShareRule.Failed(ch, "ส่งอีเมลไม่สำเร็จ — " + (log.ErrorMessage ?? "ผู้ให้บริการอีเมลไม่รับ") + tail);
        }
        catch (DbUpdateException ex)
        {
            // ฝ่ายค้าน 267-P3 LOW-3: บันทึกผลอีเมลล้มหลังผู้ให้บริการอาจรับแล้ว ⇒ ไม่บอกว่า "ส่งไม่สำเร็จ" เฉย ๆ (ผู้ใช้จะส่งซ้ำ)
            _logger.LogError(ex, "บันทึกผลส่งอีเมล (ส่งให้ลูกค้า) ไม่สำเร็จ doc={DocumentId} company={CompanyId}", documentId, companyId);
            return CustomerShareRule.Failed(ch, "บันทึกผลส่งอีเมลไม่สำเร็จ — อีเมลอาจออกไปแล้ว ตรวจกล่องขาออก/บันทึกอีเมลของเอกสารก่อนส่งซ้ำ");
        }
        catch (Exception ex) when (ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException)
        {
            return CustomerShareRule.Failed(ch, "ส่งอีเมลไม่สำเร็จ — " + ex.Message, (ex as BusinessRuleException)?.RuleCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ส่งให้ลูกค้าทางอีเมลล้ม doc={DocumentId} company={CompanyId}", documentId, companyId);
            return CustomerShareRule.Failed(ch, "ส่งอีเมลไม่สำเร็จ — ข้อผิดพลาดของระบบ (ลองใหม่ หรือดูบันทึกอีเมลของเอกสาร)");
        }
        finally
        {
            // ฝ่ายค้าน 267-P3 LOW-3: อีเมลกับ LINE ใช้ context เดียวกัน — แถวที่เส้นอีเมลค้างไว้ (บันทึกไม่สำเร็จ) ต้องไม่ถูกบันทึกซ้ำ/ลาก LINE ล้มตาม
            DetachPending();
        }
    }

    /// <summary>ถอดทุกแถวที่ค้าง (Added/Modified/Deleted) ออกจาก context — เส้นอีเมลบันทึกของตัวเองแล้วเมื่อสำเร็จ · ที่ค้างคือของที่บันทึกไม่สำเร็จ</summary>
    private void DetachPending()
    {
        foreach (var e in _db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
            e.State = EntityState.Detached;
    }

    /// <summary>LINE — ทางเข้าเดียว <see cref="IDocumentLineSendService.SendAsync"/> (ลิงก์หน้าเอกสารต่อปลายทาง) · ล้ม ⇒ ผลของช่องทางนี้เป็น "ล้ม" พร้อมเหตุ</summary>
    private async Task<CustomerShareRule.ChannelOutcome> SendLineAsync(Guid companyId, Guid documentId, CustomerShareRequest req, Guid? userId, string? actor,
        CancellationToken ct, bool hasPageLink, string? noPage)
    {
        const string ch = CustomerShareRule.ChannelLine;
        try
        {
            var r = await _line.SendAsync(companyId, documentId, req.LineEndpointIds, req.LinePayLink, userId, actor, ct, includePageLink: hasPageLink, fromShare: true);
            var notes = (r.Endpoints ?? Array.Empty<DocumentLineSendRule.EndpointOutcome>())
                .Where(e => e.Note != null).Select(e => $"{e.Display}: {e.Note}").ToList();
            if (!hasPageLink && noPage != null) notes.Add(CustomerShareRule.NoPageLinkNote(noPage) + " (การ์ดแบบเดิม)");
            return CustomerShareRule.Queued(ch, string.Join(" · ", new[] { r.Message }.Concat(notes)));
        }
        catch (Exception ex) when (ex is BusinessRuleException or PayLinkRevokedException)
        {
            return CustomerShareRule.Failed(ch, "ส่งทาง LINE ไม่ได้ — " + ex.Message, (ex as BusinessRuleException)?.RuleCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ส่งให้ลูกค้าทาง LINE ล้ม doc={DocumentId} company={CompanyId}", documentId, companyId);
            return CustomerShareRule.Failed(ch, "ส่งทาง LINE ไม่ได้ — ข้อผิดพลาดของระบบ (ลองใหม่ หรือดูคิว LINE)");
        }
    }

    private static string QrDataUri(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(6));
    }
}
