using System;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **"ข้อความนี้คือค่าน้ำประปา/น้ำคิดตามมิเตอร์ (ลบ.ม.) ไหม" — ตัวตัดสินตัวเดียวของทั้งเรพ** (pure · ทีม OCR 14 · ฝ่ายค้านรอบ 5 P2-c)
///
/// <para>═══ ที่มา ═══ ทีม OCR 12 ทำให้ <c>UnitInferrer.IsWaterSupply</c> เป็น whitelist ("ค่าน้ำ" ที่<b>จบคำ</b>) เพื่อตัดค่าน้ำขวด/ถัง/กระป๋อง/หนักเกิน —
/// แต่รายการคำต่อท้ายแคบเกินจนน้ำมิเตอร์จริงหลุด: "ค่าน้ำตามมิเตอร์" · "ค่าน้ำ ห้อง 101" · "ค่าน้ำส่วนกลาง" · "ค่าน้ำบาดาล" · "ค่าน้ำดิบ" · "ค่าน้ำเสีย" ⇒
/// 37 "ชิ้น" + เศษปัด (เดิมเป็นบรรทัดมิเตอร์ "(37 ลบ.ม.)") · "Water charge" ของการประปาไม่เคยเป็นน้ำ · และ<b>ตัวตัดสินน้ำตัวที่สอง</b> กฎหมวด
/// <c>ExpenseCategoryResolver</c> (ค่าน้ำประปา 54320) ยังใช้ substring "ค่าน้ำ" ⇒ "ค่าน้ำหนักเกิน"/"ค่าน้ำขวด"/"ค่าน้ำถัง" ลงค่าน้ำประปา (F2 ข้อ 4 — กติกาเดียวมีสองสำเนา)</para>
///
/// <para>═══ กติกา ═══ whitelist (ภาษาไทยไม่มีช่องว่างคั่นคำ ⇒ "ค่าน้ำ ที่ไม่ตามด้วยคำห้าม" ไม่มีวันครบ — บทเรียนทีม OCR 12):
/// "ค่าน้ำ" ที่ตามด้วย <see cref="ChargeContinuation"/> (จบบรรทัด · ตัวเลข/เครื่องหมาย · คำบอกงวด ประจำ/เดือน/งวด/รอบ · มาตร/มิเตอร์/ตาม… ·
/// ห้อง/ส่วนกลาง (หอพัก/อาคารชุด) · บาดาล/ดิบ/เสีย (ชนิดน้ำที่คิดตาม ลบ.ม.) · ค่า…/และ (บิลรวม "ค่าน้ำค่าไฟ") · ประปา) ·
/// คำที่ยืนยันในตัว (น้ำประปา · ค่าประปา · การประปา · ค่าใช้น้ำ · ปริมาณ/จำนวนน้ำที่ใช้ · มาตรวัดน้ำ/มิเตอร์น้ำ · ค่าบำบัดน้ำเสีย · waterworks · water supply/bill/usage/
/// consumption/meter · tap water) · ผู้ขายเป็นการประปา (<see cref="OcrUtilityKind.Water"/>) ⇒ คำอังกฤษ "water …" นับด้วย ("Water charge") ·
/// <b>ไม่รู้ = ไม่ใช่</b> (หน่วยตกไปค่าตั้งต้น — ปลอดภัยกว่า ลบ.ม. ที่แต่ง)</para>
///
/// <para>ไม่ใช่น้ำ (ล็อกในเทสต์): ค่าน้ำขวด · ค่าน้ำดื่ม · ค่าน้ำแข็ง · ค่าน้ำมัน(ดีเซล/เครื่อง) · ค่าน้ำหนักเกิน · ค่าน้ำถัง · ค่าน้ำกระป๋อง · ค่าน้ำโค้ก · ค่าน้ำเกลือ ·
/// น้ำดื่มถัง · ท่อประปา PVC · ค่ารักษามาตร (ค่าธรรมเนียมคงที่ ไม่ใช่ปริมาณน้ำ)</para>
///
/// <para>ผู้เรียก: <c>UnitInferrer.IsWaterSupply</c> (หน่วยอนุมาน ลบ.ม. · บริบทน้ำของ <see cref="OcrLineQuantityGuard"/>) · กฎหมวดค่าน้ำประปาของ
/// <c>ExpenseCategoryResolver</c> (ผ่าน <see cref="NotMeteredWaterPhrase"/> — วลี "ค่าน้ำ" ที่ไม่ใช่น้ำถูกตัดออกก่อนนับคะแนน)</para>
/// </summary>
public static class OcrMeteredWater
{
    /// <summary>หน่วยของน้ำคิดตามมิเตอร์ (ตามที่การประปาพิมพ์บนบิล)</summary>
    public const string Unit = "ลบ.ม.";

