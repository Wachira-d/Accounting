using System;
using System.Collections.Generic;
using System.Linq;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ซ่อมข้อมูลที่ผลแกะรุ่น 6/7/8 ทิ้งไว้ — กติกาตัวเดียว** (pure · ทีม OCR 11 · ฝ่ายค้าน OCR 9 P1-5 · CLAUDE.md F2 ข้อ 9 "Persist แล้วต้องมี migration")
///
/// <para>═══ ที่มา ═══ ทีม OCR 8 (13a4d788 · 2026-10-16 13:09 UTC · รุ่น 6) ลงบรรทัดสินค้าที่ไม่มีราคาต่อหน่วยเป็น 1 × ยอด + "(12 ขวด)" · ทีม OCR 9 (รุ่น 7/8) แก้บรรทัดสินค้าทั่วไป
/// แต่ยังลง 1 × ยอด ให้คอนกรีต "(2.75 ลบ.ม.)" · เคสของ AWN "(จำนวน 3)" · ค่าน้ำดื่ม "(12 ลบ.ม.)" · การเพิ่มรุ่นกันแค่ "อัปไฟล์เดิมซ้ำ" — สแกนที่เก็บไว้แล้ว
/// ยังเป็นจำนวน 1 / หน่วย "รายการ" และตอนสร้างเอกสารตัวกันรันซ้ำได้ Keep (1 × ยอด ลงตัวพอดี) ⇒ เอกสาร/สต็อกจำนวน 1 ต่อไป · ตอนนำเข้าสต็อก (ก่อนทีม 11) alias/ชื่อสินค้าใหม่/คีย์ตัวเรียน
/// ใช้คำอธิบายดิบที่ติดท้าย</para>
///
/// <para>═══ กติกา ═══ บรรทัดที่เสีย = ท้ายของตัวกัน (<see cref="OcrLineQuantityGuard.LumpSumSuffix"/>) บนบรรทัดที่<b>ไม่มีหลักฐานมิเตอร์</b> (บิล PEA "(4,272 หน่วย)" ไม่ถูกแตะ) ·
/// คีย์ที่เสีย (alias · ชื่อสินค้า · คีย์ตัวเรียน) = มีท้ายของตัวกันใด ๆ (มิเตอร์ด้วย — จำนวนหน่วยเปลี่ยนทุกเดือน) และสร้างหลัง <see cref="PollutionStartUtc"/> ·
/// <b>ซ่อมเองเฉพาะส่วนที่ปลอดภัย</b>: ติดแท็กบล็อกให้สแกน (ต้องแกะใหม่/แก้บรรทัดก่อนอนุมัติ) + คืนสิทธิ์ "แกะใหม่" · เปลี่ยนชื่อ/รวม alias และคีย์ตัวเรียน ·
/// <b>ไม่แตะ</b>เอกสารที่ออกแล้ว · การเคลื่อนไหวสต็อก · ชื่อสินค้า (รายงานพร้อมทางไปต่อ — คำตัดสินข้อ 150) · global pattern ข้ามบริษัทไม่อยู่ในรายงานต่อบริษัท</para>
/// </summary>
public static class OcrLumpSumRepair
{
    /// <summary>รหัสกติกาใน audit/หมายเหตุ</summary>
    public const string RuleCode = "OCR-LUMPSUM-REPAIR";

    /// <summary>เวลาที่ 13a4d788 (ทีม OCR 8) เริ่มเขียนท้าย "(จำนวน หน่วย)" — alias/สินค้า/คีย์ตัวเรียนก่อนเวลานี้ไม่ได้มาจากตัวกัน</summary>
    public static readonly DateTime PollutionStartUtc = new(2026, 10, 16, 13, 9, 0, DateTimeKind.Utc);

    /// <summary>รุ่นตัวแกะที่ลง 1 × ยอด บนบรรทัดที่ไม่ใช่มิเตอร์ได้ (6 = ทีม 8 · 7/8 = ทีม 9 · 9 = ทีม 11 แก้แล้ว)</summary>
    public static readonly IReadOnlyList<int> AffectedVersions = new[] { 6, 7, 8 };

    /// <summary>บรรทัดของสแกนที่ตัวกันรุ่น 6–8 ลงเป็น 1 × ยอด โดยไม่มีหลักฐานมิเตอร์ (จำนวนสินค้าหาย)</summary>
    public static bool IsPollutedLine(string? description, decimal? quantity, string? unit)
        => OcrLineQuantityGuard.LumpSumSuffix(description, quantity, unit) is { Meter: false };

