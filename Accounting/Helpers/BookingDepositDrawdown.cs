using System.Text.RegularExpressions;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>บรรทัดสมุดรายวัน "ที่อยู่ในสมุด" ของบัญชีมัดจำ/รับล่วงหน้า ที่ผูกการจองหนึ่งรายการ (ข้อเท็จจริงจากฐาน — tenant แล้ว)</summary>
/// <param name="IsReversal">มาจากตัวกลับรายการ (<c>OriginalEntryId != null</c>) — เครดิตของตัวกลับไม่นับเป็น "มีมัดจำ"</param>
public sealed record DepositLedgerLine(Guid JournalEntryId, string EntryNumber, Guid AccountId, decimal Debit, decimal Credit,
    DateTime EntryDate = default, bool IsReversal = false);

/// <summary>บัญชีหนึ่งที่ยอดมัดจำไม่พอ — <paramref name="Requested"/> = ยอดที่จะตัด (ฝั่งยกเลิก: ยอดที่บัญชีจะติดลบ) ·
/// <paramref name="Available"/> = ยอดมัดจำคงเหลือที่มีจริง (Σ Cr − Dr ของการจองนี้บนบัญชีนั้น)</summary>
public sealed record DepositShortfall(Guid AccountId, decimal Requested, decimal Available);

/// <summary>บรรทัดของ JE ที่จะลง (ข้อมูลที่ตัวแยกการจองต้องใช้)</summary>
public sealed record IncomingJournalLine(Guid AccountId, decimal Debit, decimal Credit, string? Description);

/// <summary>JE ที่ตัดเกินมัดจำที่เหลือ ณ เวลาของมัน (รายงานตรวจข้อมูล) — <paramref name="AvailableBefore"/> = คงเหลือก่อนรายการนี้ตามลำดับเวลา</summary>
public sealed record OverdrawnEntry(Guid JournalEntryId, Guid AccountId, decimal Drawn, decimal AvailableBefore);

