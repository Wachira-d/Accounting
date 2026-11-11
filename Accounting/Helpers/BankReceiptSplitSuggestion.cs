using System.Globalization;

namespace Accounting.Helpers;

/// <summary>
/// **ข้อเสนอ "เงินโอนก้อนเดียวปิดหลายใบ / ใบเดียว + ส่วนเกิน" ของบรรทัดเงินเข้าที่ยังไม่จับคู่ — ตัวเดียว** (รอบ 259 แพ็ก P5 ·
/// erp-review/2026-11-11/team-D-lump-payment.md §3.6 · DECISIONS 258-1 / 259-1-x)
/// <para><b>เสนอเท่านั้น — ไม่มีวันลงบัญชีเอง</b> (Autopilot ยังเป็น suggest-only · ปิดหนี้อัตโนมัติยังมีแค่ "ยอดตรงใบเดียว" ของ
/// <see cref="BankReceiptAutoSettle.Decide"/> ตามเดิม) · ผู้ใช้กด "บันทึกเป็นรับเงินลูกค้า" ⇒ เปิดหน้ารับชำระที่เติมยอด/วันที่ไว้แล้ว</para>
/// <list type="bullet">
/// <item><b>Split</b>: ลูกค้า<b>รายเดียว</b>ที่มีตัวระบุตัว (อ้างเลขเอกสาร/ชื่อผู้โอนตรง) และใบค้าง 2–<see cref="MaxDocs"/> ใบรวมกันเท่ายอดเงินเข้าพอดี ·
/// ต้องเป็น<b>ชุดเดียว</b>ในทุกลูกค้าที่มีตัวระบุตัว (หลายชุด = กำกวม ไม่เสนอชุดไหน) · ค้นแบบมีขอบเขต (<see cref="SubsetSum"/>)</item>
/// <item><b>InvoicePlusExcess</b>: รายการอ้างเลขใบค้างใบเดียวที่ยอดน้อยกว่าเงินเข้า ⇒ เสนอ "รับเต็ม ปิดใบนั้น ส่วนเกินเก็บเป็นเงินรอจัดสรร" (ให้คนเลือกเหตุผล)</item>
/// </list>
/// <para>ไม่มีตัวระบุตัว = ไม่เสนอ (ยอดบังเอิญตรงไม่ใช่หลักฐานว่าเป็นเงินของใบชุดนั้น — DOCTRINE §1)</para>
/// </summary>
public static class BankReceiptSplitSuggestion
{
    public const string RuleCode = "BANK-SPLIT-SUGGEST";
    /// <summary>ใบสูงสุดต่อชุด</summary>
    public const int MaxDocs = 6;
    /// <summary>ใบค้างต่อลูกค้าที่นำมาค้น (เก่าสุดก่อน) — กันค้นกว้างเกิน</summary>
    public const int MaxDocsPerContact = 20;
    public const decimal Tolerance = 0.005m;

    public enum Kind { Split, InvoicePlusExcess }

    /// <param name="Expected">ยอดที่ลูกค้าต้องโอนของใบ (สุทธิหลัง WHT — สูตรลิงก์ชำระ)</param>
    /// <param name="Cited">รายการเงินเข้าอ้างเลขเอกสารนี้ (<see cref="BankReceiptAutoSettle.CitesDocumentNumber"/>)</param>
    public sealed record Doc(Guid DocumentId, string DocumentNumber, DateTime DocumentDate, decimal Expected, bool Cited);

    /// <param name="NameConfident">ชื่อผู้โอนตรงผู้ติดต่อแบบมั่นใจ (<c>BankMatchScorer</c>)</param>
    public sealed record ContactDocs(Guid ContactId, string? ContactName, bool NameConfident, IReadOnlyList<Doc> Docs)
    {
        public bool HasIdentity => NameConfident || Docs.Any(d => d.Cited);
    }

    public sealed record Allocation(Guid DocumentId, string DocumentNumber, decimal Amount);

    public sealed record Suggestion(Kind Kind, Guid ContactId, string? ContactName, IReadOnlyList<Allocation> Allocations, decimal Excess, string Reason);

