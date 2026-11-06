namespace Accounting.Helpers;

/// <summary>
/// รอบ 232 ต่อ (เจ้าของ: "แปลนโยบายเป็นภาษาอังกฤษให้เลย" · ต่อจากรอบ 232 คำตัดสิน 232-Checkout-1) — <b>คำแปลภาษาอังกฤษของเนื้อความตั้งต้น</b>
/// (ตัวตั้งเดียว · คู่กับฉบับไทยใน <c>LegalDocumentDefaults.cs</c>)
/// <list type="bullet">
/// <item><b>ไทยเป็นฉบับผูกพัน</b> — ทุกฉบับปิดท้ายด้วย <see cref="EnglishPrevailClause"/> · เลขฉบับ/หลักฐานการยอมรับผูกฉบับไทยเท่านั้น</item>
/// <item>โครงตรงกันทีละข้อ (หัวข้อ/เลขข้อ/ตาราง) · โทเคน <c>{{…}}</c> ชุดเดียวกันทุกตำแหน่ง · ตัวเลข/ระยะเวลา/อัตราตรงต้นฉบับ
/// — ล็อกด้วย <c>Accounting.Tests/LegalDocumentDefaultsEnRound232Tests</c></item>
/// <item>แก้ฉบับไทยในไฟล์เดิม ⇒ ต้องแก้คำแปลที่นี่ในคอมมิตเดียวกัน (เทสต์จับโทเคน/ตัวเลข/จำนวนหัวข้อที่ไม่ตรง)</item>
/// <item>ใช้กับ <b>ฉบับตั้งต้นในโค้ด</b> เท่านั้น — ฉบับที่แอดมินเผยแพร่เองในฐานไม่ถูกเติมอัตโนมัติ (อาจแก้ไทยไปแล้ว คำแปลนี้จะไม่ตรง) ·
/// แอดมินดึงไปใส่เองได้ที่ปุ่ม "ใส่คำแปลตั้งต้น (อังกฤษ)" ใน <c>/admin/legal.html</c></item>
/// </list>
/// </summary>
public static partial class LegalDocumentDefaults
{
    /// <summary>ประโยคท้ายทุกฉบับอังกฤษ — ไทยเป็นฉบับผูกพัน (คำตัดสิน 232-Checkout-1)</summary>
    public const string EnglishPrevailClause =
        "This English version is provided for convenience. In case of any inconsistency, the Thai version prevails.";

    /// <summary>คำแปลอังกฤษของ <see cref="PlatformBuiltIn"/> (โทเคนชุดเดียวกัน — แทนค่าด้วย <see cref="BuildPlatformTokens"/>) · ชนิดไม่รู้จัก ⇒ โยนเหมือนฉบับไทย</summary>
    public static string PlatformBuiltInEn(string kind) => kind switch
    {
        KindPrivacy => PlatformPrivacyV1En,
        KindTerms => PlatformTermsV1En,
        KindRefund => PlatformRefundV1En,
        _ => throw Unknown(kind),
    };

    /// <summary>คำแปลอังกฤษของ <see cref="ShopTemplate"/> (โทเคนชุดเดียวกัน — <c>CmsLegalPages.BuildShopTokens</c>/<c>BuildRefundTokens</c>) · ชนิดไม่รู้จัก ⇒ โยน</summary>
    public static string ShopTemplateEn(string kind) => kind switch
    {
        KindPrivacy => ShopPrivacyTemplateEn,
        KindTerms => ShopTermsTemplateEn,
        KindRefund => ShopRefundTemplateEn,
        _ => throw Unknown(kind),
    };

    // ════════════════════════ Platform — Privacy Policy v1.0 (EN) ════════════════════════
    private const string PlatformPrivacyV1En = """
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
| Accounts, supporting accounting documents and financial statements | **5 years** from the closing date of the accounts (the Director-General may order a longer period of up to 7 years) | Accounting Act, Section 14 |
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
""";

