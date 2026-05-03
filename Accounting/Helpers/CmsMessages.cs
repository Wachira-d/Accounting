namespace Accounting.Helpers;

public static class CmsMessages
{
    private static readonly Dictionary<string, Dictionary<string, string>> Messages = new()
    {
        // ── Products ──
        ["product.created"] = new() { ["th"] = "เพิ่มสินค้าสำเร็จ", ["en"] = "Product added successfully" },
        ["product.updated"] = new() { ["th"] = "อัปเดตสินค้าสำเร็จ", ["en"] = "Product updated successfully" },
        ["product.deleted"] = new() { ["th"] = "ลบสินค้าสำเร็จ", ["en"] = "Product removed successfully" },
        ["product.notFound"] = new() { ["th"] = "ไม่พบสินค้า", ["en"] = "Product not found" },

        // ── Categories ──
        ["category.created"] = new() { ["th"] = "สร้างหมวดหมู่สำเร็จ", ["en"] = "Category created successfully" },
        ["category.updated"] = new() { ["th"] = "อัปเดตหมวดหมู่สำเร็จ", ["en"] = "Category updated successfully" },
        ["category.deleted"] = new() { ["th"] = "ลบหมวดหมู่สำเร็จ", ["en"] = "Category deleted successfully" },
        ["category.notFound"] = new() { ["th"] = "ไม่พบหมวดหมู่", ["en"] = "Category not found" },

        // ── Cart ──
        ["cart.itemAdded"] = new() { ["th"] = "เพิ่มสินค้าในตะกร้าสำเร็จ", ["en"] = "Item added to cart" },
        ["cart.updated"] = new() { ["th"] = "อัปเดตตะกร้าสำเร็จ", ["en"] = "Cart updated successfully" },
        ["cart.itemRemoved"] = new() { ["th"] = "ลบสินค้าจากตะกร้าสำเร็จ", ["en"] = "Item removed from cart" },
        ["cart.cleared"] = new() { ["th"] = "ล้างตะกร้าสำเร็จ", ["en"] = "Cart cleared successfully" },
        ["cart.merged"] = new() { ["th"] = "รวมตะกร้าสำเร็จ", ["en"] = "Cart merged successfully" },
        ["cart.notFound"] = new() { ["th"] = "ไม่พบตะกร้าสินค้า", ["en"] = "Cart not found" },

        // ── Orders ──
        ["order.created"] = new() { ["th"] = "สร้างคำสั่งซื้อสำเร็จ", ["en"] = "Order created successfully" },
        ["order.statusUpdated"] = new() { ["th"] = "อัปเดตสถานะคำสั่งซื้อสำเร็จ", ["en"] = "Order status updated successfully" },
        ["order.notFound"] = new() { ["th"] = "ไม่พบคำสั่งซื้อ", ["en"] = "Order not found" },
        ["order.quotationCreated"] = new() { ["th"] = "สร้างใบเสนอราคาสำเร็จ", ["en"] = "Quotation created successfully" },
        ["order.quotationOnly"] = new() { ["th"] = "เว็บไซต์นี้ให้บริการเฉพาะใบเสนอราคาเท่านั้น", ["en"] = "This site only generates quotations" },

        // ── ERP Sync ──
        ["erp.synced"] = new() { ["th"] = "ซิงค์เอกสาร ERP สำเร็จ", ["en"] = "ERP document synced successfully" },
        ["erp.syncFailed"] = new() { ["th"] = "ไม่สามารถซิงค์ได้", ["en"] = "Unable to sync" },

        // ── Payment Gateways ──
        ["gateway.created"] = new() { ["th"] = "สร้างช่องทางชำระเงินสำเร็จ", ["en"] = "Payment gateway created successfully" },
        ["gateway.updated"] = new() { ["th"] = "อัปเดตช่องทางชำระเงินสำเร็จ", ["en"] = "Payment gateway updated successfully" },
        ["gateway.deleted"] = new() { ["th"] = "ลบช่องทางชำระเงินสำเร็จ", ["en"] = "Payment gateway removed successfully" },
        ["gateway.notFound"] = new() { ["th"] = "ไม่พบช่องทางชำระเงิน", ["en"] = "Payment gateway not found" },

        // ── Coupons ──
        ["coupon.created"] = new() { ["th"] = "สร้างคูปองสำเร็จ", ["en"] = "Coupon created successfully" },
        ["coupon.updated"] = new() { ["th"] = "อัปเดตคูปองสำเร็จ", ["en"] = "Coupon updated successfully" },
        ["coupon.deleted"] = new() { ["th"] = "ลบคูปองสำเร็จ", ["en"] = "Coupon deleted successfully" },
        ["coupon.notFound"] = new() { ["th"] = "ไม่พบคูปอง", ["en"] = "Coupon not found" },
        ["coupon.applied"] = new() { ["th"] = "ใช้คูปองสำเร็จ", ["en"] = "Coupon applied successfully" },
        ["coupon.removed"] = new() { ["th"] = "ลบคูปองออกจากตะกร้าสำเร็จ", ["en"] = "Coupon removed from cart" },
        ["coupon.invalid"] = new() { ["th"] = "รหัสคูปองไม่ถูกต้อง", ["en"] = "Invalid coupon code" },
        ["coupon.expired"] = new() { ["th"] = "คูปองหมดอายุแล้ว", ["en"] = "Coupon has expired" },
        ["coupon.limitReached"] = new() { ["th"] = "คูปองถูกใช้งานครบจำนวนแล้ว", ["en"] = "Coupon usage limit reached" },
        ["coupon.personalLimit"] = new() { ["th"] = "คุณใช้คูปองนี้ครบจำนวนแล้ว", ["en"] = "You have reached the coupon usage limit" },
        ["coupon.firstOrderOnly"] = new() { ["th"] = "คูปองนี้ใช้ได้สำหรับคำสั่งซื้อแรกเท่านั้น", ["en"] = "This coupon is for first orders only" },
        ["coupon.notStarted"] = new() { ["th"] = "คูปองยังไม่เริ่มใช้งาน", ["en"] = "Coupon is not yet active" },
        ["coupon.noEligible"] = new() { ["th"] = "ไม่มีสินค้าที่ใช้คูปองได้ในตะกร้า", ["en"] = "No eligible products in cart" },

        // ── Shipping ──
        ["shipping.zoneCreated"] = new() { ["th"] = "สร้างโซนจัดส่งสำเร็จ", ["en"] = "Shipping zone created successfully" },
        ["shipping.zoneUpdated"] = new() { ["th"] = "อัปเดตโซนจัดส่งสำเร็จ", ["en"] = "Shipping zone updated successfully" },
        ["shipping.zoneDeleted"] = new() { ["th"] = "ลบโซนจัดส่งสำเร็จ", ["en"] = "Shipping zone deleted successfully" },
        ["shipping.zoneNotFound"] = new() { ["th"] = "ไม่พบโซนจัดส่ง", ["en"] = "Shipping zone not found" },
        ["shipping.rateAdded"] = new() { ["th"] = "เพิ่มอัตราค่าจัดส่งสำเร็จ", ["en"] = "Shipping rate added successfully" },
        ["shipping.rateUpdated"] = new() { ["th"] = "อัปเดตอัตราค่าจัดส่งสำเร็จ", ["en"] = "Shipping rate updated successfully" },
        ["shipping.rateDeleted"] = new() { ["th"] = "ลบอัตราค่าจัดส่งสำเร็จ", ["en"] = "Shipping rate deleted successfully" },
        ["shipping.rateNotFound"] = new() { ["th"] = "ไม่พบอัตราค่าจัดส่ง", ["en"] = "Shipping rate not found" },

        // ── Variants ──
        ["variant.created"] = new() { ["th"] = "เพิ่มตัวเลือกสินค้าสำเร็จ", ["en"] = "Product variant added successfully" },
        ["variant.updated"] = new() { ["th"] = "อัปเดตตัวเลือกสินค้าสำเร็จ", ["en"] = "Product variant updated successfully" },
        ["variant.deleted"] = new() { ["th"] = "ลบตัวเลือกสินค้าสำเร็จ", ["en"] = "Product variant deleted successfully" },
        ["variant.notFound"] = new() { ["th"] = "ไม่พบตัวเลือกสินค้า", ["en"] = "Product variant not found" },
        ["option.created"] = new() { ["th"] = "เพิ่มตัวเลือกสำเร็จ", ["en"] = "Option added successfully" },
        ["option.deleted"] = new() { ["th"] = "ลบตัวเลือกสำเร็จ", ["en"] = "Option deleted successfully" },
        ["option.notFound"] = new() { ["th"] = "ไม่พบตัวเลือก", ["en"] = "Option not found" },

        // ── Reviews ──
        ["review.created"] = new() { ["th"] = "สร้างรีวิวสำเร็จ", ["en"] = "Review submitted successfully" },
        ["review.moderated"] = new() { ["th"] = "อัปเดตสถานะรีวิวสำเร็จ", ["en"] = "Review moderation updated" },
        ["review.replied"] = new() { ["th"] = "ตอบกลับรีวิวสำเร็จ", ["en"] = "Reply added to review" },
        ["review.deleted"] = new() { ["th"] = "ลบรีวิวสำเร็จ", ["en"] = "Review deleted successfully" },
        ["review.notFound"] = new() { ["th"] = "ไม่พบรีวิว", ["en"] = "Review not found" },
        ["review.helpful"] = new() { ["th"] = "ขอบคุณสำหรับ feedback", ["en"] = "Thanks for your feedback" },

        // ── Wishlist ──
        ["wishlist.added"] = new() { ["th"] = "เพิ่มในรายการโปรดสำเร็จ", ["en"] = "Added to wishlist" },
        ["wishlist.removed"] = new() { ["th"] = "ลบออกจากรายการโปรดสำเร็จ", ["en"] = "Removed from wishlist" },
        ["wishlist.notFound"] = new() { ["th"] = "ไม่พบรายการ", ["en"] = "Wishlist item not found" },

        // ── Stock ──
        ["stock.deducted"] = new() { ["th"] = "หักสต็อกสำเร็จ", ["en"] = "Stock deducted successfully" },
        ["stock.restored"] = new() { ["th"] = "คืนสต็อกสำเร็จ", ["en"] = "Stock restored successfully" },

        // ── Booking ──
        ["booking.serviceCreated"] = new() { ["th"] = "สร้างบริการสำเร็จ", ["en"] = "Service created successfully" },
        ["booking.serviceUpdated"] = new() { ["th"] = "อัปเดตบริการสำเร็จ", ["en"] = "Service updated successfully" },
        ["booking.serviceDeleted"] = new() { ["th"] = "ลบบริการสำเร็จ", ["en"] = "Service deleted successfully" },
        ["booking.serviceNotFound"] = new() { ["th"] = "ไม่พบบริการ", ["en"] = "Service not found" },
        ["booking.slotCreated"] = new() { ["th"] = "สร้างช่วงเวลาสำเร็จ", ["en"] = "Time slot created successfully" },
        ["booking.slotDeleted"] = new() { ["th"] = "ลบช่วงเวลาสำเร็จ", ["en"] = "Time slot deleted successfully" },
        ["booking.slotNotFound"] = new() { ["th"] = "ไม่พบช่วงเวลา", ["en"] = "Time slot not found" },
        ["booking.created"] = new() { ["th"] = "สร้างการจองสำเร็จ", ["en"] = "Booking created successfully" },
        ["booking.statusUpdated"] = new() { ["th"] = "อัปเดตสถานะการจองสำเร็จ", ["en"] = "Booking status updated successfully" },
        ["booking.notFound"] = new() { ["th"] = "ไม่พบการจอง", ["en"] = "Booking not found" },

        // ── Site ──
        ["site.created"] = new() { ["th"] = "สร้างเว็บไซต์สำเร็จ", ["en"] = "Website created successfully" },
        ["site.updated"] = new() { ["th"] = "อัปเดตเว็บไซต์สำเร็จ", ["en"] = "Website updated successfully" },
        ["site.deleted"] = new() { ["th"] = "ลบเว็บไซต์สำเร็จ", ["en"] = "Website deleted successfully" },
        ["site.notFound"] = new() { ["th"] = "ไม่พบเว็บไซต์", ["en"] = "Website not found" },

        // ── Content ──
        ["page.created"] = new() { ["th"] = "สร้างหน้าสำเร็จ", ["en"] = "Page created successfully" },
        ["page.updated"] = new() { ["th"] = "อัปเดตหน้าสำเร็จ", ["en"] = "Page updated successfully" },
        ["page.deleted"] = new() { ["th"] = "ลบหน้าสำเร็จ", ["en"] = "Page deleted successfully" },
        ["page.published"] = new() { ["th"] = "เผยแพร่หน้าสำเร็จ", ["en"] = "Page published successfully" },
        ["page.notFound"] = new() { ["th"] = "ไม่พบหน้า", ["en"] = "Page not found" },

        // ── Customer Portal ──
        ["customer.registered"] = new() { ["th"] = "ลงทะเบียนสำเร็จ", ["en"] = "Registered successfully" },
        ["customer.loginSuccess"] = new() { ["th"] = "เข้าสู่ระบบสำเร็จ", ["en"] = "Login successful" },
        ["customer.loginFailed"] = new() { ["th"] = "อีเมลหรือรหัสผ่านไม่ถูกต้อง", ["en"] = "Invalid email or password" },
        ["customer.notFound"] = new() { ["th"] = "ไม่พบลูกค้า", ["en"] = "Customer not found" },
        ["customer.updated"] = new() { ["th"] = "อัปเดตข้อมูลลูกค้าสำเร็จ", ["en"] = "Customer updated successfully" },
        ["customer.deleted"] = new() { ["th"] = "ลบลูกค้าสำเร็จ", ["en"] = "Customer deleted successfully" },

        // ── Commerce Config ──
        ["config.updated"] = new() { ["th"] = "อัปเดตการตั้งค่าสำเร็จ", ["en"] = "Settings updated successfully" },
        ["config.notFound"] = new() { ["th"] = "ไม่พบการตั้งค่า", ["en"] = "Settings not found" },

        // ── General ──
        ["general.success"] = new() { ["th"] = "สำเร็จ", ["en"] = "Success" },
        ["general.unauthorized"] = new() { ["th"] = "ไม่มีสิทธิ์เข้าถึง", ["en"] = "Unauthorized" },
    };

    public static string Get(string key, string lang = "th")
    {
        if (Messages.TryGetValue(key, out var translations))
        {
            if (translations.TryGetValue(lang, out var msg)) return msg;
            if (translations.TryGetValue("th", out var fallback)) return fallback;
        }
        return key;
    }

    public static string Get(string key, HttpContext? context)
    {
        var lang = ResolveLanguage(context);
        return Get(key, lang);
    }

    public static string GetMinOrder(decimal amount, string lang = "th")
    {
        return lang == "en"
            ? $"Minimum order amount: {amount:N2}"
            : $"ยอดขั้นต่ำ {amount:N2} บาท";
    }

    public static string ResolveLanguage(HttpContext? context)
    {
        if (context == null) return "th";

        if (context.Request.Headers.TryGetValue("Accept-Language", out var header))
        {
            var lang = header.ToString().Split(',').FirstOrDefault()?.Trim().Split('-').FirstOrDefault()?.ToLowerInvariant();
            if (lang is "en" or "th") return lang;
        }

        if (context.Request.Query.TryGetValue("lang", out var queryLang))
        {
            var l = queryLang.ToString().ToLowerInvariant();
            if (l is "en" or "th") return l;
        }

        return "th";
    }
}
