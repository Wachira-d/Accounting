namespace Accounting.Helpers;

/// <summary>ข้อมูลสาขาเท่าที่ resolver ต้องใช้ — แยกจาก entity เพื่อให้เทสต์
/// เรียกได้โดยไม่ต้องแตะ EF (แบบเดียวกับ <see cref="DocumentBrandView"/>)</summary>
/// <param name="TaxBranchCode">รหัสสาขาสรรพากรในทะเบียน (อาจ null ถ้ายังไม่กรอก)</param>
/// <param name="Name">ชื่อสาขา</param>
/// <param name="NameEn">ชื่อสาขาภาษาอังกฤษ</param>
/// <param name="Address">ที่อยู่สถานประกอบการที่ประกอบเป็นบรรทัดเดียวแล้ว
/// (ว่าง/null = สาขานี้ไม่ได้กรอกที่อยู่ → ใช้ที่อยู่บริษัทตามเดิม)</param>
public sealed record DocumentBranchView(
    string? TaxBranchCode,
    string? Name = null,
    string? NameEn = null,
    string? Address = null,
    string? Phone = null,
    string? Email = null);

/// <summary>ผลลัพธ์: สถานประกอบการที่ต้องพิมพ์บนเอกสารใบนี้</summary>
/// <param name="Code">รหัส 5 หลักที่ใช้จริง (ส่งเข้า XML e-Tax ได้ทันที)</param>
/// <param name="Label">ถ้อยคำบนกระดาษ — "สำนักงานใหญ่" / "สาขาที่ 3 (เชียงใหม่)"</param>
/// <param name="Address">ที่อยู่ที่ควรพิมพ์ — null = ผู้เรียกใช้ที่อยู่บริษัทตามเดิม</param>
/// <param name="Phone">เบอร์ของสาขา — null = ใช้ของบริษัท</param>
/// <param name="Email">อีเมลของสาขา — null = ใช้ของบริษัท</param>
/// <param name="FromBranchRegistry">true = ใบนี้ผูกกับสาขาในทะเบียนจริง
/// (false = กิจการสาขาเดียว ใช้ค่าบนตัวบริษัทเหมือนเดิมทุกอย่าง)</param>
public sealed record IssuerBranch(
    string Code,
    string Label,
    string? Address,
    string? Phone,
    string? Email,
    bool FromBranchRegistry);

/// <summary>
/// **ตัวตัดสินเดียว** ว่าเอกสารใบหนึ่งออกจากสถานประกอบการไหน — รหัสสาขาอะไร
/// ที่อยู่ไหนที่ต้องพิมพ์ (ป.รัษฎากร §86/4(2) + ป.86/2542: ที่อยู่บนใบกำกับต้องเป็น
/// ที่ตั้งสถานประกอบการที่ออกใบ ไม่ใช่สำนักงานใหญ่เสมอไป)
///
/// ═══ กติกาที่ห้ามหลุด ═══
/// **กิจการสาขาเดียวต้องไม่รู้สึกถึงความเปลี่ยนแปลงใด ๆ** — เมื่อ
/// <paramref name="branch"/> เป็น null และไม่มี snapshot ผลลัพธ์ต้องเท่ากับค่าบน
/// ตัวบริษัท (`Company.BranchCode` / `Company.BranchName`) เป๊ะ ๆ เหมือนก่อนมี
/// ฟีเจอร์นี้ และ <c>Address/Phone/Email</c> ต้องเป็น null เพื่อให้ผู้เรียกใช้ของเดิม
///
/// ═══ ลำดับความสำคัญของ "รหัส" ═══
/// 1. <c>snapshotCode</c> (`Document.IssuerBranchCode`) — ตรึงตอนอนุมัติ
///    **ชนะทุกอย่างเสมอ**: แก้ทะเบียนสาขาวันนี้ต้องไม่ย้อนไปเปลี่ยนใบกำกับที่ออกไปแล้ว
/// 2. ทะเบียนสาขาที่ใบนี้ผูกอยู่ (`Branch.TaxBranchCode`) — ใช้ตอนยังเป็น Draft
/// 3. `Company.BranchCode` — กิจการสาขาเดียว (พฤติกรรมเดิม)
/// </summary>
public static class DocumentIssuerBranch
{
    /// <summary>
    /// รหัสสาขา 5 หลักที่ต้องใช้บนใบนี้ — ลำดับเดียวกับ <see cref="Resolve"/>
    /// (snapshot ชนะทะเบียน · ทะเบียนชนะค่าบริษัท) ใช้ตอนที่ต้องการแค่ "เลข"
    /// เช่นประกอบ TXID ของ e-Tax (เลขภาษี 13 หลัก + สาขา 5 หลัก)
    /// </summary>
    public static string ResolveCode(string? snapshotCode, string? registryCode, string? companyBranchCode)
        => TaxBranchCode.Normalize(Pick(snapshotCode, registryCode, companyBranchCode));

