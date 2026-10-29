using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs.CourseCredit;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// สายต่อเครื่องจอง CMS ↔ บัตรคอร์ส (<c>ICreditBookingLedger</c>) — รอบ 205 ทีม W6 · R-39 / COURSE_CREDIT_DESIGN B4 ·
/// ตัวตัดสินอยู่ที่ <see cref="CreditBookingLink"/> ตัวเดียว · ไฟล์นี้<b>เรียกสมุดเครดิตเท่านั้น</b> ไม่แตะ slot logic (<c>BookingSlotEngine</c>) และไม่คำนวณรายได้เอง
/// </summary>
public partial class CmsBookingService
{
    /// <summary>ก่อน Add การจองที่เลือกใช้บัตร: ยอดเงิน = 0 (ไม่มีเงินต้องเก็บ — รายได้มาจาก JE รับรู้ของบัตร) · ต้องมีลูกค้าที่ผูกผู้ติดต่อ ERP</summary>
    private async Task<Guid?> PrepareCreditBookingAsync(Guid companyId, SiteBooking booking, CreateBookingRequest request, string? userId)
    {
        if (!request.PayWithCredit) return null;
        // รอบ 209 (ทีม C ข้อ C1 · blocking): ทางสร้างการจองเป็น AllowAnonymous และ CustomerId มาจาก body ⇒ ใครรู้ id ก็ตัดเครดิตบัตรคนอื่นได้ ·
        // จนกว่าจะมีหน้าสมาชิก (ยืนยันตัวตนลูกค้า) ใช้บัตรได้เฉพาะพนักงานที่ล็อกอิน (controller ตรวจสิทธิ์ CmsOrderManage อีกชั้น)
        if (CreditBookingLink.PayWithCreditProblem(userId) is string who)
            throw new BusinessRuleException(who, CreditBookingLink.RuleCode, 403);
        if (_creditLedger == null)
            throw new BusinessRuleException("ระบบนี้ยังไม่เปิดใช้โมดูลคอร์ส/เครดิต — จองแบบชำระปกติ", CreditBookingLink.RuleCode);
        if (request.CustomerId is not Guid customerId)
            throw new BusinessRuleException("จองด้วยบัตรคอร์สต้องระบุลูกค้า (บัญชีลูกค้าของเว็บไซต์ที่ผูกผู้ติดต่อ) — แขกไม่ล็อกอินใช้บัตรไม่ได้", CreditBookingLink.RuleCode);
        var contactId = await _db.SiteCustomers.AsNoTracking()
            .Where(c => c.Id == customerId && c.CompanyId == companyId)
            .Select(c => c.ContactId).FirstOrDefaultAsync();
        if (contactId is not Guid cid)
            throw new BusinessRuleException("ลูกค้ารายนี้ยังไม่ผูกผู้ติดต่อในระบบบัญชี — ผูกผู้ติดต่อก่อนจึงใช้บัตรคอร์สได้", CreditBookingLink.RuleCode);
        booking.TotalAmount = 0m;
        booking.DepositAmount = 0m;
        return cid;
    }