    /// <summary>คีย์ (alias · ชื่อสินค้า · คีย์ตัวเรียน) ที่ติดท้ายของตัวกัน ⇒ คีย์ที่ตัดท้ายแล้ว · ไม่ติด/ไม่แน่ใจ = null (ไม่แตะ)</summary>
    public static string? CleanKey(string? text)
        => OcrLineQuantityGuard.LumpSumSuffix(text, 1m, OcrLineQuantityGuard.LumpSumUnit)?.Base;

    /// <summary>หมายเหตุบล็อกที่ติดให้สแกน (หนึ่งข้อต่อบรรทัด) — ร่องรอย ⟦1×ยอด⟧ ⇒ <see cref="OcrLineQuantityGuard.LiveNotes"/> ปลดเมื่อคนแก้บรรทัดนั้นในเอกสารแล้วทุกบรรทัดลงตัว</summary>
    public static string ScanNote(int version, int lineNo, string? description, decimal amount)
        => $"{OcrLineQuantityGuard.Tag} {RuleCode}: ผลแกะรุ่น {version} ลงบรรทัดที่ {lineNo} \"{description}\" เป็น 1 × {amount:N2} ทั้งที่ไม่มีหลักฐานมิเตอร์ "
           + "(จำนวนสินค้าตามกระดาษหายไป) — กด \"แกะใหม่\" ที่หน้าสแกน (ไม่ใช้โควต้า) หรือแก้จำนวน/ราคาของบรรทัดตามกระดาษก่อนอนุมัติ"
           + OcrLineQuantityGuard.Fingerprint(1m, amount);

    /// <summary>สแกนนี้ติดหมายเหตุของการซ่อมแล้ว (ซ่อมซ้ำ = ข้าม)</summary>
    public static bool AlreadyMarked(string? processingNotes)
        => (processingNotes ?? "").Contains(OcrLineQuantityGuard.Tag + " " + RuleCode, StringComparison.Ordinal);

    /// <summary>ทางไปต่อของเอกสารที่สร้างจากสแกนที่เสีย — ระบบไม่แก้เอกสารเอง (คำตัดสินข้อ 150: บล็อกเฉพาะเอกสาร/GL ที่ออกแล้ว พร้อมทางไปต่อภาษาไทย)</summary>
    public static string DocumentWayForward(DocumentStatus status) => status switch
    {
        DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected =>
            "ยังไม่ออก — แก้จำนวน/ราคาบรรทัดใน draft ตามกระดาษ หรือลบ draft แล้วกด \"แกะใหม่\" ที่สแกน (ด่านอนุมัติจะบล็อกจนกว่าจะแก้)",
        DocumentStatus.Voided => "ยกเลิกแล้ว — ไม่ต้องทำอะไร",
        _ => "ออกแล้ว — ระบบไม่แก้ย้อนหลัง: ถ้ายอดเงินถูกแต่จำนวนผิด ให้ปรับสต็อก/ต้นทุนด้วยมือที่หน้าสินค้า · ถ้าต้องแก้เอกสารต่อคู่ค้า ออกใบลดหนี้/เพิ่มหนี้ตามจริง",
    };

    /// <summary>ทางไปต่อของการเคลื่อนไหวสต็อกที่มาจากสแกนที่เสีย (จำนวนเข้า 1 แทนจำนวนจริง · ทุนต่อหน่วย = ยอดทั้งบรรทัด)</summary>
    public const string StockWayForward =
        "จำนวนเข้าอาจเป็น 1 แทนจำนวนจริงบนกระดาษ และทุนต่อหน่วยเท่ายอดทั้งบรรทัด — ตรวจกับกระดาษแล้วปรับสต็อกด้วยมือ (รับเข้าส่วนที่ขาดที่ทุนจริง) ระบบไม่แก้ประวัติสต็อกเอง";

    /// <summary>ทางไปต่อของสินค้าที่ชื่อติดท้ายจำนวน (สร้างจากหน้านำเข้าสต็อกก่อนทีม 11)</summary>
    public const string ProductWayForward = "ชื่อสินค้าติดจำนวนจากบรรทัดสแกน — แก้ชื่อที่หน้าสินค้า (ระบบไม่เปลี่ยนทะเบียนสินค้าเอง)";
}
