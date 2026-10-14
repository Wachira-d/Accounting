using System.Globalization;
using System.Text.RegularExpressions;
using Accounting.Models.Enums;
using Accounting.Services.Settlement.Adapters;

namespace Accounting.Helpers;

/// <summary>
/// ผลแปลงค่าหนึ่งช่อง — <b>ไม่ throw</b>: ค่าเพี้ยน = <see cref="Ok"/> false + <see cref="IssueCode"/> + ข้อความไทย (+ ค่าแนะนำถ้ามี)
/// <para><see cref="Ok"/> true + <see cref="IssueCode"/> ≠ null = แปลงได้แต่มีคำเตือน (เช่นวันที่สลับวัน/เดือนได้จริง) ·
/// <see cref="Value"/> null + <see cref="Ok"/> true + ไม่มี issue = ช่องว่าง (ไม่มีค่า)</para>
/// <para><see cref="Suggested"/> = ค่าแนะนำในรูปมาตรฐาน (ผู้ใช้กด "ใช้คำแนะนำ") — ระบบไม่ใส่เองเมื่อ <see cref="Ok"/> false (F2 ข้อ 3)</para>
/// </summary>
public sealed record ImportValue<T>(T Value, bool Ok, string? IssueCode = null, string? Message = null, string? Suggested = null)
{
    /// <summary>แปลงได้แต่มีคำเตือนที่ต้องรับทราบ</summary>
    public bool IsWarning => Ok && IssueCode != null;
}

