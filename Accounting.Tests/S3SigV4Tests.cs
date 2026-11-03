using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ลายเซ็น SigV4 ของ <see cref="S3SigV4"/> ต้องตรง<b>เวกเตอร์ทางการของ AWS</b> (S3 API Reference — "Examples: Signature Calculations
/// in AWS Signature Version 4": GET Object และ PUT Object · คีย์ตัวอย่าง AKIAIOSFODNN7EXAMPLE) — ล็อกค่าคงที่จริง ไม่ใช่ "เซ็นสองครั้งได้เท่ากัน"
/// (ตรวจค่าด้วย Python hmac/hashlib แยกอีกทางตอนเขียน — ทีม FS 2026-11-02)
/// </summary>
public class S3SigV4Tests
{
    private const string Secret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string Date = "20130524T000000Z";

    [Fact]
    public void GetObject_ตรงเวกเตอร์AWS()
    {
        var headers = new Dictionary<string, string>
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = S3SigV4.EmptyPayloadHash,
            ["x-amz-date"] = Date,
        };
        var sig = S3SigV4.Signature("GET", "/test.txt", "", headers, S3SigV4.EmptyPayloadHash, Date, "us-east-1", Secret);
        Assert.Equal("f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", sig);
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, Signature=" + sig,
            S3SigV4.Authorization("AKIAIOSFODNN7EXAMPLE", Date, "us-east-1", headers.Keys, sig));
    }

    [Fact]
    public void PutObject_ตรงเวกเตอร์AWS_และhashของpayload()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("Welcome to Amazon S3.");
        var payloadHash = S3SigV4.Sha256Hex(body);
        Assert.Equal("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072", payloadHash);
        var headers = new Dictionary<string, string>
        {
            ["date"] = "Fri, 24 May 2013 00:00:00 GMT",
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = Date,
            ["x-amz-storage-class"] = "REDUCED_REDUNDANCY",
        };
        // "$" ใน key ต้องเข้ารหัสเป็น %24 (canonical URI ของเวกเตอร์คือ /test%24file.text)
        var uri = "/" + S3SigV4.EncodeKeyPath("test$file.text");
        Assert.Equal("/test%24file.text", uri);
        Assert.Equal("98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd",
            S3SigV4.Signature("PUT", uri, "", headers, payloadHash, Date, "us-east-1", Secret));
    }

    [Fact]
    public void ลายเซ็นเปลี่ยนเมื่อpayloadเปลี่ยน_กันเซ็นแบบไม่ผูกเนื้อไฟล์()
    {
        var h = new Dictionary<string, string> { ["host"] = "h", ["x-amz-date"] = Date, ["x-amz-content-sha256"] = "a" };
        var a = S3SigV4.Signature("PUT", "/b/k", "", h, "a", Date, "auto", Secret);
        h["x-amz-content-sha256"] = "b";
        Assert.NotEqual(a, S3SigV4.Signature("PUT", "/b/k", "", h, "b", Date, "auto", Secret));
    }

    [Fact]
    public void EncodeKeyPath_คงสแลช_เข้ารหัสไทยและช่องว่างเป็นUTF8ตัวใหญ่()
    {
        Assert.Equal("web/uploads/a%20b.png", S3SigV4.EncodeKeyPath("web/uploads/a b.png"));
        Assert.Equal("attachments/%E0%B8%81.pdf", S3SigV4.EncodeKeyPath("attachments/ก.pdf"));
        Assert.Equal("a-b_c.d~e", S3SigV4.EncodeKeyPath("a-b_c.d~e"));
    }
}
