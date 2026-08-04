using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Services.Ai.Embedding;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// RAG store ของ chatbot — ไม่พึ่ง embedding service ภายนอก (กฎเหล็ก #1:
/// local ต้องยืนเองได้): ใช้ IEmbeddingService (hashing char n-gram, ไทย/
/// อังกฤษผสมได้) + keyword bonus. คุณภาพพอสำหรับ FAQ/คู่มือ; อัปเกรดเป็น
/// ONNX MiniLM ได้โดยสลับ DI ตัวเดียว (ดู OnnxSentenceEmbeddingService).
///
/// ขอบเขตข้อมูล (สำคัญที่สุดของไฟล์นี้):
///   • Public  — เฉพาะ seed FAQ ที่เขียนเพื่อเผยแพร่ + บทความที่ admin ตั้ง
///     audience=Public เอง. **ไฟล์ .md ภายใน (CLAUDE.md/DOCUMENT_FLOW.md)
///     ไม่มีวันเข้า scope นี้** — มี file:line/นโยบายภายในที่ไม่ควรหลุด
///   • Tenant  — DOCUMENT_FLOW.md + TEST_PLAN.md (คู่มือ flow ใช้ตอบ "ระบบ
///     ทำงานยังไง") + snapshot ของบริษัทตัวเอง
///   • Internal — CLAUDE.md/DEVELOPMENT_PHASES.md/CHATBOT_PLAN.md เก็บไว้
///     ให้เครื่องมือภายในเท่านั้น ไม่เสิร์ฟผ่าน chatbot ใด ๆ
/// </summary>
public class KnowledgeBaseService : IKnowledgeBaseService
{
    private readonly AccountingDbContext _db;
    private readonly IEmbeddingService _embed;
    private readonly ILogger<KnowledgeBaseService> _logger;

    // cache ชิ้น global ในหน่วยความจำ — โหลดครั้งแรก/หลัง refresh เท่านั้น
    private static List<CachedChunk>? _globalCache;
    private static readonly object _cacheLock = new();

    private sealed record CachedChunk(Guid Id, string Audience, string Title, string Content, float[] Vector);

    public KnowledgeBaseService(AccountingDbContext db, IEmbeddingService embed,
        ILogger<KnowledgeBaseService> logger)
    {
        _db = db; _embed = embed; _logger = logger;
    }

    // ── audience ต่อไฟล์ — default ปลอดภัยไว้ก่อน (ไม่รู้จัก = Internal) ──
    private static readonly (string File, string Audience)[] SourceFiles =
    {
        ("DOCUMENT_FLOW.md", "Tenant"),
        ("TEST_PLAN.md", "Tenant"),
        ("CLAUDE.md", "Internal"),
        ("DEVELOPMENT_PHASES.md", "Internal"),
        ("CHATBOT_PLAN.md", "Internal"),
    };

