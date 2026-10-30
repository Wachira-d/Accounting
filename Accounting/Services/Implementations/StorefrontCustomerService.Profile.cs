using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 216 (คำตัดสินข้อ 174) — "ข้อมูลส่วนตัว" ของสมาชิกหน้าร้าน: อ่าน (ใช้เติมฟอร์ม) · แก้เอง · บันทึกจากฟอร์มจอง/สั่งซื้อ ·
/// ทุก query กรอง <c>CompanyId</c> + <c>SiteId</c> + id ลูกค้าที่ controller ได้จากโทเคน (ไม่ใช่ body) ·
/// ตัวตรวจ/ตัวรวมค่า = <see cref="StorefrontMemberProfile"/> ตัวเดียว · <b>ไม่แตะผู้ติดต่อ ERP</b> ที่ผูกไว้ (ข้อมูลบัญชีของร้าน — ข้อ 174 ข) ·
/// ที่อยู่เก็บใน <c>SiteCustomerAddress</c> (แถวหลักต่อชนิด Shipping/Billing) — ตาราง/ช่องเดิมทั้งหมด ไม่มี migration
/// </summary>
public partial class StorefrontCustomerService
{
    private sealed record ProfileRows(SiteCustomer Customer, SiteCustomerAddress? Shipping, SiteCustomerAddress? Billing, List<SiteCustomerAddress> All);

    private static MemberAddress? ToMember(SiteCustomerAddress? a) => a == null ? null
        : new MemberAddress(a.RecipientName, a.Phone, a.AddressLine, a.SubDistrict, a.District, a.Province, a.PostalCode);

    private static StorefrontAddressDto? ToDto(MemberAddress? a) => a == null ? null : new StorefrontAddressDto
    {
        RecipientName = a.RecipientName, Phone = StorefrontMemberProfile.ComparablePhone(a.Phone), AddressLine = a.AddressLine,
        SubDistrict = a.SubDistrict, District = a.District, Province = a.Province, PostalCode = a.PostalCode,
    };

    private static MemberAddress? FromDto(StorefrontAddressDto? a) => a == null ? null
        : new MemberAddress(a.RecipientName, a.Phone, a.AddressLine, a.SubDistrict, a.District, a.Province, a.PostalCode);

    private static MemberProfile ProfileOf(ProfileRows r) => new(r.Customer.FullName, r.Customer.Phone, r.Customer.CompanyName,
        r.Customer.TaxId, r.Customer.BranchCode, ToMember(r.Shipping), ToMember(r.Billing));

    /// <summary>ลูกค้าที่ยังใช้งานของบริษัท+เว็บนี้ + ที่อยู่หลัก (ชนิดละแถว · หลักก่อน แล้วล่าสุด) · ไม่พบ ⇒ null ·
    /// ฝ่ายค้านรอบ 216: แถวชนิด Both ใช้ได้ทั้งเป็นที่อยู่จัดส่งและที่อยู่ใบกำกับ (ใบกำกับ: Billing ก่อน แล้ว Both)</summary>
    private async Task<ProfileRows?> LoadProfileRowsAsync(Guid companyId, Guid siteId, Guid customerId, bool track)
    {
        var cq = _db.SiteCustomers.Where(c => c.Id == customerId && c.CompanyId == companyId && c.SiteId == siteId && c.IsActive);
        var customer = await (track ? cq : cq.AsNoTracking()).FirstOrDefaultAsync();
        if (customer == null) return null;
        var aq = _db.SiteCustomerAddresses.Where(a => a.CompanyId == companyId && a.CustomerId == customerId && !a.IsDeleted);
        var rows = await (track ? aq : aq.AsNoTracking()).OrderByDescending(a => a.IsDefault).ThenByDescending(a => a.CreatedAt).ToListAsync();
        var ship = rows.FirstOrDefault(a => a.AddressType is SiteAddressType.Shipping or SiteAddressType.Both);
        var bill = rows.FirstOrDefault(a => a.AddressType == SiteAddressType.Billing)
            ?? rows.FirstOrDefault(a => a.AddressType == SiteAddressType.Both);
        return new ProfileRows(customer, ship, bill, rows);
    }

    public async Task<StorefrontMeResponse?> GetMeAsync(Guid companyId, Guid siteId, Guid customerId)
    {
        var r = await LoadProfileRowsAsync(companyId, siteId, customerId, track: false);
        return r == null ? null : MeOf(r);
    }

    private static StorefrontMeResponse MeOf(ProfileRows r)
    {
        var p = ProfileOf(r);
        return new StorefrontMeResponse
        {
            CustomerId = r.Customer.Id, Email = r.Customer.Email, FullName = r.Customer.FullName, HasErpContact = r.Customer.ContactId != null,
            Phone = StorefrontMemberProfile.ComparablePhone(p.Phone), CompanyName = p.CompanyName, TaxId = p.TaxId, BranchCode = p.BranchCode,
            ShippingAddress = ToDto(p.Shipping), BillingAddress = ToDto(p.Billing),
            ShippingAddressText = StorefrontMemberProfile.ComposeAddress(p.Shipping, withProvince: false),
            BillingAddressText = StorefrontMemberProfile.ComposeAddress(p.Billing, withProvince: true),
        };
    }

