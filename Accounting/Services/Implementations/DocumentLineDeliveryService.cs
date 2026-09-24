using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ส่งเอกสารผ่าน LINE Messaging API ไปยังลูกค้าที่ผูก LineUserId กับ
/// Contact ของเรา (ผ่าน LineBindCode workflow ที่มีอยู่). ใช้ flex message
/// แสดง: เลขเอกสาร, ยอดรวม, due date, ลิงก์ดู PDF + จ่ายเงิน.
///
/// FlowAccount/PEAK ยังไม่มี — เป็น Thai SME competitive moat.</summary>
public interface IDocumentLineDeliveryService
{
    /// <summary>ส่งใบเอกสารผ่าน LINE. คืน true ถ้าส่งสำเร็จ (Contact มี
    /// LineUserId + LINE API ตอบ OK). false ถ้า contact ไม่ผูก LINE / channel
    /// ไม่ active / API ล้ม.</summary>
    Task<bool> SendDocumentLineAsync(Guid companyId, Guid documentId, CancellationToken ct = default);
}

public class DocumentLineDeliveryService : IDocumentLineDeliveryService
{
    private readonly AccountingDbContext _db;
    private readonly ILineNotifyService _line;
    private readonly ILogger<DocumentLineDeliveryService> _logger;
    private readonly IConfiguration? _config;

    public DocumentLineDeliveryService(AccountingDbContext db, ILineNotifyService line,
        ILogger<DocumentLineDeliveryService> logger, IConfiguration? config = null)
    {
        _db = db;
        _line = line;
        _logger = logger;
        _config = config;
    }

    public async Task<bool> SendDocumentLineAsync(Guid companyId, Guid documentId,
        CancellationToken ct = default)
    {
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId, ct);
        if (doc == null) return false;
        // ไม่ Include Contact (INNER JOIN ตัดใบที่ contact ถูกลบ) — hydrate แยก
        await _db.HydrateContactAsync(companyId, doc);
        var lineId = doc.Contact?.LineUserId;
        if (string.IsNullOrWhiteSpace(lineId))
        {
            _logger.LogInformation("Doc {DocId} contact {ContactId} ไม่มี LineUserId — skip LINE delivery",
                documentId, doc.ContactId);
            return false;
        }

        // ภาษา + หัวเอกสารจากตัวกลางตัวเดียวกับ PDF (S-12 รอบ 193) — เดิมคำนวณ `doc ?? company` เอง (ข้ามภาษาของ
        // เทมเพลต) และมีตารางชื่อชนิดเอกสาร 6 ชนิดของตัวเอง ⇒ LINE บอก "ใบแจ้งหนี้" ขณะที่ลิงก์เปิด PDF หัวอื่น/ภาษาอื่น
        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, doc.Id);
        var isEn = heading.IsEnglish;
        var typeLabel = heading.Title;

        var dueText = doc.DueDate.HasValue
            ? (isEn ? $"Due {doc.DueDate.Value:dd/MM/yyyy}" : $"กำหนดชำระ {doc.DueDate.Value:dd/MM/yyyy}")
            : (isEn ? "Due on receipt" : "ชำระทันที");

        var flex = new
        {
            type = "bubble",
            body = new
            {
                type = "box", layout = "vertical", spacing = "md", contents = new object[]
                {
                    new { type = "text", text = typeLabel, weight = "bold", size = "lg", color = "#1e40af", wrap = true },
                    new { type = "text", text = doc.DocumentNumber, weight = "bold", size = "xl" },
                    new {
                        type = "box", layout = "vertical", margin = "md", spacing = "sm",
                        contents = new object[]
                        {
                            new {
                                type = "box", layout = "baseline", contents = new object[]
                                {
                                    new { type = "text", text = isEn ? "Total" : "ยอดรวม", color = "#666", size = "sm", flex = 0 },
                                    new { type = "text", text = $"฿{doc.TotalAmount:N2}",
                                          weight = "bold", size = "md", align = "end" },
                                }
                            },
                            new {
                                type = "box", layout = "baseline", contents = new object[]
                                {
                                    new { type = "text", text = dueText, color = "#dc2626", size = "sm" },
                                }
                            },
                        }
                    },
                }
            },
            footer = new
            {
                type = "box", layout = "vertical", spacing = "sm", contents = new object[]
                {
                    new {
                        type = "button", style = "primary", color = "#1e40af",
                        action = new {
                            type = "uri", label = isEn ? "View document / Pay" : "ดูเอกสาร / ชำระเงิน",
                            // เดิม hardcode https://app.example.com/... = ลิงก์ตาย
                            // จริงใน prod — ใช้ App:BaseUrl (ตัวเดียวกับ payslip
                            // LINE) ชี้เข้า portal ลูกค้าของบริษัทนั้น
                            uri = $"{(_config?["App:BaseUrl"] ?? "https://app.nextacc.com").TrimEnd('/')}/portal.html?company={doc.CompanyId}"
                        }
                    }
                }
            }
        };

        try
        {
            await _line.PushFlexToUserAsync(companyId, lineId,
                isEn ? $"{typeLabel} {doc.DocumentNumber} — Total ฿{doc.TotalAmount:N2}"
                     : $"{typeLabel} {doc.DocumentNumber} ยอดรวม ฿{doc.TotalAmount:N2}", flex);
            _logger.LogInformation("LINE delivery sent for doc {DocId} → LineUserId {Line}",
                documentId, lineId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LINE delivery failed for doc {DocId}", documentId);
            return false;
        }
    }
}