    public async Task<int> RefreshGlobalAsync(CancellationToken ct = default)
    {
        var upserted = 0;
        var seenKeys = new HashSet<string>();

        // 1) seed FAQ สาธารณะ (คัดเขียนเพื่อเผยแพร่ — ไม่มี internals)
        foreach (var (key, title, content) in PublicSeedArticles())
        {
            upserted += await UpsertChunkAsync(null, "Manual", key, title, content, "Public", ct) ? 1 : 0;
            seenKeys.Add(key);
        }

        // 2) ไฟล์ .md ของ repo — หั่นตามหัวข้อ "## " (ก้อนใหญ่เกินหั่นซ้ำที่ "### ")
        var root = Directory.GetCurrentDirectory();
        foreach (var (file, audience) in SourceFiles)
        {
            // ไฟล์อยู่ root ของ repo (ขึ้นจาก Accounting/ 1 ชั้น) หรือ cwd ตรง ๆ
            var path = File.Exists(Path.Combine(root, file)) ? Path.Combine(root, file)
                : File.Exists(Path.Combine(root, "..", file)) ? Path.Combine(root, "..", file)
                : null;
            if (path == null) continue;
            string text;
            try { text = await File.ReadAllTextAsync(path, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "อ่าน {File} ไม่ได้ — ข้าม", file); continue; }

            foreach (var (key, title, content) in ChunkMarkdown(file, text))
            {
                upserted += await UpsertChunkAsync(null, "File", key, title, content, audience, ct) ? 1 : 0;
                seenKeys.Add(key);
            }
        }

        // 3) ปิดชิ้น File เดิมที่หายไปจากไฟล์ (หัวข้อถูกลบ/เปลี่ยนชื่อ) —
        //    ชิ้น Manual ที่ admin เพิ่มเอง (นอก seed) ไม่แตะ
        var stale = await _db.KnowledgeChunks
            .Where(k => k.CompanyId == null && k.IsActive && !k.IsDeleted
                && (k.SourceType == "File" || k.SourceKey.StartsWith("seed:")))
            .Select(k => new { k.Id, k.SourceKey })
            .ToListAsync(ct);
        foreach (var s in stale.Where(s => !seenKeys.Contains(s.SourceKey)))
        {
            await _db.KnowledgeChunks.Where(k => k.Id == s.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(k => k.IsActive, false)
                                          .SetProperty(k => k.UpdatedAt, DateTime.UtcNow), ct);
            upserted++;
        }

        InvalidateGlobalCache();
        _logger.LogInformation("KB refresh: {Count} chunks changed", upserted);
        return upserted;
    }

