using System.Net;
using Accounting.Helpers;
using Microsoft.AspNetCore.StaticFiles;

namespace Accounting.Services.Storage;

/// <summary>ค่าตั้งของ S3-compatible storage (<c>FileStorage:S3:*</c> · env <c>FileStorage__S3__*</c>) — ผ่าน <see cref="FileStorageConfigPolicy"/> แล้ว</summary>
public sealed record S3StorageSettings(
    Uri Endpoint, string Bucket, string Region, string AccessKeyId, string SecretAccessKey,
    bool ForcePathStyle, string KeyPrefix, bool LocalReadFallback)
{
    /// <summary>ไม่พิมพ์ SecretAccessKey (record สังเคราะห์ ToString ที่รวมทุกช่อง — ห้ามหลุดลง log · ฝ่ายค้าน S6)</summary>
    public override string ToString()
        => $"S3StorageSettings {{ Endpoint = {Endpoint}, Bucket = {Bucket}, Region = {Region}, AccessKeyId = {AccessKeyId}, SecretAccessKey = ***, ForcePathStyle = {ForcePathStyle}, KeyPrefix = {KeyPrefix}, LocalReadFallback = {LocalReadFallback} }}";
}

/// <summary>ผลการขอ object เพื่อเสิร์ฟให้เบราว์เซอร์ (รองรับ Range/conditional) — ผู้เรียก dispose</summary>
public sealed class StorageServeResult : IDisposable
{
    public required int StatusCode { get; init; }
    public Stream? Body { get; init; }
    public long? ContentLength { get; init; }
    public string? ContentRange { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    internal HttpResponseMessage? Response { get; init; }
    public void Dispose() { Body?.Dispose(); Response?.Dispose(); }
}

/// <summary>ที่เก็บที่ส่งต่อ Range/If-None-Match ถึงปลายทางได้ (วิดีโอศูนย์ช่วยเหลือ seek ได้ · ไม่ดึงทั้งไฟล์) — S3 เท่านั้น</summary>
public interface IServableObjectStore
{
    /// <summary>null = ไม่มี object (404)</summary>
    Task<StorageServeResult?> ServeAsync(string location, string? range, string? ifNoneMatch, CancellationToken ct = default);
}

/// <summary>
/// ผู้ให้บริการ object storage แบบ S3 (AWS S3 · Cloudflare R2 · MinIO · DigitalOcean Spaces) — ลายเซ็น SigV4 จาก <see cref="S3SigV4"/> ตัวเดียว
///
/// <para><b>อ่านถอยไปดิสก์ (LocalReadFallback · ค่าเริ่มต้นเปิด)</b>: object ไม่มี (404) ⇒ ลองดิสก์ของเครื่องนี้ที่ตำแหน่งเดิม — ช่วงเปลี่ยนผ่าน
/// (ตั้งเป็น S3 แล้ว แต่ยังไม่ได้รันเครื่องมือย้ายไฟล์) ไฟล์เก่ายังเปิดได้ · เขียนไป S3 อย่างเดียวเสมอ · ลบ = ลบทั้งสองที่
/// (ผู้ใช้สั่งลบไฟล์ — ห้ามเหลือสำเนาบนดิสก์ที่ "ลบแล้ว" แต่ยังอยู่) · ปิดได้เมื่อย้ายครบแล้ว (<c>FileStorage:S3:LocalReadFallback=false</c>)</para>
///
/// <para>ตอบผิดปกติ (403/5xx/เครือข่าย) ⇒ <see cref="FileStorageException"/> ไม่ใช่ "ไม่พบไฟล์" · หมายเหตุ AWS: ถ้าคีย์ไม่มีสิทธิ์
/// <c>s3:ListBucket</c> AWS จะตอบ object ที่ไม่มีเป็น 403 แทน 404 ⇒ ต้องให้สิทธิ์นั้น (ดู FILE_STORAGE_AUDIT.md §deploy)</para>
/// </summary>
public sealed class S3FileStorage : IFileStorage, IRemoteObjectStore, IServableObjectStore
{
    public const string HttpClientName = "FileStorageS3";
    /// <summary>metadata ที่เก็บ SHA-256 ของเนื้อไฟล์ — เครื่องมือย้ายไฟล์ใช้ข้ามไฟล์ที่ย้ายแล้วโดยไม่ต้องดาวน์โหลด</summary>
    public const string Sha256MetaHeader = "x-amz-meta-sha256";
    /// <summary>เพดานเวลาต่อคำขอ (HttpClient ตั้ง Infinite แล้วคุมที่นี่) — PUT ไฟล์ใหญ่ได้นานกว่า (ฝ่ายค้าน S5)</summary>
    internal static readonly TimeSpan PutTimeout = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly IHttpClientFactory _httpFactory;
    private readonly S3StorageSettings _s;
    private readonly LocalDiskFileStorage _local;
    private readonly ILogger<S3FileStorage> _logger;

