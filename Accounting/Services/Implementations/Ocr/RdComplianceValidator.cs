using System.Text.Json;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Helpers;
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
///   1. หัวเรื่องตามชนิดเอกสาร: ใบกำกับภาษี §86/4 / ใบลดหนี้ §86/10 /
///      ใบเพิ่มหนี้ §86/9 — ข้ามเมื่อเอกสารไม่มี VAT หรืออ่านกระดาษไม่ได้.
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

        var results = RunRules(ocrResult, rawOcrText, company, doc);

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

    /// <summary>internal เพื่อให้เทสต์เรียกกฎตรง ๆ ได้โดยไม่ต้องมี DbContext —
    /// เมธอดนี้เป็น pure function (ไม่แตะฐานข้อมูล) ตามกฎเหล็ก #4 G</summary>
    internal static List<RuleResult> RunRules(OcrResultResponse o, string? rawText, Company company, Document doc)
    {
        var results = new List<RuleResult>();
        // ย่อข้อความก่อนค้นคำสำคัญ — OCR ไทยแทรกช่องว่างกลางคำ/แยกนิคหิตกับสระอา
        // เป็นปกติ ถ้าค้นบนข้อความดิบจะ "ไม่พบ" คำที่อยู่บนกระดาษเต็ม ๆ
        // (บั๊กจริง PI-20260820-0005: หัวกระดาษ "ต้นฉบับใบส่งสินค้า/ต้นฉบับใบกำกับภาษี"
        //  แต่ระบบเตือนว่าไม่พบคำว่า "ใบกำกับภาษี")
        var raw = ThaiTextNormalizer.SquashForKeywordMatch(rawText);
        // ข้อความ OCR สั้น/ว่าง = "ตรวจไม่ได้" ไม่ใช่ "กระดาษไม่มี" — เครื่องอ่านบาง
        // ตัว (vision AI) คืนเฉพาะฟิลด์ที่สกัดได้ ไม่ได้คืนข้อความทั้งหน้า ถ้าเอา
        // ความว่างมาสรุปว่าเอกสารไม่ครบ จะเตือนผิดทุกใบที่ใช้เครื่องอ่านแบบนั้น
        var canReadPaper = (rawText ?? "").Trim().Length >= 40;

        // Rule 1: คำที่กฎหมายบังคับให้มีบนหัวเอกสาร — **ต่างกันตามชนิดเอกสาร**
        //   ใบกำกับภาษี §86/4  → "ใบกำกับภาษี"
        //   ใบลดหนี้   §86/10 → "ใบลดหนี้"   (ไม่ใช่ "ใบกำกับภาษี")
        //   ใบเพิ่มหนี้ §86/9  → "ใบเพิ่มหนี้"
        // เดิมบังคับหาคำว่า "ใบกำกับภาษี" กับเอกสารทุกชนิด → ใบลดหนี้โดนเตือนทุกใบ
        // ทั้งที่กระดาษถูกต้องตามกฎหมายอยู่แล้ว
        var needsKeyword = doc.VatAmount > 0m;   // ไม่มี VAT = ไม่ใช่เอกสารภาษี ไม่ต้องบังคับ
        var (kwList, kwLabel) = doc.DocumentType switch
        {
            DocumentType.CreditNote  => (new[] { "ใบลดหนี้", "credit note", "ใบกำกับภาษี" }, "ใบลดหนี้ (§86/10)"),
            DocumentType.DebitNote   => (new[] { "ใบเพิ่มหนี้", "debit note", "ใบกำกับภาษี" }, "ใบเพิ่มหนี้ (§86/9)"),
            _                        => (new[] { "ใบกำกับภาษี", "tax invoice" }, "ใบกำกับภาษี (§86/4)"),
        };
        // ย่อ "คำที่ค้น" ด้วยสูตรเดียวกับข้อความ ไม่งั้น "tax invoice" (มีช่องว่าง)
        // จะหาไม่เจอในข้อความที่ตัดช่องว่างไปแล้ว
        var hasTaxKw = kwList.Any(k => raw.Contains(
            ThaiTextNormalizer.SquashForKeywordMatch(k), StringComparison.Ordinal));
        if (!needsKeyword || !canReadPaper || hasTaxKw)
        {
            results.Add(new("TAX_INVOICE_KEYWORD", true, "Info",
                !needsKeyword ? "เอกสารไม่มี VAT — ไม่ต้องมีหัวเรื่องเอกสารภาษี"
                : !canReadPaper ? "เครื่องอ่านไม่ได้คืนข้อความทั้งหน้า — ข้ามการตรวจหัวเรื่อง (ไม่ได้แปลว่าเอกสารผิด)"
                : $"พบหัวเรื่อง {kwLabel} ในเอกสาร",
                null));
        }
        else
        {
            results.Add(new("TAX_INVOICE_KEYWORD", false, "Warning",
                $"ไม่พบคำว่า \"{kwList[0]}\" บนเอกสาร — {kwLabel} ต้องมีคำนี้บนหัวเอกสาร",
                null,
                "เปิด 'ไฟล์แนบ' ด้านล่างดูกระดาษจริง — ถ้ามีคำนี้อยู่แต่สแกนไม่ชัด ให้สแกนใหม่ให้คมขึ้น "
                + "· ถ้ากระดาษไม่มีจริง ให้ขอเอกสารที่ถูกต้องจากผู้ขาย"));
        }

        // Rule 2: Seller Tax ID format
        //
        // แยกสองกรณีให้ผู้ใช้รู้ว่าต้องไปทำอะไร:
        //   • ไม่ครบ 13 หลัก → Error (ขาดรายการตาม §86/4(2) เคลมภาษีซื้อไม่ได้)
        //   • ครบ 13 หลักแต่ check digit ไม่ผ่าน → Warning "OCR น่าจะอ่านเลขเพี้ยน
        //     1 ตัว" ไม่ใช่ Error เพราะกระดาษมักถูก แต่เครื่องอ่านผิด (0↔O, 1↔I, 8↔B)
        var sellerDigits = ThaiTaxId.Normalize(o.ExtractedVendorTaxId);
        var sellerOk = ThaiTaxId.IsValid(o.ExtractedVendorTaxId);
        var sellerLenOk = sellerDigits.Length == 13;
        results.Add(new("SELLER_TAX_ID_FORMAT", sellerOk,
            sellerOk ? "Info" : sellerLenOk ? "Warning" : "Error",
            sellerOk ? "เลขผู้เสียภาษีผู้ขายถูกต้อง"
                : sellerLenOk
                    ? $"เลขผู้เสียภาษีผู้ขาย '{o.ExtractedVendorTaxId}' ครบ 13 หลักแต่ไม่ผ่าน check digit "
                      + "— เครื่องอ่านน่าจะอ่านเพี้ยนไป 1 ตัว"
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
        var companyTaxOk = ThaiTaxId.IsValid(company.TaxId);
        // ⚠️ ต้องเป็นเลขที่ "ผ่าน checksum และไม่ใช่บาร์โค้ดสินค้า" — เดิมรับเลข
        // 13 หลักอะไรก็ได้ ⇒ บาร์โค้ด EAN-13 ในตารางสินค้าที่ถูกยัดมาในช่องนี้
        // ทำให้กฎที่ 3 "ผ่าน" ด้วยเหตุผลที่ผิด แล้วกฎที่ 7 ก็ไปฟันธงว่าผิดบริษัท
        var buyerIdRead = ThaiTaxId.IsPlausibleFromScan(o.BuyerTaxId);
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
        else if (!canReadPaper)
        {
            // เครื่องอ่านไม่ได้คืนข้อความทั้งหน้า → ตรวจไม่ได้ ห้ามสรุปว่ากระดาษไม่มี
            results.Add(new("BUYER_TAX_ID_FORMAT", true, "Info",
                "เครื่องอ่านไม่ได้คืนข้อความทั้งหน้า — ตรวจเลขผู้เสียภาษีผู้ซื้อบนกระดาษอัตโนมัติไม่ได้ "
                + "(ไม่ได้แปลว่าเอกสารไม่ครบ)", company.TaxId));
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

        // Rule 4: รหัสสาขาบนใบกำกับ — §86/4 + ประกาศอธิบดีฯ ฉบับที่ 199
        //
        // ⚠️ เดิมกฎนี้เป็น stub: hard-code IsValid=true/Info แล้วรายงานสาขาของ
        // "บริษัทเรา" จาก metadata โดยไม่เคยดูกระดาษเลย — คอมเมนต์เดิมบอกว่า
        // "OcrExtractedData ยังไม่มีสาขา" ซึ่งไม่จริงแล้ว (มี VendorBranchCode/
        // BuyerBranchCode) แต่กฎไม่เคยถูกต่อสาย ⇒ ใบของสาขาที่ 3 ผ่านฉลุยเป็น
        // สำนักงานใหญ่ ทั้งที่ผิดทั้ง §86/4 และรายงานรายสถานประกอบการ (§87)
        // null = "กระดาษไม่ได้ระบุ" (ต่างจาก Normalize ที่แปลงว่าง → "00000"
        // ซึ่งจะกลบความจริงว่าไม่มีข้อมูล)
        var sellerBranch = BranchOrNull(o.VendorBranchCode);
        var buyerBranch = BranchOrNull(o.BuyerBranchCode);
        var ourBranch = TaxBranchCode.Normalize(company.BranchCode);
        if (!needsKeyword)
        {
            results.Add(new("BRANCH_CODE_PRESENT", true, "Info",
                "เอกสารไม่มี VAT — ไม่บังคับระบุสาขาตาม §86/4", null));
        }
        else if (sellerBranch == null && !canReadPaper)
        {
            results.Add(new("BRANCH_CODE_PRESENT", true, "Info",
                "เครื่องอ่านไม่ได้คืนข้อความทั้งหน้า — ตรวจรหัสสาขาบนกระดาษอัตโนมัติไม่ได้", null));
        }
        else if (sellerBranch == null)
        {
            results.Add(new("BRANCH_CODE_PRESENT", false, "Warning",
                "ไม่พบสาขาของผู้ขายบนเอกสาร — ใบกำกับเต็มรูป §86/4 ต้องระบุ \"สำนักงานใหญ่\" "
                + "หรือ \"สาขาที่ NNNNN\" ของผู้ขาย (ประกาศอธิบดีฯ ฉบับที่ 199)",
                null,
                "เปิด 'ไฟล์แนบ' ดูกระดาษจริง — ถ้ามีระบุอยู่ ให้กด 'แก้ไข' แล้วกรอกสาขาผู้ขาย "
                + "· ถ้ากระดาษไม่มีจริง ให้ขอใบกำกับที่ถูกต้องจากผู้ขาย"));
        }
        else if (buyerBranch != null && buyerBranch != ourBranch)
        {
            // สาขาผู้ซื้อบนกระดาษไม่ตรงกับสาขาที่บริษัทนี้ตั้งไว้ — ไม่ใช่เรื่องผิด
            // เสมอไป (กิจการหลายสาขาอาจสแกนใบของอีกสาขา) แต่ต้องให้คนตัดสิน
            // เพราะมีผลกับรายงานภาษีซื้อรายสถานประกอบการ (§87)
            results.Add(new("BRANCH_CODE_PRESENT", false, "Warning",
                $"สาขาผู้ซื้อบนเอกสารคือ {TaxBranchCode.Label(buyerBranch)} แต่บริษัทที่เปิดอยู่ตั้งไว้เป็น "
                + $"{TaxBranchCode.Label(ourBranch)} — รายงานภาษีซื้อแยกตามสถานประกอบการ (§87) จะเข้าคนละสาขา",
                buyerBranch,
                "ถ้าใบนี้เป็นของอีกสาขา ให้สลับสาขาที่มุมขวาบนก่อนสร้างเอกสาร "
                + "· ถ้ากระดาษระบุผิด ให้แก้ที่ช่องสาขาผู้ซื้อ"));
        }
        else
        {
            results.Add(new("BRANCH_CODE_PRESENT", true, "Info",
                $"สาขาผู้ขาย {TaxBranchCode.Label(sellerBranch)}"
                + (buyerBranch != null ? $" · สาขาผู้ซื้อ {TaxBranchCode.Label(buyerBranch)}" : ""),
                sellerBranch));
        }

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

        // Rule 7: Tenant cross-check — "ใบนี้เป็นของบริษัทที่เปิดอยู่จริงไหม"
        //
        // ═══ กติกาเหล็ก: กฎนี้ห้ามขัดกับกฎที่ 3 ═══
        // บั๊กจริง PI-20260820-0005 เตือนสองอย่างที่ขัดกันเองบนใบเดียวกัน:
        // กฎที่ 3 บอก "พบเลขผู้เสียภาษีผู้ซื้อในเอกสาร" (ผ่าน) แต่กฎที่ 7 บอก
        // "อาจอัพโหลดผิดบริษัท" (Error) — เพราะกฎที่ 7 ดูแค่ช่อง o.BuyerTaxId
        // ที่ OCR เดามา ไม่เคยเปิดดูข้อความบนกระดาษเลย ผู้ใช้เลยเห็นระบบเถียงกันเอง
        //
        // เงื่อนไขที่จะฟันธงว่า "ผิดบริษัท" ได้ ต้องครบ **ทุกข้อ**:
        //   1. เป็นเอกสารฝั่งซื้อ (ผู้ซื้อ = เราเอง) — ฝั่งขายผู้ซื้อคือลูกค้า
        //      ไม่มีทางตรงกับเลขเรา การเทียบจึงไม่มีความหมาย
        //   2. เลขที่อ่านได้เป็นเลขผู้เสียภาษีจริง (ผ่าน checksum + ไม่ใช่บาร์โค้ด)
        //   3. เลขนั้นไม่ตรงกับบริษัทเรา
        //   4. และ **ไม่พบเลขบริษัทเราที่ไหนเลยบนกระดาษ** ← ข้อที่หายไปเดิม
        var isPurchaseSide = doc.DocumentType
            is DocumentType.PurchaseInvoice or DocumentType.Expense
            or DocumentType.PaymentVoucher or DocumentType.PurchaseOrder
            or DocumentType.PurchaseRequisition or DocumentType.GoodsReceiptNote
            or DocumentType.CertificateInLieu;
        var buyerDigits = ThaiTaxId.Normalize(o.BuyerTaxId);
        var buyerIsRealTaxId = ThaiTaxId.IsPlausibleFromScan(o.BuyerTaxId);
        var buyerIsUs = ThaiTaxId.Same(o.BuyerTaxId, company.TaxId);

        if (buyerIsUs)
        {
            results.Add(new("TENANT_BUYER_MISMATCH", true, "Info",
                "ผู้ซื้อในเอกสารตรงกับบริษัทที่ใช้งานอยู่", o.BuyerTaxId));
        }
        else if (buyerDigits.Length == 0)
        {
            // ไม่มีเลขให้เทียบ — กฎที่ 3 ดูแลเรื่อง "กระดาษมีเลขผู้ซื้อไหม" อยู่แล้ว
        }
        else if (!isPurchaseSide)
        {
            results.Add(new("TENANT_BUYER_MISMATCH", true, "Info",
                "เอกสารฝั่งขาย — ผู้ซื้อบนกระดาษคือลูกค้า ไม่ต้องตรงกับเลขบริษัทเรา",
                o.BuyerTaxId));
        }
        else if (companyIdFoundInDoc)
        {
            // เลขบริษัทเราอยู่บนกระดาษจริง (กฎที่ 3 ยืนยันแล้ว) แต่ OCR ไปหยิบ
            // เลข 13 หลักตัวอื่นมาใส่ช่องผู้ซื้อ — บาร์โค้ดสินค้า / เลขทะเบียน /
            // เลขที่เอกสาร ⇒ นี่คือ "อ่านผิดช่อง" ไม่ใช่ "อัพโหลดผิดบริษัท"
            // ใช้ RuleCode คนละตัวเพื่อไม่ให้ไปติดธง OcrTenantMismatchFlag
            results.Add(new("BUYER_TAX_ID_MISREAD", false, "Warning",
                $"เครื่องอ่านหยิบเลข '{o.BuyerTaxId}' มาใส่ช่องผู้ซื้อ แต่บนกระดาษมีเลขของบริษัทเรา "
                + $"({company.TaxId}) อยู่จริง — ใบนี้เป็นของบริษัทนี้ ระบบแค่อ่านผิดช่อง",
                o.BuyerTaxId,
                "กดปุ่ม 'แก้ไข' ด้านล่าง → แก้ช่อง 'เลขผู้เสียภาษีผู้ซื้อ' เป็น " + company.TaxId
                + " → บันทึก (ระบบจะจำไว้ใช้กับใบถัดไปของผู้ขายรายนี้)"));
        }
        else if (!buyerIsRealTaxId)
        {
            // เลขที่อ่านมาไม่ผ่าน checksum หรือหน้าตาเป็นบาร์โค้ดสินค้า —
            // ยังฟันธงเรื่องบริษัทไม่ได้ ต้องให้คนดูกระดาษก่อน
            results.Add(new("BUYER_TAX_ID_MISREAD", false, "Warning",
                $"เลขในช่องผู้ซื้อ '{o.BuyerTaxId}' ไม่ใช่เลขผู้เสียภาษีที่ถูกต้อง "
                + "(ไม่ผ่าน check digit หรือเป็นบาร์โค้ดสินค้า) — ยังสรุปไม่ได้ว่าใบนี้ของบริษัทไหน",
                o.BuyerTaxId,
                "เปิด 'ไฟล์แนบ' ด้านล่างดูกระดาษจริง → ถ้าเป็นใบของบริษัทนี้ ให้กด 'แก้ไข' "
                + "แล้วกรอกเลขผู้ซื้อเป็น " + (company.TaxId ?? "เลขบริษัทเรา")));
        }
        else
        {
            // ครบทั้ง 4 เงื่อนไข — เป็นการเตือนที่มีน้ำหนักจริง
            results.Add(new("TENANT_BUYER_MISMATCH", false, "Error",
                $"⚠️ ผู้ซื้อในเอกสาร ({o.BuyerTaxId}) ไม่ตรงกับบริษัท ({company.TaxId}) "
                + "และไม่พบเลขของบริษัทนี้ที่ใดบนกระดาษ — อาจอัพโหลดผิดบริษัท",
                o.BuyerTaxId,
                "สลับบริษัทที่มุมขวาบนให้ตรงกับใบนี้ แล้วอัปโหลดใหม่ · ถ้าใบนี้เป็นของบริษัทนี้จริง "
                + "ให้แก้เลขผู้เสียภาษีบริษัทที่ ตั้งค่า → ข้อมูลบริษัท"));
        }

        return results;
    }

    /// <summary>เลขผู้เสียภาษี 13 หลักปรากฏในข้อความ OCR ทั้งหน้าหรือไม่ —
    /// บนกระดาษมักพิมพ์เป็น "0-1055-35099-51-1" หรือเว้นวรรค จึงจับเป็น "ก้อนตัวเลข
    /// ที่มี - / ช่องว่างคั่น" แล้วค่อยถอดตัวคั่นออกเทียบทีละก้อน
    ///
    /// ตั้งใจ<b>ไม่</b>ถอดตัวคั่นทั้งหน้าแล้วค้นทีเดียว เพราะจะเอาเลขคนละชุด
    /// (เช่น ยอดเงินที่มีจุดทศนิยม/ลูกน้ำ) มาต่อกันจนเกิด match ปลอม — จำกัดขอบเขต
    /// ไว้แค่ "ก้อนเดียวกัน" ที่คั่นด้วย - หรือช่องว่างเท่านั้น (ลูกน้ำ/จุด/ตัวอักษร
    /// ตัดก้อน) แล้วค้นแบบ substring ในก้อนนั้น เพื่อให้เคส "123 456 789 0105535099511"
    /// (มีเลขอื่นนำหน้าในบรรทัดเดียวกัน) ยังหาเจอ — ไม่งั้นจะเตือนผิดทั้งที่กระดาษมีเลข</summary>
    /// <summary>รหัสสาขา 5 หลัก หรือ null เมื่อกระดาษไม่ได้ระบุ/รูปแบบผิด —
    /// ห้ามใช้ <c>TaxBranchCode.Normalize</c> ตรงนี้เพราะมันแปลงว่างเป็น "00000"
    /// ซึ่งจะกลบความจริงว่า "ไม่มีข้อมูล" ให้กลายเป็น "สำนักงานใหญ่"</summary>
    private static string? BranchOrNull(string? raw)
        => TaxBranchCode.TryNormalize(raw, out var code, out _) ? code : null;

    internal static bool RawTextHasTaxId(string? rawText, string? taxId)
    {
        var want = ThaiTaxId.Normalize(taxId);
        if (want.Length != 13 || string.IsNullOrWhiteSpace(rawText)) return false;
        // \s เดิมครอบ \n ด้วย ⇒ เลขท้ายบรรทัดถูกต่อกับเลขต้นบรรทัดถัดไปจนเกิด
        // match ปลอม ซึ่งอันตรายเป็นพิเศษตรงนี้เพราะ "เจอเลขเราบนกระดาษ" คือสิ่งที่
        // ปิดคำเตือน "อัพโหลดผิดบริษัท" — match ปลอมแปลว่าเตือนของจริงถูกกลบ
        foreach (Match m in Regex.Matches(rawText, @"[\d \t-]{13,}"))
        {
            if (ThaiTaxId.Normalize(m.Value).Contains(want, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
