using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// คำตัดสินข้อ 139 (2026-10-08): ผูกใบแจ้งหนี้/ใบกำกับที่สร้างแยกเข้ากับใบเสนอราคาภายหลัง — ได้ผลเหมือนแปลงมา
/// (ความคืบหน้า · ด่านยอดสะสม · ด่านรายได้ซ้ำ นับใบนี้) โดย<b>ไม่แตะเนื้อกระดาษ</b> ไม่ลง JE ไม่ขยับสต็อก/ยอดคงค้าง/สถานะ
///
/// <para>เขียนแค่ <c>RelatedDocumentId</c> + <c>SourceLineId</c> (ตัวนับความคืบหน้าอ่านสองช่องนี้เท่านั้น) + ร่องรอย
/// <c>SourceLinkedAt/By</c> + แถว audit · ไม่แตะ <c>Reference</c> (พิมพ์ลงกระดาษเป็น "อ้างอิง" — ใบที่ออกแล้วห้ามเปลี่ยน §86/4)</para>
/// </summary>
public partial class DocumentService
{
    private static DocumentLinkPolicy.ChildFacts LinkFacts(Document child) => new(
        child.DocumentType, child.Status, child.RelatedDocumentId.HasValue, child.IsDeposit, child.DepositBaseDeducted,
        child.ReplacesDocumentId.HasValue, child.Lines.Any(l => l.SourceLineId.HasValue));

    private static List<LinkSourceLine> LinkLines(IEnumerable<DocumentLine> lines) => lines
        .OrderBy(l => l.LineOrder)
        .Select(l => new LinkSourceLine(l.Id, l.LineOrder, l.Description, l.Quantity, l.Amount))
        .ToList();

    private static List<DocumentLinkPolicy.LineKey> LinkKeys(IEnumerable<DocumentLine> lines) => lines
        .Select(l => new DocumentLinkPolicy.LineKey(l.Id, l.LineOrder, l.ProductCode, l.Description)).ToList();

    public async Task<LinkCandidatesResponse> GetLinkCandidatesAsync(Guid companyId, Guid childId)
    {
        var child = await _db.Documents.AsNoTracking().Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == childId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var childLines = LinkLines(child.Lines);
        var blocked = DocumentLinkPolicy.ChildBlockReason(LinkFacts(child));
        if (blocked != null) return new LinkCandidatesResponse(blocked, childLines, new());

        // ตัวกรองชุดเดียวกับด่านตอนผูกจริง (DocumentLinkPolicy.SourceBlockReason) — คู่ค้า/สกุลเงินเดียวกัน · ไม่ถูกยกเลิก
        var sourceTypes = DocumentLinkPolicy.SourceTypes;
        var sources = await _db.Documents.AsNoTracking().Include(d => d.Lines)
            .Where(d => d.CompanyId == companyId && sourceTypes.Contains(d.DocumentType)
                && d.ContactId == child.ContactId && d.Currency == child.Currency
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
            .OrderByDescending(d => d.DocumentDate).ThenByDescending(d => d.CreatedAt)
            .Take(30)
            .ToListAsync();

        var candidates = new List<LinkCandidate>();
        foreach (var s in sources)
        {
            var (_, billed, _) = await ComputeConsumedBaseAsync(companyId, s.Lines.Select(l => l.Id).ToList());
            var map = DocumentLinkPolicy.SuggestLineMap(LinkKeys(child.Lines), LinkKeys(s.Lines))
                .Select(m => new LinkLineSuggestion(m.ChildLineId, m.SourceLineId)).ToList();
            candidates.Add(new LinkCandidate(s.Id, s.DocumentNumber, s.DocumentDate,
                s.Lines.Sum(l => l.Amount), billed, LinkLines(s.Lines), map));
        }
        return new LinkCandidatesResponse(candidates.Count == 0
            ? "ไม่พบใบเสนอราคาของคู่ค้ารายนี้ (สกุลเงินเดียวกัน · ยังไม่ยกเลิก) ให้ผูก"
            : null, childLines, candidates);
    }

    public async Task<DocumentResponse> LinkToSourceAsync(Guid companyId, Guid childId, LinkSourceRequest request, string actor)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines == null || request.Lines.Count == 0)
            throw new BusinessRuleException("เลือกอย่างน้อย 1 บรรทัดที่ตรงกับรายการในใบเสนอราคา", DocumentLinkPolicy.RuleCode);

