using System.Net;
using System.Text;
using Accounting.Services.Implementations.Pdf;
using QuestPDF.Fluent;

namespace Accounting.Services.Implementations;

using EntDoc = Accounting.Models.Entities.Document;   // QuestPDF.Fluent มีคลาส Document ของตัวเอง — alias เดียวกับ PdfGenerationService.DocumentRenderer.cs

/// <summary>
/// รอบ 273 — แถวยอดของ<b>ใบเสร็จรับชำระที่ไม่ถือ VAT ของใบต้นทาง</b> (ผู้ใช้รายงาน: REC ของ TIV 700,000 + VAT 49,000 พิมพ์ "ยอดรวมก่อน VAT 749,000.00 · VAT 0.00")
/// <para>กฎเหล็ก #4 A (สอง renderer ห้าม drift): ป้ายแถวแรก (<see cref="SettlementSubtotalLabel"/>) · ตัววาด HTML (<see cref="AppendSettlementTotalsHtml"/>) ·
/// ตัววาด QuestPDF (<see cref="ComposeSettlementTotals"/>) อยู่ไฟล์เดียว ข้อความจาก <see cref="DocumentLabels.SettlementTotalsLines"/> ตัวเดียว ·
/// ตัวตัดสิน <c>Helpers/SettlementReceiptTotals</c> · ตัวโหลด <c>SettlementReceiptTotalsLoader</c> · แถวสุดท้ายผ่าน <c>ResolvePrintTotals</c></para>
/// </summary>
public partial class PdfGenerationService
{
    /// <summary>ป้ายแถว "ยอดก่อน VAT" ของใบนี้ — ใบปกติ = <see cref="DocumentLabels.TotalSubtotal"/> (เดิมทุกตัวอักษร) ·
    /// ใบเสร็จรับชำระที่ไม่ถือ VAT: มีภาษีหัก ⇒ <see cref="DocumentLabels.TotalSettledGross"/> · ไม่มี ⇒ <c>null</c> (ไม่มีแถวนี้ — ยอดรับชำระอยู่แถวสุดท้ายแล้ว)</summary>
    internal static string? SettlementSubtotalLabel(EntDoc doc, DocumentLabels labels)
        => doc.SettlementTotals is not { } st ? labels.TotalSubtotal
            : st.ShowSettledGrossRow ? labels.TotalSettledGross : null;

    /// <summary>renderer HTML — บรรทัดอ้างอิงใต้ยอด (escape ทุกบรรทัด · เลขที่เอกสารมาจากผู้ใช้ได้ — กฎเหล็ก #4 C)</summary>
    private static void AppendSettlementTotalsHtml(StringBuilder sb, EntDoc doc, DocumentLabels labels)
    {
        if (doc.SettlementTotals is not { } st) return;
        sb.AppendLine("<div class='settlement-ref' style='margin-top:8px;font-size:11px;color:#374151'>");
        foreach (var line in labels.SettlementTotalsLines(st))
        {
            var encoded = WebUtility.HtmlEncode(line);
            sb.AppendLine($"<div>{encoded}</div>");
        }
        sb.AppendLine("</div>");
    }

    /// <summary>renderer QuestPDF — บรรทัดเดียวกับฝั่ง HTML (<see cref="AppendSettlementTotalsHtml"/>)</summary>
    private static void ComposeSettlementTotals(QuestPDF.Fluent.ColumnDescriptor col, EntDoc doc, DocumentLabels labels)
    {
        if (doc.SettlementTotals is not { } st) return;
        foreach (var line in labels.SettlementTotalsLines(st))
            col.Item().PaddingTop(3).AlignRight().Text(line).FontSize(9).FontColor("#374151");
    }
}
