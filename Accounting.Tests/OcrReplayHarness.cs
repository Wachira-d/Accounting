using System.Text;
using Accounting.Helpers;

namespace Accounting.Tests;

/// <summary>กระดาษหนึ่งใบในชุด replay — ข้อความ + สามยอดที่ engine ส่งมา</summary>
/// <param name="Name">ชื่อที่คนอ่านแล้วรู้ว่าใบไหน (ใช้เป็นคีย์ของตารางผลต่าง)</param>
public sealed record ReplayPaper(
    string Name, string RawText,
    decimal? EngineSubTotal = null, decimal? EngineVat = null, decimal? EngineTotal = null,
    string? VendorNameFromEngine = null, decimal? BaseAmount = null);

/// <summary>คำตอบหนึ่งช่องของใบหนึ่ง</summary>
public sealed record ReplayAnswer(string Paper, string Field, string? Value);

/// <summary>
/// **Replay harness — "กระดาษชุดเดิม ก่อน/หลัง คำตอบใบไหนเปลี่ยน"** (D-7)
///
/// ═══ ทำไมมีแค่ชั้น helper ไม่ใช่ทั้งไปป์ไลน์ ═══
/// <para><b>ฝ่ายเสนอ (ทำ seam ให้ <c>ProcessScanAsync</c> รันแบบ headless)</b>: ได้ความจริง
/// ปลายทางทั้งเส้น · <b>ฝ่ายค้าน</b>: ต้องมี <c>DbContext</c> · engine · HTTP · Azure
/// ⇒ ต้องรื้อคอนสตรักเตอร์ 2,000 บรรทัดที่เรากำลังพยายามป้องกัน — <b>เครื่องมือกันถดถอย
/// ที่ตัวมันเองทำให้เกิดการถดถอย</b> · <b>คำตัดสิน (G5)</b>: เริ่มที่ชั้น pure ก่อน
/// เพราะการถดถอยที่พิสูจน์ได้ทั้งหมดใน <c>OCR_PIPELINE_REVIEW §2f</c> (RG-01..04)
/// เกิดใน<b>ตัวตัดสินที่ pure อยู่แล้ว</b> (<c>OcrHeaderAmounts</c> · <c>OcrPartyName</c> ·
/// <c>OcrDepositMarker</c> · <c>PaperWhtReader</c>) ⇒ ได้ประโยชน์ ~80% ด้วยความเสี่ยง ~0
/// · seam ของไปป์ไลน์เต็มเป็น backlog ที่ต้องทำ<b>แยกคอมมิต</b></para>
///
/// ═══ วิธีใช้ (ก่อนแตะตัวตัดสินตัวใด) ═══
/// <list type="number">
/// <item><c>var before = OcrReplayHarness.Run();</c> แล้วเก็บไว้ (หรืออ่านจาก
///   <c>OcrReplayGoldenTests</c> ซึ่งล็อกค่าไว้เป็นตัวอักษร)</item>
/// <item>แก้โค้ด</item>
/// <item><c>OcrReplayHarness.DiffTable(before, OcrReplayHarness.Run())</c> —
///   <b>ทุกแถวที่โผล่มาต้องอธิบายได้</b> ใบที่เปลี่ยนโดยอธิบายไม่ได้ = มีบั๊กอีกตัว
///   ที่ heuristic เดิมเคยกลบไว้ (กฎเหล็ก #4 H)</item>
/// </list>
/// </summary>
public static class OcrReplayHarness
{
    /// <summary>ชุดกระดาษจริงที่เคยทำให้เกิดการถดถอย — <b>ห้ามลบแถว</b> เพิ่มได้อย่างเดียว
    /// (ตัวเลข/ข้อความยกมาจากเคสที่บันทึกไว้ในเทสต์/คอมเมนต์ของเรพ)</summary>
    public static IReadOnlyList<ReplayPaper> Corpus { get; } = new[]
    {
        // RG-01 — ใบที่ engine ติดป้าย SubTotal/Total สลับ (ลักกี้เวย์)
        new ReplayPaper("luckyway-swapped",
            "บริษัท ลักกี้เวย์ จำกัด\nรวมเป็นเงิน 1,070.00\nภาษีมูลค่าเพิ่ม 70.00\nจำนวนเงินรวมทั้งสิ้น 1,000.00",
            EngineSubTotal: 1070m, EngineVat: 70m, EngineTotal: 1000m),

        // RG-02 — Makro: สามยอดถูกต้องอยู่แล้ว ห้ามแตะ
        new ReplayPaper("makro-correct",
            "บริษัท สยามแม็คโคร จำกัด (มหาชน)\nรวมเงิน 951.00\nภาษีมูลค่าเพิ่ม 7% 49.00\nรวมทั้งสิ้น 1,000.00",
            EngineSubTotal: 951m, EngineVat: 49m, EngineTotal: 1000m),

        // RG-03 — แถวฟอร์ม "หักเงินมัดจำ 0.00" ต้องไม่ทำให้ใบซื้อธรรมดาติดธงมัดจำ
        new ReplayPaper("deposit-form-row-zero",
            "ใบเสร็จรับเงิน\nค่าสินค้า 1,000.00\nหักเงินมัดจำ 0.00\nรวมสุทธิ 1,000.00",
            EngineSubTotal: 1000m, EngineVat: 0m, EngineTotal: 1000m),

        // ใบมัดจำจริง — ต้องยังติดธง
        new ReplayPaper("deposit-real",
            "ใบรับเงินมัดจำ\nเงินมัดจำค่าก่อสร้าง 50,000.00\nรวม 50,000.00",
            EngineSubTotal: 50000m, EngineVat: 0m, EngineTotal: 50000m),

        // ใบบริการที่ผู้ขายพิมพ์ส่วนหัก ณ ที่จ่ายมาเอง — กระดาษชนะทุกชั้น
        new ReplayPaper("service-wht-printed",
            "ใบแจ้งหนี้ค่าบริการ\nค่าบริการ 10,000.00\nภาษีมูลค่าเพิ่ม 7% 700.00\n"
            + "รวม 10,700.00\nหัก ณ ที่จ่าย 3% 300.00\nยอดชำระสุทธิ 10,400.00",
            EngineSubTotal: 10000m, EngineVat: 700m, EngineTotal: 10700m, BaseAmount: 10000m),

        // ใบบริการที่ไม่พิมพ์ส่วนหัก (ปกติของผู้ขายไทย) — ต้องไม่มียอดหักจากกระดาษ
        new ReplayPaper("service-wht-absent",
            "ใบแจ้งหนี้ค่าบริการ\nค่าที่ปรึกษา 20,000.00\nภาษีมูลค่าเพิ่ม 7% 1,400.00\nรวม 21,400.00",
            EngineSubTotal: 20000m, EngineVat: 1400m, EngineTotal: 21400m, BaseAmount: 20000m),

        // ใบส่งออก 0% — VAT = 0 โดยชอบ ห้ามถูกตีเป็นป้ายสลับ
        new ReplayPaper("export-zero-rated",
            "INVOICE (EXPORT)\nGoods 100,000.00\nVAT 0% 0.00\nTotal 100,000.00",
            EngineSubTotal: 100000m, EngineVat: 0m, EngineTotal: 100000m),

        // RG-04 — ชื่อผู้ซื้อถูกตัด: ตัวขยายต้องคืนชื่อเต็มจากบรรทัดกระดาษ
        new ReplayPaper("buyer-name-truncated",
            "ผู้ซื้อ\nหจก. แอม แฮปปี้เนส\nเลขประจำตัวผู้เสียภาษี 0105556000000",
            VendorNameFromEngine: "แอม แฮปปี้"),
    };

