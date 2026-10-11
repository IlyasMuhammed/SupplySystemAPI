import { Component, inject, input, output, signal } from '@angular/core';
import { DialogModule } from 'primeng/dialog';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { errorMessage } from '../core/pos-http.service';
import { FlowAuthorization, PosReturnFlow } from '../core/pos-return-flow.service';
import { MANAGER_REASON_TEXT } from '../core/pos-return-rules';
import { ManagerReason } from '../core/pos.models';
import { PinPadComponent } from './pin-pad.component';
import { PosMoneyPipe } from './pos-money.pipe';

/**
 * A39 D-8 — manager authorization at the till: why it is needed, then the manager's PIN on the A38 PIN pad.
 * Online the server issues a one-time token; offline the PIN is checked against the cached hashes.
 */
@Component({
    selector: 'pos-manager-auth',
    standalone: true,
    imports: [DialogModule, PinPadComponent, PosMoneyPipe],
    template: `
        <p-dialog [visible]="visible()" (onHide)="cancel()" [modal]="true" header="Manager authorization" [style]="{ width: 'min(420px, 95vw)' }">
            @if (visible()) {
                <div class="stack" data-testid="manager-auth">
                    <ul class="muted" style="margin: 0; padding-left: 1.1rem">
                        @for (r of reasons(); track r) { <li [attr.data-testid]="'manager-reason-' + r">{{ text[r] }}</li> }
                    </ul>
                    <p class="faint">Amount {{ flow.authAmount() | posMoney: currency() }} · {{ conn.online() ? 'checked by the server' : 'offline — checked on this device' }}</p>
                    <pos-pin-pad [busy]="busy()" [disabled]="busy()" submitLabel="Authorize" (entered)="submit($event)"></pos-pin-pad>
                    @if (error()) { <p class="err-text" data-testid="manager-error">{{ error() }}</p> }
                </div>
            }
        </p-dialog>
    `
})
export class PosManagerAuthComponent {
    readonly flow = inject(PosReturnFlow);
    readonly conn = inject(PosConnectivity);

    readonly visible = input(false);
    readonly reasons = input<ManagerReason[]>([]);
    readonly currency = input('');
    readonly authorized = output<FlowAuthorization>();
    readonly cancelled = output<void>();

    readonly busy = signal(false);
    readonly error = signal<string | null>(null);
    readonly text = MANAGER_REASON_TEXT;

    async submit(pin: string): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            const a = await this.flow.authorize(pin, this.reasons());
            this.authorized.emit(a);
        } catch (e) {
            this.error.set(errorMessage(e, 'The PIN could not be checked.'));
        } finally {
            this.busy.set(false);
        }
    }

    cancel(): void {
        this.error.set(null);
        if (this.visible()) this.cancelled.emit();
    }
}
