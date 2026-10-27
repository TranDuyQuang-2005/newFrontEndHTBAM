import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { catchError, forkJoin, Observable, of } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { AttendancePolicy, Camera, ClassSection, Room, Session, Teacher, Video } from '../core/models';
import { ModalComponent, PageTitleComponent, RowActionMenuComponent, errorText, fmtDate, statusText } from '../shared/ui';

@Component({
  standalone: true,
  imports: [FormsModule, RouterLink, ModalComponent, PageTitleComponent, RowActionMenuComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-title title="Buổi học" subtitle="Quản lý buổi thủ công và buổi được sinh từ lịch lớp học phần.">
      @if (auth.hasPermission('SESSION_CREATE_SCOPE')) { <button type="button" (click)="startAdd()">+ Thêm</button> }
      <a class="button-link secondary" routerLink="/class-sections">Lịch lớp học phần</a>
    </app-page-title>

    @if (error()) { <div class="error-box">{{ error() }}</div> }
    @for (loadError of loadErrorList(); track loadError.key) { <div class="error-box">{{ loadError.label }}: {{ loadError.message }}</div> }
    @if (message()) { <div class="success-box">{{ message() }}</div> }
    @if (loading()) { <div class="notice">Đang tải danh sách buổi học…</div> }

    <div class="filter-bar">
      <label class="grow">Tìm buổi học<input [(ngModel)]="search" placeholder="Tên lớp học phần" /></label>
      <label>Trạng thái
        <select [(ngModel)]="statusFilter" (ngModelChange)="page=1;reloadSessions()">
          <option value="">Tất cả</option>
          <option value="RUNNING">Đang diễn ra</option>
          <option value="COMPLETED">Đã kết thúc</option>
          <option value="READY">Sẵn sàng</option>
          <option value="DRAFT">Bản nháp</option>
          <option value="CANCELLED">Đã hủy</option>
        </select>
      </label>
      <button type="button" class="secondary" (click)="search='';statusFilter='';page=1;reloadSessions()">Xóa lọc</button>
    </div>

    <section class="card table-card">
      <div class="table-wrap">
        <table>
          <thead><tr><th>Nguồn tạo</th><th>Lớp học phần</th><th>Giảng viên</th><th>Nguồn dữ liệu</th><th>Thời gian</th><th>Trạng thái</th><th>Thao tác</th></tr></thead>
          <tbody>
            @for (session of filteredItems(); track session.id) {
              <tr>
                <td class="cell-title">{{ session.classScheduleId ? 'Theo lịch lớp' : 'Tạo thủ công' }}</td>
                <td>{{ sectionLabel(session.classSectionId) }}</td>
                <td>
                  <div>Giảng viên phụ trách: {{ session.originalTeacherName || '-' }}</div>
                  <small>Người dạy thực tế: {{ session.substitution?.substituteTeacherName || session.originalTeacherName || '-' }}</small>
                  @if (session.isSubstituteTeaching) { <small><span class="badge blue">Buổi dạy thay</span></small> }
                  @else if (session.activeSubstitute) { <small><span class="badge purple">Đã giao cho {{ session.activeSubstitute.substituteTeacherName }}</span></small> }
                  @else if (session.substitution?.status === 'COMPLETED') { <small><span class="badge gray">Đã dạy thay bởi {{ session.substitution?.substituteTeacherName }}</span></small> }
                </td>
                <td><span class="badge" [class.purple]="session.videoId" [class.warning]="!session.cameraId && !session.videoId">{{ sourceLabel(session) }}</span></td>
                <td>{{ date(session.scheduledStart) }}<small>@if (session.scheduledEnd) { đến {{ date(session.scheduledEnd) }} }</small></td>
                <td><span class="badge" [class.gray]="session.status === 'COMPLETED' || session.status === 'CANCELLED'" [class.blue]="session.status === 'READY' || session.status === 'DRAFT'">{{ statusLabel(session.status) }}</span></td>
                <td class="menu-cell">
                  <app-row-action-menu>
                      <a [routerLink]="['/sessions', session.id]">Xem chi tiết</a>
                      @if (session.canOperate && (session.status === 'READY' || session.status === 'DRAFT')) { <button type="button" (click)="editSource(session)">Chọn nguồn phân tích</button> }
                      @if (!session.classScheduleId && session.canManage && (session.status === 'READY' || session.status === 'DRAFT')) { <button type="button" (click)="startEdit(session)">Sửa buổi thủ công</button> }
                      @if (auth.canAssignSubstitute() && !session.activeSubstitute && (session.status === 'READY' || session.status === 'DRAFT')) { <button type="button" (click)="assignSubstitute(session)">Phân công dạy thay</button> }
                      @if (auth.canAssignSubstitute() && session.activeSubstitute && (session.status === 'READY' || session.status === 'DRAFT')) { <button type="button" class="danger" (click)="askCancelSubstitute(session)">Hủy phân công dạy thay</button> }
                      @if (session.canManage && (session.status === 'READY' || session.status === 'DRAFT')) { <button type="button" class="danger" (click)="askCancelSession(session)">Hủy buổi học</button> }
                  </app-row-action-menu>
                </td>
              </tr>
            } @empty {
              <tr><td colspan="7" class="empty">Không có buổi học phù hợp.</td></tr>
            }
          </tbody>
        </table>
      </div>
      <div class="pagination"><span>{{ total() }} buổi · Trang {{ page }}</span><div class="actions"><button type="button" class="secondary small" [disabled]="page <= 1 || loading()" (click)="page=page-1;reloadSessions()">Trước</button><button type="button" class="secondary small" [disabled]="page*pageSize >= total() || loading()" (click)="page=page+1;reloadSessions()">Sau</button></div></div>
    </section>
    @if (adding()) {
      <app-modal [title]="editingSessionId() ? 'Chỉnh sửa buổi học' : 'Thêm buổi học'" [busy]="saving()" size="lg" (close)="cancelAdd()">
        <form class="form-grid" (ngSubmit)="createSession()">
          @if (formError()) { <div class="error-box span2">{{ formError() }}</div> }
          <label>Lớp học phần <span class="required">*</span><select name="classSectionId" [(ngModel)]="form.classSectionId"><option [ngValue]="null">-- Chọn lớp --</option>@for (section of sections(); track section.id) { <option [ngValue]="section.id">{{ section.code }} - {{ section.name }}</option> }</select></label>
          <label>Chính sách chuyên cần <span class="required">*</span><select name="attendancePolicyId" [(ngModel)]="form.attendancePolicyId"><option [ngValue]="null">-- Chọn chính sách --</option>@for (policy of policies(); track policy.id) { <option [ngValue]="policy.id">{{ policy.code }} - {{ policy.name }}</option> }</select></label>
          <label>Nguồn dữ liệu <span class="required">*</span><select name="source" [(ngModel)]="form.source"><option value="camera">Camera</option><option value="video">Video</option></select></label>
          @if (form.source === 'camera') { <label>Camera <span class="required">*</span><select name="cameraId" [(ngModel)]="form.cameraId"><option [ngValue]="null">-- Chọn camera --</option>@for (camera of cameras(); track camera.id) { <option [ngValue]="camera.id">{{ camera.code }} - {{ camera.name }}{{ camera.sourceMode === 'BROWSER' ? ' · Camera laptop' : '' }}</option> }</select></label> }
          @else { <label>Video <span class="required">*</span><select name="videoId" [(ngModel)]="form.videoId"><option [ngValue]="null">-- Chọn video --</option>@for (video of readyVideos(); track video.id) { <option [ngValue]="video.id">{{ video.fileName }}</option> }</select></label> }
          <label>Ngày học <span class="required">*</span><input name="date" type="date" [(ngModel)]="form.date" /></label>
          <label>Giờ bắt đầu <span class="required">*</span><input name="startTime" type="time" [(ngModel)]="form.startTime" /></label>
          <label>Giờ kết thúc<input name="endTime" type="time" [(ngModel)]="form.endTime" /></label>
          <div class="modal-footer span2" style="margin:0 -20px -20px"><button type="button" class="secondary" [disabled]="saving()" (click)="cancelAdd()">Hủy</button><button type="submit" [disabled]="saving()">{{ saving() ? 'Đang lưu…' : 'Lưu' }}</button></div>
        </form>
      </app-modal>
    }

    @if (sourceSession()) {
      <app-modal title="Nguồn phân tích buổi học" [busy]="sourceSaving()" size="md" (close)="closeSource()">
        <p>{{ sectionLabel(sourceSession()!.classSectionId) }} · {{ date(sourceSession()!.scheduledStart) }}</p>
        @if (sourceSession()!.classScheduleId) { <p class="notice">Ngày, tiết, giờ và phòng được quản lý tại lịch lớp học phần. Việc chọn nguồn không tự bắt đầu phân tích.</p> }
        @if (sourceError()) { <div class="error-box">{{ sourceError() }}</div> }
        <form class="form-grid" (ngSubmit)="saveSource()">
          <label>Nguồn dữ liệu <span class="required">*</span><select name="sourceType" [(ngModel)]="sourceForm.type"><option value="camera">Camera</option><option value="video">Video</option></select></label>
          @if (sourceForm.type === 'camera') {
            <label>Camera <span class="required">*</span><select name="sourceCamera" [(ngModel)]="sourceForm.cameraId"><option [ngValue]="null">-- Chọn camera --</option>@for (camera of camerasForSession(sourceSession()!); track camera.id) { <option [ngValue]="camera.id">{{ camera.code }} - {{ camera.name }}{{ camera.sourceMode === 'BROWSER' ? ' · Camera trên thiết bị' : ' · Camera phòng học' }}</option> }</select></label>
          } @else {
            <label>Video <span class="required">*</span><select name="sourceVideo" [(ngModel)]="sourceForm.videoId"><option [ngValue]="null">-- Chọn video --</option>@for (video of readyVideos(); track video.id) { <option [ngValue]="video.id">{{ video.fileName }}</option> }</select></label>
          }
          <div class="modal-footer span2" style="margin:0 -20px -20px"><button type="button" class="secondary" [disabled]="sourceSaving()" (click)="closeSource()">Hủy</button><button type="submit" [disabled]="sourceSaving()">{{ sourceSaving() ? 'Đang lưu…' : 'Lưu nguồn' }}</button></div>
        </form>
      </app-modal>
    }

    @if (substitutionSession()) {
      <app-modal title="Phân công dạy thay" [busy]="substitutionSaving()" size="md" (close)="closeSubstitution()">
        <p>{{ sectionLabel(substitutionSession()!.classSectionId) }} · {{ date(substitutionSession()!.scheduledStart) }}</p>
        @if (substitutionError()) { <div class="error-box">{{ substitutionError() }}</div> }
        <div class="form-grid"><label>Giảng viên dạy thay <span class="required">*</span><select [(ngModel)]="substitutionForm.teacherId"><option [ngValue]="null">-- Chọn giảng viên --</option>@for (teacher of teachers(); track teacher.id) { <option [ngValue]="teacher.id">{{ teacher.teacherCode }} - {{ teacher.fullName }}</option> }</select></label><label>Lý do <span class="required">*</span><input [(ngModel)]="substitutionForm.reason" /></label><label class="span2">Ghi chú<textarea [(ngModel)]="substitutionForm.note" rows="3"></textarea></label></div>
        <div class="modal-footer" style="margin:20px -20px -20px"><button type="button" class="secondary" [disabled]="substitutionSaving()" (click)="closeSubstitution()">Hủy</button><button type="button" [disabled]="substitutionSaving()" (click)="saveSubstitution()">{{ substitutionSaving() ? 'Đang lưu…' : 'Lưu phân công' }}</button></div>
      </app-modal>
    }

    @if (pendingCancellation()) {
      <app-modal [title]="pendingCancellation()?.type === 'session' ? 'Xác nhận hủy buổi học' : 'Xác nhận hủy phân công'" [busy]="cancellationSaving()" size="sm" (close)="closeCancellation()">
        @if (cancellationError()) { <div class="error-box">{{ cancellationError() }}</div> }
        <p>{{ pendingCancellation()?.type === 'session' ? 'Bạn có chắc muốn hủy buổi học này' : 'Bạn có chắc muốn hủy phân công dạy thay của buổi học này' }} không?</p>
        <div class="modal-footer" style="margin:20px -20px -20px"><button type="button" class="secondary" [disabled]="cancellationSaving()" (click)="closeCancellation()">Không</button><button type="button" class="danger" [disabled]="cancellationSaving()" (click)="confirmCancellation()">{{ cancellationSaving() ? 'Đang xử lý…' : 'Xác nhận hủy' }}</button></div>
      </app-modal>
    }
  `,
})
export class SessionsComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly auth = inject(AuthService);
  readonly items = signal<Session[]>([]);
  readonly total = signal(0);
  readonly sections = signal<ClassSection[]>([]);
  readonly rooms = signal<Room[]>([]);
  readonly cameras = signal<Camera[]>([]);
  readonly videos = signal<Video[]>([]);
  readonly policies = signal<AttendancePolicy[]>([]);
  readonly teachers = signal<Teacher[]>([]);
  readonly error = signal('');
  readonly loadErrors = signal<Record<string, string>>({});
  readonly message = signal('');
  readonly formError = signal('');
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly adding = signal(false);
  readonly editingSessionId = signal<number | null>(null);
  readonly sourceSession = signal<Session | null>(null);
  readonly sourceSaving = signal(false);
  readonly sourceError = signal('');
  readonly substitutionSession = signal<Session | null>(null);
  readonly substitutionSaving = signal(false);
  readonly substitutionError = signal('');
  readonly pendingCancellation = signal<{session: Session; type: 'session'|'substitution'} | null>(null);
  readonly cancellationSaving = signal(false);
  readonly cancellationError = signal('');
  readonly date = fmtDate;
  search = '';
  statusFilter = '';
  page = 1;
  readonly pageSize = 20;
  form = this.emptyForm();
  sourceForm = { type: 'camera' as 'camera'|'video', cameraId: null as number|null, videoId: null as number|null };
  substitutionForm = { teacherId: null as number | null, reason: '', note: '' };

  ngOnInit(): void {
    this.loading.set(true);
    this.loadErrors.set({});
    forkJoin({
      sessions: this.withFallback('sessions', {items: [] as Session[], total: 0, page: 1, pageSize: this.pageSize}, this.api.sessionsPage({page:this.page,pageSize:this.pageSize})), sections: this.withFallback('sections', [], this.api.classSections()), rooms: this.withFallback('rooms', [], this.api.rooms()),
      cameras: this.withFallback('cameras', [], this.api.cameras()), videos: this.withFallback('videos', [], this.api.videos()), policies: this.withFallback('policies', [], this.api.policies()), teachers: this.withFallback('teachers', [], this.api.teachers()),
    }).subscribe({
      next: (result) => {
        this.items.set(result.sessions.items); this.total.set(result.sessions.total); this.sections.set(result.sections); this.rooms.set(result.rooms);
        this.cameras.set(result.cameras); this.videos.set(result.videos); this.policies.set(result.policies); this.teachers.set(result.teachers);
        this.loading.set(false);
      },
      error: (error) => { this.error.set(errorText(error)); this.loading.set(false); },
    });
  }

  filteredItems(): Session[] {
    const query = this.search.trim().toLowerCase();
    return this.items().filter((item) => (!query || this.sectionLabel(item.classSectionId).toLowerCase().includes(query)) && (!this.statusFilter || item.status === this.statusFilter));
  }

  sourceLabel(session: Session): string {
    if (session.videoId) return 'Video';
    if (!session.cameraId) return 'Chưa chọn nguồn';
    return this.cameras().find((camera) => camera.id === session.cameraId)?.sourceMode === 'BROWSER' ? 'Camera trên thiết bị' : 'Camera phòng học';
  }
  camerasForSession(session: Session): Camera[] { return this.cameras().filter((camera) => !session.roomId || camera.roomId === session.roomId); }
  readyVideos(): Video[] { return this.videos().filter((item) => item.status === 'READY' && item.videoType === 'INPUT_UPLOAD'); }
  loadErrorList(): Array<{key: string; label: string; message: string}> { const labels: Record<string, string> = { sessions: 'Buổi học', sections: 'Lớp học phần', rooms: 'Phòng học', cameras: 'Camera', videos: 'Video', policies: 'Chính sách điểm danh', teachers: 'Giảng viên' }; return Object.entries(this.loadErrors()).map(([key, message]) => ({ key, label: labels[key] ?? key, message })); }
  sectionLabel(id: number): string { const item = this.sections().find((section) => section.id === id); return item ? `${item.code} - ${item.name}` : `#${id}`; }
  statusLabel(status: string): string { return statusText(status); }

  startAdd(): void { this.editingSessionId.set(null); this.form = this.emptyForm(); this.formError.set(''); this.adding.set(true); }
  startEdit(session: Session): void { const start=new Date(session.scheduledStart);const end=session.scheduledEnd?new Date(session.scheduledEnd):null;this.editingSessionId.set(session.id);this.form={...this.emptyForm(),classSectionId:session.classSectionId,cameraId:session.cameraId??null,videoId:session.videoId??null,attendancePolicyId:session.attendancePolicyId,date:start.toISOString().slice(0,10),startTime:start.toTimeString().slice(0,5),endTime:end?.toTimeString().slice(0,5)??'',source:session.videoId?'video':'camera'};this.adding.set(true); }
  cancelAdd(): void { if (!this.saving()) { this.adding.set(false); this.editingSessionId.set(null); } }
  createSession(): void {
    if (!this.form.classSectionId || !this.form.attendancePolicyId || !this.form.date || !this.form.startTime || (this.form.source === 'camera' ? !this.form.cameraId : !this.form.videoId)) {
      this.formError.set('Vui lòng nhập đủ các trường bắt buộc.');
      return;
    }
    const start = new Date(`${this.form.date}T${this.form.startTime}`);
    const end = this.form.endTime ? new Date(`${this.form.date}T${this.form.endTime}`) : null;
    if (end && end <= start) { this.formError.set('Giờ kết thúc phải sau giờ bắt đầu.'); return; }
    this.saving.set(true); this.formError.set('');
    const request = {
      classSectionId: this.form.classSectionId,
      roomId: this.form.roomId,
      cameraId: this.form.source === 'camera' ? this.form.cameraId : null,
      videoId: this.form.source === 'video' ? this.form.videoId : null,
      attendancePolicyId: this.form.attendancePolicyId,
      scheduledStart: start.toISOString(),
      scheduledEnd: end?.toISOString() ?? null,
      studentIds: [],
      alertProfile: 'DEFAULT',
    };
    const operation = this.editingSessionId() ? this.api.updateSession(this.editingSessionId()!, request) : this.api.createSession(request);
    operation.subscribe({
      next: () => { this.saving.set(false); this.adding.set(false); this.editingSessionId.set(null); this.message.set('Đã lưu buổi học.'); this.reloadSessions(); },
      error: (error) => { this.formError.set(errorText(error)); this.saving.set(false); },
    });
  }

  editSource(session: Session): void {
    this.sourceForm = { type: session.videoId ? 'video' : 'camera', cameraId: session.cameraId ?? null, videoId: session.videoId ?? null };
    this.sourceError.set('');
    this.sourceSession.set(session);
  }
  closeSource(): void { if (!this.sourceSaving()) this.sourceSession.set(null); }
  saveSource(): void {
    const session = this.sourceSession();
    if (!session || (this.sourceForm.type === 'camera' ? !this.sourceForm.cameraId : !this.sourceForm.videoId)) {
      this.sourceError.set('Vui lòng chọn một nguồn hợp lệ.');
      return;
    }
    this.sourceSaving.set(true); this.sourceError.set('');
    this.api.sessionSource(session.id, { cameraId: this.sourceForm.type === 'camera' ? this.sourceForm.cameraId : null, videoId: this.sourceForm.type === 'video' ? this.sourceForm.videoId : null }).subscribe({
      next: () => { this.sourceSaving.set(false); this.sourceSession.set(null); this.message.set('Đã lưu nguồn phân tích. Buổi học chưa được tự động bắt đầu.'); this.reloadSessions(); },
      error: (error) => { this.sourceSaving.set(false); this.sourceError.set(errorText(error)); },
    });
  }

  assignSubstitute(session: Session): void {
    this.substitutionSession.set(session); this.substitutionForm={teacherId:null,reason:'',note:''};this.substitutionError.set('');
  }
  closeSubstitution(): void { if(!this.substitutionSaving())this.substitutionSession.set(null); }
  saveSubstitution(): void { const session=this.substitutionSession();if(!session||!this.substitutionForm.teacherId||!this.substitutionForm.reason.trim()){this.substitutionError.set('Vui lòng chọn giảng viên và nhập lý do.');return}this.substitutionSaving.set(true);this.api.assignSubstitution(session.id,{substituteTeacherId:this.substitutionForm.teacherId,reason:this.substitutionForm.reason.trim(),note:this.substitutionForm.note.trim()||undefined}).subscribe({next:()=>{this.substitutionSaving.set(false);this.substitutionSession.set(null);this.message.set('Đã phân công dạy thay.');this.reloadSessions()},error:error=>{this.substitutionSaving.set(false);this.substitutionError.set(errorText(error))}})}

  askCancelSubstitute(session: Session): void { this.cancellationError.set(''); this.pendingCancellation.set({session,type:'substitution'}); }
  askCancelSession(session: Session): void { this.cancellationError.set(''); this.pendingCancellation.set({session,type:'session'}); }
  closeCancellation(): void { if(!this.cancellationSaving())this.pendingCancellation.set(null); }
  confirmCancellation(): void {
    const pending=this.pendingCancellation();if(!pending)return;
    this.cancellationSaving.set(true);this.cancellationError.set('');
    const operation=pending.type==='session'?this.api.sessionAction(pending.session.id,'cancel'):this.api.cancelSubstitution(pending.session.id);
    operation.subscribe({next:()=>{this.cancellationSaving.set(false);this.pendingCancellation.set(null);this.message.set(pending.type==='session'?'Đã hủy buổi học.':'Đã hủy phân công dạy thay.');this.reloadSessions()},error:error=>{this.cancellationSaving.set(false);this.cancellationError.set(errorText(error))}});
  }

  reloadSessions(): void {
    this.loading.set(true);
    this.api.sessionsPage({page:this.page,pageSize:this.pageSize,...(this.statusFilter?{status:this.statusFilter}:{})}).subscribe({ next: (result) => { this.items.set(result.items); this.total.set(result.total); this.loading.set(false); }, error: (error) => { this.error.set(errorText(error)); this.loading.set(false); } });
  }

  private emptyForm() {
    return { classSectionId: null as number | null, roomId: null as number | null, attendancePolicyId: null as number | null, date: '', startTime: '', endTime: '', source: 'camera' as 'camera' | 'video', cameraId: null as number | null, videoId: null as number | null };
  }

  private withFallback<T>(key: string, fallback: T, source: Observable<T>): Observable<T> {
    return source.pipe(catchError((error) => { this.loadErrors.update((errors) => ({ ...errors, [key]: errorText(error) })); return of(fallback); }));
  }
}
