import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { Alert, Session } from '../core/models';
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
      <label>Buổi học<select [(ngModel)]="sessionId"><option [ngValue]="null">Tất cả</option>@for(session of sessions();track session.id){<option [ngValue]="session.id">{{ sessionLabel(session) }}</option>}</select></label>
      <label>Trạng thái<select [(ngModel)]="status"><option value="">Tất cả</option><option value="OPEN">Đang mở</option><option value="ACK">Đã xác nhận</option><option value="CLOSED">Đã đóng</option></select></label>
      <button type="button" (click)="load()">Tìm kiếm</button>
      <button type="button" class="secondary" (click)="clearFilters()">Xóa lọc</button>
    </div>

    <section class="card table-card">
      <div class="table-wrap"><table>
        <thead><tr><th>Mức độ</th><th>Buổi học</th><th>Loại cảnh báo</th><th>Nguồn phát hiện</th><th>Đối tượng</th><th>Thời gian</th><th>Trạng thái</th><th>Thao tác</th></tr></thead>
        <tbody>
          @for (alert of items(); track alert.id) {
            <tr>
              <td><span class="badge" [class.danger]="severity(alert) === 'Critical'" [class.warning]="severity(alert) === 'Warning'" [class.blue]="severity(alert) === 'Info'">{{ severityLabel(alert) }}</span></td>
              <td class="cell-title">{{ sessionName(alert.sessionId) }}</td><td>{{ behaviorLabel(alert.type) }}</td><td>{{ alert.source === 'RULE_ENGINE' ? 'Quy tắc giám sát' : 'Phân tích tự động' }}</td><td>{{ alert.studentId ? 'Sinh viên đã xác định' : alert.stableIdentityId ? 'Đối tượng đang theo dõi' : 'Chưa xác định' }}</td><td>{{ date(alert.startedAt) }}</td>
              <td><span class="badge" [class.gray]="alert.status === 'CLOSED'" [class.blue]="alert.status === 'ACK'">{{ statusLabel(alert.status) }}</span></td>
              <td class="menu-cell"><app-row-action-menu>
                @if (alert.status === 'OPEN') { <button type="button" class="small" (click)="openAction(alert, 'ack')">Xác nhận</button> }
                @if (alert.status !== 'CLOSED') { <button type="button" class="danger" (click)="openAction(alert, 'close')">Đóng</button> }
                @else { <button type="button" class="secondary small" (click)="openAction(alert, 'reopen')">Mở lại</button> }
              </app-row-action-menu></td>
            </tr>
          } @empty { <tr><td colspan="8" class="empty">Không có cảnh báo phù hợp với bộ lọc.</td></tr> }
        </tbody>
      </table></div>
    </section>

    @if (selectedAlert()) {
      <app-modal [title]="actionTitle()" size="sm" [busy]="saving()" (close)="closeAction()">
        <div class="stack">
          <p style="margin-top:0">Cảnh báo <b>{{ behaviorLabel(selectedAlert()!.type) }}</b> trong {{ sessionName(selectedAlert()!.sessionId) }}.</p>
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
  readonly sessions = signal<Session[]>([]);
  readonly error = signal(''); readonly message = signal(''); readonly saving = signal(false);
  readonly selectedAlert = signal<Alert | null>(null);
  readonly selectedAction = signal<'ack' | 'close' | 'reopen'>('ack');
  sessionId: number | null = null; status = ''; note = ''; readonly date = fmtDate;
  ngOnInit(): void { this.api.sessions().subscribe({next:items=>this.sessions.set(items),error:()=>undefined}); this.load(); }
  load(): void { const params = new URLSearchParams(); if (this.sessionId) params.set('sessionId', String(this.sessionId)); if (this.status) params.set('status', this.status); this.api.alerts(params.toString()).subscribe({ next: (items) => this.items.set(items), error: (error) => this.error.set(errorText(error)) }); }
  clearFilters(): void { this.sessionId = null; this.status = ''; this.load(); }
  openAction(alert: Alert, action: 'ack' | 'close' | 'reopen'): void { this.selectedAlert.set(alert); this.selectedAction.set(action); this.note = alert.lecturerNote ?? ''; }
  closeAction(): void { if (!this.saving()) this.selectedAlert.set(null); }
  confirmAction(): void { const alert = this.selectedAlert(); if (!alert) return; this.saving.set(true); this.api.alertAction(alert.id, this.selectedAction(), this.note.trim()).subscribe({ next: () => { this.saving.set(false); this.selectedAlert.set(null); this.message.set('Đã cập nhật cảnh báo.'); this.load(); }, error: (error) => { this.error.set(errorText(error)); this.saving.set(false); } }); }
  sessionLabel(session: Session): string { return `${session.classSection || session.course || 'Buổi học'} · ${fmtDate(session.scheduledStart)}`; }
  sessionName(id: number): string { const item=this.sessions().find(session=>session.id===id); return item ? this.sessionLabel(item) : 'buổi học đã chọn'; }
  severity(alert: Alert): 'Critical' | 'Warning' | 'Info' { if (alert.severity === 'CRITICAL') return 'Critical'; if (alert.severity === 'WARNING') return 'Warning'; if (alert.severity === 'INFO') return 'Info'; if (alert.type.toUpperCase().includes('SLEEP') || alert.confidence >= .9) return 'Critical'; if (alert.confidence >= .65) return 'Warning'; return 'Info'; }
  severityLabel(alert: Alert): string { return ({ Critical: 'Nghiêm trọng', Warning: 'Cảnh báo', Info: 'Thông tin' } as Record<string,string>)[this.severity(alert)]; }
  statusLabel(status: string): string { return statusText(status); }
  behaviorLabel(value: string): string { return behaviorText(value); }
  actionTitle(): string { return ({ ack: 'Xác nhận cảnh báo', close: 'Đóng cảnh báo', reopen: 'Mở lại cảnh báo' } as Record<string,string>)[this.selectedAction()]; }
}
