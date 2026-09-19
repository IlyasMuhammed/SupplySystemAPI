import { Routes } from '@angular/router';
import { AppLayout } from './app/layout/component/app.layout';
import { Dashboard } from './app/pages/dashboard/dashboard';
import { Notfound } from './app/pages/notfound/notfound';
import { SignUp } from './app/pages/auth/SignUp';
import { ActivationComponent } from './app/pages/auth/activation';
import { ForgotPassword } from './app/pages/auth/Forgot';
import { authGuard } from './app/pages/gaurds/auth.guard';
import { noAuthGuard } from './app/pages/gaurds/no-auth.guard';
import { AccessDeniedComponent } from './app/pages/access-denied/access-denied.component';
import { RfqPageComponent } from './app/pages/supplier-portal/rfq-page/rfq-page.component';
import { SroAckComponent } from './app/pages/supplier-portal/sro-ack/sro-ack.component';
import { TrackDeliveryComponent } from './app/pages/track-delivery/track-delivery.component';

export const appRoutes: Routes = [
    {
        path: 'portal',
        component: AppLayout,
        canActivate: [authGuard],
        children: [
            { path: 'dashboard', component: Dashboard },
            { path: 'access-denied', component: AccessDeniedComponent },
            { path: 'uikit', loadChildren: () => import('./app/pages/uikit/uikit.routes') },
            { path: 'pages', loadChildren: () => import('./app/pages/pages.routes') },
            { path: '', redirectTo: 'dashboard', pathMatch: 'full' }
        ]
    },
    // T-62 — a consignee's view of their own delivery. No guard, no layout shell: there is nobody
    // logged in. The path matches what PublicTrackingService.PathFor issues, so the link the back
    // end hands out is the link that works.
    { path: 'track/:token', component: TrackDeliveryComponent },
    { path: 'supplier-portal/rfq/:token', component: RfqPageComponent },
    { path: 'supplier-portal/sro-ack/:token', component: SroAckComponent },
    { path: 'activation/:token', component: ActivationComponent },
    { path: 'sign-up', component: SignUp, canActivate: [noAuthGuard] },
    { path: 'forgot-password', component: ForgotPassword, canActivate: [noAuthGuard] },
    { path: '', redirectTo: 'auth/login', pathMatch: 'full' },
    { path: 'notfound', component: Notfound },
    { path: 'auth', loadChildren: () => import('./app/pages/auth/auth.routes'), canActivate: [noAuthGuard] },
    { path: '**', redirectTo: '/notfound' }
];
