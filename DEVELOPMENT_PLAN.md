# Accounting System - Development Plan
## Full System Analysis & Roadmap

---

## 1. CURRENT SYSTEM STATUS OVERVIEW

| Component | Count | Completeness |
|-----------|-------|-------------|
| Entity Models | 153 | 100% defined |
| DbSets (DbContext) | 153 | 100% registered |
| Service Interfaces | 47 | 100% defined |
| Service Implementations | 47 | ~65% complete logic |
| Controllers | 48 | 100% defined |
| API Endpoints | 437+ | 100% wired |
| DTO Coverage | ~50/153 entities | ~33% |
| Enums | 40+ | Complete |

**Overall System Completeness: ~65%**

---

## 2. CRITICAL ISSUES FOUND (Must Fix)

### 2.1 Bug: Guid.Empty Hardcoded as UserId
**Files:**
- `AiController.cs` — AcceptCategorizationAsync passes `Guid.Empty`
- `MobileController.cs` — 4 endpoints pass `Guid.Empty` for userId

**Impact:** Actions are not tracked to the correct user.

### 2.2 Bug: SmartImportColumnMapping IsDeleted Filter Mismatch
- DbContext applies `HasQueryFilter(m => !m.IsDeleted)` on `SmartImportColumnMapping`
- `SmartImportColumnMapping` inherits from `BaseEntity` which has `IsDeleted` — **OK, not a real bug**
- (Verified: BaseEntity already defines IsDeleted)

### 2.3 Tax Calculation Gaps
- `TaxService`: Input VAT always returns 0 (not captured)
- WHT rate hardcoded at 7% — doesn't handle different income types
- Missing Corporate Income Tax (CIT) calculations
- Missing specific tax form generation (ภ.ง.ด.50, ภ.ง.ด.51)

### 2.4 Consolidation Incomplete
- Equity and Cost method multipliers set to `0m` — non-functional
- No minority interest calculations
- No goodwill/fair value adjustments
- Elimination logic incomplete

### 2.5 Auth Security Gaps
- No password strength validation
- No account lockout after failed attempts
- No 2FA/MFA support
- No password reset flow
- No email verification

### 2.6 Inconsistent User Extraction in Controllers
Three different patterns:
1. `JwtHelper.GetUserIdFromClaims(User)` — most controllers
2. `User.FindFirstValue(ClaimTypes.NameIdentifier)` — ImportExport, ExpenseClaim, WHT
3. `User.Identity?.Name` — various

---

## 3. DTO COVERAGE GAP (67% Missing)

### Entities WITHOUT DTOs (48 entity groups):

**Financial Core:**
- IntercompanyTransaction / IntercompanyTransactionLine
- ConsolidationGroup / ConsolidationMember / ConsolidationReport
- ContactCreditSetting / DunningLetter / PaymentReminder

**Modules:**
- PayrollItem
- AccountingDimension / Branch / JournalLineDimension
- Project / ProjectTask / ProjectCostEntry
- RevenueContract / PerformanceObligation / RevenueSchedule
- Warehouse / WarehouseStock / StockTransfer / StockTransferLine
- Loan / LoanSchedule / LoanPayment
- CommissionPlan / CommissionTier / CommissionAssignment / CommissionCalculation
- FinancialScenario / ScenarioAssumption / ScenarioResult / FinancialKpi
- BankConnection / BankFeedImport
- ComplianceFiling
- TimeEntry / BillingRate
- WebhookRegistration / WebhookDelivery
- UserDevice / SyncQueue

---

## 4. SERVICE QUALITY TIERS

### Tier A — Excellent (≥90% complete):
1. AccountingService (505 lines, 17 methods)
2. PayrollService (643 lines, 20+ methods)
3. CompanyService (180 lines)
4. AdvancedArApService (443 lines)
5. AgingReportService (125 lines)
6. ComplianceService (381 lines)
7. DashboardService
8. SubscriptionService (very large, feature-rich)

