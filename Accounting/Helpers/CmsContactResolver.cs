using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ผู้ติดต่อของลูกค้าเว็บ — ข้อเท็จจริงจากออเดอร์/การจอง/lead/บัญชีลูกค้าเว็บ (ผู้เรียกเติมจาก entity ของตัวเอง)</summary>
/// <param name="LinkedContactId">ผู้ติดต่อที่ผูกไว้แล้ว (<c>SiteCustomer.ContactId</c>) — ชนะทุกกติกาเมื่อแถวยังใช้ได้</param>
/// <param name="CreatedBy">ป้ายผู้สร้างแถวใหม่ (เช่น "storefront-order") — ใช้เมื่อต้องสร้างเท่านั้น</param>
/// <param name="FallbackName">ชื่อเมื่อไม่มีทั้งชื่อบริษัทและชื่อคน (เช่น "Online Guest")</param>
/// <param name="BuyerTaxIdExplicit">ผู้ซื้อกรอกเลขผู้เสียภาษีเพื่อขอใบกำกับในเอกสารใบนี้ (<c>SiteOrder.BillingTaxId</c>) — รอบ 209 ฝ่ายค้าน (เงิน) ข้อ 5:
/// จับคู่ด้วยเลขภาษี+สาขาเท่านั้น (ไม่ถอยอีเมล/เบอร์ · ไม่เติมเลขลงแถวคนอื่น) และผู้ติดต่อที่ผูกไว้ต้องถือเลข+สาขาเดียวกันจึงชนะ</param>
public sealed record CmsContactInput(
    Guid? LinkedContactId,
    string? TaxId,
    string? BranchCode,
    string? Email,
    string? Phone,
    string? PersonName,
    string? CompanyName,
    string? Address,
    string CreatedBy,
    string FallbackName = "ลูกค้าออนไลน์",
    bool BuyerTaxIdExplicit = false)
{
    /// <summary>โหมดเข้ม: ขอใบกำกับพร้อมเลขภาษีของผู้ซื้อ ⇒ ผู้ซื้อในใบกำกับ (§86/4(3)) ตัดสินด้วยเลข+สาขาเท่านั้น</summary>
    public bool StrictTaxKey => BuyerTaxIdExplicit && !string.IsNullOrWhiteSpace(TaxId);
}

/// <summary>จับคู่ได้ด้วยอะไร (log/เทสต์ · <see cref="Created"/> = สร้างแถวใหม่)</summary>
public enum CmsContactMatchKind { None, Linked, TaxKey, Email, Phone, Created }

public readonly record struct CmsContactDecision(Contact? Match, CmsContactMatchKind Kind);

/// <summary>ผลของ <see cref="CmsContactResolver.ResolveAsync"/> — <see cref="ContactId"/> = null เฉพาะเมื่อ <c>createIfMissing=false</c> และไม่พบ</summary>
public readonly record struct CmsContactResolution(Guid? ContactId, CmsContactMatchKind Kind);

