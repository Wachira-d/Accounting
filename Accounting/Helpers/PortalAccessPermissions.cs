namespace Accounting.Helpers;

/// <summary>สิทธิ์ย่อยของพอร์ทัลลูกค้าหนึ่งรายการ (<c>PortalAccess.Can*</c>)</summary>
public enum PortalPermission { ViewInvoices, ViewStatements, DownloadPdf, MakePayment }

/// <summary>ธงสิทธิ์ของ <c>PortalAccess</c> หนึ่งแถว (อ่านจากฐานทุกคำขอ — ผู้ขายติ๊กออกแล้วมีผลทันที)</summary>
public sealed record PortalAccessFlags(bool CanViewInvoices, bool CanViewStatements, bool CanDownloadPdf, bool CanMakePayment)
{
    public bool Has(PortalPermission p) => p switch
    {
        PortalPermission.ViewInvoices => CanViewInvoices,
        PortalPermission.ViewStatements => CanViewStatements,
        PortalPermission.DownloadPdf => CanDownloadPdf,
        PortalPermission.MakePayment => CanMakePayment,
        _ => false,
    };
}

/// <summary>
/// รอบ 257 D-H3 — <b>ตัวตัดสินธงสิทธิ์พอร์ทัลลูกค้า (ตัวเดียว · pure)</b> · ผู้เรียก: <c>PortalPublicController.DenyPortalAsync</c> ทุกเส้นข้อมูล
/// (เอกสาร · PDF · ใบแจ้งยอด · ประวัติชำระ · ตัวเลือกชำระ · แนบสลิป · ส่งออก)
/// <para>═══ ที่มา ═══ ธง CanViewInvoices/Statements/DownloadPdf/MakePayment ไม่มีผู้อ่านเลย + หน้า customer-portal ส่ง <c>permissions:{…}</c> ที่เซิร์ฟเวอร์ไม่ bind
/// ⇒ เก็บเป็น false ทั้งหมด แต่ลูกค้ายังดาวน์โหลดได้ทุกอย่าง (ติ๊กออกไม่มีผล = silent no-op)</para>
/// <para>ไม่พบสิทธิ์ (แถวถูกลบ/ปิด/ไม่ตรงบริษัท) ⇒ ปฏิเสธ (fail-closed) · ตาราง "เส้นไหนต้องธงไหน" อยู่ที่ <see cref="Route"/> ตัวเดียว</para>
/// </summary>
public static class PortalAccessPermissions
{
    /// <summary>เส้นของพอร์ทัล → ธงที่ต้องมี (ทุกข้อ)</summary>
    public static IReadOnlyList<PortalPermission> Route(string route) => route switch
    {
        "documents" => new[] { PortalPermission.ViewInvoices },
        "document" => new[] { PortalPermission.ViewInvoices },
        "pdf" => new[] { PortalPermission.ViewInvoices, PortalPermission.DownloadPdf },
        "statement" => new[] { PortalPermission.ViewStatements },
        "payments" => new[] { PortalPermission.ViewStatements },
        "pay" => new[] { PortalPermission.ViewInvoices, PortalPermission.MakePayment },
        "slip" => new[] { PortalPermission.ViewInvoices, PortalPermission.MakePayment },
        "export" => new[] { PortalPermission.ViewInvoices },
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "เส้นพอร์ทัลที่ไม่รู้จัก — เพิ่มในตาราง Route ก่อนใช้"),
    };

    public const string NoAccessMessage = "สิทธิ์พอร์ทัลนี้ใช้ไม่ได้แล้ว — กรุณาเข้าสู่ระบบใหม่ หรือติดต่อผู้ขาย";

    public static string Message(PortalPermission p) => p switch
    {
        PortalPermission.ViewInvoices => "ผู้ขายไม่ได้เปิดสิทธิ์ \"ดูเอกสาร\" ให้บัญชีพอร์ทัลนี้ — ติดต่อผู้ขายเพื่อขอสิทธิ์",
        PortalPermission.ViewStatements => "ผู้ขายไม่ได้เปิดสิทธิ์ \"ดูใบแจ้งยอด/ประวัติชำระ\" ให้บัญชีพอร์ทัลนี้ — ติดต่อผู้ขายเพื่อขอสิทธิ์",
        PortalPermission.DownloadPdf => "ผู้ขายไม่ได้เปิดสิทธิ์ \"ดาวน์โหลด PDF\" ให้บัญชีพอร์ทัลนี้ — ดูรายละเอียดบนหน้าจอได้ หรือติดต่อผู้ขาย",
        PortalPermission.MakePayment => "ผู้ขายไม่ได้เปิดสิทธิ์ \"ชำระเงิน/แนบสลิป\" ผ่านพอร์ทัลให้บัญชีนี้ — ชำระตามช่องทางบนเอกสาร หรือติดต่อผู้ขาย",
        _ => NoAccessMessage,
    };

    /// <summary>รอบ 257 ฝ่ายค้าน L5: สร้างสิทธิ์ที่ไม่เปิดอะไรเลย = ลูกค้าเข้าแล้วใช้ไม่ได้สักเส้น (และเป็นลายเซ็นของหน้าเก่าที่แคชไว้ซึ่งส่ง
    /// <c>permissions:{…}</c> ที่ไม่ bind) ⇒ ปฏิเสธ 400 พร้อมทางไปต่อ · null = ผ่าน</summary>
    public static string? CreateRefusal(bool canViewInvoices, bool canViewStatements, bool canDownloadPdf, bool canMakePayment)
        => canViewInvoices || canViewStatements || canDownloadPdf || canMakePayment
            ? null
            : "เลือกสิทธิ์อย่างน้อย 1 อย่าง (ดูเอกสาร · ดาวน์โหลด PDF · ใบแจ้งยอด · ชำระเงิน) — ถ้าหน้านี้ไม่มีช่องให้ติ๊ก กรุณาโหลดหน้าใหม่ (Ctrl+F5) แล้วสร้างอีกครั้ง";

    /// <summary>null = ผ่าน · ข้อความ = ปฏิเสธ (403) · <paramref name="flags"/> null = ไม่พบสิทธิ์ ⇒ ปฏิเสธ</summary>
    public static string? Refusal(PortalAccessFlags? flags, IEnumerable<PortalPermission> needs)
    {
        if (flags == null) return NoAccessMessage;
        foreach (var n in needs)
            if (!flags.Has(n)) return Message(n);
        return null;
    }

    /// <summary>รอบ 257 D-H3 — ย้ายข้อมูล<b>ครั้งเดียว</b> (marker <c>r257-portal-flags</c>): แถวที่สร้างจากหน้า customer-portal ก่อนแก้ได้ธงเป็น false ทั้ง 4
    /// (หน้าส่ง <c>permissions:{…}</c> ที่ไม่ bind) ขณะที่ลูกค้าใช้ได้ทุกอย่างจริง ⇒ คืนสิ่งที่ลูกค้าใช้อยู่จริงก่อนเปิดด่าน (ดู · ใบแจ้งยอด · PDF · ชำระ/แนบสลิป)
    /// — ไม่งั้นลูกค้าพอร์ทัลทุกรายถูกตัดทันทีที่ deploy · ครั้งเดียวเท่านั้น (ผู้ขายติ๊กออกหมดภายหลัง = ตั้งใจ ห้ามคืนให้) · ต้องอยู่หลัง <c>OneShotDataFixesTableSql</c></summary>
    public static IReadOnlyList<string> MigrationStatements() => new[]
    {
        """
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM "OneShotDataFixes" WHERE "Key" = 'r257-portal-flags') THEN
                UPDATE "PortalAccesses" SET "CanViewInvoices" = true, "CanViewStatements" = true, "CanDownloadPdf" = true, "CanMakePayment" = true
                 WHERE "CanViewInvoices" = false AND "CanViewStatements" = false AND "CanDownloadPdf" = false AND "CanMakePayment" = false;
                INSERT INTO "OneShotDataFixes" ("Key", "AppliedAt") VALUES ('r257-portal-flags', (now() at time zone 'utc')) ON CONFLICT DO NOTHING;
            END IF;
        END $$;
        """,
    };
}
