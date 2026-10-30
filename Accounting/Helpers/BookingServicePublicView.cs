using Accounting.Models.DTOs.Cms;

namespace Accounting.Helpers;

/// <summary>มุมมองบริการจองที่ผู้เยี่ยมชมหน้าเว็บเห็น (รอบ 207 · ฝ่ายค้านข้อ 10) — ตัดข้อมูลภายใน (รหัส/ชื่อสินค้าในระบบบัญชี · คำเตือนราคา) ·
/// ผู้เรียก: endpoint สาธารณะของ booking services (CmsStorefrontController · CmsBookingController แบบไม่ล็อกอิน)</summary>
public static class BookingServicePublicView
{
    public static List<BookingServiceResponse> Strip(List<BookingServiceResponse> list)
    {
        foreach (var s in list) { s.ProductId = null; s.ProductCode = null; s.ProductName = null; s.ProductWarning = null; s.PaymentProblem = null; }   // รอบ 218: ข้อความถึงร้าน ไม่ใช่ลูกค้า
        return list;
    }
}
