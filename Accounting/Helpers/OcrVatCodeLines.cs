using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>คำตัดสินของชั้น "อัตรา VAT รายบรรทัดตามรหัส ภ.พ. ของกระดาษ"</summary>
public enum OcrVatCodeVerdict
{
    /// <summary>กระดาษไม่มีตารางรหัส ภ.พ. ที่ผสมกลุ่มภาษี (หรือไม่มีบรรทัด) — ชั้นนี้ไม่พูดอะไร · เส้นเดิมทุกตัวอักษร</summary>
    NotApplicable = 0,
    /// <summary>อัตรารายบรรทัด (และส่วนลดต่อรหัส ถ้ามี) พิสูจน์ด้วยตารางรหัส ภ.พ. บนกระดาษแล้ว</summary>
    Applied = 1,
    /// <summary>กระดาษผสมกลุ่มภาษี แต่บรรทัดกระทบกับตารางไม่ได้ — ไม่แตะอะไร (ตกชั้นเดิม) · ด่าน <see cref="OcrVatCodeLines.TableMismatch"/> ฟ้องผลสุดท้าย</summary>
    Unreconciled = 2,
}

/// <summary>หนึ่งบรรทัดที่ป้อนชั้นนี้</summary>
/// <param name="PrintedAmount">ยอดก่อนส่วนลดท้ายบิลของบรรทัดตามที่พิมพ์ (ตัวเดียวกับที่ตัวกระทบยอดใช้ — <c>lineGross</c>)</param>
/// <param name="PresetRate">อัตราที่ชั้นบนตั้งไว้แล้ว (engine · ผู้ใช้ในหน้ารีวิว · สัญลักษณ์ท้ายบรรทัด) · null = ยังไม่มี</param>
/// <param name="Description">รายละเอียดของบรรทัด — ใช้แยก "แถวส่วนลดที่ engine คืนเป็นสินค้า" และหาสินค้าที่แถวส่วนลดเอ่ยชื่อ
/// (<see cref="OcrVatCodeLines.Decide"/> · คำตัดสินเจ้าของ 2026-10-16 ข้อ 3) · null = ไม่รู้</param>
/// <param name="AccountId">ผังบัญชีที่บรรทัดนี้จะได้ (บรรทัดที่เรียนไว้/ผู้ใช้เลือก · ไม่มี = ผังระดับหัวใบ — ผู้เรียก resolve ด้วย <c>OcrLineAccountSource</c> ตัวเดียวกับตัวสร้างบรรทัด)
/// — บรรทัดสรุปแทนรายการได้เฉพาะเมื่อทุกรายการใช้ผังเดียวกัน (ฝ่ายค้าน f7c29c2c P1-3)</param>
/// <param name="ProjectId">โครงการของบรรทัด — ตารางชนะ (ข้อ 2 ของคำตัดสินเจ้าของ 2026-10-16 ภาคต่อ) ใช้ผัง/โครงการที่ทุกรายการใช้ร่วมกัน ·
/// ต่างกัน ⇒ ของที่ยอดมากสุด + หมายเหตุรายการเดิม (ไม่บล็อก · ไม่ทิ้งเงียบ)</param>
/// <param name="AccountLabel">ป้ายผังบัญชีที่คนอ่านได้ (รหัสผัง) ของ <paramref name="AccountId"/> — ใช้ในหมายเหตุรายการเดิมเท่านั้น · null = ไม่ระบุ</param>
/// <param name="ProjectLabel">ป้ายโครงการ (ชื่อ) ของ <paramref name="ProjectId"/> — ใช้ในหมายเหตุรายการเดิมเท่านั้น</param>
public readonly record struct OcrVatCodeLineInput(decimal PrintedAmount, decimal? PresetRate, string? Description = null,
    Guid? AccountId = null, Guid? ProjectId = null, string? AccountLabel = null, string? ProjectLabel = null);

/// <summary>บรรทัดเอกสารของใบที่มีตารางรหัส ภ.พ. ผสม มาจากไหน (คำตัดสินเจ้าของ 2026-10-16 ข้อ 1)</summary>
public enum OcrVatCodeLineSource
{
    /// <summary>บรรทัดรายการที่ engine อ่าน (ลงตารางแล้ว หรือกระดาษไม่มีตารางผสม — เส้นเดิม)</summary>
    Items = 0,
    /// <summary>บรรทัดสรุปหนึ่งบรรทัดต่อกลุ่มรหัส ภ.พ. ตามตารางของกระดาษ — รายการกระทบกับตารางไม่ได้ (อัปโหลดแค่หน้าสุดท้าย/แถวขาด) ⇒ <b>ตารางชนะ</b></summary>
    TableSummary = 1,
}

/// <summary>ผลของ <see cref="OcrVatCodeLines.Decide"/> — ตัวสร้างบรรทัด (service + replay harness) เดินตามนี้อย่างเดียว</summary>
/// <param name="Source">ใช้บรรทัดรายการ หรือบรรทัดสรุปตามตาราง</param>
/// <param name="Keep">ดัชนีบรรทัดต้นทางที่เป็นบรรทัดเอกสาร (ลำดับเดิม) — แถวส่วนลดที่แปลงแล้วไม่อยู่ในนี้</param>
/// <param name="DiscountRows">ดัชนีบรรทัดต้นทางที่ engine คืนเป็น "สินค้า" แต่คือแถวส่วนลด (แปลงเป็นส่วนลดเอกสารแล้ว · ว่าง = ไม่มี)</param>
/// <param name="BillDiscount">ส่วนลดท้ายบิลที่ใช้กระทบยอด (ของกระดาษ · หรือ Σ แถวส่วนลดที่แปลงเมื่อหัวใบอ่านส่วนลดไม่ได้)</param>
/// <param name="NetSubForRecon">ฐานก่อน VAT หลังส่วนลดที่ใช้กระทบยอด (สูตรเดียวกับตัวสร้างบรรทัดเดิม)</param>
/// <param name="Recon">ผลตัวกระทบยอดบนบรรทัด <paramref name="Keep"/></param>
/// <param name="PricesIncludeVat">ยอดบรรทัดรวม VAT (ค่าที่ผู้เรียกตั้งมา หรือที่ตัวกระทบยอดตัดสิน)</param>
/// <param name="Plan">แผนอัตรา/ส่วนลดตามรหัส ภ.พ. — ดัชนีตาม <paramref name="Keep"/></param>
/// <param name="TableLines">กลุ่มของตาราง (ฐาน · VAT) เมื่อ <paramref name="Source"/> = <see cref="OcrVatCodeLineSource.TableSummary"/></param>
/// <param name="Note">ข้อความอธิบาย (ไม่มีแท็ก) · null = ไม่มีอะไรต้องบอก</param>
/// <param name="SummaryAccountId">ผังบัญชีของบรรทัดสรุป (= ผังที่ทุกรายการใช้ร่วมกัน) เมื่อ <paramref name="Source"/> = <see cref="OcrVatCodeLineSource.TableSummary"/></param>
/// <param name="SummaryProjectId">โครงการของบรรทัดสรุป (= โครงการที่ทุกรายการใช้ร่วมกัน · ต่างกัน = ของที่ยอดมากสุด · null = ไม่มี) เมื่อเป็นบรรทัดสรุป</param>
/// <param name="DiscountOnly">ทุกแถวที่ engine คืนคือแถวส่วนลด (หน้าสุดท้ายของห้างที่มีแต่ส่วนลด · Makro 2/2 405901117163) — ตารางรหัส ภ.พ. หักส่วนลดแล้ว ⇒
/// แถวเหล่านี้ไม่เป็นสินค้า 7% และไม่ถูกหักซ้ำ</param>
/// <param name="MixedDimensionsNote">หมายเหตุ (ไม่มีแท็ก · ไม่บล็อก) เมื่อตารางชนะแต่รายการเดิมมีหลายผัง/โครงการ — รายการเดิมพร้อมผัง/โครงการ ให้ผู้ใช้แยกบรรทัดเองได้ · null = ไม่มี</param>
public sealed record OcrVatCodeDecision(
    OcrVatCodeLineSource Source, IReadOnlyList<int> Keep, IReadOnlyList<int> DiscountRows,
    decimal BillDiscount, decimal NetSubForRecon, OcrLineReconcileResult Recon, bool PricesIncludeVat,
    OcrVatCodePlan Plan, IReadOnlyList<OcrVatGroup> TableLines, string? Note,
    Guid? SummaryAccountId = null, Guid? SummaryProjectId = null,
    bool DiscountOnly = false, string? MixedDimensionsNote = null);

/// <summary>ผลของชั้นนี้</summary>
/// <param name="Rates">อัตราต่อบรรทัดตามลำดับเดิม — ผู้เรียกเติมเฉพาะบรรทัดที่ยังไม่มีอัตรา <b>หรือ</b> <paramref name="ReplaceEngineRate"/> = true · null = ไม่แตะ</param>
/// <param name="ReplaceEngineRate">อัตราที่ engine ส่งมาคือ<b>รหัส ภ.พ.</b> ไม่ใช่อัตรา (Azure อ่านคอลัมน์ "VAT CODE" = 2 เป็น TaxRate 2%) ⇒ แทนด้วยอัตราตามรหัส</param>
/// <param name="AmountsAfterDiscount">ยอดหลังส่วนลดต่อบรรทัด เมื่อกระดาษมีส่วนลดท้ายบิล — ส่วนลดแบ่งตามรหัสให้ Σ ต่อรหัส = ตาราง · null = ไม่มีส่วนลดให้แบ่ง</param>
/// <param name="DiscountPercents">% ส่วนลดของบรรทัด (ส่วนลดของรหัสนั้น ÷ ยอดก่อนลดของรหัส) · null คู่กับ <paramref name="AmountsAfterDiscount"/></param>
/// <param name="PricesIncludeVat">ยอดบรรทัดรวม VAT แล้วไหม (Σ บรรทัด − ส่วนลด = แถว "รวม" ของตาราง ⇒ true · = ฐาน ⇒ false)</param>
/// <param name="Note">เหตุผลภาษาไทยพร้อมตัวเลข — ทั้งตอนใช้และตอนไม่ใช้</param>
public sealed record OcrVatCodePlan(
    OcrVatCodeVerdict Verdict, decimal?[] Rates, bool[] ReplaceEngineRate,
    decimal[]? AmountsAfterDiscount, decimal[]? DiscountPercents, bool? PricesIncludeVat, string Note)
{
    public bool Applied => Verdict == OcrVatCodeVerdict.Applied;
}

