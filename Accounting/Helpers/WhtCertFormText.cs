using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ข้อความทุกคำบนแบบ "หนังสือรับรองการหักภาษี ณ ที่จ่าย ตามมาตรา 50 ทวิ" — ที่เดียวของ renderer ฝั่ง C# ทั้งสองตัว**
/// (HTML <c>PdfGenerationService.BuildWithholdingTaxCertHtml</c> + QuestPDF <c>PdfGenerationService.WhtCert.cs</c>)
///
/// <para>ที่มา (2026-10-12 · เจ้าของ: "หน้าตา และ ข้อความบนเอกสารต้องเหมือนเอกสาร … ตัวหนังสือและรูปแบบทำให้ตรง"): ข้อความเดิมพิมพ์ซ้ำ
/// ในสอง renderer และ QuestPDF เพี้ยนจาก HTML (หมายเหตุข้อ 3 ขาด "นอกเหนือจาก 1. และ 2." · (2.2) ตัดท้าย "เพื่อเสียภาษีเงินได้นิติบุคคล" ·
/// (2.3) "ก่อนรอบบัญชี" แทน "ก่อนรอบระยะเวลาบัญชี"). ค่าทุกตัวที่นี่ <b>ยกมาจาก HTML renderer คำต่อคำ</b> (หน้าตาที่เจ้าของยอมรับ —
/// HTML ต้องออกมาเหมือนเดิมทุกไบต์ ยกเว้นจุดแก้ที่ระบุใน CHANGELOG) · การแบ่งบรรทัดของข้อความยาว (4(ข) (1) (2.2) (2.3)) เก็บเป็นคู่
/// "บรรทัดแรก/บรรทัดต่อ" ให้ทั้งสอง renderer ตัดที่เดียวกัน</para>
///
/// <para>หน้า "พิมพ์ / PDF" ใน <c>wwwroot/pages/wht.html</c> ยังประกอบฟอร์มเองใน JS (renderer ตัวที่สาม — ตัวที่ผู้ใช้พิมพ์จริง) ·
/// ข้อความในนั้นต้องตรงกับไฟล์นี้ (<c>WhtCertFormTextTests.WhtHtmlPage_PrintsTheSameFormText</c> ตรวจทุกคำ)</para>
/// </summary>
public static class WhtCertFormText
{
    public sealed record CopyLabel(string Label, string Note);

    /// <summary>ฉบับที่ 1–2 ตามแบบทางการ + ฉบับที่ 3 สำเนาของผู้หักภาษีเก็บเอง (เพิ่มโดยตั้งใจ รอบ 72 · commit 6f8e7115 — แบบทางการมี 2 ฉบับ)</summary>
    public static readonly IReadOnlyList<CopyLabel> Copies = new[]
    {
        new CopyLabel("ฉบับที่ 1", "(สำหรับผู้ถูกหักภาษี ณ ที่จ่าย ใช้แนบพร้อมกับแบบแสดงรายการภาษี)"),
        new CopyLabel("ฉบับที่ 2", "(สำหรับผู้ถูกหักภาษี ณ ที่จ่าย เก็บไว้เป็นหลักฐาน)"),
        new CopyLabel("ฉบับที่ 3", "(สำหรับผู้หักภาษี ณ ที่จ่าย เก็บไว้เป็นหลักฐาน)"),
    };

    public const string Title = "หนังสือรับรองการหักภาษี ณ ที่จ่าย";
    public const string Subtitle = "ตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร";
    public const string BookNo = "เล่มที่";
    public const string CertNo = "เลขที่";