    // ════════════════════════ Platform — Terms of Service v1.0 (EN) ════════════════════════
    private const string PlatformTermsV1En = """
These Terms constitute an agreement between you (the “**User**”) and **{{controller.name}}** (the “**Service Provider**”, “we”) for the use of the online accounting system and related services (the “**Service**”). By signing up for or using the Service, you are deemed to have read, understood and agreed to be bound by these Terms, including the [Privacy Policy](/privacy.html), which forms part of these Terms.

## 1. User Accounts and Security

- You must provide accurate and up-to-date information when signing up and are responsible for keeping your password confidential
- Signing in with a Google, Facebook or LINE account is identity verification through an external provider; we receive only your name, email and user identifier from that provider — **we do not receive your password**
- You are responsible for all activities that occur under your account and must notify us immediately upon discovering any unauthorized access
- One account per person — sharing an account with others renders the audit trail unusable as evidence

## 2. Roles of Each Party with Respect to Data in the System

> This Clause is important for liability under the Personal Data Protection Act B.E. 2562 (2019) (PDPA)

- **Your user account data** (name, email, telephone number, access history) — we are the **data controller** as stated in the [Privacy Policy](/privacy.html)
- **Data you enter into the system** (customers, vendors, employees, accounting documents, including customer data collected through your shop website/booking system) — **you** are the data controller and we are the **data processor** (Section 40), processing only on your instructions. You are responsible for having a lawful basis for collecting and using such data and for providing your own privacy policy to your customers (the system provides a template you may adapt)
- **E-mails sent on your behalf** — when you instruct the system to send a document, or a link to sign/accept a document, to your counterparty before you have configured your own e-mail sender, we send it from the system e-mail address as your **data processor** acting on your instructions. We state your business name and tax identification number as the party on whose behalf it is sent, we never use your e-mail address or domain as the sender, replies go to your e-mail address, and we use a fixed system format for transactional e-mails about that document only (not advertising; any text you add is sent as plain text). You are responsible for ensuring that the recipient is genuinely the counterparty of that document. We limit the number of recipients per e-mail and the number of e-mails per day (lower for businesses that have not yet verified their tax identification number or have never paid for the Service) and may suspend sending that is reported as misuse. Once you have configured and successfully tested your own e-mail sender, e-mails are sent directly from your own address
- **Exchanging documents with counterparties who use the Service** — when you link your business as a trading partner with another business that uses the Service and instruct the system to send a document (such as an invoice, tax invoice, receipt or withholding tax certificate) to that counterparty, we copy the data of that document into the counterparty's account on your instructions as your **data processor**; once it is in the counterparty's account, the counterparty is the data controller of that copy. Documents received through this channel always enter the recipient's system as a **draft**, and the system does not claim input tax on them automatically unless the source is an electronic tax invoice that has been digitally signed and time-stamped — an image or PDF rendered by the system is not the original tax invoice, and the recipient must confirm receipt of the original before claiming. You may unlink at any time; documents already sent remain in the recipient's account for the period the law requires the recipient to retain them
- **Documents and notifications sent via LINE on your behalf** — when you instruct the system to send a document or notification to your contacts through the Service Provider's official LINE account, we send it as your **data processor** acting on your instructions, using only templates assembled by the system and stating your business name as the party on whose behalf it is sent. You warrant that you have a lawful basis to send the document to that contact and will not use this channel for marketing or advertising. Recipients may unsubscribe at any time, and we may suspend any business's use of this channel immediately when notified by LINE or when we find use that does not comply with this clause

## 3. Accuracy of Accounting and Tax Data

- The Service is a **tool** to assist with bookkeeping and tax calculation; it does not constitute professional accounting services or case-specific tax advice
- Under Section 19 of the Accounting Act B.E. 2543 (2000), a person required to prepare accounts must engage a **bookkeeper with the qualifications prescribed by the Director-General under Section 7(6)**; use of the system does not replace that obligation
- Filing tax returns with the Revenue Department, submitting financial statements to the Department of Business Development, and the accuracy and completeness of the data filed, are your responsibility
- You should review documents, tax reports and accounting entries generated by the system every time before actual use

## 4. Artificial Intelligence (AI) and Automated Document Reading (OCR) Functions

- The system provides functions that help read documents and suggest chart-of-accounts codes/transaction classifications. The results are **suggestions** that must always be confirmed by you before they are actually recorded
- The system displays labels indicating whether each suggestion comes from an internal model or from an external AI service provider
- We do not warrant the complete accuracy of automated results — you remain responsible for review under Clause 3

## 5. Service Fees and Packages

- Service fees, scope of use and quotas of each package are as displayed on the package page at the time of sign-up
- Trial packages end at the end of the specified period. If service fees are not paid to continue, the account's usage rights will be restricted, but **your data will not be deleted immediately** (see Clause 6)
- Changes to service fees will be notified at least 30 days in advance by email to your registered address

## 6. Data Retention and Termination of Service

- You **may export your data at any time** while your account is active
- When you terminate the Service, we will retain data for the periods required by law, namely **5 years** for accounts and supporting accounting documents (Accounting Act, Section 14 — counted from the closing date of the accounts) and tax reports (Revenue Code, Section 87/3). After that period, the data will be deleted or anonymized
- During any period in which we are legally required to retain data, the data is placed under *legal hold* and **cannot be deleted upon request** (details in Clauses 7-8 of the [Privacy Policy](/privacy.html))
- We may suspend the Service immediately if we find use that is unlawful or that affects the security of the system as a whole

## 7. Prohibited Conduct

- Using the Service for any unlawful purpose, including preparing false accounting documents or tax invoices
- Hacking, conducting penetration tests without written permission, reverse engineering, or attempting to access other users' data
- Using automated processes to access the Service in a manner that affects other users, or circumventing package quotas
- Copying, modifying or reselling the software without permission

## 8. Intellectual Property

The software, user interface, standard chart of accounts, document templates and all content of the Service are the property of the Service Provider. You are granted a non-exclusive, non-transferable right of use for as long as your account remains in effect. **Data you enter into the system remains yours.**

## 9. Limitation of Liability

- The Service is provided “as is”; we do not warrant that the Service will be uninterrupted or entirely error-free
- We are not liable for penalties, surcharges or fines arising from late or incorrect filing, which are your responsibility under Clause 3
- Our aggregate liability in all cases shall not exceed the service fees you actually paid in the 12 months preceding the event
- The above limitations do not apply to liability that cannot be limited by law, such as wilful misconduct or gross negligence

## 10. Changes to these Terms

We may amend these Terms and will announce the new version together with its version number and effective date on this page. For material changes, we will give at least 30 days' prior notice by email. Continued use of the Service after the effective date constitutes your acceptance of the new version.

## 11. Governing Law

These Terms are governed by the laws of Thailand, and the Thai courts shall have jurisdiction.

## 12. Contacting the Service Provider

| Item | Details |
|---|---|
| Legal entity name | {{controller.name}} |
| Taxpayer identification number | {{controller.taxId}} |
| Address | {{controller.address}} |
| Telephone | {{controller.phone}} |
| Email | [{{controller.email}}](mailto:{{controller.email}}) |

> *This English version is provided for convenience. In case of any inconsistency, the Thai version prevails.*
""";

