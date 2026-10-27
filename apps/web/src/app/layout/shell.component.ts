import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  OnDestroy,
  OnInit,
  ViewChild,
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
import { OverlayFocus } from '../shared/overlay-focus';
import { catalogBreadcrumb } from '../shared/catalog-navigation';
import { NotificationCenterComponent } from '../shared/notification-center.component';

interface NavigationItem {
  to: string;
  label: string;
  icon: 'grid' | 'users' | 'book' | 'video' | 'calendar' | 'bell' | 'clock' | 'chart' | 'search' | 'check' | 'server' | 'user' | 'shield' | 'log';
  visible?: boolean;
  permission?: string;
  adminOnly?: boolean;
}

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, NotificationCenterComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="shell" [class.collapsed]="collapsed() && !mobile()">
      <aside #sidebar id="sidebar" class="sidebar" [class.open]="menuOpen()"
        [attr.role]="menuOpen() ? 'dialog' : null" [attr.aria-modal]="menuOpen() ? 'true' : null"
        aria-label="Thanh điều hướng Hệ thống giám sát" tabindex="-1">
        <button
          type="button"
          class="sidebar-collapse"
          [attr.aria-expanded]="!collapsed()"
          aria-controls="sidebar-navigation"
          [attr.aria-label]="collapsed() ? 'Mở rộng thanh bên' : 'Thu gọn thanh bên'"
          (click)="collapsed.set(!collapsed())"
        >
          {{ collapsed() ? '›' : '‹' }}
        </button>

        <div class="sidebar-header">
          <img class="huit-logo" src="assets/huit-logo.png" width="52" height="52" alt="Logo Trường Đại học Công Thương Thành phố Hồ Chí Minh" title="Hệ thống giám sát lớp học - Khoa Công nghệ Thông tin HUIT" />
          <div class="brand-copy">
            <strong>Hệ thống giám sát</strong>
            <small>Hệ thống theo dõi, phát hiện và đánh giá hành vi sinh viên</small>
          </div>
          <button type="button" class="drawer-close icon-button" aria-label="Đóng menu" (click)="closeMenu()">×</button>
        </div>

        <nav id="sidebar-navigation" class="sidebar-nav" aria-label="Điều hướng chính">
          <div class="nav-group">
            <p class="nav-heading">Không gian làm việc</p>
            @for (link of workLinks; track link.to) {
              @if (canSeeWorkLink(link)) {
              <a
                [routerLink]="link.to"
                routerLinkActive="active"
                [routerLinkActiveOptions]="{ exact: link.to === '/' }"
                [title]="link.label"
                [attr.aria-label]="link.label"
                (click)="closeMenu()"
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

          @if (auth.hasPermission('CATALOG_VIEW') || auth.hasPermission('CLASS_SECTION_VIEW')) {
            <div class="nav-group" aria-label="Danh mục">
              <p class="nav-heading">Danh mục</p>
              <a routerLink="/management" routerLinkActive="active" title="Danh mục dữ liệu" (click)="closeMenu()"><span class="nav-icon" aria-hidden="true">▤</span><span class="nav-label">Danh mục dữ liệu</span></a>
            </div>
          }
          @if (canSeeAdministrationLinks()) {
            <div class="nav-group">
              <p class="nav-heading">Quản trị</p>
              @for (link of adminLinks; track link.to) {
                @if (canSeeAdminLink(link)) {
                <a [routerLink]="link.to" routerLinkActive="active" [title]="link.label" [attr.aria-label]="link.label" (click)="closeMenu()">
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

      <main class="app-main" [attr.inert]="menuOpen() ? '' : null">
        <header class="topbar">
          <button type="button" class="menu-btn icon-button" aria-label="Mở menu" aria-controls="sidebar" [attr.aria-expanded]="menuOpen()" (click)="openMenu()">
            <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 6h18M3 12h18M3 18h18"/></svg>
          </button>
          <div class="topbar-copy">
            <p class="topbar-title">Ứng dụng quản lý & phân tích hành vi sinh viên</p>
            <p class="breadcrumb" [title]="'Trang chủ / ' + breadcrumb()">Trang chủ / {{ breadcrumb() }}</p>
          </div>
          <div class="topbar-actions">
            <span class="health-inline" [class.healthy]="systemOk() === true" [class.warning]="systemOk() === false" role="status">
              <span class="pulse-dot" aria-hidden="true"></span>
              {{ systemOk() === null ? 'Đang kiểm tra' : systemOk() ? 'Hệ thống ổn định' : 'Cần kiểm tra' }}
            </span>
            <app-notification-center [demo]="!!developmentWarning()" />
            @if (auth.isLoggedIn() && !auth.isAdmin()) {
            <a routerLink="/alerts" class="icon-button button-link" aria-label="Cảnh báo nghiệp vụ" title="Cảnh báo nghiệp vụ">
              <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0"/></svg>
            </a>
            }
            <div class="avatar" [title]="auth.user().fullName || ''">{{ initials() }}</div>
          </div>
        </header>
        <section class="content"><router-outlet /></section>
      </main>

      @if (menuOpen()) {
        <div class="overlay" aria-hidden="true" (click)="closeMenu()"></div>
      }
    </div>
  `,
})
export class ShellComponent implements OnInit, OnDestroy {
  readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private readonly drawerFocus = new OverlayFocus(this.document);
  @ViewChild('sidebar') private sidebar?: ElementRef<HTMLElement>;

  readonly mobile = signal(this.document.defaultView?.matchMedia('(max-width: 1023px)').matches ?? false);
  readonly menuOpen = signal(false);
  readonly collapsed = signal(false);
  readonly systemOk = signal<boolean | null>(null);
  readonly developmentWarning = signal<string | null>(null);
  readonly breadcrumb = signal('Tổng quan');

  readonly workLinks: NavigationItem[] = [
    { to: '/', label: 'Tổng quan', icon: 'grid' },
    { to: '/students', label: 'Sinh viên', icon: 'users' },
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
    { to: '/academic-configuration', label: 'Cấu hình học vụ', icon: 'calendar', permission: 'ACADEMIC_CONFIGURE' },
    { to: '/admin/users', label: 'Người dùng & phân quyền', icon: 'shield', adminOnly: true },
    { to: '/admin/audit-logs', label: 'Nhật ký hệ thống', icon: 'log', adminOnly: true },
  ];

  readonly initials = computed(() => {
    const name = this.auth.user().fullName || this.auth.user().userName || 'Hệ thống giám sát';
    return name.split(/\s+/).filter(Boolean).slice(-2).map((part) => part[0]?.toUpperCase()).join('');
  });

  readonly roleLabel = this.auth.positionLabel;
  private profileTimer?: ReturnType<typeof setInterval>;

  canSeeWorkLink(
    link: NavigationItem,
  ): boolean {
    if (link.visible === false) {
      return false;
    }

    if (this.auth.isAdmin()) {
      return (
        link.to === '/system' ||
        link.to === '/account' ||
        link.to === '/organization' ||
        link.to === '/management' ||
        link.to === '/reports'
      );
    }

    if (link.to === '/students') return this.auth.hasPermission('STUDENT_GLOBAL_VIEW');
    if (link.to === '/reports') return this.auth.hasPermission('REPORT_VIEW');
    if (link.to === '/class-sections') return this.auth.hasPermission('CLASS_SECTION_VIEW');
    if (link.to === '/management') return this.auth.hasPermission('CATALOG_VIEW');

    if (link.to === '/organization') {
      return this.auth
        .canManageStructure();
    }

    return true;
  }

  canSeeAdminLink(link: NavigationItem): boolean {
    if (link.adminOnly) return this.auth.isAdmin();
    return !link.permission || this.auth.hasPermission(link.permission);
  }

  canSeeAdministrationLinks(): boolean {
    return this.adminLinks.some((link) => this.canSeeAdminLink(link));
  }

  constructor() {
    this.updateBreadcrumb(this.router.url);
    this.router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((event) => {
        this.closeMenu();
        this.updateBreadcrumb(event.urlAfterRedirects);
      });
  }

  openMenu(): void {
    if (!this.mobile() || !this.sidebar) return;
    this.menuOpen.set(true);
    this.drawerFocus.open(this.sidebar.nativeElement, () => this.closeMenu());
  }

  closeMenu(): void {
    this.menuOpen.set(false);
    this.drawerFocus.close();
  }

  @HostListener('window:resize')
  onResize(): void {
    const mobile = this.document.defaultView?.matchMedia('(max-width: 1023px)').matches ?? false;
    if (mobile !== this.mobile()) {
      this.closeMenu();
      this.mobile.set(mobile);
    }
  }

  ngOnDestroy(): void {
    this.drawerFocus.close(false);
    clearInterval(this.profileTimer);
  }

  @HostListener('window:focus')
  refreshProfile(): void {
    if (this.auth.isLoggedIn()) this.auth.ensureProfile(true).subscribe({ error: () => undefined });
  }

  ngOnInit(): void {
    this.profileTimer = setInterval(() => {
      if (this.document.visibilityState === 'visible') this.refreshProfile();
    }, 30000);
    this.api.capabilities().subscribe({
      next: (capabilities) => { this.systemOk.set(capabilities.backend && capabilities.database); this.developmentWarning.set(capabilities.developmentDataWarning ?? null); },
      error: () => this.systemOk.set(false),
    });
  }

  private updateBreadcrumb(url: string): void {
    const path = url.split('?')[0];
    const catalog = catalogBreadcrumb(url);
    if(catalog){this.breadcrumb.set(catalog);return;}
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
