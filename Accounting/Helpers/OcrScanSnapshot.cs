using System.Reflection;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **คัดลอก "ผลการอ่านกระดาษ" จากสแกนหนึ่งไปอีกสแกนหนึ่ง — ที่เดียวของระบบ**
///
/// ═══ ที่มา (บั๊กจริง · ผู้ใช้รายงานเอง) ═══
/// <para>อัปโหลดไฟล์เดิมซ้ำ → <c>OcrService.ScanAsync</c> เจอ hash ตรง → ลัดวงจร
/// คัดลอกค่าจากสแกนเดิมแล้ว return · แต่โค้ดเดิมเป็น <b>รายการช่องที่เขียนด้วยมือ
/// 14 ช่อง</b> จากทั้งหมด 63 ช่อง ⇒ ที่เหลือ <b>หายเงียบ</b>:</para>
/// <list type="bullet">
///   <item><c>RawTextContent</c> — หน้าจอขึ้น "ไม่มี Raw Text" แล้ว<b>เดาสาเหตุผิด</b>
///     ว่า Docker ไม่ทำงาน · และมันยังป้อน 8 จุดที่ตัดสินตัวเลขจริง
///     (สกุลเงิน · เหตุผลใบลดหนี้ §86/10 · ประเภทเงินได้ 50 ทวิ · เงินมัดจำ ·
///     คำเตือน RD compliance) ⇒ กระดาษใบเดียวกันได้เอกสารคนละหน้าตา</item>
///   <item><c>ExtractedItemsJson</c> — <b>ไม่มีรายการสินค้าสักบรรทัด</b> ผิดกฎเหล็ก #3</item>
///   <item><c>FieldConfidenceJson</c> — ไฮไลต์เหลือง "ตรวจอีกครั้ง" ไม่ขึ้นเลย
///     ทั้งที่ยังโชว์ค่าความมั่นใจ 95%</item>
///   <item>ช่อง §86/4 ฝั่งผู้ซื้อ/ที่อยู่/รหัสสาขา · <c>TargetDocumentType</c>
///     (สายสร้างเอกสารอ่านช่องนี้ ไม่ใช่ <c>DocumentType</c>) · หมวดค่าใช้จ่าย ·
///     WHT · เครดิตเทอม</item>
/// </list>
///
/// ═══ กติกาที่แก้รากของปัญหา ═══
/// <para><b>เปลี่ยนจาก allow-list เป็น deny-list</b> — "รายการช่องที่คัดลอก" ที่เขียน
/// ด้วยมือ แปลว่าทุกครั้งที่เพิ่มช่องใหม่ต้องมีคนจำได้ว่าต้องมาเติมที่นี่ด้วย
/// (ช่อง §86/4 กับ <c>TargetDocumentType</c> ถูกเพิ่มทีหลัง — และหลุดทั้งคู่)
/// ที่นี่จึงกลับด้าน: <b>คัดลอกทุกช่องที่ประกาศบน <see cref="OcrScanResult"/>
/// ยกเว้นที่อยู่ใน <see cref="RowIdentityFields"/></b> ⇒ ช่องใหม่ในอนาคต
/// ตามมาเองโดยอัตโนมัติ ทิศของความผิดพลาดกลายเป็น "คัดลอกเกิน" ซึ่งเห็นได้ทันที
/// แทน "คัดลอกขาด" ซึ่งเงียบสนิท</para>
///
/// <para>ช่องที่สืบทอดจาก <c>BaseEntity</c>/<c>TenantEntity</c> (Id, CompanyId,
/// CreatedAt/By, IsDeleted, navigation <c>Company</c>) ถูกตัดออกโดยโครงสร้าง —
/// ตัวกรองดูเฉพาะ property ที่ <b>ประกาศบนคลาสนี้เอง</b></para>
/// </summary>
public static class OcrScanSnapshot
{
    /// <summary>
    /// ช่องที่เป็น **ตัวตน/ที่มาของแถวนั้น ๆ** — ห้ามรับค่าจากสแกนอื่นเด็ดขาด
    ///
    /// <para>เหตุผลรายตัว:</para>
    /// <list type="bullet">
    ///   <item><c>FileAttachmentId · OriginalFileName · FileHash</c> — ไฟล์ของแถวนี้เอง</item>
    ///   <item><c>ScanStatus · ProcessingNotes · ProcessedAt · OcrEngine</c> —
    ///     ผู้เรียกตั้งเองหลังคัดลอก (ต้องบอกความจริงว่า "ไม่มี engine ตัวไหนทำงาน")</item>
    ///   <item><c>IsDuplicate · DuplicateOfScanId · RetryCount</c> — สถานะของแถวนี้</item>
    ///   <item><c>ExternalMetadataJson</c> — metadata มากับ<b>การอัปโหลดครั้งนี้</b>
    ///     ไม่ใช่ของกระดาษ · ทับด้วยของเก่า = ยอด/โปรเจกต์ที่คู่ค้าส่งมาหาย</item>
    ///   <item><c>UserNotes</c> — ผู้ใช้พิมพ์บนแถวนี้</item>
    ///   <item><c>CreatedDocumentId · CreatedJournalEntryId</c> — <b>สำคัญ</b>: คัดลอกมา
    ///     = แถวใหม่อ้างว่าสร้างเอกสารของแถวเก่าไปแล้ว ⇒ ปุ่ม "สร้างเอกสาร" หายไป
    ///     และด่านกันสร้างซ้ำจะเข้าใจผิด</item>
    ///   <item><c>StockImportedAt</c> — เครื่องหมายกันนำสต็อกเข้าซ้ำของ<b>แถวเก่า</b>
    ///     คัดลอกมา = แถวใหม่นำสต็อกเข้าไม่ได้ตลอดกาล</item>
    /// </list>
    ///
    /// <para>⚠️ <b>ที่จงใจ "ไม่" อยู่ในลิสต์นี้ — FK ของ AI feedback</b>
    /// (<c>AiSuggestionFeedbackId · GlAccountAiFeedbackId · TargetDocTypeAiFeedbackId ·
    /// LineSplitAiFeedbackId</c>) และธง <c>*UsedAi</c>: ค่าที่โชว์อยู่<b>มาจาก</b>
    /// คำตอบ AI แถวนั้นจริง ๆ ⇒ เมื่อผู้ใช้แก้บนสำเนา <c>RecordUserChoiceAsync</c>
    /// ต้องเขียนกลับไปที่แถวที่ให้คำตอบนั้น มิฉะนั้นวงจร distillation ขาด
    /// (กฎเหล็ก #1) และป้าย "🤖 AI แนะนำ" จะโกหกว่าเป็นคำแนะนำของระบบ</para>
    /// </summary>
    /// <summary>แท็กใน <c>ProcessingNotes</c> ที่เป็น **คำตัดสินเกี่ยวกับตัวกระดาษ**
    /// (ไม่ใช่ diagnostic ของการอัปโหลดครั้งนั้น) ⇒ ต้องติดไปกับสำเนาเสมอ
    ///
    /// <para>⚠️ ที่มา (ผลตรวจ OCR 2026-09-06 · T5-N1): <c>ProcessingNotes</c> อยู่ใน
    /// deny-list (ถูกต้อง — มันมีร่องรอย tier/engine ของการอัปโหลดครั้งนั้น) แต่เส้น
    /// "ไฟล์ซ้ำ" เขียนทับทั้งก้อนด้วย <c>"Duplicate of scan …"</c> ⇒ ธง
    /// <c>[VAT-CLAIM]</c> (§82/5 เคลมภาษีซื้อไม่ได้) หายไปด้วย และระบบ<b>ไม่มีคอลัมน์
    /// อื่นเก็บคำตัดสินนี้เลย</b> — คำตัดสินทางกฎหมายอยู่ในสตริงล้วน ⇒
    /// <b>อัปไฟล์เดิมซ้ำ = ใบกำกับอย่างย่อ/ค่ารับรอง กลับมาเคลมภาษีซื้อได้</b></para>
    /// <para>รอบ 192 ฝ่ายค้าน C3: ลิสต์นี้เคยเขียนมือแยกจาก <see cref="OcrPostingReadiness.BlockingTags"/> ⇒ แท็กห้ามอนุมัติ
    /// ใหม่ (<c>[TOTAL-CONFLICT]</c> · <c>[PAY≠TOTAL]</c> — และของเดิม <c>[MATH]</c> · <c>[DATE-UNSURE]</c> · <c>[FX-UNKNOWN]</c>)
    /// หลุดตอนอัปไฟล์ซ้ำ ⇒ ใบ Shopee ที่อัปซ้ำกลายเป็นอนุมัติเองได้ · ตอนนี้ <b>ทุกแท็กห้ามอนุมัติ</b> ติดไปด้วยเสมอ (ตัวตั้งตัวเดียว)
    /// + ข้อสังเกตเรื่องตัวกระดาษที่ไม่บล็อก (ยอดรวมไม่แน่ใจ · หน้าไม่ครบ) · <c>[TOTAL]</c> ไม่อยู่ในลิสต์ — ยอดที่ยึดแล้วถูกคัดลอกเป็นค่าอยู่แล้ว
    /// และคำเดียวกันอยู่ในบรรทัดเหตุผล (<c>[Reasoning]</c>) ซึ่งเป็นของการอัปโหลดครั้งนั้น</para></summary>
    public static readonly string[] DecisionNoteTags =
        new[] { "[VAT-CLAIM]", "[VAT-NOTE]", "[TAX-INV-PENDING]", "[DATE-UNKNOWN]", "[WHT-CERT]", "[Σ-GAP]" }
            .Concat(OcrPostingReadiness.BlockingTags.Select(t => t.Tag))
            .Concat(new[] { OcrTotalAnchor.UnsureTag, OcrPageSet.PartialTag })
            // รอบ 193: ข้อเสนอบรรทัดปรับเป็นคำตัดสินเรื่องตัวกระดาษ (ติดไปกับสำเนา) · [PAY-SETTLED] ห้ามติดไป —
            // มันบอกว่า "เอกสารของสแกนต้นฉบับ" ลงบรรทัดปรับแล้ว ถ้าสำเนาได้ไปด้วย [PAY≠TOTAL] ของสำเนาจะเลิกหยุดทั้งที่ยังไม่ได้บันทึก
            .Concat(new[] { OcrSettlementProposal.PlanTag })
            // รอบ 195 ฝ่ายค้านรอบสอง R2-4: ร่องรอย "ระบบถอด VAT จากยอดรวม" ติดไปกับสำเนา — VAT ที่คัดลอกมาคือค่าที่ถอดเองตัวเดิม
            // (OcrHeaderVatEvidence.Classify อ่านร่องรอยนี้ ⇒ สำเนาไม่หลุดเป็น "พิมพ์บนกระดาษ" เพราะเลขบังเอิญตรงเลขอื่นบนใบ)
            .Concat(new[] { VatBackCalcGuard.BackCalcTag })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>คัดเฉพาะบรรทัดที่เป็นคำตัดสินจากหมายเหตุของสแกนต้นฉบับ</summary>
    public static string DecisionNotes(string? processingNotes)
    {
        if (string.IsNullOrWhiteSpace(processingNotes)) return string.Empty;
        var kept = processingNotes
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => DecisionNoteTags.Any(t => l.Contains(t, StringComparison.Ordinal)))
            .ToList();
        return kept.Count == 0 ? string.Empty : string.Join("\n", kept);
    }

    public static readonly IReadOnlySet<string> RowIdentityFields =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(OcrScanResult.FileAttachmentId),
            nameof(OcrScanResult.OriginalFileName),
            nameof(OcrScanResult.FileHash),
            nameof(OcrScanResult.ScanStatus),
            nameof(OcrScanResult.ProcessingNotes),
            nameof(OcrScanResult.ProcessedAt),
            nameof(OcrScanResult.OcrEngine),
            nameof(OcrScanResult.IsDuplicate),
            nameof(OcrScanResult.DuplicateOfScanId),
            nameof(OcrScanResult.RetryCount),
            nameof(OcrScanResult.ExternalMetadataJson),
            nameof(OcrScanResult.UserNotes),
            nameof(OcrScanResult.CreatedDocumentId),
            nameof(OcrScanResult.CreatedJournalEntryId),
            nameof(OcrScanResult.StockImportedAt),
            // ★ ร่องรอย "คนแก้" เป็นของ **การอัปโหลดครั้งนั้น** ไม่ใช่ของกระดาษ —
            // คัดลอกมาแล้วสำเนาใหม่จะถูกนับว่า "ผู้ใช้แก้แล้ว" ทั้งที่ยังไม่มีใครแตะ
            // ⇒ อัตราการแก้พองเกินจริง และ first-pass accept rate ต่ำเกินจริง
            // (ตัวชี้วัดคู่ใน Helpers/OcrQualityKpi จะอ่านไม่ได้ทันที)
            nameof(OcrScanResult.UserCorrectedAt),
            nameof(OcrScanResult.UserCorrectedFields),
        };

    // อ่าน metadata ครั้งเดียวตอนโหลดคลาส — reflection ต่อการเรียกจะช้าเกินไป
    // สำหรับเส้นทางที่รันทุกการอัปโหลด
    private static readonly PropertyInfo[] Copyable =
        typeof(OcrScanResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanRead && p.CanWrite && !RowIdentityFields.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>ชื่อช่องที่ถูกคัดลอกจริง — ให้เทสต์ยืนยันได้ว่าไม่มีช่องไหนหลุด</summary>
    public static IReadOnlyList<string> CopiedFieldNames => Copyable.Select(p => p.Name).ToArray();

    /// <summary>ชื่อ property ทุกตัวที่ประกาศบน <see cref="OcrScanResult"/> เอง</summary>
    public static IReadOnlyList<string> DeclaredFieldNames =>
        typeof(OcrScanResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanRead && p.CanWrite)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// คัดลอก **ผลการอ่านกระดาษทั้งหมด** จาก <paramref name="source"/> ไปยัง
    /// <paramref name="target"/> · ไม่แตะช่องที่เป็นตัวตนของแถวปลายทาง
    /// </summary>
    public static void CopyExtractionFrom(OcrScanResult source, OcrScanResult target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(source, target)) return;

        foreach (var p in Copyable)
            p.SetValue(target, p.GetValue(source));
    }
}
