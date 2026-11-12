using System.Linq.Expressions;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **สลิปจากพอร์ทัลลูกค้า = "แจ้งโอน" ไม่ใช่ "รับชำระแล้ว"** — ตัวตั้งตัวเดียว (รอบ 250 WP-02b · AUDIT_249_COVERAGE M-2 · AUTOPILOT_BILLING_DESIGN G-T7)
///
/// <para>═══ ที่มา ═══ <c>PortalService.UploadDocumentSlipAsync</c> เคยเพิ่มแถว <c>Payment</c> จริงทันทีพร้อม
/// <c>Notes="[PENDING-VERIFY]…"</c> (ไม่มี JE · ไม่แตะ <c>PaidAmount</c> · ไม่มีขั้นยืนยันในระบบเลย) แต่ผู้อ่านแถว Payment นับเป็นเงินรับ:
/// ใบแจ้งยอดพอร์ทัล · ใบแจ้งยอดลูกหนี้ (<c>AdvancedArApService</c>) · "การชำระของฉัน" · จับคู่ธนาคารอัตโนมัติ (<c>BankService</c>) ⇒
/// อัปสลิปปลอม = statement ขึ้นว่าชำระแล้ว + จับคู่กับเงินเข้าจริงยอดเดียวกันได้ · เจ้าของบันทึกรับชำระจริงอีกครั้ง = ลบยอดซ้ำ</para>
///
/// <para>ตอนนี้: สลิปใหม่ ⇒ <c>PaymentIntent(ManualSlip)</c> สถานะรอ (ผ่าน <c>ManualSlipPaymentProvider</c> เดิม) · พนักงานยืนยันที่หน้ารายการรับชำระ
/// (<c>PaymentGatewayController.ConfirmManually</c>) ⇒ <c>DocumentPaymentHandler</c> ⇒ <c>CreatePaymentAsync</c> ตัวเดียว (JE · ตัดหนี้ · ใบเสร็จ) ·
/// แถวเก่า ⇒ <see cref="PortalSlipLegacyMigration"/> (ย้ายเป็นรายการรอยืนยัน — ไม่ลบเงิน) · แถวที่ยังเหลือ (ต้องให้คนตัดสิน) ถูกตัดออกจาก
/// ใบแจ้งยอด/จับคู่ธนาคารด้วย <see cref="CountsAsReceived"/></para>
/// </summary>
public static class PortalSlipPending
{
    /// <summary>ป้ายของแถว Payment รุ่นเก่า (ก่อนรอบ 250) — ห้ามเขียนป้ายนี้อีก (required_call_site_check ล็อก <c>Payments.Add(</c> ใน PortalService = 0)</summary>
    public const string LegacyMarker = "[PENDING-VERIFY]";

    /// <summary>ผู้สร้างแถวรุ่นเก่า (<c>Payment.CreatedBy</c>)</summary>
    public const string LegacyCreatedBy = "portal-customer";

    public const string RuleCode = "PORTAL-SLIP-PENDING";

    /// <summary>แถว Payment นี้เป็น "เงินรับจริง" ไหม — แถวสลิปพอร์ทัลรุ่นเก่าที่ยังไม่ถูกยืนยัน (ไม่ผูก intent · ผู้สร้าง portal-customer ·
    /// หมายเหตุขึ้นต้นป้าย) = ไม่ใช่ · ฝ่ายค้าน L1: ต้องตรงผู้สร้างด้วย — หมายเหตุที่ผู้ใช้พิมพ์ขึ้นต้นป้ายเองไม่ทำให้เงินจริงหายจากยอด ·
    /// ใช้ใน <c>Where(...)</c> ของทุกผู้อ่านที่รวมยอดรับชำระ/จับคู่ธนาคาร (EF แปลเป็น SQL ได้) · ผู้ยกเลิกการชำระใช้ตัวเดียวกัน (ไม่ถอย PaidAmount ที่ไม่เคยบวก)</summary>
    public static readonly Expression<Func<Payment, bool>> CountsAsReceived =
        p => p.PaymentIntentId != null || p.Notes == null || !p.Notes.StartsWith(LegacyMarker)
             || p.CreatedBy == null || p.CreatedBy != LegacyCreatedBy;