    public const string PayerHeading = "ผู้มีหน้าที่หักภาษี ณ ที่จ่าย :-";
    public const string PayeeHeading = "ผู้ถูกหักภาษี ณ ที่จ่าย :-";
    /// <summary>ป้ายช่อง 13 หลัก (เครื่องหมาย * ต่อท้ายแยก — HTML พิมพ์สีแดง)</summary>
    public const string TaxId13 = "เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)";
    /// <summary>ป้ายช่องเลขประจำตัวผู้เสียภาษีอากรแบบเดิม 10 หลัก — ระบบเก็บแต่ 13 หลัก ⇒ ช่องว่างเสมอ (ดู <see cref="TaxId10Groups"/>)</summary>
    public const string TaxId10 = "เลขประจำตัวผู้เสียภาษีอากร";
    public const string Name = "ชื่อ";
    public const string NameHint = "(ให้ระบุว่าเป็น บุคคล นิติบุคคล บริษัท สมาคม หรือคณะบุคคล)";
    public const string Address = "ที่อยู่";
    public const string AddressHint = "(ให้ระบุ ชื่ออาคาร/หมู่บ้าน ห้องเลขที่ ชั้นที่ เลขที่ ตรอก/ซอย หมู่ที่ ถนน ตำบล/แขวง อำเภอ/เขต จังหวัด)";
    /// <summary>กลุ่มช่องเลขเดิม 10 หลักตามแบบทางการ: 1-4-4-1 (เดิม renderer วาด 3-4-3)</summary>
    public static readonly int[] TaxId10Groups = { 1, 4, 4, 1 };

    public const string SeqLabel = "ลำดับที่";
    public const string SeqInForm = "ในแบบ";
    public const string SeqHint = "(ให้สามารถอ้างอิงหรือสอบยันกันได้ระหว่างลำดับที่ตามหนังสือรับรองฯ กับแบบยื่นรายการภาษีหักที่จ่าย)";

    /// <summary>ช่องแบบยื่น 7 ช่องตามลำดับบนแบบ — แถวบน 4 แถวล่าง 3</summary>
    public static readonly IReadOnlyList<string> FormBoxes = new[]
    {
        "(1) ภ.ง.ด.1ก", "(2) ภ.ง.ด.1ก พิเศษ", "(3) ภ.ง.ด.2", "(4) ภ.ง.ด.3",
        "(5) ภ.ง.ด.2ก", "(6) ภ.ง.ด.3ก", "(7) ภ.ง.ด.53",
    };

    /// <summary>หัวคอลัมน์ตามแบบทางการ "ประเภทเงินได้พึงประเมินจ่าย" (ไม่มี "ที่" — รอบ 204 · เดิม "…ประเมินที่จ่าย") ·
    /// ที่มา: ภาพแบบที่เจ้าของส่ง (คำตัดสินข้อ 148) วัดความกว้างตัวอักษรเทียบหัวคอลัมน์ "จำนวนเงินที่จ่าย" ในภาพเดียวกัน</summary>
    public const string ColIncomeType = "ประเภทเงินได้พึงประเมินจ่าย";
    public const string ColDate1 = "วัน เดือน";
    public const string ColDate2 = "หรือปีภาษี ที่จ่าย";
    public const string ColAmount = "จำนวนเงินที่จ่าย";
    public const string ColTax1 = "ภาษีที่หัก";
    public const string ColTax2 = "และนำส่งไว้";

