namespace Accounting.Helpers;

/// <summary>
/// **ชนิดกระดาษจากผล Azure Document Intelligence — สิ่งที่พิมพ์อยู่บนใบชนะเสมอ** (pure, ไม่มี I/O)
///
/// <para>⚠️ ที่มา (ผลตรวจ 2026-09-06 · T2-14): ชื่อไฟล์เป็นตัวเลือกโมเดล (<c>AzureDiRequestPlanner.DetectModelFromFilename</c> —
/// "receipt"/"cafe"/"ใบเสร็จ" → prebuilt-receipt) แล้ว <c>modelId</c> ก็บังคับชนิดเป็น <c>Receipt</c> ต่อ ⇒ <b>ใบกำกับภาษีเต็มรูปที่ผู้ใช้
/// ตั้งชื่อไฟล์ว่า "receipt-2026-09.pdf" ถูกตีเป็นใบเสร็จ</b> แล้วโดนปิดเคลมภาษีซื้อตาม §82/5(1) ทั้งที่กระดาษถูกต้องครบ</para>
///
/// <para>รอบ 201 ทีม OC (A-OC4): ย้าย<b>ตรงตัว</b>จาก <c>OcrService.MapAzureDocType</c> (+ ทางเข้าเทสต์ <c>MapAzureDocTypeForTest</c> ที่ถูกลบ)
/// — ลำดับคำเดิมทุกตัว · เทสต์เดิม <c>AzureDiFieldMappingTests</c> เรียกตัวนี้ตรง ๆ</para>
/// </summary>
public static class OcrPaperDocumentType
{
    /// <summary>คืนชื่อชนิดกระดาษ ("Invoice" · "CreditNote" · "DebitNote" · "Receipt") — คำบนกระดาษเป็นหลักฐาน
    /// ส่วนชื่อไฟล์/โมเดลเป็นแค่การเดาของเรา · ไม่รู้จักชนิด = "Invoice" (พฤติกรรมเดิม)</summary>
    public static string FromAzure(string? azureDocType, string? modelId, string? rawText = null)
    {
        if (!string.IsNullOrWhiteSpace(rawText))
        {
            var t = Accounting.Services.Implementations.Ocr.ThaiTextNormalizer.Normalize(rawText);
            if (t.Contains("ใบลดหนี้", StringComparison.OrdinalIgnoreCase)) return "CreditNote";
            if (t.Contains("ใบเพิ่มหนี้", StringComparison.OrdinalIgnoreCase)) return "DebitNote";
            if (t.Contains("ใบกำกับภาษี", StringComparison.OrdinalIgnoreCase)
                || t.Contains("tax invoice", StringComparison.OrdinalIgnoreCase))
                return "Invoice";   // ใบกำกับ = เอกสารภาษี ไม่ใช่ Receipt
        }
        if (modelId?.Contains("receipt", StringComparison.OrdinalIgnoreCase) == true)
            return "Receipt";
        return azureDocType?.ToLowerInvariant() switch
        {
            "invoice" => "Invoice",
            "creditnote" => "CreditNote",
            "debitnote" => "DebitNote",
            "receipt" => "Receipt",
            _ => "Invoice",
        };
    }
}
