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

    // ═══════════ รอบ 257 ชุด 3 (D-H5): ระงับการเชื่อม ═══════════

    /// <summary>ระงับการเชื่อมกับคู่ค้า (ฝ่ายใดฝ่ายหนึ่งของคู่) — <c>Status = Suspended</c> ⇒ เส้นอัตโนมัติทุกเส้นที่ตรวจ <c>Accepted</c> หยุดเอง
    /// (ส่งเอกสารเข้าระบบคู่ค้า <see cref="OnDocumentSentAsync"/> · ใบซื้อร่าง <see cref="TryCreatePurchaseDraftAsync"/> · ใบสั่งซื้อ/ใบแจ้งหนี้ปลายทาง · 50 ทวิ
    /// <c>PartnerWhtCertExchangeService</c>) · เอกสารที่ส่งไปแล้วคงอยู่ (ผู้รับต้องเก็บตามกฎหมาย) · audit chain ของบริษัทผู้ระงับ + แจ้งอีกฝ่าย ·
    /// ทางกลับ = เชิญใหม่ (<see cref="InvitePartnerAsync"/> เปิดแถว Suspended เป็นคำเชิญสด) · ไม่ใช่สมาชิกของคู่ = ไม่พบ (ไม่บอกว่ามีแถว)</summary>
    public async Task<TradingPartnership> SuspendPartnershipAsync(Guid companyId, Guid partnershipId, Guid userId, string? reason)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason is { Length: > 500 }) reason = reason[..500];
        var p = await _db.TradingPartnerships.FirstOrDefaultAsync(x => x.Id == partnershipId && (x.CompanyAId == companyId || x.CompanyBId == companyId))
            ?? throw new KeyNotFoundException("ไม่พบคู่ค้า");
        if (PartnerExchangeRules.SuspendProblem(p.Status) is string problem)
            throw new BusinessRuleException(problem, PartnerExchangeRules.RuleCode, 409);
        var before = p.Status;
        p.Status = TradingPartnershipStatus.Suspended;
        p.SuspendedAt = DateTime.UtcNow;
        p.UpdatedAt = DateTime.UtcNow;
        p.UpdatedBy = userId.ToString();
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "TradingPartnership", EntityId = p.Id.ToString(),
            OldValues = JsonSerializer.Serialize(new { status = before.ToString() }),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = "TradingPartnershipSuspended", status = p.Status.ToString(), reason,
                ruleCode = PartnerExchangeRules.RuleCode, legalReference = PartnerExchangeRules.LegalReference,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        var other = p.CompanyAId == companyId ? p.CompanyBId : p.CompanyAId;
        if (_notify != null)
            await _notify.DispatchAsync(other, Accounting.Models.Constants.NotificationEvents.AutopilotPartnerExchange, new Accounting.Services.Interfaces.NotificationContext
            {
                Title = "คู่ค้าระงับการเชื่อมระบบ NextAcc",
                Message = "หยุดรับ/ส่งเอกสาร ใบซื้อร่าง และ 50 ทวิ กับคู่ค้ารายนี้ผ่านระบบแล้ว · เอกสารที่ได้รับไปแล้วยังอยู่ · เชื่อมใหม่ได้ด้วยการส่งคำเชิญ",
                ActionUrl = "/pages/partners.html", EntityType = "TradingPartnership", EntityId = p.Id,
            });
        return p;
    }

    // ═══════════ ใบขายของคู่ค้า ⇒ ใบซื้อร่างของผู้รับ (N3) ═══════════

    /// <summary>ความลึกสูงสุดของสายเอกสารต้นทาง (แปลงจาก/ออกแทน) ที่ไล่หา "ใบซื้อเดิมของการขายครั้งเดียวกัน" (ฝ่ายค้าน H3)</summary>
    private const int MaxAncestorDepth = 6;

    /// <summary>
    /// เรียกต่อจาก <see cref="OnDocumentSentAsync"/> (แถวกล่องเข้า InvoiceFlow ถูกสร้างแล้ว) — ผู้รับเปิด <c>CreatePurchaseDraftsFromPartners</c> + ยอดไม่เกินเพดานต่อคู่ ⇒
    /// สร้าง <c>Document(PurchaseInvoice, Draft)</c> ในบริษัทผู้รับ ผูก <c>link.TargetDocumentId</c> · ไม่ผ่าน ⇒ เหตุบน <c>link.Comment</c> (กล่องเข้าเห็น — ไม่เงียบ) ·
    /// ส่งซ้ำ = no-op (มีใบร่างแล้ว) · <b>ไม่อนุมัติเอง · ไม่ลงภาษีซื้อเอง</b>
    /// <para>ฝ่ายค้าน H3: ใบที่ผู้ขายแปลงจาก/ออกแทนใบที่เคยเป็นใบซื้อของผู้รับแล้ว (สาย <c>RelatedDocumentId</c>/<c>ReplacesDocumentId</c>) = การขายครั้งเดียวกัน ⇒
    /// <b>ไม่สร้างใบที่สอง</b> แนบหลักฐาน (ไฟล์ XML + เลข/วันที่ใบกำกับ) เข้าใบซื้อเดิม + แจ้งผู้รับ · ใบเดิมอนุมัติแล้ว ⇒ แนบไฟล์ ผู้รับปิดด้วย "ได้รับต้นฉบับแล้ว"</para>
    /// <para>ฝ่ายค้าน LOW: สร้างใบร่าง + ไฟล์ + หลักฐาน + แถวกล่องเข้า ในธุรกรรมเดียว (ไม่มีใบร่างกำพร้า)</para>
    /// </summary>
    internal async Task TryCreatePurchaseDraftAsync(CrossTenantDocumentLink link)
    {
        // รอบ 257 ชุด 3 (คำตัดสิน 257-3-5): ใบเสร็จที่เป็นใบกำกับ (ReceiptFlow) เข้าเส้นนี้เพื่อ "แนบหลักฐานเข้าใบซื้อเดิม" เท่านั้น — ไม่สร้างใบซื้อใหม่
        var isReceiptFlow = link.LinkType == CrossTenantLinkType.ReceiptFlow;
        if ((link.LinkType != CrossTenantLinkType.InvoiceFlow && !isReceiptFlow) || link.TargetDocumentId != null) return;
        var cfg = await _db.WorkflowAutomationConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyId == link.TargetCompanyId);
        var src = await _db.Documents.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == link.SourceDocumentId && d.CompanyId == link.SourceCompanyId && !d.IsDeleted);
        if (src == null) return;
        var partnership = await _db.TradingPartnerships.AsNoTracking().FirstOrDefaultAsync(p => p.Id == link.TradingPartnershipId);
        if (partnership == null || partnership.Status != TradingPartnershipStatus.Accepted) return;
        // ใบนี้ทำหน้าที่ใบกำกับไหม (ใบกำกับ · ใบเสร็จที่ตรึงว่าเป็นใบกำกับและมี VAT) — ตัวตัดสินเดียว
        var isTaxInvoice = PartnerExchangeRules.IsTaxInvoiceCarrier(src.DocumentType, src.IsTaxInvoiceByLaw, src.VatAmount, src.ReceiptForm);
        if (isReceiptFlow)
        {
            // ใบเสร็จที่ไม่ใช่ใบกำกับ (รับชำระใบกำกับที่ออกแล้ว · ไม่มี VAT) = หลักฐานรับเงิน ไม่ใช่หลักฐานภาษีซื้อ ⇒ กล่องเข้าเดิมพอ
            if (cfg?.CreatePurchaseDraftsFromPartners != true || !isTaxInvoice) return;
            if (PartnerExchangeRules.DraftProblem(true, DocumentType.TaxInvoice, src.Status, src.TotalAmount, null, src.Currency) is string rp)
            {
                link.Comment = rp;
                link.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return;
            }
        }
        else if (PartnerExchangeRules.DraftProblem(cfg?.CreatePurchaseDraftsFromPartners == true, src.DocumentType, src.Status, src.TotalAmount,
                     PartnerExchangeRules.ReceiverLimit(link.TargetCompanyId, partnership.CompanyAId, partnership.PurchaseDraftLimitA, partnership.PurchaseDraftLimitB),
                     src.Currency)
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

        var ownTx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var seller = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == link.SourceCompanyId);
            if (seller == null) { if (ownTx != null) await ownTx.RollbackAsync(); return; }

            // หลักฐานภาษีซื้อ (N3 · ฝ่ายค้าน H2): ระบบ<b>ไม่เคยตั้ง EtaxVerified เอง</b> (ไม่มีการตรวจกับ ETDA) — e-Tax ลงลายมือชื่อที่ไฟล์ถึงผู้รับแล้ว = AwaitingTimestamp ·
            // อื่น ๆ = PaperRequired · ผู้รับยืนยันเองว่าได้ไฟล์ที่ประทับเวลา/ต้นฉบับกระดาษ ⇒ EtaxVerified/PaperConfirmed
            var etax = isTaxInvoice
                ? await _db.EtaxInvoices.AsNoTracking()
                    .Where(e => e.DocumentId == src.Id && e.CompanyId == src.CompanyId && e.VoidedAt == null)
                    .OrderByDescending(e => e.CreatedAt)
                    .Select(e => new { e.XmlContent, e.DigitalSignature, e.CertificateSerialNumber, e.SignedAt, e.EtaxRefNumber })
                    .FirstOrDefaultAsync()
                : null;
            var signed = etax != null && !string.IsNullOrWhiteSpace(etax.DigitalSignature) && !string.IsNullOrWhiteSpace(etax.CertificateSerialNumber)
                && etax.SignedAt != null && !string.IsNullOrWhiteSpace(etax.XmlContent);

            // ── ฝ่ายค้าน H3: การขายครั้งเดียวกันที่ผู้รับมีใบซื้ออยู่แล้ว ⇒ แนบหลักฐานเข้าใบเดิม ──
            var existingId = await ExistingPurchaseForChainAsync(src, link.TargetCompanyId);
            if (existingId is Guid d1Id)
            {
                var d1 = await _db.Documents.FirstAsync(d => d.Id == d1Id && d.CompanyId == link.TargetCompanyId);
                var delivered1 = signed && await DeliverSignedXmlAsync(link.TargetCompanyId, d1.Id, etax!.EtaxRefNumber, etax.XmlContent);
                var offered = PartnerInputVatEvidence.Of(isTaxInvoice, signed, delivered1);
                // รอบ 257 ชุด 3 (D-H4): ใบที่ผูกไว้ก่อนหน้าไม่มีใบไหนเป็นใบกำกับ ⇒ คำยืนยันเดิม (ของใบแจ้งหนี้) ไม่ใช่ต้นฉบับใบกำกับ ⇒ ตั้งใหม่ด้วย Of(...)
                var priorTaxInvoice = await ChainHasTaxInvoiceAsync(link.TargetCompanyId, d1.Id, src.CompanyId, link.Id);
                var evidenceBefore = d1.PartnerInputVatEvidence;
                // ฝ่ายค้าน LOW: ใบแทน (สาย ReplacesDocumentId) ของต้นทางที่ถูกยกเลิกมาถึง ⇒ ล้าง SourceVoided แล้วตัดสินใหม่ (ตัวตัดสินเดียว AnyUnreplacedVoided)
                var currentForMerge = evidenceBefore;
                if (evidenceBefore == PartnerInputVatEvidence.SourceVoided
                    && !PartnerExchangeRules.AnyUnreplacedVoided(await ChainSourceLivenessAsync(link.TargetCompanyId, d1.Id, src.CompanyId, link.Id, src)))
                    currentForMerge = null;
                // ฝ่ายค้าน M3: VAT ของใบกำกับใหม่ไม่เท่า VAT บนใบซื้อ ⇒ ไม่ตั้งหลักฐานที่ปลดภาษีซื้อได้ (คง/ตั้ง AwaitingTaxInvoice) + เหตุบนแถว + แจ้ง
                var vatMatches = !isTaxInvoice || PartnerInputVatEvidence.VatMatches(src.VatAmount, d1.VatAmount);
                // รอบ 257 ปิดท้าย MED-4: ผู้รับยืนยันใบกำกับใบนี้ไว้แล้วผ่านทาง "ได้ใบกำกับนอกระบบ" (audit ของทางนั้น + เลขที่ตรงกับใบที่มาถึง) ⇒ คงคำยืนยัน
                // (เดิมตั้งกลับ PaperRequired ทุกครั้งที่ใบกำกับแรกของสายมาถึง — ใบที่ยืนยันถูกแล้วถูกดึงกลับ) · เลขไม่ตรง/VAT ไม่ตรง ⇒ ตั้งกลับ + แจ้งเหมือนเดิม
                var externalCovers = isTaxInvoice && PartnerInputVatEvidence.ExternalConfirmationCovers(currentForMerge,
                    await ConfirmedThroughExternalPathAsync(link.TargetCompanyId, d1.Id), d1.SupplierInvoiceNumber, src.DocumentNumber);
                d1.PartnerInputVatEvidence = PartnerInputVatEvidence.OnNewSource(currentForMerge, offered, isTaxInvoice, priorTaxInvoice, vatMatches, externalCovers);
                if (d1.Status == DocumentStatus.Draft && isTaxInvoice && vatMatches)
                {
                    // ร่างยังแก้ได้ ⇒ เลข/วันที่ใบกำกับของผู้ขายตามใบกำกับจริง · อนุมัติแล้ว ⇒ ปิดด้วย "ได้รับต้นฉบับแล้ว" (ส่งเลข/วันที่ผ่านเส้นเติมใบกำกับเดิม)
                    d1.SupplierInvoiceNumber = src.DocumentNumber;
                    d1.SupplierTaxInvoiceDate = src.DocumentDate;
                    d1.HasTaxInvoiceReference = true;
                }
                d1.UpdatedAt = DateTime.UtcNow;
                link.TargetDocumentId = d1.Id;
                link.Comment = vatMatches
                    ? $"ผูกกับใบซื้อเดิมของการขายครั้งเดียวกัน (ไม่สร้างใบที่สอง) — {PartnerInputVatEvidence.Label(d1.PartnerInputVatEvidence)}"
                    : $"ผูกกับใบซื้อเดิม — {PartnerInputVatEvidence.VatMismatchMessage} (ใบกำกับ {src.VatAmount:#,##0.00} · ใบซื้อ {d1.VatAmount:#,##0.00})";
                link.UpdatedAt = DateTime.UtcNow;
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = link.TargetCompanyId, Action = AuditAction.Update, EntityType = "Document", EntityId = d1.Id.ToString(),
                    NewValues = JsonSerializer.Serialize(new
                    {
                        @event = "PartnerEvidenceAttachedToExisting", linkId = link.Id, supplierDocumentNumber = src.DocumentNumber,
                        inputVatEvidenceBefore = evidenceBefore, inputVatEvidence = d1.PartnerInputVatEvidence, vatMatches,
                        externalConfirmationKept = externalCovers && vatMatches && !priorTaxInvoice,
                        sourceVoidedCleared = evidenceBefore == PartnerInputVatEvidence.SourceVoided && currentForMerge == null,
                        evidenceReset = evidenceBefore != d1.PartnerInputVatEvidence && !PartnerInputVatEvidence.BlocksClaim(evidenceBefore)
                            && evidenceBefore != null,
                        ruleCode = PartnerExchangeRules.RuleCode, legalReference = PartnerExchangeRules.LegalReference,
                    }),
                    Timestamp = DateTime.UtcNow,
                });
                await _db.SaveChangesAsync();
                if (ownTx != null) await ownTx.CommitAsync();
                // รอบ 257 ชุด 3 (D-H4): ข้อความตามสถานะจริงของใบ — ใบที่อนุมัติแล้วยืนยันได้จริง (ปลด VAT ผ่านเส้นเติมใบกำกับ) · ยืนยันไม่ได้ ⇒ บอกเหตุจากตัวตัดสินเดียว
                var approvedD1 = d1.Status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected or DocumentStatus.Voided);
                var next = !vatMatches
                    ? PartnerInputVatEvidence.VatMismatchMessage
                    : PartnerInputVatEvidence.ConfirmProblem(d1.PartnerInputVatEvidence) is string blocked
                    ? blocked
                    : approvedD1
                        ? "ใบซื้อเดิมอนุมัติแล้ว — เมื่อได้ต้นฉบับใบกำกับ กด \"ได้รับต้นฉบับแล้ว\" ที่หน้าคู่ค้า ระบบจะเติมเลข/วันที่ใบกำกับและย้ายภาษีซื้อเข้างวดที่เคลมได้"
                        : "ใบซื้อยังเป็นร่าง — เมื่อได้ต้นฉบับใบกำกับ กด \"ได้รับต้นฉบับแล้ว\" แล้วอนุมัติตามปกติ";
                await NotifyReceiverAsync(link.TargetCompanyId, d1.Id,
                    $"คู่ค้าส่งใบ {src.DocumentNumber} ของการขายเดิม — แนบเข้าใบซื้อเดิมแล้ว",
                    "ไม่ได้สร้างใบซื้อใหม่ (การขายครั้งเดียวกัน) · " + next);
                return;
            }

            if (isReceiptFlow)
            {
                // คำตัดสิน 257-3-5: ใบเสร็จที่เป็นใบกำกับของการขายเงินสด (ไม่มีใบซื้อเดิม) ไม่สร้างใบซื้อร่าง — เหตุบนแถวกล่องเข้า (ไม่เงียบ)
                link.Comment = PartnerExchangeRules.ReceiptNoChainMessage;
                link.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                if (ownTx != null) await ownTx.CommitAsync();
                return;
            }

            // รอบ 257 ชุด 3 (D-M2 · ฝ่ายค้าน M4): คัดลอกได้ครบเท่าต้นฉบับเท่านั้น (ตัวตรวจ/ตัวคัดลอกเดียวกับเอกสารปลายทาง) — ไม่งั้นเหตุบนแถวกล่องเข้า
            if (PartnerCopyProblem(src) is string copyProblem)
            {
                link.Comment = copyProblem;
                link.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                if (ownTx != null) await ownTx.CommitAsync();
                return;
            }

            // รอบ 257 ชุด 3 (D-M4 คู่สมมาตร): สาขาผู้ขาย = สาขาที่ออกใบนั้น (ตัวตัดสินเดียว DocumentIssuerBranch — snapshot ชนะทะเบียน ชนะค่าบริษัท)
            var registryBranch = src.BranchId is Guid srcBranchId
                ? await _db.Branches.AsNoTracking().IgnoreQueryFilters()
                    .Where(b => b.Id == srcBranchId && b.CompanyId == src.CompanyId).Select(b => b.TaxBranchCode).FirstOrDefaultAsync()
                : null;
            var sellerBranch = DocumentIssuerBranch.ResolveCode(src.IssuerBranchCode, registryBranch, seller.BranchCode);

            var supplierContact = await FindPartnerContactAsync(link.TargetCompanyId, seller, asSupplier: true);
            if (supplierContact == null)
            {
                supplierContact = NewPartnerContact(link.TargetCompanyId, seller, asSupplier: true);
                _db.Contacts.Add(supplierContact);
                await _db.SaveChangesAsync();
            }

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
                // ใบกำกับของผู้ขาย (§86/4 ฝั่งผู้รับ) — เลขที่/วันที่/สาขาตามใบต้นทาง
                SupplierInvoiceNumber = src.DocumentNumber,
                SupplierTaxInvoiceDate = isTaxInvoice ? src.DocumentDate : null,
                SupplierBranchCode = sellerBranch,
                HasTaxInvoiceReference = isTaxInvoice,
                Reference = $"คู่ค้า NextAcc {src.DocumentNumber}",
                Notes = $"สร้างเป็นร่างจากเอกสารที่คู่ค้าส่งผ่าน NextAcc ({src.DocumentNumber}) — ตรวจผังบัญชี/ภาษีหัก ณ ที่จ่ายแล้วอนุมัติ",
                PartnerSourceLinkId = link.Id,
                PartnerInputVatEvidence = PartnerInputVatEvidence.PaperRequired,
                CreatedBy = "partner-exchange",
            };
            // รอบ 257 ชุด 3 (D-M2 · ฝ่ายค้าน M4 · LOW): ยอด/ส่วนลด/ภาษีหัก/ราคารวม VAT + บรรทัด ผ่านตัวคัดลอกเดียว
            CopyPartnerMoney(src, draft);
            _db.Documents.Add(draft);
            await _db.SaveChangesAsync();

            var delivered = signed && await DeliverSignedXmlAsync(link.TargetCompanyId, draft.Id, etax!.EtaxRefNumber, etax.XmlContent);
            var evidence = PartnerInputVatEvidence.Of(isTaxInvoice, signed, delivered);
            draft.PartnerInputVatEvidence = evidence;

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
            if (ownTx != null) await ownTx.CommitAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ล้มดัง: ย้อนธุรกรรม + ทิ้งแถวครึ่งทางของผู้รับ (ไม่เขียนสมุดของบริษัทอื่นครึ่ง ๆ) + เหตุบนแถวกล่องเข้า + LogError · การส่งของผู้ขายไม่ล้มตาม
            if (ownTx != null) { try { await ownTx.RollbackAsync(); } catch (Exception rex) when (rex is not OperationCanceledException) { _ = rex; } }
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
        finally
        {
            if (ownTx != null) await ownTx.DisposeAsync();
        }
    }

    /// <summary>ฝ่ายค้าน H3: ใบซื้อของผู้รับที่มาจากใบ<b>ต้นสาย</b>ของ <paramref name="src"/> (ใบที่ใบนี้แปลงมา · ใบที่ใบนี้ออกแทน — ไล่ขึ้น ≤ 6 ชั้นในบริษัทผู้ขาย) ·
    /// null = ไม่มี (ใบใหม่ที่ไม่เกี่ยวกัน ⇒ ร่างใหม่) · ใบซื้อเดิมที่ถูกยกเลิก/ลบ ไม่นับ</summary>
    private async Task<Guid?> ExistingPurchaseForChainAsync(Document src, Guid targetCompanyId)
    {
        var ancestors = new List<Guid>();
        var frontier = new List<Guid>();
        if (src.RelatedDocumentId is Guid r0) frontier.Add(r0);
        if (src.ReplacesDocumentId is Guid p0) frontier.Add(p0);
        for (var depth = 0; depth < MaxAncestorDepth && frontier.Count > 0; depth++)
        {
            var batch = frontier.Where(id => !ancestors.Contains(id)).ToList();
            ancestors.AddRange(batch);
            var parents = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                .Where(d => batch.Contains(d.Id) && d.CompanyId == src.CompanyId)
                .Select(d => new { d.RelatedDocumentId, d.ReplacesDocumentId })
                .ToListAsync();
            frontier = parents.SelectMany(x => new[] { x.RelatedDocumentId, x.ReplacesDocumentId })
                .Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        }
        if (ancestors.Count == 0) return null;
        var targets = await _db.CrossTenantDocumentLinks.AsNoTracking()
            .Where(l => l.SourceCompanyId == src.CompanyId && l.TargetCompanyId == targetCompanyId && ancestors.Contains(l.SourceDocumentId)
                && l.TargetDocumentId != null)
            .Select(l => l.TargetDocumentId!.Value)
            .ToListAsync();
        if (targets.Count == 0) return null;
        return await _db.Documents.AsNoTracking()
            .Where(d => targets.Contains(d.Id) && d.CompanyId == targetCompanyId && !d.IsDeleted && d.Status != DocumentStatus.Voided
                && d.PartnerSourceLinkId != null)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>รอบ 257 ชุด 3 (D-M2 · ฝ่ายค้าน M4/LOW): ตัวตรวจการคัดลอกตัวเดียวของทุกเอกสารที่สร้างจากใบของคู่ค้า (ใบซื้อร่าง · ใบสั่งซื้อ/ใบแจ้งหนี้ปลายทาง)</summary>
    internal static string? PartnerCopyProblem(Document src)
    {
        return PartnerExchangeRules.CopyProblem(src.SubTotal, src.VatAmount, src.WithholdingTaxAmount, src.TotalAmount, src.RoundingAdjustment,
            src.DepositBaseDeducted, src.WhtPayerBorneAmount, src.PostVatDeductionAmount,
            src.Lines.Where(l => !l.IsDeleted)
                .Select(l => new PartnerExchangeRules.CopyLine(l.Quantity, l.UnitPrice, l.DiscountAmount, l.Amount, l.VatAmount, l.WithholdingTaxAmount)).ToList(),
            src.PricesIncludeVat, src.BillDiscountAmount);
    }

    /// <summary>รอบ 257 ชุด 3: ตัวคัดลอกยอด + บรรทัดตัวเดียว (ส่วนลดรายบรรทัด/ท้ายบิล · ผลต่างปัดเศษ · ภาษีหัก ณ ที่จ่าย · ราคารวม VAT · หน่วย · ประเภทเงินได้) —
    /// ผู้เรียกตรวจ <see cref="PartnerCopyProblem"/> ก่อนเสมอ · ไม่คัดลอกผังบัญชี/รหัสสินค้า/ผู้ถูกหักรายบรรทัดของอีกบริษัท</summary>
    internal static void CopyPartnerMoney(Document src, Document target)
    {
        target.SubTotal = src.SubTotal;
        target.DiscountAmount = src.DiscountAmount;
        target.BillDiscountPercent = src.BillDiscountPercent;
        target.BillDiscountAmount = src.BillDiscountAmount;
        target.RoundingAdjustment = src.RoundingAdjustment;
        target.WithholdingTaxAmount = src.WithholdingTaxAmount;
        target.PricesIncludeVat = src.PricesIncludeVat;
        target.VatAmount = src.VatAmount;
        target.TotalAmount = src.TotalAmount;
        target.BalanceDue = src.TotalAmount;
        var order = 1;
        foreach (var line in src.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder))
        {
            target.Lines.Add(new DocumentLine
            {
                LineOrder = order++,
                Description = line.Description,
                Quantity = line.Quantity,
                Unit = line.Unit,
                UnitPrice = line.UnitPrice,
                DiscountPercent = line.DiscountPercent,
                DiscountAmount = line.DiscountAmount,
                Amount = line.Amount,
                VatRate = line.VatRate,
                VatAmount = line.VatAmount,
                WithholdingTaxRate = line.WithholdingTaxRate,
                WithholdingTaxAmount = line.WithholdingTaxAmount,
                IncomeTypeCode = line.IncomeTypeCode,
            });
        }
    }

    /// <summary>ฝ่ายค้าน LOW: ต้นทางทุกใบในสายของใบซื้อ <paramref name="purchaseId"/> (แถวกล่องเข้าของผู้รับ · เอกสารของผู้ขายรายเดียว) + ใบใหม่ <paramref name="incoming"/> —
    /// ถูกยกเลิก (ใบยกเลิก/ลบ หรือ e-Tax ของใบถูกยกเลิกหมด) · สาย ReplacesDocumentId · ให้ <see cref="PartnerExchangeRules.AnyUnreplacedVoided"/> ตัดสิน</summary>
    private async Task<List<PartnerExchangeRules.SourceLiveness>> ChainSourceLivenessAsync(Guid targetCompanyId, Guid purchaseId, Guid sellerCompanyId,
        Guid? exceptLinkId, Document? incoming)
    {
        var sourceIds = await _db.CrossTenantDocumentLinks.AsNoTracking()
            .Where(l => l.TargetCompanyId == targetCompanyId && l.TargetDocumentId == purchaseId && l.SourceCompanyId == sellerCompanyId
                && (exceptLinkId == null || l.Id != exceptLinkId))
            .Select(l => l.SourceDocumentId)
            .ToListAsync();
        var docs = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => sourceIds.Contains(d.Id) && d.CompanyId == sellerCompanyId)
            .Select(d => new { d.Id, d.Status, d.IsDeleted, d.ReplacesDocumentId })
            .ToListAsync();
        var etaxRows = await _db.EtaxInvoices.AsNoTracking()
            .Where(e => sourceIds.Contains(e.DocumentId) && e.CompanyId == sellerCompanyId)
            .Select(e => new { e.DocumentId, e.VoidedAt })
            .ToListAsync();
        var list = docs.Select(d =>
        {
            var rows = etaxRows.Where(e => e.DocumentId == d.Id).ToList();
            var voided = d.IsDeleted || d.Status == DocumentStatus.Voided || (rows.Count > 0 && rows.All(e => e.VoidedAt != null));
            return new PartnerExchangeRules.SourceLiveness(d.Id, voided, d.ReplacesDocumentId);
        }).ToList();
        // ต้นทางที่หาไม่เจอ (ถูกลบจริง) = ยกเลิก
        list.AddRange(sourceIds.Where(id => docs.All(d => d.Id != id)).Distinct().Select(id => new PartnerExchangeRules.SourceLiveness(id, true, null)));
        if (incoming != null && list.All(x => x.Id != incoming.Id))
            list.Add(new PartnerExchangeRules.SourceLiveness(incoming.Id, incoming.Status == DocumentStatus.Voided || incoming.IsDeleted, incoming.ReplacesDocumentId));
        return list;
    }

    /// <summary>รอบ 257 ชุด 3 (D-H4): ใบซื้อ <paramref name="purchaseId"/> ของผู้รับเคยผูกกับเอกสารของผู้ขายที่ทำหน้าที่ใบกำกับแล้วไหม (ไม่นับแถว <paramref name="exceptLinkId"/>
    /// ที่กำลังผูก) — ตัดสินด้วย <see cref="PartnerExchangeRules.IsTaxInvoiceCarrier"/> ตัวเดียว · ใบต้นทางที่ถูกลบ/ยกเลิกยังนับ (คำยืนยันที่ทำไว้เป็นของใบนั้น) ·
    /// tenant: แถวกล่องเข้าของผู้รับ + เอกสารของบริษัทผู้ขาย <paramref name="sellerCompanyId"/></summary>
    private async Task<bool> ChainHasTaxInvoiceAsync(Guid targetCompanyId, Guid purchaseId, Guid sellerCompanyId, Guid exceptLinkId)
    {
        var sourceIds = await _db.CrossTenantDocumentLinks.AsNoTracking()
            .Where(l => l.TargetCompanyId == targetCompanyId && l.TargetDocumentId == purchaseId && l.Id != exceptLinkId && l.SourceCompanyId == sellerCompanyId)
            .Select(l => l.SourceDocumentId)
            .ToListAsync();
        if (sourceIds.Count == 0) return false;
        var sources = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => sourceIds.Contains(d.Id) && d.CompanyId == sellerCompanyId)
            .Select(d => new { d.DocumentType, d.IsTaxInvoiceByLaw, d.VatAmount, d.ReceiptForm })
            .ToListAsync();
        return sources.Any(d => PartnerExchangeRules.IsTaxInvoiceCarrier(d.DocumentType, d.IsTaxInvoiceByLaw, d.VatAmount, d.ReceiptForm));
    }

    /// <summary>รอบ 257 ปิดท้าย MED-4: ใบซื้อนี้เคยถูกยืนยันผ่านทาง "ได้ใบกำกับนอกระบบ" ไหม (audit ของบริษัทผู้รับเอง — tenant) ·
    /// ตัวเขียน audit เดียว <see cref="ConfirmExternalTaxInvoiceAsync"/> · ชื่อเหตุการณ์ตัวเดียว <see cref="PartnerInputVatEvidence.ExternalConfirmedEvent"/></summary>
    private Task<bool> ConfirmedThroughExternalPathAsync(Guid targetCompanyId, Guid purchaseId)
    {
        var idText = purchaseId.ToString();
        return _db.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == targetCompanyId && a.EntityType == "Document" && a.EntityId == idText
            && a.NewValues != null && a.NewValues.Contains(PartnerInputVatEvidence.ExternalConfirmedEvent));
    }

    /// <summary>แจ้งผู้รับ (บริษัทคู่ค้า) — คีย์ autopilot.partner_exchange · ไม่มีเครื่องแจ้งเตือน (สร้างนอก DI) = ข้าม</summary>
    private async Task NotifyReceiverAsync(Guid companyId, Guid documentId, string title, string message)
    {
        if (_notify == null) return;
        await _notify.DispatchAsync(companyId, Accounting.Models.Constants.NotificationEvents.AutopilotPartnerExchange, new Accounting.Services.Interfaces.NotificationContext
        {
            Title = title, Message = message, ActionUrl = "/pages/partners.html", EntityType = "Document", EntityId = documentId,
        });
    }

    /// <summary>ส่งไฟล์ XML ที่ลงลายมือชื่อของต้นทางเป็นไฟล์แนบของใบร่าง (ของบริษัทผู้รับ · ผ่านที่เก็บกลาง) · ไม่มีที่เก็บ/ล้ม = false (หลักฐานคง PaperRequired — ไม่แต่ง) ·
    /// แถวไฟล์แนบเข้า ChangeTracker — ผู้เรียกบันทึกในธุรกรรมเดียวกับใบ</summary>
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
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ส่งไฟล์ e-Tax ของคู่ค้าเข้าใบร่าง {Draft} ไม่สำเร็จ — หลักฐานเป็น PaperRequired", draftId);
            return false;
        }
    }

    /// <summary>ฝ่ายค้าน M2: ผู้ขายยกเลิกเอกสารต้นทาง ⇒ ใบซื้อของผู้รับที่มาจากใบนั้นติด <see cref="PartnerInputVatEvidence.SourceVoided"/>
    /// (ตัวตรวจ §86/4 ห้ามลงภาษีซื้อ · ยืนยันต้นฉบับไม่ได้) + แถวกล่องเข้า Withdrawn + แจ้งผู้รับ · ไม่ย้อน/ยกเลิกใบของผู้รับเอง (ผู้รับตัดสิน) ·
    /// เรียกจาก <c>DocumentService.VoidDocumentAsync</c> หลัง commit · ล้ม = LogError (ไม่ย้อนการยกเลิกของผู้ขาย)</summary>
    public async Task<int> OnSourceVoidedAsync(Guid sourceCompanyId, Guid sourceDocumentId)
    {
        try
        {
            var links = await _db.CrossTenantDocumentLinks
                .Where(l => l.SourceCompanyId == sourceCompanyId && l.SourceDocumentId == sourceDocumentId && l.TargetDocumentId != null)
                .ToListAsync();
            var n = 0;
            foreach (var l in links)
            {
                var updated = await _db.Documents
                    .Where(d => d.Id == l.TargetDocumentId && d.CompanyId == l.TargetCompanyId && d.PartnerSourceLinkId != null)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.PartnerInputVatEvidence, PartnerInputVatEvidence.SourceVoided));
                l.Status = CrossTenantLinkStatus.Withdrawn;
                l.Comment = "ผู้ขายยกเลิกเอกสารต้นทางแล้ว — ห้ามลงภาษีซื้อจากใบนี้";
                l.UpdatedAt = DateTime.UtcNow;
                if (updated > 0)
                {
                    n++;
                    await NotifyReceiverAsync(l.TargetCompanyId, l.TargetDocumentId!.Value, "คู่ค้ายกเลิกเอกสารที่ส่งเข้ามา",
                        "ใบซื้อที่มาจากเอกสารนี้ถูกตั้งเป็น \"ต้นทางถูกยกเลิก\" — ลงภาษีซื้อไม่ได้ · ตรวจแล้วยกเลิก/กลับรายการใบซื้อของท่าน");
                }
            }
            await _db.SaveChangesAsync();
            return n;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งคู่ค้าเรื่องเอกสารต้นทาง {Doc} ถูกยกเลิกไม่สำเร็จ", sourceDocumentId);
            return 0;
        }
    }

    /// <param name="LatestTaxInvoiceVat">รอบ 257 ปิดท้าย MED-3: VAT ของใบกำกับล่าสุดที่ยังมีผลในสาย (null = สายนี้ไม่มีใบกำกับผ่านระบบ)</param>
    public sealed record PartnerSourceStatus(bool Voided, string? LatestTaxInvoiceNumber, DateTime? LatestTaxInvoiceDate, decimal? LatestTaxInvoiceVat = null);

    /// <summary>ฝ่ายค้าน M2: ตรวจต้นทางสดของใบซื้อจากคู่ค้า (ทุกแถวกล่องเข้าที่ผูกใบนี้) — ใบของผู้ขายถูกลบ/ยกเลิก หรือ e-Tax ของใบนั้นถูกยกเลิกหมด ⇒ Voided ·
    /// คืนเลข/วันที่ใบกำกับล่าสุดในสาย (ใบกำกับที่มาทีหลังของการขายเดิม · H3) · tenant: แถวกล่องเข้าของผู้รับ + เอกสารของบริษัทผู้ขายตามแถว</summary>
    public async Task<PartnerSourceStatus> PartnerSourceCheckAsync(Guid targetCompanyId, Guid documentId)
    {
        var links = await _db.CrossTenantDocumentLinks.AsNoTracking()
            .Where(l => l.TargetCompanyId == targetCompanyId && l.TargetDocumentId == documentId)
            .Select(l => new { l.SourceCompanyId, l.SourceDocumentId, l.CreatedAt })
            .ToListAsync();
        string? latestTaxNo = null; DateTime? latestTaxDate = null; decimal? latestTaxVat = null;
        // ฝ่ายค้าน LOW: ต้นทางที่ถูกยกเลิกแต่มีใบแทนที่มีผลในสายเดียวกัน (ReplacesDocumentId) = ไม่ติดธง (ตัวตัดสินเดียว AnyUnreplacedVoided)
        var liveness = new List<PartnerExchangeRules.SourceLiveness>();
        foreach (var sellerId in links.Select(x => x.SourceCompanyId).Distinct())
            liveness.AddRange(await ChainSourceLivenessAsync(targetCompanyId, documentId, sellerId, null, null));
        if (PartnerExchangeRules.AnyUnreplacedVoided(liveness)) return new PartnerSourceStatus(true, null, null);
        var voidedIds = liveness.Where(x => x.Voided).Select(x => x.Id).ToHashSet();
        foreach (var l in links.OrderBy(x => x.CreatedAt))
        {
            if (voidedIds.Contains(l.SourceDocumentId)) continue;
            var s = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                .Where(d => d.Id == l.SourceDocumentId && d.CompanyId == l.SourceCompanyId)
                .Select(d => new { d.DocumentType, d.DocumentNumber, d.DocumentDate, d.IsTaxInvoiceByLaw, d.VatAmount, d.ReceiptForm })
                .FirstOrDefaultAsync();
            if (s == null) continue;
            // รอบ 257 ชุด 3: ใบเสร็จที่เป็นใบกำกับก็เป็นเลข/วันที่ใบกำกับของผู้ขาย (ตัวตัดสินเดียวกับตอนแนบหลักฐาน)
            if (PartnerExchangeRules.IsTaxInvoiceCarrier(s.DocumentType, s.IsTaxInvoiceByLaw, s.VatAmount, s.ReceiptForm)) { latestTaxNo = s.DocumentNumber; latestTaxDate = s.DocumentDate; latestTaxVat = s.VatAmount; }
        }
        return new PartnerSourceStatus(false, latestTaxNo, latestTaxDate, latestTaxVat);
    }

    /// <summary>ตั้ง SourceVoided บนใบซื้อจากคู่ค้าของผู้รับ (ใบที่ไม่ได้มาจากคู่ค้า ไม่แตะ) — ใช้หลังธุรกรรมยืนยันถูกย้อน</summary>
    public Task<int> MarkSourceVoidedAsync(Guid companyId, Guid documentId)
        => _db.Documents.Where(d => d.Id == documentId && d.CompanyId == companyId && d.PartnerSourceLinkId != null)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.PartnerInputVatEvidence, PartnerInputVatEvidence.SourceVoided));

    /// <summary>ฝ่ายค้าน M2 (ด่านอนุมัติ): ใบซื้อจากคู่ค้าที่ต้นทางถูกยกเลิกแล้ว ⇒ ตั้ง SourceVoided บนตัวใบ (tracked · ผู้เรียกบันทึก) ก่อนตัวตรวจ §86/4 ⇒ ภาษีซื้อพัก + ข้อความไทย ·
    /// ใบซื้อทั่วไป (ไม่มี PartnerSourceLinkId) ไม่แตะ · คืน true เมื่อถูกตั้ง</summary>
    public async Task<bool> MarkIfPartnerSourceVoidedAsync(Guid companyId, Document doc)
    {
        if (doc.PartnerSourceLinkId == null || doc.CompanyId != companyId || doc.PartnerInputVatEvidence == PartnerInputVatEvidence.SourceVoided) return false;
        var status = await PartnerSourceCheckAsync(companyId, doc.Id);
        if (!status.Voided) return false;
        doc.PartnerInputVatEvidence = PartnerInputVatEvidence.SourceVoided;
        return true;
    }

    /// <summary>ใบซื้อร่าง/ใบซื้อที่มาจากคู่ค้า NextAcc ของบริษัทนี้ (สำเนาของผู้รับเท่านั้น — ไม่มีข้อมูลของผู้ส่งนอกจากที่อยู่บนใบ)</summary>
    /// <param name="mayConfirmTaxInvoice">รอบ 257 ปิดท้าย LOW: ผู้เรียกมีสิทธิ์อนุมัติเอกสารซื้อไหม (controller ตัดสิน) — ปุ่ม "ได้รับใบกำกับภาษีจากผู้ขายนอกระบบ"
    /// แสดงเฉพาะผู้ที่กดแล้วผ่านจริง (server computes — เดิมทุกคนที่เห็นฝั่งซื้อเห็นปุ่มแล้วกดได้ 403)</param>
    public async Task<List<object>> IncomingPurchaseDraftsAsync(Guid companyId, CancellationToken ct = default, bool mayConfirmTaxInvoice = true)
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
            NeedsOriginal = PartnerInputVatEvidence.ConfirmProblem(r.PartnerInputVatEvidence) == null,
            // รอบ 257 ชุด 3 (D-H4 · F2 ข้อ 5): ปุ่ม "ได้ e-Tax ที่ประทับเวลา" + เหตุที่กดไม่ได้ มาจากตัวตัดสินเดียว (หน้าเว็บไม่เทียบสตริงหลักฐานเอง)
            CanConfirmStampedEtax = PartnerInputVatEvidence.CanConfirmStampedEtax(r.PartnerInputVatEvidence),
            CanConfirmExternalTaxInvoice = mayConfirmTaxInvoice && PartnerInputVatEvidence.CanConfirmExternalTaxInvoice(r.PartnerInputVatEvidence),   // ฝ่ายค้าน M1 + สิทธิ์ของผู้เรียก
            InputVatBlocked = PartnerInputVatEvidence.BlocksClaim(r.PartnerInputVatEvidence),   // สีป้าย: ยังลงภาษีซื้อไม่ได้ (รวมรอใบกำกับ · ต้นทางยกเลิก)
            ConfirmBlockedReason = PartnerInputVatEvidence.ConfirmProblem(r.PartnerInputVatEvidence),
            SourceVoided = r.PartnerInputVatEvidence == PartnerInputVatEvidence.SourceVoided,
        }).ToList();
    }

    /// <summary>ผลของการยืนยันต้นฉบับ — ใบอนุมัติแล้วไหม + เลข/วันที่ใบกำกับของผู้ขายที่ควรส่งเข้าเส้นเติมใบกำกับ (ใบกำกับที่มาทีหลังของการขายเดิม · H3)</summary>
    public sealed record ConfirmOriginalOutcome(bool Approved, string Evidence, string? SupplierInvoiceNumber, DateTime? SupplierTaxInvoiceDate);

    /// <summary>ผู้รับยืนยัน "ได้ต้นฉบับใบกำกับแล้ว" (กระดาษ ⇒ PaperConfirmed · ไฟล์ e-Tax ที่ประทับเวลา ⇒ EtaxVerified — ฝ่ายค้าน H2: ค่านี้มาจากคนเท่านั้น) ·
    /// ฝ่ายค้าน M2: ตรวจต้นทางสดก่อน (ผู้ขายยกเลิกใบ/ยกเลิก e-Tax ⇒ ปฏิเสธพร้อมเหตุ + ติด SourceVoided) · audit · ผู้เรียกห่อธุรกรรมเดียวกับ
    /// <c>CompleteSupplierTaxInvoiceAsync</c> (ฝ่ายค้าน M3 — ล้มทางใดไม่มีอะไรเปลี่ยน)</summary>
    public async Task<ConfirmOriginalOutcome> ConfirmPartnerOriginalAsync(Guid companyId, Guid documentId, Guid userId, bool stampedEtax = false)
    {
        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted && d.PartnerSourceLinkId != null)
            .Select(d => new { d.PartnerInputVatEvidence, d.Status, d.PartnerSourceLinkId })
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("ไม่พบใบซื้อจากคู่ค้า");
        if (PartnerInputVatEvidence.ConfirmProblem(doc.PartnerInputVatEvidence) is string problem)
            throw new BusinessRuleException(problem, PartnerExchangeRules.RuleCode, 409);
        // ฝ่ายค้าน LOW: "ได้ e-Tax ที่ประทับเวลา" เฉพาะใบที่ไฟล์ e-Tax ที่ลงลายมือชื่อของต้นทางถึงผู้รับแล้ว (ไม่งั้น 409 — ไม่ให้ตั้ง EtaxVerified ลอย ๆ)
        if (stampedEtax && PartnerInputVatEvidence.StampedEtaxProblem(doc.PartnerInputVatEvidence) is string stampedProblem)
            throw new BusinessRuleException(stampedProblem, PartnerExchangeRules.RuleCode, 409);

        // ฝ่ายค้าน M2: ต้นทางยังมีผลไหม (ใบของผู้ขาย + e-Tax ของใบนั้น) — ตรวจสดทุกใบในสายเดียวกัน
        var source = await PartnerSourceCheckAsync(companyId, documentId);
        if (source.Voided)
        {
            await _db.Documents.Where(d => d.Id == documentId && d.CompanyId == companyId)
                .ExecuteUpdateAsync(x => x.SetProperty(d => d.PartnerInputVatEvidence, PartnerInputVatEvidence.SourceVoided));
            throw new BusinessRuleException(PartnerInputVatEvidence.SourceVoidedMessage, PartnerExchangeRules.RuleCode, 409);
        }
        var latestTaxNo = source.LatestTaxInvoiceNumber; var latestTaxDate = source.LatestTaxInvoiceDate;

        var target = PartnerInputVatEvidence.Confirmed(stampedEtax);
        var n = await _db.Documents
            .Where(d => d.Id == documentId && d.CompanyId == companyId && d.PartnerInputVatEvidence == doc.PartnerInputVatEvidence)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.PartnerInputVatEvidence, target));
        if (n == 0) throw new BusinessRuleException("สถานะหลักฐานเปลี่ยนระหว่างกด — รีเฟรชแล้วลองใหม่", PartnerExchangeRules.RuleCode, 409);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "Document", EntityId = documentId.ToString(),
            OldValues = JsonSerializer.Serialize(new { inputVatEvidence = doc.PartnerInputVatEvidence }),
            NewValues = JsonSerializer.Serialize(new { inputVatEvidence = target, ruleCode = PartnerExchangeRules.RuleCode,
                legalReference = PartnerExchangeRules.LegalReference }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        var approved = doc.Status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected or DocumentStatus.Voided);
        return new ConfirmOriginalOutcome(approved, target, latestTaxNo, latestTaxDate);
    }

    /// <summary>ฝ่ายค้าน M1 (รอบ 257 ชุด 3): ทางออกของคนจาก <see cref="PartnerInputVatEvidence.AwaitingTaxInvoice"/> — "ได้รับใบกำกับภาษีจากผู้ขายนอกระบบ" ·
    /// ต้องมีเลขที่ + วันที่ใบกำกับ (<see cref="PartnerInputVatEvidence.ExternalTaxInvoiceProblem"/>) · กระดาษ ⇒ PaperConfirmed · ไฟล์ e-Tax ที่ประทับเวลา ⇒ EtaxVerified
    /// (คำยืนยันของคน — ระบบไม่ตรวจกับ ETDA) · ร่าง ⇒ เติมเลข/วันที่ใบกำกับของผู้ขายบนใบ · อนุมัติแล้ว ⇒ ผู้เรียกส่งเลข/วันที่เข้า <c>CompleteSupplierTaxInvoiceAsync</c>
    /// ในธุรกรรมเดียวกัน (ทางเดียวกับ "ได้รับต้นฉบับแล้ว") · ตรวจต้นทางสด (ยกเลิก ⇒ ปฏิเสธ) · compare-and-set · audit</summary>
    public async Task<ConfirmOriginalOutcome> ConfirmExternalTaxInvoiceAsync(Guid companyId, Guid documentId, Guid userId, bool stampedEtax,
        string? supplierInvoiceNumber, DateTime? supplierTaxInvoiceDate)
    {
        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted && d.PartnerSourceLinkId != null)
            ?? throw new KeyNotFoundException("ไม่พบใบซื้อจากคู่ค้า");
        if (PartnerInputVatEvidence.ExternalTaxInvoiceProblem(doc.PartnerInputVatEvidence, supplierInvoiceNumber, supplierTaxInvoiceDate, DateTime.UtcNow) is string problem)
            throw new BusinessRuleException(problem, PartnerExchangeRules.RuleCode, 409);
        // รอบ 257 ปิดท้าย MED-3: "ได้ e-Tax ที่ประทับเวลา" ต้องมีไฟล์ e-Tax ที่ลงลายมือชื่อของต้นทางถึงผู้รับ (ตัวตัดสินเดียวกับทางปกติ) — ใบที่รอใบกำกับไม่มีไฟล์นั้น ⇒ 409
        if (stampedEtax && PartnerInputVatEvidence.StampedEtaxProblem(doc.PartnerInputVatEvidence) is string stampedProblem)
            throw new BusinessRuleException(stampedProblem, PartnerExchangeRules.RuleCode, 409);
        var source = await PartnerSourceCheckAsync(companyId, documentId);
        if (source.Voided)
        {
            await _db.Documents.Where(d => d.Id == documentId && d.CompanyId == companyId)
                .ExecuteUpdateAsync(x => x.SetProperty(d => d.PartnerInputVatEvidence, PartnerInputVatEvidence.SourceVoided));
            throw new BusinessRuleException(PartnerInputVatEvidence.SourceVoidedMessage, PartnerExchangeRules.RuleCode, 409);
        }
        // รอบ 257 ปิดท้าย MED-3: สายนี้มีใบกำกับของผู้ขายผ่านระบบแล้ว (ใบรอใบกำกับเพราะ VAT ไม่ตรงตอนแนบ) ⇒ ทางนี้ต้องผ่านกติกา VAT เดียวกับตอนแนบ —
        // เดิมกดทางนี้ได้เลยโดยไม่แก้ VAT ⇒ ปลดภาษีซื้อบนยอดที่ไม่ตรงใบกำกับ
        var approvedNow = doc.Status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected or DocumentStatus.Voided);
        if (PartnerInputVatEvidence.ExternalVatProblem(source.LatestTaxInvoiceVat, doc.VatAmount, approvedNow) is string vatProblem)
            throw new BusinessRuleException(vatProblem, PartnerExchangeRules.RuleCode, 409);
        var number = supplierInvoiceNumber!.Trim();
        var date = supplierTaxInvoiceDate!.Value.Date;
        var before = doc.PartnerInputVatEvidence;
        var target = PartnerInputVatEvidence.Confirmed(stampedEtax);
        var approved = doc.Status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected or DocumentStatus.Voided);
        var n = await _db.Documents
            .Where(d => d.Id == documentId && d.CompanyId == companyId && d.PartnerInputVatEvidence == before)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.PartnerInputVatEvidence, target));
        if (n == 0) throw new BusinessRuleException("สถานะหลักฐานเปลี่ยนระหว่างกด — รีเฟรชแล้วลองใหม่", PartnerExchangeRules.RuleCode, 409);
        if (!approved)
        {
            // ร่างยังแก้ได้ ⇒ เลข/วันที่ใบกำกับของผู้ขายลงบนใบตรง ๆ (ตัวตรวจ §86/4 ตอนอนุมัติอ่านค่านี้)
            await _db.Documents.Where(d => d.Id == documentId && d.CompanyId == companyId)
                .ExecuteUpdateAsync(x => x
                    .SetProperty(d => d.SupplierInvoiceNumber, number)
                    .SetProperty(d => d.SupplierTaxInvoiceDate, (DateTime?)date)
                    .SetProperty(d => d.HasTaxInvoiceReference, true));
        }
        _db.Entry(doc).State = EntityState.Detached;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "Document", EntityId = documentId.ToString(),
            OldValues = JsonSerializer.Serialize(new { inputVatEvidence = before }),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = PartnerInputVatEvidence.ExternalConfirmedEvent, inputVatEvidence = target, supplierInvoiceNumber = number, supplierTaxInvoiceDate = date,
                kind = stampedEtax ? "StampedEtax" : "Paper", ruleCode = PartnerExchangeRules.RuleCode, legalReference = PartnerExchangeRules.LegalReference,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return new ConfirmOriginalOutcome(approved, target, number, date);
    }
}
