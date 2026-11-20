using System.Collections;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.DTOs.Document;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 276 (ฝ่ายค้าน — ข้อความตอบกลับ) — <b>ตัวช่วยกลางตัวเดียว</b>ที่ทำให้คำตอบถึงผู้ใช้ "เห็นเฉพาะเอกสารที่ตัวเองสร้าง" ปลอดภัย
/// ทุกจุดที่ตัวกรองแถวถูกยก (ทางเขียน: OCR · แปลง · คัดลอก · สร้าง/แก้) และทางอ่านที่ตรวจทั้งบริษัท (ใบซ้ำ):
/// <list type="bullet">
/// <item>ข้อความเหตุ (exception) ที่อ้างเลขเอกสารของคนอื่น หรือมาจากด่านข้ามเอกสาร (ยอดแปลงสะสม · ใบซ้ำ) ⇒ ข้อความกลาง "มีเอกสารอื่นในบริษัทที่…"
/// (ไม่มีเลข · ชื่อลูกค้า · ยอดของใบนั้น) + รหัสกติกาเดิม</item>
/// <item>ผลสำเร็จ (DTO) — สตริงที่มีเลขเอกสารของคนอื่นถูกแทน · id เอกสาร/สแกนของคนอื่นถูกล้าง</item>
/// <item>ผลตรวจใบซ้ำที่ชี้ใบของคนอื่น ⇒ ผลตัดสินอย่างเดียว (<see cref="RedactDuplicate"/>)</item>
/// </list>
/// ผู้เรียก: <c>Filters/DocumentRestrictionFilter</c> (ท้ายทางเขียนที่ยกตัวกรอง) · <c>DocumentService.CheckDuplicateAsync</c>
/// </summary>
public static class RestrictedResponseRedactor
{
    public const string OtherDocumentText = "(เอกสารอื่นในบริษัท)";

    /// <summary>รหัสกติกาของด่านที่ตัดสินจากเอกสารหลายใบ (ยอดแปลง/วางบิลสะสม · ใบซ้ำ) — ข้อความมียอดที่มาจากใบของคนอื่นเสมอ ⇒ แทนทั้งข้อความ</summary>
    internal static bool IsCrossDocumentRule(string? ruleCode) =>
        ruleCode != null && (ruleCode.StartsWith("CONVERT-", StringComparison.Ordinal)
            || ruleCode == "R276-OVER-CONVERT" || ruleCode.Contains("DUPLICATE", StringComparison.OrdinalIgnoreCase));

    internal static string GenericConflictMessage(string? ruleCode) =>
        "มีเอกสารอื่นในบริษัทที่เกี่ยวกับรายการนี้ (เช่นถูกแปลง/ออกไปแล้ว หรือเป็นใบซ้ำ) จึงทำต่อไม่ได้ — รายละเอียดเป็นเอกสารของผู้ใช้อื่น "
        + "ให้ผู้ดูแล/บัญชีตรวจให้" + (string.IsNullOrEmpty(ruleCode) ? "" : $" (รหัส {ruleCode})");

    private static readonly Regex DocNumberToken = new(@"[A-Za-z]{1,12}(?:[-_/][0-9A-Za-z]|[0-9])[0-9A-Za-z\-/_.]{2,}", RegexOptions.Compiled);

    /// <summary>แทนเลขเอกสารที่ซ่อน (pure)</summary>
    internal static string Replace(string text, IReadOnlyCollection<string> hiddenNumbers)
    {
        foreach (var n in hiddenNumbers.OrderByDescending(x => x.Length))
            text = text.Replace(n, OtherDocumentText, StringComparison.Ordinal);
        return text;
    }

    /// <summary>ผลตรวจใบซ้ำที่ชี้ใบของผู้ใช้อื่น — คืนแค่ "มีใบซ้ำ + เหตุ" (ไม่มี id/เลข/ผู้ติดต่อ/ยอดของใบนั้น)</summary>
    public static DuplicateDocumentCandidate RedactDuplicate(DuplicateDocumentCandidate c, DateTime enteredDate, decimal enteredAmount) =>
        new(Guid.Empty, OtherDocumentText + " — แจ้งผู้ดูแล/บัญชีตรวจ", c.DocumentType, enteredDate.Date, enteredAmount, "", null, null, c.MatchReason, c.IsStrong);

