using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Accounting.Helpers;

namespace Accounting.Services.Helpers;

/// <summary>
/// **อ่าน statement ธนาคารแบบ OFX** (Open Financial Exchange) — 1.x (SGML: แท็กใบไม่มีแท็กปิด) และ 2.x (XML) ด้วยตัวเดียวกัน
/// (รอบ 205 ทีม C · IMPORT_ETL_REVIEW §1 แถว 7)
///
/// <para>═══ ที่มา ═══ open-banking เดิม "อ่าน" OFX ด้วยการนับบรรทัดที่มี <c>&lt;STMTTRN&gt;</c> (หรือขึ้นต้นด้วย "D") แล้วตอบว่าเป็น
/// "รายการใหม่" ทั้งหมด <b>โดยไม่บันทึกอะไรเลย</b> = HTTP 200 โกหก · ตอนนี้แยกบรรทัดจริงแล้วลงผ่านตัวเขียนตัวเดียว
/// (<c>BankService.ImportParsedStatementRowsAsync</c>)</para>
///
/// <para>อ่าน: <c>STMTTRN</c> (DTPOSTED · TRNAMT · FITID · NAME · MEMO · TRNTYPE · REFNUM/CHECKNUM) · <c>BANKACCTFROM</c>/<c>CCACCTFROM</c>
/// (ACCTID · BANKID) · <c>CURDEF</c> · <c>LEDGERBAL</c> (BALAMT · DTASOF) · เครื่องหมายของ TRNAMT เป็นตัวตัดสินทิศ (ตามมาตรฐาน OFX —
/// บวก = เงินเข้า) · วันที่ที่มีเขตเวลา <c>[+7:ICT]</c>/<c>[-5:EST]</c> แปลงเป็นเวลาไทย · ไม่มีเขตเวลา = ตามไฟล์</para>
/// <para>ไฟล์มี statement หลายบัญชี ⇒ ล้มดังพร้อมทางไปต่อ (ส่งออกทีละบัญชี) · ไม่พบรายการ ⇒ ผลว่าง (ผู้เรียกบอก "ไม่พบรายการ")</para>
/// <para>G6: pure · ไม่ขึ้นกับ culture ของเครื่อง</para>
/// </summary>
public static class BankOfxParser
{
    /// <summary>ผลอ่าน OFX</summary>
    /// <param name="AccountId">ACCTID ของ BANKACCTFROM/CCACCTFROM</param>
    /// <param name="LedgerBalance">LEDGERBAL/BALAMT — ยอดปิดตามธนาคาร</param>
    /// <param name="Notes">ข้อสังเกตที่ผู้ใช้ควรรู้ (TRNTYPE ขัดเครื่องหมาย · บรรทัดที่อ่านไม่ได้)</param>
    public sealed record Result(
        List<BankCsvParser.Row> Rows,
        List<string> Skipped,
        string? AccountId,
        string? BankId,
        string? Currency,
        decimal? LedgerBalance,
        DateTime? LedgerBalanceDate,
        string Version,
        List<string> Notes);

    private static readonly Regex Token = new(@"<(/?)([A-Za-z0-9.]+)\s*>([^<]*)", RegexOptions.Compiled);
    private static readonly HashSet<string> DebitTypes = new(StringComparer.OrdinalIgnoreCase)
        { "DEBIT", "PAYMENT", "FEE", "SRVCHG", "ATM", "POS", "CHECK", "CASH", "DIRECTDEBIT", "REPEATPMT" };
    private static readonly HashSet<string> CreditTypes = new(StringComparer.OrdinalIgnoreCase)
        { "CREDIT", "INT", "DIV", "DEP", "DIRECTDEP" };

