using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Accounting.Services.Implementations.Ocr;

namespace Accounting.Helpers;

/// <summary>ตัวเลขหนึ่งตัวบนข้อความกระดาษ (ตามลำดับที่อ่าน) — <see cref="IsPercent"/> = มี "%" ตามหลัง ·
/// <see cref="Start"/>/<see cref="End"/> = ตำแหน่งในข้อความ (End รวม "%") · <see cref="RowIndexLike"/> = ตัวเลขตัวแรกของบรรทัดที่มีข้อความตามหลัง
/// (เลขลำดับแถว "1 หมูสับ …" — ไม่ใช่จำนวน · ฝ่ายค้าน 658e3705 P2-4) · <see cref="UnitAfter"/> = มีคำหน่วยนับตามหลังทันที ("2 ด้าม" · "3,611 หน่วย")</summary>
public readonly record struct OcrPaperNumber(
    decimal Value, bool IsPercent, int Start = 0, int End = 0, bool RowIndexLike = false, bool UnitAfter = false);

/// <summary>ข้อความกระดาษที่แยกแล้วสำหรับ <see cref="OcrLineQuantityGuard.Decide"/> (แยกครั้งเดียวต่อสแกน) — ตัวเลขตามลำดับ + ธงจาก<b>หัวคอลัมน์ตาราง</b>
/// (ช่วงข้อความไม่มีตัวเลขที่มีคำบทบาทคอลัมน์ ≥ 3 บทบาท: จำนวน · หน่วย · ราคา · ส่วนลด · ยอดเงิน · รายการ)</summary>
public sealed class OcrLinePaper
{
    public static readonly OcrLinePaper Empty = new("", Array.Empty<OcrPaperNumber>(), false, false, false);

    internal OcrLinePaper(string text, IReadOnlyList<OcrPaperNumber> numbers, bool headerDiscount, bool headerWithholding, bool headerDeposit)
    {
        Text = text;
        Numbers = numbers;
        HeaderHasDiscount = headerDiscount;
        HeaderHasWithholding = headerWithholding;
        HeaderHasDeposit = headerDeposit;
    }

    /// <summary>ข้อความกระดาษตามที่ได้รับ (ตำแหน่งของ <see cref="Numbers"/> อ้างข้อความนี้)</summary>
    public string Text { get; }
    public IReadOnlyList<OcrPaperNumber> Numbers { get; }
    /// <summary>หัวคอลัมน์มีคำ ส่วนลด/discount/ลด — หลักฐานว่าตัวเลขระหว่างราคากับยอดเป็นส่วนลด</summary>
    public bool HeaderHasDiscount { get; }
    /// <summary>หัวคอลัมน์มีคำ หัก ณ ที่จ่าย/ภาษีหัก/WHT/withholding (ป้ายของ <see cref="PaperWhtReader"/>)</summary>
    public bool HeaderHasWithholding { get; }
    /// <summary>หัวคอลัมน์มีคำมัดจำ/เงินจอง/advance/deposit/งวดก่อน (คำของ <see cref="OcrDepositMarker"/> + advance/งวดก่อน)</summary>
    public bool HeaderHasDeposit { get; }
}

/// <summary>สิ่งที่ตัวกันจำนวนระเบิดทำกับบรรทัดหนึ่ง</summary>
public enum OcrLineQuantityAction
{
    /// <summary>ไม่แตะ (ลงตัวอยู่แล้ว · ข้อมูลไม่พอ · ยอด ≤ 0)</summary>
    Keep = 0,
    /// <summary>ไม่มีราคาต่อหน่วย แต่มีจำนวน + ยอด ⇒ ราคา = ยอด ÷ จำนวน (เติมช่องว่าง ไม่ได้เปลี่ยนค่าที่อ่านได้)</summary>
    FillUnitPrice,
    /// <summary>กระดาษพิมพ์ส่วนลดของบรรทัดนี้ <b>พร้อมหลักฐานว่าเป็นส่วนลด</b> (หัวคอลัมน์ "ส่วนลด" หรือป้าย ลด/ส่วนลด ติดตัวเลข · % ที่ไม่ใช่อัตรา
    /// หัก ณ ที่จ่าย) และ round(จำนวน × ราคา) − ส่วนลด = ยอด ทุกสตางค์ ⇒ ลงเป็นส่วนลดของบรรทัด · จำนวน/ราคาคงตามกระดาษ · หมายเหตุแจ้ง (ไม่บล็อก)</summary>
    PrintedDiscount,
    /// <summary>(เคส ก เดิม) ราคาต่อหน่วย ≈ ยอดทั้งบรรทัดทั้งที่จำนวน &gt; 1 ⇒ engine อ่านยอดมาใส่ช่องราคา ⇒ ราคา = ยอด ÷ จำนวน
    /// · จำนวนพิมพ์คู่หน่วยนับบนกระดาษ (มิเตอร์) = พิสูจน์แล้ว ⇒ หมายเหตุแจ้ง · ไม่งั้นแท็กบล็อก</summary>
    FixUnitPrice,
    /// <summary>(เคส ข / เดิม) จำนวนที่ engine อ่านไม่ได้พิมพ์บนแถวของกระดาษ (หรือเป็นยอดที่อ่านหลงคอลัมน์) ⇒ จำนวน = ยอด ÷ ราคา
    /// · จำนวนใหม่พิมพ์อยู่บนแถว = พิสูจน์แล้ว ⇒ หมายเหตุแจ้ง · ไม่งั้นแท็กบล็อก</summary>
    FixQuantity,
    /// <summary>จำนวนที่ engine อ่าน<b>พิมพ์อยู่บนแถวของกระดาษ</b> แต่ จำนวน × ราคา ≠ ยอด และกระดาษไม่มีอะไรอธิบาย ⇒ คงจำนวน/ราคาตามกระดาษ (ห้ามแต่งจำนวน) +
    /// <b>ห้ามเก็บบรรทัดที่ไม่ลงตัว</b> (ฝ่ายค้าน 658e3705 P2-5 — เปิดแก้แล้วบันทึก ComputeLineAmounts คิดใหม่ = ยอดเลื่อน): ส่วนต่างบวก ⇒ ส่วนลดที่ระบุชัด ·
    /// ส่วนต่างลบ ⇒ ราคา = ยอด ÷ จำนวน · ยอดบรรทัด = ยอดบนกระดาษเสมอ · แท็กบล็อก "ส่วนต่างที่กระดาษไม่บอก — ตรวจ"</summary>
    KeepPrintedGap,
    /// <summary>ตัวเลขบนแถวลงตัวเป็นส่วนต่างพอดี แต่<b>ไม่มีหลักฐานว่าเป็นส่วนลด</b> (ไม่มีหัวคอลัมน์/ป้ายส่วนลด · % เท่าอัตราหัก ณ ที่จ่าย · ตัวเลขเปล่า
    /// เช่นรหัสสินค้า "SKU 50" · อัตรา VAT ไม่มี %) ⇒ เก็บแบบ <see cref="KeepPrintedGap"/> (ส่วนต่างเป็นส่วนลดที่ระบุชัด ยอดตามกระดาษ) + แท็กบล็อก</summary>
    UnevidencedDiscount,
    /// <summary>แถว/หัวคอลัมน์มีคำ หัก ณ ที่จ่าย/มัดจำ ⇒ ส่วนต่าง<b>ไม่ใช่ส่วนลด</b> (ภาษีหัก ณ ที่จ่ายไม่ลดมูลค่าบรรทัด · มัดจำต้องล้างบัญชีมัดจำ ไม่ใช่ลดราคา) ⇒
    /// ยอดบรรทัด = จำนวน × ราคาที่พิมพ์ (ก่อนหัก) · ส่วนที่หักเป็นของเจ้าของเดิม (ภาษีหัก ณ ที่จ่ายของเอกสาร · การหักมัดจำของเอกสาร) · แท็กบล็อก</summary>
    DeductionRow,
}

