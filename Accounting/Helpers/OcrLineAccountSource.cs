namespace Accounting.Helpers;

/// <summary>ผังบัญชีของบรรทัดเอกสารที่สร้างจากสแกน **มาจากชั้นไหน**</summary>
public enum OcrLineAccountOrigin
{
    /// <summary>ไม่มีชั้นไหนให้คำตอบ — บรรทัดจะไม่มีผังบัญชี (ผู้ใช้ต้องเลือกเอง)</summary>
    None = 0,
    /// <summary>ผังบัญชีระดับ<b>หัวใบ</b>ที่ตัวจัดหมวด/AI เลือกให้ทั้งใบ — ชั้นหยาบที่สุด</summary>
    ScanHeader = 1,
    /// <summary>ผังบัญชีที่ผูกกับ<b>บรรทัดนั้น</b> (สินค้าใน master · ตัวเรียนรายบรรทัด ·
    /// ค่าที่ผู้ใช้แก้ไว้) — แม่นกว่าหัวใบเสมอเพราะรู้ว่าบรรทัดนี้คือ "อะไร"</summary>
    ScannedLine = 2,
    /// <summary>ผังบัญชีของ<b>บรรทัดใบสั่งซื้อ</b>ที่ผูกไว้ — ชนะทุกชั้น เพราะการรับของ
    /// ต้องลงบัญชีตรงกับตอนสั่งซื้อ (ไม่งั้น 3-way match/ยอดคงเหลือ PO เพี้ยน)</summary>
    PurchaseOrderLine = 3,
}

/// <summary>
/// **ลำดับชั้น "บรรทัดนี้ลงบัญชีอะไร" ตัวเดียวของเส้น OCR** (pure, ไม่มี I/O)
///
/// ═══ ที่มา (ผลตรวจ 2026-09-18 · D3-1) ═══
/// <para>ไปป์ไลน์ serialize บรรทัดลง <c>ExtractedItemsJson</c> ตั้งแต่ต้นทาง — **ก่อน**
/// ตัวเทียบสินค้าใน master และตัวเรียนรายบรรทัดจะเขียน <c>SuggestedAccountCode</c> ⇒
/// เส้นสร้างเอกสารอ่านกลับมาได้ <c>null</c> ทุกบรรทัด แล้วตกไปใช้ผังบัญชีระดับหัวใบ
/// ⇒ <b>ชั้นหลักฐานที่แข็งที่สุด (สินค้าที่ผูกผังบัญชีไว้แล้ว) ไม่เคยถึงเอกสาร</b>
/// โดยที่ trace บนหน้าจอเขียนว่า "[Product] รายการ → บัญชี" ไปแล้ว</para>
///
/// <para>คลาสนี้ทำให้ลำดับชั้นเป็น <b>ข้อมูลที่ทดสอบได้</b> แทนที่จะเป็น <c>??</c> เรียงกัน
/// กลางเมธอดสร้างบรรทัด — และทำให้ "ใบไหนได้บัญชีจากชั้นไหน" ตอบได้ด้วยเทสต์</para>
/// </summary>
public static class OcrLineAccountSource
{
    /// <param name="purchaseOrderLineAccountId">ผังบัญชีของบรรทัด PO ที่ผูกไว้ (ถ้ามี)</param>
    /// <param name="scannedLineAccountId">ผังบัญชีที่แปลงจาก <c>SuggestedAccountCode</c> ของบรรทัดนั้น</param>
    /// <param name="scanHeaderAccountId">ผังบัญชีระดับหัวใบของสแกน (ตัวสำรองสุดท้าย)</param>
    public static (Guid? AccountId, OcrLineAccountOrigin From) Resolve(
        Guid? purchaseOrderLineAccountId, Guid? scannedLineAccountId, Guid? scanHeaderAccountId)
    {
        if (purchaseOrderLineAccountId.HasValue)
            return (purchaseOrderLineAccountId, OcrLineAccountOrigin.PurchaseOrderLine);
        if (scannedLineAccountId.HasValue)
            return (scannedLineAccountId, OcrLineAccountOrigin.ScannedLine);
        if (scanHeaderAccountId.HasValue)
            return (scanHeaderAccountId, OcrLineAccountOrigin.ScanHeader);
        return (null, OcrLineAccountOrigin.None);
    }

    /// <summary>คำอธิบายภาษาไทยของชั้น — ใช้ทั้ง trace และหน้ารีวิว
    /// (ห้ามให้แต่ละที่แต่งคำเอง = สำเนามือชุดที่สอง)</summary>
    public static string Explain(OcrLineAccountOrigin origin) => origin switch
    {
        OcrLineAccountOrigin.PurchaseOrderLine => "ผังบัญชีของบรรทัดใบสั่งซื้อที่ผูกไว้",
        OcrLineAccountOrigin.ScannedLine => "ผังบัญชีของรายการบรรทัดนั้น (สินค้าใน master/ที่เคยเรียนไว้)",
        OcrLineAccountOrigin.ScanHeader => "ผังบัญชีระดับหัวใบ (ตัวจัดหมวดของทั้งใบ)",
        _ => "ยังไม่มีผังบัญชี — ต้องเลือกเอง",
    };
}
