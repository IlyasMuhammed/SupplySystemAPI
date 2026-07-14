import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ProgressSpinnerModule } from 'primeng/progressspinner';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { NotificationService, NotificationDto, NotificationPage } from '../../services/notification.service';
import { Subject, firstValueFrom } from 'rxjs';
import { takeUntil } from 'rxjs/operators';

@Component({
  selector: 'app-notifications',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, ProgressSpinnerModule, ToastModule],
  providers: [MessageService],
  templateUrl: './notifications.component.html',
  styleUrl: './notifications.component.scss'
})
export class NotificationsComponent implements OnInit, OnDestroy {
  private readonly destroy$ = new Subject<void>();
  all: NotificationDto[]    = [];
  unread: NotificationDto[] = [];

  allPage      = 1;
  unreadPage   = 1;
  pageSize     = 20;

  allTotal     = 0;
  unreadTotal  = 0;

  isLoadingAll    = false;
  isLoadingUnread = false;

  activeTab: 'all' | 'unread' = 'all';
  selectedCategory = 'all';

  readonly categories = [
    { value: 'all',         label: 'All',         icon: 'pi pi-th-large' },
    { value: 'Procurement', label: 'Procurement',  icon: 'pi pi-shopping-cart' },
    { value: 'Finance',     label: 'Finance',      icon: 'pi pi-dollar' },
    { value: 'Warehouse',   label: 'Warehouse',    icon: 'pi pi-box' },
    { value: 'Logistics',   label: 'Logistics',    icon: 'pi pi-truck' },
    { value: 'System',      label: 'System',       icon: 'pi pi-cog' },
  ];

  constructor(
    public notifService: NotificationService,
    private msg: MessageService
  ) {}

  ngOnInit(): void {
    // Seed from the shared signal immediately so the page isn't blank while the API call is in-flight
    const cached = this.notifService.notifications();
    if (cached.length > 0) {
      this.all      = [...cached];
      this.allTotal = cached.length;
      this.unread   = cached.filter(n => !n.isRead);
      this.unreadTotal = this.unread.length;
    }

    this.loadAll();
    this.loadUnread();

    // Auto-refresh when a new notification arrives via SignalR while this page is open
    this.notifService.onNewNotification$
      .pipe(takeUntil(this.destroy$))
      .subscribe(() => {
        this.loadAll(this.allPage);
        this.loadUnread(this.unreadPage);
      });
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  async loadAll(page = 1): Promise<void> {
    this.isLoadingAll = true;
    try {
      const res = await firstValueFrom(this.notifService.getList(page, this.pageSize, false));
      const p: NotificationPage = res?.result;
      if (p) {
        this.all      = p.data;
        this.allTotal = p.totalRecords;
        this.allPage  = p.page;
      }
    } catch (err) {
      console.error('[Notifications] loadAll failed:', err);
    } finally {
      this.isLoadingAll = false;
    }
  }

  async loadUnread(page = 1): Promise<void> {
    this.isLoadingUnread = true;
    try {
      const res = await firstValueFrom(this.notifService.getList(page, this.pageSize, true));
      const p: NotificationPage = res?.result;
      if (p) {
        this.unread      = p.data;
        this.unreadTotal = p.totalRecords;
        this.unreadPage  = p.page;
        // Keep the topbar bell badge in sync with the actual DB count
        this.notifService.unreadCount.set(p.totalRecords);
      }
    } catch (err) {
      console.error('[Notifications] loadUnread failed:', err);
    } finally {
      this.isLoadingUnread = false;
    }
  }

  async markAllRead(): Promise<void> {
    await this.notifService.markAllRead();
    this.loadAll();
    this.loadUnread();
    this.msg.add({ severity: 'success', summary: 'Done', detail: 'All notifications marked as read.' });
  }

  async markOneRead(n: NotificationDto): Promise<void> {
    if (n.isRead || !n.uuid) return;
    await this.notifService.markOneRead(n.uuid);
    this.refreshLists();
  }

  async deleteNotif(n: NotificationDto): Promise<void> {
    await firstValueFrom(this.notifService.delete(n.uuid));
    this.refreshLists();
  }

  navigate(n: NotificationDto): void {
    this.notifService.navigateTo(n);
    // Don't refresh lists here — we're navigating away from the page
  }

  refreshLists(): void {
    this.loadAll(this.allPage);
    this.loadUnread(this.unreadPage);
  }

  switchTab(tab: 'all' | 'unread'): void {
    this.activeTab = tab;
    this.selectedCategory = 'all';
  }

  get filteredAll(): NotificationDto[] {
    if (this.selectedCategory === 'all') return this.all;
    return this.all.filter(n => n.category === this.selectedCategory);
  }

  get filteredUnread(): NotificationDto[] {
    if (this.selectedCategory === 'all') return this.unread;
    return this.unread.filter(n => n.category === this.selectedCategory);
  }

  timeAgo(dateStr: string): string {
    // SQL Server returns UTC datetimes without 'Z'; append it so the browser parses as UTC
    const utc  = dateStr && !dateStr.endsWith('Z') && !dateStr.includes('+') ? dateStr + 'Z' : dateStr;
    const diff = Math.floor((Date.now() - new Date(utc).getTime()) / 1000);
    if (diff < 60)     return 'just now';
    if (diff < 3600)   return `${Math.floor(diff / 60)}m ago`;
    if (diff < 86400)  return `${Math.floor(diff / 3600)}h ago`;
    if (diff < 172800) return 'Yesterday';
    if (diff < 604800) return `${Math.floor(diff / 86400)}d ago`;
    return new Date(utc).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: '2-digit' });
  }

  prevAllPage()    { if (this.allPage > 1)                          this.loadAll(this.allPage - 1); }
  nextAllPage()    { if (this.allPage < this.totalAllPages)         this.loadAll(this.allPage + 1); }
  prevUnreadPage() { if (this.unreadPage > 1)                       this.loadUnread(this.unreadPage - 1); }
  nextUnreadPage() { if (this.unreadPage < this.totalUnreadPages)   this.loadUnread(this.unreadPage + 1); }

  get totalAllPages()    { return Math.ceil(this.allTotal / this.pageSize); }
  get totalUnreadPages() { return Math.ceil(this.unreadTotal / this.pageSize); }
}
