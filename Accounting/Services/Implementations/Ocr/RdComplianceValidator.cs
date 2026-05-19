using System.Text.Json;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.DTOs.Ocr;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Post-OCR validation layer enforcing Thai Revenue Department rules
/// on uploaded tax invoices / receipts. Surfaces issues as both
/// OcrValidationLog rows (audit) and a JSON blob on
/// Document.RdComplianceIssuesJson (fast UI lookup).
///
/// Validation rules (Task 5 of ERP upgrade):
///   1. "ใบกำกับภาษี" / "Tax Invoice" keyword present in raw OCR text.
///   2. Seller Tax ID populated and validated as Thai 13-digit format.
///   3. Buyer Tax ID populated (mandatory on tax invoices ≥ 1,000 THB
///      after-VAT — RD rule).
///   4. Branch code populated (00000 = HQ acceptable).
///   5. Document date present and not in the future.
///   6. VAT breakdown balances: Subtotal × 0.07 ≈ VatAmount (±1 baht).
///   7. Tenant cross-check: extracted BuyerTaxId matches this company's
///      Company.TaxId, otherwise this is an invoice for a DIFFERENT
///      legal entity. Hard warning.
///
/// Severities follow the OcrValidationLog convention:
///   * Error   — blocks accept (operator must fix or override)
///   * Warning — surfaces in UI but allows approval
///   * Info    — informational only
/// </summary>
public class RdComplianceValidator
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<RdComplianceValidator> _logger;

    public RdComplianceValidator(AccountingDbContext db, ILogger<RdComplianceValidator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record RuleResult(string RuleCode, bool IsValid, string Severity, string? Message, string? FieldValue);

    public async Task<RdComplianceStatus> EvaluateAndPersistAsync(
        Guid companyId, Guid documentId, OcrResultResponse ocrResult, string? rawOcrText)
    {
        var company = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == companyId)
            ?? throw new KeyNotFoundException("Company not found");

        var doc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId);
        if (doc == null) throw new KeyNotFoundException("Document not found");

        var results = RunRules(ocrResult, rawOcrText, company);

        // Persist one OcrValidationLog row per rule; replaces any previous
        // results for this document (each evaluation supersedes the last).
        var existing = await _db.OcrValidationLogs
            .Where(o => o.DocumentId == documentId && o.CompanyId == companyId)
            .ToListAsync();
        _db.OcrValidationLogs.RemoveRange(existing);
        foreach (var r in results)
        {
            _db.OcrValidationLogs.Add(new OcrValidationLog
            {
                CompanyId = companyId,
                DocumentId = documentId,
                RuleCode = r.RuleCode,
                IsValid = r.IsValid,
                Severity = r.Severity,
                Message = r.Message,
                FieldValue = r.FieldValue,
            });
        }

        var hasError = results.Any(r => !r.IsValid && r.Severity == "Error");
        var hasWarning = results.Any(r => !r.IsValid && r.Severity == "Warning");

        var status = hasError ? RdComplianceStatus.Failed
            : hasWarning ? RdComplianceStatus.Warning
            : RdComplianceStatus.Valid;

        doc.RdComplianceStatus = status;
        doc.RdComplianceIssuesJson = JsonSerializer.Serialize(
            results.Where(r => !r.IsValid).Select(r => new {
                rule = r.RuleCode, severity = r.Severity, message = r.Message
            }));
        doc.OcrConfidenceScore = (decimal)ocrResult.Confidence;
        doc.OcrTenantMismatchFlag = results.Any(r => r.RuleCode == "TENANT_BUYER_MISMATCH" && !r.IsValid);

        await _db.SaveChangesAsync();
        return status;
    }

    private static List<RuleResult> RunRules(OcrResultResponse o, string? rawText, Company company)
    {
        var results = new List<RuleResult>();
        var raw = (rawText ?? "").ToLowerInvariant();

        // Rule 1: Tax Invoice keyword
        bool hasTaxKw = raw.Contains("ใบกำกับภาษี") || raw.Contains("tax invoice");
        results.Add(new("TAX_INVOICE_KEYWORD", hasTaxKw, hasTaxKw ? "Info" : "Warning",
            hasTaxKw ? "พบคำว่า 'ใบกำกับภาษี' / 'Tax Invoice' ในเอกสาร"
                     : "ไม่พบคำว่า 'ใบกำกับภาษี' / 'Tax Invoice' — อาจไม่ใช่ใบกำกับภาษีตามมาตรฐาน RD",
            null));

        // Rule 2: Seller Tax ID format
        var sellerOk = IsThaiTaxId(o.ExtractedVendorTaxId);
        results.Add(new("SELLER_TAX_ID_FORMAT", sellerOk,
            sellerOk ? "Info" : "Error",
            sellerOk ? "เลขผู้เสียภาษีผู้ขายถูกต้อง"
                     : $"เลขผู้เสียภาษีผู้ขายไม่ถูกต้อง (ต้องเป็น 13 หลัก) — ที่ได้: '{o.ExtractedVendorTaxId}'",
            o.ExtractedVendorTaxId));

        // Rule 3: Buyer Tax ID (required when total >= 1000)
        var requiresBuyerTaxId = (o.ExtractedTotalAmount ?? 0) >= 1000m;
        var buyerOk = !requiresBuyerTaxId || IsThaiTaxId(o.BuyerTaxId);
        results.Add(new("BUYER_TAX_ID_FORMAT", buyerOk,
            buyerOk ? "Info" : "Warning",
            buyerOk ? "เลขผู้เสียภาษีผู้ซื้อถูกต้อง / ไม่ต้องระบุ"
                    : $"ต้องระบุเลขผู้เสียภาษีผู้ซื้อ (ใบกำกับภาษีเต็มรูป) — ที่ได้: '{o.BuyerTaxId}'",
            o.BuyerTaxId));

        // Rule 4: Branch code present
        // (branch is part of company info or "00000" — at least one side
        //  should carry it for full RD compliance; warn if absent on both)
        // OcrExtractedData doesn't currently carry branch — surface as Info.
        results.Add(new("BRANCH_CODE_PRESENT", true, "Info",
            "Branch code อยู่ใน metadata บริษัท (ระบบใช้ค่า " + (company.BranchCode ?? "00000") + ")",
            company.BranchCode));

        // Rule 5: Document date present + not in future
        var hasDate = o.ExtractedDate.HasValue;
        var futureDate = hasDate && o.ExtractedDate!.Value > DateTime.UtcNow.Date.AddDays(1);
        results.Add(new("DOCUMENT_DATE_VALID", hasDate && !futureDate,
            !hasDate ? "Error" : (futureDate ? "Warning" : "Info"),
            !hasDate ? "ไม่พบวันที่ในเอกสาร"
                : (futureDate ? $"วันที่ในเอกสารเป็นอนาคต ({o.ExtractedDate:yyyy-MM-dd})"
                : $"วันที่ถูกต้อง ({o.ExtractedDate:yyyy-MM-dd})"),
            o.ExtractedDate?.ToString("yyyy-MM-dd")));

        // Rule 6: VAT breakdown balances (subtotal * 0.07 ≈ vat, ±1 THB tolerance)
        var sub = o.ExtractedSubTotal ?? 0m;
        var vat = o.ExtractedVatAmount ?? 0m;
        var expectedVat = Math.Round(sub * 0.07m, 2);
        var vatOk = sub == 0 || Math.Abs(vat - expectedVat) <= 1m;
        results.Add(new("VAT_BREAKDOWN_BALANCE", vatOk,
            vatOk ? "Info" : "Warning",
            vatOk ? "ยอด VAT สมดุลกับฐานภาษี"
                  : $"ยอด VAT ไม่ตรง — ฐาน {sub:N2} × 7% = {expectedVat:N2} แต่ได้ {vat:N2} (ห่าง {Math.Abs(vat - expectedVat):N2})",
            $"sub={sub:N2}, vat={vat:N2}, expected={expectedVat:N2}"));

        // Rule 7: Tenant cross-check — extracted buyer should match this company.
        // Only meaningful when there IS a buyer tax id on the document.
        if (!string.IsNullOrWhiteSpace(o.BuyerTaxId))
        {
            var match = NormalizeTaxId(o.BuyerTaxId) == NormalizeTaxId(company.TaxId);
            results.Add(new("TENANT_BUYER_MISMATCH", match,
                match ? "Info" : "Error",
                match ? "ผู้ซื้อในเอกสารตรงกับบริษัทที่ใช้งานอยู่"
                      : $"⚠️ ผู้ซื้อในเอกสาร ({o.BuyerTaxId}) ไม่ตรงกับบริษัท ({company.TaxId}) — " +
                        "อาจอัพโหลดผิดบริษัท",
                o.BuyerTaxId));
        }

        return results;
    }

    /// <summary>Thai Tax ID is 13 digits; the last is a checksum that
    /// equals (11 - (Σ digit×weight mod 11)) mod 10 where weights = 13..2.
    /// We accept any 13-digit string here (lenient) — full checksum
    /// validation can be a future tightening.</summary>
    private static bool IsThaiTaxId(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var digits = Regex.Replace(s, @"[\s-]", "");
        return digits.Length == 13 && digits.All(char.IsDigit);
    }

    private static string NormalizeTaxId(string? s)
        => string.IsNullOrWhiteSpace(s) ? "" : Regex.Replace(s, @"[\s-]", "");
}
