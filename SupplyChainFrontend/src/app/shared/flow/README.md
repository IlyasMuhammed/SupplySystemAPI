# SMS Flow page kit

Every screen follows the SMS Flow design: breadcrumbs in the top bar, a command bar, a dynamic header, content in cards,
and (on documents) a side panel. Styles: `src/assets/layout/_flow.scss` (all `sf-*`). Components: `src/app/shared/flow`
(`imports: [...FLOW]`). Colours are `--sms-*` tokens only (`src/assets/layout/variables/_common.scss`).

**Reference pages — copy their structure:**
- List report: `pages/sales/sale-orders/sale-order-list/sale-order-list.component.html`
- Object (document) page: `pages/finance/sales-invoices/sales-invoice-detail/sales-invoice-detail.component.html` (+ `stages`/`sections` getters in its `.ts`)

## Page types

### 1. List report (any "all X" page)
```html
<p-toast></p-toast>
<sf-page [crumbs]="['Sales', 'Sale orders']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="New …" icon="pi pi-plus" …></p-button>          <!-- primary: solid, first -->
    <p-button label="Export" icon="pi pi-download" [text]="true" …></p-button>  <!-- others: [text]="true" -->
    <span class="sf-sep"></span> …  <span class="sf-end">…right-aligned…</span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div><h1>Sale orders <span class="sf-count">{{ total }}</span></h1><div class="sf-sub">one-line description</div></div>
      <div class="sf-filters"> status chips: <button class="sf-chip" [class.on]="…">…</button> </div>
    </div>
    <div class="sf-grid sf-tiles"> <div class="sf-tile"><span class="l">Open</span><span class="v">12</span><span class="s">…</span></div> </div>  <!-- only if the page already has KPI/stat cards -->
    <div class="sf-filters"> search (class="sf-search"), dropdown filters, date pickers </div>
    <div class="sf-card p0"> <p-table …> (first column = record number as a link, status = p-tag or sf-pill) </p-table> </div>
  </div>
</sf-page>
```

### 2. Object page (detail of a document/record)
```html
<sf-page [crumbs]="['Finance', 'Sales invoices', inv.invoiceNumber]">
  <div sfCmd class="sf-cmd"> next-step action solid first; other actions [text]="true"; destructive ones severity="danger" [text]="true" after <span class="sf-sep"></span> </div>
  <header class="sf-hdr">
    <div class="sf-t"><div>
      <div class="sf-kicker">Document type · for <a>SO-…</a></div>
      <h1><span class="sf-mono">NUMBER</span> <p-tag …status…></p-tag></h1>
      <div class="sf-meta">Party · key dates</div>
    </div></div>
    <sf-stages [stages]="stages"></sf-stages>          <!-- when the document has a lifecycle; getter uses flowStagesFrom() -->
    <div class="sf-facts"> 2–4 <div class="sf-kpi"><span class="l">Total</span><span class="v">…</span><span class="s">…</span></div> </div>
  </header>
  <sf-anchors [sections]="sections"></sf-anchors>     <!-- when 3+ sections; each section: id="sec-…" class="sf-section …" -->
  <div class="sf-body">
    <section id="sec-lines" class="sf-section sf-card p0"><div class="sf-ch"><h2>Lines</h2>…</div><p-table>…</p-table></section>
    <section id="sec-info" class="sf-section sf-card"><div class="sf-ch"><h2>Details</h2></div><div class="sf-kv"><span>Label</span><span>Value</span>…</div></section>
  </div>
  <sf-panel #panel [tabs]="['Activity', 'Files']">   <!-- secondary info: timeline/approval history, attachments, related docs, summary -->
    <ng-container *ngIf="panel.active() === 'Activity'"> … </ng-container>
  </sf-panel>
</sf-page>
<!-- p-dialogs stay outside <sf-page>, unchanged -->
```
Values in `.sf-kv` / `.sf-fields`: label then value, alternating children.

### 3. Create / edit form
`<sf-page [crumbs]="[area, list, 'New …' | number]">`, a compact header `<header class="sf-hdr sf-compact"><h1>New purchase order</h1>…</header>`,
`.sf-body` with `.sf-card` sections (`.sf-ch` + `<div class="sf-fields">` grid of `<div class="sf-fld"><label class="…">Label</label> control</div>`),
line tables in `.sf-card.p0`, and the form buttons in a sticky footer `<div class="sf-fbar">hint … <span class="sf-end">Cancel ([text]) · Save draft · Submit (solid, last)</span></div>` at the end of `.sf-page-main` content (inside sf-page, after .sf-body).

### 4. Settings / admin / master data
List report pattern (title + table) or a compact header + cards; dialogs unchanged.

### 5. Dashboards / reports
Command bar (Refresh, Export…), `.sf-lr-head` title + period chips, `.sf-grid.sf-tiles` KPI tiles, charts and tables in `.sf-card`.

### 6. Sub-components (embedded panels, tabs, line editors — not routed pages)
No `<sf-page>`. Use `.sf-card`, `.sf-ch`, `.sf-kv`, `.sf-fields`, `.sf-pill` where they replace bespoke cards; keep them compact.

## Helpers
- `flowStagesFrom(labels, currentIndex, { subs, failed })` → stages (index = labels.length means all done).
- `flowTone(status)` → `'' | ok | wn | er | in | vi | te`, for `<span class="sf-pill" [ngClass]="flowTone(s)">`.
- `flowLabel('PARTIALLY_PAID')` → `Partially paid`.

## Non-negotiable
- Keep every binding, event, `*ngIf/*ngFor`, `#ref`, form control, `data-testid`, route and permission check. Only presentation changes.
- Keep `p-button`s as `p-button` and status `p-tag`s as `p-tag` where tests or code use them.
- Tests read `textContent` of `[data-testid]` elements. When you split an element (e.g. value + note into a KPI), keep the
  testid on the element that still contains ALL the text the spec expects (often the wrapper), and don't duplicate a testid
  (querySelector returns the first). Read the page's `.spec.ts` before restructuring it.
- Old page SCSS that is no longer referenced (page-header, hero, gradients) may be deleted; anything still used stays (tokens only).
- Tokens only, no hex. Dialogs keep the global `sdlg` look.
