using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ใบสแกนที่ผูกกับผู้ติดต่อ — ใช้เป็นหลักฐานว่า "ที่อยู่ของผู้ติดต่อมาจากกระดาษของสาขาไหน"</summary>
public sealed record ContactScanEvidence(string? VendorBranchCode, string? VendorAddress);

/// <summary>คำตัดสินของแถวผู้ติดต่อสำนักงานใหญ่หนึ่งแถว</summary>
/// <param name="Suspect">ควรให้บัญชีตรวจไหม</param>
/// <param name="RegistryDiffers">ทะเบียนบอกว่าที่อยู่ สนญ. อยู่คนละรหัสไปรษณีย์ · null = ยังเทียบไม่ได้ (ไม่มีทะเบียน/ไม่มีรหัสไปรษณีย์)</param>
/// <param name="BranchPaperCode">รหัสสาขาของใบสแกนที่พิมพ์ที่อยู่เดียวกับแถวนี้ (หลักฐานว่าถูกเติมจากใบสาขา) · null = ไม่พบ</param>
/// <param name="Reason">ข้อความไทยสำหรับหน้ารายงาน</param>
public sealed record HeadOfficeAddressVerdict(bool Suspect, bool? RegistryDiffers, string? BranchPaperCode, string Reason);

/// <summary>ผู้ติดต่อหนึ่งแถว (ข้อมูลเท่าที่รายงานแถวซ้ำต้องใช้)</summary>
public sealed record ContactKeyRow(Guid Id, string? Name, string? TaxId, string? BranchCode, string? CreatedBy, DateTime CreatedAt);

/// <summary>กลุ่มผู้ติดต่อที่ถือคีย์เดียวกัน (เลขภาษี 13 หลัก + สาขา) — <paramref name="Key"/> รูปเดียวกับกลุ่มของหน้าผู้ติดต่อ (<c>เลข|สาขา</c>)</summary>
public sealed record ContactDuplicateGroup(string Key, string TaxId, string BranchCode, IReadOnlyList<ContactKeyRow> Rows, int OcrCreated);

/// <summary>
/// **ตัวตัดสินของรายงาน "ผู้ติดต่อข้อมูลเสีย"** (รอบ 193 · คำตัดสินเจ้าของข้อ 19 · ทีม P Q-P5 · ทีม C คำถาม 3) —
/// รายงานอย่างเดียว <b>ไม่แก้ข้อมูล</b> (ที่อยู่ที่ถูกตัด/ถูกทับกู้ด้วย SQL ไม่ได้ และอาจเป็นค่าที่ผู้ใช้แก้เองแล้ว)
///
/// <list type="number">
/// <item><see cref="IsTruncatedAddress"/> — ที่อยู่ขึ้นต้น <c>/เลข</c> (เช่น <c>/861 …</c>): บั๊กเดิมตัด "12" ของ
///   <c>12/861</c> ทิ้งเพราะตัวตัดเศษชื่อกินเลขบ้าน (รอบ 190 ทีม P ซ่อมตัวอ่านแล้ว แต่แถวที่สร้างไปแล้วยังเสีย)</item>
/// <item><see cref="JudgeHeadOffice"/> — แถว<b>สำนักงานใหญ่</b>ที่ OCR สร้าง/แก้ อาจถูกที่อยู่ของใบ<b>สาขา</b>ทับ (ก่อนรอบ 190
///   ตัวจับผู้ติดต่อไม่ดูสาขา + <c>[Enrich]</c> เติมที่อยู่จากใบ) — หลักฐานสองชั้นที่ไม่ขึ้นกันและกัน:
///   (ก) ทะเบียนบอกรหัสไปรษณีย์ สนญ. ต่างจากของแถว (ข) มีใบสแกนของสาขาอื่นที่พิมพ์ที่อยู่เดียวกับแถวนี้</item>
/// </list>
/// <para>เทียบด้วย<b>รหัสไปรษณีย์</b>ไม่ใช่ข้อความทั้งก้อน — ทะเบียนเขียน "ตำบล ปากเกร็ด" กระดาษเขียน "ต.ปากเกร็ด"
/// ถ้าเทียบข้อความจะฟ้องทุกแถวที่ถูกต้อง (คำเตือนที่ฟ้องของถูกทุกแถว = ปิดด่านโดยไม่ตั้งใจ)</para>
/// </summary>
public static class ContactDataHygiene
{
    private static readonly Regex TruncatedHead = new(@"^\s*/\s*\d", RegexOptions.Compiled);
    private static readonly Regex Postal = new(@"(?<!\d)([1-9]\d{4})(?!\d)", RegexOptions.Compiled);