    /// <summary>เลขเอกสารในข้อความที่เป็นของบริษัทนี้แต่ไม่ใช่ของผู้ใช้ (ไม่ผ่านตัวกรองแถว — ตัดสินเองจากผู้สร้าง)</summary>
    private static async Task<List<string>> HiddenNumbersAsync(AccountingDbContext db, Guid companyId, Guid userId, IEnumerable<string?> texts,
        CancellationToken ct = default)
    {
        var tokens = texts.Where(t => !string.IsNullOrEmpty(t))
            .SelectMany(t => DocNumberToken.Matches(t!).Select(m => m.Value.TrimEnd('.', '/', '-', '_')))
            .Where(t => t.Length >= 4).Distinct().Take(200).ToList();
        if (tokens.Count == 0) return new();
        var uid = userId.ToString();
        return await db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.CompanyId == companyId && tokens.Contains(d.DocumentNumber)
                && !(d.CreatedByUserId == userId || (d.CreatedByUserId == null && d.CreatedBy == uid)))
            .Select(d => d.DocumentNumber).Distinct().ToListAsync(ct);
    }

    /// <summary>ข้อความเหตุที่ปลอดภัย — อ้างเลขของคนอื่น หรือเป็นด่านข้ามเอกสาร ⇒ ข้อความกลาง · อื่น ๆ = เดิม</summary>
    public static async Task<string> SafeMessageAsync(AccountingDbContext db, Guid companyId, Guid userId, string message, string? ruleCode,
        CancellationToken ct = default)
    {
        if (IsCrossDocumentRule(ruleCode)) return GenericConflictMessage(ruleCode);
        return (await HiddenNumbersAsync(db, companyId, userId, new[] { message }, ct)).Count > 0 ? GenericConflictMessage(ruleCode) : message;
    }

    /// <summary>ผลสำเร็จ — แทนเลขของคนอื่นในทุกสตริง (ลึก 4 ชั้น) · ล้าง id เอกสาร (<c>*DocumentId</c>) ของคนอื่นและ id สแกน (<c>*ScanId</c>) ที่ไม่ใช่ของผู้ใช้</summary>
    public static async Task RedactValueAsync(AccountingDbContext db, Guid companyId, Guid userId, object? value, CancellationToken ct = default)
    {
        if (value == null) return;
        var strings = new List<string?>();
        var docIds = new HashSet<Guid>();
        var scanIds = new HashSet<Guid>();
        Walk(value, 0, (o, p, v) =>
        {
            if (v is string s) strings.Add(s);
            else if (v is Guid g && g != Guid.Empty && p.Name.EndsWith("DocumentId", StringComparison.Ordinal)) docIds.Add(g);
            else if (v is Guid sg && sg != Guid.Empty && p.Name.EndsWith("ScanId", StringComparison.Ordinal)) scanIds.Add(sg);
        });
        var hidden = await HiddenNumbersAsync(db, companyId, userId, strings, ct);
        var uid = userId.ToString();
        var hiddenDocs = docIds.Count == 0 ? new HashSet<Guid>() : (await db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.CompanyId == companyId && docIds.Contains(d.Id) && !(d.CreatedByUserId == userId || (d.CreatedByUserId == null && d.CreatedBy == uid)))
            .Select(d => d.Id).ToListAsync(ct)).ToHashSet();
        var hiddenScans = scanIds.Count == 0 ? new HashSet<Guid>() : (await db.OcrScanResults.AsNoTracking()
            .Where(s => s.CompanyId == companyId && scanIds.Contains(s.Id) && s.CreatedBy != uid
                && !db.FileAttachments.Any(f => f.Id == s.FileAttachmentId && f.UploadedByUserId == userId))
            .Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        if (hidden.Count == 0 && hiddenDocs.Count == 0 && hiddenScans.Count == 0) return;
        Walk(value, 0, (o, p, v) =>
        {
            if (!p.CanWrite) return;
            if (v is string s && hidden.Count > 0 && hidden.Any(h => s.Contains(h, StringComparison.Ordinal))) p.SetValue(o, Replace(s, hidden));
            else if (v is Guid g && (hiddenDocs.Contains(g) || hiddenScans.Contains(g)))
                p.SetValue(o, Nullable.GetUnderlyingType(p.PropertyType) != null ? null : Guid.Empty);
        });
    }

    private static void Walk(object? o, int depth, Action<object, System.Reflection.PropertyInfo, object?> visit)
    {
        if (o == null || depth > 4) return;
        var t = o.GetType();
        if (t.IsPrimitive || t.IsEnum || o is string || o is Guid || o is DateTime || o is decimal) return;
        if (o is IEnumerable seq) { foreach (var x in seq) Walk(x, depth + 1, visit); return; }
        if (t.Namespace?.StartsWith("Accounting.Models.DTOs") != true) return;
        foreach (var p in t.GetProperties())
        {
            if (p.GetIndexParameters().Length > 0) continue;
            var v = p.GetValue(o);
            visit(o, p, v);
            if (v != null && v is not string && (v is IEnumerable || v.GetType().Namespace?.StartsWith("Accounting.Models.DTOs") == true))
                Walk(v, depth + 1, visit);
        }
    }
}