    public static Result Parse(byte[] bytes)
    {
        var text = DecodeOfx(bytes);
        var version = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || text.Contains("OFXHEADER=\"200\"", StringComparison.OrdinalIgnoreCase) ? "2.x" : "1.x";
        var ofxStart = text.IndexOf("<OFX", StringComparison.OrdinalIgnoreCase);
        if (ofxStart < 0)
            throw new ImportFormatException("ofx-invalid",
                "ไฟล์ OFX ไม่มีส่วนข้อมูล <OFX> — ไฟล์อาจเสียหรือไม่ใช่ OFX ส่งออก statement จากธนาคารใหม่ (OFX หรือ CSV)");
        var body = text[ofxStart..];

        var rows = new List<BankCsvParser.Row>();
        var skipped = new List<string>();
        var notes = new List<string>();
        var accounts = new List<string>();
        string? accountId = null, bankId = null, currency = null;
        decimal? ledger = null;
        DateTime? ledgerDate = null;

        Dictionary<string, string>? txn = null;
        string? aggregate = null;   // BANKACCTFROM / CCACCTFROM / LEDGERBAL ที่กำลังอ่าน
        var txnNo = 0;
        var typeConflicts = 0;

        void FinishTxn()
        {
            if (txn == null) return;
            txnNo++;
            var t = txn;
            txn = null;
            var label = t.TryGetValue("FITID", out var fid) ? $"รายการ {txnNo} (FITID {fid})" : $"รายการ {txnNo}";
            if (!t.TryGetValue("DTPOSTED", out var dts) || ParseOfxDate(dts) is not { } date)
            {
                skipped.Add($"{label}: วันที่ (DTPOSTED) อ่านไม่ได้ '{t.GetValueOrDefault("DTPOSTED")}'");
                return;
            }
            if (!t.TryGetValue("TRNAMT", out var amts) || ParseOfxAmount(amts) is not { } amount || amount == 0m)
            {
                skipped.Add($"{label}: จำนวนเงิน (TRNAMT) อ่านไม่ได้หรือเป็นศูนย์ '{t.GetValueOrDefault("TRNAMT")}'");
                return;
            }
            var type = t.GetValueOrDefault("TRNTYPE");
            if (type != null && ((amount > 0 && DebitTypes.Contains(type)) || (amount < 0 && CreditTypes.Contains(type))))
                typeConflicts++;
            var name = Clean(t.GetValueOrDefault("NAME"));
            var memo = Clean(t.GetValueOrDefault("MEMO"));
            var desc = name == null ? memo
                : memo == null || string.Equals(memo, name, StringComparison.OrdinalIgnoreCase) ? name
                : $"{name} | {memo}";
            var reference = Clean(t.GetValueOrDefault("REFNUM")) ?? Clean(t.GetValueOrDefault("CHECKNUM"));
            rows.Add(new BankCsvParser.Row(
                date,
                string.IsNullOrWhiteSpace(desc) ? "(ไม่มีรายละเอียด)" : desc!,
                amount < 0 ? -amount : 0m,
                amount > 0 ? amount : 0m,
                null,
                reference,
                null,
                ExternalId: Clean(fid),
                Payee: name,
                RowNumber: txnNo));
        }

        foreach (Match m in Token.Matches(body))
        {
            var closing = m.Groups[1].Value == "/";
            var tag = m.Groups[2].Value.ToUpperInvariant();
            var value = m.Groups[3].Value.Trim();

            if (!closing)
            {
                switch (tag)
                {
                    case "STMTTRN":
                        FinishTxn();   // SGML หลวม: บางธนาคารไม่ปิด </STMTTRN> — เปิดใบใหม่ = ปิดใบเก่า
                        txn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        continue;
                    case "BANKACCTFROM":
                    case "CCACCTFROM":
                    case "LEDGERBAL":
                        FinishTxn();
                        aggregate = tag;
                        continue;
                    case "STMTRS":
                    case "CCSTMTRS":
                        FinishTxn();
                        continue;
                }
                if (value.Length == 0) continue;   // แท็กกลุ่มอื่น / แท็กใบของ XML ที่ค่าอยู่ก่อนแท็กปิด (ค่าว่าง)
                if (txn != null) { txn[tag] = value; continue; }
                switch (aggregate)
                {
                    case "BANKACCTFROM" or "CCACCTFROM" when tag == "ACCTID":
                        accountId = WebUtility.HtmlDecode(value);
                        accounts.Add(accountId);
                        break;
                    case "BANKACCTFROM" or "CCACCTFROM" when tag == "BANKID":
                        bankId = WebUtility.HtmlDecode(value);
                        break;
                    case "LEDGERBAL" when tag == "BALAMT":
                        ledger = ParseOfxAmount(value);
                        break;
                    case "LEDGERBAL" when tag == "DTASOF":
                        ledgerDate = ParseOfxDate(value);
                        break;
                    default:
                        if (tag == "CURDEF") currency = value.ToUpperInvariant();
                        break;
                }
                continue;
            }

            switch (tag)
            {
                case "STMTTRN":
                case "BANKTRANLIST":
                    FinishTxn();
                    break;
                case "BANKACCTFROM":
                case "CCACCTFROM":
                case "LEDGERBAL":
                    aggregate = null;
                    break;
            }
        }
        FinishTxn();

        var distinctAccounts = accounts.Distinct(StringComparer.Ordinal).ToList();
        if (distinctAccounts.Count > 1)
            throw new ImportFormatException("ofx-multi-account",
                $"ไฟล์ OFX มี statement หลายบัญชี ({string.Join(", ", distinctAccounts)}) — ส่งออกทีละบัญชีแล้วนำเข้าเข้าบัญชีที่ตรงกัน");
        if (typeConflicts > 0)
            notes.Add($"ℹ️ {typeConflicts} รายการในไฟล์ OFX มีชนิดรายการ (TRNTYPE) ขัดกับเครื่องหมายของยอด — ระบบยึดเครื่องหมายตามมาตรฐาน OFX (บวก = เงินเข้า)");

        return new Result(rows, skipped, accountId, bankId, currency, ledger, ledgerDate, version, notes);
    }