    /// <summary>คำต่อท้าย "ค่าน้ำ" ที่ทำให้เป็นค่าน้ำมิเตอร์ (เนื้อใน lookahead — ใช้ทั้งทางบวก <see cref="IsMeteredWater(string?)"/> และทางลบ
    /// <see cref="NotMeteredWaterPhrase"/> ⇒ สองฝั่งเป็นสูตรเดียว) · <c>(?m:$)</c> = จบบรรทัดไม่ขึ้นกับ option ของผู้เรียก</summary>
    private const string ChargeContinuation =
        @"[ \t]*\r?(?m:$)|[ \t]*[\d๐-๙(\[/,.:;+&\-–]"
        + @"|[ \t]*(?:ประจำ|เดือน|งวด|รอบ|มาตร|มิเตอร์|ตาม|ห้อง|ส่วนกลาง|บาดาล|ดิบ|เสีย|ค่า|และ|ประปา|ปะปา)";

    private static readonly Regex MeteredWaterRe = new(
        @"ค่าน้ำ(?=" + ChargeContinuation + ")"
        + @"|น้ำประปา|ค่าประปา|การประปา|ค่าใช้น้ำ|(?:ปริมาณ|จำนวน)น้ำ(?:ที่)?ใช้|มาตร(?:วัด)?น้ำ|มิเตอร์น้ำ|บำบัดน้ำเสีย"
        + @"|waterworks|water\s*(?:supply|bill|usage|consumption|meter)|tap\s*water",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>คำอังกฤษ "water" เป็นคำเต็ม — นับเฉพาะเมื่อผู้ขายเป็นการประปา ("Water charge" ของ กปภ.) · น้ำดื่ม/ขวดไม่นับแม้ผู้ขายประปา</summary>
    private static readonly Regex WaterWordRe = new(@"(?<![A-Za-z])water(?![A-Za-z])", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex DrinkingWaterRe = new(
        @"drinking|mineral|bottle|bottled|ดื่ม|ขวด|ถัง|แกลลอน", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// **วลี "ค่าน้ำ" ที่ไม่ใช่ค่าน้ำมิเตอร์** (regex · ใช้กับข้อความตัวพิมพ์เล็ก) — ให้กฎหมวดค่าน้ำประปาของ <c>ExpenseCategoryResolver</c> ตัดออกก่อนนับคะแนน
    /// (<c>Exclusions</c> ของกฎนั้น) ⇒ "ค่าน้ำหนักเกิน"/"ค่าน้ำขวด"/"ค่าน้ำถัง"/"ค่าน้ำมัน" ไม่ได้คะแนนค่าน้ำประปา · เป็นทางลบของคำต่อท้ายชุดเดียวกับ
    /// <see cref="IsMeteredWater(string?)"/> (ไม่ใช่รายการคำห้าม)
    /// </summary>
    public const string NotMeteredWaterPhrase = @"ค่าน้ำ(?!" + ChargeContinuation + ")";

    /// <summary>หน่วยลูกบาศก์เมตรในข้อความเดียวกัน ("Water Charge (25 m3)") — คำ "water" + ลบ.ม. = น้ำคิดตามปริมาตร</summary>
    private static readonly Regex CubicRe = new(@"(?<![A-Za-z])m3(?![A-Za-z\d])|m³|ลบ\.?\s*ม|ลูกบาศก์เมตร|cubic", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>ข้อความนี้พูดถึงค่าน้ำประปา/น้ำคิดตามมิเตอร์ไหม (บริบทข้อความอย่างเดียว) — ดูกติกาที่หัวคลาส</summary>
    public static bool IsMeteredWater(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && (MeteredWaterRe.IsMatch(text) || (WaterWordRe.IsMatch(text) && CubicRe.IsMatch(text) && !DrinkingWaterRe.IsMatch(text)));

    /// <summary>เหมือน <see cref="IsMeteredWater(string?)"/> + ผู้ขายการประปา ⇒ คำอังกฤษ "water" (ที่ไม่ใช่น้ำดื่ม/ขวด/ถัง) นับด้วย ("Water charge" ของ กปภ.)</summary>
    public static bool IsMeteredWater(string? text, OcrUtilityKind seller)
    {
        if (IsMeteredWater(text)) return true;
        return seller == OcrUtilityKind.Water && !string.IsNullOrWhiteSpace(text)
               && WaterWordRe.IsMatch(text) && !DrinkingWaterRe.IsMatch(text);
    }
}
