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
/// <param name="LiteralDates">วันที่ของแถวนี้ตามที่<b>ตัวอ่านก่อนรอบ 200</b>อ่าน (ทิ้งเวลา/เขตเวลาท้ายค่า · ลำดับวัน/เดือนแบบเดิม) — ทีละ "ชุด"
/// (index เดียวกันทุกแถวของไฟล์) · <b>ใช้คิดคีย์กันซ้ำรุ่นก่อนเท่านั้น</b> (<c>SettlementTxnKey.LegacyKeys</c> · ฝ่ายค้าน I-1) ห้ามใช้เป็นวันที่รายการ ·
/// null = ไม่มีไฟล์ (PaymentIntent)</param>
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
    Guid? PaymentIntentId = null,
    IReadOnlyList<DateTime?>? LiteralDates = null);

/// <summary>ผลการอ่านไฟล์ทั้งไฟล์</summary>
/// <param name="Headers">หัวคอลัมน์ที่พบ (ตามลำดับในไฟล์)</param>
/// <param name="SkippedRows">แถวที่ข้ามโดยตั้งใจ (แถวว่าง · แถวสรุปยอด "รวม/Total") พร้อมเหตุผล — ไม่ใช่แถวที่อ่านพลาด</param>
/// <param name="LearnedDateOrder">ลำดับวัน/เดือนที่<b>ไฟล์นี้พิสูจน์ได้</b>ขณะการจับคู่ยังเป็น Auto (R-B9) — ผู้นำเข้าจำให้ช่องทาง · null = ไม่มีอะไรใหม่ให้จำ</param>
/// <param name="LearnedTimeZone">เขตเวลาที่หัวคอลัมน์วันที่ประกาศไว้ขณะการจับคู่ยังเป็น Auto (R-B8) — ผู้นำเข้าจำให้ช่องทาง</param>
/// <param name="Warnings">สิ่งที่ผู้ใช้<b>ต้องเห็นชัด</b> (แถบเตือน ไม่ใช่บรรทัดเทาของ <paramref name="SkippedRows"/>) — แถวสรุปที่ข้ามพร้อมยอด ·
/// แถวไม่มีเลขอ้างอิงที่นำเข้าเป็นรายการ (DECISIONS ข้อ 39 · ฝ่ายค้าน I-7) · null = ไม่มี</param>
public sealed record SettlementParseResult(
    string AdapterCode,
    IReadOnlyList<string> Headers,
    IReadOnlyList<SettlementParsedRow> Rows,
    IReadOnlyList<string> SkippedRows,
    SettlementDateOrder? LearnedDateOrder = null,
    SettlementFileTimeZone? LearnedTimeZone = null,
    IReadOnlyList<string>? Warnings = null);

/// <summary>ข้อมูลจากหัวรอบโอนที่ผู้ใช้กรอก ซึ่งตัวอ่านใช้เป็นหลักฐานได้ (ไม่ใช่การเดา) — ช่วงวันที่ของรอบโอน (วันตามปฏิทินไทย)
/// ตัดสินลำดับวัน/เดือนของไฟล์ช่วงสั้นที่ไม่มีวันที่เกิน 12 (R-B9)</summary>
/// <param name="WillRemember">ค่าที่ผู้ใช้เลือกในขั้นจับคู่คอลัมน์ครั้งนี้จะถูกจำไว้กับช่องทางจริงไหม (สิทธิ์ตั้งค่าช่องทาง + ไม่ใช่คีย์ API + ติ๊กจำ ·
/// ฝ่ายค้าน I-2) — ข้อความถามรูปแบบวันที่/เขตเวลาห้ามสัญญาว่า "จำให้" เมื่อ false</param>
/// <param name="NotRememberedReason">เหตุที่ไม่จำ (ไม่มีสิทธิ์/คีย์ API) — null เมื่อจำ หรือเมื่อผู้ใช้ไม่ได้ติ๊กจำเอง</param>
public sealed record SettlementParseContext(DateTime? PeriodFrom, DateTime? PeriodTo, bool WillRemember = false,
    string? NotRememberedReason = null);

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
    /// <param name="context">ช่วงวันที่ของรอบโอนจากหัวรอบโอน (หลักฐานตัดสินลำดับวัน/เดือน) — null = ไม่มี</param>
    SettlementParseResult Parse(SettlementFileInput file, SettlementChannel channel, string? columnMapJson,
        SettlementParseContext? context = null);
}
