using Accounting.Helpers;
using Accounting.Models.DTOs.Ocr;
using Accounting.Models.Enums;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// คำเตือน "ข้อมูลตามสรรพากรยังไม่ครบ" บนการ์ดผลสแกน — **ตัวเดียวของระบบ**
///
/// ═══ ทำไมต้องย้ายมาไว้ที่เซิร์ฟเวอร์ ═══
/// เดิมหน้า <c>document-scan.html</c> คำนวณกฎชุดนี้เองด้วย JavaScript โดยดูแค่
/// ช่องที่ OCR เดามา (<c>scan.buyerTaxId</c>) — เป็นสำเนามือของกฎใน
/// <see cref="RdComplianceValidator"/> ที่ไม่มีการแก้ตามมาเลย ผลคือ:
///
///   • ใบเสร็จ/ใบกำกับ หจก.สหกลชลบุรี เล่ม 007 เลขที่ 0339 พิมพ์เลขผู้ซื้อ
///     "0 2055 65017 74 1" ไว้เต็ม ๆ บนกระดาษ แต่การ์ดเตือนว่า "ใบกำกับภาษี
///     ตั้งแต่ 1,000 บาท ควรระบุเลขผู้เสียภาษีของผู้ซื้อ" เพราะ OCR ไม่ได้แยก
///     เป็นช่อง — **ไม่เคยเปิดดูข้อความบนกระดาษเลย** (defect class เดียวกับ
///     Rule 7 ที่แก้ไปแล้ว: กฎที่มองข้อมูลคนละชุดจะเถียงกันเองต่อหน้าผู้ใช้)
///   • คำเตือน "อาจเป็นเอกสารของบริษัทอื่น" ก็เป็นสำเนามือของ
///     TENANT_BUYER_MISMATCH รุ่นก่อนแก้ — ยังฟันธงจากช่องเดียวโดยไม่ดูกระดาษ
///     และไม่แยกว่าเป็นเอกสารฝั่งซื้อหรือฝั่งขาย
///
/// เอามาไว้ที่นี่ที่เดียวแล้วให้หน้าเว็บ "แสดง" อย่างเดียว — drift เป็นศูนย์
/// โดยโครงสร้าง (กฎเหล็ก #4 A: resolver กลาง ห้ามคำนวณเอง · รายการที่คัดลอก
/// มาด้วยมือ = drift แน่นอน แค่รอเวลา)
/// </summary>
public static class OcrScanComplianceEvaluator
{
    /// <summary>ชนิดเอกสารฝั่งซื้อ — ผู้ซื้อบนกระดาษคือบริษัทเราเอง จึงเทียบได้.
    /// ฝั่งขายผู้ซื้อคือลูกค้า ไม่มีทางตรงกับเลขเรา การเทียบไม่มีความหมาย</summary>
    private static bool IsPurchaseSide(string? scannedType, string? targetType, string? ourRole)
    {
        // OurRole มาจาก OcrDocumentRoleInferrer — เชื่อถือได้ที่สุดถ้ามี
        if (string.Equals(ourRole, "Seller", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(ourRole, "Buyer", StringComparison.OrdinalIgnoreCase)) return true;
        // ไม่รู้บทบาท → ตัดสินจากชนิดผ่านตัวกลาง (Helpers/DocumentSide.cs)
        // เดิมมีลิสต์ชนิดเขียนมือที่นี่ ซึ่งไม่ตรงกับอีก 2 ที่ในระบบ
        return Enum.TryParse<DocumentType>(targetType ?? scannedType, out var dt)
            && DocumentSide.IsPurchase(dt, ourRole);
    }

    public static List<OcrScanIssueDto> Evaluate(OcrResultResponse r, string? ourTaxId)
    {
        var issues = new List<OcrScanIssueDto>();
        if (!string.Equals(r.ScanStatus, "Completed", StringComparison.OrdinalIgnoreCase))
            return issues;

        // ── ช่องพื้นฐานที่ OCR ควรอ่านได้ ─────────────────────────────────
        if (string.IsNullOrWhiteSpace(r.ExtractedVendorTaxId))
            issues.Add(new("warn", "ไม่พบเลขประจำตัวผู้เสียภาษีของผู้ขาย"));
        if (string.IsNullOrWhiteSpace(r.ExtractedDocumentNumber))
            issues.Add(new("warn", "ไม่พบเลขที่เอกสาร"));
        if (r.ExtractedDate == null)
            issues.Add(new("warn", "ไม่พบวันที่เอกสาร"));
        if (r.ExtractedTotalAmount == null)
            issues.Add(new("warn", "ไม่พบยอดรวม"));

        // ── เลขผู้ซื้อ (§86/4) ────────────────────────────────────────────
        //
        // "OCR ไม่ได้แยกเป็นช่อง" ≠ "กระดาษไม่มีเลข" — ต้องค้นในข้อความทั้งหน้า
        // ก่อนตัดสินเสมอ นี่คือจุดที่เวอร์ชันฝั่ง JS ขาดไปแล้วเตือนผิดทุกใบที่
        // พิมพ์เลขผู้ซื้อไว้เหนือเส้นประของแบบฟอร์ม
        var buyerReadable = ThaiTaxId.IsPlausibleFromScan(r.BuyerTaxId);
        var buyerIsUs = ThaiTaxId.Same(r.BuyerTaxId, ourTaxId);
        var ourIdOnPaper = ThaiTaxId.IsValid(ourTaxId)
            && RdComplianceValidator.RawTextHasTaxId(r.RawTextContent, ourTaxId);
        var isTaxInvoice = string.Equals(r.DocumentType, "TaxInvoice", StringComparison.OrdinalIgnoreCase);
        var purchaseSide = IsPurchaseSide(r.ScannedDocumentType, r.TargetDocumentType, r.OurRole);

        if (isTaxInvoice && (r.ExtractedTotalAmount ?? 0) >= 1000m
            && !buyerReadable && !ourIdOnPaper)
        {
            issues.Add(new("warn",
                "ใบกำกับภาษีตั้งแต่ 1,000 บาท ควรระบุเลขผู้เสียภาษีของผู้ซื้อ — "
                + "ไม่พบทั้งในช่องที่อ่านได้และในข้อความบนกระดาษ"));
        }

        // ── ใบนี้เป็นของบริษัทที่เปิดอยู่จริงไหม ──────────────────────────
        //
        // เงื่อนไขเดียวกับ TENANT_BUYER_MISMATCH ใน RdComplianceValidator เป๊ะ ๆ
        // (ฝั่งซื้อ + เลขที่อ่านได้เป็นเลขจริง + ไม่ตรงกับเรา + เลขเราไม่อยู่บน
        // กระดาษเลย) — ห้ามให้สองที่ตอบไม่ตรงกันบนใบเดียวกัน
        if (!buyerIsUs && !string.IsNullOrWhiteSpace(r.BuyerTaxId) && purchaseSide)
        {
            if (ourIdOnPaper)
                issues.Add(new("warn",
                    $"เครื่องอ่านหยิบเลข {r.BuyerTaxId} มาใส่ช่องผู้ซื้อ แต่บนกระดาษมีเลขของบริษัทเราอยู่จริง "
                    + "— ใบนี้เป็นของบริษัทนี้ ระบบแค่อ่านผิดช่อง"));
            else if (!buyerReadable)
                issues.Add(new("warn",
                    $"เลขในช่องผู้ซื้อ {r.BuyerTaxId} ไม่ใช่เลขผู้เสียภาษีที่ถูกต้อง "
                    + "(ไม่ผ่าน check digit หรือเป็นบาร์โค้ดสินค้า)"));
            else if (ThaiTaxId.IsValid(ourTaxId))
                issues.Add(new("error",
                    $"ผู้ซื้อในเอกสาร ({r.BuyerTaxId}) ไม่ตรงกับบริษัท ({ourTaxId}) "
                    + "และไม่พบเลขของบริษัทนี้ที่ใดบนกระดาษ — อาจเป็นเอกสารของบริษัทอื่น"));
        }

        // ── ผู้ขายยังเปิดกิจการอยู่ไหม (จาก DBD) ──────────────────────────
        //
        // สถานะนิติบุคคลถูกดึงมาแล้วแต่เดิม**โผล่แค่ใน ProcessingNotes** ซึ่งเป็น
        // log ยาว ๆ ที่ผู้ใช้ไม่เปิดอ่าน. ใบกำกับจากนิติบุคคลที่เลิก/ร้าง/อยู่
        // ระหว่างชำระบัญชี เป็นสัญญาณว่าเอกสารอาจใช้เป็นรายจ่ายทางภาษีไม่ได้
        // (§65 ตรี(9) หลักฐานไม่น่าเชื่อถือ) และภาษีซื้ออาจต้องห้ามถ้าผู้ออกไม่มี
        // สิทธิออกใบกำกับแล้ว (§82/5(5)) — ต้องขึ้นหน้าการ์ดให้คนเห็นก่อนอนุมัติ
        //
        // ⚠️ สถานะเป็นข้อความอิสระจาก DBD — คำที่ไม่รู้จัก **ไม่เตือน** (ไม่ใช่
        // "ถือว่าผิด"): เตือนผิดทุกใบแย่กว่าไม่เตือน และเราไม่รู้ = ต้องเงียบ
        if (purchaseSide && r.DbdInfo is { Matched: true, Status: { } dbdStatus }
            && LooksInactive(dbdStatus))
        {
            issues.Add(new("warn",
                $"ผู้ขายรายนี้มีสถานะ \"{dbdStatus}\" ในทะเบียนกรมพัฒนาธุรกิจการค้า — "
                + "ตรวจสอบความถูกต้องของเอกสารก่อนใช้เป็นรายจ่าย/ภาษีซื้อ"));
        }

        return issues;
    }

    /// <summary>สถานะนิติบุคคลจาก DBD บ่งชี้ว่า "ไม่ได้ดำเนินกิจการแล้ว" หรือไม่ —
    /// คำที่ไม่อยู่ในลิสต์ถือว่า <b>ไม่รู้</b> (คืน false = ไม่เตือน) ไม่ใช่ "ปกติ"
    /// ทั้งสองอย่างเงียบเหมือนกัน แต่เจตนาต่างกัน: ถ้าวันหนึ่ง DBD เปลี่ยนคำ
    /// เราจะเงียบ ไม่ใช่เตือนมั่ว</summary>
    private static bool LooksInactive(string status)
    {
        var s = status.Trim();
        if (s.Length == 0) return false;
        // ตัวกลาง/DBD บางเส้นคืนสถานะเป็นอังกฤษ — ครอบทั้งสองภาษา
        string[] inactive = { "เลิก", "ร้าง", "ชำระบัญชี", "ถอนทะเบียน", "พิทักษ์ทรัพย์", "ล้มละลาย" };
        string[] inactiveEn = { "dissolved", "liquidat", "struck off", "bankrupt", "defunct" };
        return inactive.Any(k => s.Contains(k, StringComparison.Ordinal))
            || inactiveEn.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
