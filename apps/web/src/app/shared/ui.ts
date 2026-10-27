import { DOCUMENT } from '@angular/common';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  EventEmitter,
  HostListener,
  Input,
  OnDestroy,
  Output,
  ViewChild,
  inject,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { OverlayFocus } from './overlay-focus';

@Component({
  selector: 'app-page-title',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page-title">
      <div>
        <h1>{{ title }}</h1>

        @if (subtitle) {
          <p>{{ subtitle }}</p>
        }
      </div>

      <div class="actions">
        <ng-content />
      </div>
    </div>
  `,
})
export class PageTitleComponent {
  @Input({ required: true })
  title = '';

  @Input()
  subtitle = '';
}

@Component({
  selector: 'app-back-button',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="back-button" [routerLink]="to">
      <span class="back-button-icon" aria-hidden="true">←</span>
      <span>{{ label }}</span>
    </a>
  `,
})
export class BackButtonComponent {
  @Input({ required: true })
  to: string | readonly unknown[] = '/';

  @Input()
  label = 'Quay lại';
}

@Component({
  selector: 'app-modal',
  standalone: true,
  template: `
    <div
      class="modal-backdrop"
      (mousedown)="onBackdrop($event)"
    >
      <section
        #dialog
        class="modal-dialog"
        [class.modal-sm]="size === 'sm'"
        [class.modal-lg]="size === 'lg'"
        role="dialog"
        aria-modal="true"
        [attr.aria-label]="title"
        tabindex="-1"
      >
        <header>
          <h3>{{ title }}</h3>

          <button
            type="button"
            class="modal-close"
            [disabled]="busy"
            (click)="requestClose()"
            aria-label="Đóng"
          >
            ×
          </button>
        </header>

        <div class="modal-body">
          <ng-content />
        </div>
      </section>
    </div>
  `,
})
export class ModalComponent implements AfterViewInit, OnDestroy {
  private readonly document = inject(DOCUMENT);
  private readonly focus = new OverlayFocus(this.document);

  @Input()
  title = '';

  @Input()
  busy = false;

  @Input()
  size: 'sm' | 'md' | 'lg' = 'md';

  @Output()
  close = new EventEmitter<void>();

  @ViewChild('dialog')
  private dialog?: ElementRef<HTMLElement>;

  ngAfterViewInit(): void {
    const dialog = this.dialog?.nativeElement;
    if (!dialog) return;
    const firstField = dialog.querySelector<HTMLElement>(
      '.modal-body [autofocus], .modal-body input:not([type="hidden"]):not(:disabled), .modal-body select:not(:disabled), .modal-body textarea:not(:disabled)',
    );
    this.focus.open(dialog, () => this.requestClose(), firstField);
  }

  onBackdrop(event: MouseEvent): void {
    if (event.target === event.currentTarget) {
      this.requestClose();
    }
  }

  requestClose(): void {
    if (!this.busy) {
      this.close.emit();
    }
  }

  ngOnDestroy(): void {
    this.focus.close();
  }
}

