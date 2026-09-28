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
    /// "ไม่แตะ" อยู่แล้วทั้งชุด จึงเป็นสัญญาณที่ตรงความหมายที่สุด</para>
    ///
    /// <para><b>ยกเว้นรหัสสาขาสองฝั่ง</b> (รอบ 197 ฝ่ายค้าน K-1) เมื่อผู้เรียกส่ง <paramref name="before"/> (ค่าที่สแกนเก็บไว้
    /// <b>ก่อน</b>รับคำแก้): หน้ารีวิวส่งสองช่องนี้มาทุกครั้ง ⇒ "ส่งค่ามา" กลายเป็น "ผู้ใช้แก้" เสมอ ⇒ ด่าน "หลักฐานสาขาอ่อน ⇒
    /// ไม่สร้างผู้ติดต่อ" (<see cref="OcrVendorBranchContact.IsReliableBranch"/> ที่อ่าน "VendorBranchCode" จากรายการนี้) ไม่เคยกันได้
    /// บนเว็บ. นับเฉพาะเมื่อค่าเปลี่ยนจริง (<see cref="BranchChanged"/>) หรือผู้ใช้พิมพ์ยืนยัน
    /// (<see cref="OcrCorrectionRequest.VendorBranchConfirmed"/>) · <paramref name="before"/> null = กติกาเดิม (ส่งมา = แก้)</para>
    ///
    /// <para>ช่องอื่นยังใช้กติกาเดิม — ช่อง WHT มีบั๊กทรงเดียวกัน (หน้าเว็บส่ง hasWht/whtIncomeTypeCode ทุกครั้ง) แต่
    /// <c>OcrWhtLearningScope</c> นิยาม "ยืนยัน" ว่าเป็นหลักฐานโดยตั้งใจ ⇒ เปลี่ยนต้องให้เจ้าของตัดสิน (backlog K-10 ใน
    /// <c>erp-review/2026-09-25/makro-branch/review197.md</c>)</para></summary>
    public static string[] From(OcrCorrectionRequest c, OcrCorrectionBaseline? before = null)
    {
        var fields = new List<string>();
        void Add(string name, object? value) { if (value != null) fields.Add(name); }

        Add("DocumentType", c.DocumentType);
        Add("TargetDocumentType", c.TargetDocumentType);
        Add("OurRole", c.OurRole);
        Add("VendorName", c.VendorName);
        Add("VendorTaxId", c.VendorTaxId);
        if (before == null) Add("VendorBranchCode", c.VendorBranchCode);
        else if (c.VendorBranchConfirmed == true
                 || (c.VendorBranchCode != null && BranchChanged(c.VendorBranchCode, before.VendorBranchCode)))
            fields.Add("VendorBranchCode");
        Add("VendorAddress", c.VendorAddress);
        Add("BuyerName", c.BuyerName);
        Add("BuyerTaxId", c.BuyerTaxId);
        if (before == null) Add("BuyerBranchCode", c.BuyerBranchCode);
        else if (c.BuyerBranchCode != null && BranchChanged(c.BuyerBranchCode, before.BuyerBranchCode))
            fields.Add("BuyerBranchCode");
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

    /// <summary>รหัสสาขาที่ส่งมา ≠ ค่าที่เก็บไว้ไหม — เทียบหลัง normalize 5 หลัก ("5" ≡ "00005") · ว่างทั้งคู่ = เท่ากัน ·
    /// ว่าง ↔ "00000" = <b>ต่างกัน</b> (ว่าง = "ไม่รู้" ≠ สำนักงานใหญ่ — ผู้ใช้เติม 00000 ลงช่องว่างคือการตอบ) ·
    /// ผิดรูป ("8A") เทียบข้อความตรงตัว</summary>
    internal static bool BranchChanged(string? submitted, string? stored)
    {
        var a = Canon(submitted);
        var b = Canon(stored);
        return !string.Equals(a, b, StringComparison.Ordinal);

        static string Canon(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return "";
            return TaxBranchCode.TryNormalize(v, out var code, out _) && code != null ? code : v.Trim();
        }
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

/// <summary>ค่าที่สแกนเก็บไว้<b>ก่อน</b>รับคำแก้ — ตัวป้อนของ <see cref="OcrCorrectedFieldList.From"/> ให้แยก "ผู้ใช้เปลี่ยนค่า"
/// ออกจาก "หน้าเว็บส่งค่าเดิมกลับมา" (รอบ 197 ฝ่ายค้าน K-1 · ใช้กับรหัสสาขาสองฝั่งเท่านั้น)</summary>
public sealed record OcrCorrectionBaseline(string? VendorBranchCode, string? BuyerBranchCode);
