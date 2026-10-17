using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Accounting.Models.Entities;
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
    /// <summary>ไม่มีราคาต่อหน่วย แต่มีจำนวน + ยอด และราคานั้น<b>พิมพ์อยู่บนแถวของกระดาษ</b> (engine อ่านคอลัมน์ราคาหลุด) หรือจำนวน = 1 ⇒
    /// เติมราคา 2 ตำแหน่งที่ round(จำนวน × ราคา) = ยอดทุกสตางค์ (เติมช่องว่าง ไม่ได้เปลี่ยนค่าที่อ่านได้) · กระดาษไม่พิมพ์ราคา ⇒
    /// <see cref="QuantityInDescription"/> (ทีม OCR 8 · 2026-10-16 — เดิมเติม ยอด ÷ จำนวน ทศนิยม 4 ตำแหน่ง)</summary>
    FillUnitPrice,
    /// <summary>กระดาษพิมพ์ส่วนลดของบรรทัดนี้ <b>พร้อมหลักฐานว่าเป็นส่วนลด</b> (หัวคอลัมน์ "ส่วนลด" หรือป้าย ลด/ส่วนลด ติดตัวเลข · % ที่ไม่ใช่อัตรา
    /// หัก ณ ที่จ่าย) และ round(จำนวน × ราคา) − ส่วนลด = ยอด ทุกสตางค์ ⇒ ลงเป็นส่วนลดของบรรทัด · จำนวน/ราคาคงตามกระดาษ · หมายเหตุแจ้ง (ไม่บล็อก)</summary>
    PrintedDiscount,
    /// <summary>(เคส ก) ราคาต่อหน่วย ≈ ยอดทั้งบรรทัดทั้งที่จำนวน &gt; 1 ⇒ engine อ่านยอดมาใส่ช่องราคา ⇒ ราคา 2 ตำแหน่งที่ลงตัว<b>พิมพ์อยู่บนแถว</b> ⇒ ราคานั้น
    /// (หมายเหตุแจ้ง) · กระดาษไม่พิมพ์ราคาต่อหน่วย (บิลมิเตอร์) ⇒ <see cref="QuantityInDescription"/> (ทีม OCR 8 — เดิม ยอด ÷ จำนวน 4 ตำแหน่ง)</summary>
    FixUnitPrice,
    /// <summary>(เคส ข / เดิม) จำนวนที่ engine อ่านไม่ได้พิมพ์บนแถวของกระดาษ (หรือเป็นยอดที่อ่านหลงคอลัมน์) ⇒ จำนวน = ยอด ÷ ราคา
    /// · จำนวนใหม่พิมพ์อยู่บนแถว = พิสูจน์แล้ว ⇒ หมายเหตุแจ้ง · ไม่งั้นแท็กบล็อก</summary>
    FixQuantity,
    /// <summary>จำนวนที่ engine อ่าน<b>พิมพ์อยู่บนแถวของกระดาษ</b> แต่ จำนวน × ราคา ≠ ยอด และกระดาษไม่มีอะไรอธิบาย ⇒ คงจำนวน/ราคาตามกระดาษ (ห้ามแต่งจำนวน) +
    /// <b>ห้ามเก็บบรรทัดที่ไม่ลงตัว</b> (ฝ่ายค้าน 658e3705 P2-5 — เปิดแก้แล้วบันทึก ComputeLineAmounts คิดใหม่ = ยอดเลื่อน): ส่วนต่างบวก ⇒ ส่วนลดที่ระบุชัด ·
    /// ส่วนต่างลบ ⇒ 1 × ยอด + จำนวน/ราคาที่พิมพ์ในคำอธิบาย (ทีม OCR 8 — เดิม ราคา = ยอด ÷ จำนวน 4 ตำแหน่ง) · ยอดบรรทัด = ยอดบนกระดาษเสมอ ·
    /// แท็กบล็อก "ส่วนต่างที่กระดาษไม่บอก — ตรวจ"</summary>
    KeepPrintedGap,
    /// <summary>ตัวเลขบนแถวลงตัวเป็นส่วนต่างพอดี แต่<b>ไม่มีหลักฐานว่าเป็นส่วนลด</b> (ไม่มีหัวคอลัมน์/ป้ายส่วนลด · % เท่าอัตราหัก ณ ที่จ่าย · ตัวเลขเปล่า
    /// เช่นรหัสสินค้า "SKU 50" · อัตรา VAT ไม่มี %) ⇒ เก็บแบบ <see cref="KeepPrintedGap"/> (ส่วนต่างเป็นส่วนลดที่ระบุชัด ยอดตามกระดาษ) + แท็กบล็อก</summary>
    UnevidencedDiscount,
    /// <summary>แถว/หัวคอลัมน์มีคำ หัก ณ ที่จ่าย/มัดจำ ⇒ ส่วนต่าง<b>ไม่ใช่ส่วนลด</b> (ภาษีหัก ณ ที่จ่ายไม่ลดมูลค่าบรรทัด · มัดจำต้องล้างบัญชีมัดจำ ไม่ใช่ลดราคา) ⇒
    /// ยอดบรรทัด = จำนวน × ราคาที่พิมพ์ (ก่อนหัก) · ส่วนที่หักเป็นของเจ้าของเดิม (ภาษีหัก ณ ที่จ่ายของเอกสาร · การหักมัดจำของเอกสาร) · แท็กบล็อก</summary>
    DeductionRow,
    /// <summary>**บรรทัดที่กระดาษพิมพ์จำนวน + ยอด แต่ไม่พิมพ์ราคาต่อหน่วยที่เก็บได้** (บิลมิเตอร์ค่าไฟ/ค่าน้ำ/โทรศัพท์ · ชั่งน้ำหนัก · ราคาเกิน 2 ตำแหน่ง)
    /// ⇒ จำนวน 1 × ราคา = ยอดบนกระดาษ (2 ตำแหน่ง · ลงตัวทุกสตางค์) · จำนวนหน่วย (และราคาที่พิมพ์ถ้ามี) ย้ายไปอยู่ในคำอธิบาย
    /// "ค่าไฟฟ้า ประจำเดือน 08/2569 (4,272 หน่วย)" · หน่วยนับของบรรทัด = <see cref="OcrLineQuantityGuard.LumpSumUnit"/>
    /// <para>ที่มา (ทีม OCR 8 · 2026-10-16 · บิล PEA จริง XH0712609004157): เดิมตั้งราคา = ยอด ÷ จำนวน ทศนิยม 4 (4.5369/หน่วย) แต่
    /// <c>DocumentLine.UnitPrice</c> เก็บ 2 ตำแหน่ง ⇒ เปิดแก้แล้วบันทึก 4.54 × 4,272 = 19,394.88 ≠ กระดาษ 19,381.50 · เจ้าของตรวจบิลจริง:
    /// กระดาษพิมพ์แต่ยอด 2 ตำแหน่ง ตัวเลข 4 ตำแหน่งตัวเดียวคืออัตราค่า Ft — ไม่มีราคาต่อหน่วยบนกระดาษ ⇒ ระบบห้ามแต่งขึ้น (F2 ข้อ 3)</para>
    /// <para>★ ทีม OCR 9 (ฝ่ายค้าน 13a4d788 P0): <b>เฉพาะบรรทัดที่มีหลักฐานมิเตอร์</b> (<c>MeterEvidence</c>: ป้าย "หน่วยที่ใช้" ก่อนจำนวน ·
    /// หน่วยมิเตอร์ หน่วย(ค่าไฟ)/kWh/ลบ.ม./m3/นาที/GB/MB/ครั้ง(โทร)/เดือน · ผู้ขายสาธารณูปโภค <see cref="OcrUtilityVendor"/>) — สินค้า (ขวด/กก./ลิตร …)
    /// ห้ามเข้าเส้นนี้ (เดิม 12 ขวด ⇒ 1 × 300 สต็อกขาด 11 · ทุนเฉลี่ยเพี้ยน 12 เท่า) ⇒ <see cref="RoundedUnitPrice"/></para></summary>
    QuantityInDescription,
    /// <summary>**คงจำนวนที่อ่านได้ · ราคา = ยอด ÷ จำนวน ปัด 2 ตำแหน่ง · เศษเป็นส่วนลดบรรทัดที่ระบุชัด · แท็กบล็อก** (ทีม OCR 9 · ฝ่ายค้าน 13a4d788 P0)
    /// — บรรทัดสินค้าที่ จำนวน × ราคา 2 ตำแหน่ง ไม่ลงตัวกับยอด และไม่มีหลักฐานมิเตอร์ (ห้ามทิ้งจำนวนเงียบ ๆ: จำนวนไหลไปนำเข้าสต็อก/ตัดยอด PO) ·
    /// ราคาปัดขึ้นสตางค์ (≤ 0.01) เมื่อปัดปกติแล้ว จำนวน × ราคา ต่ำกว่ายอด (ส่วนลดติดลบเก็บไม่ได้) ⇒ round(จำนวน × ราคา) − ส่วนลด = ยอดบนกระดาษทุกสตางค์
    /// (บันทึกซ้ำไม่เลื่อน) · แท็กบอกส่วนต่างเป็นสตางค์ + ค่าที่ระบบตั้ง (<see cref="OcrLineQuantityGuard.Fingerprint"/>)</summary>
    RoundedUnitPrice,
}

/// <summary>ผลของ <see cref="OcrLineQuantityGuard.Decide"/> — ค่าที่ผู้เรียกต้องเขียนลงบรรทัด (null = ไม่เปลี่ยนช่องนั้น)</summary>
/// <param name="Note">ข้อความเมื่อระบบเปลี่ยน/ตีความค่าบนบรรทัด — ขึ้นต้น <see cref="OcrLineQuantityGuard.Tag"/> (บล็อกการอนุมัติเอง) หรือ
/// <see cref="OcrLineQuantityGuard.NoteTag"/> (แจ้งอย่างเดียว) · ผู้เรียกต้องส่งต่อให้คนเห็น (<see cref="OcrLineQuantityGuard.MergeNotes"/>) · null = ไม่มีอะไรต้องบอก</param>
/// <param name="Amount">ยอดบรรทัดใหม่ (เฉพาะ <see cref="OcrLineQuantityAction.DeductionRow"/> = จำนวน × ราคาก่อนหัก)</param>
/// <param name="Description">คำอธิบายใหม่ของบรรทัด (เฉพาะ <see cref="OcrLineQuantityAction.QuantityInDescription"/> — คำอธิบายเดิม + "(จำนวน หน่วย)"
/// จาก <see cref="OcrLineQuantityGuard.QuantityDescription"/>) · null = ไม่เปลี่ยน</param>
/// <param name="Unit">หน่วยนับใหม่ (เฉพาะ <see cref="OcrLineQuantityAction.QuantityInDescription"/> = <see cref="OcrLineQuantityGuard.LumpSumUnit"/>) · null = ไม่เปลี่ยน</param>
public sealed record OcrLineQuantityDecision(
    OcrLineQuantityAction Action, decimal? Quantity, decimal? UnitPrice, decimal? LineDiscount, string? Note, decimal? Amount = null,
    string? Description = null, string? Unit = null)
{
    /// <summary>หมายเหตุนี้หยุดการอนุมัติเอง (<see cref="OcrPostingReadiness.BlockingTags"/>)</summary>
    public bool Blocking => Note is not null && Note.StartsWith(OcrLineQuantityGuard.Tag, StringComparison.Ordinal);
}

/// <summary>ท้ายคำอธิบายที่ <see cref="OcrLineQuantityGuard"/> เขียนไว้บนบรรทัด 1 × ยอด (<see cref="OcrLineQuantityGuard.LumpSumSuffix"/>) —
/// <see cref="Base"/> = คำอธิบายก่อนท้าย (คีย์จับคู่) · <see cref="Suffix"/> = วงเล็บท้าย · <see cref="Meter"/> = ท้ายเป็นจำนวนหน่วยมิเตอร์ที่ตัวกันรุ่นปัจจุบันก็เขียน
/// (บิลค่าไฟ "(4,272 หน่วย)") · false = "(จำนวน N)" หรือท้ายของรุ่น 6/7/8 บนบรรทัดที่ไม่มีหลักฐานมิเตอร์ (จำนวนสินค้าหาย — รายงานซ่อม)
/// <para>ทีม OCR 12 (ฝ่ายค้านรอบ 3 P2-2): <see cref="Quantity"/>/<see cref="UnitWord"/> = จำนวนและคำหน่วยในวงเล็บ (UnitWord null = "(จำนวน N)") — ให้ตัวตรวจย้อนหลัง
/// เล่นหลักฐานมิเตอร์ด้วยบริบทเดียวกับตัวตัดสินตอนเขียน (<see cref="OcrLineQuantityGuard.WasMeterLine"/>)</para></summary>
public readonly record struct OcrLumpSumSuffix(string Base, string Suffix, bool Meter, decimal Quantity = 0m, string? UnitWord = null);

