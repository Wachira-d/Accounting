using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 228 (<c>erp-review/2026-11-01/BOOKING_PAYMENT_AUDIT.md</c> §9) — หน้า "ดู/จัดการการจองของฉัน" (my-booking/{token}) ส่วนที่ลูกค้าทำเองได้เพิ่ม:
/// <list type="bullet">
///   <item><b>มัดจำไม่บังคับ — เลือกชำระภายหลัง</b> (คำตัดสิน 228-1 · ตัวตัดสิน <see cref="BookingDepositLater"/>): ตั้งยอดมัดจำแล้วใช้ขั้นชำระเดิม (ออนไลน์ · โอน/QR · สลิป)</item>
///   <item><b>เปลี่ยนวิธีรับ/ที่อยู่ก่อนกำหนด</b> (คำตัดสิน 228-3 · ตัวตัดสิน <see cref="BookingFulfillmentChange"/>): ค่าส่งคิดใหม่ที่เซิร์ฟเวอร์ด้วยตัวคิดเดียวกับตอนจอง</item>
/// </list>
/// ทุกคำขอเดินใต้ล็อกสถานะของการจอง (ตัวเดียวกับยกเลิก/รับเงิน) — กดซ้ำ/สองแท็บ/เงินเข้าพร้อมกัน ไม่เขียนทับกัน
/// </summary>
public partial class CmsBookingService
{
    private async Task<Guid> BookingIdByTokenAsync(Guid companyId, Guid siteId, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new KeyNotFoundException("Booking not found.");
        return await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.PublicToken == token)
            .Select(b => (Guid?)b.Id).FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("Booking not found.");
    }

    private async Task<BookingResponse> CustomerViewAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        var resp = await GetBookingAsync(companyId, siteId, bookingId) ?? throw new KeyNotFoundException("Booking not found.");
        await FillManageInfoAsync(companyId, siteId, resp);
        return ForCustomer(resp);
    }

    // ═══════════════ มัดจำไม่บังคับ — เลือกชำระภายหลัง ═══════════════

    /// <summary>ผลตัดสิน "เลือกชำระมัดจำภายหลัง" ของการจองหนึ่งใบ (ค่าตั้งปัจจุบันของเว็บ/บริการ · ชนิดที่ตรึงบนการจอง)</summary>
    private async Task<BookingDepositLaterDecision> DepositLaterOfAsync(Guid companyId, Guid siteId, SiteBooking b, SiteBookingService svc, DateTime nowUtc)
    {
        var siteRow = await _db.SiteBookingSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        var eff = PaymentOf(svc, siteRow, b.BookingType);
        var d = BookingDepositLater.Decide(eff, b.BookingType, b.Status, b.DepositAmount, b.PaidAmount, b.TotalAmount, b.DeliveryFee,
            CreditBookingLink.StartUtc(b.BookingDate, b.StartTime), nowUtc, await IsPaidWithCreditAsync(companyId, b.Id));
        if (!d.Allowed) return d;
        // ช่องทางของเว็บต้องพร้อมอย่างน้อยหนึ่ง (ร้านปิดช่องทางภายหลัง ⇒ ไม่ให้ตั้งยอดที่ลูกค้าจ่ายไม่ได้)
        var ctx = await LoadPaymentContextAsync(companyId, siteId);
        return ctx.TransferReady || ctx.GatewayReady ? d
            : new BookingDepositLaterDecision(true, false, 0m, "ร้านยังไม่เปิดช่องทางชำระออนไลน์/โอน — ชำระมัดจำที่ร้าน หรือติดต่อร้าน");
    }

    public async Task<BookingResponse> ChooseDepositLaterByCustomerAsync(Guid companyId, Guid siteId, string token)
    {
        var id = await BookingIdByTokenAsync(companyId, siteId, token);
        return await WithBookingSessionLockAsync(companyId, BookingStatusLockPart(id), async () =>
        {
            var now = DateTime.UtcNow;
            var b = await _db.SiteBookings.Include(x => x.BookingService)
                .FirstOrDefaultAsync(x => x.Id == id && x.SiteId == siteId && x.CompanyId == companyId)
                ?? throw new KeyNotFoundException("Booking not found.");
            var d = await DepositLaterOfAsync(companyId, siteId, b, b.BookingService, now);
            if (!d.Show) throw new BusinessRuleException("การจองนี้ไม่มีมัดจำให้เลือกชำระ (ชำระแล้ว/เลือกไว้แล้ว/ร้านไม่ได้ตั้งมัดจำไม่บังคับ)", BookingDepositLater.RuleCode, 409);
            if (!d.Allowed) throw new BusinessRuleException(d.Problem!, BookingDepositLater.RuleCode, 409);
            b.DepositAmount = d.Amount;
            b.InternalNotes = DepositPolicyResolver.AppendNoteOnce(b.InternalNotes, BookingDepositLater.ChosenNote(d.Amount, now));
            b.UpdatedBy = CreditBookingLink.CustomerActorPrefix + token[..Math.Min(8, token.Length)];
            await _db.SaveChangesAsync();
            _logger.LogInformation("การจอง {Booking}: ลูกค้าเลือกชำระมัดจำภายหลัง {Amount}", b.BookingNumber, d.Amount);
            return await CustomerViewAsync(companyId, siteId, id);
        });
    }

    // ═══════════════ เปลี่ยนวิธีรับ/ที่อยู่ ═══════════════

    private async Task<BookingLinkedDocKind> LinkedDocKindAsync(Guid companyId, Guid? erpDocumentId)
    {
        if (erpDocumentId is not Guid docId) return BookingLinkedDocKind.None;
        var d = await _db.Documents.AsNoTracking().Where(x => x.Id == docId && x.CompanyId == companyId)
            .Select(x => new { x.IsDeposit, x.Status, x.IsDeleted, x.DocumentNumber }).FirstOrDefaultAsync();
        if (d == null || d.IsDeleted || d.Status is DocumentStatus.Voided or DocumentStatus.Rejected) return BookingLinkedDocKind.None;
        if (!d.IsDeposit) return BookingLinkedDocKind.Other;
        // ฝ่ายค้านรอบ 228 S2/S8: รออนุมัติ หรือมีเลขแล้ว = ถือว่าออกแล้ว (ร้านต้องปรับเอกสาร) · ลบได้เฉพาะร่างเลข DRAFT-
        return BookingFulfillmentChange.DepositDocKind(d.Status, d.DocumentNumber);
    }

    private async Task<bool> GatewayInProgressAsync(Guid companyId, Guid bookingId, DateTime nowUtc)
    {
        var open = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.SiteBooking && i.SourceId == bookingId
                && (i.Status == PaymentIntentStatus.Created || i.Status == PaymentIntentStatus.Pending))
            .Select(i => new { i.QrExpiresAt, i.CreatedAt }).ToListAsync();
        return open.Any(i => BookingPaymentPolicy.GatewayPaymentInProgress(true, i.QrExpiresAt, i.CreatedAt, nowUtc));
    }

    public async Task<ChangeBookingFulfillmentResponse> ChangeFulfillmentByCustomerAsync(Guid companyId, Guid siteId, string token, ChangeBookingFulfillmentRequest request)
    {
        var id = await BookingIdByTokenAsync(companyId, siteId, token);
        var actor = CreditBookingLink.CustomerActorPrefix + token[..Math.Min(8, token.Length)];
        // ตัวอย่าง (ไม่บันทึก) ไม่ต้องถือล็อก — ผลจริงตัดสินซ้ำใต้ล็อกตอนบันทึก
        if (request.PreviewOnly) return await ChangeFulfillmentCoreAsync(companyId, siteId, id, request, actor, apply: false);
        var resp = await WithBookingSessionLockAsync(companyId, BookingStatusLockPart(id),
            () => ChangeFulfillmentCoreAsync(companyId, siteId, id, request, actor, apply: true));
        // ฝ่ายค้านรอบ 228 S3: แจ้งร้านหลังปล่อยล็อก (ช่องส่งช้า/ล่มไม่ถือล็อกของการจอง — แบบเดียวกับแนบสลิป)
        if (resp.Applied) await NotifyShopFulfillmentChangedAsync(companyId, siteId, id, resp.Effect ?? "");
        resp.Booking = resp.Applied ? await CustomerViewAsync(companyId, siteId, id) : resp.Booking;
        return resp;
    }

    private async Task<ChangeBookingFulfillmentResponse> ChangeFulfillmentCoreAsync(Guid companyId, Guid siteId, Guid id,
        ChangeBookingFulfillmentRequest request, string actor, bool apply)
    {
        var now = DateTime.UtcNow;
        var b = await _db.SiteBookings.Include(x => x.BookingService)
            .FirstOrDefaultAsync(x => x.Id == id && x.SiteId == siteId && x.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Booking not found.");
        var svc = b.BookingService;
        var siteRow = await _db.SiteBookingSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        var startUtc = CreditBookingLink.StartUtc(b.BookingDate, b.StartTime);
        var resp = new ChangeBookingFulfillmentResponse { OldFee = b.DeliveryFee };

        // หน้าต่างก่อน (ร้านไม่เปิด/เลยกำหนด ⇒ ไม่ต้องคิดค่าส่ง)
        var window = BookingFulfillmentChange.Window(siteRow?.FulfillmentChangeCutoffHours, b.Status, startUtc, now);
        if (!window.Enabled || !window.Open)
        {
            var closed = BookingFulfillmentChange.Decide(new BookingFulfillmentChangeFacts(b.Status, startUtc, now, siteRow?.FulfillmentChangeCutoffHours,
                b.TotalAmount, b.DeliveryFee, b.DeliveryFee, b.DepositAmount, b.DepositAmount, b.PaidAmount, false, false, BookingLinkedDocKind.None, false));
            if (apply) throw new BusinessRuleException(closed.Problem!, BookingFulfillmentChange.RuleCode, 409);
            resp.Problem = closed.Problem;
            return resp;
        }

        // วิธี + ที่อยู่ + ค่าส่ง: ตัวตัดสิน/ตัวคิดเดียวกับตอนจอง (BookingFulfillment + QuoteFulfillmentAsync → ShopDeliveryFee)
        var eff = FulfillmentOf(svc, siteRow);
        string? method;
        try { method = BookingFulfillment.ResolveMethod(eff, request.FulfillmentMethod); }
        catch (BusinessRuleException ex) when (!apply)
        {
            // ตัวอย่าง: ยังไม่ได้เลือกวิธี ⇒ ส่งวิธีที่เปิดไปให้หน้าเว็บวาดตัวเลือก (ไม่ใช่ฟอร์มว่าง)
            resp.Quote = new BookingFulfillmentQuoteDto { Enabled = eff.Enabled, DeliveryEnabled = eff.Delivery, PickupEnabled = eff.Pickup,
                DefaultMethod = eff.DefaultMethod, PickupNote = eff.Pickup ? siteRow?.FulfillmentPickupNote : null };
            resp.Problem = ex.Message;
            return resp;
        }
        if (method == null) throw new BusinessRuleException("บริการนี้ไม่มีตัวเลือกจัดส่ง/รับเอง", BookingFulfillment.RuleCode, 409);
        var addrErrs = BookingFulfillment.AddressProblems(method, request.DeliveryAddress, request.DeliveryProvince, request.DeliveryPostalCode);
        if (addrErrs.Count > 0 && apply) throw new BusinessRuleException(string.Join(" · ", addrErrs), BookingFulfillment.RuleCode);
        var quote = await QuoteFulfillmentAsync(companyId, siteId, svc, siteRow, b.TotalAmount, method, request.DeliveryProvince,
            request.DeliveryPostalCode, request.DeliveryRateId, strict: apply);
        resp.Quote = quote.Dto;
        if (addrErrs.Count > 0 || quote.Dto.Problem != null) { resp.Problem = addrErrs.Count > 0 ? string.Join(" · ", addrErrs) : quote.Dto.Problem; return resp; }

        var newAddress = method == FulfillmentMethod.Delivery ? request.DeliveryAddress?.Trim() : null;
        var newProvince = method == FulfillmentMethod.Delivery ? request.DeliveryProvince?.Trim() : null;
        var newPostal = method == FulfillmentMethod.Delivery ? request.DeliveryPostalCode?.Trim() : null;
        if (method == b.FulfillmentMethod && newAddress == b.DeliveryAddress && newProvince == b.DeliveryProvince && newPostal == b.DeliveryPostalCode
            && quote.Rate?.RateId == b.DeliveryRateId)
        {
            // ฝ่ายค้านรอบ 228 N6: บันทึกซ้ำ (กดสองครั้ง/ลองใหม่) ⇒ คืนสถานะปัจจุบัน ไม่ใช่ข้อผิดพลาด
            if (apply) { resp.Booking = await CustomerViewAsync(companyId, siteId, id); resp.Effect = "ไม่มีอะไรเปลี่ยน — วิธีรับ/ที่อยู่ตรงกับที่บันทึกไว้แล้ว"; return resp; }
            resp.NewFee = b.DeliveryFee;
            resp.NewGrandTotal = BookingFulfillment.GrandTotal(b.TotalAmount, b.DeliveryFee);
            resp.Effect = "ยังไม่มีอะไรเปลี่ยน — เลือกวิธีรับหรือแก้ที่อยู่ แล้วระบบจะคิดค่าจัดส่งให้";
            return resp;
        }

        // มัดจำที่ตรึงตอนจองไม่คิดใหม่จากค่าตั้งปัจจุบัน — ขยับเฉพาะ "ชำระเต็มจำนวน" (มัดจำ = ค่าบริการ + ค่าส่ง · คำตัดสิน 226-4) ⇒ ยอดรวมใหม่ ·
        // มัดจำ (บาท/%) คิดจากค่าบริการ ⇒ ค่าส่งเปลี่ยนไม่กระทบ · มัดจำไม่บังคับที่ยังไม่เลือก = 0 ต่อไป
        // ฝ่ายค้านรอบ 228 S1: ตัดสินจากข้อเท็จจริงที่ตรึงบนการจอง (มัดจำเดิมครอบยอดรวมเดิม) ไม่ใช่โหมดปัจจุบันของเว็บ
        var newDeposit = BookingFulfillmentChange.NewDeposit(b.DepositAmount, b.TotalAmount, b.DeliveryFee, quote.Fee);
        var decision = BookingFulfillmentChange.Decide(new BookingFulfillmentChangeFacts(b.Status, startUtc, now, siteRow?.FulfillmentChangeCutoffHours,
            b.TotalAmount, b.DeliveryFee, quote.Fee, b.DepositAmount, newDeposit, b.PaidAmount,
            SlipPending: b.SlipUploadedAt != null && b.SlipAttachmentId != null,
            GatewayInProgress: await GatewayInProgressAsync(companyId, b.Id, now),
            LinkedDoc: await LinkedDocKindAsync(companyId, b.ErpDocumentId),
            PaidWithCredit: await IsPaidWithCreditAsync(companyId, b.Id),
            PosBillOpen: b.PosOrderId != null));
        resp.Allowed = decision.Allowed;
        resp.Problem = decision.Problem;
        resp.NewFee = quote.Fee;
        resp.NewGrandTotal = BookingFulfillment.GrandTotal(b.TotalAmount, quote.Fee);
        resp.NewDepositAmount = decision.DepositAmount;
        resp.Effect = decision.Effect;
        if (!apply) return resp;
        if (!decision.Allowed) throw new BusinessRuleException(decision.Problem!, BookingFulfillmentChange.RuleCode, 409);
        // ฝ่ายค้านรอบ 228 S6: ลูกค้ายืนยันยอดที่เห็นในตัวอย่าง — ยอดเปลี่ยนระหว่างนั้น (ร้านแก้อัตรา/โซน) ⇒ ไม่บันทึก ให้ดูใหม่
        if (request.ExpectedNewFee is not decimal expFee || request.ExpectedDepositAmount is not decimal expDep
            || Math.Abs(expFee - quote.Fee) > 0.005m || Math.Abs(expDep - decision.DepositAmount) > 0.005m)
            throw new BusinessRuleException(BookingFulfillmentChange.AmountChangedMessage, BookingFulfillmentChange.RuleCode, 409);

        // ฝ่ายค้านรอบ 228 S8: ใบรับมัดจำร่างที่ยอดไม่ตรงแล้ว ⇒ เลิกผูกและบันทึกการจองก่อน แล้วค่อยลบใบร่าง (ลบล้ม = ใบร่างกำพร้าที่ไม่ลงบัญชี + หมายเหตุ
        // ให้ร้านลบเอง · ไม่มีทาง "การจองชี้ใบที่ถูกลบ") · ลบได้เฉพาะร่างเลข DRAFT- (ตัดสินแล้วใน LinkedDocKindAsync)
        var oldMethod = b.FulfillmentMethod;
        Guid? draftToDelete = null;
        if (decision.DiscardDraftDeposit && b.ErpDocumentId is Guid draftId)
        {
            draftToDelete = draftId;
            b.ErpDocumentId = null;
            b.ErpDocumentType = null;
        }
        b.FulfillmentMethod = quote.Method;
        b.DeliveryFee = quote.Fee;
        b.DeliveryAddress = newAddress; b.DeliveryProvince = newProvince; b.DeliveryPostalCode = newPostal;
        b.DeliveryRateId = quote.Rate?.RateId; b.DeliveryRateName = quote.Rate?.Name;
        b.DepositAmount = decision.DepositAmount;
        b.InternalNotes = DepositPolicyResolver.AppendNoteOnce(b.InternalNotes,
            BookingFulfillmentChange.ChangedNote(oldMethod, quote.Method, decision.Effect, now, discardedDraft: false));
        b.UpdatedBy = actor;
        await _db.SaveChangesAsync();
        _logger.LogInformation("การจอง {Booking}: ลูกค้าเปลี่ยนวิธีรับ {Old} → {New} ค่าส่ง {Fee}", b.BookingNumber, oldMethod, quote.Method, quote.Fee);
        if (draftToDelete is Guid del)
        {
            try
            {
                if (_docService == null) throw new InvalidOperationException("ระบบเอกสารไม่พร้อม");
                await _docService.DeleteDocumentAsync(companyId, del);
                // ฝ่ายค้าน (รีวิว A–C): ข้อความ "ลบใบร่างแล้ว" ประทับเฉพาะเมื่อลบสำเร็จจริง
                var noted = await _db.SiteBookings.FirstAsync(x => x.Id == id && x.SiteId == siteId && x.CompanyId == companyId);
                noted.InternalNotes = DepositPolicyResolver.AppendNoteOnce(noted.InternalNotes,
                    $"[{BookingFulfillmentChange.RuleCode}] ลบใบรับมัดจำร่างที่ยอดไม่ตรงแล้ว (ออกใหม่อัตโนมัติตอนรับเงิน)");
                await _db.SaveChangesAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ลบใบรับมัดจำร่าง {Doc} ของการจอง {Booking} ไม่สำเร็จ (ลูกค้าเปลี่ยนวิธีรับ)", del, b.BookingNumber);
                _db.ChangeTracker.Clear();   // ทิ้งสิ่งที่เส้นลบทำค้างไว้ — ไม่บันทึกครึ่ง ๆ
                var noted = await _db.SiteBookings.FirstAsync(x => x.Id == id && x.SiteId == siteId && x.CompanyId == companyId);
                noted.InternalNotes = DepositPolicyResolver.AppendNoteOnce(noted.InternalNotes,
                    $"⚠️ [{BookingFulfillmentChange.RuleCode}] ลบใบรับมัดจำร่างเดิม (ยอดไม่ตรงแล้ว · เลิกผูกกับการจองแล้ว) ไม่สำเร็จ ({ex.Message}) — ลบใบร่างนั้นเองที่หน้าเอกสาร (ใบใหม่ออกอัตโนมัติเมื่อรับเงิน)");
                await _db.SaveChangesAsync();
            }
        }
        resp.Applied = true;
        return resp;
    }

    /// <summary>แจ้งร้านว่าลูกค้าเปลี่ยนวิธีรับ/ที่อยู่ (ร้านต้องเตรียมส่ง/รับใหม่) · ส่งไม่ถึง ⇒ ประทับบนการจอง (ไม่ล้มการเปลี่ยน · ไม่เงียบ)</summary>
    private async Task NotifyShopFulfillmentChangedAsync(Guid companyId, Guid siteId, Guid bookingId, string effect)
    {
        if (_shopNotifier == null) return;
        try
        {
            var f = await LoadBookingNotifyFactsAsync(companyId, siteId, bookingId);
            if (f == null) return;
            var msg = new CmsNotification($"ลูกค้าเปลี่ยนวิธีรับ #{f.BookingNumber} — {f.ServiceName}",
                $"ลูกค้า {BookingDisplayName(f) ?? "-"} เปลี่ยนวิธีรับของการจอง {f.BookingNumber} ที่เว็บ {await SiteNameAsync(companyId, siteId)}\n{effect}\n"
                + (await FulfillmentNotifyLineAsync(companyId, siteId, bookingId) ?? "") + "\nเปิดดูที่เมนู การจอง");
            var err = await _shopNotifier.NotifyShopAsync(companyId, siteId, msg, orderEvent: false);
            if (err != null) await StampBookingNotesAsync(companyId, siteId, bookingId, new[] { CmsNotificationText.FailureNote("ร้าน", msg.Subject, err, DateTime.UtcNow) });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งร้านเรื่องลูกค้าเปลี่ยนวิธีรับของการจอง {BookingId} ล้ม (company {CompanyId})", bookingId, companyId);
            await StampBookingNotesAsync(companyId, siteId, bookingId,
                new[] { $"{CmsNotificationText.FailureTag} แจ้งร้านเรื่องลูกค้าเปลี่ยนวิธีรับไม่สำเร็จ (ข้อผิดพลาดภายใน) — ตรวจวิธีรับบนการจองนี้" });
        }
    }

    // ═══════════════ ข้อมูลบนคำตอบ (หน้าจัดการการจอง) ═══════════════

    /// <summary>เติมปุ่ม "ชำระมัดจำภายหลัง" + หน้าต่างเปลี่ยนวิธีรับ บนคำตอบ — เรียกจาก <c>FillCustomerFacingAsync</c> (หลัง <c>FillPaymentInfoAsync</c>)</summary>
    private async Task FillManageInfoAsync(Guid companyId, Guid siteId, BookingResponse resp)
    {
        var b = await _db.SiteBookings.AsNoTracking().Include(x => x.BookingService)
            .FirstOrDefaultAsync(x => x.Id == resp.Id && x.SiteId == siteId && x.CompanyId == companyId);
        if (b == null) return;
        var now = DateTime.UtcNow;
        if (resp.Payment != null)
        {
            var d = await DepositLaterOfAsync(companyId, siteId, b, b.BookingService, now);
            resp.Payment.PayLaterShow = d.Show && resp.PublicToken != null;
            resp.Payment.CanPayDepositLater = d.Allowed && resp.PublicToken != null;
            resp.Payment.PayLaterAmount = d.Amount;
            resp.Payment.PayLaterNote = d.Allowed ? BookingDepositLater.Offer(d.Amount) : d.Problem;
        }
        var siteRow = await _db.SiteBookingSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        if (!FulfillmentOf(b.BookingService, siteRow).Enabled || resp.PublicToken == null) return;
        var w = BookingFulfillmentChange.Window(siteRow?.FulfillmentChangeCutoffHours, b.Status, CreditBookingLink.StartUtc(b.BookingDate, b.StartTime), now);
        resp.CanChangeFulfillmentOnline = w.Enabled && w.Open;
        resp.FulfillmentChangeNote = w.Note;
    }
}