    /// <summary>รันกระดาษทุกใบผ่านตัวตัดสิน pure ทุกตัว — คืน "คำตอบต่อช่อง" ที่เทียบกันได้
    ///
    /// <para><b>ไม่มี I/O ไม่มี DB ไม่มีเครือข่าย</b> ⇒ รันในเทสต์ได้เสมอ และผลต้อง
    /// deterministic (เรียงตามใบ แล้วตามชื่อช่อง)</para></summary>
    public static IReadOnlyList<ReplayAnswer> Run(IEnumerable<ReplayPaper>? corpus = null)
    {
        var result = new List<ReplayAnswer>();
        foreach (var p in corpus ?? Corpus)
        {
            // 1. สามยอดหัวใบ (OcrHeaderAmounts)
            var n = OcrHeaderAmounts.Normalize(p.EngineSubTotal, p.EngineVat, p.EngineTotal);
            result.Add(new(p.Name, "HeaderSubTotal", n.SubTotal?.ToString("0.00")));
            result.Add(new(p.Name, "HeaderTotal", n.Total?.ToString("0.00")));
            result.Add(new(p.Name, "HeaderSwapped", n.Swapped ? "true" : "false"));

            // 2. ธงมัดจำ (OcrDepositMarker)
            var dep = OcrDepositMarker.Decide(p.RawText, null);
            result.Add(new(p.Name, "IsDeposit", dep.IsDeposit ? "true" : "false"));

            // 3. ส่วนหัก ณ ที่จ่ายที่พิมพ์บนกระดาษ (PaperWhtReader)
            var wht = PaperWhtReader.Read(p.RawText, p.BaseAmount);
            result.Add(new(p.Name, "PaperWhtAmount", wht.Amount?.ToString("0.00")));
            result.Add(new(p.Name, "PaperWhtRate", wht.RatePercent?.ToString("0.##")));

            // 4. ชื่อที่ถูกตัด (OcrPartyName) — ขยายจากบรรทัดบนกระดาษเท่านั้น
            var lines = p.RawText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            result.Add(new(p.Name, "ExpandedName",
                OcrPartyName.ExpandTruncated(p.VendorNameFromEngine, lines)));
        }
        return result;
    }

