using Accounting.Models.DTOs.Document;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 259 P6 — ไฟล์ส่งออก "เงินรอจัดสรร" (xlsx/csv ผ่านตัวเขียนกลาง <see cref="TabularExport"/> · MiniExcel) — ตัวเลขทุกช่องมาจาก
/// <c>CustomerCreditService.ListAsync</c> ตัวเดียวกับหน้า (ไม่รวมยอดใหม่ที่นี่ · กติกา §6.0a-x "ไฟล์ = หน้า")
/// </summary>
public static class CustomerCreditExport
{
    public static TabularFile Render(TabularFormat format, CustomerCreditListResponse list, DateTime todayThai)
    {
        var cols = new[]
        {
            new TabularColumn<CustomerCreditRow>("ลูกค้า", r => r.ContactName ?? "(ไม่ทราบลูกค้า)"),
            new TabularColumn<CustomerCreditRow>("วันรับเงิน", r => r.PaymentDate, TabularCellKind.Date),
            new TabularColumn<CustomerCreditRow>("เลขการรับชำระ", r => r.PaymentNumber),
            new TabularColumn<CustomerCreditRow>("อ้างอิง", r => r.Reference),
            new TabularColumn<CustomerCreditRow>("ยอดรับ", r => r.Amount, TabularCellKind.Money),
            new TabularColumn<CustomerCreditRow>("คงเหลือรอจัดสรร", r => r.UnappliedAmount, TabularCellKind.Money),
            new TabularColumn<CustomerCreditRow>("ใช้ได้ (หักยอดที่จองให้ใบร่าง)", r => r.Available, TabularCellKind.Money),
            new TabularColumn<CustomerCreditRow>("เหตุผล", r => r.ReasonLabel),
            new TabularColumn<CustomerCreditRow>("หมายเหตุ", r => r.Note),
            new TabularColumn<CustomerCreditRow>("อายุ (วัน)", r => r.AgeDays, TabularCellKind.Integer),
            new TabularColumn<CustomerCreditRow>("สถานะ", r => r.NeedsReview ? "ต้องตรวจ (ก่อนรอบ 258)" : r.UnappliedAmount > 0m ? "ค้าง" : "จัดสรรครบ"),
            new TabularColumn<CustomerCreditRow>("ใบที่ผูก/ขาออก", r => string.Join(" · ",
                r.Allocations.Select(a => $"{a.DocumentNumber} {a.AllocatedAmount:N2}")
                    .Concat((r.Movements ?? new List<CustomerCreditMovementRow>()).Where(m => m.IsLive)
                        .Select(m => $"{m.KindLabel} {m.Amount:N2}{(m.TargetDocumentNumber != null ? " " + m.TargetDocumentNumber : "")}")))),
        };
        var footer = new[]
        {
            new CustomerCreditRow(Guid.Empty, "", null, "รวมเงินรอจัดสรรคงเหลือ (ไม่รวมรายการที่ต้องตรวจ)", todayThai, 0m, list.TotalUnapplied,
                "", "", null, 0, null, new List<CustomerCreditAllocationRow>()),
        };
        var title = new List<string>
        {
            "เงินรอจัดสรร (เงินรับจากลูกค้าที่ยังไม่ผูกกับใบ)",
            $"ณ วันที่ {ThaiDate.ToThaiDisplayString(todayThai)}"
                + (list.GlBalance is decimal gl ? $" · ยอดผัง {UnappliedReceiptAccount.Code} ในบัญชีแยกประเภท {gl:N2}" : ""),
        };
        if (!string.IsNullOrWhiteSpace(list.GlNote)) title.Add(list.GlNote!);
        var sheet = TabularExport.Sheet("เงินรอจัดสรร", cols, list.Items, footer, title);
        return TabularExport.Render(format, "เงินรอจัดสรร", null, todayThai, sheet);
    }
}
