import { Injectable } from '@angular/core';
import jsPDF from 'jspdf';
import autoTable from 'jspdf-autotable';
import { InvoiceDetailModel } from './finance.service';

type RGB = [number, number, number];

export interface ReportPdfConfig {
  title: string;
  subtitle?: string;
  fileName: string;
  columns: string[];
  rows: (string | number | null)[][];
  totalsRow?: (string | number | null)[];
  dateFilter?: { from?: string; to?: string };
  accentColor?: RGB;
}

@Injectable({ providedIn: 'root' })
export class PdfService {

  private fmt(d: string | null | undefined): string {
    if (!d) return '—';
    try {
      return new Date(d).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
    } catch { return d; }
  }

  private num(n: number | null | undefined, dec = 2): string {
    if (n == null) return '—';
    return n.toLocaleString('en-US', { minimumFractionDigits: dec, maximumFractionDigits: dec });
  }

  private statusColor(text: string): RGB {
    const map: Record<string, RGB> = {
      Approved:    [16, 185, 129], Matched:  [16, 185, 129], Paid: [16, 185, 129], Delivered: [16, 185, 129],
      Rejected:    [239, 68, 68],  Overdue:  [239, 68, 68],  Returned: [239, 68, 68],
      Variance:    [245, 158, 11], Partial:  [245, 158, 11], Dispatched: [245, 158, 11],
      'In Transit':[59, 130, 246], Scheduled:[59, 130, 246],
    };
    return map[text] ?? [100, 116, 139];
  }

  private drawBadge(doc: jsPDF, text: string, x: number, y: number): void {
    const c = this.statusColor(text);
    doc.setFontSize(7.5);
    doc.setFont('helvetica', 'bold');
    const tw = doc.getTextWidth(text);
    doc.setFillColor(c[0], c[1], c[2]);
    doc.roundedRect(x, y - 3.5, tw + 5, 5.5, 1.5, 1.5, 'F');
    doc.setTextColor(255, 255, 255);
    doc.text(text, x + 2.5, y);
  }

  // ─────────────────────────────────────────────────────────────────────────────
  // Invoice PDF  — professional A4 layout (v4 — purple accent, bill-to panel)
  // ─────────────────────────────────────────────────────────────────────────────