/// <summary>
/// **"JE ที่ตัดเงินมัดจำ/รับล่วงหน้าของการจอง มีมัดจำให้ตัดจริงไหม" — ตัวตัดสินตัวเดียว** (ทีม S 2026-10-26 · ผู้ใช้รายงาน)
///
/// <para><b>บั๊กที่มา:</b> การจอง #149433 — ใบรับมัดจำ RV-202609-0070 (Dr ธนาคาร 1,000 / Cr 21510) ถูกยกเลิกและกลับรายการแล้วด้วย RV-202609-0097
/// ต่อมาระบบภายนอกยิง <c>POST /api/integration/journals</c> เข้ามา: JV-INT-202609-0009 "รับรู้รายได้ Checkout" อ้างอิง RES-149433-CHK
/// Dr 21510 1,000 / Cr รายได้ 934.58 / Cr ภาษีขาย 65.42 ⇒ ระบบรับเข้า "ผ่านรายการ" ทันที ⇒ 21510 ติดลบ 1,000 · รับรู้รายได้และ<b>ภาษีขาย</b>ของเงินที่คืนไปแล้ว</para>
///
/// <para><b>กติกา:</b>
/// <list type="number">
/// <item>เลขการจอง = เลขเต็มหลัง <c>RES-</c> หรือ <c>#</c> (<see cref="ExtractKeys"/> — "RES-149433-CHK" ⇒ 149433 · "RES-101-2609-0001" ⇒ 101-2609-0001 ·
///   เทียบตรงตัว ไม่ใช่ substring)</item>
/// <item>แยกยอดที่ตัด<b>ต่อการจองตามบรรทัด</b> (<see cref="AssignDrawdown"/>): บรรทัดระบุการจองเดียว ⇒ ของการจองนั้น · บรรทัดไม่ระบุ ⇒ ใช้หัว JE เมื่อหัวอ้าง
///   การจองเดียว · กำกวม (หลายการจอง) ⇒ <b>ไม่ตัดสิน</b>บรรทัดนั้น — night audit ที่รวมหลายการจองไม่ถูกปฏิเสธทั้งใบ (ฝ่ายค้านข้อ 1)</item>
/// <item>ยอดคงเหลือ = Σ(Cr − Dr) ของบรรทัด "ที่อยู่ในสมุด" (<see cref="ReportableJournal.LedgerStatuses"/> — คู่กลับรายการหักล้าง) บนบัญชีเดียวกัน</item>
/// <item>ตัดสินเฉพาะเมื่อพบ<b>เครดิตมัดจำ</b>ที่ผูกการจองนั้นอย่างน้อย 1 ครั้ง (<see cref="HasDepositCredit"/> · ไม่นับเครดิตของตัวกลับรายการ) —
///   ไม่พบ ⇒ ไม่ตัดสิน (มัดจำอาจบันทึกด้วยเลขอ้างอิงแบบอื่น · ฝ่ายค้านข้อ 2) · ตัดเกินเศษสตางค์ ⇒ ไม่ผ่าน</item>
/// </list></para>
///
/// <para>ทิศกลับ (<see cref="EvaluateRemoval"/>): ยกเลิกใบมัดจำ/กลับรายการ JE มัดจำ ทั้งที่มี JE ตัดมัดจำนั้นผ่านรายการอยู่ ⇒ ยอดหลังยกเลิกติดลบ ⇒ บล็อก
/// พร้อมทางไปต่อ · การกลับ JE ที่<b>ตัด</b>มัดจำไม่ถูกบล็อกเสมอ · ตัวโหลด: <see cref="BookingDepositLedger"/> · จุดเรียกล็อกใน <c>tools/required_call_site_check.py</c></para>
/// </summary>
public static class BookingDepositDrawdown
{
    /// <summary>JE (จากระบบภายนอก) ตัดมัดจำเกินยอดคงเหลือของการจอง — ปฏิเสธ 422</summary>
    public const string RuleCode = "INT-DEPOSIT-OVERDRAW";
    /// <summary>ยกเลิก/กลับรายการมัดจำที่มี JE ตัดไปแล้ว — ปฏิเสธ 409</summary>
    public const string RemovalRuleCode = "DEP-DRAWN-REMOVE";
    /// <summary>JE จากระบบภายนอกที่มีขาภาษีขายโดยไม่มีใบกำกับภาษีในระบบรองรับ — ติดธง (ไม่ปฏิเสธ)</summary>
    public const string OutputVatNoInvoiceCode = "INT-JE-OUTPUT-VAT-NO-INVOICE";
    /// <summary>ผังภาษีขาย ภ.พ.30 มาตรฐาน (เส้นสร้าง JE ใช้ผังที่ <c>ResolveVatAccountAsync</c> หาได้ร่วมด้วย)</summary>
    public const string OutputVatAccountCode = "21911";
    /// <summary>ส่วนต่างที่ยอมให้ (เศษสตางค์)</summary>
    public const decimal Tolerance = 0.01m;
    /// <summary>ประเภทเหตุการณ์ในประวัติ sync ของ JE ที่ระบบภายนอกส่งผ่าน <c>POST /api/integration/journals</c> — ตัวตั้งเดียว</summary>
    public const string JournalApiEventType = "journal.created";