    /// <summary>กลับด้านของ <see cref="CountsAsReceived"/> (สร้างจากตัวเดียวกัน — ไม่มีสำเนาเงื่อนไข) — แถวสลิปรุ่นเก่าที่ยังไม่ตรวจ ·
    /// ผู้ใช้: รายงานตรวจข้อมูล (การ์ด "ต้องให้คนตัดสิน" · ฝ่ายค้าน M3)</summary>
    public static readonly Expression<Func<Payment, bool>> UnverifiedLegacy =
        Expression.Lambda<Func<Payment, bool>>(Expression.Not(CountsAsReceived.Body), CountsAsReceived.Parameters);

    /// <summary>รูปเดียวกับ <see cref="CountsAsReceived"/> สำหรับแถวที่โหลดแล้ว (ผู้ยกเลิกการชำระ — ฝ่ายค้าน M3)</summary>
    private static readonly Func<Payment, bool> CountsCompiled = CountsAsReceived.Compile();

    /// <summary>แถวนี้เคยถูกนับเป็นเงินรับ (บวก PaidAmount/ลง JE) ไหม — false = แถวสลิปรุ่นเก่าที่ยังไม่ตรวจ ⇒ ยกเลิกแล้วห้ามถอยยอดเอกสาร</summary>
    public static bool WasCountedAsReceived(Payment p) => CountsCompiled(p);

    /// <summary>คำอธิบายบนรายการรอยืนยัน (หน้ารายการรับชำระของพนักงาน) — เลขเอกสาร + ที่อยู่ไฟล์สลิป</summary>
    public static string IntentDescription(string documentNumber, string slipUrl)
        => IntentDescription(documentNumber, slipUrl, fromPayLink: false);

    /// <summary>รอบ 255 WP-12B (ฝ่ายค้าน LOW): ป้ายช่องทางตามที่มาจริง — หน้าชำระของเอกสาร ≠ พอร์ทัล · สลิปจากหน้าชำระไม่ได้เสิร์ฟตรง ⇒ บอกให้เปิดจากไฟล์แนบของเอกสาร</summary>
    public static string IntentDescription(string documentNumber, string slipUrl, bool fromPayLink)
        => fromPayLink
            ? $"สลิปจากหน้าชำระของเอกสาร (รอตรวจ) — {documentNumber} · ไฟล์: แท็บไฟล์แนบของเอกสาร"
            : $"สลิปจากพอร์ทัลลูกค้า (รอตรวจ) — {documentNumber} · ไฟล์: {slipUrl}";

    /// <summary>รอบ 260: สลิปที่ลูกค้าส่งในแชต LINE OA ของบริษัท (ไฟล์ไม่เสิร์ฟตรง — เปิดจากแท็บไฟล์แนบของเอกสาร)</summary>
    public static string LineChatIntentDescription(string documentNumber)
        => $"สลิปจากแชต LINE ของลูกค้า (รอตรวจ) — {documentNumber} · ไฟล์: แท็บไฟล์แนบของเอกสาร";
}

