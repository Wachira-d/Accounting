namespace Accounting.Helpers;

/// <summary>
/// **จับคู่ "บล็อกคู่กรณีบนสลิป" กับบัญชีธนาคารของบริษัท — ตัวตัดสินเดียว ใช้ทั้งสองฝั่ง** (รอบ 264 แพ็ก P2 · คำตัดสิน 264-4-4 = ร่าง 264-2-7 · pure)
/// <para>ผู้ใช้: ฝั่งผู้รับเงิน <c>SlipReceivingAccount</c> (ผู้รับบนสลิป ⇒ บัญชีที่เงินเข้า) · ฝั่งผู้จ่าย <c>PartnerPayablePipeline.SourceDecisionAsync</c>
/// (ผู้โอนบนสลิป ⇒ <c>PartnerPaySourceAccount</c> ชั้น <c>SlipUnique</c>)</para>
/// <list type="bullet">
/// <item><see cref="Kind.Unique"/> เฉพาะเมื่อ <b>ธนาคารตรง</b> (รหัสจากบรรทัดชื่อธนาคารหรือ QR) <b>และ</b> หลักที่เห็น ≥ <see cref="MinVisibleDigits"/> ตัวตรงตำแหน่งเดียวกันนับจากขวาของเลขบัญชี
/// <b>และ</b> เหลือบัญชีเดียว · ตัวเลขห้าม fuzzy (F2 ข้อ 3) · ชื่อบนสลิป<b>ไม่ใช้</b>สร้างผล</item>
/// <item>บัตร · จ่ายบิล (Biller ID) · พร้อมเพย์ไปเบอร์ ⇒ <see cref="Kind.NotAnAccount"/> เสมอ (ไม่ใช่เลขบัญชีธนาคาร — ห้ามจับคู่)</item>
/// <item><see cref="Kind.NotOurs"/> = อ่านได้ชัด (ธนาคาร + หลักที่เห็นพอ) แต่<b>ไม่ตรงบัญชีใด</b> — ตัดสินได้เฉพาะเมื่อบัญชีทุกบัญชีของบริษัทมีเลขบัญชีและธนาคารที่รู้จัก
/// (มีบัญชีที่ข้อมูลไม่ครบ ⇒ <see cref="Kind.Unreadable"/> — ไม่กล่าวหาว่า "ไม่ใช่ของเรา")</item>
/// <item>บัญชีที่ธนาคารในระบบไม่อยู่ในตาราง <see cref="ThaiBankCodes"/> แต่เลขตรง ⇒ นับเป็นผู้สมัคร (ตัดธนาคารไม่ได้) ⇒ ทำให้ผลเป็น <see cref="Kind.Many"/> ไม่ใช่ Unique</item>
/// <item>ไม่มีจุดเรียก AI (เลขบัญชี — คำตัดสิน 264-2-6) ⇒ ไม่มี AiFeatureKey · kill-switch ปลอดภัยโดยโครงสร้าง</item>
/// </list>
/// </summary>
public static class SlipAccountMatch
{
    public const string RuleCode = "SLIP-ACCOUNT-MATCH";

    /// <summary>หลักที่เห็นขั้นต่ำ (264-2-7) — วัดจากชุดสลิปจริง 10 ใบ (2026-11-13): เลขบัญชีปิดบางทุกใบที่อ่านได้เห็น <b>4</b> หลัก (กรุงไทย/กสิกร/KBIZ/ttb = 4 ท้าย ·
    /// SCB = 4 ท้าย) · เกณฑ์ 3 เผื่อ OCR อ่านตัวปิดเป็นตัวปิดเพิ่มหนึ่งตัว — ต่ำกว่านี้ไม่ตัดสิน</summary>
    public const int MinVisibleDigits = 3;

    public enum Kind { Unique, Many, NotOurs, Unreadable, NotAnAccount }

