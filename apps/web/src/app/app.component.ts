import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: `
    @if (auth.profileLoading()) {
      <div class="app-loading" role="status" aria-live="polite">
        <div class="compact-brand" title="Hệ thống giám sát lớp học - Khoa Công nghệ Thông tin HUIT">
          <img src="assets/huit-logo.png" alt="Logo HUIT" class="compact-brand__logo" width="26" height="26" />
          <span class="compact-brand__name">Hệ thống giám sát</span>
        </div>
        <span class="spinner" aria-hidden="true"></span>
        Đang tải quyền truy cập…
      </div>
    }
    <router-outlet />
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppComponent {
  readonly auth = inject(AuthService);
}
