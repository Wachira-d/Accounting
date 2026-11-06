using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 255 WP-12B ข้อ 6–7 — ต่อยอดคู่ค้า NextAcc เดิม (<c>TradingPartnership</c> · <c>CrossTenantDocumentLink</c>) ไม่สร้างกลไกใหม่:
/// เพดานจำนวนคู่ต่อแพ็กเกจ (Y5 · null = ไม่จำกัด) · สถานะการเชื่อมของผู้ติดต่อ (ไม่รั่วสมาชิกภาพ) · เพดานยอดใบซื้อร่างต่อคู่ ·
/// ใบขายที่ส่งถึงคู่ค้า ⇒ <b>ใบซื้อร่าง</b>ในบริษัทผู้รับ (N3 · ค่าตั้งต้นปิด · หลักฐานภาษีซื้อ <see cref="PartnerInputVatEvidence"/> · ไม่ลงภาษีซื้อเอง)
/// <para><b>tenant isolation</b>: ผู้รับได้ "สำเนา" (แถวใหม่ของบริษัทผู้รับทั้งหมด — เอกสาร · บรรทัด · ผู้ติดต่อ · ไฟล์ XML) ไม่ได้สิทธิ์อ่านเอกสาร/ผู้ติดต่อ/ผังบัญชีของผู้ส่ง ·
/// ไม่ข้าม: ผังบัญชี · ต้นทุน · หมายเหตุภายใน · ไฟล์แนบภายในของผู้ส่ง (AUTOPILOT §6.9) · ทุก query ระบุบริษัทเอง</para>
/// </summary>
public partial class CrossTenantWorkflowService
{
    // ═══════════ เพดานจำนวนคู่ (Y5) ═══════════

    /// <summary>เพดานคู่ค้าของแพ็กเกจที่มีผลของบริษัท (กลุ่มบัญชี ⇒ แม่แบบของกลุ่ม · ไม่งั้นแม่แบบตามระดับแพ็กเกจ — ลำดับเดียวกับเพดานข้อความ) · null = ไม่จำกัด</summary>
    public async Task<int?> TradingPartnerCapAsync(Guid companyId, CancellationToken ct = default)
    {
        var sub = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new { s.Plan, s.AccountSubscriptionId })
            .FirstOrDefaultAsync(ct);
        if (sub == null) return null;
        if (sub.AccountSubscriptionId is Guid aid)
        {
            var acct = await _db.AccountSubscriptions.AsNoTracking()
                .Where(a => a.Id == aid && !a.IsDeleted)
                .Select(a => new { a.PlanTemplate.MaxTradingPartners })
                .FirstOrDefaultAsync(ct);
            if (acct != null) return acct.MaxTradingPartners;
        }
        return await _db.PlanTemplates.AsNoTracking()
            .Where(p => p.Plan == sub.Plan && p.IsActive && !p.IsDeleted)
            .OrderBy(p => p.CreatedAt)
            .Select(p => p.MaxTradingPartners)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>ด่านเพดานก่อนยอมรับ — ตรวจ<b>ทั้งสองบริษัท</b> (การยอมรับเพิ่มคู่ให้ทั้งคู่) · โยน <see cref="BusinessRuleException"/> 409 พร้อมทางไปต่อ</summary>
    private async Task EnsureTradingPartnerCapAsync(TradingPartnership p)
    {
        foreach (var cid in new[] { p.CompanyAId, p.CompanyBId })
        {
            var cap = await TradingPartnerCapAsync(cid);
            if (cap == null) continue;
            var accepted = await _db.TradingPartnerships.AsNoTracking()
                .CountAsync(x => (x.CompanyAId == cid || x.CompanyBId == cid) && x.Status == TradingPartnershipStatus.Accepted && x.Id != p.Id);
            if (PartnerExchangeRules.CapProblem(accepted, cap, cid == p.InvitedByCompanyId ? "บริษัทผู้เชิญ" : "บริษัทของท่าน") is string problem)
                throw new BusinessRuleException(problem, PartnerExchangeRules.RuleCode, 409);
        }
    }

    // ═══════════ สถานะการเชื่อมของผู้ติดต่อ (ไม่รั่วสมาชิกภาพ) ═══════════

    public sealed record PartnerStatusView(string State, string StateLabel, bool Discoverable, Guid? PartnershipId);