    public async Task<StorefrontMeResponse?> UpdateProfileAsync(Guid companyId, Guid siteId, Guid customerId, UpdateStorefrontProfileRequest request)
    {
        var (errs, clean) = StorefrontMemberProfile.Validate(new MemberProfile(request.FullName, request.Phone, request.CompanyName,
            request.TaxId, request.BranchCode, FromDto(request.ShippingAddress), FromDto(request.BillingAddress)));
        if (errs.Count > 0) throw new BusinessRuleException(string.Join(" · ", errs), StorefrontMemberProfile.RuleCode);
        var rows = await LoadProfileRowsAsync(companyId, siteId, customerId, track: true);
        if (rows == null) return null;
        await ApplyProfileAsync(companyId, rows, clean, "profile");
        return await GetMeAsync(companyId, siteId, customerId);
    }

    public async Task<string?> SaveFromCheckoutAsync(Guid companyId, Guid siteId, Guid customerId, CheckoutProfileInput input)
    {
        var rows = await LoadProfileRowsAsync(companyId, siteId, customerId, track: true);
        if (rows == null) return null;
        var merge = StorefrontMemberProfile.MergeCheckout(ProfileOf(rows), input);
        if (merge.Saved.Count > 0) await ApplyProfileAsync(companyId, rows, merge.Next, "checkout");
        // ฝ่ายค้านรอบ 216: ไม่มีอะไรเปลี่ยนจริง ⇒ ไม่รายงาน "บันทึกแล้ว" (SaveNote คืน null)
        return StorefrontMemberProfile.SaveNote(merge.Saved, merge.Skipped);
    }

