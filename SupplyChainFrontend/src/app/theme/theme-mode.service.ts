import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { LayoutService } from '../layout/service/layout.service';

export type ThemeMode = 'light' | 'dark' | 'system';

/** Same key the inline script in index.html reads, so the first paint is already in the right mode. */
export const THEME_MODE_KEY = 'sms.theme';

function readStoredMode(): ThemeMode {
    try {
        const v = localStorage.getItem(THEME_MODE_KEY);
        return v === 'light' || v === 'dark' || v === 'system' ? v : 'system';
    } catch {
        return 'system';
    }
}

/**
 * Light, dark or "match my computer" — per browser (localStorage), applied through LayoutService.darkTheme so the
 * `.app-dark` class and PrimeNG's dark scheme stay in one place. "system" follows prefers-color-scheme live.
 */
@Injectable({ providedIn: 'root' })
export class ThemeModeService {
    private readonly layout = inject(LayoutService);
    private readonly media = typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
    private readonly systemDark = signal(this.media?.matches ?? false);

    readonly mode = signal<ThemeMode>(readStoredMode());
    readonly isDark = computed(() => (this.mode() === 'system' ? this.systemDark() : this.mode() === 'dark'));

    readonly options: { value: ThemeMode; label: string; icon: string }[] = [
        { value: 'light', label: 'Light', icon: 'pi pi-sun' },
        { value: 'dark', label: 'Dark', icon: 'pi pi-moon' },
        { value: 'system', label: 'Match my computer', icon: 'pi pi-desktop' }
    ];

    constructor() {
        this.media?.addEventListener('change', (e) => this.systemDark.set(e.matches));
        effect(() => {
            const dark = this.isDark();
            untracked(() => this.layout.layoutConfig.update((s) => (s.darkTheme === dark ? s : { ...s, darkTheme: dark })));
        });
    }

    setMode(mode: ThemeMode): void {
        this.mode.set(mode);
        try {
            localStorage.setItem(THEME_MODE_KEY, mode);
        } catch {
            /* private window / blocked storage: the choice lasts for this session only */
        }
    }

    /** Light ↔ dark, leaving "match my computer" for an explicit choice. */
    toggle(): void {
        this.setMode(this.isDark() ? 'light' : 'dark');
    }
}
