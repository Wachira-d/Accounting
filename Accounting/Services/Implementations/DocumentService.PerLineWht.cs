using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// ทีม F — หัก ณ ที่จ่ายตามรายการ + ฐานบาทตามเอกสาร (<c>docs/PER_LINE_WHT_DESIGN.md</c>) · ส่วนของ DocumentService ที่แตะ<b>อัตรา/ที่มา/ฐานบาท</b>
/// ของเอกสารตอนสร้าง — ตัวตัดสินเดียว <see cref="DocumentFx.Decide"/> (pure + เทสต์)
/// </summary>
public partial class DocumentService
{
    /// <summary>
    /// ตั้งอัตรา/ที่มา/ฐานบาทของเอกสารใหม่ <b>หลังคิดยอดบรรทัดแล้ว</b> (ต้องรู้ <c>doc.SubTotal</c>) · ใบที่ใช้อัตราเดียวกับใบต้นทาง (ใบลด/เพิ่มหนี้ ·
    /// ใบที่แปลงมา) ⇒ อัตราไม่ถูกเปลี่ยน (ตัดหนี้ต้องเท่ายอดที่ตั้ง) · ใบต้นทางมียอดบาทตามเอกสารและแปลงมาเต็มจำนวน ⇒ สืบทอดฐานบาท (เอกสารลูกสืบทอด — กฎ #4 A)
    /// · ผลผิด ⇒ <see cref="BusinessRuleException"/> (400 ข้อความไทย)
    /// </summary>
    private async Task ApplyDocumentFxDecisionAsync(Guid companyId, Document doc, CreateDocumentRequest request)
    {
        var lockedToSource = false;
        string? sourceRateSource = null;
        decimal? inheritedThbBase = null;
        if (doc.RelatedDocumentId is Guid srcId
            && !string.Equals(doc.Currency, "THB", StringComparison.OrdinalIgnoreCase))
        {
            var src = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == srcId && d.CompanyId == companyId)
                .Select(d => new { d.Currency, d.ExchangeRate, d.ExchangeRateSource, d.DocumentThbBase, d.SubTotal })
                .FirstOrDefaultAsync();
            if (src != null && string.Equals(src.Currency, doc.Currency, StringComparison.OrdinalIgnoreCase)
                && src.ExchangeRate > 0m && src.ExchangeRate == doc.ExchangeRate)
            {
                lockedToSource = true;
                sourceRateSource = src.ExchangeRateSource;
                if (src.DocumentThbBase is > 0m && src.SubTotal == doc.SubTotal)
                    inheritedThbBase = src.DocumentThbBase;
            }
        }
        var decision = DocumentFx.Decide(doc.Currency, doc.ExchangeRate, request.ExchangeRate.HasValue, request.ExchangeRateSource,
            request.DocumentThbBase, doc.SubTotal, lockedToSource, sourceRateSource, inheritedThbBase);
        if (decision.Error is string err)
            throw new BusinessRuleException(err, PerLineWht.RuleCode);
        doc.ExchangeRate = decision.Rate;
        doc.ExchangeRateSource = decision.Source;
        doc.DocumentThbBase = decision.ThbBase;
    }
}
