using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// แปลงลิงก์วิดีโอที่ผู้ดูแลวางมา → URL สำหรับฝัง
///
/// ที่มา: ผู้ใช้ก๊อปลิงก์จากแถบที่อยู่ (`watch?v=` · `youtu.be/` · `/shorts/`)
/// ซึ่ง **ฝังใน iframe ไม่ได้** — ถ้าไม่แปลงให้ ผู้ดูแลจะเห็นกรอบว่างแล้วเข้าใจว่า
/// ระบบพัง ทั้งที่ลิงก์ถูกต้องในมุมของเขา
///
/// เทสต์ชุดนี้ยังล็อกอีกอย่างที่สำคัญพอกัน: **ลิงก์ที่ฝังไม่ได้ต้องคืน null**
/// (ไม่ใช่เดา URL ขึ้นมาแล้วได้กรอบว่าง) เพื่อให้ UI รู้ว่าต้องวาดปุ่มเปิดแท็บใหม่แทน
/// </summary>
public class HelpMediaEmbedTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?feature=share&v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ")]
    public void ยูทูบทุกรูปแบบลิงก์แปลงเป็น_embed_ได้(string url)
    {
        Assert.Equal(HelpMediaProvider.YouTube, HelpMediaEmbed.DetectProvider(url));
        Assert.Equal("https://www.youtube.com/embed/dQw4w9WgXcQ",
            HelpMediaEmbed.ToEmbedUrl(url, HelpMediaProvider.YouTube));
    }

    /// <summary>`watch?v=…&t=90` — พารามิเตอร์เวลาของหน้า watch ใช้กับ /embed ไม่ได้
    /// ตรง ๆ จึงต้องไม่ถูกส่งต่อ (ไม่งั้นได้ URL ที่ YouTube ปฏิเสธ)</summary>
    [Fact]
    public void ยูทูบ_ตัดพารามิเตอร์ของหน้า_watch_ทิ้ง()
    {
        var e = HelpMediaEmbed.ToEmbedUrl("https://www.youtube.com/watch?v=abc12345678&t=90s", HelpMediaProvider.YouTube);
        Assert.Equal("https://www.youtube.com/embed/abc12345678", e);
        Assert.DoesNotContain("t=", e);
    }

    [Fact]
    public void ติ๊กต็อกแปลงจากลิงก์วิดีโอเต็ม()
    {
        const string url = "https://www.tiktok.com/@nextacc/video/7301234567890123456";
        Assert.Equal(HelpMediaProvider.TikTok, HelpMediaEmbed.DetectProvider(url));
        Assert.Equal("https://www.tiktok.com/embed/v2/7301234567890123456",
            HelpMediaEmbed.ToEmbedUrl(url, HelpMediaProvider.TikTok));
    }

    /// <summary>Facebook ต้องห่อผ่าน plugin endpoint และ **encode URL เดิมทั้งอัน** —
    /// ถ้าไม่ encode พารามิเตอร์ของลิงก์เดิมจะไปปนกับพารามิเตอร์ของ plugin</summary>
    [Fact]
    public void เฟซบุ๊กห่อผ่าน_plugin_และ_encode_ลิงก์เดิม()
    {
        var e = HelpMediaEmbed.ToEmbedUrl("https://www.facebook.com/nextacc/videos/123?x=1", HelpMediaProvider.Facebook);
        Assert.StartsWith("https://www.facebook.com/plugins/video.php?href=", e);
        Assert.Contains("%3A%2F%2F", e);        // :// ถูก encode แล้ว
        Assert.DoesNotContain("?x=1", e);       // พารามิเตอร์เดิมไม่หลุดออกมาเป็นของ plugin
    }

    [Fact]
    public void ไฟล์ที่อัปโหลดเองคืนพาธตรง_ไม่ใช่_iframe()
    {
        Assert.Equal("/uploads/help-media/abc.mp4",
            HelpMediaEmbed.ToEmbedUrl("/uploads/help-media/abc.mp4", HelpMediaProvider.SelfHosted));
        Assert.Equal(HelpMediaProvider.SelfHosted, HelpMediaEmbed.DetectProvider("/uploads/help-media/abc.mp4"));
    }

    /// <summary>**ฝังไม่ได้ = ต้องคืน null** ไม่ใช่เดา URL ขึ้นมา —
    /// null คือสัญญาณให้ UI วาดปุ่ม "เปิดในแท็บใหม่" แทนกรอบว่าง (ห้ามเงียบ)</summary>
    [Theory]
    [InlineData("https://example.com/some-article", HelpMediaProvider.ExternalLink)]
    [InlineData("https://www.youtube.com/@channel", HelpMediaProvider.YouTube)]      // หน้าช่อง ไม่ใช่วิดีโอ
    [InlineData("https://www.tiktok.com/@user", HelpMediaProvider.TikTok)]           // โปรไฟล์ ไม่ใช่วิดีโอ
    [InlineData("https://evil.example/uploads/x.mp4", HelpMediaProvider.SelfHosted)] // ไม่ใช่ไฟล์ของเรา
    public void ลิงก์ที่ฝังไม่ได้ต้องคืน_null(string url, HelpMediaProvider provider)
        => Assert.Null(HelpMediaEmbed.ToEmbedUrl(url, provider));

    [Fact]
    public void ค่าว่างไม่ทำให้ระเบิด()
    {
        Assert.Null(HelpMediaEmbed.ToEmbedUrl(null, HelpMediaProvider.YouTube));
        Assert.Null(HelpMediaEmbed.ToEmbedUrl("   ", HelpMediaProvider.YouTube));
        Assert.Equal(HelpMediaProvider.SelfHosted, HelpMediaEmbed.DetectProvider(null));
    }

    [Fact]
    public void ภาพปกเดาได้เฉพาะยูทูบ_เจ้าอื่นคืน_null_ห้ามเดาเป็นภาพของเจ้าอื่น()
    {
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg",
            HelpMediaEmbed.ThumbnailUrl("https://youtu.be/dQw4w9WgXcQ", HelpMediaProvider.YouTube));
        Assert.Null(HelpMediaEmbed.ThumbnailUrl("https://www.tiktok.com/@a/video/123", HelpMediaProvider.TikTok));
        Assert.Null(HelpMediaEmbed.ThumbnailUrl("/uploads/help-media/a.mp4", HelpMediaProvider.SelfHosted));
    }

    [Theory]
    [InlineData("https://fb.watch/abc123/", HelpMediaProvider.Facebook)]
    [InlineData("https://www.facebook.com/x/videos/1", HelpMediaProvider.Facebook)]
    [InlineData("https://vimeo.com/12345", HelpMediaProvider.ExternalLink)]
    public void เดาผู้ให้บริการจาก_URL(string url, HelpMediaProvider expected)
        => Assert.Equal(expected, HelpMediaEmbed.DetectProvider(url));
}