/// <summary>
/// **ย้ายแถว Payment "[PENDING-VERIFY]" รุ่นเก่า (สลิปพอร์ทัลก่อนรอบ 250) เป็นรายการรับชำระรอยืนยัน — ครั้งเดียว** (F2 ข้อ 9 · รอบ 250 WP-02b M-2)
///
/// <para>ต่อแถว (เฉพาะ <c>Notes</c> ขึ้นต้น <see cref="PortalSlipPending.LegacyMarker"/> + <c>CreatedBy = portal-customer</c> + ยังไม่ลบ):
/// <list type="bullet">
/// <item><b>ย้ายได้</b> (ไม่ผูก intent/ใบเสร็จ · ไม่มี JE อ้างเลขที่การชำระ · ไม่ถูกจับคู่กับรายการธนาคาร · เอกสารยังอยู่) ⇒ สร้าง <c>PaymentIntent(manual-slip)</c>
/// สถานะ Pending ยอดเดิม (พนักงานยืนยันแล้วจึงเดิน <c>CreatePaymentAsync</c>) + ประทับแถวเดิม <c>IsDeleted=true</c> (soft — แถวยังอยู่ในฐาน · ไม่เคยเป็นเงินรับ:
/// ไม่มี JE ไม่แตะ PaidAmount) + หมายเหตุอ้าง intent · audit ต่อแถว</item>
/// <item><b>ต้องให้คนตัดสิน</b> (ผูกกับรายการธนาคาร/JE/ใบเสร็จแล้ว) ⇒ <b>ไม่แตะ</b> · audit "ต้องตรวจ" พร้อมเหตุ · ผู้อ่านยอดตัดแถวนี้ออกด้วย
/// <see cref="PortalSlipPending.CountsAsReceived"/> จนกว่าคนจะแก้</item>
/// </list>
/// ไม่ลบจริง ไม่แก้ยอดเอกสาร ไม่แก้สถานะจับคู่ธนาคาร · marker <see cref="MarkerKey"/> + <see cref="AdvisoryLockKey.OneShotDataFix"/> ⇒ หลายเครื่องบูตพร้อมกันทำครั้งเดียว ·
/// ล้ม ⇒ ธุรกรรมย้อน + ไม่มี marker + ล้าง ChangeTracker ⇒ บูตถัดไปทำใหม่</para>
/// </summary>
public static class PortalSlipLegacyMigration
{
    public const string MarkerKey = "r250-portal-slip-pending-to-intent";
    public const string Actor = "migration:r250-portal-slip";
    public const string AuditEntityType = "Payment";

    /// <summary>ตัวกรอง "เงินรับจริง" ตัวเดียวกับผู้อ่านยอด (อ้างผ่านตัวแปรเพื่อใช้ใน Where ของ migration)</summary>
    private static System.Linq.Expressions.Expression<Func<Payment, bool>> CountsAsReceivedForMigration => PortalSlipPending.CountsAsReceived;

    public sealed record Result(int Moved, int NeedsReview);

    public static async Task<Result> MigrateAsync(AccountingDbContext db, CancellationToken ct = default)
    {
        try
        {
            return await MigrateCoreAsync(db, ct);
        }
        finally
        {
            // context ตอนบูตใช้ต่อ — แถวที่ค้าง (ล้มกลางทาง) ห้ามหลุดลงฐานกับ SaveChanges ครั้งถัดไป
            db.ChangeTracker.Clear();
        }
    }