        await using var tx = await _db.Database.BeginTransactionAsync();
        // ล็อกใบต้นทาง — กันผูก/แปลงพร้อมกันจนยอดสะสมเกินโดยไม่มีใครเห็น
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE",
            request.SourceDocumentId, companyId);

        var child = await _db.Documents.Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == childId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        var source = await _db.Documents.Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == request.SourceDocumentId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเสนอราคาที่เลือก");

        var childBlock = DocumentLinkPolicy.ChildBlockReason(LinkFacts(child));
        if (childBlock != null) throw new BusinessRuleException(childBlock, DocumentLinkPolicy.RuleCode);
        var srcBlock = DocumentLinkPolicy.SourceBlockReason(source.DocumentType, source.Status,
            source.ContactId == child.ContactId, string.Equals(source.Currency, child.Currency, StringComparison.OrdinalIgnoreCase));
        if (srcBlock != null) throw new BusinessRuleException(srcBlock, DocumentLinkPolicy.RuleCode);

        // ด่านชุดเดียวกับการแปลงบางส่วน: คู่ชนิดที่แปลงได้ · ใบต้นทางถูกยกเลิก · วงกลม · รายได้ซ้ำ (ลูกที่ด่านยอดมองไม่เห็น)
        await ValidateConversionAsync(source, child.DocumentType, companyId, partialBillingSplit: true);

        var childLineById = child.Lines.ToDictionary(l => l.Id);
        var sourceLineIds = source.Lines.Select(l => l.Id).ToHashSet();
        var map = new Dictionary<Guid, Guid>();
        foreach (var m in request.Lines)
        {
            if (!childLineById.ContainsKey(m.ChildLineId))
                throw new BusinessRuleException("บรรทัดที่เลือกไม่ใช่ของเอกสารนี้", DocumentLinkPolicy.RuleCode);
            if (!sourceLineIds.Contains(m.SourceLineId))
                throw new BusinessRuleException("บรรทัดต้นทางที่เลือกไม่ใช่ของใบเสนอราคานี้", DocumentLinkPolicy.RuleCode);
            map[m.ChildLineId] = m.SourceLineId;
        }

        // ยอดสะสมเกินใบต้นทาง ⇒ ถามยืนยันครั้งเดียว (ตัวตัดสินเดียวกับการแปลงบางส่วน · ข้อ 138)
        var consumed = await ComputeConsumedBaseAsync(companyId, sourceLineIds.ToList());
        var linkingNow = map.Keys.Sum(id => childLineById[id].Amount);
        var sourceBase = source.Lines.Sum(l => l.Amount);
        string? overNote = null;
        if (PartialConvertPolicy.IsOverAmount(consumed.Billing, linkingNow, sourceBase, consumed.LineCount + map.Count))
        {
            var msg = PartialConvertPolicy.OverAmountMessage(source.DocumentNumber, "วางบิล", consumed.Billing, linkingNow, sourceBase);
            if (!request.ConfirmOverSourceAmount)
                throw new BusinessRuleException(msg, PartialConvertPolicy.OverAmountRule, 422);
            overNote = msg + " (ผู้ใช้ยืนยันแล้ว)";
        }

        var before = new { child.RelatedDocumentId, Lines = child.Lines.Select(l => new { l.Id, l.SourceLineId }).ToList() };
        child.RelatedDocumentId = source.Id;
        foreach (var (lineId, srcLineId) in map) childLineById[lineId].SourceLineId = srcLineId;
        child.SourceLinkedAt = DateTime.UtcNow;
        child.SourceLinkedBy = actor;
        if (overNote != null)
            child.InternalNotes = string.IsNullOrWhiteSpace(child.InternalNotes) ? overNote : child.InternalNotes + "\n" + overNote;
        child.UpdatedAt = DateTime.UtcNow;
        child.UpdatedBy = actor;

        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            Action = AuditAction.Update,
            EntityType = "Document",
            EntityId = child.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "LinkToSource",
                ruleCode = DocumentLinkPolicy.RuleCode,
                documentNumber = child.DocumentNumber,
                source = source.DocumentNumber,
                lines = map.Select(kv => new { childLineId = kv.Key, sourceLineId = kv.Value }),
                linkingAmount = linkingNow,
                overNote,
                by = actor,
            }),
        });
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetDocumentAsync(companyId, child.Id);
    }

    public async Task<DocumentResponse> UnlinkSourceAsync(Guid companyId, Guid childId, string actor)
    {
        var child = await _db.Documents.Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == childId && d.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        // ยกเลิกได้เฉพาะการผูกภายหลัง — ใบที่แปลงมาจริงยกเลิกไม่ได้ (ไม่งั้นยกเลิกผูกแล้วแปลงซ้ำ = สองใบสำหรับบิลเดียว)
        if (child.SourceLinkedAt == null || child.RelatedDocumentId == null)
            throw new BusinessRuleException("ใบนี้ไม่ได้ถูกผูกภายหลัง (แปลงมาจากใบต้นทางโดยตรง) — ยกเลิกการผูกไม่ได้", DocumentLinkPolicy.RuleCode);

        var parentId = child.RelatedDocumentId.Value;
        var parentLineIds = await _db.DocumentLines.AsNoTracking()
            .Where(l => l.DocumentId == parentId && l.Document.CompanyId == companyId)
            .Select(l => l.Id).ToListAsync();
        var before = new { child.RelatedDocumentId, child.SourceLinkedAt, child.SourceLinkedBy };
        foreach (var l in child.Lines.Where(l => l.SourceLineId.HasValue && parentLineIds.Contains(l.SourceLineId.Value)))
            l.SourceLineId = null;
        child.RelatedDocumentId = null;
        child.SourceLinkedAt = null;
        child.SourceLinkedBy = null;
        child.UpdatedAt = DateTime.UtcNow;
        child.UpdatedBy = actor;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            Action = AuditAction.Update,
            EntityType = "Document",
            EntityId = child.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "UnlinkSource",
                ruleCode = DocumentLinkPolicy.RuleCode,
                documentNumber = child.DocumentNumber,
                by = actor,
            }),
        });
        await _db.SaveChangesAsync();
        return await GetDocumentAsync(companyId, child.Id);
    }
}
