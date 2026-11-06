using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// <b>ลำดับลบ "แถวลูก" ของเอกสารตอนลบถาวร — ตัวตั้งตัวเดียว</b> (รอบ 242 · เจ้าของ 2026-11-05)
///
/// <para>═══ ที่มา (บั๊กจริง) ═══ <c>PurgeDocumentAsync</c> ลบ <c>DocumentApprovals</c> <b>ก่อน</b> <c>DocumentSignatures</c> — ฐาน production สร้างสองตารางนี้
/// ด้วย SQL ตอนบูต (<c>Program.cs</c>) ที่ FK เป็น <c>ON DELETE RESTRICT</c> (<c>FK_DocumentSignatures_Approvals</c>) ⇒ ใบที่เคยผ่านขั้นอนุมัติ/มีลายเซ็น
/// ลบถาวรไม่ได้เลย (500 · 23001) · และตารางอื่นที่อ้างเอกสารด้วย FK แบบ RESTRICT/NO ACTION (ใบทวงหนี้ · การเตือนชำระ · ภาษีเลื่อนงวด ·
/// ลิงก์ข้ามบริษัท · เช็คลงวันที่ล่วงหน้า · การปันส่วนชำระ · เช็คของการชำระ) ไม่เคยถูกจัดการ ⇒ ลบใบที่มีของพวกนี้ก็ล้มแบบเดียวกัน</para>
///
/// <para>═══ กติกา ═══ <see cref="DocumentSteps"/> เรียงลูกก่อนแม่ (ลายเซ็น → ขั้นอนุมัติ → …) · <see cref="PaymentSteps"/> ทำก่อนลบ
/// <c>Payments</c> · ตารางที่อ้างเอกสารด้วย FK แบบ RESTRICT/NO ACTION ทุกตัวต้องอยู่ในชุดนี้ (เทสต์ Db <c>DocumentPurgeDbTests</c> ตรวจกับ
/// <c>pg_constraint</c> ของฐานจริง — ตารางใหม่ที่อ้างเอกสารแล้วไม่เพิ่มที่นี่ = เทสต์แดง) · ทุกคำสั่งรับชุด id ที่ผู้เรียก<b>ยืนยันบริษัทแล้ว</b></para>
/// </summary>
public static class DocumentPurgeOrder
{
    public enum StepAction { Delete, Nullify }

    /// <summary>หนึ่งขั้น: ตาราง · คอลัมน์ที่อ้าง · ตารางแม่ที่คอลัมน์นั้นชี้ · ลบแถว/ตั้งเป็น NULL · SQL (พารามิเตอร์ {0} = uuid[] ของ id แม่)</summary>
    public sealed record Step(string Table, string Column, string ReferencedTable, StepAction Action, string Sql);

    private static Step Delete(string table, string column, string referenced, string? where = null)
        => new(table, column, referenced, StepAction.Delete,
            $"DELETE FROM \"{table}\" WHERE {where ?? $"\"{column}\" = ANY({{0}})"}");

    private static Step Nullify(string table, string column, string referenced)
        => new(table, column, referenced, StepAction.Nullify,
            $"UPDATE \"{table}\" SET \"{column}\" = NULL WHERE \"{column}\" = ANY({{0}})");

    /// <summary>แถวลูกของเอกสาร (id แม่ = Documents.Id) — <b>เรียงลูกก่อนแม่</b>: ลายเซ็นชี้ขั้นอนุมัติด้วย RESTRICT ⇒ ต้องมาก่อนขั้นอนุมัติ</summary>
    public static readonly IReadOnlyList<Step> DocumentSteps = new[]
    {
        // ลายเซ็นของใบนี้ + ลายเซ็นที่ชี้ขั้นอนุมัติของใบนี้ (กันแถวที่ DocumentId ไม่ตรงกับขั้นอนุมัติ)
        Delete("DocumentSignatures", "DocumentId", "Documents",
            "\"DocumentId\" = ANY({0}) OR \"DocumentApprovalId\" IN (SELECT \"Id\" FROM \"DocumentApprovals\" WHERE \"DocumentId\" = ANY({0}))"),
        Delete("DocumentApprovals", "DocumentId", "Documents"),
        Delete("DocumentRevisions", "DocumentId", "Documents"),
        Delete("DunningLetterLines", "DocumentId", "Documents"),
        Delete("PaymentReminders", "DocumentId", "Documents"),
        Delete("VatDeferrals", "DocumentId", "Documents"),
        Delete("CrossTenantDocumentLinks", "SourceDocumentId", "Documents"),
        Nullify("CrossTenantDocumentLinks", "TargetDocumentId", "Documents"),
        Nullify("PostDatedChecks", "SourceDocumentId", "Documents"),
        Delete("OcrValidationLogs", "DocumentId", "Documents"),
        Delete("DocumentAdjustingJournalLines", "DocumentId", "Documents"),
        Delete("PaymentAllocations", "DocumentId", "Documents"),
        // ฝ่ายค้านรอบ 242 N3 — เครดิตภาษีถูกหัก ณ ที่จ่ายที่ยังรอหนังสือรับรอง (Pending = 0) ลบไปพร้อมใบ · Received/Claimed ถูกตัวตัดสินกันก่อนถึงขั้นนี้ ·
        // รอบ 252 (ฝ่ายค้าน WP-09 M2): แถว Pending ที่ได้ใบบางส่วนแล้ว (ReceivedAmount > 0) ไม่ลบ — ตัวตัดสินกันไว้ก่อน (WhtCreditUsed)
        Delete("WhtCreditsReceived", "DocumentId", "Documents", "\"DocumentId\" = ANY({0}) AND \"Status\" = 0 AND \"ReceivedAmount\" = 0"),
        // รอบ 240 — outbox เหตุการณ์ลูกค้าเซ็น (ไม่มี FK) · ใบหายแล้วห้ามยิง webhook ถึงเว็บต้นทางเรื่องใบที่ไม่มีอยู่
        Delete("SignEventOutbox", "DocumentId", "Documents"),
        Nullify("DocumentEmailLogs", "DocumentId", "Documents"),
    };