    public const string Row1 = "1. เงินเดือน ค่าจ้าง เบี้ยเลี้ยง โบนัส ฯลฯ ตามมาตรา 40 (1)";
    public const string Row2 = "2. ค่าธรรมเนียม ค่านายหน้า ฯลฯ ตามมาตรา 40 (2)";
    public const string Row3 = "3. ค่าแห่งลิขสิทธิ์ ฯลฯ ตามมาตรา 40 (3)";
    public const string Row4a = "4. (ก) ดอกเบี้ย ฯลฯ ตามมาตรา 40 (4) (ก)";
    public const string Row4b = "(ข) เงินปันผล เงินส่วนแบ่งกำไร ฯลฯ ตามมาตรา 40 (4) (ข)";
    // 4(ข) — ข้อความและจุดตัดบรรทัดตามแบบทางการ (รอบ 204 · คำตัดสินข้อ 148 "เป๊ะทุกอย่าง") — เดิม: ตัดหลัง "โดยจ่ายจาก" ·
    // "กิจการที่ต้องเสียภาษี…ในอัตราดังนี้" · (2.2) "เงินปันผลหรือเงินส่วนแบ่ง…" · (2.3) "กำไรสุทธิส่วนที่ได้หัก… 5 ปี / ก่อนรอบระยะเวลา…"
    public const string Div1a = "(1) กรณีผู้ได้รับเงินปันผลได้รับเครดิตภาษี โดยจ่ายจากกำไรสุทธิของ";
    public const string Div1b = "กิจการที่ได้ต้องเสียภาษีเงินได้นิติบุคคลในอัตรา ดังนี้";
    public const string Div11 = "(1.1) อัตราร้อยละ 30 ของกำไรสุทธิ";
    public const string Div12 = "(1.2) อัตราร้อยละ 25 ของกำไรสุทธิ";
    public const string Div13 = "(1.3) อัตราร้อยละ 20 ของกำไรสุทธิ";
    public const string Div14 = "(1.4) อัตราอื่น ๆ (ระบุ)";
    public const string Div14Tail = "ของกำไรสุทธิ";
    public const string Div2 = "(2) กรณีผู้ได้รับเงินปันผลไม่ได้รับเครดิตภาษี เนื่องจากจ่ายจาก";
    public const string Div21 = "(2.1) กำไรสุทธิของกิจการที่ได้รับยกเว้นภาษีเงินได้นิติบุคคล";
    public const string Div22a = "(2.2) เงินปันผลหรือส่วนแบ่งของกำไรที่ได้รับยกเว้นไม่ต้องนำมารวม";
    public const string Div22b = "คำนวณเป็นรายได้เพื่อเสียภาษีเงินได้นิติบุคคล";
    public const string Div23a = "(2.3) กำไรสุทธิที่ได้หักผลขาดทุนสุทธิยกมาไม่เกิน 5 ปีก่อนรอบระยะ";
    public const string Div23b = "เวลาบัญชีปีปัจจุบัน";
    public const string Div24 = "(2.4) กำไรที่รับรู้ทางบัญชีโดยวิธีส่วนได้เสีย (equity method)";
    public const string Div25 = "(2.5) อื่น ๆ (ระบุ)";
    public const string Row5 = "5. การจ่ายเงินได้ที่ต้องหักภาษี ณ ที่จ่าย ตามคำสั่งกรมสรรพากรที่ออกตามมาตรา 3 เตรส เช่น รางวัล ส่วนลดหรือประโยชน์ใด ๆ เนื่องจากการส่งเสริมการขาย รางวัลในการประกวด การแข่งขัน การชิงโชค ค่าแสดงของนักแสดงสาธารณะ ค่าจ้างทำของ ค่าโฆษณา ค่าเช่า ค่าขนส่ง ค่าบริการ ค่าเบี้ยประกันวินาศภัย ฯลฯ";
    /// <summary>แถว 6 — ต่อท้ายด้วย "รายการ" ของบรรทัดที่ตกแถวนี้ (<see cref="Row6Spec"/>) หรือเส้นจุดเมื่อไม่มี</summary>
    public const string Row6 = "6. อื่น ๆ (ระบุ)";

    public const string Total = "รวมเงินที่จ่ายและภาษีที่หักนำส่ง";
    public const string TotalWords = "รวมเงินภาษีที่หักนำส่ง";
    public const string TotalWordsHint = "(ตัวอักษร)";

    /// <summary>หัวบรรทัดกองทุน (ตัวหนาบนแบบทางการ) — ต่อด้วย <see cref="FundGov"/> · รอบ 204 แยกจาก FundGov เดิม (ข้อความรวมเท่าเดิม)</summary>
    public const string FundLead = "เงินที่จ่ายเข้า";
    public const string FundGov = "กบข./กสจ./กองทุนสงเคราะห์ครูโรงเรียนเอกชน";
    public const string FundSso = "กองทุนประกันสังคม";
    public const string FundPvd = "กองทุนสำรองเลี้ยงชีพ";
    public const string Baht = "บาท";

    public const string PayerConditionHeading = "ผู้จ่ายเงิน";
    public static readonly IReadOnlyList<string> PayerConditions = new[]
    {
        "(1) หัก ณ ที่จ่าย", "(2) ออกให้ตลอดไป", "(3) ออกให้ครั้งเดียว", "(4) อื่น ๆ (ระบุ)",
    };