/// <summary>
/// **อัตรา VAT รายบรรทัดตาม "รหัส ภ.พ." ที่กระดาษพิมพ์ — ตารางรหัส ภ.พ. ของกระดาษเป็นตัวตั้ง ไม่ใช่ชื่อสินค้า** (2026-10-16 · คำสั่งเจ้าของ
/// "อัตรา VAT ต่อบรรทัดยังเดาจากชื่อสินค้า ยังไม่ได้ยึดตารางรหัส ภ.พ.")
///
/// ═══ ที่มา ═══
/// <para>ใบกำกับห้าง (Makro/CP Axtra · Lotus's · Big C · Tops) พิมพ์คอลัมน์ "รหัส ภ.พ. / VAT CODE" ต่อแถวสินค้า และตารางสรุปต่อรหัส
/// (ฐาน · VAT · รวม — <see cref="OcrLineVatMarks.ReadGroups"/>) พร้อมคำอธิบายรหัส ("1=สินค้าได้รับการยกเว้นภาษีมูลค่าเพิ่ม 2=สินค้าที่ต้องเสีย…") ·
/// เดิมเมื่อ engine คืนบรรทัดรายการ อัตรารายบรรทัดมาจาก <see cref="ThaiVatTypeRule.Suggest"/> (เดาจากชื่อ — "ไข่ไก่" ไม่อยู่ในรายการคำ ⇒ 7%) ·
/// ตารางถูกใช้แค่บอกยอดยกเว้นให้ชั้นพิสูจน์ทั้งใบ "ไม่ตัดสิน" ⇒ ใบผสมได้ [Σ-GAP] VatRateMismatch ทุกใบ ให้คนแยกเอง ·
/// ส่วนลดท้ายบิลถูกกระจาย % เดียวทุกบรรทัด ⇒ แม้อัตราถูก Σ ยกเว้นก็ไม่เท่าตาราง (ส่วนลดของกลุ่ม VAT ไหลไปกลุ่มยกเว้น)</para>
///
/// ═══ ลำดับชั้น (DECISION_DOCTRINE §1 — หลักฐานใกล้ของจริงชนะการเดา) ═══
/// <list type="number">
/// <item>อัตราที่ชั้นบนตั้งแล้ว (engine/ผู้ใช้/สัญลักษณ์ท้ายบรรทัด) — ชนะ · <b>ยกเว้น</b>ค่าที่เป็นรหัสบนตารางและไม่ใช่อัตราไทย (1/2) = engine อ่านรหัสเป็นอัตรา</item>
/// <item><b>รหัสรายบรรทัดที่พิมพ์บนแถวสินค้า</b> (<c>ราคาต่อหน่วย รหัส ยอด</c> หรือ <c>ยอด รหัส</c> ท้ายแถว · จับคู่ด้วยยอดตรงถึงสตางค์ตามลำดับ)
///   แปลความหมายด้วย<b>คำอธิบาย/ตารางของกระดาษเอง</b> (<see cref="OcrVatGroupKind"/>) — ไม่มีตารางรหัสตายตัวต่อห้างในเส้นทั่วไป</item>
/// <item><b>ผลรวมของตารางเป็นข้อบังคับ</b>: บรรทัดที่ไม่มีรหัส (engine ตัดคอลัมน์รหัสทิ้ง) แบ่งเข้ากลุ่มได้เมื่อมี<b>ทางเดียว</b>ที่ Σ ต่อกลุ่ม = ตาราง
///   (ไม่มีส่วนลด · สองกลุ่ม · ≤ 20 บรรทัด) — สองทางขึ้นไป = ไม่เดา</item>
/// <item>ตัวพิสูจน์ทั้งใบ / ตัวเดาจากชื่อ (ชั้นเดิม) — เฉพาะบรรทัดที่ยังว่าง</item>
/// </list>
///
/// ═══ เงื่อนไขก่อนใช้ (ไม่ครบ = <see cref="OcrVatCodeVerdict.Unreconciled"/> · ไม่แตะอะไร) ═══
/// <list type="bullet">
/// <item>ทุกกลุ่มในตารางรู้ความหมาย (<see cref="OcrVatGroupTable.AllKnown"/>) — รหัสที่คำอธิบายไม่บอก/ขัดกับตัวเลข = ไม่เดา</item>
/// <item>VAT ของตาราง = VAT หัวใบ · รวมของตาราง = ยอดรวมหัวใบ (±0.02) — กระดาษกับหัวสแกนต้องเป็นเรื่องเดียวกัน</item>
/// <item>Σ บรรทัด − ส่วนลดท้ายบิล = แถว "รวม" ของตาราง (ราคารวม VAT) หรือ = ฐาน (ราคาก่อน VAT) และตรงกับที่ตัวกระทบยอดตัดสิน</item>
/// <item>ต่อกลุ่ม: Σ บรรทัดของกลุ่ม − ส่วนลดของกลุ่ม = ยอดกลุ่มในตาราง · ส่วนลดของกลุ่ม ≥ 0 · Σ ส่วนลดทุกกลุ่ม = ส่วนลดท้ายบิลบนกระดาษ ·
///   ไม่มีส่วนลด ⇒ Σ บรรทัดของกลุ่ม = ยอดกลุ่มพอดี</item>
/// <item>ไม่มีบรรทัดติดลบ (ส่วนลดเป็นบรรทัดแยกรหัสไม่ได้)</item>
/// </list>
/// <para>ส่วนลดต่อกลุ่มกระจายลงบรรทัดในกลุ่มตามสัดส่วน เศษลงบรรทัดสุดท้ายของกลุ่ม ⇒ Σ ต่อกลุ่ม = ตาราง<b>ทุกสตางค์</b> (ไม่ใช่ % เดียวทั้งใบ)</para>
/// </summary>
public static class OcrVatCodeLines
{
    private const MidpointRounding R = MidpointRounding.AwayFromZero;
    private const decimal HeaderTol = 0.02m;
    /// <summary>เพดานจำนวนบรรทัดที่ไม่มีรหัสสำหรับการหาทางแบ่งกลุ่มเดียว (2^n)</summary>
    public const int MaxFreeLines = 20;
    private const string Money = @"\d{1,3}(?:,\d{3})+\.\d{2}|\d+\.\d{2}";

    /// <summary>แถวสินค้าที่พิมพ์รหัสระหว่างราคาต่อหน่วยกับยอด (คอลัมน์ "ราคาต่อหน่วย | รหัส ภ.พ. | จำนวนเงินรวม" ของ Makro)</summary>
    private static readonly Regex CodeBetween = new(
        @"(?<![\d.,])(?<price>" + Money + @")[ \t]+(?<code>[0-9A-Z])[ \t]+(?<amt>" + Money + @")(?:[ \t]+[,.;:'`|]{1,2})?[ \t]*$",
        RegexOptions.CultureInvariant);

    /// <summary>แถวสินค้าที่พิมพ์รหัส<b>ตัวอักษร</b>ท้ายยอด (<c>220.00 V</c>) — ฝ่ายค้าน afbb0956 P2-1: ตัวเลขท้ายยอด (<c>ค่าขนส่ง 100.00 1</c> ·
    /// <c>ลำดับ 2 ส่วนลด 10.00 1</c>) คือจำนวน/ลำดับได้ ⇒ ไม่รับเป็นรหัส</summary>
    private static readonly Regex CodeTrailing = new(
        @"(?<![\d.,])(?<amt>" + Money + @")[ \t]+(?<code>[A-Z])[ \t]*$", RegexOptions.CultureInvariant);

    /// <summary>ตัวเลขที่อาจเป็น "จำนวน" ก่อนราคาต่อหน่วยบนแถวเดียวกัน (<c>6 EA</c> · <c>1.235 KG</c>)</summary>
    private static readonly Regex QtyToken = new(@"(?<![\w.,])\d{1,5}(?:\.\d{1,3})?(?![\w,]|\.\d)", RegexOptions.CultureInvariant);

    /// <summary>หัวข้อความของด่าน <see cref="TableMismatch"/> — ตัวเดียวทั้งฝั่งเขียนและผู้อ่านข้อความ</summary>
    public const string MismatchLead = "อัตรา VAT รายบรรทัดไม่ตรงตารางรหัส ภ.พ. บนกระดาษ";

    /// <summary>ตารางรหัส ภ.พ. ที่ "ผสมกลุ่มภาษี" (มีกลุ่มที่ไม่ใช่ 7%) — เงื่อนไขเดียวที่ชั้นนี้และด่าน <see cref="TableMismatch"/> ทำงาน ·
    /// ตารางกลุ่มเดียว 7% (Wine Pro "V 7 …") = ไม่ผสม ⇒ ชั้นพิสูจน์ทั้งใบเดิมตัดสิน</summary>
    internal static bool IsMixed(OcrVatGroupTable table)
        => table.Found && table.Groups.Any(g => g.Kind != OcrVatGroupKind.Standard7);

    /// <summary>อัตราตามกลุ่ม (<c>7</c> · <c>0</c> · <c>-1</c>) · null = ไม่รู้</summary>
    private static decimal? RateOf(OcrVatGroupKind kind) => kind switch
    {
        OcrVatGroupKind.Standard7 => 7m,
        OcrVatGroupKind.ZeroRated => 0m,
        OcrVatGroupKind.Exempt => ThaiVatTypeRule.ExemptRate,
        _ => null,
    };

    private static OcrVatGroupKind? KindOfRate(decimal rate) => rate switch
    {
        7m => OcrVatGroupKind.Standard7,
        0m => OcrVatGroupKind.ZeroRated,
        ThaiVatTypeRule.ExemptRate => OcrVatGroupKind.Exempt,
        _ => null,
    };

