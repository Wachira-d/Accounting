using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 288b (ฝ่ายค้านข้อ 1 · คำตัดสิน 288-9) — <b>ตราเนื้อหาเอกสาร</b>ตัวเดียว (canonical function เดียว ใช้ทั้งฝั่งที่ตรวจและฝั่งที่อนุมัติ — กฎเหล็ก #4 C) ·
/// ผู้ตรวจที่ไม่ได้ถือล็อกแถวใบ (งานอัตโนมัติ — อ่านใบ ตัดสินเพดาน/ผัง/ภาษีหัก ณ ที่จ่าย/ใบกำกับ แล้วจึงสั่งอนุมัติ) ส่งตรานี้ให้
/// <c>DocumentService.ApproveDocumentAsync</c> ⇒ ใต้ล็อก <c>FOR UPDATE</c> ของการอนุมัติ อ่านใบสดแล้วเทียบตรา · ไม่ตรง = ใบถูกแก้ระหว่างตรวจ ⇒
/// ไม่อนุมัติ (ใบคงเป็นร่าง · ผู้เรียกบอกเหตุ แล้วประเมินใหม่รอบถัดไป) · pure (ไม่แตะ DB)
/// <para>ครอบเฉพาะค่าที่ด่านอนุมัติเองอ่าน: ผู้ติดต่อ · วันที่ · สกุลเงิน · ยอดทุกช่อง · ภาษีหัก ณ ที่จ่าย · ใบกำกับของผู้ขาย · หลักฐานภาษีซื้อ ·
/// ทุกบรรทัดที่ไม่ถูกลบ (ผัง · จำนวน · ราคา · ส่วนลด · VAT · สิทธิ์เคลม · ภาษีหัก ณ ที่จ่าย · แถวคำแนะนำผัง) · <b>ไม่</b>ครอบหมายเหตุภายใน/เวลาแก้ไข
/// (งานอัตโนมัติเองเขียนสองช่องนี้)</para>
/// </summary>
public static class ApprovalContentStamp
{
    public const string RuleCode = "R288B-APPROVE-CONTENT-CHANGED";
    public const string ChangedMessage =
        "ใบถูกแก้ระหว่างที่ระบบตรวจเงื่อนไขอนุมัติเอง (ยอด/บรรทัด/ผังบัญชี/ภาษี) — ไม่อนุมัติ ใบยังเป็นร่าง · ระบบตรวจใหม่รอบถัดไป หรือตรวจแล้วกดอนุมัติเองได้";

    public static string Of(Document doc)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(512);
        sb.Append("v1|").Append(doc.Id).Append('|').Append(doc.CompanyId).Append('|').Append((int)doc.DocumentType).Append('|').Append(doc.ContactId)
            .Append('|').Append(doc.DocumentDate.ToString("yyyy-MM-dd", inv)).Append('|').Append((doc.Currency ?? "").Trim())
            .Append('|').Append(M(doc.SubTotal)).Append('|').Append(M(doc.DiscountAmount))
            .Append('|').Append(M(doc.VatAmount)).Append('|').Append(M(doc.TotalAmount)).Append('|').Append(M(doc.RoundingAdjustment))
            .Append('|').Append(M(doc.WithholdingTaxAmount)).Append('|').Append(M(doc.WhtPayerBorneAmount))
            .Append('|').Append(doc.HasTaxInvoiceReference ? '1' : '0').Append('|').Append(doc.SupplierInvoiceNumber ?? "")
            .Append('|').Append(doc.SupplierTaxInvoiceDate?.ToString("yyyy-MM-dd", inv) ?? "").Append('|').Append(doc.SupplierBranchCode ?? "")
            .Append('|').Append(doc.PartnerInputVatEvidence ?? "");
        foreach (var l in doc.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.Id))
        {
            sb.Append("\n").Append(l.Id).Append('|').Append(l.LineOrder).Append('|').Append(l.Description ?? "").Append('|').Append(M(l.Quantity))
                .Append('|').Append(M(l.UnitPrice)).Append('|').Append(M(l.DiscountPercent)).Append('|').Append(M(l.DiscountAmount)).Append('|').Append(M(l.Amount))
                .Append('|').Append(M(l.VatRate)).Append('|').Append(M(l.VatAmount)).Append('|').Append(l.IsVatClaimable ? '1' : '0')
                .Append('|').Append(M(l.WithholdingTaxRate)).Append('|').Append(M(l.WithholdingTaxAmount)).Append('|').Append(l.IncomeTypeCode ?? "")
                .Append('|').Append(l.AccountId?.ToString() ?? "").Append('|').Append(l.GlAccountAiFeedbackId?.ToString() ?? "");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>ตัวเลขเงินรูปแบบเดียว (ตัดศูนย์ท้าย — 1.10 กับ 1.1 จาก PostgreSQL numeric คือค่าเดียวกัน)</summary>
    private static string M(decimal v) => v.ToString("0.############################", CultureInfo.InvariantCulture);
}
