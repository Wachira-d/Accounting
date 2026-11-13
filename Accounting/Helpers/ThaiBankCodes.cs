using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **รหัสธนาคารไทย ⇄ ชื่อ/ตัวย่อไทย-อังกฤษ — ตารางตัวเดียว (OWNER)** (รอบ 264 แพ็ก P2 · คำตัดสิน 264-4-2 · F2 ข้อ 4: ห้ามสำเนาที่สอง)
/// <para>ผู้ใช้: <see cref="OcrSlipText"/> (บรรทัดชื่อธนาคารบนสลิป) · <see cref="ThaiSlipQr"/> (รหัสธนาคารผู้โอนใน QR) · <see cref="SlipAccountMatch"/> (ธนาคารของบัญชีในระบบ) ·
/// <c>SlipReceivingAccount.SameBank</c> (ยุบมาใช้ตารางนี้ — คงพฤติกรรมเดิมเมื่อชื่อไม่อยู่ในตาราง)</para>
/// <para>รหัส = รหัสสถาบันการเงิน 3 หลักของ ธปท. (ตัวเดียวกับที่อยู่ใน QR ตรวจสลิป) · <b>ยืนยันกับสลิปจริงแล้ว</b>: 004 (K+ · QR) · 006 (กรุงไทย · QR) · 014 (SCB · QR) ·
/// ชื่อบนกระดาษจริง: "กรุงไทย" · "กสิกรไทย" · "ธ.กสิกรไทย" · "Kasikornbank" · "กรุงเทพ" · "ttb" · ตัวอื่นมาจากรายชื่อรหัสสถาบันการเงินของ ธปท. ยังไม่มีสลิปจริงยืนยัน</para>
/// <para>ชื่อสั้นภาษาอังกฤษ (KTB · SCB · ttb …) จับเป็น<b>คำทั้งคำ</b>เท่านั้น (ไม่จับ "ttb" กลางคำอื่น) · ไม่รู้จัก = null (ไม่เดา)</para>
/// </summary>
public static class ThaiBankCodes
{
    /// <param name="Code">รหัส 3 หลักของ ธปท.</param>
    /// <param name="Short">ตัวย่อที่ใช้ในระบบ/ป้าย</param>
    /// <param name="ThaiName">ชื่อไทยสั้น (ป้ายบนหน้า)</param>
    /// <param name="Aliases">ชื่อ/ตัวย่อที่พบบนสลิปหรือในช่อง "ธนาคาร" ของบัญชี (ไทย = จับเป็นสตริงย่อย · อังกฤษ = จับทั้งคำ ไม่สนตัวพิมพ์)</param>
    public sealed record BankInfo(string Code, string Short, string ThaiName, string EnglishName, IReadOnlyList<string> Aliases);

    public static IReadOnlyList<BankInfo> All { get; } = new[]
    {
        new BankInfo("002", "BBL", "กรุงเทพ", "Bangkok Bank", new[] { "กรุงเทพ", "BBL", "Bangkok Bank", "Bualuang" }),
        new BankInfo("004", "KBANK", "กสิกรไทย", "Kasikornbank", new[] { "กสิกรไทย", "กสิกร", "KBANK", "Kasikornbank", "Kasikorn", "K PLUS", "K+", "KPLUS", "KBIZ", "K BIZ" }),
        new BankInfo("006", "KTB", "กรุงไทย", "Krungthai Bank", new[] { "กรุงไทย", "KTB", "Krungthai", "Krung Thai" }),
        new BankInfo("011", "TTB", "ทหารไทยธนชาต", "TMBThanachart Bank", new[] { "ทหารไทยธนชาต", "ทีทีบี", "ทหารไทย", "ธนชาต", "ttb", "TMBThanachart", "TMB" }),
        new BankInfo("014", "SCB", "ไทยพาณิชย์", "Siam Commercial Bank", new[] { "ไทยพาณิชย์", "SCB", "Siam Commercial" }),
        new BankInfo("022", "CIMBT", "ซีไอเอ็มบี ไทย", "CIMB Thai", new[] { "ซีไอเอ็มบี", "CIMB" }),
        new BankInfo("024", "UOBT", "ยูโอบี", "UOB Thailand", new[] { "ยูโอบี", "UOB" }),
        new BankInfo("025", "BAY", "กรุงศรีอยุธยา", "Bank of Ayudhya (Krungsri)", new[] { "กรุงศรีอยุธยา", "กรุงศรี", "BAY", "Krungsri", "Ayudhya" }),
        new BankInfo("030", "GSB", "ออมสิน", "Government Savings Bank", new[] { "ออมสิน", "GSB", "Government Savings" }),
        new BankInfo("033", "GHB", "อาคารสงเคราะห์", "Government Housing Bank", new[] { "อาคารสงเคราะห์", "ธอส", "GHB" }),
        new BankInfo("034", "BAAC", "ธ.ก.ส.", "Bank for Agriculture and Agricultural Cooperatives", new[] { "ธ.ก.ส.", "ธกส", "เพื่อการเกษตร", "BAAC" }),
        new BankInfo("066", "ISBT", "อิสลามแห่งประเทศไทย", "Islamic Bank of Thailand", new[] { "อิสลามแห่งประเทศไทย", "ISBT" }),
        new BankInfo("067", "TISCO", "ทิสโก้", "TISCO Bank", new[] { "ทิสโก้", "TISCO" }),
        new BankInfo("069", "KKP", "เกียรตินาคินภัทร", "Kiatnakin Phatra Bank", new[] { "เกียรตินาคินภัทร", "เกียรตินาคิน", "KKP" }),
        new BankInfo("073", "LHB", "แลนด์ แอนด์ เฮ้าส์", "Land and Houses Bank", new[] { "แลนด์ แอนด์ เฮ้าส์", "LH Bank", "LHBANK" }),
    };

