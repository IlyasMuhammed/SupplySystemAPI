import { Injectable, NgZone, OnDestroy, computed, inject, signal } from '@angular/core';
import { Subject } from 'rxjs';

/**
 * A38 POS-SYN — is the terminal online? Three signals combine: the browser's `navigator.onLine` (+ online/offline events),
 * the API answering (any request that fails with no HTTP status marks it unreachable, any answer marks it reachable) and the
 * `/hubs/pos` connection (shown separately; a connected hub also proves the API is reachable).
 */
@Injectable({ providedIn: 'root' })
export class PosConnectivity implements OnDestroy {
    private readonly zone = inject(NgZone);

    readonly browserOnline = signal<boolean>(typeof navigator === 'undefined' ? true : navigator.onLine !== false);
    readonly apiReachable = signal<boolean>(true);
    readonly hubConnected = signal<boolean>(false);

    /** Usable for API calls right now. */
    readonly online = computed(() => this.browserOnline() && this.apiReachable());

    /** Emits each time the terminal comes back online (offline → online). */
    readonly reconnected$ = new Subject<void>();

    private readonly onOnline = () => this.zone.run(() => this.setBrowser(true));
    private readonly onOffline = () => this.zone.run(() => this.setBrowser(false));

    constructor() {
        if (typeof window !== 'undefined') {
            window.addEventListener('online', this.onOnline);
            window.addEventListener('offline', this.onOffline);
        }
    }

    ngOnDestroy(): void {
        if (typeof window !== 'undefined') {
            window.removeEventListener('online', this.onOnline);
            window.removeEventListener('offline', this.onOffline);
        }
    }

    setBrowser(on: boolean): void {
        const was = this.online();
        this.browserOnline.set(on);
        if (on) this.apiReachable.set(true); // optimistic; the next failed request corrects it
        this.emitIfReconnected(was);
    }

    /** A request failed without an HTTP status (network down, DNS, CORS-less timeout). */
    reportFailure(): void {
        this.apiReachable.set(false);
    }

    /** The API answered (any status). */
    reportSuccess(): void {
        const was = this.online();
        this.apiReachable.set(true);
        this.emitIfReconnected(was);
    }

    setHub(connected: boolean): void {
        this.hubConnected.set(connected);
        if (connected) this.reportSuccess();
    }

    private emitIfReconnected(was: boolean): void {
        if (!was && this.online()) this.reconnected$.next();
    }
}
