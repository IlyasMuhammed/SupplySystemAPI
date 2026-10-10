import { Component, OnInit, OnDestroy, HostListener, inject } from '@angular/core';
import { MenuItem } from 'primeng/api';
import { RouterModule } from '@angular/router';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { StyleClassModule } from 'primeng/styleclass';
import { LayoutService } from '../service/layout.service';
import { ThemeModeService } from '../../theme/theme-mode.service';
import { FlowCrumbsService } from '../../shared/flow/flow-crumbs.service';
import { AuthService } from '../../pages/service/auth.service';
import { Router } from '@angular/router';
import { Subject, forkJoin, of, EMPTY } from 'rxjs';
import { debounceTime, distinctUntilChanged, switchMap, catchError, takeUntil } from 'rxjs/operators';
import { SupplierService } from '../../services/supplier.service';
import { DemandService } from '../../services/demand.service';
import { WarehouseService } from '../../services/warehouse.service';
import { NotificationService, NotificationDto } from '../../services/notification.service';
import { TenantService } from '../../pages/service/tenant.service';

@Component({
    selector: 'app-topbar',
    standalone: true,
    imports: [RouterModule, CommonModule, FormsModule, StyleClassModule],
    template: `
    <div class="layout-topbar">

        <!-- ── Brand ──────────────────────────────────────────────────────────── -->
        <div class="layout-topbar-logo-container">
            <button class="layout-menu-button layout-topbar-action" (click)="layoutService.onMenuToggle()">
                <i class="pi pi-bars"></i>
            </button>
            <a class="layout-topbar-logo" routerLink="/portal/dashboard">
                <svg viewBox="0 0 54 40" fill="none" xmlns="http://www.w3.org/2000/svg">
                    <path fill-rule="evenodd" clip-rule="evenodd"
                        d="M17.1637 19.2467C17.1566 19.4033 17.1529 19.561 17.1529 19.7194C17.1529 25.3503 21.7203 29.915 27.3546 29.915C32.9887 29.915 37.5561 25.3503 37.5561 19.7194C37.5561 19.5572 37.5524 19.3959 37.5449 19.2355C38.5617 19.0801 39.5759 18.9013 40.5867 18.6994L40.6926 18.6782C40.7191 19.0218 40.7326 19.369 40.7326 19.7194C40.7326 27.1036 34.743 33.0896 27.3546 33.0896C19.966 33.0896 13.9765 27.1036 13.9765 19.7194C13.9765 19.374 13.9896 19.0316 14.0154 18.6927L14.0486 18.6994C15.0837 18.9062 16.1223 19.0886 17.1637 19.2467ZM33.3284 11.4538C31.6493 10.2396 29.5855 9.52381 27.3546 9.52381C25.1195 9.52381 23.0524 10.2421 21.3717 11.4603C20.0078 11.3232 18.6475 11.1387 17.2933 10.907C19.7453 8.11308 23.3438 6.34921 27.3546 6.34921C31.36 6.34921 34.9543 8.10844 37.4061 10.896C36.0521 11.1292 34.692 11.3152 33.3284 11.4538ZM43.826 18.0518C43.881 18.6003 43.9091 19.1566 43.9091 19.7194C43.9091 28.8568 36.4973 36.2642 27.3546 36.2642C18.2117 36.2642 10.8 28.8568 10.8 19.7194C10.8 19.1615 10.8276 18.61 10.8816 18.0663L7.75383 17.4411C7.66775 18.1886 7.62354 18.9488 7.62354 19.7194C7.62354 30.6102 16.4574 39.4388 27.3546 39.4388C38.2517 39.4388 47.0855 30.6102 47.0855 19.7194C47.0855 18.9439 47.0407 18.1789 46.9536 17.4267L43.826 18.0518ZM44.2613 9.54743L40.9084 10.2176C37.9134 5.95821 32.9593 3.1746 27.3546 3.1746C21.7442 3.1746 16.7856 5.96385 13.7915 10.2305L10.4399 9.56057C13.892 3.83178 20.1756 0 27.3546 0C34.5281 0 40.8075 3.82591 44.2613 9.54743Z"
                        fill="var(--primary-color)" />
                    <mask id="mask0_1413_1551" style="mask-type: alpha" maskUnits="userSpaceOnUse" x="0" y="8" width="54" height="11">
                        <path d="M27 18.3652C10.5114 19.1944 0 8.88892 0 8.88892C0 8.88892 16.5176 14.5866 27 14.5866C37.4824 14.5866 54 8.88892 54 8.88892C54 8.88892 43.4886 17.5361 27 18.3652Z" fill="var(--primary-color)" />
                    </mask>
                    <g mask="url(#mask0_1413_1551)">
                        <path d="M-4.673e-05 8.88887L3.73084 -1.91434L-8.00806 17.0473L-4.673e-05 8.88887ZM27 18.3652L26.4253 6.95109L27 18.3652ZM54 8.88887L61.2673 17.7127L50.2691 -1.91434L54 8.88887Z" fill="var(--primary-color)" />
                    </g>
                </svg>
</a>
            <!-- SMS Flow: breadcrumbs set by the page (<sf-page [crumbs]>); the organization name otherwise -->
            <nav class="sf-crumbs" aria-label="Breadcrumb" *ngIf="crumbs.crumbs().length; else orgName" data-testid="topbar-crumbs">
                <ng-container *ngFor="let c of crumbs.crumbs(); let last = last; let i = index">
                    <strong *ngIf="last; else crumbPart">{{ c }}</strong>
                    <ng-template #crumbPart>
                        <a *ngIf="i === 1 && crumbs.crumbs().length > 2 && listUrl; else plainCrumb" [routerLink]="listUrl" class="sf-crumb-link">{{ c }}</a>
                        <ng-template #plainCrumb><span>{{ c }}</span></ng-template>
                        <span class="sf-crumb-sep">›</span>
                    </ng-template>
                </ng-container>
            </nav>
            <ng-template #orgName>
                <div class="brand-block">
                    <span class="brand-name" *ngIf="tenantService.tenant() as t; else plainBrand">
                        {{ t.orgName }} ({{ t.orgCode }})
                    </span>
                    <ng-template #plainBrand><span class="brand-name">Supply Chain</span></ng-template>
                </div>
            </ng-template>
        </div>

        <!-- ── Search trigger ──────────────────────────────────────────────────── -->
        <div class="topbar-center">
            <button class="topbar-search-btn" type="button" (click)="openSearch()">
                <i class="pi pi-search topbar-search-icon"></i>
                <span class="topbar-search-text">Search anything...</span>
                <kbd class="topbar-kbd">Ctrl K</kbd>
            </button>
        </div>

        <!-- ── Right actions ───────────────────────────────────────────────────── -->
        <div class="layout-topbar-actions">

            <!-- Notifications -->
            <div class="relative notif-wrapper">
                <button class="layout-topbar-action topbar-notif-btn" type="button"
                        title="Notifications" (click)="toggleNotifPanel($event)">
                    <i class="pi pi-bell"></i>
                </button>
                <!-- Badge is outside the button to avoid layout-topbar-action span{display:none} -->
                <span *ngIf="notifService.unreadCount() > 0" class="notif-badge">
                    {{ notifService.unreadCount() > 99 ? '99+' : notifService.unreadCount() }}
                </span>

                <!-- Notification dropdown panel -->
                <div *ngIf="showNotifPanel" class="notif-panel" (click)="$event.stopPropagation()">

                    <!-- Header -->
                    <div class="notif-panel-header">
                        <div class="notif-header-left">
                            <div class="notif-header-icon">
                                <i class="pi pi-bell"></i>
                            </div>
                            <div>
                                <p class="notif-header-title">Notifications</p>
                                <p class="notif-header-sub" *ngIf="notifService.unreadCount() > 0; else allRead">
                                    {{ notifService.unreadCount() }} unread
                                </p>
                                <ng-template #allRead>
                                    <p class="notif-header-sub">All caught up</p>
                                </ng-template>
                            </div>
                        </div>
                        <button *ngIf="notifService.unreadCount() > 0"
                                class="notif-mark-all-btn" (click)="markAllRead()" title="Mark all as read">
                            <i class="pi pi-check-circle"></i>
                            Mark all read
                        </button>
                    </div>

                    <!-- List -->
                    <div class="notif-list">
                        <ng-container *ngIf="notifService.notifications().length === 0">
                            <div class="notif-empty">
                                <div class="notif-empty-icon">
                                    <i class="pi pi-inbox"></i>
                                </div>
                                <p class="notif-empty-title">No notifications</p>
                                <p class="notif-empty-sub">You're all caught up!</p>
                            </div>
                        </ng-container>

                        <div *ngFor="let n of notifService.notifications().slice(0, 8)"
                             class="notif-item" [class.notif-unread]="!n.isRead"
                             (click)="clickNotif(n)">
                            <!-- Category accent bar -->
                            <div class="notif-accent" [style.background]="notifService.categoryColor(n.category)"></div>
                            <!-- Icon -->
                            <div class="notif-icon-wrap"
                                 [style.--icon-color]="notifService.categoryColor(n.category)">
                                <i [class]="notifService.categoryIcon(n.category)"></i>
                            </div>
                            <!-- Content -->
                            <div class="notif-content">
                                <div class="notif-content-top">
                                    <p class="notif-title">{{ n.title }}</p>
                                    <span class="notif-time">{{ notifService.timeAgo(n.createdAt) }}</span>
                                </div>
                                <p class="notif-msg">{{ n.message }}</p>
                                <span class="notif-category-pill"
                                      [style.--cat]="notifService.categoryColor(n.category)">
                                    {{ n.category }}
                                </span>
                            </div>
                            <!-- Unread dot -->
                            <div *ngIf="!n.isRead" class="notif-dot"></div>
                        </div>
                    </div>

                    <!-- Footer -->
                    <div class="notif-panel-footer">
                        <button class="notif-view-all-btn" (click)="goToAllNotifications()">
                            View all notifications
                            <i class="pi pi-arrow-right"></i>
                        </button>
                    </div>
                </div>
            </div>

            <!-- Appearance: light / dark / match my computer -->
            <div class="layout-config-menu relative">
                <button type="button" class="layout-topbar-action" title="Appearance" aria-label="Appearance"
                        pStyleClass="@next"
                        enterFromClass="hidden" enterActiveClass="animate-scalein"
                        leaveToClass="hidden" leaveActiveClass="animate-fadeout"
                        [hideOnOutsideClick]="true" data-testid="theme-menu-btn">
                    <i [class]="currentThemeIcon"></i>
                </button>
                <div class="user-dropdown theme-dropdown hidden" role="menu" aria-label="Appearance">
                    <ul class="user-dropdown-menu">
                        <li *ngFor="let o of themeMode.options">
                            <button class="user-dropdown-item" role="menuitemradio" [attr.aria-checked]="themeMode.mode() === o.value"
                                    (click)="themeMode.setMode(o.value)" [attr.data-testid]="'theme-' + o.value">
                                <i [class]="o.icon"></i>
                                <span>{{ o.label }}</span>
                                <i *ngIf="themeMode.mode() === o.value" class="pi pi-check theme-check"></i>
                            </button>
                        </li>
                    </ul>
                </div>
            </div>

            <!-- Divider -->
            <div class="topbar-vdivider"></div>

            <!-- User menu -->
            <div class="relative">
                <button class="layout-topbar-action user-menu-btn"
                        pStyleClass="@next"
                        enterFromClass="hidden" enterActiveClass="animate-scalein"
                        leaveToClass="hidden" leaveActiveClass="animate-fadeout"
                        [hideOnOutsideClick]="true">
                    <div class="user-avatar">
                        <img *ngIf="userProfilePicture" [src]="userProfilePicture" class="ua-img" alt="Profile" />
                        <span *ngIf="!userProfilePicture">{{ userInitials }}</span>
                    </div>
                    <span class="user-display-name">{{ userFirstName }}</span>
                    <i class="pi pi-angle-down user-chevron"></i>
                </button>

                <!-- Dropdown -->
                <div class="user-dropdown hidden">

                    <div class="user-dropdown-header">
                        <div class="user-dropdown-avatar">
                            <img *ngIf="userProfilePicture" [src]="userProfilePicture" class="ua-img" alt="Profile" />
                            <span *ngIf="!userProfilePicture">{{ userInitials }}</span>
                        </div>
                        <div class="user-dropdown-info">
                            <p class="user-dropdown-name">{{ userFullName }}</p>
                            <p class="user-dropdown-role">{{ userRole }}</p>
                        </div>
                    </div>

                    <div class="user-dropdown-divider"></div>

                    <ul class="user-dropdown-menu">
                        <li>
                            <button class="user-dropdown-item" (click)="goToProfile()">
                                <i class="pi pi-user"></i>
                                <span>Edit Profile</span>
                            </button>
                        </li>
                        <li>
                            <button class="user-dropdown-item" (click)="goToChangePassword()">
                                <i class="pi pi-lock"></i>
                                <span>Change Password</span>
                            </button>
                        </li>
                    </ul>

                    <div class="user-dropdown-divider"></div>

                    <ul class="user-dropdown-menu">
                        <li>
                            <button class="user-dropdown-item user-dropdown-item--danger" (click)="logout()">
                                <i class="pi pi-sign-out"></i>
                                <span>Sign Out</span>
                            </button>
                        </li>
                    </ul>
                </div>
            </div>
        </div>
    </div>

    <!-- ── Global Search Overlay ──────────────────────────────────────────── -->
    <div *ngIf="showSearch" class="search-overlay" (click)="closeSearch()">
        <div class="search-panel" (click)="$event.stopPropagation()">

            <div class="search-input-row">
                <i class="pi pi-search search-icon"></i>
                <input #searchInput type="text" class="search-input"
                       placeholder="Search suppliers, POs, GRNs, requisitions..."
                       [(ngModel)]="searchQuery"
                       (ngModelChange)="onSearchInput($event)"
                       autocomplete="off" />
                <button class="search-close-btn" type="button" (click)="closeSearch()">
                    <i class="pi pi-times"></i>
                </button>
            </div>

            <div *ngIf="isSearching" class="search-state">
                <i class="pi pi-spin pi-spinner"></i>&nbsp; Searching...
            </div>

            <div *ngIf="!isSearching && searchQuery.trim().length >= 2 && hasNoResults" class="search-state search-empty">
                No results for &ldquo;{{ searchQuery }}&rdquo;
            </div>

            <div *ngIf="!isSearching && hasResults" class="search-results">
                <ng-container *ngIf="searchResults.suppliers.length > 0">
                    <div class="search-group-label">Suppliers</div>
                    <button *ngFor="let s of searchResults.suppliers" class="search-result-row"
                            (click)="navigateTo('/portal/pages/suppliers/supplier-detail/' + s.uuid)">
                        <i class="pi pi-building search-result-icon"></i>
                        <span class="search-result-name">{{ s.supplierName }}</span>
                        <span class="search-result-meta">{{ s.supplierCode }}</span>
                    </button>
                </ng-container>

                <ng-container *ngIf="searchResults.pos.length > 0">
                    <div class="search-group-label">Purchase Orders</div>
                    <button *ngFor="let po of searchResults.pos" class="search-result-row"
                            (click)="navigateTo('/portal/pages/demand/purchase-orders/' + po.uuid)">
                        <i class="pi pi-file search-result-icon"></i>
                        <span class="search-result-name">{{ po.poNumber }}</span>
                        <span class="search-result-meta">{{ po.supplierName }}</span>
                    </button>
                </ng-container>

                <ng-container *ngIf="searchResults.grns.length > 0">
                    <div class="search-group-label">Goods Receipts</div>
                    <button *ngFor="let grn of searchResults.grns" class="search-result-row"
                            (click)="navigateTo('/portal/pages/warehouse/grn/' + grn.uuid)">
                        <i class="pi pi-box search-result-icon"></i>
                        <span class="search-result-name">{{ grn.grnNumber }}</span>
                        <span class="search-result-meta">{{ grn.supplierName }}</span>
                    </button>
                </ng-container>

                <ng-container *ngIf="searchResults.prs.length > 0">
                    <div class="search-group-label">Requisitions</div>
                    <button *ngFor="let pr of searchResults.prs" class="search-result-row"
                            (click)="navigateTo('/portal/pages/demand/requisitions/' + pr.uuid)">
                        <i class="pi pi-list search-result-icon"></i>
                        <span class="search-result-name">{{ pr.prNumber }}</span>
                        <span class="search-result-meta">{{ pr.prTitle }}</span>
                    </button>
                </ng-container>
            </div>

            <div *ngIf="!searchQuery.trim()" class="search-hint">
                <span>Type at least 2 characters to search &nbsp;&bull;&nbsp; Press <kbd>Esc</kbd> to close</span>
            </div>
        </div>
    </div>
    `,
    styles: [`
        .brand-block {
            display: flex;
            align-items: baseline;
            gap: 0.375rem;
        }

        .brand-name {
            font-size: 1rem;
            font-weight: 700;
            color: var(--text-color);
            letter-spacing: -0.025em;
            white-space: nowrap;
        }

        .sf-crumbs {
            display: flex; align-items: center; gap: 6px; flex-wrap: nowrap; min-width: 0;
            font-size: 0.93rem; color: var(--sms-text-muted); margin-left: 0.5rem; overflow: hidden;
            span, strong { white-space: nowrap; }
            strong { color: var(--sms-text); font-weight: 600; overflow: hidden; text-overflow: ellipsis; }
            .sf-crumb-sep { opacity: .7; }
            .sf-crumb-link { color: var(--sms-text-muted); white-space: nowrap; }
            .sf-crumb-link:hover { color: var(--sms-primary-text); text-decoration: underline; }
        }

        .brand-tag {
            font-size: 0.6rem;
            font-weight: 700;
            letter-spacing: 0.08em;
            text-transform: uppercase;
            color: var(--primary-color);
            background: color-mix(in srgb, var(--primary-color) 12%, transparent);
            padding: 0.125rem 0.375rem;
            border-radius: 4px;
            line-height: 1.6;
            border: 1px solid color-mix(in srgb, var(--primary-color) 20%, transparent);
        }

        /* ── Notification bell ── */
        .topbar-notif-btn { position: relative; }
        .notif-wrapper    { position: relative; }

        .notif-badge {
            position: absolute;
            top: 2px; right: 2px;
            min-width: 17px; height: 17px;
            background: #d93a30;
            color: #fff;
            font-size: 0.58rem;
            font-weight: 700;
            border-radius: 999px;
            display: flex; align-items: center; justify-content: center;
            padding: 0 4px;
            line-height: 1;
            border: 2px solid var(--sms-surface);
            pointer-events: none;
            z-index: 1;
            animation: badgePop .2s cubic-bezier(.34,1.56,.64,1);
        }

        @keyframes badgePop {
            from { transform: scale(0); opacity: 0; }
            to   { transform: scale(1); opacity: 1; }
        }

        /* ── Panel shell ── */
        .notif-panel {
            position: absolute;
            top: calc(100% + 10px);
            right: -4px;
            width: 400px;
            background: var(--surface-card);
            border: 1px solid var(--surface-border);
            border-radius: var(--sms-radius);
            box-shadow: var(--sms-shadow-lg);
            z-index: 99999;
            overflow: hidden;
            animation: notifSlideIn .18s cubic-bezier(.22,.68,0,1.2);
        }

        @keyframes notifSlideIn {
            from { opacity:0; transform:translateY(-10px) scale(.97); }
            to   { opacity:1; transform:translateY(0)    scale(1);    }
        }

        /* ── Header ── */
        .notif-panel-header {
            display: flex; align-items: center; justify-content: space-between;
            padding: 1rem 1.125rem .875rem;
            background: var(--surface-ground);
            border-bottom: 1px solid var(--surface-border);
        }

        .notif-header-left {
            display: flex; align-items: center; gap: .75rem;
        }

        .notif-header-icon {
            width: 38px; height: 38px;
            border-radius: 10px;
            background: color-mix(in srgb, var(--primary-color) 14%, transparent);
            border: 1px solid color-mix(in srgb, var(--primary-color) 25%, transparent);
            display: flex; align-items: center; justify-content: center;
            flex-shrink: 0;
            i {
                font-size: 1rem;
                color: var(--primary-color);
            }
        }

        .notif-header-title {
            font-size: .9rem; font-weight: 700;
            color: var(--text-color);
            margin: 0 0 1px;
        }

        .notif-header-sub {
            font-size: .72rem;
            color: var(--text-color-secondary);
            margin: 0;
        }

        .notif-mark-all-btn {
            display: flex; align-items: center; gap: .3rem;
            background: none; border: 1px solid var(--surface-border);
            cursor: pointer; border-radius: 6px;
            font-size: .72rem; color: var(--text-color-secondary);
            font-weight: 600; padding: .3rem .6rem;
            transition: all .15s;
            white-space: nowrap;
            &:hover {
                border-color: var(--primary-color);
                color: var(--primary-color);
                background: color-mix(in srgb, var(--primary-color) 6%, transparent);
            }
            i { font-size: .72rem; }
        }

        /* ── List ── */
        .notif-list {
            max-height: 400px;
            overflow-y: auto;
            scrollbar-width: thin;
            scrollbar-color: var(--surface-300) transparent;
            &::-webkit-scrollbar { width: 4px; }
            &::-webkit-scrollbar-track { background: transparent; }
            &::-webkit-scrollbar-thumb { background: var(--surface-300); border-radius: 99px; }
        }

        /* ── Empty state ── */
        .notif-empty {
            display: flex; flex-direction: column; align-items: center; justify-content: center;
            gap: .5rem; padding: 3rem 1.5rem;
        }

        .notif-empty-icon {
            width: 56px; height: 56px; border-radius: 16px;
            background: var(--surface-100);
            display: flex; align-items: center; justify-content: center;
            margin-bottom: .25rem;
            i { font-size: 1.5rem; color: var(--text-color-secondary); opacity: .5; }
        }

        .notif-empty-title {
            font-size: .875rem; font-weight: 600;
            color: var(--text-color);
            margin: 0;
        }

        .notif-empty-sub {
            font-size: .78rem; color: var(--text-color-secondary);
            margin: 0;
        }

        /* ── Notification item ── */
        .notif-item {
            display: flex; align-items: flex-start; gap: 0;
            cursor: pointer;
            border-bottom: 1px solid var(--surface-50);
            transition: background .12s;
            position: relative;
            overflow: hidden;
            &:last-child { border-bottom: none; }
            &:hover { background: var(--surface-hover); }
        }

        .notif-unread {
            background: color-mix(in srgb, var(--primary-color) 4%, var(--surface-card));
        }

        /* Left color accent bar */
        .notif-accent {
            width: 3px;
            align-self: stretch;
            flex-shrink: 0;
            opacity: .75;
        }

        .notif-icon-wrap {
            width: 34px; height: 34px; border-radius: 9px;
            background: color-mix(in srgb, var(--icon-color, #64748b) 12%, transparent);
            border: 1px solid color-mix(in srgb, var(--icon-color, #64748b) 20%, transparent);
            display: flex; align-items: center; justify-content: center;
            flex-shrink: 0;
            margin: .75rem .625rem .75rem .75rem;
            i {
                font-size: .8rem;
                color: var(--icon-color, #64748b);
            }
        }

        .notif-content {
            flex: 1; min-width: 0;
            padding: .625rem .75rem .625rem 0;
        }

        .notif-content-top {
            display: flex; align-items: flex-start; justify-content: space-between;
            gap: .5rem; margin-bottom: 2px;
        }

        .notif-title {
            font-size: .8rem; font-weight: 600;
            color: var(--text-color);
            margin: 0;
            white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
            flex: 1; min-width: 0;
        }

        .notif-time {
            font-size: .68rem; color: var(--text-color-secondary);
            white-space: nowrap; flex-shrink: 0;
            margin-top: 1px;
        }

        .notif-msg {
            font-size: .75rem; color: var(--text-color-secondary);
            margin: 0 0 5px;
            display: -webkit-box; -webkit-line-clamp: 2;
            -webkit-box-orient: vertical; overflow: hidden;
            line-height: 1.4;
        }

        .notif-category-pill {
            display: inline-block;
            font-size: .62rem; font-weight: 700;
            letter-spacing: .04em;
            text-transform: uppercase;
            color: var(--cat, #64748b);
            background: color-mix(in srgb, var(--cat, #64748b) 10%, transparent);
            border: 1px solid color-mix(in srgb, var(--cat, #64748b) 20%, transparent);
            padding: 1px 6px; border-radius: 4px;
        }

        /* Unread indicator dot (right side) */
        .notif-dot {
            width: 7px; height: 7px; border-radius: 50%;
            background: var(--primary-color);
            flex-shrink: 0;
            margin: .875rem .75rem 0 0;
            box-shadow: 0 0 0 2px color-mix(in srgb, var(--primary-color) 25%, transparent);
        }

        /* ── Footer ── */
        .notif-panel-footer {
            padding: .75rem 1rem;
            border-top: 1px solid var(--surface-border);
            background: var(--surface-ground);
        }

        .notif-view-all-btn {
            display: flex; align-items: center; justify-content: center;
            gap: .5rem; width: 100%;
            background: none; border: none; cursor: pointer;
            font-size: .8rem; font-weight: 600;
            color: var(--primary-color);
            padding: .4rem;
            border-radius: 8px;
            transition: background .12s;
            i { font-size: .75rem; transition: transform .15s; }
            &:hover {
                background: color-mix(in srgb, var(--primary-color) 8%, transparent);
                i { transform: translateX(3px); }
            }
        }

        .user-menu-btn {
            display: flex;
            align-items: center;
            gap: 0.5rem;
            padding: 0.375rem 0.75rem;
            border-radius: 8px;
            cursor: pointer;
            background: transparent;
            border: none;
            transition: background-color var(--element-transition-duration);

            &:hover { background-color: var(--surface-hover); }
        }

        .user-avatar {
            width: 2rem;
            height: 2rem;
            border-radius: 50%;
            background: var(--primary-color);
            color: #fff;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 0.75rem;
            font-weight: 700;
            flex-shrink: 0;
            letter-spacing: 0.02em;
            overflow: hidden;
        }

        /* .layout-topbar-action hides every span (icon-only buttons); the initials must stay visible. */
        .user-menu-btn .user-avatar span { display: inline; font-size: 0.75rem; }

        .ua-img {
            width: 100%; height: 100%;
            object-fit: cover; border-radius: 50%;
            display: block;
        }

        .user-display-name {
            font-size: 0.875rem;
            font-weight: 600;
            color: var(--text-color);
            max-width: 120px;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
        }

        .user-chevron {
            font-size: 0.75rem;
            color: var(--text-color-secondary);
        }

        .user-dropdown {
            position: absolute;
            top: calc(100% + 0.5rem);
            right: 0;
            min-width: 240px;
            background-color: var(--sms-surface);
            border: 1px solid var(--sms-border);
            border-radius: var(--sms-radius);
            box-shadow: var(--sms-shadow-lg);
            z-index: 9999;
            overflow: hidden;
        }

        .user-dropdown-header {
            display: flex;
            align-items: center;
            gap: 0.75rem;
            padding: 0.875rem 1rem;
            background-color: var(--sms-surface-2);
        }

        .user-dropdown-avatar {
            width: 2.5rem;
            height: 2.5rem;
            border-radius: 50%;
            background: var(--primary-color);
            color: #fff;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 0.875rem;
            font-weight: 700;
            flex-shrink: 0;
            overflow: hidden;
        }

        .user-dropdown-info {
            flex: 1;
            overflow: hidden;
        }

        .user-dropdown-name {
            font-size: 0.875rem;
            font-weight: 700;
            color: var(--text-color);
            margin: 0 0 0.1rem;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
        }

        .user-dropdown-role {
            font-size: 0.75rem;
            color: var(--text-color-secondary);
            margin: 0;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
        }

        .user-dropdown-divider {
            height: 1px;
            background: var(--surface-200);
        }

        .user-dropdown-menu {
            list-style: none;
            margin: 0;
            padding: 0.375rem 0;
        }

        .user-dropdown-item {
            display: flex;
            align-items: center;
            gap: 0.625rem;
            width: 100%;
            padding: 0.5rem 1rem;
            background-color: transparent;
            border: none;
            cursor: pointer;
            font-size: 0.875rem;
            color: var(--sms-text);
            text-align: left;
            transition: background-color 0.12s;

            i { font-size: 0.875rem; color: var(--sms-text-muted); }
        }

        .user-dropdown-item:hover {
            background-color: var(--sms-row-hover);
        }

        .user-dropdown-item--danger {
            color: var(--sms-danger);
        }

        .user-dropdown-item--danger i {
            color: var(--sms-danger);
        }

        .user-dropdown-item--danger:hover {
            background-color: var(--sms-danger-soft);
        }

        .theme-dropdown { min-width: 220px; }
        .theme-check { margin-left: auto; color: var(--sms-primary-text) !important; }

        /* ── Global Search Overlay ── */
        .search-overlay {
            position: fixed;
            inset: 0;
            background: rgba(9, 17, 28, 0.45);
            z-index: 100000;
            display: flex;
            align-items: flex-start;
            justify-content: center;
            padding-top: 6rem;
            backdrop-filter: blur(2px);
        }

        .search-panel {
            width: 100%;
            max-width: 640px;
            background: var(--surface-card);
            border: 1px solid var(--surface-border);
            border-radius: 12px;
            box-shadow: var(--sms-shadow-lg);
            overflow: hidden;
        }

        .search-input-row {
            display: flex;
            align-items: center;
            gap: 0.75rem;
            padding: 0.875rem 1rem;
            border-bottom: 1px solid var(--surface-200);
        }

        .search-icon {
            font-size: 1rem;
            color: var(--text-color-secondary);
            flex-shrink: 0;
        }

        .search-input {
            flex: 1;
            border: none;
            outline: none;
            background: transparent;
            font-size: 1rem;
            color: var(--text-color);
            font-family: inherit;
        }

        .search-input::placeholder { color: var(--text-color-secondary); }

        .search-close-btn {
            background: none;
            border: none;
            cursor: pointer;
            color: var(--text-color-secondary);
            font-size: 0.875rem;
            padding: 0.25rem;
            border-radius: 4px;
            flex-shrink: 0;
            &:hover { background: var(--surface-100); color: var(--text-color); }
        }

        .search-state {
            padding: 1.25rem 1rem;
            font-size: 0.875rem;
            color: var(--text-color-secondary);
            text-align: center;
        }

        .search-results {
            max-height: 420px;
            overflow-y: auto;
        }

        .search-group-label {
            font-size: 0.7rem;
            font-weight: 700;
            text-transform: uppercase;
            letter-spacing: 0.06em;
            color: var(--text-color-secondary);
            padding: 0.625rem 1rem 0.25rem;
        }

        .search-result-row {
            display: flex;
            align-items: center;
            gap: 0.75rem;
            width: 100%;
            padding: 0.6rem 1rem;
            background: none;
            border: none;
            cursor: pointer;
            text-align: left;
            color: var(--text-color);
            transition: background 0.12s;
            &:hover { background: var(--surface-hover); }
        }

        .search-result-icon {
            font-size: 0.875rem;
            color: var(--primary-color);
            flex-shrink: 0;
            width: 1.25rem;
        }

        .search-result-name {
            font-size: 0.875rem;
            font-weight: 600;
            flex: 1;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
        }

        .search-result-meta {
            font-size: 0.78rem;
            color: var(--text-color-secondary);
            flex-shrink: 0;
        }

        .search-hint {
            padding: 0.875rem 1rem;
            border-top: 1px solid var(--surface-100);
            font-size: 0.78rem;
            color: var(--text-color-secondary);
            text-align: center;

            kbd {
                display: inline-block;
                padding: 0.1rem 0.35rem;
                font-size: 0.75rem;
                border: 1px solid var(--surface-300);
                border-radius: 4px;
                background: var(--surface-100);
                font-family: inherit;
            }
        }
    `]
})
export class AppTopbar implements OnInit, OnDestroy {
    items!: MenuItem[];

