import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { catchError, forkJoin, of } from 'rxjs';
import { ApiService } from '../core/api.service';
import { Capabilities } from '../core/models';
import { PageTitleComponent, errorText } from '../shared/ui';

@Component({
  standalone: true,
  imports: [PageTitleComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-title title="Trạng thái hệ thống" subtitle="Theo dõi khả năng hoạt động của Hệ thống giám sát.">
      <button type="button" class="secondary" (click)="load()">Cập nhật</button>
    </app-page-title>
    @if (error()) { <div class="error-box">{{ error() }}</div> }
    @if (capabilitiesError()) { <div class="error-box">Không thể kiểm tra chức năng hệ thống: {{ capabilitiesError() }}</div> }
    @if (countsError()) { <div class="error-box">Không thể tải số liệu tổng quan: {{ countsError() }}</div> }
    <div class="service-grid">
      @for (service of services(); track service.name) {
        <section class="card" style="margin:0">
          <div class="service-card" style="padding:0;border:0">
            <span class="status-dot" [style.background]="service.ok ? '#16a34a' : '#dc2626'"></span>
            <div><strong>{{ service.name }}</strong><small>{{ service.description }}</small></div>
          </div>
          <div style="margin-top:14px"><span class="badge" [class.danger]="!service.ok">{{ service.ok ? 'Hoạt động' : 'Ngoại tuyến' }}</span></div>
        </section>
      } @empty {
        <section class="card"><div class="empty-state"><div><strong>Đang kiểm tra dịch vụ</strong><span>Vui lòng chờ phản hồi từ hệ thống.</span></div></div></section>
      }
    </div>
    <section class="card" style="margin-top:18px">
      <div class="card-header" style="margin:-20px -20px 18px"><div><h3>Khả năng xử lý</h3><p>Các chức năng đang sẵn sàng</p></div></div>
      <div class="kv">
        <span>Phân tích buổi học</span><b>{{ caps()?.sessionInference ? 'Sẵn sàng' : 'Chưa sẵn sàng' }}</b>
        <span>Đăng ký khuôn mặt</span><b>{{ caps()?.faceEnrollment ? 'Sẵn sàng' : 'Chưa sẵn sàng' }}</b>
        <span>Xuất video có chú thích</span><b>{{ caps()?.annotatedVideoOutput ? 'Sẵn sàng' : 'Chưa sẵn sàng' }}</b>
        <span>Tạo nhận xét tự động</span><b>{{ caps()?.llmProvider && caps()?.llmProvider !== 'NOT_CONFIGURED' ? 'Sẵn sàng' : 'Chưa cấu hình' }}</b>
        <span>Mô hình phân tích</span><b>{{ caps()?.loadedModels?.length ? caps()!.loadedModels.length + ' mô hình sẵn sàng' : 'Chưa sẵn sàng' }}</b>
      </div>
    </section>
  `,
})
export class SystemComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly caps = signal<Capabilities | null>(null);
  readonly counts = signal<any>(null);
  readonly error = signal('');
  readonly capabilitiesError = signal('');
  readonly countsError = signal('');

  ngOnInit(): void { this.load(); }

  load(): void {
    this.error.set('');
    this.capabilitiesError.set('');
    this.countsError.set('');
    forkJoin({
      caps: this.api.capabilities().pipe(catchError((error) => { this.capabilitiesError.set(errorText(error)); return of(null as Capabilities | null); })),
      counts: this.api.counts().pipe(catchError((error) => { this.countsError.set(errorText(error)); return of(null); })),
    }).subscribe({
      next: (result) => { this.caps.set(result.caps); this.counts.set(result.counts); },
      error: (error) => this.error.set(errorText(error)),
    });
  }

  services(): Array<{name: string; description: string; ok: boolean}> {
    const c = this.caps();
    if (!c) return [];
    return [
      { name: 'Máy chủ ứng dụng', description: 'Xử lý yêu cầu của người dùng', ok: c.backend },
      { name: 'Cơ sở dữ liệu', description: 'Lưu trữ dữ liệu nghiệp vụ', ok: c.database },
      { name: 'Kho tệp', description: 'Lưu trữ video và hình ảnh', ok: c.objectStorage },
      { name: 'Phân tích lớp học', description: 'Phân tích hình ảnh và hành vi', ok: c.aiHealth },
      { name: 'Nhận diện khuôn mặt', description: 'Đăng ký và nhận diện khuôn mặt', ok: c.faceEnrollment },
      { name: 'Nhận xét tự động', description: c.llmProvider !== 'NOT_CONFIGURED' ? 'Đã sẵn sàng' : 'Chưa cấu hình', ok: c.llmProvider !== 'NOT_CONFIGURED' },
    ];
  }
}
