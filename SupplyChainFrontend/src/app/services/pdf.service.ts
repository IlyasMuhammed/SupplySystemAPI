import { Injectable } from '@angular/core';
import jsPDF from 'jspdf';
import autoTable from 'jspdf-autotable';

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
