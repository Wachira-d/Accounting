using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **อ่านข้อความ OCR ของสลิปโอนเงินไทย — ตัวตัดสินเดียว** (pure · รอบ 260 คำตัดสิน 260-1-1 · ย้ายมาจากตัวแยกใน <c>SlipOcrAssistService</c> ที่เคยฝังใน service)
/// <para>ผู้เรียก: <c>SlipOcrAssistService</c> (สลิปค่าบริการ — badge "ยอดตรง") · <c>TesseractSlipImageReader</c> (สลิปที่ลูกค้าส่งในแชต LINE OA ของบริษัท) ·
/// <b>ไม่เรียก AI</b> (ตัวเลขล้วน — F2 ข้อ 3: ไม่รู้ = บอกว่าไม่รู้ ไม่เดา)</para>
/// <list type="bullet">
/// <item>ยอด: รูปแบบสลิปไทยมีทศนิยม 2 ตำแหน่งเสมอ (เลขบัญชี/เลขอ้างอิงไม่มีจุด) · ทิ้ง 0.00 (ค่าธรรมเนียม 0) · ปัด 2 ตำแหน่งแบบ AwayFromZero</item>
/// <item><see cref="Reading.Amount"/> ("ยอดที่มั่นใจ") มีค่าเฉพาะเมื่อ (ก) บรรทัดที่มีคำว่า จำนวน/ยอดเงิน/ยอดโอน/Amount (หรือบรรทัดถัดไป) ให้ยอดเดียว
/// หรือ (ข) ทั้งสลิปมียอดที่ไม่ใช่ศูนย์ยอดเดียว — อื่น ๆ = null (ผู้เรียกต้องให้คน/ลูกค้าเลือก ห้ามเลือกยอดสูงสุดเอง)</item>
/// <item>เลขอ้างอิง: ชุดตัวอักษรพิมพ์ใหญ่/ตัวเลข 10–25 ตัวที่มีตัวเลข (ยาวสุด) · วันที่: dd/mm/yy(yy) หรือ "12 พ.ย. 69" (พ.ศ. ⇒ ค.ศ.) หรือ "2 Nov 26" (รอบ 264)</item>
/// <item>รอบ 264 แพ็ก P2 (คำตัดสิน 264-4-1/3): QR ตรวจสลิปที่ถอดได้ (<see cref="ThaiSlipQr"/>) ชนะเลขอ้างอิงจากข้อความ (กระดาษจริงกรุงไทย: OCR ตัดตัวพิมพ์เล็กทิ้ง
/// ได้ "79024680135" แทน "Aba…" · SCB: หยิบ Biller/Ref2 แทน) · คู่กรณี <see cref="Reading.Payer"/>/<see cref="Reading.Payee"/> จาก <see cref="Parties"/> ·
/// ชุดกระดาษจริง 10 ใบ (redact แล้ว) อยู่ใน <c>Accounting.Tests/SlipCorpusRound264.cs</c></item>
/// </list>
/// </summary>
public static class OcrSlipText
{
    /// <param name="Amounts">ยอดที่ไม่ใช่ศูนย์ทั้งหมดบนสลิป (ไม่ซ้ำ · ลำดับที่พบ)</param>
    /// <param name="Amount">ยอดโอนที่มั่นใจ · null = ไม่มั่นใจ/ไม่มี</param>
    /// <param name="LooksLikeSlip">รอบ 260 ฝ่ายค้าน M3: ภาพมี "สัญญาณว่าเป็นสลิปโอนที่ทำรายการแล้ว" (เลขอ้างอิง หรือคำว่า สำเร็จ/successful/รหัสอ้างอิง/เลขที่รายการ) —
    /// ไม่มี ⇒ ภาพหน้าชำระ/QR ที่แสดงยอดก็มียอดเดียวได้ ผู้เรียกต้องให้ลูกค้ากดยืนยันก่อน (คำตัดสิน 260-1-7)</param>
    /// <param name="Payer">รอบ 264 P2: บล็อกผู้โอน · null = ไม่รู้ (ไม่มีป้าย จาก/From และ QR ตัดสินไม่ได้ — ไม่เดาจากลำดับบน-ล่าง)</param>
    /// <param name="Payee">รอบ 264 P2: บล็อกผู้รับ · null = ไม่รู้</param>
    /// <param name="Roles">บทบาทของสองบล็อกมาจากอะไร (ป้าย · QR · ไม่รู้)</param>
    /// <param name="Qr">QR ตรวจสลิปที่ถอดได้และ CRC ถูก (<see cref="ThaiSlipQr"/>) · null = ไม่มี/อ่านไม่ได้</param>
    /// <param name="Memo">ข้อความบันทึกช่วยจำ (KBIZ "Memo …") · ไม่เก็บลงฐาน (ใช้ในหน่วยความจำเท่านั้น)</param>
    public sealed record Reading(IReadOnlyList<decimal> Amounts, decimal? Amount, DateTime? Date, string? Ref, bool LooksLikeSlip = false,
        SlipParty? Payer = null, SlipParty? Payee = null, RoleSource Roles = RoleSource.None, ThaiSlipQr.Payload? Qr = null, string? Memo = null)
    {
        public static readonly Reading Empty = new(Array.Empty<decimal>(), null, null, null);
        public bool HasAnyAmount => Amounts.Count > 0;
    }

