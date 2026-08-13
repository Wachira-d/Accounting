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

    /// <summary><para><b>Action</b> = "ต้องทำอะไร ที่ไหน" — ทุกข้อที่เตือนต้องบอก
    /// ทางออกให้ผู้ใช้เสมอ ไม่ใช่บอกแค่ว่าผิด (ผู้ใช้อ่านคำเตือนแล้วไม่รู้จะไปแก้
    /// ตรงไหนคือคำเตือนที่ไร้ประโยชน์)</para></summary>
    public record RuleResult(string RuleCode, bool IsValid, string Severity, string? Message, string? FieldValue,
        string? Action = null);

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
                rule = r.RuleCode, severity = r.Severity, message = r.Message,
                action = r.Action        // "ต้องไปทำอะไรที่ไหน" — UI แสดงใต้ข้อความ
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
            null,
            hasTaxKw ? null
                : "เปิด 'ไฟล์แนบ' ด้านล่างดูกระดาษจริง — ถ้าเป็นใบกำกับภาษีจริงแต่สแกนไม่ชัด "
                  + "ให้สแกนใหม่ให้คมขึ้น · ถ้าเป็นบิลเงินสด/ใบเสร็จธรรมดา ให้ติ๊ก 'ไม่เคลมภาษีซื้อ' ที่บรรทัดรายการ"));

        // Rule 2: Seller Tax ID format
        var sellerOk = IsThaiTaxId(o.ExtractedVendorTaxId);
        results.Add(new("SELLER_TAX_ID_FORMAT", sellerOk,
            sellerOk ? "Info" : "Error",
            sellerOk ? "เลขผู้เสียภาษีผู้ขายถูกต้อง"
                     : $"เลขผู้เสียภาษีผู้ขายไม่ถูกต้อง (ต้องเป็น 13 หลัก) — ที่ได้: '{o.ExtractedVendorTaxId}'",
            o.ExtractedVendorTaxId,
            sellerOk ? null
                : "แก้ที่ผู้ติดต่อของผู้ขาย: กดชื่อผู้ขายด้านบน → แก้ไข → กรอก 'เลขบัตรประชาชน/เลขผู้เสียภาษี' "
                  + "13 หลัก → บันทึก (ระบบจะใช้เลขนี้กับใบถัดไปของผู้ขายรายนี้อัตโนมัติ)"));

        // Rule 3: เลขผู้เสียภาษี "ผู้ซื้อ" (บังคับบนใบกำกับเต็มรูปเมื่อยอด ≥ 1,000)
        //
        // ผู้ซื้อบนใบกำกับฝั่งซื้อ = บริษัทเราเอง ดังนั้น "OCR อ่านไม่เจอ" ≠
        // "กระดาษไม่มีเลข" — เดิมสรุปจาก o.BuyerTaxId ว่างอย่างเดียวแล้วเตือนว่า
        // "ต้องระบุ... — ที่ได้: ''" ซึ่งอ่านแล้วเหมือนระบบพัง และเตือนผิดเกือบทุกใบ
        // เพราะใบกำกับไทยมักพิมพ์เลขผู้ซื้อตัวเล็ก/อยู่มุมที่ OCR จับไม่ติด
        // → ตรวจซ้ำในข้อความ OCR ทั้งหน้าด้วยเลขของบริษัทเอง ก่อนตัดสิน
        var requiresBuyerTaxId = (o.ExtractedTotalAmount ?? 0) >= 1000m;
        var companyTaxOk = IsThaiTaxId(company.TaxId);
        var buyerIdRead = IsThaiTaxId(o.BuyerTaxId);
        var companyIdFoundInDoc = companyTaxOk && RawTextHasTaxId(rawText, company.TaxId);
        if (!requiresBuyerTaxId)
        {
            results.Add(new("BUYER_TAX_ID_FORMAT", true, "Info",
                $"ยอดต่ำกว่า 1,000 บาท — ไม่บังคับระบุเลขผู้เสียภาษีผู้ซื้อ", o.BuyerTaxId));
        }
        else if (buyerIdRead || companyIdFoundInDoc)
        {
            results.Add(new("BUYER_TAX_ID_FORMAT", true, "Info",
                buyerIdRead
                    ? "พบเลขผู้เสียภาษีผู้ซื้อในเอกสาร"
                    : $"พบเลขผู้เสียภาษีของบริษัท ({company.TaxId}) ในเอกสาร (OCR ไม่ได้แยกเป็นช่องผู้ซื้อ แต่มีอยู่บนกระดาษ)",
                o.BuyerTaxId ?? company.TaxId));
        }
        else if (!companyTaxOk)
        {
            // เคสนี้แก้ได้จริงและต้องแก้ — บริษัทเรายังไม่มีเลขผู้เสียภาษีในระบบ
            results.Add(new("BUYER_TAX_ID_FORMAT", false, "Warning",
                "ยังไม่ได้ตั้งเลขผู้เสียภาษีของบริษัท — ระบบตรวจใบกำกับเต็มรูป (§86/4) ให้ไม่ได้",
                company.TaxId,
                "ไปที่ เมนู ตั้งค่า → ข้อมูลบริษัท → กรอก 'เลขผู้เสียภาษี' 13 หลัก แล้วกดสแกน/ตรวจใหม่อีกครั้ง"));
        }
        else
        {
            results.Add(new("BUYER_TAX_ID_FORMAT", false, "Warning",
                $"ไม่พบเลขผู้เสียภาษีของบริษัท ({company.TaxId}) ในเอกสาร — ใบกำกับเต็มรูป §86/4 ต้องมีเลขผู้ซื้อ "
                + "ถ้ากระดาษไม่มีจริง จะเคลมภาษีซื้อไม่ได้ (§82/5(1))",
                company.TaxId,
                "เปิด 'ไฟล์แนบ' ด้านล่างเพื่อดูกระดาษจริง — ถ้ามีเลขอยู่ ข้ามคำเตือนนี้ได้เลย "
                + "ถ้าไม่มี ให้ขอใบกำกับใหม่จากผู้ขาย (แจ้งชื่อ/ที่อยู่/เลขผู้เสียภาษีของเราให้ครบ)"));
        }

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
            o.ExtractedDate?.ToString("yyyy-MM-dd"),
            (hasDate && !futureDate) ? null
                : "กดปุ่ม 'แก้ไข' ด้านล่างเอกสารนี้ → แก้ช่อง 'วันที่เอกสาร' ให้ตรงกับกระดาษ → บันทึก "
                  + "(วันที่มีผลต่องวดภาษีที่ใบนี้เข้า ภ.พ.30)"));

        // Rule 6: VAT plausibility — รองรับใบกำกับที่มีหลายรายการและบางรายการ
        // ยกเว้น/0% (mixed VAT). VAT ที่ถูกต้องอยู่ในช่วง 0..(ฐานเต็ม × 7%):
        // ถ้า vat < sub×7% แปลว่ามีบางรายการไม่คิด VAT (ปกติ) — ไม่ใช่ error.
        // เตือนเฉพาะเมื่อ vat เกินเพดาน (มากกว่าฐานเต็ม×7%) หรือ ติดลบ —
        // ซึ่งเป็นไปไม่ได้ทางบัญชี. แสดงฐานที่คิด VAT จริง (vat/7%) เพื่ออ้างอิง.
        var sub = o.ExtractedSubTotal ?? 0m;
        var vat = o.ExtractedVatAmount ?? 0m;
        var maxVat = Math.Round(sub * 0.07m, 2);          // เพดาน: ทุกบาทคิด 7%
        var impliedBase = vat > 0 ? Math.Round(vat / 0.07m, 2) : 0m;  // ฐานที่คิด VAT จริง
        var vatOk = sub == 0 || (vat >= -0.01m && vat <= maxVat + 1m);
        var isMixed = vatOk && sub > 0 && vat > 0 && vat < maxVat - 1m;
        results.Add(new("VAT_BREAKDOWN_BALANCE", vatOk,
            vatOk ? "Info" : "Warning",
            !vatOk
                ? $"ยอด VAT ผิดปกติ — VAT {vat:N2} เกินเพดาน 7% ของฐาน {sub:N2} ({maxVat:N2}) หรือ ติดลบ"
                : isMixed
                    ? $"VAT {vat:N2} คิดจากฐาน ~{impliedBase:N2} (มีรายการยกเว้น/0% ~{sub - impliedBase:N2}) — ปกติสำหรับใบหลายรายการ"
                    : "ยอด VAT สมดุลกับฐานภาษี",
            $"sub={sub:N2}, vat={vat:N2}, maxVat={maxVat:N2}, impliedBase={impliedBase:N2}",
            vatOk ? null
                : "กดปุ่ม 'แก้ไข' ด้านล่าง → ตรวจตารางรายการ (จำนวน/ราคา/VAT ต่อบรรทัด) ให้ตรงกับกระดาษ → บันทึก "
                  + "· ถ้ากระดาษมีทั้งรายการมี VAT และยกเว้น ให้ตั้ง VAT ต่อบรรทัดให้ถูก"));

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
                o.BuyerTaxId,
                match ? null
                    : "สลับบริษัทที่มุมขวาบนให้ตรงกับใบนี้ แล้วอัปโหลดใหม่ · ถ้าใบนี้เป็นของบริษัทนี้จริง "
                      + "ให้แก้เลขผู้เสียภาษีบริษัทที่ ตั้งค่า → ข้อมูลบริษัท"));
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

    /// <summary>เลขผู้เสียภาษี 13 หลักปรากฏในข้อความ OCR ทั้งหน้าหรือไม่ —
    /// บนกระดาษมักพิมพ์เป็น "0-1055-35099-51-1" หรือเว้นวรรค จึงจับเป็น "ก้อนตัวเลข
    /// ที่มี - / ช่องว่างคั่น" แล้วค่อยถอดตัวคั่นออกเทียบทีละก้อน
    ///
    /// ตั้งใจ<b>ไม่</b>ถอดตัวคั่นทั้งหน้าแล้วค้นทีเดียว เพราะจะเอาเลขคนละชุด
    /// (เช่น ยอดเงินที่มีจุดทศนิยม/ลูกน้ำ) มาต่อกันจนเกิด match ปลอม — จำกัดขอบเขต
    /// ไว้แค่ "ก้อนเดียวกัน" ที่คั่นด้วย - หรือช่องว่างเท่านั้น (ลูกน้ำ/จุด/ตัวอักษร
    /// ตัดก้อน) แล้วค้นแบบ substring ในก้อนนั้น เพื่อให้เคส "123 456 789 0105535099511"
    /// (มีเลขอื่นนำหน้าในบรรทัดเดียวกัน) ยังหาเจอ — ไม่งั้นจะเตือนผิดทั้งที่กระดาษมีเลข</summary>
    private static bool RawTextHasTaxId(string? rawText, string? taxId)
    {
        var want = NormalizeTaxId(taxId);
        if (want.Length != 13 || string.IsNullOrWhiteSpace(rawText)) return false;
        foreach (Match m in Regex.Matches(rawText, @"[\d\s-]{13,}"))
        {
            if (NormalizeTaxId(m.Value).Contains(want, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
