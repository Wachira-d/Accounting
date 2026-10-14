using System.Security.Cryptography;
using System.Text;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.DTOs.OpenBanking;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class OpenBankingService : IOpenBankingService
{
    private readonly AccountingDbContext _db;
    private readonly string _encryptionKey;
    private readonly IErrorLogService _errorLogService;
    /// <summary>ตัวเขียน statement ตัวเดียว (รอบ 205 ทีม C) — null (เทสต์ที่ไม่ผ่าน DI) ⇒ สร้างจาก context เดียวกัน</summary>
    private readonly IBankService _bank;

    public OpenBankingService(AccountingDbContext db, IConfiguration configuration, IErrorLogService errorLogService,
        IBankService? bank = null)
    {
        _db = db;
        _bank = bank ?? new BankService(db);
        // ★ H-A6: เดิมตกไปใช้คีย์ที่เขียนไว้ใน source **เงียบ ๆ** ⇒ credential
        // ของธนาคาร (ClientId/ClientSecret/token) ถูกเข้ารหัสด้วยกุญแจสาธารณะ
        // = เท่ากับ plaintext ถ้าฐานข้อมูลรั่ว · JWT_SECRET / ENCRYPTION_KEY
        // fail-fast ใน production มาตั้งแต่ต้น (Program.cs) — ตัวนี้ตกหล่นตัวเดียว
        _encryptionKey = Environment.GetEnvironmentVariable("OPENBANKING_ENCRYPTION_KEY")
            ?? configuration["OpenBanking:EncryptionKey"]
            ?? (IsProductionEnvironment()
                ? throw new InvalidOperationException(
                    "OPENBANKING_ENCRYPTION_KEY environment variable is required in production — " +
                    "ห้ามเข้ารหัส credential ของธนาคารด้วยคีย์ที่อยู่ใน source")
                : "DefaultKeyForDev-Change-In-Production!");
        _errorLogService = errorLogService;
    }

    /// <summary>อ่านจาก ASPNETCORE_ENVIRONMENT ตรง ๆ — service นี้ไม่ได้รับ
    /// <c>IWebHostEnvironment</c> และการเพิ่ม dependency เพื่อเช็คค่าเดียวไม่คุ้ม</summary>
    private static bool IsProductionEnvironment()
        => string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Production", StringComparison.OrdinalIgnoreCase);

    // ===== Connections =====

    public async Task<BankConnectionResponse> CreateConnectionAsync(Guid companyId, CreateBankConnectionRequest request)
    {
        var connection = new BankConnection
        {
            CompanyId = companyId,
            BankCode = request.BankCode,
            BankName = request.BankName,
            ConnectionType = request.ConnectionType,
            ApiEndpoint = request.ApiEndpoint,
            ClientId = request.ClientId,
            EncryptedCredentials = EncryptString(request.Credentials ?? ""),
            AutoSync = request.AutoSync,
            SyncIntervalMinutes = request.SyncIntervalMinutes,
            LinkedBankAccountId = request.LinkedBankAccountId,
            Status = "Active"
        };

        _db.Set<BankConnection>().Add(connection);
        await _db.SaveChangesAsync();

        return MapToConnectionResponse(connection);
    }

    public async Task<List<BankConnectionResponse>> GetConnectionsAsync(Guid companyId)
    {
        return await _db.Set<BankConnection>()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => MapToConnectionResponse(c))
            .ToListAsync();
    }

    public async Task<BankConnectionResponse> UpdateConnectionAsync(Guid companyId, Guid connectionId, UpdateBankConnectionRequest request)
    {
        var connection = await _db.Set<BankConnection>()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId && !c.IsDeleted)
            ?? throw new InvalidOperationException("Bank connection not found.");

        if (request.AutoSync.HasValue) connection.AutoSync = request.AutoSync.Value;
        if (request.SyncIntervalMinutes.HasValue) connection.SyncIntervalMinutes = request.SyncIntervalMinutes.Value;
        if (request.Credentials != null) connection.EncryptedCredentials = EncryptString(request.Credentials);

        await _db.SaveChangesAsync();

        return MapToConnectionResponse(connection);
    }

    public async Task DeleteConnectionAsync(Guid companyId, Guid connectionId)
    {
        var connection = await _db.Set<BankConnection>()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId)
            ?? throw new InvalidOperationException("Bank connection not found.");

        connection.IsDeleted = true;
        connection.Status = "Disconnected";
        await _db.SaveChangesAsync();
    }

    // ===== Sync =====

    public async Task<BankFeedImportResponse> SyncTransactionsAsync(Guid companyId, Guid connectionId, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var connection = await _db.Set<BankConnection>()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == connectionId && !c.IsDeleted)
            ?? throw new InvalidOperationException("Bank connection not found.");

        var syncFrom = fromDate ?? connection.LastSyncAt ?? DateTime.UtcNow.AddDays(-30);
        var syncTo = toDate ?? DateTime.UtcNow;

        // In production, this would call the bank's API to fetch transactions.
        // Here we simulate by checking for existing bank transactions in that period
        // and creating an import record.

        var linkedBankAccountId = connection.LinkedBankAccountId;
        int totalTransactions = 0;
        int newTransactions = 0;
        int duplicateSkipped = 0;
        int autoMatched = 0;

        if (linkedBankAccountId.HasValue)
        {
            // Count existing transactions in the period as "fetched"
            // tenant isolation (กฎเหล็ก #2 M) — คิวรีเดิมไม่มี `CompanyId`
            // (จุดที่ 6 ที่เหลือจากรอบที่แล้ว)
            var existingTransactions = await _db.BankTransactions
                .Where(t => t.CompanyId == companyId
                          && t.BankAccountId == linkedBankAccountId.Value
                          && t.TransactionDate >= syncFrom
                          && t.TransactionDate <= syncTo)
                .ToListAsync();

            totalTransactions = existingTransactions.Count;

            // Simulate: some are new, some are duplicates
            var existingReferences = existingTransactions
                .Where(t => t.Reference != null)
                .Select(t => t.Reference!)
                .ToHashSet();

            newTransactions = existingTransactions.Count(t => t.ReconciliationStatus == ReconciliationStatus.Unmatched);
            duplicateSkipped = totalTransactions - newTransactions;
            autoMatched = existingTransactions.Count(t => t.ReconciliationStatus == ReconciliationStatus.Matched);
        }

        var import = new BankFeedImport
        {
            CompanyId = companyId,
            BankConnectionId = connectionId,
            ImportDate = DateTime.UtcNow,
            PeriodStart = syncFrom,
            PeriodEnd = syncTo,
            TotalTransactions = totalTransactions,
            NewTransactions = newTransactions,
            DuplicateSkipped = duplicateSkipped,
            AutoMatched = autoMatched,
            Status = "Completed"
        };

        _db.Set<BankFeedImport>().Add(import);

        // Update connection sync status
        connection.LastSyncAt = DateTime.UtcNow;
        connection.LastSyncStatus = "Success";

        await _db.SaveChangesAsync();

        return MapToImportResponse(import);
    }

    public async Task<List<BankFeedImportResponse>> GetImportHistoryAsync(Guid companyId, Guid connectionId)
    {
        return await _db.Set<BankFeedImport>()
            .Where(i => i.BankConnectionId == connectionId && i.CompanyId == companyId)
            .OrderByDescending(i => i.ImportDate)
            .Select(i => MapToImportResponse(i))
            .ToListAsync();
    }

    public async Task ProcessAutoSyncAsync()
    {
        // Find all connections that need auto-sync
        var connectionsToSync = await _db.Set<BankConnection>()
            .Where(c => c.AutoSync
                      && !c.IsDeleted
                      && c.Status == "Active"
                      && (c.LastSyncAt == null
                          || c.LastSyncAt.Value.AddMinutes(c.SyncIntervalMinutes) <= DateTime.UtcNow))
            .ToListAsync();

        foreach (var connection in connectionsToSync)
        {
            try
            {
                await SyncTransactionsAsync(connection.CompanyId, connection.Id);
            }
            catch (Exception ex)
            {
                connection.LastSyncStatus = "Failed";
                connection.LastError = ex.Message;
                await _db.SaveChangesAsync();
                await _errorLogService.LogErrorAsync(ex, $"OpenBanking.SyncTransactions/{connection.Id}");
            }
        }
    }

    // ===== File Import =====

    /// <summary>
    /// นำเข้าไฟล์ statement (CSV/TSV · Excel · OFX) — แยกไฟล์ด้วย <c>BankStatementFileReader</c> แล้วลงผ่านตัวเขียนตัวเดียว
    /// <see cref="IBankService.ImportParsedStatementRowsAsync"/> (รอบ 205 ทีม C)
    ///
    /// <para>═══ ที่มา ═══ เดิมทางนี้เป็นตัวเขียนที่สอง: กันซ้ำด้วยกติกาของตัวเอง · ยอดคงเหลือคำนวณเดินเอง (คนละความหมายกับหน้า "ธนาคาร") ·
    /// <b>OFX/QIF นับบรรทัดแล้วตอบว่า "ใหม่" N รายการโดยไม่บันทึกอะไรเลย</b> (HTTP 200 โกหก) · ตัวจับคู่ของตัวเองอีกชุด ⇒ ตอนนี้ OFX ลงจริง ·
    /// QIF ล้มดัง "ยังไม่รองรับ" (คำตัดสินข้อ 151) · จับคู่ด้วย <c>AutoMatchAsync</c> ตัวเดียวกับทุกทางเข้า (ผ่านตัวเขียน)</para>
    /// <para>รายการที่วัน+ยอดตรงของเดิมแต่คำอธิบายต่าง = ลงเป็นรายการใหม่ + คำเตือน (ความหมายเดิมของทางนี้ — ไม่มีหน้าจอให้เลือก)</para>
    /// </summary>
    public async Task<BankFeedImportResponse> ImportFileAsync(Guid companyId, Guid bankAccountId, string fileFormat, string base64Content)
    {
        if (!await _db.BankAccounts.AnyAsync(a => a.CompanyId == companyId && a.Id == bankAccountId))
            throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");
        if (string.IsNullOrWhiteSpace(base64Content))
            throw new ArgumentException("กรุณาแนบไฟล์ statement");

        var fileBytes = Convert.FromBase64String(base64Content);
        var fileName = "statement." + (fileFormat ?? "csv").Trim().ToLowerInvariant() switch
        {
            "excel" or "xlsx" => "xlsx",
            "ofx" => "ofx",
            _ => "csv",
        };
        var parsed = Helpers.BankStatementFileReader.Read(fileFormat, fileName, fileBytes);

        var result = await _bank.ImportParsedStatementRowsAsync(companyId, bankAccountId,
            Helpers.BankStatementFileReader.ToLines(parsed),
            Helpers.BankStatementFileReader.WriteOptions(parsed, BankStatementConflictPolicy.InsertAsNew,
                Helpers.BankStatementFileReader.SourceFile(parsed, fileName, fileBytes)));

        var import = new BankFeedImport
        {
            CompanyId = companyId,
            BankConnectionId = null, // File import — no connection (FK is nullable)
            ImportDate = DateTime.UtcNow,
            PeriodStart = parsed.Rows.Count > 0 ? parsed.Rows.Min(r => r.Date) : DateTime.UtcNow,
            PeriodEnd = parsed.Rows.Count > 0 ? parsed.Rows.Max(r => r.Date) : DateTime.UtcNow,
            TotalTransactions = parsed.Rows.Count,
            NewTransactions = result.Lines.Count(l => l.Outcome == BankStatementLineOutcome.Created),
            DuplicateSkipped = result.Skipped,
            AutoMatched = result.AutoMatched,
            Status = "Completed",
            ErrorMessage = result.Warnings.Count > 0 ? string.Join("\n", result.Warnings) : null,
        };
        _db.Set<BankFeedImport>().Add(import);
        await _db.SaveChangesAsync();

        return MapToImportResponse(import) with { Warnings = result.Warnings };
    }

    // ===== Mapping =====

    private static BankConnectionResponse MapToConnectionResponse(BankConnection c) => new(
        c.Id, c.BankCode, c.BankName, c.ConnectionType, c.Status,
        c.AutoSync, c.SyncIntervalMinutes, c.LastSyncAt, c.LastSyncStatus, c.LinkedBankAccountId);

    private static BankFeedImportResponse MapToImportResponse(BankFeedImport i) => new(
        i.Id, i.ImportDate, i.PeriodStart, i.PeriodEnd,
        i.TotalTransactions, i.NewTransactions, i.DuplicateSkipped, i.AutoMatched,
        i.Status, i.ErrorMessage);

    private string EncryptString(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(Encoding.UTF8.GetBytes(_encryptionKey));
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
        return Convert.ToBase64String(result);
    }
}
