using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Auth;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Accounting.Services.Implementations;

public class AuthService : IAuthService
{
    private readonly AccountingDbContext _db;
    private readonly IConfiguration _config;
    private readonly IEmailService _emailService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAccountingService _accountingService;
    private readonly ICompanyService _companyService;
    private readonly ILogger<AuthService> _logger;
    private readonly ISecretProtector? _secrets;
    /// <summary>ใช้อ่าน IP + user-agent ตอนบันทึกความยินยอม PDPA เท่านั้น
    /// (optional — งานเบื้องหลัง/เทสต์ที่เรียก service ตรงไม่มี HttpContext)</summary>
    private readonly IHttpContextAccessor? _http;

    // Account lockout settings (configurable via appsettings Security section)
    private readonly int _maxFailedAttempts;
    private readonly int _lockoutMinutes;

    public AuthService(
        AccountingDbContext db,
        IConfiguration config,
        IEmailService emailService,
        ISubscriptionService subscriptionService,
        IAccountingService accountingService,
        ICompanyService companyService,
        ILogger<AuthService> logger,
        ISecretProtector? secrets = null,
        IHttpContextAccessor? http = null)
    {
        _http = http;
        _secrets = secrets;
        _db = db;
        _config = config;
        _emailService = emailService;
        _subscriptionService = subscriptionService;
        _accountingService = accountingService;
        _companyService = companyService;
        _logger = logger;
        _maxFailedAttempts = int.Parse(config["Security:MaxLoginAttemptsBeforeLockout"] ?? "5");
        // key ต้องตรงกับ appsettings ("Security:LockoutMinutes") — เดิมมี underscore
        // นำหน้า ทำให้อ่านไม่เจอและใช้ default 15 เสมอ ต่อให้ผู้ดูแลตั้งค่าไว้
        _lockoutMinutes = int.Parse(config["Security:LockoutMinutes"] ?? "15");
    }

    // ===== ความยินยอม PDPA ตอนสมัคร (ม.19) — ด่านเดียวของทุกทางสมัคร =====
    // ทุกทางที่ "สร้างผู้ใช้ใหม่" ต้องผ่าน 2 ฟังก์ชันนี้เท่านั้น: กรอกฟอร์มเอง,
    // สมัครผ่าน Google/Facebook/LINE, และรับคำเชิญเข้าบริษัท. ห้ามมีทางไหนสร้าง
    // User โดยไม่เรียก — ไม่งั้นกลับไปเป็นช่องโหว่เดิม (ติ๊กแล้วไม่มีใครเก็บ)

    /// <summary>ไม่ติ๊กยอมรับ = ไม่สมัคร. ปุ่ม SSO บนหน้าสมัครอยู่**นอก** &lt;form&gt;
    /// ⇒ เบราว์เซอร์ไม่บังคับ required ให้ ด่านจริงจึงต้องอยู่ฝั่ง server</summary>
    private static void RequireSignupConsent(bool acceptedTerms)
    {
        if (!acceptedTerms)
            throw new InvalidOperationException(
                "กรุณายอมรับข้อกำหนดการใช้งานและนโยบายความเป็นส่วนตัวก่อนสมัครสมาชิก");
    }

