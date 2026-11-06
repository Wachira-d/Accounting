using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Recurring;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 248 — รายการประจำแบบ Autopilot (AUTOPILOT_BILLING_DESIGN §4.3–§4.5 · §5.1) · <b>ตัวต่อสาย + ตัวรวบรวมข้อเท็จจริง</b> เท่านั้น (หลักการ #1):
/// ความพร้อม = <see cref="AutopilotReadiness.Evaluate"/> · คำเตือนก่อนอนุมัติ = ตัวรวบรวมของ <c>DocumentService</c> บนใบจำลอง ·
/// สิ่งที่หยุด = <see cref="ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource, IReadOnlyList{string}, AutoApprovePolicy?)"/> ·
/// ยอดในลิงก์ = <see cref="DocumentPayLinkPolicy.AmountsFor"/> · ช่องทาง = <see cref="AutopilotSettingsResolver"/>
///
/// <para><b>ความพร้อม/ทดลองรันไม่เขียนอะไรเลย</b> — ไม่มี SaveChanges · ไม่ออกลิงก์ · ไม่ส่ง · ไม่จองโควตา (ล็อกด้วย Db test
/// <c>RecurringAutopilotRound248DbTests</c> + <c>tools/required_call_site_check.py</c>) · ทุก query กรองบริษัท</para>
/// </summary>
public class RecurringAutopilotService : IRecurringAutopilotService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly IDocumentPayLinkService? _payLinks;
    private readonly IPermissionService? _permissions;

    public RecurringAutopilotService(AccountingDbContext db, IDocumentService docs,
        IDocumentPayLinkService? payLinks = null, IPermissionService? permissions = null)
    {
        _db = db;
        _docs = docs;
        _payLinks = payLinks;
        _permissions = permissions;
    }

    // ═══════════════ ตัวเลือก ═══════════════

    public async Task<AutopilotOptionsResponse> OptionsAsync(Guid companyId, CancellationToken ct = default)
    {
        var facts = await CompanyPayFactsAsync(companyId, ct);
        var available = facts == null ? Array.Empty<string>() : DocumentPayLinkPolicy.CompanyAvailable(facts);
        return new AutopilotOptionsResponse(
            AutopilotSettingsResolver.Modes.Select(m => new AutopilotOption(m, AutopilotSettingsResolver.Label(m))).ToList(),
            available.Select(c => new AutopilotOption(c, DocumentPayLinkPolicy.Label(c))).ToList(),
            AutoApprovePolicy.Catalogue.Select(c => new AutopilotOption(c.Code, c.Title, c.Why)).ToList(),
            AutoApprovePolicy.NeverList.Select(c => new AutopilotOption(c.Code, c.Title, c.Why)).ToList(),
            facts == null ? "ยังไม่ได้ตั้งค่ารับชำระเงินออนไลน์" : DocumentPayLinkPolicy.UnavailableReason(facts));
    }

    // ═══════════════ ความพร้อม / ทดลองรัน ═══════════════

    public async Task<AutopilotReadinessResponse> ReadinessAsync(Guid companyId, Guid recurringId, CancellationToken ct = default)
        => (await BuildAsync(companyId, await LoadAsync(companyId, recurringId, ct), ct)).Readiness;

    public async Task<AutopilotReadinessResponse> ReadinessForDraftAsync(Guid companyId, CreateRecurringTransactionRequest draft, Guid? recurringId,
        CancellationToken ct = default)
        => (await BuildAsync(companyId, await FromDraftAsync(companyId, draft, recurringId, ct), ct)).Readiness;

    public async Task<RecurringDryRunResponse> DryRunAsync(Guid companyId, Guid recurringId, CancellationToken ct = default)
        => await BuildAsync(companyId, await LoadAsync(companyId, recurringId, ct), ct);

    public async Task<RecurringDryRunResponse> DryRunDraftAsync(Guid companyId, CreateRecurringTransactionRequest draft, Guid? recurringId,
        CancellationToken ct = default)
        => await BuildAsync(companyId, await FromDraftAsync(companyId, draft, recurringId, ct), ct);

    private async Task<RecurringTransaction> LoadAsync(Guid companyId, Guid recurringId, CancellationToken ct)
        => await _db.RecurringTransactions.AsNoTracking()
               .FirstOrDefaultAsync(r => r.Id == recurringId && r.CompanyId == companyId, ct)
           ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");

    /// <summary>ร่างในฟอร์ม ⇒ แถวในหน่วยความจำ (ไม่ถูกติดตาม) · แก้รายการเดิม ⇒ ใช้นโยบายที่ตั้งไว้ของรายการนั้น (นโยบายตั้งผ่าน endpoint แยก)</summary>
    private async Task<RecurringTransaction> FromDraftAsync(Guid companyId, CreateRecurringTransactionRequest d, Guid? recurringId, CancellationToken ct)
    {
        if (AutopilotSettingsResolver.ValidateMode(d.PaymentMode) is { } badMode)
            throw new BusinessRuleException(badMode, AutopilotSettingsResolver.RuleCode);
        string? storedPolicy = null;
        if (recurringId is { } rid)
            storedPolicy = await _db.RecurringTransactions.AsNoTracking()
                .Where(r => r.Id == rid && r.CompanyId == companyId)
                .Select(r => r.AutoApprovePolicyJson)
                .FirstOrDefaultAsync(ct);
        return new RecurringTransaction
        {
            Id = recurringId ?? Guid.Empty,
            CompanyId = companyId,
            Name = d.Name,
            Frequency = d.Frequency,
            StartDate = d.StartDate,
            EndDate = d.EndDate,
            NextRunDate = d.StartDate,
            PreferredDay = d.StartDate.Day,
            MaxRuns = d.MaxRuns,
            TemplateType = d.TemplateType,
            DocumentType = d.DocumentType,
            ContactId = d.ContactId,
            TemplateData = d.TemplateData,
            AutoApprove = d.AutoApprove || d.AutoSendEmail,
            AutoSendEmail = d.AutoSendEmail,
            PaymentMode = AutopilotSettingsResolver.Normalize(d.PaymentMode),
            PayLinkChannelsJson = DocumentPayLinkPolicy.SerializeChannels(d.PayLinkChannels),
            AutoApprovePolicyJson = storedPolicy,
        };
    }

    private async Task<DocumentPayLinkPolicy.CompanyFacts?> CompanyPayFactsAsync(Guid companyId, CancellationToken ct)
        => _payLinks == null ? null : await _payLinks.CompanyFactsAsync(companyId, ct);

    /// <summary>รวบรวมข้อเท็จจริงครั้งเดียว (อ่านอย่างเดียว) → ตัวตัดสิน → ผลทดลองรัน</summary>
    private async Task<RecurringDryRunResponse> BuildAsync(Guid companyId, RecurringTransaction r, CancellationToken ct)
    {
        var notes = new List<string>();
        var isDocument = !string.Equals(r.TemplateType, "journal", StringComparison.OrdinalIgnoreCase);
        var docType = r.DocumentType ?? DocumentType.Invoice;
        var nextDates = new List<DateTime>();
        var cursor = r.NextRunDate;
        for (var i = 0; i < 6 && RecurringSchedule.WithinEnd(cursor, r.EndDate) && (r.MaxRuns == null || r.TotalRuns + i < r.MaxRuns); i++)
        {
            nextDates.Add(cursor);
            cursor = RecurringSchedule.Next(r.Frequency, cursor, r.PreferredDay, r.StartDate);
        }

        // ── ผู้ติดต่อ ──
        Contact? contact = null;
        if (r.ContactId is { } cid)
            contact = await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid && c.CompanyId == companyId, ct);

        // ── บรรทัด (ตัวอ่านเดียวกับตอนสร้าง: RecurringTemplate · ตัวแทนค่าเดียวกัน: ApplyRecurringPlaceholders) ──
        string? templateError = null;
        var lines = new List<RecurringDryRunLine>();
        var pricesIncludeVat = false;
        string currency = "THB";
        if (isDocument)
        {
            try
            {
                using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.TemplateData) ? "{}" : r.TemplateData);
                var root = json.RootElement;
                pricesIncludeVat = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("pricesIncludeVat", out var piv)
                    && piv.ValueKind == JsonValueKind.True;
                if (RecurringTemplate.Text(root, "currency") is { Length: > 0 } cur) currency = cur.Trim().ToUpperInvariant();
                var baseDate = RecurringSchedule.PlaceholderBaseDate(r.NextRunDate, DateTime.UtcNow, true);
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
                {
                    var lineNo = 0;
                    foreach (var line in linesEl.EnumerateArray())
                    {
                        lineNo++;
                        var qty = RecurringTemplate.Number(line, "quantity", 1m, lineNo);
                        var price = RecurringTemplate.Number(line, "unitPrice", 0m, lineNo);
                        var vatRate = RecurringTemplate.Number(line, "vatRate", 7m, lineNo);
                        var whtRate = RecurringTemplate.Number(line, "withholdingTaxRate", 0m, lineNo);
                        var discPct = RecurringTemplate.Number(line, "discountPercent", 0m, lineNo);
                        var discAmt = RecurringTemplate.Number(line, "discountAmount", 0m, lineNo);
                        var gross = Math.Round(qty * price * (1m - discPct / 100m) - discAmt, 2, MidpointRounding.AwayFromZero);
                        var amount = pricesIncludeVat && vatRate > 0m
                            ? Math.Round(gross * 100m / (100m + vatRate), 2, MidpointRounding.AwayFromZero)
                            : gross;
                        var vat = pricesIncludeVat && vatRate > 0m ? gross - amount
                            : Math.Round(amount * vatRate / 100m, 2, MidpointRounding.AwayFromZero);
                        var wht = Math.Round(amount * whtRate / 100m, 2, MidpointRounding.AwayFromZero);
                        var desc = RecurringTransactionService.ApplyRecurringPlaceholders(RecurringTemplate.Text(line, "description"), baseDate) ?? "";
                        lines.Add(new RecurringDryRunLine(desc, qty, price, vatRate, amount, vat, whtRate, wht));
                    }
                }
                if (lines.Count == 0)
                    templateError = "เทมเพลตไม่มีรายการสินค้า/บริการ — เพิ่มอย่างน้อย 1 บรรทัดในขั้น \"รายการ & ราคา\"";
            }
            catch (JsonException) { templateError = "อ่านเทมเพลตไม่ได้ — เปิดแก้ไขรายการประจำแล้วบันทึกใหม่"; }
            catch (BusinessRuleException ex) { templateError = ex.Message; }
        }
        var subTotal = lines.Sum(l => l.Amount);
        var vatTotal = lines.Sum(l => l.VatAmount);
        var total = subTotal + vatTotal;
        var whtTotal = lines.Sum(l => l.WithholdingTaxAmount);
        var buyerWithholds = DocumentPayLinkPolicy.BuyerWithholds(contact?.ContactType, contact?.TaxId) != false;
        var expectedWht = buyerWithholds ? whtTotal : 0m;
        var amounts = DocumentPayLinkPolicy.AmountsFor(total - expectedWht, expectedWht, 0m);

        // ── คำเตือนก่อนอนุมัติบนใบจำลอง (ตัวรวบรวมเดียวกับการอนุมัติจริง · ใบไม่อยู่ใน context) ──
        IReadOnlyList<string>? warnings = null;
        if (isDocument && contact != null && lines.Count > 0 && templateError == null)
        {
            var draft = new Document
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                DocumentType = docType,
                DocumentNumber = "DRAFT-DRYRUN",
                DocumentDate = DateTime.UtcNow,   // ตอนรันจริงวันเอกสาร = วันที่รัน (CreateDocumentAsync ใช้ UtcNow) ⇒ ใบจำลองใช้วันนี้ (ไม่ฟ้อง "ล่วงหน้า" ผิด)
                ContactId = contact.Id,
                Contact = contact,
                Status = DocumentStatus.Draft,
                Currency = currency,
                PricesIncludeVat = pricesIncludeVat,
                SubTotal = subTotal,
                VatAmount = vatTotal,
                TotalAmount = total,
                WithholdingTaxAmount = whtTotal,
                BalanceDue = total - whtTotal,
                Lines = lines.Select((l, i) => new DocumentLine
                {
                    LineOrder = i + 1,
                    Description = l.Description,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    Amount = l.Amount,
                    VatRate = l.VatRate,
                    VatAmount = l.VatAmount,
                    WithholdingTaxRate = l.WithholdingTaxRate,
                    WithholdingTaxAmount = l.WithholdingTaxAmount,
                }).ToList(),
            };
            try { warnings = await _docs.PreviewApprovalWarningsForUnsavedAsync(companyId, draft); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add("ตรวจคำเตือนก่อนอนุมัติของใบจำลองไม่สำเร็จ: " + ex.Message);
            }
        }

        // ── นโยบาย (ตัวโหลดเดียวกับเส้นอนุมัติจริง) ──
        var policy = await RecurringApprovePolicyLoader.LoadAsync(_db, _permissions, companyId, r, ct);
        var blocking = warnings == null ? Array.Empty<string>()
            : ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.UnattendedWithPolicy, warnings, policy.Policy);
        var released = warnings == null ? Array.Empty<string>() : AutoApprovePolicy.Released(policy.Policy, warnings);

        // ── ช่องทางรับเงิน (ตัวต่อชั้นเดียว) ──
        var payFacts = await CompanyPayFactsAsync(companyId, ct);
        var available = payFacts == null ? Array.Empty<string>() : DocumentPayLinkPolicy.CompanyAvailable(payFacts);
        var settings = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId).FirstOrDefaultAsync(ct);
        var recurringLayer = AutopilotSettingsResolver.FromMode(r.PaymentMode, DocumentPayLinkPolicy.ParseChannels(r.PayLinkChannelsJson), available);
        var contactLayer = contact == null ? null
            : AutopilotSettingsResolver.FromMode(contact.PaymentMode, DocumentPayLinkPolicy.ParseChannels(contact.PayLinkChannelsJson), available);
        var choice = AutopilotSettingsResolver.Resolve(null, recurringLayer, contactLayer, settings?.PayLinkDefaultEnabled ?? false,
            DocumentPayLinkPolicy.ParseChannels(settings?.PayLinkDefaultChannelsJson));
        var allowed = DocumentPayLinkPolicy.Allowed(available, choice.Channels, null);
        var saleSide = DocumentPayLinkPolicy.IsSaleSide(docType, null);
        var effectiveMode = AutopilotSettingsResolver.Normalize(r.PaymentMode) ?? AutopilotSettingsResolver.Normalize(contact?.PaymentMode);
        var hasRecurringRule = await _db.EmailScheduleRules.AsNoTracking()
            .AnyAsync(x => x.CompanyId == companyId && x.IsActive && !x.IsDeleted && x.Trigger == "RecurringInvoiceCreated"
                && (x.DocumentType == null || x.DocumentType == docType.ToString()), ct);
        if (hasRecurringRule)
            notes.Add("มีกฎ \"เมื่อรายการประจำสร้างเอกสาร\" ในหน้าตารางส่งอีเมล — ระบบส่งตามกฎนั้น (ค่าของกฎชนะค่าของรายการนี้เมื่อกฎตั้งไว้)");

        // ── ผู้ซื้อ §86/4 (ตัวตัดสินเดียวกับงานหลังรับชำระ) ──
        var buyerBlock = saleSide && contact != null
            ? Tax.TaxInvoiceCompletenessChecker.BuyerBlockingFields(true, vatTotal, contact, isDeferredVatDeposit: false,
                buyerDeclinedTaxInvoice: TemplateBuyerDeclined(r.TemplateData))
            : null;

        var senderVerified = settings != null && settings.EmailConfigured
            && Accounting.Services.Implementations.Email.EmailSenderFactory.HasTenantSender(settings);

        var report = AutopilotReadiness.Evaluate(new AutopilotReadiness.Facts
        {
            IsDocumentTemplate = isDocument,
            DocumentType = docType,
            AutoApprove = r.AutoApprove || r.AutoSendEmail,
            AutoSendEmail = r.AutoSendEmail,
            ContactId = r.ContactId,
            ContactUsable = contact != null,
            ContactEmail = contact?.Email,
            BuyerBlockingFields = buyerBlock,
            CompanySenderVerified = senderVerified,
            SaleSide = saleSide,
            Currency = currency,
            PayLinkAttach = choice.Attach,
            PayLinkSource = choice.Source,
            PaymentMode = effectiveMode,
            PayLinkUnavailableReason = payFacts == null ? "ยังไม่ได้ตั้งค่ารับชำระเงินออนไลน์" : DocumentPayLinkPolicy.UnavailableReason(payFacts),
            PayLinkAllowed = allowed,
            ApprovalWarnings = warnings,
            ApprovalBlocking = blocking,
            ApprovalReleased = released,
            Policy = policy.Stored,
            PolicySetterActive = policy.SetterActive,
            PolicyInactiveReason = policy.InactiveReason,
            TemplateError = templateError,
        });
        var readiness = new AutopilotReadinessResponse(report.Level, report.LevelLabel, report.Items, report.WarnCount, report.FailCount);

        var approvalOutcome = !(r.AutoApprove || r.AutoSendEmail) ? "ไม่ได้เปิดอนุมัติอัตโนมัติ — ได้ใบร่าง"
            : warnings == null ? "ตรวจไม่ได้ (ต้องมีผู้ติดต่อและรายการ)"
            : blocking.Count > 0 ? $"จะหยุดเป็นใบร่าง — คำเตือน {blocking.Count} ข้อที่ต้องมีคนรับทราบ"
            : released.Count > 0 ? $"จะอนุมัติ — นโยบายปล่อย {released.Count} ข้อ (ลงหมายเหตุ + audit {AutoApprovePolicy.RuleCode})"
            : "จะอนุมัติ (ได้เลขที่จริงตอนอนุมัติ)";
        var emailPlan = !r.AutoSendEmail ? "ไม่ส่งอีเมลอัตโนมัติ"
            : !RecurringEmailReadiness.HasDeliverableEmail(contact?.Email) ? RecurringEmailReadiness.NotSentNoEmail
            : blocking.Count > 0 ? "ไม่ส่ง — ใบยังเป็นร่าง (เลขที่จริงออกตอนอนุมัติ §86/4)"
            : (senderVerified ? "ส่งจากอีเมลบริษัท" : "ส่งผ่านอีเมลของ NextAcc ในนามบริษัท") + " ถึง " + contact!.Email!.Trim();
        var payPlan = !saleSide ? "ไม่มีลิงก์ชำระ (ไม่ใช่เอกสารเรียกเก็บเงิน)"
            : !choice.Attach ? $"ไม่แนบลิงก์ชำระ (ตาม{choice.Source})"
            : DocumentPayLinkPolicy.OnlineOnly(allowed).Count == 0 ? "ตั้งให้แนบ แต่ไม่มีช่องทางออนไลน์ที่บริษัทเปิด — จะไม่แนบ"
            : $"แนบลิงก์ชำระ ({string.Join(" · ", allowed.Select(DocumentPayLinkPolicy.Label))}) ยอดในลิงก์ {amounts.Net:N2} บาท"
              + (amounts.Wht > 0m ? $" (หลังหัก ณ ที่จ่าย {amounts.Wht:N2})" : "");
        notes.Add("ยอดในการทดลองรันคำนวณจากเทมเพลต (ประมาณการ) — ยอดจริงคำนวณตอนสร้างเอกสารด้วยตัวคำนวณของเอกสาร");

        return new RecurringDryRunResponse(
            readiness, nextDates,
            DocumentTypeNames.Title(docType, "th"),
            "เลขที่จริงออกตอนอนุมัติเท่านั้น (§86/4) — ก่อนอนุมัติเป็น DRAFT-…",
            lines, subTotal, vatTotal, total, expectedWht, amounts.Net,
            approvalOutcome, warnings ?? Array.Empty<string>(), released, blocking,
            contact?.Email, emailPlan, payPlan, notes);
    }

    private static bool TemplateBuyerDeclined(string? templateData)
    {
        if (string.IsNullOrWhiteSpace(templateData)) return false;
        try
        {
            using var json = JsonDocument.Parse(templateData);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("buyerDeclinedTaxInvoice", out var el) && el.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    // ═══════════════ นโยบายอนุมัติ ═══════════════

    public async Task<AutoApprovePolicyView?> SetRecurringPolicyAsync(Guid companyId, Guid recurringId, SetAutoApprovePolicyRequest request,
        Guid userId, CancellationToken ct = default)
    {
        var r = await _db.RecurringTransactions.FirstOrDefaultAsync(x => x.Id == recurringId && x.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");
        var before = r.AutoApprovePolicyJson;
        // ผูกลายนิ้วมือของค่าปัจจุบัน (ผู้ติดต่อ · ชนิด · ยอด/บรรทัด · ธงอัตโนมัติ) — เปลี่ยนภายหลังโดยผู้ไม่มีสิทธิ์อนุมัติ ⇒ หยุดมีผล (ฝ่ายค้าน HIGH/MED-2) ·
        // Inherit = ใบรับรอง "ใช้นโยบายของผู้ติดต่อ" ที่ผูกลายนิ้วมือเหมือนกัน (นโยบายผู้ติดต่อไม่มีผลกับรายการที่ไม่มีใบรับรอง)
        r.AutoApprovePolicyJson = request.Clear ? null
            : request.Inherit
                ? AutoApprovePolicy.Serialize(AutoApprovePolicy.Create(Array.Empty<string>(), userId.ToString(), DateTime.UtcNow,
                    RecurringApprovePolicyLoader.CurrentBindingHash(r), inheritContact: true))
                : PolicyJsonFrom(request, userId, RecurringApprovePolicyLoader.CurrentBindingHash(r));
        AuditPolicy(companyId, "RecurringTransaction", r.Id, before, r.AutoApprovePolicyJson, userId);
        await _db.SaveChangesAsync(ct);
        return RecurringApprovePolicyLoader.View(AutoApprovePolicy.Parse(r.AutoApprovePolicyJson, AutoApprovePolicy.ScopeRecurring));
    }

    public async Task<ContactAutopilotSettingsResponse> SetContactPolicyAsync(Guid companyId, Guid contactId, SetAutoApprovePolicyRequest request,
        Guid userId, CancellationToken ct = default)
    {
        var c = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId && x.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        var before = c.AutoApprovePolicyJson;
        c.AutoApprovePolicyJson = request.Clear || request.Inherit ? null : PolicyJsonFrom(request, userId, null);
        AuditPolicy(companyId, "Contact", c.Id, before, c.AutoApprovePolicyJson, userId);
        await _db.SaveChangesAsync(ct);
        return ContactView(c);
    }

    /// <summary>คำขอ ⇒ JSON (ตัวตรวจเดียว <see cref="AutoApprovePolicy.Validate"/> — รหัสห้ามปล่อย/ไม่รู้จัก ⇒ 400 พร้อมเหตุ)</summary>
    private static string PolicyJsonFrom(SetAutoApprovePolicyRequest request, Guid userId, string? bindingHash)
    {
        if (AutoApprovePolicy.Validate(request.PassCodes) is { } why)
            throw new BusinessRuleException(why, AutoApprovePolicy.RuleCode);
        return AutoApprovePolicy.Serialize(AutoApprovePolicy.Create(request.PassCodes, userId.ToString(), DateTime.UtcNow, bindingHash));
    }

    /// <summary>หลักการ #10: ทุกการตั้งนโยบายปล่อยผ่าน = ร่องรอยใน hash chain (ผู้ตั้ง · ก่อน/หลัง · รหัสกฎ)</summary>
    private void AuditPolicy(Guid companyId, string entityType, Guid entityId, string? before, string? after, Guid userId)
    {
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = userId,
            Action = AuditAction.Update,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            OldValues = JsonSerializer.Serialize(new { autoApprovePolicy = before }),
            NewValues = JsonSerializer.Serialize(new
            {
                action = "SetAutoApprovePolicy",
                autoApprovePolicy = after,
                ruleCode = AutoApprovePolicy.RuleCode,
                legalReference = ArOverdueApprovalWarning.LegalReference,
            }),
        });
    }

    // ═══════════════ ช่องทางรับเงินของผู้ติดต่อ ═══════════════

    public async Task<ContactAutopilotSettingsResponse> GetContactSettingsAsync(Guid companyId, Guid contactId, CancellationToken ct = default)
    {
        var c = await _db.Contacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contactId && x.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        return ContactView(c);
    }

    public async Task<ContactAutopilotSettingsResponse> UpdateContactPaymentAsync(Guid companyId, Guid contactId, UpdateContactAutopilotRequest request,
        Guid userId, CancellationToken ct = default)
    {
        var c = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId && x.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        if (AutopilotSettingsResolver.ValidateMode(request.PaymentMode) is { } badMode)
            throw new BusinessRuleException(badMode, AutopilotSettingsResolver.RuleCode);
        if (AutopilotSettingsResolver.ValidateChannels(request.PayLinkChannels) is { } badCh)
            throw new BusinessRuleException(badCh, AutopilotSettingsResolver.RuleCode);
        var before = new { c.PaymentMode, c.PayLinkChannelsJson };
        if (request.PaymentMode != null) c.PaymentMode = AutopilotSettingsResolver.Normalize(request.PaymentMode);
        if (request.PayLinkChannels != null) c.PayLinkChannelsJson = DocumentPayLinkPolicy.SerializeChannels(request.PayLinkChannels);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = userId,
            Action = AuditAction.Update,
            EntityType = "Contact",
            EntityId = c.Id.ToString(),
            OldValues = JsonSerializer.Serialize(before),
            NewValues = JsonSerializer.Serialize(new { action = "SetContactPaymentMode", c.PaymentMode, c.PayLinkChannelsJson,
                ruleCode = AutopilotSettingsResolver.RuleCode }),
        });
        await _db.SaveChangesAsync(ct);
        return ContactView(c);
    }

    private static ContactAutopilotSettingsResponse ContactView(Contact c)
        => new(c.Id, c.Name, c.PaymentMode, DocumentPayLinkPolicy.ParseChannels(c.PayLinkChannelsJson),
            RecurringApprovePolicyLoader.View(AutoApprovePolicy.Parse(c.AutoApprovePolicyJson, AutoApprovePolicy.ScopeContact)));
}