### Tier B — Good (70-90%):
9. DocumentService
10. BankService (185 lines)
11. BudgetService (173 lines)
12. ApprovalService (269 lines)
13. CurrencyService (122 lines)
14. WebhookService
15. FreelanceService

### Tier C — Fair (50-70%):
16. TaxService (163 lines — calculation gaps)
17. AiService (825 lines — pseudo-AI only)
18. RecurringTransactionService (199 lines)
19. ExpenseClaimService (248 lines)
20. FixedAssetService (200 lines)
21. IntercompanyService (172 lines)
22. ConsolidationService (282 lines — Equity/Cost broken)
23. ImportExportService (now enhanced with Smart Import)

### Tier D — Incomplete (<50%):
24. OcrService (stub — no real OCR)
25. FileAttachmentService (66 lines — no actual storage)
26. NotificationService (88 lines — InApp only, no email/SMS)
27. AuditTrailService (100 lines — queries only, no triggers)

### Tier E — Not Reviewed (large files, likely functional):
28. FpaService (~26KB)
29. ReportBuilderService (~25KB)
30. TimeBillingService (~19KB)
31. MobileApiService (~19KB)
32. DocumentTemplateService (~24KB)
33. PortalService
34. OpenBankingService

---

## 5. DEVELOPMENT PLAN — PRIORITIZED PHASES

---

### PHASE 1: CRITICAL FIXES (Bug fixes & Security)
**Priority: URGENT**

#### 1.1 Fix Guid.Empty Bug
- [ ] AiController — extract userId from JWT claims
- [ ] MobileController — extract userId from JWT claims (4 endpoints)

#### 1.2 Standardize User Extraction
- [ ] Create helper extension method `HttpContext.GetUserId()`
- [ ] Replace all 3 patterns with unified method across all controllers

#### 1.3 Auth Security Hardening
- [ ] Add password strength validation (min 8 chars, uppercase, number, special)
- [ ] Implement account lockout after 5 failed attempts (15 min lockout)
- [ ] Add password reset flow via email token
- [ ] Add email verification on registration

#### 1.4 Fix Tax Calculations
- [ ] TaxService: Implement input VAT capture from purchase documents
- [ ] Add WHT rate table by income type (not hardcoded 7%)
- [ ] Add CIT half-year (ภ.ง.ด.51) and year-end (ภ.ง.ด.50) calculations

#### 1.5 Fix Consolidation
- [ ] Implement Equity method calculation (share of net income × ownership %)
- [ ] Implement Cost method (investment at cost, dividend income only)
- [ ] Add minority interest calculation for Full consolidation
- [ ] Fix elimination entries for intercompany balances

---

### PHASE 2: CORE COMPLETENESS (Missing DTOs & Service Logic)
**Priority: HIGH**

#### 2.1 Create Missing DTOs — Financial Core
- [ ] IntercompanyDtos.cs (CreateIntercompanyRequest, IntercompanyResponse, etc.)
- [ ] ConsolidationDtos.cs (CreateGroupRequest, GroupResponse, ConsolidatedReportResponse)
- [ ] AdvancedArApDtos.cs (CreditSettingDto, DunningLetterResponse, PaymentReminderResponse)

#### 2.2 Create Missing DTOs — Modules
- [ ] ProjectDtos.cs (CreateProjectRequest, ProjectResponse, TaskResponse, CostEntryResponse)
- [ ] WarehouseDtos.cs (CreateWarehouseRequest, StockResponse, TransferRequest)
- [ ] LoanDtos.cs (CreateLoanRequest, LoanResponse, ScheduleResponse, PaymentResponse)
- [ ] CommissionDtos.cs (CreatePlanRequest, PlanResponse, AssignmentResponse, CalculationResponse)
- [ ] RevenueRecognitionDtos.cs (CreateContractRequest, ObligationResponse, ScheduleResponse)
- [ ] FpaDtos.cs (CreateScenarioRequest, ScenarioResponse, KpiResponse)
- [ ] TimeBillingDtos.cs (CreateTimeEntryRequest, TimeEntryResponse, BillingRateResponse)

