import jsPDF from 'jspdf';
import autoTable from 'jspdf-autotable';
import { MivDetail } from '../../../services/material.service';

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

export function downloadMivPdf(miv: MivDetail): void {
  const doc  = new jsPDF({ orientation: 'p', unit: 'mm', format: 'a4' });
  const pageW  = doc.internal.pageSize.getWidth();
  const pageH  = doc.internal.pageSize.getHeight();
  const margin = 14;
  const cW     = pageW - margin * 2;

  // ── Header bar ───────────────────────────────────────────────────────────────
  doc.setFillColor(108, 99, 255);
  doc.rect(0, 0, pageW, 24, 'F');

  doc.setTextColor(255, 255, 255);
  doc.setFontSize(15);
  doc.setFont('helvetica', 'bold');
  doc.text('Supply Management System', margin, 11);
  doc.setFontSize(8);
  doc.setFont('helvetica', 'normal');
  doc.text('Stores & Inventory Division', margin, 17);

  doc.setFontSize(17);
  doc.setFont('helvetica', 'bold');
  doc.text(miv.issueNo, pageW - margin, 11, { align: 'right' });
  doc.setFontSize(8);
  doc.setFont('helvetica', 'normal');
  doc.text('MATERIAL ISSUE VOUCHER', pageW - margin, 17, { align: 'right' });

  // ── Status badge ─────────────────────────────────────────────────────────────
  let y = 32;
  const statusColor: Record<string, [number, number, number]> = {
    POSTED:    [34, 197, 94],
    DRAFT:     [148, 163, 184],
    CANCELLED: [239, 68, 68],
  };
  const [sr, sg, sb] = statusColor[miv.status] ?? [148, 163, 184];
  doc.setFillColor(sr, sg, sb);
  doc.roundedRect(margin, y - 5, 24, 6, 1.5, 1.5, 'F');
  doc.setTextColor(255, 255, 255);
  doc.setFontSize(7);
  doc.setFont('helvetica', 'bold');
  doc.text(miv.status, margin + 12, y - 0.5, { align: 'center' });

  // ── Meta grid (4 cols × 2 rows) ──────────────────────────────────────────────
  y = 40;
  const colW = cW / 4;
  const metaItems = [
    { label: 'MIR Reference', value: miv.mirRequestNo },
    { label: 'Project',       value: miv.mirProjectName ?? '—' },
    { label: 'Issued To',     value: miv.issuedTo ?? '—' },
    { label: 'Issue Date',    value: fmtDate(miv.issueDate) },
    { label: 'Total Value',   value: fmtNum(miv.totalValue) },
    { label: 'Posted On',     value: fmtDateTime(miv.postedDate) },
    { label: 'Created',       value: fmtDateTime(miv.createdDate) },
    { label: 'Notes',         value: miv.notes ?? '—' },
  ];

  metaItems.forEach((item, idx) => {
    const col = idx % 4;
    const row = Math.floor(idx / 4);
    const cx  = margin + col * colW;
    const cy  = y + row * 15;

    doc.setFillColor(248, 247, 255);
    doc.rect(cx, cy, colW, 13, 'F');
    doc.setDrawColor(226, 232, 240);
    doc.rect(cx, cy, colW, 13, 'S');

    doc.setTextColor(124, 111, 205);
    doc.setFontSize(6.5);
    doc.setFont('helvetica', 'bold');
    doc.text(item.label.toUpperCase(), cx + 3, cy + 4.5);

    doc.setTextColor(30, 41, 59);
    doc.setFontSize(8);
    doc.setFont('helvetica', 'bold');
    const val = item.value.length > 28 ? item.value.slice(0, 25) + '...' : item.value;
    doc.text(val, cx + 3, cy + 10);
  });

  // ── Lines table ───────────────────────────────────────────────────────────────
  y = y + 2 * 15 + 6;
  doc.setTextColor(108, 99, 255);
  doc.setFontSize(8);
  doc.setFont('helvetica', 'bold');
  doc.text('ISSUE LINES', margin, y);
  y += 3;

  const rows = miv.lines.map((l, i) => [
    String(i + 1),
    l.itemDescription,
    l.unitOfMeasure ?? '—',
    l.warehouseName ?? '—',
    fmtNum(l.issuedQty, 4),
    fmtNum(l.unitCost, 4),
    fmtNum(l.lineValue, 2),
    l.notes ?? '',
  ]);

  autoTable(doc, {
    startY: y,
    margin: { left: margin, right: margin },
    head: [['#', 'Item Description', 'UOM', 'Warehouse', 'Issued Qty', 'Unit Cost', 'Line Value', 'Notes']],
    body: rows,
    foot: [['', '', '', '', '', 'TOTAL', fmtNum(miv.totalValue), '']],
    styles:          { fontSize: 8, cellPadding: 2.8 },
    headStyles:      { fillColor: [108, 99, 255], textColor: 255, fontStyle: 'bold', fontSize: 7.5 },
    footStyles:      { fillColor: [237, 233, 254], textColor: [30, 41, 59], fontStyle: 'bold' },
    alternateRowStyles: { fillColor: [248, 247, 255] },
    columnStyles: {
      0: { cellWidth: 8,  halign: 'center' },
      2: { cellWidth: 14, halign: 'center' },
      4: { cellWidth: 22, halign: 'right' },
      5: { cellWidth: 22, halign: 'right' },
      6: { cellWidth: 24, halign: 'right', fontStyle: 'bold' },
      7: { cellWidth: 26, textColor: [100, 116, 139], fontStyle: 'italic' },
    },
  });

  // ── Signatures ────────────────────────────────────────────────────────────────
  const afterTable = (doc as any).lastAutoTable.finalY + 18;
  const sigW = cW / 3;
  ['Prepared By', 'Issued By / Storekeeper', 'Received By'].forEach((lbl, i) => {
    const sx = margin + i * sigW;
    doc.setDrawColor(148, 163, 184);
    doc.line(sx, afterTable, sx + sigW - 8, afterTable);
    doc.setTextColor(71, 85, 105);
    doc.setFontSize(7.5);
    doc.setFont('helvetica', 'bold');
    doc.text(lbl.toUpperCase(), sx, afterTable + 5);
  });

  // ── Page footer ───────────────────────────────────────────────────────────────
  const fy = pageH - 8;
  doc.setDrawColor(226, 232, 240);
  doc.line(margin, fy - 3, pageW - margin, fy - 3);
  doc.setTextColor(148, 163, 184);
  doc.setFontSize(7);
  doc.setFont('helvetica', 'normal');
  doc.text(
    `Generated: ${fmtDateTime(new Date().toISOString())}  •  ${miv.issueNo}  •  Supply Management System`,
    pageW / 2, fy, { align: 'center' }
  );

  doc.save(`${miv.issueNo}.pdf`);
}
