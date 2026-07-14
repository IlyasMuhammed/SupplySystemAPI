import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, Subject, firstValueFrom } from 'rxjs';
import * as signalR from '@microsoft/signalr';
import { AuthService } from '../pages/service/auth.service';

export interface NotificationDto {
  uuid:           string;
  userId:         number;
  type:           string;
  category:       string;
  title:          string;
  message:        string;
  entityType?:    string;
  entityUuid?:    string;
  navigationUrl?: string;
  isRead:         boolean;
  readAt?:        string;
  createdBy:      number;
  createdAt:      string;
}

export interface NotificationPage {
  data:         NotificationDto[];
  totalRecords: number;
  page:         number;
  pageSize:     number;
  totalPages:   number;
}

@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly BASE = 'https://localhost:51800/api/notifications';
  private readonly HUB  = 'https://localhost:51800/hubs/notifications';

  // ── Reactive state ──────────────────────────────────────────────────────────
  readonly notifications = signal<NotificationDto[]>([]);
  readonly unreadCount   = signal<number>(0);
  readonly isConnected   = signal<boolean>(false);

  private readonly _newNotif$ = new Subject<NotificationDto>();
  /** Emits each time a notification arrives via SignalR. Subscribe in page components to auto-refresh. */
  readonly onNewNotification$ = this._newNotif$.asObservable();

  private hub: signalR.HubConnection | null = null;

  constructor(
    private http: HttpClient,
    private auth: AuthService,
    private router: Router
  ) {}

  // ── SignalR ─────────────────────────────────────────────────────────────────

  connect(): void {
    if (this.hub) return;

    const token = this.auth.getToken();
    if (!token) return;

    this.hub = new signalR.HubConnectionBuilder()
      .withUrl(this.HUB, {
        accessTokenFactory: () => this.auth.getToken() ?? '',
        transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    this.hub.on('ReceiveNotification', (dto: NotificationDto) => {
      this.notifications.update(list => [dto, ...list].slice(0, 50));
      this.unreadCount.update(n => n + 1);
      this._newNotif$.next(dto);
    });

    this.hub.onreconnected(() => this.isConnected.set(true));
    this.hub.onclose(() => this.isConnected.set(false));

    this.hub.start()
      .then(() => this.isConnected.set(true))
      .catch(err => {
        this.isConnected.set(false);
        console.warn('[NotifService] SignalR connect failed:', err);
      });
  }

  disconnect(): void {
    this.hub?.stop();
    this.hub = null;
    this.isConnected.set(false);
  }

  // ── REST ────────────────────────────────────────────────────────────────────

  getList(page = 1, pageSize = 20, unreadOnly = false): Observable<any> {
    return this.http.get<any>(`${this.BASE}?page=${page}&pageSize=${pageSize}&unreadOnly=${unreadOnly}`);
  }

  markRead(uuids?: string[]): Observable<any> {
    return this.http.post<any>(`${this.BASE}/mark-read`, { uuids: uuids ?? null });
  }

  delete(uuid: string): Observable<any> {
    return this.http.delete<any>(`${this.BASE}/${uuid}`);
  }

  // ── Initialise (called once from AppLayout.ngOnInit) ───────────────────────

  async init(): Promise<void> {
    try {
      const [listRes, countRes] = await Promise.all([
        firstValueFrom(this.getList(1, 20)),
        firstValueFrom(this.http.get<any>(`${this.BASE}/unread-count`))
      ]);

      const page: NotificationPage = listRes?.result;
      if (page) this.notifications.set(page.data ?? []);

      const count: number = countRes?.result?.count ?? 0;
      this.unreadCount.set(count);
    } catch (err) {
      console.warn('[NotifService] init() failed:', err);
    }

    this.connect();
  }

  /** Re-fetch the unread count from the server and sync the badge signal. */
  async loadUnreadCount(): Promise<void> {
    try {
      const res = await firstValueFrom(this.http.get<any>(`${this.BASE}/unread-count`));
      this.unreadCount.set(res?.result?.count ?? 0);
    } catch { /* non-critical */ }
  }

  async markAllRead(): Promise<void> {
    await firstValueFrom(this.markRead());
    this.notifications.update(list => list.map(n => ({ ...n, isRead: true })));
    this.unreadCount.set(0);
  }

  async markOneRead(uuid: string): Promise<void> {
    if (!uuid) return;
    await firstValueFrom(this.markRead([uuid]));
    this.notifications.update(list =>
      list.map(n => n.uuid === uuid ? { ...n, isRead: true } : n));
    this.unreadCount.update(c => Math.max(0, c - 1));
  }

  navigateTo(n: NotificationDto): void {
    if (n.uuid && !n.isRead) this.markOneRead(n.uuid);
    if (n.navigationUrl) this.router.navigateByUrl(n.navigationUrl);
  }

  categoryIcon(category: string): string {
    const map: Record<string, string> = {
      Procurement: 'pi pi-shopping-cart',
      Finance:     'pi pi-dollar',
      Warehouse:   'pi pi-box',
      Logistics:   'pi pi-truck',
      System:      'pi pi-cog'
    };
    return map[category] ?? 'pi pi-bell';
  }

  categoryColor(category: string): string {
    const map: Record<string, string> = {
      Procurement: '#3b82f6',
      Finance:     '#10b981',
      Warehouse:   '#8b5cf6',
      Logistics:   '#f59e0b',
      System:      '#64748b'
    };
    return map[category] ?? '#64748b';
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
    return new Date(utc).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
  }
}
