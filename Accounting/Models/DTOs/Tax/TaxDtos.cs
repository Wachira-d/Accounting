using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Tax;

public record CreateTaxReportRequest(
    TaxType TaxType,
    int Year,
    int Month);

public record TaxReportResponse(
    Guid Id,
    TaxType TaxType,
    int Year,
    int Month,
    TaxReportStatus Status,
    DateTime? FiledDate,
    decimal OutputVat,
    decimal InputVat,
    decimal NetVat,
    decimal TotalIncome,
    decimal TotalTaxWithheld,
    List<TaxReportLineResponse> Lines,
    string? Notes = null,
    // ── ข้อมูลผู้ประกอบการ (สำหรับ header ฟอร์มราชการ §87) — เติมตอน GetTaxReportAsync ──
    string? CompanyName = null,
    string? CompanyTaxId = null,
    string? CompanyBranchCode = null,
    /// <summary>ภาษีเงินได้นิติบุคคล (ภ.ง.ด.50/51) — null สำหรับรายงานชนิดอื่น.
    /// แยกจาก TotalTaxWithheld ที่เป็นยอดหัก ณ ที่จ่าย</summary>
    decimal? CitAmount = null);

public record UpdateTaxReportRequest(
    string? Notes,
    List<UpdateTaxReportLineRequest>? Lines);

public record UpdateTaxReportLineRequest(
    Guid Id,
    decimal? IncomeAmount,
    decimal? TaxRate,
    decimal? TaxAmount,
    string? Description,
    bool? Excluded = null);

public record PullableDocumentDto(
    Guid Id,
    string DocumentNumber,
    string DocumentType,
    DateTime DocumentDate,
    string ContactName,
    decimal SubTotal,
    decimal VatAmount,
    bool IsInput);

public record PullDocumentRequest(Guid DocumentId);

public record TaxReportLineResponse(
    Guid Id,
    int LineOrder,
    string? TaxPayerId,
    string? TaxPayerName,
    DateTime TransactionDate,
    string? Description,
    decimal IncomeAmount,
    decimal TaxRate,
    decimal TaxAmount,
    string? IncomeTypeCode,
    bool IsExcluded = false,
    // ── ฟอร์มราชการ §87 (ฉบับที่ 104) — เติมตอน GetTaxReportAsync ──
    Guid? DocumentId = null,
    string? InvoiceNumber = null,   // เลขที่ใบกำกับ (ขาย=เลขเรา / ซื้อ=เลขผู้ขาย)
    string? BranchCode = null,      // สาขาผู้ขาย/ผู้ซื้อ (00000=สนญ.)
    // ชนิดเอกสาร "ตามกฎหมาย" ของแถวนี้ — ตัวย่อของเลขที่เอกสาร (TIV/REC/RV/INV)
    // บอกแค่ชนิดข้อมูลในระบบ ไม่ได้บอกว่ากระดาษใบนั้นคืออะไร: ใบที่หัวพิมพ์
    // "ใบกำกับภาษี/ใบเสร็จรับเงิน" เหมือนกันเป๊ะ อยู่ได้ทั้งบน TIV- และ REC-
    // ⇒ นักบัญชีที่เปิดรายงานเห็นเลขปนกันแล้วไล่ไม่ออกว่าใบไหนเป็นใบกำกับจริง
    // ค่านี้มาจาก resolver หัวเอกสารตัวเดียวกับที่พิมพ์ลงกระดาษ (ห้ามคำนวณเอง)
    string? DocumentKindLabel = null,
    /// <summary>ฝั่งของบรรทัด: <c>"output"</c> ภาษีขาย · <c>"input"</c> ภาษีซื้อ ·
    /// <c>"summary"</c> ยอดรวมของแบบ ภ.พ.30 (ไม่มีวันที่/เลขที่ใบ ห้ามลงตาราง §87)
    ///
    /// <para>เซิร์ฟเวอร์คำนวณจาก <c>Helpers/VatReportLineKind</c> — หน้าเว็บ<b>แสดง
    /// อย่างเดียว</b> ห้ามจำแนกจาก <c>IncomeTypeCode</c> เอง (เดิม tax.html มีสำเนา
    /// กติกา 2 ชุด ซึ่งเป็นทางที่รหัสใหม่หลุดออกจากการจัดฝั่งโดยไม่มีอะไรฟ้อง)</para></summary>
    string Side = "output");