    public const string WarningHeading = "คำเตือน";
    public const string WarningText = "ผู้มีหน้าที่ออกหนังสือรับรองการหักภาษี ณ ที่จ่าย ฝ่าฝืนไม่ปฏิบัติตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร ต้องรับโทษทางอาญาตามมาตรา 35 แห่งประมวลรัษฎากร";
    public const string Certify = "ขอรับรองว่าข้อความและตัวเลขดังกล่าวข้างต้นถูกต้องตรงกับความจริงทุกประการ";
    public const string SignLead = "ลงชื่อ";
    /// <summary>ป้ายท้ายช่องลงชื่อตามแบบทางการ "ผู้จ่ายเงิน" — คำตัดสินเจ้าของข้อ 148 (2026-10-12) ทุก renderer
    /// (รอบ 72 `6f8e7115` เคยเปลี่ยนเป็น "ผู้มีหน้าที่หักภาษี ณ ที่จ่าย" — ยกเลิกแล้ว)</summary>
    public const string SignRole = "ผู้จ่ายเงิน";
    public const string SignDateHint = "(วัน เดือน ปี ที่ออกหนังสือรับรองฯ)";
    public static readonly IReadOnlyList<string> StampLines = new[] { "ประทับตรา", "นิติบุคคล", "(ถ้ามี)" };

    public const string FootnoteHeading = "หมายเหตุ";
    /// <summary>"เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)" + "*" + " หมายถึง"</summary>
    public const string FootnoteLead = "เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)";
    public const string FootnoteLeadTail = "หมายถึง";
    public static readonly IReadOnlyList<string> FootnoteItems = new[]
    {
        "1. กรณีบุคคลธรรมดาไทย ให้ใช้เลขประจำตัวประชาชนของกรมการปกครอง",
        "2. กรณีนิติบุคคล ให้ใช้เลขทะเบียนนิติบุคคลของกรมพัฒนาธุรกิจการค้า",
        "3. กรณีอื่น ๆ นอกเหนือจาก 1. และ 2. ให้ใช้เลขประจำตัวผู้เสียภาษีอากร (13 หลัก) ของกรมสรรพากร",
    };

    /// <summary>ลายน้ำใบที่ถูกยกเลิก (QuestPDF) — ไม่ใช่ข้อความของแบบ แต่เก็บที่นี่ให้ renderer ไม่มี literal ภาษาไทย</summary>
    public const string VoidWatermark = "ยกเลิก\nCANCELLED";

    /// <summary>ทุกข้อความคงที่ (เทสต์ "renderer พิมพ์ครบทุกคำ")</summary>
    internal static IEnumerable<string> AllStaticStrings()
    {
        foreach (var c in Copies) { yield return c.Label; yield return c.Note; }
        foreach (var s in new[]
        {
            Title, Subtitle, BookNo, CertNo, PayerHeading, PayeeHeading, TaxId13, TaxId10, Name, NameHint, Address, AddressHint,
            SeqLabel, SeqInForm, SeqHint, ColIncomeType, ColDate1, ColDate2, ColAmount, ColTax1, ColTax2,
            Row1, Row2, Row3, Row4a, Row4b, Div1a, Div1b, Div11, Div12, Div13, Div14, Div14Tail, Div2, Div21, Div22a, Div22b,
            Div23a, Div23b, Div24, Div25, Row5, Row6, Total, TotalWords, TotalWordsHint, FundLead, FundGov, FundSso, FundPvd, Baht,
            PayerConditionHeading, WarningHeading, WarningText, Certify, SignLead, SignRole, SignDateHint,
            FootnoteHeading, FootnoteLead, FootnoteLeadTail,
        }) yield return s;
        foreach (var s in FormBoxes) yield return s;
        foreach (var s in PayerConditions) yield return s;
        foreach (var s in StampLines) yield return s;
        foreach (var s in FootnoteItems) yield return s;
    }

