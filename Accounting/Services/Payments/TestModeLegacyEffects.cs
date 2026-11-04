using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using EffectKind = Accounting.Helpers.TestModeLegacyEffectPolicy.EffectKind;
using TestEvidence = Accounting.Helpers.TestModeLegacyEffectPolicy.TestEvidence;

namespace Accounting.Services.Payments;

/// <summary>ผลทางบัญชีหนึ่งชิ้นของ intent โหมดทดสอบ — <see cref="PaymentId"/> มีค่าเฉพาะชนิดที่กลับรายการในหน้านี้ได้</summary>
public sealed record TestModeLegacyEffect(EffectKind Kind, string Label, string? Number, string? Href, Guid? PaymentId, Guid? DocumentId,
    bool CanReverseHere, string Hint);

/// <summary>หนึ่งแถวของรายงาน</summary>
public sealed record TestModeLegacyRow(Guid IntentId, Guid CompanyId, PaymentSourceKind SourceKind, Guid SourceId, decimal Amount,
    PaymentIntentStatus Status, DateTime? ConfirmedAt, DateTime CreatedAt, TestEvidence Evidence, IReadOnlyList<TestModeLegacyEffect> Effects);

/// <summary>รายงาน + ตัวบอกว่าถูกตัด (ฝ่ายค้าน S5)</summary>
public sealed record TestModeLegacyReport(IReadOnlyList<TestModeLegacyRow> Rows, int ScannedCandidates, int TotalCandidates, bool Truncated);

/// <summary>
/// รอบ 235 (คำตัดสิน 235-X3 + ฝ่ายค้าน B1/S5) — ผู้โหลด "เงินโหมดทดสอบที่ลงบัญชีไปแล้ว" (ก่อนด่าน <see cref="TestModePaymentPolicy"/> รอบ 232) · กติกาคัดกรอง/หลักฐาน
/// อยู่ที่ <see cref="TestModeLegacyEffectPolicy"/> (pure) · โหมดทดสอบตัดสินด้วย <see cref="PaymentIntentTestMode.Decide"/> (แกนเดียวกับ <c>ResolveAsync</c> —
/// โหลดโหมดของช่องทางเป็นชุด ไม่ถามทีละแถว) · ทุก query กรอง <c>CompanyId</c> ของ intent (PosPayment ไม่มี CompanyId ⇒ กรองผ่านบิล) · อ่านอย่างเดียว
///
/// <para>"ผล" = บันทึกที่ handler ของระบบประทับด้วยเลขอ้างอิงของเงินก้อนนี้ (<see cref="GatewayPaymentRecord.Reference"/> /
/// <see cref="GatewayPaymentRecord.ReservationReference"/>) หรือผูก <c>PaymentIntentId</c> ตรง — บันทึกมือของพนักงานไม่นับ · โหลดเป็นชุดต่อชนิด (ไม่ N+1)</para>
/// </summary>
public static class TestModeLegacyEffects
{
    private sealed record Candidate(PaymentIntent Intent, TestEvidence Evidence);