    public async Task<bool> RefreshTenantIfStaleAsync(Guid companyId, TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        var ttl = maxAge ?? TimeSpan.FromHours(6);
        var newest = await _db.KnowledgeChunks.AsNoTracking()
            .Where(k => k.CompanyId == companyId && k.SourceType == "TenantSnapshot" && k.IsActive)
            .MaxAsync(k => (DateTime?)(k.UpdatedAt ?? k.CreatedAt), ct);
        if (newest.HasValue && DateTime.UtcNow - newest.Value < ttl) return false;

        // ── company profile ──
        var co = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.Name, c.BusinessType, c.IsVatRegistered, c.IsSocialSecurityRegistered, c.BranchCode })
            .FirstOrDefaultAsync(ct);
        if (co == null) return false;
        var profile = $"บริษัท: {co.Name}\nประเภทธุรกิจ: {co.BusinessType}\n"
            + $"จดทะเบียน VAT: {(co.IsVatRegistered ? "ใช่ (ต้องยื่น ภ.พ.30 ทุกเดือน แม้ยอดเป็นศูนย์)" : "ไม่ (ห้ามออกใบกำกับภาษี)")}\n"
            + $"ขึ้นทะเบียนประกันสังคม: {(co.IsSocialSecurityRegistered ? "ใช่ (ยื่น สปส.1-10 ทุกเดือน)" : "ไม่")}\n"
            + $"สาขา: {(string.IsNullOrEmpty(co.BranchCode) || co.BranchCode == "00000" ? "สำนักงานใหญ่" : co.BranchCode)}";
        await UpsertChunkAsync(companyId, "TenantSnapshot", "tenant:profile",
            "ข้อมูลกิจการ", profile, "Tenant", ct, forceTouch: true);

        // ── ผังบัญชี (เฉพาะ posting-level, แบ่งตามหมวด 1-5 กันก้อนโต) ──
        var accounts = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted && a.Level >= 4)
            .OrderBy(a => a.AccountCode)
            .Select(a => new { a.AccountCode, a.AccountName })
            .ToListAsync(ct);
        foreach (var grp in accounts.GroupBy(a => a.AccountCode.Length > 0 ? a.AccountCode[0] : '?'))
        {
            var className = grp.Key switch
            {
                '1' => "สินทรัพย์", '2' => "หนี้สิน", '3' => "ทุน",
                '4' => "รายได้", '5' => "ค่าใช้จ่าย", _ => "อื่น ๆ"
            };
            var body = $"ผังบัญชีหมวด{className}ของกิจการ (ใช้เลือกหมวดตอนลงบันทึก):\n"
                + string.Join("\n", grp.Take(120).Select(a => $"{a.AccountCode} {a.AccountName}"));
            await UpsertChunkAsync(companyId, "TenantSnapshot", $"tenant:coa:{grp.Key}",
                $"ผังบัญชี — {className}", body, "Tenant", ct, forceTouch: true);
        }

        // ── ผู้ขายประจำ + ผังที่เคยใช้ (จากประวัติจริง = คำตอบ "หมวดไหนดี") ──
        var vendorGl = await _db.DocumentLines.AsNoTracking()
            .Where(l => l.Document.CompanyId == companyId && !l.Document.IsDeleted
                && l.Document.Status != Models.Enums.DocumentStatus.Draft
                && l.Document.Status != Models.Enums.DocumentStatus.Voided
                && l.AccountId != null && l.Document.Contact != null)
            .GroupBy(l => new { Vendor = l.Document.Contact!.Name, l.Account!.AccountCode, l.Account.AccountName })
            .Select(g => new { g.Key.Vendor, g.Key.AccountCode, g.Key.AccountName, N = g.Count() })
            .OrderByDescending(g => g.N).Take(80)
            .ToListAsync(ct);
        if (vendorGl.Count > 0)
        {
            var body = "ประวัติการลงบัญชีของกิจการ (ผู้ขาย → ผังที่ใช้บ่อย):\n"
                + string.Join("\n", vendorGl.Select(v => $"{v.Vendor} → {v.AccountCode} {v.AccountName} ({v.N} ครั้ง)"));
            await UpsertChunkAsync(companyId, "TenantSnapshot", "tenant:vendor-gl",
                "ผู้ขายประจำ + ผังบัญชีที่เคยใช้", body, "Tenant", ct, forceTouch: true);
        }

        _logger.LogInformation("Tenant KB rebuilt for {CompanyId} ({Coa} accounts, {Vg} vendor mappings)",
            companyId, accounts.Count, vendorGl.Count);
        return true;
    }

    public async Task<List<(KnowledgeChunk Chunk, double Score)>> SearchAsync(
        string query, string audience, Guid? companyId, int topK = 4, CancellationToken ct = default)
    {
        var qv = _embed.Embed(query);
        var results = new List<(KnowledgeChunk, double)>();

        // global — จาก cache (Public เห็นเฉพาะ Public; Tenant เห็น Public+Tenant)
        var allowed = audience == "Public" ? new[] { "Public" } : new[] { "Public", "Tenant" };
        foreach (var c in await GetGlobalCacheAsync(ct))
        {
            if (!allowed.Contains(c.Audience)) continue;
            var score = Cosine(qv, c.Vector) + KeywordBonus(query, c.Title, c.Content);
            if (score > 0.15) results.Add((new KnowledgeChunk { Id = c.Id, Title = c.Title, Content = c.Content, Audience = c.Audience }, score));
        }

        // tenant chunks — โหลดสด (ต่อบริษัท ปริมาณเล็ก)
        if (companyId.HasValue && audience != "Public")
        {
            var tenantChunks = await _db.KnowledgeChunks.AsNoTracking()
                .Where(k => k.CompanyId == companyId && k.IsActive && !k.IsDeleted)
                .ToListAsync(ct);
            foreach (var k in tenantChunks)
            {
                var v = DeserializeVector(k.EmbeddingJson) ?? _embed.Embed(k.Content);
                var score = Cosine(qv, v) + KeywordBonus(query, k.Title, k.Content)
                    + 0.05; // ข้อมูลของบริษัทตัวเองได้แต้มต่อเล็กน้อย — ตรงบริบทกว่าคู่มือกลาง
                if (score > 0.15) results.Add((k, score));
            }
        }

        return results.OrderByDescending(r => r.Item2).Take(topK).ToList();
    }

    // ═══════════════ internals ═══════════════

    private async Task<bool> UpsertChunkAsync(Guid? companyId, string sourceType, string key,
        string title, string content, string audience, CancellationToken ct, bool forceTouch = false)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        var existing = await _db.KnowledgeChunks
            .FirstOrDefaultAsync(k => k.CompanyId == companyId && k.SourceKey == key && !k.IsDeleted, ct);
        if (existing != null && existing.ContentHash == hash && existing.IsActive)
        {
            // เนื้อหาเดิม — tenant snapshot ต้อง touch UpdatedAt ให้ TTL รู้ว่า refresh แล้ว
            if (forceTouch) { existing.UpdatedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct); }
            return false;
        }

        var vector = _embed.Embed(title + "\n" + content);
        if (existing == null)
        {
            _db.KnowledgeChunks.Add(new KnowledgeChunk
            {
                CompanyId = companyId, SourceType = sourceType, SourceKey = key,
                Title = title, Content = content, Audience = audience,
                EmbeddingJson = JsonSerializer.Serialize(vector), ContentHash = hash, IsActive = true,
            });
        }
        else
        {
            existing.Title = title; existing.Content = content; existing.Audience = audience;
            existing.EmbeddingJson = JsonSerializer.Serialize(vector);
            existing.ContentHash = hash; existing.IsActive = true;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>หั่น markdown ตาม "## " — ก้อน > 3500 ตัวอักษรหั่นซ้ำตาม "### "
    /// แล้วยังโตอีกก็ตัดท่อนละ 3500 (กันชิ้นเดียวกิน prompt ทั้งงบ)</summary>
    internal static List<(string Key, string Title, string Content)> ChunkMarkdown(string file, string text)
    {
        var outChunks = new List<(string, string, string)>();
        var sections = SplitByHeading(text, "## ");
        var idx = 0;
        foreach (var (title, body) in sections)
        {
            if (body.Length <= 3500)
            {
                if (body.Trim().Length > 40)
                    outChunks.Add(($"{file}#s{idx++}", $"{file} — {title}", body.Trim()));
                continue;
            }
            foreach (var (subTitle, subBody) in SplitByHeading(body, "### "))
            {
                var trimmed = subBody.Trim();
                if (trimmed.Length <= 40) continue;
                for (var off = 0; off < trimmed.Length; off += 3500)
                    outChunks.Add(($"{file}#s{idx++}",
                        $"{file} — {title} › {subTitle}",
                        trimmed.Substring(off, Math.Min(3500, trimmed.Length - off))));
            }
        }
        return outChunks;
    }

    private static List<(string Title, string Body)> SplitByHeading(string text, string marker)
    {
        var parts = new List<(string, string)>();
        var lines = text.Split('\n');
        var title = "(บทนำ)"; var buf = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith(marker))
            {
                if (buf.Length > 0) parts.Add((title, buf.ToString()));
                title = line[marker.Length..].Trim();
                buf.Clear();
            }
            else buf.AppendLine(line);
        }
        if (buf.Length > 0) parts.Add((title, buf.ToString()));
        return parts;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot; // เวกเตอร์ L2-normalized แล้ว — dot = cosine
    }

    /// <summary>bonus คำตรง — hashing embedding อ่อนเรื่องคำเฉพาะ (เช่น
    /// "ภ.พ.30") การเจอคำถามทั้งคำในชิ้นความรู้จึงมีน้ำหนักเพิ่ม</summary>
    private static double KeywordBonus(string query, string title, string content)
    {
        double bonus = 0;
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (term.Length < 3) continue;
            if (title.Contains(term, StringComparison.OrdinalIgnoreCase)) bonus += 0.10;
            else if (content.Contains(term, StringComparison.OrdinalIgnoreCase)) bonus += 0.05;
        }
        return Math.Min(bonus, 0.35);
    }

    private static float[]? DeserializeVector(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<float[]>(json); }
        catch { return null; }
    }

    private async Task<List<CachedChunk>> GetGlobalCacheAsync(CancellationToken ct)
    {
        if (_globalCache != null) return _globalCache;
        var rows = await _db.KnowledgeChunks.AsNoTracking()
            .Where(k => k.CompanyId == null && k.IsActive && !k.IsDeleted)
            .Select(k => new { k.Id, k.Audience, k.Title, k.Content, k.EmbeddingJson })
            .ToListAsync(ct);
        var cache = rows.Select(r => new CachedChunk(r.Id, r.Audience, r.Title, r.Content,
                DeserializeVector(r.EmbeddingJson) ?? _embed.Embed(r.Content)))
            .ToList();
        lock (_cacheLock) { _globalCache = cache; }
        return cache;
    }

    private static void InvalidateGlobalCache()
    {
        lock (_cacheLock) { _globalCache = null; }
    }

    /// <summary>ความรู้สาธารณะชุดตั้งต้น — เขียนเพื่อเผยแพร่โดยเฉพาะ ทวนจาก
    /// ความสามารถจริงของระบบ (ไม่มี file/line/นโยบายภายใน). admin เพิ่ม/แก้
    /// ผ่านหน้าจัดการ KB ได้ (Phase ถัดไปใน CHATBOT_PLAN.md)</summary>
    private static IEnumerable<(string Key, string Title, string Content)> PublicSeedArticles()
    {
        yield return ("seed:overview", "NextAcc คืออะไร",
            "NextAcc เป็นระบบบัญชีออนไลน์สำหรับธุรกิจไทย ครอบคลุม: ออกเอกสารขาย/ซื้อครบวงจร "
            + "(ใบเสนอราคา ใบแจ้งหนี้ ใบกำกับภาษี ใบเสร็จ ใบลดหนี้/เพิ่มหนี้ ใบสำคัญจ่าย/รับ) "
            + "ลงบัญชีอัตโนมัติ (สมุดรายวัน แยกประเภท งบทดลอง งบการเงิน) ภาษีไทยครบ "
            + "(ภ.พ.30, ภ.ง.ด.1/3/53, หัก ณ ที่จ่าย 50 ทวิ, e-Tax Invoice) เงินเดือน+ประกันสังคม "
            + "สต๊อกสินค้า สินทรัพย์ถาวร-ค่าเสื่อม และ OCR สแกนบิลอัตโนมัติด้วย AI");
        yield return ("seed:ocr", "สแกนบิล/ใบเสร็จอัตโนมัติ (OCR)",
            "ถ่ายรูปหรืออัปโหลดใบเสร็จ/ใบกำกับภาษี ระบบอ่านข้อมูลให้ครบทุกช่อง (ผู้ขาย เลขผู้เสียภาษี "
            + "วันที่ ยอดเงิน VAT รายการสินค้า) แล้วสร้างเอกสารบัญชีให้ทันที ผู้ใช้แค่กดยืนยัน "
            + "ส่งรูปผ่าน LINE ก็ได้ — โยนบิลเข้าแชท ระบบสร้างเอกสารและกดอนุมัติจากในแชทได้เลย "
            + "บิลที่ไม่มีเลขผู้เสียภาษี (บิลเงินสดร้านค้าทั่วไป) ระบบออกใบรับรองแทนใบเสร็จรับเงินให้อัตโนมัติ "
            + "เพื่อให้ใช้เป็นรายจ่ายทางภาษีได้ถูกต้อง");
        yield return ("seed:vat", "ภาษีมูลค่าเพิ่ม (ภ.พ.30)",
            "ระบบจัดทำรายงานภาษีซื้อ-ภาษีขาย และแบบ ภ.พ.30 อัตโนมัติจากเอกสารที่บันทึก "
            + "รองรับใบกำกับภาษีเต็มรูป/อย่างย่อ อัตรา 7%/0%/ยกเว้น เครดิตภาษียกไป "
            + "กรอบเวลาเคลมภาษีซื้อ 6 เดือน ภาษีซื้อต้องห้าม และแจ้งเตือนกำหนดยื่นทุกเดือน "
            + "มีปฏิทินนำส่งภาษีบนแดชบอร์ดบอกว่าเดือนไหนยื่นแล้ว/ยังไม่ยื่น");
        yield return ("seed:wht", "หัก ณ ที่จ่าย + 50 ทวิ",
            "บันทึกหัก ณ ที่จ่ายอัตโนมัติตามประเภทเงินได้ (ค่าบริการ 3%, ค่าเช่า 5%, ขนส่ง 1% ฯลฯ) "
            + "ออกหนังสือรับรอง 50 ทวิให้ทันทีตอนจ่ายเงิน สรุปยอดนำส่ง ภ.ง.ด.1/3/53 รายเดือน "
            + "พร้อมไฟล์ยื่น e-Filing");
        yield return ("seed:payroll", "เงินเดือน + ประกันสังคม",
            "คำนวณเงินเดือน ภาษีเงินได้ (ขั้นบันได + ค่าลดหย่อน) ประกันสังคม 5% (เพดาน 750 บาท) "
            + "กองทุนสำรองเลี้ยงชีพ ออกสลิปเงินเดือน (ส่งผ่าน LINE ได้) ไฟล์ยื่น สปส.1-10 และ ภ.ง.ด.1 "
            + "พร้อมบันทึกการนำส่งและแนบหลักฐานการจ่าย");
        yield return ("seed:etax", "e-Tax Invoice",
            "ออกใบกำกับภาษีอิเล็กทรอนิกส์ (e-Tax Invoice & e-Receipt) ตามมาตรฐาน ETDA "
            + "ส่งกรมสรรพากรอัตโนมัติตามรอบ พร้อมติดตามสถานะการตอบรับ");
        yield return ("seed:stock", "สต๊อกสินค้า + สินทรัพย์",
            "คุมสต๊อกแบบ FIFO/ต้นทุนถัวเฉลี่ย ตัดสต๊อกอัตโนมัติตอนขาย/ซื้อ หลายคลัง/lot "
            + "ทะเบียนสินทรัพย์ถาวร คำนวณค่าเสื่อมรายเดือนอัตโนมัติ (เส้นตรง/ลดยอด) "
            + "OCR ตรวจจับรายการที่ควรขึ้นทะเบียนเป็นสินทรัพย์ให้เอง");
        yield return ("seed:pricing", "แพ็กเกจ / ราคา / ทดลองใช้",
            "มีแพ็กเกจให้เลือกตามขนาดธุรกิจ เริ่มต้นทดลองใช้ฟรีได้ ดูรายละเอียดแพ็กเกจและราคา"
            + "ปัจจุบันได้ที่หน้าแพ็กเกจบนเว็บไซต์ หรือฝากอีเมลไว้ให้ทีมงานติดต่อกลับ");
        yield return ("seed:security", "ความปลอดภัยของข้อมูล",
            "ข้อมูลแยกตามบริษัท (tenant isolation) เข้ารหัสการเชื่อมต่อ TLS สำรองข้อมูลสม่ำเสมอ "
            + "กำหนดสิทธิ์ผู้ใช้หลายระดับ (เจ้าของ นักบัญชี พนักงาน ผู้ตรวจ) มี audit log "
            + "ทุกการแก้ไข และปฏิบัติตาม พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล (PDPA)");
        yield return ("seed:accountant", "ทำงานร่วมกับนักบัญชี/สำนักงานบัญชี",
            "เชิญนักบัญชีภายนอกเข้าดู/ทำงานในกิจการได้ มีหน้ารวมสำหรับสำนักงานบัญชีดูแล"
            + "หลายบริษัท ปิดงวด ตรวจเอกสาร และยื่นภาษีได้จากที่เดียว");
        yield return ("seed:line", "ใช้งานผ่าน LINE",
            "ผูกบัญชีกับ LINE แล้วส่งรูปใบเสร็จเข้าแชทเพื่อบันทึกบัญชีได้ทันที กดอนุมัติเอกสาร"
            + "จากในแชท บันทึกค่าใช้จ่ายด้วยข้อความสั้น ๆ ดูยอดเงินเข้า-ออกประจำเดือน "
            + "และรับสลิปเงินเดือน/เอกสารทาง LINE");
        yield return ("seed:contact", "ติดต่อทีมงาน",
            "พิมพ์ \"ติดต่อเจ้าหน้าที่\" ในแชทนี้ได้เลย เจ้าหน้าที่จะเข้ามาตอบโดยเร็ว "
            + "หรือฝากชื่อ-อีเมลให้ทีมงานติดต่อกลับ");
    }
}
