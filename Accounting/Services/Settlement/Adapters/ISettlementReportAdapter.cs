using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Settlement.Adapters;

// ═══════════════════════════════════════════════════════════════════════
// Settlement report adapter — รอบ 198 เฟส 1 ทีม B (report-S2 §3 "Adapter")
//
// ขอบเขต: ความรู้เฉพาะแพลตฟอร์ม (ชื่อคอลัมน์ · ป้ายประเภทรายการ · รูปแบบไฟล์) อยู่ใต้โฟลเดอร์นี้เท่านั้น
// (tools/settlement_adapter_boundary_check.py) · ชั้นบริการ/helper ถามผ่าน interface/ผลลัพธ์ข้างล่าง
//
// สัญญา: adapter "อ่าน" ไฟล์เป็นแถวที่มีเครื่องหมายมุม wallet (บวก = ค้างเราเพิ่ม · ลบ = ถูกหัก) —
// **ไม่จัดประเภทด้วย AI · ไม่จับคู่ · ไม่แตะฐานข้อมูล** · อ่านไม่ได้ = ล้มดังทั้งไฟล์ (SettlementFormatException)
// ห้ามคืนครึ่งไฟล์ (report-S2 §5 ความเสี่ยง "ลงครึ่งทาง")
// ═══════════════════════════════════════════════════════════════════════

/// <summary>1 แถวที่ adapter อ่านได้ — ยังไม่ตัด PII (ตัวนำเข้าตัดก่อนเก็บ) · ยังไม่ผ่านการจัดประเภทชั้นอื่น</summary>
/// <param name="SourceRow">เลขแถวในไฟล์ (1-based รวมหัวตาราง) — ใช้แจ้งปัญหาให้ผู้ใช้หาเจอ</param>
/// <param name="RawTypeLabel">ป้ายประเภทดิบ (ค่าในคอลัมน์ประเภท หรือชื่อคอลัมน์ยอดเงินในไฟล์แบบกว้าง)</param>
/// <param name="Amount">ยอดมีเครื่องหมายมุม wallet (รวม VAT ถ้ามี)</param>
/// <param name="VatAmount">VAT ที่ไฟล์ระบุ (รวมอยู่ใน Amount · เครื่องหมายเดียวกับ Amount) — null = ไม่ระบุ</param>
/// <param name="ExplicitType">ประเภทที่ adapter รู้แน่ (กติกา adapter / ผู้ใช้กำหนดต่อคอลัมน์) — null = ให้ตัวจัดประเภทตัดสิน</param>
/// <param name="RawTxnId">id รายการดิบจากไฟล์ (ยังไม่ทำให้ไม่ซ้ำ) — null = ไฟล์ไม่มี</param>
/// <param name="PayoutRef">เลขรอบโอนของแถว (ถ้าไฟล์มีคอลัมน์นี้)</param>
/// <param name="PaymentIntentId">แถวที่ประกอบจาก PaymentIntent ในระบบ — อยู่ในผังพักแล้ว (SettlementBatchMath.Plan ไม่ลงซ้ำ)</param>
public sealed record SettlementParsedRow(
    int SourceRow,
    string? RawTypeLabel,
    string? Description,
    DateTime? TxnDate,
    string? ExternalOrderId,
    string? RawTxnId,
    decimal Amount,
    decimal? VatAmount,
    decimal? WhtAmount,
    SettlementLineType? ExplicitType,
    string? PayoutRef = null,
    Guid? PaymentIntentId = null);

/// <summary>ผลการอ่านไฟล์ทั้งไฟล์</summary>
/// <param name="Headers">หัวคอลัมน์ที่พบ (ตามลำดับในไฟล์)</param>
/// <param name="SkippedRows">แถวที่ข้ามโดยตั้งใจ (แถวว่าง · แถวสรุปยอด "รวม/Total") พร้อมเหตุผล — ไม่ใช่แถวที่อ่านพลาด</param>
public sealed record SettlementParseResult(
    string AdapterCode,
    IReadOnlyList<string> Headers,
    IReadOnlyList<SettlementParsedRow> Rows,
    IReadOnlyList<string> SkippedRows);

/// <summary>ไฟล์ที่ผู้ใช้อัปโหลด (ถือไบต์ไว้แล้ว — ตัวนำเข้าต้องเก็บไฟล์ต้นฉบับผ่าน attachment abstraction อยู่ดี)</summary>
public sealed record SettlementFileInput(string FileName, byte[] Content)
{
    public bool IsExcel => FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);
    public bool IsCsv => FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                         || FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
}

/// <summary>อ่านไฟล์ไม่ได้/รูปแบบไม่ตรงกับที่จับคู่ไว้ — ข้อความไทยพร้อม "ทางไปต่อ" (จับคู่คอลัมน์ใหม่) ·
/// ตัวนำเข้าแปลงเป็น <c>BusinessRuleException</c> (400) · <b>ไม่มีแถวใดถูกบันทึก</b></summary>
public sealed class SettlementFormatException : Exception
{
    /// <summary>รหัสปัญหาให้หน้าจอพาไปขั้นที่ถูก (เช่น เปิดหน้าจับคู่คอลัมน์)</summary>
    public string Code { get; }

    public SettlementFormatException(string code, string message) : base(message) { Code = code; }
}

/// <summary>
/// **ตัวอ่าน settlement report 1 รูปแบบ** — ทุก adapter อยู่ใต้ <c>Services/Settlement/Adapters/</c>
/// <para>adapter เฉพาะเจ้า (Shopee/Lazada/Omise ฯลฯ) เขียน<b>เมื่อมีไฟล์จริงเท่านั้น</b> (DECISIONS ข้อ 9) —
/// เฟส 1 มี <see cref="GenericColumnMapAdapter"/> (ผู้ใช้จับคู่คอลัมน์ · ระบบจำ) เป็นตัวเดียวที่อ่านไฟล์</para>
/// </summary>
public interface ISettlementReportAdapter
{
    /// <summary>รหัสที่เก็บใน <c>SettlementChannel.AdapterCode</c></summary>
    string Code { get; }

    /// <summary>ไฟล์หน้าตานี้ adapter อ่านได้แค่ไหน (0–1) จากหัวคอลัมน์ + แถวตัวอย่าง — ไม่ throw</summary>
    decimal Detect(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> sampleRows, SettlementChannel channel);

    /// <summary>อ่านทั้งไฟล์ — อ่านไม่ได้/รูปแบบเปลี่ยน ⇒ <see cref="SettlementFormatException"/> (ห้ามคืนครึ่งไฟล์)</summary>
    /// <param name="columnMapJson">การจับคู่คอลัมน์ที่ใช้ (ของคำขอนี้ หรือที่ช่องทางจำไว้)</param>
    SettlementParseResult Parse(SettlementFileInput file, SettlementChannel channel, string? columnMapJson);
}
