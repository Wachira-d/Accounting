using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 241 — ลิงก์ชำระเงินของเอกสารขาย + ค่าตั้ง "ส่งเอกสารอัตโนมัติ" (DOCUMENT_FLOW §2.12g · คำตัดสิน 241-1..241-4)
///
/// <para>ผู้เขียนเดียวของ <c>Document.PayLink*</c> และ <c>CompanySettings.PayLink*/PostPay*</c> · ทุกการตัดสินอยู่ที่ <see cref="DocumentPayLinkPolicy"/>
/// (ช่องทางที่บริษัทเปิดจริง · ช่องทางต่อใบ · ใบนี้ออกลิงก์ได้ไหม · ยอดสุทธิหลังหัก ณ ที่จ่าย) · ไม่พึ่ง <c>IDocumentService</c>
/// (<c>EmailScheduleService</c> เรียกได้ — <c>DocumentService</c> → <c>IEmailScheduleService</c> อยู่แล้ว)</para>
///
/// <para>เพิกถอน: ใบยกเลิก/ชำระครบ/กลับเป็นร่าง ถูกตัดสินสด<b>ทุกครั้ง</b>ที่เปิดลิงก์หรือสร้างรายการชำระ (ไม่มีธงให้ลืมประทับ) · ผู้ใช้กดยกเลิกลิงก์ = ประทับ
/// <c>PayLinkRevokedAt</c> · ส่งอีเมลรอบใหม่หลังหมดอายุ/เพิกถอน = ออก token ใหม่ (token เดิมใช้ไม่ได้)</para>
/// </summary>
public class DocumentPayLinkService : IDocumentPayLinkService
{
    private readonly AccountingDbContext _db;
    private readonly IEnumerable<IPaymentProvider> _providers;
    private readonly IConfiguration? _config;
    private readonly ILogger<DocumentPayLinkService>? _logger;
    /// <summary>เข้ารหัส token สำหรับประกอบ URL เดิมในอีเมลรอบถัดไป (ฐานเก็บ hash สำหรับค้น — ไม่มี token ตรง · คำตัดสิน 241-10)</summary>
    private readonly ISecretProtector _secrets;

    /// <summary>รอบ 246 QR — ที่เก็บกลาง (อ่านรูป QR ของบัญชีให้หน้าชำระสาธารณะ) · null = ใช้บัญชีรับโอนของเว็บอย่างเดียว (พฤติกรรมเดิม)</summary>
    private readonly Accounting.Services.Storage.IFileStorage? _storage;

    public DocumentPayLinkService(AccountingDbContext db, IEnumerable<IPaymentProvider> providers, ISecretProtector secrets,
        IConfiguration? config = null, ILogger<DocumentPayLinkService>? logger = null,
        Accounting.Services.Storage.IFileStorage? storage = null)
    {
        _storage = storage;
        _db = db;
        _providers = providers;
        _secrets = secrets;
        _config = config;
        _logger = logger;
    }

    // ═══════════════ ความจริงของบริษัท ═══════════════

