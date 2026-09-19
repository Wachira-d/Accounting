using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class BankFeedService : IBankFeedService
{
    private readonly AccountingDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BankFeedService> _logger;
    private readonly ISecretProtector _secrets;
    private readonly Accounting.Services.Ai.IBankAiAugmenter? _aiAugmenter;

    private static readonly Dictionary<string, string> BankApiEndpoints = new()
    {
        { "SCB", "https://api-sandbox.partners.scb/partners/sandbox" },
        { "KBANK", "https://openapi.kasikornbank.com" },
        { "BBL", "https://api.bangkokbank.com" },
        { "BAY", "https://api.krungsri.com" },
        { "KTB", "https://api.krungthai.com" },
        { "TTB", "https://api.ttbbank.com" }
    };

    public BankFeedService(AccountingDbContext db, IHttpClientFactory httpClientFactory,
        ILogger<BankFeedService> logger, ISecretProtector secrets,
        Accounting.Services.Ai.IBankAiAugmenter? aiAugmenter = null)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _secrets = secrets;
        _aiAugmenter = aiAugmenter;
    }

    public async Task<BankFeedConnectionResponse> CreateConnectionAsync(Guid companyId, CreateBankFeedConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BankCode))
            throw new InvalidOperationException("กรุณาระบุรหัสธนาคาร");

        var conn = new BankConnection
        {
            CompanyId = companyId,
            BankCode = request.BankCode.ToUpper(),
            BankName = request.BankName,
            ConnectionType = request.ConnectionType ?? "API",
            ApiEndpoint = request.ApiEndpoint ?? BankApiEndpoints.GetValueOrDefault(request.BankCode.ToUpper()),
            ClientId = request.ClientId,
            EncryptedCredentials = _secrets.Protect(request.ClientSecret),
            AccessToken = _secrets.Protect(request.AccessToken),
            AutoSync = request.AutoSync,
            SyncIntervalMinutes = request.SyncIntervalMinutes,
            LinkedBankAccountId = request.LinkedBankAccountId,
            Status = "Pending"
        };

        _db.BankConnections.Add(conn);
        await _db.SaveChangesAsync();

        return MapToResponse(conn);
    }

    public async Task<List<BankFeedConnectionResponse>> GetConnectionsAsync(Guid companyId)
    {
        return await _db.BankConnections
            .Where(c => c.CompanyId == companyId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => MapToResponse(c))
            .ToListAsync();
    }

    public async Task<BankFeedConnectionResponse> GetConnectionAsync(Guid companyId, Guid connectionId)
    {
        var conn = await _db.BankConnections
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId)
            ?? throw new KeyNotFoundException("ไม่พบการเชื่อมต่อธนาคาร");
        return MapToResponse(conn);
    }

    public async Task<BankFeedSyncResult> SyncAsync(Guid companyId, Guid connectionId)
    {
        var conn = await _db.BankConnections
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId)
            ?? throw new KeyNotFoundException("ไม่พบการเชื่อมต่อธนาคาร");

        if (conn.Status == "Disabled")
            throw new InvalidOperationException("การเชื่อมต่อถูกปิดใช้งาน");

        if (!conn.LinkedBankAccountId.HasValue)
            throw new InvalidOperationException("กรุณาเชื่อมโยงบัญชีธนาคารก่อนทำ Sync");

        try
        {
            var transactions = await FetchTransactionsFromBankAsync(conn);

            var existingRefs = await _db.BankTransactions
                .Where(t => t.CompanyId == companyId && t.BankAccountId == conn.LinkedBankAccountId)
                .Select(t => t.Reference)
                .Where(r => r != null)
                .ToListAsync();

            int newCount = 0, duplicateCount = 0, autoMatched = 0;

            foreach (var txn in transactions)
            {
                if (!string.IsNullOrEmpty(txn.Reference) && existingRefs.Contains(txn.Reference))
                {
                    duplicateCount++;
                    continue;
                }

                var bankTxn = new BankTransaction
                {
                    CompanyId = companyId,
                    BankAccountId = conn.LinkedBankAccountId!.Value,
                    TransactionDate = txn.Date,
                    Amount = Math.Abs(txn.Amount),
                    TransactionType = txn.Amount >= 0
                        ? Models.Enums.BankTransactionType.Deposit
                        : Models.Enums.BankTransactionType.Withdrawal,
                    Description = txn.Description,
                    Reference = txn.Reference,
                    ReconciliationStatus = Models.Enums.ReconciliationStatus.Unmatched
                };

                _db.BankTransactions.Add(bankTxn);
                newCount++;

                if (await TryAutoMatchAsync(companyId, bankTxn))
                    autoMatched++;
            }

            var feedImport = new BankFeedImport
            {
                CompanyId = companyId,
                BankConnectionId = connectionId,
                ImportDate = DateTime.UtcNow,
                PeriodStart = transactions.Any() ? transactions.Min(t => t.Date) : DateTime.UtcNow,
                PeriodEnd = transactions.Any() ? transactions.Max(t => t.Date) : DateTime.UtcNow,
                TotalTransactions = transactions.Count,
                NewTransactions = newCount,
                DuplicateSkipped = duplicateCount,
                AutoMatched = autoMatched,
                Status = "Completed"
            };

            _db.BankFeedImports.Add(feedImport);

            conn.LastSyncAt = DateTime.UtcNow;
            conn.LastSyncStatus = "Success";
            conn.LastError = null;
            conn.Status = "Active";

            await _db.SaveChangesAsync();

            return new BankFeedSyncResult(connectionId, conn.BankName,
                transactions.Count, newCount, duplicateCount, autoMatched, "Success", null);
        }
        catch (Exception ex)
        {
            conn.LastSyncAt = DateTime.UtcNow;
            conn.LastSyncStatus = "Failed";
            conn.LastError = ex.Message;
            await _db.SaveChangesAsync();

            _logger.LogError(ex, "Bank feed sync failed for {BankCode} connection {ConnectionId}", conn.BankCode, connectionId);
            return new BankFeedSyncResult(connectionId, conn.BankName, 0, 0, 0, 0, "Failed", ex.Message);
        }
    }

    public async Task<BankFeedSyncResult> SyncAllAsync(Guid companyId)
    {
        var connections = await _db.BankConnections
            .Where(c => c.CompanyId == companyId && c.AutoSync && c.Status == "Active")
            .ToListAsync();

        int totalNew = 0, totalDuplicates = 0, totalMatched = 0;

        foreach (var conn in connections)
        {
            var result = await SyncAsync(companyId, conn.Id);
            totalNew += result.NewTransactions;
            totalDuplicates += result.DuplicateSkipped;
            totalMatched += result.AutoMatched;
        }

        return new BankFeedSyncResult(Guid.Empty, "All Banks",
            totalNew + totalDuplicates, totalNew, totalDuplicates, totalMatched, "Success", null);
    }

    public async Task DeleteConnectionAsync(Guid companyId, Guid connectionId)
    {
        var conn = await _db.BankConnections
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId)
            ?? throw new KeyNotFoundException("ไม่พบการเชื่อมต่อธนาคาร");

        conn.Status = "Disabled";
        conn.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    public async Task<BankFeedConnectionResponse> TestConnectionAsync(Guid companyId, Guid connectionId)
    {
        var conn = await _db.BankConnections
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId)
            ?? throw new KeyNotFoundException("ไม่พบการเชื่อมต่อธนาคาร");

        try
        {
            var client = _httpClientFactory.CreateClient();
            var token = _secrets.Unprotect(conn.AccessToken);
            if (!string.IsNullOrEmpty(token))
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var endpoint = conn.ApiEndpoint ?? BankApiEndpoints.GetValueOrDefault(conn.BankCode, "");
            if (!string.IsNullOrEmpty(endpoint))
            {
                var response = await client.GetAsync($"{endpoint}/health");
                conn.Status = response.IsSuccessStatusCode ? "Active" : "Error";
                conn.LastSyncStatus = response.IsSuccessStatusCode ? "Connected" : $"HTTP {response.StatusCode}";
            }
            else
            {
                conn.Status = "Active";
                conn.LastSyncStatus = "Connected (no endpoint test)";
            }

            conn.LastError = null;
        }
        catch (Exception ex)
        {
            conn.Status = "Error";
            conn.LastSyncStatus = "Failed";
            conn.LastError = ex.Message;
        }

        await _db.SaveChangesAsync();
        return MapToResponse(conn);
    }

    private async Task<List<BankFeedTransaction>> FetchTransactionsFromBankAsync(BankConnection conn)
    {
        var client = _httpClientFactory.CreateClient();

        var token = _secrets.Unprotect(conn.AccessToken);
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var fromDate = conn.LastSyncAt?.AddDays(-1) ?? DateTime.UtcNow.AddDays(-30);
        var toDate = DateTime.UtcNow;

        var endpoint = conn.ApiEndpoint ?? BankApiEndpoints.GetValueOrDefault(conn.BankCode, "");
        if (string.IsNullOrEmpty(endpoint))
            return new List<BankFeedTransaction>();

        try
        {
            var url = $"{endpoint}/v1/accounts/transactions?from={fromDate:yyyy-MM-dd}&to={toDate:yyyy-MM-dd}";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Bank API returned {StatusCode} for {BankCode}", response.StatusCode, conn.BankCode);
                return new List<BankFeedTransaction>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var transactions = System.Text.Json.JsonSerializer.Deserialize<List<BankFeedTransaction>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return transactions ?? new List<BankFeedTransaction>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch transactions from {BankCode}", conn.BankCode);
            throw new InvalidOperationException($"ไม่สามารถเชื่อมต่อ {conn.BankName} ได้: {ex.Message}");
        }
    }

    /// <summary>
    /// จับคู่รายการที่เพิ่ง sync เข้ามากับรายการชำระเงินที่ค้างอยู่.
    ///
    /// ═══ ของเดิมพังตรงไหน (`DECISION_AUDIT_2026-09-18.md` §3 D4-2, P0) ═══
    /// 1. **ประทับ `Matched` กับความว่าง** — เจอ `Document` ที่ยอดตรงแล้วตั้ง
    ///    `txn.ReconciliationStatus = Matched` **โดยไม่เก็บว่าจับกับอะไร**
    ///    (ไม่ตั้ง `MatchedPaymentId` / ไม่มีคอลัมน์ document id เลย) และไม่แตะ
    ///    เอกสาร ⇒ บรรทัดธนาคารขึ้นว่า "จับคู่แล้ว" แต่ใบยังค้างชำระ และ
    ///    **ไม่มีใครตามได้ว่าคู่คือใคร** (R1 สถานะปลายทางประทับเอง)
    /// 2. **ถาม AI ก่อนลองในบ้าน** — ชั้นในบ้านมีแค่ "ยอดตรงเป๊ะ + memo อ้าง
    ///    เลขเอกสาร" พลาดปุ๊บยิง AI ทันที ทั้งที่ยังไม่ได้ลองชื่อผู้โอน/คะแนน
    ///    ผู้สมัครเลย (ขัด `DECISION_DOCTRINE` §2.1 student-first)
    ///
    /// ═══ ตอนนี้ ═══
    /// ลองชั้นในบ้านให้ครบก่อน: ให้คะแนนด้วย <see cref="Accounting.Helpers.BankMatchScorer"/>
    /// (สูตรเดียวกับรายการที่มนุษย์เห็นบนจอ — รวมชื่อผู้โอน) แล้วให้
    /// <see cref="Accounting.Helpers.BankMatchArbiter"/> ตัดสิน. ทุกสถานะที่เขียน
    /// **ต้องมี id ของคู่เสมอ** — ประทับ `Matched` ได้เฉพาะ verdict `Apply`,
    /// `Suggest` เขียน `Suggested` พร้อม `MatchedPaymentId` ของคู่ที่เสนอ
    ///
    /// AI เป็น last resort และคำตอบต้องผ่านด่าน
    /// <see cref="Accounting.Helpers.BankMatchArbiter.ScreenAiProposals"/> (กัน id ที่แต่งขึ้น)
    /// **ข้อจำกัดที่ยังค้าง**: augmenter ตอบเป็น `Document` id แต่
    /// `BankTransaction` **ไม่มีคอลัมน์เก็บ document id** ⇒ ถ้าเอกสารนั้นยัง
    /// ไม่มีรายการชำระให้ผูก เราจะ **ไม่ประทับสถานะใด ๆ** (ปล่อย `Unmatched`
    /// + log) ดีกว่าประทับสถานะที่บันทึกคู่ไม่ได้ — ตามหลัก "ค่าที่แต่งขึ้น
    /// อันตรายกว่าการไม่ตอบ" (F2 ข้อ 3). ต้องเพิ่มคอลัมน์
    /// `SuggestedDocumentId` ก่อนถึงจะเปิดเส้นนี้เต็มได้ (SQL อยู่ในรายงานรอบนี้)
    /// </summary>
    private async Task<bool> TryAutoMatchAsync(Guid companyId, BankTransaction txn)
    {
        // ── ชั้นที่ 1 (ในบ้าน): รายการชำระเงินที่ยังไม่ถูกจับคู่ ────────────
        var (decision, byId) = await ScoreLocalPaymentCandidatesAsync(companyId, txn);

        if (decision.Verdict == Accounting.Helpers.BankMatchVerdict.Apply)
        {
            var chosen = decision.Chosen!;
            txn.ReconciliationStatus = Models.Enums.ReconciliationStatus.Matched;
            txn.MatchedPaymentId = chosen.Id;
            txn.SuggestedDocumentId = null;
            txn.ReconciledAt = DateTime.UtcNow;
            txn.ReconciledBy = Accounting.Helpers.BankMatchAttribution.BankFeed;
            txn.MatchRuleCode = decision.RuleCode;
            txn.MatchReason = decision.Reason;
            _logger.LogInformation(
                "Bank txn {Txn} → จับคู่กับรายการชำระ {PaymentId} ({Rule}: {Reason})",
                txn.Id, chosen.Id, decision.RuleCode, decision.Reason);
            return true;
        }

        if (decision.Verdict == Accounting.Helpers.BankMatchVerdict.Suggest)
        {
            var chosen = decision.Chosen!;
            // เสนอ = ยังไม่ใช่การกระทบยอด → ไม่ตั้ง ReconciledAt
            txn.ReconciliationStatus = Models.Enums.ReconciliationStatus.Suggested;
            txn.MatchedPaymentId = chosen.Id;
            txn.ReconciledBy = Accounting.Helpers.BankMatchAttribution.BankFeedSuggested;
            txn.MatchRuleCode = decision.RuleCode;
            txn.MatchReason = decision.Reason;
            _logger.LogInformation(
                "Bank txn {Txn} → เสนอรายการชำระ {PaymentId} ({Rule}: {Reason})",
                txn.Id, chosen.Id, decision.RuleCode, decision.Reason);
            return true;
        }

        // ── ชั้นที่ 2 (ครูพิเศษ): ถาม AI เฉพาะตอนชั้นในบ้านไม่ได้คำตอบ ──────
        if (_aiAugmenter == null) return false;
        if (string.IsNullOrWhiteSpace(txn.Description) && string.IsNullOrWhiteSpace(txn.Reference))
            return false;
        try
        {
            using var aiCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var memo = string.IsNullOrEmpty(txn.Reference) ? txn.Description : $"{txn.Reference} {txn.Description}";
            // ⚠ เดิมส่ง `"THB"` **ตายตัว** ⇒ บัญชี USD ได้ชุดผู้สมัครของใบ THB
            // (augmenter กรอง `d.Currency == currency`) = ชุดผิดตั้งแต่ต้นทาง
            var feedCurrency = await _db.BankAccounts.AsNoTracking()
                .Where(a => a.Id == txn.BankAccountId && a.CompanyId == companyId)
                .Select(a => a.Currency).FirstOrDefaultAsync() ?? "THB";
            var aiResult = await _aiAugmenter.SuggestStatementMatchAsync(
                companyId, txn.Id, memo, txn.TransactionDate, txn.Amount, feedCurrency,
                txn.TransactionType, localBestDocumentId: null, localConfidence: 0m,
                aiCts.Token);
            if (!aiResult.UsedAi
                || string.IsNullOrEmpty(aiResult.Answer) || aiResult.Answer == "__NEW__"
                || !Guid.TryParse(aiResult.Answer, out var aiDocId))
                return false;

            // ── ด่านกัน hallucination (กฎเหล็ก #1 · DOCTRINE §2.2) ──────────
            // **candidate set ที่ตรวจกลับได้** = เอกสารของบริษัทนี้ที่
            // (ก) ไม่ถูกลบ/ยกเลิก (ข) **ยังค้างชำระ** — ใบที่ปิดแล้วไม่ใช่ต้นทาง
            // ของเงินก้อนใหม่ (ค) สกุลเงินตรงกับบัญชี — ยอดข้ามสกุลเทียบกันไม่ได้
            // (ง) อยู่ในกรอบวันที่เดียวกับที่ให้คะแนนในบ้าน
            var openDocIds = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && d.Status != Models.Enums.DocumentStatus.Voided
                    && d.BalanceDue > 0
                    && d.Currency == feedCurrency
                    && d.DocumentDate >= txn.TransactionDate.Date.AddDays(-90)
                    && d.DocumentDate <= txn.TransactionDate.Date.AddDays(7))
                .Select(d => d.Id)
                .ToListAsync();

            // ด่านนี้ **บังคับจริง** — `Accepted` ว่าง = ทิ้งคำตอบ
            // (ห้ามคำนวณด่านแล้วไม่ใช้ผล: "มี ≠ ถูกเรียก" F2 ข้อ 2)
            var screened = Accounting.Helpers.BankMatchArbiter.ScreenAiProposals(
                new[] { new Accounting.Helpers.BankMatchArbiter.AiProposal(
                    aiDocId, "Document", aiResult.Confidence ?? 0m) },
                openDocIds);
            if (screened.Accepted.Count == 0)
            {
                _logger.LogWarning(
                    "AI เสนอเอกสาร {Doc} ให้ txn {Txn} แต่ไม่ผ่านด่าน "
                    + "(นอกชุดผู้สมัคร {Unknown} · ความมั่นใจต่ำ {Low}) — ทิ้งคำตอบ",
                    aiDocId, txn.Id, screened.RejectedUnknownIds.Count,
                    screened.RejectedLowConfidence.Count);
                return false;
            }

            // คำตอบของ AI เป็น "เอกสาร". ถ้าเอกสารนั้นมีรายการชำระอยู่ในชุด
            // ผู้สมัครในบ้านแล้ว ให้ผูกกับรายการชำระ (แม่นกว่า) — ถ้ายังไม่มี
            // ให้เก็บลง `SuggestedDocumentId` ซึ่ง**เป็นคอลัมน์ที่เพิ่งเพิ่ม**
            // เพื่อเปิดเส้นนี้กลับมา: ก่อนหน้านี้ตารางไม่มีที่เก็บ document id
            // เลยเลือก "ไม่ประทับสถานะใด ๆ" ⇒ ความสามารถนี้หายไปทั้งเส้น
            // (`DECISION_AUDIT_2026-09-18.md` §10.5 ข้อ 5)
            // **สถานะทุกตัวยังต้องมีคู่ที่กดดูได้เสมอ** (ราก R1)
            var viaPayment = byId.Values.FirstOrDefault(p => p.DocumentId == aiDocId);

            txn.ReconciliationStatus = Models.Enums.ReconciliationStatus.Suggested;
            txn.ReconciledBy = Accounting.Helpers.BankMatchAttribution.BankFeedAiSuggested;
            txn.MatchRuleCode = "BANK-MATCH-AI-DOC";
            if (viaPayment != null)
            {
                txn.MatchedPaymentId = viaPayment.Id;
                txn.SuggestedDocumentId = aiDocId;
                txn.MatchReason =
                    $"AI เสนอเอกสารนี้ (ความมั่นใจ {(aiResult.Confidence ?? 0m):P0}) "
                    + "และพบรายการชำระของเอกสารเดียวกันในกรอบวันที่ — รอยืนยัน";
                _logger.LogInformation("Bank txn {Txn} → AI เสนอรายการชำระ {PaymentId} ({Conf:P0})",
                    txn.Id, viaPayment.Id, aiResult.Confidence ?? 0m);
            }
            else
            {
                txn.SuggestedDocumentId = aiDocId;
                txn.MatchReason =
                    $"AI เสนอเอกสารนี้ (ความมั่นใจ {(aiResult.Confidence ?? 0m):P0}) "
                    + "แต่ยังไม่มีรายการชำระของเอกสารนี้ — บันทึกการชำระก่อนจึงจะกระทบยอดได้";
                _logger.LogInformation("Bank txn {Txn} → AI เสนอเอกสาร {Doc} ({Conf:P0}) — ยังไม่มีรายการชำระ",
                    txn.Id, aiDocId, aiResult.Confidence ?? 0m);
            }
            return true;
        }
        catch (Exception aiEx)
        {
            _logger.LogWarning(aiEx, "AI bank-match fallback failed for txn {Id} — leaving Unmatched", txn.Id);
        }

        return false;
    }

    /// <summary>
    /// ให้คะแนนรายการชำระเงินที่เป็นไปได้ด้วยสูตรกลาง แล้วให้ arbiter ตัดสิน.
    /// คืนคำตัดสิน + ตารางผู้สมัคร (ใช้ตรวจคำตอบ AI ต่อ)
    /// </summary>
    private async Task<(Accounting.Helpers.BankMatchArbiter.Decision Decision,
        Dictionary<Guid, Payment> ById)> ScoreLocalPaymentCandidatesAsync(
        Guid companyId, BankTransaction txn)
    {
        var winStart = txn.TransactionDate.Date.AddDays(-7);
        var winEnd = txn.TransactionDate.Date.AddDays(7);

        // กันจับซ้ำ: id ที่ถูกอ้างเป็นคู่ (หรือคู่ที่เสนอ) ของบรรทัดอื่นแล้ว
        var usedPaymentIds = await _db.Set<BankTransaction>()
            .Where(t => t.CompanyId == companyId && t.MatchedPaymentId != null && t.Id != txn.Id)
            .Select(t => t.MatchedPaymentId!.Value)
            .ToListAsync();

        var candidates = await _db.Payments
            .Include(p => p.Document)
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                && p.PaymentDate >= winStart && p.PaymentDate <= winEnd
                && p.Document.Status != Models.Enums.DocumentStatus.Voided
                && !usedPaymentIds.Contains(p.Id))
            .ToListAsync();
        await _db.HydratePaymentContactsAsync(companyId, candidates);

        var byId = candidates.ToDictionary(p => p.Id);

        // FX — ยอดของ Payment อยู่ในสกุลเอกสาร ต้องแปลงเป็นสกุลของบัญชีธนาคาร
        // ก่อนเทียบ. แปลงไม่ได้ = **ข้ามผู้สมัครรายนั้น** (เส้นนี้ประทับสถานะเอง)
        var scoreBankCurrency = await _db.BankAccounts.AsNoTracking()
            .Where(a => a.Id == txn.BankAccountId && a.CompanyId == companyId)
            .Select(a => a.Currency).FirstOrDefaultAsync();
        var scoreHomeCurrency = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.BaseCurrency).FirstOrDefaultAsync();
        var fxByPayment = candidates.ToDictionary(
            p => p.Id,
            p => Accounting.Helpers.BankMatchCurrency.Convert(
                scoreBankCurrency, p.Document?.Currency, p.Amount,
                p.ExchangeRate, p.Document?.ExchangeRate, scoreHomeCurrency));

        var options = candidates.Where(p => fxByPayment[p.Id].Comparable).Select(p =>
        {
            var r = Accounting.Helpers.BankMatchScorer.Score(new Accounting.Helpers.BankMatchScorer.Input(
                CandidateAmount: fxByPayment[p.Id].BankCurrencyAmount,
                BankAmount: Math.Abs(txn.Amount),
                CandidateDate: p.PaymentDate,
                BankDate: txn.TransactionDate,
                CandidateRef: p.Reference,
                CandidateNotes: p.Notes,
                CandidateDocNumber: p.Document?.DocumentNumber,
                CandidateName: p.Document?.Contact?.Name,
                BankDescription: txn.Description,
                BankReference: txn.Reference,
                BankPayee: txn.Payee));
            return new Accounting.Helpers.BankMatchArbiter.Option(
                p.Id, "Payment", r.Score, r.HasIdentitySignal, r.Reason);
        }).ToList();

        return (Accounting.Helpers.BankMatchArbiter.Decide(options), byId);
    }

    private static BankFeedConnectionResponse MapToResponse(BankConnection c) => new(
        c.Id, c.BankCode, c.BankName, c.ConnectionType, c.Status,
        c.LastSyncAt, c.LastSyncStatus, c.LastError, c.AutoSync,
        c.SyncIntervalMinutes, c.LinkedBankAccountId);
}

internal record BankFeedTransaction(
    DateTime Date,
    decimal Amount,
    string? Description,
    string? Reference,
    string? CounterParty);