/// <summary>ผลของ <see cref="OcrLineQuantityGuard.Decide"/> — ค่าที่ผู้เรียกต้องเขียนลงบรรทัด (null = ไม่เปลี่ยนช่องนั้น)</summary>
/// <param name="Note">ข้อความเมื่อระบบเปลี่ยน/ตีความค่าบนบรรทัด — ขึ้นต้น <see cref="OcrLineQuantityGuard.Tag"/> (บล็อกการอนุมัติเอง) หรือ
/// <see cref="OcrLineQuantityGuard.NoteTag"/> (แจ้งอย่างเดียว) · ผู้เรียกต้องส่งต่อให้คนเห็น (<see cref="OcrLineQuantityGuard.MergeNotes"/>) · null = ไม่มีอะไรต้องบอก</param>
/// <param name="Amount">ยอดบรรทัดใหม่ (เฉพาะ <see cref="OcrLineQuantityAction.DeductionRow"/> = จำนวน × ราคาก่อนหัก)</param>
public sealed record OcrLineQuantityDecision(
    OcrLineQuantityAction Action, decimal? Quantity, decimal? UnitPrice, decimal? LineDiscount, string? Note, decimal? Amount = null)
{
    /// <summary>หมายเหตุนี้หยุดการอนุมัติเอง (<see cref="OcrPostingReadiness.BlockingTags"/>)</summary>
    public bool Blocking => Note is not null && Note.StartsWith(OcrLineQuantityGuard.Tag, StringComparison.Ordinal);
}

/// <summary>บรรทัดเอกสาร<b>ตอนนี้</b> สำหรับตรวจสดของ <see cref="OcrLineQuantityGuard.LiveNotes"/> (ค่าที่บันทึกใน DocumentLine)</summary>
public readonly record struct OcrLiveLine(
    decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal DiscountAmount, decimal Amount, decimal VatRate);

/// <summary>
/// **ตัวกัน "จำนวนระเบิด" ของบรรทัดสแกน + หลักฐานจากแถวบนกระดาษ** (pure · ตัวตัดสินตัวเดียวของ <c>OcrService.SanitizeVatSplitArtifacts</c> ข้อ 1b
/// และ replay harness)
///
/// <para>═══ ที่มา (ผู้ใช้รายงาน 2026-10-16 · RE-20261000111 พรามี่ ควอลิตี้) ═══ "จำนวนสินค้า ทำไมระบบเปลี่ยนไปจากเอกสาร ในเอกสารขึ้นเป็นส่วนลด
/// ตามรายการ ระบบต้องจัดการบันทึกให้ถูกส่วน ไม่ใช่มาปรับจำนวนรายการ" — กระดาษพิมพ์ จำนวน 1.00 · ราคา 1,330.00 · ส่วนลด 293.00 · VAT 7% ·
/// มูลค่ารวมภาษี 1,037.00 · engine คืนบรรทัด (1, 1,330, 1,037) โดยไม่มีช่องส่วนลด ⇒ ตัวกันเดิมหารจำนวนใหม่ 1,037 ÷ 1,330 = 0.78 เงียบ ๆ</para>
///
/// <para>═══ ฝ่ายค้าน 73573e9a..658e3705 (2026-10-16 · ทีม OCR 6) ═══ รุ่นแรกตีตัวเลขใดก็ได้ที่ลงตัวเป็นส่วนลด (Note = null · อนุมัติเองได้):
/// คอลัมน์หัก ณ ที่จ่าย 3%/5% · "หักมัดจำ 30,000" (มัดจำไม่เคยถูกล้าง) · รหัสสินค้า "SKU 50" · อัตรา VAT ไม่มี % ⇒ ทุกตัวเป็น "ส่วนลด" ·
/// เลขลำดับแถวถูกนับเป็นจำนวน · KeepPrintedGap เก็บบรรทัดที่ไม่ลงตัว (บันทึกซ้ำเลื่อน) · แก้แล้วจำนวนที่พิสูจน์ได้ยังบล็อกทุกเดือน</para>
///
/// <para>═══ กติกา (ลำดับ) ═══
/// <list type="number">
/// <item>ส่วนลดที่เอกสารประกาศแล้ว (<see cref="OcrEtaxLineNormalizer.ProvenLineDiscount"/>) อธิบายยอดได้ = ไม่แตะ</item>
/// <item>หาแถว: ราคาต่อหน่วย → (ตัวเลข ≤ <see cref="MaxNumbersBetween"/> ตัว) → ยอดบรรทัด · ตัวเลข d ระหว่างนั้นที่ทำให้ round(จำนวน × ราคา) − d = ยอด
///   ทุกสตางค์ (หรือ d% ของยอดก่อนลด = ส่วนต่าง · ไม่รับ 7% · ไม่รับตัวเลขที่เท่า VAT ของบรรทัด) จำแนกตาม<b>หลักฐาน</b> (ใกล้สุดก่อน):
///   ป้ายติดตัวเลข (หัก ณ ที่จ่าย/มัดจำ ⇒ ส่วนหัก · ลด/ส่วนลด ⇒ ส่วนลด) → คำในแถว (หัก ณ ที่จ่าย/มัดจำ ⇒ ส่วนหัก) → หัวคอลัมน์
///   (ส่วนลดอย่างเดียว ⇒ ส่วนลด · หัก/มัดจำอย่างเดียว ⇒ ส่วนหัก · ทั้งคู่ ⇒ ไม่รู้) → ไม่มีหลักฐาน: % ที่ไม่ใช่อัตรา <see cref="ThaiWhtRateTable"/> ⇒ ส่วนลด ·
///   % เท่าอัตราหัก ณ ที่จ่าย / ตัวเลขเปล่า ⇒ ไม่รู้</item>
/// <item>ส่วนลดมีหลักฐาน ⇒ <see cref="OcrLineQuantityAction.PrintedDiscount"/> + หมายเหตุแจ้ง (ก่อนเกณฑ์ ±2% — ส่วนลด 15 บาทของ 1,000 ก็จริง)</item>
/// <item>ส่วนหัก ⇒ <see cref="OcrLineQuantityAction.DeductionRow"/> (ยอด = จำนวน × ราคา · แท็กบล็อก)</item>
/// <item>ไม่รู้ ⇒ <see cref="OcrLineQuantityAction.UnevidencedDiscount"/> (แท็กบล็อก)</item>
/// <item>ลงตัวในเกณฑ์เดิม (±2% หรือ ฿1) = ไม่แตะ</item>
/// <item>(เคส ก) ราคา ≈ ยอด · จำนวน &gt; 1 ⇒ แก้ราคา</item>
/// <item><b>จำนวนพิมพ์อยู่บนแถว</b> (ติดก่อนราคาโดยมีแค่คำหน่วยนับคั่น และไม่ใช่เลขลำดับแถว · หรืออยู่ระหว่างราคากับยอด) ⇒ ห้ามหารจำนวนใหม่ ·
///   แถวมีคำหัก/มัดจำ ⇒ <see cref="OcrLineQuantityAction.DeductionRow"/> · ไม่งั้น <see cref="OcrLineQuantityAction.KeepPrintedGap"/></item>
/// <item>อื่น ๆ ⇒ แก้จำนวน = ยอด ÷ ราคา (พฤติกรรมเดิม) · จำนวนใหม่พิมพ์บนแถว = หมายเหตุแจ้ง · ไม่งั้นแท็กบล็อก</item>
/// </list></para>
///
/// <para>แท็กบล็อกทุกตัวพก "ค่าที่ระบบตั้ง" (<see cref="Fingerprint"/>) ⇒ ตอนอนุมัติจาก LINE/สร้าง+อนุมัติ ตรวจสดบนบรรทัดเอกสาร (<see cref="LiveNotes"/>):
/// คนแก้บรรทัดนั้นแล้ว (ไม่มีบรรทัดที่ยังถือค่าของระบบ) และทุกบรรทัดลงตัว ⇒ ปลดแท็ก · ค่าของระบบยังอยู่ = ยังไม่มีคนดู ⇒ บล็อกต่อ (F2 ข้อ 3)</para>
///
/// <para>ไม่ทำ (จดตรง ๆ): ส่วนลดที่พิมพ์<b>หลัง</b>ยอดบรรทัด · ราคา/ยอดที่ OCR อ่านเพี้ยนจนหาแถวไม่เจอ ⇒ กติกาท้าย · ไม่ตรวจ "จำนวนอยู่คอลัมน์เดียวกันทุกแถว"
/// (ข้อความ engine ไม่มีพิกัดคอลัมน์) · ไม่ลงภาษีหัก ณ ที่จ่าย/การหักมัดจำเอง (เจ้าของเดิมตัดสิน — แท็กบอกยอด)</para>
/// </summary>
public static class OcrLineQuantityGuard
{
    /// <summary>แท็กใน ProcessingNotes เมื่อระบบเปลี่ยน/ตีความค่าบนบรรทัดโดยไม่มีหลักฐานพอ (ห้ามอนุมัติเอง — <c>OcrPostingReadiness.BlockingTags</c>)</summary>
    public const string Tag = "[LINE-QTY]";

