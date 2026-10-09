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
public sealed record ReplayLine(string? Description, decimal? Quantity, decimal? UnitPrice, decimal Amount);

/// <summary>ผลของขั้นสร้างบรรทัดเอกสาร (<see cref="OcrReplayHarness.BuildLines"/>) — ทุกช่องเป็นข้อความอ่านออก เพื่อล็อกเป็น golden</summary>
/// <param name="Mode"><c>Items</c> (มีรายการ) · <c>VatGroups</c> (ไม่มีรายการแต่กระดาษพิมพ์ตารางสรุปตามกลุ่มภาษี) · <c>Summary</c> (บรรทัดสรุปใบเดียว)</param>
/// <param name="ReconCase">เคสของ <see cref="OcrLineReconciler"/> (เฉพาะ Items · อื่น ๆ = null)</param>
/// <param name="Lines">หนึ่งบรรทัดต่อรายการ คั่นด้วย <c>;</c> — รูป <c>ยอดก่อนVAT|อัตรา|VAT|ราคาต่อหน่วย|ส่วนลด%|ส่วนลดบาท|หัก ณ ที่จ่าย</c></param>
/// <param name="Gaps">ช่องว่างที่ตัวสร้างจะเขียนเป็น <c>[Σ-GAP]</c> (เคสกระทบยอด + ชนิดปัญหาของ <see cref="OcrAmountIntegrity"/>) · <c>ok</c> = ไม่มี</param>
/// <param name="RoundingAdjustment">ผลต่างปัดเศษหัวเอกสาร (= −Σ shift ของบรรทัด)</param>
/// <param name="DuplicateRowsDropped">จำนวนแถวที่ด่านแถวซ้ำ (<see cref="OcrDuplicateLineGuard"/>) ตัดออกก่อนทุกขั้น — 0 = ไม่ตัด
/// (2026-10-09 ใบต้นฉบับ+สำเนา · <see cref="ReplayPaper.EngineLines"/>)</param>
public sealed record ReplayBuiltLines(
    string Mode, string? ReconCase, bool PricesIncludeVat, string Lines, string Gaps, decimal RoundingAdjustment,
    int DuplicateRowsDropped = 0);

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
                    OcrLineVatPlanner.PaperExemptAmount(OcrLineVatMarks.Read(p.RawText), OcrLineVatMarks.ReadGroups(p.RawText)),
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
    /// <para>⚠️ ค่าที่ล็อกคือ "คำตอบวันนี้" — รวมถึงพฤติกรรมที่น่าสงสัย (เช่น บรรทัดไม่มีราคาต่อหน่วยในเคส C ได้ <c>ส่วนลดบาท = 0</c>
    /// ทั้งที่ % ส่วนลด &gt; 0) เพื่อให้การแก้ครั้งถัดไป<b>มองเห็น</b>ว่าใบไหนเปลี่ยน ไม่ใช่เพื่อรับรองว่าถูก</para>
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
        var duplicateRowsDropped = 0;
        if (lines is { Count: >= 2 })
        {
            // ส่วนลดท้ายบิลที่ตัวสร้างบรรทัดยอมให้กระจาย (PostInvoice = 0 แล้วจาก OcrTotalDecomposer) — ตรงกับ service ที่ส่ง data.DiscountAmount
            var dup = OcrDuplicateLineGuard.Decide(
                lines.Select(l => new OcrCandidateRow(l.Description, l.Quantity, l.UnitPrice, l.Amount)).ToList(),
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

        if (lines is { Count: > 0 })
        {
            var n = lines.Count;
            var printed = lines.Select(l => l.Amount).ToList();
            // ตรงกับ service: บรรทัดที่เอกสารประกาศส่วนลดรายบรรทัดเอง (e-Tax) ใช้ยอดหลังลด — กระดาษไม่มีช่องนี้ ⇒ 0 ทุกบรรทัด (เรียกตัวเดียวกัน ไม่ประกอบเอง)
            var lineOwnDisc = lines.Select(l => OcrEtaxLineNormalizer.ProvenLineDiscount(l.Quantity, l.UnitPrice, l.Amount, null)).ToList();
            var lineGross = lines.Select((l, gi) => lineOwnDisc[gi] > 0m ? l.Amount
                : OcrTotalDecomposer.LineGross(l.Quantity, l.UnitPrice, l.Amount)).ToList();
            var grossSum = lineGross.Sum();

            var netSubForRecon = hdrSub;
            if (hdrDiscRaw > 0m && hdrTotal > 0m)
                netSubForRecon = OcrHeaderAmounts.NetSubTotal(hdrSub, hdrVat, hdrTotal, hdrDiscRaw);
            else if (hdrSub <= 0m && hdrTotal > 0m)
                netSubForRecon = Math.Max(0m, hdrTotal - hdrVat);

            var recon = OcrLineReconciler.Classify(grossSum, netSubForRecon, hdrVat, hdrTotal, hdrDiscRaw);
            var docDiscountPercent = recon.DiscountPercent;
            var pricesIncludeVat = recon.PricesIncludeVat;
            // ตรงกับ service: บรรทัด e-Tax ที่ติดธงราคารวม VAT ทั้งใบ ⇒ เอกสารราคารวม VAT — บรรทัดกระดาษไม่มีธง ⇒ false เสมอ
            if (OcrEtaxLineNormalizer.AllLinesPriceIncludeVat(lines.Select(l => (l.Amount, false)).ToList()))
                pricesIncludeVat = true;

            var amounts = printed.ToArray();
            if (docDiscountPercent > 0m && recon.TargetLineSum is decimal targetSum && grossSum > 0m)
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

            // อัตรา VAT รายบรรทัด — ลำดับเดียวกับ service
            var rates = new decimal?[n];
            var markPick = OcrLineVatMarks.Assign(printed, paperVatSplit, hdrVat);
            if (markPick.Applied)
                for (var i = 0; i < n; i++) rates[i] ??= markPick.Rates[i];
            // ตรงกับ service: ข้อความดิบ + ข้อความ normalize · engine/หมายเหตุของสแกนไม่มีในชุดกระดาษ (null) · ยอดรวมที่ยึด
            var vatPrinted = OcrHeaderVatEvidence.Classify(p.RawText,
                Accounting.Services.Implementations.Ocr.ThaiTextNormalizer.Normalize(p.RawText), hdrVat, null, null, anchoredTotal)
                == OcrHeaderVatSource.Labelled;
            var plan = OcrLineVatPlanner.PlanWholeInvoice(amounts, rates, hdrVat, netSubForRecon, hdrTotal, pricesIncludeVat,
                OcrLineVatPlanner.PaperExemptAmount(paperVatSplit, OcrLineVatMarks.ReadGroups(p.RawText)), vatPrinted);
            if (plan.Decided)
                for (var i = 0; i < n; i++) rates[i] ??= plan.Rates[i];
            var standardVatRate = hdrVat > 0m ? 7m : 0m;
            for (var i = 0; i < n; i++)
                rates[i] ??= hdrVat > 0m
                    ? ThaiVatTypeRule.ToVatRate(ThaiVatTypeRule.Suggest(lines[i].Description, null, p.VendorTaxId), standardVatRate)
                    : 0m;

            // บรรทัดจากภาพ/ข้อความไม่มีธง PriceIncludesVat (เป็นของ e-Tax XML) ⇒ InclusiveLineVats คืน null ⇒ เฉลี่ยหัวใบตามเดิม
            var spreadVat = (pricesIncludeVat
                    ? OcrEtaxLineNormalizer.InclusiveLineVats(
                        amounts.Select((a, i) => (a, rates[i] ?? 0m, false)).ToList(), hdrVat)
                    : null)
                ?? ThaiVatTypeRule.SpreadHeaderVat(amounts.Select((a, i) => (a, rates[i] ?? 0m)).ToList(), hdrVat);

            var planned = new List<OcrPlannedLine>(n);
            for (var i = 0; i < n; i++)
                planned.Add(new OcrPlannedLine(
                    pricesIncludeVat ? Math.Round(amounts[i] - spreadVat[i], 2, R) : amounts[i],
                    rates[i] ?? standardVatRate, spreadVat[i]));
            var check = OcrAmountIntegrity.Check(planned, hdrVat, hdrTotal, paperVatSplit.TaxableAmount, paperVatSplit.NonTaxableAmount);
            var gaps = new List<string>();
            if (recon.UnreconciledGap != 0m) gaps.Add("Σ-GAP:" + recon.Case);
            foreach (var problem in check.Problems)
            {
                if (recon.UnreconciledGap != 0m
                    && problem.Kind is OcrAmountIntegrityKind.TotalMismatch or OcrAmountIntegrityKind.VatRateMismatch)
                    continue;
                gaps.Add(problem.Kind.ToString());
            }

            // ผลต่างปัดเศษของราคาต่อหน่วย — เฉพาะบรรทัดที่มี VAT (ฝ่ายค้าน P5 รอบ 193) · บรรทัดกระดาษไม่มีส่วนลดรายบรรทัดของเอกสาร (lineOwnDisc = 0)
            var (shifts, _) = DocumentRounding.CapShifts(lines
                .Select((l, i) => rates[i] is decimal r && r <= 0m ? 0m
                    : DocumentRounding.FromPrintedLine(l.Quantity, l.UnitPrice, lineGross[i]).Shift)
                .ToList());

            var lineAmountSum = amounts.Sum();
            decimal whtAssigned = 0m, roundingShift = 0m;
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
                var unitPrice = lines[i].UnitPrice ?? amount;
                var discPct = OcrLineReconciler.LineDiscountPercent(docDiscountPercent, lineGross[i]);
                var discAmt = docDiscountPercent > 0m
                    ? Math.Round((lines[i].UnitPrice.HasValue ? lineGross[i] : amount * (lines[i].Quantity ?? 1m)) - amount, 2, R)
                    : 0m;
                var net = (pricesIncludeVat ? Math.Round(amount - lineVat, 2, R) : amount) + shifts[i];
                rows.Add(Fmt(net, rates[i] ?? standardVatRate, lineVat, unitPrice, discPct, discAmt, lineWht));
            }
            return new ReplayBuiltLines("Items", recon.Case.ToString(), pricesIncludeVat,
                string.Join(";", rows), GapText(gaps), -roundingShift, duplicateRowsDropped);
        }

        // ไม่มีรายการ — ตารางสรุปตามกลุ่มภาษีบนกระดาษ (Makro 3/3) ก่อน แล้วค่อยบรรทัดสรุปใบเดียว
        var groups = OcrTotalDecomposer.SummaryGroupLines(
            OcrTotalDecomposer.Decompose(p.RawText, headerSub, headerVat, anchoredTotal, hdrDiscRaw),
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
            var check = OcrAmountIntegrity.Check(planned, hdrVat, hdrTotal, paperVatSplit.TaxableAmount, paperVatSplit.NonTaxableAmount);
            return new ReplayBuiltLines("VatGroups", null, false, string.Join(";", rows),
                GapText(check.Problems.Select(x => x.Kind.ToString())), 0m);
        }

        var summaryRate = hdrVat > 0m ? 7m : 0m;
        var summaryCheck = OcrAmountIntegrity.Check(
            new[] { new OcrPlannedLine(headerSubTotal, summaryRate, hdrVat) }, hdrVat, hdrTotal,
            paperVatSplit.TaxableAmount, paperVatSplit.NonTaxableAmount);
        return new ReplayBuiltLines("Summary", null, false,
            Fmt(headerSubTotal, summaryRate, hdrVat, headerSubTotal, 0m, 0m, headerWht),
            GapText(summaryCheck.Problems.Select(x => x.Kind.ToString())), 0m);
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
