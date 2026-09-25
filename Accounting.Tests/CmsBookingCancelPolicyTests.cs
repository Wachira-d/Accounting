using System.Globalization;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 194 ทีม C (spec S6 C-1) — การจองหน้าเว็บ (CMS booking) ถูกยกเลิก/ให้บริการเสร็จ ⇒ ทำอะไรกับเอกสาร ERP · สองทิศ:
/// <b>บั๊กที่แก้</b> ใบมัดจำที่ออกแล้วไม่ถูกยกเลิก (เดิม void ทั้งใบ = ลบภาษีขายเดือนที่รับเงินย้อนหลัง) · มัดจำเต็มยอดไม่ถูกรับรู้เป็นรายได้
/// ไม่มี VAT เงียบ ๆ · <b>ของเดิมที่ต้องยังเหมือนเดิม</b> ใบกำกับ/ใบเสนอราคาของการจองยังถูกยกเลิก · มัดจำ VAT ทันทียังรับรู้อัตโนมัติ
/// </summary>
public class CmsBookingCancelPolicyTests
{
    // ═════════════════ บั๊ก C-1 — ใบมัดจำที่ออกแล้วห้ามยกเลิก ═════════════════

    [Theory]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Sent)]
    [InlineData(DocumentStatus.PartiallyPaid)]
    [InlineData(DocumentStatus.Paid)]
    [InlineData(DocumentStatus.Overdue)]
    public void Cancel_IssuedDeposit_KeptAsLiability_NotVoided(DocumentStatus status)
        => Assert.Equal(CmsBookingCancelAction.KeepDepositAsLiability, CmsBookingCancelPolicy.DecideOnCancel(isDeposit: true, status));

    // ═════════════════ ทิศตรงข้าม — ของเดิมยังเหมือนเดิม ═════════════════

    /// <summary>ใบกำกับ (Guaranteed) / ใบเสนอราคา (Appointment) ของการจอง — ยกเลิกการจองแล้วยังยกเลิกใบเหมือนเดิม</summary>
    [Theory]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Paid)]
    [InlineData(DocumentStatus.Draft)]
    public void Cancel_NonDepositDocument_StillVoided(DocumentStatus status)
        => Assert.Equal(CmsBookingCancelAction.VoidDocument, CmsBookingCancelPolicy.DecideOnCancel(isDeposit: false, status));

    /// <summary>ใบมัดจำที่ยังไม่ออก (ร่าง/รออนุมัติ — ยังไม่มีเลข §86/4 · ไม่มี JE · ไม่เข้า ภ.พ.30) ยกเลิกได้เหมือนเดิม</summary>
    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.WaitingApproval)]
    [InlineData(DocumentStatus.Rejected)]
    public void Cancel_UnissuedDeposit_StillVoided(DocumentStatus status)
        => Assert.Equal(CmsBookingCancelAction.VoidDocument, CmsBookingCancelPolicy.DecideOnCancel(isDeposit: true, status));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cancel_AlreadyVoided_NothingToDo(bool isDeposit)
        => Assert.Equal(CmsBookingCancelAction.NothingToDo, CmsBookingCancelPolicy.DecideOnCancel(isDeposit, DocumentStatus.Voided));

    // ═════════════════ ให้บริการเสร็จ ═════════════════

    /// <summary>พฤติกรรมเดิม: มัดจำ VAT ทันที (มี VAT บนใบ · ใบก่อนรอบ 194 ลักษณะ NULL) ⇒ รับรู้รายได้อัตโนมัติ</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(DepositNature.PartOfPrice)]
    public void Complete_TaxedDeposit_AutoRealize_AsBefore(DepositNature? nature)
        => Assert.Equal(CmsBookingCompleteAction.AutoRealize,
            CmsBookingCancelPolicy.DecideOnComplete(true, DocumentStatus.Approved, 65.42m, nature, companyVatRegistered: true));

    /// <summary>มัดจำเต็มยอด (VAT 0) ของบริษัทที่จด VAT ⇒ ห้ามรับรู้ตรงเข้ารายได้ (= รายได้ไม่มี VAT ไม่มีใบกำกับเลย) — บอกผู้ใช้แทน</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(DepositNature.PartOfPrice)]
    public void Complete_FullDepositOfVatCompany_NeedsTaxInvoice(DepositNature? nature)
    {
        var action = CmsBookingCancelPolicy.DecideOnComplete(true, DocumentStatus.Approved, 0m, nature, companyVatRegistered: true);
        Assert.Equal(CmsBookingCompleteAction.NeedsTaxInvoice, action);
        var notice = CmsBookingCancelPolicy.CompleteNotice(action, "REC-001");
        Assert.NotNull(notice);
        Assert.Contains("REC-001", notice);
        Assert.Contains("ใบกำกับภาษี", notice);
    }

    [Fact]
    public void Complete_NoVatCases_AutoRealize()
    {
        // บริษัทไม่จด VAT — ไม่มีภาษีให้ขาด
        Assert.Equal(CmsBookingCompleteAction.AutoRealize,
            CmsBookingCancelPolicy.DecideOnComplete(true, DocumentStatus.Approved, 0m, null, companyVatRegistered: false));
        // นอกระบบ VAT (§81) — VAT 0 ถูกต้อง
        Assert.Equal(CmsBookingCompleteAction.AutoRealize,
            CmsBookingCancelPolicy.DecideOnComplete(true, DocumentStatus.Paid, 0m, DepositNature.NonVatSupply, companyVatRegistered: true));
        Assert.Null(CmsBookingCancelPolicy.CompleteNotice(CmsBookingCompleteAction.AutoRealize, "REC-001"));
    }

    [Fact]
    public void Complete_Security_NotRevenue()
        => Assert.Equal(CmsBookingCompleteAction.SecurityNotRevenue,
            CmsBookingCancelPolicy.DecideOnComplete(true, DocumentStatus.Approved, 0m, DepositNature.RefundableSecurity, true));

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.Voided)]
    public void Complete_DepositNotIssued_Told(DocumentStatus status)
    {
        var action = CmsBookingCancelPolicy.DecideOnComplete(true, status, 65.42m, null, true);
        Assert.Equal(CmsBookingCompleteAction.DepositNotIssued, action);
        Assert.NotNull(CmsBookingCancelPolicy.CompleteNotice(action, "REC-9"));
    }

    [Fact]
    public void Complete_NotDeposit_NothingToDo()
        => Assert.Equal(CmsBookingCompleteAction.NothingToDo,
            CmsBookingCancelPolicy.DecideOnComplete(false, DocumentStatus.Approved, 70m, null, true));

    // ═════════════════ ข้อความ ═════════════════

    /// <summary>วันที่ตามปฏิทินไทย (UTC 20:00 = 03:00 วันถัดไปที่กรุงเทพ) · ค.ศ. InvariantCulture — culture th-TH ต้องไม่บวกปีซ้ำ</summary>
    [Fact]
    public void CancelNote_BangkokDate_InvariantYear()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var note = CmsBookingCancelPolicy.CancelNote(new DateTime(2026, 9, 24, 20, 0, 0, DateTimeKind.Utc));
            Assert.Equal("ยกเลิกการจอง 25/09/2026 — รอตัดสินคืน (ใบลดหนี้) หรือริบ ที่ศูนย์มัดจำ", note);
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public void Notices_NameDocumentAndNextStep()
    {
        var keep = CmsBookingCancelPolicy.KeepDepositNotice("REC-2026-001");
        Assert.Contains("REC-2026-001", keep);
        Assert.Contains("86/4", keep);
        Assert.Contains("ใบลดหนี้", keep);
        var fail = CmsBookingCancelPolicy.FailureNote("การยกเลิกเอกสาร", "TIV-01", "งวดภาษียื่นแล้ว");
        Assert.Contains("TIV-01", fail);
        Assert.Contains("งวดภาษียื่นแล้ว", fail);
    }
}
