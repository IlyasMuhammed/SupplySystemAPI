import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { Router, ActivatedRoute } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { PasswordModule } from 'primeng/password';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { AuthService, LoggedInUser } from '../service/auth.service';

function passwordMatchValidator(control: AbstractControl): ValidationErrors | null {
  const newPwd = control.get('newPassword')?.value;
  const confirm = control.get('confirmPassword')?.value;
  if (newPwd && confirm && newPwd !== confirm) {
    control.get('confirmPassword')?.setErrors({ mismatch: true });
    return { mismatch: true };
  }
  if (control.get('confirmPassword')?.hasError('mismatch')) {
    control.get('confirmPassword')?.setErrors(null);
  }
  return null;
}

const ALLOWED_TYPES = ['image/jpeg', 'image/png', 'image/webp'];
const MAX_SIZE_MB   = 5;

@Component({
  selector: 'app-profile',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule,
    ButtonModule, InputTextModule, PasswordModule,
    ToastModule, TooltipModule
  ],
  templateUrl: './profile.component.html',
  styleUrls: ['./profile.component.scss'],
  providers: [MessageService]
})
export class ProfileComponent implements OnInit, OnDestroy {
  user: LoggedInUser | null = null;
  activeTab: 'profile' | 'password' | 'account' = 'profile';

  // ── Profile form ──────────────────────────────────────────────────────────
  profileForm!: FormGroup;
  isSavingProfile = false;

  // ── Password form ─────────────────────────────────────────────────────────
  passwordForm!: FormGroup;
  isSavingPassword = false;

  // ── Profile picture ───────────────────────────────────────────────────────
  previewUrl: string | null = null;
  selectedFile: File | null = null;
  isUploadingPicture = false;
  showDeleteConfirm  = false;

  constructor(
    private fb: FormBuilder,
    private authService: AuthService,
    private messageService: MessageService,
    private router: Router,
    private route: ActivatedRoute
  ) {}

  ngOnInit() {
    this.user = this.authService.getUserData();

    this.route.queryParamMap.subscribe(params => {
      const tab = params.get('tab');
      if (tab === 'password' || tab === 'account') {
        this.activeTab = tab;
      } else {
        this.activeTab = 'profile';
      }
    });

    this.profileForm = this.fb.group({
      firstName: [this.user?.firstName ?? '', [Validators.required, Validators.minLength(2), Validators.maxLength(50)]],
      lastName:  [this.user?.lastName  ?? ''],
      email:     [this.user?.email     ?? '', [Validators.required, Validators.email]],
      phone:     [this.user?.phoneNo   ?? '']
    });

    this.passwordForm = this.fb.group({
      currentPassword: ['', [Validators.required, Validators.minLength(6)]],
      newPassword:     ['', [Validators.required, Validators.minLength(8)]],
      confirmPassword: ['', [Validators.required]]
    }, { validators: passwordMatchValidator });
  }

  ngOnDestroy() {
    if (this.previewUrl) URL.revokeObjectURL(this.previewUrl);
  }

  get pf()  { return this.profileForm.controls; }
  get pwf() { return this.passwordForm.controls; }

  get userInitials(): string {
    const first = this.user?.firstName?.[0] ?? '';
    const last  = this.user?.lastName?.[0]  ?? '';
    return (first + last).toUpperCase() || 'U';
  }

  get userFullName(): string {
    return [this.user?.firstName, this.user?.lastName].filter(Boolean).join(' ') || 'User';
  }

  get avatarSrc(): string | null {
    return this.previewUrl ?? this.user?.profilePictureUrl ?? null;
  }

  setTab(tab: 'profile' | 'password' | 'account') {
    this.activeTab = tab;
    this.router.navigate([], { queryParams: tab === 'profile' ? {} : { tab }, replaceUrl: true });
  }

  // ── Picture management ────────────────────────────────────────────────────

  triggerFilePicker(input: HTMLInputElement) {
    input.value = '';
    input.click();
  }

  onFileSelected(event: Event) {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) return;

    if (!ALLOWED_TYPES.includes(file.type)) {
      this.messageService.add({ severity: 'warn', summary: 'Invalid File', detail: 'Please select a JPEG, PNG, or WebP image.' });
      return;
    }
    if (file.size > MAX_SIZE_MB * 1024 * 1024) {
      this.messageService.add({ severity: 'warn', summary: 'File Too Large', detail: `Image must be smaller than ${MAX_SIZE_MB} MB.` });
      return;
    }

