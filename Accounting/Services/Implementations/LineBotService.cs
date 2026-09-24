using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
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
    private readonly IPayslipLineDeliveryService _payslipLine;
    private readonly IOcrService _ocr;
    private readonly IOcrQuotaService _ocrQuota;
    private readonly IChatbotService _chat;

    public LineBotService(AccountingDbContext db, IConfiguration config,
        IHttpClientFactory httpFactory, ILogger<LineBotService> logger,
        IDocumentService docService, IPayslipLineDeliveryService payslipLine,
        IOcrService ocr, IOcrQuotaService ocrQuota, IChatbotService chat,
        IPermissionService perms, IChatRateLimiter rateLimiter, IEmailService email)
    {
        _db = db; _config = config; _httpFactory = httpFactory;
        _logger = logger; _docService = docService; _payslipLine = payslipLine;
        _ocr = ocr; _ocrQuota = ocrQuota; _chat = chat; _perms = perms;
        _rateLimiter = rateLimiter; _email = email;
    }
    private readonly IPermissionService _perms;
    private readonly IChatRateLimiter _rateLimiter;
    private readonly IEmailService _email;

    /// <summary>เพดานความพยายาม "ผูกบัญชี" ต่อ LINE user หนึ่งราย
    ///
    /// <para>⚠️ ที่มา (ผลตรวจทีม A · A-01): รหัสผูก 6 หลักถูกค้นแบบ **global**
    /// (query ไม่มีอะไรผูกกับผู้ส่งเลย) และ <c>LineBindCode</c> ไม่มีตัวนับความ
    /// พยายาม ⇒ เดาผิดไม่มีผลอะไร · ด่านเดียวที่เหลือคือ rate limit ชั้น HTTP ซึ่ง
    /// <c>/api/line-webhook</c> ตกชั้น anonymous <b>600 req/นาที</b> และคิดต่อ IP
    /// ซึ่งเป็น **IP ของ LINE** ทั้งหมด ⇒ ที่รหัสมีชีวิต 10 ใบ ต้องเดาราว 100,000
    /// ครั้ง = <b>~5.5 ชม.</b> ก็ยึดบัญชีคนอื่นได้ (เห็น P&amp;L · ถาม RAG ของบริษัท
    /// นั้น · สร้าง/อนุมัติเอกสาร)</para>
    ///
    /// <para>ที่ 10 ครั้ง/วัน การเดา 100,000 ครั้งใช้เวลา ~27 ปี · นับผ่าน
    /// <see cref="IChatRateLimiter"/> ซึ่งเป็น atomic upsert บน PostgreSQL จึงนับ
    /// ตรงข้าม instance (ห้ามใช้ dict ใน process ตามกฎ multi-instance)</para></summary>
    private const int BindAttemptsPerMinute = 3;
    private const int BindAttemptsPerDay = 10;

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

    private const string BindThrottledReply =
        "⛔ ลองผูกบัญชีบ่อยเกินไป — กรุณารอสักครู่แล้วลองใหม่\n"
        + "ถ้าไม่ได้เป็นคนลอง แปลว่ามีคนพยายามเดารหัสผูกบัญชีของคุณอยู่ "
        + "กรุณาแจ้งผู้ดูแลระบบ";

    /// <summary>นับความพยายามผูกบัญชีต่อ LINE user — fail-open เหมือน
    /// <see cref="IChatRateLimiter"/> (ตารางนับพัง ≠ เหตุผลที่จะปิดการผูกบัญชี
    /// ทั้งระบบ) แต่ทุกครั้งที่ชนเพดานจะถูกบันทึกเป็น strike ไว้ให้ตามรอย</summary>
    private async Task<bool> TryConsumeBindAttemptAsync(string lineUserId)
    {
        var key = "linebind:" + lineUserId;
        var ok = await _rateLimiter.TryConsumeAsync(key, BindAttemptsPerMinute, BindAttemptsPerDay);
        if (!ok)
        {
            await _rateLimiter.RecordStrikeAsync(key);
            _logger.LogWarning("ความพยายามผูกบัญชี LINE เกินเพดาน — lineUserId ลงท้าย {Tail}",
                lineUserId.Length > 6 ? lineUserId[^6..] : lineUserId);
        }
        return ok;
    }

    /// <summary>แจ้งเจ้าของบัญชีทางอีเมลว่ามี LINE ผูกเข้ามา — ห้ามเงียบ
    /// (ล้มก็ไม่ทำให้การผูกล้ม แต่ต้อง log ให้เห็น)</summary>
    private async Task NotifyLineBoundAsync(User user, string lineUserId)
    {
        if (string.IsNullOrWhiteSpace(user.Email)) return;
        var tail = lineUserId.Length > 6 ? lineUserId[^6..] : lineUserId;
        try
        {
            await _email.SendAsync(user.Email, "มีการเชื่อมบัญชี LINE เข้ากับบัญชีของคุณ",
                $"<p>บัญชี LINE (ลงท้าย <b>{System.Net.WebUtility.HtmlEncode(tail)}</b>) ถูกเชื่อมกับบัญชี "
                + $"<b>{System.Net.WebUtility.HtmlEncode(user.Email)}</b> เมื่อ "
                + $"{DateTime.UtcNow.AddHours(7):dd/MM/yyyy HH:mm} น. (เวลาไทย)</p>"
                + "<p>ถ้าไม่ได้ทำเอง กรุณาเข้าเว็บไซต์เพื่อ<b>ถอดการเชื่อมต่อ</b> "
                + "และเปลี่ยนรหัสผ่านทันที</p>");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ส่งอีเมลแจ้งการผูก LINE ไม่สำเร็จ (userId {UserId})", user.Id);
        }
    }

    public async Task<string?> HandleMessageAsync(string lineUserId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var msg = text.Trim();

        // 0) Employee payslip-bind — "สลิป 123456" / "ผูกสลิป 123456" ผูก LINE นี้
        // กับ "พนักงาน" (ไม่ใช่ผู้ใช้ระบบ) เพื่อรับสลิปเงินเดือน. ต้องเช็คก่อน
        // user-bind ด้านล่างเพราะ "ผูกสลิป" ก็ขึ้นต้นด้วย "ผูก".
        if (msg.StartsWith("สลิป", StringComparison.OrdinalIgnoreCase)
         || msg.StartsWith("ผูกสลิป", StringComparison.OrdinalIgnoreCase)
         || msg.StartsWith("payslip", StringComparison.OrdinalIgnoreCase))
        {
            var slipCode = new string(msg.Where(char.IsDigit).ToArray());
            if (slipCode.Length != 6)
                return "กรุณาส่ง: สลิป {รหัส 6 หลัก ที่ได้จากฝ่ายบุคคล}";
            if (!await TryConsumeBindAttemptAsync(lineUserId))
                return BindThrottledReply;
            var reply = await _payslipLine.TryBindFromLineAsync(lineUserId, slipCode);
            return reply ?? "❌ รหัสรับสลิปไม่ถูกต้องหรือหมดอายุ — กรุณาขอรหัสใหม่จากฝ่ายบุคคล";
        }

        // 1) Bind command — "ผูก 123456" links this LineUserId to a Next Acc account.
        if (msg.StartsWith("ผูก", StringComparison.OrdinalIgnoreCase)
         || msg.StartsWith("bind", StringComparison.OrdinalIgnoreCase))
        {
            var code = new string(msg.Where(char.IsDigit).ToArray());
            if (code.Length != 6) return "กรุณาส่ง: ผูก {รหัส 6 หลัก จากเว็บไซต์}";
            if (!await TryConsumeBindAttemptAsync(lineUserId))
                return BindThrottledReply;
            var row = await _db.LineBindCodes
                .Include(c => c.User)
                .Where(c => c.Code == code && c.UsedAt == null && c.ExpiresAt > DateTime.UtcNow)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync();
            if (row == null) return "❌ รหัสไม่ถูกต้องหรือหมดอายุ — กรุณาขอรหัสใหม่จากเว็บไซต์";
            row.UsedAt = DateTime.UtcNow;
            row.UsedByLineUserId = lineUserId;
            // LINE เดียวผูกได้กับบัญชีเดียว — ถอดของบัญชีอื่นที่ถือค่าเดิมก่อน
            // (คอลัมน์นี้เป็น index **ไม่ unique** ⇒ ถ้ามีสองแถวค่าเท่ากัน
            //  `FirstOrDefaultAsync` ที่ไม่มี OrderBy จะหยิบแถวไหนก็ได้ตามแผน
            //  query ⇒ บอททำงานในนามบัญชีที่ผู้ใช้ไม่ได้ตั้งใจ — ผลตรวจ E-07)
            await _db.Users
                .Where(u => u.LineUserId == lineUserId && u.Id != row.UserId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LineUserId, (string?)null));
            row.User.LineUserId = lineUserId;

            // การเปลี่ยนแปลงที่กระทบ "ใครเข้าบัญชีได้" ห้ามเงียบ (กฎเหล็ก #4)
            _db.AuditLogs.Add(new AuditLog
            {
                UserId = row.UserId,
                UserEmail = row.User.Email,
                Action = Models.Enums.AuditAction.Update,
                EntityType = "LineBindCode",
                EntityId = row.UserId.ToString(),
                NewValues = JsonSerializer.Serialize(new
                {
                    Bound = true,
                    // ห้าม log id เต็ม (เป็นตัวระบุตัวบุคคลฝั่ง LINE)
                    LineUserIdTail = lineUserId.Length > 6 ? lineUserId[^6..] : lineUserId,
                }),
            });
            await _db.SaveChangesAsync();
            await NotifyLineBoundAsync(row.User, lineUserId);
            return $"✅ เชื่อมต่อสำเร็จ — สวัสดี {row.User.FullName}\n" +
                   "ลองใช้งาน:\n" +
                   "• 📷 ส่งรูปใบเสร็จมาได้เลย — ระบบอ่านและสร้างเอกสารให้ทันที\n" +
                   "• บันทึก เซเว่น 250 — บันทึกค่าใช้จ่ายด้วยข้อความ\n" +
                   "• ถาม ค่าน้ำมันลงหมวดไหน — ปรึกษาผู้ช่วยบัญชี AI\n" +
                   "• ดูยอด — ดูเงินเข้า/ออกเดือนนี้\n" +
                   "• ช่วยเหลือ — ดูคำสั่งทั้งหมด";
        }

        // 2) Everything below requires a bound user.
        var user = await _db.Users.FirstOrDefaultAsync(u => u.LineUserId == lineUserId);
        if (user == null)
            return "👋 ยังไม่ได้เชื่อมต่อบัญชี — กรุณาขอรหัสจากเว็บไซต์ Next Acc แล้วส่งข้อความ\n" +
                   "ผูก {รหัส 6 หลัก}";
        // ⚠️ ด่านสถานะบัญชีต้องครอบ **ทุกทางเข้า** ไม่ใช่แค่เว็บ —
        // PayrollService ตั้ง Status=Inactive ให้อัตโนมัติเมื่อพนักงานลาออก
        // พร้อมคอมเมนต์ว่า "login + LIFF are revoked" แต่เส้น LINE ไม่เคย
        // อ่านค่านั้นเลย ⇒ คนที่ลาออกแล้วยังส่งใบเสร็จ/กดอนุมัติผ่าน LINE ได้
        // (control ที่ไม่มีใครเรียก = ไม่มี control)
        var gate = Accounting.Helpers.UserLoginPolicy.Evaluate(user.Status, false);
        if (!gate.Can) return "⛔ " + gate.Reason;

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

        var state = await GetOrCreateStateAsync(lineUserId, user.Id);
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
                   "• 📷 ส่งรูปใบเสร็จ/บิล (หรือไฟล์ PDF) — ระบบอ่านและสร้างเอกสารให้ทันที\n" +
                   "  บิลไม่มีเลขผู้เสียภาษี → ออกใบรับรองแทนใบเสร็จให้อัตโนมัติ\n" +
                   "• ถาม {คำถาม} — ผู้ช่วยบัญชี AI เช่น 'ถาม ค่าน้ำมันลงหมวดไหน'\n" +
                   "• บันทึก {ร้าน} {จำนวน} — เช่น 'บันทึก เซเว่น 250'\n" +
                   "• ดูยอด — เงินเข้า/ออกเดือนนี้\n" +
                   (companies.Count > 1 ? "• เลือกบริษัท {เลข} — สลับบริษัท\n" : "") +
                   "• ผูก {รหัส} — เชื่อมบัญชีใหม่";
        }

        // 4b) "ถาม {คำถาม}" — ผู้ช่วยบัญชีตัวเดียวกับในเว็บ (RAG ของบริษัทนี้)
        //     ต้องเช็คก่อน "บันทึก" ไม่ได้ชนกัน แต่วางไว้ก่อนเพื่อความชัด
        if (msg.StartsWith("ถาม", StringComparison.OrdinalIgnoreCase)
         || msg.StartsWith("ask ", StringComparison.OrdinalIgnoreCase))
        {
            var q = msg.StartsWith("ถาม") ? msg[3..].Trim() : msg[4..].Trim();
            if (string.IsNullOrWhiteSpace(q))
                return "พิมพ์: ถาม {คำถาม}\nเช่น \"ถาม ค่าน้ำมันรถส่งของลงหมวดไหน\"";
            try
            {
                var res = await _chat.AskTenantAsync(companyId, user.Id, user.Email, q, null, default);
                return (res.UsedAi ? "🤖 " : "⚙️ ") + res.Answer;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LINE assistant ask failed");
                return "❌ ตอบไม่ได้ตอนนี้ — ลองใหม่อีกครั้ง หรือถามในเว็บที่เมนู \"ผู้ช่วยบัญชี AI\"";
            }
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
                        // Positional order: Description, Quantity, Unit, UnitPrice,
                        // DiscountPercent, VatRate, WithholdingTaxRate, AccountId
                        // (rest are optional with defaults).
                        new("ค่าใช้จ่ายจาก " + vendor, 1, "รายการ", amount.Value, 0, 0, 0, null)
                    }
                ), createdBy: user.Email);
                // ด่านสิทธิ์เดียวกับเว็บ/postback — เดิมเส้นข้อความนี้อนุมัติอัตโนมัติให้ทุกสมาชิก
                // (Viewer พิมพ์ "จ่าย xxx 99999" ⇒ Expense Approved + JE) และ `catch {}` กลืน error
                // แล้วตอบ ✅ พร้อมเลข DRAFT- (ERP_REVIEW G-03)
                if (!await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, user.Id, Models.Enums.DocumentType.Expense))
                    return $"📝 บันทึกเป็นฉบับร่างแล้ว {vendor} {amount.Value:N2} ฿ — สิทธิ์ของคุณอนุมัติไม่ได้ แจ้งเจ้าของกิจการ/นักบัญชีอนุมัติในระบบ";
                try
                {
                    var approved = await _docService.ApproveDocumentAsync(companyId, doc.Id, user.Email);
                    return $"✅ บันทึกและอนุมัติแล้ว {vendor} {amount.Value:N2} ฿\nเลขที่เอกสาร: {approved.DocumentNumber}";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "LINE บันทึกค่าใช้จ่าย: สร้างร่างแล้วแต่อนุมัติไม่ผ่าน doc {DocId}", doc.Id);
                    return $"📝 บันทึกเป็นฉบับร่างแล้ว {vendor} {amount.Value:N2} ฿ แต่อนุมัติไม่ผ่าน: {ex.Message}\nเปิดในระบบเพื่อแก้แล้วอนุมัติ";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LineBot expense create failed");
                return "❌ บันทึกไม่สำเร็จ: " + ex.Message;
            }
        }

        return "❓ ไม่เข้าใจคำสั่ง — ลองส่ง 'ช่วยเหลือ' เพื่อดูคำสั่งที่ใช้ได้";
    }

    /// <summary>Upsert the per-LINE-user state row. Two webhook events arriving
    /// simultaneously for a new user would both see "null" and both try to
    /// INSERT, hitting the UNIQUE index on LineUserId. We catch the race and
    /// re-read on the second attempt.</summary>
    private async Task<LineUserState> GetOrCreateStateAsync(string lineUserId, Guid userId)
    {
        var state = await _db.LineUserStates.FirstOrDefaultAsync(s => s.LineUserId == lineUserId);
        if (state == null)
        {
            state = new LineUserState { LineUserId = lineUserId, UserId = userId };
            _db.LineUserStates.Add(state);
            try { await _db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                _db.Entry(state).State = EntityState.Detached;
                state = await _db.LineUserStates.FirstAsync(s => s.LineUserId == lineUserId);
            }
        }
        return state;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  "โยนบิลเข้าไลน์" — รูปใบเสร็จ/ไฟล์ PDF → OCR → เอกสาร (ฉบับร่าง) ทันที
    //
    //  ใช้ pipeline เดียวกับหน้าเว็บทุกขั้น (กฎ: ห้ามมี path คู่ขนาน):
    //  preflight คุณภาพไฟล์ → dedup ด้วย SHA-256 → โควต้า OCR → ScanAsync
    //  (autoCreate=true) ซึ่งมี safety gate ครบอยู่แล้ว — confidence ≥ 0.85,
    //  ครบ field สำคัญ (วันที่/เลขที่/ยอด), ระงับเมื่อเจอลายมือ/สินทรัพย์,
    //  กันใบซ้ำ. เอกสารที่สร้างเป็น "ฉบับร่าง" เสมอ — เลขจริงออกตอน Approve
    //  (กฎเหล็ก #3: ระบบกรอกครบ ผู้ใช้แค่ยืนยัน — ไม่ใช่ข้ามการยืนยัน)
    // ═══════════════════════════════════════════════════════════════════
    public async Task<string?> HandleImageAsync(string lineUserId, string messageId, string? fileName = null)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.LineUserId == lineUserId);
        if (user == null)
            return "👋 ได้รับรูปแล้ว แต่ยังไม่ได้เชื่อมต่อบัญชี — กรุณาขอรหัสจากเว็บไซต์ Next Acc แล้วส่งข้อความ\nผูก {รหัส 6 หลัก}\nจากนั้นส่งรูปใบเสร็จมาอีกครั้ง";
        // ⚠️ ด่านสถานะบัญชีต้องครอบ **ทุกทางเข้า** ไม่ใช่แค่เว็บ —
        // PayrollService ตั้ง Status=Inactive ให้อัตโนมัติเมื่อพนักงานลาออก
        // พร้อมคอมเมนต์ว่า "login + LIFF are revoked" แต่เส้น LINE ไม่เคย
        // อ่านค่านั้นเลย ⇒ คนที่ลาออกแล้วยังส่งใบเสร็จ/กดอนุมัติผ่าน LINE ได้
        // (control ที่ไม่มีใครเรียก = ไม่มี control)
        var gate = Accounting.Helpers.UserLoginPolicy.Evaluate(user.Status, false);
        if (!gate.Can) return "⛔ " + gate.Reason;

        var companies = await _db.CompanyUsers
            .Where(cu => cu.UserId == user.Id)
            .Include(cu => cu.Company)
            .Select(cu => new { cu.CompanyId, Name = cu.Company.Name })
            .ToListAsync();
        if (companies.Count == 0) return "❌ ไม่พบบริษัทที่ผูกกับบัญชีนี้";

        var state = await GetOrCreateStateAsync(lineUserId, user.Id);
        state.LastInteractionAt = DateTime.UtcNow;

        Guid companyId;
        if (companies.Count == 1)
        {
            companyId = companies[0].CompanyId;
            state.ActiveCompanyId = companyId;
            await _db.SaveChangesAsync();
        }
        else if (state.ActiveCompanyId.HasValue && companies.Any(c => c.CompanyId == state.ActiveCompanyId.Value))
        {
            companyId = state.ActiveCompanyId.Value;
            await _db.SaveChangesAsync();
        }
        else
        {
            await _db.SaveChangesAsync();
            return BuildCompanyMenu(companies,
                "📷 ได้รับรูปแล้ว — แต่คุณมีหลายบริษัท กรุณาเลือกบริษัทก่อน แล้วส่งรูปอีกครั้ง:",
                c => c.Name);
        }

        // ── 1) ดึงไฟล์จริงจาก LINE Content API ──
        var token = _config["Line:ChannelAccessToken"];
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("LINE ChannelAccessToken not configured — image intake skipped");
            return "❌ ระบบยังไม่ได้ตั้งค่า LINE — แจ้งผู้ดูแลระบบ";
        }
        byte[] bytes; string contentType;
        try
        {
            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
            client.Timeout = TimeSpan.FromSeconds(30);
            using var res = await client.GetAsync($"https://api-data.line.me/v2/bot/message/{messageId}/content");
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("LINE content download returned {Status} for message {Id}", res.StatusCode, messageId);
                return "❌ ดึงไฟล์จาก LINE ไม่สำเร็จ — กรุณาส่งใหม่อีกครั้ง";
            }
            bytes = await res.Content.ReadAsByteArrayAsync();
            contentType = res.Content.Headers.ContentType?.MediaType
                ?? (fileName != null && fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? "application/pdf" : "image/jpeg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LINE content download failed for message {Id}", messageId);
            return "❌ ดึงไฟล์จาก LINE ไม่สำเร็จ — กรุณาส่งใหม่อีกครั้ง";
        }
        if (bytes.Length == 0) return "❌ ไฟล์ว่าง — กรุณาส่งใหม่";
        if (bytes.Length > 10 * 1024 * 1024) return "❌ ไฟล์ใหญ่เกิน 10MB — ถ่ายรูปส่งแทนได้ครับ";

        var ext = contentType switch
        {
            "application/pdf" => ".pdf",
            "image/png" => ".png",
            _ => ".jpg",
        };
        var effectiveName = string.IsNullOrWhiteSpace(fileName) ? $"line-{messageId}{ext}" : fileName;

        // ── 2) ตรวจคุณภาพไฟล์ก่อนตัดโควต้า — รูปเบลอ/เล็กเกินไม่ควรเสียเครดิต ──
        var preflight = Ocr.OcrPreprocessor.Check(bytes, contentType, effectiveName);
        if (!preflight.Ok)
            return "❌ " + (preflight.ErrorMessage ?? "ไฟล์ไม่ผ่านการตรวจสอบ")
                + "\n💡 เคล็ดลับ: ถ่ายให้เห็นทั้งใบ แสงสว่างพอ ไม่เบลอ แล้วส่งใหม่";

        // ── 3) กันส่งซ้ำ (LINE ชอบเผลอกดส่ง 2 ครั้ง) — hash เดิมใน 10 นาที ──
        var fileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var recent = await _db.Set<OcrScanResult>()
            .Where(r => r.CompanyId == companyId && r.FileHash == fileHash
                && r.CreatedAt >= DateTime.UtcNow.AddMinutes(-10))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id, r.CreatedDocumentId })
            .FirstOrDefaultAsync();
        if (recent != null)
        {
            if (recent.CreatedDocumentId.HasValue)
            {
                var dupDoc = await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == recent.CreatedDocumentId.Value)
                    .Select(d => d.DocumentNumber).FirstOrDefaultAsync();
                return $"⚠️ รูปนี้เพิ่งส่งไปแล้ว — สร้างเอกสาร {dupDoc} ให้แล้วก่อนหน้านี้ (ไม่บันทึกซ้ำ)";
            }
            return "⚠️ รูปนี้เพิ่งส่งไปแล้ว — กำลังอยู่ในคิวตรวจสอบ (ไม่บันทึกซ้ำ)";
        }

        // ── 4) โควต้า OCR (atomic check-and-decrement เหมือนหน้าเว็บ) ──
        if (!await _ocrQuota.TryConsumeAsync(companyId))
        {
            var status = await _ocrQuota.GetQuotaStatusAsync(companyId);
            return $"❌ โควต้า OCR เดือนนี้หมด ({status.UsedThisMonth}/{status.MaxPagesPerMonth} หน้า)\n"
                + "ซื้อเครดิตเพิ่มได้ที่เว็บไซต์ หรือบันทึกด้วยข้อความ: บันทึก {ร้าน} {จำนวน}";
        }

        // ── 5) เก็บไฟล์ + FileAttachment (ที่เดียวกับ upload หน้าเว็บ) ──
        Guid attachmentId;
        try
        {
            var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "ocr");
            Directory.CreateDirectory(uploadsDir);
            var storedName = $"{Guid.NewGuid()}{ext}";
            var filePath = Path.Combine(uploadsDir, storedName);
            await File.WriteAllBytesAsync(filePath, bytes);

            var attachment = new FileAttachment
            {
                CompanyId = companyId,
                FileName = storedName,
                OriginalFileName = effectiveName,
                ContentType = contentType,
                FileSize = bytes.Length,
                StoragePath = filePath,
                EntityType = "OcrScan",
                EntityId = Guid.NewGuid(),
                UploadedByUserId = user.Id,
            };
            _db.FileAttachments.Add(attachment);
            await _db.SaveChangesAsync();
            attachmentId = attachment.Id;
        }
        catch (Exception ex)
        {
            await _ocrQuota.RefundAsync(companyId);
            _logger.LogError(ex, "LINE image save failed for company {CompanyId}", companyId);
            return "❌ บันทึกไฟล์ไม่สำเร็จ — ลองใหม่อีกครั้ง";
        }

        // ── 6) OCR + auto-create (ฉบับร่าง) — path เดียวกับหน้าเว็บ ──
        // metadata ฝัง lineUserId ไว้กับ scan → ตอนนักบัญชีอนุมัติบนเว็บ
        // ApproveDocumentAsync จะแจ้งกลับผู้ส่งบิลทางไลน์ได้ (ปิดลูปให้ผู้ส่ง
        // รู้ผลโดยไม่ต้องเปิดแอป)
        var lineMeta = JsonSerializer.Serialize(new { sourceChannel = "line", lineUserId });
        Models.DTOs.Ocr.OcrResultResponse result;
        try
        {
            // ส่ง actingUserId เสมอ — เส้นนี้สร้างเอกสารเองโดยไม่ผ่าน
            // /create-document ที่มีด่านสิทธิ์ ⇒ ด่านต้องอยู่ในบริการ (E-02)
            result = await _ocr.ScanAsync(companyId, attachmentId, null, lineMeta,
                autoCreate: true, actingUserId: user.Id);
        }
        catch (Exception ex)
        {
            await _ocrQuota.RefundAsync(companyId);
            _logger.LogError(ex, "LINE OCR scan failed for company {CompanyId}", companyId);
            return "❌ อ่านเอกสารไม่สำเร็จ — ลองถ่ายใหม่ให้ชัดขึ้น หรืออัปโหลดผ่านหน้าเว็บ";
        }
        // คืนโควต้าตามกติกาเดียวกับ upload หน้าเว็บ: งานซ้ำ/ล้มเหลว/e-Tax XML
        // (ไม่ได้ใช้ OCR engine จริง) ไม่ควรเสียเครดิต
        if (result.IsDuplicate || result.ScanStatus != "Completed" || result.OcrEngine == "EtaxXml")
            await _ocrQuota.RefundAsync(companyId);

        // สร้างเอกสารสำเร็จ → ส่งการ์ด Flex พร้อมปุ่ม "อนุมัติเลย" กดจบในแชท
        // (ส่งไม่ผ่านก็ตกลงมาใช้ข้อความธรรมดา — ผู้ใช้ต้องได้คำตอบเสมอ)
        if (result.CreatedDocumentId.HasValue && !result.IsDuplicate
            && await TrySendCreatedDocFlexAsync(lineUserId, result))
            return null;

        return await BuildScanReplyAsync(companyId, result);
    }

    /// <summary>การ์ดสรุปเอกสารที่สร้าง + ปุ่ม "✅ อนุมัติเลย" (postback) และ
    /// "🔍 ตรวจ/แก้ไข" (ลิงก์หน้า review). คืน false เมื่อส่งไม่สำเร็จเพื่อให้
    /// caller ตกลงมาใช้ text reply.</summary>
    private async Task<bool> TrySendCreatedDocFlexAsync(string lineUserId, Models.DTOs.Ocr.OcrResultResponse r)
    {
        try
        {
            var doc = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == r.CreatedDocumentId!.Value)
                .Select(d => new { d.DocumentType, d.TotalAmount, d.VatAmount })
                .FirstOrDefaultAsync();
            if (doc == null) return false;

            var baseUrl = (await _db.SiteSettings.AsNoTracking()
                .Select(s => s.AppBaseUrl).FirstOrDefaultAsync())?.TrimEnd('/');
            var vendor = string.IsNullOrWhiteSpace(r.ExtractedVendorName) ? "ไม่ทราบผู้ขาย" : r.ExtractedVendorName;
            var isCert = doc.DocumentType == Models.Enums.DocumentType.CertificateInLieu;

            var bodyRows = new List<object>
            {
                new { type = "text", text = ThaiDocTypeName(doc.DocumentType), weight = "bold", size = "lg", color = "#1e40af" },
                new { type = "text", text = "ฉบับร่าง — เลขที่จริงออกตอนอนุมัติ", size = "xs", color = "#94a3b8" },
                new { type = "text", text = vendor!, size = "md", margin = "md", wrap = true },
                new
                {
                    type = "box", layout = "baseline", margin = "sm", contents = new object[]
                    {
                        new { type = "text", text = "ยอดรวม", color = "#666666", size = "sm", flex = 0 },
                        new { type = "text", text = $"฿{doc.TotalAmount:N2}", weight = "bold", size = "xl", align = "end" },
                    }
                },
            };
            if (doc.VatAmount > 0)
                bodyRows.Add(new { type = "text", text = $"VAT {doc.VatAmount:N2} ฿", size = "sm", color = "#666666" });
            if (isCert)
                bodyRows.Add(new
                {
                    type = "text", size = "xs", color = "#b45309", wrap = true, margin = "md",
                    text = "📌 บิลไม่มีเลขผู้เสียภาษี — ออกใบรับรองแทนใบเสร็จรับเงินให้ พร้อมแนบรูปเป็นหลักฐาน (§65 ตรี)",
                });

            // ── คำเตือนที่เซิร์ฟเวอร์ตัดสินไว้แล้ว ต้องอยู่บนการ์ดด้วย ──────────
            // ⚠️ ที่มา (ผลตรวจ 2026-09-06 · T4-15): การ์ดนี้ให้ "อนุมัติ 1 แตะ" โดย
            // **ไม่แสดง §82/5 / Σ-GAP / หน้าที่หัก ณ ที่จ่าย เลยสักบรรทัด** ⇒ ผู้ใช้
            // LINE ตัดสินใจจากข้อมูลน้อยกว่าผู้ใช้เว็บบนกระดาษใบเดียวกัน
            var lineWarnings = OcrScanWarnings(r.ProcessingNotes);
            // ผลตรวจ §86/4 ที่เซิร์ฟเวอร์คำนวณแล้ว (OcrScanComplianceEvaluator)
            // ต้องอยู่บนการ์ดด้วย ไม่ใช่เห็นเฉพาะบนเว็บ
            foreach (var ci in r.ComplianceIssues ?? new List<Models.DTOs.Ocr.OcrScanIssueDto>())
                lineWarnings.Add(ci.Message);
            foreach (var w in lineWarnings.Take(3))
                bodyRows.Add(new
                {
                    type = "text", size = "xs", color = "#b45309", wrap = true, margin = "sm",
                    text = "⚠️ " + w,
                });
            // ตัดคำเตือนทิ้งเงียบ ๆ ไม่ได้ — ต้องบอกว่ายังมีอีกกี่ข้อและดูที่ไหน
            if (lineWarnings.Count > 3)
                bodyRows.Add(new
                {
                    type = "text", size = "xs", color = "#b45309", wrap = true, margin = "sm",
                    text = $"⚠️ …และอีก {lineWarnings.Count - 3} ข้อ — เปิดหน้าเว็บเพื่อดูทั้งหมด",
                });

            // ตัวตัดสิน "พร้อมลงบัญชีเองไหม" ตัวเดียวกับเว็บ (Helpers/OcrPostingReadiness)
            //
            // ⚠️ เดิมส่งแค่ ProcessingNotes + มีวันที่ไหม ⇒ **ไม่เคยอ่าน
            // `ComplianceIssues` เลย** ทั้งที่ค่ามาถึงในมือแล้ว (ScanAsync คืน
            // ผ่าน AttachComplianceAsync) ⇒ ใบที่เว็บ**ไม่มีแม้ช่องให้ติ๊ก**
            // ("ผู้ซื้อไม่ใช่บริษัทนี้ — อาจเป็นเอกสารของบริษัทอื่น") บน LINE
            // ขึ้นปุ่มเขียวให้กดลง JE + เข้ารายงานภาษีซื้อ (ผลตรวจทีม E · E-03)
            var readiness = Accounting.Helpers.OcrPostingReadiness.Evaluate(
                r.ProcessingNotes, r.ExtractedDate != null,
                r.ComplianceIssues?.Select(i => i.Severity),
                r.Quality?.Letter);

            var buttons = new List<object>();
            if (readiness.CanAutoApprove)
                buttons.Add(new
                {
                    type = "button", style = "primary", color = "#16a34a", height = "sm",
                    action = new
                    {
                        type = "postback", label = "✅ อนุมัติเลย",
                        data = $"approve:{r.CreatedDocumentId!.Value}",
                        displayText = "อนุมัติเอกสาร",
                    }
                });
            else
                bodyRows.Add(new
                {
                    type = "text", size = "xs", color = "#b91c1c", wrap = true, margin = "md",
                    text = "⛔ ยังอนุมัติจากแชทไม่ได้ — " + readiness.Reason,
                });
            if (!string.IsNullOrEmpty(baseUrl))
                buttons.Add(new
                {
                    type = "button", style = "secondary", height = "sm",
                    action = new
                    {
                        type = "uri", label = "🔍 ตรวจ / แก้ไขก่อน",
                        uri = $"{baseUrl}/pages/document-scan.html?reviewScan={r.Id}",
                    }
                });

            // Flex ที่ไม่มีปุ่มเลย = การ์ดที่กดอะไรไม่ได้ → ตกไปใช้ข้อความธรรมดา
            // (ผู้ใช้ต้องได้คำตอบเสมอ และต้องมีทางไปต่อ)
            if (buttons.Count == 0) return false;

            var bubble = new
            {
                type = "bubble",
                body = new { type = "box", layout = "vertical", spacing = "sm", contents = bodyRows.ToArray() },
                footer = new { type = "box", layout = "vertical", spacing = "sm", contents = buttons.ToArray() },
            };
            return await PushFlexAsync(lineUserId,
                $"สร้าง{ThaiDocTypeName(doc.DocumentType)} {vendor} ฿{doc.TotalAmount:N2} — กดอนุมัติได้เลย", bubble);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LINE flex card failed — falling back to text reply");
            return false;
        }
    }

    /// <summary>คำเตือนจากแท็กที่เซิร์ฟเวอร์ใส่ไว้ใน <c>ProcessingNotes</c> —
    /// ใช้แท็กชุดเดียวกับหน้าเว็บ (<c>Page.parseScanNotes</c>) เพื่อไม่ให้สองช่องทาง
    /// เห็นข้อมูลคนละชุดบนกระดาษใบเดียวกัน</summary>
    internal static List<string> OcrScanWarnings(string? processingNotes)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(processingNotes)) return result;
        // [WHT-CERT] เคยตกหล่นจากลิสต์นี้ทั้งที่เป็น blocking tag ใน
        // OcrPostingReadiness ⇒ ปุ่มอนุมัติหายโดยไม่มีคำเตือนอธิบายว่าทำไม
        string[] tags = { "[VAT-CLAIM]", "[Σ-GAP]", "[WHT-SUGGEST]", "[WHT-CERT]", "[TAX-INV-PENDING]", "[PP36]", "[DATE-UNKNOWN]", "[FX-UNKNOWN]" };
        foreach (var line in processingNotes.Split('\n'))
        {
            var t = line.Trim();
            foreach (var tag in tags)
            {
                if (!t.StartsWith(tag, StringComparison.Ordinal)) continue;
                var text = t[tag.Length..].Trim();
                if (text.Length > 0) result.Add(text.Length > 160 ? text[..160] + "…" : text);
                break;
            }
        }
        return result;
    }

    /// <summary>postback จากปุ่มใน Flex card — "approve:{documentId}".
    /// data ปลอมได้ (ไม่ได้มาจาก UI เสมอ) → ตรวจ binding + tenant + role
    /// ทุกครั้งก่อนแตะเอกสาร.</summary>
    public async Task<string?> HandlePostbackAsync(string lineUserId, string data)
    {
        if (string.IsNullOrWhiteSpace(data) || !data.StartsWith("approve:")) return null;
        if (!Guid.TryParse(data["approve:".Length..], out var docId))
            return null;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.LineUserId == lineUserId);
        if (user == null)
            return "👋 ยังไม่ได้เชื่อมต่อบัญชี — ผูกบัญชีก่อนแล้วลองใหม่ (ส่ง: ผูก {รหัส 6 หลัก})";
        // ⚠️ ด่านสถานะบัญชีต้องครอบ **ทุกทางเข้า** ไม่ใช่แค่เว็บ —
        // PayrollService ตั้ง Status=Inactive ให้อัตโนมัติเมื่อพนักงานลาออก
        // พร้อมคอมเมนต์ว่า "login + LIFF are revoked" แต่เส้น LINE ไม่เคย
        // อ่านค่านั้นเลย ⇒ คนที่ลาออกแล้วยังส่งใบเสร็จ/กดอนุมัติผ่าน LINE ได้
        // (control ที่ไม่มีใครเรียก = ไม่มี control)
        var gate = Accounting.Helpers.UserLoginPolicy.Evaluate(user.Status, false);
        if (!gate.Can) return "⛔ " + gate.Reason;

        var doc = await _db.Documents.AsNoTracking()
            .Where(d => d.Id == docId && !d.IsDeleted)
            .Select(d => new { d.Id, d.CompanyId, d.Status, d.DocumentNumber, d.DocumentType, d.TotalAmount })
            .FirstOrDefaultAsync();
        if (doc == null) return "❌ ไม่พบเอกสาร — อาจถูกลบไปแล้ว";

        // tenant guard: ต้องเป็นสมาชิกบริษัทเจ้าของเอกสาร
        var membership = await _db.CompanyUsers.AsNoTracking()
            .FirstOrDefaultAsync(cu => cu.CompanyId == doc.CompanyId && cu.UserId == user.Id);
        if (membership == null) return "❌ คุณไม่มีสิทธิ์ในบริษัทของเอกสารนี้";

        // ด่านสิทธิ์ตัวเดียวกับเว็บ (DocumentPermissionHelper) — เดิมเป็นสำเนามือของ role list
        // ⇒ custom role ที่ได้ Document.Approve อนุมัติในเว็บได้แต่ LINE ปฏิเสธ · Accountant ที่ถูก
        // ถอดสิทธิ์ยังอนุมัติผ่าน LINE ได้ · ไม่แยกทิศซื้อ/ขาย (ERP_REVIEW G-04)
        if (!await DocumentPermissionHelper.CanApproveAsync(_perms, doc.CompanyId, user.Id, doc.DocumentType))
            return "❌ สิทธิ์ของคุณอนุมัติเอกสารไม่ได้ — เอกสารบันทึกเป็นฉบับร่างไว้แล้ว แจ้งเจ้าของกิจการ/นักบัญชีให้อนุมัติในระบบ";

        if (doc.Status is not (Models.Enums.DocumentStatus.Draft or Models.Enums.DocumentStatus.WaitingApproval))
        {
            var statusTh = doc.Status switch
            {
                Models.Enums.DocumentStatus.Approved => "อนุมัติแล้ว",
                Models.Enums.DocumentStatus.Paid => "ชำระแล้ว",
                Models.Enums.DocumentStatus.Voided => "ถูกยกเลิก",
                _ => doc.Status.ToString(),
            };
            return $"ℹ️ เอกสาร {doc.DocumentNumber} {statusTh}ไปแล้ว — ไม่ต้องทำซ้ำ";
        }

        // ด่าน "พร้อมลงบัญชีเองไหม" ต้องตรวจซ้ำ**ตอนกด** ไม่ใช่เชื่อการ์ดที่วาดไว้ก่อน (รอบ 190 ข้อ 9)
        // — postback data ปลอม/ส่งซ้ำได้ (doc-comment ข้างบน) และการ์ดเก่าในแชทยังกดได้หลังผลสแกน
        // ถูกตรวจใหม่ ⇒ เอกสารที่มาจากสแกนต้องผ่านตัวตัดสินตัวเดียวกับเว็บ (Helpers/OcrPostingReadiness)
        // เช่น [Σ-GAP] "ยอดที่สร้างไม่ตรงกระดาษ" ห้ามลง JE จากแชทเงียบ ๆ · เอกสารที่ไม่ได้มาจากสแกน
        // ไม่มีแถวให้ตรวจ = ข้ามด่านนี้ (ไม่ใช่ "ไม่ผ่าน")
        var scanGate = await _db.Set<OcrScanResult>().AsNoTracking()
            .Where(r => r.CompanyId == doc.CompanyId && r.CreatedDocumentId == doc.Id)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.ProcessingNotes, r.ExtractedDate })
            .FirstOrDefaultAsync();
        if (scanGate != null)
        {
            var readiness = Accounting.Helpers.OcrPostingReadiness.Evaluate(
                scanGate.ProcessingNotes, scanGate.ExtractedDate != null);
            if (!readiness.CanAutoApprove)
                return "⛔ ยังอนุมัติจากแชทไม่ได้ — " + readiness.Reason;
        }

        try
        {
            await _docService.ApproveDocumentAsync(doc.CompanyId, doc.Id, user.Email);
            var number = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == doc.Id).Select(d => d.DocumentNumber).FirstOrDefaultAsync();
            return $"✅ อนุมัติแล้ว — {ThaiDocTypeName(doc.DocumentType)} เลขที่ {number}\n"
                + $"💰 {doc.TotalAmount:N2} ฿ ลงบัญชี + รายงานภาษีให้เรียบร้อย";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LINE postback approve failed for doc {DocId}", doc.Id);
            return "❌ อนุมัติไม่สำเร็จ: " + ex.Message + "\nเปิดดู/แก้ไขได้ที่หน้าเว็บ";
        }
    }

    /// <summary>push flex ผ่าน channel ของบอทเอง (token เดียวกับ ReplyAsync) —
    /// ผู้ใช้แชทกับบอทช่องนี้ ใช้ token บริษัท (LINE Notify OA) จะส่งไม่ถึง.</summary>
    private async Task<bool> PushFlexAsync(string lineUserId, string altText, object contents)
    {
        var token = _config["Line:ChannelAccessToken"];
        if (string.IsNullOrEmpty(token)) return false;
        var client = _httpFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
        var payload = JsonSerializer.Serialize(new
        {
            to = lineUserId,
            messages = new object[] { new { type = "flex", altText, contents } }
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            var res = await client.PostAsync("https://api.line.me/v2/bot/message/push", content);
            if (!res.IsSuccessStatusCode)
                _logger.LogWarning("LINE flex push returned {Status} for {User}", res.StatusCode, lineUserId);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LINE flex push failed");
            return false;
        }
    }

    /// <summary>สรุปผลสแกนเป็นข้อความ LINE ที่คนไม่ใช่นักบัญชีอ่านรู้เรื่อง —
    /// บอกว่าระบบทำอะไรให้แล้ว เหลืออะไรให้ผู้ใช้ทำ พร้อมลิงก์ที่กดต่อได้เลย.</summary>
    private async Task<string> BuildScanReplyAsync(Guid companyId, Models.DTOs.Ocr.OcrResultResponse r)
    {
        var baseUrl = (await _db.SiteSettings.AsNoTracking()
            .Select(s => s.AppBaseUrl).FirstOrDefaultAsync())?.TrimEnd('/');
        var reviewLink = string.IsNullOrEmpty(baseUrl)
            ? null : $"{baseUrl}/pages/document-scan.html?reviewScan={r.Id}";

        if (r.IsDuplicate)
            return "⚠️ เอกสารนี้เคยบันทึกไปแล้ว (เลขที่/ผู้ขาย/ยอดตรงกับใบเดิม) — ระบบกันบันทึกซ้ำให้"
                + (reviewLink != null ? $"\n🔍 ตรวจสอบ: {reviewLink}" : "");

        if (r.ScanStatus != "Completed")
            return "❌ อ่านเอกสารไม่สำเร็จ — ลองถ่ายใหม่: เห็นทั้งใบ แสงพอ ไม่เบลอ"
                + (reviewLink != null ? $"\nหรือกรอกเองที่: {reviewLink}" : "");

        var vendor = string.IsNullOrWhiteSpace(r.ExtractedVendorName) ? "ไม่ทราบผู้ขาย" : r.ExtractedVendorName;
        var total = r.ExtractedTotalAmount ?? 0m;

        if (r.CreatedDocumentId.HasValue)
        {
            var doc = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == r.CreatedDocumentId.Value)
                .Select(d => new { d.DocumentNumber, d.DocumentType, d.TotalAmount, d.VatAmount })
                .FirstOrDefaultAsync();
            if (doc != null)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"✅ สร้าง{ThaiDocTypeName(doc.DocumentType)}ให้แล้ว (ฉบับร่าง)");
                sb.AppendLine($"🏪 {vendor}");
                sb.Append($"💰 ยอดรวม {doc.TotalAmount:N2} ฿");
                if (doc.VatAmount > 0) sb.Append($" (VAT {doc.VatAmount:N2})");
                sb.AppendLine();
                if (doc.DocumentType == Models.Enums.DocumentType.CertificateInLieu)
                    sb.AppendLine("📌 บิลนี้ไม่มีเลขผู้เสียภาษี — ระบบออก \"ใบรับรองแทนใบเสร็จรับเงิน\" พร้อมแนบรูปเป็นหลักฐาน ให้ใช้เป็นรายจ่ายทางภาษีได้ตามแนวทางกรมสรรพากร (§65 ตรี)");
                sb.Append(reviewLink != null
                    ? $"👉 ตรวจและอนุมัติ: {reviewLink}"
                    : "👉 ตรวจและอนุมัติได้ที่หน้า \"สแกนเอกสาร\" บนเว็บ");
                return sb.ToString();
            }
        }

        // อ่านได้แต่ยังไม่สร้างอัตโนมัติ — safety gate ระงับไว้ (confidence ต่ำ /
        // field ขาด / ลายมือ / สินทรัพย์) → ชี้ไปหน้า review ที่ pre-fill ให้แล้ว
        var summary = total > 0 ? $"{vendor} · ยอด {total:N2} ฿" : vendor;
        return $"📋 อ่านข้อมูลได้แล้ว: {summary}\n"
            + "ระบบขอให้ยืนยันก่อนสร้างเอกสาร (ข้อมูลบางส่วนไม่ชัดหรือต้องตรวจ)\n"
            + (reviewLink != null
                ? $"👉 กดยืนยันที่นี่ (กรอกให้แล้ว): {reviewLink}"
                : "👉 ไปที่หน้า \"สแกนเอกสาร\" บนเว็บเพื่อยืนยัน");
    }

    private static string ThaiDocTypeName(Models.Enums.DocumentType t) => t switch
    {
        Models.Enums.DocumentType.Expense => "บันทึกค่าใช้จ่าย",
        Models.Enums.DocumentType.PaymentVoucher => "ใบสำคัญจ่าย",
        Models.Enums.DocumentType.CertificateInLieu => "ใบรับรองแทนใบเสร็จรับเงิน",
        Models.Enums.DocumentType.PurchaseInvoice => "ใบแจ้งหนี้ซื้อ",
        Models.Enums.DocumentType.PurchaseOrder => "ใบสั่งซื้อ",
        Models.Enums.DocumentType.Receipt => "ใบเสร็จรับเงิน",
        Models.Enums.DocumentType.ReceiptVoucher => "ใบสำคัญรับเงิน",
        Models.Enums.DocumentType.Invoice => "ใบแจ้งหนี้",
        Models.Enums.DocumentType.TaxInvoice => "ใบกำกับภาษี",
        Models.Enums.DocumentType.CreditNote => "ใบลดหนี้",
        Models.Enums.DocumentType.DebitNote => "ใบเพิ่มหนี้",
        _ => "เอกสาร",
    };

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