    /// <summary>กรองฝั่ง SQL ก่อน Take: ต้นทางร้าน · ได้เงิน · สร้างก่อน <see cref="TestModeLegacyEffectPolicy.GuardSinceUtc"/> · <c>IsTestMode != false</c> ·
    /// (ประทับ true หรือช่องทางตอนนี้เป็น Test หรือเลขอ้างอิง <c>chrg_test_</c>) — แล้วยืนยันด้วยตัวตัดสินเดียว</summary>
    private static async Task<(List<Candidate> Items, int Total)> CandidatesAsync(AccountingDbContext db, Guid? companyId, IReadOnlyCollection<Guid>? onlyIntentIds, CancellationToken ct)
    {
        // ชุดต้นทางร้านจากตัวตั้งเดียว (ไม่สำเนารายชื่อชนิด)
        var storeKinds = Enum.GetValues<PaymentSourceKind>().Where(TestModeLegacyEffectPolicy.IsStoreSource).ToArray();
        var settled = Enum.GetValues<PaymentIntentStatus>().Where(PaymentIntentPolicy.IsSettledPositive).ToArray();
        var cutoff = TestModeLegacyEffectPolicy.GuardSinceUtc;
        var configs = db.PaymentProviderConfigs.AsNoTracking().IgnoreQueryFilters();
        var modes = await (companyId is Guid c0 ? configs.Where(x => x.CompanyId == c0) : configs)
            .Select(x => new { x.Id, x.Mode }).ToDictionaryAsync(x => x.Id, x => x.Mode, ct);
        var testConfigIds = modes.Where(kv => kv.Value == PaymentProviderMode.Test).Select(kv => kv.Key).ToList();

        var q = db.PaymentIntents.AsNoTracking().IgnoreQueryFilters().Where(i => !i.IsDeleted);
        if (companyId is Guid cid) q = q.Where(i => i.CompanyId == cid);
        if (onlyIntentIds != null) q = q.Where(i => onlyIntentIds.Contains(i.Id));
        q = q.Where(i => storeKinds.Contains(i.SourceKind) && settled.Contains(i.Status) && i.CreatedAt < cutoff && i.IsTestMode != false
            && (i.IsTestMode == true
                || (i.ProviderConfigId != null && testConfigIds.Contains(i.ProviderConfigId.Value))
                || (i.ProviderRef != null && i.ProviderRef.StartsWith(TestModeLegacyEffectPolicy.OmiseTestChargePrefix))));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(i => i.CreatedAt).Take(TestModeLegacyEffectPolicy.MaxRows).ToListAsync(ct);

        var result = new List<Candidate>(rows.Count);
        foreach (var i in rows)
        {
            PaymentProviderMode? mode = i.ProviderConfigId is Guid pc && modes.TryGetValue(pc, out var m) ? m : null;
            var resolved = PaymentIntentTestMode.Decide(i.IsTestMode, mode);
            var evidence = TestModeLegacyEffectPolicy.Evidence(i.IsTestMode, resolved, i.ProviderCode, i.ProviderRef);
            if (TestModeLegacyEffectPolicy.IsCandidate(i.SourceKind, i.Status, evidence, i.CreatedAt)) result.Add(new Candidate(i, evidence));
        }
        return (result, total);
    }

