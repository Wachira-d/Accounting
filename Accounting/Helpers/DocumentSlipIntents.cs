using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **สลิปโอนของเอกสาร (พอร์ทัลลูกค้า → <c>PaymentIntent(Document, ManualSlip)</c>) — ตัวตัดสินตัวเดียว** (รอบ 250 WP-02b · ฝ่ายค้าน H1/H2/M4/M6)
/// <list type="bullet">
/// <item>H2 รับสลิปได้ไหม = ดู<b>ยอดค้างของเอกสาร</b> (ไม่ใช่ "เคยมีรายการสำเร็จ" — สลิปงวด 2 ต้องรับได้) · สลิปแต่ละใบเป็นรายการของตัวเอง (ไม่รวมสองสลิปยอดเท่ากันเป็นรายการเดียว)</item>
/// <item>H1 สลิปที่รอตรวจแล้วมีเงินก้อนอื่นบันทึกจนยอดค้างต่ำกว่ายอดสลิป ⇒ <b>ติดธง</b> (ยืนยันแล้วจะรับเงินซ้ำ) · ยืนยันด้วยมือถูกบล็อกเมื่อยอดสลิป &gt; ยอดค้าง
/// (เดิม: ยืนยัน ⇒ intent สำเร็จ แต่ <c>CreatePaymentAsync</c> ปฏิเสธจ่ายเกิน ⇒ "สำเร็จแต่ไม่มีการรับชำระ")</item>
/// <item>M4 วันที่รับเงินของการยืนยัน = วันที่เงินเข้าตามหลักฐาน (ค่าตั้งต้น = วันที่ลูกค้าแนบสลิป · เวลาไทย) ไม่ใช่วันที่กดยืนยัน — tax point §78/1</item>
/// <item>M6 ที่อยู่ไฟล์สลิปของแถวรุ่นเก่าอยู่ในหมายเหตุ ("ไฟล์: …") — ดึงมาใส่คำอธิบายรายการรอยืนยัน</item>
/// </list>
/// </summary>
public static class DocumentSlipIntents
{
    public const string RuleCode = "PORTAL-SLIP";
    public const string SupersededCode = "slip_superseded";

    private static readonly TimeSpan Bangkok = TimeSpan.FromHours(7);

    /// <summary>รายการนี้เป็นสลิปโอนของเอกสารไหม</summary>
    public static bool IsDocumentSlip(PaymentSourceKind kind, PaymentMethodKind method)
        => kind == PaymentSourceKind.Document && method == PaymentMethodKind.ManualSlip;

    /// <summary>ยอดสลิปเกินยอดค้างของเอกสาร (เผื่อปัดเศษ 0.01)</summary>
    public static bool ExceedsBalance(decimal slipAmount, decimal balanceDue) => slipAmount > balanceDue + 0.01m;

    /// <summary>H2 + L4: ลูกค้าแนบสลิปของเอกสารนี้ได้ไหม · null = ได้ · ข้อความ = ไทยถึงลูกค้า</summary>
    public static string? UploadProblem(DocumentStatus status, decimal balanceDue, decimal amount)
    {
        if (status is DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Voided or DocumentStatus.Rejected)
            return "เอกสารนี้ยังไม่พร้อมรับชำระ — ติดต่อผู้ขาย";
        if (status == DocumentStatus.Paid || balanceDue <= 0.009m)
            return "เอกสารนี้ชำระครบแล้ว — ไม่ต้องแนบสลิปเพิ่ม (ถ้าโอนซ้ำ ติดต่อผู้ขายเพื่อขอคืนเงิน)";
        if (amount <= 0m)
            return "ยอดที่จะชำระต้องมากกว่า 0";
        if (ExceedsBalance(amount, balanceDue))
            return $"ยอดสลิปมากกว่ายอดค้างชำระ ({balanceDue:#,##0.00} บาท) — ตรวจยอดอีกครั้ง หรือติดต่อผู้ขาย";
        return null;
    }