/// <summary>
/// **ตัวหาผู้ติดต่อตัวเดียวของลูกค้าเว็บ (CMS)** — รอบ 209 P6 · R-209-D4 · CLAUDE.md #4 A "Resolver กลาง"
///
/// <para><b>ที่มา</b>: เดิมมี 5 สำเนาที่กติกาต่างกัน — <c>CmsCommerceService.ResolveErpContactForOrderAsync</c> (อีเมลตรงตัว ·
/// <b>ไม่กรองแถวที่ลบแล้ว</b>) · <c>ResolveOrCreateContactFromOrderAsync</c> (กรอง IsDeleted) · <c>CmsBookingService.SyncBookingToErpAsync</c>
/// (อีเมลตรงตัว ไม่กรอง) · <c>CmsLeadService.EnsureContactLinkedAsync</c> (เลขภาษี+สาขา → อีเมล) · <c>CmsCustomerService.AutoLinkToErpContactAsync</c>
/// (เลขภาษี+สาขา → อีเมล บนแถว IsActive) ⇒ ลูกค้ารายเดียวกันได้ผู้ติดต่อคนละแถวตามทางเข้า (ใบขายเว็บกับใบมัดจำจองคนละลูกหนี้)</para>
///
/// <para><b>กติกา</b> (ทุกขั้นกรอง <c>CompanyId</c> + ไม่ลบ + ใช้งานอยู่ · เรียงแถวแน่นอน — ผลไม่ขึ้นกับลำดับจากฐาน):</para>
/// <list type="number">
/// <item>ผู้ติดต่อที่ผูกไว้แล้ว (<see cref="CmsContactInput.LinkedContactId"/>) ที่ยังใช้ได้</item>
/// <item>เลขภาษี + สาขา ผ่าน <see cref="ContactTaxBranchKey"/> ตัวเดียวของระบบ (ตัวเลขล้วนตรงกันเท่านั้น — <b>ห้าม fuzzy บนตัวเลข</b>)</item>
/// <item>อีเมล (ตัดช่องว่าง · ไม่สนตัวพิมพ์) บนชุด <see cref="ContactTaxBranchKey.SoftScope(IEnumerable{Contact}, string?, ContactKeyMatch)"/>
///   — มีเลขภาษีแต่เลขนั้นมีเจ้าของแล้วคนละสาขา ⇒ ไม่ถอยมาอีเมล/เบอร์ (ได้แถวสาขาอื่นของนิติบุคคลเดียวกัน)</item>
/// <item>เบอร์โทร (ตัวเลขล้วน · +66 → 0 · ต้องยาว ≥ 9 หลัก) เทียบเท่ากันทั้งเบอร์บนชุดเดียวกัน</item>
/// <item>แถวที่จับได้ด้วยอีเมล/เบอร์ผ่าน <see cref="ContactTaxBranchKey.AdoptTaxId"/> (Reject = ห้ามใช้ ⇒ สร้างใหม่)</item>
/// <item><b>โหมดเข้ม</b> (<see cref="CmsContactInput.StrictTaxKey"/> — ขอใบกำกับพร้อมเลขภาษี · รอบ 209 ฝ่ายค้าน (เงิน) ข้อ 5): ข้อ 1 ชนะเฉพาะแถวที่ถือเลข+สาขาเดียวกัน ·
///   ข้าม 3–5 (ไม่จับด้วยอีเมล/เบอร์ · ไม่เติมเลขลงแถวอื่น) ⇒ ไม่พบเลข+สาขา = สร้างใหม่</item>
/// <item>ไม่พบ ⇒ สร้างผู้ติดต่อใหม่ (ชนิด/สาขาผ่าน <see cref="ContactTypeResolver.ResolveWithBranch"/> · ป้ายเลขภาษีไม่ผ่าน checksum)</item>
/// </list>
/// </summary>
public static class CmsContactResolver
{
    public const string RuleCode = "CMS-CONTACT";
    /// <summary>เบอร์สั้นกว่านี้ (เช่นเบอร์ภายใน 4 หลัก) ไม่ใช้จับคู่ — ชนกันง่ายเกินไป</summary>
    public const int MinPhoneDigits = 9;

    /// <summary>อีเมลเทียบกันได้ — ตัดช่องว่าง + ตัวพิมพ์เล็ก (ว่าง = null)</summary>
    public static string? NormalizeEmail(string? email)
    {
        var e = email?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(e) || !e.Contains('@') ? null : e;
    }

