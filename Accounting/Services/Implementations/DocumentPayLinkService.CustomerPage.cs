using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 267 แพ็ก P1 — <b>หน้าเอกสารสำหรับลูกค้า</b> บน token เดิมของลิงก์ชำระ (คำตัดสิน 267-1-1 · DOCUMENT_FLOW §2.12n) ·
/// ทุกการตัดสิน (ออกลิงก์ได้ไหม · เปิดได้ไหม · ปุ่มไหนเปิด · ข้อความ) อยู่ที่ <see cref="CustomerDocPage"/> — ที่นี่โหลดข้อเท็จจริงแล้วส่งต่อเท่านั้น ·
/// อ่านอย่างเดียวทั้งไฟล์ ยกเว้น (1) ประทับ "เปิดแล้ว" (2) บันทึกคำขอลิงก์ใหม่ลง audit chain — ไม่มีเส้นเงิน/เส้นลงนามใหม่ (P2)
/// </summary>
public partial class DocumentPayLinkService
{
    /// <summary>จำนวนใบที่เกี่ยวข้องสูงสุดที่หน้าแสดง (ใบวางบิลใหญ่ ⇒ แสดงชุดแรก · ลูกค้าดูครบได้จาก PDF ของใบวางบิล)</summary>
    private const int MaxRelated = 20;
    private const int MaxLines = 500;

    /// <summary>ชนิดของแถว audit "ลูกค้าขอลิงก์ใหม่" (หนึ่งแถว/ใบ/วัน — ตัวกันซ้ำอ่านจากแถวนี้ ไม่ใช่ตัวนับในหน่วยความจำ)</summary>
    public const string LinkRequestEntity = "DocumentLinkRequest";

    /// <summary>ข้อเท็จจริงของใบสำหรับหน้าเอกสาร (ฉายเฉพาะช่องที่หน้าใช้ — ไม่ดึงรูปลายเซ็น/หมายเหตุภายใน)</summary>
    private sealed class PageRow
    {
        public Guid Id { get; init; }
        public Guid ContactId { get; init; }
        public DocumentType Type { get; init; }
        public DocumentStatus Status { get; init; }
        public string Currency { get; init; } = "THB";
        public decimal SubTotal { get; init; }
        public decimal DiscountAmount { get; init; }
        public decimal VatAmount { get; init; }
        public decimal WithholdingTaxAmount { get; init; }
        public decimal TotalAmount { get; init; }
        public decimal PaidAmount { get; init; }
        public decimal BalanceDue { get; init; }
        public int RevisionNumber { get; init; }
        public Guid? RelatedDocumentId { get; init; }
        public Guid? ReplacedByDocumentId { get; init; }
        public bool? CnDnPurchaseSideOverride { get; init; }
        public DateTime? SignLinkIssuedAt { get; init; }
        public DateTime? PayLinkIssuedAt { get; init; }
        public DateTime? PayLinkExpiresAt { get; init; }
        public DateTime? QuotationAcceptedAt { get; init; }
        public string? QuotationAcceptedBy { get; init; }
        public DateTime? DeliverySignedAt { get; init; }
        public string? DeliverySignedBy { get; init; }
        public bool ContactSupplierOnly { get; set; }
        public bool ContactCustomerOnly { get; set; }
        /// <summary>ลูกค้าเห็นใบนี้ได้ไหม — ตัวตัดสินฝั่งเดียวกับพอร์ทัล/ลิงก์ชำระ (<see cref="DocumentPayLinkPolicy.IsCustomerFacing"/>)</summary>
        public bool CustomerFacing { get; set; }
    }

