import { Routes } from '@angular/router';
import { posCashierGuard, posInitGuard, posOpenShiftGuard, posPermissionGuard, posReturnsGuard } from './core/pos.guards';
import { POS_PERM } from './core/pos.models';
import { PosShellComponent } from './ui/pos-shell.component';
import { PosStartComponent } from './ui/pos-start.component';

/** A38 API-CONTRACT §11 — the full-screen POS PWA (outside the portal layout). */
export default [
    {
        path: '',
        component: PosShellComponent,
        canActivate: [posInitGuard],
        children: [
            { path: '', component: PosStartComponent, title: 'SMS POS' },
            {
                path: 'sell',
                canActivate: [posCashierGuard, posOpenShiftGuard],
                loadComponent: () => import('./ui/pos-sell.component').then(m => m.PosSellComponent),
                title: 'SMS POS · Sell'
            },
            {
                path: 'payment',
                canActivate: [posCashierGuard, posOpenShiftGuard],
                loadComponent: () => import('./ui/pos-payment.component').then(m => m.PosPaymentComponent),
                title: 'SMS POS · Payment'
            },
            {
                path: 'receipt/:localId',
                canActivate: [posCashierGuard],
                loadComponent: () => import('./ui/pos-receipt.component').then(m => m.PosReceiptComponent),
                title: 'SMS POS · Receipt'
            },
            {
                path: 'shift',
                canActivate: [posCashierGuard],
                loadComponent: () => import('./ui/pos-shift.component').then(m => m.PosShiftComponent),
                title: 'SMS POS · Shift'
            },
            {
                path: 'shift/close',
                canActivate: [posCashierGuard, posPermissionGuard],
                data: { posPermissions: [POS_PERM.SHIFT_CLOSE] },
                loadComponent: () => import('./ui/pos-shift-close.component').then(m => m.PosShiftCloseComponent),
                title: 'SMS POS · Close shift'
            },
            {
                path: 'history',
                canActivate: [posCashierGuard],
                loadComponent: () => import('./ui/pos-history.component').then(m => m.PosHistoryComponent),
                title: 'SMS POS · Orders'
            },
            // ── A39 §8 returns, exchanges, store credit ──
            {
                path: 'return',
                canActivate: [posCashierGuard, posOpenShiftGuard, posReturnsGuard],
                loadComponent: () => import('./ui/pos-return-lookup.component').then(m => m.PosReturnLookupComponent),
                title: 'SMS POS · Return'
            },
            {
                path: 'return/:orderUuid',
                canActivate: [posCashierGuard, posOpenShiftGuard, posReturnsGuard],
                loadComponent: () => import('./ui/pos-return-items.component').then(m => m.PosReturnItemsComponent),
                title: 'SMS POS · Return items'
            },
            {
                path: 'return/:orderUuid/refund',
                canActivate: [posCashierGuard, posOpenShiftGuard, posReturnsGuard],
                loadComponent: () => import('./ui/pos-return-refund.component').then(m => m.PosReturnRefundComponent),
                title: 'SMS POS · Refund'
            },
            {
                path: 'exchange/:orderUuid',
                canActivate: [posCashierGuard, posOpenShiftGuard, posReturnsGuard],
                loadComponent: () => import('./ui/pos-exchange.component').then(m => m.PosExchangeComponent),
                title: 'SMS POS · Exchange'
            },
            {
                path: 'return-receipt/:localId',
                canActivate: [posCashierGuard],
                loadComponent: () => import('./ui/pos-return-receipt.component').then(m => m.PosReturnReceiptComponent),
                title: 'SMS POS · Return receipt'
            },
            {
                path: 'credit-note/:uuid',
                canActivate: [posCashierGuard],
                loadComponent: () => import('./ui/pos-credit-note.component').then(m => m.PosCreditNoteComponent),
                title: 'SMS POS · Store credit'
            },
            {
                path: 'settings',
                canActivate: [posCashierGuard],
                loadComponent: () => import('./ui/pos-settings.component').then(m => m.PosSettingsComponent),
                title: 'SMS POS · Device'
            },
            { path: '**', redirectTo: '' }
        ]
    }
] satisfies Routes;
