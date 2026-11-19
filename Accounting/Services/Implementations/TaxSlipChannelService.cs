using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 272 — <b>ช่องทางของเส้นสลิปภาษี</b> (ตัวเดียว): กลุ่ม/อีเมลของผู้ติดต่อ <c>NextAccTenant</c> ในบริษัทผู้ให้บริการ ⇄ บริษัทลูกค้า ·
/// ส่งการ์ดผลกลับ (reply ฟรีก่อน · ไม่มี replyToken ⇒ push ผ่าน OA กลาง · อีเมลผ่านตัวส่งอีเมลเดิม) และ<b>ประทับผลการส่งบนแถวสลิปทุกครั้ง</b>
/// (การ์ดส่งไม่ได้ = เห็นบนแถว + คิวพนักงาน ไม่ใช่เงียบ · ดีไซน์ §6.1) · ทุก query ผูก <c>CompanyId</c> ของบริษัทผู้ให้บริการ
/// </summary>
public class TaxSlipChannelService
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ILineNotifyService _line;
    private readonly ICustomerLineLinkService _links;
    private readonly IEmailService? _email;
    private readonly ILogger<TaxSlipChannelService> _logger;

    public TaxSlipChannelService(AccountingDbContext db, ISecretProtector secrets, ILineNotifyService line, ICustomerLineLinkService links,
        ILogger<TaxSlipChannelService> logger, IEmailService? email = null)
    { _db = db; _secrets = secrets; _line = line; _links = links; _logger = logger; _email = email; }

    /// <summary>ผลการผูกของช่องทางหนึ่ง — ผู้ติดต่อ · บริษัทลูกค้า (จาก <c>ExternalId</c> ของผู้ติดต่อ NextAccTenant) · แสดงยอดได้ไหม (ทุกปลายทางต้องเปิด)</summary>
    public sealed record Binding(IReadOnlyList<Guid> ContactIds, IReadOnlyList<Guid> ClientCompanyIds, bool ShowAmounts, bool AnyEndpoint);

    /// <summary>กลุ่ม LINE (แฮช <c>LineIdHash</c>) ที่ผูกกับผู้ติดต่อของบริษัทผู้ให้บริการผ่าน OA กลาง</summary>
    public async Task<Binding> GroupBindingAsync(Guid providerId, string idHash, CancellationToken ct = default)
    {
        var eps = await _db.ContactNotifyEndpoints.AsNoTracking()
            .Where(e => e.CompanyId == providerId && e.Kind == NotifyEndpointKind.LineGroup && e.LineIdHash == idHash && e.UnlinkedAt == null
                && e.OaScope == NotifyEndpointKind.ScopeNextAcc)
            .Select(e => new { e.ContactId, e.ShowAmounts }).ToListAsync(ct);
        return await BindingFromAsync(providerId, eps.Select(e => (e.ContactId, e.ShowAmounts)).ToList(), ct);
    }

    /// <summary>อีเมลผู้ส่ง (ตัวพิมพ์เล็ก) ที่ผูกกับผู้ติดต่อของบริษัทผู้ให้บริการ — อีเมลหลักของผู้ติดต่อ หรือปลายทางอีเมลที่เพิ่มไว้</summary>
    public async Task<Binding> EmailBindingAsync(Guid providerId, string fromLower, CancellationToken ct = default)
    {
        var primary = await _db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == providerId && c.Email != null && c.Email.ToLower() == fromLower).Select(c => c.Id).ToListAsync(ct);
        var extra = await _db.ContactNotifyEndpoints.AsNoTracking()
            .Where(e => e.CompanyId == providerId && e.Kind == NotifyEndpointKind.Email && e.UnlinkedAt == null && e.Email != null && e.Email.ToLower() == fromLower)
            .Select(e => e.ContactId).ToListAsync(ct);
        return await BindingFromAsync(providerId, primary.Concat(extra).Distinct().Select(id => (id, true)).ToList(), ct);
    }

    private async Task<Binding> BindingFromAsync(Guid providerId, List<(Guid ContactId, bool Show)> eps, CancellationToken ct)
    {
        var ids = eps.Select(e => e.ContactId).Distinct().ToList();
        var contacts = ids.Count == 0 ? new List<(Guid, string?, string?)>()
            : (await _db.Contacts.AsNoTracking().Where(c => c.CompanyId == providerId && ids.Contains(c.Id))
                .Select(c => new { c.Id, c.ExternalSystem, c.ExternalId }).ToListAsync(ct)).Select(c => (c.Id, c.ExternalSystem, c.ExternalId)).ToList();
        var clients = contacts.Where(c => c.Item2 == PlatformInvoiceRules.TenantContactSystem && Guid.TryParse(c.Item3, out _))
            .Select(c => Guid.Parse(c.Item3!)).Where(g => g != providerId).Distinct().ToList();
        return new Binding(contacts.Select(c => c.Item1).ToList(), clients, eps.Count > 0 && eps.All(e => e.Show), eps.Count > 0);
    }

    /// <summary>ผู้ติดต่อ (ในบริษัทผู้ให้บริการ) ที่เป็นบริษัทลูกค้านี้</summary>
    public async Task<List<Guid>> ClientContactIdsAsync(Guid providerId, Guid clientId, CancellationToken ct = default)
    {
        var key = clientId.ToString();
        return await _db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == providerId && c.ExternalSystem == PlatformInvoiceRules.TenantContactSystem && c.ExternalId == key)
            .Select(c => c.Id).ToListAsync(ct);
    }

    /// <summary>ส่งข้อความถึงทุกกลุ่ม LINE (OA กลาง) + อีเมลของผู้ติดต่อของบริษัทลูกค้านี้ · <paramref name="text"/> รับธง ShowAmounts ของปลายทาง ·
    /// คืนผลการส่งที่อ่านได้ (ว่าง = ไม่มีปลายทาง — ผู้เรียกต้องบอกผู้ใช้ ไม่ใช่เงียบ)</summary>
    public async Task<string> PostToClientAsync(Guid providerId, Guid clientId, Func<bool, string> text, CancellationToken ct = default)
    {
        var contactIds = await ClientContactIdsAsync(providerId, clientId, ct);
        if (contactIds.Count == 0) return "ไม่มีผู้ติดต่อของบริษัทลูกค้านี้ในบริษัทผู้ให้บริการ";
        var eps = await _db.ContactNotifyEndpoints.AsNoTracking()
            .Where(e => e.CompanyId == providerId && contactIds.Contains(e.ContactId) && e.UnlinkedAt == null
                && ((e.Kind == NotifyEndpointKind.LineGroup && e.OaScope == NotifyEndpointKind.ScopeNextAcc) || e.Kind == NotifyEndpointKind.Email))
            .ToListAsync(ct);
        var results = new List<string>();
        foreach (var e in eps.Where(x => x.Kind == NotifyEndpointKind.LineGroup).GroupBy(x => x.LineIdHash).Select(g => g.First()))
        {
            var gid = _secrets.Unprotect(e.LineGroupId);
            if (string.IsNullOrWhiteSpace(gid)) { results.Add("กลุ่ม LINE: ถอดรหัสปลายทางไม่ได้"); continue; }
            var r = await _line.TryPushContactTextAsync(providerId, NotifyEndpointKind.ScopeNextAcc, LineTargetKind.ContactGroup, gid,
                TaxSlipReplyCard.Clean(text(e.ShowAmounts)), Guid.NewGuid());
            results.Add("กลุ่ม LINE: " + (r.Ok ? "ส่งแล้ว" : "ส่งไม่สำเร็จ (" + r.Error + ")"));
        }
        var emails = eps.Where(x => x.Kind == NotifyEndpointKind.Email).Select(x => x.Email).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        foreach (var to in emails)
            results.Add("อีเมล: " + (await TrySendEmailAsync(to!, "แจ้งผลจากสำนักงานบัญชี NextAcc", text(true)) ? "ส่งแล้ว" : "ส่งไม่สำเร็จ"));
        return string.Join(" · ", results);
    }

    private async Task<bool> TrySendEmailAsync(string to, string subject, string body)
    {
        if (_email == null) return false;
        try
        {
            return await _email.TrySendAsync(to, subject, "<p>" + System.Net.WebUtility.HtmlEncode(body).Replace("\n", "<br>") + "</p>");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ส่งอีเมลผลสลิปภาษีไม่สำเร็จ");
            return false;
        }
    }

    /// <summary>ตอบการ์ดของสลิปแถวนี้ — LINE: reply (ฟรี) ถ้ามี token แล้ว push เข้ากลุ่มเดิม · อีเมล: ตอบผู้ส่ง ·
    /// <paramref name="quickReply"/> = ปุ่ม postback (เฉพาะ reply) · ประทับ <c>LastCard*</c> บนแถวเสมอ (ผู้เรียก SaveChanges)</summary>
    public async Task<bool> SendSlipCardAsync(TaxSlipEvidence row, string text, string? replyToken, string? emailTo = null,
        IReadOnlyList<object>? quickReply = null, CancellationToken ct = default)
    {
        var clean = TaxSlipReplyCard.Clean(text);
        bool ok; string outcome;
        if (row.Source == TaxSlipIntakeService.SourceEmail)
        {
            if (emailTo == null && row.ContactId is Guid contact)
                emailTo = await _db.Contacts.AsNoTracking().Where(c => c.Id == contact && c.CompanyId == row.CompanyId).Select(c => c.Email).FirstOrDefaultAsync(ct);
            ok = emailTo != null && await TrySendEmailAsync(emailTo, "ผลสลิปชำระภาษี — สำนักงานบัญชี NextAcc", clean);
            outcome = ok ? "อีเมล: ส่งแล้ว" : emailTo == null ? "อีเมล: ไม่รู้ผู้รับ" : "อีเมล: ส่งไม่สำเร็จ";
        }
        else if (!string.IsNullOrEmpty(replyToken))
        {
            object msg = quickReply is { Count: > 0 } ? new { type = "text", text = clean, quickReply = new { items = quickReply } } : new { type = "text", text = clean };
            ok = await _links.ReplyMessagesAsync(CustomerNotifyChannel.ScopeNextAcc, null, replyToken, new[] { msg }, ct);
            outcome = ok ? "LINE reply: ส่งแล้ว" : "LINE reply: ส่งไม่สำเร็จ";
            if (!ok) (ok, outcome) = await PushToSourceAsync(row, clean, ct);
        }
        else (ok, outcome) = await PushToSourceAsync(row, clean, ct);
        row.LastCardText = clean.Length > 1000 ? clean[..1000] : clean;
        row.LastCardOutcome = outcome.Length > 300 ? outcome[..300] : outcome;
        row.LastCardAt = DateTime.UtcNow;
        if (!ok) _logger.LogWarning("ส่งการ์ดผลสลิปภาษี {Slip} ไม่สำเร็จ: {Outcome}", row.Id, outcome);
        return ok;
    }

    private async Task<(bool, string)> PushToSourceAsync(TaxSlipEvidence row, string text, CancellationToken ct)
    {
        var ep = await _db.ContactNotifyEndpoints.AsNoTracking()
            .Where(e => e.CompanyId == row.CompanyId && e.Kind == NotifyEndpointKind.LineGroup && e.LineIdHash == row.SourceKey && e.UnlinkedAt == null
                && e.OaScope == NotifyEndpointKind.ScopeNextAcc)
            .OrderByDescending(e => e.LinkedAt).FirstOrDefaultAsync(ct);
        var gid = ep == null ? null : _secrets.Unprotect(ep.LineGroupId);
        if (string.IsNullOrWhiteSpace(gid)) return (false, "LINE push: กลุ่มไม่ได้ผูกแล้ว");
        var r = await _line.TryPushContactTextAsync(row.CompanyId, NotifyEndpointKind.ScopeNextAcc, LineTargetKind.ContactGroup, gid, text, Guid.NewGuid());
        return (r.Ok, r.Ok ? "LINE push: ส่งแล้ว" : "LINE push: ส่งไม่สำเร็จ (" + r.Error + ")");
    }
}
