import { ChangeDetectionStrategy, Component, OnDestroy, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { errorText } from '../shared/ui';

@Component({
  standalone: true,
  imports: [ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="login-page">
      <section class="login-visual" aria-label="Giới thiệu HTBAM">
        <div class="login-brand">
          <div class="brand-mark" aria-hidden="true">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4"><path d="M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/></svg>
          </div>
          <div><strong>HTBAM</strong><small>Khoa Công nghệ Thông tin</small></div>
        </div>

        <div class="login-illustration" aria-hidden="true">
          <svg viewBox="0 0 520 360" fill="none">
            <rect x="38" y="52" width="444" height="270" rx="16" fill="#1e293b" stroke="#334155" stroke-width="2"/>
            <rect x="78" y="82" width="364" height="124" rx="8" fill="#0f172a" stroke="#2563eb" stroke-width="2"/>
            <path d="M106 174V116M128 174v-42M150 174v-28M172 174v-49M194 174v-35" stroke-linecap="round" stroke-width="9" stroke="#2563eb" opacity=".72"/>
            <path d="M228 119h88M228 134h68M228 149h98" stroke="#334155" stroke-width="5" stroke-linecap="round"/>
            <rect x="338" y="104" width="72" height="54" rx="8" fill="#1e293b" stroke="#60a5fa" stroke-width="2"/>
            <circle cx="374" cy="131" r="17" fill="#0f172a" stroke="#2563eb" stroke-width="2"/><circle cx="374" cy="131" r="5" fill="#2563eb"/><circle cx="400" cy="112" r="4" fill="#22c55e"/>
            @for (student of students; track student.x) {
              <g>
                <circle [attr.cx]="student.x + 21" cy="232" r="11" [attr.fill]="student.active ? '#2563eb' : '#475569'"/>
                <rect [attr.x]="student.x" y="247" width="42" height="28" rx="5" fill="#0f172a" stroke="#475569"/>
                <circle [attr.cx]="student.x + 34" cy="220" r="4" [attr.fill]="student.active ? '#22c55e' : '#f59e0b'"/>
              </g>
            }
            <path d="M374 159v62H214" stroke="#3b82f6" stroke-dasharray="5 5" opacity=".55"/>
            <rect x="80" y="287" width="130" height="13" rx="6" fill="#334155"/><rect x="220" y="287" width="82" height="13" rx="6" fill="#334155"/>
          </svg>
        </div>

        <div class="login-tagline">
          <h2>Phân tích lớp học thông minh,<br/><span>hỗ trợ giảng dạy hiệu quả hơn.</span></h2>
          <p>Theo dõi và phân tích hành vi sinh viên theo thời gian thực bằng các mô hình AI và dữ liệu nhận diện của HTBAM.</p>
        </div>
      </section>

      <main class="login-form-panel">
        <div class="login-wrap">
          <div class="login-mobile-brand login-brand">
            <div class="brand-mark">H</div><div><strong style="color:#0f172a">HTBAM</strong><small>Khoa Công nghệ Thông tin</small></div>
          </div>

          <form class="login-card" [formGroup]="form" (ngSubmit)="submit()" novalidate>
            <div>
              <h1>Đăng nhập hệ thống</h1>
              <p class="intro">Nhập thông tin tài khoản để tiếp tục</p>
            </div>

            @if (error()) { <div class="error-box" role="alert">{{ error() }}</div> }

            <label>
              <span>Tài khoản <span class="required" aria-hidden="true">*</span></span>
              <input formControlName="userName" autocomplete="username" placeholder="Nhập tên đăng nhập" required
                [attr.aria-invalid]="form.controls.userName.touched && form.controls.userName.invalid"
                [attr.aria-describedby]="form.controls.userName.touched && form.controls.userName.invalid ? 'username-error' : null" />
              @if (form.controls.userName.touched && form.controls.userName.hasError('required')) {
                <small id="username-error" class="field-error">Vui lòng nhập tài khoản.</small>
              }
            </label>

            <div class="field">
              <label for="login-password"><span>Mật khẩu <span class="required" aria-hidden="true">*</span></span></label>
              <div class="password-field">
              <input id="login-password" [type]="showPassword() ? 'text' : 'password'" formControlName="password" autocomplete="current-password" placeholder="Nhập mật khẩu" required
                [attr.aria-invalid]="form.controls.password.touched && form.controls.password.invalid"
                [attr.aria-describedby]="form.controls.password.touched && form.controls.password.invalid ? 'password-error' : null" />
              <button type="button" class="password-toggle" aria-controls="login-password" [attr.aria-pressed]="showPassword()" [attr.aria-label]="showPassword() ? 'Ẩn mật khẩu' : 'Hiện mật khẩu'" (click)="showPassword.set(!showPassword())">
                @if (showPassword()) {
                  <svg viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="2"><path d="M17.9 17.9A10 10 0 0 1 12 20c-7 0-11-8-11-8a18 18 0 0 1 5.1-5.9M9.9 4.2A9 9 0 0 1 12 4c7 0 11 8 11 8a18 18 0 0 1-2.2 3.2M1 1l22 22"/></svg>
                } @else {
                  <svg viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="2"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8Z"/><circle cx="12" cy="12" r="3"/></svg>
                }
              </button>
              </div>
              @if (form.controls.password.touched && form.controls.password.hasError('required')) {
                <small id="password-error" class="field-error">Vui lòng nhập mật khẩu.</small>
              }
            </div>

            <label class="check">
              <input type="checkbox" formControlName="remember" />
              Ghi nhớ phiên đăng nhập
            </label>

            <button class="login-submit" type="submit" [disabled]="loading() || cooldownSeconds() > 0">
              {{ loading() ? 'Đang đăng nhập…' : cooldownSeconds() > 0 ? 'Thử lại sau ' + cooldownSeconds() + ' giây' : 'Đăng nhập' }}
            </button>
          </form>
          <p class="login-footer">© 2026 HTBAM — Khoa Công nghệ Thông tin</p>
        </div>
      </main>
    </div>
  `,
})
export class LoginComponent implements OnDestroy {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly loading = signal(false);
  readonly error = signal('');
  readonly showPassword = signal(false);
  readonly cooldownSeconds = signal(0);
  private readonly cooldownKey = 'htbam_login_rate_limit_until';
  private cooldownDeadline = 0;
  private countdownTimer?: ReturnType<typeof setInterval>;
  readonly students = [
    { x: 80, active: true },
    { x: 142, active: false },
    { x: 204, active: true },
    { x: 266, active: true },
    { x: 328, active: false },
    { x: 390, active: true },
  ];

  readonly form = this.fb.nonNullable.group({
    userName: ['', Validators.required],
    password: ['', Validators.required],
    remember: [false],
  });

  constructor() {
    this.restoreCooldown();
  }

  submit(): void {
    if (
      this.form.invalid ||
      this.loading() ||
      this.cooldownSeconds() > 0
    ) {
      this.form.markAllAsTouched();
      return;
    }

    this.loading.set(true);
    this.error.set('');
    const value = this.form.getRawValue();

    this.auth.login(value.userName.trim(), value.password).subscribe({
      next: () => {
        this.clearStoredCooldown();
        this.loading.set(false);
        void this.router.navigateByUrl('/');
      },
      error: (error) => {
        this.loading.set(false);

        if (Number(error?.status) === 401) {
          this.error.set('Tài khoản hoặc mật khẩu không đúng.');
          return;
        }

        if (Number(error?.status) === 429) {
          const bodySeconds = Number(
            error?.error?.retryAfterSeconds,
          );
          const headerSeconds = Number(
            error?.headers?.get?.('Retry-After'),
          );
          const seconds =
            Number.isFinite(bodySeconds) && bodySeconds > 0
              ? Math.ceil(bodySeconds)
              : Number.isFinite(headerSeconds) && headerSeconds > 0
                ? Math.ceil(headerSeconds)
                : 60;

          this.startCooldown(seconds);
          return;
        }

        this.error.set(errorText(error));
      },
    });
  }

  ngOnDestroy(): void {
    this.stopCountdown();
  }

  private startCooldown(seconds: number): void {
    this.stopCountdown();
    this.cooldownDeadline = Date.now() + Math.max(1, seconds) * 1000;
    try {
      sessionStorage.setItem(this.cooldownKey, String(this.cooldownDeadline));
    } catch {
      // Countdown vẫn hoạt động trong phiên hiện tại nếu storage bị chặn.
    }
    this.runCountdown();
  }

  private runCountdown(): void {
    this.refreshCooldown();
    if (this.cooldownSeconds() === 0) return;
    this.countdownTimer = setInterval(() => {
      this.refreshCooldown();
    }, 1000);
  }

  private refreshCooldown(): void {
    const remaining = Math.max(
      0,
      Math.ceil((this.cooldownDeadline - Date.now()) / 1000),
    );
    this.cooldownSeconds.set(remaining);
    if (remaining > 0) {
      this.updateCooldownMessage();
      return;
    }

    this.stopCountdown();
    this.clearStoredCooldown();
    this.error.set('Bạn có thể thử đăng nhập lại.');
  }

  private restoreCooldown(): void {
    try {
      const storedDeadline = Number(sessionStorage.getItem(this.cooldownKey));
      if (Number.isFinite(storedDeadline) && storedDeadline > Date.now()) {
        this.cooldownDeadline = storedDeadline;
        this.runCountdown();
      } else {
        sessionStorage.removeItem(this.cooldownKey);
      }
    } catch {
      // Trình duyệt có thể chặn sessionStorage; login vẫn hoạt động bình thường.
    }
  }

  private updateCooldownMessage(): void {
    this.error.set(
      `Bạn đã thử đăng nhập quá nhiều lần. Vui lòng thử lại sau ${this.cooldownSeconds()} giây.`,
    );
  }

  private stopCountdown(): void {
    if (this.countdownTimer) {
      clearInterval(this.countdownTimer);
      this.countdownTimer = undefined;
    }
  }

  private clearStoredCooldown(): void {
    this.cooldownDeadline = 0;
    try {
      sessionStorage.removeItem(this.cooldownKey);
    } catch {
      // Không cần chặn luồng đăng nhập nếu storage không khả dụng.
    }
  }
}