    /// <param name="Note">ข้อความเมื่อไม่มีข้อเสนอ/กำกวม (null = มีข้อเสนอ)</param>
    public sealed record Result(IReadOnlyList<Suggestion> Suggestions, string? Note);

    private static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static Result Suggest(decimal lineAmount, IReadOnlyList<ContactDocs> contacts)
    {
        if (lineAmount <= 0m) return new Result(Array.Empty<Suggestion>(), "ไม่ใช่เงินเข้า");
        var identified = (contacts ?? Array.Empty<ContactDocs>()).Where(c => c.HasIdentity && c.Docs.Count > 0)
            .OrderBy(c => c.ContactId).ToList();
        if (identified.Count == 0)
            return new Result(Array.Empty<Suggestion>(),
                "ไม่มีลูกค้าที่ระบุตัวได้จากรายการ (เลขเอกสาร/ชื่อผู้โอน) — เลือกลูกค้าเองแล้วบันทึกรับเงิน");

        var list = new List<Suggestion>();
        string? note = null;

        // (1) Split — ชุดเดียวในทุกลูกค้าที่มีตัวระบุตัว
        var splits = new List<(ContactDocs Contact, List<Doc> Docs)>();
        var exhausted = false;
        foreach (var c in identified)
        {
            var docs = c.Docs.Where(d => d.Expected > 0m)
                .OrderBy(d => d.DocumentDate).ThenBy(d => d.DocumentNumber, StringComparer.Ordinal)
                .Take(MaxDocsPerContact).ToList();
            var r = SubsetSum.Find(docs.Select(d => d.Expected).ToList(), lineAmount, Tolerance, minCount: 2, maxCount: MaxDocs, maxSolutions: 2);
            exhausted |= r.Exhausted;
            foreach (var s in r.Solutions) splits.Add((c, s.Select(i => docs[i]).ToList()));
            if (splits.Count > 1) break;
        }
        if (splits.Count == 1 && !exhausted)
        {
            var (c, docs) = splits[0];
            list.Add(new Suggestion(Kind.Split, c.ContactId, c.ContactName,
                docs.Select(d => new Allocation(d.DocumentId, d.DocumentNumber, R(d.Expected))).ToList(), 0m,
                $"ยอด {Money(lineAmount)} เท่ากับใบค้าง {docs.Count} ใบของ {c.ContactName ?? "ลูกค้ารายนี้"} รวมกันพอดี ("
                + string.Join(" + ", docs.Select(d => d.DocumentNumber)) + ") — ตรวจแล้วบันทึกรับชำระทีละใบ (ลิงก์เปิดใบแรกด้วยยอดของใบนั้น · ใบถัดไปเปิดจากรายการ)"));
        }
        else if (splits.Count > 1 || exhausted)
            note = "มีหลายชุดใบค้างที่รวมกันได้ยอดนี้ — ระบบไม่เลือกชุดให้ เลือกใบเองในหน้ารับชำระ";

        // (2) ใบที่อ้างเลขใบเดียว + ส่วนเกิน
        var cited = identified.SelectMany(c => c.Docs.Where(d => d.Cited).Select(d => (Contact: c, Doc: d))).ToList();
        if (cited.Count == 1 && cited[0].Doc.Expected > 0m && cited[0].Doc.Expected < lineAmount - Tolerance)
        {
            var (c, d) = cited[0];
            var excess = R(lineAmount - d.Expected);
            list.Add(new Suggestion(Kind.InvoicePlusExcess, c.ContactId, c.ContactName,
                new[] { new Allocation(d.DocumentId, d.DocumentNumber, R(d.Expected)) }, excess,
                $"รายการอ้าง {d.DocumentNumber} (ต้องโอน {Money(d.Expected)}) แต่เงินเข้า {Money(lineAmount)} — รับเต็มแล้วปิดใบนี้ "
                + $"ส่วนเกิน {Money(excess)} เก็บเป็นเงินรอจัดสรร (เลือกเหตุผลในหน้ารับชำระ)"));
        }
        if (list.Count == 0 && note == null)
            note = "ไม่พบชุดใบค้างที่รวมกันได้ยอดนี้ และรายการไม่อ้างเลขใบที่ยอดน้อยกว่าเงินเข้า — เลือกใบเองในหน้ารับชำระ";
        return new Result(list, note);
    }
}