    /// <summary>ผลของ intent เป็นชุด — หนึ่ง query ต่อชนิดต้นทาง (ไม่ N+1)</summary>
    private static async Task<Dictionary<Guid, List<TestModeLegacyEffect>>> EffectsAsync(AccountingDbContext db, IReadOnlyList<Candidate> cands, CancellationToken ct)
    {
        var map = cands.ToDictionary(c => c.Intent.Id, _ => new List<TestModeLegacyEffect>());
        TestModeLegacyEffect E(TestEvidence ev, EffectKind k, string? number, string? href, Guid? paymentId = null, Guid? docId = null)
        {
            var can = TestModeLegacyEffectPolicy.CanReverseHere(k, ev) && paymentId != null;
            return new(k, TestModeLegacyEffectPolicy.Label(k), number, href, can ? paymentId : null, docId, can, TestModeLegacyEffectPolicy.ReverseHint(k, ev));
        }
        List<Candidate> Of(PaymentSourceKind k) => cands.Where(c => c.Intent.SourceKind == k).ToList();

        // ── เอกสาร: Payment ที่ผูก intent หรือถือเลขอ้างอิงของ intent ──
        var docs = Of(PaymentSourceKind.Document);
        if (docs.Count > 0)
        {
            var ids = docs.Select(c => c.Intent.Id).ToList();
            var refs = docs.ToDictionary(c => GatewayPaymentRecord.Reference(c.Intent.Id), c => c);
            var refKeys = refs.Keys.ToList();
            var companies = docs.Select(c => c.Intent.CompanyId).Distinct().ToList();
            var pays = await db.Payments.AsNoTracking().IgnoreQueryFilters()
                .Where(p => companies.Contains(p.CompanyId) && !p.IsDeleted
                    && ((p.PaymentIntentId != null && ids.Contains(p.PaymentIntentId.Value)) || (p.Reference != null && refKeys.Contains(p.Reference))))
                .Select(p => new { p.Id, p.CompanyId, p.PaymentNumber, p.DocumentId, p.PaymentIntentId, p.Reference, DocNo = p.Document.DocumentNumber })
                .ToListAsync(ct);
            foreach (var p in pays)
            {
                var c = (p.PaymentIntentId is Guid pi ? docs.FirstOrDefault(x => x.Intent.Id == pi) : null)
                        ?? (p.Reference != null && refs.TryGetValue(p.Reference, out var byRef) ? byRef : null);
                if (c == null || c.Intent.CompanyId != p.CompanyId) continue;   // tenant
                map[c.Intent.Id].Add(E(c.Evidence, EffectKind.DocumentPayment, $"{p.PaymentNumber} · {p.DocNo}",
                    "/pages/documents.html?openDoc=" + p.DocumentId.ToString("D"), p.Id, p.DocumentId));
            }
        }

        // ── คำสั่งซื้อหน้าร้าน: แถวรับเงินสำเร็จที่ถือเลขอ้างอิงของ intent ──
        var orders = Of(PaymentSourceKind.SiteOrder);
        if (orders.Count > 0)
        {
            var refs = orders.ToDictionary(c => GatewayPaymentRecord.Reference(c.Intent.Id), c => c);
            var refKeys = refs.Keys.ToList();
            var rows = await db.SiteOrderPayments.AsNoTracking().IgnoreQueryFilters()
                .Where(p => !p.IsDeleted && p.Status == SitePaymentStatus.Completed && p.Reference != null && refKeys.Contains(p.Reference))
                .Select(p => new { p.CompanyId, p.OrderId, p.Reference, OrderNo = p.Order.OrderNumber })
                .ToListAsync(ct);
            foreach (var p in rows)
                if (refs.TryGetValue(p.Reference!, out var c) && c.Intent.CompanyId == p.CompanyId && c.Intent.SourceId == p.OrderId)
                    map[c.Intent.Id].Add(E(c.Evidence, EffectKind.SiteOrderPayment, p.OrderNo, "/pages/cms-orders.html"));
        }

        // ── POS: PosPayment ผูก intent (ไม่มี CompanyId — กรองผ่านบิล) ──
        var pos = Of(PaymentSourceKind.PosOrder);
        if (pos.Count > 0)
        {
            var ids = pos.Select(c => c.Intent.Id).ToList();
            var rows = await db.PosPayments.AsNoTracking().IgnoreQueryFilters()
                .Where(p => p.PaymentIntentId != null && ids.Contains(p.PaymentIntentId.Value) && !p.IsDeleted)
                .Select(p => new { p.OrderId, p.PaymentIntentId, CompanyId = p.Order.CompanyId, OrderNo = p.Order.OrderNumber })
                .ToListAsync(ct);
            foreach (var p in rows)
            {
                var c = pos.First(x => x.Intent.Id == p.PaymentIntentId);
                if (c.Intent.CompanyId != p.CompanyId) continue;
                map[c.Intent.Id].Add(E(c.Evidence, EffectKind.PosPayment, p.OrderNo, "/pages/pos-reports.html?orderId=" + p.OrderId.ToString("D")));
            }
        }

        // ── ที่พัก: การจองที่ถือเลขอ้างอิงของ intent ──
        var lodg = Of(PaymentSourceKind.LodgingReservation);
        if (lodg.Count > 0)
        {
            var srcIds = lodg.Select(c => c.Intent.SourceId).Distinct().ToList();
            var rows = await db.LodgingReservations.AsNoTracking().IgnoreQueryFilters()
                .Where(x => srcIds.Contains(x.Id) && !x.IsDeleted)
                .Select(x => new { x.Id, x.CompanyId, x.ReservationNumber, x.PaymentReference })
                .ToListAsync(ct);
            foreach (var c in lodg)
            {
                var r = rows.FirstOrDefault(x => x.Id == c.Intent.SourceId && x.CompanyId == c.Intent.CompanyId);
                if (r == null) continue;
                if (r.PaymentReference == GatewayPaymentRecord.ReservationReference(c.Intent.Id, c.Intent.ProviderRef)
                    || r.PaymentReference == GatewayPaymentRecord.Reference(c.Intent.Id))
                    map[c.Intent.Id].Add(E(c.Evidence, EffectKind.LodgingDeposit, r.ReservationNumber, "/pages/lodging.html"));
            }
        }

        // ── การจองนัดหมาย: เลขอ้างอิงบนการจอง หรือแถวรับเงินรายก้อน ──
        var book = Of(PaymentSourceKind.SiteBooking);
        if (book.Count > 0)
        {
            var srcIds = book.Select(c => c.Intent.SourceId).Distinct().ToList();
            var refKeys = book.Select(c => GatewayPaymentRecord.Reference(c.Intent.Id)).ToList();
            var bookings = await db.SiteBookings.AsNoTracking().IgnoreQueryFilters()
                .Where(x => srcIds.Contains(x.Id) && !x.IsDeleted)
                .Select(x => new { x.Id, x.CompanyId, x.BookingNumber, x.PaymentReference })
                .ToListAsync(ct);
            var pays = await db.SiteBookingPayments.AsNoTracking().IgnoreQueryFilters()
                .Where(p => srcIds.Contains(p.BookingId) && !p.IsDeleted && !p.IsRefund && p.Status == SitePaymentStatus.Completed
                    && p.Reference != null && refKeys.Contains(p.Reference))
                .Select(p => new { p.BookingId, p.CompanyId, p.Reference })
                .ToListAsync(ct);
            foreach (var c in book)
            {
                var b = bookings.FirstOrDefault(x => x.Id == c.Intent.SourceId && x.CompanyId == c.Intent.CompanyId);
                if (b == null) continue;
                var reference = GatewayPaymentRecord.Reference(c.Intent.Id);
                if (b.PaymentReference == reference
                    || pays.Any(p => p.BookingId == b.Id && p.CompanyId == c.Intent.CompanyId && p.Reference == reference))
                    map[c.Intent.Id].Add(E(c.Evidence, EffectKind.BookingDeposit, b.BookingNumber, "/pages/cms-bookings.html"));
            }
        }
        return map;
    }

