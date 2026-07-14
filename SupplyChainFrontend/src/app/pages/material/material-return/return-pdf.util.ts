import jsPDF from 'jspdf';
import autoTable from 'jspdf-autotable';
import { ReturnDetail } from '../../../services/material.service';

type RGB = [number, number, number];

const PURPLE:     RGB = [124,  58, 237];
const DARK_PUR:   RGB = [ 30,  27,  75];
const GRAY:       RGB = [100, 116, 139];
const LIGHT_GRAY: RGB = [226, 232, 240];
const DARK:       RGB = [ 30,  41,  59];
const WHITE:      RGB = [255, 255, 255];
const GREEN_BG:   RGB = [220, 252, 231];
const GREEN_FG:   RGB = [ 21, 128,  61];
const RED_BG:     RGB = [254, 226, 226];
const RED_FG:     RGB = [185,  28,  28];
const LAVENDER:   RGB = [250, 245, 255];
const FOOT_BG:    RGB = [237, 233, 254];

function fmtNum(n: number, d = 2): string {
  return n.toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
}

function fmtDate(s?: string | null): string {
  if (!s) return '—';
  return new Date(s).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
}

function fmtDateTime(s?: string | null): string {
  if (!s) return '—';
  return new Date(s).toLocaleString('en-GB', {
    day: '2-digit', month: 'short', year: 'numeric',
    hour: '2-digit', minute: '2-digit'
  });
}