    /// <summary>สถานะการเชื่อมกับผู้ติดต่อ (เลขภาษี + สาขา) — บอกเฉพาะ เชื่อมแล้ว · ระงับ · <b>เขา</b>ขอเชื่อมมา · ไม่งั้น "ยังไม่เชื่อม" (คำเชิญที่เราส่งเองไม่บอกผล — P-1) ·
    /// "ใช้ NextAcc" บอกได้เฉพาะปลายทางที่เปิดให้ค้นเจอเอง (<c>PartnerDiscoverable</c>) · tenant = <paramref name="companyId"/></summary>
    public async Task<PartnerStatusView> PartnerStatusForContactAsync(Guid companyId, Guid contactId, CancellationToken ct = default)
    {
        var contact = await _db.Contacts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบผู้ติดต่อ");
        var none = new PartnerStatusView(nameof(PartnerExchangeRules.LinkState.NotLinked),
            PartnerExchangeRules.StateLabel(PartnerExchangeRules.LinkState.NotLinked), false, null);
        var taxId = PartnerCompanyMatch.NormalizeTaxId(contact.TaxId);
        if (taxId == null) return none;
        var rawTax = contact.TaxId!.Trim();
        var candidates = await _db.Companies.AsNoTracking()
            .Where(c => !c.IsDeleted && c.Id != companyId && (c.TaxId == taxId || c.TaxId == rawTax))
            .Select(c => new { c.Id, c.BranchCode, c.PartnerDiscoverable })
            .ToListAsync(ct);
        var pick = PartnerCompanyMatch.Pick(candidates.Select(c => new PartnerCompanyCandidate(c.Id, c.BranchCode)).ToList(), contact.BranchCode);
        if (pick.Kind != PartnerMatchKind.Matched || pick.CompanyId is not Guid partnerId) return none;   // ไม่พบ/กำกวม = ตอบเหมือนกัน

        var (a, b) = OrderPair(companyId, partnerId);
        var row = await _db.TradingPartnerships.AsNoTracking()
            .Where(p => p.CompanyAId == a && p.CompanyBId == b)
            .Select(p => new { p.Id, p.Status, p.InvitedByCompanyId })
            .FirstOrDefaultAsync(ct);
        var state = PartnerExchangeRules.StateOf(row?.Status, row != null && row.InvitedByCompanyId == companyId);
        var discoverable = PartnerExchangeRules.ShowDiscoverable(state, candidates.First(c => c.Id == partnerId).PartnerDiscoverable);
        return new PartnerStatusView(state.ToString(), PartnerExchangeRules.StateLabel(state), discoverable,
            state == PartnerExchangeRules.LinkState.NotLinked ? null : row?.Id);
    }

    // ═══════════ เพดานยอดใบซื้อร่างต่อคู่ (ของฝั่งผู้รับ) ═══════════