    // ════════════════════════ Platform — Cancellation & Refund Policy v1.0 (EN) ════════════════════════
    private const string PlatformRefundV1En = """
This Policy applies to all types of service fees that you pay to **{{controller.name}}** (the “Service Provider”, “we”) for the **{{site.name}}** service and forms part of the [Terms of Service](/terms.html) — the system asks you to tick to accept this Policy before every payment and keeps a record of the version you accepted as evidence.

## 1. No Refund of Service Fees

- Package fees, add-on fees, document/message packs and other service fees that have been paid are **non-refundable**, in whole or in part, including where the billing cycle is not fully used or the Service is not used
- Prices and billing cycles are displayed on the payment page every time before you confirm

## 2. Cancelling a Package

- You may cancel your package at any time from the "Packages" page in the system
- Cancellation **takes effect at the end of the paid billing cycle** — you may continue to use the package's entitlements until the end of the cycle, and we will not charge for the next cycle
- Before the end of the cycle, you may resume (undo the cancellation) from the same page
- During a free trial, you may cancel immediately at no cost

## 3. Changing Packages

- Upgrade: takes effect once payment for the new package has been received
- Downgrade or change of billing cycle: takes effect at the end of the current cycle — no refund of the difference for the cycle already paid

## 4. Cases in Which We Will Refund

Notwithstanding Clause 1, we will refund in the following cases:

- Duplicate charges, or charges of an incorrect amount due to a system error — the excess will be refunded
- You have paid but the system did not grant your entitlements and we are unable to fix it
- Cases in which a refund is required by law

Where a refund is made after a tax invoice has been issued, we will issue you a **credit note** (Revenue Code, Section 86/10) in every case.

## 5. Refund Method and Timeframe (only for cases under Clause 4)

- Paid by card: refunded to the original card through the payment service provider — the time for the refund to appear depends on the card-issuing bank
- Paid by PromptPay/bank transfer/mobile banking: transferred back to the account you designate
- We will process the refund within 30 days of confirming that the case falls under Clause 4

## 6. Your Data after Cancellation

You may export your data at any time while your account is active · Retention of data after termination of the Service is governed by Clause 6 of the [Terms of Service](/terms.html) and the [Privacy Policy](/privacy.html)

## 7. Statutory Rights

This Policy does not exclude or limit your rights under the Consumer Protection Act B.E. 2522 (1979), the Unfair Contract Terms Act B.E. 2540 (1997) or any other applicable law — if any provision conflicts with the law, the law shall prevail to the extent of that conflict only.

## 8. Contact

| Item | Details |
|---|---|
| Service Provider | {{controller.name}} |
| Taxpayer identification number | {{controller.taxId}} |
| Address | {{controller.address}} |
| Telephone | {{controller.phone}} |
| Email | [{{controller.email}}](mailto:{{controller.email}}) |

> *This English version is provided for convenience. In case of any inconsistency, the Thai version prevails.*
""";