    // ═══════════════ รอบ 264 แพ็ก P2 — คู่กรณีบนสลิป (คำตัดสิน 264-4-3) ═══════════════

    /// <summary>ชนิดของปลายทาง/ต้นทาง — <b>เฉพาะ <see cref="PartyKind.BankAccount"/> จับคู่บัญชีธนาคารได้</b> (บัตร · บิล · พร้อมเพย์ไปเบอร์ = ไม่ใช่เลขบัญชี)</summary>
    public enum PartyKind { Unknown = 0, BankAccount = 1, PromptPay = 2, Biller = 3, Card = 4 }

    /// <summary>บทบาทของบล็อกมาจากไหน</summary>
    public enum RoleSource { None = 0, Label = 1, Qr = 2 }

    /// <param name="BankCode">รหัสธนาคาร 3 หลัก (<see cref="ThaiBankCodes"/>) · null = ไม่รู้ / ข้อความกับ QR ขัดกัน</param>
    /// <param name="BankFromQr">ธนาคารมาจาก QR (บล็อกไม่มีบรรทัดชื่อธนาคาร — เช่น SCB ที่มีแต่โลโก้)</param>
    /// <param name="AccountMask">เลขปิดบางตามรูปบนสลิป ปรับรูปแล้ว (ตัวปิด = <c>x</c> · เช่น <c>xxx-x-xx319-7</c>) · <b>เก็บเฉพาะหลักที่เห็น ไม่เติม/ไม่เดาหลักที่ปิด</b></param>
    /// <param name="Name">ชื่อบนบล็อก (ใช้ในหน่วยความจำเท่านั้น · ไม่ลงฐาน · ไม่ใช้สร้างผลจับคู่ — 264-2-7)</param>
    /// <param name="BillerId">Biller ID ของการจ่ายบิล (เลขผู้รับชำระสาธารณะ)</param>
    public sealed record SlipParty(PartyKind Kind, string? BankCode, bool BankFromQr, string? AccountMask, string? Name, string? BillerId)
    {
        /// <summary>หลักที่เห็นของเลขบัญชี (นับจากขวา: 0 = หลักสุดท้าย) — ว่าง = ไม่มี</summary>
        public IReadOnlyList<(int FromRight, char Digit)> VisibleDigits => MaskDigits(AccountMask);
        /// <summary>หลักที่เห็นเรียงตามบนสลิป (ป้าย "••3197")</summary>
        public string VisibleText => new(VisibleDigits.OrderByDescending(d => d.FromRight).Select(d => d.Digit).ToArray());
    }

