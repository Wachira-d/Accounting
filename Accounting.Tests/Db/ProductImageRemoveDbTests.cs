using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Storage;
using Microsoft.Extensions.FileProviders;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// <c>ProductService.RemoveImageAsync</c> ลบไฟล์ผ่านที่เก็บกลาง (ทีม FS 2026-11-02 · ฝ่ายค้าน S7) — ทั้งสองทิศ:
/// รูปในแกลเลอรีของสินค้านี้ (โฟลเดอร์บริษัทนี้) ⇒ ลบรูป + thumb · URL ที่ไม่อยู่ในแกลเลอรี / โฟลเดอร์บริษัทอื่น / มี <c>..</c> ⇒ ไม่แตะไฟล์
/// (เดิมลบตาม URL ที่ผู้เรียกส่งมาได้ทุกไฟล์)
/// </summary>
[Trait("Category", "Db")]
public sealed class ProductImageRemoveDbTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _base = Path.Combine(Path.GetTempPath(), "fs-prod-" + Guid.NewGuid().ToString("N"));
    private readonly LocalDiskFileStorage _storage;

    public ProductImageRemoveDbTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_base);
        _storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(_base, "uploads"), Path.Combine(_base, "wwwroot"), _base, _base));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

    private string Web(string url) => Path.Combine(_base, "wwwroot", url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task ลบรูปในแกลเลอรีของบริษัทนี้_ลบไฟล์และthumb_ทิศตรงข้ามไม่แตะไฟล์อื่น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัททดสอบรูปสินค้า", TaxId = "0105556000001" };
        var other = new Company { Name = "บริษัทอื่น", TaxId = "0105556000002" };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();

        var mine = $"/uploads/products/{company.Id}/a.jpg";
        var theirs = $"/uploads/products/{other.Id}/b.jpg";
        var escape = $"/uploads/products/{company.Id}/../{other.Id}/b.jpg";
        var product = new Product
        {
            CompanyId = company.Id, Code = "IMG1", Name = "สินค้ามีรูป", ProductType = ProductType.Product, Unit = "ชิ้น",
            // URL ของบริษัทอื่น/ที่มี .. ถูกฉีดเข้าแกลเลอรีได้ทางจัดลำดับรูป — การลบต้องไม่ตามไปลบไฟล์นั้น
            ImageUrlsJson = System.Text.Json.JsonSerializer.Serialize(new[] { mine, theirs, escape }),
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        foreach (var u in new[] { mine, theirs })
        {
            await _storage.WriteAsync(Web(u), new byte[] { 1 });
            await _storage.WriteAsync(Web(u).Replace(".jpg", "_thumb.jpg"), new byte[] { 2 });
        }

        var svc = new ProductService(db, new FakeImages(_storage), new FakeEnv(Path.Combine(_base, "wwwroot")), null!, null!);

        await svc.RemoveImageAsync(company.Id, product.Id, theirs);
        await svc.RemoveImageAsync(company.Id, product.Id, escape);
        await svc.RemoveImageAsync(company.Id, product.Id, $"/uploads/products/{company.Id}/not-in-gallery.jpg");
        Assert.True(File.Exists(Web(theirs)));                                   // ทิศตรงข้าม: ไฟล์บริษัทอื่นอยู่ครบ
        Assert.True(File.Exists(Web(theirs).Replace(".jpg", "_thumb.jpg")));

        await svc.RemoveImageAsync(company.Id, product.Id, mine);
        Assert.False(File.Exists(Web(mine)));                                    // ทิศหลัก: รูปของสินค้านี้ถูกลบจริง
        Assert.False(File.Exists(Web(mine).Replace(".jpg", "_thumb.jpg")));
    }

    private sealed class FakeImages : IImageProcessingService
    {
        public FakeImages(IFileStorage storage) => Storage = storage;
        public IFileStorage Storage { get; }
        public Task<ProcessedImageBytes> ProcessToBytesAsync(Stream input, string contentType, string originalFileName, ImageProfile profile)
            => throw new NotSupportedException();
        public Task<ProcessedImageResult> ProcessAndSaveAsync(Stream input, string contentType, string originalFileName, string absoluteDirectory,
            string webBaseUrl, ImageProfile profile, bool generateThumb = false) => throw new NotSupportedException();
        public bool IsProcessableImage(string contentType) => true;
    }

    private sealed class FakeEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public FakeEnv(string webRoot) { WebRootPath = webRoot; ContentRootPath = Path.GetDirectoryName(webRoot)!; }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Accounting.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Testing";
    }
}