@Component({
  selector: 'app-row-action-menu',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      #trigger
      type="button"
      class="icon-button"
      aria-label="Thao tác"
      aria-haspopup="menu"
      [attr.aria-expanded]="open"
      (click)="toggle($event)"
      (keydown)="onTriggerKeydown($event)"
    >⋮</button>

    <span #panelHost class="row-action-menu-panel-host">
      <div
        #menu
        class="action-menu action-menu-overlay"
        role="menu"
        hidden
        (click)="onMenuClick($event)"
        (keydown)="onMenuKeydown($event)"
      >
        <ng-content />
      </div>
    </span>
  `,
  host: { class: 'row-action-menu' },
})
export class RowActionMenuComponent implements OnDestroy {
  private static active?: RowActionMenuComponent;
  private readonly document = inject(DOCUMENT);
  @ViewChild('panelHost', { static: true }) private panelHost?: ElementRef<HTMLElement>;
  @ViewChild('menu', { static: true }) private menu?: ElementRef<HTMLElement>;

  open = false;
  private anchor?: HTMLElement;
  private animationFrame?: number;
  private listening = false;
  private readonly scrollListener = () => this.close();
  private readonly resizeListener = () => this.queuePosition();
  private readonly viewportScrollListener = () => this.queuePosition();
  private readonly actionCaptureListener = (event: Event) => {
    const target = event.target;
    if (!(target instanceof Element)) return;
    const action = target.closest<HTMLElement>('button, a[href], [role="menuitem"]');
    if (!action || action.getAttribute('aria-disabled') === 'true') return;
    if (action instanceof HTMLButtonElement && action.disabled) return;
    this.close();
  };

  toggle(event: MouseEvent): void {
    event.stopPropagation();
    if (this.open) {
      this.close();
      return;
    }
    RowActionMenuComponent.active?.close();
    RowActionMenuComponent.active = this;
    const anchor = event.currentTarget as HTMLElement | null;
    if (!anchor) {
      this.close();
      return;
    }
    this.anchor = anchor;
    this.open = true;
    anchor.setAttribute('aria-expanded', 'true');

    const menu = this.menu?.nativeElement;
    if (!menu) {
      this.close();
      return;
    }
    menu.hidden = false;
    menu.style.visibility = 'hidden';
    menu.style.top = '0px';
    menu.style.left = '0px';
    menu.querySelectorAll<HTMLElement>('button, a').forEach((item) => {
      if (!item.hasAttribute('role')) item.setAttribute('role', 'menuitem');
    });
    this.document.body.appendChild(menu);
    this.listen();
    this.queuePosition();
  }

  onTriggerKeydown(event: KeyboardEvent): void {
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
    event.preventDefault();
    if (!this.open) {
      this.toggleFromKeyboard(event.currentTarget as HTMLElement | null);
    }
    this.queueFocus(event.key === 'ArrowUp' ? 'last' : 'first');
  }

  onMenuClick(event: MouseEvent): void {
    event.stopPropagation();
  }

  onMenuKeydown(event: KeyboardEvent): void {
    const items = this.menuItems();
    if (!items.length) return;

    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.close(true);
      return;
    }

    if (event.key === 'Tab') {
      this.close();
      return;
    }

    const currentIndex = items.indexOf(this.document.activeElement as HTMLElement);
    let nextIndex: number | undefined;
    if (event.key === 'ArrowDown') nextIndex = (currentIndex + 1) % items.length;
    if (event.key === 'ArrowUp') nextIndex = (currentIndex - 1 + items.length) % items.length;
    if (event.key === 'Home') nextIndex = 0;
    if (event.key === 'End') nextIndex = items.length - 1;
    if (nextIndex == null) return;

    event.preventDefault();
    items[nextIndex].focus();
  }

  @HostListener('document:click')
  close(restoreFocus = false): void {
    const anchor = this.anchor;
    if (this.animationFrame != null) {
      this.document.defaultView?.cancelAnimationFrame(this.animationFrame);
      this.animationFrame = undefined;
    }
    this.open = false;
    this.anchor?.setAttribute('aria-expanded', 'false');
    this.anchor = undefined;

    const menu = this.menu?.nativeElement;
    const panelHost = this.panelHost?.nativeElement;
    if (menu) {
      menu.hidden = true;
      menu.style.visibility = 'hidden';
      if (panelHost && menu.parentElement !== panelHost) panelHost.appendChild(menu);
    }
    if (RowActionMenuComponent.active === this) RowActionMenuComponent.active = undefined;
    this.unlisten();
    if (restoreFocus && anchor?.isConnected) anchor.focus();
  }

  @HostListener('document:keydown.escape', ['$event'])
  closeOnEscape(event: KeyboardEvent): void {
    if (!this.open) return;
    event.preventDefault();
    this.close(true);
  }

  ngOnDestroy(): void {
    this.close();
  }

  private positionMenu(): void {
    this.animationFrame = undefined;
    const anchor = this.anchor;
    const menu = this.menu?.nativeElement;
    const view = this.document.defaultView;
    if (!this.open || !anchor?.isConnected || !menu || !view) {
      this.close();
      return;
    }

    const margin = 8;
    const gap = 6;
    const buttonRect = anchor.getBoundingClientRect();
    const menuRect = menu.getBoundingClientRect();
    const visualViewport = view.visualViewport;
    const viewportLeft = visualViewport?.offsetLeft ?? 0;
    const viewportTop = visualViewport?.offsetTop ?? 0;
    const viewportWidth = visualViewport?.width ?? view.innerWidth;
    const viewportHeight = visualViewport?.height ?? view.innerHeight;
    const minLeft = viewportLeft + margin;
    const minTop = viewportTop + margin;
    const maxLeft = Math.max(minLeft, viewportLeft + viewportWidth - menuRect.width - margin);
    const maxTop = Math.max(minTop, viewportTop + viewportHeight - menuRect.height - margin);
    const left = Math.max(minLeft, Math.min(buttonRect.right - menuRect.width, maxLeft));
    const opensBelow = viewportTop + viewportHeight - buttonRect.bottom - margin >= menuRect.height + gap;
    const preferredTop = opensBelow
      ? buttonRect.bottom + gap
      : buttonRect.top - menuRect.height - gap;
    const top = Math.max(minTop, Math.min(preferredTop, maxTop));

    menu.style.left = `${Math.round(left)}px`;
    menu.style.top = `${Math.round(top)}px`;
    menu.style.visibility = 'visible';
  }

  private listen(): void {
    if (this.listening) return;
    this.document.addEventListener('scroll', this.scrollListener, true);
    this.document.defaultView?.addEventListener('resize', this.resizeListener);
    this.document.defaultView?.visualViewport?.addEventListener('resize', this.resizeListener);
    this.document.defaultView?.visualViewport?.addEventListener('scroll', this.viewportScrollListener);
    this.menu?.nativeElement.addEventListener('click', this.actionCaptureListener, true);
    this.listening = true;
  }

  private unlisten(): void {
    if (!this.listening) return;
    this.document.removeEventListener('scroll', this.scrollListener, true);
    this.document.defaultView?.removeEventListener('resize', this.resizeListener);
    this.document.defaultView?.visualViewport?.removeEventListener('resize', this.resizeListener);
    this.document.defaultView?.visualViewport?.removeEventListener('scroll', this.viewportScrollListener);
    this.menu?.nativeElement.removeEventListener('click', this.actionCaptureListener, true);
    this.listening = false;
  }

  private queuePosition(): void {
    const view = this.document.defaultView;
    if (!view || !this.open) return;
    if (this.animationFrame != null) view.cancelAnimationFrame(this.animationFrame);
    this.animationFrame = view.requestAnimationFrame(() => this.positionMenu());
  }

  private toggleFromKeyboard(anchor: HTMLElement | null): void {
    if (!anchor) return;
    this.toggle({
      currentTarget: anchor,
      stopPropagation: () => undefined,
    } as unknown as MouseEvent);
  }

  private queueFocus(position: 'first' | 'last'): void {
    const view = this.document.defaultView;
    if (!view) return;
    view.requestAnimationFrame(() => {
      view.requestAnimationFrame(() => {
        const items = this.menuItems();
        const item = position === 'first' ? items[0] : items.at(-1);
        item?.focus();
      });
    });
  }

  private menuItems(): HTMLElement[] {
    const menu = this.menu?.nativeElement;
    if (!menu) return [];
    return Array.from(
      menu.querySelectorAll<HTMLElement>('button, a[href], [role="menuitem"]'),
    ).filter(
      (item) =>
        !item.hidden &&
        item.getAttribute('aria-disabled') !== 'true' &&
        (!(item instanceof HTMLButtonElement) || !item.disabled) &&
        item.getClientRects().length > 0,
    );
  }
}

export function errorText(error: any): string {
  const status = Number(error?.status ?? 0);
  const body = error?.error;
  const code =
    body && typeof body === 'object' && typeof body.code === 'string'
      ? body.code
      : '';

  const codeMessages: Record<string, string> = {
    INVALID_CREDENTIALS:
      'Tài khoản hoặc mật khẩu không đúng.',
    LOGIN_RATE_LIMITED:
      'Bạn đã thử đăng nhập quá nhiều lần. Vui lòng chờ rồi thử lại.',
    DUPLICATE_FIELDS:
      'Không thể lưu vì có thông tin bị trùng.',
  };

  if (codeMessages[code]) {
    const seconds = safeRetrySeconds(body?.retryAfterSeconds);
    return code === 'LOGIN_RATE_LIMITED' && seconds
      ? `Bạn đã thử đăng nhập quá nhiều lần. Vui lòng thử lại sau ${seconds} giây.`
      : codeMessages[code];
  }

  if ([500, 502, 503, 504].includes(status)) {
    return status === 500
      ? 'Hệ thống gặp lỗi khi xử lý yêu cầu. Vui lòng thử lại sau.'
      : 'Dịch vụ hiện chưa sẵn sàng. Vui lòng thử lại sau.';
  }

  const message = safeApiMessage(
    body && typeof body === 'object'
      ? body.message
      : body,
  );

  if (message) {
    if (
      status === 409 &&
      /video.*trùng.*nội dung/i.test(message)
    ) {
      return 'Video này trùng nội dung với một video đã được tải lên trước đó.';
    }

    return message;
  }

  const validation = validationText(body?.errors);

  if (validation) {
    return validation;
  }

  const statusMessages: Record<number, string> = {
    0: 'Không thể kết nối đến hệ thống. Vui lòng kiểm tra kết nối và thử lại.',
    400: 'Dữ liệu nhập chưa hợp lệ. Vui lòng kiểm tra lại.',
    401: 'Phiên đăng nhập không hợp lệ hoặc đã hết hạn. Vui lòng đăng nhập lại.',
    403: 'Bạn không có quyền thực hiện thao tác này.',
    404: 'Không tìm thấy dữ liệu yêu cầu.',
    409: 'Dữ liệu bị trùng hoặc đang được sử dụng. Vui lòng kiểm tra lại.',
    413: 'Tệp tải lên vượt quá dung lượng cho phép.',
    429: 'Bạn thao tác quá nhiều lần. Vui lòng chờ rồi thử lại.',
    500: 'Hệ thống gặp lỗi khi xử lý yêu cầu. Vui lòng thử lại sau.',
    502: 'Dịch vụ hiện chưa sẵn sàng. Vui lòng thử lại sau.',
    503: 'Dịch vụ hiện chưa sẵn sàng. Vui lòng thử lại sau.',
    504: 'Dịch vụ hiện chưa sẵn sàng. Vui lòng thử lại sau.',
  };

  return statusMessages[status] ??
    'Đã xảy ra lỗi. Vui lòng thử lại.';
}

export function apiFieldErrors(error: any): Record<string, string> {
  const errors = error?.error?.errors;
  if (Array.isArray(errors)) {
    return errors.reduce((result: Record<string, string>, item: any) => {
      if (typeof item?.field === 'string' && typeof item?.message === 'string') {
        result[item.field] = item.message;
      }
      return result;
    }, {});
  }
  if (errors && typeof errors === 'object') {
    return Object.entries(errors).reduce((result: Record<string, string>, [field, messages]) => {
      const message = Array.isArray(messages) ? messages.find((value) => typeof value === 'string') : messages;
      if (typeof message === 'string') result[field] = message;
      return result;
    }, {});
  }
  const field = error?.error?.field;
  const message = error?.error?.message;
  return typeof field === 'string' && typeof message === 'string'
    ? { [field]: message }
    : {};
}

export function statusText(status: string | null | undefined): string {
  if (!status) return '—';
  const labels: Record<string, string> = {
    ACTIVE: 'Hoạt động',
    INACTIVE: 'Ngừng hoạt động',
    LOCKED: 'Đã khóa',
    READY: 'Sẵn sàng',
    RUNNING: 'Đang diễn ra',
    STARTING: 'Đang khởi động',
    PROCESSING: 'Đang xử lý',
    UPLOADED: 'Chờ xử lý',
    COMPLETED: 'Đã hoàn tất',
    CANCELLED: 'Đã hủy',
    DRAFT: 'Bản nháp',
    FINALIZING: 'Đang hoàn tất',
    FINALIZE_FAILED: 'Hoàn tất thất bại',
    FAILED: 'Lỗi',
    OPEN: 'Đang mở',
    ACK: 'Đã xác nhận',
    ACKNOWLEDGED: 'Đã xác nhận',
    CLOSED: 'Đã đóng',
    ONLINE: 'Trực tuyến',
    OFFLINE: 'Ngoại tuyến',
    UNKNOWN: 'Chưa xác định',
    IN_PROGRESS: 'Đang tải ảnh',
    NEEDS_RETAKE: 'Cần chụp lại',
    PENDING_AI: 'Chờ AI xử lý',
    CONNECTING: 'Đang kết nối',
    NOT_CONFIGURED: 'Chưa cấu hình',
    IDENTIFIED: 'Đã nhận diện',
    UNIDENTIFIED: 'Chưa xác định',
  };
  return labels[status] ?? status;
}

export function roleText(role: string): string {
  return ({
    ADMIN: 'Quản trị hệ thống',
    LECTURER: 'Giảng viên',
    TECH_AI: 'Kỹ thuật AI',
  } as Record<string, string>)[role] ?? role;
}

export function behaviorText(value: string | null | undefined): string {
  if (!value) return 'Không quan sát được';
  return ({
    FOCUSED: 'Tập trung',
    DISTRACTED: 'Mất tập trung',
    SLEEPY: 'Buồn ngủ',
    ACTIVE: 'Hoạt động',
    PHONE_USE: 'Sử dụng điện thoại',
  } as Record<string, string>)[value] ?? value;
}

function safeApiMessage(value: unknown): string | null {
  if (typeof value !== 'string') {
    return null;
  }

  const message = value.trim();

  if (
    !message ||
    message.length > 500 ||
    /^[{\[]/.test(message) ||
    /http failure response|https?:\/\/|\/api\/|status\s*text|stack\s*trace|\bexception\b|\b(typeerror|referenceerror|syntaxerror|sqlexception|httprequestexception)\b|\bat\s+[\w.]+\(|connection\s*string|server\s*=|password\s*=|<html|bad gateway|nginx/i.test(message)
  ) {
    return null;
  }

  return message;
}

function validationText(value: unknown): string | null {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const messages = Object.values(value as Record<string, unknown>)
    .flatMap((item) => Array.isArray(item) ? item : [item])
    .map(safeApiMessage)
    .filter((item): item is string => !!item)
    .slice(0, 4);

  return messages.length
    ? `Vui lòng kiểm tra dữ liệu: ${messages.join(' ')}`
    : null;
}

function safeRetrySeconds(value: unknown): number | null {
  const seconds = Number(value);
  return Number.isFinite(seconds) && seconds > 0
    ? Math.ceil(seconds)
    : null;
}

export function fmtDate(
  value?: string | null,
): string {
  return value
    ? new Date(/Z$|[+-]\d\d:\d\d$/.test(value) || value.length <= 10 ? value : value + 'Z').toLocaleString('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh' })
    : '—';
}
