using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ชนิดเอกสารที่เส้น OCR จะสร้าง + ธง "ใบมัดจำ"</summary>
/// <param name="Type">ชนิดจริงที่จะบันทึกลง <c>Document.DocumentType</c></param>
/// <param name="IsDeposit">true = pseudo-target "Deposit" (ลงเป็น Receipt + IsDeposit)</param>
public readonly record struct OcrTargetResolution(DocumentType Type, bool IsDeposit);

/// <summary>
/// ตัวตัดสิน "สแกนใบนี้จะกลายเป็นเอกสารชนิดไหน" — pure, ไม่มี I/O
///
/// ═══ ทำไมต้องอยู่ที่เดียว (ผลตรวจย้อน 2026-09-06 · คอมมิต 4fd8dd6) ═══
/// ด่านสิทธิ์ "สร้างเอกสาร" ใน <c>OcrController</c> เดิมมีตัวแปลงชนิดของตัวเองที่
/// <b>คืน null</b> เมื่อ <c>TargetDocumentType</c> ว่าง/แปลงไม่ได้ (รวมค่า pseudo
/// "Deposit") แล้วโค้ดข้ามด่านไปเลย — แต่ <c>OcrService</c> ยัง<b>สร้างเอกสารจริง</b>
/// ต่อด้วย fallback ตามชนิดกระดาษ (Expense/PaymentVoucher/PurchaseInvoice) และ
/// "Deposit" ก็กลายเป็น <b>Receipt ฝั่งขาย</b> ⇒ ด่านที่ดูเหมือนมีแต่ไม่ทำงานเลยในเคส
/// ที่พบบ่อยที่สุด (สแกนใบเสร็จร้านค้าที่ยังไม่มี TargetDocumentType)
///
/// กติกา: ทั้งเส้นสร้างเอกสารและด่านสิทธิ์ต้องเรียกฟังก์ชันนี้ตัวเดียว — ห้ามคำนวณเอง
/// (defect class "สำเนามือ = drift" + "ด่านที่ครอบแค่ทางเดียว")
/// </summary>
public static class OcrTargetDocumentType
{
    /// <summary>ค่า pseudo-target ที่ไม่ใช่สมาชิกของ <see cref="DocumentType"/></summary>
    public const string DepositPseudoType = "Deposit";

    /// <param name="targetTypeOverride">ชนิดที่ผู้ใช้เลือกสด ๆ ในหน้า review (null = ไม่ระบุ)</param>
    /// <param name="scanTargetDocumentType">ชนิดที่ role-inferrer/VendorIntel เขียนไว้บนแถวสแกน</param>
    /// <param name="scannedPaperType">ชนิดของ<b>กระดาษ</b>ที่ engine อ่านได้ (<c>OcrScanResult.DocumentType</c>)</param>
    /// <param name="hasLinkedPurchaseOrder">สแกนนี้ถูกผูกกับใบสั่งซื้อผ่าน /link-po แล้วหรือยัง</param>
    public static OcrTargetResolution Resolve(
        string? targetTypeOverride,
        string? scanTargetDocumentType,
        string? scannedPaperType,
        bool hasLinkedPurchaseOrder = false)
    {
        // (0) ผูกกับ PO แล้ว = รับของเข้าตาม PO เสมอ (ฝั่งซื้อ) — ชนะทุกอย่าง
        if (hasLinkedPurchaseOrder)
            return new(DocumentType.PurchaseInvoice, false);

        // (1) ใบมัดจำ — ต้องตัดสินก่อน Enum.TryParse ไม่งั้นตกไป fallback แล้วกลายเป็น Expense
        var wantDeposit =
            string.Equals(targetTypeOverride, DepositPseudoType, StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(targetTypeOverride)
                && string.Equals(scanTargetDocumentType, DepositPseudoType, StringComparison.OrdinalIgnoreCase));
        if (wantDeposit)
            return new(DocumentType.Receipt, true);

        // (2) ผู้ใช้เลือกเอง
        if (!string.IsNullOrWhiteSpace(targetTypeOverride)
            && Enum.TryParse<DocumentType>(targetTypeOverride, ignoreCase: true, out var overrideTarget))
            return new(overrideTarget, false);

        // (3) ชนิดที่ระบบอนุมานไว้บนแถวสแกน
        if (!string.IsNullOrWhiteSpace(scanTargetDocumentType)
            && Enum.TryParse<DocumentType>(scanTargetDocumentType, ignoreCase: true, out var inferredTarget))
            return new(inferredTarget, false);

        // (4) fallback ตามชนิดกระดาษ — ไม่มีทาง "ไม่ตอบ" เพราะเส้นสร้างเอกสารสร้างจริงเสมอ
        var fallback = scannedPaperType switch
        {
            "Invoice" or "TaxInvoice" => DocumentType.PurchaseInvoice,
            "Receipt" => DocumentType.PaymentVoucher,
            "CertificateInLieu" => DocumentType.CertificateInLieu,
            _ => DocumentType.Expense,
        };
        return new(fallback, false);
    }
}