/// <summary>
/// **แปลงค่าจากไฟล์นำเข้าเป็นค่ามาตรฐาน — ตัวเดียวของขั้น Transform** (รอบ 205 T0 · IMPORT_ETL_REVIEW §2.3)
///
/// <para>แทน <c>DateTime.TryParse</c>×23 · <c>decimal.TryParse</c>×41 · <c>bool.TryParse</c>×2 ที่ขึ้นกับ culture ใน ImportExportService
/// ("ใช่"/"Y" = false · dd/MM↔MM/dd สลับเงียบ) · <b>ไม่เขียนสูตรซ้ำ</b> — มอบให้ owner เดิม: วันที่ = <see cref="ThaiDate.TryParseFlexible"/>
/// (+ <see cref="SettlementValueParser.TryParseDate(string?, SettlementDateOrder, out DateTime?)"/> สำหรับ เดือน/วัน · ชื่อเดือนอังกฤษ · เวลาท้ายค่า) ·
/// ยอดเงิน = <see cref="SettlementValueParser.TryParseAmount"/> · เลขผู้เสียภาษี = <see cref="ThaiTaxIdValidator"/> · สาขา =
/// <see cref="TaxBranchCode.TryNormalize"/> · เลขไทย = <see cref="SettlementValueParser.ThaiDigitsToAscii"/> · เลขเสียหลักจาก Excel =
/// <see cref="SettlementValueParser.IdLostPrecision"/></para>
///
/// <para>G6: pure · ไม่ throw · ไม่ขึ้นกับ culture · ไม่แต่งค่า (ไม่รู้ = issue ให้คน/ชั้นถัดไปตัดสิน)</para>
/// </summary>
public static class ImportValueNormalizer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ═══════════════════════ วันที่ ═══════════════════════

    /// <summary>
    /// วันที่ → วันตามปฏิทิน 00:00 Kind=Utc · พ.ศ. → ค.ศ. · Excel serial (เลขล้วน 10000–80000 ⇒ 1927–2119) · ISO ไม่สลับ ·
    /// <paramref name="order"/> = <see cref="ImportDateOrder.MonthDayYear"/> เท่านั้นที่อ่านแบบ เดือน/วัน · Auto/ไทย = วัน/เดือน และค่าที่สลับได้จริง
    /// ("05/08/2569") ติดคำเตือน <c>date-order-assumed</c> เมื่อเป็น Auto
    /// </summary>
    public static ImportValue<DateTime?> Date(string? raw, ImportDateOrder order = ImportDateOrder.Auto)
    {
        var s = Clean(raw);
        if (s.Length == 0) return Empty<DateTime?>();

        if (Regex.IsMatch(s, @"^[0-9]{5}(\.[0-9]+)?$")
            && double.TryParse(s, NumberStyles.AllowDecimalPoint, Inv, out var serial) && serial is >= 10000d and <= 80000d)
        {
            var d = DateTime.FromOADate(serial).Date;
            return new(DateTime.SpecifyKind(d, DateTimeKind.Utc), true);
        }

        if (order == ImportDateOrder.MonthDayYear)
        {
            if (SettlementValueParser.TryParseDate(s, SettlementDateOrder.MonthDayYear, out var md) && md is { } mdv)
                return new(mdv, true);
            return Bad<DateTime?>("date-invalid", $"อ่านวันที่ \"{raw?.Trim()}\" ไม่ได้ (รูปแบบ เดือน/วัน/ปี)");
        }

        if (ThaiDate.TryParseFlexible(s, out var th))
            return OrderWarning(s, order, new ImportValue<DateTime?>(th, true));
        // ชื่อเดือนอังกฤษ ("05-Aug-2026") · มีเวลา/เขตเวลาท้ายค่า ("05/08/2569 10:30") — ตัวอ่านของ settlement (วัน/เดือน)
        if (SettlementValueParser.TryParseDate(s, SettlementDateOrder.DayMonthYear, out var dm) && dm is { } dmv)
            return OrderWarning(s, order, new ImportValue<DateTime?>(dmv, true));
        return Bad<DateTime?>("date-invalid",
            $"อ่านวันที่ \"{raw?.Trim()}\" ไม่ได้ — ใช้รูปแบบ วัน/เดือน/ปี (เช่น 05/08/2569) หรือ ปี-เดือน-วัน (2026-08-05)");
    }

    private static ImportValue<DateTime?> OrderWarning(string s, ImportDateOrder order, ImportValue<DateTime?> v)
    {
        if (order != ImportDateOrder.Auto || !SettlementValueParser.IsOrderSensitive(s)) return v;
        return v with
        {
            IssueCode = "date-order-assumed",
            Message = $"\"{s}\" อ่านได้ทั้ง วัน/เดือน และ เดือน/วัน — ระบบอ่านเป็น วัน/เดือน (แบบไทย) · ถ้าไฟล์เป็น เดือน/วัน ให้ตั้งรูปแบบวันที่ของคอลัมน์",
        };
    }

    /// <summary>วันที่รูปมาตรฐานสำหรับ writer เดิมหลัง Transform — <c>yyyy-MM-dd</c> invariant (ไม่มีเวลา · ไม่ขึ้นกับ culture)</summary>
    public static string CanonicalDate(DateTime value) => value.ToString("yyyy-MM-dd", Inv);

    // ═══════════════════════ ตัวเลข ═══════════════════════

    /// <summary>ยอดเงิน (2 ตำแหน่ง · AwayFromZero) — คั่นหลักพัน · วงเล็บ/ลบท้าย = ติดลบ · ฿/THB/บาท · เลขไทย (owner = <see cref="SettlementValueParser.TryParseAmount"/>)</summary>
    public static ImportValue<decimal?> Amount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Empty<decimal?>();
        if (SettlementValueParser.TryParseAmount(raw, out var v)) return new(v, true);
        return Bad<decimal?>("amount-invalid", $"อ่านจำนวนเงิน \"{raw.Trim()}\" ไม่ได้ — ใช้ตัวเลข เช่น 1,234.50 หรือ (1,234.50) สำหรับค่าติดลบ");
    }

    /// <summary>ยอดเงินรูปมาตรฐานสำหรับ writer เดิม — invariant (จุดทศนิยม · ไม่มีคั่นหลักพัน)</summary>
    public static string CanonicalAmount(decimal value) => value.ToString(Inv);

    /// <summary>จำนวน/อัตราที่ไม่ใช่เงิน (ไม่ปัดเศษ) — คั่นหลักพัน · เลขไทย · เครื่องหมายลบนำหน้า</summary>
    public static ImportValue<decimal?> Number(string? raw)
    {
        var s = Clean(raw).Replace(" ", "", StringComparison.Ordinal);
        if (s.Length == 0 || s == "-") return Empty<decimal?>();
        if (Regex.IsMatch(s, @"^[+\-]?([0-9]{1,3}(,[0-9]{3})+|[0-9]+)(\.[0-9]+)?$")
            && decimal.TryParse(s.Replace(",", "", StringComparison.Ordinal),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Inv, out var d))
            return new(d, true);
        return Bad<decimal?>("number-invalid", $"อ่านตัวเลข \"{raw?.Trim()}\" ไม่ได้");
    }

    // ═══════════════════════ ใช่/ไม่ใช่ ═══════════════════════

    private static readonly HashSet<string> TrueWords = new(StringComparer.OrdinalIgnoreCase)
        { "true", "yes", "y", "1", "ใช่", "มี", "ใช้", "เปิด", "✓", "✔", "x", "/", "on", "t" };
    private static readonly HashSet<string> FalseWords = new(StringComparer.OrdinalIgnoreCase)
        { "false", "no", "n", "0", "ไม่", "ไม่ใช่", "ไม่มี", "ไม่ใช้", "ปิด", "✗", "✘", "off", "f" };

    /// <summary>ใช่/ไม่ใช่ — "ใช่"/"Y"/"1"/"✓"/"มี" = true · "ไม่ใช่"/"N"/"0"/"ไม่มี" = false · คำอื่น = issue (ไม่เดา)</summary>
    public static ImportValue<bool?> Flag(string? raw)
    {
        var s = Clean(raw);
        if (s.Length == 0) return Empty<bool?>();
        if (TrueWords.Contains(s)) return new(true, true);
        if (FalseWords.Contains(s)) return new(false, true);
        return Bad<bool?>("flag-invalid", $"\"{s}\" ไม่ใช่ค่าใช่/ไม่ใช่ — ใช้ ใช่/ไม่ใช่ · Y/N · 1/0");
    }

    // ═══════════════════════ เลขผู้เสียภาษี / สาขา ═══════════════════════

    /// <summary>
    /// เลขผู้เสียภาษี 13 หลัก + checksum (<see cref="ThaiTaxIdValidator"/>) · ขีด/ช่องว่างตัดออก · รูป scientific จาก Excel ⇒ เสียหลักแล้ว
    /// (issue · ไม่มีค่าแนะนำ — กู้ไม่ได้) · 12 หลักที่เติม "0" หน้าแล้ว checksum ผ่าน ⇒ <b>แนะนำ</b> (Excel ตัดศูนย์นำหน้า) ไม่ใส่เอง
    /// </summary>
    public static ImportValue<string?> TaxId(string? raw)
    {
        var s = Clean(raw);
        if (s.Length == 0) return Empty<string?>();
        if (SettlementValueParser.IdLostPrecision(s))
            return Bad<string?>("taxid-lost-precision",
                $"เลขผู้เสียภาษี \"{s}\" ถูก Excel แปลงเป็นตัวเลขจนหลักท้ายหาย — จัดคอลัมน์เป็น \"ข้อความ\" ในไฟล์ต้นทางแล้วส่งออกใหม่");
        var digits = new string(s.Where(ch => ch is >= '0' and <= '9').ToArray());
        if (digits.Length != s.Count(ch => ch is not ('-' or ' ')))
            return Bad<string?>("taxid-invalid", $"เลขผู้เสียภาษี \"{s}\" มีอักขระที่ไม่ใช่ตัวเลข");
        if (digits.Length == 12 && ThaiTaxIdValidator.Check("0" + digits).IsValid)
            return Bad<string?>("taxid-leading-zero", $"เลขผู้เสียภาษี \"{s}\" มี 12 หลัก — น่าจะเป็น \"0{digits}\" (Excel ตัดศูนย์นำหน้า)",
                suggested: "0" + digits);
        var check = ThaiTaxIdValidator.Check(digits);
        if (!check.IsValid)
            return Bad<string?>("taxid-invalid", $"เลขผู้เสียภาษี \"{s}\" ไม่ถูกต้อง — {check.Reason}");
        return new(digits, true);
    }

    /// <summary>รหัสสาขา 5 หลัก (<see cref="TaxBranchCode.TryNormalize"/>) · "สำนักงานใหญ่"/"สนญ"/"HQ"/"Head Office" = 00000 ·
    /// "สาขาที่ 3"/"สาขา 00003"/"Branch 3" ⇒ ตัวเลขในข้อความ</summary>
    public static ImportValue<string?> BranchCode(string? raw)
    {
        var s = Clean(raw);
        if (s.Length == 0) return Empty<string?>();
        if (TaxBranchCode.IsHeadOfficeName(s) || TaxBranchCode.IsHeadOfficeName(s.ToUpperInvariant())
            || s.Equals("head office", StringComparison.OrdinalIgnoreCase))
            return new(TaxBranchCode.HeadOffice, true);
        var m = Regex.Match(s, @"^(?:สาขาที่|สาขา|branch(?:\s*no\.?)?)\s*([0-9]{1,5})$", RegexOptions.IgnoreCase);
        var candidate = m.Success ? m.Groups[1].Value : s;
        if (TaxBranchCode.TryNormalize(candidate, out var code, out var error) && code != null) return new(code, true);
        return Bad<string?>("branch-invalid", error ?? $"รหัสสาขา \"{s}\" ไม่ถูกต้อง — ต้องเป็นตัวเลข 5 หลัก (สำนักงานใหญ่ = 00000)");
    }

    // ═══════════════════════ VAT ═══════════════════════

    private static readonly HashSet<string> ExemptWords = new(StringComparer.OrdinalIgnoreCase)
        { "ยกเว้น", "ยกเว้นภาษี", "exempt", "vat exempt", "non-vat", "nonvat", "ไม่มีภาษี" };

    /// <summary>อัตรา VAT (%) — "7%"/"7"/"0.07" (เซลล์เปอร์เซ็นต์ของ Excel) ⇒ 7 · "0" ⇒ 0 · "ยกเว้น"/"exempt" ⇒ ค่า null + <c>vat-exempt</c>
    /// (ไม่ใช่ 0% — §81 ต่างจาก §80/1) · อัตราที่ไม่ใช่ 0/7 ⇒ คำเตือน</summary>
    public static ImportValue<decimal?> VatRate(string? raw)
    {
        var s = Clean(raw);
        if (s.Length == 0) return Empty<decimal?>();
        if (ExemptWords.Contains(s))
            return new(null, true, "vat-exempt", "ยกเว้นภาษีมูลค่าเพิ่ม (ม.81) — ไม่ใช่อัตรา 0% (ม.80/1)");
        var n = Number(s.TrimEnd('%').Trim());
        if (!n.Ok || n.Value is not { } rate || rate < 0 || rate > 100)
            return Bad<decimal?>("vat-rate-invalid", $"อัตราภาษี \"{s}\" ไม่ถูกต้อง — ใช้ 7 หรือ 7% (0 = อัตราศูนย์ · \"ยกเว้น\" = ยกเว้นภาษี)");
        if (rate > 0 && rate < 1 && !s.EndsWith('%')) rate = Math.Round(rate * 100m, 2, MidpointRounding.AwayFromZero);
        if (rate != 0m && rate != 7m)
            return new(rate, true, "vat-rate-unusual", $"อัตราภาษี {rate.ToString(Inv)}% ไม่ใช่อัตราปกติ (7% / 0%) — ตรวจว่าถูกต้อง");
        return new(rate, true);
    }

    // ═══════════════════════ ติดต่อ ═══════════════════════

    /// <summary>เบอร์โทร — เก็บเฉพาะตัวเลข (+66 → 0) · 9–10 หลัก = ปกติ · นอกนั้นเก็บตามไฟล์พร้อมคำเตือน (ไม่ทิ้งข้อมูล)</summary>
    public static ImportValue<string?> Phone(string? raw)
    {
        var s = Clean(raw);
        if (s.Length == 0) return Empty<string?>();
        var digits = new string(s.Where(ch => ch is >= '0' and <= '9').ToArray());
        if (s.TrimStart().StartsWith("+66", StringComparison.Ordinal) && digits.StartsWith("66", StringComparison.Ordinal))
            digits = "0" + digits[2..];
        if (digits.Length is 9 or 10 && digits[0] == '0') return new(digits, true);
        return new(s, true, "phone-unusual", $"เบอร์โทร \"{s}\" ไม่ใช่รูปแบบเบอร์ไทย 9–10 หลัก — เก็บตามไฟล์");
    }

    private static readonly Regex EmailRe = new(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$", RegexOptions.Compiled);

    /// <summary>อีเมล — ตัดช่องว่าง · ตัวเล็ก · รูปผิด = issue</summary>
    public static ImportValue<string?> Email(string? raw)
    {
        var s = Clean(raw);
        if (s.Length == 0) return Empty<string?>();
        var e = s.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        if (EmailRe.IsMatch(e)) return new(e, true);
        return Bad<string?>("email-invalid", $"อีเมล \"{s}\" รูปแบบไม่ถูกต้อง");
    }

    // ═══════════════════════ ข้อความ / enum / เลขอ้างอิง ═══════════════════════

    /// <summary>ข้อความ — ตัดช่องว่างหัวท้าย · ยุบช่องว่างซ้อน · ตัดอักขระควบคุม · ยาวเกิน <paramref name="maxLength"/> ⇒ ตัดพร้อมคำเตือน</summary>
    public static ImportValue<string?> Text(string? raw, int? maxLength = null)
    {
        if (raw == null) return Empty<string?>();
        var s = Regex.Replace(new string(raw.Where(ch => !char.IsControl(ch) || ch == '\n').ToArray()), @"[ \t\u00A0]+", " ").Trim();
        if (s.Length == 0) return Empty<string?>();
        if (maxLength is int max && s.Length > max)
            return new(s[..max], true, "text-truncated", $"ข้อความยาว {s.Length} ตัวอักษร เกินที่เก็บได้ {max} — ตัดส่วนท้ายออก");
        return new(s, true);
    }

    /// <summary>เลขอ้างอิง/เลขเอกสาร (ห้ามผ่านตัวเลข) — รูป scientific จาก Excel = เสียหลักแล้ว ⇒ issue (กู้ไม่ได้ · F2 ข้อ 3)</summary>
    public static ImportValue<string?> Reference(string? raw)
    {
        var t = Text(raw, 200);
        if (t.Value != null && SettlementValueParser.IdLostPrecision(t.Value))
            return Bad<string?>("reference-lost-precision",
                $"เลขอ้างอิง \"{t.Value}\" ถูก Excel แปลงเป็นตัวเลขจนหลักท้ายหาย — จัดคอลัมน์เป็น \"ข้อความ\" ในไฟล์ต้นทางแล้วส่งออกใหม่");
        return t;
    }

    /// <summary>ค่า enum — ชื่อ (ไม่สนตัวพิมพ์) · คำพ้องจาก <paramref name="aliases"/> (ภาษาไทย ฯลฯ) · เลขที่นิยามไว้จริง · นอกนั้น issue พร้อมรายชื่อที่รับได้</summary>
    public static ImportValue<T?> Enum<T>(string? raw, IReadOnlyDictionary<string, T>? aliases = null) where T : struct, System.Enum
    {
        var s = Clean(raw);
        if (s.Length == 0) return Empty<T?>();
        if (aliases != null)
            foreach (var kv in aliases)
                if (string.Equals(kv.Key.Trim(), s, StringComparison.OrdinalIgnoreCase)) return new(kv.Value, true);
        if (int.TryParse(s, NumberStyles.None, Inv, out var n))
        {
            var byNumber = (T)System.Enum.ToObject(typeof(T), n);
            if (System.Enum.IsDefined(typeof(T), byNumber)) return new(byNumber, true);
        }
        else if (System.Enum.TryParse<T>(s, ignoreCase: true, out var byName) && System.Enum.IsDefined(typeof(T), byName))
            return new(byName, true);
        var allowed = string.Join(", ", System.Enum.GetNames(typeof(T)).Concat(aliases?.Keys ?? Enumerable.Empty<string>()));
        return Bad<T?>("enum-invalid", $"\"{s}\" ไม่อยู่ในค่าที่รับได้ ({allowed})");
    }

    // ═══════════════════════ ภายใน ═══════════════════════

    /// <summary>ตัดช่องว่าง · เลขไทย → อารบิก · ขีดลบยูนิโค้ด → "-" ไม่แตะ (ตัวแปลงของ owner จัดการเอง)</summary>
    private static string Clean(string? raw)
        => raw == null ? "" : SettlementValueParser.ThaiDigitsToAscii(raw).Replace('\u00A0', ' ').Trim();

    private static ImportValue<T> Empty<T>() => new(default!, true);

    private static ImportValue<T> Bad<T>(string code, string message, string? suggested = null)
        => new(default!, false, code, message, suggested);
}
