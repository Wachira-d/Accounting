using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 269 ชุดสอง M1 — ช่อง 2 (0%) / ช่อง 3 (ยกเว้น) ของ ภ.พ.30 <b>หลังสร้างรายงาน</b> (ติ๊กแถวออก · ปุ่มดึง · สร้างใหม่คืนติ๊ก · ตัวทบทวน)
/// ใช้ส่วนของแต่ละใบจากตัวตัดสินชุดเดียวกับตอนสร้าง (<see cref="OutputVatOwnership.SalesBoxOwner"/> · <see cref="OutputVatOwnership.ExemptBoxOwner"/> ·
/// <see cref="OutputVatOwnership.AdjustmentNoteCounts"/> ผ่าน loop ของ <c>GenerateVatReport</c>) แล้วรวมด้วย <see cref="Pp30SalesBoxSummary.Totals"/> ตัวเดียว
/// </summary>
public partial class TaxService
{
    private static readonly string[] SalesBoxSummaryCodes = { VatReportLineKind.ZeroRatedSales, VatReportLineKind.ExemptSales };

    private static IEnumerable<Pp30SalesBoxSummary.Row> SalesBoxRows(TaxReport report)
        => report.Lines.Select(l => new Pp30SalesBoxSummary.Row(l.DocumentId, l.Description, l.IncomeTypeCode, l.IsExcluded));