    /// <summary>ตารางผลต่าง "ก่อน → หลัง" — <b>ว่าง = ไม่มีใบไหนเปลี่ยนคำตอบ</b></summary>
    public static IReadOnlyList<(string Paper, string Field, string? Before, string? After)> Diff(
        IReadOnlyList<ReplayAnswer> before, IReadOnlyList<ReplayAnswer> after)
    {
        var b = before.ToDictionary(x => (x.Paper, x.Field), x => x.Value);
        var a = after.ToDictionary(x => (x.Paper, x.Field), x => x.Value);
        var keys = b.Keys.Union(a.Keys).OrderBy(k => k.Paper, StringComparer.Ordinal)
            .ThenBy(k => k.Field, StringComparer.Ordinal);
        var rows = new List<(string, string, string?, string?)>();
        foreach (var k in keys)
        {
            b.TryGetValue(k, out var bv);
            a.TryGetValue(k, out var av);
            if (!string.Equals(bv, av, StringComparison.Ordinal))
                rows.Add((k.Paper, k.Field, bv, av));
        }
        return rows;
    }

    /// <summary>ตารางที่คนอ่านแล้วรู้ทันทีว่า "ใบไหนคำตอบเปลี่ยน" — เอาไปแปะในคอมมิตได้</summary>
    public static string DiffTable(
        IReadOnlyList<ReplayAnswer> before, IReadOnlyList<ReplayAnswer> after)
    {
        var rows = Diff(before, after);
        if (rows.Count == 0) return "ไม่มีใบไหนคำตอบเปลี่ยน (0 แถว)";
        var sb = new StringBuilder();
        sb.AppendLine("| ใบ | ช่อง | เดิม | ตอนนี้ |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var (paper, field, bv, av) in rows)
            sb.AppendLine($"| {paper} | {field} | {bv ?? "(ว่าง)"} | {av ?? "(ว่าง)"} |");
        return sb.ToString();
    }

    /// <summary>รูปข้อความบรรทัดเดียวต่อคำตอบ — ใช้ล็อกเป็น golden ในเทสต์
    /// (อ่านออกด้วยตา ⇒ diff ของ git บอกได้เองว่าใบไหนเปลี่ยน)</summary>
    public static string Snapshot(IReadOnlyList<ReplayAnswer> answers)
    {
        var sb = new StringBuilder();
        foreach (var a in answers.OrderBy(x => x.Paper, StringComparer.Ordinal)
                     .ThenBy(x => x.Field, StringComparer.Ordinal))
            sb.Append(a.Paper).Append(" · ").Append(a.Field).Append(" = ")
              .Append(a.Value ?? "(ว่าง)").Append('\n');
        return sb.ToString();
    }
}
