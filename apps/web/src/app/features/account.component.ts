import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AuthService } from '../core/auth.service';
import { UserInfo } from '../core/models';
import { PageTitleComponent, errorText, roleText, statusText } from '../shared/ui';

@Component({
  standalone: true,
  imports: [ReactiveFormsModule, PageTitleComponent],
  template: `
    <app-page-title
      title="Tài khoản"
      subtitle="Thông tin người dùng và đổi mật khẩu"
    />

    @if (error()) {
      <div class="error-box">{{ error() }}</div>
    }
    @if (message()) {
      <div class="success-box">{{ message() }}</div>
    }

    <div class="profile-grid">
      <section class="card profile-card">
        @if (me()) {
          <div class="avatar large">{{ initials() }}</div>
          <h2>{{ me()!.fullName }}</h2>
          <p>{{ me()!.email || 'Chưa có email' }}</p>
          <span class="badge blue">{{ auth.positionLabel() }}</span>
          <div class="profile-list">
            <div><span>Tên đăng nhập</span><strong>{{ me()!.userName }}</strong></div>
            <div><span>Trạng thái</span><strong>{{ statusText(me()!.status) }}</strong></div>
            <div><span>Chức vụ</span><strong>{{ auth.positionLabel() }}</strong></div>
          </div>
        }
      </section>

      <section class="card">
        <h3>Đổi mật khẩu</h3>
        <form class="stack" style="max-width:520px" [formGroup]="form" (ngSubmit)="save()">
          <label>
            Mật khẩu hiện tại
            <input type="password" formControlName="current" />
          </label>
          <label>
            Mật khẩu mới
            <input type="password" formControlName="next" />
          </label>
          <label>
            Xác nhận
            <input type="password" formControlName="confirm" />
          </label>
          <div class="form-actions" style="justify-content:flex-start"><button [disabled]="form.invalid">Đổi mật khẩu</button><button type="button" class="secondary" (click)="form.reset()">Hủy</button></div>
        </form>
      </section>
    </div>
  `,
})
export class AccountComponent implements OnInit {
  readonly auth = inject(AuthService);
  private readonly fb = inject(FormBuilder);

  readonly me = signal<UserInfo | null>(null);
  readonly error = signal('');
  readonly message = signal('');
  readonly statusText = statusText;
  readonly form = this.fb.nonNullable.group({
    current: ['', Validators.required],
    next: ['', Validators.required],
    confirm: ['', Validators.required],
  });

  ngOnInit(): void {
    this.auth.me().subscribe({
      next: (user) => this.me.set(user),
      error: (error) => this.error.set(errorText(error)),
    });
  }

  initials(): string {
    const name = this.me()?.fullName || this.me()?.userName || 'HTBAM';
    return name.split(/\s+/).filter(Boolean).slice(-2).map((part) => part[0]?.toUpperCase()).join('');
  }

  roles(): string {
    return this.me()?.roles.map(roleText).join(', ') || 'Chưa phân quyền';
  }

  save(): void {
    const value = this.form.getRawValue();

    if (value.next !== value.confirm) {
      this.error.set('Xác nhận mật khẩu không khớp.');
      return;
    }

    this.auth.changePassword(value.current, value.next).subscribe({
      next: () => {
        this.message.set(
          'Đổi mật khẩu thành công. Vui lòng đăng nhập lại.',
        );
        setTimeout(() => this.auth.clear(), 1200);
      },
      error: (error) => this.error.set(errorText(error)),
    });
  }
}
