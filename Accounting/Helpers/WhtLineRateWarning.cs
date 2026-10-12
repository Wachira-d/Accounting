using System;
using System.Collections.Generic;
using System.Linq;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **คำเตือนก่อนอนุมัติ "อัตรา WHT ไม่ตรงประเภทเงินได้ของบรรทัด + ชนิดผู้ถูกหัก" (ในประเทศ)** — ตัวประกอบข้อความเดียว (pure)
///
/// ═══ ที่มา (ฝ่ายค้านรอบ WHT/50 ทวิ M2 + M3 · 2026-10-12) ═══
/// <para>M2: ด่านใน <c>DocumentService.CollectApprovalWarningsAsync</c> ตัดสินชนิดผู้รับจาก <c>doc.Contact</c> (ผู้รับเงินบน PV)
/// ทั้งที่ผู้มีเงินได้คือ "ผู้ถูกหักภาษี" (<c>WhtPayeeContactId</c>) — ซื้อประกันผ่านนายหน้าบุคคลธรรมดา (ผู้รับเงิน) แต่ผู้ถูกหักคือบริษัทประกัน
/// (นิติบุคคล) ⇒ "8ins" 1% ถูกเตือนว่าหักผิดทั้งที่ถูก · ผู้เรียกต้องส่งข้อมูลของ<b>ผู้ถูกหัก</b> (<c>LoadWhtPayeeAsync</c>)</para>
/// <para>M3: คำเตือนนี้ไม่มีป้าย ⇒ <see cref="ApprovalAcknowledgement"/> หยุดทางเข้าที่ไม่มีคนเห็น (ใบประจำ · LINE · OCR อนุมัติอัตโนมัติ · API)
/// ขณะที่นโยบายเจ้าของคือ "เตือน ไม่บล็อก" ⇒ ทุกข้อความขึ้นต้นด้วย <see cref="Prefix"/> และผ่านทางเข้าที่ไม่มีคน (เว็บ/มือถือยังแสดงให้กดรับทราบ)</para>
/// </summary>
public static class WhtLineRateWarning
{
    /// <summary>ป้ายของคำเตือนชุดนี้ — ห้ามเปลี่ยนค่า (ตัวกรองใน <see cref="ApprovalAcknowledgement"/> อ่านป้ายนี้)</summary>
    public const string Prefix = "[WHT-RATE]";

    /// <summary>เป็นคำเตือนอัตราเทียบประเภทเงินได้ไหม</summary>
    public static bool IsApprovalWarning(string warning)
        => warning.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// คำเตือนรายบรรทัด (ฝั่งผู้จ่าย · ผู้ถูกหักในประเทศ) — บรรทัดที่หัก &gt; 0 และระบุประเภทเงินได้ เทียบกับ
    /// <see cref="ThaiWhtRateTable.CheckRateForType"/> ตามชนิด<b>ผู้ถูกหัก</b> · ไม่รู้ชนิด = ยอมรับอัตราของชนิดใดชนิดหนึ่ง
    /// </summary>
    /// <param name="lines">บรรทัดของเอกสาร (คำอธิบาย · รหัสประเภทเงินได้ · อัตรา %)</param>
    /// <param name="payeeTaxId">เลขผู้เสียภาษีของผู้ถูกหัก (ไม่ใช่ผู้รับเงินบนเอกสารเมื่อแยกกัน)</param>
    /// <param name="payeeContactType">ประเภทผู้ติดต่อของผู้ถูกหัก</param>
    /// <param name="payeeName">ชื่อผู้ถูกหัก</param>
    /// <param name="paymentDate">วันจ่าย (อัตราลดชั่วคราวตามช่วงเวลา)</param>
    public static IReadOnlyList<string> ForDomesticPayee(
        IEnumerable<(string? Description, string? IncomeTypeCode, decimal Rate)> lines,
        string? payeeTaxId, ContactType payeeContactType, string? payeeName, DateTime paymentDate)
    {
        var juristic = WhtPayeeKind.Detect(payeeTaxId, payeeContactType, payeeName);
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (line.Rate <= 0m || string.IsNullOrWhiteSpace(line.IncomeTypeCode)) continue;
            if (ThaiWhtRateTable.CheckRateForType(line.IncomeTypeCode, line.Rate, juristic, paymentDate) is string warn)
                result.Add($"{Prefix} อัตรา WHT ของ '{line.Description}': {warn}");
        }
        return result;
    }
}