    // ════════════════════════ Shop (customer's CMS website/booking system) — Privacy Policy template (EN) ════════════════════════
    private const string ShopPrivacyTemplateEn = """
This Policy explains how **{{company.name}}** (the “Shop”, “we”) collects, uses and discloses the personal data of customers who place orders, book services, register as members or make contact through the website **{{site.name}}**, in accordance with the Personal Data Protection Act B.E. 2562 (2019) (PDPA).

## 1. Data Controller

| Item | Details |
|---|---|
| Business operator name | {{company.name}} |
| Taxpayer identification number | {{company.taxId}} |
| Address | {{company.address}} |
| Telephone | {{company.phone}} |
| Email | [{{company.email}}](mailto:{{company.email}}) |

> The Shop is the **data controller** of its customers' personal data, while the provider of the website and accounting system (NextAcc) is a **data processor** (Section 40) that processes data only on the Shop's instructions. Please contact the Shop directly through the channels above for any request to exercise your rights.

## 2. Personal Data We Collect

| Category | Details | Collected when |
|---|---|---|
| Contact data | Full name · telephone number · email · delivery address | Placing an order · booking · submitting a contact form |
| Order and booking data | Products/services · booking date and time · number of guests/service recipients · notes you provide | Placing an order · booking |
| Payment data | Amount paid · payment channel · transaction reference number · payment slip you attach — **the Shop does not store credit card numbers** (the payment service provider processes card data) | Making a payment |
| Tax invoice data | Name · address · taxpayer identification number · branch (only when you request a full tax invoice) | Requesting a tax invoice |
| Member account | Name · email · telephone number · password stored with one-way encryption · order/booking/course history | Registering as a member |
| LINE | LINE user ID linked to an order/booking/member account for sending notifications | When you choose to link LINE |
| Technical data | IP address · browser type · strictly necessary cookies, and analytics cookies (only when you accept them) | Visiting the website |
| Evidence of consent | Text you consented to · date and time · channel | Ticking the consent box on a form |

## 3. Purposes and Lawful Bases

| Purpose | Lawful basis |
|---|---|
| Processing orders, delivery, confirming/changing/cancelling bookings and maintaining member accounts | Contract (Section 24(3)) |
| Issuing receipts/tax invoices and keeping accounting evidence | Legal obligation (Section 24(6)) — Revenue Code · Accounting Act |
| Notifying order/booking status by email, SMS or LINE | Contract (Section 24(3)) |
| Preventing fraud and maintaining system security | Legitimate interest (Section 24(5)) |
| Sending news/promotions | **Consent** (Section 19) — may be withdrawn at any time |

## 4. Disclosure of Data

The Shop does not sell personal data and discloses it only to the extent necessary, namely to:

- **The website and accounting system provider** (data processor) — to store data and issue documents for the Shop
- **Payment service providers** — only when you pay through an online channel
- **Delivery service providers** — name, telephone number and delivery address, only for orders that require delivery
- **LINE** — only when you link your LINE account to receive notifications
- **Government authorities** — where required by law (such as the Revenue Department)

## 5. Retention Periods

- Order, booking, receipt and tax invoice data: **5 years** under Section 87/3 of the Revenue Code and Section 14 of the Accounting Act
- Member accounts: for as long as you remain a member
- Marketing data: until you withdraw your consent
- Upon expiry, the Shop will delete or anonymize the data

## 6. Your Rights

You have the right to request access/a copy (Section 30) · portability (Section 31) · to object (Section 32) · erasure (Section 33) · restriction of use (Section 34) · rectification (Section 35), and to withdraw your consent at any time, by contacting the Shop at [{{company.email}}](mailto:{{company.email}}) or by telephone at {{company.phone}}. The Shop will act on your request within **30 days** — data that the law requires to be retained (such as tax invoices) can be deleted only after the period in Clause 5 has expired.

If you believe that the Shop has processed your data unlawfully, you may lodge a complaint with the Office of the Personal Data Protection Committee (PDPC).

## 7. Changes to this Policy

The Shop may update this Policy and will display the date of the latest update on this page.

> *This English version is provided for convenience. In case of any inconsistency, the Thai version prevails.*
""";

