import { Component, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { MessageService } from 'primeng/api';
import { ToastModule } from 'primeng/toast';
import { AuthService } from '../service/auth.service';

@Component({
    selector: 'app-accept-invite',
    standalone: true,
    imports: [ButtonModule, InputTextModule, FormsModule, ToastModule],
    providers: [MessageService],
    template: `
        <p-toast position="top-right"></p-toast>
        <div class="bg-surface-50 dark:bg-surface-950 flex items-center justify-center min-h-screen min-w-[100vw] overflow-hidden">
            <div class="flex flex-col items-center justify-center">
                <div style="border-radius: 56px; padding: 0.3rem; background: linear-gradient(180deg, var(--primary-color) 10%, rgba(33, 150, 243, 0) 30%)">
                    <div class="w-full bg-surface-0 dark:bg-surface-900 py-20 px-8 sm:px-20" style="border-radius: 53px">
                        <div class="text-center mb-8">
                            <div class="text-surface-900 dark:text-surface-0 text-3xl font-medium mb-4">Welcome</div>
                            <span class="text-muted-color font-medium">Set a password to activate your organization admin account</span>
                        </div>

                        @if (!token) {
                            <div class="text-center">
                                <i class="pi pi-times-circle text-4xl text-red-500"></i>
                                <p class="mt-4 text-red-500 font-medium">Invalid invite link.</p>
                            </div>
                        }

                        @if (token && !accepted) {
                            <div class="w-full md:w-[30rem]">
                                <label for="newPassword" class="block text-surface-900 dark:text-surface-0 text-xl font-medium mb-2">New Password</label>
                                <input pInputText id="newPassword" type="password" placeholder="New Password" class="w-full mb-8" [(ngModel)]="newPassword" />

                                <label for="confirmPassword" class="block text-surface-900 dark:text-surface-0 text-xl font-medium mb-2">Confirm New Password</label>
                                <input pInputText id="confirmPassword" type="password" placeholder="Confirm New Password" class="w-full mb-8" [(ngModel)]="confirmPassword" />

                                <p-button label="Activate Account" styleClass="w-full" (onClick)="submit()" [loading]="isLoading"></p-button>
                            </div>
                        }

                        @if (accepted) {
                            <div class="text-center">
                                <i class="pi pi-check-circle text-4xl text-green-500"></i>
                                <p class="mt-4 text-green-500 font-medium">Your account is ready. Redirecting to login...</p>
                            </div>
                        }
                    </div>
                </div>
            </div>
        </div>
    `,
})
export class AcceptInviteComponent implements OnInit {
    token = '';
    newPassword = '';
    confirmPassword = '';
    isLoading = false;
    accepted = false;

    constructor(
        private route: ActivatedRoute,
        private router: Router,
        private authService: AuthService,
        private messageService: MessageService
    ) {}

    ngOnInit(): void {
        this.token = this.route.snapshot.queryParamMap.get('token') || '';
    }

    submit(): void {
        if (!this.newPassword || this.newPassword !== this.confirmPassword) {
            this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Passwords do not match.', life: 3000 });
            return;
        }
        this.isLoading = true;
        this.authService.acceptInvite(this.token, this.newPassword).subscribe({
            next: (response) => {
                this.isLoading = false;
                if (response.success) {
                    this.accepted = true;
                    this.messageService.add({ severity: 'success', summary: 'Success', detail: response.message || 'Invite accepted.', life: 3000 });
                    setTimeout(() => this.router.navigate(['/auth/login']), 2500);
                } else {
                    this.messageService.add({ severity: 'error', summary: 'Error', detail: response.message || 'Failed to accept invite.', life: 3000 });
                }
            },
            error: (error) => {
                this.isLoading = false;
                this.messageService.add({ severity: 'error', summary: 'Error', detail: error.error?.message || 'This invite link is invalid or has expired.', life: 3000 });
            }
        });
    }
}