    /// <summary>แถวลูกของการชำระ (id แม่ = Payments.Id) — ทำก่อนลบ <c>Payments</c></summary>
    public static readonly IReadOnlyList<Step> PaymentSteps = new[]
    {
        Delete("PaymentAllocations", "PaymentId", "Payments"),
        Nullify("Cheques", "PaymentId", "Payments"),
    };

    /// <summary>ตาราง/คอลัมน์ที่ขั้นตอนลบถาวรจัดการเองนอกชุดนี้ (มีตรรกะเฉพาะใน <c>PurgeDocumentAsync</c>: บรรทัดเอกสาร · การชำระ · e-Tax · 50 ทวิ ·
    /// สินทรัพย์ · สแกน) — ให้เทสต์ความครบถือว่า "จัดการแล้ว"</summary>
    public static readonly IReadOnlyList<(string Table, string Column)> HandledElsewhere = new[]
    {
        ("DocumentLines", "DocumentId"),
        ("Payments", "DocumentId"),
        ("EtaxInvoices", "DocumentId"),
        ("WithholdingTaxCerts", "DocumentId"),
        ("FixedAssets", "SourceDocumentId"),
        ("OcrScanResults", "CreatedDocumentId"),
        ("StockMovements", "DocumentId"),
    };

    /// <summary>true = การอ้าง (ตาราง, คอลัมน์) → ตารางแม่นี้ ถูกจัดการตอนลบถาวรแล้ว (ชุดนี้หรือขั้นเฉพาะ) · ใช้โดยเทสต์ความครบเทียบ pg_constraint</summary>
    internal static bool Covers(string table, string column, string referencedTable)
    {
        bool Match(Step s) => s.Table == table && s.Column == column && s.ReferencedTable == referencedTable;
        if (DocumentSteps.Any(Match) || PaymentSteps.Any(Match)) return true;
        // ลายเซ็น → ขั้นอนุมัติ: ขั้นแรกของ DocumentSteps ลบด้วยเงื่อนไข DocumentApprovalId ด้วย
        if (table == "DocumentSignatures" && column == "DocumentApprovalId" && referencedTable == "DocumentApprovals") return true;
        return referencedTable == "Documents" && HandledElsewhere.Contains((table, column));
    }

    /// <summary>รันขั้นของเอกสาร (ตามลำดับ) — ผู้เรียกถือธุรกรรมอยู่แล้ว · <paramref name="documentIds"/> ยืนยันบริษัทแล้ว</summary>
    public static Task DeleteDocumentChildrenAsync(AccountingDbContext db, IReadOnlyCollection<Guid> documentIds)
        => RunAsync(db, DocumentSteps, documentIds);

    /// <summary>รันขั้นของการชำระ (ตามลำดับ) — ก่อนลบแถว <c>Payments</c> · <paramref name="paymentIds"/> ยืนยันบริษัทแล้ว</summary>
    public static Task DeletePaymentChildrenAsync(AccountingDbContext db, IReadOnlyCollection<Guid> paymentIds)
        => RunAsync(db, PaymentSteps, paymentIds);

    private static async Task RunAsync(AccountingDbContext db, IReadOnlyList<Step> steps, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return;
        var arr = ids.Distinct().ToArray();
        // บางตารางมีเฉพาะฐานที่สร้างจากโมเดล (EnsureCreated) — ฐาน production ที่โตด้วย DatabaseMigrationHelper อาจไม่มี (เช่น ใบทวงหนี้)
        // ⇒ ข้ามตารางที่ไม่มีจริง (ไม่มีตาราง = ไม่มีแถวที่อ้าง) · ตารางที่มีแต่คำสั่งล้ม = ล้มดังทั้งธุรกรรม
        var names = steps.Select(s => s.Table).Distinct().ToArray();
        var existing = (await db.Database.SqlQueryRaw<string>(
                "SELECT c.relname AS \"Value\" FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "WHERE c.relkind IN ('r','p') AND n.nspname = current_schema() AND c.relname = ANY({0})",
                new object[] { names })
            .ToListAsync()).ToHashSet(StringComparer.Ordinal);
        foreach (var s in steps)
        {
            if (!existing.Contains(s.Table)) continue;
            await db.Database.ExecuteSqlRawAsync(s.Sql, new object[] { arr });
        }
    }
}
