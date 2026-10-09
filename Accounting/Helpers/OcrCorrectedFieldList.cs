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
    /// <para><b>ช่อง WHT สามช่อง</b> (รอบ 200 · คำตัดสินเจ้าของข้อ 19 · K-10) เมื่อ <paramref name="before"/> มี <see cref="OcrCorrectionBaseline.Wht"/>:
    /// หน้ารีวิวส่ง <c>hasWht</c> (true/false เสมอ) · <c>whtRate</c> · <c>whtIncomeTypeCode</c> ("" เมื่อไม่เลือก) มาทุกครั้ง ⇒ เดิม "HasWht" เข้ารายการทุกใบบนเว็บ
    /// ⇒ <c>OcrWhtLearningScope</c> ตีเป็น <c>UserEdited</c> ⇒ ประวัติ WHT ของผู้ขายถูกสอนด้วยค่าที่ไม่มีใครแตะ (วงจรสอนตัวเองที่ D3-2 ปิดไว้ กลับมาทางประตูนี้).
    /// ตอนนี้นับเฉพาะเมื่อค่า<b>เปลี่ยนจากที่สแกนเก็บไว้</b> (<see cref="WhtChanged"/>) — รูปแบบเดียวกับรหัสสาขา (K-1) ·
    /// <c>Wht</c> null = กติกาเดิม (ส่งมา = แก้) สำหรับผู้เรียกที่ไม่มีค่าก่อนแก้</para>
    ///
    /// <para>ช่องอื่นยังใช้กติกาเดิม (ส่งมา = แก้)</para></summary>
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
        // ที่อยู่ผู้ขาย (รอบ 200 · K-9): หน้ารีวิวส่ง vendorAddress ทุกครั้งที่ช่องมีค่า ⇒ ตัวอ่าน "ผู้ใช้พิมพ์ที่อยู่เอง" (ที่อยู่แถวสาขาใหม่
        // ในเส้นสร้างเอกสาร) ต้องนับเฉพาะเมื่อเปลี่ยนจริง — กติกาเดียวกับสาขา/WHT
        if (before?.VendorAddress is not OcrTextBaseline addr) Add("VendorAddress", c.VendorAddress);
        else if (c.VendorAddress != null && TextChanged(c.VendorAddress, addr.Value)) fields.Add("VendorAddress");
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
        // ผังบัญชี Dr/Cr (ฝ่ายค้านรอบสาม f1690d11): หน้ารีวิวส่งทั้งสอง dropdown ทุกครั้ง ⇒ นับเฉพาะเมื่อเปลี่ยนจากคู่ที่<b>จอแสดง</b>
        // (Accounts baseline · null = กติกาเดิม "ส่งมา = แก้")
        if (before?.Accounts is not OcrAccountsBaseline acc)
        {
            Add("DebitAccountCode", c.DebitAccountCode);
            Add("CreditAccountCode", c.CreditAccountCode);
        }
        else
        {
            if (AccountChanged(c.DebitAccountCode, acc.DebitCode)) fields.Add("DebitAccountCode");
            if (AccountChanged(c.CreditAccountCode, acc.CreditCode)) fields.Add("CreditAccountCode");
        }
        if (before?.Wht is not OcrWhtBaseline wht)
        {
            Add("HasWht", c.HasWht);
            Add("WhtRate", c.WhtRate);
            Add("WhtIncomeTypeCode", c.WhtIncomeTypeCode);
        }
        else
        {
            if (c.HasWht is bool hasWht && hasWht != wht.HasWht) fields.Add("HasWht");
            if (c.WhtRate is decimal rate && rate != (wht.WhtRate ?? 0m)) fields.Add("WhtRate");
            if (c.WhtIncomeTypeCode != null && WhtChanged(c.WhtIncomeTypeCode, wht.WhtIncomeTypeCode))
                fields.Add("WhtIncomeTypeCode");
        }
        Add("PaymentTermsDays", c.PaymentTermsDays);
        // Notes = เหตุผลทางธุรกิจที่ผู้ใช้พิมพ์เอง — ระบบไม่เคยเติมให้อยู่แล้ว
        // การกรอกจึงไม่ใช่ "การแก้สิ่งที่ OCR ทำผิด" ⇒ ไม่นับเข้าตัวชี้วัด
        return fields.ToArray();
    }

    /// <summary>
    /// **"ผู้ใช้พิมพ์ที่อยู่ผู้ขายเอง" ตามกติกาใหม่ไหม** (รอบ 200 ทีม K2 · คำตัดสินข้อ 29 · ฝ่ายค้าน K R3) — ผู้เขียน <c>OcrScanResult.VendorAddressUserTyped</c>
    /// <para>นับเฉพาะเมื่อคำขอนี้มี baseline ของที่อยู่ (<see cref="OcrCorrectionBaseline.VendorAddress"/>) <b>และ</b> <see cref="From"/> นับ "VendorAddress" —
    /// ไม่มี baseline = กติกาเดิม "ส่งมา = แก้" ⇒ แยกไม่ได้ว่าคนพิมพ์จริง ⇒ <c>false</c> (ไม่รู้ ≠ ผ่าน · DOCTRINE §1) · ค่าที่เคยเป็น true ไม่ถูกล้างด้วยคำขอ
    /// ถัดไป (ผู้เรียก OR กับค่าเดิม — การพิมพ์ครั้งก่อนยังเป็นหลักฐาน)</para>
    /// </summary>
    public static bool VendorAddressTyped(IReadOnlyCollection<string> correctedFields, OcrCorrectionBaseline? before)
        => before?.VendorAddress != null && correctedFields.Contains("VendorAddress", StringComparer.Ordinal);

    /// <summary>
    /// **ค่าชื่อ/ที่อยู่ผู้ขายที่ส่งมาควรถูกจำเป็น "คำแก้ของคน" ในคลัง known-good ไหม** (รอบ 201 ทีม OC · A-OC1 · team-K Q2)
    /// <para>หน้ารีวิวส่ง <c>vendorName</c>/<c>vendorAddress</c> กลับมา<b>ทุกครั้ง</b>ที่ช่องมีค่า ⇒ เดิมทุกการกดบันทึกเขียน
    /// <c>Source = "UserCorrection"</c> (และเพิ่ม <c>ConfirmedCount</c>) ทั้งที่ผู้ใช้ไม่ได้แตะ ⇒ ค่าที่ OCR อ่านผิดแล้วผู้ใช้ไม่สังเกต
    /// กลายเป็น "คำแก้ของคน" ที่ชนะ Azure ถาวร — ทรงเดียวกับ K-10 (WHT) · หลัก baseline เดียวกับ K-1/K-10/ข้อ 19/ข้อ 29
    /// (ไม่ใช่ทางแยกใหม่): จำเฉพาะเมื่อค่า<b>เปลี่ยนจากที่สแกนเก็บไว้ก่อนรับคำแก้</b> (<see cref="TextChanged"/>) และไม่ว่าง
    /// (ล้างช่อง = ไม่มีค่าให้จำ)</para>
    /// </summary>
    /// <param name="submitted">ค่าที่หน้าเว็บส่งมา (null = ไม่ได้แตะ)</param>
    /// <param name="scannedBefore">ค่าบนแถวสแกน<b>ก่อน</b>รับคำแก้นี้</param>
    public static bool ShouldRememberKnownGood(string? submitted, string? scannedBefore)
        => !string.IsNullOrWhiteSpace(submitted) && TextChanged(submitted, scannedBefore);

    /// <summary>
    /// **ผู้ใช้เปลี่ยนเลขผู้เสียภาษีผู้ขายจริงไหม** (รอบ 201 ทีม OC · ฝ่ายค้าน OCX-1 · คำตัดสินข้อ 96) — ผู้เขียน <c>OcrScanResult.VendorTaxIdUserChanged</c>
    /// <para>หน้ารีวิวส่ง <c>vendorTaxId</c> กลับมา<b>ทุกครั้ง</b> (<c>_buildReviewCorrection</c>) และปุ่มสร้างเอกสารบันทึกคำแก้ก่อนสร้างเสมอ
    /// (<c>_persistReviewEdits</c>) ⇒ "VendorTaxId" ใน <see cref="From"/> (กติกา "ส่งมา = แก้") ติดทุกใบบนเว็บ ⇒ ด่าน C-23 "ผู้ใช้แตะเลข" ไม่กันอะไร ·
    /// นับเฉพาะเมื่อ<b>ตัวเลข</b>ต่างจากค่าบนแถวสแกนก่อนรับคำแก้ (เลขเดิมพิมพ์มีขีด/เว้นวรรค = ไม่เปลี่ยน) และเลขใหม่ไม่ว่าง (ล้างช่อง = ไม่ใช่นิติบุคคลอื่น)</para>
    /// </summary>
    public static bool VendorTaxIdTyped(string? submitted, string? storedBeforeCorrection)
    {
        if (submitted == null) return false;
        var now = ThaiTaxId.Normalize(submitted);
        return now.Length > 0 && now != ThaiTaxId.Normalize(storedBeforeCorrection);
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

    /// <summary>รหัสประเภทเงินได้ที่ส่งมา ≠ ค่าที่เก็บไว้ไหม — ว่าง/ช่องว่าง ≡ ไม่ระบุ ("" คือค่าที่หน้าเว็บส่งเมื่อไม่ได้เลือก) ·
    /// เทียบหลังตัดช่องว่างแบบไม่สนตัวพิมพ์ (รหัสเป็นตัวอักษร+เลข เช่น "40(2)")</summary>
    internal static bool WhtChanged(string? submitted, string? stored)
        => !string.Equals((submitted ?? "").Trim(), (stored ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>ข้อความอิสระที่ส่งมา ≠ ค่าที่เก็บไว้ไหม — ว่าง ≡ ไม่มีค่า · ตัดช่องว่างหัวท้ายและยุบช่องว่างซ้อน (hydrate ลง input แล้วส่งกลับ
    /// ต้องไม่นับว่าแก้) · ตัวพิมพ์เล็ก/ใหญ่นับว่าต่าง (ผู้ใช้แก้ตัวสะกดได้)</summary>
    /// <summary>ผู้ใช้<b>เปลี่ยน</b>ผังบัญชีช่องนี้จากที่จอแสดงไหม — ว่าง/ไม่ส่ง = ไม่เปลี่ยน · รหัสเทียบตรงตัวหลังตัดช่องว่างหัวท้าย
    /// (ตัวเดียวของ "ผู้ใช้ส่งฝั่งนี้" ใน <c>SubmitCorrectionAsync</c> และ <see cref="From"/>)</summary>
    public static bool AccountChanged(string? submitted, string? shown)
        => !string.IsNullOrWhiteSpace(submitted)
           && !string.Equals(submitted.Trim(), (shown ?? "").Trim(), StringComparison.Ordinal);

    internal static bool TextChanged(string? submitted, string? stored)
        => !string.Equals(Squash(submitted), Squash(stored), StringComparison.Ordinal);

    private static string Squash(string? v)
        => string.Join(' ', (v ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
/// ออกจาก "หน้าเว็บส่งค่าเดิมกลับมา" (รอบ 197 ฝ่ายค้าน K-1: รหัสสาขาสองฝั่ง · รอบ 200 K-10: ช่อง WHT ผ่าน <paramref name="Wht"/>)</summary>
public sealed record OcrCorrectionBaseline(string? VendorBranchCode, string? BuyerBranchCode, OcrWhtBaseline? Wht = null,
    OcrTextBaseline? VendorAddress = null, OcrAccountsBaseline? Accounts = null);

/// <summary>คู่ผังบัญชี Dr/Cr ที่หน้ารีวิว<b>แสดง</b>ก่อนรับคำแก้ (หลังด่านวางฝั่ง <see cref="OcrAccountPlacement.ResolveStored"/> —
/// ค่าเดียวกับที่ฟอร์ม hydrate ลง dropdown)</summary>
public sealed record OcrAccountsBaseline(string? DebitCode, string? CreditCode);

/// <summary>ค่าข้อความหนึ่งช่องของสแกนก่อนรับคำแก้ (null ทั้งตัว = ผู้เรียกไม่มีค่าก่อนแก้ ⇒ กติกาเดิม "ส่งมา = แก้")</summary>
public sealed record OcrTextBaseline(string? Value);

/// <summary>ช่อง WHT ของสแกนก่อนรับคำแก้ (<c>OcrScanResult.HasWht/WhtRate/WhtIncomeTypeCode</c>) — ค่าเดียวกับที่หน้ารีวิว hydrate ลงฟอร์ม</summary>
public sealed record OcrWhtBaseline(bool HasWht, decimal? WhtRate, string? WhtIncomeTypeCode);
