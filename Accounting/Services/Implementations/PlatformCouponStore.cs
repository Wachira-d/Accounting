using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §7.1 · คำตัดสินข้อ 230-Q2) — <b>ตัวเก็บ/นับการใช้คูปองแพลตฟอร์มตัวเดียว</b> (static · DbContext ตรง ⇒ เรียกได้จาก
/// <c>SubscriptionService</c>/<c>QuotaAddOnPurchaseService</c> โดยไม่เพิ่มวง DI) · ตัวตัดสินเงื่อนไข/ยอด = <see cref="PlatformCouponRule"/>
/// <list type="bullet">
/// <item><b>จอง</b> (<see cref="ReserveAsync"/>) ตอนสร้างรายการชำระ — ใต้ <c>pg_advisory_xact_lock</c> ต่อคูปอง ในธุรกรรมของผู้เรียก ⇒ สองคำขอพร้อมกันไม่ทะลุเพดาน ·
/// การจองนับเข้าเพดาน <see cref="PlatformCouponRule.ReservationDays"/> วัน</item>
/// <item><b>นับ</b> (<see cref="RedeemAsync"/>) เมื่อเงินเข้า — UPDATE แบบมีเงื่อนไข (Reserved/Released → Redeemed) แล้วจึง +1 <c>UsedCount</c> ⇒ webhook ซ้ำไม่นับซ้ำ ·
/// เงินเข้าหลังหมดอายุการจอง/หลังปลด = ยังให้ส่วนลด (ลูกค้าจ่ายตามยอดที่เห็นแล้ว · ไม่เก็บย้อนหลัง ข้อ 178 ②)</item>
/// <item><b>ปลด</b> (<see cref="ReleaseAsync"/>) เมื่อรายการถูกยกเลิก/ปฏิเสธ</item>
/// </list>
/// ทุกคำสั่งกรอง <c>CompanyId</c> ของผู้จ่าย (กฎ M) · คูปองเป็นตารางระดับแพลตฟอร์ม (ไม่มี tenant)
/// </summary>
public static class PlatformCouponStore
{
    public static Task<PlatformCoupon?> FindAsync(AccountingDbContext db, string? code, CancellationToken ct = default)
    {
        var c = PlatformCouponRule.Normalize(code);
        return c == null ? Task.FromResult<PlatformCoupon?>(null) : db.PlatformCoupons.FirstOrDefaultAsync(x => x.Code == c, ct);
    }

    /// <summary>จำนวนที่นับเข้าเพดาน (Redeemed + Reserved ที่ยังไม่หมดอายุการจอง) — ไม่รวม <paramref name="excludeSourceId"/></summary>
    public static async Task<(int Total, int ByCompany)> CountAsync(AccountingDbContext db, Guid couponId, Guid companyId, DateTime nowUtc,
        Guid? excludeSourceId, CancellationToken ct = default)
    {
        var q = db.PlatformCouponRedemptions.AsNoTracking()
            .Where(r => r.CouponId == couponId && (excludeSourceId == null || r.SourceId != excludeSourceId)
                && (r.Status == PlatformCouponRedemptionStatus.Redeemed
                    || (r.Status == PlatformCouponRedemptionStatus.Reserved && r.ReservedUntilUtc > nowUtc)));
        var rows = await q.Select(r => r.CompanyId).ToListAsync(ct);
        return (rows.Count, rows.Count(x => x == companyId));
    }

    /// <summary>บริษัทนี้ยังไม่เคยจ่ายค่าบริการ NextAcc (ไม่มีรายการชำระค่าแพ็กเกจที่อนุมัติแล้วยอด &gt; 0 และไม่มีโควตาเสริมที่รับเงินแล้ว)</summary>
    /// <para>ฝ่ายค้าน N3: นับส่วนเสริมสวิตช์ที่จ่ายแล้วด้วย · <paramref name="excludeSourceId"/> ≠ null ⇒ การจองคูปอง "ครั้งแรกเท่านั้น" ใบอื่นที่ยังมีผลของบริษัทนี้ = ไม่ใช่ครั้งแรก
    /// (สองคูปองครั้งแรกจองพร้อมกันไม่ได้ — ผู้เรียกถือล็อกระดับบริษัท)</para>
    public static async Task<bool> IsFirstPaymentAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default, Guid? excludeSourceId = null,
        DateTime? nowUtc = null)
    {
        var paidPlan = await db.SubscriptionPayments.AsNoTracking()
            .AnyAsync(p => p.Subscription.CompanyId == companyId && p.Status == SubscriptionPaymentStatus.Approved && p.Amount > 0m, ct);
        if (paidPlan) return false;
        if (await db.CompanyQuotaAddOns.AsNoTracking().AnyAsync(q => q.CompanyId == companyId && q.ReceivedAmount > 0m, ct)) return false;
        if (await db.CompanyFeatures.AsNoTracking().AnyAsync(f => f.CompanyId == companyId && f.PaymentStatus == AddOnPaymentStatus.Paid, ct)) return false;
        if (excludeSourceId is Guid ex)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var otherFirst = await db.PlatformCouponRedemptions.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.SourceId != ex
                    && (r.Status == PlatformCouponRedemptionStatus.Redeemed || (r.Status == PlatformCouponRedemptionStatus.Reserved && r.ReservedUntilUtc > now)))
                .Join(db.PlatformCoupons.AsNoTracking().Where(c => c.FirstPaymentOnly), r => r.CouponId, c => c.Id, (r, c) => r.Id)
                .AnyAsync(ct);
            if (otherFirst) return false;
        }
        return true;
    }

    /// <summary>ดูผลคูปอง (ไม่จอง) — หน้าเว็บแสดงยอดก่อนยืนยัน · ไม่มีรหัส = ไม่ลด</summary>
    public static async Task<PlatformCouponRule.Evaluation?> PreviewAsync(AccountingDbContext db, Guid companyId, string? code,
        PlatformCouponScope scope, string? plan, string? cycle, string? resource, decimal price, DateTime nowUtc, CancellationToken ct = default,
        bool isRenewal = false, bool renewalSpecialPrice = false)
    {
        if (PlatformCouponRule.Normalize(code) == null) return null;
        var coupon = await FindAsync(db, code, ct);
        var (total, mine) = coupon == null ? (0, 0) : await CountAsync(db, coupon.Id, companyId, nowUtc, null, ct);
        var first = coupon?.FirstPaymentOnly == true && await IsFirstPaymentAsync(db, companyId, ct);
        return PlatformCouponRule.Apply(coupon, price,
            new PlatformCouponRule.Target(scope, plan, cycle, resource, first, isRenewal, renewalSpecialPrice), nowUtc, total, mine);   // รอบ 235 K-2 · S-2
    }

    private static Task LockAsync(AccountingDbContext db, Guid couponId, CancellationToken ct)
        => LockKeyAsync(db, AdvisoryLockKey.For(AdvisoryLockKey.PlatformCoupon, couponId.ToString("N")), ct);

    private static async Task LockKeyAsync(AccountingDbContext db, long key, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("จอง/นับคูปองต้องเรียกภายในธุรกรรม (pg_advisory_xact_lock หลุดทันทีถ้าไม่มีธุรกรรม)");
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { key }, ct);
    }

    /// <summary>ฝ่ายค้าน S3: ก่อนนับเมื่อเงินเข้า — การจองหมดอายุ/ถูกปลดไปแล้ว ⇒ นับเพดานใหม่ใต้ล็อกของคูปอง (ต้องอยู่ในธุรกรรม) · null = นับได้ ·
    /// ข้อความ = เกินเพดานแล้ว (ผู้เรียก: ระบบยืนยัน ⇒ พักให้แอดมิน · แอดมินอนุมัติ = ตัดสินแล้ว นับเกินได้)</summary>
    public static async Task<string?> RedeemProblemAsync(AccountingDbContext db, Guid companyId, string sourceKind, Guid sourceId, CancellationToken ct = default)
    {
        var row = await db.PlatformCouponRedemptions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.SourceKind == sourceKind && r.SourceId == sourceId, ct);
        var now = DateTime.UtcNow;
        if (row == null || row.Status == PlatformCouponRedemptionStatus.Redeemed
            || (row.Status == PlatformCouponRedemptionStatus.Reserved && row.ReservedUntilUtc > now)) return null;   // การจองยังมีผล = นับไว้ในเพดานแล้ว
        var coupon = await db.PlatformCoupons.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.Id == row.CouponId, ct);
        if (coupon == null) return null;
        await LockAsync(db, coupon.Id, ct);
        var (total, mine) = await CountAsync(db, coupon.Id, companyId, now, sourceId, ct);
        if (coupon.MaxUsesTotal is int mt && total >= mt)
            return $"คูปอง {coupon.Code} ถูกใช้ครบ {mt:N0} ครั้งแล้วระหว่างที่รายการนี้ค้างชำระ (การจองหมดอายุ) — ตรวจแล้วอนุมัติ (นับเกิน) หรือคืนเงินส่วนต่าง";
        if (coupon.MaxUsesPerCompany is int mc && mine >= mc)
            return $"บริษัทนี้ใช้คูปอง {coupon.Code} ครบ {mc:N0} ครั้งแล้วระหว่างที่รายการนี้ค้างชำระ — ตรวจแล้วอนุมัติหรือคืนเงินส่วนต่าง";
        return null;
    }

    /// <summary>ฝ่ายค้าน S5: แอดมินแก้ยอดของรายการที่มีคูปอง — ส่วนลด = ราคาก่อนคูปอง − ยอดใหม่ (ไม่ติดลบ) · ยอดใหม่ ≥ ราคาก่อนคูปอง ⇒ ปลดคูปอง ·
    /// แก้แถวการใช้ในธุรกรรมเดียวกับรายการ (ผู้เรียก SaveChanges) · คืนส่วนลดใหม่ (0 = ไม่มีคูปองแล้ว)</summary>
    public static async Task<decimal> AdjustForOverrideAsync(AccountingDbContext db, Guid companyId, string sourceKind, Guid sourceId,
        decimal listAmount, decimal newAmount, string actor, CancellationToken ct = default)
    {
        var discount = PlatformCouponRule.R2(listAmount - newAmount);
        var row = await db.PlatformCouponRedemptions
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.SourceKind == sourceKind && r.SourceId == sourceId, ct);
        if (discount <= 0m)
        {
            if (row != null && row.Status == PlatformCouponRedemptionStatus.Reserved)
                await ReleaseAsync(db, companyId, sourceKind, sourceId, "แอดมินแก้ยอดไม่ต่ำกว่าราคาก่อนคูปอง — ไม่ใช้คูปอง", actor, ct);
            return 0m;
        }
        if (row != null)
        {
            row.DiscountAmount = discount; row.AmountAfter = PlatformCouponRule.R2(newAmount); row.ListAmount = PlatformCouponRule.R2(listAmount);
            row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = actor;
        }
        return discount;
    }

    /// <summary>จองคูปองให้รายการที่กำลังสร้าง (ผู้เรียกต้องอยู่ในธุรกรรม และ SaveChanges/commit เอง) — ใช้ไม่ได้ ⇒ <see cref="BusinessRuleException"/> 400 ข้อความไทย (ไม่สร้างรายการ)</summary>
    public static async Task<(PlatformCoupon Coupon, PlatformCouponRule.Evaluation Eval)> ReserveAsync(AccountingDbContext db, Guid companyId, string code,
        PlatformCouponScope scope, string? plan, string? cycle, string? resource, decimal price, string sourceKind, Guid sourceId, string actor,
        DateTime nowUtc, CancellationToken ct = default, bool isRenewal = false, bool renewalSpecialPrice = false)
    {
        var coupon = await FindAsync(db, code, ct)
            ?? throw new BusinessRuleException(PlatformCouponRule.InvalidCodeMessage, PlatformCouponRule.RuleCode);
        await LockAsync(db, coupon.Id, ct);
        await db.Entry(coupon).ReloadAsync(ct);   // หลังได้ล็อก — แอดมินอาจปิด/แก้เพดานระหว่างนี้
        // ฝ่ายค้าน N3: "ครั้งแรกเท่านั้น" ตัดสินใต้ล็อกระดับบริษัท (คนละคูปองก็กันกัน)
        if (coupon.FirstPaymentOnly) await LockKeyAsync(db, AdvisoryLockKey.For(companyId, AdvisoryLockKey.PlatformCoupon, "first-payment"), ct);
        var (total, mine) = await CountAsync(db, coupon.Id, companyId, nowUtc, sourceId, ct);
        var first = coupon.FirstPaymentOnly && await IsFirstPaymentAsync(db, companyId, ct, sourceId, nowUtc);
        var eval = PlatformCouponRule.Apply(coupon, price, new PlatformCouponRule.Target(scope, plan, cycle, resource, first, isRenewal, renewalSpecialPrice), nowUtc, total, mine);   // รอบ 235 K-2 · S-2
        if (!eval.Ok) throw new BusinessRuleException(eval.Problem ?? PlatformCouponRule.InvalidCodeMessage, PlatformCouponRule.RuleCode);
        db.PlatformCouponRedemptions.Add(new PlatformCouponRedemption
        {
            CouponId = coupon.Id, CompanyId = companyId, SourceKind = sourceKind, SourceId = sourceId,
            ListAmount = eval.ListAmount, DiscountAmount = eval.Discount, AmountAfter = eval.AmountAfter,
            Status = PlatformCouponRedemptionStatus.Reserved, ReservedUntilUtc = nowUtc.AddDays(PlatformCouponRule.ReservationDays),
            CreatedBy = actor,
        });
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Create, EntityType = nameof(PlatformCouponRedemption), EntityId = sourceId.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "PlatformCouponReserved", ruleCode = PlatformCouponRule.RuleCode, coupon = coupon.Code, sourceKind,
                list = eval.ListAmount, discount = eval.Discount, after = eval.AmountAfter, by = actor,
            }),
            Timestamp = nowUtc,
        });
        return (coupon, eval);
    }

    /// <summary>เงินเข้าแล้ว ⇒ นับการใช้ (idempotent) · ไม่มีการจองของรายการนี้ = ไม่ทำอะไร · ผู้เรียก SaveChanges เอง (เส้น relational เขียนทันทีด้วย UPDATE แบบมีเงื่อนไข)</summary>
    public static async Task RedeemAsync(AccountingDbContext db, Guid companyId, string sourceKind, Guid sourceId, string actor, CancellationToken ct = default)
    {
        var row = await db.PlatformCouponRedemptions
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.SourceKind == sourceKind && r.SourceId == sourceId, ct);
        if (row == null || row.Status == PlatformCouponRedemptionStatus.Redeemed) return;
        var now = DateTime.UtcNow;
        if (db.Database.IsRelational())
        {
            // UPDATE แบบมีเงื่อนไข ⇒ สองทางเข้าพร้อมกัน (webhook + แอดมิน) นับได้ครั้งเดียว
            var changed = await db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE ""PlatformCouponRedemptions"" SET ""Status"" = {(int)PlatformCouponRedemptionStatus.Redeemed}, ""RedeemedAt"" = {now}, ""UpdatedAt"" = {now}, ""UpdatedBy"" = {actor}
                   WHERE ""Id"" = {row.Id} AND ""Status"" <> {(int)PlatformCouponRedemptionStatus.Redeemed}", ct);
            if (changed == 1)
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $@"UPDATE ""PlatformCoupons"" SET ""UsedCount"" = ""UsedCount"" + 1, ""UpdatedAt"" = {now} WHERE ""Id"" = {row.CouponId}", ct);
            else return;
            db.Entry(row).State = EntityState.Detached;
        }
        else
        {
            row.Status = PlatformCouponRedemptionStatus.Redeemed; row.RedeemedAt = now; row.UpdatedAt = now; row.UpdatedBy = actor;
            var coupon = await db.PlatformCoupons.FirstOrDefaultAsync(c => c.Id == row.CouponId, ct);
            if (coupon != null) coupon.UsedCount++;
        }
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(PlatformCouponRedemption), EntityId = sourceId.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action = "PlatformCouponRedeemed", ruleCode = PlatformCouponRule.RuleCode, sourceKind, discount = row.DiscountAmount, by = actor }),
            Timestamp = now,
        });
    }

    /// <summary>รายการถูกยกเลิก/ปฏิเสธ ⇒ ปลดการจอง (ไม่นับเข้าเพดาน) · นับแล้ว (เงินเข้า) = ไม่ปลด · ผู้เรียก SaveChanges เอง</summary>
    public static async Task ReleaseAsync(AccountingDbContext db, Guid companyId, string sourceKind, Guid sourceId, string reason, string actor,
        CancellationToken ct = default)
    {
        var row = await db.PlatformCouponRedemptions
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.SourceKind == sourceKind && r.SourceId == sourceId
                && r.Status == PlatformCouponRedemptionStatus.Reserved, ct);
        if (row == null) return;
        row.Status = PlatformCouponRedemptionStatus.Released;
        row.ReleasedAt = DateTime.UtcNow;
        row.ReleaseReason = reason.Length > 300 ? reason[..300] : reason;
        row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = actor;
    }
}
