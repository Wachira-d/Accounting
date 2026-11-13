using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Email;
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
        var link = new CustomerShareLink(state.ToString(), url, state == CustomerDocPage.LinkState.Usable ? d.PayLinkExpiresAt : null, refusal, linkNote,
            ReissueOffered: state == CustomerDocPage.LinkState.Revoked && refusal == null);

        // ปุ่มที่ลูกค้าจะเห็น — view model เดียวกับหน้าลูกค้า (โหมดพรีวิว: ไม่ออกลิงก์ · ไม่ประทับเปิด)
        var preview = refusal == null ? await _links.PageForDocumentAsync(companyId, documentId, CustomerDocPage.Mode.Preview, ct) : null;
        var actions = (preview?.Page?.Actions ?? Array.Empty<CustomerDocPage.PageAction>())
            .Select(a => new CustomerShareAction(a.Id, a.Label)).ToList();

        var full = await _db.Documents.AsNoTracking().FirstAsync(x => x.Id == d.Id && x.CompanyId == companyId, ct);
        var sign = SignRequestKinds.Read(full);
        var respond = CustomerShareRule.Respond(d.Type, d.Status, SignRequestKinds.LinkUsable(sign, now), sign.SignedAt);

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
        var email = new CustomerShareEmail(route.Channel.ToString(), route.Reason, string.IsNullOrWhiteSpace(defaultTo) ? null : defaultTo!.Trim(),
            carries, carries ? null : CustomerShareRule.EtaxNoPageLinkNote,
            carries && line.PayLinkOffered, carries ? line.PayLinkNote : CustomerShareRule.EtaxNoPageLinkNote, problems, warnings);

        var channels = CustomerShareRule.Channels.Select(c => c.Code switch
        {
            CustomerShareRule.ChannelLine => new CustomerShareChannelOption(c.Code, c.Label, c.Hint, line.CanSend, line.Problem),
            CustomerShareRule.ChannelEmail => new CustomerShareChannelOption(c.Code, c.Label, c.Hint, problems.Count == 0,
                problems.Count == 0 ? null : string.Join(" · ", problems)),
            _ => new CustomerShareChannelOption(c.Code, c.Label, c.Hint, true, null),
        }).ToList();

        var status = await _links.StatusAsync(companyId, documentId, ct);
        var postPay = new CustomerSharePostPay(status.PostPaySettingApplies, status.PostPayReceiptMode, status.PostPayPartialMode, status.PostPayEffectiveLabel);

        var warn = new List<string>();
        if (preview != null && preview.Page?.ShowAmounts == false) warn.Add("หน้าเอกสารของใบส่งของไม่แสดงราคา/ยอด (รายการ + จำนวนเท่านั้น)");
        return new CustomerShareOptions(d.Id, d.Number, heading.Title, refusal == null, refusal, link, actions, respond, channels, email,
            new CustomerShareLine(line.Problem, line.Targets, line.PayLinkOffered, line.PayLinkNote), postPay, warn);
    }

    // ═══════════════ ส่ง ═══════════════

    public async Task<CustomerShareResult> ShareAsync(Guid companyId, Guid documentId, CustomerShareRequest request, Guid? userId, string? actor, string origin,
        CancellationToken ct = default)
    {
        var (channels, channelProblem) = CustomerShareRule.ParseChannels(request?.Channels);
        if (channelProblem != null) throw new BusinessRuleException(channelProblem, CustomerShareRule.RuleCode, 400);
        var req = request!;
        var d = await LoadAsync(companyId, documentId, ct) ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        // 1) ลิงก์ (ตัวออกเดียว) — ออกไม่ได้ ⇒ ไม่มีช่องทางใดทำงานได้ ⇒ ปฏิเสธทั้งคำขอก่อนส่งอะไรออกไป
        CustomerLinkResult link;
        try
        {
            link = await _links.EnsureCustomerLinkAsync(companyId, documentId, "share", ct, allowReissueRevoked: req.ReissueRevoked, actor: actor,
                fallbackOrigin: origin);
        }
        catch (PayLinkRevokedException)
        {
            throw new BusinessRuleException("ลิงก์ของใบนี้ถูกยกเลิกไว้ — ติ๊ก \"ออกลิงก์ใหม่แทน\" ถ้าตั้งใจส่งใหม่ (ไม่มีช่องทางใดถูกส่ง)",
                CustomerShareRule.RuleCode, 409);
        }
        if (!link.Ok) throw new BusinessRuleException(link.Refusal ?? "ออกลิงก์หน้าเอกสารไม่สำเร็จ — ไม่มีช่องทางใดถูกส่ง", CustomerShareRule.RuleCode, 409);

        // 2) เปิดช่วงตอบรับ (ผู้ใช้ติ๊ก) — ล้ม ⇒ ไม่ส่ง "ขอให้ยอมรับ" ที่ลูกค้ากดไม่ได้ (ปฏิเสธทั้งคำขอพร้อมเหตุ)
        if (req.OpenRespond && SignRequestKinds.For(d.Type) != null)
        {
            if (_sign == null) throw new BusinessRuleException("บริการลิงก์ให้ลูกค้าตอบรับยังไม่พร้อม", CustomerShareRule.RuleCode, 503);
            var full = await _db.Documents.AsNoTracking().FirstAsync(x => x.Id == d.Id && x.CompanyId == companyId, ct);
            if (!SignRequestKinds.LinkUsable(SignRequestKinds.Read(full), DateTime.UtcNow))
                await _sign.IssueLinkAsync(companyId, documentId, new IssueSignLinkOptions(Regenerate: false, StaffIntent: true), origin, ct);
        }

        // 3) ช่องทาง — ทีละช่องทาง · ผลแยกกัน (ช่องทางหนึ่งล้มไม่หยุดช่องทางถัดไป)
        var outcomes = new List<CustomerShareRule.ChannelOutcome>();
        string? qr = null;
        foreach (var ch in channels)
        {
            switch (ch)
            {
                case CustomerShareRule.ChannelCopy:
                    outcomes.Add(CustomerShareRule.Ok(ch, "ลิงก์พร้อมคัดลอก"));
                    break;
                case CustomerShareRule.ChannelQr:
                    qr = QrDataUri(link.Url!);
                    outcomes.Add(CustomerShareRule.Ok(ch, "QR พร้อมพิมพ์"));
                    break;
                case CustomerShareRule.ChannelEmail:
                    outcomes.Add(await SendEmailAsync(companyId, documentId, req, actor));
                    break;
                case CustomerShareRule.ChannelLine:
                    outcomes.Add(await SendLineAsync(companyId, documentId, req, userId, actor, ct));
                    break;
            }
        }
        var (allOk, message) = CustomerShareRule.Summary(outcomes);
        return new CustomerShareResult(allOk, message, link.Url, link.ExpiresAt, qr, outcomes);
    }

    /// <summary>อีเมล — ทางเข้าเดียว <see cref="IDocumentEmailService.SendDocumentRoutedAsync"/> · ล้ม ⇒ ผลของช่องทางนี้เป็น "ล้ม" พร้อมเหตุ (ไม่โยนต่อ — LINE ยังส่ง)</summary>
    private async Task<CustomerShareRule.ChannelOutcome> SendEmailAsync(Guid companyId, Guid documentId, CustomerShareRequest req, string? actor)
    {
        const string ch = CustomerShareRule.ChannelEmail;
        var to = (req.EmailTo ?? "").Trim();
        if (to.Length == 0) return CustomerShareRule.Failed(ch, "ไม่มีอีเมลผู้รับ — ใส่อีเมลลูกค้าแล้วส่งใหม่");
        try
        {
            var route = await _email.RouteAsync(companyId, documentId);
            var carries = CustomerShareRule.EmailCarriesPageLink(route.Channel);
            var log = await _email.SendDocumentRoutedAsync(companyId, documentId,
                new SendDocumentEmailRequest(to, string.IsNullOrWhiteSpace(req.EmailCc) ? null : req.EmailCc.Trim(), null, null, null,
                    AttachPdf: req.AttachPdf || !carries, IncludePayLink: carries && req.EmailPayLink, IncludePageLink: carries), actor);
            if (log.Status == EmailLogStatus.Sent)
                return CustomerShareRule.Ok(ch, (log.IsEtaxByEmail ? "ส่ง e-Tax by Email แล้ว (รอประทับเวลา)" : "ส่งอีเมลแล้ว") + " ถึง " + to);
            return CustomerShareRule.Failed(ch, "ส่งอีเมลไม่สำเร็จ — " + (log.ErrorMessage ?? "ผู้ให้บริการอีเมลไม่รับ"));
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
    }

    /// <summary>LINE — ทางเข้าเดียว <see cref="IDocumentLineSendService.SendAsync"/> (ลิงก์หน้าเอกสารต่อปลายทาง) · ล้ม ⇒ ผลของช่องทางนี้เป็น "ล้ม" พร้อมเหตุ</summary>
    private async Task<CustomerShareRule.ChannelOutcome> SendLineAsync(Guid companyId, Guid documentId, CustomerShareRequest req, Guid? userId, string? actor,
        CancellationToken ct)
    {
        const string ch = CustomerShareRule.ChannelLine;
        try
        {
            var r = await _line.SendAsync(companyId, documentId, req.LineEndpointIds, req.LinePayLink, userId, actor, ct, includePageLink: true);
            var notes = (r.Endpoints ?? Array.Empty<DocumentLineSendRule.EndpointOutcome>())
                .Where(e => e.Note != null).Select(e => $"{e.Display}: {e.Note}");
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
