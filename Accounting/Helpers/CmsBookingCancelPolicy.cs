using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลของการยกเลิกการจองต่อเอกสาร ERP ที่ผูกไว้</summary>
public enum CmsBookingCancelAction
{
    /// <summary>เอกสารถูกยกเลิกไปแล้ว — ไม่ต้องทำอะไร</summary>
    NothingToDo = 0,
    /// <summary>ยกเลิกเอกสาร (<c>VoidDocumentAsync</c>) — พฤติกรรมเดิมของใบที่ไม่ใช่มัดจำ/ใบมัดจำที่ยังไม่ออก</summary>
    VoidDocument = 1,
    /// <summary>ใบมัดจำที่ออกแล้ว — <b>ห้ามยกเลิก</b> · คงเป็นหนี้สินให้ผู้ใช้ตัดสินคืน (ใบลดหนี้) หรือริบที่ศูนย์มัดจำ + ประทับหมายเหตุ</summary>
    KeepDepositAsLiability = 2,
}

/// <summary>ผลของการ "ให้บริการเสร็จ" ต่อใบมัดจำของการจอง</summary>
public enum CmsBookingCompleteAction
{
    /// <summary>ไม่ใช่ใบมัดจำ — ไม่มีอะไรต้องรับรู้</summary>
    NothingToDo = 0,
    /// <summary>รับรู้รายได้จากมัดจำอัตโนมัติ (<c>RealizeDepositAsync</c>) — VAT เสียแล้วตอนรับเงิน/ย้ายจาก 21913 ในเส้นรับรู้ ·
    /// หรือบริษัทไม่จด VAT · หรือสิ่งที่ขายนอกระบบ VAT</summary>
    AutoRealize = 1,
    /// <summary>มัดจำเต็มยอด (ยังไม่แยก VAT) ของบริษัทที่จด VAT — การให้บริการเสร็จ = จุดความรับผิด ต้องออกใบกำกับเต็มจำนวนแล้วตัดชำระ
    /// ด้วยมัดจำ · รับรู้ตรงจากหนี้สินเข้ารายได้ = รายได้ไม่มี VAT เงียบ ๆ ⇒ ไม่ทำอัตโนมัติ</summary>
    NeedsTaxInvoice = 2,
    /// <summary>เงินประกันที่ต้องคืน — ไม่ใช่รายได้เมื่อให้บริการเสร็จ · คืน/ตัดชำระที่ศูนย์มัดจำ</summary>
    SecurityNotRevenue = 3,
    /// <summary>ใบมัดจำยังไม่ออก (ร่าง/รออนุมัติ/ถูกปฏิเสธ) หรือถูกยกเลิก — รับรู้ไม่ได้</summary>
    DepositNotIssued = 4,
}

/// <summary>
/// <b>นโยบายเอกสารมัดจำของการจองหน้าเว็บ (CMS booking) เมื่อการจองจบ</b> — ยกเลิก / ให้บริการเสร็จ (รอบ 194 · spec S6 C-1) · pure
///
/// <para>C-1 (ยืนยันแล้ว): เดิมยกเลิกการจอง = <c>VoidDocumentAsync</c> ใบมัดจำที่อนุมัติแล้วทั้งใบ ⇒ ลบภาษีขายของเดือนที่รับเงินย้อนหลัง
/// ทั้งที่ยังไม่ได้คืนเงิน (§86/4 ห้ามแก้ย้อนหลัง · คืนเงินต้องออกใบลดหนี้ §86/10 เดือนที่คืนจริง) และส่วนที่ควรริบก็ถูกลบ VAT ไปด้วย ·
/// ความล้มเหลวถูกกลืนเป็น LogWarning · ตอนนี้: ใบมัดจำที่ออกแล้ว <b>ไม่ถูกยกเลิก</b> — คงเป็นหนี้สิน รอผู้ใช้ตัดสินที่ศูนย์มัดจำ</para>
///
/// <para>ให้บริการเสร็จ: ใบมัดจำแบบเต็มยอดของบริษัทที่จด VAT ถ้ารับรู้ตรงเข้ารายได้ = ไม่มีใครออกใบกำกับเลย ⇒ บอกผู้ใช้แทนการรับรู้เงียบ ๆ</para>
/// </summary>
public static class CmsBookingCancelPolicy
{
    /// <summary>ยกเลิกการจอง → ทำอะไรกับเอกสาร ERP ที่ผูกไว้</summary>
    public static CmsBookingCancelAction DecideOnCancel(bool isDeposit, DocumentStatus status)
    {
        if (status == DocumentStatus.Voided) return CmsBookingCancelAction.NothingToDo;
        if (isDeposit && DocumentStatusRules.IsIssued(status)) return CmsBookingCancelAction.KeepDepositAsLiability;
        return CmsBookingCancelAction.VoidDocument;
    }