    private const string KeyPattern = @"(\d{3,}(?:-\d+)*)(?![\d])";
    private static readonly Regex ResRef = new(@"(?<![A-Za-z0-9])RES-?" + KeyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BookingHash = new(@"(?:การจอง|เลขจอง|booking|reservation)\s*(?:no\.?|เลขที่)?\s*#\s*" + KeyPattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>หมายเหตุ/คำเตือนของ JE จาก API ที่มีภาษีขายโดยไม่มีใบกำกับภาษีในระบบ (ม.86/4 · ม.87)</summary>
    public static string OutputVatNoInvoiceNote(decimal outputVat)
        => $"[{OutputVatNoInvoiceCode}] รายการนี้ลงภาษีขาย {outputVat:N2} บาท โดยไม่มีใบกำกับภาษีในระบบรองรับ — ภาษีขายเข้า ภ.พ.30 ผ่านตัวกวาดสมุดรายวัน "
            + "แต่รายงานภาษีขาย (ม.87) ไม่มีเลขที่ใบกำกับ/ผู้ซื้อ · ถ้าออกใบกำกับให้ลูกค้าแล้ว ให้ส่งเป็นใบกำกับผ่าน /api/integration/invoices แทน "
            + "· ถ้าไม่ควรมีภาษีขาย (เช่น ยอดมาจากมัดจำที่ยกเลิกแล้ว) ให้กลับรายการ";

    /// <summary>เลขการจองทุกตัวที่ข้อความอ้าง (เลขเต็ม · ไม่ fuzzy) — "RES-149433-CHK" ⇒ 149433 · "การจอง #149433 (คุณปราณี)" ⇒ 149433 ·
    /// "RES-101-2609-0001" ⇒ 101-2609-0001 (ไม่ใช่ 101) · ข้อความที่ไม่อ้างการจอง ⇒ ว่าง · pure</summary>
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

        void Add(string k)
        {
            if (k.Length > 0 && !keys.Contains(k)) keys.Add(k);
        }
    }

    /// <summary>ข้อความชุดนี้อ้างการจอง <paramref name="key"/> ไหม (ตรงตัว — 149433 ไม่ตรง 1494330 / 149433-2)</summary>
    public static bool Mentions(string key, params string?[] texts) => ExtractKeys(texts).Contains(key);

    /// <summary>เลขจองแบบเต็มที่เก็บใน <c>Document.BookingNumber</c> (มี index) — ตัวสะกดที่ระบบภายนอกใช้</summary>
    public static IReadOnlyList<string> BookingNumberForms(string key) => new[] { "RES-" + key, "RES" + key, key };

    /// <summary>บัญชีนี้เป็น "เงินมัดจำ/รับล่วงหน้า" (หนี้สินที่ต้องมียอดเครดิตก่อนถูกตัด) ไหม — หลัก: ผังที่ระบบตั้งไว้จริง
    /// (<paramref name="configuredCodes"/> = ประเภทเงินมัดจำ · ผังพักมัดจำของที่พัก) · เสริม: ความหมายจากชื่อ (ไม่ใช่เลขนำหน้า —
    /// ผังมาตรฐาน 215xx = ค่าสาธารณูปโภคค้างจ่าย · ผังโรงแรม 21510 = มัดจำค่าห้องพัก) · ต้องเป็นหมวดหนี้สิน</summary>
    public static bool IsDepositLiabilityAccount(AccountType type, string? code, string? name, IReadOnlyCollection<string>? configuredCodes = null)
    {
        if (type != AccountType.Liability) return false;
        if (configuredCodes != null && !string.IsNullOrWhiteSpace(code) && configuredCodes.Contains(code.Trim())) return true;
        var n = (name ?? "").Trim();
        if (n.Length == 0) return false;
        return n.Contains("มัดจำ") || n.Contains("รับล่วงหน้า") || n.Contains("รอรับรู้") || n.Contains("รับจอง")
            || (n.Contains("เงินประกัน") && !n.Contains("ประกันสังคม")) || n.Contains("ค้ำประกัน")
            || n.Contains("deposit", StringComparison.OrdinalIgnoreCase)
            || n.Contains("unearned", StringComparison.OrdinalIgnoreCase)
            || n.Contains("deferred revenue", StringComparison.OrdinalIgnoreCase)
            || n.Contains("advance received", StringComparison.OrdinalIgnoreCase)
            || n.Contains("received in advance", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>แยกยอดสุทธิ (Dr − Cr) บนบัญชีมัดจำของ JE ใหม่ <b>ต่อการจอง</b> ตามบรรทัด — บรรทัดระบุการจองเดียว ⇒ การจองนั้น ·
    /// ไม่ระบุ ⇒ หัว JE ถ้าหัวอ้างการจองเดียว · กำกวม ⇒ ไม่นับ (ไม่ตัดสิน) · คืนเฉพาะการจองที่มี Dr สุทธิ &gt; เศษสตางค์ · pure</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, decimal>> AssignDrawdown(
        IReadOnlyList<string?> headerTexts, IEnumerable<IncomingJournalLine> lines, Func<Guid, bool> isDepositAccount)
    {
        var headerKeys = ExtractKeys(headerTexts.ToArray());
        var acc = new Dictionary<string, Dictionary<Guid, decimal>>();
        foreach (var l in lines)
        {
            if (!isDepositAccount(l.AccountId)) continue;
            var lineKeys = ExtractKeys(l.Description);
            string? key = lineKeys.Count == 1 ? lineKeys[0]
                : lineKeys.Count == 0 && headerKeys.Count == 1 ? headerKeys[0]
                : null;
            if (key == null) continue;
            if (!acc.TryGetValue(key, out var perAcct)) acc[key] = perAcct = new Dictionary<Guid, decimal>();
            perAcct[l.AccountId] = perAcct.GetValueOrDefault(l.AccountId) + l.Debit - l.Credit;
        }
        return acc.Where(kv => kv.Value.Values.Any(v => v > Tolerance))
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<Guid, decimal>)kv.Value);
    }