    /// <summary>ธนาคารของรหัส · ไม่รู้จัก = null</summary>
    public static BankInfo? ByCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();
        return All.FirstOrDefault(b => b.Code == c);
    }

    /// <summary>
    /// รหัสธนาคารจากข้อความ (ชื่อบนสลิป · ช่อง "ธนาคาร" ของบัญชี เช่น "ธนาคารกสิกรไทย (KBANK)") — alias ที่ยาวสุดชนะ (กรุงศรีอยุธยา ก่อน กรุงศรี) ·
    /// สองธนาคารต่างกันพบในข้อความเดียว ⇒ null (ไม่เดา) · ไม่พบ = null
    /// </summary>
    public static string? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hits = new List<(string Code, int Len)>();
        foreach (var b in All)
            foreach (var a in b.Aliases)
                if (Contains(text, a)) hits.Add((b.Code, a.Length));
        if (hits.Count == 0) return null;
        var codes = hits.Select(h => h.Code).Distinct().ToList();
        if (codes.Count == 1) return codes[0];
        // "ทหารไทยธนชาต" มี "ทหารไทย" + "ธนชาต" (ธนาคารเดียวกัน) แต่ "กรุงไทย" กับ "กสิกรไทย" ในข้อความเดียว = ข้อความหลายธนาคาร ⇒ ไม่เดา
        return null;
    }

    /// <summary>
    /// บรรทัดนี้เป็น<b>บรรทัดชื่อธนาคารล้วน</b>ไหม (บล็อกคู่กรณีบนสลิป) — ตัดคำนำ "ธ." / "ธนาคาร" / สัญลักษณ์รบกวนจาก OCR แล้ว<b>เหลือแค่ alias เดียว</b> ·
    /// ชื่อคน/บริษัทที่มีชื่อธนาคารปน (เช่น "บจก. กรุงไทยการไฟฟ้า") ไม่ใช่บรรทัดธนาคาร
    /// </summary>
    public static string? FromBankLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var s = Regex.Replace(line, @"[^\p{L}\p{M}\d+.\s]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = Regex.Replace(s, @"^(ธนาคาร|ธ\.)\s*", "");
        s = s.Trim().TrimEnd('.').Trim();
        if (s.Length == 0) return null;
        foreach (var b in All)
            foreach (var a in b.Aliases)
                if (string.Equals(s, a, StringComparison.OrdinalIgnoreCase) || string.Equals(s, a + " bank", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Replace(" ", ""), a.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    return b.Code;
        return null;
    }

    /// <summary>ชื่อธนาคารสองแบบเป็นธนาคารเดียวกันไหม — <b>ทั้งคู่รู้จักในตาราง</b> ⇒ เทียบรหัส · อื่น ๆ ⇒ null (ผู้เรียกใช้กติกาเดิมของตัวเอง)</summary>
    public static bool? SameByTable(string? a, string? b)
    {
        var (ca, cb) = (FromText(a), FromText(b));
        if (ca == null || cb == null) return null;
        return ca == cb;
    }

    private static bool Contains(string text, string alias)
    {
        if (IsAscii(alias))
        {
            // คำทั้งคำ (ไม่สนตัวพิมพ์) — ขอบคำ = ไม่ใช่ตัวอักษร/ตัวเลขละติน (ข้อความไทยติดกันนับเป็นขอบได้)
            var rx = @"(?<![A-Za-z0-9])" + Regex.Escape(alias).Replace(@"\ ", @"\s*") + @"(?![A-Za-z0-9])";
            return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase);
        }
        return text.Contains(alias, StringComparison.Ordinal);
    }

    private static bool IsAscii(string s) => s.All(c => c < 128);
}