/// <summary>บรรทัดเอกสาร<b>ตอนนี้</b> สำหรับตรวจสดของ <see cref="OcrLineQuantityGuard.LiveNotes"/> (ค่าที่บันทึกใน DocumentLine) ·
/// <see cref="Description"/> = คำอธิบายบรรทัด (ทีม OCR 12 — ด่านอนุมัติของหมายเหตุซ่อมเทียบบรรทัดเดียวกัน · null = ไม่เทียบคำอธิบาย)</summary>
public readonly record struct OcrLiveLine(
    decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal DiscountAmount, decimal Amount, decimal VatRate)
{
    /// <summary>คำอธิบายบรรทัด (init — expression tree ของ EF ใช้ optional argument ไม่ได้)</summary>
    public string? Description { get; init; }
    /// <summary>หน่วยนับของบรรทัด (init · ทีม OCR 14 — ตัดท้ายของตัวกันก่อนตัดสินว่าเป็นบรรทัดมิเตอร์ และเสนอคำอธิบายในทางไปต่อ)</summary>
    public string? Unit { get; init; }
}

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
/// <item>ราคาว่าง + จำนวน &gt; 0: จำนวน 1 ⇒ ราคา = ยอด · round(ยอด ÷ จำนวน, 2) × จำนวน = ยอดทุกสตางค์ ⇒ คงจำนวน + ราคานั้น (พิมพ์บนแถว ⇒ เงียบ ·
///   ไม่พิมพ์ ⇒ หมายเหตุแจ้ง) · ไม่ลงตัว + หลักฐานมิเตอร์ ⇒ 1 × ยอด + จำนวนในคำอธิบาย (แจ้ง) · ไม่ลงตัว ไม่มีหลักฐานมิเตอร์ ⇒
///   <see cref="OcrLineQuantityAction.RoundedUnitPrice"/> (คงจำนวน · แท็กบล็อก — ทีม OCR 9 ฝ่ายค้าน 13a4d788 P0)</item>
/// <item>ส่วนลดที่เอกสารประกาศแล้ว (<see cref="OcrEtaxLineNormalizer.ProvenLineDiscount"/>) อธิบายยอดได้ = ไม่แตะ</item>
/// <item>หาแถว: ราคาต่อหน่วย → (ตัวเลข ≤ <see cref="MaxNumbersBetween"/> ตัว) → ยอดบรรทัด · ตัวเลข d ระหว่างนั้นที่ทำให้ round(จำนวน × ราคา) − d = ยอด
///   ทุกสตางค์ (หรือ d% ของยอดก่อนลด = ส่วนต่าง · ไม่รับ 7% · ไม่รับตัวเลขที่เท่า VAT ของบรรทัด) จำแนกตาม<b>หลักฐาน</b> (ใกล้สุดก่อน):
///   ป้ายติดตัวเลข (หัก ณ ที่จ่าย/มัดจำ ⇒ ส่วนหัก · ลด/ส่วนลด ⇒ ส่วนลด) → คำในแถว (หัก ณ ที่จ่าย/มัดจำ ⇒ ส่วนหัก) → หัวคอลัมน์
///   (ส่วนลดอย่างเดียว ⇒ ส่วนลด · หัก/มัดจำอย่างเดียว ⇒ ส่วนหัก · ทั้งคู่ ⇒ ไม่รู้) → ไม่มีหลักฐาน: % ที่ไม่ใช่อัตรา <see cref="ThaiWhtRateTable"/> ⇒ ส่วนลด ·
///   % เท่าอัตราหัก ณ ที่จ่าย / ตัวเลขเปล่า ⇒ ไม่รู้</item>
/// <item>ส่วนลดมีหลักฐาน ⇒ <see cref="OcrLineQuantityAction.PrintedDiscount"/> + หมายเหตุแจ้ง (ก่อนเกณฑ์ ±2% — ส่วนลด 15 บาทของ 1,000 ก็จริง)</item>
/// <item>ส่วนหัก ⇒ <see cref="OcrLineQuantityAction.DeductionRow"/> (ยอด = จำนวน × ราคา · แท็กบล็อก)</item>
/// <item>ไม่รู้ ⇒ <see cref="OcrLineQuantityAction.UnevidencedDiscount"/> (แท็กบล็อก)</item>
/// <item>ลงตัวในเกณฑ์เดิม (±2% หรือ ฿1) = ไม่แตะ — <b>เว้น</b>ราคาเกิน 2 ตำแหน่งที่<b>ปัด 2 ตำแหน่งแล้วไม่ลงตัว</b> round(จำนวน × round(ราคา, 2)) ≠ ยอด
///   (เก็บใน DocumentLine แล้วบันทึกซ้ำเลื่อน) ⇒ ลงตัวด้วย round(ยอด ÷ จำนวน, 2) ⇒ ราคานั้น (แจ้ง) · หลักฐานมิเตอร์ ⇒ 1 × ยอด · ไม่งั้น
///   <see cref="OcrLineQuantityAction.RoundedUnitPrice"/> (ปัดแล้วลงตัว ⇒ ไม่แตะ — ทีม OCR 9 แก้ "1.235 × 80.0405 ⇒ 1 × 98.85")</item>
/// <item>(เคส ก) ราคา ≈ ยอด · จำนวน &gt; 1 ⇒ ราคา 2 ตำแหน่งที่ลงตัวพิมพ์บนแถว ⇒ ราคานั้น · ยอด ÷ จำนวน ลงตัวทุกสตางค์ ⇒ ราคานั้น คงจำนวน
///   (จำนวนพิมพ์คู่หน่วยนับ/ป้าย ⇒ แจ้ง · ไม่งั้นแท็กบล็อก — รุ่น 5 เดิม) · ไม่ลงตัว + หลักฐานมิเตอร์ (บิลมิเตอร์) ⇒ 1 × ยอด + จำนวนหน่วยในคำอธิบาย ·
///   ไม่งั้น <see cref="OcrLineQuantityAction.RoundedUnitPrice"/></item>
/// <item><b>จำนวนพิมพ์อยู่บนแถว</b> (ติดก่อนราคาโดยมีแค่คำหน่วยนับคั่น และไม่ใช่เลขลำดับแถว · หรืออยู่ระหว่างราคากับยอด) ⇒ ห้ามหารจำนวนใหม่ ·
///   แถวมีคำหัก/มัดจำ ⇒ <see cref="OcrLineQuantityAction.DeductionRow"/> · ไม่งั้น <see cref="OcrLineQuantityAction.KeepPrintedGap"/>
///   (ส่วนต่างลบ ⇒ หลักฐานมิเตอร์: 1 × ยอด + จำนวน/ราคาที่พิมพ์ในคำอธิบาย · ไม่งั้น <see cref="OcrLineQuantityAction.RoundedUnitPrice"/> — แท็กบล็อกทั้งคู่)</item>
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

    /// <summary>หน่วยนับของบรรทัด <see cref="OcrLineQuantityAction.QuantityInDescription"/> — จำนวน 1 คือ "ทั้งรายการ" ไม่ใช่ 1 หน่วยมิเตอร์
    /// (เดิม <c>UnitInferrer</c> เติม "หน่วย" ให้ค่าไฟ ⇒ "1 หน่วย × 19,381.50" อ่านผิดความหมาย)</summary>
    public const string LumpSumUnit = "รายการ";

    /// <summary>วลีของหมายเหตุทุกตัวที่ย้ายจำนวนไปไว้ในคำอธิบาย (ค้นในเทสต์/หน้ารีวิว) — <b>เฉพาะบรรทัดที่มีหลักฐานมิเตอร์</b> (ทีม OCR 9: บรรทัดสินค้าไม่เข้าเส้นนี้)</summary>
    public const string QuantityInDescriptionPhrase = "จำนวนหน่วยตามมิเตอร์อยู่ในคำอธิบาย";

    /// <summary>วลีกลางของหมายเหตุที่คงจำนวนแล้วตั้งราคา = ยอด ÷ จำนวน (ไม่พูดถึงมิเตอร์ — บรรทัดสินค้า · ทีม OCR 9)</summary>
    public const string KeepQuantityPhrase = "คงจำนวนตามที่อ่านได้ · ราคาต่อหน่วย = ยอด ÷ จำนวน";

    /// <summary>หน่วยมิเตอร์ที่ชี้ชัดในตัว (kWh · นาที · เดือน) — ของตัวกันนี้เท่านั้น · ทีม OCR 11: ลบ.ม./m3 และ GB/MB ย้ายไป <see cref="ContextMeterUnitRe"/>
    /// (คอนกรีต/ทราย/หิน ขายเป็น ลบ.ม. · "Flash Drive (64 GB)" เป็นสเปกสินค้า)</summary>
    private static readonly Regex StrongMeterUnitRe = new(
        @"^(?:kwh\b|กิโลวัตต์|นาที|mins?\b|minutes?\b|เดือน|months?\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>หน่วยที่เป็นมิเตอร์เฉพาะเมื่อมีบริบท (<see cref="IsMeterUnit"/>): "หน่วย"/unit ⇒ ค่าไฟ · "ครั้ง" ⇒ โทร · ลบ.ม./m3 ⇒ น้ำประปา ·
    /// GB/MB ⇒ อินเทอร์เน็ต/ดาต้า (สินค้า "12 หน่วย" · คอนกรีต "2.75 ลบ.ม." · "64 GB" ทั่วไปไม่นับ)</summary>
    private static readonly Regex ContextMeterUnitRe = new(@"^(?:หน่วย|units?\b|ครั้ง|ลบ\.?\s*ม\.?|ลูกบาศก์เมตร|m3\b|m³|gb\b|mb\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>บริบทอินเทอร์เน็ต/ดาต้าของ GB/MB — "data" ต้องเป็นคำเต็ม ("DataTraveler" คือแฟลชไดรฟ์)</summary>
    private static readonly Regex DataContextRe = new(
        @"อินเทอร์เน็ต|อินเตอร์เน็ต|internet|(?<![A-Za-z])data(?![A-Za-z])|ดาต้า|เน็ตบ้าน|เน็ตมือถือ|ค่าเน็ต|แพ็กเกจ|แพคเกจ|แพ็คเกจ|package|roaming|โรมมิ่ง|broadband|บรอดแบนด์",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>วัสดุก่อสร้างที่ขายเป็น ลบ.ม. — บริบทน้ำของ<b>ทั้งใบ</b> (ใบมีคำ "น้ำประปา") ไม่ทำให้บรรทัดนี้เป็นมิเตอร์ (ร้านวัสดุขายท่อ/ค่าน้ำประปาหน้างานปนกับคอนกรีต)</summary>
    private static readonly Regex BulkMaterialRe = new(
        @"คอนกรีต|ทราย|หิน|ดิน|กรวด|ลูกรัง|ปูน|ยางมะตอย|แอสฟัลต์|concrete|sand|gravel|soil|asphalt",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>บริบทของหน่วยมิเตอร์ที่กำกวม นอกเหนือคำอธิบายบรรทัด — ผู้ขายสาธารณูปโภค (ชนิดบริการ) · ข้อความทั้งใบพูดถึงน้ำประปา ·
    /// <c>default</c> = บรรทัดอย่างเดียว (ตัวประกอบท้ายคำอธิบาย <see cref="QuantityDescription"/> และตัวตัดกลับ <see cref="LumpSumSuffix"/> ใช้ค่านี้ — สมมาตร)</summary>
    private readonly record struct MeterContext(OcrUtilityKind Seller, bool PaperWater);

    /// <summary>บริบทค่าไฟของหน่วย "หน่วย"/unit — ทีม OCR 14 (ฝ่ายค้านรอบ 5 P1-1): + "ค่าพลังงาน" / "ค่า Ft" (บิล กฟภ./กฟน. แยกบรรทัด · จำนวนคือหน่วยที่ใช้)</summary>
    private static readonly Regex ElectricityContextRe = new(@"ค่าไฟ|kwh|กิโลวัตต์|พลังงานไฟฟ้า|ค่าพลังงาน|ค่า\s*ft(?![a-z])|ค่าเอฟที|electricity|energy\s*charge",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>ค่าบริการประจำของบิลสาธารณูปโภค ("ค่าบริการรายเดือน" · service charge · ค่ารักษามาตร) — มิเตอร์เฉพาะเมื่อผู้ขายเป็นสาธารณูปโภค (<see cref="MeterChargeDescription"/>)</summary>
    private static readonly Regex UtilityServiceChargeRe = new(@"ค่าบริการ|ค่ารักษามาตร|ค่ารักษาหม้อ|service\s*(?:charge|fee)|monthly\s*(?:service\s*)?(?:fee|charge)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>ค่าโทร/ดาต้าบนบิลผู้ให้บริการสื่อสาร (ใช้คู่ผู้ขาย <see cref="OcrUtilityKind.Telecom"/> เท่านั้น)</summary>
    private static readonly Regex TelecomChargeRe = new(@"ค่าโทร|ค่าบริการโทร|ค่าอินเทอร์เน็ต|ค่าอินเตอร์เน็ต|ค่าเน็ต|call\s*charge|internet\s*(?:charge|fee)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// **คำอธิบายบรรทัดเป็น "ค่ามิเตอร์" ไหม** (ทีม OCR 14 · ฝ่ายค้านรอบ 5 P1-1) — ค่าไฟ/ค่าพลังงาน/ค่า Ft/Electricity · ค่าน้ำมิเตอร์ (<see cref="OcrMeteredWater"/> —
    /// "Water charge" นับเมื่อผู้ขายเป็นการประปา) · ค่าบริการรายเดือน/service charge <b>บนบิลของผู้ขายสาธารณูปโภค</b> · ค่าโทร/อินเทอร์เน็ตบนบิลผู้ให้บริการสื่อสาร
    /// <para>ผ่อนกฎทีม OCR 11 "ผู้ขายอย่างเดียวไม่ใช่หลักฐาน" <b>เฉพาะเมื่อคำอธิบายเองเป็นค่ามิเตอร์ด้วย</b> — AWN/ทรูมูฟ เอช ขาย "เคส iPhone 16" 3 ชิ้น
    /// ยังคงจำนวน (ล็อกในเทสต์) · บิลจริงที่ไม่มีป้าย "หน่วยที่ใช้" และ engine ไม่ให้หน่วย (PEA ค่าพลังงาน 4,272 · ค่า Ft · PWA Water charge 412 · ค่าน้ำบาดาล 1,251)
    /// เดิมได้ 4,272 × 4.54 − "ส่วนลด" 13.38 + บล็อกการอนุมัติโดยคน ⇒ ตอนนี้ 1 × ยอด + จำนวนในคำอธิบาย (เหมือนเส้น PEA เดิม)</para>
    /// </summary>
    internal static bool MeterChargeDescription(string? description, OcrUtilityKind seller)
    {
        var d = ThaiTextNormalizer.MapThaiPua(description ?? "");
        if (string.IsNullOrWhiteSpace(d)) return false;
        if (ElectricityContextRe.IsMatch(d) || OcrMeteredWater.IsMeteredWater(d, seller)) return true;
        if (seller == OcrUtilityKind.None) return false;
        // ★ ฝ่ายค้าน OCR 14 (P1): วลีที่อาศัยผู้ขาย (ค่าบริการ/service fee/ค่าเน็ต …) ต้อง<b>นำหน้า</b>คำอธิบาย — "เคส iPhone 16 รวมค่าบริการติดตั้ง" × 3 ·
        // "Router WiFi 6 + service fee" · "ซิมการ์ด ค่าเน็ต 30 วัน" · "หลอด LED (ค่าบริการขนส่งรวม)" ของผู้ขายสาธารณูปโภคเป็นสินค้า ต้องคงจำนวน
        var lead = LeadingChargeText(d);
        return UtilityServiceChargeRe.IsMatch(lead) || (seller == OcrUtilityKind.Telecom && TelecomChargeRe.IsMatch(lead));
    }

    /// <summary>ส่วนต้นของคำอธิบายที่วลีค่ามิเตอร์แบบอาศัยผู้ขายต้องขึ้นต้น — ตัดเลขลำดับ/สัญลักษณ์นำหน้า ("1. ", "- ", "•") แล้วคืนข้อความตั้งแต่ตัวแรก
    /// ถ้าวลีเริ่มที่ตำแหน่ง 0 เท่านั้นจึงตรง (regex ทั้งสองไม่มี anchor ⇒ ส่งเฉพาะ prefix ที่ยาวเท่าวลี)</summary>
    private static string LeadingChargeText(string d)
    {
        var t = LeadingNumberingRe.Replace(d, "");
        var m = UtilityServiceChargeRe.Match(t);
        var m2 = TelecomChargeRe.Match(t);
        if (m.Success && m.Index == 0) return m.Value;
        if (m2.Success && m2.Index == 0) return m2.Value;
        return "";
    }

    private static readonly Regex LeadingNumberingRe = new(@"^[\s\d.)\-•*·]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CallContextRe = new(@"โทร|call|voice",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>หน่วยนับสินค้า (นับชิ้น/ชั่ง/ตวง) — หลักฐานว่า "ไม่ใช่มิเตอร์" (ชนะผู้ขายสาธารณูปโภค) · "ชิ้น" ที่ engine ส่งมาเฉย ๆ ไม่นับ (ค่าตั้งต้นที่หลุดมา — <see cref="LineUnit"/>)</summary>
    private static readonly Regex GoodsUnitRe = new(
        @"^(?:ชิ้น|อัน|ด้าม|ม้วน|คู่|ใบ(?!เสร็จ|กำกับ|กํากับ|แจ้ง|สั่ง|ส่ง|ลด|เพิ่ม|รับ|วาง|ขน)|ตัว|ชุด|กล่อง|ถุง|ขวด|แพ็ค|แพค|แพ็ก|ซอง|กระป๋อง|หลอด|ลัง|โหล|แผง|แผ่น"
        + @"|แท่ง|ก้อน|ห่อ|เส้น|เครื่อง|กก\.?|กิโลกรัม|กิโล(?!วัตต์)|กรัม|ลิตร|เมตร|ฟอง|ถัง|แกลลอน|คัน|เล่ม|ดวง|กระสอบ|ถาด|แก้ว|จาน"
        + @"|pcs?\b|ea\b|each\b|kg\b|sets?\b|box(?:es)?\b|bags?\b|rolls?\b|packs?\b|ltr\b|lit(?:re|er)s?\b|bottles?\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>ท้ายคำอธิบายที่ <see cref="QuantityDescription"/> ประกอบ: " (4,272 หน่วย)" · " (จำนวน 12)" · " (… × 4.5369)" — ตัวเดียวทั้งฝั่งเขียนและฝั่งตัด</summary>
    private static readonly Regex QuantitySuffixRe = new(
        @"\s*\((?:จำนวน (?<cn>[\d,]+(?:\.\d+)?)|(?<n>[\d,]+(?:\.\d+)?) (?<u>[^()\s]+))(?: × [\d,]+\.\d{2,4})?\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>หน่วยของบรรทัดสำหรับตัดสินมิเตอร์/ประกอบคำอธิบาย — หน่วยที่ engine ส่ง · ว่าง/"รายการ"/"ชิ้น" (ค่าตั้งต้นที่หลุดมา — กติกาเดียวกับ
    /// <c>UnitInferrer.Resolve</c>) ⇒ อนุมานจากคำอธิบาย (ค่าไฟ → หน่วย · ค่าน้ำ → ลบ.ม. · อินเทอร์เน็ต → เดือน) · ไม่รู้ = null</summary>
    private static string? LineUnit(string? description, string? unit)
    {
        var given = unit?.Trim();
        return string.IsNullOrEmpty(given) || given == LumpSumUnit || given == "ชิ้น" ? UnitInferrer.Infer(description) : given;
    }

    /// <summary>คำหน่วยนี้เป็นหน่วยมิเตอร์ของบรรทัดนี้ไหม (<see cref="StrongMeterUnitRe"/> · หรือหน่วยกำกวมที่มีบริบท: คำอธิบาย → ผู้ขาย → ข้อความทั้งใบ)
    /// <para>ทีม OCR 11 (ฝ่ายค้าน OCR 9 P1-1): ลบ.ม. ⇒ น้ำประปาในคำอธิบาย (<see cref="UnitInferrer.IsWaterSupply"/>) · ผู้ขายการประปา · ใบพูดถึงน้ำประปา
    /// และบรรทัดไม่ใช่วัสดุก่อสร้าง — "คอนกรีตผสมเสร็จ 2.75 ลบ.ม." คงจำนวน 2.75</para></summary>
    private static bool IsMeterUnit(string? unitWord, string? description, MeterContext ctx = default)
    {
        var w = ThaiTextNormalizer.MapThaiPua((unitWord ?? "").Trim());
        if (w.Length == 0) return false;
        if (StrongMeterUnitRe.IsMatch(w)) return true;
        var m = ContextMeterUnitRe.Match(w);
        if (!m.Success) return false;
        var d = ThaiTextNormalizer.MapThaiPua(description ?? "");
        var u = m.Value.ToLowerInvariant();
        if (u.StartsWith("ครั้ง", StringComparison.Ordinal))
            return CallContextRe.IsMatch(d) || ctx.Seller == OcrUtilityKind.Telecom;
        if (u is "gb" or "mb")
            return DataContextRe.IsMatch(d) || ctx.Seller == OcrUtilityKind.Telecom;
        if (u.StartsWith("หน่วย", StringComparison.Ordinal) || u.StartsWith("unit", StringComparison.Ordinal))
            return ElectricityContextRe.IsMatch(d) || ctx.Seller == OcrUtilityKind.Electricity;
        // ลบ.ม. / ลูกบาศก์เมตร / m3 / m³
        return OcrMeteredWater.IsMeteredWater(d) || ctx.Seller == OcrUtilityKind.Water
            || (ctx.PaperWater && !BulkMaterialRe.IsMatch(d));
    }

    /// <summary>คำหน่วยที่ตามหลังตัวเลขบนกระดาษ (หน่วยมิเตอร์ก่อน แล้ว <see cref="UnitRe"/>) · ไม่มี = null</summary>
    private static string? UnitWordAfter(string text, int end)
    {
        var i = end;
        while (i < text.Length && i < end + 3 && (text[i] == ' ' || text[i] == '\t' || text[i] == '\n' || text[i] == '\r')) i++;
        if (i >= text.Length) return null;
        var tail = ThaiTextNormalizer.MapThaiPua(text.Substring(i, Math.Min(16, text.Length - i)));
        var meter = StrongMeterUnitRe.Match(tail);
        if (meter.Success) return meter.Value;
        var ctxMeter = ContextMeterUnitRe.Match(tail);
        if (ctxMeter.Success) return ctxMeter.Value;
        var m = UnitRe.Match(tail);
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// **หลักฐานมิเตอร์ของบรรทัด — ตัวเดียวที่เปิดทาง <see cref="OcrLineQuantityAction.QuantityInDescription"/>** (ทีม OCR 9 · ฝ่ายค้าน 13a4d788 P0/P1)
    /// <para>ลำดับ: บนกระดาษ — ตัวเลขจำนวนที่ยอดตามมาในแถว (ไม่ใช่เลขลำดับแถว) มีป้าย "หน่วยที่ใช้/จำนวนหน่วย" นำหน้า หรือหน่วยมิเตอร์ตามหลัง ⇒ ใช่ ·
    /// หน่วยสินค้าตามหลัง (ขวด/กก./ลิตร …) ⇒ ไม่ใช่ → หน่วยของบรรทัด (<see cref="LineUnit"/>) เป็นหน่วยมิเตอร์ ⇒ ใช่ → ไม่งั้น<b>ไม่ใช่</b> ·
    /// เดิม (13a4d788) "คำหน่วยนับใดก็ได้ตามหลัง" = มิเตอร์ ⇒ น้ำดื่ม 12 ขวด / หมู 5 กก. กลายเป็น 1 × ยอด</para>
    /// <para>★ ทีม OCR 11 (ฝ่ายค้าน OCR 9 P1-2): <b>ผู้ขายสาธารณูปโภคอย่างเดียวไม่ใช่หลักฐาน</b> — เดิมข้อสุดท้าย "ผู้ขาย ⇒ ใช่" ⇒ AWN/ทรูมูฟ เอช ขายเคส iPhone 3 ชิ้น
    /// ได้ 1 × 2,775.70 "(จำนวน 3)" ไม่บล็อก · ตอนนี้ผู้ขายเป็นแค่<b>บริบท</b>ของหน่วยกำกวม (ประปา ⇒ ลบ.ม. · ไฟฟ้า ⇒ หน่วย · สื่อสาร ⇒ ครั้ง/GB) —
    /// ต้องมีคำ/หน่วยมิเตอร์บนบรรทัดเองหรือป้ายมิเตอร์บนกระดาษ ⇒ ผู้ขายอย่างเดียวพาไปได้แค่ <see cref="OcrLineQuantityAction.RoundedUnitPrice"/> (บล็อก)</para>
    /// </summary>
    private static bool MeterEvidence(OcrLinePaper paper, decimal qty, decimal amt, string? description, string? unit, OcrUtilityKind seller)
    {
        var ctx = new MeterContext(seller, OcrMeteredWater.IsMeteredWater(ThaiTextNormalizer.MapThaiPua(paper.Text)));
        var goodsOnPaper = false;
        var nums = paper.Numbers;
        for (var k = 0; k < nums.Count; k++)
        {
            if (nums[k].IsPercent || nums[k].Value != qty || nums[k].RowIndexLike) continue;
            var amountFollows = false;
            for (var j = k + 1; j < nums.Count && j <= k + MaxNumbersBetween + 1; j++)
                if (!nums[j].IsPercent && nums[j].Value == amt) { amountFollows = true; break; }
            if (!amountFollows) continue;
            if (MeterLabelBefore(paper.Text, nums[k].Start)) return true;
            var word = UnitWordAfter(paper.Text, nums[k].End);
            if (word is null) continue;
            if (IsMeterUnit(word, description, ctx)) return true;
            if (GoodsUnitRe.IsMatch(word)) goodsOnPaper = true;
        }
        if (goodsOnPaper) return false;
        var lineUnit = LineUnit(description, unit);
        if (lineUnit is not null && IsMeterUnit(lineUnit, description, ctx)) return true;
        // ★ ทีม OCR 14 (ฝ่ายค้านรอบ 5 P1-1): หน่วยสินค้าบนบรรทัด (ขวด/ถัง/กก. …) ชนะคำอธิบาย · ไม่งั้นคำอธิบายที่เป็นค่ามิเตอร์ (คู่ผู้ขาย) = หลักฐาน
        if (lineUnit is not null && GoodsUnitRe.IsMatch(ThaiTextNormalizer.MapThaiPua(lineUnit))) return false;
        return MeterChargeDescription(description, seller);
    }

    /// <summary>**ตัวเดียวที่ประกอบคำอธิบาย "(จำนวน หน่วย)"** ของ <see cref="OcrLineQuantityAction.QuantityInDescription"/> — service + harness ได้ค่าผ่าน
    /// <see cref="Decide"/> (ห้ามต่อสตริงเอง) · หน่วย = <see cref="LineUnit"/> ถ้าเป็นหน่วยมิเตอร์ ไม่งั้น "จำนวน X" (ทีม OCR 9: ท้ายคำอธิบายมีรูปเดียวที่
    /// <see cref="DescriptionForMatching"/> ตัดกลับได้) · <paramref name="printedUnitPrice"/> = ราคาที่<b>กระดาษพิมพ์</b>แต่เก็บเป็นบรรทัดลงตัวไม่ได้ ("× 4.5369") ·
    /// คำอธิบายที่มีวงเล็บเดียวกันอยู่แล้วไม่ต่อซ้ำ (idempotent) · ยาวเกิน <see cref="DocumentLine.DescriptionMaxLength"/> ⇒ ตัดคำอธิบาย คงท้ายครบ (<see cref="FitDescription"/>)</summary>
    internal static string QuantityDescription(string? description, decimal quantity, string? unit, decimal? printedUnitPrice = null)
    {
        var lineUnit = LineUnit(description, unit);
        var qtyText = quantity.ToString("#,0.###", CultureInfo.InvariantCulture);
        // ทีม OCR 14 (ฝ่ายค้านรอบ 5 P1-1): "เดือน" ที่อนุมานจากคำ "รายเดือน/ประจำเดือน" คือรอบบิล ไม่ใช่หน่วยของจำนวน — "ค่าบริการรายเดือน" 412 เดิมได้ "(412 เดือน)" (หน่วยที่แต่ง)
        if (lineUnit == PeriodUnit && string.IsNullOrWhiteSpace(unit) && PeriodWordRe.IsMatch(description ?? "")) lineUnit = null;
        var inner = lineUnit is not null && IsMeterUnit(lineUnit, description) ? $"{qtyText} {lineUnit}" : $"จำนวน {qtyText}";
        if (printedUnitPrice is decimal p) inner += $" × {p.ToString("#,0.00##", CultureInfo.InvariantCulture)}";
        var suffix = $"({inner})";
        var desc = (description ?? "").TrimEnd();
        if (desc.EndsWith(suffix, StringComparison.Ordinal)) return desc;
        return FitDescription(desc, suffix);
    }

    private const string PeriodUnit = "เดือน";

    /// <summary>คำบอกรอบบิล (ไม่ใช่จำนวน)</summary>
    private static readonly Regex PeriodWordRe = new(@"รายเดือน|ประจำเดือน|monthly", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>**ตัวเดียวที่ต่อท้ายคำอธิบายบรรทัดให้พอดีคอลัมน์** <see cref="DocumentLine.DescriptionMaxLength"/> — คงท้าย (<paramref name="suffix"/>) ครบเสมอ ·
    /// ตัดคำอธิบายแล้วต่อ "…" (ฝ่ายค้าน 13a4d788 P2: เดิมต่อไม่ตัด ⇒ คำอธิบาย 995 ตัวอักษร + ท้าย ⇒ บันทึกล้ม)</summary>
    internal static string FitDescription(string? description, string suffix)
    {
        var max = DocumentLine.DescriptionMaxLength;
        var desc = (description ?? "").TrimEnd();
        if (suffix.Length >= max) return suffix[..max];
        if (desc.Length == 0) return suffix;
        var room = max - suffix.Length - 1;
        if (desc.Length <= room) return $"{desc} {suffix}";
        if (room < 2) return suffix;
        return $"{desc[..(room - 1)].TrimEnd()}… {suffix}";
    }

    /// <summary>
    /// **คำอธิบายสำหรับจับคู่สินค้า/คีย์ตัวเรียน — ตัวเดียว** (ทีม OCR 9 · ฝ่ายค้าน 13a4d788 P1): บรรทัดที่ตัวกันลงเป็น 1 × ยอด ⇒ ตัดท้าย "(4,272 หน่วย)"
    /// ที่ <see cref="QuantityDescription"/> ต่อไว้ — ไม่งั้นทุกเดือนได้คีย์ใหม่ (จำนวนหน่วยเปลี่ยน) · alias ที่เรียนติดจำนวน · <c>ProductMatcher.Normalize</c> เก็บตัวเลข ·
    /// ตัดเฉพาะท้ายที่เป็น<b>เครื่องหมายของตัวกันเอง</b> (<see cref="LumpSumSuffix"/> — ทีม OCR 11 ฝ่ายค้าน OCR 9 P2-3: เดิมตัดทุกวงเล็บ "ตัวเลข คำ" ⇒ "Flash Drive (64 GB)" ⇒
    /// "Flash Drive" ในทุกทางที่เรียก รวม backfill alias จากบรรทัดที่พิมพ์มือ) · ไม่แน่ใจ ⇒ ไม่ตัด (alias ที่พลาดปลอดภัยกว่าชื่อสินค้าผิด) ·
    /// ผู้ใช้: <c>OcrLearnerDescriptionKey.For</c> · หน้านำเข้าสต็อก (พรีวิว + ตอนนำเข้า) · เรียน alias ตอนผูก PO · backfill alias · รายงานซ่อมผลแกะรุ่น 6–8
    /// </summary>
    public static string DescriptionForMatching(string? description, decimal? quantity, string? unit)
    {
        return LumpSumSuffix(description, quantity, unit) is { } s ? s.Base : (description ?? "").Trim();
    }

    /// <summary>
    /// **ท้ายคำอธิบาย "(จำนวน หน่วย)" ที่ตัวกันนี้เขียน — ตัวอ่านกลับตัวเดียว (คู่ของ <see cref="QuantityDescription"/>)** · ทีม OCR 11 (ฝ่ายค้าน OCR 9 P2-3)
    /// <para>เงื่อนไขร่วม: จำนวน 1 · หน่วย <see cref="LumpSumUnit"/> · ท้ายรูป <see cref="QuantitySuffixRe"/> · มีคำอธิบายเหลือ ⇒ แล้ว:
    /// (1) "(จำนวน N)" = เครื่องหมายของตัวกัน (ชื่อสินค้าจริงไม่เขียนแบบนี้) · (2) "(N หน่วย)" ที่<b>ตัวเขียนจะเขียน</b> = หน่วยเป็นหน่วยมิเตอร์ด้วยบริบทของคำอธิบายเอง
    /// (<see cref="IsMeterUnit"/> แบบบรรทัดอย่างเดียว — เงื่อนไขเดียวกับฝั่งเขียน ⇒ สมมาตร · <see cref="Meter"/> = true) ·
    /// (3) ย้อนหลังรุ่น 6/7/8 (ทีม 8 ต่อหน่วยสินค้าใดก็ได้ "(12 ขวด)" · ทีม 9 ต่อ ลบ.ม./GB โดยไม่ดูบริบท): หน่วยเป็นคำหน่วยนับ (<see cref="UnitRe"/> ทั้งคำ — ไม่รวม
    /// GB/MB/ml ซึ่งเป็นสเปกสินค้า) <b>และ</b>ตัวเลขในวงเล็บไม่อยู่ในคำอธิบายเอง ("ปูน 50 กก. (50 กก.)" = สเปก) · นอกนั้น = null (ไม่ตัด — "Flash Drive (64 GB)")</para>
    /// </summary>
    public static OcrLumpSumSuffix? LumpSumSuffix(string? description, decimal? quantity, string? unit)
    {
        var d = (description ?? "").Trim();
        if (quantity != 1m || unit?.Trim() != LumpSumUnit) return null;
        var m = QuantitySuffixRe.Match(d);
        if (!m.Success || m.Index == 0) return null;
        var baseText = d[..m.Index].TrimEnd();
        if (baseText.Length == 0) return null;
        static decimal Num(string t) => decimal.TryParse(t.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;
        if (m.Groups["cn"].Success) return new OcrLumpSumSuffix(baseText, m.Value.Trim(), Meter: false, Num(m.Groups["cn"].Value));
        var unitWord = m.Groups["u"].Value;
        var n = Num(m.Groups["n"].Value);
        if (IsMeterUnit(unitWord, baseText)) return new OcrLumpSumSuffix(baseText, m.Value.Trim(), Meter: true, n, unitWord);
        var mapped = ThaiTextNormalizer.MapThaiPua(unitWord);
        var um = UnitRe.Match(mapped);
        var wholeWord = um.Success && mapped[um.Length..].Trim('.').Length == 0;
        var number = m.Groups["n"].Value;
        var specInDescription = Regex.IsMatch(baseText, @"(?<![\d.,])" + Regex.Escape(number) + @"(?![\d])", RegexOptions.CultureInvariant)
                                || Regex.IsMatch(baseText, @"(?<![\d.,])" + Regex.Escape(number.Replace(",", "")) + @"(?![\d])", RegexOptions.CultureInvariant);
        return wholeWord && !specInDescription ? new OcrLumpSumSuffix(baseText, m.Value.Trim(), Meter: false, n, unitWord) : null;
    }

    /// <summary>
    /// **ท้ายที่อ่านกลับได้ว่า "ไม่ใช่มิเตอร์" (<see cref="OcrLumpSumSuffix.Meter"/> = false) เป็นบรรทัดมิเตอร์จริงเมื่อเล่นด้วยบริบทของตัวตัดสินตอนเขียนไหม** · ทีม OCR 12 (ฝ่ายค้านรอบ 3 P2-2)
    /// <para>ตัวเขียน (<see cref="QuantityDescription"/>) ใช้บริบทบรรทัดอย่างเดียว แต่ตัวตัดสิน (<see cref="MeterEvidence"/>) ใช้ป้ายบนกระดาษ + ชนิดผู้ขาย ⇒ บิลจริง
    /// "ค่าพลังงาน" (ป้าย "จำนวนหน่วยที่ใช้") · "ค่า Ft" (ผู้ขาย กฟภ.) · "Water charge" (ผู้ขาย กปภ.) ถูกเขียนเป็น "(จำนวน N)" ⇒ ตัวอ่านกลับบอก Meter:false ⇒ รายงานซ่อมติดป้าย
    /// "จำนวนสินค้าหาย" บนบิลที่ถูก · ตัวนี้เล่น <see cref="MeterEvidence"/> ซ้ำด้วยจำนวนในวงเล็บ + ยอด + ข้อความกระดาษ + ผู้ขายของสแกน (บริบทเดียวกับตอนเขียน) ·
    /// ไม่มีข้อความกระดาษ/ผู้ขาย ⇒ ตัดสินจากบรรทัดอย่างเดียว (เหมือนเดิม)</para>
    /// </summary>
    public static bool WasMeterLine(OcrLumpSumSuffix suffix, decimal amount, OcrLinePaper? paper, OcrUtilityKind seller)
    {
        if (suffix.Meter) return true;
        if (suffix.Quantity <= 0m || amount <= 0m) return false;
        return MeterEvidence(paper ?? OcrLinePaper.Empty, suffix.Quantity, amount, suffix.Base, suffix.UnitWord, seller);
    }

    /// <summary>ราคาเก็บใน <c>DocumentLine.UnitPrice</c> ได้ตรงตัว (<c>HasPrecision(18,2)</c> — ห้ามขยาย · เจ้าของ 2026-10-16) — ราคาเกิน 2 ตำแหน่ง
    /// ถูกปัดตอนบันทึก ⇒ เปิดแก้แล้วบันทึก round(จำนวน × ราคาที่ปัด) ≠ ยอดบนกระดาษ</summary>
    private static bool PriceFitsDocumentLine(decimal price) => price == R2(price);

    /// <summary>round(ยอด ÷ จำนวน, 2) × จำนวน = ยอดทุกสตางค์ ⇒ ราคานั้น (เลขคณิตลงตัว — ไม่ใช่ราคาที่แต่ง) · ไม่ลงตัว = null</summary>
    private static decimal? TiePrice(decimal qty, decimal amt)
    {
        if (qty <= 0m) return null;
        var p = R2(amt / qty);
        return p > 0m && R2(qty * p) == amt ? p : null;
    }

    /// <summary>ราคา 2 ตำแหน่ง <paramref name="price"/> พิมพ์อยู่บนแถวของกระดาษ (ราคา → ยอดตามมา — <see cref="FindRow"/>) และ round(จำนวน × ราคา) = ยอดทุกสตางค์</summary>
    private static bool PrintedPriceTies(OcrLinePaper paper, decimal qty, decimal price, decimal amt)
        => price > 0m && R2(qty * price) == amt && FindRow(paper, qty, price, amt).Found;

    /// <summary>ราคาเกิน 2 ตำแหน่งนี้ถูก<b>พิมพ์</b>บนข้อความกระดาษ (ไม่ใช่ระบบหารเอง) — ค้นตัวอักษรของตัวเลขตรง ๆ ("32.4567" · "1,234.5678")
    /// เพราะตัวแยกตัวเลข (<see cref="NumberRe"/>) ตั้งใจตัดทศนิยม 2 ตำแหน่งก่อนเพื่อแยกช่องที่ PdfPig ต่อติดกัน ("4272.0019,381.50")</summary>
    private static bool OnPaper(OcrLinePaper paper, decimal value)
    {
        if (string.IsNullOrEmpty(paper.Text)) return false;
        foreach (var fmt in new[] { "0.####", "#,0.####" })
        {
            var t = value.ToString(fmt, CultureInfo.InvariantCulture);
            if (Regex.IsMatch(paper.Text, @"(?<![\d.,])" + Regex.Escape(t) + @"(?![\d])", RegexOptions.CultureInvariant)) return true;
        }
        return false;
    }

    private static OcrLineQuantityDecision InDescription(
        OcrLineQuantityAction action, decimal qty, decimal amt, string? description, string? unit, decimal? printedPrice, string note)
        => new(action, 1m, amt, null, note,
            Description: QuantityDescription(description, qty, unit, printedPrice), Unit: LumpSumUnit);

    /// <summary>เพดาน "เศษจากการปัดราคาต่อหน่วย" ที่ยอมเก็บเป็นส่วนลดบรรทัด (<see cref="OcrLineQuantityAction.RoundedUnitPrice"/>) — ตัวเลขเดียวกับเพดานเศษถอด VAT
    /// (<see cref="OcrEtaxLineNormalizer.MaxVatStripResidual"/> 1.00 บาท และ <see cref="OcrEtaxLineNormalizer.MaxVatStripResidualShare"/> 0.5% ของยอดบรรทัด):
    /// ราคา 2 ตำแหน่งบิดยอดได้ไม่เกิน จำนวน × 0.01 — เกินบาท/เกินครึ่งเปอร์เซ็นต์ = ราคาบนกระดาษเกิน 2 ตำแหน่งหรือจำนวนผิด ไม่ใช่ "เศษ" (ทีม OCR 11 · ฝ่ายค้าน OCR 9 P2-1:
    /// น็อต 100,000 ตัว / 500 เดิมได้ "ส่วนลด" 500 = 50% ของบรรทัด)</summary>
    internal static bool RoundingRemainderWithinCap(decimal remainder, decimal amount)
        => remainder >= 0m && remainder <= OcrEtaxLineNormalizer.MaxVatStripResidual
           && remainder <= Math.Abs(amount) * OcrEtaxLineNormalizer.MaxVatStripResidualShare;

    /// <summary>วลีของเศษปัดราคาต่อหน่วย — หมายเหตุ · ป้ายส่วนลดหน้ารีวิว (<see cref="OcrEtaxLineNormalizer.DiscountLabel"/>) · ไม่ใช่ส่วนลดบนกระดาษ</summary>
    public const string RoundingRemainderPhrase = "เศษจากการปัดราคาต่อหน่วย";

    /// <summary>รหัสในหมายเหตุ <see cref="Tag"/> ของบรรทัดที่เศษปัดราคาต่อหน่วย<b>เกินเพดาน</b> (<see cref="RoundingRemainderWithinCap"/>) — ทีม OCR 12 (ฝ่ายค้านรอบ 3 P2-1):
    /// เศษก้อนใหญ่ในช่องส่วนลดบรรทัดห้ามถึงเอกสารที่ออก ⇒ <see cref="OcrPostingReadiness.HumanApprovalBlockingMarkers"/> บล็อกการอนุมัติโดยคนด้วย</summary>
    public const string RoundingOverCapCode = "OCR-ROUNDING-OVER-CAP";

    /// <summary>
    /// **ทางไปต่อของบรรทัดสินค้าที่เศษปัดเกินเพดาน — ตัวเดียว** (ทีม OCR 14 · ฝ่ายค้านรอบ 5 P1-1: ข้อความเดิม "เปลี่ยนหน่วยนับเป็นแพ็ก เช่น ราคาต่อ 100 ชิ้น" ไม่บอกว่าพิมพ์อะไร ·
    /// และ "แกะใหม่" ได้ผลเดิมเพราะตัวตัดสิน deterministic) — บอกค่าที่ต้องพิมพ์ทุกช่อง ยอดบรรทัดเท่าเดิมทุกทาง:
    /// (1) หน่วยแพ็ก 10/100/1,000 ที่ จำนวนแพ็ก × ราคาแพ็ก 2 ตำแหน่ง = ยอดทุกสตางค์ (ไม่มี ⇒ ไม่เสนอ) · (2) 1 × ยอด + จำนวนในคำอธิบาย (จำนวนสต็อกจะเป็น 1 — ผู้ใช้ตัดสิน) ·
    /// (3) จำนวนผิด ⇒ แก้จำนวนตามกระดาษ · ผู้เรียก: หมายเหตุ <see cref="Rounded"/> และเหตุผลของด่านอนุมัติโดยคน (<see cref="OcrPostingReadiness.EvaluateHumanApproval"/>)
    /// </summary>
    public static string OverCapWayForward(decimal quantity, decimal amount)
    {
        var parts = new List<string>();
        foreach (var k in new[] { 10m, 100m, 1000m, 10000m })
        {
            var packs = quantity / k;
            if (packs < 1m || packs != Math.Floor(packs)) continue;
            if (TiePrice(packs, amount) is decimal packPrice)
            {
                parts.Add($"ถ้าราคาบนกระดาษเป็นราคาต่อชิ้นเกิน 2 ตำแหน่ง ⇒ พิมพ์ จำนวน {packs:#,0} · หน่วย \"แพ็ก {k:#,0}\" · ราคา {packPrice:N2} · ส่วนลด 0 (ยอด {amount:N2} เท่าเดิม)");
                break;
            }
        }
        parts.Add($"หรือ จำนวน 1 · ราคา {amount:N2} · ส่วนลด 0 แล้วต่อท้ายคำอธิบาย \"(จำนวน {quantity:#,0.###})\" (จำนวนเข้าสต็อกจะเป็น 1)");
        parts.Add("หรือถ้าจำนวนอ่านผิด ⇒ แก้จำนวนตามกระดาษ · \"แกะใหม่\" ได้ผลเดิม (กระดาษเดิม คำนวณเดิม)");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// **ทางไปต่อของบรรทัดมิเตอร์ที่ผลแกะรุ่นก่อน (≤ 12) เก็บเป็นราคาปัด + เศษในช่องส่วนลด** (ทีม OCR 14 · ฝ่ายค้านรอบ 5 P1-1 · ข้อมูลที่ persist F2 ข้อ 9) —
    /// รุ่นนี้ลงบรรทัดมิเตอร์เป็น 1 × ยอด เอง ⇒ "แกะใหม่"/สร้างเอกสารจากสแกนเดิมอีกครั้งได้บรรทัดที่ถูกโดยไม่ต้องพิมพ์ · หรือพิมพ์ค่าตามนี้
    /// </summary>
    /// <param name="rescanFixes">false = เอกสารลูก (คัดลอก/แปลง) — แกะสแกนใหม่ไม่แก้ใบนี้ ⇒ บอกเฉพาะค่าที่ต้องพิมพ์</param>
    public static string MeterLineWayForward(decimal quantity, decimal amount, string? description, string? unit, bool rescanFixes = true)
        => "บรรทัดมิเตอร์: "
           + (rescanFixes ? $"ลบ Draft แล้วกด \"แกะใหม่\" ที่หน้าสแกน (ไม่ใช้โควต้า — ระบบรุ่นนี้ลงเป็น 1 × {amount:N2} พร้อมจำนวนหน่วยในคำอธิบายให้เอง ไม่ต้องพิมพ์) หรือ" : "")
           + $"พิมพ์ในใบ Draft: จำนวน 1 · ราคา {amount:N2} · ส่วนลด 0 · คำอธิบาย \"{QuantityDescription(DescriptionForMatching(description, quantity, unit), quantity, unit)}\"";

    /// <summary>ราคา 2 ตำแหน่งที่ <see cref="Rounded"/> ตั้งได้จาก ยอด ÷ จำนวน — round ปกติ และปัดขึ้น 1 สตางค์ (ใช้จำผลของตัวเองตอนรันซ้ำบน ExtractedItemsJson)</summary>
    private static (decimal Nearest, decimal Up) RoundedPrices(decimal qty, decimal amt)
    {
        var nearest = R2(amt / qty);
        if (nearest <= 0m) nearest = 0.01m;
        var up = R2(qty * nearest) < amt ? Math.Ceiling(amt / qty * 100m) / 100m : nearest;
        return (nearest, up);
    }

    /// <summary><see cref="OcrLineQuantityAction.RoundedUnitPrice"/> — คงจำนวนเสมอ (ห้ามทิ้งจำนวนเงียบ · ทีม OCR 9) · แท็กบล็อกเสมอ
    /// <para>ทีม OCR 11 (ฝ่ายค้าน OCR 9 P2-1/P2-2): (ก) เศษ ≤ เพดาน (<see cref="RoundingRemainderWithinCap"/>) ⇒ ราคา round(ยอด ÷ จำนวน, 2) (ปัดขึ้น 1 สตางค์เมื่อคูณแล้วต่ำกว่ายอด)
    /// · เศษเป็นส่วนลดบรรทัดให้ round(จำนวน × ราคา) − ส่วนลด = ยอดทุกสตางค์ (VAT คิดจากฐานบนกระดาษ · บันทึกซ้ำไม่เลื่อน) แต่<b>ติดป้าย
    /// "<see cref="RoundingRemainderPhrase"/>" ไม่ใช่ส่วนลดบนกระดาษ</b> (ผู้เรียกเก็บ <c>UnitPriceRoundingResidual</c>) · (ข) เศษเกินเพดาน ⇒ ราคา round(ยอด ÷ จำนวน, 2)
    /// <b>ไม่แต่งส่วนลด</b> · บรรทัดไม่ลงตัวโดยตั้งใจ (ยอดบรรทัด = ยอดบนกระดาษ · จำนวน × ราคา ≠ ยอด) · แท็กบอกให้ตรวจราคาต่อหน่วยบนกระดาษ ·
    /// ข้อความไม่ขึ้นกับทางเข้า (<paramref name="why"/>/ราคาที่อ่าน) ⇒ รันซ้ำบน ExtractedItemsJson ได้หมายเหตุเดิมทุกตัวอักษร (MergeNotes ไม่ต่อซ้ำ)</para></summary>
    private static OcrLineQuantityDecision Rounded(decimal qty, decimal amt, decimal? readPrice, string why)
    {
        var (nearest, up) = RoundedPrices(qty, amt);
        var gap = R2(qty * up) - amt;
        if (!RoundingRemainderWithinCap(gap, amt))
        {
            // ★ ทีม OCR 12 (ฝ่ายค้านรอบ 3 P2-1): เดิม (ทีม 11) "ไม่แต่งส่วนลด" ⇒ เก็บ 100,000 × 0.01 แต่ยอด 500 — ขัด CLAUDE #2 A (ยอด = round(จำนวน × ราคา, 2)) และ
            // เปิดแก้แล้วบันทึก ComputeLineAmounts คิดใหม่เป็น 1,000 เงียบ ๆ · DocumentLine.UnitPrice เก็บ 2 ตำแหน่ง (HasPrecision(18,2) — ห้ามขยาย) ⇒ รูปที่สอดคล้องรูปเดียวที่
            // คงจำนวนกระดาษคือ ราคาปัด + เศษในช่องส่วนลดบรรทัด (ป้าย "เศษจากการปัดราคาต่อหน่วย" ผ่าน UnitPriceRoundingResidual — ไม่ใช่ส่วนลดบนกระดาษ) ·
            // เศษเกินเพดาน = ราคาบนกระดาษเกิน 2 ตำแหน่งหรือจำนวนผิด ⇒ รหัส RoundingOverCapCode ⇒ ด่านอนุมัติ<b>โดยคน</b>ก็บล็อก (OcrPostingReadiness.EvaluateHumanApproval ·
            // คำตัดสินข้อ 150: อนุมัติ = ออกเอกสาร + ลง GL) จนกว่าบรรทัดเลิกถือค่าที่ระบบตั้ง ⇒ ส่วนลดปลอมไม่มีทางถึงเอกสารที่ออก
            var overGross = R2(qty * up);
            return new(OcrLineQuantityAction.RoundedUnitPrice, null, up, gap,
                $"{Tag} {RoundingOverCapCode}: {qty:#,0.###} × {up:N2} = {overGross:N2} ≠ ยอดบนกระดาษ {amt:N2} (ต่าง {gap:+#,0.00;-#,0.00}) — ไม่มีราคาต่อหน่วย 2 ตำแหน่งที่คูณจำนวนแล้วได้ยอด "
                + $"และเศษเกินเพดานปัด ({OcrEtaxLineNormalizer.MaxVatStripResidual:0.00} บาท / {OcrEtaxLineNormalizer.MaxVatStripResidualShare * 100m:0.#}% ของบรรทัด) ⇒ "
                + $"ระบบคงจำนวนตามที่อ่านได้ · เก็บส่วนต่าง {gap:N2} ในช่องส่วนลดบรรทัดเป็น{RoundingRemainderPhrase} (ไม่ใช่ส่วนลดบนกระดาษ · ยอดบรรทัด = ยอดบนกระดาษ · บันทึกซ้ำไม่เลื่อน) · "
                + "อนุมัติไม่ได้จนกว่าจะแก้บรรทัด — " + OverCapWayForward(qty, amt)
                + Fingerprint(qty, up));
        }
        var grossUp = R2(qty * up);
        return new(OcrLineQuantityAction.RoundedUnitPrice, null, up, gap > 0m ? gap : null,
            $"{Tag} {why} — ระบบ{KeepQuantityPhrase}: {qty:#,0.###} × {up:N2} = {grossUp:N2} · ยอดบนกระดาษ {amt:N2} · "
            + (gap > 0m ? $"ส่วนต่าง {gap * 100m:0.##} สตางค์ = {RoundingRemainderPhrase} (ไม่ใช่ส่วนลดบนกระดาษ · เก็บในช่องส่วนลดบรรทัด {gap:N2} ให้ยอดตรงกระดาษ · บันทึกซ้ำไม่เลื่อน)" : "ลงตัวทุกสตางค์")
            + (readPrice is decimal rp ? $" · ราคาที่อ่านได้ {rp:#,0.00##}" : "")
            + " · ตรวจจำนวน/ราคาต่อหน่วยกับกระดาษก่อนอนุมัติ" + Fingerprint(qty, up));
    }

    /// <summary>ตัดสินบรรทัดหนึ่ง — ดูกติกาที่หัวคลาส · <paramref name="paper"/> null/ว่าง = ไม่มีหลักฐานจากกระดาษ (พฤติกรรมเดิม + แท็ก) ·
    /// <paramref name="description"/>/<paramref name="unit"/> = คำอธิบาย/หน่วยของบรรทัด (หลักฐานมิเตอร์ + ใช้เมื่อย้ายจำนวนไปไว้ในคำอธิบาย) ·
    /// <paramref name="utilitySeller"/> = ชนิดบริการของผู้ขายสาธารณูปโภค (<see cref="OcrUtilityVendor.KindOf"/> — ผู้เรียกตัดสินจากเลขผู้เสียภาษี/ชื่อของใบ) ·
    /// ทีม OCR 11: เป็นบริบทของหน่วยกำกวมเท่านั้น ไม่ใช่หลักฐานมิเตอร์ด้วยตัวเอง (<see cref="MeterEvidence"/>)</summary>
    public static OcrLineQuantityDecision Decide(
        decimal? quantity, decimal? unitPrice, decimal? amount, decimal? lineDiscount, OcrLinePaper? paper,
        string? description = null, string? unit = null, OcrUtilityKind utilitySeller = OcrUtilityKind.None)
    {
        paper ??= OcrLinePaper.Empty;
        var amt = amount ?? 0m;
        var up = unitPrice ?? 0m;
        var qty = quantity ?? 0m;
        if (amt <= 0m) return Keep;
        var tol = Math.Max(1m, Math.Abs(amt) * 0.02m);
        var qtyLooksLikeAmount = Math.Abs(qty - amt) <= tol;

        // ราคา/หน่วยหาย แต่มียอด+จำนวน (บิลสาธารณูปโภคพิมพ์แต่ปริมาณกับยอดรวม · engine อ่านคอลัมน์ราคาหลุด · AI แตกบรรทัด/ระบบภายนอกไม่ส่งราคา)
        // ★ ทีม OCR 8 (2026-10-16 · บิล PEA จริง): เดิมเติม ยอด ÷ จำนวน ทศนิยม 4 ⇒ DocumentLine เก็บ 2 ตำแหน่ง ⇒ บันทึกซ้ำเลื่อน
        // ★ ทีม OCR 9 (ฝ่ายค้าน 13a4d788 P0): ทีม 8 ลง 1 × ยอด ทุกบรรทัดที่ราคาไม่พิมพ์บนแถว ⇒ น้ำดื่ม 12 / 300 ⇒ 1 × 300 (สต็อกขาด 11 · ทุนเพี้ยน 12 เท่า) ·
        //   ตอนนี้: จำนวน 1 ⇒ ราคา = ยอด · ยอด ÷ จำนวน ลงตัวทุกสตางค์ ⇒ คงจำนวน (รุ่น 5 ได้ 12 × 25.00 เหมือนกัน) · ไม่ลงตัว: มิเตอร์ ⇒ 1 × ยอด ·
        //   สินค้า/ไม่รู้ ⇒ คงจำนวน + ราคาปัด 2 ตำแหน่ง + ส่วนลดเศษ + แท็กบล็อก
        if (up <= 0m && qty > 0m)
        {
            if (qty == 1m) return new(OcrLineQuantityAction.FillUnitPrice, null, amt, null, null);
            if (TiePrice(qty, amt) is decimal filled)
            {
                if (PrintedPriceTies(paper, qty, filled, amt))
                    return new(OcrLineQuantityAction.FillUnitPrice, null, filled, null, null);
                // จำนวน ≈ ยอด (engine อ่านยอดมาใส่ช่องจำนวน — 300 × 1.00) = ลงตัวโดยบังเอิญ ⇒ บล็อก · ไม่งั้นแจ้ง
                return new(OcrLineQuantityAction.FillUnitPrice, null, filled, null, qtyLooksLikeAmount
                    ? $"{Tag} จำนวน {qty:#,0.###} เท่ากับยอด {amt:N2} และไม่มีราคาต่อหน่วย — ระบบตั้งราคา = ยอด ÷ จำนวน = {filled:N2} · จำนวนอาจเป็นยอดที่อ่านหลงคอลัมน์ — ตรวจกับกระดาษก่อนอนุมัติ"
                        + Fingerprint(qty, filled)
                    : $"{NoteTag} ราคาต่อหน่วย {filled:N2} = ยอด {amt:N2} ÷ จำนวน {qty:#,0.###} ลงตัวทุกสตางค์ (engine ไม่ส่งราคาต่อหน่วย) — {KeepQuantityPhrase}");
            }
            if (MeterEvidence(paper, qty, amt, description, unit, utilitySeller))
                return InDescription(OcrLineQuantityAction.QuantityInDescription, qty, amt, description, unit, null,
                    $"{NoteTag} {QuantityInDescriptionPhrase} — กระดาษพิมพ์จำนวน {qty:#,0.###} และยอด {amt:N2} แต่ไม่พิมพ์ราคาต่อหน่วย ⇒ "
                    + $"ระบบลง 1 × {amt:N2} (ไม่ตั้งราคาต่อหน่วยที่กระดาษไม่พิมพ์ · ยอดตรงกระดาษ · บันทึกซ้ำไม่เลื่อน)");
            return Rounded(qty, amt, null,
                $"จำนวน {qty:#,0.###} · ยอด {amt:N2} · ไม่มีราคาต่อหน่วย และ ยอด ÷ จำนวน ไม่ลงตัวทุกสตางค์");
        }
        if (up <= 0m || qty <= 0m) return Keep;

        var gross = R2(up * qty);
        var gap = gross - amt;
        var proven = OcrEtaxLineNormalizer.ProvenLineDiscount(qty, up, amt, lineDiscount);
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
        if (Math.Abs(computed - amt) <= tol)   // ตรงอยู่แล้ว (เกณฑ์เดิม)
        {
            // ★ ทีม OCR 8: "ลงตัวในเกณฑ์ ±2%" ≠ เก็บได้ — ราคาเกิน 2 ตำแหน่ง (สแกนเก่าที่ persist 4.5369 · ราคาน้ำมัน 4 ตำแหน่ง) ⇒ DocumentLine ปัดเหลือ 2 ตำแหน่ง
            // ★ ทีม OCR 9 (ฝ่ายค้าน 13a4d788 P1): ด่านต้องถาม "ปัด 2 ตำแหน่งแล้วลงตัวไหม" ไม่ใช่ "ราคามีเกิน 2 ตำแหน่งไหม" — 1.235 × 80.0405 = 98.85 และ
            //   1.235 × 80.04 = 98.85 เหมือนกัน (เดิมตีเป็น 1 × 98.85 พร้อมหมายเหตุ "98.85 ≠ 98.85") ⇒ ไม่แตะ · ไม่ลงตัว: ยอด ÷ จำนวน ลงตัว ⇒ ราคานั้น ·
            //   มิเตอร์ ⇒ 1 × ยอด · ไม่งั้นคงจำนวน + แท็กบล็อก · ราคา 2 ตำแหน่ง / จำนวน 1 / ส่วนลดที่พิสูจน์แล้ว ⇒ ไม่แตะ (ทิศตรงข้ามล็อกในเทสต์)
            if (proven == 0m && qty != 1m && !PriceFitsDocumentLine(up) && R2(qty * R2(up)) != amt)
            {
                var storedLine = $"เอกสารเก็บ 2 ตำแหน่ง: {qty:#,0.###} × {R2(up):N2} = {R2(qty * R2(up)):N2} ≠ ยอด {amt:N2} — บันทึกซ้ำแล้วยอดเลื่อน";
                if (TiePrice(qty, amt) is decimal tied)
                    return new(OcrLineQuantityAction.FixUnitPrice, null, tied, null,
                        $"{NoteTag} ราคาต่อหน่วย {up:0.00##} เกิน 2 ตำแหน่ง ({storedLine}) ⇒ ราคา {tied:N2} = ยอด ÷ จำนวน ลงตัวทุกสตางค์ · {KeepQuantityPhrase}");
                if (MeterEvidence(paper, qty, amt, description, unit, utilitySeller))
                    return InDescription(OcrLineQuantityAction.QuantityInDescription, qty, amt, description, unit, OnPaper(paper, up) ? up : null,
                        $"{NoteTag} {QuantityInDescriptionPhrase} — ราคาต่อหน่วย {up:0.00##} เกิน 2 ตำแหน่ง ({storedLine}) ⇒ ระบบลง 1 × ยอดตามกระดาษ");
                return Rounded(qty, amt, up, $"ราคาต่อหน่วย {up:0.00##} เกิน 2 ตำแหน่ง ({storedLine})");
            }
            // ★ ทีม OCR 12 (ฝ่ายค้านรอบ 3 P2-1): สแกนรุ่น 9 ที่ persist บรรทัดเกินเพดานแบบไม่สอดคล้อง (4,272 × 4.54 ยอด 19,381.50 — ส่วนต่าง 13.38 อยู่ในเกณฑ์ ±2%
            // จึงเคย Keep) ⇒ เส้นสร้างเอกสารรันซ้ำต้องได้รูปสอดคล้องของ Rounded · แคบ: ราคา = round(ยอด ÷ จำนวน, 2) ของตัวกันเอง + เศษเกินเพดาน (ใบที่ราคากระดาษ 33.33 × 3 = 100.00
            // ส่วนต่างสตางค์เดียวไม่ถูกแตะ)
            // ★ ทีม OCR 14 (ฝ่ายค้านรอบ 5 P1-1): บรรทัดมิเตอร์ที่รุ่นก่อนเก็บเป็นราคาปัด (ผู้เรียกส่งเศษปัดของตัวกันเป็น lineDiscount = null) ⇒ 1 × ยอด
            //   + จำนวนในคำอธิบาย ทั้งเศษใต้/เกินเพดาน (บรรทัดมิเตอร์ห้ามถือ "ส่วนลด" ที่ระบบแต่ง และห้ามบล็อกคน) · สินค้าคงกติกาเดิม (เกินเพดานเท่านั้น)
            if (proven == 0m && qty != 1m && !qtyLooksLikeAmount && gap != 0m
                && RoundedPrices(qty, amt) is var legacy && (up == legacy.Nearest || up == legacy.Up))
            {
                if (MeterEvidence(paper, qty, amt, description, unit, utilitySeller))
                    return InDescription(OcrLineQuantityAction.QuantityInDescription, qty, amt, description, unit, null, RerunMeterNote(line, amt));
                if (!RoundingRemainderWithinCap(Math.Abs(gap), amt))
                    return Rounded(qty, amt, up, $"{line} · ราคาต่อหน่วยคือ ยอด ÷ จำนวน ที่ปัด 2 ตำแหน่งแล้ว");
            }
            return Keep;
        }

        // ★ ทีม OCR 11 (ฝ่ายค้าน OCR 9 P2-1): ราคา = round(ยอด ÷ จำนวน, 2) อยู่แล้ว แต่คูณกลับห่างยอดเกินเกณฑ์ (ราคาต่อหน่วยเศษสตางค์ 100,000 × 0.005 —
        // ผลของ Rounded เกินเพดานที่ persist ไว้ หรือ engine ปัดราคาเอง) ⇒ ตัวผิดคือ "ราคาเก็บ 2 ตำแหน่งไม่ได้" ไม่ใช่จำนวน ⇒ ห้ามหารจำนวนใหม่ (เดิมได้ 50,000) ·
        // ตัดสินแบบ Rounded ซ้ำ (บล็อก · หมายเหตุเดิม) — รันซ้ำตอนสร้างเอกสารต้องได้คำตอบเดิม
        if (proven == 0m && qty != 1m && !qtyLooksLikeAmount && RoundedPrices(qty, amt) is var rp && (up == rp.Nearest || up == rp.Up))
            return MeterEvidence(paper, qty, amt, description, unit, utilitySeller)
                ? InDescription(OcrLineQuantityAction.QuantityInDescription, qty, amt, description, unit, null, RerunMeterNote(line, amt))
                : Rounded(qty, amt, up, $"{line} · ราคาต่อหน่วยคือ ยอด ÷ จำนวน ที่ปัด 2 ตำแหน่งแล้ว");

        // ⚠️ เคส ก (บิลค่าไฟ 59 ล้าน — เดิม): ราคา ≈ ยอดทั้งที่จำนวน > 1 ⇒ ตัวผิดคือราคา ไม่ใช่จำนวน (3,611 หน่วยคือมิเตอร์จริง)
        if (qty > 1m && !qtyLooksLikeAmount && Math.Abs(up - amt) <= tol)
        {
            // ราคา 2 ตำแหน่งที่ลงตัวพิมพ์อยู่บนแถว ("ปากกา 2 ด้าม 10.00 20.00" · engine อ่านยอดมาใส่ช่องราคา) ⇒ ราคาจากกระดาษ (แจ้ง)
            var paperPrice = R2(amt / qty);
            if (PrintedPriceTies(paper, qty, paperPrice, amt))
                return new(OcrLineQuantityAction.FixUnitPrice, null, paperPrice, null,
                    $"{NoteTag} ราคาต่อหน่วย {paperPrice:N2} จากแถวบนกระดาษ · ราคาที่อ่านได้ {up:N2} คือยอดทั้งบรรทัด — {qty:#,0.###} × {paperPrice:N2} = {amt:N2}");
            // ฝ่ายค้าน P2-3: จำนวนพิมพ์คู่หน่วยนับ (หรือหลังป้าย "หน่วยที่ใช้") แล้วยอดตามมา = จำนวนพิสูจน์แล้ว (สินค้าหรือมิเตอร์ก็ได้ — ไม่ใช่หลักฐานมิเตอร์)
            var qtyOnPaper = MeterQuantityPrinted(paper, qty, amt, Math.Round(amt / qty, 4, MidpointRounding.AwayFromZero));
            // ★ ทีม OCR 9 (ฝ่ายค้าน P1): "น้ำดื่ม 12 ขวด 300.00" ⇒ 12 × 25.00 (รุ่น 5 เดิม) — ยอด ÷ จำนวน ลงตัวทุกสตางค์ = เลขคณิต ไม่ใช่ราคาที่แต่ง
            if (TiePrice(qty, amt) is decimal tied)
                return qtyOnPaper
                    ? new(OcrLineQuantityAction.FixUnitPrice, null, tied, null,
                        $"{NoteTag} จำนวน {qty:#,0.###} พิมพ์คู่หน่วยนับบนกระดาษ · ราคาที่อ่านได้ {up:N2} คือยอดทั้งบรรทัด — ราคาต่อหน่วย = ยอด ÷ จำนวน = {tied:N2} ลงตัวทุกสตางค์")
                    : new(OcrLineQuantityAction.FixUnitPrice, null, tied, null,
                        $"{Tag} ราคาต่อหน่วย {up:N2} เท่ากับยอดทั้งบรรทัด {amt:N2} (จำนวน {qty:#,0.###}) — ระบบตั้งราคาต่อหน่วย = ยอด ÷ จำนวน = {tied:N2} (ลงตัวทุกสตางค์) "
                        + "แต่ไม่พบจำนวนนี้คู่หน่วยนับบนกระดาษ · ตรวจกับกระดาษก่อนอนุมัติ" + Fingerprint(qty, tied));
            // ★ ทีม OCR 8: บิลมิเตอร์ (กระดาษไม่พิมพ์ราคาต่อหน่วย) ⇒ ห้ามแต่ง ยอด ÷ จำนวน (เดิม 4 ตำแหน่ง ⇒ บันทึกซ้ำเลื่อน 6.35) — 1 × ยอด + จำนวนในคำอธิบาย ·
            // ★ ทีม OCR 9: เฉพาะเมื่อมีหลักฐานมิเตอร์ (ไม่ใช่ "คำหน่วยนับใดก็ได้") — สินค้า ⇒ คงจำนวน + แท็กบล็อก
            if (MeterEvidence(paper, qty, amt, description, unit, utilitySeller))
                return qtyOnPaper
                    ? InDescription(OcrLineQuantityAction.QuantityInDescription, qty, amt, description, unit, null,
                        $"{NoteTag} {QuantityInDescriptionPhrase} — จำนวน {qty:#,0.###} พิมพ์บนกระดาษ · ราคาที่อ่านได้ {up:N2} คือยอดทั้งบรรทัด และกระดาษไม่พิมพ์ราคาต่อหน่วย ⇒ "
                        + $"ระบบลง 1 × {amt:N2} (ไม่ตั้งราคาต่อหน่วยที่กระดาษไม่พิมพ์)")
                    : InDescription(OcrLineQuantityAction.QuantityInDescription, qty, amt, description, unit, null,
                        $"{Tag} ราคาต่อหน่วย {up:N2} เท่ากับยอดทั้งบรรทัด {amt:N2} (จำนวน {qty:#,0.###}) และไม่พบจำนวนนี้คู่หน่วยนับบนกระดาษ — "
                        + $"ระบบลง 1 × {amt:N2} · {QuantityInDescriptionPhrase} · ตรวจจำนวนกับกระดาษก่อนอนุมัติ"
                        + Fingerprint(1m, amt));
            return Rounded(qty, amt, up,
                $"ราคาต่อหน่วย {up:N2} เท่ากับยอดทั้งบรรทัด {amt:N2} (จำนวน {qty:#,0.###}) · ยอด ÷ จำนวน ไม่ลงตัวทุกสตางค์");
        }

        // ★ จำนวนพิมพ์อยู่บนแถว + ไม่มีอะไรอธิบาย ⇒ ห้ามแต่งจำนวน (เคส ข "จำนวน = ยอด" ยังแก้ตามเดิม — จำนวนนั้นไม่ใช่จำนวน)
        if (!qtyLooksLikeAmount && row.QuantityPrinted)
        {
            // แถวมีคำหัก ณ ที่จ่าย/มัดจำ ⇒ ส่วนต่างห้ามเป็นส่วนลด (ฝ่ายค้าน P1-1 ข้อ a)
            if (gap > 0m && row.DeductionKind is string ctx)
                return Deduction(qty, up, gross, amt, gap, ctx, line);
            // ฝ่ายค้าน P2-5: ห้ามเก็บ จำนวน × ราคา − ส่วนลด ≠ ยอด — ยอดตามกระดาษ · จำนวน/ราคาตามกระดาษ · ส่วนต่างเป็นส่วนลดที่ระบุชัด
            if (gap > 0m)
                return new(OcrLineQuantityAction.KeepPrintedGap, null, null, gap,
                    $"{Tag} {line} (ต่าง {gap:N2}) และกระดาษไม่พิมพ์อะไรอธิบายส่วนต่าง — ระบบคงจำนวน/ราคาตามกระดาษ (ไม่หารจำนวนใหม่) "
                    + $"และเก็บส่วนต่างเป็นส่วนลดบรรทัด {gap:N2} ให้ยอดตรงกระดาษ · ส่วนต่างที่กระดาษไม่บอก — ตรวจ" + Fingerprint(qty, up));
            // ส่วนต่างลบ (ยอดมากกว่า จำนวน × ราคา): ทีม OCR 8 ⇒ 1 × ยอด + จำนวน/ราคาที่พิมพ์ในคำอธิบาย · ทีม OCR 9 ⇒ เฉพาะมิเตอร์ — สินค้าคงจำนวน (แท็กบล็อกทั้งคู่)
            if (MeterEvidence(paper, qty, amt, description, unit, utilitySeller))
                return InDescription(OcrLineQuantityAction.KeepPrintedGap, qty, amt, description, unit, up,
                    $"{Tag} {line} (ยอดมากกว่า {-gap:N2}) และกระดาษไม่พิมพ์อะไรอธิบาย — ระบบลง 1 × ยอดตามกระดาษ (ไม่แต่งราคาต่อหน่วย) · "
                    + $"{QuantityInDescriptionPhrase} · ส่วนต่างที่กระดาษไม่บอก — ตรวจ" + Fingerprint(1m, amt));
            return Rounded(qty, amt, up, $"{line} (ยอดมากกว่า {-gap:N2}) และกระดาษไม่พิมพ์อะไรอธิบาย · ส่วนต่างที่กระดาษไม่บอก — ตรวจ");
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

    /// <summary>หมายเหตุแจ้ง (ไม่บล็อก) ของบรรทัดมิเตอร์ที่ผลแกะรุ่นก่อนเก็บเป็นราคาปัด แล้วตัวกันรุ่นนี้ลงใหม่เป็น 1 × ยอด (ทีม OCR 14)</summary>
    private static string RerunMeterNote(string line, decimal amt)
        => $"{NoteTag} {QuantityInDescriptionPhrase} — {line} · ราคาต่อหน่วยคือ ยอด ÷ จำนวน ที่ปัด 2 ตำแหน่ง (ผลแกะรุ่นก่อน) และกระดาษไม่พิมพ์ราคาต่อหน่วย ⇒ "
           + $"ระบบลง 1 × {amt:N2} (บรรทัดมิเตอร์ไม่ถือเศษปัดในช่องส่วนลด · ยอดตรงกระดาษ)";

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
            if (nums[k].IsPercent || nums[k].Value != qty || nums[k].RowIndexLike) continue;
            if (!nums[k].UnitAfter && !MeterLabelBefore(paper.Text, nums[k].Start)) continue;
            for (var j = k + 1; j < nums.Count && j <= k + MaxNumbersBetween + 1; j++)
            {
                if (nums[j].IsPercent) continue;
                if (nums[j].Value == amt) return true;
                if (Math.Abs(nums[j].Value - price) > 0.01m) break;
            }
        }
        return false;
    }

    /// <summary>ป้ายจำนวนหน่วยมิเตอร์ที่พิมพ์<b>ก่อน</b>ตัวเลข ("หน่วยที่ใช้ 4272.00" — บิล PEA จริง · "จำนวนหน่วย" · "ปริมาณน้ำที่ใช้") — ของตัวกันนี้เท่านั้น</summary>
    private static readonly Regex MeterLabelBeforeRe = new(
        @"(?:หน่วยที่ใช้|จำนวนหน่วย(?:ที่ใช้)?|ปริมาณ(?:น้ำ)?(?:ที่)?ใช้(?:ไป)?|units?\s*used|kwh\s*used)\s*[:：]?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static bool MeterLabelBefore(string text, int start)
    {
        var from = Math.Max(0, start - 24);
        return MeterLabelBeforeRe.IsMatch(ThaiTextNormalizer.MapThaiPua(text[from..start]));
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
                && !lines.Any(l => HoldsValue(l, q, p)))
            {
                changed = true;
                continue;
            }
            kept.Add(raw);
        }
        return changed ? string.Join("\n", kept) : processingNotes;
    }

    /// <summary>บรรทัดนี้ยังถือ "ค่าที่ระบบตั้ง" ⟦q×p⟧ — ราคาเทียบ ±0.005 (เก็บ 2 ตำแหน่ง) · ตัวเทียบเดียวของ <see cref="LiveNotes"/> และ <see cref="SystemValueHeld"/></summary>
    private static bool HoldsValue(OcrLiveLine l, decimal q, decimal p)
        => Math.Abs(l.Quantity - q) < 0.0005m && Math.Abs(l.UnitPrice - p) <= 0.005m;

    /// <summary>
    /// **หมายเหตุหนึ่งข้อยังมีบรรทัดถือ "ค่าที่ระบบตั้ง" (<see cref="Fingerprint"/>) ของมันไหม** · ทีม OCR 12 (ด่านอนุมัติโดยคน — <see cref="OcrPostingReadiness.EvaluateHumanApproval"/>)
    /// <para>ต่างจาก <see cref="LiveNotes"/> (ด่านอนุมัติเอง) ตรงที่<b>ไม่ถามว่าทุกบรรทัดลงตัว</b> — ด่านที่บล็อกคนต้องมีทางไปต่อเสมอ (ดู <see cref="HeldLines"/>) ·
    /// หมายเหตุไม่มีร่องรอย ⇒ true (ไม่รู้ = ยังไม่มีคนดู)</para>
    /// </summary>
    public static bool SystemValueHeld(string note, IReadOnlyList<OcrLiveLine> lines, string? description = null)
        => !FingerprintRe.IsMatch(note ?? "") || HeldLines(note!, lines, description).Count > 0;

    /// <summary>
    /// **บรรทัดตอนนี้ที่ยังเป็น "บรรทัดที่หมายเหตุเขียนถึง" และยังถือค่าที่ระบบตั้ง** (ทีม OCR 14 · ฝ่ายค้านรอบ 5 P2-b — ตัวเดียวของ <see cref="SystemValueHeld"/> และเหตุผลของด่าน)
    /// <para>เดิม (ทีม 12): ไม่มีบรรทัดคำอธิบายเดียวกันเหลือ ⇒ ตัดสินจากร่องรอยอย่างเดียว ⇒ ผู้ใช้เปลี่ยนชื่อบรรทัดที่เสียแล้วแก้เป็น 12 × 25 แต่บรรทัด "ค่าขนส่ง" 1 × 300
    /// ที่ไม่เกี่ยวกัน (ค่าเดียวกันโดยบังเอิญ) ทำให้ติดตลอด และข้อความอ้างบรรทัดที่ไม่มีแล้ว · ตอนนี้ <paramref name="description"/> ไม่ว่าง ⇒ นับเฉพาะบรรทัดที่<b>เป็นบรรทัดนั้น</b>:
    /// คำอธิบายเดียวกัน หรือคำอธิบายเดียวกันหลังตัดท้ายของตัวกัน ("น้ำดื่ม 600ml (12 ขวด)" ↔ "น้ำดื่ม 600ml") — บรรทัดนั้นไม่เหลือบน draft แล้ว (เปลี่ยนชื่อเป็นอย่างอื่น = มีคนแก้) ⇒ ปลด ·
    /// <paramref name="amount"/> (เส้นเอกสารลูก — <see cref="OcrPostingReadiness.EvaluateHumanApproval"/> แบบ carried) ⇒ ยอดบรรทัดต้องตรงด้วย</para>
    /// </summary>
    public static IReadOnlyList<OcrLiveLine> HeldLines(string note, IReadOnlyList<OcrLiveLine> lines, string? description = null, decimal? amount = null)
    {
        var fp = FingerprintRe.Match(note ?? "");
        if (!fp.Success
            || !decimal.TryParse(fp.Groups["q"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var q)
            || !decimal.TryParse(fp.Groups["p"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var p))
            return Array.Empty<OcrLiveLine>();
        var want = description?.Trim();
        return lines.Where(l => HoldsValue(l, q, p)
                && (amount is not decimal a || Math.Abs(l.Amount - a) <= 0.01m)
                && (string.IsNullOrEmpty(want) || SameMarkedLine(l.Description, want)))
            .ToList();
    }

    /// <summary>คำอธิบายบรรทัดตอนนี้คือบรรทัดที่หมายเหตุเขียนถึง — ตรงกัน หรือตรงกันหลังตัดท้าย "(จำนวน หน่วย)" ของตัวกัน (<see cref="QuantitySuffixRe"/>) ทั้งสองฝั่ง</summary>
    private static bool SameMarkedLine(string? live, string want)
    {
        if (live is null) return false;
        var a = live.Trim();
        if (string.Equals(a, want, StringComparison.Ordinal)) return true;
        static string Base(string d) => QuantitySuffixRe.Match(d) is { Success: true, Index: > 0 } m ? d[..m.Index].TrimEnd() : d;
        return string.Equals(Base(a), Base(want), StringComparison.Ordinal);
    }

    private static readonly Regex NoteAmountRe = new(@"ยอดบนกระดาษ (?<a>[\d,]+\.\d{2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>ยอดบรรทัดที่หมายเหตุบล็อกเขียนถึง (เศษเกินเพดาน: "ยอดบนกระดาษ X" · หมายเหตุซ่อม: ร่องรอย ⟦1×ยอด⟧) · อ่านไม่ได้ = null — ใช้จับเอกสารลูกที่ก๊อปค่าบรรทัดไป</summary>
    public static decimal? NoteAmount(string? note)
    {
        if (string.IsNullOrEmpty(note)) return null;
        if (NoteAmountRe.Match(note) is { Success: true } m
            && decimal.TryParse(m.Groups["a"].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var a)) return a;
        var fp = FingerprintRe.Match(note);
        return fp.Success && fp.Groups["q"].Value == "1"
               && decimal.TryParse(fp.Groups["p"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    /// <summary>
    /// **บรรทัดนี้มีรูปของค่าที่ระบบตั้งแล้วบล็อกคนไหม** (ทีม OCR 14 · ฝ่ายค้านรอบ 5 P2-a — ตัวกรองก่อนค้นหมายเหตุสแกนของบริษัทสำหรับเอกสารที่<b>ไม่ได้</b>สร้างจากสแกน) —
    /// เศษเกินเพดานในช่องส่วนลด (ราคา = ยอด ÷ จำนวน ปัด 2 ตำแหน่ง · ส่วนลด = เศษเกินเพดาน) หรือ 1 × ยอด + ท้ายของตัวกันที่ไม่ใช่มิเตอร์ · ใบทั่วไปไม่ผ่าน ⇒ ไม่มี query เพิ่ม
    /// </summary>
    public static bool MayCarryHumanBlock(decimal quantity, decimal unitPrice, decimal discountAmount, decimal amount, string? description, string? unit)
    {
        if (amount <= 0m || quantity <= 0m) return false;
        if (quantity != 1m && discountAmount > 0m && !RoundingRemainderWithinCap(discountAmount, amount)
            && RoundedPrices(quantity, amount) is var rp && (unitPrice == rp.Nearest || unitPrice == rp.Up)) return true;
        return LumpSumSuffix(description, quantity, unit) is { Meter: false };
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