    public S3FileStorage(IHttpClientFactory httpFactory, S3StorageSettings settings, LocalDiskFileStorage local, ILogger<S3FileStorage> logger)
    {
        _httpFactory = httpFactory;
        _s = settings;
        _local = local;
        _logger = logger;
    }

    public string ProviderName => FileStorageConfigPolicy.ProviderS3;
    public bool IsLocal => false;
    public FileStorageLocator Locator => _local.Locator;
    public string Describe => $"{_s.Endpoint.GetLeftPart(UriPartial.Authority)} bucket={_s.Bucket}{(_s.KeyPrefix.Length > 0 ? " prefix=" + _s.KeyPrefix : "")}";

    // ───────────── IFileStorage ─────────────

    public Task WriteAsync(string location, byte[] content, string? contentType = null, CancellationToken ct = default)
        => PutSegmentAsync(RequireKey(location), new ArraySegment<byte>(content), contentType ?? GuessContentType(location), ct);

    public async Task WriteAsync(string location, Stream content, string? contentType = null, CancellationToken ct = default)
    {
        // SigV4 เซ็น SHA-256 ของเนื้อไฟล์ ⇒ ต้องรู้ไบต์ทั้งก้อนก่อนส่ง (ไม่เขียนไฟล์ชั่วคราวลงดิสก์ — นั่นคือสิ่งที่ abstraction นี้มาแทน) ·
        // จองบัฟเฟอร์ตามความยาวที่รู้ + ส่ง segment ของบัฟเฟอร์ตรง (ไม่ ToArray ซ้ำอีกก้อน · ฝ่ายค้าน S5)
        var key = RequireKey(location);
        long? known = null;
        try { if (content.CanSeek) known = content.Length - content.Position; } catch (NotSupportedException) { }
        using var ms = known is > 0 and < int.MaxValue ? new MemoryStream((int)known.Value) : new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var seg = ms.TryGetBuffer(out var buf) ? buf : new ArraySegment<byte>(ms.ToArray());
        await PutSegmentAsync(key, seg, contentType ?? GuessContentType(location), ct);
    }

    public async Task<byte[]?> ReadAllBytesAsync(string? location, CancellationToken ct = default)
    {
        var key = Locator.ToKey(location);
        if (key != null && await GetAsync(key, ct) is { } bytes) return bytes;
        return _s.LocalReadFallback ? await _local.ReadAllBytesAsync(location, ct) : null;
    }

    public byte[]? ReadAllBytes(string? location)
    {
        var key = Locator.ToKey(location);
        if (key != null)
        {
            using var resp = SendSync(HttpMethod.Get, key, null, null);
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                using var ms = new MemoryStream();
                resp.Content.ReadAsStream().CopyTo(ms);
                return ms.ToArray();
            }
            EnsureNotFound(resp, "GET", key);
        }
        return _s.LocalReadFallback ? _local.ReadAllBytes(location) : null;
    }

    public async Task<Stream?> OpenReadAsync(string? location, CancellationToken ct = default)
    {
        var key = Locator.ToKey(location);
        if (key != null)
        {
            var resp = await SendAsync(HttpMethod.Get, key, null, null, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == HttpStatusCode.OK)
                return new ResponseOwningStream(await resp.Content.ReadAsStreamAsync(ct), resp);
            using (resp) EnsureNotFound(resp, "GET", key);
        }
        return _s.LocalReadFallback ? await _local.OpenReadAsync(location, ct) : null;
    }

    public async Task<bool> ExistsAsync(string? location, CancellationToken ct = default)
    {
        var key = Locator.ToKey(location);
        if (key != null && await HeadAsync(key, ct) != null) return true;
        return _s.LocalReadFallback && _local.Exists(location);
    }

    public bool Exists(string? location)
    {
        var key = Locator.ToKey(location);
        if (key != null)
        {
            using var resp = SendSync(HttpMethod.Head, key, null, null);
            if (resp.IsSuccessStatusCode) return true;
            EnsureNotFound(resp, "HEAD", key);
        }
        return _s.LocalReadFallback && _local.Exists(location);
    }