    if (this.previewUrl) URL.revokeObjectURL(this.previewUrl);
    this.selectedFile = file;
    this.previewUrl   = URL.createObjectURL(file);
    this.showDeleteConfirm = false;
  }

  cancelPreview() {
    if (this.previewUrl) URL.revokeObjectURL(this.previewUrl);
    this.previewUrl   = null;
    this.selectedFile = null;
  }

  uploadPicture() {
    if (!this.selectedFile || !this.user?.userId) return;

    const formData = new FormData();
    formData.append('file', this.selectedFile);
    formData.append('userId', String(this.user.userId));

    this.isUploadingPicture = true;
    this.authService.uploadProfilePicture(formData).subscribe({
      next: (res: any) => {
        this.isUploadingPicture = false;
        if (res.success) {
          const pictureUrl: string = res.result?.profilePictureUrl ?? this.previewUrl ?? '';
          const updated = { ...this.user!, profilePictureUrl: pictureUrl };
          localStorage.setItem('userData', JSON.stringify(updated));
          this.user = updated;
          if (this.previewUrl) URL.revokeObjectURL(this.previewUrl);
          this.previewUrl   = null;
          this.selectedFile = null;
          this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Profile picture updated.' });
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Upload failed.' });
        }
      },
      error: (err: any) => {
        this.isUploadingPicture = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to upload picture.' });
      }
    });
  }

  confirmDelete() {
    this.showDeleteConfirm = true;
    this.cancelPreview();
  }

  deletePicture() {
    if (!this.user?.userId) return;
    this.isUploadingPicture = true;
    this.showDeleteConfirm  = false;

    this.authService.deleteProfilePicture().subscribe({
      next: (res: any) => {
        this.isUploadingPicture = false;
        if (res.success) {
          const updated = { ...this.user! };
          delete updated.profilePictureUrl;
          localStorage.setItem('userData', JSON.stringify(updated));
          this.user = updated;
          this.messageService.add({ severity: 'success', summary: 'Removed', detail: 'Profile picture removed.' });
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Remove failed.' });
        }
      },
      error: (err: any) => {
        this.isUploadingPicture = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to remove picture.' });
      }
    });
  }

  // ── Profile form save ────────────────────────────────────────────────────
  saveProfile() {
    if (this.profileForm.invalid) { this.profileForm.markAllAsTouched(); return; }
    if (!this.user?.userId) return;

    this.isSavingProfile = true;
    const raw = this.profileForm.value;

    this.authService.updateProfile({
      userID:    this.user.userId,
      firstName: raw.firstName.trim(),
      lastName:  raw.lastName?.trim() || undefined,
      email:     raw.email.trim(),
      phone:     raw.phone?.trim()    || undefined
    }).subscribe({
      next: (res: any) => {
        this.isSavingProfile = false;
        if (res.success) {
          const updated = { ...this.user!, firstName: raw.firstName.trim(), lastName: raw.lastName?.trim() ?? '', email: raw.email.trim(), phoneNo: raw.phone?.trim() };
          localStorage.setItem('userData', JSON.stringify(updated));
          this.user = updated;
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Profile updated successfully.' });
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to update profile.' });
        }
      },
      error: (err: any) => {
        this.isSavingProfile = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to update profile.' });
      }
    });
  }

  // ── Password change ──────────────────────────────────────────────────────
  changePassword() {
    if (this.passwordForm.invalid) { this.passwordForm.markAllAsTouched(); return; }
    if (!this.user?.userId) return;

    this.isSavingPassword = true;
    const raw = this.passwordForm.value;

    this.authService.updatePassword(this.user.userId, raw.currentPassword, raw.newPassword).subscribe({
      next: (res: any) => {
        this.isSavingPassword = false;
        if (res.success) {
          this.passwordForm.reset();
          this.messageService.add({ severity: 'success', summary: 'Password Changed', detail: 'Password updated. Please log in again.' });
          setTimeout(() => this.authService.logout(), 2500);
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to change password.' });
        }
      },
      error: (err: any) => {
        this.isSavingPassword = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to change password.' });
      }
    });
  }
}
