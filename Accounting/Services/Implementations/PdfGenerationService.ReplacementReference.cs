using System.Net;
using System.Text;
using Accounting.Data;
using Accounting.Services.Implementations.Pdf;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Accounting.Services.Implementations;

using EntDoc = Accounting.Models.Entities.Document;   // QuestPDF.Fluent มีคลาส Document ของตัวเอง — ใช้ alias เดียวกับ PdfGenerationService.DocumentRenderer.cs

/// <summary>
/// รอบ 266 แพ็ก B (คำตัดสิน 266-1-26) — บรรทัด <b>"ออกแทนใบเลขที่ … ลงวันที่ …"</b> บนใบเสร็จที่ออกแทนใบเดิมหลัง "ย้ายการรับชำระ"
/// <para>กฎเหล็ก #4 A (สอง renderer ห้าม drift): ตัวโหลด (<see cref="ResolveReplacementReferenceAsync"/>) · ข้อความ (<see cref="ReplacementReferenceText"/>) ·
/// ตัววาด HTML (<see cref="AppendReplacementReferenceHtml"/>) · ตัววาด QuestPDF (<see cref="ComposeReplacementRef"/>) อยู่ไฟล์เดียวกัน ใช้ข้อความจาก <see cref="DocumentLabels"/> ตัวเดียว</para>
/// <para><b>ขอบเขตจงใจแคบ:</b> เฉพาะ<b>ใบเสร็จของการรับชำระ</b> (<c>IsSettlementReceipt</c>) ที่ <c>ReplacesDocumentId</c> มีค่า — ผู้สร้างเดียวคือ <c>DocumentService.MoveSettlementAsync</c> ·
/// ใบแทนชนิดอื่นที่มีอยู่ก่อนรอบนี้ (ใบกำกับเต็มรูปแทนอย่างย่อ · ยกเลิกและออกใบแทน) พิมพ์ข้อความในหมายเหตุอยู่แล้ว ⇒ ไม่แตะ (กระดาษที่ออกไปแล้วพิมพ์ซ้ำต้องเหมือนเดิม)</para>
/// </summary>
public partial class PdfGenerationService
{
    /// <summary>เติม <c>ReplacementOfNumber/Date</c> (transient) จากใบที่ถูกแทน — เรียกจากตัวโหลด transient กลางของทั้งสอง renderer · tenant</summary>
    private static async Task ResolveReplacementReferenceAsync(AccountingDbContext db, Guid companyId, EntDoc doc)
    {
        doc.ReplacementOfNumber = null;
        doc.ReplacementOfDate = null;
        if (!doc.IsSettlementReceipt || doc.ReplacesDocumentId is not Guid replacedId) return;
        // ใบที่ถูกแทนถูกยกเลิกแล้ว (อาจถูกซ่อน) — อ่านข้าม query filter เพื่อให้เลขที่ยังพิมพ์ได้เสมอ
        var replaced = await db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.Id == replacedId && d.CompanyId == companyId)
            .Select(d => new { d.DocumentNumber, d.DocumentDate })
            .FirstOrDefaultAsync();
        if (replaced == null) return;
        doc.ReplacementOfNumber = replaced.DocumentNumber;
        doc.ReplacementOfDate = replaced.DocumentDate;
    }

    /// <summary>ข้อความบรรทัดเดียว (ยังไม่ escape) — null = ใบนี้ไม่ได้ออกแทนใบใด · ตัวเดียวของทั้งสอง renderer</summary>
    internal static string? ReplacementReferenceText(EntDoc doc, DocumentLabels labels)
    {
        if (string.IsNullOrWhiteSpace(doc.ReplacementOfNumber)) return null;
        var replacedOn = doc.ReplacementOfDate.HasValue ? labels.Date(doc.ReplacementOfDate.Value) : "-";
        return string.Format(labels.ReplacesDocument, doc.ReplacementOfNumber, replacedOn);
    }

    /// <summary>renderer HTML — กล่องเดียวกับฝั่ง QuestPDF (<see cref="ComposeReplacementRef"/>) · เลขที่เอกสารผ่าน HtmlEncode (กฎเหล็ก #4 C)</summary>
    private static void AppendReplacementReferenceHtml(StringBuilder sb, EntDoc doc, DocumentLabels labels)
    {
        if (ReplacementReferenceText(doc, labels) is not string text) return;
        sb.AppendLine("<div style='margin:8px 0;padding:6px 10px;border:1px solid #D1D5DB;background:#FFFBEB;font-size:11px'>");
        var encoded = WebUtility.HtmlEncode(text);
        sb.AppendLine($"<div style='font-weight:bold'>{encoded}</div>");
        sb.AppendLine("</div>");
    }

    /// <summary>renderer QuestPDF — กล่องเดียวกับฝั่ง HTML (<see cref="AppendReplacementReferenceHtml"/>)</summary>
    private static void ComposeReplacementRef(ColumnDescriptor col, EntDoc doc, string accent, DocumentLabels labels)
    {
        if (ReplacementReferenceText(doc, labels) is not string text) return;
        col.Item().PaddingTop(6).Border(1).BorderColor("#D1D5DB").Background("#FFFBEB")
            .PaddingVertical(5).PaddingHorizontal(9)
            .Text(text).FontSize(9.5f).Bold().FontColor(accent);
    }
}
