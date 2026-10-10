import { Component } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { InputTextModule } from 'primeng/inputtext';
import { PasswordModule } from 'primeng/password';
import { RippleModule } from 'primeng/ripple';
import { MessageService } from 'primeng/api';
import { ToastModule } from 'primeng/toast';
import { AppFloatingConfigurator } from '../../layout/component/app.floatingconfigurator';
import { AuthService } from '../service/auth.service';

@Component({
    selector: 'app-login',
    standalone: true,
    imports: [
        ButtonModule,
        CheckboxModule,
        InputTextModule,
        PasswordModule,
        FormsModule,
        RippleModule,
        AppFloatingConfigurator,
        ToastModule,
    ],
    providers: [MessageService],
    template: `
        <app-floating-configurator />
        <p-toast position="top-right"></p-toast>
        <div class="flex items-center justify-center min-h-screen min-w-[100vw] overflow-hidden" style="background: var(--sms-bg)">
            <div class="flex flex-col items-center justify-center">
                <div style="border-radius: var(--sms-radius)">
                    <div class="w-full py-20 px-8 sm:px-20" style="border-radius: var(--sms-radius); background: var(--sms-surface); border: 1px solid var(--sms-border); border-top: 3px solid var(--sms-primary); box-shadow: var(--sms-shadow-lg)">
                        <div class="text-center mb-8">
                            <div class="text-surface-900 dark:text-surface-0 text-3xl font-medium mb-4">Supply Chain Management</div>
                            <span class="text-muted-color font-medium">Sign in to continue</span>
                        </div>

                        <div>
                            <label for="email1" class="block text-surface-900 dark:text-surface-0 text-xl font-medium mb-2">Email</label>
                            <input
                                pInputText
                                id="email1"
                                type="email"
                                placeholder="Email address"
                                class="w-full md:w-[30rem]"
                                [class.ng-invalid]="submitted && !email"
                                [class.ng-dirty]="submitted && !email"
                                [(ngModel)]="email"
                                (keyup.enter)="authenticate()"
                            />
                            @if (submitted && !email) {
                                <small class="text-red-500 block mt-1 mb-4">Email is required.</small>
                            } @else {
                                <div class="mb-8"></div>
                            }

                            <label for="password1" class="block text-surface-900 dark:text-surface-0 font-medium text-xl mb-2">Password</label>
                            <p-password
                                id="password1"
                                [(ngModel)]="password"
                                placeholder="Password"
                                [toggleMask]="true"
                                styleClass="mb-1"
                                [fluid]="true"
                                [feedback]="false"
                                (keyup.enter)="authenticate()"
                            ></p-password>
                            @if (submitted && !password) {
                                <small class="text-red-500 block mt-1 mb-4">Password is required.</small>
                            } @else {
                                <div class="mb-4"></div>
                            }

                            <div class="flex items-center justify-between mt-2 mb-8 gap-8">
                                <div class="flex items-center gap-2">
                                    <p-checkbox [(ngModel)]="rememberMe" [binary]="true" inputId="rememberMe"></p-checkbox>
                                    <label for="rememberMe" class="cursor-pointer select-none text-surface-700 dark:text-surface-200">Remember me</label>
                                </div>
                                <span class="font-medium no-underline cursor-pointer text-primary" (click)="navigateToForgot()">Forgot password?</span>
                            </div>

                            <p-button label="Sign In" styleClass="w-full" (onClick)="authenticate()" [loading]="isLoading"></p-button>

                            <div class="text-center mt-6">
                                <span class="text-muted-color">Don't have an account?&nbsp;</span>
                                <span class="font-medium cursor-pointer text-primary" (click)="navigateToSignUp()">Sign Up</span>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    `,
})
export class Login {
    email = '';
    password = '';
    rememberMe = false;
    isLoading = false;
    submitted = false;

    constructor(
        private authService: AuthService,
        private router: Router,
        private messageService: MessageService
    ) {
        const saved = localStorage.getItem('rememberedEmail');
        if (saved) {
            this.email = saved;
            this.rememberMe = true;
        }
    }

    authenticate(): void {
        this.submitted = true;
        if (!this.email || !this.password) return;

        if (this.rememberMe) {
            localStorage.setItem('rememberedEmail', this.email);
        } else {
            localStorage.removeItem('rememberedEmail');
        }

        this.isLoading = true;
        this.authService.login(this.email, this.password).subscribe({
            next: (response) => {
                this.isLoading = false;
                if (response.success && response.result) {
                    const redirectUrl = sessionStorage.getItem('redirectUrl');
                    if (redirectUrl) {
                        sessionStorage.removeItem('redirectUrl');
                        this.router.navigateByUrl(redirectUrl);
                    } else {
                        this.router.navigate(['portal/dashboard']);
                    }
                } else {
                    this.messageService.add({ severity: 'error', summary: 'Login Failed', detail: response.message || 'Login failed.' });
                }
            },
            error: (err) => {
                this.isLoading = false;
                // The server's own errors (locked account, bad credentials) carry their message in
                // result.exceptionMessage; an unexpected failure carries none — only the generic message
                // with a reference to the server log — so it falls through to err.error.message.
                const detail = err.error?.result?.exceptionMessage || err.error?.message || 'Invalid email or password.';
                this.messageService.add({
                    severity: 'error',
                    summary: 'Login Failed',
                    detail,
                    life: 8000,
                });
                this.password = '';
            }
        });
    }

    navigateToSignUp(): void {
        this.router.navigate(['/sign-up']);
    }

    navigateToForgot(): void {
        this.router.navigate(['/forgot-password']);
    }
}