    /// <summary>เขียนแถวสรุปช่อง 2/3 ของรายงานจากส่วนของแต่ละใบ + แถวที่ยังใช้ — ตัวเดียวของตอนสร้างและตอนแก้ ·
    /// <paramref name="persisted"/> = รายงานที่อยู่ในฐานแล้ว (แถวใหม่ผ่าน <c>AddNewChild</c> · แถวที่หมดยอดถูกลบจริง)</summary>
    private void ApplySalesBoxSummary(TaxReport report, IReadOnlyDictionary<Guid, Pp30SalesBoxSummary.Part> parts, bool persisted)
    {
        var totals = Pp30SalesBoxSummary.Totals(SalesBoxRows(report), parts);
        void Put(string code, Pp30FormBox box, decimal amount)
        {
            var existing = report.Lines.Where(l => l.IncomeTypeCode == code).ToList();
            var keep = existing.FirstOrDefault();
            foreach (var extra in existing.Skip(1)) { report.Lines.Remove(extra); if (persisted) _db.Remove(extra); }
            if (amount == 0m)
            {
                if (keep != null) { report.Lines.Remove(keep); if (persisted) _db.Remove(keep); }
                return;
            }
            if (keep != null)
            {
                keep.IncomeAmount = amount;
                keep.Description = Pp30FormBoxes.Label(box);   // แถวรุ่นก่อนที่ป้าย "— ช่อง 7/8" ได้ป้ายทางการเมื่อถูกคำนวณใหม่
                keep.IsExcluded = false;
                keep.UpdatedAt = DateTime.UtcNow;
                return;
            }
            var row = new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = (report.Lines.Count == 0 ? 0 : report.Lines.Max(l => l.LineOrder)) + 1,
                Description = Pp30FormBoxes.Label(box),
                IncomeAmount = amount,
                TaxRate = 0,
                TaxAmount = 0,
                IncomeTypeCode = code,
            };
            if (persisted) _db.AddNewChild(report.Lines, row);
            else report.Lines.Add(row);
        }
        Put(VatReportLineKind.ZeroRatedSales, Pp30FormBox.ZeroRatedSales, totals.ZeroRated);
        Put(VatReportLineKind.ExemptSales, Pp30FormBox.ExemptSales, totals.Exempt);
    }

    /// <summary>ส่วนของแต่ละใบตามกติกาปัจจุบันสำหรับรายงาน <paramref name="report"/> — คำนวณสดผ่าน loop เดียวกับตอนสร้าง (งวดเดียวกัน) +
    /// ใบที่ถูกดึงเข้ามาจากงวดอื่น (ไม่อยู่ใน loop ของงวดนี้) ผ่าน <see cref="LoadSalesBoxPartsOfDocumentsAsync"/></summary>
    internal async Task<Pp30LegacySalesBoxTrace> ComputeSalesBoxPartsAsync(Guid companyId, TaxReport report)
    {
        var trace = new Pp30LegacySalesBoxTrace();
        var start = new DateTime(report.Year, report.Month, 1);
        var probe = new TaxReport { Id = Guid.NewGuid(), CompanyId = companyId, TaxType = TaxType.VAT, Year = report.Year, Month = report.Month };
        await GenerateVatReport(companyId, start, start.AddMonths(1).AddDays(-1), probe, trace);
        var inProbe = probe.Lines.Where(l => l.DocumentId.HasValue).Select(l => l.DocumentId!.Value).ToHashSet();
        var outsiders = report.Lines
            .Where(l => l.DocumentId.HasValue && VatReportLineKind.SideOf(l.IncomeTypeCode) == VatReportSide.Output
                && !TaxReportPullEligibility.IsWarningLineDescription(l.Description))
            .Select(l => l.DocumentId!.Value)
            .Where(id => !inProbe.Contains(id) && !trace.Parts.ContainsKey(id))
            .Distinct().ToList();
        foreach (var (id, part) in await LoadSalesBoxPartsOfDocumentsAsync(companyId, outsiders))
            trace.Parts[id] = part;
        return trace;
    }

    /// <summary>คำนวณแถวสรุปช่อง 2/3 ของรายงานที่บันทึกไว้ใหม่หลังแก้แถว (ไม่ SaveChanges — ผู้เรียกบันทึก)</summary>
    private async Task RecomputeSalesBoxSummaryAsync(Guid companyId, TaxReport report)
    {
        if (report.TaxType != TaxType.VAT) return;
        var trace = await ComputeSalesBoxPartsAsync(companyId, report);
        ApplySalesBoxSummary(report, trace.Parts, persisted: true);
    }

    /// <summary>ส่วนช่อง 2/3 ของใบที่ไม่อยู่ใน loop ของงวด (ใบที่ดึงเข้ามา) — ตัวตัดสินเดียวกับ loop (SalesBoxOwner/ExemptBoxOwner) ไม่เทียบงวด
    /// (แถวอยู่ในรายงานนี้แล้ว) · ใบลดหนี้/เพิ่มหนี้ไม่ถูกดึงเป็นฝั่งขาย ⇒ ไม่นับ · ใบยกเลิก/ยังไม่ออก/ยอดยกมา ⇒ ไม่นับ</summary>
    private async Task<Dictionary<Guid, Pp30SalesBoxSummary.Part>> LoadSalesBoxPartsOfDocumentsAsync(Guid companyId, List<Guid> ids)
    {
        var result = new Dictionary<Guid, Pp30SalesBoxSummary.Part>();
        if (ids.Count == 0) return result;
        var docs = await _db.Documents.AsNoTracking().Include(d => d.Lines)
            .Where(d => d.CompanyId == companyId && ids.Contains(d.Id)
                && !DocumentStatusRules.NotIssued.Contains(d.Status) && d.Status != DocumentStatus.Voided)
            .ToListAsync();
        var invoiceIds = docs.Where(d => d.DocumentType == DocumentType.Invoice).Select(d => d.Id).ToList();
        var superseded = await LoadSupersededInvoiceIdsAsync(companyId, invoiceIds);
        var supersedingExempt = await LoadSupersedingExemptBaseAsync(companyId, superseded.ToList());
        var ownedByReceipt = await LoadInvoiceIdsOwnedByVatReceiptAsync(companyId, invoiceIds);
        var gl21913 = await Load21913NetBySourceAsync(companyId,
            invoiceIds.Concat(docs.Where(d => d.IsDeposit).Select(d => d.Id)).Distinct().ToList());
        var related = await LoadRelatedDocInfosAsync(companyId,
            docs.Where(d => d.RelatedDocumentId.HasValue).Select(d => d.RelatedDocumentId!.Value).Distinct().ToList());
        foreach (var d in docs)
        {
            if (d.DocumentType is DocumentType.CreditNote or DocumentType.DebitNote || OutputVatOwnership.IsOutsidePp30(d)) continue;
            var hasRel = d.RelatedDocumentId.HasValue && related.ContainsKey(d.RelatedDocumentId.Value);
            var rel = hasRel ? related[d.RelatedDocumentId!.Value] : default;
            var facts = OutputVatOwnership.FactsOf(d, superseded.Contains(d.Id), ownedByReceipt.Contains(d.Id), gl21913.GetValueOrDefault(d.Id),
                hasRel ? rel.Type : null, hasRel ? rel.OutputVatDueAt : null, hasRel && rel.IsRollupBillingNote,
                superseded.Contains(d.Id) ? supersedingExempt.GetValueOrDefault(d.Id) : (decimal?)null);
            var split = Pp30SalesClassifier.Split(d.Lines.Where(l => !l.IsDeleted).Select(Pp30LineOf));
            var exOwner = OutputVatOwnership.ExemptBoxOwner(facts);
            var zero = OutputVatOwnership.SalesBoxOwner(facts).Owns ? DocumentFx.ToBaht(split.ZeroRatedBase, d.ExchangeRate) : 0m;
            var exempt = !exOwner.Owns ? 0m
                : exOwner.RuleCode == OutputVatOwnership.RuleSupersededExemptRemainder
                    ? OutputVatOwnership.ExemptRemainder(DocumentFx.ToBaht(split.ExemptBase, d.ExchangeRate),
                        DocumentFx.ToBaht(supersedingExempt.GetValueOrDefault(d.Id), d.ExchangeRate))
                    : DocumentFx.ToBaht(split.ExemptBase, d.ExchangeRate);
            if (zero != 0m || exempt != 0m) result[d.Id] = new Pp30SalesBoxSummary.Part(zero, exempt);
        }
        return result;
    }
}