    /// <summary>ตั้งเพดานยอดใบซื้อร่างที่บริษัท <paramref name="companyId"/> รับจากคู่นี้ (null = ไม่จำกัด · ติดลบ = ปฏิเสธ) · audit chain</summary>
    public async Task<decimal?> SetPurchaseDraftLimitAsync(Guid companyId, Guid partnershipId, decimal? limit, Guid userId)
    {
        if (limit is decimal l && l < 0m)
            throw new BusinessRuleException("เพดานยอดต้องไม่ติดลบ (เว้นว่าง = ไม่จำกัด)", PartnerExchangeRules.RuleCode, 400);
        var p = await _db.TradingPartnerships.FirstOrDefaultAsync(x => x.Id == partnershipId && (x.CompanyAId == companyId || x.CompanyBId == companyId))
            ?? throw new KeyNotFoundException("ไม่พบคู่ค้า");
        var before = PartnerExchangeRules.ReceiverLimit(companyId, p.CompanyAId, p.PurchaseDraftLimitA, p.PurchaseDraftLimitB);
        if (p.CompanyAId == companyId) p.PurchaseDraftLimitA = limit; else p.PurchaseDraftLimitB = limit;
        p.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "TradingPartnership", EntityId = p.Id.ToString(),
            OldValues = JsonSerializer.Serialize(new { purchaseDraftLimit = before }),
            NewValues = JsonSerializer.Serialize(new { purchaseDraftLimit = limit, ruleCode = PartnerExchangeRules.RuleCode }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return limit;
    }

    // ═══════════ ใบขายของคู่ค้า ⇒ ใบซื้อร่างของผู้รับ (N3) ═══════════

    /// <summary>
    /// เรียกต่อจาก <see cref="OnDocumentSentAsync"/> (แถวกล่องเข้า InvoiceFlow ถูกสร้างแล้ว) — ผู้รับเปิด <c>CreatePurchaseDraftsFromPartners</c> + ยอดไม่เกินเพดานต่อคู่ ⇒
    /// สร้าง <c>Document(PurchaseInvoice, Draft)</c> ในบริษัทผู้รับ ผูก <c>link.TargetDocumentId</c> · ไม่ผ่าน ⇒ เหตุบน <c>link.Comment</c> (กล่องเข้าเห็น — ไม่เงียบ) ·
    /// ส่งซ้ำ = no-op (มีใบร่างแล้ว) · <b>ไม่อนุมัติเอง · ไม่ลงภาษีซื้อเอง</b>
    /// </summary>
    internal async Task TryCreatePurchaseDraftAsync(CrossTenantDocumentLink link)
    {
        if (link.LinkType != CrossTenantLinkType.InvoiceFlow || link.TargetDocumentId != null) return;
        var cfg = await _db.WorkflowAutomationConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == link.TargetCompanyId);
        var src = await _db.Documents.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == link.SourceDocumentId && d.CompanyId == link.SourceCompanyId && !d.IsDeleted);
        if (src == null) return;
        var partnership = await _db.TradingPartnerships.AsNoTracking().FirstOrDefaultAsync(p => p.Id == link.TradingPartnershipId);
        if (partnership == null || partnership.Status != TradingPartnershipStatus.Accepted) return;
        var cap = PartnerExchangeRules.ReceiverLimit(link.TargetCompanyId, partnership.CompanyAId, partnership.PurchaseDraftLimitA, partnership.PurchaseDraftLimitB);
        if (PartnerExchangeRules.DraftProblem(cfg?.CreatePurchaseDraftsFromPartners == true, src.DocumentType, src.Status, src.TotalAmount, cap, src.Currency)
            is string problem)
        {
            // ค่าตั้งต้นปิด ⇒ ไม่ต้องเขียนเหตุทุกใบ (กล่องเข้าทำงานเหมือนเดิม) · เปิดแล้วไม่ผ่าน ⇒ เหตุบนแถว
            if (cfg?.CreatePurchaseDraftsFromPartners == true)
            {
                link.Comment = problem;
                link.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
            return;
        }

        try
        {
            var seller = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == link.SourceCompanyId);
            if (seller == null) return;
            var supplierContact = await FindPartnerContactAsync(link.TargetCompanyId, seller, asSupplier: true);
            if (supplierContact == null)
            {
                supplierContact = NewPartnerContact(link.TargetCompanyId, seller, asSupplier: true);
                _db.Contacts.Add(supplierContact);
                await _db.SaveChangesAsync();
            }

            // หลักฐานภาษีซื้อ (N3): e-Tax ที่ลงลายมือชื่อของต้นทาง (ไม่ถูกยกเลิก) · ส่งไฟล์ XML ถึงผู้รับเป็นไฟล์แนบของใบร่าง (ผู้รับถือต้นฉบับ)
            var isTaxInvoice = src.DocumentType == DocumentType.TaxInvoice;
            var etax = isTaxInvoice
                ? await _db.EtaxInvoices.AsNoTracking()
                    .Where(e => e.DocumentId == src.Id && e.CompanyId == src.CompanyId && e.VoidedAt == null)
                    .OrderByDescending(e => e.CreatedAt)
                    .Select(e => new { e.XmlContent, e.DigitalSignature, e.CertificateSerialNumber, e.SignedAt, e.EtdaStampedAt, e.EtdaStampedFileAttachmentId, e.EtaxRefNumber })
                    .FirstOrDefaultAsync()
                : null;
            var signed = etax != null && !string.IsNullOrWhiteSpace(etax.DigitalSignature) && !string.IsNullOrWhiteSpace(etax.CertificateSerialNumber)
                && etax.SignedAt != null && !string.IsNullOrWhiteSpace(etax.XmlContent);
            var stamped = etax?.EtdaStampedAt != null && etax.EtdaStampedFileAttachmentId != null;

            var draft = new Document
            {
                CompanyId = link.TargetCompanyId,
                DocumentNumber = $"DRAFT-{Guid.NewGuid()}",   // §86/4 เลขของผู้รับออกตอนอนุมัติเท่านั้น
                DocumentType = DocumentType.PurchaseInvoice,
                Status = DocumentStatus.Draft,
                DocumentDate = src.DocumentDate,
                DueDate = src.DueDate,
                ContactId = supplierContact.Id,
                Currency = "THB",
                SubTotal = src.SubTotal,
                VatAmount = src.VatAmount,
                TotalAmount = src.TotalAmount,
                BalanceDue = src.TotalAmount,
                // ใบกำกับของผู้ขาย (§86/4 ฝั่งผู้รับ) — เลขที่/วันที่/สาขาตามใบต้นทาง
                SupplierInvoiceNumber = src.DocumentNumber,
                SupplierTaxInvoiceDate = isTaxInvoice ? src.DocumentDate : null,
                SupplierBranchCode = string.IsNullOrWhiteSpace(seller.BranchCode) ? TaxBranchCode.HeadOffice : seller.BranchCode,
                HasTaxInvoiceReference = isTaxInvoice,
                Reference = $"คู่ค้า NextAcc {src.DocumentNumber}",
                Notes = $"สร้างเป็นร่างจากเอกสารที่คู่ค้าส่งผ่าน NextAcc ({src.DocumentNumber}) — ตรวจผังบัญชี/ภาษีหัก ณ ที่จ่ายแล้วอนุมัติ",
                PartnerSourceLinkId = link.Id,
                CreatedBy = "partner-exchange",
            };
            var order = 1;
            foreach (var line in src.Lines.OrderBy(l => l.LineOrder))
            {
                draft.Lines.Add(new DocumentLine
                {
                    LineOrder = order++,
                    Description = line.Description,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    Amount = line.Amount,
                    VatRate = line.VatRate,
                    VatAmount = line.VatAmount,
                });
            }
            _db.Documents.Add(draft);
            await _db.SaveChangesAsync();

            var delivered = signed && await DeliverSignedXmlAsync(link.TargetCompanyId, draft.Id, etax!.EtaxRefNumber, etax.XmlContent);
            var evidence = PartnerInputVatEvidence.Of(isTaxInvoice, signed, stamped, delivered);
            await _db.Documents.Where(d => d.Id == draft.Id && d.CompanyId == link.TargetCompanyId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.PartnerInputVatEvidence, evidence));