    private static readonly Regex MaskCandidateRx = new(@"[0-9xX%<*×Kk๐-๙]+(?:-[0-9xX%<*×Kk๐-๙]+){2,3}", RegexOptions.Compiled);
    private static readonly Regex BillerRx = new(@"Biller\s*ID\s*[:：]?\s*(\d{10,18})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CardWordRx = new(@"credit\s*card|debit\s*card|บัตร", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MemoRx = new(@"^\s*(?:Memo|บันทึกช่วยจำ)\s*[:：]?\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly string[] PayerLabels = { "จาก", "อจาก", "อาก", "from", "ผู้โอน" };
    private static readonly string[] PayeeLabels = { "ไปยัง", "to", "ผู้รับ", "ถึง" };
    private static readonly Regex StopRx = new(@"^(จำนวน|ยอด|amount|ค่าธรรมเนียม|fee|details|เลขที่รายการ|รหัสอ้างอิง|transaction|reference|memo|received)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// ปรับรูปโทเค็นเลขปิดบางจาก OCR (ตัวปิดที่ OCR อ่านเพี้ยน % &lt; * × K ตัวเลขไทย ⇒ <c>x</c> · ตัดช่องว่าง) แล้วจัดชนิด ·
    /// คืน (ชนิด, mask) · ไม่ใช่รูปที่รู้จัก ⇒ (Unknown, null) — <b>กลุ่มแรกต้องปิดทั้งกลุ่ม</b> (OCR ที่อ่านตัวปิดเป็นเลข เช่น <c>2%60%&lt;-…</c> ⇒ ไม่เชื่อทั้งโทเค็น)
    /// <list type="bullet">
    /// <item>บัญชี: กลุ่มท้าย 1 ตัว + (…-1-5-1 แบบกรุงไทย/กสิกร/ttb หรือ …-6-1 แบบ SCB) · แต่ละกลุ่ม = ตัวปิดนำหน้าแล้วตามด้วยเลข (<c>xx319</c>)</item>
    /// <item>พร้อมเพย์ (เบอร์/เลขที่ปิดบาง): 3 กลุ่ม · กลุ่มท้ายเลข 4 ตัว · กลุ่มอื่นปิดทั้งหมด (<c>xxx-xxx-0035</c>)</item>
    /// <item>บัตร: 4 กลุ่ม กลุ่มแรกเป็นเลข 4 ตัว (BIN · <c>4417-70xx-xxxx-1154</c>)</item>
    /// </list>
    /// </summary>
    public static (PartyKind Kind, string? Mask) ClassifyMask(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return (PartyKind.Unknown, null);
        var sb = new System.Text.StringBuilder();
        foreach (var ch in token)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is >= '0' and <= '9' or '-') sb.Append(ch);
            else if (ch is 'x' or 'X' or '%' or '<' or '*' or '×' or 'K' or 'k' || ch is >= '๐' and <= '๙') sb.Append('x');
            else return (PartyKind.Unknown, null);
        }
        var m = sb.ToString();
        var g = m.Split('-');
        if (g.Length is < 3 or > 4 || g.Any(p => p.Length == 0)) return (PartyKind.Unknown, null);
        if (g.Length == 4 && g[0].Length == 4 && g[0].All(char.IsAsciiDigit)) return (PartyKind.Card, m);
        var masked = m.Contains('x');
        if (masked && g[0].Any(char.IsAsciiDigit)) return (PartyKind.Unknown, null);
        if (g.Length == 3 && g[2].Length == 4 && g[2].All(char.IsAsciiDigit) && g[0].All(c => c == 'x') && g[1].All(c => c == 'x'))
            return (PartyKind.PromptPay, m);
        var shapeOk = g[^1].Length == 1 && ((g.Length == 4 && g[1].Length == 1 && g[2].Length == 5) || (g.Length == 3 && g[1].Length == 6));
        if (!shapeOk || !g.All(p => Regex.IsMatch(p, "^x*[0-9]*$"))) return (PartyKind.Unknown, null);
        return (PartyKind.BankAccount, m);
    }

