# NextAcc ERP — World-Class Development Roadmap

> **Goal**: Become the #1 ERP system — combining SAP's depth, Odoo's usability, and Thailand-first tax/compliance expertise.
>
> **Current**: 72,937 LOC | 841 API endpoints | 194 entities | 80 enums

---

## Phase 0: Foundation & Quality (Must-do before scaling)

> Without this foundation, scaling will create technical debt that's impossible to recover from.

### 0.1 Testing Infrastructure
- [ ] xUnit test project setup with test database (SQLite in-memory or TestContainers)
- [ ] Unit tests for all 72 service classes (target: 80% coverage)
- [ ] Integration tests for critical flows: Document→Journal, Order→Stock→Invoice, Payroll→Tax
- [ ] API endpoint tests for auth, RBAC, multi-tenancy isolation
- [ ] Test data seeder/factory classes

### 0.2 CI/CD Pipeline
- [ ] Dockerfile (multi-stage build)
- [ ] docker-compose.yml (app + PostgreSQL + Redis)
- [ ] GitHub Actions: build → test → lint → deploy
- [ ] Environment configs (development, staging, production)
- [ ] Database migration strategy (EF Core migrations instead of raw SQL)

### 0.3 Caching & Performance
- [ ] Redis integration (IDistributedCache)
- [ ] Response caching for read-heavy endpoints (products, categories, reports)
- [ ] Query optimization audit (N+1 queries, missing indexes)
- [ ] Connection pooling configuration

### 0.4 Observability
- [ ] Serilog structured logging (console + file + seq)
- [ ] Health check endpoints (/health, /health/db, /health/redis)
- [ ] OpenTelemetry tracing for request flow
- [ ] Performance metrics (response times, error rates)

**Estimated effort**: 3-4 weeks
**Files**: ~40 new files, ~20 modified

---

## Phase 1: Manufacturing & Supply Chain (Market Differentiator #1)

> This is the single biggest gap. Without MRP/Manufacturing, NextAcc cannot serve 60%+ of businesses.

### 1.1 Bill of Materials (BOM)
```
Entities: BillOfMaterial, BomLine, BomRevision, BomAlternative
Features:
- Multi-level BOM (nested assemblies)
- BOM versioning with revision history
- Phantom/sub-assembly BOM types
- BOM cost roll-up (material + labor + overhead)
- Where-used analysis (reverse BOM lookup)
- BOM comparison between revisions
- Engineering BOM vs Manufacturing BOM
```

### 1.2 Work Centers & Routing
```
Entities: WorkCenter, WorkCenterGroup, RoutingHeader, RoutingOperation
Features:
- Work center capacity planning (hours/day, efficiency %)
- Routing with sequential/parallel operations
- Setup time + run time per operation
- Standard costs per work center
- Work center availability calendar
- Subcontracting work centers
```

### 1.3 Production Orders
```
Entities: ProductionOrder, ProductionOrderLine, ProductionOrderOperation,
          ProductionConsumption, ProductionOutput
Features:
- Production order from sales order or MRP
- Material reservation/allocation
- Operation tracking (start/pause/complete)
- Backflushing (auto-consume materials)
- Scrap reporting with reason codes
- Rework orders
- Partial completion & over-production
- Production cost analysis (planned vs actual)
```

### 1.4 Material Requirements Planning (MRP)
```
Entities: MrpRun, MrpSuggestion, MrpParameter
Features:
- MRP wizard (run for selected items or all)
- Net requirements calculation
- Planned purchase orders
- Planned production orders
- Lead time offsetting
- Safety stock consideration
- Lot sizing rules (fixed, EOQ, lot-for-lot)
- MRP exception messages (reschedule, cancel)
- Pegging (link demand to supply)
```

### 1.5 Quality Management
```
Entities: QualityInspection, QualityTemplate, QualityParameter,
          QualityResult, NonConformance, CorrectiveAction
Features:
- Inspection at receiving / in-process / final
- Inspection templates with parameters
- Accept/reject/conditional accept
- Non-conformance reporting (NCR)
- Corrective & preventive actions (CAPA)
- Certificate of Analysis (CoA)
- Statistical process control (SPC) basics
```

### 1.6 Advanced Supply Chain
```
Entities: PurchaseRequisition, RfqHeader, RfqVendorResponse,
          VendorEvaluation, LandedCost, LotBatch, SerialNumber,
          GoodsReceipt, GoodsReceiptLine, ThreeWayMatch
Features:
- Purchase Requisition → RFQ → PO → GR → Invoice (full cycle)
- 3-way matching (PO vs GR vs Invoice)
- Vendor evaluation scorecards
- Lot/batch tracking with expiry dates
- Serial number tracking
- Landed cost allocation (freight, customs, insurance)
- Consignment inventory management
- Dropship workflow
- Barcode/QR integration points
- ABC/XYZ inventory classification
- Min/max/reorder point automation
- Demand forecasting (moving average, exponential smoothing)
```

