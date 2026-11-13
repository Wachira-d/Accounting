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
public partial class DocumentPayLinkService : IDocumentPayLinkService
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
    {
        return await ActivePaymentConfig.Ordered(_db, companyId).AsNoTracking().FirstOrDefaultAsync(ct);   // รอบ 260-3 L4: ตัวเลือกเดียว
    }

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
            WhtCertReminderEnabled: s?.WhtCertReminderEnabled ?? false,
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
        var before = new { s.PayLinkDefaultEnabled, s.PayLinkDefaultChannelsJson, s.PostPayEmailReceipt, s.PostPayIncludePartial, s.WhtCertReminderEnabled };
        if (request.PayLinkDefaultEnabled is { } en) s.PayLinkDefaultEnabled = en;
        if (request.PayLinkDefaultChannels != null) s.PayLinkDefaultChannelsJson = DocumentPayLinkPolicy.SerializeChannels(request.PayLinkDefaultChannels);
        if (request.PostPayEmailReceipt is { } er) s.PostPayEmailReceipt = er;
        if (request.PostPayIncludePartial is { } ip) s.PostPayIncludePartial = ip;
        if (request.WhtCertReminderEnabled is { } wr) s.WhtCertReminderEnabled = wr;
        s.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(CompanySettings), EntityId = s.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event = "DocumentAutoSendSettings", s.PayLinkDefaultEnabled, s.PayLinkDefaultChannelsJson,
                s.PostPayEmailReceipt, s.PostPayIncludePartial, s.WhtCertReminderEnabled, actor }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return await ToDtoAsync(companyId, s, ct);
    }

    // ═══════════════ ลิงก์ต่อเอกสาร ═══════════════

    private sealed record DocFacts(Guid Id, Guid CompanyId, Guid ContactId, DocumentType Type, DocumentStatus Status, decimal BalanceDue,
        decimal WithholdingTaxAmount, string? Currency, string DocumentNumber, DateTime DocumentDate, DateTime? DueDate,
        string? PayLinkTokenHash, string? PayLinkTokenProtected, DateTime? PayLinkExpiresAt, DateTime? PayLinkRevokedAt, string? PayLinkChannelsJson,
        DocumentType? RelatedType, bool? CnDnPurchaseSideOverride, bool ContactSupplierOnly)
    {
        /// <summary>รอบ 250 (WP-02 §2.3): เลขใบกำกับภาษีที่อ้างใบแจ้งหนี้นี้และยังมีผล (โหลดใน <see cref="LoadDocAsync"/> — ตัดสินใน policy ไม่ใช่ service)</summary>
        public string? SupersedingTaxInvoice { get; init; }
        /// <summary>รอบ 267 ฝ่ายค้าน M7: วันออกลิงก์ (แยกลิงก์ชำระรุ่นก่อน — <see cref="CustomerDocPage.IsPayOnlyLink"/>)</summary>
        public DateTime? PayLinkIssuedAt { get; init; }
        /// <summary>รอบ 262 ฝ่ายค้าน H1: ใบค่าบริการของผู้เช่า (บริษัทผู้ให้บริการ + ผู้ติดต่อผูก tenant) — ห้ามลิงก์ทั่วไปทุกทาง</summary>
        public bool PlatformTenantDocument { get; init; }
    }

    /// <summary>โหลดตาม id หรือตาม <b>hash</b> ของ token (ฐานไม่มี token ตรง) — ผู้เรียกฝั่ง token ต้องยืนยันซ้ำด้วย <c>TokenMatches</c> (เวลาคงที่)</summary>
    private async Task<DocFacts?> LoadDocAsync(Guid companyId, Guid? documentId, string? tokenHash, CancellationToken ct)
    {
        var q = _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && !d.IsDeleted);
        q = documentId is Guid id ? q.Where(d => d.Id == id) : q.Where(d => d.PayLinkTokenHash == tokenHash);
        var facts = await q.Select(d => new DocFacts(d.Id, d.CompanyId, d.ContactId, d.DocumentType, d.Status, d.BalanceDue, d.WithholdingTaxAmount,
                d.Currency, d.DocumentNumber, d.DocumentDate, d.DueDate, d.PayLinkTokenHash, d.PayLinkTokenProtected, d.PayLinkExpiresAt, d.PayLinkRevokedAt,
                d.PayLinkChannelsJson,
                _db.Documents.Where(r => r.Id == d.RelatedDocumentId && r.CompanyId == companyId).Select(r => (DocumentType?)r.DocumentType).FirstOrDefault(),
                d.CnDnPurchaseSideOverride,
                // ฝ่ายค้าน WP-02 ข้อ 3: ผู้ติดต่อเป็นผู้ขายอย่างเดียว — ข้อเท็จจริงเดียวกับที่ JE รับชำระใช้ตัดสินฝั่ง
                _db.Contacts.IgnoreQueryFilters().Where(c => c.Id == d.ContactId && c.CompanyId == companyId).Select(c => c.IsSupplier && !c.IsCustomer).FirstOrDefault()))
            .FirstOrDefaultAsync(ct);
        if (facts == null) return null;
        // รอบ 250 (WP-02 §2.3): fact "มีใบกำกับอ้างแล้ว" — ตัวโหลดเดียวกับ QR บนกระดาษ/งานหลังรับชำระ
        return facts with
        {
            SupersedingTaxInvoice = await PaymentQrService.SupersedingTaxInvoiceNumberAsync(_db, companyId, facts.Type, facts.Id, ct),
            PlatformTenantDocument = await PlatformInvoiceQueries.IsTenantDocumentAsync(_db, companyId, facts.ContactId, ct),
            PayLinkIssuedAt = await _db.Documents.AsNoTracking().Where(x => x.Id == facts.Id && x.CompanyId == companyId)
                .Select(x => x.PayLinkIssuedAt).FirstOrDefaultAsync(ct),
        };
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
    {
        return DocumentPayLinkPolicy.IssueRefusal(d.Type, d.Status, d.BalanceDue, DocumentPayLinkPolicy.IsSaleSide(d.Type, d.RelatedType, d.CnDnPurchaseSideOverride, d.ContactSupplierOnly), d.Currency,
            d.SupersedingTaxInvoice, d.PlatformTenantDocument);
    }

    private async Task<(IReadOnlyList<string> All, IReadOnlyList<string>? CompanyDefault, string? Unavailable)> ChannelsAsync(Guid companyId, CancellationToken ct)
    {
        var facts = await CompanyFactsAsync(companyId, ct);
        var def = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => s.PayLinkDefaultChannelsJson).FirstOrDefaultAsync(ct);
        return (DocumentPayLinkPolicy.CompanyAvailable(facts), DocumentPayLinkPolicy.ParseChannels(def), DocumentPayLinkPolicy.UnavailableReason(facts));
    }

    public async Task<PayLinkResult> EnsureLinkAsync(Guid companyId, Guid documentId, IReadOnlyCollection<string>? scheduleChannels, CancellationToken ct = default,
        bool allowReissueRevoked = false, string? actor = null, bool customerPage = false)
    {
        var d = await LoadDocAsync(companyId, documentId, null, ct);
        if (d == null) return new PayLinkResult(null, null, Array.Empty<string>(), "ไม่พบเอกสาร");
        // รอบ 267 (267-1-1): ลิงก์หน้าเอกสาร — ด่านของ "หน้า" (ชนิด/ฝั่ง/สถานะ/ใบค่าบริการผู้เช่า) แทนด่านของ "การจ่าย" ·
        // ปุ่มจ่ายบนหน้ายังตัดสินสดด้วย Refusal(d) ทุกครั้งที่เปิด (PublicViewAsync) — ออกลิงก์ได้ ≠ จ่ายได้
        var payRefusal = Refusal(d);
        if (customerPage)
        {
            if (CustomerDocPage.IssueRefusal(d.Type, d.Status, await CustomerFacingAsync(companyId, d, ct), d.PlatformTenantDocument) is { } noPage)
                return new PayLinkResult(null, null, Array.Empty<string>(), noPage);
        }
        else if (payRefusal is { } no) return new PayLinkResult(null, null, Array.Empty<string>(), no);
        // ฝ่ายค้าน 259 B-L5 · C-M1 (259-2-8): ลิงก์ที่ผู้มีสิทธิ์เพิกถอนไว้ — ออกใหม่ได้เฉพาะผู้เรียกที่ตั้งใจ (ปุ่มออกลิงก์ของพนักงาน · อีเมลตามตารางที่พนักงานตั้ง)
        // หน้าต่างส่งเอง/พอร์ทัลลูกค้า ⇒ โยนชนิดเฉพาะ (ไม่ล้างการเพิกถอนเงียบ)
        if (d.PayLinkRevokedAt != null && !allowReissueRevoked) throw new PayLinkRevokedException();
        var (all, companyDefault, unavailable) = await ChannelsAsync(companyId, ct);
        if (unavailable != null && !customerPage) return new PayLinkResult(null, null, Array.Empty<string>(), unavailable);
        // รอบ 248 (AUTOPILOT §5.1): ช่องทาง = ตารางส่ง › ผู้ติดต่อ › ค่าบริษัท (ตัวต่อชั้นเดียว AutopilotSettingsResolver) — ผู้ติดต่อไม่ได้ตั้ง = เท่าเดิม
        var contactLayer = await ContactPayLayerAsync(companyId, d.ContactId, all, ct);
        // ฝ่ายค้าน 267 M3 (267-1-27): ทางจ่าย (ปุ่มออกลิงก์ชำระ/ตารางส่ง) = ผู้เรียกตั้งใจให้จ่ายได้ (companyAttach: true เดิม) ·
        // ลิงก์หน้าเอกสาร = ใช้ค่า "แนบลิงก์ชำระ" ของบริษัทจริง ⇒ บริษัท/ผู้ติดต่อที่ไม่เปิดรับชำระทางลิงก์ (ปิด · โอนอย่างเดียว · ไม่มี) ไม่ได้ปุ่มจ่ายจากหน้าเอกสาร
        var companyAttach = !customerPage || (await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => (bool?)s.PayLinkDefaultEnabled).FirstOrDefaultAsync(ct) ?? false);
        var choice = AutopilotSettingsResolver.Resolve(
            scheduleChannels is { Count: > 0 } ? new AutopilotSettingsResolver.Layer(null, scheduleChannels.ToList()) : null,
            null, contactLayer, companyAttach, companyDefault);
        var allowed = DocumentPayLinkPolicy.Allowed(all, choice.Channels, null);
        var payable = payRefusal == null && unavailable == null && choice.Attach && DocumentPayLinkPolicy.OnlineOnly(allowed).Count > 0;
        if (DocumentPayLinkPolicy.OnlineOnly(allowed).Count == 0 && !customerPage)
            return new PayLinkResult(null, null, allowed, "ช่องทางที่เลือกไม่มีช่องทางออนไลน์ที่บริษัทเปิดอยู่ — ตรวจช่องทางในตารางส่งเอกสาร/ค่าตั้งส่งเอกสารอัตโนมัติ");

        var now = DateTime.UtcNow;
        // ใบที่จ่ายไม่ได้ (ใบเสนอราคา · ใบเสร็จ · ชำระครบ · บริษัทยังไม่เปิดรับออนไลน์) ⇒ ไม่ตรึงช่องทาง (null = ยังไม่เคยเลือก)
        var json = payable ? DocumentPayLinkPolicy.SerializeChannels(allowed) : null;
        var expiresAt = d.PayLinkExpiresAt;
        // หนึ่งลิงก์ต่อใบ — ลิงก์เดิมยังใช้ได้ + ถอด token ฉบับเข้ารหัสได้ ⇒ URL เดิม (อีเมลรอบก่อนยังใช้ได้) · ช่องทางตามรอบล่าสุด
        // รอบ 267: ลิงก์หน้าเอกสาร (customerPage) ตัดสิน "ยังใช้ได้" ระดับหน้า — ใบแจ้งหนี้ที่ออกใบกำกับแล้วยังเปิดหน้าได้ ⇒ ลิงก์เดิมไม่ถูกหมุนทิ้ง ·
        // ทางจ่ายคงเงื่อนไขใบกำกับของรอบ 250 (มาถึงตรงนี้ได้เฉพาะใบที่ไม่มีใบกำกับอ้างอยู่แล้ว — Refusal ตัดไปก่อน)
        string? token = DocumentPayLinkPolicy.LinkUsable(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, now, customerPage ? null : d.SupersedingTaxInvoice)
            ? Recover(d.PayLinkTokenProtected, d.PayLinkTokenHash) : null;
        if (token != null && d.PayLinkChannelsJson == null && json != null)
        {
            // ฝ่ายค้าน 267 M4 (267-1-27): ลิงก์หน้าเอกสารที่ออกตอน "ยังจ่ายไม่ได้" (ยังไม่เคยตรึงช่องทาง) ⇒ ตรึงช่องทางครั้งแรกตอนที่จ่ายได้
            // (เขียนแบบมีเงื่อนไข "ยังว่างอยู่" — ไม่ใช่การเขียนทับตัวเลือกของลิงก์ที่ใช้อยู่ · 259 C-M1 คงเดิม)
            await PinChannelsIfUnsetAsync(companyId, d, json, ct);
        }
        else if (token != null)
        {
            // ฝ่ายค้าน 259 C-M1: ไม่เขียนทับช่องทางของลิงก์ที่ยังใช้ได้ (ลิงก์ที่ส่งออกไปแล้วต้องเปิดได้ด้วยช่องทางเดิม) ⇒ คืนช่องทางที่ลิงก์ถืออยู่
            allowed = DocumentPayLinkPolicy.Allowed(all, DocumentPayLinkPolicy.ParseChannels(d.PayLinkChannelsJson), companyDefault);
        }
        else
        {
            var fresh = DocumentPayLinkPolicy.NewToken();
            var freshHash = DocumentPayLinkPolicy.TokenHash(fresh);
            // ชื่อช่องตรงกับคอลัมน์ — ทะเบียนหมุนกุญแจ (SecretReprotectJob) ตรวจด้วย tools/secret_registry_check.py
            var sealedToken = new { PayLinkTokenProtected = _secrets.Protect(fresh) };
            var exp = CustomerDocPage.NewLinkExpiry(now);   // รอบ 267: อายุลิงก์ตัวเลขเดียว (ลิงก์ชำระ = ลิงก์หน้าเอกสาร)
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
            if (n > 0)
            {
                token = fresh;
                expiresAt = exp;
                // ฝ่ายค้าน 259 C-M1: ออกใหม่แทนลิงก์เดิม (หมดอายุ/ถูกเพิกถอน/ถอด token ไม่ได้) ⇒ audit ทุกครั้ง
                if (oldHash != null)
                {
                    _db.AddChainedAuditLog(new AuditLog
                    {
                        CompanyId = companyId, UserEmail = actor ?? "system", Action = AuditAction.Update,
                        EntityType = "DocumentPayLink", EntityId = d.Id.ToString(),
                        NewValues = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            @event = "pay-link-reissued", wasRevoked = d.PayLinkRevokedAt != null, previousExpiresAt = d.PayLinkExpiresAt,
                            allowReissueRevoked, channels = json, customerPage,
                        }),
                        Timestamp = now,
                    });
                    await _db.SaveChangesAsync(ct);
                }
            }
            else
            {
                var won = await _db.Documents.AsNoTracking().Where(x => x.Id == d.Id && x.CompanyId == companyId)
                    .Select(x => new { x.PayLinkTokenHash, x.PayLinkTokenProtected, x.PayLinkExpiresAt }).FirstOrDefaultAsync(ct);
                token = Recover(won?.PayLinkTokenProtected, won?.PayLinkTokenHash);
                expiresAt = won?.PayLinkExpiresAt;
                if (token == null) return new PayLinkResult(null, null, allowed, "ออกลิงก์ชำระเงินชนกับคำขออื่น — ลองใหม่อีกครั้ง");
            }
        }
        var origin = await SystemBaseUrl.ResolveAsync(_db, _config, ct);
        if (string.IsNullOrWhiteSpace(origin))
            return new PayLinkResult(null, null, allowed, "ยังไม่ได้ตั้ง URL ของระบบ (App:BaseUrl) — สร้างลิงก์สาธารณะไม่ได้")
            {
                // รอบ 267: token ออกแล้ว — ผู้เรียกที่รู้ origin ของคำขอ (ปุ่มของพนักงาน) ประกอบ URL เองได้ผ่าน EnsureCustomerLinkAsync(fallbackOrigin)
                Token = token, ExpiresAt = expiresAt,
            };
        // ยอดของลิงก์มีความหมายเฉพาะใบที่ชั้นจ่ายรับ (ใบเสนอราคา/ใบเสร็จ ไม่มียอดที่ต้องโอน)
        var amounts = payRefusal == null ? (await AmountsAsync(companyId, d, ct)).Amounts : null;
        return new PayLinkResult(DocumentPayLinkPolicy.Url(origin, companyId, token), amounts, payable || !customerPage ? allowed : Array.Empty<string>(), null)
        {
            Token = token, ExpiresAt = expiresAt,
        };
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
        // ชนิดที่ไม่มีลิงก์ชำระเลย (ใบซื้อ/ใบเสร็จ ฯลฯ) ⇒ ไม่มีอะไรต้องบอก (ไม่ใช่ "ไม่แนบเพราะ…" บนทุกแถวคิว)
        if (doc.DocumentType is not (DocumentType.Invoice or DocumentType.TaxInvoice or DocumentType.DebitNote)) return null;
        // 259-2-8: อีเมลตามตารางที่พนักงานตั้ง = ผู้เรียกที่ตั้งใจ (ความหมายเดิมของการเพิกถอน: "อีเมลรอบถัดไปจะได้ลิงก์ใหม่")
        var r = await EnsureLinkAsync(doc.CompanyId, doc.Id, DocumentPayLinkPolicy.ParseChannels(rule.PayLinkChannelsJson), ct,
            allowReissueRevoked: true, actor: "email-schedule");
        if (!r.Ok)
        {
            // ไม่ใช่ความผิดพลาดของการส่ง (ใบชำระครบ/บริษัทปิดรับออนไลน์/ยังไม่ตั้ง URL ของระบบ) — อีเมลยังส่งตามปกติโดยไม่มีลิงก์ ·
            // รอบ 250 (WP-02 §2.4 ข้อ 2 · คำตัดสิน §2 #9): คืนผลพร้อมเหตุ (Url = null) ⇒ ตัวเข้าคิวเขียนเหตุบนแถวคิว (ส่งได้แต่บอกว่าไม่มีลิงก์เพราะอะไร) — เดิมคืน null เงียบ
            _logger?.LogInformation("ไม่แนบลิงก์ชำระเงินให้ {Doc}: {Why}", doc.Id, r.Refusal);
            return r;
        }
        return r;
    }

    public async Task<string?> CurrentUrlAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var d = await LoadDocAsync(companyId, documentId, null, ct);
        if (d == null || Refusal(d) != null
            || !DocumentPayLinkPolicy.LinkUsable(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, DateTime.UtcNow, d.SupersedingTaxInvoice)) return null;
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
        if (DocumentPayLinkPolicy.LinkUsable(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, DateTime.UtcNow, d.SupersedingTaxInvoice))
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
        var raw = DocumentPayLinkPolicy.LinkUsable(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, DateTime.UtcNow, d.SupersedingTaxInvoice)
            ? Recover(d.PayLinkTokenProtected, d.PayLinkTokenHash) : null;
        var usable = raw != null;
        var origin = await SystemBaseUrl.ResolveAsync(_db, _config, ct);
        var pageRaw = CustomerDocPage.StateOf(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, DateTime.UtcNow) == CustomerDocPage.LinkState.Usable
            ? (raw ?? Recover(d.PayLinkTokenProtected, d.PayLinkTokenHash)) : null;
        var pageUrl = pageRaw != null && origin != null ? DocumentPayLinkPolicy.Url(origin, companyId, pageRaw) : null;
        var runs = await _db.PostPaymentAutomationRuns.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.DocumentId == documentId)
            .OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync(ct);
        var postPay = await PostPaymentAutomationService.ResolveForAsync(_db, companyId, documentId, ct);
        return new DocumentPayLinkStatusDto(
            Refusal: Refusal(d) ?? unavailable,
            Url: raw != null && origin != null ? DocumentPayLinkPolicy.Url(origin, companyId, raw) : null,
            ExpiresAt: d.PayLinkExpiresAt,
            Usable: usable,
            Channels: Options(allowed),
            Gross: amounts.Gross, Wht: amounts.Wht, Net: amounts.Net,
            WhtNote: whtNote,
            Runs: runs.Select(r => new PostPaymentRunDto(r.Id, r.PaymentId, r.Source, r.Status, PostPaymentAutomationPolicy.StatusLabel(r.Status),
                r.Attempts, r.Message, r.StepsDone, r.ReceiptDocumentId, r.CreatedAt, r.CompletedAt)).ToList())
        {
            // ฝ่ายค้าน 267 H2: สถานะลิงก์ "ระดับหน้า" (ไม่ขึ้นกับว่าจ่ายได้ไหม) — ปุ่มเพิกถอนบนหน้าพนักงานต้องขึ้นกับค่านี้
            // (ใบเสนอราคา · ใบส่งของ · ใบเสร็จ · ใบที่ชำระครบ/ออกใบกำกับแล้ว ก็มีลิงก์ที่ลูกค้าเปิดได้)
            PageLinkUsable = pageUrl != null,
            PageUrl = pageUrl,
            PageLinkNote = pageUrl == null ? null : PageLinkStaffNote(d, DateTime.UtcNow),
            // รอบ 267 P4 (267-1-40): ค่าตั้งหลังรับชำระต่อใบ + ค่าที่มีผลตอนนี้ (ตัวโหลด/ตัวต่อชั้นเดียวกับงานหลังรับชำระ)
            PostPaySettingApplies = postPay.Found && PostPaymentAutomationPolicy.DocumentLayerAllowed(postPay.Type),
            PostPayReceiptMode = PostPaymentAutomationPolicy.ModeCode(postPay.DocEmailReceipt),
            PostPayPartialMode = PostPaymentAutomationPolicy.ModeCode(postPay.DocIncludePartial),
            PostPayEffectiveLabel = PostPaymentAutomationPolicy.EffectiveLabel(postPay.Resolution),
        };
    }

    private static string PageLinkStaffNote(DocFacts d, DateTime nowUtc)
        => CustomerDocPage.IsPayOnlyLink(d.PayLinkIssuedAt, d.PayLinkExpiresAt)
            ? "ลิงก์นี้ออกก่อนมีหน้าเอกสาร — ลูกค้าเห็นเฉพาะยอดและปุ่มชำระ (ยกเลิกลิงก์แล้วออกใหม่เพื่อให้เห็นรายการ/ดาวน์โหลด PDF)"
            : "ลูกค้าที่ถือลิงก์นี้เปิดดูเอกสาร/ดาวน์โหลด PDF ได้จนกว่าจะหมดอายุหรือถูกยกเลิก";

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
        // รอบ 250 (WP-02 §2.3): ใบแจ้งหนี้ที่ออกใบกำกับแล้ว ⇒ ลิงก์เดิมใช้ไม่ได้ทันที (ตัดสินสด — fact จาก LoadDocAsync) · ข้อความบอกให้ชำระที่ใบกำกับ
        // รอบ 267 (267-1-1 · 267-1-22): ลิงก์ = ลิงก์หน้าเอกสาร ⇒ "เปิดหน้าได้" ตัดสินระดับหน้า (มี hash · ไม่เพิกถอน · ไม่หมดอายุ) —
        // ใบแจ้งหนี้ที่ออกใบกำกับแล้วยังเปิดดู/ดาวน์โหลดได้ · "จ่ายได้" ยังตัดสินสดรวมเงื่อนไขใบกำกับ (payLinkUsable ด้านล่าง — กติการอบ 250 คงเดิม) ·
        // ใบกลับเป็นร่าง/ลูกค้าไม่เห็น ⇒ ไม่เปิดเผยอะไรเหมือนลิงก์ใช้ไม่ได้
        if (CustomerDocPage.StateOf(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, now) != CustomerDocPage.LinkState.Usable)
            return UnusableView(d, now);
        var row = await LoadPageRowAsync(companyId, d.Id, ct);
        if (row == null || CustomerDocPage.ViewRefused(d.Type, d.Status, row.CustomerFacing))
            return UnusableView(d, now);

        var (all, companyDefault, unavailable) = await ChannelsAsync(companyId, ct);
        var allowed = DocumentPayLinkPolicy.Allowed(all, DocumentPayLinkPolicy.ParseChannels(d.PayLinkChannelsJson), companyDefault);
        var online = DocumentPayLinkPolicy.OnlineOnly(allowed);
        // ใบที่ไม่ใช่ชนิดตั้งหนี้ (ใบเสนอราคา · ใบส่งของ · ใบเสร็จ ฯลฯ) ไม่มียอดของลิงก์ — ไม่เรียกตัวคิด WHT ของการรับชำระ
        var payType = CustomerDocPage.IsPayableType(d.Type);
        var amounts = payType ? (await AmountsAsync(companyId, d, ct)).Amounts : new DocumentPayLinkPolicy.Amounts(0m, 0m, 0m);

        // ตัดสินสดทุกครั้ง: ใบแจ้งหนี้ที่ออกใบกำกับแล้ว (ลิงก์ใช้จ่ายไม่ได้ทันที — รอบ 250) · ใบยกเลิก/ชำระครบ · บริษัทปิดรับออนไลน์ · ช่องทางของใบไม่เหลือ
        var payLinkUsable = DocumentPayLinkPolicy.LinkUsable(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, now, d.SupersedingTaxInvoice);
        var blocked = (payLinkUsable ? null : DocumentPayLinkPolicy.UnusableNote(d.SupersedingTaxInvoice, UnusableLinkNote))
            ?? Refusal(d)
            // ฝ่ายค้าน 267 M3: ลิงก์ที่ยังไม่เคยถูกเปิดให้จ่าย (ไม่มีช่องทางที่ตรึงไว้ — ออกเป็นลิงก์หน้าเอกสารขณะบริษัท/ผู้ติดต่อไม่เปิดรับชำระทางลิงก์)
            // ⇒ ไม่มีปุ่มจ่าย/บัญชีรับโอน/ฟอร์มสลิป จนกว่าทางจ่ายจะตรึงช่องทาง (ปุ่มออกลิงก์ชำระ · ตารางส่ง)
            ?? (d.PayLinkChannelsJson == null ? PayNotOpenedNote : null)
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
        if (allowed.Contains(DocumentPayLinkPolicy.BankTransferCode, StringComparer.OrdinalIgnoreCase) && Refusal(d) == null && d.PayLinkChannelsJson != null)
            transfer = await TransferBlockAsync(companyId, d.Id, amounts.Net, ct);

        var title = heading.Title;
        // รอบ 253 (WP-10 · AUTO_SEND §6.3 · §11 ข้อ 3): ตัวเลือก "ไม่หัก ณ ที่จ่าย" — ตัวตัดสินเดียว NoWhtChoicePolicy บนยอดชุดเดียวกับลิงก์ (AmountsFor)
        var noWht = blocked == null
            ? NoWhtChoicePolicy.OfferFor(amounts, await BuyerWithholdsAsync(companyId, d.ContactId, ct))
            : new NoWhtChoicePolicy.Offer(false, false, amounts.Net);
        var target = new PublicPayTarget(PaymentSourceKind.Document, d.Id, Guid.Empty, blocked == null ? amounts.Net : 0m,
            $"{title} {d.DocumentNumber}", contact?.Email, contact?.Phone, d.DocumentNumber, BlockedNote: blocked,
            AllowedMethods: online, ContactId: d.ContactId);
        var slipOffered = Accounting.Helpers.SlipEvidenceRule.PayLinkSlipAllowed(transfer != null, amounts.Net);
        // รอบ 267 P1: ส่วนหน้าเอกสาร — ปุ่มทั้งหมดจากตัวตัดสินเดียว (CustomerDocPage.Actions ใน BuildPageAsync) บนข้อเท็จจริงชุดเดียวกับช่องจ่ายด้านล่าง
        var page = await BuildPageAsync(companyId, d, row, CustomerDocPage.Mode.Link, heading.IsEnglish,
            payOffered: blocked == null && amounts.Net > 0.005m, payBlockedNote: blocked, slipOffered: slipOffered, token: token, nowUtc: now, ct,
            // ฝ่ายค้าน 267 M7 (267-1-30): ลิงก์ชำระที่ออกก่อนรอบ 267 คงขอบเขตเดิม (หัวใบ + ยอด + จ่าย/สลิป) — ไม่ได้บรรทัด/PDF/ใบที่เกี่ยวข้องย้อนหลัง
            payOnly: CustomerDocPage.IsPayOnlyLink(row.PayLinkIssuedAt, row.PayLinkExpiresAt));
        return new PublicDocumentPayView(
            CompanyName: (heading.IsEnglish && !string.IsNullOrWhiteSpace(company?.NameEn) ? company!.NameEn : company?.Name) ?? "",
            DocumentTitle: title, DocumentNumber: d.DocumentNumber, DocumentDate: d.DocumentDate, DueDate: d.DueDate,
            CustomerName: contact?.Name,
            Gross: amounts.Gross, Wht: amounts.Wht, Net: amounts.Net, Currency: "THB",
            CanPayOnline: blocked == null && amounts.Net > 0.005m,
            // ชนิดที่ไม่มีการจ่าย (ใบเสนอราคา ฯลฯ) ⇒ ไม่มีข้อความ "จ่ายไม่ได้เพราะ…" บนหน้า (เป้าหมายรายการชำระยังถูกปิดด้วยเหตุเดิม)
            BlockedNote: payType ? blocked : null,
            OnlineMethods: blocked == null ? Options(online) : new List<PayChannelOption>(),
            Transfer: transfer,
            Card: blocked == null ? card : null,
            IsTestMode: cfg?.Mode == PaymentProviderMode.Test,
            NoWhtOffered: noWht.Offered,
            NoWhtWarn: noWht.Warn,
            NoWhtNotice: noWht.Warn ? NoWhtChoicePolicy.NoticeText : null,
            NoWhtNoticeVersion: noWht.Offered ? NoWhtChoicePolicy.NoticeVersion : null,
            NoWhtAmount: noWht.GrossAmount)
        {
            Target = target,
            // รอบ 255 WP-12B ข้อ 1: แจ้งโอนได้เมื่อหน้านี้แสดงกล่องโอน (ใบรับโอนได้ · มีบัญชีรับโอน) และยังมียอดค้าง — ไม่ขึ้นกับว่าเปิดชำระออนไลน์ไหม (โหมดโอนอย่างเดียว)
            CanSubmitSlip = Accounting.Helpers.SlipEvidenceRule.PayLinkSlipAllowed(transfer != null, amounts.Net),
            SlipAmount = amounts.Net,
            SlipAmountNote = Accounting.Helpers.SlipEvidenceRule.PayLinkSlipAllowed(transfer != null, amounts.Net)
                ? Accounting.Helpers.SlipEvidenceRule.PayLinkSlipAmountNote(amounts.Net) : null,
            SlipDocumentId = d.Id,
            Page = page,
        };
    }

    /// <summary>
    /// รอบ 253 (WP-10 · คำตัดสินเจ้าของ §11 ข้อ 3) — ลูกค้าเลือก "ไม่หัก ณ ที่จ่าย": ตรวจ token/ใบ/ยอดผ่าน <see cref="PublicViewAsync"/> ตัวเดิม →
    /// ตัวตัดสินเดียว <see cref="NoWhtChoicePolicy.AcceptRefusal"/> (เสนอจริง · ติ๊กยอมรับ · version ตรงข้อความที่แสดง) → <b>บันทึกการยอมรับ</b>
    /// (แถว <see cref="NoWhtAcceptance"/> + audit chain: ผู้กด · เวลา · hash IP · ข้อความ/version/hash · ยอด · hash หลักฐาน) ก่อนสร้างรายการชำระ ·
    /// เป้าหมายที่คืน = เป้าหมายเดิมของลิงก์ แต่ยอด = <see cref="NoWhtChoicePolicy.AmountDue"/> (Gross จากตัวคิดยอดเดิม — ไม่มีสูตรที่สอง)
    /// </summary>
    public async Task<NoWhtAcceptResult> AcceptNoWhtAsync(Guid companyId, string token, NoWhtAcceptRequest request, CancellationToken ct = default)
    {
        var view = await PublicViewAsync(companyId, token, ct);
        if (view == null) return new NoWhtAcceptResult(null, null, null);
        if (!view.CanPayOnline || view.Target.ContactId is not Guid contactId)
            return new NoWhtAcceptResult(view.Target, null, view.BlockedNote ?? "เอกสารนี้ชำระออนไลน์ไม่ได้ตอนนี้ — กรุณาติดต่อผู้ขาย");
        var amounts = new DocumentPayLinkPolicy.Amounts(view.Gross, view.Wht, view.Net);
        var withholds = await BuyerWithholdsAsync(companyId, contactId, ct);
        var offer = NoWhtChoicePolicy.OfferFor(amounts, withholds);
        if (NoWhtChoicePolicy.AcceptRefusal(offer, request.Accepted, request.NoticeVersion) is string refusal)
            return new NoWhtAcceptResult(view.Target, null, refusal);

        var raw = DateTime.UtcNow;
        var at = new DateTime(raw.Ticks - raw.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);   // ระดับวินาที — hash หลักฐานคำนวณซ้ำได้หลัง round-trip ฐาน
        var ipHash = NoWhtChoicePolicy.IpHash(NoWhtChoicePolicy.DeriveIpKey(_config?["Jwt:Secret"]), companyId, request.Ip);   // ฝ่ายค้าน L4: HMAC กุญแจของเซิร์ฟเวอร์
        var name = WhtCertUploadLinkPolicy.SanitizeSenderName(request.PayerName);
        var noticeHash = NoWhtChoicePolicy.NoticeHash();
        var row = new NoWhtAcceptance
        {
            CompanyId = companyId, DocumentId = view.Target.SourceId, ContactId = contactId, AcceptedAt = at, AcceptedByName = name, IpHash = ipHash,
            NoticeVersion = NoWhtChoicePolicy.NoticeVersion, NoticeText = NoWhtChoicePolicy.NoticeText, NoticeHash = noticeHash,
            BuyerWithholds = withholds, GrossAmount = amounts.Gross, WhtAmount = amounts.Wht, NetAmount = amounts.Net,
            EvidenceHash = NoWhtChoicePolicy.EvidenceHash(companyId, view.Target.SourceId, at, amounts.Gross, amounts.Wht, amounts.Net,
                NoWhtChoicePolicy.NoticeVersion, noticeHash, ipHash, name),
            RetainUntil = NoWhtChoicePolicy.RetainUntil(at), LegalHold = true,
            CreatedBy = "public-pay",
        };
        _db.NoWhtAcceptances.Add(row);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = name ?? "ผู้ถือลิงก์ชำระ (ไม่ได้ล็อกอิน)", Action = AuditAction.Create,
            EntityType = nameof(NoWhtAcceptance), EntityId = row.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "no-wht-accepted", ruleCode = NoWhtChoicePolicy.RuleCode,
                legalReference = "ประมวลรัษฎากร ม.3 เตรส · ม.50 · ม.54 · ท.ป.4/2528",
                documentId = view.Target.SourceId, view.DocumentNumber, row.GrossAmount, row.WhtAmount, row.NetAmount, row.BuyerWithholds,
                row.NoticeVersion, row.NoticeHash, row.IpHash, row.EvidenceHash,
            }),
            Timestamp = at,
        });
        await _db.SaveChangesAsync(ct);
        return new NoWhtAcceptResult(view.Target with { AmountDue = NoWhtChoicePolicy.AmountDue(amounts, offer, noWithholding: true) }, row.Id, null);
    }

    /// <summary>ผูกการยอมรับกับรายการชำระที่สร้างตามมา (ตัวบันทึกรับชำระอ่านด้วย PaymentIntentId) · ผูกแล้วไม่ทับ</summary>
    public async Task LinkNoWhtIntentAsync(Guid companyId, Guid acceptanceId, Guid paymentIntentId, CancellationToken ct = default)
        => await _db.NoWhtAcceptances
            .Where(a => a.Id == acceptanceId && a.CompanyId == companyId && a.PaymentIntentId == null)
            .ExecuteUpdateAsync(x => x.SetProperty(a => a.PaymentIntentId, (Guid?)paymentIntentId), ct);

    /// <summary>กล่อง "โอนเข้าบัญชี" ของใบ (ตัวเดียว — หน้าชำระสาธารณะ + พอร์ทัลลูกค้า · รอบ 250 WP-03 ฝ่ายค้าน H1):
    /// บัญชีของใบก่อน (ตัวเลือกบัญชีเดียวกับกระดาษ <c>PaymentQrService.PickDocumentAccountAsync</c>) · ไม่มี ⇒ บัญชีรับโอนของเว็บ ·
    /// QR พร้อมเพย์ฝังยอดสุทธิ <paramref name="net"/> (หลัง WHT ที่ผู้ซื้อจะหัก — ผู้เรียกต้องส่ง <c>AmountsAsync().Net</c>)</summary>
    private async Task<PublicTransferInfo?> TransferBlockAsync(Guid companyId, Guid documentId, decimal net, CancellationToken ct)
    {
        // รอบ 246 QR (ฝ่ายค้าน S2): บัญชีของใบก่อน — ตัวเลือกบัญชีเดียวกับกระดาษ (PaymentQrService.PickDocumentAccountAsync) · ไม่มี ⇒ บัญชีรับโอนของเว็บ (เดิม)
        var transfer = await DocumentAccountTransferAsync(companyId, documentId, net, ct);
        if (transfer != null) return transfer;
        var gw = await SiteTransferAccount.Rows(_db.Set<SitePaymentGateway>().AsNoTracking(), companyId, null).FirstOrDefaultAsync(ct);
        if (gw == null) return null;
        // รอบ 246 QR: QR พร้อมเพย์ที่เซิร์ฟเวอร์สร้าง (ฝังยอดสุทธิของลิงก์ · data URI จากไบต์ที่สร้างเอง) — เลขที่ใช้ไม่ได้ ⇒ ไม่มี QR (ข้อความเดิม)
        string? qr = null;
        var ppId = PaymentQrPolicy.NormalizePromptPayId(gw.PromptPayId).Id;
        if (ppId != null && net > 0.005m)
            qr = "data:image/png;base64," + Convert.ToBase64String(new PromptPayService().GenerateQrImage(ppId, net, size: 330));
        return new PublicTransferInfo(gw.BankName, gw.BankAccountNumber, gw.BankAccountName, gw.PromptPayId, qr);
    }

    /// <summary>รอบ 250 WP-03 (ฝ่ายค้าน H1): ตัวเลือกโอน/QR ของใบสำหรับพอร์ทัลลูกค้า — <b>กติกาเดียวกับหน้าชำระ</b>:
    /// <see cref="DocumentPayLinkPolicy.IssueRefusal"/> (ฝั่งขาย · THB · ออกแล้ว · ยอดค้าง &gt; 0 · ใบกำกับอ้างแล้ว) แล้วจึงประกอบกล่องโอนด้วยยอดสุทธิหลัง WHT ·
    /// ผู้เรียกต้องตรวจความเป็นเจ้าของใบก่อน (พอร์ทัล = <c>PortalDocuments</c>) · null = ไม่พบใบ</summary>
    public async Task<DocumentTransferView?> TransferForDocumentAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var d = await LoadDocAsync(companyId, documentId, null, ct);
        if (d == null) return null;
        if (Refusal(d) is { } refusal) return new DocumentTransferView(refusal, 0m, 0m, 0m, null);
        var (amounts, _) = await AmountsAsync(companyId, d, ct);
        return new DocumentTransferView(null, amounts.Gross, amounts.Wht, amounts.Net,
            await TransferBlockAsync(companyId, d.Id, amounts.Net, ct));
    }

    public async Task<DocumentTransferView?> DueAmountsForDocumentAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var d = await LoadDocAsync(companyId, documentId, null, ct);
        if (d == null) return null;
        if (Refusal(d) is { } refusal) return new DocumentTransferView(refusal, 0m, 0m, 0m, null);
        var (amounts, _) = await AmountsAsync(companyId, d, ct);
        return new DocumentTransferView(null, amounts.Gross, amounts.Wht, amounts.Net, null);
    }

    /// <summary>รอบ 246 QR (ฝ่ายค้าน S2) — บัญชีรับโอนของใบนี้จากตัวเลือกบัญชีเดียวกับกระดาษ (แหล่งเงินของใบ → บัญชีของใบ → เทมเพลต → บัญชีเดียวที่ตั้ง QR) +
    /// QR ของบัญชีนั้น (พร้อมเพย์ฝังยอดสุทธิของลิงก์ · รูปที่อัปโหลด) · ไม่มีที่เก็บ/ไม่พบบัญชี = null (ผู้เรียกใช้บัญชีรับโอนของเว็บ)</summary>
    private async Task<PublicTransferInfo?> DocumentAccountTransferAsync(Guid companyId, Guid documentId, decimal net, CancellationToken ct)
    {
        if (_storage == null) return null;
        var doc = await _db.Documents.AsNoTracking().Include(x => x.Brand)
            .FirstOrDefaultAsync(x => x.Id == documentId && x.CompanyId == companyId, ct);
        if (doc == null) return null;
        // รอบ 260-2: ตัวเลือกบัญชีของใบตัวเดียวกับค่าแนะนำตอนยืนยันสลิป (SlipReceivingAccount — "บัญชีที่แสดงให้ลูกค้าโอน")
        var (acct, _) = await PaymentQrService.PickForDocumentAsync(_db, _storage.Locator.AttachmentsRoot, companyId, doc, ct);
        if (acct == null) return null;
        var superseded = await PaymentQrService.SupersededByTaxInvoiceAsync(_db, companyId, doc, ct);
        var qr = await PaymentQrService.BuildAccountQrAsync(_storage, new PromptPayService(), _logger, companyId, acct, acct.PromptPayId,
            acct.PaymentQrSource, PaymentQrPolicy.EmbedAmount(null, net, superseded), ct);
        return new PublicTransferInfo(acct.BankName, acct.AccountNumber, acct.AccountName,
            qr?.PromptPayId == null ? null : PaymentQrPolicy.DisplayPromptPayId(qr.PromptPayId),
            qr == null ? null : "data:image/png;base64," + Convert.ToBase64String(qr.Png));
    }

    /// <summary>ฝ่ายค้าน 267 M3: ลิงก์หน้าเอกสารที่ยังไม่ถูกเปิดให้จ่าย</summary>
    public const string PayNotOpenedNote = "ผู้ขายยังไม่เปิดรับชำระผ่านลิงก์สำหรับเอกสารนี้ — กรุณาติดต่อผู้ขาย";

    /// <summary>ฝ่ายค้าน 267 M4 — ตรึงช่องทางของลิงก์ที่ยังไม่เคยมีช่องทาง (เขียนเฉพาะเมื่อยังว่างและยังเป็น token เดิม · สองคำขอพร้อมกัน = ค่าของคนแรก)</summary>
    private async Task PinChannelsIfUnsetAsync(Guid companyId, DocFacts d, string json, CancellationToken ct)
    {
        var sameHash = d.PayLinkTokenHash;
        await _db.Documents
            .Where(x => x.Id == d.Id && x.CompanyId == companyId && x.PayLinkTokenHash == sameHash && x.PayLinkChannelsJson == null)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.PayLinkChannelsJson, (string?)json), ct);
    }

    /// <summary>
    /// ฝ่ายค้าน 267 M7 (267-1-31) — เพิกถอนลิงก์หน้าเอกสารทุกใบของผู้ติดต่อ (ลบ/ปิดผู้ติดต่อ · DSR erase): ผู้เขียน <c>PayLinkRevokedAt</c> อยู่ไฟล์นี้ไฟล์เดียว ·
    /// static เพราะผู้เรียก (<c>DocumentService.DeleteContactAsync</c> · <c>PdpaService</c>) ไม่พึ่ง service นี้ · คืนจำนวนใบที่เพิกถอน + แถว audit chain 1 แถว
    /// (ผู้เรียก SaveChanges) · ออกลิงก์ใหม่ภายหลังได้เฉพาะปุ่มของพนักงาน (259-2-8)
    /// </summary>
    public static async Task<int> RevokeForContactAsync(AccountingDbContext db, Guid companyId, Guid contactId, string actor, string reason, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var n = await db.Documents
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId && d.PayLinkTokenHash != null && d.PayLinkRevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.PayLinkRevokedAt, (DateTime?)now), ct);
        if (n > 0)
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserEmail = actor, Action = AuditAction.Update, EntityType = "DocumentPayLink", EntityId = contactId.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event = "pay-links-revoked-for-contact", contactId, documents = n, reason, ruleCode = CustomerDocPage.RuleCode }),
                Timestamp = now,
            });
        return n;
    }

    public const string UnusableLinkNote = "ลิงก์นี้ใช้ไม่ได้แล้ว (หมดอายุหรือถูกยกเลิก) — กรุณาติดต่อผู้ขายเพื่อขอลิงก์ใหม่";

    /// <summary>มุมมองของลิงก์ที่ใช้ไม่ได้ — ไม่มีข้อมูลของใบเลย (ฝ่ายค้าน S2) · รอบ 250: ใบแจ้งหนี้ที่ออกใบกำกับแล้ว ⇒ ข้อความบอกให้ชำระที่ใบกำกับ</summary>
    private static PublicDocumentPayView UnusableView(DocFacts d, DateTime nowUtc)
    {
        // รอบ 267: ใบแจ้งหนี้ที่ออกใบกำกับแล้วเปิดหน้าได้ตามปกติ ⇒ มุมมองนี้เหลือเฉพาะ เพิกถอน/หมดอายุ/ใบที่ลูกค้าไม่เห็น — ข้อความเดียว
        var note = DocumentPayLinkPolicy.UnusableNote(null, UnusableLinkNote);
        var state = CustomerDocPage.StateOf(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, nowUtc);
        return new PublicDocumentPayView(
            CompanyName: "", DocumentTitle: "", DocumentNumber: "", DocumentDate: default, DueDate: null, CustomerName: null,
            Gross: 0m, Wht: 0m, Net: 0m, Currency: "THB", CanPayOnline: false, BlockedNote: note,
            OnlineMethods: new List<PayChannelOption>(), Transfer: null, Card: null, IsTestMode: false)
        {
            Target = new PublicPayTarget(PaymentSourceKind.Document, d.Id, Guid.Empty, 0m, "", null, null, "",
                BlockedNote: note, AllowedMethods: Array.Empty<string>(), ContactId: d.ContactId),
            // หมดอายุไม่นาน ⇒ หน้าแสดงปุ่ม "ขอลิงก์ใหม่" (ไม่มีข้อมูลของใบ) · เพิกถอน ⇒ ไม่มีปุ่ม (ผู้ขายตั้งใจปิด)
            Page = CustomerDocPageView.Unusable(state, CustomerDocPage.CanRequestNewLink(state, d.PayLinkExpiresAt, nowUtc)),
        };
    }
}
