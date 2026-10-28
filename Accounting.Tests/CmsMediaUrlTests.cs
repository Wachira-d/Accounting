using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// URL ของสื่อ CMS/สลิปออเดอร์ (<see cref="CmsMediaUrl"/> · ทีม W4 · R-43) — ไฟล์ใหม่ผ่านที่เก็บกลาง · ไฟล์เดิมบน disk ยังเปิดได้ (ทิศตรงข้าม · F2 #9)
/// </summary>
public class CmsMediaUrlTests
{
    private static readonly System.Guid Cid = System.Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly System.Guid Sid = System.Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly System.Guid Att = System.Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void ทิศตรงข้าม_แถวเดิมไม่มีAttachmentId_ยังชี้URLเดิม_uploads_cms()
    {
        Assert.Equal($"/uploads/cms/{Cid}/{Sid}/abc.jpg", CmsMediaUrl.PublicUrl(Cid, Sid, "abc.jpg", null));
        Assert.Equal($"/uploads/cms/{Cid}/{Sid}/abc.jpg", CmsMediaUrl.PublicUrl(Cid, Sid, "abc.jpg", System.Guid.Empty));
    }

    [Fact]
    public void แถวใหม่_ชี้endpoint_storefront_ที่ตรวจบริษัทและเว็บ()
    {
        Assert.Equal($"/api/companies/{Cid}/cms/sites/{Sid}/storefront/media/{Att}", CmsMediaUrl.PublicUrl(Cid, Sid, "ignored.jpg", Att));
    }

    [Fact]
    public void ชื่อไฟล์เดิมที่มีอักขระพิเศษ_ถูกescapeในURL()
    {
        var url = CmsMediaUrl.LegacyMediaUrl(Cid, Sid, "a b#c.jpg");
        Assert.EndsWith("/a%20b%23c.jpg", url);
    }

    [Fact]
    public void สลิป_URLดาวน์โหลดต้องแนบJWT_สลิปเดิมเปิดตรง()
    {
        var url = CmsMediaUrl.SlipDownloadUrl(Cid, Att);
        Assert.Equal($"/api/companies/{Cid}/attachments/{Att}/download", url);
        Assert.True(CmsMediaUrl.NeedsAuth(url));
        Assert.False(CmsMediaUrl.NeedsAuth("/uploads/order-slips/2026-10/abc.jpg"));
        Assert.False(CmsMediaUrl.NeedsAuth(null));
        Assert.False(CmsMediaUrl.NeedsAuth(""));
    }

    [Fact]
    public void ชนิดไฟล์แนบ_ตรงกับตารางสิทธิ์()
    {
        // SiteOrder มีกฎใน AttachmentPermissionScope อยู่แล้ว · SiteMedia เพิ่มรอบนี้ — ชื่อต้องตรงกัน ไม่งั้นด่านลบ/ดาวน์โหลดตอบ "ไม่ทราบชนิด"
        Assert.NotNull(AttachmentPermissionScope.Resolve(CmsMediaUrl.OrderEntityType).CanonicalType);
        Assert.NotNull(AttachmentPermissionScope.Resolve(CmsMediaUrl.SiteMediaEntityType).CanonicalType);
    }
}
