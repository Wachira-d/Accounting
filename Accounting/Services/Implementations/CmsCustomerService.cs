using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CmsCustomerService : ICmsCustomerService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CmsCustomerService> _logger;
    private readonly IConfiguration _config;
    // รอบ 209 P6 (D7): ฟอร์ม → lead · อีเมลแจ้งร้าน/ตอบกลับ (null = DI ไม่ครบในเทสต์เก่า — ฟอร์มที่ต้องใช้แจ้งผลบนแถว/โยนชัด)
    private readonly CmsLeadService? _leads;
    private readonly IEmailService? _email;

    public CmsCustomerService(AccountingDbContext db, ILogger<CmsCustomerService> logger, IConfiguration config,
        CmsLeadService? leads = null, IEmailService? email = null)
    {
        _db = db;
        _logger = logger;
        _config = config;
        _leads = leads;
        _email = email;
    }

    // ===== Customers =====

    public async Task<SiteCustomerResponse> CreateCustomerAsync(Guid companyId, Guid siteId, CreateSiteCustomerRequest request, string userId)
    {
        if (await _db.SiteCustomers.AnyAsync(c => c.SiteId == siteId && c.Email == request.Email))
            throw new InvalidOperationException("Customer with this email already exists on this site.");

        var customer = new SiteCustomer
        {
            CompanyId = companyId, SiteId = siteId, Email = request.Email,
            FullName = request.FullName, Phone = request.Phone,
            TaxId = request.TaxId, BranchCode = request.BranchCode,
            CompanyName = request.CompanyName,
            PreferredLanguage = request.PreferredLanguage,
            PreferredCurrency = request.PreferredCurrency,
            AcceptMarketing = request.AcceptMarketing,
            Tags = request.Tags, CustomerGroup = request.CustomerGroup,
            CreatedBy = userId
        };

        if (!string.IsNullOrEmpty(request.Password))
            customer.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

        _db.SiteCustomers.Add(customer);
        await _db.SaveChangesAsync();

        // Auto-link to ERP Contact
        await AutoLinkToErpContactAsync(companyId, siteId, customer.Id);

        return await GetCustomerAsync(companyId, siteId, customer.Id) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<SiteCustomerResponse> UpdateCustomerAsync(Guid companyId, Guid siteId, Guid customerId, UpdateSiteCustomerRequest request, string userId)
    {
        var c = await _db.SiteCustomers.FirstOrDefaultAsync(c => c.Id == customerId && c.SiteId == siteId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Customer not found.");

        if (request.FullName != null) c.FullName = request.FullName;
        if (request.Phone != null) c.Phone = request.Phone;
        if (request.TaxId != null) c.TaxId = request.TaxId;
        if (request.BranchCode != null) c.BranchCode = request.BranchCode;
        if (request.CompanyName != null) c.CompanyName = request.CompanyName;
        if (request.PreferredLanguage != null) c.PreferredLanguage = request.PreferredLanguage;
        if (request.PreferredCurrency != null) c.PreferredCurrency = request.PreferredCurrency;
        if (request.AcceptMarketing.HasValue) c.AcceptMarketing = request.AcceptMarketing.Value;
        if (request.IsActive.HasValue) c.IsActive = request.IsActive.Value;
        if (request.Tags != null) c.Tags = request.Tags;
        if (request.CustomerGroup != null) c.CustomerGroup = request.CustomerGroup;
        c.UpdatedBy = userId;

        await _db.SaveChangesAsync();
        return await GetCustomerAsync(companyId, siteId, customerId) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<SiteCustomerResponse?> GetCustomerAsync(Guid companyId, Guid siteId, Guid customerId)
    {
        return await _db.SiteCustomers.AsNoTracking()
            .Where(c => c.Id == customerId && c.SiteId == siteId && c.CompanyId == companyId)
            .Select(c => new SiteCustomerResponse
            {
                Id = c.Id, Email = c.Email, FullName = c.FullName, Phone = c.Phone,
                TaxId = c.TaxId, BranchCode = c.BranchCode, CompanyName = c.CompanyName,
                ContactId = c.ContactId,
                ContactName = c.Contact != null ? c.Contact.Name : null,
                PreferredLanguage = c.PreferredLanguage, PreferredCurrency = c.PreferredCurrency,
                AcceptMarketing = c.AcceptMarketing, ConsentGiven = c.ConsentGiven,
                ConsentGivenAt = c.ConsentGivenAt, IsActive = c.IsActive,
                EmailVerified = c.EmailVerified, LastLoginAt = c.LastLoginAt,
                Tags = c.Tags, CustomerGroup = c.CustomerGroup,
                OrderCount = c.Orders.Count(o => !o.IsDeleted),
                BookingCount = c.Bookings.Count(b => !b.IsDeleted),
                TotalSpent = c.Orders.Where(o => !o.IsDeleted && o.Status != SiteOrderStatus.Cancelled).Sum(o => o.TotalAmount),
                CreatedAt = c.CreatedAt
            })
            .FirstOrDefaultAsync();
    }

    public async Task<PagedResponse<SiteCustomerListResponse>> GetCustomersAsync(Guid companyId, Guid siteId, string? search, string? group, int page, int pageSize)
    {
        var query = _db.SiteCustomers.AsNoTracking().Where(c => c.SiteId == siteId && c.CompanyId == companyId);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(c => c.Email.Contains(search) || (c.FullName != null && c.FullName.Contains(search)) || (c.Phone != null && c.Phone.Contains(search)));
        if (!string.IsNullOrWhiteSpace(group))
            query = query.Where(c => c.CustomerGroup == group);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new SiteCustomerListResponse
            {
                Id = c.Id, Email = c.Email, FullName = c.FullName, Phone = c.Phone,
                IsActive = c.IsActive, CustomerGroup = c.CustomerGroup,
                OrderCount = c.Orders.Count(o => !o.IsDeleted),
                TotalSpent = c.Orders.Where(o => !o.IsDeleted && o.Status != SiteOrderStatus.Cancelled).Sum(o => o.TotalAmount),
                LastLoginAt = c.LastLoginAt, CreatedAt = c.CreatedAt
            })
            .ToListAsync();

        return new PagedResponse<SiteCustomerListResponse>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<bool> DeleteCustomerAsync(Guid companyId, Guid siteId, Guid customerId)
    {
        // Multi-tenant guard — the prior query trusted Site+Customer Ids alone,
        // so a caller who guessed (or learned via another tenant) a customer
        // GUID could soft-delete that record by hitting their own company's URL.
        var c = await _db.SiteCustomers.FirstOrDefaultAsync(c =>
            c.Id == customerId && c.SiteId == siteId && c.CompanyId == companyId);
        if (c == null) return false;
        c.IsDeleted = true;
        c.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Portal Auth =====

    public async Task<CustomerLoginResponse> CustomerLoginAsync(Guid companyId, Guid siteId, CustomerLoginRequest request)
    {
        var customer = await _db.SiteCustomers.FirstOrDefaultAsync(c => c.SiteId == siteId && c.Email == request.Email && c.IsActive)
            ?? throw new UnauthorizedAccessException("Invalid email or password.");

        if (customer.LockoutEnd.HasValue && customer.LockoutEnd > DateTime.UtcNow)
            throw new UnauthorizedAccessException("Account is temporarily locked.");

        if (string.IsNullOrEmpty(customer.PasswordHash) || !BCrypt.Net.BCrypt.Verify(request.Password, customer.PasswordHash))
        {
            customer.FailedLoginAttempts++;
            if (customer.FailedLoginAttempts >= 5)
                customer.LockoutEnd = DateTime.UtcNow.AddMinutes(15);
            await _db.SaveChangesAsync();
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        customer.FailedLoginAttempts = 0;
        customer.LockoutEnd = null;
        customer.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // ⚠️ ห้ามใช้ `JwtHelper.GenerateToken` ตัวเดียวกับผู้ใช้ ERP — โทเคนที่
        // ออกให้คนที่สมัครหน้าร้านเองได้ฟรี จะมี key/issuer/audience/รูปร่าง claim
        // เหมือนโทเคนพนักงานทุกประการ ⇒ ผ่าน [Authorize] ของ ERP ทุกตัว
        // (ผลตรวจทีม A · A-02) · ตัวนี้ใช้ audience คนละค่า ⇒ scheme ของ ERP
        // ปฏิเสธตั้งแต่ชั้น validate
        var token = JwtHelper.GenerateStorefrontCustomerToken(
            customer.Id, siteId, customer.Email, customer.FullName ?? "", _config);
        return new CustomerLoginResponse
        {
            Token = token, FullName = customer.FullName, Email = customer.Email,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        };
    }

    public async Task<SiteCustomerResponse> CustomerRegisterAsync(Guid companyId, Guid siteId, CustomerRegisterRequest request)
    {
        if (await _db.SiteCustomers.AnyAsync(c => c.SiteId == siteId && c.Email == request.Email))
            throw new InvalidOperationException("An account with this email already exists.");

        var customer = new SiteCustomer
        {
            CompanyId = companyId, SiteId = siteId,
            Email = request.Email, FullName = request.FullName, Phone = request.Phone,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            AcceptMarketing = request.AcceptMarketing,
            ConsentGiven = request.ConsentGiven,
            ConsentGivenAt = request.ConsentGiven ? DateTime.UtcNow : null,
            EmailVerificationToken = Guid.NewGuid().ToString("N")
        };

        _db.SiteCustomers.Add(customer);
        await _db.SaveChangesAsync();

        await AutoLinkToErpContactAsync(companyId, siteId, customer.Id);

        return await GetCustomerAsync(companyId, siteId, customer.Id) ?? throw new InvalidOperationException("Failed.");
    }

    // ===== Addresses =====

    public async Task<CustomerAddressResponse> AddAddressAsync(Guid companyId, Guid siteId, Guid customerId, CreateCustomerAddressRequest request)
    {
        if (request.IsDefault)
        {
            var existing = await _db.SiteCustomerAddresses.Where(a => a.CustomerId == customerId && a.IsDefault && a.AddressType == request.AddressType).ToListAsync();
            existing.ForEach(a => a.IsDefault = false);
        }

        var addr = new SiteCustomerAddress
        {
            CompanyId = companyId, CustomerId = customerId,
            Label = request.Label, RecipientName = request.RecipientName, Phone = request.Phone,
            BuildingNumber = request.BuildingNumber, BuildingName = request.BuildingName,
            StreetName = request.StreetName, SubDistrict = request.SubDistrict,
            District = request.District, Province = request.Province,
            PostalCode = request.PostalCode, CountryCode = request.CountryCode,
            AddressLine = request.AddressLine, IsDefault = request.IsDefault,
            AddressType = request.AddressType
        };
        _db.SiteCustomerAddresses.Add(addr);
        await _db.SaveChangesAsync();

        return MapAddressResponse(addr);
    }

    public async Task<List<CustomerAddressResponse>> GetAddressesAsync(Guid companyId, Guid siteId, Guid customerId)
    {
        return await _db.SiteCustomerAddresses.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .OrderByDescending(a => a.IsDefault)
            .Select(a => new CustomerAddressResponse
            {
                Id = a.Id, Label = a.Label, RecipientName = a.RecipientName, Phone = a.Phone,
                BuildingNumber = a.BuildingNumber, BuildingName = a.BuildingName,
                StreetName = a.StreetName, SubDistrict = a.SubDistrict,
                District = a.District, Province = a.Province,
                PostalCode = a.PostalCode, CountryCode = a.CountryCode,
                AddressLine = a.AddressLine, IsDefault = a.IsDefault,
                AddressType = a.AddressType
            })
            .ToListAsync();
    }

    public async Task<bool> DeleteAddressAsync(Guid companyId, Guid siteId, Guid customerId, Guid addressId)
    {
        var addr = await _db.SiteCustomerAddresses.FirstOrDefaultAsync(a => a.Id == addressId && a.CustomerId == customerId);
        if (addr == null) return false;
        _db.SiteCustomerAddresses.Remove(addr);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== PDPA =====

    public async Task<bool> UpdateConsentAsync(Guid companyId, Guid siteId, Guid customerId, ConsentUpdateRequest request)
    {
        var c = await _db.SiteCustomers.FirstOrDefaultAsync(c => c.Id == customerId && c.SiteId == siteId);
        if (c == null) return false;

        c.ConsentGiven = request.ConsentGiven;
        c.ConsentGivenAt = request.ConsentGiven ? DateTime.UtcNow : null;
        if (!request.ConsentGiven) c.ConsentWithdrawnAt = DateTime.UtcNow;
        c.AcceptMarketing = request.AcceptMarketing;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ProcessDataDeletionAsync(Guid companyId, Guid siteId, DataDeletionRequest request, string userId)
    {
        var c = await _db.SiteCustomers.FirstOrDefaultAsync(c => c.Id == request.CustomerId && c.CompanyId == companyId);
        if (c == null) return false;

        // รอบ 209 P6 (D6): ช่องที่ล้างอยู่ที่ Helpers/CmsPdpaErasure ตัวเดียว (ใช้ร่วมกับ DSR ของ PdpaService) — เพิ่มถอดโทเคน/SSO/การตลาด
        c.DataDeletionRequestedAt = DateTime.UtcNow;
        CmsPdpaErasure.AnonymizeCustomer(c, DateTime.UtcNow);

        // Remove addresses
        var addresses = await _db.SiteCustomerAddresses.Where(a => a.CustomerId == request.CustomerId).ToListAsync();
        _db.SiteCustomerAddresses.RemoveRange(addresses);

        await _db.SaveChangesAsync();
        _logger.LogInformation("PDPA data deletion processed for customer {CustomerId} by {UserId}", request.CustomerId, userId);
        return true;
    }

    // ===== CRM Merge =====

    public async Task<bool> MergeCustomersAsync(Guid companyId, MergeCustomersRequest request, string userId)
    {
        var primary = await _db.SiteCustomers.FirstOrDefaultAsync(c => c.Id == request.PrimaryCustomerId && c.CompanyId == companyId);
        var merged = await _db.SiteCustomers.FirstOrDefaultAsync(c => c.Id == request.MergedCustomerId && c.CompanyId == companyId);
        if (primary == null || merged == null) return false;

        // Transfer orders
        var orders = await _db.SiteOrders.Where(o => o.CustomerId == merged.Id).ToListAsync();
        orders.ForEach(o => o.CustomerId = primary.Id);

        // Transfer bookings
        var bookings = await _db.SiteBookings.Where(b => b.CustomerId == merged.Id).ToListAsync();
        bookings.ForEach(b => b.CustomerId = primary.Id);

        // Log merge
        _db.SiteCustomerMerges.Add(new SiteCustomerMerge
        {
            CompanyId = companyId, PrimaryCustomerId = primary.Id,
            MergedCustomerId = merged.Id, MergedFromSiteId = merged.SiteId,
            MergeReason = request.MergeReason, MergedBy = userId
        });

        // Deactivate merged customer
        merged.IsDeleted = true;
        merged.IsActive = false;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Customer {MergedId} merged into {PrimaryId}", merged.Id, primary.Id);
        return true;
    }

    public async Task<bool> AutoLinkToErpContactAsync(Guid companyId, Guid siteId, Guid customerId)
    {
        var customer = await _db.SiteCustomers.FirstOrDefaultAsync(c => c.Id == customerId && c.CompanyId == companyId);
        if (customer == null || customer.ContactId.HasValue) return false;

        // รอบ 209 P6 (D4): ตัวหาเดียวของลูกค้าเว็บ (Helpers/CmsContactResolver) — เลขภาษี+สาขา → อีเมล → เบอร์ บนชุด SoftScope ·
        // เลขนี้มีเจ้าของแล้วคนละสาขา ⇒ ไม่ถอยไปอีเมล · walk-in/ลูกค้าทั่วไปไม่รับเลข (Reject) · เมธอดนี้ไม่สร้างผู้ติดต่อ (createIfMissing: false)
        var r = await CmsContactResolver.ResolveAsync(_db, companyId, CmsContactResolver.ForCustomer(customer), customer, createIfMissing: false);
        if (r.ContactId == null) return false;
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Forms =====

    public async Task<FormResponse> CreateFormAsync(Guid companyId, Guid siteId, CreateFormRequest request, string userId)
    {
        var form = new SiteForm
        {
            CompanyId = companyId, SiteId = siteId, Name = request.Name,
            Description = request.Description, FormType = request.FormType,
            NotifyEmails = request.NotifyEmails, SendAutoReply = request.SendAutoReply,
            AutoReplySubject = request.AutoReplySubject, AutoReplyBody = request.AutoReplyBody,
            SuccessMessage = request.SuccessMessage, RedirectUrl = request.RedirectUrl,
            RequireCaptcha = request.RequireCaptcha, RateLimitPerHour = request.RateLimitPerHour,
            CreateErpDocument = request.CreateErpDocument, ErpDocumentType = request.ErpDocumentType,
            CreatedBy = userId
        };
        _db.SiteForms.Add(form);

        if (request.Fields?.Any() == true)
        {
            foreach (var f in request.Fields)
            {
                _db.SiteFormFields.Add(new SiteFormField
                {
                    CompanyId = companyId, FormId = form.Id, FieldName = f.FieldName,
                    Label = f.Label, LabelEn = f.LabelEn, Placeholder = f.Placeholder,
                    FieldType = f.FieldType, IsRequired = f.IsRequired,
                    ValidationPattern = f.ValidationPattern, ValidationMessage = f.ValidationMessage,
                    OptionsJson = f.OptionsJson, DefaultValue = f.DefaultValue,
                    SortOrder = f.SortOrder, Width = f.Width, CreatedBy = userId
                });
            }
        }

        await _db.SaveChangesAsync();
        return await GetFormAsync(companyId, siteId, form.Id) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<FormResponse?> GetFormAsync(Guid companyId, Guid siteId, Guid formId)
    {
        return await _db.SiteForms.AsNoTracking()
            .Where(f => f.Id == formId && f.SiteId == siteId && f.CompanyId == companyId)
            .Select(f => new FormResponse
            {
                Id = f.Id, Name = f.Name, Description = f.Description, FormType = f.FormType,
                SendAutoReply = f.SendAutoReply, RequireCaptcha = f.RequireCaptcha,
                CreateErpDocument = f.CreateErpDocument, ErpDocumentType = f.ErpDocumentType,
                IsActive = f.IsActive, SubmissionCount = f.Submissions.Count,
                Fields = f.Fields.OrderBy(ff => ff.SortOrder).Select(ff => new FormFieldResponse
                {
                    Id = ff.Id, FieldName = ff.FieldName, Label = ff.Label, LabelEn = ff.LabelEn,
                    Placeholder = ff.Placeholder, FieldType = ff.FieldType, IsRequired = ff.IsRequired,
                    ValidationPattern = ff.ValidationPattern, OptionsJson = ff.OptionsJson,
                    DefaultValue = ff.DefaultValue, SortOrder = ff.SortOrder, Width = ff.Width
                }).ToList(),
                CreatedAt = f.CreatedAt
            })
            .FirstOrDefaultAsync();
    }

    public async Task<List<FormResponse>> GetFormsAsync(Guid companyId, Guid siteId)
    {
        return await _db.SiteForms.AsNoTracking()
            .Where(f => f.SiteId == siteId && f.CompanyId == companyId && f.IsActive)
            .Select(f => new FormResponse
            {
                Id = f.Id, Name = f.Name, Description = f.Description, FormType = f.FormType,
                IsActive = f.IsActive, SubmissionCount = f.Submissions.Count,
                CreatedAt = f.CreatedAt
            })
            .ToListAsync();
    }

    public async Task<bool> DeleteFormAsync(Guid companyId, Guid siteId, Guid formId)
    {
        var f = await _db.SiteForms.FirstOrDefaultAsync(f => f.Id == formId && f.SiteId == siteId);
        if (f == null) return false;
        f.IsActive = false;
        f.IsDeleted = true;
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Form Submissions =====

    public async Task<FormSubmissionResponse> SubmitFormAsync(Guid companyId, Guid siteId, Guid formId, SubmitFormRequest request, string? ipAddress, string? userAgent)
    {
        // รอบ 209 P6: ทางเข้านิรนาม ⇒ ฟอร์มต้องเป็นของบริษัทนี้ (กฎ M — เดิมกรองแค่ SiteId)
        var form = await _db.SiteForms.Include(f => f.Fields)
            .FirstOrDefaultAsync(f => f.Id == formId && f.SiteId == siteId && f.CompanyId == companyId && f.IsActive)
            ?? throw new KeyNotFoundException("Form not found or inactive.");

        // CAPTCHA verification
        if (form.RequireCaptcha)
        {
            var site = await _db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId);
            if (!string.IsNullOrEmpty(site?.CaptchaProvider) && !string.IsNullOrEmpty(site?.CaptchaSiteKey))
            {
                if (string.IsNullOrEmpty(request.CaptchaToken))
                    throw new InvalidOperationException("CAPTCHA token is required.");
                var secretKey = _config["Captcha:SecretKey"];
                if (!string.IsNullOrEmpty(secretKey))
                {
                    var verified = await VerifyCaptchaAsync(site.CaptchaProvider, secretKey, request.CaptchaToken);
                    if (!verified) throw new InvalidOperationException("CAPTCHA verification failed.");
                }
            }
        }

        // Rate limiting
        if (form.RateLimitPerHour.HasValue && !string.IsNullOrEmpty(ipAddress))
        {
            var hourAgo = DateTime.UtcNow.AddHours(-1);
            var recentCount = await _db.SiteFormSubmissions.CountAsync(s => s.FormId == formId && s.IpAddress == ipAddress && s.CreatedAt >= hourAgo);
            if (recentCount >= form.RateLimitPerHour.Value)
                throw new InvalidOperationException("Rate limit exceeded. Please try again later.");
        }

        // รอบ 209 P6 (ฑ/D7): ช่องมาตรฐานจากคำตอบ (อีเมล/เบอร์ใช้ผูกความยินยอม · สร้าง lead · ตอบกลับ) — ตัวแยกเดียว CmsFormLeadMapper
        var fields = CmsFormLeadMapper.Map(request.DataJson);
        var sub = new SiteFormSubmission
        {
            CompanyId = companyId, FormId = formId,
            DataJson = request.DataJson,
            IpAddress = ipAddress, UserAgent = userAgent
        };
        // ด่านยินยอม PDPA ก่อนบันทึก (Helpers/CmsDataConsent) · ติ๊กแล้ว ⇒ PdpaConsentRecord บันทึกพร้อมคำตอบ
        await CmsDataConsent.EnforceAsync(_db, companyId, siteId, CmsDataConsent.FormPurpose, request.ConsentGiven,
            actorUserId: null, customerId: null, fields.Email, fields.Phone, reference: $"form:{sub.Id:N}");

        _db.SiteFormSubmissions.Add(sub);
        await _db.SaveChangesAsync();

        // รอบ 209 P6 (D7): "ส่งเข้าระบบ" ⇒ CmsLead (เส้นมาตรฐาน) — เดิม _db.Documents.Add ตรง (ข้าม IDocumentService · ไม่มีผู้ติดต่อ · ไม่มีเลขรัน) ·
        // พนักงานแปลง lead เป็นใบเสนอราคาที่หน้า lead (ตัวหาผู้ติดต่อเดียว + IDocumentService)
        if (form.CreateErpDocument)
        {
            if (_leads == null)
                throw new InvalidOperationException("ฟอร์มนี้ตั้งให้ส่งเข้าระบบ แต่บริการ lead ไม่พร้อม — ติดต่อผู้ดูแลระบบ");
            var lead = await _leads.CreateFromFormAsync(companyId, siteId, new CreateLeadRequest(
                LeadType: CmsFormLeadMapper.LeadTypeFor(form.FormType, form.ErpDocumentType),
                SourceSlug: $"form:{form.Name}",
                CustomerName: fields.Name, CustomerEmail: fields.Email, CustomerPhone: fields.Phone,
                CustomerCompany: fields.Company, CustomerTaxId: fields.TaxId, Message: fields.Message,
                Fields: fields.Extras.Count > 0 ? fields.Extras : null));
            sub.LeadId = lead.Id;
        }

        // แจ้งร้าน (NotifyEmails) + ตอบกลับอัตโนมัติ (SendAutoReply) — ผลประทับบนแถว (ล้มไม่ล้มการส่งฟอร์ม · ไม่เงียบ · F2 #7)
        var notify = new List<string>();
        var siteName = await _db.Sites.AsNoTracking().Where(x => x.Id == siteId && x.CompanyId == companyId)
            .Select(x => x.Name).FirstOrDefaultAsync() ?? "";
        var recipients = CmsFormLeadMapper.Recipients(form.NotifyEmails);
        if (recipients.Count > 0)
        {
            var html = CmsFormLeadMapper.NotifyHtml(form.Name, siteName, fields);
            foreach (var to in recipients)
            {
                var ok = _email != null && await _email.TrySendAsync(to, $"[{siteName}] คำตอบใหม่: {form.Name}", html);
                notify.Add(ok ? $"แจ้งร้าน {to} ✓" : $"แจ้งร้าน {to} ไม่สำเร็จ (ยังไม่ตั้งค่าอีเมลระบบ หรือส่งล้ม)");
            }
        }
        if (form.SendAutoReply)
        {
            var replyTo = CmsContactResolver.NormalizeEmail(fields.Email);
            if (replyTo == null) notify.Add("ตอบกลับอัตโนมัติ: ไม่มีอีเมลผู้กรอก");
            else
            {
                var ok = _email != null && await _email.TrySendAsync(replyTo,
                    string.IsNullOrWhiteSpace(form.AutoReplySubject) ? $"ขอบคุณที่ติดต่อ {siteName}" : form.AutoReplySubject!,
                    CmsFormLeadMapper.AutoReplyHtml(form.AutoReplyBody, siteName));
                notify.Add(ok ? "ตอบกลับอัตโนมัติ ✓" : "ตอบกลับอัตโนมัติไม่สำเร็จ (ยังไม่ตั้งค่าอีเมลระบบ หรือส่งล้ม)");
            }
        }
        if (notify.Count > 0) sub.NotifyResult = string.Join(" · ", notify);
        if (sub.LeadId != null || sub.NotifyResult != null) await _db.SaveChangesAsync();

        return new FormSubmissionResponse
        {
            Id = sub.Id, FormId = formId, FormName = form.Name,
            DataJson = sub.DataJson, Status = sub.Status,
            ErpDocumentId = sub.ErpDocumentId, LeadId = sub.LeadId, NotifyResult = sub.NotifyResult,
            IpAddress = sub.IpAddress, CreatedAt = sub.CreatedAt
        };
    }

    public async Task<PagedResponse<FormSubmissionResponse>> GetSubmissionsAsync(Guid companyId, Guid siteId, Guid formId, string? status, int page, int pageSize)
    {
        var query = _db.SiteFormSubmissions.AsNoTracking().Where(s => s.FormId == formId);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<FormSubmissionStatus>(status, out var st))
            query = query.Where(s => s.Status == st);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new FormSubmissionResponse
            {
                Id = s.Id, FormId = s.FormId, FormName = s.Form.Name,
                DataJson = s.DataJson, Status = s.Status,
                CustomerId = s.CustomerId,
                CustomerName = s.Customer != null ? s.Customer.FullName : null,
                ErpDocumentId = s.ErpDocumentId, LeadId = s.LeadId, NotifyResult = s.NotifyResult,
                Notes = s.Notes, RepliedBy = s.RepliedBy, RepliedAt = s.RepliedAt,
                IpAddress = s.IpAddress, CreatedAt = s.CreatedAt
            })
            .ToListAsync();

        return new PagedResponse<FormSubmissionResponse>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<bool> UpdateSubmissionStatusAsync(Guid companyId, Guid siteId, Guid formId, Guid submissionId, string status, string? notes)
    {
        var sub = await _db.SiteFormSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId && s.FormId == formId);
        if (sub == null) return false;

        if (Enum.TryParse<FormSubmissionStatus>(status, out var st))
            sub.Status = st;
        if (notes != null) sub.Notes = notes;

        await _db.SaveChangesAsync();
        return true;
    }

    private static async Task<bool> VerifyCaptchaAsync(string provider, string secretKey, string token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        string verifyUrl;
        if (provider.Equals("hcaptcha", StringComparison.OrdinalIgnoreCase))
            verifyUrl = "https://api.hcaptcha.com/siteverify";
        else
            verifyUrl = "https://www.google.com/recaptcha/api/siteverify";

        var resp = await http.PostAsync(verifyUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["secret"] = secretKey,
            ["response"] = token
        }));
        if (!resp.IsSuccessStatusCode) return false;
        var json = await resp.Content.ReadAsStringAsync();
        return json.Contains("\"success\":true") || json.Contains("\"success\": true");
    }

    private static CustomerAddressResponse MapAddressResponse(SiteCustomerAddress a) => new()
    {
        Id = a.Id, Label = a.Label, RecipientName = a.RecipientName, Phone = a.Phone,
        BuildingNumber = a.BuildingNumber, BuildingName = a.BuildingName,
        StreetName = a.StreetName, SubDistrict = a.SubDistrict,
        District = a.District, Province = a.Province,
        PostalCode = a.PostalCode, CountryCode = a.CountryCode,
        AddressLine = a.AddressLine, IsDefault = a.IsDefault, AddressType = a.AddressType
    };
}
