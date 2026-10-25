import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  NavigationEnd,
  Router,
  RouterLink,
  RouterLinkActive,
  RouterOutlet,
} from '@angular/router';
import { filter } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';

interface NavigationItem {
  to: string;
  label: string;
  icon: 'grid' | 'users' | 'book' | 'video' | 'calendar' | 'bell' | 'clock' | 'chart' | 'search' | 'check' | 'server' | 'user' | 'shield' | 'log';
  visible?: boolean;
}

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="shell" [class.collapsed]="collapsed()">
      <aside class="sidebar" [class.open]="menuOpen()">
        <button
          type="button"
          class="sidebar-collapse"
          [attr.aria-label]="collapsed() ? 'Mở rộng thanh bên' : 'Thu gọn thanh bên'"
          (click)="collapsed.set(!collapsed())"
        >
          {{ collapsed() ? '›' : '‹' }}
        </button>

        <div class="sidebar-header">
          <div class="brand-mark" aria-hidden="true">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.3">
              <path d="M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7Z" />
              <circle cx="12" cy="12" r="3" />
            </svg>
          </div>
          <div class="brand-copy">
            <strong>HTBAM</strong>
            <small>Hệ thống theo dõi, phát hiện và đánh giá hành vi sinh viên</small>
          </div>
        </div>

        <nav class="sidebar-nav" aria-label="Điều hướng chính">
          <div class="nav-group">
            <p class="nav-heading">Không gian làm việc</p>
            @for (link of workLinks; track link.to) {
              @if (canSeeWorkLink(link)) {
              <a
                [routerLink]="link.to"
                routerLinkActive="active"
                [routerLinkActiveOptions]="{ exact: link.to === '/' }"
                [title]="collapsed() ? link.label : ''"
                (click)="menuOpen.set(false)"
              >
                <span class="nav-icon" aria-hidden="true">
                  @switch (link.icon) {
                    @case ('grid') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="7" height="7"/><rect x="14" y="3" width="7" height="7"/><rect x="3" y="14" width="7" height="7"/><rect x="14" y="14" width="7" height="7"/></svg> }
                    @case ('users') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75"/></svg> }
                    @case ('book') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2Z"/></svg> }
                    @case ('video') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polygon points="23 7 16 12 23 17 23 7"/><rect x="1" y="5" width="15" height="14" rx="2"/></svg> }
                    @case ('calendar') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4M8 2v4M3 10h18"/></svg> }
                    @case ('bell') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0"/></svg> }
                    @case ('clock') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/></svg> }
                    @case ('chart') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M18 20V10M12 20V4M6 20v-6"/></svg> }
                    @case ('search') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="8"/><path d="m21 21-4.35-4.35"/></svg> }
                    @case ('check') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="m20 6-11 11-5-5"/></svg> }
                    @case ('server') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="2" y="2" width="20" height="8" rx="2"/><rect x="2" y="14" width="20" height="8" rx="2"/><path d="M6 6h.01M6 18h.01"/></svg> }
                    @case ('user') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></svg> }
                    @case ('shield') { <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z"/></svg> }
                  }
                </span>
                <span class="nav-label">{{ link.label }}</span>
              </a>
              }
            }
          </div>

          @if (auth.isAdmin()) {
            <div class="nav-group">
              <p class="nav-heading">Quản trị</p>
              @for (link of adminLinks; track link.to) {
                <a [routerLink]="link.to" routerLinkActive="active" [title]="collapsed() ? link.label : ''" (click)="menuOpen.set(false)">
                  <span class="nav-icon" aria-hidden="true">
                    @if (link.icon === 'shield') {
                      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z"/></svg>
                    } @else {
                      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z"/><path d="M14 2v6h6M8 13h8M8 17h8"/></svg>
                    }
                  </span>
                  <span class="nav-label">{{ link.label }}</span>
                </a>
              }
            </div>
          }
        </nav>

        <div class="side-footer">
          <div class="user-summary">
            <div class="avatar">{{ initials() }}</div>
            <div class="user-meta">
              <strong>{{ auth.user().fullName || auth.user().userName || 'Người dùng' }}</strong>
              <small>{{ roleLabel() }}</small>
            </div>
            <button type="button" class="logout-button" title="Đăng xuất" aria-label="Đăng xuất" (click)="auth.logout()">
              <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2"><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9"/></svg>
            </button>
          </div>
        </div>
      </aside>

      <main class="app-main">
        <header class="topbar">
          <button type="button" class="menu-btn icon-button" aria-label="Mở menu" (click)="menuOpen.set(true)">
            <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 6h18M3 12h18M3 18h18"/></svg>
          </button>
          <div class="topbar-copy">
            <p class="topbar-title">Ứng dụng quản lý & phân tích hành vi sinh viên</p>
            <p class="breadcrumb">Trang chủ / {{ breadcrumb() }}</p>
          </div>
          <div class="topbar-actions">
            <span class="health-inline">
              <span class="pulse-dot" [style.background]="systemOk() === false ? '#f59e0b' : '#22c55e'"></span>
              {{ systemOk() === null ? 'Đang kiểm tra' : systemOk() ? 'Hệ thống ổn định' : 'Cần kiểm tra' }}
            </span>
            <button type="button" class="icon-button" aria-label="Thông báo" title="Thông báo">
              <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0"/></svg>
            </button>
            <div class="avatar" [title]="auth.user().fullName || ''">{{ initials() }}</div>
          </div>
        </header>
        <section class="content"><router-outlet /></section>
      </main>

      @if (menuOpen()) {
        <div class="overlay" role="button" tabindex="0" aria-label="Đóng menu" (click)="menuOpen.set(false)"></div>
      }
    </div>
  `,
})
export class ShellComponent implements OnInit {
  readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly menuOpen = signal(false);
  readonly collapsed = signal(false);
  readonly systemOk = signal<boolean | null>(null);
  readonly breadcrumb = signal('Tổng quan');

  readonly workLinks: NavigationItem[] = [
    { to: '/', label: 'Tổng quan', icon: 'grid' },
    { to: '/students', label: 'Sinh viên', icon: 'users' },
    { to: '/management', label: 'Danh mục', icon: 'book' },
    { to: '/organization', label: 'Cơ cấu & phân quyền', icon: 'shield' },
    { to: '/videos', label: 'Dữ liệu video', icon: 'video' },
    { to: '/sessions', label: 'Buổi học', icon: 'calendar' },
    { to: '/alerts', label: 'Cảnh báo', icon: 'bell' },
    { to: '/history', label: 'Lịch sử', icon: 'clock' },
    { to: '/reports', label: 'Báo cáo', icon: 'chart' },
    { to: '/search', label: 'Tìm kiếm', icon: 'search', visible: false },
    { to: '/attendance', label: 'Chuyên cần', icon: 'check' },
    { to: '/system', label: 'Trạng thái hệ thống', icon: 'server' },
    { to: '/account', label: 'Tài khoản', icon: 'user' },
  ];

  readonly adminLinks: NavigationItem[] = [
    { to: '/admin/users', label: 'Người dùng & phân quyền', icon: 'shield' },
    { to: '/admin/audit-logs', label: 'Nhật ký hệ thống', icon: 'log' },
  ];

  readonly initials = computed(() => {
    const name = this.auth.user().fullName || this.auth.user().userName || 'HTBAM';
    return name.split(/\s+/).filter(Boolean).slice(-2).map((part) => part[0]?.toUpperCase()).join('');
  });

  readonly roleLabel = computed(() => {
    const roles = this.auth.user().roles ?? [];
    if (roles.includes('ADMIN')) return 'Quản trị viên';
    if (roles.includes('LECTURER')) return 'Giảng viên';
    if (roles.includes('TECH_AI')) return 'Kỹ thuật AI';
    return roles.join(', ') || 'Người dùng';
  });

  canSeeWorkLink(
    link: NavigationItem,
  ): boolean {
    if (link.visible === false) {
      return false;
    }

    if (this.auth.isAdmin()) {
      return (
        link.to === '/management' ||
        link.to === '/system' ||
        link.to === '/account'
      );
    }

    if (
      link.to === '/organization'
    ) {
      return this.auth
        .canManageStructure();
    }

    return true;
  }

  constructor() {
    this.updateBreadcrumb(this.router.url);
    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((event) => this.updateBreadcrumb(event.urlAfterRedirects));
  }

  ngOnInit(): void {
    this.api.capabilities().subscribe({
      next: (capabilities) => this.systemOk.set(capabilities.backend && capabilities.database),
      error: () => this.systemOk.set(false),
    });
  }

  private updateBreadcrumb(url: string): void {
    const path = url.split('?')[0];
    const allLinks = [...this.workLinks, ...this.adminLinks];
    const exact = allLinks.find((item) => item.to === path);
    if (exact) {
      this.breadcrumb.set(exact.label);
      return;
    }
    if (path.startsWith('/students/')) this.breadcrumb.set('Đăng ký khuôn mặt');
    else if (path.startsWith('/sessions/')) this.breadcrumb.set('Chi tiết buổi học');
    else this.breadcrumb.set('Tổng quan');
  }
}