    /// <summary>หลักที่เห็นของ mask (นับจากขวา · ข้ามขีด) — ผู้ใช้: <see cref="SlipParty.VisibleDigits"/> · <c>SlipAccountMatch</c></summary>
    internal static IReadOnlyList<(int FromRight, char Digit)> MaskDigits(string? mask)
    {
        if (string.IsNullOrEmpty(mask)) return Array.Empty<(int, char)>();
        var chars = mask.Where(c => c != '-').Reverse().ToArray();
        var list = new List<(int, char)>();
        for (var i = 0; i < chars.Length; i++) if (char.IsAsciiDigit(chars[i])) list.Add((i, chars[i]));
        return list;
    }

    /// <summary>
    /// คู่กรณีบนสลิป (pure) — บทบาทจาก <b>ป้ายคู่</b> (จาก/From/ผู้โอน แล้ว ไปยัง/To/ผู้รับ) หรือ <b>ธนาคารผู้โอนจาก QR ตรงบล็อกเดียว</b> (สลิปไม่มีป้ายที่มีบล็อกธนาคาร 2 บล็อกพอดี) ·
    /// ไม่มีทั้งสองอย่าง ⇒ ไม่กำหนดบทบาท (K+ โอนในธนาคารเดียวกัน · ttb · K+ จ่ายบัตร — ตรวจกับกระดาษจริงแล้ว) ·
    /// ธนาคารบนข้อความของผู้โอนขัดกับ QR ⇒ ธนาคารผู้โอน = null (ไม่เลือกข้าง)
    /// </summary>
    public static (SlipParty? Payer, SlipParty? Payee, RoleSource Roles) Parties(string? text, ThaiSlipQr.Payload? qr)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null, RoleSource.None);
        var lines = NormalizeThai(text).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var payerAt = lines.FindIndex(l => PayerLabels.Contains(LeadToken(l).ToLowerInvariant()));
        var payeeAt = payerAt < 0 ? -1 : lines.FindIndex(payerAt + 1, l => PayeeLabels.Contains(LeadToken(l).ToLowerInvariant()));
        if (payerAt >= 0 && payeeAt > payerAt)
        {
            var stop = lines.FindIndex(payeeAt + 1, l => StopRx.IsMatch(LeadToken(l)));
            if (stop < 0) stop = lines.Count;
            var payer = FromBlock(AfterLabel(lines[payerAt]), lines.GetRange(payerAt + 1, payeeAt - payerAt - 1));
            var payee = FromBlock(AfterLabel(lines[payeeAt]), lines.GetRange(payeeAt + 1, stop - payeeAt - 1));
            if (qr != null)
            {
                if (payer.BankCode == null && payer.Kind is PartyKind.BankAccount or PartyKind.Unknown)
                    payer = payer with { BankCode = qr.SendingBankCode, BankFromQr = true };
                else if (payer.BankCode != null && payer.BankCode != qr.SendingBankCode)
                    payer = payer with { BankCode = null };
            }
            return (payer, payee, RoleSource.Label);
        }
        if (qr == null) return (null, null, RoleSource.None);
        // ไม่มีป้าย — บล็อก = บรรทัดชื่อธนาคารล้วน + ชื่อบรรทัดก่อน + เลขปิดบางบรรทัดถัดไป (หรือก่อนหน้า) · ต้องมี 2 บล็อกพอดี และ QR ตรงบล็อกเดียว
        var banks = lines.Select((l, i) => (Code: ThaiBankCodes.FromBankLine(l), At: i)).Where(x => x.Code != null).ToList();
        if (banks.Count != 2) return (null, null, RoleSource.None);
        var blocks = banks.Select(b => BlockAt(lines, b.At, b.Code!)).ToList();
        var own = blocks.Where(b => b.BankCode == qr.SendingBankCode).ToList();
        if (own.Count != 1) return (null, null, RoleSource.None);
        return (own[0], blocks.First(b => !ReferenceEquals(b, own[0])), RoleSource.Qr);
    }

    private static SlipParty BlockAt(List<string> lines, int bankAt, string code)
    {
        string? mask = null; var kind = PartyKind.Unknown;
        foreach (var i in new[] { bankAt + 1, bankAt - 1 })
        {
            if (i < 0 || i >= lines.Count) continue;
            var (k, m) = MaskOn(lines[i]);
            if (k == PartyKind.Unknown) continue;
            (kind, mask) = (k, m); break;
        }
        var name = bankAt - 1 >= 0 && MaskOn(lines[bankAt - 1]).Kind == PartyKind.Unknown ? CleanName(lines[bankAt - 1]) : null;
        return new SlipParty(kind == PartyKind.Unknown ? PartyKind.Unknown : kind, code, false, mask, name, null);
    }

    private static SlipParty FromBlock(string? labelRest, List<string> block)
    {
        var all = new List<string>();
        if (!string.IsNullOrWhiteSpace(labelRest)) all.Add(labelRest!);
        all.AddRange(block);
        string? bank = null, mask = null, biller = null, name = null;
        var kind = PartyKind.Unknown; var cardWord = false;
        foreach (var l in all)
        {
            var b = ThaiBankCodes.FromBankLine(l);
            if (b != null) { bank ??= b; continue; }
            var bm = BillerRx.Match(l);
            if (bm.Success) { biller ??= bm.Groups[1].Value; continue; }
            var (k, m) = MaskOn(l);
            if (k != PartyKind.Unknown) { if (mask == null) { mask = m; kind = k; } continue; }
            if (CardWordRx.IsMatch(l)) cardWord = true;
            name ??= CleanName(l);
        }
        if (biller != null) kind = PartyKind.Biller;
        else if (kind == PartyKind.Card || (cardWord && kind != PartyKind.BankAccount)) kind = PartyKind.Card;
        return new SlipParty(kind, bank, false, kind is PartyKind.BankAccount or PartyKind.PromptPay or PartyKind.Card ? mask : null, name, biller);
    }

    private static (PartyKind Kind, string? Mask) MaskOn(string line)
    {
        var m = MaskCandidateRx.Match(line.Replace(" ", ""));
        return m.Success ? ClassifyMask(m.Value) : (PartyKind.Unknown, null);
    }

    /// <summary>คำแรกของบรรทัดหลังตัดสัญลักษณ์/ไอคอนที่ OCR อ่านเป็นอักษรรบกวน (๐ © @ 1) ฯลฯ)</summary>
    private static string LeadToken(string line)
    {
        var s = StripLead(line);
        var sp = s.IndexOfAny(new[] { ' ', '\t', ':' });
        return sp < 0 ? s : s[..sp];
    }

    private static string AfterLabel(string line)
    {
        var s = StripLead(line);
        var sp = s.IndexOfAny(new[] { ' ', '\t', ':' });
        return sp < 0 ? "" : s[(sp + 1)..].Trim();
    }

    private static string StripLead(string line)
    {
        var i = 0;
        while (i < line.Length && !(line[i] is >= 'ก' and <= 'ฮ' or >= 'เ' and <= 'ไ' or >= 'A' and <= 'Z' or >= 'a' and <= 'z')) i++;
        return line[i..].Trim();
    }

    private static string? CleanName(string line)
    {
        var s = StripLead(line);
        return s.Length == 0 ? null : s;
    }

    /// <summary>OCR ไทยมักอ่าน "ำ" เป็น "ํ"+"า" (U+0E4D U+0E32) — ปรับให้ป้ายภาษาไทยเทียบได้ (ใช้กับส่วนคู่กรณีเท่านั้น · ยอด/วันที่คงตัวแยกเดิม — 260 L7)</summary>
    private static string NormalizeThai(string text) => text.Replace("\r\n", "\n").Replace("ํา", "ำ");

    // ═══════════════ ผลอ่านที่เก็บลงฐาน (PaymentIntent/LineChatSlip.SlipReadJson) — ตัวเขียน/ตัวอ่านเดียว ═══════════════

    /// <summary>ผลอ่านที่เก็บ (<b>ไม่มีชื่อ · ไม่มีบันทึกช่วยจำ</b> — PDPA ม.22 เก็บเท่าที่จำเป็นต่อการจับคู่บัญชี) · ธนาคาร + เลขปิดบางที่ธนาคารปิดมาแล้ว + Biller ID + ผลถอด QR</summary>
    public sealed record Stored(string? QrBank, string? QrRef, RoleSource Roles, SlipParty? Payer, SlipParty? Payee);

    private sealed record StoredParty(string Kind, string? Bank, bool Qr, string? Mask, string? Biller);
    private sealed record StoredJson(int V, string? QrBank, string? QrRef, string Roles, StoredParty? Payer, StoredParty? Payee);

    private static readonly System.Text.Json.JsonSerializerOptions StoredOpts = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    /// <summary>JSON ที่เก็บ · ไม่มี QR และไม่มีคู่กรณี ⇒ null (ไม่เก็บอะไร)</summary>
    public static string? ToStoredJson(Reading? r)
    {
        if (r == null || (r.Qr == null && r.Payer == null && r.Payee == null)) return null;
        static StoredParty? P(SlipParty? p) => p == null ? null : new StoredParty(p.Kind.ToString(), p.BankCode, p.BankFromQr, p.AccountMask, p.BillerId);
        return System.Text.Json.JsonSerializer.Serialize(new StoredJson(1, r.Qr?.SendingBankCode, r.Qr?.TransactionRef, r.Roles.ToString(), P(r.Payer), P(r.Payee)), StoredOpts);
    }

    /// <summary>อ่าน JSON ที่เก็บ · ว่าง/รูปผิด/รุ่นไม่รู้จัก ⇒ null (ผู้เรียกถือว่า "ไม่มีผลอ่าน" = พฤติกรรมเดิม)</summary>
    public static Stored? FromStoredJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var s = System.Text.Json.JsonSerializer.Deserialize<StoredJson>(json, StoredOpts);
            if (s == null || s.V != 1) return null;
            static SlipParty? P(StoredParty? p) => p == null || !Enum.TryParse<PartyKind>(p.Kind, out var k) ? null
                // mask ที่เก็บต้องจัดชนิดได้ตรงเดิม (กันแถวที่ถูกแก้มือ/รูปเก่า) — ไม่ตรง ⇒ ทิ้ง mask (ไม่ใช่หลักฐาน)
                : new SlipParty(k, p.Bank != null && ThaiBankCodes.ByCode(p.Bank) == null && !(p.Bank.Length == 3 && p.Bank.All(char.IsAsciiDigit)) ? null : p.Bank,
                    p.Qr, p.Mask != null && ClassifyMask(p.Mask).Kind == k ? p.Mask : null, null, p.Biller);
            return new Stored(s.QrBank, s.QrRef, Enum.TryParse<RoleSource>(s.Roles, out var ro) ? ro : RoleSource.None, P(s.Payer), P(s.Payee));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>บันทึกช่วยจำ (KBIZ "Memo …") · ไม่พบ = null</summary>
    private static string? ExtractMemo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = MemoRx.Match(text);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static readonly Regex AmountRx = new(@"(?<![\d.,])\d{1,3}(?:,\d{3})*\.\d{2}(?![\d])|(?<![\d.,])\d{4,9}\.\d{2}(?![\d])", RegexOptions.Compiled);
    private static readonly Regex RefRx = new(@"[A-Z0-9]{10,25}", RegexOptions.Compiled);
    private static readonly Regex DateRx = new(@"\b(\d{1,2})[\/\-.](\d{1,2})[\/\-.](\d{2,4})\b", RegexOptions.Compiled);
    private static readonly Regex ThaiDateRx = new(@"(\d{1,2})\s*(ม\.ค\.|ก\.พ\.|มี\.ค\.|เม\.ย\.|พ\.ค\.|มิ\.ย\.|ก\.ค\.|ส\.ค\.|ก\.ย\.|ต\.ค\.|พ\.ย\.|ธ\.ค\.)\s*(\d{2,4})", RegexOptions.Compiled);
    private static readonly Regex AmountKeyword = new(@"จำนวนเงิน|จำนวน|ยอดเงิน|ยอดโอน|โอนเงิน|amount", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SlipKeyword = new(@"สำเร็จ|successful|success|รหัสอ้างอิง|เลขที่รายการ|เลขอ้างอิง|transaction\s*(id|no|ref)|ref\.?\s*no", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EnglishDateRx = new(@"\b(\d{1,2})\s+(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?,?\s+(\d{2,4})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] EnglishMonths = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
    private static readonly string[] ThaiMonths = { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." };

    /// <summary>ยอดเงินทุกตัวในข้อความ (ไม่ซ้ำ · ไม่ใช่ศูนย์ · ปัด 2 ตำแหน่ง AwayFromZero)</summary>
    public static IReadOnlyList<decimal> Amounts(string? text)
    {
        var list = new List<decimal>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (Match m in AmountRx.Matches(text))
        {
            if (!decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) continue;
            d = Math.Round(d, 2, MidpointRounding.AwayFromZero);
            if (d <= 0m || list.Contains(d)) continue;
            list.Add(d);
        }
        return list;
    }

    /// <param name="nowYear">ปี ค.ศ. ปัจจุบัน (ตีความปีสองหลัก — <see cref="TwoDigitYear"/>) · null = นาฬิกาเครื่อง</param>
    /// <param name="qrPayload">รอบ 264 P2: ข้อความที่ถอดจาก QR ในรูป (null = ไม่มี/ไม่ได้ถอด ⇒ ผลเท่าเดิมทุกประการ — ผู้เรียกเดิม 260 L7) ·
    /// QR ตรวจสลิปที่ CRC ถูก ⇒ <see cref="Reading.Ref"/> = เลขอ้างอิงจาก QR (ตัวอ่าน OCR ตัดตัวพิมพ์เล็กทิ้ง/หยิบ Biller ID แทน — เห็นบนกระดาษจริงกรุงไทย/SCB) + ถือว่าเป็นสลิปที่ทำรายการแล้ว</param>
    public static Reading Parse(string? text, int? nowYear = null, string? qrPayload = null)
    {
        var qr = ThaiSlipQr.Parse(qrPayload);
        if (string.IsNullOrWhiteSpace(text))
            return qr == null ? Reading.Empty : new Reading(Array.Empty<decimal>(), null, null, qr.TransactionRef, LooksLikeSlip: true, Qr: qr);
        var all = Amounts(text);
        var reference = qr?.TransactionRef ?? ExtractRef(text);
        var (payer, payee, roles) = Parties(text, qr);
        return new Reading(all, ConfidentAmount(text, all), ExtractDate(text, nowYear), reference,
            LooksLikeSlip: reference != null || SlipKeyword.IsMatch(text),
            Payer: payer, Payee: payee, Roles: roles, Qr: qr, Memo: ExtractMemo(text));
    }

    /// <summary>รอบ 260 (ฝ่ายค้าน L7): badge "ยอดตรงกับที่แจ้ง" ของสลิปค่าบริการ (<c>SlipOcrAssistService</c>) — ยอดที่แจ้งอยู่บนสลิป (± 0.01) ⇒ (ยอดที่แจ้ง, true) ·
    /// ไม่มี ⇒ (ยอดสูงสุด, false) · ไม่มียอดเลย ⇒ (null, null) — พฤติกรรมเดิมของ service ย้ายมาให้เทสต์ล็อกได้</summary>
    public static (decimal? Chosen, bool? Matches) BadgeAmount(IReadOnlyList<decimal> amounts, decimal expected)
    {
        if (amounts.Count == 0) return (null, null);
        return amounts.Any(a => Math.Abs(a - expected) < 0.01m) ? (expected, true) : (amounts.Max(), false);
    }

    /// <summary>ปีสองหลักบนสลิป — ตัวตีความเดียวของทั้งรูป dd/mm/yy และ "12 พ.ย. 69" (ฝ่ายค้าน L8): มากกว่า (ปี ค.ศ. ปัจจุบัน % 100) + 10 ⇒ พ.ศ. (25yy − 543) ·
    /// อื่น ๆ ⇒ ค.ศ. 20yy (ปี 2026: "69" = 2569 = 2026 · "26" = 2026)</summary>
    private static int TwoDigitYear(int yy, int nowYear)
        => yy > (nowYear % 100) + 10 ? 2500 + yy - 543 : 2000 + yy;

    private static decimal? ConfidentAmount(string text, IReadOnlyList<decimal> all)
    {
        if (all.Count == 0) return null;
        // รอบ 264 P2: OCR อ่าน "จำนวน" เป็น "จํานวน" (ํ+า) — ปรับก่อนหาคำนำ (กระดาษจริง K+ จ่ายบิล: ค่าธรรมเนียม 0.00 อ่านเป็น 660.00 ⇒ เดิมไม่มั่นใจ · ตอนนี้ "จำนวน:" ชี้ 159.00) ·
        // อีก 9 ใบในชุดกระดาษจริงผลเท่าเดิม (OcrSlipTextPartiesTests ล็อก)
        var lines = NormalizeThai(text).Split('\n');
        var keyed = new List<decimal>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!AmountKeyword.IsMatch(lines[i])) continue;
            var onLine = Amounts(lines[i]);
            if (onLine.Count == 0 && i + 1 < lines.Length) onLine = Amounts(lines[i + 1]);
            foreach (var a in onLine) if (!keyed.Contains(a)) keyed.Add(a);
        }
        if (keyed.Count == 1) return keyed[0];
        if (keyed.Count > 1) return null;            // คำว่า "จำนวน" ชี้หลายยอด — ไม่เดา
        return all.Count == 1 ? all[0] : null;
    }

    /// <summary>เลขอ้างอิงรายการ (ชุดตัวอักษร/ตัวเลขยาวสุดที่มีตัวเลข · 10–25 ตัว) · ไม่พบ = null</summary>
    private static string? ExtractRef(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return RefRx.Matches(text).Select(m => m.Value)
            .Where(v => v.Any(char.IsDigit) && v.Length >= 10)
            .OrderByDescending(v => v.Length).FirstOrDefault();
    }

    /// <summary>วันที่บนสลิป (UTC เที่ยงคืน) — dd/mm/yy(yy) ก่อน แล้ว "12 พ.ย. 69" · พ.ศ. ⇒ ค.ศ. · ไม่พบ/ผิดรูป = null</summary>
    public static DateTime? ExtractDate(string? text, int? nowYear = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var year = nowYear ?? DateTime.UtcNow.Year;
        var m = DateRx.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var d) && int.TryParse(m.Groups[2].Value, out var mo) && int.TryParse(m.Groups[3].Value, out var y))
        {
            if (y < 100) y = TwoDigitYear(y, year);
            else if (y > 2400) y -= 543;      // พ.ศ. → ค.ศ.
            if (Build(y, mo, d) is DateTime dt) return dt;
        }
        var t = ThaiDateRx.Match(text);
        if (t.Success && int.TryParse(t.Groups[1].Value, out var td) && int.TryParse(t.Groups[3].Value, out var ty))
        {
            var month = Array.IndexOf(ThaiMonths, t.Groups[2].Value) + 1;
            if (ty < 100) ty = TwoDigitYear(ty, year);
            else if (ty > 2400) ty -= 543;
            return Build(ty, month, td);
        }
        // รอบ 264 P2: สลิปภาษาอังกฤษ (ttb "2 Nov 26, 4:53 PM" · KBIZ "2 Nov 26 11:44") — ลองหลังสองรูปเดิม (ใบที่อ่านได้อยู่แล้วไม่ถูกแตะ)
        var e = EnglishDateRx.Match(text);
        if (e.Success && int.TryParse(e.Groups[1].Value, out var ed) && int.TryParse(e.Groups[3].Value, out var ey))
        {
            var month = Array.FindIndex(EnglishMonths, x => e.Groups[2].Value.StartsWith(x, StringComparison.OrdinalIgnoreCase)) + 1;
            if (ey < 100) ey = TwoDigitYear(ey, year);
            else if (ey > 2400) ey -= 543;
            return Build(ey, month, ed);
        }
        return null;
    }

    private static DateTime? Build(int y, int mo, int d)
    {
        if (d is < 1 or > 31 || mo is < 1 or > 12 || y is < 2000 or > 2100) return null;
        if (d > DateTime.DaysInMonth(y, mo)) return null;
        return new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Utc);
    }
}
