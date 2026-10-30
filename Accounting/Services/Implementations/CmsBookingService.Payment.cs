using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 205 แพ็ก W2 (R-08 R-09 R-10 R-11) — <b>เส้นเงินของการจองนัดหมาย</b> (ไฟล์แยกจาก <c>CmsBookingService.cs</c> ตามขอบเขตทะเบียน §3 W2)
/// <list type="bullet">
/// <item>R-08 รับเงินมัดจำ (พนักงาน/gateway) ⇒ <c>PaidAmount</c> · ครบมัดจำ = ยืนยันการจอง + ใบเสร็จมัดจำอนุมัติ<b>ลงวันที่รับเงิน</b> (§78/1) · กันซ้ำด้วยเลขอ้างอิง</item>
/// <item>R-09 ให้บริการเสร็จ ⇒ ใบกำกับ/ใบเสร็จส่วนที่เหลือ<b>หักฐานมัดจำ</b> (<c>DepositBaseDeducted</c> — ตัวเดียวกับเช็คเอาต์ที่พัก/MTO ข้อ 156) · tax point = วันใช้บริการ</item>
/// <item>R-10 ลูกค้ายกเลิกเองผ่านลิงก์โทเคน · R-11 ไม่มา ⇒ ค่าธรรมเนียมตามนโยบายผ่าน <see cref="BookingCancellationFee"/> ริบ ≤ มัดจำ (ข้อ 126) ด้วย <c>RealizeDepositAsync(ForfeitAs)</c> เดิม (ไม่มี posting ใหม่)</item>
/// </list>
/// ตัวตัดสินทั้งหมดอยู่ใน <c>Helpers/</c> (<see cref="CmsBookingOnlinePayment"/> · <see cref="BookingCancellationFee"/>) — ไฟล์นี้ต่อสายเท่านั้น ·
/// ไม่แตะ <c>BookingSlotEngine</c>/<c>PaymentIntentService</c> (ของทีม B/W1)
/// </summary>
public partial class CmsBookingService
{
    private const string PaymentRuleCode = CmsBookingOnlinePayment.RuleCode;

    // ═══════════════ R-08 รับเงินมัดจำ ═══════════════

    public async Task<BookingResponse> RecordDepositPaymentAsync(Guid companyId, Guid siteId, Guid bookingId, RecordBookingPaymentRequest request,
        string actor, Guid? moneyInAccountId = null, bool fromOnlinePayment = false, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var amount = Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero);
        if (CmsBookingOnlinePayment.ValidatePaidAt(request.PaidAt, now) is string dateErr)
            throw new BusinessRuleException(dateErr, PaymentRuleCode);
        var paidAt = request.PaidAt ?? now;
        var reference = request.Reference?.Trim();