    /// <summary>ที่อยู่ขึ้นต้นด้วย "/เลข" — เลขบ้านส่วนหน้าหายไป</summary>
    public static bool IsTruncatedAddress(string? address)
        => !string.IsNullOrWhiteSpace(address) && TruncatedHead.IsMatch(address);

    /// <summary>แถวนี้ OCR สร้างหรือแก้ล่าสุด (<c>CreatedBy/UpdatedBy</c> ขึ้นต้น "OCR" — แท็กเดียวกับ
    /// <c>EnrichContactAddress</c> ที่ใช้ตัดสินว่าทับได้ไหม)</summary>
    public static bool IsOcrManaged(string? createdBy, string? updatedBy)
        => (createdBy ?? "").StartsWith("OCR", StringComparison.OrdinalIgnoreCase)
        || (updatedBy ?? "").StartsWith("OCR", StringComparison.OrdinalIgnoreCase);

    /// <summary>รหัสไปรษณีย์ 5 หลัก — ช่อง structured ก่อน แล้วค่อยหาในข้อความ (ตัวสุดท้ายของข้อความ =
    /// ท้ายที่อยู่ · เลขบ้าน/เลขภาษีที่ยาวกว่า 5 หลักไม่ถูกหยิบ)</summary>
    internal static string? PostalCodeOf(string? structuredPostal, string? address)
    {
        var s = (structuredPostal ?? "").Trim();
        if (s.Length == 5 && s.All(ch => ch >= '0' && ch <= '9')) return s;
        if (string.IsNullOrWhiteSpace(address)) return null;
        var ms = Postal.Matches(address);
        return ms.Count == 0 ? null : ms[^1].Groups[1].Value;
    }

    /// <summary>
    /// คีย์ผู้ติดต่อซ้ำ = เลขภาษี 13 หลัก (ตัวเลขล้วน) + รหัสสาขา (ว่าง ≡ 00000 สำนักงานใหญ่) — <b>ตัวเดียว</b>ของทั้งแถบเตือน/เครื่องมือรวมในหน้า
    /// ผู้ติดต่อ (<c>DocumentService.GetDuplicateContactGroupsAsync</c>) และรายงานนี้ (รอบ 200 K-5 — เดิมสูตรนี้อยู่ใน DocumentService ที่เดียว ⇒
    /// ถ้าเขียนสำเนาที่สองในรายงาน สองหน้าจะนับกลุ่มไม่ตรงกัน) · เลขไม่ครบ 13 หลัก = null (ไม่ตัดสินว่าซ้ำด้วยเลข)
    /// </summary>
    public static string? DuplicateKey(string? taxId, string? branchCode)
    {
        var tax = new string((taxId ?? "").Where(char.IsDigit).ToArray());
        if (tax.Length != 13) return null;
        var br = new string((branchCode ?? "").Where(char.IsDigit).ToArray());
        return tax + "|" + (br.Length == 0 ? TaxBranchCode.HeadOffice : br.PadLeft(5, '0'));
    }