#### 2.3 Create Missing DTOs — Integration
- [ ] OpenBankingDtos.cs (BankConnectionRequest, FeedImportResponse)
- [ ] WebhookDtos.cs (RegisterWebhookRequest, WebhookResponse, DeliveryResponse)
- [ ] MobileDtos.cs (RegisterDeviceRequest, SyncRequest, SyncResponse)
- [ ] ComplianceDtos.cs (FilingResponse, FilingStatusResponse)
- [ ] DimensionDtos.cs (CreateDimensionRequest, DimensionResponse, BranchResponse)

#### 2.4 Enhance NotificationService
- [ ] Add email notification channel (SMTP integration)
- [ ] Add notification preference settings per user
- [ ] Add notification digest/batching

#### 2.5 Enhance AuditTrailService
- [ ] Implement automatic audit logging on entity changes (SaveChanges override)
- [ ] Capture old values vs new values for UPDATE operations
- [ ] Track IP address from HttpContext

#### 2.6 Enhance FileAttachmentService
- [ ] Implement actual file storage (local disk or cloud)
- [ ] Add file type validation (whitelist extensions)
- [ ] Add virus scan placeholder
- [ ] Add max file size enforcement per entity type

---

### PHASE 3: MODULE COMPLETION
**Priority: HIGH-MEDIUM**

#### 3.1 Complete OcrService
- [ ] Integrate with OCR provider (Azure Form Recognizer or Tesseract)
- [ ] Auto-extract data from receipts/invoices
- [ ] Map extracted data to document creation DTOs
- [ ] Support Thai + English document recognition

#### 3.2 Complete FixedAssetService
- [ ] Add automatic monthly depreciation batch posting
- [ ] Add asset revaluation support
- [ ] Add asset group/category management
- [ ] Track accumulated depreciation properly

#### 3.3 Complete BankService Reconciliation
- [ ] Implement smart statement matching (fuzzy match on amount + date + reference)
- [ ] Add bank feed import (CSV/OFX format)
- [ ] Add suggested match list with confidence scoring
- [ ] Batch reconciliation support

#### 3.4 Complete BudgetService
- [ ] Add budget variance alerts (when actual > budget)
- [ ] Add departmental budget support
- [ ] Add budget revision/amendment workflow
- [ ] Add rolling forecast from budget

#### 3.5 Complete ExpenseClaimService
- [ ] Add policy compliance checks (max amounts, approved categories)
- [ ] Integration with OCR for receipt scanning
- [ ] Add budget constraint checking before approval

---

### PHASE 4: API QUALITY & CONSISTENCY
**Priority: MEDIUM**

#### 4.1 HTTP Status Codes
- [ ] POST endpoints: return 201 Created with Location header
- [ ] DELETE endpoints: return 204 No Content
- [ ] Validation failures: return 400 BadRequest with details
- [ ] Duplicate operations: return 409 Conflict
- [ ] Not found: return 404 (ensure KeyNotFoundException maps to 404)

#### 4.2 Input Validation
- [ ] Add FluentValidation for request DTOs
- [ ] Add [Required] and validation attributes on DTOs
- [ ] Consistent error message format (error code + Thai message)

#### 4.3 Pagination Consistency
- [ ] Ensure all list endpoints support PagedRequest
- [ ] Add sort and filter parameters consistently
- [ ] Max pageSize enforcement (e.g., 100)

#### 4.4 API Documentation
- [ ] Add XML comments on all controller methods
- [ ] Swagger/OpenAPI tags for grouping
- [ ] Request/Response examples

---

### PHASE 5: ADVANCED FEATURES
**Priority: MEDIUM-LOW**

#### 5.1 Enhanced AI Features
- [ ] Improve auto-categorization with weighted scoring
- [ ] Add feedback loop (accepted/rejected results improve future predictions)
- [ ] Enhanced anomaly detection with trending analysis
- [ ] Better cash flow forecast using seasonal patterns