    /// <summary>ข้อความของ OFX — XML ประกาศ encoding / หัว SGML <c>CHARSET:</c> · UTF-8 เข้มงวดก่อน → Windows-874 (ตัวถอดกลาง) ·
    /// <c>CHARSET:1252</c> ที่ไม่ใช่ UTF-8 = Windows-1252 (ไฟล์ OFX จากโปรแกรมฝั่งตะวันตก)</summary>
    private static string DecodeOfx(byte[] bytes)
    {
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        if (Regex.IsMatch(head, @"CHARSET\s*:\s*1252", RegexOptions.IgnoreCase))
        {
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                var latin = CodePagesEncodingProvider.Instance.GetEncoding(1252);
                if (latin != null) return latin.GetString(bytes);
            }
        }
        return ImportTabularReader.Decode(bytes);
    }

    private static string? Clean(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = Regex.Replace(WebUtility.HtmlDecode(v), @"\s+", " ").Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>TRNAMT/BALAMT — ทศนิยมจุด (หรือจุลภาคตามมาตรฐาน OFX เมื่อไม่มีจุด) · เครื่องหมายนำหน้า · ปัด 2 ตำแหน่ง AwayFromZero</summary>
    internal static decimal? ParseOfxAmount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (s.Contains(',') && !s.Contains('.')) s = s.Replace(',', '.');
        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d))
            return null;
        return Math.Round(d, 2, MidpointRounding.AwayFromZero);
    }

    private static readonly Regex OfxDate = new(
        @"^(?<y>\d{4})(?<mo>\d{2})(?<d>\d{2})(?:(?<h>\d{2})(?<mi>\d{2})(?:(?<s>\d{2})(?:\.\d+)?)?)?\s*(?:\[(?<off>[+\-]?\d+(?:\.\d+)?)(?::[^\]]*)?\])?$",
        RegexOptions.Compiled);

    /// <summary>DTPOSTED/DTASOF: <c>YYYYMMDD[HHMMSS[.XXX]][[±gmt:tz]]</c> · มีเขตเวลา ⇒ แปลงเป็นเวลาไทย (+7) · ปี พ.ศ. ⇒ ค.ศ.</summary>
    internal static DateTime? ParseOfxDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = OfxDate.Match(raw.Trim());
        if (!m.Success) return null;
        var y = ThaiDate.NormalizeYear(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture));
        int G(string n) => m.Groups[n].Success ? int.Parse(m.Groups[n].Value, CultureInfo.InvariantCulture) : 0;
        DateTime local;
        try { local = new DateTime(y, G("mo"), G("d"), G("h"), G("mi"), G("s"), DateTimeKind.Unspecified); }
        catch (ArgumentOutOfRangeException) { return null; }
        if (!m.Groups["off"].Success) return local;
        if (!decimal.TryParse(m.Groups["off"].Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var offHours)) return local;
        if (!m.Groups["h"].Success) return local;   // วันที่ล้วน = วันตามปฏิทินของธนาคาร (ไม่เลื่อนวันเพราะเขตเวลา)
        var bangkok = local.AddHours((double)(7m - offHours));
        return DateTime.SpecifyKind(bangkok, DateTimeKind.Unspecified);
    }
}