    /// <summary>
    /// <b>ผู้ติดต่อซ้ำที่มีอยู่แล้ว</b> (รอบ 200 · คำตัดสินเจ้าของข้อ 19 · K-5) — ล็อกสร้างผู้ติดต่อจาก OCR กันแถวซ้ำ<b>ใหม่</b> แต่แถวซ้ำที่เกิดไปแล้ว
    /// (อัปโหลดพร้อมกันก่อนมีล็อก · ระบบรุ่นก่อน) ยังอยู่ ⇒ <b>รายงาน</b>ให้คนรวมเอง ไม่รวมอัตโนมัติ (การรวมย้ายเอกสาร/ประวัติทั้งหมด = ย้อนไม่ได้) และยัง
    /// <b>ไม่</b>เพิ่ม unique index จนกว่ากลุ่มเหล่านี้จะถูกจัดการ · เรียงกลุ่มที่ OCR สร้างมากก่อน แล้วตามเลขภาษี · ในกลุ่มเรียงแถวเก่าสุดก่อน
    /// </summary>
    public static IReadOnlyList<ContactDuplicateGroup> DuplicateKeyGroups(IEnumerable<ContactKeyRow> rows)
        => (rows ?? Array.Empty<ContactKeyRow>())
            .Select(r => (Key: DuplicateKey(r.TaxId, r.BranchCode), Row: r))
            .Where(x => x.Key != null)
            .GroupBy(x => x.Key!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g =>
            {
                var ordered = g.Select(x => x.Row).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToList();
                var parts = g.Key.Split('|');
                return new ContactDuplicateGroup(g.Key, parts[0], parts[1], ordered,
                    ordered.Count(r => IsOcrManaged(r.CreatedBy, null)));
            })
            .OrderByDescending(g => g.OcrCreated)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

    public static HeadOfficeAddressVerdict JudgeHeadOffice(
        string? contactBranch, string? createdBy, string? updatedBy,
        string? contactAddress, string? contactPostal,
        string? registryHeadOfficeAddress,
        IEnumerable<ContactScanEvidence>? scans)
    {
        if (!TaxBranchCode.IsHeadOffice(contactBranch))
            return new(false, null, null, "แถวนี้เป็นสาขา — ไม่อยู่ในขอบเขตรายงานนี้");
        if (!IsOcrManaged(createdBy, updatedBy))
            return new(false, null, null, "ผู้ใช้กรอก/แก้เอง — ไม่ตัดสินแทน");
        if (string.IsNullOrWhiteSpace(contactAddress))
            return new(false, null, null, "ไม่มีที่อยู่");

        bool? registryDiffers = null;
        var mine = PostalCodeOf(contactPostal, contactAddress);
        var reg = PostalCodeOf(null, registryHeadOfficeAddress);
        if (mine != null && reg != null) registryDiffers = mine != reg;

        string? branchPaper = null;
        foreach (var s in scans ?? Array.Empty<ContactScanEvidence>())
        {
            if (string.IsNullOrWhiteSpace(s.VendorBranchCode) || TaxBranchCode.IsHeadOffice(s.VendorBranchCode)) continue;
            if (!OcrBuyerAddressReader.SameAddress(s.VendorAddress, contactAddress)) continue;
            branchPaper = TaxBranchCode.Normalize(s.VendorBranchCode);
            break;
        }

        if (registryDiffers == true)
            return new(true, true, branchPaper,
                $"ทะเบียนระบุที่ตั้งสำนักงานใหญ่รหัสไปรษณีย์ {reg} แต่แถวนี้เป็น {mine}"
                + (branchPaper != null ? $" · ที่อยู่ตรงกับใบของ{TaxBranchCode.Label(branchPaper)}" : "")
                + " — ตรวจว่าที่อยู่สาขาทับแถวสำนักงานใหญ่หรือไม่");
        // ทะเบียนยืนยันว่ารหัสไปรษณีย์ตรง = สาขาอยู่ที่เดียวกับ สนญ. ได้ (พบบ่อย) ⇒ ไม่ฟ้อง
        if (branchPaper != null && registryDiffers == null)
            return new(true, null, branchPaper,
                $"ที่อยู่ของแถวสำนักงานใหญ่ตรงกับที่อยู่บนใบของ{TaxBranchCode.Label(branchPaper)} · ทะเบียนยังยืนยันไม่ได้"
                + " — ตรวจว่าเป็นที่อยู่ของสำนักงานใหญ่จริงไหม");
        return new(false, registryDiffers, branchPaper,
            registryDiffers == false ? "รหัสไปรษณีย์ตรงทะเบียน" : "ไม่มีหลักฐานว่าถูกทับ");
    }
}
