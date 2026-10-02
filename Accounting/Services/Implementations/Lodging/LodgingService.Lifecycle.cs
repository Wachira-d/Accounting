using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Lodging;

/// <summary>โมดูลที่พัก — lifecycle ของการจอง + เอกสารบัญชี (partial 3/4)
///
/// เส้นเงิน (ทุกใบผ่าน IDocumentService — เลข gap-free §86/4):
///   ยืนยัน+รับมัดจำ → Receipt(IsDeposit) ⇒ Cr 217xx ตาม "วิธีบันทึกมัดจำ" (DepositPolicyResolver · รอบ 193 #34):
///                    เต็มยอดไม่แยก VAT · แยก VAT รอเรียกเก็บ 21913 · ออกใบกำกับ VAT ทันที 21911 (§78/1 ค่าเริ่มต้น)
///   เช็คเอาต์      → TaxInvoice/Invoice ทั้งการเข้าพัก + folio แล้วใช้มัดจำตามโหมดของแต่ละใบ (LodgingDepositSettlement):
///                    ออกใบกำกับแล้ว → หักฐานมัดจำออกจากฐานภาษีใบสุดท้าย + RealizeDeposit (VAT ไม่ซ้ำ) ·
///                    เต็มยอด/รอเรียกเก็บ → ApplyDepositToInvoice (Dr 217xx [+21913] / Cr ลูกหนี้) · รับส่วนที่เหลือ = Payment
///   ยกเลิก/no-show → ส่วนที่ริบ = RealizeDeposit ทันที · ส่วนที่ต้องคืน = "ค้างคืน" (ไม่แตะเงินสด · F-03) →
///                    RecordRefundPaidAsync ตอนโอนคืนจริง = RefundDeposit (JE + ใบลดหนี้) จากบัญชีที่เงินเข้า
/// </summary>
public partial class LodgingService
{
    private static readonly LodgingReservationStatus[] Terminal = { LodgingReservationStatus.Cancelled, LodgingReservationStatus.NoShow, LodgingReservationStatus.CheckedOut };

    /// <summary>ป้ายโมดูลบนเอกสารที่โมดูลนี้ออกให้ — ใช้กันนับโควตาเอกสารซ้ำ
    /// (มิเตอร์ของที่พักคือ lodging.stay ไม่ใช่จำนวนใบ — DocumentQuotaPolicy)</summary>
    internal const string LodgingOrigin = "Lodging";

    /// <summary>นับการเข้าพักนี้เป็น 1 หน่วยมิเตอร์ (`lodging.stay`)
    ///
    /// เรียกตอน **ปิดสถานะ** เท่านั้น: เช็คเอาต์ · no-show · ยกเลิกที่มีเงินมัดจำ —
    /// ไม่ใช่ตอนจอง (จองแล้วยกเลิกฟรีต้องไม่โดนคิด) และไม่ใช่ตอนออกเอกสาร (ที่พัก
    /// ที่ตั้ง AccountingMode=Off ไม่มีเอกสารเลยแต่ต้องนับเท่ากัน — §13.2/§13.3)
    ///
    /// กันนับซ้ำสองชั้น: `MeteredPeriod` บนการจอง + IdempotencyKey ต่อ ReservationId
    /// **ห้าม throw** — มิเตอร์พังต้องไม่ทำให้เช็คเอาต์/ยกเลิกทำไม่ได้</summary>
    private async Task MeterStayAsync(Guid companyId, LodgingReservation r, string reason)
    {
        if (r.MeteredPeriod != null) return;
        var period = AddOnBilling.PeriodOf(DateTime.UtcNow);
        r.MeteredPeriod = period;
        if (_metering == null) return;
        try
        {
            await _metering.RecordAsync(new Accounting.Services.Interfaces.UsageRecordRequest(
                CompanyId: companyId,
                FeatureCode: Models.Constants.AddOnCodes.LodgingStay,
                Quantity: 1,
                IdempotencyKey: $"stay:{r.Id:N}",
                RefEntityType: "LodgingReservation",
                RefEntityId: r.Id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "บันทึกมิเตอร์การเข้าพัก {No} ({Reason}) ไม่สำเร็จ — งานหลักสำเร็จแล้ว",
                r.ReservationNumber, reason);
        }
    }

    private IQueryable<LodgingReservation> ResQuery(Guid companyId) => _db.LodgingReservations
        .Include(r => r.Property)
        .Include(r => r.Rooms).ThenInclude(x => x.Unit)
        .Include(r => r.Extras)
        .Include(r => r.Charges)
        .Include(r => r.RatePlan)
        .Where(r => r.CompanyId == companyId);

    private async Task<LodgingReservation> RequireReservationAsync(Guid companyId, Guid reservationId)
        => await ResQuery(companyId).FirstOrDefaultAsync(r => r.Id == reservationId) ?? throw new KeyNotFoundException("ไม่พบการจอง");

    private async Task<LodgingReservation?> ByTokenAsync(Guid companyId, Guid siteId, string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        var t = token.Trim().ToLowerInvariant();
        return await ResQuery(companyId).FirstOrDefaultAsync(r => r.PublicToken == t && r.SiteId == siteId);
    }

    // ═══════════════════════════ Public (token) ═══════════════════════════

    public async Task<LodgingReservationResponse?> GetReservationByTokenAsync(Guid companyId, Guid siteId, string token)
    {
        var r = await ByTokenAsync(companyId, siteId, token);
        if (r == null) return null;
        // คำตัดสินข้อ 128: แขกเปิดหน้าการจองหลังหมดเวลาส่งสลิป ⇒ ให้ตัวยกเลิกอัตโนมัติ (กติกาเดียว LodgingHoldRule) ทำงานก่อน
        // แล้วอ่านแถวใหม่ — หน้าแขกเห็น "หมดเวลาส่งสลิป การจองถูกยกเลิก" ตรงกับความจริง ไม่ใช่ "รอสลิป" ที่ห้องถูกปล่อยไปแล้ว
        if (r.Status == LodgingReservationStatus.Pending && r.HoldExpiresAt is DateTime hold && hold <= DateTime.UtcNow)
        {
            await ExpireHoldsAsync(companyId, r.PropertyId);
            await _db.Entry(r).ReloadAsync();
        }
        return await MapAsync(companyId, r, includeToken: true, includeInternal: false);
    }

    public async Task<(string Path, string ContentType)?> GetSlipFileAsync(Guid companyId, Guid reservationId)
    {
        var url = await _db.LodgingReservations.AsNoTracking()
            .Where(x => x.Id == reservationId && x.CompanyId == companyId && !x.IsDeleted)
            .Select(x => x.PaymentSlipUrl).FirstOrDefaultAsync();
        return SlipFile(url);
    }

    public async Task<(string Path, string ContentType)?> GetSlipFileByTokenAsync(Guid companyId, Guid siteId, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var url = await _db.LodgingReservations.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.SiteId == siteId
                     && x.PublicToken == token && !x.IsDeleted)
            .Select(x => x.PaymentSlipUrl).FirstOrDefaultAsync();
        return SlipFile(url);
    }

    private static (string Path, string ContentType)? SlipFile(string? storedUrl)
    {
        var path = ResolveSlipPath(storedUrl);
        return path == null ? null : (path, SlipContentType(path));
    }

