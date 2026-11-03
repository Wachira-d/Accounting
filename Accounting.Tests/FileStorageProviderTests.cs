using System.Net;
using Accounting.Helpers;
using Accounting.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ผู้ให้บริการที่เก็บไฟล์ (ทีม FS 2026-11-02) — round-trip เขียน→อ่าน→ลบ ของ Local · คำขอ S3 ที่ประกอบจริง (path-style · SigV4 ·
/// metadata hash) · โหมด S3 ถอยไปอ่านดิสก์เฉพาะ 404 (ไม่ใช่ 5xx) · เครื่องมือย้ายไฟล์ตรวจ hash แล้วไม่ลบไฟล์บนดิสก์
/// ("control ที่ไม่มีเทสต์ยืนยัน = ไม่มี control" — กฎเหล็ก #4 G)
/// </summary>
public sealed class FileStorageProviderTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "fs-test-" + Guid.NewGuid().ToString("N"));
    private readonly FileStorageLocator _loc;
    private readonly LocalDiskFileStorage _local;

    public FileStorageProviderTests()
    {
        Directory.CreateDirectory(_base);
        _loc = new FileStorageLocator(Path.Combine(_base, "uploads"), Path.Combine(_base, "wwwroot"), _base, _base);
        _local = new LocalDiskFileStorage(_loc);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

    // ───────────── Local ─────────────

    [Fact]
    public async Task Local_เขียนอ่านลบ_ที่พาธเดิม_พฤติกรรมเดิม()
    {
        var path = Path.Combine(_base, "uploads", "c1", "Document", "a.pdf");
        await _local.WriteAsync(path, new byte[] { 1, 2, 3 });
        Assert.True(File.Exists(path));                       // ไฟล์อยู่ที่พาธเดิมจริง (สร้างโฟลเดอร์ให้เอง)
        Assert.Equal(new byte[] { 1, 2, 3 }, await _local.ReadAllBytesAsync(path));
        Assert.True(await _local.ExistsAsync(path));
        Assert.True(await _local.DeleteAsync(path));
        Assert.False(File.Exists(path));
        Assert.Null(await _local.ReadAllBytesAsync(path));    // ไม่มี = null ไม่ใช่ exception
        Assert.False(await _local.DeleteAsync(path));
    }

    [Fact]
    public async Task Local_พาธสัมพัทธ์_resolveกับworkingDirเดิม()
    {
        var rel = Path.Combine("uploads", "attachments", "c1", "f.txt");
        await using (var s = new MemoryStream(new byte[] { 9 })) await _local.WriteAsync(rel, s);
        Assert.True(File.Exists(Path.Combine(_base, rel)));
        Assert.Equal(new byte[] { 9 }, _local.ReadAllBytes(rel));
    }

    [Fact]
    public async Task Local_เขียนนอกราก_ล้มดัง_ไม่เขียนไฟล์ที่โหมดS3หาไม่เจอ()
    {
        var outside = Path.Combine(Path.GetTempPath(), "fs-outside-" + Guid.NewGuid().ToString("N"), "x.bin");
        await Assert.ThrowsAsync<FileStorageException>(() => _local.WriteAsync(outside, new byte[] { 1 }));
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task FileResult_Local_เป็นPhysicalFile_ไม่มีไฟล์ได้null()
    {
        var path = Path.Combine(_base, "wwwroot", "uploads", "slips", "s.png");
        await _local.WriteAsync(path, new byte[] { 7 });
        var r = await _local.FileResultAsync(path, "image/png", "slip.png");
        var pf = Assert.IsType<Microsoft.AspNetCore.Mvc.PhysicalFileResult>(r);
        Assert.Equal("slip.png", pf.FileDownloadName);
        Assert.Null(await _local.FileResultAsync(path + ".missing", "image/png"));
    }

    // ───────────── S3 (HTTP ปลอม) ─────────────

    private S3FileStorage S3(FakeS3Handler h, bool fallback = true) => new(
        new FakeFactory(h),
        new S3StorageSettings(new Uri("https://acc.r2.cloudflarestorage.com"), "nextacc-files", "auto", "AKIA2REALKEY",
            "q8Zt3vPp0LrX7mNc2YbKs9WdFh4JgTa1UeQiOyR6", ForcePathStyle: true, KeyPrefix: "prod/", LocalReadFallback: fallback),
        _local, NullLogger<S3FileStorage>.Instance);

    [Fact]
    public async Task S3_PUT_pathStyle_keyจากพาธเดิม_เซ็นSigV4_แนบhash()
    {
        var h = new FakeS3Handler();
        var s3 = S3(h);
        var path = Path.Combine(_base, "wwwroot", "uploads", "logos", "c 1.png");
        await s3.WriteAsync(path, new byte[] { 1, 2 });

        var req = Assert.Single(h.Requests);
        Assert.Equal("PUT", req.Method);
        Assert.Equal("https://acc.r2.cloudflarestorage.com/nextacc-files/prod/web/uploads/logos/c%201.png", req.Url);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIA2REALKEY/", req.Authorization);
        Assert.Contains("/auto/s3/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date;x-amz-meta-sha256, Signature=", req.Authorization);
        Assert.Equal(S3SigV4.Sha256Hex(new byte[] { 1, 2 }), req.MetaSha256);
        Assert.Equal("image/png", req.ContentType);
        Assert.False(File.Exists(path));                     // โหมด S3 ไม่เขียนดิสก์
        Assert.Equal(new byte[] { 1, 2 }, await s3.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task S3_404ถอยไปอ่านดิสก์_ช่วงยังไม่ย้าย_ปิดfallbackแล้วได้null()
    {
        var path = Path.Combine(_base, "uploads", "c1", "Document", "old.pdf");
        await _local.WriteAsync(path, new byte[] { 5 });
        Assert.Equal(new byte[] { 5 }, await S3(new FakeS3Handler()).ReadAllBytesAsync(path));
        Assert.True(S3(new FakeS3Handler()).Exists(path));
        Assert.Null(await S3(new FakeS3Handler(), fallback: false).ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task S3_ตอบ500_ล้มดัง_ไม่แกล้งว่าไฟล์หายแล้วถอยไปดิสก์()
    {
        var path = Path.Combine(_base, "uploads", "c1", "Document", "old.pdf");
        await _local.WriteAsync(path, new byte[] { 5 });
        var h = new FakeS3Handler { ForceStatus = HttpStatusCode.InternalServerError };
        await Assert.ThrowsAsync<FileStorageException>(() => S3(h).ReadAllBytesAsync(path));
        await Assert.ThrowsAsync<FileStorageException>(() => S3(h).WriteAsync(path, new byte[] { 1 }));
    }

    [Fact]
    public async Task S3_ลบ_ลบทั้งobjectและสำเนาบนดิสก์()
    {
        var h = new FakeS3Handler();
        var s3 = S3(h);
        var path = Path.Combine(_base, "uploads", "c1", "Document", "a.pdf");
        await s3.WriteAsync(path, new byte[] { 1 });
        await _local.WriteAsync(path, new byte[] { 1 });
        Assert.True(await s3.DeleteAsync(path));
        Assert.Empty(h.Objects);
        Assert.False(File.Exists(path));
        Assert.False(await s3.DeleteAsync(path));
    }

    // ───────────── เครื่องมือย้ายไฟล์ ─────────────

    [Fact]
    public async Task ย้ายไฟล์_dryRunนับอย่างเดียว_ย้ายจริงตรวจhash_รอบสองข้าม_ไม่ลบดิสก์()
    {
        var a = Path.Combine(_base, "uploads", "c1", "Document", "a.pdf");
        var b = Path.Combine(_base, "wwwroot", "uploads", "logos", "b.png");
        var c = Path.Combine(_base, "storage", "etax", "c1", "INV1.xml");
        await _local.WriteAsync(a, new byte[] { 1 });
        await _local.WriteAsync(b, new byte[] { 2, 2 });
        await _local.WriteAsync(c, new byte[] { 3, 3, 3 });
        var remote = new MemRemote();

        var dry = await FileStorageMigrationService.MigrateAsync(_local, remote, dryRun: true, limit: 100, CancellationToken.None);
        Assert.Equal((3, 0, 3, 0), (dry.Scanned, dry.Copied, dry.Remaining, dry.Failed));
        Assert.Empty(remote.Objects);

        // ครบ limit แล้วไม่อ่าน/hash ไฟล์ที่เหลือ — นับเป็น Unchecked (ฝ่ายค้าน S1) · ยังไม่ครบ ⇒ ข้อความห้ามถอด volume
        var run1 = await FileStorageMigrationService.MigrateAsync(_local, remote, dryRun: false, limit: 2, CancellationToken.None);
        Assert.Equal((2, 0, 1, 0), (run1.Copied, run1.Remaining, run1.Unchecked, run1.Failed));
        Assert.False(run1.Complete);
        Assert.Contains("ยังไม่ครบ", run1.Note);
        var run2 = await FileStorageMigrationService.MigrateAsync(_local, remote, dryRun: false, limit: 2, CancellationToken.None);
        Assert.Equal((2, 1, 0, 0, 0), (run2.AlreadyPresent, run2.Copied, run2.Remaining, run2.Unchecked, run2.Failed));
        Assert.True(run2.Complete);
        Assert.Contains("ครบแล้ว", run2.Note);

        Assert.Equal(new byte[] { 1 }, remote.Objects["attachments/c1/Document/a.pdf"]);
        Assert.Equal(new byte[] { 2, 2 }, remote.Objects["web/uploads/logos/b.png"]);
        Assert.Equal(new byte[] { 3, 3, 3 }, remote.Objects["app/storage/etax/c1/INV1.xml"]);
        Assert.True(File.Exists(a) && File.Exists(b) && File.Exists(c));   // ไม่ลบไฟล์บนดิสก์เลย
    }

    [Fact]
    public async Task ย้ายไฟล์_ปลายทางมีไบต์ต่าง_ไม่เขียนทับ_และอ่านกลับไม่ตรง_นับล้ม()
    {
        var a = Path.Combine(_base, "uploads", "c1", "Document", "a.pdf");
        var b = Path.Combine(_base, "uploads", "c1", "Document", "b.pdf");
        await _local.WriteAsync(a, new byte[] { 1 });
        await _local.WriteAsync(b, new byte[] { 2 });
        var remote = new MemRemote();
        remote.Objects["attachments/c1/Document/a.pdf"] = new byte[] { 9 };   // ของคนอื่น (ไม่มี hash ใน metadata)
        remote.CorruptKey = "attachments/c1/Document/b.pdf";                  // PUT แล้วอ่านกลับได้ไบต์เพี้ยน

        var r = await FileStorageMigrationService.MigrateAsync(_local, remote, dryRun: false, limit: 100, CancellationToken.None);
        Assert.Equal((0, 2), (r.Copied, r.Failed));
        Assert.Equal(new byte[] { 9 }, remote.Objects["attachments/c1/Document/a.pdf"]);
        Assert.Contains(r.Failures, f => f.Contains("ไม่เขียนทับ"));
        Assert.Contains(r.Failures, f => f.Contains("SHA-256"));
    }

    [Fact]
    public async Task ย้ายไฟล์_ปลายทางที่ระบบเขียนเองต่างจากดิสก์_เป็นฉบับใหม่กว่า_ไม่ใช่ความล้มเหลว()
    {
        // ฝ่ายค้าน S2: หลังสลับเป็น S3 ระบบเขียนทับ object ที่ key เดียวกัน (มี hash ใน metadata) — ดิสก์คือฉบับเก่า ⇒ ไม่เขียนทับ ไม่นับล้ม
        var same = Path.Combine(_base, "uploads", "c1", "Document", "same.pdf");
        var diff = Path.Combine(_base, "uploads", "c1", "Document", "diff.pdf");
        await _local.WriteAsync(same, new byte[] { 1 });
        await _local.WriteAsync(diff, new byte[] { 1 });
        var remote = new MemRemote();
        remote.SeedSystemWritten("attachments/c1/Document/same.pdf", new byte[] { 2 });       // ขนาดเท่า ไบต์ต่าง
        remote.SeedSystemWritten("attachments/c1/Document/diff.pdf", new byte[] { 2, 2, 2 }); // ขนาดต่าง (ตัดสินได้ไม่ต้องอ่านไฟล์)
        var r = await FileStorageMigrationService.MigrateAsync(_local, remote, dryRun: false, limit: 100, CancellationToken.None);
        Assert.Equal((2, 0, 0), (r.RemoteNewer, r.Failed, r.Copied));
        Assert.Equal(new byte[] { 2 }, remote.Objects["attachments/c1/Document/same.pdf"]);
    }

    [Fact]
    public async Task ย้ายไฟล์_ไม่ส่งไฟล์ความลับ_และแถวในฐานนอกราก_ทำให้ยังไม่ครบ()
    {
        await _local.WriteAsync(Path.Combine(_base, "storage", "etax", "cert.pfx"), new byte[] { 1 });
        await _local.WriteAsync(Path.Combine(_base, "uploads", "c1", "a.pdf"), new byte[] { 1 });
        var remote = new MemRemote();
        var files = await FileStorageMigrationService.MigrateAsync(_local, remote, dryRun: false, limit: 100, CancellationToken.None);
        Assert.Equal((1, 1), (files.Skipped, files.Copied));
        Assert.DoesNotContain(remote.Objects.Keys, k => k.EndsWith(".pfx"));
        Assert.True(files.Complete);
        // ทิศตรงข้าม: ไฟล์ครบแต่ฐานมีพาธนอกราก ⇒ ไม่ครบ · Ok=false · ข้อความไม่พูดว่า "ครบแล้ว"
        var withDb = FileStorageMigrationService.WithDb(files, 10, 1, new[] { "x" });
        Assert.False(withDb.Complete);
        Assert.False(withDb.Ok);
        Assert.DoesNotContain("ครบแล้ว", withDb.Note);
    }

    [Fact]
    public void ค่าตั้งS3_ToString_ไม่พิมพ์SecretAccessKey()
    {
        var s = new S3StorageSettings(new Uri("https://x.r2.cloudflarestorage.com"), "b", "auto", "AKIA2REALKEY",
            "q8Zt3vPp0LrX7mNc2YbKs9WdFh4JgTa1UeQiOyR6", true, "", true);
        Assert.DoesNotContain("q8Zt3vPp0LrX7mNc2YbKs9WdFh4JgTa1UeQiOyR6", s.ToString());
        Assert.Contains("SecretAccessKey = ***", s.ToString());
    }

    // ───────────── /uploads middleware (โหมด S3) ─────────────

    private async Task<(int Status, string? ContentRange, byte[] Body, bool NextCalled)> GetUploads(IFileStorage storage, string path, string? range = null)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        if (range != null) ctx.Request.Headers.Range = range;
        var body = new MemoryStream();
        ctx.Response.Body = body;
        var next = false;
        var mw = new FileStorageUploadsMiddleware(_ => { next = true; ctx.Response.StatusCode = 404; return Task.CompletedTask; });
        await mw.InvokeAsync(ctx, storage);
        return (ctx.Response.StatusCode, ctx.Response.Headers.ContentRange.ToString(), body.ToArray(), next);
    }

    [Fact]
    public async Task Uploads_S3_เสิร์ฟจากbucket_Rangeได้206()
    {
        var s3 = S3(new FakeS3Handler(), fallback: false);
        await s3.WriteAsync(Path.Combine(_base, "wwwroot", "uploads", "help-media", "v.mp4"), new byte[] { 1, 2, 3, 4, 5 });
        var full = await GetUploads(s3, "/uploads/help-media/v.mp4");
        Assert.Equal((200, new byte[] { 1, 2, 3, 4, 5 }, false), (full.Status, full.Body, full.NextCalled));
        var part = await GetUploads(s3, "/uploads/help-media/v.mp4", "bytes=1-2");
        Assert.Equal((206, new byte[] { 2, 3 }), (part.Status, part.Body));
        Assert.Equal("bytes 1-2/5", part.ContentRange);
    }

    [Theory]
    [InlineData("/uploads/../appsettings.json")]
    [InlineData("/uploads/logos/..\\..\\appsettings.json")]
    [InlineData("/uploads/logos/x.unknownext")]
    [InlineData("/uploads/logos/missing.png")]
    public async Task Uploads_S3_พาธหลบราก_ชนิดไม่รู้จัก_ไม่มีobject_ส่งต่อไม่เสิร์ฟ(string path)
    {
        await File.WriteAllTextAsync(Path.Combine(_base, "appsettings.json"), "{\"secret\":1}");
        var r = await GetUploads(S3(new FakeS3Handler()), path);
        Assert.True(r.NextCalled);
        Assert.Empty(r.Body);
    }

    [Fact]
    public async Task Uploads_Local_ส่งต่อให้staticHandlerเสมอ()
        => Assert.True((await GetUploads(_local, "/uploads/logos/x.png")).NextCalled);

    [Fact]
    public void Uploads_ตำแหน่งที่ลอง_ทั้งwwwrootและworkingDir_ไม่มีbackslash()
    {
        var c = FileStorageUploadsMiddleware.CandidateLocations(_loc, "/cms/c1/s1/a.png");
        Assert.Equal(new[] { Path.Combine(_base, "wwwroot", "uploads", "cms", "c1", "s1", "a.png"), Path.Combine(_base, "uploads", "cms", "c1", "s1", "a.png") }, c);
        Assert.Empty(FileStorageUploadsMiddleware.CandidateLocations(_loc, "logos\\x.png"));
        Assert.Empty(FileStorageUploadsMiddleware.CandidateLocations(_loc, "../../etc/passwd"));
    }

    [Fact]
    public void โฟลเดอร์ที่ย้าย_ตัดโฟลเดอร์ซ้อน_ไม่เดินทั้งcontentRoot()
    {
        // BasePath ค่าเริ่มต้น = {cwd}/uploads ⇒ ตรงกับ {contentRoot}/uploads — ต้องเหลือตัวเดียว ไม่นับไฟล์ซ้ำ
        var dirs = _loc.MigrationDirectories();
        Assert.Equal(3, dirs.Count);
        Assert.DoesNotContain(Path.GetFullPath(_base), dirs);
    }

    // ───────────── ของปลอม ─────────────

    private sealed class MemRemote : IRemoteObjectStore
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        private readonly Dictionary<string, string> _sha = new();
        public string? CorruptKey { get; set; }
        /// <summary>object ที่ระบบเขียนเอง (มี hash ใน metadata) — จำลองการเขียนทับหลังสลับเป็น S3</summary>
        public void SeedSystemWritten(string key, byte[] content) { Objects[key] = content; _sha[key] = S3SigV4.Sha256Hex(content); }
        public string Describe => "mem";
        public Task<RemoteObjectInfo?> HeadAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Objects.TryGetValue(key, out var b) ? new RemoteObjectInfo(b.LongLength, _sha.GetValueOrDefault(key)) : null);
        public Task PutAsync(string key, byte[] content, string? contentType, CancellationToken ct = default)
        {
            Objects[key] = key == CorruptKey ? content.Concat(new byte[] { 0 }).ToArray() : content;
            _sha[key] = S3SigV4.Sha256Hex(content);
            return Task.CompletedTask;
        }
        public Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Objects.TryGetValue(key, out var b) ? b : null);
    }

    private sealed record Recorded(string Method, string Url, string Authorization, string? MetaSha256, string? ContentType);

    /// <summary>S3 ในหน่วยความจำ — เก็บตาม URL · HEAD/GET ไม่มี = 404</summary>
    private sealed class FakeS3Handler : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = new();
        public Dictionary<string, byte[]> Objects { get; } = new();
        public HttpStatusCode? ForceStatus { get; set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
            => SendAsync(request, ct).GetAwaiter().GetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.OriginalString;
            if (request.Method == HttpMethod.Put)
                Requests.Add(new Recorded("PUT", url, request.Headers.GetValues("Authorization").Single(),
                    request.Headers.TryGetValues(S3FileStorage.Sha256MetaHeader, out var v) ? v.Single() : null,
                    request.Content?.Headers.ContentType?.ToString()));
            if (ForceStatus is { } forced) return new HttpResponseMessage(forced);
            if (request.Method == HttpMethod.Put)
            {
                Objects[url] = await request.Content!.ReadAsByteArrayAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (request.Method == HttpMethod.Delete)
            {
                Objects.Remove(url);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (!Objects.TryGetValue(url, out var body)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Get && request.Headers.Range?.Ranges.FirstOrDefault() is { From: { } from, To: { } to })
            {
                var part = new HttpResponseMessage(HttpStatusCode.PartialContent)
                    { Content = new ByteArrayContent(body.Skip((int)from).Take((int)(to - from + 1)).ToArray()) };
                part.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, body.Length);
                return part;
            }
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.Method == HttpMethod.Head ? Array.Empty<byte>() : body) };
            return resp;
        }
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public FakeFactory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }
}