#### 5.2 Multi-Currency Enhancement
- [ ] Auto-fetch exchange rates from BOT (Bank of Thailand)
- [ ] Unrealized gain/loss calculation
- [ ] Month-end revaluation journal entries
- [ ] Currency conversion in documents

#### 5.3 Report Builder Enhancement
- [ ] Add custom formula support
- [ ] Add chart/visualization support
- [ ] Export to Excel with formatting
- [ ] Scheduled report generation

#### 5.4 Approval Workflow Enhancement
- [ ] Add parallel approval support
- [ ] Add conditional routing rules
- [ ] Auto-escalation after timeout
- [ ] Delegation support (when approver is away)

---

### PHASE 6: INTEGRATION & PRODUCTION READINESS
**Priority: LOW (pre-launch)

#### 6.1 Open Banking Integration
- [ ] Implement BOT Open Banking API connection
- [ ] Automatic bank feed sync
- [ ] Account verification flow

#### 6.2 E-Tax Filing
- [ ] Generate XML for RD e-Filing (กรมสรรพากร)
- [ ] Generate XML for DBD e-Filing (กรมพัฒนาธุรกิจการค้า)
- [ ] SSO monthly filing export

#### 6.3 Email/SMS Infrastructure
- [ ] SMTP email integration for notifications
- [ ] SMS gateway for OTP and alerts
- [ ] Email template management

#### 6.4 Performance & Scaling
- [ ] Add caching layer (Redis) for reports and dashboards
- [ ] Database indexing optimization
- [ ] Query performance audit
- [ ] Add background job processing (Hangfire) for heavy operations

#### 6.5 Monitoring & Logging
- [ ] Structured logging (Serilog)
- [ ] Health check endpoints
- [ ] Error tracking (Sentry or similar)
- [ ] Performance metrics

---

## 6. RECOMMENDED DEVELOPMENT ORDER

```
Sprint 1 (Week 1-2):  Phase 1 — Critical Fixes
Sprint 2 (Week 3-4):  Phase 2.1-2.3 — Missing DTOs
Sprint 3 (Week 5-6):  Phase 2.4-2.6 — Service Enhancements
Sprint 4 (Week 7-8):  Phase 3.1-3.2 — OCR + Fixed Asset
Sprint 5 (Week 9-10): Phase 3.3-3.5 — Bank + Budget + Expense
Sprint 6 (Week 11-12): Phase 4 — API Quality
Sprint 7 (Week 13-14): Phase 5.1-5.2 — AI + Currency
Sprint 8 (Week 15-16): Phase 5.3-5.4 — Reports + Approval
Sprint 9 (Week 17-18): Phase 6.1-6.3 — Integrations
Sprint 10 (Week 19-20): Phase 6.4-6.5 — Production Readiness
```

---

## 7. FILES THAT NEED IMMEDIATE ATTENTION

| File | Issue | Priority |
|------|-------|----------|
| `Controllers/AiController.cs` | Guid.Empty userId | CRITICAL |
| `Controllers/MobileController.cs` | Guid.Empty userId (4 places) | CRITICAL |
| `Services/Implementations/TaxService.cs` | Input VAT = 0, WHT rate hardcoded | CRITICAL |
| `Services/Implementations/ConsolidationService.cs` | Equity/Cost multiplier = 0 | CRITICAL |
| `Services/Implementations/AuthService.cs` | No password validation, no lockout | HIGH |
| `Services/Implementations/NotificationService.cs` | InApp only | HIGH |
| `Services/Implementations/FileAttachmentService.cs` | No actual storage | HIGH |
| `Services/Implementations/AuditTrailService.cs` | No auto-trigger | HIGH |
| `Services/Implementations/OcrService.cs` | Stub only | MEDIUM |
| `Services/Implementations/ImportExportService.cs` | 52KB — refactor candidate | LOW |