    public async Task<LodgingReservationResponse?> UploadSlipByTokenAsync(Guid companyId, Guid siteId, string token, IFormFile file, string? reference)
    {
        var r = await ByTokenAsync(companyId, siteId, token);
        if (r == null) return null;
        // ด่านเร็วก่อนเขียนไฟล์ (ตัดสินจริงอีกครั้งใต้ล็อกหลังอ่านแถวใหม่ — ฝ่ายค้านรอบ 202 P2-2)
        EnsureSlipAcceptable(r);
        var url = await SaveSlipFileAsync(companyId, r.Id, file);
        // รอบ 202 (O-P1-5 · คำตัดสินข้อ 127): สลิปรอตรวจ = กันห้องจนพนักงานตัดสิน (LodgingHoldRule) — ตัดสิน "ห้องยังว่างไหม" + บันทึกสลิปใต้ล็อกที่พัก ·
        // แขกส่งสลิปหลัง hold หมดและห้องถูกจองไปแล้ว ⇒ **รับสลิปไว้เสมอ** (เงินโอนแล้ว · ไม่คืนอัตโนมัติ) แต่ติดธง "เงินเข้าแต่ยืนยันไม่ได้" + แจ้งที่พัก ·
        // ฝ่ายค้าน P1-3ก: ใบที่ "ระบบ" ยกเลิกเพราะหมดเวลาถือห้อง (LodgingHoldRule.IsAutoExpiredHold) ก็รับสลิป + ติดธงเช่นกัน (แขกโอนแล้วจริง)
        string? problem = null;
        var confirmedBySlip = false;
        await WithPropertyLockAsync(companyId, r.PropertyId, async () =>
        {
            await _db.Entry(r).ReloadAsync();
            EnsureSlipAcceptable(r);
            if (LodgingHoldRule.IsAutoExpiredHold(r.Status, r.CancellationReason, r.DepositPaid))
                problem = $"แขกส่งสลิปหลังระบบยกเลิกการจองเพราะหมดเวลาชำระมัดจำ (ยกเลิกเมื่อ {r.CancelledAt?.AddHours(7):dd/MM/yyyy HH:mm})";
            else if (LodgingHoldRule.HoldLapsed(
                new LodgingHoldFacts(r.Status, r.HoldExpiresAt, r.DepositPaid, r.SlipUploadedAt != null, r.PaymentProblemAt != null), DateTime.UtcNow))
            {
                var ctx = await LoadContextAsync(companyId, r.PropertyId, r.CheckInDate, r.CheckOutDate, excludeReservationId: r.Id);
                try { EnsureRoomsAvailable(ctx, r, "แขกส่งสลิปหลังหมดเวลาถือห้อง"); }
                catch (BusinessRuleException ex) when (ex.RuleCode == "LODGING-OVERSOLD") { problem = $"แขกส่งสลิปแล้วแต่ห้องเต็ม — {ex.Message}"; }
            }
            r.PaymentSlipUrl = url; r.PaymentReference = reference; r.SlipUploadedAt = DateTime.UtcNow;
            // ส่งใหม่แล้ว = ล้างผลปฏิเสธครั้งก่อนออกจากหน้าจอ (แต่ **คงตัวนับไว้** เพราะ
            // มันคือหลักฐานว่าใบนี้เคยมีปัญหา — ตัวนับที่รีเซ็ตทุกครั้งจะไม่มีวันถึงเกณฑ์)
            r.SlipRejectedReason = null; r.SlipRejectedAt = null;
            // คำตัดสินข้อ 128: โหมด RequireSlip + AutoConfirmOnSlip ⇒ ยืนยันการจองทันที — ตัดสินใต้ล็อกเดียวกับการตรวจห้องว่างข้างบน
            // (hold หมดแล้วห้องเต็ม / ใบถูกระบบยกเลิก ⇒ problem ⇒ ธงข้อ 127 ไม่ใช่ยืนยัน) · **ส่งสลิป ≠ รับเงิน**: ไม่แตะ DepositPaid/PaidAmount —
            // ยอดบันทึกเมื่อพนักงานกด “บันทึกรับเงินตามสลิป” (ConfirmAsync) · ผู้ยืนยัน = SlipConfirmActor (ตัวแยกตอนปฏิเสธสลิป)
            if (LodgingGuestConfirmPolicy.OnSlipUploaded(r.GuestConfirmMode, r.Property.AutoConfirmOnSlip, r.Status, problemFound: problem != null)
                == LodgingSlipOutcome.ConfirmNow)
            {
                r.Status = LodgingReservationStatus.Confirmed;
                r.ConfirmedAt = DateTime.UtcNow; r.ConfirmedBy = LodgingGuestConfirmPolicy.SlipConfirmActor;
                r.HoldExpiresAt = null;
                confirmedBySlip = true;
                AppendInternal(r, "ยืนยันอัตโนมัติจากสลิปของแขก (ค่าตั้ง “ส่งสลิปแล้วยืนยันทันที”) — ยังไม่ได้บันทึกรับเงิน: "
                    + "ตรวจยอดโอนแล้วกด “บันทึกรับเงินตามสลิป” หรือปฏิเสธสลิป (การจองกลับเป็นรอชำระ)");
            }
            // อัปโหลดสลิปแล้ว = ต่อเวลาถือห้องให้พนักงานตรวจ (ป้ายเวลาบนจอ · การกันห้องจริงอ่านจาก "มีสลิปรอตรวจ" ไม่ใช่เวลานี้)
            else if (r.Status == LodgingReservationStatus.Pending && r.HoldExpiresAt != null && r.HoldExpiresAt < DateTime.UtcNow.AddHours(24))
                r.HoldExpiresAt = DateTime.UtcNow.AddHours(24);
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "GuestUploadedSlip", reference, problem, confirmedBySlip }));
            await _db.SaveChangesAsync();
            return true;
        });
        if (problem != null)
            await FlagPaymentProblemAsync(companyId, r.Id, problem, "slip");
        await TryNotifyAsync(companyId, r.Id, "slip");
        if (confirmedBySlip) await TryNotifyAsync(companyId, r.Id, "confirmed");   // อีเมลยืนยันถึงแขก (เส้นเดียวกับพนักงานยืนยัน)
        var mapped = await MapAsync(companyId, r, includeToken: true, includeInternal: false);
        mapped.GuestMessage = LodgingGuestConfirmPolicy.SlipUploadedMessage(r.Status, r.ConfirmedBy, r.DepositPaid,
            paymentProblem: problem != null, r.GuestConfirmMode);
        return mapped;
    }

    /// <summary>ด่านรับสลิปของแขก — ยกเลิก/no-show = ปฏิเสธ <b>ยกเว้น</b>ใบที่ระบบยกเลิกเพราะหมดเวลาถือห้อง (แขกโอนแล้วจริง ⇒ รับไว้ + ธงให้พนักงานตัดสิน ·
    /// ฝ่ายค้านรอบ 202 P1-3ก · คำตัดสินข้อ 127) · ปิดรับสลิปของใบนี้ = ปฏิเสธพร้อมทางไปต่อ</summary>
    private static void EnsureSlipAcceptable(LodgingReservation r)
    {
        if (r.Status is LodgingReservationStatus.Cancelled or LodgingReservationStatus.NoShow
            && !LodgingHoldRule.IsAutoExpiredHold(r.Status, r.CancellationReason, r.DepositPaid))
            throw new BusinessRuleException("การจองนี้ถูกยกเลิกแล้ว — หากโอนเงินไปแล้ว กรุณาติดต่อที่พักโดยตรง");
        // ที่พักปิดรับสลิปของใบนี้แล้ว (พบสลิปไม่ตรง/ปลอมซ้ำ) — ต้องบอกทางไปต่อ
        // ไม่ใช่ปฏิเสธเฉย ๆ (กติกา "ปฏิเสธแล้วต้องมีทางไปต่อ")
        if (r.SlipUploadBlocked)
            throw new BusinessRuleException(
                "ที่พักปิดรับสลิปของการจองนี้แล้ว — กรุณาชำระออนไลน์ผ่านหน้านี้ "
                + "หรือติดต่อที่พักโดยตรงเพื่อยืนยันการชำระเงิน");
    }

    /// <summary>
    /// **ปฏิเสธสลิปที่แขกส่งมา** (LDG-P0-02)
    ///
    /// <para>เดิมพนักงานมีแค่ "ยืนยัน + รับมัดจำ" กับ "ยกเลิกทั้งใบ" ⇒ เจอสลิปไม่ตรง
    /// หรือสลิปปลอมก็ได้แต่<b>เงียบ</b> แล้วหน้าแขกค้างข้อความ "รอที่พักตรวจสอบ"
    /// ตลอดไป — silent no-op ในรูปที่มองไม่เห็นที่สุด (ไม่มี error ให้ไล่ เพราะ
    /// ไม่มีอะไรเกิดขึ้นเลย)</para>
    ///
    /// <para><b>สามอย่างที่ต้องเกิดพร้อมกัน</b> ตามกติกา "ดัง 3 ที่":
    /// (ก) ล้างสลิปออกจากแถวเพื่อให้แขกส่งใหม่ได้ (ข) เก็บเหตุผลไว้บนตัวข้อมูล
    /// ให้ทั้งแขกและ audit เห็น (ค) แจ้งแขกทางอีเมล</para>
    ///
    /// <para><b>ต่ออายุ hold ด้วย</b> — ปฏิเสธแล้วปล่อยห้องทันทีเท่ากับลงโทษแขก
    /// สำหรับความผิดที่ยังไม่พิสูจน์ · ให้เวลาส่งใหม่ 24 ชม.
    /// ยกเว้นกรณีปิดรับสลิป (ตั้งใจไม่ต่อให้ เพราะไม่รอสลิปแล้ว)</para>
    ///
    /// <para>⚠️ <b>ไม่ลบไฟล์จริง</b> — สลิปที่ถูกปฏิเสธคือหลักฐานตั้งต้นถ้ามีข้อพิพาท
    /// (บทเรียนเดียวกับ "เปลี่ยนชื่อฉบับเก่า ไม่ใช่ลบ" ของไฟล์แนบเงินเดือน)</para>
    /// </summary>
    public async Task<LodgingReservationResponse?> RejectSlipAsync(
        Guid companyId, Guid reservationId, LodgingRejectSlipRequest request, string userId)
    {
        var reason = (request.Reason ?? "").Trim();
        if (reason.Length == 0)
            throw new BusinessRuleException(
                "ต้องระบุเหตุผลที่สลิปไม่ผ่าน — ข้อความนี้คือสิ่งเดียวที่แขกจะเห็นว่าต้องทำอะไรต่อ");
        if (reason.Length > 500) reason = reason[..500];

        // คำตัดสินข้อ 128: ใบที่ยืนยันเพราะสลิปถูกปฏิเสธ ⇒ เปลี่ยนสถานะกลับ — ล็อกต่อการจองเดียวกับ ConfirmAsync (พนักงานกดรับเงินพร้อมกัน
        // ต้องไม่ได้ "รอชำระที่มีเงินรับแล้ว") · อ่านแถวใหม่หลังได้ล็อก
        // ฝ่ายค้าน P2-2: ล็อกต่อการจองอย่างเดียวไม่กันเช็คอิน/สลิปใบที่สอง (สองเส้นนั้นถือล็อกที่พัก) ⇒ ถือล็อกที่พักต่อจากล็อกการจอง
        // (ลำดับเดียวกับยืนยัน: การจอง → ที่พัก) แล้วอ่านแถวใหม่ + ตรวจว่าสิ่งที่พนักงานเห็นยังเป็นปัจจุบัน
        var propertyId = await _db.LodgingReservations.AsNoTracking()
            .Where(x => x.Id == reservationId && x.CompanyId == companyId).Select(x => (Guid?)x.PropertyId).FirstOrDefaultAsync();
        if (propertyId == null) return null;
        var r = await WithReservationLockAsync<LodgingReservation?>(companyId, reservationId, AdvisoryLockKey.LodgingConfirm, () =>
            WithPropertyLockAsync<LodgingReservation?>(companyId, propertyId.Value, async () =>
        {
            var row = await ResQuery(companyId).FirstOrDefaultAsync(x => x.Id == reservationId);
            if (row == null) return null;
            await _db.Entry(row).ReloadAsync();
            if (row.SlipUploadedAt == null)
                throw new BusinessRuleException("การจองนี้ยังไม่มีสลิปให้ตรวจ");
            if (LodgingGuestConfirmPolicy.RejectSlipStaleProblem(request.SeenSlipUploadedAt, row.SlipUploadedAt, request.SeenStatus, row.Status) is string stale)
                throw new BusinessRuleException(stale, LodgingGuestConfirmPolicy.RejectSlipStaleRuleCode);

            var now = DateTime.UtcNow;
            var statusBefore = row.Status;
            var revert = LodgingGuestConfirmPolicy.IsSlipConfirmed(row.Status, row.ConfirmedBy, row.DepositPaid);
            var oldUrl = row.PaymentSlipUrl;
            row.PaymentSlipUrl = null;
            row.PaymentReference = null;
            row.SlipUploadedAt = null;
            row.SlipRejectedCount += 1;
            row.SlipRejectedReason = reason;
            row.SlipRejectedAt = now;
            if (request.BlockFurtherUploads) row.SlipUploadBlocked = true;
            row.HoldExpiresAt = LodgingGuestConfirmPolicy.HoldAfterSlipRejected(statusBefore, revert, row.HoldExpiresAt,
                request.BlockFurtherUploads, now, row.Property.SlipDeadlineMinutes);
            if (revert)
            {
                // ยืนยันเพราะสลิป (ยังไม่มีเงินบันทึก) ⇒ สลิปไม่ผ่าน = การจองยังไม่สำเร็จ · กลับเป็นรอชำระ + ถือห้องตามกติกาเดิม (หมดแล้วยกเลิกอัตโนมัติ)
                row.Status = LodgingReservationStatus.Pending;
                row.ConfirmedAt = null; row.ConfirmedBy = null;
                var holdText = row.HoldExpiresAt is DateTime h ? LodgingGuestConfirmPolicy.ThaiTime(h) : "-";
                AppendInternal(row, $"ปฏิเสธสลิปของการจองที่ยืนยันอัตโนมัติจากสลิป — กลับเป็นรอชำระ (ถือห้องถึง {holdText})");
            }

            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, row, new
            {
                action = "SlipRejected", reason, blocked = row.SlipUploadBlocked,
                rejectedCount = row.SlipRejectedCount, previousSlip = oldUrl, by = userId,
                revertedFromSlipConfirm = revert,
            }));
            await _db.SaveChangesAsync();
            return row;
        }));
        if (r == null) return null;
        await TryNotifyAsync(companyId, r.Id, "slip-rejected");
        return await MapAsync(companyId, r, includeToken: true, includeInternal: true);
    }

    public async Task<LodgingReservationResponse?> CancelByTokenAsync(Guid companyId, Guid siteId, string token, LodgingCancelRequest request)
    {
        var r = await ByTokenAsync(companyId, siteId, token);
        if (r == null) return null;
        if (r.Status is LodgingReservationStatus.CheckedIn or LodgingReservationStatus.CheckedOut)
            throw new BusinessRuleException("เช็คอินแล้ว — กรุณาติดต่อที่พักโดยตรง");
        // ยกเลิกแล้ว/ไม่มาเข้าพัก = ตอบสถานะเดิม (แขกดับเบิลคลิก/กลับมากดวันหลัง) — ห้ามคิดค่าปรับซ้ำ (C3)
        if (r.Status is LodgingReservationStatus.Cancelled or LodgingReservationStatus.NoShow)
            throw new BusinessRuleException("การจองนี้ถูกยกเลิกไปแล้ว — หากมียอดคืนเงิน ที่พักจะโอนคืนตามที่แจ้งไว้");
        try
        {
            await CancelCoreAsync(companyId, r, request.Reason ?? "แขกยกเลิกเอง", "guest", noShow: false);
        }
        catch (BusinessRuleException ex) when (ex.RuleCode == "LODGING-CANCEL-FORFEIT")
        {
            // P9 — การยกเลิกสำเร็จแล้ว (สถานะถูกบันทึกก่อนลงบัญชี) · ส่วนที่ล้มคือการลงรายได้ส่วนที่ริบ ซึ่งเป็นงานของพนักงาน
            // (หมายเหตุ + audit บนการจองแล้ว) — แขกต้องไม่เห็นข้อความภายใน/HTTP 400 ทั้งที่ยกเลิกสำเร็จ
            _logger.LogError(ex, "Guest cancel {No}: ยกเลิกแล้วแต่ลงรายได้ส่วนที่ริบไม่สำเร็จ", r.ReservationNumber);
        }
        return await MapAsync(companyId, r, includeToken: true, includeInternal: false);
    }

    public async Task<LodgingGuestRequestDto?> CreateGuestRequestByTokenAsync(Guid companyId, Guid siteId, string token, LodgingGuestRequestCreate request)
    {
        var r = await ByTokenAsync(companyId, siteId, token);
        if (r == null) return null;
        if (string.IsNullOrWhiteSpace(request.Details)) throw new BusinessRuleException("กรุณาระบุรายละเอียด");
        var gr = new LodgingGuestRequest
        {
            CompanyId = companyId, PropertyId = r.PropertyId, ReservationId = r.Id, RequestType = request.RequestType,
            Details = request.Details.Trim().Length > 2000 ? request.Details.Trim()[..2000] : request.Details.Trim(), CreatedBy = "guest",
        };
        _db.LodgingGuestRequests.Add(gr);
        await _db.SaveChangesAsync();
        return ToDto(gr, r);
    }

    // ═══════════════════════════ Admin: list / get / update ═══════════════════════════

    public async Task<PagedResponse<LodgingReservationListItem>> ListReservationsAsync(Guid companyId, Guid? propertyId, string? status,
        DateTime? from, DateTime? to, string? search, string? view, int page, int pageSize)
    {
        if (propertyId is Guid pidExp) await ExpireHoldsAsync(companyId, pidExp);
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        var q = _db.LodgingReservations.AsNoTracking().Where(r => r.CompanyId == companyId);
        if (propertyId is Guid pid) q = q.Where(r => r.PropertyId == pid);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<LodgingReservationStatus>(status, true, out var st)) q = q.Where(r => r.Status == st);
        var today = DateTime.UtcNow.AddHours(7).Date;
        switch (view)
        {
            case "arrivals": q = q.Where(r => r.CheckInDate == (from ?? today).Date && (r.Status == LodgingReservationStatus.Confirmed || r.Status == LodgingReservationStatus.Pending)); break;
            case "departures": q = q.Where(r => r.CheckOutDate == (from ?? today).Date && r.Status == LodgingReservationStatus.CheckedIn); break;
            case "inhouse": q = q.Where(r => r.Status == LodgingReservationStatus.CheckedIn); break;
            case "pending": q = q.Where(r => r.Status == LodgingReservationStatus.Pending); break;
            // คำตัดสินข้อ 128: รวมใบที่ "ยืนยันอัตโนมัติจากสลิป" ที่ยังไม่บันทึกรับเงิน (ไม่งั้นพนักงานไม่เห็นใบที่ต้องตรวจยอดโอน)
            // ฝ่ายค้าน P2-3: ตัวกรองเดียวกับตัวนับบนแดชบอร์ด (LodgingGuestConfirmPolicy.AwaitingSlipReview)
            case "slips": q = q.Where(LodgingGuestConfirmPolicy.AwaitingSlipReview); break;
            // F-03 — คิว "ต้องคืนเงินแขก": ยกเลิก/no-show ที่ยอดต้องคืนยังมากกว่ายอดที่ยืนยันว่าคืนแล้ว
            // + เช็คเอาต์ที่มัดจำเกินยอดใบสุดท้าย (C2) · ไม่รวมแถวก่อนรอบ 193 (ระบบเดิมลงคืนไปแล้ว — ไม่มีข้อมูลการโอน)
            case "refunds": q = q.Where(r => (r.Status == LodgingReservationStatus.Cancelled || r.Status == LodgingReservationStatus.NoShow
                    || r.Status == LodgingReservationStatus.CheckedOut)
                && r.RefundAmount - r.RefundPaidAmount > 0.005m
                && (r.RefundPaidBy == null || !r.RefundPaidBy.StartsWith(LodgingDepositSettlement.LegacyRefundMarker))); break;
            // รอบ 202 (O-P0-2 · คำตัดสินข้อ 119): "ค้างปิด" — เลยวันเช็คเอาต์แล้วยังไม่ถูกปิด (night audit ไม่ปิดเองแล้ว) · ช่วงวันที่เดียวกับ LodgingOverdueRule.Classify
            case "overdue": q = q.Where(r => r.CheckOutDate < today
                && (r.Status == LodgingReservationStatus.CheckedIn || r.Status == LodgingReservationStatus.Confirmed || r.Status == LodgingReservationStatus.Pending)); break;
            // รอบ 202 (O-P1-4): เงินออนไลน์เข้าแล้วแต่ยืนยันอัตโนมัติไม่ได้
            case "payproblem": q = q.Where(r => r.PaymentProblemAt != null); break;
            // รอบ 202: แถวที่ night audit รุ่นก่อนประทับ CheckedOut/NoShow เอง (อ่านอย่างเดียว · ปิดต่อผ่านปุ่มในรายละเอียด) — เงื่อนไขเดียวกับ LodgingOverdueRule
            case "legacy": q = q.Where(r =>
                (r.Status == LodgingReservationStatus.CheckedOut && r.FinalDocumentId == null && r.InternalNotes != null
                    && r.InternalNotes.Contains(LodgingOverdueRule.LegacyAutoCheckoutMarker) && r.Property.AccountingMode != LodgingAccountingMode.Off)
                || (r.Status == LodgingReservationStatus.NoShow && r.CancellationFee == 0 && r.RefundAmount == 0 && r.CancellationReason != null
                    && r.CancellationReason.StartsWith(LodgingOverdueRule.LegacyAutoNoShowReason))); break;
            default:
                if (from is DateTime f) q = q.Where(r => r.CheckOutDate > f.Date);
                if (to is DateTime t) q = q.Where(r => r.CheckInDate <= t.Date);
                break;
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(r => r.ReservationNumber.Contains(s) || r.GuestName.Contains(s) || (r.GuestPhone != null && r.GuestPhone.Contains(s))
                || (r.GuestEmail != null && r.GuestEmail.Contains(s)) || (r.SourceReference != null && r.SourceReference.Contains(s)));
        }
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(r => r.CheckInDate).ThenByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new
            {
                r.Id, r.ReservationNumber, r.Status, r.Source, r.GuestName, r.GuestPhone, r.CheckInDate, r.CheckOutDate, r.Nights,
                r.TotalAmount, r.FolioTotal, r.PaidAmount, r.DepositRequired, r.HoldExpiresAt, r.SlipUploadedAt, r.CreatedAt,
                r.RefundAmount, r.RefundPaidAmount, r.RefundPaidBy,
                r.PaymentProblemAt, r.FinalDocumentId, r.CancellationReason, r.CancellationFee,
                r.GuestConfirmMode, r.ConfirmedBy, r.DepositPaid, r.SlipUploadBlocked,
                NotesHaveLegacyMarker = r.InternalNotes != null && r.InternalNotes.Contains(LodgingOverdueRule.LegacyAutoCheckoutMarker),
                AccountingOff = r.Property.AccountingMode == LodgingAccountingMode.Off,
                Rooms = r.Rooms.Select(x => new { x.RoomTypeName, UnitNumber = x.Unit != null ? x.Unit.Number : null }).ToList(),
            }).ToListAsync();
        var list = items.Select(r => new LodgingReservationListItem
        {
            Id = r.Id, ReservationNumber = r.ReservationNumber, Status = r.Status, Source = r.Source, GuestName = r.GuestName, GuestPhone = r.GuestPhone,
            CheckInDate = r.CheckInDate, CheckOutDate = r.CheckOutDate, Nights = r.Nights, RoomCount = r.Rooms.Count,
            RoomSummary = string.Join(" · ", r.Rooms.GroupBy(x => x.RoomTypeName).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)),
            UnitNumbers = string.Join(", ", r.Rooms.Where(x => x.UnitNumber != null).Select(x => x.UnitNumber)),
            TotalAmount = r.TotalAmount, FolioTotal = r.FolioTotal, PaidAmount = r.PaidAmount,
            BalanceDue = Accounting.Helpers.LodgingAmounts.BalanceDue(r.TotalAmount, r.FolioTotal, r.PaidAmount), DepositRequired = r.DepositRequired,
            HoldExpiresAt = r.HoldExpiresAt, HasSlip = r.SlipUploadedAt != null, CreatedAt = r.CreatedAt,
            RefundPending = LodgingDepositSettlement.RefundPendingOf(r.RefundAmount, r.RefundPaidAmount, r.RefundPaidBy),
            OverdueLabel = LodgingOverdueRule.Label(LodgingOverdueRule.Classify(r.Status, r.CheckOutDate, today)),
            HasPaymentProblem = r.PaymentProblemAt != null,
            SlipStateLabel = LodgingGuestConfirmPolicy.StaffSlipLabel(new LodgingGuestFacts(r.GuestConfirmMode, r.Status, r.DepositRequired, r.DepositPaid,
                r.TotalAmount, r.FolioTotal, r.PaidAmount, r.HoldExpiresAt, r.SlipUploadedAt != null, r.SlipUploadBlocked,
                r.ConfirmedBy, r.CancellationReason, r.PaymentProblemAt != null)),
            NeedsLegacyClose = LodgingOverdueRule.IsLegacyAutoCheckout(r.Status, r.FinalDocumentId,
                    r.NotesHaveLegacyMarker ? LodgingOverdueRule.LegacyAutoCheckoutMarker : null, r.AccountingOff)
                || LodgingOverdueRule.IsLegacyAutoNoShow(r.Status, r.CancellationReason, r.CancellationFee, r.RefundAmount),
        }).ToList();
        return new PagedResponse<LodgingReservationListItem>(list, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<LodgingReservationResponse?> GetReservationAsync(Guid companyId, Guid reservationId)
    {
        var r = await ResQuery(companyId).AsNoTracking().FirstOrDefaultAsync(x => x.Id == reservationId);
        return r == null ? null : await MapAsync(companyId, r, includeToken: true, includeInternal: true);
    }

    public async Task<LodgingReservationResponse> UpdateReservationAsync(Guid companyId, Guid reservationId, LodgingUpdateReservationRequest u, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        if (u.GuestName != null) { if (string.IsNullOrWhiteSpace(u.GuestName)) throw new BusinessRuleException("ชื่อผู้เข้าพักห้ามว่าง"); r.GuestName = u.GuestName.Trim(); }
        if (u.GuestEmail != null) r.GuestEmail = u.GuestEmail.Trim();
        if (u.GuestPhone != null) r.GuestPhone = u.GuestPhone.Trim();
        if (u.GuestNationality != null) r.GuestNationality = u.GuestNationality;
        if (u.GuestIdNumber != null) r.GuestIdNumber = u.GuestIdNumber.Trim();
        if (u.GuestAddress != null) r.GuestAddress = u.GuestAddress;
        if (u.GuestTaxId != null)
        {
            if (!string.IsNullOrWhiteSpace(u.GuestTaxId) && !ThaiTaxId.IsValid(u.GuestTaxId)) throw new BusinessRuleException("เลขประจำตัวผู้เสียภาษีไม่ถูกต้อง", "RD-86/4");
            r.GuestTaxId = string.IsNullOrWhiteSpace(u.GuestTaxId) ? null : ThaiTaxId.Normalize(u.GuestTaxId);
        }
        if (u.GuestCompanyName != null) r.GuestCompanyName = u.GuestCompanyName;
        if (u.ArrivalTime != null) r.ArrivalTime = u.ArrivalTime;
        if (u.SpecialRequests != null) r.SpecialRequests = u.SpecialRequests;
        if (u.InternalNotes != null) r.InternalNotes = u.InternalNotes;
        if (u.Infants is int inf) r.Infants = Math.Max(0, inf);
        if (u.SourceReference != null) r.SourceReference = u.SourceReference;
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    // ═══════════════════════════ Confirm + deposit ═══════════════════════════

    public async Task<LodgingReservationResponse> ConfirmAsync(Guid companyId, Guid reservationId, LodgingConfirmRequest request, string userId, Guid? moneyInAccountId = null, bool fromOnlinePayment = false)
    {
        // ฝ่ายค้านรอบ 202 P2-1: ล็อกต่อการจองครอบทั้งเส้น (สองคำขอยืนยันพร้อมกัน ⇒ ใบมัดจำสองใบ + DepositPaid เขียนทับกัน) · อ่านแถวใหม่หลังได้ล็อก
        return await WithReservationLockAsync(companyId, reservationId, AdvisoryLockKey.LodgingConfirm,
            () => ConfirmCoreAsync(companyId, reservationId, request, userId, moneyInAccountId, fromOnlinePayment));
    }

    /// <summary>
    /// ล็อกระดับ session ต่อการจอง (คีย์คงที่ <c>AdvisoryLockKey.For(บริษัท, scope, idการจอง)</c>) — <b>รอ</b>ได้สูงสุด ~10 วินาที (ไม่ใช่ปฏิเสธทันทีแบบ
    /// เช็คเอาต์ · webhook กับ poll ของเงินก้อนเดียวมาชนกันเป็นเรื่องปกติ: คำขอที่สองต้องรอแล้วเห็นว่าบันทึกแล้ว) · เกินเวลา = ปฏิเสธดังพร้อมทางไปต่อ ·
    /// ลำดับล็อก: การจอง → ที่พัก (xact) → เอกสาร/JE → audit · ไม่มีเส้นไหนถือล็อกที่พักแล้วขอล็อกการจอง
    /// </summary>
    private async Task<T> WithReservationLockAsync<T>(Guid companyId, Guid reservationId, string scope, Func<Task<T>> work)
    {
        var key = AdvisoryLockKey.For(companyId, scope, reservationId.ToString());
        var conn = _db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere) await conn.OpenAsync();
        var acquired = false;
        try
        {
            for (var attempt = 0; attempt < 20 && !acquired; attempt++)
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT pg_try_advisory_lock({key})";
                    acquired = Convert.ToBoolean(await cmd.ExecuteScalarAsync());
                }
                if (!acquired) await Task.Delay(500);
            }
            if (!acquired)
                throw new BusinessRuleException("มีรายการยืนยัน/รับมัดจำของการจองนี้กำลังทำงานอยู่ — รอสักครู่แล้วเปิดการจองดูใหม่ (อาจบันทึกไปแล้ว)",
                    "LODGING-CONFIRM-BUSY");
            return await work();
        }
        finally
        {
            if (acquired)
            {
                await using var unlock = conn.CreateCommand();
                unlock.CommandText = $"SELECT pg_advisory_unlock({key})";
                await unlock.ExecuteScalarAsync();
            }
            if (openedHere) await conn.CloseAsync();
        }
    }

    private async Task<LodgingReservationResponse> ConfirmCoreAsync(Guid companyId, Guid reservationId, LodgingConfirmRequest request, string userId, Guid? moneyInAccountId, bool fromOnlinePayment)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        await _db.Entry(r).ReloadAsync();   // P2-1: ค่าหลังได้ล็อก (ผู้เรียกอาจถือแถวที่อ่านไว้ก่อน)
        if (Terminal.Contains(r.Status)) throw new BusinessRuleException($"การจองอยู่ในสถานะ {StatusTh(r.Status)} — ยืนยันไม่ได้");
        // webhook + poll ของเงินก้อนเดียวกัน: คำขอที่สองเห็นเลขอ้างอิงเดิมถูกบันทึกแล้ว ⇒ ตอบสถานะปัจจุบัน (ไม่ออกใบมัดจำซ้ำ)
        if (fromOnlinePayment && !string.IsNullOrWhiteSpace(request.PaymentReference)
            && r.PaymentReference == request.PaymentReference && r.DepositPaid > 0m)
            return await MapAsync(companyId, r, true, true);
        // ฝ่ายค้านรอบ 202 P1-3ค: เงินออนไลน์ที่เข้าแล้วแต่ยืนยันไม่ได้ ⇒ พนักงานยืนยันภายหลัง ใบมัดจำต้องลงบัญชีพักของช่องทางชำระจากรายการนั้น
        // (เซิร์ฟเวอร์ตัดสิน — ไม่พึ่งบัญชีที่หน้าจอส่งมา)
        if (!fromOnlinePayment && r.PaymentProblemIntentId is Guid problemIntentId)
            (moneyInAccountId, request) = await ProblemIntentMoneyInAsync(companyId, r, problemIntentId, moneyInAccountId, request);
        var amount = request.DepositAmount ?? Math.Max(0, r.DepositRequired - r.DepositPaid);
        if (amount < 0) throw new BusinessRuleException("ยอดมัดจำต้องไม่ติดลบ");
        var outstanding = r.TotalAmount + r.FolioTotal - r.PaidAmount;
        if (amount > outstanding + 0.005m) throw new BusinessRuleException($"ยอดรับเกินยอดคงค้าง ({outstanding:N2})");

        // ตรวจห้องว่างอีกครั้งตอนยืนยัน — Pending ที่หมด hold แล้วอาจถูกคนอื่นจองทับไปแล้ว
        // O-P0-1 รอบ 202: ตรวจ + จองห้องไว้ใต้ล็อกที่พัก (เดิมตรวจนอกล็อก ⇒ ยืนยันพร้อมการจองใหม่ = ห้องสุดท้ายถูกใช้สองใบ) ·
        // ใบมัดจำออกหลังปลดล็อก (เส้นเอกสารเปิดธุรกรรมของตัวเอง — ซ้อนใต้ล็อกไม่ได้)
        if (r.Status == LodgingReservationStatus.Pending)
            await ClaimInventoryForConfirmAsync(companyId, r);

        if (amount > 0)
        {
            if (r.DepositDocumentId != null && r.DepositPaid == 0)
                throw new BusinessRuleException("มีใบเสร็จมัดจำอยู่แล้วแต่ยอดยังไม่ถูกบันทึก — ตรวจสอบเอกสารก่อน");
            // โหมด Off = ไม่ออกเอกสารบัญชี (ลูกค้าใช้โปรแกรมบัญชีอื่น) — ยังบันทึกยอด
            // รับเงินไว้บนการจองตามปกติ และ**ยังนับมิเตอร์เท่าเดิม** (§13.3)
            if (r.Property.AccountingMode != LodgingAccountingMode.Off)
            {
                var doc = await CreateDepositReceiptAsync(companyId, r, amount, request, userId, moneyInAccountId);
                r.DepositDocumentId ??= doc.Id;
            }
            r.DepositPaid += amount; r.PaidAmount += amount;
            if (!string.IsNullOrWhiteSpace(request.PaymentReference)) r.PaymentReference = request.PaymentReference;
        }
        var wasPending = r.Status == LodgingReservationStatus.Pending;
        // S-06 — เงินเข้าจากช่องทางออนไลน์ / พนักงานกด "รับชำระเพิ่ม" ยืนยันเฉพาะเมื่อที่พักเปิด AutoConfirmOnDeposit ·
        // พนักงานกด "ยืนยัน" = ยืนยันเสมอ (C9: เดิมปุ่ม "รับชำระเพิ่ม" ยืนยันการจองเสมอ ไม่ตรงป้ายค่าตั้ง)
        var explicitConfirm = !fromOnlinePayment && (request.ConfirmReservation ?? true);
        r.Status = LodgingDepositSettlement.StatusAfterDeposit(r.Status, r.Property.AutoConfirmOnDeposit, explicitStaffConfirm: explicitConfirm);
        var confirmedNow = wasPending && r.Status == LodgingReservationStatus.Confirmed;
        if (confirmedNow) { r.ConfirmedAt ??= DateTime.UtcNow; r.ConfirmedBy ??= userId; }
        // hold ถูกล้างเสมอเมื่อมีเงินเข้า — การจองที่รับมัดจำแล้วห้ามถูกยกเลิกอัตโนมัติเพราะหมดเวลาถือห้อง
        if (amount > 0 || confirmedNow) r.HoldExpiresAt = null;
        // O-P1-4: เงินที่เคยเข้ามาแล้วยืนยันไม่ได้ ถูกบันทึกรับแล้ว ⇒ ปลดธง (หมายเหตุเดิมคงไว้เป็นประวัติ)
        if (amount > 0 && r.PaymentProblemAt != null)
        {
            AppendInternal(r, $"ปิดเรื่อง “เงินเข้าแต่ยืนยันไม่ได้” — บันทึกรับ {amount:N2} แล้ว");
            r.PaymentProblemAt = null; r.PaymentProblemNote = null; r.PaymentProblemIntentId = null;
        }
        if (wasPending && !confirmedNow && amount > 0)
            AppendInternal(r, $"รับมัดจำ {amount:N2}{(fromOnlinePayment ? " ผ่านช่องทางออนไลน์" : " (รับชำระเพิ่ม)")} แล้ว — ที่พักตั้งไม่ยืนยันอัตโนมัติ รอพนักงานกด “ยืนยัน”");
        if (!string.IsNullOrWhiteSpace(request.Note)) AppendInternal(r, request.Note);
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Approve, r, new { action = "Confirm", depositReceived = amount, method = request.PaymentMethod.ToString(), by = userId }));
        await _db.SaveChangesAsync();
        if (confirmedNow) await TryNotifyAsync(companyId, r.Id, "confirmed");
        return await MapAsync(companyId, r, true, true);
    }

    /// <summary>
    /// <b>ปิดเรื่อง "เงินเข้าแต่ยืนยันไม่ได้"</b> (ฝ่ายค้านรอบ 202 P1-3ข · คำตัดสินข้อ 127) — พนักงานเลือกทางเอง ทุกทางต้องมีเหตุผล + audit ·
    /// ห้ามล้างธงเงียบ:
    /// <list type="number">
    ///   <item><b>เปิดการจองกลับ</b> — เฉพาะใบที่ระบบยกเลิกเพราะหมดเวลาถือห้อง (<c>LodgingHoldRule.IsAutoExpiredHold</c>) · ตรวจห้องว่างจริงใต้ล็อกที่พัก ·
    ///     กลับเป็น "รอมัดจำ" (กันห้องเพราะธงยังติด) แล้วพนักงานกด "ยืนยัน + รับมัดจำ" ตามปกติ (ธงปลดตอนรับเงินสำเร็จ · บัญชีจากรายการชำระ P1-3ค)</item>
    ///   <item><b>คืนเงินแล้ว</b> — เลขอ้างอิงการคืนบังคับ · เงินก้อนนี้<b>ยังไม่เคยถูกลงบัญชีรับ</b> (ยืนยันไม่ได้ = ไม่มีใบมัดจำ/JE) ⇒ คืนแล้วไม่ต้องลงบัญชีคืน
    ///     (เงินเข้า-ออกจับคู่กันในการกระทบยอดธนาคาร/รายการชำระ) · มีมัดจำบนใบแล้ว ⇒ ปฏิเสธ (ใช้ "ยืนยันคืนเงินแล้ว" ของเส้นยกเลิกแทน)</item>
    ///   <item><b>ปิดโดยเหตุผล</b> — เช่น เงินไม่ได้เข้าจริง/บันทึกที่อื่นแล้ว</item>
    /// </list>
    /// </summary>
    public async Task<LodgingReservationResponse> ResolvePaymentProblemAsync(Guid companyId, Guid reservationId,
        LodgingResolvePaymentProblemRequest request, string userId)
    {
        var reason = (request.Reason ?? "").Trim();
        if (reason.Length == 0)
            throw new BusinessRuleException("ต้องระบุเหตุผลที่ปิดเรื่อง “เงินเข้าแต่ยืนยันไม่ได้” — ข้อความนี้คือหลักฐานว่าใครตัดสินอะไร", "LODGING-PAYMENT-PROBLEM-RESOLVE");
        var check = await RequireReservationAsync(companyId, reservationId);
        return await WithReservationLockAsync(companyId, reservationId, AdvisoryLockKey.LodgingConfirm, async () =>
        {
            var r = check;
            await _db.Entry(r).ReloadAsync();
            if (r.PaymentProblemAt == null)
                throw new BusinessRuleException("การจองนี้ไม่มีเรื่อง “เงินเข้าแต่ยืนยันไม่ได้” ค้างอยู่ (อาจปิดไปแล้ว — เปิดการจองดูใหม่)", "LODGING-PAYMENT-PROBLEM-RESOLVE");
            switch (request.Resolution)
            {
                case LodgingPaymentProblemResolution.Reopen:
                    if (!LodgingHoldRule.IsAutoExpiredHold(r.Status, r.CancellationReason, r.DepositPaid))
                        throw new BusinessRuleException(r.Status == LodgingReservationStatus.Pending
                            ? "การจองยังรอมัดจำอยู่ — กด “ยืนยัน + บันทึกรับมัดจำ” ได้เลย (ไม่ต้องเปิดกลับ)"
                            : "เปิดกลับได้เฉพาะการจองที่ระบบยกเลิกเพราะหมดเวลาชำระมัดจำ — ใบนี้ให้ใช้ “คืนเงินแล้ว” หรือ “ปิดโดยเหตุผล”",
                            "LODGING-PAYMENT-PROBLEM-RESOLVE");
                    await WithPropertyLockAsync(companyId, r.PropertyId, async () =>
                    {
                        var ctx = await LoadContextAsync(companyId, r.PropertyId, r.CheckInDate, r.CheckOutDate, excludeReservationId: r.Id);
                        EnsureRoomsAvailable(ctx, r, "เปิดการจองกลับไม่ได้ · ติดต่อแขกเพื่อเลื่อนวัน หรือคืนเงินแล้วกด “คืนเงินแล้ว”");
                        var cancelledAt = r.CancelledAt;
                        r.Status = LodgingReservationStatus.Pending;
                        r.CancelledAt = null; r.CancellationReason = null;
                        r.HoldExpiresAt = DateTime.UtcNow.AddHours(24);
                        AppendInternal(r, $"เปิดการจองกลับ (ระบบยกเลิกเพราะหมดเวลาถือห้องเมื่อ {cancelledAt?.AddHours(7):dd/MM/yyyy HH:mm} แต่เงินเข้าแล้ว) — {reason} (โดย {userId}) · "
                            + "กด “ยืนยัน + บันทึกรับมัดจำ” เพื่อออกใบมัดจำ");
                        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "PaymentProblemReopen", reason, by = userId }));
                        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
                        await _db.SaveChangesAsync();
                        return true;
                    });
                    break;   // ธงคงไว้จนรับเงินสำเร็จ (ConfirmCoreAsync ปลด)
                case LodgingPaymentProblemResolution.Refunded:
                    if (string.IsNullOrWhiteSpace(request.Reference))
                        throw new BusinessRuleException("ต้องระบุเลขอ้างอิงการโอนคืน/คืนผ่านช่องทางชำระ", "LODGING-PAYMENT-PROBLEM-RESOLVE");
                    if (r.DepositPaid > 0m)
                        throw new BusinessRuleException("การจองนี้มีมัดจำที่ลงบัญชีแล้ว — ใช้ปุ่ม “ยืนยันคืนเงินแล้ว” ของเส้นยกเลิก (ออกใบลดหนี้/JE คืนเงิน)",
                            "LODGING-PAYMENT-PROBLEM-RESOLVE");
                    ClosePaymentProblem(companyId, r, $"คืนเงินแขกแล้ว (อ้างอิง {request.Reference!.Trim()}) — {reason}", "PaymentProblemRefunded", request, userId);
                    await _db.SaveChangesAsync();
                    break;
                case LodgingPaymentProblemResolution.Dismissed:
                    ClosePaymentProblem(companyId, r, $"ปิดเรื่องโดยเหตุผล — {reason}", "PaymentProblemDismissed", request, userId);
                    await _db.SaveChangesAsync();
                    break;
                default:
                    throw new BusinessRuleException("ไม่รู้จักวิธีปิดเรื่องที่เลือก — รีเฟรชหน้าแล้วเลือกใหม่", "LODGING-PAYMENT-PROBLEM-RESOLVE");
            }
            return await MapAsync(companyId, r, true, true);
        });
    }

    /// <summary>ปลดธง "เงินเข้าแต่ยืนยันไม่ได้" พร้อมประวัติ (หมายเหตุ + บันทึกต่อท้ายในช่องธง + audit) — ห้ามล้างธงที่อื่นโดยไม่ผ่านตัวนี้หรือการรับเงินสำเร็จ</summary>
    private void ClosePaymentProblem(Guid companyId, LodgingReservation r, string note, string action,
        LodgingResolvePaymentProblemRequest request, string userId)
    {
        AppendInternal(r, $"ปิดเรื่อง “เงินเข้าแต่ยืนยันไม่ได้”: {note} (โดย {userId}) · ประวัติ: {r.PaymentProblemNote}");
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new
        {
            action, note, resolution = request.Resolution.ToString(), reference = request.Reference,
            problemNote = r.PaymentProblemNote, paymentIntentId = r.PaymentProblemIntentId, by = userId,
        }));
        r.PaymentProblemAt = null; r.PaymentProblemIntentId = null; r.PaymentProblemNote = null;
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>บัญชีขาเงินเข้าของใบมัดจำ เมื่อยืนยันใบที่ "เงินออนไลน์เข้าแต่ยืนยันไม่ได้" ภายหลัง — จากรายการชำระที่ติดธงไว้ (บัญชีพักของ gateway ผ่าน
    /// <c>IGatewayAccountResolver</c> ตัวเดียวกับ handler) · เลขอ้างอิงตั้งต้น = เลขของรายการ · ยอดที่พนักงานกรอกต่างจากยอดรายการ ⇒ หมายเหตุให้เห็น ·
    /// รายการไม่อยู่ในสถานะสำเร็จ/หา resolver ไม่ได้ ⇒ ปฏิเสธ (ห้ามเดาบัญชี)</summary>
    private async Task<(Guid? MoneyIn, LodgingConfirmRequest Request)> ProblemIntentMoneyInAsync(
        Guid companyId, LodgingReservation r, Guid intentId, Guid? requested, LodgingConfirmRequest request)
    {
        var intent = await _db.PaymentIntents.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId && i.SourceId == r.Id
                && i.SourceKind == PaymentSourceKind.LodgingReservation);
        if (intent == null || intent.Status != PaymentIntentStatus.Succeeded)
            throw new BusinessRuleException(
                "รายการชำระออนไลน์ที่ผูกกับธง “เงินเข้าแต่ยืนยันไม่ได้” ไม่อยู่ในสถานะชำระสำเร็จ — ตรวจที่หน้ารายการรับชำระออนไลน์ก่อน "
                + "แล้วปิดเรื่องด้วยปุ่ม “ปิดเรื่องเงินเข้า”", "LODGING-PAYMENT-PROBLEM-INTENT");
        if (_gatewayAccounts == null)
            throw new BusinessRuleException("หาบัญชีพักของช่องทางชำระเงินไม่ได้ — ยืนยันใบนี้ไม่ได้จนกว่าระบบรับชำระจะพร้อม", "LODGING-PAYMENT-PROBLEM-INTENT");
        var moneyIn = await _gatewayAccounts.ResolveMoneyInAccountAsync(intent);
        if (requested != null && requested != moneyIn)
            AppendInternal(r, "บัญชีรับเงินที่ส่งมาถูกแทนด้วยบัญชีของช่องทางชำระออนไลน์ (เงินเข้าผ่านรายการชำระที่ติดธงไว้)");
        var amount = request.DepositAmount ?? Math.Max(0, r.DepositRequired - r.DepositPaid);
        if (amount != intent.Amount)
            AppendInternal(r, $"⚠️ ยอดที่บันทึก {amount:N2} ต่างจากยอดรายการชำระออนไลน์ {intent.Amount:N2} — ตรวจส่วนต่าง");
        var fixedRequest = request with
        {
            PaymentReference = string.IsNullOrWhiteSpace(request.PaymentReference) ? intent.ProviderRef : request.PaymentReference,
            BankAccountId = null,
        };
        return (moneyIn, fixedRequest);
    }

    /// <summary>
    /// <b>ธง "เงินเข้าแต่ยืนยันไม่ได้"</b> (O-P1-4 · คำตัดสินข้อ 127) — เงินออนไลน์เข้าแล้ว/แขกส่งสลิปแล้ว แต่ระบบยืนยันห้องให้ไม่ได้
    /// (ห้องเต็ม · hold หมด · สถานะเปลี่ยน) ⇒ <b>ไม่คืนเงินอัตโนมัติ</b> · ล้มดัง 3 ที่: (1) ตัวการจอง — ธง + หมายเหตุ + audit + ตัวกรองบนหน้าพนักงาน
    /// (2) สถานะงาน — ผู้เรียก (handler ของเงินออนไลน์) โยน error ต่อให้ประวัติของรายการชำระ (3) คำตอบ — หน้าแขกเปลี่ยนเป็น "ได้รับเงินแล้ว ที่พักจะติดต่อกลับ"
    /// + อีเมลแจ้งที่พัก · ระหว่างติดธง การจองยังกันห้องและไม่ถูกยกเลิกอัตโนมัติ (LodgingHoldRule)
    ///
    /// <para>เขียนด้วยการอ่านแถวใหม่ — ไม่บันทึกของค้างจากเส้นที่ล้มมาก่อน (ใบมัดจำ/ยอดที่ยังไม่ครบ)</para>
    /// </summary>
    public async Task FlagPaymentProblemAsync(Guid companyId, Guid reservationId, string reason, string source, Guid? paymentIntentId = null)
    {
        var now = DateTime.UtcNow;
        var line = $"[{now.AddHours(7).ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)}] {reason}";
        var r = await _db.LodgingReservations.FirstOrDefaultAsync(x => x.Id == reservationId && x.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบการจอง");
        r.PaymentProblemAt ??= now;
        if (paymentIntentId is Guid pi) r.PaymentProblemIntentId = pi;   // P1-3ค: บัญชีขาเงินเข้าตอนยืนยันภายหลังมาจากรายการนี้
        r.PaymentProblemNote = string.IsNullOrWhiteSpace(r.PaymentProblemNote) ? line : r.PaymentProblemNote.TrimEnd() + "\n" + line;
        AppendInternal(r, $"⚠️ เงินเข้าแต่ยืนยันการจองไม่ได้ ({source}): {reason} — ไม่คืนเงินอัตโนมัติ · ติดต่อแขกเพื่อเลื่อนวัน/ย้ายห้องแล้วกด “ยืนยัน” หรือคืนเงินตามช่องทางที่รับมา");
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "PaymentArrivedUnconfirmed", reason, source, paymentIntentId }));
        await _db.SaveChangesAsync();
        _logger.LogError("เงินเข้าแต่ยืนยันการจองที่พักไม่ได้ {No} ({Source}): {Reason}", r.ReservationNumber, source, reason);
        await TryNotifyAsync(companyId, r.Id, "payment-problem");   // อีเมลถึงที่อยู่แจ้งเตือนของที่พัก (เสมอ)
        if (_notify != null)
        {
            // เครื่องแจ้งเตือนกลาง (กระดิ่ง/อีเมล/LINE ตามที่บริษัทตั้งใน NotificationSettings) — ล้มต้องไม่กลบธงที่บันทึกแล้ว แต่ต้องดังใน log
            try
            {
                await _notify.DispatchAsync(companyId, Models.Constants.NotificationEvents.LodgingPaymentUnconfirmed, new Accounting.Services.Interfaces.NotificationContext
                {
                    Title = $"เงินเข้าแต่ยืนยันการจองไม่ได้ — {r.ReservationNumber}",
                    Message = $"{r.GuestName}: {reason} — ระบบไม่คืนเงินเอง กรุณาติดต่อแขกเพื่อเลื่อนวัน/ย้ายห้องแล้วกด “ยืนยัน” หรือคืนเงินตามช่องทางที่รับมา",
                    ActionUrl = "/pages/lodging.html",
                    EntityType = "LodgingReservation", EntityId = r.Id,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "แจ้งเตือนเงินเข้าแต่ยืนยันไม่ได้ของการจอง {No} ไม่สำเร็จ (ธงบนการจองบันทึกแล้ว)", r.ReservationNumber);
            }
        }
    }

    /// <summary>ใบเสร็จมัดจำ — Receipt IsDeposit=true (Cr ขายรอรับรู้ 217xx) ยอด = gross ที่รับจริง
    /// · รูปของใบ (VAT บรรทัด + ธง 21913) มาจากวิธีบันทึกมัดจำที่ตัดสินแล้ว (รอบ 193 #34) — DocumentService
    /// ลง JE จากช่องเหล่านี้ตามเส้นเดิม ไม่มีเส้นใหม่ · VatImmediate = หัว "ใบกำกับภาษี/ใบเสร็จรับเงิน"
    /// (เมื่อข้อมูลผู้ซื้อครบ §86/4) → เลขชุด TIV ผ่าน TaxInvoiceSeriesPolicy + ภ.พ.30 เดือนที่รับเงิน</summary>
    private async Task<DocumentResponse> CreateDepositReceiptAsync(Guid companyId, LodgingReservation r, decimal amount, LodgingConfirmRequest req, string userId, Guid? moneyInAccountId = null)
    {
        var prop = r.Property;
        var vatRate = await EffectiveVatRateAsync(companyId, prop);
        // รอบ 194 (spec S4): ประเภทของที่พัก → ค่าเดิมของที่พัก → ประเภทเริ่มต้นบริษัท → ค่าบริษัท → ประเภทธุรกิจ (ตัวตัดสินตัวเดียว)
        var kind = await DepositKindForAsync(companyId, prop);
        var legacyShape = DepositPolicyResolver.ShapeFor(kind.Treatment, vatRate);
        var product = await FirstRoomProductCodeAsync(companyId, r);
        var lines = new List<DocumentLineRequest>
        {
            new(Description: $"มัดจำค่าห้องพัก {prop.Name} — จอง {r.ReservationNumber} ({RoomSummary(r)})",
                Quantity: 1, Unit: "รายการ", UnitPrice: amount, DiscountPercent: 0, VatRate: legacyShape.LineVatRate, WithholdingTaxRate: 0,
                AccountId: null, ProductCode: product)
        };
        // ไม่มีประเภทเลย (บริษัทที่ยังไม่ถูก seed) ⇒ decision = null ⇒ รูปใบเดิมทุกตัวอักษร · มีประเภท ⇒ ตัวจัดรูปตัวเดียว
        // (DocumentService จัดซ้ำจาก DepositKindId ด้วยตัวเดียวกัน — ผลเท่ากัน) · นอกระบบ VAT ⇒ VAT 0 + หมายเหตุ RD-81
        var shaped = DepositDocumentShaping.Apply(lines, kind.KindId is null ? null : kind, vatRate,
            legacyShape.DepositOutputVatDeferred, prop.DepositDeferredAccountCode);
        var request = new CreateDocumentRequest(
            DocumentType: DocumentType.Receipt,
            DocumentDate: req.PaymentDate ?? DateTime.UtcNow,
            DueDate: null,
            ContactId: r.ContactId ?? throw new BusinessRuleException("การจองไม่มีผู้ติดต่อ (Contact) — แก้ไขข้อมูลแขกก่อน"),
            Reference: r.ReservationNumber,
            Notes: $"มัดจำการจองที่พัก {r.ReservationNumber} · เข้าพัก {r.CheckInDate:dd/MM/yyyy}–{r.CheckOutDate:dd/MM/yyyy} ({r.Nights} คืน)",
            Lines: shaped.Lines.ToList(),
            PricesIncludeVat: true,
            BankAccountId: req.BankAccountId,
            // ขา "เงินเข้า" ของใบเสร็จมัดจำ: รับผ่าน gateway → บัญชีพัก 11340
            // (เงินยังไม่เข้าธนาคาร จะเข้า T+n หลังหักค่าธรรมเนียม) · null = ตามเดิม
            PaymentAccountId: moneyInAccountId,
            BranchId: prop.BranchId,
            IsDeposit: true,
            DepositDeferredAccountCode: shaped.DepositDeferredAccountCode,
            DepositOutputVatDeferred: shaped.DepositOutputVatDeferred,
            BookingNumber: r.ReservationNumber,
            PaymentType: null,
            // สัญญาทีม B รอบ 194: ส่ง id ประเภท ⇒ CreateDocumentAsync ตรึงประเภท/ลักษณะ/ชื่อลงใบ + จัดรูปด้วยตัวเดียวกัน
            DepositKindId: kind.KindId);
        // P1 (รอบ 194 regsec): ส่งอัตรา VAT ของที่พักเข้าตัวจัดรูป — ที่พักที่ไม่คิด VAT (ChargeVat=false) คงรูปใบ VAT 0 ไม่เลื่อน
        // (เดิมเซิร์ฟเวอร์จัดซ้ำด้วยอัตราบริษัท ⇒ กลายเป็น "มัดจำเต็มยอด" แล้วตอนริบได้ใบกำกับ 7%) · พารามิเตอร์เมธอด ไม่ใช่ช่องใน request
        var created = await _docService.CreateDocumentAsync(companyId, request, userId, LodgingOrigin, depositChannelVatRate: vatRate);
        // ตรึง "ประเภทไหน · ใช้วิธีไหน · ใครตั้ง" ลงหมายเหตุภายในของใบมัดจำ (โหมดจริงอ่านย้อนจากช่องที่ตรึงบนใบ —
        // DepositPolicyResolver.OfDocument) + คำเตือนตามลักษณะเงิน (ราคา × เลื่อน VAT = ขัด §78/1)
        var depDoc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == created.Id && d.CompanyId == companyId);
        var posted = (depDoc != null ? DepositPolicyResolver.OfDocument(true, depDoc.VatAmount, depDoc.DepositOutputVatDeferred) : null)
            ?? kind.Treatment;
        if (depDoc != null)
            depDoc.InternalNotes = DepositPolicyResolver.AppendNoteOnce(depDoc.InternalNotes,
                $"[วิธีบันทึกมัดจำ] {kind.Name} ({NatureLabelTh(kind.Nature)}) · {DepositPolicyResolver.LabelOf(posted)} · ที่มา: {KindSourceTh(kind.Source)} · "
                + DepositPolicyResolver.DescribePosting(posted, amount, vatRate)
                + (kind.Warning != null ? $" · {kind.RuleCode}: {kind.Warning}" : ""));
        await _db.SaveChangesAsync();   // บันทึกหมายเหตุก่อนอนุมัติ — ขั้นอนุมัติอาจ reload เอกสารจาก DB
        // ฝ่ายค้านรอบ 201 รอบสาม P2-6: ไม่มีคนเห็นคำเตือนในเส้นนี้ ⇒ SystemWorkflow — ผ่านเหมือนเดิม (ไม่หยุดการออกเอกสาร) แต่ร่องรอยบอกตามจริงว่าไม่ใช่คนรับทราบ (เดิม acknowledgeWarnings: true = ประทับ AcknowledgedByPerson)
        var approvedDeposit = await _docService.ApproveDocumentAsync(companyId, created.Id, userId, ApprovalAckSource.SystemWorkflow, withAiHints: false);
        if (kind.Warning != null)
            AppendInternal(r, $"มัดจำ {amount:N2} บันทึกแบบ \"{DepositPolicyResolver.LabelOf(posted)}\" ({kind.Name}) — {kind.Warning}");
        // audit: ประเภทไหน · ใช้โหมดไหน · ใครตั้ง · ยอด JE ที่คาดหวังของโหมดนั้น (ให้ผู้ตรวจเทียบกับ JE จริงของใบได้)
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Create, r, new
        {
            action = "DepositReceipt", document = approvedDeposit.DocumentNumber,
            kind = kind.Code, nature = kind.Nature.ToString(),
            treatment = posted.ToString(), source = kind.Source.ToString(), ruleCode = kind.RuleCode,
            expectedPosting = DepositPolicyResolver.PreviewReceipt(posted, amount, vatRate), by = userId,
        }));
        return created;
    }

    /// <summary>ใบมัดจำทุกใบของการจองนี้ (เรียงเก่า→ใหม่) — หาจากเลขจองที่ประทับบนใบ (<c>BookingNumber</c>) +
    /// <c>DepositDocumentId</c> เดิม · เดิมเก็บแค่ใบแรก ⇒ รับชำระเพิ่มรอบสองแล้วเช็คเอาต์ หักมัดจำรวมแต่ชี้ใบเดียว ·
    /// tenant-safe (CompanyId) · ตัดใบร่าง/ยกเลิก
    /// <para>⚠️ รอบ 194: คืน<b>ทุกใบ</b> รวมเงินประกันความเสียหาย (เลขจองเดียวกัน) พร้อมลักษณะเงินที่ตรึงบนใบ — เส้นมัดจำค่าห้อง
    /// (เช็คเอาต์/ยกเลิก/คืนเงิน) ต้องกรองด้วย <see cref="LodgingDepositSettlement.RoomDeposits"/> ทุกจุด · เส้นเงินประกันใช้
    /// <see cref="LodgingDepositSettlement.SecurityDeposit"/> (ล็อกด้วย tools/required_call_site_check.py)</para></summary>
    private async Task<List<LodgingDepositSnapshot>> LoadDepositSnapshotsAsync(Guid companyId, LodgingReservation r)
    {
        var firstId = r.DepositDocumentId ?? Guid.Empty;
        var rows = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.IsDeposit && !d.IsDeleted
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                && (d.Id == firstId || (d.BookingNumber == r.ReservationNumber && d.OriginModule == LodgingOrigin)))
            .OrderBy(d => d.DocumentDate).ThenBy(d => d.CreatedAt)
            .Select(d => new LodgingDepositSnapshot(d.Id, d.DocumentNumber, d.ContactId,
                d.SubTotal, d.VatAmount, d.TotalAmount,
                d.DepositOutputVatDeferred && d.DepositOutputVatRecognizedAt == null,
                d.DepositRealizedAmount, d.DepositRefundedAmount, d.DepositAppliedToDocumentId, d.DepositNature))
            .ToListAsync();
        return rows;
    }

    /// <summary>สถานะ VAT ของบริษัท + อัตราที่ที่พักนี้ใช้ — ตัวอ่านตัวเดียว (<c>CompanyVatStatus</c> ของทีม V ·
    /// ไม่จด VAT = 0 เสมอ §90/2)</summary>
    private async Task<(bool Registered, decimal PropertyRate)> VatProfileAsync(Guid companyId, LodgingProperty prop)
    {
        var (registered, rate) = await CompanyVatStatus.ProfileAsync(_db, companyId);
        return (registered, LodgingPricingEngine.PropertyVatRate(prop.ChargeVat, registered, rate));
    }

    private async Task<string?> FirstRoomProductCodeAsync(Guid companyId, LodgingReservation r)
    {
        var rtId = r.Rooms.Select(x => x.RoomTypeId).FirstOrDefault();
        if (rtId == Guid.Empty) return null;
        var pid = await _db.LodgingRoomTypes.AsNoTracking().Where(x => x.Id == rtId && x.CompanyId == companyId).Select(x => x.ProductId).FirstOrDefaultAsync();
        return pid == null ? null : await _db.Products.AsNoTracking().Where(p => p.Id == pid && p.CompanyId == companyId).Select(p => p.Code).FirstOrDefaultAsync();
    }

    private static string RoomSummary(LodgingReservation r)
        => string.Join(" · ", r.Rooms.GroupBy(x => x.RoomTypeName).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key));

    private static void AppendInternal(LodgingReservation r, string note)
    {
        var stamp = DateTime.UtcNow.AddHours(7).ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var line = $"[{stamp}] {note.Trim()}";
        r.InternalNotes = string.IsNullOrWhiteSpace(r.InternalNotes) ? line : r.InternalNotes.TrimEnd() + "\n" + line;
    }

    private static string StatusTh(LodgingReservationStatus s) => LodgingAmounts.StatusLabel(s, 0m, 0m);

    // ═══════════════════════════ Assign / check-in ═══════════════════════════

    public async Task<LodgingReservationResponse> AssignUnitAsync(Guid companyId, Guid reservationId, LodgingAssignUnitRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        if (Terminal.Contains(r.Status)) throw new BusinessRuleException("การจองสิ้นสุดแล้ว");
        // O-P0-1 รอบ 202: ตรวจ "ห้องนี้ถูกจัดให้ใบอื่นแล้วไหม" + บันทึก ใต้ล็อกที่พัก — เดิมสองแท็บจัดห้องเดียวกันให้สองใบพร้อมกันได้
        await WithPropertyLockAsync(companyId, r.PropertyId, async () =>
        {
            await AssignCoreAsync(companyId, r, request.ReservationRoomId, request.UnitId);
            r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return true;
        });
        return await MapAsync(companyId, r, true, true);
    }

    private async Task AssignCoreAsync(Guid companyId, LodgingReservation r, Guid reservationRoomId, Guid? unitId)
    {
        var room = r.Rooms.FirstOrDefault(x => x.Id == reservationRoomId) ?? throw new KeyNotFoundException("ไม่พบห้องในการจอง");
        if (unitId == null) { room.UnitId = null; room.Unit = null; return; }
        var unit = await _db.LodgingUnits.Include(u => u.RoomType).FirstOrDefaultAsync(u => u.Id == unitId && u.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบหมายเลขห้อง");
        if (unit.RoomType.PropertyId != r.PropertyId) throw new BusinessRuleException("ห้องนี้ไม่ได้อยู่ในที่พักเดียวกัน");
        if (unit.RoomTypeId != room.RoomTypeId) AppendInternal(r, $"จัดห้อง {unit.Number} ({unit.RoomType.Name}) ให้การจองประเภท {room.RoomTypeName} — upgrade/ย้ายประเภท");
        if (unit.IsOutOfService && (unit.OutOfServiceUntil == null || unit.OutOfServiceUntil >= r.CheckInDate))
            throw new BusinessRuleException($"ห้อง {unit.Number} ปิดซ่อม/ปิดใช้งานอยู่");
        if (await UnitTakenByOtherAsync(companyId, r, unit.Id))
            throw new BusinessRuleException($"ห้อง {unit.Number} ถูกจัดให้การจองอื่นในช่วงเดียวกันแล้ว (หรือแขกเดิมยังไม่เช็คเอาต์)");
        room.UnitId = unit.Id; room.Unit = unit;
    }

    /// <summary>หมายเลขห้องนี้ถูกการจองอื่นใช้ในช่วง [เช็คอิน, เช็คเอาต์) ของ <paramref name="r"/> ไหม — กติกาห้องชนตัวเดียว
    /// (<see cref="LodgingHoldRule.OccupiesUnit"/>: ยังกันห้อง + ทับช่วง · แขกเช็คอินค้างหลังวันออกนับว่ายังอยู่ · สลิป/เงินค้างนับว่ากัน) ·
    /// ผู้เรียก: จัดห้อง · เช็คอินก่อนวันจองที่ขยายคืน (ฝ่ายค้านรอบ 202 P1-1)</summary>
    private async Task<bool> UnitTakenByOtherAsync(Guid companyId, LodgingReservation r, Guid unitId)
    {
        var now = DateTime.UtcNow; var todayThai = now.AddHours(7).Date;
        var others = await _db.LodgingReservationRooms.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.UnitId == unitId && x.ReservationId != r.Id
                && (x.Reservation.Status == LodgingReservationStatus.Confirmed || x.Reservation.Status == LodgingReservationStatus.CheckedIn
                    || x.Reservation.Status == LodgingReservationStatus.Pending)
                && x.Reservation.CheckInDate < r.CheckOutDate)
            .Select(x => new
            {
                x.Reservation.Status, x.Reservation.CheckInDate, x.Reservation.CheckOutDate, x.Reservation.HoldExpiresAt,
                x.Reservation.DepositPaid, x.Reservation.SlipUploadedAt, x.Reservation.PaymentProblemAt, x.Reservation.CheckedOutAt,
            }).ToListAsync();
        return others.Any(o => LodgingHoldRule.OccupiesUnit(
            new LodgingHoldFacts(o.Status, o.HoldExpiresAt, o.DepositPaid, o.SlipUploadedAt != null, o.PaymentProblemAt != null),
            o.CheckInDate, o.CheckOutDate, o.CheckedOutAt, r.CheckInDate, r.CheckOutDate, now, todayThai));
    }

    /// <summary>ฝ่ายค้านรอบ 202 P1-1: เช็คอินก่อนวันจองขยายคืนวันนี้แล้ว ⇒ หมายเลขห้องที่จัดไว้ล่วงหน้าต้องว่างคืนนั้นด้วย (เดิมตรวจแค่จำนวนห้องต่อประเภท ⇒
    /// ห้อง 101 ที่จัดให้ใบนี้ไว้ ซ้อนแขกอีกใบที่ยังพักคืนนี้) · ปิดซ่อมคืนนั้นก็ไม่ได้ · ชน ⇒ ปฏิเสธพร้อมทางไปต่อ (ย้ายห้อง)</summary>
    private async Task EnsureAssignedUnitsFreeAsync(Guid companyId, LodgingReservation r)
    {
        foreach (var room in r.Rooms.Where(x => x.UnitId != null))
        {
            var unit = room.Unit ?? await _db.LodgingUnits.AsNoTracking().FirstOrDefaultAsync(u => u.Id == room.UnitId && u.CompanyId == companyId);
            var number = unit?.Number ?? "?";
            if (unit != null && unit.IsOutOfService && (unit.OutOfServiceUntil == null || unit.OutOfServiceUntil >= r.CheckInDate))
                throw new BusinessRuleException($"ห้อง {number} ปิดซ่อมคืนวันที่ {r.CheckInDate:dd/MM/yyyy} — เลือกหมายเลขห้องอื่นในหน้ารายละเอียดก่อนเช็คอินก่อนกำหนด",
                    "LODGING-EARLY-CHECKIN-UNIT");
            if (await UnitTakenByOtherAsync(companyId, r, room.UnitId!.Value))
                throw new BusinessRuleException(
                    $"ห้อง {number} ที่จัดไว้ยังมีแขกอื่นพัก/ถูกจองคืนวันที่ {r.CheckInDate:dd/MM/yyyy} — ย้ายห้อง (เลือกหมายเลขห้องใหม่ในหน้ารายละเอียด) "
                    + "แล้วกดเช็คอินอีกครั้ง หรือเช็คอินในวันที่จอง", "LODGING-EARLY-CHECKIN-UNIT");
        }
    }

    public async Task<LodgingReservationResponse> CheckInAsync(Guid companyId, Guid reservationId, LodgingCheckInRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        if (r.Status == LodgingReservationStatus.CheckedIn) throw new BusinessRuleException("เช็คอินไปแล้ว");
        if (r.Status != LodgingReservationStatus.Confirmed && r.Status != LodgingReservationStatus.Pending)
            throw new BusinessRuleException($"การจองอยู่ในสถานะ {StatusTh(r.Status)} — เช็คอินไม่ได้");
        if (r.Status == LodgingReservationStatus.Pending && r.DepositRequired > 0 && r.DepositPaid <= 0)
            throw new BusinessRuleException("ยังไม่ได้ยืนยันการจอง/รับมัดจำ — กด \"ยืนยัน\" ก่อน (หรือรับชำระตอนเช็คอิน)");
        // ฝ่ายค้านรอบ 202 P1-1 (ข้อ 128): ยืนยันเพราะสลิปแต่ยังไม่บันทึกรับเงิน ⇒ เช็คอินไม่ได้ (เดิมเช็คอินได้แล้วหลุดจากทุกคิว)
        if (LodgingGuestConfirmPolicy.CheckInProblem(r.Status, r.ConfirmedBy, r.DepositPaid) is string slipProblem)
            throw new BusinessRuleException(slipProblem, LodgingGuestConfirmPolicy.CheckInRuleCode);
        var today = DateTime.UtcNow.AddHours(7).Date;
        if (r.CheckInDate.Date > today.AddDays(1)) throw new BusinessRuleException($"วันเช็คอินคือ {r.CheckInDate:dd/MM/yyyy} — เช็คอินก่อนกำหนดไม่ได้ (เลื่อนวันก่อน)");
        // O-P0-1 รอบ 202: จัดห้อง + เช็คอิน ใต้ล็อกที่พัก (ห้องเดียวกันต้องไม่ถูกเช็คอินให้สองใบพร้อมกัน)
        await WithPropertyLockAsync(companyId, r.PropertyId, async () =>
        {
            // ฝ่ายค้าน P2-2: อ่านแถวใหม่ใต้ล็อก — ปฏิเสธสลิป/ยืนยันจากสลิปที่เกิดระหว่างนี้ต้องถูกเห็น (ด่านสถานะ/สลิปตรวจซ้ำ)
            await _db.Entry(r).ReloadAsync();
            if (r.Status != LodgingReservationStatus.Confirmed && r.Status != LodgingReservationStatus.Pending)
                throw new BusinessRuleException($"การจองอยู่ในสถานะ {StatusTh(r.Status)} — เช็คอินไม่ได้", "LODGING-STATE-CHANGED");
            if (r.Status == LodgingReservationStatus.Pending && r.DepositRequired > 0 && r.DepositPaid <= 0)
                throw new BusinessRuleException("ยังไม่ได้ยืนยันการจอง/รับมัดจำ — กด \"ยืนยัน\" ก่อน (หรือรับชำระตอนเช็คอิน)");
            if (LodgingGuestConfirmPolicy.CheckInProblem(r.Status, r.ConfirmedBy, r.DepositPaid) is string slipProblemLocked)
                throw new BusinessRuleException(slipProblemLocked, LodgingGuestConfirmPolicy.CheckInRuleCode);
            // คำตัดสินเจ้าของข้อ 125: เช็คอินก่อนวันจอง 1 วัน = เพิ่ม 1 คืน (คืนวันนี้) — ต้องมีห้องว่าง · ราคาคืนนั้นจาก engine · บันทึกประวัติ
            // (เดิมยอมให้เช็คอินล่วงหน้าโดยไม่คิดคืนนั้นและไม่ตรวจห้องว่าง ⇒ ห้องคืนนั้นอาจถูกขายซ้อน + พักฟรี 1 คืน)
            var extendedEarly = r.CheckInDate.Date == today.AddDays(1);
            if (extendedEarly)
                await ExtendStayOneNightEarlierAsync(companyId, r, userId);
            foreach (var a in request.Assignments ?? new()) await AssignCoreAsync(companyId, r, a.ReservationRoomId, a.UnitId);
            // ฝ่ายค้าน P1-1: ห้องที่จัดไว้ก่อนขยายคืนต้องว่างคืนที่เพิ่มด้วย (ตรวจหลังรับการย้ายห้องที่ส่งมาในคำขอ) — ใต้ล็อกเดียวกัน
            if (extendedEarly) await EnsureAssignedUnitsFreeAsync(companyId, r);
            var unassigned = r.Rooms.Where(x => x.UnitId == null).ToList();
            if (unassigned.Count > 0)
                throw new BusinessRuleException($"ยังไม่ได้จัดหมายเลขห้องให้ {unassigned.Count} ห้อง ({string.Join(", ", unassigned.Select(x => x.RoomTypeName))})");
            if (r.Property.RequireGuestIdNumber && string.IsNullOrWhiteSpace(request.GuestIdNumber ?? r.GuestIdNumber))
                throw new BusinessRuleException("ต้องบันทึกเลขบัตรประชาชน/พาสปอร์ตของผู้เข้าพักก่อนเช็คอิน");
            if (!string.IsNullOrWhiteSpace(request.GuestIdNumber)) r.GuestIdNumber = request.GuestIdNumber.Trim();
            if (!string.IsNullOrWhiteSpace(request.GuestNationality)) r.GuestNationality = request.GuestNationality;

            r.Status = LodgingReservationStatus.CheckedIn; r.CheckedInAt = DateTime.UtcNow; r.HoldExpiresAt = null;
            r.ConfirmedAt ??= DateTime.UtcNow; r.ConfirmedBy ??= userId;
            foreach (var room in r.Rooms.Where(x => x.Unit != null)) room.Unit!.HousekeepingStatus = LodgingHousekeepingStatus.Occupied;

            // ── ค่าเช็คอินก่อนเวลา (LDG-P2-06) ──
            // `EarlyCheckInFee` มีคอลัมน์ + หน้าตั้งค่ามาตั้งแต่รอบ 124 แต่**ไม่มีใครอ่าน**
            // (จดไว้ใน LODGING_TAKETIME_ANALYSIS §2) ⇒ ที่พักตั้งค่าไว้แล้วไม่เคยเก็บได้เลย
            // พนักงานเป็นคนติ๊ก ไม่ใช่ระบบเก็บเอง — ห้องอาจว่างอยู่แล้วและหลายที่ยกเว้นให้
            if (request.ChargeEarlyCheckIn && r.Property.EarlyCheckInFee > 0)
                await AddChargeCoreAsync(companyId, r, new LodgingAddChargeRequest(
                    Description: $"ค่าเช็คอินก่อนเวลา (ก่อน {Time(r.Property.CheckInTime)} น.)",
                    Quantity: 1, UnitPrice: r.Property.EarlyCheckInFee,
                    Source: LodgingChargeSource.Manual), userId);

            if (!string.IsNullOrWhiteSpace(request.Note)) AppendInternal(r, request.Note);
            r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "CheckIn", units = r.Rooms.Select(x => x.Unit?.Number), by = userId }));
            await _db.SaveChangesAsync();
            return true;
        });
        return await MapAsync(companyId, r, true, true);
    }

    /// <summary>
    /// <b>เช็คอินก่อนวันจอง 1 วัน = เพิ่มคืนวันนี้</b> (คำตัดสินเจ้าของข้อ 125 · รอบ 202) — เรียกใต้ล็อกที่พักจากเส้นเช็คอิน
    ///
    /// <para>ตรวจห้องว่างคืน [วันนี้, วันเช็คอินเดิม) ด้วยตัวนับตัวเดียว · ไม่ว่าง = ปฏิเสธพร้อมบอกว่าเช็คอินได้วันจริง ·
    /// ราคา = engine (ราคาวันจริงของคืนนั้น · แผนราคาเดิมของการจอง) <b>เฉพาะคืนที่เพิ่ม</b> — คืนเดิมคงราคาที่ตกลงไว้ (ไม่คิดใหม่ทั้งทริปแบบเลื่อนวัน) ·
    /// บริการเสริมแบบต่อคืนบวก 1 คืน · service charge/VAT คิดจากส่วนเพิ่มด้วยสูตรเดียวกับตอนจอง · มัดจำที่ตกลงไว้ไม่เปลี่ยน (ส่วนเพิ่มเก็บตอนเช็คเอาต์)</para>
    /// </summary>
    private async Task ExtendStayOneNightEarlierAsync(Guid companyId, LodgingReservation r, string userId)
    {
        var oldIn = r.CheckInDate;
        var newIn = oldIn.AddDays(-1);
        var ctx = await LoadContextAsync(companyId, r.PropertyId, newIn, oldIn, excludeReservationId: r.Id);
        var now = DateTime.UtcNow;
        foreach (var g in r.Rooms.GroupBy(x => x.RoomTypeId))
        {
            var avail = LodgingAvailability.AvailableRooms(newIn, oldIn, ctx.UnitsByRoomType.GetValueOrDefault(g.Key),
                ctx.Property.OverbookingAllowance, ctx.BookedFor(g.Key), ctx.OverridesFor(g.Key), now);
            if (avail < g.Count())
                throw new BusinessRuleException(
                    $"เช็คอินก่อนวันจอง = เพิ่ม 1 คืน (คืนวันที่ {newIn:dd/MM/yyyy}) แต่{g.First().RoomTypeName}คืนนั้นว่างไม่พอ ({avail}/{g.Count()}) — "
                    + $"เช็คอินได้ในวันที่จอง {oldIn:dd/MM/yyyy}", "LODGING-EARLY-CHECKIN-FULL");
        }
        var plan = r.RatePlanId is Guid planId ? ctx.RatePlans.FirstOrDefault(p => p.Id == planId) : null;
        decimal roomDelta = 0m; var guests = 0;
        Dictionary<Guid, decimal>? keptExtra = null;
        foreach (var room in r.Rooms)
        {
            var rt = ctx.RoomTypes.FirstOrDefault(x => x.Id == room.RoomTypeId)
                ?? throw new BusinessRuleException($"ประเภทห้อง {room.RoomTypeName} ถูกปิดขายแล้ว — คิดราคาคืนเพิ่มไม่ได้ · เช็คอินได้ในวันที่จอง {oldIn:dd/MM/yyyy}",
                    "LODGING-EARLY-CHECKIN-FULL");
            var roomInput = ctx.InputFor(rt, plan);
            // ฝ่ายค้านรอบ 202 P2-4/P2-5: คนเสริมที่ซื้อไว้แล้วแต่ตอนนี้ที่พักปิด/ไม่มีราคาเตียงเสริม ⇒ คืนที่เพิ่มคิดราคาคนเสริมเดิมของการจอง (ไม่ฟรี · ไม่เดา)
            if (room.ExtraBeds > 0 && !LodgingOccupancy.SellsExtraBeds(rt.AllowExtraBed, rt.MaxExtraBeds, rt.ExtraBedPrice))
            {
                if (!(keptExtra ??= KeptExtraBedPrices(r)).TryGetValue(rt.Id, out var keptPrice))
                    throw new BusinessRuleException($"ห้อง {room.RoomTypeName} มีคนเสริม แต่ที่พักปิด/ไม่ได้ตั้งราคาเตียงเสริมแล้ว และหาราคาเดิมของการจองไม่ได้ — "
                        + $"คิดคืนเพิ่มไม่ได้ · เช็คอินได้ในวันที่จอง {oldIn:dd/MM/yyyy}", "LODGING-EARLY-CHECKIN-FULL");
                roomInput = roomInput with { ExtraBedPrice = keptPrice };
            }
            var q = LodgingPricingEngine.QuoteRoom(newIn, oldIn, room.Adults, room.Children, room.ExtraBeds, roomInput);
            room.Subtotal += q.Subtotal;
            room.NightlyRatesJson = J(NightsDto(q.Nights).Concat(ParseNights(room.NightlyRatesJson)).ToList());
            roomDelta += q.Subtotal;
            guests += LodgingOccupancy.ChargeableGuests(room.Adults, room.Children, room.ExtraBeds);
        }
        decimal extrasDelta = 0m;
        foreach (var e in r.Extras.Where(e => e.PriceMode is LodgingExtraPriceMode.PerNight or LodgingExtraPriceMode.PerPersonPerNight))
        {
            var d = LodgingPricingEngine.ExtraTotal(new LodgingExtraInput(e.Name, e.PriceMode, e.UnitPrice, e.Quantity), 1, guests);
            e.Total += d; extrasDelta += d;
        }
        var delta = LodgingPricingEngine.Totals(roomDelta, extrasDelta, 0m, ctx.Property.ServiceChargePercent, ctx.VatRate,
            ctx.Property.PricesIncludeVat, 0m, null, 0m, null);
        var oldTotal = r.TotalAmount;
        r.RoomSubtotal += delta.RoomSubtotal; r.ExtrasTotal += delta.ExtrasTotal;
        r.ServiceChargeAmount += delta.ServiceChargeAmount; r.VatAmount += delta.VatAmount; r.TotalAmount += delta.TotalAmount;
        r.CheckInDate = newIn; r.Nights += 1;
        AppendInternal(r, $"เช็คอินก่อนวันจอง 1 วัน — เพิ่มคืนวันที่ {newIn:dd/MM/yyyy} (ห้อง {roomDelta:N2} · บริการต่อคืน {extrasDelta:N2}) · "
            + $"เข้าพัก {oldIn:dd/MM/yyyy} → {newIn:dd/MM/yyyy} · ยอด {oldTotal:N2} → {r.TotalAmount:N2} (โดย {userId})");
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new
        {
            action = "EarlyCheckInExtraNight", oldCheckIn = oldIn, newCheckIn = newIn, roomDelta, extrasDelta, totalDelta = delta.TotalAmount, by = userId,
        }));
    }

    // ═══════════════════════════ Folio ═══════════════════════════

    public async Task<LodgingReservationResponse> AddChargeAsync(Guid companyId, Guid reservationId, LodgingAddChargeRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        await AddChargeCoreAsync(companyId, r, request, userId);
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    /// <summary>เพิ่มรายการ folio 1 บรรทัด — **ไม่ SaveChanges และไม่ map**
    ///
    /// <para>แยกออกมาเพื่อให้เส้นอื่น (ค่าเช็คอินก่อนเวลา/เช็คเอาต์ช้า) ใช้
    /// <b>ตรรกะเดียวกัน</b> ได้ในธุรกรรมเดียว — ไม่ใช่คัดลอกสูตรคิดยอด/VAT
    /// ไปไว้ที่สอง (defect class "สำเนามือที่ drift")</para></summary>
    private async Task AddChargeCoreAsync(Guid companyId, LodgingReservation r,
        LodgingAddChargeRequest request, string userId)
    {
        r.Charges.Add(await BuildChargeAsync(companyId, r, request, userId));
        RecalcFolio(r);
    }

    /// <summary>สร้างรายการ folio 1 บรรทัด (ตรวจ + อัตรา VAT ผ่านด่าน §90/2) โดย<b>ยังไม่ผูก</b>กับการจอง — เช็คเอาต์ใช้ตัวนี้แล้ว
    /// ผูกหลังออกใบสำเร็จ (C5: เดิมผูกก่อน ⇒ ด่านที่ throw ภายหลังทำให้กดใหม่แล้วค่าเสียหายซ้ำ)</summary>
    private async Task<LodgingFolioCharge> BuildChargeAsync(Guid companyId, LodgingReservation r,
        LodgingAddChargeRequest request, string userId)
    {
        if (r.Status is LodgingReservationStatus.CheckedOut or LodgingReservationStatus.Cancelled or LodgingReservationStatus.NoShow)
            throw new BusinessRuleException("การจองสิ้นสุดแล้ว — เพิ่มรายการไม่ได้ (ออกเอกสารแยกแทน)");
        if (string.IsNullOrWhiteSpace(request.Description)) throw new BusinessRuleException("กรุณาระบุรายการ");
        if (request.Quantity <= 0) throw new BusinessRuleException("จำนวนต้องมากกว่า 0");
        if (request.UnitPrice < 0) throw new BusinessRuleException("ราคาต้องไม่ติดลบ");
        // C8 รอบ 193 — อัตราที่ผู้ใช้พิมพ์ต้องผ่านด่าน §90/2 ด้วย (เดิม request.VatRate ข้ามด่าน ⇒ บริษัทไม่จด VAT ได้บรรทัด 7%)
        var (registered, propRate) = await VatProfileAsync(companyId, r.Property);
        var vat = LodgingPricingEngine.ChargeVatRate(request.VatRate, registered, propRate);
        return new LodgingFolioCharge
        {
            CompanyId = companyId, ReservationId = r.Id, ProductId = request.ProductId, Description = request.Description.Trim(),
            Quantity = request.Quantity, UnitPrice = request.UnitPrice,
            Total = Math.Round(request.Quantity * request.UnitPrice, 2, MidpointRounding.AwayFromZero),
            VatRate = vat, Source = request.Source, Status = LodgingChargeStatus.Pending, ChargedAt = DateTime.UtcNow, Notes = request.Notes, CreatedBy = userId,
        };
    }

    public async Task<LodgingReservationResponse> CancelChargeAsync(Guid companyId, Guid reservationId, Guid chargeId, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        var c = r.Charges.FirstOrDefault(x => x.Id == chargeId) ?? throw new KeyNotFoundException("ไม่พบรายการ");
        if (c.Status == LodgingChargeStatus.Paid) throw new BusinessRuleException("รายการนี้ออกเอกสารแล้ว — ใช้ใบลดหนี้แทน");
        c.Status = LodgingChargeStatus.Cancelled; c.UpdatedBy = userId; c.UpdatedAt = DateTime.UtcNow;
        RecalcFolio(r);
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    private static void RecalcFolio(LodgingReservation r)
        => r.FolioTotal = Math.Round(r.Charges.Where(c => c.Status != LodgingChargeStatus.Cancelled).Sum(c => c.Total), 2, MidpointRounding.AwayFromZero);

    // ═══════════════════════════ Check-out ═══════════════════════════

    public async Task<LodgingReservationResponse> CheckOutAsync(Guid companyId, Guid reservationId, LodgingCheckOutRequest request, string userId)
    {
        return await ExclusiveCheckoutAsync(companyId, reservationId, () => CheckOutCoreAsync(companyId, reservationId, request, userId));
    }

    /// <summary>ล็อกต่อการจอง (ข้ามเครื่องได้) ของเส้นเช็คเอาต์และออกใบเช็คเอาต์ใหม่ (รอบ 201 ฝ่ายค้าน X7) — คีย์คงที่
    /// <c>AdvisoryLockKey.For(บริษัท, LodgingCheckout, idการจอง)</c> · session lock เพราะเส้นออกเอกสารเปิดธุรกรรมของตัวเองหลายขั้น ·
    /// ถืออยู่ = ปฏิเสธดังพร้อมทางไปต่อ (ไม่รอ — กดซ้ำสองแท็บคือสาเหตุหลัก)</summary>
    private async Task<LodgingReservationResponse> ExclusiveCheckoutAsync(Guid companyId, Guid reservationId,
        Func<Task<LodgingReservationResponse>> work)
    {
        LodgingReservationResponse? result = null;
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.LodgingCheckout, reservationId.ToString(),
            async () => { result = await work(); }, _logger, companyId);
        if (!ran || result == null)
            throw new BusinessRuleException("มีผู้ใช้อื่นกำลังเช็คเอาต์/ออกใบเช็คเอาต์ของการจองนี้อยู่ — รอสักครู่แล้วเปิดการจองดูใหม่",
                "LODGING-CHECKOUT-BUSY");
        return result;
    }

    private async Task<LodgingReservationResponse> CheckOutCoreAsync(Guid companyId, Guid reservationId, LodgingCheckOutRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        // รอบ 202 (O-P0-2): แถวที่ night audit รุ่นก่อนประทับ "เช็คเอาต์แล้ว" เองโดยไม่ได้ออกบิล ⇒ ออกใบเช็คเอาต์ย้อนหลังผ่านเส้นนี้ได้หนึ่งครั้ง
        // (ใบออกแล้วประทับ FinalDocumentId ⇒ ไม่ตรงเงื่อนไขอีก) · ตัวตัดสินเดียว LodgingOverdueRule.IsLegacyAutoCheckout
        var legacyAutoClosed = LodgingOverdueRule.IsLegacyAutoCheckout(r.Status, r.FinalDocumentId, r.InternalNotes,
            r.Property.AccountingMode == LodgingAccountingMode.Off);
        if (r.Status != LodgingReservationStatus.CheckedIn && !legacyAutoClosed)
            throw new BusinessRuleException($"ต้องเช็คอินก่อนจึงเช็คเอาต์ได้ (สถานะปัจจุบัน {StatusTh(r.Status)})");
        if (legacyAutoClosed)
        {
            // ฝ่ายค้านรอบ 202 P1-4: วันใช้บริการ (tax point §78/1 · ServiceUsedDate = วันเช็คเอาต์เดิม) อยู่ในเดือนที่ยื่น ภ.พ.30 แล้ว ⇒ คนต้องรับทราบเอง
            // (ใบนี้ต้องยื่นแบบเพิ่มเติม) — เดิมอนุมัติแบบ SystemWorkflow ทำให้คำเตือนผ่านเงียบ · ตัดสินก่อนแตะข้อมูลใด ๆ
            var vatPeriodFiled = await VatPeriodFiledAsync(companyId, r.CheckOutDate);
            if (LodgingOverdueRule.BackfillVatAckProblem(vatPeriodFiled, request.AcknowledgeFiledVatPeriod, r.CheckOutDate) is string vatAckProblem)
                throw new BusinessRuleException(vatAckProblem, "LODGING-BACKFILL-VAT-FILED");
            // พนักงานกดออกใบย้อนหลัง = เปิดการเข้าพักกลับเข้าเส้นเช็คเอาต์ปกติ (การกระทำของคน · audit) — CheckedOutAt เดิมของ job คงไว้
            // เป็นตัวบอก "เปิดกลับจากแถวรุ่นเก่า" (LodgingOverdueRule.IsReopenedLegacy) ⇒ ขั้นปิดไม่แตะสถานะห้อง/งานแม่บ้านปัจจุบัน
            // (ห้องอาจมีแขกใหม่แล้ว) · ล้มก่อนออกใบ = คืนสถานะเดิม (P2-3 ด้านล่าง) · ล้มหลังออกใบ = กดเช็คเอาต์ซ้ำทำต่อ (ResumeCheckOutAsync)
            r.Status = LodgingReservationStatus.CheckedIn;
            AppendInternal(r, $"เปิดการเข้าพักที่ระบบรุ่นก่อนปิดเองโดยยังไม่ได้ออกบิล เพื่อออกใบเช็คเอาต์ย้อนหลัง (โดย {userId})"
                + (vatPeriodFiled ? $" · รับทราบแล้วว่าเดือน {r.CheckOutDate:MM/yyyy} ยื่น ภ.พ.30 แล้ว ต้องยื่นแบบเพิ่มเติม" : ""));
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new
            {
                action = "ReopenLegacyAutoCheckout", checkedOutAtByJob = r.CheckedOutAt, vatPeriodFiled,
                vatFiledAcknowledgedBy = vatPeriodFiled ? userId : null, by = userId,
            }));
        }
        try
        {
            var prop = r.Property;
            // ออกใบสุดท้ายไปแล้วแต่ขั้นใช้มัดจำล้ม (การจองยังเช็คอินอยู่) — กดเช็คเอาต์อีกครั้ง = ทำขั้นที่ค้างต่อ ไม่ออกใบใหม่ (C2)
            if (r.FinalDocumentId is Guid pendingFinal)
                return await ResumeCheckOutAsync(companyId, r, pendingFinal, request, userId);
            // โหมด "ไม่ออกเอกสาร" — ปิดการเข้าพักให้จบงานหน้าเคาน์เตอร์ แล้วนับมิเตอร์
            // เท่ากับโหมดปกติ (ลูกค้าเลือกทิ้งมูลค่าส่วนเอกสารเอง ไม่ใช่ได้ใช้ฟรี)
            if (prop.AccountingMode == LodgingAccountingMode.Off)
                return await CheckOutWithoutDocumentAsync(companyId, r, request, userId);
            var (registered, vatRate) = await VatProfileAsync(companyId, prop);
            var docType = vatRate > 0 ? DocumentType.TaxInvoice : DocumentType.Invoice;

            // ── ด่านทั้งหมดก่อนแตะข้อมูล (ฝ่ายค้าน C5) ──
            // เดิมเพิ่มค่าเสียหาย/ค่าเช็คเอาต์ช้าก่อน แล้ว FindOrCreateContactAsync (SaveChanges) persist รายการนั้นไปด้วย
            // ก่อนถึงด่านที่ throw ⇒ ผู้ใช้กดใหม่ตามข้อความ = ค่าเสียหายถูกบันทึกซ้ำ
            // รอบ 194 — เงินประกันความเสียหายไม่ใช่ส่วนหนึ่งของราคา ⇒ ห้ามเข้าแผนหัก/ตัดชำระของมัดจำค่าห้อง (ปิดแยกที่ SettleSecurityDepositAsync)
            var deposits = LodgingDepositSettlement.RoomDeposits(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId);
            if (LodgingDepositSettlement.HasTaxedRemaining(deposits) && docType != DocumentType.TaxInvoice)
                throw new BusinessRuleException(
                    "มัดจำของการจองนี้ออกเป็นใบกำกับภาษีแล้ว แต่ที่พักตอนนี้ไม่คิด VAT — ใบสุดท้ายหักมูลค่ามัดจำ"
                    + "ออกจากฐานภาษีไม่ได้ (ยังไม่รองรับ) · เปิด “คิด VAT” ของที่พักกลับก่อนเช็คเอาต์ หรือออกใบลดหนี้ใบมัดจำก่อน",
                    "LODGING-DEPOSIT-VAT-MISMATCH");
            var contactId = r.ContactId ?? throw new BusinessRuleException("การจองไม่มีผู้ติดต่อ (Contact)");
            if (request.IssueTaxInvoiceToCompany)
            {
                if (string.IsNullOrWhiteSpace(r.GuestTaxId) || string.IsNullOrWhiteSpace(r.GuestCompanyName))
                    throw new BusinessRuleException("ออกใบกำกับในนามบริษัทต้องมีชื่อบริษัท + เลขผู้เสียภาษีของแขก (§86/4)", "RD-86/4");
                // บันทึกเฉพาะผู้ติดต่อ (ยังไม่มีรายการ folio ใหม่ใน context — สร้างแยกด้านล่างและผูกหลังออกใบสำเร็จ)
                contactId = await FindOrCreateContactAsync(companyId, new LodgingCreateReservationRequest(r.CheckInDate, r.CheckOutDate, new(), r.GuestName, r.GuestEmail, r.GuestPhone,
                    GuestAddress: r.GuestAddress, GuestTaxId: r.GuestTaxId, GuestCompanyName: r.GuestCompanyName), userId);
            }
            var wrongContact = LodgingDepositSettlement.ApplyCandidates(deposits).FirstOrDefault(a => a.ContactId != contactId);
            if (wrongContact != null)
                throw new BusinessRuleException(
                    $"มัดจำ {wrongContact.Number} ออกในนามผู้เข้าพัก แต่ใบสุดท้ายจะออกในนามอื่น — มัดจำแบบ “เต็มยอด/ภาษีรอเรียกเก็บ” "
                    + "ต้องตัดชำระกับลูกค้ารายเดียวกัน · เช็คเอาต์ในนามผู้เข้าพัก หรือคืนมัดจำใบเดิมแล้วรับใหม่ในนามบริษัท",
                    "LODGING-DEPOSIT-CONTACT");

            // ── รายการใหม่ของเช็คเอาต์: สร้างแต่ยังไม่ผูกกับการจอง — ผูกหลังออกใบสำเร็จ (สร้างเอกสารล้ม = ไม่มีอะไรค้าง) ──
            var newCharges = new List<LodgingFolioCharge>();
            if (request.DamageCharge is decimal dmg && dmg > 0)
                newCharges.Add(await BuildChargeAsync(companyId, r, new LodgingAddChargeRequest(
                    Description: "ค่าเสียหาย/ของหาย" + (string.IsNullOrWhiteSpace(request.DamageDescription) ? "" : $" — {request.DamageDescription.Trim()}"),
                    Quantity: 1, UnitPrice: dmg, Source: LodgingChargeSource.System), userId));
            // ── ค่าเช็คเอาต์ช้า (LDG-P2-06) — คู่กับ EarlyCheckInFee ที่ต่อสายตอนเช็คอิน ──
            if (request.ChargeLateCheckOut && prop.LateCheckOutFee > 0)
                newCharges.Add(await BuildChargeAsync(companyId, r, new LodgingAddChargeRequest(
                    Description: $"ค่าเช็คเอาต์ช้า (หลัง {Time(prop.CheckOutTime)} น.)",
                    Quantity: 1, UnitPrice: prop.LateCheckOutFee, Source: LodgingChargeSource.System), userId));

            var pendingCharges = r.Charges.Where(c => c.Status == LodgingChargeStatus.Pending).Concat(newCharges).ToList();
            // ตัวสร้างรายการใบสุดท้ายตัวเดียวของที่พัก — เส้นเช็คเอาต์และเส้น "ออกใบเช็คเอาต์ใหม่" (A-IN5) เรียกตัวเดียวกัน
            var lines = await BuildFinalInvoiceLinesAsync(companyId, r, prop, registered, vatRate, pendingCharges);
            if (lines.Count == 0) throw new BusinessRuleException("ไม่มีรายการให้ออกเอกสาร");

            // ── แผนใช้มัดจำ — วางแผน **ก่อน** ออกเลขใบ (C2 · §86/4 gap-free) ด้วยตัวคำนวณยอดตัวเดียวกับ CreateDocumentAsync ──
            // มัดจำเกินยอดใบสุดท้าย (เลื่อนวันจนยอดลด · ยกเลิกรายการ folio) ⇒ ใช้เท่าที่รับได้ ส่วนเกิน = ค้างคืนแขก
            // (เดิม: VAT ทันทีรับรู้รายได้เกินจริง · สองโหมดที่เหลือ Apply ล้มหลังประทับเลขใบ ⇒ การจองค้าง "เช็คอิน" ถาวร)
            var full = DocumentService.PreviewTotals(lines, prop.PricesIncludeVat, 0m);
            var depositPlan = LodgingDepositSettlement.PlanCheckout(deposits, full.Net,
                deducted => DocumentService.PreviewTotals(lines, prop.PricesIncludeVat, 0m, deducted).Total);

            var create = new CreateDocumentRequest(
                DocumentType: docType, DocumentDate: DateTime.UtcNow, DueDate: request.CollectBalanceNow ? null : DateTime.UtcNow.AddDays(30),
                ContactId: contactId, Reference: r.ReservationNumber,
                Notes: $"เข้าพัก {prop.Name} {r.CheckInDate:dd/MM/yyyy}–{r.CheckOutDate:dd/MM/yyyy} ({r.Nights} คืน) · จอง {r.ReservationNumber}",
                Lines: lines,
                // มัดจำที่ออกใบกำกับแล้ว (VAT เข้า ภ.พ.30 เดือนที่รับ): หักฐานออกจากฐานภาษีใบนี้ (รูปแบบ B ·
                // DOCUMENT_FLOW §2.3) ⇒ VAT ใบนี้ = VAT ของยอดคงเหลือเท่านั้น · renderer พิมพ์ป้าย "หักมูลค่ามัดจำ
                // ตามใบกำกับภาษี {เลข}" จากคู่ DepositBaseDeducted + DepositAppliedRef · ฝ่ายค้านรอบสาม R3-1: ช่องของตัวเอง
                // (ไม่ใช่ BillDiscountAmount ซึ่งเป็นส่วนลดการค้า) ⇒ ตอนอนุมัติรับรู้มัดจำเท่าฐานนี้เท่านั้น
                DepositAppliedRef: depositPlan.DeductionRef,
                DepositBaseDeducted: depositPlan.BaseDeducted > 0m ? depositPlan.BaseDeducted : null,
                PricesIncludeVat: prop.PricesIncludeVat,
                BankAccountId: request.BankAccountId,
                BranchId: prop.BranchId,
                BookingNumber: r.ReservationNumber,
                ServiceUsedDate: r.CheckOutDate);
            var finalDraftId = await UpsertFinalDraftAsync(companyId, r, create, docType, userId);
            // ฝ่ายค้านรอบ 201 รอบสาม P2-6: ไม่มีคนเห็นคำเตือนในเส้นนี้ ⇒ SystemWorkflow — ผ่านเหมือนเดิม (ไม่หยุดการออกเอกสาร) แต่ร่องรอยบอกตามจริงว่าไม่ใช่คนรับทราบ (เดิม acknowledgeWarnings: true = ประทับ AcknowledgedByPerson)
            var approved = await _docService.ApproveDocumentAsync(companyId, finalDraftId, userId, ApprovalAckSource.SystemWorkflow, withAiHints: false);

            // ออกใบสำเร็จแล้วจึงผูกรายการใหม่เข้าการจอง + ประทับเลขใบทันที — ถ้าขั้นใช้มัดจำด้านล่างล้ม
            // การกดเช็คเอาต์ซ้ำ = ทำขั้นที่ค้างต่อ (ResumeCheckOutAsync) ไม่ออกใบกำกับใบที่สอง และไม่เพิ่มค่าเสียหายซ้ำ
            foreach (var c in newCharges) r.Charges.Add(c);
            RecalcFolio(r);
            r.ContactId = contactId;
            r.FinalDocumentId = approved.Id;

            // ตาข่าย: VAT ใบสุดท้ายต้อง = VAT ของ (ฐานเต็ม − ฐานมัดจำที่ออกใบกำกับแล้ว) · ไม่ตรง = มีบรรทัดอัตราอื่นปน
            // (ส่วนหักท้ายบิลถูกเฉลี่ยลงบรรทัด 0% ด้วย ⇒ VAT ใบนี้สูงกว่าที่ควร) — ไม่แก้เอกสารเอง แต่ต้องเห็น (ล้มดัง)
            decimal roundingDelta = 0m;
            if (depositPlan.BaseDeducted > 0m)
            {
                var expectedVat = LodgingDepositSettlement.FinalInvoiceVat(full.Net, depositPlan.BaseDeducted, vatRate);
                if (Math.Abs(expectedVat - approved.VatAmount) > 0.05m)
                    AppendInternal(r, $"⚠️ VAT ใบ {approved.DocumentNumber} = {approved.VatAmount:N2} แต่ควรเป็น {expectedVat:N2} หลังหักฐานมัดจำ {depositPlan.BaseDeducted:N2} "
                        + "— มีรายการอัตรา VAT อื่นปน · ให้นักบัญชีตรวจ (อาจต้องออกใบลดหนี้ส่วนต่าง)");
                // C7 — VAT คิดรายใบบนฐานของใบเอง (ปัด AwayFromZero) ⇒ รวมสองใบอาจต่างจากยอดจอง ±0.01 ที่ VAT (ฐานไม่เพี้ยน) · บันทึกให้เห็น
                roundingDelta = LodgingDepositSettlement.RoundingDelta(full.Total, depositPlan.GrossDeducted, approved.TotalAmount);
                if (roundingDelta != 0m)
                    AppendInternal(r, $"ปัดเศษ VAT รายใบ: ใบมัดจำ + {approved.DocumentNumber} รวม {(roundingDelta > 0 ? "มากกว่า" : "น้อยกว่า")}ยอดเต็ม {Math.Abs(roundingDelta):N2} "
                        + "(ส่วนต่างอยู่ที่ VAT · รายได้ไม่เพี้ยน · ตามกฎ VAT คิดรายใบ)");
            }
            await _db.SaveChangesAsync();

            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new
            {
                action = "CheckOutInvoiceIssued", finalDocument = approved.DocumentNumber, docType = docType.ToString(),
                depositTaxInvoicedDeducted = new { baseAmt = depositPlan.BaseDeducted, vat = depositPlan.VatDeducted, gross = depositPlan.GrossDeducted, refs = depositPlan.DeductionRef },
                depositApplied = depositPlan.GrossApplied, depositExcessToRefund = depositPlan.ExcessGross, roundingDelta, by = userId,
            }));
            return await SettleCheckOutAsync(companyId, r, approved, depositPlan, request, userId);
        }
        catch (Exception ex) when (legacyAutoClosed && r.FinalDocumentId == null && ex is not OperationCanceledException)
        {
            // ฝ่ายค้านรอบ 202 P2-3: เปิดกลับแล้วล้มก่อนออกใบ ⇒ คืนสถานะเดิม (แขกออกไปนานแล้ว — ห้ามค้าง "เช็คอิน") + ล้มดังที่ตัวการจอง แล้วโยนต่อ
            await RestoreLegacyAutoCheckoutAsync(companyId, r, ex, userId);
            throw;
        }
    }

    /// <summary>คืนสถานะ "เช็คเอาต์แล้ว (ระบบรุ่นก่อน)" เมื่อการออกใบย้อนหลังล้มก่อนออกเลขใบ — หมายเหตุ + audit · บันทึกล้มเอง ⇒ log error (ไม่กลบ error เดิม ·
    /// แถวที่ค้าง "เช็คอิน + มีเวลาเช็คเอาต์" ไม่กันห้อง (EffectiveCheckOut) และขึ้นป้ายค้างปิดให้เห็น)</summary>
    private async Task RestoreLegacyAutoCheckoutAsync(Guid companyId, LodgingReservation r, Exception cause, string userId)
    {
        try
        {
            r.Status = LodgingReservationStatus.CheckedOut;
            AppendInternal(r, $"⚠️ ออกใบเช็คเอาต์ย้อนหลังไม่สำเร็จ ({cause.Message}) — คืนสถานะเดิมแล้ว · แก้สาเหตุแล้วกด “ออกใบเช็คเอาต์ย้อนหลัง” อีกครั้ง");
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "ReopenLegacyAutoCheckoutRolledBack", error = cause.Message, by = userId }));
            await _db.SaveChangesAsync();
        }
        catch (Exception restoreError) when (restoreError is not OperationCanceledException)
        {
            _logger.LogError(restoreError, "คืนสถานะการจอง {No} หลังออกใบย้อนหลังล้มไม่สำเร็จ — แถวค้างสถานะเช็คอิน (ไม่กันห้อง · ป้ายค้างปิด) · error เดิม: {Cause}",
                r.ReservationNumber, cause.Message);
        }
    }

    /// <summary>ใบร่างของใบสุดท้าย — ใช้ใบร่างที่ค้างของการจองนี้ (idempotent) หรือสร้างใหม่ · ตัวเดียวของเส้นเช็คเอาต์และเส้นออกใบใหม่ (A-IN5)</summary>
    private async Task<Guid> UpsertFinalDraftAsync(Guid companyId, LodgingReservation r, CreateDocumentRequest create,
        DocumentType docType, string userId)
    {
        // รอบ 194 R3 (P-1) — กดซ้ำหลังอนุมัติล้ม (ยังไม่ประทับ FinalDocumentId) ⇒ ใช้ใบร่างเดิม (idempotent) โดยเขียนเนื้อหาของรอบนี้ทับ
        // (ยอด/แผนมัดจำของรอบนี้คือความจริงล่าสุด) · เดิมสร้างใบร่างใหม่ทุกครั้ง ใบเก่าค้างเป็นขยะที่อนุมัติผิดใบได้
        var leftovers = await _db.Documents.AsNoTracking()
            .Where(LodgingCheckoutDraft.LeftoverOf(companyId, r.ReservationNumber, LodgingOrigin))
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new { d.Id, d.DocumentType })
            .ToListAsync();
        var draftPlan = LodgingCheckoutDraft.Plan(leftovers.Select(d => (d.Id, d.DocumentType)).ToList(), docType);
        foreach (var staleId in draftPlan.Discard)
            await _docService.DeleteDocumentAsync(companyId, staleId);
        if (draftPlan.Reuse is Guid reuseId)
        {
            await _docService.UpdateDocumentAsync(companyId, reuseId, new UpdateDocumentRequest(
                DocumentDate: create.DocumentDate,
                // ฝ่ายค้านรอบสี่ R4-1: null = "คงค่าเดิม" ⇒ เก็บเงินเลยรอบนี้ต้องล้างวันครบกำหนดของรอบก่อน (sentinel MinValue = ล้าง)
                DueDate: create.DueDate ?? DateTime.MinValue, ContactId: create.ContactId,
                Reference: create.Reference, Notes: create.Notes, Lines: create.Lines,
                BankAccountId: create.BankAccountId, PricesIncludeVat: create.PricesIncludeVat, BranchId: create.BranchId,
                ServiceUsedDate: create.ServiceUsedDate, BookingNumber: create.BookingNumber,
                // ""/0 = ล้างค่าของรอบก่อน (แผนมัดจำรอบนี้อาจไม่หักแล้ว) — ห้าม null ซึ่งแปลว่า "คงค่าเดิม"
                DepositAppliedRef: create.DepositAppliedRef ?? "",
                DepositBaseDeducted: create.DepositBaseDeducted ?? 0m,
                // R4-1: ใบร่างก่อนรอบ 193 R3-1 เคยเก็บฐานมัดจำไว้ในส่วนลดท้ายบิล — ล้าง ไม่งั้นหักซ้ำกับ DepositBaseDeducted
                BillDiscountPercent: create.BillDiscountPercent ?? 0m,
                BillDiscountAmount: create.BillDiscountAmount ?? 0m));
            return reuseId;
        }
        else
        {
            var created = await _docService.CreateDocumentAsync(companyId, create, userId, LodgingOrigin);
            return created.Id;
        }
    }

    /// <summary>รายการของใบสุดท้าย (ใบเช็คเอาต์) — <b>ตัวสร้างเดียวของที่พัก</b> (รอบ 201 ทีม IN · A-IN5): ค่าห้องรายห้อง · บริการเสริม
    /// (ยอดรวมต่อรายการ) · รายการ folio ที่ส่งมา · service charge · ใช้ทั้งเช็คเอาต์ (folio ที่ค้าง + รายการใหม่) และการออกใบใหม่หลังใบเดิม
    /// ถูกยกเลิก (folio ที่ออกเอกสารแล้ว) ⇒ ใบแทนได้รายการชุดเดียวกับใบเดิมเสมอ</summary>
    private async Task<List<DocumentLineRequest>> BuildFinalInvoiceLinesAsync(Guid companyId, LodgingReservation r, LodgingProperty prop,
        bool registered, decimal vatRate, IReadOnlyList<LodgingFolioCharge> charges)
    {
        var roomTypeIds = r.Rooms.Select(x => x.RoomTypeId).Distinct().ToList();
        var productCodes = await (from rt in _db.LodgingRoomTypes.AsNoTracking()
                                  join p in _db.Products.AsNoTracking() on rt.ProductId equals p.Id
                                  where rt.CompanyId == companyId && roomTypeIds.Contains(rt.Id)
                                  select new { rt.Id, p.Code }).ToDictionaryAsync(x => x.Id, x => x.Code);
        var extraProductIds = r.Extras.Where(e => e.ProductId != null).Select(e => e.ProductId!.Value)
            .Concat(charges.Where(c => c.ProductId != null).Select(c => c.ProductId!.Value)).Distinct().ToList();
        var extraCodes = await _db.Products.AsNoTracking().Where(p => p.CompanyId == companyId && extraProductIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Code);

        var lines = new List<DocumentLineRequest>();
        foreach (var room in r.Rooms)
            lines.Add(new(
                Description: $"ค่าห้องพัก {room.RoomTypeName}{(room.Unit != null ? $" ห้อง {room.Unit.Number}" : "")} {r.Nights} คืน ({r.CheckInDate:dd/MM/yyyy}–{r.CheckOutDate:dd/MM/yyyy})",
                Quantity: 1, Unit: "รายการ", UnitPrice: room.Subtotal, DiscountPercent: 0, VatRate: vatRate, WithholdingTaxRate: 0,
                AccountId: null, AccountCode: prop.RoomRevenueAccountCode, ProductCode: productCodes.GetValueOrDefault(room.RoomTypeId)));
        foreach (var e in r.Extras)
            // Total ของบริการเสริมคูณด้วยคืน/คน ตามโหมด (ไม่ใช่ UnitPrice×Quantity ตรง ๆ) —
            // จึงลงเป็น 1 รายการยอดรวม ห้ามหาร Total/Quantity แล้วปัด (ยอดเพี้ยน 0.01)
            lines.Add(new(Description: $"{e.Name} ×{e.Quantity}", Quantity: 1, Unit: "รายการ", UnitPrice: e.Total,
                DiscountPercent: 0, VatRate: vatRate, WithholdingTaxRate: 0, AccountId: null,
                ProductCode: e.ProductId != null ? extraCodes.GetValueOrDefault(e.ProductId.Value) : null));
        foreach (var c in charges)
            // C8 — แถวเดิมที่เก็บ VAT 7 ไว้ก่อนรอบนี้ (บริษัทไม่จด VAT) ต้องผ่านด่าน §90/2 ตอนออกใบด้วย
            lines.Add(new(Description: c.Description, Quantity: c.Quantity, Unit: "รายการ", UnitPrice: c.UnitPrice, DiscountPercent: 0,
                VatRate: LodgingPricingEngine.ChargeVatRate(c.VatRate, registered, vatRate), WithholdingTaxRate: 0, AccountId: null,
                ProductCode: c.ProductId != null ? extraCodes.GetValueOrDefault(c.ProductId.Value) : null));
        if (r.ServiceChargeAmount > 0)
            lines.Add(new(Description: $"Service charge {prop.ServiceChargePercent:0.##}%", Quantity: 1, Unit: "รายการ", UnitPrice: r.ServiceChargeAmount,
                DiscountPercent: 0, VatRate: vatRate, WithholdingTaxRate: 0, AccountId: null, AccountCode: prop.ServiceChargeAccountCode));
        return lines;
    }

    /// <summary>ออกใบเช็คเอาต์ใหม่หลังใบเดิมถูกยกเลิก (รอบ 201 ทีม IN · A-IN5 · คำตัดสินข้อ 36) — ตัวสร้างรายการ/แผนมัดจำ/ใบร่าง/การใช้มัดจำ
    /// ชุดเดียวกับเส้นเช็คเอาต์ · ยอด+ชนิดต้องเท่าใบเดิม (<see cref="LodgingCheckoutReissue.Problem"/>) · อ้างเลขใบเดิมในหมายเหตุ + audit ·
    /// ไม่นับมิเตอร์/ไม่สร้างงานแม่บ้าน/ไม่รับเงินซ้ำ (การรับชำระเดิมถูกยกเลิกไปพร้อมใบ — บันทึกรับชำระที่ใบใหม่ตามจริง)</summary>
    public async Task<LodgingReservationResponse> ReissueFinalDocumentAsync(Guid companyId, Guid reservationId,
        LodgingReissueFinalRequest request, string userId)
    {
        return await ExclusiveCheckoutAsync(companyId, reservationId,
            () => ReissueFinalDocumentCoreAsync(companyId, reservationId, request, userId));
    }

    private async Task<LodgingReservationResponse> ReissueFinalDocumentCoreAsync(Guid companyId, Guid reservationId,
        LodgingReissueFinalRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        var prop = r.Property;
        var accountingOff = prop.AccountingMode == LodgingAccountingMode.Off;
        var oldId = r.FinalDocumentId
            ?? throw new BusinessRuleException("การจองนี้ยังไม่มีใบเช็คเอาต์ — ใช้ปุ่ม “เช็คเอาต์”", LodgingCheckoutReissue.RuleCode);
        var old = await _docService.GetDocumentAsync(companyId, oldId);

        var (registered, vatRate) = await VatProfileAsync(companyId, prop);
        var docType = vatRate > 0 ? DocumentType.TaxInvoice : DocumentType.Invoice;
        // รายการ folio ที่ออกเอกสารแล้วตอนเช็คเอาต์ (สถานะ Paid ถูกประทับที่เช็คเอาต์เท่านั้น) = รายการบนใบเดิม
        var billedCharges = r.Charges.Where(c => c.Status == LodgingChargeStatus.Paid).ToList();
        var lines = await BuildFinalInvoiceLinesAsync(companyId, r, prop, registered, vatRate, billedCharges);
        // void ใบเดิมกลับการรับรู้/ตัดชำระมัดจำแล้ว ⇒ แผนจากสถานะมัดจำปัจจุบัน (ตัวเดียวกับเช็คเอาต์)
        var deposits = LodgingDepositSettlement.RoomDeposits(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId);
        var full = DocumentService.PreviewTotals(lines, prop.PricesIncludeVat, 0m);
        var depositPlan = LodgingDepositSettlement.PlanCheckout(deposits, full.Net,
            deducted => DocumentService.PreviewTotals(lines, prop.PricesIncludeVat, 0m, deducted).Total);
        var rebuiltTotal = DocumentService.PreviewTotals(lines, prop.PricesIncludeVat, 0m, depositPlan.BaseDeducted).Total;

        // ฝ่ายค้าน X2: ผู้ใช้อาจออกใบแทนเองที่หน้าเอกสารแล้ว (ข้อความก่อนรอบ 201 สั่งให้ทำ) โดย FinalDocumentId ยังชี้ใบเดิม ⇒ หาใบขายที่ยังมีผล
        // ซึ่งอ้างเลขจองนี้ · ใบที่ออกเองโดยไม่อ้างเลขจอง ตรวจจากข้อมูลไม่ได้ ⇒ ผู้ใช้ต้องติ๊กยืนยันทุกครั้ง (ไม่มีวันที่ยกเลิกให้แยกยุค)
        var liveReplacement = await FindLiveReplacementSaleAsync(companyId, r.ReservationNumber, oldId);
        if (LodgingCheckoutReissue.Problem(r.Status, old.Status, accountingOff, old.DocumentType, old.TotalAmount,
                docType, rebuiltTotal, old.DocumentNumber, liveReplacement, request.ConfirmNoManualReissue) is { } problem)
            throw new BusinessRuleException(problem, LodgingCheckoutReissue.RuleCode);
        if (lines.Count == 0) throw new BusinessRuleException("ไม่มีรายการให้ออกเอกสาร", LodgingCheckoutReissue.RuleCode);

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        var create = new CreateDocumentRequest(
            DocumentType: docType, DocumentDate: DateTime.UtcNow, DueDate: old.DueDate,
            // ผู้ซื้อเดิมของใบที่ยกเลิก (อาจเป็นบริษัทของแขก) — ใบแทนต้องออกถึงคนเดิม
            ContactId: old.Contact.Id, Reference: r.ReservationNumber,
            Notes: $"เข้าพัก {prop.Name} {r.CheckInDate:dd/MM/yyyy}–{r.CheckOutDate:dd/MM/yyyy} ({r.Nights} คืน) · จอง {r.ReservationNumber} · "
                + LodgingCheckoutReissue.ReferenceNote(old.DocumentNumber),
            Lines: lines,
            DepositAppliedRef: depositPlan.DeductionRef,
            DepositBaseDeducted: depositPlan.BaseDeducted > 0m ? depositPlan.BaseDeducted : null,
            PricesIncludeVat: prop.PricesIncludeVat,
            BankAccountId: old.BankAccountId,
            BranchId: prop.BranchId,
            BookingNumber: r.ReservationNumber,
            ServiceUsedDate: r.CheckOutDate);
        var draftId = await UpsertFinalDraftAsync(companyId, r, create, docType, userId);
        // ฝ่ายค้านรอบ 201 รอบสาม P2-6: ไม่มีคนเห็นคำเตือนในเส้นนี้ ⇒ SystemWorkflow — ผ่านเหมือนเดิม (ไม่หยุดการออกเอกสาร) แต่ร่องรอยบอกตามจริงว่าไม่ใช่คนรับทราบ (เดิม acknowledgeWarnings: true = ประทับ AcknowledgedByPerson)
        var approved = await _docService.ApproveDocumentAsync(companyId, draftId, userId, ApprovalAckSource.SystemWorkflow, withAiHints: false);
        r.FinalDocumentId = approved.Id;
        AppendInternal(r, $"ออกใบเช็คเอาต์ใหม่ {approved.DocumentNumber} แทน {old.DocumentNumber} ที่ยกเลิก" + (reason != null ? $" — {reason}" : ""));
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new
        {
            action = "CheckOutInvoiceReissued", finalDocument = approved.DocumentNumber, replaces = old.DocumentNumber,
            total = approved.TotalAmount, depositTaxInvoicedDeducted = depositPlan.BaseDeducted, depositApplied = depositPlan.GrossApplied,
            reason, ruleCode = LodgingCheckoutReissue.RuleCode, by = userId,
        }));
        await _db.SaveChangesAsync();

        try
        {
            await ApplyFinalDepositPlanAsync(companyId, prop, approved, depositPlan, userId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ล้มดัง 3 ที่ (F2 ข้อ 7) — ใบใหม่ออกแล้ว (ห้ามออกซ้ำ) · ทางไปต่อ: หักมัดจำที่หน้าเอกสารของใบใหม่
            AppendInternal(r, $"⚠️ ออก {approved.DocumentNumber} แล้ว แต่นำมัดจำไปใช้ไม่สำเร็จ: {ex.Message}");
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "CheckOutReissueDepositFailed", finalDocument = approved.DocumentNumber, error = ex.Message, by = userId }));
            await _db.SaveChangesAsync();
            throw new BusinessRuleException(
                $"ออก {approved.DocumentNumber} แทน {old.DocumentNumber} แล้ว แต่นำมัดจำไปใช้ไม่สำเร็จ ({ex.Message}) — "
                + "แก้สาเหตุแล้วใช้ปุ่ม “หักมัดจำ” ที่หน้าเอกสารของใบ " + approved.DocumentNumber + " (อย่าออกใบใหม่ซ้ำ)",
                LodgingCheckoutReissue.RuleCode);
        }
        return await MapAsync(companyId, r, true, true);
    }

    /// <summary>เลขเอกสารขายที่ออกแล้วและยังมีผล (ไม่ใช่ใบ <paramref name="excludeId"/> · ไม่ใช่ใบมัดจำ · ไม่ใช่ใบเสร็จคู่การรับชำระ) ซึ่งอ้างเลขจองนี้
    /// ใน <c>BookingNumber</c> หรือ <c>Reference</c> — มี = ผู้ใช้ออกใบแทนใบเช็คเอาต์ที่ยกเลิกเองแล้ว (ฝ่ายค้าน X2) · null = ไม่พบ</summary>
    internal async Task<string?> FindLiveReplacementSaleAsync(Guid companyId, string reservationNumber, Guid excludeId)
    {
        var saleTypes = LodgingCheckoutReissue.SaleTypes;
        var notIssued = DocumentStatusRules.NotIssued;
        return await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.Id != excludeId && !d.IsDeposit
                && (d.BookingNumber == reservationNumber || d.Reference == reservationNumber)
                && saleTypes.Contains(d.DocumentType)
                && !notIssued.Contains(d.Status) && d.Status != DocumentStatus.Voided
                && !_db.Payments.Any(p => p.CompanyId == companyId && p.ReceiptDocumentId == d.Id))
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => d.DocumentNumber)
            .FirstOrDefaultAsync();
    }

    /// <summary>ทำเช็คเอาต์ที่ค้างต่อ — ออกใบสุดท้ายแล้ว (<c>FinalDocumentId</c>) แต่ขั้นใช้มัดจำล้ม · วางแผนใหม่จากสถานะจริง:
    /// ฐานที่ยังต้องรับรู้ = ส่วนหักท้ายบิลของใบ − ที่รับรู้เพื่อใบนี้ไปแล้ว (<c>JournalEntry.DepositRealizedForDocumentId</c>) ·
    /// มัดจำที่ตัดชำระใบนี้แล้วไม่ตัดซ้ำ · ยอดค้างของใบ = ยอดที่ยังตัดได้</summary>
    private async Task<LodgingReservationResponse> ResumeCheckOutAsync(
        Guid companyId, LodgingReservation r, Guid finalId, LodgingCheckOutRequest request, string userId)
    {
        var finalDoc = await _docService.GetDocumentAsync(companyId, finalId);
        if (finalDoc.Status is DocumentStatus.Voided or DocumentStatus.Rejected or DocumentStatus.Draft)
            throw new BusinessRuleException(
                $"ใบเช็คเอาต์ {finalDoc.DocumentNumber} ของการจองนี้ถูกยกเลิก/ยังไม่อนุมัติ — ทำต่อจากโมดูลที่พักไม่ได้ · ให้ผู้ดูแลตรวจที่หน้าเอกสาร",
                "LODGING-CHECKOUT-FINAL");
        var deposits = LodgingDepositSettlement.RoomDeposits(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId);
        var realizedForFinal = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && j.DepositRealizedForDocumentId == finalId
                && j.OriginalEntryId == null && j.ReversedByEntryId == null
                && j.Status == JournalEntryStatus.Posted && !j.IsDeleted)
            .SumAsync(j => (decimal?)j.TotalDebit) ?? 0m;
        var plan = LodgingDepositSettlement.PlanCheckout(deposits,
            Math.Max(0m, finalDoc.DepositBaseDeducted - realizedForFinal), _ => finalDoc.BalanceDue, finalId);
        AppendInternal(r, $"ทำเช็คเอาต์ที่ค้างต่อ (ใบ {finalDoc.DocumentNumber} ออกแล้ว) — รับรู้ฐาน {plan.BaseDeducted:N2} · ตัดชำระ {plan.GrossApplied:N2} · ค้างคืน {plan.ExcessGross:N2}");
        return await SettleCheckOutAsync(companyId, r, finalDoc, plan, request, userId);
    }

    /// <summary>ฐานมัดจำที่รับรู้เพื่อใบสุดท้ายใบนี้แล้ว แยกตามใบมัดจำ (JE ที่ผูก <c>DepositRealizedForDocumentId</c> · ไม่นับที่ถูกกลับ)</summary>
    private async Task<Dictionary<Guid, decimal>> RealizedForFinalByDepositAsync(Guid companyId, Guid finalId)
        => await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && j.DepositRealizedForDocumentId == finalId && j.SourceDocumentId != null
                && j.OriginalEntryId == null && j.ReversedByEntryId == null
                && j.Status == JournalEntryStatus.Posted && !j.IsDeleted)
            .GroupBy(j => j.SourceDocumentId!.Value)
            .Select(g => new { Id = g.Key, Sum = g.Sum(x => x.TotalDebit) })
            .ToDictionaryAsync(x => x.Id, x => x.Sum);

    /// <summary>ใช้มัดจำกับใบสุดท้ายตามแผน — ตัวเดียวของเส้นเช็คเอาต์และเส้นออกใบใหม่ (A-IN5) · คืนใบสุดท้ายฉบับล่าสุด (ยอดค้างหลังตัดชำระ)</summary>
    private async Task<DocumentResponse> ApplyFinalDepositPlanAsync(Guid companyId, LodgingProperty prop, DocumentResponse finalDoc,
        LodgingCheckoutDepositPlan plan, string userId)
    {
        var finalId = finalDoc.Id;
        // ออกใบกำกับแล้ว → รับรู้ฐานมัดจำเป็นรายได้ (Dr 217xx / Cr รายได้) · VAT มัดจำอยู่งวดเดิม ไม่ถูกกลับ ·
        // ผูก JE กับใบสุดท้าย (FinalInvoiceId) ⇒ void ใบสุดท้ายแล้วกลับการรับรู้นี้ได้ (C4)
        // รอบ 193 ฝ่ายค้านรอบสอง: การอนุมัติใบสุดท้ายรับรู้ฐานมัดจำให้แล้ว (DocumentService.RealizeTaxedDepositDeductionsAsync —
        // ตัวเดียวของทุกเส้น) ⇒ ที่นี่รับรู้เฉพาะส่วนที่ยังขาด (ใบที่ออกก่อนมีตัวนั้น / ทำเช็คเอาต์ต่อ) — ไม่รับรู้ซ้ำ
        var realizedFor = await RealizedForFinalByDepositAsync(companyId, finalId);
        foreach (var d in plan.Deduct)
        {
            var left = d.Base - realizedFor.GetValueOrDefault(d.Id);
            if (left <= 0.005m) continue;
            await _docService.RealizeDepositAsync(companyId, d.Id,
                new RealizeDepositRequest(left, DateTime.UtcNow, prop.RoomRevenueAccountCode, finalId), userId);
        }
        // เต็มยอด/ภาษีรอเรียกเก็บ → ตัดชำระใบสุดท้าย (Dr 217xx [+ 21913] / Cr ลูกหนี้) · ไม่เกินยอดค้างของใบ (วางแผนไว้แล้ว)
        foreach (var a in plan.Apply)
        {
            var amount = Math.Min(a.Gross, finalDoc.BalanceDue);
            if (amount <= 0.005m) continue;
            finalDoc = await _docService.ApplyDepositToInvoiceAsync(companyId, finalId,
                new ApplyDepositRequest(a.Id, amount, DateTime.UtcNow), userId);
        }
        return finalDoc;
    }

    /// <summary>ขั้นใช้มัดจำ + ปิดการเข้าพัก (ใช้ทั้งเช็คเอาต์ปกติและทำต่อ) — ล้มกลางทาง = ล้มดัง 3 ที่ และกดเช็คเอาต์ซ้ำทำต่อได้</summary>
    private async Task<LodgingReservationResponse> SettleCheckOutAsync(
        Guid companyId, LodgingReservation r, DocumentResponse finalDoc, LodgingCheckoutDepositPlan plan,
        LodgingCheckOutRequest request, string userId)
    {
        var prop = r.Property;
        var finalId = finalDoc.Id;
        // รอบ 202: แถวรุ่นเก่าที่ night audit เคยปิดเอง แล้วพนักงานเปิดกลับมาออกใบย้อนหลัง — ห้องจริงถูกปล่อยไปนานแล้ว (อาจมีแขกใหม่)
        var reopenedLegacy = LodgingOverdueRule.IsReopenedLegacy(r.Status, r.CheckedOutAt);
        try
        {
            finalDoc = await ApplyFinalDepositPlanAsync(companyId, prop, finalDoc, plan, userId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ล้มดัง 3 ที่ (F2 ข้อ 7): หมายเหตุบนการจอง · audit · คำตอบผู้เรียก — และมีทางไปต่อจริง (กดเช็คเอาต์อีกครั้ง)
            AppendInternal(r, $"⚠️ ออก {finalDoc.DocumentNumber} แล้ว แต่นำมัดจำไปใช้ไม่สำเร็จ: {ex.Message}");
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "CheckOutDepositSettleFailed", finalDocument = finalDoc.DocumentNumber, error = ex.Message, by = userId }));
            await _db.SaveChangesAsync();
            throw new BusinessRuleException(
                $"ออก {finalDoc.DocumentNumber} แล้ว แต่นำมัดจำไปใช้ไม่สำเร็จ ({ex.Message}) — แก้สาเหตุแล้วกด “เช็คเอาต์” อีกครั้ง "
                + "ระบบจะทำขั้นที่ค้างต่อ (ไม่ออกใบใหม่ · ไม่ใช้มัดจำซ้ำ · ไม่เพิ่มค่าเสียหายซ้ำ)",
                "LODGING-DEPOSIT-SETTLE");
        }

        decimal collected = 0;
        if (request.CollectBalanceNow && finalDoc.BalanceDue > 0.005m)
        {
            await _docService.CreatePaymentAsync(companyId, new CreatePaymentRequest(
                DocumentId: finalId, PaymentDate: DateTime.UtcNow, Amount: finalDoc.BalanceDue, PaymentMethod: request.PaymentMethod,
                Reference: request.PaymentReference, BankAccount: null, Notes: $"ชำระตอนเช็คเอาต์ {r.ReservationNumber}",
                OverrideBankAccountId: request.BankAccountId), userId);
            collected = finalDoc.BalanceDue;
        }

        foreach (var c in r.Charges.Where(c => c.Status == LodgingChargeStatus.Pending)) c.Status = LodgingChargeStatus.Paid;
        r.PaidAmount += collected;
        // C2 — มัดจำเกินยอดใบสุดท้าย = ค้างคืนแขก (ยังไม่ลงบัญชีคืนเงิน · กด “ยืนยันคืนเงินแล้ว” เมื่อโอนจริง — กลไกเดียวกับการยกเลิก)
        r.RefundAmount = plan.ExcessGross;
        if (plan.ExcessGross > 0.005m)
            r.RefundBaselineGross = LodgingDepositSettlement.RoomDeposits(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId)
                .Sum(d => d.RefundedGross);
        if (plan.ExcessGross > 0.005m)
            AppendInternal(r, $"มัดจำเกินยอดใบ {finalDoc.DocumentNumber} — ค้างคืนเงินแขก {plan.ExcessGross:N2} "
                + $"({string.Join(", ", plan.Excess.Select(x => $"{x.Number} {x.Gross:N2}"))}) · กด “ยืนยันคืนเงินแล้ว” เมื่อโอนคืนจริง");
        r.Status = LodgingReservationStatus.CheckedOut; r.CheckedOutAt = reopenedLegacy ? r.CheckedOutAt : DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.Note)) AppendInternal(r, request.Note);
        if (!request.CollectBalanceNow && finalDoc.BalanceDue > 0.005m)
            AppendInternal(r, $"เช็คเอาต์แบบเครดิต — ค้างชำระ {finalDoc.BalanceDue:N2} บน {finalDoc.DocumentNumber}");
        if (reopenedLegacy)
            AppendInternal(r, $"ออกใบเช็คเอาต์ย้อนหลัง {finalDoc.DocumentNumber} แล้ว — ไม่แตะสถานะห้อง/งานแม่บ้านปัจจุบัน (ห้องถูกปล่อยตั้งแต่ระบบรุ่นก่อนปิดเอง)");

        foreach (var room in r.Rooms.Where(x => x.Unit != null && !reopenedLegacy))
        {
            room.Unit!.HousekeepingStatus = LodgingHousekeepingStatus.VacantDirty;
            if (prop.AutoCreateHousekeepingTaskOnCheckout)
                _db.LodgingHousekeepingTasks.Add(new LodgingHousekeepingTask
                {
                    CompanyId = companyId, PropertyId = prop.Id, UnitId = room.Unit.Id, ReservationId = r.Id,
                    TaskType = LodgingHousekeepingTaskType.CheckoutClean, Priority = LodgingTaskPriority.Normal, Status = LodgingTaskStatus.Pending,
                    DueAt = DateTime.UtcNow.AddHours(2), EstimatedMinutes = prop.HousekeepingMinutesPerRoom, CreatedBy = userId,
                });
        }
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        await MeterStayAsync(companyId, r, "checkout");
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new
        {
            action = "CheckOut", finalDocument = finalDoc.DocumentNumber,
            depositTaxInvoicedRealized = plan.BaseDeducted, depositApplied = plan.GrossApplied, refundDue = plan.ExcessGross,
            collected, balanceLeft = finalDoc.BalanceDue - collected, by = userId,
        }));
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    /// <summary>เช็คเอาต์ของที่พักที่ตั้ง `AccountingMode = Off` — ปิดการเข้าพัก
    /// เก็บยอดที่รับจริงไว้บนการจอง แต่ไม่ออกเอกสารบัญชีใด ๆ (ลูกค้าลงบัญชีที่อื่น)
    /// มิเตอร์ `lodging.stay` ยังนับเท่าเดิม (§13.3)</summary>
    private async Task<LodgingReservationResponse> CheckOutWithoutDocumentAsync(
        Guid companyId, LodgingReservation r, LodgingCheckOutRequest request, string userId)
    {
        var vatRate = await EffectiveVatRateAsync(companyId, r.Property);
        if (request.DamageCharge is decimal dmg && dmg > 0)
        {
            r.Charges.Add(new LodgingFolioCharge
            {
                CompanyId = companyId, ReservationId = r.Id,
                Description = "ค่าเสียหาย/ของหาย" + (string.IsNullOrWhiteSpace(request.DamageDescription) ? "" : $" — {request.DamageDescription!.Trim()}"),
                Quantity = 1, UnitPrice = dmg, Total = Math.Round(dmg, 2, MidpointRounding.AwayFromZero), VatRate = vatRate,
                Source = LodgingChargeSource.System, Status = LodgingChargeStatus.Pending, ChargedAt = DateTime.UtcNow, CreatedBy = userId,
            });
            RecalcFolio(r);
        }
        var balance = Accounting.Helpers.LodgingAmounts.BalanceDue(r.TotalAmount, r.FolioTotal, r.PaidAmount);
        if (request.CollectBalanceNow && balance > 0) r.PaidAmount += balance;
        foreach (var c in r.Charges.Where(c => c.Status == LodgingChargeStatus.Pending)) c.Status = LodgingChargeStatus.Paid;
        r.Status = LodgingReservationStatus.CheckedOut; r.CheckedOutAt = DateTime.UtcNow;
        AppendInternal(r, $"เช็คเอาต์แบบไม่ออกเอกสาร (โหมด {r.Property.AccountingMode}) — ยอดรวม {r.TotalAmount + r.FolioTotal:N2}"
            + (request.CollectBalanceNow ? $" รับเพิ่ม {balance:N2}" : " ยังไม่รับส่วนที่เหลือ"));
        if (!string.IsNullOrWhiteSpace(request.Note)) AppendInternal(r, request.Note!);
        foreach (var room in r.Rooms.Where(x => x.Unit != null))
        {
            room.Unit!.HousekeepingStatus = LodgingHousekeepingStatus.VacantDirty;
            if (r.Property.AutoCreateHousekeepingTaskOnCheckout)
                _db.LodgingHousekeepingTasks.Add(new LodgingHousekeepingTask
                {
                    CompanyId = companyId, PropertyId = r.PropertyId, UnitId = room.Unit.Id, ReservationId = r.Id,
                    TaskType = LodgingHousekeepingTaskType.CheckoutClean, Priority = LodgingTaskPriority.Normal,
                    Status = LodgingTaskStatus.Pending, DueAt = DateTime.UtcNow.AddHours(2),
                    EstimatedMinutes = r.Property.HousekeepingMinutesPerRoom, CreatedBy = userId,
                });
        }
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        await MeterStayAsync(companyId, r, "checkout-nodoc");
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new
        { action = "CheckOutNoDocument", mode = r.Property.AccountingMode.ToString(), collected = request.CollectBalanceNow ? balance : 0m, by = userId }));
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    // ═══════════════════════════ Cancel / no-show ═══════════════════════════

    public async Task<LodgingReservationResponse> CancelAsync(Guid companyId, Guid reservationId, LodgingCancelRequest request, string userId, bool noShow = false)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        // รอบ 202 (O-P0-2): no-show ที่ night audit รุ่นก่อนประทับเองโดยไม่คิดค่าปรับ/ตั้งยอดคืน ⇒ พนักงานกด "No-show" ได้หนึ่งครั้งเพื่อเดินเส้นยกเลิกปกติ
        var legacyNoShow = noShow && LodgingOverdueRule.IsLegacyAutoNoShow(r.Status, r.CancellationReason, r.CancellationFee, r.RefundAmount);
        if (Terminal.Contains(r.Status) && !legacyNoShow) throw new BusinessRuleException($"การจองอยู่ในสถานะ {StatusTh(r.Status)} แล้ว");
        if (r.Status == LodgingReservationStatus.CheckedIn) throw new BusinessRuleException("แขกเช็คอินแล้ว — ต้องเช็คเอาต์ (ออกบิล) แทนการยกเลิก");
        if (noShow && r.CheckInDate > DateTime.UtcNow.AddHours(7).Date) throw new BusinessRuleException("ยังไม่ถึงวันเช็คอิน — บันทึก no-show ไม่ได้");
        await CancelCoreAsync(companyId, r, request.Reason ?? (noShow ? "ไม่มาเข้าพัก" : "ยกเลิกโดยพนักงาน"), userId, noShow, legacyNoShow);
        return await MapAsync(companyId, r, true, true);
    }

    /// <summary>ยกเลิก: คิดค่าปรับจากนโยบายที่ตรึงไว้ ณ วันจอง → ส่วนที่ริบ RealizeDeposit ทันที (รายได้ริบมัดจำ)
    /// · ส่วนที่ต้องคืน = <b>ยอดค้างคืน</b> เท่านั้น (F-03 รอบ 193)
    ///
    /// <para>เดิมเรียก <c>RefundDepositAsync</c> ที่นี่ ⇒ ลง Cr 111 เงินสด + ใบลดหนี้ Approved ทันทีที่แขกกดยกเลิก
    /// ทั้งที่ยังไม่มีใครโอนคืน (และ 111 ตายตัวแม้มัดจำเข้าทาง gateway 11340) = สถานะปลายทางที่ระบบประทับเอง
    /// (DECISION_DOCTRINE R1) · ตอนนี้ภาระคืนยังอยู่ในหนี้สินมัดจำ (217xx [+VAT]) จนพนักงานกด
    /// "ยืนยันคืนเงินแล้ว" (<see cref="RecordRefundPaidAsync"/>) — JE คืนเงิน + ใบลดหนี้เกิดตอนนั้น</para></summary>
    private async Task CancelCoreAsync(Guid companyId, LodgingReservation r, string reason, string actor, bool noShow, bool legacyNoShowSettle = false)
    {
        // C3 รอบ 193 หลังฝ่ายค้าน — ด่านสถานะอยู่ที่ตัวกลาง (ไม่ใช่ทางเข้าแต่ละทาง): เดิมเส้นแขก (CancelByTokenAsync) ไม่มีด่าน ⇒
        // กดยกเลิกซ้ำ = คิดค่าปรับใหม่จากมัดจำที่เหลือ → ยอดที่ "ต้องคืน" ถูกริบเป็นรายได้ + RefundAmount ถูกเขียนทับ + มิเตอร์นับซ้ำ
        // รอบ 202: ข้อยกเว้นเดียว = no-show ที่ night audit รุ่นก่อนประทับเอง (ยังไม่เคยคิดค่าปรับ) — ตัวตัดสินตรวจซ้ำที่นี่ ไม่เชื่อธงจากผู้เรียกลอย ๆ ·
        // เส้นนี้เขียนทับเหตุผลยกเลิก ⇒ ไม่ตรงเงื่อนไขอีก = คิดได้ครั้งเดียว
        var legacySettle = legacyNoShowSettle && noShow
            && LodgingOverdueRule.IsLegacyAutoNoShow(r.Status, r.CancellationReason, r.CancellationFee, r.RefundAmount);
        if (Terminal.Contains(r.Status) && !legacySettle)
            throw new BusinessRuleException($"การจองอยู่ในสถานะ {StatusTh(r.Status)} แล้ว — ยกเลิกซ้ำไม่ได้", "LODGING-CANCEL-TERMINAL");
        if (r.Status == LodgingReservationStatus.CheckedIn)
            throw new BusinessRuleException("แขกเช็คอินแล้ว — ต้องเช็คเอาต์ (ออกบิล) แทนการยกเลิก", "LODGING-CANCEL-TERMINAL");
        decimal fee;
        if (noShow) fee = Math.Round(r.TotalAmount * r.Property.NoShowChargePercent / 100m, 2, MidpointRounding.AwayFromZero);
        else
        {
            var (rules, nonRefundable) = SnapshotRules(r);
            fee = LodgingPricingEngine.CancellationFee(r.TotalAmount, r.CheckInDate, DateTime.UtcNow.AddHours(7), rules, nonRefundable).Fee;
        }
        var deposit = r.DepositPaid;
        // คำตัดสินเจ้าของข้อ 126 (รอบ 202): ค่าปรับยกเลิก/no-show ไม่เกินมัดจำที่รับไว้เสมอ — ไม่มี "ส่วนต่างที่ยังไม่ได้เรียกเก็บ" (ไม่ออกใบแจ้งหนี้ส่วนต่าง)
        var policyFee = fee;
        fee = LodgingBookingGuards.CappedCancellationFee(policyFee, deposit);
        // รอบ 194 — เงินประกันความเสียหายไม่ใช่มัดจำค่าห้อง: ห้ามถูกริบเป็นค่าปรับยกเลิก/คืนปนกับยอดค้างคืน (ปิดแยกที่ปุ่มเงินประกัน)
        var deposits = r.Property.AccountingMode != LodgingAccountingMode.Off && deposit > 0
            ? LodgingDepositSettlement.RoomDeposits(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId)
            : new List<LodgingDepositSnapshot>();
        var plan = LodgingDepositSettlement.PlanCancellation(fee, deposit, deposits);

        // ── ปิดสถานะก่อน แล้วค่อยลงบัญชีส่วนที่ริบ (P0-2 รอบ 193 · atomicity) ──
        // เดิมลง JE ก่อนแล้วค่อยเปลี่ยนสถานะ ⇒ JE ใบแรก commit แล้วใบถัดไปล้ม = การจองยังไม่ยกเลิก ⇒ กดซ้ำ
        // ลง JE/ใบลดหนี้ซ้ำ · ตอนนี้บันทึกสถานะ + ยอดค้างคืนก่อน (กดซ้ำถูกด่าน "อยู่ในสถานะยกเลิกแล้ว" กันไว้)
        r.Status = noShow ? LodgingReservationStatus.NoShow : LodgingReservationStatus.Cancelled;
        r.CancelledAt = legacySettle ? (r.CancelledAt ?? DateTime.UtcNow) : DateTime.UtcNow;
        r.CancellationReason = reason; r.CancellationFee = fee; r.RefundAmount = plan.Refund; r.HoldExpiresAt = null;
        if (legacySettle) AppendInternal(r, $"คิดค่าปรับ no-show ย้อนหลังของแถวที่ระบบรุ่นก่อนบันทึกเอง — ค่าปรับ {fee:N2} · ค้างคืน {plan.Refund:N2} (โดย {actor})");
        // N3 — จุดตั้งยอดค้างคืน: การคืนบนใบมัดจำก่อนจุดนี้ถูกหักออกจากยอดต้องคืนแล้ว (ไม่ใช่การคืนของยอดนี้)
        r.RefundBaselineGross = deposits.Sum(d => d.RefundedGross);
        // PaidAmount ไม่ลดที่นี่ — เงินยังอยู่กับที่พักจนกว่าจะโอนคืนจริง (ลดใน RecordRefundPaidAsync)
        if (plan.Refund > 0.005m)
            AppendInternal(r, $"ค้างคืนเงินแขก {plan.Refund:N2} — ยังไม่ได้ลงบัญชีคืนเงิน · กด “ยืนยันคืนเงินแล้ว” เมื่อโอนคืนจริง (ระบบจะออกใบลดหนี้ตอนนั้น)");
        if (r.SecurityDepositDocumentId != null && r.SecurityDepositSettledAt == null)
            AppendInternal(r, "มีเงินประกันความเสียหายค้างอยู่ — ไม่ถูกนำไปหักค่าปรับยกเลิก · คืน/ริบที่ปุ่ม “เงินประกัน” ของการจองนี้");
        foreach (var room in r.Rooms) room.UnitId = null;
        // นับมิเตอร์เฉพาะการจองที่ "มีเงินเกี่ยวข้องจริง" — จองแล้วยกเลิกฟรีก่อนจ่ายมัดจำ
        // ต้องไม่ถูกคิด (ไม่งั้นการเปิดให้จองฟรีจะกลายเป็นกับดัก) · no-show คิดเสมอ
        // เพราะห้องถูกกันไว้จริงและมีค่าปรับตามนโยบาย
        if (noShow || deposit > 0 || fee > 0) await MeterStayAsync(companyId, r, noShow ? "no-show" : "cancel");
        _db.AddChainedAuditLog(Audit(companyId, noShow ? AuditAction.Update : AuditAction.Delete, r, new { action = noShow ? "NoShow" : "Cancel", reason, fee, policyFee, refundDue = plan.Refund, forfeit = plan.Forfeit, refundPaid = 0m, by = actor }));
        await _db.SaveChangesAsync();
        // ฝ่ายค้านรอบ 202 P1-1 (ข้อ 128 · กติกาข้อ 127): มีสลิปค้างตรวจที่ยังไม่บันทึกรับเงิน ⇒ เงินตามสลิปอาจอยู่ในบัญชีแล้วและต้องคืน —
        // ติดธงให้พนักงานตัดสิน (ทั้งเส้นแขก token / พนักงาน / no-show เพราะทุกเส้นเดินตัวกลางนี้) · ไม่ประทับยอดคืนเอง (R1)
        if (LodgingGuestConfirmPolicy.CancelLeavesUnverifiedSlip(r.SlipUploadedAt != null, r.DepositPaid))
            await FlagPaymentProblemAsync(companyId, r.Id, LodgingGuestConfirmPolicy.UnverifiedSlipOnCancelNote(noShow), noShow ? "no-show" : "cancel");

        // ส่วนที่ริบ = เหตุการณ์เกิดแล้วจริง → รับรู้รายได้ (ฐาน ไม่ใช่ gross — P0-2 · ฐานคำนวณให้ส่วนที่คืนภายหลังผ่านด่าน
        // "คืนเกินคงเหลือ" พอดี) · รอบ 194 (spec S3 · L1 C-2/C-3): VAT ของส่วนที่ริบตามลักษณะเงิน ไม่ใช่ตามโหมด — มัดจำค่าห้อง =
        // ส่วนหนึ่งของราคา ⇒ ริบเป็น "ราคา/ค่าธรรมเนียมยกเลิก" (ForfeitAs PriceOrFee): VAT ทันที = คงอยู่งวดเดิม · ภาษีรอเรียกเก็บ =
        // ย้ายเข้า 21911 + ธง [DEPOSIT-LATE-VAT] · มัดจำเต็มยอด = DocumentService ออกใบกำกับของยอดที่ริบแล้วตัดชำระด้วยมัดจำ
        // (ห้ามลงรายได้ไม่มี VAT เงียบ ๆ อย่างเดิม) — ตัดสินที่ DepositPolicyResolver.ForfeitVatDecision ตัวเดียว
        var realized = new List<string>();
        // P1 (regsec): อัตรา VAT ของที่พัก ณ ตอนริบ — ที่พักไม่คิด VAT ⇒ ใบ VAT 0 ของที่พักเป็น "VAT 0 โดยชอบ" ไม่ออกใบกำกับ 7%
        var channelVatRate = await EffectiveVatRateAsync(companyId, r.Property);
        try
        {
            foreach (var line in plan.Lines.Where(l => l.ForfeitBase > 0.005m))
            {
                await _docService.RealizeDepositAsync(companyId, line.Id,
                    new RealizeDepositRequest(line.ForfeitBase, DateTime.UtcNow, r.Property.CancellationFeeAccountCode ?? r.Property.RoomRevenueAccountCode,
                        ForfeitAs: DepositForfeitAs.PriceOrFee), actor, channelVatRate: channelVatRate);
                realized.Add(line.Number);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ล้มดัง 3 ที่: หมายเหตุบนการจอง · audit · คำตอบผู้เรียก — สถานะยกเลิกคงไว้ (ห้ามลงซ้ำตอนกดใหม่)
            // M1(ก): DocumentService ไม่ล้าง change tracker ทั้งก้อนอีกแล้ว ⇒ `r` ยังถูกติดตาม หมายเหตุด้านล่างถูกบันทึกจริง
            // M1(ค): ทางไปต่อข้อความเดียวกับ DocumentService (DepositKindDocumentRules.ForfeitRetryHint) — กดริบซ้ำปลอดภัย ระบบทำต่อจากใบกำกับที่ค้าง
            var pendingLines = plan.Lines.Where(l => l.ForfeitBase > 0.005m && !realized.Contains(l.Number)).ToList();
            var pendingForfeit = string.Join(", ", pendingLines.Select(l => $"{l.Number} ฐาน {l.ForfeitBase:N2}"));
            var retry = string.Join(" · ", pendingLines.Select(l => DepositKindDocumentRules.ForfeitRetryHint(l.Number, l.ForfeitBase)));
            AppendInternal(r, $"⚠️ ยกเลิกแล้ว แต่ลงรายได้ส่วนที่ริบไม่สำเร็จ ({ex.Message}) — {retry}");
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "CancelForfeitFailed", error = ex.Message, pending = pendingForfeit, by = actor }));
            await _db.SaveChangesAsync();
            throw new BusinessRuleException(
                $"ยกเลิกการจองแล้ว แต่ลงรายได้ส่วนที่ริบไม่สำเร็จ ({ex.Message}) — {retry}",
                "LODGING-CANCEL-FORFEIT");
        }
        _logger.LogInformation("Lodging reservation {No} {Action}: fee {Fee} refund-due {Refund} forfeit {Forfeit}", r.ReservationNumber, noShow ? "no-show" : "cancelled", fee, plan.Refund, plan.Forfeit);
    }

    // ═══════════════════════════ คืนเงินแขก (F-03) ═══════════════════════════

    /// <summary>ยืนยันว่าโอน/จ่ายคืนแขกแล้วจริง — ลง JE คืนเงิน + ใบลดหนี้ (ผ่าน <c>RefundDepositAsync</c> เส้นเดิม)
    /// จากบัญชีที่เงินเข้ามาจริง · "คืนแล้ว" ตั้งจากการกระทำนี้เท่านั้น (หลักฐาน = พนักงานยืนยัน + เลขอ้างอิงการโอน)</summary>
    public async Task<LodgingReservationResponse> RecordRefundPaidAsync(Guid companyId, Guid reservationId, LodgingRefundPaidRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        // ล็อกระดับการจอง (ข้ามเครื่องได้) — เดิมสองคำขอพร้อมกันแบบบางส่วนผ่านด่านทั้งคู่ แล้ว RefundPaidAmount ถูกเขียนทับ
        // (lost update) · RefundDepositAsync เปิดธุรกรรมของตัวเองต่อใบ จึงใช้ session lock ไม่ใช่ xact lock
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.LodgingRefundPaid, reservationId.ToString(),
            async () =>
            {
                await _db.Entry(r).ReloadAsync();   // ค่าล่าสุดใต้ล็อก
                await RecordRefundPaidCoreAsync(companyId, r, request, userId);
            }, _logger, companyId);
        if (!ran)
            throw new BusinessRuleException("มีผู้ใช้อื่นกำลังบันทึกคืนเงินของการจองนี้อยู่ — รอสักครู่แล้วเปิดดูใหม่", "LODGING-REFUND-BUSY");
        return await MapAsync(companyId, r, true, true);
    }

    private async Task RecordRefundPaidCoreAsync(Guid companyId, LodgingReservation r, LodgingRefundPaidRequest request, string userId)
    {
        if (r.Status is not (LodgingReservationStatus.Cancelled or LodgingReservationStatus.NoShow or LodgingReservationStatus.CheckedOut))
            throw new BusinessRuleException("บันทึกการคืนเงินได้เฉพาะการจองที่ยกเลิก/ไม่มาเข้าพัก หรือเช็คเอาต์ที่มัดจำเกินยอด", "LODGING-REFUND");
        // C10 — แถวก่อนรอบ 193: ระบบเดิมลง JE คืนเงิน + ใบลดหนี้ไปแล้วตอนยกเลิก ⇒ ห้ามลงซ้ำ
        if (LodgingDepositSettlement.IsLegacyRefund(r.RefundPaidBy))
            throw new BusinessRuleException("การจองนี้ยกเลิกก่อนรอบ 193 — ระบบเดิมลงบัญชีคืนเงินไปแล้วตอนยกเลิก (ไม่มีข้อมูลการโอน) "
                + "· บันทึกซ้ำไม่ได้ ให้ตรวจการโอนจริงกับใบลดหนี้เดิม", "LODGING-REFUND-LEGACY");
        var paidAt = request.PaidAt ?? DateTime.UtcNow;
        if (paidAt > DateTime.UtcNow.AddDays(1))
            throw new BusinessRuleException("วันที่คืนเงินต้องไม่เป็นวันในอนาคต", "LODGING-REFUND");
        // ห้ามลงวันที่ย้อนเข้างวด ภ.พ.30 ที่ยื่น/ประกาศว่ายื่นแล้ว — ใบลดหนี้ของมัดจำที่รายงาน VAT แล้วจะไปอยู่ในงวดที่ปิดไปแล้ว
        if (await VatPeriodFiledAsync(companyId, paidAt))
            throw new BusinessRuleException($"งวด ภ.พ.30 {paidAt:MM/yyyy} ยื่นแล้ว — ลงวันที่คืนเงินย้อนเข้างวดนั้นไม่ได้ · ใช้วันที่โอนจริงในงวดที่ยังเปิด", "LODGING-REFUND-PERIOD");
        var reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();

        var creditNotesFor = new List<string>();
        var deposits = r.Property.AccountingMode != LodgingAccountingMode.Off
            ? LodgingDepositSettlement.RoomDeposits(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId)
            : new List<LodgingDepositSnapshot>();
        // ยอดที่ "คืนแล้ว" ตามบัญชีจริง = ยอดคืนบนใบมัดจำ (แหล่งความจริงเดียว) — ถ้าคำขอก่อนล้มหลัง RefundDepositAsync commit
        // แต่ก่อนบันทึกการจอง ยอดค้างจะซ่อมตัวเองที่นี่ (เดิมกดซ้ำแล้วชนด่าน "คืนเกิน" ค้างถาวร)
        var caughtUp = deposits.Count > 0 ? SyncRefundPaidFromDeposits(r, deposits, userId) : 0m;
        if (caughtUp > 0m)
        {
            // N3 — บันทึกผลซ่อม**ก่อน**ตรวจยอด (เดิม throw ก่อน SaveChanges ⇒ สิ่งที่ซ่อมหาย ค้างถาวร)
            await _db.SaveChangesAsync();
            if (LodgingDepositSettlement.RefundPending(r.RefundAmount, r.RefundPaidAmount) <= 0.005m)
            {
                // ใบมัดจำลงคืนครบแล้ว (คำขอก่อนหน้าล้มหลัง commit) — จบแบบสำเร็จ ไม่ลงคืนซ้ำ
                r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
                _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "RefundPaidCaughtUp", amount = caughtUp, by = userId }));
                await _db.SaveChangesAsync();
                return;
            }
        }
        var pending = LodgingDepositSettlement.RefundPending(r.RefundAmount, r.RefundPaidAmount);
        var amount = Math.Round(request.Amount ?? pending, 2, MidpointRounding.AwayFromZero);
        if (LodgingDepositSettlement.ValidateRefundPayment(amount, r.RefundAmount, r.RefundPaidAmount) is string invalid)
            throw new BusinessRuleException(invalid, "LODGING-REFUND");

        if (deposits.Count > 0)
        {
            var moneyAccountId = await ResolveRefundMoneyAccountAsync(companyId, request.BankAccountId, deposits);
            var allocation = LodgingDepositSettlement.AllocateRefund(amount, deposits);
            var allocated = allocation.Sum(a => a.Gross);
            if (allocated < amount - 0.005m)
                throw new BusinessRuleException(
                    $"มัดจำคงเหลือในเอกสาร ({allocated:N2}) น้อยกว่ายอดที่จะคืน ({amount:N2}) — มัดจำอาจถูกรับรู้/คืนไปบางส่วนแล้ว "
                    + "· ตรวจที่หน้า “เงินมัดจำ” ก่อนบันทึกคืนเงิน", "LODGING-REFUND");
            foreach (var (id, number, gross) in allocation)
            {
                await _docService.RefundDepositAsync(companyId, id, new RefundDepositRequest(gross, paidAt,
                    $"คืนเงินแขก — {RefundReasonTh(r.Status)} {r.ReservationNumber}"
                    + (reference != null ? $" · อ้างอิง {reference}" : ""), moneyAccountId), userId);
                creditNotesFor.Add(number);
                // บันทึกทีละใบ — ใบถัดไปล้มแล้วกดซ้ำ ต้องไม่คืนใบที่คืนไปแล้วซ้ำ (ยอดค้างลดตามจริงทุกใบ)
                r.RefundPaidAmount = Math.Round(r.RefundPaidAmount + gross, 2, MidpointRounding.AwayFromZero);
                r.PaidAmount = Math.Max(0, r.PaidAmount - gross);
                r.RefundPaidAt = paidAt; r.RefundPaidBy = userId;
                await _db.SaveChangesAsync();
            }
        }
        else
        {
            // โหมดไม่ออกเอกสาร — บันทึกบนการจองอย่างเดียว (ไม่มี JE ให้ลง)
            r.RefundPaidAmount = Math.Round(r.RefundPaidAmount + amount, 2, MidpointRounding.AwayFromZero);
            r.PaidAmount = Math.Max(0, r.PaidAmount - amount);
            r.RefundPaidAt = paidAt; r.RefundPaidBy = userId;
        }
        r.RefundReference = reference ?? r.RefundReference;
        AppendInternal(r, $"ยืนยันคืนเงินแขก {amount:N2}{(reference != null ? $" (อ้างอิง {reference})" : "")}"
            + (creditNotesFor.Count > 0 ? $" · ลงบัญชีคืนมัดจำ + ใบลดหนี้จาก {string.Join(", ", creditNotesFor)}" : "")
            + (!string.IsNullOrWhiteSpace(request.Note) ? $" — {request.Note!.Trim()}" : ""));
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "RefundPaid", amount, paidAt, reference, deposits = creditNotesFor, by = userId }));
        await _db.SaveChangesAsync();
    }

    /// <summary>งวด ภ.พ.30 ของวันที่นี้ยื่น/ประกาศว่ายื่นแล้วไหม — ด่านวันที่คืนเงิน (มัดจำค่าห้อง + เงินประกัน · ตัวเดียว)</summary>
    private async Task<bool> VatPeriodFiledAsync(Guid companyId, DateTime date)
    {
        return await _db.TaxReports.AsNoTracking()
            .AnyAsync(t => t.CompanyId == companyId && !t.IsDeleted && t.TaxType == TaxType.VAT
                && TaxFilingLockPolicy.DeclaredOrFiledStatuses.Contains(t.Status)
                && t.Year == date.Year && t.Month == date.Month);
    }

    private static string RefundReasonTh(LodgingReservationStatus s) => s switch
    {
        LodgingReservationStatus.NoShow => "no-show",
        LodgingReservationStatus.CheckedOut => "มัดจำเกินยอดเช็คเอาต์",
        _ => "ยกเลิก",
    };

    /// <summary>ยอดคืนแล้วบนการจอง ↔ ยอดคืนจริงบนใบมัดจำ (แหล่งความจริง) — ถ้าใบมัดจำคืนไปมากกว่าที่การจองรู้
    /// (คำขอก่อนล้มกลางทาง) ให้การจองตามทัน · ไม่ลดยอด (คืนด้วยมือที่หน้า "เงินมัดจำ" ก็นับเป็นคืนแล้วเช่นกัน)</summary>
    private decimal SyncRefundPaidFromDeposits(LodgingReservation r, IReadOnlyList<LodgingDepositSnapshot> deposits, string userId)
    {
        var gap = LodgingDepositSettlement.RefundPaidCatchUp(r.RefundAmount, r.RefundPaidAmount,
            deposits.Sum(d => d.RefundedGross), r.RefundBaselineGross);
        if (gap <= 0.005m) return 0m;
        r.RefundPaidAmount += gap;
        r.PaidAmount = Math.Max(0, r.PaidAmount - gap);
        r.RefundPaidBy ??= userId;
        AppendInternal(r, $"ปรับยอดคืนแล้วให้ตรงใบมัดจำ +{gap:N2} (มีการคืนเงินที่ลงบัญชีแล้วแต่การจองยังไม่ได้บันทึก)");
        return gap;
    }

    /// <summary>บัญชีที่เงินคืนออก — (1) บัญชีธนาคารที่พนักงานเลือก (ผังที่ผูกไว้) (2) ขาเงินเข้าของใบมัดจำ
    /// (Dr ใน JE รับเงินของใบนั้น — เช่น 11340 เมื่อรับผ่าน gateway) · หาไม่ได้ = ปฏิเสธให้เลือก ไม่เดา 111 (F-03)</summary>
    private async Task<Guid> ResolveRefundMoneyAccountAsync(Guid companyId, Guid? bankAccountId, IReadOnlyList<LodgingDepositSnapshot> deposits)
    {
        if (bankAccountId is Guid b)
        {
            var linked = await _db.BankAccounts.AsNoTracking()
                .Where(x => x.Id == b && x.CompanyId == companyId)
                .Select(x => x.LinkedAccountId).FirstOrDefaultAsync();
            return linked ?? throw new BusinessRuleException(
                "บัญชีธนาคารที่เลือกยังไม่ได้ผูกผังบัญชี — ผูกผังที่หน้า “บัญชีธนาคาร” ก่อน หรือเลือกบัญชีอื่น", "LODGING-REFUND-ACCOUNT");
        }
        var depIds = deposits.Select(d => d.Id).ToList();
        var moneyIn = await (from l in _db.JournalEntryLines.AsNoTracking()
                             join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                             where j.CompanyId == companyId && j.SourceDocumentId != null && depIds.Contains(j.SourceDocumentId.Value)
                                   && j.JournalType == JournalType.CashReceipts
                                   && j.OriginalEntryId == null && j.ReversedByEntryId == null
                                   && j.Status == JournalEntryStatus.Posted && !j.IsDeleted && !l.IsDeleted
                                   && l.DebitAmount > 0
                             orderby l.DebitAmount descending
                             select (Guid?)l.AccountId).FirstOrDefaultAsync();
        return moneyIn ?? throw new BusinessRuleException(
            "หาบัญชีที่รับมัดจำเข้ามาไม่พบ — กรุณาเลือกบัญชีธนาคารที่ใช้โอนคืน", "LODGING-REFUND-ACCOUNT");
    }

    // ═══════════════════════════ เงินประกันความเสียหาย (รอบ 194 · spec S3) ═══════════════════════════
    // เงินประกันที่ต้องคืน ≠ มัดจำค่าห้อง: ยังไม่ใช่ค่าตอบแทน (ป.73/2541) ⇒ ไม่ใช่ tax point · ไม่นับใน PaidAmount/ยอดค้างของการเข้าพัก ·
    // ไม่เข้าแผนหัก/ริบของมัดจำค่าห้อง (RoomDeposits) · ปิดได้ 3 ทาง: คืน · ตัดชำระใบเช็คเอาต์ที่คิด VAT (ไม่ลดฐานภาษี) ·
    // ริบเป็นค่าเสียหาย (ไม่มี VAT — ความมั่นใจกฎหมายกลาง-ต่ำ ⇒ หน้าจอบอกให้เลือกแบบมี VAT ถ้าเป็นค่าของที่ใช้ไป/ค่าบริการ)

    /// <summary>รับเงินประกันความเสียหาย — ออกใบรับเงินมัดจำด้วยประเภทเงินประกันของที่พัก (<c>SecurityDepositKindId</c>) ·
    /// ผูก <c>LodgingReservation.SecurityDepositDocumentId</c> · ยอดเริ่มต้น = <c>LodgingProperty.SecurityDepositAmount</c></summary>
    public async Task<LodgingReservationResponse> ReceiveSecurityDepositAsync(
        Guid companyId, Guid reservationId, LodgingSecurityDepositReceiveRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        var prop = r.Property;
        if (r.Status is not (LodgingReservationStatus.Confirmed or LodgingReservationStatus.CheckedIn))
            throw new BusinessRuleException($"รับเงินประกันได้เมื่อการจองยืนยันแล้วหรือเช็คอินอยู่ (สถานะปัจจุบัน {StatusTh(r.Status)})", "LODGING-SECURITY");
        // C3 ฝ่ายค้านรอบ 194: ตัวตัดสินตัวเดียว "มีเงินประกันค้างไหม" — ใบที่ผูกถูกยกเลิก/ลบที่หน้าเอกสาร = ไม่มีเงินค้าง ⇒ รับใหม่ได้
        // (เดิมตรวจแค่ลิงก์+วันปิด ⇒ รับใหม่ไม่ได้และปิดก็ไม่ได้ตลอดไป เพราะตัวโหลดตัดใบที่ถูกยกเลิกทิ้ง)
        var secLink = LodgingDepositSettlement.SecurityLinkState(r.SecurityDepositDocumentId, r.SecurityDepositSettledAt,
            await LoadDepositSnapshotsAsync(companyId, r));
        if (secLink == LodgingSecurityLinkState.Open)
            throw new BusinessRuleException("รับเงินประกันของการจองนี้ไว้แล้วและยังไม่ได้ปิด — ถ้ายอดผิด ให้ปิด (คืน) ใบเดิมก่อนแล้วรับใหม่", "LODGING-SECURITY");
        var replacedGoneSecurityDocId = secLink == LodgingSecurityLinkState.DocumentGone ? r.SecurityDepositDocumentId : null;
        if (prop.AccountingMode == LodgingAccountingMode.Off)
            throw new BusinessRuleException("ที่พักตั้ง “ไม่ออกเอกสาร” — บันทึกเงินประกันในระบบบัญชีที่ใช้ออกเอกสารของที่พัก", "LODGING-SECURITY");
        var kindId = prop.SecurityDepositKindId ?? throw new BusinessRuleException(
            "ยังไม่ได้เลือก “ประเภทเงินประกันความเสียหาย” ของที่พัก — ตั้งที่หน้าตั้งค่าที่พัก › ภาษี & บัญชี ก่อน", "LODGING-SECURITY");
        var kindEntity = await _db.DepositKinds.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Id == kindId && k.CompanyId == companyId && !k.IsDeleted && k.IsActive);
        if (kindEntity is null || kindEntity.Nature != DepositNature.RefundableSecurity)
            throw new BusinessRuleException("ประเภทเงินประกันของที่พักถูกปิด/ลบ หรือไม่ใช่ “เงินประกัน (ต้องคืน)” — เลือกใหม่ที่หน้าตั้งค่าที่พัก", "LODGING-SECURITY");
        var amount = Math.Round(request.Amount ?? prop.SecurityDepositAmount, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0m)
            throw new BusinessRuleException("ระบุยอดเงินประกัน (ที่พักยังไม่ได้ตั้งยอดเริ่มต้น)", "LODGING-SECURITY");
        var contactId = r.ContactId ?? throw new BusinessRuleException("การจองไม่มีผู้ติดต่อ (Contact) — แก้ไขข้อมูลแขกก่อน");

        var vatRate = await EffectiveVatRateAsync(companyId, prop);
        var companySetting = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId).Select(s => s.DepositVatTreatment).FirstOrDefaultAsync();
        // ประเภทเงินประกันเป็น "ประเภทบนใบ" (ชั้น ①) · ไม่ผ่านชั้นของช่องทาง (ค่าของมัดจำค่าห้อง) — ตัวตัดสินตัวเดียว
        var kind = DepositPolicyResolver.ResolveKind(companyId, DepositSupplyNature.Service,
            documentKind: kindEntity, channelKind: null, channelTreatment: null, companyDefaultKind: null,
            companySetting: companySetting, chartHas21530: await ChartHas21530Async(companyId));
        var legacyShape = DepositPolicyResolver.ShapeFor(kind.Treatment, vatRate);
        var lines = new List<DocumentLineRequest>
        {
            new(Description: $"เงินประกันความเสียหาย {prop.Name} — จอง {r.ReservationNumber} (เงินที่ต้องคืน · หักได้เฉพาะค่าเสียหาย)",
                Quantity: 1, Unit: "รายการ", UnitPrice: amount, DiscountPercent: 0, VatRate: legacyShape.LineVatRate, WithholdingTaxRate: 0,
                AccountId: null)
        };
        var shaped = DepositDocumentShaping.Apply(lines, kind, vatRate, legacyShape.DepositOutputVatDeferred, null);
        var created = await _docService.CreateDocumentAsync(companyId, new CreateDocumentRequest(
            DocumentType: DocumentType.Receipt,
            DocumentDate: request.PaymentDate ?? DateTime.UtcNow,
            DueDate: null,
            ContactId: contactId,
            Reference: r.ReservationNumber,
            Notes: $"เงินประกันความเสียหาย การจองที่พัก {r.ReservationNumber} · เข้าพัก {r.CheckInDate:dd/MM/yyyy}–{r.CheckOutDate:dd/MM/yyyy} — "
                + "เงินที่ต้องคืน ยังไม่ใช่ค่าบริการ",
            Lines: shaped.Lines.ToList(),
            PricesIncludeVat: true,
            BankAccountId: request.BankAccountId,
            BranchId: prop.BranchId,
            IsDeposit: true,
            DepositDeferredAccountCode: shaped.DepositDeferredAccountCode,
            DepositOutputVatDeferred: shaped.DepositOutputVatDeferred,
            BookingNumber: r.ReservationNumber,
            PaymentType: null,
            // สัญญาทีม B รอบ 194: ตรึงประเภท/ลักษณะ "เงินประกัน" ลงใบ ⇒ ด่าน DEP-SEC-DEDUCT + ริบตามลักษณะ อ่านจากใบนี้
            DepositKindId: kindEntity.Id), userId, LodgingOrigin,
            // รอบ 194 R2 (P1 ค้าง): อัตราของที่พักเข้าตัวจัดรูปฝั่งเซิร์ฟเวอร์ด้วย — เดิมไม่ส่ง ⇒ DocumentService จัดรูปซ้ำด้วยอัตราบริษัท
            // (ที่พัก ChargeVat=false ได้รูปใบคนละแบบกับที่จัดไว้ข้างบน) · เส้นเดียวกับมัดจำค่าห้อง (CreateDepositReceiptAsync)
            depositChannelVatRate: vatRate);
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == created.Id && d.CompanyId == companyId);
        if (doc != null)
            doc.InternalNotes = DepositPolicyResolver.AppendNoteOnce(doc.InternalNotes,
                $"[เงินประกันความเสียหาย] {kind.Name} ({NatureLabelTh(kind.Nature)}) · {DepositPolicyResolver.LabelOf(kind.Treatment)} · "
                + $"บัญชี {shaped.DepositDeferredAccountCode ?? "ตามระบบ"} · การจอง {r.ReservationNumber}"
                + (kind.Warning != null ? $" · {kind.RuleCode}: {kind.Warning}" : ""));
        await _db.SaveChangesAsync();
        // ฝ่ายค้านรอบ 201 รอบสาม P2-6: ไม่มีคนเห็นคำเตือนในเส้นนี้ ⇒ SystemWorkflow — ผ่านเหมือนเดิม (ไม่หยุดการออกเอกสาร) แต่ร่องรอยบอกตามจริงว่าไม่ใช่คนรับทราบ (เดิม acknowledgeWarnings: true = ประทับ AcknowledgedByPerson)
        var approved = await _docService.ApproveDocumentAsync(companyId, created.Id, userId, ApprovalAckSource.SystemWorkflow, withAiHints: false);

        if (replacedGoneSecurityDocId != null)
            AppendInternal(r, LodgingDepositSettlement.SecurityDocumentGoneNote + $" — ลิงก์เดิม ({replacedGoneSecurityDocId}) ถูกแทนที่ด้วยใบใหม่");
        r.SecurityDepositDocumentId = created.Id;
        r.SecurityDepositSettledAt = null;
        AppendInternal(r, $"รับเงินประกันความเสียหาย {amount:N2} — {approved.DocumentNumber} (หนี้สิน · ไม่นับเป็นค่าห้อง)"
            + (!string.IsNullOrWhiteSpace(request.PaymentReference) ? $" อ้างอิง {request.PaymentReference.Trim()}" : "")
            + (!string.IsNullOrWhiteSpace(request.Note) ? $" — {request.Note.Trim()}" : "")
            + (kind.Warning != null ? $" · ⚠️ {kind.Warning}" : ""));
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Create, r, new
        {
            action = "SecurityDepositReceived", document = approved.DocumentNumber, amount, kind = kind.Code,
            nature = kind.Nature.ToString(), treatment = kind.Treatment.ToString(), account = shaped.DepositDeferredAccountCode,
            ruleCode = kind.RuleCode, replacedVoidedSecurityDocumentId = replacedGoneSecurityDocId, by = userId,
        }));
        await _db.SaveChangesAsync();
        return await MapAsync(companyId, r, true, true);
    }

    /// <summary>ปิดเงินประกัน (spec S3): ① ตัดชำระยอดค้างของใบเช็คเอาต์ (ใบนั้นคิด VAT ตามปกติ — เส้นเดิม ApplyDeposit) →
    /// ② ริบเป็นค่าเสียหาย (<c>ForfeitAs: Compensation</c> — รายได้อื่นไม่มี VAT) → ③ คืนส่วนที่เหลือ (เส้นเดิม RefundDeposit) ·
    /// ล็อกระดับการจองเดียวกับการคืนเงินค่าห้อง (ทั้งสองเส้นแตะ PaidAmount)</summary>
    public async Task<LodgingReservationResponse> SettleSecurityDepositAsync(
        Guid companyId, Guid reservationId, LodgingSecurityDepositSettleRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.LodgingRefundPaid, reservationId.ToString(),
            async () =>
            {
                await _db.Entry(r).ReloadAsync();   // ค่าล่าสุดใต้ล็อก
                await SettleSecurityDepositCoreAsync(companyId, r, request, userId);
            }, _logger, companyId);
        if (!ran)
            throw new BusinessRuleException("มีผู้ใช้อื่นกำลังบันทึกคืนเงิน/เงินประกันของการจองนี้อยู่ — รอสักครู่แล้วเปิดดูใหม่", "LODGING-REFUND-BUSY");
        return await MapAsync(companyId, r, true, true);
    }

    private async Task SettleSecurityDepositCoreAsync(Guid companyId, LodgingReservation r, LodgingSecurityDepositSettleRequest request, string userId)
    {
        if (r.Status is not (LodgingReservationStatus.CheckedOut or LodgingReservationStatus.Cancelled or LodgingReservationStatus.NoShow))
            throw new BusinessRuleException("ปิดเงินประกันได้หลังเช็คเอาต์ (หรือเมื่อการจองถูกยกเลิก/ไม่มาเข้าพัก) — ค่าเสียหายต้องอยู่ในใบเช็คเอาต์ก่อน", "LODGING-SECURITY");
        if (r.SecurityDepositDocumentId is null)
            throw new BusinessRuleException("การจองนี้ไม่มีเงินประกัน", "LODGING-SECURITY");
        var security = LodgingDepositSettlement.SecurityDeposit(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId)
            ?? throw new BusinessRuleException("ไม่พบใบเงินประกันที่อนุมัติแล้ว (ถูกยกเลิก/ยังเป็นร่าง) — ตรวจที่หน้า “เงินมัดจำ”", "LODGING-SECURITY");
        var paidAt = request.PaidAt ?? DateTime.UtcNow;
        if (paidAt > DateTime.UtcNow.AddDays(1))
            throw new BusinessRuleException("วันที่ต้องไม่เป็นวันในอนาคต", "LODGING-SECURITY");
        var forfeitReason = (request.ForfeitReason ?? "").Trim();
        if (request.ForfeitAsCompensation > 0.005m && forfeitReason.Length == 0)
            throw new BusinessRuleException("ริบเป็นค่าเสียหายต้องระบุรายการความเสียหาย (หลักฐานว่าเป็นค่าสินไหมทดแทน ไม่ใช่ค่าบริการ)", "LODGING-SECURITY");

        // ใบเช็คเอาต์ที่ตัดชำระได้ — อนุมัติแล้ว ไม่ถูกยกเลิก · ต้องเป็นลูกค้ารายเดียวกับใบเงินประกัน
        decimal? finalBalance = null;
        var sameContact = false;
        string? finalNumber = null;
        if (r.FinalDocumentId is Guid fid)
        {
            var fin = await _docService.GetDocumentAsync(companyId, fid);
            if (fin.Status is not (DocumentStatus.Voided or DocumentStatus.Rejected or DocumentStatus.Draft))
            {
                finalBalance = fin.BalanceDue;
                finalNumber = fin.DocumentNumber;
                var finContact = await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == fid && d.CompanyId == companyId).Select(d => d.ContactId).FirstOrDefaultAsync();
                sameContact = finContact == security.ContactId;
            }
        }
        var (plan, problem) = LodgingDepositSettlement.PlanSecuritySettlement(
            security, request.ApplyToFinalInvoice, request.ForfeitAsCompensation, finalBalance, sameContact);
        if (plan is null)
            throw new BusinessRuleException(problem ?? "ปิดเงินประกันไม่ได้", "LODGING-SECURITY");
        if (plan.ApplyGross <= 0.005m && plan.ForfeitGross <= 0.005m && (!request.RefundRemainder || plan.RemainderGross <= 0.005m))
            throw new BusinessRuleException("ไม่มีอะไรให้บันทึก — ระบุยอดตัดชำระ/ริบ หรือเลือก “คืนส่วนที่เหลือ”", "LODGING-SECURITY");
        // ใบลดหนี้ของเงินประกันที่เคยออกใบกำกับ (ตั้งประเภทผิดโหมด) ห้ามลงย้อนเข้างวดที่ยื่นแล้ว — ด่านเดียวกับคืนเงินค่าห้อง
        if (request.RefundRemainder && plan.RemainderGross > 0.005m && security.VatAmount > 0.005m && await VatPeriodFiledAsync(companyId, paidAt))
            throw new BusinessRuleException($"งวด ภ.พ.30 {paidAt:MM/yyyy} ยื่นแล้ว — ลงวันที่คืนเงินประกันย้อนเข้างวดนั้นไม่ได้ · ใช้วันที่โอนจริงในงวดที่ยังเปิด", "LODGING-REFUND-PERIOD");
        var reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();

        var done = new List<string>();
        try
        {
            if (plan.ApplyGross > 0.005m)
            {
                // เส้นเดิม: Dr เงินประกัน (21530/21620) / Cr ลูกหนี้ — รับชำระหนี้ของใบที่คิด VAT แล้ว (ไม่ลดฐานภาษี · ไม่ใช่ DepositBaseDeducted)
                await _docService.ApplyDepositToInvoiceAsync(companyId, r.FinalDocumentId!.Value,
                    new ApplyDepositRequest(security.Id, plan.ApplyGross, paidAt), userId);
                r.PaidAmount = Math.Round(r.PaidAmount + plan.ApplyGross, 2, MidpointRounding.AwayFromZero);
                done.Add($"ตัดชำระ {finalNumber} {plan.ApplyGross:N2}");
                await _db.SaveChangesAsync();   // ทีละขั้น — ขั้นถัดไปล้มแล้วกดใหม่ ต้องไม่ตัดซ้ำโดยไม่รู้ตัว (ยอดค้างของใบลดตามจริง)
            }
            if (plan.ForfeitBase > 0.005m)
            {
                var account = await SecurityForfeitAccountAsync(companyId, security.Id, r.Property);
                await _docService.RealizeDepositAsync(companyId, security.Id,
                    new RealizeDepositRequest(plan.ForfeitBase, paidAt, account, ForfeitAs: DepositForfeitAs.Compensation), userId);
                done.Add($"ริบเป็นค่าเสียหาย {plan.ForfeitGross:N2} ({forfeitReason})");
            }
            if (request.RefundRemainder)
            {
                // ยอดคืนจากสถานะจริงหลังขั้นก่อนหน้า (สูตรปัดตัวเดียวกับการคืนมัดจำ) — ไม่ใช้ตัวเลขในแผนตรง ๆ กันเศษ 0.01 ติดด่านคืนเกิน
                var fresh = LodgingDepositSettlement.SecurityDeposit(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId);
                var refund = fresh is null ? 0m : LodgingDepositSettlement.HeldGross(fresh);
                if (refund > 0.005m)
                {
                    var moneyAccountId = await ResolveRefundMoneyAccountAsync(companyId, request.BankAccountId, new[] { security });
                    await _docService.RefundDepositAsync(companyId, security.Id, new RefundDepositRequest(refund, paidAt,
                        $"คืนเงินประกันความเสียหาย {r.ReservationNumber}" + (reference != null ? $" · อ้างอิง {reference}" : ""),
                        moneyAccountId), userId);
                    done.Add($"คืน {refund:N2}");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ล้มดัง 3 ที่ (F2 ข้อ 7): หมายเหตุบนการจอง · audit · คำตอบผู้เรียก — ขั้นที่ทำแล้วถูกบันทึกแล้ว (ยอดคงเหลือบนหน้าจอเป็นของจริง)
            var doneText = done.Count == 0 ? "ยังไม่มีขั้นใดสำเร็จ" : "ทำแล้ว: " + string.Join(" · ", done);
            AppendInternal(r, $"⚠️ ปิดเงินประกันไม่ครบ ({ex.Message}) — {doneText}");
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "SecurityDepositSettleFailed", error = ex.Message, done, by = userId }));
            await _db.SaveChangesAsync();
            throw new BusinessRuleException(
                $"ปิดเงินประกันไม่ครบ ({ex.Message}) — {doneText} · เปิดการจองใหม่ดูยอดเงินประกันคงเหลือ แล้วเลือกเฉพาะส่วนที่ยังค้าง",
                "LODGING-SECURITY-SETTLE");
        }

        var after = LodgingDepositSettlement.SecurityDeposit(await LoadDepositSnapshotsAsync(companyId, r), r.SecurityDepositDocumentId);
        var held = after is null ? 0m : LodgingDepositSettlement.HeldGross(after);
        if (held <= 0.005m) r.SecurityDepositSettledAt = DateTime.UtcNow;
        AppendInternal(r, $"เงินประกัน {security.Number}: {string.Join(" · ", done)}"
            + (held > 0.005m ? $" · คงเหลือ {held:N2} (ยังเป็นหนี้สิน)" : " · ปิดครบ")
            + (!string.IsNullOrWhiteSpace(request.Note) ? $" — {request.Note.Trim()}" : ""));
        r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new
        {
            action = "SecurityDepositSettled", document = security.Number, applied = plan.ApplyGross, finalDocument = finalNumber,
            forfeitCompensation = plan.ForfeitGross, forfeitBase = plan.ForfeitBase, forfeitReason, refunded = request.RefundRemainder,
            held, reference, by = userId,
        }));
        await _db.SaveChangesAsync();
    }

    /// <summary>บัญชีรายได้ของ "ริบเงินประกันเป็นค่าเสียหาย" — บัญชีริบของประเภทที่ตรึงบนใบ (<c>DepositKind.ForfeitAccountCode</c>) เท่านั้น ·
    /// ไม่ตั้ง ⇒ null ให้ DocumentService เลือกบัญชีรายได้อื่น (43080 ค่าปรับ/ค่าเสียหายที่ได้รับ → 43070 → ล้มดังพร้อมทางไปต่อ ·
    /// <c>DepositPolicyResolver.RevenueAccountPlan</c> ตัวเดียว) · รอบ 194 P-c: เดิมตกไปค่าปรับยกเลิก/รายได้ค่าห้อง = รายได้ขายที่ไม่มีใน ภ.พ.30
    /// ⇒ กระทบยอดรายได้ GL↔ภ.พ.30 ไม่ลง (ค่าธรรมเนียมยกเลิกของมัดจำค่าห้องยังใช้ CancellationFeeAccountCode เดิม — คนละเส้น)</summary>
    /// <param name="prop">คงไว้ให้จุดเรียกเดิม (ส่วนรับ/ปิดเงินประกันเป็นของอีกทีมในรอบนี้) — ไม่ใช้ตัดสินบัญชีแล้ว (ห้ามตกไปบัญชีรายได้ของที่พัก)</param>
    private async Task<string?> SecurityForfeitAccountAsync(Guid companyId, Guid securityDocumentId, LodgingProperty prop)
    {
        _ = prop;
        var kindId = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == securityDocumentId && d.CompanyId == companyId).Select(d => d.DepositKindId).FirstOrDefaultAsync();
        string? kindAccount = null;
        if (kindId is Guid k)
            kindAccount = await _db.DepositKinds.AsNoTracking()
                .Where(x => x.Id == k && x.CompanyId == companyId).Select(x => x.ForfeitAccountCode).FirstOrDefaultAsync();
        return !string.IsNullOrWhiteSpace(kindAccount) ? kindAccount.Trim() : null;
    }

    private static (IReadOnlyList<LodgingCancellationRule> Rules, bool NonRefundable) SnapshotRules(LodgingReservation r)
    {
        if (string.IsNullOrWhiteSpace(r.CancellationPolicySnapshotJson)) return (Array.Empty<LodgingCancellationRule>(), false);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.CancellationPolicySnapshotJson);
            var root = doc.RootElement;
            var nr = root.TryGetProperty("nonRefundable", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.True;
            var rules = root.TryGetProperty("rules", out var arr) ? LodgingPricingEngine.ParseRules(arr.GetRawText()) : Array.Empty<LodgingCancellationRule>();
            return (rules, nr);
        }
        catch { return (Array.Empty<LodgingCancellationRule>(), false); }
    }

    // ═══════════════════════════ Reschedule ═══════════════════════════

    public async Task<LodgingReservationResponse> RescheduleAsync(Guid companyId, Guid reservationId, LodgingRescheduleRequest request, string userId)
    {
        var r = await RequireReservationAsync(companyId, reservationId);
        if (r.Status != LodgingReservationStatus.Pending && r.Status != LodgingReservationStatus.Confirmed)
            throw new BusinessRuleException($"เลื่อนวันได้เฉพาะการจองที่ยังไม่เช็คอิน (สถานะ {StatusTh(r.Status)})");
        var checkIn = ThaiDate.CalendarDateUtc(request.CheckIn); var checkOut = ThaiDate.CalendarDateUtc(request.CheckOut);
        // O-P0-1 รอบ 202: ตรวจห้องว่างช่วงใหม่ + คิดราคา + บันทึก ใต้ล็อกที่พัก (เดิมนอกล็อก ⇒ เลื่อนเข้าช่วงที่เพิ่งถูกจองห้องสุดท้ายพร้อมกันได้)
        await WithPropertyLockAsync(companyId, r.PropertyId, async () =>
        {
            var ctx = await LoadContextAsync(companyId, r.PropertyId, checkIn, checkOut, excludeReservationId: r.Id);
            var roomList = r.Rooms.ToList();
            var rooms = roomList.Select(x => new LodgingQuoteRoomRequest(x.RoomTypeId, x.Adults, x.Children, x.ExtraBeds)).ToList();
            var extrasWithId = r.Extras.Where(e => e.ExtraId != null).ToList();
            var extras = extrasWithId.Select(e => new LodgingQuoteExtraRequest(e.ExtraId!.Value, e.Quantity)).ToList();
            // ฝ่ายค้านรอบ 202 P2-4: ใบที่ซื้อคนเสริมไว้ก่อนที่พักปิดเตียงเสริม ⇒ คำเตือน + คงราคาคนเสริมเดิม (เส้นพนักงานเท่านั้น)
            var quote = BuildQuote(ctx, checkIn, checkOut, rooms, extras, r.RatePlanId, isStaff: true, excludeReservationId: r.Id,
                staffKeptExtraBedPrice: KeptExtraBedPrices(r),
                reservationMode: r.GuestConfirmMode);   // คำตัดสินข้อ 128: มัดจำที่ต้องชำระตามโหมดที่ตรึงบนใบ (ไม่ใช่โหมดของเส้นพนักงาน)
            if (quote.Errors.Count > 0) throw new BusinessRuleException(string.Join(" · ", quote.Errors));
            foreach (var w in quote.Warnings) AppendInternal(r, $"เลื่อนวัน: {w}");

            // P2 รอบ 202: จับคู่บรรทัดด้วยกุญแจ ไม่ใช่ลำดับ — ตัวคิดราคาจัดกลุ่มตามประเภทห้อง (ห้อง A,B,A ⇒ ราคาออกมา A,A,B) และข้ามบริการเสริม
            // ที่ไม่มี ExtraId/ถูกปิด ⇒ index เดิมเอาราคาของอีกบรรทัดมาใส่ (ยอดรวมถูกแต่รายบรรทัดผิด · ใบเช็คเอาต์พิมพ์จากรายบรรทัด)
            // (ภายในประเภทเดียวกัน ตัวคิดราคาคงลำดับเดิม ⇒ กุญแจ = ประเภทห้อง ก็พอ · จำนวนแขก/เตียงเสริมที่ถูก clamp ไม่ทำให้หลุดคู่)
            var roomPairs = LodgingBookingGuards.PairByKey(
                roomList.Select(x => x.RoomTypeId).ToList(), quote.Rooms.Select(q => q.RoomTypeId).ToList());
            var extraPairs = LodgingBookingGuards.PairByKey(
                extrasWithId.Select(e => e.ExtraId!.Value).ToList(), quote.Extras.Select(q => q.ExtraId).ToList());
            if (roomPairs.Any(x => x == null))
                throw new BusinessRuleException("คิดราคาห้องช่วงใหม่ไม่ครบทุกห้อง (ประเภทห้องถูกปิด/ลบ) — จัดประเภทห้องใหม่ก่อนเลื่อนวัน", "LODGING-RESCHEDULE-LINES");
            var droppedExtras = extrasWithId.Where((e, i) => extraPairs[i] == null).Select(e => e.Name).ToList();
            if (droppedExtras.Count > 0)
                throw new BusinessRuleException($"บริการเสริม {string.Join(", ", droppedExtras)} คิดราคาช่วงใหม่ไม่ได้ (ถูกปิด/ลบจากผังแล้ว) — ยกเลิกบริการนั้นก่อนเลื่อนวัน",
                    "LODGING-RESCHEDULE-LINES");

            var old = new { r.CheckInDate, r.CheckOutDate, r.TotalAmount };
            r.CheckInDate = checkIn; r.CheckOutDate = checkOut; r.Nights = quote.Nights;
            r.RoomSubtotal = quote.RoomSubtotal; r.ExtrasTotal = quote.ExtrasTotal; r.ServiceChargeAmount = quote.ServiceChargeAmount;
            r.VatAmount = quote.VatAmount; r.TotalAmount = quote.TotalAmount; r.PriceBreakdownJson = J(quote);
            // มัดจำที่รับแล้วคงเดิม (เงินอยู่ในบัญชีแล้ว) — เปลี่ยนเฉพาะ "ที่ต้องเรียกเก็บ" ถ้ายังไม่จ่าย
            if (r.DepositPaid <= 0) r.DepositRequired = quote.DepositRequired;
            for (var i = 0; i < roomList.Count; i++)
            {
                var q = quote.Rooms[roomPairs[i]!.Value];
                roomList[i].Subtotal = q.Subtotal; roomList[i].NightlyRatesJson = J(q.Nights);
                roomList[i].UnitId = null; roomList[i].Unit = null;   // ห้องเดิมอาจไม่ว่างในช่วงใหม่ — ให้จัดใหม่
            }
            for (var i = 0; i < extrasWithId.Count; i++)
                extrasWithId[i].Total = quote.Extras[extraPairs[i]!.Value].Total;
            AppendInternal(r, $"เลื่อนวัน {old.CheckInDate:dd/MM/yyyy}–{old.CheckOutDate:dd/MM/yyyy} → {checkIn:dd/MM/yyyy}–{checkOut:dd/MM/yyyy} · ยอด {old.TotalAmount:N2} → {r.TotalAmount:N2}{(string.IsNullOrWhiteSpace(request.Reason) ? "" : $" ({request.Reason})")}");
            r.UpdatedBy = userId; r.UpdatedAt = DateTime.UtcNow;
            _db.AddChainedAuditLog(Audit(companyId, AuditAction.Update, r, new { action = "Reschedule", old, @new = new { checkIn, checkOut, r.TotalAmount }, reason = request.Reason, by = userId }));
            await _db.SaveChangesAsync();
            return true;
        });
        return await MapAsync(companyId, r, true, true);
    }
}