**Estimated effort**: 6-8 weeks
**New entities**: ~35-40
**New endpoints**: ~120-150
**New services**: ~8-10

---

## Phase 2: CRM & Sales Automation

> Every ERP competitor has CRM built-in. Without it, sales teams need separate tools.

### 2.1 Lead & Pipeline Management
```
Entities: Lead, LeadSource, LeadActivity, SalesPipeline, PipelineStage,
          Opportunity, OpportunityLine, OpportunityActivity
Features:
- Lead capture (web form, API, import)
- Lead scoring (rule-based + AI)
- Lead assignment rules (round-robin, territory)
- Sales pipeline with customizable stages
- Opportunity tracking with win probability
- Pipeline analytics & forecasting
- Activity logging (calls, emails, meetings)
- Lead → Opportunity → Quotation → Sales Order conversion
```

### 2.2 Campaign & Marketing
```
Entities: Campaign, CampaignContact, CampaignActivity, EmailCampaign
Features:
- Campaign tracking with ROI
- Email campaign integration
- Customer segmentation (tags, groups, purchase history)
- UTM tracking for lead sources
- Campaign budget tracking
```

### 2.3 Customer 360° View
```
Features:
- Unified customer dashboard (orders, invoices, payments, support, activities)
- Purchase history & trends
- Communication history
- Customer lifetime value (CLV)
- Net Promoter Score (NPS)
- Customer health score
- Contract & warranty tracking
```

### 2.4 Sales Automation
```
Entities: SalesTerritory, SalesTarget, SalesQuota, SalesCommissionRule
Features:
- Territory management
- Sales targets & quotas with tracking
- Commission calculation rules
- Sales order → Delivery → Invoice workflow
- Quotation validity & follow-up automation
- Competitor tracking per opportunity
```

**Estimated effort**: 4-5 weeks
**New entities**: ~20-25
**New endpoints**: ~80-100

---

## Phase 3: HRM & People Management

> Payroll alone is not HR. Modern ERPs cover the full employee lifecycle.

### 3.1 Recruitment (ATS)
```
Entities: JobPosting, Applicant, ApplicationStage, Interview,
          InterviewFeedback, OfferLetter
Features:
- Job posting management
- Applicant tracking pipeline
- Interview scheduling
- Evaluation scorecards
- Offer letter generation
- Careers page integration (CMS)
```

### 3.2 Onboarding & Offboarding
```
Entities: OnboardingTemplate, OnboardingTask, OnboardingChecklist
Features:
- Onboarding checklists (IT setup, documents, training)
- Task assignment to departments
- Progress tracking
- Offboarding workflows (asset return, access revocation)
```

### 3.3 Performance Management
```
Entities: PerformanceReview, ReviewCycle, Goal, OkrObjective,
          OkrKeyResult, CompetencyMatrix, SkillAssessment
Features:
- Review cycles (quarterly, annual)
- OKR/KPI tracking
- 360° feedback
- Competency frameworks
- Performance improvement plans (PIP)
- Goal cascading (company → department → individual)
```

### 3.4 Attendance & Scheduling
```
Entities: AttendanceRecord, ShiftSchedule, ShiftTemplate,
          OvertimeRequest, OvertimeRule
Features:
- Clock in/out (web, mobile, biometric API)
- Shift scheduling with templates
- Overtime rules & calculations
- Attendance reports & analytics
- Late/early/absent tracking
- Integration with payroll
```

### 3.5 Training & Development (LMS)
```
Entities: TrainingCourse, TrainingSession, TrainingEnrollment,
          Certification, CertificationTracking
Features:
- Course catalog management
- Enrollment & completion tracking
- Certification management with expiry
- Training budget tracking
- Skills gap analysis
```

### 3.6 Employee Self-Service
```
Features:
- Personal info updates
- Leave requests with balance view
- Payslip download
- Tax documents (ภงด.91)
- Expense submission
- Training enrollment
- Attendance history
```

**Estimated effort**: 5-6 weeks
**New entities**: ~30-35
**New endpoints**: ~100-120

---

## Phase 4: Workflow Engine & Low-Code Platform

> This is what separates a product from a platform. SAP BTP, Dynamics Power Platform, Odoo Studio.

