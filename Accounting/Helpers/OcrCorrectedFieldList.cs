using Accounting.Models.DTOs.Ocr;

namespace Accounting.Helpers;

/// <summary>
/// **"ผู้ใช้แก้ช่องไหนบ้าง"** (pure, ไม่มี I/O) — ตัวป้อนของ KPI คู่
/// (<see cref="OcrQualityKpi"/>) และของการจัดลำดับงานปรับปรุงไปป์ไลน์
///
/// <para>⚠️ ตัวชี้วัดคุณภาพเดิมนับ "ใบที่ถูกแก้" จาก <c>OcrScanResult.UpdatedAt != null</c>
/// ซึ่งขยับทุกครั้งที่ <b>ระบบเอง</b> บันทึกแถว (จบการสแกน · ผูกเอกสารที่สร้าง ·
/// sync ตอนอนุมัติ) ⇒ อัตราการแก้ ≈ 100% ทุก tenant ตลอดกาล = ตัวเลขที่อ่านไม่ได้
/// (ผลตรวจ 2026-09-06 · T5). ตัวนี้ตอบเฉพาะ "ช่องที่<b>คน</b>ส่งค่ามาแก้"</para>
///
/// <para>รู้ว่า "แก้ตรงไหนบ่อย" มีค่ากว่ารู้ว่า "ถูกแก้" — มันบอกว่าควรไปปรับปรุง
/// ตัวสกัดช่องไหนก่อน แทนที่จะเดา</para>
/// </summary>
public static class OcrCorrectedFieldList
{
    /// <summary>ชื่อช่องที่ผู้ใช้ส่งค่ามาแก้ในคำขอนี้ (ว่าง = ไม่ได้แก้อะไรเลย)
    ///
    /// <para>นับเฉพาะช่องที่ <b>ไม่เป็น null</b> — record นี้ใช้ <c>null</c> แปลว่า
    /// "ไม่แตะ" อยู่แล้วทั้งชุด จึงเป็นสัญญาณที่ตรงความหมายที่สุด</para></summary>
    public static string[] From(OcrCorrectionRequest c)
    {
        var fields = new List<string>();
        void Add(string name, object? value) { if (value != null) fields.Add(name); }

        Add("DocumentType", c.DocumentType);
        Add("TargetDocumentType", c.TargetDocumentType);
        Add("OurRole", c.OurRole);
        Add("VendorName", c.VendorName);
        Add("VendorTaxId", c.VendorTaxId);
        Add("VendorBranchCode", c.VendorBranchCode);
        Add("VendorAddress", c.VendorAddress);
        Add("BuyerName", c.BuyerName);
        Add("BuyerTaxId", c.BuyerTaxId);
        Add("BuyerBranchCode", c.BuyerBranchCode);
        Add("BuyerAddress", c.BuyerAddress);
        Add("DocumentNumber", c.DocumentNumber);
        Add("DocumentDate", c.DocumentDate);
        Add("SubTotal", c.SubTotal);
        Add("VatAmount", c.VatAmount);
        Add("TotalAmount", c.TotalAmount);
        Add("ExpenseCategory", c.ExpenseCategory);
        Add("DebitAccountCode", c.DebitAccountCode);
        Add("CreditAccountCode", c.CreditAccountCode);
        Add("HasWht", c.HasWht);
        Add("WhtRate", c.WhtRate);
        Add("WhtIncomeTypeCode", c.WhtIncomeTypeCode);
        Add("PaymentTermsDays", c.PaymentTermsDays);
        // Notes = เหตุผลทางธุรกิจที่ผู้ใช้พิมพ์เอง — ระบบไม่เคยเติมให้อยู่แล้ว
        // การกรอกจึงไม่ใช่ "การแก้สิ่งที่ OCR ทำผิด" ⇒ ไม่นับเข้าตัวชี้วัด
        return fields.ToArray();
    }

    /// <summary>รวมรายการเดิมกับรายการใหม่แบบไม่ซ้ำ เรียงตามตัวอักษร
    /// (แก้หลายรอบต้องไม่ทำให้ช่องเดิมถูกนับซ้ำ)</summary>
    public static string Merge(string? existingCsv, IEnumerable<string> newFields)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(existingCsv))
            foreach (var f in existingCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(f);
        foreach (var f in newFields) set.Add(f);
        return string.Join(",", set);
    }
}