    // ── Search state ────────────────────────────────────────────────────────
    showSearch = false;
    searchQuery = '';
    isSearching = false;
    searchResults: { suppliers: any[]; pos: any[]; grns: any[]; prs: any[] } =
        { suppliers: [], pos: [], grns: [], prs: [] };

    private searchSubject = new Subject<string>();
    private destroy$ = new Subject<void>();

    // ── Notification panel state ─────────────────────────────────────────────
    showNotifPanel = false;

    constructor(
        public layoutService: LayoutService,
        private authService: AuthService,
        private router: Router,
        private supplierService: SupplierService,
        private demandService: DemandService,
        private warehouseService: WarehouseService,
        public notifService: NotificationService,
        public tenantService: TenantService
    ) {}

    ngOnInit() {
        this.searchSubject.pipe(
            debounceTime(300),
            distinctUntilChanged(),
            switchMap(q => {
                const trimmed = q.trim();
                if (trimmed.length < 2) {
                    this.isSearching = false;
                    this.searchResults = { suppliers: [], pos: [], grns: [], prs: [] };
                    return EMPTY;
                }
                this.isSearching = true;
                return forkJoin({
                    suppliers: this.supplierService.getSuppliers({ search: trimmed, page: 1, pageSize: 5 }).pipe(catchError(() => of(null))),
                    pos:       this.demandService.getPos({ search: trimmed, page: 1, pageSize: 5 }).pipe(catchError(() => of(null))),
                    grns:      this.warehouseService.getGrns({ search: trimmed, page: 1, pageSize: 5 }).pipe(catchError(() => of(null))),
                    prs:       this.demandService.getPrs({ search: trimmed, page: 1, pageSize: 5 }).pipe(catchError(() => of(null)))
                });
            }),
            takeUntil(this.destroy$)
        ).subscribe(results => {
            if (!results) return;
            this.isSearching = false;
            this.searchResults = {
                suppliers: (results as any).suppliers?.result?.data ?? [],
                pos:       (results as any).pos?.result?.data ?? [],
                grns:      (results as any).grns?.result?.data ?? [],
                prs:       (results as any).prs?.result?.data ?? []
            };
        });
    }