    /// <summary>แท็กหมายเหตุแจ้ง (ไม่บล็อก) — ค่าที่ระบบเติม/แก้มีหลักฐานบนกระดาษครบ (ส่วนลดที่มีหัวคอลัมน์ · จำนวนที่พิมพ์ · มิเตอร์) ·
    /// ข้อความ "[LINE-QTY-NOTE]" ไม่มี "[LINE-QTY]" เป็นสตริงย่อย ⇒ ด่านอนุมัติไม่นับ (ล็อกในเทสต์)</summary>
    public const string NoteTag = "[LINE-QTY-NOTE]";

    /// <summary>จำนวนตัวเลขที่ยอมให้อยู่ระหว่างราคาต่อหน่วยกับยอดบรรทัดบนแถวเดียวกัน (ส่วนลด · อัตรา VAT · ราคาหลังลด · ส่วนลด %)</summary>
    public const int MaxNumbersBetween = 4;

    /// <summary>ระยะ (ตัวอักษร) ย้อนหลังจากตัวเลขตัวแรกของแถวที่นับเป็น "ข้อความของแถว" (ไม่เกินต้นบรรทัด)</summary>
    private const int RowLookBack = 60;

    // ลำดับ alternation สำคัญ: เงินมีจุลภาค 2 ตำแหน่ง → จำนวนเต็มมีจุลภาค → ทศนิยม 2 ตำแหน่ง → ทศนิยมอื่น → จำนวนเต็ม ·
    // ไม่มี lookahead ห้ามตัวเลขตาม ⇒ ข้อความ PDF ที่ช่องติดกัน ("1,330.00293.007%1,037.00" — PdfPig ของใบ RE-20261000111) แตกได้ถูกตัว
    private static readonly Regex NumberRe = new(
        @"(?<n>\d{1,3}(?:,\d{3})+\.\d{2}|\d{1,3}(?:,\d{3})+(?![\d.])|\d+\.\d{2}|\d+\.\d{1,4}|\d+)(?<pct>\s?%)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>คำหน่วยนับที่ติดหลังจำนวน — หลักฐานว่าตัวเลขคือ "จำนวน" (ไม่ใช่เลขลำดับแถว/รหัส) · ของตัวกันนี้เท่านั้น
    /// (<c>OcrVatCodeLines.GenericTokens</c> เป็นชุด "คำที่ไม่ชี้สินค้า" คนละคำถาม)</summary>
    private static readonly Regex UnitRe = new(
        @"^(?:ชิ้น|อัน|ด้าม|ม้วน|คู่|ใบ(?!เสร็จ|กำกับ|กํากับ|แจ้ง|สั่ง|ส่ง|ลด|เพิ่ม|รับ|วาง|ขน)|ตัว|ชุด|กล่อง|ถุง|ขวด|แพ็ค|แพค|แพ็ก|ซอง|กระป๋อง|หลอด|ลัง|โหล|แผง|แผ่น"
        + @"|แท่ง|ก้อน|ห่อ|เส้น|เครื่อง|หน่วย|กก\.?|กิโล|กรัม|ลิตร|ลบ\.?\s*ม|เมตร|ฟอง|ถัง|แกลลอน|คัน|ครั้ง|เดือน|วัน(?!ที่)|ชั่วโมง|ชม\.|งาน|เล่ม|ดวง|กระสอบ|ถาด"
        + @"|pcs?\b|ea\b|each\b|units?\b|kg\b|kwh\b|sets?\b|box(?:es)?\b|bags?\b|rolls?\b|packs?\b|lots?\b|jobs?\b|hrs?\b|months?\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>คำมัดจำที่ <see cref="OcrDepositMarker.Keyword"/> ไม่ครอบ (ผู้เรียกในที่นี้ถามแค่ "ส่วนต่างนี้เป็นการหักมัดจำ/งวดก่อนไหม" ไม่ใช่ "ใบมัดจำไหม")</summary>
    private static readonly Regex DepositExtraRe = new(
        @"(?<![A-Za-z])advance(?![A-Za-z])|งวด[ \t]*(?:ที่)?[ \t]*ก่อน|งวดที่แล้ว",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>คำส่วนลด (บนข้อความที่ย่อด้วย <see cref="ThaiTextNormalizer.SquashForKeywordMatch"/>) — "ลด" เดี่ยวไม่นับ "ลดหนี้" (หัวกระดาษใบลดหนี้)</summary>
    private static readonly Regex DiscountWordRe = new(
        @"ส่วนลด|discount|disc|ลด(?!หนี้)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>บทบาทหัวคอลัมน์ (ย่อแล้ว) — ช่วงข้อความไม่มีตัวเลขที่มี ≥ 3 บทบาท = หัวตาราง</summary>
    private static readonly string[][] HeaderRoles =
    {
        new[] { "จำนวน", "qty", "quantity" },
        new[] { "หน่วย", "unit" },
        new[] { "ราคา", "price" },
        new[] { "ส่วนลด", "discount", "disc" },
        new[] { "จำนวนเงิน", "ยอดเงิน", "มูลค่า", "amount", "total", "ยอดสุทธิ", "รวมเงิน" },
        new[] { "รายการ", "รายละเอียด", "คำอธิบาย", "description", "item" },
    };

    private static readonly Regex DigitFreeRe = new(@"[^0-9๐-๙]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FingerprintRe = new(
        @"⟦(?<q>-?\d+(?:\.\d+)?)×(?<p>-?\d+(?:\.\d+)?)⟧", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string Q(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>ร่องรอย "ค่าที่ระบบตั้ง" (จำนวน × ราคา) ต่อท้ายแท็กบล็อก — ตัวเดียวทั้งฝั่งเขียน (<see cref="Decide"/>) และฝั่งตรวจสด (<see cref="LiveNotes"/>)</summary>
    public static string Fingerprint(decimal quantity, decimal unitPrice) => $" · ค่าที่ระบบตั้ง ⟦{Q(quantity)}×{Q(unitPrice)}⟧";

    /// <summary>แยกข้อความกระดาษ (เรียกครั้งเดียวต่อสแกน แล้วส่งให้ <see cref="Decide"/> ทุกบรรทัด) · null/ว่าง = <see cref="OcrLinePaper.Empty"/></summary>
    public static OcrLinePaper Read(string? paperText)
    {
        if (string.IsNullOrWhiteSpace(paperText)) return OcrLinePaper.Empty;
        var list = new List<OcrPaperNumber>();
        foreach (Match m in NumberRe.Matches(paperText))
        {
            if (!decimal.TryParse(m.Groups["n"].Value.Replace(",", ""), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var v)) continue;
            var start = m.Index;
            var end = m.Index + m.Length;
            var unitAfter = UnitAfter(paperText, end);
            list.Add(new OcrPaperNumber(v, m.Groups["pct"].Success, start, end,
                RowIndexLike: !m.Groups["pct"].Success && !unitAfter && LineLeadingFollowedByText(paperText, start, end),
                UnitAfter: unitAfter));
        }
        bool disc = false, wht = false, dep = false;
        foreach (Match seg in DigitFreeRe.Matches(ThaiTextNormalizer.MapThaiPua(paperText)))
        {
            var squashed = ThaiTextNormalizer.SquashForKeywordMatch(seg.Value);
            if (HeaderRoles.Count(role => role.Any(w => squashed.Contains(w, StringComparison.Ordinal))) < 3) continue;
            disc |= DiscountWordRe.IsMatch(squashed);
            wht |= IsWithholdingText(seg.Value);
            dep |= IsDepositText(seg.Value);
        }
        return new OcrLinePaper(paperText, list, disc, wht, dep);
    }

    private static bool IsWithholdingText(string mapped) => PaperWhtReader.HasLabel(mapped);

    private static bool IsDepositText(string mapped) => OcrDepositMarker.Keyword.IsMatch(mapped) || DepositExtraRe.IsMatch(mapped);

    private static bool IsDiscountLabel(string mapped) => DiscountWordRe.IsMatch(ThaiTextNormalizer.SquashForKeywordMatch(mapped));

    private static bool UnitAfter(string text, int end)
    {
        var i = end;
        while (i < text.Length && i < end + 3 && (text[i] == ' ' || text[i] == '\t' || text[i] == '\n' || text[i] == '\r')) i++;
        if (i >= text.Length) return false;
        var tail = ThaiTextNormalizer.MapThaiPua(text.Substring(i, Math.Min(16, text.Length - i)));
        return UnitRe.IsMatch(tail);
    }

    /// <summary>ตัวเลขตัวแรกของบรรทัด (ก่อนหน้ามีแต่ช่องว่าง/วงเล็บ/#) และบรรทัดเดียวกันมีข้อความตามหลัง ("1 หมูสับ" · " 1.  (แพ็ค3)…")</summary>
    private static bool LineLeadingFollowedByText(string text, int start, int end)
    {
        var ls = text.LastIndexOf('\n', Math.Max(0, start - 1));
        ls = ls < 0 ? 0 : ls + 1;
        if (start > 0 && ls > start) ls = start;
        for (var k = ls; k < start; k++)
            if (char.IsLetterOrDigit(text[k])) return false;
        var i = end;
        while (i < text.Length && (text[i] is ' ' or '\t' or '.' or ')' or '(' or ']' or ':' or '-')) i++;
        return i < text.Length && text[i] != '\n' && text[i] != '\r' && char.IsLetter(text[i]);
    }

    /// <summary>ข้อความระหว่างจำนวนกับราคา "ติดกัน": ว่าง · คำหน่วยนับคำเดียว · x/×/@</summary>
    private static bool AdjacentGap(string text, int from, int to)
    {
        if (to < from) return false;
        var t = ThaiTextNormalizer.MapThaiPua(text[from..to]).Trim(' ', '\t', '\r', '\n', ':', '.', ',', '|', '/', '(', ')', '[', ']', '-');
        if (t.Length == 0 || t is "x" or "X" or "×" or "@") return true;
        var m = UnitRe.Match(t);
        return m.Success && t.Length - m.Length <= 2;
    }

    /// <summary>ตัดสินบรรทัดหนึ่ง — ดูกติกาที่หัวคลาส · <paramref name="paper"/> null/ว่าง = ไม่มีหลักฐานจากกระดาษ (พฤติกรรมเดิม + แท็ก)</summary>
    public static OcrLineQuantityDecision Decide(
        decimal? quantity, decimal? unitPrice, decimal? amount, decimal? lineDiscount, OcrLinePaper? paper)
    {
        paper ??= OcrLinePaper.Empty;
        var amt = amount ?? 0m;
        var up = unitPrice ?? 0m;
        var qty = quantity ?? 0m;
        if (amt <= 0m) return Keep;

        // ราคา/หน่วยหาย แต่มียอด+จำนวน → หารหาให้ (บิลสาธารณูปโภคพิมพ์แต่ปริมาณกับยอดรวม ไม่พิมพ์ราคาต่อหน่วย) — เดิมทุกตัวอักษร
        if (up <= 0m && qty > 0m)
            return new(OcrLineQuantityAction.FillUnitPrice, null,
                Math.Round(amt / qty, 4, MidpointRounding.AwayFromZero), null, null);
        if (up <= 0m || qty <= 0m) return Keep;

        var gross = R2(up * qty);
        var gap = gross - amt;
        var proven = OcrEtaxLineNormalizer.ProvenLineDiscount(qty, up, amt, lineDiscount);
        var tol = Math.Max(1m, Math.Abs(amt) * 0.02m);
        var line = $"จำนวน {qty:0.###} × ราคา {up:N2} = {gross:N2} · ยอดบรรทัด {amt:N2}";

        // ★ ตัวเลขบนแถวที่อธิบายส่วนต่าง — ก่อนเกณฑ์ ±2% (ส่วนลด 15 บาทของสินค้า 1,000 ก็เป็นส่วนลดจริง)
        var row = proven > 0m ? default : FindRow(paper, qty, up, amt);
        if (proven == 0m && row.Discount is decimal printed)
            return new(OcrLineQuantityAction.PrintedDiscount, null, null, printed,
                $"{NoteTag} ส่วนลดรายบรรทัด {printed:N2} จากกระดาษ ({row.DiscountEvidence}) — {qty:0.###} × {up:N2} − {printed:N2} = {amt:N2} · จำนวน/ราคาตามกระดาษ");
        if (proven == 0m && row.Deduction is decimal deducted)
            return Deduction(qty, up, gross, amt, deducted, row.DeductionKind!, line);
        if (proven == 0m && row.Unevidenced is decimal unknown)
            return new(OcrLineQuantityAction.UnevidencedDiscount, null, null, gap,
                $"{Tag} {line} — ตัวเลข {unknown:N2} บนแถวลงตัวเป็นส่วนต่าง {gap:N2} แต่{row.UnevidencedWhy} — "
                + $"ระบบเก็บส่วนต่างเป็นส่วนลดบรรทัด {gap:N2} ให้ยอดตรงกระดาษ (ส่วนต่างที่กระดาษไม่บอก — ตรวจ: ส่วนลด/หัก ณ ที่จ่าย/รหัสสินค้า)"
                + Fingerprint(qty, up));

        var computed = gross - proven;
        if (Math.Abs(computed - amt) <= tol) return Keep;   // ตรงอยู่แล้ว (เกณฑ์เดิม)

        // ⚠️ เคส ก (บิลค่าไฟ 59 ล้าน — เดิม): ราคา ≈ ยอดทั้งที่จำนวน > 1 ⇒ ตัวผิดคือราคา ไม่ใช่จำนวน (3,611 หน่วยคือมิเตอร์จริง)
        var qtyLooksLikeAmount = Math.Abs(qty - amt) <= tol;
        if (qty > 1m && !qtyLooksLikeAmount && Math.Abs(up - amt) <= tol)
        {
            var price = Math.Round(amt / qty, 4, MidpointRounding.AwayFromZero);
            // ฝ่ายค้าน P2-3: จำนวนพิมพ์คู่หน่วยนับบนกระดาษ + ยอดตามมา (บิลมิเตอร์) = พิสูจน์แล้ว ⇒ แจ้ง ไม่บล็อกทุกเดือน
            return MeterQuantityPrinted(paper, qty, amt, price)
                ? new(OcrLineQuantityAction.FixUnitPrice, null, price, null,
                    $"{NoteTag} จำนวน {qty:0.###} พิมพ์คู่หน่วยนับบนกระดาษ · ราคาต่อหน่วยที่อ่านได้ {up:N2} คือยอดทั้งบรรทัด — ระบบตั้งราคาต่อหน่วย = ยอด ÷ จำนวน = {price:0.####}")
                : new(OcrLineQuantityAction.FixUnitPrice, null, price, null,
                    $"{Tag} ราคาต่อหน่วย {up:N2} เท่ากับยอดทั้งบรรทัด {amt:N2} (จำนวน {qty:0.###}) — ระบบตั้งราคาต่อหน่วย = ยอด ÷ จำนวน = {price:0.####} · ตรวจกับกระดาษก่อนอนุมัติ"
                    + Fingerprint(qty, price));
        }

        // ★ จำนวนพิมพ์อยู่บนแถว + ไม่มีอะไรอธิบาย ⇒ ห้ามแต่งจำนวน (เคส ข "จำนวน = ยอด" ยังแก้ตามเดิม — จำนวนนั้นไม่ใช่จำนวน)
        if (!qtyLooksLikeAmount && row.QuantityPrinted)
        {
            // แถวมีคำหัก ณ ที่จ่าย/มัดจำ ⇒ ส่วนต่างห้ามเป็นส่วนลด (ฝ่ายค้าน P1-1 ข้อ a)
            if (gap > 0m && row.DeductionKind is string ctx)
                return Deduction(qty, up, gross, amt, gap, ctx, line);
            // ฝ่ายค้าน P2-5: ห้ามเก็บ จำนวน × ราคา − ส่วนลด ≠ ยอด — ยอดตามกระดาษ · จำนวน/ราคาตามกระดาษ · ส่วนต่างเป็นส่วนลดที่ระบุชัด (ส่วนต่างลบ ⇒ ราคา = ยอด ÷ จำนวน)
            if (gap > 0m)
                return new(OcrLineQuantityAction.KeepPrintedGap, null, null, gap,
                    $"{Tag} {line} (ต่าง {gap:N2}) และกระดาษไม่พิมพ์อะไรอธิบายส่วนต่าง — ระบบคงจำนวน/ราคาตามกระดาษ (ไม่หารจำนวนใหม่) "
                    + $"และเก็บส่วนต่างเป็นส่วนลดบรรทัด {gap:N2} ให้ยอดตรงกระดาษ · ส่วนต่างที่กระดาษไม่บอก — ตรวจ" + Fingerprint(qty, up));
            var derived = Math.Round(amt / qty, 4, MidpointRounding.AwayFromZero);
            return new(OcrLineQuantityAction.KeepPrintedGap, null, derived, null,
                $"{Tag} {line} (ยอดมากกว่า {-gap:N2}) และกระดาษไม่พิมพ์อะไรอธิบาย — ระบบคงจำนวนตามกระดาษ ยอดตามกระดาษ "
                + $"และตั้งราคาต่อหน่วย = ยอด ÷ จำนวน = {derived:0.####} · ส่วนต่างที่กระดาษไม่บอก — ตรวจ" + Fingerprint(qty, derived));
        }

        var fixedQty = Math.Round(amt / up, 3, MidpointRounding.AwayFromZero);
        var newQty = fixedQty > 0m ? fixedQty : 1m;
        // ฝ่ายค้าน P2-3: จำนวนใหม่พิมพ์อยู่บนแถว (ใบ qty-misread-engine-one: กระดาษพิมพ์ 2 ก่อนราคา · เคส ข 118 คู่) และลงตัวทุกสตางค์ = พิสูจน์แล้ว ⇒ แจ้ง
        if (R2(newQty * up) == amt && FindRow(paper, newQty, up, amt) is { Found: true, QuantityPrinted: true })
            return new(OcrLineQuantityAction.FixQuantity, newQty, null, null,
                $"{NoteTag} จำนวน {newQty:0.###} จากแถวบนกระดาษ (engine อ่าน {qty:0.###}) — {newQty:0.###} × {up:N2} = {amt:N2}");
        return new(OcrLineQuantityAction.FixQuantity, newQty, null, null,
            $"{Tag} {line} และไม่พบจำนวนนี้บนแถวของกระดาษ — ระบบตั้งจำนวน = ยอด ÷ ราคา = {newQty:0.###} · ตรวจกับกระดาษก่อนอนุมัติ"
            + Fingerprint(newQty, up));
    }

    private static OcrLineQuantityDecision Deduction(decimal qty, decimal up, decimal gross, decimal amt, decimal deducted, string kind, string line)
        => new(OcrLineQuantityAction.DeductionRow, null, null, null,
            $"{Tag} {line} — ส่วนต่าง {deducted:N2} บนแถวเป็น{kind} ไม่ใช่ส่วนลด ⇒ ระบบตั้งยอดบรรทัด = จำนวน × ราคา = {gross:N2} (ก่อนหัก) · "
            + (kind == WithholdingKind
                ? "ภาษีหัก ณ ที่จ่ายลงที่ช่องหัก ณ ที่จ่ายของเอกสาร (ไม่ลดมูลค่ารายการ)"
                : "การหักมัดจำ/งวดก่อนต้องตัดผ่านการหักมัดจำของเอกสาร (ล้างบัญชีมัดจำ) — ไม่ใช่ส่วนลด")
            + " · ตรวจกับกระดาษก่อนอนุมัติ" + Fingerprint(qty, up),
            Amount: gross);

    private const string WithholdingKind = "ภาษีหัก ณ ที่จ่าย";
    private const string DepositKind = "การหักมัดจำ/งวดก่อน";

    private static readonly OcrLineQuantityDecision Keep = new(OcrLineQuantityAction.Keep, null, null, null, null);

    private enum Kind { Discount, Deduction, Unknown }

    private readonly record struct RowEvidence(
        bool Found, bool QuantityPrinted,
        decimal? Discount, string? DiscountEvidence,
        decimal? Deduction, string? DeductionKind,
        decimal? Unevidenced, string? UnevidencedWhy);

    /// <summary>หาแถวของบรรทัดบนข้อความกระดาษ: ราคาต่อหน่วย แล้วยอดบรรทัดตามมาภายใน <see cref="MaxNumbersBetween"/> ตัว ·
    /// คืนตัวเลขที่อธิบายส่วนต่าง (จำแนกตามหลักฐาน) · จำนวนพิมพ์อยู่บนแถวไหม · คำหัก/มัดจำบนแถว/หัวคอลัมน์ ·
    /// ค่าต้องเป็นค่าเดียวทุกแถวที่เจอ (สองค่าต่างกัน/สองชนิด = ไม่รู้)</summary>
    private static RowEvidence FindRow(OcrLinePaper paper, decimal qty, decimal up, decimal amt)
    {
        var nums = paper.Numbers;
        if (nums.Count == 0) return default;
        var text = paper.Text;
        var gross = R2(up * qty);
        var gap = gross - amt;
        var hits = new List<(Kind Kind, decimal Value, string Why)>();
        var qtyPrinted = false;
        var found = false;
        string? context = null;
        for (var i = 0; i < nums.Count; i++)
        {
            if (nums[i].IsPercent || nums[i].Value != up) continue;
            // จำนวนพิมพ์ติดก่อนราคา: ตัวเลขตัวก่อนหน้า · ไม่ใช่เลขลำดับแถว · คั่นด้วยคำหน่วยนับ/ว่างเท่านั้น (ฝ่ายค้าน P2-4)
            var qtyAt = i > 0 && !nums[i - 1].IsPercent && nums[i - 1].Value == qty && !nums[i - 1].RowIndexLike
                        && AdjacentGap(text, nums[i - 1].End, nums[i].Start) ? i - 1 : -1;
            var j = -1;
            for (var k = i + 1; k < nums.Count && k <= i + MaxNumbersBetween + 1; k++)
                if (!nums[k].IsPercent && nums[k].Value == amt) { j = k; break; }
            if (j < 0)
            {
                if (qtyAt >= 0) qtyPrinted = true;
                continue;
            }
            found = true;
            if (qtyAt < 0)
                for (var k = i + 1; k < j; k++)
                    if (!nums[k].IsPercent && nums[k].Value == qty) { qtyAt = k; break; }
            if (qtyAt >= 0) qtyPrinted = true;

            var first = qtyAt >= 0 && qtyAt < i ? qtyAt : i;
            var lineStart = text.LastIndexOf('\n', Math.Max(0, nums[first].Start - 1)) + 1;
            var rowStart = Math.Max(lineStart, nums[first].Start - RowLookBack);
            if (rowStart > nums[first].Start) rowStart = nums[first].Start;
            var rowText = ThaiTextNormalizer.MapThaiPua(text[rowStart..nums[j].End]);
            var rowKind = IsWithholdingText(rowText) ? WithholdingKind : IsDepositText(rowText) ? DepositKind : null;
            context ??= rowKind;
            if (gap < 0.01m) continue;
            for (var k = i + 1; k < j; k++)
            {
                if (k == qtyAt) continue;   // ตัวเลขเดียวกันนับเป็นทั้งจำนวนและส่วนต่างไม่ได้
                var v = nums[k].Value;
                bool explains;
                if (!nums[k].IsPercent)
                    // ตัวเลขที่เท่า VAT 7% ของบรรทัดพอดี (ราคารวม VAT · VAT · ยอดก่อน VAT = "107.00 7.00 100.00") คือคอลัมน์ VAT ได้เท่ากัน ⇒ ไม่นับ
                    explains = v > 0m && v < gross && R2(gross - v) == amt && v != R2(amt * 0.07m) && v != R2(gross * 7m / 107m);
                else
                    explains = v > 0m && v < 100m && v != 7m && R2(gross * v / 100m) == gap;
                if (!explains) continue;
                var label = ThaiTextNormalizer.MapThaiPua(text[(k > i ? nums[k - 1].End : rowStart)..nums[k].Start]);
                var (kind, why) = Classify(label, rowKind, paper, nums[k].IsPercent, v);
                hits.Add((kind, nums[k].IsPercent ? gap : v, why));
            }
        }
        // คำหัก/มัดจำบนแถว ⇒ หรือบนหัวคอลัมน์ (ฝ่ายค้าน P1-1 ข้อ a: "แถว หรือหัวคอลัมน์ของมัน") — ใช้กับแขนงจำนวนพิมพ์บนแถวที่ไม่มีตัวเลขอธิบาย
        context ??= paper.HeaderHasWithholding ? WithholdingKind : paper.HeaderHasDeposit ? DepositKind : null;
        decimal? discount = null, deduction = null, unknown = null;
        string? discountWhy = null, deductionKind = context, unknownWhy = null;
        var kinds = hits.Select(h => h.Kind).Distinct().ToList();
        var values = hits.Select(h => h.Value).Distinct().ToList();
        if (hits.Count > 0)
        {
            if (kinds.Count == 1 && values.Count == 1 && kinds[0] == Kind.Discount)
                (discount, discountWhy) = (values[0], hits[0].Why);
            else if (kinds.Count == 1 && values.Count == 1 && kinds[0] == Kind.Deduction)
                (deduction, deductionKind) = (values[0], hits[0].Why);
            else
                (unknown, unknownWhy) = (hits[0].Value,
                    kinds.Count == 1 && values.Count == 1 ? hits[0].Why : "ตัวเลขบนแถวตีความได้มากกว่าหนึ่งแบบ");
        }
        return new RowEvidence(found, qtyPrinted, discount, discountWhy, deduction, deductionKind, unknown, unknownWhy);
    }

    /// <summary>จำแนกตัวเลขที่อธิบายส่วนต่าง — หลักฐานใกล้สุดก่อน (ป้ายติดตัวเลข → คำในแถว → หัวคอลัมน์ → ชนิดตัวเลข) ·
    /// ค่า Why: ส่วนลด = หลักฐาน · ส่วนหัก = ชนิด (<see cref="WithholdingKind"/>/<see cref="DepositKind"/>) · ไม่รู้ = เหตุที่ไม่รับ</summary>
    private static (Kind, string) Classify(string label, string? rowKind, OcrLinePaper paper, bool isPercent, decimal v)
    {
        if (IsWithholdingText(label)) return (Kind.Deduction, WithholdingKind);
        if (IsDepositText(label)) return (Kind.Deduction, DepositKind);
        if (IsDiscountLabel(label)) return (Kind.Discount, "ป้ายส่วนลดติดตัวเลข");
        if (rowKind is not null) return (Kind.Deduction, rowKind);
        var hdrDeduct = paper.HeaderHasWithholding || paper.HeaderHasDeposit;
        if (paper.HeaderHasDiscount && !hdrDeduct) return (Kind.Discount, "หัวคอลัมน์ส่วนลด");
        if (hdrDeduct && !paper.HeaderHasDiscount)
            return (Kind.Deduction, paper.HeaderHasWithholding ? WithholdingKind : DepositKind);
        if (hdrDeduct) return (Kind.Unknown, "หัวคอลัมน์มีทั้งส่วนลดและหัก ณ ที่จ่าย/มัดจำ");
        if (isPercent && !IsWithholdingRate(v)) return (Kind.Discount, $"ส่วนลด {v:0.##}% บนแถว");
        return isPercent
            ? (Kind.Unknown, $"{v:0.##}% เท่าอัตราภาษีหัก ณ ที่จ่ายและกระดาษไม่มีหัวคอลัมน์ส่วนลด")
            : (Kind.Unknown, "กระดาษไม่มีหัวคอลัมน์ส่วนลดหรือป้ายส่วนลดติดตัวเลข");
    }

    /// <summary>อัตราตามตารางหัก ณ ที่จ่ายตัวเดียว (<see cref="ThaiWhtRateTable.StatutoryRates"/> + อัตราลดชั่วคราว)</summary>
    private static bool IsWithholdingRate(decimal v)
        => ThaiWhtRateTable.StatutoryRates.Contains(v) || ThaiWhtRateTable.TemporaryReducedRates.Any(t => t.Rate == v);

    /// <summary>บิลมิเตอร์ (เคส ก): จำนวนพิมพ์คู่หน่วยนับ ไม่ใช่เลขลำดับแถว แล้วยอดตามมา · ตัวเลขที่คั่นกลางต้องเป็นราคาต่อหน่วยที่ตั้ง (±0.01) หรือ %</summary>
    private static bool MeterQuantityPrinted(OcrLinePaper paper, decimal qty, decimal amt, decimal price)
    {
        var nums = paper.Numbers;
        for (var k = 0; k < nums.Count; k++)
        {
            if (nums[k].IsPercent || nums[k].Value != qty || !nums[k].UnitAfter || nums[k].RowIndexLike) continue;
            for (var j = k + 1; j < nums.Count && j <= k + MaxNumbersBetween + 1; j++)
            {
                if (nums[j].IsPercent) continue;
                if (nums[j].Value == amt) return true;
                if (Math.Abs(nums[j].Value - price) > 0.01m) break;
            }
        }
        return false;
    }

    /// <summary>บรรทัดที่<b>ผู้ใช้แก้เอง</b> (ฝ่ายค้าน P2-6 · CLAUDE #4 A "ค่าผู้ใช้ชนะเสมอ"): ตัวกันไม่แก้ค่าใด ๆ · ไม่ลงตัว ⇒ คืนแท็กบล็อกให้คนดู · ลงตัว/ข้อมูลไม่พอ ⇒ null</summary>
    public static string? UserEditedNote(decimal? quantity, decimal? unitPrice, decimal? amount, decimal? lineDiscount)
    {
        if (quantity is not > 0m || amount is not > 0m) return null;
        if (unitPrice is not > 0m)
            return $"{Tag} บรรทัดที่ผู้ใช้แก้: ราคาต่อหน่วยว่าง/0 แต่ยอด {amount:N2} — ระบบไม่เติมราคาแทนผู้ใช้ · ตรวจบรรทัดก่อนอนุมัติ"
                + Fingerprint(quantity.Value, unitPrice ?? 0m);
        var gross = R2(quantity.Value * unitPrice.Value);
        var after = gross - (lineDiscount ?? 0m);
        if (Math.Abs(after - amount.Value) <= 0.01m) return null;
        return $"{Tag} บรรทัดที่ผู้ใช้แก้: จำนวน {quantity:0.###} × ราคา {unitPrice:N2} − ส่วนลด {lineDiscount ?? 0m:N2} = {after:N2} ≠ ยอด {amount:N2} — "
            + "ระบบไม่แก้ค่าของผู้ใช้ · ตรวจบรรทัดก่อนอนุมัติ" + Fingerprint(quantity.Value, unitPrice.Value);
    }

    /// <summary>ต่อหมายเหตุของตัวกัน (<see cref="Tag"/>/<see cref="NoteTag"/>) จาก ReasoningTrace ลง ProcessingNotes — ตัวเดียวของทุกเส้น
    /// (สแกน · สร้าง · พรีวิว · ดึงรายการซ้ำ — ฝ่ายค้าน P2-6: สามเส้นหลังเคยทิ้ง) · ข้อความเดิมที่มีแล้วไม่ต่อซ้ำ</summary>
    public static string? MergeNotes(string? processingNotes, IEnumerable<string> trace)
    {
        var notes = processingNotes;
        foreach (var t in trace)
        {
            if (!t.StartsWith(Tag, StringComparison.Ordinal) && !t.StartsWith(NoteTag, StringComparison.Ordinal)) continue;
            if ((notes ?? "").Contains(t, StringComparison.Ordinal)) continue;
            notes = (notes ?? "") + "\n" + t;
        }
        return notes;
    }

    /// <summary>
    /// **ตรวจสดแท็ก <see cref="Tag"/> บนบรรทัดเอกสารตอนนี้** (ฝ่ายค้าน P2-2 — แท็กของตอนสแกนเคยค้างหยุด LINE/สร้าง+อนุมัติตลอดไป · รูปเดียวกับ
    /// <see cref="OcrVatCodeLines.LiveNotes"/>) — ปลดแท็กหนึ่งข้อเมื่อ (1) ไม่มีบรรทัดใดยังถือ "ค่าที่ระบบตั้ง" (<see cref="Fingerprint"/>) ของข้อนั้น
    /// (คนแก้/ลบแล้ว — ค่าที่ระบบประทับเองไม่ใช่หลักฐานว่ามีคนดู · F2 ข้อ 3) และ (2) ทุกบรรทัดลงตัว round(จำนวน × ราคา) − ส่วนลด = ยอด
    /// (ถอด VAT ตามธงเอกสาร) · ราคาเทียบแบบ ±0.005 (<c>DocumentLine.UnitPrice</c> เก็บ 2 ตำแหน่ง — ราคา 4 ตำแหน่งที่ระบบตั้งต้องยังนับว่า "ค่าของระบบ") · ตรวจสดไม่ได้ (<paramref name="checkedLive"/> = false) / แท็กเก่าไม่มีร่องรอย ⇒ คงไว้ (บล็อกต่อ)
    /// </summary>
    public static string? LiveNotes(string? processingNotes, IReadOnlyList<OcrLiveLine> lines, bool pricesIncludeVat, bool checkedLive)
    {
        if (!checkedLive || lines.Count == 0 || string.IsNullOrEmpty(processingNotes)) return processingNotes;
        var allConsistent = lines.All(l => Consistent(l, pricesIncludeVat));
        var kept = new List<string>();
        var changed = false;
        foreach (var raw in processingNotes.Split('\n'))
        {
            if (raw.TrimStart().StartsWith(Tag, StringComparison.Ordinal)
                && FingerprintRe.Match(raw) is { Success: true } fp
                && decimal.TryParse(fp.Groups["q"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var q)
                && decimal.TryParse(fp.Groups["p"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var p)
                && allConsistent
                && !lines.Any(l => Math.Abs(l.Quantity - q) < 0.0005m && Math.Abs(l.UnitPrice - p) <= 0.005m))
            {
                changed = true;
                continue;
            }
            kept.Add(raw);
        }
        return changed ? string.Join("\n", kept) : processingNotes;
    }

    /// <summary>บรรทัดลงตัวตามสูตรเอกสาร (ส่วนลดบาทชนะ % · ราคารวม VAT ⇒ ถอด VAT) ภายใน 0.01 — ส่วนลดท้ายบิลที่เฉลี่ยตอนคำนวณทำให้ไม่ลงตัว ⇒ ไม่ปลด (ทิศปลอดภัย)</summary>
    private static bool Consistent(OcrLiveLine l, bool pricesIncludeVat)
    {
        var gross = R2(l.Quantity * l.UnitPrice);
        var disc = l.DiscountAmount > 0m ? Math.Min(R2(l.DiscountAmount), gross) : R2(gross * l.DiscountPercent / 100m);
        var after = gross - disc;
        var net = pricesIncludeVat && l.VatRate > 0m ? R2(after * 100m / (100m + l.VatRate)) : after;
        return Math.Abs(net - l.Amount) <= 0.01m;
    }

    /// <summary>ใบที่อ่านส่วนลดรายบรรทัดจากกระดาษได้ (ส่วนลด ≥ 1 บรรทัด) และเลขหัวใบพิสูจน์ว่าราคาบนกระดาษ<b>รวม VAT</b>
    /// (ตัวจำแนกตัวเดียว <see cref="OcrLineReconciler.Classify"/> = เคส A บน Σ ยอดหลังลด) ⇒ ราคา · ส่วนลด · ยอด ของทุกบรรทัดอยู่ฐานรวม VAT
    /// (ธง <c>PriceIncludesVat</c> ของบรรทัด — ป้ายส่วนลดในหน้ารีวิว · ทุนสต็อก/สินทรัพย์ถอด VAT) · ไม่มีส่วนลดจากกระดาษ = false (ใบเดิมไม่ถูกแตะ —
    /// ตัวสร้างบรรทัดตัดสินเคส A เองเหมือนเดิม)</summary>
    public static bool PaperDiscountPricesIncludeVat(
        bool anyPaperDiscount, decimal lineSum, decimal? headerSubTotal, decimal? headerVat, decimal? headerTotal, decimal? headerDiscount)
    {
        if (!anyPaperDiscount || headerVat is not > 0m || headerTotal is not > 0m) return false;
        return OcrLineReconciler.Classify(lineSum, headerSubTotal ?? 0m, headerVat.Value, headerTotal.Value, headerDiscount ?? 0m).Case
            == OcrLineReconcileCase.PricesIncludeVat;
    }
}