    private async Task<(bool SupplierOnly, bool CustomerOnly)> ContactSidesAsync(Guid companyId, Guid contactId, CancellationToken ct)
    {
        var c = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.Id == contactId && x.CompanyId == companyId)
            .Select(x => new { x.IsSupplier, x.IsCustomer }).FirstOrDefaultAsync(ct);
        return c == null ? (false, false) : (c.IsSupplier && !c.IsCustomer, c.IsCustomer && !c.IsSupplier);
    }

    /// <summary>ลูกค้าเห็นใบนี้ได้ไหม (ด่านออกลิงก์หน้าเอกสาร) — ข้อเท็จจริงเดียวกับ <see cref="LoadPageRowAsync"/></summary>
    private async Task<bool> CustomerFacingAsync(Guid companyId, DocFacts d, CancellationToken ct)
    {
        var (supplierOnly, customerOnly) = await ContactSidesAsync(companyId, d.ContactId, ct);
        return DocumentPayLinkPolicy.IsCustomerFacing(d.Type, d.RelatedType, d.CnDnPurchaseSideOverride, supplierOnly, customerOnly);
    }

    private async Task<PageRow?> LoadPageRowAsync(Guid companyId, Guid documentId, CancellationToken ct)
    {
        var row = await _db.Documents.AsNoTracking()
            .Where(x => x.Id == documentId && x.CompanyId == companyId && !x.IsDeleted)
            .Select(x => new PageRow
            {
                Id = x.Id, ContactId = x.ContactId, Type = x.DocumentType, Status = x.Status, Currency = x.Currency,
                SubTotal = x.SubTotal, DiscountAmount = x.DiscountAmount, VatAmount = x.VatAmount, WithholdingTaxAmount = x.WithholdingTaxAmount,
                TotalAmount = x.TotalAmount, PaidAmount = x.PaidAmount, BalanceDue = x.BalanceDue, RevisionNumber = x.RevisionNumber,
                RelatedDocumentId = x.RelatedDocumentId, ReplacedByDocumentId = x.ReplacedByDocumentId, CnDnPurchaseSideOverride = x.CnDnPurchaseSideOverride,
                SignLinkIssuedAt = x.SignLinkIssuedAt, PayLinkIssuedAt = x.PayLinkIssuedAt, PayLinkExpiresAt = x.PayLinkExpiresAt,
                QuotationAcceptedAt = x.QuotationAcceptedAt, QuotationAcceptedBy = x.QuotationAcceptedBy,
                DeliverySignedAt = x.DeliverySignedAt, DeliverySignedBy = x.DeliverySignedBy,
            })
            .FirstOrDefaultAsync(ct);
        if (row == null) return null;
        var relatedType = row.RelatedDocumentId is Guid rid
            ? await _db.Documents.AsNoTracking().Where(r => r.Id == rid && r.CompanyId == companyId).Select(r => (DocumentType?)r.DocumentType).FirstOrDefaultAsync(ct)
            : null;
        (row.ContactSupplierOnly, row.ContactCustomerOnly) = await ContactSidesAsync(companyId, row.ContactId, ct);
        row.CustomerFacing = DocumentPayLinkPolicy.IsCustomerFacing(row.Type, relatedType, row.CnDnPurchaseSideOverride, row.ContactSupplierOnly, row.ContactCustomerOnly);
        return row;
    }

    // ═══════════════ ออกลิงก์ ═══════════════

    public async Task<CustomerLinkResult> EnsureCustomerLinkAsync(Guid companyId, Guid documentId, string purpose, CancellationToken ct = default,
        bool allowReissueRevoked = false, string? actor = null, string? fallbackOrigin = null)
    {
        // ตัวออก token ตัวเดียว (EnsureLinkAsync) — ชั้นนี้เลือกด่านของ "หน้าเอกสาร" (CustomerDocPage.IssueRefusal) และไม่ตรึงช่องทางจ่าย
        var who = string.IsNullOrWhiteSpace(actor) ? purpose : $"{actor} ({purpose})";
        var r = await EnsureLinkAsync(companyId, documentId, null, ct, allowReissueRevoked, who, customerPage: true);
        if (r.Ok) return new CustomerLinkResult(r.Url, r.Token, r.ExpiresAt, null);
        // ยังไม่ได้ตั้ง URL ของระบบ แต่ token ออกแล้ว + ผู้เรียกรู้ origin ของคำขอ (เส้นลิงก์เซ็นเดิมใช้ origin ของคำขอ) ⇒ URL จาก origin นั้น
        if (r.Token != null && !string.IsNullOrWhiteSpace(fallbackOrigin))
            return new CustomerLinkResult(DocumentPayLinkPolicy.Url(fallbackOrigin, companyId, r.Token), r.Token, r.ExpiresAt, null);
        return new CustomerLinkResult(null, null, null, r.Refusal ?? "ออกลิงก์หน้าเอกสารไม่สำเร็จ");
    }

    public async Task<string?> CurrentCustomerUrlAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var d = await LoadDocAsync(companyId, documentId, null, ct);
        if (d == null || CustomerDocPage.StateOf(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, DateTime.UtcNow) != CustomerDocPage.LinkState.Usable) return null;
        if (CustomerDocPage.ViewRefused(d.Type, d.Status, await CustomerFacingAsync(companyId, d, ct))) return null;
        var raw = Recover(d.PayLinkTokenProtected, d.PayLinkTokenHash);
        var origin = raw == null ? null : await SystemBaseUrl.ResolveAsync(_db, _config, ct);
        return raw == null || string.IsNullOrWhiteSpace(origin) ? null : DocumentPayLinkPolicy.Url(origin, companyId, raw);
    }

    // ═══════════════ view model ═══════════════

    /// <summary>ส่วนหน้าเอกสาร — บรรทัด · ยอด · ใบที่เกี่ยวข้อง · ปุ่ม (ตัวตัดสินเดียว <see cref="CustomerDocPage.Actions"/>) ·
    /// ผู้เรียก: <see cref="PublicViewAsync"/> (โหมดลิงก์) · <see cref="PageForDocumentAsync"/> (พอร์ทัล/LINE/พรีวิว)</summary>
    private async Task<CustomerDocPageView> BuildPageAsync(Guid companyId, DocFacts d, PageRow row, CustomerDocPage.Mode mode, bool isEnglish,
        bool payOffered, string? payBlockedNote, bool slipOffered, string? token, DateTime nowUtc, CancellationToken ct)
    {
        var show = CustomerDocPage.ShowsAmounts(d.Type);
        var lines = await _db.DocumentLines.AsNoTracking()
            .Where(l => l.DocumentId == d.Id && l.Document.CompanyId == companyId)
            .OrderBy(l => l.LineOrder).Take(MaxLines)
            .Select(l => new { l.Description, l.Quantity, l.Unit, l.UnitPrice, l.DiscountAmount, l.Amount })
            .ToListAsync(ct);
        var respondedAt = d.Type == DocumentType.Quotation ? row.QuotationAcceptedAt : d.Type == DocumentType.DeliveryNote ? row.DeliverySignedAt : null;
        var respondedBy = d.Type == DocumentType.Quotation ? row.QuotationAcceptedBy : d.Type == DocumentType.DeliveryNote ? row.DeliverySignedBy : null;
        // ช่วงเปลี่ยนผ่าน P1→P2: ปุ่มตอบรับพาไปหน้ายอมรับ/เซ็นรับเดิมด้วย token ตัวเดียวกัน (หน้าเดิมรับ token ของหน้าเอกสาร — PublicQuotationController)
        var respondHref = mode == CustomerDocPage.Mode.Link && token != null ? CustomerDocPage.LegacyRespondPath(d.Type, token) : null;
        var actions = CustomerDocPage.Actions(new CustomerDocPage.Facts(
            d.Type, d.Status, mode, isEnglish, payOffered, payBlockedNote, slipOffered,
            d.DueDate, row.SignLinkIssuedAt ?? row.PayLinkIssuedAt, respondedAt, respondHref), nowUtc);
        var related = await RelatedAsync(companyId, d, row, isEnglish, ct);
        return new CustomerDocPageView(
            Mode: CustomerDocPage.ModeCode(mode),
            LinkState: CustomerDocPage.LinkState.Usable.ToString(),
            Viewable: true,
            CanRequestLink: false,
            IsEnglish: isEnglish,
            StatusLabel: CustomerDocPage.StatusLabel(isEnglish, d.Type, d.Status, d.BalanceDue),
            Voided: d.Status == DocumentStatus.Voided,
            RevisionNumber: row.RevisionNumber,
            ShowAmounts: show,
            Currency: string.IsNullOrWhiteSpace(row.Currency) ? "THB" : row.Currency,
            SubTotal: show ? row.SubTotal : 0m,
            DiscountAmount: show ? row.DiscountAmount : 0m,
            VatAmount: show ? row.VatAmount : 0m,
            WithholdingTaxAmount: show ? row.WithholdingTaxAmount : 0m,
            TotalAmount: show ? row.TotalAmount : 0m,
            PaidAmount: show ? row.PaidAmount : 0m,
            BalanceDue: show ? row.BalanceDue : 0m,
            Lines: lines.Select(l => new CustomerDocPageLine(l.Description, l.Quantity, l.Unit,
                show ? l.UnitPrice : null, show ? l.DiscountAmount : null, show ? l.Amount : null)).ToList(),
            Actions: actions,
            Related: related,
            Labels: CustomerDocPage.Labels(isEnglish, d.Type),
            RespondedBy: respondedBy,
            RespondedAt: respondedAt,
            ExpiresAt: mode == CustomerDocPage.Mode.Link ? row.PayLinkExpiresAt : null);
    }

    /// <summary>
    /// ใบที่เกี่ยวข้องที่หน้าแสดง (และที่ token นี้ดาวน์โหลดได้ — <see cref="ResolvePdfDocumentAsync"/> ใช้ชุดนี้ชุดเดียว):
    /// ใบต้นทาง · ใบลูก · ใบที่ออกแทน · ใบในใบวางบิล — <b>บริษัทเดียวกัน + ผู้ติดต่อเดียวกัน + ออกแล้วยังมีผล + ลูกค้าเห็นได้</b> ·
    /// ใบสั่งซื้อ (ถึงผู้ขาย) ไม่มีใบที่เกี่ยวข้องบนหน้า (ใบรับของ/ใบซื้อเป็นของฝั่งเรา)
    /// </summary>
    private async Task<List<CustomerDocPageRelated>> RelatedAsync(Guid companyId, DocFacts d, PageRow row, bool isEnglish, CancellationToken ct)
    {
        var result = new List<CustomerDocPageRelated>();
        if (d.Type == DocumentType.PurchaseOrder) return result;
        var kinds = new Dictionary<Guid, string>();
        if (row.RelatedDocumentId is Guid parent) kinds[parent] = "parent";
        if (row.ReplacedByDocumentId is Guid replacement) kinds[replacement] = "replacement";
        if (d.Type == DocumentType.BillingNote)
        {
            var billed = await _db.DocumentLines.AsNoTracking()
                .Where(l => l.DocumentId == d.Id && l.Document.CompanyId == companyId && l.SourceDocumentId != null)
                .Select(l => l.SourceDocumentId!.Value).Distinct().Take(MaxRelated).ToListAsync(ct);
            foreach (var id in billed) kinds.TryAdd(id, "billed");
        }
        var keys = kinds.Keys.ToList();
        var docId = d.Id;
        var contactId = d.ContactId;
        var rows = await _db.Documents.AsNoTracking()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.ContactId == contactId && x.Id != docId
                && (keys.Contains(x.Id) || x.RelatedDocumentId == docId))
            .OrderBy(x => x.DocumentDate).ThenBy(x => x.DocumentNumber).Take(MaxRelated)
            .Select(x => new { x.Id, x.DocumentType, x.Status, x.DocumentNumber, x.DocumentDate, x.TotalAmount, x.BalanceDue, x.RelatedDocumentId, x.CnDnPurchaseSideOverride })
            .ToListAsync(ct);
        if (rows.Count == 0) return result;
        var parentIds = rows.Where(r => r.RelatedDocumentId != null).Select(r => r.RelatedDocumentId!.Value).Distinct().ToList();
        var parentTypes = await _db.Documents.AsNoTracking()
            .Where(x => x.CompanyId == companyId && parentIds.Contains(x.Id))
            .Select(x => new { x.Id, x.DocumentType }).ToDictionaryAsync(x => x.Id, x => x.DocumentType, ct);
        foreach (var r in rows)
        {
            if (!DocumentStatusRules.IsEffective(r.Status)) continue;
            DocumentType? parentType = r.RelatedDocumentId is Guid pid && parentTypes.TryGetValue(pid, out var pt) ? pt : null;
            if (!DocumentPayLinkPolicy.IsCustomerFacing(r.DocumentType, parentType, r.CnDnPurchaseSideOverride, row.ContactSupplierOnly, row.ContactCustomerOnly))
                continue;
            var kind = kinds.TryGetValue(r.Id, out var k) ? k : "child";
            string title;
            try { title = (await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, r.Id)).Title; }
            catch (KeyNotFoundException) { continue; }
            var show = CustomerDocPage.ShowsAmounts(r.DocumentType);
            result.Add(new CustomerDocPageRelated(r.Id, kind, RelatedKindLabel(isEnglish, kind), title, r.DocumentNumber, r.DocumentDate,
                show ? r.TotalAmount : null, show && CustomerDocPage.IsPayableType(r.DocumentType) ? r.BalanceDue : null,
                CustomerDocPage.StatusLabel(isEnglish, r.DocumentType, r.Status, r.BalanceDue)));
        }
        return result;
    }

    private static string RelatedKindLabel(bool en, string kind) => kind switch
    {
        "parent" => en ? "Source document" : "เอกสารต้นทาง",
        "replacement" => en ? "Replaced by" : "เอกสารที่ออกแทน",
        "billed" => en ? "Billed document" : "เอกสารในใบวางบิล",
        _ => en ? "Follow-up document" : "เอกสารที่ออกต่อจากใบนี้",
    };

    public async Task<PublicDocumentPayView?> PageForDocumentAsync(Guid companyId, Guid documentId, CustomerDocPage.Mode mode, CancellationToken ct = default)
    {
        // โหมดลิงก์ต้องมาทาง token เท่านั้น (PublicViewAsync) — เส้นนี้ไม่มีเป้าหมายรายการชำระ
        if (mode == CustomerDocPage.Mode.Link) return null;
        var d = await LoadDocAsync(companyId, documentId, null, ct);
        if (d == null) return null;
        var row = await LoadPageRowAsync(companyId, d.Id, ct);
        if (row == null || CustomerDocPage.ViewRefused(d.Type, d.Status, row.CustomerFacing)) return null;

        var now = DateTime.UtcNow;
        var payType = CustomerDocPage.IsPayableType(d.Type);
        var payRefusal = Refusal(d);
        var amounts = payType && payRefusal == null ? (await AmountsAsync(companyId, d, ct)).Amounts : new DocumentPayLinkPolicy.Amounts(0m, 0m, 0m);
        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, d.Id);
        var company = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => new { c.Name, c.NameEn }).FirstOrDefaultAsync(ct);
        var contactName = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == d.ContactId && c.CompanyId == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var payOpen = payType && payRefusal == null && amounts.Net > 0.005m;
        var page = await BuildPageAsync(companyId, d, row, mode, heading.IsEnglish,
            payOffered: payOpen, payBlockedNote: payType ? payRefusal : null, slipOffered: false, token: null, nowUtc: now, ct);
        const string noTarget = "ทำรายการชำระได้จากลิงก์หน้าเอกสารเท่านั้น";
        return new PublicDocumentPayView(
            CompanyName: (heading.IsEnglish && !string.IsNullOrWhiteSpace(company?.NameEn) ? company!.NameEn : company?.Name) ?? "",
            DocumentTitle: heading.Title, DocumentNumber: d.DocumentNumber, DocumentDate: d.DocumentDate, DueDate: d.DueDate,
            CustomerName: contactName,
            Gross: amounts.Gross, Wht: amounts.Wht, Net: amounts.Net, Currency: "THB",
            CanPayOnline: false, BlockedNote: null,
            OnlineMethods: new List<PayChannelOption>(), Transfer: null, Card: null, IsTestMode: false)
        {
            Target = new PublicPayTarget(PaymentSourceKind.Document, d.Id, Guid.Empty, 0m, "", null, null, "",
                BlockedNote: noTarget, AllowedMethods: Array.Empty<string>(), ContactId: d.ContactId),
            Page = page,
        };
    }

    public async Task<Guid?> ResolvePdfDocumentAsync(Guid companyId, string token, Guid? relatedId, CancellationToken ct = default)
    {
        // ด่านเดียวกับการเปิดหน้า (token · เพิกถอน/หมดอายุ · ใบที่ลูกค้าเห็นได้) — ไม่มีทางลัด
        var view = await PublicViewAsync(companyId, token, ct);
        if (view?.Page is not { Viewable: true } page) return null;
        if (relatedId == null) return view.Target.SourceId;
        // ใบที่เกี่ยวข้อง: ต้องอยู่ในชุดที่หน้าแสดงของใบนี้ (บริษัทเดียวกันอย่างเดียวไม่พอ)
        return page.Related.Any(r => r.RelId == relatedId.Value) ? relatedId : null;
    }

    public async Task MarkOpenedAsync(Guid companyId, string token, CancellationToken ct = default)
    {
        if (!DocumentPayLinkPolicy.LooksValid(token)) return;
        try
        {
            var hash = DocumentPayLinkPolicy.TokenHash(token);
            var now = DateTime.UtcNow;
            var signTypes = SignRequestKinds.All.Select(k => k.DocumentType).ToList();
            // ป้าย "ลูกค้าเปิดลิงก์แล้ว" มีเฉพาะชนิดที่ติดตามลิงก์เซ็น (ผู้อ่าน: SignRequestKinds.Status) — เขียนแบบมีเงื่อนไข ครั้งแรกเท่านั้น
            await _db.Documents
                .Where(x => x.CompanyId == companyId && x.PayLinkTokenHash == hash && x.PayLinkRevokedAt == null && !x.IsDeleted
                    && signTypes.Contains(x.DocumentType) && x.SignLinkOpenedAt == null
                    && x.QuotationAcceptedAt == null && x.DeliverySignedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.SignLinkOpenedAt, (DateTime?)now), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ป้ายติดตามเท่านั้น (ไม่ใช่เส้นเงิน/ภาษี) — ล้มไม่กระทบการเปิดดูของลูกค้า แต่ต้องเห็นใน log ระดับ error
            _logger?.LogError(ex, "ประทับเวลาเปิดลิงก์หน้าเอกสารไม่สำเร็จ (company {CompanyId})", companyId);
        }
    }

    // ═══════════════ ขอลิงก์ใหม่ (ไม่ออก token ให้คนนอก) ═══════════════

    public async Task<CustomerLinkRequestResult> RequestNewLinkAsync(Guid companyId, string token, CancellationToken ct = default)
    {
        var none = new CustomerLinkRequestResult(false, false, Guid.Empty, "", CustomerDocPage.NotFoundMessage);
        if (!DocumentPayLinkPolicy.LooksValid(token)) return none;
        var d = await LoadDocAsync(companyId, null, DocumentPayLinkPolicy.TokenHash(token), ct);
        if (d == null || !DocumentPayLinkPolicy.TokenMatches(token, d.PayLinkTokenHash)) return none;
        var now = DateTime.UtcNow;
        var state = CustomerDocPage.StateOf(d.PayLinkTokenHash, d.PayLinkExpiresAt, d.PayLinkRevokedAt, now);
        // ลิงก์ที่ยังใช้ได้ (ปุ่มปิดเพราะพ้นกำหนดตอบรับ) หรือหมดอายุไม่นาน เท่านั้น — เพิกถอน/หมดอายุนานเกิน = ข้อความเดียว
        if (state != CustomerDocPage.LinkState.Usable && !CustomerDocPage.CanRequestNewLink(state, d.PayLinkExpiresAt, now)) return none;
        return await RecordLinkRequestAsync(companyId, d, "link", ct);
    }

    public async Task<CustomerLinkRequestResult> RequestLinkForDocumentAsync(Guid companyId, Guid documentId, string source, CancellationToken ct = default)
    {
        var d = await LoadDocAsync(companyId, documentId, null, ct);
        if (d == null) return new CustomerLinkRequestResult(false, false, Guid.Empty, "", CustomerDocPage.NotFoundMessage);
        return await RecordLinkRequestAsync(companyId, d, source, ct);
    }

    private async Task<CustomerLinkRequestResult> RecordLinkRequestAsync(Guid companyId, DocFacts d, string source, CancellationToken ct)
    {
        var row = await LoadPageRowAsync(companyId, d.Id, ct);
        if (row == null || CustomerDocPage.ViewRefused(d.Type, d.Status, row.CustomerFacing))
            return new CustomerLinkRequestResult(false, false, Guid.Empty, "", CustomerDocPage.NotFoundMessage);
        var en = (await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, d.Id)).IsEnglish;
        var labels = CustomerDocPage.Labels(en, d.Type);
        var now = DateTime.UtcNow;
        var since = now.AddDays(-1);
        var entityId = d.Id.ToString();
        // กันซ้ำ 1 ครั้ง/ใบ/วัน จากแถว audit เอง (ฐานข้อมูล — ใช้ร่วมหลายเครื่อง · ไม่มี state ในหน่วยความจำ)
        var already = await _db.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == companyId
            && a.EntityType == LinkRequestEntity && a.EntityId == entityId && a.Timestamp >= since, ct);
        if (already) return new CustomerLinkRequestResult(true, false, d.Id, d.DocumentNumber, labels["requestAlready"]);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = "ผู้ถือลิงก์เอกสาร (ไม่ได้ล็อกอิน)", Action = AuditAction.Create,
            EntityType = LinkRequestEntity, EntityId = entityId,
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                @event = "customer-link-requested", ruleCode = CustomerDocPage.RuleCode, source, d.DocumentNumber,
                linkExpiresAt = d.PayLinkExpiresAt,
            }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        return new CustomerLinkRequestResult(true, true, d.Id, d.DocumentNumber, labels["requestSent"]);
    }
}