    ngOnDestroy() {
        this.destroy$.next();
        this.destroy$.complete();
    }

    @HostListener('document:click')
    onDocumentClick() {
        if (this.showNotifPanel) this.showNotifPanel = false;
    }

    @HostListener('document:keydown', ['$event'])
    handleKeyboard(event: KeyboardEvent) {
        if ((event.ctrlKey || event.metaKey) && event.key === 'k') {
            event.preventDefault();
            this.openSearch();
        }
        if (event.key === 'Escape' && this.showSearch) {
            this.closeSearch();
        }
    }

    openSearch() { this.showSearch = true; }

    closeSearch() {
        this.showSearch = false;
        this.searchQuery = '';
        this.isSearching = false;
        this.searchResults = { suppliers: [], pos: [], grns: [], prs: [] };
    }

    onSearchInput(value: string) { this.searchSubject.next(value); }

    navigateTo(path: string) {
        this.router.navigate([path]);
        this.closeSearch();
    }

    get hasResults(): boolean {
        return this.searchResults.suppliers.length > 0 ||
               this.searchResults.pos.length > 0 ||
               this.searchResults.grns.length > 0 ||
               this.searchResults.prs.length > 0;
    }

    get hasNoResults(): boolean { return !this.hasResults; }

    get userInitials(): string {
        const user = this.authService.getUserData();
        const first = user?.firstName?.[0] ?? '';
        const last  = user?.lastName?.[0]  ?? '';
        return (first + last).toUpperCase() || 'U';
    }