    private async Task<PaymentProviderConfig?> ActiveConfigAsync(Guid companyId, CancellationToken ct)
        => await _db.PaymentProviderConfigs.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.IsActive && !c.IsDeleted)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<DocumentPayLinkPolicy.CompanyFacts> CompanyFactsAsync(Guid companyId, CancellationToken ct = default)
    {
        var cfg = await ActiveConfigAsync(companyId, ct);
        var online = new List<string>();
        var configured = cfg != null && cfg.ProviderCode != Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code;
        if (configured)
        {
            var adapter = _providers.FirstOrDefault(p => p.ProviderCode == cfg!.ProviderCode);
            // ตัวตัดสินเดียวกับหน้าชำระค่าบริการ/ทดลองจ่าย: ที่ตั้งไว้ ∩ ที่ผู้ให้บริการรองรับ ∩ ที่หน้าชำระทำจบได้ (บัตรต้องมีคีย์สาธารณะของโหมดนี้)
            if (adapter != null)
                online = PlatformPayMethods.Effective(adapter.Capabilities.Methods,
                    PlatformPaymentService.ConfiguredMethods(cfg!.EnabledMethodsJson),
                    cardClientReady: PaymentClientSetup.For(adapter, cfg) != null);
        }
        var bank = await SiteTransferAccount.Rows(_db.Set<SitePaymentGateway>().AsNoTracking(), companyId, null)
            .AnyAsync(g => (g.PromptPayId != null && g.PromptPayId != "") || (g.BankAccountNumber != null && g.BankAccountNumber != ""), ct);
        return new DocumentPayLinkPolicy.CompanyFacts(configured, online, bank);
    }

    private static List<PayChannelOption> Options(IEnumerable<string> codes)
        => codes.Select(c => new PayChannelOption(c, DocumentPayLinkPolicy.Label(c))).ToList();

    // ═══════════════ ค่าตั้งบริษัท ═══════════════

    public async Task<DocumentAutoSendSettingsDto> GetSettingsAsync(Guid companyId, CancellationToken ct = default)
        => await ToDtoAsync(companyId, await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId, ct), ct);

    private async Task<DocumentAutoSendSettingsDto> ToDtoAsync(Guid companyId, CompanySettings? s, CancellationToken ct)
    {
        var facts = await CompanyFactsAsync(companyId, ct);
        var reason = DocumentPayLinkPolicy.UnavailableReason(facts);
        var verified = s != null && s.EmailConfigured && Accounting.Services.Implementations.Email.EmailSenderFactory.HasTenantSender(s);
        return new DocumentAutoSendSettingsDto(
            PayLinkAvailable: reason == null,
            PayLinkUnavailableReason: reason,
            CompanyChannels: Options(DocumentPayLinkPolicy.CompanyAvailable(facts)),
            PayLinkDefaultEnabled: s?.PayLinkDefaultEnabled ?? false,
            PayLinkDefaultChannels: DocumentPayLinkPolicy.ParseChannels(s?.PayLinkDefaultChannelsJson),
            PostPayEmailReceipt: s?.PostPayEmailReceipt ?? false,
            PostPayIncludePartial: s?.PostPayIncludePartial ?? false,
            SenderNote: verified
                ? "อีเมลอัตโนมัติส่งจากอีเมลของบริษัท (ตั้งค่า → อีเมล ทดสอบผ่านแล้ว)"
                : "ยังไม่ได้ตั้ง/ทดสอบอีเมลของบริษัท — อีเมลอัตโนมัติ (ตามตารางและหลังรับชำระ) ส่งด้วยอีเมลของระบบ NextAcc ในนามบริษัท: "
                  + "มีเพดานต่อวัน/ต่อผู้รับ · ผู้รับรวม BCC ไม่เกิน 5 · เนื้อหาที่เขียนเองในตารางส่งเป็นข้อความล้วน (ไม่รับ HTML) · ฉบับที่เกินเพดานไม่ถูกส่งและแสดงเหตุในคิว");
    }

    public async Task<DocumentAutoSendSettingsDto> UpdateSettingsAsync(Guid companyId, UpdateDocumentAutoSendSettings request,
        string actor, CancellationToken ct = default)
    {
        var facts = await CompanyFactsAsync(companyId, ct);
        var available = DocumentPayLinkPolicy.CompanyAvailable(facts);
        // เปิดแนบลิงก์ได้เฉพาะบริษัทที่ตั้งรับชำระออนไลน์แล้ว (เซิร์ฟเวอร์ตัดสิน · ปุ่มบนหน้าเป็นแค่การแสดง)
        if (request.PayLinkDefaultEnabled == true && DocumentPayLinkPolicy.UnavailableReason(facts) is { } why)
            throw new BusinessRuleException(why, DocumentPayLinkPolicy.RuleCode);
        if (request.PayLinkDefaultChannels != null && DocumentPayLinkPolicy.Unknown(available, request.PayLinkDefaultChannels) is { Count: > 0 } bad)
            throw new BusinessRuleException("ช่องทางที่บริษัทยังไม่ได้เปิด: " + string.Join(", ", bad) + " — เลือกได้เฉพาะช่องทางที่ตั้งไว้ในหน้าตั้งค่ารับชำระเงิน",
                DocumentPayLinkPolicy.RuleCode);

        var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId, ct)
                ?? await CompanySettingsFactory.AddNewAsync(_db, companyId, ct);
        var before = new { s.PayLinkDefaultEnabled, s.PayLinkDefaultChannelsJson, s.PostPayEmailReceipt, s.PostPayIncludePartial };
        if (request.PayLinkDefaultEnabled is { } en) s.PayLinkDefaultEnabled = en;
        if (request.PayLinkDefaultChannels != null) s.PayLinkDefaultChannelsJson = DocumentPayLinkPolicy.SerializeChannels(request.PayLinkDefaultChannels);
        if (request.PostPayEmailReceipt is { } er) s.PostPayEmailReceipt = er;
        if (request.PostPayIncludePartial is { } ip) s.PostPayIncludePartial = ip;
        s.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(CompanySettings), EntityId = s.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event = "DocumentAutoSendSettings", s.PayLinkDefaultEnabled, s.PayLinkDefaultChannelsJson,
                s.PostPayEmailReceipt, s.PostPayIncludePartial, actor }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return await ToDtoAsync(companyId, s, ct);
    }

    // ═══════════════ ลิงก์ต่อเอกสาร ═══════════════

    private sealed record DocFacts(Guid Id, Guid CompanyId, Guid ContactId, DocumentType Type, DocumentStatus Status, decimal BalanceDue,
        decimal WithholdingTaxAmount, string? Currency, string DocumentNumber, DateTime DocumentDate, DateTime? DueDate,
        string? PayLinkTokenHash, string? PayLinkTokenProtected, DateTime? PayLinkExpiresAt, DateTime? PayLinkRevokedAt, string? PayLinkChannelsJson,
        DocumentType? RelatedType);

    /// <summary>โหลดตาม id หรือตาม <b>hash</b> ของ token (ฐานไม่มี token ตรง) — ผู้เรียกฝั่ง token ต้องยืนยันซ้ำด้วย <c>TokenMatches</c> (เวลาคงที่)</summary>
    private async Task<DocFacts?> LoadDocAsync(Guid companyId, Guid? documentId, string? tokenHash, CancellationToken ct)
    {
        var q = _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && !d.IsDeleted);
        q = documentId is Guid id ? q.Where(d => d.Id == id) : q.Where(d => d.PayLinkTokenHash == tokenHash);
        return await q.Select(d => new DocFacts(d.Id, d.CompanyId, d.ContactId, d.DocumentType, d.Status, d.BalanceDue, d.WithholdingTaxAmount,
                d.Currency, d.DocumentNumber, d.DocumentDate, d.DueDate, d.PayLinkTokenHash, d.PayLinkTokenProtected, d.PayLinkExpiresAt, d.PayLinkRevokedAt,
                d.PayLinkChannelsJson,
                _db.Documents.Where(r => r.Id == d.RelatedDocumentId && r.CompanyId == companyId).Select(r => (DocumentType?)r.DocumentType).FirstOrDefault()))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>ผู้ซื้อมีหน้าที่หัก ณ ที่จ่ายไหม (ฝ่ายค้าน S3) — ตัวตัดสินเดียว <see cref="DocumentPayLinkPolicy.BuyerWithholds"/></summary>
    private async Task<bool?> BuyerWithholdsAsync(Guid companyId, Guid contactId, CancellationToken ct)
    {
        var b = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == contactId && c.CompanyId == companyId)
            .Select(c => new { c.ContactType, c.TaxId }).FirstOrDefaultAsync(ct);
        return DocumentPayLinkPolicy.BuyerWithholds(b?.ContactType, b?.TaxId);
    }

    /// <summary>ยอดของลิงก์ — ยอดค้าง · WHT ที่คาดว่าจะหักจริง (ผู้ซื้อไม่มีหน้าที่ = 0) · ที่หักไปแล้วจากตัวนับเดียวกับตัวบันทึกรับชำระ</summary>
    private async Task<(DocumentPayLinkPolicy.Amounts Amounts, string? WhtNote)> AmountsAsync(Guid companyId, DocFacts d, CancellationToken ct)
    {
        var withholds = await BuyerWithholdsAsync(companyId, d.ContactId, ct);
        // รอบ 244 (ฝ่ายค้าน S2): WHT ที่เหลือจากตัวคิดเดียวกับตัวบันทึกรับชำระ (ใบลดหนี้ที่มีผล · ที่หักไปแล้ว) — NetCap
        var doc = await _db.Documents.AsNoTracking().FirstAsync(x => x.Id == d.Id && x.CompanyId == companyId, ct);
        var figures = await DocumentService.PaymentWhtFiguresAsync(_db, companyId, doc, d.BalanceDue, ct);
        var amounts = DocumentPayLinkPolicy.AmountsFor(d.BalanceDue,
            DocumentPayLinkPolicy.ExpectedWht(figures.NetCap, withholds), 0m);
        return (amounts, DocumentPayLinkPolicy.WhtMismatchNote(d.WithholdingTaxAmount, withholds));
    }

    /// <summary>ถอด token ฉบับเข้ารหัส แล้วยืนยันกับ hash ที่เก็บ (เวลาคงที่) — ถอดไม่ได้/ไม่ตรง ⇒ null (ผู้เรียกออก token ใหม่)</summary>
    private string? Recover(string? protectedToken, string? storedHash)
    {
        if (string.IsNullOrEmpty(protectedToken)) return null;
        string? raw;
        try { raw = _secrets.Unprotect(protectedToken); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        return DocumentPayLinkPolicy.TokenMatches(raw, storedHash) ? raw : null;
    }

    private static string? Refusal(DocFacts d)
        => DocumentPayLinkPolicy.IssueRefusal(d.Type, d.Status, d.BalanceDue, DocumentPayLinkPolicy.IsSaleSide(d.Type, d.RelatedType), d.Currency);

    private async Task<(IReadOnlyList<string> All, IReadOnlyList<string>? CompanyDefault, string? Unavailable)> ChannelsAsync(Guid companyId, CancellationToken ct)
    {
        var facts = await CompanyFactsAsync(companyId, ct);
        var def = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => s.PayLinkDefaultChannelsJson).FirstOrDefaultAsync(ct);
        return (DocumentPayLinkPolicy.CompanyAvailable(facts), DocumentPayLinkPolicy.ParseChannels(def), DocumentPayLinkPolicy.UnavailableReason(facts));
    }

    public async Task<PayLinkResult> EnsureLinkAsync(Guid companyId, Guid documentId, IReadOnlyCollection<string>? scheduleChannels, CancellationToken ct = default)
    {
        var d = await LoadDocAsync(companyId, documentId, null, ct);
        if (d == null) return new PayLinkResult(null, null, Array.Empty<string>(), "ไม่พบเอกสาร");
        if (Refusal(d) is { } no) return new PayLinkResult(null, null, Array.Empty<string>(), no);
        var (all, companyDefault, unavailable) = await ChannelsAsync(companyId, ct);
        if (unavailable != null) return new PayLinkResult(null, null, Array.Empty<string>(), unavailable);
        // รอบ 248 (AUTOPILOT §5.1): ช่องทาง = ตารางส่ง › ผู้ติดต่อ › ค่าบริษัท (ตัวต่อชั้นเดียว AutopilotSettingsResolver) — ผู้ติดต่อไม่ได้ตั้ง = เท่าเดิม
        var contactLayer = await ContactPayLayerAsync(companyId, d.ContactId, all, ct);
        var choice = AutopilotSettingsResolver.Resolve(
            scheduleChannels is { Count: > 0 } ? new AutopilotSettingsResolver.Layer(null, scheduleChannels.ToList()) : null,
            null, contactLayer, companyAttach: true, companyDefault);
        var allowed = DocumentPayLinkPolicy.Allowed(all, choice.Channels, null);
        if (DocumentPayLinkPolicy.OnlineOnly(allowed).Count == 0)
            return new PayLinkResult(null, null, allowed, "ช่องทางที่เลือกไม่มีช่องทางออนไลน์ที่บริษัทเปิดอยู่ — ตรวจช่องทางในตารางส่งเอกสาร/ค่าตั้งส่งเอกสารอัตโนมัติ");

        var now = DateTime.UtcNow;
        var json = DocumentPayLinkPolicy.SerializeChannels(allowed);
        // หนึ่งลิงก์ต่อใบ — ลิงก์เดิมยังใช้ได้ + ถอด token ฉบับเข้ารหัสได้ ⇒ URL เดิม (อีเมลรอบก่อนยังใช้ได้) · ช่องทางตามรอบล่าสุด
        string? token = DocumentPayLinkPolicy.LinkUsable(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, now)
            ? Recover(d.PayLinkTokenProtected, d.PayLinkTokenHash) : null;
        if (token != null)
        {
            if (!string.Equals(d.PayLinkChannelsJson, json, StringComparison.Ordinal))
                await _db.Documents.Where(x => x.Id == d.Id && x.CompanyId == companyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.PayLinkChannelsJson, json), ct);
        }
        else
        {
            var fresh = DocumentPayLinkPolicy.NewToken();
            var freshHash = DocumentPayLinkPolicy.TokenHash(fresh);
            // ชื่อช่องตรงกับคอลัมน์ — ทะเบียนหมุนกุญแจ (SecretReprotectJob) ตรวจด้วย tools/secret_registry_check.py
            var sealedToken = new { PayLinkTokenProtected = _secrets.Protect(fresh) };
            var exp = now.AddDays(DocumentPayLinkPolicy.TtlDays);
            // เขียนแบบมีเงื่อนไข (hash เดิมยังเป็นค่าที่อ่านมา) — สองเครื่องออกพร้อมกันได้ token เดียว
            var oldHash = d.PayLinkTokenHash;
            var n = await _db.Documents
                .Where(x => x.Id == d.Id && x.CompanyId == companyId && x.PayLinkTokenHash == oldHash)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.PayLinkTokenHash, freshHash)
                    .SetProperty(x => x.PayLinkTokenProtected, sealedToken.PayLinkTokenProtected)
                    .SetProperty(x => x.PayLinkIssuedAt, (DateTime?)now)
                    .SetProperty(x => x.PayLinkExpiresAt, (DateTime?)exp)
                    .SetProperty(x => x.PayLinkRevokedAt, (DateTime?)null)
                    .SetProperty(x => x.PayLinkChannelsJson, json), ct);
            if (n > 0) token = fresh;
            else
            {
                var won = await _db.Documents.AsNoTracking().Where(x => x.Id == d.Id && x.CompanyId == companyId)
                    .Select(x => new { x.PayLinkTokenHash, x.PayLinkTokenProtected }).FirstOrDefaultAsync(ct);
                token = Recover(won?.PayLinkTokenProtected, won?.PayLinkTokenHash);
                if (token == null) return new PayLinkResult(null, null, allowed, "ออกลิงก์ชำระเงินชนกับคำขออื่น — ลองใหม่อีกครั้ง");
            }
        }
        var origin = await SystemBaseUrl.ResolveAsync(_db, _config, ct);
        if (string.IsNullOrWhiteSpace(origin))
            return new PayLinkResult(null, null, allowed, "ยังไม่ได้ตั้ง URL ของระบบ (App:BaseUrl) — สร้างลิงก์สาธารณะไม่ได้");
        var (amounts, _) = await AmountsAsync(companyId, d, ct);
        return new PayLinkResult(DocumentPayLinkPolicy.Url(origin, companyId, token), amounts, allowed, null);
    }

    /// <summary>รอบ 248: ชั้นช่องทางรับเงินของผู้ติดต่อ (null = ไม่ได้ตั้ง — ตามบริษัท) · <paramref name="companyAvailable"/> null ⇒ อ่านเองเมื่อจำเป็น (โหมด Online) ·
    /// กรองบริษัทเสมอ (tenant) · ผู้ติดต่อถูกลบก็ยังใช้ค่าที่ตั้งไว้ได้ (IgnoreQueryFilters แบบตัวอ่านผู้ซื้อของไฟล์นี้)</summary>
    private async Task<AutopilotSettingsResolver.Layer?> ContactPayLayerAsync(Guid companyId, Guid contactId,
        IReadOnlyList<string>? companyAvailable, CancellationToken ct)
    {
        var c = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.Id == contactId && x.CompanyId == companyId)
            .Select(x => new { x.PaymentMode, x.PayLinkChannelsJson })
            .FirstOrDefaultAsync(ct);
        if (c == null || (c.PaymentMode == null && c.PayLinkChannelsJson == null)) return null;
        var available = companyAvailable ?? DocumentPayLinkPolicy.CompanyAvailable(await CompanyFactsAsync(companyId, ct));
        var layer = AutopilotSettingsResolver.FromMode(c.PaymentMode, DocumentPayLinkPolicy.ParseChannels(c.PayLinkChannelsJson), available);
        return layer.Attach == null && layer.Channels == null ? null : layer;
    }

    public async Task<PayLinkResult?> ForScheduledEmailAsync(EmailScheduleRule rule, Document doc, CancellationToken ct = default)
    {
        var companyAttach = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == doc.CompanyId)
            .Select(s => (bool?)s.PayLinkDefaultEnabled).FirstOrDefaultAsync(ct) ?? false;
        // รอบ 248: แนบไหม = ตารางส่ง (รวมชั้นรายการประจำที่ EmailScheduleService เติมไว้) › ผู้ติดต่อ › บริษัท — ตัวต่อชั้นเดียว
        var contactLayer = await ContactPayLayerAsync(doc.CompanyId, doc.ContactId, null, ct);
        var attach = AutopilotSettingsResolver.Resolve(
            new AutopilotSettingsResolver.Layer(rule.AttachPayLink, DocumentPayLinkPolicy.ParseChannels(rule.PayLinkChannelsJson)),
            null, contactLayer, companyAttach, null).Attach;
        if (!attach) return null;
        var r = await EnsureLinkAsync(doc.CompanyId, doc.Id, DocumentPayLinkPolicy.ParseChannels(rule.PayLinkChannelsJson), ct);
        if (!r.Ok)
        {
            // ไม่ใช่ความผิดพลาดของการส่ง (ใบชำระครบ/ใบซื้อ/บริษัทปิดรับออนไลน์) — อีเมลยังส่งตามปกติโดยไม่มีลิงก์ · เหตุผลอยู่ในหน้าตั้งค่า/หน้าเอกสาร
            _logger?.LogInformation("ไม่แนบลิงก์ชำระเงินให้ {Doc}: {Why}", doc.Id, r.Refusal);
            return null;
        }
        return r;
    }

    public async Task<string?> CurrentUrlAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var d = await LoadDocAsync(companyId, documentId, null, ct);
        if (d == null || Refusal(d) != null
            || !DocumentPayLinkPolicy.LinkUsable(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, DateTime.UtcNow)) return null;
        var raw = Recover(d.PayLinkTokenProtected, d.PayLinkTokenHash);
        var origin = raw == null ? null : await SystemBaseUrl.ResolveAsync(_db, _config, ct);
        return raw == null || string.IsNullOrWhiteSpace(origin) ? null : DocumentPayLinkPolicy.Url(origin, companyId, raw);
    }

    /// <summary>รอบ 246 QR — ข้อเท็จจริงสำหรับ QR บนกระดาษ (อ่านอย่างเดียว · ไม่ออก/ต่ออายุลิงก์): ยอดสุทธิหลังหัก ณ ที่จ่ายจากตัวคิดเดียวกับลิงก์
    /// (<see cref="AmountsAsync"/>) · URL ของลิงก์ที่ยังใช้ได้ (เพิกถอน/หมดอายุ/ใบไม่เข้าเงื่อนไข ⇒ null — กระดาษที่พิมพ์ใหม่ไม่มี QR) ·
    /// จำนวนช่องทางออนไลน์ของใบที่บริษัทยังเปิด · null = ไม่พบใบ</summary>
    public async Task<DocumentPayQrFacts?> PayQrFactsAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var d = await LoadDocAsync(companyId, documentId, null, ct);
        if (d == null) return null;
        var refusal = Refusal(d);
        if (refusal != null) return new DocumentPayQrFacts(refusal, null, null, null, 0);
        var (amounts, whtNote) = await AmountsAsync(companyId, d, ct);
        string? url = null;
        var online = 0;
        if (DocumentPayLinkPolicy.LinkUsable(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, DateTime.UtcNow))
        {
            var (all, companyDefault, unavailable) = await ChannelsAsync(companyId, ct);
            if (unavailable == null)
            {
                online = DocumentPayLinkPolicy.OnlineOnly(
                    DocumentPayLinkPolicy.Allowed(all, DocumentPayLinkPolicy.ParseChannels(d.PayLinkChannelsJson), companyDefault)).Count;
                var raw = Recover(d.PayLinkTokenProtected, d.PayLinkTokenHash);
                var origin = raw == null ? null : await SystemBaseUrl.ResolveAsync(_db, _config, ct);
                if (raw != null && !string.IsNullOrWhiteSpace(origin)) url = DocumentPayLinkPolicy.Url(origin, companyId, raw);
            }
        }
        return new DocumentPayQrFacts(null, amounts.Net, url, url == null ? null : d.PayLinkExpiresAt, online, whtNote);
    }

    public async Task RevokeAsync(Guid companyId, Guid documentId, string actor, CancellationToken ct = default)
    {
        var n = await _db.Documents.Where(d => d.Id == documentId && d.CompanyId == companyId && d.PayLinkTokenHash != null && d.PayLinkRevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.PayLinkRevokedAt, (DateTime?)DateTime.UtcNow), ct);
        if (n == 0) throw new BusinessRuleException("เอกสารนี้ไม่มีลิงก์ชำระเงินที่ใช้งานอยู่", DocumentPayLinkPolicy.RuleCode);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(Document), EntityId = documentId.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event = "PayLinkRevoked", actor }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<DocumentPayLinkStatusDto> StatusAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var d = await LoadDocAsync(companyId, documentId, null, ct) ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var (all, companyDefault, unavailable) = await ChannelsAsync(companyId, ct);
        var allowed = DocumentPayLinkPolicy.Allowed(all, DocumentPayLinkPolicy.ParseChannels(d.PayLinkChannelsJson), companyDefault);
        var (amounts, whtNote) = await AmountsAsync(companyId, d, ct);
        var raw = DocumentPayLinkPolicy.LinkUsable(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, DateTime.UtcNow)
            ? Recover(d.PayLinkTokenProtected, d.PayLinkTokenHash) : null;
        var usable = raw != null;
        var origin = await SystemBaseUrl.ResolveAsync(_db, _config, ct);
        var runs = await _db.PostPaymentAutomationRuns.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.DocumentId == documentId)
            .OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync(ct);
        return new DocumentPayLinkStatusDto(
            Refusal: Refusal(d) ?? unavailable,
            Url: raw != null && origin != null ? DocumentPayLinkPolicy.Url(origin, companyId, raw) : null,
            ExpiresAt: d.PayLinkExpiresAt,
            Usable: usable,
            Channels: Options(allowed),
            Gross: amounts.Gross, Wht: amounts.Wht, Net: amounts.Net,
            WhtNote: whtNote,
            Runs: runs.Select(r => new PostPaymentRunDto(r.Id, r.PaymentId, r.Source, r.Status, PostPaymentAutomationPolicy.StatusLabel(r.Status),
                r.Attempts, r.Message, r.StepsDone, r.ReceiptDocumentId, r.CreatedAt, r.CompletedAt)).ToList());
    }

    // ═══════════════ หน้าชำระสาธารณะ ═══════════════

    public async Task<PublicDocumentPayView?> PublicViewAsync(Guid companyId, string token, CancellationToken ct = default)
    {
        // ค้นด้วย hash (ฐานไม่มี token ตรง) แล้วยืนยันซ้ำแบบเวลาคงที่ · รูปผิด = "ไม่พบ" เหมือน token ผิด
        if (!DocumentPayLinkPolicy.LooksValid(token)) return null;
        var d = await LoadDocAsync(companyId, null, DocumentPayLinkPolicy.TokenHash(token), ct);
        if (d == null || !DocumentPayLinkPolicy.TokenMatches(token, d.PayLinkTokenHash)) return null;

        var now = DateTime.UtcNow;
        // ฝ่ายค้าน S2: ลิงก์เพิกถอน/หมดอายุ ⇒ ไม่เปิดเผยอะไรของใบ (ชื่อลูกค้า · เลขเอกสาร · ยอด · บัญชีรับโอน) — ข้อความเดียว
        // (Target ยังผูกใบเดิมเพื่อให้รายการชำระที่สร้างไปแล้วอ่านสถานะได้ · ยอด 0 = สร้างรายการใหม่ไม่ได้ · ไม่ส่งออกใน JSON)
        if (!DocumentPayLinkPolicy.LinkUsable(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, now))
            return UnusableView(d);

        var (all, companyDefault, unavailable) = await ChannelsAsync(companyId, ct);
        var allowed = DocumentPayLinkPolicy.Allowed(all, DocumentPayLinkPolicy.ParseChannels(d.PayLinkChannelsJson), companyDefault);
        var online = DocumentPayLinkPolicy.OnlineOnly(allowed);
        var (amounts, _) = await AmountsAsync(companyId, d, ct);

        // ตัดสินสดทุกครั้ง: ใบยกเลิก/ชำระครบ · บริษัทปิดรับออนไลน์ · ช่องทางของใบไม่เหลือ
        var blocked = Refusal(d)
            ?? (unavailable != null || online.Count == 0 ? "ผู้ขายยังไม่เปิดรับชำระออนไลน์สำหรับเอกสารนี้ — กรุณาติดต่อผู้ขาย" : null);
        // เอกสารนี้รับเงินออนไลน์สำเร็จไปแล้วหนึ่งครั้ง ⇒ ไม่รับซ้ำ (ด่านเดียวกับ PaymentIntentService.StartAsync "ชำระเรียบร้อยแล้ว" — บอกก่อนลูกค้ากด)
        if (blocked == null && await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.CompanyId == companyId
                && i.SourceKind == PaymentSourceKind.Document && i.SourceId == d.Id && i.Status == PaymentIntentStatus.Succeeded, ct))
            blocked = "ได้รับเงินผ่านลิงก์นี้แล้ว — ถ้ายังมียอดคงเหลือ กรุณาติดต่อผู้ขาย";

        var company = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => new { c.Name, c.NameEn }).FirstOrDefaultAsync(ct);
        var contact = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == d.ContactId && c.CompanyId == companyId).Select(c => new { c.Name, c.Email, c.Phone }).FirstOrDefaultAsync(ct);
        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, d.Id);
        var cfg = await ActiveConfigAsync(companyId, ct);
        var adapter = cfg == null ? null : _providers.FirstOrDefault(p => p.ProviderCode == cfg.ProviderCode);
        var card = online.Contains(nameof(PaymentMethodKind.Card), StringComparer.OrdinalIgnoreCase) ? PaymentClientSetup.For(adapter, cfg) : null;

        PublicTransferInfo? transfer = null;
        if (allowed.Contains(DocumentPayLinkPolicy.BankTransferCode, StringComparer.OrdinalIgnoreCase) && Refusal(d) == null)
        {
            // รอบ 246 QR (ฝ่ายค้าน S2): บัญชีของใบก่อน — ตัวเลือกบัญชีเดียวกับกระดาษ (PaymentQrService.PickDocumentAccountAsync) · ไม่มี ⇒ บัญชีรับโอนของเว็บ (เดิม)
            transfer = await DocumentAccountTransferAsync(companyId, d.Id, amounts.Net, ct);
            var gw = transfer != null ? null
                : await SiteTransferAccount.Rows(_db.Set<SitePaymentGateway>().AsNoTracking(), companyId, null).FirstOrDefaultAsync(ct);
            if (gw != null)
            {
                // รอบ 246 QR: QR พร้อมเพย์ที่เซิร์ฟเวอร์สร้าง (ฝังยอดสุทธิของลิงก์ · data URI จากไบต์ที่สร้างเอง) — เลขที่ใช้ไม่ได้ ⇒ ไม่มี QR (ข้อความเดิม)
                string? qr = null;
                var ppId = PaymentQrPolicy.NormalizePromptPayId(gw.PromptPayId).Id;
                if (ppId != null && amounts.Net > 0.005m)
                    qr = "data:image/png;base64," + Convert.ToBase64String(new PromptPayService().GenerateQrImage(ppId, amounts.Net, size: 330));
                transfer = new PublicTransferInfo(gw.BankName, gw.BankAccountNumber, gw.BankAccountName, gw.PromptPayId, qr);
            }
        }

        var title = heading.Title;
        var target = new PublicPayTarget(PaymentSourceKind.Document, d.Id, Guid.Empty, blocked == null ? amounts.Net : 0m,
            $"{title} {d.DocumentNumber}", contact?.Email, contact?.Phone, d.DocumentNumber, BlockedNote: blocked,
            AllowedMethods: online, ContactId: d.ContactId);
        return new PublicDocumentPayView(
            CompanyName: (heading.IsEnglish && !string.IsNullOrWhiteSpace(company?.NameEn) ? company!.NameEn : company?.Name) ?? "",
            DocumentTitle: title, DocumentNumber: d.DocumentNumber, DocumentDate: d.DocumentDate, DueDate: d.DueDate,
            CustomerName: contact?.Name,
            Gross: amounts.Gross, Wht: amounts.Wht, Net: amounts.Net, Currency: "THB",
            CanPayOnline: blocked == null && amounts.Net > 0.005m,
            BlockedNote: blocked,
            OnlineMethods: blocked == null ? Options(online) : new List<PayChannelOption>(),
            Transfer: transfer,
            Card: blocked == null ? card : null,
            IsTestMode: cfg?.Mode == PaymentProviderMode.Test)
        { Target = target };
    }

    /// <summary>รอบ 246 QR (ฝ่ายค้าน S2) — บัญชีรับโอนของใบนี้จากตัวเลือกบัญชีเดียวกับกระดาษ (แหล่งเงินของใบ → บัญชีของใบ → เทมเพลต → บัญชีเดียวที่ตั้ง QR) +
    /// QR ของบัญชีนั้น (พร้อมเพย์ฝังยอดสุทธิของลิงก์ · รูปที่อัปโหลด) · ไม่มีที่เก็บ/ไม่พบบัญชี = null (ผู้เรียกใช้บัญชีรับโอนของเว็บ)</summary>
    private async Task<PublicTransferInfo?> DocumentAccountTransferAsync(Guid companyId, Guid documentId, decimal net, CancellationToken ct)
    {
        if (_storage == null) return null;
        var doc = await _db.Documents.AsNoTracking().Include(x => x.Brand)
            .FirstOrDefaultAsync(x => x.Id == documentId && x.CompanyId == companyId, ct);
        if (doc == null) return null;
        var template = await PdfGenerationService.ResolveDocumentTemplateAsync(_db, companyId, doc, null);
        var (acct, _) = await PaymentQrService.PickDocumentAccountAsync(_db, _storage.Locator.AttachmentsRoot, companyId,
            doc.PaymentAccountId, doc.BankAccountId, template.PaymentQrBankAccountId, ct);
        if (acct == null) return null;
        var superseded = await PaymentQrService.SupersededByTaxInvoiceAsync(_db, companyId, doc, ct);
        var qr = await PaymentQrService.BuildAccountQrAsync(_storage, new PromptPayService(), _logger, companyId, acct, acct.PromptPayId,
            acct.PaymentQrSource, PaymentQrPolicy.EmbedAmount(null, net, superseded), ct);
        return new PublicTransferInfo(acct.BankName, acct.AccountNumber, acct.AccountName,
            qr?.PromptPayId == null ? null : PaymentQrPolicy.DisplayPromptPayId(qr.PromptPayId),
            qr == null ? null : "data:image/png;base64," + Convert.ToBase64String(qr.Png));
    }

    public const string UnusableLinkNote = "ลิงก์นี้ใช้ไม่ได้แล้ว (หมดอายุหรือถูกยกเลิก) — กรุณาติดต่อผู้ขายเพื่อขอลิงก์ใหม่";

    /// <summary>มุมมองของลิงก์ที่ใช้ไม่ได้ — ไม่มีข้อมูลของใบเลย (ฝ่ายค้าน S2)</summary>
    private static PublicDocumentPayView UnusableView(DocFacts d) => new(
        CompanyName: "", DocumentTitle: "", DocumentNumber: "", DocumentDate: default, DueDate: null, CustomerName: null,
        Gross: 0m, Wht: 0m, Net: 0m, Currency: "THB", CanPayOnline: false, BlockedNote: UnusableLinkNote,
        OnlineMethods: new List<PayChannelOption>(), Transfer: null, Card: null, IsTestMode: false)
    {
        Target = new PublicPayTarget(PaymentSourceKind.Document, d.Id, Guid.Empty, 0m, "", null, null, "",
            BlockedNote: UnusableLinkNote, AllowedMethods: Array.Empty<string>(), ContactId: d.ContactId),
    };
}
