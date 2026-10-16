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
    private static readonly Regex Postal = new(@"(?<![0-9])([1-9][0-9]{4})(?![0-9])", RegexOptions.Compiled);

    /// <summary>ที่อยู่ขึ้นต้นด้วย "/เลข" — เลขบ้านส่วนหน้าหายไป</summary>
    public static bool IsTruncatedAddress(string? address)
        => !string.IsNullOrWhiteSpace(address) && TruncatedHead.IsMatch(address);

    /// <summary>แถวนี้ OCR สร้างหรือแก้ล่าสุด (<c>CreatedBy/UpdatedBy</c> ขึ้นต้น "OCR" — แท็กเดียวกับ
    /// <c>EnrichContactAddress</c> ที่ใช้ตัดสินว่าทับได้ไหม)</summary>
    public static bool IsOcrManaged(string? createdBy, string? updatedBy)
        => (createdBy ?? "").StartsWith("OCR", StringComparison.OrdinalIgnoreCase)
        || (updatedBy ?? "").StartsWith("OCR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <b>รหัสไปรษณีย์ 5 หลัก — ตัวตัดสินเดียวของระบบ</b> (รายงานผู้ติดต่อ · ช่อง 12 ของไฟล์ ภ.ง.ด.1 · <c>ThaiAddressParser</c> ซึ่ง
    /// ป้อนการสร้างผู้ติดต่อ/backfill ที่อยู่แยกช่อง/OCR) — ช่อง structured ก่อน แล้วค่อยหาในข้อความ · ไม่รู้ = <c>null</c>
    /// (ไม่เดา — ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ) · เลขไทย ๐–๙ แปลงเป็น ASCII ก่อน
    ///
    /// <para><b>ด่านตัดผู้สมัคร</b> (ตัวเลข 5 หลักที่<b>ไม่ใช่</b>รหัสไปรษณีย์แน่ ๆ): นอกช่วง 10–96 · ตามหลังป้ายบ้าน/ห้อง/ซอย/หมู่/ชั้น
    /// ("เลขที่ 12345" · "ห้อง 10201" · "No.") · ติด <c>/</c> <c>-</c> กับตัวเลข ("12345/6" · "0-4055-51136-08-5") ·
    /// <b>อยู่ในช่วงเบอร์โทร</b> — ตามหลังป้าย โทร/โทรศัพท์/โทรสาร/Tel/Fax/มือถือ/ต่อ/Ext./T./F. โดยมีแต่ตัวเลข/วรรค/วงเล็บคั่น
    /// หรือเป็นท้ายกลุ่มเลขที่ขึ้นต้น 0 รวม 8–11 หลัก ("02 123 45678" ไม่มีป้าย) · ตามหลังป้ายสาขา/เลขผู้เสียภาษี</para>
    ///
    /// <para><b>หลักฐานว่าเป็นรหัสไปรษณีย์</b> (ไล่จากท้ายข้อความ): ชั้น 1 = ตามหลังจังหวัด (จ./จังหวัด/ชื่อ 77 จังหวัด/กรุงเทพฯ/กทม)
    /// หรืออำเภอ/เขต/ตำบล/แขวงที่มีคำนำ · ชั้น 2 = ข้างหลังไม่มีส่วนประกอบที่อยู่ละเอียด (ถนน/ซอย/หมู่/ตำบล/แขวง/อำเภอ/เขต/ห้อง/ชั้น/
    /// อาคาร/Road/Soi/Moo/Floor…) — จะตามด้วยประเทศ/เบอร์โทร/(สำนักงานใหญ่)/สาขา/เลขผู้เสียภาษี/อีเมลก็ได้ · หรือทะเบียนราชการบอกว่า
    /// รหัสนี้อยู่จังหวัดที่ข้อความเขียนไว้ · คำแรกของข้อความ (ปกติเป็นบ้านเลขที่) ผ่านชั้น 2 ได้เฉพาะรหัสที่ทะเบียนรู้จัก
    /// ("10500 กรุงเทพมหานคร" · "10330 Bangkok Thailand")</para>
    ///
    /// <para>ประวัติ: เดิม (ก่อน W1c) หยิบ "ตัวเลข 5 หลักตัวสุดท้าย" ส่วน parser หยิบ "ตัวแรก" ⇒ บ้านเลขที่/เลขห้อง 5 หลักกลายเป็นรหัสไปรษณีย์ ·
    /// W1c (b195bc96) รวมเป็นตัวตัดสินเดียวแต่ยอมให้ "ท้ายข้อความ" ตามด้วยได้แค่ประเทศ/เบอร์โทร และไม่ตัดเลขในเบอร์โทร ⇒
    /// "กรุงเทพฯ 10110 โทร 02 123 45678" ได้ 45678 · "Bangkok 10110 (Head office)" ได้ null (ฝ่ายค้าน W1c P2-1/P2-2) ·
    /// ชุดเทียบ 92 ที่อยู่ล็อกใน <c>ThaiAddressPostalHarnessTests</c> (ห้ามถดถอยจาก parent 36b6125f ทุกช่อง)</para>
    /// </summary>
    internal static string? PostalCodeOf(string? structuredPostal, string? address)
    {
        var s = AsciiDigits((structuredPostal ?? "").Trim());
        if (s.Length == 5 && s.All(ch => ch >= '0' && ch <= '9')) return s;
        if (string.IsNullOrWhiteSpace(address)) return null;
        var text = AsciiDigits(address);
        var cands = new List<(string Code, string Before, string After)>();
        foreach (Match m in Postal.Matches(text))
        {
            var code = m.Groups[1].Value;
            if (!InThaiPostalRange(code)) continue;
            var before = text[..m.Index];
            var after = text[(m.Index + m.Length)..];
            if (HouseLabelBefore.IsMatch(before) || DigitJoinBefore.IsMatch(before) || DigitJoinAfter.IsMatch(after)) continue;
            if (PhoneLabelBefore.IsMatch(before) || IdLabelBefore.IsMatch(before) || EndsUnlabeledPhone(before)) continue;
            cands.Add((code, before, after));
        }
        // ชั้น 1 — ตามหลังจังหวัด/เขตปกครองที่มีคำนำ (หลักฐานแรงสุด · ตัวท้ายสุดชนะ)
        for (var i = cands.Count - 1; i >= 0; i--)
            if (FollowsProvince(cands[i].Before)) return cands[i].Code;
        // ชั้น 2 — ท้ายที่อยู่ (ไม่มีส่วนประกอบที่อยู่ละเอียดตามหลัง) หรือทะเบียนยืนยันจังหวัด
        for (var i = cands.Count - 1; i >= 0; i--)
        {
            var (code, before, after) = cands[i];
            var registryProvince = Accounting.Services.Implementations.ThaiAdminCodes.ProvinceOfPostcode(code);
            var isFirstWord = before.Trim().Length == 0;
            if ((!isFirstWord || registryProvince != null) && !FineAddressPart.IsMatch(after)) return code;
            if (registryProvince != null && TextMentionsProvince(text, registryProvince)) return code;
        }
        return null;
    }

    /// <summary>สองหลักแรก 10–96 = ช่วงรหัสไปรษณีย์ไทย (10 กรุงเทพฯ … 96 นราธิวาส)</summary>
    private static bool InThaiPostalRange(string code)
        => code.Length == 5 && int.TryParse(code.AsSpan(0, 2), out var p) && p is >= 10 and <= 96;

    private static readonly Regex HouseLabelBefore = new(
        @"(?:บ้านเลขที่|เลขที่|ห้อง|ซอย|(?<![ก-๙])ซ\.|หมู่ที่|หมู่|(?<![ก-๙])ม\.|ชั้น|\bNo\.?|\bRoom|\bUnit|\bSoi|\bMoo|\bFloor|#)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DigitJoinBefore = new(@"[0-9]\s*[/\-]\s*$", RegexOptions.Compiled);
    private static readonly Regex DigitJoinAfter = new(@"^\s*[/\-]\s*[0-9]", RegexOptions.Compiled);
    private const string PhoneLabel = @"(?:โทร\S*|มือถือ\S*|แฟกซ์\S*|ต่อ|\b(?:Tel|Telephone|Phone|Fax|Mobile|Mob|Ext)\b\.?:?|\b[TFM]\.:?)";
    /// <summary>ป้ายเบอร์โทร/แฟกซ์/ต่อ แล้วมีแต่ตัวเลข วรรค - . ( ) / + , : (และป้ายเบอร์ซ้อน "โทร … ต่อ") จนถึงผู้สมัคร</summary>
    private static readonly Regex PhoneLabelBefore = new(
        PhoneLabel + @"[\s0-9\-.()/+,:]*(?:" + PhoneLabel + @"[\s0-9\-.()/+,:]*)*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>ป้ายสาขา/เลขผู้เสียภาษี แล้วมีแต่ตัวเลข/วรรค/ขีด — "สาขาที่ 10001" · "Tax ID 0 4055 51136 08 5"</summary>
    private static readonly Regex IdLabelBefore = new(
        @"(?:สาขา(?:ที่)?|\bBranch(?:\s*No\.?)?|เลขประจำตัว\S*|เลข(?:ที่)?ผู้เสียภาษี\S*|\bTax\s*ID\S*|\bTIN\b)[\s0-9\-.:#]*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingDigitRun = new(@"(?:^|[^0-9\s.()+])\s*((?:\+?\(?[0-9]+\)?[\s.]+)+)$", RegexOptions.Compiled);

    /// <summary>ผู้สมัครเป็นท้ายกลุ่มเลขเบอร์โทรที่ไม่มีป้าย — กลุ่มก่อนหน้า (คั่นวรรค/จุด) มีช่วงที่ขึ้นต้น 0 หรือ +66 ซึ่งรวมกับผู้สมัคร
    /// ได้ 8–11 หลัก ("02 123 45678" · "0 2234 56789") · "ซอย 71 10110" ไม่เข้า (ไม่ขึ้นต้น 0)</summary>
    private static bool EndsUnlabeledPhone(string before)
    {
        var m = TrailingDigitRun.Match(before);
        if (!m.Success) return false;
        var groups = Regex.Matches(m.Groups[1].Value, @"\+?\(?[0-9]+\)?").Select(g => g.Value).ToList();
        for (var k = 0; k < groups.Count; k++)
        {
            var head = groups[k].TrimStart('+', '(');
            if (!head.StartsWith('0') && !head.StartsWith("66", StringComparison.Ordinal)) continue;
            var digits = groups.Skip(k).Sum(g => g.Count(char.IsAsciiDigit)) + 5;
            if (digits is >= 8 and <= 11) return true;
        }
        return false;
    }

    /// <summary>ส่วนประกอบที่อยู่ "ละเอียด" ที่ไม่มีวันอยู่หลังรหัสไปรษณีย์ (จังหวัด/ประเทศ/เบอร์/สาขา/เลขภาษีอยู่ได้)</summary>
    private static readonly Regex FineAddressPart = new(
        @"ถนน|(?<![ก-๙])[ถซต]\.|(?<![ก-๙])อ\.|ซอย|หมู่|(?<![ก-๙])ม\.\s*[0-9]|ตำบล|แขวง|อำเภอ|เขต|ห้อง|ชั้น|อาคาร|ตึก"
        + @"|\b(?:Road|Rd|Soi|Moo|Floor|Fl|Room|Unit|Building|Bldg|Street|St|Lane|Tambon|Amphoe|Khet|Khwaeng|Sub-?district|District)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ข้อความเขียนจังหวัดของรหัสนี้ไว้ (ตามทะเบียน) — กรุงเทพฯ รับ กรุงเทพ*/กทม/Bangkok ด้วย</summary>
    private static bool TextMentionsProvince(string text, string province)
        => text.Contains(province, StringComparison.Ordinal)
        || (province == "กรุงเทพมหานคร" && (text.Contains("กรุงเทพ", StringComparison.Ordinal) || text.Contains("กทม", StringComparison.Ordinal)
            || text.Contains("Bangkok", StringComparison.OrdinalIgnoreCase)));

    private static readonly Regex AreaMarkedToken = new(@"^(?:อำเภอ|อ\.|เขต|ตำบล|ต\.|แขวง)\S+", RegexOptions.Compiled);
    private static readonly Regex LastToken = new(@"(\S+)\s*[,]?\s*$", RegexOptions.Compiled);

    /// <summary>คำก่อนหน้าเป็นจังหวัด — จ.X · จังหวัดX · ชื่อ 77 จังหวัด · กรุงเทพ*/กทม* · หรืออำเภอ/เขต/ตำบล/แขวงที่มีคำนำ</summary>
    private static bool FollowsProvince(string before)
    {
        var m = LastToken.Match(before);
        if (!m.Success) return false;
        var tok = m.Groups[1].Value.Trim().TrimEnd('ฯ', '.', ',');
        if (tok.StartsWith("จังหวัด", StringComparison.Ordinal)) tok = tok["จังหวัด".Length..];
        else if (tok.StartsWith("จ.", StringComparison.Ordinal)) tok = tok[2..];
        tok = tok.Trim().TrimEnd('ฯ', '.');
        // ตามหลังอำเภอ/เขต/ตำบล/แขวงที่มีคำนำ ("อ.บางนา 10260 สาขา …" — ที่อยู่บนหัวกระดาษที่ไม่มีจังหวัด และมีข้อความต่อท้าย)
        if (AreaMarkedToken.IsMatch(m.Groups[1].Value)) return true;
        if (tok.Length == 0)
        {
            // "จังหวัด ชลบุรี 20140" ⇒ คำสุดท้ายคือ "ชลบุรี" อยู่แล้ว · "จ. 20140" (ไม่มีชื่อ) ไม่นับ
            return false;
        }
        return tok.StartsWith("กรุงเทพ", StringComparison.Ordinal) || tok.StartsWith("กทม", StringComparison.Ordinal)
            || Accounting.Services.Implementations.ThaiAddressParser.IsProvinceName(tok);
    }

    /// <summary>เลขไทย ๐–๙ → 0–9 (ตัวอื่นคงเดิม · ความยาวเท่าเดิม ⇒ ตำแหน่ง match ใช้กับข้อความเดิมได้)</summary>
    private static string AsciiDigits(string s)
    {
        if (s.All(ch => ch is < '๐' or > '๙')) return s;
        var a = s.ToCharArray();
        for (var i = 0; i < a.Length; i++)
            if (a[i] is >= '๐' and <= '๙') a[i] = (char)('0' + (a[i] - '๐'));
        return new string(a);
    }

    /// <summary>
    /// คีย์ผู้ติดต่อซ้ำ = เลขภาษี 13 หลัก (ตัวเลขล้วน) + รหัสสาขา (ว่าง ≡ 00000 สำนักงานใหญ่) — <b>ตัวเดียว</b>ของทั้งแถบเตือน/เครื่องมือรวมในหน้า
    /// ผู้ติดต่อ (<c>DocumentService.GetDuplicateContactGroupsAsync</c>) และรายงานนี้ (รอบ 200 K-5 — เดิมสูตรนี้อยู่ใน DocumentService ที่เดียว ⇒
    /// ถ้าเขียนสำเนาที่สองในรายงาน สองหน้าจะนับกลุ่มไม่ตรงกัน) · เลขไม่ครบ 13 หลัก = null (ไม่ตัดสินว่าซ้ำด้วยเลข)
    /// </summary>
    public static string? DuplicateKey(string? taxId, string? branchCode)
    {
        var tax = ThaiTaxId.Normalize(taxId);   // ASCII ล้วน (เลขไทยแปลงแล้ว — P2-3) · ตัวตัดสินเดียวของ "เลขผู้เสียภาษีเหลือแต่ตัวเลข"
        if (tax.Length != 13) return null;
        var br = ThaiTaxId.Normalize(branchCode);
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

    // ─── รอบ 201 ทีม OC (A-OC2 · team-K2 R4): แถวสาขาที่ OCR สร้างแล้วไม่มีอะไรอ้าง ───

    /// <summary>แท็ก <c>CreatedBy</c> ของแถวผู้ติดต่อสาขาที่ OCR สร้างเอง (เส้นสแกน Branch 0 · เส้นสร้างเอกสาร/แก้ผลสแกน K-4) —
    /// <b>ตัวตั้งตัวเดียว</b> ของผู้เขียน (<c>OcrService</c>) และผู้อ่าน (รายงานนี้)</summary>
    public const string OcrBranchAutoCreateTag = "OCR-BranchAutoCreate";

    /// <summary>แถวนี้เกิดจาก OCR สร้างแถวสาขาเองไหม (เทียบแท็กตรงตัว — ไม่ใช่ "ขึ้นต้น OCR" ซึ่งรวมแถวที่ OCR แค่เติมข้อมูล)</summary>
    private static bool IsOcrBranchAutoCreated(string? createdBy)
        => string.Equals(createdBy?.Trim(), OcrBranchAutoCreateTag, StringComparison.Ordinal);

    /// <summary>
    /// **แถวสาขาที่ OCR สร้างแล้วไม่มีเอกสาร/สแกน/ข้อมูลอื่นอ้างถึง** — ผู้ใช้พิมพ์รหัสสาขาผิดในหน้ารีวิว ⇒ แถวของรหัสผิดเกิดขึ้น
    /// แล้วค้างถาวรหลังแก้กลับ (team-K2 R4) · <b>รายงานอย่างเดียว</b> — การลบต้องให้คนกดทีละแถว (<see cref="OrphanRetireBlock"/> ตรวจซ้ำที่เซิร์ฟเวอร์)
    /// </summary>
    /// <param name="referencedIds">id ผู้ติดต่อที่ถูกอ้างโดยข้อมูลใดก็ตามที่ผู้เรียกตรวจ (เอกสาร · สแกน · 50 ทวิ · เครดิตภาษี · alias · รายการประจำ)</param>
    public static IReadOnlyList<ContactKeyRow> OrphanOcrBranchRows(IEnumerable<ContactKeyRow> rows, IReadOnlyCollection<Guid> referencedIds)
        => rows.Where(r => IsOcrBranchAutoCreated(r.CreatedBy) && !referencedIds.Contains(r.Id))
            .OrderBy(r => r.TaxId, StringComparer.Ordinal).ThenBy(r => r.BranchCode, StringComparer.Ordinal).ThenBy(r => r.CreatedAt)
            .ToList();

    /// <summary>ด่านของปุ่ม "ลบแถวนี้" (soft) — คืนข้อความไทยเมื่อ<b>ห้าม</b> · null = ลบได้ · เซิร์ฟเวอร์ตรวจซ้ำทุกครั้ง (หน้ารายงานอาจค้าง:
    /// ระหว่างเปิดรายงานกับกดปุ่ม มีเอกสารผูกแถวนี้แล้วก็ได้)</summary>
    public static string? OrphanRetireBlock(string? createdBy, bool referenced)
    {
        if (!IsOcrBranchAutoCreated(createdBy))
            return "ลบจากหน้านี้ได้เฉพาะแถวสาขาที่ระบบสแกนสร้างเอง — แถวอื่นลบที่หน้าผู้ติดต่อ";
        if (referenced)
            return "แถวนี้มีเอกสาร/สแกน/ข้อมูลอื่นอ้างถึงแล้ว — ลบไม่ได้ (เปิดผู้ติดต่อเพื่อรวมหรือปิดการใช้งานแทน)";
        return null;
    }
}
