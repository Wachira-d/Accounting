namespace Accounting.Models.Entities;

/// <summary>
/// Unified lead-capture record for non-commerce storefront forms. Eight
/// of the 17 industry templates we ship (Trading, Construction,
/// RealEstate, Technology, Education, Transportation, Service /
/// Freelance, Agriculture) center on a "request" workflow that doesn't
/// fit either the Cart→Order flow (SiteOrder) or the slot-based
/// Booking flow (SiteBooking). They all have the same shape underneath:
///
///   "Customer submits a form with their needs. Owner sees the
///    submission, contacts back, eventually creates a Quotation
///    document, customer signs, work happens."
///
/// So instead of cloning 8 controllers + 8 entities, we put them all
/// in one normalized table and discriminate by <see cref="LeadType"/>.
/// The form fields specific to each lead type live in
/// <see cref="DataJson"/> — a free-form JSON payload that the
/// owner-facing UI renders as a read-only summary.
///
/// Workflow status lives on <see cref="Status"/>:
///   New → Qualified → Quoted → Won/Lost
/// Each transition can attach an InternalNote so the owner has an
/// audit trail.
/// </summary>
public class CmsLead : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string LeadNumber { get; set; } = null!;     // L-YYMM-NNNN, generated on insert
    public LeadType LeadType { get; set; }
    public LeadStatus Status { get; set; } = LeadStatus.New;

    /// <summary>Optional Page slug the form was submitted from
    /// ("rfq", "viewing", "demo", "contact"). Useful for tracking
    /// which page converts best.</summary>
    public string? SourceSlug { get; set; }

    // ── Customer info (denormalized; we may not have a Contact yet) ──
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerPhone { get; set; }
    public string? CustomerCompany { get; set; }
    public string? CustomerTaxId { get; set; }

    /// <summary>Free-form summary from the form (matches the
    /// "message" field on most lead forms).</summary>
    public string? Message { get; set; }

    /// <summary>JSON payload of every form field — captures industry-
    /// specific extras like "property type, budget range, square
    /// meters, preferred move-in" for a RealEstate viewing, or
    /// "course name, batch, payment method" for an Education
    /// enrollment. Schema is open and intentional — we store what
    /// the form sent rather than forcing a rigid schema per type.</summary>
    public string? DataJson { get; set; }

    // ── Owner-side workflow ──
    public Guid? AssignedToUserId { get; set; }          // sales rep
    public string? AssignedToName { get; set; }
    public DateTime? QualifiedAt { get; set; }
    public DateTime? QuotedAt { get; set; }
    public DateTime? WonAt { get; set; }
    public DateTime? LostAt { get; set; }
    public string? LostReason { get; set; }
    public string? InternalNotes { get; set; }

    // ── Linked ERP records (set when the lead converts) ──
    /// <summary>If converted to a Quotation/Invoice, the Document Id.
    /// Lets the owner deep-link to the ERP doc from the lead detail
    /// modal.</summary>
    public Guid? ErpDocumentId { get; set; }
    /// <summary>The Contact row created when the lead is qualified.
    /// On first qualification, we either match an existing contact
    /// by tax-id/email or create a new one; further notes/quotes
    /// attach to that Contact.</summary>
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }
}

public enum LeadType
{
    /// <summary>Generic contact-form submission — what a "ติดต่อเรา"
    /// page produces. No structured workflow expectation.</summary>
    Contact = 0,
    /// <summary>Request For Quote — Trading / Manufacturing /
    /// Construction. Customer describes what they need, owner sends
    /// a Quotation document.</summary>
    Rfq = 1,
    /// <summary>Property viewing request — RealEstate. Customer
    /// picks a listing + asks to schedule a viewing.</summary>
    Viewing = 2,
    /// <summary>Demo / sales meeting — Technology / SaaS. Customer
    /// requests a product demo with the sales team.</summary>
    Demo = 3,
    /// <summary>Course enrollment — Education. Customer signs up
    /// for a specific course/batch.</summary>
    Enrollment = 4,
    /// <summary>Quote request — Service / Freelance / Transportation.
    /// Customer describes their project, owner quotes per-job.</summary>
    Quote = 5,
    /// <summary>Subscription/recurring request — Agriculture (CSA
    /// box subscription), other recurring services.</summary>
    Subscription = 6,
    /// <summary>Shipment / logistics quote — Transportation specific.
    /// Has origin/destination/weight fields.</summary>
    ShipmentQuote = 7,
    /// <summary>Catch-all for custom lead types not covered above.</summary>
    Other = 99
}

public enum LeadStatus
{
    /// <summary>Just-submitted — owner hasn't reviewed yet.</summary>
    New = 0,
    /// <summary>Owner has reviewed + decided to pursue. Contact
    /// row is created at this transition.</summary>
    Qualified = 1,
    /// <summary>Owner has sent a Quotation to the customer.
    /// ErpDocumentId is populated.</summary>
    Quoted = 2,
    /// <summary>Customer accepted the quote. Won record goes to
    /// pipeline reporting.</summary>
    Won = 3,
    /// <summary>Customer declined / went silent / chose competitor.
    /// LostReason captures the why.</summary>
    Lost = 4,
    /// <summary>Spam or auto-filtered — keep the row for analytics
    /// but exclude from the active inbox.</summary>
    Spam = 5
}
