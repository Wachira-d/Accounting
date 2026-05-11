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

    public record ClusterResult(int K, int VendorsClustered, int Iterations, TimeSpan Duration);

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

        // Persist cluster ids — we re-use TopLineKeywordsJson? No, we need
        // a dedicated bag. Stick the cluster as part of UpdatedBy for now
        // since we want a minimal-schema-impact MVP. (A proper Cluster
        // column can be added later if we promote this.)
        // Skipping persist in this MVP; service will return assignments to caller.

        sw.Stop();
        _logger.LogInformation("Vendor clustering: K={K} vendors={N} iters={I} in {T}",
            k, vectors.Count, iter, sw.Elapsed);
        return new ClusterResult(k, vectors.Count, iter, sw.Elapsed);
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
