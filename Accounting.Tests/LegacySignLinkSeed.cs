using System.Collections.Concurrent;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Tests;

/// <summary>
/// รอบ 267 P1 (คำตัดสิน 267-1-3) — ตัวจำลอง "ลิงก์ยอมรับ/เซ็นรับรุ่นก่อนรอบ 267" สำหรับเทสต์ของรอบ 239/240/250:
/// ฐานเก็บ<b>เฉพาะ hash</b> ของ token (<see cref="CustomerDocPage.LegacyStored"/>) เหมือนของจริงหลัง migration ·
/// token ดิบ (สิ่งที่ลูกค้าถือใน URL) อยู่ในหน่วยความจำของเทสต์เท่านั้น — เทสต์เดิมที่เคยอ่าน token จากคอลัมน์ใช้ <see cref="RawOf"/> แทน
/// (ระบบจริงไม่ออกลิงก์รุ่นนี้เพิ่มแล้ว — ลิงก์ที่ส่งให้ลูกค้า = ลิงก์หน้าเอกสารของใบ)
/// </summary>
internal static class LegacySignLinkSeed
{
    private static readonly ConcurrentDictionary<Guid, string> Raw = new();

    /// <summary>ตั้งลิงก์รุ่นก่อนบนใบ (ก่อนบันทึก) — ใบเสนอราคา 30 วัน · ใบส่งของ 14 วัน (ค่าเดิม) หรือ <paramref name="expires"/></summary>
    public static string Seed(Document doc, DateTime? expires = null, DateTime? issuedAt = null)
    {
        var raw = DocumentPayLinkPolicy.NewToken();
        var now = issuedAt ?? DateTime.UtcNow;
        if (doc.DocumentType == DocumentType.Quotation)
        {
            doc.QuotationAcceptToken = CustomerDocPage.LegacyStored(raw);
            doc.QuotationAcceptTokenExpiresAt = expires ?? now.AddDays(30);
        }
        else
        {
            doc.DeliverySignToken = CustomerDocPage.LegacyStored(raw);
            doc.DeliverySignTokenExpiresAt = expires ?? now.AddDays(14);
        }
        doc.SignLinkIssuedAt = now;
        Raw[doc.Id] = raw;
        return raw;
    }

    /// <summary>token ดิบของใบที่ <see cref="Seed"/> ไว้ (สิ่งที่ลูกค้าถือ)</summary>
    public static string RawOf(Document doc) => Raw[doc.Id];
}
