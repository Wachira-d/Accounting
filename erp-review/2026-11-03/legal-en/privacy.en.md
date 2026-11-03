## 1. Data Controller

| Item | Details |
|---|---|
| Legal entity name | {{controller.name}} |
| Taxpayer identification number | {{controller.taxId}} |
| Address | {{controller.address}} |
| Telephone | {{controller.phone}} |
| Contact email for personal data matters | [{{controller.email}}](mailto:{{controller.email}}) |

> **Scope of this Policy:** This Policy covers only your **user account** data with us. For the data of customers, vendors and employees that you enter into the system, you are the data controller and we act solely as a **data processor** on your instructions (Section 40) — see Clause 5.

## 2. Personal Data We Collect

| Category | Details | Source |
|---|---|---|
| User account data | Full name · email · telephone number (if provided) · password stored with one-way encryption (bcrypt) — **we do not store passwords in readable form** | Provided by you |
| Data from signing in with an external account | Display name · email · the user identifier assigned by that provider — **we do not receive the password** of your Google / Facebook / LINE account | Google · Facebook · LINE (when you grant permission) |
| Business data you create | Business name · taxpayer identification number · address · selected package | Provided by you |
| Usage and security data | IP address · browser type (user-agent) · sign-in times · failed sign-in history · change trail (audit log) | Collected automatically when you use the Service |
| Evidence of consent | Purpose consented to · version of the policy shown to you when you accepted · date and time · IP address · channel (form/Google/Facebook/LINE) · integrity check value (hash) | Recorded at sign-up or when you accept a new version |

We do not collect **sensitive data** under Section 26 (such as race, religion, health data or biometric data) for the purpose of opening a user account.

## 3. Purposes and Lawful Bases

| Purpose | Lawful basis |
|---|---|
| Creating and maintaining your user account · providing the service under your selected package · issuing service fee receipts | Necessity for the performance of a contract (Section 24(3)) |
| Preventing unauthorized access · detecting abnormal use · keeping audit logs | Legitimate interest (Section 24(5)) |
| Complying with accounting and tax laws (Accounting Act · Revenue Code) | Legal obligation (Section 24(6)) |
| Sending news, promotions or marketing surveys | **Consent** (Section 19) — may be withdrawn at any time without affecting your use of the Service |
| Accepting the Terms of Service and this Policy at sign-up | **Consent** (Section 19) — we keep evidence as described in Clause 2 |

## 4. Cookies and Data Stored in Your Browser

- We use `localStorage` to store your sign-in token, the currently selected company and your display settings so that you do not need to sign in every time — this is **strictly necessary for the Service to function**
- During sign-in with LINE, we use `sessionStorage` to temporarily store a verification value (state) to prevent forged requests, and delete it immediately after use
- Clearing your browser data will require you to sign in again but does not affect the data in your account

## 5. Disclosure to Third Parties

We do not sell personal data and disclose it only to the extent necessary to provide the Service, namely to:

- **Infrastructure and email service providers** — to store data and send system emails (identity verification, notifications, receipts)
- **Identity providers** — Google / Facebook / LINE, only when you choose to sign in with that method
- **Artificial intelligence service providers** — when you use document-reading or automated suggestion functions, the system is designed to **process primarily with internal models** and to send data to external providers only for items the internal models cannot yet decide; the system administrator can disable all calls to external services
- **Government authorities** — where required by law or court order

## 6. Cross-Border Data Transfers

Some service providers may have servers located outside the Kingdom of Thailand. In such cases we will act in accordance with Section 28 by selecting service providers with adequate data protection standards and binding them by standard data protection contractual clauses.

## 7. Data Retention Periods

| Data | Retention period | Basis |
|---|---|---|
| User account and profile data | For as long as you use the Service | Contract |
| Accounts, supporting accounting documents and financial statements | **5 years** | Accounting Act, Section 10 |
| Purchase/sales tax reports and supporting documents | **5 years** from the filing date | Revenue Code, Section 87/3 |
| Personal data access records (PII access log) | Not less than **1 year** | PDPA, Section 37 |
| Evidence of consent | For the life of the account and thereafter for the applicable limitation period | PDPA, Section 19 (burden of proof) |

Where a single data item is subject to more than one law, the system applies the **longest retention period** and places it under *legal hold* to prevent premature deletion. Upon expiry, the data will be deleted or anonymized.

## 8. Rights of the Data Subject

- **Right of access and to obtain a copy** of your data (Section 30)
- **Right to data portability** in a machine-readable format (Section 31)
- **Right to object** to the collection, use or disclosure (Section 32)
- **Right to erasure or destruction**, or anonymization (Section 33)
- **Right to restriction of use** of the data (Section 34)
- **Right to rectification** so that the data is accurate and up to date (Section 35)
- **Right to withdraw consent** previously given at any time (Section 19, paragraph five) — withdrawal does not affect the lawfulness of processing carried out before withdrawal

You may exercise your rights by sending a request to [{{controller.email}}](mailto:{{controller.email}}) or, if you are already signed in, through the **Privacy (PDPA)** menu in the system. We will act on your request within **30 days** of receipt.

> **Limitation to note:** A request for erasure cannot be fulfilled for data that we are legally required to retain, such as accounting documents and tax reports still within the 5-year period under Clause 7. In such cases we will inform you of the reasons and of the date on which the data can be deleted.

## 9. Security Measures (Section 37)

- Data in transit is encrypted with TLS, and sensitive data is encrypted at rest
- Role-based access control (RBAC) is enforced and each business's data is segregated from others
- Personal data access records and change trails are kept in append-only form with chained integrity check values
- Accounts are temporarily locked after more than the permitted number of failed sign-in attempts

## 10. Personal Data Breach Notification

In the event of a personal data breach, we will notify the Office of the Personal Data Protection Committee (PDPC) within **72 hours** of becoming aware of it, and will notify data subjects without delay if the breach poses a high risk to your rights and freedoms (Section 37(4)).

## 11. Changes to this Policy

When this Policy is amended, we will announce the new version together with its version number and effective date on this page. If a material change relies on consent as its lawful basis, we will **request your consent again** rather than treat you as having accepted it by implication.

## 12. Complaints

If you believe that our processing of your data is unlawful, you have the right to lodge a complaint with the **Office of the Personal Data Protection Committee (PDPC)** through the channels it prescribes; however, we welcome the opportunity to address your concern directly first at [{{controller.email}}](mailto:{{controller.email}}).

> *This English version is provided for convenience. In case of any inconsistency, the Thai version prevails.*
