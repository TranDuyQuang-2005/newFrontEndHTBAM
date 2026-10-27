import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { Alert } from '../core/models';
import { ModalComponent, PageTitleComponent, RowActionMenuComponent, behaviorText, errorText, fmtDate, statusText } from '../shared/ui';

@Component({
  standalone: true,
  imports: [FormsModule, PageTitleComponent, ModalComponent, RowActionMenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-title title="Cảnh báo" subtitle="Theo dõi, xác nhận và xử lý cảnh báo từ các buổi học." />
    @if (error()) { <div class="error-box">{{ error() }}</div> }
    @if (message()) { <div class="success-box">{{ message() }}</div> }

    <div class="filter-bar">
      <label>Buổi học<input type="number" [(ngModel)]="sessionId" placeholder="Mã buổi học" /></label>
      <label>Trạng thái<select [(ngModel)]="status"><option value="">Tất cả</option><option value="OPEN">Đang mở</option><option value="ACK">Đã xác nhận</option><option value="CLOSED">Đã đóng</option></select></label>
      <button type="button" (click)="load()">Tìm kiếm</button>
      <button type="button" class="secondary" (click)="clearFilters()">Xóa lọc</button>
    </div>

    <section class="card table-card">
      <div class="table-wrap"><table>
        <thead><tr><th>Mức độ</th><th>Buổi học</th><th>Loại cảnh báo</th><th>Nguồn</th><th>Liên kết</th><th>Độ tin cậy</th><th>Chất lượng quan sát</th><th>Thời gian</th><th>Trạng thái</th><th>Thao tác</th></tr></thead>
        <tbody>
          @for (alert of items(); track alert.id) {
            <tr>
              <td><span class="badge" [class.danger]="severity(alert) === 'Critical'" [class.warning]="severity(alert) === 'Warning'" [class.blue]="severity(alert) === 'Info'">{{ severityLabel(alert) }}</span></td>
              <td class="cell-title">#{{ alert.sessionId }}</td><td>{{ behaviorLabel(alert.type) }}</td><td>{{ alert.source === 'RULE_ENGINE' ? 'Luật từ hành vi' : 'AI trực tiếp' }}</td><td>{{ alert.studentId ? 'SV #' + alert.studentId : alert.stableIdentityId ? 'Identity #' + alert.stableIdentityId : '—' }}<small>{{ alert.trackId ? 'Track ' + alert.trackId : alert.behaviorEventId ? 'Event ' + alert.behaviorEventId : '' }}</small></td><td>{{ percent(alert.confidence) }}</td><td>{{ percent(alert.observationQuality) }}</td><td>{{ date(alert.startedAt) }}</td>
              <td><span class="badge" [class.gray]="alert.status === 'CLOSED'" [class.blue]="alert.status === 'ACK'">{{ statusLabel(alert.status) }}</span></td>
              <td class="menu-cell"><app-row-action-menu>
                @if (alert.status === 'OPEN') { <button type="button" class="small" (click)="openAction(alert, 'ack')">Xác nhận</button> }
                @if (alert.status !== 'CLOSED') { <button type="button" class="danger" (click)="openAction(alert, 'close')">Đóng</button> }
                @else { <button type="button" class="secondary small" (click)="openAction(alert, 'reopen')">Mở lại</button> }
              </app-row-action-menu></td>
            </tr>
          } @empty { <tr><td colspan="10" class="empty">0 cảnh báo — không có rule/model nào phát hiện cảnh báo phù hợp.</td></tr> }
        </tbody>
      </table></div>
    </section>

    @if (selectedAlert()) {
      <app-modal [title]="actionTitle()" size="sm" [busy]="saving()" (close)="closeAction()">
        <div class="stack">
          <p style="margin-top:0">Cảnh báo <b>{{ behaviorLabel(selectedAlert()!.type) }}</b> của buổi học #{{ selectedAlert()!.sessionId }}.</p>
          <label>Ghi chú<textarea [(ngModel)]="note" rows="4" placeholder="Ghi chú xử lý (không bắt buộc)"></textarea></label>
          <div class="modal-footer" style="margin:0 -20px -20px"><button type="button" class="secondary" [disabled]="saving()" (click)="closeAction()">Hủy</button><button type="button" [disabled]="saving()" (click)="confirmAction()">{{ saving() ? 'Đang lưu…' : 'Xác nhận' }}</button></div>
        </div>
      </app-modal>
    }
  `,
})
export class AlertsComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly items = signal<Alert[]>([]);
  readonly error = signal(''); readonly message = signal(''); readonly saving = signal(false);
  readonly selectedAlert = signal<Alert | null>(null);
  readonly selectedAction = signal<'ack' | 'close' | 'reopen'>('ack');
  sessionId: number | null = null; status = ''; note = ''; readonly date = fmtDate;
  ngOnInit(): void { this.load(); }
  load(): void { const params = new URLSearchParams(); if (this.sessionId) params.set('sessionId', String(this.sessionId)); if (this.status) params.set('status', this.status); this.api.alerts(params.toString()).subscribe({ next: (items) => this.items.set(items), error: (error) => this.error.set(errorText(error)) }); }
  clearFilters(): void { this.sessionId = null; this.status = ''; this.load(); }
  openAction(alert: Alert, action: 'ack' | 'close' | 'reopen'): void { this.selectedAlert.set(alert); this.selectedAction.set(action); this.note = alert.lecturerNote ?? ''; }
  closeAction(): void { if (!this.saving()) this.selectedAlert.set(null); }
  confirmAction(): void { const alert = this.selectedAlert(); if (!alert) return; this.saving.set(true); this.api.alertAction(alert.id, this.selectedAction(), this.note.trim()).subscribe({ next: () => { this.saving.set(false); this.selectedAlert.set(null); this.message.set('Đã cập nhật cảnh báo.'); this.load(); }, error: (error) => { this.error.set(errorText(error)); this.saving.set(false); } }); }
  percent(value: number): string { return `${Math.round(value * 100)}%`; }
  severity(alert: Alert): 'Critical' | 'Warning' | 'Info' { if (alert.severity === 'CRITICAL') return 'Critical'; if (alert.severity === 'WARNING') return 'Warning'; if (alert.severity === 'INFO') return 'Info'; if (alert.type.toUpperCase().includes('SLEEP') || alert.confidence >= .9) return 'Critical'; if (alert.confidence >= .65) return 'Warning'; return 'Info'; }
  severityLabel(alert: Alert): string { return ({ Critical: 'Nghiêm trọng', Warning: 'Cảnh báo', Info: 'Thông tin' } as Record<string,string>)[this.severity(alert)]; }
  statusLabel(status: string): string { return statusText(status); }
  behaviorLabel(value: string): string { return behaviorText(value); }
  actionTitle(): string { return ({ ack: 'Xác nhận cảnh báo', close: 'Đóng cảnh báo', reopen: 'Mở lại cảnh báo' } as Record<string,string>)[this.selectedAction()]; }
}