    /// <summary>รหัส ภ.พ. ที่พิมพ์บนแถวสินค้า (ยอด · รหัส) ตามลำดับบนกระดาษ — รับเฉพาะรหัสที่อยู่ใน <paramref name="codes"/> (รหัสของตารางบนกระดาษเดียวกัน)
    /// · ข้ามแถวตารางสรุป/คำอธิบาย (ไม่มีรูป "เงิน รหัส เงิน")
    /// <para>ฝ่ายค้าน afbb0956 P2-1 — ตัวเลขระหว่างราคากับยอดอาจเป็น<b>จำนวน</b> (<c>ITEM 2 59.00 2 118.00</c> · <c>ไข่ไก่ 53.50 2 107.00</c>) ⇒ รับเป็นรหัสเมื่อ
    /// เลขคณิตของแถวพิสูจน์ว่าจำนวนอยู่<b>ที่อื่น</b>: มีเลขก่อนราคาที่ จำนวน × ราคา = ยอด (คอลัมน์จำนวนแยก) และ (รหัส = 1 หรือ ราคา × รหัส ≠ ยอด) ·
    /// แถวที่ ราคา × รหัส = ยอด ("อาจเป็นจำนวน") รับได้เฉพาะเมื่อกระดาษเดียวกันมีแถวที่<b>พิสูจน์คอลัมน์รหัส</b>แล้ว (ราคา × รหัส ≠ ยอด) อย่างน้อยหนึ่งแถว ·
    /// ไม่มีแถวพิสูจน์ = ไม่รับรหัสตัวเลขเลย (ตกทางแบ่งด้วยผลรวม/ตารางชนะ) · รหัสตัวอักษรท้ายยอดไม่ใช่จำนวน — รับตามเดิม</para></summary>
    internal static IReadOnlyList<(decimal Amount, string Code)> ReadRowCodes(string? rawText, IReadOnlyCollection<string> codes)
    {
        var list = new List<(decimal Amount, string Code, bool Proven)>();
        if (string.IsNullOrWhiteSpace(rawText) || codes.Count == 0) return Array.Empty<(decimal, string)>();
        static decimal Num(string v) => decimal.Parse(v.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture);
        foreach (var raw in rawText.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var m = CodeBetween.Match(line);
            if (m.Success && codes.Contains(m.Groups["code"].Value))
            {
                var code = m.Groups["code"].Value;
                var amt = Num(m.Groups["amt"].Value);
                if (!int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var c))
                {
                    list.Add((amt, code, true));      // รหัสตัวอักษรไม่ใช่จำนวน
                    continue;
                }
                var price = Num(m.Groups["price"].Value);
                var qtyElsewhere = QtyToken.Matches(line[..m.Groups["price"].Index])
                    .Select(q => decimal.Parse(q.Value, NumberStyles.Number, CultureInfo.InvariantCulture))
                    .Any(q => q > 0m && Math.Abs(q * price - amt) <= 0.01m);
                if (!qtyElsewhere) continue;          // ไม่มีคอลัมน์จำนวนแยก ⇒ เลขนี้อาจเป็นจำนวน — ไม่รับ
                var couldBeQty = Math.Abs(c * price - amt) <= 0.01m;
                list.Add((amt, code, !couldBeQty));
                continue;
            }
            m = CodeTrailing.Match(line);
            if (!m.Success || !codes.Contains(m.Groups["code"].Value)) continue;
            list.Add((Num(m.Groups["amt"].Value), m.Groups["code"].Value, true));
        }
        // คอลัมน์รหัสต้องถูกพิสูจน์บนกระดาษเดียวกันอย่างน้อยหนึ่งแถว — ไม่งั้นแถว "อาจเป็นจำนวน" ทั้งหมดไม่นับ
        if (!list.Any(x => x.Proven)) return Array.Empty<(decimal, string)>();
        return list.Select(x => (x.Amount, x.Code)).ToList();
    }

    /// <summary>ตัดสินอัตรา VAT รายบรรทัด (และส่วนลดต่อรหัส) จากรหัส ภ.พ. + ตารางรหัส ภ.พ. ของกระดาษ (pure)</summary>
    /// <param name="lines">บรรทัดตามลำดับเดิม</param>
    /// <param name="rawText">ข้อความทั้งใบ (อ่านรหัสบนแถวสินค้า)</param>
    /// <param name="table">ตารางรหัส ภ.พ. (<see cref="OcrLineVatMarks.ReadGroups"/>)</param>
    /// <param name="headerVat">VAT หัวใบ</param>
    /// <param name="headerTotal">ยอดรวมทั้งสิ้นหัวใบ (0 = ไม่รู้)</param>
    /// <param name="headerDiscount">ส่วนลดท้ายบิลที่กระดาษพิมพ์ (0 = ไม่มี)</param>
    /// <param name="reconPricesIncludeVat">ตัวกระทบยอด (<see cref="OcrLineReconciler"/>) ตัดสินว่ายอดบรรทัดรวม VAT</param>
    /// <param name="reconTargetLineSum">เป้า Σ บรรทัดหลังส่วนลดของตัวกระทบยอด (null = ไม่มีส่วนลดให้กระจาย)</param>
    /// <param name="namedDiscounts">ส่วนลดที่แถวส่วนลด<b>เอ่ยชื่อสินค้า</b> (ดัชนีบรรทัด · ยอด) — ลงบรรทัดนั้นก่อน ส่วนที่เหลือของกลุ่มกระจายตามสัดส่วน
    /// (คำตัดสินเจ้าของ 2026-10-16 ข้อ 3) · null = ไม่มี (สูตรเดิมทุกตัวอักษร)</param>
    public static OcrVatCodePlan Plan(
        IReadOnlyList<OcrVatCodeLineInput> lines, string? rawText, OcrVatGroupTable table,
        decimal headerVat, decimal headerTotal, decimal headerDiscount,
        bool reconPricesIncludeVat, decimal? reconTargetLineSum,
        IReadOnlyList<(int Line, decimal Amount)>? namedDiscounts = null)
    {
        var n = lines.Count;
        var rates = new decimal?[n];
        var replace = new bool[n];
        OcrVatCodePlan Result(OcrVatCodeVerdict v, string why)
            => new(v, new decimal?[n], new bool[n], null, null, null, why);
        OcrVatCodePlan No(string why) => Result(OcrVatCodeVerdict.Unreconciled, "รหัส ภ.พ.: " + why + " — ไม่ใช้ตาราง แบ่งอัตรารายบรรทัด");

        if (n == 0 || !IsMixed(table)) return Result(OcrVatCodeVerdict.NotApplicable, "");
        if (!table.AllKnown)
            return No("ตารางมีรหัสที่กระดาษไม่บอกความหมาย ("
                + string.Join(" · ", table.Groups.Where(g => g.Kind == OcrVatGroupKind.Unknown).Select(g => g.Evidence)) + ")");
        if (Math.Abs(table.Vat - headerVat) > HeaderTol)
            return No($"VAT ของตาราง {table.Vat:N2} ≠ VAT หัวใบ {headerVat:N2}");
        if (headerTotal > 0m && Math.Abs(table.Gross - headerTotal) > HeaderTol)
            return No($"รวมของตาราง {table.Gross:N2} ≠ ยอดรวมหัวใบ {headerTotal:N2}");
        if (lines.Any(l => l.PrintedAmount < 0m))
            return No("มีบรรทัดติดลบ (ส่วนลดเป็นบรรทัด) — แบ่งตามรหัสไม่ได้");

        // ── 1) กลุ่มของแต่ละบรรทัด: อัตราที่ตั้งแล้ว > รหัสที่ engine ส่งมาเป็น "อัตรา" > รหัสบนแถวสินค้า ──
        var codes = table.Groups.Select(g => g.PaperCode).ToHashSet(StringComparer.Ordinal);
        var kindOfCode = table.Groups.GroupBy(g => g.PaperCode).ToDictionary(g => g.Key, g => g.First().Kind, StringComparer.Ordinal);
        var kinds = new OcrVatGroupKind?[n];
        // ฝ่ายค้าน afbb0956 P1-1: รหัสที่พิมพ์บนแถวจับคู่กับ<b>ทุกบรรทัด</b> (รวมบรรทัดที่ engine ตั้งค่ามาแล้ว) — รหัสบนกระดาษชนะ "อัตรา" ของ engine ที่เป็นรหัส
        // (Makro จริงที่ engine ให้ [2,1,1,2,2,2,2] เดิม engine ชนะ) · อัตราไทยที่ตั้งไว้ขัดกับรหัสที่พิมพ์ = กระทบไม่ได้ (ไม่เลือกข้างเงียบ)
        var printedCodes = MatchRowCodes(lines, ReadRowCodes(rawText, codes));
        var fromRow = 0;
        var engineOverruled = 0;
        for (var i = 0; i < n; i++)
        {
            OcrVatGroupKind? printedKind = printedCodes[i] is string pc ? kindOfCode[pc] : null;
            if (lines[i].PresetRate is decimal pr)
            {
                var asCode = pr == decimal.Truncate(pr) ? pr.ToString("0", CultureInfo.InvariantCulture) : null;
                if (KindOfRate(pr) is null && asCode != null && kindOfCode.TryGetValue(asCode, out var ck))
                {
                    kinds[i] = printedKind ?? ck; replace[i] = true;
                    if (printedKind is OcrVatGroupKind pk0 && pk0 != ck) engineOverruled++;
                    if (printedKind is not null) fromRow++;
                    continue;
                }
                if (KindOfRate(pr) is OcrVatGroupKind pk)
                {
                    if (printedKind is OcrVatGroupKind pkk && pkk != pk)
                        return No($"บรรทัดที่ {i + 1} ตั้งอัตรา {RateText(pr)} แต่รหัส {printedCodes[i]} ที่พิมพ์บนแถวคือ{KindText(pkk)}");
                    kinds[i] = pk;
                    continue;
                }
                return No($"บรรทัดที่ {i + 1} มีอัตรา {pr:0.##}% ที่ไม่ใช่อัตราไทยและไม่ใช่รหัสบนตาราง");
            }
            if (printedKind is not null) { kinds[i] = printedKind; fromRow++; }
        }

        // ── 2) ราคารวม/ก่อน VAT: Σ บรรทัด − ส่วนลด = แถว "รวม" หรือ ฐาน ของตาราง ──
        var sum = lines.Sum(l => l.PrintedAmount);
        var sumTol = Math.Max(0.02m, 0.01m * lines.Count(l => l.PrintedAmount != 0m));
        bool inclusive;
        if (table.Vat > 0m && Math.Abs(sum - headerDiscount - table.Gross) <= sumTol) inclusive = true;
        else if (Math.Abs(sum - headerDiscount - table.Net) <= sumTol) inclusive = false;
        else
            return No($"Σ บรรทัด {sum:N2}{(headerDiscount > 0m ? $" − ส่วนลด {headerDiscount:N2}" : "")} ไม่เท่าตาราง (ฐาน {table.Net:N2} · รวม {table.Gross:N2}) "
                + "— อ่านบรรทัดขาด/เกิน หรือส่วนลดอยู่ในบรรทัด");
        if (table.Vat > 0m && inclusive != reconPricesIncludeVat)
            return No($"ตารางบอกว่ายอดบรรทัด{(inclusive ? "รวม" : "ไม่รวม")} VAT แต่ตัวกระทบยอดตัดสินตรงข้าม");
        var tableAmt = inclusive ? table.Gross : table.Net;
        if (headerDiscount > 0m && (reconTargetLineSum is not decimal tgt || Math.Abs(tgt - tableAmt) > HeaderTol))
            return No($"ส่วนลดท้ายบิล {headerDiscount:N2} แต่ตัวกระทบยอดไม่ได้กระจายส่วนลดไปที่ {tableAmt:N2}");

        decimal Target(OcrVatGroupKind k) => table.Groups.Where(g => g.Kind == k).Sum(g => inclusive ? g.Gross : g.Net);
        var tableKinds = table.Groups.Select(g => g.Kind).Distinct().ToList();
        if (kinds.Any(k => k is OcrVatGroupKind kk && !tableKinds.Contains(kk)))
            return No("บรรทัดที่ตั้งอัตราไว้แล้วอยู่กลุ่มที่ตารางไม่มี");

        var free = Enumerable.Range(0, n).Where(i => kinds[i] is null && lines[i].PrintedAmount != 0m).ToList();
        decimal Fixed(OcrVatGroupKind k) => Enumerable.Range(0, n).Where(i => kinds[i] == k).Sum(i => lines[i].PrintedAmount);
        string source;

        // ── 3) บรรทัดที่ไม่มีรหัส: ผลรวมของตารางบังคับ "ทางเดียว" (ไม่มีส่วนลด · สองกลุ่ม) ──
        if (free.Count > 0)
        {
            if (headerDiscount > 0m)
                return No($"มีส่วนลดท้ายบิล {headerDiscount:N2} และ {free.Count} บรรทัดไม่มีรหัส ภ.พ. — แบ่งส่วนลดตามรหัสไม่ได้");
            if (tableKinds.Count != 2 || !tableKinds.Contains(OcrVatGroupKind.Standard7))
                return No($"{free.Count} บรรทัดไม่มีรหัส ภ.พ. และตารางมี {tableKinds.Count} กลุ่ม — แบ่งด้วยผลรวมได้เฉพาะสองกลุ่ม (7% + อีกกลุ่ม)");
            if (free.Count > MaxFreeLines)
                return No($"{free.Count} บรรทัดไม่มีรหัส ภ.พ. (เกิน {MaxFreeLines}) — ไม่ค้นทางแบ่ง");
            var other = tableKinds.Single(k => k != OcrVatGroupKind.Standard7);
            var need = Target(other) - Fixed(other);
            // ฝ่ายค้าน afbb0956 P2-3: ค้นด้วยค่าเผื่อเดียวกับด่านต่อกลุ่มข้างล่าง (≥ 2 สตางค์ · 1 สตางค์ต่อบรรทัด) และต้องเป็นทางเดียว<b>ภายใต้ค่าเผื่อนั้น</b>
            // — เดิมค้นตรงถึงสตางค์แต่ตรวจแบบมีค่าเผื่อ ⇒ OCR อ่าน 500.01 (จริง 500.00) แล้วชุดอื่นที่รวมได้ 500.00 พอดีถูกเลือกเป็นยกเว้น
            var solutions = SubsetsSumming(free.Select(i => lines[i].PrintedAmount).ToList(), need, 2, Math.Max(2, free.Count));
            if (solutions.Count != 1)
                return No(solutions.Count == 0
                    ? $"ไม่มีชุดบรรทัดที่รวมได้ยอดกลุ่ม{KindText(other)} {need:N2} พอดี"
                    : $"บรรทัดรวมได้ยอดกลุ่ม{KindText(other)} {need:N2} ได้มากกว่าหนึ่งทาง — ไม่เดาว่าบรรทัดไหน");
            var picked = solutions[0];
            for (var f = 0; f < free.Count; f++)
                kinds[free[f]] = picked.Contains(f) ? other : OcrVatGroupKind.Standard7;
            source = $"บรรทัดที่ไม่มีรหัส {free.Count} บรรทัดแบ่งด้วยผลรวมของตาราง (ทางเดียวที่ลงตัว)";
        }
        else
            source = (fromRow > 0 ? $"รหัสที่พิมพ์บนแถวสินค้า {fromRow} บรรทัด" : "รหัส/อัตราที่ชั้นบนตั้งไว้")
                + (engineOverruled > 0 ? $" · รหัสบนแถวชนะค่าของ engine {engineOverruled} บรรทัด" : "");

        // ── 4) ต่อกลุ่ม: Σ บรรทัด − ส่วนลดของกลุ่ม = ตาราง · Σ ส่วนลด = ส่วนลดท้ายบิล ──
        decimal[]? after = null, pcts = null;
        var discByKind = new Dictionary<OcrVatGroupKind, decimal>();
        foreach (var k in tableKinds)
        {
            var have = Fixed(k);
            var d = have - Target(k);
            var kTol = Math.Max(0.02m, 0.01m * Enumerable.Range(0, n).Count(i => kinds[i] == k && lines[i].PrintedAmount != 0m));
            if (headerDiscount <= 0m)
            {
                if (Math.Abs(d) > kTol)
                    return No($"กลุ่ม{KindText(k)}: Σ บรรทัด {have:N2} ≠ ตาราง {Target(k):N2}");
                continue;
            }
            if (d < -kTol)
                return No($"กลุ่ม{KindText(k)}: Σ บรรทัด {have:N2} น้อยกว่าตาราง {Target(k):N2} — อ่านบรรทัดขาด");
            discByKind[k] = Math.Max(0m, d);
        }
        if (headerDiscount > 0m)
        {
            var dSum = discByKind.Values.Sum();
            if (Math.Abs(dSum - headerDiscount) > HeaderTol)
                return No($"ส่วนลดที่ตารางบอกต่อกลุ่มรวม {dSum:N2} ≠ ส่วนลดท้ายบิลบนกระดาษ {headerDiscount:N2}");
            after = lines.Select(l => l.PrintedAmount).ToArray();
            pcts = new decimal[n];
            foreach (var (k, d) in discByKind)
            {
                if (d <= 0m) continue;
                var idx = Enumerable.Range(0, n).Where(i => kinds[i] == k && lines[i].PrintedAmount != 0m).ToList();
                // ส่วนลดที่เอ่ยชื่อสินค้าในกลุ่มนี้ ลงบรรทัดนั้นก่อน — ใช้ได้เมื่อ Σ ไม่เกินส่วนลดของกลุ่มตามตาราง และไม่เกินยอดของบรรทัด
                var named = (namedDiscounts ?? Array.Empty<(int Line, decimal Amount)>())
                    .Where(nd => nd.Line >= 0 && nd.Line < n && kinds[nd.Line] == k).ToList();
                var namedByLine = named.GroupBy(nd => nd.Line).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
                if (named.Count > 0 && (named.Sum(x => x.Amount) > d + HeaderTol
                        || namedByLine.Any(kv => kv.Value > lines[kv.Key].PrintedAmount)))
                    namedByLine.Clear();
                var rest = d - namedByLine.Values.Sum();
                var remaining = idx.Select(i => lines[i].PrintedAmount - namedByLine.GetValueOrDefault(i)).ToList();
                var shares = SplitProportional(rest, remaining);
                var groupPct = Math.Round(d / idx.Sum(i => lines[i].PrintedAmount) * 100m, 2, R);
                for (var x = 0; x < idx.Count; x++)
                {
                    var i = idx[x];
                    var share = shares[x] + namedByLine.GetValueOrDefault(i);
                    after[i] = lines[i].PrintedAmount - share;
                    // % ของบรรทัด: ไม่มีส่วนลดที่เอ่ยชื่อ = % ของกลุ่ม (สูตรเดิม) · มี = % ของบรรทัดเอง · ส่วนลด 0 = 0% (ห้ามติด % ที่ไม่มีบาทรองรับ)
                    pcts[i] = share <= 0m ? 0m
                        : namedByLine.Count == 0 ? groupPct
                        : Math.Round(share / lines[i].PrintedAmount * 100m, 2, R);
                }
            }
        }

        for (var i = 0; i < n; i++)
            if (kinds[i] is OcrVatGroupKind k && (lines[i].PresetRate is null || replace[i]))
                rates[i] = RateOf(k);
        var groupsText = string.Join(" · ", tableKinds.Select(k =>
            $"{KindText(k)} {Target(k):N2}" + (discByKind.TryGetValue(k, out var dk) && dk > 0m ? $" (ส่วนลด {dk:N2})" : "")));
        var engineText = replace.Any(x => x) ? $" · อัตราจาก engine {replace.Count(x => x)} บรรทัดเป็นรหัส ภ.พ. (ไม่ใช่ %) ⇒ แทนด้วยอัตราตามรหัส" : "";
        return new OcrVatCodePlan(OcrVatCodeVerdict.Applied, rates, replace, after, pcts, inclusive,
            $"อัตรา VAT รายบรรทัดตามตารางรหัส ภ.พ. บนกระดาษ ({source}{engineText}): {groupsText} — ยอด{(inclusive ? "รวม" : "ก่อน")} VAT ต่อกลุ่มตรงตาราง");
    }

    /// <summary>
    /// **ด่านผลสุดท้าย**: บรรทัดที่จะเขียนจริง แบ่งตามอัตราแล้ว Σ ฐานต่อกลุ่ม = ฐานต่อกลุ่มในตารางรหัส ภ.พ. ไหม — ไม่ว่าอัตรามาจากชั้นไหน
    /// (engine · ผู้ใช้ · ตัวเดาจากชื่อ) · null = ตรง / กระดาษไม่มีตารางผสม · ข้อความ (ไม่มีแท็ก) = ผู้เรียกเขียนเป็น <c>[Σ-GAP]</c>
    /// <para>รหัสไม่รู้ความหมายที่มี VAT (ตัวเลขขัดกันเอง) ⇒ ฟ้องเสมอ · กลุ่ม VAT 0 ที่ไม่รู้ว่ายกเว้น/0% ⇒ เทียบรวมกับกลุ่มไม่มี VAT (เรื่องเงิน — ฝ่ายค้าน
    /// afbb0956 P2-4: เดิมฟ้องทุกสแกนของตาราง "N 0 …" ที่ไม่มีคำอธิบาย) · ผู้อ่านตอนอนุมัติ/LINE ตรวจ<b>สด</b>บนบรรทัดตอนนี้ (<see cref="LiveNotes"/>) · ไม่แก้ตัวเลขใด ๆ</para>
    /// </summary>
    public static string? TableMismatch(IReadOnlyList<OcrPlannedLine> planned, OcrVatGroupTable table)
    {
        if (!IsMixed(table) || planned.Count == 0) return null;
        // รหัสที่ไม่รู้ความหมาย<b>และมี VAT</b> ที่ตัวเลขไม่บอกว่า 7% = ตัวเลขของกระดาษเองขัดกัน — ฟ้อง (ไม่เดา)
        var unknownWithVat = table.Groups.Where(g => g.Kind == OcrVatGroupKind.Unknown && g.Vat != 0m).ToList();
        if (unknownWithVat.Count > 0)
            return $"{MismatchLead}: ตารางมีรหัสที่กระดาษไม่บอกความหมาย ("
                + string.Join(" · ", unknownWithVat.Select(g => g.Evidence))
                + ") — อัตรารายบรรทัดยังมาจากการเดา ตรวจกับคอลัมน์รหัส ภ.พ. บนกระดาษก่อนอนุมัติ";
        // ฝ่ายค้าน afbb0956 P2-4: กลุ่ม VAT 0 ที่ไม่รู้ว่า "ยกเว้น" หรือ "0%" (ตารางตัวอักษร N 0 … ไม่มีคำอธิบาย) เดิมฟ้องทุกสแกน — ด่านนี้คือเรื่อง<b>เงิน</b>
        // (ฐานต่อกลุ่ม) ⇒ เทียบกลุ่มไม่มี VAT รวมกัน (ยกเว้น + 0%) · แยกยกเว้น/0% เฉพาะเมื่อตารางมีทั้งสองชนิดที่รู้ความหมาย · ความหมายของรหัสเป็นงานของ
        // ตัวตัดสินความหมาย (ไม่ใช่ด่านนี้)
        var precise = table.Groups.Any(g => g.Kind == OcrVatGroupKind.Exempt) && table.Groups.Any(g => g.Kind == OcrVatGroupKind.ZeroRated)
                      && table.Groups.All(g => g.Kind != OcrVatGroupKind.Unknown);
        string? Bucket(OcrVatGroupKind? k) => k switch
        {
            OcrVatGroupKind.Standard7 => "7",
            OcrVatGroupKind.ZeroRated or OcrVatGroupKind.Exempt or OcrVatGroupKind.Unknown => precise ? k.ToString() : "0",
            _ => null,
        };
        var noVatKinds = table.Groups.Where(g => g.Kind != OcrVatGroupKind.Standard7).Select(g => g.Kind).Distinct().ToList();
        string BucketText(string b) => b switch
        {
            "7" => KindText(OcrVatGroupKind.Standard7),
            // กลุ่มไม่มี VAT ทุกกลุ่มเป็นชนิดเดียวที่รู้ความหมาย ⇒ เรียกชื่อชนิดนั้น · ไม่งั้น (ไม่รู้ว่ายกเว้น/0%) เรียกรวม
            "0" => noVatKinds.Count == 1 && noVatKinds[0] != OcrVatGroupKind.Unknown ? KindText(noVatKinds[0]) : "ไม่มี VAT (ยกเว้น/0%)",
            _ => KindText(Enum.Parse<OcrVatGroupKind>(b)),
        };
        var problems = new List<string>();
        var buckets = table.Groups.Select(g => Bucket(g.Kind)!).Distinct().ToList();
        foreach (var b in buckets)
        {
            var mine = planned.Where(l => Bucket(KindOfRate(l.VatRate)) == b).ToList();
            var doc = mine.Sum(l => l.NetAmount);
            var paper = table.Groups.Where(g => Bucket(g.Kind) == b).Sum(g => g.Net);
            if (Math.Abs(doc - paper) > Math.Max(0.02m, 0.01m * mine.Count))
                problems.Add($"{BucketText(b)} เอกสาร {doc:N2} · กระดาษ {paper:N2}");
        }
        var stray = planned.Where(l => l.NetAmount != 0m && (Bucket(KindOfRate(l.VatRate)) is not string sb || !buckets.Contains(sb))).ToList();
        if (stray.Count > 0)
            problems.Add($"บรรทัดอัตราที่ตารางไม่มี {stray.Count} บรรทัด ({string.Join("/", stray.Select(l => RateText(l.VatRate)).Distinct())}) รวม {stray.Sum(l => l.NetAmount):N2}");
        return problems.Count == 0 ? null
            : $"{MismatchLead}: {string.Join(" · ", problems)} — แก้อัตรา VAT รายบรรทัดตามคอลัมน์รหัส ภ.พ. ({table.Evidence})";
    }

    /// <summary>หมายเหตุสแกนที่ข้อ <c>[Σ-GAP]</c> ของด่านนี้ (ตัวเลขของบรรทัด<b>ตอนสร้าง</b>) ถูกแทนด้วยผลตรวจ<b>สด</b>บนบรรทัดเอกสารตอนนี้ —
    /// ฝ่ายค้าน afbb0956 P2-4 (แบบเดียวกับข้อเกินกระดาษของ 7c782d24): ผู้ใช้แก้อัตราตามคอลัมน์รหัสแล้ว หมายเหตุเก่าต้องไม่ค้างหยุดการอนุมัติเอง/LINE
    /// และไม่ขอ "รับทราบ" ทุกครั้ง · <paramref name="liveMismatch"/> = ผลของ <see cref="TableMismatch"/> บนบรรทัดตอนนี้ (null = ตรงแล้ว) ·
    /// <paramref name="checkedLive"/> = false (ตรวจสดไม่ได้) ⇒ คืนหมายเหตุเดิม (ปลอดภัย: หยุดต่อ)</summary>
    public static string? LiveNotes(string? processingNotes, string? liveMismatch, bool checkedLive)
    {
        if (!checkedLive || string.IsNullOrEmpty(processingNotes)) return processingNotes;
        var stale = OcrLineBuildNotes.GapTag + " " + MismatchLead;
        var kept = processingNotes.Split('\n').Where(l => !l.TrimStart().StartsWith(stale, StringComparison.Ordinal)).ToList();
        if (liveMismatch is not null) kept.Add(OcrLineBuildNotes.GapTag + " " + liveMismatch);
        return string.Join("\n", kept);
    }

    /// <summary>คำที่บอกว่าแถวคือ "ส่วนลด" ไม่ใช่สินค้า (Makro พิมพ์แถวส่วนลดในคอลัมน์ราคา ไม่มีรหัส ภ.พ. ลงท้าย "(*1.0)")</summary>
    private static readonly Regex DiscountLabel = new(
        @"ส่วนลด|ลด|discount|\bslab\d*|\([ \t]*\*[ \t]*\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>ข้อความต้นของหมายเหตุ "ตารางชนะ — ใช้บรรทัดสรุปตามตาราง" (ไม่มีแท็ก · ผู้เรียกต่อท้ายแท็ก <see cref="OcrTotalDecomposer.NoItemsTag"/>)</summary>
    public const string TableSummaryLead = "รายการที่อ่านได้กระทบกับตารางรหัส ภ.พ. ของกระดาษไม่ได้";

    /// <summary>
    /// **ตัดสินว่าบรรทัดเอกสารของใบนี้มาจากไหน** — ตัวตัดสินเดียวของ "รายการ หรือ ตารางรหัส ภ.พ." (pure · service + replay harness)
    ///
    /// <para><b>คำตัดสินเจ้าของ 2026-10-16 (ภาคต่อของ DECISIONS ข้อ 153 — แทนข้อยกเว้น "ครบทุกหน้า ⇒ คงรายการ" ของ 6b9bf926 P2-5)</b>:
    /// "ถ้าตารางสินค้าตรงกับยอดรวม ยึด รายการสินค้าทั้งหมด · แต่ถ้าไม่ตรงและตารางภพ ตรงกับยอดรวม ให้ยึดตาราง ภพ"</para>
    /// <list type="number">
    /// <item><b>รายการลงยอด</b> (<see cref="Plan"/> = Applied — Σ รายการ − ส่วนลด = ตาราง/ยอดรวม · หรือหลังแปลงแถวส่วนลดที่ engine คืนเป็นสินค้า)
    ///   ⇒ คงรายการทั้งหมด + อัตรา/ส่วนลดต่อรหัสตามตาราง</item>
    /// <item><b>รายการไม่ลงยอด แต่ตารางลงยอด</b> (แถว "รวม" ของตาราง = ยอดรวมที่พิสูจน์แล้ว · VAT ตาราง = VAT ที่พิมพ์) ⇒ <b>บรรทัดสรุปต่อรหัสตามตาราง</b> —
    ///   ไม่ว่าเลขหน้าจะบอกว่าครบหรือไม่ · รายการต่างผัง/โครงการ ⇒ ใช้ของที่ยอดมากสุด + หมายเหตุรายการเดิม (ไม่บล็อก — <see cref="MixedDimensionsNote"/>)
    ///   · หน้าที่มีแต่แถวส่วนลด (<see cref="AllDiscountRows"/>) อยู่ในข้อนี้ (ตารางหักส่วนลดแล้ว ⇒ ไม่ลงเป็นสินค้า · ไม่หักซ้ำ)</item>
    /// <item><b>ไม่ลงทั้งคู่</b> ⇒ คงรายการ + [Σ-GAP] (ตัวกระทบยอด/ด่าน <see cref="TableMismatch"/> ฟ้อง) — ไม่แต่งตัวเลข</item>
    /// </list>
    /// <para>ยกเว้นที่คงไว้ (ตัดสินแทนเจ้าของรอบก่อน): หัก ณ ที่จ่าย / ผูกบรรทัด PO ⇒ ผู้เรียกปิด <paramref name="tableLinesAllowed"/> ⇒ ข้อ 3 ·
    /// ไม่มีตารางรหัส ภ.พ. ผสม ⇒ <see cref="OcrVatCodeLineSource.Items"/> + ตัวกระทบยอดสูตรเดิมทุกตัวอักษร</para>
    /// </summary>
    /// <param name="lines">บรรทัดตามลำดับเดิม (ยอดก่อนลดตามที่พิมพ์ · อัตราที่ตั้งแล้ว · รายละเอียด · ผัง/โครงการ)</param>
    /// <param name="headerSub">ยอดก่อน VAT หัวสแกน</param>
    /// <param name="headerVat">VAT หัวใบ</param>
    /// <param name="headerTotal">ยอดรวมทั้งสิ้นหัวใบ (ยอดที่ยึดแล้ว)</param>
    /// <param name="headerDiscount">ส่วนลดท้ายบิลบนกระดาษ (0 = ไม่มี)</param>
    /// <param name="pricesIncludeVatPreset">เอกสารเป็นราคารวม VAT อยู่แล้ว (บรรทัด e-Tax ติดธง ฯลฯ)</param>
    /// <param name="tableLinesAllowed">ผู้เรียกยอมให้แทนรายการด้วยบรรทัดสรุปได้ (ไม่มีหัก ณ ที่จ่าย · ไม่ผูกบรรทัด PO)</param>
    /// <param name="paperTotal">ยอดรวมทั้งสิ้นที่<b>กระดาษพิสูจน์</b> (<see cref="OcrTotalAnchor.Find"/> Proven/Confirmed) — ด่าน "ตารางลงยอด" เทียบกับค่านี้ได้
    /// เมื่อหัวสแกนที่ persist ไว้ยังผิด (สแกนก่อนขั้นยึดยอด: 11,846.50) · null = ไม่มี (เทียบหัวใบอย่างเดียว)</param>
    /// <param name="paperVat">VAT ที่พิมพ์ซึ่งปิดยอด <paramref name="paperTotal"/></param>
    public static OcrVatCodeDecision Decide(
        IReadOnlyList<OcrVatCodeLineInput> lines, string? rawText, OcrVatGroupTable table,
        decimal headerSub, decimal headerVat, decimal headerTotal, decimal headerDiscount,
        bool pricesIncludeVatPreset, bool tableLinesAllowed,
        decimal? paperTotal = null, decimal? paperVat = null)
    {
        var all = Enumerable.Range(0, lines.Count).ToList();
        var first = Analyze(lines, all, rawText, table, headerSub, headerVat, headerTotal, headerDiscount, pricesIncludeVatPreset, null);
        if (first.Plan.Verdict != OcrVatCodeVerdict.Unreconciled) return first;

        // ── ข้อ 1 (ต่อ): แถวส่วนลดที่ engine คืนเป็นสินค้ายอดบวก ⇒ ส่วนลดเอกสาร — รายการที่เหลือต้องลงตารางพอดี ไม่งั้นทิ้งการแปลง ──
        var discountOnly = AllDiscountRows(lines, rawText, table, headerDiscount);
        if (!discountOnly && DiscountRows(lines, rawText, table, headerDiscount) is { } conv)
        {
            var keep = all.Where(i => !conv.Indexes.Contains(i)).ToList();
            var named = conv.Named.Select(nd => (Line: keep.IndexOf(nd.Line), nd.Amount)).Where(nd => nd.Line >= 0).ToList();
            var alt = Analyze(lines, keep, rawText, table, headerSub, headerVat, headerTotal, conv.Discount, pricesIncludeVatPreset, named);
            if (alt.Plan.Applied)
            {
                var rowsSum = conv.Indexes.Sum(i => lines[i].PrintedAmount);
                var partial = conv.Discount - rowsSum > HeaderTol;
                return alt with
                {
                    DiscountRows = conv.Indexes,
                    Note = $"แถวส่วนลด {conv.Indexes.Count} แถวที่ engine อ่านเป็นสินค้า (รวม {rowsSum:N2}) "
                        + (partial
                            ? $"เป็นส่วนหนึ่งของรวมส่วนลดบนกระดาษ {conv.Discount:N2} (แถวที่เหลืออยู่หน้าอื่น) — ไม่ลงเป็นสินค้า และไม่หักซ้ำ: ใช้ส่วนลดของเอกสาร {conv.Discount:N2} ตามกระดาษ"
                            : $"แปลงเป็นส่วนลดของเอกสาร {conv.Discount:N2}")
                        + (named.Count > 0 ? $" · ส่วนลดที่ระบุชื่อสินค้า {named.Count} แถวลงบรรทัดสินค้านั้น" : "")
                        + " — ส่วนลดแบ่งต่อรหัส ภ.พ. ให้ตรงตาราง",
                };
            }
        }

        // ── ข้อ 2: รายการไม่ลงยอด แต่ตารางลงยอด ⇒ ตารางชนะ (ไม่ดูเลขหน้า · ไม่บล็อกเพราะผัง/โครงการต่างกัน) ──
        if (tableLinesAllowed
            && SummaryInsteadOfItems(first.Plan, table, headerVat, headerTotal, paperTotal, paperVat) is { Count: > 0 } groups)
        {
            var used = lines.Where(l => l.PrintedAmount != 0m).ToList();
            var account = LargestShare(used, l => l.AccountId);
            var project = LargestShare(used, l => l.ProjectId);
            var mixed = MixedDimensionsNote(used);
            var pages = OcrPageSet.Read(rawText);
            var allPages = pages.DeclaredPages is int m && pages.MissingPages.Count == 0;
            var why = discountOnly
                ? $"หน้านี้มีแต่แถวส่วนลด {used.Count} แถว (รวม {used.Sum(l => l.PrintedAmount):N2}"
                    + (headerDiscount > 0m ? $" ⊂ รวมส่วนลดบนกระดาษ {headerDiscount:N2}" : "")
                    + ") ไม่มีรายการสินค้า — ตารางรหัส ภ.พ. หักส่วนลดแล้ว ⇒ แถวส่วนลดไม่ลงเป็นสินค้าและไม่หักซ้ำ"
                : $"{TableSummaryLead} [{first.Plan.Note}]"
                    + (allPages ? $" (กระดาษบอกว่าไฟล์ครบทุกหน้า {(pages.DeclaredPages == 1 ? "หน้า 1 จาก 1" : $"หน้า 1–{pages.DeclaredPages} จาก {pages.DeclaredPages}")} — รายการที่ไม่ลงยอดคือแถวที่อ่านผิด/ขาด)" : "");
            return first with
            {
                Source = OcrVatCodeLineSource.TableSummary,
                TableLines = groups,
                SummaryAccountId = account,
                SummaryProjectId = project,
                DiscountOnly = discountOnly,
                MixedDimensionsNote = mixed,
                Note = $"{why} — ระบบใช้บรรทัดสรุปตามตาราง {groups.Count} บรรทัด (ยอดและ VAT ตรงกระดาษ: "
                    + string.Join(" · ", groups.Select(g => $"รหัส {g.PaperCode} {KindText(g.Kind)} ฐาน {g.Net:N2} VAT {g.Vat:N2}"))
                    + $" · รวม {table.Gross:N2})"
                    + (discountOnly ? ""
                        : $" · สิ่งที่ถูกแทน: รายการสินค้า {lines.Count} แถวที่อ่านได้ไม่ถูกลงเป็นบรรทัด ⇒ ไม่มีรายการย่อยสำหรับตัดสต็อก "
                          + "(หากต้องตัดสต็อก กรุณาบันทึกรายการย่อยเอง)")
                    + (mixed is null
                        ? " · ผังบัญชี" + (project.HasValue ? "และโครงการ" : "") + "ของบรรทัดสรุป = ของรายการ (ทุกรายการใช้ร่วมกัน — ไม่มีอะไรหาย)"
                        : " · ผังบัญชี/โครงการของบรรทัดสรุป = ของรายการที่ยอดมากสุด (ดูรายการเดิมในหมายเหตุ)"),
            };
        }
        // ── ข้อ 3: ไม่ลงทั้งคู่ ⇒ คงรายการ (ตัวกระทบยอด/ด่าน TableMismatch เขียน [Σ-GAP]) ──
        return first;
    }

    /// <summary>หัวข้อความของหมายเหตุรายการเดิมที่มีหลายผัง/โครงการ (คำของเจ้าของ) — ตัวเดียวทั้งฝั่งเขียนและเทสต์</summary>
    public const string MixedDimensionsLead = "ตรวจแยกบรรทัดถ้าต้องการ";

    /// <summary>ค่าของมิติ (ผัง/โครงการ) ที่ยอดรวมมากสุดในรายการ — เท่ากันใช้ตัวที่มาก่อน (pure · deterministic)</summary>
    private static Guid? LargestShare(IReadOnlyList<OcrVatCodeLineInput> used, Func<OcrVatCodeLineInput, Guid?> key)
    {
        if (used.Count == 0) return null;
        return used.Select((l, i) => (Key: key(l), l.PrintedAmount, Index: i))
            .GroupBy(x => x.Key)
            .Select(g => (g.Key, Sum: g.Sum(x => Math.Abs(x.PrintedAmount)), First: g.Min(x => x.Index)))
            .OrderByDescending(g => g.Sum).ThenBy(g => g.First)
            .First().Key;
    }

    /// <summary>
    /// **หมายเหตุรายการเดิมเมื่อตารางชนะแต่รายการมีหลายผัง/โครงการ** (คำตัดสินเจ้าของ 2026-10-16 ภาคต่อ: "ไม่บล็อก · ใช้ของที่ยอดมากสุด · บอกรายการเดิม")
    /// — แทนเหตุคงรายการของฝ่ายค้าน f7c29c2c P1-2/P1-3 · null = ทุกรายการผัง/โครงการเดียวกัน (ไม่มีอะไรหาย)
    /// </summary>
    internal static string? MixedDimensionsNote(IReadOnlyList<OcrVatCodeLineInput> used)
    {
        var accounts = used.Select(l => l.AccountId).Distinct().Count();
        var projects = used.Select(l => l.ProjectId).Distinct().Count();
        if (accounts <= 1 && projects <= 1) return null;
        var what = accounts > 1 && projects > 1 ? "หลายผังและหลายโครงการ" : accounts > 1 ? "หลายผัง" : "หลายโครงการ";
        static string Label(string? label, Guid? id) => label ?? (id.HasValue ? id.Value.ToString()[..8] : "ผังหัวใบ");
        var items = used.Select(l =>
            $"{(string.IsNullOrWhiteSpace(l.Description) ? "(ไม่มีรายละเอียด)" : ShortText(l.Description!))} {l.PrintedAmount:N2}"
            + (accounts > 1 ? $" ผัง {Label(l.AccountLabel, l.AccountId)}" : "")
            + (projects > 1 ? $" โครงการ {(l.ProjectId.HasValue ? Label(l.ProjectLabel, l.ProjectId) : "ไม่ระบุ")}" : ""));
        return $"รายการเดิม {used.Count} บรรทัดมี{what} — {MixedDimensionsLead}: " + string.Join(" · ", items);
    }

    private static string ShortText(string s) { s = s.Trim(); return s.Length <= 40 ? s : s[..40] + "…"; }
    private static OcrVatCodeDecision Analyze(
        IReadOnlyList<OcrVatCodeLineInput> lines, IReadOnlyList<int> keep, string? rawText, OcrVatGroupTable table,
        decimal headerSub, decimal headerVat, decimal headerTotal, decimal discount, bool pricesIncludeVatPreset,
        IReadOnlyList<(int Line, decimal Amount)>? named)
    {
        // ฐานก่อน VAT หลังส่วนลดที่ใช้กระทบยอด — สูตรเดียวกับตัวสร้างบรรทัด (เดิมอยู่ใน OcrService.BuildScanLinesAsync)
        var netSub = headerSub;
        if (discount > 0m && headerTotal > 0m)
            netSub = OcrHeaderAmounts.NetSubTotal(headerSub, headerVat, headerTotal, discount);
        else if (headerSub <= 0m && headerTotal > 0m)
            netSub = Math.Max(0m, headerTotal - headerVat);
        var kept = keep.Select(i => lines[i]).ToList();
        var recon = OcrLineReconciler.Classify(kept.Sum(l => l.PrintedAmount), netSub, headerVat, headerTotal, discount);
        var incl = pricesIncludeVatPreset || recon.PricesIncludeVat;
        var plan = Plan(kept, rawText, table, headerVat, headerTotal, discount, incl, recon.TargetLineSum, named);
        return new OcrVatCodeDecision(OcrVatCodeLineSource.Items, keep, Array.Empty<int>(), discount, netSub, recon, incl,
            plan, Array.Empty<OcrVatGroup>(), null);
    }

    /// <summary>กลุ่มของตารางที่ใช้เป็นบรรทัดสรุปแทนรายการ (ข้อ 2 "ตารางลงยอด ⇒ ตารางชนะ") · ว่าง = ใช้ไม่ได้ (คงรายการ)
    /// <para>"ตารางลงยอด" = แถว "รวม" ของตาราง = ยอดรวมทั้งสิ้น และ VAT ตาราง = VAT — เทียบกับหัวใบ <b>หรือ</b>ค่าที่กระดาษพิสูจน์
    /// (<paramref name="paperTotal"/>/<paramref name="paperVat"/> จาก <see cref="OcrTotalAnchor.Find"/>) · 2026-10-16 Makro 2/2: หัวสแกนที่ persist ก่อนขั้นยึดยอด
    /// (11,846.50) ไม่ใช่เหตุให้ตารางที่ปิดยอด 10,980 ห้าทางแพ้</para></summary>
    internal static IReadOnlyList<OcrVatGroup> SummaryInsteadOfItems(
        OcrVatCodePlan plan, OcrVatGroupTable table, decimal headerVat, decimal headerTotal,
        decimal? paperTotal = null, decimal? paperVat = null)
    {
        if (plan.Verdict != OcrVatCodeVerdict.Unreconciled || !IsMixed(table) || !table.AllKnown)
            return Array.Empty<OcrVatGroup>();
        static bool Same(decimal a, decimal? b) => b is decimal x && x > 0m && Math.Abs(a - x) <= HeaderTol;
        var totalOk = Same(table.Gross, headerTotal) || Same(table.Gross, paperTotal);
        var vatOk = Math.Abs(table.Vat - headerVat) <= HeaderTol || (paperVat is decimal pv && Math.Abs(table.Vat - pv) <= HeaderTol);
        if (!totalOk || !vatOk) return Array.Empty<OcrVatGroup>();
        return table.Groups.Where(g => g.Net > 0m || g.Vat > 0m).ToList();
    }

    /// <summary>ยอด <paramref name="amount"/> พิมพ์อยู่บนบรรทัดของกระดาษที่มีคำบอกว่าเป็นส่วนลด (<see cref="DiscountLabel"/>) — หลักฐานจากกระดาษว่าแถวที่ engine คืนคือแถวส่วนลด
    /// (ไม่ใช่แค่คำในรายละเอียดที่ engine ส่งมา)</summary>
    private static bool OnDiscountLine(string? rawText, decimal amount)
        => amount > 0m && OcrPaperAmounts.Lines(rawText).Any(l => DiscountLabel.IsMatch(l)
            && OcrPaperAmounts.MoneyOn(l).Any(m => Math.Abs(m - amount) <= 0.005m));

    /// <summary>
    /// **ทุกแถวที่ engine คืนคือแถวส่วนลด** (2026-10-16 · Makro 2/2 เลขที่ 405901117163 หน้าที่ 2 จาก 2 — "ยอดรวมลงถูกแต่บอดราย รายการ") — กระดาษหน้าสุดท้ายของห้าง
    /// ที่ไม่มีแถวสินค้าเลย มีแค่แถวส่วนลด (30 · 60 · 10) + "รวมส่วนลด 228" (รวมส่วนลดของหน้าก่อนด้วย) + ตารางรหัส ภ.พ. ที่<b>หักส่วนลดแล้ว</b>
    /// <para>เงื่อนไข: ตารางผสม · ทุกแถวที่มียอดไม่มีรหัส ภ.พ. บนกระดาษ · ยอดบวก · แต่ละแถวมีคำว่า ลด/ส่วนลด/discount/SLAB/"(*n)" ในรายละเอียด <b>หรือ</b>
    /// ยอดพิมพ์บนบรรทัดส่วนลดของกระดาษ · กระดาษมีรวมส่วนลด ⇒ Σ ≤ รวมส่วนลด (รายการส่วนลดบางส่วนของหน้าสุดท้าย) · ไม่มีรวมส่วนลด ⇒ ต้องมีทั้งคำและบรรทัดบนกระดาษ</para>
    /// <para>ผล: ตัวตัดสินไม่ลองแปลงเป็นส่วนลดเอกสาร (ไม่มีสินค้าให้หัก) · ตารางลงยอด ⇒ บรรทัดสรุปตามตาราง (ข้อ 2) · สินค้าจริงที่ชื่อมีคำว่า "ลด" (นมลดไขมัน)
    /// มีรหัส ภ.พ. บนแถว / ยอดเกินรวมส่วนลด ⇒ ไม่เข้ารูป</para>
    /// </summary>
    internal static bool AllDiscountRows(
        IReadOnlyList<OcrVatCodeLineInput> lines, string? rawText, OcrVatGroupTable table, decimal headerDiscount)
    {
        if (lines.Count == 0 || !IsMixed(table)) return false;
        var codes = table.Groups.Select(g => g.PaperCode).ToHashSet(StringComparer.Ordinal);
        var coded = MatchRowCodes(lines, ReadRowCodes(rawText, codes));
        var used = Enumerable.Range(0, lines.Count).Where(i => lines[i].PrintedAmount != 0m).ToList();
        if (used.Count == 0 || used.Any(i => coded[i] is not null || lines[i].PrintedAmount < 0m)) return false;
        foreach (var i in used)
        {
            var labelled = DiscountLabel.IsMatch(lines[i].Description ?? "");
            var onPaper = OnDiscountLine(rawText, lines[i].PrintedAmount);
            if (headerDiscount > 0m ? !(labelled || onPaper) : !(labelled && onPaper)) return false;
        }
        return headerDiscount <= 0m || used.Sum(i => lines[i].PrintedAmount) <= headerDiscount + HeaderTol;
    }

    /// <summary>แถวส่วนลดที่ engine คืนเป็น "สินค้า" (คำตัดสินเจ้าของข้อ 3)</summary>
    internal sealed record DiscountConversion(IReadOnlyList<int> Indexes, decimal Discount, IReadOnlyList<(int Line, decimal Amount)> Named);

    /// <summary>หาแถวส่วนลดในบรรทัดที่ engine คืน — แถวที่<b>ไม่มีรหัส ภ.พ. บนกระดาษ</b> · ไม่มีอัตราตั้งไว้ · ยอดบวก และ
    /// (Σ = ส่วนลดบนกระดาษ) หรือ (มีคำว่า ลด/ส่วนลด/discount/SLAB/"(*n)" และ Σ = ส่วนลดบนกระดาษ หรือกระดาษไม่บอกส่วนลด) หรือ
    /// (มีคำบอกส่วนลด + ยอดพิมพ์บนบรรทัดส่วนลดของกระดาษ + Σ &lt; รวมส่วนลด — รายการส่วนลดบางส่วน · ส่วนลดเอกสาร = รวมส่วนลดของกระดาษ) · null = ไม่มี/ไม่แน่ใจ ·
    /// แถวส่วนลดที่ระบุชื่อสินค้า (คำในรายละเอียดของสินค้า<b>บรรทัดเดียว</b>อยู่ในข้อความแถวส่วนลด) ⇒ คืนเป็นส่วนลดของบรรทัดนั้น</summary>
    internal static DiscountConversion? DiscountRows(
        IReadOnlyList<OcrVatCodeLineInput> lines, string? rawText, OcrVatGroupTable table, decimal headerDiscount)
    {
        if (lines.Count < 2 || !IsMixed(table)) return null;
        var codes = table.Groups.Select(g => g.PaperCode).ToHashSet(StringComparer.Ordinal);
        var coded = MatchRowCodes(lines, ReadRowCodes(rawText, codes)).Select(c => c is not null).ToArray();
        var uncoded = Enumerable.Range(0, lines.Count)
            .Where(i => !coded[i] && lines[i].PresetRate is null && lines[i].PrintedAmount != 0m).ToList();
        if (uncoded.Count == 0 || uncoded.Any(i => lines[i].PrintedAmount < 0m)) return null;
        var labelled = uncoded.Where(i => DiscountLabel.IsMatch(lines[i].Description ?? "")).ToList();
        decimal Sum(IEnumerable<int> idx) => idx.Sum(i => lines[i].PrintedAmount);
        List<int>? pick = null;
        if (headerDiscount > 0m && Math.Abs(Sum(uncoded) - headerDiscount) <= HeaderTol) pick = uncoded;
        else if (labelled.Count > 0 && (headerDiscount <= 0m || Math.Abs(Sum(labelled) - headerDiscount) <= HeaderTol)) pick = labelled;
        // 2026-10-16 (b): รายการส่วนลด<b>บางส่วน</b>ของหน้าสุดท้าย — แถวที่มีคำบอกส่วนลด และยอดพิมพ์บนบรรทัดส่วนลดของกระดาษ · Σ &lt; รวมส่วนลดบนกระดาษ
        // (ส่วนที่เหลืออยู่หน้าก่อน) ⇒ ตัดแถวทิ้งแล้วใช้รวมส่วนลดของกระดาษ (ไม่บวกซ้ำ) · ผลต้องลงตารางพอดี (ผู้เรียกทิ้งการแปลงถ้าไม่ลง)
        else if (headerDiscount > 0m && labelled.Count > 0 && Sum(labelled) < headerDiscount - HeaderTol
                 && labelled.All(i => OnDiscountLine(rawText, lines[i].PrintedAmount))) pick = labelled;
        if (pick is null || pick.Count >= lines.Count) return null;
        var discount = headerDiscount > 0m ? headerDiscount : Sum(pick);

        // ส่วนลดที่เอ่ยชื่อสินค้า: คำ<b>เฉพาะตัว</b> (<see cref="DistinctiveTokens"/> — ไม่ใช่หน่วย/บรรจุภัณฑ์/คำทั่วไป) ในรายละเอียดของสินค้าที่เหลือ อยู่ในข้อความแถวส่วนลด
        // — ต้องชี้สินค้าได้บรรทัดเดียว · ชี้ไม่ได้/หลายบรรทัด ⇒ ไม่ระบุ (ส่วนลดแบ่งตามสัดส่วนในกลุ่ม) · ฝ่ายค้าน f7c29c2c P2-1: "ลด 20 น้ำยาล้างจาน ขวด"
        // เดิมชี้ "น้ำดื่ม ขวด" ด้วยคำว่า "ขวด"
        var products = Enumerable.Range(0, lines.Count).Where(i => !pick.Contains(i)).ToList();
        var named = new List<(int Line, decimal Amount)>();
        foreach (var d in pick)
        {
            var text = lines[d].Description ?? "";
            var hits = products.Where(p => DistinctiveTokens(lines[p].Description).Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
            if (hits.Count == 1 && lines[d].PrintedAmount <= lines[hits[0]].PrintedAmount)
                named.Add((hits[0], lines[d].PrintedAmount));
        }
        return new DiscountConversion(pick, discount, named);
    }

    /// <summary>คำหน่วย/บรรจุภัณฑ์/คำทั่วไปที่ไม่ชี้สินค้า (ฝ่ายค้าน f7c29c2c P2-1) — "ขวด" อยู่ในทั้งน้ำดื่มและน้ำยาล้างจาน</summary>
    private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        // หน่วย/บรรจุภัณฑ์ไทย
        "ขวด", "แพ็ค", "แพค", "แพ็ก", "ถุง", "กล่อง", "ชิ้น", "ซอง", "กระป๋อง", "กระปุก", "หลอด", "ลัง", "โหล", "แผง", "ม้วน", "แท่ง", "ก้อน", "ห่อ", "อัน",
        "ใบ", "ตัว", "คู่", "ชุด", "แกลลอน", "ถัง", "กิโล", "กรัม", "ลิตร", "มล.", "ซม.", "เมตร", "หน่วย", "รายการ",
        // คำทั่วไปบนแถวส่วนลด/สินค้า
        "ส่วนลด", "ลดราคา", "สินค้า", "โปรโมชั่น", "โปรฯ", "พิเศษ", "ราคา", "บาท", "ขนาด", "ฟรี", "แถม",
        // อังกฤษ
        "pack", "pck", "pcs", "piece", "pieces", "each", "bottle", "btl", "box", "bag", "can", "unit", "units", "set", "pair",
        "carton", "ctn", "dozen", "roll", "tube", "item", "items", "disc", "discount", "promo", "promotion", "free", "size", "price",
    };

    /// <summary>คำที่ชี้สินค้าได้ — ไม่อยู่ใน <see cref="GenericTokens"/> · ไม่มีตัวเลข · ไทย ≥ 3 ตัวอักษร หรือละติน ≥ 4 ตัวอักษร</summary>
    internal static IEnumerable<string> DistinctiveTokens(string? s)
        => Regex.Split(s ?? "", @"[\s()\[\],/:;*]+")
            .Where(t => !t.Any(char.IsDigit) && !GenericTokens.Contains(t)
                && (t.Any(c => c >= '\u0E00' && c <= '\u0E7F') ? t.Length >= 3 : t.Count(char.IsLetter) >= 4));

    /// <summary>จับคู่รหัสที่พิมพ์บนแถวสินค้ากับบรรทัด — ยอดตรงถึงสตางค์ตามลำดับบนกระดาษ (ตัวเดียวของ <see cref="Plan"/> และ <see cref="DiscountRows"/>)</summary>
    private static string?[] MatchRowCodes(IReadOnlyList<OcrVatCodeLineInput> lines, IReadOnlyList<(decimal Amount, string Code)> marks)
    {
        var result = new string?[lines.Count];
        var j = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var a = Math.Round(lines[i].PrintedAmount, 2, R);
            for (var k = j; k < marks.Count; k++)
            {
                if (marks[k].Amount != a) continue;
                result[i] = marks[k].Code;
                j = k + 1;
                break;
            }
        }
        return result;
    }

    /// <summary>แบ่ง <paramref name="total"/> (≥ 0) ตามสัดส่วน <paramref name="weights"/> (≥ 0) ด้วยวิธี<b>เศษเหลือมากสุด</b> (largest remainder) ทีละสตางค์ —
    /// ฝ่ายค้าน afbb0956 P2-2: เดิม "ปัดทุกตัว เศษลงตัวสุดท้าย" ⇒ หกบรรทัด 10.00 ส่วนลด 0.03 ได้ตัวสุดท้าย −0.02 · ตอนนี้ทุกส่วน ≥ 0 และ ≤ ส่วนของตัวเองปัดขึ้น ·
    /// Σ = total ทุกสตางค์ · เท่ากันใช้ลำดับก่อน</summary>
    internal static decimal[] SplitProportional(decimal total, IReadOnlyList<decimal> weights)
    {
        var result = new decimal[weights.Count];
        var baseSum = weights.Where(w => w > 0m).Sum();
        if (total <= 0m || baseSum <= 0m || weights.Count == 0) return result;
        var cents = (long)Math.Round(total * 100m, 0, R);
        var exact = weights.Select(w => w > 0m ? cents * w / baseSum : 0m).ToArray();
        var floor = exact.Select(e => (long)Math.Floor(e)).ToArray();
        var left = cents - floor.Sum();
        foreach (var x in Enumerable.Range(0, weights.Count).OrderByDescending(x => exact[x] - floor[x]).ThenBy(x => x))
        {
            if (left <= 0) break;
            if (weights[x] <= 0m) continue;
            floor[x]++; left--;
        }
        for (var x = 0; x < weights.Count; x++) result[x] = floor[x] / 100m;
        return result;
    }

    /// <summary>ชุดดัชนีที่ผลรวมยอดห่าง <paramref name="target"/> ไม่เกิน <paramref name="tolCents"/> สตางค์ — หยุดเมื่อพบครบ <paramref name="limit"/> ชุด (พอรู้ว่า "ทางเดียว" ไหม)</summary>
    private static List<HashSet<int>> SubsetsSumming(IReadOnlyList<decimal> amounts, decimal target, int limit, long tolCents = 0)
    {
        var cents = amounts.Select(a => (long)Math.Round(a * 100m, 0, R)).ToArray();
        var goal = (long)Math.Round(target * 100m, 0, R);
        var found = new List<HashSet<int>>();
        if (goal < 0 || cents.Any(c => c < 0)) return found;
        // ผลรวมคงเหลือของบรรทัดที่ยังไม่พิจารณา — ตัดกิ่งที่ไปไม่ถึงเป้า
        var suffix = new long[cents.Length + 1];
        for (var i = cents.Length - 1; i >= 0; i--) suffix[i] = suffix[i + 1] + cents[i];
        var chosen = new Stack<int>();
        void Walk(int i, long acc)
        {
            if (found.Count >= limit) return;
            if (i == cents.Length)
            {
                if (Math.Abs(acc - goal) <= tolCents) found.Add(chosen.ToHashSet());
                return;
            }
            if (acc > goal + tolCents || acc + suffix[i] < goal - tolCents) return;
            chosen.Push(i);
            Walk(i + 1, acc + cents[i]);
            chosen.Pop();
            Walk(i + 1, acc);
        }
        Walk(0, 0);
        return found;
    }

    private static string KindText(OcrVatGroupKind k) => k switch
    {
        OcrVatGroupKind.Standard7 => "มี VAT 7%",
        OcrVatGroupKind.ZeroRated => "อัตรา 0%",
        OcrVatGroupKind.Exempt => "ยกเว้น VAT",
        _ => "ไม่รู้ความหมาย",
    };

    private static string RateText(decimal r) => r == ThaiVatTypeRule.ExemptRate ? "ยกเว้น" : $"{r:0.##}%";
}