    private static async Task<TestModeLegacyReport> ReportAsync(AccountingDbContext db, Guid? companyId, IReadOnlyCollection<Guid>? onlyIntentIds, CancellationToken ct)
    {
        var (cands, total) = await CandidatesAsync(db, companyId, onlyIntentIds, ct);
        var effects = await EffectsAsync(db, cands, ct);
        var rows = new List<TestModeLegacyRow>();
        foreach (var c in cands)
        {
            var e = effects[c.Intent.Id];
            // ตัวคัดกรองเดียว
            if (!TestModeLegacyEffectPolicy.ShouldReport(c.Intent.SourceKind, c.Intent.Status, c.Evidence, c.Intent.CreatedAt, e.Count)) continue;
            var i = c.Intent;
            rows.Add(new TestModeLegacyRow(i.Id, i.CompanyId, i.SourceKind, i.SourceId, i.Amount, i.Status, i.ConfirmedAt, i.CreatedAt, c.Evidence, e));
        }
        return new TestModeLegacyReport(rows, cands.Count, total, total > TestModeLegacyEffectPolicy.MaxRows);
    }

    /// <summary>รายงานของร้านหนึ่งร้าน (หน้า "รายการรับชำระออนไลน์")</summary>
    public static Task<TestModeLegacyReport> ForCompanyAsync(AccountingDbContext db, Guid companyId, CancellationToken ct)
        => ReportAsync(db, companyId, null, ct);

    /// <summary>ประเมิน intent เดียวด้วยเส้นเดียวกับรายงาน (ปุ่มกลับรายการตัดสินซ้ำฝั่งเซิร์ฟเวอร์) — null = ไม่เข้าข่าย/ไม่มีผล</summary>
    public static async Task<TestModeLegacyRow?> EvaluateAsync(AccountingDbContext db, Guid companyId, Guid intentId, CancellationToken ct)
        => (await ReportAsync(db, companyId, new[] { intentId }, ct)).Rows.FirstOrDefault();

    /// <summary>แอดมินแพลตฟอร์ม (อ่านอย่างเดียว): จำนวนร้าน + รายการที่ค้าง ข้ามทุก tenant · ถูกตัดไหม</summary>
    public static async Task<(int Companies, int Intents, int Proven, bool Truncated)> CountAllAsync(AccountingDbContext db, CancellationToken ct)
    {
        var r = await ReportAsync(db, null, null, ct);
        return (r.Rows.Select(x => x.CompanyId).Distinct().Count(), r.Rows.Count,
            r.Rows.Count(x => TestModeLegacyEffectPolicy.IsProven(x.Evidence)), r.Truncated);
    }
}
