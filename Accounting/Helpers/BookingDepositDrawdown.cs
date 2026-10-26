using System.Text.RegularExpressions;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>บรรทัดสมุดรายวัน "ที่อยู่ในสมุด" ของบัญชีมัดจำ/รับล่วงหน้า ที่ผูกการจองหนึ่งรายการ (ข้อเท็จจริงจากฐาน — tenant แล้ว)</summary>
public sealed record DepositLedgerLine(Guid JournalEntryId, string EntryNumber, Guid AccountId, decimal Debit, decimal Credit);

/// <summary>บัญชีหนึ่งที่ยอดมัดจำไม่พอ — <paramref name="Requested"/> = ยอดที่จะตัด (หรือยอดที่ถูกตัดไปแล้ว ฝั่งยกเลิก) ·
/// <paramref name="Available"/> = ยอดมัดจำคงเหลือที่มีจริง (Σ Cr − Dr ของการจองนี้บนบัญชีนั้น · หลังเอาส่วนที่จะยกเลิกออกแล้ว ฝั่งยกเลิก)</summary>
public sealed record DepositShortfall(Guid AccountId, decimal Requested, decimal Available);

/// <summary>
/// **"JE ที่ตัดเงินมัดจำ/รับล่วงหน้าของการจอง มีมัดจำให้ตัดจริงไหม" — ตัวตัดสินตัวเดียว** (ทีม S 2026-10-26 · ผู้ใช้รายงาน)
///
/// <para><b>บั๊กที่มา:</b> การจอง #149433 — ใบรับมัดจำ RV-202609-0070 (Dr ธนาคาร 1,000 / Cr 21510) ถูกยกเลิกและกลับรายการแล้วด้วย RV-202609-0097
/// ต่อมาระบบภายนอกยิง <c>POST /api/integration/journals</c> เข้ามา: JV-INT-202609-0009 "รับรู้รายได้ Checkout" อ้างอิง RES-149433-CHK
/// Dr 21510 1,000 / Cr รายได้ 934.58 / Cr ภาษีขาย 65.42 ⇒ ระบบรับเข้า "ผ่านรายการ" ทันที (ด่านเดียวคือผังมีอยู่ + Dr=Cr) ⇒ 21510 ติดลบ 1,000 ·
/// รับรู้รายได้และ<b>ภาษีขาย</b>ของเงินที่คืนไปแล้ว</para>
///
/// <para><b>กติกา:</b>
/// <list type="number">
/// <item>ใช้เฉพาะ JE ที่<b>อ้างการจอง</b> (<see cref="ExtractKeys"/> — <c>RES-123456</c> ในเลขอ้างอิง/เลขจอง หรือ "การจอง #123456" ในคำอธิบาย)
///   และมีขา <b>Dr สุทธิ</b>บนบัญชีมัดจำ/รับล่วงหน้า (<see cref="IsDepositLiabilityAccount"/>) — JE อื่นไม่แตะ (พฤติกรรมเดิมทุกตัวอักษร)</item>
/// <item>ยอดคงเหลือ = Σ(Cr − Dr) ของบรรทัด "ที่อยู่ในสมุด" (<see cref="ReportableJournal.LedgerStatuses"/> — Posted + Reversed นับคู่ ⇒
///   ใบมัดจำที่ถูกกลับรายการหักล้างเป็น 0) บนบัญชีเดียวกัน ของทุก JE ที่ผูกการจองนี้ (เลขอ้างอิง/คำอธิบาย/เอกสารต้นทาง + ตัวกลับรายการของมัน)</item>
/// <item>ตัดเกินยอดคงเหลือ (เกิน <see cref="Tolerance"/>) ⇒ ไม่ผ่าน · ตัดได้เท่าที่มี (มัดจำบางส่วน) ⇒ ผ่าน</item>
/// <item><b>ไม่พบร่องรอยการจองนี้ในระบบเลย</b> (ไม่มีทั้ง JE และเอกสาร) ⇒ <b>ไม่ตัดสิน</b> (ผ่าน) — มัดจำอาจบันทึกด้วยเลขอ้างอิงรูปแบบอื่น ·
///   ระบบไม่รู้ = ไม่เดาว่าผิด (หลักการ F2 ข้อ 3) · รายงานตรวจข้อมูลยังจับยอดติดลบภายหลังได้</item>
/// </list></para>
///
/// <para>ทิศกลับ (<see cref="EvaluateRemoval"/>): ยกเลิกใบมัดจำ/กลับรายการ JE มัดจำ ทั้งที่มี JE ตัดมัดจำนั้นผ่านรายการอยู่ ⇒ ยอดหลังยกเลิกติดลบ ⇒ บล็อก
/// พร้อมทางไปต่อ "กลับรายการ JE ที่ตัดก่อน" · การกลับ JE ที่<b>ตัด</b>มัดจำ (เพิ่มยอดคืน) ไม่ถูกบล็อกเสมอ</para>
///
/// <para>ตัวโหลดข้อเท็จจริงจากฐาน: <see cref="BookingDepositLedger"/> · จุดเรียกล็อกใน <c>tools/required_call_site_check.py</c></para>
/// </summary>
public static class BookingDepositDrawdown
{
    /// <summary>JE (จากระบบภายนอก) ตัดมัดจำเกินยอดคงเหลือของการจอง — ปฏิเสธ 422</summary>
    public const string RuleCode = "INT-DEPOSIT-OVERDRAW";
    /// <summary>ยกเลิก/กลับรายการมัดจำที่มี JE ตัดไปแล้ว — ปฏิเสธ 409</summary>
    public const string RemovalRuleCode = "DEP-DRAWN-REMOVE";
    /// <summary>JE จากระบบภายนอกที่มีขาภาษีขาย (21911) โดยไม่มีใบกำกับภาษีในระบบรองรับ — ติดธง (ไม่ปฏิเสธ)</summary>
    public const string OutputVatNoInvoiceCode = "INT-JE-OUTPUT-VAT-NO-INVOICE";
    /// <summary>ผังภาษีขาย ภ.พ.30</summary>
    public const string OutputVatAccountCode = "21911";
    /// <summary>ส่วนต่างที่ยอมให้ (เศษสตางค์)</summary>
    public const decimal Tolerance = 0.01m;
    /// <summary>ประเภทเหตุการณ์ในประวัติ sync ของ JE ที่ระบบภายนอกส่งผ่าน <c>POST /api/integration/journals</c> — ตัวตั้งเดียว
    /// (เส้นสร้าง JE เขียน · รายงานตรวจข้อมูล/ตัวกระทบภาษี-GL ใช้ไล่หา "JE จาก API")</summary>
    public const string JournalApiEventType = "journal.created";