            link.TargetDocumentId = draft.Id;
            link.Comment = "สร้างใบซื้อร่างแล้ว — " + PartnerInputVatEvidence.Label(evidence);
            link.SnapshotJson ??= JsonSerializer.Serialize(new
            {
                src.DocumentNumber, src.DocumentType, src.DocumentDate, src.SubTotal, src.VatAmount, src.TotalAmount,
                Lines = src.Lines.Select(l => new { l.Description, l.Quantity, l.UnitPrice, l.Amount, l.VatAmount }),
            });
            link.UpdatedAt = DateTime.UtcNow;
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = link.TargetCompanyId, Action = AuditAction.Create, EntityType = "Document", EntityId = draft.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new
                {
                    @event = "PartnerPurchaseDraftCreated", linkId = link.Id, supplierDocumentNumber = src.DocumentNumber,
                    total = src.TotalAmount, inputVatEvidence = evidence, ruleCode = PartnerExchangeRules.RuleCode,
                    legalReference = PartnerExchangeRules.LegalReference,
                }),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ล้มดัง: ทิ้งแถวครึ่งทางของผู้รับ (ไม่เขียนสมุดของบริษัทอื่นครึ่ง ๆ) + เหตุบนแถวกล่องเข้า + LogError · การส่งของผู้ขายไม่ล้มตาม
            DetachPendingCrossTenantWrites();
            _logger.LogError(ex, "สร้างใบซื้อร่างจาก link {Link} ไม่สำเร็จ", link.Id);
            try
            {
                await _db.CrossTenantDocumentLinks.Where(l => l.Id == link.Id && l.TargetCompanyId == link.TargetCompanyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.Comment, "สร้างใบซื้อร่างไม่สำเร็จ — บันทึกเองจากกล่องเอกสารเข้า"));
            }
            catch (Exception stampEx) when (stampEx is not OperationCanceledException)
            {
                _logger.LogError(stampEx, "บันทึกเหตุบน link {Link} ไม่สำเร็จ", link.Id);
            }
        }
    }

    /// <summary>ส่งไฟล์ XML ที่ลงลายมือชื่อของต้นทางเป็นไฟล์แนบของใบร่าง (ของบริษัทผู้รับ · ผ่านที่เก็บกลาง) · ไม่มีที่เก็บ/ล้ม = false (หลักฐานไม่ถึง EtaxVerified — ไม่แต่ง)</summary>
    private async Task<bool> DeliverSignedXmlAsync(Guid targetCompanyId, Guid draftId, string? refNumber, string xml)
    {
        if (_storage == null || string.IsNullOrWhiteSpace(xml)) return false;
        try
        {
            var relDir = $"uploads/partner-etax/{DateTime.UtcNow:yyyy-MM}";
            var name = $"{Guid.NewGuid():N}.xml";
            var abs = System.IO.Path.Combine(_storage.Locator.RelativeBase, "wwwroot", relDir, name);
            await _storage.WriteAsync(abs, System.Text.Encoding.UTF8.GetBytes(xml), "application/xml");
            _db.Set<FileAttachment>().Add(new FileAttachment
            {
                CompanyId = targetCompanyId, FileName = name,
                OriginalFileName = $"etax-{(string.IsNullOrWhiteSpace(refNumber) ? draftId.ToString("N")[..8] : refNumber)}.xml",
                ContentType = "application/xml", StoragePath = abs, EntityType = "Document", EntityId = draftId,
                UploadedByUserId = null, CreatedBy = "partner-exchange",
            });
            await _db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ส่งไฟล์ e-Tax ของคู่ค้าเข้าใบร่าง {Draft} ไม่สำเร็จ — หลักฐานเป็น PaperRequired", draftId);
            DetachPendingCrossTenantWrites();
            return false;
        }
    }

    /// <summary>ใบซื้อร่าง/ใบซื้อที่มาจากคู่ค้า NextAcc ของบริษัทนี้ (สำเนาของผู้รับเท่านั้น — ไม่มีข้อมูลของผู้ส่งนอกจากที่อยู่บนใบ)</summary>
    public async Task<List<object>> IncomingPurchaseDraftsAsync(Guid companyId, CancellationToken ct = default)
    {
        var rows = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.PartnerSourceLinkId != null && !d.IsDeleted)
            .OrderByDescending(d => d.CreatedAt)
            .Take(200)
            .Select(d => new { d.Id, d.DocumentNumber, d.SupplierInvoiceNumber, d.DocumentDate, d.TotalAmount, d.VatAmount, d.Status, d.PartnerInputVatEvidence, d.ContactId })
            .ToListAsync(ct);
        var contactIds = rows.Select(r => r.ContactId).Distinct().ToList();
        var names = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return rows.Select(r => (object)new
        {
            r.Id, r.DocumentNumber, r.SupplierInvoiceNumber, r.DocumentDate, r.TotalAmount, r.VatAmount,
            Status = r.Status.ToString(),
            StatusLabel = LodgingReservationDocuments.StatusLabel(r.Status),   // ป้ายสถานะเอกสารไทยตัวเดียว (ร่าง/มีผล/ชำระแล้ว/ยกเลิก)
            SupplierName = names.GetValueOrDefault(r.ContactId),
            InputVatEvidence = r.PartnerInputVatEvidence,
            InputVatEvidenceLabel = PartnerInputVatEvidence.Label(r.PartnerInputVatEvidence),
            NeedsOriginal = PartnerInputVatEvidence.BlocksClaim(r.PartnerInputVatEvidence),
        }).ToList();
    }

    /// <summary>ผู้รับยืนยัน "ได้ต้นฉบับใบกำกับแล้ว" — ปลดด่านภาษีซื้อ (ตัวตรวจ §86/4 ตัวเดียวอ่านค่านี้) · audit · คืนว่าใบอนุมัติแล้วไหม
    /// (อนุมัติแล้ว ⇒ ผู้เรียกต้องสั่งย้าย VAT 11640 → 11610 ผ่าน <c>CompleteSupplierTaxInvoiceAsync</c> ตัวเดิม)</summary>
    public async Task<bool> ConfirmPartnerOriginalAsync(Guid companyId, Guid documentId, Guid userId)
    {
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted && d.PartnerSourceLinkId != null)
            .Select(d => new { d.PartnerInputVatEvidence, d.Status })
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("ไม่พบใบซื้อจากคู่ค้า");
        if (PartnerInputVatEvidence.ConfirmProblem(doc.PartnerInputVatEvidence) is string problem)
            throw new BusinessRuleException(problem, PartnerExchangeRules.RuleCode, 409);
        var n = await _db.Documents
            .Where(d => d.Id == documentId && d.CompanyId == companyId && d.PartnerInputVatEvidence == doc.PartnerInputVatEvidence)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.PartnerInputVatEvidence, PartnerInputVatEvidence.PaperConfirmed));
        if (n == 0) throw new BusinessRuleException("สถานะหลักฐานเปลี่ยนระหว่างกด — รีเฟรชแล้วลองใหม่", PartnerExchangeRules.RuleCode, 409);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "Document", EntityId = documentId.ToString(),
            OldValues = JsonSerializer.Serialize(new { inputVatEvidence = doc.PartnerInputVatEvidence }),
            NewValues = JsonSerializer.Serialize(new { inputVatEvidence = PartnerInputVatEvidence.PaperConfirmed, ruleCode = PartnerExchangeRules.RuleCode,
                legalReference = PartnerExchangeRules.LegalReference }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return doc.Status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected or DocumentStatus.Voided);
    }
}