    private static async Task<Result> MigrateCoreAsync(AccountingDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(AdvisoryLockKey.OneShotDataFix, MarkerKey) }, ct);
        // ตาราง marker จากตัวตั้งเดียว (รอบ 250 WP-04) — เส้นนี้อาจถูกเรียกก่อน ApplyMissingColumns ในเทสต์ จึงสร้างซ้ำได้ (idempotent)
        await db.Database.ExecuteSqlRawAsync(Accounting.Data.DatabaseMigrationHelper.OneShotDataFixesTableSql, ct);
        var markerKey = MarkerKey;   // ตัวแปร ⇒ เป็นพารามิเตอร์ของ FormattableString
        var done = await db.Database
            .SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "OneShotDataFixes" WHERE "Key" = {markerKey}""")
            .SingleAsync(ct);
        if (done > 0)
        {
            await tx.CommitAsync(ct);
            return new Result(0, 0);
        }

        var marker = PortalSlipPending.LegacyMarker;
        var legacy = await db.Payments.IgnoreQueryFilters()
            .Where(p => !p.IsDeleted && p.CreatedBy == PortalSlipPending.LegacyCreatedBy
                && p.Notes != null && p.Notes.StartsWith(marker))
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        int moved = 0, review = 0;
        foreach (var p in legacy)
        {
            var cid = p.CompanyId;
            var reasons = new List<string>();
            if (p.PaymentIntentId != null) reasons.Add("ผูกรายการชำระออนไลน์แล้ว");
            if (p.ReceiptDocumentId != null) reasons.Add("ออกใบเสร็จแล้ว");
            if (await db.JournalEntries.IgnoreQueryFilters().AnyAsync(j => j.CompanyId == cid && !j.IsDeleted && j.Reference == p.PaymentNumber, ct))
                reasons.Add("มีรายการบัญชีอ้างเลขที่การชำระนี้");
            if (await db.BankTransactions.IgnoreQueryFilters().AnyAsync(t => t.CompanyId == cid && !t.IsDeleted && t.MatchedPaymentId == p.Id, ct))
                reasons.Add("ถูกจับคู่กับรายการเดินบัญชีธนาคารแล้ว");
            // ฝ่ายค้าน M1: ทุกที่ที่อ้างแถว Payment ได้ — มีผู้อ้าง = มีคนใช้แถวนี้ไปแล้ว ห้ามย้ายเอง
            var pid = p.Id;
            if (await db.ReconciliationGroupItems.IgnoreQueryFilters().AnyAsync(r => !r.IsDeleted && r.ItemType == ReconciliationItemType.Payment && r.ItemId == pid, ct))
                reasons.Add("อยู่ในกลุ่มกระทบยอดธนาคาร");
            if (await db.PaymentAllocations.IgnoreQueryFilters().AnyAsync(a => a.CompanyId == cid && !a.IsDeleted && a.PaymentId == pid, ct))
                reasons.Add("มีการจัดสรรหลายใบ");
            if (await db.WhtCreditsReceived.IgnoreQueryFilters().AnyAsync(w => w.CompanyId == cid && !w.IsDeleted && w.PaymentId == pid, ct))
                reasons.Add("ผูกทะเบียนภาษีถูกหัก ณ ที่จ่าย");
            if (await db.WithholdingTaxCerts.IgnoreQueryFilters().AnyAsync(w => w.CompanyId == cid && !w.IsDeleted && w.SourcePaymentId == pid, ct))
                reasons.Add("ผูกหนังสือรับรอง 50 ทวิ");
            if (await db.Cheques.IgnoreQueryFilters().AnyAsync(c => c.CompanyId == cid && !c.IsDeleted && c.PaymentId == pid, ct))
                reasons.Add("ผูกเช็ค");
            if (await db.Documents.IgnoreQueryFilters().AnyAsync(d => d.CompanyId == cid && !d.IsDeleted && d.SettlementPaymentId == pid, ct))
                reasons.Add("เป็นการชำระของใบเสร็จรับเงิน");
            var doc = await db.Documents.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.Id == p.DocumentId && d.CompanyId == cid)
                .Select(d => new { d.Id, d.DocumentNumber, d.ContactId, d.IsDeleted, d.BalanceDue })
                .FirstOrDefaultAsync(ct);
            if (doc == null || doc.IsDeleted) reasons.Add("เอกสารต้นทางไม่อยู่แล้ว");
            else
            {
                // ฝ่ายค้าน H1: ส่วนใหญ่ร้านบันทึกรับเงินจริงแยกไปแล้ว — มีการรับชำระจริง (นับเป็นเงินรับ · ไม่ลบ) ตั้งแต่วันแนบสลิป หรือยอดค้างน้อยกว่ายอดสลิป
                // ⇒ ยืนยันสลิปนี้ = รับเงินซ้ำ ⇒ ให้คนตัดสิน (ไม่สร้างรายการรอยืนยัน)
                var slipDate = p.PaymentDate;
                if (await db.Payments.Where(CountsAsReceivedForMigration)
                        .AnyAsync(x => x.CompanyId == cid && x.DocumentId == doc.Id && x.Id != pid && x.PaymentDate >= slipDate, ct))
                    reasons.Add("มีการรับชำระจริงของเอกสารนี้ตั้งแต่วันแนบสลิป (อาจเป็นเงินก้อนเดียวกัน)");
                if (DocumentSlipIntents.ExceedsBalance(p.Amount, doc.BalanceDue))
                    reasons.Add($"ยอดค้างของเอกสาร ({doc.BalanceDue:#,##0.00}) น้อยกว่ายอดสลิป");
            }

            if (reasons.Count > 0)
            {
                review++;
                db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = cid, UserEmail = Actor, Action = AuditAction.Update,
                    EntityType = AuditEntityType, EntityId = p.Id.ToString(),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        @event = "PortalSlipLegacyNeedsReview",
                        paymentNumber = p.PaymentNumber, amount = p.Amount, documentId = p.DocumentId,
                        reasons,
                        note = "รอบ 250: แถวสลิปพอร์ทัลรุ่นเก่า (ยังไม่ผ่านการตรวจ) ที่ระบบไม่ย้ายเอง — ไม่นับในใบแจ้งยอด/จับคู่ธนาคาร จนกว่าผู้ใช้ตรวจและแก้ด้วยมือ",
                        ruleCode = PortalSlipPending.RuleCode,
                    }),
                    Timestamp = now,
                });
                continue;
            }

            var intent = new PaymentIntent
            {
                CompanyId = cid,
                ProviderCode = Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code,
                SourceKind = PaymentSourceKind.Document,
                SourceId = doc!.Id,
                ContactId = doc.ContactId,
                Amount = p.Amount,
                // ฝ่ายค้าน M6: ที่อยู่ไฟล์สลิปจากหมายเหตุเดิม (ไฟล์แนบของเอกสารยังอยู่ — แถว FileAttachment เดิมไม่ถูกแตะ)
                Description = $"สลิปจากพอร์ทัลลูกค้า (รอตรวจ · ย้ายจากรายการ {p.PaymentNumber} รุ่นเก่า) — {doc.DocumentNumber}"
                    + (DocumentSlipIntents.SlipUrlFromLegacyNotes(p.Notes) is string slipUrl ? $" · ไฟล์: {slipUrl}" : " · ไฟล์: ดูไฟล์แนบของเอกสาร"),
                MethodKind = PaymentMethodKind.ManualSlip,
                Status = PaymentIntentStatus.Pending,
                ProviderStatusRaw = "awaiting_slip",
                AttemptCount = 1,
                IdempotencyKey = $"legacy-portal-slip:{p.Id:N}",
                CreatedAt = p.CreatedAt,
                CreatedBy = Actor,
            };
            intent.ProviderRef = $"slip:{intent.Id:N}";
            db.PaymentIntents.Add(intent);
            db.PaymentIntentEvents.Add(new PaymentIntentEvent
            {
                CompanyId = cid, IntentId = intent.Id, At = now, Source = PaymentEventSource.System,
                FromStatus = null, ToStatus = PaymentIntentStatus.Pending,
                Note = $"ย้ายจากแถวรับชำระ {p.PaymentNumber} ที่สร้างตอนลูกค้าแนบสลิปในพอร์ทัล (ยังไม่เคยตรวจ) — รอพนักงานตรวจสลิปแล้วยืนยัน",
            });

            var before = new { p.Notes, p.IsDeleted };
            p.IsDeleted = true;
            p.Notes = $"{p.Notes} · [ย้ายเป็นรายการรอยืนยัน {intent.Id:N} — รอบ 250]";
            p.UpdatedAt = now;
            p.UpdatedBy = Actor;
            moved++;
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = cid, UserEmail = Actor, Action = AuditAction.Update,
                EntityType = AuditEntityType, EntityId = p.Id.ToString(),
                OldValues = System.Text.Json.JsonSerializer.Serialize(before),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @event = "PortalSlipLegacyMovedToIntent",
                    paymentNumber = p.PaymentNumber, amount = p.Amount, documentId = p.DocumentId,
                    paymentIntentId = intent.Id, isDeleted = true,
                    note = "รอบ 250: สลิปพอร์ทัลเคยถูกบันทึกเป็นแถวรับชำระก่อนตรวจ (ไม่มี JE · ไม่ตัดหนี้) — ย้ายเป็นรายการรอยืนยัน · ยืนยันแล้วระบบบันทึกรับชำระตามปกติ",
                    ruleCode = PortalSlipPending.RuleCode,
                }),
                Timestamp = now,
            });
        }
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            """INSERT INTO "OneShotDataFixes" ("Key", "AppliedAt") VALUES ({0}, (now() at time zone 'utc')) ON CONFLICT DO NOTHING""",
            new object[] { MarkerKey }, ct);
        await tx.CommitAsync(ct);
        return new Result(moved, review);
    }
}
