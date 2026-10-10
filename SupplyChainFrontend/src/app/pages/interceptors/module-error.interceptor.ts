import { DatePipe } from '@angular/common';
import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { MessageService } from 'primeng/api';
import { catchError, throwError } from 'rxjs';
import { ModuleService } from '../../services/module.service';
import { MODULE_NOT_LICENSED, moduleName } from '../../services/module.models';

/** The toast key the shell (AppLayout) renders global notices with. */
export const GLOBAL_TOAST_KEY = 'sms-global';

/** One toast per module per window — a page firing five calls at a switched-off module shows one notice, not five. */
const DEDUPE_MS = 5000;
const lastShown = new Map<string, number>();

/** Test hook: forget which modules were already announced. */
export function resetModuleErrorNotices(): void {
  lastShown.clear();
}

/**
 * A37 §1.4 — a 403 with errorCode MODULE_NOT_LICENSED means the organization switched the module (or feature) off.
 * Shows one clear, global toast for it (with the grace end when there is one), reloads the enabled modules so the menu
 * and *smsIfModule catch up, and passes the error on unchanged — pages keep their own error handling.
 */
export const moduleErrorInterceptor: HttpInterceptorFn = (req, next) => {
  const messages = inject(MessageService, { optional: true });
  const modules = inject(ModuleService, { optional: true });

  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 403 && err.error?.errorCode === MODULE_NOT_LICENSED) {
        const code: string = err.error?.result?.module ?? '';
        const now = Date.now();
        if (now - (lastShown.get(code) ?? 0) > DEDUPE_MS) {
          lastShown.set(code, now);
          messages?.add({
            key: GLOBAL_TOAST_KEY,
            severity: 'warn',
            summary: 'Module switched off',
            detail: moduleNotice(code, err.error?.message, err.error?.result?.graceEndsAt),
            life: 8000
          });
          modules?.refresh();
        }
      }
      return throwError(() => err);
    })
  );
};

/** "The Manufacturing module is switched off for your organization. …" */
export function moduleNotice(code: string, serverMessage?: string | null, graceEndsAt?: string | null): string {
  const name = code ? moduleName(code) : (/^The (.+?) module /.exec(serverMessage ?? '')?.[1] ?? 'requested');
  const head = `The ${name} module is switched off for your organization.`;
  if (graceEndsAt) {
    const until = new DatePipe('en-US').transform(graceEndsAt, 'd MMM y') ?? graceEndsAt;
    return `${head} Existing records can still be finished until ${until}; nothing new can be created.`;
  }
  return `${head} Ask an administrator to switch it on in Settings › Modules.`;
}
