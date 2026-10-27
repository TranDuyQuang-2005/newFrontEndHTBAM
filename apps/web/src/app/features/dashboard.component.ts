import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { catchError, forkJoin, of } from 'rxjs';
import { ApiService } from '../core/api.service';
import { Alert, Capabilities, Session } from '../core/models';
import { PageTitleComponent, behaviorText, errorText, fmtDate, statusText } from '../shared/ui';

@Component({
  standalone: true,
  imports: [PageTitleComponent, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-title title="Tổng quan" [subtitle]="todayLabel()">
      @if (runningSessions()) {
        <span class="badge">{{ runningSessions() }} buổi học đang diễn ra</span>
      }
    </app-page-title>

    @if (countsError()) { <div class="error-box">Thống kê: {{ countsError() }}</div> }

    <div class="stats">
      @for (card of cards(); track card.key; let index = $index) {
        <section class="stat-card">
          <div class="stat-card-head">
            <div class="stat-icon" [class.purple]="index === 1" [class.sky]="index === 2" [class.green]="index === 3" [class.amber]="index === 4" [class.red]="index === 5">
              @switch (card.key) {
                @case ('students') { <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.9"/></svg> }
                @case ('classSections') { <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2Z"/></svg> }
                @case ('sessions') { <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4M8 2v4M3 10h18"/></svg> }
                @case ('cameras') { <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><polygon points="23 7 16 12 23 17 23 7"/><rect x="1" y="5" width="15" height="14" rx="2"/></svg> }
                @case ('videos') { <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><path d="m10 8 6 4-6 4Z"/></svg> }
                @default { <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2"><path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z"/><path d="M12 9v4M12 17h.01"/></svg> }
              }
            </div>
          </div>
          <span>{{ card.label }}</span>
          <strong>{{ card.value }}</strong>
          <small>{{ card.subtext }}</small>
        </section>
      }
    </div>

    <div class="dashboard-grid">
      <section class="card">
        <div class="card-header" style="margin:-20px -20px 16px">
          <div><h3>Buổi học gần đây</h3><p>Dữ liệu trực tiếp từ hệ thống</p></div>
          <a class="button-link secondary small" routerLink="/sessions">Xem tất cả</a>
        </div>
        <div class="table-wrap">
          @if (sessionsError()) { <div class="error-box">{{ sessionsError() }}</div> }
          <table>
            <thead><tr><th>Buổi học</th><th>Lớp học phần</th><th>Thời gian</th><th>Trạng thái</th></tr></thead>
            <tbody>
              @for (session of recentSessions(); track session.id) {
                <tr>
                  <td class="cell-title">#{{ session.id }}</td>
                  <td>{{ session.classSectionId }}</td>
                  <td>{{ date(session.scheduledStart) }}</td>
                  <td><span class="badge" [class.gray]="session.status === 'COMPLETED'" [class.blue]="session.status === 'DRAFT' || session.status === 'READY'">{{ sessionStatus(session.status) }}</span></td>
                </tr>
              } @empty {
                <tr><td colspan="4" class="empty">Chưa có buổi học để hiển thị.</td></tr>
              }
            </tbody>
          </table>
        </div>
      </section>

      <div>
        <section class="card">
          <div class="card-header" style="margin:-20px -20px 16px"><div><h3>Hoạt động hôm nay</h3><p>Cảnh báo đang được theo dõi</p></div></div>
          @if (alertsError()) { <div class="error-box">{{ alertsError() }}</div> }
          @for (alert of recentAlerts(); track alert.id) {
            <div class="service-card" style="margin-bottom:8px">
              <span class="status-dot" [style.background]="alert.status === 'OPEN' ? '#f59e0b' : '#16a34a'"></span>
              <div><strong>{{ behaviorLabel(alert.type) }}</strong><small>Buổi học #{{ alert.sessionId }} · {{ date(alert.createdAt) }}</small></div>
            </div>
          } @empty {
            <div class="empty-state" style="min-height:140px"><div><strong>Không có cảnh báo gần đây</strong><span>Hệ thống chưa trả về hoạt động cần chú ý.</span></div></div>
          }
        </section>

        <section class="card">
          <div class="card-header" style="margin:-20px -20px 16px"><div><h3>Tình trạng dịch vụ</h3><p>Dữ liệu kiểm tra năng lực hiện tại</p></div></div>
          @if (capabilitiesError()) { <div class="error-box">{{ capabilitiesError() }}</div> }
          <div class="service-grid service-grid-two">
            @for (service of services(); track service.name) {
              <div class="service-card"><span class="status-dot" [style.background]="service.ok ? '#16a34a' : '#dc2626'"></span><div><strong>{{ service.name }}</strong><small>{{ service.ok ? 'Hoạt động' : 'Cần kiểm tra' }}</small></div></div>
            }
          </div>
        </section>
      </div>
    </div>

    <section class="card">
      <div class="card-header" style="margin:-20px -20px 16px"><div><h3>Phân tích hành vi</h3><p>Tổng hợp hành vi được hiển thị trong báo cáo theo buổi học, sinh viên hoặc lớp học phần.</p></div><a class="button-link secondary small" routerLink="/reports">Mở báo cáo</a></div>
      <div class="empty-state" style="min-height:120px"><div><strong>Chọn phạm vi báo cáo để xem phân tích</strong><span>HTBAM không dùng dữ liệu mô phỏng trên màn tổng quan.</span></div></div>
    </section>
  `,
})
export class DashboardComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly counts = signal<Record<string, number>>({});
  readonly capabilities = signal<Capabilities | null>(null);
  readonly sessions = signal<Session[]>([]);
  readonly alerts = signal<Alert[]>([]);
  readonly countsError = signal('');
  readonly capabilitiesError = signal('');
  readonly sessionsError = signal('');
  readonly alertsError = signal('');
  readonly date = fmtDate;
  readonly todayLabel = signal(new Intl.DateTimeFormat('vi-VN', { weekday: 'long', day: '2-digit', month: 'long', year: 'numeric' }).format(new Date()));

  readonly cards = computed(() => {
    const values = this.counts();
    return [
      { key: 'students', label: 'Tổng sinh viên', value: values['students'] ?? '—', subtext: 'Đang hoạt động' },
      { key: 'classSections', label: 'Lớp học phần', value: values['classSections'] ?? '—', subtext: 'Đang hoạt động' },
      { key: 'sessions', label: 'Buổi học', value: values['sessions'] ?? '—', subtext: 'Tổng số buổi' },
      { key: 'cameras', label: 'Camera', value: values['cameras'] ?? '—', subtext: 'Đang hoạt động' },
      { key: 'videos', label: 'Video đã xử lý', value: values['videos'] ?? '—', subtext: 'Đã sẵn sàng' },
      { key: 'openAlerts', label: 'Cảnh báo đang mở', value: values['openAlerts'] ?? '—', subtext: 'Cần theo dõi' },
    ];
  });
  readonly runningSessions = computed(() => this.sessions().filter((item) => item.status === 'RUNNING').length);
  readonly recentSessions = computed(() => this.sessions().slice(0, 5));
  readonly recentAlerts = computed(() => this.alerts().slice(0, 4));
  readonly services = computed(() => {
    const c = this.capabilities();
    if (!c) return [];
    return [
      { name: 'Máy chủ API', ok: c.backend }, { name: 'Cơ sở dữ liệu', ok: c.database },
      { name: 'Kho lưu trữ MinIO', ok: c.objectStorage }, { name: 'Dịch vụ AI', ok: c.aiHealth },
      { name: 'Nhận diện khuôn mặt', ok: c.faceEnrollment }, { name: 'Dịch vụ mô hình ngôn ngữ', ok: c.llmProvider !== 'NOT_CONFIGURED' },
    ];
  });

  ngOnInit(): void {
    this.countsError.set('');
    this.capabilitiesError.set('');
    this.sessionsError.set('');
    this.alertsError.set('');
    forkJoin({
      counts: this.api.counts().pipe(catchError((error) => { this.countsError.set(errorText(error)); return of({} as Record<string, number>); })),
      capabilities: this.api.capabilities().pipe(catchError((error) => { this.capabilitiesError.set(errorText(error)); return of(null as Capabilities | null); })),
      sessions: this.api.sessions().pipe(catchError((error) => { this.sessionsError.set(errorText(error)); return of([] as Session[]); })),
      alerts: this.api.alerts('status=OPEN').pipe(catchError((error) => { this.alertsError.set(errorText(error)); return of([] as Alert[]); })),
    }).subscribe({
      next: (result) => {
        this.counts.set(result.counts);
        this.capabilities.set(result.capabilities);
        this.sessions.set(result.sessions);
        this.alerts.set(result.alerts);
      },
    });
  }

  sessionStatus(status: string): string { return statusText(status); }
  behaviorLabel(value: string): string { return behaviorText(value); }
}