    /// <summary>เบอร์เทียบกันได้ — ตัวเลขล้วน · 66 นำหน้า (11 หลัก) ⇒ 0 · สั้นกว่า <see cref="MinPhoneDigits"/> = null (ไม่ใช้จับคู่)</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 11 && digits.StartsWith("66", StringComparison.Ordinal)) digits = "0" + digits[2..];
        return digits.Length < MinPhoneDigits ? null : digits;
    }

    /// <summary>
    /// **ตัวตัดสิน (pure)** บนแถวผู้สมัครที่ผู้เรียกโหลดมาแล้ว (กรองบริษัท/ไม่ลบ/ใช้งานอยู่มาแล้ว) — ไม่มี I/O ·
    /// แถวที่จับได้ด้วยอีเมล/เบอร์อาจถูกเติมเลขภาษี (<see cref="ContactTaxBranchKey.AdoptTaxId"/>) — ผู้เรียกต้อง SaveChanges
    /// </summary>
    public static CmsContactDecision Decide(IReadOnlyCollection<Contact> candidates, CmsContactInput input)
    {
        var rows = (candidates ?? Array.Empty<Contact>()).Where(c => !c.IsDeleted && c.IsActive)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList();

        var strict = input.StrictTaxKey;
        // รอบ 209 ฝ่ายค้าน (เงิน) ข้อ 5(b): ผู้ซื้อกรอกเลขภาษีเองในใบนี้ ⇒ ผู้ติดต่อที่ผูกไว้ชนะเฉพาะเมื่อถือเลข+สาขาเดียวกัน
        // (เดิมผูกไว้ชนะเสมอ ⇒ ใบกำกับออกชื่อ/เลขของผู้ติดต่อเดิม ทั้งที่ผู้ซื้อขอใบในนามนิติบุคคลอื่น)
        if (input.LinkedContactId is Guid linked && rows.FirstOrDefault(c => c.Id == linked) is Contact l
            && (!strict || ContactTaxBranchKey.Pick(new[] { new ContactKeyCandidate(l.Id, l.TaxId, l.BranchCode) },
                input.TaxId, input.BranchCode).ContactId == l.Id))
            return new CmsContactDecision(l, CmsContactMatchKind.Linked);

        var taxKey = ContactTaxBranchKey.Pick(rows.Select(c => new ContactKeyCandidate(c.Id, c.TaxId, c.BranchCode)),
            input.TaxId, input.BranchCode);
        if (taxKey.ContactId is Guid keyId)
            return new CmsContactDecision(rows.First(c => c.Id == keyId), CmsContactMatchKind.TaxKey);
        // ข้อ 5(a): ขอใบกำกับพร้อมเลขภาษี ⇒ ไม่พบเลข+สาขา = สร้างใหม่ — ห้ามถอยอีเมล/เบอร์ และห้าม AdoptTaxId ลงแถวที่จับได้ด้วยข้อมูลที่ใครก็กรอกได้
        // (เดิม: กรอกอีเมล/เบอร์ของลูกค้ารายอื่น + เลขภาษีของตน ⇒ เลขถูกเติมลงผู้ติดต่อของคนอื่น + ใบกำกับออกชื่อคนอื่น)
        if (strict) return new CmsContactDecision(null, CmsContactMatchKind.None);

        var soft = ContactTaxBranchKey.SoftScope(rows, input.TaxId, taxKey).ToList();
        Contact? hit = null;
        var kind = CmsContactMatchKind.None;
        var email = NormalizeEmail(input.Email);
        if (email != null)
        {
            hit = soft.FirstOrDefault(c => NormalizeEmail(c.Email) == email);
            if (hit != null) kind = CmsContactMatchKind.Email;
        }
        var phone = NormalizePhone(input.Phone);
        if (hit == null && phone != null)
        {
            hit = soft.FirstOrDefault(c => NormalizePhone(c.Phone) == phone);
            if (hit != null) kind = CmsContactMatchKind.Phone;
        }
        if (hit != null && ContactTaxBranchKey.AdoptTaxId(hit, input.TaxId, input.BranchCode,
                kind == CmsContactMatchKind.Email ? ContactMatchKind.Email : ContactMatchKind.Phone) == ContactAdoptOutcome.Reject)
            hit = null;
        return hit == null ? new CmsContactDecision(null, CmsContactMatchKind.None) : new CmsContactDecision(hit, kind);
    }

    /// <summary>ผู้ติดต่อใหม่จากข้อเท็จจริงของลูกค้าเว็บ (pure — ผู้เรียก Add + Save) · ชื่อบริษัทชนะชื่อคน (ผู้ซื้อในใบกำกับ)</summary>
    internal static Contact NewContact(Guid companyId, CmsContactInput input)
    {
        var name = !string.IsNullOrWhiteSpace(input.CompanyName) ? input.CompanyName!.Trim()
            : !string.IsNullOrWhiteSpace(input.PersonName) ? input.PersonName!.Trim()
            : input.FallbackName;
        var rawTax = string.IsNullOrWhiteSpace(input.TaxId) ? null : input.TaxId!.Trim();
        var digits = rawTax == null ? null : new string(rawTax.Where(char.IsAsciiDigit).ToArray());
        var taxId = rawTax == null ? null : (digits!.Length == 13 ? digits : rawTax);
        var identity = ContactTypeResolver.ResolveWithBranch(
            !string.IsNullOrWhiteSpace(input.CompanyName) && taxId == null ? "JuristicPerson" : null,
            taxId, input.BranchCode, name);
        var contact = new Contact
        {
            CompanyId = companyId,
            Name = name,
            ContactType = identity.Type,
            TaxId = taxId,
            BranchCode = taxId == null ? null : identity.BranchCode,
            Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email!.Trim(),
            Phone = string.IsNullOrWhiteSpace(input.Phone) ? null : input.Phone!.Trim(),
            Address = string.IsNullOrWhiteSpace(input.Address) ? null : input.Address!.Trim(),
            IsCustomer = true,
            IsSupplier = false,
            CreatedBy = input.CreatedBy,
        };
        ContactTaxBranchKey.StampTaxIdWarning(contact);
        return contact;
    }

    /// <summary>
    /// โหลดผู้สมัคร (บริษัทนี้ · ไม่ลบ · ใช้งานอยู่) → <see cref="Decide"/> → ไม่พบและ <paramref name="createIfMissing"/> ⇒ สร้าง + บันทึก ·
    /// จับได้แถวเดิมที่ยังไม่เป็นลูกค้า ⇒ ติด <c>IsCustomer</c> · ผูก <paramref name="customer"/>.ContactId เมื่อยังว่าง
    /// (ผู้เรียกบันทึกเองพร้อมงานของตน เว้นแต่สร้างแถวใหม่ซึ่งต้องบันทึกก่อนออกเอกสาร)
    /// </summary>
    public static async Task<CmsContactResolution> ResolveAsync(AccountingDbContext db, Guid companyId, CmsContactInput input,
        SiteCustomer? customer = null, bool createIfMissing = true, CancellationToken ct = default)
    {
        var scope = db.Contacts.Where(c => c.CompanyId == companyId && !c.IsDeleted && c.IsActive);
        var candidates = new List<Contact>();
        if (input.LinkedContactId is Guid linkedId)
            candidates.AddRange(await scope.Where(c => c.Id == linkedId).ToListAsync(ct));
        // ข้อ 5: โหมดเข้มต้องโหลดแถวเลขภาษีเสมอ (ผู้ติดต่อที่ผูกไว้อาจถือเลขอื่น) และไม่โหลดอีเมล/เบอร์ (ไม่ใช้จับคู่)
        if (candidates.Count == 0 || input.StrictTaxKey)
        {
            candidates.AddRange(await ContactTaxBranchKey.LoadByTaxIdsAsync(scope, companyId, new[] { input.TaxId }, ct));
            var email = input.StrictTaxKey ? null : NormalizeEmail(input.Email);
            if (email != null)
                candidates.AddRange(await scope.Where(c => c.Email != null && c.Email.Trim().ToLower() == email)
                    .OrderBy(c => c.CreatedAt).Take(50).ToListAsync(ct));
            var phone = input.StrictTaxKey ? null : NormalizePhone(input.Phone);
            if (phone != null)
            {
                // ตัวกรองหยาบฝั่งฐาน (4 หลักท้ายติดกันในทุกรูปแบบการพิมพ์เบอร์) แล้วเทียบทั้งเบอร์ในหน่วยความจำ — ไม่ใช่ fuzzy
                var tail = phone[^4..];
                candidates.AddRange(await scope.Where(c => c.Phone != null && c.Phone.Contains(tail))
                    .OrderBy(c => c.CreatedAt).Take(200).ToListAsync(ct));
            }
        }
        var distinct = candidates.GroupBy(c => c.Id).Select(g => g.First()).ToList();
        var decision = Decide(distinct, input);

        Guid? contactId = null;
        var kind = decision.Kind;
        if (decision.Match is Contact hit)
        {
            if (!hit.IsCustomer) { hit.IsCustomer = true; hit.UpdatedAt = DateTime.UtcNow; }
            contactId = hit.Id;
        }
        else if (createIfMissing)
        {
            var created = NewContact(companyId, input);
            db.Contacts.Add(created);
            await db.SaveChangesAsync(ct);
            contactId = created.Id;
            kind = CmsContactMatchKind.Created;
        }
        // รอบ 209 ฝ่ายค้าน (security ข้อ 1 · blocking): อีเมล/เบอร์/เลขภาษีบนบัญชีลูกค้าหน้าร้าน = ลูกค้ากรอกเอง ไม่ได้ยืนยัน ⇒ ห้ามผูกถาวร
        // (เดิม: สมัครด้วยเบอร์เหยื่อ → จอง 1 ครั้ง → ผูกผู้ติดต่อของเหยื่อ → เห็น/ใช้บัตรคอร์สของเหยื่อ) · ผูกถาวรได้เฉพาะผู้ติดต่อที่สร้างจากข้อมูลของลูกค้าคนนี้เอง ·
        // จับคู่หลวมยังใช้กับเอกสารใบนี้ได้ (ผู้ติดต่อบนใบขาย) · พนักงานผูกเองได้ที่หน้าลูกค้า (LinkToErp)
        if (contactId is Guid cid && customer != null && customer.ContactId == null && PersistsCustomerLink(kind))
            customer.ContactId = cid;
        return new CmsContactResolution(contactId, kind);
    }

    /// <summary>ผลจับคู่แบบไหนผูก <c>SiteCustomer.ContactId</c> ถาวรได้ — เฉพาะผู้ติดต่อที่สร้างใหม่จากข้อมูลของลูกค้าคนนี้ (ไม่ใช่ข้อมูลที่ใครก็กรอกได้)</summary>
    public static bool PersistsCustomerLink(CmsContactMatchKind kind) => kind == CmsContactMatchKind.Created;

    /// <summary>ข้อเท็จจริงของออเดอร์เว็บ (ลูกค้าเว็บชนะช่องผู้รับ · ช่องใบกำกับชนะบัญชีลูกค้าเรื่องเลขภาษี)</summary>
    public static CmsContactInput ForOrder(SiteOrder order, string createdBy) => new(
        LinkedContactId: order.Customer?.ContactId,
        TaxId: !string.IsNullOrWhiteSpace(order.BillingTaxId) ? order.BillingTaxId : order.Customer?.TaxId,
        BranchCode: !string.IsNullOrWhiteSpace(order.BillingTaxId) ? order.BillingBranchCode : order.Customer?.BranchCode,
        Email: order.Customer?.Email ?? order.ShippingEmail,
        Phone: order.Customer?.Phone ?? order.ShippingPhone,
        PersonName: order.Customer?.FullName ?? order.ShippingName ?? order.BillingName,
        CompanyName: !string.IsNullOrWhiteSpace(order.BillingTaxId) ? order.BillingName : order.Customer?.CompanyName,
        Address: order.BillingAddress ?? order.ShippingAddress,
        CreatedBy: createdBy,
        FallbackName: "Online Guest",
        // รอบ 209 ฝ่ายค้าน (เงิน) ข้อ 5: เลขภาษีที่ผู้ซื้อกรอกในช่องใบกำกับของออเดอร์นี้ = ผู้ซื้อของใบกำกับ ⇒ จับคู่ด้วยเลข+สาขาเท่านั้น
        BuyerTaxIdExplicit: !string.IsNullOrWhiteSpace(order.BillingTaxId));

    /// <summary>ข้อเท็จจริงของการจองเว็บ</summary>
    public static CmsContactInput ForBooking(SiteBooking booking, string createdBy) => new(
        LinkedContactId: booking.Customer?.ContactId,
        TaxId: booking.Customer?.TaxId,
        BranchCode: booking.Customer?.BranchCode,
        Email: booking.Customer?.Email ?? booking.GuestEmail,
        Phone: booking.Customer?.Phone ?? booking.GuestPhone,
        PersonName: booking.Customer?.FullName ?? booking.GuestName,
        CompanyName: booking.Customer?.CompanyName,
        Address: null,
        CreatedBy: createdBy,
        FallbackName: "Walk-in Customer");

    /// <summary>ข้อเท็จจริงของ lead (lead ไม่มีช่องสาขา ⇒ "ไม่ระบุ" = แถวสำนักงานใหญ่ก่อนตามกติกาคีย์)</summary>
    public static CmsContactInput ForLead(CmsLead lead) => new(
        LinkedContactId: lead.ContactId,
        TaxId: lead.CustomerTaxId,
        BranchCode: null,
        Email: lead.CustomerEmail,
        Phone: lead.CustomerPhone,
        PersonName: lead.CustomerName,
        CompanyName: lead.CustomerCompany,
        Address: null,
        CreatedBy: "lead-qualify",
        FallbackName: "(ไม่ระบุชื่อ)");

    /// <summary>ข้อเท็จจริงของบัญชีลูกค้าเว็บ (ผูกอัตโนมัติตอนสมัคร — ไม่สร้างแถวใหม่)</summary>
    public static CmsContactInput ForCustomer(SiteCustomer c) => new(
        LinkedContactId: c.ContactId,
        TaxId: c.TaxId,
        BranchCode: c.BranchCode,
        Email: c.Email,
        Phone: c.Phone,
        PersonName: c.FullName,
        CompanyName: c.CompanyName,
        Address: null,
        CreatedBy: "storefront-customer");
}