    /// <summary>หมายเหตุ/คำเตือนของ JE จาก API ที่มีภาษีขายโดยไม่มีใบกำกับภาษีในระบบ (ม.86/4 · ม.87)</summary>
    public static string OutputVatNoInvoiceNote(decimal outputVat)
        => $"[{OutputVatNoInvoiceCode}] รายการนี้ลงภาษีขาย {outputVat:N2} บาท โดยไม่มีใบกำกับภาษีในระบบรองรับ — ภาษีขายเข้า ภ.พ.30 ผ่านตัวกวาดสมุดรายวัน "
            + "แต่รายงานภาษีขาย (ม.87) ไม่มีเลขที่ใบกำกับ/ผู้ซื้อ · ถ้าออกใบกำกับให้ลูกค้าแล้ว ให้ส่งเป็นใบกำกับผ่าน /api/integration/invoices แทน "
            + "· ถ้าไม่ควรมีภาษีขาย (เช่น ยอดมาจากมัดจำที่ยกเลิกแล้ว) ให้กลับรายการ";

    private static readonly Regex ResRef = new(@"(?<![A-Za-z0-9])RES-?(\d{3,})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BookingHash = new(@"(?:การจอง|จองที่|เลขจอง|booking|reservation)\s*(?:no\.?|เลขที่)?\s*#\s*(\d{3,})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>เลขการจองทุกตัวที่ข้อความอ้าง (ตัวเลขล้วน · ไม่ fuzzy) — "RES-149433-CHK" ⇒ 149433 · "การจอง #149433 (คุณปราณี)" ⇒ 149433 ·
    /// ข้อความที่ไม่อ้างการจอง ⇒ ว่าง · pure</summary>
    public static IReadOnlyList<string> ExtractKeys(params string?[] texts)
    {
        var keys = new List<string>();
        foreach (var t in texts)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            foreach (Match m in ResRef.Matches(t)) Add(m.Groups[1].Value);
            foreach (Match m in BookingHash.Matches(t)) Add(m.Groups[1].Value);
        }
        return keys;

        void Add(string digits)
        {
            var k = digits.TrimStart('0');
            if (k.Length > 0 && !keys.Contains(k)) keys.Add(k);
        }
    }

