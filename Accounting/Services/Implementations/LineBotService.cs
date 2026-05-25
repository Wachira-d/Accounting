using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// LINE bot for non-IT users — bind your LINE account once, then send
/// short Thai commands to log expenses or check balances without
/// opening the app. Designed to be friendly: every command shows a
/// helpful "?" reply when it doesn't recognise the input.
/// </summary>
public class LineBotService : ILineBotService
{
    private readonly AccountingDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LineBotService> _logger;
    private readonly IDocumentService _docService;

    public LineBotService(AccountingDbContext db, IConfiguration config,
        IHttpClientFactory httpFactory, ILogger<LineBotService> logger,
        IDocumentService docService)
    {
        _db = db; _config = config; _httpFactory = httpFactory;
        _logger = logger; _docService = docService;
    }

    public async Task<string> IssueBindCodeAsync(Guid userId)
    {
        // Invalidate any unused codes for this user — only one live at a time.
        await _db.LineBindCodes
            .Where(c => c.UserId == userId && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, DateTime.UtcNow)
                                       .SetProperty(c => c.UsedByLineUserId, "expired-by-reissue"));

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _db.LineBindCodes.Add(new LineBindCode
        {
            UserId = userId,
            Code = code,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        });
        await _db.SaveChangesAsync();
        return code;
    }

    public bool VerifySignature(string body, string? headerSignature)
    {
        var secret = _config["Line:ChannelSecret"];
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(headerSignature)) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        var expected = Convert.ToBase64String(digest);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(headerSignature));
    }

    public async Task<string?> HandleMessageAsync(string lineUserId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var msg = text.Trim();

        // 1) Bind command — "ผูก 123456" links this LineUserId to a Next Acc account.
        if (msg.StartsWith("ผูก", StringComparison.OrdinalIgnoreCase)
         || msg.StartsWith("bind", StringComparison.OrdinalIgnoreCase))
        {
            var code = new string(msg.Where(char.IsDigit).ToArray());
            if (code.Length != 6) return "กรุณาส่ง: ผูก {รหัส 6 หลัก จากเว็บไซต์}";
            var row = await _db.LineBindCodes
                .Include(c => c.User)
                .Where(c => c.Code == code && c.UsedAt == null && c.ExpiresAt > DateTime.UtcNow)
                .FirstOrDefaultAsync();
            if (row == null) return "❌ รหัสไม่ถูกต้องหรือหมดอายุ — กรุณาขอรหัสใหม่จากเว็บไซต์";
            row.UsedAt = DateTime.UtcNow;
            row.UsedByLineUserId = lineUserId;
            row.User.LineUserId = lineUserId;
            await _db.SaveChangesAsync();
            return $"✅ เชื่อมต่อสำเร็จ — สวัสดี {row.User.FullName}\n" +
                   "ลองคำสั่ง:\n" +
                   "• บันทึก เซเว่น 250 — บันทึกค่าใช้จ่าย\n" +
                   "• ดูยอด — ดูเงินเข้า/ออกเดือนนี้\n" +
                   "• ช่วยเหลือ — ดูคำสั่งทั้งหมด";
        }

        // 2) Everything below requires a bound user.
        var user = await _db.Users.FirstOrDefaultAsync(u => u.LineUserId == lineUserId);
        if (user == null)
            return "👋 ยังไม่ได้เชื่อมต่อบัญชี — กรุณาขอรหัสจากเว็บไซต์ Next Acc แล้วส่งข้อความ\n" +
                   "ผูก {รหัส 6 หลัก}";

        // Resolve the active company for this LINE user.
        // - Single-company users: use that company silently.
        // - Multi-company: use the saved active company; if none saved, prompt
        //   to pick one with "เลือกบริษัท {N}".
        // - "เลือกบริษัท" itself is intercepted here so it can update state.
        var companies = await _db.CompanyUsers
            .Where(cu => cu.UserId == user.Id)
            .Include(cu => cu.Company)
            .Select(cu => new { cu.CompanyId, Name = cu.Company.Name })
            .ToListAsync();
        if (companies.Count == 0) return "❌ ไม่พบบริษัทที่ผูกกับบัญชีนี้";

        // Upsert the per-LINE-user state row. Two webhook events arriving
        // simultaneously for a new user would both see "null" and both try
        // to INSERT, hitting the UNIQUE index on LineUserId. We catch the
        // race and re-read on the second attempt.
        var state = await _db.LineUserStates.FirstOrDefaultAsync(s => s.LineUserId == lineUserId);
        if (state == null)
        {
            state = new LineUserState { LineUserId = lineUserId, UserId = user.Id };
            _db.LineUserStates.Add(state);
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                _db.Entry(state).State = EntityState.Detached;
                state = await _db.LineUserStates.FirstAsync(s => s.LineUserId == lineUserId);
            }
        }
        state.LastInteractionAt = DateTime.UtcNow;

        // "เลือกบริษัท N" — switch active company by 1-based index.
        if (msg.StartsWith("เลือกบริษัท") || msg.StartsWith("เลือก") || msg.StartsWith("switch"))
        {
            var n = new string(msg.Where(char.IsDigit).ToArray());
            if (int.TryParse(n, out var idx) && idx >= 1 && idx <= companies.Count)
            {
                state.ActiveCompanyId = companies[idx - 1].CompanyId;
                await _db.SaveChangesAsync();
                return $"✅ เลือกบริษัท: {companies[idx - 1].Name}";
            }
            return BuildCompanyMenu(companies, "เลือกบริษัท:", c => c.Name);
        }

        // Determine which company this command operates against.
        Guid companyId;
        if (companies.Count == 1)
        {
            companyId = companies[0].CompanyId;
            if (state.ActiveCompanyId != companyId) state.ActiveCompanyId = companyId;
        }
        else if (state.ActiveCompanyId.HasValue && companies.Any(c => c.CompanyId == state.ActiveCompanyId.Value))
        {
            companyId = state.ActiveCompanyId.Value;
        }
        else
        {
            await _db.SaveChangesAsync();
            return BuildCompanyMenu(companies,
                "👤 คุณมีหลายบริษัท — กรุณาเลือกบริษัทก่อน (ส่ง 'เลือกบริษัท 1' เป็นต้น):",
                c => c.Name);
        }
        await _db.SaveChangesAsync();

        // 3) "ดูยอด" — quick this-month summary.
        if (msg.StartsWith("ดูยอด") || msg.Equals("ยอด", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTime.UtcNow;
            var from = new DateTime(now.Year, now.Month, 1);
            var lines = await _db.JournalEntryLines
                .Include(l => l.JournalEntry).Include(l => l.Account)
                .Where(l => l.JournalEntry.CompanyId == companyId
                    && l.JournalEntry.Status == Models.Enums.JournalEntryStatus.Posted
                    && l.JournalEntry.EntryDate >= from
                    && l.JournalEntry.EntryDate <= now)
                .ToListAsync();
            var rev = lines.Where(l => l.Account?.AccountType == Models.Enums.AccountType.Revenue).Sum(l => l.CreditAmount - l.DebitAmount);
            var exp = lines.Where(l => l.Account?.AccountType == Models.Enums.AccountType.Expense).Sum(l => l.DebitAmount - l.CreditAmount);
            return $"📊 เดือน {now:MM/yyyy}\n" +
                   $"💰 เงินเข้า: {rev:N2} ฿\n" +
                   $"💸 เงินออก: {exp:N2} ฿\n" +
                   $"📈 คงเหลือ: {(rev - exp):N2} ฿";
        }

        // 4) "ช่วยเหลือ" / "help"
        if (msg.StartsWith("ช่วย") || msg.Equals("help", StringComparison.OrdinalIgnoreCase) || msg == "?")
        {
            return "📖 คำสั่งที่ใช้ได้:\n" +
                   "• บันทึก {ร้าน} {จำนวน} — เช่น 'บันทึก เซเว่น 250'\n" +
                   "• ดูยอด — เงินเข้า/ออกเดือนนี้\n" +
                   (companies.Count > 1 ? "• เลือกบริษัท {เลข} — สลับบริษัท\n" : "") +
                   "• ผูก {รหัส} — เชื่อมบัญชีใหม่";
        }

        // 5) "บันทึก {vendor} {amount}" — quick expense.
        if (msg.StartsWith("บันทึก") || msg.StartsWith("จ่าย"))
        {
            var (vendor, amount) = ParseExpenseCommand(msg);
            if (vendor == null || amount == null || amount <= 0)
                return "📝 รูปแบบ: บันทึก {ร้าน} {จำนวน}\nเช่น: บันทึก เซเว่น 250";

            // Find or auto-create the vendor as a Supplier contact.
            var contact = await _db.Contacts.FirstOrDefaultAsync(c =>
                c.CompanyId == companyId && c.Name.ToLower() == vendor.ToLower() && c.IsSupplier);
            if (contact == null)
            {
                contact = new Contact { CompanyId = companyId, Name = vendor, IsSupplier = true, IsCustomer = false };
                _db.Contacts.Add(contact);
                await _db.SaveChangesAsync();
            }

            try
            {
                var doc = await _docService.CreateDocumentAsync(companyId, new CreateDocumentRequest(
                    DocumentType: Models.Enums.DocumentType.Expense,
                    DocumentDate: DateTime.UtcNow.Date,
                    DueDate: DateTime.UtcNow.Date,
                    ContactId: contact.Id,
                    Reference: "LINE-bot",
                    Notes: $"บันทึกผ่าน LINE: {msg}",
                    Lines: new List<DocumentLineRequest> {
                        new("ค่าใช้จ่ายจาก " + vendor, 1, "รายการ", amount.Value, 0, 0)
                    }
                ), createdBy: user.Email);
                try { await _docService.ApproveDocumentAsync(companyId, doc.Id, user.Email); } catch { /* show success even if approve hiccups */ }
                return $"✅ บันทึกแล้ว {vendor} {amount.Value:N2} ฿\nเลขที่เอกสาร: {doc.DocumentNumber}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LineBot expense create failed");
                return "❌ บันทึกไม่สำเร็จ: " + ex.Message;
            }
        }

        return "❓ ไม่เข้าใจคำสั่ง — ลองส่ง 'ช่วยเหลือ' เพื่อดูคำสั่งที่ใช้ได้";
    }

    /// <summary>Render the company picker as a numbered list reply. Caller
    /// passes a selector so we don't rely on reflection (which would break
    /// under AOT / trimming).</summary>
    private static string BuildCompanyMenu<T>(IReadOnlyList<T> companies, string title, Func<T, string> nameOf)
    {
        var lines = new System.Text.StringBuilder();
        lines.AppendLine(title);
        for (var i = 0; i < companies.Count; i++)
            lines.AppendLine($"  {i + 1}. {nameOf(companies[i])}");
        lines.Append("\nส่ง: เลือกบริษัท 1 (หรือเลขที่ต้องการ)");
        return lines.ToString();
    }

    /// <summary>Parse "บันทึก เซเว่น 250" → ("เซเว่น", 250).</summary>
    private static (string? Vendor, decimal? Amount) ParseExpenseCommand(string msg)
    {
        var tokens = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        if (tokens.Count == 0) return (null, null);
        // Amount is the LAST token that parses as a number (allows "บันทึก ร้านยา ไอเทมที่ 3 250").
        decimal? amt = null; int amtIdx = -1;
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var raw = new string(tokens[i].Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(",", "");
            if (!string.IsNullOrEmpty(raw) && decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v > 0)
            { amt = v; amtIdx = i; break; }
        }
        if (amt == null) return (null, null);
        var vendor = string.Join(' ', tokens.Take(amtIdx)).Trim();
        if (string.IsNullOrEmpty(vendor)) return (null, null);
        return (vendor, amt);
    }

    public async Task ReplyAsync(string lineUserId, string text)
    {
        var token = _config["Line:ChannelAccessToken"];
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("LINE ChannelAccessToken not configured — bot reply skipped.");
            return;
        }
        var client = _httpFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
        var payload = JsonSerializer.Serialize(new
        {
            to = lineUserId,
            messages = new[] { new { type = "text", text } }
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            var res = await client.PostAsync("https://api.line.me/v2/bot/message/push", content);
            if (!res.IsSuccessStatusCode)
                _logger.LogWarning("LINE push returned {Status} for {User}", res.StatusCode, lineUserId);
        }
        catch (Exception ex) { _logger.LogError(ex, "LINE push failed"); }
    }
}