    public async Task<bool> DeleteAsync(string? location, CancellationToken ct = default)
    {
        var deleted = false;
        var key = Locator.ToKey(location);
        if (key != null)
        {
            var existed = await HeadAsync(key, ct) != null;
            using var resp = await SendAsync(HttpMethod.Delete, key, null, null, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
                throw Fail(resp, "DELETE", key);
            deleted = existed;
        }
        // สำเนาเดิมบนดิสก์ (ก่อนย้าย) — ผู้ใช้สั่งลบแล้วต้องไม่เหลือ (ระยะเก็บรักษาตัดสินโดยผู้เรียกก่อนมาถึงตรงนี้)
        if (await _local.DeleteAsync(location, ct)) deleted = true;
        return deleted;
    }

    // ───────────── IRemoteObjectStore (ไม่ถอยไปดิสก์) ─────────────

    public async Task<RemoteObjectInfo?> HeadAsync(string key, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Head, key, null, null, HttpCompletionOption.ResponseContentRead, ct);
        if (resp.IsSuccessStatusCode)
        {
            var size = resp.Content.Headers.ContentLength ?? 0;
            var sha = resp.Headers.TryGetValues(Sha256MetaHeader, out var v) ? v.FirstOrDefault() : null;
            return new RemoteObjectInfo(size, sha);
        }
        EnsureNotFound(resp, "HEAD", key);
        return null;
    }

    public Task PutAsync(string key, byte[] content, string? contentType, CancellationToken ct = default)
        => PutSegmentAsync(key, new ArraySegment<byte>(content), contentType, ct);

