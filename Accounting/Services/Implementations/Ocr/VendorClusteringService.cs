using Accounting.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// K-means clustering of vendors by spend pattern. Each vendor is
/// represented as a vector over chart-of-accounts buckets (one
/// dimension per debit account they've been booked against). Vendors
/// that cluster together share similar expense profiles.
///
/// Why this matters in the OCR pipeline:
///   • When a brand-new vendor appears (first scan ever), we have no
///     per-vendor history — the only signals are global rules and
///     basket-mined patterns. If we can identify which cluster the new
///     vendor *most resembles* (by name tokens, by amount range, by
///     industry keywords), we can seed predictions from cluster-level
///     averages: "vendors that look like this typically book to 5402".
///
///   • Clustering also surfaces "similar vendor" suggestions in the UI
///     — useful when a user wants to find replacement suppliers or
///     consolidate AP.
///
/// Algorithm:
///   1. Feature vector per vendor = normalized distribution over the
///      top-N debit account codes (top-N = 30 by default).
///   2. Standard K-means with K supplied by the caller (default 8).
///   3. Persist (vendor → cluster id) so scan time can do a single
///      lookup.
/// </summary>
public class VendorClusteringService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<VendorClusteringService> _logger;

    public VendorClusteringService(AccountingDbContext db, ILogger<VendorClusteringService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>สรุปกลุ่มหนึ่งกลุ่ม — ขนาดกลุ่ม + ผังบัญชีเด่นของกลุ่ม
    /// (มิติที่ centroid มีน้ำหนักสูงสุด) ไว้ให้แอดมินอ่านผลได้จริง</summary>
    public record ClusterSummary(int ClusterIndex, int Size, IReadOnlyList<string> TopAccountCodes);

    /// <param name="Persisted">ผลถูกบันทึกลงฐานหรือไม่ — ปัจจุบัน <b>false</b> เสมอ
    /// (ยังไม่มีคอลัมน์เก็บ cluster). ต้องส่งออกไปให้ผู้เรียกรู้ เพราะเดิม
    /// endpoint แอดมินขึ้นข้อความ "จัดกลุ่มสำเร็จ (4,812 vendors)" ทั้งที่
    /// **ไม่ได้เก็บอะไรไว้เลย** และไม่ได้คืน assignment ให้ใครด้วย
    /// = silent no-op ที่รายงานว่าสำเร็จ</param>
    public record ClusterResult(
        int K, int VendorsClustered, int Iterations, TimeSpan Duration,
        IReadOnlyList<ClusterSummary> Clusters, bool Persisted = false)
    {
        public ClusterResult(int k, int vendorsClustered, int iterations, TimeSpan duration)
            : this(k, vendorsClustered, iterations, duration,
                   Array.Empty<ClusterSummary>(), false) { }
    }

    /// <summary>Run a system-wide K-means pass. Reads every
    /// OcrVendorIntelligence row's DebitAccountBreakdownJson and writes
    /// cluster assignments to a JSON column on the same row (no new
    /// table needed — keeps the schema lean).</summary>
    public async Task<ClusterResult> ClusterAsync(int k = 8, int maxIterations = 50, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = await _db.OcrVendorIntelligence.AsNoTracking()
            .Where(v => !v.IsDeleted && v.TotalDocuments > 0
                && v.DebitAccountBreakdownJson != null)
            .Select(v => new { v.Id, v.DebitAccountBreakdownJson })
            .ToListAsync(ct);
        if (rows.Count < k * 2) return new ClusterResult(k, 0, 0, sw.Elapsed);

        // 1. Build the universe of account codes (top-N by usage across system)
        var allAcctCounts = new Dictionary<string, int>();
        var parsed = rows.Select(r => new
        {
            r.Id,
            Map = ParseBreakdown(r.DebitAccountBreakdownJson),
        }).ToList();
        foreach (var p in parsed)
            foreach (var (acct, cnt) in p.Map)
                allAcctCounts[acct] = allAcctCounts.GetValueOrDefault(acct) + cnt;
        var dims = allAcctCounts.OrderByDescending(kv => kv.Value).Take(30)
            .Select(kv => kv.Key).ToArray();
        if (dims.Length == 0) return new ClusterResult(k, 0, 0, sw.Elapsed);

        // 2. Build normalized vectors per vendor
        var vectors = parsed.Select(p =>
        {
            var v = new double[dims.Length];
            double total = p.Map.Values.Sum();
            if (total <= 0) return (p.Id, v);
            for (int i = 0; i < dims.Length; i++)
                v[i] = p.Map.GetValueOrDefault(dims[i], 0) / total;
            return (p.Id, v);
        }).ToList();

        // 3. K-means with kmeans++ initialization
        var centroids = InitializeCentroidsPlusPlus(vectors.Select(v => v.v).ToList(), k);
        var assignments = new int[vectors.Count];
        int iter;
        for (iter = 0; iter < maxIterations && !ct.IsCancellationRequested; iter++)
        {
            bool changed = false;
            for (int i = 0; i < vectors.Count; i++)
            {
                int best = 0;
                double bestD = double.MaxValue;
                for (int c = 0; c < centroids.Length; c++)
                {
                    var d = EuclideanSq(vectors[i].v, centroids[c]);
                    if (d < bestD) { bestD = d; best = c; }
                }
                if (assignments[i] != best) { assignments[i] = best; changed = true; }
            }
            if (!changed) { iter++; break; }
            // Recompute centroids
            var sums = new double[centroids.Length][];
            var counts = new int[centroids.Length];
            for (int c = 0; c < centroids.Length; c++) sums[c] = new double[dims.Length];
            for (int i = 0; i < vectors.Count; i++)
            {
                var c = assignments[i];
                for (int d = 0; d < dims.Length; d++) sums[c][d] += vectors[i].v[d];
                counts[c]++;
            }
            for (int c = 0; c < centroids.Length; c++)
            {
                if (counts[c] == 0) continue;
                for (int d = 0; d < dims.Length; d++) centroids[c][d] = sums[c][d] / counts[c];
            }
        }

        // ⚠️ ผลการจัดกลุ่ม **ยังไม่ถูกบันทึกลงฐาน** (ไม่มีคอลัมน์เก็บ) —
        // เดิมโค้ดตรงนี้เขียนหมายเหตุว่า "service will return assignments to
        // caller" แต่ ClusterResult ก็ไม่มีช่อง assignment ⇒ งาน K-means ทั้งรอบ
        // ถูกทิ้งทันที ขณะที่ endpoint แอดมินขึ้นข้อความว่า "สำเร็จ N vendors"
        // ตอนนี้อย่างน้อยต้องคืน **สรุปกลุ่ม** ให้ผู้เรียกเห็นของจริง และติดธง
        // Persisted=false ไว้ให้ UI พูดความจริง
        var summaries = new List<ClusterSummary>();
        for (int c = 0; c < centroids.Length; c++)
        {
            var size = assignments.Count(x => x == c);
            if (size == 0) continue;
            var topDims = Enumerable.Range(0, dims.Length)
                .OrderByDescending(d => centroids[c][d])
                .Take(3)
                .Where(d => centroids[c][d] > 0.0001)
                .Select(d => dims[d])
                .ToList();
            summaries.Add(new ClusterSummary(c, size, topDims));
        }

        sw.Stop();
        _logger.LogInformation(
            "Vendor clustering: K={K} vendors={N} iters={I} in {T} — ผลไม่ได้ถูกบันทึกลงฐาน (ยังไม่มีคอลัมน์เก็บ)",
            k, vectors.Count, iter, sw.Elapsed);
        return new ClusterResult(k, vectors.Count, iter, sw.Elapsed,
            summaries.OrderByDescending(x => x.Size).ToList(), Persisted: false);
    }

    private static Dictionary<string, int> ParseBreakdown(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new(); }
        catch { return new(); }
    }

    private static double[][] InitializeCentroidsPlusPlus(IReadOnlyList<double[]> points, int k)
    {
        var rng = new Random(42);
        var chosen = new List<double[]> { points[rng.Next(points.Count)] };
        while (chosen.Count < k)
        {
            var d2 = points.Select(p => chosen.Min(c => EuclideanSq(p, c))).ToArray();
            var sum = d2.Sum();
            if (sum <= 0) { chosen.Add(points[rng.Next(points.Count)]); continue; }
            var pick = rng.NextDouble() * sum;
            double cumulative = 0;
            int idx = 0;
            for (int i = 0; i < d2.Length; i++)
            {
                cumulative += d2[i];
                if (cumulative >= pick) { idx = i; break; }
            }
            chosen.Add(points[idx]);
        }
        return chosen.Select(c => c.ToArray()).ToArray();
    }

    private static double EuclideanSq(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            s += d * d;
        }
        return s;
    }
}