        BookingType type;
        decimal depositAmount;
        var duplicate = false;
        Guid? supersededSlipIntent = null;   // ฝ่ายค้านรอบ 218 (S1): สลิปรอตรวจที่ถูกแทนด้วยเงินก้อนอื่น ⇒ ปิดรายการชำระของสลิปหลัง commit
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            // ล็อกต่อการจอง (xact) — webhook+poll ของเงินก้อนเดียว / พนักงานสองแท็บ ⇒ คำขอที่สองเห็นเลขอ้างอิงที่บันทึกแล้ว
            // รอบ 208 (ฝ่ายค้านรอบ 2 ข้อ 1): ถือคีย์เดียวกับเส้นเปลี่ยนสถานะ <b>ก่อน</b>คีย์ใบ (ลำดับเดียวกับเส้นสถานะ ⇒ ไม่ deadlock) (xact ชน session lock ข้าม session · reentrant ใน session เดียวกัน)
            // ⇒ ยกเลิก/ปล่อยที่ระหว่างรับเงินไม่เขียนทับกัน (เดิม: รับเงินเขียน Confirmed ทับ Cancelled ที่ void ใบมัดจำแล้ว)
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                AdvisoryLockKey.For(companyId, AdvisoryLockKey.CmsBookingInvoice, BookingStatusLockPart(bookingId)));
            var key = AdvisoryLockKey.For(companyId, AdvisoryLockKey.CmsBookingInvoice, bookingId.ToString());
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", key);
            var booking = await _db.SiteBookings.Include(b => b.BookingService)
                .FirstOrDefaultAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("Booking not found.");
            type = booking.BookingService.BookingType;
            depositAmount = booking.DepositAmount;

            if (fromOnlinePayment && CmsBookingOnlinePayment.AlreadyRecorded(booking.PaymentReference, reference))
                duplicate = true;
            else
            {
                if (booking.Status is BookingStatus.Cancelled or BookingStatus.NoShow)
                    throw new BusinessRuleException(
                        $"การจอง {booking.BookingNumber} ถูกยกเลิก/บันทึกไม่มาแล้ว — บันทึกรับเงินไม่ได้ · ถ้าเงินเข้าแล้วจริง ให้คืนเงิน หรือออกใบเสร็จแยกที่หน้าเอกสาร (อ้างเลขการจอง)",
                        PaymentRuleCode, 409);
                if (CmsBookingOnlinePayment.ValidateAmount(amount, booking.TotalAmount, booking.PaidAmount) is string amtErr)
                    throw new BusinessRuleException(amtErr, PaymentRuleCode);
                // รอบ 208 (ฝ่ายค้าน W2 ข้อ 10): วันรับเงิน = วันที่ใบเสร็จมัดจำ (§78/1) ⇒ ห้ามก่อนวันที่จอง (กันลงย้อนเข้างวดที่ยื่นแล้ว)
                if (ThaiDate.CalendarDateUtc(paidAt) < ThaiDate.CalendarDateUtc(booking.CreatedAt))
                    throw new BusinessRuleException($"วันที่รับเงินต้องไม่ก่อนวันที่จอง ({ThaiDate.CalendarDateUtc(booking.CreatedAt):dd/MM/yyyy})", PaymentRuleCode);

                booking.PaidAmount = Math.Round(booking.PaidAmount + amount, 2, MidpointRounding.AwayFromZero);
                booking.WaitlistHoldUntil = null;   // รอบ 208: รับเงินแล้ว = เลิกถือรอบ (job ปล่อยรอบไม่แตะใบที่มีเงิน)
                booking.PaymentHoldUntil = null;    // รอบ 218: รับเงินแล้ว = เลิกกำหนดชำระ (job ไม่ยกเลิกใบที่มีเงิน)
                // รอบ 218: เงินก้อนนี้คือสลิปที่รอตรวจ (ยืนยันผ่าน PaymentIntent) หรือรับครบแล้ว ⇒ สลิปไม่ "รอตรวจ" อีก (ไฟล์คงไว้เป็นหลักฐาน)
                var isSlipMoney = BookingSlipRule.IsSlipReference(booking.SlipIntentId, reference);
                if (booking.SlipUploadedAt != null
                    && BookingSlipRule.SettlesPendingSlip(booking.SlipIntentId, reference, booking.DepositAmount, booking.PaidAmount))
                {
                    // ฝ่ายค้านรอบ 218 (S1): ครบด้วยเงินก้อนอื่น ⇒ รายการชำระของสลิปต้องปิด (ไม่งั้น "ยืนยันด้วยมือ" ภายหลัง = รับซ้ำ)
                    if (!isSlipMoney) supersededSlipIntent = booking.SlipIntentId;
                    booking.SlipUploadedAt = null;
                }
                // รอบ 208 (ฝ่ายค้าน W2 ข้อ 5): แถวรับเงินรายก้อน — หลักฐานของ gateway re-dispatch อ่านเวลาที่บันทึก (กันรับซ้ำเมื่อพนักงานกรอกวันย้อนหลัง)
                _db.SiteBookingPayments.Add(new SiteBookingPayment
                {
                    CompanyId = companyId, BookingId = booking.Id, Amount = amount, Currency = booking.Currency,
                    // ฝ่ายค้านรอบ 218: เงินของสลิป = โอนเงิน (ไม่ใช่ "ออนไลน์")
                    PaymentMethod = isSlipMoney ? PaymentMethod.BankTransfer
                        : fromOnlinePayment ? PaymentMethod.Other : (request.BankAccountId != null ? PaymentMethod.BankTransfer : PaymentMethod.Cash),
                    Reference = reference, Status = SitePaymentStatus.Completed, PaidAt = paidAt, CreatedBy = actor,
                });
                booking.PaymentReference = CmsBookingOnlinePayment.AppendReference(booking.PaymentReference, reference);
                booking.PaidAt = paidAt;
                // ครบมัดจำ = ยืนยันการจอง (PrePayment: เงินคือการยืนยัน) · คิวสำรอง/ยืนยันแล้ว ไม่แตะสถานะ
                if (type == BookingType.PrePayment && booking.Status == BookingStatus.Pending
                    && CmsBookingOnlinePayment.DepositSettled(booking.DepositAmount, booking.PaidAmount))
                {
                    booking.Status = BookingStatus.Confirmed;
                    booking.ConfirmedAt = now;
                }
                var note = CmsBookingOnlinePayment.PaidNote(amount, reference, paidAt, fromOnlinePayment && !isSlipMoney, booking.PaidAmount, booking.DepositAmount)
                    + (string.IsNullOrWhiteSpace(request.Note) ? "" : $" · {request.Note.Trim()}");
                booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes, note);
                booking.UpdatedBy = actor;
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        if (supersededSlipIntent is Guid sup)
            await FailSlipIntentAsync(companyId, sup, BookingSlipRule.SupersededRaw, "การจองรับเงินครบด้วยรายการอื่นแล้ว — ปิดรายการของสลิป", actor);

        if (duplicate)
        {
            _logger.LogInformation("การจอง {Booking} บันทึกเงินอ้างอิง {Ref} แล้ว — ข้าม (webhook/poll ซ้ำ)", bookingId, reference);
            return await GetBookingAsync(companyId, siteId, bookingId) ?? throw new InvalidOperationException("Failed.");
        }

        // ขั้น ERP หลัง commit (เส้นเอกสารเปิดธุรกรรมของตัวเอง — แบบเดียวกับ CreateBookingAsync) · ล้ม = ล้มดังบนการจอง + คำตอบ ไม่ย้อนเงินที่บันทึก
        var notices = new List<string>();
        var bookingNotes = new List<string>();
        if (type == BookingType.PrePayment && _docService != null
            && CmsBookingErpPolicy.NeedsErpDocument(type, depositAmount)
            && CreditBookingLink.ShouldSyncErp(await IsPaidWithCreditAsync(companyId, bookingId)))
        {
            await IssueDepositReceiptAfterPaymentAsync(companyId, siteId, bookingId, amount, paidAt, moneyInAccountId, request.BankAccountId, actor, notices, bookingNotes);
        }
        else if (type != BookingType.PrePayment)
        {
            // Guaranteed/Appointment: ยอดรับเงินบันทึกบนการจอง · ใบกำกับเดินเส้นเดิม (ปุ่ม "รับเงินแล้ว — ออกใบกำกับ" / tax point เมื่อให้บริการ)
            notices.Add("บันทึกยอดรับเงินบนการจองแล้ว — บริการชนิดนี้ออกใบกำกับผ่านปุ่ม “รับเงินแล้ว — ออกใบกำกับภาษี” หรือเมื่อให้บริการเสร็จ (ไม่ใช่ใบเสร็จมัดจำ)");
        }

        if (bookingNotes.Count > 0)
        {
            var noted = await _db.SiteBookings.FirstAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId, ct);
            foreach (var n in bookingNotes) noted.InternalNotes = DepositPolicyResolver.AppendNoteOnce(noted.InternalNotes, n);
            noted.UpdatedBy = actor;
            await _db.SaveChangesAsync(ct);
        }
        var result = await GetBookingAsync(companyId, siteId, bookingId) ?? throw new InvalidOperationException("Failed.");
        result.ErpNotices = notices;
        return result;
    }

    /// <summary>ใบเสร็จมัดจำหลังเงินเข้า: ร่างที่รอเงิน ⇒ วันที่ใบ = วันรับเงิน + อนุมัติ · ไม่มีใบ (ขั้น ERP ตอนจองล้ม) ⇒ สร้างด้วยเส้นเดิมลงวันที่รับเงิน ·
    /// ออกแล้ว ⇒ ไม่ออกซ้ำ บอกทางไปต่อ · รับบางส่วน ⇒ รอครบ</summary>
    private async Task IssueDepositReceiptAfterPaymentAsync(Guid companyId, Guid siteId, Guid bookingId, decimal amount, DateTime paidAt,
        Guid? moneyInAccountId, Guid? bankAccountId, string actor, List<string> notices, List<string> bookingNotes)
    {
        var b = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId)
            .Select(x => new { x.ErpDocumentId, x.DepositAmount, x.PaidAmount, x.BookingNumber })
            .FirstOrDefaultAsync();
        if (b == null) return;
        if (!CmsBookingOnlinePayment.DepositSettled(b.DepositAmount, b.PaidAmount))
        {
            var partial = $"รับเงินบางส่วน {b.PaidAmount:N2}/{b.DepositAmount:N2} — ใบเสร็จมัดจำยังเป็นร่าง จะอนุมัติเมื่อรับครบมัดจำ";
            notices.Add(partial); bookingNotes.Add(partial);
            return;
        }

        if (b.ErpDocumentId is null)
        {
            try
            {
                var newId = await SyncBookingToErpAsync(companyId, siteId, bookingId, null, paidAt, moneyInAccountId, bankAccountId);
                var made = newId is Guid nid
                    ? await _db.Documents.AsNoTracking().Where(d => d.Id == nid && d.CompanyId == companyId)
                        .Select(d => new { d.DocumentNumber, d.DocumentType, d.Status, d.DocumentDate }).FirstOrDefaultAsync()
                    : null;
                if (made != null && made.Status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval))
                {
                    var ok = BookingInvoiceTimingPolicy.IssuedNotice(BookingInvoiceEvent.PaymentReceived, made.DocumentNumber, made.DocumentType, ThaiDate.CalendarDateUtc(made.DocumentDate));
                    notices.Add(ok); bookingNotes.Add(ok);
                }
                else
                {
                    var pending = CmsBookingCancelPolicy.FailureNote("การออกใบเสร็จมัดจำของการจอง", made?.DocumentNumber,
                        "ใบยังไม่ได้อนุมัติ — เปิดเอกสารแล้วกด “อนุมัติ” (ดูหมายเหตุบนใบ)");
                    notices.Add(pending); bookingNotes.Add(pending);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                _logger.LogWarning(ex, "สร้างใบเสร็จมัดจำหลังเงินเข้าของการจอง {Booking} ไม่สำเร็จ", b.BookingNumber);
                var fail = CmsBookingCancelPolicy.FailureNote("การออกใบเสร็จมัดจำของการจอง", null, ReasonOf(ex));
                notices.Add(fail); bookingNotes.Add(fail);
            }
            return;
        }

        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == b.ErpDocumentId && d.CompanyId == companyId);
        if (doc == null)
        {
            var missing = CmsBookingCancelPolicy.FailureNote("การอนุมัติใบเสร็จมัดจำ", null, "ไม่พบเอกสารที่ผูกกับการจอง");
            notices.Add(missing); bookingNotes.Add(missing);
            return;
        }
        if (!doc.IsDeposit)
        {
            var notDeposit = $"เอกสารที่ผูกกับการจอง ({doc.DocumentNumber}) ไม่ใช่ใบมัดจำ — ยอดรับเงินบันทึกบนการจองอย่างเดียว · ตรวจเอกสารใบนั้นเอง";
            notices.Add(notDeposit); bookingNotes.Add(notDeposit);
            return;
        }
        if (doc.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
        {
            // ร่างที่รอเงิน (AwaitingPaymentNote): วันที่ใบ = วันรับเงินจริง (§78/1 — ใบเสร็จคือหลักฐานรับเงิน) · ผังเงินเข้าที่ผู้จ่าย/พนักงานบอก
            doc.DocumentDate = paidAt;
            if (moneyInAccountId != null) doc.PaymentAccountId = moneyInAccountId;
            if (bankAccountId != null) doc.BankAccountId = bankAccountId;
            doc.InternalNotes = DepositPolicyResolver.AppendNoteOnce(doc.InternalNotes,
                $"[{PaymentRuleCode}] รับเงินมัดจำครบ {b.PaidAmount:N2} เมื่อ {ThaiDate.CalendarDateUtc(paidAt):dd/MM/yyyy} — อนุมัติอัตโนมัติลงวันที่รับเงิน (§78/1)");
            await _db.SaveChangesAsync();
            await ApproveBookingTaxDocumentAsync(companyId, doc.Id, doc.DocumentNumber, BookingInvoiceEvent.PaymentReceived, actor, notices, bookingNotes);
            return;
        }
        if (doc.Status is DocumentStatus.Voided or DocumentStatus.Rejected)
        {
            var dead = $"ใบมัดจำ {doc.DocumentNumber} ที่ผูกกับการจองถูกยกเลิกแล้ว — ยอดรับเงินบันทึกบนการจองอย่างเดียว · ออกใบเสร็จมัดจำใหม่ที่หน้าเอกสาร (อ้างเลขการจอง {b.BookingNumber})";
            notices.Add(dead); bookingNotes.Add(dead);
            return;
        }
        var issued = CmsBookingOnlinePayment.AlreadyIssuedNote(doc.DocumentNumber, amount);
        notices.Add(issued); bookingNotes.Add(issued);
    }

    // ═══════════════ มุมมองลูกค้า (โทเคน) ═══════════════

    public async Task<BookingResponse?> GetBookingByTokenAsync(Guid companyId, Guid siteId, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var id = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.PublicToken == token)
            .Select(b => (Guid?)b.Id).FirstOrDefaultAsync();
        if (id is not Guid bookingId) return null;
        var resp = await GetBookingAsync(companyId, siteId, bookingId);
        return resp == null ? null : ForCustomer(resp);
    }

    /// <summary>ตัดข้อมูลภายในออกจากคำตอบที่ลูกค้าเห็น (หมายเหตุภายใน · ข้อความ ERP · ผู้ให้บริการภายใน) — ตัวเดียว</summary>
    private static BookingResponse ForCustomer(BookingResponse r)
    {
        r.InternalNotes = null;
        r.ErpNotices = new List<string>();
        r.ErpDocumentId = null;
        // รอบ 207: ข้อมูลหน้าทำงานของพนักงาน (เอกสาร · ประวัติรับเงิน · แผนตอนเสร็จ · สัดส่วนงาน) ไม่ใช่ของลูกค้า
        r.Documents = new List<BookingDocumentLinkDto>();
        r.PaymentLines = new List<BookingPaymentLineDto>();
        r.CompletionPlan = null;
        foreach (var a in r.Assignments) a.SharePercent = 0m;
        r.PaymentReference = null;
        // รอบ 218: รายการชำระของสลิป/ลิงก์ไฟล์สลิปของพนักงาน ไม่ใช่ของลูกค้า (ลูกค้าเปิดสลิปตัวเองผ่านเส้นโทเคน)
        if (r.Payment != null) { r.Payment.SlipIntentId = null; r.Payment.SlipFileUrl = null; }
        return r;
    }

    /// <summary>มัดจำค้าง/จ่ายออนไลน์ได้ไหม/นโยบายยกเลิก — ค่าจากเซิร์ฟเวอร์ให้ทุกคำตอบ (หน้าเว็บ+หลังบ้านเห็นชุดเดียว)</summary>
    private async Task FillCustomerFacingAsync(Guid companyId, Guid siteId, BookingResponse resp)
    {
        var now = DateTime.UtcNow;
        var onlineEnabled = await _db.SiteBookingSettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId)
            .Select(s => (bool?)s.OnlineDepositEnabled).FirstOrDefaultAsync() ?? false;
        var due = CmsBookingOnlinePayment.DepositDue(resp.BookingType, resp.Status, resp.DepositAmount, resp.PaidAmount);
        // รอบ 218: ยอด/สถานะ/ช่องทาง/สลิป (BookingPaymentPolicy) — สลิปรอตรวจ ⇒ ปิดช่องจ่ายออนไลน์ชั่วคราว (ตัวเดียวกับ PublicPaymentResolver)
        var slipPending = await FillPaymentInfoAsync(companyId, siteId, resp, due);
        resp.OnlinePaymentNote = CmsBookingOnlinePayment.BlockedNote(onlineEnabled, resp.BookingType, resp.Status, due, slipPending);
        resp.OnlineDepositDue = onlineEnabled && resp.OnlinePaymentNote == null ? due : 0m;

        var startUtc = StartUtcOf(resp.BookingDate, resp.StartTime);
        resp.CanCancelOnline = resp.PublicToken != null && startUtc > now
            && resp.Status is BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.Waitlisted;
        if (!resp.CanCancelOnline) return;
        var policy = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.Id == resp.BookingServiceId && s.CompanyId == companyId)
            .Select(s => new { s.FreeCancellationHours, s.CancellationFeePercent }).FirstOrDefaultAsync();
        if (policy == null) return;
        var plan = BookingCancellationFee.Plan(BookingCancelKind.CustomerCancel, now, startUtc,
            policy.FreeCancellationHours, policy.CancellationFeePercent, resp.TotalAmount, resp.PaidAmount);
        resp.CancelPolicyNote = BookingCancellationFee.Describe(plan, policy.FreeCancellationHours);
        // รอบ 206: เลื่อนนัดเองได้เฉพาะในหน้าต่างยกเลิกฟรี · คิวสำรองไม่มีรอบให้เลื่อน
        resp.CanRescheduleOnline = resp.Status != BookingStatus.Waitlisted && BookingCancellationFee.CanCustomerReschedule(plan);
    }

    private static DateTime StartUtcOf(DateTime bookingDate, string startTime)
        => CreditBookingLink.StartUtc(bookingDate,
            TimeOnly.TryParseExact(startTime, "HH:mm", null, System.Globalization.DateTimeStyles.None, out var t) ? t : TimeOnly.MinValue);

    // ═══════════════ R-10 ลูกค้ายกเลิกเอง ═══════════════

    public async Task<BookingResponse> CancelByCustomerAsync(Guid companyId, Guid siteId, string token, CustomerCancelBookingRequest request)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new KeyNotFoundException("Booking not found.");
        var id = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.SiteId == siteId && x.PublicToken == token)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync() ?? throw new KeyNotFoundException("Booking not found.");
        // รอบ 208 (ฝ่ายค้าน W2 ข้อ 1 blocking): ยกเลิก + ริบมัดจำเป็นก้อนเดียวใต้ล็อกเดียวกับเส้นสถานะ — กดซ้ำ/สองแท็บพร้อมกัน ⇒
        // คำขอที่สองเห็นสถานะยกเลิกแล้วและถูกปฏิเสธ (เดิมทั้งสองคำขอริบ ⇒ ริบ 600 แทน 300)
        return await WithBookingSessionLockAsync(companyId, BookingStatusLockPart(id),
            () => CancelByCustomerLockedAsync(companyId, siteId, token, request));
    }

    private async Task<BookingResponse> CancelByCustomerLockedAsync(Guid companyId, Guid siteId, string token, CustomerCancelBookingRequest request)
    {
        var now = DateTime.UtcNow;
        var b = await _db.SiteBookings.AsNoTracking().Include(x => x.BookingService)
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && x.PublicToken == token)
            ?? throw new KeyNotFoundException("Booking not found.");
        if (b.Status == BookingStatus.Cancelled)
            throw new BusinessRuleException("การจองนี้ถูกยกเลิกแล้ว", BookingCancellationFee.RuleCode, 409);
        if (b.Status is BookingStatus.Completed or BookingStatus.NoShow)
            throw new BusinessRuleException("การจองนี้ให้บริการ/บันทึกผลแล้ว — ยกเลิกไม่ได้ กรุณาติดต่อร้าน", BookingCancellationFee.RuleCode, 409);
        var startUtc = CreditBookingLink.StartUtc(b.BookingDate, b.StartTime);
        if (startUtc <= now)
            throw new BusinessRuleException("เลยเวลานัดแล้ว — ยกเลิกออนไลน์ไม่ได้ กรุณาติดต่อร้าน", BookingCancellationFee.RuleCode, 409);

        var plan = BookingCancellationFee.Plan(BookingCancelKind.CustomerCancel, now, startUtc,
            b.BookingService.FreeCancellationHours, b.BookingService.CancellationFeePercent, b.TotalAmount, b.PaidAmount);
        var actor = CreditBookingLink.CustomerActorPrefix + token[..Math.Min(8, token.Length)];
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "ลูกค้ายกเลิกเองผ่านลิงก์" : $"ลูกค้ายกเลิกเองผ่านลิงก์: {request.Reason.Trim()}";

        // เส้นสถานะเดิม: เอกสาร ERP (ใบมัดจำที่ออกแล้วคงเป็นหนี้สิน / ใบร่าง void) · เลื่อนคิวสำรอง · แจ้งคนได้คิว
        var afterCancel = await UpdateBookingStatusAsync(companyId, siteId, b.Id,
            new UpdateBookingStatusRequest { Status = BookingStatus.Cancelled, CancellationReason = reason }, actor);
        var notices = new List<string>(afterCancel.ErpNotices);
        var bookingNotes = new List<string>();
        await ForfeitDepositByPolicyAsync(companyId, siteId, b.Id, plan, actor, notices, bookingNotes);
        await AppendBookingNotesAsync(companyId, siteId, b.Id, bookingNotes, actor);

        var result = await GetBookingByTokenAsync(companyId, siteId, token) ?? throw new InvalidOperationException("Failed.");
        result.CancelPolicyNote = BookingCancellationFee.Describe(plan, b.BookingService.FreeCancellationHours);
        return result;
    }

    // ═══════════════ R-11 ไม่มา ⇒ ริบตามนโยบาย ═══════════════

    /// <summary>เรียกจาก <c>UpdateBookingStatusAsync</c> เมื่อสถานะเป็น NoShow (ครั้งแรก) — ไม่มีหน้าต่างยกเลิกฟรี · ไม่ตั้ง % = ไม่ริบเอง</summary>
    private async Task ForfeitOnNoShowAsync(Guid companyId, Guid siteId, Guid bookingId, string actor, List<string> notices, List<string> bookingNotes)
    {
        var b = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId)
            .Select(x => new { x.BookingDate, x.StartTime, x.TotalAmount, x.PaidAmount, x.BookingService.FreeCancellationHours, x.BookingService.CancellationFeePercent, x.BookingService.BookingType })
            .FirstOrDefaultAsync();
        if (b == null || b.BookingType != BookingType.PrePayment) return;
        var plan = BookingCancellationFee.Plan(BookingCancelKind.NoShow, DateTime.UtcNow, CreditBookingLink.StartUtc(b.BookingDate, b.StartTime),
            b.FreeCancellationHours, b.CancellationFeePercent, b.TotalAmount, b.PaidAmount);
        await ForfeitDepositByPolicyAsync(companyId, siteId, bookingId, plan, actor, notices, bookingNotes);
    }

    /// <summary>ริบมัดจำตามแผน (ตัวเดียวทั้งยกเลิกเอง/ไม่มา): ใบมัดจำที่ออกแล้ว ⇒ <c>RealizeDepositAsync(ForfeitAs: PriceOrFee)</c> เท่าฐานของส่วนที่ริบ (ไม่เกินฐานคงค้าง) ·
    /// ส่วนที่เหลือรอคืนที่ศูนย์มัดจำ (ระบบไม่คืนเอง — R1) · ไม่มีใบ/ไม่ตั้งนโยบาย ⇒ จดอย่างเดียว</summary>
    private async Task ForfeitDepositByPolicyAsync(Guid companyId, Guid siteId, Guid bookingId, BookingCancellationFeePlan plan, string actor,
        List<string> notices, List<string> bookingNotes)
    {
        var now = DateTime.UtcNow;
        var b = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId)
            .Select(x => new { x.ErpDocumentId, x.PaidAmount, x.BookingNumber }).FirstOrDefaultAsync();
        if (b == null) return;
        Document? doc = b.ErpDocumentId is Guid did
            ? await _db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == did && d.CompanyId == companyId)
            : null;
        bookingNotes.Add(BookingCancellationFee.InternalNote(plan, doc?.DocumentNumber, now));
        var summary = BookingCancellationFee.Describe(plan, null);
        if (plan.PolicyUndefined)
        {
            if (b.PaidAmount > 0.005m) notices.Add($"มัดจำ {b.PaidAmount:N2} ของการจอง {b.BookingNumber}: {summary} — ตัดสินคืน/ริบที่ “เงินมัดจำ”");
            return;
        }
        if (!plan.HasForfeit)
        {
            notices.Add(summary);
            return;
        }
        if (_docService == null || doc == null || !doc.IsDeposit || !DocumentStatusRules.IsEffective(doc.Status))
        {
            // รอบ 208 (ฝ่ายค้าน W2 ข้อ 6/13): ข้อความเดิม "ไม่มีเงิน · บันทึกเป็นยอดค้าง" ไม่จริง — อาจรับเงินแล้วแต่ยังไม่มีใบ และไม่มีแถวยอดค้างใด ๆ
            notices.Add(b.PaidAmount > 0.005m
                ? $"{summary} — รับเงินไว้แล้ว {b.PaidAmount:N2} บาท แต่ใบมัดจำยังไม่ออก/ถูกยกเลิก ระบบจึงไม่ริบเอง · คืนเงินลูกค้า หรือออกใบเสร็จมัดจำที่หน้าเอกสารก่อนแล้วจึงริบ"
                : $"{summary} — ยังไม่ได้รับเงินมัดจำ ไม่มีอะไรให้ริบ");
            return;
        }
        var vatPortion = doc.TotalAmount > 0m ? doc.VatAmount / doc.TotalAmount : 0m;
        var refundedBase = Math.Round(doc.DepositRefundedAmount * (1 - vatPortion), 2, MidpointRounding.AwayFromZero);
        var remainingBase = doc.SubTotal - doc.DepositRealizedAmount - refundedBase;
        var forfeitBase = BookingCancellationFee.ForfeitBase(plan.ForfeitFromDeposit, doc.SubTotal, doc.TotalAmount, remainingBase);
        if (forfeitBase <= 0.005m)
        {
            notices.Add($"{summary} — มัดจำ {doc.DocumentNumber} ไม่มีฐานคงค้างให้ริบ (รับรู้/คืนไปแล้ว)");
            return;
        }
        try
        {
            await _docService.RealizeDepositAsync(companyId, doc.Id,
                new RealizeDepositRequest(forfeitBase, now, null, null, ForfeitAs: DepositForfeitAs.PriceOrFee), actor);
            // รอบ 208 (ฝ่ายค้าน W2 ข้อ 7): ยอดที่ริบจริง = ฐานที่ realize แปลงกลับเป็นยอดรวม VAT — ใบมัดจำอาจเล็กกว่ายอดที่รับบนการจอง (ข้อความเดิมบอกเกินจริง)
            var forfeitGross = BookingCancellationFee.GrossOfBase(forfeitBase, doc.SubTotal, doc.TotalAmount, plan.ForfeitFromDeposit);
            var done = $"ริบมัดจำ {forfeitGross:N2} บาท (ฐาน {forfeitBase:N2}) จากใบ {doc.DocumentNumber} เป็นค่าธรรมเนียม{(plan.Kind == BookingCancelKind.NoShow ? "ไม่มาตามนัด" : "ยกเลิก")}"
                + (forfeitGross + 0.005m < plan.ForfeitFromDeposit ? $" (ตามแผน {plan.ForfeitFromDeposit:N2} แต่ใบมัดจำเหลือให้ริบเท่านี้ — ส่วนต่างไม่ได้ริบ)" : "")
                + (plan.HasRefund ? $" · ส่วนที่เหลือ {plan.RefundDue:N2} รอคืนที่ “เงินมัดจำ” (ใบลดหนี้ §86/10)" : "");
            notices.Add(done); bookingNotes.Add(done);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning(ex, "ริบมัดจำ {Doc} ของการจอง {Booking} ไม่สำเร็จ", doc.DocumentNumber, b.BookingNumber);
            var fail = CmsBookingCancelPolicy.FailureNote("การริบมัดจำตามนโยบาย", doc.DocumentNumber, ReasonOf(ex));
            notices.Add(fail); bookingNotes.Add(fail);
        }
    }

    private async Task AppendBookingNotesAsync(Guid companyId, Guid siteId, Guid bookingId, List<string> bookingNotes, string actor)
    {
        if (bookingNotes.Count == 0) return;
        var noted = await _db.SiteBookings.FirstAsync(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId);
        foreach (var n in bookingNotes) noted.InternalNotes = DepositPolicyResolver.AppendNoteOnce(noted.InternalNotes, n);
        noted.UpdatedBy = actor;
        await _db.SaveChangesAsync();
    }

    // ═══════════════ R-09 ให้บริการเสร็จ ⇒ ใบส่วนที่เหลือหักมัดจำ ═══════════════

    /// <summary>ตัดสินตอน Completed: มัดจำที่ออกใบแล้ว (VAT ตอนรับ · ไม่ใช่เงินประกัน) และราคาเต็ม &gt; มัดจำ ⇒ ออกใบกำกับ/ใบเสร็จส่วนที่เหลือ
    /// หักฐานมัดจำ (<c>DepositBaseDeducted</c> · การอนุมัติใบสุดท้ายรับรู้ฐานมัดจำให้เอง — <c>RealizeTaxedDepositDeductionsAsync</c>) ·
    /// กรณีอื่น (มัดจำ = ราคาเต็ม · มัดจำเต็มยอดรอ VAT · เงินประกัน) ⇒ เส้นเดิม <c>RealizeDepositOnCompleteAsync</c> / ข้อความเดิม</summary>
    private async Task SettleDepositOnCompleteAsync(Guid companyId, Guid siteId, Guid bookingId, Guid erpDocId, string userId,
        List<string> notices, List<string> bookingNotes)
    {
        var erpDoc = await _db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == erpDocId && d.CompanyId == companyId);
        if (erpDoc == null || !erpDoc.IsDeposit) return;
        var booking = await _db.SiteBookings.AsNoTracking().Include(b => b.BookingService).ThenInclude(s => s.Product)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId);
        if (booking == null) return;

        var (vatRegistered, defaultRate) = await CompanyVatStatus.ProfileAsync(_db, companyId);
        var action = CmsBookingCancelPolicy.DecideOnComplete(erpDoc.IsDeposit, erpDoc.Status, erpDoc.VatAmount, erpDoc.DepositNature, vatRegistered);
        var remainder = Math.Round(booking.TotalAmount - erpDoc.TotalAmount, 2, MidpointRounding.AwayFromZero);
        if (action != CmsBookingCompleteAction.AutoRealize || erpDoc.DepositOutputVatDeferred || remainder <= 0.005m || _docService == null)
        {
            await RealizeDepositOnCompleteAsync(companyId, erpDocId, userId, notices, bookingNotes);
            return;
        }

        var svc = booking.BookingService;
        // รอบ 207/208: สินค้าที่ผูก ⇒ อัตรา VAT/บัญชีรายได้/รหัส ผ่านตัวตัดสินเดียวกับใบตอนจอง (BookingProductLink)
        var product = svc.Product is { IsDeleted: false } pr && pr.CompanyId == companyId ? pr : null;
        var vatRate = BookingProductLink.VatRate(vatRegistered, OutputVatRate.ForCompany(vatRegistered, defaultRate), product?.VatRate);
        var docType = vatRegistered ? DocumentType.TaxInvoice : DocumentType.Invoice;
        // รอบ 208 (ฝ่ายค้าน W2 ข้อ 9): ราคาที่ตรึงบนการจอง (TotalAmount) ไม่ใช่ราคาบริการวันนี้ — หารลงตัวต่อช่วงได้ใช้ต่อช่วง ไม่งั้น 1 × ยอดรวม
        var (qty, unitPrice) = BookingCancellationFee.FrozenUnitPrice(booking.TotalAmount, booking.Units);
        var lines = new List<DocumentLineRequest>
        {
            new(Description: $"{svc.Name} ({booking.BookingDate:yyyy-MM-dd} {booking.StartTime:HH:mm}-{booking.EndTime:HH:mm})",
                Quantity: qty, Unit: qty > 1 ? "ช่วง" : "ครั้ง",
                UnitPrice: unitPrice, DiscountPercent: 0m, VatRate: vatRate, WithholdingTaxRate: 0m, AccountId: product?.SalesAccountId, ProductCode: product?.Code),
        };
        var full = DocumentService.PreviewTotals(lines, true, 0m);
        var vatPortion = erpDoc.TotalAmount > 0m ? erpDoc.VatAmount / erpDoc.TotalAmount : 0m;
        var refundedBase = Math.Round(erpDoc.DepositRefundedAmount * (1 - vatPortion), 2, MidpointRounding.AwayFromZero);
        var remainingBase = Math.Max(0m, erpDoc.SubTotal - erpDoc.DepositRealizedAmount - refundedBase);
        var deductBase = Math.Min(remainingBase, full.Net);
        if (deductBase <= 0.005m)
        {
            await RealizeDepositOnCompleteAsync(companyId, erpDocId, userId, notices, bookingNotes);
            return;
        }
        var serviceUsed = CreditBookingLink.StartUtc(booking.BookingDate, booking.StartTime);
        var create = new CreateDocumentRequest(
            DocumentType: docType, DocumentDate: DateTime.UtcNow, DueDate: null,
            ContactId: erpDoc.ContactId, Reference: booking.BookingNumber,
            Notes: $"ให้บริการ {svc.Name} เสร็จ {booking.BookingDate:dd/MM/yyyy} · จอง {booking.BookingNumber} · หักมัดจำตามใบ {erpDoc.DocumentNumber}",
            Lines: lines,
            DepositAppliedRef: erpDoc.DocumentNumber,
            DepositBaseDeducted: deductBase,
            PricesIncludeVat: true,
            Currency: svc.Currency,
            BookingNumber: booking.BookingNumber,
            ServiceUsedDate: serviceUsed,
            BranchId: erpDoc.BranchId);   // รอบ 209 P6 (ฐ): ใบส่วนที่เหลือสืบทอดสาขาจากใบมัดจำ (เอกสารลูกสืบทอด · หักมัดจำในสาขาเดียวกัน)
        DocumentResponse created;
        try
        {
            created = await _docService.CreateDocumentAsync(companyId, create, userId, originModule: DocumentQuotaPolicy.CmsBookingOrigin);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning(ex, "สร้างใบส่วนที่เหลือของการจอง {Booking} ไม่สำเร็จ", booking.BookingNumber);
            var fail = CmsBookingCancelPolicy.FailureNote("การออกใบส่วนที่เหลือ (หักมัดจำ)", erpDoc.DocumentNumber,
                ReasonOf(ex) + " — ออกใบขายเองที่หน้าเอกสารแล้วหักมัดจำใบนี้");
            notices.Add(fail); bookingNotes.Add(fail);
            return;
        }
        await ApproveBookingTaxDocumentAsync(companyId, created.Id, created.DocumentNumber, BookingInvoiceEvent.ServiceCompleted, userId, notices, bookingNotes);
        var after = await _db.Documents.AsNoTracking().Where(d => d.Id == created.Id && d.CompanyId == companyId)
            .Select(d => new { d.Status, d.TotalAmount, d.BalanceDue }).FirstOrDefaultAsync();
        if (after != null && after.Status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval))
        {
            var done = $"ใบ {created.DocumentNumber}: ค่าบริการเต็ม {full.Total:N2} หักมัดจำ (ฐาน {deductBase:N2}) ⇒ ค้างชำระ {after.BalanceDue:N2} · tax point วันใช้บริการ {ThaiDate.CalendarDateUtc(serviceUsed):dd/MM/yyyy}";
            notices.Add(done); bookingNotes.Add(done);
        }
    }
}