    /// <summary>
    /// ช่องแบบยื่นที่ต้องติ๊ก (ดัชนีใน <see cref="FormBoxes"/>) — <b>ตัวตัดสินเดียวของทั้ง 3 renderer</b> (HTML · QuestPDF · หน้าพิมพ์ใน
    /// <c>wht.html</c> ผ่าน <c>WithholdingTaxCertResponse.FormBoxIndex</c> — หน้าไม่จับคู่ชื่อแบบเอง) · null = ไม่มีช่องบนแบบ (เช่น ภ.ง.ด.54)
    /// <para>ภ.ง.ด.1 → (1) ภ.ง.ด.1ก (ค่าเดิม) · ภ.ง.ด.2 → (3) (รอบ 204 · คำตัดสินข้อ 148 · เดิมไม่มีแบบนี้ ช่อง (3) ว่างเสมอ) ·
    /// ภ.ง.ด.3 → (4) · ภ.ง.ด.53 → (7) · ช่อง (2) 1ก พิเศษ / (5) 2ก / (6) 3ก ไม่มีแบบในระบบ ⇒ ไม่ติ๊ก</para>
    /// </summary>
    public static int? FormBoxIndex(TaxType formType) => formType switch
    {
        TaxType.WithholdingTax1 => 0,
        TaxType.WithholdingTax2 => 2,
        TaxType.WithholdingTax3 => 3,
        TaxType.WithholdingTax53 => 6,
        _ => null,
    };

    /// <summary>
    /// เงื่อนไข "ผู้จ่ายเงิน" ที่ต้องติ๊ก (ดัชนีใน <see cref="PayerConditions"/>) — ตัวตัดสินเดียวของทั้ง 3 renderer
    /// (หน้าเว็บผ่าน <c>WithholdingTaxCertResponse.PayerConditionIndex</c>) · (1) หัก ณ ที่จ่าย · (2) ออกให้ตลอดไป ·
    /// (3) ออกให้ครั้งเดียว (รอบ 204 · <c>PayOnce</c>) · (4) อื่น ๆ ไม่มีค่าในระบบ ⇒ null = ไม่ติ๊กช่องใด
    /// </summary>
    public static int? PayerConditionIndex(WithholdingTaxCertType certificateType) => certificateType switch
    {
        WithholdingTaxCertType.Withhold => 0,
        WithholdingTaxCertType.PayAlways => 1,
        WithholdingTaxCertType.PayOnce => 2,
        _ => null,
    };

    /// <summary>
    /// "รายการ" ที่พิมพ์ในช่อง (ระบุ) ของแถว 6 อื่น ๆ — บรรทัดที่ตกแถว 6 (<see cref="ThaiWhtRateTable.CertificateRow"/> = "6")
    /// · ชื่อประเภทจากตารางกลาง → คำอธิบายบรรทัด → รหัส · ไม่มีบรรทัด = null (renderer พิมพ์เส้นจุด)
    /// <para>แถว 5 ไม่มีช่อง (ระบุ) บนแบบทางการ ⇒ ไม่พิมพ์รายการ (ข้อความแถว 5 คงเดิมคำต่อคำ)</para>
    /// <para>ตัวตั้งข้อความ "ระบุ" ตัวเดียว = <see cref="ThaiWhtRateTable.CertificateRowSpecification"/> (ชื่อประเภทจาก
    /// <c>CertificateRowText</c> · null = ห้ามพิมพ์ชื่อประเภท ห้าม fallback ไป <c>Name</c>) — ที่นี่แค่เรียงตามลำดับบรรทัดแล้วส่งต่อ</para>
    /// </summary>
    public static string? Row6Spec(IEnumerable<WithholdingTaxCertLine> lines)
    {
        return ThaiWhtRateTable.CertificateRowSpecification("6",
            lines.OrderBy(l => l.LineOrder).Select(l => ((string?)l.IncomeTypeCode, (string?)LineSpecOrNull(l))));
    }

    /// <summary>รายการของบรรทัดเดียว — ส่งให้หน้าเว็บผ่าน <c>WithholdingTaxCertLineResponse.CertificateSpecText</c> (หน้าไม่คิดเอง)</summary>
    public static string LineSpec(WithholdingTaxCertLine line)
        => LineSpecOrNull(line) ?? (line.IncomeTypeCode ?? "").Trim();

    /// <summary>ข้อความ "ระบุ" ของบรรทัด: ชื่อประเภทบนแบบ (<see cref="ThaiWhtRateTable.CertificateRowText"/>) → คำอธิบายบรรทัด</summary>
    private static string? LineSpecOrNull(WithholdingTaxCertLine line)
    {
        var text = ThaiWhtRateTable.CertificateRowText(line.IncomeTypeCode);
        if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        return string.IsNullOrWhiteSpace(line.IncomeDescription) ? null : line.IncomeDescription.Trim();
    }
}