    private async Task PutSegmentAsync(string key, ArraySegment<byte> content, string? contentType, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Put, key, content, contentType ?? "application/octet-stream",
            HttpCompletionOption.ResponseContentRead, ct);
        if (!resp.IsSuccessStatusCode) throw Fail(resp, "PUT", key);
    }

    // ───────────── IServableObjectStore ─────────────

    public async Task<StorageServeResult?> ServeAsync(string location, string? range, string? ifNoneMatch, CancellationToken ct = default)
    {
        var key = Locator.ToKey(location);
        if (key == null) return null;
        var extra = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(range)) extra.Add(("Range", range));
        if (!string.IsNullOrWhiteSpace(ifNoneMatch)) extra.Add(("If-None-Match", ifNoneMatch));
        var resp = await SendAsync(HttpMethod.Get, key, null, null, HttpCompletionOption.ResponseHeadersRead, ct, extra);
        var status = (int)resp.StatusCode;
        if (status is 200 or 206 or 304 or 416)
        {
            var c = resp.Content.Headers;
            return new StorageServeResult
            {
                StatusCode = status,
                Body = status is 200 or 206 ? await resp.Content.ReadAsStreamAsync(ct) : null,
                ContentLength = c.ContentLength,
                ContentRange = c.ContentRange?.ToString(),
                ETag = resp.Headers.ETag?.ToString(),
                LastModified = c.LastModified,
                Response = resp,
            };
        }
        using (resp) EnsureNotFound(resp, "GET", key);
        return null;
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Get, key, null, null, HttpCompletionOption.ResponseContentRead, ct);
        if (resp.StatusCode == HttpStatusCode.OK) return await resp.Content.ReadAsByteArrayAsync(ct);
        EnsureNotFound(resp, "GET", key);
        return null;
    }

    // ───────────── HTTP + SigV4 ─────────────

    private string RequireKey(string location)
        => Locator.ToKey(location) ?? throw new FileStorageException(
            $"ตำแหน่งไฟล์ \"{location}\" อยู่นอกรากที่เก็บไฟล์ — แปลงเป็น object key ไม่ได้ (ประกอบพาธผ่าน FileStorageLocator)");

    private static string GuessContentType(string location)
        => ContentTypes.TryGetContentType(location, out var t) ? t : "application/octet-stream";

    /// <summary>คำขอที่เซ็นแล้ว — path-style (<c>{endpoint}/{bucket}/{key}</c>) เป็นค่าเริ่มต้นเพราะใช้ได้ทุกเจ้า (MinIO บังคับ)</summary>
    internal HttpRequestMessage BuildRequest(HttpMethod method, string key, ArraySegment<byte>? body, string? contentType, DateTime utcNow,
        IReadOnlyList<(string Name, string Value)>? unsignedHeaders = null)
    {
        var objectKey = _s.KeyPrefix + key;
        var encodedKey = S3SigV4.EncodeKeyPath(objectKey);
        var scheme = _s.Endpoint.Scheme;
        var authority = _s.Endpoint.Authority; // ตัดพอร์ตมาตรฐานออกเอง — ตรงกับ Host ที่ HttpClient ส่ง
        string host, canonicalUri;
        if (_s.ForcePathStyle) { host = authority; canonicalUri = "/" + S3SigV4.UriEncode(_s.Bucket) + "/" + encodedKey; }
        else { host = _s.Bucket + "." + authority; canonicalUri = "/" + encodedKey; }
        var uri = new Uri($"{scheme}://{host}{canonicalUri}", new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });

        var amzDate = S3SigV4.AmzDate(utcNow);
        var payloadHash = body is { } b ? S3SigV4.Sha256Hex(b.AsSpan()) : S3SigV4.EmptyPayloadHash;
        var signed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        if (body != null) signed[Sha256MetaHeader] = payloadHash;
        var signature = S3SigV4.Signature(method.Method, canonicalUri, "", signed, payloadHash, amzDate, _s.Region, _s.SecretAccessKey);

        var req = new HttpRequestMessage(method, uri);
        foreach (var (name, value) in signed)
            if (name != "host") req.Headers.TryAddWithoutValidation(name, value);
        req.Headers.TryAddWithoutValidation("Authorization", S3SigV4.Authorization(_s.AccessKeyId, amzDate, _s.Region, signed.Keys, signature));
        if (unsignedHeaders != null)
            foreach (var (name, value) in unsignedHeaders) req.Headers.TryAddWithoutValidation(name, value);
        if (body is { } seg)
        {
            req.Content = new ByteArrayContent(seg.Array!, seg.Offset, seg.Count);
            req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType ?? "application/octet-stream");
        }
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string key, ArraySegment<byte>? body, string? contentType,
        HttpCompletionOption completion, CancellationToken ct, IReadOnlyList<(string, string)>? unsignedHeaders = null)
    {
        using var req = BuildRequest(method, key, body, contentType, DateTime.UtcNow, unsignedHeaders);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(method == HttpMethod.Put ? PutTimeout : DefaultTimeout);
        try { return await _httpFactory.CreateClient(HttpClientName).SendAsync(req, completion, cts.Token); }
        catch (HttpRequestException ex) { throw new FileStorageException($"ที่เก็บไฟล์ ({Describe}) ติดต่อไม่ได้: {ex.Message}", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new FileStorageException($"ที่เก็บไฟล์ ({Describe}) ไม่ตอบภายในเวลา", ex); }
    }

    private HttpResponseMessage SendSync(HttpMethod method, string key, ArraySegment<byte>? body, string? contentType)
    {
        using var req = BuildRequest(method, key, body, contentType, DateTime.UtcNow);
        using var cts = new CancellationTokenSource(DefaultTimeout);
        try { return _httpFactory.CreateClient(HttpClientName).Send(req, HttpCompletionOption.ResponseContentRead, cts.Token); }
        catch (HttpRequestException ex) { throw new FileStorageException($"ที่เก็บไฟล์ ({Describe}) ติดต่อไม่ได้: {ex.Message}", ex); }
        catch (TaskCanceledException ex) { throw new FileStorageException($"ที่เก็บไฟล์ ({Describe}) ไม่ตอบภายในเวลา", ex); }
    }

    /// <summary>404 = ไม่มี (คืนให้ผู้เรียกตัดสิน) · อย่างอื่นที่ไม่ใช่ 2xx = ล้มดัง</summary>
    private void EnsureNotFound(HttpResponseMessage resp, string op, string key)
    {
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        throw Fail(resp, op, key);
    }

    private FileStorageException Fail(HttpResponseMessage resp, string op, string key)
    {
        _logger.LogError("S3 {Op} {Key} ตอบ {Status} ({Target})", op, key, (int)resp.StatusCode, Describe);
        return new FileStorageException($"ที่เก็บไฟล์ ({Describe}) ตอบ {(int)resp.StatusCode} {resp.ReasonPhrase} ตอน {op} — ตรวจ bucket/สิทธิ์ของคีย์");
    }

    /// <summary>สตรีมที่ปิด HTTP response เมื่อผู้อ่าน dispose (ไม่ดึงทั้งไฟล์เข้าหน่วยความจำ — วิดีโอศูนย์ช่วยเหลือ)</summary>
    private sealed class ResponseOwningStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _resp;
        public ResponseOwningStream(Stream inner, HttpResponseMessage resp) { _inner = inner; _resp = resp; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _resp.Content.Headers.ContentLength ?? throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); _resp.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
