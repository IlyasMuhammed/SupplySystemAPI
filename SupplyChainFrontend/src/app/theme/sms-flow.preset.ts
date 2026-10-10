import { definePreset } from '@primeng/themes';
import Aura from '@primeng/themes/aura';

/**
 * SMS Flow — the application theme (navy navigation, SAP-style blue, slate surfaces), light and dark.
 * Built on Aura so every PrimeNG component keeps its structure; only the palette, radii and a few colours change.
 * The matching layout tokens (--sms-*) live in assets/layout/variables/_common.scss.
 */
export const SmsFlow = definePreset(Aura, {
    primitive: {
        borderRadius: { none: '0', xs: '2px', sm: '4px', md: '8px', lg: '10px', xl: '14px' }
    },
    semantic: {
        primary: {
            50: '#e8f1fc',
            100: '#cfe2f8',
            200: '#a3c8f2',
            300: '#6eaaea',
            400: '#3a88e0',
            500: '#0064d9',
            600: '#004fae',
            700: '#003f8c',
            800: '#00316c',
            900: '#00244f',
            950: '#001631'
        },
        colorScheme: {
            light: {
                surface: {
                    0: '#ffffff',
                    50: '#f8f9fb',
                    100: '#f3f5f8',
                    200: '#dce2e9',
                    300: '#c6d0db',
                    400: '#9aa9b9',
                    500: '#5a6b7d',
                    600: '#46576a',
                    700: '#1b2b3c',
                    800: '#15222f',
                    900: '#0f1924',
                    950: '#0a1119'
                },
                primary: { color: '{primary.500}', contrastColor: '#ffffff', hoverColor: '{primary.600}', activeColor: '{primary.700}' },
                highlight: { background: '{primary.50}', focusBackground: '{primary.100}', color: '{primary.600}', focusColor: '{primary.700}' }
            },
            dark: {
                surface: {
                    0: '#ffffff',
                    50: '#f1f4f8',
                    100: '#e3e9f0',
                    200: '#cfd8e2',
                    300: '#b8c6d6',
                    400: '#9aabbf',
                    500: '#6f8297',
                    600: '#4b5f78',
                    700: '#2a3a4e',
                    800: '#1b2838',
                    900: '#162130',
                    950: '#0e1622'
                },
                primary: { color: '#2f7de1', contrastColor: '#ffffff', hoverColor: '#4c95ec', activeColor: '#6cb0ff' },
                highlight: {
                    background: 'rgba(76,149,236,.18)',
                    focusBackground: 'rgba(76,149,236,.28)',
                    color: '#cfe2f8',
                    focusColor: '#e8f1fc'
                },
                text: { color: '#e3e9f0', hoverColor: '#ffffff', mutedColor: '{surface.400}', hoverMutedColor: '{surface.300}' }
            }
        }
    }
});