    /// <summary>บันทึกหลักฐานความยินยอมลง PdpaConsentRecord — เรียกหลัง
    /// SaveChanges ของ User แล้วเท่านั้น (ต้องมี <c>user.Id</c> จริง).
    /// ล้มเหลว = ไม่ทำให้การสมัครล้ม แต่ log เป็น Error ให้เห็นชัด: ผู้ใช้ที่
    /// ติ๊กยอมรับแล้วไม่ควรถูกเด้งกลับเพราะปัญหาฝั่งเรา แต่ก็ห้ามหายเงียบ</summary>
    private async Task RecordSignupConsentAsync(User user, string? policyVersionShown, string channel)
    {
        try
        {
            var ctx = _http?.HttpContext;
            // X-Forwarded-For ตัวแรก = client จริงเมื่ออยู่หลัง reverse proxy
            var ip = ctx?.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
            if (string.IsNullOrWhiteSpace(ip))
                ip = ctx?.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(ip) && ip.Length > 45) ip = ip[..45];   // varchar(45)
            var ua = ctx?.Request.Headers.UserAgent.ToString();

            var grantedAt = DateTime.UtcNow;
            _db.PdpaConsentRecords.Add(new PdpaConsentRecord
            {
                CompanyId = PdpaPolicy.PlatformScopeCompanyId,
                SubjectUserId = user.Id,
                SubjectContact = user.Email,
                Purpose = PdpaPolicy.SignupPurpose,
                // เก็บเวอร์ชันที่ **server** ใช้อยู่เป็นเวอร์ชันของแถว ส่วนเวอร์ชันที่
                // หน้าเว็บแสดงจริงอยู่ใน evidence hash — ต่างกันเมื่อไรแปลว่าเบราว์เซอร์
                // ค้าง cache ฉบับเก่า (log เตือนด้านล่าง) ไม่ใช่ข้อมูลสูญหาย
                PolicyVersion = PdpaPolicy.CurrentVersion,
                GrantedAt = grantedAt,
                Channel = channel,
                IpAddress = ip,
                EvidenceHash = PdpaConsentEvidence.ComputeHash(
                    user.Email, PdpaPolicy.SignupPurpose, policyVersionShown,
                    grantedAt, channel, ip, ua),
            });
            await _db.SaveChangesAsync();

            if (!string.IsNullOrWhiteSpace(policyVersionShown)
                && policyVersionShown != PdpaPolicy.CurrentVersion)
            {
                _logger.LogWarning(
                    "PDPA consent: หน้าเว็บแสดงนโยบายเวอร์ชัน {Shown} แต่ระบบใช้ {Current} — เบราว์เซอร์อาจค้าง cache (user={UserId})",
                    policyVersionShown, PdpaPolicy.CurrentVersion, user.Id);
            }
        }
        catch (Exception ex)
        {
            // ห้ามกลืนเงียบ (กฎเหล็ก #4 E) — แต่ก็ห้ามล้มการสมัครของผู้ใช้
            _logger.LogError(ex, "PDPA consent: บันทึกความยินยอมไม่สำเร็จ (user={UserId}, channel={Channel})",
                user.Id, channel);
        }
    }

    public async Task<LoginResponse> RegisterAsync(RegisterRequest request)
    {
        RequireSignupConsent(request.AcceptedTerms);

        // Normalize to lowercase — case-insensitive uniqueness so
        // "Alice@Example.com" and "alice@example.com" can't both register.
        var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email.ToLower() == email))
            throw new InvalidOperationException("อีเมลนี้ถูกใช้งานแล้ว");

        // ด่าน "เปิดรับสมัคร" ของแพลตฟอร์ม (S-08 · รอบ 193) — เดิมกันแค่หน้า register.html ⇒ ยิง API ตรงยังสมัครได้ ·
        // ปิดอยู่ + มีคำเชิญของอีเมลนี้ = สมัครได้แต่ห้ามสร้างบริษัท (เข้าบริษัทที่เชิญเท่านั้น)
        var registration = await EvaluateRegistrationAsync(email, request.InvitationToken);
        if (!registration.Allowed)
            throw new BusinessRuleException(registration.Message!, RegistrationPolicy.RuleCode, registration.StatusCode);

        // ตั๋ว SSO ต้องยังไม่ถูกใช้ผูกกับใคร — ตรวจ**ก่อน**สร้างผู้ใช้ ไม่งั้น
        // จะได้บัญชีที่สร้างสำเร็จแต่ผูกไม่ได้ (ตั๋วใบเดียวสมัครหลายบัญชีได้ใน
        // 20 นาที เพราะไม่มีใครเก็บ jti)
        var ticketPre = JwtHelper.ReadSsoSignupTicket(request.SsoTicket, _config);
        if (ticketPre is { } tp && await _db.UserExternalLogins.AnyAsync(x =>
                x.Provider == tp.Provider && x.ProviderUserId == tp.ProviderUserId))
            throw new BusinessRuleException(
                $"บัญชี {SsoIdentityPolicy.DisplayName(tp.Provider)} นี้ถูกใช้สมัครไปแล้ว — "
                + "กรุณาเข้าสู่ระบบด้วยปุ่มเดิม หรือใช้ \"ลืมรหัสผ่าน\" หากจำอีเมลที่สมัครไว้ไม่ได้",
                "SSO-TICKET-USED");

        ValidatePassword(request.Password);

        // Support both FullName and FirstName+LastName from frontend
        var fullName = !string.IsNullOrWhiteSpace(request.FullName)
            ? request.FullName
            : $"{request.FirstName} {request.LastName}".Trim();

        if (string.IsNullOrWhiteSpace(fullName))
            throw new InvalidOperationException("กรุณากรอกชื่อ-นามสกุล");

        var user = new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FullName = fullName,
            Phone = request.Phone
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // หลักฐานความยินยอม — ต้องหลัง SaveChanges เพราะต้องใช้ user.Id จริง
        // ตั๋ว SSO (สมัครต่อจากการกดปุ่ม LINE/Google ที่ provider ไม่ให้อีเมลมา)
        // → ช่องทางสมัครคือ provider นั้น ไม่ใช่ web-form เปล่า ๆ
        var ssoTicket = JwtHelper.ReadSsoSignupTicket(request.SsoTicket, _config);
        await RecordSignupConsentAsync(user, request.PolicyVersion,
            ssoTicket is { } t0 ? "sso-" + t0.Provider.ToLowerInvariant() : "web-form");

        // ผูกบัญชีภายนอกให้ทันทีตามตั๋ว — ผู้ใช้เพิ่งผ่าน OAuth มาสด ๆ และบัญชีนี้
        // เพิ่งเกิดจากการกรอกอีเมลของเขาเอง ⇒ ไม่มีบัญชีเดิมให้ยึด ผูกได้เลย
        if (ssoTicket is { } ticket)
        {
            // ผูกให้ตามตั๋ว — ถ้าผูกไม่สำเร็จจะ throw ออกไป (ผู้ใช้ต้องรู้)
            // ⚠️ ตัวตนที่ถูกผูกไปแล้วถูกปฏิเสธตั้งแต่**ก่อนสร้างผู้ใช้** ด้านบน
            // แล้ว (ดู RequireUnusedSsoTicketAsync) — เดิมเช็คตรงนี้แล้ว log
            // อย่างเดียวแล้วตอบว่าสมัครสำเร็จ ⇒ ผู้ใช้เชื่อว่าครั้งหน้ากดปุ่ม
            // provider เข้าได้ แต่ไม่ได้ผูกจริง (silent partial success)
            await LinkExternalLoginAsync(user, ticket.Provider, ticket.ProviderUserId,
                ticket.Email ?? user.Email, confirmed: true, notify: false);
        }

        // Invitation acceptance — when the user registered via an invitation
        // link, consume the matching pending invite for THIS email and skip
        // the company-create branch below: they're joining an existing
        // company, not creating their own. Invalid / expired / mismatched
        // invitations are ignored (the new account is still created so the
        // user isn't left on the signup page in confusion).
        // ตรรกะอยู่ใน ConsumeInvitationAsync ตัวเดียว — เส้น SSO เรียกตัวเดียวกัน
        // (เดิมเส้น SSO ไม่มีตรรกะนี้เลย ⇒ คนที่ถูกเชิญแล้วสมัครด้วย Google/LINE
        //  ไม่ได้เข้าบริษัทที่เชิญ)
        bool consumedInvite = await ConsumeInvitationAsync(user, request.InvitationToken);

        // Create company if companyName provided AND the user wasn't routed
        // through an invitation. An invited member shouldn't auto-spawn a
        // stub company they didn't ask for.
        // + ปิดรับสมัครอยู่ = ห้ามสร้างบริษัทแม้คำเชิญจะใช้ไม่สำเร็จในจังหวะนี้ (RegistrationPolicy.MayCreateCompany)
        if (!consumedInvite && registration.MayCreateCompany && !string.IsNullOrWhiteSpace(request.CompanyName))
        {
            var company = new Company
            {
                Name = request.CompanyName,
                TaxId = "-"
            };
            _db.Companies.Add(company);
            // แถวค่าตั้งเกิดพร้อมบริษัท + seed VAT จากบริษัท (ยังไม่จด = ค่าเริ่มต้นของ Company) — S-01
            _db.CompanySettings.Add(CompanySettingsFactory.NewFor(company));

            _db.CompanyUsers.Add(new CompanyUser
            {
                CompanyId = company.Id,
                UserId = user.Id,
                Role = Models.Enums.UserRole.Owner
            });

            await _db.SaveChangesAsync();

            // Auto-start subscription with the chosen plan (default: Free Edition — permanent free).
            // FreeTrial plan template has IsPermanentFree=true so users get a perpetually-free
            // entry-level subscription. Pro/Enterprise get a 14/30-day trial.
            var plan = request.Plan ?? Models.Enums.SubscriptionPlan.FreeTrial;
            try
            {
                await _subscriptionService.StartTrialAsync(
                    new Models.DTOs.Subscription.StartTrialRequest(company.Id, plan),
                    user.Id.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-start trial for company {CompanyId}", company.Id);
                // Continue — user can start trial manually from settings
            }

            // If this user already has an active License (e.g. they're
            // re-registering after a prior signup, or SSO is linking a new
            // company to an existing identity), attach the new company to it
            // so quota/features flow from the License rather than the stub
            // FreeTrial we just seeded. No-op when no License exists.
            // ส่ง plan ที่เลือกไปด้วย — ถ้า StartTrialAsync ข้างบนล้มเหลว เส้นนี้จะ
            // retry ด้วย plan เดิม (เดิม fallback เป็น FreeTrial stub → plan ที่เลือกหาย)
            try { await _companyService.EnsureSubscriptionForNewCompanyAsync(company.Id, user.Id, plan); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "License auto-attach failed for company {CompanyId}", company.Id);
            }

            // Auto-seed default chart of accounts so the user can immediately start using the system
            try
            {
                await _accountingService.SeedDefaultAccountsAsync(company.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed accounts for company {CompanyId}", company.Id);
            }
        }

        return await GenerateLoginResponse(user);
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        // Case-insensitive login — see RegisterAsync for the normalization
        // rationale.
        var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email)
            ?? throw new UnauthorizedAccessException("อีเมลหรือรหัสผ่านไม่ถูกต้อง");

        // Check account lockout
        if (user.LockoutEnd.HasValue && user.LockoutEnd > DateTime.UtcNow)
        {
            var remaining = (int)(user.LockoutEnd.Value - DateTime.UtcNow).TotalMinutes + 1;
            throw new UnauthorizedAccessException(
                $"บัญชีถูกล็อคชั่วคราว กรุณาลองใหม่ในอีก {remaining} นาที");
        }

        // บัญชีที่สร้างผ่าน SSO มี PasswordHash = "" → BCrypt.Verify โยน
        // SaltParseException ⇒ middleware แปลงเป็น **500** (ไม่ใช่ 401) ผู้ใช้เห็น
        // "เกิดข้อผิดพลาดภายในระบบ" แทนที่จะรู้ว่าต้องกดปุ่ม provider
        if (string.IsNullOrEmpty(user.PasswordHash))
            throw new UnauthorizedAccessException(
                UserLoginPolicy.PasswordLoginUnavailableMessage(user.AuthProvider));

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            // Increment failed attempts
            user.FailedLoginAttempts = (user.FailedLoginAttempts ?? 0) + 1;

            if (user.FailedLoginAttempts >= _maxFailedAttempts)
            {
                user.LockoutEnd = DateTime.UtcNow.AddMinutes(_lockoutMinutes);
                user.FailedLoginAttempts = 0;
                await _db.SaveChangesAsync();
                throw new UnauthorizedAccessException(
                    $"เข้าสู่ระบบผิดพลาดเกินกำหนด บัญชีถูกล็อค {_lockoutMinutes} นาที");
            }

            await _db.SaveChangesAsync();
            throw new UnauthorizedAccessException("อีเมลหรือรหัสผ่านไม่ถูกต้อง");
        }

        // ด่านสถานะบัญชี — เช็ค **หลัง** ยืนยันรหัสผ่านเสมอ เพื่อไม่บอกสถานะบัญชี
        // ให้คนที่เดารหัสอยู่ (ถามอีเมลอย่างเดียวแล้วได้คำตอบ = enumeration)
        var loginGate = UserLoginPolicy.Evaluate(user.Status, viaVerifiedSso: false);
        if (!loginGate.Can) throw new UnauthorizedAccessException(loginGate.Reason);

        // Reset failed attempts on successful login
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = DateTime.UtcNow;

        // Track weak (legacy) passwords so we can warn / force-change after a grace period.
        // We have plaintext here (login request body); after this method the password is
        // discarded and we only retain the hash, so this is the right place to evaluate.
        PasswordWeakNotice? weakNotice = null;
        if (!IsPasswordCompliant(request.Password))
        {
            user.PasswordWeakDetectedAt ??= DateTime.UtcNow;
            weakNotice = BuildPasswordWeakNotice(user.PasswordWeakDetectedAt.Value);
        }
        else if (user.PasswordWeakDetectedAt != null)
        {
            // Compliant now (rules may have changed) — clear the flag.
            user.PasswordWeakDetectedAt = null;
        }

        await _db.SaveChangesAsync();

        return await GenerateLoginResponse(user, weakNotice);
    }

    public async Task<LoginResponse> RefreshTokenAsync(string refreshToken)
    {
        // Re-use detection — if the caller is presenting the PREVIOUS token
        // (which we issued and then rotated), it means either (a) replay by a
        // client that didn't see the previous response, or (b) a stolen old
        // cookie. Safe assumption is (b): kill all sessions for the user.
        var reuseHit = await _db.Users.FirstOrDefaultAsync(u =>
            u.PreviousRefreshToken == refreshToken);
        if (reuseHit != null)
        {
            reuseHit.RefreshToken = null;
            reuseHit.PreviousRefreshToken = null;
            reuseHit.RefreshTokenExpiry = null;
            reuseHit.RefreshTokenRevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            throw new UnauthorizedAccessException(
                "ตรวจพบการใช้ token ซ้ำ — เซสชันทั้งหมดถูกยกเลิก กรุณาเข้าสู่ระบบใหม่");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.RefreshToken == refreshToken
            && u.RefreshTokenExpiry > DateTime.UtcNow
            && u.RefreshTokenRevokedAt == null)
            ?? throw new UnauthorizedAccessException("Refresh token ไม่ถูกต้องหรือหมดอายุ");

        // ปิด/ระงับบัญชีต้องมีผลกับ **เซสชันที่เปิดค้างอยู่** ด้วย ไม่ใช่แค่ทางเข้าใหม่
        // (ไม่งั้นแอดมินกดปิดบัญชีแล้วผู้ใช้ยังต่ออายุ token ไปได้เรื่อย ๆ)
        var refreshGate = UserLoginPolicy.Evaluate(user.Status, viaVerifiedSso: false);
        if (!refreshGate.Can) throw new UnauthorizedAccessException(refreshGate.Reason);

        return await GenerateLoginResponse(user);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request)
    {
        var user = await _db.Users.FindAsync(userId)
            ?? throw new KeyNotFoundException("ไม่พบผู้ใช้งาน");

        // บัญชีที่สร้างผ่าน SSO มี PasswordHash = "" → BCrypt โยน SaltParseException
        // ⇒ 500 (บั๊กเดียวกับที่แก้ใน LoginAsync แต่รอบนั้นแก้จุดเดียว)
        if (string.IsNullOrEmpty(user.PasswordHash))
            throw new UnauthorizedAccessException(
                UserLoginPolicy.PasswordLoginUnavailableMessage(user.AuthProvider));

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            throw new UnauthorizedAccessException("รหัสผ่านเดิมไม่ถูกต้อง");

        ValidatePassword(request.NewPassword);

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.PasswordWeakDetectedAt = null;
        await _db.SaveChangesAsync();
    }

    public async Task<string> ForgotPasswordAsync(string email)
    {
        var normEmail = (email ?? string.Empty).Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normEmail);
        if (user == null)
            return "หากอีเมลนี้มีในระบบ คุณจะได้รับลิงก์รีเซ็ตรหัสผ่านทางอีเมล";

        var token = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        user.PasswordResetToken = token;
        user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
        await _db.SaveChangesAsync();

        // Send password reset email
        await _emailService.SendPasswordResetAsync(user.Email, user.FullName, token);

        return "หากอีเมลนี้มีในระบบ คุณจะได้รับลิงก์รีเซ็ตรหัสผ่านทางอีเมล";
    }

    public async Task ResetPasswordAsync(string token, string newPassword)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.PasswordResetToken == token && u.PasswordResetTokenExpiry > DateTime.UtcNow)
            ?? throw new InvalidOperationException("ลิงก์รีเซ็ตรหัสผ่านไม่ถูกต้องหรือหมดอายุ");

        ValidatePassword(newPassword);

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.PasswordResetToken = null;
        user.PasswordResetTokenExpiry = null;
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        user.PasswordWeakDetectedAt = null;

        // ── ยึดบัญชีคืน = ต้องตัดทางเข้าของคนอื่นให้หมด ─────────────────────
        // เคส pre-hijacking: ผู้โจมตีสมัครบัญชีด้วยอีเมลของเหยื่อไว้ล่วงหน้า
        // แล้วผูก SSO ของตัวเองไว้ → เหยื่อมาสมัครไม่ได้ จึงกด "ลืมรหัสผ่าน"
        // ยึดบัญชีคืน — แต่เดิม **link ของผู้โจมตีไม่เคยถูกถอด** เขาจึงยังกดปุ่ม
        // SSO เข้าบัญชีเดิมได้ตลอดไป (และ refresh token เดิมก็ยังใช้ได้)
        var links = await _db.UserExternalLogins
            .Where(x => x.UserId == user.Id && !x.IsDeleted).ToListAsync();
        foreach (var l in links)
        {
            l.IsDeleted = true;
            l.UpdatedAt = DateTime.UtcNow;
        }
        user.AuthProvider = null;
        user.AuthProviderId = null;
        // ตัดเซสชันเก่าทั้งหมดด้วย — คนที่ถือ refresh token อยู่ต้องหลุด
        user.RefreshToken = null;
        user.PreviousRefreshToken = null;
        user.RefreshTokenExpiry = null;
        user.RefreshTokenRevokedAt = DateTime.UtcNow;
        if (links.Count > 0)
        {
            _db.AuditLogs.Add(new AuditLog
            {
                UserId = user.Id,
                UserEmail = user.Email,
                Action = Models.Enums.AuditAction.Delete,
                EntityType = "UserExternalLogin",
                EntityId = user.Id.ToString(),
                OldValues = JsonSerializer.Serialize(links.Select(l => new { l.Provider, l.ProviderEmail })),
                NewValues = JsonSerializer.Serialize(new
                {
                    Reason = "ตั้งรหัสผ่านใหม่ผ่านลิงก์อีเมล — ถอดการผูกภายนอกทั้งหมดเพื่อความปลอดภัย",
                    RuleCode = "SSO-RESET-UNLINK",
                }),
                IpAddress = ClientIp(),
                Timestamp = DateTime.UtcNow,
            });
            try
            {
                await _emailService.SendNotificationEmailAsync(user.Email, user.FullName,
                    "ถอดการผูกบัญชีภายนอกทั้งหมดแล้ว",
                    "เนื่องจากมีการตั้งรหัสผ่านใหม่ผ่านลิงก์ในอีเมล ระบบได้ถอดการผูก "
                    + $"{links.Count} บัญชี (Google/LINE/Facebook) และยกเลิกเซสชันทั้งหมด "
                    + "เพื่อความปลอดภัย — คุณผูกใหม่ได้ที่ ตั้งค่า → ความปลอดภัยบัญชี");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "แจ้งอีเมลการถอดการผูกหลังรีเซ็ตรหัสผ่านไม่สำเร็จ (user={UserId})", user.Id);
            }
        }

        // ตั้งรหัสผ่านผ่านลิงก์ที่ส่งไปอีเมล = พิสูจน์การเข้าถึงอีเมลแล้ว →
        // activate ผู้ใช้ที่ admin สร้างไว้ (WP-D1) ให้ล็อกอินได้ทันที
        if (user.Status == Models.Enums.UserStatus.PendingVerification)
        {
            user.Status = Models.Enums.UserStatus.Active;
            user.EmailVerified = true;
        }
        await _db.SaveChangesAsync();
    }

    public async Task<UserProfileResponse> GetProfileAsync(Guid userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีผู้ใช้");
        return MapProfile(user);
    }

    public async Task<UserProfileResponse> UpdateProfileAsync(Guid userId, UpdateProfileRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีผู้ใช้");

        if (!string.IsNullOrWhiteSpace(request.FullName))
            user.FullName = request.FullName.Trim();
        if (request.Phone != null)
            user.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();

        // Signature image: support data URL or raw base64. Cap at ~2 MB to protect DB.
        if (request.SignatureImageBase64 != null)
        {
            var sig = request.SignatureImageBase64.Trim();
            if (sig.Length == 0)
            {
                user.SignatureImageBase64 = null;
            }
            else
            {
                if (sig.Length > 2_800_000)
                    throw new InvalidOperationException("ลายเซ็นมีขนาดใหญ่เกินไป (สูงสุด ~2 MB)");
                if (!sig.StartsWith("data:") && !IsLikelyBase64(sig))
                    throw new InvalidOperationException("รูปแบบลายเซ็นไม่ถูกต้อง");
                user.SignatureImageBase64 = sig;
            }
        }
        if (request.SignatureName != null)
            user.SignatureName = string.IsNullOrWhiteSpace(request.SignatureName) ? null : request.SignatureName.Trim();
        if (request.SignatureTitle != null)
            user.SignatureTitle = string.IsNullOrWhiteSpace(request.SignatureTitle) ? null : request.SignatureTitle.Trim();

        await _db.SaveChangesAsync();
        return MapProfile(user);
    }

    private static bool IsLikelyBase64(string s)
    {
        try { _ = Convert.FromBase64String(s); return true; } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Base64 validation failed: {ex.Message}"); return false; }
    }

    private static UserProfileResponse MapProfile(Models.Entities.User u) => new(
        u.Id, u.Email, u.FullName, u.Phone,
        u.SignatureImageBase64, u.SignatureName, u.SignatureTitle);

    /// <summary>เข้าสู่ระบบ/สมัครด้วย Google · Facebook · LINE
    ///
    /// ลำดับด่าน (ห้ามสลับ):
    /// 1. provider รองรับ + เปิดใช้ + คีย์ครบ
    /// 2. verify token กับ provider → ได้ id + email + **ยืนยันอีเมลหรือยัง**
    /// 3. หา identity ที่ **ผูกและยืนยันแล้ว** จาก `UserExternalLogins`
    /// 4. ไม่เจอ → หาบัญชีเดิมด้วยอีเมล
    ///    - เจอ + provider ยืนยันอีเมล → ผูกให้ (พร้อม audit + อีเมลแจ้งเจ้าของ)
    ///    - เจอ + ยืนยันไม่ได้ → **ไม่ผูก ไม่ให้เข้า** แต่ส่งลิงก์ยืนยันไปที่อีเมล
    ///    - ไม่เจอ → สมัครใหม่ (ต้องผ่านด่านยินยอม PDPA ม.19)
    /// 5. ด่านสถานะบัญชี (`UserLoginPolicy`) — ปิด/ระงับ = เข้าไม่ได้ทุกทาง
    /// </summary>
    public async Task<LoginResponse> SsoLoginAsync(SsoLoginRequest request)
    {
        var provider = request.Provider?.Trim() ?? "";
        if (!SsoIdentityPolicy.IsSupported(provider))
            throw new InvalidOperationException("รองรับเฉพาะ Google, Facebook และ LINE เท่านั้น");

        // ผู้ให้บริการต้อง "เปิดใช้ + มีคีย์ครบ" ก่อน — ไม่งั้นปฏิเสธตั้งแต่ต้น
        // (กันเคสปุ่มหลุดมาบนหน้า login แล้วยิงเข้ามาโดยยังไม่ได้ตั้งค่า)
        var sso = await GetSsoSettingsAsync();
        EnsureProviderEnabled(provider, sso);

        // Validate token with provider and extract user info
        var identity = await ValidateProviderTokenAsync(provider, request.IdToken, sso);
        var providerUserId = identity.Id;
        var fullName = identity.Name;

        // ⚠️ **อีเมลไม่ใช่สิ่งจำเป็นอีกต่อไป** — ตัวระบุตัวตนหลักคือ `Id` ที่
        // provider ออกให้ (LINE userId / Google sub) ซึ่งมั่นคงกว่าอีเมลด้วยซ้ำ.
        // เดิม throw ทิ้งทันทีเมื่อไม่มีอีเมล ⇒ ปุ่ม LINE ใช้ไม่ได้เลยแม้แต่กับคนที่
        // ผูกบัญชีไว้แล้ว เพราะ channel ส่วนใหญ่ยังไม่ได้รับอนุมัติสิทธิ์ email
        // (ต้องยื่นเอกสารกับ LINE) — อีเมลใช้เฉพาะตอน "จับคู่บัญชีเดิม/สร้างใหม่"
        string emailNonNull = (identity.Email ?? "").Trim().ToLowerInvariant();

        // ตัวตนนี้ "เชื่อได้" ไหม — ตัวชี้ขาดว่าจะผูกเข้าบัญชีเดิมได้ทันทีหรือไม่
        var trustedIdentity = SsoIdentityPolicy.CanAutoLink(provider, identity.EmailVerified);

        // ① ตัวตนที่ผูกไว้แล้ว (ตารางเป็นตัวตัดสิน ไม่ใช่คอลัมน์เดี่ยวบน Users)
        var link = await _db.UserExternalLogins.Include(x => x.User)
            .FirstOrDefaultAsync(x => x.Provider == provider && x.ProviderUserId == providerUserId);

        User? user = null;
        var alreadyLinked = false;
        if (link is { ConfirmedAt: not null })
        {
            user = link.User;
            alreadyLinked = true;
            link.LastUsedAt = DateTime.UtcNow;
        }

        if (user == null)
        {
            // ② ยังไม่เคยผูก + provider ไม่ให้อีเมลมา → จับคู่บัญชีเดิมไม่ได้ และ
            // สร้างบัญชีใหม่ก็ไม่ได้ (Email เป็น unique key ของ Users). **ไม่ตัน**:
            // ออกตั๋วที่เซ็นแล้วพาไปหน้าสมัคร กรอกแค่อีเมล+รหัสผ่าน แล้วระบบผูก
            // LINE ให้เอง — ผู้ใช้ไม่ต้องกดปุ่ม SSO ซ้ำ (code ใช้ได้ครั้งเดียว)
            if (string.IsNullOrWhiteSpace(emailNonNull))
            {
                throw new SsoSignupRequiredException(provider,
                    JwtHelper.GenerateSsoSignupTicket(provider, providerUserId,
                        identity.Name, identity.Email, identity.PictureUrl, _config),
                    identity.Name, identity.PictureUrl);
            }

            // ③ บัญชีเดิมที่อีเมลตรงกัน (case-insensitive)
            var existing = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == emailNonNull);

            if (existing != null)
            {
                // เช็คสถานะ**ก่อน**ผูก/ก่อนส่งอีเมล — บัญชีที่ถูกปิดต้องไม่ได้อะไร
                // จากเส้นนี้เลย แม้แต่อีเมลชวนยืนยัน
                var gateExisting = UserLoginPolicy.Evaluate(existing.Status, trustedIdentity);
                if (!gateExisting.Can) throw new UnauthorizedAccessException(gateExisting.Reason);

                if (!trustedIdentity)
                {
                    // ⚠️ หัวใจของการแก้รอบนี้: **ห้ามผูกด้วยอีเมลอย่างเดียว**
                    // (Facebook ไม่มีสัญญาณยืนยัน / Google บอกว่ายังไม่ยืนยัน)
                    // → ไม่ให้เข้า แต่ก็ไม่ตัน: ส่งลิงก์ยืนยันไปที่อีเมลของบัญชีเดิม
                    await SendSsoLinkConfirmationAsync(existing, provider, providerUserId, emailNonNull, link);
                    throw new InvalidOperationException(
                        SsoIdentityPolicy.LinkNeedsConfirmationMessage(provider, existing.Email));
                }

                await LinkExternalLoginAsync(existing, provider, providerUserId, emailNonNull,
                    // มาถึงตรงนี้ได้แปลว่า trustedIdentity = provider ยืนยันอีเมลแล้ว
                    confirmed: true, notify: true, markEmailVerified: true);

                // คำเชิญเข้าบริษัทใช้ได้กับ **บัญชีเดิม** ด้วย — คนที่มีบัญชีอยู่แล้ว
                // แล้วถูกเชิญเข้าอีกบริษัท กดลิงก์คำเชิญ → เลือกเข้าด้วย Google
                // เดิมจะเข้าระบบได้แต่คำเชิญค้าง Pending ตลอดไปโดยไม่มีอะไรบอก
                // (silent no-op) ⇒ แอดมินเห็น "รอตอบรับ" ทั้งที่คนนั้นเข้ามาแล้ว
                await ConsumeInvitationAsync(existing, request.InvitationToken);
                user = existing;
            }
            else
            {
                // สมัครใหม่ผ่าน SSO — ต้องยอมรับข้อกำหนด/นโยบายก่อนเสมอ.
                // หน้า login ไม่ได้ส่งธงนี้มา (และไม่ควรส่ง) ⇒ กดปุ่ม SSO ที่หน้า
                // เข้าสู่ระบบโดยยังไม่เคยมีบัญชี จะถูกส่งกลับไปหน้าสมัครสมาชิก
                // ซึ่งเป็นที่เดียวที่แสดงข้อความให้อ่านและมีช่องติ๊กให้ยินยอมจริง
                // ⇒ **พาไปเลย** พร้อมตั๋วที่มีชื่อ/อีเมล/รูป — หน้าสมัครเติมให้หมด
                // ผู้ใช้กรอกเพิ่มแค่รหัสผ่าน (หรือผูกกับบัญชีเดิมถ้าเคยสมัครด้วยอีเมลอื่น)
                // ไม่ต้องกดปุ่ม provider ซ้ำ (code ใช้ได้ครั้งเดียว)
                // ด่าน "เปิดรับสมัคร" ตัวเดียวกับ RegisterAsync (S-08) — ตรวจ**ก่อน**พาไปหน้าสมัคร/สร้างบัญชี
                // (ปิดอยู่ + ไม่มีคำเชิญ = บอกตรงนี้เลย ไม่ใช่พาไปหน้าสมัครที่ปิดอยู่)
                var ssoRegistration = await EvaluateRegistrationAsync(emailNonNull, request.InvitationToken);
                if (!ssoRegistration.Allowed)
                    throw new BusinessRuleException(ssoRegistration.Message!, RegistrationPolicy.RuleCode,
                        ssoRegistration.StatusCode);

                if (!request.AcceptedTerms)
                    throw new SsoSignupRequiredException(provider,
                        JwtHelper.GenerateSsoSignupTicket(provider, providerUserId,
                            identity.Name, emailNonNull, identity.PictureUrl, _config),
                        identity.Name, identity.PictureUrl, emailNonNull,
                        SsoIdentityPolicy.NoAccountMessage, "SSO-NO-ACCOUNT");

                // Create new user via SSO (no password needed)
                user = new User
                {
                    Email = emailNonNull,
                    PasswordHash = "", // SSO users don't have password
                    FullName = fullName ?? emailNonNull.Split('@')[0],
                    AuthProvider = provider,
                    AuthProviderId = providerUserId,
                    // ห้ามตั้ง true ให้ฟรี ๆ กับ provider ที่ยืนยันอีเมลไม่ได้ —
                    // ไม่งั้นบัญชีจะกลายเป็น "ยืนยันแล้ว" ทั้งที่ไม่มีใครยืนยัน
                    EmailVerified = SsoIdentityPolicy.MarksEmailVerifiedOnSignup(
                        provider, identity.EmailVerified),
                };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();

                await LinkExternalLoginAsync(user, provider, providerUserId, emailNonNull,
                    confirmed: true, notify: false);   // บัญชีเพิ่งเกิด ไม่ต้องแจ้งว่า "ถูกผูก"

                await RecordSignupConsentAsync(user, request.PolicyVersion,
                    "sso-" + provider.ToLowerInvariant());

                // คำเชิญเข้าบริษัท — เดิมเส้น SSO **ไม่เคยใช้ invitation เลย**
                // ⇒ คนที่ถูกเชิญแล้วเลือกสมัครด้วย Google/LINE ไม่ได้เข้าบริษัท
                // ที่เชิญ ต้องให้แอดมินเชิญซ้ำ (ใช้ helper ตัวเดียวกับ RegisterAsync)
                var consumedInvite = await ConsumeInvitationAsync(user, request.InvitationToken);

                // Create company if provided (ปิดรับสมัคร + เข้ามาด้วยคำเชิญ = ห้ามสร้างบริษัท · S-08)
                if (!consumedInvite && ssoRegistration.MayCreateCompany && !string.IsNullOrWhiteSpace(request.CompanyName))
                {
                    var company = new Company { Name = request.CompanyName, TaxId = "-" };
                    _db.Companies.Add(company);
                    // เส้น SSO ใช้ตัวสร้างค่าตั้งตัวเดียวกับเส้นสมัครปกติ (S-01)
                    _db.CompanySettings.Add(CompanySettingsFactory.NewFor(company));
                    _db.CompanyUsers.Add(new CompanyUser
                    {
                        CompanyId = company.Id,
                        UserId = user.Id,
                        Role = Models.Enums.UserRole.Owner
                    });
                    await _db.SaveChangesAsync();

                    // Auto-start subscription ตามแพ็กเกจที่เลือกบนหน้า register
                    // (เดิม hardcode FreeTrial → เลือก Pro แล้วสมัครผ่าน SSO ได้ FreeTrial)
                    var ssoPlan = request.Plan ?? Models.Enums.SubscriptionPlan.FreeTrial;
                    try
                    {
                        await _subscriptionService.StartTrialAsync(
                            new Models.DTOs.Subscription.StartTrialRequest(company.Id, ssoPlan),
                            user.Id.ToString());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SSO: failed to auto-start trial for company {CompanyId}", company.Id);
                    }

                    // SSO re-link path: an existing License-holder signing in
                    // through SSO with a new company name needs the new
                    // company attached to their License, not stranded on the
                    // FreeTrial stub above. ส่ง plan ไปด้วยให้ retry path ใช้ plan เดิม.
                    try { await _companyService.EnsureSubscriptionForNewCompanyAsync(company.Id, user.Id, ssoPlan); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SSO: License auto-attach failed for company {CompanyId}", company.Id);
                    }

                    try { await _accountingService.SeedDefaultAccountsAsync(company.Id); }
                    catch (Exception ex) { _logger.LogWarning(ex, "SSO: failed to seed accounts for company {CompanyId}", company.Id); }
                }
            }
        }

        // ⑤ ด่านสถานะบัญชี — ครอบ**ทุกเส้น** รวมตัวตนที่ผูกไว้นานแล้ว
        // (เดิมไม่มีด่านนี้เลย ⇒ พนักงานที่ลาออกแล้ว ซึ่ง PayrollService ตั้ง
        //  Status=Inactive ให้อัตโนมัติ ยังล็อกอินเข้าระบบได้ตามปกติ)
        var gate = UserLoginPolicy.Evaluate(user.Status, trustedIdentity);
        if (!gate.Can) throw new UnauthorizedAccessException(gate.Reason);

        if (UserLoginPolicy.ActivatesPendingVerification(user.Status, trustedIdentity))
        {
            // เข้าด้วย SSO ที่ยืนยันอีเมลแล้ว = พิสูจน์การเข้าถึงอีเมล ซึ่งเป็น
            // สิ่งเดียวกับที่ลิงก์ตั้งรหัสผ่านต้องการ → เลื่อนเป็น Active
            user.Status = Models.Enums.UserStatus.Active;
            user.EmailVerified = true;
        }

        // คอลัมน์เดิมบน Users = "ตัวล่าสุดที่ใช้เข้าระบบ" (ตารางเป็นตัวตัดสิน)
        user.AuthProvider = provider;
        user.AuthProviderId = providerUserId;
        user.LastLoginAt = DateTime.UtcNow;
        // ⚠️ ล้าง lockout เฉพาะตอนเข้าด้วยตัวตนที่ **ผูกไว้ก่อนหน้าแล้ว** — การ
        // ผูกครั้งแรกยังไม่ใช่หลักฐานว่าเป็นเจ้าของบัญชีมากพอที่จะไปปลดล็อกฝั่ง
        // รหัสผ่านที่กำลังโดนเดารหัสอยู่
        if (alreadyLinked)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEnd = null;
        }
        await _db.SaveChangesAsync();

        return await GenerateLoginResponse(user);
    }

    /// <summary>หน้าสมัคร (มาถึงด้วยตั๋ว SSO): ผู้ใช้บอกว่า "ฉันมีบัญชีอยู่แล้วที่อีเมล X"
    ///
    /// สามจังหวะในเมธอดเดียว:
    ///   1. **เช็ค** (ไม่ส่งรหัสผ่าน) — บอกว่าอีเมลนี้มีบัญชีไหม (หน้าสมัครเปิดเผยอยู่แล้ว
    ///      ผ่าน "อีเมลนี้ถูกใช้งานแล้ว" จึงไม่ใช่ข้อมูลใหม่) ถ้ามี = ส่งลิงก์ยืนยันไปที่อีเมล
    ///      นั้นทันที (เจ้าของอีเมลเท่านั้นที่กดได้ — กติกา "ค่าที่ตรงกัน ≠ พิสูจน์ตัวตน")
    ///   2. **ผูกด้วยรหัสผ่าน** — รหัสผ่านของบัญชีเดิมคือหลักฐานความเป็นเจ้าของที่แข็งกว่า
    ///      อีเมลตรงกัน ⇒ ผูกได้ทันที + ออก token เข้าระบบเลย (ใช้ lockout เดียวกับ login)
    ///   3. ไม่มีบัญชี — บอกให้สมัครใหม่ด้วยอีเมลนั้นได้เลย (ไม่ throw: ไม่ใช่ error)
    /// ตั๋วผูกกับตัวตน provider ที่ผ่าน OAuth มาสด ๆ (อายุ 20 นาที) — ตัวตนที่ถูกผูกไปแล้ว
    /// ถูกปฏิเสธก่อนทุกอย่าง</summary>
    public async Task<SsoLinkExistingResponse> SsoLinkExistingAsync(SsoLinkExistingRequest request)
    {
        var ticket = JwtHelper.ReadSsoSignupTicket(request.SsoTicket, _config)
            ?? throw new BusinessRuleException(
                "ตั๋วยืนยันตัวตนหมดอายุหรือไม่ถูกต้อง — กลับไปกดปุ่ม Google/LINE ที่หน้าเข้าสู่ระบบอีกครั้ง",
                "SSO-TICKET-INVALID");
        var providerName = SsoIdentityPolicy.DisplayName(ticket.Provider);

        var used = await _db.UserExternalLogins.AnyAsync(x =>
            x.Provider == ticket.Provider && x.ProviderUserId == ticket.ProviderUserId && x.ConfirmedAt != null);
        if (used)
            throw new BusinessRuleException(
                $"บัญชี {providerName} นี้ผูกกับผู้ใช้ในระบบอยู่แล้ว — กดเข้าสู่ระบบด้วยปุ่ม {providerName} ได้เลย",
                "SSO-TICKET-USED");

        var email = (request.Email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            throw new BusinessRuleException("กรุณากรอกอีเมลให้ครบ", "SSO-LINK-EMAIL");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email);
        if (user == null)
            return new SsoLinkExistingResponse(false, false, false, null,
                $"ยังไม่มีบัญชีสำหรับ {email} — สมัครใหม่ด้วยอีเมลนี้ได้เลย ระบบจะผูก {providerName} ให้อัตโนมัติ");

        // ด่านสถานะบัญชี — บัญชีที่ถูกปิดต้องไม่ได้อะไรจากเส้นนี้ แม้แต่อีเมลชวนยืนยัน
        var gate = UserLoginPolicy.Evaluate(user.Status, viaVerifiedSso: false);
        if (!gate.Can) throw new UnauthorizedAccessException(gate.Reason);

        var providerEmail = string.IsNullOrWhiteSpace(ticket.Email) ? user.Email : ticket.Email!;

        if (string.IsNullOrEmpty(request.Password))
        {
            await SendSsoLinkConfirmationAsync(user, ticket.Provider, ticket.ProviderUserId, providerEmail, null);
            return new SsoLinkExistingResponse(true, false, true, PiiMask.Email(user.Email),
                $"มีบัญชีสำหรับอีเมลนี้แล้ว — เราส่งลิงก์ยืนยันการผูก {providerName} ไปที่ {PiiMask.Email(user.Email)} "
                + "(อายุ 1 ชั่วโมง) หรือกรอกรหัสผ่านของบัญชีนั้นด้านล่างเพื่อผูกและเข้าระบบทันที");
        }

        // ── ผูกด้วยรหัสผ่าน — กติกา lockout เดียวกับ LoginAsync (ห้ามเป็นช่องเดารหัสผ่านใหม่) ──
        if (user.LockoutEnd.HasValue && user.LockoutEnd > DateTime.UtcNow)
        {
            var remaining = (int)(user.LockoutEnd.Value - DateTime.UtcNow).TotalMinutes + 1;
            throw new UnauthorizedAccessException($"บัญชีถูกล็อคชั่วคราว กรุณาลองใหม่ในอีก {remaining} นาที");
        }
        if (string.IsNullOrEmpty(user.PasswordHash))
            throw new UnauthorizedAccessException(
                "บัญชีนี้สมัครผ่าน Google/Facebook/LINE ไม่มีรหัสผ่าน — ใช้ปุ่ม \"ส่งลิงก์ยืนยันทางอีเมล\" แทน");
        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginAttempts = (user.FailedLoginAttempts ?? 0) + 1;
            if (user.FailedLoginAttempts >= _maxFailedAttempts)
            {
                user.LockoutEnd = DateTime.UtcNow.AddMinutes(_lockoutMinutes);
                user.FailedLoginAttempts = 0;
                await _db.SaveChangesAsync();
                throw new UnauthorizedAccessException($"รหัสผ่านผิดเกินกำหนด บัญชีถูกล็อค {_lockoutMinutes} นาที");
            }
            await _db.SaveChangesAsync();
            throw new UnauthorizedAccessException("รหัสผ่านไม่ถูกต้อง");
        }

        // รหัสผ่านถูก = เจ้าของบัญชีจริง ⇒ ผูกทันที (แจ้งอีเมลเจ้าของตามกติกา "ห้ามผูกเงียบ")
        // ไม่ตั้ง EmailVerified ให้ — อีเมลของบัญชีไม่ได้ถูกยืนยันจากเส้นนี้
        await LinkExternalLoginAsync(user, ticket.Provider, ticket.ProviderUserId, providerEmail,
            confirmed: true, notify: true, markEmailVerified: false);
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var login = await GenerateLoginResponse(user);
        return new SsoLinkExistingResponse(true, true, false, PiiMask.Email(user.Email),
            $"ผูกบัญชี {providerName} กับ {PiiMask.Email(user.Email)} แล้ว — ครั้งหน้ากดปุ่ม {providerName} เข้าได้เลย", login);
    }

    /// <summary>verify token กับ provider — ตัวกลางเดียวที่ทั้งเส้นล็อกอินและเส้น
    /// "ผูกบัญชีตอนล็อกอินอยู่แล้ว" เรียก (ห้ามเขียน switch ซ้ำสองที่)</summary>
    private async Task<SsoIdentity> ValidateProviderTokenAsync(
        string provider, string idToken, SsoSettings sso)
    {
        var identity = provider switch
        {
            SsoIdentityPolicy.Google => await ValidateGoogleTokenAsync(idToken, sso.GoogleClientId),
            SsoIdentityPolicy.Facebook => await ValidateFacebookTokenAsync(idToken, sso.FacebookAppId),
            _ => await ValidateLineTokenAsync(idToken, sso.LineChannelId),
        };
        if (string.IsNullOrWhiteSpace(identity.Id))
            throw new UnauthorizedAccessException(
                SsoIdentityPolicy.DisplayName(provider) + " ไม่คืนรหัสผู้ใช้ — ลองใหม่อีกครั้ง");
        return identity;
    }

    /// <summary>ผูกบัญชีภายนอกเข้ากับผู้ใช้ที่ **ล็อกอินอยู่แล้ว**
    ///
    /// เส้นนี้ไม่ต้องใช้อีเมลจาก provider เลย — ตัวผู้ใช้ยืนยันตัวเองด้วย JWT
    /// มาแล้ว และเพิ่งผ่าน OAuth ของ provider มาสด ๆ ⇒ ทั้งสองฝั่งพิสูจน์ครบ
    /// (ต่างจากเส้นล็อกอินที่ต้องเดาว่า "คนนี้คือใคร" จากอีเมล)</summary>
    public async Task<ExternalLoginResponse> LinkExternalLoginForUserAsync(
        Guid userId, string provider, string idToken)
    {
        provider = provider?.Trim() ?? "";
        if (!SsoIdentityPolicy.IsSupported(provider))
            throw new BusinessRuleException("รองรับเฉพาะ Google, Facebook และ LINE เท่านั้น");

        var sso = await GetSsoSettingsAsync();
        EnsureProviderEnabled(provider, sso);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีผู้ใช้");
        var gate = UserLoginPolicy.Evaluate(user.Status, false);
        if (!gate.Can) throw new UnauthorizedAccessException(gate.Reason);

        var identity = await ValidateProviderTokenAsync(provider, idToken, sso);

        // ตัวตนนี้ถูกผูกกับ "คนอื่น" อยู่หรือเปล่า — ห้ามแย่งมาเงียบ ๆ
        var taken = await _db.UserExternalLogins.FirstOrDefaultAsync(x =>
            x.Provider == provider && x.ProviderUserId == identity.Id);
        if (taken != null && taken.UserId != userId)
            throw new BusinessRuleException(
                $"บัญชี {SsoIdentityPolicy.DisplayName(provider)} นี้ถูกผูกกับผู้ใช้อื่นในระบบแล้ว — "
                + "ให้เจ้าของบัญชีนั้นถอดการผูกก่อน (ตั้งค่า → ความปลอดภัยบัญชี)");

        var row = await LinkExternalLoginAsync(user, provider, identity.Id,
            identity.Email ?? user.Email, confirmed: true, notify: true);

        return new ExternalLoginResponse(row.Id, row.Provider,
            SsoIdentityPolicy.DisplayName(row.Provider), row.ProviderEmail,
            row.CreatedAt, row.LastUsedAt, true, row.ConfirmedAt, row.LinkedIp);
    }

    // ===== การผูกบัญชีภายนอก (Google/Facebook/LINE) =====
    // ทุกเส้นที่ "ผูก" ต้องผ่านเมธอดเดียวนี้ — ห้ามมีที่ไหนเซ็ต AuthProvider เอง
    // (ไม่งั้นจะกลับไปเป็นการผูกเงียบที่ไม่มี audit ไม่มีอีเมลแจ้ง เหมือนเดิม)

    /// <summary>ผูกตัวตนภายนอกเข้ากับผู้ใช้ + บันทึก audit + แจ้งเจ้าของทางอีเมล
    /// <paramref name="notify"/> = false เฉพาะตอนสมัครใหม่ (บัญชีเพิ่งเกิด
    /// การแจ้งว่า "บัญชีคุณถูกผูกกับ X" ไม่มีความหมาย)</summary>
    private async Task<UserExternalLogin> LinkExternalLoginAsync(
        User user, string provider, string providerUserId, string providerEmail,
        bool confirmed, bool notify, bool markEmailVerified = false)
    {
        var row = await _db.UserExternalLogins.FirstOrDefaultAsync(x =>
            x.Provider == provider && x.ProviderUserId == providerUserId);
        var now = DateTime.UtcNow;
        // ⚠️ แถวเดิมต้องเป็นของผู้ใช้คนเดียวกันเท่านั้น — เดิมโค้ดไม่เคยเช็ค
        // และไม่เคยเซ็ต row.UserId ⇒ แถว "รอยืนยัน" ของผู้ใช้ A ถูก confirm
        // ระหว่างที่คนที่เพิ่ง authenticate คือ B ⇒ ครั้งถัดไป link.User = A
        // เจ้าของตัวตนนั้นเข้าบัญชี A ได้ (เส้นพี่น้อง LinkExternalLoginForUserAsync
        // มีด่านนี้ครบอยู่แล้ว — เส้นล็อกอินลืม)
        if (row != null && row.UserId != user.Id)
            throw new BusinessRuleException(
                $"บัญชี {SsoIdentityPolicy.DisplayName(provider)} นี้ผูกกับผู้ใช้อื่นในระบบอยู่แล้ว — "
                + "ให้เจ้าของบัญชีนั้นถอดการผูกก่อน (ตั้งค่า → ความปลอดภัยบัญชี)",
                "SSO-LINK-OWNED");
        if (row == null)
        {
            row = new UserExternalLogin
            {
                UserId = user.Id,
                Provider = provider,
                ProviderUserId = providerUserId,
                ProviderEmail = providerEmail,
                LinkedIp = ClientIp(),
            };
            _db.UserExternalLogins.Add(row);
        }
        row.ProviderEmail = providerEmail;
        if (confirmed)
        {
            row.ConfirmedAt ??= now;
            row.ConfirmToken = null;
            row.ConfirmTokenExpiry = null;
            row.LastUsedAt = now;
        }
        user.AuthProvider = provider;
        user.AuthProviderId = providerUserId;
        // ⚠️ "ผูกสำเร็จ" ≠ "อีเมลของบัญชีนี้ถูกยืนยันแล้ว" — สองเรื่องนี้เคยผูกกัน
        // อยู่ ⇒ สมัครด้วย Facebook (อีเมลไม่ยืนยัน) หรือสมัครด้วยตั๋ว LINE แล้ว
        // พิมพ์อีเมลอะไรก็ได้ ก็ได้ธง EmailVerified=true ฟรี ๆ ทับนโยบายที่
        // SsoIdentityPolicy.MarksEmailVerifiedOnSignup ตั้งใจไว้เอง
        if (markEmailVerified) user.EmailVerified = true;

        _db.AuditLogs.Add(new AuditLog
        {
            UserId = user.Id,
            UserEmail = user.Email,
            Action = Models.Enums.AuditAction.Update,
            EntityType = "UserExternalLogin",
            EntityId = user.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                Provider = provider,
                // ห้าม log id เต็ม (เป็นตัวระบุตัวบุคคลฝั่ง provider) — เก็บพอให้
                // ตามรอยได้ว่าเป็นตัวเดียวกันไหม
                ProviderUserIdTail = providerUserId.Length > 6
                    ? providerUserId[^6..] : providerUserId,
                ProviderEmail = providerEmail,
                Confirmed = confirmed,
            }),
            IpAddress = ClientIp(),
            UserAgent = _http?.HttpContext?.Request.Headers.UserAgent.ToString(),
            Timestamp = now,
        });
        await _db.SaveChangesAsync();

        if (notify)
        {
            // ห้ามผูกเงียบ — เจ้าของบัญชีต้องรู้ทันทีเพื่อทักท้วงได้ถ้าไม่ใช่ตัวเอง
            try
            {
                await _emailService.SendNotificationEmailAsync(user.Email, user.FullName,
                    $"บัญชีของคุณถูกผูกกับ {SsoIdentityPolicy.DisplayName(provider)}",
                    // HtmlEncode — providerEmail มาจากภายนอก และปลายทางคือ HTML
                    $"ระบบได้ผูกบัญชี {SsoIdentityPolicy.DisplayName(provider)} ({System.Net.WebUtility.HtmlEncode(providerEmail)}) "
                    // ⚠️ ระบุ InvariantCulture — ถ้า process ตั้ง culture th-TH
                    // ปฏิทินเริ่มต้นเป็นพุทธศักราช ปี 2026 จะกลายเป็น 2569 เงียบ ๆ
                    // ในข้อความที่ไม่ใช่แบบยื่นภาษี (ซึ่งต้องเป็น ค.ศ.)
                    + $"เข้ากับบัญชี NextAcc ของคุณ เมื่อ "
                    + now.AddHours(7).ToString("dd/MM/yyyy HH:mm",
                        System.Globalization.CultureInfo.InvariantCulture)
                    + " น. (เวลาไทย)<br>"
                    + "หากไม่ใช่คุณ กรุณาเปลี่ยนรหัสผ่านและติดต่อผู้ดูแลระบบทันที "
                    + "และถอดการผูกได้ที่หน้าโปรไฟล์ของคุณ");
            }
            catch (Exception ex)
            {
                // ส่งอีเมลไม่ได้ ต้องไม่ทำให้ล็อกอินล้ม แต่ห้ามหายเงียบ
                _logger.LogError(ex, "SSO: แจ้งอีเมลการผูกบัญชีไม่สำเร็จ (user={UserId}, provider={Provider})",
                    user.Id, provider);
            }
        }
        return row;
    }

    /// <summary>provider ยืนยันอีเมลให้ไม่ได้ → ส่งลิงก์ยืนยันการผูกไปที่อีเมล
    /// ของบัญชีเดิม (อายุ 1 ชม.) — เจ้าของอีเมลเท่านั้นที่กดได้</summary>
    private async Task SendSsoLinkConfirmationAsync(
        User user, string provider, string providerUserId, string providerEmail,
        UserExternalLogin? existingRow)
    {
        var row = existingRow ?? await _db.UserExternalLogins.FirstOrDefaultAsync(x =>
            x.Provider == provider && x.ProviderUserId == providerUserId);
        if (row == null)
        {
            row = new UserExternalLogin
            {
                UserId = user.Id,
                Provider = provider,
                ProviderUserId = providerUserId,
            };
            _db.UserExternalLogins.Add(row);
        }
        row.UserId = user.Id;
        row.ProviderEmail = providerEmail;
        row.LinkedIp = ClientIp();
        row.ConfirmToken = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        row.ConfirmTokenExpiry = DateTime.UtcNow.AddHours(1);
        await _db.SaveChangesAsync();

        var baseUrl = await AppBaseUrlAsync();
        var url = string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : baseUrl.TrimEnd('/') + "/api/auth/sso/confirm-link?token="
              + Uri.EscapeDataString(row.ConfirmToken);
        try
        {
            await _emailService.SendNotificationEmailAsync(user.Email, user.FullName,
                $"ยืนยันการผูกบัญชี {SsoIdentityPolicy.DisplayName(provider)}",
                $"มีการขอผูกบัญชี {SsoIdentityPolicy.DisplayName(provider)} ({System.Net.WebUtility.HtmlEncode(providerEmail)}) "
                + "เข้ากับบัญชี NextAcc ของคุณ<br>"
                + "กดปุ่มด้านล่างเพื่อยืนยัน (ลิงก์มีอายุ 1 ชั่วโมง)<br><br>"
                + "<b>หากไม่ใช่คุณ ไม่ต้องทำอะไร</b> — การผูกจะไม่เกิดขึ้นและบัญชีของคุณยังปลอดภัย",
                url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSO: ส่งลิงก์ยืนยันการผูกบัญชีไม่สำเร็จ (user={UserId}, provider={Provider})",
                user.Id, provider);
        }
    }

    /// <summary>ผู้ใช้กดลิงก์ยืนยันในอีเมล → ผูกจริง. คืนชื่อ provider ไว้ให้
    /// controller บอกผู้ใช้ว่ายืนยันอะไรสำเร็จ</summary>
    public async Task<string> ConfirmSsoLinkAsync(string token)
    {
        var row = await _db.UserExternalLogins.Include(x => x.User)
            .FirstOrDefaultAsync(x => x.ConfirmToken == token
                                   && x.ConfirmTokenExpiry > DateTime.UtcNow)
            ?? throw new InvalidOperationException(
                "ลิงก์ยืนยันไม่ถูกต้องหรือหมดอายุ — กรุณากดปุ่มเข้าสู่ระบบด้วยผู้ให้บริการเดิมอีกครั้ง "
                + "เพื่อขอลิงก์ใหม่");

        // สถานะบัญชีอาจเปลี่ยนระหว่างรอยืนยัน — ตรวจซ้ำก่อนผูกจริง
        var gate = UserLoginPolicy.Evaluate(row.User.Status, false);
        if (!gate.Can) throw new UnauthorizedAccessException(gate.Reason);

        // กดลิงก์ในอีเมลของตัวเอง = พิสูจน์การเข้าถึงอีเมลนั้นจริง
        await LinkExternalLoginAsync(row.User, row.Provider, row.ProviderUserId,
            row.ProviderEmail ?? row.User.Email,
            confirmed: true, notify: true, markEmailVerified: true);
        return row.Provider;
    }

    public async Task<List<ExternalLoginResponse>> GetExternalLoginsAsync(Guid userId)
    {
        var rows = await _db.UserExternalLogins.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Provider)
            .ToListAsync();
        return rows.Select(x => new ExternalLoginResponse(
            x.Id, x.Provider, SsoIdentityPolicy.DisplayName(x.Provider),
            x.ProviderEmail, x.CreatedAt, x.LastUsedAt, x.ConfirmedAt != null,
            x.ConfirmedAt, x.LinkedIp)).ToList();
    }

    /// <summary>ถอดการผูก — ต้องเหลือทางเข้าอย่างน้อยหนึ่งทางเสมอ
    /// (ไม่งั้นผู้ใช้ที่สมัครผ่าน SSO และยังไม่เคยตั้งรหัสผ่าน จะล็อกตัวเองออก
    /// จากระบบถาวรด้วยการกดปุ่มเดียว — "สถานะปลายทางที่ผู้ใช้ไปต่อไม่ได้")</summary>
    public async Task RemoveExternalLoginAsync(Guid userId, Guid linkId)
    {
        var row = await _db.UserExternalLogins.FirstOrDefaultAsync(
            x => x.Id == linkId && x.UserId == userId)
            ?? throw new KeyNotFoundException("ไม่พบการผูกบัญชีนี้");
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีผู้ใช้");

        var others = await _db.UserExternalLogins.CountAsync(
            x => x.UserId == userId && x.Id != linkId && x.ConfirmedAt != null);
        if (string.IsNullOrEmpty(user.PasswordHash) && others == 0)
            throw new InvalidOperationException(
                "ถอดไม่ได้ — นี่เป็นทางเข้าเดียวของบัญชีนี้ กรุณากด \"ลืมรหัสผ่าน\" "
                + "เพื่อตั้งรหัสผ่านสำหรับเข้าด้วยอีเมลก่อน แล้วค่อยถอด");

        row.IsDeleted = true;
        row.UpdatedAt = DateTime.UtcNow;
        if (user.AuthProvider == row.Provider && user.AuthProviderId == row.ProviderUserId)
        {
            // คอลัมน์ "ตัวล่าสุด" ต้องไม่ชี้ไปที่การผูกที่ถอดไปแล้ว
            var fallback = await _db.UserExternalLogins.FirstOrDefaultAsync(
                x => x.UserId == userId && x.Id != linkId && x.ConfirmedAt != null);
            user.AuthProvider = fallback?.Provider;
            user.AuthProviderId = fallback?.ProviderUserId;
        }
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            UserEmail = user.Email,
            Action = Models.Enums.AuditAction.Delete,
            EntityType = "UserExternalLogin",
            EntityId = userId.ToString(),
            OldValues = JsonSerializer.Serialize(new { row.Provider, row.ProviderEmail }),
            IpAddress = ClientIp(),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        try
        {
            await _emailService.SendNotificationEmailAsync(user.Email, user.FullName,
                $"ถอดการผูกบัญชี {SsoIdentityPolicy.DisplayName(row.Provider)} แล้ว",
                $"บัญชี {SsoIdentityPolicy.DisplayName(row.Provider)} ถูกถอดออกจากบัญชี NextAcc ของคุณ "
                + "หากไม่ใช่คุณ กรุณาเปลี่ยนรหัสผ่านทันที");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSO: แจ้งอีเมลการถอดการผูกไม่สำเร็จ (user={UserId})", userId);
        }
    }

    /// <summary>IP ของผู้เรียก (เคารพ X-Forwarded-For ตัวแรกเมื่ออยู่หลัง proxy) —
    /// ตัวเดียวที่ทุกจุดในไฟล์นี้ใช้ ตัดที่ 45 ตัวอักษรตามความกว้างคอลัมน์</summary>
    private string? ClientIp()
    {
        var ctx = _http?.HttpContext;
        var ip = ctx?.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrWhiteSpace(ip)) ip = ctx?.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(ip) && ip.Length > 45) ip = ip[..45];
        return ip;
    }

    private async Task<string> AppBaseUrlAsync()
    {
        var baseUrl = await _db.SiteSettings.AsNoTracking()
            .Select(s => s.AppBaseUrl).FirstOrDefaultAsync();
        return FirstNonEmpty(baseUrl, _config["App:BaseUrl"]);
    }

    /// <summary>ด่าน "เปิดรับสมัคร" ของแพลตฟอร์ม (S-08) — โหลดหลักฐาน (สวิตช์ SiteSettings + คำเชิญที่แนบมา) แล้วให้
    /// <see cref="RegistrationPolicy"/> ตัดสิน · ใช้ทั้ง RegisterAsync และเส้นสมัครผ่าน SSO</summary>
    private async Task<RegistrationDecision> EvaluateRegistrationAsync(string email, string? invitationToken)
    {
        var enabled = await _db.SiteSettings.AsNoTracking()
            .Select(s => (bool?)s.RegistrationEnabled).FirstOrDefaultAsync();
        if (RegistrationPolicy.IsOpen(enabled)) return RegistrationPolicy.EvaluateNewAccount(enabled, false);

        var usableInvite = false;
        if (!string.IsNullOrWhiteSpace(invitationToken))
        {
            var inv = await _db.CompanyInvitations.AsNoTracking()
                .Where(i => i.Token == invitationToken && !i.IsDeleted)
                .Select(i => new { i.Status, i.ExpiresAt, i.Email })
                .FirstOrDefaultAsync();
            usableInvite = inv != null
                && RegistrationPolicy.IsInvitationUsable(inv.Status, inv.ExpiresAt, inv.Email, email, DateTime.UtcNow);
        }
        return RegistrationPolicy.EvaluateNewAccount(enabled, usableInvite);
    }

    /// <summary>ใช้คำเชิญเข้าบริษัท (ถ้ามีและยังใช้ได้) — helper ตัวเดียวที่ทั้ง
    /// <c>RegisterAsync</c> และ <c>SsoLoginAsync</c> เรียก. คืน true = เข้าบริษัท
    /// ผู้เชิญแล้ว ผู้เรียกต้อง**ไม่**สร้างบริษัท stub ให้อีก</summary>
    private async Task<bool> ConsumeInvitationAsync(User user, string? invitationToken)
    {
        if (string.IsNullOrWhiteSpace(invitationToken)) return false;

        var inv = await _db.CompanyInvitations
            .FirstOrDefaultAsync(i => i.Token == invitationToken && !i.IsDeleted);
        // predicate ตัวเดียวกับด่านเปิดรับสมัคร (RegistrationPolicy.IsInvitationUsable) — "ผ่านด่านด้วยคำเชิญ"
        // ต้องแปลว่าคำเชิญใช้ได้จริงที่นี่
        if (inv != null
            && RegistrationPolicy.IsInvitationUsable(inv.Status, inv.ExpiresAt, inv.Email, user.Email, DateTime.UtcNow))
        {
            // เส้น SSO ของ "บัญชีเดิม" เรียกเมธอดนี้ได้ด้วย ⇒ ผู้ใช้อาจเป็นสมาชิก
            // บริษัทนั้นอยู่แล้ว การ Add ซ้ำจะได้สองสิทธิ์ในบริษัทเดียว (บทบาทไหน
            // ชนะขึ้นกับลำดับแถว) — ถือว่าคำเชิญถูกใช้แล้ว แต่ไม่เพิ่มแถวใหม่
            // ⚠️ `CompanyUser` เป็น join entity ที่ **ไม่ได้สืบทอด `BaseEntity`**
            // จึงไม่มี `IsDeleted` — การถอนสมาชิกคือ **ลบแถวจริง** ไม่ใช่ soft delete
            var already = await _db.CompanyUsers.AnyAsync(cu =>
                cu.CompanyId == inv.CompanyId && cu.UserId == user.Id);
            if (!already)
            {
                _db.CompanyUsers.Add(new CompanyUser
                {
                    CompanyId = inv.CompanyId,
                    UserId = user.Id,
                    Role = inv.Role,
                });
            }
            inv.Status = Models.Enums.InvitationStatus.Accepted;
            inv.AcceptedAt = DateTime.UtcNow;
            inv.AcceptedByUserId = user.Id;
            inv.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return true;
        }

        _logger.LogInformation(
            "Invitation token did not match a valid invite (token={Token}, email={Email}) — proceeding without auto-join",
            invitationToken, user.Email);
        return false;
    }

    /// <summary>ด่าน "ผู้ให้บริการนี้เปิดใช้แล้วหรือยัง" — **ตัวเดียว**ของทุกเส้น
    /// ที่แตะ SSO (ล็อกอิน · ผูกบัญชีเพิ่ม · ยืนยันการผูก)
    ///
    /// <para>เดิมคัดลอกทั้ง switch และข้อความไว้สองที่ ⇒ เพิ่ม provider ตัวที่สี่
    /// แล้วแก้ที่เดียว อีกเส้นจะตกไปที่ <c>_ =&gt; sso.LineEnabled</c> เงียบ ๆ
    /// (ผูก provider ใหม่ได้ทั้งที่แอดมินยังไม่ได้เปิด)</para></summary>
    private static void EnsureProviderEnabled(string provider, SsoSettings sso)
    {
        var enabled = provider switch
        {
            SsoIdentityPolicy.Google => sso.GoogleEnabled,
            SsoIdentityPolicy.Facebook => sso.FacebookEnabled,
            SsoIdentityPolicy.Line => sso.LineEnabled,
            _ => false,   // ไม่รู้จัก = ปิดไว้ก่อน ห้ามเดาว่าเป็น LINE
        };
        if (!enabled)
            throw new BusinessRuleException("ยังไม่ได้เปิดใช้การเข้าสู่ระบบด้วย "
                + SsoIdentityPolicy.DisplayName(provider) + " — ติดต่อผู้ดูแลระบบ");
    }

    /// <summary>ค่า SSO ที่ "ใช้จริง" — DB (ตั้งจากหน้าแอดมิน) ชนะ appsettings
    /// (deployment เดิม). ตัวตัดสินตัวเดียวของทั้งระบบ: endpoint sso-config ที่
    /// หน้า login เรียก และ SsoLoginAsync ต้องอ่านจากตัวนี้เท่านั้น ไม่งั้นปุ่ม
    /// โผล่/หายไม่ตรงกับที่ backend ยอมรับจริง.
    /// เปิดใช้ = ติ๊กเปิด **และ** มีคีย์ครบ — คีย์ว่าง = ปุ่มกดแล้วพัง</summary>
    public async Task<SsoSettings> GetSsoSettingsAsync()
    {
        var s = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync();
        var googleId = FirstNonEmpty(s?.GoogleClientId, _config["OAuth:Google:ClientId"]);
        var fbId = FirstNonEmpty(s?.FacebookAppId, _config["OAuth:Facebook:AppId"]);
        var lineId = FirstNonEmpty(s?.LineLoginChannelId, _config["OAuth:Line:ChannelId"]);
        // ไม่มีแถว SiteSettings เลย (deployment เก่า) → ใช้ appsettings เป็นเกณฑ์:
        // ตั้งคีย์ไว้ = ถือว่าเปิด (พฤติกรรมเดิมก่อนมีสวิตช์ ไม่ให้ของหายไปเฉย ๆ)
        return new SsoSettings(
            GoogleEnabled: (s?.GoogleLoginEnabled ?? !string.IsNullOrWhiteSpace(googleId))
                && !string.IsNullOrWhiteSpace(googleId),
            GoogleClientId: googleId,
            FacebookEnabled: (s?.FacebookLoginEnabled ?? !string.IsNullOrWhiteSpace(fbId))
                && !string.IsNullOrWhiteSpace(fbId),
            FacebookAppId: fbId,
            LineEnabled: (s?.LineLoginEnabled ?? !string.IsNullOrWhiteSpace(lineId))
                && !string.IsNullOrWhiteSpace(lineId),
            LineChannelId: lineId,
            // ค่าเดียวกับที่ GetSsoRedirectUriAsync ใช้ตอนแลก code — ส่งให้หน้า
            // login ใช้ต่อ เพื่อให้ทั้งสองขั้นตอนอ้าง URL เดียวกันเป๊ะ
            LoginCallbackUrl: BuildLoginCallbackUrl(
                FirstNonEmpty(s?.AppBaseUrl, _config["App:BaseUrl"])),
            GoogleSecretConfigured: !string.IsNullOrWhiteSpace(
                FirstNonEmpty(s?.GoogleClientSecret, _config["OAuth:Google:ClientSecret"])));
    }

    private static string FirstNonEmpty(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) ? a.Trim() : (b ?? "").Trim();

    /// <summary>Callback URL เดียวของระบบ — ตัวกลางที่ทั้งฝั่งพาไป LINE (หน้า
    /// login) และฝั่งแลก code (server) ต้องใช้ร่วมกัน. baseUrl ว่าง = คืนค่าว่าง
    /// (ห้ามคืน "/login.html" แบบ relative ซึ่ง LINE ไม่รับและทำให้ error กำกวม)</summary>
    private static string BuildLoginCallbackUrl(string? baseUrl)
        => string.IsNullOrWhiteSpace(baseUrl) ? "" : baseUrl.TrimEnd('/') + "/login.html";

    /// <summary>Channel Secret ของ LINE — เก็บเข้ารหัสใน DB (SecretProtector)
    /// fallback appsettings สำหรับ deployment เดิม. **ห้ามส่งออกจาก server**</summary>
    private async Task<string> GetLineChannelSecretAsync()
    {
        var enc = await _db.SiteSettings.AsNoTracking()
            .Select(s => s.LineLoginChannelSecret).FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(enc))
            return (_secrets != null ? _secrets.Unprotect(enc) : enc) ?? "";
        return _config["OAuth:Line:ChannelSecret"] ?? "";
    }

    /// <summary>App Secret ของ Facebook — เก็บเข้ารหัสใน DB (SecretProtector)
    /// fallback appsettings. **ห้ามส่งออกจาก server**</summary>
    private async Task<string> GetFacebookAppSecretAsync()
    {
        var enc = await _db.SiteSettings.AsNoTracking()
            .Select(s => s.FacebookAppSecret).FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(enc))
            return (_secrets != null ? _secrets.Unprotect(enc) : enc) ?? "";
        return _config["OAuth:Facebook:AppSecret"] ?? "";
    }

    private static string HmacSha256Hex(string key, string message)
    {
        using var h = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(message)))
            .ToLowerInvariant();
    }

    /// <summary>Client Secret ของ Google — เก็บเข้ารหัสใน DB (SecretProtector)
    /// fallback appsettings สำหรับ deployment เดิม. **ห้ามส่งออกจาก server**
    /// มีค่า = ใช้ authorization-code redirect flow ได้ (ไม่ต้องพึ่งสคริปต์
    /// One Tap ที่ถูก CSP/ตัวบล็อก/นโยบายคุกกี้บุคคลที่สามขวางได้)</summary>
    private async Task<string> GetGoogleClientSecretAsync()
    {
        var enc = await _db.SiteSettings.AsNoTracking()
            .Select(s => s.GoogleClientSecret).FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(enc))
            return (_secrets != null ? _secrets.Unprotect(enc) : enc) ?? "";
        return _config["OAuth:Google:ClientSecret"] ?? "";
    }

    /// <summary>Callback URL ที่ต้องตรงกับที่ลงทะเบียนไว้ที่ผู้ให้บริการ —
    /// หน้า login ส่งผู้ใช้ไปด้วยค่านี้ ตอนแลก code ก็ต้องส่งค่าเดียวกัน
    /// (ทั้ง LINE และ Google ตรวจตรง ๆ ไม่ตรง = invalid_grant / redirect_uri_mismatch).
    /// ตัวเดียวของทั้งระบบ — ห้ามคำนวณซ้ำที่อื่น</summary>
    private async Task<string> GetSsoRedirectUriAsync(string provider)
    {
        var baseUrl = await _db.SiteSettings.AsNoTracking()
            .Select(s => s.AppBaseUrl).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = _config["App:BaseUrl"] ?? "";
        var url = BuildLoginCallbackUrl(baseUrl);
        // ยังไม่ได้ตั้ง URL ระบบ → เดิมคืน "/login.html" แบบ relative ซึ่ง provider
        // ปฏิเสธ แล้วผู้ใช้เห็นแค่ "แลก code ไม่สำเร็จ" โดยไม่รู้ว่าต้องไปตั้งอะไร
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                $"ยังไม่ได้ตั้ง \"URL ของระบบ\" (AppBaseUrl) — {provider} Login ต้องใช้ค่านี้สร้าง "
                + "Callback URL ที่ต้องตรงกับที่ลงทะเบียนไว้ฝั่งผู้ให้บริการ. "
                + "ตั้งที่หน้าแอดมิน → ตั้งค่าระบบ → URL ของระบบ แล้วลองใหม่");
        return url;
    }

    /// <summary>LINE Login — verify id_token กับ LINE Platform (§ตรวจ audience
    /// ด้วย Channel ID ของเราเอง กัน token ของแอปอื่นเอามาใช้). อีเมลมาก็ต่อเมื่อ
    /// channel ขอ scope `email` และผู้ใช้อนุญาต — ถ้าไม่มี ตัวเรียกจะ throw
    /// พร้อมข้อความบอกให้อนุญาตอีเมล (เหมือน provider อื่น)</summary>
    private async Task<SsoIdentity> ValidateLineTokenAsync(
        string idToken, string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            throw new UnauthorizedAccessException("ยังไม่ได้ตั้งค่า LINE Channel ID");
        using var http = new HttpClient();

        // หน้า login ส่ง "authorization code" มา (web flow) ไม่ใช่ id_token —
        // แลกเป็น id_token ที่นี่ (ต้องใช้ channel secret ซึ่งห้ามออกจาก server).
        // id_token เป็น JWT = มีจุดคั่น 2 ตัวเสมอ ใช้แยกสองกรณีได้
        if (idToken.Count(ch => ch == '.') != 2)
        {
            var secret = await GetLineChannelSecretAsync();
            if (string.IsNullOrWhiteSpace(secret))
                throw new UnauthorizedAccessException("ยังไม่ได้ตั้งค่า LINE Channel Secret");
            var tokenRes = await http.PostAsync("https://api.line.me/oauth2/v2.1/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = idToken,
                    ["redirect_uri"] = await GetSsoRedirectUriAsync("LINE"),
                    ["client_id"] = channelId,
                    ["client_secret"] = secret,
                }));
            if (!tokenRes.IsSuccessStatusCode)
                throw new UnauthorizedAccessException("แลก LINE authorization code ไม่สำเร็จ (ตรวจ Callback URL ใน LINE Developers ให้ตรงกับระบบ)");
            using var td = JsonDocument.Parse(await tokenRes.Content.ReadAsStringAsync());
            idToken = td.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(idToken))
                throw new UnauthorizedAccessException("LINE ไม่คืน id_token — ตรวจว่า scope มี openid");
        }

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id_token"] = idToken,
            ["client_id"] = channelId,
        });
        var res = await http.PostAsync("https://api.line.me/oauth2/v2.1/verify", body);
        if (!res.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("LINE token ไม่ถูกต้องหรือหมดอายุ");

        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // LINE ตรวจ aud ให้แล้วจาก client_id ที่ส่งไป — เช็คซ้ำกันพลาด
        var aud = root.TryGetProperty("aud", out var a) ? a.GetString() ?? "" : "";
        if (!string.IsNullOrEmpty(aud) && aud != channelId)
            throw new UnauthorizedAccessException("LINE token ไม่ตรงกับ Channel ID ของระบบ");

        var sub = root.TryGetProperty("sub", out var s) ? s.GetString() ?? "" : "";
        var email = root.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var picture = root.TryGetProperty("picture", out var pic) ? pic.GetString() ?? "" : "";
        // LINE คืนอีเมลเฉพาะเมื่อ channel ได้สิทธิ์ email (ต้องยื่นเอกสารขออนุมัติ)
        // และอีเมลนั้นคืออีเมลที่ผู้ใช้ยืนยันกับ LINE แล้ว ⇒ มีอีเมล = ยืนยันแล้ว
        // ไม่มีอีเมลก็ยังใช้ได้ — `sub` (LINE userId) คือตัวระบุตัวตนหลัก
        return new SsoIdentity(SsoIdentityPolicy.Line, sub, email, name, picture,
            !string.IsNullOrWhiteSpace(email));
    }

    /// <summary>Google Login — รับได้ทั้ง <c>id_token</c> (One Tap / GIS) และ
    /// <c>authorization code</c> จาก redirect flow เหมือนฝั่ง LINE.
    ///
    /// ⚠️ redirect flow เป็นเส้นหลักตั้งแต่รอบ 118: สคริปต์ One Tap
    /// (accounts.google.com/gsi/client) ต้องผ่าน CSP + ตัวบล็อกโฆษณา + คุกกี้
    /// บุคคลที่สาม/FedCM จึงจะทำงาน — พังได้หลายทางโดยหน้าเว็บไม่รู้สาเหตุ
    /// ส่วน redirect ใช้แค่การเปลี่ยนหน้า ไม่มีอะไรมาขวางได้</summary>
    private async Task<SsoIdentity> ValidateGoogleTokenAsync(
        string idToken, string expectedClientIdOverride)
    {
        using var http = new HttpClient();

        // id_token เป็น JWT = มีจุดคั่น 2 ตัวเสมอ — ไม่ใช่ = authorization code
        // ที่ต้องแลกด้วย client secret (ห้ามออกจาก server) เหมือน ValidateLineTokenAsync
        if (idToken.Count(ch => ch == '.') != 2)
        {
            var secret = await GetGoogleClientSecretAsync();
            if (string.IsNullOrWhiteSpace(secret))
                throw new UnauthorizedAccessException(
                    "ยังไม่ได้ตั้ง Google Client Secret — ตั้งที่หน้าแอดมิน → "
                    + "เข้าสู่ระบบ (Google/Facebook/LINE) แล้วลองใหม่");
            var clientIdForExchange = !string.IsNullOrWhiteSpace(expectedClientIdOverride)
                ? expectedClientIdOverride
                : (_config["OAuth:Google:ClientId"] ?? "");
            var tokenRes = await http.PostAsync("https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = idToken,
                    ["redirect_uri"] = await GetSsoRedirectUriAsync("Google"),
                    ["client_id"] = clientIdForExchange,
                    ["client_secret"] = secret,
                }));
            if (!tokenRes.IsSuccessStatusCode)
                throw new UnauthorizedAccessException(
                    "แลก Google authorization code ไม่สำเร็จ (ตรวจ Authorized redirect URI "
                    + "ใน Google Cloud Console ให้ตรงกับ Callback URL ของระบบ)");
            using var td = JsonDocument.Parse(await tokenRes.Content.ReadAsStringAsync());
            idToken = td.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(idToken))
                throw new UnauthorizedAccessException("Google ไม่คืน id_token — ตรวจว่า scope มี openid");
        }

        var res = await http.GetAsync($"https://oauth2.googleapis.com/tokeninfo?id_token={Uri.EscapeDataString(idToken)}");
        if (!res.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("Google token ไม่ถูกต้องหรือหมดอายุ");

        var json = await res.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify audience matches our client ID
        var aud = root.GetProperty("aud").GetString() ?? "";
        var expectedClientId = !string.IsNullOrWhiteSpace(expectedClientIdOverride)
            ? expectedClientIdOverride
            : (_config["OAuth:Google:ClientId"] ?? "");
        if (!string.IsNullOrEmpty(expectedClientId) && aud != expectedClientId)
            throw new UnauthorizedAccessException("Google token audience ไม่ตรงกับ client ID");

        var sub = root.GetProperty("sub").GetString() ?? "";
        var email = root.GetProperty("email").GetString() ?? "";
        var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";

        // ⚠️ `email_verified` เป็นตัวชี้ขาดว่าจะ "ผูกเข้าบัญชีเดิม" ได้ไหม — เดิม
        // ไม่เคยอ่านเลย ⇒ อีเมลที่ Google ยังไม่ยืนยันก็ยึดบัญชีในระบบเราได้
        // tokeninfo คืนมาเป็น **สตริง** "true"/"false" (บาง endpoint เป็น bool)
        var verified = root.TryGetProperty("email_verified", out var ev)
            && (ev.ValueKind == JsonValueKind.True
                || (ev.ValueKind == JsonValueKind.String
                    && string.Equals(ev.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

        var picture = root.TryGetProperty("picture", out var picProp) ? picProp.GetString() ?? "" : "";
        return new SsoIdentity(SsoIdentityPolicy.Google, sub, email, name, picture, verified);
    }

    private async Task<SsoIdentity> ValidateFacebookTokenAsync(string accessToken, string expectedAppId)
    {
        using var http = new HttpClient();

        // ⚠️ ต้องพิสูจน์ว่า token ออกให้ **แอปของเรา** ก่อน — graph /me รับ token
        // ของแอปไหนก็ได้ ⇒ ผู้โจมตีใช้แอป Facebook ของตัวเองปั๊ม identity +
        // อีเมลของคนอื่นมายิงเส้นสมัคร เพื่อจองอีเมลเหยื่อล่วงหน้าเป็นชุด
        // (Google/LINE ตรวจ aud อยู่แล้ว — Facebook คือด่านที่อ่อนกว่าโดยไม่มีเหตุผล)
        var appSecret = await GetFacebookAppSecretAsync();
        if (!string.IsNullOrWhiteSpace(expectedAppId) && !string.IsNullOrWhiteSpace(appSecret))
        {
            var dbg = await http.GetAsync("https://graph.facebook.com/debug_token"
                + $"?input_token={Uri.EscapeDataString(accessToken)}"
                + $"&access_token={Uri.EscapeDataString(expectedAppId + "|" + appSecret)}");
            if (!dbg.IsSuccessStatusCode)
                throw new UnauthorizedAccessException("ตรวจสอบ Facebook token ไม่สำเร็จ");
            using var dd = JsonDocument.Parse(await dbg.Content.ReadAsStringAsync());
            var data = dd.RootElement.TryGetProperty("data", out var dEl) ? dEl : default;
            var appId = data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("app_id", out var aEl) ? aEl.GetString() ?? "" : "";
            var isValid = data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("is_valid", out var vEl) && vEl.ValueKind == JsonValueKind.True;
            if (!isValid || appId != expectedAppId)
                throw new UnauthorizedAccessException(
                    "Facebook token ไม่ได้ออกให้แอปของระบบนี้ — ลองกดปุ่ม Facebook ใหม่อีกครั้ง");
        }

        // appsecret_proof — กัน token ที่หลุดออกไปถูกใช้จากที่อื่น
        var proof = string.IsNullOrWhiteSpace(appSecret) ? null : HmacSha256Hex(appSecret, accessToken);
        var res = await http.GetAsync(
            $"https://graph.facebook.com/me?fields=id,name,email&access_token={Uri.EscapeDataString(accessToken)}"
            + (proof == null ? "" : $"&appsecret_proof={proof}"));
        if (!res.IsSuccessStatusCode)
            throw new UnauthorizedAccessException("Facebook token ไม่ถูกต้องหรือหมดอายุ");

        var json = await res.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id = root.GetProperty("id").GetString() ?? "";
        var email = root.TryGetProperty("email", out var emailProp) ? emailProp.GetString() ?? "" : "";
        var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";

        // Graph API **ไม่มีสัญญาณยืนยันอีเมลเลย** — จึงผูกเข้าบัญชีเดิมทันทีไม่ได้
        // (ต้องส่งลิงก์ยืนยันไปที่อีเมลก่อน ดู SsoIdentityPolicy)
        return new SsoIdentity(SsoIdentityPolicy.Facebook, id, email, name, null, false);
    }

    // Grace period before a user with a weak (legacy) password is forced to change it.
    private const int WeakPasswordGraceDays = 30;

    /// <summary>
    /// Non-throwing password rule check. Returns true when the password meets
    /// the current complexity rules (8+ chars + at least 2 of 4 categories).
    /// </summary>
    private static bool IsPasswordCompliant(string? password)
    {
        password ??= "";
        if (password.Length < 8) return false;

        var categories = 0;
        if (Regex.IsMatch(password, @"[A-Z]")) categories++;
        if (Regex.IsMatch(password, @"[a-z]")) categories++;
        if (Regex.IsMatch(password, @"[0-9]")) categories++;
        if (Regex.IsMatch(password, @"[!@#$%^&*()_+\-=\[\]{};':""\\|,.<>\/?~`]")) categories++;
        return categories >= 2;
    }

    /// <summary>
    /// Password validation for register/change/reset flows:
    /// - Minimum 8 characters
    /// - Must satisfy at least 2 of 4 complexity categories: uppercase, lowercase, digit, special
    /// All failing rules are reported in a single message so the user can fix them in one go.
    /// </summary>
    private static void ValidatePassword(string password)
    {
        password ??= "";
        var errors = new List<string>();

        if (password.Length < 8)
            errors.Add("ความยาวอย่างน้อย 8 ตัวอักษร");

        var categories = 0;
        if (Regex.IsMatch(password, @"[A-Z]")) categories++;
        if (Regex.IsMatch(password, @"[a-z]")) categories++;
        if (Regex.IsMatch(password, @"[0-9]")) categories++;
        if (Regex.IsMatch(password, @"[!@#$%^&*()_+\-=\[\]{};':""\\|,.<>\/?~`]")) categories++;

        if (categories < 2)
            errors.Add("ผสมอย่างน้อย 2 ประเภทจาก: ตัวพิมพ์ใหญ่ (A-Z), ตัวพิมพ์เล็ก (a-z), ตัวเลข (0-9), อักขระพิเศษ (!@#$...)");

        if (errors.Count > 0)
            throw new InvalidOperationException("รหัสผ่านไม่ปลอดภัย — " + string.Join(" และ ", errors));
    }

    /// <summary>
    /// Build a PasswordWeakNotice for users still inside the grace period.
    /// Returns null when grace has expired (caller should force change instead).
    /// </summary>
    private static PasswordWeakNotice? BuildPasswordWeakNotice(DateTime detectedAt)
    {
        var deadline = detectedAt.AddDays(WeakPasswordGraceDays);
        var daysRemaining = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalDays);
        var forced = daysRemaining <= 0;
        return new PasswordWeakNotice(
            detectedAt,
            deadline,
            Math.Max(0, daysRemaining),
            forced,
            forced
                ? "รหัสผ่านปัจจุบันไม่เป็นไปตามมาตรฐานความปลอดภัย กรุณาเปลี่ยนรหัสผ่านก่อนใช้งานต่อ"
                : $"รหัสผ่านปัจจุบันไม่ปลอดภัย กรุณาเปลี่ยนภายใน {Math.Max(1, daysRemaining)} วัน"
        );
    }

    private async Task<LoginResponse> GenerateLoginResponse(User user, PasswordWeakNotice? weakNotice = null)
    {
        var accessToken = JwtHelper.GenerateToken(user.Id, user.Email, user.FullName, _config, user.IsSystemAdmin);
        var refreshToken = JwtHelper.GenerateRefreshToken();
        var refreshDays = int.TryParse(_config["Jwt:RefreshTokenDays"], out var rd) ? rd : 7;

        // Rotate: keep the PREVIOUS token for re-use detection in the next
        // refresh. Re-presenting it triggers a session kill.
        user.PreviousRefreshToken = user.RefreshToken;
        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(refreshDays);
        user.RefreshTokenRevokedAt = null;
        await _db.SaveChangesAsync();

        var expireMinutes = int.TryParse(_config["Jwt:ExpireMinutes"], out var em) ? em : 60;

        return new LoginResponse(
            accessToken,
            refreshToken,
            DateTime.UtcNow.AddMinutes(expireMinutes),
            new UserInfo(user.Id, user.Email, user.FullName, user.Phone, user.IsSystemAdmin),
            weakNotice);
    }
}