    /// <summary>เขียนโปรไฟล์ที่ผ่านตัวตรวจแล้ว: ช่องบนลูกค้า + ที่อยู่หลักชนิดละแถว (ไม่มี ⇒ ลบแถวหลักเดิมของชนิดนั้น) ·
    /// audit chain บันทึก "ช่องที่เปลี่ยน" (ไม่ใส่ค่า — ลดข้อมูลส่วนบุคคลในบันทึก)</summary>
    private async Task ApplyProfileAsync(Guid companyId, ProfileRows rows, MemberProfile p, string via)
    {
        var c = rows.Customer;
        var before = ProfileOf(rows);
        var changed = new List<string>();
        if (before.FullName != p.FullName) changed.Add("fullName");
        if (before.Phone != p.Phone) changed.Add("phone");
        if (before.CompanyName != p.CompanyName || before.TaxId != p.TaxId || before.BranchCode != p.BranchCode) changed.Add("taxInvoice");
        if (before.Shipping != p.Shipping) changed.Add("shippingAddress");
        if (before.Billing != p.Billing) changed.Add("billingAddress");
        if (changed.Count == 0) return;

        var now = DateTime.UtcNow;
        var actor = CreditBookingLink.StorefrontActor(c.Id);
        c.FullName = p.FullName; c.Phone = p.Phone;
        c.CompanyName = p.CompanyName; c.TaxId = p.TaxId; c.BranchCode = p.BranchCode;
        c.UpdatedAt = now; c.UpdatedBy = actor;
        await using var tx = await _db.Database.BeginTransactionAsync();
        if (before.Shipping != p.Shipping || before.Billing != p.Billing)
        {
            // คำขอพร้อมกันของลูกค้าคนเดียว (ฟอร์มสองแท็บ · พนักงานเพิ่มที่อยู่) เรียงคิวที่แถวลูกค้า — ทางเดียวกับ CmsCustomerService.AddAddressAsync
            await CustomerAddressDefaults.LockCustomerAsync(_db, companyId, c.Id);
            var chosen = ApplyAddresses(companyId, c.Id, rows, p.Shipping, p.Billing, actor, now);
            // ด่านฐาน UX_SiteCustomerAddresses_Customer_Type_Default: ปลดแถวหลักเดิมให้ลงฐานก่อน แล้วค่อยตั้งแถวที่เลือก (ลำดับคำสั่งใน batch เดียวไม่แน่นอน)
            foreach (var r in chosen) r.IsDefault = false;
            await _db.SaveChangesAsync();
            foreach (var r in chosen) r.IsDefault = true;
        }
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(SiteCustomer), EntityId = c.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new { changed, via, actor = "storefront-member", rule = StorefrontMemberProfile.RuleCode }),
        });
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>
    /// เขียนที่อยู่หลักสองชนิด (ฝ่ายค้านรอบ 216 · หมายเหตุ Both/แถวหลักซ้ำ/คอลัมน์โครง):
    /// <list type="bullet">
    /// <item>แถว Both แถวเดียวเป็นทั้งจัดส่งและใบกำกับ: สองค่าเท่ากัน ⇒ แก้แถวเดียว · ต่างกัน ⇒ แถวเดิมเป็น Shipping + แถว Billing ใหม่ ·
    /// ล้างฝั่งหนึ่ง ⇒ แปลงแถวเป็นอีกชนิด (ไม่ลบที่อยู่ที่ยังใช้อยู่)</item>
    /// <item>แถว Both ที่เป็นที่อยู่จัดส่งแล้วล้างจัดส่ง ⇒ แปลงเป็น Billing (ไม่ลบ) · Billing ที่ล้าง ⇒ ลบ</item>
    /// <item>แถวหลักอื่นของชนิดเดียวกันถูกปลดเป็น "ไม่ใช่หลัก" ใน SaveChanges เดียวกัน (ด่านฐาน: <c>UX_SiteCustomerAddresses_Customer_Type_Default</c>)</item>
    /// <item>บรรทัดที่อยู่เปลี่ยน ⇒ ล้างคอลัมน์โครงเดิม (บ้านเลขที่/อาคาร/ถนน) ที่ไม่ตรงกับข้อความใหม่แล้ว</item>
    /// </list>
    /// </summary>
    private List<SiteCustomerAddress> ApplyAddresses(Guid companyId, Guid customerId, ProfileRows rows, MemberAddress? ship, MemberAddress? bill, string actor, DateTime now)
    {
        var shipRow = rows.Shipping; var billRow = rows.Billing;
        var chosen = new List<SiteCustomerAddress>();
        if (shipRow != null && ReferenceEquals(shipRow, billRow))
        {
            if (ship == bill)
            {
                if (ship == null) _db.SiteCustomerAddresses.Remove(shipRow);
                else { Fill(shipRow, ship, actor, now); chosen.Add(shipRow); }
            }
            else if (ship != null)
            {
                shipRow.AddressType = SiteAddressType.Shipping; Fill(shipRow, ship, actor, now); chosen.Add(shipRow);
                if (bill != null) chosen.Add(NewRow(companyId, customerId, SiteAddressType.Billing, bill, actor, now));
            }
            else
            {
                shipRow.AddressType = SiteAddressType.Billing; Fill(shipRow, bill!, actor, now); chosen.Add(shipRow);
            }
        }
        else
        {
            if (ship != null)
            {
                if (shipRow == null) chosen.Add(NewRow(companyId, customerId, SiteAddressType.Shipping, ship, actor, now));
                else { Fill(shipRow, ship, actor, now); chosen.Add(shipRow); }
            }
            else if (shipRow != null)
            {
                if (shipRow.AddressType == SiteAddressType.Both) { shipRow.AddressType = SiteAddressType.Billing; shipRow.IsDefault = false; shipRow.UpdatedAt = now; shipRow.UpdatedBy = actor; }
                else _db.SiteCustomerAddresses.Remove(shipRow);
            }
            if (bill != null)
            {
                if (billRow == null) chosen.Add(NewRow(companyId, customerId, SiteAddressType.Billing, bill, actor, now));
                else { Fill(billRow, bill, actor, now); chosen.Add(billRow); }
            }
            else if (billRow != null)
            {
                if (billRow.AddressType == SiteAddressType.Both) { billRow.AddressType = SiteAddressType.Shipping; billRow.IsDefault = false; billRow.UpdatedAt = now; billRow.UpdatedBy = actor; }
                else _db.SiteCustomerAddresses.Remove(billRow);
            }
        }
        // แถวหลักต่อชนิดมีแถวเดียว — ปลดแถวหลักอื่นของชนิดที่เลือก (รวมแถวที่เพิ่งถูกแปลงชนิด)
        foreach (var other in rows.All.Where(a => a.IsDefault && !chosen.Contains(a)
                     && chosen.Any(x => x.AddressType == a.AddressType)))
        {
            other.IsDefault = false; other.UpdatedAt = now; other.UpdatedBy = actor;
        }
        return chosen;
    }

    private SiteCustomerAddress NewRow(Guid companyId, Guid customerId, SiteAddressType type, MemberAddress a, string actor, DateTime now)
    {
        var row = new SiteCustomerAddress { CompanyId = companyId, CustomerId = customerId, AddressType = type, CountryCode = "TH", CreatedBy = actor };
        _db.SiteCustomerAddresses.Add(row);
        Fill(row, a, actor, now);
        return row;
    }

    private static void Fill(SiteCustomerAddress row, MemberAddress a, string actor, DateTime now)
    {
        if (row.AddressLine != a.AddressLine) { row.BuildingNumber = null; row.BuildingName = null; row.StreetName = null; }
        row.RecipientName = a.RecipientName; row.Phone = a.Phone; row.AddressLine = a.AddressLine;
        row.SubDistrict = a.SubDistrict; row.District = a.District; row.Province = a.Province; row.PostalCode = a.PostalCode;
        row.IsDefault = true; row.UpdatedAt = now; row.UpdatedBy = actor;
    }
}