### 4.1 Visual Workflow Engine
```
Entities: WorkflowDefinition, WorkflowNode, WorkflowTransition,
          WorkflowInstance, WorkflowTask, WorkflowHistory,
          WorkflowRule, EscalationRule
Features:
- BPMN-like visual designer (frontend)
- Node types: Approval, Condition, Action, Notification, Timer, Script
- Parallel & sequential paths
- Conditional branching (field-based rules)
- Escalation with SLA timers
- Delegation & reassignment
- Auto-trigger on entity events (create/update/delete)
- Template library (common workflows)
- Version history
```

### 4.2 Custom Fields (User-Defined Fields)
```
Entities: CustomFieldDefinition, CustomFieldValue
Features:
- Add custom fields to any entity (Contact, Product, Document, Order, etc.)
- Field types: Text, Number, Date, Dropdown, Lookup, Checkbox, File
- Validation rules per field
- Required/optional per workflow stage
- Searchable & filterable
- Export/import with custom fields
- Per-tenant field definitions
```

### 4.3 Custom Views & Dashboards
```
Entities: CustomView, CustomDashboard, DashboardWidget
Features:
- Saved filters/views per user
- Custom list views with column selection
- Drag-and-drop dashboard builder
- Widget types: Chart, KPI, Table, Calendar, Pipeline
- Role-based default dashboards
- Shared views
```

### 4.4 Automation Rules (If-This-Then-That)
```
Entities: AutomationRule, AutomationAction, AutomationLog
Features:
- Trigger: On Create, On Update, On Field Change, On Schedule
- Conditions: Field equals, contains, greater than, is empty
- Actions: Update field, Send email, Create record, Call webhook, Assign user
- Action chains (sequential actions)
- Execution log & debugging
```

**Estimated effort**: 5-6 weeks
**New entities**: ~15-20
**New endpoints**: ~60-80

---

## Phase 5: Business Intelligence & Analytics

### 5.1 Dashboard Builder
```
Features:
- Drag-and-drop widget placement
- Real-time data from any entity
- Chart types: Line, Bar, Pie, Donut, Funnel, Gauge, Heatmap, Map
- Date range selectors with presets
- Comparison (YoY, MoM, QoQ)
- Auto-refresh intervals
- Full-screen presentation mode
- Export to PDF/Image
```

### 5.2 Report Builder Enhancement
```
Features:
- Visual query builder (no SQL needed)
- Cross-entity joins via UI
- Pivot tables
- Drill-down navigation
- Scheduled reports (daily/weekly/monthly email)
- Report subscriptions
- Template library
```

### 5.3 Predictive Analytics
```
Features:
- Revenue forecasting (AI-based)
- Customer churn prediction
- Demand forecasting for inventory
- Cash flow projections with scenarios
- Anomaly detection with alerting
- What-if analysis
- Trend analysis with seasonality
```

### 5.4 Natural Language Queries
```
Features:
- "Show me revenue by product this quarter"
- "What are the top 10 customers by outstanding balance?"
- "Compare expenses this year vs last year"
- AI-powered query interpretation
- Auto-visualization selection
```

**Estimated effort**: 4-5 weeks
**New entities**: ~10-15
**New endpoints**: ~40-50

---

## Phase 6: Advanced Compliance & Security

### 6.1 PDPA/GDPR Compliance
```
Entities: ConsentRecord, DataSubjectRequest, DataRetentionPolicy,
          PersonalDataMapping, DataBreachLog
Features:
- Consent tracking & management
- Data subject requests (access, erasure, portability)
- Personal data mapping (what data, where, why)
- Data retention policies with auto-purge
- Breach notification workflow
- Privacy impact assessments
- Cookie consent integration (CMS)
- Data anonymization tools
```

### 6.2 Advanced Audit & Controls
```
Features:
- Segregation of duties (SoD) matrix
- Conflict detection & alerts
- Role-based access control audit
- Periodic access review campaigns
- SOX compliance controls
- Internal audit management
- Risk assessment framework
- Control testing & evidence collection
```

### 6.3 E-Signature & Legal
```
Features:
- Digital signature integration (Thai e-signature standards)
- Contract lifecycle management
- Document watermarking
- Tamper detection
- Legal hold on documents
- Compliance filing automation
```

**Estimated effort**: 3-4 weeks
**New entities**: ~15-20
**New endpoints**: ~50-60

---

## Phase 7: Integration Ecosystem

