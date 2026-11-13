# A50: document and email template placeholders (task 361)

Owner: TPL-50. Decisions: [ADDENDUM-50-ANALYSIS.md](ADDENDUM-50-ANALYSIS.md) D-25, D-26, D-65..D-69. Endpoints: [API-CONTRACT.md](API-CONTRACT.md) "D-TPL".

The list below is the code's list. `GET api/onboarding/document-templates/placeholders/{type}` and `GET api/onboarding/email-templates/placeholders/{type}` return it from `src/SMS.Modules.Onboarding/Templates/PlaceholderCatalog.cs`.

## 1. How a placeholder is filled

- **Syntax:** `{Name}`, letters and digits only.
  - Case-insensitive: `{customername}` = `{CustomerName}`.
  - The engine is `SMS.Shared.Onboarding.TemplateText`, dunning-style.
- **Unknown tokens are left as typed.** A typo like `{CustmerName}` shows up in the preview; it does not quietly vanish.
- **Values are HTML-encoded** in every HTML part:
  - document header, body and footer;
  - email body.
  - A customer called `<b>Acme</b>` prints exactly as typed, never as markup.
- **The email subject is plain text.** Its values are not encoded, and line breaks are removed, so a value can never inject a header.
- **`{Message}`:** the sender's message from the email dialog. It is encoded and keeps its line breaks.
- **Plain-text email bodies:** a body with no tags is treated as plain text. It is encoded and its line breaks become `<br>`.
- **Empty values:** a value the document doesn't have prints empty. Examples: `{DeliveryNumber}` on an invoice with no delivery, `{Notes}`.

## 2. Document templates

A template has a header, a body, a footer, a colour, a logo, bank details and terms.

### 2.1 Where each part is used

| Part | Without a body (bodyHtml empty, as the seeded packs ship, D-65) | With a body |
|---|---|---|
| **Header HTML** | Replaces the letterhead's company block (name, address, contact line). The title, document number, date and party block are still printed by the layout. | Same |
| **Body HTML** | The built-in layout is used: invoice with payments and status banners, statement with aging, packing list by carton, and so on. | Rendered by the templated renderer, with the structural blocks below |
| **Footer HTML** | Replaces the old footer text line. The "Generated … / page x of y" line stays. | Same |
| **Colour / logo** | Accents, table heads, stripes / logo top right | Same |
| **Bank details** | Invoice "Payment details" box (the template's text, else the letterhead's; none when *Show bank details* is off); quotation | `{BankDetails}` |
| **Terms & conditions** | A "TERMS & CONDITIONS" section at the end | `{TermsAndConditions}` |

### 2.2 The header/footer HTML subset (D-66)

The built-in layouts are QuestPDF, not a browser, so they read only a small subset of HTML:
- **Lines:** `<div>`, `<p>`, `<br>`, `<li>` and `<h1>`–`<h4>` separate lines.
- **Columns:** `<td>` cells sit side by side.
- **Bold:** `<b>`, `<strong>`, `<h1>`–`<h4>` or `font-weight:bold`.
- **Size:** `font-size:Npx` or `Npt`.
- **Colour:** `color:#RRGGBB`.
- **Alignment:** `text-align`.

Everything else is ignored and is never an error. The body accepts what the rich-text editor produces: p, div, br, b/strong, i/em, u, ul/ol/li, h1–h4, text-align.

### 2.3 Common placeholders (every document type)

Company values come from the organization's letterhead (Settings › Documents › PO template). Without one, the company name is the organization's name.

| Token | Value |
|---|---|
| `{CompanyName}` `{CompanyAddress}` `{CompanyPhone}` `{CompanyEmail}` `{CompanyTaxId}` | The letterhead |
| `{DocumentTitle}` | INVOICE, QUOTATION, PURCHASE ORDER, PACKING LIST, PAYMENT RECEIPT, STATEMENT OF ACCOUNT |
| `{DocumentNumber}` `{DocumentDate}` | The document's number and date (dd MMM yyyy) |
| `{CurrencyCode}` `{TotalAmount}` `{AmountInWords}` | e.g. `PKR`, `3,780.00`, `PKR Three thousand seven hundred and eighty only` |
| `{Notes}` `{GeneratedDate}` | The document's notes; when it was printed |
| `{PageNumber}` | Reserved; empty for now |

Structural blocks: put each **on its own**, outside any `<p>`. They are body only.

| Token | Block |
|---|---|
| `{LineItems}` | The lines as a table, with the type's columns |
| `{TaxSummary}` | Tax per code/rate (invoice, quotation) |
| `{Totals}` | Subtotal / discount / tax / total (/ paid / balance due) |
| `{BankDetails}` | The template's bank details, if shown |
| `{TermsAndConditions}` | The template's terms |
| `{SignatureBlock}` | Signature lines. The PO uses the letterhead's labels, or its disclaimer when the signature block is off |

### 2.4 Per type

