using System.Text;
using Accounting.Helpers;

namespace Accounting.Tests;

/// <summary>กระดาษหนึ่งใบในชุด replay — ข้อความ + สามยอดที่ engine ส่งมา</summary>
/// <param name="Name">ชื่อที่คนอ่านแล้วรู้ว่าใบไหน (ใช้เป็นคีย์ของตารางผลต่าง)</param>
public sealed record ReplayPaper(
    string Name, string RawText,
    decimal? EngineSubTotal = null, decimal? EngineVat = null, decimal? EngineTotal = null,
    string? VendorNameFromEngine = null, decimal? BaseAmount = null,
    decimal[]? LineAmounts = null,
    IReadOnlyList<ReplayLine>? Lines = null,
    string? VendorTaxId = null,
    IReadOnlyList<ReplayLine>? EngineLines = null);

/// <summary>บรรทัดรายการ<b>ตามที่กระดาษพิมพ์</b> (คำอธิบาย · จำนวน · ราคาต่อหน่วย · ยอด) — ป้อนขั้นสร้างบรรทัดเอกสาร
/// (2026-10-09 ratchet ระดับบรรทัด) · ใบที่ให้แต่ <see cref="ReplayPaper.LineAmounts"/> จะถูกแปลงเป็นบรรทัดที่ไม่มีจำนวน/ราคา
/// (เหมือน engine ที่คืนแค่ยอด) · <see cref="ReplayPaper.EngineLines"/> = แถว<b>ตามที่ engine คืน</b> เมื่อต่างจากที่พิมพ์ (ต้นฉบับ+สำเนา ⇒
/// ชุดเดิมสองรอบ) — ด่านแถวซ้ำ (<see cref="OcrDuplicateLineGuard"/>) ต้องพาแถวกลับมาเท่า <c>Lines</c></summary>
/// <param name="LineDiscount">ส่วนลดของบรรทัด (ฐานเดียวกับราคา) — <b>ไม่ใช่ข้อมูลป้อน</b>: engine ภาพไม่คืนช่องนี้ ⇒ ใบในชุดปล่อย null ·
/// <see cref="OcrReplayHarness.BuildLines"/> เติมจากแถวบนกระดาษด้วยตัวตัดสินตัวเดียวกับ service (<see cref="OcrLineQuantityGuard"/>)</param>
/// <param name="PriceIncludesVat">ธงรวม VAT ของบรรทัด (ผลของ <see cref="OcrLineQuantityGuard.PaperDiscountPricesIncludeVat"/>) — ไม่ใช่ข้อมูลป้อน</param>
/// <param name="Account">ผังบัญชีรายบรรทัด (รหัสผังที่เรียนไว้/ผู้ใช้เลือก) — null = ผังหัวใบ · ป้อนตัวตัดสินตารางรหัส ภ.พ. (ผัง/โครงการของบรรทัดสรุป ·
/// หมายเหตุรายการเดิม — 2026-10-16 คำตัดสินเจ้าของภาคต่อ)</param>
/// <param name="Project">ชื่อโครงการรายบรรทัด — null = ไม่มี</param>
public sealed record ReplayLine(string? Description, decimal? Quantity, decimal? UnitPrice, decimal Amount,
    decimal? LineDiscount = null, bool PriceIncludesVat = false, string? Account = null, string? Project = null);

