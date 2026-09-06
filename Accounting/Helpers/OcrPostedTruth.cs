namespace Accounting.Helpers;

/// <summary>ค่าของเอกสาร "ตามที่ลงบัญชีจริง" เทียบกับ "ที่สแกนอ่านได้"
/// — ทุกช่องเป็น nullable โดยตั้งใจ: <c>null</c> แปลว่า <b>ไม่แตะ</b></summary>
/// <param name="VendorName">ชื่อคู่ค้าที่ผูกกับเอกสารจริง</param>
/// <param name="VendorTaxId">เลขผู้เสียภาษีของคู่ค้าที่ผูกจริง</param>
/// <param name="VendorBranchCode">รหัสสาขาที่ลงในเอกสาร (§86/4)</param>
/// <param name="DocumentNumber">เลขใบกำกับของผู้ขายที่ลงจริง (ฝั่งซื้อเท่านั้น)</param>
/// <param name="DocumentDate">วันที่เอกสารที่ลงจริง (= tax point)</param>
/// <param name="SubTotal">ยอดก่อน VAT ที่ลงจริง</param>
/// <param name="VatAmount">VAT ที่ลงจริง</param>
/// <param name="TotalAmount">ยอดรวมที่ลงจริง</param>
/// <param name="TargetDocumentType">ชนิดเอกสารที่เกิดขึ้นจริง</param>
public readonly record struct OcrPostedSnapshot(
    string? VendorName = null,
    string? VendorTaxId = null,
    string? VendorBranchCode = null,
    string? DocumentNumber = null,
    DateTime? DocumentDate = null,
    decimal? SubTotal = null,
    decimal? VatAmount = null,
    decimal? TotalAmount = null,
    string? TargetDocumentType = null)
{
    /// <summary>มีอย่างน้อยหนึ่งช่องที่ต้องเปลี่ยนไหม</summary>
    public bool HasChanges =>
        VendorName != null || VendorTaxId != null || VendorBranchCode != null
        || DocumentNumber != null || DocumentDate != null
        || SubTotal != null || VatAmount != null || TotalAmount != null
        || TargetDocumentType != null;
}

/// <summary>
/// **ปิดลูปการเรียนรู้ที่ "เอกสารที่ลงจริง" ไม่ใช่ "ช่องบนสแกน"** (pure, ไม่มี I/O)
///
/// ═══ ปัญหาที่แก้ (สถาปัตยกรรมเป้าหมาย D3) ═══
/// ตัวอย่างที่นักเรียนเรียนทั้งหมดมาจาก <c>OcrScanResult</c> ณ **ตอนกดสร้างเอกสาร**
/// (ยัง Draft อยู่) — ถ้าผู้ใช้เปิด Draft แล้วแก้ชื่อผู้ขาย/วันที่/ยอด/รหัสสาขา
/// ก่อนอนุมัติ **ไม่มีใครบอกนักเรียนเลย** ⇒ ระบบเรียนจากคำตอบที่ผู้ใช้ปฏิเสธไปแล้ว
/// และครั้งหน้าก็เติมค่าเดิมผิดซ้ำ — ตรงข้ามกับเจตนาของกฎเหล็ก #1 ทุกประการ
///
/// <para>ตัวนี้บอกว่า "ช่องไหนของสแกนควรถูกอัปเดตให้ตรงกับสิ่งที่ลงบัญชีจริง"
/// โดยยึดกติกาสองข้อของไฟล์ CLAUDE.md อย่างเคร่งครัด:</para>
/// <list type="number">
/// <item><b>ห้ามลบของที่มีอยู่ด้วยค่าว่าง</b> — เอกสารที่ไม่ได้กรอกช่องนั้น
///   (เลขใบกำกับยังไม่มา · ยอดเป็น 0) ต้องไม่ไปล้างค่าที่สแกนอ่านมาได้
///   ("ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ" — ใช้กับการ<b>ลบ</b>ด้วย ไม่ใช่แค่การเติม)</item>
/// <item><b>ต่างจริงถึงจะเปลี่ยน</b> — เทียบสตริงแบบตัดช่องว่าง/ไม่สนตัวพิมพ์ ·
///   จำนวนเงินเผื่อ 1 สตางค์ · วันที่เทียบเฉพาะส่วนวัน ⇒ การอนุมัติซ้ำ
///   (re-approve) จะไม่สร้างแถวเรียนรู้ปลอมเพิ่มทุกครั้ง</item>
/// </list>
/// </summary>
public static class OcrPostedTruth
{
    /// <summary>ค่าเผื่อปัดเศษของจำนวนเงิน (บาท)</summary>
    public const decimal MoneyTolerance = 0.01m;

    /// <param name="scan">ค่าที่อยู่บนแถวสแกนตอนนี้</param>
    /// <param name="posted">ค่าตามเอกสารที่อนุมัติแล้ว (ช่องที่ไม่รู้ = null/0)</param>
    /// <returns>เฉพาะช่องที่ต้องเปลี่ยน — ช่องอื่นเป็น <c>null</c> (ไม่แตะ)</returns>
    public static OcrPostedSnapshot Diff(OcrPostedSnapshot scan, OcrPostedSnapshot posted) => new(
        VendorName: PickText(scan.VendorName, posted.VendorName),
        VendorTaxId: PickText(scan.VendorTaxId, posted.VendorTaxId),
        VendorBranchCode: PickText(scan.VendorBranchCode, posted.VendorBranchCode),
        DocumentNumber: PickText(scan.DocumentNumber, posted.DocumentNumber),
        DocumentDate: PickDate(scan.DocumentDate, posted.DocumentDate),
        SubTotal: PickMoney(scan.SubTotal, posted.SubTotal),
        VatAmount: PickMoney(scan.VatAmount, posted.VatAmount),
        TotalAmount: PickMoney(scan.TotalAmount, posted.TotalAmount),
        TargetDocumentType: PickText(scan.TargetDocumentType, posted.TargetDocumentType));

    private static string? PickText(string? current, string? postedValue)
    {
        if (string.IsNullOrWhiteSpace(postedValue)) return null;      // ไม่รู้ = ไม่แตะ
        var p = postedValue.Trim();
        if (!string.IsNullOrWhiteSpace(current)
            && string.Equals(current.Trim(), p, StringComparison.OrdinalIgnoreCase)) return null;
        return p;
    }

    private static DateTime? PickDate(DateTime? current, DateTime? postedValue)
    {
        if (postedValue is not DateTime p) return null;
        if (current is DateTime c && c.Date == p.Date) return null;
        return p.Date;
    }

    /// <summary>ยอด 0 = "เอกสารไม่ได้บอก" ไม่ใช่ "ยอดเป็นศูนย์จริง" — จึงไม่แตะ
    ///
    /// <para>ยกเว้น VAT ที่เป็น 0 ได้จริง (ใบ 0%/ยกเว้น) แต่การแยกสองความหมายนี้
    /// ต้องอาศัยบริบทที่ helper นี้ไม่มี ⇒ ผู้เรียกส่ง <c>null</c> มาแทน 0 เมื่อ
    /// ต้องการให้ "ไม่แตะ" และส่ง 0 มาจริง ๆ เมื่อยืนยันว่าเป็นศูนย์</para></summary>
    private static decimal? PickMoney(decimal? current, decimal? postedValue)
    {
        if (postedValue is not decimal p) return null;
        if (p < 0m) return null;
        if (current is decimal c && Math.Abs(c - p) <= MoneyTolerance) return null;
        return p;
    }
}