    get userProfilePicture(): string | null {
        return this.authService.getUserData()?.profilePictureUrl ?? null;
    }

    get userFirstName(): string {
        return this.authService.getUserData()?.firstName ?? 'User';
    }

    get userFullName(): string {
        const user = this.authService.getUserData();
        return [user?.firstName, user?.lastName].filter(Boolean).join(' ') || 'User';
    }

    get userRole(): string {
        return this.authService.getUserData()?.role?.value ?? '';
    }

    toggleNotifPanel(event: Event) {
        event.stopPropagation();
        this.showNotifPanel = !this.showNotifPanel;
    }

    markAllRead() {
        this.notifService.markAllRead();
    }

    clickNotif(n: NotificationDto) {
        this.showNotifPanel = false;
        this.notifService.navigateTo(n);
    }

    goToAllNotifications() {
        this.showNotifPanel = false;
        this.router.navigate(['/portal/pages/notifications']);
    }

    readonly themeMode = inject(ThemeModeService);
    readonly crumbs = inject(FlowCrumbsService);

    /**
     * Where the "list" breadcrumb of a record page leads: the current URL without its record part
     * (…/orders/<uuid>/edit → …/orders, …/orders/new → …/orders). Null when nothing would be left to strip.
     */
    get listUrl(): string | null {
        const path = this.router.url.split(/[?#]/)[0].split('/').filter((s) => !!s);
        const isRecordPart = (s: string) => /^(new|create|edit|[0-9a-f]{8}-[0-9a-f-]{27,}|\d+)$/i.test(s);
        let n = path.length;
        while (n > 0 && isRecordPart(path[n - 1])) n--;
        return n > 0 && n < path.length ? '/' + path.slice(0, n).join('/') : null;
    }

    get currentThemeIcon(): string {
        return this.themeMode.options.find((o) => o.value === this.themeMode.mode())?.icon ?? 'pi pi-sun';
    }

    goToProfile() {
        this.router.navigate(['/portal/pages/profile']);
    }

    goToChangePassword() {
        this.router.navigate(['/portal/pages/profile'], { queryParams: { tab: 'password' } });
    }

    logout() {
        this.authService.logout();
    }
}