    /// <summary>ข้อความเมื่อยืนยันสลิปไม่ได้เพราะยอดสลิปเกินยอดค้าง (มีเงินก้อนอื่นบันทึกไปแล้ว)</summary>
    public static string ConfirmBlockedMessage(decimal slipAmount, decimal balanceDue)
        => $"ยอดสลิป {slipAmount:#,##0.00} บาท มากกว่ายอดค้างของเอกสารตอนนี้ ({balanceDue:#,##0.00} บาท) — อาจมีการรับเงินก้อนนี้ไปแล้วด้วยรายการอื่น · "
           + "ตรวจบัญชีธนาคาร: ถ้าเงินตามสลิปเข้าซ้ำ ให้ \"ปฏิเสธสลิป\" แล้วคืนเงินลูกค้า · ถ้าเป็นเงินคนละก้อน ให้บันทึกรับชำระส่วนที่เหลือที่หน้าเอกสาร";

    /// <summary>ข้อความธงบนรายการรอยืนยันเมื่อเงินก้อนอื่นทำให้ยอดค้างต่ำกว่ายอดสลิป</summary>
    private static string SupersededMessage(decimal balanceDue)
        => $"⚠️ มีการรับชำระเอกสารนี้ด้วยรายการอื่นแล้ว (ยอดค้างเหลือ {balanceDue:#,##0.00} บาท) — ยืนยันสลิปนี้ไม่ได้ · ตรวจว่าเงินซ้ำไหม แล้วปฏิเสธสลิป";

    /// <summary>M4: วันที่รับเงินของการยืนยัน (UTC เที่ยงคืนของวันไทย) · <paramref name="requested"/> = วันที่ที่พนักงานระบุ (วันไทย) ·
    /// null ⇒ วันที่ลูกค้าแนบสลิป · อนาคต ⇒ ปัญหา</summary>
    public static (DateTime? PaidAtUtc, string? Problem) ReceivedDate(DateTime? requested, DateTime slipUploadedAtUtc, DateTime nowUtc)
    {
        var todayTh = (nowUtc + Bangkok).Date;
        var day = requested?.Date ?? (slipUploadedAtUtc + Bangkok).Date;
        if (day > todayTh)
            return (null, "วันที่รับเงินต้องไม่เป็นวันในอนาคต");
        return (DateTime.SpecifyKind(day, DateTimeKind.Utc), null);
    }

    private static readonly Regex LegacyFile = new(@"ไฟล์:\s*(\S+)", RegexOptions.CultureInvariant);

    /// <summary>M6: ที่อยู่ไฟล์สลิปจากหมายเหตุของแถวรุ่นเก่า ("… · ไฟล์: /uploads/portal-slips/…") · ไม่มี = null</summary>
    public static string? SlipUrlFromLegacyNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var m = LegacyFile.Match(notes);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>H1: หลังบันทึกรับชำระ — สลิปรอตรวจของเอกสารเดียวกันที่ยอดเกินยอดค้างใหม่ ⇒ ติดธง (FailureCode/FailureMessage + ประวัติ) ·
    /// <b>ไม่เปลี่ยนสถานะ</b> (ผู้เขียนสถานะคือ PaymentIntentService ตัวเดียว · พนักงานปฏิเสธที่หน้ารายการรับชำระ) · คืนจำนวนที่ติดธง</summary>
    public static async Task<int> FlagSupersededAsync(AccountingDbContext db, Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var balance = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => (decimal?)d.BalanceDue)
            .FirstOrDefaultAsync(ct);
        if (balance == null) return 0;
        var open = await db.PaymentIntents
            .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.Document && i.SourceId == documentId
                && i.MethodKind == PaymentMethodKind.ManualSlip
                && (i.Status == PaymentIntentStatus.Pending || i.Status == PaymentIntentStatus.Created)
                && (i.FailureCode == null || i.FailureCode != SupersededCode))
            .ToListAsync(ct);
        var flagged = 0;
        foreach (var i in open.Where(i => ExceedsBalance(i.Amount, balance.Value)))
        {
            i.FailureCode = SupersededCode;
            i.FailureMessage = SupersededMessage(balance.Value);
            i.UpdatedAt = DateTime.UtcNow;
            db.PaymentIntentEvents.Add(new PaymentIntentEvent
            {
                CompanyId = companyId, IntentId = i.Id, At = DateTime.UtcNow, Source = PaymentEventSource.System,
                FromStatus = i.Status, ToStatus = i.Status, Note = i.FailureMessage,
            });
            flagged++;
        }
        if (flagged > 0) await db.SaveChangesAsync(ct);
        return flagged;
    }
}
