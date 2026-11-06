using System;
using System.Globalization;

namespace Accounting.Helpers;

/// <summary>
/// **คำเตือนก่อนอนุมัติ "ลูกค้ายังค้างชำระเกิน 30 วัน"** — ตัวประกอบข้อความ + ป้ายรหัสเดียว (pure) · รอบ 248 (AUTOPILOT_BILLING_DESIGN §4.3 · ช่องว่าง A-2)
///
/// <para>═══ ที่มา ═══ คำเตือนนี้เดิมเป็นข้อความล้วน (<c>DocumentService.CollectApprovalWarningsAsync</c>) ⇒ ใบประจำที่ตั้งอนุมัติอัตโนมัติหยุดเป็นร่างทุกงวด
/// เมื่อลูกค้ามีใบค้าง และเจ้าของไม่มีทางเลือก "อนุมัติต่อพร้อมบันทึก" · นโยบายอนุมัติ (<see cref="AutoApprovePolicy"/>) ปล่อยได้เฉพาะคำเตือนที่มี
/// <b>ป้ายรหัส</b> — ห้าม match ข้อความไทย (ข้อความเปลี่ยนแล้วนโยบายหยุดทำงานเงียบ ๆ) ⇒ ทุกข้อความขึ้นต้นด้วย <see cref="Prefix"/></para>
/// <para>ไม่อยู่ในชุด "ผ่านโดยไม่มีคน" ของ <see cref="ApprovalAcknowledgement"/> — ทางเข้าอัตโนมัติยังหยุดตามเดิม (รอบ 247 ไม่ถดถอย) เว้นแต่เจ้าของตั้งนโยบายไว้เอง</para>
/// </summary>
public static class ArOverdueApprovalWarning
{
    /// <summary>รหัสของคำเตือนนี้ในนโยบายอนุมัติ — ห้ามเปลี่ยนค่า (เก็บใน <c>AutoApprovePolicyJson</c>)</summary>
    public const string Code = "AR-OVERDUE";

    /// <summary>ป้ายนำหน้าข้อความ — ห้ามเปลี่ยนค่า</summary>
    public const string Prefix = "[" + Code + "]";

    /// <summary>มาตราอ้างอิงในร่องรอย — เป็นการควบคุมสินเชื่อภายในของบริษัท ไม่ใช่ข้อบังคับทางภาษี (ห้ามอ้าง §86 ซึ่งไม่เกี่ยว)</summary>
    public const string LegalReference = "INTERNAL-CONTROL/AR-OVERDUE";

    /// <summary>เป็นคำเตือนค้างชำระไหม</summary>
    public static bool IsApprovalWarning(string? warning)
        => warning != null && warning.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>ข้อความคำเตือน (ตัวประกอบเดียว)</summary>
    /// <param name="overdueCount">จำนวนใบที่ค้างเกิน 30 วัน</param>
    /// <param name="overdueTotal">ยอดค้างรวม (บาท)</param>
    public static string Message(int overdueCount, decimal overdueTotal)
        => $"{Prefix} ผู้ติดต่อนี้ยังค้างชำระ {overdueCount} ใบ รวม {overdueTotal.ToString("N2", CultureInfo.InvariantCulture)} THB — ตรวจวงเงินเครดิตก่อนอนุมัติเอกสารใหม่";
}
