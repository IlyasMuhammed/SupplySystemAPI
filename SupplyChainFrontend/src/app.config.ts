import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, isDevMode } from '@angular/core';
import { provideServiceWorker } from '@angular/service-worker';
import { DATE_PIPE_DEFAULT_TIMEZONE } from '@angular/common';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideRouter, withEnabledBlockingInitialNavigation, withInMemoryScrolling } from '@angular/router';
import { providePrimeNG } from 'primeng/config';
import { SmsFlow } from './app/theme/sms-flow.preset';
import { appRoutes } from './app.routes';
import { authInterceptor } from './app/pages/interceptors/auth.interceptor';
import { tokenRefreshInterceptor } from './app/pages/interceptors/token-refresh.interceptor';
import { moduleErrorInterceptor } from './app/pages/interceptors/module-error.interceptor';
import { MessageService } from 'primeng/api';

export const appConfig: ApplicationConfig = {
    providers: [
        provideRouter(appRoutes, withInMemoryScrolling({ anchorScrolling: 'enabled', scrollPositionRestoration: 'enabled' }), withEnabledBlockingInitialNavigation()),
        provideHttpClient(withFetch(), withInterceptors([authInterceptor, tokenRefreshInterceptor, moduleErrorInterceptor])),
        // A37 — root instance for global notices (the shell's <p-toast key="sms-global">, e.g. module 403s).
        // Pages keep providing their own MessageService for their own toasts.
        MessageService,
        provideAnimationsAsync(),
        providePrimeNG({ theme: { preset: SmsFlow, options: { darkModeSelector: '.app-dark' } } }),
        // A38 — app shell cache for the offline POS (ngsw-config.json). Production builds only; API data lives in Dexie.
        provideServiceWorker('ngsw-worker.js', { enabled: !isDevMode(), registrationStrategy: 'registerWhenStable:30000' }),
        { provide: DATE_PIPE_DEFAULT_TIMEZONE, useValue: '+0500' }
    ]
};
