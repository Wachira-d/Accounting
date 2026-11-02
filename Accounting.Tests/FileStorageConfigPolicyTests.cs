using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่านค่าตั้งที่เก็บไฟล์ (<see cref="FileStorageConfigPolicy"/> · ทีม FS 2026-11-02) — production ที่ตั้ง S3 แต่คีย์ว่าง/placeholder ต้องไม่ boot
/// (กฎเหล็ก #4 C) · ทิศตรงข้าม: Local ไม่มีค่าบังคับ และค่าจริงของ R2/MinIO/AWS ต้องผ่าน (ด่านที่ฟ้องค่าจริง = ปิดระบบโดยไม่ตั้งใจ)
/// </summary>
public class FileStorageConfigPolicyTests
{
    private const string RealSecret = "q8Zt3vPp0LrX7mNc2YbKs9WdFh4JgTa1UeQiOyR6";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Local")]
    [InlineData("localdisk")]
    public void Local_ไม่มีค่าบังคับ(string? provider)
        => Assert.Empty(FileStorageConfigPolicy.Problems(provider, null, null, null, null, null));

    [Fact]
    public void ผู้ให้บริการไม่รู้จัก_ฟ้อง()
        => Assert.Single(FileStorageConfigPolicy.Problems("azure", null, null, null, null, null));

    [Fact]
    public void S3_ไม่มีคีย์_ฟ้องครบทุกช่อง()
    {
        var p = FileStorageConfigPolicy.Problems("S3", "", "https://acc.r2.cloudflarestorage.com", "auto", "", "");
        Assert.Contains(p, x => x.Contains("Bucket"));
        Assert.Contains(p, x => x.Contains("AccessKeyId"));
        Assert.Contains(p, x => x.Contains("SecretAccessKey"));
    }

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("CHANGE_ME")]
    [InlineData("your-access-key")]
    [InlineData("<access-key>")]
    [InlineData("${S3_KEY}")]
    public void S3_คีย์เป็นplaceholder_ฟ้อง(string key)
        => Assert.Contains(FileStorageConfigPolicy.Problems("S3", "nextacc-files", "https://minio.local:9000", "us-east-1", key, RealSecret),
            x => x.Contains("AccessKeyId"));

    [Fact]
    public void S3_secretสั้นผิดปกติ_ฟ้อง()
        => Assert.Contains(FileStorageConfigPolicy.Problems("S3", "b", "https://minio.local:9000", "us-east-1", "AKIA2REALKEY", "short"),
            x => x.Contains("สั้น"));

    [Fact]
    public void AWS_ไม่ระบุEndpoint_ต้องมีregionจริง()
    {
        Assert.Contains(FileStorageConfigPolicy.Problems("S3", "b", null, "auto", "AKIA2REALKEY", RealSecret), x => x.Contains("Endpoint"));
        Assert.Empty(FileStorageConfigPolicy.Problems("S3", "nextacc-prod", null, "ap-southeast-1", "AKIA2REALKEY", RealSecret));
        Assert.Equal("https://s3.ap-southeast-1.amazonaws.com/", FileStorageConfigPolicy.ResolveEndpoint(null, "ap-southeast-1").ToString());
    }

    [Theory]
    [InlineData("https://0123abcd.r2.cloudflarestorage.com", "auto")]
    [InlineData("http://minio:9000", "us-east-1")]
    [InlineData("https://sgp1.digitaloceanspaces.com", "sgp1")]
    public void ค่าจริงของแต่ละเจ้า_ผ่าน(string endpoint, string region)
        => Assert.Empty(FileStorageConfigPolicy.Problems("s3", "nextacc-files", endpoint, region, "AKIA2REALKEY", RealSecret));

    [Fact]
    public void Endpointไม่ใช่URL_ฟ้อง()
        => Assert.Contains(FileStorageConfigPolicy.Problems("S3", "b", "minio:9000", "us-east-1", "AKIA2REALKEY", RealSecret), x => x.Contains("Endpoint"));
}