    /// <summary>กันเครดิตให้การจอง — เรียก<b>ในธุรกรรมเดียวกับ Decide</b> (ล้ม ⇒ ธุรกรรมถอย ⇒ ไม่มีการจอง · ไม่มีเครดิตค้าง)</summary>
    private async Task HoldCreditForBookingAsync(Guid companyId, SiteBookingService svc, SiteBooking booking, Guid contactId,
        Guid? passId, decimal servicePrice, string? actor)
    {
        var hold = await _creditLedger!.HoldAsync(companyId, new CreditHoldRequest(
            ContactId: contactId, PassId: passId, BookingServiceId: svc.Id,
            ExternalRef: CreditBookingLink.Ref(booking.Id),
            StartAt: CreditBookingLink.StartUtc(booking.BookingDate, booking.StartTime),
            DurationMinutes: booking.DurationMinutes,
            Format: CreditClassFormat.Any, Seats: booking.Units), actor ?? "storefront-customer");
        booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes,
            CreditBookingLink.PaidNote(hold.PassNumber, hold.Credits, servicePrice));
        await _db.SaveChangesAsync();
    }

    /// <summary>การจองนี้จ่ายด้วยบัตรไหม — ความจริงอยู่ที่สมุดเครดิต (ExternalRef) ไม่ใช่ธงบนการจอง (ไม่ต้องแก้ schema SiteBooking)</summary>
    private async Task<bool> IsPaidWithCreditAsync(Guid companyId, Guid bookingId)
    {
        if (_creditLedger == null) return false;
        var r = CreditBookingLink.Ref(bookingId);
        return await _db.CreditClassBookings.AsNoTracking().AnyAsync(b => b.CompanyId == companyId && b.ExternalRef == r);
    }

    /// <summary>เลขบัตรของการจองที่จ่ายด้วยบัตร (แสดงบนหน้าการจอง) · null = การจองปกติ</summary>
    private async Task<string?> CreditPassNumberAsync(Guid companyId, Guid bookingId)
    {
        if (_creditLedger == null) return null;
        var r = CreditBookingLink.Ref(bookingId);
        return await (from b in _db.CreditClassBookings.AsNoTracking()
                      join p in _db.CreditPasses.AsNoTracking() on b.PassId equals p.Id
                      where b.CompanyId == companyId && p.CompanyId == companyId && b.ExternalRef == r
                      orderby b.BookedAt descending
                      select p.PassNumber).FirstOrDefaultAsync();
    }

    /// <summary>หลังบันทึกสถานะการจองแล้ว: ให้บริการเสร็จ ⇒ ตัดเครดิต · ไม่มา ⇒ ตัดเครดิต (no-show) · ยกเลิก ⇒ คืนเครดิต (ร้านยกเลิก) ·
    /// ล้ม ⇒ สถานะการจองคงที่ผู้ใช้กด แต่<b>ล้มดัง</b>: หมายเหตุบนการจอง + ตอบผู้กด + log Error (ไม่กลืน)</summary>
    private async Task SettleCreditOnStatusAsync(Guid companyId, Guid bookingId, BookingStatus oldStatus, BookingStatus newStatus,
        string userId, List<string> notices, List<string> bookingNotes)
    {
        var action = CreditBookingLink.ActionFor(oldStatus, newStatus, paidWithCredit: true);
        if (action == CreditBookingLink.LedgerAction.None || _creditLedger == null) return;
        var r = CreditBookingLink.Ref(bookingId);
        try
        {
            var result = action switch
            {
                CreditBookingLink.LedgerAction.SettleAttended => await _creditLedger.SettleAsync(companyId, r, attended: true, userId),
                CreditBookingLink.LedgerAction.SettleNoShow => await _creditLedger.SettleAsync(companyId, r, attended: false, userId),
                // รอบ 208 (ฝ่ายค้าน W6 ข้อ 5): ลูกค้ายกเลิกเองผ่านลิงก์ = ไม่ใช่ร้านยกเลิก ⇒ นโยบายยกเลิกของคอร์ส (ทันเวลาคืน · ช้าริบ) ·
                // ร้าน/ระบบยกเลิก = คืนเครดิตเสมอ (เดิม)
                _ => await _creditLedger.ReleaseAsync(companyId, r, byBusiness: !CreditBookingLink.IsCustomerActor(userId), userId),
            };
            notices.Add($"{CreditBookingLink.ActionTh(action)} บัตร {result.PassNumber} แล้ว — {result.StatusLabel}"
                + (result.RecognizedAmount > 0m ? $" · รับรู้รายได้ {result.RecognizedAmount:N2}" : ""));
        }
        catch (KeyNotFoundException)
        {
            // การจองในสมุดปิด/ยกเลิกไปก่อนแล้ว (เช่น ปิดจากหน้า "คอร์ส & เครดิต") — ไม่ใช่ความล้มเหลว แต่บอกให้รู้
            notices.Add($"สมุดเครดิตไม่มีการจองค้างของใบนี้แล้ว ({CreditBookingLink.ActionTh(action)}ไว้ก่อนหน้า) — ไม่เปลี่ยนแปลงเครดิต");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "การจอง {BookingId}: {Action} ในสมุดเครดิตไม่สำเร็จ", bookingId, action);
            _db.ChangeTracker.Clear();
            var note = CreditBookingLink.LedgerFailureNote(action, ex.Message);
            notices.Add(note);
            bookingNotes.Add(note);
        }
    }
}