    /// <summary>ข้อความชุดนี้อ้างการจอง <paramref name="key"/> ไหม (ตรงตัว — 149433 ไม่ตรง 1494330)</summary>
    public static bool Mentions(string key, params string?[] texts) => ExtractKeys(texts).Contains(key);

    /// <summary>บัญชีนี้เป็น "เงินมัดจำ/รับล่วงหน้า" (หนี้สินที่ต้องมียอดเครดิตก่อนถูกตัด) ไหม — ดูความหมายจากชื่อ ไม่ใช่เลขนำหน้า
    /// (ผังมาตรฐาน 215xx = ค่าสาธารณูปโภคค้างจ่าย · ผังโรงแรม 21510 = มัดจำค่าห้องพัก — เลขชุดเดียวกันคนละความหมาย) · ต้องเป็นหมวดหนี้สิน</summary>
    public static bool IsDepositLiabilityAccount(AccountType type, string? code, string? name)
    {
        if (type != AccountType.Liability) return false;
        var n = (name ?? "").Trim();
        if (n.Length == 0) return false;
        return n.Contains("มัดจำ") || n.Contains("รับล่วงหน้า") || n.Contains("รอรับรู้")
            || (n.Contains("เงินประกัน") && !n.Contains("ประกันสังคม")) || n.Contains("ค้ำประกัน")
            || n.Contains("deposit", StringComparison.OrdinalIgnoreCase)
            || n.Contains("unearned", StringComparison.OrdinalIgnoreCase)
            || n.Contains("deferred revenue", StringComparison.OrdinalIgnoreCase)
            || n.Contains("advance received", StringComparison.OrdinalIgnoreCase)
            || n.Contains("received in advance", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ยอดคงเหลือต่อบัญชี = Σ(Cr − Dr) · pure</summary>
    public static IReadOnlyDictionary<Guid, decimal> Balances(IEnumerable<DepositLedgerLine> lines)
        => lines.GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.Sum(l => l.Credit - l.Debit));

    /// <summary>JE ใหม่ที่จะลง: บัญชีมัดจำที่ถูกตัดเกินยอดคงเหลือ (ว่าง = ผ่าน)</summary>
    /// <param name="incomingNetDebit">Dr − Cr สุทธิต่อบัญชีมัดจำของ JE ใหม่ (เฉพาะบัญชีมัดจำ)</param>
    /// <param name="ledger">บรรทัดที่อยู่ในสมุดของการจองนี้ (บัญชีมัดจำ)</param>
    /// <param name="bookingTraceFound">พบร่องรอยการจองนี้ในระบบ (JE หรือเอกสาร ใด ๆ — รวมใบที่ถูกยกเลิก) · false ⇒ ไม่ตัดสิน</param>
    public static IReadOnlyList<DepositShortfall> EvaluateDrawdown(
        IReadOnlyDictionary<Guid, decimal> incomingNetDebit, IEnumerable<DepositLedgerLine> ledger, bool bookingTraceFound)
    {
        var requested = incomingNetDebit.Where(kv => kv.Value > Tolerance).ToList();
        if (requested.Count == 0 || !bookingTraceFound) return Array.Empty<DepositShortfall>();
        var bal = Balances(ledger);
        return requested
            .Select(kv => new DepositShortfall(kv.Key, kv.Value, Math.Max(0m, bal.GetValueOrDefault(kv.Key))))
            .Where(s => s.Requested - s.Available > Tolerance)
            .ToList();
    }

    /// <summary>ยกเลิก/กลับรายการ JE ชุด <paramref name="removedEntryIds"/> (ใบมัดจำ): บัญชีที่ยอดคงเหลือหลังยกเลิกติดลบเพราะมี JE ตัดไปแล้ว (ว่าง = ยกเลิกได้) ·
    /// ยกเลิกที่ไม่ทำให้ยอดแย่ลง (เช่น กลับ JE ที่ตัดมัดจำ · มัดจำที่ยังไม่ถูกตัด) ⇒ ไม่บล็อกเสมอ · pure</summary>
    public static IReadOnlyList<DepositShortfall> EvaluateRemoval(IEnumerable<DepositLedgerLine> ledger, IReadOnlyCollection<Guid> removedEntryIds)
    {
        var all = ledger.ToList();
        var before = Balances(all);
        var removedNet = Balances(all.Where(l => removedEntryIds.Contains(l.JournalEntryId)));
        var result = new List<DepositShortfall>();
        foreach (var (acct, removedCr) in removedNet)
        {
            if (removedCr <= Tolerance) continue;                    // ส่วนที่ยกเลิกไม่ได้ให้เครดิตมัดจำ ⇒ ไม่ทำให้ยอดลดลง
            var after = before.GetValueOrDefault(acct) - removedCr;
            if (after >= -Tolerance) continue;                       // ยอดหลังยกเลิกไม่ติดลบ ⇒ ยกเลิกได้
            // Requested = ยอดที่บัญชีจะติดลบหลังยกเลิก (ส่วนที่ถูกตัดเกินมัดจำที่เหลือ) · Available = 0 (ไม่มีมัดจำอื่นพอรองรับ)
            result.Add(new DepositShortfall(acct, -after, 0m));
        }
        return result;
    }

    /// <summary>ข้อความปฏิเสธ JE ที่ตัดมัดจำเกิน (ฝั่งรับ JE) — ระบุการจอง · บัญชี · ยอดที่ขอ/คงเหลือ · ใบมัดจำที่ถูกยกเลิก · ทางไปต่อ</summary>
    public static string DrawdownMessage(string key, IReadOnlyList<DepositShortfall> shortfalls,
        Func<Guid, string> accountLabel, IReadOnlyCollection<string> voidedDepositDocs)
    {
        var parts = shortfalls.Select(s =>
            $"{accountLabel(s.AccountId)} ขอตัด {s.Requested:N2} แต่มัดจำคงเหลือของการจองนี้ {s.Available:N2}");
        var voided = voidedDepositDocs.Count > 0
            ? $" · เอกสารมัดจำของการจองนี้ถูกยกเลิกแล้ว: {string.Join(", ", voidedDepositDocs)}"
            : "";
        return $"ไม่รับรายการบัญชี — ตัดเงินมัดจำ/รับล่วงหน้าของการจอง #{key} เกินยอดที่มีอยู่จริงในบัญชี ({string.Join(" · ", parts)}){voided} · "
            + "ยังไม่มีอะไรถูกบันทึก · ทางไปต่อ: ถ้ามัดจำถูกยกเลิก/คืนแล้ว ให้ส่งรายการรับรู้รายได้โดยไม่ตัดบัญชีมัดจำ (ตั้งลูกหนี้/รับเงินตามจริง) "
            + "หรือบันทึกรับมัดจำให้ถูกก่อนแล้วส่งใหม่ "
            + $"[{RuleCode}]";
    }

    /// <summary>ข้อความบล็อกยกเลิก/กลับรายการมัดจำที่ถูกตัดไปแล้ว — ระบุ JE ที่ตัด + ทางไปต่อ</summary>
    public static string RemovalMessage(string key, string what, IReadOnlyList<DepositShortfall> shortfalls,
        Func<Guid, string> accountLabel, IReadOnlyCollection<string> drawingEntryNumbers)
    {
        var parts = shortfalls.Select(s => $"{accountLabel(s.AccountId)} จะติดลบ {s.Requested:N2}");
        var jes = drawingEntryNumbers.Count > 0 ? string.Join(", ", drawingEntryNumbers) : "(ดูบัญชีแยกประเภทของบัญชีมัดจำ)";
        return $"{what}ไม่ได้ — มัดจำของการจอง #{key} ถูกนำไปตัด/รับรู้รายได้แล้วโดยรายการบัญชี {jes} ({string.Join(" · ", parts)}) · "
            + "ยกเลิกตอนนี้จะทำให้บัญชีมัดจำติดลบ และรายได้/ภาษีขายที่รับรู้จากมัดจำค้างอยู่ทั้งที่ไม่มีเงิน · "
            + $"ทางไปต่อ: กลับรายการ {jes} ก่อน (หน้าสมุดรายวัน → กลับรายการ) แล้วค่อย{what} [{RemovalRuleCode}]";
    }
}