    /// <summary>พบเครดิตมัดจำที่ผูกการจองนี้อย่างน้อย 1 ครั้ง (ไม่นับตัวกลับรายการ) — เงื่อนไขก่อนตัดสินทุกครั้ง</summary>
    public static bool HasDepositCredit(IEnumerable<DepositLedgerLine> ledger)
        => ledger.Any(l => l.Credit > Tolerance && !l.IsReversal);

    /// <summary>ยอดคงเหลือต่อบัญชี = Σ(Cr − Dr) · pure</summary>
    public static IReadOnlyDictionary<Guid, decimal> Balances(IEnumerable<DepositLedgerLine> lines)
        => lines.GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.Sum(l => l.Credit - l.Debit));

    /// <summary>JE ใหม่ที่จะลง: บัญชีมัดจำที่ถูกตัดเกินยอดคงเหลือของการจองหนึ่ง (ว่าง = ผ่าน/ไม่ตัดสิน)</summary>
    /// <param name="incomingNetDebit">Dr − Cr สุทธิต่อบัญชีมัดจำของ JE ใหม่ ที่เป็นของการจองนี้ (<see cref="AssignDrawdown"/>)</param>
    /// <param name="ledger">บรรทัดที่อยู่ในสมุดของการจองนี้ (บัญชีมัดจำ)</param>
    public static IReadOnlyList<DepositShortfall> EvaluateDrawdown(IReadOnlyDictionary<Guid, decimal> incomingNetDebit, IEnumerable<DepositLedgerLine> ledger)
    {
        var all = ledger.ToList();
        var requested = incomingNetDebit.Where(kv => kv.Value > Tolerance).ToList();
        if (requested.Count == 0 || !HasDepositCredit(all)) return Array.Empty<DepositShortfall>();
        var bal = Balances(all);
        return requested
            .Select(kv => new DepositShortfall(kv.Key, kv.Value, Math.Max(0m, bal.GetValueOrDefault(kv.Key))))
            .Where(s => s.Requested - s.Available > Tolerance)
            .ToList();
    }

    /// <summary>ยกเลิก/กลับรายการ JE ชุด <paramref name="removedEntryIds"/> (ใบมัดจำ): บัญชีที่ยอดหลังยกเลิกติดลบเพราะมี JE ตัดไปแล้ว (ว่าง = ยกเลิกได้) ·
    /// ยกเลิกที่ไม่ลดยอด (กลับ JE ที่ตัด · ส่วนที่ไม่มีเครดิตมัดจำ) ⇒ ไม่บล็อกเสมอ · pure</summary>
    public static IReadOnlyList<DepositShortfall> EvaluateRemoval(IEnumerable<DepositLedgerLine> ledger, IReadOnlyCollection<Guid> removedEntryIds)
    {
        var all = ledger.ToList();
        var before = Balances(all);
        var removedNet = Balances(all.Where(l => removedEntryIds.Contains(l.JournalEntryId)));
        var result = new List<DepositShortfall>();
        foreach (var (acct, removedCr) in removedNet)
        {
            if (removedCr <= Tolerance) continue;
            var after = before.GetValueOrDefault(acct) - removedCr;
            if (after >= -Tolerance) continue;
            result.Add(new DepositShortfall(acct, -after, 0m));
        }
        return result;
    }

    /// <summary>รายงานตรวจข้อมูล: เดินบรรทัดตามลำดับเวลา (วันที่ → เครดิตก่อนเดบิตในวันเดียวกัน → เลข JE) แล้วคืน JE ใน <paramref name="candidates"/> ที่ตัด
    /// เกินคงเหลือ ณ ตอนนั้น — มัดจำ 1,000 ตัด 600 + 600 ⇒ ติดธงเฉพาะใบที่สอง (คงเหลือก่อน 400) · ไม่มีเครดิตมัดจำเลย ⇒ ว่าง · pure</summary>
    public static IReadOnlyList<OverdrawnEntry> OverdrawnInOrder(IEnumerable<DepositLedgerLine> ledger, IReadOnlyCollection<Guid> candidates)
    {
        var all = ledger.ToList();
        if (!HasDepositCredit(all)) return Array.Empty<OverdrawnEntry>();
        var result = new List<OverdrawnEntry>();
        foreach (var acct in all.GroupBy(l => l.AccountId))
        {
            var perEntry = acct.GroupBy(l => l.JournalEntryId)
                .Select(g => (Id: g.Key, Date: g.Min(l => l.EntryDate), No: g.First().EntryNumber, Net: g.Sum(l => l.Credit - l.Debit)))
                .OrderBy(e => e.Date).ThenBy(e => e.Net >= 0 ? 0 : 1).ThenBy(e => e.No, StringComparer.Ordinal)
                .ToList();
            decimal running = 0m;
            foreach (var e in perEntry)
            {
                if (e.Net < -Tolerance && candidates.Contains(e.Id) && -e.Net - Math.Max(0m, running) > Tolerance)
                    result.Add(new OverdrawnEntry(e.Id, acct.Key, -e.Net, Math.Max(0m, running)));
                running += e.Net;
            }
        }
        return result;
    }

    /// <summary>ข้อความปฏิเสธ JE ที่ตัดมัดจำเกิน (ฝั่งรับ JE)</summary>
    public static string DrawdownMessage(string key, IReadOnlyList<DepositShortfall> shortfalls,
        Func<Guid, string> accountLabel, IReadOnlyCollection<string> voidedDepositDocs)
    {
        var parts = shortfalls.Select(s =>
            $"{accountLabel(s.AccountId)} ขอตัด {s.Requested:N2} แต่มัดจำคงเหลือของการจองนี้ {s.Available:N2}");
        var voided = voidedDepositDocs.Count > 0
            ? $" · เอกสารของการจองนี้ที่ถูกยกเลิกแล้ว: {string.Join(", ", voidedDepositDocs)}"
            : "";
        return $"ไม่รับรายการบัญชี — ตัดเงินมัดจำ/รับล่วงหน้าของการจอง #{key} เกินยอดที่มีอยู่จริงในบัญชี ({string.Join(" · ", parts)}){voided} · "
            + "ยังไม่มีอะไรถูกบันทึก · ทางไปต่อ: ถ้ามัดจำถูกยกเลิก/คืนแล้ว ให้ส่งรายการรับรู้รายได้โดยไม่ตัดบัญชีมัดจำ (ตั้งลูกหนี้/รับเงินตามจริง) "
            + "หรือบันทึกรับมัดจำให้ถูกก่อนแล้วส่งใหม่ "
            + $"[{RuleCode}]";
    }

    /// <summary>ข้อความบล็อกยกเลิก/กลับรายการมัดจำที่ถูกตัดไปแล้ว — JE สมุดรายวันที่ตัด ⇒ "กลับรายการ" · JE ที่มาจากเอกสาร ⇒ "ยกเลิกเอกสาร"
    /// (หน้าสมุดรายวันกลับ JE ของเอกสารไม่ได้ — ฝ่ายค้านข้อ 7)</summary>
    public static string RemovalMessage(string key, string what, IReadOnlyList<DepositShortfall> shortfalls,
        Func<Guid, string> accountLabel, IReadOnlyCollection<string> journalsToReverse, IReadOnlyCollection<string> documentsToVoid)
    {
        var parts = shortfalls.Select(s => $"{accountLabel(s.AccountId)} จะติดลบ {s.Requested:N2}");
        var by = journalsToReverse.Concat(documentsToVoid).ToList();
        var steps = new List<string>();
        if (journalsToReverse.Count > 0) steps.Add($"กลับรายการ {string.Join(", ", journalsToReverse)} (หน้าสมุดรายวัน → กลับรายการ)");
        if (documentsToVoid.Count > 0) steps.Add($"ยกเลิกเอกสาร {string.Join(", ", documentsToVoid)}");
        if (steps.Count == 0) steps.Add("ตรวจบัญชีแยกประเภทของบัญชีมัดจำแล้วกลับรายการที่ตัดมัดจำนี้");
        return $"{what}ไม่ได้ — มัดจำของการจอง #{key} ถูกนำไปตัด/รับรู้รายได้แล้ว"
            + (by.Count > 0 ? $"โดย {string.Join(", ", by)}" : "")
            + $" ({string.Join(" · ", parts)}) · ยกเลิกตอนนี้จะทำให้บัญชีมัดจำติดลบ และรายได้/ภาษีขายที่รับรู้จากมัดจำค้างอยู่ทั้งที่ไม่มีเงิน · "
            + $"ทางไปต่อ: {string.Join(" · ", steps)} ก่อน แล้วค่อย{what} [{RemovalRuleCode}]";
    }
}
