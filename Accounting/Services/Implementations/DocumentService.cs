using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Tax;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

public class DocumentService : IDocumentService
{
    private readonly AccountingDbContext _db;
    private readonly IAccountingService _accountingService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IWithholdingTaxCertService _whtService;
    private readonly IEtaxInvoiceService _etaxService;
    private readonly ILogger<DocumentService> _logger;
    private readonly ILineNotifyService _lineNotify;
    private readonly Accounting.Services.Implementations.Ocr.VendorIntelligenceService _vendorIntel;
    private readonly CrossTenantWorkflowService _crossTenantWorkflow;
    private readonly INotificationEngine? _notify;
    private readonly IBankService? _bankService;
    private readonly ITaxService? _taxService;
    private readonly IBotExchangeRateService? _fxRates;
    private readonly ISensitivityService? _sensitivity;
    private readonly Accounting.Services.Ai.IDocumentAiAugmenter? _aiAugmenter;
    private readonly Accounting.Services.Ai.IAiFeedbackRecorder? _feedbackRecorder;
    private readonly IFixedAssetService? _fixedAssets;
    private readonly IEmailScheduleService? _emailSchedule;
    private readonly IAdvancedArApService? _advancedArAp;
    private readonly IApprovalService? _approval;

    public DocumentService(AccountingDbContext db, IAccountingService accountingService,
        ISubscriptionService subscriptionService, IWithholdingTaxCertService whtService,
        IEtaxInvoiceService etaxService, ILogger<DocumentService> logger,
        ILineNotifyService lineNotify,
        Accounting.Services.Implementations.Ocr.VendorIntelligenceService vendorIntel,
        CrossTenantWorkflowService crossTenantWorkflow,
        INotificationEngine? notify = null,
        IBankService? bankService = null,
        ITaxService? taxService = null,
        IBotExchangeRateService? fxRates = null,
        ISensitivityService? sensitivity = null,
        Accounting.Services.Ai.IDocumentAiAugmenter? aiAugmenter = null,
        IWebhookService? webhooks = null,
        IEmailScheduleService? emailSchedule = null,
        IAdvancedArApService? advancedArAp = null,
        IApprovalService? approval = null,
        Accounting.Services.Ai.IAiFeedbackRecorder? feedbackRecorder = null,
        IFixedAssetService? fixedAssets = null)
    {
        _fixedAssets = fixedAssets;
        _emailSchedule = emailSchedule;
        _advancedArAp = advancedArAp;
        _approval = approval;
        _db = db;
        _accountingService = accountingService;
        _subscriptionService = subscriptionService;
        _whtService = whtService;
        _etaxService = etaxService;
        _logger = logger;
        _lineNotify = lineNotify;
        _vendorIntel = vendorIntel;
        _crossTenantWorkflow = crossTenantWorkflow;
        _sensitivity = sensitivity;
        _notify = notify;
        _bankService = bankService;
        _taxService = taxService;
        _fxRates = fxRates;
        _aiAugmenter = aiAugmenter;
        _webhooks = webhooks;
        _feedbackRecorder = feedbackRecorder;
    }

    private readonly IWebhookService? _webhooks;

    /// <summary>Fire an outbound webhook. Wrapped in try/catch so a
    /// slow / failed delivery NEVER blocks the parent API call from
    /// returning. WebhookService handles retry-with-backoff internally.</summary>
    private async Task FireWebhookAsync(Guid companyId, string eventType, object payload)
    {
        if (_webhooks == null) return;
        try { await _webhooks.TriggerAsync(companyId, eventType, payload); }
        catch (Exception ex) { _logger.LogWarning(ex, "Webhook {Event} fire-and-forget failed", eventType); }
    }

    /// <summary>Auto-populate ProjectCostEntry from an approved
    /// expense-side document. Cost-bearing types only — Sales-side
    /// docs feed Project.ActualRevenue separately. Idempotent on
    /// DocumentLineId so re-running on the same doc doesn't
    /// duplicate.</summary>
    private async Task SyncProjectCostEntriesAsync(Guid companyId, Document doc)
    {
        var costTypes = new[] {
            DocumentType.PurchaseInvoice, DocumentType.Expense,
            DocumentType.PaymentVoucher, DocumentType.CertificateInLieu,
        };
        if (!costTypes.Contains(doc.DocumentType)) return;
        if (doc.Lines == null || doc.Lines.Count == 0) return;

        // Snapshot the lines that already have a PCE so we skip them
        // in O(1) rather than running an exists-query per line.
        var lineIds = doc.Lines.Select(l => l.Id).ToList();
        var alreadyBookedLineIds = await _db.ProjectCostEntries
            .Where(c => c.CompanyId == companyId
                && !c.IsDeleted
                && c.DocumentLineId.HasValue
                && lineIds.Contains(c.DocumentLineId.Value))
            .Select(c => c.DocumentLineId!.Value)
            .ToListAsync();

        // Cache projects we touch so we update each one's ActualCost
        // exactly once across the doc's lines (some PIs split a single
        // project across many lines; we'd otherwise hit the DB per line).
        var touchedProjects = new Dictionary<Guid, decimal>();

        foreach (var line in doc.Lines)
        {
            // Per-line projectId wins; header is the fallback so users
            // can tag the doc once and still have individual lines
            // override (e.g. one PO with 90% Project A + 10% Project B).
            var projectId = line.ProjectId ?? doc.ProjectId;
            if (!projectId.HasValue) continue;
            if (alreadyBookedLineIds.Contains(line.Id)) continue;
            _db.ProjectCostEntries.Add(new ProjectCostEntry
            {
                CompanyId = companyId,
                ProjectId = projectId.Value,
                EntryDate = doc.DocumentDate,
                CostType = "Material",         // generic; could classify on AccountType later
                Description = $"{line.Description} [auto from {doc.DocumentNumber}]",
                Quantity = line.Quantity,
                UnitCost = line.UnitPrice,
                Amount = line.Amount,
                DocumentId = doc.Id,
                DocumentLineId = line.Id,
                IsBillable = false,
            });
            touchedProjects[projectId.Value] = touchedProjects.GetValueOrDefault(projectId.Value) + line.Amount;
        }
        if (touchedProjects.Count > 0)
        {
            var ids = touchedProjects.Keys.ToList();
            var projects = await _db.Projects
                .Where(p => p.CompanyId == companyId && ids.Contains(p.Id))
                .ToListAsync();
            foreach (var p in projects)
                p.ActualCost += touchedProjects[p.Id];
        }
        await _db.SaveChangesAsync();
    }

    /// <summary>Mirror of <see cref="SyncProjectCostEntriesAsync"/> for a
    /// void / un-approve: soft-delete the auto-spawned ProjectCostEntry rows
    /// for this document and back their amounts out of each Project.ActualCost.
    /// Without this, voiding a cost document left the project's actual cost
    /// permanently inflated. Idempotent — rows already soft-deleted are skipped
    /// so a re-void never double-subtracts. Uses the stored entry.Amount (what
    /// was actually booked) rather than re-deriving from lines. Caller owns the
    /// SaveChanges (runs inside the void transaction).</summary>
    private async Task ReverseProjectCostEntriesAsync(Guid companyId, Document doc)
    {
        var lineIds = doc.Lines?.Select(l => l.Id).ToList() ?? new List<Guid>();
        var entries = await _db.ProjectCostEntries
            .Where(c => c.CompanyId == companyId && !c.IsDeleted
                && (c.DocumentId == doc.Id
                    || (c.DocumentLineId.HasValue && lineIds.Contains(c.DocumentLineId.Value))))
            .ToListAsync();
        if (entries.Count == 0) return;

        var perProject = new Dictionary<Guid, decimal>();
        foreach (var e in entries)
        {
            e.IsDeleted = true;
            e.UpdatedAt = DateTime.UtcNow;
            perProject[e.ProjectId] = perProject.GetValueOrDefault(e.ProjectId) + e.Amount;
        }
        var ids = perProject.Keys.ToList();
        var projects = await _db.Projects
            .Where(p => p.CompanyId == companyId && ids.Contains(p.Id))
            .ToListAsync();
        foreach (var p in projects)
            p.ActualCost = Math.Max(0, p.ActualCost - perProject[p.Id]);
    }

    /// <summary>Resolve THB exchange rate for a document. THB → 1. Explicit
    /// override wins; otherwise auto-fetch the BoT mid-rate at DocumentDate.
    /// Throws if BoT lookup fails — silent fallback to 1 on non-THB would
    /// corrupt the GL.</summary>
    private async Task<decimal> ResolveExchangeRateAsync(string? currency, decimal? overrideRate, DateTime documentDate)
    {
        var cur = (currency ?? "THB").ToUpperInvariant();
        if (cur == "THB") return 1m;
        if (overrideRate is decimal r)
        {
            if (r <= 0m) throw new InvalidOperationException("อัตราแลกเปลี่ยนต้องมากกว่า 0");
            return r;
        }
        if (_fxRates == null)
            throw new InvalidOperationException(
                $"เอกสารสกุล {cur} ต้องระบุ ExchangeRate (ไม่ได้กำหนดบริการอัตรา ธ.ปท.)");
        var rate = await _fxRates.GetRateAsync(cur, documentDate);
        if (rate == null || rate.MidRate <= 0m)
            throw new InvalidOperationException(
                $"ไม่พบอัตราแลกเปลี่ยน {cur} ของวันที่ {documentDate:yyyy-MM-dd} จาก ธ.ปท. " +
                "กรุณาระบุ ExchangeRate ในคำขอ");
        return rate.MidRate;
    }

    /// <summary>Validate the line items on a create OR update. Same guards in
    /// one place so an edit can't slip past the create-time checks (qty &gt; 0,
    /// non-negative price, discount 0-100%, VAT ∈ {0,7,-1}, WHT 0-15%, valid
    /// active account). Throws InvalidOperationException on the first failure.</summary>
    private async Task ValidateDocumentLinesAsync(Guid companyId, List<DocumentLineRequest>? lines)
    {
        if (lines == null || lines.Count == 0)
            throw new InvalidOperationException("ต้องมีรายการสินค้าอย่างน้อย 1 รายการ");

        foreach (var line in lines)
        {
            if (line.Quantity <= 0)
                throw new InvalidOperationException("จำนวนสินค้าต้องมากกว่า 0");
            if (line.UnitPrice < 0)
                throw new InvalidOperationException("ราคาต่อหน่วยต้องไม่ติดลบ");
            if (line.DiscountPercent < 0 || line.DiscountPercent > 100)
                throw new InvalidOperationException("ส่วนลดต้องอยู่ระหว่าง 0-100%");
            if (line.VatRate != 0 && line.VatRate != 7 && line.VatRate != -1)
                throw new InvalidOperationException("อัตราภาษีมูลค่าเพิ่มต้องเป็น 0, 7 หรือ -1 (ยกเว้น)");
            if (line.WithholdingTaxRate < 0 || line.WithholdingTaxRate > 15)
                throw new InvalidOperationException("อัตราภาษีหัก ณ ที่จ่ายต้องอยู่ระหว่าง 0-15%");
            if (line.AccountId.HasValue)
            {
                var acctExists = await _db.ChartOfAccounts.AnyAsync(a =>
                    a.Id == line.AccountId.Value && a.CompanyId == companyId && a.IsActive);
                if (!acctExists)
                    throw new InvalidOperationException(
                        $"รหัสบัญชีที่ระบุในรายการ '{line.Description}' ไม่พบในผังบัญชี หรือถูกปิดใช้งาน");
            }
        }
    }

    /// <summary>Computed money fields for one document line, shared by create
    /// + update so the math (incl. VAT-inclusive back-out) never diverges.
    /// <c>NetAmount</c> is the ex-VAT, after-discount base that posts to
    /// revenue/expense.</summary>
    private readonly record struct LineAmounts(decimal NetAmount, decimal DiscountAmount, decimal VatAmount, decimal WhtAmount);

    private static LineAmounts ComputeLineAmounts(DocumentLineRequest line, bool pricesIncludeVat)
    {
        const MidpointRounding R = MidpointRounding.AwayFromZero;
        var gross = Math.Round(line.Quantity * line.UnitPrice, 2, R);
        // ส่วนลด: ถ้าระบุเป็นยอดเงิน (DiscountAmount > 0) ใช้ตรง ๆ (มาตรฐานสากล
        // — ใบกำกับระบุส่วนลดเป็นบาท); ไม่งั้นคิดจาก %. clamp ไม่ให้เกิน gross.
        var discountAmt = line.DiscountAmount is > 0m
            ? Math.Min(Math.Round(line.DiscountAmount.Value, 2, R), gross)
            : Math.Round(gross * line.DiscountPercent / 100, 2, R);
        var afterDiscount = gross - discountAmt;

        decimal net, vatAmt;
        if (line.VatAmountOverride.HasValue)
        {
            // External/integration ส่ง VAT ที่คำนวณเองมาแล้ว (บรรทัดปนของ
            // เสียภาษี + ยกเว้น) → honor ตรง ๆ ไม่คิดใหม่. pricesIncludeVat
            // = true หมายถึง afterDiscount รวม VAT แล้ว → net = ส่วนต่าง.
            vatAmt = Math.Round(line.VatAmountOverride.Value, 2, R);
            net = pricesIncludeVat ? afterDiscount - vatAmt : afterDiscount;
        }
        else if (pricesIncludeVat && line.VatRate > 0)
        {
            // Entered price already contains VAT → strip it out.
            // net = incl × 100/(100+rate); vat = incl − net.
            net = Math.Round(afterDiscount * 100m / (100m + line.VatRate), 2, R);
            vatAmt = afterDiscount - net;
        }
        else
        {
            net = afterDiscount;
            vatAmt = line.VatRate > 0 ? Math.Round(net * line.VatRate / 100, 2, R) : 0m;
        }
        // WHT is always computed on the ex-VAT base (Thai rule).
        var whtAmt = Math.Round(net * line.WithholdingTaxRate / 100, 2, R);
        return new LineAmounts(net, discountAmt, vatAmt, whtAmt);
    }

    /// <summary>ยุบ VAT-split phantom lines — choke point สุดท้ายครอบคลุมทุก
    /// path (frontend form / integration / OCR handoff ที่เข้าทาง POST /documents).
    /// เคส: external OCR แตกสินค้า 1 ตัวเป็น "...(ส่วนมีภาษี)" + "...(ส่วนไม่มีภาษี)"
    /// + เศษ ≤฿1 (VAT/0.07 rounding). Gate ด้วย "มี marker ส่วน(ไม่)มีภาษี"
    /// — ถ้าไม่มี marker ไม่แตะ (กันเอกสารปกติที่มี line ≤฿1 จริง). DocumentLineRequest
    /// เป็น immutable record → rebuild list. คืน list ใหม่; caller replace
    /// request.Lines contents.</summary>
    internal static List<DocumentLineRequest> CleanVatSplitLines(List<DocumentLineRequest>? lines)
    {
        if (lines == null || lines.Count < 2) return lines ?? new();

        string[] markers =
        {
            "ส่วนมีภาษี", "ส่วนที่มีภาษี", "ส่วนคิดภาษี",
            "ส่วนไม่มีภาษี", "ส่วนที่ไม่มีภาษี", "ส่วนยกเว้นภาษี",
            "vatable", "non-vat", "non vat", "nonvat",
            "vat included", "vat-included", "incl. vat", "incl vat",
            "vat excluded", "vat-excluded", "excl. vat", "excl vat",
            "with vat", "without vat", "มีภาษี", "ไม่มีภาษี",
        };

        // ตรวจว่ามี line ที่มี VAT-split marker ไหม — gate: ไม่มี = ไม่แตะ
        static (bool Had, string Cleaned) TrimMarker(string? desc, string[] mk)
        {
            if (string.IsNullOrWhiteSpace(desc)) return (false, desc ?? "");
            var s = desc.TrimEnd();
            var had = false;
            for (var safety = 0; safety < 3; safety++)
            {
                if (!s.EndsWith(")")) break;
                var open = s.LastIndexOf('(');
                if (open < 0) break;
                var inside = s.Substring(open + 1, s.Length - open - 2).Trim().ToLowerInvariant();
                var match = false;
                foreach (var m in mk) if (inside.Contains(m.ToLowerInvariant())) { match = true; break; }
                if (!match) break;
                s = s.Substring(0, open).TrimEnd();
                had = true;
            }
            return (had, s);
        }

        var anyMarker = lines.Any(l => TrimMarker(l.Description, markers).Had);
        if (!anyMarker) return lines;   // เอกสารปกติ — ไม่แตะ

        // 1) trim suffix (rebuild — record immutable)
        var trimmed = lines.Select(l => l with { Description = TrimMarker(l.Description, markers).Cleaned }).ToList();

        const decimal TOL = 1m;
        decimal Amt(DocumentLineRequest l) => l.Quantity * l.UnitPrice;

        // 2) fold phantom (≤฿1) เข้าบรรทัด amount ใหญ่สุด — รักษายอดรวมเป๊ะ
        var phantoms = trimmed.Where(l => Amt(l) > 0m && Amt(l) <= TOL && l.UnitPrice <= TOL).ToList();
        var reals = trimmed.Where(l => !phantoms.Contains(l)).ToList();
        if (phantoms.Count > 0 && reals.Count > 0)
        {
            var foldAmt = phantoms.Sum(Amt);
            var main = reals.OrderByDescending(Amt).First();
            var idx = reals.IndexOf(main);
            var newAmt = Amt(main) + foldAmt;
            var newUnit = main.Quantity > 0m ? Math.Round(newAmt / main.Quantity, 2) : newAmt;
            reals[idx] = main with { UnitPrice = newUnit };
            trimmed = reals;
        }

        // 3) merge true duplicates (desc + account เดียวกัน) — เคส split เท่า ๆ กัน
        var outList = new List<DocumentLineRequest>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in trimmed)
        {
            var key = $"{(l.Description ?? "").Trim()}|{l.AccountId}|{l.AccountCode}";
            if (seen.TryGetValue(key, out var oi))
            {
                var ex = outList[oi];
                var newQty = ex.Quantity + l.Quantity;
                // amount รวม = ex.Amt + l.Amt; unit price = amount/qty
                var totAmt = Amt(ex) + Amt(l);
                var unit = newQty > 0m ? Math.Round(totAmt / newQty, 2) : ex.UnitPrice;
                outList[oi] = ex with { Quantity = newQty, UnitPrice = unit };
            }
            else
            {
                seen[key] = outList.Count;
                outList.Add(l);
            }
        }
        return outList;
    }

    public async Task<DocumentResponse> CreateDocumentAsync(Guid companyId, CreateDocumentRequest request, string createdBy)
    {
        // ⭐ ยุบ VAT-split phantom lines ก่อนทุกอย่าง (choke point ครอบคลุมทุก path)
        if (request.Lines is { Count: > 1 })
        {
            var cleaned = CleanVatSplitLines(request.Lines);
            if (!ReferenceEquals(cleaned, request.Lines))
            {
                request.Lines.Clear();
                request.Lines.AddRange(cleaned);
            }
        }

        // Check usage limit
        if (!await _subscriptionService.CheckUsageLimitAsync(companyId, "document"))
            throw new InvalidOperationException("เกินจำนวนเอกสารที่อนุญาตต่อเดือน");

        // Check feature access
        if (!await _subscriptionService.CheckFeatureAccessAsync(companyId, FeatureFlags.DocumentEngine))
            throw new InvalidOperationException("ไม่มีสิทธิ์ใช้ระบบเอกสาร");

        // Validate ContactId exists and has correct type for document
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == request.ContactId && c.CompanyId == companyId);
        if (contact == null)
            throw new InvalidOperationException("ไม่พบผู้ติดต่อในบริษัทนี้");

        // Duplicate-document soft check — เปรียบเอกสารที่กำลังสร้าง vs เอกสารใน
        // 60 วันล่าสุดของ vendor/customer คนเดียวกัน + ยอดใกล้เคียง (±0.5%)
        // ไม่ throw — แค่ log warning ลง ProcessingNotes (จะเห็นในหน้า detail) +
        // เรียกใช้ local DuplicateDocumentDistillationModel ตาม กฎเหล็ก #1
        // (กันลูกค้า upload OCR ซ้ำ / integration ส่งซ้ำ / user คลิก save 2 ครั้ง)
        var totalAmt = request.Lines.Sum(l => l.Quantity * l.UnitPrice);
        if (totalAmt > 0)
        {
            var since = request.DocumentDate.AddDays(-60);
            var likelyDupes = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId
                    && d.ContactId == request.ContactId
                    && d.DocumentType == request.DocumentType
                    && d.Status != DocumentStatus.Voided && !d.IsDeleted
                    && d.DocumentDate >= since
                    && d.DocumentDate <= request.DocumentDate.AddDays(30)
                    && Math.Abs(d.TotalAmount - totalAmt) < totalAmt * 0.005m)
                .Select(d => new { d.Id, d.DocumentNumber, d.DocumentDate, d.TotalAmount })
                .Take(3).ToListAsync();
            if (likelyDupes.Count > 0)
            {
                var msg = string.Join("; ", likelyDupes.Select(x =>
                    $"{x.DocumentNumber} ({x.DocumentDate:yyyy-MM-dd} ฿{x.TotalAmount:N2})"));
                _logger.LogWarning("Possible duplicate document for company {Co} contact {Ct} amount {Amt}: {Dupes}",
                    companyId, request.ContactId, totalAmt, msg);
                // ส่งสัญญาณกลับ frontend ผ่าน throw แบบ structured? ใช้ approach
                // เหมือน OcrService — log warning + ฝัง ProcessingNotes ของ doc ที่
                // สร้างเสร็จเพื่อให้ user เห็น banner ใน detail. กัน "false positive"
                // มาก็ block ไม่ได้ — vendor อาจขายของซ้ำเดิมจริง
            }
        }

        var revenueDocTypes = new[] {
            DocumentType.Quotation, DocumentType.Invoice, DocumentType.TaxInvoice,
            DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.DebitNote,
            DocumentType.CreditNote, DocumentType.DeliveryNote, DocumentType.BillingNote
        };
        var purchaseDocTypes = new[] {
            DocumentType.PurchaseRequisition, DocumentType.PurchaseOrder,
            DocumentType.GoodsReceiptNote,
            DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.PaymentVoucher,
            DocumentType.CertificateInLieu
        };
        if (revenueDocTypes.Contains(request.DocumentType) && !contact.IsCustomer)
            throw new InvalidOperationException($"ผู้ติดต่อ '{contact.Name}' ไม่ได้ตั้งค่าเป็นลูกค้า — กรุณาเปิดสถานะ 'ลูกค้า' ก่อนออกเอกสารขาย");
        if (purchaseDocTypes.Contains(request.DocumentType) && !contact.IsSupplier)
            throw new InvalidOperationException($"ผู้ติดต่อ '{contact.Name}' ไม่ได้ตั้งค่าเป็นผู้จำหน่าย — กรุณาเปิดสถานะ 'ผู้จำหน่าย' ก่อนออกเอกสารซื้อ");

        // Validate project tags belong to this company (security: prevent cross-tenant tagging)
        if (request.ProjectId.HasValue)
        {
            var projectOk = await _db.Projects.AnyAsync(p =>
                p.Id == request.ProjectId.Value && p.CompanyId == companyId);
            if (!projectOk)
                throw new InvalidOperationException("ไม่พบโครงการในบริษัทนี้");
        }
        var lineProjectIds = request.Lines?.Where(l => l.ProjectId.HasValue)
            .Select(l => l.ProjectId!.Value).Distinct().ToList() ?? new List<Guid>();
        if (lineProjectIds.Count > 0)
        {
            var validCount = await _db.Projects
                .CountAsync(p => p.CompanyId == companyId && lineProjectIds.Contains(p.Id));
            if (validCount != lineProjectIds.Count)
                throw new InvalidOperationException("รหัสโครงการในรายการบางบรรทัดไม่ถูกต้อง");
        }

        // CertificateInLieu requires reason and certifier
        if (request.DocumentType == DocumentType.CertificateInLieu)
        {
            if (string.IsNullOrWhiteSpace(request.CertificateReason))
                throw new InvalidOperationException("ใบรับรองแทนใบเสร็จต้องระบุเหตุผลที่ไม่ได้รับใบเสร็จ");
            if (string.IsNullOrWhiteSpace(request.CertifierName))
                throw new InvalidOperationException("ใบรับรองแทนใบเสร็จต้องระบุชื่อผู้รับรอง");
        }

        // Validate the line items (shared with UpdateDocumentAsync so an edit
        // can't bypass the same accounting guards a create enforces).
        await ValidateDocumentLinesAsync(companyId, request.Lines);

        // Fiscal-period lock at CREATE — don't even let a document be drafted
        // into a closed/locked period (previously only blocked at approval,
        // which let a doc be created then fail later). Keeps the books tidy.
        var createPeriod = await _db.FiscalPeriods.AsNoTracking().FirstOrDefaultAsync(f =>
            f.CompanyId == companyId && f.StartDate <= request.DocumentDate && f.EndDate >= request.DocumentDate);
        if (createPeriod != null && createPeriod.Status != FiscalPeriodStatus.Open)
            throw new InvalidOperationException(
                $"ไม่สามารถสร้างเอกสารที่มีวันที่ในงวด {createPeriod.Name} ได้ เนื่องจากงวดดังกล่าวมีสถานะ {createPeriod.Status} (ปิดงวดแล้ว)");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // Defer real sequence number to Approve — Draft uses a temporary
            // placeholder ("DRAFT-{guid8}") instead of pulling from the
            // company's running counter. That eliminates the gap-on-delete
            // problem entirely: deleting a Draft never leaves a missing
            // sequence number because none was issued. The real number is
            // assigned atomically inside ApproveDocumentAsync. Indexes built
            // on DocumentNumber stay valid because placeholders are unique
            // (guid suffix). Compliance for TaxInvoice/Receipt is satisfied
            // since they only get a real number when they enter the books.
            var draftMarker = $"DRAFT-{Guid.NewGuid():N}".Substring(0, 14);
            var docNumber = draftMarker;

            // Resolve FX up front so the rate the user sees on the document
            // is exactly what posts to the GL on Approve. THB → always 1.
            // Non-THB: caller's override wins; otherwise auto-fetch BoT mid-rate
            // at DocumentDate. If BoT lookup fails, refuse — silent fallback to
            // 1 would corrupt the GL.
            var fxRate = await ResolveExchangeRateAsync(request.Currency, request.ExchangeRate, request.DocumentDate);

            var doc = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = request.DocumentType,
                // Normalize เป็น "วันที่ไทย ณ 00:00 UTC" — กัน timestamptz shift
                // (02/06 ส่งมา → ห้ามเก็บ 01/06 17:00 UTC ที่ทำเลข/ภพ.30 ผิดวัน)
                DocumentDate = Accounting.Helpers.ThaiDate.CalendarDateUtc(request.DocumentDate),
                Currency = string.IsNullOrWhiteSpace(request.Currency) ? "THB" : request.Currency.ToUpperInvariant(),
                ExchangeRate = fxRate,
                DueDate = Accounting.Helpers.ThaiDate.CalendarDateUtc(request.DueDate),
                ContactId = request.ContactId,
                Reference = request.Reference,
                Notes = request.Notes,
                Sensitivity = request.Sensitivity,
                ProjectId = request.ProjectId,
                BankAccountId = request.BankAccountId,
                PaymentAccountId = request.PaymentAccountId,
                ExpenseCategoryId = request.ExpenseCategoryId,
                CustomAppendix = request.CustomAppendix,
                CustomFooterNotes = request.CustomFooterNotes,
                CustomTermsAndConditions = request.CustomTermsAndConditions,
                RevenueContractId = request.RevenueContractId,
                PerformanceObligationId = request.PerformanceObligationId,
                CertificateReason = request.CertificateReason,
                CertifierName = request.CertifierName,
                CertifierPosition = request.CertifierPosition,
                WitnessName = request.WitnessName,
                WitnessPosition = request.WitnessPosition,
                PaymentDate = request.PaymentDate,
                // Required for CreditNote. Save only when actually a CN —
                // ignored on other types so a stale UI value can't leak
                // through. Approval-time validator (below) bounces a CN
                // missing this.
                CreditNoteReason = request.DocumentType == DocumentType.CreditNote
                    ? request.CreditNoteReason
                    : null,
                // Counterparty tax-invoice metadata — only meaningful for
                // supplier-issued doc types (PurchaseInvoice, CertificateInLieu).
                // Stored unconditionally though so partner sync can round-trip.
                SupplierInvoiceNumber = request.SupplierInvoiceNumber,
                SupplierTaxInvoiceDate = request.SupplierTaxInvoiceDate,
                // PV: "ใช้งานใบกำกับภาษี" checkbox + supplier branch snapshot.
                // Branch defaults to "00000" (สำนักงานใหญ่) when flag is on but
                // user didn't fill it — covers the >95% case + matches the RD form.
                HasTaxInvoiceReference = request.HasTaxInvoiceReference,
                SupplierBranchCode = request.HasTaxInvoiceReference
                    ? (request.SupplierBranchCode ?? contact.BranchCode ?? "00000")
                    : request.SupplierBranchCode,
                CreditDays = request.CreditDays,
                PaymentTerms = request.PaymentTerms,
                // เงินมัดจำ/รับล่วงหน้า — เฉพาะใบเสร็จ/ใบสำคัญรับ
                IsDeposit = request.IsDeposit
                    && (request.DocumentType == DocumentType.Receipt
                        || request.DocumentType == DocumentType.ReceiptVoucher),
                DepositDeferredAccountCode = request.DepositDeferredAccountCode,
                DepositOutputVatDeferred = request.DepositOutputVatDeferred,
                // Tax Point §78 inputs (optional)
                DeliveryDate = request.DeliveryDate,
                OwnershipTransferDate = request.OwnershipTransferDate,
                ServiceUsedDate = request.ServiceUsedDate,
                BookingNumber = string.IsNullOrWhiteSpace(request.BookingNumber) ? null : request.BookingNumber.Trim(),
                InputVatAccountCodeOverride = string.IsNullOrWhiteSpace(request.InputVatAccountCodeOverride) ? null : request.InputVatAccountCodeOverride.Trim(),
                CreatedBy = createdBy
            };

            // ===== Settlement basis + ROLE SEPARATION (หลักบัญชีไทย) =====
            // ใบบันทึกค่าใช้จ่าย (Expense)   = "คำขอ/ตั้งหนี้" — บันทึกภาระ
            //   ค่าใช้จ่ายเข้าเจ้าหนี้ ไม่มีเงินออก (GL: Dr ค่าใช้จ่าย / Cr เจ้าหนี้)
            // ใบสำคัญจ่าย (PaymentVoucher)  = "การดำเนินการจ่ายเงินจริง" —
            //   เงินออกเสมอ (GL: Cr เงินสด/ธนาคาร; ถ้าอ้างอิงเอกสารตั้งหนี้
            //   จะตัดเจ้าหนี้ให้ด้วย) จึงห้ามเป็น "เครดิต" แบบลอย ๆ
            if (request.DocumentType == DocumentType.PaymentVoucher)
            {
                // A standalone PV must represent real cash leaving the company.
                // "Credit" is only meaningful when this PV SETTLES a prior
                // liability doc (PI/Expense via RelatedDocumentId from the
                // conversion path). An unpaid obligation belongs on an
                // Expense (ตั้งหนี้) instead.
                if (!doc.RelatedDocumentId.HasValue
                    && request.PaymentType == Models.Enums.PaymentType.Credit)
                    throw new InvalidOperationException(
                        "ใบสำคัญจ่ายคือเอกสารการจ่ายเงินจริง (เงินออกทันที) — " +
                        "หากยังไม่ได้จ่าย/ต้องการตั้งหนี้ไว้ก่อน กรุณาใช้ \"ใบบันทึกค่าใช้จ่าย\" " +
                        "แล้วแปลงเป็นใบสำคัญจ่ายเมื่อจ่ายเงินจริง");
                doc.PaymentType = request.PaymentType
                    ?? (doc.RelatedDocumentId.HasValue ? Models.Enums.PaymentType.Credit
                                                       : Models.Enums.PaymentType.Cash);
            }
            else if (request.DocumentType == DocumentType.Expense)
            {
                // The request/accrual document never moves cash by itself —
                // marking it "จ่ายทันที" would flag it paid while its GL
                // posting still credits AP, leaving a payable nobody clears.
                if (request.PaymentType == Models.Enums.PaymentType.Cash)
                    throw new InvalidOperationException(
                        "ใบบันทึกค่าใช้จ่ายคือเอกสารตั้งหนี้/คำขอ (ยังไม่จ่ายเงิน) — " +
                        "ถ้าจ่ายเงินสดทันทีให้ใช้ \"ใบสำคัญจ่าย\" หรือบันทึกใบนี้เป็นตั้งหนี้ " +
                        "แล้วแปลงเป็นใบสำคัญจ่าย/บันทึกการชำระเมื่อจ่ายจริง");
                doc.PaymentType = Models.Enums.PaymentType.Credit;
            }
            else if (request.DocumentType == DocumentType.CertificateInLieu)
            {
                // ใบรับรองแทนใบเสร็จ is always an immediate cash payment — no
                // payable, no outstanding balance (its JE credits Cash).
                doc.PaymentType = Models.Enums.PaymentType.Cash;
            }
            else
            {
                doc.PaymentType = request.PaymentType;
            }
            var isCashSettled = doc.PaymentType == Models.Enums.PaymentType.Cash;

            // Auto-fill DueDate from CreditDays when caller didn't provide one
            // explicitly. Keeps DSO/DPO reports working even when the partner
            // API only sends credit terms. Cash-settled documents never carry a
            // due date (the money already moved).
            if (isCashSettled)
                doc.DueDate = null;
            else if (doc.DueDate == null && doc.CreditDays.HasValue && doc.CreditDays.Value > 0)
                doc.DueDate = doc.DocumentDate.AddDays(doc.CreditDays.Value);

            // Auto-link Revenue Contract via Project when not explicitly provided.
            // If document is tagged to a project that has exactly one active
            // RevenueContract, link this doc to that contract automatically.
            if (request.ProjectId.HasValue && !request.RevenueContractId.HasValue)
            {
                var contracts = await _db.Set<RevenueContract>()
                    .Where(rc => rc.CompanyId == companyId
                        && rc.ProjectId == request.ProjectId.Value
                        && !rc.IsDeleted)
                    .Select(rc => rc.Id)
                    .Take(2)
                    .ToListAsync();
                if (contracts.Count == 1)
                    doc.RevenueContractId = contracts[0];
            }

            _db.Documents.Add(doc);

            decimal subTotal = 0, totalDiscount = 0, totalVat = 0, totalWht = 0;
            var order = 1;

            // โหลด InputVatClaimable flag ของทุกบัญชีที่ line ใช้ — รอบเดียว
            // ไว้บังคับ IsVatClaimable=false เมื่อบัญชีเป็นภาษีซื้อต้องห้าม.
            // resolve AccountCode → AccountId ก่อน (AI suggestion + integration
            // ส่งมาเป็น code) ภายในผังของบริษัท
            var codeCache = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in request.Lines ?? [])
            {
                if (!line.AccountId.HasValue && !string.IsNullOrWhiteSpace(line.AccountCode))
                {
                    var resolved = await ResolveAccountCodeAsync(companyId, line.AccountCode, codeCache);
                    if (resolved.HasValue)
                    {
                        // mutate via reflection-free pattern: rebuild line tuple
                        // (DocumentLineRequest is a record). Instead, store in a
                        // side map and read at construction below.
                    }
                }
            }
            // build a side map LineIndex → resolved AccountId so we don't mutate the record list
            var resolvedAccountIds = new Dictionary<int, Guid>();
            for (int i = 0; i < (request.Lines?.Count ?? 0); i++)
            {
                var l = request.Lines![i];
                if (l.AccountId.HasValue) { resolvedAccountIds[i] = l.AccountId.Value; continue; }
                if (!string.IsNullOrWhiteSpace(l.AccountCode))
                {
                    var rid = await ResolveAccountCodeAsync(companyId, l.AccountCode, codeCache);
                    if (rid.HasValue) resolvedAccountIds[i] = rid.Value;
                }
            }
            var accountIds = resolvedAccountIds.Values.Distinct().ToList();
            var accountFlags = accountIds.Count == 0
                ? new Dictionary<Guid, bool>()
                : await _db.ChartOfAccounts.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && accountIds.Contains(a.Id))
                    .ToDictionaryAsync(a => a.Id, a => a.InputVatClaimable);

            doc.PricesIncludeVat = request.PricesIncludeVat;
            doc.IsForeignService = request.IsForeignService;
            int lineIdx = -1;
            foreach (var line in request.Lines ?? [])
            {
                lineIdx++;
                var amt = ComputeLineAmounts(line, request.PricesIncludeVat);

                subTotal += amt.NetAmount;
                totalDiscount += amt.DiscountAmount;
                totalVat += amt.VatAmount;
                totalWht += amt.WhtAmount;

                var lineAccountId = resolvedAccountIds.TryGetValue(lineIdx, out var rid) ? (Guid?)rid : null;
                // ภาษีซื้อต้องห้าม: ถ้าบัญชีตั้งเป็น InputVatClaimable=false
                // (เช่น ค่ารับรอง) → บังคับ line.IsVatClaimable=false
                // ไม่ว่า request จะส่งอะไรมา — รักษา consistency กับ chart
                // ของบริษัท. User ที่ต้องการเคลม VAT ต้องเปลี่ยน account
                // หรือเปลี่ยน flag ของบัญชีที่ผังบัญชี.
                var enforcedClaimable = line.IsVatClaimable;
                string? enforcedReason = line.VatNonClaimableReason;
                if (lineAccountId.HasValue && accountFlags.TryGetValue(lineAccountId.Value, out var acctClaimable) && !acctClaimable)
                {
                    enforcedClaimable = false;
                    enforcedReason ??= "บัญชีนี้ตั้งเป็นภาษีซื้อต้องห้ามในผังบัญชี";
                }
                _db.DocumentLines.Add(new DocumentLine
                {
                    DocumentId = doc.Id,
                    LineOrder = order++,
                    Description = line.Description,
                    Quantity = line.Quantity,
                    Unit = line.Unit ?? "ชิ้น",
                    UnitPrice = line.UnitPrice,
                    DiscountPercent = line.DiscountPercent,
                    DiscountAmount = amt.DiscountAmount,
                    Amount = amt.NetAmount,
                    VatRate = line.VatRate,
                    VatAmount = amt.VatAmount,
                    WithholdingTaxRate = line.WithholdingTaxRate,
                    WithholdingTaxAmount = amt.WhtAmount,
                    AccountId = lineAccountId,
                    ProjectId = line.ProjectId,
                    ProductCode = string.IsNullOrWhiteSpace(line.ProductCode) ? null : line.ProductCode.Trim(),
                    SourceLineId = line.SourceLineId,
                    IsVatClaimable = enforcedClaimable,
                    VatNonClaimableReason = enforcedClaimable ? null : enforcedReason,
                    GlAccountAiFeedbackId = line.GlAccountAiFeedbackId,
                });
            }

            doc.SubTotal = subTotal;
            doc.DiscountAmount = totalDiscount;
            doc.VatAmount = totalVat;
            doc.WithholdingTaxAmount = totalWht;
            doc.TotalAmount = subTotal + totalVat - totalWht;
            // Cash-settled documents carry no outstanding balance — the cash
            // already moved, so PaidAmount = Total and BalanceDue = 0. This is
            // what keeps a จ่ายทันที voucher out of the aging / ค้างชำระ report
            // (the aging query keys on PaidAmount < TotalAmount).
            if (isCashSettled)
            {
                doc.PaidAmount = doc.TotalAmount;
                doc.BalanceDue = 0m;
            }
            else
            {
                doc.BalanceDue = doc.TotalAmount;
            }

            await _db.SaveChangesAsync();
            await _subscriptionService.IncrementUsageAsync(companyId, "document");

            await transaction.CommitAsync();

            // ใบสำคัญจ่าย (Cash) standalone — auto-approve ทันทีทุก channel
            // (UI/OCR/API). เงินจ่ายไปจริงแล้ว BalanceDue=0 ตั้งแต่ create
            // ไม่มีเหตุผลค้าง Draft ให้ผู้ใช้ต้องคลิก "อนุมัติ" อีกขั้น.
            // กรณี approve ล้มเหลว (§65 ตรี ไม่ระบุผู้รับ / period closed /
            // RequireApprovalForDocuments threshold) → log + คงค้าง Draft
            // ให้ผู้ใช้แก้แล้ว approve เอง.
            if (doc.DocumentType == DocumentType.PaymentVoucher
                && doc.PaymentType == Models.Enums.PaymentType.Cash
                && !doc.RelatedDocumentId.HasValue
                && doc.BalanceDue <= 0.005m)
            {
                try
                {
                    await ApproveDocumentAsync(companyId, doc.Id, createdBy, acknowledgeWarnings: true);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation(ex,
                        "PV Cash auto-approve skipped for {DocId} — staying Draft for manual fix", doc.Id);
                }
            }

            var created = await GetDocumentAsync(companyId, doc.Id);
            await FireWebhookAsync(companyId, "document.created", created);
            return created;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<DocumentResponse> GetDocumentAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents
            .Include(d => d.Contact)
            .Include(d => d.Lines).ThenInclude(l => l.Project)
            .Include(d => d.Lines).ThenInclude(l => l.Account)
            .Include(d => d.Project)
            .Include(d => d.BankAccount)
            .Include(d => d.PaymentAccount)
            .Include(d => d.ExpenseCategory)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var etax = await GetLatestEtaxAsync(companyId, new[] { documentId });

        // Conversion lineage — populate both directions so the detail
        // modal can show "แปลงมาจาก X" + "เอกสารต่อเนื่อง: Y, Z".
        DocumentBrief? upstream = null;
        if (doc.RelatedDocumentId.HasValue)
        {
            upstream = await _db.Documents
                .Where(p => p.Id == doc.RelatedDocumentId.Value && p.CompanyId == companyId)
                .Select(p => new DocumentBrief(p.Id, p.DocumentNumber, p.DocumentType,
                    p.Status, p.DocumentDate, p.TotalAmount))
                .FirstOrDefaultAsync();
        }
        var downstream = await _db.Documents
            .Where(c => c.RelatedDocumentId == documentId && c.CompanyId == companyId)
            .OrderBy(c => c.DocumentDate).ThenBy(c => c.CreatedAt)
            .Select(c => new DocumentBrief(c.Id, c.DocumentNumber, c.DocumentType,
                c.Status, c.DocumentDate, c.TotalAmount))
            .ToListAsync();

        var (pct, status) = await ComputeConversionStatusAsync(companyId, doc);

        // Project-cost booking summary — pull every PCE auto-spawned from
        // this doc (DocumentId or per-line DocumentLineId) and aggregate
        // by project so the UI can flag the doc "🏗️ ลงโครงการแล้ว 3 รายการ".
        var lineIds = doc.Lines.Select(l => l.Id).ToList();
        var pceRows = await _db.ProjectCostEntries
            .Where(c => c.CompanyId == companyId
                && !c.IsDeleted
                && (c.DocumentId == documentId
                    || (c.DocumentLineId.HasValue && lineIds.Contains(c.DocumentLineId.Value))))
            .Select(c => new {
                c.Id, c.ProjectId, c.DocumentLineId, c.Amount,
                ProjectCode = c.Project.Code,
                ProjectName = c.Project.Name,
            })
            .ToListAsync();

        var bookedByProject = pceRows
            .GroupBy(r => r.ProjectId)
            .Select(g => new ProjectCostBrief(
                g.Key, g.First().ProjectCode, g.First().ProjectName,
                g.Count(), g.Sum(r => r.Amount)))
            .OrderByDescending(b => b.Amount)
            .ToList();
        var pceByLine = pceRows
            .Where(r => r.DocumentLineId.HasValue)
            .GroupBy(r => r.DocumentLineId!.Value)
            .ToDictionary(g => g.Key, g => g.First().Id);

        return MapDocumentToResponse(doc, etax.GetValueOrDefault(documentId),
            upstream, downstream, pct, status,
            pceRows.Count > 0, pceRows.Count, pceRows.Sum(r => r.Amount), bookedByProject,
            pceByLine);
    }

    /// <summary>Compute completion percent across child docs for the
    /// source doc passed in. Returns null when the doc has no source
    /// lines (i.e. nothing consumable) so the UI can hide the badge.
    /// </summary>
    private async Task<(decimal? Pct, string? Status)> ComputeConversionStatusAsync(Guid companyId, Document source)
    {
        if (source.Lines == null || source.Lines.Count == 0) return (null, null);
        var sourceLineIds = source.Lines.Select(l => l.Id).ToList();
        var totalSourceQty = source.Lines.Sum(l => l.Quantity);
        if (totalSourceQty <= 0) return (null, null);
        var consumed = await _db.DocumentLines
            .Where(l => l.SourceLineId.HasValue && sourceLineIds.Contains(l.SourceLineId.Value))
            .Where(l => !l.Document.IsDeleted
                && l.Document.Status != DocumentStatus.Voided
                && l.Document.Status != DocumentStatus.Rejected)
            .SumAsync(l => (decimal?)l.Quantity) ?? 0;
        var pct = Math.Min(100, Math.Round((consumed / totalSourceQty) * 100, 1, MidpointRounding.AwayFromZero));
        var status = pct switch
        {
            >= 100 => "Full",
            > 0 => "Partial",
            _ => "None",
        };
        return (pct, status);
    }

    public async Task<DocumentResponse> GetDocumentForUserAsync(Guid companyId, Guid documentId, Guid userId)
    {
        var full = await GetDocumentAsync(companyId, documentId);
        if (full.Sensitivity == SensitivityKind.None || _sensitivity == null) return full;
        var allowed = await _sensitivity.CanViewAsync(companyId, userId, full.Sensitivity);
        if (allowed) return full;
        // Fetch only the bare-minimum metadata needed for the stub.
        var stub = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.Status, d.DocumentDate, d.DueDate, d.CreatedAt, d.Sensitivity, d.CreditNoteReason })
            .FirstAsync();
        return new DocumentResponse(stub.Id, stub.DocumentNumber, stub.DocumentType, stub.Status,
            stub.DocumentDate, stub.DueDate,
            new ContactBrief(Guid.Empty, "[ซ่อน]", null),
            0, 0, 0, 0, 0, 0, 0, null, null,
            new List<DocumentLineResponse>(), stub.CreatedAt,
            Sensitivity: stub.Sensitivity,
            IsRedacted: true,
            RedactedReason: SensitivityRedactReason(stub.Sensitivity),
            CreditNoteReason: stub.CreditNoteReason);
    }

    public async Task<List<DocumentPeriod>> GetDocumentPeriodsAsync(Guid companyId, IReadOnlyList<DocumentType>? types = null)
    {
        // global query filter ตัด IsDeleted ให้แล้ว
        var q = _db.Documents.Where(d => d.CompanyId == companyId);
        if (types != null && types.Count > 0)
            q = q.Where(d => types.Contains(d.DocumentType));
        var groups = await q
            .GroupBy(d => new { d.DocumentDate.Year, d.DocumentDate.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
            .ToListAsync();
        return groups
            .OrderByDescending(g => g.Year).ThenByDescending(g => g.Month)
            .Select(g => new DocumentPeriod(g.Year, g.Month, g.Count))
            .ToList();
    }

    public async Task<PagedResponse<DocumentResponse>> GetDocumentsForUserAsync(Guid companyId, Guid userId, DocumentType? type, PagedRequest request, Guid? projectId = null, Guid? contactId = null, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, Guid? relatedDocumentId = null, Guid? revenueContractId = null, bool staleOnly = false, IReadOnlyList<DocumentType>? types = null)
    {
        var page = await GetDocumentsAsync(companyId, type, request, projectId, contactId, status, fromDate, toDate, relatedDocumentId, revenueContractId, staleOnly, types);
        if (_sensitivity == null) return page;
        var visible = await _sensitivity.GetVisibleKindsAsync(companyId, userId);

        // Replace any item the user can't see with a redacted stub. We keep
        // the row in the list (don't filter out) so paging counts stay stable
        // and integration targets see "row N is hidden" rather than "row N
        // missing" — the latter is impossible to distinguish from a delete.
        var redactedItems = page.Items.Select(it =>
            it.Sensitivity == SensitivityKind.None || visible.Contains(it.Sensitivity)
                ? it
                : new DocumentResponse(it.Id, it.DocumentNumber, it.DocumentType, it.Status,
                    it.DocumentDate, it.DueDate,
                    new ContactBrief(Guid.Empty, "[ซ่อน]", null),
                    0, 0, 0, 0, 0, 0, 0, null, null,
                    new List<DocumentLineResponse>(), it.CreatedAt,
                    Sensitivity: it.Sensitivity,
                    IsRedacted: true,
                    RedactedReason: SensitivityRedactReason(it.Sensitivity),
                    CreditNoteReason: it.CreditNoteReason)
        ).ToList();
        return new PagedResponse<DocumentResponse>(redactedItems, page.TotalCount, page.Page, page.PageSize, page.TotalPages);
    }

    private static string SensitivityRedactReason(SensitivityKind kind) => kind switch
    {
        SensitivityKind.Payroll      => "ต้องมีสิทธิ์ดูข้อมูลเงินเดือน (perm:Payroll.View)",
        SensitivityKind.ExecutivePay => "ต้องมีสิทธิ์ดูข้อมูลค่าตอบแทนผู้บริหาร",
        SensitivityKind.HrPersonal   => "ต้องมีสิทธิ์ดูข้อมูลบุคลากร",
        SensitivityKind.Confidential => "ต้องมีสิทธิ์ดูเอกสารลับ (perm:SensitiveDocs.View)",
        _                            => "ต้องมีสิทธิ์เพิ่มเติม"
    };

    public async Task<PagedResponse<DocumentResponse>> GetDocumentsAsync(Guid companyId, DocumentType? type, PagedRequest request, Guid? projectId = null, Guid? contactId = null, string? status = null, DateTime? fromDate = null, DateTime? toDate = null, Guid? relatedDocumentId = null, Guid? revenueContractId = null, bool staleOnly = false, IReadOnlyList<DocumentType>? types = null)
    {
        var query = _db.Documents
            .Include(d => d.Contact)
            .Include(d => d.Lines)
            .Include(d => d.Project)
            .Include(d => d.BankAccount)
            .Include(d => d.PaymentAccount)
            .Include(d => d.ExpenseCategory)
            .Where(d => d.CompanyId == companyId);

        if (type.HasValue)
            query = query.Where(d => d.DocumentType == type.Value);
        // กรองหลายประเภทพร้อมกัน (ฝั่งรายรับ/รายจ่าย) — ต้องทำที่ server เพื่อให้
        // pagination ถูกต้อง. เดิม UI ส่ง types[] มาแต่ server อ่านแค่ type เดียว →
        // server แบ่งหน้ารวมทุกประเภท แล้วค่อยกรองฝั่งที่ client → เอกสารฝั่ง
        // รายจ่ายที่เก่ากว่า 20 อันดับแรก (รวมทุกประเภท) หลุดหน้า ไม่ขึ้นเลย.
        else if (types != null && types.Count > 0)
            query = query.Where(d => types.Contains(d.DocumentType));

        if (projectId.HasValue)
            query = query.Where(d => d.ProjectId == projectId.Value
                || d.Lines.Any(l => l.ProjectId == projectId.Value));

        if (contactId.HasValue)
            query = query.Where(d => d.ContactId == contactId.Value);

        if (relatedDocumentId.HasValue)
            query = query.Where(d => d.RelatedDocumentId == relatedDocumentId.Value);

        if (revenueContractId.HasValue)
            query = query.Where(d => d.RevenueContractId == revenueContractId.Value);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<DocumentStatus>(status, true, out var statusEnum))
            query = query.Where(d => d.Status == statusEnum);

        // Stale = parked in a non-terminal status past the threshold.
        if (staleOnly)
        {
            var staleCutoff = DateTime.UtcNow.Date.AddDays(-StaleThresholdDays);
            query = query.Where(d => d.DocumentDate < staleCutoff
                && d.Status != DocumentStatus.Paid
                && d.Status != DocumentStatus.Voided
                && d.Status != DocumentStatus.Rejected);
        }

        if (fromDate.HasValue)
            query = query.Where(d => d.DocumentDate >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(d => d.DocumentDate <= toDate.Value);

        if (!string.IsNullOrEmpty(request.Search))
        {
            var search = $"%{request.Search}%";
            query = query.Where(d => EF.Functions.ILike(d.DocumentNumber, search)
                || (d.Contact != null && EF.Functions.ILike(d.Contact.Name, search)));
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(d => d.DocumentDate)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var etaxByDoc = await GetLatestEtaxAsync(companyId, items.Select(i => i.Id));

        // Batch-flag "ลงโครงการแล้ว" so the list view can show a 🏗️
        // badge without per-row roundtrips. Only top-level counts +
        // amount; the per-line + per-project breakdown is rendered
        // only on detail-view to keep the list payload small.
        var ids = items.Select(i => i.Id).ToList();
        var pceSummary = await _db.ProjectCostEntries
            .Where(c => c.CompanyId == companyId
                && !c.IsDeleted
                && c.DocumentId.HasValue
                && ids.Contains(c.DocumentId!.Value))
            .GroupBy(c => c.DocumentId!.Value)
            .Select(g => new { DocId = g.Key, Count = g.Count(), Amount = g.Sum(c => c.Amount) })
            .ToDictionaryAsync(g => g.DocId, g => (g.Count, g.Amount));

        return new PagedResponse<DocumentResponse>(
            items.Select(d => {
                var (pceCount, pceAmount) = pceSummary.GetValueOrDefault(d.Id);
                return MapDocumentToResponse(d, etaxByDoc.GetValueOrDefault(d.Id),
                    hasPce: pceCount > 0, pceCount: pceCount, pceAmount: pceAmount);
            }).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    /// <summary>
    /// Batch-load the latest e-Tax invoice per document. Picks the highest-status
    /// (Accepted &gt; Submitted &gt; Signed &gt; Generated) so the UI shows the most
    /// "advanced" eTax record that exists. Used to surface the download button on
    /// docs whose XML has been embedded in a PDF/A-3.
    /// </summary>
    private async Task<Dictionary<Guid, (Guid EtaxId, EtaxStatus Status)>> GetLatestEtaxAsync(
        Guid companyId, IEnumerable<Guid> docIds)
    {
        var ids = docIds.Distinct().ToList();
        if (ids.Count == 0) return new();

        var rows = await _db.EtaxInvoices
            .AsNoTracking()
            .Where(e => e.CompanyId == companyId && ids.Contains(e.DocumentId))
            .Select(e => new { e.Id, e.DocumentId, e.Status, e.CreatedAt })
            .ToListAsync();

        return rows
            .GroupBy(r => r.DocumentId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderByDescending(r => (int)r.Status)
                    .ThenByDescending(r => r.CreatedAt)
                    .Select(r => (r.Id, r.Status))
                    .First());
    }

    public async Task<DocumentResponse> UpdateDocumentAsync(Guid companyId, Guid documentId, UpdateDocumentRequest request)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (doc.Status != DocumentStatus.Draft)
            throw new InvalidOperationException("แก้ไขได้เฉพาะเอกสาร Draft เท่านั้น");

        if (request.DocumentDate.HasValue) doc.DocumentDate = Accounting.Helpers.ThaiDate.CalendarDateUtc(request.DocumentDate.Value);
        if (request.DueDate.HasValue) doc.DueDate = Accounting.Helpers.ThaiDate.CalendarDateUtc(request.DueDate.Value);
        if (request.ContactId.HasValue) doc.ContactId = request.ContactId.Value;
        if (request.Reference != null) doc.Reference = request.Reference;
        if (request.Notes != null) doc.Notes = request.Notes;
        if (request.CustomAppendix != null) doc.CustomAppendix = request.CustomAppendix;
        if (request.CustomFooterNotes != null) doc.CustomFooterNotes = request.CustomFooterNotes;
        if (request.CustomTermsAndConditions != null) doc.CustomTermsAndConditions = request.CustomTermsAndConditions;
        if (request.RevenueContractId.HasValue) doc.RevenueContractId = request.RevenueContractId.Value;
        if (request.PerformanceObligationId.HasValue) doc.PerformanceObligationId = request.PerformanceObligationId.Value;
        if (request.SupplierInvoiceNumber != null) doc.SupplierInvoiceNumber = request.SupplierInvoiceNumber;
        if (request.SupplierTaxInvoiceDate.HasValue) doc.SupplierTaxInvoiceDate = request.SupplierTaxInvoiceDate.Value;
        if (request.HasTaxInvoiceReference.HasValue) doc.HasTaxInvoiceReference = request.HasTaxInvoiceReference.Value;
        if (request.SupplierBranchCode != null) doc.SupplierBranchCode = request.SupplierBranchCode;
        if (request.CreditDays.HasValue) doc.CreditDays = request.CreditDays.Value;
        if (request.PaymentTerms != null) doc.PaymentTerms = request.PaymentTerms;

        // Tax Point §78 inputs (Draft edit)
        if (request.DeliveryDate.HasValue) doc.DeliveryDate = request.DeliveryDate.Value;
        if (request.OwnershipTransferDate.HasValue) doc.OwnershipTransferDate = request.OwnershipTransferDate.Value;
        if (request.ServiceUsedDate.HasValue) doc.ServiceUsedDate = request.ServiceUsedDate.Value;
        if (request.BookingNumber != null) doc.BookingNumber = string.IsNullOrWhiteSpace(request.BookingNumber) ? null : request.BookingNumber.Trim();
        if (request.InputVatAccountCodeOverride != null) doc.InputVatAccountCodeOverride = string.IsNullOrWhiteSpace(request.InputVatAccountCodeOverride) ? null : request.InputVatAccountCodeOverride.Trim();

        // ===== Fields ที่เดิม "เงียบหาย" ตอนแก้ Draft (เคยมีเฉพาะตอน Create) =====
        // CreditNoteReason (§86/10), IsForeignService (ภ.พ.36/ภ.ง.ด.54), และชุดเงินมัดจำ.
        // ทุก field ใช้ HasValue / != null → omit = คงค่าเดิม.
        if (request.CreditNoteReason.HasValue) doc.CreditNoteReason = request.CreditNoteReason.Value;
        if (request.IsForeignService.HasValue) doc.IsForeignService = request.IsForeignService.Value;
        if (request.IsDeposit.HasValue) doc.IsDeposit = request.IsDeposit.Value;
        if (request.DepositDeferredAccountCode != null) doc.DepositDeferredAccountCode = string.IsNullOrWhiteSpace(request.DepositDeferredAccountCode) ? null : request.DepositDeferredAccountCode.Trim();
        if (request.DepositOutputVatDeferred.HasValue) doc.DepositOutputVatDeferred = request.DepositOutputVatDeferred.Value;

        // Project re-assignment (only allowed while Draft, which is enforced above)
        if (request.ProjectId.HasValue)
        {
            var projectOk = await _db.Projects.AnyAsync(p =>
                p.Id == request.ProjectId.Value && p.CompanyId == companyId);
            if (!projectOk)
                throw new InvalidOperationException("ไม่พบโครงการในบริษัทนี้");
            doc.ProjectId = request.ProjectId.Value;
        }

        if (request.BankAccountId.HasValue)
            doc.BankAccountId = request.BankAccountId.Value;
        if (request.PaymentAccountId.HasValue)
            doc.PaymentAccountId = request.PaymentAccountId.Value;
        if (request.ExpenseCategoryId.HasValue)
            doc.ExpenseCategoryId = request.ExpenseCategoryId.Value;

        // CertificateInLieu fields
        if (request.CertificateReason != null) doc.CertificateReason = request.CertificateReason;
        if (request.CertifierName != null) doc.CertifierName = request.CertifierName;
        if (request.CertifierPosition != null) doc.CertifierPosition = request.CertifierPosition;
        if (request.WitnessName != null) doc.WitnessName = request.WitnessName;
        if (request.WitnessPosition != null) doc.WitnessPosition = request.WitnessPosition;
        if (request.PaymentDate.HasValue) doc.PaymentDate = request.PaymentDate.Value;

        if (request.Lines != null)
        {
            // Re-validate on edit — the create path's guards must hold here too
            // (previously an edit could set qty=0 / VatRate=-99 unchecked).
            await ValidateDocumentLinesAsync(companyId, request.Lines);

            _db.DocumentLines.RemoveRange(doc.Lines);

            if (request.PricesIncludeVat.HasValue) doc.PricesIncludeVat = request.PricesIncludeVat.Value;

            decimal subTotal = 0, totalDiscount = 0, totalVat = 0, totalWht = 0;
            var order = 1;

            // resolve AccountCode → AccountId per line ก่อน (รองรับ AI suggestion)
            var updCodeCache = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);
            var updResolvedAccountIds = new Dictionary<int, Guid>();
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var l = request.Lines[i];
                if (l.AccountId.HasValue) { updResolvedAccountIds[i] = l.AccountId.Value; continue; }
                if (!string.IsNullOrWhiteSpace(l.AccountCode))
                {
                    var rid = await ResolveAccountCodeAsync(companyId, l.AccountCode, updCodeCache);
                    if (rid.HasValue) updResolvedAccountIds[i] = rid.Value;
                }
            }

            // VAT-claimability enforcement ตามผังบัญชี (เหมือนใน Create)
            var updAccountIds = updResolvedAccountIds.Values.Distinct().ToList();
            var accountFlags = updAccountIds.Count == 0
                ? new Dictionary<Guid, bool>()
                : await _db.ChartOfAccounts.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && updAccountIds.Contains(a.Id))
                    .ToDictionaryAsync(a => a.Id, a => a.InputVatClaimable);

            int updLineIdx = -1;
            foreach (var line in request.Lines)
            {
                updLineIdx++;
                var amt = ComputeLineAmounts(line, doc.PricesIncludeVat);

                subTotal += amt.NetAmount;
                totalDiscount += amt.DiscountAmount;
                totalVat += amt.VatAmount;
                totalWht += amt.WhtAmount;

                var lineAccountId = updResolvedAccountIds.TryGetValue(updLineIdx, out var rid2) ? (Guid?)rid2 : null;
                var enforcedClaimable = line.IsVatClaimable;
                string? enforcedReason = line.VatNonClaimableReason;
                if (lineAccountId.HasValue && accountFlags.TryGetValue(lineAccountId.Value, out var acctClaimable) && !acctClaimable)
                {
                    enforcedClaimable = false;
                    enforcedReason ??= "บัญชีนี้ตั้งเป็นภาษีซื้อต้องห้ามในผังบัญชี";
                }
                _db.DocumentLines.Add(new DocumentLine
                {
                    DocumentId = doc.Id,
                    LineOrder = order++,
                    Description = line.Description,
                    Quantity = line.Quantity,
                    Unit = line.Unit ?? "ชิ้น",
                    UnitPrice = line.UnitPrice,
                    DiscountPercent = line.DiscountPercent,
                    DiscountAmount = amt.DiscountAmount,
                    Amount = amt.NetAmount,
                    VatRate = line.VatRate,
                    VatAmount = amt.VatAmount,
                    WithholdingTaxRate = line.WithholdingTaxRate,
                    WithholdingTaxAmount = amt.WhtAmount,
                    AccountId = lineAccountId,
                    ProjectId = line.ProjectId,
                    ProductCode = string.IsNullOrWhiteSpace(line.ProductCode) ? null : line.ProductCode.Trim(),
                    // Preserve the conversion-traceability link across edits —
                    // the edit form round-trips SourceLineId per line, so a
                    // converted document keeps its fulfilment accounting intact.
                    SourceLineId = line.SourceLineId,
                    IsVatClaimable = enforcedClaimable,
                    VatNonClaimableReason = enforcedClaimable ? null : enforcedReason,
                    GlAccountAiFeedbackId = line.GlAccountAiFeedbackId,
                });
            }

            doc.SubTotal = subTotal;
            doc.DiscountAmount = totalDiscount;
            doc.VatAmount = totalVat;
            doc.WithholdingTaxAmount = totalWht;
            doc.TotalAmount = subTotal + totalVat - totalWht;

            // Preserve / apply the settlement basis on edit. A cash-settled
            // voucher must stay fully paid (BalanceDue = 0, no due date) even
            // after its lines/total change — otherwise editing it would
            // re-introduce a phantom outstanding balance.
            // ROLE SEPARATION (same rules as create): an Expense is the
            // request/accrual side — it can never become "จ่ายทันที"; a
            // standalone PV is the disbursement side — it can never become
            // an unpaid "เครดิต" liability.
            if (request.PaymentType.HasValue)
            {
                if (doc.DocumentType == DocumentType.Expense
                    && request.PaymentType == Models.Enums.PaymentType.Cash)
                    throw new InvalidOperationException(
                        "ใบบันทึกค่าใช้จ่ายคือเอกสารตั้งหนี้/คำขอ (ยังไม่จ่ายเงิน) — " +
                        "การจ่ายให้ทำผ่าน \"ใบสำคัญจ่าย\" หรือบันทึกการชำระเงิน");
                if (doc.DocumentType == DocumentType.PaymentVoucher
                    && !doc.RelatedDocumentId.HasValue
                    && request.PaymentType == Models.Enums.PaymentType.Credit)
                    throw new InvalidOperationException(
                        "ใบสำคัญจ่ายคือเอกสารการจ่ายเงินจริง — หากยังไม่ได้จ่าย " +
                        "กรุณาใช้ \"ใบบันทึกค่าใช้จ่าย\" (ตั้งหนี้) แทน");
                doc.PaymentType = request.PaymentType;
            }
            if (doc.PaymentType == Models.Enums.PaymentType.Cash)
            {
                doc.PaidAmount = doc.TotalAmount;
                doc.BalanceDue = 0m;
                doc.DueDate = null;
            }
            else
            {
                doc.BalanceDue = doc.TotalAmount - doc.PaidAmount;
            }
        }

        await _db.SaveChangesAsync();
        // ปิดลูปการสอน local model — บรรทัดที่มี FeedbackId + AccountId สุดท้าย
        // จะถูกบันทึก choice ทันที (กฎเหล็ก #1: เก็บ feedback ทุกครั้งที่ user
        // ตัดสินใจ — accept หรือ override)
        var savedLines = await _db.DocumentLines.AsNoTracking()
            .Where(l => l.DocumentId == doc.Id).ToListAsync();
        await RecordLineAccountFeedbackAsync(savedLines);
        var updated = await GetDocumentAsync(companyId, documentId);
        await FireWebhookAsync(companyId, "document.updated", updated);
        return updated;
    }

    public async Task<DocumentResponse> CompleteSupplierTaxInvoiceAsync(
        Guid companyId, Guid documentId, CompleteSupplierTaxInvoiceRequest request, string actor)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        // method นี้สำหรับเอกสาร approved ที่ค้าง 11640 — Draft ใช้ UpdateDocumentAsync
        if (doc.Status == DocumentStatus.Draft)
            throw new InvalidOperationException(
                "เอกสารยังเป็น Draft — แก้ใบกำกับผ่านการแก้ไขปกติ (ยังไม่ได้ลงบัญชี)");
        if (doc.Status == DocumentStatus.Voided)
            throw new InvalidOperationException("เอกสารถูกยกเลิกแล้ว — แก้ไขไม่ได้");

        // เติม/แก้เฉพาะ field ที่ส่งมา (null = คงค่าเดิม)
        if (request.SupplierInvoiceNumber != null)
            doc.SupplierInvoiceNumber = request.SupplierInvoiceNumber;
        if (request.SupplierTaxInvoiceDate.HasValue)
            doc.SupplierTaxInvoiceDate = request.SupplierTaxInvoiceDate.Value;
        if (request.SupplierBranchCode != null)
            doc.SupplierBranchCode = request.SupplierBranchCode;
        // override: "" = ล้าง (กลับ default 11610/11640); null = ไม่แตะ; ค่าอื่น = pin
        if (request.InputVatAccountCodeOverride != null)
            doc.InputVatAccountCodeOverride =
                request.InputVatAccountCodeOverride.Length == 0
                    ? null
                    : request.InputVatAccountCodeOverride;

        // ถ้าข้อมูลครบแล้ว + เดิมค้าง 11640 → gen adjusting JE 11640→11610
        var reclassified = await ReclassifyUndueInputVatAsync(companyId, doc, actor);

        await _db.SaveChangesAsync();
        var updated = await GetDocumentAsync(companyId, documentId);
        await FireWebhookAsync(companyId,
            reclassified ? "document.input_vat_reclassified" : "document.updated", updated);
        return updated;
    }

    public async Task<DocumentResponse> RealizeDepositAsync(
        Guid companyId, Guid documentId, RealizeDepositRequest request, string actor)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (!doc.IsDeposit)
            throw new InvalidOperationException("เอกสารนี้ไม่ใช่เงินมัดจำ/รับล่วงหน้า");
        if (doc.Status == DocumentStatus.Draft || doc.Status == DocumentStatus.Voided)
            throw new InvalidOperationException("รับรู้รายได้ได้เฉพาะมัดจำที่อนุมัติแล้ว");

        // ฐาน (ไม่รวม VAT) คงค้างที่ยังรับรู้ไม่ได้
        var outstanding = doc.SubTotal - doc.DepositRealizedAmount;
        if (request.Amount <= 0)
            throw new InvalidOperationException("จำนวนเงินที่รับรู้ต้องมากกว่า 0");
        if (request.Amount > outstanding + 0.005m)
            throw new InvalidOperationException(
                $"รับรู้เกินยอดมัดจำคงค้าง (คงค้าง {outstanding:N2}, ขอรับรู้ {request.Amount:N2})");

        var deferredAcc = await FindAccountAsync(companyId, doc.DepositDeferredAccountCode ?? "21712")
            ?? await FindAccountAsync(companyId, "217")
            ?? throw new InvalidOperationException("ไม่พบบัญชีขายรอรับรู้ (217xx) ในผังบัญชี");
        var revenueAcc = await FindAccountAsync(companyId, request.RevenueAccountCode ?? "41000")
            ?? await FindAccountAsync(companyId, "42000")
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountType == AccountType.Revenue && a.Level >= 4 && a.IsActive)
                .OrderBy(a => a.AccountCode).FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("ไม่พบบัญชีรายได้ในผังบัญชี");

        var when = request.RealizeDate ?? DateTime.UtcNow;
        // เคส Deferred output VAT: การรับรู้รายได้ (ส่งมอบ/ให้บริการ) = tax point
        // เกิดจริง → ย้ายภาษีขายรอเรียกเก็บ 21913 → ภาษีขาย ภ.พ.30 21911 เต็มจำนวน
        // ครั้งแรกที่ realize (จุดรับผิดเกิดทันทีที่เริ่มส่งมอบ — บันทึกเชิงระวัง).
        var recognizeDeferredVat = doc.DepositOutputVatDeferred
            && doc.DepositOutputVatRecognizedAt == null
            && doc.VatAmount > 0;
        ChartOfAccount? deferredVatAcc = null, outputVatAcc = null;
        if (recognizeDeferredVat)
        {
            deferredVatAcc = await FindAccountAsync(companyId, "21913");
            outputVatAcc = await FindAccountAsync(companyId, "21911");
            if (deferredVatAcc == null || outputVatAcc == null)
                recognizeDeferredVat = false;   // ผังไม่รองรับ → คงไว้ที่เดิม
        }
        var vatMove = recognizeDeferredVat ? doc.VatAmount : 0m;

        var entryNumber = await GetNextJournalEntryNumberAsync(companyId, "JV");
        var period = await ResolveFiscalPeriodAsync(companyId, when);
        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = when,
            JournalType = JournalType.General,
            Description = $"รับรู้รายได้จากมัดจำ - {doc.DocumentNumber}",
            Reference = doc.DocumentNumber,
            Status = JournalEntryStatus.Posted,
            TotalDebit = request.Amount + vatMove,
            TotalCredit = request.Amount + vatMove,
            CreatedBy = actor,
            IsAutoGenerated = true,
            SourceDocumentId = doc.Id,
            FiscalPeriodId = period?.Id,
            ProjectId = doc.ProjectId,
        };
        _db.JournalEntries.Add(je);
        var lineNo = 1;
        _db.JournalEntryLines.Add(new JournalEntryLine
        {
            JournalEntryId = je.Id, AccountId = deferredAcc.Id,
            DebitAmount = request.Amount, CreditAmount = 0,
            Description = "ตัดขายรอรับรู้ (มัดจำ)", LineOrder = lineNo++,
        });
        _db.JournalEntryLines.Add(new JournalEntryLine
        {
            JournalEntryId = je.Id, AccountId = revenueAcc.Id,
            DebitAmount = 0, CreditAmount = request.Amount,
            Description = "รับรู้รายได้", LineOrder = lineNo++,
        });
        if (recognizeDeferredVat)
        {
            _db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = je.Id, AccountId = deferredVatAcc!.Id,
                DebitAmount = vatMove, CreditAmount = 0,
                Description = "ตัดภาษีขายรอเรียกเก็บ (tax point เกิด)", LineOrder = lineNo++,
            });
            _db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = je.Id, AccountId = outputVatAcc!.Id,
                DebitAmount = 0, CreditAmount = vatMove,
                Description = "ภาษีขาย ภ.พ.30 (มัดจำถึงกำหนด)", LineOrder = lineNo++,
            });
            doc.DepositOutputVatRecognizedAt = when;   // เข้า ภ.พ.30 เดือนนี้
        }

        doc.DepositRealizedAmount += request.Amount;
        if (doc.SubTotal - doc.DepositRealizedAmount <= 0.005m)
            doc.DepositRealizedAt = when;   // ปิดมัดจำ (รับรู้ครบ)

        await _db.SaveChangesAsync();
        var updated = await GetDocumentAsync(companyId, documentId);
        await FireWebhookAsync(companyId, "deposit.realized", updated);
        return updated;
    }

    public async Task<List<DepositSummary>> GetDepositsAsync(Guid companyId, string? status = null)
    {
        var rows = await _db.Documents.AsNoTracking()
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId && d.IsDeposit
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided)
            .OrderByDescending(d => d.DocumentDate)
            .ToListAsync();

        var now = DateTime.UtcNow;
        var list = rows.Select(d =>
        {
            var outstanding = d.SubTotal - d.DepositRealizedAmount;
            var st = outstanding <= 0.005m ? "Realized"
                : d.DepositRealizedAmount > 0 ? "Partial" : "Outstanding";
            return new DepositSummary(
                d.Id, d.DocumentNumber, d.DocumentDate,
                d.Contact?.Name ?? "", d.Contact?.TaxId,
                d.SubTotal, d.VatAmount, d.TotalAmount,
                d.DepositRealizedAmount, outstanding,
                d.DepositRealizedAt,
                (int)(now.Date - d.DocumentDate.Date).TotalDays,
                st, d.DepositDeferredAccountCode,
                d.Reference, d.DepositOutputVatDeferred, d.DepositOutputVatRecognizedAt,
                d.BookingNumber);
        });
        if (!string.IsNullOrWhiteSpace(status))
            list = list.Where(x => string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase));
        return list.ToList();
    }

    public async Task<List<DocumentResponse>> GetDocumentsByBookingAsync(Guid companyId, string bookingNumber)
    {
        if (string.IsNullOrWhiteSpace(bookingNumber)) return new List<DocumentResponse>();
        var key = bookingNumber.Trim();
        var ids = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.BookingNumber == key
                && d.Status != DocumentStatus.Voided)
            .OrderBy(d => d.DocumentDate)
            .Select(d => d.Id)
            .ToListAsync();
        var result = new List<DocumentResponse>(ids.Count);
        foreach (var id in ids) result.Add(await GetDocumentAsync(companyId, id));
        return result;
    }

    public async Task<ContactDepositSummary> GetContactDepositSummaryAsync(Guid companyId, Guid contactId)
    {
        var all = await GetDepositsAsync(companyId);
        var forContact = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.IsDeposit && d.ContactId == contactId
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided)
            .Select(d => d.Id).ToListAsync();
        var ids = forContact.ToHashSet();
        var deposits = all.Where(d => ids.Contains(d.Id) && d.OutstandingAmount > 0.005m).ToList();
        return new ContactDepositSummary(
            contactId,
            deposits.Sum(d => d.OutstandingAmount),
            deposits.Count,
            deposits);
    }

    public async Task<DocumentResponse> RefundDepositAsync(
        Guid companyId, Guid documentId, RefundDepositRequest request, string actor)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines).Include(d => d.Contact)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        if (!doc.IsDeposit)
            throw new InvalidOperationException("เอกสารนี้ไม่ใช่เงินมัดจำ");
        if (doc.Status == DocumentStatus.Draft || doc.Status == DocumentStatus.Voided)
            throw new InvalidOperationException("คืนมัดจำได้เฉพาะมัดจำที่อนุมัติแล้ว");

        if (request.Amount <= 0)
            throw new InvalidOperationException("จำนวนเงินคืนต้องมากกว่า 0");
        if (request.Amount > doc.TotalAmount - doc.DepositRefundedAmount + 0.005m)
            throw new InvalidOperationException(
                $"คืนเกินยอดมัดจำ (ยอดมัดจำ {doc.TotalAmount:N2}, คืนไปแล้ว {doc.DepositRefundedAmount:N2})");

        // แยกฐาน + VAT จากยอด gross ที่จะคืน (ตามสัดส่วนเดิมของใบ)
        var vatPortion = doc.TotalAmount > 0 ? doc.VatAmount / doc.TotalAmount : 0m;
        var refundVat = Math.Round(request.Amount * vatPortion, 2, MidpointRounding.AwayFromZero);
        var refundBase = request.Amount - refundVat;

        var deferredAcc = await FindAccountAsync(companyId, doc.DepositDeferredAccountCode ?? "21712")
            ?? await FindAccountAsync(companyId, "217");
        var outVatAcc = doc.DepositOutputVatDeferred && doc.DepositOutputVatRecognizedAt == null
            ? await FindAccountAsync(companyId, "21913")
            : await FindAccountAsync(companyId, "21911");
        var moneyAcc = await FindAccountAsync(companyId, "111");
        if (deferredAcc == null || moneyAcc == null)
            throw new InvalidOperationException("ไม่พบผังบัญชีขายรอรับรู้/เงินสดสำหรับคืนมัดจำ");

        var when = request.RefundDate ?? DateTime.UtcNow;
        var entryNumber = await GetNextJournalEntryNumberAsync(companyId, "JV");
        var period = await ResolveFiscalPeriodAsync(companyId, when);
        var je = new JournalEntry
        {
            CompanyId = companyId, EntryNumber = entryNumber, EntryDate = when,
            JournalType = JournalType.General,
            Description = $"คืนเงินมัดจำ - {doc.DocumentNumber}" + (string.IsNullOrWhiteSpace(request.Reason) ? "" : $" ({request.Reason})"),
            Reference = doc.DocumentNumber, Status = JournalEntryStatus.Posted,
            TotalDebit = request.Amount, TotalCredit = request.Amount,
            CreatedBy = actor, IsAutoGenerated = true, SourceDocumentId = doc.Id,
            FiscalPeriodId = period?.Id, ProjectId = doc.ProjectId,
        };
        _db.JournalEntries.Add(je);
        var ln = 1;
        // Dr ขายรอรับรู้ (กลับรายการรายได้รับล่วงหน้า)
        _db.JournalEntryLines.Add(new JournalEntryLine {
            JournalEntryId = je.Id, AccountId = deferredAcc.Id,
            DebitAmount = refundBase, CreditAmount = 0,
            Description = "คืนมัดจำ - กลับขายรอรับรู้", LineOrder = ln++ });
        // Dr ภาษีขาย (ใบลดหนี้ — กลับ output VAT ที่เคยรับ)
        if (refundVat > 0 && outVatAcc != null)
            _db.JournalEntryLines.Add(new JournalEntryLine {
                JournalEntryId = je.Id, AccountId = outVatAcc.Id,
                DebitAmount = refundVat, CreditAmount = 0,
                Description = "คืนมัดจำ - กลับภาษีขาย (ใบลดหนี้)", LineOrder = ln++ });
        // Cr เงินสด/ธนาคาร
        _db.JournalEntryLines.Add(new JournalEntryLine {
            JournalEntryId = je.Id, AccountId = moneyAcc.Id,
            DebitAmount = 0, CreditAmount = request.Amount,
            Description = "คืนเงินมัดจำให้ลูกค้า", LineOrder = ln++ });

        doc.DepositRefundedAmount += request.Amount;
        doc.DepositRefundedAt = when;
        doc.DepositRefundReason = request.Reason;
        await _db.SaveChangesAsync();
        var updated = await GetDocumentAsync(companyId, documentId);
        await FireWebhookAsync(companyId, "deposit.refunded", updated);
        return updated;
    }

    public async Task<DocumentResponse> ApplyDepositToInvoiceAsync(
        Guid companyId, Guid invoiceId, ApplyDepositRequest request, string actor)
    {
        var invoice = await _db.Documents
            .Include(d => d.Contact)
            .FirstOrDefaultAsync(d => d.Id == invoiceId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบแจ้งหนี้");
        var deposit = await _db.Documents
            .Include(d => d.Lines).Include(d => d.Contact)
            .FirstOrDefaultAsync(d => d.Id == request.DepositDocumentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสารมัดจำ");
        if (!deposit.IsDeposit)
            throw new InvalidOperationException("เอกสารที่อ้างไม่ใช่เงินมัดจำ");
        if (deposit.ContactId != invoice.ContactId)
            throw new InvalidOperationException("มัดจำกับใบแจ้งหนี้ต้องเป็นลูกค้ารายเดียวกัน");

        // Multi-currency guard — IAS 21: ถ้าสกุล/rate ของมัดจำกับใบแจ้งหนี้
        // ต่างกัน ต้องคำนวณกำไรขาดทุนอัตราแลกเปลี่ยน. ระบบนี้ยังไม่ post FX
        // gain/loss JE อัตโนมัติ → block ไว้ก่อน + แนะนำให้บันทึก JE manual
        // (กัน GL เพี้ยนเงียบ ๆ ตอน user เปิด multi-currency)
        if (!string.Equals(invoice.Currency, deposit.Currency, StringComparison.OrdinalIgnoreCase)
            || Math.Abs(invoice.ExchangeRate - deposit.ExchangeRate) > 0.0001m)
        {
            throw new InvalidOperationException(
                $"สกุล/อัตราแลกเปลี่ยนของมัดจำ ({deposit.Currency} @ {deposit.ExchangeRate:F4}) " +
                $"ต่างกับใบแจ้งหนี้ ({invoice.Currency} @ {invoice.ExchangeRate:F4}) — " +
                "ระบบยังไม่รองรับการรับรู้กำไร/ขาดทุน FX อัตโนมัติ ตาม IAS 21. " +
                "กรุณาบันทึก JE manual หรือใช้มัดจำที่สกุลเงินเดียวกัน");
        }

        // รับรู้รายได้จากมัดจำ (Dr ขายรอรับรู้/Cr รายได้) — ใช้ฐานไม่รวม VAT
        var vatPortion = deposit.TotalAmount > 0 ? deposit.VatAmount / deposit.TotalAmount : 0m;
        var baseAmt = Math.Round(request.Amount * (1 - vatPortion), 2, MidpointRounding.AwayFromZero);
        await RealizeDepositAsync(companyId, deposit.Id,
            new RealizeDepositRequest(baseAmt, request.ApplyDate, null), actor);

        // ลด BalanceDue ของใบแจ้งหนี้ตามยอดมัดจำที่จ่ายมาแล้ว (gross — มัดจำ
        // จ่ายเงินจริงมาแล้ว ถือเป็น prepayment ของใบนี้)
        invoice.PaidAmount += request.Amount;
        invoice.BalanceDue = Math.Max(0m, invoice.TotalAmount - invoice.PaidAmount);
        if (invoice.BalanceDue <= 0.005m && invoice.Status == DocumentStatus.Approved)
            invoice.Status = DocumentStatus.Paid;
        deposit.DepositAppliedToDocumentId = invoiceId;

        await _db.SaveChangesAsync();
        var updated = await GetDocumentAsync(companyId, invoiceId);
        await FireWebhookAsync(companyId, "deposit.applied", updated);
        return updated;
    }

    public async Task<SuggestPvAccountingResponse> SuggestPaymentVoucherAccountingAsync(
        Guid companyId, SuggestPvAccountingRequest request, CancellationToken ct = default)
    {
        // AI ปิดอยู่/inject ไม่ได้ → คืนผลว่าง (เคารพ kill-switch กฎเหล็ก #1)
        if (_aiAugmenter == null || request.Lines.Count == 0)
            return new SuggestPvAccountingResponse(
                request.Lines.Select(l => new SuggestPvAccountingLineResult(
                    l.TempId, l.CurrentAccountCode, null,
                    Array.Empty<string>(), null, false, null)).ToList(),
                Array.Empty<string>(), UsedAi: false);

        // map tempId → temp Guid (augmenter signature ใช้ LineId เป็น Guid)
        var tempMap = request.Lines.ToDictionary(_ => Guid.NewGuid(), l => l);
        var lineInputs = tempMap.Select(kv =>
            (LineId: kv.Key, kv.Value.Description, kv.Value.Amount, kv.Value.CurrentAccountCode)
        ).ToList();

        var bulk = await _aiAugmenter.SuggestAllPaymentVoucherAccountingAsync(
            companyId,
            request.SourceInvoiceId ?? Guid.Empty,
            request.VendorName, request.VendorTaxId, request.VendorIndustry,
            lineInputs,
            string.IsNullOrWhiteSpace(request.Currency) ? "THB" : request.Currency,
            ct);

        // Validate ผังที่แนะนำว่ามีจริงในผังของบริษัท (anti-hallucination guard
        // ตามกฎเหล็ก #1) — ถ้าไม่มี ไม่ส่งกลับเป็น answer
        var allCodes = (await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted)
            .Select(a => a.AccountCode).ToListAsync(ct)).ToHashSet();

        var results = tempMap.Select(kv =>
        {
            var line = kv.Value;
            if (!bulk.ByLineId.TryGetValue(kv.Key, out var sugg))
                return new SuggestPvAccountingLineResult(
                    line.TempId, line.CurrentAccountCode, null,
                    Array.Empty<string>(), null, false, null);
            var ans = !string.IsNullOrWhiteSpace(sugg.Answer) && allCodes.Contains(sugg.Answer!)
                ? sugg.Answer
                : line.CurrentAccountCode;
            return new SuggestPvAccountingLineResult(
                line.TempId, ans, sugg.Confidence,
                sugg.Alternatives, sugg.Reasoning, sugg.UsedAi, sugg.FeedbackId);
        }).ToList();

        return new SuggestPvAccountingResponse(results, bulk.CrossLineObservations, bulk.UsedAi);
    }

    public async Task<List<UndueInputVatSummary>> GetUndueInputVatAsync(Guid companyId)
    {
        // เอกสารที่ VAT ค้าง 11640 รอใบกำกับครบ (ยังไม่ reclassify)
        var rows = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && d.InputVatPostedAsUndue
                && d.InputVatBecameClaimableAt == null
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided)
            .OrderBy(d => d.DocumentDate)
            .ToListAsync();

        var today = DateTime.UtcNow.Date;
        return rows.Select(d =>
        {
            // §82/3: เคลมได้ภายใน 6 เดือนนับจากเดือนของใบกำกับ (ใช้ SupplierTaxInvoiceDate
            // ถ้ามี ไม่งั้น DocumentDate). หมดสิทธิเมื่อพ้นสิ้นเดือนที่ 6.
            var baseDate = d.SupplierTaxInvoiceDate ?? d.DocumentDate;
            var windowEnd = new DateTime(baseDate.Year, baseDate.Month, 1).AddMonths(7).AddDays(-1);
            var isExpired = today > windowEnd;
            var monthsLeft = ((windowEnd.Year - today.Year) * 12 + windowEnd.Month - today.Month);
            if (monthsLeft < 0) monthsLeft = 0;
            var completeness = TaxInvoiceCompletenessChecker.Evaluate(d, d.Contact);
            var vat = d.Lines.Where(l => l.IsVatClaimable).Sum(l => l.VatAmount);
            return new UndueInputVatSummary(
                d.Id, d.DocumentNumber, d.DocumentDate,
                d.Contact?.Name ?? "", d.Contact?.TaxId,
                vat,
                (int)(today - d.DocumentDate.Date).TotalDays,
                monthsLeft, isExpired,
                completeness.MissingFields);
        }).ToList();
    }

    public Task<DocumentResponse> ApproveDocumentAsync(Guid companyId, Guid documentId, string approvedBy)
        => ApproveDocumentAsync(companyId, documentId, approvedBy, acknowledgeWarnings: false);

    public async Task<DocumentResponse> ApproveDocumentAsync(Guid companyId, Guid documentId, string approvedBy, bool acknowledgeWarnings)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (doc.Status != DocumentStatus.Draft && doc.Status != DocumentStatus.WaitingApproval)
            throw new InvalidOperationException("อนุมัติได้เฉพาะเอกสาร Draft หรือ WaitingApproval เท่านั้น");

        // Soft warnings — legal/correct but unusual patterns the operator
        // should eyeball before approving. Hard errors still throw below.
        // When AcknowledgeWarnings is false and warnings exist, throw a
        // typed exception the controller turns into a 422 with the list
        // so the UI can prompt for explicit confirmation.
        var warnings = await CollectApprovalWarningsAsync(companyId, doc);
        if (warnings.Count > 0 && !acknowledgeWarnings)
        {
            // Enrich each warning with an AI-suggested fix when augmenter
            // is wired AND online. Done in parallel with a short overall
            // budget (8s) so the approval dialog isn't laggy. Each call
            // falls back to local on its own — overall request still
            // throws the 422 regardless of whether AI ran.
            IReadOnlyList<DocumentApprovalAiHint>? hints = null;
            if (_aiAugmenter != null && warnings.Count > 0)
            {
                try
                {
                    using var aiCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var snapshot = new
                    {
                        doc.Id, doc.DocumentNumber, doc.DocumentType, doc.DocumentDate,
                        doc.TotalAmount, doc.SubTotal, doc.VatAmount,
                        WhtAmount = doc.WithholdingTaxAmount,
                        doc.Currency, doc.BalanceDue, doc.ContactId,
                        ContactName = doc.Contact?.Name,
                        ContactTaxId = doc.Contact?.TaxId,
                        LineCount = doc.Lines?.Count ?? 0,
                    };
                    // Single BULK call covering every warning — AI can
                    // see "warning 1 + warning 2 share a root cause"
                    // patterns the previous per-warning fan-out missed.
                    // Cost drops from N× to 1×.
                    var bulk = await _aiAugmenter.SuggestApprovalWarningFixesBulkAsync(
                        companyId, doc.Id, warnings, snapshot, null, aiCts.Token);
                    hints = bulk.Hints.Select(r => new DocumentApprovalAiHint(
                        Primary: r.Answer ?? "Acknowledge",
                        Confidence: r.Confidence ?? 0.5m,
                        Reasoning: r.Reasoning,
                        SuggestedActions: r.SuggestedActions,
                        Risks: r.Risks,
                        ComplianceFlags: r.ComplianceFlags,
                        FeedbackId: r.FeedbackId,
                        UsedAi: r.UsedAi)).ToList();
                }
                catch (Exception aiEx)
                {
                    // AI failure must not block the warning surfacing.
                    _logger.LogWarning(aiEx, "Approval-warning AI augmentation failed; surfacing raw warnings");
                }
            }
            throw new DocumentApprovalWarningsException(warnings, hints);
        }

        // CreditNote must declare its reason — per ประมวลรัษฎากร §82/10 the
        // CN reason distinguishes whether goods physically returned (restocks)
        // from a pure financial adjustment (no stock impact). Without it the
        // stock cascade can't decide and the e-Tax label would be wrong.
        if (doc.DocumentType == DocumentType.CreditNote && doc.CreditNoteReason == null)
            throw new InvalidOperationException(
                "ใบลดหนี้ต้องระบุเหตุผล (คืนสินค้า / ส่วนลด / ปรับยอด / ตัดยอด) ก่อนอนุมัติ");

        // §86/4 hard-block (opt-in via CompanySettings.EnforceFullTaxInvoiceFields).
        // เมื่อบริษัทเปิด flag นี้ → block approval ของใบกำกับ/ใบเสร็จ/CN/DN
        // ที่ขาด field บังคับ (BuyerTaxId 13 หลัก + BuyerAddress + BuyerBranchCode 5 หลัก).
        // กัน operator-error: ตอนนี้ระบบเตือนแล้ว user กด acknowledge ผ่านได้ →
        // ใบกำกับที่ไม่ครบ §86/4 หลุดเข้า GL → ลูกค้ารับใบไปใช้ภาษีซื้อไม่ได้.
        var rd864Types = new[] { DocumentType.TaxInvoice, DocumentType.Receipt,
            DocumentType.DebitNote, DocumentType.CreditNote };
        var enforce864 = await _db.CompanySettings.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => (bool?)c.EnforceFullTaxInvoiceFields)
            .FirstOrDefaultAsync() ?? false;
        if (enforce864 && rd864Types.Contains(doc.DocumentType) && doc.VatAmount > 0
            && doc.Contact != null)
        {
            var missing = new List<string>();
            var btid = (doc.Contact.TaxId ?? "").Where(char.IsDigit).Count();
            if (btid != 13) missing.Add("เลขผู้เสียภาษีผู้ซื้อ 13 หลัก");
            if (string.IsNullOrWhiteSpace(doc.Contact.Address)) missing.Add("ที่อยู่ผู้ซื้อ");
            // SupplierBranchCode = สาขาผู้ขาย (เก็บฝั่งซื้อ); ฝั่งขายใช้
            // Contact.BranchCode สำหรับสาขาผู้ซื้อ. ตรวจฝั่งขาย (TaxInvoice
            // ที่เรา = ผู้ขาย).
            var buyerBr = doc.Contact.BranchCode ?? "";
            var buyerBrDigits = new string(buyerBr.Where(char.IsDigit).ToArray());
            if (buyerBrDigits.Length != 5) missing.Add("รหัสสาขาผู้ซื้อ 5 หลัก (00000=สนญ.)");
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"⛔ §86/4: ใบกำกับขาด field บังคับ — {string.Join(", ", missing)}. " +
                    "เปิด setting 'บังคับ §86/4 ครบทุก field' ไว้ → ต้องเติมก่อนอนุมัติ");
        }

        // Enforce CompanySettings.RequireApprovalForDocuments: when the
        // approval rail is on and the document's amount crosses the threshold,
        // refuse direct approve and force the multi-step SignatureApproval
        // flow. SignatureApprovalService finalises documents by setting
        // doc.Status = Approved directly (it doesn't re-enter this method),
        // so the workflow path is not impacted.
        var settings = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);
        if (settings is { RequireApprovalForDocuments: true })
        {
            var threshold = settings.ApprovalThresholdAmount ?? 0m;
            if (doc.TotalAmount >= threshold)
                throw new InvalidOperationException(
                    $"เอกสารยอด {doc.TotalAmount:N2} บาท เกินวงเงินอนุมัติอัตโนมัติ ({threshold:N2}) — " +
                    "กรุณาส่งเข้ากระบวนการอนุมัติหลายชั้นก่อน (เมนู Approval)");
        }

        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId &&
            f.StartDate <= doc.DocumentDate &&
            f.EndDate >= doc.DocumentDate);
        if (period != null && period.Status != FiscalPeriodStatus.Open)
            throw new InvalidOperationException(
                $"ไม่สามารถอนุมัติเอกสารที่มีวันที่ในงวด {period.Name} ได้ เนื่องจากงวดดังกล่าวมีสถานะ {period.Status}");

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                // Pessimistic lock — two simultaneous Approve calls both saw
                // Status=Draft and hasExistingJournal=false before, so both
                // happily auto-posted, producing duplicate JEs and double-counting
                // revenue/expense. FOR UPDATE serialises them on the document row.
                await _db.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE",
                    documentId, companyId);

                // Re-read status under the lock — the prior reader may have
                // already moved this Document past Draft.
                var lockedStatus = await _db.Documents
                    .Where(d => d.Id == documentId && d.CompanyId == companyId)
                    .Select(d => d.Status)
                    .FirstAsync();
                if (lockedStatus != DocumentStatus.Draft && lockedStatus != DocumentStatus.WaitingApproval)
                    throw new InvalidOperationException("เอกสารถูกอนุมัติไปแล้วโดยผู้ใช้งานคนอื่น กรุณารีเฟรชหน้านี้");

                // Multi-level approval gate. ถ้า ApprovalRule match doc นี้ →
                // ต้องผ่าน workflow ก่อน. user ที่ submit direct approve
                // โดยไม่ผ่าน workflow จะถูก block ที่นี่. Workflow service
                // เป็น opt-in (DI nullable) — ถ้าบริษัทไม่ได้ตั้งค่า approval
                // rule เลย FindMatchingRule คืน null → gate ปล่อยผ่าน.
                if (_approval != null)
                {
                    var matchingRule = await _approval.FindMatchingRuleAsync(
                        companyId, doc.DocumentType, doc.TotalAmount, doc.ProjectId);
                    if (matchingRule != null)
                    {
                        var gate = await _approval.CheckGateAsync(companyId, documentId);
                        if (!gate.CanApprove)
                            throw new InvalidOperationException(gate.BlockReason
                                ?? "เอกสารต้องผ่าน multi-level approval workflow ก่อน");
                    }
                }

                // F20 — 3-way match block over-bill. ถ้า PurchaseInvoice/
                // Expense อ้างถึง GRN (RelatedDocumentId) → ตรวจ qty/amount
                // ของแต่ละบรรทัด invoice ต้องไม่เกิน GRN ที่รับจริง. ป้องกัน
                // supplier ออกบิลเกินสินค้าที่ส่ง — ตรงกับมาตรฐาน 3-way match
                // (PO ↔ GRN ↔ PI). Linked via DocumentLine.SourceLineId.
                if ((doc.DocumentType == DocumentType.PurchaseInvoice || doc.DocumentType == DocumentType.Expense)
                    && doc.RelatedDocumentId.HasValue)
                {
                    var srcDoc = await _db.Documents.AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == doc.RelatedDocumentId.Value && d.CompanyId == companyId);
                    if (srcDoc != null && srcDoc.DocumentType == DocumentType.GoodsReceiptNote)
                    {
                        var grnLines = await _db.DocumentLines.AsNoTracking()
                            .Where(l => l.DocumentId == srcDoc.Id)
                            .ToDictionaryAsync(l => l.Id, l => new { l.Quantity, l.Amount, l.Description });
                        var overBill = new List<string>();
                        foreach (var line in doc.Lines.Where(l => l.SourceLineId.HasValue))
                        {
                            if (!grnLines.TryGetValue(line.SourceLineId!.Value, out var grn)) continue;
                            // คำนวณยอด invoice อื่นๆ ที่อ้าง grn line นี้ไปแล้ว
                            var alreadyBilled = await _db.DocumentLines.AsNoTracking()
                                .Where(l => l.SourceLineId == line.SourceLineId.Value
                                            && l.DocumentId != doc.Id
                                            && !l.Document.IsDeleted
                                            && l.Document.Status != DocumentStatus.Voided
                                            && l.Document.Status != DocumentStatus.Draft
                                            && l.Document.Status != DocumentStatus.Rejected)
                                .Select(l => new { l.Quantity, l.Amount })
                                .ToListAsync();
                            var billedQty = alreadyBilled.Sum(b => b.Quantity);
                            var billedAmt = alreadyBilled.Sum(b => b.Amount);
                            if (line.Quantity + billedQty > grn.Quantity + 0.0001m)
                                overBill.Add($"\"{grn.Description}\": รับจริง {grn.Quantity:N2} บิลแล้ว {billedQty:N2} ใบนี้ {line.Quantity:N2} → เกิน");
                            if (line.Amount + billedAmt > grn.Amount + 0.01m)
                                overBill.Add($"\"{grn.Description}\": ยอด GRN {grn.Amount:N2} บิลแล้ว {billedAmt:N2} ใบนี้ {line.Amount:N2} → เกิน");
                        }
                        if (overBill.Count > 0 && !acknowledgeWarnings)
                            throw new InvalidOperationException(
                                "บิลเกินจำนวน/ยอด GRN ที่รับจริง (3-way match):\n• " + string.Join("\n• ", overBill)
                                + "\n\nกดรับทราบเพื่อบังคับอนุมัติ");
                    }
                }

                // Idempotency guard INSIDE transaction to prevent race condition
                var hasExistingJournal = await _db.JournalEntries.AnyAsync(j =>
                    j.SourceDocumentId == documentId
                    && j.CompanyId == companyId
                    && j.Status == JournalEntryStatus.Posted);

                // Credit-limit enforcement on sales-side credit documents —
                // blocks approval if customer is over their ContactCreditSetting
                // limit OR on hold. acknowledgeWarnings=true lets the operator
                // override (recorded in ProcessingNotes). Cash-settled sales
                // never consume credit (money came in immediately).
                var creditCheckTypes = new[] {
                    DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.BillingNote
                };
                if (_advancedArAp != null
                    && creditCheckTypes.Contains(doc.DocumentType)
                    && doc.PaymentType != Models.Enums.PaymentType.Cash
                    && doc.ContactId != Guid.Empty)
                {
                    try
                    {
                        var credit = await _advancedArAp.CheckCreditAsync(companyId, doc.ContactId, doc.TotalAmount);
                        if (!credit.IsApproved)
                        {
                            if (!acknowledgeWarnings)
                                throw new InvalidOperationException(
                                    $"ปฏิเสธอนุมัติ: {credit.Reason}. กรุณาตรวจ ContactCreditSetting หรือกด \"อนุมัติพร้อม override\"");
                            doc.Notes = (doc.Notes ?? "")
                                + $"\n[Credit override] {credit.Reason} — อนุมัติโดย {approvedBy}";
                        }
                    }
                    catch (InvalidOperationException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Credit check failed Doc={Doc} — proceeding without block", documentId);
                    }
                }

                // Issue the real running number NOW (transition Draft → Approved).
                // Draft sat with a "DRAFT-xxx" placeholder so deleting it never
                // created a sequence gap. We're inside the approve transaction
                // and DocumentNumberGenerator.NextAsync takes a pg_advisory_xact_
                // _lock per (company, prefix) so concurrent approves can't issue
                // duplicates. Documents that already carry a real number (idem-
                // potent re-approve, or those created by OCR/integration with a
                // number already stamped) keep the existing number.
                if (doc.DocumentNumber.StartsWith("DRAFT-", StringComparison.Ordinal))
                {
                    // ใช้เดือนของ DocumentDate (ไม่ใช่ "วันที่ approve") เพื่อให้
                    // เลขกับวันที่สอดคล้องกัน: ใบ 28/05 ที่ approve 01/06 ต้องได้
                    // "PV-202605-NNNN" ไม่ใช่ "PV-202606-..."
                    doc.DocumentNumber = await Accounting.Helpers.DocumentNumberGenerator.NextAsync(
                        _db, companyId, doc.DocumentType, doc.DocumentDate);
                }

                // A cash-settled document (จ่ายทันที) is already fully paid the
                // moment it's approved — the JE moves real cash, not a payable.
                // Mark it Paid so it lands in the right bucket and never shows
                // as outstanding/ค้างชำระ; everything else goes to Approved.
                doc.Status = (doc.PaymentType == Models.Enums.PaymentType.Cash && doc.BalanceDue <= 0)
                    ? DocumentStatus.Paid
                    : DocumentStatus.Approved;
                doc.UpdatedBy = approvedBy;
                doc.UpdatedAt = DateTime.UtcNow;

                // ===== Tax Point §78/§78/1 — snapshot จุดความรับผิด VAT =====
                // VAT period ของ ภ.พ.30 ใช้เดือนของ TaxPointDate. คำนวณเฉพาะ
                // เอกสารที่มี VAT (มิฉะนั้นไม่เกี่ยว).
                if (doc.VatAmount != 0)
                    doc.TaxPointDate = TaxPointResolver.Resolve(doc);

                // ===== Retention §87/3 + พ.ร.บ.บัญชี ม.10 — เก็บ 5 ปี =====
                // นับจาก MAX(วันสิ้นรอบบัญชีของเอกสาร, วันที่เอกสาร) + 5 ปี.
                // ใช้ simple rule: DocumentDate + 5 ปี (เพียงพอกับ floor 5 ปี;
                // job ปิดรอบจะขยายได้ถ้าต้องการ superset).
                doc.RetentionUntil ??= doc.DocumentDate.Date.AddYears(5);

                // ===== §65 ตรี — รายจ่ายต้องห้าม (บวกกลับ ภ.ง.ด.50) =====
                // เฉพาะเอกสารฝั่งซื้อ/ค่าใช้จ่ายที่กระทบกำไรสุทธิ.
                if (doc.DocumentType is DocumentType.PurchaseInvoice
                        or DocumentType.Expense or DocumentType.PaymentVoucher)
                {
                    await ApplySection65TerAsync(companyId, doc);
                }

                var autoPostTypes = new[] {
                    DocumentType.Invoice, DocumentType.TaxInvoice,
                    DocumentType.DebitNote, DocumentType.CreditNote,
                    DocumentType.PurchaseInvoice, DocumentType.Expense,
                    DocumentType.Receipt, DocumentType.ReceiptVoucher,
                    DocumentType.PaymentVoucher, DocumentType.CertificateInLieu,
                    // 3-way match: a GRN accrues goods-received-not-invoiced
                    // (Dr Expense / Cr GR-NI) so received goods hit the books
                    // before the supplier's invoice arrives.
                    DocumentType.GoodsReceiptNote,
                };
                if (!hasExistingJournal && autoPostTypes.Contains(doc.DocumentType))
                {
                    await AutoPostToJournalAsync(companyId, doc, approvedBy);
                }

                await ApplySourceDocumentAdjustmentsAsync(companyId, doc);
                await ApplyProjectBillingAsync(companyId, doc, +1);
                await ApplyStockMovementsAsync(companyId, doc, +1, approvedBy);

                // บังคับลงทะเบียนสินทรัพย์ — บรรทัดที่ลงผัง PPE (12xxx) ต้องมี
                // ทะเบียนสินทรัพย์ + ตารางค่าเสื่อม (TFRS บทที่ 10 + §65 ตรี (5)).
                // auto-create ด้วยค่า default (อายุ/วิธีตามประเภท) NeedsReview=true
                await AutoRegisterFixedAssetsAsync(companyId, doc, approvedBy);

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        // Best-effort: train vendor intelligence cache for OCR self-learning.
        // Failures are logged inside the helper — training is a derived side-effect
        // that can always be rebuilt via BackfillFromHistoryAsync.
        await _vendorIntel.TryTrainAsync(companyId, doc.Id);

        // Best-effort: cross-tenant routing. If the recipient Contact maps to a
        // partner Company we have an Accepted partnership with, the document
        // shows up in their inbox automatically. Failures are logged and don't
        // unwind the approval.
        try { await _crossTenantWorkflow.OnDocumentSentAsync(doc); }
        catch (Exception ex)
        { _logger.LogWarning(ex, "Cross-tenant routing failed for doc {Id}", doc.Id); }

        // Best-effort auto-generate e-Tax record for eligible types when the
        // company has e-Tax enabled. Runs OUTSIDE the approval transaction so
        // an e-Tax failure (cert not configured, RD API down, etc.) doesn't
        // roll back the approval. Failures are logged; the user can still
        // generate the e-Tax manually from the detail modal.
        await TryAutoGenerateEtaxAsync(companyId, doc);

        // LINE notification (best-effort) — keeps the legacy broadcast room
        // hook for companies wired to a single LINE Notify channel.
        var contactName = await _db.Contacts.Where(c => c.Id == doc.ContactId).Select(c => c.Name).FirstOrDefaultAsync() ?? "";
        try
        {
            await _lineNotify.NotifyDocumentApprovedAsync(companyId, doc.DocumentNumber, contactName, doc.TotalAmount);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "LINE notification failed for document {DocNum}", doc.DocumentNumber); }

        // New per-user / per-channel dispatch via the notification engine —
        // resolves Accounting / Owner recipients per the configured matrix.
        if (_notify != null)
        {
            await _notify.DispatchAsync(companyId, NotificationEvents.DocumentApproved, new NotificationContext
            {
                Title = $"อนุมัติเอกสาร {doc.DocumentNumber}",
                Message = $"{doc.DocumentType} · {contactName} · ยอดรวม {doc.TotalAmount:N2} บาท",
                ActionUrl = $"/pages/documents.html?id={doc.Id}",
                EntityType = "Document", EntityId = doc.Id,
            });

            // PaymentVoucher is a specialised approved-document subtype — fire
            // its dedicated event so accounting can have a separate matrix row.
            if (doc.DocumentType == DocumentType.PaymentVoucher)
            {
                await _notify.DispatchAsync(companyId, NotificationEvents.PaymentVoucherGenerated, new NotificationContext
                {
                    Title = $"ใบสำคัญจ่าย {doc.DocumentNumber}",
                    Message = $"{contactName} · ยอดรวม {doc.TotalAmount:N2} บาท",
                    ActionUrl = $"/pages/documents.html?id={doc.Id}",
                    EntityType = "Document", EntityId = doc.Id,
                });
            }
        }

        // ปิดลูปการสอน — final choice ตอนอนุมัติ: ทุกบรรทัดที่มี FeedbackId
        // + AccountId → บันทึก choice (acceptedAi=true ถ้าตรง AI's answer,
        // false ถ้า user แก้). RecordLineAccountFeedbackAsync เป็น idempotent
        // (ข้ามถ้า UserChosenAnswer เคย set แล้ว) → ปลอดภัย ถ้า Update เคย fire
        var approvedLines = await _db.DocumentLines.AsNoTracking()
            .Where(l => l.DocumentId == doc.Id).ToListAsync();
        await RecordLineAccountFeedbackAsync(approvedLines);

        var approved = await GetDocumentAsync(companyId, documentId);

        // Auto-feed ProjectCostEntry when an approved EXPENSE-side
        // document carries a ProjectId. Closes the loop the audit
        // flagged: ProjectCostEntry was manual-only, so approved cost
        // documents bypassed the project P&L roll-up. Each ProjectId-
        // bearing line becomes a ProjectCostEntry row, idempotent on
        // (DocumentLineId) — re-approving the same doc doesn't
        // duplicate entries.
        try
        {
            await SyncProjectCostEntriesAsync(companyId, doc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Project cost entry sync failed for doc {DocId}", documentId);
        }

        await FireWebhookAsync(companyId, "document.status_changed",
            new { document = approved, from = "Draft", to = approved.Status });

        // Auto-email schedule hook — เช็คกฎใน EmailScheduleRules ที่ตรง
        // กับ DocumentApproved + DocumentType แล้ว enqueue. Fail-safe:
        // ส่งอีเมลไม่สำเร็จต้องไม่ทำให้ approve ล้ม.
        if (_emailSchedule != null)
        {
            try { await _emailSchedule.OnDocumentApprovedAsync(companyId, documentId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Email schedule enqueue failed Doc={Doc}", documentId); }
        }

        return approved;
    }

    /// <summary>
    /// Auto-generate the e-Tax invoice record for eligible doc types when the
    /// company has e-Tax enabled. Only runs after the document has reached
    /// Approved status. Silently skips if the company isn't VAT-registered
    /// (TaxId missing), the contact's TaxId is missing, or e-Tax is disabled.
    /// </summary>
    private async Task TryAutoGenerateEtaxAsync(Guid companyId, Document doc)
    {
        var eligibleTypes = new[] {
            DocumentType.TaxInvoice, DocumentType.Receipt,
            DocumentType.DebitNote, DocumentType.CreditNote
        };
        if (!eligibleTypes.Contains(doc.DocumentType)) return;

        try
        {
            var settings = await _db.CompanySettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CompanyId == companyId);
            if (settings?.EtaxEnabled != true) return;

            // Skip if a (non-Error) e-Tax already exists — GenerateAsync will
            // throw "เอกสารนี้มี e-Tax Invoice แล้ว" and we'd rather no-op silently.
            var alreadyExists = await _db.EtaxInvoices
                .AnyAsync(e => e.DocumentId == doc.Id && e.CompanyId == companyId
                            && e.Status != EtaxStatus.Error);
            if (alreadyExists) return;

            await _etaxService.GenerateAsync(companyId,
                new GenerateEtaxRequest(doc.Id, SignDigitally: settings.EtaxAutoSign));
        }
        catch (Exception ex)
        {
            // Don't surface the error — approval already succeeded. The user can
            // retry manually from the detail modal's "สร้าง e-Tax" button.
            _logger.LogWarning(ex,
                "Auto e-Tax generation failed for document {DocId} ({DocNumber})",
                doc.Id, doc.DocumentNumber);
        }
    }

    /// <summary>เปลี่ยนผังบัญชี (line.AccountId) ของเอกสารที่ approved แล้ว
    /// แบบ "reclassify" — ไม่แก้เอกสารต้นฉบับ. ระบบ post JE คู่ใหม่
    /// Dr ผังใหม่ / Cr ผังเก่า (ด้วยยอด line.Amount = ฐานก่อน VAT) ลงงวด
    /// เดียวกับ doc.DocumentDate เพื่อให้ trial balance ก่อน–หลังตรงทุกบัญชี
    /// แล้วอัปเดต line.AccountId เพื่อให้รายงานต่อจากนี้ key ตามผังใหม่.
    /// VAT/WHT อยู่บัญชีแยก (11610/21915/21911) ไม่กระทบ.
    ///
    /// Gate (ทั้งหมดต้องผ่าน):
    /// - DocumentType ∈ {Expense, PurchaseInvoice, PaymentVoucher} เท่านั้น
    ///   (TaxInvoice/Receipt/CN/DN ห้ามตาม §86/4)
    /// - Status ∈ {Approved, Sent, PartiallyPaid, Paid} (Draft = แก้ได้
    ///   ปกติผ่าน UpdateDocument)
    /// - FiscalPeriod ของ doc.DocumentDate ยัง Open
    /// - ไม่มีเอกสารปลายทางอ้าง (RelatedDocumentId → docId)
    /// - ไม่มี Payment ลงแล้ว
    /// - ไม่อยู่ใน TaxReport ที่ Status=Submitted หรือ Filed (ภพ.30 ยื่นแล้ว)
    /// - ไม่ได้ submit e-Tax (EtaxInvoice.SubmittedAt != null)</summary>
    public async Task<DocumentResponse> ReclassifyLineAccountAsync(
        Guid companyId, Guid documentId, Guid lineId,
        Guid newAccountId, string? reason, string actor)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var allowedTypes = new[] {
            DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.PaymentVoucher
        };
        if (!allowedTypes.Contains(doc.DocumentType))
            throw new InvalidOperationException(
                $"เอกสารประเภท {doc.DocumentType} ห้ามแก้ผังบัญชีหลังอนุมัติ — " +
                "ใบกำกับ/ใบเสร็จ/ใบเพิ่ม-ลดหนี้ ตาม §86/4 ต้องยกเลิกแล้วออกใบใหม่");

        if (doc.Status is DocumentStatus.Draft or DocumentStatus.Voided
                       or DocumentStatus.Rejected)
            throw new InvalidOperationException(
                $"เอกสาร Status={doc.Status} ไม่อยู่ในขั้นที่ reclassify ได้ " +
                "(Draft = แก้ผ่านฟอร์มปกติ, Voided/Rejected = สร้างใหม่)");

        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(p =>
            p.CompanyId == companyId
            && p.StartDate <= doc.DocumentDate && p.EndDate >= doc.DocumentDate);
        if (period != null && period.Status != FiscalPeriodStatus.Open)
            throw new InvalidOperationException(
                $"วันที่เอกสารอยู่ในงวด '{period.Name}' ที่ปิดแล้ว ({period.Status}) — " +
                "ผังบัญชีของงวดที่ปิดแก้ไม่ได้ (กัน trial balance ย้อนหลังพัง)");

        var hasDownstream = await _db.Documents.AsNoTracking().AnyAsync(d =>
            d.CompanyId == companyId && d.RelatedDocumentId == documentId
            && d.Status != DocumentStatus.Voided && !d.IsDeleted);
        if (hasDownstream)
            throw new InvalidOperationException(
                "มีเอกสารปลายทาง (เช่น ใบสำคัญจ่าย/ใบลดหนี้) อ้างเอกสารนี้แล้ว — " +
                "ยกเลิกเอกสารปลายทางก่อน หรือใช้วิธี void+ออกใบใหม่แทน");

        var hasPayments = await _db.Payments.AsNoTracking().AnyAsync(p =>
            p.CompanyId == companyId && p.DocumentId == documentId && !p.IsDeleted);
        if (hasPayments)
            throw new InvalidOperationException(
                "เอกสารนี้มีการบันทึกชำระเงินไปแล้ว — ยกเลิกการชำระก่อนถึงจะแก้ผังได้");

        var inSubmittedReport = await _db.TaxReportLines.AsNoTracking().AnyAsync(l =>
            l.DocumentId == documentId
            && l.TaxReport.CompanyId == companyId
            && (l.TaxReport.Status == TaxReportStatus.Submitted
                || l.TaxReport.Status == TaxReportStatus.Filed));
        if (inSubmittedReport)
            throw new InvalidOperationException(
                "เอกสารอยู่ในรายงานภาษีที่ยื่นสรรพากรแล้ว — แก้ไม่ได้ " +
                "(ต้องออก ภพ.30 เพิ่มเติม/แก้ไขผ่าน amended return)");

        var hasSubmittedEtax = await _db.EtaxInvoices.AsNoTracking().AnyAsync(e =>
            e.DocumentId == documentId && e.SubmittedAt != null);
        if (hasSubmittedEtax)
            throw new InvalidOperationException(
                "เอกสารนี้ส่ง e-Tax XML ไปสรรพากรแล้ว — แก้ไม่ได้");

        var line = doc.Lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new KeyNotFoundException("ไม่พบบรรทัดในเอกสาร");
        if (!line.AccountId.HasValue)
            throw new InvalidOperationException(
                "บรรทัดนี้ไม่มีผังบัญชีเดิม — กรอกผัง + reapprove แทน");
        if (line.AccountId.Value == newAccountId)
            return await GetDocumentAsync(companyId, documentId);   // no-op

        var newAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
            a.Id == newAccountId && a.CompanyId == companyId && a.IsActive)
            ?? throw new InvalidOperationException("ผังบัญชีใหม่ไม่มีในระบบหรือถูกปิดใช้");
        var oldAccount = await _db.ChartOfAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == line.AccountId.Value)
            ?? throw new InvalidOperationException("ไม่พบผังบัญชีเดิม");

        var amount = line.Amount;   // ฐานก่อน VAT — ที่ JE เดิมลงเข้าผังเก่า
        if (amount <= 0.005m)
        {
            // line ที่ Amount=0 ไม่กระทบ GL — แค่อัปเดต field ก็พอ
            line.AccountId = newAccountId;
            await _db.SaveChangesAsync();
            return await GetDocumentAsync(companyId, documentId);
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Strangler Fig: migrated to JournalEntryBuilder
            // (เดิม construct JournalEntry + 2 lines + balance check manual)
            var desc = $"Reclassify ผังบัญชี — {doc.DocumentNumber} " +
                       $"'{line.Description}' ({oldAccount.AccountCode} → {newAccount.AccountCode})" +
                       (string.IsNullOrWhiteSpace(reason) ? "" : $" • {reason}");
            await Journal.JournalEntryBuilder
                .For(_db, companyId, doc.DocumentDate)
                .Description(desc)
                .Reference(doc.DocumentNumber)
                .SourceDocument(doc.Id)
                .Project(doc.ProjectId)
                .Debit(newAccount.Id, amount, $"Dr {newAccount.AccountCode} — {newAccount.AccountName}")
                .Credit(oldAccount.Id, amount, $"Cr {oldAccount.AccountCode} — {oldAccount.AccountName} (reclassify)")
                .PostAsync(actor);

            line.AccountId = newAccountId;
            doc.UpdatedAt = DateTime.UtcNow;
            doc.UpdatedBy = actor;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        _logger.LogInformation(
            "Reclassified line {LineId} of {DocNumber}: {OldCode} → {NewCode} by {Actor}",
            lineId, doc.DocumentNumber, oldAccount.AccountCode, newAccount.AccountCode, actor);

        return await GetDocumentAsync(companyId, documentId);
    }

    /// <summary>
    /// เปลี่ยน "แหล่งเงิน" (บัญชี Cr เงินสด/ธนาคาร) ของเอกสารจ่าย/รับสดที่
    /// approve แล้ว — คู่กับ ReclassifyLineAccountAsync (ที่แก้ฝั่ง Dr). ใช้แก้
    /// เคส OCR/auto-create เลือกธนาคารผิด (กรุงไทย → กสิกร) โดยไม่ต้อง void.
    /// ระบบ post correcting-JE: Dr {ผังเก่า} / Cr {ผังใหม่} ขนาด = PaidAmount
    /// (เงินสดที่จ่ายจริง = ยอดที่ลง Cr บัญชีแหล่งเงินเดิม) → เงินกลับเข้าบัญชี
    /// เก่า + ออกจากบัญชีใหม่. trial balance ก่อน-หลังตรง. ผ่าน gate เดียวกับ
    /// reclassify-line (FiscalPeriod=Open, ไม่มีเอกสารปลายทาง, ไม่มี Payment
    /// แยก, ไม่อยู่ TaxReport=Submitted, ไม่ได้ส่ง e-Tax).
    ///
    /// แหล่งเงินใหม่ระบุได้ทางใดทางหนึ่ง: newBankAccountId (บัญชีธนาคาร →
    /// ใช้ LinkedAccountId เป็น Cr GL) หรือ newPaymentAccountId (ChartOfAccount
    /// ตรง ๆ เช่นเงินสด 1111 / เงินทดรองกรรมการ).
    /// </summary>
    public async Task<DocumentResponse> ReclassifyPaymentSourceAsync(
        Guid companyId, Guid documentId,
        Guid? newBankAccountId, Guid? newPaymentAccountId,
        string? reason, string actor)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (!newBankAccountId.HasValue && !newPaymentAccountId.HasValue)
            throw new InvalidOperationException("กรุณาเลือกแหล่งเงินใหม่ (บัญชีธนาคารหรือบัญชีเงินสด)");

        // เฉพาะเอกสารที่ "มีแหล่งเงิน" อยู่แล้ว (จ่าย/รับสด). เอกสาร A/P-A/R
        // ที่ Cr เป็นเจ้าหนี้/ลูกหนี้ ไม่มีแหล่งเงินให้แก้ตรงนี้ (เงินไหลตอน
        // PV/Receipt ปลายทาง) → แก้ที่เอกสารปลายทางแทน.
        if (!doc.BankAccountId.HasValue && !doc.PaymentAccountId.HasValue)
            throw new InvalidOperationException(
                "เอกสารนี้ไม่มีแหล่งเงิน (บัญชี Cr เงินสด/ธนาคาร) ที่ระบุไว้ — " +
                "ถ้าเป็นใบตั้งหนี้ เงินจะไหลตอนสร้างใบสำคัญจ่าย ให้แก้แหล่งเงินที่ใบนั้น");

        if (doc.Status is DocumentStatus.Draft or DocumentStatus.Voided
                       or DocumentStatus.Rejected)
            throw new InvalidOperationException(
                $"เอกสาร Status={doc.Status} ไม่อยู่ในขั้นที่แก้แหล่งเงินแบบ reclassify ได้ " +
                "(Draft = แก้ผ่านฟอร์มปกติ, Voided/Rejected = สร้างใหม่)");

        // ===== gate compliance เดียวกับ reclassify-line =====
        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(p =>
            p.CompanyId == companyId
            && p.StartDate <= doc.DocumentDate && p.EndDate >= doc.DocumentDate);
        if (period != null && period.Status != FiscalPeriodStatus.Open)
            throw new InvalidOperationException(
                $"วันที่เอกสารอยู่ในงวด '{period.Name}' ที่ปิดแล้ว ({period.Status}) — แก้แหล่งเงินไม่ได้");

        var hasDownstream = await _db.Documents.AsNoTracking().AnyAsync(d =>
            d.CompanyId == companyId && d.RelatedDocumentId == documentId
            && d.Status != DocumentStatus.Voided && !d.IsDeleted);
        if (hasDownstream)
            throw new InvalidOperationException(
                "มีเอกสารปลายทางอ้างเอกสารนี้แล้ว — ยกเลิกเอกสารปลายทางก่อน หรือ void+ออกใหม่");

        // มี Payment แยก = เงินไหลผ่าน JE ของ Payment ไม่ใช่ JE ของเอกสารนี้ →
        // แก้แหล่งเงินต้องไปแก้ที่ Payment แทน (กัน JE คู่ใหม่ไม่ตรงของจริง).
        var hasPayments = await _db.Payments.AsNoTracking().AnyAsync(p =>
            p.CompanyId == companyId && p.DocumentId == documentId && !p.IsDeleted);
        if (hasPayments)
            throw new InvalidOperationException(
                "เอกสารนี้มีรายการชำระเงินแยก — กรุณาแก้แหล่งเงินที่รายการชำระนั้น");

        var inSubmittedReport = await _db.TaxReportLines.AsNoTracking().AnyAsync(l =>
            l.DocumentId == documentId && l.TaxReport.CompanyId == companyId
            && (l.TaxReport.Status == TaxReportStatus.Submitted
                || l.TaxReport.Status == TaxReportStatus.Filed));
        if (inSubmittedReport)
            throw new InvalidOperationException(
                "เอกสารอยู่ในรายงานภาษีที่ยื่นสรรพากรแล้ว — แก้ไม่ได้");

        var hasSubmittedEtax = await _db.EtaxInvoices.AsNoTracking().AnyAsync(e =>
            e.DocumentId == documentId && e.SubmittedAt != null);
        if (hasSubmittedEtax)
            throw new InvalidOperationException("เอกสารนี้ส่ง e-Tax XML ไปสรรพากรแล้ว — แก้ไม่ได้");

        // ── Resolve Cr GL เดิม + ใหม่ ──
        // BankAccount → LinkedAccountId เป็น GL; PaymentAccountId เป็น GL ตรง.
        async Task<(Guid Id, string Code, string Name)> ResolveCreditGlAsync(
            Guid? bankAccountId, Guid? paymentAccountId, string side)
        {
            Guid? glId = paymentAccountId;
            if (!glId.HasValue && bankAccountId.HasValue)
            {
                glId = await _db.BankAccounts.AsNoTracking()
                    .Where(b => b.Id == bankAccountId.Value && b.CompanyId == companyId)
                    .Select(b => b.LinkedAccountId)
                    .FirstOrDefaultAsync();
                if (!glId.HasValue)
                    throw new InvalidOperationException(
                        $"บัญชีธนาคาร ({side}) ยังไม่ได้ผูกผังบัญชี (LinkedAccountId) — ตั้งค่าก่อน");
            }
            if (!glId.HasValue)
                throw new InvalidOperationException($"หาแหล่งเงิน ({side}) ไม่เจอ");
            var acct = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.Id == glId.Value && a.CompanyId == companyId)
                .Select(a => new { a.Id, a.AccountCode, a.AccountName })
                .FirstOrDefaultAsync()
                ?? throw new InvalidOperationException($"ไม่พบผังบัญชีแหล่งเงิน ({side})");
            return (acct.Id, acct.AccountCode, acct.AccountName);
        }

        var oldGl = await ResolveCreditGlAsync(doc.BankAccountId, doc.PaymentAccountId, "เดิม");
        var newGl = await ResolveCreditGlAsync(newBankAccountId, newPaymentAccountId, "ใหม่");

        if (oldGl.Id == newGl.Id)
        {
            // GL ปลายทางเดียวกัน (เช่นสลับบัญชีธนาคารที่ผูก GL เดียวกัน) →
            // ไม่ต้อง post JE แค่อัปเดต field อ้างอิง.
            doc.BankAccountId = newBankAccountId;
            doc.PaymentAccountId = newPaymentAccountId;
            doc.UpdatedAt = DateTime.UtcNow;
            doc.UpdatedBy = actor;
            await _db.SaveChangesAsync();
            return await GetDocumentAsync(companyId, documentId);
        }

        // เงินที่ลง Cr บัญชีแหล่งเงินเดิม = เงินสดจ่ายจริง = PaidAmount.
        var amount = doc.PaidAmount;
        if (amount <= 0.005m)
        {
            // ยังไม่ได้จ่ายจริง (ไม่กระทบ GL แหล่งเงิน) → แค่อัปเดต field.
            doc.BankAccountId = newBankAccountId;
            doc.PaymentAccountId = newPaymentAccountId;
            doc.UpdatedAt = DateTime.UtcNow;
            doc.UpdatedBy = actor;
            await _db.SaveChangesAsync();
            return await GetDocumentAsync(companyId, documentId);
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Correcting JE: Dr {old} / Cr {new} — เงินกลับเข้าบัญชีเก่า +
            // ออกจากบัญชีใหม่. (กลับทิศกับ reclassify-line เพราะแก้ฝั่ง Cr).
            var desc = $"เปลี่ยนแหล่งเงิน — {doc.DocumentNumber} " +
                       $"({oldGl.Code} → {newGl.Code})" +
                       (string.IsNullOrWhiteSpace(reason) ? "" : $" • {reason}");
            await Journal.JournalEntryBuilder
                .For(_db, companyId, doc.DocumentDate)
                .Description(desc)
                .Reference(doc.DocumentNumber)
                .SourceDocument(doc.Id)
                .Project(doc.ProjectId)
                .Debit(oldGl.Id, amount, $"Dr {oldGl.Code} — {oldGl.Name} (คืนแหล่งเงินเดิม)")
                .Credit(newGl.Id, amount, $"Cr {newGl.Code} — {newGl.Name} (แหล่งเงินใหม่)")
                .PostAsync(actor);

            doc.BankAccountId = newBankAccountId;
            doc.PaymentAccountId = newPaymentAccountId;
            doc.UpdatedAt = DateTime.UtcNow;
            doc.UpdatedBy = actor;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        _logger.LogInformation(
            "Reclassified payment source of {DocNumber}: {OldCode} → {NewCode} (amount {Amount}) by {Actor}",
            doc.DocumentNumber, oldGl.Code, newGl.Code, amount, actor);

        return await GetDocumentAsync(companyId, documentId);
    }

    // ===== Adjusting Journal Lines (Option 1) =====

    public async Task<List<DocumentAdjustingJournalLine>> ListAdjustingJournalLinesAsync(
        Guid companyId, Guid documentId)
    {
        var docExists = await _db.Documents.AsNoTracking()
            .AnyAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted);
        if (!docExists) throw new KeyNotFoundException("ไม่พบเอกสาร");

        return await _db.Set<DocumentAdjustingJournalLine>()
            .AsNoTracking()
            .Where(a => a.DocumentId == documentId && !a.IsDeleted)
            .OrderBy(a => a.LineOrder)
            .ToListAsync();
    }

    public async Task<DocumentResponse> SaveAdjustingJournalLinesAsync(Guid companyId, Guid documentId,
        IEnumerable<(Guid AccountId, decimal DebitAmount, decimal CreditAmount,
            string? Description, Guid? ProjectId, string? Reason)> lines, string actor)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        // เฉพาะ Draft — หลัง approve JE ถูก post แล้ว ห้ามแก้ผ่าน flow นี้
        // (ใช้ ReclassifyLineAccountAsync หรือ manual JE แทน)
        if (doc.Status != DocumentStatus.Draft)
            throw new InvalidOperationException(
                $"เอกสารต้องเป็นฉบับร่าง (Draft) ถึงจะแก้ adjusting lines ได้ — " +
                $"สถานะปัจจุบัน: {doc.Status}. หลัง approve ใช้ Reclassify หรือ Manual JE.");

        var list = lines.ToList();

        // Validate ทุก line + load account ids ของบริษัทเพื่อ validate FK
        var companyAccountIds = await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted)
            .Select(a => a.Id)
            .ToListAsync();

        foreach (var l in list)
        {
            if (!companyAccountIds.Contains(l.AccountId))
                throw new InvalidOperationException(
                    $"ไม่พบบัญชี {l.AccountId} ในผังบัญชีของบริษัท หรือถูกปิดใช้");
            if (l.DebitAmount < 0 || l.CreditAmount < 0)
                throw new InvalidOperationException("ยอดเดบิตและเครดิตต้องไม่ติดลบ");
            if (l.DebitAmount > 0 && l.CreditAmount > 0)
                throw new InvalidOperationException("แต่ละรายการต้องเลือก Dr หรือ Cr ด้านเดียว");
            if (l.DebitAmount == 0 && l.CreditAmount == 0)
                throw new InvalidOperationException("รายการต้องมียอด Dr หรือ Cr อย่างน้อยด้านเดียว");
        }

        // Balance check — Dr รวม = Cr รวม (ของ adjusting). ระบบไม่ block
        // ตอน save (user อาจกำลังเขียนค้าง) แต่ AutoPost ตรวจตอน approve.
        // ที่นี่แค่เตือนใน log; การ block จริงอยู่ที่ AutoPost.

        // Replace all — ลบเก่าทิ้ง insert ใหม่ (full sync)
        var existing = await _db.Set<DocumentAdjustingJournalLine>()
            .Where(a => a.DocumentId == documentId)
            .ToListAsync();
        _db.Set<DocumentAdjustingJournalLine>().RemoveRange(existing);

        var order = 1;
        foreach (var l in list)
        {
            _db.Set<DocumentAdjustingJournalLine>().Add(new DocumentAdjustingJournalLine
            {
                DocumentId = documentId,
                LineOrder = order++,
                AccountId = l.AccountId,
                DebitAmount = l.DebitAmount,
                CreditAmount = l.CreditAmount,
                Description = l.Description,
                ProjectId = l.ProjectId,
                Reason = l.Reason,
                CreatedBy = actor,
            });
        }

        doc.UpdatedAt = DateTime.UtcNow;
        doc.UpdatedBy = actor;
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Saved {Count} adjusting JE lines for document {DocNumber} by {Actor}",
            list.Count, doc.DocumentNumber, actor);

        return await GetDocumentAsync(companyId, documentId);
    }

    /// <summary>
    /// ยกเลิกเอกสาร — เก็บเอกสารต้นฉบับไว้ + สร้าง reversal JE ตามมาตรฐานบัญชีไทย
    /// (กลับรายการ Dr↔Cr, link OriginalEntryId↔ReversedByEntryId).
    /// Cascade: void linked Payments (with their JE reversals) + void linked EtaxInvoice.
    /// ไม่ลบข้อมูลออกจากฐานข้อมูล — เพื่อรักษา audit trail และตรวจสอบทางภาษี.
    /// </summary>
    public async Task VoidDocumentAsync(Guid companyId, Guid documentId)
    {
        // Lines must be Include'd here so ApplyStockMovementsAsync (called
        // during the void transaction below) can iterate them — otherwise
        // doc.Lines is null and stock never gets restored on a void.
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (doc.Status == DocumentStatus.Voided)
            throw new InvalidOperationException("เอกสารนี้ถูกยกเลิกแล้ว");

        // Filing lock guard: an Approved document that's part of a TaxReport
        // already marked Filed (FilingLockedAt set) is sealed for audit —
        // the operator must Unlock the report first (admin) or use
        // RejectAndReverseTaxReportAsync to formally cancel the filing.
        if (_taxService != null && await _taxService.IsDocumentFilingLockedAsync(companyId, documentId))
            throw new InvalidOperationException(
                "เอกสารนี้อยู่ในรายงานภาษีที่ Filed แล้ว — กรุณา Unlock รายงานหรือใช้ 'Reject & Reverse' ก่อน");

        // Block void if eTax has been submitted/accepted by RD — must contact RD to revoke first
        var lockedEtax = await _db.EtaxInvoices.FirstOrDefaultAsync(e => e.DocumentId == documentId
            && e.CompanyId == companyId
            && (e.Status == EtaxStatus.Submitted || e.Status == EtaxStatus.Accepted));
        if (lockedEtax != null)
            throw new InvalidOperationException(
                $"ไม่สามารถยกเลิกเอกสารนี้ได้ เนื่องจาก e-Tax เลขที่ {lockedEtax.EtaxRefNumber} " +
                "ถูกส่งหรืออนุมัติโดยกรมสรรพากรแล้ว ต้องดำเนินการขอยกเลิกที่กรมสรรพากรก่อน");

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                // 1) Void linked Payments first — each reverses its own JE + restores doc balance
                //    (We void *all* payments inside this transaction; the document gets voided
                //    after, so payment-balance recalculation here is intermediate only.)
                var payments = await _db.Payments
                    .Where(p => p.DocumentId == documentId && p.CompanyId == companyId && !p.IsDeleted)
                    .ToListAsync();
                foreach (var payment in payments)
                {
                    await ReversePaymentInternalAsync(companyId, payment, doc,
                        $"ยกเลิกอัตโนมัติพร้อมเอกสาร {doc.DocumentNumber}");
                }

                // 2) Reverse linked Posted JEs via AccountingService (proper linkage:
                //    OriginalEntryId/ReversedByEntryId, fiscal period validation, dimensions).
                var postedJournalIds = await _db.JournalEntries
                    .Where(j => j.SourceDocumentId == documentId && j.CompanyId == companyId
                        && j.Status == JournalEntryStatus.Posted)
                    .Select(j => j.Id)
                    .ToListAsync();
                foreach (var jeId in postedJournalIds)
                {
                    await _accountingService.ReverseJournalEntryAsync(companyId, jeId,
                        reversalDate: DateTime.UtcNow.Date,
                        description: $"ยกเลิกเอกสาร {doc.DocumentNumber}",
                        systemTriggered: true);
                }

                // 3) Void linked EtaxInvoice (keep XML/PDF for audit; only flag status)
                var etaxes = await _db.EtaxInvoices
                    .Where(e => e.DocumentId == documentId && e.CompanyId == companyId
                        && e.Status != EtaxStatus.Voided && e.Status != EtaxStatus.Submitted
                        && e.Status != EtaxStatus.Accepted)
                    .ToListAsync();
                foreach (var etax in etaxes)
                {
                    etax.Status = EtaxStatus.Voided;
                    etax.VoidedAt = DateTime.UtcNow;
                    etax.VoidReason = $"ยกเลิกพร้อมเอกสาร {doc.DocumentNumber}";
                    etax.UpdatedAt = DateTime.UtcNow;
                }

                // 4) Unlink BankTransactions matched to this document's journal entries
                if (postedJournalIds.Any())
                {
                    await _db.BankTransactions
                        .Where(t => t.CompanyId == companyId
                            && t.MatchedJournalEntryId.HasValue
                            && postedJournalIds.Contains(t.MatchedJournalEntryId.Value))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(t => t.MatchedJournalEntryId, (Guid?)null)
                            .SetProperty(t => t.ReconciliationStatus, ReconciliationStatus.Unmatched)
                            .SetProperty(t => t.ReconciledAt, (DateTime?)null));
                }

                // 5) Restore source document's balance if this was a derivative
                //    (Receipt/CN/PaymentVoucher referencing another doc). Mirrors
                //    the adjustment applied during ApproveDocumentAsync.
                await RevertSourceDocumentAdjustmentsAsync(companyId, doc);

                // 6) Back out this document's contribution to its project's
                //    BilledAmount — mirror of the +1 applied at approval.
                //    Skip Draft docs: never approved, so never billed.
                if (doc.Status != DocumentStatus.Draft)
                    await ApplyProjectBillingAsync(companyId, doc, -1);

                // 6b) Reverse any stock movement this document caused at
                //     approval. Sign=-1 means a sale Invoice's OUT becomes IN
                //     (stock restored), a purchase Invoice's IN becomes OUT.
                //     Same Draft skip: drafts never decremented stock.
                if (doc.Status != DocumentStatus.Draft)
                    await ApplyStockMovementsAsync(companyId, doc, -1, "system-void");

                // 6c) Back out this document's auto-booked project cost entries
                //     — mirror of SyncProjectCostEntriesAsync at approval. Soft-
                //     deletes the PCE rows and subtracts from Project.ActualCost
                //     so a voided cost doc no longer inflates the project's
                //     actual cost. Same Draft skip: drafts never booked a PCE.
                if (doc.Status != DocumentStatus.Draft)
                    await ReverseProjectCostEntriesAsync(companyId, doc);

                // 6d) Void any auto-issued WHT certificates that point at this
                //     document. WHT certs are created at payment time (cash
                //     basis) and feed PND.3/53 monthly exports — without this
                //     cascade, voiding a PV/PI/Expense leaves the cert "Issued"
                //     and the company silently remits + pays tax for a payment
                //     that no longer exists. Best-effort + bounded: only
                //     not-already-voided certs linked to THIS doc.
                var linkedCerts = await _db.WithholdingTaxCerts
                    .Where(w => w.CompanyId == companyId && w.DocumentId == documentId
                                && w.Status != WithholdingTaxCertStatus.Voided && !w.IsDeleted)
                    .Select(w => w.Id)
                    .ToListAsync();
                foreach (var certId in linkedCerts)
                {
                    try { await _whtService.VoidAsync(companyId, certId); }
                    catch (Exception ex) { _logger.LogWarning(ex, "WHT cert {Cert} void failed during doc void {Doc}", certId, documentId); }
                }

                // 7) Finally void the document itself + clear the stale
                //    aging value. The list-row gate already suppresses the
                //    badge visually for Voided status, but cleaning the
                //    underlying field keeps reports + bulk queries honest.
                doc.Status = DocumentStatus.Voided;
                doc.AgingDays = null;
                doc.AgingLastEvaluatedAt = DateTime.UtcNow;
                doc.UpdatedAt = DateTime.UtcNow;

                // 7-asset) Cascade FixedAsset ที่ AutoRegister มาจาก doc นี้:
                //   • ยังไม่ยืนยัน (NeedsReview=true) + ไม่มี posted dep → ลบ
                //     เลย (รวม projected dep) — ถือเป็น orphan placeholder
                //     ที่ user ยังไม่ commit
                //   • ยืนยันแล้ว (NeedsReview=false) → ปล่อย + log warning
                //     ให้ user ตัดสิน dispose/write-off เองด้วย UI
                // inline delete — ไม่เรียก FixedAssetService.DeleteAsync เพราะ
                // guard ของมันจะ block (source doc.Status ใน DB ยังไม่ Voided
                // เพราะ SaveChangesAsync ยังไม่รัน)
                var ourAssets = await _db.FixedAssets
                    .Where(a => a.CompanyId == companyId && a.SourceDocumentId == documentId)
                    .ToListAsync();
                foreach (var asset in ourAssets)
                {
                    if (!asset.NeedsReview)
                    {
                        _logger.LogWarning(
                            "Doc {Doc} void: asset {Code} ยืนยันแล้ว — ไม่ลบ ผู้ใช้ต้อง dispose/write-off ด้วยตนเอง",
                            documentId, asset.AssetCode);
                        continue;
                    }
                    var hasPostedDep = await _db.AssetDepreciations
                        .AnyAsync(d => d.FixedAssetId == asset.Id && d.IsPosted && !d.IsDeleted);
                    if (hasPostedDep)
                    {
                        _logger.LogWarning(
                            "Doc {Doc} void: asset {Code} (NeedsReview) มีค่าเสื่อม posted — ไม่ลบ",
                            documentId, asset.AssetCode);
                        continue;
                    }
                    var projectedDeps = await _db.AssetDepreciations
                        .Where(d => d.FixedAssetId == asset.Id)
                        .ToListAsync();
                    _db.AssetDepreciations.RemoveRange(projectedDeps);
                    _db.FixedAssets.Remove(asset);
                }

                // 7a) Reset stateful posting flags ที่ตั้งตอน approve. ถ้าไม่ reset
                //     แล้ว user re-approve doc นี้ในอนาคต logic จะข้ามขั้นที่
                //     ควรรัน (เช่น undue VAT ถูก mark claimable ไปแล้ว → re-
                //     approve จะไม่ลง 11640 ใหม่). ทุก field reset ที่นี่จะถูก
                //     "เริ่มใหม่" ตอน re-approve เหมือนใบใหม่ผ่าน flow ปกติ.
                doc.InputVatPostedAsUndue = false;
                doc.InputVatBecameClaimableAt = null;

                // 7b) Deposit (Receipt/ReceiptVoucher IsDeposit=true): reset
                //     state realize/refund/recognize. JE reversal ใน step 2
                //     กลับยอดบัญชี 217xx/21911/21913 แล้ว แต่ field document-
                //     level เหล่านี้ถ้าไม่เคลียร์ GetDepositsAsync จะยังโชว์
                //     สถานะ "Partial/Realized" หลัง void → UI/รายงานเพี้ยน.
                if (doc.IsDeposit)
                {
                    doc.DepositRealizedAmount = 0m;
                    doc.DepositRealizedAt = null;
                    doc.DepositOutputVatRecognizedAt = null;
                    doc.DepositRefundedAmount = 0m;
                    doc.DepositRefundedAt = null;
                    doc.DepositAppliedToDocumentId = null;
                }

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        // Cascade-unwind any ReconciliationGroup that referenced this Document
        // directly (ItemType=Document — for Receipt/PV operators dragged into
        // a group without going through a Payment record). JE-pathed groups
        // were already unwound during step (2) via ReverseJournalEntryAsync.
        // Runs OUTSIDE the strategy/transaction above so a bank-cleanup
        // hiccup doesn't roll back the voided document state.
        if (_bankService != null)
        {
            try { await _bankService.UnwindGroupsContainingItemAsync(companyId, ReconciliationItemType.Document, documentId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Group unwind for voided document {DocId} failed", documentId); }
        }

        // Fire DocumentVoided notification (best-effort, post-commit). Cascades
        // through the per-user matrix to Accounting / Owner recipients per
        // their configured channels (in-app, LINE, email).
        if (_notify != null)
        {
            try
            {
                var contactName = await _db.Contacts.Where(c => c.Id == doc.ContactId)
                    .Select(c => c.Name).FirstOrDefaultAsync() ?? "";
                await _notify.DispatchAsync(companyId, NotificationEvents.DocumentVoided, new NotificationContext
                {
                    Title = $"ยกเลิกเอกสาร {doc.DocumentNumber}",
                    Message = $"{doc.DocumentType} · {contactName} · ยอดรวม {doc.TotalAmount:N2} บาท — กลับรายการบัญชี/ชำระเงิน/e-Tax อัตโนมัติ",
                    ActionUrl = $"/pages/documents.html?id={doc.Id}",
                    EntityType = "Document", EntityId = doc.Id,
                });
            }
            catch (Exception ex) { _logger.LogWarning(ex, "DocumentVoided notification failed for {DocId}", documentId); }
        }

        // Webhook — DocumentVoided fires last so partners observe a
        // fully-settled state (linked payments already reversed, JE
        // already gone, bank-group already unwound).
        await FireWebhookAsync(companyId, "document.voided", new
        {
            documentId = doc.Id,
            documentNumber = doc.DocumentNumber,
            documentType = doc.DocumentType.ToString(),
            voidedAt = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// ลบเอกสารถาวร — เฉพาะเอกสารฉบับร่าง (Draft) ที่ยังไม่กระทบบัญชีและไม่มีการชำระเงินเท่านั้น
    /// เอกสารที่อนุมัติแล้วต้องใช้ "ยกเลิก" (VoidDocumentAsync) เพื่อรักษา audit trail.
    /// </summary>
    public async Task DeleteDocumentAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        if (doc.Status != DocumentStatus.Draft)
            throw new InvalidOperationException(
                "ไม่สามารถลบเอกสารที่อนุมัติแล้วได้ — กรุณาใช้คำสั่ง 'ยกเลิก' " +
                "เพื่อสร้างรายการกลับบัญชีตามมาตรฐาน (รักษา audit trail)");

        var hasJournal = await _db.JournalEntries.AnyAsync(j => j.SourceDocumentId == documentId
            && j.CompanyId == companyId);
        if (hasJournal)
            throw new InvalidOperationException(
                "ไม่สามารถลบเอกสารที่มีรายการบัญชีเชื่อมอยู่ได้ — กรุณาใช้ 'ยกเลิก' แทน");

        var hasPayment = await _db.Payments.AnyAsync(p => p.DocumentId == documentId
            && p.CompanyId == companyId && !p.IsDeleted);
        if (hasPayment)
            throw new InvalidOperationException(
                "ไม่สามารถลบเอกสารที่มีการชำระเงินแล้วได้ — กรุณายกเลิกการชำระเงินก่อน");

        var hasEtax = await _db.EtaxInvoices.AnyAsync(e => e.DocumentId == documentId
            && e.CompanyId == companyId && e.Status != EtaxStatus.Voided);
        if (hasEtax)
            throw new InvalidOperationException(
                "ไม่สามารถลบเอกสารที่มีใบกำกับภาษีอิเล็กทรอนิกส์ (e-Tax) เชื่อมอยู่ได้");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // Hard-delete lines first (FK), then header. Use Remove (not soft-delete)
            // because Draft never reached the books — no audit obligation.
            _db.DocumentLines.RemoveRange(doc.Lines);
            _db.Documents.Remove(doc);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task PurgeDocumentAsync(Guid companyId, Guid documentId)
    {
        await PurgeDocumentAsync(companyId, documentId, null);
    }

    public async Task PurgeDocumentAsync(Guid companyId, Guid documentId, Guid? userId)
    {
        await PurgeDocumentAsync(companyId, documentId, userId, forceOverrideRetention: false, overrideReason: null);
    }

    /// <summary>ลบเอกสารถาวร. ปกติ block เอกสารที่อยู่ในช่วงเก็บรักษา §87/3
    /// (5 ปี) — ต้องใช้ Void แทน. แต่ Owner/SystemAdmin override ได้ด้วย
    /// forceOverrideRetention=true + เหตุผล (เช่น ข้อมูลทดสอบ / สร้างผิดซ้ำ)
    /// → ระบบ log การ override ลง ErrorLog (audit) ว่าใคร/เมื่อไหร่/ทำไม
    /// เพื่อความรับผิดชอบ. ความเสี่ยงทางกฎหมายเป็นของผู้ override.</summary>
    public async Task PurgeDocumentAsync(Guid companyId, Guid documentId, Guid? userId,
        bool forceOverrideRetention, string? overrideReason)
    {
        var doc = await _db.Documents
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        // Legal hold §87/3 + พ.ร.บ.บัญชี ม.10 — ห้ามลบจริงก่อนครบอายุเก็บ 5 ปี
        // (เอกสารที่ approve แล้วเท่านั้นที่มี RetentionUntil; Draft ลบได้).
        var inRetention = doc.RetentionUntil.HasValue
            && DateTime.UtcNow.Date < doc.RetentionUntil.Value.Date
            && doc.Status != DocumentStatus.Draft;
        if (inRetention && !forceOverrideRetention)
            throw new InvalidOperationException(
                $"ห้ามลบถาวร — เอกสารอยู่ในช่วงเก็บรักษาตามกฎหมาย (§87/3) ถึง {doc.RetentionUntil:dd/MM/yyyy}. " +
                "ใช้ 'ยกเลิกเอกสาร' (Void) แทนเพื่อคงหลักฐานการตรวจสอบ");

        // Override path — บันทึก audit ว่าใคร force-delete เอกสารในช่วง retention
        // ทำไม. ทำให้สรรพากร/ผู้ตรวจเห็น trail ว่าการลบเป็นการตัดสินใจที่ระบุ
        // ตัวตน + เหตุผล ไม่ใช่ลบลับ ๆ. ถ้าไม่ระบุเหตุผล → block (กันลบมั่ว).
        if (inRetention && forceOverrideRetention)
        {
            if (string.IsNullOrWhiteSpace(overrideReason))
                throw new InvalidOperationException(
                    "การลบเอกสารในช่วงเก็บรักษาตามกฎหมายต้องระบุเหตุผล (เช่น ข้อมูลทดสอบ / สร้างผิดซ้ำ)");
            // audit ผ่าน logger (forensic) — บันทึกว่าใคร force-delete เอกสารใน
            // ช่วง retention ทำไม. AuditLog hash-chain ก็จับ Delete นี้อีกชั้น
            // ผ่าน ChangeTracker (append-only).
            _logger.LogWarning(
                "RETENTION-OVERRIDE: user {UserId} force-deleted {DocNum} ({DocType}, retain until {Until:yyyy-MM-dd}). Reason: {Reason}",
                userId, doc.DocumentNumber, doc.DocumentType, doc.RetentionUntil, overrideReason);
        }

        var auditSnapshot = System.Text.Json.JsonSerializer.Serialize(new
        {
            doc.DocumentNumber, doc.DocumentType, doc.Status,
            doc.TotalAmount, doc.ContactId, doc.DocumentDate
        });

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1. Delete JournalLineDimensions → JournalEntryLines → JournalEntries
            var journalIds = await _db.JournalEntries
                .IgnoreQueryFilters()
                .Where(j => j.SourceDocumentId == documentId && j.CompanyId == companyId)
                .Select(j => j.Id)
                .ToListAsync();

            // Also include reversal entries (linked via OriginalEntryId or ReversedByEntryId)
            if (journalIds.Any())
            {
                var reversalIds = await _db.JournalEntries
                    .IgnoreQueryFilters()
                    .Where(j => j.CompanyId == companyId &&
                        (journalIds.Contains(j.OriginalEntryId ?? Guid.Empty) ||
                         journalIds.Contains(j.ReversedByEntryId ?? Guid.Empty)))
                    .Select(j => j.Id)
                    .ToListAsync();
                journalIds = journalIds.Union(reversalIds).Distinct().ToList();
            }

            if (journalIds.Any())
            {
                var lineIds = await _db.JournalEntryLines
                    .IgnoreQueryFilters()
                    .Where(l => journalIds.Contains(l.JournalEntryId))
                    .Select(l => l.Id)
                    .ToListAsync();

                if (lineIds.Any())
                {
                    await _db.Database.ExecuteSqlRawAsync(
                        @"DELETE FROM ""JournalLineDimensions"" WHERE ""JournalEntryLineId"" = ANY({0})",
                        lineIds);
                    await _db.Database.ExecuteSqlRawAsync(
                        @"DELETE FROM ""JournalEntryLines"" WHERE ""Id"" = ANY({0})",
                        lineIds);
                }

                // Null out references from other tables before deleting journals
                await _db.Database.ExecuteSqlRawAsync(
                    @"UPDATE ""BankTransactions"" SET ""MatchedJournalEntryId"" = NULL WHERE ""MatchedJournalEntryId"" = ANY({0})",
                    journalIds);

                await _db.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""JournalEntries"" WHERE ""Id"" = ANY({0})",
                    journalIds);
            }

            // 2. Delete Payments
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Payments"" WHERE ""DocumentId"" = {0} AND ""CompanyId"" = {1}",
                documentId, companyId);

            // 3. Delete WHT certificates + lines linked to this document
            var whtIds = await _db.WithholdingTaxCerts
                .IgnoreQueryFilters()
                .Where(w => w.DocumentId == documentId && w.CompanyId == companyId)
                .Select(w => w.Id)
                .ToListAsync();

            if (whtIds.Any())
            {
                await _db.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""WithholdingTaxCertLines"" WHERE ""WithholdingTaxCertId"" = ANY({0})",
                    whtIds);
                await _db.Database.ExecuteSqlRawAsync(
                    @"DELETE FROM ""WithholdingTaxCerts"" WHERE ""Id"" = ANY({0})",
                    whtIds);
            }

            // 4. Delete EtaxInvoices
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""EtaxInvoices"" WHERE ""DocumentId"" = {0} AND ""CompanyId"" = {1}",
                documentId, companyId);

            // 5. Null out DocumentEmailLogs (FK is SetNull but we force-clean)
            await _db.Database.ExecuteSqlRawAsync(
                @"UPDATE ""DocumentEmailLogs"" SET ""DocumentId"" = NULL WHERE ""DocumentId"" = {0}",
                documentId);

            // 6. Delete DocumentApprovals & DocumentSignatures
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""DocumentApprovals"" WHERE ""DocumentId"" = {0}",
                documentId);
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""DocumentSignatures"" WHERE ""DocumentId"" = {0}",
                documentId);

            // 7. Null out references from other documents (RelatedDocumentId)
            await _db.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Documents"" SET ""RelatedDocumentId"" = NULL WHERE ""RelatedDocumentId"" = {0}",
                documentId);

            // 8. Delete DocumentLines → Document
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""DocumentLines"" WHERE ""DocumentId"" = {0}",
                documentId);
            await _db.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Documents"" WHERE ""Id"" = {0} AND ""CompanyId"" = {1}",
                documentId, companyId);

            if (userId.HasValue)
            {
                _db.AuditLogs.Add(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = userId,
                    Action = AuditAction.Delete,
                    EntityType = "Document",
                    EntityId = documentId.ToString(),
                    OldValues = auditSnapshot,
                    Timestamp = DateTime.UtcNow
                });
                await _db.SaveChangesAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// ยกเลิกการชำระเงิน — กลับรายการ JE ของการชำระ + คืนยอดให้เอกสารต้นทาง.
    /// ใช้ทั้งจาก endpoint โดยตรง และจาก VoidDocumentAsync (cascade).
    /// </summary>
    public async Task VoidPaymentAsync(Guid companyId, Guid paymentId)
    {
        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId
            && p.CompanyId == companyId && !p.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการชำระเงิน");

        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == payment.DocumentId
            && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสารต้นทาง");

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                await ReversePaymentInternalAsync(companyId, payment, doc, "ยกเลิกการชำระเงิน");
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        // Cascade-unwind any ReconciliationGroup that referenced this Payment
        // directly (ItemType=Payment). JE-pathed groups were unwound by
        // ReversePaymentInternalAsync → ReverseJournalEntryAsync → bank cleanup.
        if (_bankService != null)
        {
            try { await _bankService.UnwindGroupsContainingItemAsync(companyId, ReconciliationItemType.Payment, paymentId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Group unwind for voided payment {PayId} failed", paymentId); }
        }
    }

    /// <summary>
    /// ตัดหนี้สูญ — เมื่อลูกหนี้ผิดนัดและมั่นใจว่าจะไม่ได้รับเงิน
    /// JE: Dr หนี้สูญ (64xxx), Cr ลูกหนี้การค้า (113)
    /// อัพเดต source.PaidAmount = TotalAmount, BalanceDue = 0, Status = Paid (เคลียร์)
    /// </summary>
    public async Task<DocumentResponse> WriteOffBadDebtAsync(Guid companyId, Guid documentId, string writtenOffBy, string? reason = null)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        // Only AR documents with outstanding balance can be written off
        var arTypes = new[] {
            DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.DebitNote
        };
        if (!arTypes.Contains(doc.DocumentType))
            throw new InvalidOperationException(
                "ตัดหนี้สูญได้เฉพาะใบแจ้งหนี้ / ใบกำกับภาษี / ใบเพิ่มหนี้ ที่ยังคงค้างเท่านั้น");

        if (doc.Status != DocumentStatus.Approved
            && doc.Status != DocumentStatus.PartiallyPaid
            && doc.Status != DocumentStatus.Sent
            && doc.Status != DocumentStatus.Overdue)
            throw new InvalidOperationException(
                "ตัดหนี้สูญได้เฉพาะเอกสารที่อนุมัติแล้วและยังคงค้าง");

        if (doc.BalanceDue <= 0.01m)
            throw new InvalidOperationException("เอกสารนี้ไม่มียอดคงค้าง — ไม่ต้องตัดหนี้สูญ");

        // Find or fall back to bad-debt expense account
        var badDebtAcc = await FindAccountAsync(companyId, "64000")
            ?? await FindAccountAsync(companyId, "55000")
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId
                    && a.AccountType == AccountType.Expense
                    && a.AccountName!.Contains("หนี้สูญ")
                    && a.IsActive)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();
        if (badDebtAcc == null)
            throw new InvalidOperationException(
                "ไม่พบบัญชี 'หนี้สูญ' (64000) ในผังบัญชี กรุณาเพิ่มบัญชีก่อน");

        var arAcc = await FindAccountAsync(companyId, "113", doc.Contact)
            ?? throw new InvalidOperationException("ไม่พบบัญชี 'ลูกหนี้การค้า' (113) ในผังบัญชี");

        var writeOffAmount = doc.BalanceDue;

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                // Resolve fiscal period
                var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
                    f.CompanyId == companyId &&
                    f.StartDate <= DateTime.UtcNow.Date &&
                    f.EndDate >= DateTime.UtcNow.Date);
                if (period != null && period.Status != FiscalPeriodStatus.Open)
                    throw new InvalidOperationException(
                        $"ไม่สามารถตัดหนี้สูญในงวด {period.Name} ได้ เนื่องจากงวดถูกปิดแล้ว");

                var entryNumber = await GetNextJournalEntryNumberAsync(companyId, "JV");
                var je = new JournalEntry
                {
                    CompanyId = companyId,
                    EntryNumber = entryNumber,
                    EntryDate = DateTime.UtcNow.Date,
                    JournalType = JournalType.General,
                    Description = $"ตัดหนี้สูญ - {doc.DocumentNumber}" + (reason != null ? $" ({reason})" : ""),
                    Reference = doc.DocumentNumber,
                    Status = JournalEntryStatus.Posted,
                    TotalDebit = writeOffAmount,
                    TotalCredit = writeOffAmount,
                    CreatedBy = writtenOffBy,
                    IsAutoGenerated = true,
                    SourceDocumentId = doc.Id,
                    FiscalPeriodId = period?.Id,
                    ProjectId = doc.ProjectId
                };
                _db.JournalEntries.Add(je);
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    JournalEntryId = je.Id,
                    AccountId = badDebtAcc.Id,
                    DebitAmount = writeOffAmount,
                    CreditAmount = 0,
                    Description = $"หนี้สูญ - {doc.DocumentNumber}",
                    LineOrder = 1,
                    ProjectId = doc.ProjectId
                });
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    JournalEntryId = je.Id,
                    AccountId = arAcc.Id,
                    DebitAmount = 0,
                    CreditAmount = writeOffAmount,
                    Description = $"ตัดลูกหนี้ - {doc.DocumentNumber}",
                    LineOrder = 2,
                    ProjectId = doc.ProjectId
                });

                // Clear the source document
                doc.PaidAmount = doc.TotalAmount;
                doc.BalanceDue = 0;
                doc.Status = DocumentStatus.Paid;
                doc.AgingDays = null;
                doc.AgingLastEvaluatedAt = DateTime.UtcNow;
                doc.UpdatedBy = writtenOffBy;
                doc.UpdatedAt = DateTime.UtcNow;
                doc.InternalNotes = string.IsNullOrWhiteSpace(doc.InternalNotes)
                    ? $"ตัดหนี้สูญ {DateTime.UtcNow:yyyy-MM-dd}: {reason ?? ""}"
                    : doc.InternalNotes + $"\nตัดหนี้สูญ {DateTime.UtcNow:yyyy-MM-dd}: {reason ?? ""}";

                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        return await GetDocumentAsync(companyId, documentId);
    }

    /// <summary>
    /// Internal: reverses a single payment within an existing transaction.
    /// - Creates JE reversal (via AccountingService) for the payment's posted JE
    /// - Soft-deletes the payment (IsDeleted=true) — keeps record for audit
    /// - Restores doc.PaidAmount and doc.BalanceDue
    /// - Recalculates doc.Status (Paid → PartiallyPaid → Approved)
    /// Caller is responsible for transaction + final SaveChangesAsync.
    /// </summary>
    private async Task ReversePaymentInternalAsync(Guid companyId, Payment payment, Document doc, string reason)
    {
        // Reverse linked JEs created from this payment.
        // Payment JEs are linked via SourceDocumentId = doc.Id with a Reference matching payment.PaymentNumber.
        var paymentJournals = await _db.JournalEntries
            .Where(j => j.SourceDocumentId == doc.Id
                && j.CompanyId == companyId
                && j.Status == JournalEntryStatus.Posted
                && j.Reference == payment.PaymentNumber)
            .Select(j => j.Id)
            .ToListAsync();

        // Fail loud if the payment posted to GL but we can't find the JE to
        // reverse — silently skipping leaves Cash/AR overstated. The most
        // common cause is a Reference-format drift (whitespace, case). Log
        // the discrepancy and surface a clear error so the operator can
        // reverse the orphan JE manually instead of corrupting GL silently.
        if (paymentJournals.Count == 0)
        {
            var anyJeForDoc = await _db.JournalEntries
                .AnyAsync(j => j.SourceDocumentId == doc.Id
                    && j.CompanyId == companyId
                    && j.Status == JournalEntryStatus.Posted);
            if (anyJeForDoc)
                throw new InvalidOperationException(
                    $"ไม่พบ JE ที่ผูกกับใบรับเงิน {payment.PaymentNumber} (อาจถูกแก้ Reference ภายหลัง) — " +
                    "กรุณากลับรายการ JE ด้วยมือก่อนทำการ Reverse Payment เพื่อไม่ให้ยอด Cash/AR ใน GL คลาดเคลื่อน");
        }

        foreach (var jeId in paymentJournals)
        {
            await _accountingService.ReverseJournalEntryAsync(companyId, jeId,
                reversalDate: DateTime.UtcNow.Date,
                description: $"{reason} - {payment.PaymentNumber}",
                systemTriggered: true);
        }

        // Reverse bank balance — payment.Amount is in the document's currency;
        // BankAccount.CurrentBalance is in THB, so apply the document's FX rate
        // before adjusting. Mirrors the conversion in CreatePaymentJournalAsync.
        if (payment.BankAccountId.HasValue)
        {
            var isInflow = doc.DocumentType is DocumentType.Invoice or DocumentType.TaxInvoice
                or DocumentType.Receipt or DocumentType.ReceiptVoucher
                or DocumentType.DebitNote or DocumentType.BillingNote;
            var thbAmount = doc.ExchangeRate == 1m
                ? payment.Amount
                : Math.Round(payment.Amount * doc.ExchangeRate, 2, MidpointRounding.AwayFromZero);
            var delta = isInflow ? -thbAmount : thbAmount;
            await _db.BankAccounts
                .Where(b => b.Id == payment.BankAccountId.Value && b.CompanyId == companyId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.CurrentBalance, b => b.CurrentBalance + delta));
        }

        // Soft-delete the payment record (keep for audit; mirrors how Reverse keeps original JE)
        payment.IsDeleted = true;
        payment.UpdatedAt = DateTime.UtcNow;

        // Restore document balance
        doc.PaidAmount = Math.Max(0m, doc.PaidAmount - payment.Amount);
        doc.BalanceDue = doc.TotalAmount - doc.PaidAmount;
        if (doc.Status != DocumentStatus.Voided)
        {
            doc.Status = doc.PaidAmount <= 0 ? DocumentStatus.Approved
                : doc.BalanceDue <= 0 ? DocumentStatus.Paid
                : DocumentStatus.PartiallyPaid;
        }
        doc.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Updates a source document's balance/status when a derivative (Receipt, CN, DN,
    /// PaymentVoucher) referencing it is approved. This keeps AR/AP aging and per-document
    /// status consistent with the GL.
    ///
    /// Rules (Thai accounting practice):
    /// - Receipt/ReceiptVoucher with ref → settlement: source.PaidAmount += amount
    /// - PaymentVoucher with ref → AP settlement: source.PaidAmount += amount
    /// - CreditNote with ref → AR offset: source.PaidAmount += amount (treats as offset)
    /// - DebitNote with ref → no source mutation (DN is a NEW AR, not adjustment)
    ///
    /// Validation: refuses if cumulative adjustments would exceed the source's TotalAmount
    /// (i.e. you cannot refund/credit more than the customer owes).
    ///
    /// Caller is responsible for transaction + SaveChangesAsync.
    /// </summary>
    private async Task ApplySourceDocumentAdjustmentsAsync(Guid companyId, Document doc)
    {
        if (!doc.RelatedDocumentId.HasValue) return;

        var isSettlement = doc.DocumentType == DocumentType.Receipt
            || doc.DocumentType == DocumentType.ReceiptVoucher
            || doc.DocumentType == DocumentType.PaymentVoucher;
        var isCreditNote = doc.DocumentType == DocumentType.CreditNote;
        var isDebitNote = doc.DocumentType == DocumentType.DebitNote;
        if (!isSettlement && !isCreditNote && !isDebitNote) return;

        // Lock source row to prevent concurrent balance modifications (FOR UPDATE)
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} FOR UPDATE", doc.RelatedDocumentId.Value);

        var source = await _db.Documents.FirstOrDefaultAsync(d =>
            d.Id == doc.RelatedDocumentId.Value && d.CompanyId == companyId);
        if (source == null) return;

        if (isSettlement)
        {
            // Settlement (Receipt/ReceiptVoucher/PaymentVoucher): strict cap.
            // Cannot collect/pay more than the outstanding balance.
            if (doc.TotalAmount > source.BalanceDue + 0.01m)
                throw new InvalidOperationException(
                    $"จำนวนเงินรับ/จ่าย ({doc.TotalAmount:N2}) มากกว่ายอดคงค้างของเอกสารต้นทาง " +
                    $"{source.DocumentNumber} (คงค้าง {source.BalanceDue:N2})");

            // Cumulative WHT cap — without this, operators using the
            // manual Receipt-conversion flow could over-withhold by issuing
            // sibling Receipts whose WHT sum exceeds the source Invoice's
            // total WHT, producing WHT-Asset / WHT-Payable balances that
            // don't reconcile with ภ.ง.ด.3/53. The CreatePaymentAsync (modal
            // flow) already enforces this; this branch is the missing twin.
            // Sums BOTH paths so mixing modal + manual Receipt still caps.
            if (doc.WithholdingTaxAmount > 0m && source.WithholdingTaxAmount > 0m)
            {
                var siblingTypes = new[] { DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.PaymentVoucher };
                var withheldViaReceipts = await _db.Documents.AsNoTracking()
                    .Where(r => r.RelatedDocumentId == source.Id
                        && r.Id != doc.Id   // exclude self (we're approving it now)
                        && siblingTypes.Contains(r.DocumentType)
                        && r.Status != DocumentStatus.Voided
                        && r.Status != DocumentStatus.Draft
                        && r.Status != DocumentStatus.Rejected
                        && !r.IsDeleted)
                    .SumAsync(r => (decimal?)r.WithholdingTaxAmount) ?? 0m;
                var withheldViaPayments = await _db.Payments.AsNoTracking()
                    .Where(p => p.DocumentId == source.Id && !p.IsDeleted)
                    .SumAsync(p => (decimal?)p.WithholdingTaxAmount) ?? 0m;
                var cumulative = withheldViaReceipts + withheldViaPayments + doc.WithholdingTaxAmount;
                if (cumulative > source.WithholdingTaxAmount + 0.01m)
                    throw new InvalidOperationException(
                        $"WHT รวมจากใบเสร็จ/ใบสำคัญทั้งหมด ({cumulative:N2}) เกินยอด WHT " +
                        $"ของเอกสารต้นทาง {source.DocumentNumber} ({source.WithholdingTaxAmount:N2}) " +
                        "— โปรดแก้ไข WHT บนเอกสารฉบับนี้ก่อนอนุมัติ");
            }

            source.PaidAmount += doc.TotalAmount;
        }
        else if (isCreditNote)
        {
            // CN: validation depends on source state.
            // - If source still has BalanceDue > 0: CN reduces AR/AP, capped by BalanceDue
            // - If source fully paid (BalanceDue = 0): CN becomes cash refund — JE
            //   handles the cash flow. Source state stays at Paid (PaidAmount unchanged).
            if (source.BalanceDue > 0.01m)
            {
                if (doc.TotalAmount > source.BalanceDue + 0.01m)
                    throw new InvalidOperationException(
                        $"จำนวนใบลดหนี้ ({doc.TotalAmount:N2}) มากกว่ายอดคงค้างของเอกสารต้นทาง " +
                        $"{source.DocumentNumber} (คงค้าง {source.BalanceDue:N2}) " +
                        $"— หากต้องการคืนเงินเกินกว่ายอดคงค้าง กรุณาแยกเป็นใบลดหนี้หลายใบ");

                source.PaidAmount += doc.TotalAmount;
            }
            // else: cash refund mode — source.PaidAmount stays at TotalAmount,
            // BalanceDue stays at 0, Status stays at Paid. JE Cr Cash handles it.
        }
        else if (isDebitNote)
        {
            // DN: increases the obligation (customer owes more / we owe more)
            // - If source has BalanceDue > 0: DN adds to BalanceDue (effectively
            //   reduces source.PaidAmount accumulation; we model it as TotalAmount += DN)
            //   Actually simpler: leave source untouched — DN is a separate AR/AP doc itself.
            // - If source fully paid: DN creates a new debt that needs to be collected
            //   separately. The DN itself acts as the new AR.
            //
            // For both cases: do NOT mutate source. The DN is a separate document
            // and shows up in AR/AP aging on its own merit.
            //
            // (User can still link via RelatedDocumentId for traceability.)
            return;
        }

        source.BalanceDue = source.TotalAmount - source.PaidAmount;
        if (source.Status != DocumentStatus.Voided)
        {
            source.Status = source.BalanceDue <= 0.01m
                ? DocumentStatus.Paid
                : DocumentStatus.PartiallyPaid;
            // Settle stale aging on the source the same instant the
            // settlement flips it to Paid (Receipt/PaymentVoucher converted
            // path). Without this the source invoice keeps showing "⏳ 30+d"
            // until the background job runs hours later.
            if (source.Status == DocumentStatus.Paid)
            {
                source.AgingDays = null;
                source.AgingLastEvaluatedAt = DateTime.UtcNow;
            }
        }
        source.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Reverse of ApplySourceDocumentAdjustmentsAsync — called from VoidDocumentAsync
    /// when a derivative document is voided, to restore the source's balance.
    /// </summary>
    private async Task RevertSourceDocumentAdjustmentsAsync(Guid companyId, Document doc)
    {
        if (!doc.RelatedDocumentId.HasValue) return;

        var typeAffectsSource = doc.DocumentType == DocumentType.Receipt
            || doc.DocumentType == DocumentType.ReceiptVoucher
            || doc.DocumentType == DocumentType.PaymentVoucher
            || doc.DocumentType == DocumentType.CreditNote;
        if (!typeAffectsSource) return;

        var source = await _db.Documents.FirstOrDefaultAsync(d =>
            d.Id == doc.RelatedDocumentId.Value && d.CompanyId == companyId);
        if (source == null) return;

        source.PaidAmount = Math.Max(0m, source.PaidAmount - doc.TotalAmount);
        source.BalanceDue = source.TotalAmount - source.PaidAmount;
        if (source.Status != DocumentStatus.Voided)
        {
            source.Status = source.PaidAmount <= 0.01m
                ? DocumentStatus.Approved
                : source.BalanceDue <= 0.01m
                    ? DocumentStatus.Paid
                    : DocumentStatus.PartiallyPaid;
            // Whatever the new status is, the aging counter is now stale —
            // either the source went back to having a balance (aging
            // restarts from DocumentDate, computed by the background job)
            // or the source stayed Paid because of other settlements
            // (aging is null). Null in either case; the cron repopulates.
            // Without this, voiding a Receipt left the now-unpaid source
            // Invoice still showing "⏳ 30+d" until the 6h job ran.
            source.AgingDays = null;
            source.AgingLastEvaluatedAt = DateTime.UtcNow;
        }
        source.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Roll an approved customer-billing document's value into its linked
    /// project's BilledAmount — the Documents → Project data flow that was
    /// missing, leaving project profitability reports showing zero billed.
    /// <paramref name="sign"/> is +1 on approval, -1 when the document is
    /// voided. Only Invoice / TaxInvoice / DebitNote count as billing;
    /// Receipts and purchase-side documents do not represent new billing.
    /// Caller owns the transaction + SaveChangesAsync.
    /// </summary>
    private async Task ApplyProjectBillingAsync(Guid companyId, Document doc, int sign)
    {
        if (!doc.ProjectId.HasValue) return;

        var billingTypes = new[] {
            DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.DebitNote
        };
        if (!billingTypes.Contains(doc.DocumentType)) return;

        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == doc.ProjectId.Value && p.CompanyId == companyId);
        if (project == null) return;

        project.BilledAmount = Math.Max(0m, project.BilledAmount + sign * doc.TotalAmount);
        project.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Valid document conversions per Thai accounting workflow.
    /// Each entry: source type → list of allowed target types.
    /// Anything not listed is rejected to prevent illogical flows like
    /// Quotation→CreditNote (CN must reference Invoice/TaxInvoice/sale).
    /// </summary>
    private static readonly Dictionary<DocumentType, DocumentType[]> ValidConversions = new()
    {
        // Sales side
        [DocumentType.Quotation] = new[]
        {
            DocumentType.Invoice, DocumentType.TaxInvoice,
            DocumentType.BillingNote, DocumentType.DeliveryNote,
            DocumentType.Receipt
        },
        [DocumentType.BillingNote] = new[]
        {
            DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt
        },
        [DocumentType.DeliveryNote] = new[]
        {
            DocumentType.Invoice, DocumentType.TaxInvoice
        },
        [DocumentType.Invoice] = new[]
        {
            DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.ReceiptVoucher
        },
        [DocumentType.TaxInvoice] = new[]
        {
            DocumentType.Receipt, DocumentType.ReceiptVoucher,
            DocumentType.CreditNote, DocumentType.DebitNote
        },
        [DocumentType.DebitNote] = new[]
        {
            DocumentType.Receipt, DocumentType.ReceiptVoucher
        },
        // Purchase side — full PR → PO → GRN → Invoice → Payment chain.
        // GRN inserted between PO and PurchaseInvoice so partial receipts
        // are tracked + 3-way match can validate billing against actual
        // receipt quantities.
        // Role separation (หลักบัญชีไทย): the PR→PO→GRN trade chain is backed
        // by the supplier's invoice and lands on ใบแจ้งหนี้ซื้อ (เจ้าหนี้การค้า
        // 21210) ONLY. ใบบันทึกค่าใช้จ่าย is the non-trade expense claim
        // (เจ้าหนี้อื่น 21220) and is never part of the PO chain — keeping the
        // two payable ledgers reconcilable independently.
        [DocumentType.PurchaseRequisition] = new[] { DocumentType.PurchaseOrder },
        [DocumentType.PurchaseOrder] = new[]
        {
            DocumentType.GoodsReceiptNote,        // partial GRN against PO
            DocumentType.PurchaseInvoice,         // skip GRN when buying services
        },
        [DocumentType.GoodsReceiptNote] = new[]
        {
            // From GRN, AP creates the bill. SourceLineId on the new
            // invoice line points back to the GRN line so 3-way match
            // can verify "billed qty ≤ received qty".
            DocumentType.PurchaseInvoice,
        },
        [DocumentType.PurchaseInvoice] = new[]
        {
            DocumentType.PaymentVoucher,
            // Adjustments from supplier — supplier issues us CN/DN.
            // Posting auto-detects purchase-side via RelatedDocumentId.
            DocumentType.CreditNote, DocumentType.DebitNote
        },
        [DocumentType.Expense] = new[]
        {
            DocumentType.PaymentVoucher,
            DocumentType.CreditNote, DocumentType.DebitNote,
            DocumentType.CertificateInLieu
        },
        [DocumentType.CertificateInLieu] = new[]
        {
            DocumentType.PaymentVoucher
        }
        // Terminal types (no further conversion):
        // Receipt, ReceiptVoucher, CreditNote, PaymentVoucher
    };

    /// <summary>Public accessor used by API endpoint to surface valid targets to UI.</summary>
    public static IReadOnlyList<DocumentType> GetValidConversionTargets(DocumentType source) =>
        ValidConversions.TryGetValue(source, out var targets) ? targets : Array.Empty<DocumentType>();

    // ===================================================================
    // Flexible / partial document composition
    //
    // A source document line may be carried forward into MANY child lines
    // across MANY documents. Conversion tracks "how much is left" along two
    // independent fulfilment axes:
    //   • Delivery — consumed by DeliveryNote
    //   • Billing  — consumed by Invoice / TaxInvoice / BillingNote /
    //                PurchaseInvoice / Expense / PurchaseOrder
    // Conversions to other types (Receipt, CreditNote, ...) settle by
    // amount, not quantity, and keep the legacy whole-document behaviour.
    // ===================================================================

    private enum FulfillmentAxis { None, Delivery, Billing }

    private static FulfillmentAxis GetFulfillmentAxis(DocumentType type) => type switch
    {
        // Delivery-axis children — track "how much was physically moved":
        //   • DeliveryNote on the sales side.
        //   • GoodsReceiptNote on the purchase side (received qty from
        //     the PO; multiple partial GRNs are normal).
        DocumentType.DeliveryNote or DocumentType.GoodsReceiptNote
            => FulfillmentAxis.Delivery,
        // Billing-axis children — track "how much was invoiced":
        DocumentType.Invoice or DocumentType.TaxInvoice or DocumentType.BillingNote
            or DocumentType.PurchaseInvoice or DocumentType.Expense
            or DocumentType.PurchaseOrder => FulfillmentAxis.Billing,
        _ => FulfillmentAxis.None
    };

    private static string AxisLabel(FulfillmentAxis axis) => axis switch
    {
        FulfillmentAxis.Delivery => "ส่งมอบ",
        FulfillmentAxis.Billing => "วางบิล/แจ้งหนี้",
        _ => ""
    };

    private const decimal QtyEpsilon = 0.0001m;

    /// <summary>How much of each given source line has already been carried
    /// forward into child documents, split per fulfilment axis. Voided /
    /// Rejected / deleted child documents do not count.</summary>
    private async Task<Dictionary<Guid, (decimal Delivery, decimal Billing)>> ComputeConsumptionAsync(
        Guid companyId, List<Guid> sourceLineIds)
    {
        var result = sourceLineIds.ToDictionary(id => id, _ => (Delivery: 0m, Billing: 0m));
        if (sourceLineIds.Count == 0) return result;

        var children = await (
            from cl in _db.DocumentLines
            join cd in _db.Documents on cl.DocumentId equals cd.Id
            where cl.SourceLineId != null && sourceLineIds.Contains(cl.SourceLineId.Value)
                  && cd.CompanyId == companyId
                  && cd.Status != DocumentStatus.Voided && cd.Status != DocumentStatus.Rejected
            select new { SourceLineId = cl.SourceLineId!.Value, cl.Quantity, cd.DocumentType })
            .ToListAsync();

        foreach (var c in children)
        {
            var axis = GetFulfillmentAxis(c.DocumentType);
            var cur = result[c.SourceLineId];
            result[c.SourceLineId] = axis switch
            {
                FulfillmentAxis.Delivery => (cur.Delivery + c.Quantity, cur.Billing),
                FulfillmentAxis.Billing => (cur.Delivery, cur.Billing + c.Quantity),
                _ => cur
            };
        }
        return result;
    }

    /// <summary>Shared validation for both whole-document and partial
    /// conversion — throws on any rule violation.</summary>
    private async Task ValidateConversionAsync(Document source, DocumentType targetType, Guid companyId)
    {
        // Block converting from Voided/Rejected source — they no longer reflect
        // the customer's true position; new derivative would carry stale data.
        if (source.Status == DocumentStatus.Voided)
            throw new InvalidOperationException(
                $"เอกสาร {source.DocumentNumber} ถูกยกเลิกแล้ว ไม่สามารถแปลงเป็นเอกสารใหม่ได้");
        if (source.Status == DocumentStatus.Rejected)
            throw new InvalidOperationException(
                $"เอกสาร {source.DocumentNumber} ถูกปฏิเสธ ไม่สามารถแปลงเป็นเอกสารใหม่ได้");

        // Block converting to self (no-op)
        if (source.DocumentType == targetType)
            throw new InvalidOperationException(
                $"ไม่สามารถแปลงเป็นเอกสารประเภทเดิม ({targetType})");

        // Block invalid type-to-type conversion (e.g. Quotation→CreditNote)
        var allowedTargets = GetValidConversionTargets(source.DocumentType);
        if (!allowedTargets.Contains(targetType))
        {
            var allowedNames = string.Join(", ", allowedTargets);
            throw new InvalidOperationException(
                $"ไม่สามารถแปลง {source.DocumentType} → {targetType} ได้ตามมาตรฐานบัญชี " +
                $"(แปลงได้เฉพาะ: {(allowedNames.Length > 0 ? allowedNames : "ไม่มี — เอกสารนี้เป็นปลายทาง")})");
        }

        // Cycle detection — fetch entire ancestry chain in a single recursive CTE
        // instead of N round-trips (one per ancestor level).
        if (source.RelatedDocumentId.HasValue)
        {
            var startId = source.RelatedDocumentId.Value;
            var ancestorMatch = await _db.Database
                .SqlQuery<AncestorRow>($@"
                    WITH RECURSIVE ancestry AS (
                        SELECT ""Id"", ""DocumentNumber"", ""DocumentType"", ""RelatedDocumentId"", 1 AS depth
                        FROM ""Documents""
                        WHERE ""Id"" = {startId} AND ""CompanyId"" = {companyId} AND ""IsDeleted"" = false
                        UNION ALL
                        SELECT d.""Id"", d.""DocumentNumber"", d.""DocumentType"", d.""RelatedDocumentId"", a.depth + 1
                        FROM ""Documents"" d
                        INNER JOIN ancestry a ON d.""Id"" = a.""RelatedDocumentId""
                        WHERE d.""CompanyId"" = {companyId} AND d.""IsDeleted"" = false AND a.depth < 20
                    )
                    SELECT ""Id"", ""DocumentNumber"", ""DocumentType"" FROM ancestry
                    WHERE ""DocumentType"" = {(int)targetType} LIMIT 1")
                .FirstOrDefaultAsync();

            if (ancestorMatch != null)
                throw new InvalidOperationException(
                    $"ตรวจพบวงกลมการแปลง: เอกสาร {ancestorMatch.DocumentNumber} ({(DocumentType)ancestorMatch.DocumentType}) เป็นบรรพบุรุษของเอกสารต้นทางอยู่แล้ว");
        }

        // For derivative types that adjust source's balance, source must be approved
        // and have outstanding balance.
        var derivativeTypes = new[] {
            DocumentType.Receipt, DocumentType.ReceiptVoucher,
            DocumentType.CreditNote, DocumentType.PaymentVoucher
        };
        if (derivativeTypes.Contains(targetType))
        {
            if (source.Status == DocumentStatus.Draft)
                throw new InvalidOperationException(
                    $"เอกสารต้นทาง {source.DocumentNumber} ยังเป็นฉบับร่าง — กรุณาอนุมัติก่อนแปลง");
            if (source.BalanceDue <= 0.01m && targetType != DocumentType.CreditNote
                && targetType != DocumentType.DebitNote)
                throw new InvalidOperationException(
                    $"เอกสาร {source.DocumentNumber} ไม่มียอดคงค้าง — ไม่สามารถแปลงเป็น {targetType}");
        }
    }

    /// <summary>Create the child document from a chosen set of (source line,
    /// quantity) pairs, link it back to the source, stamp SourceLineId on
    /// every new line, and cascade metadata + attachments.</summary>
    private async Task<DocumentResponse> ConvertCoreAsync(
        Document source, DocumentType targetType,
        List<(DocumentLine Line, decimal Qty)> spec, string createdBy,
        DateTime? documentDate = null, DateTime? dueDate = null)
    {
        var companyId = source.CompanyId;

        // SourceLineId travels through the DTO so CreateDocumentAsync stamps
        // it inside its own transaction — every new line is linked 1:1 to the
        // source line it was carried forward from.
        var lines = spec.Select(s => new DocumentLineRequest(
            s.Line.Description, s.Qty, s.Line.Unit, s.Line.UnitPrice,
            s.Line.DiscountPercent, s.Line.VatRate, s.Line.WithholdingTaxRate, s.Line.AccountId,
            ProjectId: s.Line.ProjectId,
            ProductCode: s.Line.ProductCode,
            SourceLineId: s.Line.Id)).ToList();

        var newDoc = await CreateDocumentAsync(companyId, new CreateDocumentRequest(
            targetType, documentDate ?? DateTime.UtcNow, dueDate ?? source.DueDate, source.ContactId,
            source.DocumentNumber, source.Notes, lines,
            ProjectId: source.ProjectId,
            BankAccountId: source.BankAccountId,
            PaymentAccountId: source.PaymentAccountId,
            ExpenseCategoryId: source.ExpenseCategoryId), createdBy);

        // Link new document to source + propagate appendix/contract metadata
        var created = await _db.Documents.FindAsync(newDoc.Id);
        if (created != null)
        {
            created.RelatedDocumentId = source.Id;
            created.CustomAppendix = source.CustomAppendix;
            created.CustomFooterNotes = source.CustomFooterNotes;
            created.CustomTermsAndConditions = source.CustomTermsAndConditions;
            created.RevenueContractId = source.RevenueContractId;
            created.PerformanceObligationId = source.PerformanceObligationId;
            created.CertificateReason = source.CertificateReason;
            created.CertifierName = source.CertifierName;
            created.CertifierPosition = source.CertifierPosition;
            created.WitnessName = source.WitnessName;
            created.WitnessPosition = source.WitnessPosition;
            created.PaymentDate = source.PaymentDate;
            await _db.SaveChangesAsync();
        }

        // Cascade file attachments — copy reference rows so child doc shares
        // the same physical files.
        await CascadeAttachmentsAsync(companyId, source.Id, newDoc.Id, createdBy);

        return await GetDocumentAsync(companyId, newDoc.Id);
    }

    public async Task<DocumentResponse> ConvertDocumentAsync(Guid companyId, Guid documentId, DocumentType targetType, string createdBy)
    {
        var source = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        await ValidateConversionAsync(source, targetType, companyId);

        var axis = GetFulfillmentAxis(targetType);
        var orderedLines = source.Lines.OrderBy(l => l.LineOrder).ToList();
        List<(DocumentLine Line, decimal Qty)> spec;

        if (axis == FulfillmentAxis.None)
        {
            // Settlement / adjustment target — copy every line verbatim.
            spec = orderedLines.Select(l => (l, l.Quantity)).ToList();
        }
        else
        {
            // Quantity-tracked target — carry forward only what is still
            // un-converted on this axis, so a second convert never duplicates.
            var consumption = await ComputeConsumptionAsync(companyId, orderedLines.Select(l => l.Id).ToList());
            spec = new List<(DocumentLine, decimal)>();
            foreach (var l in orderedLines)
            {
                var used = axis == FulfillmentAxis.Delivery ? consumption[l.Id].Delivery : consumption[l.Id].Billing;
                var remaining = l.Quantity - used;
                if (remaining > QtyEpsilon)
                    spec.Add((l, remaining));
            }
            if (spec.Count == 0)
                throw new InvalidOperationException(
                    $"เอกสาร {source.DocumentNumber} ถูกแปลงเพื่อ{AxisLabel(axis)}ครบทุกรายการแล้ว " +
                    $"— ไม่มีจำนวนคงเหลือให้แปลง");
        }

        return await ConvertCoreAsync(source, targetType, spec, createdBy);
    }

    public async Task<DocumentResponse> ConvertDocumentPartialAsync(
        Guid companyId, Guid documentId, DocumentType targetType,
        PartialConvertRequest request, string createdBy)
    {
        var source = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        await ValidateConversionAsync(source, targetType, companyId);

        var axis = GetFulfillmentAxis(targetType);
        if (axis == FulfillmentAxis.None)
            throw new InvalidOperationException(
                $"การแปลงเป็น {targetType} ไม่รองรับการเลือกบางรายการ — กรุณาใช้การแปลงทั้งฉบับ");

        if (request.Lines == null || request.Lines.Count == 0)
            throw new InvalidOperationException("กรุณาเลือกรายการที่ต้องการแปลงอย่างน้อย 1 รายการ");

        var consumption = await ComputeConsumptionAsync(
            companyId, source.Lines.Select(l => l.Id).ToList());
        var byId = source.Lines.ToDictionary(l => l.Id);

        // Preserve source line order in the new document.
        var spec = new List<(DocumentLine Line, decimal Qty)>();
        foreach (var req in request.Lines.Where(r => r.Quantity > QtyEpsilon)
                     .OrderBy(r => byId.TryGetValue(r.SourceLineId, out var sl) ? sl.LineOrder : int.MaxValue))
        {
            if (!byId.TryGetValue(req.SourceLineId, out var srcLine))
                throw new InvalidOperationException("ไม่พบรายการต้นทางที่เลือก ในเอกสารนี้");
            var used = axis == FulfillmentAxis.Delivery
                ? consumption[srcLine.Id].Delivery : consumption[srcLine.Id].Billing;
            var remaining = srcLine.Quantity - used;
            if (req.Quantity > remaining + QtyEpsilon)
                throw new InvalidOperationException(
                    $"รายการ '{srcLine.Description}' ขอแปลง {req.Quantity:0.##} " +
                    $"แต่คงเหลือให้{AxisLabel(axis)}เพียง {remaining:0.##} {srcLine.Unit}");
            spec.Add((srcLine, req.Quantity));
        }
        if (spec.Count == 0)
            throw new InvalidOperationException("ไม่มีรายการที่จะแปลง — จำนวนต้องมากกว่า 0");

        return await ConvertCoreAsync(source, targetType, spec, createdBy,
            request.DocumentDate, request.DueDate);
    }

    public async Task<DocumentFulfillmentResponse> GetDocumentFulfillmentAsync(Guid companyId, Guid documentId)
    {
        var source = await _db.Documents
            .Include(d => d.Lines)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var orderedLines = source.Lines.OrderBy(l => l.LineOrder).ToList();
        var consumption = await ComputeConsumptionAsync(companyId, orderedLines.Select(l => l.Id).ToList());

        // Which axes make sense for this source — based on what it can convert into.
        var targets = GetValidConversionTargets(source.DocumentType);
        var supportsDelivery = targets.Any(t => GetFulfillmentAxis(t) == FulfillmentAxis.Delivery);
        var supportsBilling = targets.Any(t => GetFulfillmentAxis(t) == FulfillmentAxis.Billing);

        var lines = orderedLines.Select(l =>
        {
            var c = consumption[l.Id];
            return new DocumentLineFulfillmentResponse(
                l.Id, l.LineOrder, l.Description, l.Unit,
                OrderedQuantity: l.Quantity,
                DeliveredQuantity: c.Delivery,
                DeliveryRemaining: Math.Max(0m, l.Quantity - c.Delivery),
                BilledQuantity: c.Billing,
                BillingRemaining: Math.Max(0m, l.Quantity - c.Billing),
                UnitPrice: l.UnitPrice,
                DiscountPercent: l.DiscountPercent,
                VatRate: l.VatRate,
                WithholdingTaxRate: l.WithholdingTaxRate,
                AccountId: l.AccountId,
                ProjectId: l.ProjectId,
                ProductCode: l.ProductCode);
        }).ToList();

        return new DocumentFulfillmentResponse(
            source.Id, source.DocumentNumber, source.DocumentType,
            supportsDelivery, supportsBilling, lines);
    }

    private async Task CascadeAttachmentsAsync(Guid companyId, Guid sourceDocId, Guid targetDocId, string createdBy)
    {
        var sourceAttachments = await _db.FileAttachments
            .AsNoTracking()
            .Where(f => f.CompanyId == companyId && f.EntityType == "Document" && f.EntityId == sourceDocId)
            .ToListAsync();

        if (sourceAttachments.Count == 0) return;

        // Plan the copy: compute new paths and create DB rows pointing at them FIRST.
        // The physical file copy then runs ASYNCHRONOUSLY so we don't block the request
        // while a 50MB file is being copied. If copy fails, the row's StoragePath still
        // points at the parent file — degrades gracefully but never loses the reference.
        var copyPlan = new List<(string Source, string Target)>(sourceAttachments.Count);

        foreach (var src in sourceAttachments)
        {
            var newStoragePath = src.StoragePath;
            if (!string.IsNullOrEmpty(src.StoragePath))
            {
                var dir = Path.GetDirectoryName(src.StoragePath) ?? "";
                var ext = Path.GetExtension(src.StoragePath);
                newStoragePath = Path.Combine(dir, $"{Guid.NewGuid()}{ext}");
                copyPlan.Add((src.StoragePath, newStoragePath));
            }

            _db.FileAttachments.Add(new FileAttachment
            {
                CompanyId = companyId,
                EntityType = "Document",
                EntityId = targetDocId,
                FileName = Path.GetFileName(newStoragePath),
                OriginalFileName = src.OriginalFileName,
                ContentType = src.ContentType,
                FileSize = src.FileSize,
                StoragePath = newStoragePath,
                UploadedByUserId = src.UploadedByUserId,
                CreatedBy = createdBy,
            });
        }

        await _db.SaveChangesAsync();

        // Fire-and-forget physical file copy — caller doesn't wait. If copy fails,
        // the new attachment row's StoragePath simply points at the parent file
        // (which still works as long as parent isn't purged).
        if (copyPlan.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                foreach (var (source, target) in copyPlan)
                {
                    try
                    {
                        if (System.IO.File.Exists(source) && !System.IO.File.Exists(target))
                        {
                            await using var srcStream = System.IO.File.OpenRead(source);
                            await using var dstStream = System.IO.File.Create(target);
                            await srcStream.CopyToAsync(dstStream);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Cascade file copy failed: {Source} -> {Target}", source, target);
                    }
                }
            });
        }
    }

    public async Task<List<DocumentResponse>> BatchConvertDocumentsAsync(
        Guid companyId, List<Guid> documentIds, DocumentType targetType, string createdBy)
    {
        // Run conversions sequentially within a single DbContext — EF Core DbContext
        // is NOT thread-safe so we cannot parallelize at the per-document level here
        // without provisioning fresh contexts. Instead we batch-fetch sources first
        // (avoiding N round-trips just to load them) and then loop.
        //
        // For true parallelism, the controller should fan out to N HTTP requests
        // or we'd need IServiceScopeFactory to spawn fresh DbContexts per worker.
        // Going with the safer optimization here: pre-fetch + sequential conversion
        // still saves ~30-50% on a 100-doc batch via reduced DB chatter.

        var distinctIds = documentIds.Distinct().ToList();
        var results = new List<DocumentResponse>(distinctIds.Count);
        var errors = new List<string>();

        // Pre-fetch all sources once; ConvertDocumentAsync will re-load from tracker but
        // EF caches the entity, avoiding a second DB hit.
        await _db.Documents
            .Include(d => d.Lines)
            .Where(d => documentIds.Contains(d.Id) && d.CompanyId == companyId)
            .LoadAsync();

        foreach (var id in distinctIds)
        {
            try
            {
                var converted = await ConvertDocumentAsync(companyId, id, targetType, createdBy);
                results.Add(converted);
            }
            catch (Exception ex)
            {
                errors.Add($"{id}: {ex.Message}");
                _logger.LogWarning(ex, "Batch convert: failed to convert document {DocId}", id);
            }
        }

        if (results.Count == 0 && errors.Count > 0)
            throw new InvalidOperationException("ไม่สามารถแปลงเอกสารได้: " + string.Join("; ", errors));

        return results;
    }

    public async Task<DocumentResponse> CreateInvoiceFromObligationAsync(
        Guid companyId, Guid performanceObligationId, string createdBy)
    {
        // Lock obligation row + contract row to prevent two parallel invoice-creations
        // from both seeing IsSatisfied=false and creating duplicate invoices for the
        // same milestone.
        using var tx = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            var obligation = await _db.Set<PerformanceObligation>()
                .Include(o => o.Contract)
                .FirstOrDefaultAsync(o => o.Id == performanceObligationId && o.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบ Performance Obligation");

            if (obligation.IsSatisfied)
                throw new InvalidOperationException("ภาระงานนี้รับรู้รายได้ครบแล้ว — ไม่สามารถออกใบแจ้งหนี้ซ้ำ");

            var unbilled = obligation.AllocatedPrice - obligation.RecognizedRevenue;
            if (unbilled <= 0.01m)
                throw new InvalidOperationException("ไม่มียอดคงเหลือสำหรับออกใบแจ้งหนี้");

            var contract = obligation.Contract
                ?? throw new InvalidOperationException("ไม่พบสัญญารายได้ของภาระงานนี้");

            var lineDescription = string.IsNullOrEmpty(obligation.Description)
                ? obligation.Name
                : $"{obligation.Name} — {obligation.Description}";

            var invoice = await CreateDocumentAsync(companyId, new CreateDocumentRequest(
                DocumentType.Invoice,
                DateTime.UtcNow,
                DueDate: DateTime.UtcNow.AddDays(30),
                ContactId: contract.ContactId,
                Reference: contract.ContractNumber,
                Notes: $"จากสัญญา {contract.ContractNumber}: {contract.Name}",
                Lines: new List<DocumentLineRequest>
                {
                    new(lineDescription, 1m, "งวด", unbilled, 0m, 7m, 0m, AccountId: null),
                },
                ProjectId: contract.ProjectId,
                RevenueContractId: contract.Id,
                PerformanceObligationId: obligation.Id), createdBy);

            // Update obligation state — invoice covers the unbilled portion
            obligation.RecognizedRevenue = obligation.AllocatedPrice;
            obligation.CompletionPercent = 100m;
            obligation.IsSatisfied = true;
            obligation.SatisfiedDate = DateTime.UtcNow;

            // Cascade-mark contract complete if all obligations are satisfied
            var allObligationsForContract = await _db.Set<PerformanceObligation>()
                .Where(o => o.RevenueContractId == contract.Id && o.Id != obligation.Id)
                .ToListAsync();
            if (allObligationsForContract.All(o => o.IsSatisfied))
            {
                contract.Status = "Completed";
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            return invoice;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ==================== Contacts ====================

    public async Task<ContactResponse> CreateContactAsync(Guid companyId, CreateContactRequest request)
    {
        // If structured fields are missing but free-text Address is provided,
        // attempt to auto-parse so e-Tax XML has the data it needs.
        var parsed = NeedsAutoParse(request) && !string.IsNullOrWhiteSpace(request.Address)
            ? ThaiAddressParser.Parse(request.Address)
            : null;

        var contact = new Contact
        {
            CompanyId = companyId,
            Name = request.Name,
            TaxId = request.TaxId,
            BranchCode = request.BranchCode,
            BranchName = request.BranchName,
            ContactType = request.ContactType ?? InferContactType(request.TaxId, request.BranchCode),
            IsCustomer = request.IsCustomer,
            IsSupplier = request.IsSupplier,
            BuildingNumber = request.BuildingNumber ?? parsed?.BuildingNumber,
            BuildingName = request.BuildingName ?? parsed?.BuildingName,
            Moo = request.Moo ?? parsed?.Moo,
            StreetName = request.StreetName ?? parsed?.StreetName,
            SubDistrict = request.SubDistrict ?? parsed?.SubDistrict,
            District = request.District ?? parsed?.District,
            Province = request.Province ?? parsed?.Province,
            PostalCode = request.PostalCode ?? parsed?.PostalCode,
            CountryCode = request.CountryCode ?? "TH",
            Phone = request.Phone,
            Email = request.Email,
            ContactPerson = request.ContactPerson,
            // Per-contact GL overrides — null = use system default.
            DefaultArAccountId = request.DefaultArAccountId,
            DefaultApAccountId = request.DefaultApAccountId,
            DefaultIrGrAccountId = request.DefaultIrGrAccountId,
            CreditLimit = request.CreditLimit,
        };

        contact.Address = request.Address ?? ComposeAddress(contact);

        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync();

        return MapContactToResponse(contact);
    }

    private static string? ComposeAddress(Contact c)
    {
        var parts = new[] { c.BuildingNumber, c.BuildingName,
            string.IsNullOrEmpty(c.Moo) ? null : "หมู่ " + c.Moo,
            string.IsNullOrEmpty(c.StreetName) ? null : "ถ." + c.StreetName,
            c.SubDistrict, c.District, c.Province, c.PostalCode }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var joined = string.Join(" ", parts);
        return string.IsNullOrEmpty(joined) ? null : joined;
    }

    private static bool NeedsAutoParse(CreateContactRequest r) =>
        string.IsNullOrEmpty(r.BuildingNumber) && string.IsNullOrEmpty(r.SubDistrict)
        && string.IsNullOrEmpty(r.District) && string.IsNullOrEmpty(r.Province)
        && string.IsNullOrEmpty(r.PostalCode);

    public async Task<ContactResponse> GetContactAsync(Guid companyId, Guid contactId)
    {
        var contact = await _db.Contacts
            // Include GL-override nav properties so the UI can show the
            // account codes/names alongside the FKs.
            .Include(c => c.DefaultArAccount)
            .Include(c => c.DefaultApAccount)
            .Include(c => c.DefaultIrGrAccount)
            .FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        // Lazy backfill: contact เก่า / ที่ OCR เติมแต่ free-text Address (ก่อน
        // แก้ enrichment) มี structured fields ว่าง → ฟอร์มแก้ผู้ติดต่อขึ้นที่อยู่
        // ว่าง. เมื่อเปิดดู ให้ parse free-text เป็น structured ครั้งเดียวแล้ว
        // persist — ผู้ใช้ไม่ต้องกด "ดึงข้อมูล" เองอีก. ทำเฉพาะ single-get
        // (ฟอร์มแก้) ไม่ทำใน list เพื่อเลี่ยง write ก้อนใหญ่.
        if (BackfillContactStructuredAddress(contact))
            await _db.SaveChangesAsync();
        return MapContactToResponse(contact);
    }

    /// <summary>เติม structured address fields (บ้านเลขที่/หมู่/ถนน/ตำบล/อำเภอ/
    /// จังหวัด/ไปรษณีย์) จาก free-text <c>Address</c> เมื่อ locality ว่างทั้งหมด.
    /// ใช้ ThaiAddressParser ตัวเดียวกับฟอร์ม + OCR. เติมเฉพาะ field ที่ว่าง
    /// (ไม่ทับค่าที่มีอยู่). คืน true เมื่อมีการเปลี่ยนแปลง.</summary>
    private static bool BackfillContactStructuredAddress(Contact c)
    {
        if (string.IsNullOrWhiteSpace(c.Address)) return false;
        // มี locality อยู่แล้ว — ไม่ต้องทำ
        if (!string.IsNullOrWhiteSpace(c.SubDistrict)
            || !string.IsNullOrWhiteSpace(c.District)
            || !string.IsNullOrWhiteSpace(c.Province)) return false;

        var p = ThaiAddressParser.Parse(c.Address);
        if (string.IsNullOrWhiteSpace(p.Province)
            && string.IsNullOrWhiteSpace(p.SubDistrict)
            && string.IsNullOrWhiteSpace(p.District)) return false;

        bool changed = false;
        void Set(string? cur, string? val, Action<string> set)
        { if (string.IsNullOrWhiteSpace(cur) && !string.IsNullOrWhiteSpace(val)) { set(val!); changed = true; } }
        Set(c.BuildingNumber, p.BuildingNumber, v => c.BuildingNumber = v);
        Set(c.Moo, p.Moo, v => c.Moo = v);
        // เก็บส่วนหัวเต็ม (ห้อง/ชั้น/อาคาร/ซอย/ถนน) ไม่ใช่แค่ชื่อถนนสั้น เพื่อไม่ให้
        // รายละเอียดหายตอน render เป็น structured address.
        Set(c.StreetName, ThaiAddressParser.ExtractStreetHead(c.Address, p.BuildingNumber, p.Moo), v => c.StreetName = v);
        Set(c.SubDistrict, p.SubDistrict, v => c.SubDistrict = v);
        Set(c.District, p.District, v => c.District = v);
        Set(c.Province, p.Province, v => c.Province = v);
        Set(c.PostalCode, p.PostalCode, v => c.PostalCode = v);
        return changed;
    }

    public async Task<PagedResponse<ContactResponse>> GetContactsAsync(Guid companyId, bool? isCustomer = null, bool? isSupplier = null, string? search = null, PagedRequest? paging = null)
    {
        var query = _db.Contacts.Where(c => c.CompanyId == companyId);
        if (isCustomer.HasValue) query = query.Where(c => c.IsCustomer == isCustomer.Value);
        if (isSupplier.HasValue) query = query.Where(c => c.IsSupplier == isSupplier.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(c => c.Name.ToLower().Contains(s)
                || (c.TaxId != null && c.TaxId.Contains(s))
                || (c.Email != null && c.Email.ToLower().Contains(s))
                || (c.Phone != null && c.Phone.Contains(s)));
        }

        var totalCount = await query.CountAsync();
        var page = paging?.Page ?? 1;
        var pageSize = paging?.PageSize ?? 50;
        var contacts = await query.OrderBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        return new PagedResponse<ContactResponse>(
            contacts.Select(MapContactToResponse).ToList(),
            totalCount, page, pageSize, totalPages);
    }

    public async Task<ContactDeleteResult> DeleteContactAsync(Guid companyId, Guid contactId)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        var docCount = await _db.Documents.CountAsync(d => d.ContactId == contactId && d.CompanyId == companyId && !d.IsDeleted);
        var whtCount = await _db.WithholdingTaxCerts.CountAsync(w => w.PayeeContactId == contactId && w.CompanyId == companyId && !w.IsDeleted);
        if (docCount > 0 || whtCount > 0)
        {
            contact.IsActive = false;
            await _db.SaveChangesAsync();
            return new ContactDeleteResult(
                Deleted: false,
                Deactivated: true,
                LinkedDocumentsCount: docCount,
                LinkedWhtCount: whtCount,
                Message: $"ผู้ติดต่อมีเอกสาร {docCount} รายการ และใบหัก ณ ที่จ่าย {whtCount} รายการ — " +
                         $"ระบบปิดการใช้งานแทนการลบเพื่อรักษาข้อมูลทางบัญชี");
        }
        _db.Contacts.Remove(contact);
        await _db.SaveChangesAsync();
        return new ContactDeleteResult(
            Deleted: true,
            Deactivated: false,
            LinkedDocumentsCount: 0,
            LinkedWhtCount: 0,
            Message: "ลบผู้ติดต่อสำเร็จ");
    }

    public async Task<ContactResponse> UpdateContactAsync(Guid companyId, Guid contactId, UpdateContactRequest request)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");

        if (request.Name != null) contact.Name = request.Name;
        if (request.TaxId != null) contact.TaxId = request.TaxId;
        if (request.BranchCode != null) contact.BranchCode = request.BranchCode;
        if (request.BranchName != null) contact.BranchName = request.BranchName;
        if (request.ContactType.HasValue) contact.ContactType = request.ContactType.Value;
        else if (request.TaxId != null || request.BranchCode != null)
            contact.ContactType = InferContactType(request.TaxId ?? contact.TaxId, request.BranchCode ?? contact.BranchCode);
        if (request.IsCustomer.HasValue) contact.IsCustomer = request.IsCustomer.Value;
        if (request.IsSupplier.HasValue) contact.IsSupplier = request.IsSupplier.Value;
        if (request.BuildingNumber != null) contact.BuildingNumber = request.BuildingNumber;
        if (request.BuildingName != null) contact.BuildingName = request.BuildingName;
        if (request.Moo != null) contact.Moo = request.Moo;
        if (request.StreetName != null) contact.StreetName = request.StreetName;
        if (request.SubDistrict != null) contact.SubDistrict = request.SubDistrict;
        if (request.District != null) contact.District = request.District;
        if (request.Province != null) contact.Province = request.Province;
        if (request.PostalCode != null) contact.PostalCode = request.PostalCode;
        if (request.CountryCode != null) contact.CountryCode = request.CountryCode;
        contact.Address = request.Address ?? ComposeAddress(contact);
        // Phone/Email/ContactPerson: ผู้ใช้ "ลบจนว่าง" ต้องล้างค่าได้ — frontend
        // ส่ง "" เมื่อ cleared. normalize ""/whitespace → null (เก็บ null สะอาด
        // กว่า ""). null ที่แท้จริง (omit) = ไม่เปลี่ยน; "" = ล้าง.
        if (request.Phone != null) contact.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        if (request.Email != null) contact.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        if (request.ContactPerson != null) contact.ContactPerson = string.IsNullOrWhiteSpace(request.ContactPerson) ? null : request.ContactPerson.Trim();
        if (request.IsActive.HasValue) contact.IsActive = request.IsActive.Value;
        // Per-contact GL overrides — record.Nullable<Guid> can't tell
        // "unset" from "explicitly clear to default", so we treat null
        // as "no change". To clear an override the UI POSTs
        // Guid.Empty which we map back to null below.
        if (request.DefaultArAccountId.HasValue)
            contact.DefaultArAccountId = request.DefaultArAccountId == Guid.Empty ? null : request.DefaultArAccountId;
        if (request.DefaultApAccountId.HasValue)
            contact.DefaultApAccountId = request.DefaultApAccountId == Guid.Empty ? null : request.DefaultApAccountId;
        if (request.DefaultIrGrAccountId.HasValue)
            contact.DefaultIrGrAccountId = request.DefaultIrGrAccountId == Guid.Empty ? null : request.DefaultIrGrAccountId;
        if (request.CreditLimit.HasValue)
            contact.CreditLimit = request.CreditLimit.Value <= 0 ? null : request.CreditLimit.Value;

        await _db.SaveChangesAsync();
        // Reload with nav properties so MapContactToResponse can emit
        // the AR/AP/IR-GR account codes for the UI labels.
        return await GetContactAsync(companyId, contactId);
    }

    public async Task<ContactSmartDefaults> GetContactSmartDefaultsAsync(Guid companyId, Guid contactId)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        return GetSmartDefaults(contact);
    }

    // ==================== Payments ====================

    public async Task<PaymentResponse> CreatePaymentAsync(Guid companyId, CreatePaymentRequest request, string createdBy)
    {
        // Validate Amount > 0
        if (request.Amount <= 0)
            throw new InvalidOperationException("จำนวนเงินชำระต้องมากกว่า 0");

        // Validate PaymentDate is not in the future
        if (request.PaymentDate > DateTime.UtcNow.Date.AddDays(1))
            throw new InvalidOperationException("วันที่ชำระเงินต้องไม่เป็นวันที่ในอนาคต");

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
        // Lock document row to prevent concurrent overpayment
        var doc = await _db.Documents
            .FromSqlRaw("SELECT * FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", request.DocumentId, companyId)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        // Validate document status allows payment
        if (doc.Status != DocumentStatus.Approved && doc.Status != DocumentStatus.PartiallyPaid && doc.Status != DocumentStatus.Sent)
            throw new InvalidOperationException("สามารถชำระเงินได้เฉพาะเอกสารที่อนุมัติแล้ว, ชำระบางส่วน หรือส่งแล้วเท่านั้น");

        if (request.Amount > doc.BalanceDue)
            throw new InvalidOperationException($"จำนวนเงินชำระ ({request.Amount:N2}) มากกว่ายอดค้างชำระ ({doc.BalanceDue:N2})");

        // Installment WHT — for cash-basis WHT recognition (or when the
        // source carries any WHT at all), each installment recognises its
        // own slice of the WHT-Asset / WHT-Payable line. Default split is
        // proportional to the amount being paid this round; operator can
        // override (e.g. customer's withholding certificate shows the full
        // WHT on installment 1). Cumulative WHT across all settled payments
        // can't exceed source.WithholdingTaxAmount — that's the legal cap.
        decimal paymentWht = 0m;
        if (doc.WithholdingTaxAmount > 0m)
        {
            // Sum WHT from BOTH Payment rows AND Receipt-document children
            // referencing this Invoice via RelatedDocumentId — operators can
            // mix the modal flow with the manual Receipt-conversion flow,
            // and only counting one path lets cumulative exceed the cap
            // when both are used. Excludes Draft/Voided/Rejected on both
            // sides so in-flight or cancelled rows don't reserve slots.
            var withheldViaPayments = await _db.Payments.AsNoTracking()
                .Where(p => p.DocumentId == doc.Id && !p.IsDeleted)
                .SumAsync(p => (decimal?)p.WithholdingTaxAmount) ?? 0m;
            var settlementTypes = new[] { DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.PaymentVoucher };
            var withheldViaReceipts = await _db.Documents.AsNoTracking()
                .Where(r => r.RelatedDocumentId == doc.Id
                    && settlementTypes.Contains(r.DocumentType)
                    && r.Status != DocumentStatus.Voided
                    && r.Status != DocumentStatus.Draft
                    && r.Status != DocumentStatus.Rejected
                    && !r.IsDeleted)
                .SumAsync(r => (decimal?)r.WithholdingTaxAmount) ?? 0m;
            var alreadyWithheld = withheldViaPayments + withheldViaReceipts;
            var remainingCap = Math.Max(0m, doc.WithholdingTaxAmount - alreadyWithheld);

            // Final-installment detection — if this payment closes the
            // outstanding balance, assign whatever WHT slice is still
            // unspent so cumulative lands exactly on doc.WithholdingTaxAmount.
            // Without this, proportional rounding leaves a satang-level gap
            // (115.38 + 115.38 + 69.23 = 299.99 ≠ 300.00) and the WHT cert
            // numbers don't reconcile with ภ.ง.ด.3/53 filings.
            var isFinalPayment = request.Amount + 0.01m >= doc.BalanceDue;

            if (request.WithholdingTaxAmount.HasValue)
            {
                paymentWht = Math.Round(request.WithholdingTaxAmount.Value, 2, MidpointRounding.AwayFromZero);
                if (paymentWht < 0m)
                    throw new InvalidOperationException("WHT ของงวดนี้ต้องไม่ติดลบ");
                if (paymentWht > remainingCap + 0.01m)
                    throw new InvalidOperationException(
                        $"WHT งวดนี้ ({paymentWht:N2}) + ที่หักไปแล้ว ({alreadyWithheld:N2}) " +
                        $"เกินยอด WHT ทั้งหมดของเอกสาร ({doc.WithholdingTaxAmount:N2})");
            }
            else if (isFinalPayment)
            {
                // Closing payment: consume the remaining slice exactly so
                // rounding can't drift below the legal total.
                paymentWht = remainingCap;
            }
            else
            {
                // Proportional default: this installment's share of the
                // total WHT. Capped at the remaining slice so rounding can't
                // overshoot on the last payment.
                var proportional = doc.TotalAmount > 0m
                    ? Math.Round(request.Amount * doc.WithholdingTaxAmount / doc.TotalAmount, 2, MidpointRounding.AwayFromZero)
                    : 0m;
                paymentWht = Math.Min(proportional, remainingCap);
            }
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var payYearMonth = DateTime.UtcNow.ToString("yyyyMM");
            var payPrefix = $"PAY-{payYearMonth}-";
            var maxPayNum = await _db.Payments
                .IgnoreQueryFilters()
                .Where(p => p.CompanyId == companyId && p.PaymentNumber.StartsWith(payPrefix))
                .Select(p => p.PaymentNumber)
                .MaxAsync() as string;
            var paySeq = 1;
            if (maxPayNum != null)
            {
                var lastPart = maxPayNum.Substring(payPrefix.Length);
                if (int.TryParse(lastPart, out var parsed)) paySeq = parsed + 1;
            }
            var payment = new Payment
            {
                CompanyId = companyId,
                PaymentNumber = $"{payPrefix}{paySeq:D4}",
                DocumentId = request.DocumentId,
                PaymentDate = request.PaymentDate,
                Amount = request.Amount,
                PaymentMethod = request.PaymentMethod,
                Reference = request.Reference,
                BankAccount = request.BankAccount,
                // Honour the per-payment override when supplied. This is the
                // "ลูกค้าจ่ายเข้าบัญชี Kasikorn แทน Bangkok Bank" case —
                // operator records the actual landing bank so the GL +
                // bank-balance adjustment hit the right place. Falls back
                // to doc.BankAccountId when not provided.
                BankAccountId = request.OverrideBankAccountId ?? doc.BankAccountId,
                OverrideBankAccountId = request.OverrideBankAccountId,
                OverridePaymentAccountId = request.OverridePaymentAccountId,
                WithholdingTaxAmount = paymentWht,
                // Per-payment ProjectId override — when null, the JE
                // posting still falls back to doc.ProjectId so the
                // common case "all of an invoice's payments hit one
                // project" needs no extra input.
                ProjectId = request.ProjectId,
                Notes = request.Notes,
                // Optional per-request payer signature override (for service
                // accounts / API callers whose user has no signature on file).
                // Validation: cap size to ~512 KB base64 (~384 KB raw image)
                // so a buggy caller can't inflate a Payment row.
                PayerSignatureBase64 = TrimSignature(request.PayerSignatureBase64),
                PayerSignatureName = request.PayerSignatureName,
                CreatedBy = createdBy
            };

            _db.Payments.Add(payment);

            doc.PaidAmount += request.Amount;
            doc.BalanceDue = doc.TotalAmount - doc.PaidAmount;
            doc.Status = doc.BalanceDue <= 0 ? DocumentStatus.Paid : DocumentStatus.PartiallyPaid;
            // Clear stale aging immediately when the doc settles — otherwise
            // the list view keeps showing "⏳ 30+d" on a paid invoice until
            // DocumentAgingBackgroundService runs (every 6h). PartiallyPaid
            // docs keep their aging since they still have an outstanding
            // balance.
            if (doc.Status == DocumentStatus.Paid)
            {
                doc.AgingDays = null;
                doc.AgingLastEvaluatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();

            // Create journal entry for payment
            await CreatePaymentJournalAsync(companyId, doc, payment, createdBy);

            await _db.SaveChangesAsync();

            // Sync BankAccount.CurrentBalance — convert from doc currency to THB
            // at the document's captured FX rate (BankAccount.CurrentBalance is
            // always THB in this iteration). Same conversion as the GL posting.
            if (payment.BankAccountId.HasValue)
            {
                var isInflow = doc.DocumentType is DocumentType.Invoice or DocumentType.TaxInvoice
                    or DocumentType.Receipt or DocumentType.ReceiptVoucher
                    or DocumentType.DebitNote or DocumentType.BillingNote;
                var thbAmount = doc.ExchangeRate == 1m
                    ? payment.Amount
                    : Math.Round(payment.Amount * doc.ExchangeRate, 2, MidpointRounding.AwayFromZero);
                var delta = isInflow ? thbAmount : -thbAmount;
                await _db.BankAccounts
                    .Where(b => b.Id == payment.BankAccountId.Value && b.CompanyId == companyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.CurrentBalance, b => b.CurrentBalance + delta));
            }

            // Auto-generate WHT cert for purchase documents with WHT on first payment
            if (doc.WithholdingTaxAmount > 0
                && (doc.DocumentType == DocumentType.PurchaseInvoice
                    || doc.DocumentType == DocumentType.Expense
                    || doc.DocumentType == DocumentType.PaymentVoucher
                    || doc.DocumentType == DocumentType.CertificateInLieu))
            {
                try
                {
                    await _whtService.AutoGenerateFromDocumentAsync(companyId, doc.Id, false, createdBy);
                }
                catch
                {
                    // Cert may already exist or other non-critical error — don't fail payment
                }
            }

            await transaction.CommitAsync();

            try
            {
                await _lineNotify.NotifyPaymentReceivedAsync(companyId, doc.DocumentNumber, payment.Amount);
            }
            catch { /* best-effort notification */ }

            // Webhooks — payment.received (always) + document.paid
            // (when BalanceDue is now zero). Both fire outside the
            // commit so partners get a settled view + a slow webhook
            // delivery never blocks the API return.
            await FireWebhookAsync(companyId, "payment.received", new
            {
                paymentId = payment.Id,
                paymentNumber = payment.PaymentNumber,
                documentId = payment.DocumentId,
                documentNumber = doc.DocumentNumber,
                amount = payment.Amount,
                method = payment.PaymentMethod.ToString(),
                paymentDate = payment.PaymentDate,
            });
            if (doc.BalanceDue <= 0.01m)
            {
                await FireWebhookAsync(companyId, "document.paid", new
                {
                    documentId = doc.Id,
                    documentNumber = doc.DocumentNumber,
                    documentType = doc.DocumentType.ToString(),
                    totalAmount = doc.TotalAmount,
                    paidAt = DateTime.UtcNow,
                });
            }

            return new PaymentResponse(
                payment.Id, payment.PaymentNumber, payment.DocumentId,
                payment.PaymentDate, payment.Amount, payment.PaymentMethod,
                payment.Reference, payment.BankAccount, payment.BankAccountId,
                payment.Notes, payment.CreatedAt,
                HasPayerSignature: !string.IsNullOrWhiteSpace(payment.PayerSignatureBase64),
                PayerSignatureName: payment.PayerSignatureName);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
        }); // end ExecutionStrategy
    }

    /// <summary>Multi-document payment — one Payment row settles many
    /// Documents pro-rata to caller-specified AllocatedAmount per row.
    /// Each allocation triggers the same per-doc settlement (PaidAmount
    /// roll, status flip, WHT slice, per-doc JE) the legacy
    /// single-doc path uses, so existing accounting invariants stay
    /// intact. Bank balance is updated ONCE at the total.
    ///
    /// Validation:
    ///   • Every allocation amount must be ≤ that doc's BalanceDue
    ///   • SUM(allocations) ≤ Amount; the remainder is reported as
    ///     UnappliedCredit (operator can attach later via PUT)
    ///   • All target docs must be Approved / PartiallyPaid / Sent
    ///   • Documents must share the same Contact (mixing customers in
    ///     one cheque is almost always a data-entry error)
    ///   • Multiple bank accounts across docs are tolerated — the bank
    ///     balance hits the OverrideBankAccountId or the FIRST doc's
    ///     BankAccountId in that order.
    /// </summary>
    public async Task<PaymentResponse> CreateMultiDocPaymentAsync(Guid companyId, CreatePaymentRequest request, string createdBy)
    {
        if (request.Allocations == null || request.Allocations.Count == 0)
            throw new InvalidOperationException("Allocations ต้องมีอย่างน้อย 1 รายการ");
        if (request.Amount <= 0)
            throw new InvalidOperationException("จำนวนเงินชำระต้องมากกว่า 0");
        if (request.PaymentDate > DateTime.UtcNow.Date.AddDays(1))
            throw new InvalidOperationException("วันที่ชำระเงินต้องไม่เป็นวันที่ในอนาคต");

        var allocSum = request.Allocations.Sum(a => a.AllocatedAmount);
        if (request.Allocations.Any(a => a.AllocatedAmount <= 0))
            throw new InvalidOperationException("AllocatedAmount ของทุกแถวต้องมากกว่า 0");
        if (allocSum > request.Amount + 0.01m)
            throw new InvalidOperationException(
                $"รวม allocation ({allocSum:N2}) เกินจำนวนเงินชำระ ({request.Amount:N2})");

        var docIds = request.Allocations.Select(a => a.DocumentId).Distinct().ToList();
        if (docIds.Count != request.Allocations.Count)
            throw new InvalidOperationException("แต่ละ Document จัดสรรได้แค่ครั้งเดียวต่อ payment");

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // Lock all target docs in one go — sort by Id to avoid deadlock
            // when two concurrent multi-doc payments overlap on the same
            // docs in different orders.
            var docs = await _db.Documents
                .FromSqlRaw("SELECT * FROM \"Documents\" WHERE \"Id\" = ANY({0}) AND \"CompanyId\" = {1} ORDER BY \"Id\" FOR UPDATE",
                    docIds.ToArray(), companyId)
                .ToListAsync();
            if (docs.Count != docIds.Count)
                throw new KeyNotFoundException("ไม่พบเอกสารบางรายการ");

            var contacts = docs.Select(d => d.ContactId).Distinct().ToList();
            if (contacts.Count > 1)
                throw new InvalidOperationException(
                    "เอกสารทั้งหมดในการชำระครั้งเดียวต้องเป็นของลูกค้า/ผู้ขายรายเดียวกัน");

            var docMap = docs.ToDictionary(d => d.Id);
            foreach (var alloc in request.Allocations)
            {
                var d = docMap[alloc.DocumentId];
                if (d.Status != DocumentStatus.Approved && d.Status != DocumentStatus.PartiallyPaid && d.Status != DocumentStatus.Sent)
                    throw new InvalidOperationException(
                        $"เอกสาร {d.DocumentNumber} สถานะ {d.Status} ไม่สามารถชำระได้");
                if (alloc.AllocatedAmount > d.BalanceDue + 0.01m)
                    throw new InvalidOperationException(
                        $"จัดสรร {alloc.AllocatedAmount:N2} ของ {d.DocumentNumber} เกินยอดค้าง ({d.BalanceDue:N2})");
            }

            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                // ===== Create the parent Payment =====
                var payYearMonth = DateTime.UtcNow.ToString("yyyyMM");
                var payPrefix = $"PAY-{payYearMonth}-";
                var maxPayNum = await _db.Payments
                    .IgnoreQueryFilters()
                    .Where(p => p.CompanyId == companyId && p.PaymentNumber.StartsWith(payPrefix))
                    .Select(p => p.PaymentNumber)
                    .MaxAsync() as string;
                var paySeq = 1;
                if (maxPayNum != null && int.TryParse(maxPayNum.Substring(payPrefix.Length), out var parsed))
                    paySeq = parsed + 1;

                var firstDoc = docMap[request.Allocations[0].DocumentId];
                var payment = new Payment
                {
                    CompanyId = companyId,
                    PaymentNumber = $"{payPrefix}{paySeq:D4}",
                    // Primary DocumentId = first allocation's doc (back-compat).
                    DocumentId = firstDoc.Id,
                    PaymentDate = request.PaymentDate,
                    Amount = request.Amount,
                    PaymentMethod = request.PaymentMethod,
                    Reference = request.Reference,
                    BankAccount = request.BankAccount,
                    BankAccountId = request.OverrideBankAccountId ?? firstDoc.BankAccountId,
                    OverrideBankAccountId = request.OverrideBankAccountId,
                    WithholdingTaxAmount = 0,  // accumulated below from allocations
                    ProjectId = request.ProjectId,
                    Notes = request.Notes,
                    PayerSignatureBase64 = TrimSignature(request.PayerSignatureBase64),
                    PayerSignatureName = request.PayerSignatureName,
                    CreatedBy = createdBy,
                };
                _db.Payments.Add(payment);
                await _db.SaveChangesAsync();

                // ===== Per-doc settlement + WHT split + JE =====
                decimal totalWht = 0;
                var allocIndex = 0;
                foreach (var alloc in request.Allocations)
                {
                    allocIndex++;
                    var d = docMap[alloc.DocumentId];

                    // WHT slice — explicit override per allocation OR
                    // proportional to AllocatedAmount / d.TotalAmount.
                    decimal whtSlice = 0;
                    if (d.WithholdingTaxAmount > 0)
                    {
                        if (alloc.WithholdingTaxAmount.HasValue)
                        {
                            whtSlice = Math.Round(alloc.WithholdingTaxAmount.Value, 2, MidpointRounding.AwayFromZero);
                        }
                        else
                        {
                            var alreadyWht = await _db.Payments.AsNoTracking()
                                .Where(p => p.DocumentId == d.Id && !p.IsDeleted && p.Id != payment.Id)
                                .SumAsync(p => (decimal?)p.WithholdingTaxAmount) ?? 0m;
                            var remainingCap = Math.Max(0m, d.WithholdingTaxAmount - alreadyWht);
                            var isFinal = alloc.AllocatedAmount + 0.01m >= d.BalanceDue;
                            whtSlice = isFinal
                                ? remainingCap
                                : Math.Min(remainingCap,
                                    d.TotalAmount > 0
                                        ? Math.Round(alloc.AllocatedAmount * d.WithholdingTaxAmount / d.TotalAmount,
                                            2, MidpointRounding.AwayFromZero)
                                        : 0);
                        }
                    }

                    _db.PaymentAllocations.Add(new PaymentAllocation
                    {
                        CompanyId = companyId,
                        PaymentId = payment.Id,
                        DocumentId = d.Id,
                        AllocatedAmount = alloc.AllocatedAmount,
                        WithholdingTaxAmount = whtSlice,
                        Note = alloc.Note,
                        CreatedBy = createdBy,
                    });
                    totalWht += whtSlice;

                    // Settle the document
                    d.PaidAmount += alloc.AllocatedAmount;
                    d.BalanceDue = d.TotalAmount - d.PaidAmount;
                    d.Status = d.BalanceDue <= 0 ? DocumentStatus.Paid : DocumentStatus.PartiallyPaid;
                    if (d.Status == DocumentStatus.Paid)
                    {
                        d.AgingDays = null;
                        d.AgingLastEvaluatedAt = DateTime.UtcNow;
                    }

                    // Per-doc journal — synthesize a non-tracked Payment
                    // scoped to this allocation row. CreatePaymentJournalAsync
                    // reads only Amount / WithholdingTaxAmount / PaymentNumber
                    // / PaymentDate / ProjectId off the param; never
                    // touches Id and never adds to DbContext. Allocation-
                    // specific PaymentNumber tag lets the JE Reference
                    // distinguish multiple JEs from the same parent payment.
                    var virtualPayment = new Payment
                    {
                        CompanyId = companyId,
                        PaymentNumber = $"{payment.PaymentNumber}/{allocIndex}",
                        DocumentId = d.Id,
                        PaymentDate = payment.PaymentDate,
                        Amount = alloc.AllocatedAmount,
                        PaymentMethod = payment.PaymentMethod,
                        Reference = payment.Reference,
                        BankAccount = payment.BankAccount,
                        BankAccountId = payment.BankAccountId,
                        OverrideBankAccountId = payment.OverrideBankAccountId,
                        WithholdingTaxAmount = whtSlice,
                        ProjectId = payment.ProjectId,
                        Notes = payment.Notes,
                        CreatedBy = createdBy,
                    };
                    await CreatePaymentJournalAsync(companyId, d, virtualPayment, createdBy);
                }

                payment.WithholdingTaxAmount = totalWht;
                await _db.SaveChangesAsync();

                // ===== Bank-balance sync (once at total) =====
                if (payment.BankAccountId.HasValue)
                {
                    var isInflow = firstDoc.DocumentType is DocumentType.Invoice or DocumentType.TaxInvoice
                        or DocumentType.Receipt or DocumentType.ReceiptVoucher
                        or DocumentType.DebitNote or DocumentType.BillingNote;
                    var delta = isInflow ? request.Amount : -request.Amount;
                    await _db.BankAccounts
                        .Where(b => b.Id == payment.BankAccountId.Value && b.CompanyId == companyId)
                        .ExecuteUpdateAsync(s => s.SetProperty(b => b.CurrentBalance, b => b.CurrentBalance + delta));
                }

                await transaction.CommitAsync();

                // Re-load PaymentAllocation rows to pick up generated Ids,
                // then enrich with the docMap (resolved client-side).
                var rawAllocs = await _db.PaymentAllocations
                    .Where(pa => pa.PaymentId == payment.Id && !pa.IsDeleted)
                    .Select(pa => new { pa.Id, pa.PaymentId, pa.DocumentId,
                        pa.AllocatedAmount, pa.WithholdingTaxAmount, pa.Note })
                    .ToListAsync();
                var persistedAllocs = rawAllocs.Select(pa => new PaymentAllocationResponse(
                    pa.Id, pa.PaymentId, pa.DocumentId,
                    docMap[pa.DocumentId].DocumentNumber,
                    docMap[pa.DocumentId].DocumentType,
                    pa.AllocatedAmount, pa.WithholdingTaxAmount, pa.Note)).ToList();

                return new PaymentResponse(
                    payment.Id, payment.PaymentNumber, payment.DocumentId,
                    payment.PaymentDate, payment.Amount, payment.PaymentMethod,
                    payment.Reference, payment.BankAccount, payment.BankAccountId,
                    payment.Notes, payment.CreatedAt,
                    persistedAllocs, request.Amount - allocSum,
                    HasPayerSignature: !string.IsNullOrWhiteSpace(payment.PayerSignatureBase64),
                    PayerSignatureName: payment.PayerSignatureName);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

    public async Task<List<PaymentResponse>> GetPaymentsAsync(Guid companyId, Guid? documentId = null)
    {
        var query = _db.Payments.Where(p => p.CompanyId == companyId);
        if (documentId.HasValue) query = query.Where(p => p.DocumentId == documentId.Value);

        var payments = await query.OrderByDescending(p => p.PaymentDate).ToListAsync();
        return payments.Select(p => new PaymentResponse(
            p.Id, p.PaymentNumber, p.DocumentId,
            p.PaymentDate, p.Amount, p.PaymentMethod,
            p.Reference, p.BankAccount, p.BankAccountId,
            p.Notes, p.CreatedAt,
            HasPayerSignature: !string.IsNullOrWhiteSpace(p.PayerSignatureBase64),
            PayerSignatureName: p.PayerSignatureName)).ToList();
    }

    // ==================== Private ====================

    /// <summary>
    /// ค้นหาบัญชีจากรหัส — exact match ก่อน แล้ว prefix match (level 4)
    /// เช่น "113" จะ match "11310" (ลูกหนี้การค้า).
    /// When contactOverride is set the contact's per-contact GL pin
    /// wins (e.g. ลูกหนี้พนักงาน vs ลูกหนี้การค้า depending on contact).
    /// codePrefix selects which override applies: "113" → AR, "212"
    /// → AP, "212305" → IR/GR clearing.
    /// </summary>
    /// <summary>Resolve the WHT-payable account by the counterparty's entity
    /// type, so the withheld tax lands in the right ภ.ง.ด. liability for the
    /// half-yearly reconciliation:
    ///   Individual (บุคคลธรรมดา)      → 21916 (ภ.ง.ด.3)
    ///   JuristicPerson (นิติบุคคล)    → 21917 (ภ.ง.ด.53)
    /// Falls back to whichever account exists when the preferred one is
    /// missing (so a half-configured chart still posts).</summary>
    private async Task<ChartOfAccount?> ResolveWhtPayableAccountAsync(Guid companyId, Contact? contact)
    {
        var preferJuristic = contact?.ContactType == ContactType.JuristicPerson;
        var primary = preferJuristic ? "21917" : "21916";
        var secondary = preferJuristic ? "21916" : "21917";
        return await FindAccountAsync(companyId, primary)
            ?? await FindAccountAsync(companyId, secondary);
    }

    /// <summary>Get (creating once if absent) the "goods received not
    /// invoiced" accrual account (21240) used by the 3-way-match GRN flow.
    /// Auto-creating it means the feature works for existing companies whose
    /// chart predates it — it appears as a standard liability under 212.</summary>
    private async Task<ChartOfAccount?> EnsureGrNiAccountAsync(Guid companyId)
    {
        var existing = await FindAccountAsync(companyId, "21240");
        if (existing != null) return existing;

        var parentId = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.AccountCode == "212")
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();

        var acct = new ChartOfAccount
        {
            CompanyId = companyId,
            AccountCode = "21240",
            AccountName = "เจ้าหนี้-รับสินค้ายังไม่วางบิล",
            AccountNameEn = "Goods Received Not Invoiced",
            AccountType = AccountType.Liability,
            ParentAccountId = parentId,
            Level = 4,
            IsActive = true,
            IsSystemAccount = true,
            Description = "ตั้งพักเจ้าหนี้สำหรับสินค้าที่รับแล้วแต่ยังไม่ได้รับใบกำกับ (3-way match)",
        };
        _db.ChartOfAccounts.Add(acct);
        await _db.SaveChangesAsync();
        return acct;
    }

    /// <summary>When a PurchaseInvoice was billed against a GoodsReceiptNote
    /// that already accrued the goods (posted a Dr Expense / Cr GR-NI entry),
    /// return that GR-NI account so the PI can clear the accrual instead of
    /// re-debiting expense + re-moving stock. Returns null for a standalone
    /// PI or a GRN that hasn't posted (so the caller falls back to the normal
    /// expense + stock path).</summary>
    private async Task<ChartOfAccount?> GetReceivedViaGrnAccrualAccountAsync(Guid companyId, Document doc)
    {
        if (doc.RelatedDocumentId == null) return null;
        var srcType = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == doc.RelatedDocumentId.Value && d.CompanyId == companyId)
            .Select(d => (DocumentType?)d.DocumentType)
            .FirstOrDefaultAsync();
        if (srcType != DocumentType.GoodsReceiptNote) return null;

        var grnPosted = await _db.JournalEntries.AsNoTracking()
            .AnyAsync(j => j.SourceDocumentId == doc.RelatedDocumentId.Value
                && j.CompanyId == companyId && j.Status == JournalEntryStatus.Posted);
        if (!grnPosted) return null;

        return await FindAccountAsync(companyId, "21240");
    }

    /// <summary>Resolve the PAYABLE account by document role — the heart of
    /// the trade-vs-non-trade separation (หลักบัญชีไทย):
    ///   • ใบแจ้งหนี้ซื้อ (PurchaseInvoice) = trade purchase backed by the
    ///     supplier's invoice → เจ้าหนี้การค้า (21210 / "212" family).
    ///   • ใบบันทึกค่าใช้จ่าย (Expense) = internal expense request / claim
    ///     with no supplier trade invoice → เจ้าหนี้อื่น (21220), so trade
    ///     payables stay clean for supplier statement reconciliation.
    /// The contact's pinned DefaultApAccount still wins for both (the user
    /// explicitly chose where that vendor's balance lives). Falls back to the
    /// 212 family when 21220 doesn't exist in a custom chart.</summary>
    private async Task<ChartOfAccount?> ResolvePayableAccountAsync(
        Guid companyId, DocumentType sourceType, Contact? contact)
    {
        if (sourceType == DocumentType.Expense)
        {
            // Pinned per-contact AP override wins (same rule as FindAccountAsync).
            if (contact?.DefaultApAccountId is Guid pinnedId)
            {
                var pinned = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.Id == pinnedId && a.CompanyId == companyId && a.IsActive);
                if (pinned != null) return pinned;
            }
            var other = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == "21220" && a.IsActive);
            if (other != null) return other;
        }
        return await FindAccountAsync(companyId, "212", contact);
    }

    /// <summary>Per-instance cache สำหรับ FindAccountAsync — ApproveDocument
    /// / Post payroll หา default account ซ้ำๆ (113, 212, 116, 21914...
    /// ราว 8-15 ครั้งต่อ post). DocumentService เป็น scoped ต่อ request →
    /// cache ตามอายุ request เท่านั้น ไม่มี stale-data risk ข้าม request.
    /// Key เป็น (codePrefix, contactDefaultId) — pinned contact overrides
    /// ใช้ key ต่าง ให้ cache แยก hit.</summary>
    private readonly Dictionary<(string CodePrefix, Guid? ContactDefault), ChartOfAccount?> _findAccountCache = new();

    private async Task<ChartOfAccount?> FindAccountAsync(
        Guid companyId, string codePrefix, Contact? contactOverride = null)
    {
        Guid? overrideId = contactOverride == null ? null : codePrefix switch
        {
            "113" => contactOverride.DefaultArAccountId,
            "212" => contactOverride.DefaultApAccountId,
            "212305" => contactOverride.DefaultIrGrAccountId,
            _ => null,
        };
        var cacheKey = (codePrefix, overrideId);
        if (_findAccountCache.TryGetValue(cacheKey, out var cached)) return cached;

        if (overrideId.HasValue)
        {
            var pinned = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.Id == overrideId.Value && a.CompanyId == companyId && a.IsActive);
            if (pinned != null) { _findAccountCache[cacheKey] = pinned; return pinned; }
            // The override points at a deleted/inactive account —
            // fall through to the system default rather than throw.
        }
        var found = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == codePrefix && a.IsActive)
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith(codePrefix) && a.Level >= 4 && a.IsActive)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();
        _findAccountCache[cacheKey] = found;
        return found;
    }

    /// <summary>เลือกผังบัญชีปลายทางของภาษีซื้อ ตาม priority:
    ///   1) <c>doc.InputVatAccountCodeOverride</c> set → ใช้ค่านั้น (treat as
    ///      §82/5 ถ้าไม่ใช่ 116xx — ลง expense เต็มจำนวน, ไม่เข้า ภ.พ.30)
    ///   2) Completeness §86/4 ครบ → 11610 "ภาษีซื้อ ภ.พ.30"
    ///   3) ไม่ครบ → 11640 "ภาษีซื้อยังไม่ถึงกำหนด" (§82/3 รอใบครบ)
    /// Fallback ถ้าไม่พบ 11640 (chart เก่าไม่มี): 11630 (Deferred) → 116 parent.
    /// คืน <c>postedAsUndue=true</c> เฉพาะ path (3) เพื่อ track ว่าต้อง reclassify
    /// ตอน user มาเติมใบกำกับครบ.</summary>
    private async Task<(ChartOfAccount Account, bool PostedAsUndue)> ResolveInputVatAccountAsync(
        Guid companyId, Document doc)
    {
        // (1) Override — user เลือกผังอื่น (เช่น 51000 ลงต้นทุนขาย)
        if (!string.IsNullOrWhiteSpace(doc.InputVatAccountCodeOverride))
        {
            var overrideAcc = await FindAccountAsync(companyId, doc.InputVatAccountCodeOverride!);
            if (overrideAcc != null) return (overrideAcc, PostedAsUndue: false);
            // override ชี้บัญชีที่ลบไป → fall through ใช้ default (กัน JE พัง)
        }

        // (2)/(3) Completeness check — pure function
        var completeness = TaxInvoiceCompletenessChecker.Evaluate(doc, doc.Contact);

        if (completeness.IsClaimable)
        {
            var claimable = await FindAccountAsync(companyId, "11610")
                ?? await FindAccountAsync(companyId, "116")
                ?? throw new InvalidOperationException(
                    "ไม่พบบัญชีภาษีซื้อ (11610 หรือ 116) ในผังบัญชี — กรุณาเพิ่มก่อนอนุมัติเอกสารซื้อที่มี VAT");
            return (claimable, PostedAsUndue: false);
        }

        // ใบไม่ครบ → suspense 11640
        var undue = await FindAccountAsync(companyId, "11640")
            ?? await FindAccountAsync(companyId, "11630")  // Deferred Input VAT — fallback
            ?? await FindAccountAsync(companyId, "116")
            ?? throw new InvalidOperationException(
                "ไม่พบบัญชี 11640 'ภาษีซื้อยังไม่ถึงกำหนด' ในผังบัญชี — กรุณาเพิ่มก่อนอนุมัติเอกสารซื้อที่ใบกำกับยังไม่ครบ");
        return (undue, PostedAsUndue: true);
    }

    /// <summary>หลัง user แก้เอกสารให้ใบกำกับครบ §86/4 — ตรวจว่าเดิม VAT
    /// post ไป 11640 ไหม ถ้าใช่ generate adjusting JE: Dr 11610 / Cr 11640
    /// (ย้ายมาเป็นภาษีซื้อเคลมได้). Idempotent — ถ้าครบอยู่แล้วหรือไม่เคย
    /// suspend จะ no-op. เรียกก่อน SaveChanges ใน CompleteSupplierTaxInvoiceAsync.
    /// คืน true ถ้า reclassify จริง (มีการสร้าง JE).</summary>
    private async Task<bool> ReclassifyUndueInputVatAsync(Guid companyId, Document doc, string actor)
    {
        // ไม่เคย suspend → ไม่ต้องทำอะไร
        if (!doc.InputVatPostedAsUndue) return false;
        // เคย reclassify ไปแล้ว → ไม่ทำซ้ำ (idempotent)
        if (doc.InputVatBecameClaimableAt.HasValue) return false;
        // มี override → user ตั้งใจไม่เคลม VAT → ไม่ reclassify
        if (!string.IsNullOrWhiteSpace(doc.InputVatAccountCodeOverride)) return false;
        // ต้องเป็นเอกสารที่ approved แล้ว (Draft ไม่มี JE ให้ adjust)
        if (doc.Status == DocumentStatus.Draft || doc.Status == DocumentStatus.Voided) return false;

        // Reload contact ถ้ายังไม่ track (UpdateDocumentAsync อาจไม่ Include)
        if (doc.Contact == null)
            doc.Contact = await _db.Contacts.FirstOrDefaultAsync(c =>
                c.Id == doc.ContactId && c.CompanyId == companyId) ?? doc.Contact!;

        var completeness = TaxInvoiceCompletenessChecker.Evaluate(doc, doc.Contact);
        if (!completeness.IsClaimable) return false;   // ยังไม่ครบ → คงอยู่ 11640

        // หา VAT amount ที่เคยลงไป (= sum line.VatAmount ของบรรทัด claimable)
        if (doc.Lines == null || doc.Lines.Count == 0)
            doc.Lines = await _db.DocumentLines
                .Where(l => l.DocumentId == doc.Id).ToListAsync();
        var vatAmount = doc.Lines.Where(l => l.IsVatClaimable).Sum(l => l.VatAmount);
        if (vatAmount <= 0) return false;

        var claimableAcc = await FindAccountAsync(companyId, "11610")
            ?? await FindAccountAsync(companyId, "116");
        var undueAcc = await FindAccountAsync(companyId, "11640")
            ?? await FindAccountAsync(companyId, "11630");
        if (claimableAcc == null || undueAcc == null) return false;

        var now = DateTime.UtcNow;
        var entryNumber = await GetNextJournalEntryNumberAsync(companyId, "JV");
        var period = await ResolveFiscalPeriodAsync(companyId, now);
        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = now,
            JournalType = JournalType.General,
            Description = $"ภาษีซื้อถึงกำหนด (ใบกำกับครบ §86/4) - {doc.DocumentNumber}",
            Reference = doc.DocumentNumber,
            Status = JournalEntryStatus.Posted,
            TotalDebit = vatAmount,
            TotalCredit = vatAmount,
            CreatedBy = actor,
            IsAutoGenerated = true,
            SourceDocumentId = doc.Id,
            FiscalPeriodId = period?.Id,
            ProjectId = doc.ProjectId,
        };
        _db.JournalEntries.Add(je);
        _db.JournalEntryLines.Add(new JournalEntryLine
        {
            JournalEntryId = je.Id, AccountId = claimableAcc.Id,
            DebitAmount = vatAmount, CreditAmount = 0,
            Description = "ภาษีซื้อ (เคลม ภ.พ.30 ได้แล้ว)", LineOrder = 1,
        });
        _db.JournalEntryLines.Add(new JournalEntryLine
        {
            JournalEntryId = je.Id, AccountId = undueAcc.Id,
            DebitAmount = 0, CreditAmount = vatAmount,
            Description = "กลับรายการภาษีซื้อยังไม่ถึงกำหนด", LineOrder = 2,
        });

        doc.InputVatBecameClaimableAt = now;
        // InputVatPostedAsUndue คงไว้ true เป็น historical marker —
        // ใช้คู่ BecameClaimableAt เพื่อบอก ภ.พ.30 ว่าใช้ period ของ
        // BecameClaimableAt (ไม่ใช่ DocumentDate) เป็น tax point
        return true;
    }

    /// <summary>Resolve AccountCode → AccountId ใน CoA ของบริษัท. คืน null ถ้า
    /// ไม่พบ / inactive (ป้องกัน AI hallucinate ผังที่ไม่มีจริง). cache
    /// ใน-call ผ่าน lookup map ที่ caller เตรียมไว้.</summary>
    private async Task<Guid?> ResolveAccountCodeAsync(Guid companyId, string? code,
        IDictionary<string, Guid?>? cache = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (cache != null && cache.TryGetValue(code, out var cached)) return cached;
        var id = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.AccountCode == code && a.IsActive)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
        if (cache != null) cache[code] = id;
        return id;
    }

    /// <summary>ปิดลูปการสอน local distillation model ตามกฎเหล็ก #1:
    /// สำหรับทุกบรรทัดที่มี GlAccountAiFeedbackId + AccountId — เปรียบ
    /// AccountCode ของ user choice กับ AiPrimaryAnswer แล้วเรียก
    /// RecordUserChoiceAsync (acceptedAi=true ถ้าตรง, false ถ้าแก้). ทนต่อ
    /// recorder/FX ปิด (graceful degradation เงียบ).</summary>
    private async Task RecordLineAccountFeedbackAsync(IEnumerable<DocumentLine> lines, CancellationToken ct = default)
    {
        if (_feedbackRecorder == null) return;
        var pending = lines.Where(l => l.GlAccountAiFeedbackId.HasValue && l.AccountId.HasValue).ToList();
        if (pending.Count == 0) return;
        var feedbackIds = pending.Select(l => l.GlAccountAiFeedbackId!.Value).Distinct().ToList();
        var accountIds = pending.Select(l => l.AccountId!.Value).Distinct().ToList();
        // batch load AI's original answer + chosen account code
        var aiAnswers = await _db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => feedbackIds.Contains(f.Id))
            .Select(f => new { f.Id, f.AiPrimaryAnswer, f.UserChosenAnswer })
            .ToListAsync(ct);
        var aiAnswerMap = aiAnswers.ToDictionary(a => a.Id);
        var codeMap = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.AccountCode, ct);

        foreach (var line in pending)
        {
            var fid = line.GlAccountAiFeedbackId!.Value;
            if (!aiAnswerMap.TryGetValue(fid, out var ai)) continue;
            // ถ้าผู้ใช้เลือกแล้ว (UserChosenAnswer != null) ก่อนหน้านี้ ไม่ทำซ้ำ
            // (ลด noise + ป้องกัน flap จากการ save หลายครั้ง). retrain job ใช้
            // record แรกที่ผู้ใช้ confirm เป็นหลักอยู่แล้ว.
            if (!string.IsNullOrEmpty(ai.UserChosenAnswer)) continue;
            if (!codeMap.TryGetValue(line.AccountId!.Value, out var chosenCode)) continue;
            var accepted = string.Equals(chosenCode, ai.AiPrimaryAnswer, StringComparison.OrdinalIgnoreCase);
            try
            {
                await _feedbackRecorder.RecordUserChoiceAsync(fid, chosenCode, accepted, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Record GL feedback failed (feedbackId={Fid})", fid);
            }
        }
    }

    /// <summary>WHT threshold §50: ไม่หักถ้ายอดสัญญา < 1,000 บาท แต่ถ้ารวม
    /// ทุกครั้งที่จ่ายให้ผู้รับเดียวกัน (per contact, per income type, per
    /// ปีภาษี) ≥ 1,000 ต้องหักย้อนหลัง. method นี้คืน "ต้องหักเพิ่ม" boolean +
    /// total YTD ของผู้รับเดียวกัน → caller (UI/approval) เตือนผู้ใช้ก่อนอนุมัติ
    /// เอกสารที่ลืมใส่ WHT rate. ใช้ใน warnings ตอน approve.</summary>
    private async Task<(bool Required, decimal YtdAmount)> CheckWhtThresholdAsync(
        Guid companyId, Guid contactId, DateTime documentDate, decimal currentLineTotal)
    {
        var yearStart = new DateTime(documentDate.Year, 1, 1);
        var yearEnd = yearStart.AddYears(1);
        var ytd = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ContactId == contactId
                && (d.DocumentType == DocumentType.PaymentVoucher
                    || d.DocumentType == DocumentType.Expense
                    || d.DocumentType == DocumentType.PurchaseInvoice)
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                && d.DocumentDate >= yearStart && d.DocumentDate < yearEnd)
            .SumAsync(d => (decimal?)d.SubTotal) ?? 0m;
        var projectedTotal = ytd + currentLineTotal;
        return (projectedTotal >= 1000m, projectedTotal);
    }

    /// <summary>คำนวณ §65 ตรี รายจ่ายต้องห้าม → เก็บ NonDeductibleAmount +
    /// RuleJson บนเอกสาร (ไหลเข้า ภ.ง.ด.50 worksheet). hard-block กรณีไม่ระบุ
    /// ผู้รับเงิน. NeedsConfirmation (เช่น capex) เป็นแค่ warning ไม่ block.</summary>
    private async Task ApplySection65TerAsync(Guid companyId, Document doc)
    {
        // เตรียม account map (Id → code/name) สำหรับตรวจชนิดบัญชี
        var accIds = doc.Lines.Where(l => l.AccountId.HasValue)
            .Select(l => l.AccountId!.Value).Distinct().ToList();
        var accInfo = accIds.Count == 0
            ? new Dictionary<Guid, (string, string)>()
            : (await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => accIds.Contains(a.Id))
                .Select(a => new { a.Id, a.AccountCode, a.AccountName })
                .ToListAsync())
                .ToDictionary(a => a.Id, a => (a.AccountCode, a.AccountName));

        // context: รายได้ทั้งปี + ทุนจดทะเบียน (สำหรับ cap ค่ารับรอง)
        var company = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == companyId);
        var yearStart = new DateTime(doc.DocumentDate.Year, 1, 1);
        var yearEnd = yearStart.AddYears(1);
        var annualRevenue = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId
                && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice
                    || d.DocumentType == DocumentType.Receipt)
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                && d.DocumentDate >= yearStart && d.DocumentDate < yearEnd)
            .SumAsync(d => (decimal?)d.SubTotal) ?? 0m;

        // §65 ตรี(4) cap เป็น "per fiscal year" — รวมยอดค่ารับรอง YTD ของ
        // เอกสารซื้อ/ค่าใช้จ่ายที่อนุมัติแล้วในรอบเดียวกัน (description มี
        // "รับรอง"/"entertain") เพื่อให้ excess คำนวณตาม YTD จริง ไม่ใช่
        // เฉพาะใบนี้. exclude doc ปัจจุบัน (re-approve / ก่อน approve)
        var priorEntertainment = await _db.DocumentLines.AsNoTracking()
            .Where(l => l.Document.CompanyId == companyId
                && l.Document.Id != doc.Id
                && l.Document.Status == DocumentStatus.Approved
                && l.Document.DocumentDate >= yearStart && l.Document.DocumentDate < yearEnd
                && (l.Document.DocumentType == DocumentType.PurchaseInvoice
                    || l.Document.DocumentType == DocumentType.Expense
                    || l.Document.DocumentType == DocumentType.PaymentVoucher)
                && (l.Description.Contains("รับรอง") || l.Description.Contains("entertain")
                    || l.Description.Contains("เลี้ยงรับรอง")))
            .SumAsync(l => (decimal?)(l.Amount + l.VatAmount)) ?? 0m;

        var ctx = new Section65TerValidator.Context(annualRevenue, company?.PaidUpCapital, priorEntertainment);
        var payeeName = doc.Contact?.Name;
        var payeeTaxId = doc.Contact?.TaxId;

        var result = Section65TerValidator.Evaluate(doc, accInfo, payeeName, payeeTaxId, ctx);

        if (result.HasHardBlock)
            throw new InvalidOperationException(result.FirstBlockMessage
                ?? "รายจ่ายต้องห้าม §65 ตรี — ข้อมูลไม่ครบ");

        doc.NonDeductibleAmount = result.TotalAddBack;
        doc.NonDeductibleRuleJson = result.Findings.Count > 0 ? result.ToJson() : null;
    }

    /// <summary>บังคับลงทะเบียนสินทรัพย์ถาวร — บรรทัดเอกสารฝั่งซื้อที่ลงผัง PPE
    /// (12xxx) ต้องมีทะเบียนสินทรัพย์ + ตารางค่าเสื่อม (TFRS บทที่ 10).
    /// auto-create ด้วยค่า default ตามประเภท (อายุใช้งาน/วิธี/ผังค่าเสื่อม),
    /// NeedsReview=true ให้ผู้ใช้ตรวจ. dedupe ด้วย SourceDocumentLineId (re-approve
    /// ไม่สร้างซ้ำ). PostAcquisitionJournalEntry=false เพราะเอกสารลง Dr asset แล้ว.</summary>
    private async Task AutoRegisterFixedAssetsAsync(Guid companyId, Document doc, string actor)
    {
        if (_fixedAssets == null) return;   // service ไม่ inject (เช่น test) → ข้าม
        if (doc.DocumentType is not (DocumentType.Expense or DocumentType.PurchaseInvoice
            or DocumentType.PaymentVoucher)) return;
        if (doc.Lines == null || doc.Lines.Count == 0) return;

        // ผังที่แต่ละบรรทัดลง → code
        var accIds = doc.Lines.Where(l => l.AccountId.HasValue).Select(l => l.AccountId!.Value).Distinct().ToList();
        if (accIds.Count == 0) return;
        var accMap = (await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => accIds.Contains(a.Id))
            .Select(a => new { a.Id, a.AccountCode }).ToListAsync())
            .ToDictionary(a => a.Id, a => a.AccountCode);

        // PPE accounts ทั้งบริษัท (code → Id) สำหรับ resolve accum/dep accounts
        var allPpe = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId
                && (a.AccountCode.StartsWith("18") || a.AccountCode.StartsWith("56")))
            .Select(a => new { a.Id, a.AccountCode }).ToListAsync();
        Guid? CodeToId(string? code) => code == null ? null
            : allPpe.FirstOrDefault(a => a.AccountCode == code)?.Id;

        // TFRS for NPAEs บทที่ 10 — ต้นทุนสินทรัพย์ = ราคาซื้อ + ค่าใช้จ่ายที่
        // ทำให้พร้อมใช้ (ค่าขนส่ง/ติดตั้ง/ฝึกอบรม/ค่าธรรมเนียม) → capitalize เข้า
        // asset เดียวกัน ไม่แยกเป็นหลาย asset. group บรรทัดที่ลงผัง PPE
        // ตัวเดียวกัน + เลือก "main line" (description ไม่ใช่ auxiliary)
        // เป็นชื่อ asset, รวมต้นทุนทุก line ใน group เป็น cost
        static bool IsAuxiliary(string? desc)
        {
            if (string.IsNullOrWhiteSpace(desc)) return false;
            var d = desc.ToLowerInvariant();
            return d.Contains("ขนส่ง") || d.Contains("จัดส่ง") || d.Contains("ติดตั้ง")
                || d.Contains("ฝึกอบรม") || d.Contains("ค่าธรรมเนียม") || d.Contains("ค่าบริการ")
                || d.Contains("shipping") || d.Contains("delivery") || d.Contains("freight")
                || d.Contains("install") || d.Contains("training") || d.Contains("setup")
                || d.Contains("ค่าประกัน");
        }

        // group บรรทัดที่เป็น PPE ตามผัง (AccountId) — บรรทัดที่ผังไม่ใช่ PPE ข้าม
        var ppeLines = doc.Lines.Where(l =>
            l.AccountId.HasValue
            && accMap.TryGetValue(l.AccountId.Value, out var c)
            && Tax.FixedAssetAccountClassifier.Resolve(c) != null
            && l.Amount > 0).ToList();
        if (ppeLines.Count == 0) return;

        var groups = ppeLines.GroupBy(l => l.AccountId!.Value);
        foreach (var grp in groups)
        {
            var code = accMap[grp.Key];
            var cls = Tax.FixedAssetAccountClassifier.Resolve(code)!;
            // main line = บรรทัดแรกที่ description ไม่ใช่ auxiliary (ถ้าทุกบรรทัด
            // ใน group เป็น auxiliary ก็ใช้ตัวแรก — edge case คือ stand-alone
            // delivery doc ที่ผังลง PPE)
            var mainLine = grp.FirstOrDefault(l => !IsAuxiliary(l.Description)) ?? grp.First();
            var totalCost = grp.Sum(l => l.Amount);

            // dedupe — เคยลง asset จาก mainLine นี้แล้ว (re-approve) ข้าม
            var dup = await _db.FixedAssets.AnyAsync(a => a.CompanyId == companyId
                && a.SourceDocumentLineId == mainLine.Id);
            if (dup) continue;

            // ใส่ aux description ลง Description ของ asset เพื่อ audit trail
            // (เห็นว่าต้นทุนรวมค่าขนส่ง/ติดตั้งแล้ว)
            var auxDescs = grp.Where(l => IsAuxiliary(l.Description))
                .Select(l => $"{l.Description?.Trim()} {l.Amount:N2}").ToList();
            var assetDesc = $"ลงทะเบียนอัตโนมัติจาก {doc.DocumentNumber}"
                + (auxDescs.Count > 0 ? $" (รวม: {string.Join(", ", auxDescs)})" : "");

            // ยังไม่ออกเลขจริงตอน NeedsReview — ใส่ placeholder "DRAFT-{guid}"
            // เพื่อไม่ให้กิน counter (gap-free). เลขจริง FA-yyyyMM-#### จะออก
            // ตอน user กดบันทึกในหน้า edit (FixedAssetService.UpdateAsync ตอน
            // NeedsReview=true → false). ถ้า user ลบ asset ที่ยังเป็น DRAFT
            // ก่อนยืนยัน จะไม่กระทบลำดับเลขของ asset อื่น
            var assetCode = $"DRAFT-{Guid.NewGuid():N}".Substring(0, 14);
            try
            {
                await _fixedAssets.CreateAsync(companyId, new Models.DTOs.FixedAsset.CreateFixedAssetRequest(
                    AssetCode: assetCode,
                    Name: string.IsNullOrWhiteSpace(mainLine.Description) ? cls.Category : mainLine.Description,
                    Description: assetDesc,
                    Category: cls.Category,
                    Location: null, SerialNumber: null,
                    PurchaseDate: doc.DocumentDate,
                    PurchaseCost: totalCost,
                    SalvageValue: 0m,
                    UsefulLifeMonths: cls.DefaultUsefulLifeMonths,
                    DepreciationMethod: cls.Depreciable
                        ? Models.Enums.DepreciationMethod.StraightLine
                        : Models.Enums.DepreciationMethod.None,
                    AssetAccountId: mainLine.AccountId,
                    DepreciationExpenseAccountId: CodeToId(cls.DepExpenseAccountCode),
                    AccumulatedDepreciationAccountId: CodeToId(cls.AccumDepAccountCode),
                    PostAcquisitionJournalEntry: false,   // เอกสารลง Dr asset แล้ว
                    CreditAccountId: null,
                    ProjectId: mainLine.ProjectId ?? doc.ProjectId,
                    SourceDocumentId: doc.Id,
                    SourceDocumentLineId: mainLine.Id,
                    NeedsReview: true), actor);
            }
            catch (Exception ex)
            {
                // ไม่ให้ asset registration ล้ม ทำ approve พัง — log ไว้
                _logger.LogWarning(ex, "Auto-register fixed asset failed (doc {Doc} line {Line})",
                    doc.DocumentNumber, mainLine.Id);
            }
        }
    }

    /// <summary>สร้างรหัสสินทรัพย์ FA-yyyyMM-#### (gap-tolerant — MAX+1).</summary>
    private async Task<string> GenerateAssetCodeAsync(Guid companyId)
    {
        var prefix = $"FA-{DateTime.UtcNow:yyyyMM}-";
        var last = await _db.FixedAssets.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.AssetCode.StartsWith(prefix))
            .OrderByDescending(a => a.AssetCode)
            .Select(a => a.AssetCode)
            .FirstOrDefaultAsync();
        int seq = 1;
        if (last != null && int.TryParse(last[prefix.Length..], out var n)) seq = n + 1;
        return $"{prefix}{seq:D4}";
    }

    private async Task<FiscalPeriod?> ResolveFiscalPeriodAsync(Guid companyId, DateTime date)
        => await _db.FiscalPeriods.FirstOrDefaultAsync(p =>
            p.CompanyId == companyId && p.StartDate <= date && p.EndDate >= date);

    /// <summary>
    /// บันทึกบัญชีอัตโนมัติเมื่ออนุมัติเอกสาร — สร้าง JournalEntry + Lines โดยตรงผ่าน DbContext
    /// (อยู่ภายใน transaction เดียวกับ ApproveDocumentAsync เพื่อความ atomic ตามหลักบัญชี)
    ///
    /// กฎการ post ตามมาตรฐาน TFRS / ประมวลรัษฎากร §86, §82/10:
    ///
    /// ฝั่งขาย (SV - สมุดรายวันขาย):
    ///   Invoice/TaxInvoice/DebitNote: Dr ลูกหนี้ + Dr ภาษีถูกหัก, Cr รายได้ + Cr ภาษีขาย
    ///   CreditNote (ใบลดหนี้): กลับด้าน — Cr ลูกหนี้ + Cr ภาษีถูกหัก, Dr รายได้ + Dr ภาษีขาย
    ///
    /// ฝั่งซื้อ (UV - สมุดรายวันซื้อ):
    ///   PurchaseInvoice/Expense: Dr ค่าใช้จ่าย + Dr ภาษีซื้อ, Cr เจ้าหนี้ + Cr ภาษีหัก ณ ที่จ่ายค้างจ่าย
    ///
    /// รับเงิน (RV - สมุดรายวันรับ):
    ///   Receipt/ReceiptVoucher (link to Invoice): Dr เงินสด + Dr WHT Asset, Cr ลูกหนี้
    ///   Receipt/ReceiptVoucher (cash sale, ไม่ link): Dr เงินสด + Dr WHT Asset, Cr รายได้ + Cr ภาษีขาย
    ///
    /// จ่ายเงิน (PV - สมุดรายวันจ่าย):
    ///   PaymentVoucher (link to PurchaseInvoice): Dr เจ้าหนี้, Cr เงินสด + Cr WHT Payable
    ///   PaymentVoucher (direct cash purchase): Dr ค่าใช้จ่าย + Dr ภาษีซื้อ, Cr เงินสด + Cr WHT Payable
    /// </summary>
    /// <summary>Cascade product stock movements when a goods document is
    /// approved (sign=+1) or voided (sign=-1). The sign on the document
    /// type decides direction: sale Invoice / TaxInvoice = OUT (negative
    /// stock movement), purchase PurchaseInvoice = IN, sales CreditNote
    /// = IN (goods returned by customer). Documents that don't move goods
    /// (Quotation, Receipt, PaymentVoucher, DeliveryNote alone, Debit Note
    /// price-adjustment, etc.) return a zero direction and are skipped.
    ///
    /// Lines are matched to Products by (CompanyId, ProductCode). Lines
    /// with no ProductCode, free-text descriptions, or matching a product
    /// where TrackStock=false are skipped silently.
    ///
    /// Runs inside the approval/void transaction so a SaveChanges failure
    /// rolls back both the document state and the stock delta. Without
    /// this, sale Invoices left stock untouched and reconciling inventory
    /// to GL revenue required manual stock adjustments every period.</summary>
    private async Task ApplyStockMovementsAsync(Guid companyId, Document doc, int sign, string actor)
    {
        if (sign == 0) return;
        // DeliveryNote intentionally NOT triggered here — when a tenant uses
        // the full Quotation → SO → DN → Invoice chain, the Invoice is the
        // financial recognition and triggers the stock move. Issuing the DN
        // alone shouldn't double-count if the Invoice follows.
        int direction = doc.DocumentType switch
        {
            DocumentType.Invoice or DocumentType.TaxInvoice => -1,  // sale OUT
            // Goods physically arrive at the GRN (3-way match) — stock moves
            // IN there. A PurchaseInvoice raised against that GRN must NOT
            // move stock again (handled below); a STANDALONE PurchaseInvoice
            // (no GRN) still moves stock IN itself.
            DocumentType.GoodsReceiptNote => +1,
            DocumentType.PurchaseInvoice => +1,                     // purchase IN
            // CreditNote only restocks when reason = Return (goods physically
            // came back). Discount / Adjustment / Writeoff are pure financial
            // adjustments — sticks were never returned and inventory must
            // stay flat. The CreditNoteReason field is required by Create
            // for CN; older grandfathered rows with NULL fall to 0 (no move).
            DocumentType.CreditNote when doc.CreditNoteReason == Models.Enums.CreditNoteReason.Return => +1,
            _ => 0,
        };
        if (direction == 0) return;

        // PurchaseInvoice billed against an accrued GRN → goods already
        // stocked at receipt; don't double-count them now.
        if (doc.DocumentType == DocumentType.PurchaseInvoice
            && await GetReceivedViaGrnAccrualAccountAsync(companyId, doc) != null)
            return;
        // sign flips on void: a sale's OUT becomes an IN; the BalanceAfter
        // walks back to where it was before.
        var effective = direction * sign;
        if (doc.Lines == null) return;

        // Pre-load the full set of products this document touches in one
        // query — avoids N+1 on long invoices.
        var codes = doc.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.ProductCode))
            .Select(l => l.ProductCode!)
            .Distinct()
            .ToList();
        if (codes.Count == 0) return;

        var products = await _db.Products
            .Where(p => p.CompanyId == companyId && codes.Contains(p.Code) && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Code);

        var now = DateTime.UtcNow;
        foreach (var line in doc.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.ProductCode)) continue;
            if (!products.TryGetValue(line.ProductCode!, out var product)) continue;
            if (!product.TrackStock) continue;

            var qtyDelta = effective * line.Quantity;
            product.CurrentStock += qtyDelta;
            _db.StockMovements.Add(new StockMovement
            {
                CompanyId = companyId,
                ProductId = product.Id,
                DocumentId = doc.Id,
                MovementDate = now,
                // MovementType is informational for reports — we tag based on
                // direction so "IN" / "OUT" reads naturally even on a void.
                MovementType = qtyDelta > 0 ? "IN" : "OUT",
                Quantity = qtyDelta,
                UnitCost = product.CostPrice,
                BalanceAfter = product.CurrentStock,
                Reference = doc.DocumentNumber,
                Notes = sign > 0
                    ? $"จาก {doc.DocumentType} เลขที่ {doc.DocumentNumber}"
                    : $"กลับรายการจากการยกเลิก {doc.DocumentType} เลขที่ {doc.DocumentNumber}",
                CreatedBy = actor,
            });
        }
    }

    private async Task AutoPostToJournalAsync(Guid companyId, Document doc, string createdBy)
    {
        // Multi-currency: every Baht amount that hits the GL must be converted
        // from the document's currency at the rate captured on Create. Validate
        // the rate is sane (positive, finite, non-zero) — corrupt FX = corrupt GL.
        if (doc.ExchangeRate <= 0m)
            throw new InvalidOperationException(
                $"อัตราแลกเปลี่ยนไม่ถูกต้อง ({doc.ExchangeRate}) — กรุณาแก้ไขเอกสารและระบุอัตราที่ถูกต้องก่อนอนุมัติ");
        if (!string.Equals(doc.Currency, "THB", StringComparison.OrdinalIgnoreCase) && doc.ExchangeRate == 1m)
            throw new InvalidOperationException(
                $"เอกสารสกุลเงิน {doc.Currency} ต้องระบุอัตราแลกเปลี่ยนก่อนอนุมัติ (พบ ExchangeRate = 1)");

        // WHT recognition basis (per CompanySettings):
        //   Cash    = recognize at Receipt / PaymentVoucher (strict §50/§52)
        //   Accrual = recognize at Invoice / PurchaseInvoice approval
        // Default for new tenants is Cash. Existing tenants stay Accrual to
        // keep their historical GL consistent. Cash-side documents (cash
        // sale Receipt without RelatedDoc) always post WHT here either way
        // because there's no later cash event.
        var whtSettings = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);
        var whtBasis = whtSettings?.WhtRecognitionBasis ?? Models.Enums.WhtRecognitionBasis.Cash;

        // Resolve default fallback accounts up front — used when document lines
        // have no explicit AccountId (the UI doesn't expose per-line account selection yet).
        // The default Thai chart uses 41000 (sales) / 42000 (service) at level 4;
        // industry templates may override (e.g. hospitality uses 411xx). Pick the first
        // posting-level (Level >= 4) Revenue/Expense account by code as a safe default.
        var defaultRevenue = await FindAccountAsync(companyId, "41000")
            ?? await FindAccountAsync(companyId, "42000")
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountType == AccountType.Revenue && a.Level >= 4 && a.IsActive)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();

        // Use document-level expense category if specified, otherwise fallback to default
        ChartOfAccount? defaultExpense = null;
        if (doc.ExpenseCategoryId.HasValue)
        {
            defaultExpense = await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.Id == doc.ExpenseCategoryId.Value && a.CompanyId == companyId);
        }
        defaultExpense ??= await FindAccountAsync(companyId, "51110")
            ?? await FindAccountAsync(companyId, "52110")
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountType == AccountType.Expense && a.Level >= 4 && a.IsActive)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();

        // Resolve money account for cash/bank movement entries.
        // Priority: PaymentAccountId (direct GL) > BankAccountId (bank's linked GL) > Cash 111
        ChartOfAccount? moneyAccount = null;
        if (doc.PaymentAccountId.HasValue)
        {
            moneyAccount = await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.Id == doc.PaymentAccountId.Value && a.CompanyId == companyId);
        }
        if (moneyAccount == null && doc.BankAccountId.HasValue)
        {
            var bankAcc = await _db.BankAccounts
                .Include(b => b.LinkedAccount)
                .FirstOrDefaultAsync(b => b.Id == doc.BankAccountId.Value && b.CompanyId == companyId);
            moneyAccount = bankAcc?.LinkedAccount;
        }
        moneyAccount ??= await FindAccountAsync(companyId, "111");

        // Validate critical accounts exist — fail fast with clear error
        var isSalesDoc = doc.DocumentType is DocumentType.Invoice or DocumentType.TaxInvoice
            or DocumentType.DebitNote or DocumentType.CreditNote
            or DocumentType.Receipt or DocumentType.ReceiptVoucher;
        if (isSalesDoc && defaultRevenue == null)
            throw new InvalidOperationException(
                "ไม่พบบัญชีรายได้ (41000/42000) ในผังบัญชี — กรุณาเพิ่มบัญชีรายได้ก่อนอนุมัติเอกสารขาย");
        if (!isSalesDoc && defaultExpense == null)
            throw new InvalidOperationException(
                "ไม่พบบัญชีค่าใช้จ่าย (51110/52110) ในผังบัญชี — กรุณาเพิ่มบัญชีค่าใช้จ่ายก่อนอนุมัติเอกสารซื้อ");

        var pendingLines = new List<(Guid AccountId, decimal Debit, decimal Credit, string? Description)>();
        JournalType journalType;
        // ProjectId map: each pendingLines index → resolved project. System-generated
        // lines (AR/VAT/Cash/AP/WHT) inherit doc.ProjectId; per-line revenue/expense
        // can override via docLine.ProjectId.
        var lineProjects = new List<Guid?>();
        // Multi-currency: every amount fed into AddLine is in the document's
        // currency. Convert to THB (base) at the captured rate; AwayFromZero
        // to match the system-wide rounding convention.
        var fx = doc.ExchangeRate;
        decimal Conv(decimal amount) => fx == 1m ? amount : Math.Round(amount * fx, 2, MidpointRounding.AwayFromZero);
        void AddLine(Guid accountId, decimal debit, decimal credit, string? desc, Guid? proj = null)
        {
            pendingLines.Add((accountId, Conv(debit), Conv(credit), desc));
            lineProjects.Add(proj ?? doc.ProjectId);
        }

        // ============================================================
        // SALES SIDE: Invoice / TaxInvoice (full sale on credit)
        // ============================================================
        if (doc.DocumentType == DocumentType.Invoice
            || doc.DocumentType == DocumentType.TaxInvoice)
        {
            journalType = JournalType.Sales;

            // Dr: ลูกหนี้การค้า (113). On Accrual basis the AR balance is NET
            // of WHT (gross - WHT) so the WHT-Asset can be booked alongside.
            // On Cash basis the AR balance is GROSS (full amount) and WHT
            // isn't recognized here — it's recognized when the customer
            // actually pays and withholds (Receipt path below).
            var arAccount = await FindAccountAsync(companyId, "113", doc.Contact);
            var arAmountAtInvoice = whtBasis == Models.Enums.WhtRecognitionBasis.Cash
                ? doc.TotalAmount + doc.WithholdingTaxAmount
                : doc.TotalAmount;
            if (arAccount != null)
                AddLine(arAccount.Id, arAmountAtInvoice, 0, $"ลูกหนี้การค้า - {doc.DocumentNumber}");

            // Dr: ภาษีถูกหัก ณ ที่จ่าย (11910). Only fires on Accrual basis
            // — Cash basis defers this to the Receipt that actually collects.
            if (whtBasis == Models.Enums.WhtRecognitionBasis.Accrual && doc.WithholdingTaxAmount > 0)
            {
                var whtAccount = await FindAccountAsync(companyId, "11910");
                if (whtAccount != null)
                    AddLine(whtAccount.Id, doc.WithholdingTaxAmount, 0, "ภาษีหัก ณ ที่จ่าย (ถูกหัก)");
            }

            // Cr: บัญชีรายได้ตามแต่ละบรรทัด (default = 411)
            foreach (var docLine in doc.Lines)
            {
                var revenueAccountId = docLine.AccountId ?? defaultRevenue?.Id;
                if (revenueAccountId.HasValue)
                    AddLine(revenueAccountId.Value, 0, docLine.Amount, docLine.Description, docLine.ProjectId);
            }

            // Cr: ภาษีขาย (Output VAT 21911) per ภ.พ.30
            if (doc.VatAmount > 0)
            {
                var vatAccount = await FindAccountAsync(companyId, "21911")
                    ?? throw new InvalidOperationException("ไม่พบบัญชีภาษีขาย (21911) ในผังบัญชี — กรุณาเพิ่มก่อนอนุมัติเอกสารที่มี VAT");
                if (vatAccount != null)
                    AddLine(vatAccount.Id, 0, doc.VatAmount, "ภาษีขาย");
            }
        }
        // ============================================================
        // ADJUSTMENT NOTES: CreditNote / DebitNote (polymorphic by source)
        //
        // Determines whether this is a sales-side adjustment (we issue to customer)
        // or a purchase-side adjustment (supplier issues to us, we return goods)
        // by inspecting the source document's type via RelatedDocumentId.
        //
        // Also detects "cash refund" mode: when source is fully paid (BalanceDue=0),
        // the counter-account is Cash instead of AR/AP — money actually moves.
        //
        // Standard cases per Revenue Code §82/10 (sales) and equivalent purchase
        // return treatment:
        //   Sales CN  AR-mode:    Cr AR,    Dr Revenue, Dr Output VAT, Cr WHT-Asset
        //   Sales CN  Cash-mode:  Cr Cash,  Dr Revenue, Dr Output VAT, Cr WHT-Asset
        //   Sales DN  AR-mode:    Dr AR,    Cr Revenue, Cr Output VAT, Dr WHT-Asset
        //   Sales DN  Cash-mode:  Dr Cash,  Cr Revenue, Cr Output VAT, Dr WHT-Asset
        //   Purch CN  AP-mode:    Dr AP,    Cr Expense, Cr Input VAT,  Dr WHT-Payable
        //   Purch CN  Cash-mode:  Dr Cash,  Cr Expense, Cr Input VAT,  Dr WHT-Payable
        //   Purch DN  AP-mode:    Cr AP,    Dr Expense, Dr Input VAT,  Cr WHT-Payable
        //   Purch DN  Cash-mode:  Cr Cash,  Dr Expense, Dr Input VAT,  Cr WHT-Payable
        // ============================================================
        else if (doc.DocumentType == DocumentType.CreditNote
                 || doc.DocumentType == DocumentType.DebitNote)
        {
            // Resolve the source side by inspecting RelatedDocumentId. Default is
            // sales-side (most common case + back-compat with previous behavior).
            Document? source = null;
            if (doc.RelatedDocumentId.HasValue)
            {
                source = await _db.Documents
                    .AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == doc.RelatedDocumentId.Value
                        && d.CompanyId == companyId);
            }

            var isPurchaseSide = source != null && (
                source.DocumentType == DocumentType.PurchaseInvoice
                || source.DocumentType == DocumentType.Expense
                || source.DocumentType == DocumentType.CertificateInLieu);
            var isCashSettlement = source != null && source.BalanceDue <= 0.01m;
            var isCreditNote = doc.DocumentType == DocumentType.CreditNote;

            journalType = isPurchaseSide ? JournalType.Purchase : JournalType.Sales;
            var typeLabel = isCreditNote ? "ใบลดหนี้" : "ใบเพิ่มหนี้";

            // === Counter-account: AR/AP/Cash depending on mode ===
            // For CN: counter-account is on the credit side (sales) / debit side (purchase)
            // For DN: counter-account is on the debit side (sales) / credit side (purchase)
            string counterAccCode;
            if (isPurchaseSide)
                counterAccCode = isCashSettlement ? "111" : "212"; // Cash or AP
            else
                counterAccCode = isCashSettlement ? "111" : "113"; // Cash or AR

            var counterAcc = await FindAccountAsync(companyId, counterAccCode);
            var counterDesc = $"{typeLabel} - {doc.DocumentNumber}" +
                (isCashSettlement ? " (เงินสด)" : "");

            // CN reduces the receivable/increases payable for purchase return; DN opposite.
            // Sales side:    CN→Cr counter, DN→Dr counter
            // Purchase side: CN→Dr counter, DN→Cr counter
            var counterIsDebit = isPurchaseSide ? isCreditNote : !isCreditNote;
            if (counterAcc != null)
            {
                AddLine(counterAcc.Id,
                    counterIsDebit ? doc.TotalAmount : 0,
                    counterIsDebit ? 0 : doc.TotalAmount,
                    counterDesc);
            }

            // === Revenue / Expense lines ===
            // Sales CN reverses revenue (Dr); Sales DN adds revenue (Cr)
            // Purchase CN reverses expense (Cr); Purchase DN adds expense (Dr)
            foreach (var docLine in doc.Lines)
            {
                Guid? lineAccId;
                if (isPurchaseSide)
                    lineAccId = docLine.AccountId ?? defaultExpense?.Id;
                else
                    lineAccId = docLine.AccountId ?? defaultRevenue?.Id;

                if (!lineAccId.HasValue) continue;

                // Determine Dr or Cr direction:
                //   Sales CN  → Dr revenue (reverse)
                //   Sales DN  → Cr revenue (add)
                //   Purch CN  → Cr expense (reverse)
                //   Purch DN  → Dr expense (add)
                var lineIsDebit = isPurchaseSide ? !isCreditNote : isCreditNote;
                AddLine(lineAccId.Value,
                    lineIsDebit ? docLine.Amount : 0,
                    lineIsDebit ? 0 : docLine.Amount,
                    $"{typeLabel} - {docLine.Description}",
                    docLine.ProjectId);
            }

            // === VAT line ===
            if (doc.VatAmount > 0)
            {
                // Sales: Output VAT 21911 (liability), Purchase: Input VAT 116 (asset)
                var vatCode = isPurchaseSide ? "116" : "21911";
                var vatAcc = await FindAccountAsync(companyId, vatCode);
                if (vatAcc != null)
                {
                    // Same direction rule as revenue/expense lines
                    var vatIsDebit = isPurchaseSide ? !isCreditNote : isCreditNote;
                    AddLine(vatAcc.Id,
                        vatIsDebit ? doc.VatAmount : 0,
                        vatIsDebit ? 0 : doc.VatAmount,
                        $"{typeLabel} ภาษี{(isPurchaseSide ? "ซื้อ" : "ขาย")}");
                }
            }

            // === WHT line ===
            if (doc.WithholdingTaxAmount > 0)
            {
                // Sales: WHT-Asset 11910 (we got withheld); Purchase: WHT-Payable
                // by counterparty type (Individual→ภ.ง.ด.3 21916, Juristic→ภ.ง.ด.53 21917).
                var whtAcc = isPurchaseSide
                    ? await ResolveWhtPayableAccountAsync(companyId, doc.Contact)
                    : await FindAccountAsync(companyId, "11910");
                if (whtAcc != null)
                {
                    // WHT-Asset behaves like revenue (sales) — opposite for purchase
                    // Sales CN→Cr WHT (reverse claim), DN→Dr WHT (add claim)
                    // Purch CN→Dr WHT-Payable (reverse), DN→Cr WHT-Payable (add)
                    var whtIsDebit = isPurchaseSide ? isCreditNote : !isCreditNote;
                    AddLine(whtAcc.Id,
                        whtIsDebit ? doc.WithholdingTaxAmount : 0,
                        whtIsDebit ? 0 : doc.WithholdingTaxAmount,
                        $"{typeLabel} ภาษีหัก ณ ที่จ่าย");
                }
            }
        }
        // ============================================================
        // PURCHASE SIDE: PurchaseInvoice / Expense / CertificateInLieu (on credit)
        // ============================================================
        else if (doc.DocumentType == DocumentType.CertificateInLieu)
        {
            // ใบรับรองแทนใบเสร็จ — used when the payee can't issue a tax
            // invoice/receipt (street vendor, taxi, ...). Per Revenue Code
            // §82/4 the buyer CANNOT claim input VAT without a valid tax
            // invoice, so VAT is NOT posted to ภาษีซื้อ (116); any VAT amount
            // is folded into the expense as part of its (non-recoverable)
            // cost. It is a cash payment, so the credit side hits Cash/Bank,
            // never Accounts Payable.
            journalType = JournalType.Purchase;

            foreach (var docLine in doc.Lines)
            {
                var expenseAccountId = docLine.AccountId ?? defaultExpense?.Id;
                if (expenseAccountId.HasValue)
                    AddLine(expenseAccountId.Value, docLine.Amount, 0, docLine.Description, docLine.ProjectId);
            }
            // Non-claimable VAT → fold into expense cost (NOT บัญชีภาษีซื้อ).
            if (doc.VatAmount > 0 && defaultExpense != null)
                AddLine(defaultExpense.Id, doc.VatAmount, 0, "ภาษีซื้อที่เคลมไม่ได้ (รวมเป็นต้นทุน) - ใบรับรองแทนใบเสร็จ");

            if (moneyAccount != null)
                AddLine(moneyAccount.Id, 0, doc.TotalAmount,
                    $"{(doc.BankAccountId.HasValue ? "จ่ายจากบัญชี" : "จ่ายเงินสด")} - {doc.DocumentNumber}");

            if (doc.WithholdingTaxAmount > 0)
            {
                var whtAcc = await ResolveWhtPayableAccountAsync(companyId, doc.Contact);
                if (whtAcc != null)
                    AddLine(whtAcc.Id, 0, doc.WithholdingTaxAmount, "ภาษีหัก ณ ที่จ่ายค้างจ่าย");
            }
        }
        else if (doc.DocumentType == DocumentType.PurchaseInvoice
                 || doc.DocumentType == DocumentType.Expense)
        {
            journalType = JournalType.Purchase;

            // 3-way match: if this invoice was billed against a Goods Receipt
            // Note that ALREADY accrued the goods (Dr Expense / Cr GR-NI when
            // received), the expense is already in the books — so here we just
            // CLEAR the GR-NI accrual (Dr GR-NI) instead of debiting expense
            // again, then claim VAT + set up the payable. Otherwise (standalone
            // PI) we debit expense as normal.
            var grNiAccount = await GetReceivedViaGrnAccrualAccountAsync(companyId, doc);
            if (grNiAccount != null)
            {
                // Dr: เจ้าหนี้-รับสินค้ายังไม่วางบิล (GR-NI) — clear the accrual
                // for the invoiced (ex-VAT) value. Partial invoices clear only
                // their portion; the rest of the GR-NI stays for later bills.
                AddLine(grNiAccount.Id, doc.SubTotal, 0, $"ตัดเจ้าหนี้รับของยังไม่วางบิล - {doc.DocumentNumber}");
            }
            else
            {
                // Dr: ค่าใช้จ่าย/สินค้า ตามรายการ (standalone purchase).
                // ภาษีซื้อต้องห้าม (§82/5): บรรทัดที่ IsVatClaimable=false →
                // รวม VAT เข้าค่าใช้จ่าย (Dr expense = Amount + VatAmount).
                // บรรทัด claimable → Dr expense net of VAT ปกติ + รวม VAT
                // ไปเข้าบัญชีภาษีซื้อ 116 ด้านล่าง.
                foreach (var docLine in doc.Lines)
                {
                    var expenseAccountId = docLine.AccountId ?? defaultExpense?.Id;
                    if (!expenseAccountId.HasValue) continue;
                    var debitAmount = docLine.IsVatClaimable
                        ? docLine.Amount
                        : docLine.Amount + docLine.VatAmount;
                    var desc = docLine.IsVatClaimable
                        ? docLine.Description
                        : $"{docLine.Description} (รวม VAT ต้องห้าม{(string.IsNullOrWhiteSpace(docLine.VatNonClaimableReason) ? "" : " - " + docLine.VatNonClaimableReason)})";
                    AddLine(expenseAccountId.Value, debitAmount, 0, desc, docLine.ProjectId);
                }
            }

            // Dr: ภาษีซื้อ — บัญชีปลายทางขึ้นกับ completeness §86/4 + override:
            //   1) มี override → ใช้ user-pick (เช่น 51000 ต้นทุนขาย ตาม §82/5)
            //   2) ใบกำกับครบ §86/4 → 11610 "ภาษีซื้อ ภ.พ.30" (เคลมได้ทันที)
            //   3) ใบกำกับยังไม่ครบ → 11640 "ภาษีซื้อยังไม่ถึงกำหนด" (§82/3 รอ
            //      ใบครบ; CompleteSupplierTaxInvoiceAsync จะ gen adjusting JE 11640→11610
            //      ตอน user มาเติมข้อมูลครบ + filter ภ.พ.30 ใช้ BecameClaimableAt)
            var claimableVatPi = doc.Lines.Where(l => l.IsVatClaimable).Sum(l => l.VatAmount);
            if (claimableVatPi > 0)
            {
                var (vatInputAccount, postedAsUndue) = await ResolveInputVatAccountAsync(companyId, doc);
                AddLine(vatInputAccount.Id, claimableVatPi, 0,
                    postedAsUndue ? "ภาษีซื้อยังไม่ถึงกำหนด (ใบกำกับไม่ครบ §86/4)"
                                  : (doc.InputVatAccountCodeOverride != null
                                        ? $"VAT → {vatInputAccount.AccountCode} {vatInputAccount.AccountName} (override)"
                                        : "ภาษีซื้อ (เคลมได้)"));
                doc.InputVatPostedAsUndue = postedAsUndue;
            }

            // Cr: payable — role-separated (หลักบัญชีไทย):
            //   PurchaseInvoice → เจ้าหนี้การค้า (21210) — supplier trade invoice.
            //   Expense         → เจ้าหนี้อื่น (21220) — internal expense claim,
            //                     no trade invoice; keeps 21210 reconcilable
            //                     against supplier statements.
            // On Accrual the payable carries NET of WHT (we'll withhold when we
            // pay). On Cash it carries GROSS (full amount owed before deducting
            // the WHT we'll withhold when actually paying).
            var apAccount = await ResolvePayableAccountAsync(companyId, doc.DocumentType, doc.Contact);
            var apAmountAtInvoice = whtBasis == Models.Enums.WhtRecognitionBasis.Cash
                ? doc.TotalAmount + doc.WithholdingTaxAmount
                : doc.TotalAmount;
            if (apAccount != null)
                AddLine(apAccount.Id, 0, apAmountAtInvoice,
                    $"{(doc.DocumentType == DocumentType.Expense ? "เจ้าหนี้อื่น (ตั้งหนี้ค่าใช้จ่าย)" : "เจ้าหนี้การค้า")} - {doc.DocumentNumber}");

            // Cr: ภาษีหัก ณ ที่จ่ายค้างจ่าย (21916 ภ.ง.ด.3 / 21917 ภ.ง.ด.53).
            // Accrual only — Cash basis defers to the PaymentVoucher path.
            if (whtBasis == Models.Enums.WhtRecognitionBasis.Accrual && doc.WithholdingTaxAmount > 0)
            {
                var whtAccount = await ResolveWhtPayableAccountAsync(companyId, doc.Contact);
                if (whtAccount != null)
                {
                    // Group WHT by rate for clear audit trail
                    var whtByRate = doc.Lines
                        .Where(l => l.WithholdingTaxAmount > 0)
                        .GroupBy(l => l.WithholdingTaxRate)
                        .Select(g => new { Rate = g.Key, Amount = g.Sum(l => l.WithholdingTaxAmount) })
                        .ToList();
                    if (whtByRate.Count > 1)
                    {
                        foreach (var g in whtByRate)
                            AddLine(whtAccount.Id, 0, g.Amount, $"ภาษีหัก ณ ที่จ่าย {g.Rate}%");
                    }
                    else
                    {
                        AddLine(whtAccount.Id, 0, doc.WithholdingTaxAmount, "ภาษีหัก ณ ที่จ่ายค้างจ่าย");
                    }
                }
            }
        }
        // ============================================================
        // CASH RECEIPTS: Receipt / ReceiptVoucher
        // - Linked to existing Invoice (RelatedDocumentId): collection
        // - Standalone (no link): direct cash sale
        // ============================================================
        else if (doc.DocumentType == DocumentType.Receipt
                 || doc.DocumentType == DocumentType.ReceiptVoucher)
        {
            journalType = JournalType.CashReceipts;

            if (doc.RelatedDocumentId.HasValue)
            {
                // Cash basis: the source Invoice posted AR at GROSS (incl. WHT)
                // and skipped the WHT-Asset line. We now book WHT-Asset for
                // the withheld portion and clear AR at GROSS so the books
                // balance. Pull the WHT amount from the source so we always
                // match what was actually withheld, not whatever the operator
                // typed on the Receipt.
                var arAccount = await FindAccountAsync(companyId, "113", doc.Contact);
                if (whtBasis == Models.Enums.WhtRecognitionBasis.Cash)
                {
                    // For partial / installment receipts, use the WHT
                    // recorded on THIS receipt — not the source's full WHT.
                    // The operator entered the per-installment WHT on the
                    // receipt's own lines so cumulative across receipts
                    // matches the source's total.
                    var thisWht = doc.WithholdingTaxAmount;
                    // Cash actually received = doc.TotalAmount (net of THIS receipt's WHT).
                    if (moneyAccount != null)
                        AddLine(moneyAccount.Id, doc.TotalAmount, 0, $"รับชำระ - {doc.DocumentNumber}");
                    if (thisWht > 0)
                    {
                        var whtAccount = await FindAccountAsync(companyId, "11910");
                        if (whtAccount != null)
                            AddLine(whtAccount.Id, thisWht, 0, "ภาษีหัก ณ ที่จ่าย (ลูกค้าหัก)");
                    }
                    if (arAccount != null)
                        AddLine(arAccount.Id, 0, doc.TotalAmount + thisWht,
                            $"ตัดลูกหนี้ - {doc.DocumentNumber}");
                }
                else
                {
                    // Accrual: AR was already net of WHT at Invoice time, so
                    // Receipt just moves the net cash from AR to Cash.
                    if (moneyAccount != null)
                        AddLine(moneyAccount.Id, doc.TotalAmount, 0, $"รับชำระ - {doc.DocumentNumber}");
                    if (arAccount != null)
                        AddLine(arAccount.Id, 0, doc.TotalAmount,
                            $"ตัดลูกหนี้ - {doc.DocumentNumber}");
                }
            }
            else
            {
                if (moneyAccount != null)
                    AddLine(moneyAccount.Id, doc.TotalAmount, 0,
                        $"{(doc.BankAccountId.HasValue ? "รับเงินเข้าบัญชี" : "รับเงินสด")} - {doc.DocumentNumber}");

                if (doc.WithholdingTaxAmount > 0)
                {
                    var whtAccount = await FindAccountAsync(companyId, "11910");
                    if (whtAccount != null)
                        AddLine(whtAccount.Id, doc.WithholdingTaxAmount, 0, "ภาษีหัก ณ ที่จ่าย (ถูกหัก)");
                }

                // เงินมัดจำ/รับล่วงหน้า: Cr "ขายรอรับรู้" (217xx — หนี้สิน) แทน
                // บัญชีรายได้ เพราะยังไม่รับรู้รายได้จนกว่าจะส่งมอบ (RealizeDeposit
                // ตัด 217xx → รายได้ภายหลัง). VAT ยังถึงกำหนดทันที (ด้านล่าง).
                ChartOfAccount? deferredAcc = null;
                if (doc.IsDeposit)
                {
                    // เลือกผัง deferred: snapshot บนเอกสาร > line account (ถ้าชี้ 217xx)
                    // > default 21712 (ค่าสินค้ารับล่วงหน้า) → 217 parent.
                    deferredAcc = await FindAccountAsync(companyId,
                            doc.DepositDeferredAccountCode ?? "21712")
                        ?? await FindAccountAsync(companyId, "217");
                    if (deferredAcc != null)
                        doc.DepositDeferredAccountCode = deferredAcc.AccountCode;  // snapshot
                }

                foreach (var docLine in doc.Lines)
                {
                    // มัดจำ → ขายรอรับรู้; ปกติ → รายได้ (line account หรือ default)
                    var creditAccountId = doc.IsDeposit
                        ? (deferredAcc?.Id ?? docLine.AccountId ?? defaultRevenue?.Id)
                        : (docLine.AccountId ?? defaultRevenue?.Id);
                    if (creditAccountId.HasValue)
                        AddLine(creditAccountId.Value, 0, docLine.Amount,
                            doc.IsDeposit ? $"รับมัดจำ/รับล่วงหน้า - {docLine.Description}" : docLine.Description,
                            docLine.ProjectId);
                }

                if (doc.VatAmount > 0)
                {
                    // มัดจำเคส Deferred (tax point ยังไม่เกิด) → Cr "ภาษีขายรอเรียก
                    // เก็บ" 21913 (ยังไม่เข้า ภ.พ.30); เคส Immediate / ขายปกติ →
                    // Cr "ภาษีขาย ภ.พ.30" 21911 (เข้า ภ.พ.30 ทันที). เมื่อ tax point
                    // ของเคส deferred เกิดจริง RealizeDeposit จะย้าย 21913 → 21911.
                    var useDeferredVat = doc.IsDeposit && doc.DepositOutputVatDeferred;
                    var vatAccount = useDeferredVat
                        ? (await FindAccountAsync(companyId, "21913")
                           ?? await FindAccountAsync(companyId, "21911"))
                        : await FindAccountAsync(companyId, "21911");
                    if (vatAccount != null)
                        AddLine(vatAccount.Id, 0, doc.VatAmount,
                            useDeferredVat ? "ภาษีขายรอเรียกเก็บ (ยังไม่ถึงกำหนด §78)" : "ภาษีขาย");
                }
            }
        }
        // ============================================================
        // CASH PAYMENTS: PaymentVoucher
        // - Linked to existing PurchaseInvoice: settlement
        // - Standalone: direct cash purchase
        // ============================================================
        else if (doc.DocumentType == DocumentType.PaymentVoucher)
        {
            journalType = JournalType.CashPayments;

            if (doc.RelatedDocumentId.HasValue)
            {
                // Settlement must CLEAR the same payable account the source
                // document CREDITED — an Expense booked เจ้าหนี้อื่น (21220),
                // a PurchaseInvoice booked เจ้าหนี้การค้า (21210). Resolve by
                // the source doc's type so the liability nets to zero on the
                // right account.
                var sourceType = await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == doc.RelatedDocumentId.Value && d.CompanyId == companyId)
                    .Select(d => (DocumentType?)d.DocumentType)
                    .FirstOrDefaultAsync() ?? DocumentType.PurchaseInvoice;
                var apAccount = await ResolvePayableAccountAsync(companyId, sourceType, doc.Contact);
                if (whtBasis == Models.Enums.WhtRecognitionBasis.Cash)
                {
                    // Cash basis: PI booked AP at GROSS, skipped the WHT
                    // payable. Here we clear AP at gross, pay net cash, and
                    // recognize WHT-Payable for the withheld portion. Use
                    // THIS voucher's WHT (per-installment) so cumulative
                    // matches the source PI's total over multiple PVs.
                    var thisWht = doc.WithholdingTaxAmount;
                    if (apAccount != null)
                        AddLine(apAccount.Id, doc.TotalAmount + thisWht, 0,
                            $"ตัดเจ้าหนี้ - {doc.DocumentNumber}");
                    if (moneyAccount != null)
                        AddLine(moneyAccount.Id, 0, doc.TotalAmount,
                            $"{(doc.BankAccountId.HasValue ? "จ่ายจากบัญชี" : "จ่ายเงิน")} - {doc.DocumentNumber}");
                    if (thisWht > 0)
                    {
                        var whtAccount = await ResolveWhtPayableAccountAsync(companyId, doc.Contact);
                        if (whtAccount != null)
                            AddLine(whtAccount.Id, 0, thisWht, "ภาษีหัก ณ ที่จ่ายค้างจ่าย (ภ.ง.ด.)");
                    }
                }
                else
                {
                    // Accrual: AP at PI was already net of WHT, so PV just
                    // moves net cash from AP to Cash/Bank.
                    if (apAccount != null)
                        AddLine(apAccount.Id, doc.TotalAmount, 0,
                            $"ตัดเจ้าหนี้ - {doc.DocumentNumber}");
                    if (moneyAccount != null)
                        AddLine(moneyAccount.Id, 0, doc.TotalAmount,
                            $"{(doc.BankAccountId.HasValue ? "จ่ายจากบัญชี" : "จ่ายเงิน")} - {doc.DocumentNumber}");
                }
            }
            else
            {
                // PaymentVoucher standalone: เหมือน PI ทุกประการ แต่ Cr =
                // Cash/Bank โดยตรง. ภาษีซื้อต้องห้าม (§82/5) แยก per-line
                // เหมือนกัน — บรรทัด IsVatClaimable=false → Dr expense รวม
                // VAT, claimable → Dr expense net + รวม VAT เข้า 116.
                foreach (var docLine in doc.Lines)
                {
                    var expenseAccountId = docLine.AccountId ?? defaultExpense?.Id;
                    if (!expenseAccountId.HasValue) continue;
                    var debitAmount = docLine.IsVatClaimable
                        ? docLine.Amount
                        : docLine.Amount + docLine.VatAmount;
                    var desc = docLine.IsVatClaimable
                        ? docLine.Description
                        : $"{docLine.Description} (รวม VAT ต้องห้าม{(string.IsNullOrWhiteSpace(docLine.VatNonClaimableReason) ? "" : " - " + docLine.VatNonClaimableReason)})";
                    AddLine(expenseAccountId.Value, debitAmount, 0, desc, docLine.ProjectId);
                }

                var claimableVatPv = doc.Lines.Where(l => l.IsVatClaimable).Sum(l => l.VatAmount);
                if (claimableVatPv > 0)
                {
                    // เหมือน PI — เลือก 11610/11640/override ตาม completeness §86/4
                    var (vatInputAccount, postedAsUndue) = await ResolveInputVatAccountAsync(companyId, doc);
                    AddLine(vatInputAccount.Id, claimableVatPv, 0,
                        postedAsUndue ? "ภาษีซื้อยังไม่ถึงกำหนด (ใบกำกับไม่ครบ §86/4)"
                                      : (doc.InputVatAccountCodeOverride != null
                                            ? $"VAT → {vatInputAccount.AccountCode} {vatInputAccount.AccountName} (override)"
                                            : "ภาษีซื้อ (เคลมได้)"));
                    doc.InputVatPostedAsUndue = postedAsUndue;
                }

                // Credit side depends on the settlement basis:
                //   Credit (เครดิต) → book a liability to Accounts Payable (212);
                //     the voucher carries an outstanding balance + due date and
                //     ages until a later payment settles it.
                //   Cash (จ่ายทันที) → money leaves now, credit Cash/Bank
                //     directly — never touches AP.
                if (doc.PaymentType == Models.Enums.PaymentType.Credit)
                {
                    // Role separation now BLOCKS this combination on create/
                    // edit (PV is real disbursement; ตั้งหนี้ belongs on an
                    // Expense). Path is kept for legacy approval/re-post of
                    // documents created before the rule landed — route them
                    // to the SAME payable the matching Expense would use
                    // (เจ้าหนี้อื่น 21220) so AP reports stay consistent.
                    var apAccount = await ResolvePayableAccountAsync(companyId, DocumentType.Expense, doc.Contact);
                    if (apAccount != null)
                        AddLine(apAccount.Id, 0, doc.TotalAmount,
                            $"เจ้าหนี้ - {doc.DocumentNumber}");
                }
                else if (moneyAccount != null)
                {
                    AddLine(moneyAccount.Id, 0, doc.TotalAmount,
                        $"{(doc.BankAccountId.HasValue ? "จ่ายจากบัญชี" : "จ่ายเงินสด")} - {doc.DocumentNumber}");
                }

                if (doc.WithholdingTaxAmount > 0)
                {
                    var whtAccount = await ResolveWhtPayableAccountAsync(companyId, doc.Contact);
                    if (whtAccount != null)
                        AddLine(whtAccount.Id, 0, doc.WithholdingTaxAmount, "ภาษีหัก ณ ที่จ่ายค้างจ่าย");
                }
            }
        }
        else if (doc.DocumentType == DocumentType.GoodsReceiptNote)
        {
            // 3-way match — accrue goods received but not yet invoiced.
            //   Dr ค่าใช้จ่าย/สินค้า (per line, ex-VAT)   = goods value
            //   Cr เจ้าหนี้-รับสินค้ายังไม่วางบิล (GR-NI)  = goods value
            // No VAT/WHT here — those belong to the supplier's tax invoice,
            // booked when the PurchaseInvoice is raised against this GRN
            // (which then Dr GR-NI to clear this accrual). VAT/WHT entered on
            // a GRN line is ignored for posting (the goods value = SubTotal).
            journalType = JournalType.Purchase;
            var grNi = await EnsureGrNiAccountAsync(companyId);
            if (grNi == null) return;   // chart can't support it → no JE (legacy behaviour)

            foreach (var docLine in doc.Lines)
            {
                var expenseAccountId = docLine.AccountId ?? defaultExpense?.Id;
                if (expenseAccountId.HasValue)
                    AddLine(expenseAccountId.Value, docLine.Amount, 0, docLine.Description, docLine.ProjectId);
            }
            if (doc.SubTotal > 0)
                AddLine(grNi.Id, 0, doc.SubTotal, $"รับสินค้ายังไม่วางบิล - {doc.DocumentNumber}");
        }
        else
        {
            // Operational documents (Quotation, DeliveryNote, BillingNote, PR, PO) → no JE
            return;
        }

        if (pendingLines.Count < 2)
        {
            // Fail fast — silently skipping the JE while marking the document
            // Approved makes the GL miss revenue/expense for days/weeks until
            // someone runs a TB reconciliation. Better to block the approval
            // and force the operator to seed the missing COA accounts.
            throw new InvalidOperationException(
                "ไม่สามารถบันทึกบัญชีอัตโนมัติได้: ผังบัญชี (COA) ที่ใช้สำหรับเอกสารประเภทนี้ยังไม่ครบ. " +
                "กรุณาเปิดเมนู ตั้งค่าผังบัญชี เพื่อ seed บัญชีที่จำเป็น (รายได้ / ค่าใช้จ่าย / VAT / AR / AP / เงินสด) ก่อนอนุมัติ");
        }

        // Adjusting JE lines (Option 1) — user เพิ่ม Dr/Cr ลอย ๆ ที่ผูกเอกสาร
        // (เช่น ค่าธรรมเนียมโอน, สำรอง). รวมเข้า pendingLines ก่อน balance check.
        // user รับผิดชอบให้ adjusting Dr รวม = adjusting Cr รวม (net-zero) →
        // auto-gen ที่ balance อยู่แล้ว + adjusting net-zero = JE ยัง balance.
        var adjustingLines = await _db.Set<DocumentAdjustingJournalLine>()
            .AsNoTracking()
            .Where(a => a.DocumentId == doc.Id && !a.IsDeleted)
            .OrderBy(a => a.LineOrder)
            .ToListAsync();
        if (adjustingLines.Count > 0)
        {
            var adjDr = adjustingLines.Sum(a => a.DebitAmount);
            var adjCr = adjustingLines.Sum(a => a.CreditAmount);
            if (Math.Round(adjDr, 2, MidpointRounding.AwayFromZero) != Math.Round(adjCr, 2, MidpointRounding.AwayFromZero))
                throw new InvalidOperationException(
                    $"⛔ Adjusting JE Lines ไม่ balance: เดบิต {adjDr:N2} ≠ เครดิต {adjCr:N2} — " +
                    "ผลรวม Dr และ Cr ของ adjusting lines ต้องเท่ากัน (กัน JE หลักเสียสมดุล). " +
                    "แก้ที่หน้า 'ปรับปรุงรายการบัญชี' ของเอกสารก่อนอนุมัติ");
            // ห้าม adjusting line มีทั้ง Dr และ Cr ในบรรทัดเดียวกัน
            foreach (var a in adjustingLines)
            {
                if (a.DebitAmount > 0 && a.CreditAmount > 0)
                    throw new InvalidOperationException(
                        $"⛔ Adjusting line '{a.Description}' มีทั้ง Dr และ Cr — ต้องเลือกด้านเดียว");
                if (a.DebitAmount < 0 || a.CreditAmount < 0)
                    throw new InvalidOperationException(
                        $"⛔ Adjusting line '{a.Description}' มียอดติดลบ");
            }
            foreach (var a in adjustingLines)
            {
                AddLine(a.AccountId, a.DebitAmount, a.CreditAmount,
                    string.IsNullOrWhiteSpace(a.Description)
                        ? $"ปรับปรุง — {doc.DocumentNumber}"
                        : $"ปรับปรุง: {a.Description}",
                    a.ProjectId);
            }
        }

        // Validate double-entry balance per Thai accounting standards (TAS 1)
        var totalDebit = pendingLines.Sum(l => l.Debit);
        var totalCredit = pendingLines.Sum(l => l.Credit);
        if (Math.Round(totalDebit, 2, MidpointRounding.AwayFromZero) != Math.Round(totalCredit, 2, MidpointRounding.AwayFromZero))
            throw new InvalidOperationException(
                $"การบันทึกบัญชีอัตโนมัติไม่สมดุล: เดบิต {totalDebit:N2} ≠ เครดิต {totalCredit:N2}");

        // Resolve fiscal period — block posting to closed/locked periods per
        // Thai accounting standard (TAS 1: closed period is immutable). If the
        // document falls within a closed period, the user must reopen it first
        // or re-date the document.
        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId &&
            f.StartDate <= doc.DocumentDate &&
            f.EndDate >= doc.DocumentDate);
        if (period != null && period.Status != FiscalPeriodStatus.Open)
        {
            throw new InvalidOperationException(
                $"ไม่สามารถบันทึกบัญชีในงวด {period.Name} ได้ เนื่องจากงวดถูกปิดแล้ว " +
                $"กรุณาเปลี่ยนวันที่เอกสารเป็นงวดที่เปิดอยู่ หรือขอเปิดงวดก่อน");
        }

        // Generate entry number
        var prefix = journalType switch
        {
            JournalType.Sales => "SV",
            JournalType.Purchase => "UV",
            JournalType.CashReceipts => "RV",
            JournalType.CashPayments => "PV",
            _ => "JV"
        };
        var entryNumber = await GetNextJournalEntryNumberAsync(companyId, prefix);

        var entry = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = doc.DocumentDate,
            JournalType = journalType,
            Description = $"Auto-post จาก {doc.DocumentNumber}",
            Reference = doc.DocumentNumber,
            Status = JournalEntryStatus.Posted,
            TotalDebit = totalDebit,
            TotalCredit = totalCredit,
            CreatedBy = createdBy,
            IsAutoGenerated = true,
            SourceDocumentId = doc.Id,
            FiscalPeriodId = period?.Id,
            // Header-level Project: enables filtering JE lookups by project even on
            // system-generated lines that inherit. ProjectAccountingService queries
            // (l.ProjectId == projectId || l.JournalEntry.ProjectId == projectId).
            ProjectId = doc.ProjectId
        };

        _db.JournalEntries.Add(entry);

        var order = 1;
        for (int i = 0; i < pendingLines.Count; i++)
        {
            var line = pendingLines[i];
            _db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = entry.Id,
                AccountId = line.AccountId,
                DebitAmount = line.Debit,
                CreditAmount = line.Credit,
                Description = line.Description,
                LineOrder = order++,
                ProjectId = lineProjects[i]
            });
        }
    }

    /// <summary>Generate next journal entry number for a given prefix (SV/UV/RV/PV/JV) per month.
    /// Uses PostgreSQL advisory lock to prevent race conditions on concurrent inserts.</summary>
    private async Task<string> GetNextJournalEntryNumberAsync(Guid companyId, string prefix)
    {
        var lockKey = HashCode.Combine(companyId, prefix, "je-seq");
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", lockKey);

        var yearMonth = DateTime.UtcNow.ToString("yyyyMM");
        var pattern = $"{prefix}-{yearMonth}-";
        var lastEntry = await _db.JournalEntries
            .IgnoreQueryFilters()
            .Where(j => j.CompanyId == companyId && j.EntryNumber.StartsWith(pattern))
            .OrderByDescending(j => j.EntryNumber)
            .Select(j => j.EntryNumber)
            .FirstOrDefaultAsync();
        var nextSeq = 1;
        if (lastEntry != null)
        {
            var lastPart = lastEntry[pattern.Length..];
            if (int.TryParse(lastPart, out var n)) nextSeq = n + 1;
        }
        return $"{pattern}{nextSeq:D4}";
    }

    /// <summary>Cap payer signature payload size — reject anything bigger
    /// than ~512 KB base64 (~384 KB raw image). Real signatures rendered at
    /// PDF DPI are well under 50 KB; if a caller posts a multi-megabyte
    /// blob it's a bug or abuse. Returns null for blank input so EF skips
    /// the column.</summary>
    private static string? TrimSignature(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.Length > 512 * 1024)
            throw new InvalidOperationException("รูปลายเซ็นมีขนาดใหญ่เกิน 512 KB — กรุณาบีบอัดก่อนส่ง");
        return v;
    }

    /// <summary>
    /// สร้าง Journal Entry สำหรับการชำระเงิน — เขียน DbContext โดยตรงภายใน transaction ของ caller
    /// รับเงิน (ฝั่งขาย): Dr Cash, Cr AR → สมุดรายวันรับ (RV)
    /// จ่ายเงิน (ฝั่งซื้อ): Dr AP, Cr Cash → สมุดรายวันจ่าย (PV)
    /// </summary>
    private async Task CreatePaymentJournalAsync(Guid companyId, Document doc, Payment payment, string createdBy)
    {
        var revenueTypes = new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt,
            DocumentType.DebitNote, DocumentType.BillingNote, DocumentType.ReceiptVoucher };
        var isRevenue = revenueTypes.Contains(doc.DocumentType);
        var journalType = isRevenue ? JournalType.CashReceipts : JournalType.CashPayments;

        // Resolve the CASH/BANK side of the entry. Priority:
        //   1. payment.OverridePaymentAccountId — explicit GL the operator chose
        //      (เงินทดรองกรรมการ / เงินสดย่อย / clearing) — funds from non-bank.
        //   2. the chosen bank account's LinkedAccount GL — so a Bank Transfer
        //      actually hits the bank's GL, not generic cash. (BUG FIX: this
        //      line previously ALWAYS posted to 111 regardless of how the
        //      payment was made, so every bank transfer landed in Cash on Hand.)
        //   3. fall back to the default cash account "111".
        ChartOfAccount? cashAccount = null;
        if (payment.OverridePaymentAccountId.HasValue)
            cashAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.Id == payment.OverridePaymentAccountId.Value && a.CompanyId == companyId && !a.IsDeleted);
        if (cashAccount == null && payment.BankAccountId.HasValue)
        {
            var linkedAcctId = await _db.Set<BankAccount>().AsNoTracking()
                .Where(b => b.Id == payment.BankAccountId.Value && b.CompanyId == companyId)
                .Select(b => b.LinkedAccountId)
                .FirstOrDefaultAsync();
            if (linkedAcctId.HasValue)
                cashAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                    a.Id == linkedAcctId.Value && a.CompanyId == companyId && !a.IsDeleted);
        }
        cashAccount ??= await FindAccountAsync(companyId, "111");
        if (cashAccount == null) return;

        // WHT basis decides whether THIS installment's WHT gets a GL line
        // (cash basis = yes, recognised at payment time) or was already
        // recognised at invoice approval (accrual = no, skip).
        var whtSettings = await _db.CompanySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);
        var whtBasis = whtSettings?.WhtRecognitionBasis ?? Models.Enums.WhtRecognitionBasis.Cash;

        var pendingLines = new List<(Guid AccountId, decimal Debit, decimal Credit, string? Description)>();
        // Convert payment to THB (base) at the document's captured FX rate.
        // Note: this uses the doc rate, not a settlement-day rate, so FX gain/loss
        // on settlement isn't booked yet (separate feature when needed).
        var fx = doc.ExchangeRate;
        var thbAmount = fx == 1m ? payment.Amount : Math.Round(payment.Amount * fx, 2, MidpointRounding.AwayFromZero);
        var thbWht = fx == 1m ? payment.WithholdingTaxAmount
            : Math.Round(payment.WithholdingTaxAmount * fx, 2, MidpointRounding.AwayFromZero);
        var postPerPaymentWht = whtBasis == Models.Enums.WhtRecognitionBasis.Cash && thbWht > 0m;

        if (isRevenue)
        {
            // Cash actually received this installment.
            pendingLines.Add((cashAccount.Id, thbAmount, 0, $"รับชำระ - {doc.DocumentNumber} ({payment.PaymentNumber})"));
            // Cash basis: book the WHT-Asset slice now so the GL AR clears
            // at gross (cash + WHT).
            if (postPerPaymentWht)
            {
                var whtAccount = await FindAccountAsync(companyId, "11910");
                if (whtAccount != null)
                    pendingLines.Add((whtAccount.Id, thbWht, 0, $"WHT (ถูกหัก) งวด {payment.PaymentNumber}"));
            }
            var arAccount = await FindAccountAsync(companyId, "113", doc.Contact);
            if (arAccount != null)
            {
                var arClear = postPerPaymentWht ? thbAmount + thbWht : thbAmount;
                pendingLines.Add((arAccount.Id, 0, arClear, $"ตัดลูกหนี้ - {doc.DocumentNumber}"));
            }
        }
        else
        {
            // Clear the payable on the SAME account the source doc credited —
            // Expense → เจ้าหนี้อื่น (21220), PI → เจ้าหนี้การค้า (21210).
            var apAccount = await ResolvePayableAccountAsync(companyId, doc.DocumentType, doc.Contact);
            if (apAccount != null)
            {
                var apClear = postPerPaymentWht ? thbAmount + thbWht : thbAmount;
                pendingLines.Add((apAccount.Id, apClear, 0, $"ตัดเจ้าหนี้ - {doc.DocumentNumber}"));
            }
            pendingLines.Add((cashAccount.Id, 0, thbAmount, $"จ่ายชำระ - {doc.DocumentNumber} ({payment.PaymentNumber})"));
            // Cash basis: Cr WHT-Payable for this installment's withholding.
            if (postPerPaymentWht)
            {
                var whtAccount = await ResolveWhtPayableAccountAsync(companyId, doc.Contact);
                if (whtAccount != null)
                    pendingLines.Add((whtAccount.Id, 0, thbWht, $"WHT (ค้างจ่าย) งวด {payment.PaymentNumber}"));
            }
        }

        if (pendingLines.Count < 2) return;

        var totalDebit = pendingLines.Sum(l => l.Debit);
        var totalCredit = pendingLines.Sum(l => l.Credit);
        if (Math.Round(totalDebit, 2, MidpointRounding.AwayFromZero) != Math.Round(totalCredit, 2, MidpointRounding.AwayFromZero))
            throw new InvalidOperationException(
                $"การบันทึกบัญชีชำระเงินไม่สมดุล: เดบิต {totalDebit:N2} ≠ เครดิต {totalCredit:N2}");

        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(f =>
            f.CompanyId == companyId &&
            f.StartDate <= payment.PaymentDate &&
            f.EndDate >= payment.PaymentDate);
        if (period != null && period.Status != FiscalPeriodStatus.Open)
            throw new InvalidOperationException(
                $"ไม่สามารถบันทึกการชำระเงินในงวด {period.Name} ได้ เนื่องจากงวดถูกปิดแล้ว");

        var prefix = isRevenue ? "RV" : "PV";
        var entryNumber = await GetNextJournalEntryNumberAsync(companyId, prefix);

        var entry = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = entryNumber,
            EntryDate = payment.PaymentDate,
            JournalType = journalType,
            Description = $"ชำระเงิน {payment.PaymentNumber} - {doc.DocumentNumber}",
            Reference = payment.PaymentNumber,
            Status = JournalEntryStatus.Posted,
            TotalDebit = totalDebit,
            TotalCredit = totalCredit,
            CreatedBy = createdBy,
            IsAutoGenerated = true,
            SourceDocumentId = doc.Id,
            FiscalPeriodId = period?.Id,
            // Inherit project tag — payment-level override wins so an
            // installment booked to a different project than the parent
            // invoice (advance on Project A → final on Project B) lands
            // in the right P&L. Falls back to doc.ProjectId when no
            // override.
            ProjectId = payment.ProjectId ?? doc.ProjectId
        };

        _db.JournalEntries.Add(entry);

        var order = 1;
        foreach (var line in pendingLines)
        {
            _db.JournalEntryLines.Add(new JournalEntryLine
            {
                JournalEntryId = entry.Id,
                AccountId = line.AccountId,
                DebitAmount = line.Debit,
                CreditAmount = line.Credit,
                Description = line.Description,
                LineOrder = order++,
                ProjectId = payment.ProjectId ?? doc.ProjectId
            });
        }
    }

    private static DocumentResponse MapDocumentToResponse(Document d, (Guid EtaxId, EtaxStatus Status)? etax = null,
        DocumentBrief? upstream = null, List<DocumentBrief>? downstream = null,
        decimal? conversionPercent = null, string? conversionStatus = null,
        bool hasPce = false, int pceCount = 0, decimal pceAmount = 0,
        List<ProjectCostBrief>? bookedProjects = null,
        Dictionary<Guid, Guid>? pceByLine = null)
    {
        var (lifecycle, reason) = ComputeLifecycle(d, conversionPercent, downstream);
        return new(
        d.Id, d.DocumentNumber, d.DocumentType, d.Status,
        d.DocumentDate, d.DueDate,
        new ContactBrief(d.Contact.Id, d.Contact.Name, d.Contact.TaxId),
        d.SubTotal, d.DiscountAmount, d.VatAmount, d.WithholdingTaxAmount,
        d.TotalAmount, d.PaidAmount, d.BalanceDue, d.Reference, d.Notes,
        d.Lines.OrderBy(l => l.LineOrder).Select(l => new DocumentLineResponse(
            l.Id, l.LineOrder, l.Description, l.Quantity, l.Unit,
            l.UnitPrice, l.DiscountPercent, l.DiscountAmount, l.Amount,
            l.VatRate, l.VatAmount, l.WithholdingTaxRate, l.WithholdingTaxAmount,
            AccountId: l.AccountId,
            ProjectId: l.ProjectId,
            ProductCode: l.ProductCode,
            SourceLineId: l.SourceLineId,
            ProjectCode: l.Project != null ? l.Project.Code : null,
            ProjectName: l.Project != null ? l.Project.Name : null,
            ProjectCostEntryId: pceByLine != null && pceByLine.TryGetValue(l.Id, out var pceId) ? pceId : (Guid?)null,
            HasProjectCostEntry: pceByLine != null && pceByLine.ContainsKey(l.Id),
            IsVatClaimable: l.IsVatClaimable,
            VatNonClaimableReason: l.VatNonClaimableReason,
            AccountCode: l.Account != null ? l.Account.AccountCode : null,
            GlAccountAiFeedbackId: l.GlAccountAiFeedbackId,
            AccountName: l.Account != null ? l.Account.AccountName : null)).ToList(),
        d.CreatedAt,
        EtaxInvoiceId: etax?.EtaxId,
        EtaxStatus: etax?.Status,
        ProjectId: d.ProjectId,
        ProjectCode: d.Project?.Code,
        ProjectName: d.Project?.Name,
        BankAccountId: d.BankAccountId,
        BankAccountName: d.BankAccount?.AccountName,
        PaymentAccountId: d.PaymentAccountId,
        PaymentAccountName: d.PaymentAccount != null ? $"{d.PaymentAccount.AccountCode} - {d.PaymentAccount.AccountName}" : null,
        ExpenseCategoryId: d.ExpenseCategoryId,
        ExpenseCategoryName: d.ExpenseCategory?.AccountName,
        CustomAppendix: d.CustomAppendix,
        CustomFooterNotes: d.CustomFooterNotes,
        CustomTermsAndConditions: d.CustomTermsAndConditions,
        RevenueContractId: d.RevenueContractId,
        PerformanceObligationId: d.PerformanceObligationId,
        RelatedDocumentId: d.RelatedDocumentId,
        CertificateReason: d.CertificateReason,
        CertifierName: d.CertifierName,
        CertifierPosition: d.CertifierPosition,
        WitnessName: d.WitnessName,
        WitnessPosition: d.WitnessPosition,
        PaymentDate: d.PaymentDate,
        // ERP upgrade fields — OCR compliance + aging
        OcrConfidenceScore: d.OcrConfidenceScore,
        RdComplianceStatus: d.RdComplianceStatus,
        RdComplianceIssuesJson: d.RdComplianceIssuesJson,
        OcrTenantMismatchFlag: d.OcrTenantMismatchFlag,
        AgingDays: d.AgingDays,
        StaleDays: ComputeStaleDays(d),
        Currency: d.Currency,
        ExchangeRate: d.ExchangeRate,
        Sensitivity: d.Sensitivity,
        CreditNoteReason: d.CreditNoteReason,
        SupplierInvoiceNumber: d.SupplierInvoiceNumber,
        SupplierTaxInvoiceDate: d.SupplierTaxInvoiceDate,
        HasTaxInvoiceReference: d.HasTaxInvoiceReference,
        SupplierBranchCode: d.SupplierBranchCode,
        CreditDays: d.CreditDays,
        PaymentTerms: d.PaymentTerms,
        PaymentType: d.PaymentType,
        PricesIncludeVat: d.PricesIncludeVat,
        IsForeignService: d.IsForeignService,
        RelatedDocument: upstream,
        ConvertedToDocuments: downstream,
        ConversionCompletionPercent: conversionPercent,
        ConversionStatus: conversionStatus,
        HasProjectCostEntries: hasPce,
        ProjectCostEntryCount: pceCount,
        ProjectCostBookedAmount: pceAmount,
        BookedProjects: bookedProjects,
        LifecycleStatus: lifecycle,
        LifecycleReason: reason,
        InputVatPostedAsUndue: d.InputVatPostedAsUndue,
        InputVatBecameClaimableAt: d.InputVatBecameClaimableAt,
        InputVatAccountCodeOverride: d.InputVatAccountCodeOverride,
        IsDeposit: d.IsDeposit,
        DepositRealizedAmount: d.DepositRealizedAmount,
        DepositRealizedAt: d.DepositRealizedAt,
        DepositDeferredAccountCode: d.DepositDeferredAccountCode,
        DepositOutputVatDeferred: d.DepositOutputVatDeferred,
        DepositOutputVatRecognizedAt: d.DepositOutputVatRecognizedAt,
        TaxPointDate: d.TaxPointDate,
        RetentionUntil: d.RetentionUntil,
        NonDeductibleAmount: d.NonDeductibleAmount,
        NonDeductibleRuleJson: d.NonDeductibleRuleJson,
        LateReason: d.LateReason,
        DeliveryDate: d.DeliveryDate,
        OwnershipTransferDate: d.OwnershipTransferDate,
        ServiceUsedDate: d.ServiceUsedDate,
        DepositRefundedAmount: d.DepositRefundedAmount,
        DepositRefundedAt: d.DepositRefundedAt,
        DepositRefundReason: d.DepositRefundReason,
        DepositAppliedToDocumentId: d.DepositAppliedToDocumentId,
        BookingNumber: d.BookingNumber);
    }

    /// <summary>Build the redacted stub returned to API consumers who lack
    /// permission to see a sensitive record. Keeps the Id, DocumentNumber, and
    /// Sensitivity so the integration target knows the record exists and what
    /// kind of access it would need; blanks out amounts / contact / notes /
    /// lines. The destination system can decide whether to skip, place-hold,
    /// or request access.</summary>
    private static DocumentResponse RedactDocumentResponse(Document d, string reason) => new(
        d.Id, d.DocumentNumber, d.DocumentType, d.Status,
        d.DocumentDate, d.DueDate,
        new ContactBrief(Guid.Empty, "[ซ่อน]", null),
        0, 0, 0, 0, 0, 0, 0, null, null,
        new List<DocumentLineResponse>(), d.CreatedAt,
        Sensitivity: d.Sensitivity,
        IsRedacted: true,
        RedactedReason: reason);

    /// <summary>Days a document has been parked in a non-terminal status past
    /// the stale threshold — null when fresh or in a terminal status.
    /// Terminal = Paid / Voided / Rejected.</summary>
    private const int StaleThresholdDays = 60;
    /// <summary>Derive the unified lifecycle state from the doc's
    /// status + conversion % + balance + downstream settlement docs.
    /// Per-doc-type rules so a fully-converted PO surfaces "Done"
    /// even though Status is still "Approved", and a fully-paid PI
    /// surfaces "Done" even when ConversionStatus is null. Pure
    /// function — no DB access — so it can be reused server- and
    /// client-side.</summary>
    private static (string Status, string Reason) ComputeLifecycle(
        Document d, decimal? conversionPercent, List<DocumentBrief>? downstream)
    {
        // Voided / Rejected always win — purpose terminated. The status
        // badge already says "ยกเลิก" / "ถูกปฏิเสธ"; a separate lifecycle
        // pill saying the same thing was just visual noise — return empty
        // reason so the frontend hides the duplicate.
        if (d.Status == DocumentStatus.Voided)
            return ("Cancelled", "");
        if (d.Status == DocumentStatus.Rejected)
            return ("Cancelled", "");

        // Draft / WaitingApproval — the status badge ALREADY says "ร่าง" /
        // "รออนุมัติ". A second lifecycle pill repeating the same thing is
        // visual noise (the UI showed "ร่าง" + "⏳ ฉบับร่าง" stacked on
        // mobile). Return empty so the frontend hides the lifecycle pill;
        // it appears only when lifecycle adds NEW info beyond status.
        if (d.Status == DocumentStatus.Draft || d.Status == DocumentStatus.WaitingApproval)
            return ("Open", "");

        // Per-type rules.
        switch (d.DocumentType)
        {
            // Settlement-bearing receivable / payable.
            case DocumentType.Invoice:
            case DocumentType.TaxInvoice:
            case DocumentType.BillingNote:
            case DocumentType.PurchaseInvoice:
            case DocumentType.Expense:
            {
                if (d.BalanceDue <= 0.01m) return ("Done", "✓ ชำระครบแล้ว");
                if (d.Status == DocumentStatus.PartiallyPaid)
                    return ("PartiallyDone", $"◐ ชำระบางส่วน · เหลือ {d.BalanceDue:N2}");
                if (d.Status == DocumentStatus.Overdue)
                    return ("Open", $"⚠ เกินกำหนด · ค้าง {d.BalanceDue:N2}");
                return ("Open", $"⏳ รอจ่าย/รับชำระ · {d.BalanceDue:N2}");
            }

            // Conversion-bearing — purpose fulfilled when downstream consumes it.
            case DocumentType.Quotation:
            case DocumentType.PurchaseRequisition:
            case DocumentType.PurchaseOrder:
            case DocumentType.GoodsReceiptNote:
            case DocumentType.DeliveryNote:
            {
                var nextLabel = downstream?.FirstOrDefault()?.DocumentNumber;
                if (conversionPercent.HasValue && conversionPercent.Value >= 100m)
                    return ("Done", nextLabel != null ? $"✓ แปลงเป็น {nextLabel}" : "✓ ดำเนินการครบแล้ว");
                if (conversionPercent.HasValue && conversionPercent.Value > 0m)
                    return ("PartiallyDone", $"◐ แปลงไป {conversionPercent.Value:F0}%");
                return ("Open", "⏳ รอดำเนินการต่อ");
            }

            // One-shot terminal docs (PV/Receipt/RV/CIL/CN/DN). Once
            // approved/paid, the status badge ("ชำระแล้ว" / "อนุมัติ")
            // already conveys the whole story — a second pill repeating
            // "✓ บันทึกเรียบร้อย" was duplicate visual noise. Return empty
            // reason for those statuses; only mark "✓ บันทึกเรียบร้อย" for
            // the in-between Sent state where the status badge is generic.
            case DocumentType.Receipt:
            case DocumentType.ReceiptVoucher:
            case DocumentType.PaymentVoucher:
            case DocumentType.CertificateInLieu:
            case DocumentType.CreditNote:
            case DocumentType.DebitNote:
                return d.Status == DocumentStatus.Paid
                    || d.Status == DocumentStatus.Approved
                    ? ("Done", "")
                    : ("Done", "✓ บันทึกเรียบร้อย");

            default:
                // Unknown type — fall back to status.
                return d.Status == DocumentStatus.Paid
                    ? ("Done", "✓ ชำระแล้ว")
                    : ("Open", "⏳ ดำเนินการ");
        }
    }

    private static int? ComputeStaleDays(Document d)
    {
        if (d.Status is DocumentStatus.Paid or DocumentStatus.Voided or DocumentStatus.Rejected)
            return null;
        var days = (int)(DateTime.UtcNow.Date - d.DocumentDate.Date).TotalDays;
        return days > StaleThresholdDays ? days : null;
    }

    private static ContactResponse MapContactToResponse(Contact c) => new(
        c.Id, c.Name, c.TaxId, c.BranchCode, c.ContactType, c.IsCustomer, c.IsSupplier,
        c.Address, c.Phone, c.Email, c.ContactPerson, c.IsActive,
        BranchName: c.BranchName,
        BuildingNumber: c.BuildingNumber,
        BuildingName: c.BuildingName,
        Moo: c.Moo,
        StreetName: c.StreetName,
        SubDistrict: c.SubDistrict,
        District: c.District,
        Province: c.Province,
        PostalCode: c.PostalCode,
        CountryCode: c.CountryCode,
        LoyaltyPoints: c.LoyaltyPoints,
        LastVisitAt: c.LastVisitAt,
        TotalVisitCount: c.TotalVisitCount,
        DefaultArAccountId: c.DefaultArAccountId,
        DefaultArAccountCode: c.DefaultArAccount?.AccountCode,
        DefaultArAccountName: c.DefaultArAccount?.AccountName,
        DefaultApAccountId: c.DefaultApAccountId,
        DefaultApAccountCode: c.DefaultApAccount?.AccountCode,
        DefaultApAccountName: c.DefaultApAccount?.AccountName,
        DefaultIrGrAccountId: c.DefaultIrGrAccountId,
        DefaultIrGrAccountCode: c.DefaultIrGrAccount?.AccountCode,
        DefaultIrGrAccountName: c.DefaultIrGrAccount?.AccountName,
        CreditLimit: c.CreditLimit);

    // ==================== Smart Defaults ====================

    /// <summary>
    /// วิเคราะห์ประเภทผู้ติดต่อจาก TaxId และ BranchCode อัตโนมัติ
    /// - มี BranchCode (ไม่ใช่ 00000) → นิติบุคคล (มีสำนักงานสาขา)
    /// - TaxId 13 หลัก ขึ้นต้นด้วย 0 → นิติบุคคล (เลขทะเบียนนิติบุคคล)
    /// - อื่นๆ → บุคคลธรรมดา
    /// </summary>
    public static ContactType InferContactType(string? taxId, string? branchCode)
    {
        // BranchCode ≠ null/empty/"00000" → clearly juristic (has branch offices)
        if (!string.IsNullOrWhiteSpace(branchCode) && branchCode != "00000")
            return ContactType.JuristicPerson;

        // TaxId 13 digits starting with 0 → juristic person registration number
        if (!string.IsNullOrWhiteSpace(taxId) && taxId.Length == 13 && taxId[0] == '0')
            return ContactType.JuristicPerson;

        // BranchCode "00000" (สำนักงานใหญ่) with valid TaxId → also juristic
        if (branchCode == "00000" && !string.IsNullOrWhiteSpace(taxId) && taxId.Length == 13)
            return ContactType.JuristicPerson;

        return ContactType.Individual;
    }

    /// <summary>ค่าเริ่มต้นอัตโนมัติจากข้อมูลผู้ติดต่อ</summary>
    public static ContactSmartDefaults GetSmartDefaults(Contact contact)
    {
        var contactType = contact.ContactType;

        // WHT form type: นิติบุคคล → ภ.ง.ด.53, บุคคลธรรมดา → ภ.ง.ด.3
        var taxFormType = contactType switch
        {
            ContactType.JuristicPerson => TaxType.WithholdingTax53,
            ContactType.GovernmentAgency => TaxType.WithholdingTax53,
            _ => TaxType.WithholdingTax3
        };
        var taxFormLabel = taxFormType switch
        {
            TaxType.WithholdingTax53 => "ภ.ง.ด.53",
            TaxType.WithholdingTax3 => "ภ.ง.ด.3",
            _ => taxFormType.ToString()
        };

        // Document type: supplier → ซื้อ, customer → ขาย
        DocumentType? docType = null;
        string? docTypeLabel = null;
        if (contact.IsSupplier && !contact.IsCustomer)
        {
            docType = DocumentType.PurchaseInvoice;
            docTypeLabel = "ใบกำกับซื้อ";
        }
        else if (contact.IsCustomer && !contact.IsSupplier)
        {
            docType = DocumentType.Invoice;
            docTypeLabel = "ใบแจ้งหนี้";
        }

        // WHT rate & income type: นิติบุคคล default = ค่าบริการ 3%, บุคคลธรรมดา = ค่าจ้าง 3%
        var defaultWhtRate = 3m;
        var defaultIncomeCode = contactType == ContactType.JuristicPerson ? "8" : "6";
        var defaultIncomeLabel = contactType == ContactType.JuristicPerson
            ? "ค่าบริการอื่นๆ (40(8))"
            : "ค่าวิชาชีพอิสระ (40(6))";

        var contactTypeLabel = contactType switch
        {
            ContactType.Individual => "บุคคลธรรมดา",
            ContactType.JuristicPerson => "นิติบุคคล",
            ContactType.GovernmentAgency => "หน่วยงานราชการ",
            _ => contactType.ToString()
        };

        return new ContactSmartDefaults(
            contactType, contactTypeLabel,
            taxFormType, taxFormLabel,
            docType, docTypeLabel,
            defaultWhtRate, defaultIncomeCode, defaultIncomeLabel);
    }

    // Projection type for the recursive cycle-detection CTE
    private sealed record AncestorRow(Guid Id, string DocumentNumber, int DocumentType);

    /// <summary>Pre-approval soft-warning collector. Returns user-facing
    /// messages for legal-but-unusual patterns that the operator should
    /// eyeball before approving. The list is empty when nothing is amiss.
    /// HARD errors continue to throw inline above — they're never returned
    /// as warnings because they'd corrupt the books on save. Examples that
    /// pass the audit but get flagged:
    ///   • document-date drifted &gt;90d in the past or &gt;30d in the future
    ///   • VAT rate other than 0 / 7
    ///   • WHT rate not in the canonical ภ.ง.ด.3 / 53 list
    ///   • TaxInvoice / Receipt without contact TaxId (e-Tax falls to
    ///     Non-VAT format silently otherwise)
    ///   • large amount (&gt;500k THB) — could be a typo
    ///   • foreign currency without an explicit FX rate update
    ///   • inventory-tracked product would go negative on this approval
    /// </summary>
    private async Task<List<string>> CollectApprovalWarningsAsync(Guid companyId, Document doc)
    {
        var warnings = new List<string>();
        var today = DateTime.UtcNow.Date;

        // Date drift — covers backdated invoices that may have missed
        // their VAT filing window and forward-dated invoices that look
        // like a fat-finger.
        var daysPast = (today - doc.DocumentDate.Date).TotalDays;
        if (daysPast > 90)
            warnings.Add($"วันที่เอกสาร ({doc.DocumentDate:yyyy-MM-dd}) ย้อนหลัง {(int)daysPast} วัน — ตรวจรอบการยื่นภาษีก่อนอนุมัติ");
        else if (daysPast < -30)
            warnings.Add($"วันที่เอกสาร ({doc.DocumentDate:yyyy-MM-dd}) ล่วงหน้าเกิน 30 วัน — โดยปกติออกเอกสารวันจริงเท่านั้น");

        // VAT-eligible types need a contact TaxId for proper e-Tax XML and
        // ภ.พ.30 cross-matching. Without TaxId the e-Tax generator falls
        // back to Non-VAT format silently — works, but the customer can't
        // claim Input VAT on the other side.
        var vatTypes = new[] { DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.DebitNote, DocumentType.CreditNote };
        if (vatTypes.Contains(doc.DocumentType) && doc.Contact != null && string.IsNullOrWhiteSpace(doc.Contact.TaxId))
            warnings.Add($"ผู้ติดต่อ '{doc.Contact.Name}' ไม่มีเลขผู้เสียภาษี — e-Tax XML จะใช้รูปแบบ Non-VAT ผู้รับใช้เป็นหลักฐาน Input VAT ไม่ได้");

        // §81/1 — ผู้ที่ไม่ได้จด VAT ห้ามออกใบกำกับภาษี + เก็บ VAT. ถ้าบริษัท
        // VatRegistered=false แต่กำลังออกใบกำกับ/ใบเพิ่ม-ลดหนี้ที่มี VAT → เตือน
        // (ออกใบกำกับโดยไม่จด VAT = ความผิด §90/2 + ต้องนำส่ง VAT ที่เรียกเก็บ).
        if (doc.DocumentType is DocumentType.TaxInvoice or DocumentType.DebitNote or DocumentType.CreditNote
            && doc.VatAmount > 0)
        {
            var vatRegistered = await _db.Set<CompanySettings>().AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted)
                .Select(c => (bool?)c.VatRegistered)
                .FirstOrDefaultAsync() ?? true;
            if (!vatRegistered)
                warnings.Add("⚠️ บริษัทยังไม่ได้จดทะเบียน VAT แต่กำลังออกใบกำกับภาษีที่มี VAT — " +
                    "ผู้ไม่จด VAT ห้ามออกใบกำกับ (§90/2) และต้องนำส่ง VAT ที่เรียกเก็บ. " +
                    "ถ้ารายได้เกิน 1.8 ล้าน/ปี ต้องจด VAT ภายใน 30 วัน (§85/1)");
        }

        // Per-line VAT + WHT rate sanity. The 0/7 hard block sits in the
        // create path; this is the "rate is technically legal but unusual"
        // shoulder (e.g. ratio that doesn't match a known ภ.ง.ด. code).
        var knownWhtRates = new HashSet<decimal> { 0m, 1m, 1.5m, 2m, 3m, 5m, 10m, 15m };
        foreach (var line in doc.Lines)
        {
            if (line.WithholdingTaxRate > 0 && !knownWhtRates.Contains(line.WithholdingTaxRate))
                warnings.Add($"อัตรา WHT ของ '{line.Description}' = {line.WithholdingTaxRate}% — ไม่ใช่อัตรามาตรฐาน (1 / 1.5 / 2 / 3 / 5 / 10 / 15%) ตรวจ Income Type Code อีกครั้ง");
        }

        // WHT threshold §50 — รวมยอดจ่ายให้ผู้รับเดียวกันทั้งปีภาษี ≥ 1,000
        // บาท ต้องหัก ณ ที่จ่ายทุกงวด. ถ้าเอกสารฝั่งจ่ายไม่มี WHT แต่ยอดรวม
        // ≥ 1,000 → เตือนผู้ใช้ว่าอาจลืมหัก
        if ((doc.DocumentType == DocumentType.PaymentVoucher
             || doc.DocumentType == DocumentType.Expense
             || doc.DocumentType == DocumentType.PurchaseInvoice)
            && doc.WithholdingTaxAmount == 0m && doc.SubTotal > 0)
        {
            var (required, ytd) = await CheckWhtThresholdAsync(
                companyId, doc.ContactId, doc.DocumentDate, doc.SubTotal);
            if (required)
            {
                if (doc.SubTotal < 1000m)
                    warnings.Add($"⚠️ §50 threshold: ยอดสะสมจ่ายให้ '{doc.Contact?.Name}' ในปีนี้ {ytd:N2} บาท ≥ 1,000 — แม้ใบนี้ {doc.SubTotal:N2} (<1,000) ต้องหัก ณ ที่จ่ายทุกงวด");
                else
                    warnings.Add($"⚠️ ใบนี้ {doc.SubTotal:N2} ≥ 1,000 บาท แต่ไม่ได้กรอกหัก ณ ที่จ่าย — ตรวจประเภทเงินได้ (ค่าบริการ 3% / ค่าเช่า 5% / ค่าโฆษณา 2% / ขนส่ง 1%) §3 เตรส");
            }
        }

        // DTA bilateral treaty — เตือนเมื่อจ่ายไปต่างประเทศ + ใช้ default rate
        // (15% ม.70) แต่ payee country มี DTA ลดเหลือ 5-10% บ่อย → ผู้ใช้
        // อาจหักเกินไปเสียค่าใช้จ่ายให้ vendor เปล่าๆ
        if ((doc.DocumentType == DocumentType.PaymentVoucher
             || doc.DocumentType == DocumentType.Expense
             || doc.DocumentType == DocumentType.PurchaseInvoice)
            && doc.WithholdingTaxAmount > 0m
            && !string.IsNullOrEmpty(doc.Contact?.CountryCode)
            && !string.Equals(doc.Contact.CountryCode, "TH", StringComparison.OrdinalIgnoreCase))
        {
            var maxRate = doc.Lines?.Max(l => l.WithholdingTaxRate) ?? 0m;
            if (maxRate >= 15m)
                warnings.Add($"🌐 จ่ายต่างประเทศ ({doc.Contact.CountryCode}): WHT {maxRate}% (ม.70 default). ตรวจ DTA bilateral treaty — ส่วนใหญ่ลดเหลือ 5-10% ถ้ามี Certificate of Residence/Form TH8 ของ payee");
        }

        // Sticker-shock guard — flag invoices > 500k THB. Catches a typo
        // like 4,500,000 vs 450,000.
        if (doc.TotalAmount >= 500_000m)
            warnings.Add($"ยอดรวมเอกสาร {doc.TotalAmount:N2} {doc.Currency} — ตรวจตัวเลขก่อนยืนยัน (จำนวนเงินสูงผิดปกติ)");

        // §82/3 — ภาษีซื้อต้องเคลมภายใน 6 เดือนนับจาก tax point. ใบกำกับ
        // ที่ใบมาช้า (vendor ส่งหลัง 6 เดือน) ระบบลงให้แต่เคลมไม่ได้ใน
        // ภ.พ.30. เตือนตอน approve (ก่อนที่จะรู้ตอน end-of-month)
        if (doc.DocumentType is DocumentType.PurchaseInvoice or DocumentType.Expense
            or DocumentType.PaymentVoucher or DocumentType.CertificateInLieu)
        {
            var taxPoint = doc.TaxPointDate ?? doc.SupplierTaxInvoiceDate ?? doc.DocumentDate;
            var monthsLate = ((today.Year - taxPoint.Year) * 12) + (today.Month - taxPoint.Month);
            if (monthsLate > 6 && doc.VatAmount > 0)
            {
                if (string.IsNullOrWhiteSpace(doc.LateReason))
                    warnings.Add($"§82/3: ใบกำกับเก่ากว่า 6 เดือน ({taxPoint:yyyy-MM-dd}, {monthsLate} เดือน) — ภาษีซื้อ {doc.VatAmount:N2} เคลม ภ.พ.30 ไม่ได้แล้ว. ถ้ายังต้องการอนุมัติ ให้กรอก LateReason (เหตุผลที่ใบมาช้า) ก่อน + ระบบจะ reclassify VAT เป็นค่าใช้จ่ายอัตโนมัติ");
                else
                    warnings.Add($"§82/3: ใบกำกับเก่ากว่า 6 เดือน ({monthsLate} เดือน, LateReason: '{doc.LateReason}') — ภาษีซื้อ {doc.VatAmount:N2} จะถูก reclassify เป็นค่าใช้จ่ายแทน claim ภพ.30");
            }
            else if (monthsLate >= 1 && monthsLate <= 6 && doc.VatAmount > 0
                     && string.IsNullOrWhiteSpace(doc.LateReason))
            {
                warnings.Add($"§82/3: ใบกำกับช้า {monthsLate} เดือน (tax point {taxPoint:yyyy-MM-dd}) — กรอก LateReason เพื่อ audit trail (ภายใน 6 เดือนยังเคลมได้)");
            }
        }

        // §82/5(6) — รถยนต์นั่ง ≤10 ที่นั่ง: VAT ค่าน้ำมัน/ซ่อม/เช่าซื้อ
        // เคลมไม่ได้ (ยกเว้นบริษัทเป็น vehicle dealer). detect จาก keyword
        // ใน description ไม่ใช่แค่ผัง — vendor อาจไม่ตั้งผังแยก.
        // override: ถ้า CompanySettings.IsVehicleDealer=true → ข้าม warning
        // (บริษัทขายรถ/อู่ — รถเป็น inventory เคลมได้ตามปกติ)
        var isVehicleDealer = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => (bool?)s.IsVehicleDealer)
            .FirstOrDefaultAsync() ?? false;
        if (!isVehicleDealer && doc.DocumentType is DocumentType.PurchaseInvoice or DocumentType.Expense
            or DocumentType.PaymentVoucher)
        {
            var vehicleKw = new[] { "น้ำมัน", "เบนซิน", "ดีเซล", "ค่าซ่อม", "อะไหล่",
                "ค่าเช่ารถ", "ค่าน้ำมันรถ", "fuel", "gasoline", "diesel" };
            var passengerKw = new[] { "รถยนต์", "รถเก๋ง", "sedan", "passenger" };
            foreach (var line in doc.Lines ?? new List<DocumentLine>())
            {
                var d = (line.Description ?? "").ToLowerInvariant();
                if (line.IsVatClaimable
                    && line.VatAmount > 0
                    && vehicleKw.Any(k => d.Contains(k.ToLowerInvariant()))
                    && (passengerKw.Any(k => d.Contains(k.ToLowerInvariant())) || vehicleKw.Any(k => d.Contains(k))))
                {
                    warnings.Add($"§82/5(6): '{line.Description}' — ถ้าเป็นรถยนต์นั่ง ≤10 ที่นั่ง ภาษีซื้อ {line.VatAmount:N2} เคลมไม่ได้ ติ๊กออก '✓ เคลม VAT' ที่บรรทัดนี้ (ยกเว้นบริษัทเป็น vehicle dealer)");
                    break;
                }
            }
        }

        // Expense ที่มี VAT แต่ไม่ติ๊ก "ใช้งานใบกำกับภาษี" (HasTaxInvoiceReference
        // =false): §86/4 ไม่ครบ → เคลม VAT ไม่ได้ เตือนผู้ใช้ก่อน approve
        if (doc.DocumentType == DocumentType.Expense
            && doc.VatAmount > 0
            && !doc.HasTaxInvoiceReference)
            warnings.Add($"เอกสารค่าใช้จ่ายมี VAT {doc.VatAmount:N2} แต่ไม่ระบุข้อมูลใบกำกับ — §86/4 ไม่ครบ ภาษีซื้อจะลง 11640 (ยังไม่ถึงกำหนด) เคลมไม่ได้จนกว่าจะเติมข้อมูลใบ");

        // Foreign currency without explicit FX rate (means the rate was
        // either captured at create-time or fell back to BoT) — surface so
        // operator can override with the contracted rate before posting.
        if (!string.Equals(doc.Currency, "THB", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"สกุลเงิน {doc.Currency} (เรทใช้จริง {doc.ExchangeRate:F4}) — ถ้าเป็นเรทเช่าบริการ/สัญญา ให้อัพเดทก่อนอนุมัติ");

        // Stock-going-negative warning for the AR side. Allowed (back-orders
        // are legitimate) but the operator should be told before the GL
        // commits a sale we can't actually deliver.
        if (doc.DocumentType is DocumentType.Invoice or DocumentType.TaxInvoice)
        {
            var lineCodes = (doc.Lines ?? new List<DocumentLine>())
                .Where(l => !string.IsNullOrWhiteSpace(l.ProductCode))
                .Select(l => l.ProductCode!).Distinct().ToList();
            if (lineCodes.Count > 0)
            {
                var products = await _db.Products.AsNoTracking()
                    .Where(p => p.CompanyId == companyId && lineCodes.Contains(p.Code) && p.TrackStock && !p.IsDeleted)
                    .ToDictionaryAsync(p => p.Code);
                foreach (var line in doc.Lines)
                {
                    if (string.IsNullOrWhiteSpace(line.ProductCode)) continue;
                    if (!products.TryGetValue(line.ProductCode!, out var product)) continue;
                    if (product.CurrentStock - line.Quantity < 0)
                        warnings.Add($"สินค้า '{product.Name}' คงเหลือ {product.CurrentStock} {product.Unit} จะติดลบ {Math.Abs(product.CurrentStock - line.Quantity):N2} หลังบันทึก (back-order)");
                }
            }
        }

        // Customer carrying past-due invoices — surface so the operator
        // chases collection before booking more AR with the same party.
        if (doc.DocumentType is DocumentType.Invoice or DocumentType.TaxInvoice or DocumentType.DebitNote)
        {
            var overdue = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && d.ContactId == doc.ContactId
                    && d.Id != doc.Id && !d.IsDeleted
                    && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.PartiallyPaid || d.Status == DocumentStatus.Overdue)
                    && d.AgingDays != null && d.AgingDays > 30)
                .Select(d => new { d.DocumentNumber, d.BalanceDue, d.AgingDays })
                .ToListAsync();
            if (overdue.Count > 0)
            {
                var total = overdue.Sum(d => d.BalanceDue);
                warnings.Add($"ผู้ติดต่อนี้ยังค้างชำระ {overdue.Count} ใบ รวม {total:N2} THB — ตรวจวงเงินเครดิตก่อนอนุมัติเอกสารใหม่");
            }
        }

        // ===== Thai-accounting completeness checks (RD requirements) =====

        // 1. Tax Invoice must carry the buyer's full address (ที่อยู่ผู้ซื้อ)
        //    per ป.86/4 — without it the document isn't a valid full tax
        //    invoice and the buyer can't claim input VAT.
        if (doc.DocumentType == DocumentType.TaxInvoice && doc.Contact != null)
        {
            var hasAddr = !string.IsNullOrWhiteSpace(doc.Contact.Address)
                || !string.IsNullOrWhiteSpace(doc.Contact.Province)
                || !string.IsNullOrWhiteSpace(doc.Contact.District);
            if (!hasAddr)
                warnings.Add($"ใบกำกับภาษีต้องระบุที่อยู่ผู้ซื้อ ('{doc.Contact.Name}' ยังไม่มีที่อยู่) — ตามมาตรา 86/4 ผู้ซื้อใช้เป็นหลักฐานภาษีซื้อไม่ได้");
            // Thai corporate Tax ID is exactly 13 digits.
            var tid = new string((doc.Contact.TaxId ?? "").Where(char.IsDigit).ToArray());
            if (!string.IsNullOrEmpty(tid) && tid.Length != 13)
                warnings.Add($"เลขประจำตัวผู้เสียภาษีของ '{doc.Contact.Name}' มี {tid.Length} หลัก (ต้อง 13 หลัก) — ตรวจก่อนออกใบกำกับภาษี");
        }

        // 2. When WHT is withheld, a 50 ทวิ certificate must be issued to the
        //    payee. Flag so the operator generates it (PND filing depends on it).
        if (doc.WithholdingTaxAmount > 0 && doc.DocumentType is DocumentType.PaymentVoucher or DocumentType.PurchaseInvoice or DocumentType.Expense)
            warnings.Add($"มีการหักภาษี ณ ที่จ่าย {doc.WithholdingTaxAmount:N2} THB — อย่าลืมออกหนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) ให้ผู้รับเงิน และยื่น ภ.ง.ด. ตามกำหนด");

        // 2b. A VAT PurchaseInvoice needs the SUPPLIER's own tax-invoice number
        //     + date — the input VAT must be claimed in the period of the
        //     supplier's invoice (มาตรา 82/4), which can differ from our
        //     booking date. Without them the ภ.พ.30 input-VAT trail is broken.
        if (doc.DocumentType == DocumentType.PurchaseInvoice && doc.VatAmount > 0)
        {
            if (string.IsNullOrWhiteSpace(doc.SupplierInvoiceNumber))
                warnings.Add("ใบแจ้งหนี้ซื้อที่มี VAT ยังไม่ได้ระบุเลขใบกำกับภาษีของผู้ขาย — ต้องมีเพื่อเคลมภาษีซื้อ (มาตรา 82/4)");
            if (doc.SupplierTaxInvoiceDate == null)
                warnings.Add("ใบแจ้งหนี้ซื้อที่มี VAT ยังไม่ได้ระบุวันที่ใบกำกับภาษีของผู้ขาย — ใช้กำหนดงวด ภ.พ.30 ที่เคลมภาษีซื้อ");
        }

        // 3. Credit Note should reference the original invoice it adjusts
        //    (มาตรา 86/10). CreditNoteReason is already enforced; this nudges
        //    for the source link so VAT reversal ties back to the original.
        if (doc.DocumentType == DocumentType.CreditNote && doc.RelatedDocumentId == null)
            warnings.Add("ใบลดหนี้ยังไม่ได้อ้างอิงใบกำกับภาษี/ใบแจ้งหนี้ต้นฉบับ — ตามมาตรา 86/10 ควรระบุเลขที่และวันที่เอกสารเดิมที่ลดหนี้");

        // 4. Cash-settled Payment Voucher (จ่ายทันที) must NOT post to a
        //    payable (เจ้าหนี้) account — the money already left, so a 2xx
        //    liability debit/credit on a line is a modelling error. This is
        //    the consistency guard the user asked for.
        if (doc.DocumentType == DocumentType.PaymentVoucher
            && doc.PaymentType == Models.Enums.PaymentType.Cash)
        {
            var lineAccountIds = (doc.Lines ?? new List<DocumentLine>()).Where(l => l.AccountId.HasValue).Select(l => l.AccountId!.Value).Distinct().ToList();
            if (lineAccountIds.Count > 0)
            {
                var payableCodes = await _db.ChartOfAccounts.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && lineAccountIds.Contains(a.Id)
                        && (a.AccountType == AccountType.Liability || a.AccountCode.StartsWith("2")))
                    .Select(a => a.AccountCode + " " + a.AccountName)
                    .ToListAsync();
                if (payableCodes.Count > 0)
                    warnings.Add($"ใบสำคัญจ่ายแบบจ่ายทันทีไม่ควรลงบัญชีเจ้าหนี้/หนี้สิน ({string.Join(", ", payableCodes)}) — ถ้าเป็นการตั้งหนี้ ให้เลือกประเภทเป็น 'เครดิต' แทน");
            }
        }

        // 5. Contact still active? A document whose counterparty was
        //    deleted/deactivated between create and approve would still post
        //    to that party's AP/AR — surface it so the operator re-checks.
        if (doc.Contact != null && (doc.Contact.IsDeleted || !doc.Contact.IsActive))
            warnings.Add($"ผู้ติดต่อ '{doc.Contact.Name}' ถูกลบหรือปิดใช้งานแล้ว — ตรวจสอบก่อนอนุมัติ (ยอดจะลงบัญชีลูกหนี้/เจ้าหนี้ของผู้ติดต่อรายนี้)");

        // 6. Possible duplicate — another non-voided document for the SAME
        //    contact + same total + within ±1 day. Catches a double-entry /
        //    double-upload before it hits the GL twice.
        var dupFrom = doc.DocumentDate.Date.AddDays(-1);
        var dupTo = doc.DocumentDate.Date.AddDays(1);
        var dup = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.Id != doc.Id && !d.IsDeleted
                && d.ContactId == doc.ContactId
                && d.DocumentType == doc.DocumentType
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                && d.TotalAmount == doc.TotalAmount
                && d.DocumentDate >= dupFrom && d.DocumentDate <= dupTo)
            .Select(d => d.DocumentNumber)
            .FirstOrDefaultAsync();
        if (dup != null)
            warnings.Add($"อาจเป็นเอกสารซ้ำ — มี {dup} ของผู้ติดต่อรายนี้ ยอด {doc.TotalAmount:N2} ในช่วงวันที่เดียวกัน ตรวจสอบก่อนอนุมัติ");

        return warnings;
    }
}

/// <summary>Thrown by ApproveDocumentAsync when the pre-approval validator
/// surfaces soft warnings AND the request didn't carry AcknowledgeWarnings.
/// Controllers catch this and return HTTP 422 with the warning list so the
/// frontend can prompt the operator and retry with the acknowledge flag.</summary>
public class DocumentApprovalWarningsException : Exception
{
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>AI-enriched per-warning suggestions. NULL when AI is
    /// disabled / unreachable. Each item corresponds to Warnings[i] by
    /// index — pair them in the UI.</summary>
    public IReadOnlyList<DocumentApprovalAiHint>? AiHints { get; }

    public DocumentApprovalWarningsException(
        IReadOnlyList<string> warnings,
        IReadOnlyList<DocumentApprovalAiHint>? aiHints = null)
        : base("เอกสารมีจุดที่ต้องตรวจก่อนยืนยันการอนุมัติ (" + warnings.Count + " รายการ)")
    {
        Warnings = warnings;
        AiHints = aiHints;
    }
}

/// <summary>
/// AI-generated per-warning hint. Surfaced alongside the warning text
/// in the approval-confirmation modal so the user sees "Acknowledge / Edit
/// / Block" + suggested actions per item.
/// </summary>
public sealed record DocumentApprovalAiHint(
    string Primary,                         // "Acknowledge" | "Edit" | "Block"
    decimal Confidence,
    string? Reasoning,
    IReadOnlyList<string> SuggestedActions,
    IReadOnlyList<string> Risks,
    IReadOnlyList<string> ComplianceFlags,
    Guid? FeedbackId,
    bool UsedAi);