    /// <param name="BankName">ช่อง "ธนาคาร" ของบัญชีในระบบ (ข้อความอิสระ — แปลงผ่าน <see cref="ThaiBankCodes.FromText"/>)</param>
    public sealed record AccountKey(Guid Id, string? BankName, string? AccountNumber);

    /// <param name="AccountId">มีค่าเฉพาะ <see cref="Kind.Unique"/></param>
    /// <param name="Candidates">บัญชีที่เข้ากันได้ (Many)</param>
    /// <param name="PartyLabel">ป้ายของบล็อกบนสลิป เช่น "กสิกรไทย ••0621" (server computes)</param>
    public sealed record Result(Kind Kind, Guid? AccountId, IReadOnlyList<Guid> Candidates, string Reason, string? PartyLabel)
    {
        public bool UsedAi => false;
    }

    public static Result Match(OcrSlipText.SlipParty? party, IReadOnlyList<AccountKey> accounts)
    {
        if (party == null) return R(Kind.Unreadable, "ไม่รู้ว่าบล็อกไหนบนสลิปคือคู่กรณีนี้ (ไม่มีป้าย จาก/ไปยัง และ QR ตัดสินไม่ได้)", null);
        var label = Label(party);
        if (party.Kind is OcrSlipText.PartyKind.Card or OcrSlipText.PartyKind.Biller or OcrSlipText.PartyKind.PromptPay)
            return R(Kind.NotAnAccount, party.Kind switch
            {
                OcrSlipText.PartyKind.Card => "คู่กรณีบนสลิปเป็นบัตร — ไม่ใช่บัญชีธนาคาร (ไม่จับคู่)",
                OcrSlipText.PartyKind.Biller => "สลิปจ่ายบิล (Biller ID) — ปลายทางเป็นผู้รับชำระ ไม่ใช่เลขบัญชี (ไม่จับคู่)",
                _ => "พร้อมเพย์ไปเบอร์/เลขที่ปิดบาง — ไม่รู้ว่าผูกบัญชีไหน (ไม่จับคู่)",
            }, label);
        if (party.Kind != OcrSlipText.PartyKind.BankAccount || party.AccountMask == null)
            return R(Kind.Unreadable, "อ่านเลขบัญชีบนสลิปไม่ได้", label);
        if (party.BankCode == null) return R(Kind.Unreadable, "อ่านธนาคารบนสลิปไม่ได้ (หรือข้อความขัดกับ QR)", label);
        var digits = party.VisibleDigits;
        if (digits.Count < MinVisibleDigits) return R(Kind.Unreadable, $"เห็นเลขบัญชีบนสลิปไม่ถึง {MinVisibleDigits} หลัก", label);

        var hits = new List<Guid>();
        var unknownBankHits = new List<Guid>();
        var incomplete = false;
        foreach (var a in accounts)
        {
            var num = new string((a.AccountNumber ?? "").Where(char.IsAsciiDigit).ToArray());
            var code = ThaiBankCodes.FromText(a.BankName);
            if (num.Length == 0 || code == null) incomplete = true;
            if (num.Length == 0 || !DigitsFit(num, digits)) continue;
            if (code == party.BankCode) hits.Add(a.Id);
            else if (code == null) unknownBankHits.Add(a.Id);
        }
        var all = hits.Concat(unknownBankHits).Distinct().ToList();
        if (hits.Count == 1 && unknownBankHits.Count == 0)
            return new Result(Kind.Unique, hits[0], hits, "ธนาคารและเลขท้ายบนสลิปตรงบัญชีนี้บัญชีเดียว", label);
        if (all.Count >= 1)
            return new Result(Kind.Many, null, all, all.Count == 1
                ? "เลขท้ายตรงบัญชีหนึ่ง แต่ธนาคารของบัญชีในระบบไม่อยู่ในตาราง — ยืนยันไม่ได้ (เลือกเอง)"
                : $"เลขท้ายบนสลิปเข้ากันได้ {all.Count} บัญชี — ระบบไม่เดา (เลือกเอง)", label);
        if (incomplete)
            return R(Kind.Unreadable, "ไม่ตรงบัญชีที่มีข้อมูลครบ แต่มีบัญชีที่ไม่มีเลขบัญชี/ธนาคารที่รู้จัก — ตัดสินไม่ได้ว่าไม่ใช่ของบริษัท", label);
        // ฝ่ายค้าน 264-P2 L1: บัญชีธนาคารเดียวกันที่หลักที่เห็นต่างกันแค่ 1 ตำแหน่ง = อาจเป็น OCR อ่านเพี้ยนหนึ่งตัว ⇒ ไม่กล่าวหาว่า "ไม่ใช่ของบริษัท"
        // (ไม่ใช่ fuzzy match — ไม่สร้าง Unique · แค่ลดผลจาก NotOurs เป็นอ่านไม่ได้)
        if (accounts.Any(a => ThaiBankCodes.FromText(a.BankName) == party.BankCode && DigitsOffBy(Digits(a.AccountNumber), digits) <= 1))
            return R(Kind.Unreadable, "เลขท้ายบนสลิปต่างจากบัญชีของบริษัทเพียงหลักเดียว — อาจอ่านรูปเพี้ยน ตัดสินไม่ได้ (เลือกเอง)", label);
        return R(Kind.NotOurs, "บัญชีบนสลิปไม่ตรงบัญชีใดของบริษัท", label);
    }