    /// <summary>
    /// true = ต้องใช้ **ที่อยู่ของสาขา** แทนที่อยู่บริษัทบนใบนี้ (ป.86/2542)
    ///
    /// ⚠️ กติกา "ทั้งชุดหรือไม่ใช้เลย" — ห้ามหยิบบางช่องจากสาขาบางช่องจากบริษัท
    /// (ตำบลของสาขา + จังหวัดของสำนักงานใหญ่ = ที่อยู่ที่ไม่มีอยู่จริงบนโลก)
    /// สาขาที่ยังไม่กรอกที่อยู่ → ใช้ที่อยู่บริษัททั้งชุดตามเดิม
    /// </summary>
    public static bool UseBranchAddress(string? branchAddress)
        => !string.IsNullOrWhiteSpace(branchAddress);

    /// <param name="snapshotCode">`Document.IssuerBranchCode` — null เมื่อยังไม่อนุมัติ/เอกสารเก่า</param>
    /// <param name="branch">สาขาที่ใบนี้ผูกอยู่ — null = กิจการสาขาเดียว</param>
    /// <param name="companyBranchCode">`Company.BranchCode` (ค่าเดิมของระบบ)</param>
    /// <param name="companyBranchName">`Company.BranchName` (ค่าเดิมของระบบ)</param>
    public static IssuerBranch Resolve(
        string? snapshotCode,
        DocumentBranchView? branch,
        string? companyBranchCode,
        string? companyBranchName,
        bool isEnglish = false)
    {
        var fromRegistry = branch != null;

        // ชื่อสาขาที่ใช้ต่อท้ายรหัส — ของทะเบียนก่อน แล้วค่อยของบริษัท
        var name = fromRegistry
            ? (isEnglish && !string.IsNullOrWhiteSpace(branch!.NameEn) ? branch.NameEn : branch!.Name)
            : companyBranchName;

        // รหัส: snapshot ชนะทะเบียน, ทะเบียนชนะค่าบริษัท
        var rawCode = Pick(snapshotCode, branch?.TaxBranchCode, companyBranchCode);

        // ⚠️ snapshot ที่ไม่ตรงกับทะเบียนปัจจุบัน = สาขาถูกแก้รหัสหลังออกใบ
        // ชื่อสาขาในทะเบียนอาจไม่ใช่ของรหัสที่พิมพ์ไว้แล้ว → ตัดชื่อทิ้ง เหลือแต่รหัส
        // ที่เป็นตัวที่กฎหมายคุม (ดีกว่าพิมพ์ "สาขาที่ 3 (เชียงใหม่)" ทั้งที่ตอนออกใบ
        // รหัส 3 ยังเป็นสาขาอื่น)
        if (fromRegistry
            && !string.IsNullOrWhiteSpace(snapshotCode)
            && TaxBranchCode.Normalize(snapshotCode) != TaxBranchCode.Normalize(branch!.TaxBranchCode))
        {
            name = null;
        }

        return new IssuerBranch(
            Code: ResolveCode(snapshotCode, branch?.TaxBranchCode, companyBranchCode),
            Label: TaxBranchCode.LabelWithName(rawCode, name, isEnglish),
            // สาขาที่ไม่ได้กรอกที่อยู่/เบอร์/อีเมล → null = ผู้เรียกใช้ของบริษัทตามเดิม
            Address: Blank(branch?.Address),
            Phone: Blank(branch?.Phone),
            Email: Blank(branch?.Email),
            FromBranchRegistry: fromRegistry);
    }

    private static string? Pick(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
}
