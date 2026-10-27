import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { DateInputComponent } from '../shared/date-input.component';
import { ModalComponent, PageTitleComponent, RowActionMenuComponent, errorText, fmtDate } from '../shared/ui';

@Component({
  standalone: true,
  imports: [DateInputComponent, FormsModule, PageTitleComponent, ModalComponent, RowActionMenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-title title="Nhật ký hệ thống" subtitle="Theo dõi các thao tác quản trị và thay đổi dữ liệu." />
    @if(error()){<div class="error-box">{{error()}}</div>}
    <div class="filter-bar">
      <label class="grow">Loại thao tác<input [(ngModel)]="action" placeholder="Nhập loại thao tác cần tìm" /></label>
      <label>Từ ngày<app-date-input label="Từ ngày" #fromDate="ngModel" [(ngModel)]="from" [max]="to" /></label>
      <label>Đến ngày<app-date-input label="Đến ngày" #toDate="ngModel" [(ngModel)]="to" [min]="from" errorMessage="Ngày kết thúc phải lớn hơn hoặc bằng ngày bắt đầu." /></label>
      <button type="button" [disabled]="fromDate.invalid || toDate.invalid" (click)="load()">Tìm kiếm</button>
      <button type="button" class="secondary" (click)="clear()">Xóa lọc</button>
    </div>
    <section class="card table-card"><div class="table-wrap"><table>
      <thead><tr><th>Thời gian</th><th>Người thực hiện</th><th>Loại thay đổi</th><th>Đối tượng</th><th>Thao tác</th></tr></thead>
      <tbody>@for(item of items();track item.id){<tr><td>{{date(item.createdAt)}}</td><td>{{actorLabel(item)}}</td><td><span class="badge blue">{{actionLabel(item.action)}}</span></td><td>{{entityLabel(item.entityType)}}</td><td class="menu-cell"><app-row-action-menu><button type="button" (click)="selected.set(item)">Xem chi tiết</button></app-row-action-menu></td></tr>}@empty{<tr><td colspan="5" class="empty">Không có nhật ký phù hợp.</td></tr>}</tbody>
    </table></div></section>
    @if(selected()){<app-modal title="Chi tiết nhật ký" size="sm" (close)="selected.set(null)">
      <div class="profile-list" style="margin-top:0;padding-top:0">
        <div><span>Thời gian</span><strong>{{date(selected()!.createdAt)}}</strong></div>
        <div><span>Người thực hiện</span><strong>{{actorLabel(selected()!)}}</strong></div>
        <div><span>Thao tác</span><strong>{{actionLabel(selected()!.action)}}</strong></div>
        <div><span>Đối tượng</span><strong>{{entityLabel(selected()!.entityType)}}</strong></div>
        <div><span>Địa chỉ truy cập</span><strong>{{selected()!.ipAddress||'Không ghi nhận'}}</strong></div>
      </div>
      <h3 style="margin-top:20px">Nội dung thay đổi</h3>
      @for(entry of metadataEntries(selected()!.metadataJson);track entry.key){<div class="service-card" style="margin-bottom:8px"><div><strong>{{entry.key}}</strong><small>{{entry.value}}</small></div></div>}@empty{<p class="empty">Không có thông tin bổ sung.</p>}
      <div class="modal-footer" style="margin:20px -20px -20px"><button type="button" class="secondary" (click)="selected.set(null)">Đóng</button></div>
    </app-modal>}
  `,
})
export class AuditComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly items = signal<any[]>([]);
  readonly error = signal('');
  readonly selected = signal<any | null>(null);
  action = '';
  from = '';
  to = '';
  readonly date = fmtDate;

  ngOnInit(): void { this.load(); }
  load(): void {
    if (this.from && this.to && this.to < this.from) { this.error.set('Ngày kết thúc phải lớn hơn hoặc bằng ngày bắt đầu.'); return; }
    const params = new URLSearchParams();
    if (this.action.trim()) params.set('action', this.action.trim());
    if (this.from) params.set('from', new Date(`${this.from}T00:00:00+07:00`).toISOString());
    if (this.to) params.set('to', new Date(`${this.to}T23:59:59+07:00`).toISOString());
    this.api.audit(params.toString()).subscribe({ next: items => this.items.set(items), error: error => this.error.set(errorText(error)) });
  }
  clear(): void { this.action = ''; this.from = ''; this.to = ''; this.load(); }
  actorLabel(item: any): string { return item?.userId ? 'Người dùng đã đăng nhập' : 'Hệ thống'; }
  actionLabel(value: string): string {
    const key = String(value || '').toUpperCase();
    const labels: Record<string, string> = { CREATE:'Thêm mới', UPDATE:'Cập nhật', DELETE:'Xóa', LOGIN:'Đăng nhập', LOGOUT:'Đăng xuất', STUDENT_CREATE:'Thêm sinh viên', STUDENT_UPDATE:'Cập nhật sinh viên', STUDENT_DELETE:'Xóa sinh viên', SESSION_START:'Bắt đầu buổi học', SESSION_STOP:'Kết thúc buổi học' };
    return labels[key] ?? (key.includes('CREATE') ? 'Thêm mới' : key.includes('UPDATE') ? 'Cập nhật' : key.includes('DELETE') ? 'Xóa dữ liệu' : key.includes('START') ? 'Bắt đầu' : key.includes('STOP') ? 'Kết thúc' : 'Thao tác quản trị');
  }
  entityLabel(value: string): string {
    const key = String(value || '').replace(/[^a-z]/gi, '').toLowerCase();
    const labels: Record<string, string> = { student:'Sinh viên', students:'Sinh viên', session:'Buổi học', sessions:'Buổi học', classsection:'Lớp học phần', user:'Người dùng', teacher:'Giảng viên', course:'Môn học', room:'Phòng học', camera:'Camera', video:'Video', alert:'Cảnh báo' };
    return labels[key] ?? 'Dữ liệu nghiệp vụ';
  }
  metadataEntries(json: string | null | undefined): Array<{key: string; value: string}> {
    if (!json) return [];
    try {
      const hidden = /id$|token|secret|password|provider|model|embedding|track|endpoint|errorcode|json/i;
      const labels: Record<string, string> = { status:'Trạng thái', oldStatus:'Trạng thái trước', newStatus:'Trạng thái mới', reason:'Lý do', note:'Ghi chú', fullName:'Họ tên', studentCode:'MSSV', userName:'Tên đăng nhập', email:'Email' };
      return Object.entries(JSON.parse(json)).filter(([key]) => !hidden.test(key)).map(([key, value]) => ({ key: labels[key] ?? 'Thông tin thay đổi', value: typeof value === 'object' ? 'Nội dung đã được cập nhật' : String(value) }));
    } catch { return []; }
  }
}