    private static string Digits(string? s) => new((s ?? "").Where(char.IsAsciiDigit).ToArray());

    /// <summary>จำนวนตำแหน่งที่เห็นแต่ไม่ตรง (ตำแหน่งเกินความยาวเลขบัญชีนับเป็นไม่ตรง)</summary>
    internal static int DigitsOffBy(string accountDigits, IReadOnlyList<(int FromRight, char Digit)> visible)
    {
        if (accountDigits.Length == 0) return int.MaxValue;
        var off = 0;
        foreach (var (pos, d) in visible)
        {
            var i = accountDigits.Length - 1 - pos;
            if (i < 0 || accountDigits[i] != d) off++;
        }
        return off;
    }

    /// <summary>หลักที่เห็นทุกตัวตรงตำแหน่งนับจากขวา · เลขบัญชีสั้นกว่าตำแหน่งที่เห็น ⇒ ไม่ตรง</summary>
    internal static bool DigitsFit(string accountDigits, IReadOnlyList<(int FromRight, char Digit)> visible)
    {
        foreach (var (pos, d) in visible)
        {
            var i = accountDigits.Length - 1 - pos;
            if (i < 0 || accountDigits[i] != d) return false;
        }
        return true;
    }

    /// <summary>ป้ายบล็อก: "{ธนาคาร} ••{หลักที่เห็น}" / "Biller ID …" / "บัตร" — ไม่มีชื่อคน (PDPA · ไม่จำเป็นต่อการตัดสิน)</summary>
    public static string Label(OcrSlipText.SlipParty p)
    {
        var bank = ThaiBankCodes.ByCode(p.BankCode)?.ThaiName ?? (p.BankCode != null ? "ธนาคาร " + p.BankCode : "ไม่ทราบธนาคาร");
        return p.Kind switch
        {
            OcrSlipText.PartyKind.Biller => "จ่ายบิล Biller ID " + p.BillerId,
            OcrSlipText.PartyKind.Card => "บัตร" + (p.AccountMask?.Split('-')[^1] is { } last && last.All(char.IsAsciiDigit) ? " ••" + last : ""),
            OcrSlipText.PartyKind.PromptPay => "พร้อมเพย์ " + p.AccountMask,
            _ => bank + (p.VisibleText.Length > 0 ? " ••" + p.VisibleText : ""),
        };
    }

    private static Result R(Kind k, string reason, string? label) => new(k, null, Array.Empty<Guid>(), reason, label);
}