export function downloadReturnPdf(ret: ReturnDetail): void {
  const doc   = new jsPDF({ orientation: 'p', unit: 'mm', format: 'a4' });
  const pw    = 210;
  const pl    = 14;
  const pr    = pw - pl;
  const cw    = pr - pl;

  let y = 14;

  // ── Header ─────────────────────────────────────────────────────────────────
  doc.setFont('helvetica', 'bold');
  doc.setFontSize(17);
  doc.setTextColor(...PURPLE);
  doc.text('Supply Management System', pl, y + 7);

  doc.setFont('helvetica', 'normal');
  doc.setFontSize(8.5);
  doc.setTextColor(...GRAY);
  doc.text('Stores & Inventory Division', pl, y + 13);

  doc.setFont('helvetica', 'normal');
  doc.setFontSize(8);
  doc.setTextColor(...GRAY);
  doc.text('MATERIAL RETURN VOUCHER', pr, y + 5, { align: 'right' });

  doc.setFont('helvetica', 'bold');
  doc.setFontSize(17);
  doc.setTextColor(...DARK_PUR);
  doc.text(ret.returnNo, pr, y + 13, { align: 'right' });

  // Status pill
  const pillW = 26;
  const pillH = 5.5;
  const pillX = pr - pillW;
  const pillY = y + 15;
  doc.setFillColor(...GREEN_BG);
  doc.roundedRect(pillX, pillY, pillW, pillH, 1.2, 1.2, 'F');
  doc.setFont('helvetica', 'bold');
  doc.setFontSize(7);
  doc.setTextColor(...GREEN_FG);
  doc.text(ret.status, pillX + pillW / 2, pillY + 3.7, { align: 'center' });

  // Divider
  y += 24;
  doc.setFillColor(...PURPLE);
  doc.rect(pl, y, cw, 0.7, 'F');
  y += 5;

  // ── Meta grid (2 rows × 4 cols) ────────────────────────────────────────────
  const totalValue = ret.lines.reduce((s, l) => s + l.lineValue, 0);
  const meta: [string, string][] = [
    ['Issue Voucher', ret.issueNo],
    ['Return Date',   fmtDate(ret.returnDate)],
    ['Total Value',   fmtNum(totalValue) + ' PKR'],
    ['Posted On',     fmtDateTime(ret.postedDate)],
    ['Created',       fmtDateTime(ret.createdDate)],
    ['Lines',         String(ret.lines.length)],
    ['Notes',         ret.notes || '—'],
    ['', ''],
  ];

  const cellW  = cw / 4;
  const cellH  = 15;
  const nCols  = 4;
  const nRows  = Math.ceil(meta.length / nCols);

  for (let i = 0; i < meta.length; i++) {
    const col = i % nCols;
    const row = Math.floor(i / nCols);
    const cx  = pl + col * cellW;
    const cy  = y  + row * cellH;

    doc.setFillColor(...LAVENDER);
    doc.rect(cx + 0.3, cy + 0.3, cellW - 0.6, cellH - 0.6, 'F');

    if (meta[i][0]) {
      doc.setFont('helvetica', 'bold');
      doc.setFontSize(6.5);
      doc.setTextColor(...PURPLE);
      doc.text(meta[i][0].toUpperCase(), cx + 3, cy + 5.5);

      doc.setFont('helvetica', 'bold');
      doc.setFontSize(9);
      doc.setTextColor(...DARK);
      doc.text(meta[i][1], cx + 3, cy + 11.5, { maxWidth: cellW - 6 });
    }
  }

  y += nRows * cellH + 7;

  // ── Section title ──────────────────────────────────────────────────────────
  doc.setFont('helvetica', 'bold');
  doc.setFontSize(7.5);
  doc.setTextColor(...PURPLE);
  doc.text('RETURN LINES', pl, y);
  y += 4;

  // ── Lines table ────────────────────────────────────────────────────────────
  const head = [['#', 'Item Description', 'UOM', 'Condition', 'Returned Qty', 'Unit Cost', 'Line Value', 'Reason']];
  const body = ret.lines.map((l, i) => [
    String(i + 1),
    l.itemDescription,
    l.unitOfMeasure ?? '—',
    l.condition,
    fmtNum(l.returnedQty, 4),
    fmtNum(l.unitCost,    4),
    fmtNum(l.lineValue,   2),
    l.reason ?? ''
  ]);

  let tableEndY = y;

  autoTable(doc, {
    startY: y,
    head,
    body,
    foot: [['', '', '', '', '', 'TOTAL', fmtNum(totalValue), '']],
    margin: { left: pl, right: pl },
    styles: {
      fontSize: 8,
      cellPadding: 2.5,
      textColor: DARK,
      overflow: 'linebreak'
    },
    headStyles: {
      fillColor: PURPLE,
      textColor: WHITE,
      fontStyle: 'bold',
      fontSize: 7.5
    },
    footStyles: {
      fillColor: FOOT_BG,
      textColor: DARK,
      fontStyle: 'bold',
      fontSize: 9
    },
    alternateRowStyles: { fillColor: LAVENDER },
    columnStyles: {
      0: { cellWidth:  8, halign: 'center' },
      2: { cellWidth: 13, halign: 'center' },
      3: { cellWidth: 23, halign: 'center' },
      4: { cellWidth: 25, halign: 'right'  },
      5: { cellWidth: 23, halign: 'right'  },
      6: { cellWidth: 25, halign: 'right'  },
      7: { cellWidth: 24 },
    },
    didParseCell: (data) => {
      if (data.section === 'body' && data.column.index === 3) {
        const cond = data.cell.raw as string;
        data.cell.styles.fontStyle  = 'bold';
        data.cell.styles.halign     = 'center';
        data.cell.styles.fillColor  = cond === 'GOOD' ? GREEN_BG : RED_BG;
        data.cell.styles.textColor  = cond === 'GOOD' ? GREEN_FG : RED_FG;
      }
    },
    didDrawPage: (data) => {
      tableEndY = data.cursor?.y ?? tableEndY;
    }
  });

  tableEndY = (doc as any).lastAutoTable?.finalY ?? tableEndY;

  // ── Signature section ──────────────────────────────────────────────────────
  const sigY   = tableEndY + 20;
  const sigCW  = cw / 3;
  const labels = ['Returned By', 'Received By / Storekeeper', 'Approved By'];

  labels.forEach((label, i) => {
    const sx = pl + i * sigCW;
    doc.setDrawColor(...LIGHT_GRAY);
    doc.setLineWidth(0.4);
    doc.line(sx, sigY, sx + sigCW - 8, sigY);
    doc.setFont('helvetica', 'bold');
    doc.setFontSize(7.5);
    doc.setTextColor(...GRAY);
    doc.text(label.toUpperCase(), sx, sigY + 5);
  });

  // ── Page footer ────────────────────────────────────────────────────────────
  const pageH = 297;
  doc.setDrawColor(...LIGHT_GRAY);
  doc.setLineWidth(0.3);
  doc.line(pl, pageH - 12, pr, pageH - 12);
  doc.setFont('helvetica', 'normal');
  doc.setFontSize(7);
  doc.setTextColor(...GRAY);
  doc.text(
    `Generated: ${fmtDateTime(new Date().toISOString())}  •  ${ret.returnNo}  •  Supply Management System`,
    pw / 2, pageH - 7, { align: 'center' }
  );

  doc.save(`${ret.returnNo}.pdf`);
}
