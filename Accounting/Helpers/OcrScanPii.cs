using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Accounting.Helpers;

/// <summary>
/// "ช่องไหนของผลสแกนเก็บข้อมูลส่วนบุคคล" — ตัวตัดสิน<b>ตัวเดียว</b>ของเส้น PDPA
///
/// ═══ ทำไมต้องมี (ผลตรวจ OCR 2026-09-06 · T5) ═══
/// <c>PdpaService.ApplyErasureAsync</c> (ม.33 สิทธิขอให้ลบ) anonymise
/// <c>Users</c> + <c>Contacts</c> ครบ — แต่ <b>ไม่เคยแตะ <c>OcrScanResult</c></b>
/// ทั้งที่แถวนั้นเก็บ <c>RawTextContent</c> = <b>ข้อความทั้งหน้ากระดาษ</b>
/// (ชื่อ · ที่อยู่ · เลขประจำตัวผู้เสียภาษี ของเจ้าของข้อมูล) พร้อมช่องที่แยกไว้
/// แล้วอีกสิบกว่าช่อง ⇒ ผู้ที่ใช้สิทธิขอลบยัง<b>ค้นเจอตัวเองได้เต็ม ๆ</b>
/// ในตารางสแกน · และ <c>GenerateAccessReportAsync</c> (ม.30) ก็ไม่รายงานว่ามี
/// ข้อมูลชุดนี้อยู่ ⇒ คำตอบที่ส่งให้เจ้าของข้อมูลไม่ครบตามที่กฎหมายบังคับ
///
/// ═══ ทำไมเป็น "รายการที่ต้องตัดสินทุกช่อง" ไม่ใช่ deny-list ═══
/// ช่องข้อความบน <c>OcrScanResult</c> มี 30+ ช่องและ<b>เพิ่มขึ้นเรื่อย ๆ</b> —
/// ถ้าเขียนเป็น "ลบช่องเหล่านี้" เฉย ๆ ช่องใหม่ที่ใครเพิ่มทีหลังจะไม่ถูกลบ
/// โดยไม่มีอะไรฟ้อง (defect class เดียวกับ <see cref="OcrScanSnapshot"/>)
/// จึงบังคับให้ทุกช่องข้อความต้องอยู่ในลิสต์ใดลิสต์หนึ่ง แล้วมีเทสต์ reflection
/// ฟ้องเมื่อมีช่องใหม่ที่ยังไม่ถูกตัดสิน
/// </summary>
public static class OcrScanPii
{
    /// <summary>ช่องที่<b>อาจมีข้อมูลส่วนบุคคล</b> — ต้องล้างตอนใช้สิทธิขอลบ</summary>
    public static readonly string[] PersonalDataFields =
    {
        "RawTextContent",          // ข้อความทั้งหน้า — ตัวหนักที่สุด
        "OriginalFileName",        // ผู้ใช้ตั้งชื่อไฟล์เป็นชื่อคนได้
        "ExtractedVendorName", "ExtractedVendorTaxId", "VendorAddress",
        "BuyerName", "BuyerTaxId", "BuyerAddress",
        "ExtractedItemsJson",      // คำอธิบายรายการอาจมีชื่อบุคคล
        "ProcessingNotes",         // มี [Buyer] ... TaxID:... และ trace ที่ยกข้อความมา
        "UserNotes",               // ผู้ใช้พิมพ์เอง
        "ExternalMetadataJson",    // partner ส่งอะไรมาก็ได้
        "FieldDecisionsJson",      // เก็บค่าของแต่ละช่องพร้อมที่มา
        "SuggestedAccountsJson",   // มีชื่อผู้ขายในเหตุผล
        "PotentialAssetLinesJson", // คำอธิบายรายการ
        "PoLineMappingsJson",      // คำอธิบายรายการ
    };

    /// <summary>ช่องข้อความที่<b>ไม่ใช่</b>ข้อมูลส่วนบุคคล — ต้องระบุไว้ชัดเจน
    /// เพื่อให้เทสต์ reflection ฟ้องเฉพาะช่องใหม่ที่ยังไม่มีใครตัดสิน
    /// (เลขที่เอกสาร/รหัสบัญชี/สถานะ = ข้อมูลทางบัญชี ไม่ใช่ตัวระบุตัวบุคคล
    /// และหลายตัวต้องคงไว้ตาม พ.ร.บ.การบัญชี ม.10)</summary>
    public static readonly string[] NotPersonalDataFields =
    {
        "ScanStatus", "DocumentType", "ScannedDocumentType", "TargetDocumentType",
        "OurRole", "SuggestedEntryMode", "OcrEngine", "Currency", "ExpenseCategory",
        "WhtIncomeTypeCode", "GlAccountAiSuggestedCode", "TargetDocTypeAiSuggested",
        "ExtractedDocumentNumber", "LinkedPurchaseOrderNumber", "OpenPoNumbersJson",
        "VendorBranchCode", "BuyerBranchCode",
        "ContentFingerprint", "FileHash",           // แฮช — ไม่ย้อนกลับเป็นข้อความ
        "FieldConfidenceJson", "UserCorrectedFields",  // ชื่อช่อง + ตัวเลข ไม่ใช่ค่า
        // ── ช่องของ BaseEntity ──
        // ชื่อผู้ทำรายการ = ข้อมูลของ**พนักงานผู้บันทึก** ไม่ใช่ของเจ้าของข้อมูล
        // ที่ใช้สิทธิ · และเป็นร่องรอยตรวจสอบที่ต้องเก็บตาม พ.ร.บ.การบัญชี ม.10
        // (ถ้าพนักงานคนนั้นใช้สิทธิเอง จะถูกล้างผ่านเส้น Users)
        "CreatedBy", "UpdatedBy",
    };

    /// <summary>ล้างข้อมูลส่วนบุคคลออกจากแถวสแกน (คงโครงบัญชีไว้)
    /// — คืนจำนวนช่องที่ถูกล้างจริง</summary>
    public static int Anonymize(object scanRow)
    {
        if (scanRow == null) return 0;
        var t = scanRow.GetType();
        var cleared = 0;
        foreach (var name in PersonalDataFields)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || p.PropertyType != typeof(string) || !p.CanWrite) continue;
            var current = (string?)p.GetValue(scanRow);
            if (string.IsNullOrEmpty(current)) continue;
            // ช่องที่ประกาศ non-nullable (`= ""`) ต้องเป็นสตริงว่าง ไม่ใช่ null
            p.SetValue(scanRow, name == "OriginalFileName" ? "[ANONYMIZED]" : null);
            cleared++;
        }
        return cleared;
    }

    /// <summary>ช่องข้อความบนชนิดนั้นที่ยัง<b>ไม่ถูกตัดสิน</b>ว่าเป็นข้อมูลส่วนบุคคล
    /// หรือไม่ — ใช้โดยเทสต์เพื่อกัน "ช่องใหม่ที่ไม่มีใครลบ"</summary>
    public static IReadOnlyList<string> UndecidedTextFields(Type scanType)
    {
        var known = PersonalDataFields.Concat(NotPersonalDataFields)
            .ToHashSet(StringComparer.Ordinal);
        return scanType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite && !known.Contains(p.Name))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }
}