### 7.1 Pre-built Connectors
```
Thailand-specific:
- LINE Official Account (messaging, notifications, rich menu)
- Shopee Seller API (orders, products, shipping)
- Lazada Seller API (orders, products, shipping)
- SCB/KBANK/BBL Open Banking APIs
- Thailand Post / Kerry / Flash Express tracking
- PromptPay QR generation
- DBD e-Filing integration
- Revenue Department e-Filing

International:
- Stripe / PayPal payment processing
- QuickBooks / Xero migration tools
- Slack / Microsoft Teams notifications
- Google Workspace (Calendar, Drive, Gmail)
- Zapier / Make.com webhook targets
```

### 7.2 API Platform
```
Features:
- API versioning (v1/v2)
- GraphQL endpoint
- OAuth2 authorization server
- API documentation portal (beyond Swagger)
- API usage analytics per key
- Rate limiting per endpoint per key
- Webhook delivery with retry & dead letter queue
- Bulk operations API
- Cursor-based pagination
```

### 7.3 EDI & B2B
```
Features:
- EDI X12 / EDIFACT support
- Electronic PO exchange
- Electronic invoice exchange (Peppol)
- Supplier portal (self-service)
- Customer portal enhancement
```

**Estimated effort**: 4-5 weeks

---

## Phase 8: AI Copilot & Autonomous Agents

> This is the future differentiator. SAP has Joule, Microsoft has Copilot.

### 8.1 NextAcc Copilot
```
Features:
- Natural language commands ("Create invoice for ABC Corp, 3 items")
- Contextual suggestions ("This payment matches invoice INV-2024-0042")
- Document summarization
- Email draft generation
- Report interpretation
- Anomaly explanation
- Multi-language support (TH/EN)
```

### 8.2 Autonomous Agents
```
Features:
- Auto-reconciliation agent (matches bank ↔ invoices)
- Collection agent (sends reminders, escalates overdue)
- Procurement agent (detects low stock → creates PO → sends to vendor)
- Compliance agent (monitors deadlines → alerts → auto-files)
- Data entry agent (OCR → validate → create records)
```

### 8.3 Predictive Intelligence
```
Features:
- Customer payment behavior prediction
- Inventory demand forecasting
- Revenue prediction by segment
- Cash flow scenario modeling
- Supplier risk assessment
- Price optimization suggestions
```

**Estimated effort**: 4-5 weeks

---

## Phase 9: ESG & Sustainability

### 9.1 Carbon Footprint Tracking
```
Entities: EmissionRecord, EmissionFactor, CarbonFootprint,
          SustainabilityTarget, EsgReport
Features:
- Scope 1, 2, 3 emission tracking
- Carbon footprint per product/service
- Emission factors database
- Reduction target tracking
- ESG reporting (GRI, TCFD, CSRD)
- Sustainability dashboard
```

**Estimated effort**: 2-3 weeks

---

## Phase 10: Mobile & Offline

### 10.1 Mobile App Shell
```
Features:
- React Native / Capacitor wrapper
- Biometric authentication
- Push notifications (FCM/APNS)
- Camera: receipt capture, barcode scan, QR scan
- Offline queue with sync
- Mobile-optimized dashboards
- Approval workflow on mobile
- GPS expense logging
```

**Estimated effort**: 3-4 weeks

---

## Summary Timeline

| Phase | Module | Duration | New Entities | New Endpoints | Priority |
|-------|--------|----------|-------------|---------------|----------|
| 0 | Foundation (Testing/CI/CD/Cache) | 3-4 wk | 0 | 5 | 🔴 |
| 1 | Manufacturing + Supply Chain | 6-8 wk | ~40 | ~150 | 🔴 |
| 2 | CRM & Sales | 4-5 wk | ~25 | ~100 | 🔴 |
| 3 | HRM & People | 5-6 wk | ~35 | ~120 | 🔴 |
| 4 | Workflow + Low-Code | 5-6 wk | ~20 | ~80 | 🟡 |
| 5 | BI & Analytics | 4-5 wk | ~15 | ~50 | 🟡 |
| 6 | Compliance & Security | 3-4 wk | ~20 | ~60 | 🟡 |
| 7 | Integration Ecosystem | 4-5 wk | ~10 | ~40 | 🟡 |
| 8 | AI Copilot & Agents | 4-5 wk | ~10 | ~30 | 🟢 |
| 9 | ESG & Sustainability | 2-3 wk | ~5 | ~20 | 🟢 |
| 10 | Mobile & Offline | 3-4 wk | ~5 | ~15 | 🟢 |
| **Total** | | **~44-55 wk** | **~185** | **~670** | |

### After All Phases:
- **Entities**: 194 → ~380 (nearly 2x)
- **Endpoints**: 841 → ~1,500+
- **LOC**: 72,937 → ~180,000+
- **Feature parity**: SAP/Dynamics/Odoo level with Thailand-first advantage