    /// <summary>ให้บริการเสร็จ → ทำอะไรกับใบมัดจำ</summary>
    /// <param name="depositVatAmount">VAT บนใบมัดจำ (0 = เต็มยอด/บริษัทไม่จด VAT/นอกระบบ VAT)</param>
    /// <param name="nature">ลักษณะเงินที่ตรึงบนใบ (null = ใบก่อนรอบ 194 · ไม่ทราบ)</param>
    /// <param name="companyVatRegistered">บริษัทจด VAT (<c>CompanyVatStatus</c>)</param>
    public static CmsBookingCompleteAction DecideOnComplete(
        bool isDeposit, DocumentStatus status, decimal depositVatAmount, DepositNature? nature, bool companyVatRegistered)
    {
        if (!isDeposit) return CmsBookingCompleteAction.NothingToDo;
        if (!DocumentStatusRules.IsEffective(status)) return CmsBookingCompleteAction.DepositNotIssued;
        if (nature == DepositNature.RefundableSecurity) return CmsBookingCompleteAction.SecurityNotRevenue;
        if (nature == DepositNature.NonVatSupply || !companyVatRegistered) return CmsBookingCompleteAction.AutoRealize;
        return depositVatAmount <= 0.005m ? CmsBookingCompleteAction.NeedsTaxInvoice : CmsBookingCompleteAction.AutoRealize;
    }

    /// <summary>หมายเหตุที่ประทับบนใบมัดจำและการจองเมื่อยกเลิก (ต่อแบบไม่ซ้ำด้วย <c>DepositPolicyResolver.AppendNoteOnce</c>) —
    /// วันที่ตามปฏิทินไทย รูป dd/MM/yyyy (ค.ศ. · InvariantCulture กันปีบวกซ้ำเมื่อ culture เป็น th-TH)</summary>
    public static string CancelNote(DateTime cancelledAtUtc)
        => $"ยกเลิกการจอง {ThaiDate.CalendarDateUtc(cancelledAtUtc).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} "
           + "— รอตัดสินคืน (ใบลดหนี้) หรือริบ ที่ศูนย์มัดจำ";

    /// <summary>ข้อความถึงผู้กดยกเลิก (คำตอบของ API) — บอกว่าทำไมใบไม่ถูกยกเลิก และต้องทำอะไรต่อ</summary>
    public static string KeepDepositNotice(string documentNumber)
        => $"ใบมัดจำ {documentNumber} ไม่ถูกยกเลิก — ออกใบ/ลงภาษีขายไปแล้วจึงแก้ย้อนหลังไม่ได้ (ป.รัษฎากร มาตรา 86/4) · "
           + "เงินยังเป็นมัดจำรับ (หนี้สิน) · ไปที่ “เงินมัดจำ” เพื่อคืนเงิน (ระบบออกใบลดหนี้ มาตรา 86/10 ให้) หรือริบตามเงื่อนไขการยกเลิก";

    /// <summary>ข้อความเมื่อให้บริการเสร็จแต่ระบบไม่รับรู้รายได้อัตโนมัติ — null = รับรู้ได้/ไม่เกี่ยว</summary>
    public static string? CompleteNotice(CmsBookingCompleteAction action, string documentNumber) => action switch
    {
        CmsBookingCompleteAction.NeedsTaxInvoice =>
            $"ใบมัดจำ {documentNumber} บันทึกแบบมัดจำเต็มยอด (ยังไม่แยก VAT) — ให้บริการเสร็จแล้วภาษีขายถึงกำหนด (มาตรา 78/1) · "
            + "ระบบไม่รับรู้รายได้อัตโนมัติเพื่อไม่ให้เกิดรายได้ที่ไม่มี VAT · ออกใบกำกับภาษีเต็มจำนวนแล้วกด “หักมัดจำ” ตัดชำระด้วยใบนี้",
        CmsBookingCompleteAction.SecurityNotRevenue =>
            $"ใบ {documentNumber} เป็นเงินประกันที่ต้องคืน — ไม่ใช่รายได้เมื่อให้บริการเสร็จ · คืนเงินหรือตัดชำระค่าเสียหายที่หน้า “เงินมัดจำ”",
        CmsBookingCompleteAction.DepositNotIssued =>
            $"ใบมัดจำ {documentNumber} ยังไม่ได้อนุมัติ (หรือถูกยกเลิก) — ระบบรับรู้รายได้จากมัดจำไม่ได้ · อนุมัติใบก่อนแล้วรับรู้ที่หน้า “เงินมัดจำ”",
        _ => null,
    };

    /// <summary>ข้อความเมื่อขั้นตอน ERP อัตโนมัติล้ม — ประทับบนการจอง + ตอบผู้เรียก (ไม่ใช่แค่ log · F2 ข้อ 7)</summary>
    public static string FailureNote(string step, string? documentNumber, string reason)
        => $"⚠️ {step} ของเอกสาร {documentNumber ?? "-"} ไม่สำเร็จ: {reason} — ต้องดำเนินการเองที่หน้าเอกสาร";
}
