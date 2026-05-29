using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// Account-level (per-User) subscription that covers one or more Companies the
/// user owns or manages. Solves the multi-company-one-user case:
///   • One person opens 3 companies (Holding + 2 subs) → 1 plan covers all 3
///   • An accountant manages 10 client books → 1 plan covers all 10
///   • A franchise with 5 branch companies → 1 plan covers all 5
///
/// Resolution order at quota-check time:
///   1) If a Company has its own Subscription row (per-company plan), it WINS
///      — used when the company wants separate billing / e-Tax pricing / SLA.
///   2) Else look up the Company.Owner's active AccountSubscription and use it.
///   3) Else fall back to the company's auto-trial Subscription.
///
/// MaxCompanies on the Account Plan limits how many tenants can share the plan.
/// All other limits (MaxUsers, MaxDocuments, OCR pages, storage, feature flags)
/// apply per-company under the account.
/// </summary>
public class AccountSubscription : BaseEntity
{
    /// <summary>The paying user. One AccountSubscription per User (enforced
    /// via partial unique index — only one Active row per user).</summary>
    public Guid OwnerUserId { get; set; }
    public User Owner { get; set; } = null!;

    public Guid PlanTemplateId { get; set; }
    public PlanTemplate PlanTemplate { get; set; } = null!;

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trial;

    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    /// <summary>Max Companies the account can cover. Pulled from the PlanTemplate
    /// at create time; admin can override (e.g. enterprise deal).</summary>
    public int MaxCompanies { get; set; } = 1;

    /// <summary>Users PER COMPANY — each Company under this plan can have up
    /// to this many CompanyUser rows. Per-company because team members
    /// typically only belong to one of the accountant's clients, not all.</summary>
    public int MaxUsersPerCompany { get; set; } = 1;

    /// <summary>AGGREGATE limits — counted ACROSS ALL Companies under the
    /// account, summed up to one total per month. Examples:
    ///   • Accountant Pro Plan = 1,000 docs/mo means total across 10 client
    ///     books, not 1,000 per book.
    ///   • One business owner with 3 companies = same — totals roll up.
    /// This is the SaaS model customers expect; per-company quotas confuse
    /// people ("why does company B count against company A's quota?").</summary>
    public int MaxDocumentsPerMonth { get; set; } = 30;
    public int MaxJournalEntriesPerMonth { get; set; } = 50;
    public long MaxStorageBytes { get; set; } = 100 * 1024 * 1024;
    public int MaxOcrPagesPerMonth { get; set; } = 0;
    public int? AzureOcrPagesPerMonth { get; set; }
    public int? LocalOcrPagesPerMonth { get; set; }

    public FeatureFlags EnabledFeatures { get; set; } = FeatureFlags.TrialFeatures;

    /// <summary>Billing — same model as Subscription (manual bank slip).</summary>
    public decimal MonthlyPrice { get; set; }
    public decimal AnnualPrice { get; set; }
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;  // 1=Monthly default
    public DateTime? LastPaidAt { get; set; }

    /// <summary>Grace period after EndDate during which companies under this
    /// account stay writable. 7 days default — gives the owner time to renew
    /// without losing day-to-day ops.</summary>
    public int GracePeriodDays { get; set; } = 7;

    /// <summary>Last "expiring soon" reminder we sent. Used by
    /// AccountPlanExpiryReminderJob to skip rows it already nudged this cycle —
    /// without it the daily loop would spam the owner every run.</summary>
    public DateTime? LastExpiryReminderAt { get; set; }
    /// <summary>Bitmask of the reminder buckets we've already fired against
    /// this account in the current expiry window:
    ///   1 = 7-day  reminder sent
    ///   2 = 3-day  reminder sent
    ///   4 = 1-day  reminder sent
    ///   8 = expired notification sent
    /// Reset when EndDate moves forward (renewal flow).</summary>
    public int ExpiryRemindersSentMask { get; set; } = 0;

    public ICollection<Subscription> CompanySubscriptions { get; set; } = new List<Subscription>();
}