| Type | Tokens |
|---|---|
| **SalesInvoice** | `{CustomerName}` `{CustomerAddress}` `{CustomerEmail}` `{CustomerPhone}` `{InvoiceNumber}` `{InvoiceDate}` `{DueDate}` `{PaymentTerms}` `{SaleOrderNumber}` `{DeliveryNumber}` `{Status}` `{Subtotal}` `{DiscountAmount}` `{TaxAmount}` `{AmountPaid}` `{BalanceDue}` |
| **Quotation** | `{CustomerName}` `{CustomerAddress}` `{CustomerEmail}` `{CustomerReference}` `{QuotationNumber}` `{QuotationDate}` `{ValidUntil}` `{PaymentTerms}` `{DeliveryTerms}` `{Status}` `{Subtotal}` `{DiscountAmount}` `{TaxAmount}`. Rejected lines are left out. |
| **PurchaseOrder** | `{SupplierName}` `{SupplierAddress}` `{SupplierPhone}` `{SupplierMobile}` `{PoNumber}` `{PoTitle}` `{PoDate}` `{DeliveryDate}` `{DeliveryWarehouse}` `{BudgetCode}` `{Status}`. Also every pre-A50 PO token without the double braces (`{{PoNumber}}` → `{PoNumber}`; `{{LineItemsTable}}` → `{LineItems}`). |
| **DeliveryNote** (packing list) | `{CustomerName}` `{DeliveryNumber}` `{SourceNumber}` `{ShipFrom}` `{ShipTo}` `{PromisedDate}` `{PackageCount}` `{TotalPieces}` `{GrossWeight}` `{Status}`. The gate pass keeps the house look. |
| **PaymentReceipt** | `{CustomerName}` `{PaymentNumber}` `{PaymentDate}` `{PaymentMethod}` `{Reference}` `{AmountPaid}` `{AllocatedAmount}` `{UnallocatedAmount}` `{InvoiceNumber}` (the invoices paid, comma-separated) |
| **CustomerStatement** | `{CustomerName}` `{CustomerCode}` `{PeriodFrom}` `{PeriodTo}` `{OpeningBalance}` `{ClosingBalance}`. Customer statements only; vendor statements keep the house look. |

## 3. Email templates

Common to every type: `{CompanyName}` `{CompanyAddress}` `{CompanyPhone}` `{CompanyEmail}` `{CompanyTaxId}` `{Message}` `{DocumentUrl}` (empty for now).

| Type | Tokens | Sent by |
|---|---|---|
| **InvoiceSent** | `{CustomerName}` `{CustomerEmail}` `{InvoiceNumber}` `{InvoiceDate}` `{DueDate}` `{TotalAmount}` `{AmountDue}` `{CurrencyCode}` `{AmountInWords}` | `POST api/sales-invoices/{uuid}/email` (PDF attached) |
| **QuotationSent** | `{CustomerName}` `{QuotationNumber}` `{QuotationDate}` `{ValidUntil}` `{TotalAmount}` `{CurrencyCode}` `{AmountInWords}` | `POST api/sale-quotations/{uuid}/email` (PDF attached) |
| **POToSupplier** | `{SupplierName}` `{PoNumber}` `{PoDate}` `{DeliveryDate}` `{TotalAmount}` `{CurrencyCode}` `{AmountInWords}` | `POST api/purchase-orders/{uuid}/email` (PDF attached) |
| **PaymentReminder** | `{CustomerName}` `{InvoiceNumber}` `{DueDate}` `{AmountDue}` `{CurrencyCode}` `{DaysOverdue}` | Seeded only. A48 dunning keeps its own templates (D-26). |
| **PaymentReceived** | `{CustomerName}` `{PaymentNumber}` `{PaymentDate}` `{AmountPaid}` `{CurrencyCode}` `{InvoiceNumber}` | Seeded only (residual R-TPL-3) |
| **WelcomeEmail** | `{UserName}` `{FirstName}` `{OrganizationName}` `{RoleName}` `{LoginUrl}` `{AcceptInviteUrl}` (empty: sent after the invite was accepted) | Accept-invite (Auth hook `IUserInviteAcceptedHandler`) |
| **PayslipSent** | `{EmployeeName}` `{PayPeriod}` `{NetPay}` `{CurrencyCode}` | Seeded here; sent by HRM (`POST api/hrm/payslips/{uuid}/email`, D-93). `{PayPeriod}` = month and year of the period end, `{NetPay}` = 2 decimals |

When an org has no default, active template of a type, the built-in text is sent. That text has the same wording as Onboarding's fallback.

## 4. Preview and send-test sample values

- Previews and send-test fill the tokens with sample values: Acme Traders Ltd, SINV-20260920-0001, 3,780.00 PKR, and so on.
- Where the org has a letterhead, its own company block replaces the sample one.
- The document preview without a body shows these blocks in order: `{LineItems}{TaxSummary}{Totals}{Notes}{BankDetails}{TermsAndConditions}`. They sit under the template's header, footer, colour and logo. This approximates the built-in layout (R-TPL-1).