    // ════════════════════════ Shop — Terms of Service template (EN) ════════════════════════
    private const string ShopTermsTemplateEn = """
These Terms apply to purchases of products, bookings of services and member registrations through the website **{{site.name}}**, operated by **{{company.name}}** (the “Shop”). By placing an order or making a booking, you are deemed to accept these Terms and the Shop's [Privacy Policy](privacy).

## 1. Seller/Service Provider

| Item | Details |
|---|---|
| Business operator name | {{company.name}} |
| Taxpayer identification number | {{company.taxId}} |
| Address | {{company.address}} |
| Telephone | {{company.phone}} |
| Email | [{{company.email}}](mailto:{{company.email}}) |

## 2. Orders and Prices

- Prices are displayed in Thai baht (THB) and are as shown at the time of order (inclusive or exclusive of value added tax as indicated on the product page)
- An order becomes effective when the Shop confirms it and payment has been received through the selected channel (or when the Shop confirms a cash-on-delivery order)
- If a product is out of stock or a price is manifestly displayed in error, the Shop will notify you and refund in full for any item that cannot be delivered

## 3. Bookings of Services/Rooms

- A booking is confirmed when the Shop notifies its confirmation and (if applicable) the deposit specified at the time of booking has been received
- Rescheduling or cancelling a booking is subject to the cancellation/fee policy displayed at the time of booking and on your booking page
- Please arrive on time — if you are later than the time set by the Shop without notice, the Shop may treat the booking as forfeited

## 4. Payment and Documents

- Payment is made through the channels shown on the payment page — the Shop does not store your credit card number
- You may request a full tax invoice by completing the buyer information at the time of order/booking

## 5. Delivery, Returns and Refunds

- Delivery times and fees are as displayed at the time of order
- Cancellations, returns and refunds are governed by the Shop's [Cancellation & Refund Policy](refund), which you accept before payment
- Your rights under consumer protection law are not limited by these Terms

## 6. Member Accounts

- You are responsible for keeping your password secure and for activities that occur under your account
- You may request to close your account at any time by contacting the Shop (data that the law requires to be retained will be kept in accordance with the [Privacy Policy](privacy))

## 7. Contact and Complaints

Contact the Shop at [{{company.email}}](mailto:{{company.email}}) or by telephone at {{company.phone}} — these Terms are governed by the laws of Thailand.

> *This English version is provided for convenience. In case of any inconsistency, the Thai version prevails.*
""";

    // ════════════════════════ Shop — Cancellation & Refund Policy template (EN) ════════════════════════
    // ค่าโทเคน shop.* ต้องออกเป็นภาษาอังกฤษเมื่อแสดงฉบับนี้ — CmsLegalPages.BuildRefundTokens(..., english: true)
    private const string ShopRefundTemplateEn = """
This Policy applies to purchases of products, bookings of services and accommodation bookings through the website **{{site.name}}**, operated by **{{company.name}}** (the “Shop”), and forms part of the Shop's [Terms of Service](terms) — you tick to accept this Policy before confirming an order/booking, and the Shop keeps the version you accepted as evidence.

## 1. Cancelling an Order

- You may cancel before the Shop ships or begins producing the product by contacting the Shop through the channels below — if already paid, you will receive a full refund
- For custom-made/made-to-order products for which a deposit has been paid, upon cancellation: {{shop.depositForfeit}}
- If you cancel after the Shop has shipped, delivery costs actually incurred are non-refundable

## 2. Cancelling Service and Room Bookings

- Subject to the cancellation policy displayed on the booking page before you confirm and pay (for example, free cancellation within a specified time, cancellation fees, or non-refundable rates) — the system keeps a copy of the policy as at the booking date
- If you do not show up as scheduled without prior notice, the Shop may treat the booking as forfeited and not refund the deposit

## 3. Returns

- For products that are defective, damaged or not as ordered, please notify the Shop within **{{shop.returnWindowDays}} days** of receipt, with photographs/evidence — the Shop will replace the product or refund
- Personalized custom-made products, and products that have been used or naturally deteriorate (such as food), cannot be returned unless defective or not as ordered

## 4. Refund Method and Timeframe

- Refunds are made through the original channel: paid by card, refunded to the original card (the time for the refund to appear depends on the card-issuing bank) · paid by PromptPay/bank transfer, refunded to the account you designate
- The Shop will process the refund within **{{shop.refundProcessingDays}} days** of approving the refund
- Where the Shop has already issued a tax invoice, the Shop will issue a **credit note** under Section 86/10 of the Revenue Code

## 5. Statutory Rights

This Policy does not exclude or limit your rights under consumer protection law.

## 6. Contacting the Shop

Contact the Shop at [{{company.email}}](mailto:{{company.email}}) or by telephone at {{company.phone}}

> *This English version is provided for convenience. In case of any inconsistency, the Thai version prevails.*
""";
}
