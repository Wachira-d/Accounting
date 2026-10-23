namespace Accounting.Models.DTOs.Tax;

/// <summary>ตรา + ลายเซ็นของหน้าพิมพ์ 50 ทวิ (<c>wht.html printCert</c>) — เซิร์ฟเวอร์ตัดสินด้วยตัวเดียวกับ PDF ทั้งสอง
/// (<c>Helpers/DocumentStampPolicy</c> + สายผู้ลงนามของใบ) · หน้าแสดงอย่างเดียว · ทีม J 2026-10-22
/// (เดิมหน้าพิมพ์ใช้ลายเซ็นของ "ผู้ที่กดพิมพ์" จากโปรไฟล์ ⇒ คนละคนกับ PDF · และไม่มีตราเลย)</summary>
/// <param name="StampImage">data URI ของรูปตรา (null = ไม่ประทับ — พิมพ์วงกลม "ประทับตรานิติบุคคล (ถ้ามี)" เหมือนเดิม)</param>
/// <param name="SignatureImage">data URI ลายเซ็นผู้จ่ายเงิน (null = เส้นจุดว่างให้เซ็นมือ)</param>
public record WhtCertPrintDecoration(
    bool ShowStamp,
    string? StampImage,
    decimal StampWidthMm,
    decimal StampHeightMm,
    decimal StampOffsetXMm,
    decimal StampOffsetYMm,
    string? SignatureImage,
    string RuleCode);