/// <summary>ผลของขั้นสร้างบรรทัดเอกสาร (<see cref="OcrReplayHarness.BuildLines"/>) — ทุกช่องเป็นข้อความอ่านออก เพื่อล็อกเป็น golden</summary>
/// <param name="Mode"><c>Items</c> (มีรายการ) · <c>VatGroups</c> (ไม่มีรายการแต่กระดาษพิมพ์ตารางสรุปตามกลุ่มภาษี) · <c>Summary</c> (บรรทัดสรุปใบเดียว)</param>
/// <param name="ReconCase">เคสของ <see cref="OcrLineReconciler"/> (เฉพาะ Items · อื่น ๆ = null)</param>
/// <param name="Lines">หนึ่งบรรทัดต่อรายการ คั่นด้วย <c>;</c> — รูป <c>ยอดก่อนVAT|อัตรา|VAT|ราคาต่อหน่วย|ส่วนลด%|ส่วนลดบาท|หัก ณ ที่จ่าย</c></param>
/// <param name="Gaps">ช่องว่างที่ตัวสร้างจะเขียนเป็น <c>[Σ-GAP]</c> (เคสกระทบยอด + ชนิดปัญหาของ <see cref="OcrAmountIntegrity"/>) · <c>ok</c> = ไม่มี</param>
/// <param name="RoundingAdjustment">ผลต่างปัดเศษหัวเอกสาร (= −Σ shift ของบรรทัด)</param>
/// <param name="DuplicateRowsDropped">จำนวนแถวที่ด่านแถวซ้ำ (<see cref="OcrDuplicateLineGuard"/>) ตัดออกก่อนทุกขั้น — 0 = ไม่ตัด
/// (2026-10-09 ใบต้นฉบับ+สำเนา · <see cref="ReplayPaper.EngineLines"/>)</param>
/// <param name="QuantityGuard">ผลของตัวกันจำนวนระเบิด (<see cref="OcrLineQuantityGuard"/>) ต่อบรรทัด <c>การกระทำ|จำนวน|ราคา|ส่วนลดบรรทัด|block/note/-</c> คั่นด้วย <c>;</c>
/// (2026-10-16 RE-20261000111) · null = ใบไม่มีรายการ</param>
/// <param name="Descriptions">คำอธิบายบรรทัดหลังตัวกันจำนวนระเบิด คั่นด้วย <c>;</c> — <b>เฉพาะใบที่ตัวกันเปลี่ยนคำอธิบาย</b> (ย้ายจำนวนหน่วยมิเตอร์ไปไว้ในคำอธิบาย ·
/// ทีม OCR 8 2026-10-16) · null = ไม่เปลี่ยน (ใบเดิมไม่มีแถวนี้)</param>
/// <param name="SummaryDims">ตารางชนะแทนรายการ: ผัง|โครงการ ที่บรรทัดสรุปแบก (+ <c>|mixed</c> เมื่อรายการเดิมหลายผัง/โครงการ ⇒ หมายเหตุรายการเดิม) —
/// เฉพาะใบในชุดที่ระบุผัง/โครงการรายบรรทัด (2026-10-16 คำตัดสินเจ้าของภาคต่อ) · null = ไม่มีช่องนี้</param>
public sealed record ReplayBuiltLines(
    string Mode, string? ReconCase, bool PricesIncludeVat, string Lines, string Gaps, decimal RoundingAdjustment,
    int DuplicateRowsDropped = 0, string? QuantityGuard = null, string? Descriptions = null, string? SummaryDims = null);

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
    /// <summary>5 บรรทัดของใบ BS2026100001 ตามที่กระดาษพิมพ์ (คำอธิบายตามหน้า review ของผู้ใช้ · จำนวน × ราคา = ยอด ทุกบรรทัด · Σ 21,005.00)</summary>
    private static readonly ReplayLine[] BoonsapPrintedLines =
    {
        new("สายไฟ FD-CV 0.6/1KV 1*16 mm2 YAZAKI ดำ", 100m, 103.16m, 10316.00m),
        new("สายไฟ FD-0.6/1K.V-CV 1x10 SQ.mm ยาซากิ (ดำ)", 50m, 69.26m, 3463.00m),
        new("สายไฟ IEC 01 THW 1 x 6 SQ.MM YAZAKI สีดำ", 100m, 36.18m, 3618.00m),
        new("สายไฟ IEC 01 THW 1 x 4 SQ.MM YAZAKI ดำ", 100m, 22.08m, 2208.00m),
        new("สายไฟ IEC 01 THW 1 x 2.5 SQ.MM YAZAKI สีดำ", 100m, 14.00m, 1400.00m),
    };

    /// <summary>7 แถวสินค้าของ <see cref="OcrPaperSamples.MakroPage1of2Synthetic"/> ตามที่พิมพ์ (ราคารวม VAT · Σ 11,208.00)</summary>
    private static readonly ReplayLine[] MakroSyntheticItems =
    {
        new("ข้าวสารหอมมะลิ 100% ตราเอโร่ 15 กก.", 1m, 1000.00m, 1000.00m),
        new("ไข่ไก่ เบอร์ 0 แผง 30 ฟอง", 1m, 248.00m, 248.00m),
        new("น้ำยาล้างจาน ซันไลต์ 3,600 มล.", 6m, 189.00m, 1134.00m),
        new("กระดาษทิชชู่ ม้วนใหญ่ 24 ม้วน", 10m, 259.00m, 2590.00m),
        new("ถุงขยะดำ ฮีโร่ 30x40 นิ้ว", 20m, 59.00m, 1180.00m),
        new("น้ำดื่ม เอโร่ 600 มล. แพ็ค 12 ขวด", 15m, 64.00m, 960.00m),
        new("แชมพู ซันซิล 900 มล.", 8m, 512.00m, 4096.00m),
    };

    /// <summary>แถวส่วนลด 3 แถวของ Makro หน้า 2/2 (405901117163) ตามที่ engine คืนเป็น "สินค้า" — ไม่มีจำนวน/ราคา · ผังรายบรรทัดตามที่ส่ง (null = ผังหัวใบ)</summary>
    private static ReplayLine[] MakroPage2DiscountRows(string? a1, string? a2, string? a3) => new ReplayLine[]
    {
        new("ซื้อสินค้าฮีโร่ ถุงขยะ ถุงหูหิ้ว ถุงซิป ครบ 359 ลด 30 บาท(*1.0)", null, null, 30.00m, Account: a1),
        new("ลด 60บเมื่อซื้อสินค้ายูนิลีเวอร์ ดูแลเส้นผม ผิวหน้า ผิวกาย ที่ร่วมครบ 599 บ(*1.0)", null, null, 60.00m, Account: a2),
        new("SLAB1(*5.0)", null, null, 10.00m, Account: a3),
    };

    /// <summary>ลบคอลัมน์รหัส ภ.พ. ออกจากแถวสินค้า ("248.00 1 248.00" → "248.00 248.00") — จำลอง engine ที่อ่านคอลัมน์รหัสไม่ได้</summary>
    private static string DropVatCodeColumn(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"(\d\.\d{2}) [0-9] (\d[\d,]*\.\d{2})$", "$1 $2",
            System.Text.RegularExpressions.RegexOptions.Multiline);

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

        // ── รอบ 190 ข้อ 9: ส่วนลดท้ายบิล · ใบผสม VAT/ไม่มี VAT (ข้อความเต็มใน OcrPaperSamples) ──

        // ใบ A ของเจ้าของ (Wine Pro) — ใบที่ถูกอยู่แล้ว: V ทุกบรรทัด · บรรทัด 0.00 · VAT INCLUDED
        new ReplayPaper("winepro-vat-included", OcrPaperSamples.WinePro,
            EngineSubTotal: 3357.94m, EngineVat: 235.06m, EngineTotal: 3593m,
            LineAmounts: new[] { 524m, 3069m, 0m },
            // 2026-10-09 ratchet ระดับบรรทัด: จำนวน × ราคาตามที่กระดาษพิมพ์ (ช่องเดิมของใบนี้ไม่เปลี่ยน — Lines ป้อนเฉพาะขั้นสร้างบรรทัด)
            Lines: new ReplayLine[]
            {
                new("BELCOLLE Moscato d' Asti DOCG", 1m, 524.00m, 524.00m),
                new("VINA TOLDOS Red", 12m, 255.75m, 3069.00m),
                new("2 BOTTLES WOVEN WINE BAG (Wine", 1m, 0.00m, 0.00m),
            }),

        // ใบกำกับร้านวัสดุ ลดท้ายบิล 5% — ตัวอ่านเดิมหยิบ "ยอดหลังหักส่วนลด 1,325.25" เป็นส่วนลด
        // 2026-10-09: เพิ่มรายการตามที่กระดาษพิมพ์ (3 บรรทัด) ⇒ ใบนี้ได้แถวใหม่ LineVatRates/LineVatPlan/Lines (ช่องเดิมไม่เปลี่ยน)
        new ReplayPaper("hardware-bill-discount", OcrPaperSamples.HardwareBillDiscount,
            EngineSubTotal: 1395m, EngineVat: 92.77m, EngineTotal: 1418.02m,
            LineAmounts: new[] { 370m, 890m, 135m },
            Lines: new ReplayLine[]
            {
                new("ปูนกาวซีเมนต์ 20 กก.", 2m, 185.00m, 370.00m),
                new("สีน้ำอะครีลิค 3.5 ลิตร", 1m, 890.00m, 890.00m),
                new("แปรงทาสี 4 นิ้ว", 3m, 45.00m, 135.00m),
            }),

        // ทีม D 2026-10-13 (B5 · "เคส C ไม่มีราคาต่อหน่วย"): กระดาษใบเดียวกับข้างบน แต่ engine คืน**แค่ยอดบรรทัด** (ไม่มีจำนวน/ราคา —
        // รูปแบบที่ engine ภาพคืนบ่อย) ⇒ ตัวสร้างเก็บ UnitPrice = ยอดหลังกระจายส่วนลด · หัวใบ/ยอด/VAT เท่าใบข้างบนทุกตัว
        // ล็อกว่าบรรทัดแบบนี้ได้ส่วนลด 0% (เดิม 5% ⇒ เปิดแก้แล้วบันทึก ComputeLineAmounts หักซ้ำ 351.50 → 333.92)
        new ReplayPaper("hardware-bill-discount-amounts-only", OcrPaperSamples.HardwareBillDiscount,
            EngineSubTotal: 1395m, EngineVat: 92.77m, EngineTotal: 1418.02m,
            LineAmounts: new[] { 370m, 890m, 135m }),

        // ใบซูเปอร์มาร์เก็ตราคารวม VAT ลดสมาชิก (engine หยิบ "รวม 774" เป็น SubTotal)
        // 2026-10-09: เพิ่มรายการตามที่กระดาษพิมพ์ (3 บรรทัด · ราคารวม VAT) ⇒ แถวใหม่เท่านั้น
        new ReplayPaper("supermarket-member-discount", OcrPaperSamples.SupermarketMemberDiscount,
            EngineSubTotal: 774m, EngineVat: 48.10m, EngineTotal: 735.30m,
            LineAmounts: new[] { 159m, 438m, 177m },
            Lines: new ReplayLine[]
            {
                new("น้ำยาล้างจาน 3,600 มล.", 1m, 159.00m, 159.00m),
                new("กระดาษทิชชู่ 24 ม้วน", 2m, 219.00m, 438.00m),
                new("ถุงขยะ 30x40 นิ้ว", 3m, 59.00m, 177.00m),
            }),

        // ใบค้าส่งผสมสินค้ายกเว้น §81 (N) กับสินค้า VAT (V)
        new ReplayPaper("wholesale-mixed-vat", OcrPaperSamples.WholesaleMixedVat,
            EngineSubTotal: 764m, EngineVat: 28m, EngineTotal: 792m,
            LineAmounts: OcrPaperSamples.WholesaleLineAmounts,
            Lines: new ReplayLine[]
            {
                new("ไข่ไก่ เบอร์ 2 (30 ฟอง)", 1m, 125.00m, 125.00m),
                new("หมูสามชั้น 1 กก.", 1m, 189.00m, 189.00m),
                new("ผักกาดขาว", 2m, 25.00m, 50.00m),
                new("น้ำมันพืช 1 ลิตร", 2m, 52.00m, 104.00m),
                new("น้ำปลา 700 มล.", 3m, 35.00m, 105.00m),
                new("ผงซักฟอก 2.7 กก.", 1m, 219.00m, 219.00m),
            }),

        // ── รอบ 192 (Total-first): กระดาษจริง 3 ใบของเจ้าของ (BRIEF-TOTAL) ──

        // Makro หน้า 3/3 — engine หยิบป้าย "TOTAL 24,110.00" (ยอดก่อนหักส่วนลด 297.75)
        new ReplayPaper("makro-page3-total-first", OcrPaperSamples.MakroPage3of3,
            EngineSubTotal: 22663.97m, EngineVat: 1148.28m, EngineTotal: 24110.00m),

        // 2026-10-16 Makro หน้า 2/2 (ผู้ใช้รายงาน "ยอดส่วนไม่มี vat สร้างผิด · ควร 1,220 · รวม 10,980") — หัวสแกนตามภาพหน้าจอผู้ใช้:
        // ฐาน 11,208 (= ป้าย "ราคาสินค้ารวมภาษีมูลค่าเพิ่ม/ TOTAL" ยอดรวม VAT ก่อนส่วนลด) · VAT 638.50 · ยอดรวม 11,846.50 (= 11,208 + VAT ซ้ำ ไม่มีบนกระดาษ)
        new ReplayPaper("makro-page2-vat-added-twice", OcrPaperSamples.MakroPage2of2,
            EngineSubTotal: 11208.00m, EngineVat: 638.50m, EngineTotal: 11846.50m),

        // 2026-10-16 DECISIONS ข้อ 153 ข้อ 2 — ภาพเดียวกันผ่าน Tesseract 5.3.4 จริง: คำอธิบายรหัส "1«สินค้าได้รับการยกเว้น…" (ไม่มี "=") ⇒ รหัส 1 ไม่รู้ความหมาย ·
        // ชุดกระดาษไม่มีคำตอบ AI ⇒ ค่าตั้งต้น "ยกเว้น" (cold start / ปิด AI) · หัวสแกน = ยอดที่ยึดได้ (10,980 · ฐาน 10,341.50) — ⚠️ ข้อความ Tesseract นี้
        // ยึด 10,980 จากหัวสแกน 11,846.50 ไม่ได้ (NET AMOUNT อ่านเป็น 40,980 · ป้าย TOTAL เพี้ยน — ช่องเปิดของขั้นยึดยอด ไม่ใช่ของตารางรหัส)
        new ReplayPaper("makro-page2-tesseract-garbled-legend", OcrMakroPage2VatAddedTwiceTests.MakroPage2of2Tesseract,
            EngineSubTotal: 10341.50m, EngineVat: 638.50m, EngineTotal: 10980.00m),

        // 2026-10-16 รหัส ภ.พ. รายบรรทัด (คำสั่งเจ้าของ "อัตรา VAT ต่อบรรทัดยังเดาจากชื่อสินค้า … ควรต้องตามตาราง ภพ") — <b>หน้า 1 สังเคราะห์</b> + หน้า 2 จริง
        // (เราไม่มีหน้า 1 ของจริง · ตัวเลขคิดมือให้ลงกับตารางหน้า 2 ทุกสตางค์ — ดู OcrPaperSamples.MakroPage1of2Synthetic) · หัวสแกนหลังยึดยอด 10,980
        new ReplayPaper("makro-p1p2-vatcode-synthetic", OcrPaperSamples.MakroPage1of2Synthetic + "\n" + OcrPaperSamples.MakroPage2of2,
            EngineSubTotal: 10341.50m, EngineVat: 638.50m, EngineTotal: 10980.00m, VendorTaxId: "0107567000414",
            Lines: MakroSyntheticItems),

        // ใบเดียวกัน แต่ engine อ่านคอลัมน์รหัส ภ.พ. ไม่ได้ (แถวสินค้าไม่มีรหัส) — มีส่วนลด 228 ⇒ แบ่งส่วนลดตามรหัสไม่ได้ ⇒ ต้องฟ้อง ไม่ลงเงียบ
        new ReplayPaper("makro-p1p2-no-row-codes-synthetic", DropVatCodeColumn(OcrPaperSamples.MakroPage1of2Synthetic) + "\n" + OcrPaperSamples.MakroPage2of2,
            EngineSubTotal: 10341.50m, EngineVat: 638.50m, EngineTotal: 10980.00m, VendorTaxId: "0107567000414",
            Lines: MakroSyntheticItems),

        // ใบเดียวกัน แต่ engine คืนแถวส่วนลด 5 แถวเป็น "สินค้า" ยอดบวก (28 · 100 · 30 · 60 · 10) — Σ บรรทัดเกิน ⇒ ต้องฟ้อง ไม่แบ่งตามรหัสเงียบ ๆ
        new ReplayPaper("makro-p1p2-discount-rows-as-items-synthetic", OcrPaperSamples.MakroPage1of2Synthetic + "\n" + OcrPaperSamples.MakroPage2of2,
            EngineSubTotal: 10341.50m, EngineVat: 638.50m, EngineTotal: 10980.00m, VendorTaxId: "0107567000414",
            Lines: MakroSyntheticItems.Concat(new ReplayLine[]
            {
                new("ลดราคาไข่ไก่ แม็คโคร(*1.0)", null, null, 28.00m),
                new("ลด 100บ เมื่อซื้อทิชชู่ครบ 2,500 บ(*1.0)", null, null, 100.00m),
                new("ซื้อสินค้าฮีโร่ ถุงขยะ ถุงหูหิ้ว ถุงซิป ครบ 359 ลด 30 บาท(*1.0)", null, null, 30.00m),
                new("ลด 60บเมื่อซื้อสินค้ายูนิลีเวอร์ ดูแลเส้นผม ผิวหน้า ผิวกาย ที่ร่วมครบ 599 บ(*1.0)", null, null, 60.00m),
                new("SLAB1(*5.0)", null, null, 10.00m),
            }).ToList()),

        // 2026-10-16 Makro หน้า 2/2 สแกนซ้ำ (เจ้าของ: "ยอดรวมลงถูกแต่บอดราย รายการ … เห็นตารางสรุปรวม ก็ใช้ตารางนั้นได้เลย") — engine คืน<b>แถวส่วนลด 3 แถวเป็นสินค้า</b>
        // (30 · 60 · 10 ไม่มีจำนวน/ราคา · ผัง 52120 ทุกแถวตามภาพหน้าจอ) · หัวสแกนตามภาพหน้าจอ: ยอดก่อน VAT 11,208 (= ป้าย "รวมภาษีมูลค่าเพิ่ม/ TOTAL" ก่อนส่วนลด) ·
        // VAT 638.50 · ยอดรวม 10,980 (ถูก) · คำตอบที่ถูก = บรรทัดสรุปตามตาราง ยกเว้น 1,220.00 + 9,121.50/638.50 · ฐานหัวใบ 10,341.50 · ไม่มี [Σ-GAP]
        new ReplayPaper("makro-page2-discount-rows-only", OcrPaperSamples.MakroPage2of2,
            EngineSubTotal: 11208.00m, EngineVat: 638.50m, EngineTotal: 10980.00m, VendorTaxId: "0107567000414",
            Lines: MakroPage2DiscountRows("52120", "52120", "52120")),

        // ใบเดียวกัน — แถว SLAB1 ไม่มีผังของตัวเอง (ผังหัวใบ) ⇒ เดิม "ผังต่างกัน 2 ผัง" (ฝ่ายค้าน f7c29c2c P1-3) คงแถวส่วนลด 3 แถวเป็นสินค้า 7% + ⛔ Σ บรรทัด 100 น้อยกว่า
        // ยอดก่อน VAT 10,341.50 (ทำซ้ำผ่าน PreviewDocumentLinesAsync จริงบน PostgreSQL) — คำตัดสินเจ้าของภาคต่อ: ตารางลงยอด ⇒ ตารางชนะ · ผังยอดมากสุด + หมายเหตุรายการเดิม
        new ReplayPaper("makro-page2-discount-rows-mixed-accounts", OcrPaperSamples.MakroPage2of2,
            EngineSubTotal: 11208.00m, EngineVat: 638.50m, EngineTotal: 10980.00m, VendorTaxId: "0107567000414",
            Lines: MakroPage2DiscountRows("52120", "52120", null)),

        // ใบเดียวกัน ข้อความ Tesseract 5.3.4 จริง (คำอธิบายรหัส 1 เพี้ยน ⇒ ค่าตั้งต้นยกเว้น · ยอดยึดไม่ได้จากข้อความ) + แถวส่วนลด 3 แถว
        new ReplayPaper("makro-page2-tesseract-discount-rows-only", OcrMakroPage2VatAddedTwiceTests.MakroPage2of2Tesseract,
            EngineSubTotal: 11208.00m, EngineVat: 638.50m, EngineTotal: 10980.00m, VendorTaxId: "0107567000414",
            Lines: MakroPage2DiscountRows("52120", "52120", "52120")),

        // Shopee — ใบกำกับ 536 · ส่วนลดพิเศษ 98 หลัง VAT · จ่าย 438
        new ReplayPaper("uptoyou-shopee-pay-not-total", OcrPaperSamples.UptoyouShopee,
            EngineSubTotal: 500.93m, EngineVat: 35.07m, EngineTotal: 536.00m,
            LineAmounts: new[] { 536.00m },
            Lines: new ReplayLine[] { new("[ซัก + ปรับ] ไฮยีน เลิฟทัช น้ำยาปรับผ้านุ่ม 1000 มล. + น้ำยาซักผ้า 1400 มล. [แพ็คคู่]", 2m, 268.00m, 536.00m) }),

        // Lazada — ส่วนลด 216.82 ก่อน VAT · แถว (0.00) ของกลุ่มยกเว้น
        new ReplayPaper("scommerce-lazada-prevat-discount", OcrPaperSamples.ScommerceLazada,
            EngineSubTotal: 4912.15m, EngineVat: 328.67m, EngineTotal: 5024.00m,
            // รอบ 195: ยอดบรรทัดตามที่พิมพ์ (นมผง 4,912.15 · ค่าจัดส่ง 0.00) — ให้ขั้น 7 (ชั้นพิสูจน์ทั้งใบ) ตรวจใบนี้ได้
            LineAmounts: new[] { 4912.15m, 0.00m },
            // 4 × 1,228.04 = 4,912.16 แต่พิมพ์ 4,912.15 — ยอดที่พิมพ์ชนะ · ส่วนต่าง 0.01 ไปที่ผลต่างปัดเศษหัวเอกสาร (รอบ 193 ข้อ 8)
            Lines: new ReplayLine[]
            {
                new("นมผงเอนฟาโกร เอนฟินิทัส สูตร3 1425 กรัม:สูตร3", 4m, 1228.04m, 4912.15m),
                new("ค่าจัดส่ง / Shipping Fee", 1m, 0.00m, 0.00m),
            }),

        // ── 2026-10-09 ตรวจความครอบคลุมเส้นกระดาษ: ใบแจ้งค่าบริการรายเดือนที่มียอดค้างชำระจากรอบก่อน ──

        // บิลเน็ตที่มีค้าง 500 — engine หยิบ "ยอดรวมที่ต้องชำระ 1,570" (= ใบกำกับ 1,070 + ค้าง 500) เป็นยอดรวม ⇒ ต้องยึด 1,070
        new ReplayPaper("telecom-prior-balance", OcrPaperSamples.TelecomBillPriorBalance,
            EngineSubTotal: 1000m, EngineVat: 70m, EngineTotal: 1570m,
            LineAmounts: new[] { 1000m },
            Lines: new ReplayLine[] { new("ค่าบริการรายเดือน", 1m, 1000.00m, 1000.00m) }),

        // บิลเดียวกันรอบที่ไม่มีค้าง (แถวฟอร์ม 0.00) — ใบที่ถูกอยู่แล้ว ห้ามแตะ
        new ReplayPaper("telecom-no-arrears", OcrPaperSamples.TelecomBillNoArrears,
            EngineSubTotal: 1000m, EngineVat: 70m, EngineTotal: 1070m,
            LineAmounts: new[] { 1000m },
            Lines: new ReplayLine[] { new("ค่าบริการรายเดือน", 1m, 1000.00m, 1000.00m) }),

        // ── 2026-10-09 ผู้ใช้รายงาน "จำนวนทุกบรรทัด ×2" — PDF ต้นฉบับ (หน้า 1) + สำเนา (หน้า 2) ของใบเดียวกัน ──

        // ใบ BS2026100001 บุญทรัพย์ ถาวร (กระดาษจริง): engine คืน 5 บรรทัดสองรอบ (EngineLines) ⇒ ต้องกลับมา 5 บรรทัดจำนวน 100/50/100/100/100
        // (คำอธิบายใน Lines = ตามที่ engine/หน้า review แสดง · ตัวเลขทุกตัวจากกระดาษ · ข้อความ text-layer จริงใน OcrPaperSamples)
        new ReplayPaper("boonsap-original-copy-pages", OcrPaperSamples.BoonsapOriginalCopyPages,
            EngineSubTotal: 21005.00m, EngineVat: 1470.35m, EngineTotal: 22475.35m,
            LineAmounts: new[] { 10316.00m, 3463.00m, 3618.00m, 2208.00m, 1400.00m },
            Lines: BoonsapPrintedLines,
            EngineLines: BoonsapPrintedLines.Concat(BoonsapPrintedLines).ToList()),

        // ทิศตรงข้าม: กระดาษพิมพ์รายการเดียวกันสองบรรทัดจริง (น้ำดื่ม 10 ขวด × 2 บรรทัด · หัวใบ 100) — Σ ทุกแถวตรงหัวใบ ⇒ ห้ามตัด
        new ReplayPaper("water-genuine-repeat-rows",
            "ใบเสร็จรับเงิน/ใบกำกับภาษี\nน้ำดื่ม 10 ขวด 5.00 50.00\nน้ำดื่ม 10 ขวด 5.00 50.00\n"
            + "รวมเป็นเงิน 100.00\nภาษีมูลค่าเพิ่ม 7% 7.00\nรวมทั้งสิ้น 107.00",
            EngineSubTotal: 100m, EngineVat: 7m, EngineTotal: 107m,
            LineAmounts: new[] { 50m, 50m },
            Lines: new ReplayLine[] { new("น้ำดื่ม", 10m, 5.00m, 50.00m), new("น้ำดื่ม", 10m, 5.00m, 50.00m) }),

        // 2026-10-16 ใบจริง RE-20261000111 (พรามี่): ส่วนลดรายบรรทัด 293.00 พิมพ์บนแถว · engine คืน (1, 1,330, 1,037) ไม่มีช่องส่วนลด ⇒
        // เดิมตัวกันจำนวนระเบิดหารจำนวนเป็น 0.78 · ต้องได้ จำนวน 1 · ราคา 1,330 (รวม VAT) · ส่วนลด 293 · ฐาน 969.16 · VAT 67.84 · รวม 1,037.00
        new ReplayPaper("pramie-line-discount-re111", OcrPaperSamples.PramieLineDiscountPdfText,
            EngineSubTotal: 969.16m, EngineVat: 67.84m, EngineTotal: 1037.00m,
            LineAmounts: new[] { 1037.00m },
            Lines: new ReplayLine[] { new("(แพ็ค3)แผ่นรองซับ M 50 แผ่น (P00022)", 1m, 1330.00m, 1037.00m) }),

        // ทิศตรงข้าม (1): สินค้าชั่งน้ำหนัก — กระดาษพิมพ์แค่ราคาต่อ กก. กับยอด ไม่พิมพ์จำนวน · engine เติมจำนวน 1 ⇒ จำนวน = ยอด ÷ ราคา ยังถูก (0.45 กก.)
        // แต่ไม่เงียบ: [LINE-QTY] (ระบบเปลี่ยนค่าที่ engine อ่านโดยไม่มีจำนวนบนกระดาษ)
        new ReplayPaper("weighed-item-qty-not-printed",
            "ใบเสร็จรับเงิน\nหมูสับ (ราคาต่อ กก.) 120.00 54.00\nรวมทั้งสิ้น 54.00",
            EngineSubTotal: 54m, EngineVat: 0m, EngineTotal: 54m,
            LineAmounts: new[] { 54.00m },
            Lines: new ReplayLine[] { new("หมูสับ", 1m, 120.00m, 54.00m) }),

        // ทิศตรงข้าม (2): engine อ่านจำนวนผิด (1) ทั้งที่กระดาษพิมพ์ 2 ติดก่อนราคา — จำนวน 1 ไม่ได้พิมพ์บนแถว ⇒ ยอด ÷ ราคา = 2 (ตรงเลขบนกระดาษ) + [LINE-QTY]
        new ReplayPaper("qty-misread-engine-one",
            "ใบเสร็จรับเงิน/ใบกำกับภาษี\nปากกาเจล 2 ด้าม 10.00 20.00\nรวมเป็นเงิน 20.00\nภาษีมูลค่าเพิ่ม 7% 1.40\nรวมทั้งสิ้น 21.40",
            EngineSubTotal: 20m, EngineVat: 1.40m, EngineTotal: 21.40m,
            LineAmounts: new[] { 20.00m },
            Lines: new ReplayLine[] { new("ปากกาเจล", 1m, 10.00m, 20.00m) }),

        // ทิศตรงข้าม (3): จำนวน 2 พิมพ์บนแถว แต่ 2 × 100 ≠ 150 และไม่มีส่วนลดบนกระดาษ ⇒ ห้ามแต่งจำนวน 1.5 — คงจำนวน/ราคา + [LINE-QTY] + [Σ-GAP] ให้คนดู
        new ReplayPaper("qty-printed-gap-unexplained",
            "ใบเสร็จรับเงิน/ใบกำกับภาษี\nสายยาง 2 ม้วน 100.00 150.00\nรวมเป็นเงิน 150.00\nภาษีมูลค่าเพิ่ม 7% 10.50\nรวมทั้งสิ้น 160.50",
            EngineSubTotal: 150m, EngineVat: 10.50m, EngineTotal: 160.50m,
            LineAmounts: new[] { 150.00m },
            Lines: new ReplayLine[] { new("สายยาง", 2m, 100.00m, 150.00m) }),

        // ── ฝ่ายค้าน 658e3705 (ทีม OCR 6 · 2026-10-16) — ใบที่ทำให้อัตราแท็ก [LINE-QTY] มีความหมาย (ใบละหนึ่ง defect class) ──
        // (1) คอลัมน์หัก ณ ที่จ่าย: ค่าบริการ 10,000 หัก 3% = 9,700 · เดิม "ส่วนลด 300" อนุมัติเองได้ ⇒ ยอดบรรทัด 10,000 (ก่อนหัก) + แท็กบล็อก
        //     · หัก ณ ที่จ่ายหัวใบ 300 เป็นของตัวอ่าน WHT เดิม (PaperWhtReader) ไม่ใช่ส่วนลด
        new ReplayPaper("wht-column-service-invoice",
            "ใบแจ้งหนี้/ใบกำกับภาษี\nรายการ จำนวน ราคา/หน่วย หัก ณ ที่จ่าย ยอดสุทธิ\nค่าบริการทำความสะอาด 1 10,000.00 3% 9,700.00\n"
            + "รวมเป็นเงิน 10,000.00\nภาษีมูลค่าเพิ่ม 7% 700.00\nรวมทั้งสิ้น 10,700.00\nหัก ณ ที่จ่าย 3% 300.00\nยอดชำระ 10,400.00",
            EngineSubTotal: 10000m, EngineVat: 700m, EngineTotal: 10700m,
            LineAmounts: new[] { 9700.00m },
            Lines: new ReplayLine[] { new("ค่าบริการทำความสะอาด", 1m, 10000.00m, 9700.00m) }),

        // (2) ใบงวดที่หักมัดจำ: งวด 2 100,000 หักมัดจำ 30,000 = 70,000 · เดิม "ส่วนลด 30,000" (มัดจำ 11810 ไม่ถูกล้าง) ⇒ ยอดบรรทัด 100,000 + แท็กบล็อก
        //     · ตัวสร้างบรรทัดฟ้อง [Σ-GAP] ต่อ (การหักมัดจำต้องลงที่เอกสารผ่านวงจรมัดจำ — ไม่แต่งเป็นส่วนลดท้ายบิล)
        new ReplayPaper("deposit-installment-invoice",
            "ใบแจ้งหนี้/ใบกำกับภาษี\nงวดที่ 2 1 100,000.00 หักมัดจำ 30,000.00 70,000.00\nรวมเป็นเงิน 70,000.00\nภาษีมูลค่าเพิ่ม 7% 4,900.00\nรวมทั้งสิ้น 74,900.00",
            EngineSubTotal: 70000m, EngineVat: 4900m, EngineTotal: 74900m,
            LineAmounts: new[] { 70000.00m },
            Lines: new ReplayLine[] { new("งวดที่ 2", 1m, 100000.00m, 70000.00m) }),

        // (3) รหัสสินค้าบนแถว: "SKU 50" ลงตัวเป็นส่วนต่าง 50 พอดี · เดิม "ส่วนลด 50" อนุมัติเองได้ ⇒ ยอดตามกระดาษ (450) ส่วนต่างเป็นส่วนลดที่ระบุชัด + แท็กบล็อก
        new ReplayPaper("sku-code-on-row",
            "ใบเสร็จรับเงิน\nรายการ จำนวน ราคา ยอดเงิน\nสินค้า 1 500.00 SKU 50 450.00\nรวมทั้งสิ้น 450.00",
            EngineSubTotal: null, EngineVat: 0m, EngineTotal: 450m,
            LineAmounts: new[] { 450.00m },
            Lines: new ReplayLine[] { new("สินค้า", 1m, 500.00m, 450.00m) }),

        // (4) ใบเสร็จมีเลขลำดับแถว: "1 หมูสับ 120.00 54.00" · เดิมเลข 1 = จำนวนที่พิมพ์ ⇒ KeepPrintedGap · ต้องได้ 0.45 กก. (ยังไม่พิสูจน์ ⇒ แท็กบล็อก) ·
        //     แถว 2 ลงตัวอยู่แล้ว ⇒ ไม่แตะ
        new ReplayPaper("row-index-receipt",
            "ใบเสร็จรับเงิน\n1 หมูสับ 120.00 54.00\n2 ไข่ไก่ 10 ฟอง 4.00 40.00\nรวมทั้งสิ้น 94.00",
            EngineSubTotal: null, EngineVat: 0m, EngineTotal: 94m,
            LineAmounts: new[] { 54.00m, 40.00m },
            Lines: new ReplayLine[] { new("หมูสับ", 1m, 120.00m, 54.00m), new("ไข่ไก่", 10m, 4.00m, 40.00m) }),

        // (5) บิลค่าไฟรายเดือน (เคส ก): engine อ่านยอดมาใส่ราคาต่อหน่วย · มิเตอร์ 3,611 หน่วยพิมพ์คู่หน่วยนับ ⇒ ราคา = ยอด ÷ จำนวน · พิสูจน์แล้ว ⇒ แจ้ง ไม่บล็อก
        new ReplayPaper("utility-meter-bill",
            "การไฟฟ้าส่วนภูมิภาค ใบแจ้งค่าไฟฟ้า/ใบกำกับภาษี\nค่าไฟฟ้า 3,611 หน่วย 16,351.48\nรวมเงิน 16,351.48\nภาษีมูลค่าเพิ่ม 7% 1,144.60\nรวมทั้งสิ้น 17,496.08",
            EngineSubTotal: 16351.48m, EngineVat: 1144.60m, EngineTotal: 17496.08m,
            LineAmounts: new[] { 16351.48m },
            Lines: new ReplayLine[] { new("ค่าไฟฟ้า", 3611m, 16351.48m, 16351.48m) }),

        // (6) ทิศตรงข้ามของ (1): หัวคอลัมน์ "ส่วนลด" ⇒ 3% คือส่วนลดจริง ⇒ ส่วนลดบรรทัด 300 + หมายเหตุแจ้ง (ไม่บล็อก)
        new ReplayPaper("discount-header-invoice",
            "ใบกำกับภาษี\nรายการ จำนวน ราคา/หน่วย ส่วนลด จำนวนเงิน\nค่าบริการออกแบบ 1 10,000.00 3% 9,700.00\nรวมเป็นเงิน 9,700.00\nภาษีมูลค่าเพิ่ม 7% 679.00\nรวมทั้งสิ้น 10,379.00",
            EngineSubTotal: 9700m, EngineVat: 679m, EngineTotal: 10379m,
            LineAmounts: new[] { 9700.00m },
            Lines: new ReplayLine[] { new("ค่าบริการออกแบบ", 1m, 10000.00m, 9700.00m) }),

        // ── ทีม OCR 8 (2026-10-16) — บิลค่าไฟจริง กฟภ. (PDF ข้อความ · ไม่มี XML) · เจ้าของ: "มี 2 หน่วยนะ 4หน่วยน่าจะเป็นค่า ft" ──
        // ตารางพิมพ์ จำนวนหน่วย 4,272.00 + จำนวนเงิน 19,381.50 · ไม่มีราคาต่อหน่วย ⇒ 1 × 19,381.50 · "(4,272 หน่วย)" ในคำอธิบาย (เดิม 4.5369/หน่วย ⇒ เก็บ 4.54 ⇒
        // บันทึกซ้ำ 19,394.88) · หัวใบ = ค่าที่ engine ภาพอ่าน (Azure/python) — เส้นข้อความ PDF ล้วนอ่านหัวใบผิด (ล็อกแยกใน OcrMeterBillLumpSumTests)
        new ReplayPaper("pea-electricity-bill-0869", OcrPaperSamples.PeaElectricityBill0869,
            EngineSubTotal: 19381.50m, EngineVat: 1356.71m, EngineTotal: 20738.21m,
            LineAmounts: new[] { 19381.50m },
            Lines: new ReplayLine[] { new("ค่าไฟฟ้า ประจำเดือน 08/2569", 4272m, null, 19381.50m) },
            VendorTaxId: "0994000165501"),

        // ── ทีม OCR 10 (2026-10-16) — สองใบจริงข้างบน ด้วยข้อความ PDF ที่ประกอบตามพิกัด (Helpers/PdfPositionalText · ข้อความที่ PdfTextLayerExtractor คืนตอนนี้) ──
        // ตัวเลข engine / บรรทัด / เลขผู้ขาย เท่าใบเดิมทุกตัว ⇒ ผลต่างระหว่างคู่ใบ = ผลของข้อความอย่างเดียว · ใบเดิม (page.Text) คงไว้ไม่แก้
        new ReplayPaper("pea-electricity-bill-0869-positional", OcrPaperSamples.PeaElectricityBill0869Positional,
            EngineSubTotal: 19381.50m, EngineVat: 1356.71m, EngineTotal: 20738.21m,
            LineAmounts: new[] { 19381.50m },
            Lines: new ReplayLine[] { new("ค่าไฟฟ้า ประจำเดือน 08/2569", 4272m, null, 19381.50m) },
            VendorTaxId: "0994000165501"),
        new ReplayPaper("pramie-re111-positional", OcrPaperSamples.PramieRe111Positional,
            EngineSubTotal: 969.16m, EngineVat: 67.84m, EngineTotal: 1037.00m,
            LineAmounts: new[] { 1037.00m },
            Lines: new ReplayLine[] { new("(แพ็ค3)แผ่นรองซับ M 50 แผ่น (P00022)", 1m, 1330.00m, 1037.00m) }),

        // ── ทีม OCR 13 (2026-10-16 · ฝ่ายค้าน OCR 10) — ข้อความ PDF ตามพิกัดรุ่นปัจจุบัน (ช่องคั่น tab) ของสองใบจริงที่รุ่น OCR 10 อ่านแย่กว่า page.Text ──
        // หัวใบ (ชื่อ/เลข/สาขา) ล็อกใน PdfTextOcr13Tests · ที่นี่ล็อกตัวตัดสิน pure (ยอดยึด · ที่อยู่ · หัก ณ ที่จ่าย) — ไม่ป้อนบรรทัด (บรรทัดสรุป)
        new ReplayPaper("crc-thaiwatsadu-srcie75384-positional", OcrPaperSamples.CrcThaiwatsaduSrcie75384Positional,
            EngineSubTotal: 2991.59m, EngineVat: 209.41m, EngineTotal: 3201.00m),
        // 50 ทวิ: ยอดของเอกสาร = จำนวนเงินที่จ่าย 7,264.00 (แถว "รวมเงินที่จ่ายและภาษีที่หักนำส่ง") · ภาษีที่หัก 72.64 เป็นช่องหัก ณ ที่จ่าย ไม่ใช่ยอดรวม
        new ReplayPaper("wht-cert-50tawi-w53-positional", OcrPaperSamples.WhtCert50TawiW53Positional,
            EngineSubTotal: null, EngineVat: 0m, EngineTotal: 7264.00m, BaseAmount: 7264.00m),

        // ── ทีม OCR 9 (2026-10-16 · ฝ่ายค้าน 13a4d788 P0) — บรรทัดสินค้าที่ engine หลุดคอลัมน์ราคา/อ่านยอดเป็นราคา ห้ามกลายเป็น 1 × ยอด ──
        // (ก) น้ำดื่ม 12 ขวด 300 ไม่มีราคาต่อหน่วยบนแถว: ทีม 8 ⇒ 1 × 300 "(12 ขวด)" (สต็อกขาด 11 · ทุนเพี้ยน 12 เท่า) · 300 ÷ 12 = 25.00 ลงตัว ⇒ 12 × 25.00 (รุ่น 5 เดิม · แจ้ง)
        new ReplayPaper("goods-qty-price-column-lost",
            "ใบเสร็จรับเงิน\nน้ำดื่ม 600ml 12 ขวด 300.00\nรวมทั้งสิ้น 300.00",
            EngineSubTotal: null, EngineVat: 0m, EngineTotal: 300m,
            LineAmounts: new[] { 300.00m },
            Lines: new ReplayLine[] { new("น้ำดื่ม 600ml", 12m, null, 300.00m) }),
        // (ข) ดีเซล 30.512 ลิตร 990.09 ไม่มีราคา: ยอด ÷ จำนวน = 32.4492 ไม่ลงตัว · ลิตรเป็นสินค้า (ไม่ใช่มิเตอร์) ⇒ คง 30.512 × 32.45 − ส่วนลด 0.02 + แท็กบล็อก
        new ReplayPaper("goods-fractional-qty-no-price",
            "ใบเสร็จรับเงิน\nดีเซล 30.512 ลิตร 990.09\nรวมทั้งสิ้น 990.09",
            EngineSubTotal: null, EngineVat: 0m, EngineTotal: 990.09m,
            LineAmounts: new[] { 990.09m },
            Lines: new ReplayLine[] { new("ดีเซล", 30.512m, null, 990.09m) }),
        // (ค) หมูสับ 5 กก. 450 · engine อ่านยอดมาใส่ช่องราคา (เคส ก): ทีม 8 ⇒ 1 × 450 (กก. ถูกนับเป็นมิเตอร์) · 450 ÷ 5 = 90.00 ลงตัว + จำนวนพิมพ์คู่หน่วย ⇒ 5 × 90 (แจ้ง)
        new ReplayPaper("goods-price-read-as-amount",
            "ใบเสร็จรับเงิน\nหมูสับ 5 กก. 450.00\nรวมทั้งสิ้น 450.00",
            EngineSubTotal: null, EngineVat: 0m, EngineTotal: 450m,
            LineAmounts: new[] { 450.00m },
            Lines: new ReplayLine[] { new("หมูสับ", 5m, 450.00m, 450.00m) }),
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

            // 5. ส่วนลดท้ายบิลบนกระดาษ (OcrBillDiscount) + ยอดก่อน VAT หลังส่วนลด (OcrHeaderAmounts.NetSubTotal)
            var disc = OcrBillDiscount.Read(p.RawText, n.Total);
            result.Add(new(p.Name, "BillDiscount", disc.Amount?.ToString("0.00")));
            result.Add(new(p.Name, "NetSubTotal",
                OcrHeaderAmounts.NetSubTotal(n.SubTotal, n.Vat, n.Total, disc.Amount ?? 0m).ToString("0.00")));

            // 5b. รอบ 192 Total-first — ยอดรวมทั้งสิ้นที่ยึด (OcrTotalAnchor) + ส่วนลดอยู่ตรงไหน (OcrTotalDecomposer)
            //     ช่องใหม่ = แถวใหม่เท่านั้น · ช่องเดิมของทุกใบต้องไม่เปลี่ยน
            var anchor = OcrTotalAnchor.Find(p.RawText, n.Total);
            var anchoredTotal = anchor.Verdict is OcrTotalVerdict.Proven or OcrTotalVerdict.Confirmed
                ? anchor.Total : n.Total;
            result.Add(new(p.Name, "AnchorVerdict", anchor.Verdict.ToString()));
            result.Add(new(p.Name, "AnchorTotal", anchoredTotal?.ToString("0.00")));
            var shape = OcrTotalDecomposer.Decompose(p.RawText, n.SubTotal, n.Vat, anchoredTotal, disc.Amount ?? 0m);
            result.Add(new(p.Name, "DiscountPlacement", shape.Placement.ToString()));
            result.Add(new(p.Name, "DiscountToSpread", shape.DiscountToSpread.ToString("0.00")));
            result.Add(new(p.Name, "AnchoredNetSubTotal",
                OcrHeaderAmounts.NetSubTotal(n.SubTotal, n.Vat, anchoredTotal, shape.DiscountToSpread).ToString("0.00")));
            // 2026-10-16 (Makro 2/2 · หน้ารีวิว "ยอดก่อน VAT 11208"): ช่องยอดก่อน VAT ของหัวสแกนหลังขั้นยึดยอด (OcrTotalAnchor.Plan — ตัวเดียวกับ ApplyTotalFirst)
            var writePlan = OcrTotalAnchor.Plan(anchor, n.SubTotal, n.Vat,
                Accounting.Services.Implementations.Ocr.ThaiTextNormalizer.Normalize(p.RawText));
            result.Add(new(p.Name, "PlannedHeaderSubTotal", (writePlan.SubTotal ?? n.SubTotal)?.ToString("0.00")));

            // 6. อัตรา VAT รายบรรทัดจากสัญลักษณ์บนกระดาษ (OcrLineVatMarks) — เฉพาะใบที่มีรายการ
            if (p.LineAmounts is { } amounts)
            {
                var pick = OcrLineVatMarks.Assign(amounts, OcrLineVatMarks.Read(p.RawText), n.Vat ?? 0m);
                result.Add(new(p.Name, "LineVatRates", pick.Applied
                    ? string.Join(",", pick.Rates.Select(r => r?.ToString("0.##") ?? "-"))
                    : "(ไม่ใช้)"));

                // 7. รอบ 195 — ชั้น "ตัวเลขหัวใบพิสูจน์อัตราทั้งใบ" (OcrLineVatPlanner) บนยอดหลังกระจายส่วนลด
                //    ลำดับเดียวกับ BuildScanLinesAsync: สัญลักษณ์บนกระดาษ → กระทบยอด/กระจายส่วนลด → ชั้นนี้ (บรรทัดที่ยังว่าง)
                var netBase = OcrHeaderAmounts.NetSubTotal(n.SubTotal, n.Vat, anchoredTotal, shape.DiscountToSpread);
                var recon = OcrLineReconciler.Classify(amounts.Sum(), netBase, n.Vat ?? 0m, anchoredTotal ?? 0m, shape.DiscountToSpread);
                var nets = amounts.ToArray();
                if (recon.DiscountPercent > 0m && recon.TargetLineSum is decimal target && amounts.Sum() > 0m)
                {
                    decimal assigned = 0m;
                    for (var i = 0; i < nets.Length; i++)
                    {
                        nets[i] = i == nets.Length - 1 ? target - assigned
                            : Math.Round(target * amounts[i] / amounts.Sum(), 2, MidpointRounding.AwayFromZero);
                        assigned += nets[i];
                    }
                }
                var plan = OcrLineVatPlanner.PlanWholeInvoice(nets,
                    pick.Applied ? pick.Rates : new decimal?[nets.Length],
                    n.Vat ?? 0m, netBase, anchoredTotal ?? 0m, recon.PricesIncludeVat,
                    // ฝ่ายค้าน f7c29c2c P1-1: ตารางที่ตัดสินความหมายแล้ว (ตัวเดียวกับ service — OcrVatCodeMeaning.ResolvedTable)
                    OcrLineVatPlanner.PaperExemptAmount(OcrLineVatMarks.Read(p.RawText),
                        OcrVatCodeMeaning.ResolvedTable(Accounting.Services.Implementations.Ocr.ThaiTextNormalizer.Normalize(p.RawText), null).Table),
                    // รอบ 195 ฝ่ายค้าน R2-1: ส่งหลักฐานจริงจากข้อความกระดาษ (ห้าม hard-code true — replay ชุดกระดาษจริงต้องเห็นผลของด่าน)
                    vatPrintedOnPaper: OcrHeaderVatEvidence.Classify(p.RawText, null, n.Vat ?? 0m, null) == OcrHeaderVatSource.Labelled);
                result.Add(new(p.Name, "LineVatPlan", plan.Decided
                    ? plan.Verdict + ":" + string.Join(",", plan.Rates.Select(r => r?.ToString("0.##") ?? "-"))
                    : plan.Verdict.ToString()));
            }

            // 8. 2026-10-09 ratchet ระดับบรรทัด — บรรทัดเอกสารที่จะถูกสร้างจริง (ยอด/อัตรา/VAT/ส่วนลด/หัก ณ ที่จ่าย ต่อบรรทัด)
            //    เล่นซ้ำขั้น pure ของ BuildScanLinesAsync ทั้งสามสาขา (มีรายการ · ตารางกลุ่มภาษี · บรรทัดสรุป) — แถวใหม่ทั้งหมด
            var built = BuildLines(p, n.SubTotal, n.Vat, anchoredTotal, shape.DiscountToSpread, wht);
            result.Add(new(p.Name, "LineMode", built.Mode));
            result.Add(new(p.Name, "ReconCase", built.ReconCase));
            result.Add(new(p.Name, "PricesIncludeVat", built.PricesIncludeVat ? "true" : "false"));
            result.Add(new(p.Name, "Lines", built.Lines));
            result.Add(new(p.Name, "IntegrityGaps", built.Gaps));
            result.Add(new(p.Name, "RoundingAdjustment", built.RoundingAdjustment.ToString("0.00")));
            // 2026-10-09 ใบต้นฉบับ+สำเนา: จำนวนแถวที่ด่านแถวซ้ำตัด (ช่องใหม่ทุกใบ — ใบเดิมได้ 0)
            result.Add(new(p.Name, "DupRowsDropped", built.DuplicateRowsDropped.ToString()));
            // 2026-10-16 RE-20261000111: ตัวกันจำนวนระเบิด (OcrLineQuantityGuard) ต่อบรรทัด — การกระทำ|จำนวน|ราคา|ส่วนลดบรรทัด (ช่องใหม่เฉพาะใบที่มีรายการ)
            if (built.QuantityGuard is string qg) result.Add(new(p.Name, "LineQtyGuard", qg));
            // ทีม OCR 8: คำอธิบายที่ตัวกันเปลี่ยน ("ค่าไฟฟ้า … (4,272 หน่วย)") — แถวใหม่เฉพาะใบที่เปลี่ยน
            if (built.Descriptions is string ds) result.Add(new(p.Name, "LineDescriptions", ds));
            // 2026-10-16 คำตัดสินเจ้าของภาคต่อ: ผัง/โครงการของบรรทัดสรุปเมื่อตารางชนะ (ช่องใหม่เฉพาะใบที่ระบุผังรายบรรทัด)
            if (built.SummaryDims is string sd) result.Add(new(p.Name, "SummaryDims", sd));

            // 9. ทีม OCR 13 (ฝ่ายค้าน OCR 10 P2): ที่อยู่ผู้ขายจากป้าย "ที่อยู่" (ParseThaiDocument — ตัวอ่าน VendorAddressRegex ที่ทีม OCR 10 เปลี่ยนรูป) ·
            //    เดิมไม่อยู่ใน snapshot ⇒ การเปลี่ยนรูป regex ที่ให้ผลต่างจริง (fuzz 5,065/300k) มองไม่เห็น · แถวใหม่ทุกใบ
            result.Add(new(p.Name, "VendorAddress", Accounting.Services.Implementations.OcrService.ParseThaiDocument(
                Accounting.Services.Implementations.Ocr.ThaiTextNormalizer.Normalize(p.RawText)).VendorAddress));

            // 10. ทีม OCR 15 (ฝ่ายค้าน OCR 13 P1-3): ช่องคู่สัญญา §86/4 — ParseThaiDocument → SmartFieldExtractor.Enrich (ลำดับเดียวกับ ProcessScanAsync ชั้น pure) ·
            //     เดิมไม่อยู่ใน snapshot ทั้งที่ตัวอ่านที่ใช้ร่วมกันเปลี่ยนหมด (ThaiCompanyName · ป้ายผู้ซื้อบนบรรทัด · ผู้ออกบิลรัฐ · สาขา · ชื่อผูกเลขผู้ขาย) ⇒
            //     ชื่อสลับคอลัมน์ (P1-2) ผ่าน golden เงียบ ๆ · golden ตัวอักษรอยู่ที่ OcrReplayPartyGoldenTests · "" = ว่าง (เท่า null)
            var party = Accounting.Services.Implementations.OcrService.ParseThaiDocument(
                Accounting.Services.Implementations.Ocr.ThaiTextNormalizer.Normalize(p.RawText));
            Accounting.Services.Implementations.Ocr.SmartFieldExtractor.Enrich(party, p.RawText);
            static string? Blank(string? s) => string.IsNullOrEmpty(s) ? null : s;
            result.Add(new(p.Name, "VendorName", Blank(party.VendorName)));
            result.Add(new(p.Name, "VendorTaxId", Blank(party.VendorTaxId)));
            result.Add(new(p.Name, "VendorBranch", Blank(party.VendorBranchCode)));
            result.Add(new(p.Name, "BuyerName", Blank(party.BuyerName)));
            result.Add(new(p.Name, "BuyerTaxId", Blank(party.BuyerTaxId)));
            result.Add(new(p.Name, "BuyerBranch", Blank(party.BuyerBranchCode)));
        }
        return result;
    }

    /// <summary>
    /// **เล่นซ้ำขั้นสร้างบรรทัดเอกสารของ <c>OcrService.BuildScanLinesAsync</c> ด้วยตัวตัดสิน pure ชุดเดียวกัน** (ไม่มี DB)
    ///
    /// <para>ลำดับตรงกับ service: ยอดก่อนลดของบรรทัด (<see cref="OcrTotalDecomposer.LineGross"/>) → ฐานกระทบยอดหลังส่วนลด
    /// (<see cref="OcrHeaderAmounts.NetSubTotal"/>) → จำแนกเคส (<see cref="OcrLineReconciler.Classify"/>) → กระจายส่วนลด (เคส C/E) →
    /// อัตรา VAT: สัญลักษณ์บนกระดาษ (<see cref="OcrLineVatMarks.Assign"/>) → ตัวเลขหัวใบพิสูจน์ทั้งใบ (<see cref="OcrLineVatPlanner"/>) →
    /// เดาจากชื่อ (<see cref="ThaiVatTypeRule"/>) → เฉลี่ย VAT หัวใบ (<see cref="ThaiVatTypeRule.SpreadHeaderVat"/>) → ผลต่างปัดเศษของ
    /// ราคาต่อหน่วย (<see cref="DocumentRounding"/>) → ด่าน Σ (<see cref="OcrAmountIntegrity.Check"/>)</para>
    ///
    /// <para>สิ่งที่<b>ไม่</b>เล่นซ้ำ (ต้องมี DB/ข้อมูลสแกน): ผังบัญชี · PO · ประเภทเงินได้ ม.40 · ธง <c>PriceIncludesVat</c> ของบรรทัด e-Tax
    /// (กระดาษไม่มี ⇒ ส่ง false ทุกบรรทัด เหมือน engine ภาพ) · อัตราหัก ณ ที่จ่ายใช้ที่กระดาษพิมพ์ (<see cref="PaperWhtReader"/>) แทน
    /// <c>result.HasWht/WhtRate</c> ซึ่งในไปป์ไลน์จริงมาได้หลายทาง</para>
    ///
    /// <para>⚠️ ค่าที่ล็อกคือ "คำตอบวันนี้" — รวมถึงพฤติกรรมที่น่าสงสัย เพื่อให้การแก้ครั้งถัดไป<b>มองเห็น</b>ว่าใบไหนเปลี่ยน
    /// ไม่ใช่เพื่อรับรองว่าถูก (ตัวอย่างที่แก้แล้ว: บรรทัดไม่มีราคาต่อหน่วยในเคส C เคยได้ % ส่วนลด &gt; 0 คู่ <c>ส่วนลดบาท = 0</c> ⇒
    /// เปิดแก้แล้วบันทึกหักซ้ำ — ทีม D 2026-10-13 B5 ตอนนี้ 0% ผ่าน <see cref="OcrLineReconciler.LineDiscount"/>)</para>
    /// </summary>
    public static ReplayBuiltLines BuildLines(
        ReplayPaper p, decimal? headerSub, decimal? headerVat, decimal? anchoredTotal, decimal discountToSpread, PaperWht paperWht)
    {
        const MidpointRounding R = MidpointRounding.AwayFromZero;
        IReadOnlyList<ReplayLine>? printedRows = p.Lines
            ?? p.LineAmounts?.Select(a => new ReplayLine(null, null, null, a)).ToList();
        if (p.Lines != null && p.LineAmounts != null && !p.Lines.Select(l => l.Amount).SequenceEqual(p.LineAmounts))
            throw new InvalidOperationException($"{p.Name}: Lines กับ LineAmounts ยอดไม่ตรงกัน — กระดาษใบเดียวต้องมีตัวเลขชุดเดียว");

        // 2026-10-09 ตรงกับ SanitizeVatSplitArtifacts ข้อ 3b (ก่อนขั้นยุบ/กระทบยอดทุกขั้น): แถวตามที่ engine คืน (EngineLines — PDF ต้นฉบับ+สำเนา
        // ให้ชุดเดิมสองรอบ) เดินด่านแถวซ้ำด้วยยอดหัวใบ · ใบที่ไม่มี EngineLines = แถวที่พิมพ์ (ไม่มีอะไรซ้ำ ⇒ 0 · ช่องเดิมไม่เปลี่ยน)
        var lines = p.EngineLines ?? printedRows;
        // 2026-10-16 ตรงกับ SanitizeVatSplitArtifacts ข้อ 1b (ก่อนด่านแถวซ้ำ): ตัวกันจำนวนระเบิดถามแถวบนกระดาษ (OcrLineQuantityGuard — ตัวเดียวกับ service)
        // ส่วนลดที่พิมพ์ ⇒ ส่วนลดของบรรทัด · จำนวนที่พิมพ์ ⇒ คง · ไม่มีหลักฐาน ⇒ ยอด ÷ ราคา (เดิม) · ใบที่ส่วนลดจากกระดาษ + หัวใบพิสูจน์รวม VAT ⇒ ธงรวม VAT ทุกบรรทัด
        string? quantityGuard = null;
        string? descriptions = null;
        if (lines is { Count: > 0 })
        {
            var linePaper = OcrLineQuantityGuard.Read(p.RawText);
            // ทีม OCR 9/11: ชนิดผู้ขายสาธารณูปโภค = บริบทของหน่วยมิเตอร์ที่กำกวม (ตัวตัดสินเดียวกับ service — Helpers/OcrUtilityVendor)
            var utilitySeller = OcrUtilityVendor.KindOf(p.VendorTaxId, p.VendorNameFromEngine);
            var guarded = new List<ReplayLine>(lines.Count);
            var guardText = new List<string>(lines.Count);
            var paperDiscountRead = false;
            var descriptionChanged = false;
            foreach (var l in lines)
            {
                var d = OcrLineQuantityGuard.Decide(l.Quantity, l.UnitPrice, l.Amount, l.LineDiscount, linePaper, l.Description, null, utilitySeller);
                // ทีม OCR 8 (2026-10-16): เล่นซ้ำทุกการกระทำ รวม FillUnitPrice (เดิมข้าม — ช่องว่างที่จดไว้ว่าเส้น "ไม่มีราคา + จำนวน > 0" ไม่ตรง service) ·
                // ใบในชุดก่อนรอบนี้ไม่มีบรรทัด "มีจำนวน ไม่มีราคา" (LineAmounts-only = จำนวน null ⇒ Keep) ⇒ golden เดิมไม่ขยับ
                descriptionChanged |= d.Description is not null;
                var g = l with
                {
                    Description = d.Description ?? l.Description,
                    Quantity = d.Quantity ?? l.Quantity,
                    UnitPrice = d.UnitPrice ?? l.UnitPrice,
                    LineDiscount = d.LineDiscount ?? l.LineDiscount,
                    // ฝ่ายค้าน 658e3705 P1-1: แถวหัก ณ ที่จ่าย/มัดจำ ⇒ ยอดบรรทัด = จำนวน × ราคาก่อนหัก (ตรงกับ service)
                    Amount = d.Amount ?? l.Amount,
                };
                // ทีม OCR 9: เศษปัดของ RoundedUnitPrice ไม่ใช่ส่วนลดจากกระดาษ (ตรงกับ service)
                paperDiscountRead |= d.LineDiscount.HasValue && d.Action != OcrLineQuantityAction.RoundedUnitPrice;
                guarded.Add(g);
                // ช่องท้าย: block = แท็กบล็อก [LINE-QTY] · note = หมายเหตุแจ้ง [LINE-QTY-NOTE] · - = ไม่มีหมายเหตุ (ฝ่ายค้าน 658e3705 — อัตราแท็กวัดได้จาก golden)
                guardText.Add($"{d.Action}|{g.Quantity?.ToString("0.###") ?? "-"}|{g.UnitPrice?.ToString("0.00##") ?? "-"}|{g.LineDiscount?.ToString("0.00") ?? "-"}"
                    + $"|{(d.Note is null ? "-" : d.Blocking ? "block" : "note")}");
            }
            if (OcrLineQuantityGuard.PaperDiscountPricesIncludeVat(paperDiscountRead, guarded.Sum(l => l.Amount),
                    headerSub, headerVat, anchoredTotal, discountToSpread))
                guarded = guarded.Select(l => l.Amount != 0m ? l with { PriceIncludesVat = true } : l).ToList();
            lines = guarded;
            quantityGuard = string.Join(";", guardText);
            if (descriptionChanged) descriptions = string.Join(";", guarded.Select(l => l.Description ?? "-"));
        }
        var duplicateRowsDropped = 0;
        if (lines is { Count: >= 2 })
        {
            // ส่วนลดท้ายบิลที่ตัวสร้างบรรทัดยอมให้กระจาย (PostInvoice = 0 แล้วจาก OcrTotalDecomposer) — ตรงกับ service ที่ส่ง data.DiscountAmount
            var dup = OcrDuplicateLineGuard.Decide(
                lines.Select(l => new OcrCandidateRow(l.Description, l.Quantity, l.UnitPrice, l.Amount, l.LineDiscount)).ToList(),
                headerSub, headerVat, anchoredTotal, discountToSpread);
            if (dup.Deduped)
            {
                var kept = lines;
                lines = dup.KeepIndexes.Select(i => kept[i]).ToList();
                duplicateRowsDropped = dup.DroppedCount;
            }
        }

        var hdrSub = headerSub ?? 0m;
        var hdrVat = headerVat ?? 0m;
        var hdrTotal = anchoredTotal ?? 0m;
        var hdrDiscRaw = discountToSpread;
        // CreateDocumentFromScanCoreAsync: whtBase = ยอดรวม − VAT · อัตราจาก HasWht/WhtRate (ที่นี่ = ที่กระดาษพิมพ์)
        var whtRate = paperWht.Amount.HasValue ? (paperWht.RatePercent ?? 0m) : 0m;
        var whtBase = Math.Max(0m, hdrTotal - hdrVat);
        var headerWht = whtRate > 0m ? Math.Round(whtBase * whtRate / 100m, 2, R) : 0m;
        var headerSubTotal = OcrHeaderAmounts.NetSubTotal(headerSub, headerVat, anchoredTotal, hdrDiscRaw);
        var paperVatSplit = OcrLineVatMarks.Read(p.RawText);

        static string Fmt(decimal net, decimal rate, decimal vat, decimal unit, decimal discPct, decimal discAmt, decimal wht)
            => $"{net:0.00}|{rate:0.##}|{vat:0.00}|{unit:0.00}|{discPct:0.##}|{discAmt:0.00}|{wht:0.00}";
        static string GapText(IEnumerable<string> gaps)
        {
            var list = gaps.ToList();
            return list.Count == 0 ? "ok" : string.Join(",", list);
        }
        // 2026-10-16 ตรงกับ service (ท้าย BuildScanLinesAsync): ยอดเอกสารที่สร้างเกินยอดรวมที่กระดาษยืนยัน ⇒ [Σ-GAP] (OcrTotalAnchor.DocumentOverstatement)
        var normalizedText = Accounting.Services.Implementations.Ocr.ThaiTextNormalizer.Normalize(p.RawText);
        IEnumerable<string> WithOverstatement(IEnumerable<string> gaps, decimal builtTotal)
            => OcrTotalAnchor.DocumentOverstatement(normalizedText, anchoredTotal, builtTotal) is null
                ? gaps : gaps.Append("Overstated");

        // ตรงกับ service: ตารางรหัส ภ.พ. อ่านครั้งเดียว · ตัวตัดสิน "รายการ หรือ ตาราง" (OcrVatCodeLines.Decide — คำตัดสินเจ้าของ 2026-10-16 ข้อ 1/3)
        // ตรงกับ service (DECISIONS ข้อ 153 ข้อ 2): รหัสที่กระดาษไม่บอกความหมาย ⇒ ตัวเลขของแถว / ค่าตั้งต้น — ชุดกระดาษไม่มีคำตอบ AI ที่ persist (null)
        var codeTable = OcrVatCodeMeaning.ResolvedTable(normalizedText, null).Table;
        IReadOnlyList<OcrVatGroup>? tableLinesInsteadOfItems = null;
        string? summaryAccount = null, summaryProject = null;
        var mixedDimensions = false;
        if (lines is { Count: > 0 })
        {
            var n0 = lines.Count;
            var printed0 = lines.Select(l => l.Amount).ToList();
            // ตรงกับ service: บรรทัดที่มีส่วนลดรายบรรทัด (e-Tax · หรือที่พิมพ์บนแถวของกระดาษ — OcrLineQuantityGuard ข้างบน) ใช้ยอดหลังลด (เรียกตัวเดียวกัน ไม่ประกอบเอง)
            var lineOwnDisc0 = lines.Select(l => OcrEtaxLineNormalizer.ProvenLineDiscount(l.Quantity, l.UnitPrice, l.Amount, l.LineDiscount)).ToList();
            var lineGross0 = lines.Select((l, gi) => lineOwnDisc0[gi] > 0m ? l.Amount
                : OcrTotalDecomposer.LineGross(l.Quantity, l.UnitPrice, l.Amount)).ToList();
            // ตรงกับ service: สัญลักษณ์ท้ายบรรทัด (ก่อนตัวกระทบยอด) → ตัวตัดสินตารางรหัส ภ.พ. (บรรทัดกระดาษไม่มีธงราคารวม VAT ⇒ preset = false)
            var rates0 = new decimal?[n0];
            var markPick = OcrLineVatMarks.Assign(printed0, paperVatSplit, hdrVat);
            if (markPick.Applied)
                for (var i = 0; i < n0; i++) rates0[i] ??= markPick.Rates[i];
            // ตรงกับ service: ผัง/โครงการรายบรรทัด (null = ผังหัวใบ · ตัวเดียวกันทุกบรรทัด) + ยอดที่กระดาษพิสูจน์ (OcrTotalAnchor.Find) เข้าด่าน "ตารางลงยอด"
            var paperAnchor = OcrTotalAnchor.Find(normalizedText, hdrTotal);
            var paperProven = paperAnchor.Verdict is OcrTotalVerdict.Proven or OcrTotalVerdict.Confirmed;
            var decision = OcrVatCodeLines.Decide(
                lineGross0.Select((g, i) => new OcrVatCodeLineInput(g, rates0[i], lines[i].Description,
                    AccountId: DimensionId(lines[i].Account ?? "ผังหัวใบ"), ProjectId: lines[i].Project is string pj ? DimensionId(pj) : null,
                    AccountLabel: lines[i].Account ?? "ผังหัวใบ", ProjectLabel: lines[i].Project)).ToList(),
                normalizedText, codeTable, hdrSub, hdrVat, hdrTotal, hdrDiscRaw,
                pricesIncludeVatPreset: OcrEtaxLineNormalizer.AllLinesPriceIncludeVat(lines.Select(l => (l.Amount, l.PriceIncludesVat)).ToList()),
                tableLinesAllowed: headerWht == 0m,
                paperTotal: paperProven ? paperAnchor.Total : null, paperVat: paperProven ? paperAnchor.PrintedVat : null);
            if (decision.Source == OcrVatCodeLineSource.TableSummary)
            {
                tableLinesInsteadOfItems = decision.TableLines;
                summaryAccount = decision.SummaryAccountId is Guid sa
                    ? lines.Select(l => l.Account ?? "ผังหัวใบ").First(a => DimensionId(a) == sa) : null;
                summaryProject = decision.SummaryProjectId is Guid sp ? lines.Select(l => l.Project).First(x => x != null && DimensionId(x!) == sp) : null;
                mixedDimensions = decision.MixedDimensionsNote is not null;
            }
            else
            {
            var keep = decision.Keep;
            lines = keep.Select(k => lines[k]).ToList();
            var n = lines.Count;
            var printed = keep.Select(k => printed0[k]).ToList();
            var lineGross = keep.Select(k => lineGross0[k]).ToList();
            var lineOwnDisc = keep.Select(k => lineOwnDisc0[k]).ToList();
            var grossSum = lineGross.Sum();
            hdrDiscRaw = decision.BillDiscount;
            var netSubForRecon = decision.NetSubForRecon;
            var recon = decision.Recon;
            var docDiscountPercent = recon.DiscountPercent;
            // ตรงกับ service: ทุกบรรทัดติดธงรวม VAT (e-Tax · หรือส่วนลดจากกระดาษในใบรวม VAT) ⇒ เอกสารราคารวม VAT เสมอ
            var pricesIncludeVat = decision.PricesIncludeVat
                || OcrEtaxLineNormalizer.AllLinesPriceIncludeVat(lines.Select(l => (l.Amount, l.PriceIncludesVat)).ToList());
            var rates = keep.Select(k => rates0[k]).ToArray();
            var codePlan = decision.Plan;
            if (codePlan.Applied)
                for (var i = 0; i < n; i++)
                    if (codePlan.Rates[i] is decimal cr && (rates[i] is null || codePlan.ReplaceEngineRate[i])) rates[i] = cr;

            var amounts = printed.ToArray();
            if (codePlan.AmountsAfterDiscount is { } codeAmounts)
                amounts = codeAmounts.ToArray();
            else if (docDiscountPercent > 0m && recon.TargetLineSum is decimal targetSum && grossSum > 0m)
            {
                decimal assigned = 0m;
                for (var i = 0; i < n; i++)
                {
                    var share = i == n - 1 ? targetSum - assigned
                        : Math.Round(targetSum * lineGross[i] / grossSum, 2, R);
                    assigned += share;
                    amounts[i] = share;
                }
            }

            // อัตรา VAT รายบรรทัด — ลำดับเดียวกับ service (สัญลักษณ์ท้ายบรรทัด + รหัส ภ.พ. ตั้งไว้ข้างบนแล้ว)
            // ตรงกับ service: ข้อความดิบ + ข้อความ normalize · engine/หมายเหตุของสแกนไม่มีในชุดกระดาษ (null) · ยอดรวมที่ยึด
            var vatPrinted = OcrHeaderVatEvidence.Classify(p.RawText,
                Accounting.Services.Implementations.Ocr.ThaiTextNormalizer.Normalize(p.RawText), hdrVat, null, null, anchoredTotal)
                == OcrHeaderVatSource.Labelled;
            var plan = OcrLineVatPlanner.PlanWholeInvoice(amounts, rates, hdrVat, netSubForRecon, hdrTotal, pricesIncludeVat,
                OcrLineVatPlanner.PaperExemptAmount(paperVatSplit, codeTable), vatPrinted);
            if (plan.Decided)
                for (var i = 0; i < n; i++) rates[i] ??= plan.Rates[i];
            var standardVatRate = hdrVat > 0m ? 7m : 0m;
            for (var i = 0; i < n; i++)
                rates[i] ??= hdrVat > 0m
                    ? ThaiVatTypeRule.ToVatRate(ThaiVatTypeRule.Suggest(lines[i].Description, null, p.VendorTaxId, p.VendorNameFromEngine), standardVatRate)
                    : 0m;

            // บรรทัดจากภาพ/ข้อความไม่มีธง PriceIncludesVat (เว้นใบส่วนลดจากกระดาษที่พิสูจน์รวม VAT) ⇒ InclusiveLineVats คืน null ⇒ เฉลี่ยหัวใบตามเดิม
            var spreadVat = (pricesIncludeVat
                    ? OcrEtaxLineNormalizer.InclusiveLineVats(
                        amounts.Select((a, i) => (a, rates[i] ?? 0m, lines[i].PriceIncludesVat)).ToList(), hdrVat)
                    : null)
                ?? ThaiVatTypeRule.SpreadHeaderVat(amounts.Select((a, i) => (a, rates[i] ?? 0m)).ToList(), hdrVat);

            var planned = new List<OcrPlannedLine>(n);
            for (var i = 0; i < n; i++)
                planned.Add(new OcrPlannedLine(
                    pricesIncludeVat ? Math.Round(amounts[i] - spreadVat[i], 2, R) : amounts[i],
                    rates[i] ?? standardVatRate, spreadVat[i]));
            var check = OcrAmountIntegrity.Check(planned, hdrVat, hdrTotal, paperVatSplit.TaxableAmount, paperVatSplit.NonTaxableAmount, codeTable);
            var gaps = new List<string>();
            if (recon.UnreconciledGap != 0m) gaps.Add("Σ-GAP:" + recon.Case);
            foreach (var problem in check.Problems)
            {
                if (recon.UnreconciledGap != 0m
                    && problem.Kind is OcrAmountIntegrityKind.TotalMismatch or OcrAmountIntegrityKind.VatRateMismatch)
                    continue;
                gaps.Add(problem.Kind.ToString());
            }
            // ตรงกับ service: ด่านผลสุดท้ายของตารางรหัส ภ.พ. (เฉพาะเมื่อตัวกระทบยอดไม่ได้รายงานช่องว่าง)
            if (recon.UnreconciledGap == 0m && OcrVatCodeLines.TableMismatch(planned, codeTable) is not null)
                gaps.Add("VatCodeTable");

            // ผลต่างปัดเศษของราคาต่อหน่วย — เฉพาะบรรทัดที่มี VAT (ฝ่ายค้าน P5 รอบ 193) · ยอดก่อนลดบรรทัด = ยอดหลังลด + ส่วนลดของบรรทัด (ตรงกับ service)
            var (shifts, _) = DocumentRounding.CapShifts(lines
                .Select((l, i) => rates[i] is decimal r && r <= 0m ? 0m
                    : DocumentRounding.FromPrintedLine(l.Quantity, l.UnitPrice, lineGross[i] + lineOwnDisc[i]).Shift)
                .ToList());

            var lineAmountSum = amounts.Sum();
            decimal whtAssigned = 0m, roundingShift = 0m, builtItemsTotal = 0m;
            var rows = new List<string>(n);
            for (var i = 0; i < n; i++)
            {
                var amount = amounts[i];
                var lineVat = spreadVat[i];
                decimal lineWht = 0m;
                if (headerWht > 0m && lineAmountSum > 0m)
                {
                    lineWht = i == n - 1 ? Math.Round(headerWht - whtAssigned, 2, R)
                        : Math.Round(headerWht * amount / lineAmountSum, 2, R);
                    whtAssigned += lineWht;
                }
                roundingShift += shifts[i];
                // ตรงกับ service: ตัวตัดสินตัวเดียว (ไม่มีราคาต่อหน่วย ⇒ 0% — ทีม D 2026-10-13 B5 · จำนวน < 1 ไม่มีราคา ⇒ ราคาจากตัวตัดสิน — ทีม E)
                var (discPct, discAmt, decidedUnitPrice) = OcrLineReconciler.LineDiscount(
                    codePlan.DiscountPercents?[i] ?? docDiscountPercent, lineGross[i], lines[i].UnitPrice.HasValue, amount, lines[i].Quantity);
                var unitPrice = decidedUnitPrice ?? lines[i].UnitPrice ?? amount;
                var net = (pricesIncludeVat ? Math.Round(amount - lineVat, 2, R) : amount) + shifts[i];
                builtItemsTotal += net + lineVat;
                // ตรงกับ service: DiscountAmount = ส่วนลดจากตัวกระจาย + ส่วนลดรายบรรทัดของเอกสาร/กระดาษ
                rows.Add(Fmt(net, rates[i] ?? standardVatRate, lineVat, unitPrice, discPct, discAmt + lineOwnDisc[i], lineWht));
            }
            return new ReplayBuiltLines("Items", recon.Case.ToString(), pricesIncludeVat,
                string.Join(";", rows), GapText(WithOverstatement(gaps, builtItemsTotal - roundingShift)), -roundingShift, duplicateRowsDropped, quantityGuard, descriptions);
            }
        }

        // ไม่มีรายการ — ตารางสรุปตามกลุ่มภาษีบนกระดาษ (Makro 3/3) ก่อน แล้วค่อยบรรทัดสรุปใบเดียว
        // 2026-10-16 ข้อ 1: รายการกระทบตารางไม่ได้ ⇒ ตารางชนะ (สาขาเดียวกัน — ตรงกับ service)
        var groups = tableLinesInsteadOfItems ?? OcrTotalDecomposer.SummaryGroupLines(
            OcrTotalDecomposer.Decompose(p.RawText, headerSub, headerVat, anchoredTotal, hdrDiscRaw) with { Groups = codeTable },
            anchoredTotal, headerVat, headerSubTotal);
        if (groups.Count > 0 && headerWht == 0m)
        {
            var planned = new List<OcrPlannedLine>(groups.Count);
            var rows = new List<string>(groups.Count);
            foreach (var g in groups)
            {
                var rate = g.Kind switch
                {
                    OcrVatGroupKind.Standard7 => 7m,
                    OcrVatGroupKind.ZeroRated => 0m,
                    _ => ThaiVatTypeRule.ExemptRate,
                };
                planned.Add(new OcrPlannedLine(g.Net, rate, g.Vat));
                rows.Add(Fmt(g.Net, rate, g.Vat, g.Net, 0m, 0m, 0m));
            }
            var check = OcrAmountIntegrity.Check(planned, hdrVat, hdrTotal, paperVatSplit.TaxableAmount, paperVatSplit.NonTaxableAmount, codeTable);
            // ตารางชนะแทนรายการ: ผัง/โครงการที่บรรทัดสรุปแบก (+ ธงหมายเหตุรายการเดิมหลายผัง/โครงการ) — เฉพาะใบที่มีรายการผัง/โครงการรายบรรทัดในชุด (ช่องเดิมไม่เปลี่ยน)
            var dims = tableLinesInsteadOfItems is not null && (p.Lines ?? p.EngineLines ?? Array.Empty<ReplayLine>()).Any(l => l.Account != null || l.Project != null)
                ? $"{summaryAccount ?? "-"}|{summaryProject ?? "-"}{(mixedDimensions ? "|mixed" : "")}" : null;
            return new ReplayBuiltLines(tableLinesInsteadOfItems is null ? "VatGroups" : "VatGroupsInsteadOfItems", null, false, string.Join(";", rows),
                GapText(WithOverstatement(check.Problems.Select(x => x.Kind.ToString()), groups.Sum(g => g.Net + g.Vat))), 0m, duplicateRowsDropped, quantityGuard, descriptions, dims);
        }

        var summaryRate = hdrVat > 0m ? 7m : 0m;
        var summaryCheck = OcrAmountIntegrity.Check(
            new[] { new OcrPlannedLine(headerSubTotal, summaryRate, hdrVat) }, hdrVat, hdrTotal,
            paperVatSplit.TaxableAmount, paperVatSplit.NonTaxableAmount, codeTable);
        return new ReplayBuiltLines("Summary", null, false,
            Fmt(headerSubTotal, summaryRate, hdrVat, headerSubTotal, 0m, 0m, headerWht),
            GapText(WithOverstatement(summaryCheck.Problems.Select(x => x.Kind.ToString()), headerSubTotal + hdrVat)), 0m);
    }

    /// <summary>Guid คงที่ต่อชื่อผัง/โครงการ (ชุดกระดาษไม่มี DB — ชื่อเดียวกัน = ค่าเดียวกัน)</summary>
    private static Guid DimensionId(string name)
        => new(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(name)));

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
