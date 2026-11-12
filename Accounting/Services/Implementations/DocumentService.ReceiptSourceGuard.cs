using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 266 แพ็ก A — ด่านเซิร์ฟเวอร์ของ "ใบเสร็จอ้างใบต้นทาง" (G-1 · คำตัดสิน 266-1-13) + ตั้งใบต้นทางของใบเสร็จร่างผ่านฟอร์มแก้ไข (266-1-2)
///
/// <para>ที่มา: ใบเสร็จที่สร้างเองเลือก "รับชำระใบค้างของลูกค้า" ได้ แต่เงื่อนไข "ผู้ติดต่อเดียวกัน" อยู่แค่ในตัวกรองของหน้าจอ (<c>?contactId=</c>) ·
/// <c>CreateDocumentAsync</c> ตั้ง <c>RelatedDocumentId</c> ตามที่ส่งมา · อนุมัติแล้ว <c>ApplySourceDocumentAdjustmentsAsync</c> ตัดยอดค้างโดยไม่เทียบผู้ติดต่อ
/// ⇒ API/คำขอตรงผูกใบเสร็จของลูกค้า ข เข้าใบแจ้งหนี้ของลูกค้า ก ได้ (ลูกหนี้ ก ถูกตัดด้วยเงินของ ข · กระดาษใบเสร็จระบุผู้ซื้อคนละรายกับใบที่อ้าง)</para>
///
/// <para>จุดเรียก (ล็อกใน <c>tools/required_call_site_check.py</c> กติกา R266): สร้าง (<c>CreateDocumentAsync</c>) · แก้ไข (<c>UpdateDocumentAsync</c> —
/// ทั้งเปลี่ยนใบต้นทางและเปลี่ยนผู้ติดต่อของใบที่ผูกอยู่) · อนุมัติ (<c>ApproveDocumentAsync</c> ก่อนลง JE) · ตัวตัดสินเดียว <see cref="ReceiptLinkPolicy.SourceRefusal"/>
/// + <see cref="ReceiptLinkPolicy.SourceStateRefusal"/> (ฝ่ายค้าน M2 — ด่านฝั่งใบปลายทางชุดเดียวกับปุ่มผูก)</para>
/// </summary>
public partial class DocumentService
{
    /// <summary>ใบเสร็จ/ใบสำคัญรับที่อ้างใบต้นทาง ต้องเป็นผู้ติดต่อเดียวกัน (+ สกุลเดียวกัน · ชนิดที่แปลงมาเป็นใบเสร็จได้) — ไม่ผ่าน = throw (ไม่มีอะไรถูกบันทึก) ·
    /// ใบเสร็จที่สร้างเอง (ไม่ใช่ของการชำระ/มัดจำ) ที่อ้างใบตั้งลูกหนี้: ตรวจสภาพใบต้นทางซ้ำด้วย (ยกเลิก/ออกใบแทน · ยังไม่อนุมัติ · ใบกำกับขายเงินสด) และตอนอนุมัติ
    /// ตรวจ "รับบางส่วนของใบแจ้งหนี้ที่มี VAT" (M2)</summary>
    /// <param name="checkCurrency">false = ใบเสร็จอัตโนมัติของการชำระ (สกุลอยู่ที่การชำระ)</param>
    /// <param name="atApproval">true = ตรวจยอดที่จะปิดหนี้ด้วย (ยอดใบร่างแก้ไม่ได้อีกแล้ว)</param>
    private async Task GuardReceiptSourceAsync(Guid companyId, Document doc, bool checkCurrency, bool atApproval = false)
    {
        if (!ReceiptLinkPolicy.IsReceiptType(doc.DocumentType) || doc.RelatedDocumentId is not Guid sourceId) return;
        var manual = !doc.IsSettlementReceipt && doc.SettlementPaymentId == null && !doc.IsDeposit;
        var source = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == sourceId && d.CompanyId == companyId);
        // ฝ่ายค้าน L4: ใบต้นทางถูกลบ/ไม่พบ — บอกทางล้างให้ตรงกับชนิดของใบ (ใบร่างที่สร้างเองล้างในฟอร์มได้ · ใบของการชำระ/มัดจำล้างไม่ได้ ต้องลบใบร่าง)
        if (source == null || source.IsDeleted)
            throw new BusinessRuleException("ไม่พบใบต้นทางที่ใบเสร็จนี้อ้างในบริษัทนี้ (อาจถูกลบแล้ว) — "
                + (manual
                    ? "เปิดแก้ไขใบร่างแล้วเลือกใบค้างของผู้ติดต่อรายนี้ใหม่ หรือเลือก \"ใบเสร็จอิสระ\" เพื่อล้างใบต้นทาง"
                    : "ใบนี้เป็นของการรับชำระ/มัดจำ จึงเปลี่ยนใบต้นทางไม่ได้ — ลบใบร่างนี้แล้วบันทึกรับชำระที่ใบที่ถูกต้อง"),
                ReceiptLinkPolicy.SourceCode, 404);
        var sameEntityOtherBranch = source.ContactId != doc.ContactId && doc.ContactId != Guid.Empty
            && (await ContactTaxBranchKey.SameEntityIdsAsync(_db.Contacts.AsNoTracking(), companyId, doc.ContactId)).Contains(source.ContactId);
        var refusal = ReceiptLinkPolicy.SourceRefusal(doc.DocumentType, doc.ContactId, doc.Currency,
            source.DocumentType, source.DocumentNumber, source.ContactId, source.Currency,
            sourceTypeAllowed: GetPredecessorTypes(doc.DocumentType).Contains(source.DocumentType),
            sameTaxIdOtherBranch: sameEntityOtherBranch, checkCurrency: checkCurrency);
        if (refusal != null)
            throw new BusinessRuleException(refusal, ReceiptLinkPolicy.SourceCode, 409);
        if (manual && ReceiptLinkPolicy.SourceStateRefusal(ReceiptLinkFacts.Invoice(source),
                atApproval ? ForeignServiceVat.PayeeAmount(doc, false) : null) is { } state)
            throw new BusinessRuleException($"ใบเสร็จ {doc.DocumentNumber}: {state.Message}", state.Code, 409);
    }

    /// <summary>ฝ่ายค้าน M1: ใต้ล็อกแถวของการอนุมัติ — ใบต้นทาง/ผู้ติดต่อที่อ่านไว้ก่อนล็อกต้องยังตรงกับฐาน (ฟอร์มแก้ไขอีกคำขอเปลี่ยนไประหว่างนั้น ⇒ JE จะลงตามค่าเก่า
    /// ขณะที่ใบถือค่าใหม่) — ไม่ตรง = ปฏิเสธให้โหลดใหม่</summary>
    private async Task EnsureReceiptSourceUnchangedUnderLockAsync(Guid companyId, Document doc)
    {
        if (!ReceiptLinkPolicy.IsReceiptType(doc.DocumentType)) return;
        var live = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == doc.Id && d.CompanyId == companyId)
            .Select(d => new { d.RelatedDocumentId, d.ContactId }).FirstAsync();
        if (live.RelatedDocumentId != doc.RelatedDocumentId || live.ContactId != doc.ContactId)
            throw new BusinessRuleException("ใบเสร็จนี้ถูกแก้ไข (ใบต้นทาง/ผู้ติดต่อ) โดยผู้ใช้อื่นระหว่างอนุมัติ — โหลดหน้าใหม่แล้วอนุมัติอีกครั้ง",
                ReceiptLinkPolicy.StaleCode, 409);
    }

    /// <summary>
    /// ฟอร์มแก้ไขของใบเสร็จร่าง: ตั้ง/เปลี่ยน/ล้างใบต้นทาง (<paramref name="requested"/>: null = ไม่แตะ · <c>Guid.Empty</c> = ล้าง) ผ่านตัวตัดสินเดียวกับปุ่มผูก
    /// (<see cref="ReceiptLinkPolicy.Decide"/> เส้น SetDraftSource) · ใบที่ไม่ใช่ใบเสร็จร่างที่สร้างเอง ⇒ ส่งค่าใหม่มา = ปฏิเสธดัง (ห้าม silent no-op) ·
    /// แล้วตรวจ G-1 ซ้ำเสมอ (แก้ผู้ติดต่อของใบที่ผูกอยู่ = ทางเลี่ยงด่านเดิม)
    /// <para>ฝ่ายค้าน M1: เมื่อค่าจะเปลี่ยน ผู้เรียกเปิดธุรกรรมครอบถึง <c>SaveChanges</c> แล้วเมธอดนี้ล็อกแถวใบเสร็จ + ใบต้นทางใหม่ (เรียง Id) และจัดชนิดจาก
    /// <b>สถานะสดใต้ล็อก</b> (ไม่ใช่ค่าที่โหลดไว้ก่อน) — แข่งกับการอนุมัติไม่ได้ใบที่ออกเลขแล้วถูกเขียนใบต้นทาง (266-1-2) · M5: แถว audit มีผู้กระทำ</para>
    /// </summary>
    private async Task ApplyReceiptSourceOnUpdateAsync(Guid companyId, Document doc, Guid? requested, string? actor)
    {
        if (requested is Guid req)
        {
            var target = req == Guid.Empty ? (Guid?)null : req;
            if (target != doc.RelatedDocumentId)
            {
                foreach (var lid in new[] { doc.Id, target ?? doc.Id }.Distinct().OrderBy(x => x))
                    await _db.Database.ExecuteSqlRawAsync(
                        "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", lid, companyId);
                var live = await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == doc.Id && d.CompanyId == companyId)
                    .Select(d => new { d.Status, d.RelatedDocumentId }).FirstAsync();
                // ตัวจัดชนิดเดียวกับปุ่มผูก/ช่องฟอร์ม (DocumentResponse.CanChangeReceiptSource): ใบเสร็จร่างที่สร้างเองเท่านั้น — จากสถานะสดใต้ล็อก
                var facts = ReceiptLinkFacts.Source(doc) with { Status = live.Status };
                if (ReceiptLinkPolicy.Classify(facts) != ReceiptLinkKind.DraftReceipt || live.RelatedDocumentId != doc.RelatedDocumentId)
                    throw new BusinessRuleException(ReceiptLinkPolicy.SourceLockedMessage(doc.DocumentNumber), ReceiptLinkPolicy.SourceCode, 409);
                if (target is Guid invoiceId)
                {
                    var inv = await _db.Documents.AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == invoiceId && d.CompanyId == companyId)
                        ?? throw new BusinessRuleException("ไม่พบใบต้นทางที่เลือกในบริษัทนี้", ReceiptLinkPolicy.SourceCode, 404);
                    var sameEntity = inv.ContactId != doc.ContactId && doc.ContactId != Guid.Empty
                        && (await ContactTaxBranchKey.SameEntityIdsAsync(_db.Contacts.AsNoTracking(), companyId, doc.ContactId)).Contains(inv.ContactId);
                    var verdict = ReceiptLinkPolicy.Decide(facts, ReceiptLinkFacts.Invoice(inv),
                        new ReceiptLinkContext(doc.TotalAmount, SameTaxIdOtherBranch: sameEntity));
                    if (!verdict.Allowed || verdict.Route != ReceiptLinkRoute.SetDraftSource)
                        throw new BusinessRuleException((verdict.Message ?? "") + (verdict.WayForward is { } w ? " · " + w : ""), verdict.RuleCode, 409);
                }
                var before = doc.RelatedDocumentId;
                doc.RelatedDocumentId = target;
                if (!string.IsNullOrWhiteSpace(actor)) doc.UpdatedBy = actor;
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = Guid.TryParse(actor, out var actorId) ? actorId : null,
                    Action = AuditAction.Update,
                    EntityType = "Document",
                    EntityId = doc.Id.ToString(),
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new { RelatedDocumentId = before }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = target == null ? "ClearDraftReceiptSource" : "SetDraftReceiptSource",
                        RuleCode = ReceiptLinkPolicy.RuleCode,
                        ReceiptLinkPolicy.LegalReference,
                        RelatedDocumentId = target,
                        doc.DocumentNumber,
                        via = "UpdateDocument",
                        by = actor,
                    }),
                });
            }
        }
        await GuardReceiptSourceAsync(companyId, doc, checkCurrency: !doc.IsSettlementReceipt);
    }
}

