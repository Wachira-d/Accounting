using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// นักเรียนของ <see cref="AiFeatureKey.OcrFullReview"/> — "ตรวจผลสแกนทั้งใบ"
///
/// ═══ ทำไมต้องมี (ผลตรวจสถาปัตยกรรม AI) ═══
/// CLAUDE.md กฎเหล็ก #1 ระบุชื่อคลาสนี้ไว้ตรง ๆ ว่าต้องมี แต่ Program.cs กลับ
/// <b>ยกเว้น OcrFullReview ออกจากการ register</b> โดยให้เหตุผลว่า "single-answer
/// model ผิดรูปกับ output แบบ structured" ⇒ ผลคือ:
/// <list type="bullet">
/// <item>ปิด provider ทุกตัว → <c>ReviewOcrAsync</c> คืน Fallback(null) →
///   หน้าเว็บขึ้น toast "AI ไม่ตอบ" แล้ว feature <b>ตายเงียบ</b> ซึ่งเป็น
///   failure mode ที่กฎเหล็ก #1 ห้ามไว้ตรง ๆ (kill-switch test ไม่ผ่าน)</item>
/// <item>ไม่มี student = ไม่มีใครเรียนจากคำตอบครูเลย จ่าย token ฟรีทุกครั้ง</item>
/// </list>
///
/// ═══ วิธีที่ใช้ (ไม่ใช่ single-answer) ═══
/// คำตอบของ feature นี้เป็น JSON ก้อน <c>{corrections, target_document, …}</c>
/// จึงเรียนแบบ <b>รายช่อง</b>: จำว่า "ผู้ขายรายนี้ (+ชนิดกระดาษนี้) เคยถูกแก้
/// เป็นค่าอะไร" แล้วประกอบกลับเป็น JSON รูปเดียวกับที่ครูตอบ
///
/// แหล่งความจริงคือคำแก้ที่ผู้ใช้ยืนยันแล้ว ซึ่งมีอยู่ใน <c>OcrScanResult</c>
/// ที่สร้างเอกสารสำเร็จไปแล้ว — ข้อมูลชุดนี้ถูกสะสมมาตลอดโดยไม่มีใครใช้
///
/// <para><b>Cold-start</b>: tenant ใหม่ที่ยังไม่มีประวัติ → คืน null แล้ว
/// orchestrator ตกไปหาครู; ถ้าครูก็ไม่มี ผู้เรียกยังมีคำตอบของกติกา
/// (SmartFieldExtractor + role inferrer) อยู่แล้วเสมอ — feature ไม่พัง</para>
/// </summary>
public sealed class OcrFullReviewDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.OcrFullReview;
    public string Version { get; private set; } = "v0";

    public bool IsReady
    {
        get { lock (_lock) return _byVendor.Count > 0; }
    }

    private readonly IServiceProvider _services;
    private readonly ILogger<OcrFullReviewDistillationModel> _logger;

    /// <summary>(บริษัท, vendorKey, ชนิดกระดาษ) → สิ่งที่ผู้ใช้ยืนยันครั้งหลังสุด</summary>
    private readonly Dictionary<(Guid CompanyId, string VendorKey, string ScannedType), VendorProfile> _byVendor = new();
    private readonly object _lock = new();

    /// <summary>ต้องเคยเห็นกี่ใบถึงจะเชื่อ — น้อยกว่านี้ถือว่ายังไม่รู้จัก
    /// ผู้ขายรายนี้ดีพอ (คืน null ให้ครูตอบแทน)</summary>
    private const int MinSamples = 2;

    /// <summary>เพดานความมั่นใจ — ต้องต่ำกว่า short-circuit ของ Hybrid (0.85)
    /// เพื่อไม่ให้ student ไปปิดกั้นครูที่ยังทำงานได้ปกติ. student ตัวนี้ทำ
    /// หน้าที่ "ตาข่ายรองตอนครูไม่อยู่" ไม่ใช่ "ตัวแทนครู"</summary>
    private const decimal MaxConfidence = 0.80m;

    private sealed record VendorProfile(
        string? TargetDocumentType,
        string? VendorName,
        string? VendorTaxId,
        string? VendorBranchCode,
        string? ExpenseCategory,
        int Samples);

    public OcrFullReviewDistillationModel(
        IServiceProvider services, ILogger<OcrFullReviewDistillationModel> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

            // ความจริงที่ยืนยันแล้ว = ผลสแกนที่ถูกใช้สร้างเอกสารจริง
            // (ผู้ใช้ตรวจแล้วกดสร้าง = ยอมรับค่าเหล่านี้)
            var rows = await db.Set<OcrScanResult>().AsNoTracking()
                .Where(r => r.CompanyId == companyId && !r.IsDeleted
                    && r.CreatedDocumentId != null
                    && r.ScanStatus == "Completed")
                .OrderByDescending(r => r.CreatedAt)
                .Take(2000)
                .Select(r => new
                {
                    r.ExtractedVendorTaxId, r.ExtractedVendorName, r.VendorBranchCode,
                    r.ScannedDocumentType, r.DocumentType, r.TargetDocumentType, r.ExpenseCategory,
                })
                .ToListAsync(ct);

            var grouped = new Dictionary<(Guid, string, string), VendorProfile>();
            foreach (var r in rows)
            {
                var vKey = VendorKey(r.ExtractedVendorTaxId, r.ExtractedVendorName);
                if (string.IsNullOrEmpty(vKey)) continue;
                var scanned = r.ScannedDocumentType ?? r.DocumentType ?? "";
                var key = (companyId, vKey, scanned);

                if (grouped.TryGetValue(key, out var prev))
                {
                    // แถวเรียงใหม่→เก่า ⇒ ตัวแรกคือล่าสุด เก็บค่าล่าสุดไว้
                    // แล้วนับจำนวนตัวอย่างเพิ่ม
                    grouped[key] = prev with { Samples = prev.Samples + 1 };
                }
                else
                {
                    grouped[key] = new VendorProfile(
                        r.TargetDocumentType, r.ExtractedVendorName, r.ExtractedVendorTaxId,
                        r.VendorBranchCode, r.ExpenseCategory, 1);
                }
            }

            lock (_lock)
            {
                foreach (var k in _byVendor.Keys.Where(k => k.CompanyId == companyId).ToList())
                    _byVendor.Remove(k);
                foreach (var (k, v) in grouped) _byVendor[k] = v;
                Version = $"v{DateTime.UtcNow:yyyyMMddHHmm}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OcrFullReviewDistillationModel: โหลดข้อมูลฝึกไม่สำเร็จ (company {CompanyId})", companyId);
        }
    }

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;

            var vendorName = Str(root, "vendor_name") ?? Str(root, "vendorName");
            var vendorTaxId = Str(root, "vendor_tax_id") ?? Str(root, "vendorTaxId");
            var scannedType = Str(root, "scanned_document_type") ?? Str(root, "document_type") ?? "";

            var vKey = VendorKey(vendorTaxId, vendorName);
            if (string.IsNullOrEmpty(vKey)) return Task.FromResult<LocalPrediction?>(null);

            VendorProfile? hit;
            lock (_lock)
            {
                if (!_byVendor.TryGetValue((companyId, vKey, scannedType), out hit))
                {
                    // ลองแบบไม่ระบุชนิดกระดาษ — ผู้ขายรายเดิมมักออกเอกสารรูปเดียว
                    hit = _byVendor
                        .Where(kv => kv.Key.CompanyId == companyId && kv.Key.VendorKey == vKey)
                        .OrderByDescending(kv => kv.Value.Samples)
                        .Select(kv => kv.Value)
                        .FirstOrDefault();
                }
            }
            if (hit == null || hit.Samples < MinSamples) return Task.FromResult<LocalPrediction?>(null);

            // ประกอบคำตอบให้เป็นรูปเดียวกับที่ครูตอบ — ผู้เรียกฝั่ง
            // AdvancedAiAugmenter parse ด้วย schema เดียวกันทั้งสองทาง
            var answer = JsonSerializer.Serialize(new
            {
                corrections = new
                {
                    vendor_name = hit.VendorName,
                    vendor_tax_id = hit.VendorTaxId,
                    vendor_branch_code = hit.VendorBranchCode,
                    expense_category = hit.ExpenseCategory,
                },
                target_document = hit.TargetDocumentType,
                vendor_canonical = hit.VendorName,
                line_items = Array.Empty<object>(),
                source = "local-distillation",
            });

            // ยิ่งเคยเห็นบ่อยยิ่งมั่นใจ แต่ไม่เกินเพดาน
            var conf = Math.Min(MaxConfidence, 0.5m + 0.05m * hit.Samples);
            return Task.FromResult<LocalPrediction?>(new LocalPrediction(
                answer, conf, Array.Empty<string>(), hit.Samples, Version));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OcrFullReviewDistillationModel: อ่าน input ไม่ได้ — ปล่อยให้ครูตอบ");
            return Task.FromResult<LocalPrediction?>(null);
        }
    }

    /// <summary>คีย์ผู้ขาย — เลขภาษี 13 หลักชนะชื่อเสมอ (ชื่อสะกดต่างได้)
    /// รูปแบบเดียวกับที่ ExpenseCategoryLearner / GlobalDocWorkflowLearner ใช้</summary>
    private static string VendorKey(string? taxId, string? name)
    {
        var digits = Accounting.Helpers.ThaiTaxId.Normalize(taxId);
        if (digits.Length == 13) return $"tax:{digits}";
        return string.IsNullOrWhiteSpace(name) ? "" : $"name:{name.Trim().ToLowerInvariant()}";
    }

    private static string? Str(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