  downloadInvoice(inv: InvoiceDetailModel): void {
    const doc = new jsPDF({ orientation: 'portrait', unit: 'mm', format: 'a4' });
    const W = 210, M = 15, pH = 297;
    let y = 0;

    // ── Palette ───────────────────────────────────────────────────────────────
    const INK:    RGB = [15,  23,  42];
    const PUR:    RGB = [108, 99, 255];   // #6c63ff — app primary
    const PUR2:   RGB = [76,  29, 149];   // darker purple for contrast text
    const MUTED:  RGB = [100, 116, 139];
    const PALE:   RGB = [245, 243, 255];  // light purple tint
    const LIGHT:  RGB = [237, 233, 254];
    const BORDER: RGB = [221, 214, 254];
    const WHITE:  RGB = [255, 255, 255];

    const tx = (size: number, style: 'normal' | 'bold', c: RGB) => {
      doc.setFontSize(size);
      doc.setFont('helvetica', style);
      doc.setTextColor(c[0], c[1], c[2]);
    };

    const secHead = (label: string, at: number): number => {
      doc.setFillColor(PUR[0], PUR[1], PUR[2]);
      doc.rect(M, at, 3.5, 5.5, 'F');
      tx(8, 'bold', INK);
      doc.text(label, M + 7, at + 4.2);
      return at + 10;
    };

    // ══════════════════════════════════════════════════════════════════════════
    // ZONE A — HEADER BAND
    // ══════════════════════════════════════════════════════════════════════════

    // Deep navy background
    doc.setFillColor(INK[0], INK[1], INK[2]);
    doc.rect(0, 0, W, 52, 'F');

    // Purple accent left stripe
    doc.setFillColor(PUR[0], PUR[1], PUR[2]);
    doc.rect(0, 0, 5, 52, 'F');

    // Logo badge (purple gradient simulation — solid fill)
    doc.setFillColor(PUR[0], PUR[1], PUR[2]);
    doc.roundedRect(M, 10, 26, 26, 4, 4, 'F');
    tx(12, 'bold', WHITE);
    doc.text('SMS', M + 13, 25.5, { align: 'center' });

    // Company name
    tx(10, 'bold', WHITE);
    doc.text('SUPPLY MANAGEMENT SYSTEM', M + 33, 18);
    tx(7, 'normal', [148, 163, 184] as RGB);
    doc.text('Supply Chain Procurement Platform', M + 33, 25.5);

    // Invoice number (top-right)
    tx(8, 'normal', [148, 163, 184] as RGB);
    doc.text('INVOICE NO.', W - M, 14, { align: 'right' });
    tx(14, 'bold', WHITE);
    doc.text(inv.invoiceNumber, W - M, 23, { align: 'right' });

    // Status badges row
    let badgeX = W - M;
    [
      { text: inv.paymentStatus, c: this.statusColor(inv.paymentStatus) },
      { text: inv.matchStatus,   c: this.statusColor(inv.matchStatus)   },
    ].forEach(b => {
      doc.setFontSize(7);
      doc.setFont('helvetica', 'bold');
      const bw = doc.getTextWidth(b.text) + 7;
      badgeX -= bw;
      doc.setFillColor(b.c[0], b.c[1], b.c[2]);
      doc.roundedRect(badgeX, 30, bw, 6, 1.5, 1.5, 'F');
      doc.setTextColor(255, 255, 255);
      doc.text(b.text, badgeX + bw / 2, 34.2, { align: 'center' });
      badgeX -= 3;
    });

    // "INVOICE" watermark word — right-side, light
    tx(36, 'bold', [30, 41, 59] as RGB);
    doc.text('INVOICE', W - M, 47, { align: 'right' });

    // Purple accent bottom border of header
    doc.setFillColor(PUR[0], PUR[1], PUR[2]);
    doc.rect(0, 52, W, 2, 'F');

    y = 62;

    // ══════════════════════════════════════════════════════════════════════════
    // ZONE B — BILL FROM / BILL TO / INVOICE DETAILS  (3-column)
    // ══════════════════════════════════════════════════════════════════════════

    const detailRows: [string, string][] = [
      ['Invoice Date',  this.fmt(inv.invoiceDate)],
      ['Received Date', this.fmt(inv.receivedDate)],
      ['Due Date',      this.fmt(inv.dueDate)],
      ['PO Reference',  inv.poNumber],
    ];
    if (inv.supplierInvoiceNo) detailRows.push(['Supplier Ref', inv.supplierInvoiceNo]);
    if (inv.grnNumber)         detailRows.push(['GRN Ref',      inv.grnNumber]);
    if (inv.paymentMethod)     detailRows.push(['Payment',      inv.paymentMethod]);

    const detailRowH = 7;
    const rightInnerH = detailRows.length * detailRowH;

    doc.setFontSize(11);
    doc.setFont('helvetica', 'bold');
    const fromLines = doc.splitTextToSize(inv.supplierName, 50) as string[];
    const toLines   = doc.splitTextToSize('Supply Management System', 50) as string[];
    const partyH    = Math.max(fromLines.length, toLines.length) * 6 + 8;
    const innerPad  = 8;
    const panelH    = Math.max(Math.max(partyH, 32), rightInnerH) + innerPad * 2;

    const colW  = 56;
    const col2X = M + colW + 5;
    const col3X = col2X + colW + 5;
    const col3W = W - M - col3X;

    // ── From panel ────────────────────────────────────────────────────────────
    doc.setFillColor(PALE[0], PALE[1], PALE[2]);
    doc.setDrawColor(BORDER[0], BORDER[1], BORDER[2]);
    doc.setLineWidth(0.3);
    doc.roundedRect(M, y, colW, panelH, 3, 3, 'FD');

    doc.setFillColor(PUR[0], PUR[1], PUR[2]);
    doc.roundedRect(M, y, colW, 7.5, 3, 3, 'F');
    doc.rect(M, y + 4, colW, 3.5, 'F');
    tx(6, 'bold', WHITE);
    doc.text('BILLED FROM', M + 5, y + 5.5);

    let ly = y + innerPad + 5;
    tx(9, 'bold', PUR2);
    doc.text(fromLines, M + 5, ly);
    ly += fromLines.length * 6 + 2;
    tx(7, 'normal', MUTED);
    doc.text('Vendor / Supplier', M + 5, ly);

    // ── Bill To panel ─────────────────────────────────────────────────────────
    doc.setFillColor(PALE[0], PALE[1], PALE[2]);
    doc.setDrawColor(BORDER[0], BORDER[1], BORDER[2]);
    doc.setLineWidth(0.3);
    doc.roundedRect(col2X, y, colW, panelH, 3, 3, 'FD');

    doc.setFillColor(INK[0], INK[1], INK[2]);
    doc.roundedRect(col2X, y, colW, 7.5, 3, 3, 'F');
    doc.rect(col2X, y + 4, colW, 3.5, 'F');
    tx(6, 'bold', WHITE);
    doc.text('BILLED TO', col2X + 5, y + 5.5);

    let ty = y + innerPad + 5;
    tx(9, 'bold', INK);
    doc.text(toLines, col2X + 5, ty);
    ty += toLines.length * 6 + 2;
    tx(7, 'normal', MUTED);
    doc.text('Procurement Department', col2X + 5, ty);

    // ── Details panel ─────────────────────────────────────────────────────────
    doc.setFillColor(WHITE[0], WHITE[1], WHITE[2]);
    doc.setDrawColor(BORDER[0], BORDER[1], BORDER[2]);
    doc.setLineWidth(0.3);
    doc.roundedRect(col3X, y, col3W, panelH, 3, 3, 'FD');

    doc.setFillColor(PUR[0], PUR[1], PUR[2]);
    doc.roundedRect(col3X, y, col3W, 7.5, 3, 3, 'F');
    doc.rect(col3X, y + 4, col3W, 3.5, 'F');
    tx(6, 'bold', WHITE);
    doc.text('INVOICE DETAILS', col3X + 5, y + 5.5);

    let rowy = y + innerPad + 5;
    detailRows.forEach(([lbl, val], i) => {
      if (i % 2 === 1) {
        doc.setFillColor(PALE[0], PALE[1], PALE[2]);
        doc.rect(col3X + 1, rowy - 5, col3W - 2, detailRowH, 'F');
      }
      tx(6.5, 'normal', MUTED);
      doc.text(lbl, col3X + 5, rowy);
      tx(7.5, 'bold', INK);
      doc.text(val, col3X + col3W - 5, rowy, { align: 'right' });
      rowy += detailRowH;
    });

    y += panelH + 10;

    // ══════════════════════════════════════════════════════════════════════════
    // ZONE C — FINANCIAL SNAPSHOT STRIP
    // ══════════════════════════════════════════════════════════════════════════

    const stripH = 22;
    doc.setFillColor(INK[0], INK[1], INK[2]);
    doc.roundedRect(M, y, W - M * 2, stripH, 3, 3, 'F');

    // Purple accent top edge
    doc.setFillColor(PUR[0], PUR[1], PUR[2]);
    doc.rect(M, y, W - M * 2, 1.5, 'F');

    const tiles = [
      { lbl: 'SUBTOTAL',     val: `${this.num(inv.subtotal)} ${inv.currency}`,    hi: false },
      { lbl: 'TAX',          val: `${this.num(inv.taxAmount)} ${inv.currency}`,   hi: false },
      { lbl: 'AMOUNT DUE',   val: `${this.num(inv.totalAmount)} ${inv.currency}`, hi: true  },
    ];
    const tileW = (W - M * 2) / 3;
    tiles.forEach((tile, i) => {
      const cx = M + i * tileW + tileW / 2;
      if (i > 0) {
        doc.setDrawColor(40, 58, 82);
        doc.setLineWidth(0.3);
        doc.line(M + i * tileW, y + 5, M + i * tileW, y + stripH - 4);
      }
      tx(6, 'normal', [148, 163, 184] as RGB);
      doc.text(tile.lbl, cx, y + 9.5, { align: 'center' });
      tx(tile.hi ? 11.5 : 10, 'bold', tile.hi ? [167, 139, 250] as RGB : WHITE);
      doc.text(tile.val, cx, y + 19, { align: 'center' });
    });

    y += stripH + 10;

    // ══════════════════════════════════════════════════════════════════════════
    // ZONE D — LINE ITEMS
    // ══════════════════════════════════════════════════════════════════════════

    if (inv.lines?.length) {
      y = secHead('LINE ITEMS', y);

      autoTable(doc, {
        startY: y,
        margin: { left: M, right: M },
        head: [['#', 'Item Description', 'Unit', 'Qty', 'Unit Price', `Amount (${inv.currency})`]],
        body: inv.lines.map(l => [
          l.lineNo, l.itemDescription, l.unitOfMeasure ?? '—',
          l.qtyInvoiced.toLocaleString(), this.num(l.unitPrice), this.num(l.lineTotal),
        ]),
        foot: [['', 'Lines Subtotal', '', '', '', this.num(inv.subtotal)]],
        headStyles: {
          fillColor: PUR, textColor: WHITE, fontSize: 8, fontStyle: 'bold',
          cellPadding: { top: 4, right: 3.5, bottom: 4, left: 3.5 },
        },
        bodyStyles: { fontSize: 8, textColor: INK, cellPadding: { top: 3, right: 3.5, bottom: 3, left: 3.5 } },
        footStyles: { fillColor: LIGHT as RGB, textColor: PUR2, fontStyle: 'bold', fontSize: 8 },
        alternateRowStyles: { fillColor: PALE },
        columnStyles: {
          0: { cellWidth: 10, halign: 'center' },
          2: { cellWidth: 16, halign: 'center' },
          3: { cellWidth: 18, halign: 'right'  },
          4: { cellWidth: 30, halign: 'right'  },
          5: { cellWidth: 38, halign: 'right'  },
        },
        tableLineColor: BORDER, tableLineWidth: 0.2,
      });

      y = (doc as any).lastAutoTable.finalY + 10;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ZONE E — FINANCIAL SUMMARY BOX (right-aligned)
    // ══════════════════════════════════════════════════════════════════════════

    if (y > 225) { doc.addPage(); y = 20; }

    const sumBoxW = 88;
    const sumBoxX = W - M - sumBoxW;
    const sumRowH = 9;

    type SumRow = { lbl: string; val: string; kind: 'plain' | 'total' | 'muted' | 'variance' };
    const sumRows: SumRow[] = [
      { lbl: 'Subtotal',     val: `${this.num(inv.subtotal)} ${inv.currency}`,    kind: 'plain' },
      { lbl: 'Tax Amount',   val: `${this.num(inv.taxAmount)} ${inv.currency}`,   kind: 'plain' },
      { lbl: 'AMOUNT DUE',  val: `${this.num(inv.totalAmount)} ${inv.currency}`, kind: 'total' },
    ];
    if (inv.matchedPoValue) {
      sumRows.push({ lbl: 'Matched PO Value', val: `${this.num(inv.matchedPoValue)} ${inv.currency}`, kind: 'muted' });
    }
    if (inv.varianceAmount != null && Math.abs(inv.varianceAmount) > 0.001) {
      sumRows.push({ lbl: 'Variance', val: `${this.num(inv.varianceAmount)} ${inv.currency}`, kind: 'variance' });
    }

    const sumBoxH = sumRows.length * sumRowH;

    doc.setDrawColor(BORDER[0], BORDER[1], BORDER[2]);
    doc.setLineWidth(0.35);
    doc.rect(sumBoxX, y, sumBoxW, sumBoxH);

    sumRows.forEach((row, i) => {
      const rowTop = y + i * sumRowH;
      const textY  = rowTop + sumRowH - 2.8;

      if (row.kind === 'total') {
        doc.setFillColor(PUR[0], PUR[1], PUR[2]);
      } else if (i % 2 === 0) {
        doc.setFillColor(PALE[0], PALE[1], PALE[2]);
      } else {
        doc.setFillColor(255, 255, 255);
      }
      doc.rect(sumBoxX, rowTop, sumBoxW, sumRowH, 'F');

      if (i < sumRows.length - 1 && row.kind !== 'total') {
        doc.setDrawColor(BORDER[0], BORDER[1], BORDER[2]);
        doc.setLineWidth(0.2);
        doc.line(sumBoxX, rowTop + sumRowH, sumBoxX + sumBoxW, rowTop + sumRowH);
      }

      const lc: RGB = row.kind === 'total' ? WHITE : MUTED;
      tx(row.kind === 'total' ? 9 : 8, row.kind === 'total' ? 'bold' : 'normal', lc);
      doc.text(row.lbl, sumBoxX + 5, textY);

      const vc: RGB = row.kind === 'total'    ? WHITE
                    : row.kind === 'variance' ? (inv.varianceAmount! > 0 ? [220, 38, 38] as RGB : [16, 185, 129] as RGB)
                    : row.kind === 'muted'    ? MUTED : INK;
      tx(row.kind === 'total' ? 9.5 : 8.5, 'bold', vc);
      doc.text(row.val, sumBoxX + sumBoxW - 5, textY, { align: 'right' });
    });

    y += sumBoxH + 10;

    // ══════════════════════════════════════════════════════════════════════════
    // ZONE F — PAYMENT HISTORY
    // ══════════════════════════════════════════════════════════════════════════

    if (inv.payments?.length) {
      if (y > 238) { doc.addPage(); y = 20; }
      y = secHead('PAYMENT HISTORY', y);

      autoTable(doc, {
        startY: y,
        margin: { left: M, right: M },
        head: [['Payment #', 'Date', 'Method', `Amount (${inv.currency})`, 'Status']],
        body: inv.payments.map(p => [
          p.paymentNumber, this.fmt(p.paymentDate), p.paymentMethod, this.num(p.amountPaid), p.status,
        ]),
        headStyles: {
          fillColor: INK, textColor: WHITE, fontSize: 8, fontStyle: 'bold',
          cellPadding: { top: 3.5, right: 3.5, bottom: 3.5, left: 3.5 },
        },
        bodyStyles: { fontSize: 8, textColor: INK, cellPadding: 3 },
        alternateRowStyles: { fillColor: PALE },
        tableLineColor: BORDER, tableLineWidth: 0.2,
      });

      y = (doc as any).lastAutoTable.finalY + 10;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ZONE G — NOTES
    // ══════════════════════════════════════════════════════════════════════════

    if (inv.notes) {
      if (y > 255) { doc.addPage(); y = 20; }
      y = secHead('NOTES', y);

      const noteLines = doc.splitTextToSize(inv.notes, W - M * 2 - 10) as string[];
      const shown     = noteLines.slice(0, 5);
      const noteH     = shown.length * 5 + 10;

      doc.setFillColor(245, 243, 255);
      doc.setDrawColor(BORDER[0], BORDER[1], BORDER[2]);
      doc.setLineWidth(0.3);
      doc.roundedRect(M, y, W - M * 2, noteH, 2, 2, 'FD');
      tx(8, 'normal', PUR2);
      doc.text(shown, M + 5, y + 7);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // FOOTER — every page
    // ══════════════════════════════════════════════════════════════════════════

    const genDate = new Date().toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
    const totalPages = doc.getNumberOfPages();
    for (let pg = 1; pg <= totalPages; pg++) {
      doc.setPage(pg);
      doc.setFillColor(INK[0], INK[1], INK[2]);
      doc.rect(0, pH - 12, W, 12, 'F');
      doc.setFillColor(PUR[0], PUR[1], PUR[2]);
      doc.rect(0, pH - 12, 5, 12, 'F');
      tx(7, 'normal', [148, 163, 184] as RGB);
      doc.text('Supply Management System  ·  Confidential Document', M, pH - 4.5);
      doc.text(`Generated: ${genDate}  |  Page ${pg} of ${totalPages}`, W - M, pH - 4.5, { align: 'right' });
    }

    doc.save(`${inv.invoiceNumber}.pdf`);
  }

  // ─────────────────────────────────────────────────────────────────────────────
  // Generic tabular report PDF
  // ─────────────────────────────────────────────────────────────────────────────

  downloadTableReport(cfg: ReportPdfConfig): void {
    const landscape = cfg.columns.length >= 7;
    const doc = new jsPDF({ orientation: landscape ? 'landscape' : 'portrait', unit: 'mm', format: 'a4' });
    const W = doc.internal.pageSize.getWidth();
    const pH = doc.internal.pageSize.getHeight();
    const M = 14;
    const accent: RGB = cfg.accentColor ?? [15, 23, 42];
    let y = M;

    // ── Header band ───────────────────────────────────────────────────────────
    doc.setFillColor(accent[0], accent[1], accent[2]);
    doc.rect(0, 0, W, 28, 'F');

    doc.setFontSize(7.5);
    doc.setFont('helvetica', 'normal');
    doc.setTextColor(200, 215, 228);
    doc.text('SUPPLY MANAGEMENT SYSTEM', M, 10);

    doc.setFontSize(15);
    doc.setFont('helvetica', 'bold');
    doc.setTextColor(255, 255, 255);
    doc.text(cfg.title, M, 21);

    const genDate = new Date().toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
    doc.setFontSize(7.5);
    doc.setFont('helvetica', 'normal');
    doc.setTextColor(200, 215, 228);
    doc.text(`Generated: ${genDate}`, W - M, 10, { align: 'right' });

    if (cfg.dateFilter?.from || cfg.dateFilter?.to) {
      const parts = [
        cfg.dateFilter.from && `From: ${cfg.dateFilter.from}`,
        cfg.dateFilter.to   && `To: ${cfg.dateFilter.to}`
      ].filter(Boolean) as string[];
      doc.text(parts.join('  ·  '), W - M, 21, { align: 'right' });
    }

    y = 36;

    if (cfg.subtitle) {
      doc.setFontSize(8.5);
      doc.setFont('helvetica', 'normal');
      doc.setTextColor(100, 116, 139);
      doc.text(cfg.subtitle, M, y);
      y += 6;
    }

    // ── Table ─────────────────────────────────────────────────────────────────
    autoTable(doc, {
      startY: y,
      margin: { left: M, right: M },
      head: [cfg.columns],
      body: cfg.rows.map(r => r.map(v => (v != null ? v.toString() : '—'))),
      foot: cfg.totalsRow ? [cfg.totalsRow.map(v => (v != null ? v.toString() : ''))] : undefined,
      headStyles: {
        fillColor: accent, textColor: [255, 255, 255] as RGB,
        fontSize: 8, fontStyle: 'bold', cellPadding: 3
      },
      bodyStyles: { fontSize: 7.5, textColor: [15, 23, 42] as RGB, cellPadding: 2.5 },
      footStyles: {
        fillColor: [241, 245, 249] as RGB, textColor: [15, 23, 42] as RGB,
        fontStyle: 'bold', fontSize: 8
      },
      alternateRowStyles: { fillColor: [248, 250, 252] as RGB },
      didDrawPage: (data: any) => {
        // Repeat thin header strip on continuation pages
        if (data.pageNumber > 1) {
          doc.setFillColor(accent[0], accent[1], accent[2]);
          doc.rect(0, 0, W, 8, 'F');
          doc.setFontSize(6);
          doc.setFont('helvetica', 'normal');
          doc.setTextColor(220, 230, 240);
          doc.text(`${cfg.title} — Supply Management System`, M, 5.5);
        }
        // Page footer
        doc.setFillColor(248, 250, 252);
        doc.rect(0, pH - 11, W, 11, 'F');
        doc.setFontSize(6.5);
        doc.setFont('helvetica', 'normal');
        doc.setTextColor(148, 163, 184);
        doc.text('Supply Management System — Confidential', M, pH - 4);
        doc.text(
          `${cfg.rows.length} records  |  Page ${data.pageNumber}`,
          W - M, pH - 4, { align: 'right' }
        );
      }
    });

    doc.save(`${cfg.fileName}.pdf`);
  }
}
