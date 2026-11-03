using Accounting.Models.Constants;
using Accounting.Services.Implementations;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §B.5 · คำตัดสินข้อ 231-4) — <b>กติกาใบแจ้งหนี้ค่าใช้งาน/ส่วนเกินรายเดือน (pure)</b> ตัวเดียวที่ <c>UsageInvoicingJob</c> ใช้:
/// <list type="bullet">
/// <item><see cref="Dispose"/> — งวดนี้ทำอะไร: prepaid ปิดรอบไม่มีใบ · รวม 0 บาท ปิดงวดไม่มีใบ (ใบที่กฎหมายบังคับในโหมดล็อก) · ต่ำกว่าขั้นต่ำ ยกยอด · อื่น ออกใบ</item>
/// <item><see cref="Build"/> — บรรทัดต่อ (งวด · รหัส · หน่วย ณ ตอนซื้อ · ราคา/หน่วย) — ฝ่ายค้าน B3/S2: ใบเดียวรวมได้หลายงวดที่ยกยอดมา (คนละบรรทัด) และราคาต่างกันในงวดเดียวกันแยกบรรทัด
/// ("@ ฿x") เพื่อให้ Qty × ราคา/หน่วย = ยอดบรรทัดทุกบรรทัด (§86/4 — ยอดบรรทัดคือ <c>ChargedAmount</c> ที่ snapshot ไว้) · ชื่อจาก <see cref="OverageResources.InvoiceName"/> → แคตตาล็อก → <see cref="QuotaResources.InvoiceName"/> · งวดเป็นเดือนไทย</item>
/// <item><see cref="LatestPeriod"/>/<see cref="Reference"/> — ใบที่รวมหลายงวดอ้างอิง <c>USAGE-{งวดล่าสุด}-{shortId}</c> · แต่ละแถวตีตรา <c>BilledPeriod</c> ของงวดตัวเอง</item>
/// <item><see cref="DueDate"/> — ครบกำหนด = วันออก + <c>OverageGraceDays</c> (ผ่อนผันอย่างน้อย 1 วัน)</item>
/// </list>
/// </summary>
public static class UsageInvoiceLines
{
    /// <summary>ภาพของ <c>UsageEvent</c> ที่ตัวตัดสินต้องใช้</summary>
    public sealed record Row(string Period, string FeatureCode, string? UnitLabelSnapshot, decimal UnitPriceSnapshot, int Quantity, decimal ChargedAmount);

    public enum Disposition { Issue = 0, ClosePrepaid = 1, CloseZero = 2, Carry = 3 }

    public static Disposition Dispose(bool prepaid, decimal total, decimal minInvoice)
    {
        if (prepaid) return Disposition.ClosePrepaid;
        if (total <= 0m) return Disposition.CloseZero;
        if (total < minInvoice) return Disposition.Carry;
        return Disposition.Issue;
    }

    /// <summary>ขั้นต่ำที่ใช้จริง — ค่าตั้งของแอดมิน (&gt; 0) ไม่งั้น fallback ค่าตั้งต้น</summary>
    public static decimal MinInvoice(decimal configured, decimal fallback) => configured > 0m ? configured : fallback;

    public static DateTime DueDate(DateTime issueDateUtc, int graceDays) => issueDateUtc.AddDays(Math.Max(1, graceDays));

    /// <summary>"ส่วนเกินโควตาเอกสาร · งวด พ.ย. 2569 @ ฿5.00" — ชื่อที่ผู้เช่าอ่านออก ไม่ใช่รหัสดิบ · ราคา/หน่วยบนบรรทัด (S2) · ราคา 0 = ไม่แสดง @</summary>
    public static string Description(string featureCode, string? catalogName, string period, decimal unitPrice = 0m)
        => $"{OverageResources.InvoiceName(featureCode) ?? catalogName ?? QuotaResources.InvoiceName(featureCode) ?? featureCode} · งวด {UsageHistoryRule.Label(period)}"
           + (unitPrice > 0m ? $" @ ฿{unitPrice:N2}" : "");

    /// <summary>งวดล่าสุดในชุดแถว (ข้อความ yyyy-MM เรียงได้) — ใช้ตั้งอ้างอิงใบ</summary>
    public static string LatestPeriod(IEnumerable<Row> rows) => rows.Select(r => r.Period).DefaultIfEmpty("").Max()!;

    /// <summary>อ้างอิงคงที่ต่อ (บริษัท, งวดล่าสุด) — ถ้ารอบก่อนออกใบแล้วตีตราไม่สำเร็จ จะเห็นว่าเป็นใบซ้ำได้ทันที</summary>
    public static string Reference(Guid companyId, string latestPeriod) => $"{PlatformInvoiceRules.UsageReferencePrefix}{latestPeriod}-{companyId.ToString("N")[..8]}";

    /// <summary>แยกบรรทัดตาม (งวด · รหัส · หน่วย ณ ตอนซื้อ · ราคา/หน่วย) — แพ็ก 500 กับแพ็ก 1,000 คนละบรรทัด (ฝ่ายค้านรอบ 213 S3) · งวดที่ยกยอดมาคนละบรรทัด (B3) ·
    /// ราคาต่างกันคนละบรรทัด (S2) · เรียงงวดเก่าก่อน แล้วยอดมากก่อน</summary>
    public static List<PlatformInvoiceLine> Build(IEnumerable<Row> rows, Func<string, (string? Name, string? UnitLabel)?> catalog)
    {
        return rows
            .GroupBy(r => new { r.Period, r.FeatureCode, r.UnitLabelSnapshot, r.UnitPriceSnapshot })
            .Select(g =>
            {
                var meta = catalog(g.Key.FeatureCode);
                var qty = g.Sum(x => x.Quantity);
                return new
                {
                    g.Key.Period,
                    Line = new PlatformInvoiceLine(
                        Description: Description(g.Key.FeatureCode, meta?.Name, g.Key.Period, g.Key.UnitPriceSnapshot),
                        AmountNet: g.Sum(x => x.ChargedAmount),
                        Unit: AddOnBilling.InvoiceUnit(g.Key.UnitLabelSnapshot, meta?.UnitLabel),
                        Quantity: qty > 0 ? qty : 1m),
                };
            })
            .OrderBy(x => x.Period).ThenByDescending(x => x.Line.AmountNet)
            .Select(x => x.Line)
            .ToList();
    }
}