/// <summary>ตัวแปลง entity → ข้อเท็จจริงของ <see cref="ReceiptLinkPolicy"/> (ตัวเดียว — ฟอร์มแก้ไข · บริการผูก · เทสต์ใช้ร่วมกัน)</summary>
public static class ReceiptLinkFacts
{
    public static ReceiptLinkSourceFacts Source(Document d, bool fromUnallocatedCredit = false, string? previousSourceNumber = null) => new(
        IsBareCredit: false, d.DocumentType, d.Status, d.IsDeleted, d.ReplacedByDocumentId.HasValue, d.IsDeposit,
        d.IsSettlementReceipt, d.SettlementPaymentId.HasValue, d.RelatedDocumentId, d.IssuedAsCashReceipt,
        d.ContactId, d.Currency, d.ExchangeRate, d.TotalAmount, d.DocumentNumber, fromUnallocatedCredit, previousSourceNumber);

    public static ReceiptLinkSourceFacts Credit(Payment p) => new(
        IsBareCredit: true, DocumentType.Receipt, DocumentStatus.Approved, p.IsDeleted, false, false, false, true, null, false,
        p.ContactId ?? Guid.Empty, "THB", 1m, p.UnappliedAmount, p.PaymentNumber);

    public static ReceiptLinkInvoiceFacts Invoice(Document d) => new(
        d.Id, d.DocumentType, d.Status, d.IsDeleted, d.ReplacedByDocumentId.HasValue, d.ContactId, d.Currency, d.ExchangeRate,
        d.BalanceDue, d.VatAmount, ServesAsReceipt: d.DocumentType == DocumentType.TaxInvoice && d.IssuedAsCashReceipt, d.DocumentNumber);
}
